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
using Microsoft.Win32;

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

        public FileCursorEntry Clone()
        {
            FileCursorEntry c = new FileCursorEntry();
            c.FileKey = FileKey;
            c.FileSize = FileSize;
            c.MtimeTicks = MtimeTicks;
            c.ByteOffset = ByteOffset;
            c.ExtraMeta = ExtraMeta;
            if (Events != null)
            {
                c.Events = new List<UsageEvent>(Events.Count);
                for (int i = 0; i < Events.Count; i++)
                {
                    UsageEvent ev = Events[i];
                    c.Events.Add(ev != null ? ev.Clone() : null);
                }
            }
            else
            {
                c.Events = new List<UsageEvent>();
            }
            return c;
        }
    }

    // Persistent SQLite Cursor & Instant-Snapshot Store (%APPDATA%\TokLens\toklens.db)
    public static class LocalUsageStore
    {
        private const string StoreSchema = "toklens.store.v2.7";
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

        private static Dictionary<string, FileCursorEntry> CloneCursorMap(Dictionary<string, FileCursorEntry> src)
        {
            Dictionary<string, FileCursorEntry> copy = new Dictionary<string, FileCursorEntry>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return copy;
            foreach (KeyValuePair<string, FileCursorEntry> kv in src)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    copy[kv.Key] = kv.Value.Clone();
            }
            return copy;
        }

        private static bool InitializeDbTablesAndSchema(WinSqlite db)
        {
            if (db == null || !db.IsHealthy()) return false;

            if (!db.Exec("CREATE TABLE IF NOT EXISTS kv_meta (k TEXT PRIMARY KEY, v TEXT);")) return false;
            if (!db.Exec("CREATE TABLE IF NOT EXISTS file_cursors (file_key TEXT PRIMARY KEY, file_size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, byte_offset INTEGER NOT NULL, extra_meta TEXT, events_data TEXT);")) return false;
            if (!db.Exec("CREATE TABLE IF NOT EXISTS daily_archive (home_key TEXT NOT NULL, rec_date TEXT NOT NULL, tool TEXT NOT NULL, model TEXT NOT NULL, turns INTEGER NOT NULL, fi INTEGER NOT NULL, out_tok INTEGER NOT NULL, cr INTEGER NOT NULL, cw INTEGER NOT NULL, cost_usd REAL NOT NULL, base_usd REAL NOT NULL, rate_label TEXT, PRIMARY KEY(home_key, rec_date, tool, model));")) return false;

            string curVer = "";
            db.Query("SELECT v FROM kv_meta WHERE k = 'schema' LIMIT 1", delegate(WinSqlite.SqliteRow r)
            {
                curVer = r.GetString(0);
            });

            if (!string.Equals(curVer, StoreSchema, StringComparison.Ordinal))
            {
                // Preserve daily_archive permanently and preserve cursors of already-deleted files;
                // reset cursors of existing live files so updated parsers re-scan cleanly.
                List<string> liveKeys = new List<string>();
                try
                {
                    db.Query("SELECT file_key FROM file_cursors", delegate(WinSqlite.SqliteRow r)
                    {
                        string fk = r.GetString(0);
                        if (!string.IsNullOrEmpty(fk))
                        {
                            long dSz, dMt;
                            if (TokenScanner.TryGetFileStatInternal(fk, out dSz, out dMt))
                                liveKeys.Add(fk);
                        }
                    });
                }
                catch { }

                db.Exec("BEGIN IMMEDIATE;");
                try
                {
                    if (liveKeys.Count > 0)
                    {
                        for (int i = 0; i < liveKeys.Count; i++)
                            db.Exec("DELETE FROM file_cursors WHERE file_key = " + WinSqlite.SqlQuote(liveKeys[i]) + ";");
                    }
                    else
                    {
                        db.Exec("DELETE FROM file_cursors;");
                    }
                    db.Exec("DELETE FROM daily_archive WHERE tool IN ('Google Antigravity', 'OpenAI Codex');");
                    db.Exec("DELETE FROM file_cursors WHERE file_key LIKE '%state_5.sqlite';");
                    db.Exec("DELETE FROM kv_meta;");
                    db.Exec("INSERT OR REPLACE INTO kv_meta (k, v) VALUES ('schema', " + WinSqlite.SqlQuote(StoreSchema) + ");");
                    db.Exec("COMMIT;");
                }
                catch
                {
                    db.Exec("ROLLBACK;");
                    db.Exec("DELETE FROM file_cursors;");
                    db.Exec("DELETE FROM daily_archive WHERE tool IN ('Google Antigravity', 'OpenAI Codex');");
                    db.Exec("DELETE FROM kv_meta;");
                    db.Exec("INSERT OR REPLACE INTO kv_meta (k, v) VALUES ('schema', " + WinSqlite.SqlQuote(StoreSchema) + ");");
                }
            }
            return true;
        }

        private static void BackupCorruptDbFiles(string dbPath)
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    string bakPath = dbPath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    try
                    {
                        File.Move(dbPath, bakPath);
                    }
                    catch
                    {
                        try { File.Delete(dbPath); } catch { }
                    }
                }
            }
            catch { }
            try { if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal"); } catch { }
            try { if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm"); } catch { }
        }

        private static WinSqlite OpenInitializedDb()
        {
            string dbPath = DbFilePath;
            WinSqlite db = WinSqlite.OpenReadWrite(dbPath);
            if (db != null && InitializeDbTablesAndSchema(db))
                return db;

            if (db != null)
            {
                db.Dispose();
                db = null;
            }

            // Self-heal corrupted local store by backing up damaged DB + WAL/SHM and creating a fresh database
            BackupCorruptDbFiles(dbPath);
            _memCursors = null;

            db = WinSqlite.OpenReadWrite(dbPath);
            if (db != null && InitializeDbTablesAndSchema(db))
                return db;

            if (db != null)
                db.Dispose();
            return null;
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
                    return CloneCursorMap(_memCursors);
                }

                Dictionary<string, FileCursorEntry> map = new Dictionary<string, FileCursorEntry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!File.Exists(DbFilePath))
                    {
                        _memCursors = map;
                        return CloneCursorMap(map);
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
                return CloneCursorMap(map);
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
                    {
                        FileCursorEntry dc = dirtyCursors[i];
                        if (dc != null && !string.IsNullOrEmpty(dc.FileKey))
                            _memCursors[dc.FileKey] = dc.Clone();
                    }
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
                  .Append(e.DurationMs.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.CacheWrite1h.ToString(CultureInfo.InvariantCulture));
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
                        long fi, outTok, cr, cw, tsMs = 0L, cw1h = 0L;
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
                        if (p.Length >= 13) long.TryParse(p[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out cw1h);
                        list.Add(new UsageEvent(p[0], p[1], p[2], fi, outTok, cr, cw, cost, turns, dkey, tsMs, durMs, cw1h));
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
                        List<RecordBucket> toDelete = new List<RecordBucket>();

                        // 检测同一 (Date, Tool) 下是否发生了“模型重分类”（总轮次与总 Token >= 归档 98%，但旧错分类模型缩水或被替代）
                        Dictionary<string, int> liveDtTurns = new Dictionary<string, int>(StringComparer.Ordinal);
                        Dictionary<string, long> liveDtTokens = new Dictionary<string, long>(StringComparer.Ordinal);
                        Dictionary<string, RecordBucket> liveByKey = new Dictionary<string, RecordBucket>(StringComparer.Ordinal);
                        for (int i = 0; i < liveBuckets.Count; i++)
                        {
                            RecordBucket lb = liveBuckets[i];
                            string lk = (lb.Date ?? "") + "||" + (lb.Tool ?? "") + "||" + (lb.Model ?? "");
                            liveByKey[lk] = lb;
                            string dtTool = (lb.Date ?? "") + "||" + (lb.Tool ?? "");
                            int curT;
                            liveDtTurns.TryGetValue(dtTool, out curT);
                            liveDtTurns[dtTool] = curT + lb.Turns;
                            long curTok;
                            liveDtTokens.TryGetValue(dtTool, out curTok);
                            liveDtTokens[dtTool] = curTok + lb.FreshInput + lb.Output + lb.CacheRead + lb.CacheWrite;
                        }

                        Dictionary<string, int> archDtTurns = new Dictionary<string, int>(StringComparer.Ordinal);
                        Dictionary<string, long> archDtTokens = new Dictionary<string, long>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, RecordBucket> kv in archivedMap)
                        {
                            RecordBucket ab = kv.Value;
                            string dtTool = (ab.Date ?? "") + "||" + (ab.Tool ?? "");
                            int curT;
                            archDtTurns.TryGetValue(dtTool, out curT);
                            archDtTurns[dtTool] = curT + ab.Turns;
                            long curTok;
                            archDtTokens.TryGetValue(dtTool, out curTok);
                            archDtTokens[dtTool] = curTok + ab.FreshInput + ab.Output + ab.CacheRead + ab.CacheWrite;
                        }

                        HashSet<string> reclassifiedDtTools = new HashSet<string>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, int> kv in archDtTurns)
                        {
                            string dtTool = kv.Key;
                            int hTurns = kv.Value;
                            if (hTurns <= 0) continue;
                            int lTurns;
                            liveDtTurns.TryGetValue(dtTool, out lTurns);
                            long hTok;
                            archDtTokens.TryGetValue(dtTool, out hTok);
                            long lTok;
                            liveDtTokens.TryGetValue(dtTool, out lTok);
                            bool isAgyWithLegacyModels = false;
                            if (lTurns > 0 && dtTool.EndsWith("||Google Antigravity", StringComparison.Ordinal))
                            {
                                foreach (KeyValuePair<string, RecordBucket> akv in archivedMap)
                                {
                                    RecordBucket ab = akv.Value;
                                    if (((ab.Date ?? "") + "||" + (ab.Tool ?? "")) != dtTool) continue;
                                    if (string.Equals(ab.Model, "gemini-2.5-pro", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(ab.Model, "orchestrator", StringComparison.OrdinalIgnoreCase))
                                    {
                                        RecordBucket lbMatch;
                                        if (!liveByKey.TryGetValue(akv.Key, out lbMatch) || lbMatch.Turns < ab.Turns)
                                        {
                                            isAgyWithLegacyModels = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            if (isAgyWithLegacyModels)
                            {
                                reclassifiedDtTools.Add(dtTool);
                            }
                            else if (lTurns >= hTurns && (double)lTok >= (double)hTok * 0.98)
                            {
                                bool hasExcessArchModel = false;
                                foreach (KeyValuePair<string, RecordBucket> akv in archivedMap)
                                {
                                    RecordBucket ab = akv.Value;
                                    if (((ab.Date ?? "") + "||" + (ab.Tool ?? "")) != dtTool) continue;
                                    RecordBucket lbMatch;
                                    if (!liveByKey.TryGetValue(akv.Key, out lbMatch) ||
                                        ab.Turns > lbMatch.Turns ||
                                        (ab.FreshInput + ab.Output + ab.CacheRead + ab.CacheWrite) > (lbMatch.FreshInput + lbMatch.Output + lbMatch.CacheRead + lbMatch.CacheWrite))
                                    {
                                        hasExcessArchModel = true;
                                        break;
                                    }
                                }
                                if (hasExcessArchModel)
                                    reclassifiedDtTools.Add(dtTool);
                            }
                        }

                        for (int i = 0; i < liveBuckets.Count; i++)
                        {
                            RecordBucket lb = liveBuckets[i];
                            string lk = (lb.Date ?? "") + "||" + (lb.Tool ?? "") + "||" + (lb.Model ?? "");
                            string dtTool = (lb.Date ?? "") + "||" + (lb.Tool ?? "");
                            seenLiveKeys.Add(lk);

                            RecordBucket ab;
                            if (!archivedMap.TryGetValue(lk, out ab))
                            {
                                toUpsert.Add(lb);
                            }
                            else if (reclassifiedDtTools.Contains(dtTool))
                            {
                                // 同一 (Date, Tool) 已确认发生模型重分类：以活跃拆分结果为准覆盖归档，禁止旧桶叠加导致翻倍
                                bool changedRec = lb.Turns != ab.Turns ||
                                                  lb.FreshInput != ab.FreshInput ||
                                                  lb.Output != ab.Output ||
                                                  lb.CacheRead != ab.CacheRead ||
                                                  lb.CacheWrite != ab.CacheWrite ||
                                                  Math.Abs(lb.CostUsd - ab.CostUsd) > 1e-9 ||
                                                  Math.Abs(lb.BaselineUsd - ab.BaselineUsd) > 1e-9 ||
                                                  !string.Equals(lb.RateLabel ?? "", ab.RateLabel ?? "", StringComparison.Ordinal);
                                if (changedRec)
                                    toUpsert.Add(lb);
                            }
                            else
                            {
                                bool liveCoversArchive =
                                    lb.Turns >= ab.Turns &&
                                    lb.FreshInput >= ab.FreshInput &&
                                    lb.Output >= ab.Output &&
                                    lb.CacheRead >= ab.CacheRead &&
                                    lb.CacheWrite >= ab.CacheWrite;

                                int mergedTurns = Math.Max(lb.Turns, ab.Turns);
                                long mergedFi = Math.Max(lb.FreshInput, ab.FreshInput);
                                long mergedOut = Math.Max(lb.Output, ab.Output);
                                long mergedCr = Math.Max(lb.CacheRead, ab.CacheRead);
                                long mergedCw = Math.Max(lb.CacheWrite, ab.CacheWrite);

                                double mergedCost;
                                double mergedBase;
                                if (liveCoversArchive)
                                {
                                    // Live scan covers all archived tokens (no deleted logs): adopt latest recalculated cost/rate
                                    mergedCost = lb.CostUsd;
                                    mergedBase = lb.BaselineUsd;
                                }
                                else
                                {
                                    // Logs were pruned/deleted: retain historical high-water mark cost
                                    mergedCost = Math.Max(lb.CostUsd, ab.CostUsd);
                                    mergedBase = Math.Max(lb.BaselineUsd, ab.BaselineUsd);
                                }

                                string mergedRate = !string.IsNullOrEmpty(lb.RateLabel) ? lb.RateLabel : (ab.RateLabel ?? "");

                                bool changed = mergedTurns != ab.Turns ||
                                               mergedFi != ab.FreshInput ||
                                               mergedOut != ab.Output ||
                                               mergedCr != ab.CacheRead ||
                                               mergedCw != ab.CacheWrite ||
                                               Math.Abs(mergedCost - ab.CostUsd) > 1e-9 ||
                                               Math.Abs(mergedBase - ab.BaselineUsd) > 1e-9 ||
                                               !string.Equals(mergedRate, ab.RateLabel ?? "", StringComparison.Ordinal);

                                lb.Turns = mergedTurns;
                                lb.FreshInput = mergedFi;
                                lb.Output = mergedOut;
                                lb.CacheRead = mergedCr;
                                lb.CacheWrite = mergedCw;
                                lb.CostUsd = mergedCost;
                                lb.BaselineUsd = mergedBase;
                                lb.RateLabel = mergedRate;

                                if (changed)
                                    toUpsert.Add(lb);
                            }
                        }

                        bool addedMissing = false;
                        foreach (KeyValuePair<string, RecordBucket> kv in archivedMap)
                        {
                            if (!seenLiveKeys.Contains(kv.Key))
                            {
                                RecordBucket ab = kv.Value;
                                string dtTool = (ab.Date ?? "") + "||" + (ab.Tool ?? "");
                                if (reclassifiedDtTools.Contains(dtTool))
                                {
                                    // 被模型重分类淘汰的历史错误模型行：从 liveBuckets 剔除并自 daily_archive 清理
                                    toDelete.Add(ab);
                                }
                                else
                                {
                                    liveBuckets.Add(ab);
                                    addedMissing = true;
                                }
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

                        if (toUpsert.Count > 0 || toDelete.Count > 0)
                        {
                            db.Exec("BEGIN IMMEDIATE;");
                            try
                            {
                                for (int i = 0; i < toDelete.Count; i++)
                                {
                                    RecordBucket dbkt = toDelete[i];
                                    string delSql = string.Format(
                                        CultureInfo.InvariantCulture,
                                        "DELETE FROM daily_archive WHERE home_key = {0} AND rec_date = {1} AND tool = {2} AND model = {3};",
                                        WinSqlite.SqlQuote(homeKey),
                                        WinSqlite.SqlQuote(dbkt.Date ?? ""),
                                        WinSqlite.SqlQuote(dbkt.Tool ?? ""),
                                        WinSqlite.SqlQuote(dbkt.Model ?? ""));
                                    db.Exec(delSql);
                                }
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
                    {
                        if (lv <= 0L) return 0L;
                        if (lv > 1000000000000000L) return 1000000000000000L;
                        return lv;
                    }
                }
                double dv;
                if (double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                {
                    if (double.IsNaN(dv) || double.IsInfinity(dv) || dv <= 0.0) return 0L;
                    if (dv >= 1000000000000000.0) return 1000000000000000L;
                    return (long)dv;
                }
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
                {
                    if (double.IsNaN(dv) || double.IsInfinity(dv) || dv <= 0.0) return 0.0;
                    if (dv > 1000000000.0) return 1000000000.0;
                    return dv;
                }
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

        private static bool IsBalancedJsonObject(byte[] buf, int len)
        {
            int start = 0;
            while (start < len && (buf[start] == (byte)' ' || buf[start] == (byte)'\t' || buf[start] == (byte)'\r' || buf[start] == (byte)'\n'))
                start++;
            int end = len;
            while (end > start && (buf[end - 1] == (byte)' ' || buf[end - 1] == (byte)'\t' || buf[end - 1] == (byte)'\r' || buf[end - 1] == (byte)'\n'))
                end--;

            if (end - start < 2 || buf[start] != (byte)'{' || buf[end - 1] != (byte)'}')
                return false;

            int depth = 0;
            bool inStr = false;
            for (int i = start; i < end; i++)
            {
                byte b = buf[i];
                if (inStr)
                {
                    if (b == (byte)'\\')
                    {
                        i++;
                        if (i >= end) return false;
                        continue;
                    }
                    if (b == (byte)'"')
                        inStr = false;
                }
                else
                {
                    if (b == (byte)'"')
                    {
                        inStr = true;
                    }
                    else if (b == (byte)'{')
                    {
                        depth++;
                    }
                    else if (b == (byte)'}')
                    {
                        depth--;
                        if (depth < 0) return false;
                        if (depth == 0 && i != end - 1) return false;
                    }
                }
            }
            return !inStr && depth == 0;
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
            ScanFromOffset(openPath, startOffset, initialLineIndex, 0L, null, anyKeywords, onLine, out validEndOffset, out finalLineIndex);
        }

        public static void ScanFromOffset(
            string openPath,
            long startOffset,
            int initialLineIndex,
            long mtimeTicks,
            Action onReset,
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
                bool needReset = false;
                if (startOffset < 0 || startOffset > fileLen)
                {
                    needReset = startOffset > 0;
                    startOffset = 0;
                }
                else if (startOffset > 0)
                {
                    fs.Seek(startOffset - 1, SeekOrigin.Begin);
                    int prevByte = fs.ReadByte();
                    if (prevByte != (int)'\n')
                    {
                        needReset = true;
                        startOffset = 0;
                    }
                }

                if (needReset)
                {
                    validEndOffset = 0;
                    finalLineIndex = 0;
                    if (onReset != null)
                        onReset();
                }

                if (startOffset == fileLen)
                    return;

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
                    bool recentlyModified = false;
                    if (mtimeTicks > 0L)
                    {
                        try
                        {
                            long nowTicks = DateTime.UtcNow.ToFileTimeUtc();
                            long diffTicks = nowTicks - mtimeTicks;
                            // Skip advancing cursor on newline-free tail if file was modified within the last 2 seconds
                            if (diffTicks >= -20000000L && diffTicks < 20000000L)
                                recentlyModified = true;
                        }
                        catch { }
                    }

                    if (!recentlyModified)
                    {
                        int trimEnd = lineLen;
                        while (trimEnd > 0 && (lineBuf[trimEnd - 1] == (byte)' ' || lineBuf[trimEnd - 1] == (byte)'\r' || lineBuf[trimEnd - 1] == (byte)'\t'))
                            trimEnd--;

                        if (trimEnd > 2 && IsBalancedJsonObject(lineBuf, trimEnd))
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
        private static readonly Regex AgyModelRegex = new Regex(@"<USER_SETTINGS_CHANGE>[\s\S]*?`Model Selection`\s+from\s+.+?\s+to\s+`?([^\r\n`]+?)`?(?:\.(?:\s|$)|[\r\n<]|$)", RegexOptions.Compiled);
        private static readonly Regex AgySenderRegex = new Regex(@"\bsender=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                string norm = path.Replace('/', '\\');
                if (norm.Length >= 3 && norm[1] == ':' && norm[2] == '\\')
                    return @"\\?\" + norm;
                if (norm.StartsWith(@"\\", StringComparison.Ordinal))
                    return @"\\?\UNC\" + norm.Substring(2);
            }
            return path;
        }

        internal static bool TryGetFileStatInternal(string path, out long size, out long mtimeTicks)
        {
            return TryGetFileStat(path, out size, out mtimeTicks);
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
            if (_scanWsl)
            {
                const string wslPrefix1 = @"\\wsl.localhost";
                const string wslPrefix2 = @"\\wsl$";
                if (fileKey.StartsWith(wslPrefix1, StringComparison.OrdinalIgnoreCase))
                {
                    if (fileKey.Length == wslPrefix1.Length || fileKey[wslPrefix1.Length] == '\\' || fileKey[wslPrefix1.Length] == '/')
                        return true;
                }
                if (fileKey.StartsWith(wslPrefix2, StringComparison.OrdinalIgnoreCase))
                {
                    if (fileKey.Length == wslPrefix2.Length || fileKey[wslPrefix2.Length] == '\\' || fileKey[wslPrefix2.Length] == '/')
                        return true;
                }
            }
            if (string.IsNullOrEmpty(_home)) return false;
            string normHome = _home.TrimEnd('\\', '/');
            if (normHome.Length == 0) return false;
            if (fileKey.StartsWith(normHome, StringComparison.OrdinalIgnoreCase))
            {
                if (fileKey.Length == normHome.Length)
                    return true;
                char sep = fileKey[normHome.Length];
                if (sep == '\\' || sep == '/')
                    return true;
            }
            return false;
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
                        if (!string.IsNullOrEmpty(tid))
                            _codexLiveThreads.Add(tid);
                        MatchCollection mc = UuidRegex.Matches(Path.GetFileName(key));
                        for (int mi = 0; mi < mc.Count; mi++)
                        {
                            string ft = mc[mi].Groups[1].Value;
                            if (!string.IsNullOrEmpty(ft))
                                _codexLiveThreads.Add(ft);
                        }
                    }

                    for (int i = 0; i < cur.Events.Count; i++)
                    {
                        UsageEvent ev = cur.Events[i];
                        if (ev == null) continue;
                        if (!string.IsNullOrEmpty(ev.DedupKey))
                        {
                            string toolName = ev.Tool ?? "";
                            if (toolName.StartsWith("Claude ", StringComparison.Ordinal))
                            {
                                if (!_claudeSeenKeys.Add(ev.DedupKey))
                                    continue;
                            }
                            else if (toolName.StartsWith("OpenCode", StringComparison.Ordinal))
                            {
                                if (!_openCodeSeenKeys.Add(ev.DedupKey))
                                    continue;
                            }
                            else if (toolName.StartsWith("MobileClaw", StringComparison.Ordinal))
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

            const int maxWalkDepth = 24;
            HashSet<string> visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<KeyValuePair<string, int>> dirs = new Stack<KeyValuePair<string, int>>();
            dirs.Push(new KeyValuePair<string, int>(lpRoot, 0));
            visitedDirs.Add(lpRoot);
            while (dirs.Count > 0)
            {
                KeyValuePair<string, int> item = dirs.Pop();
                string current = item.Key;
                int depth = item.Value;
                try
                {
                    string[] files = Directory.GetFiles(current, pattern);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string f = files[i];
                        if (f.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                            f = @"\\" + f.Substring(8);
                        else if (f.StartsWith(@"\\?\", StringComparison.Ordinal))
                            f = f.Substring(4);
                        results.Add(f);
                    }
                }
                catch { }

                if (recursive && depth < maxWalkDepth)
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
                            try
                            {
                                FileAttributes attr = File.GetAttributes(sd);
                                if ((attr & FileAttributes.ReparsePoint) != 0)
                                    continue;
                            }
                            catch
                            {
                                continue;
                            }
                            if (visitedDirs.Add(sd))
                                dirs.Push(new KeyValuePair<string, int>(sd, depth + 1));
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

        private static long ClampSafeTokens(long val)
        {
            if (val <= 0L) return 0L;
            if (val > 1000000000000000L) return 1000000000000000L;
            return val;
        }

        private static long SaturatingAddTokens(long a, long b)
        {
            long sa = a > 0L ? a : 0L;
            long sb = b > 0L ? b : 0L;
            const long maxBucketTok = 2000000000000000000L;
            if (sa >= maxBucketTok - sb) return maxBucketTok;
            return sa + sb;
        }

        private static bool IsValidIsoCalendarDate(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 10 || s[4] != '-' || s[7] != '-')
                return false;
            DateTime dt;
            return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0L;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0L;
            if (v is bool) return 0L;
            if (v is int) return ClampSafeTokens((long)(int)v);
            if (v is long) return ClampSafeTokens((long)v);
            if (v is double)
            {
                double dv = (double)v;
                if (double.IsNaN(dv) || double.IsInfinity(dv) || dv <= 0.0) return 0L;
                if (dv >= 1000000000000000.0) return 1000000000000000L;
                return (long)dv;
            }
            if (v is decimal)
            {
                decimal dec = (decimal)v;
                if (dec <= 0m) return 0L;
                if (dec >= 1000000000000000m) return 1000000000000000L;
                return (long)dec;
            }
            string sv = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(sv)) return 0L;
            long parsed;
            if (long.TryParse(sv.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return ClampSafeTokens(parsed);
            double parsedD;
            if (double.TryParse(sv.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsedD))
            {
                if (double.IsNaN(parsedD) || double.IsInfinity(parsedD) || parsedD <= 0.0) return 0L;
                if (parsedD >= 1000000000000000.0) return 1000000000000000L;
                return (long)parsedD;
            }
            return 0L;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0.0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0.0;
            if (v is bool) return 0.0;
            double res = 0.0;
            if (v is double) res = (double)v;
            else if (v is int) res = (double)(int)v;
            else if (v is long) res = (double)(long)v;
            else if (v is decimal) res = (double)(decimal)v;
            else
            {
                double parsed;
                if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                    res = parsed;
            }
            if (double.IsNaN(res) || double.IsInfinity(res) || res <= 0.0) return 0.0;
            if (res > 1000000000.0) return 1000000000.0;
            return res;
        }

        private static string ToLocalDate(object val, DateTime fallbackLocal)
        {
            if (val != null && !(val is bool))
            {
                if (val is int || val is long || val is double || val is decimal)
                {
                    double num = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(num) && !double.IsInfinity(num) && num > 0.0)
                    {
                        double sec = num > 1e11 ? num / 1000.0 : num;
                        if (sec <= 253402300799.0)
                        {
                            try
                            {
                                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                                return epoch.AddSeconds(sec).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            }
                            catch { }
                        }
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
                        string sub = s.Substring(0, 10);
                        if (IsValidIsoCalendarDate(sub))
                            return sub;
                    }
                }
            }
            return fallbackLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static long ToUnixEpochMs(object val, DateTime fallbackLocal)
        {
            if (val != null && !(val is bool))
            {
                if (val is int || val is long || val is double || val is decimal)
                {
                    double num = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(num) && !double.IsInfinity(num) && num > 0.0 && num <= 253402300799000.0)
                    {
                        if (num > 1e11) return (long)num;
                        if (num > 1e9) return (long)(num * 1000.0);
                    }
                }
                else
                {
                    string s = Convert.ToString(val, CultureInfo.InvariantCulture).Trim();
                    double numStr;
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out numStr) &&
                        !double.IsNaN(numStr) && !double.IsInfinity(numStr) && numStr > 1e9 && numStr <= 253402300799000.0)
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
            ScanClaudeAt(_home, false);
        }

        private static void AddUniqueExistingDir(List<string> list, HashSet<string> seen, string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return;
            string norm = dirPath.TrimEnd('\\', '/');
            if (norm.Length == 0 || !seen.Add(norm)) return;
            try
            {
                if (Directory.Exists(ToLongPath(norm)))
                    list.Add(norm);
            }
            catch { }
        }

        private void ScanClaudeAt(string homeRoot, bool isWsl)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;

            List<string> roots = new List<string>();
            HashSet<string> seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string dotClaudeProjects = Path.Combine(homeRoot, ".claude", "projects");
            string dotClaudeTranscripts = Path.Combine(homeRoot, ".claude", "transcripts");
            string xdgClaudeProjects = Path.Combine(homeRoot, ".config", "claude", "projects");
            string xdgClaudeTranscripts = Path.Combine(homeRoot, ".config", "claude", "transcripts");

            AddUniqueExistingDir(roots, seenRoots, dotClaudeProjects);
            AddUniqueExistingDir(roots, seenRoots, dotClaudeTranscripts);
            AddUniqueExistingDir(roots, seenRoots, xdgClaudeProjects);
            AddUniqueExistingDir(roots, seenRoots, xdgClaudeTranscripts);

            if (!isWsl)
            {
                // Standard %APPDATA%\Claude paths (Desktop & Code tab sessions)
                List<string> roamingCandidates = new List<string>();
                roamingCandidates.Add(Path.Combine(homeRoot, "AppData", "Roaming", "Claude"));
                try
                {
                    string sysProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(sysProfile) &&
                        string.Equals(homeRoot.TrimEnd('\\', '/'), sysProfile.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        string sysAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        if (!string.IsNullOrEmpty(sysAppData))
                            roamingCandidates.Add(Path.Combine(sysAppData, "Claude"));
                    }
                }
                catch { }

                for (int rc = 0; rc < roamingCandidates.Count; rc++)
                {
                    string rClaude = roamingCandidates[rc];
                    AddUniqueExistingDir(roots, seenRoots, Path.Combine(rClaude, "projects"));
                    AddUniqueExistingDir(roots, seenRoots, Path.Combine(rClaude, "transcripts"));
                    AddUniqueExistingDir(roots, seenRoots, Path.Combine(rClaude, "claude-code-sessions"));
                    AddUniqueExistingDir(roots, seenRoots, Path.Combine(rClaude, "local-agent-mode-sessions"));
                }

                // Windows Store MSIX packaged Claude Desktop (AppData\Local\Packages\Claude_*\LocalCache\Roaming\...)
                string pkgBase = Path.Combine(homeRoot, "AppData", "Local", "Packages");
                try
                {
                    if (Directory.Exists(ToLongPath(pkgBase)))
                    {
                        string[] claudePkgs = Directory.GetDirectories(ToLongPath(pkgBase), "Claude_*");
                        for (int p = 0; p < claudePkgs.Length; p++)
                        {
                            string pkgDir = claudePkgs[p];
                            if (pkgDir.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                                pkgDir = @"\\" + pkgDir.Substring(8);
                            else if (pkgDir.StartsWith(@"\\?\", StringComparison.Ordinal))
                                pkgDir = pkgDir.Substring(4);

                            string lcRoaming = Path.Combine(pkgDir, "LocalCache", "Roaming");
                            string lcClaude = Path.Combine(lcRoaming, "Claude");
                            AddUniqueExistingDir(roots, seenRoots, Path.Combine(lcClaude, "projects"));
                            AddUniqueExistingDir(roots, seenRoots, Path.Combine(lcClaude, "transcripts"));
                            AddUniqueExistingDir(roots, seenRoots, Path.Combine(lcClaude, "claude-code-sessions"));
                            AddUniqueExistingDir(roots, seenRoots, Path.Combine(lcClaude, "local-agent-mode-sessions"));
                            AddUniqueExistingDir(roots, seenRoots, Path.Combine(lcRoaming, ".claude", "projects"));
                            AddUniqueExistingDir(roots, seenRoots, Path.Combine(lcRoaming, ".claude", "transcripts"));
                        }
                    }
                }
                catch { }
            }

            if (roots.Count == 0) return;

            List<ClaudeFileItem> parsedItems = new List<ClaudeFileItem>();
            HashSet<string> desktopProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> localSeenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int r = 0; r < roots.Count; r++)
            {
                string rootPath = roots[r];
                bool isProjectsStyleRoot = rootPath.EndsWith("projects", StringComparison.OrdinalIgnoreCase);
                List<string> files = SafeFindFiles(rootPath, "*.jsonl", true);
                for (int i = 0; i < files.Count; i++)
                {
                    string fp = files[i];
                    if (!localSeenFiles.Add(fp)) continue;

                    long fSize, fMtime;
                    if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                    TrackVisitedFile(fp);

                    string projGroup = isProjectsStyleRoot ? GetClaudeProjectGroup(rootPath, fp) : "";
                    bool pathIsDesktop = !isWsl && (
                        fp.IndexOf("Claude-Data", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fp.IndexOf("claude-pzs8sxrjxfjjc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fp.IndexOf(@"\Packages\Claude_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fp.IndexOf(@"\Roaming\Claude\", StringComparison.OrdinalIgnoreCase) >= 0);

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        string cachedEp = ExtractMetaField(cur.ExtraMeta, "ep", pathIsDesktop ? "desktop" : "unknown");
                        if (pathIsDesktop && cachedEp == "unknown") cachedEp = "desktop";
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
                                if (oldEv == null) continue;
                                string dk = string.IsNullOrEmpty(oldEv.DedupKey) ? ("old:" + e.ToString(CultureInfo.InvariantCulture)) : oldEv.DedupKey;
                                fileDedup[dk] = oldEv.Clone();
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
                                fileDedup[oldEv.DedupKey] = oldEv.Clone();
                            }
                        }
                    }

                    DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                    long validEnd = startOffset;
                    int endLineIdx = lineIdx;
                    long lastReqStartMs = 0L;
                    long.TryParse(ExtractMetaField(cur != null ? cur.ExtraMeta : "", "lrs", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lastReqStartMs);
                    string activeReqKey = ExtractMetaField(cur != null ? cur.ExtraMeta : "", "ark", "");
                    long activeReqStart = 0L;
                    long.TryParse(ExtractMetaField(cur != null ? cur.ExtraMeta : "", "ars", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out activeReqStart);
                    Dictionary<string, long> reqStartByUid = new Dictionary<string, long>(StringComparer.Ordinal);
                    if (!string.IsNullOrEmpty(activeReqKey) && activeReqStart > 0L)
                        reqStartByUid[activeReqKey] = activeReqStart;

                    string initDefaultTool = isWsl ? "Claude Code (WSL)" : "Claude Code";

                    try
                    {
                        Utf8LineScanner.ScanFromOffset(
                            ToLongPath(fp),
                            startOffset,
                            lineIdx,
                            fMtime,
                            delegate()
                            {
                                fileDedup.Clear();
                                reqStartByUid.Clear();
                                epKind = pathIsDesktop ? "desktop" : "unknown";
                                lastReqStartMs = 0L;
                                activeReqKey = "";
                                activeReqStart = 0L;
                            },
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
                                        else if (!pathIsDesktop && epKind != "desktop" && ep.IndexOf("cli", StringComparison.OrdinalIgnoreCase) >= 0)
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

                                long cw1h = 0L;
                                if (cw > 0L && uEnd > uStart && line.IndexOf("\"ephemeral_1h_input_tokens\"", uStart, uEnd - uStart, StringComparison.Ordinal) >= 0)
                                {
                                    cw1h = FastJson.ExtractLong(line, "\"ephemeral_1h_input_tokens\"", uStart, uEnd);
                                    if (cw1h < 0L) cw1h = 0L;
                                    else if (cw1h > cw) cw1h = cw;
                                }

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
                                activeReqKey = key;
                                activeReqStart = startRefMs;

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
                                    long finalCw1h = cw1h > 0L ? cw1h : (prev != null ? prev.CacheWrite1h : 0L);
                                    if (finalCw1h > cw) finalCw1h = cw;
                                    fileDedup[key] = new UsageEvent(initDefaultTool, model, dateStr, fi, outTok, cr, cw, cost, 1, key, tsMs, durMs, finalCw1h);
                                }
                            },
                            out validEnd,
                            out endLineIdx);
                    }
                    catch { }

                    if (pathIsDesktop && epKind == "unknown") epKind = "desktop";
                    if (epKind == "desktop" && projGroup.Length > 0)
                        desktopProjects.Add(projGroup);

                    List<UsageEvent> fileEvents = (fileDedup.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                        ? new List<UsageEvent>(cur.Events)
                        : new List<UsageEvent>(fileDedup.Values);
                    string safeArk = string.IsNullOrEmpty(activeReqKey) ? "" : activeReqKey.Replace(";", "_");
                    string metaStr = string.Format(
                        CultureInfo.InvariantCulture,
                        "ep={0};ln={1};lrs={2};ark={3};ars={4}",
                        epKind, endLineIdx, lastReqStartMs, safeArk, activeReqStart);

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
                string finalTool;
                if (isWsl)
                {
                    finalTool = "Claude Code (WSL)";
                }
                else
                {
                    bool isDesktop = item.EntrypointKind == "desktop" ||
                                     (item.EntrypointKind != "cli" && item.ProjectGroup.Length > 0 && desktopProjects.Contains(item.ProjectGroup));
                    finalTool = isDesktop ? "Claude Desktop" : "Claude Code";

                    if (isDesktop && item.EntrypointKind == "unknown" && !string.IsNullOrEmpty(item.MetaStr) && item.MetaStr.StartsWith("ep=unknown", StringComparison.Ordinal))
                    {
                        item.MetaStr = "ep=desktop" + item.MetaStr.Substring("ep=unknown".Length);
                        item.IsDirty = true;
                    }
                }

                bool toolUpdated = false;
                for (int e = 0; e < item.Events.Count; e++)
                {
                    UsageEvent src = item.Events[e];
                    if (src == null) continue;
                    if (!string.Equals(src.Tool, finalTool, StringComparison.Ordinal))
                    {
                        src = new UsageEvent(finalTool, src.Model, src.Date, src.FreshInput, src.Output, src.CacheRead, src.CacheWrite, src.LoggedCostUsd, src.Turns, src.DedupKey, src.TimestampMs, src.DurationMs, src.CacheWrite1h);
                        item.Events[e] = src;
                        toolUpdated = true;
                    }
                    string dk = string.IsNullOrEmpty(src.DedupKey) ? (finalTool + ":" + i.ToString(CultureInfo.InvariantCulture) + ":" + e.ToString(CultureInfo.InvariantCulture)) : src.DedupKey;
                    UsageEvent prev;
                    if (!globalDedup.TryGetValue(dk, out prev) ||
                        (src.FreshInput + src.Output + src.CacheRead + src.CacheWrite) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                    {
                        int finalDur = src.DurationMs > 0 ? src.DurationMs : (prev != null ? prev.DurationMs : 0);
                        long finalTs = src.TimestampMs > 0 ? src.TimestampMs : (prev != null ? prev.TimestampMs : 0L);
                        long finalCw1h = src.CacheWrite1h > 0L ? src.CacheWrite1h : (prev != null ? prev.CacheWrite1h : 0L);
                        globalDedup[dk] = new UsageEvent(finalTool, src.Model, src.Date, src.FreshInput, src.Output, src.CacheRead, src.CacheWrite, src.LoggedCostUsd, src.Turns, dk, finalTs, finalDur, finalCw1h);
                    }
                }

                if (item.IsDirty || toolUpdated)
                {
                    SaveDirtyCursor(item.FilePath, item.FileSize, item.MtimeTicks, item.ValidEnd, item.MetaStr, item.Events);
                }
            }

            foreach (KeyValuePair<string, UsageEvent> kv in globalDedup)
            {
                if (_claudeSeenKeys.Add(kv.Key))
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
            ScanCodexLiveAt(_home, "OpenAI Codex");
        }

        private void ScanCodexLiveAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string codexDir = Path.Combine(homeRoot, ".codex");
            string[] subs = new string[] { "sessions", "archived_sessions" };

            for (int s = 0; s < subs.Length; s++)
            {
                string subDir = Path.Combine(codexDir, subs[s]);
                List<string> rawJsonl = SafeFindFiles(subDir, "*.jsonl", true);
                List<string> rawZst = SafeFindFiles(subDir, "*.jsonl.zst", true);
                List<string> files = new List<string>(rawJsonl.Count + rawZst.Count);
                HashSet<string> seenSubFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rawJsonl.Count; i++)
                {
                    if (seenSubFiles.Add(rawJsonl[i])) files.Add(rawJsonl[i]);
                }
                for (int i = 0; i < rawZst.Count; i++)
                {
                    if (seenSubFiles.Add(rawZst[i])) files.Add(rawZst[i]);
                }

                for (int i = 0; i < files.Count; i++)
                {
                    string fp = files[i];
                    long fSize, fMtime;
                    if (!TryGetFileStat(fp, out fSize, out fMtime)) continue;
                    TrackVisitedFile(fp);

                    string fileTid = "";
                    MatchCollection mc = UuidRegex.Matches(Path.GetFileName(fp));
                    for (int mi = 0; mi < mc.Count; mi++)
                    {
                        string u = mc[mi].Groups[1].Value;
                        if (!string.IsNullOrEmpty(u))
                        {
                            _codexLiveThreads.Add(u);
                            fileTid = u;
                        }
                    }

                    FileCursorEntry cur;
                    if (_cursors.TryGetValue(fp, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime)
                    {
                        string cachedTid = ExtractMetaField(cur.ExtraMeta, "tid", "");
                        if (cachedTid.Length > 0) _codexLiveThreads.Add(cachedTid);
                        _events.AddRange(cur.Events);
                        continue;
                    }

                    bool isZst = fp.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
                    long startOffset = 0L;
                    int lineIdx = 0;
                    string curModel = "gpt-5.2-codex";
                    long prevTotIn = -1, prevTotOut = -1, prevTotCr = -1;
                    string metaTid = fileTid;
                    List<UsageEvent> fileEvents = new List<UsageEvent>();

                    if (!isZst && cur != null && fSize > cur.ByteOffset && cur.ByteOffset > 0)
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
                    if (!isZst)
                        long.TryParse(ExtractMetaField(cur != null ? cur.ExtraMeta : "", "tsm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out turnStartMs);
                    string lastNoTotSig = "";

                    Action<string, int> onCodexLine = delegate(string line, int curIdx)
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
                            string smMdl = FastJson.ExtractString(line, "\"model\"", plStart, plEnd);
                            if (!string.IsNullOrEmpty(smMdl))
                                curModel = PricingEngine.NormalizeModelId(smMdl, curModel);
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
                            {
                                infoStart = plStart;
                                infoEnd = plEnd;
                            }

                            int lastStart, lastEnd;
                            if (!FastJson.FindObjectBounds(line, "\"last_token_usage\"", infoStart, infoEnd, out lastStart, out lastEnd))
                                return;

                            long rawIn = FastJson.ExtractLong(line, "\"input_tokens\"", lastStart, lastEnd);
                            long cr = FastJson.ExtractLong(line, "\"cached_input_tokens\"", lastStart, lastEnd);
                            if (cr == 0) cr = FastJson.ExtractLong(line, "\"cached_tokens\"", lastStart, lastEnd);
                            long outTok = FastJson.ExtractLong(line, "\"output_tokens\"", lastStart, lastEnd);
                            long fi = rawIn >= cr ? (rawIn - cr) : rawIn;
                            if (fi + outTok + cr <= 0) return;

                            string tsRaw = FastJson.ExtractRawScalar(line, "\"timestamp\"", 0, Math.Min(line.Length, 160));

                            long tIn = 0, tOut = 0, tCr = 0;
                            int totStart, totEnd;
                            bool hasTotal = FastJson.FindObjectBounds(line, "\"total_token_usage\"", infoStart, infoEnd, out totStart, out totEnd);
                            if (hasTotal)
                            {
                                tIn = FastJson.ExtractLong(line, "\"input_tokens\"", totStart, totEnd);
                                tOut = FastJson.ExtractLong(line, "\"output_tokens\"", totStart, totEnd);
                                tCr = FastJson.ExtractLong(line, "\"cached_input_tokens\"", totStart, totEnd);
                                if (tCr == 0) tCr = FastJson.ExtractLong(line, "\"cached_tokens\"", totStart, totEnd);
                            }

                            if (hasTotal && (tIn > 0 || tOut > 0 || tCr > 0))
                            {
                                if (tIn == prevTotIn && tOut == prevTotOut && tCr == prevTotCr) return;
                                prevTotIn = tIn; prevTotOut = tOut; prevTotCr = tCr;
                            }
                            else
                            {
                                // Support logs that only emit last_token_usage without total_token_usage
                                string sig = (tsRaw ?? "") + "|" + rawIn.ToString(CultureInfo.InvariantCulture) + "|" + outTok.ToString(CultureInfo.InvariantCulture) + "|" + cr.ToString(CultureInfo.InvariantCulture);
                                if (string.Equals(sig, lastNoTotSig, StringComparison.Ordinal)) return;
                                lastNoTotSig = sig;
                            }

                            string evMdl = FastJson.ExtractString(line, "\"model\"", infoStart, infoEnd);
                            if (!string.IsNullOrEmpty(evMdl))
                                curModel = PricingEngine.NormalizeModelId(evMdl, curModel);

                            string dtStr = ToLocalDate(tsRaw, fallbackDt);
                            long tsMs = ToUnixEpochMs(tsRaw, fallbackDt);
                            int durMs = ComputeSafeDurationMs(turnStartMs, tsMs);
                            if (tsMs > 0) turnStartMs = tsMs;
                            fileEvents.Add(new UsageEvent(toolName, curModel, dtStr, fi, outTok, cr, 0L, 0.0, 1, "", tsMs, durMs));
                        }
                    };

                    try
                    {
                        if (isZst)
                        {
                            string[] zstKws = new string[] { "turn_context", "token_count", "session_meta", "call_output", "user_message", "task_started" };
                            if (WinZstdReader.TryScanLines(fp, zstKws, onCodexLine))
                                validEnd = fSize;
                        }
                        else
                        {
                            Utf8LineScanner.ScanFromOffset(
                                ToLongPath(fp),
                                startOffset,
                                lineIdx,
                                fMtime,
                                delegate()
                                {
                                    fileEvents.Clear();
                                    curModel = "gpt-5.2-codex";
                                    prevTotIn = -1;
                                    prevTotOut = -1;
                                    prevTotCr = -1;
                                    turnStartMs = 0L;
                                    lastNoTotSig = "";
                                },
                                CodexKeywords,
                                onCodexLine,
                                out validEnd,
                                out endIdx);
                        }
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

        // -------------------- 3. Google Antigravity (Seek Cursor + .db gen_metadata + Parent Chain) --------------------
        private sealed class AgyConvItem
        {
            public string ConvId;
            public string FilePath;
            public long FileSize;
            public long MtimeTicks;
            public long ValidEnd;
            public string ExplicitModel;
            public string ParentConvId;
            public string PendingStepKey;
            public long PendingStartMs;
            public string DbSig;
            public bool IsDirty;
            public List<UsageEvent> Events;
        }

        private static readonly Regex AgyModelFromToRegex = new Regex(
            @"<USER_SETTINGS_CHANGE>[\s\S]*?`Model Selection`\s+from\s+`?([^\r\n`]+?)`?\s+to\s+`?([^\r\n`]+?)`?(?:\.(?:\s|$)|[\r\n<]|$)",
            RegexOptions.Compiled);

        private static string GetAntigravityDbSignature(string convDbPath)
        {
            if (string.IsNullOrEmpty(convDbPath)) return "";
            long dbSz = 0L, dbMt = 0L, walSz = 0L, walMt = 0L;
            bool hasDb = TryGetFileStat(convDbPath, out dbSz, out dbMt);
            bool hasWal = TryGetFileStat(convDbPath + "-wal", out walSz, out walMt);
            if (!hasDb && !hasWal) return "";
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", dbSz, dbMt & 0xFFFFFFFFL, walSz, walMt & 0xFFFFFFFFL);
        }

        private static bool IsPrintableModelSlugBytes(byte[] data, int start, int len)
        {
            bool hasAlpha = false;
            bool hasSep = false;
            for (int i = 0; i < len; i++)
            {
                byte b = data[start + i];
                if ((b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z'))
                {
                    hasAlpha = true;
                }
                else if (b >= (byte)'0' && b <= (byte)'9')
                {
                }
                else if (b == (byte)'-' || b == (byte)'_' || b == (byte)'.' || b == (byte)'/' || b == (byte)':')
                {
                    if (b == (byte)'-' || b == (byte)'_') hasSep = true;
                }
                else
                {
                    return false;
                }
            }
            if (!hasAlpha) return false;
            if (hasSep) return true;
            string s = Encoding.ASCII.GetString(data, start, len);
            return s.IndexOf("orchestrator", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int FindAsciiBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || haystack.Length < needle.Length) return -1;
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return i;
            }
            return -1;
        }

        private static bool TryLoadAntigravityDbStepModels(string convDbPath, out Dictionary<string, string> stepModels, out string latestDbModel)
        {
            stepModels = new Dictionary<string, string>(StringComparer.Ordinal);
            latestDbModel = "";
            if (string.IsNullOrEmpty(convDbPath)) return false;
            long dSz, dMt;
            if (!TryGetFileStat(convDbPath, out dSz, out dMt) || dSz <= 0L)
                return false;

            byte[] lsiBytes = Encoding.ASCII.GetBytes("last_step_index");
            try
            {
                using (WinSqlite db = WinSqlite.OpenReadOnly(convDbPath))
                {
                    if (db == null || !db.IsHealthy()) return false;
                    Dictionary<string, string> localSteps = stepModels;
                    string localLatest = "";
                    db.Query("SELECT idx, substr(data, max(1, length(data) - 2048), 2049) FROM gen_metadata ORDER BY idx", delegate(WinSqlite.SqliteRow r)
                    {
                        byte[] tail = r.GetBlob(1);
                        if (tail == null || tail.Length < 8) return;

                        string stepKey = (r.GetInt64(0) + 1L).ToString(CultureInfo.InvariantCulture);
                        int lsiPos = FindAsciiBytes(tail, lsiBytes);
                        if (lsiPos >= 0)
                        {
                            int maxProbe = Math.Min(tail.Length - 3, lsiPos + lsiBytes.Length + 6);
                            for (int p = lsiPos + lsiBytes.Length; p <= maxProbe; p++)
                            {
                                if (tail[p] == 0x12)
                                {
                                    int slen = tail[p + 1];
                                    if (slen >= 1 && slen <= 12 && p + 2 + slen <= tail.Length)
                                    {
                                        bool allDigits = true;
                                        for (int d = 0; d < slen; d++)
                                        {
                                            byte dbByte = tail[p + 2 + d];
                                            if (dbByte < (byte)'0' || dbByte > (byte)'9')
                                            {
                                                allDigits = false;
                                                break;
                                            }
                                        }
                                        if (allDigits)
                                        {
                                            long parsedIdx;
                                            if (long.TryParse(Encoding.ASCII.GetString(tail, p + 2, slen), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedIdx))
                                            {
                                                stepKey = (parsedIdx + 1L).ToString(CultureInfo.InvariantCulture);
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        string foundRaw = "";
                        int limit = tail.Length - 4;
                        for (int k = 0; k < limit; k++)
                        {
                            if (tail[k] == 0x9A && tail[k + 1] == 0x01)
                            {
                                int mlen = tail[k + 2];
                                if (mlen >= 4 && mlen <= 64 && k + 3 + mlen <= tail.Length)
                                {
                                    if (IsPrintableModelSlugBytes(tail, k + 3, mlen))
                                        foundRaw = Encoding.ASCII.GetString(tail, k + 3, mlen);
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(foundRaw))
                        {
                            string norm = PricingEngine.NormalizeModelId(foundRaw, "");
                            if (string.Equals(norm, "orchestrator", StringComparison.OrdinalIgnoreCase))
                                norm = "gemini-pro-orchestrator";
                            if (!string.IsNullOrEmpty(norm) && !string.Equals(norm, "unknown", StringComparison.OrdinalIgnoreCase))
                            {
                                localSteps[stepKey] = norm;
                                localLatest = norm;
                            }
                        }
                    });
                    latestDbModel = localLatest;
                    return stepModels.Count > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void ScanAntigravity()
        {
            string agyRoot = Path.Combine(_home, ".gemini", "antigravity");
            string brainDir = Path.Combine(agyRoot, "brain");
            try { if (!Directory.Exists(ToLongPath(brainDir))) return; } catch { return; }

            string[] convDirs;
            try { convDirs = Directory.GetDirectories(ToLongPath(brainDir)); } catch { return; }

            Dictionary<string, string> convParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string summariesDbPath = Path.Combine(agyRoot, "conversation_summaries.db");
            try
            {
                long sSz, sMt;
                if (TryGetFileStat(summariesDbPath, out sSz, out sMt) && sSz > 0L)
                {
                    using (WinSqlite sdb = WinSqlite.OpenReadOnly(summariesDbPath))
                    {
                        if (sdb != null && sdb.IsHealthy())
                        {
                            sdb.Query("SELECT conversation_id, parent_conversation_id FROM conversation_summaries WHERE parent_conversation_id IS NOT NULL AND parent_conversation_id != ''", delegate(WinSqlite.SqliteRow sr)
                            {
                                string cid = sr.GetString(0);
                                string pid = sr.GetString(1);
                                if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(pid) && !string.Equals(cid, pid, StringComparison.OrdinalIgnoreCase))
                                    convParents[cid] = pid;
                            });
                        }
                    }
                }
            }
            catch { }

            string convsDir = Path.Combine(agyRoot, "conversations");
            List<AgyConvItem> agyItems = new List<AgyConvItem>(convDirs.Length);
            Dictionary<string, string> convExplicitModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < convDirs.Length; i++)
            {
                string convDir = convDirs[i];
                if (convDir.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    convDir = @"\\" + convDir.Substring(8);
                else if (convDir.StartsWith(@"\\?\", StringComparison.Ordinal))
                    convDir = convDir.Substring(4);

                string convId = Path.GetFileName(convDir) ?? "";
                string tf = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                long fSize, fMtime;
                if (!TryGetFileStat(tf, out fSize, out fMtime)) continue;
                TrackVisitedFile(tf);

                string convDbPath = !string.IsNullOrEmpty(convId) ? Path.Combine(convsDir, convId + ".db") : "";
                string dbSig = GetAntigravityDbSignature(convDbPath);

                FileCursorEntry cur;
                if (_cursors.TryGetValue(tf, out cur) && cur.FileSize == fSize && cur.MtimeTicks == fMtime &&
                    string.Equals(ExtractMetaField(cur.ExtraMeta, "dbs", ""), dbSig, StringComparison.Ordinal))
                {
                    string cachedMdl = ExtractMetaField(cur.ExtraMeta, "mdl", "");
                    if (string.Equals(cachedMdl, "unknown", StringComparison.OrdinalIgnoreCase))
                        cachedMdl = "";
                    else if (string.Equals(cachedMdl, "orchestrator", StringComparison.OrdinalIgnoreCase))
                        cachedMdl = "gemini-pro-orchestrator";
                    string cachedPid = ExtractMetaField(cur.ExtraMeta, "pid", "");
                    if (string.IsNullOrEmpty(cachedPid) && !string.IsNullOrEmpty(convId))
                        convParents.TryGetValue(convId, out cachedPid);
                    string cachedPsk = ExtractMetaField(cur.ExtraMeta, "psk", "");
                    long cachedPsm = 0L;
                    long.TryParse(ExtractMetaField(cur.ExtraMeta, "psm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out cachedPsm);

                    if (!string.IsNullOrEmpty(convId) && !string.IsNullOrEmpty(cachedMdl))
                        convExplicitModels[convId] = cachedMdl;
                    if (!string.IsNullOrEmpty(convId) && !string.IsNullOrEmpty(cachedPid))
                        convParents[convId] = cachedPid;

                    AgyConvItem cachedItem = new AgyConvItem();
                    cachedItem.ConvId = convId;
                    cachedItem.FilePath = tf;
                    cachedItem.FileSize = cur.FileSize;
                    cachedItem.MtimeTicks = cur.MtimeTicks;
                    cachedItem.ValidEnd = cur.ByteOffset;
                    cachedItem.ExplicitModel = cachedMdl;
                    cachedItem.ParentConvId = cachedPid ?? "";
                    cachedItem.PendingStepKey = cachedPsk;
                    cachedItem.PendingStartMs = cachedPsm;
                    cachedItem.DbSig = dbSig;
                    cachedItem.IsDirty = false;
                    cachedItem.Events = cur.Events ?? new List<UsageEvent>();
                    agyItems.Add(cachedItem);
                    continue;
                }

                long startOffset = 0L;
                string curModel = "";
                string parentConvId = "";
                if (!string.IsNullOrEmpty(convId))
                    convParents.TryGetValue(convId, out parentConvId);
                string pendingStepKey = "";
                long pendingStartMs = 0L;
                Dictionary<string, UsageEvent> seenSteps = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
                List<string> stepOrder = new List<string>();

                if (cur != null && fSize >= cur.ByteOffset && cur.ByteOffset > 0)
                {
                    startOffset = cur.ByteOffset;
                    curModel = ExtractMetaField(cur.ExtraMeta, "mdl", "");
                    if (string.Equals(curModel, "unknown", StringComparison.OrdinalIgnoreCase))
                        curModel = "";
                    else if (string.Equals(curModel, "orchestrator", StringComparison.OrdinalIgnoreCase))
                        curModel = "gemini-pro-orchestrator";
                    string metaPid = ExtractMetaField(cur.ExtraMeta, "pid", "");
                    if (!string.IsNullOrEmpty(metaPid)) parentConvId = metaPid;
                    pendingStepKey = ExtractMetaField(cur.ExtraMeta, "psk", "");
                    long.TryParse(ExtractMetaField(cur.ExtraMeta, "psm", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out pendingStartMs);

                    if (cur.Events != null)
                    {
                        for (int e = 0; e < cur.Events.Count; e++)
                        {
                            UsageEvent oldEv = cur.Events[e];
                            if (oldEv == null) continue;
                            string dk = string.IsNullOrEmpty(oldEv.DedupKey) ? e.ToString(CultureInfo.InvariantCulture) : oldEv.DedupKey;
                            if (!seenSteps.ContainsKey(dk))
                                stepOrder.Add(dk);
                            seenSteps[dk] = oldEv.Clone();
                        }
                    }
                }

                DateTime fallbackDt = FileTimeTicksToLocal(fMtime);
                long validEnd = startOffset;
                int endIdx = 0;

                if (fSize > startOffset)
                {
                    try
                    {
                        Utf8LineScanner.ScanFromOffset(
                            ToLongPath(tf),
                            startOffset,
                            0,
                            fMtime,
                            delegate()
                            {
                                seenSteps.Clear();
                                stepOrder.Clear();
                                curModel = "";
                                pendingStepKey = "";
                                pendingStartMs = 0L;
                            },
                            AgyKeywords,
                            delegate(string line, int curIdx)
                            {
                                string stepType = FastJson.ExtractString(line, "\"type\"", 0, Math.Min(line.Length, 240));
                                if (stepType == null) stepType = FastJson.ExtractString(line, "\"type\"", 0, line.Length);

                                string createdRaw = FastJson.ExtractRawScalar(line, "\"created_at\"", 0, Math.Min(line.Length, 320));
                                if (createdRaw == null) createdRaw = FastJson.ExtractRawScalar(line, "\"created_at\"", 0, line.Length);
                                long curStepMs = !string.IsNullOrEmpty(createdRaw) ? ToUnixEpochMs(createdRaw, fallbackDt) : 0L;

                                if (!string.IsNullOrEmpty(pendingStepKey))
                                {
                                    if (stepType == "GENERIC")
                                    {
                                        if (curStepMs > pendingStartMs)
                                        {
                                            int backfilledDur = ComputeSafeDurationMs(pendingStartMs, curStepMs);
                                            UsageEvent pendEv;
                                            if (backfilledDur > 0 && seenSteps.TryGetValue(pendingStepKey, out pendEv) && pendEv != null)
                                                pendEv.DurationMs = backfilledDur;
                                        }
                                        pendingStepKey = "";
                                        pendingStartMs = 0L;
                                    }
                                    else if (!string.IsNullOrEmpty(stepType))
                                    {
                                        pendingStepKey = "";
                                        pendingStartMs = 0L;
                                    }
                                }

                                if (stepType == "USER_INPUT" || stepType == "SYSTEM_MESSAGE")
                                {
                                    if (string.IsNullOrEmpty(parentConvId) && line.IndexOf("sender=", StringComparison.Ordinal) >= 0)
                                    {
                                        string sysContent = FastJson.ExtractString(line, "\"content\"", 0, line.Length);
                                        if (!string.IsNullOrEmpty(sysContent))
                                        {
                                            Match ms = AgySenderRegex.Match(sysContent);
                                            if (ms.Success)
                                            {
                                                string candPid = ms.Groups[1].Value;
                                                if (!string.Equals(candPid, convId, StringComparison.OrdinalIgnoreCase))
                                                    parentConvId = candPid;
                                            }
                                        }
                                    }

                                    if (line.IndexOf("Model Selection", StringComparison.Ordinal) >= 0 &&
                                        line.IndexOf("USER_SETTINGS_CHANGE", StringComparison.Ordinal) >= 0)
                                    {
                                        string content = FastJson.ExtractString(line, "\"content\"", 0, line.Length);
                                        if (!string.IsNullOrEmpty(content))
                                        {
                                            if (string.IsNullOrEmpty(curModel) && seenSteps.Count > 0)
                                            {
                                                Match mft = AgyModelFromToRegex.Match(content);
                                                if (mft.Success)
                                                {
                                                    string fromRaw = mft.Groups[1].Value.Trim().TrimEnd('.');
                                                    string fromNorm = PricingEngine.NormalizeModelId(fromRaw, "");
                                                    if (string.Equals(fromNorm, "orchestrator", StringComparison.OrdinalIgnoreCase))
                                                        fromNorm = "gemini-pro-orchestrator";
                                                    if (!string.IsNullOrEmpty(fromNorm) && !string.Equals(fromNorm, "unknown", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        foreach (UsageEvent prevEv in seenSteps.Values)
                                                        {
                                                            if (prevEv != null && string.IsNullOrEmpty(prevEv.Model))
                                                                prevEv.Model = fromNorm;
                                                        }
                                                    }
                                                }
                                            }

                                            MatchCollection mc = AgyModelRegex.Matches(content);
                                            if (mc.Count > 0)
                                            {
                                                string toRaw = mc[mc.Count - 1].Groups[1].Value.Trim().TrimEnd('.');
                                                string toNorm = PricingEngine.NormalizeModelId(toRaw, "");
                                                if (string.Equals(toNorm, "orchestrator", StringComparison.OrdinalIgnoreCase))
                                                    toNorm = "gemini-pro-orchestrator";
                                                if (!string.IsNullOrEmpty(toNorm) && !string.Equals(toNorm, "unknown", StringComparison.OrdinalIgnoreCase))
                                                    curModel = toNorm;
                                            }
                                        }
                                    }
                                }

                                if (line.IndexOf("input_tokens", StringComparison.Ordinal) < 0)
                                    return;

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

                                    int durMs = 0;
                                    long explicitDur = FastJson.ExtractLong(line, "\"duration_ms\"", 0, Math.Min(line.Length, 400));
                                    if (explicitDur >= 100 && explicitDur <= 600000)
                                        durMs = (int)explicitDur;

                                    UsageEvent oldStep;
                                    if (durMs == 0 && seenSteps.TryGetValue(stepKey, out oldStep) && oldStep != null && oldStep.DurationMs > 0)
                                        durMs = oldStep.DurationMs;

                                    if (!seenSteps.ContainsKey(stepKey))
                                        stepOrder.Add(stepKey);
                                    seenSteps[stepKey] = new UsageEvent("Google Antigravity", curModel, dtStr, fi, outTok, cr, cw, 0.0, 1, stepKey, tsMs, durMs);

                                    if (durMs == 0 && curStepMs > 0)
                                    {
                                        pendingStepKey = stepKey;
                                        pendingStartMs = curStepMs;
                                    }
                                }
                            },
                            out validEnd,
                            out endIdx);
                    }
                    catch { }
                }

                Dictionary<string, string> dbStepModels;
                string latestDbModel;
                TryLoadAntigravityDbStepModels(convDbPath, out dbStepModels, out latestDbModel);

                List<UsageEvent> fileEvents;
                if (seenSteps.Count == 0 && cur != null && cur.Events != null && cur.Events.Count > 0)
                {
                    fileEvents = new List<UsageEvent>(cur.Events);
                }
                else
                {
                    fileEvents = new List<UsageEvent>(stepOrder.Count);
                    for (int k = 0; k < stepOrder.Count; k++)
                    {
                        UsageEvent sev;
                        if (seenSteps.TryGetValue(stepOrder[k], out sev) && sev != null)
                            fileEvents.Add(sev);
                    }
                }

                // 优先以主库 gen_metadata 精确后端模型覆盖每步，次之使用会话级明确选型或最近真实模型
                for (int k = 0; k < fileEvents.Count; k++)
                {
                    UsageEvent fev = fileEvents[k];
                    if (fev == null) continue;
                    string resolved = "";
                    if (!string.IsNullOrEmpty(fev.DedupKey) && dbStepModels.TryGetValue(fev.DedupKey, out resolved) && !string.IsNullOrEmpty(resolved))
                    {
                        fev.Model = resolved;
                    }
                    else if (string.IsNullOrEmpty(fev.Model) || string.Equals(fev.Model, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dbStepModels.Count > 0 && !string.IsNullOrEmpty(latestDbModel))
                            fev.Model = latestDbModel;
                        else if (!string.IsNullOrEmpty(curModel) && !string.Equals(curModel, "gemini-pro-orchestrator", StringComparison.OrdinalIgnoreCase))
                            fev.Model = curModel;
                        else if (!string.IsNullOrEmpty(latestDbModel))
                            fev.Model = latestDbModel;
                        else if (!string.IsNullOrEmpty(curModel))
                            fev.Model = curModel;
                    }
                    else if (string.Equals(fev.Model, "orchestrator", StringComparison.OrdinalIgnoreCase))
                    {
                        fev.Model = "gemini-pro-orchestrator";
                    }
                }

                string effectiveExplicit = !string.IsNullOrEmpty(latestDbModel) ? latestDbModel : curModel;
                if (string.Equals(effectiveExplicit, "orchestrator", StringComparison.OrdinalIgnoreCase))
                    effectiveExplicit = "gemini-pro-orchestrator";

                if (!string.IsNullOrEmpty(convId) && !string.IsNullOrEmpty(effectiveExplicit))
                    convExplicitModels[convId] = effectiveExplicit;
                if (!string.IsNullOrEmpty(convId) && !string.IsNullOrEmpty(parentConvId))
                    convParents[convId] = parentConvId;

                AgyConvItem item = new AgyConvItem();
                item.ConvId = convId;
                item.FilePath = tf;
                item.FileSize = fSize;
                item.MtimeTicks = fMtime;
                item.ValidEnd = validEnd;
                item.ExplicitModel = effectiveExplicit;
                item.ParentConvId = parentConvId ?? "";
                item.PendingStepKey = pendingStepKey;
                item.PendingStartMs = pendingStartMs;
                item.DbSig = dbSig;
                item.IsDirty = true;
                item.Events = fileEvents;
                agyItems.Add(item);
            }

            // Second pass: resolve subagent parent model inheritance (conversation_summaries.db + sender=) and default fallback
            for (int i = 0; i < agyItems.Count; i++)
            {
                AgyConvItem item = agyItems[i];
                string fallbackModel = item.ExplicitModel;
                if (string.IsNullOrEmpty(fallbackModel))
                {
                    string walkPid = item.ParentConvId;
                    if (string.IsNullOrEmpty(walkPid) && !string.IsNullOrEmpty(item.ConvId))
                        convParents.TryGetValue(item.ConvId, out walkPid);
                    for (int hop = 0; hop < 6 && !string.IsNullOrEmpty(walkPid); hop++)
                    {
                        string pMdl;
                        if (convExplicitModels.TryGetValue(walkPid, out pMdl) && !string.IsNullOrEmpty(pMdl))
                        {
                            fallbackModel = pMdl;
                            break;
                        }
                        string nextPid;
                        if (!convParents.TryGetValue(walkPid, out nextPid) || string.Equals(nextPid, walkPid, StringComparison.OrdinalIgnoreCase))
                            break;
                        walkPid = nextPid;
                    }
                }
                if (string.IsNullOrEmpty(fallbackModel))
                    fallbackModel = "gemini-4-carbon";

                bool modelPatched = false;
                for (int e = 0; e < item.Events.Count; e++)
                {
                    UsageEvent ev = item.Events[e];
                    if (ev == null) continue;
                    if (string.IsNullOrEmpty(ev.Model) || string.Equals(ev.Model, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        ev = new UsageEvent(ev.Tool, fallbackModel, ev.Date, ev.FreshInput, ev.Output, ev.CacheRead, ev.CacheWrite, ev.LoggedCostUsd, ev.Turns, ev.DedupKey, ev.TimestampMs, ev.DurationMs);
                        item.Events[e] = ev;
                        modelPatched = true;
                    }
                    else if (string.Equals(ev.Model, "orchestrator", StringComparison.OrdinalIgnoreCase))
                    {
                        ev = new UsageEvent(ev.Tool, "gemini-pro-orchestrator", ev.Date, ev.FreshInput, ev.Output, ev.CacheRead, ev.CacheWrite, ev.LoggedCostUsd, ev.Turns, ev.DedupKey, ev.TimestampMs, ev.DurationMs);
                        item.Events[e] = ev;
                        modelPatched = true;
                    }
                }

                if (item.IsDirty || modelPatched)
                {
                    string safePsk = string.IsNullOrEmpty(item.PendingStepKey) ? "" : item.PendingStepKey.Replace(";", "_");
                    string safeDbs = string.IsNullOrEmpty(item.DbSig) ? "" : item.DbSig.Replace(";", "_");
                    string meta = string.Format(
                        CultureInfo.InvariantCulture,
                        "mdl={0};pid={1};psk={2};psm={3};dbs={4}",
                        item.ExplicitModel ?? "",
                        item.ParentConvId ?? "",
                        safePsk,
                        item.PendingStartMs,
                        safeDbs);
                    SaveDirtyCursor(item.FilePath, item.FileSize, item.MtimeTicks, item.ValidEnd, meta, item.Events);
                }

                _events.AddRange(item.Events);
            }
        }

        // -------------------- 4. Gemini CLI --------------------
        private void ScanGeminiCli()
        {
            ScanGeminiCliAt(_home, "Gemini CLI");
        }

        private void ScanGeminiCliAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string tmpDir = Path.Combine(homeRoot, ".gemini", "tmp");
            List<string> files = SafeFindFiles(tmpDir, "*.json", true);
            if (files.Count == 0) return;
            JavaScriptSerializer js = null;

            for (int i = 0; i < files.Count; i++)
            {
                string fp = files[i];
                if (fp.IndexOf("\\chats\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                    fp.IndexOf("/chats/", StringComparison.OrdinalIgnoreCase) < 0)
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
                                        fileEvents.Add(new UsageEvent(toolName, mdl, dtStr, fi, outTok, cr, 0L, 0.0, 1, "", tsMs, durMs));
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
            ScanZCodeAt(_home, "ZCode");
        }

        private void ScanZCodeAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string zdb = Path.Combine(homeRoot, ".zcode", "cli", "db", "db.sqlite");
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
                    if (ev == null) continue;
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
                            msgDedup[dk] = new UsageEvent(toolName, mdl, ToLocalDate(createdTs, DateTime.Now), fi, outTok, cr, cw, cost, 1, dk, tsMs, durMs);
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
            ScanKimiCodeAt(_home, "Kimi Code");
        }

        private void ScanKimiCodeAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string[] sessionDirs = new string[]
            {
                Path.Combine(homeRoot, ".kimi-code", "sessions"),
                Path.Combine(homeRoot, ".kimi", "sessions")
            };

            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int sd = 0; sd < sessionDirs.Length; sd++)
            {
                List<string> files = SafeFindFiles(sessionDirs[sd], "wire.jsonl", true);
                for (int i = 0; i < files.Count; i++)
                {
                    string wf = files[i];
                    if (!seenFiles.Add(wf)) continue;

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
                            fMtime,
                            delegate()
                            {
                                fileEvents.Clear();
                                curModel = "kimi-for-coding";
                                lastReqMs = 0L;
                            },
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
                                        fileEvents.Add(new UsageEvent(toolName, mdl, ToLocalDate(tRaw, fallbackDt), fi, outTok, cr, cw, 0.0, 1, "", tsMs, durMs));
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
            ScanMobileClawAt(_home, "MobileClaw");
        }

        private void ScanMobileClawAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string mcDir = Path.Combine(homeRoot, ".local", "share", "mobileclaw", "workspaces");
            try { if (!Directory.Exists(ToLongPath(mcDir))) return; } catch { return; }

            JavaScriptSerializer js = null;
            string[] wsDirs;
            try { wsDirs = Directory.GetDirectories(ToLongPath(mcDir)); } catch { return; }

            for (int w = 0; w < wsDirs.Length; w++)
            {
                string wsDir = wsDirs[w];
                if (wsDir.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    wsDir = @"\\" + wsDir.Substring(8);
                else if (wsDir.StartsWith(@"\\?\", StringComparison.Ordinal))
                    wsDir = wsDir.Substring(4);

                string msgRoot = Path.Combine(wsDir, "storage", "message");
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
                            if (ev == null) continue;
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
                            UsageEvent ev = ParseOpenCodeMessageDict(toolName, d, FileTimeTicksToLocal(fMtime), null, "deepseek-v4.1-flash");
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
                        if (ev == null) continue;
                        if (string.IsNullOrEmpty(ev.DedupKey) || _mobileClawSeenKeys.Add(ev.DedupKey))
                            _events.Add(ev);
                    }
                    SaveDirtyCursor(mf, fSize, fMtime, fSize, "", fileEvents);
                }
            }
        }

        private void ScanOpenCode()
        {
            ScanOpenCodeAt(_home, "OpenCode");
        }

        private void ScanOpenCodeAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string[] bases = new string[]
            {
                Path.Combine(homeRoot, ".local", "share", "opencode"),
                Path.Combine(homeRoot, "AppData", "Local", "opencode")
            };
            JavaScriptSerializer js = null;

            for (int b = 0; b < bases.Length; b++)
            {
                string baseDir = bases[b];
                try { if (!Directory.Exists(ToLongPath(baseDir))) continue; } catch { continue; }

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
                            if (ev == null) continue;
                            if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                                _events.Add(ev);
                        }
                        continue;
                    }

                    List<UsageEvent> dbEvents = new List<UsageEvent>();
                    bool dbOpened = false;
                    DateTime dbFallbackDt = FileTimeTicksToLocal(cMtime);
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
                                        UsageEvent ev = ParseOpenCodeMessageDict(toolName, d, dbFallbackDt, tc, "claude-sonnet-4-6");
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
                            if (ev == null) continue;
                            if (string.IsNullOrEmpty(ev.DedupKey) || _openCodeSeenKeys.Add(ev.DedupKey))
                                _events.Add(ev);
                        }
                        continue;
                    }

                    dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
                    for (int e = 0; e < dbEvents.Count; e++)
                    {
                        UsageEvent ev = dbEvents[e];
                        if (ev == null) continue;
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
                            if (ev == null) continue;
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
                            UsageEvent ev = ParseOpenCodeMessageDict(toolName, d, FileTimeTicksToLocal(fMtime), null, "claude-sonnet-4-6");
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
                        if (ev == null) continue;
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
            ScanWorkBuddyAndCodeBuddyAt(_home, "");
        }

        private void ScanWorkBuddyAndCodeBuddyAt(string homeRoot, string suffix)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string sfx = suffix ?? "";
            string[,] targets = new string[,]
            {
                { "WorkBuddy" + sfx, Path.Combine(homeRoot, ".workbuddy") },
                { "WorkBuddy" + sfx, Path.Combine(homeRoot, ".workbuddy-ai") },
                { "CodeBuddy" + sfx, Path.Combine(homeRoot, ".codebuddy") }
            };
            JavaScriptSerializer js = null;

            for (int t = 0; t < targets.GetLength(0); t++)
            {
                string toolName = targets[t, 0];
                string root = targets[t, 1];
                string proj = Path.Combine(root, "projects");
                try { if (!Directory.Exists(ToLongPath(proj))) continue; } catch { continue; }

                string defaultModel = "workbuddy-default";
                string stFile = Path.Combine(root, "settings.json");
                try
                {
                    if (File.Exists(ToLongPath(stFile)))
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
            ScanDshAt(_home, "DeepSeek (DSH)");
        }

        private void ScanDshAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string dshDir = Path.Combine(homeRoot, ".dsh", "storages");
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
                            long prevRowMs = 0L;
                            for (int r = 0; r < rows.Count; r++)
                            {
                                Dictionary<string, object> item = AsDict(rows[r]);
                                if (item == null) continue;
                                object ts = item.ContainsKey("created_at") ? item["created_at"] : (item.ContainsKey("timestamp") ? item["timestamp"] : null);
                                long curRowMs = ts != null ? ToUnixEpochMs(ts, mtime) : 0L;

                                Dictionary<string, object> meta = AsDict(item.ContainsKey("meta") ? item["meta"] : null);
                                Dictionary<string, object> u = AsDict(item.ContainsKey("usage") ? item["usage"] : (meta != null && meta.ContainsKey("usage") ? meta["usage"] : null));
                                if (u == null)
                                {
                                    if (curRowMs > 0) prevRowMs = curRowMs;
                                    continue;
                                }

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
                                    string mdl = PricingEngine.NormalizeModelId(GetStr(item, "model") ?? GetStr(meta, "model"), "deepseek-chat");
                                    long tsMs = curRowMs > 0 ? curRowMs : ToUnixEpochMs(null, mtime);
                                    int durMs = ComputeSafeDurationMs(prevRowMs, tsMs);
                                    string dk = sf + ":" + r.ToString(CultureInfo.InvariantCulture);
                                    fileEvents.Add(new UsageEvent(toolName, mdl, ToLocalDate(ts, mtime), fi, outTok, cr, 0L, 0.0, 1, dk, tsMs, durMs));
                                }
                                if (curRowMs > 0) prevRowMs = curRowMs;
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
            ScanCommandCodeAt(_home, "Command Code");
        }

        private void ScanCommandCodeAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            ScanGenericJsonlTree(toolName, Path.Combine(homeRoot, ".commandcode", "projects"), "claude-sonnet-4-6", "*.jsonl");
        }

        // -------------------- 12. VS Code / Cursor / Insiders / VSCodium / Trae + WSL Remote Cline Family --------------------
        private static readonly string[,] ClineFamilyExtensions = new string[,]
        {
            { "Cline", "saoudrizwan.claude-dev" },
            { "Roo Code", "rooveterinaryinc.roo-cline" },
            { "Kilo Code", "kilocode.kilo-code" }
        };

        private void ScanClineTasksDir(string tasksDir, string label, ref JavaScriptSerializer js)
        {
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
                                string mdl = PricingEngine.NormalizeModelId(GetStr(info, "model") ?? GetStr(info, "apiProtocol"), "claude-sonnet-4-6");
                                object rawTs = item.ContainsKey("ts") ? item["ts"] : null;
                                string dtStr = ToLocalDate(rawTs, fallbackDt);
                                long tsMs = ToUnixEpochMs(rawTs, fallbackDt);

                                int durMs = 0;
                                long expDur = GetLong(info, "durationMs");
                                if (expDur == 0) expDur = GetLong(info, "duration");
                                if (expDur >= 100 && expDur <= 600000)
                                {
                                    durMs = (int)expDur;
                                }
                                else if (tsMs > 0 && m + 1 < msgs.Count)
                                {
                                    Dictionary<string, object> nextItem = AsDict(msgs[m + 1]);
                                    if (nextItem != null && nextItem.ContainsKey("ts"))
                                    {
                                        long nextTsMs = ToUnixEpochMs(nextItem["ts"], fallbackDt);
                                        if (nextTsMs > tsMs && (nextTsMs - tsMs) <= 180000L)
                                            durMs = ComputeSafeDurationMs(tsMs, nextTsMs);
                                    }
                                }

                                string dk = uf + ":" + m.ToString(CultureInfo.InvariantCulture);
                                fileEvents.Add(new UsageEvent(label, mdl, dtStr, fi, outTok, cr, cw, cost, 1, dk, tsMs, durMs));
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

        private void ScanVsCodeClineFamily()
        {
            string roaming = Path.Combine(_home, "AppData", "Roaming");
            string[] ides = new string[] { "Code", "Code - Insiders", "Cursor", "Windsurf", "Antigravity", "VSCodium", "Trae" };
            JavaScriptSerializer js = null;

            for (int i = 0; i < ides.Length; i++)
            {
                for (int e = 0; e < ClineFamilyExtensions.GetLength(0); e++)
                {
                    string tasksDir = Path.Combine(roaming, ides[i], "User", "globalStorage", ClineFamilyExtensions[e, 1], "tasks");
                    string label = ClineFamilyExtensions[e, 0] + " (" + ides[i] + ")";
                    ScanClineTasksDir(tasksDir, label, ref js);
                }
            }
        }

        private void ScanWslRemoteClineFamily(string wslHome)
        {
            if (string.IsNullOrWhiteSpace(wslHome)) return;
            string[,] remoteServers = new string[,]
            {
                { "WSL Code", ".vscode-server" },
                { "WSL Code Insiders", ".vscode-server-insiders" },
                { "WSL Cursor", ".cursor-server" },
                { "WSL Windsurf", ".windsurf-server" },
                { "WSL Antigravity", ".antigravity-server" },
                { "WSL VSCodium", ".vscodium-server" },
                { "WSL Trae", ".trae-server" }
            };
            JavaScriptSerializer js = null;

            for (int s = 0; s < remoteServers.GetLength(0); s++)
            {
                string gsBase = Path.Combine(wslHome, remoteServers[s, 1], "data", "User", "globalStorage");
                try { if (!Directory.Exists(ToLongPath(gsBase))) continue; } catch { continue; }

                for (int e = 0; e < ClineFamilyExtensions.GetLength(0); e++)
                {
                    string tasksDir = Path.Combine(gsBase, ClineFamilyExtensions[e, 1], "tasks");
                    string label = ClineFamilyExtensions[e, 0] + " (" + remoteServers[s, 0] + ")";
                    ScanClineTasksDir(tasksDir, label, ref js);
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
            long dummyTs;
            int dummyDur;
            return TryExtractGenericUsage(obj, curModel, fallbackTime, out modelOut, out dateOut, out fiOut, out outTokOut, out crOut, out cwOut, out costOut, out dedupIdOut, out dummyTs, out dummyDur);
        }

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
            out string dedupIdOut,
            out long tsMsOut,
            out int durMsOut)
        {
            modelOut = curModel;
            dateOut = "";
            fiOut = 0; outTokOut = 0; crOut = 0; cwOut = 0;
            costOut = 0.0;
            dedupIdOut = "";
            tsMsOut = 0L;
            durMsOut = 0;
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
            Dictionary<string, object> promptDetails = AsDict(u.ContainsKey("prompt_tokens_details") ? u["prompt_tokens_details"] : null);
            crOut = GetLong(u, "cache_read_input_tokens");
            if (crOut == 0) crOut = GetLong(u, "cacheRead");
            if (crOut == 0) crOut = GetLong(u, "cache_read_tokens");
            if (crOut == 0) crOut = GetLong(u, "cached_input_tokens");
            if (crOut == 0) crOut = GetLong(u, "cached_tokens");
            if (crOut == 0) crOut = GetLong(u, "cachedContentTokenCount");
            if (crOut == 0) crOut = GetLong(u, "cached");
            if (crOut == 0) crOut = GetLong(u, "prompt_cache_hit_tokens");
            if (crOut == 0) crOut = GetLong(u, "inputCacheRead");
            if (crOut == 0 && cacheSub != null) crOut = GetLong(cacheSub, "read");
            if (crOut == 0 && promptDetails != null) crOut = GetLong(promptDetails, "cached_tokens");

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
            else if (((u.ContainsKey("prompt_tokens") || u.ContainsKey("promptTokenCount")) && crOut > 0 && rawIn >= crOut) ||
                     (tot > 0 && Math.Abs((rawIn + outTokOut) - tot) <= 4 && crOut > 0))
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
                : (obj.ContainsKey("time") ? obj["time"]
                : (msg != null && msg.ContainsKey("timestamp") ? msg["timestamp"]
                : (msg != null && msg.ContainsKey("created_at") ? msg["created_at"]
                : (meta != null && meta.ContainsKey("timestamp") ? meta["timestamp"] : null))))));

            Dictionary<string, object> tsDict = AsDict(tsObj);
            if (tsDict != null)
            {
                object startObj = tsDict.ContainsKey("created") ? tsDict["created"] : (tsDict.ContainsKey("start") ? tsDict["start"] : null);
                object endObj = tsDict.ContainsKey("completed") ? tsDict["completed"] : (tsDict.ContainsKey("end") ? tsDict["end"] : startObj);
                long startMs = ToUnixEpochMs(startObj, fallbackTime);
                long endMs = ToUnixEpochMs(endObj, fallbackTime);
                tsMsOut = endMs > 0 ? endMs : startMs;
                durMsOut = ComputeSafeDurationMs(startMs, endMs);
                tsObj = startObj ?? endObj;
            }
            else
            {
                tsMsOut = ToUnixEpochMs(tsObj, fallbackTime);
            }

            if (durMsOut == 0)
            {
                long expDur = GetLong(u, "durationMs");
                if (expDur == 0) expDur = GetLong(u, "duration_ms");
                if (expDur == 0) expDur = GetLong(obj, "durationMs");
                if (expDur == 0) expDur = GetLong(obj, "duration_ms");
                if (expDur == 0) expDur = GetLong(obj, "latencyMs");
                if (expDur == 0) expDur = GetLong(obj, "latency_ms");
                if (expDur == 0 && meta != null) expDur = GetLong(meta, "durationMs");
                if (expDur >= 100 && expDur <= 600000)
                    durMsOut = (int)expDur;
            }

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
                        if (ev == null) continue;
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
                        long lastAnchorMs = 0L;
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            idx++;
                            if (line.IndexOf("usage", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("tokens", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("input", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("model", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("timestamp", StringComparison.Ordinal) < 0 &&
                                line.IndexOf("created_at", StringComparison.Ordinal) < 0)
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
                            long fi, outTok, cr, cw, tsMs;
                            double cost;
                            int durMs;
                            if (!TryExtractGenericUsage(obj, curM, mtime, out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did, out tsMs, out durMs))
                            {
                                object rawStepTs = obj.ContainsKey("timestamp") ? obj["timestamp"] : (obj.ContainsKey("created_at") ? obj["created_at"] : null);
                                if (rawStepTs != null)
                                {
                                    long stepMs = ToUnixEpochMs(rawStepTs, mtime);
                                    if (stepMs > 0) lastAnchorMs = stepMs;
                                }
                                continue;
                            }

                            if (durMs == 0 && lastAnchorMs > 0 && tsMs > lastAnchorMs)
                                durMs = ComputeSafeDurationMs(lastAnchorMs, tsMs);
                            if (tsMs > 0)
                                lastAnchorMs = tsMs;

                            curM = mdl;
                            string key = !string.IsNullOrEmpty(did) ? did : (fp + ":L" + idx.ToString(CultureInfo.InvariantCulture));
                            UsageEvent prev;
                            if (!fileDedup.TryGetValue(key, out prev) ||
                                (fi + outTok + cr + cw) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                            {
                                if (durMs == 0 && prev != null && prev.DurationMs > 0) durMs = prev.DurationMs;
                                fileDedup[key] = new UsageEvent(toolName, mdl, dt, fi, outTok, cr, cw, cost, 1, key, tsMs, durMs);
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
                    if (ev == null) continue;
                    string dk = string.IsNullOrEmpty(ev.DedupKey) ? (fp + ":e" + e.ToString(CultureInfo.InvariantCulture)) : ev.DedupKey;
                    UsageEvent prev;
                    if (!dedup.TryGetValue(dk, out prev) ||
                        (ev.FreshInput + ev.Output + ev.CacheRead + ev.CacheWrite) >= (prev.FreshInput + prev.Output + prev.CacheRead + prev.CacheWrite))
                    {
                        dedup[dk] = ev;
                    }
                }
            }

            foreach (UsageEvent ev in dedup.Values)
                _events.Add(ev);
        }

        private void ScanOpenClaw(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            try
            {
                string[] dirs = Directory.GetDirectories(ToLongPath(homeRoot), ".openclaw*");
                for (int i = 0; i < dirs.Length; i++)
                {
                    string d = dirs[i];
                    if (d.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                        d = @"\\" + d.Substring(8);
                    else if (d.StartsWith(@"\\?\", StringComparison.Ordinal))
                        d = d.Substring(4);
                    ScanGenericJsonlTree("OpenClaw", Path.Combine(d, "agents"), "claude-sonnet-4-6", "*.jsonl");
                    ScanGenericJsonlTree("OpenClaw", Path.Combine(d, "sessions"), "claude-sonnet-4-6", "*.jsonl");
                }
            }
            catch { }
        }

        private void ScanPiAndOmp(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            ScanGenericJsonlTree("Pi Agent", Path.Combine(homeRoot, ".pi", "agent", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Pi Agent", Path.Combine(homeRoot, ".pi", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Oh My Pi", Path.Combine(homeRoot, ".omp", "agent", "sessions"), "claude-sonnet-4-6", "*.jsonl");
            ScanGenericJsonlTree("Oh My Pi", Path.Combine(homeRoot, ".omp", "sessions"), "claude-sonnet-4-6", "*.jsonl");
        }

        private void ScanQwenCode(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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
                                    string mdl = PricingEngine.NormalizeModelId(GetStr(item, "model"), "qwen3-coder-plus");
                                    long tsMs = curMsgMs > 0 ? curMsgMs : ToUnixEpochMs(null, mtime);
                                    int durMs = ComputeSafeDurationMs(prevMsgMs, tsMs);
                                    string dk = fp + ":" + m.ToString(CultureInfo.InvariantCulture);
                                    fileEvents.Add(new UsageEvent("Qwen Code", mdl, ToLocalDate(rawTsObj, mtime), fi, outTok, cr, 0L, 0.0, 1, dk, tsMs, durMs));
                                }
                                if (curMsgMs > 0) prevMsgMs = curMsgMs;
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

        private void ScanFactoryDroid(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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
                    string mdl, dt, did; long fi, outTok, cr, cw, tsMs; double cost; int durMs;
                    if (TryExtractGenericUsage(obj, "claude-sonnet-4-6", FileTimeTicksToLocal(fMtime), out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did, out tsMs, out durMs))
                        fileEvents.Add(new UsageEvent("Factory Droid", mdl, dt, fi, outTok, cr, cw, cost, 1, did, tsMs, durMs));
                }
                catch { }
                SaveDirtyCursor(fp, fSize, fMtime, fSize, "", fileEvents);
                _events.AddRange(fileEvents);
            }
        }

        private void ScanAmp(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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
                                    string mdl, dt, did; long fi, outTok, cr, cw, tsMs; double cost; int durMs;
                                    if (TryExtractGenericUsage(AsDict(msgs[m]), "claude-sonnet-4-6", mtime, out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did, out tsMs, out durMs))
                                    {
                                        string dk = !string.IsNullOrEmpty(did) ? did : (fp + ":" + m.ToString(CultureInfo.InvariantCulture));
                                        fileEvents.Add(new UsageEvent("Amp", mdl, dt, fi, outTok, cr, cw, cost, 1, dk, tsMs, durMs));
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
        }

        private void ScanGoose(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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

                Dictionary<string, string> oldDates = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, long> oldTsMap = new Dictionary<string, long>(StringComparer.Ordinal);
                if (cur != null && cur.Events != null)
                {
                    for (int e = 0; e < cur.Events.Count; e++)
                    {
                        UsageEvent oe = cur.Events[e];
                        if (oe != null && !string.IsNullOrEmpty(oe.DedupKey))
                        {
                            if (!string.IsNullOrEmpty(oe.Date)) oldDates[oe.DedupKey] = oe.Date;
                            if (oe.TimestampMs > 0L) oldTsMap[oe.DedupKey] = oe.TimestampMs;
                        }
                    }
                }

                List<UsageEvent> dbEvents = new List<UsageEvent>();
                bool dbOpened = false;
                DateTime fallbackDt = FileTimeTicksToLocal(cMtime);
                try
                {
                    using (WinSqlite db = WinSqlite.OpenReadOnly(sdb))
                    {
                        if (db != null)
                        {
                            dbOpened = true;
                            bool hasId = false;
                            bool hasModel = false;
                            db.Query("PRAGMA table_info(sessions)", delegate(WinSqlite.SqliteRow cr)
                            {
                                string cn = cr.GetString(1);
                                if (string.Equals(cn, "id", StringComparison.OrdinalIgnoreCase)) hasId = true;
                                else if (string.Equals(cn, "model", StringComparison.OrdinalIgnoreCase)) hasModel = true;
                            });

                            string idExpr = hasId ? "CAST(id AS TEXT)" : "CAST(rowid AS TEXT)";
                            string mdlExpr = hasModel ? "model" : "''";
                            string sql = "SELECT " + idExpr + ", input_tokens, output_tokens, created_at, " + mdlExpr + " FROM sessions WHERE input_tokens + output_tokens > 0";
                            db.Query(sql, delegate(WinSqlite.SqliteRow row)
                            {
                                string sid = row.GetString(0);
                                long fi = row.GetInt64(1);
                                long outTok = row.GetInt64(2);
                                string ts = row.GetString(3);
                                string rawM = row.GetString(4);
                                if (fi + outTok <= 0) return;

                                string dk = "goose:" + (string.IsNullOrEmpty(sid) ? dbEvents.Count.ToString(CultureInfo.InvariantCulture) : sid);
                                string dtStr;
                                long tsMs;
                                if (!string.IsNullOrWhiteSpace(ts))
                                {
                                    dtStr = ToLocalDate(ts, fallbackDt);
                                    tsMs = ToUnixEpochMs(ts, fallbackDt);
                                }
                                else
                                {
                                    if (!oldDates.TryGetValue(dk, out dtStr) || string.IsNullOrEmpty(dtStr))
                                        dtStr = ToLocalDate(null, fallbackDt);
                                    if (!oldTsMap.TryGetValue(dk, out tsMs) || tsMs <= 0L)
                                        tsMs = ToUnixEpochMs(null, fallbackDt);
                                }
                                string mdl = PricingEngine.NormalizeModelId(rawM, "claude-sonnet-4-6");
                                dbEvents.Add(new UsageEvent("Goose", mdl, dtStr, fi, outTok, 0L, 0L, 0.0, 1, dk, tsMs, 0));
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
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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

                Dictionary<string, string> oldDates = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, long> oldTsMap = new Dictionary<string, long>(StringComparer.Ordinal);
                if (cur != null && cur.Events != null)
                {
                    for (int e = 0; e < cur.Events.Count; e++)
                    {
                        UsageEvent oe = cur.Events[e];
                        if (oe != null && !string.IsNullOrEmpty(oe.DedupKey))
                        {
                            if (!string.IsNullOrEmpty(oe.Date)) oldDates[oe.DedupKey] = oe.Date;
                            if (oe.TimestampMs > 0L) oldTsMap[oe.DedupKey] = oe.TimestampMs;
                        }
                    }
                }

                List<UsageEvent> dbEvents = new List<UsageEvent>();
                bool dbOpened = false;
                DateTime fallbackDt = FileTimeTicksToLocal(cMtime);
                try
                {
                    using (WinSqlite db = WinSqlite.OpenReadOnly(cdb))
                    {
                        if (db != null)
                        {
                            dbOpened = true;
                            bool hasId = false;
                            bool hasModel = false;
                            db.Query("PRAGMA table_info(sessions)", delegate(WinSqlite.SqliteRow cr)
                            {
                                string cn = cr.GetString(1);
                                if (string.Equals(cn, "id", StringComparison.OrdinalIgnoreCase)) hasId = true;
                                else if (string.Equals(cn, "model", StringComparison.OrdinalIgnoreCase)) hasModel = true;
                            });

                            string idExpr = hasId ? "CAST(id AS TEXT)" : "CAST(rowid AS TEXT)";
                            string mdlExpr = hasModel ? "model" : "''";
                            string sql = "SELECT " + idExpr + ", prompt_tokens, completion_tokens, cost, created_at, " + mdlExpr + " FROM sessions WHERE prompt_tokens + completion_tokens > 0";
                            db.Query(sql, delegate(WinSqlite.SqliteRow row)
                            {
                                string sid = row.GetString(0);
                                long fi = row.GetInt64(1);
                                long outTok = row.GetInt64(2);
                                double cost = row.GetDouble(3);
                                long ts = row.GetInt64(4);
                                string rawM = row.GetString(5);
                                if (fi + outTok <= 0) return;

                                string dk = "crush:" + (string.IsNullOrEmpty(sid) ? dbEvents.Count.ToString(CultureInfo.InvariantCulture) : sid);
                                string dtStr;
                                long tsMs;
                                if (ts > 0L)
                                {
                                    dtStr = ToLocalDate(ts, fallbackDt);
                                    tsMs = ToUnixEpochMs(ts, fallbackDt);
                                }
                                else
                                {
                                    if (!oldDates.TryGetValue(dk, out dtStr) || string.IsNullOrEmpty(dtStr))
                                        dtStr = ToLocalDate(null, fallbackDt);
                                    if (!oldTsMap.TryGetValue(dk, out tsMs) || tsMs <= 0L)
                                        tsMs = ToUnixEpochMs(null, fallbackDt);
                                }
                                string mdl = PricingEngine.NormalizeModelId(rawM, "claude-sonnet-4-6");
                                dbEvents.Add(new UsageEvent("Charm Crush", mdl, dtStr, fi, outTok, 0L, 0L, cost, 1, dk, tsMs, 0));
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
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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
                    using (FileStream fs = new FileStream(ToLongPath(fp), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        int lineNo = 0;
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            lineNo++;
                            if (line.IndexOf("tokens", StringComparison.Ordinal) < 0) continue;
                            Dictionary<string, object> obj;
                            try { obj = AsDict(js.DeserializeObject(line)); } catch { continue; }
                            if (obj == null) continue;
                            Dictionary<string, object> props = AsDict(obj.ContainsKey("properties") ? obj["properties"] : null) ?? obj;
                            long rawIn = GetLong(props, "prompt_tokens");
                            if (rawIn == 0) rawIn = GetLong(props, "sent_tokens");
                            long outTok = GetLong(props, "completion_tokens");
                            if (outTok == 0) outTok = GetLong(props, "received_tokens");
                            long cr = GetLong(props, "cache_read_tokens");
                            long cw = GetLong(props, "cache_creation_tokens");
                            long fi = (rawIn >= cr && cr > 0 && props.ContainsKey("prompt_tokens")) ? (rawIn - cr) : rawIn;
                            if (fi + outTok + cr + cw > 0)
                            {
                                string mdl = PricingEngine.NormalizeModelId(GetStr(props, "main_model") ?? GetStr(props, "model"), "claude-sonnet-4-6");
                                double cost = GetDouble(props, "cost");
                                if (cost <= 0) cost = GetDouble(props, "message_cost");
                                object rawTime = obj.ContainsKey("time") ? obj["time"] : (props.ContainsKey("time") ? props["time"] : null);
                                long tsMs = ToUnixEpochMs(rawTime, mtime);
                                string dk = fp + ":L" + lineNo.ToString(CultureInfo.InvariantCulture);
                                fileEvents.Add(new UsageEvent("Aider", mdl, ToLocalDate(rawTime, mtime), fi, outTok, cr, cw, cost, 1, dk, tsMs, 0));
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

        private void ScanContinue(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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
                                    string mdl, dt, did; long fi, outTok, cr, cw, tsMs; double cost; int durMs;
                                    if (TryExtractGenericUsage(AsDict(logs[l]), "claude-sonnet-4-6", mtime, out mdl, out dt, out fi, out outTok, out cr, out cw, out cost, out did, out tsMs, out durMs))
                                    {
                                        string dk = !string.IsNullOrEmpty(did) ? did : (fp + ":" + h.ToString(CultureInfo.InvariantCulture) + ":" + l.ToString(CultureInfo.InvariantCulture));
                                        fileEvents.Add(new UsageEvent("Continue.dev", mdl, dt, fi, outTok, cr, cw, cost, 1, dk, tsMs, durMs));
                                    }
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
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
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
            Dictionary<string, long> oldTsMap = new Dictionary<string, long>(StringComparer.Ordinal);
            if (cur != null && cur.Events != null)
            {
                for (int i = 0; i < cur.Events.Count; i++)
                {
                    UsageEvent oe = cur.Events[i];
                    if (oe != null && !string.IsNullOrEmpty(oe.DedupKey))
                    {
                        if (!string.IsNullOrEmpty(oe.Date)) oldDates[oe.DedupKey] = oe.Date;
                        if (oe.TimestampMs > 0L) oldTsMap[oe.DedupKey] = oe.TimestampMs;
                    }
                }
            }

            List<UsageEvent> dbEvents = new List<UsageEvent>();
            bool dbOpened = false;
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
                    dbOpened = true;
                    db.Query("SELECT key, value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND value LIKE '%tokenCount%'", delegate(WinSqlite.SqliteRow row)
                    {
                        string bKey = row.GetString(0);
                        string val = row.GetString(1);
                        if (string.IsNullOrEmpty(val)) return;
                        int tcStart, tcEnd;
                        if (!FastJson.FindObjectBounds(val, "\"tokenCount\"", 0, val.Length, out tcStart, out tcEnd))
                            return;

                        long rawIn = FastJson.ExtractLong(val, "\"inputTokens\"", tcStart, tcEnd);
                        long outTok = FastJson.ExtractLong(val, "\"outputTokens\"", tcStart, tcEnd);
                        long cr = FastJson.ExtractLong(val, "\"cacheReadInputTokens\"", tcStart, tcEnd);
                        if (cr == 0) cr = FastJson.ExtractLong(val, "\"cachedTokens\"", tcStart, tcEnd);
                        long cw = FastJson.ExtractLong(val, "\"cacheWriteInputTokens\"", tcStart, tcEnd);
                        long fi = rawIn;
                        if (fi + outTok + cr + cw > 0)
                        {
                            string rawM = null;
                            int miStart, miEnd;
                            if (FastJson.FindObjectBounds(val, "\"modelInfo\"", 0, val.Length, out miStart, out miEnd))
                                rawM = FastJson.ExtractString(val, "\"modelName\"", miStart, miEnd);
                            if (string.IsNullOrEmpty(rawM))
                                rawM = FastJson.ExtractString(val, "\"model\"", 0, val.Length);
                            string mdl = PricingEngine.NormalizeModelId(rawM, "claude-sonnet-4-6");

                            long startMs = 0L;
                            long endMs = 0L;
                            int tiStart, tiEnd;
                            if (FastJson.FindObjectBounds(val, "\"timingInfo\"", 0, val.Length, out tiStart, out tiEnd))
                            {
                                long rpcSend = FastJson.ExtractLong(val, "\"clientRpcSendTime\"", tiStart, tiEnd);
                                if (rpcSend == 0L) rpcSend = FastJson.ExtractLong(val, "\"clientStartTime\"", tiStart, tiEnd);
                                long settle = FastJson.ExtractLong(val, "\"clientSettleTime\"", tiStart, tiEnd);
                                if (settle == 0L) settle = FastJson.ExtractLong(val, "\"clientEndTime\"", tiStart, tiEnd);
                                if (rpcSend > 0L) startMs = ToUnixEpochMs(rpcSend, mtime);
                                if (settle > 0L) endMs = ToUnixEpochMs(settle, mtime);
                            }

                            string cAt = FastJson.ExtractRawScalar(val, "\"createdAt\"", 0, val.Length);
                            long createdMs = !string.IsNullOrEmpty(cAt) ? ToUnixEpochMs(cAt, mtime) : 0L;

                            long tsMs = endMs > 0L ? endMs : (createdMs > 0L ? createdMs : startMs);
                            int durMs = ComputeSafeDurationMs(startMs > 0L ? startMs : createdMs, endMs);

                            string dtStr;
                            if (!string.IsNullOrEmpty(cAt))
                            {
                                dtStr = ToLocalDate(cAt, mtime);
                            }
                            else if (tsMs > 0L)
                            {
                                dtStr = ToLocalDate(tsMs, mtime);
                            }
                            else
                            {
                                string prevDate;
                                if (!string.IsNullOrEmpty(bKey) && oldDates.TryGetValue(bKey, out prevDate) && !string.IsNullOrEmpty(prevDate))
                                    dtStr = prevDate;
                                else
                                    dtStr = ToLocalDate(null, mtime);

                                long prevTs;
                                if (!string.IsNullOrEmpty(bKey) && oldTsMap.TryGetValue(bKey, out prevTs) && prevTs > 0L)
                                    tsMs = prevTs;
                                else
                                    tsMs = ToUnixEpochMs(null, mtime);
                            }

                            dbEvents.Add(new UsageEvent("Cursor", mdl, dtStr, fi, outTok, cr, cw, 0.0, 1, bKey, tsMs, durMs));
                        }
                    });
                }
            }
            catch { }

            if (!dbOpened && cur != null && cur.Events != null)
            {
                _events.AddRange(cur.Events);
                return;
            }

            dbEvents = MergeSqliteCursorEvents(cur, dbEvents);
            SaveDirtyCursor(vscdb, cSize, cMtime, cSize, "", dbEvents);
            _events.AddRange(dbEvents);
        }

        private void ScanZed()
        {
            ScanZedAt(_home, false);
        }

        private void ScanZedAt(string homeRoot, bool isWsl)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string zdb = isWsl
                ? Path.Combine(homeRoot, ".local", "share", "zed", "threads", "threads.db")
                : Path.Combine(homeRoot, "AppData", "Local", "Zed", "threads", "threads.db");

            long cSize, cMtime;
            if (!TryGetSqliteCombinedStat(zdb, out cSize, out cMtime)) return;
            TrackVisitedFile(zdb);

            FileCursorEntry cur;
            if (_cursors.TryGetValue(zdb, out cur) && cur.FileSize == cSize && cur.MtimeTicks == cMtime)
            {
                _events.AddRange(cur.Events);
                return;
            }

            // Group settled historical daily slices per thread_id so cumulative_token_usage never double-counts across days
            Dictionary<string, List<UsageEvent>> priorByTid = new Dictionary<string, List<UsageEvent>>(StringComparer.Ordinal);
            if (cur != null && cur.Events != null)
            {
                for (int i = 0; i < cur.Events.Count; i++)
                {
                    UsageEvent oe = cur.Events[i];
                    if (oe == null || string.IsNullOrEmpty(oe.DedupKey)) continue;
                    int atIdx = oe.DedupKey.LastIndexOf('@');
                    string baseTid = atIdx > 0 ? oe.DedupKey.Substring(0, atIdx) : oe.DedupKey;
                    if (string.IsNullOrEmpty(baseTid)) continue;

                    List<UsageEvent> slices;
                    if (!priorByTid.TryGetValue(baseTid, out slices))
                    {
                        slices = new List<UsageEvent>();
                        priorByTid[baseTid] = slices;
                    }
                    UsageEvent normEv = atIdx > 0
                        ? oe.Clone()
                        : new UsageEvent(oe.Tool, oe.Model, oe.Date, oe.FreshInput, oe.Output, oe.CacheRead, oe.CacheWrite, oe.LoggedCostUsd, oe.Turns, baseTid + "@" + (oe.Date ?? ""), oe.TimestampMs, oe.DurationMs);
                    slices.Add(normEv);
                }
            }

            List<UsageEvent> dbEvents = new List<UsageEvent>();
            HashSet<string> seenLiveTids = new HashSet<string>(StringComparer.Ordinal);
            bool dbOpened = false;
            DateTime fallbackDt = FileTimeTicksToLocal(cMtime);
            string toolLabel = isWsl ? "Zed AI (WSL)" : "Zed AI";

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
                    dbOpened = true;
                    bool hasIdCol = false;
                    db.Query("PRAGMA table_info(threads)", delegate(WinSqlite.SqliteRow colRow)
                    {
                        if (string.Equals(colRow.GetString(1), "id", StringComparison.OrdinalIgnoreCase))
                            hasIdCol = true;
                    });

                    string zSql = hasIdCol ? "SELECT id, updated_at, data FROM threads" : "SELECT CAST(rowid AS TEXT), updated_at, data FROM threads";
                    int rowSeq = 0;
                    db.Query(zSql, delegate(WinSqlite.SqliteRow row)
                    {
                        rowSeq++;
                        string rowId = row.GetString(0);
                        string upd = row.GetString(1);
                        string ds = row.GetString(2);
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
                            if (fi + outTok + cr + cw <= 0) return;

                            Dictionary<string, object> mObj = AsDict(d.ContainsKey("model") ? d["model"] : null);
                            string mdl = PricingEngine.NormalizeModelId(GetStr(mObj, "name") ?? GetStr(d, "model"), "claude-sonnet-4-6");
                            string tid = !string.IsNullOrEmpty(rowId) ? rowId : (GetStr(d, "id") ?? GetStr(d, "thread_id") ?? ("r" + rowSeq.ToString(CultureInfo.InvariantCulture)));
                            seenLiveTids.Add(tid);

                            List<UsageEvent> priorSlices;
                            priorByTid.TryGetValue(tid, out priorSlices);

                            string updDate;
                            long tsMs;
                            if (!string.IsNullOrWhiteSpace(upd))
                            {
                                updDate = ToLocalDate(upd, fallbackDt);
                                tsMs = ToUnixEpochMs(upd, fallbackDt);
                            }
                            else if (priorSlices != null && priorSlices.Count > 0)
                            {
                                UsageEvent lastPrior = priorSlices[priorSlices.Count - 1];
                                updDate = !string.IsNullOrEmpty(lastPrior.Date) ? lastPrior.Date : ToLocalDate(null, fallbackDt);
                                tsMs = lastPrior.TimestampMs > 0L ? lastPrior.TimestampMs : ToUnixEpochMs(null, fallbackDt);
                            }
                            else
                            {
                                updDate = ToLocalDate(null, fallbackDt);
                                tsMs = ToUnixEpochMs(null, fallbackDt);
                            }

                            long settledFi = 0L, settledOut = 0L, settledCr = 0L, settledCw = 0L;
                            UsageEvent sameDayPrior = null;
                            if (priorSlices != null)
                            {
                                for (int p = 0; p < priorSlices.Count; p++)
                                {
                                    UsageEvent ps = priorSlices[p];
                                    if (ps == null) continue;
                                    if (string.Equals(ps.Date, updDate, StringComparison.Ordinal))
                                    {
                                        sameDayPrior = ps;
                                    }
                                    else
                                    {
                                        settledFi += ps.FreshInput;
                                        settledOut += ps.Output;
                                        settledCr += ps.CacheRead;
                                        settledCw += ps.CacheWrite;
                                    }
                                }
                            }

                            long cumTotal = fi + outTok + cr + cw;
                            long settledTotal = settledFi + settledOut + settledCr + settledCw;
                            if (cumTotal < settledTotal && priorSlices != null)
                            {
                                // Thread was truncated below historical settled watermarks: preserve settled slices without adding duplicate delta
                                for (int p = 0; p < priorSlices.Count; p++)
                                {
                                    if (priorSlices[p] != null)
                                        dbEvents.Add(priorSlices[p]);
                                }
                                return;
                            }

                            if (priorSlices != null)
                            {
                                for (int p = 0; p < priorSlices.Count; p++)
                                {
                                    UsageEvent ps = priorSlices[p];
                                    if (ps != null && !string.Equals(ps.Date, updDate, StringComparison.Ordinal))
                                        dbEvents.Add(ps);
                                }
                            }

                            long deltaFi = Math.Max(0L, fi - settledFi);
                            long deltaOut = Math.Max(0L, outTok - settledOut);
                            long deltaCr = Math.Max(0L, cr - settledCr);
                            long deltaCw = Math.Max(0L, cw - settledCw);
                            if (sameDayPrior != null)
                            {
                                deltaFi = Math.Max(deltaFi, sameDayPrior.FreshInput);
                                deltaOut = Math.Max(deltaOut, sameDayPrior.Output);
                                deltaCr = Math.Max(deltaCr, sameDayPrior.CacheRead);
                                deltaCw = Math.Max(deltaCw, sameDayPrior.CacheWrite);
                            }

                            if (deltaFi + deltaOut + deltaCr + deltaCw > 0)
                            {
                                string sliceKey = tid + "@" + updDate;
                                dbEvents.Add(new UsageEvent(toolLabel, mdl, updDate, deltaFi, deltaOut, deltaCr, deltaCw, 0.0, 1, sliceKey, tsMs, 0));
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }

            if (!dbOpened && cur != null && cur.Events != null)
            {
                _events.AddRange(cur.Events);
                return;
            }

            // Preserve historical slices for threads deleted from threads.db
            foreach (KeyValuePair<string, List<UsageEvent>> kv in priorByTid)
            {
                if (!seenLiveTids.Contains(kv.Key) && kv.Value != null)
                {
                    for (int p = 0; p < kv.Value.Count; p++)
                    {
                        if (kv.Value[p] != null)
                            dbEvents.Add(kv.Value[p]);
                    }
                }
            }

            SaveDirtyCursor(zdb, cSize, cMtime, cSize, "", dbEvents);
            _events.AddRange(dbEvents);
        }

        private void ScanCustomRules(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string cfgPath = Path.Combine(homeRoot, ".toklens", "harnesses.json");
            try
            {
                if (!File.Exists(ToLongPath(cfgPath))) return;
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

        private static List<string> DiscoverWslDistroRoots()
        {
            List<string> distroNames = new List<string>();
            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (RegistryKey lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (lxss != null)
                    {
                        string[] subKeys = lxss.GetSubKeyNames();
                        for (int i = 0; i < subKeys.Length; i++)
                        {
                            try
                            {
                                using (RegistryKey sk = lxss.OpenSubKey(subKeys[i]))
                                {
                                    if (sk == null) continue;
                                    string dName = sk.GetValue("DistributionName") as string;
                                    if (string.IsNullOrWhiteSpace(dName)) continue;
                                    dName = dName.Trim();
                                    if (string.Equals(dName, "docker-desktop-data", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(dName, "docker-desktop", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    if (seenNames.Add(dName))
                                        distroNames.Add(dName);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            if (distroNames.Count == 0)
            {
                string[] fallbacks = new string[] { "Ubuntu", "Ubuntu-24.04", "Ubuntu-22.04", "Ubuntu-20.04", "Debian", "kali-linux", "Arch" };
                for (int i = 0; i < fallbacks.Length; i++)
                {
                    if (seenNames.Add(fallbacks[i]))
                        distroNames.Add(fallbacks[i]);
                }
            }

            List<string> roots = new List<string>();
            string[] uncHosts = new string[] { @"\\wsl.localhost\", @"\\wsl$\" };
            for (int d = 0; d < distroNames.Count; d++)
            {
                string name = distroNames[d];
                for (int h = 0; h < uncHosts.Length; h++)
                {
                    string cand = uncHosts[h] + name;
                    try
                    {
                        if (Directory.Exists(ToLongPath(Path.Combine(cand, "home"))) ||
                            Directory.Exists(ToLongPath(Path.Combine(cand, "root"))))
                        {
                            roots.Add(cand);
                            break;
                        }
                    }
                    catch { }
                }
            }
            return roots;
        }

        private void ScanLocalWslHomes()
        {
            try
            {
                List<string> distroRoots = DiscoverWslDistroRoots();
                for (int d = 0; d < distroRoots.Count; d++)
                {
                    string distroRoot = distroRoots[d];
                    List<string> wslHomes = new List<string>();
                    HashSet<string> seenHomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    string homeBase = Path.Combine(distroRoot, "home");
                    try
                    {
                        if (Directory.Exists(ToLongPath(homeBase)))
                        {
                            string[] userHomes = Directory.GetDirectories(ToLongPath(homeBase));
                            for (int u = 0; u < userHomes.Length; u++)
                            {
                                string uh = userHomes[u];
                                if (uh.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                                    uh = @"\\" + uh.Substring(8);
                                else if (uh.StartsWith(@"\\?\", StringComparison.Ordinal))
                                    uh = uh.Substring(4);
                                if (seenHomes.Add(uh))
                                    wslHomes.Add(uh);
                            }
                        }
                    }
                    catch { }

                    string rootHome = Path.Combine(distroRoot, "root");
                    try
                    {
                        if (Directory.Exists(ToLongPath(rootHome)))
                        {
                            // Probe read access to /root before adding
                            Directory.GetDirectories(ToLongPath(rootHome));
                            if (seenHomes.Add(rootHome))
                                wslHomes.Add(rootHome);
                        }
                    }
                    catch { }

                    for (int u = 0; u < wslHomes.Count; u++)
                    {
                        string wh = wslHomes[u];
                        ScanClaudeAt(wh, true);
                        ScanCodexLiveAt(wh, "OpenAI Codex (WSL)");
                        ScanCodexSqliteArchiveAt(wh, "OpenAI Codex (WSL)");
                        ScanGeminiCliAt(wh, "Gemini CLI (WSL)");
                        ScanKimiCodeAt(wh, "Kimi Code (WSL)");
                        ScanZCodeAt(wh, "ZCode (WSL)");
                        ScanMobileClawAt(wh, "MobileClaw (WSL)");
                        ScanOpenCodeAt(wh, "OpenCode (WSL)");
                        ScanWorkBuddyAndCodeBuddyAt(wh, " (WSL)");
                        ScanDshAt(wh, "DeepSeek (DSH)");
                        ScanCommandCodeAt(wh, "Command Code (WSL)");
                        ScanWslRemoteClineFamily(wh);
                        ScanOpenClaw(wh);
                        ScanPiAndOmp(wh);
                        ScanQwenCode(wh);
                        ScanFactoryDroid(wh);
                        ScanAmp(wh);
                        ScanGoose(wh);
                        ScanCrush(wh);
                        ScanAider(wh);
                        ScanContinue(wh);
                        ScanVibeAndAgents(wh);
                        ScanZedAt(wh, true);
                        ScanCustomRules(wh);
                    }
                }
            }
            catch { }
        }

        // -------------------- 13. Native Codex SQLite Archive (~/.codex/state_5.sqlite only) --------------------
        private void ScanCodexSqliteArchive()
        {
            ScanCodexSqliteArchiveAt(_home, "OpenAI Codex");
        }

        private void ScanCodexSqliteArchiveAt(string homeRoot, string toolName)
        {
            if (string.IsNullOrWhiteSpace(homeRoot)) return;
            string stDb = Path.Combine(homeRoot, ".codex", "state_5.sqlite");
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

                List<UsageEvent> freshThreads = new List<UsageEvent>();
                bool dbOpened = false;
                DateTime fallbackDt = FileTimeTicksToLocal(cMtime);
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
                                string mdl = PricingEngine.NormalizeModelId(model, "gpt-5.2-codex");
                                string dtStr;
                                if (createdAt > 0L)
                                    dtStr = ToLocalDate(createdAt, fallbackDt);
                                else if (!string.IsNullOrEmpty(tid) && oldDates.TryGetValue(tid, out dtStr) && !string.IsNullOrEmpty(dtStr))
                                { }
                                else
                                    dtStr = ToLocalDate(null, fallbackDt);

                                freshThreads.Add(new UsageEvent(toolName, mdl, dtStr, fi, outTok, cr, 0L, 0.0, 1, tid, 0L, -1));
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
                _pricing.ComputeBucketCost(ev.Model, ev.Turns, ev.FreshInput, ev.Output, ev.CacheRead, ev.CacheWrite, ev.CacheWrite1h, ev.LoggedCostUsd, out actualUsd, out baseUsd, out rate);

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
                b.Turns = b.Turns >= 1000000000 - Math.Max(0, ev.Turns) ? 1000000000 : (b.Turns + Math.Max(0, ev.Turns));
                b.FreshInput = SaturatingAddTokens(b.FreshInput, ev.FreshInput);
                b.Output = SaturatingAddTokens(b.Output, ev.Output);
                b.CacheRead = SaturatingAddTokens(b.CacheRead, ev.CacheRead);
                b.CacheWrite = SaturatingAddTokens(b.CacheWrite, ev.CacheWrite);
                b.CostUsd += actualUsd;
                if (double.IsNaN(b.CostUsd) || double.IsInfinity(b.CostUsd) || b.CostUsd < 0.0) b.CostUsd = 0.0;
                else if (b.CostUsd > 1000000000000.0) b.CostUsd = 1000000000000.0;
                b.BaselineUsd += baseUsd;
                if (double.IsNaN(b.BaselineUsd) || double.IsInfinity(b.BaselineUsd) || b.BaselineUsd < 0.0) b.BaselineUsd = 0.0;
                else if (b.BaselineUsd > 1000000000000.0) b.BaselineUsd = 1000000000000.0;

                if (IsValidIsoCalendarDate(ev.Date) && string.CompareOrdinal(ev.Date, latestDate) > 0)
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
                rr.Date = IsValidIsoCalendarDate(src.Date) ? src.Date : DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                rr.TimeLabel = FormatEpochTimeLabel(src.TimestampMs);
                rr.Tool = src.Tool;
                rr.Model = src.Model;
                rr.FreshInput = ClampSafeTokens(src.FreshInput);
                rr.CacheRead = ClampSafeTokens(src.CacheRead);
                rr.CacheWrite = ClampSafeTokens(src.CacheWrite);
                rr.Output = ClampSafeTokens(src.Output);
                rr.DurationMs = src.DurationMs >= 0 ? src.DurationMs : 0;
                double rcUsd = liveCandidates[i].CostUsd;
                rr.CostUsd = (double.IsNaN(rcUsd) || double.IsInfinity(rcUsd) || rcUsd < 0.0) ? 0.0 : Math.Min(1000000000000.0, rcUsd);
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
                if (IsValidIsoCalendarDate(d) && string.CompareOrdinal(d, latestDate) > 0)
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
            if (val <= 0.0000005 || double.IsNaN(val) || double.IsInfinity(val)) return sym + "0.00";
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

        private static string SafeCsvQuote(string val)
        {
            string s = val ?? "";
            if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' || s[0] == '\t' || s[0] == '\r'))
                s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public static void ExportCsv(List<RecordBucket> rows, double rate, string outPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("日期,所属设备,AI 工具,模型名称,调用次数,新增输入 Token,缓存读取 Token,缓存写入 Token,输出 Token,总 Token,单价($/1M In/Out),等效美元($),等效人民币(¥)");
            for (int i = 0; i < rows.Count; i++)
            {
                RecordBucket r = rows[i];
                string mName = string.IsNullOrEmpty(r.MachineName) ? "本机" : r.MachineName;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11:F4},{12:F4}\r\n",
                    SafeCsvQuote(r.Date), SafeCsvQuote(mName), SafeCsvQuote(r.Tool), SafeCsvQuote(r.Model),
                    r.Turns, r.FreshInput, r.CacheRead, r.CacheWrite, r.Output, r.TotalTokens,
                    SafeCsvQuote(r.RateLabel), r.CostUsd, r.CostUsd * rate);
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
                double rCost = (double.IsNaN(r.CostUsd) || double.IsInfinity(r.CostUsd) || r.CostUsd < 0.0) ? 0.0 : Math.Min(1000000000000.0, r.CostUsd);
                double rBase = (double.IsNaN(r.BaselineUsd) || double.IsInfinity(r.BaselineUsd) || r.BaselineUsd < 0.0) ? 0.0 : Math.Min(1000000000000.0, r.BaselineUsd);
                totCost = Math.Min(100000000000000.0, totCost + rCost);
                totBase = Math.Min(100000000000000.0, totBase + rBase);

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
                tv.CostUsd = Math.Min(100000000000000.0, tv.CostUsd + rCost);
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
            root["app"] = AppConstants.ProductTitle;
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
