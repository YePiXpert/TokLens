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
    internal sealed class WinSqlite : IDisposable
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;
        private const int SQLITE_OPEN_CREATE = 0x00000004;
        private const int SQLITE_OPEN_NOMUTEX = 0x00008000;

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
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

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
            return "'" + val.Replace("'", "''") + "'";
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
            IntPtr db = TryOpenDirect(dbPath);
            if (db != IntPtr.Zero && CanQueryMaster(db))
            {
                return new WinSqlite(db, null);
            }
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
            }

            // Avoid copying multi-hundred-MB locked IDE state DBs into %TEMP% on UI refresh
            try
            {
                FileInfo fi = new FileInfo(dbPath);
                if (fi.Exists && fi.Length > 120L * 1024L * 1024L)
                    return null;
            }
            catch { }

            string tempDir = Path.Combine(Path.GetTempPath(), "TokLens_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dst = Path.Combine(tempDir, Path.GetFileName(dbPath));
                File.Copy(dbPath, dst, true);
                string[] suffixes = new string[] { "-wal", "-shm" };
                for (int i = 0; i < suffixes.Length; i++)
                {
                    string side = dbPath + suffixes[i];
                    if (File.Exists(side))
                    {
                        try { File.Copy(side, dst + suffixes[i], true); } catch { }
                    }
                }
                IntPtr dbCopy = TryOpenDirect(dst);
                if (dbCopy != IntPtr.Zero)
                {
                    return new WinSqlite(dbCopy, tempDir);
                }
            }
            catch
            {
            }

            try { Directory.Delete(tempDir, true); } catch { }
            return null;
        }

        private static IntPtr TryOpenDirect(string path)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(path + "\0");
            IntPtr db;
            int rc = sqlite3_open_v2(utf8, out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
            if (rc == SQLITE_OK && db != IntPtr.Zero)
            {
                return db;
            }
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            return IntPtr.Zero;
        }

        private static bool CanQueryMaster(IntPtr db)
        {
            byte[] sql = Encoding.UTF8.GetBytes("SELECT 1 FROM sqlite_master LIMIT 1\0");
            IntPtr stmt;
            int rc = sqlite3_prepare_v2(db, sql, sql.Length - 1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) return false;
            sqlite3_step(stmt);
            sqlite3_finalize(stmt);
            return true;
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

        public void Query(string sql, Action<SqliteRow> onRow)
        {
            if (_db == IntPtr.Zero) return;
            byte[] utf8 = Encoding.UTF8.GetBytes(sql + "\0");
            IntPtr stmt;
            int rc = sqlite3_prepare_v2(_db, utf8, utf8.Length - 1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK || stmt == IntPtr.Zero) return;
            try
            {
                SqliteRow row = new SqliteRow(stmt);
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    onRow(row);
                }
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
        public long TimestampMs;
        public int DurationMs;

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns)
            : this(tool, model, date, fi, outTok, cr, cw, loggedCost, turns, "", 0L, 0)
        {
        }

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns, string dedupKey)
            : this(tool, model, date, fi, outTok, cr, cw, loggedCost, turns, dedupKey, 0L, 0)
        {
        }

        public UsageEvent(string tool, string model, string date, long fi, long outTok, long cr, long cw, double loggedCost, int turns, string dedupKey, long timestampMs, int durationMs)
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
            get { return FreshInput + Output + CacheRead + CacheWrite; }
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
        public List<RecordBucket> Records = new List<RecordBucket>();
    }

    public sealed class MachineRowView
    {
        public string MachineId { get; set; }
        public string MachineName { get; set; }
        public string TypeBadge { get; set; }
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

        public bool IsConfigured
        {
            get
            {
                if (string.Equals(Provider, "webdav", StringComparison.OrdinalIgnoreCase))
                    return !string.IsNullOrWhiteSpace(WebDavUrl);
                return !string.IsNullOrWhiteSpace(GistId) && !string.IsNullOrWhiteSpace(GistToken);
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
        public string RateLabel { get; set; }
        public double CostUsd { get; set; }
        public double DisplayCost { get; set; }
        public string FormattedCost { get; set; }
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
        public long Output { get; set; }
        public string OutputText { get; set; }
        public long TotalTokens { get; set; }
        public string TotalTokensText { get; set; }
        public double CostUsd { get; set; }
        public string UsdText { get; set; }
        public double CostCny { get; set; }
        public string CnyText { get; set; }
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
        private readonly Dictionary<string, ModelRate> _rates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();
        private static Dictionary<string, ModelRate> _memCloudRates = null;
        private static long _memCloudCacheMtimeTicks = -1;
        private static string _lastCloudSource = "内置基准";
        private static string _lastCloudUpdated = "";

        private static readonly Regex ParenRegex = new Regex(@"\s*\([^)]*\)\s*", RegexOptions.Compiled);
        private static readonly Regex SpaceUnderscoreRegex = new Regex(@"[\s_]+", RegexOptions.Compiled);
        private static readonly Regex TierSuffixRegex = new Regex(@"-(high|medium|low|minimal|thinking|preview|latest)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DateSuffixRegex = new Regex(@"-\d{8}$", RegexOptions.Compiled);

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
            // Anthropic Claude
            Add("claude-opus-4-6", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4-5", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4-1", 15.0, 75.0, 1.5, 18.75);
            Add("claude-opus-4", 15.0, 75.0, 1.5, 18.75);
            Add("claude-sonnet-4-6", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4-5", 3.0, 15.0, 0.3, 3.75);
            Add("claude-sonnet-4", 3.0, 15.0, 0.3, 3.75);
            Add("claude-3-7-sonnet", 3.0, 15.0, 0.3, 3.75);
            Add("claude-3-5-sonnet", 3.0, 15.0, 0.3, 3.75);
            Add("claude-haiku-4-5", 1.0, 5.0, 0.1, 1.25);
            Add("claude-3-5-haiku", 0.8, 4.0, 0.08, 1.0);

            // OpenAI / Codex
            Add("gpt-5.4", 2.5, 15.0, 0.25, 0.0);
            Add("gpt-5.3-codex", 1.75, 14.0, 0.175, 0.0);
            Add("gpt-5.2-codex", 1.75, 14.0, 0.175, 0.0);
            Add("gpt-5.2", 1.75, 14.0, 0.175, 0.0);
            Add("gpt-5.1-codex", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5.1", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5-codex", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5", 1.25, 10.0, 0.125, 0.0);
            Add("gpt-5-mini", 0.25, 2.0, 0.025, 0.0);
            Add("gpt-4.1", 2.0, 8.0, 0.5, 0.0);
            Add("gpt-4.1-mini", 0.4, 1.6, 0.1, 0.0);
            Add("gpt-4o", 2.5, 10.0, 1.25, 0.0);
            Add("gpt-4o-mini", 0.15, 0.6, 0.075, 0.0);
            Add("o3", 2.0, 8.0, 0.5, 0.0);
            Add("o4-mini", 1.1, 4.4, 0.275, 0.0);

            // Google Gemini
            Add("gemini-3.1-pro", 2.0, 12.0, 0.2, 0.0);
            Add("gemini-3-pro", 2.0, 12.0, 0.2, 0.0);
            Add("gemini-3-flash", 0.5, 3.0, 0.05, 0.0);
            Add("gemini-2.5-pro", 1.25, 10.0, 0.125, 0.0);
            Add("gemini-2.5-flash", 0.3, 2.5, 0.03, 0.0);
            Add("gemini-2.0-flash", 0.1, 0.4, 0.025, 0.0);

            // Zhipu GLM / Kimi / DeepSeek / Qwen / Mistral / Grok
            Add("glm-5", 1.0, 3.2, 0.2, 0.0);
            Add("glm-4.7", 0.6, 2.2, 0.11, 0.0);
            Add("glm-4.6", 0.6, 2.2, 0.11, 0.0);
            Add("glm-4.5", 0.6, 2.2, 0.11, 0.0);
            Add("kimi-k2.5", 0.6, 3.0, 0.1, 0.0);
            Add("kimi-k2", 0.6, 2.5, 0.15, 0.0);
            Add("deepseek-v3.2", 0.28, 0.42, 0.028, 0.0);
            Add("deepseek-chat", 0.27, 1.1, 0.07, 0.0);
            Add("deepseek-reasoner", 0.55, 2.19, 0.14, 0.0);
            Add("qwen3-coder-plus", 1.0, 5.0, 0.1, 0.0);
            Add("qwen3-coder", 0.3, 1.2, 0.03, 0.0);
            Add("qwen3-max", 1.2, 6.0, 0.12, 0.0);
            Add("qwen-max", 1.6, 6.4, 0.16, 0.0);
            Add("qwen-plus", 0.4, 1.2, 0.04, 0.0);
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
            Add("claude-fable-5-1", 10.0, 50.0, 0.25, 12.5);
            Add("claude-fable-5", 10.0, 50.0, 1.0, 12.5);
            Add("claude-mythos-5", 10.0, 50.0, 1.0, 12.5);
            Add("claude-opus-5-5", 4.0, 20.0, 0.2, 5.0);
            Add("claude-opus-5", 5.0, 25.0, 0.5, 6.25);
            Add("claude-sonnet-5-5", 2.0, 10.0, 0.1, 2.5);
            Add("claude-sonnet-5", 2.0, 10.0, 0.2, 2.5);
            Add("claude-opus-4-8", 5.0, 25.0, 0.5, 6.25);
            Add("claude-opus-4-7", 5.0, 25.0, 0.5, 6.25);

            Add("gpt-6-astra", 10.0, 50.0, 1.0, 12.5);
            Add("gpt-6.1-sol", 2.0, 10.0, 0.1, 2.5);
            Add("gpt-6-sol", 2.0, 10.0, 0.2, 2.5);
            Add("gpt-6.1-terra", 1.0, 5.0, 0.05, 1.25);
            Add("gpt-6-luna", 0.1, 0.5, 0.01, 0.125);

            Add("gemini-4-carbon", 2.0, 10.0, 0.2, 0.0);
            Add("gemini-4-neon", 0.4, 2.0, 0.04, 0.0);
            Add("orchestrator", 2.0, 10.0, 0.2, 0.0);

            Add("glm-5.3", 1.4, 4.4, 0.28, 0.0);
            Add("glm-5.3-flash", 0.1, 0.4, 0.02, 0.0);
            Add("glm-5.2", 1.2, 4.0, 0.24, 0.0);
            Add("glm-5.1", 1.0, 3.2, 0.2, 0.0);

            Add("kimi-for-coding", 0.95, 4.0, 0.14, 0.0);
            Add("k3", 1.2, 5.0, 0.18, 0.0);
            Add("k3-256k", 1.2, 5.0, 0.18, 0.0);
            Add("kimi-k3", 1.2, 5.0, 0.18, 0.0);

            Add("deepseek-v4.1-flash", 0.1, 0.4, 0.01, 0.0);
            Add("deepseek-v4", 0.3, 1.2, 0.03, 0.0);
            Add("deepseek-flash", 0.07, 0.28, 0.007, 0.0);
            Add("workbuddy-default", 0.6, 2.4, 0.06, 0.0);
        }

        private void LoadCloudPricingFast()
        {
            try
            {
                string fp = CloudPriceCachePath;
                if (!File.Exists(fp)) return;
                long mtimeTicks = File.GetLastWriteTimeUtc(fp).Ticks;

                lock (CacheLock)
                {
                    if (_memCloudRates == null || _memCloudCacheMtimeTicks != mtimeTicks)
                    {
                        Dictionary<string, ModelRate> parsed = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        js.MaxJsonLength = int.MaxValue;
                        Dictionary<string, object> root = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                        if (root != null)
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
                    }

                    if (_memCloudRates != null)
                    {
                        foreach (KeyValuePair<string, ModelRate> kv in _memCloudRates)
                            _rates[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private void LoadCustomPricing()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string fp = Path.Combine(appData, "TokLens", "custom_prices.json");
                if (!File.Exists(fp)) return;
                JavaScriptSerializer js = new JavaScriptSerializer();
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
                    if (ic > 0 || oc > 0)
                        _rates[kv.Key.ToLowerInvariant()] = new ModelRate(ic, oc, crc, cwc, "custom");
                }
            }
            catch { }
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
                    if (old != null && old.ContainsKey("ts_utc") && old.ContainsKey("models"))
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
                                if (slug.Length >= 2)
                                    merged[slug] = new double[] { Math.Round(ic, 4), Math.Round(oc, 4), Math.Round(crc, 4), Math.Round(cwc, 4) };
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
                                if (slug.Length >= 2)
                                    merged[slug] = new double[] { Math.Round(ic, 4), Math.Round(oc, 4), Math.Round(crc, 4), Math.Round(cwc, 4) };
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
                                    merged[slug] = new double[] { Math.Round(ic, 4), Math.Round(oc, 4), Math.Round(crc, 4), Math.Round(cwc, 4) };
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
                req.UserAgent = "TokLens/2.1";
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
            string m = rawModel.Trim();
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
            string clean = ParenRegex.Replace(m, "").Trim();
            string slug = SpaceUnderscoreRegex.Replace(clean.ToLowerInvariant(), "-");
            return string.IsNullOrEmpty(slug) ? fallback : slug;
        }

        private static bool IsPrefixBoundaryMatch(string text, string prefix)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (text.Length == prefix.Length) return true;
            char next = text[prefix.Length];
            return next == '-' || next == '.' || next == '_';
        }

        public ModelRate Lookup(string rawModel)
        {
            string slug = NormalizeModelId(rawModel, "unknown");
            ModelRate r;
            if (_rates.TryGetValue(slug, out r)) return r;

            string stripped = TierSuffixRegex.Replace(slug, "");
            if (_rates.TryGetValue(stripped, out r)) return r;

            string noDate = DateSuffixRegex.Replace(stripped, "");
            if (_rates.TryGetValue(noDate, out r)) return r;

            string bestKey = null;
            foreach (string key in _rates.Keys)
            {
                if (key.Length < 4) continue;
                if (IsPrefixBoundaryMatch(slug, key) || IsPrefixBoundaryMatch(noDate, key))
                {
                    if (bestKey == null || key.Length > bestKey.Length)
                        bestKey = key;
                }
            }
            if (bestKey != null) return _rates[bestKey];

            if (slug.Contains("kimi") || slug.StartsWith("k3", StringComparison.Ordinal)) return _rates["kimi-for-coding"];
            if (slug.Contains("glm")) return _rates[slug.Contains("flash") ? "glm-5.3-flash" : "glm-5.3"];
            if (slug.Contains("deepseek")) return _rates[slug.Contains("flash") ? "deepseek-v4.1-flash" : "deepseek-v3.2"];
            if (slug.Contains("qwen")) return _rates["qwen3-coder-plus"];
            if (slug.Contains("devstral") || slug.Contains("codestral")) return _rates["devstral"];
            if (slug.Contains("opus")) return _rates["claude-opus-5-5"];
            if (slug.Contains("sonnet")) return _rates["claude-sonnet-4-6"];
            if (slug.Contains("haiku")) return _rates["claude-haiku-4-5"];
            if (slug.Contains("gemini")) return _rates["gemini-4-carbon"];
            if (slug.Contains("gpt") || slug.Contains("codex")) return _rates["gpt-5.2-codex"];

            return new ModelRate(1.0, 4.0, 0.1, 0.0, "fallback");
        }

        public void ComputeCost(string model, long fi, long outTok, long cr, long cw, double loggedUsd, out double actualUsd, out double baselineUsd, out ModelRate rate)
        {
            rate = Lookup(model);
            double calc = (fi * rate.InputPerM + outTok * rate.OutputPerM + cr * rate.CacheReadPerM + cw * rate.CacheWritePerM) / 1000000.0;
            double baseCost = ((fi + cr + cw) * rate.InputPerM + outTok * rate.OutputPerM) / 1000000.0;
            actualUsd = loggedUsd > 0.0 ? loggedUsd : calc;
            baselineUsd = baseCost > actualUsd ? baseCost : actualUsd;
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
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "TokLens", "fx_cache.json");
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
                req.UserAgent = "TokLens/2.1";
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
