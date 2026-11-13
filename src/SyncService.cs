using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TokLens
{
    public sealed class SyncOutcome
    {
        public bool Success;
        public string Message;
        public int RemoteNodeCount;
        public List<NodeSnapshot> RemoteNodes = new List<NodeSnapshot>();
    }

    public static class SyncService
    {
        public const string SchemaVersion = "toklens.node.v2";
        private static readonly Regex SafeIdRegex = new Regex(@"[^a-zA-Z0-9_-]+", RegexOptions.Compiled);
        private static readonly Regex WebDavNodeRegex = new Regex(@"(toklens_node_[a-zA-Z0-9_-]+\.json)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly ConditionalWeakTable<NodeSnapshot, object> NonDefaultHomeSnapshots = new ConditionalWeakTable<NodeSnapshot, object>();

        public static string AppConfigDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "TokLens");
            }
        }

        public static string ConfigFilePath
        {
            get { return Path.Combine(AppConfigDir, "config.json"); }
        }

        public static string NodesCacheDir
        {
            get { return Path.Combine(AppConfigDir, "nodes"); }
        }

        private static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
        }

        private static JavaScriptSerializer NewJson()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;
            return js;
        }

        public static string SanitizeMachineId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "win-pc";
            string s = SafeIdRegex.Replace(raw.Trim().ToLowerInvariant(), "-").Trim('-');
            if (s.Length > 48) s = s.Substring(0, 48);
            return string.IsNullOrEmpty(s) ? "win-pc" : s;
        }

        public static SyncConfig LoadConfig()
        {
            SyncConfig cfg = new SyncConfig();
            string defaultHost = "Windows-PC";
            try { defaultHost = Environment.MachineName; } catch { }
            cfg.MachineId = SanitizeMachineId("win-" + defaultHost);
            cfg.MachineName = defaultHost;

            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    JavaScriptSerializer js = NewJson();
                    Dictionary<string, object> d = js.DeserializeObject(File.ReadAllText(ConfigFilePath, Encoding.UTF8)) as Dictionary<string, object>;
                    if (d != null)
                    {
                        if (d.ContainsKey("machine_id") && d["machine_id"] != null)
                            cfg.MachineId = SanitizeMachineId(Convert.ToString(d["machine_id"], CultureInfo.InvariantCulture));
                        if (d.ContainsKey("machine_name") && d["machine_name"] != null)
                        {
                            string mn = Convert.ToString(d["machine_name"], CultureInfo.InvariantCulture).Trim();
                            if (!string.IsNullOrEmpty(mn)) cfg.MachineName = mn;
                        }
                        if (d.ContainsKey("provider") && d["provider"] != null)
                            cfg.Provider = Convert.ToString(d["provider"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("auto_sync") && d["auto_sync"] != null)
                            cfg.AutoSync = Convert.ToBoolean(d["auto_sync"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("scan_local_wsl") && d["scan_local_wsl"] != null)
                            cfg.ScanLocalWsl = Convert.ToBoolean(d["scan_local_wsl"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("gist_id") && d["gist_id"] != null)
                            cfg.GistId = Convert.ToString(d["gist_id"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("gist_token") && d["gist_token"] != null)
                            cfg.GistToken = Convert.ToString(d["gist_token"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("webdav_url") && d["webdav_url"] != null)
                            cfg.WebDavUrl = Convert.ToString(d["webdav_url"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("webdav_user") && d["webdav_user"] != null)
                            cfg.WebDavUser = Convert.ToString(d["webdav_user"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("webdav_pass") && d["webdav_pass"] != null)
                            cfg.WebDavPass = Convert.ToString(d["webdav_pass"], CultureInfo.InvariantCulture);
                    }
                }
            }
            catch { }

            return cfg;
        }

        public static void SaveConfig(SyncConfig cfg)
        {
            Directory.CreateDirectory(AppConfigDir);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["machine_id"] = SanitizeMachineId(cfg.MachineId);
            d["machine_name"] = string.IsNullOrWhiteSpace(cfg.MachineName) ? Environment.MachineName : cfg.MachineName.Trim();
            d["provider"] = cfg.Provider ?? "gist";
            d["auto_sync"] = cfg.AutoSync;
            d["scan_local_wsl"] = cfg.ScanLocalWsl;
            d["gist_id"] = cfg.GistId ?? "";
            d["gist_token"] = cfg.GistToken ?? "";
            d["webdav_url"] = cfg.WebDavUrl ?? "";
            d["webdav_user"] = cfg.WebDavUser ?? "";
            d["webdav_pass"] = cfg.WebDavPass ?? "";

            JavaScriptSerializer js = NewJson();
            File.WriteAllText(ConfigFilePath, js.Serialize(d), new UTF8Encoding(false));
        }

        public static string GeneratePairingCode(SyncConfig cfg)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["provider"] = cfg.Provider ?? "gist";
            p["gist_id"] = cfg.GistId ?? "";
            p["gist_token"] = cfg.GistToken ?? "";
            p["webdav_url"] = cfg.WebDavUrl ?? "";
            p["webdav_user"] = cfg.WebDavUser ?? "";
            p["webdav_pass"] = cfg.WebDavPass ?? "";
            JavaScriptSerializer js = NewJson();
            string raw = js.Serialize(p);
            return "toklens1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryApplyPairingCode(SyncConfig targetCfg, string code, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(code))
            {
                error = "配对码为空";
                return false;
            }
            string s = code.Trim();
            if (s.StartsWith("toklens1:", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(9).Trim();
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(s));
                JavaScriptSerializer js = NewJson();
                Dictionary<string, object> d = js.DeserializeObject(json) as Dictionary<string, object>;
                if (d == null)
                {
                    error = "配对码内容无效";
                    return false;
                }
                if (d.ContainsKey("provider") && d["provider"] != null)
                    targetCfg.Provider = Convert.ToString(d["provider"], CultureInfo.InvariantCulture);
                if (d.ContainsKey("gist_id") && d["gist_id"] != null)
                    targetCfg.GistId = Convert.ToString(d["gist_id"], CultureInfo.InvariantCulture).Trim();
                if (d.ContainsKey("gist_token") && d["gist_token"] != null)
                    targetCfg.GistToken = Convert.ToString(d["gist_token"], CultureInfo.InvariantCulture).Trim();
                if (d.ContainsKey("webdav_url") && d["webdav_url"] != null)
                    targetCfg.WebDavUrl = Convert.ToString(d["webdav_url"], CultureInfo.InvariantCulture).Trim();
                if (d.ContainsKey("webdav_user") && d["webdav_user"] != null)
                    targetCfg.WebDavUser = Convert.ToString(d["webdav_user"], CultureInfo.InvariantCulture).Trim();
                if (d.ContainsKey("webdav_pass") && d["webdav_pass"] != null)
                    targetCfg.WebDavPass = Convert.ToString(d["webdav_pass"], CultureInfo.InvariantCulture);
                targetCfg.AutoSync = true;
                if (!targetCfg.IsConfigured)
                {
                    error = "配对码中不包含完整的云同步凭据";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "配对码格式解析失败：" + ex.Message;
                return false;
            }
        }

        public static bool TryReadGitHubCliToken(out string token)
        {
            token = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("gh", "auth token");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3500);
                    if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                    {
                        string t = stdout.Trim();
                        if (t.Length >= 16 && t.IndexOf(' ') < 0 && t.IndexOf('\n') < 0)
                        {
                            token = t;
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                return Path.GetFullPath(path.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            catch
            {
                return path.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            }
        }

        private static void MarkNonDefaultHomeSnapshot(NodeSnapshot snap)
        {
            if (snap == null) return;
            try
            {
                NonDefaultHomeSnapshots.Remove(snap);
                NonDefaultHomeSnapshots.Add(snap, new object());
            }
            catch { }
        }

        private static bool IsNonDefaultHomeSnapshot(NodeSnapshot snap)
        {
            if (snap == null) return false;
            object dummy;
            return NonDefaultHomeSnapshots.TryGetValue(snap, out dummy);
        }

        public static string GetLocalHighWaterFilePath(string machineId)
        {
            return Path.Combine(AppConfigDir, "local_highwater_" + SanitizeMachineId(machineId) + ".json");
        }

        private static int CompareRecordBuckets(RecordBucket a, RecordBucket b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int c = string.CompareOrdinal(a.Date ?? "", b.Date ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Tool ?? "", b.Tool ?? "");
            if (c != 0) return c;
            return string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
        }

        private static Dictionary<string, RecordBucket> BuildBucketMap(List<RecordBucket> records, string machineId, string machineName, string machineType)
        {
            Dictionary<string, RecordBucket> map = new Dictionary<string, RecordBucket>(StringComparer.Ordinal);
            if (records == null) return map;
            for (int i = 0; i < records.Count; i++)
            {
                RecordBucket src = records[i];
                if (src == null) continue;
                string dt = src.Date ?? "";
                string tool = src.Tool ?? "";
                string model = src.Model ?? "";
                string key = dt + "||" + tool + "||" + model;
                RecordBucket existing;
                if (map.TryGetValue(key, out existing))
                {
                    existing.Turns += src.Turns;
                    existing.FreshInput += src.FreshInput;
                    existing.CacheRead += src.CacheRead;
                    existing.CacheWrite += src.CacheWrite;
                    existing.Output += src.Output;
                    existing.CostUsd += src.CostUsd;
                    existing.BaselineUsd += src.BaselineUsd;
                    if (string.IsNullOrWhiteSpace(existing.RateLabel) && !string.IsNullOrWhiteSpace(src.RateLabel))
                        existing.RateLabel = src.RateLabel;
                }
                else
                {
                    RecordBucket copy = new RecordBucket();
                    copy.MachineId = machineId;
                    copy.MachineName = machineName;
                    copy.MachineType = machineType;
                    copy.Date = dt;
                    copy.Tool = tool;
                    copy.Model = model;
                    copy.Turns = src.Turns;
                    copy.FreshInput = src.FreshInput;
                    copy.CacheRead = src.CacheRead;
                    copy.CacheWrite = src.CacheWrite;
                    copy.Output = src.Output;
                    copy.CostUsd = src.CostUsd;
                    copy.BaselineUsd = src.BaselineUsd;
                    copy.RateLabel = src.RateLabel ?? "";
                    map[key] = copy;
                }
            }
            return map;
        }

        private static void CopySnapshotInto(NodeSnapshot src, NodeSnapshot dst)
        {
            if (src == null || dst == null || ReferenceEquals(src, dst)) return;
            dst.MachineId = src.MachineId;
            dst.MachineName = src.MachineName;
            dst.MachineType = src.MachineType;
            dst.OsInfo = src.OsInfo;
            dst.SyncedAt = src.SyncedAt;
            dst.Records.Clear();
            for (int i = 0; i < src.Records.Count; i++)
            {
                dst.Records.Add(src.Records[i]);
            }
        }

        public static NodeSnapshot MergeNodeSnapshotsHighWater(NodeSnapshot primary, NodeSnapshot historical)
        {
            if (primary == null && historical == null) return null;

            NodeSnapshot res = new NodeSnapshot();
            if (primary != null)
            {
                string fallbackMid = historical != null ? historical.MachineId : null;
                res.MachineId = SanitizeMachineId(!string.IsNullOrWhiteSpace(primary.MachineId) ? primary.MachineId : fallbackMid);
                res.MachineName = !string.IsNullOrWhiteSpace(primary.MachineName)
                    ? primary.MachineName
                    : (historical != null && !string.IsNullOrWhiteSpace(historical.MachineName) ? historical.MachineName : res.MachineId);
                res.MachineType = !string.IsNullOrWhiteSpace(primary.MachineType)
                    ? primary.MachineType
                    : (historical != null && !string.IsNullOrWhiteSpace(historical.MachineType) ? historical.MachineType : "windows");
                res.OsInfo = !string.IsNullOrWhiteSpace(primary.OsInfo)
                    ? primary.OsInfo
                    : (historical != null && !string.IsNullOrWhiteSpace(historical.OsInfo) ? historical.OsInfo : "");
                res.SyncedAt = !string.IsNullOrWhiteSpace(primary.SyncedAt)
                    ? primary.SyncedAt
                    : (historical != null && !string.IsNullOrWhiteSpace(historical.SyncedAt) ? historical.SyncedAt : "");
                res.IsLocal = primary.IsLocal;
            }
            else
            {
                res.MachineId = SanitizeMachineId(historical.MachineId);
                res.MachineName = !string.IsNullOrWhiteSpace(historical.MachineName) ? historical.MachineName : res.MachineId;
                res.MachineType = !string.IsNullOrWhiteSpace(historical.MachineType) ? historical.MachineType : "vps";
                res.OsInfo = historical.OsInfo ?? "";
                res.SyncedAt = historical.SyncedAt ?? "";
                res.IsLocal = historical.IsLocal;
            }

            Dictionary<string, RecordBucket> primaryMap = BuildBucketMap(primary != null ? primary.Records : null, res.MachineId, res.MachineName, res.MachineType);
            Dictionary<string, RecordBucket> histMap = BuildBucketMap(historical != null ? historical.Records : null, res.MachineId, res.MachineName, res.MachineType);

            foreach (KeyValuePair<string, RecordBucket> kv in primaryMap)
            {
                RecordBucket p = kv.Value;
                RecordBucket h;
                if (histMap.TryGetValue(kv.Key, out h))
                {
                    RecordBucket m = new RecordBucket();
                    m.MachineId = res.MachineId;
                    m.MachineName = res.MachineName;
                    m.MachineType = res.MachineType;
                    m.Date = p.Date;
                    m.Tool = p.Tool;
                    m.Model = p.Model;
                    m.Turns = Math.Max(p.Turns, h.Turns);
                    m.FreshInput = Math.Max(p.FreshInput, h.FreshInput);
                    m.CacheRead = Math.Max(p.CacheRead, h.CacheRead);
                    m.CacheWrite = Math.Max(p.CacheWrite, h.CacheWrite);
                    m.Output = Math.Max(p.Output, h.Output);
                    m.CostUsd = Math.Max(p.CostUsd, h.CostUsd);
                    m.BaselineUsd = Math.Max(p.BaselineUsd, h.BaselineUsd);
                    m.RateLabel = !string.IsNullOrWhiteSpace(p.RateLabel) ? p.RateLabel : (h.RateLabel ?? "");
                    res.Records.Add(m);
                }
                else
                {
                    res.Records.Add(p);
                }
            }

            foreach (KeyValuePair<string, RecordBucket> kv in histMap)
            {
                if (!primaryMap.ContainsKey(kv.Key))
                {
                    res.Records.Add(kv.Value);
                }
            }

            res.Records.Sort(CompareRecordBuckets);
            return res;
        }

        public static NodeSnapshot MergeWithLocalHighWater(NodeSnapshot localSnap)
        {
            if (localSnap == null) return null;
            localSnap.MachineId = SanitizeMachineId(localSnap.MachineId);
            string hwPath = GetLocalHighWaterFilePath(localSnap.MachineId);
            NodeSnapshot merged = null;
            try
            {
                if (File.Exists(hwPath))
                {
                    NodeSnapshot cached = DeserializeSnapshot(File.ReadAllText(hwPath, Encoding.UTF8), true);
                    merged = MergeNodeSnapshotsHighWater(localSnap, cached);
                }
                else
                {
                    merged = MergeNodeSnapshotsHighWater(localSnap, null);
                }
            }
            catch
            {
                merged = MergeNodeSnapshotsHighWater(localSnap, null);
            }

            try
            {
                Directory.CreateDirectory(AppConfigDir);
                File.WriteAllText(hwPath, SerializeSnapshot(merged), new UTF8Encoding(false));
            }
            catch { }

            CopySnapshotInto(merged, localSnap);
            return merged;
        }

        public static NodeSnapshot BuildLocalSnapshot(ScanResult localRes, SyncConfig cfg)
        {
            if (cfg == null) cfg = LoadConfig();
            NodeSnapshot snap = new NodeSnapshot();
            snap.MachineId = SanitizeMachineId(cfg.MachineId);
            snap.MachineName = string.IsNullOrWhiteSpace(cfg.MachineName) ? Environment.MachineName : cfg.MachineName.Trim();
            snap.MachineType = "windows";
            snap.OsInfo = Environment.OSVersion.VersionString;
            snap.SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            snap.IsLocal = true;

            if (localRes != null && localRes.Records != null)
            {
                for (int i = 0; i < localRes.Records.Count; i++)
                {
                    RecordBucket src = localRes.Records[i];
                    if (src == null) continue;
                    RecordBucket copy = new RecordBucket();
                    copy.MachineId = snap.MachineId;
                    copy.MachineName = snap.MachineName;
                    copy.MachineType = snap.MachineType;
                    copy.Date = src.Date;
                    copy.Tool = src.Tool;
                    copy.Model = src.Model;
                    copy.Turns = src.Turns;
                    copy.FreshInput = src.FreshInput;
                    copy.CacheRead = src.CacheRead;
                    copy.CacheWrite = src.CacheWrite;
                    copy.Output = src.Output;
                    copy.CostUsd = src.CostUsd;
                    copy.BaselineUsd = src.BaselineUsd;
                    copy.RateLabel = src.RateLabel;
                    snap.Records.Add(copy);
                }
            }

            string defaultHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (localRes != null &&
                !string.IsNullOrWhiteSpace(localRes.UserHome) &&
                !string.IsNullOrWhiteSpace(defaultHome) &&
                string.Equals(NormalizePath(localRes.UserHome), NormalizePath(defaultHome)))
            {
                snap = MergeWithLocalHighWater(snap);
            }
            else
            {
                snap.Records.Sort(CompareRecordBuckets);
                MarkNonDefaultHomeSnapshot(snap);
            }
            return snap;
        }

        public static string SerializeSnapshot(NodeSnapshot snap)
        {
            List<object[]> rows = new List<object[]>(snap.Records.Count);
            for (int i = 0; i < snap.Records.Count; i++)
            {
                RecordBucket r = snap.Records[i];
                rows.Add(new object[]
                {
                    r.Date,
                    r.Tool,
                    r.Model,
                    r.Turns,
                    r.FreshInput,
                    r.CacheRead,
                    r.CacheWrite,
                    r.Output,
                    Math.Round(r.CostUsd, 6),
                    Math.Round(r.BaselineUsd, 6),
                    r.RateLabel ?? ""
                });
            }

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = SchemaVersion;
            root["machine_id"] = snap.MachineId;
            root["machine_name"] = snap.MachineName;
            root["machine_type"] = snap.MachineType ?? "windows";
            root["os"] = snap.OsInfo ?? "";
            root["synced_at"] = snap.SyncedAt ?? "";
            root["records"] = rows;

            JavaScriptSerializer js = NewJson();
            return js.Serialize(root);
        }

        public static NodeSnapshot DeserializeSnapshot(string json, bool isLocal)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                JavaScriptSerializer js = NewJson();
                Dictionary<string, object> d = js.DeserializeObject(json) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("machine_id")) return null;

                NodeSnapshot snap = new NodeSnapshot();
                snap.MachineId = SanitizeMachineId(Convert.ToString(d["machine_id"], CultureInfo.InvariantCulture));
                snap.MachineName = d.ContainsKey("machine_name") ? Convert.ToString(d["machine_name"], CultureInfo.InvariantCulture) : snap.MachineId;
                snap.MachineType = d.ContainsKey("machine_type") ? Convert.ToString(d["machine_type"], CultureInfo.InvariantCulture) : "vps";
                snap.OsInfo = d.ContainsKey("os") ? Convert.ToString(d["os"], CultureInfo.InvariantCulture) : "";
                snap.SyncedAt = d.ContainsKey("synced_at") ? Convert.ToString(d["synced_at"], CultureInfo.InvariantCulture) : "";
                snap.IsLocal = isLocal;

                if (d.ContainsKey("records"))
                {
                    IList arr = d["records"] as IList;
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            IList row = arr[i] as IList;
                            if (row == null || row.Count < 10) continue;
                            RecordBucket r = new RecordBucket();
                            r.MachineId = snap.MachineId;
                            r.MachineName = snap.MachineName;
                            r.MachineType = snap.MachineType;
                            r.Date = Convert.ToString(row[0], CultureInfo.InvariantCulture);
                            r.Tool = Convert.ToString(row[1], CultureInfo.InvariantCulture);
                            r.Model = Convert.ToString(row[2], CultureInfo.InvariantCulture);
                            r.Turns = Convert.ToInt32(row[3], CultureInfo.InvariantCulture);
                            r.FreshInput = Convert.ToInt64(row[4], CultureInfo.InvariantCulture);
                            r.CacheRead = Convert.ToInt64(row[5], CultureInfo.InvariantCulture);
                            r.CacheWrite = Convert.ToInt64(row[6], CultureInfo.InvariantCulture);
                            r.Output = Convert.ToInt64(row[7], CultureInfo.InvariantCulture);
                            r.CostUsd = Convert.ToDouble(row[8], CultureInfo.InvariantCulture);
                            r.BaselineUsd = Convert.ToDouble(row[9], CultureInfo.InvariantCulture);
                            r.RateLabel = row.Count > 10 ? Convert.ToString(row[10], CultureInfo.InvariantCulture) : "";
                            snap.Records.Add(r);
                        }
                    }
                }
                return snap;
            }
            catch
            {
                return null;
            }
        }

        public static List<NodeSnapshot> LoadCachedRemoteNodes(string localMachineId)
        {
            List<NodeSnapshot> list = new List<NodeSnapshot>();
            try
            {
                if (!Directory.Exists(NodesCacheDir)) return list;
                string[] files = Directory.GetFiles(NodesCacheDir, "toklens_node_*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        NodeSnapshot snap = DeserializeSnapshot(File.ReadAllText(files[i], Encoding.UTF8), false);
                        if (snap != null && !string.Equals(snap.MachineId, localMachineId, StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add(snap);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public static NodeSnapshot SaveRemoteNodeCache(NodeSnapshot snap)
        {
            if (snap == null || string.IsNullOrEmpty(snap.MachineId)) return snap;
            snap.MachineId = SanitizeMachineId(snap.MachineId);
            try
            {
                Directory.CreateDirectory(NodesCacheDir);
                string fn = Path.Combine(NodesCacheDir, "toklens_node_" + snap.MachineId + ".json");
                if (File.Exists(fn))
                {
                    NodeSnapshot oldCached = DeserializeSnapshot(File.ReadAllText(fn, Encoding.UTF8), false);
                    if (oldCached != null)
                    {
                        NodeSnapshot merged = MergeNodeSnapshotsHighWater(snap, oldCached);
                        CopySnapshotInto(merged, snap);
                    }
                }
                File.WriteAllText(fn, SerializeSnapshot(snap), new UTF8Encoding(false));
            }
            catch { }
            return snap;
        }

        public static void RemoveCachedNode(string machineId)
        {
            try
            {
                string fn = Path.Combine(NodesCacheDir, "toklens_node_" + SanitizeMachineId(machineId) + ".json");
                if (File.Exists(fn)) File.Delete(fn);
            }
            catch { }
        }

        public static string GetEmbeddedVpsAgentScript()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream("TokLens.toklens_agent.py"))
                {
                    if (st != null)
                    {
                        using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
            }
            catch { }

            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string localPy = Path.Combine(exeDir, "vps", "toklens_agent.py");
                if (File.Exists(localPy))
                    return File.ReadAllText(localPy, Encoding.UTF8);
            }
            catch { }

            return "";
        }

        public static string FindExistingTokLensGist(string token)
        {
            EnsureTls();
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                JavaScriptSerializer js = NewJson();
                string respBody = SendHttpJson("GET", "https://api.github.com/gists?per_page=100", token.Trim(), null, null);
                IList list = js.DeserializeObject(respBody) as IList;
                if (list == null) return null;

                string descFallbackId = null;
                for (int i = 0; i < list.Count; i++)
                {
                    Dictionary<string, object> g = list[i] as Dictionary<string, object>;
                    if (g == null || !g.ContainsKey("id") || g["id"] == null) continue;
                    string gid = Convert.ToString(g["id"], CultureInfo.InvariantCulture).Trim();
                    if (string.IsNullOrEmpty(gid)) continue;

                    if (g.ContainsKey("files") && g["files"] is Dictionary<string, object>)
                    {
                        Dictionary<string, object> files = (Dictionary<string, object>)g["files"];
                        foreach (string fn in files.Keys)
                        {
                            if (fn.StartsWith("toklens_node_", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fn, "toklens_agent.py", StringComparison.OrdinalIgnoreCase))
                            {
                                return gid;
                            }
                        }
                    }

                    if (descFallbackId == null && g.ContainsKey("description") && g["description"] != null)
                    {
                        string desc = Convert.ToString(g["description"], CultureInfo.InvariantCulture);
                        if (desc.IndexOf("TokLens", StringComparison.OrdinalIgnoreCase) >= 0)
                            descFallbackId = gid;
                    }
                }
                return descFallbackId;
            }
            catch
            {
                return null;
            }
        }

        public static string FindOrCreatePrivateGist(string token, NodeSnapshot localSnap, out bool reusedExisting)
        {
            string existingId = FindExistingTokLensGist(token);
            if (!string.IsNullOrEmpty(existingId))
            {
                reusedExisting = true;
                return existingId;
            }
            reusedExisting = false;
            return CreatePrivateGist(token, localSnap);
        }

        public static string CreatePrivateGist(string token, NodeSnapshot localSnap)
        {
            EnsureTls();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("请先填写 GitHub Personal Access Token（需勾选 gist 权限）。");

            Dictionary<string, object> files = new Dictionary<string, object>();
            string nodeFile = "toklens_node_" + localSnap.MachineId + ".json";
            Dictionary<string, object> nfObj = new Dictionary<string, object>();
            nfObj["content"] = SerializeSnapshot(localSnap);
            files[nodeFile] = nfObj;

            string agentPy = GetEmbeddedVpsAgentScript();
            if (!string.IsNullOrEmpty(agentPy))
            {
                Dictionary<string, object> pyObj = new Dictionary<string, object>();
                pyObj["content"] = agentPy;
                files["toklens_agent.py"] = pyObj;
            }

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["description"] = "知微 TokLens — 多设备与 VPS 账单私有同步库 (请勿公开)";
            payload["public"] = false;
            payload["files"] = files;

            JavaScriptSerializer js = NewJson();
            string respBody = SendHttpJson("POST", "https://api.github.com/gists", token.Trim(), null, js.Serialize(payload));
            Dictionary<string, object> resp = js.DeserializeObject(respBody) as Dictionary<string, object>;
            if (resp == null || !resp.ContainsKey("id"))
                throw new InvalidOperationException("GitHub 返回格式异常，未能获取 Gist ID。");
            return Convert.ToString(resp["id"], CultureInfo.InvariantCulture);
        }

        public static SyncOutcome SyncNow(SyncConfig cfg, NodeSnapshot localSnap, bool uploadAgentScript)
        {
            SyncOutcome outcome = new SyncOutcome();
            try
            {
                if (!cfg.IsConfigured)
                {
                    outcome.Success = false;
                    outcome.Message = "尚未配置多端同步凭据";
                    outcome.RemoteNodes = LoadCachedRemoteNodes(localSnap.MachineId);
                    outcome.RemoteNodeCount = outcome.RemoteNodes.Count;
                    return outcome;
                }

                if (string.Equals(cfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase))
                {
                    return SyncWebDav(cfg, localSnap, uploadAgentScript);
                }
                return SyncGist(cfg, localSnap, uploadAgentScript);
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Message = "同步失败：" + ex.Message;
                outcome.RemoteNodes = LoadCachedRemoteNodes(localSnap.MachineId);
                outcome.RemoteNodeCount = outcome.RemoteNodes.Count;
                return outcome;
            }
        }

        private static string ReadGistFileContent(Dictionary<string, object> fmeta, string bearerToken)
        {
            if (fmeta == null) return null;
            string content = fmeta.ContainsKey("content") && fmeta["content"] != null
                ? Convert.ToString(fmeta["content"], CultureInfo.InvariantCulture)
                : null;
            bool truncated = fmeta.ContainsKey("truncated") && fmeta["truncated"] != null && Convert.ToBoolean(fmeta["truncated"], CultureInfo.InvariantCulture);
            if ((truncated || string.IsNullOrEmpty(content)) && fmeta.ContainsKey("raw_url") && fmeta["raw_url"] != null)
            {
                string rawUrl = Convert.ToString(fmeta["raw_url"], CultureInfo.InvariantCulture);
                bool isRawCdn = rawUrl.IndexOf("githubusercontent.com", StringComparison.OrdinalIgnoreCase) >= 0;
                try
                {
                    content = SendHttpJson("GET", rawUrl, isRawCdn ? null : bearerToken, null, null);
                }
                catch
                {
                    content = SendHttpJson("GET", rawUrl, bearerToken, null, null);
                }
            }
            return content;
        }

        private static SyncOutcome SyncGist(SyncConfig cfg, NodeSnapshot localSnap, bool uploadAgentScript)
        {
            EnsureTls();
            JavaScriptSerializer js = NewJson();

            localSnap.MachineId = SanitizeMachineId(localSnap.MachineId);
            string localFileName = "toklens_node_" + localSnap.MachineId + ".json";
            string url = "https://api.github.com/gists/" + cfg.GistId.Trim();
            string token = cfg.GistToken.Trim();

            // 1. 拉取远端现有 Gist，若存在同名本机快照则先做高水位合并，防止重装/清空日志后冲刷云端历史
            string preRespText = SendHttpJson("GET", url, token, null, null);
            Dictionary<string, object> preGistObj = js.DeserializeObject(preRespText) as Dictionary<string, object>;
            if (preGistObj != null && preGistObj.ContainsKey("files"))
            {
                Dictionary<string, object> preFiles = preGistObj["files"] as Dictionary<string, object>;
                if (preFiles != null)
                {
                    foreach (KeyValuePair<string, object> kv in preFiles)
                    {
                        if (string.Equals(kv.Key, localFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            Dictionary<string, object> existingMeta = kv.Value as Dictionary<string, object>;
                            string existingContent = ReadGistFileContent(existingMeta, token);
                            NodeSnapshot remoteExistingSelf = DeserializeSnapshot(existingContent, true);
                            if (remoteExistingSelf != null)
                            {
                                NodeSnapshot mergedSelf = MergeNodeSnapshotsHighWater(localSnap, remoteExistingSelf);
                                CopySnapshotInto(mergedSelf, localSnap);
                                if (!IsNonDefaultHomeSnapshot(localSnap))
                                {
                                    NodeSnapshot hwMerged = MergeWithLocalHighWater(localSnap);
                                    CopySnapshotInto(hwMerged, localSnap);
                                }
                            }
                            break;
                        }
                    }
                }
            }

            // 2. 上传合并后的完整本机高水位快照
            Dictionary<string, object> files = new Dictionary<string, object>();
            Dictionary<string, object> nf = new Dictionary<string, object>();
            nf["content"] = SerializeSnapshot(localSnap);
            files[localFileName] = nf;

            if (uploadAgentScript)
            {
                string agentPy = GetEmbeddedVpsAgentScript();
                if (!string.IsNullOrEmpty(agentPy))
                {
                    Dictionary<string, object> pyf = new Dictionary<string, object>();
                    pyf["content"] = agentPy;
                    files["toklens_agent.py"] = pyf;
                }
            }

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["files"] = files;

            string respText = SendHttpJson("PATCH", url, token, null, js.Serialize(body));
            Dictionary<string, object> gistObj = js.DeserializeObject(respText) as Dictionary<string, object>;
            if (gistObj == null || !gistObj.ContainsKey("files"))
                throw new InvalidOperationException("GitHub Gist 响应缺少 files 字段");

            Dictionary<string, object> remoteFiles = gistObj["files"] as Dictionary<string, object>;
            List<NodeSnapshot> remotes = new List<NodeSnapshot>();

            if (remoteFiles != null)
            {
                foreach (KeyValuePair<string, object> kv in remoteFiles)
                {
                    string fn = kv.Key;
                    if (!fn.StartsWith("toklens_node_", StringComparison.OrdinalIgnoreCase) ||
                        !fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Dictionary<string, object> fmeta = kv.Value as Dictionary<string, object>;
                    if (fmeta == null) continue;

                    string content = ReadGistFileContent(fmeta, token);
                    NodeSnapshot snap = DeserializeSnapshot(content, false);
                    if (snap != null && !string.Equals(snap.MachineId, localSnap.MachineId, StringComparison.OrdinalIgnoreCase))
                    {
                        snap = SaveRemoteNodeCache(snap);
                        remotes.Add(snap);
                    }
                }
            }

            SyncOutcome ok = new SyncOutcome();
            ok.Success = true;
            ok.RemoteNodes = remotes;
            ok.RemoteNodeCount = remotes.Count;
            ok.Message = string.Format(CultureInfo.InvariantCulture, "已同步（本机 + {0} 台远端设备）", remotes.Count);
            return ok;
        }

        private static SyncOutcome SyncWebDav(SyncConfig cfg, NodeSnapshot localSnap, bool uploadAgentScript)
        {
            EnsureTls();
            localSnap.MachineId = SanitizeMachineId(localSnap.MachineId);
            string baseUrl = cfg.WebDavUrl.Trim().TrimEnd('/') + "/";
            string basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes((cfg.WebDavUser ?? "") + ":" + (cfg.WebDavPass ?? "")));

            // Try MKCOL (ignore if already exists)
            try { SendHttpRaw("MKCOL", baseUrl, basicAuth, null, null, null); } catch { }

            // 1. 先 PROPFIND 探测远端已有节点列表
            Dictionary<string, string> extraHeaders = new Dictionary<string, string>();
            extraHeaders["Depth"] = "1";
            string propXml = SendHttpRaw("PROPFIND", baseUrl, basicAuth, "application/xml", null, extraHeaders);

            HashSet<string> nodeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MatchCollection matches = WebDavNodeRegex.Matches(propXml ?? "");
            for (int i = 0; i < matches.Count; i++)
            {
                nodeFiles.Add(matches[i].Groups[1].Value);
            }

            string localFn = "toklens_node_" + localSnap.MachineId + ".json";
            string existingRemoteSelfFn = null;
            foreach (string fn in nodeFiles)
            {
                if (string.Equals(fn, localFn, StringComparison.OrdinalIgnoreCase))
                {
                    existingRemoteSelfFn = fn;
                    break;
                }
            }

            // 2. 若远端已存在同名本机快照，先 GET 拉回与本机做高水位合并，防止冲刷云端历史
            if (existingRemoteSelfFn != null)
            {
                try
                {
                    string existingJson = SendHttpRaw("GET", baseUrl + Uri.EscapeDataString(existingRemoteSelfFn), basicAuth, null, null, null);
                    NodeSnapshot remoteExistingSelf = DeserializeSnapshot(existingJson, true);
                    if (remoteExistingSelf != null)
                    {
                        NodeSnapshot mergedSelf = MergeNodeSnapshotsHighWater(localSnap, remoteExistingSelf);
                        CopySnapshotInto(mergedSelf, localSnap);
                        if (!IsNonDefaultHomeSnapshot(localSnap))
                        {
                            NodeSnapshot hwMerged = MergeWithLocalHighWater(localSnap);
                            CopySnapshotInto(hwMerged, localSnap);
                        }
                    }
                }
                catch { }
            }

            // 3. PUT 上传合并后的完整历史快照
            SendHttpRaw("PUT", baseUrl + Uri.EscapeDataString(localFn), basicAuth, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SerializeSnapshot(localSnap)), null);

            if (uploadAgentScript)
            {
                string agentPy = GetEmbeddedVpsAgentScript();
                if (!string.IsNullOrEmpty(agentPy))
                {
                    try { SendHttpRaw("PUT", baseUrl + "toklens_agent.py", basicAuth, "text/x-python; charset=utf-8", Encoding.UTF8.GetBytes(agentPy), null); } catch { }
                }
            }

            // 4. 拉取其余远端设备快照并与本地缓存做高水位合并
            List<NodeSnapshot> remotes = new List<NodeSnapshot>();
            foreach (string fn in nodeFiles)
            {
                if (string.Equals(fn, localFn, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string json = SendHttpRaw("GET", baseUrl + Uri.EscapeDataString(fn), basicAuth, null, null, null);
                    NodeSnapshot snap = DeserializeSnapshot(json, false);
                    if (snap != null && !string.Equals(snap.MachineId, localSnap.MachineId, StringComparison.OrdinalIgnoreCase))
                    {
                        snap = SaveRemoteNodeCache(snap);
                        remotes.Add(snap);
                    }
                }
                catch { }
            }

            SyncOutcome ok = new SyncOutcome();
            ok.Success = true;
            ok.RemoteNodes = remotes;
            ok.RemoteNodeCount = remotes.Count;
            ok.Message = string.Format(CultureInfo.InvariantCulture, "WebDAV 已同步（本机 + {0} 台远端设备）", remotes.Count);
            return ok;
        }

        public static string BuildLinuxVpsOneLiner(SyncConfig cfg, string vpsDisplayName, int cronMinutes)
        {
            string pairCode = GeneratePairingCode(cfg);
            string safeName = (vpsDisplayName ?? "").Replace("'", "");
            string nameArg = string.IsNullOrWhiteSpace(safeName) ? "" : (" --name '" + safeName + "'");
            int mins = Math.Max(5, Math.Min(59, cronMinutes));

            if (string.Equals(cfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase))
            {
                string baseUrl = (cfg.WebDavUrl ?? "").Trim().TrimEnd('/') + "/toklens_agent.py";
                string u = (cfg.WebDavUser ?? "").Replace("'", "");
                string p = (cfg.WebDavPass ?? "").Replace("'", "");
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "mkdir -p ~/.toklens && curl -fsSL -u '{0}:{1}' '{2}' -o ~/.toklens/toklens_agent.py && python3 ~/.toklens/toklens_agent.py --pair '{3}'{4} --sync --install-cron {5}",
                    u, p, baseUrl, pairCode, nameArg, mins);
            }
            else
            {
                string gid = (cfg.GistId ?? "").Trim();
                string tok = (cfg.GistToken ?? "").Trim();
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "mkdir -p ~/.toklens && python3 -c \"import urllib.request,json,pathlib;req=urllib.request.Request('https://api.github.com/gists/{0}',headers={{'Authorization':'Bearer {1}','User-Agent':'TokLens'}});d=json.loads(urllib.request.urlopen(req).read().decode());f=d['files']['toklens_agent.py'];c=f.get('content') if not f.get('truncated') else urllib.request.urlopen(urllib.request.Request(f['raw_url'],headers={{'User-Agent':'TokLens'}})).read().decode();pathlib.Path('~/.toklens/toklens_agent.py').expanduser().write_text(c,encoding='utf-8')\" && python3 ~/.toklens/toklens_agent.py --pair '{2}'{3} --sync --install-cron {4}",
                    gid, tok, pairCode, nameArg, mins);
            }
        }

        private static string SendHttpJson(string method, string url, string bearerToken, string basicAuth, string jsonBody)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "TokLens-Desktop/2.0";
            req.Accept = "application/vnd.github+json, application/json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            if (!string.IsNullOrEmpty(bearerToken))
                req.Headers["Authorization"] = "Bearer " + bearerToken;
            else if (!string.IsNullOrEmpty(basicAuth))
                req.Headers["Authorization"] = "Basic " + basicAuth;

            if (jsonBody != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = bytes.Length;
                using (Stream rs = req.GetRequestStream())
                {
                    rs.Write(bytes, 0, bytes.Length);
                }
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream st = resp.GetResponseStream())
                {
                    if (st == null) return "";
                    using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    try
                    {
                        using (Stream es = wex.Response.GetResponseStream())
                        using (StreamReader sr = new StreamReader(es, Encoding.UTF8))
                        {
                            string errBody = sr.ReadToEnd();
                            HttpWebResponse hr = wex.Response as HttpWebResponse;
                            int code = hr != null ? (int)hr.StatusCode : 0;
                            throw new InvalidOperationException("HTTP " + code.ToString(CultureInfo.InvariantCulture) + ": " + (errBody.Length > 180 ? errBody.Substring(0, 180) : errBody));
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        throw;
                    }
                    catch { }
                }
                throw;
            }
        }

        private static string SendHttpRaw(string method, string url, string basicAuth, string contentType, byte[] body, Dictionary<string, string> extraHeaders)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "TokLens-Desktop/2.0";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            if (!string.IsNullOrEmpty(basicAuth))
                req.Headers["Authorization"] = "Basic " + basicAuth;
            if (extraHeaders != null)
            {
                foreach (KeyValuePair<string, string> kv in extraHeaders)
                    req.Headers[kv.Key] = kv.Value;
            }
            if (!string.IsNullOrEmpty(contentType))
                req.ContentType = contentType;

            if (body != null)
            {
                req.ContentLength = body.Length;
                using (Stream rs = req.GetRequestStream())
                {
                    rs.Write(body, 0, body.Length);
                }
            }
            else if (method != "GET")
            {
                req.ContentLength = 0;
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            {
                if (st == null) return "";
                using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
        }
    }
}
