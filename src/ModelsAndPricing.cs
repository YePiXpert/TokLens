using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TokLens
{
    public static class AppConstants
    {
        public const string Version = "2.3.0";
        public const string VersionTag = "v2.3.0";
        public const string ProductTitle = "知微 · TokLens v2.3.0";
    }

    internal sealed class WinSqlite : IDisposable
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;
        private const int SQLITE_OPEN_CREATE = 0x00000004;
        private const int SQLITE_OPEN_URI = 0x00000040;
        private const int SQLITE_OPEN_NOMUTEX = 0x00008000;
        private const long MaxTempCopyBytes = 256L * 1024L * 1024L;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSqlUtf8, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_backup_init(IntPtr dest, byte[] destName, IntPtr source, byte[] sourceName);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_backup_step(IntPtr backup, int pages);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_backup_finish(IntPtr backup);

        private IntPtr _db = IntPtr.Zero;
        private string _tempDir = null;

        private WinSqlite(IntPtr db, string tempDir)
        {
            _db = db;
            _tempDir = tempDir;
            try { sqlite3_busy_timeout(_db, 2500); } catch { }
        }

        public static string SqlQuote(string val)
        {
            if (val == null) return "''";
            string clean = val.IndexOf('\0') >= 0 ? val.Replace("\0", "") : val;
            return "'" + clean.Replace("'", "''") + "'";
        }

        public bool BackupTo(string path)
        {
            using (WinSqlite dest = OpenReadWrite(path))
            {
                if (dest == null) return false;
                byte[] main = Encoding.ASCII.GetBytes("main\0");
                IntPtr backup = sqlite3_backup_init(dest._db, main, _db, main);
                if (backup == IntPtr.Zero) return false;
                int rc = sqlite3_backup_step(backup, -1);
                int finishRc = sqlite3_backup_finish(backup);
                return rc == SQLITE_DONE && finishRc == SQLITE_OK;
            }
        }

        public static WinSqlite OpenReadWrite(string dbPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                byte[] utf8 = Encoding.UTF8.GetBytes(dbPath + "\0");
                IntPtr db;
                int rc = sqlite3_open_v2(utf8, out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
                if (rc == SQLITE_OK && db != IntPtr.Zero)
                {
                    WinSqlite ws = new WinSqlite(db, null);
                    ws.Exec("PRAGMA journal_mode=WAL;");
                    ws.Exec("PRAGMA synchronous=NORMAL;");
                    return ws;
                }
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
            }
            catch { }
            return null;
        }

        public static WinSqlite OpenReadOnly(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) return null;

            IntPtr db = TryOpenDirect(dbPath, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX);
            if (db != IntPtr.Zero && CanQueryMaster(db))
            {
                return new WinSqlite(db, null);
            }
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
            }

            // Zero-copy SQLite URI read-only fallback for large or locked IDE databases (e.g. >120MB Cursor state.vscdb)
            string roUri = BuildSqliteFileUri(dbPath, "mode=ro");
            if (!string.IsNullOrEmpty(roUri))
            {
                IntPtr dbUriRo = TryOpenDirect(roUri, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX);
                if (dbUriRo != IntPtr.Zero && CanQueryMaster(dbUriRo))
                {
                    return new WinSqlite(dbUriRo, null);
                }
                if (dbUriRo != IntPtr.Zero)
                {
                    sqlite3_close_v2(dbUriRo);
                }
            }

            // Copy to private %TEMP% directory using shared FileStream (up to 256MB) and open READWRITE so SQLite can recover WAL frames
            bool canCopy = true;
            try
            {
                FileInfo fi = new FileInfo(dbPath);
                if (fi.Exists && fi.Length > MaxTempCopyBytes)
                    canCopy = false;
            }
            catch { }

            if (canCopy)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "TokLens_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(tempDir);
                    string dst = Path.Combine(tempDir, Path.GetFileName(dbPath));
                    if (CopyFileShared(dbPath, dst, MaxTempCopyBytes))
                    {
                        string[] suffixes = new string[] { "-wal", "-shm" };
                        for (int i = 0; i < suffixes.Length; i++)
                        {
                            string side = dbPath + suffixes[i];
                            try
                            {
                                if (File.Exists(side))
                                    CopyFileShared(side, dst + suffixes[i], MaxTempCopyBytes);
                            }
                            catch { }
                        }

                        IntPtr dbCopy = TryOpenDirect(dst, SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX);
                        if (dbCopy == IntPtr.Zero || !CanQueryMaster(dbCopy))
                        {
                            if (dbCopy != IntPtr.Zero) sqlite3_close_v2(dbCopy);
                            dbCopy = TryOpenDirect(dst, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX);
                        }
                        if (dbCopy != IntPtr.Zero && CanQueryMaster(dbCopy))
                        {
                            return new WinSqlite(dbCopy, tempDir);
                        }
                        if (dbCopy != IntPtr.Zero)
                        {
                            sqlite3_close_v2(dbCopy);
                        }
                    }
                }
                catch
                {
                }

                try { Directory.Delete(tempDir, true); } catch { }
            }

            // Final zero-copy immutable URI fallback for >256MB locked databases
            string immUri = BuildSqliteFileUri(dbPath, "immutable=1");
            if (!string.IsNullOrEmpty(immUri))
            {
                IntPtr dbImm = TryOpenDirect(immUri, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX);
                if (dbImm != IntPtr.Zero && CanQueryMaster(dbImm))
                {
                    return new WinSqlite(dbImm, null);
                }
                if (dbImm != IntPtr.Zero)
                {
                    sqlite3_close_v2(dbImm);
                }
            }

            return null;
        }

        private static string BuildSqliteFileUri(string rawPath, string queryParam)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return null;
            string p = rawPath.Trim();
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
                p = p.Substring(4);

            string escaped = p.Replace("%", "%25").Replace("?", "%3f").Replace("#", "%23").Replace('\\', '/');
            string uriBase;
            if (escaped.StartsWith("//", StringComparison.Ordinal))
                uriBase = "file:" + escaped;
            else if (escaped.StartsWith("/", StringComparison.Ordinal))
                uriBase = "file://" + escaped;
            else
                uriBase = "file:///" + escaped;

            if (string.IsNullOrEmpty(queryParam))
                return uriBase;
            return uriBase + "?" + queryParam;
        }

        private static bool CopyFileShared(string srcPath, string dstPath, long maxBytes)
        {
            using (FileStream src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
            {
                if (maxBytes > 0 && src.Length > maxBytes)
                    return false;
                using (FileStream dst = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    byte[] buf = new byte[65536];
                    int read;
                    long total = 0L;
                    while ((read = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += read;
                        if (maxBytes > 0 && total > maxBytes)
                            return false;
                        dst.Write(buf, 0, read);
                    }
                }
            }
            return true;
        }

        private static IntPtr TryOpenDirect(string pathOrUri, int flags)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(pathOrUri + "\0");
            IntPtr db;
            int rc = sqlite3_open_v2(utf8, out db, flags, IntPtr.Zero);
            if (rc == SQLITE_OK && db != IntPtr.Zero)
            {
                return db;
            }
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            return IntPtr.Zero;
        }

        public bool IsHealthy()
        {
            if (_db == IntPtr.Zero) return false;
            return CanQueryMaster(_db);
        }

        private static bool CanQueryMaster(IntPtr db)
        {
            byte[] sql = Encoding.UTF8.GetBytes("SELECT 1 FROM sqlite_master LIMIT 1\0");
            IntPtr stmt;
            int rc = sqlite3_prepare_v2(db, sql, sql.Length - 1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) return false;
            int stepRc = sqlite3_step(stmt);
            sqlite3_finalize(stmt);
            return stepRc == SQLITE_ROW || stepRc == SQLITE_DONE;
        }

        public bool Exec(string sql)
        {
            if (_db == IntPtr.Zero || string.IsNullOrEmpty(sql)) return false;
            byte[] utf8 = Encoding.UTF8.GetBytes(sql + "\0");
            IntPtr stmt;
            int rc = sqlite3_prepare_v2(_db, utf8, utf8.Length - 1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK || stmt == IntPtr.Zero) return false;
            try
            {
                int stepRc = sqlite3_step(stmt);
                return stepRc == SQLITE_DONE || stepRc == SQLITE_ROW;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        public bool ExecRequired(string sql)
        {
            if (!Exec(sql)) throw new IOException("无法更新用量账本；事务已中止，原有记录将保留。");
            return true;
        }

        public bool Query(string sql, Action<SqliteRow> onRow)
        {
            if (_db == IntPtr.Zero) return false;
            byte[] utf8 = Encoding.UTF8.GetBytes(sql + "\0");
            IntPtr stmt;
            int rc = sqlite3_prepare_v2(_db, utf8, utf8.Length - 1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK || stmt == IntPtr.Zero) return false;
            try
            {
                SqliteRow row = new SqliteRow(stmt);
                int stepRc;
                while ((stepRc = sqlite3_step(stmt)) == SQLITE_ROW)
                {
                    onRow(row);
                }
                return stepRc == SQLITE_DONE;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        public struct SqliteRow
        {
            private readonly IntPtr _stmt;
            public SqliteRow(IntPtr stmt) { _stmt = stmt; }

            public string GetString(int col)
            {
                IntPtr ptr = sqlite3_column_text(_stmt, col);
                if (ptr == IntPtr.Zero) return string.Empty;
                int len = sqlite3_column_bytes(_stmt, col);
                if (len <= 0) return string.Empty;
                byte[] buf = new byte[len];
                Marshal.Copy(ptr, buf, 0, len);
                return Encoding.UTF8.GetString(buf);
            }

            public byte[] GetBlob(int col)
            {
                IntPtr ptr = sqlite3_column_blob(_stmt, col);
                if (ptr == IntPtr.Zero) return null;
                int len = sqlite3_column_bytes(_stmt, col);
                if (len <= 0) return new byte[0];
                byte[] buf = new byte[len];
                Marshal.Copy(ptr, buf, 0, len);
                return buf;
            }

            public long GetInt64(int col)
            {
                return sqlite3_column_int64(_stmt, col);
            }

            public double GetDouble(int col)
            {
                return sqlite3_column_double(_stmt, col);
            }
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero)
            {
                sqlite3_close_v2(_db);
                _db = IntPtr.Zero;
            }
            if (!string.IsNullOrEmpty(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }
    }

    // 原生系统 archiveint.dll Zstandard (.zst) 流式解压器（Windows 10/11 零外部依赖）
    internal static class WinZstdReader
    {
        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr archive_read_new();

        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int archive_read_support_filter_zstd(IntPtr a);

        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int archive_read_support_format_raw(IntPtr a);

        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int archive_read_open_filename_w(IntPtr a, [MarshalAs(UnmanagedType.LPWStr)] string filename, UIntPtr blockSize);

        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int archive_read_next_header(IntPtr a, out IntPtr entry);

        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr archive_read_data(IntPtr a, byte[] buff, UIntPtr size);

        [DllImport("archiveint.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int archive_read_free(IntPtr a);

        private static bool MatchesAnyKeyword(string line, string[] keywords)
        {
            if (keywords == null || keywords.Length == 0) return true;
            for (int i = 0; i < keywords.Length; i++)
            {
                string kw = keywords[i];
                if (!string.IsNullOrEmpty(kw) && line.IndexOf(kw, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsBalancedJsonLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line.Trim();
            return t.Length >= 2 && t[0] == '{' && t[t.Length - 1] == '}';
        }

        internal static bool TryScanLines(string filePath, string[] anyKeywords, Action<string, int> onLine)
        {
            if (string.IsNullOrWhiteSpace(filePath) || onLine == null)
                return false;

            IntPtr a = IntPtr.Zero;
            try
            {
                a = archive_read_new();
                if (a == IntPtr.Zero) return false;
                if (archive_read_support_filter_zstd(a) != 0) return false;
                if (archive_read_support_format_raw(a) != 0) return false;

                UIntPtr blockSz = new UIntPtr(65536U);
                int openRc = archive_read_open_filename_w(a, filePath, blockSz);
                if (openRc != 0 && !filePath.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    archive_read_free(a);
                    a = archive_read_new();
                    if (a == IntPtr.Zero) return false;
                    if (archive_read_support_filter_zstd(a) != 0) return false;
                    if (archive_read_support_format_raw(a) != 0) return false;
                    string longPath = filePath.StartsWith(@"\\", StringComparison.Ordinal)
                        ? @"\\?\UNC\" + filePath.Substring(2)
                        : @"\\?\" + filePath;
                    openRc = archive_read_open_filename_w(a, longPath, blockSz);
                }
                if (openRc != 0) return false;

                IntPtr entry;
                if (archive_read_next_header(a, out entry) != 0)
                    return false;

                byte[] byteBuf = new byte[65536];
                char[] charBuf = new char[65544];
                Decoder decoder = Encoding.UTF8.GetDecoder();
                StringBuilder lineSb = new StringBuilder(4096);
                int lineIdx = 0;

                while (true)
                {
                    IntPtr nPtr = archive_read_data(a, byteBuf, blockSz);
                    long n = nPtr.ToInt64();
                    if (n <= 0L)
                    {
                        if (n < 0L) return false;
                        break;
                    }

                    int byteCount = (int)Math.Min((long)byteBuf.Length, n);
                    int charCount = decoder.GetChars(byteBuf, 0, byteCount, charBuf, 0, false);
                    for (int i = 0; i < charCount; i++)
                    {
                        char ch = charBuf[i];
                        if (ch == '\n')
                        {
                            int len = lineSb.Length;
                            if (len > 0 && lineSb[len - 1] == '\r')
                                lineSb.Length = len - 1;
                            if (lineSb.Length > 0)
                            {
                                string line = lineSb.ToString();
                                lineSb.Length = 0;
                                lineIdx++;
                                if (MatchesAnyKeyword(line, anyKeywords))
                                    onLine(line, lineIdx);
                            }
                        }
                        else
                        {
                            lineSb.Append(ch);
                        }
                    }
                }

                int tailChars = decoder.GetChars(byteBuf, 0, 0, charBuf, 0, true);
                for (int i = 0; i < tailChars; i++)
                {
                    char ch = charBuf[i];
                    if (ch != '\n') lineSb.Append(ch);
                }
                if (lineSb.Length > 0)
                {
                    if (lineSb[lineSb.Length - 1] == '\r')
                        lineSb.Length = lineSb.Length - 1;
                    string rem = lineSb.ToString();
                    if (IsBalancedJsonLine(rem))
                    {
                        lineIdx++;
                        if (MatchesAnyKeyword(rem, anyKeywords))
                            onLine(rem, lineIdx);
                    }
                }

                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (a != IntPtr.Zero)
                {
                    try { archive_read_free(a); } catch { }
                }
            }
        }
    }

    public sealed class ModelRate
    {
        public double InputPerM;
        public double OutputPerM;
        public double CacheReadPerM;
        public double CacheWritePerM;
        public string Source;

        public ModelRate(double inp, double outp, double cr, double cw, string src)
        {
            InputPerM = inp;
            OutputPerM = outp;
            CacheReadPerM = cr;
            CacheWritePerM = cw;
            Source = src;
        }

        public string Label
        {
            get
            {
                if (Source == "unpriced") return "未定价";
                return string.Format(CultureInfo.InvariantCulture, "${0:G} / ${1:G}", InputPerM, OutputPerM);
            }
        }
    }

    public sealed class UsageEvent
    {
        public string Tool;
        public string Model;
        public string Date;
        public long FreshInput;
        public long Output;
        public long CacheRead;
        public long CacheWrite;
        public double LoggedCostUsd;
        public int Turns;
        public string DedupKey;
        public string SessionId = "";
        public long TimestampMs;
        public int DurationMs;
        public string TimingSource = ""; // "request" only for an explicit elapsed time or paired request timestamps.
        public long CacheWrite1h;

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns)
            : this(tool, model, date, fi, outTok, cr, cw, loggedCost, turns, "", 0L, 0, 0L)
        {
        }

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns, string dedupKey)
            : this(tool, model, date, fi, outTok, cr, cw, loggedCost, turns, dedupKey, 0L, 0, 0L)
        {
        }

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns, string dedupKey, long timestampMs, int durationMs)
            : this(tool, model, date, fi, outTok, cr, cw, loggedCost, turns, dedupKey, timestampMs, durationMs, 0L)
        {
        }

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns, string dedupKey, long timestampMs, int durationMs, long cacheWrite1h)
        {
            Tool = tool;
            Model = model;
            Date = date;
            FreshInput = fi;
            Output = outTok;
            CacheRead = cr;
            CacheWrite = cw;
            LoggedCostUsd = loggedCost;
            Turns = turns;
            DedupKey = dedupKey ?? "";
            TimestampMs = timestampMs;
            DurationMs = durationMs;
            CacheWrite1h = cacheWrite1h > 0L ? cacheWrite1h : 0L;
        }

        public UsageEvent Clone()
        {
            UsageEvent copy = new UsageEvent(Tool, Model, Date, FreshInput, Output, CacheRead, CacheWrite, LoggedCostUsd, Turns, DedupKey, TimestampMs, DurationMs, CacheWrite1h);
            copy.SessionId = SessionId;
            copy.TimingSource = TimingSource;
            return copy;
        }
    }

    public sealed class RecentRequestRecord
    {
        public long TimestampMs;
        public string Date;
        public string TimeLabel;
        public string Tool;
        public string Model;
        public long FreshInput;
        public long CacheRead;
        public long CacheWrite;
        public long Output;
        public int DurationMs;
        public string TimingSource = "";
        public double CostUsd;

        public long TotalTokens
        {
            get { return FreshInput + CacheRead + CacheWrite + Output; }
        }
    }

    public sealed class LiveRequestRowView
    {
        public long TimestampMs { get; set; }
        public string Date { get; set; }
        public string TimeText { get; set; }
        public string Tool { get; set; }
        public string Model { get; set; }
        public long FreshInput { get; set; }
        public string FreshInputText { get; set; }
        public long CacheRead { get; set; }
        public string CacheReadText { get; set; }
        public long CacheWrite { get; set; }
        public string CacheWriteText { get; set; }
        public long Output { get; set; }
        public string OutputText { get; set; }
        public long TotalTokens { get; set; }
        public string TotalTokensText { get; set; }
        public int DurationMs { get; set; }
        public string TimingSource { get; set; }
        public string DurationText { get; set; }
        public double Tps { get; set; }
        public string TpsText { get; set; }
        public double CostUsd { get; set; }
        public double DisplayCost { get; set; }
        public string FormattedCost { get; set; }
    }

    public sealed class RecordBucket
    {
        public string MachineId;
        public string MachineName;
        public string MachineType;
        public string Tool;
        public string Model;
        public string Date;
        public int Turns;
        public long FreshInput;
        public long Output;
        public long CacheRead;
        public long CacheWrite;
        public double CostUsd;
        public double BaselineUsd;
        public string RateLabel;

        public long TotalTokens
        {
            get
            {
                long a = FreshInput > 0L ? FreshInput : 0L;
                long b = Output > 0L ? Output : 0L;
                long c = CacheRead > 0L ? CacheRead : 0L;
                long d = CacheWrite > 0L ? CacheWrite : 0L;
                double sumD = (double)a + (double)b + (double)c + (double)d;
                if (sumD >= 9000000000000000000.0) return 9000000000000000000L;
                return a + b + c + d;
            }
        }

        public RecordBucket Clone()
        {
            RecordBucket c = new RecordBucket();
            c.MachineId = MachineId;
            c.MachineName = MachineName;
            c.MachineType = MachineType;
            c.Tool = Tool;
            c.Model = Model;
            c.Date = Date;
            c.Turns = Turns;
            c.FreshInput = FreshInput;
            c.Output = Output;
            c.CacheRead = CacheRead;
            c.CacheWrite = CacheWrite;
            c.CostUsd = CostUsd;
            c.BaselineUsd = BaselineUsd;
            c.RateLabel = RateLabel;
            return c;
        }
    }

    public sealed class NodeSnapshot
    {
        public string MachineId;
        public string MachineName;
        public string MachineType;
        public string OsInfo;
        public string SyncedAt;
        public bool IsLocal;
        public int ScannerRev = UsageAccounting.Revision;
        public long AccountingUpdatedMs;
        public Dictionary<string, long> ReconciledPartitions = new Dictionary<string, long>(StringComparer.Ordinal);
        public List<RecordBucket> Records = new List<RecordBucket>();

        public NodeSnapshot Clone()
        {
            NodeSnapshot c = new NodeSnapshot();
            c.MachineId = MachineId;
            c.MachineName = MachineName;
            c.MachineType = MachineType;
            c.OsInfo = OsInfo;
            c.SyncedAt = SyncedAt;
            c.IsLocal = IsLocal;
            c.ScannerRev = ScannerRev;
            c.AccountingUpdatedMs = AccountingUpdatedMs;
            c.ReconciledPartitions = new Dictionary<string, long>(ReconciledPartitions, StringComparer.Ordinal);
            c.Records = new List<RecordBucket>(Records != null ? Records.Count : 0);
            if (Records != null)
            {
                for (int i = 0; i < Records.Count; i++)
                {
                    if (Records[i] != null)
                        c.Records.Add(Records[i].Clone());
                }
            }
            return c;
        }
    }

    public sealed class MachineRowView
    {
        public string MachineId { get; set; }
        public string MachineName { get; set; }
        public string TypeBadge { get; set; }
        public string OsInfo { get; set; }
        public bool IsOnline { get; set; }
        public string StatusText { get; set; }
        public string SyncedAt { get; set; }
        public string ToolsSummary { get; set; }
        public int ToolCount { get; set; }
        public int Turns { get; set; }
        public long FreshInput { get; set; }
        public string FreshInputText { get; set; }
        public long CacheRead { get; set; }
        public string CacheReadText { get; set; }
        public long Output { get; set; }
        public string OutputText { get; set; }
        public long TotalTokens { get; set; }
        public string TotalTokensText { get; set; }
        public double CostUsd { get; set; }
        public string UsdText { get; set; }
        public double CostCny { get; set; }
        public string CnyText { get; set; }
        public string FormattedMoney { get; set; }
        public double SharePercent { get; set; }
        public string ShareText { get; set; }
        public double BarRatio { get; set; }
        public string ColorHex { get; set; }
        public bool IsLocal { get; set; }
    }

    public sealed class SubscriptionPlanConfig
    {
        public string EcoId = "";       // "google", "claude", "openai", "cursor", "windsurf", "coding_plan"
        public bool Enabled = false;    // 用户是否持有/开启该订阅
        public string PlanName = "";    // 例如 "Google AI Pro"、"Claude Max 5x"、"自定义月费"
        public double MonthlyUsd = 0.0; // Legacy only; never treated as verified payment.
        public string Id = "";
        public string OfferId = "";
        public string Currency = "USD";
        public double Amount;
        public int BillingMonths = 1;
        public string Region = "";
        public string AccountLabel = "";
        public string StartDate = "";
        public string EndDate = "";
        public string Note = "";
        public bool FeeConfirmed;

        public SubscriptionPlanConfig Clone()
        {
            SubscriptionPlanConfig c = new SubscriptionPlanConfig();
            c.EcoId = this.EcoId ?? "";
            c.Enabled = this.Enabled;
            c.PlanName = this.PlanName ?? "";
            c.MonthlyUsd = this.MonthlyUsd;
            c.Id=Id; c.OfferId=OfferId; c.Currency=Currency; c.Amount=Amount; c.BillingMonths=BillingMonths;
            c.Region=Region; c.AccountLabel=AccountLabel; c.StartDate=StartDate; c.EndDate=EndDate; c.Note=Note; c.FeeConfirmed=FeeConfirmed;
            return c;
        }
    }

    public sealed class SyncConfig
    {
        public string MachineId = "";
        public string MachineName = "";
        public string Provider = "gist";
        public bool AutoSync = true;
        public bool ScanLocalWsl = false;
        public string GistId = "";
        public string GistToken = "";
        public string WebDavUrl = "";
        public string WebDavUser = "";
        public string WebDavPass = "";
        public bool IsDarkTheme = true;
        public bool PreferCny = true;
        public bool MinimizeToTray = false;
        // 可选功能：订阅记账与回本比例，默认关闭
        public bool ShowSubscriptions = false;
        public Dictionary<string, string> NodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool SubscriptionsCustomized = false;
        public List<SubscriptionPlanConfig> Subscriptions = new List<SubscriptionPlanConfig>();

        public bool IsConfigured
        {
            get
            {
                if (string.Equals(Provider, "webdav", StringComparison.OrdinalIgnoreCase))
                    return !string.IsNullOrWhiteSpace(WebDavUrl);
                return !string.IsNullOrWhiteSpace(GistId) && !string.IsNullOrWhiteSpace(GistToken);
            }
        }

        public SyncConfig Clone()
        {
            SyncConfig copy = new SyncConfig();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(SyncConfig other)
        {
            if (other == null) return;
            MachineId = other.MachineId ?? "";
            MachineName = other.MachineName ?? "";
            Provider = other.Provider ?? "gist";
            AutoSync = other.AutoSync;
            ScanLocalWsl = other.ScanLocalWsl;
            GistId = other.GistId ?? "";
            GistToken = other.GistToken ?? "";
            WebDavUrl = other.WebDavUrl ?? "";
            WebDavUser = other.WebDavUser ?? "";
            WebDavPass = other.WebDavPass ?? "";
            IsDarkTheme = other.IsDarkTheme;
            PreferCny = other.PreferCny;
            MinimizeToTray = other.MinimizeToTray;
            ShowSubscriptions = other.ShowSubscriptions;
            NodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (other.NodeAliases != null)
            {
                foreach (KeyValuePair<string, string> kv in other.NodeAliases)
                {
                    if (!string.IsNullOrEmpty(kv.Key))
                        NodeAliases[kv.Key] = kv.Value ?? "";
                }
            }
            SubscriptionsCustomized = other.SubscriptionsCustomized;
            Subscriptions = new List<SubscriptionPlanConfig>();
            if (other.Subscriptions != null)
            {
                for (int i = 0; i < other.Subscriptions.Count; i++)
                {
                    if (other.Subscriptions[i] != null)
                        Subscriptions.Add(other.Subscriptions[i].Clone());
                }
            }
        }
    }

    public sealed class ToolRowView
    {
        public string Tool { get; set; }
        public int Turns { get; set; }
        public int ModelCount { get; set; }
        public long FreshInput { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long Output { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public double DisplayMoney { get; set; }
        public string FormattedMoney { get; set; }
        public string FormattedTokens { get; set; }
        public double SharePercent { get; set; }
        public double BarRatio { get; set; }
        public string ColorHex { get; set; }
    }

    public sealed class ModelRowView
    {
        public string Model { get; set; }
        public string Tool { get; set; }
        public int Turns { get; set; }
        public long FreshInput { get; set; }
        public string FreshInputText { get; set; }
        public long CacheRead { get; set; }
        public string CacheReadText { get; set; }
        public long CacheWrite { get; set; }
        public string CacheWriteText { get; set; }
        public long Output { get; set; }
        public string OutputText { get; set; }
        public long TotalTokens { get; set; }
        public string TotalTokensText { get; set; }
        public double InputPerM { get; set; }
        public string RateLabel { get; set; }
        public string CacheHitText { get; set; }
        public double CostUsd { get; set; }
        public double DisplayCost { get; set; }
        public string FormattedCost { get; set; }
        public double BarRatio { get; set; }
        public string ShareText { get; set; }
    }

    public sealed class DailyRowView
    {
        public string Date { get; set; }
        public int Turns { get; set; }
        public long FreshInput { get; set; }
        public string FreshInputText { get; set; }
        public long CacheRead { get; set; }
        public string CacheReadText { get; set; }
        public long CacheWrite { get; set; }
        public string CacheWriteText { get; set; }
        public long Output { get; set; }
        public string OutputText { get; set; }
        public long TotalTokens { get; set; }
        public string TotalTokensText { get; set; }
        public double CostUsd { get; set; }
        public string UsdText { get; set; }
        public double CostCny { get; set; }
        public string CnyText { get; set; }
        public string FormattedCost { get; set; }
    }

    public sealed class ScanResult
    {
        public string UserHome;
        public string GeneratedAt;
        public string ReferenceDate;
        public int PricingModelCount;
        public double ElapsedSeconds;
        public int ChangedFilesCount;
        public int TotalFilesTracked;
        public int ArchivedFilesCount;
        public long AccountingUpdatedMs;
        public Dictionary<string, long> ReconciledPartitions = new Dictionary<string, long>(StringComparer.Ordinal);
        public List<RecordBucket> Records = new List<RecordBucket>();
        public List<RecentRequestRecord> RecentRequests = new List<RecentRequestRecord>();
        public List<string> Warnings = new List<string>();
    }

    public sealed class PricingSyncInfo
    {
        public int CloudModelCount;
        public string Source;
        public string UpdatedAt;
        public bool IsLive;

        public PricingSyncInfo(int count, string source, string updatedAt, bool isLive)
        {
            CloudModelCount = count;
            Source = source;
            UpdatedAt = updatedAt;
            IsLive = isLive;
        }
    }

    // 100% Self-Sufficient Cloud & Builtin Pricing Engine (Zero External Software Dependency)
    public sealed class PricingEngine
    {
        private readonly string _homeDir;
        private readonly bool _isCustomHome;
        private readonly Dictionary<string, ModelRate> _rates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();
        private static Dictionary<string, ModelRate> _memCloudRates = null;
        private static long _memCloudCacheMtimeTicks = -1;
        private static string _memCloudCachePath = "";
        private static string _lastCloudSource = "内置基准";
        private static string _lastCloudUpdated = "";

        private static readonly Regex SpaceUnderscoreRegex = new Regex(@"[\s_]+", RegexOptions.Compiled);
        private static readonly Regex TierSuffixRegex = new Regex(@"-(?:xhigh|high|medium|low|minimal|thinking|nothinking|reasoning|preview|latest|beta|exp|experimental|fast|max|auto|online|nitro|exacto|1m)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DateSuffixRegex = new Regex(@"-(?:20\d{6}|20\d{2}-\d{2}-\d{2}|\d{4})$", RegexOptions.Compiled);
        private static readonly Regex ClaudeDotVerRegex = new Regex(@"^(claude-(?:opus|sonnet|haiku|fable|mythos)-\d+)\.(\d+.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ClaudeDashVerRegex = new Regex(@"^(claude-(?:opus|sonnet|haiku|fable|mythos)-\d+)-(\d+)(?=-|$)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ClaudeLegacyDotRegex = new Regex(@"^(claude-\d+)\.(\d+-(?:opus|sonnet|haiku).*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ClaudeLegacyDashRegex = new Regex(@"^(claude-\d+)-(\d+-(?:opus|sonnet|haiku).*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public int Count { get { return _rates.Count; } }
        public static string CurrentSourceLabel { get { return _lastCloudSource; } }
        public static string CurrentUpdatedLabel { get { return _lastCloudUpdated; } }

        public static string CloudPriceCachePath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "TokLens", "pricing_cache.json");
            }
        }

        public PricingEngine(string homeDir)
        {
            string defaultHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            bool hasExplicit = !string.IsNullOrWhiteSpace(homeDir);
            _homeDir = hasExplicit ? homeDir : defaultHome;
            _isCustomHome = hasExplicit &&
                            !string.IsNullOrWhiteSpace(defaultHome) &&
                            !string.Equals(_homeDir.TrimEnd('\\', '/'), defaultHome.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            InitBuiltin();
            LoadCloudPricingFast();
            InitInternalModelOverrides();
            LoadCustomPricing();
        }

        private void Add(string id, double inp, double outp, double cr, double cw)
        {
            _rates[id.ToLowerInvariant()] = new ModelRate(inp, outp, cr, cw, "builtin");
        }

        private void InitBuiltin()
        {
            // Anthropic Claude (both dash and dot minor version forms registered to prevent prefix mismatch)
            Add("claude-opus-4-6", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4.6", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4-5", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4.5", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4-1", 15.0, 75.0, 1.5, 18.75);
            Add("claude-opus-4.1", 15.0, 75.0, 1.5, 18.75);
            Add("claude-opus-4", 15.0, 75.0, 1.5, 18.75);
            Add("claude-opus-4-0", 15.0, 75.0, 1.5, 18.75);
            Add("claude-opus-4.0", 15.0, 75.0, 1.5, 18.75);
            Add("claude-sonnet-4-6", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4.6", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4-5", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4.5", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4-0", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4.0", 3.0, 15.0, 0.3, 3.75);
            Add("claude-3-7-sonnet", 3.0, 15.0, 0.3, 3.75);
            Add("claude-3.7-sonnet", 3.0, 15.0, 0.3, 3.75);
            Add("claude-3-5-sonnet", 3.0, 15.0, 0.3, 3.75);
            Add("claude-3.5-sonnet", 3.0, 15.0, 0.3, 3.75);
            Add("claude-haiku-4-5", 1.0, 5.0, 0.1, 1.25);
            Add("claude-haiku-4.5", 1.0, 5.0, 0.1, 1.25);
            Add("claude-3-5-haiku", 0.8, 4.0, 0.08, 1.0);
            Add("claude-3.5-haiku", 0.8, 4.0, 0.08, 1.0);
            Add("claude-3-opus", 15.0, 75.0, 1.5, 18.75);
            Add("claude-3-haiku", 0.25, 1.25, 0.03, 0.3);

            // OpenAI / Codex
            Add("gpt-5.4-pro", 15.0, 60.0, 1.5, 0.0);
            Add("gpt-5.4", 2.5, 15.0, 0.25, 0.0);
            Add("gpt-5.4-mini", 0.4, 1.6, 0.04, 0.0);
            Add("gpt-5.4-nano", 0.1, 0.4, 0.01, 0.0);
            Add("gpt-5.3-codex", 1.75, 14.0, 0.175, 0.0);
            Add("gpt-5.2-codex", 1.75, 14.0, 0.175, 0.0);
            Add("gpt-5.2", 1.75, 14.0, 0.175, 0.0);
            Add("gpt-5.1-codex-max", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5.1-codex", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5.1-codex-mini", 0.25, 2.0, 0.025, 0.0);
            Add("gpt-5.1", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5-codex", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5-mini", 0.25, 2.0, 0.025, 0.0);
            Add("gpt-5-nano", 0.05, 0.4, 0.005, 0.0);
            Add("codex-mini", 1.5, 6.0, 0.375, 0.0);
            Add("codex-mini-latest", 1.5, 6.0, 0.375, 0.0);
            Add("gpt-4.1", 2.0, 8.0, 0.5, 0.0);
            Add("gpt-4.1-mini", 0.4, 1.6, 0.1, 0.0);
            Add("gpt-4.1-nano", 0.1, 0.4, 0.025, 0.0);
            Add("gpt-4o", 2.5, 10.0, 1.25, 0.0);
            Add("gpt-4o-mini", 0.15, 0.6, 0.075, 0.0);
            Add("o3-pro", 20.0, 80.0, 5.0, 0.0);
            Add("o3", 2.0, 8.0, 0.5, 0.0);
            Add("o3-mini", 1.1, 4.4, 0.275, 0.0);
            Add("o4-mini", 1.1, 4.4, 0.275, 0.0);
            Add("o1", 15.0, 60.0, 7.5, 0.0);
            Add("o1-mini", 1.1, 4.4, 0.55, 0.0);

            // Google Gemini
            Add("gemini-3.1-pro", 2.0, 12.0, 0.2, 0.0);
            Add("gemini-3.1-flash", 0.5, 3.0, 0.05, 0.0);
            Add("gemini-3.1-flash-lite", 0.1, 0.4, 0.025, 0.0);
            Add("gemini-3-pro", 2.0, 12.0, 0.2, 0.0);
            Add("gemini-3.0-pro", 2.0, 12.0, 0.2, 0.0);
            Add("gemini-3-flash", 0.5, 3.0, 0.05, 0.0);
            Add("gemini-3.0-flash", 0.5, 3.0, 0.05, 0.0);
            Add("gemini-2.5-pro", 1.25, 10.0, 0.125, 0.0);
            Add("gemini-2.5-flash", 0.3, 2.5, 0.03, 0.0);
            Add("gemini-2.5-flash-lite", 0.1, 0.4, 0.025, 0.0);
            Add("gemini-2.0-flash", 0.1, 0.4, 0.025, 0.0);
            Add("gemini-2.0-flash-lite", 0.075, 0.3, 0.01875, 0.0);

            // Zhipu GLM / Kimi / DeepSeek / MiniMax / Qwen / Doubao / Mistral / Grok
            Add("glm-5", 1.0, 3.2, 0.2, 0.0);
            Add("glm-4.7", 0.6, 2.2, 0.11, 0.0);
            Add("glm-4.6", 0.6, 2.2, 0.11, 0.0);
            Add("glm-4.5", 0.6, 2.2, 0.11, 0.0);
            Add("glm-4.5-air", 0.2, 1.1, 0.03, 0.0);
            Add("glm-4.5-flash", 0.1, 0.3, 0.01, 0.0);
            Add("kimi-k2.5", 0.6, 3.0, 0.1, 0.0);
            Add("kimi-k2-thinking", 0.6, 2.5, 0.15, 0.0);
            Add("kimi-k2-turbo", 0.6, 2.5, 0.15, 0.0);
            Add("kimi-k2", 0.6, 2.5, 0.15, 0.0);
            Add("deepseek-r1", 0.55, 2.19, 0.14, 0.0);
            Add("deepseek-reasoner", 0.55, 2.19, 0.14, 0.0);
            Add("deepseek-v3.2", 0.28, 0.42, 0.028, 0.0);
            Add("deepseek-v3.1", 0.55, 1.67, 0.055, 0.0);
            Add("deepseek-v3", 0.27, 1.1, 0.07, 0.0);
            Add("deepseek-chat", 0.27, 1.1, 0.07, 0.0);
            Add("minimax-m2.5", 0.3, 1.2, 0.03, 0.375);
            Add("minimax-m2.5-highspeed", 0.3, 1.2, 0.03, 0.375);
            Add("minimax-m2.1", 0.3, 1.2, 0.03, 0.375);
            Add("minimax-m2", 0.3, 1.2, 0.03, 0.375);
            Add("minimax-m1", 0.4, 2.2, 0.04, 0.0);
            Add("qwen3.5-plus", 0.4, 1.2, 0.04, 0.0);
            Add("qwen3.5-coder", 1.0, 4.0, 0.1, 0.0);
            Add("qwen3-coder-plus", 1.0, 5.0, 0.1, 0.0);
            Add("qwen3-coder", 0.3, 1.2, 0.03, 0.0);
            Add("qwen3-coder-flash", 0.15, 0.6, 0.015, 0.0);
            Add("qwen3-max", 1.2, 6.0, 0.12, 0.0);
            Add("qwen3-235b-a22b", 0.3, 1.2, 0.03, 0.0);
            Add("qwen3-32b", 0.15, 0.6, 0.015, 0.0);
            Add("qwq-32b", 0.15, 0.6, 0.015, 0.0);
            Add("qwen-max", 1.6, 6.4, 0.16, 0.0);
            Add("qwen-plus", 0.4, 1.2, 0.04, 0.0);
            Add("doubao-seed-code", 0.15, 0.6, 0.03, 0.0);
            Add("doubao-1.5-pro", 0.15, 0.6, 0.03, 0.0);
            Add("codestral", 0.3, 0.9, 0.03, 0.0);
            Add("devstral", 0.1, 0.3, 0.01, 0.0);
            Add("mistral-large", 2.0, 6.0, 0.2, 0.0);
            Add("grok-4", 3.0, 15.0, 0.75, 0.0);
            Add("grok-3", 3.0, 15.0, 0.75, 0.0);
            Add("grok-code-fast", 0.2, 1.5, 0.02, 0.0);
        }

        private void InitInternalModelOverrides()
        {
            // Next-gen / harness-specific model IDs that may not yet exist in public registries
            Add("claude-fable-5-2", 8.0, 40.0, 0.2, 10.0);
            Add("claude-fable-5.2", 8.0, 40.0, 0.2, 10.0);
            Add("claude-fable-5-1", 10.0, 50.0, 0.25, 12.5);
            Add("claude-fable-5.1", 10.0, 50.0, 0.25, 12.5);
            Add("claude-fable-5", 10.0, 50.0, 1.0, 12.5);
            Add("claude-mythos-5", 10.0, 50.0, 1.0, 12.5);
            Add("claude-opus-5-5", 4.0, 20.0, 0.2, 5.0);
            Add("claude-opus-5.5", 4.0, 20.0, 0.2, 5.0);
            Add("claude-opus-5", 5.0, 25.0, 0.5, 6.25);
            Add("claude-sonnet-5-5", 2.0, 10.0, 0.1, 2.5);
            Add("claude-sonnet-5.5", 2.0, 10.0, 0.1, 2.5);
            Add("claude-sonnet-5", 2.0, 10.0, 0.2, 2.5);
            Add("claude-opus-4-8", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4.8", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4-7", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4.7", 5.0, 25.0, 0.5, 6.25);

            Add("gpt-6-astra", 10.0, 50.0, 1.0, 12.5);
            Add("gpt-6.1-sol", 2.0, 10.0, 0.1, 2.5);
            Add("gpt-6-sol", 2.0, 10.0, 0.2, 2.5);
            Add("gpt-6.1-terra", 1.0, 5.0, 0.05, 1.25);
            Add("gpt-6-luna", 0.1, 0.5, 0.01, 0.125);

            Add("gemini-4-carbon", 2.0, 10.0, 0.1, 0.0);
            Add("gemini-4-neon", 0.4, 2.0, 0.04, 0.0);
            Add("gemini-4-argon", 2.0, 10.0, 0.1, 0.0);
            Add("gemini-4-hydrogen", 1.5, 7.5, 0.075, 0.0);
            Add("gemini-pro-orchestrator", 2.0, 10.0, 0.1, 0.0);
            Add("orchestrator-carbon", 2.0, 10.0, 0.1, 0.0);
            Add("orchestrator", 2.0, 10.0, 0.1, 0.0);
            Add("gemini-flash-worker-agy", 0.75, 3.75, 0.075, 0.0);
            Add("gemini-3.8-flash", 0.75, 3.75, 0.075, 0.0);
            Add("gemini-3.8-flash-tiered", 0.4, 2.0, 0.04, 0.0);
            Add("gemini-3.8-flash-low", 0.2, 1.0, 0.02, 0.0);
            Add("gemini-3.5-flash-lite", 0.1, 0.4, 0.025, 0.0);
            Add("gemini-3.1-flash", 0.3, 1.5, 0.03, 0.0);
            Add("gemini-3.1-pro", 1.5, 8.0, 0.15, 0.0);
            Add("qwen3.5-coder", 0.4, 1.6, 0.08, 0.0);
            Add("qwen3.5-plus", 0.4, 1.2, 0.08, 0.0);

            Add("glm-5.3", 1.4, 4.4, 0.28, 0.0);
            Add("glm-5.3-flash", 0.1, 0.4, 0.02, 0.0);
            Add("glm-5.2", 1.2, 4.0, 0.24, 0.0);
            Add("glm-5.1", 1.0, 3.2, 0.2, 0.0);

            Add("kimi-for-coding", 0.95, 4.0, 0.14, 0.0);
            Add("kimi-k3.1-pro-highspeed", 4.0, 20.0, 0.2, 4.0);
            Add("kimi-k3.1-pro", 2.0, 10.0, 0.2, 2.0);
            Add("kimi-k2.7-code", 0.95, 4.0, 0.19, 0.0);
            Add("kimi-k2.6", 0.95, 4.0, 0.16, 0.0);
            Add("k3", 1.2, 5.0, 0.18, 0.0);
            Add("k3-256k", 1.2, 5.0, 0.18, 0.0);
            Add("kimi-k3", 1.2, 5.0, 0.18, 0.0);

            Add("deepseek-v4.1-flash", 0.1, 0.4, 0.01, 0.0);
            Add("deepseek-v4-pro", 0.66, 1.98, 0.022, 0.0);
            Add("deepseek-v4-flash", 0.14, 0.28, 0.0028, 0.0);
            Add("deepseek-v4", 0.3, 1.2, 0.03, 0.0);
            Add("deepseek-flash", 0.07, 0.28, 0.007, 0.0);
            Add("workbuddy-default", 0.6, 2.4, 0.06, 0.0);
        }

        private void LoadCloudPricingFast()
        {
            try
            {
                string fp = _isCustomHome
                    ? Path.Combine(_homeDir, ".toklens", "pricing_cache.json")
                    : CloudPriceCachePath;
                if (!File.Exists(fp)) return;
                long mtimeTicks = File.GetLastWriteTimeUtc(fp).Ticks;

                lock (CacheLock)
                {
                    if (_memCloudRates == null ||
                        _memCloudCacheMtimeTicks != mtimeTicks ||
                        !string.Equals(_memCloudCachePath, fp, StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, ModelRate> parsed = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        js.MaxJsonLength = int.MaxValue;
                        Dictionary<string, object> root = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                        bool validSchema = root != null &&
                                           root.ContainsKey("schema") &&
                                           string.Equals(Convert.ToString(root["schema"], CultureInfo.InvariantCulture), "v2.2", StringComparison.Ordinal);
                        if (validSchema)
                        {
                            if (root.ContainsKey("source") && root["source"] != null)
                                _lastCloudSource = Convert.ToString(root["source"], CultureInfo.InvariantCulture);
                            if (root.ContainsKey("updated_at") && root["updated_at"] != null)
                                _lastCloudUpdated = Convert.ToString(root["updated_at"], CultureInfo.InvariantCulture);

                            if (root.ContainsKey("models"))
                            {
                                Dictionary<string, object> models = root["models"] as Dictionary<string, object>;
                                if (models != null)
                                {
                                    foreach (KeyValuePair<string, object> kv in models)
                                    {
                                        IList arr = kv.Value as IList;
                                        if (arr == null || arr.Count < 4) continue;
                                        double ic = Convert.ToDouble(arr[0], CultureInfo.InvariantCulture);
                                        double oc = Convert.ToDouble(arr[1], CultureInfo.InvariantCulture);
                                        double crc = Convert.ToDouble(arr[2], CultureInfo.InvariantCulture);
                                        double cwc = Convert.ToDouble(arr[3], CultureInfo.InvariantCulture);
                                        if (ic > 0 || oc > 0)
                                            parsed[kv.Key.ToLowerInvariant()] = new ModelRate(ic, oc, crc, cwc, "cloud");
                                    }
                                }
                            }
                        }
                        _memCloudRates = parsed;
                        _memCloudCacheMtimeTicks = mtimeTicks;
                        _memCloudCachePath = fp;
                    }

                    if (_memCloudRates != null)
                    {
                        foreach (KeyValuePair<string, ModelRate> kv in _memCloudRates)
                        {
                            ModelRate existing;
                            if (_rates.TryGetValue(kv.Key, out existing))
                            {
                                // 保护内置官方直连单价免受第三方代理网关（如 +5% 手续费 $0.42 vs $0.40）微幅加价污染
                                if (string.Equals(existing.Source, "builtin", StringComparison.OrdinalIgnoreCase) &&
                                    existing.InputPerM > 0 && existing.OutputPerM > 0 &&
                                    Math.Abs(existing.InputPerM - kv.Value.InputPerM) <= existing.InputPerM * 0.15 &&
                                    Math.Abs(existing.OutputPerM - kv.Value.OutputPerM) <= existing.OutputPerM * 0.15)
                                {
                                    continue;
                                }
                                double crc = kv.Value.CacheReadPerM > 0 ? kv.Value.CacheReadPerM : (Math.Abs(existing.InputPerM - kv.Value.InputPerM) <= kv.Value.InputPerM * 0.25 ? existing.CacheReadPerM : 0.0);
                                double cwc = kv.Value.CacheWritePerM > 0 ? kv.Value.CacheWritePerM : (Math.Abs(existing.InputPerM - kv.Value.InputPerM) <= kv.Value.InputPerM * 0.25 ? existing.CacheWritePerM : 0.0);
                                _rates[kv.Key] = new ModelRate(kv.Value.InputPerM, kv.Value.OutputPerM, crc, cwc, "cloud");
                            }
                            else
                            {
                                _rates[kv.Key] = kv.Value;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadCustomPricingFromFile(string fp)
        {
            if (string.IsNullOrWhiteSpace(fp)) return;
            try
            {
                if (!File.Exists(fp)) return;
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> map = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                if (map == null) return;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    Dictionary<string, object> m = kv.Value as Dictionary<string, object>;
                    if (m == null) continue;
                    double ic = m.ContainsKey("input") ? Convert.ToDouble(m["input"], CultureInfo.InvariantCulture) : 0;
                    double oc = m.ContainsKey("output") ? Convert.ToDouble(m["output"], CultureInfo.InvariantCulture) : 0;
                    double crc = m.ContainsKey("cache_read") ? Convert.ToDouble(m["cache_read"], CultureInfo.InvariantCulture) : 0;
                    double cwc = m.ContainsKey("cache_write") ? Convert.ToDouble(m["cache_write"], CultureInfo.InvariantCulture) : 0;
                    if (ic >= 0 && oc >= 0 && (m.ContainsKey("input") || m.ContainsKey("output")))
                    {
                        ModelRate customRate = new ModelRate(ic, oc, crc, cwc, "custom");
                        string rawKey = kv.Key.Trim().ToLowerInvariant();
                        _rates[rawKey] = customRate;
                        string normKey = NormalizeModelId(kv.Key, "");
                        if (!string.IsNullOrEmpty(normKey))
                            _rates[normKey] = customRate;
                    }
                }
            }
            catch { }
        }

        private void LoadCustomPricing()
        {
            try
            {
                if (_isCustomHome)
                {
                    if (!string.IsNullOrWhiteSpace(_homeDir))
                    {
                        LoadCustomPricingFromFile(Path.Combine(_homeDir, ".toklens", "custom_prices.json"));
                    }
                    return;
                }

                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    LoadCustomPricingFromFile(Path.Combine(userProfile, ".toklens", "custom_prices.json"));
                }
                if (!string.IsNullOrWhiteSpace(_homeDir) &&
                    !string.Equals(_homeDir.TrimEnd('\\', '/'), (userProfile ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    LoadCustomPricingFromFile(Path.Combine(_homeDir, ".toklens", "custom_prices.json"));
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                {
                    LoadCustomPricingFromFile(Path.Combine(appData, "TokLens", "custom_prices.json"));
                }
            }
            catch { }
        }

        private static bool IsFirstPartyProviderKey(string rawKey)
        {
            if (string.IsNullOrEmpty(rawKey)) return false;
            int slash = rawKey.IndexOf('/');
            if (slash < 0 && rawKey.IndexOf(':') < 0)
                return true;
            if (slash > 0)
            {
                string prov = rawKey.Substring(0, slash).ToLowerInvariant();
                return prov == "anthropic" || prov == "openai" || prov == "google" || prov == "gemini" ||
                       prov == "deepseek" || prov == "qwen" || prov == "moonshotai" || prov == "moonshot" ||
                       prov == "z-ai" || prov == "zhipu" || prov == "minimax" || prov == "x-ai" || prov == "mistralai";
            }
            return false;
        }

        private static void UpsertMergedCloudRate(
            Dictionary<string, double[]> merged,
            Dictionary<string, int> mergedPriority,
            string slug,
            double ic,
            double oc,
            double crc,
            double cwc,
            int priority)
        {
            if (string.IsNullOrEmpty(slug) || slug.Length < 2) return;
            double rIc = Math.Round(ic, 4);
            double rOc = Math.Round(oc, 4);
            double rCrc = Math.Round(crc, 4);
            double rCwc = Math.Round(cwc, 4);

            double[] prev;
            if (!merged.TryGetValue(slug, out prev))
            {
                merged[slug] = new double[] { rIc, rOc, rCrc, rCwc };
                mergedPriority[slug] = priority;
                return;
            }

            int prevPrio = 0;
            mergedPriority.TryGetValue(slug, out prevPrio);
            bool similarInput = prev[0] > 0 && Math.Abs(prev[0] - rIc) <= Math.Max(prev[0], rIc) * 0.25;

            if (priority < prevPrio)
            {
                if (similarInput)
                {
                    if (prev[2] <= 0 && rCrc > 0) prev[2] = rCrc;
                    if (prev[3] <= 0 && rCwc > 0) prev[3] = rCwc;
                }
                return;
            }

            if (similarInput)
            {
                if (rCrc <= 0 && prev[2] > 0) rCrc = prev[2];
                if (rCwc <= 0 && prev[3] > 0) rCwc = prev[3];
            }
            merged[slug] = new double[] { rIc, rOc, rCrc, rCwc };
            mergedPriority[slug] = priority;
        }

        public static PricingSyncInfo SyncCloudPricing(bool forceRefresh)
        {
            try
            {
                string fp = CloudPriceCachePath;
                if (!forceRefresh && File.Exists(fp))
                {
                    JavaScriptSerializer jsCheck = new JavaScriptSerializer();
                    jsCheck.MaxJsonLength = int.MaxValue;
                    Dictionary<string, object> old = jsCheck.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                    if (old != null &&
                        old.ContainsKey("schema") &&
                        string.Equals(Convert.ToString(old["schema"], CultureInfo.InvariantCulture), "v2.2", StringComparison.Ordinal) &&
                        old.ContainsKey("ts_utc") &&
                        old.ContainsKey("models"))
                    {
                        long tsUtc = Convert.ToInt64(old["ts_utc"], CultureInfo.InvariantCulture);
                        long nowUtc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        Dictionary<string, object> mOld = old["models"] as Dictionary<string, object>;
                        if (mOld != null && mOld.Count > 50 && (nowUtc - tsUtc) < 24 * 3600)
                        {
                            string srcOld = old.ContainsKey("source") ? Convert.ToString(old["source"], CultureInfo.InvariantCulture) : "云端价格库";
                            string updOld = old.ContainsKey("updated_at") ? Convert.ToString(old["updated_at"], CultureInfo.InvariantCulture) : "";
                            _lastCloudSource = srcOld;
                            _lastCloudUpdated = updOld;
                            return new PricingSyncInfo(mOld.Count, srcOld, updOld, true);
                        }
                    }
                }
            }
            catch { }

            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }

            Dictionary<string, double[]> merged = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> mergedPrio = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> sourcesOk = new List<string>();
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;

            // Source 1: LiteLLM Official Registry (via jsDelivr CDN, fallback GitHub Raw)
            string[] litellmUrls = new string[]
            {
                "https://cdn.jsdelivr.net/gh/BerriAI/litellm@main/model_prices_and_context_window.json",
                "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
            };
            for (int u = 0; u < litellmUrls.Length; u++)
            {
                string body = FetchUrlString(litellmUrls[u], 8000);
                if (!string.IsNullOrEmpty(body))
                {
                    try
                    {
                        Dictionary<string, object> dict = js.DeserializeObject(body) as Dictionary<string, object>;
                        if (dict != null && dict.Count > 100)
                        {
                            foreach (KeyValuePair<string, object> kv in dict)
                            {
                                if (kv.Key == "sample_spec" || kv.Key.EndsWith(":free", StringComparison.OrdinalIgnoreCase)) continue;
                                Dictionary<string, object> item = kv.Value as Dictionary<string, object>;
                                if (item == null) continue;
                                double ic = ParsePerTokenToMillion(item, "input_cost_per_token");
                                double oc = ParsePerTokenToMillion(item, "output_cost_per_token");
                                if (ic <= 0 || oc <= 0 || ic > 500 || oc > 1500) continue;
                                double crc = ParsePerTokenToMillion(item, "cache_read_input_token_cost");
                                double cwc = ParsePerTokenToMillion(item, "cache_creation_input_token_cost");
                                string slug = NormalizeModelId(kv.Key, "");
                                int prio = IsFirstPartyProviderKey(kv.Key) ? 20 : 10;
                                UpsertMergedCloudRate(merged, mergedPrio, slug, ic, oc, crc, cwc, prio);
                            }
                            sourcesOk.Add("LiteLLM");
                            break;
                        }
                    }
                    catch { }
                }
            }

            // Source 2: OpenRouter Live Public Models API
            string orBody = FetchUrlString("https://openrouter.ai/api/v1/models", 7000);
            if (!string.IsNullOrEmpty(orBody))
            {
                try
                {
                    Dictionary<string, object> orRoot = js.DeserializeObject(orBody) as Dictionary<string, object>;
                    if (orRoot != null && orRoot.ContainsKey("data"))
                    {
                        IList dataList = orRoot["data"] as IList;
                        if (dataList != null)
                        {
                            for (int i = 0; i < dataList.Count; i++)
                            {
                                Dictionary<string, object> m = dataList[i] as Dictionary<string, object>;
                                if (m == null || !m.ContainsKey("id") || !m.ContainsKey("pricing")) continue;
                                string rawId = Convert.ToString(m["id"], CultureInfo.InvariantCulture);
                                if (rawId.EndsWith(":free", StringComparison.OrdinalIgnoreCase)) continue;
                                Dictionary<string, object> pr = m["pricing"] as Dictionary<string, object>;
                                if (pr == null) continue;
                                double ic = ParsePerTokenToMillion(pr, "prompt");
                                double oc = ParsePerTokenToMillion(pr, "completion");
                                if (ic <= 0 || oc <= 0 || ic > 500 || oc > 1500) continue;
                                double crc = ParsePerTokenToMillion(pr, "input_cache_read");
                                double cwc = ParsePerTokenToMillion(pr, "input_cache_write");
                                string slug = NormalizeModelId(rawId, "");
                                int prio = IsFirstPartyProviderKey(rawId) ? 25 : 15;
                                UpsertMergedCloudRate(merged, mergedPrio, slug, ic, oc, crc, cwc, prio);
                            }
                            sourcesOk.Add("OpenRouter");
                        }
                    }
                }
                catch { }
            }

            // Source 3: models.dev Open AI Coding Model Registry (USD per 1M tokens directly, ultra-accurate Cache Read/Write)
            string mdBody = FetchUrlString("https://models.dev/api.json", 7000);
            if (!string.IsNullOrEmpty(mdBody))
            {
                try
                {
                    Dictionary<string, object> mdRoot = js.DeserializeObject(mdBody) as Dictionary<string, object>;
                    if (mdRoot != null && mdRoot.Count > 3)
                    {
                        int mdAdded = 0;
                        foreach (KeyValuePair<string, object> provKv in mdRoot)
                        {
                            Dictionary<string, object> provObj = provKv.Value as Dictionary<string, object>;
                            if (provObj == null || !provObj.ContainsKey("models")) continue;
                            Dictionary<string, object> modelsObj = provObj["models"] as Dictionary<string, object>;
                            if (modelsObj == null) continue;
                            bool firstPartyProv = IsFirstPartyProviderKey(provKv.Key + "/x");
                            foreach (KeyValuePair<string, object> mKv in modelsObj)
                            {
                                Dictionary<string, object> mObj = mKv.Value as Dictionary<string, object>;
                                if (mObj == null || !mObj.ContainsKey("cost")) continue;
                                Dictionary<string, object> costObj = mObj["cost"] as Dictionary<string, object>;
                                if (costObj == null) continue;
                                double ic = ParseDirectPerMillion(costObj, "input");
                                double oc = ParseDirectPerMillion(costObj, "output");
                                if (ic <= 0 || oc <= 0 || ic > 500 || oc > 1500) continue;
                                double crc = ParseDirectPerMillion(costObj, "cache_read");
                                double cwc = ParseDirectPerMillion(costObj, "cache_write");
                                string slug = NormalizeModelId(mKv.Key, "");
                                if (slug.Length >= 2)
                                {
                                    int prio = firstPartyProv ? 30 : 18;
                                    UpsertMergedCloudRate(merged, mergedPrio, slug, ic, oc, crc, cwc, prio);
                                    mdAdded++;
                                }
                            }
                        }
                        if (mdAdded > 0) sourcesOk.Add("models.dev");
                    }
                }
                catch { }
            }

            if (merged.Count > 0)
            {
                string srcJoined = string.Join(" + ", sourcesOk.ToArray());
                string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                long nowUtc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                try
                {
                    string fp = CloudPriceCachePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(fp));
                    Dictionary<string, object> saveObj = new Dictionary<string, object>();
                    saveObj["schema"] = "v2.2";
                    saveObj["source"] = srcJoined;
                    saveObj["updated_at"] = nowStr;
                    saveObj["ts_utc"] = nowUtc;
                    saveObj["models"] = merged;
                    File.WriteAllText(fp, js.Serialize(saveObj), new UTF8Encoding(false));
                    lock (CacheLock) { _memCloudCacheMtimeTicks = -1; }
                }
                catch { }
                _lastCloudSource = srcJoined;
                _lastCloudUpdated = nowStr;
                return new PricingSyncInfo(merged.Count, srcJoined, nowStr, true);
            }

            return new PricingSyncInfo(0, "内置基准", "", false);
        }

        private static double ParseDirectPerMillion(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return 0.0;
            double v;
            if (double.TryParse(Convert.ToString(d[key], CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return v > 0 ? v : 0.0;
            return 0.0;
        }

        private static double ParsePerTokenToMillion(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return 0.0;
            double v;
            if (double.TryParse(Convert.ToString(d[key], CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return v > 0 ? (v * 1000000.0) : 0.0;
            return 0.0;
        }

        private static string FetchUrlString(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "TokLens/2.2";
                req.Accept = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream st = resp.GetResponseStream())
                {
                    if (st == null) return null;
                    using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public static string NormalizeModelId(string rawModel, string fallback)
        {
            if (string.IsNullOrWhiteSpace(rawModel)) return fallback;
            StringBuilder sbClean = null;
            for (int i = 0; i < rawModel.Length; i++)
            {
                char ch = rawModel[i];
                if (ch < ' ')
                {
                    if (sbClean == null)
                    {
                        sbClean = new StringBuilder(rawModel.Length);
                        sbClean.Append(rawModel, 0, i);
                    }
                }
                else if (sbClean != null)
                {
                    sbClean.Append(ch);
                }
            }
            string m = (sbClean != null ? sbClean.ToString() : rawModel).Trim();
            if (string.IsNullOrEmpty(m)) return fallback;
            if (m == "<synthetic>" || m == "agent-loop" || m == "unknown" || m == "none" || m == "null")
                return fallback;
            int slashIdx = m.LastIndexOf('/');
            if (slashIdx >= 0 && slashIdx < m.Length - 1)
                m = m.Substring(slashIdx + 1).Trim();
            if (!m.StartsWith("ft:", StringComparison.OrdinalIgnoreCase))
            {
                int colonIdx = m.LastIndexOf(':');
                if (colonIdx >= 0 && colonIdx < m.Length - 1)
                    m = m.Substring(colonIdx + 1).Trim();
            }
            string clean = m.Replace("(", " ").Replace(")", " ").Trim();
            string slug = SpaceUnderscoreRegex.Replace(clean.ToLowerInvariant(), "-");
            return string.IsNullOrEmpty(slug) ? fallback : slug;
        }

        private static string ConvertClaudeVersionForm(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
                return null;
            Match mDot = ClaudeDotVerRegex.Match(s);
            if (mDot.Success)
                return mDot.Groups[1].Value + "-" + mDot.Groups[2].Value;
            Match mDash = ClaudeDashVerRegex.Match(s);
            if (mDash.Success)
                return mDash.Groups[1].Value + "." + mDash.Groups[2].Value + mDash.Groups[3].Value;
            Match mLegDot = ClaudeLegacyDotRegex.Match(s);
            if (mLegDot.Success)
                return mLegDot.Groups[1].Value + "-" + mLegDot.Groups[2].Value;
            Match mLegDash = ClaudeLegacyDashRegex.Match(s);
            if (mLegDash.Success)
                return mLegDash.Groups[1].Value + "." + mLegDash.Groups[2].Value;
            return null;
        }

        private bool TryDirectOrClaudeAlias(string candidate, out ModelRate rate)
        {
            rate = null;
            if (string.IsNullOrEmpty(candidate)) return false;
            if (_rates.TryGetValue(candidate, out rate)) return true;
            string alt = ConvertClaudeVersionForm(candidate);
            if (!string.IsNullOrEmpty(alt) && _rates.TryGetValue(alt, out rate))
                return true;
            return false;
        }

        public ModelRate Lookup(string rawModel)
        {
            string slug = NormalizeModelId(rawModel, "unknown");
            ModelRate r;
            if (TryDirectOrClaudeAlias(slug, out r)) return r;

            string cur = slug;
            for (int iter = 0; iter < 6; iter++)
            {
                string next = TierSuffixRegex.Replace(cur, "");
                if (!string.Equals(next, cur, StringComparison.Ordinal))
                {
                    cur = next;
                    if (TryDirectOrClaudeAlias(cur, out r)) return r;
                }
                string nextNoDate = DateSuffixRegex.Replace(cur, "");
                if (!string.Equals(nextNoDate, cur, StringComparison.Ordinal))
                {
                    cur = nextNoDate;
                    if (TryDirectOrClaudeAlias(cur, out r)) return r;
                }
                else if (string.Equals(next, cur, StringComparison.Ordinal))
                {
                    break;
                }
            }

            // A shared family name is not evidence for a model's price.
            // Preserve measured tokens and expose the missing rate explicitly.
            return new ModelRate(0.0, 0.0, 0.0, 0.0, "unpriced");
        }

        private static void ApplyLongContextTierIfApplicable(
            string normModel,
            ModelRate baseRate,
            long avgPromptTokens,
            out double effIn,
            out double effOut,
            out double effCr,
            out double effCw)
        {
            effIn = baseRate.InputPerM;
            effOut = baseRate.OutputPerM;
            effCr = baseRate.CacheReadPerM;
            effCw = baseRate.CacheWritePerM;

            if (avgPromptTokens <= 200000L || string.Equals(baseRate.Source, "custom", StringComparison.OrdinalIgnoreCase))
                return;

            if (normModel.StartsWith("gemini-2.5-pro", StringComparison.Ordinal) ||
                normModel.StartsWith("gemini-3-pro", StringComparison.Ordinal) ||
                normModel.StartsWith("gemini-3.0-pro", StringComparison.Ordinal) ||
                normModel.StartsWith("gemini-3.1-pro", StringComparison.Ordinal))
            {
                effIn = baseRate.InputPerM * 2.0;
                effOut = baseRate.OutputPerM * 1.5;
                effCr = baseRate.CacheReadPerM * 2.0;
            }
            else if (normModel.StartsWith("claude-sonnet-4", StringComparison.Ordinal) ||
                     normModel.StartsWith("claude-4-sonnet", StringComparison.Ordinal) ||
                     normModel.StartsWith("claude-4.5-sonnet", StringComparison.Ordinal) ||
                     normModel.StartsWith("claude-4.6-sonnet", StringComparison.Ordinal))
            {
                effIn = baseRate.InputPerM * 2.0;
                effOut = baseRate.OutputPerM * 1.5;
                effCr = baseRate.CacheReadPerM * 2.0;
                effCw = baseRate.CacheWritePerM * 2.0;
            }
        }

        public void ComputeCost(string model, long fi, long outTok, long cr, long cw, double loggedUsd, out double actualUsd, out double baselineUsd, out ModelRate rate)
        {
            ComputeBucketCost(model, 1, fi, outTok, cr, cw, loggedUsd, out actualUsd, out baselineUsd, out rate);
        }

        public void ComputeBucketCost(string model, int turns, long fi, long outTok, long cr, long cw, double loggedUsd, out double actualUsd, out double baselineUsd, out ModelRate rate)
        {
            ComputeBucketCost(model, turns, fi, outTok, cr, cw, 0L, loggedUsd, out actualUsd, out baselineUsd, out rate);
        }

        public void ComputeBucketCost(string model, int turns, long fi, long outTok, long cr, long cw, long cw1h, double loggedUsd, out double actualUsd, out double baselineUsd, out ModelRate rate)
        {
            rate = Lookup(model);
            int safeTurns = turns > 0 ? turns : 1;
            long safeFi = fi > 0L ? fi : 0L;
            long safeOut = outTok > 0L ? outTok : 0L;
            long safeCr = cr > 0L ? cr : 0L;
            long safeCw = cw > 0L ? cw : 0L;
            long safeCw1h = cw1h > 0L ? (cw1h > safeCw ? safeCw : cw1h) : 0L;
            long safeCw5m = safeCw - safeCw1h;

            double totalPromptD = (double)safeFi + (double)safeCr + (double)safeCw;
            long avgPrompt = (long)Math.Min((double)long.MaxValue, totalPromptD / safeTurns);
            string norm = NormalizeModelId(model, "");

            double effIn, effOut, effCr, effCw;
            ApplyLongContextTierIfApplicable(norm, rate, avgPrompt, out effIn, out effOut, out effCr, out effCw);

            // Anthropic 1 小时缓存写入按 2.0x Input 计费（区别于 5 分钟缓存写入 1.25x）
            double effCw1h = string.Equals(rate.Source, "custom", StringComparison.OrdinalIgnoreCase)
                ? effCw
                : Math.Max(effCw, effIn * 2.0);

            double calc = ((double)safeFi * effIn + (double)safeOut * effOut + (double)safeCr * effCr + (double)safeCw5m * effCw + (double)safeCw1h * effCw1h) / 1000000.0;
            if (double.IsNaN(calc) || double.IsInfinity(calc) || calc < 0.0) calc = 0.0;
            else if (calc > 1000000000000.0) calc = 1000000000000.0;

            double baseCost = (totalPromptD * effIn + (double)safeOut * effOut) / 1000000.0;
            if (double.IsNaN(baseCost) || double.IsInfinity(baseCost) || baseCost < 0.0) baseCost = 0.0;
            else if (baseCost > 1000000000000.0) baseCost = 1000000000000.0;

            bool validLogged = loggedUsd > 0.0 && loggedUsd <= 1000000000.0 && !double.IsNaN(loggedUsd) && !double.IsInfinity(loggedUsd);
            actualUsd = validLogged ? loggedUsd : calc;
            baselineUsd = baseCost > actualUsd ? baseCost : actualUsd;
        }

        public void RecalculateRemoteSnapshot(NodeSnapshot snap)
        {
            if (snap == null || snap.Records == null) return;
            for (int i = 0; i < snap.Records.Count; i++)
            {
                RecordBucket r = snap.Records[i];
                if (r == null || r.TotalTokens <= 0) continue;

                ModelRate rate = Lookup(r.Model);
                bool isCustom = string.Equals(rate.Source, "custom", StringComparison.OrdinalIgnoreCase);
                bool missingCost = r.CostUsd <= 0.0;
                string trimmedLabel = r.RateLabel != null ? r.RateLabel.Trim() : "";
                bool wasFallbackRate = string.IsNullOrEmpty(trimmedLabel) ||
                                       string.Equals(trimmedLabel, "$1 / $4", StringComparison.Ordinal);
                bool hasBetterLocalRate = !string.Equals(rate.Source, "fallback", StringComparison.OrdinalIgnoreCase);

                if (isCustom || missingCost || (wasFallbackRate && hasBetterLocalRate))
                {
                    double actualUsd, baseUsd;
                    ModelRate appliedRate;
                    ComputeBucketCost(r.Model, r.Turns, r.FreshInput, r.Output, r.CacheRead, r.CacheWrite, 0.0, out actualUsd, out baseUsd, out appliedRate);
                    r.CostUsd = actualUsd;
                    r.BaselineUsd = baseUsd;
                    r.RateLabel = appliedRate.Label;
                }
            }
        }

    }

    public sealed class ExchangeRateInfo
    {
        public double Rate;
        public string Source;
        public string UpdatedAt;
        public bool IsLive;

        public ExchangeRateInfo(double rate, string source, string updatedAt, bool isLive)
        {
            Rate = rate;
            Source = source;
            UpdatedAt = updatedAt;
            IsLive = isLive;
        }
    }

    public static class ExchangeRateService
    {
        public const double DefaultRate = 7.25;

        private static string CacheFilePath
        {
            get
            {
                return Path.Combine(SyncService.AppConfigDir, "fx_cache.json");
            }
        }

        private static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
        }

        public static ExchangeRateInfo LoadCachedOrDefault(out bool isRecentCache)
        {
            isRecentCache = false;
            try
            {
                string fp = CacheFilePath;
                if (File.Exists(fp))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    Dictionary<string, object> d = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("rate"))
                    {
                        double r = Convert.ToDouble(d["rate"], CultureInfo.InvariantCulture);
                        string src = d.ContainsKey("source") ? Convert.ToString(d["source"], CultureInfo.InvariantCulture) : "本地缓存";
                        string upd = d.ContainsKey("updated_at") ? Convert.ToString(d["updated_at"], CultureInfo.InvariantCulture) : "";
                        long tsUtc = d.ContainsKey("ts_utc") ? Convert.ToInt64(d["ts_utc"], CultureInfo.InvariantCulture) : 0L;
                        if (r > 3.0 && r < 20.0)
                        {
                            long nowUtc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                            isRecentCache = (nowUtc - tsUtc) >= 0 && (nowUtc - tsUtc) < 6 * 3600;
                            return new ExchangeRateInfo(Math.Round(r, 4), src + " (缓存)", upd, true);
                        }
                    }
                }
            }
            catch { }

            return new ExchangeRateInfo(DefaultRate, "内置默认", "", false);
        }

        private static void SaveCache(double rate, string source, string updatedAt)
        {
            try
            {
                string fp = CacheFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(fp));
                long nowUtc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["rate"] = Math.Round(rate, 4);
                d["source"] = source;
                d["updated_at"] = updatedAt;
                d["ts_utc"] = nowUtc;
                JavaScriptSerializer js = new JavaScriptSerializer();
                File.WriteAllText(fp, js.Serialize(d), new UTF8Encoding(false));
            }
            catch { }
        }

        public static ExchangeRateInfo FetchLatestUsdToCny(bool forceRefresh)
        {
            bool recent;
            ExchangeRateInfo cached = LoadCachedOrDefault(out recent);
            if (!forceRefresh && recent)
                return cached;

            EnsureTls();
            JavaScriptSerializer js = new JavaScriptSerializer();
            string nowLabel = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            string s1 = FetchJson("https://open.er-api.com/v6/latest/USD", 4500);
            if (!string.IsNullOrEmpty(s1))
            {
                double r1 = ExtractNestedRate(js, s1, "rates", "CNY");
                if (r1 > 3.0 && r1 < 20.0)
                {
                    SaveCache(r1, "ExchangeRate-API", nowLabel);
                    return new ExchangeRateInfo(Math.Round(r1, 4), "ExchangeRate-API", nowLabel, true);
                }
            }

            string s2 = FetchJson("https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json", 4500);
            if (!string.IsNullOrEmpty(s2))
            {
                double r2 = ExtractNestedRate(js, s2, "usd", "cny");
                if (r2 > 3.0 && r2 < 20.0)
                {
                    SaveCache(r2, "Currency-API CDN", nowLabel);
                    return new ExchangeRateInfo(Math.Round(r2, 4), "Currency-API CDN", nowLabel, true);
                }
            }

            string s3 = FetchJson("https://api.frankfurter.app/latest?from=USD&to=CNY", 4500);
            if (!string.IsNullOrEmpty(s3))
            {
                double r3 = ExtractNestedRate(js, s3, "rates", "CNY");
                if (r3 > 3.0 && r3 < 20.0)
                {
                    SaveCache(r3, "ECB Frankfurter", nowLabel);
                    return new ExchangeRateInfo(Math.Round(r3, 4), "ECB Frankfurter", nowLabel, true);
                }
            }

            return cached;
        }

        private static double ExtractNestedRate(JavaScriptSerializer js, string json, string parentKey, string childKey)
        {
            try
            {
                Dictionary<string, object> root = js.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey(parentKey)) return 0.0;
                Dictionary<string, object> sub = root[parentKey] as Dictionary<string, object>;
                if (sub == null || !sub.ContainsKey(childKey) || sub[childKey] == null) return 0.0;
                return Convert.ToDouble(sub[childKey], CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0.0;
            }
        }

        private static string FetchJson(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "TokLens/2.2";
                req.Accept = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream st = resp.GetResponseStream())
                {
                    if (st == null) return null;
                    using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
