using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TokLens
{
    internal sealed class FileCursorEntry
    {
        public string FileKey;
        public long FileSize;
        public long MtimeTicks;
        public long ByteOffset;
        public string ExtraMeta;
        public List<UsageEvent> Events;
    }

    // Persistent SQLite Cursor & Instant-Snapshot Store (%APPDATA%\TokLens\toklens.db)
    public static class LocalUsageStore
    {
        private const string StoreSchema = "toklens.store.v2.4";
        private static readonly object StoreLock = new object();
        private static Dictionary<string, FileCursorEntry> _memCursors = null;

        public static string DbFilePath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "TokLens", "toklens.db");
            }
        }

        private static WinSqlite OpenInitializedDb()
        {
            WinSqlite db = WinSqlite.OpenReadWrite(DbFilePath);
            if (db == null) return null;

            db.Exec("CREATE TABLE IF NOT EXISTS kv_meta (k TEXT PRIMARY KEY, v TEXT);");
            db.Exec("CREATE TABLE IF NOT EXISTS file_cursors (file_key TEXT PRIMARY KEY, file_size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, byte_offset INTEGER NOT NULL, extra_meta TEXT, events_data TEXT);");
            db.Exec("CREATE TABLE IF NOT EXISTS daily_archive (home_key TEXT NOT NULL, rec_date TEXT NOT NULL, tool TEXT NOT NULL, model TEXT NOT NULL, turns INTEGER NOT NULL, fi INTEGER NOT NULL, out_tok INTEGER NOT NULL, cr INTEGER NOT NULL, cw INTEGER NOT NULL, cost_usd REAL NOT NULL, base_usd REAL NOT NULL, rate_label TEXT, PRIMARY KEY(home_key, rec_date, tool, model));");

            string curVer = "";
            db.Query("SELECT v FROM kv_meta WHERE k = 'schema' LIMIT 1", delegate(WinSqlite.SqliteRow r)
            {
                curVer = r.GetString(0);
            });

            if (!string.Equals(curVer, StoreSchema, StringComparison.Ordinal))
            {
                // Preserve daily_archive permanently across schema upgrades; only reset transient cursors/kv_meta
                db.Exec("DELETE FROM file_cursors;");
                db.Exec("DELETE FROM kv_meta;");
                db.Exec("INSERT OR REPLACE INTO kv_meta (k, v) VALUES ('schema', " + WinSqlite.SqlQuote(StoreSchema) + ");");
            }
            return db;
        }

        public static ScanResult TryLoadInstantSnapshot(string homeDir)
        {
            if (string.IsNullOrWhiteSpace(homeDir)) return null;
            string snapKey = "snap:" + homeDir.Trim().ToLowerInvariant();
            string raw = null;

            lock (StoreLock)
            {
                try
                {
                    if (!File.Exists(DbFilePath)) return null;
                    using (WinSqlite db = OpenInitializedDb())
                    {
                        if (db == null) return null;
                        db.Query("SELECT v FROM kv_meta WHERE k = " + WinSqlite.SqlQuote(snapKey) + " LIMIT 1", delegate(WinSqlite.SqliteRow r)
                        {
                            raw = r.GetString(0);
                        });
                    }
                }
                catch
                {
                    return null;
                }
            }

            if (string.IsNullOrEmpty(raw)) return null;
            return DeserializeSnapshotResult(homeDir, raw);
        }

        internal static Dictionary<string, FileCursorEntry> LoadAllCursors()
        {
            lock (StoreLock)
            {
                if (_memCursors != null)
                {
                    return new Dictionary<string, FileCursorEntry>(_memCursors, StringComparer.OrdinalIgnoreCase);
                }

                Dictionary<string, FileCursorEntry> map = new Dictionary<string, FileCursorEntry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!File.Exists(DbFilePath))
                    {
                        _memCursors = map;
                        return new Dictionary<string, FileCursorEntry>(map, StringComparer.OrdinalIgnoreCase);
                    }
                    using (WinSqlite db = OpenInitializedDb())
                    {
                        if (db == null) return map;
                        db.Query("SELECT file_key, file_size, mtime_ticks, byte_offset, extra_meta, events_data FROM file_cursors", delegate(WinSqlite.SqliteRow r)
                        {
                            FileCursorEntry e = new FileCursorEntry();
                            e.FileKey = r.GetString(0);
                            e.FileSize = r.GetInt64(1);
                            e.MtimeTicks = r.GetInt64(2);
                            e.ByteOffset = r.GetInt64(3);
                            e.ExtraMeta = r.GetString(4);
                            e.Events = DeserializeEvents(r.GetString(5));
                            if (!string.IsNullOrEmpty(e.FileKey))
                                map[e.FileKey] = e;
                        });
                    }
                }
                catch { }
                _memCursors = map;
                return new Dictionary<string, FileCursorEntry>(map, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static void SaveScanBatch(string homeDir, List<FileCursorEntry> dirtyCursors, ScanResult snapshot)
        {
            if (dirtyCursors == null || dirtyCursors.Count == 0)
                return;

            lock (StoreLock)
            {
                if (_memCursors != null)
                {
                    for (int i = 0; i < dirtyCursors.Count; i++)
                        _memCursors[dirtyCursors[i].FileKey] = dirtyCursors[i];
                }

                try
                {
                    using (WinSqlite db = OpenInitializedDb())
                    {
                        if (db == null) return;
                        db.Exec("BEGIN IMMEDIATE;");
                        try
                        {
                            for (int i = 0; i < dirtyCursors.Count; i++)
                            {
                                FileCursorEntry c = dirtyCursors[i];
                                string evData = SerializeEvents(c.Events);
                                string sql = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "INSERT OR REPLACE INTO file_cursors (file_key, file_size, mtime_ticks, byte_offset, extra_meta, events_data) VALUES ({0}, {1}, {2}, {3}, {4}, {5});",
                                    WinSqlite.SqlQuote(c.FileKey),
                                    c.FileSize,
                                    c.MtimeTicks,
                                    c.ByteOffset,
                                    WinSqlite.SqlQuote(c.ExtraMeta ?? ""),
                                    WinSqlite.SqlQuote(evData));
                                db.Exec(sql);
                            }

                            if (snapshot != null && !string.IsNullOrWhiteSpace(homeDir))
                            {
                                string snapKey = "snap:" + homeDir.Trim().ToLowerInvariant();
                                string snapVal = SerializeSnapshotResult(snapshot);
                                db.Exec("INSERT OR REPLACE INTO kv_meta (k, v) VALUES (" + WinSqlite.SqlQuote(snapKey) + ", " + WinSqlite.SqlQuote(snapVal) + ");");
                            }

                            db.Exec("COMMIT;");
                        }
                        catch
                        {
                            db.Exec("ROLLBACK;");
                        }
                    }
                }
                catch { }
            }
        }

        private static string EscapeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");
        }

        internal static string SerializeEvents(List<UsageEvent> events)
        {
            if (events == null || events.Count == 0) return "";
            StringBuilder sb = new StringBuilder(events.Count * 80);
            for (int i = 0; i < events.Count; i++)
            {
                UsageEvent e = events[i];
                if (i > 0) sb.Append('\n');
                sb.Append(EscapeField(e.Tool)).Append('\t')
                  .Append(EscapeField(e.Model)).Append('\t')
                  .Append(EscapeField(e.Date)).Append('\t')
                  .Append(e.FreshInput.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Output.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.CacheRead.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.CacheWrite.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.LoggedCostUsd.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Turns.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(EscapeField(e.DedupKey)).Append('\t')
                  .Append(e.TimestampMs.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.DurationMs.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        internal static List<UsageEvent> DeserializeEvents(string data)
        {
            List<UsageEvent> list = new List<UsageEvent>();
            if (string.IsNullOrEmpty(data)) return list;
            int pos = 0;
            int len = data.Length;
            while (pos < len)
            {
                int nl = data.IndexOf('\n', pos);
                int end = nl < 0 ? len : nl;
                if (end > pos)
                {
                    string line = data.Substring(pos, end - pos);
                    string[] p = line.Split('\t');
                    if (p.Length >= 9)
                    {
                        long fi, outTok, cr, cw, tsMs = 0L;
                        double cost;
                        int turns, durMs = 0;
                        long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out fi);
                        long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out outTok);
                        long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out cr);
                        long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out cw);
                        double.TryParse(p[7], NumberStyles.Any, CultureInfo.InvariantCulture, out cost);
                        if (!int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out turns) || turns <= 0)
                            turns = 1;
                        string dkey = p.Length >= 10 ? p[9] : "";
                        if (p.Length >= 11) long.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out tsMs);
                        if (p.Length >= 12) int.TryParse(p[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out durMs);
                        list.Add(new UsageEvent(p[0], p[1], p[2], fi, outTok, cr, cw, cost, turns, dkey, tsMs, durMs));
                    }
                }
                pos = end + 1;
            }
            return list;
        }

        public static void MergeAndSaveDailyArchive(string homeDir, List<RecordBucket> liveBuckets)
        {
            if (string.IsNullOrWhiteSpace(homeDir) || liveBuckets == null)
                return;

            string homeKey = homeDir.Trim().ToLowerInvariant();

            lock (StoreLock)
            {
                try
                {
                    using (WinSqlite db = OpenInitializedDb())
                    {
                        if (db == null) return;

                        Dictionary<string, RecordBucket> archivedMap = new Dictionary<string, RecordBucket>(StringComparer.Ordinal);
                        string selSql = "SELECT rec_date, tool, model, turns, fi, out_tok, cr, cw, cost_usd, base_usd, rate_label FROM daily_archive WHERE home_key = " + WinSqlite.SqlQuote(homeKey);
                        db.Query(selSql, delegate(WinSqlite.SqliteRow r)
                        {
                            RecordBucket ab = new RecordBucket();
                            ab.Date = r.GetString(0);
                            ab.Tool = r.GetString(1);
                            ab.Model = r.GetString(2);
                            ab.Turns = (int)r.GetInt64(3);
                            ab.FreshInput = r.GetInt64(4);
                            ab.Output = r.GetInt64(5);
                            ab.CacheRead = r.GetInt64(6);
                            ab.CacheWrite = r.GetInt64(7);
                            ab.CostUsd = r.GetDouble(8);
                            ab.BaselineUsd = r.GetDouble(9);
                            ab.RateLabel = r.GetString(10);
                            string ak = ab.Date + "||" + ab.Tool + "||" + ab.Model;
                            archivedMap[ak] = ab;
                        });

                        HashSet<string> seenLiveKeys = new HashSet<string>(StringComparer.Ordinal);
                        List<RecordBucket> toUpsert = new List<RecordBucket>();

                        for (int i = 0; i < liveBuckets.Count; i++)
                        {
                            RecordBucket lb = liveBuckets[i];
                            string lk = (lb.Date ?? "") + "||" + (lb.Tool ?? "") + "||" + (lb.Model ?? "");
                            seenLiveKeys.Add(lk);

                            RecordBucket ab;
                            if (!archivedMap.TryGetValue(lk, out ab))
                            {
                                toUpsert.Add(lb);
                            }
                            else
                            {
                                int mergedTurns = Math.Max(lb.Turns, ab.Turns);
                                long mergedFi = Math.Max(lb.FreshInput, ab.FreshInput);
                                long mergedOut = Math.Max(lb.Output, ab.Output);
                                long mergedCr = Math.Max(lb.CacheRead, ab.CacheRead);
                                long mergedCw = Math.Max(lb.CacheWrite, ab.CacheWrite);
                                double mergedCost = Math.Max(lb.CostUsd, ab.CostUsd);
                                double mergedBase = Math.Max(lb.BaselineUsd, ab.BaselineUsd);
                                if (string.IsNullOrEmpty(lb.RateLabel) && !string.IsNullOrEmpty(ab.RateLabel))
                                    lb.RateLabel = ab.RateLabel;

                                bool grew = mergedTurns > ab.Turns ||
                                            mergedFi > ab.FreshInput ||
                                            mergedOut > ab.Output ||
                                            mergedCr > ab.CacheRead ||
                                            mergedCw > ab.CacheWrite ||
                                            (mergedCost - ab.CostUsd) > 1e-9 ||
                                            (mergedBase - ab.BaselineUsd) > 1e-9;

                                lb.Turns = mergedTurns;
                                lb.FreshInput = mergedFi;
                                lb.Output = mergedOut;
                                lb.CacheRead = mergedCr;
                                lb.CacheWrite = mergedCw;
                                lb.CostUsd = mergedCost;
                                lb.BaselineUsd = mergedBase;

                                if (grew)
                                    toUpsert.Add(lb);
                            }
                        }

                        bool addedMissing = false;
                        foreach (KeyValuePair<string, RecordBucket> kv in archivedMap)
                        {
                            if (!seenLiveKeys.Contains(kv.Key))
                            {
                                liveBuckets.Add(kv.Value);
                                addedMissing = true;
                            }
                        }

                        if (addedMissing)
                        {
                            liveBuckets.Sort(delegate(RecordBucket x, RecordBucket y)
                            {
                                int c = string.CompareOrdinal(x.Date, y.Date);
                                if (c != 0) return c;
                                c = string.CompareOrdinal(x.Tool, y.Tool);
                                if (c != 0) return c;
                                return string.CompareOrdinal(x.Model, y.Model);
                            });
                        }

                        if (toUpsert.Count > 0)
                        {
                            db.Exec("BEGIN IMMEDIATE;");
                            try
                            {
                                for (int i = 0; i < toUpsert.Count; i++)
                                {
                                    RecordBucket b = toUpsert[i];
                                    double safeCost = (double.IsNaN(b.CostUsd) || double.IsInfinity(b.CostUsd)) ? 0.0 : b.CostUsd;
                                    double safeBase = (double.IsNaN(b.BaselineUsd) || double.IsInfinity(b.BaselineUsd)) ? 0.0 : b.BaselineUsd;
                                    string upSql = string.Format(
                                        CultureInfo.InvariantCulture,
                                        "INSERT OR REPLACE INTO daily_archive (home_key, rec_date, tool, model, turns, fi, out_tok, cr, cw, cost_usd, base_usd, rate_label) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11});",
                                        WinSqlite.SqlQuote(homeKey),
                                        WinSqlite.SqlQuote(b.Date ?? ""),
                                        WinSqlite.SqlQuote(b.Tool ?? ""),
                                        WinSqlite.SqlQuote(b.Model ?? ""),
                                        b.Turns,
                                        b.FreshInput,
                                        b.Output,
                                        b.CacheRead,
                                        b.CacheWrite,
                                        safeCost.ToString("R", CultureInfo.InvariantCulture),
                                        safeBase.ToString("R", CultureInfo.InvariantCulture),
                                        WinSqlite.SqlQuote(b.RateLabel ?? ""));
                                    db.Exec(upSql);
                                }
                                db.Exec("COMMIT;");
                            }
                            catch
                            {
                                db.Exec("ROLLBACK;");
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static string SerializeSnapshotResult(ScanResult res)
        {
            StringBuilder sb = new StringBuilder((res.Records != null ? res.Records.Count : 0) * 80 + 4096);
            sb.Append("H\t")
              .Append(EscapeField(res.GeneratedAt)).Append('\t')
              .Append(EscapeField(res.ReferenceDate)).Append('\t')
              .Append(res.PricingModelCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(res.TotalFilesTracked.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(res.ArchivedFilesCount.ToString(CultureInfo.InvariantCulture));

            if (res.Records != null)
            {
                for (int i = 0; i < res.Records.Count; i++)
                {
                    RecordBucket r = res.Records[i];
                    sb.Append("\nB\t")
                      .Append(EscapeField(r.Date)).Append('\t')
                      .Append(EscapeField(r.Tool)).Append('\t')
                      .Append(EscapeField(r.Model)).Append('\t')
                      .Append(r.Turns.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r.FreshInput.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r.CacheRead.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r.CacheWrite.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r.Output.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r.CostUsd.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r.BaselineUsd.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(EscapeField(r.RateLabel));
                }
            }

            if (res.RecentRequests != null)
            {
                int limit = Math.Min(res.RecentRequests.Count, 300);
                for (int i = 0; i < limit; i++)
                {
                    RecentRequestRecord rq = res.RecentRequests[i];
                    sb.Append("\nR\t")
                      .Append(rq.TimestampMs.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(EscapeField(rq.Date)).Append('\t')
                      .Append(EscapeField(rq.TimeLabel)).Append('\t')
                      .Append(EscapeField(rq.Tool)).Append('\t')
                      .Append(EscapeField(rq.Model)).Append('\t')
                      .Append(rq.FreshInput.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rq.CacheRead.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rq.CacheWrite.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rq.Output.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rq.DurationMs.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rq.CostUsd.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        private static ScanResult DeserializeSnapshotResult(string homeDir, string raw)
        {
            try
            {
                ScanResult res = new ScanResult();
                res.UserHome = homeDir;
                res.ElapsedSeconds = 0.0;
                string[] lines = raw.Split('\n');
                if (lines.Length == 0) return null;

                string[] hdr = lines[0].Split('\t');
                if (hdr.Length >= 5 && hdr[0] == "H")
                {
                    res.GeneratedAt = hdr[1];
                    res.ReferenceDate = hdr[2];
                    int pmc, tft;
                    int.TryParse(hdr[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out pmc);
                    int.TryParse(hdr[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out tft);
                    res.PricingModelCount = pmc;
                    res.TotalFilesTracked = tft;
                    if (hdr.Length >= 6)
                    {
                        int afc;
                        int.TryParse(hdr[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out afc);
                        res.ArchivedFilesCount = afc;
                    }
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    string[] p = lines[i].Split('\t');
                    if (p.Length >= 12 && p[0] == "B")
                    {
                        RecordBucket b = new RecordBucket();
                        b.Date = p[1];
                        b.Tool = p[2];
                        b.Model = p[3];
                        int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Turns);
                        long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.FreshInput);
                        long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.CacheRead);
                        long.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.CacheWrite);
                        long.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Output);
                        double.TryParse(p[9], NumberStyles.Any, CultureInfo.InvariantCulture, out b.CostUsd);
                        double.TryParse(p[10], NumberStyles.Any, CultureInfo.InvariantCulture, out b.BaselineUsd);
                        b.RateLabel = p[11];
                        res.Records.Add(b);
                    }
                    else if (p.Length >= 12 && p[0] == "R")
                    {
                        RecentRequestRecord rq = new RecentRequestRecord();
                        long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rq.TimestampMs);
                        rq.Date = p[2];
                        rq.TimeLabel = p[3];
                        rq.Tool = p[4];
                        rq.Model = p[5];
                        long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out rq.FreshInput);
                        long.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out rq.CacheRead);
                        long.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out rq.CacheWrite);
                        long.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out rq.Output);
                        int.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out rq.DurationMs);
                        double.TryParse(p[11], NumberStyles.Any, CultureInfo.InvariantCulture, out rq.CostUsd);
                        res.RecentRequests.Add(rq);
                    }
                }
                return res;
            }
            catch
            {
                return null;
            }
        }
    }

    // Lightweight Zero-Allocation JSON Field & Span Extractor
    internal static class FastJson
    {
        private static int FindKeyColon(string s, string quotedKey, int startIdx, int endIdx)
        {
            int limit = endIdx < 0 || endIdx > s.Length ? s.Length : endIdx;
            int searchPos = startIdx < 0 ? 0 : startIdx;
            while (searchPos < limit)
            {
                int count = limit - searchPos;
                if (count < quotedKey.Length) return -1;
                int idx = s.IndexOf(quotedKey, searchPos, count, StringComparison.Ordinal);
                if (idx < 0) return -1;
                int afterKey = idx + quotedKey.Length;
                while (afterKey < limit && (s[afterKey] == ' ' || s[afterKey] == '\t' || s[afterKey] == '\r' || s[afterKey] == '\n'))
                    afterKey++;
                if (afterKey < limit && s[afterKey] == ':')
                    return afterKey + 1;
                searchPos = idx + quotedKey.Length;
            }
            return -1;
        }

        public static bool FindObjectBounds(string s, string quotedKey, int startIdx, int endIdx, out int objStart, out int objEnd)
        {
            objStart = -1;
            objEnd = -1;
            int limit = endIdx < 0 || endIdx > s.Length ? s.Length : endIdx;
            int searchFrom = startIdx;

            while (searchFrom < limit)
            {
                int valPos = FindKeyColon(s, quotedKey, searchFrom, limit);
                if (valPos < 0) return false;

                while (valPos < limit && (s[valPos] == ' ' || s[valPos] == '\t' || s[valPos] == '\r' || s[valPos] == '\n'))
                    valPos++;

                if (valPos >= limit) return false;
                if (s[valPos] != '{')
                {
                    searchFrom = valPos + 1;
                    continue;
                }

                objStart = valPos;
                int depth = 0;
                bool inStr = false;
                for (int i = valPos; i < limit; i++)
                {
                    char c = s[i];
                    if (inStr)
                    {
                        if (c == '\\') { i++; continue; }
                        if (c == '"') inStr = false;
                    }
                    else
                    {
                        if (c == '"') inStr = true;
                        else if (c == '{') depth++;
                        else if (c == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                objEnd = i + 1;
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
            return false;
        }

        public static string ExtractString(string s, string quotedKey, int startIdx, int endIdx)
        {
            int limit = endIdx < 0 || endIdx > s.Length ? s.Length : endIdx;
            int searchFrom = startIdx;

            while (searchFrom < limit)
            {
                int valPos = FindKeyColon(s, quotedKey, searchFrom, limit);
                if (valPos < 0) return null;

                while (valPos < limit && (s[valPos] == ' ' || s[valPos] == '\t' || s[valPos] == '\r' || s[valPos] == '\n'))
                    valPos++;

                if (valPos >= limit) return null;
                if (s[valPos] != '"')
                {
                    searchFrom = valPos + 1;
                    continue;
                }

                int strStart = valPos + 1;
                bool hasEscape = false;
                for (int i = strStart; i < limit; i++)
                {
                    char c = s[i];
                    if (c == '\\')
                    {
                        hasEscape = true;
                        i++;
                        continue;
                    }
                    if (c == '"')
                    {
                        if (!hasEscape)
                            return s.Substring(strStart, i - strStart);
                        return UnescapeJsonString(s, strStart, i);
                    }
                }
                return null;
            }
            return null;
        }

        private static string UnescapeJsonString(string s, int start, int end)
        {
            StringBuilder sb = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end)
                {
                    char nx = s[++i];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < end)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(nx); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public static long ExtractLong(string s, string quotedKey, int startIdx, int endIdx)
        {
            int limit = endIdx < 0 || endIdx > s.Length ? s.Length : endIdx;
            int searchFrom = startIdx;

            while (searchFrom < limit)
            {
                int valPos = FindKeyColon(s, quotedKey, searchFrom, limit);
                if (valPos < 0) return 0L;

                while (valPos < limit && (s[valPos] == ' ' || s[valPos] == '\t' || s[valPos] == '\r' || s[valPos] == '\n'))
                    valPos++;
                if (valPos >= limit) return 0L;

                if (s[valPos] == '"') valPos++;
                if (valPos >= limit || (!char.IsDigit(s[valPos]) && s[valPos] != '-'))
                {
                    searchFrom = valPos + 1;
                    continue;
                }

                int numStart = valPos;
                bool isFloat = false;
                if (s[valPos] == '-') valPos++;
                while (valPos < limit)
                {
                    char c = s[valPos];
                    if (c >= '0' && c <= '9') { valPos++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+') { isFloat = true; valPos++; continue; }
                    break;
                }

                string numStr = s.Substring(numStart, valPos - numStart);
                if (!isFloat)
                {
                    long lv;
                    if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                        return lv;
                }
                double dv;
                if (double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                    return (long)dv;
                return 0L;
            }
            return 0L;
        }

        public static double ExtractDouble(string s, string quotedKey, int startIdx, int endIdx)
        {
            int limit = endIdx < 0 || endIdx > s.Length ? s.Length : endIdx;
            int searchFrom = startIdx;

            while (searchFrom < limit)
            {
                int valPos = FindKeyColon(s, quotedKey, searchFrom, limit);
                if (valPos < 0) return 0.0;

                while (valPos < limit && (s[valPos] == ' ' || s[valPos] == '\t' || s[valPos] == '\r' || s[valPos] == '\n'))
                    valPos++;
                if (valPos >= limit) return 0.0;

                if (s[valPos] == '"') valPos++;
                if (valPos >= limit || (!char.IsDigit(s[valPos]) && s[valPos] != '-'))
                {
                    searchFrom = valPos + 1;
                    continue;
                }

                int numStart = valPos;
                if (s[valPos] == '-') valPos++;
                while (valPos < limit)
                {
                    char c = s[valPos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        valPos++;
                        continue;
                    }
                    break;
                }
                double dv;
                if (double.TryParse(s.Substring(numStart, valPos - numStart), NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                    return dv;
                return 0.0;
            }
            return 0.0;
        }

        public static string ExtractRawScalar(string s, string quotedKey, int startIdx, int endIdx)
        {
            int limit = endIdx < 0 || endIdx > s.Length ? s.Length : endIdx;
            int valPos = FindKeyColon(s, quotedKey, startIdx, limit);
            if (valPos < 0) return null;
            while (valPos < limit && (s[valPos] == ' ' || s[valPos] == '\t' || s[valPos] == '\r' || s[valPos] == '\n'))
                valPos++;
            if (valPos >= limit) return null;
            if (s[valPos] == '"')
                return ExtractString(s, quotedKey, startIdx, limit);
            if (char.IsDigit(s[valPos]))
            {
                int st = valPos;
                while (valPos < limit && (char.IsDigit(s[valPos]) || s[valPos] == '.'))
                    valPos++;
                return s.Substring(st, valPos - st);
            }
            return null;
        }
    }

    // Byte-Exact Incremental JSONL Reader with Raw UTF-8 Keyword Pre-Filtering
    internal static class Utf8LineScanner
    {
        public static readonly byte[] KwInputTokens = Encoding.ASCII.GetBytes("\"input_tokens\"");
        public static readonly byte[] KwEntrypoint = Encoding.ASCII.GetBytes("\"entrypoint\"");
        public static readonly byte[] KwApiRequest = Encoding.ASCII.GetBytes("\"api-request\"");
        public static readonly byte[] KwTypeUser = Encoding.ASCII.GetBytes("\"user\"");
        public static readonly byte[] KwTurnContext = Encoding.ASCII.GetBytes("turn_context");
        public static readonly byte[] KwTokenCount = Encoding.ASCII.GetBytes("token_count");
        public static readonly byte[] KwSessionMeta = Encoding.ASCII.GetBytes("session_meta");
        public static readonly byte[] KwCallOutput = Encoding.ASCII.GetBytes("_call_output");
        public static readonly byte[] KwUserMessage = Encoding.ASCII.GetBytes("user_message");
        public static readonly byte[] KwTaskStarted = Encoding.ASCII.GetBytes("task_started");
        public static readonly byte[] KwModelSelection = Encoding.ASCII.GetBytes("Model Selection");
        public static readonly byte[] KwCreatedAt = Encoding.ASCII.GetBytes("\"created_at\"");
        public static readonly byte[] KwUsageRecord = Encoding.ASCII.GetBytes("\"usage.record\"");
        public static readonly byte[] KwLlmRequest = Encoding.ASCII.GetBytes("\"llm.request\"");

        private static bool ContainsAsciiPattern(byte[] hay, int hayLen, byte[] needle)
        {
            int nLen = needle.Length;
            if (hayLen < nLen) return false;
            byte first = needle[0];
            int maxIdx = hayLen - nLen;
            for (int i = 0; i <= maxIdx; i++)
            {
                if (hay[i] != first) continue;
                int j = 1;
                while (j < nLen && hay[i + j] == needle[j]) j++;
                if (j == nLen) return true;
            }
            return false;
        }

        private static bool MatchesAnyKeyword(byte[] lineBuf, int len, byte[][] keywords)
        {
            if (keywords == null || keywords.Length == 0) return true;
            for (int k = 0; k < keywords.Length; k++)
            {
                if (ContainsAsciiPattern(lineBuf, len, keywords[k]))
                    return true;
            }
            return false;
        }

        public static void ScanFromOffset(
            string openPath,
            long startOffset,
            int initialLineIndex,
            byte[][] anyKeywords,
            Action<string, int> onLine,
            out long validEndOffset,
            out int finalLineIndex)
        {
            validEndOffset = startOffset;
            finalLineIndex = initialLineIndex;

            using (FileStream fs = new FileStream(openPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
            {
                long fileLen = fs.Length;
                if (startOffset < 0 || startOffset > fileLen)
                {
                    startOffset = 0;
                    validEndOffset = 0;
                    finalLineIndex = 0;
                }
                if (startOffset == fileLen)
                    return;

                if (startOffset > 0)
                    fs.Seek(startOffset, SeekOrigin.Begin);

                byte[] readBuf = new byte[65536];
                byte[] lineBuf = new byte[65536];
                int lineLen = 0;
                long chunkStartPos = startOffset;

                int bytesRead;
                while ((bytesRead = fs.Read(readBuf, 0, readBuf.Length)) > 0)
                {
                    int segStart = 0;
                    for (int i = 0; i < bytesRead; i++)
                    {
                        if (readBuf[i] == (byte)'\n')
                        {
                            int segLen = i - segStart;
                            if (segLen > 0)
                            {
                                if (lineLen + segLen > lineBuf.Length)
                                {
                                    byte[] bigger = new byte[Math.Max(lineBuf.Length * 2, lineLen + segLen)];
                                    if (lineLen > 0) Buffer.BlockCopy(lineBuf, 0, bigger, 0, lineLen);
                                    lineBuf = bigger;
                                }
                                Buffer.BlockCopy(readBuf, segStart, lineBuf, lineLen, segLen);
                                lineLen += segLen;
                            }

                            int cleanLen = lineLen;
                            if (cleanLen > 0 && lineBuf[cleanLen - 1] == (byte)'\r')
                                cleanLen--;

                            finalLineIndex++;
                            validEndOffset = chunkStartPos + i + 1;

                            if (cleanLen > 2 && MatchesAnyKeyword(lineBuf, cleanLen, anyKeywords))
                            {
                                string decoded = Encoding.UTF8.GetString(lineBuf, 0, cleanLen);
                                onLine(decoded, finalLineIndex);
                            }

                            lineLen = 0;
                            segStart = i + 1;
                        }
                    }

                    int tailLen = bytesRead - segStart;
                    if (tailLen > 0)
                    {
                        if (lineLen + tailLen > lineBuf.Length)
                        {
                            byte[] bigger = new byte[Math.Max(lineBuf.Length * 2, lineLen + tailLen)];
                            if (lineLen > 0) Buffer.BlockCopy(lineBuf, 0, bigger, 0, lineLen);
                            lineBuf = bigger;
                        }
                        Buffer.BlockCopy(readBuf, segStart, lineBuf, lineLen, tailLen);
                        lineLen += tailLen;
                    }

                    chunkStartPos += bytesRead;
                }

                // Handle closed JSONL files whose final JSON object lacks a trailing newline
                if (lineLen > 2)
                {
                    int trimEnd = lineLen;
                    while (trimEnd > 0 && (lineBuf[trimEnd - 1] == (byte)' ' || lineBuf[trimEnd - 1] == (byte)'\r' || lineBuf[trimEnd - 1] == (byte)'\t'))
                        trimEnd--;

                    if (trimEnd > 2 && lineBuf[trimEnd - 1] == (byte)'}')
                    {
                        finalLineIndex++;
                        validEndOffset = chunkStartPos;
                        if (MatchesAnyKeyword(lineBuf, trimEnd, anyKeywords))
                        {
                            string decoded = Encoding.UTF8.GetString(lineBuf, 0, trimEnd);
                            onLine(decoded, finalLineIndex);
                        }
                    }
                }
            }
        }
    }

    // 100% Native Multi-Harness Incremental Scanner (Zero External Software Dependency)
    public sealed class TokenScanner
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetFileAttributesExW(string lpFileName, int fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA fileData);

        private readonly string _home;
        private readonly bool _scanWsl;
        private readonly PricingEngine _pricing;
        private readonly List<UsageEvent> _events = new List<UsageEvent>();
        private readonly HashSet<string> _codexLiveThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claudeSeenKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _openCodeSeenKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _mobileClawSeenKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<UsageEvent> _codexArchiveCandidates = new List<UsageEvent>();
        private readonly List<string> _warnings = new List<string>();

        private Dictionary<string, FileCursorEntry> _cursors;
        private readonly List<FileCursorEntry> _dirtyCursors = new List<FileCursorEntry>();
        private int _totalTrackedFiles = 0;
        private int _archivedFilesCount = 0;

        private static readonly Regex UuidRegex = new Regex(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AgyModelRegex = new Regex(@"<USER_SETTINGS_CHANGE>[\s\S]*?`Model Selection`\s+from\s+.+?\s+to\s+([^\r\n.]+)", RegexOptions.Compiled);

        private static readonly byte[][] ClaudeKeywords = new byte[][] { Utf8LineScanner.KwInputTokens, Utf8LineScanner.KwEntrypoint, Utf8LineScanner.KwApiRequest, Utf8LineScanner.KwTypeUser };
        private static readonly byte[][] CodexKeywords = new byte[][] { Utf8LineScanner.KwTurnContext, Utf8LineScanner.KwTokenCount, Utf8LineScanner.KwSessionMeta, Utf8LineScanner.KwCallOutput, Utf8LineScanner.KwUserMessage, Utf8LineScanner.KwTaskStarted };
        private static readonly byte[][] AgyKeywords = new byte[][] { Utf8LineScanner.KwInputTokens, Utf8LineScanner.KwModelSelection, Utf8LineScanner.KwCreatedAt };
        private static readonly byte[][] KimiKeywords = new byte[][] { Utf8LineScanner.KwUsageRecord, Utf8LineScanner.KwLlmRequest };

        public TokenScanner(string homeDir) : this(homeDir, false)
        {
        }

        public TokenScanner(string homeDir, bool scanLocalWsl)
        {
            _home = homeDir;
            _scanWsl = scanLocalWsl;
            _pricing = new PricingEngine(homeDir);
        }

        internal static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.Length >= 240 && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                if (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                    return @"\\?\" + path.Replace('/', '\\');
            }
            return path;
        }

        private static bool TryGetFileStat(string path, out long size, out long mtimeTicks)
        {
            WIN32_FILE_ATTRIBUTE_DATA data;
            if (GetFileAttributesExW(ToLongPath(path), 0, out data))
            {
                size = ((long)data.nFileSizeHigh << 32) | (long)(ulong)data.nFileSizeLow;
                mtimeTicks = ((long)data.ftLastWriteTimeHigh << 32) | (long)(ulong)data.ftLastWriteTimeLow;
                return true;
            }
            size = 0L;
            mtimeTicks = 0L;
            return false;
        }

        private static bool TryGetSqliteCombinedStat(string dbPath, out long combinedSize, out long maxMtimeTicks)
        {
            long s0, m0;
            if (!TryGetFileStat(dbPath, out s0, out m0))
            {
                combinedSize = 0L;
                maxMtimeTicks = 0L;
                return false;
            }
            combinedSize = s0;
            maxMtimeTicks = m0;
            long sw, mw;
            if (TryGetFileStat(dbPath + "-wal", out sw, out mw))
            {
                combinedSize += sw;
                if (mw > maxMtimeTicks) maxMtimeTicks = mw;
            }
            return true;
        }

        private static DateTime FileTimeTicksToLocal(long fileTimeTicks)
        {
            if (fileTimeTicks > 0)
            {
                try { return DateTime.FromFileTime(fileTimeTicks); }
                catch { }
            }
            return DateTime.Now;
        }

        private static JavaScriptSerializer NewJson()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;
            return js;
        }

        public ScanResult Run()
        {
            Stopwatch sw = Stopwatch.StartNew();
            _cursors = LocalUsageStore.LoadAllCursors();

            CheckDirAccess();
            ScanClaude();
            ScanCodexLive();
            ScanAntigravity();
            ScanGeminiCli();
            ScanZCode();
            ScanKimiCode();
            ScanMobileClaw();
            ScanOpenCode();
            ScanWorkBuddyAndCodeBuddy();
            ScanDsh();
            ScanCommandCode();
            ScanVsCodeClineFamily();
            ScanOpenClaw(_home);
            ScanPiAndOmp(_home);
            ScanQwenCode(_home);
            ScanFactoryDroid(_home);
            ScanAmp(_home);
            ScanGoose(_home);
            ScanCrush(_home);
            ScanAider(_home);
            ScanContinue(_home);
            ScanVibeAndAgents(_home);
            ScanCursorNative();
            ScanZed();
            ScanCustomRules(_home);
            ScanCodexSqliteArchive();

            if (_scanWsl)
            {
                ScanLocalWslHomes();
            }

            RecoverDeletedFileCursors();

            sw.Stop();
            ScanResult result = BuildResult(sw.Elapsed.TotalSeconds);
            LocalUsageStore.SaveScanBatch(_home, _dirtyCursors, result);
            return result;
        }

        private void TrackVisitedFile(string fileKey)
        {
            _totalTrackedFiles++;
            if (!string.IsNullOrEmpty(fileKey))
                _visitedFiles.Add(fileKey);
        }

        private void SaveDirtyCursor(string key, long size, long mtime, long offset, string meta, List<UsageEvent> events)
        {
            if (!string.IsNullOrEmpty(key))
                _visitedFiles.Add(key);
            FileCursorEntry c = new FileCursorEntry();
            c.FileKey = key;
            c.FileSize = size;
            c.MtimeTicks = mtime;
            c.ByteOffset = offset;
            c.ExtraMeta = meta ?? "";
            c.Events = events ?? new List<UsageEvent>();
            _cursors[key] = c;
            _dirtyCursors.Add(c);
        }

        private static List<UsageEvent> MergeSqliteCursorEvents(FileCursorEntry cur, List<UsageEvent> freshEvents)
        {
            if (cur == null || cur.Events == null || cur.Events.Count == 0)
                return freshEvents ?? new List<UsageEvent>();
            if (freshEvents == null || freshEvents.Count == 0)
                return new List<UsageEvent>(cur.Events);

            Dictionary<string, UsageEvent> byKey = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
            for (int i = 0; i < cur.Events.Count; i++)
            {
                UsageEvent oldEv = cur.Events[i];
                if (oldEv != null && !string.IsNullOrEmpty(oldEv.DedupKey))
                    byKey[oldEv.DedupKey] = oldEv;
            }

            List<UsageEvent> noKeyFresh = new List<UsageEvent>();
            for (int i = 0; i < freshEvents.Count; i++)
            {
                UsageEvent ev = freshEvents[i];
                if (ev == null) continue;
                if (!string.IsNullOrEmpty(ev.DedupKey))
                {
                    UsageEvent prev;
                    if (!byKey.TryGetValue(ev.DedupKey, out prev) ||
                        (ev.FreshInput + ev.Output + ev.CacheRead + ev.CacheWrite) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                    {
                        byKey[ev.DedupKey] = ev;
                    }
                }
                else
                {
                    noKeyFresh.Add(ev);
                }
            }

            List<UsageEvent> merged = new List<UsageEvent>(byKey.Count + noKeyFresh.Count);
            foreach (UsageEvent kvEv in byKey.Values)
                merged.Add(kvEv);
            for (int i = 0; i < noKeyFresh.Count; i++)
                merged.Add(noKeyFresh[i]);
            return merged;
        }

        private bool BelongsToCurrentHome(string fileKey)
        {
            if (string.IsNullOrEmpty(fileKey)) return false;
            const string wslPrefix = @"\\wsl.localhost";
            if (_scanWsl && fileKey.StartsWith(wslPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (fileKey.Length == wslPrefix.Length || fileKey[wslPrefix.Length] == '\\' || fileKey[wslPrefix.Length] == '/')
                    return true;
            }
            if (string.IsNullOrEmpty(_home)) return false;
            string normHome = _home.TrimEnd('\\', '/');
            if (normHome.Length == 0) return false;
            if (!fileKey.StartsWith(normHome, StringComparison.OrdinalIgnoreCase))
                return false;
            if (fileKey.Length == normHome.Length)
                return true;
            char sep = fileKey[normHome.Length];
            return sep == '\\' || sep == '/';
        }

        private void RecoverDeletedFileCursors()
        {
            if (_cursors != null)
            {
                foreach (KeyValuePair<string, FileCursorEntry> kv in _cursors)
                {
                    string key = kv.Key;
                    FileCursorEntry cur = kv.Value;
                    if (string.IsNullOrEmpty(key) || _visitedFiles.Contains(key))
                        continue;
                    if (cur == null || cur.Events == null || cur.Events.Count == 0)
                        continue;
                    if (!BelongsToCurrentHome(key))
                        continue;

                    long dummySz, dummyMt;
                    if (TryGetFileStat(key, out dummySz, out dummyMt))
                        continue;

                    _archivedFilesCount++;

                    if (key.EndsWith("state_5.sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        _codexArchiveCandidates.AddRange(cur.Events);
                        continue;
                    }

                    if (key.IndexOf(".codex", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string tid = ExtractMetaField(cur.ExtraMeta, "tid", "");
                        if (string.IsNullOrEmpty(tid))
                        {
                            Match mu = UuidRegex.Match(Path.GetFileName(key));
                            if (mu.Success) tid = mu.Groups[1].Value;
                        }
                        if (!string.IsNullOrEmpty(tid))
                            _codexLiveThreads.Add(tid);
                    }

                    for (int i = 0; i < cur.Events.Count; i++)
                    {
                        UsageEvent ev = cur.Events[i];
                        if (ev == null) continue;
                        if (!string.IsNullOrEmpty(ev.DedupKey))
                        {
                            if (string.Equals(ev.Tool, "Claude Desktop", StringComparison.Ordinal) ||
                                string.Equals(ev.Tool, "Claude Code", StringComparison.Ordinal))
                            {
                                if (!_claudeSeenKeys.Add(ev.DedupKey))
                                    continue;
                            }
                            else if (string.Equals(ev.Tool, "OpenCode", StringComparison.Ordinal))
                            {
                                if (!_openCodeSeenKeys.Add(ev.DedupKey))
                                    continue;
                            }
                            else if (string.Equals(ev.Tool, "MobileClaw", StringComparison.Ordinal))
                            {
                                if (!_mobileClawSeenKeys.Add(ev.DedupKey))
                                    continue;
                            }
                        }
                        _events.Add(ev);
                    }
                }
            }

            for (int i = 0; i < _codexArchiveCandidates.Count; i++)
            {
                UsageEvent ev = _codexArchiveCandidates[i];
                if (ev == null) continue;
                if (!string.IsNullOrEmpty(ev.DedupKey) && _codexLiveThreads.Contains(ev.DedupKey))
                    continue;
                _events.Add(ev);
            }
        }

        private void CheckDirAccess()
        {
            string[] check = new string[] { ".claude", ".codex", ".zcode", ".kimi-code", ".workbuddy", ".dsh" };
            for (int i = 0; i < check.Length; i++)
            {
                string p = Path.Combine(_home, check[i]);
                try
                {
                    Directory.GetFileSystemEntries(p);
                }
                catch (UnauthorizedAccessException)
                {
                    _warnings.Add(p);
                }
                catch
                {
                }
            }
        }

        private static List<string> SafeFindFiles(string rootDir, string pattern, bool recursive)
        {
            List<string> results = new List<string>();
            string lpRoot = ToLongPath(rootDir);
            try
            {
                if (!Directory.Exists(lpRoot)) return results;
            }
            catch
            {
                return results;
            }

            Stack<string> dirs = new Stack<string>();
            dirs.Push(lpRoot);
            while (dirs.Count > 0)
            {
                string current = dirs.Pop();
                try
                {
                    string[] files = Directory.GetFiles(current, pattern);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string f = files[i];
                        if (f.StartsWith(@"\\?\", StringComparison.Ordinal) && !f.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                            f = f.Substring(4);
                        results.Add(f);
                    }
                }
                catch { }

                if (recursive)
                {
                    try
                    {
                        string[] subDirs = Directory.GetDirectories(current);
                        for (int i = 0; i < subDirs.Length; i++)
                        {
                            string sd = subDirs[i];
                            int lastSlash = sd.LastIndexOf('\\');
                            string name = lastSlash >= 0 ? sd.Substring(lastSlash + 1) : sd;
                            if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Cache", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Code Cache", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                                continue;
                            dirs.Push(sd);
                        }
                    }
                    catch { }
                }
            }
            return results;
        }

        private static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0L;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0L;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is decimal) return (long)(decimal)v;
            long parsed;
            if (long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return 0L;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0.0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0.0;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is decimal) return (double)(decimal)v;
            double parsed;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return 0.0;
        }

        private static string ToLocalDate(object val, DateTime fallbackLocal)
        {
            if (val != null)
            {
                if (val is int || val is long || val is double || val is decimal)
                {
                    double num = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                    if (num > 0)
                    {
                        double sec = num > 1e11 ? num / 1000.0 : num;
                        try
                        {
                            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                            return epoch.AddSeconds(sec).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        }
                        catch { }
                    }
                }
                else
                {
                    string s = Convert.ToString(val, CultureInfo.InvariantCulture).Trim();
                    long digits;
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out digits) && digits > 0)
                    {
                        return ToLocalDate(digits, fallbackLocal);
                    }
                    DateTime dt;
                    if (DateTimeOffsetAsLocal(s, out dt))
                    {
                        return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    if (s.Length >= 10 && s[4] == '-' && s[7] == '-')
                    {
                        return s.Substring(0, 10);
                    }
                }
            }
            return fallbackLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static long ToUnixEpochMs(object val, DateTime fallbackLocal)
        {
            if (val != null)
            {
                if (val is int || val is long || val is double || val is decimal)
                {
                    double num = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                    if (num > 0)
                    {
                        if (num > 1e11) return (long)num;
                        if (num > 1e9) return (long)(num * 1000.0);
                    }
                }
                else
                {
                    string s = Convert.ToString(val, CultureInfo.InvariantCulture).Trim();
                    double numStr;
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out numStr) && numStr > 1e9)
                    {
                        return numStr > 1e11 ? (long)numStr : (long)(numStr * 1000.0);
                    }
                    DateTimeOffset dto;
                    if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                    {
                        return (long)(dto.UtcDateTime - UnixEpochUtc).TotalMilliseconds;
                    }
                }
            }
            try
            {
                return (long)(fallbackLocal.ToUniversalTime() - UnixEpochUtc).TotalMilliseconds;
            }
            catch
            {
                return 0L;
            }
        }

        private static int ComputeSafeDurationMs(long startMs, long endMs)
        {
            if (startMs <= 0L || endMs <= startMs) return 0;
            long delta = endMs - startMs;
            if (delta < 120L || delta > 600000L) return 0;
            return (int)delta;
        }

        internal static string FormatEpochTimeLabel(long epochMs)
        {
            if (epochMs <= 0L) return "";
            try
            {
                DateTime localDt = UnixEpochUtc.AddMilliseconds(epochMs).ToLocalTime();
                return localDt.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }

        private static bool DateTimeOffsetAsLocal(string s, out DateTime localDt)
        {
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
            {
                localDt = dto.LocalDateTime;
                return true;
            }
            localDt = DateTime.MinValue;
            return false;
        }

        private static DateTime SafeLastWriteTime(string path)
        {
            long sz, mt;
            if (TryGetFileStat(path, out sz, out mt))
                return FileTimeTicksToLocal(mt);
            return DateTime.Now;
        }

        private static string ReadAllTextLong(string path)
        {
            using (FileStream fs = new FileStream(ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        // -------------------- 1. Claude Desktop & Claude Code (Entrypoint-Aware + Seek Cursor) --------------------
        private sealed class ClaudeFileItem
        {
            public string FilePath;
            public long FileSize;
            public long MtimeTicks;
            public long ValidEnd;
            public string MetaStr;
            public bool IsDirty;
            public string ProjectGroup;
            public string EntrypointKind; // "desktop", "cli", or "unknown"
            public List<UsageEvent> Events;
        }

        private static string GetClaudeProjectGroup(string projectsRoot, string filePath)
        {
            if (string.IsNullOrEmpty(projectsRoot) || !filePath.StartsWith(projectsRoot, StringComparison.OrdinalIgnoreCase))
                return "";
            string rel = filePath.Substring(projectsRoot.Length).TrimStart('\\', '/');
            int slash = rel.IndexOfAny(new char[] { '\\', '/' });
            return slash > 0 ? rel.Substring(0, slash) : rel;
        }

        private void ScanClaude()
        {
            string projectsRoot = Path.Combine(_home, ".claude", "projects");
            string transcriptsRoot = Path.Combine(_home, ".claude", "transcripts");
            string[] roots = new string[] { projectsRoot, transcriptsRoot };

            List<ClaudeFileItem> parsedItems = new List<ClaudeFileItem>();
            HashSet<string> desktopProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int r = 0; r < roots.Length; r++)
            {
                List<string> files = SafeFindFiles(roots[r], "*.jsonl", true);
                for (int i = 0; i < files.Count; i++)
                {
                    string fp = files[i];
                    long fSize, fMtime;
                    if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                    TrackVisitedFile(fp);

                    string projGroup = GetClaudeProjectGroup(projectsRoot, fp);
                    bool pathIsDesktop = fp.IndexOf("Claude-Data", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         fp.IndexOf("claude-pzs8sxrjxfjjc", StringComparison.OrdinalIgnoreCase) >= 0;

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        string cachedEp = ExtractMetaField(cur.ExtraMeta, "ep", pathIsDesktop ? "desktop" : "unknown");
                        if (pathIsDesktop) cachedEp = "desktop";
                        if (cachedEp == "unknown" && cur.Events != null)
                        {
                            for (int ce = 0; ce < cur.Events.Count; ce++)
                            {
                                if (cur.Events[ce] != null && string.Equals(cur.Events[ce].Tool, "Claude Desktop", StringComparison.Ordinal))
                                {
                                    cachedEp = "desktop";
                                    break;
                                }
                            }
                        }
                        if (cachedEp == "desktop" && projGroup.Length > 0)
                            desktopProjects.Add(projGroup);

                        ClaudeFileItem item = new ClaudeFileItem();
                        item.FilePath = fp;
                        item.FileSize = cur.FileSize;
                        item.MtimeTicks = cur.MtimeTicks;
                        item.ValidEnd = cur.ByteOffset;
                        item.MetaStr = cur.ExtraMeta ?? "";
                        item.IsDirty = false;
                        item.ProjectGroup = projGroup;
                        item.EntrypointKind = cachedEp;
                        item.Events = cur.Events ?? new List<UsageEvent>();
                        parsedItems.Add(item);
                        continue;
                    }

                    long startOffset = 0L;
                    int lineIdx = 0;
                    string epKind = pathIsDesktop ? "desktop" : "unknown";
                    Dictionary<string, UsageEvent> fileDedup = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);

                    if (cur != null && fSize > cur.ByteOffset && cur.ByteOffset > 0)
                    {
                        startOffset = cur.ByteOffset;
                        epKind = ExtractMetaField(cur.ExtraMeta, "ep", epKind);
                        int.TryParse(ExtractMetaField(cur.ExtraMeta, "ln", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lineIdx);
                        if (cur.Events != null)
                        {
                            for (int e = 0; e < cur.Events.Count; e++)
                            {
                                UsageEvent oldEv = cur.Events[e];
                                string dk = string.IsNullOrEmpty(oldEv.DedupKey) ? ("old:" + e.ToString(CultureInfo.InvariantCulture)) : oldEv.DedupKey;
                                fileDedup[dk] = oldEv;
                            }
                        }
                    }
                    else if (cur != null && cur.Events != null && cur.Events.Count > 0)
                    {
                        epKind = ExtractMetaField(cur.ExtraMeta, "ep", epKind);
                        string posPrefix = fp + ":";
                        for (int e = 0; e < cur.Events.Count; e++)
                        {
                            UsageEvent oldEv = cur.Events[e];
                            if (oldEv != null && !string.IsNullOrEmpty(oldEv.DedupKey) && !oldEv.DedupKey.StartsWith(posPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                fileDedup[oldEv.DedupKey] = oldEv;
                            }
                        }
                    }

                    DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                    long validEnd = startOffset;
                    int endLineIdx = lineIdx;
                    long lastReqStartMs = 0L;
                    long.TryParse(ExtractMetaField(cur != null ? cur.ExtraMeta : "", "lrs", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lastReqStartMs);
                    Dictionary<string, long> reqStartByUid = new Dictionary<string, long>(StringComparer.Ordinal);

                    try
                    {
                        Utf8LineScanner.ScanFromOffset(
                            ToLongPath(fp),
                            startOffset,
                            lineIdx,
                            ClaudeKeywords,
                            delegate(string line, int curLineIdx)
                            {
                                if (line.IndexOf("\"entrypoint\"", StringComparison.Ordinal) >= 0)
                                {
                                    string ep = FastJson.ExtractString(line, "\"entrypoint\"", 0, line.Length);
                                    if (!string.IsNullOrEmpty(ep))
                                    {
                                        if (ep.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0)
                                            epKind = "desktop";
                                        else if (epKind != "desktop" && ep.IndexOf("cli", StringComparison.OrdinalIgnoreCase) >= 0)
                                            epKind = "cli";
                                    }
                                }

                                string lineType = FastJson.ExtractString(line, "\"type\"", 0, Math.Min(line.Length, 220));
                                if (lineType == null) lineType = FastJson.ExtractString(line, "\"type\"", 0, line.Length);
                                if (lineType == "api-request" || lineType == "user")
                                {
                                    string reqTs = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, line.Length);
                                    if (!string.IsNullOrEmpty(reqTs))
                                    {
                                        long rms = ToUnixEpochMs(reqTs, fallbackDt);
                                        if (rms > 0) lastReqStartMs = rms;
                                    }
                                }

                                if (line.IndexOf("\"input_tokens\"", StringComparison.Ordinal) < 0)
                                    return;

                                int msgStart, msgEnd;
                                if (!FastJson.FindObjectBounds(line, "\"message\"", 0, line.Length, out msgStart, out msgEnd))
                                    return;
                                int uStart, uEnd;
                                if (!FastJson.FindObjectBounds(line, "\"usage\"", msgStart, msgEnd, out uStart, out uEnd))
                                    return;

                                string rawModel = FastJson.ExtractString(line, "\"model\"", msgStart, msgEnd);
                                if (string.IsNullOrEmpty(rawModel))
                                    rawModel = FastJson.ExtractString(line, "\"model\"", 0, line.Length);
                                if (rawModel == "<synthetic>") return;
                                string model = PricingEngine.NormalizeModelId(rawModel, "claude-sonnet-4-6");

                                long fi = FastJson.ExtractLong(line, "\"input_tokens\"", uStart, uEnd);
                                long outTok = FastJson.ExtractLong(line, "\"output_tokens\"", uStart, uEnd);
                                long cr = FastJson.ExtractLong(line, "\"cache_read_input_tokens\"", uStart, uEnd);
                                long cw = FastJson.ExtractLong(line, "\"cache_creation_input_tokens\"", uStart, uEnd);
                                if (fi + outTok + cr + cw <= 0) return;

                                string msgId = FastJson.ExtractString(line, "\"id\"", msgStart, msgEnd) ?? "";
                                string reqId = FastJson.ExtractString(line, "\"requestId\"", 0, line.Length) ?? "";
                                string key;
                                if (msgId.Length > 0 && reqId.Length > 0) key = msgId + ":" + reqId;
                                else if (msgId.Length > 0) key = "m:" + msgId;
                                else if (reqId.Length > 0) key = "r:" + reqId;
                                else key = fp + ":" + curLineIdx.ToString(CultureInfo.InvariantCulture);

                                string tsRaw = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, line.Length);
                                string dateStr = ToLocalDate(tsRaw, fallbackDt);
                                long tsMs = ToUnixEpochMs(tsRaw, fallbackDt);
                                long startRefMs;
                                if (!reqStartByUid.TryGetValue(key, out startRefMs))
                                {
                                    startRefMs = lastReqStartMs;
                                    reqStartByUid[key] = startRefMs;
                                }
                                int durMs = ComputeSafeDurationMs(startRefMs, tsMs);
                                if (durMs == 0)
                                {
                                    long explicitDur = FastJson.ExtractLong(line, "\"durationMs\"", 0, line.Length);
                                    if (explicitDur >= 100 && explicitDur <= 600000) durMs = (int)explicitDur;
                                }
                                if (tsMs > 0) lastReqStartMs = tsMs;

                                double cost = FastJson.ExtractDouble(line, "\"costUSD\"", 0, line.Length);

                                UsageEvent prev;
                                if (!fileDedup.TryGetValue(key, out prev) ||
                                    (fi + outTok + cr + cw) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                                {
                                    if (durMs == 0 && prev != null && prev.DurationMs > 0) durMs = prev.DurationMs;
                                    fileDedup[key] = new UsageEvent("Claude Code", model, dateStr, fi, outTok, cr, cw, cost, 1, key, tsMs, durMs);
                                }
                            },
                            out validEnd,
                            out endLineIdx);
                    }
                    catch { }

                    if (pathIsDesktop) epKind = "desktop";
                    if (epKind == "desktop" && projGroup.Length > 0)
                        desktopProjects.Add(projGroup);

                    List<UsageEvent> fileEvents = (fileDedup.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                        ? new List<UsageEvent>(cur.Events)
                        : new List<UsageEvent>(fileDedup.Values);
                    string metaStr = "ep=" + epKind + ";ln=" + endLineIdx.ToString(CultureInfo.InvariantCulture) + ";lrs=" + lastReqStartMs.ToString(CultureInfo.InvariantCulture);

                    ClaudeFileItem fiItem = new ClaudeFileItem();
                    fiItem.FilePath = fp;
                    fiItem.FileSize = fSize;
                    fiItem.MtimeTicks = fMtime;
                    fiItem.ValidEnd = validEnd;
                    fiItem.MetaStr = metaStr;
                    fiItem.IsDirty = true;
                    fiItem.ProjectGroup = projGroup;
                    fiItem.EntrypointKind = epKind;
                    fiItem.Events = fileEvents;
                    parsedItems.Add(fiItem);
                }
            }

            // Apply project-level desktop inheritance, persist accurate Tool into cursors, and deduplicate across files
            Dictionary<string, UsageEvent> globalDedup = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
            for (int i = 0; i < parsedItems.Count; i++)
            {
                ClaudeFileItem item = parsedItems[i];
                bool isDesktop = item.EntrypointKind == "desktop" ||
                                 (item.EntrypointKind != "cli" && item.ProjectGroup.Length > 0 && desktopProjects.Contains(item.ProjectGroup));
                string finalTool = isDesktop ? "Claude Desktop" : "Claude Code";

                bool toolUpdated = false;
                if (isDesktop && item.EntrypointKind == "unknown" && !string.IsNullOrEmpty(item.MetaStr) && item.MetaStr.StartsWith("ep=unknown", StringComparison.Ordinal))
                {
                    item.MetaStr = "ep=desktop" + item.MetaStr.Substring("ep=unknown".Length);
                    toolUpdated = true;
                }

                for (int e = 0; e < item.Events.Count; e++)
                {
                    UsageEvent src = item.Events[e];
                    if (!string.Equals(src.Tool, finalTool, StringComparison.Ordinal))
                    {
                        src.Tool = finalTool;
                        toolUpdated = true;
                    }
                    string dk = string.IsNullOrEmpty(src.DedupKey) ? (finalTool + ":" + i.ToString(CultureInfo.InvariantCulture) + ":" + e.ToString(CultureInfo.InvariantCulture)) : src.DedupKey;
                    UsageEvent prev;
                    if (!globalDedup.TryGetValue(dk, out prev) ||
                        (src.FreshInput + src.Output + src.CacheRead + src.CacheWrite) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                    {
                        int finalDur = src.DurationMs > 0 ? src.DurationMs : (prev != null ? prev.DurationMs : 0);
                        long finalTs = src.TimestampMs > 0 ? src.TimestampMs : (prev != null ? prev.TimestampMs : 0L);
                        globalDedup[dk] = new UsageEvent(finalTool, src.Model, src.Date, src.FreshInput, src.Output, src.CacheRead, src.CacheWrite, src.LoggedCostUsd, src.Turns, dk, finalTs, finalDur);
                    }
                }

                if (item.IsDirty || toolUpdated)
                {
                    SaveDirtyCursor(item.FilePath, item.FileSize, item.MtimeTicks, item.ValidEnd, item.MetaStr, item.Events);
                }
            }

            foreach (KeyValuePair<string, UsageEvent> kv in globalDedup)
            {
                _claudeSeenKeys.Add(kv.Key);
                _events.Add(kv.Value);
            }
        }

        private static string ExtractMetaField(string extraMeta, string field, string fallback)
        {
            if (string.IsNullOrEmpty(extraMeta)) return fallback;
            string prefix = field + "=";
            string[] parts = extraMeta.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith(prefix, StringComparison.Ordinal))
                    return parts[i].Substring(prefix.Length);
            }
            return fallback;
        }

        // -------------------- 2. OpenAI Codex (Live JSONL with Byte-Offset Seek Cursor) --------------------
        private void ScanCodexLive()
        {
            string codexDir = Path.Combine(_home, ".codex");
            string[] subs = new string[] { "sessions", "archived_sessions" };

            for (int s = 0; s < subs.Length; s++)
            {
                List<string> files = SafeFindFiles(Path.Combine(codexDir, subs[s]), "*.jsonl", true);
                for (int i = 0; i < files.Count; i++)
                {
                    string fp = files[i];
                    long fSize, fMtime;
                    if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                    TrackVisitedFile(fp);

                    Match mu = UuidRegex.Match(Path.GetFileName(fp));
                    string fileTid = mu.Success ? mu.Groups[1].Value : "";
                    if (fileTid.Length > 0) _codexLiveThreads.Add(fileTid);

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        string cachedTid = ExtractMetaField(cur.ExtraMeta, "tid", "");
                        if (cachedTid.Length > 0) _codexLiveThreads.Add(cachedTid);
                        _events.AddRange(cur.Events);
                        continue;
                    }

                    long startOffset = 0L;
                    int lineIdx = 0;
                    string curModel = "gpt-5.2-codex";
                    long prevTotIn = -1, prevTotOut = -1, prevTotCr = -1;
                    string metaTid = fileTid;
                    List<UsageEvent> fileEvents = new List<UsageEvent>();

                    if (cur != null && fSize > cur.ByteOffset && cur.ByteOffset > 0)
                    {
                        startOffset = cur.ByteOffset;
                        curModel = ExtractMetaField(cur.ExtraMeta, "mdl", curModel);
                        metaTid = ExtractMetaField(cur.ExtraMeta, "tid", metaTid);
                        long.TryParse(ExtractMetaField(cur.ExtraMeta, "pi", "-1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out prevTotIn);
                        long.TryParse(ExtractMetaField(cur.ExtraMeta, "po", "-1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out prevTotOut);
                        long.TryParse(ExtractMetaField(cur.ExtraMeta, "pc", "-1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out prevTotCr);
                        fileEvents.AddRange(cur.Events);
                    }

                    DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                    long validEnd = startOffset;
                    int endIdx = lineIdx;
                    long turnStartMs = 0L;
                    long.TryParse(ExtractMetaField(cur != null ? cur.ExtraMeta : "", "tsm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out turnStartMs);

                    try
                    {
                        Utf8LineScanner.ScanFromOffset(
                            ToLongPath(fp),
                            startOffset,
                            lineIdx,
                            CodexKeywords,
                            delegate(string line, int curIdx)
                            {
                                string typ = FastJson.ExtractString(line, "\"type\"", 0, Math.Min(line.Length, 180));
                                if (typ == null) typ = FastJson.ExtractString(line, "\"type\"", 0, line.Length);
                                if (typ == null) return;

                                int plStart, plEnd;
                                if (!FastJson.FindObjectBounds(line, "\"payload\"", 0, line.Length, out plStart, out plEnd))
                                    return;

                                if (typ == "session_meta")
                                {
                                    string tid = FastJson.ExtractString(line, "\"id\"", plStart, plEnd);
                                    if (!string.IsNullOrEmpty(tid))
                                    {
                                        metaTid = tid;
                                        _codexLiveThreads.Add(tid);
                                    }
                                }
                                else if (typ == "turn_context")
                                {
                                    string mdl = FastJson.ExtractString(line, "\"model\"", plStart, plEnd);
                                    if (!string.IsNullOrEmpty(mdl))
                                        curModel = PricingEngine.NormalizeModelId(mdl, curModel);
                                    string tcTs = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, Math.Min(line.Length, 160));
                                    long tcms = ToUnixEpochMs(tcTs, fallbackDt);
                                    if (tcms > 0) turnStartMs = tcms;
                                }
                                else if (typ == "response_item")
                                {
                                    string rType = FastJson.ExtractString(line, "\"type\"", plStart, Math.Min(plEnd, plStart + 180));
                                    if (rType != null && rType.IndexOf("_call_output", StringComparison.Ordinal) >= 0)
                                    {
                                        string riTs = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, Math.Min(line.Length, 160));
                                        long rims = ToUnixEpochMs(riTs, fallbackDt);
                                        if (rims > 0) turnStartMs = rims;
                                    }
                                }
                                else if (typ == "event_msg")
                                {
                                    string pType = FastJson.ExtractString(line, "\"type\"", plStart, Math.Min(plEnd, plStart + 160));
                                    if (pType == "user_message" || pType == "task_started")
                                    {
                                        string stTs = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, Math.Min(line.Length, 160));
                                        long stms = ToUnixEpochMs(stTs, fallbackDt);
                                        if (stms > 0) turnStartMs = stms;
                                        return;
                                    }
                                    if (pType != "token_count") return;

                                    int infoStart, infoEnd;
                                    if (!FastJson.FindObjectBounds(line, "\"info\"", plStart, plEnd, out infoStart, out infoEnd))
                                        return;

                                    int lastStart, lastEnd;
                                    if (!FastJson.FindObjectBounds(line, "\"last_token_usage\"", infoStart, infoEnd, out lastStart, out lastEnd))
                                        return;

                                    long tIn = 0, tOut = 0, tCr = 0;
                                    int totStart, totEnd;
                                    if (FastJson.FindObjectBounds(line, "\"total_token_usage\"", infoStart, infoEnd, out totStart, out totEnd))
                                    {
                                        tIn = FastJson.ExtractLong(line, "\"input_tokens\"", totStart, totEnd);
                                        tOut = FastJson.ExtractLong(line, "\"output_tokens\"", totStart, totEnd);
                                        tCr = FastJson.ExtractLong(line, "\"cached_input_tokens\"", totStart, totEnd);
                                    }
                                    if (tIn == 0 && tOut == 0 && tCr == 0) return;
                                    if (tIn == prevTotIn && tOut == prevTotOut && tCr == prevTotCr) return;
                                    prevTotIn = tIn; prevTotOut = tOut; prevTotCr = tCr;

                                    long rawIn = FastJson.ExtractLong(line, "\"input_tokens\"", lastStart, lastEnd);
                                    long cr = FastJson.ExtractLong(line, "\"cached_input_tokens\"", lastStart, lastEnd);
                                    long outTok = FastJson.ExtractLong(line, "\"output_tokens\"", lastStart, lastEnd);
                                    long fi = rawIn > cr ? (rawIn - cr) : 0L;
                                    if (fi + outTok + cr > 0)
                                    {
                                        string tsRaw = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, Math.Min(line.Length, 160));
                                        string dtStr = ToLocalDate(tsRaw, fallbackDt);
                                        long tsMs = ToUnixEpochMs(tsRaw, fallbackDt);
                                        int durMs = ComputeSafeDurationMs(turnStartMs, tsMs);
                                        if (tsMs > 0) turnStartMs = tsMs;
                                        fileEvents.Add(new UsageEvent("OpenAI Codex", curModel, dtStr, fi, outTok, cr, 0L, 0.0, 1, "", tsMs, durMs));
                                    }
                                }
                            },
                            out validEnd,
                            out endIdx);
                    }
                    catch { }

                    if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                    {
                        metaTid = ExtractMetaField(cur.ExtraMeta, "tid", metaTid);
                        fileEvents.AddRange(cur.Events);
                    }
                    if (metaTid.Length > 0) _codexLiveThreads.Add(metaTid);
                    string meta = string.Format(
                        CultureInfo.InvariantCulture,
                        "mdl={0};tid={1};pi={2};po={3};pc={4};tsm={5}",
                        curModel, metaTid, prevTotIn, prevTotOut, prevTotCr, turnStartMs);
                    SaveDirtyCursor(fp, fSize, fMtime, validEnd, meta, fileEvents);
                    _events.AddRange(fileEvents);
                }
            }
        }

        // -------------------- 3. Google Antigravity (Seek Cursor + FastJson) --------------------
        private void ScanAntigravity()
        {
            string brainDir = Path.Combine(_home, ".gemini", "antigravity", "brain");
            try { if (!Directory.Exists(brainDir)) return; } catch { return; }

            string[] convDirs;
            try { convDirs = Directory.GetDirectories(brainDir); } catch { return; }

            for (int i = 0; i < convDirs.Length; i++)
            {
                string tf = Path.Combine(convDirs[i], ".system_generated", "logs", "transcript.jsonl");
                long fSize, fMtime;
                if (!TryGetFileStat(tf, out fSize, out fMtime)) continue;
                TrackVisitedFile(tf);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(tf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                long startOffset = 0L;
                string curModel = "gemini-4-carbon";
                long genStartMs = 0L;
                long toolStartMs = 0L;
                Dictionary<string, UsageEvent> seenSteps = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);

                if (cur != null && fSize > cur.ByteOffset && cur.ByteOffset > 0)
                {
                    startOffset = cur.ByteOffset;
                    curModel = ExtractMetaField(cur.ExtraMeta, "mdl", curModel);
                    long.TryParse(ExtractMetaField(cur.ExtraMeta, "gsm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out genStartMs);
                    long.TryParse(ExtractMetaField(cur.ExtraMeta, "tsm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out toolStartMs);
                    for (int e = 0; e < cur.Events.Count; e++)
                    {
                        UsageEvent oldEv = cur.Events[e];
                        string dk = string.IsNullOrEmpty(oldEv.DedupKey) ? e.ToString(CultureInfo.InvariantCulture) : oldEv.DedupKey;
                        seenSteps[dk] = oldEv;
                    }
                }

                DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                long validEnd = startOffset;
                int endIdx = 0;

                try
                {
                    Utf8LineScanner.ScanFromOffset(
                        ToLongPath(tf),
                        startOffset,
                        0,
                        AgyKeywords,
                        delegate(string line, int curIdx)
                        {
                            if (line.IndexOf("Model Selection", StringComparison.Ordinal) >= 0 &&
                                line.IndexOf("USER_SETTINGS_CHANGE", StringComparison.Ordinal) >= 0)
                            {
                                string content = FastJson.ExtractString(line, "\"content\"", 0, line.Length);
                                if (!string.IsNullOrEmpty(content))
                                {
                                    Match m = AgyModelRegex.Match(content);
                                    if (m.Success)
                                        curModel = PricingEngine.NormalizeModelId(m.Groups[1].Value, curModel);
                                }
                            }

                            string createdRaw = FastJson.ExtractRawScalar(line, "\"created_at\"", 0, Math.Min(line.Length, 320));
                            if (createdRaw == null) createdRaw = FastJson.ExtractRawScalar(line, "\"created_at\"", 0, line.Length);
                            long curStepMs = !string.IsNullOrEmpty(createdRaw) ? ToUnixEpochMs(createdRaw, fallbackDt) : 0L;

                            if (line.IndexOf("input_tokens", StringComparison.Ordinal) < 0)
                            {
                                string stepType = FastJson.ExtractString(line, "\"type\"", 0, Math.Min(line.Length, 180));
                                if (stepType == "USER_INPUT")
                                {
                                    if (curStepMs > 0) genStartMs = curStepMs;
                                    toolStartMs = 0L;
                                }
                                else if (stepType == "GENERIC" && curStepMs > 0)
                                {
                                    // Tool calls start right when LLM generation finishes!
                                    if (toolStartMs == 0L) toolStartMs = curStepMs;
                                }
                                else if (curStepMs > 0 && genStartMs == 0L)
                                {
                                    genStartMs = curStepMs;
                                }
                                return;
                            }

                            long fi = FastJson.ExtractLong(line, "\"input_tokens\"", 0, line.Length);
                            long outTok = FastJson.ExtractLong(line, "\"output_tokens\"", 0, line.Length);
                            long cr = FastJson.ExtractLong(line, "\"cache_read_tokens\"", 0, line.Length);
                            long cw = FastJson.ExtractLong(line, "\"cache_creation_tokens\"", 0, line.Length);
                            if (cw == 0) cw = FastJson.ExtractLong(line, "\"cache_write_tokens\"", 0, line.Length);

                            if (fi + outTok + cr + cw > 0)
                            {
                                string stepKey = FastJson.ExtractRawScalar(line, "\"step_index\"", 0, Math.Min(line.Length, 200));
                                if (string.IsNullOrEmpty(stepKey)) stepKey = "s:" + seenSteps.Count.ToString(CultureInfo.InvariantCulture);
                                string dtStr = ToLocalDate(createdRaw, fallbackDt);
                                long tsMs = curStepMs > 0 ? curStepMs : ToUnixEpochMs(null, fallbackDt);
                                long genEndMs = (toolStartMs > genStartMs && toolStartMs <= tsMs) ? toolStartMs : tsMs;
                                int durMs = ComputeSafeDurationMs(genStartMs, genEndMs);
                                UsageEvent oldStep;
                                if (durMs == 0 && seenSteps.TryGetValue(stepKey, out oldStep) && oldStep.DurationMs > 0)
                                    durMs = oldStep.DurationMs;
                                seenSteps[stepKey] = new UsageEvent("Google Antigravity", curModel, dtStr, fi, outTok, cr, cw, 0.0, 1, stepKey, tsMs, durMs);
                            }
                            if (curStepMs > 0)
                            {
                                genStartMs = curStepMs;
                                toolStartMs = 0L;
                            }
                        },
                        out validEnd,
                        out endIdx);
                }
                catch { }

                List<UsageEvent> fileEvents = (seenSteps.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                    ? new List<UsageEvent>(cur.Events)
                    : new List<UsageEvent>(seenSteps.Values);
                SaveDirtyCursor(tf, fSize, fMtime, validEnd, "mdl=" + curModel + ";gsm=" + genStartMs.ToString(CultureInfo.InvariantCulture) + ";tsm=" + toolStartMs.ToString(CultureInfo.InvariantCulture), fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        // -------------------- 4. Gemini CLI --------------------
        private void ScanGeminiCli()
        {
            string tmpDir = Path.Combine(_home, ".gemini", "tmp");
            List<string> files = SafeFindFiles(tmpDir, "*.json", true);
            if (files.Count == 0) return;
            JavaScriptSerializer js = null;

            for (int i = 0; i < files.Count; i++)
            {
                string fp = files[i];
                if (fp.IndexOf(Path.DirectorySeparatorChar + "chats" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                long fSize, fMtime;
                if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                TrackVisitedFile(fp);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> fileEvents = new List<UsageEvent>();
                DateTime mtime = FileTimeTicksToLocal(fMtime);
                try
                {
                    string raw = ReadAllTextLong(fp);
                    if (raw.IndexOf("\"tokens\"", StringComparison.Ordinal) >= 0)
                    {
                        if (js == null) js = NewJson();
                        Dictionary<string, object> data = AsDict(js.DeserializeObject(raw));
                        if (data != null && data.ContainsKey("messages"))
                        {
                            IList msgs = data["messages"] as IList;
                            if (msgs != null)
                            {
                                long prevMsgMs = 0L;
                                for (int m = 0; m < msgs.Count; m++)
                                {
                                    Dictionary<string, object> item = AsDict(msgs[m]);
                                    if (item == null) continue;
                                    object rawTsObj = item.ContainsKey("timestamp") ? item["timestamp"] : null;
                                    long curMsgMs = rawTsObj != null ? ToUnixEpochMs(rawTsObj, mtime) : 0L;

                                    if (!item.ContainsKey("tokens"))
                                    {
                                        if (curMsgMs > 0) prevMsgMs = curMsgMs;
                                        continue;
                                    }
                                    Dictionary<string, object> tok = AsDict(item["tokens"]);
                                    if (tok == null)
                                    {
                                        if (curMsgMs > 0) prevMsgMs = curMsgMs;
                                        continue;
                                    }
                                    long rawIn = GetLong(tok, "input");
                                    long outTok = GetLong(tok, "output") + GetLong(tok, "thoughts");
                                    long cr = GetLong(tok, "cached");
                                    long fi = rawIn >= cr ? (rawIn - cr) : rawIn;
                                    if (fi + outTok + cr > 0)
                                    {
                                        string mdl = PricingEngine.NormalizeModelId(GetStr(item, "model"), "gemini-2.5-pro");
                                        string dtStr = ToLocalDate(rawTsObj, mtime);
                                        long tsMs = curMsgMs > 0 ? curMsgMs : ToUnixEpochMs(null, mtime);
                                        int durMs = ComputeSafeDurationMs(prevMsgMs, tsMs);
                                        fileEvents.Add(new UsageEvent("Gemini CLI", mdl, dtStr, fi, outTok, cr, 0L, 0.0, 1, "", tsMs, durMs));
                                    }
                                    if (curMsgMs > 0) prevMsgMs = curMsgMs;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                    fileEvents.AddRange(cur.Events);
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        // -------------------- 5. ZCode (Incremental SQLite Cursor + FastJson) --------------------
        private void ScanZCode()
        {
            string zdb = Path.Combine(_home, ".zcode", "cli", "db", "db.sqlite");
            long combSize, combMtime;
            if (!TryGetSqliteCombinedStat(zdb, out combSize, out combMtime)) return;
            TrackVisitedFile(zdb);

            FileCursorEntry cur;
            if (_cursors.TryGetValue(zdb, out cur) && cur.FileSize == combSize && cur.MtimeTicks == combMtime)
            {
                _events.AddRange(cur.Events);
                return;
            }

            long lastUpdated = 0L;
            Dictionary<string, UsageEvent> msgDedup = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
            if (cur != null && cur.Events != null && cur.Events.Count > 0)
            {
                long.TryParse(ExtractMetaField(cur.ExtraMeta, "tu", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lastUpdated);
                if (combSize < cur.ByteOffset)
                    lastUpdated = 0L;
                for (int i = 0; i < cur.Events.Count; i++)
                {
                    UsageEvent ev = cur.Events[i];
                    string dk = string.IsNullOrEmpty(ev.DedupKey) ? ("z:" + i.ToString(CultureInfo.InvariantCulture)) : ev.DedupKey;
                    msgDedup[dk] = ev;
                }
            }

            long maxSeenUpdated = lastUpdated;
            try
            {
                using (WinSqlite db = WinSqlite.OpenReadOnly(zdb))
                {
                    if (db == null)
                    {
                        if (cur != null && cur.Events != null) _events.AddRange(cur.Events);
                        return;
                    }

                    if (lastUpdated > 0)
                    {
                        long dbMaxUpdated = -1L;
                        db.Query("SELECT COALESCE(MAX(time_updated), 0) FROM message", delegate(WinSqlite.SqliteRow mr)
                        {
                            dbMaxUpdated = mr.GetInt64(0);
                        });
                        if (dbMaxUpdated >= 0 && dbMaxUpdated < lastUpdated)
                        {
                            lastUpdated = 0L;
                            maxSeenUpdated = 0L;
                        }
                    }

                    string sql = string.Format(
                        CultureInfo.InvariantCulture,
                        "SELECT id, time_created, time_updated, data FROM message WHERE time_updated >= {0} AND data LIKE '%\"tokens\"%' AND data LIKE '%\"assistant\"%'",
                        lastUpdated > 0 ? (lastUpdated - 1) : 0);

                    db.Query(sql, delegate(WinSqlite.SqliteRow row)
                    {
                        string msgId = row.GetString(0);
                        long tCreated = row.GetInt64(1);
                        long tUpdated = row.GetInt64(2);
                        string dataStr = row.GetString(3);
                        if (tUpdated > maxSeenUpdated) maxSeenUpdated = tUpdated;
                        if (string.IsNullOrEmpty(dataStr)) return;

                        string role = FastJson.ExtractString(dataStr, "\"role\"", 0, Math.Min(dataStr.Length, 160));
                        if (role != "assistant")
                        {
                            role = FastJson.ExtractString(dataStr, "\"role\"", 0, dataStr.Length);
                            if (role != "assistant") return;
                        }

                        int tokStart, tokEnd;
                        if (!FastJson.FindObjectBounds(dataStr, "\"tokens\"", 0, dataStr.Length, out tokStart, out tokEnd))
                            return;

                        long rawIn = FastJson.ExtractLong(dataStr, "\"input\"", tokStart, tokEnd);
                        long outTok = FastJson.ExtractLong(dataStr, "\"output\"", tokStart, tokEnd);
                        long tot = FastJson.ExtractLong(dataStr, "\"total\"", tokStart, tokEnd);
                        long cr = 0, cw = 0;
                        int cStart, cEnd;
                        if (FastJson.FindObjectBounds(dataStr, "\"cache\"", tokStart, tokEnd, out cStart, out cEnd))
                        {
                            cr = FastJson.ExtractLong(dataStr, "\"read\"", cStart, cEnd);
                            cw = FastJson.ExtractLong(dataStr, "\"write\"", cStart, cEnd);
                        }

                        string prov = FastJson.ExtractString(dataStr, "\"providerID\"", 0, dataStr.Length) ?? "";
                        long fi;
                        if ((tot > 0 && Math.Abs((rawIn + outTok) - tot) <= 4 && cr > 0) ||
                            (rawIn >= cr && cr > 0 && prov.IndexOf("anthropic", StringComparison.OrdinalIgnoreCase) < 0))
                        {
                            fi = Math.Max(0L, rawIn - cr - cw);
                        }
                        else
                        {
                            fi = rawIn;
                        }
                        if (fi + outTok + cr + cw <= 0) return;

                        string rawMdl = FastJson.ExtractString(dataStr, "\"modelID\"", 0, dataStr.Length);
                        if (string.IsNullOrEmpty(rawMdl))
                            rawMdl = FastJson.ExtractString(dataStr, "\"model\"", 0, dataStr.Length);
                        string mdl = PricingEngine.NormalizeModelId(rawMdl, "glm-5.3");

                        long createdTs = tCreated;
                        long completedTs = tUpdated;
                        int tmStart, tmEnd;
                        if (FastJson.FindObjectBounds(dataStr, "\"time\"", 0, Math.Min(dataStr.Length, 240), out tmStart, out tmEnd))
                        {
                            long cTs = FastJson.ExtractLong(dataStr, "\"created\"", tmStart, tmEnd);
                            long uTs = FastJson.ExtractLong(dataStr, "\"completed\"", tmStart, tmEnd);
                            if (cTs > 0) createdTs = cTs;
                            if (uTs > 0) completedTs = uTs;
                        }

                        long tsMs = ToUnixEpochMs(completedTs > 0 ? completedTs : createdTs, DateTime.Now);
                        long startMs = ToUnixEpochMs(createdTs, DateTime.Now);
                        int durMs = ComputeSafeDurationMs(startMs, tsMs);

                        double cost = FastJson.ExtractDouble(dataStr, "\"cost\"", 0, dataStr.Length);
                        string dk = string.IsNullOrEmpty(msgId) ? ("z:" + tCreated.ToString(CultureInfo.InvariantCulture)) : msgId;
                        UsageEvent prevEv;
                        if (!msgDedup.TryGetValue(dk, out prevEv) ||
                            (fi + outTok + cr + cw) >= (prevEv.FreshInput + prevEv.Output + prevEv.CacheRead + prevEv.CacheWrite))
                        {
                            msgDedup[dk] = new UsageEvent("ZCode", mdl, ToLocalDate(createdTs, DateTime.Now), fi, outTok, cr, cw, cost, 1, dk, tsMs, durMs);
                        }
                    });
                }
            }
            catch { }

            List<UsageEvent> zEvents = new List<UsageEvent>(msgDedup.Values);
            SaveDirtyCursor(zdb, combSize, combMtime, combSize, "tu=" + maxSeenUpdated.ToString(CultureInfo.InvariantCulture), zEvents);
            _events.AddRange(zEvents);
        }

        // -------------------- 6. Kimi Code (Seek Cursor + FastJson) --------------------
        private void ScanKimiCode()
        {
            string sessionsDir = Path.Combine(_home, ".kimi-code", "sessions");
            List<string> files = SafeFindFiles(sessionsDir, "wire.jsonl", true);

            for (int i = 0; i < files.Count; i++)
            {
                string wf = files[i];
                long fSize, fMtime;
                if (!TryGetFileStat(wf, out fSize, out fMtime)) continue;
                TrackVisitedFile(wf);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(wf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                long startOffset = 0L;
                string curModel = "kimi-for-coding";
                long lastReqMs = 0L;
                List<UsageEvent> fileEvents = new List<UsageEvent>();

                if (cur != null && fSize > cur.ByteOffset && cur.ByteOffset > 0)
                {
                    startOffset = cur.ByteOffset;
                    curModel = ExtractMetaField(cur.ExtraMeta, "mdl", curModel);
                    long.TryParse(ExtractMetaField(cur.ExtraMeta, "lrm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lastReqMs);
                    fileEvents.AddRange(cur.Events);
                }

                DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                long validEnd = startOffset;
                int endIdx = 0;

                try
                {
                    Utf8LineScanner.ScanFromOffset(
                        ToLongPath(wf),
                        startOffset,
                        0,
                        KimiKeywords,
                        delegate(string line, int curIdx)
                        {
                            string t = FastJson.ExtractString(line, "\"type\"", 0, Math.Min(line.Length, 160));
                            if (t == null) t = FastJson.ExtractString(line, "\"type\"", 0, line.Length);
                            if (t == "llm.request")
                            {
                                string m = FastJson.ExtractString(line, "\"model\"", 0, line.Length)
                                           ?? FastJson.ExtractString(line, "\"modelAlias\"", 0, line.Length);
                                curModel = PricingEngine.NormalizeModelId(m, curModel);
                                string reqTRaw = FastJson.ExtractRawScalar(line, "\"time\"", 0, Math.Min(line.Length, 180))
                                                 ?? FastJson.ExtractRawScalar(line, "\"time\"", 0, line.Length);
                                if (!string.IsNullOrEmpty(reqTRaw))
                                {
                                    long rms = ToUnixEpochMs(reqTRaw, fallbackDt);
                                    if (rms > 0) lastReqMs = rms;
                                }
                            }
                            else if (t == "usage.record")
                            {
                                int uStart, uEnd;
                                if (!FastJson.FindObjectBounds(line, "\"usage\"", 0, line.Length, out uStart, out uEnd))
                                    return;
                                long fi = FastJson.ExtractLong(line, "\"inputOther\"", uStart, uEnd);
                                long outTok = FastJson.ExtractLong(line, "\"output\"", uStart, uEnd);
                                long cr = FastJson.ExtractLong(line, "\"inputCacheRead\"", uStart, uEnd);
                                long cw = FastJson.ExtractLong(line, "\"inputCacheCreation\"", uStart, uEnd);
                                if (fi + outTok + cr + cw > 0)
                                {
                                    string mdl = PricingEngine.NormalizeModelId(FastJson.ExtractString(line, "\"model\"", 0, line.Length), curModel);
                                    string tRaw = FastJson.ExtractRawScalar(line, "\"time\"", 0, Math.Min(line.Length, 180))
                                                  ?? FastJson.ExtractRawScalar(line, "\"time\"", 0, line.Length);
                                    long tsMs = ToUnixEpochMs(tRaw, fallbackDt);
                                    int durMs = ComputeSafeDurationMs(lastReqMs, tsMs);
                                    if (tsMs > 0) lastReqMs = tsMs;
                                    fileEvents.Add(new UsageEvent("Kimi Code", mdl, ToLocalDate(tRaw, fallbackDt), fi, outTok, cr, cw, 0.0, 1, "", tsMs, durMs));
                                }
                            }
                        },
                        out validEnd,
                        out endIdx);
                }
                catch { }

                if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                    fileEvents.AddRange(cur.Events);
                SaveDirtyCursor(wf, fSize, fMtime, validEnd, "mdl=" + curModel + ";lrm=" + lastReqMs.ToString(CultureInfo.InvariantCulture), fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        // -------------------- 7. MobileClaw & 8. OpenCode --------------------
        private static UsageEvent ParseOpenCodeMessageDict(string toolName, Dictionary<string, object> d, DateTime fallbackTime, object fallbackTs, string defaultModel)
        {
            if (d == null || GetStr(d, "role") != "assistant") return null;
            Dictionary<string, object> tok = AsDict(d.ContainsKey("tokens") ? d["tokens"] : null);
            if (tok == null) return null;

            long rawIn = GetLong(tok, "input");
            long outTok = GetLong(tok, "output");
            long reas = GetLong(tok, "reasoning");
            if (outTok == 0 && reas > 0) outTok = reas;
            Dictionary<string, object> cache = AsDict(tok.ContainsKey("cache") ? tok["cache"] : null);
            long cr = GetLong(cache, "read");
            long cw = GetLong(cache, "write");
            long tot = GetLong(tok, "total");

            long fi = (tot > 0 && Math.Abs((rawIn + outTok) - tot) <= 4 && cr > 0) ? Math.Max(0L, rawIn - cr - cw) : rawIn;
            if (fi + outTok + cr + cw <= 0) return null;

            string mdl = PricingEngine.NormalizeModelId(GetStr(d, "modelID") ?? GetStr(d, "model"), defaultModel);
            Dictionary<string, object> timeObj = AsDict(d.ContainsKey("time") ? d["time"] : null);
            object tVal = (timeObj != null && timeObj.ContainsKey("created")) ? timeObj["created"] : fallbackTs;
            object tDone = (timeObj != null && timeObj.ContainsKey("completed")) ? timeObj["completed"] : tVal;
            long startMs = ToUnixEpochMs(tVal, fallbackTime);
            long endMs = ToUnixEpochMs(tDone, fallbackTime);
            int durMs = ComputeSafeDurationMs(startMs, endMs);
            double cost = GetDouble(d, "cost");
            string mid = GetStr(d, "id") ?? "";
            return new UsageEvent(toolName, mdl, ToLocalDate(tVal, fallbackTime), fi, outTok, cr, cw, cost, 1, mid, endMs > 0 ? endMs : startMs, durMs);
        }

        private void ScanMobileClaw()
        {
            string mcDir = Path.Combine(_home, ".local", "share", "mobileclaw", "workspaces");
            try { if (!Directory.Exists(mcDir)) return; } catch { return; }

            JavaScriptSerializer js = null;
            string[] wsDirs;
            try { wsDirs = Directory.GetDirectories(mcDir); } catch { return; }

            for (int w = 0; w < wsDirs.Length; w++)
            {
                string msgRoot = Path.Combine(wsDirs[w], "storage", "message");
                List<string> files = SafeFindFiles(msgRoot, "*.json", true);
                for (int i = 0; i < files.Count; i++)
                {
                    string mf = files[i];
                    long fSize, fMtime;
                    if (!TryGetFileStat(mf, out fSize, out fMtime)) continue;
                    TrackVisitedFile(mf);

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(mf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        for (int e = 0; e < cur.Events.Count; e++)
                        {
                            UsageEvent ev = cur.Events[e];
                            if (string.IsNullOrEmpty(ev.DedupKey) || _mobileClawSeenKeys.Add(ev.DedupKey))
                                _events.Add(ev);
                        }
                        continue;
                    }

                    List<UsageEvent> fileEvents = new List<UsageEvent>();
                    try
                    {
                        if (js == null) js = NewJson();
                        Dictionary<string, object> d = AsDict(js.DeserializeObject(ReadAllTextLong(mf)));
                        if (d != null)
                        {
                            string mid = GetStr(d, "id") ?? Path.GetFileNameWithoutExtension(mf);
                            UsageEvent ev = ParseOpenCodeMessageDict("MobileClaw", d, FileTimeTicksToLocal(fMtime), null, "deepseek-v4.1-flash");
                            if (ev != null)
                            {
                                ev.DedupKey = mid;
                                fileEvents.Add(ev);
                            }
                        }
                    }
                    catch { }
                    if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                        fileEvents.AddRange(cur.Events);
                    for (int e = 0; e < fileEvents.Count; e++)
                    {
                        UsageEvent ev = fileEvents[e];
                        if (string.IsNullOrEmpty(ev.DedupKey) || _mobileClawSeenKeys.Add(ev.DedupKey))
                            _events.Add(ev);
                    }
                    SaveDirtyCursor(mf, fSize, fMtime, fSize, "", fileEvents);
                }
            }
        }

        private void ScanOpenCode()
        {
            string[] bases = new string[]
            {
                Path.Combine(_home, ".local", "share", "opencode"),
                Path.Combine(_home, "AppData", "Local", "opencode")
            };
            JavaScriptSerializer js = null;

            for (int b = 0; b < bases.Length; b++)
            {
                string baseDir = bases[b];
                try { if (!Directory.Exists(baseDir)) continue; } catch { continue; }

                List<string> dbs = SafeFindFiles(baseDir, "opencode*.db", false);
                for (int i = 0; i < dbs.Count; i++)
                {
                    string dbPath = dbs[i];
                    long cSize, cMtime;
                    if (!TryGetSqliteCombinedStat(dbPath, out cSize, out cMtime)) continue;
                    TrackVisitedFile(dbPath);

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(dbPath, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
                    {
                        for (int e = 0; e < cur.Events.Count; e++)
                        {
                            UsageEvent ev = cur.Events[e];
                            if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                                _events.Add(ev);
                        }
                        continue;
                    }

                    List<UsageEvent> dbEvents = new List<UsageEvent>();
                    bool dbOpened = false;
                    try
                    {
                        if (js == null) js = NewJson();
                        using (WinSqlite db = WinSqlite.OpenReadOnly(dbPath))
                        {
                            if (db != null)
                            {
                                dbOpened = true;
                                db.Query("SELECT id, time_created, data FROM message WHERE data LIKE '%\"tokens\"%'", delegate(WinSqlite.SqliteRow row)
                                {
                                    string mid = row.GetString(0);
                                    long tc = row.GetInt64(1);
                                    string ds = row.GetString(2);
                                    if (string.IsNullOrEmpty(ds)) return;
                                    try
                                    {
                                        Dictionary<string, object> d = AsDict(js.DeserializeObject(ds));
                                        UsageEvent ev = ParseOpenCodeMessageDict("OpenCode", d, DateTime.Now, tc, "claude-sonnet-4-6");
                                        if (ev != null)
                                        {
                                            ev.DedupKey = mid;
                                            dbEvents.Add(ev);
                                        }
                                    }
                                    catch { }
                                });
                            }
                        }
                    }
                    catch { }

                    if (!dbOpened && cur != null && cur.Events != null)
                    {
                        for (int e = 0; e < cur.Events.Count; e++)
                        {
                            UsageEvent ev = cur.Events[e];
                            if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                                _events.Add(ev);
                        }
                        continue;
                    }

                    dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
                    for (int e = 0; e < dbEvents.Count; e++)
                    {
                        UsageEvent ev = dbEvents[e];
                        if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                            _events.Add(ev);
                    }
                    SaveDirtyCursor(dbPath, cSize, cMtime, cSize, "", dbEvents);
                }

                string msgDir = Path.Combine(baseDir, "storage", "message");
                List<string> jsonFiles = SafeFindFiles(msgDir, "*.json", true);
                for (int i = 0; i < jsonFiles.Count; i++)
                {
                    string mf = jsonFiles[i];
                    long fSize, fMtime;
                    if (!TryGetFileStat(mf, out fSize, out fMtime)) continue;
                    TrackVisitedFile(mf);

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(mf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        for (int e = 0; e < cur.Events.Count; e++)
                        {
                            UsageEvent ev = cur.Events[e];
                            if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                                _events.Add(ev);
                        }
                        continue;
                    }

                    List<UsageEvent> fileEvents = new List<UsageEvent>();
                    try
                    {
                        if (js == null) js = NewJson();
                        Dictionary<string, object> d = AsDict(js.DeserializeObject(ReadAllTextLong(mf)));
                        if (d != null)
                        {
                            string mid = GetStr(d, "id") ?? Path.GetFileNameWithoutExtension(mf);
                            UsageEvent ev = ParseOpenCodeMessageDict("OpenCode", d, FileTimeTicksToLocal(fMtime), null, "claude-sonnet-4-6");
                            if (ev != null)
                            {
                                ev.DedupKey = mid;
                                fileEvents.Add(ev);
                            }
                        }
                    }
                    catch { }
                    if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                        fileEvents.AddRange(cur.Events);
                    for (int e = 0; e < fileEvents.Count; e++)
                    {
                        UsageEvent ev = fileEvents[e];
                        if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                            _events.Add(ev);
                    }
                    SaveDirtyCursor(mf, fSize, fMtime, fSize, "", fileEvents);
                }
            }
        }

        // -------------------- 9. WorkBuddy / CodeBuddy --------------------
        private void ScanWorkBuddyAndCodeBuddy()
        {
            string[,] targets = new string[,]
            {
                { "WorkBuddy", Path.Combine(_home, ".workbuddy") },
                { "WorkBuddy", Path.Combine(_home, ".workbuddy-ai") },
                { "CodeBuddy", Path.Combine(_home, ".codebuddy") }
            };
            JavaScriptSerializer js = null;

            for (int t = 0; t < targets.GetLength(0); t++)
            {
                string toolName = targets[t, 0];
                string root = targets[t, 1];
                string proj = Path.Combine(root, "projects");
                try { if (!Directory.Exists(proj)) continue; } catch { continue; }

                string defaultModel = "workbuddy-default";
                string stFile = Path.Combine(root, "settings.json");
                try
                {
                    if (File.Exists(stFile))
                    {
                        if (js == null) js = NewJson();
                        Dictionary<string, object> st = AsDict(js.DeserializeObject(ReadAllTextLong(stFile)));
                        if (st != null)
                        {
                            string[] mks = new string[] { "model", "defaultModel", "currentModel", "selectedModel" };
                            for (int k = 0; k < mks.Length; k++)
                            {
                                string v = GetStr(st, mks[k]);
                                if (!string.IsNullOrEmpty(v))
                                {
                                    defaultModel = PricingEngine.NormalizeModelId(v, defaultModel);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                ScanGenericJsonlTree(toolName, proj, defaultModel, "*.jsonl");
            }
        }

        // -------------------- 10. DeepSeek (DSH) --------------------
        private void ScanDsh()
        {
            string dshDir = Path.Combine(_home, ".dsh", "storages");
            List<string> files = SafeFindFiles(dshDir, "session-*.json", true);
            if (files.Count == 0) return;
            JavaScriptSerializer js = null;

            for (int i = 0; i < files.Count; i++)
            {
                string sf = files[i];
                long fSize, fMtime;
                if (!TryGetFileStat(sf, out fSize, out fMtime)) continue;
                TrackVisitedFile(sf);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(sf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> fileEvents = new List<UsageEvent>();
                DateTime mtime = FileTimeTicksToLocal(fMtime);
                try
                {
                    if (js == null) js = NewJson();
                    Dictionary<string, object> data = AsDict(js.DeserializeObject(ReadAllTextLong(sf)));
                    if (data != null && data.ContainsKey("record"))
                    {
                        Dictionary<string, object> rec = AsDict(data["record"]);
                        IList rows = (rec != null && rec.ContainsKey("rows")) ? (rec["rows"] as IList) : null;
                        if (rows != null)
                        {
                            for (int r = 0; r < rows.Count; r++)
                            {
                                Dictionary<string, object> item = AsDict(rows[r]);
                                if (item == null) continue;
                                Dictionary<string, object> meta = AsDict(item.ContainsKey("meta") ? item["meta"] : null);
                                Dictionary<string, object> u = AsDict(item.ContainsKey("usage") ? item["usage"] : (meta != null && meta.ContainsKey("usage") ? meta["usage"] : null));
                                if (u == null) continue;

                                long rawIn = GetLong(u, "prompt_tokens");
                                if (rawIn == 0) rawIn = GetLong(u, "input_tokens");
                                long outTok = GetLong(u, "completion_tokens");
                                if (outTok == 0) outTok = GetLong(u, "output_tokens");
                                long cr = GetLong(u, "prompt_cache_hit_tokens");
                                if (cr == 0) cr = GetLong(u, "cache_read_tokens");
                                if (cr == 0) cr = GetLong(u, "cached_tokens");

                                long fi = (rawIn >= cr && cr > 0) ? (rawIn - cr) : rawIn;
                                if (fi + outTok + cr > 0)
                                {
                                    string mdl = PricingEngine.NormalizeModelId(GetStr(item, "model"), "deepseek-flash");
                                    object ts = item.ContainsKey("created_at") ? item["created_at"] : (item.ContainsKey("timestamp") ? item["timestamp"] : null);
                                    fileEvents.Add(new UsageEvent("DeepSeek (DSH)", mdl, ToLocalDate(ts, mtime), fi, outTok, cr, 0L, 0.0, 1));
                                }
                            }
                        }
                    }
                }
                catch { }

                if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                    fileEvents.AddRange(cur.Events);
                SaveDirtyCursor(sf, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        // -------------------- 11. Command Code --------------------
        private void ScanCommandCode()
        {
            ScanGenericJsonlTree("Command Code", Path.Combine(_home, ".commandcode", "projects"), "claude-sonnet-4-6", "*.jsonl");
        }

        // -------------------- 12. VS Code / Cursor Cline Family --------------------
        private void ScanVsCodeClineFamily()
        {
            string roaming = Path.Combine(_home, "AppData", "Roaming");
            string[] ides = new string[] { "Code", "Cursor", "Windsurf", "Antigravity" };
            string[,] exts = new string[,]
            {
                { "Cline", "saoudrizwan.claude-dev" },
                { "Roo Code", "rooveterinaryinc.roo-cline" },
                { "Kilo Code", "kilocode.kilo-code" }
            };
            JavaScriptSerializer js = null;

            for (int i = 0; i < ides.Length; i++)
            {
                for (int e = 0; e < exts.GetLength(0); e++)
                {
                    string tasksDir = Path.Combine(roaming, ides[i], "User", "globalStorage", exts[e, 1], "tasks");
                    List<string> files = SafeFindFiles(tasksDir, "ui_messages.json", true);
                    for (int f = 0; f < files.Count; f++)
                    {
                        string uf = files[f];
                        long fSize, fMtime;
                        if (!TryGetFileStat(uf, out fSize, out fMtime)) continue;
                        TrackVisitedFile(uf);

                        FileCursorEntry cur;
                        if (_cursors.TryGetValue(uf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                        {
                            _events.AddRange(cur.Events);
                            continue;
                        }

                        List<UsageEvent> fileEvents = new List<UsageEvent>();
                        DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                        try
                        {
                            if (js == null) js = NewJson();
                            IList msgs = js.DeserializeObject(ReadAllTextLong(uf)) as IList;
                            if (msgs != null)
                            {
                                string label = exts[e, 0] + " (" + ides[i] + ")";
                                for (int m = 0; m < msgs.Count; m++)
                                {
                                    Dictionary<string, object> item = AsDict(msgs[m]);
                                    if (item == null || GetStr(item, "say") != "api_req_started") continue;
                                    string txt = GetStr(item, "text");
                                    if (string.IsNullOrEmpty(txt)) continue;
                                    Dictionary<string, object> info = AsDict(js.DeserializeObject(txt));
                                    if (info == null) continue;
                                    long fi = GetLong(info, "tokensIn");
                                    long outTok = GetLong(info, "tokensOut");
                                    long cr = GetLong(info, "cacheReads");
                                    long cw = GetLong(info, "cacheWrites");
                                    double cost = GetDouble(info, "cost");
                                    if (fi + outTok + cr + cw > 0)
                                    {
                                        string mdl = PricingEngine.NormalizeModelId(GetStr(info, "model"), "claude-sonnet-4-6");
                                        fileEvents.Add(new UsageEvent(label, mdl, ToLocalDate(item.ContainsKey("ts") ? item["ts"] : null, fallbackDt), fi, outTok, cr, cw, cost, 1));
                                    }
                                }
                            }
                        }
                        catch { }

                        if (fileEvents.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                            fileEvents.AddRange(cur.Events);
                        SaveDirtyCursor(uf, fSize, fMtime, fSize, "", fileEvents);
                        _events.AddRange(fileEvents);
                    }
                }
            }
        }

        // -------------------- 12b. Expanded Multi-Harness Scanners --------------------
        private bool TryExtractGenericUsage(
            Dictionary<string, object> obj,
            string curModel,
            DateTime fallbackTime,
            out string modelOut,
            out string dateOut,
            out long fiOut,
            out long outTokOut,
            out long crOut,
            out long cwOut,
            out double costOut,
            out string dedupIdOut)
        {
            modelOut = curModel;
            dateOut = "";
            fiOut = 0; outTokOut = 0; crOut = 0; cwOut = 0;
            costOut = 0.0;
            dedupIdOut = "";
            if (obj == null) return false;

            Dictionary<string, object> msg = AsDict(obj.ContainsKey("message") ? obj["message"] : null);
            Dictionary<string, object> meta = AsDict(obj.ContainsKey("_meta") ? obj["_meta"] : null);
            Dictionary<string, object> pdata = AsDict(obj.ContainsKey("providerData") ? obj["providerData"] : null);

            string role = GetStr(msg, "role") ?? GetStr(obj, "role");
            if (!string.IsNullOrEmpty(role) && role != "assistant" && role != "model" && role != "ai" && role != "agent")
                return false;

            Dictionary<string, object> u = null;
            string[] uKeys = new string[] { "usage", "tokens", "tokenUsage", "usageMetadata" };
            for (int k = 0; k < uKeys.Length; k++)
            {
                if (obj.ContainsKey(uKeys[k])) { u = AsDict(obj[uKeys[k]]); if (u != null) break; }
                if (msg != null && msg.ContainsKey(uKeys[k])) { u = AsDict(msg[uKeys[k]]); if (u != null) break; }
            }
            if (u == null) return false;

            string rawM = GetStr(msg, "model") ?? GetStr(msg, "modelId") ?? GetStr(obj, "model") ?? GetStr(obj, "modelId") ?? GetStr(obj, "modelID")
                          ?? GetStr(meta, "model") ?? GetStr(meta, "modelId") ?? GetStr(pdata, "model");
            if (rawM == "<synthetic>") return false;
            modelOut = PricingEngine.NormalizeModelId(rawM, curModel);

            bool hasAnthropic = u.ContainsKey("input_tokens") && (u.ContainsKey("cache_read_input_tokens") || u.ContainsKey("cache_creation_input_tokens"));
            bool hasPi = u.ContainsKey("input") && (u.ContainsKey("cacheRead") || u.ContainsKey("cacheWrite"));

            long rawIn = GetLong(u, "input_tokens");
            if (rawIn == 0) rawIn = GetLong(u, "input");
            if (rawIn == 0) rawIn = GetLong(u, "prompt_tokens");
            if (rawIn == 0) rawIn = GetLong(u, "promptTokenCount");
            if (rawIn == 0) rawIn = GetLong(u, "tokensIn");
            if (rawIn == 0) rawIn = GetLong(u, "inputOther");

            outTokOut = GetLong(u, "output_tokens");
            if (outTokOut == 0) outTokOut = GetLong(u, "output");
            if (outTokOut == 0) outTokOut = GetLong(u, "completion_tokens");
            if (outTokOut == 0) outTokOut = GetLong(u, "candidatesTokenCount");
            if (outTokOut == 0) outTokOut = GetLong(u, "tokensOut");
            outTokOut += GetLong(u, "thoughts") + GetLong(u, "thoughtsTokenCount");
            if (outTokOut == 0) outTokOut = GetLong(u, "reasoning") + GetLong(u, "reasoning_tokens");

            Dictionary<string, object> cacheSub = AsDict(u.ContainsKey("cache") ? u["cache"] : null);
            crOut = GetLong(u, "cache_read_input_tokens");
            if (crOut == 0) crOut = GetLong(u, "cacheRead");
            if (crOut == 0) crOut = GetLong(u, "cache_read_tokens");
            if (crOut == 0) crOut = GetLong(u, "cached_input_tokens");
            if (crOut == 0) crOut = GetLong(u, "cached_tokens");
            if (crOut == 0) crOut = GetLong(u, "cached");
            if (crOut == 0) crOut = GetLong(u, "prompt_cache_hit_tokens");
            if (crOut == 0) crOut = GetLong(u, "inputCacheRead");
            if (crOut == 0 && cacheSub != null) crOut = GetLong(cacheSub, "read");

            cwOut = GetLong(u, "cache_creation_input_tokens");
            if (cwOut == 0) cwOut = GetLong(u, "cacheWrite");
            if (cwOut == 0) cwOut = GetLong(u, "cache_creation_tokens");
            if (cwOut == 0) cwOut = GetLong(u, "cache_write_tokens");
            if (cwOut == 0) cwOut = GetLong(u, "inputCacheCreation");
            if (cwOut == 0 && cacheSub != null) cwOut = GetLong(cacheSub, "write");

            long tot = GetLong(u, "total_tokens");
            if (tot == 0) tot = GetLong(u, "totalTokens");
            if (tot == 0) tot = GetLong(u, "total");

            if (hasAnthropic || hasPi || u.ContainsKey("inputOther"))
                fiOut = rawIn;
            else if ((u.ContainsKey("prompt_tokens") && crOut > 0 && rawIn >= crOut) || (tot > 0 && Math.Abs((rawIn + outTokOut) - tot) <= 4 && crOut > 0))
                fiOut = Math.Max(0L, rawIn - crOut - cwOut);
            else
                fiOut = rawIn;

            if (fiOut + outTokOut + crOut + cwOut <= 0)
                return false;

            if (u.ContainsKey("cost"))
            {
                Dictionary<string, object> cDict = AsDict(u["cost"]);
                costOut = cDict != null ? GetDouble(cDict, "total") : GetDouble(u, "cost");
            }
            if (costOut <= 0) costOut = GetDouble(obj, "costUSD");
            if (costOut <= 0) costOut = GetDouble(obj, "cost");
            if (costOut <= 0) costOut = GetDouble(obj, "total_cost");

            object tsObj = obj.ContainsKey("timestamp") ? obj["timestamp"]
                : (obj.ContainsKey("created_at") ? obj["created_at"]
                : (obj.ContainsKey("createdAt") ? obj["createdAt"]
                : (obj.ContainsKey("time") ? obj["time"] : null)));
            Dictionary<string, object> tsDict = AsDict(tsObj);
            if (tsDict != null)
                tsObj = tsDict.ContainsKey("created") ? tsDict["created"] : (tsDict.ContainsKey("start") ? tsDict["start"] : null);
            dateOut = ToLocalDate(tsObj, fallbackTime);

            dedupIdOut = GetStr(obj, "requestId") ?? GetStr(msg, "id") ?? GetStr(obj, "id") ?? GetStr(obj, "uuid") ?? "";
            return true;
        }

        private void ScanGenericJsonlTree(string toolName, string rootDir, string defaultModel, string pattern)
        {
            List<string> files = SafeFindFiles(rootDir, pattern, true);
            if (files.Count == 0) return;
            JavaScriptSerializer js = null;
            Dictionary<string, UsageEvent> dedup = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);

            for (int i = 0; i < files.Count; i++)
            {
                string fp = files[i];
                if (fp.EndsWith(".checkpoints.jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                long fSize, fMtime;
                if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                TrackVisitedFile(fp);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    for (int e = 0; e < cur.Events.Count; e++)
                    {
                        UsageEvent ev = cur.Events[e];
                        string dk = string.IsNullOrEmpty(ev.DedupKey) ? (fp + ":c" + e.ToString(CultureInfo.InvariantCulture)) : ev.DedupKey;
                        UsageEvent prev;
                        if (!dedup.TryGetValue(dk, out prev) ||
                            (ev.FreshInput + ev.Output + ev.CacheRead + ev.CacheWrite) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                        {
                            dedup[dk] = ev;
                        }
                    }
                    continue;
                }

                if (js == null) js = NewJson();
                DateTime mtime = FileTimeTicksToLocal(fMtime);
                string curM = defaultModel;
                Dictionary<string, UsageEvent> fileDedup = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);

                try
                {
                    using (FileStream fs = new FileStream(ToLongPath(fp), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        int idx = 0;
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            idx++;
                            if (line.IndexOf("usage", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("tokens", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("input", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("model", StringComparison.Ordinal) < 0)
                                continue;

                            Dictionary<string, object> obj;
                            try { obj = AsDict(js.DeserializeObject(line)); }
                            catch { continue; }
                            if (obj == null) continue;

                            string typ = GetStr(obj, "type");
                            if (typ == "model_change" || typ == "turn_context" || typ == "llm.request" || typ == "session")
                            {
                                Dictionary<string, object> pl = AsDict(obj.ContainsKey("payload") ? obj["payload"] : null);
                                string cand = GetStr(obj, "modelId") ?? GetStr(obj, "model") ?? GetStr(pl, "model");
                                if (!string.IsNullOrEmpty(cand)) curM = PricingEngine.NormalizeModelId(cand, curM);
                            }
                            else
                            {
                                Dictionary<string, object> meta = AsDict(obj.ContainsKey("_meta") ? obj["_meta"] : null);
                                Dictionary<string, object> pdata = AsDict(obj.ContainsKey("providerData") ? obj["providerData"] : null);
                                string cand = GetStr(obj, "model") ?? GetStr(meta, "model") ?? GetStr(meta, "modelId") ?? GetStr(pdata, "model");
                                if (!string.IsNullOrEmpty(cand)) curM = PricingEngine.NormalizeModelId(cand, curM);
                            }

                            string mdl, dt, did;
                            long fi, outTok, cr, cw;
                            double cost;
                            if (!TryExtractGenericUsage(obj, curM, mtime, out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did))
                                continue;

                            curM = mdl;
                            string key = !string.IsNullOrEmpty(did) ? did : (fp + ":L" + idx.ToString(CultureInfo.InvariantCulture));
                            UsageEvent prev;
                            if (!fileDedup.TryGetValue(key, out prev) ||
                                (fi + outTok + cr + cw) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                            {
                                fileDedup[key] = new UsageEvent(toolName, mdl, dt, fi, outTok, cr, cw, cost, 1, key);
                            }
                        }
                    }
                }
                catch { }

                List<UsageEvent> fileEvents = (fileDedup.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                    ? new List<UsageEvent>(cur.Events)
                    : new List<UsageEvent>(fileDedup.Values);
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);

                for (int e = 0; e < fileEvents.Count; e++)
                {
                    UsageEvent ev = fileEvents[e];
                    UsageEvent prev;
                    if (!dedup.TryGetValue(ev.DedupKey, out prev) ||
                        (ev.FreshInput + ev.Output + ev.CacheRead + ev.CacheWrite) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                    {
                        dedup[ev.DedupKey] = ev;
                    }
                }
            }

            foreach (UsageEvent ev in dedup.Values)
                _events.Add(ev);
        }

        private void ScanOpenClaw(string homeRoot)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(homeRoot, ".openclaw*");
                for (int i = 0; i < dirs.Length; i++)
                {
                    ScanGenericJsonlTree("OpenClaw", Path.Combine(dirs[i], "agents"), "claude-sonnet-4-6", "*.jsonl");
                    ScanGenericJsonlTree("OpenClaw", Path.Combine(dirs[i], "sessions"), "claude-sonnet-4-6", "*.jsonl");
                }
            }
            catch { }
        }

        private void ScanPiAndOmp(string homeRoot)
        {
            ScanGenericJsonlTree("Pi Agent", Path.Combine(homeRoot, ".pi", "agent", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Pi Agent", Path.Combine(homeRoot, ".pi", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Oh My Pi", Path.Combine(homeRoot, ".omp", "agent", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Oh My Pi", Path.Combine(homeRoot, ".omp", "sessions"), "claude-sonnet-4-6", "*.jsonl");
        }

        private void ScanQwenCode(string homeRoot)
        {
            ScanGenericJsonlTree("Qwen Code", Path.Combine(homeRoot, ".qwen", "projects"), "qwen3-coder-plus", "*.jsonl");
            List<string> chatFiles = SafeFindFiles(Path.Combine(homeRoot, ".qwen", "tmp"), "*.json", true);
            if (chatFiles.Count == 0) return;
            JavaScriptSerializer js = null;
            for (int i = 0; i < chatFiles.Count; i++)
            {
                string fp = chatFiles[i];
                if (fp.IndexOf("chats", StringComparison.OrdinalIgnoreCase) < 0) continue;
                long fSize, fMtime;
                if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                TrackVisitedFile(fp);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> fileEvents = new List<UsageEvent>();
                DateTime mtime = FileTimeTicksToLocal(fMtime);
                try
                {
                    if (js == null) js = NewJson();
                    Dictionary<string, object> data = AsDict(js.DeserializeObject(ReadAllTextLong(fp)));
                    if (data != null && data.ContainsKey("messages"))
                    {
                        IList msgs = data["messages"] as IList;
                        if (msgs != null)
                        {
                            for (int m = 0; m < msgs.Count; m++)
                            {
                                Dictionary<string, object> item = AsDict(msgs[m]);
                                if (item == null || !item.ContainsKey("tokens")) continue;
                                Dictionary<string, object> tok = AsDict(item["tokens"]);
                                if (tok == null) continue;
                                long rawIn = GetLong(tok, "input");
                                long outTok = GetLong(tok, "output") + GetLong(tok, "thoughts");
                                long cr = GetLong(tok, "cached");
                                long fi = rawIn >= cr ? (rawIn - cr) : rawIn;
                                if (fi + outTok + cr > 0)
                                {
                                    string mdl = PricingEngine.NormalizeModelId(GetStr(item, "model"), "qwen3-coder-plus");
                                    fileEvents.Add(new UsageEvent("Qwen Code", mdl, ToLocalDate(item.ContainsKey("timestamp") ? item["timestamp"] : null, mtime), fi, outTok, cr, 0L, 0.0, 1));
                                }
                            }
                        }
                    }
                }
                catch { }
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        private void ScanFactoryDroid(string homeRoot)
        {
            string fDir = Path.Combine(homeRoot, ".factory", "sessions");
            ScanGenericJsonlTree("Factory Droid", fDir, "claude-sonnet-4-6", "*.jsonl");
            List<string> jFiles = SafeFindFiles(fDir, "*.json", true);
            if (jFiles.Count == 0) return;
            JavaScriptSerializer js = null;
            for (int i = 0; i < jFiles.Count; i++)
            {
                string fp = jFiles[i];
                long fSize, fMtime;
                if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                TrackVisitedFile(fp);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> fileEvents = new List<UsageEvent>();
                try
                {
                    if (js == null) js = NewJson();
                    Dictionary<string, object> obj = AsDict(js.DeserializeObject(ReadAllTextLong(fp)));
                    string mdl, dt, did; long fi, outTok, cr, cw; double cost;
                    if (TryExtractGenericUsage(obj, "claude-sonnet-4-6", FileTimeTicksToLocal(fMtime), out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did))
                        fileEvents.Add(new UsageEvent("Factory Droid", mdl, dt, fi, outTok, cr, cw, cost, 1));
                }
                catch { }
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        private void ScanAmp(string homeRoot)
        {
            string[] roots = new string[]
            {
                Path.Combine(homeRoot, ".local", "share", "amp", "threads"),
                Path.Combine(homeRoot, ".amp", "threads"),
                Path.Combine(homeRoot, "AppData", "Local", "amp", "threads")
            };
            JavaScriptSerializer js = null;
            for (int r = 0; r < roots.Length; r++)
            {
                List<string> files = SafeFindFiles(roots[r], "*.json", true);
                for (int i = 0; i < files.Count; i++)
                {
                    string fp = files[i];
                    long fSize, fMtime;
                    if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                    TrackVisitedFile(fp);

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        _events.AddRange(cur.Events);
                        continue;
                    }

                    List<UsageEvent> fileEvents = new List<UsageEvent>();
                    DateTime mtime = FileTimeTicksToLocal(fMtime);
                    try
                    {
                        if (js == null) js = NewJson();
                        Dictionary<string, object> data = AsDict(js.DeserializeObject(ReadAllTextLong(fp)));
                        if (data != null)
                        {
                            IList msgs = data.ContainsKey("messages") ? (data["messages"] as IList) : null;
                            if (msgs != null)
                            {
                                for (int m = 0; m < msgs.Count; m++)
                                {
                                    string mdl, dt, did; long fi, outTok, cr, cw; double cost;
                                    if (TryExtractGenericUsage(AsDict(msgs[m]), "claude-sonnet-4-6", mtime, out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did))
                                        fileEvents.Add(new UsageEvent("Amp", mdl, dt, fi, outTok, cr, cw, cost, 1));
                                }
                            }
                        }
                    }
                    catch { }
                    SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                    _events.AddRange(fileEvents);
                }
            }
        }

        private void ScanGoose(string homeRoot)
        {
            string[] bases = new string[]
            {
                Path.Combine(homeRoot, ".local", "share", "goose", "sessions"),
                Path.Combine(homeRoot, "AppData", "Roaming", "Block", "goose", "data", "sessions")
            };
            for (int b = 0; b < bases.Length; b++)
            {
                ScanGenericJsonlTree("Goose", bases[b], "claude-sonnet-4-6", "*.jsonl");
                string sdb = Path.Combine(bases[b], "sessions.db");
                long cSize, cMtime;
                if (!TryGetSqliteCombinedStat(sdb, out cSize, out cMtime)) continue;
                TrackVisitedFile(sdb);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(sdb, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> dbEvents = new List<UsageEvent>();
                bool dbOpened = false;
                try
                {
                    using (WinSqlite db = WinSqlite.OpenReadOnly(sdb))
                    {
                        if (db != null)
                        {
                            dbOpened = true;
                            db.Query("SELECT input_tokens, output_tokens, created_at FROM sessions WHERE input_tokens + output_tokens > 0", delegate(WinSqlite.SqliteRow row)
                            {
                                long fi = row.GetInt64(0);
                                long outTok = row.GetInt64(1);
                                string ts = row.GetString(2);
                                if (fi + outTok > 0)
                                    dbEvents.Add(new UsageEvent("Goose", "claude-sonnet-4-6", ToLocalDate(ts, DateTime.Now), fi, outTok, 0L, 0L, 0.0, 1));
                            });
                        }
                    }
                }
                catch { }
                if (!dbOpened && cur != null && cur.Events != null)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }
                dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
                SaveDirtyCursor(sdb, cSize, cMtime, cSize, "", dbEvents);
                _events.AddRange(dbEvents);
            }
        }

        private void ScanCrush(string homeRoot)
        {
            string[] dbs = new string[]
            {
                Path.Combine(homeRoot, ".local", "share", "crush", "crush.db"),
                Path.Combine(homeRoot, "AppData", "Local", "crush", "crush.db")
            };
            for (int i = 0; i < dbs.Length; i++)
            {
                string cdb = dbs[i];
                long cSize, cMtime;
                if (!TryGetSqliteCombinedStat(cdb, out cSize, out cMtime)) continue;
                TrackVisitedFile(cdb);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(cdb, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> dbEvents = new List<UsageEvent>();
                bool dbOpened = false;
                try
                {
                    using (WinSqlite db = WinSqlite.OpenReadOnly(cdb))
                    {
                        if (db != null)
                        {
                            dbOpened = true;
                            db.Query("SELECT prompt_tokens, completion_tokens, cost, created_at FROM sessions WHERE prompt_tokens + completion_tokens > 0", delegate(WinSqlite.SqliteRow row)
                            {
                                long fi = row.GetInt64(0);
                                long outTok = row.GetInt64(1);
                                double cost = row.GetDouble(2);
                                long ts = row.GetInt64(3);
                                if (fi + outTok > 0)
                                    dbEvents.Add(new UsageEvent("Charm Crush", "claude-sonnet-4-6", ToLocalDate(ts, DateTime.Now), fi, outTok, 0L, 0L, cost, 1));
                            });
                        }
                    }
                }
                catch { }
                if (!dbOpened && cur != null && cur.Events != null)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }
                dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
                SaveDirtyCursor(cdb, cSize, cMtime, cSize, "", dbEvents);
                _events.AddRange(dbEvents);
            }
        }

        private void ScanAider(string homeRoot)
        {
            string[] candidates = new string[]
            {
                Path.Combine(homeRoot, ".aider", "analytics.jsonl"),
                Path.Combine(homeRoot, ".aider.analytics.jsonl")
            };
            JavaScriptSerializer js = null;
            for (int c = 0; c < candidates.Length; c++)
            {
                string fp = candidates[c];
                long fSize, fMtime;
                if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                TrackVisitedFile(fp);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> fileEvents = new List<UsageEvent>();
                DateTime mtime = FileTimeTicksToLocal(fMtime);
                try
                {
                    if (js == null) js = NewJson();
                    using (FileStream fs = new FileStream(ToLongPath(fp), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.IndexOf("tokens", StringComparison.Ordinal) < 0) continue;
                            Dictionary<string, object> obj;
                            try { obj = AsDict(js.DeserializeObject(line)); } catch { continue; }
                            if (obj == null) continue;
                            Dictionary<string, object> props = AsDict(obj.ContainsKey("properties") ? obj["properties"] : null) ?? obj;
                            long fi = GetLong(props, "prompt_tokens");
                            if (fi == 0) fi = GetLong(props, "sent_tokens");
                            long outTok = GetLong(props, "completion_tokens");
                            if (outTok == 0) outTok = GetLong(props, "received_tokens");
                            if (fi + outTok > 0)
                            {
                                string mdl = PricingEngine.NormalizeModelId(GetStr(props, "main_model") ?? GetStr(props, "model"), "claude-sonnet-4-6");
                                double cost = GetDouble(props, "cost");
                                if (cost <= 0) cost = GetDouble(props, "message_cost");
                                fileEvents.Add(new UsageEvent("Aider", mdl, ToLocalDate(obj.ContainsKey("time") ? obj["time"] : null, mtime), fi, outTok, 0L, 0L, cost, 1));
                            }
                        }
                    }
                }
                catch { }
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        private void ScanContinue(string homeRoot)
        {
            List<string> files = SafeFindFiles(Path.Combine(homeRoot, ".continue", "sessions"), "*.json", true);
            if (files.Count == 0) return;
            JavaScriptSerializer js = null;
            for (int i = 0; i < files.Count; i++)
            {
                string fp = files[i];
                long fSize, fMtime;
                if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                TrackVisitedFile(fp);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                {
                    _events.AddRange(cur.Events);
                    continue;
                }

                List<UsageEvent> fileEvents = new List<UsageEvent>();
                DateTime mtime = FileTimeTicksToLocal(fMtime);
                try
                {
                    if (js == null) js = NewJson();
                    Dictionary<string, object> data = AsDict(js.DeserializeObject(ReadAllTextLong(fp)));
                    if (data != null && data.ContainsKey("history"))
                    {
                        IList hist = data["history"] as IList;
                        if (hist != null)
                        {
                            for (int h = 0; h < hist.Count; h++)
                            {
                                Dictionary<string, object> hItem = AsDict(hist[h]);
                                if (hItem == null || !hItem.ContainsKey("promptLogs")) continue;
                                IList logs = hItem["promptLogs"] as IList;
                                if (logs == null) continue;
                                for (int l = 0; l < logs.Count; l++)
                                {
                                    string mdl, dt, did; long fi, outTok, cr, cw; double cost;
                                    if (TryExtractGenericUsage(AsDict(logs[l]), "claude-sonnet-4-6", mtime, out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did))
                                        fileEvents.Add(new UsageEvent("Continue.dev", mdl, dt, fi, outTok, cr, cw, cost, 1));
                                }
                            }
                        }
                    }
                }
                catch { }
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        private void ScanVibeAndAgents(string homeRoot)
        {
            ScanGenericJsonlTree("Mistral Vibe", Path.Combine(homeRoot, ".vibe", "logs", "session"), "devstral", "*.jsonl");
            ScanGenericJsonlTree("OpenHands", Path.Combine(homeRoot, ".openhands", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Hermes Agent", Path.Combine(homeRoot, ".hermes", "sessions"), "claude-sonnet-4-6", "*.jsonl");
        }

        private void ScanCursorNative()
        {
            string vscdb = Path.Combine(_home, "AppData", "Roaming", "Cursor", "User", "globalStorage", "state.vscdb");
            long cSize, cMtime;
            if (!TryGetSqliteCombinedStat(vscdb, out cSize, out cMtime)) return;
            TrackVisitedFile(vscdb);

            FileCursorEntry cur;
            if (_cursors.TryGetValue(vscdb, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
            {
                _events.AddRange(cur.Events);
                return;
            }

            Dictionary<string, string> oldDates = new Dictionary<string, string>(StringComparer.Ordinal);
            if (cur != null && cur.Events != null)
            {
                for (int i = 0; i < cur.Events.Count; i++)
                {
                    UsageEvent oe = cur.Events[i];
                    if (oe != null && !string.IsNullOrEmpty(oe.DedupKey) && !string.IsNullOrEmpty(oe.Date))
                        oldDates[oe.DedupKey] = oe.Date;
                }
            }

            List<UsageEvent> dbEvents = new List<UsageEvent>();
            DateTime mtime = FileTimeTicksToLocal(cMtime);
            try
            {
                using (WinSqlite db = WinSqlite.OpenReadOnly(vscdb))
                {
                    if (db == null)
                    {
                        if (cur != null && cur.Events != null) _events.AddRange(cur.Events);
                        return;
                    }
                    db.Query("SELECT key, value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND value LIKE '%tokenCount%'", delegate(WinSqlite.SqliteRow row)
                    {
                        string bKey = row.GetString(0);
                        string val = row.GetString(1);
                        if (string.IsNullOrEmpty(val)) return;
                        int tcStart, tcEnd;
                        if (!FastJson.FindObjectBounds(val, "\"tokenCount\"", 0, val.Length, out tcStart, out tcEnd))
                            return;
                        long fi = FastJson.ExtractLong(val, "\"inputTokens\"", tcStart, tcEnd);
                        long outTok = FastJson.ExtractLong(val, "\"outputTokens\"", tcStart, tcEnd);
                        if (fi + outTok > 0)
                        {
                            string rawM = null;
                            int miStart, miEnd;
                            if (FastJson.FindObjectBounds(val, "\"modelInfo\"", 0, val.Length, out miStart, out miEnd))
                                rawM = FastJson.ExtractString(val, "\"modelName\"", miStart, miEnd);
                            if (string.IsNullOrEmpty(rawM))
                                rawM = FastJson.ExtractString(val, "\"model\"", 0, val.Length);
                            string mdl = PricingEngine.NormalizeModelId(rawM, "claude-sonnet-4-6");
                            string cAt = FastJson.ExtractRawScalar(val, "\"createdAt\"", 0, val.Length);
                            string prevDate;
                            string dtStr;
                            if (!string.IsNullOrEmpty(cAt))
                                dtStr = ToLocalDate(cAt, mtime);
                            else if (!string.IsNullOrEmpty(bKey) && oldDates.TryGetValue(bKey, out prevDate))
                                dtStr = prevDate;
                            else
                                dtStr = ToLocalDate(null, mtime);
                            dbEvents.Add(new UsageEvent("Cursor", mdl, dtStr, fi, outTok, 0L, 0L, 0.0, 1, bKey));
                        }
                    });
                }
            }
            catch { }

            dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
            SaveDirtyCursor(vscdb, cSize, cMtime, cSize, "", dbEvents);
            _events.AddRange(dbEvents);
        }

        private void ScanZed()
        {
            string zdb = Path.Combine(_home, "AppData", "Local", "Zed", "threads", "threads.db");
            long cSize, cMtime;
            if (!TryGetSqliteCombinedStat(zdb, out cSize, out cMtime)) return;
            TrackVisitedFile(zdb);

            FileCursorEntry cur;
            if (_cursors.TryGetValue(zdb, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
            {
                _events.AddRange(cur.Events);
                return;
            }

            List<UsageEvent> dbEvents = new List<UsageEvent>();
            try
            {
                JavaScriptSerializer js = NewJson();
                using (WinSqlite db = WinSqlite.OpenReadOnly(zdb))
                {
                    if (db == null)
                    {
                        if (cur != null && cur.Events != null) _events.AddRange(cur.Events);
                        return;
                    }
                    bool hasIdCol = false;
                    db.Query("PRAGMA table_info(threads)", delegate(WinSqlite.SqliteRow colRow)
                    {
                        if (string.Equals(colRow.GetString(1), "id", StringComparison.OrdinalIgnoreCase))
                            hasIdCol = true;
                    });

                    string zSql = hasIdCol ? "SELECT id, updated_at, data FROM threads" : "SELECT updated_at, data FROM threads";
                    db.Query(zSql, delegate(WinSqlite.SqliteRow row)
                    {
                        string rowId = hasIdCol ? row.GetString(0) : "";
                        string upd = row.GetString(hasIdCol ? 1 : 0);
                        string ds = row.GetString(hasIdCol ? 2 : 1);
                        if (string.IsNullOrEmpty(ds) || (ds.IndexOf("token_usage", StringComparison.Ordinal) < 0 && ds.IndexOf("input_tokens", StringComparison.Ordinal) < 0))
                            return;
                        try
                        {
                            Dictionary<string, object> d = AsDict(js.DeserializeObject(ds));
                            if (d == null) return;
                            Dictionary<string, object> u = AsDict(d.ContainsKey("cumulative_token_usage") ? d["cumulative_token_usage"] : (d.ContainsKey("token_usage") ? d["token_usage"] : null));
                            if (u == null) return;
                            long fi = GetLong(u, "input_tokens");
                            long outTok = GetLong(u, "output_tokens");
                            long cr = GetLong(u, "cache_read_input_tokens");
                            long cw = GetLong(u, "cache_creation_input_tokens");
                            if (fi + outTok + cr + cw > 0)
                            {
                                Dictionary<string, object> mObj = AsDict(d.ContainsKey("model") ? d["model"] : null);
                                string mdl = PricingEngine.NormalizeModelId(GetStr(mObj, "name") ?? GetStr(d, "model"), "claude-sonnet-4-6");
                                string tid = !string.IsNullOrEmpty(rowId) ? rowId : (GetStr(d, "id") ?? GetStr(d, "thread_id") ?? "");
                                dbEvents.Add(new UsageEvent("Zed AI", mdl, ToLocalDate(upd, DateTime.Now), fi, outTok, cr, cw, 0.0, 1, tid));
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }

            dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
            SaveDirtyCursor(zdb, cSize, cMtime, cSize, "", dbEvents);
            _events.AddRange(dbEvents);
        }

        private void ScanCustomRules(string homeRoot)
        {
            string cfgPath = Path.Combine(homeRoot, ".toklens", "harnesses.json");
            try
            {
                if (!File.Exists(cfgPath)) return;
                JavaScriptSerializer js = NewJson();
                IList rules = js.DeserializeObject(ReadAllTextLong(cfgPath)) as IList;
                if (rules == null) return;
                for (int i = 0; i < rules.Count; i++)
                {
                    Dictionary<string, object> r = AsDict(rules[i]);
                    if (r == null) continue;
                    string name = GetStr(r, "name") ?? "Custom Harness";
                    string dir = GetStr(r, "dir");
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (dir.StartsWith("~")) dir = homeRoot + dir.Substring(1);
                    string pat = GetStr(r, "pattern") ?? "*.jsonl";
                    string defM = GetStr(r, "default_model") ?? "claude-sonnet-4-6";
                    ScanGenericJsonlTree(name, dir, defM, pat);
                }
            }
            catch { }
        }

        private void ScanLocalWslHomes()
        {
            try
            {
                string wslRoot = @"\\wsl.localhost";
                if (!Directory.Exists(wslRoot)) return;
                string[] distros = Directory.GetDirectories(wslRoot);
                for (int d = 0; d < distros.Length; d++)
                {
                    string homeBase = Path.Combine(distros[d], "home");
                    if (!Directory.Exists(homeBase)) continue;
                    string[] userHomes = Directory.GetDirectories(homeBase);
                    for (int u = 0; u < userHomes.Length; u++)
                    {
                        string wh = userHomes[u];
                        ScanGenericJsonlTree("Claude Code (WSL)", Path.Combine(wh, ".claude", "projects"), "claude-sonnet-4-6", "*.jsonl");
                        ScanOpenClaw(wh);
                        ScanPiAndOmp(wh);
                        ScanQwenCode(wh);
                        ScanFactoryDroid(wh);
                        ScanAmp(wh);
                        ScanGoose(wh);
                        ScanAider(wh);
                    }
                }
            }
            catch { }
        }

        // -------------------- 13. Native Codex SQLite Archive (~/.codex/state_5.sqlite only) --------------------
        private void ScanCodexSqliteArchive()
        {
            string stDb = Path.Combine(_home, ".codex", "state_5.sqlite");
            long cSize, cMtime;
            if (!TryGetSqliteCombinedStat(stDb, out cSize, out cMtime)) return;
            TrackVisitedFile(stDb);

            FileCursorEntry cur;
            List<UsageEvent> rawThreads;
            if (_cursors.TryGetValue(stDb, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
            {
                rawThreads = cur.Events ?? new List<UsageEvent>();
            }
            else
            {
                List<UsageEvent> freshThreads = new List<UsageEvent>();
                bool dbOpened = false;
                try
                {
                    using (WinSqlite db = WinSqlite.OpenReadOnly(stDb))
                    {
                        if (db != null)
                        {
                            dbOpened = true;
                            db.Query("SELECT id, model, tokens_used, created_at FROM threads WHERE tokens_used > 0", delegate(WinSqlite.SqliteRow row)
                            {
                                string tid = row.GetString(0);
                                string model = row.GetString(1);
                                long tot = row.GetInt64(2);
                                long createdAt = row.GetInt64(3);
                                if (tot <= 0) return;

                                long cr = (long)(tot * 0.963);
                                long outTok = Math.Max(1L, (long)(tot * 0.004));
                                long fi = Math.Max(0L, tot - cr - outTok);
                                string mdl = PricingEngine.NormalizeModelId(model, "gpt-6-astra");
                                freshThreads.Add(new UsageEvent("OpenAI Codex", mdl, ToLocalDate(createdAt, DateTime.Now), fi, outTok, cr, 0L, 0.0, 1, tid, 0L, -1));
                            });
                        }
                    }
                }
                catch { }

                if (!dbOpened && cur != null && cur.Events != null)
                {
                    _codexArchiveCandidates.AddRange(cur.Events);
                    return;
                }

                rawThreads = MergeSqliteCursorEvents(cur, freshThreads);
                SaveDirtyCursor(stDb, cSize, cMtime, cSize, "", rawThreads);
            }

            if (rawThreads != null && rawThreads.Count > 0)
                _codexArchiveCandidates.AddRange(rawThreads);
        }

        private struct ScoredRecentEvent
        {
            public UsageEvent Ev;
            public double CostUsd;
        }

        private ScanResult BuildResult(double elapsedSec)
        {
            Dictionary<string, RecordBucket> agg = new Dictionary<string, RecordBucket>(StringComparer.Ordinal);
            List<ScoredRecentEvent> liveCandidates = new List<ScoredRecentEvent>(Math.Min(_events.Count, 8192));
            string latestDate = "";

            for (int i = 0; i < _events.Count; i++)
            {
                UsageEvent ev = _events[i];
                double actualUsd, baseUsd;
                ModelRate rate;
                _pricing.ComputeCost(ev.Model, ev.FreshInput, ev.Output, ev.CacheRead, ev.CacheWrite, ev.LoggedCostUsd, out actualUsd, out baseUsd, out rate);

                string key = ev.Tool + "||" + ev.Model + "||" + ev.Date;
                RecordBucket b;
                if (!agg.TryGetValue(key, out b))
                {
                    b = new RecordBucket();
                    b.Tool = ev.Tool;
                    b.Model = ev.Model;
                    b.Date = ev.Date;
                    b.RateLabel = rate.Label;
                    agg[key] = b;
                }
                b.Turns += ev.Turns;
                b.FreshInput += ev.FreshInput;
                b.Output += ev.Output;
                b.CacheRead += ev.CacheRead;
                b.CacheWrite += ev.CacheWrite;
                b.CostUsd += actualUsd;
                b.BaselineUsd += baseUsd;

                if (ev.Date != "未知日期" && string.CompareOrdinal(ev.Date, latestDate) > 0)
                    latestDate = ev.Date;

                if (ev.TimestampMs > 0L && ev.DurationMs >= 0 && ev.Turns == 1)
                {
                    ScoredRecentEvent sre = new ScoredRecentEvent();
                    sre.Ev = ev;
                    sre.CostUsd = actualUsd;
                    liveCandidates.Add(sre);
                }
            }

            liveCandidates.Sort(delegate(ScoredRecentEvent a, ScoredRecentEvent b)
            {
                return b.Ev.TimestampMs.CompareTo(a.Ev.TimestampMs);
            });

            List<RecentRequestRecord> recentList = new List<RecentRequestRecord>(Math.Min(liveCandidates.Count, 500));
            int recCount = Math.Min(liveCandidates.Count, 500);
            for (int i = 0; i < recCount; i++)
            {
                UsageEvent src = liveCandidates[i].Ev;
                RecentRequestRecord rr = new RecentRequestRecord();
                rr.TimestampMs = src.TimestampMs;
                rr.Date = src.Date;
                rr.TimeLabel = FormatEpochTimeLabel(src.TimestampMs);
                rr.Tool = src.Tool;
                rr.Model = src.Model;
                rr.FreshInput = src.FreshInput;
                rr.CacheRead = src.CacheRead;
                rr.CacheWrite = src.CacheWrite;
                rr.Output = src.Output;
                rr.DurationMs = src.DurationMs;
                rr.CostUsd = liveCandidates[i].CostUsd;
                recentList.Add(rr);
            }

            List<RecordBucket> list = new List<RecordBucket>(agg.Values);
            list.Sort(delegate(RecordBucket x, RecordBucket y)
            {
                int c = string.CompareOrdinal(x.Date, y.Date);
                if (c != 0) return c;
                c = string.CompareOrdinal(x.Tool, y.Tool);
                if (c != 0) return c;
                return string.CompareOrdinal(x.Model, y.Model);
            });

            LocalUsageStore.MergeAndSaveDailyArchive(_home, list);
            for (int i = 0; i < list.Count; i++)
            {
                string d = list[i].Date;
                if (!string.IsNullOrEmpty(d) && d != "未知日期" && string.CompareOrdinal(d, latestDate) > 0)
                    latestDate = d;
            }

            ScanResult res = new ScanResult();
            res.UserHome = _home;
            res.GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            res.ReferenceDate = string.IsNullOrEmpty(latestDate) ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : latestDate;
            res.PricingModelCount = _pricing.Count;
            res.ElapsedSeconds = elapsedSec;
            res.ChangedFilesCount = _dirtyCursors.Count;
            res.TotalFilesTracked = _totalTrackedFiles;
            res.ArchivedFilesCount = _archivedFilesCount;
            res.Records = list;
            res.RecentRequests = recentList;
            res.Warnings.AddRange(_warnings);
            return res;
        }
    }

    public static class FormatterUtil
    {
        public static string FormatTokens(long n)
        {
            if (n <= 0) return "0";
            if (n >= 1000000000L) return (n / 1e9).ToString("F2", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000L) return (n / 1e6).ToString("F2", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000L) return (n / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        public static string FormatMoney(double usd, bool isCny, double rate)
        {
            double val = isCny ? (usd * rate) : usd;
            string sym = isCny ? "¥" : "$";
            if (val >= 100.0) return sym + val.ToString("N2", CultureInfo.InvariantCulture);
            if (val >= 1.0) return sym + val.ToString("F2", CultureInfo.InvariantCulture);
            if (val >= 0.01) return sym + val.ToString("F3", CultureInfo.InvariantCulture);
            return sym + val.ToString("F4", CultureInfo.InvariantCulture);
        }

        public static string FormatDuration(int durationMs)
        {
            if (durationMs < 150 || durationMs > 600000) return "—";
            if (durationMs < 1000) return durationMs.ToString(CultureInfo.InvariantCulture) + "ms";
            return (durationMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "s";
        }

        public static double ComputeTps(long outputTokens, int durationMs)
        {
            if (outputTokens <= 0 || durationMs < 200 || durationMs > 600000) return 0.0;
            double tps = outputTokens * 1000.0 / durationMs;
            if (tps < 0.1 || tps > 2000.0) return 0.0;
            return tps;
        }

        public static string FormatTps(double tps)
        {
            if (tps <= 0.0) return "—";
            return tps.ToString("F1", CultureInfo.InvariantCulture) + " tok/s";
        }

        public static void ExportCsv(ScanResult res, double rate, string outPath)
        {
            ExportCsv(res != null ? res.Records : new List<RecordBucket>(), rate, outPath);
        }

        public static void ExportCsv(List<RecordBucket> rows, double rate, string outPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("日期,所属设备,AI工具,模型名称,调用次数,纯新增输入Token,缓存读取Token,缓存写入Token,输出Token,总Token,单价($/M In/Out),等效美元($),等效人民币(¥)");
            for (int i = 0; i < rows.Count; i++)
            {
                RecordBucket r = rows[i];
                string mName = string.IsNullOrEmpty(r.MachineName) ? "本机" : r.MachineName;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "\"{0}\",\"{1}\",\"{2}\",\"{3}\",{4},{5},{6},{7},{8},{9},\"{10}\",{11:F4},{12:F4}\r\n",
                    r.Date, mName.Replace("\"", "\"\""), r.Tool.Replace("\"", "\"\""), r.Model.Replace("\"", "\"\""),
                    r.Turns, r.FreshInput, r.CacheRead, r.CacheWrite, r.Output, r.TotalTokens,
                    r.RateLabel, r.CostUsd, r.CostUsd * rate);
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }

        public static void ExportSummaryJson(ScanResult res, double rate, string outPath)
        {
            ExportSummaryJson(res, rate, "默认/自定义", outPath);
        }

        public static void ExportSummaryJson(ScanResult res, double rate, string rateSource, string outPath)
        {
            long totFi = 0, totOut = 0, totCr = 0, totCw = 0;
            int totTurns = 0;
            double totCost = 0, totBase = 0;
            Dictionary<string, ToolRowView> toolMap = new Dictionary<string, ToolRowView>(StringComparer.Ordinal);
            for (int i = 0; i < res.Records.Count; i++)
            {
                RecordBucket r = res.Records[i];
                totTurns += r.Turns;
                totFi += r.FreshInput;
                totOut += r.Output;
                totCr += r.CacheRead;
                totCw += r.CacheWrite;
                totCost += r.CostUsd;
                totBase += r.BaselineUsd;

                ToolRowView tv;
                if (!toolMap.TryGetValue(r.Tool, out tv))
                {
                    tv = new ToolRowView();
                    tv.Tool = r.Tool;
                    toolMap[r.Tool] = tv;
                }
                tv.Turns += r.Turns;
                tv.FreshInput += r.FreshInput;
                tv.CacheRead += r.CacheRead;
                tv.CacheWrite += r.CacheWrite;
                tv.Output += r.Output;
                tv.TotalTokens += r.TotalTokens;
                tv.CostUsd += r.CostUsd;
            }

            List<ToolRowView> sortedTools = new List<ToolRowView>(toolMap.Values);
            sortedTools.Sort(delegate(ToolRowView a, ToolRowView b) { return b.CostUsd.CompareTo(a.CostUsd); });
            List<Dictionary<string, object>> byTool = new List<Dictionary<string, object>>();
            for (int i = 0; i < sortedTools.Count; i++)
            {
                ToolRowView t = sortedTools[i];
                Dictionary<string, object> td = new Dictionary<string, object>();
                td["tool"] = t.Tool;
                td["turns"] = t.Turns;
                td["total_tokens"] = t.TotalTokens;
                td["cost_usd"] = Math.Round(t.CostUsd, 4);
                byTool.Add(td);
            }

            List<Dictionary<string, object>> recentSample = new List<Dictionary<string, object>>();
            if (res.RecentRequests != null)
            {
                int sampleN = Math.Min(res.RecentRequests.Count, 12);
                for (int i = 0; i < sampleN; i++)
                {
                    RecentRequestRecord rq = res.RecentRequests[i];
                    double tps = ComputeTps(rq.Output, rq.DurationMs);
                    Dictionary<string, object> rd = new Dictionary<string, object>();
                    rd["time"] = rq.TimeLabel;
                    rd["tool"] = rq.Tool;
                    rd["model"] = rq.Model;
                    rd["fresh_input"] = rq.FreshInput;
                    rd["cache_read"] = rq.CacheRead;
                    rd["output"] = rq.Output;
                    rd["duration_ms"] = rq.DurationMs;
                    rd["tps"] = Math.Round(tps, 1);
                    rd["cost_cny"] = Math.Round(rq.CostUsd * rate, 4);
                    recentSample.Add(rd);
                }
            }

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["app"] = "知微 TokLens v2.1";
            root["generated_at"] = res.GeneratedAt;
            root["elapsed_seconds"] = Math.Round(res.ElapsedSeconds, 4);
            root["changed_files_count"] = res.ChangedFilesCount;
            root["total_files_tracked"] = res.TotalFilesTracked;
            root["archived_files_count"] = res.ArchivedFilesCount;
            root["recent_requests_count"] = res.RecentRequests != null ? res.RecentRequests.Count : 0;
            root["user_home"] = res.UserHome;
            root["pricing_model_count"] = res.PricingModelCount;
            root["tool_count"] = toolMap.Count;
            root["turns"] = totTurns;
            root["fresh_input"] = totFi;
            root["output"] = totOut;
            root["cache_read"] = totCr;
            root["cache_write"] = totCw;
            root["total_tokens"] = totFi + totOut + totCr + totCw;
            root["cost_usd"] = Math.Round(totCost, 4);
            root["exchange_rate"] = Math.Round(rate, 4);
            root["exchange_rate_source"] = rateSource ?? "";
            root["cost_cny"] = Math.Round(totCost * rate, 2);
            root["saved_usd"] = Math.Round(Math.Max(0.0, totBase - totCost), 4);
            root["by_tool"] = byTool;
            root["recent_requests_sample"] = recentSample;
            root["warnings"] = res.Warnings;

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            File.WriteAllText(outPath, js.Serialize(root), new UTF8Encoding(false));
        }
    }
}
