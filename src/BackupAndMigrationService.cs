using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web.Script.Serialization;

namespace TokLens
{
    /// <summary>
    /// 本地时光机备份包及归档快照元数据信息。
    /// </summary>
    public sealed class BackupArchiveInfo
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string CreatedAtText { get; set; }
        public long SizeBytes { get; set; }
        public string SizeText { get; set; }
        public int NodeCount { get; set; }
        public int RecordCount { get; set; }

        public BackupArchiveInfo()
        {
            FilePath = "";
            FileName = "";
            CreatedAtText = "";
            SizeBytes = 0L;
            SizeText = "0 B";
            NodeCount = 0;
            RecordCount = 0;
        }

        public string DisplaySummary
        {
            get
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}  ({1} 台设备 / {2} 条记录, {3})",
                    CreatedAtText,
                    NodeCount,
                    RecordCount,
                    SizeText);
            }
        }

        public override string ToString()
        {
            return DisplaySummary;
        }
    }

    /// <summary>
    /// 本地账本时光机自动备份、离线打包导出与跨设备高水位无损合并服务。
    /// 解决重装系统、换电脑离线迁移、误操作回滚及内网隔离环境 U 盘同步痛点。
    /// </summary>
    public static class BackupAndMigrationService
    {
        public const string BundleSchemaVersion = "toklens.backup.v1";
        public const int MaxAutoBackupsToKeep = 10;

        private const string AutoBackupPrefix = "toklens_backup_";
        private const long MaxArchiveReadBytes = 256L * 1024L * 1024L;
        private const int FastHeaderProbeBytes = 8192;

        private static readonly object BackupGate = new object();

        private static string _cachedBackupsDir = "";
        private static string _cachedAutoBackupDay = "";
        private static BackupArchiveInfo _cachedAutoBackupInfo = null;
        private static bool _cachedAutoBackupHadData = false;

        /// <summary>
        /// 本地时光机自动备份目录（默认位于 %APPDATA%\TokLens\backups，自动保留最近 10 份自动快照）。
        /// </summary>
        public static string BackupsDir
        {
            get { return Path.Combine(SyncService.AppConfigDir, "backups"); }
        }

        private static bool IsUsingDefaultAppConfigDir()
        {
            try
            {
                string customEnv = Environment.GetEnvironmentVariable("TOKLENS_CONFIG_DIR");
                if (!string.IsNullOrWhiteSpace(customEnv))
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string defDir = Path.GetFullPath(Path.Combine(appData, "TokLens"))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string curDir = Path.GetFullPath(SyncService.AppConfigDir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.Equals(defDir, curDir, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return true;
        }

        private static JavaScriptSerializer NewJson()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;
            return js;
        }

        private static double SanitizeDouble(double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;
            if (decimals >= 0)
                return Math.Round(value, decimals);
            return value;
        }

        /// <summary>
        /// 将字节数格式化为易读体积文本（B / KB / MB / GB）。
        /// </summary>
        public static string FormatFileSize(long sizeBytes)
        {
            if (sizeBytes <= 0L)
                return "0 B";
            if (sizeBytes < 1024L)
                return sizeBytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (sizeBytes < 1024L * 1024L)
                return (sizeBytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
            if (sizeBytes < 1024L * 1024L * 1024L)
                return (sizeBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " MB";
            return (sizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        }

        private static void AtomicWriteBytes(string filePath, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("目标备份文件路径不能为空。");

            string fullPath = Path.GetFullPath(filePath.Trim());
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            byte[] payload = data ?? new byte[0];
            try
            {
                using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    if (payload.Length > 0)
                        fs.Write(payload, 0, payload.Length);
                    fs.Flush();
                }

                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Replace(tmpPath, fullPath, null, true);
                        return;
                    }
                    catch
                    {
                        try { File.Delete(fullPath); } catch { }
                    }
                }
                File.Move(tmpPath, fullPath);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static byte[] CompressUtf8ToGZip(string text)
        {
            byte[] utf8 = new UTF8Encoding(false).GetBytes(text ?? "");
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    if (utf8.Length > 0)
                        gz.Write(utf8, 0, utf8.Length);
                }
                return ms.ToArray();
            }
        }

        private static string StripUtf8BomAndTrim(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            return raw.Trim('\uFEFF', '\u200B', ' ', '\t', '\r', '\n');
        }

        private static string ReadArchiveText(string filePath, int maxCharsOrNegativeForFull)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
            {
                if (fs.Length <= 0L)
                    return "";

                int b0 = fs.ReadByte();
                int b1 = fs.ReadByte();
                fs.Position = 0L;

                bool isGZip = (b0 == 0x1F && b1 == 0x8B);
                if (isGZip)
                {
                    using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                    {
                        if (maxCharsOrNegativeForFull > 0)
                        {
                            byte[] probeBuf = new byte[maxCharsOrNegativeForFull];
                            int totalRead = 0;
                            while (totalRead < probeBuf.Length)
                            {
                                int n = gz.Read(probeBuf, totalRead, probeBuf.Length - totalRead);
                                if (n <= 0) break;
                                totalRead += n;
                            }
                            if (totalRead <= 0) return "";
                            return StripUtf8BomAndTrim(Encoding.UTF8.GetString(probeBuf, 0, totalRead));
                        }

                        using (MemoryStream decompressed = new MemoryStream())
                        {
                            byte[] buf = new byte[65536];
                            long total = 0L;
                            int read;
                            while ((read = gz.Read(buf, 0, buf.Length)) > 0)
                            {
                                total += read;
                                if (total > MaxArchiveReadBytes)
                                    throw new InvalidDataException("备份归档解压后体积超过 256MB 安全上限。");
                                decompressed.Write(buf, 0, read);
                            }
                            decompressed.Position = 0L;
                            using (StreamReader sr = new StreamReader(decompressed, Encoding.UTF8, true))
                            {
                                return StripUtf8BomAndTrim(sr.ReadToEnd());
                            }
                        }
                    }
                }
                else
                {
                    if (fs.Length > MaxArchiveReadBytes)
                        throw new InvalidDataException("备份文件体积超过 256MB 安全上限。");

                    if (maxCharsOrNegativeForFull > 0)
                    {
                        int byteCap = Math.Min((int)Math.Min(fs.Length, (long)(maxCharsOrNegativeForFull * 2)), 32768);
                        byte[] probeBuf = new byte[byteCap];
                        int totalRead = 0;
                        while (totalRead < byteCap)
                        {
                            int n = fs.Read(probeBuf, totalRead, byteCap - totalRead);
                            if (n <= 0) break;
                            totalRead += n;
                        }
                        if (totalRead <= 0) return "";
                        return StripUtf8BomAndTrim(Encoding.UTF8.GetString(probeBuf, 0, totalRead));
                    }

                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                    {
                        return StripUtf8BomAndTrim(sr.ReadToEnd());
                    }
                }
            }
        }

        private static string ResolveDefaultBackupFilePath(DateTime now)
        {
            string dir = BackupsDir;
            Directory.CreateDirectory(dir);
            string stamp = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string basePath = Path.Combine(dir, AutoBackupPrefix + stamp + ".toklens.gz");
            if (!File.Exists(basePath))
                return basePath;

            string msStamp = now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string altPath = Path.Combine(dir, AutoBackupPrefix + msStamp + ".toklens.gz");
            if (!File.Exists(altPath))
                return altPath;

            return Path.Combine(dir, AutoBackupPrefix + stamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".toklens.gz");
        }

        private static string NormalizeTargetFilePath(string targetFilePathOrNull, DateTime now, out bool useGZip, out bool isAutoManagedInBackupsDir)
        {
            if (string.IsNullOrWhiteSpace(targetFilePathOrNull))
            {
                useGZip = true;
                isAutoManagedInBackupsDir = true;
                return ResolveDefaultBackupFilePath(now);
            }

            string trimmed = targetFilePathOrNull.Trim();
            if (trimmed.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                useGZip = true;
            }
            else if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                useGZip = false;
            }
            else
            {
                trimmed = trimmed + ".toklens.gz";
                useGZip = true;
            }

            string fullTarget = Path.GetFullPath(trimmed);
            string targetDir = Path.GetDirectoryName(fullTarget) ?? "";
            string targetName = Path.GetFileName(fullTarget) ?? "";
            string normBackupsDir = "";
            try
            {
                normBackupsDir = Path.GetFullPath(BackupsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { }

            string normTargetDir = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            isAutoManagedInBackupsDir =
                !string.IsNullOrEmpty(normBackupsDir) &&
                string.Equals(normTargetDir, normBackupsDir, StringComparison.OrdinalIgnoreCase) &&
                targetName.StartsWith(AutoBackupPrefix, StringComparison.OrdinalIgnoreCase);

            return fullTarget;
        }

        private static NodeSnapshot BuildBestLocalNodeSnapshot(SyncConfig cfg, string defaultHome)
        {
            string localMid = SyncService.SanitizeMachineId(cfg != null ? cfg.MachineId : "win-pc");
            string localName = (cfg != null && !string.IsNullOrWhiteSpace(cfg.MachineName))
                ? cfg.MachineName.Trim()
                : Environment.MachineName;
            string osText = "";
            try { osText = Environment.OSVersion.VersionString; } catch { }
            string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            bool useDefaultGlobalStores = IsUsingDefaultAppConfigDir();
            NodeSnapshot merged = null;

            // 1. 读取本机高水位持久化文件 local_highwater_<id>.json
            try
            {
                string hwPath = SyncService.GetLocalHighWaterFilePath(localMid);
                if (File.Exists(hwPath))
                {
                    string hwRaw = File.ReadAllText(hwPath, Encoding.UTF8);
                    NodeSnapshot hwSnap = SyncService.DeserializeSnapshot(hwRaw, true);
                    if (hwSnap != null)
                    {
                        hwSnap.MachineId = localMid;
                        hwSnap.IsLocal = true;
                        merged = hwSnap;
                    }
                }
            }
            catch { }

            // 2. 读取 LocalUsageStore 瞬时快照（毫秒级 kv_meta 点查）并与高水位合并
            if (useDefaultGlobalStores)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(defaultHome))
                    {
                        ScanResult instant = LocalUsageStore.TryLoadInstantSnapshot(defaultHome);
                        if (instant != null && instant.Records != null && instant.Records.Count > 0)
                        {
                            NodeSnapshot instSnap = new NodeSnapshot();
                            instSnap.MachineId = localMid;
                            instSnap.MachineName = localName;
                            instSnap.MachineType = "windows";
                            instSnap.OsInfo = osText;
                            instSnap.SyncedAt = !string.IsNullOrWhiteSpace(instant.GeneratedAt) ? instant.GeneratedAt : nowStr;
                            instSnap.IsLocal = true;
                            for (int i = 0; i < instant.Records.Count; i++)
                            {
                                RecordBucket rb = instant.Records[i];
                                if (rb != null)
                                {
                                    RecordBucket copy = rb.Clone();
                                    copy.MachineId = localMid;
                                    copy.MachineName = localName;
                                    copy.MachineType = "windows";
                                    instSnap.Records.Add(copy);
                                }
                            }
                            merged = SyncService.MergeNodeSnapshotsHighWater(instSnap, merged);
                        }
                    }
                }
                catch { }
            }

            // 3. 若上述两处均无记录但 SQLite 账本已存在（如刚升级 Schema 尚未重扫），兜底提取 daily_archive 历史归档
            if (useDefaultGlobalStores &&
                (merged == null || merged.Records == null || merged.Records.Count == 0) &&
                !string.IsNullOrWhiteSpace(defaultHome))
            {
                try
                {
                    if (File.Exists(LocalUsageStore.DbFilePath))
                    {
                        List<RecordBucket> archivedBuckets = new List<RecordBucket>();
                        LocalUsageStore.MergeAndSaveDailyArchive(defaultHome, archivedBuckets);
                        if (archivedBuckets.Count > 0)
                        {
                            NodeSnapshot archSnap = new NodeSnapshot();
                            archSnap.MachineId = localMid;
                            archSnap.MachineName = localName;
                            archSnap.MachineType = "windows";
                            archSnap.OsInfo = osText;
                            archSnap.SyncedAt = nowStr;
                            archSnap.IsLocal = true;
                            for (int i = 0; i < archivedBuckets.Count; i++)
                            {
                                RecordBucket rb = archivedBuckets[i];
                                if (rb != null)
                                {
                                    RecordBucket copy = rb.Clone();
                                    copy.MachineId = localMid;
                                    copy.MachineName = localName;
                                    copy.MachineType = "windows";
                                    archSnap.Records.Add(copy);
                                }
                            }
                            merged = SyncService.MergeNodeSnapshotsHighWater(archSnap, merged);
                        }
                    }
                }
                catch { }
            }

            // 4. 若 nodes 目录下存在同 MachineId 的残留快照，一并高水位吸收防漏
            try
            {
                string sameNodeCache = Path.Combine(SyncService.NodesCacheDir, "toklens_node_" + localMid + ".json");
                if (File.Exists(sameNodeCache))
                {
                    string rawSame = File.ReadAllText(sameNodeCache, Encoding.UTF8);
                    NodeSnapshot cachedSelf = SyncService.DeserializeSnapshot(rawSame, true);
                    if (cachedSelf != null)
                        merged = SyncService.MergeNodeSnapshotsHighWater(merged, cachedSelf);
                }
            }
            catch { }

            if (merged == null)
            {
                merged = new NodeSnapshot();
                merged.MachineId = localMid;
                merged.MachineName = localName;
                merged.MachineType = "windows";
                merged.OsInfo = osText;
                merged.SyncedAt = nowStr;
                merged.IsLocal = true;
            }
            else
            {
                merged.MachineId = localMid;
                if (string.IsNullOrWhiteSpace(merged.MachineName))
                    merged.MachineName = localName;
                if (string.IsNullOrWhiteSpace(merged.MachineType))
                    merged.MachineType = "windows";
                if (string.IsNullOrWhiteSpace(merged.OsInfo))
                    merged.OsInfo = osText;
                if (string.IsNullOrWhiteSpace(merged.SyncedAt))
                    merged.SyncedAt = nowStr;
                merged.IsLocal = true;
            }

            return merged;
        }

        private static List<NodeSnapshot> CollectAllSnapshotsForBackup(SyncConfig cfg, string defaultHome)
        {
            string localMid = SyncService.SanitizeMachineId(cfg != null ? cfg.MachineId : "win-pc");
            NodeSnapshot localSnap = BuildBestLocalNodeSnapshot(cfg, defaultHome);

            Dictionary<string, NodeSnapshot> otherMap = new Dictionary<string, NodeSnapshot>(StringComparer.OrdinalIgnoreCase);

            List<NodeSnapshot> cachedRemotes = SyncService.LoadCachedRemoteNodes(localMid);
            for (int i = 0; i < cachedRemotes.Count; i++)
            {
                NodeSnapshot r = cachedRemotes[i];
                if (r == null || string.IsNullOrWhiteSpace(r.MachineId)) continue;
                string mid = SyncService.SanitizeMachineId(r.MachineId);
                if (string.Equals(mid, localMid, StringComparison.OrdinalIgnoreCase))
                {
                    localSnap = SyncService.MergeNodeSnapshotsHighWater(localSnap, r);
                    continue;
                }
                r.MachineId = mid;
                r.IsLocal = false;
                NodeSnapshot prev;
                if (otherMap.TryGetValue(mid, out prev))
                    otherMap[mid] = SyncService.MergeNodeSnapshotsHighWater(r, prev);
                else
                    otherMap[mid] = r;
            }

            // 额外扫描 AppConfigDir 下可能遗留的旧设备名高水位文件 local_highwater_*.json，确保换过设备名也零遗漏
            try
            {
                if (Directory.Exists(SyncService.AppConfigDir))
                {
                    string[] hwFiles = Directory.GetFiles(SyncService.AppConfigDir, "local_highwater_*.json");
                    for (int i = 0; i < hwFiles.Length; i++)
                    {
                        try
                        {
                            string raw = File.ReadAllText(hwFiles[i], Encoding.UTF8);
                            NodeSnapshot extra = SyncService.DeserializeSnapshot(raw, false);
                            if (extra == null || extra.Records == null || extra.Records.Count == 0)
                                continue;
                            string mid = SyncService.SanitizeMachineId(extra.MachineId);
                            if (string.Equals(mid, localMid, StringComparison.OrdinalIgnoreCase))
                            {
                                localSnap = SyncService.MergeNodeSnapshotsHighWater(localSnap, extra);
                            }
                            else
                            {
                                extra.MachineId = mid;
                                extra.IsLocal = false;
                                NodeSnapshot prev;
                                if (otherMap.TryGetValue(mid, out prev))
                                    otherMap[mid] = SyncService.MergeNodeSnapshotsHighWater(prev, extra);
                                else
                                    otherMap[mid] = extra;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            List<NodeSnapshot> result = new List<NodeSnapshot>();
            if (localSnap != null && (localSnap.Records.Count > 0 || otherMap.Count == 0))
            {
                result.Add(localSnap);
            }

            if (otherMap.Count > 0)
            {
                List<NodeSnapshot> sortedOthers = new List<NodeSnapshot>(otherMap.Values);
                sortedOthers.Sort(delegate(NodeSnapshot a, NodeSnapshot b)
                {
                    return string.Compare(a.MachineId ?? "", b.MachineId ?? "", StringComparison.OrdinalIgnoreCase);
                });
                for (int i = 0; i < sortedOthers.Count; i++)
                {
                    result.Add(sortedOthers[i]);
                }
            }

            return result;
        }

        private static Dictionary<string, object> SerializeNodeToDict(NodeSnapshot snap)
        {
            List<object[]> rows = new List<object[]>(snap.Records != null ? snap.Records.Count : 0);
            if (snap.Records != null)
            {
                for (int i = 0; i < snap.Records.Count; i++)
                {
                    RecordBucket r = snap.Records[i];
                    if (r == null) continue;
                    rows.Add(new object[]
                    {
                        r.Date ?? "",
                        r.Tool ?? "",
                        r.Model ?? "",
                        r.Turns,
                        r.FreshInput,
                        r.CacheRead,
                        r.CacheWrite,
                        r.Output,
                        SanitizeDouble(r.CostUsd, 6),
                        SanitizeDouble(r.BaselineUsd, 6),
                        r.RateLabel ?? ""
                    });
                }
            }

            Dictionary<string, object> nodeObj = new Dictionary<string, object>();
            nodeObj["schema"] = SyncService.SchemaVersion;
            nodeObj["machine_id"] = SyncService.SanitizeMachineId(snap.MachineId);
            nodeObj["machine_name"] = !string.IsNullOrWhiteSpace(snap.MachineName) ? snap.MachineName : snap.MachineId;
            nodeObj["machine_type"] = !string.IsNullOrWhiteSpace(snap.MachineType) ? snap.MachineType : "windows";
            nodeObj["os"] = snap.OsInfo ?? "";
            nodeObj["synced_at"] = snap.SyncedAt ?? "";
            nodeObj["is_local"] = snap.IsLocal;
            nodeObj["records"] = rows;
            return nodeObj;
        }

        private static void MergeCustomPricesFileInto(string filePath, Dictionary<string, object> target, JavaScriptSerializer js)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;
            try
            {
                string raw = StripUtf8BomAndTrim(File.ReadAllText(filePath, Encoding.UTF8));
                if (string.IsNullOrEmpty(raw)) return;
                Dictionary<string, object> map = js.DeserializeObject(raw) as Dictionary<string, object>;
                if (map == null) return;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    IDictionary m = kv.Value as IDictionary;
                    if (m == null) continue;
                    double ic = ReadDictDouble(m, "input");
                    double oc = ReadDictDouble(m, "output");
                    double crc = ReadDictDouble(m, "cache_read");
                    double cwc = ReadDictDouble(m, "cache_write");
                    if (ic > 0.0 || oc > 0.0)
                    {
                        Dictionary<string, object> cleanEntry = new Dictionary<string, object>();
                        cleanEntry["input"] = SanitizeDouble(ic, 6);
                        cleanEntry["output"] = SanitizeDouble(oc, 6);
                        cleanEntry["cache_read"] = SanitizeDouble(crc, 6);
                        cleanEntry["cache_write"] = SanitizeDouble(cwc, 6);
                        target[kv.Key.Trim()] = cleanEntry;
                    }
                }
            }
            catch { }
        }

        private static Dictionary<string, object> CollectCustomPricesForBackup(string defaultHome, JavaScriptSerializer js)
        {
            Dictionary<string, object> merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (IsUsingDefaultAppConfigDir())
            {
                if (!string.IsNullOrWhiteSpace(defaultHome))
                {
                    MergeCustomPricesFileInto(Path.Combine(defaultHome, ".toklens", "custom_prices.json"), merged, js);
                }
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                {
                    MergeCustomPricesFileInto(Path.Combine(appData, "TokLens", "custom_prices.json"), merged, js);
                }
            }
            string cfgPrices = Path.Combine(SyncService.AppConfigDir, "custom_prices.json");
            MergeCustomPricesFileInto(cfgPrices, merged, js);
            return merged;
        }

        private static string SanitizeUrlWithoutCredentials(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return "";
            string trimmed = rawUrl.Trim();
            Uri uri;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                try
                {
                    UriBuilder ub = new UriBuilder(uri);
                    ub.UserName = "";
                    ub.Password = "";
                    return ub.Uri.AbsoluteUri;
                }
                catch
                {
                    int atIdx = trimmed.IndexOf('@');
                    int schemeIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
                    if (schemeIdx >= 0 && atIdx > schemeIdx + 3)
                        return trimmed.Substring(0, schemeIdx + 3) + trimmed.Substring(atIdx + 1);
                }
            }
            return trimmed;
        }

        private static Dictionary<string, object> BuildSanitizedConfigDict(SyncConfig cfg)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            if (cfg == null) return d;

            // 严格脱敏：绝不写入任何明文或 DPAPI 密文 Token / WebDAV 密码
            d["machine_id"] = SyncService.SanitizeMachineId(cfg.MachineId);
            d["machine_name"] = cfg.MachineName ?? "";
            d["provider"] = string.IsNullOrWhiteSpace(cfg.Provider) ? "gist" : cfg.Provider.Trim();
            d["auto_sync"] = cfg.AutoSync;
            d["scan_local_wsl"] = cfg.ScanLocalWsl;
            d["gist_id"] = SyncService.NormalizeGistId(cfg.GistId);
            d["has_gist_token"] = !string.IsNullOrWhiteSpace(cfg.GistToken);
            d["gist_token_hint"] = SyncService.MaskTokenFingerprint(cfg.GistToken);
            d["webdav_url"] = SanitizeUrlWithoutCredentials(cfg.WebDavUrl);
            d["webdav_user"] = (cfg.WebDavUser ?? "").Trim();
            d["has_webdav_pass"] = !string.IsNullOrWhiteSpace(cfg.WebDavPass);
            d["is_dark_theme"] = cfg.IsDarkTheme;
            d["prefer_cny"] = cfg.PreferCny;
            d["minimize_to_tray"] = cfg.MinimizeToTray;
            d["daily_budget_usd"] = SanitizeDouble(Math.Max(0.0, cfg.DailyBudgetUsd), 4);

            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cfg.NodeAliases != null)
            {
                foreach (KeyValuePair<string, string> kv in cfg.NodeAliases)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        aliases[kv.Key.Trim()] = kv.Value.Trim();
                }
            }
            d["node_aliases"] = aliases;
            return d;
        }

        /// <summary>
        /// 清理超期自动备份，仅保留最近 maxKeep 份自动快照以防占用磁盘空间。
        /// </summary>
        public static int PruneOldAutoBackups(int maxKeep)
        {
            int keep = maxKeep < 1 ? 1 : maxKeep;
            int deleted = 0;
            try
            {
                string dir = BackupsDir;
                if (!Directory.Exists(dir))
                    return 0;

                string[] allFiles = Directory.GetFiles(dir);
                List<FileInfo> autoFiles = new List<FileInfo>();
                for (int i = 0; i < allFiles.Length; i++)
                {
                    string fn = Path.GetFileName(allFiles[i]) ?? "";
                    if (!fn.StartsWith(AutoBackupPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!fn.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
                        !fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        FileInfo fi = new FileInfo(allFiles[i]);
                        if (fi.Exists)
                            autoFiles.Add(fi);
                    }
                    catch { }
                }

                if (autoFiles.Count <= keep)
                    return 0;

                autoFiles.Sort(CompareAutoBackupFilesNewestFirst);
                for (int i = keep; i < autoFiles.Count; i++)
                {
                    try
                    {
                        File.Delete(autoFiles[i].FullName);
                        deleted++;
                    }
                    catch { }
                }
            }
            catch { }
            return deleted;
        }

        private static int CompareAutoBackupFilesNewestFirst(FileInfo a, FileInfo b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            string stampA = ExtractStampSortKey(a.Name);
            string stampB = ExtractStampSortKey(b.Name);
            if (!string.IsNullOrEmpty(stampA) && !string.IsNullOrEmpty(stampB))
            {
                int cmp = string.CompareOrdinal(stampB, stampA);
                if (cmp != 0) return cmp;
            }
            int timeCmp = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            if (timeCmp != 0) return timeCmp;
            return string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseBackupStamp(string fileName, out string datePart8, out string timePart6)
        {
            datePart8 = "";
            timePart6 = "";
            if (string.IsNullOrEmpty(fileName)) return false;
            int prefixLen = AutoBackupPrefix.Length; // 15 ("toklens_backup_")
            if (fileName.Length < prefixLen + 15) return false;
            if (!fileName.StartsWith(AutoBackupPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName[prefixLen + 8] != '_') return false;
            for (int i = 0; i < 8; i++)
            {
                char c = fileName[prefixLen + i];
                if (c < '0' || c > '9') return false;
            }
            for (int i = 0; i < 6; i++)
            {
                char c = fileName[prefixLen + 9 + i];
                if (c < '0' || c > '9') return false;
            }
            datePart8 = fileName.Substring(prefixLen, 8);
            timePart6 = fileName.Substring(prefixLen + 9, 6);
            return true;
        }

        private static string ExtractStampSortKey(string fileName)
        {
            string d;
            string t;
            if (!TryParseBackupStamp(fileName, out d, out t))
                return "";
            return d + "_" + t;
        }

        /// <summary>
        /// 将当前本机账本快照（LocalUsageStore / 已缓存的 nodes/*.json 全节点高水位数据、
        /// custom_prices.json 自定义单价，以及可选的不含明文密码脱敏配置）打包为单一高压缩 .toklens.gz 或 .toklens.json。
        /// 若 targetFilePathOrNull 为 null，则自动存入 BackupsDir 并自动清理超期旧快照。
        /// </summary>
        public static BackupArchiveInfo CreateSnapshotBundle(string targetFilePathOrNull, bool includeSanitizedConfig)
        {
            lock (BackupGate)
            {
                DateTime now = DateTime.Now;
                bool useGZip;
                bool isAutoManaged;
                string finalPath = NormalizeTargetFilePath(targetFilePathOrNull, now, out useGZip, out isAutoManaged);

                SyncConfig cfg = SyncService.LoadConfig();
                string defaultHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                JavaScriptSerializer js = NewJson();

                List<NodeSnapshot> nodes = CollectAllSnapshotsForBackup(cfg, defaultHome);
                List<Dictionary<string, object>> serializedNodes = new List<Dictionary<string, object>>(nodes.Count);
                int totalRecordCount = 0;

                for (int i = 0; i < nodes.Count; i++)
                {
                    NodeSnapshot n = nodes[i];
                    if (n == null) continue;
                    totalRecordCount += (n.Records != null ? n.Records.Count : 0);
                    serializedNodes.Add(SerializeNodeToDict(n));
                }

                Dictionary<string, object> customPrices = CollectCustomPricesForBackup(defaultHome, js);
                string createdAtText = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                // 将高频元数据字段置于根字典最前方，使 ListLocalBackups 仅需解压前数 KB 即可在 <1ms 内读取统计信息
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["schema"] = BundleSchemaVersion;
                root["created_at"] = createdAtText;
                root["app_version"] = AppConstants.Version;
                root["local_machine_id"] = SyncService.SanitizeMachineId(cfg.MachineId);
                root["node_count"] = serializedNodes.Count;
                root["record_count"] = totalRecordCount;
                if (customPrices.Count > 0)
                {
                    root["custom_prices"] = customPrices;
                }
                if (includeSanitizedConfig)
                {
                    root["sanitized_config"] = BuildSanitizedConfigDict(cfg);
                }
                root["nodes"] = serializedNodes;

                string jsonPayload = js.Serialize(root);
                byte[] outBytes = useGZip
                    ? CompressUtf8ToGZip(jsonPayload)
                    : new UTF8Encoding(false).GetBytes(jsonPayload);

                AtomicWriteBytes(finalPath, outBytes);

                if (isAutoManaged)
                {
                    PruneOldAutoBackups(MaxAutoBackupsToKeep);
                }

                long sizeBytes = outBytes.LongLength;
                try
                {
                    FileInfo fi = new FileInfo(finalPath);
                    if (fi.Exists)
                        sizeBytes = fi.Length;
                }
                catch { }

                BackupArchiveInfo info = new BackupArchiveInfo();
                info.FilePath = finalPath;
                info.FileName = Path.GetFileName(finalPath) ?? "";
                info.CreatedAtText = createdAtText;
                info.SizeBytes = sizeBytes;
                info.SizeText = FormatFileSize(sizeBytes);
                info.NodeCount = serializedNodes.Count;
                info.RecordCount = totalRecordCount;

                if (isAutoManaged)
                {
                    string todayCompact = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    _cachedBackupsDir = BackupsDir;
                    _cachedAutoBackupDay = todayCompact;
                    _cachedAutoBackupInfo = info;
                    _cachedAutoBackupHadData = (totalRecordCount > 0 || customPrices.Count > 0);
                }

                return info;
            }
        }

        private static bool HasNonEmptyLocalSourceFast(SyncConfig cfg)
        {
            try
            {
                string localMid = SyncService.SanitizeMachineId(cfg != null ? cfg.MachineId : "win-pc");
                string hwPath = SyncService.GetLocalHighWaterFilePath(localMid);
                if (File.Exists(hwPath))
                {
                    FileInfo fi = new FileInfo(hwPath);
                    if (fi.Exists && fi.Length > 220L)
                        return true;
                }

                if (Directory.Exists(SyncService.NodesCacheDir))
                {
                    string[] remoteFiles = Directory.GetFiles(SyncService.NodesCacheDir, "toklens_node_*.json");
                    if (remoteFiles.Length > 0)
                        return true;
                }

                if (IsUsingDefaultAppConfigDir() && File.Exists(LocalUsageStore.DbFilePath))
                {
                    FileInfo dbfi = new FileInfo(LocalUsageStore.DbFilePath);
                    if (dbfi.Exists && dbfi.Length > 16384L)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 每日首次启动或同步前静默生成一份本地时光机快照（若今日已存在有效快照则直接跳过，耗时 &lt; 20ms）。
        /// </summary>
        public static BackupArchiveInfo EnsureDailyAutoBackup()
        {
            lock (BackupGate)
            {
                try
                {
                    DateTime now = DateTime.Now;
                    string todayCompact = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    string dir = BackupsDir;

                    // 1. 进程内内存热缓存快速命中（< 0.05ms）
                    if (string.Equals(_cachedBackupsDir, dir, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(_cachedAutoBackupDay, todayCompact, StringComparison.Ordinal) &&
                        _cachedAutoBackupInfo != null &&
                        !string.IsNullOrEmpty(_cachedAutoBackupInfo.FilePath) &&
                        File.Exists(_cachedAutoBackupInfo.FilePath))
                    {
                        if (_cachedAutoBackupHadData)
                            return _cachedAutoBackupInfo;

                        SyncConfig quickCfg = SyncService.LoadConfig();
                        if (!HasNonEmptyLocalSourceFast(quickCfg))
                            return _cachedAutoBackupInfo;
                    }

                    // 2. 磁盘目录快速检索今日快照文件（仅读前 8KB 流头，< 2ms）
                    if (Directory.Exists(dir))
                    {
                        string[] allFiles = Directory.GetFiles(dir);
                        FileInfo newestToday = null;
                        for (int i = 0; i < allFiles.Length; i++)
                        {
                            string fn = Path.GetFileName(allFiles[i]) ?? "";
                            if (!fn.StartsWith(AutoBackupPrefix, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!fn.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
                                !fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                continue;

                            bool isTodayByName = fn.StartsWith(AutoBackupPrefix + todayCompact + "_", StringComparison.OrdinalIgnoreCase);
                            try
                            {
                                FileInfo fi = new FileInfo(allFiles[i]);
                                if (!fi.Exists || fi.Length <= 0L)
                                    continue;
                                if (!isTodayByName && fi.LastWriteTime.Date != now.Date)
                                    continue;

                                if (newestToday == null || CompareAutoBackupFilesNewestFirst(fi, newestToday) < 0)
                                    newestToday = fi;
                            }
                            catch { }
                        }

                        if (newestToday != null)
                        {
                            BackupArchiveInfo existingInfo = TryReadArchiveInfoFast(newestToday);
                            if (existingInfo != null)
                            {
                                bool hasData = existingInfo.RecordCount > 0;
                                if (!hasData)
                                {
                                    SyncConfig quickCfg = SyncService.LoadConfig();
                                    if (HasNonEmptyLocalSourceFast(quickCfg))
                                    {
                                        // 当日首次启动空快照后账本已产生数据：静默升级一次今日快照并清理空快照
                                        string staleEmptyPath = newestToday.FullName;
                                        BackupArchiveInfo upgraded = CreateSnapshotBundle(null, true);
                                        if (upgraded != null &&
                                            upgraded.RecordCount > 0 &&
                                            !string.Equals(upgraded.FilePath, staleEmptyPath, StringComparison.OrdinalIgnoreCase))
                                        {
                                            try { File.Delete(staleEmptyPath); } catch { }
                                        }
                                        return upgraded ?? existingInfo;
                                    }
                                }

                                _cachedBackupsDir = dir;
                                _cachedAutoBackupDay = todayCompact;
                                _cachedAutoBackupInfo = existingInfo;
                                _cachedAutoBackupHadData = hasData;
                                return existingInfo;
                            }
                        }
                    }

                    // 3. 今日尚无自动备份，直接基于本地缓存快照生成高压缩时光机归档
                    return CreateSnapshotBundle(null, true);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static bool TryExtractJsonHeaderInt(string text, string quotedKey, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(quotedKey))
                return false;
            int idx = text.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0)
                return false;
            int pos = idx + quotedKey.Length;
            int len = text.Length;
            while (pos < len && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r' || text[pos] == '\n'))
                pos++;
            if (pos >= len || text[pos] != ':')
                return false;
            pos++;
            while (pos < len && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r' || text[pos] == '\n'))
                pos++;
            if (pos >= len || text[pos] < '0' || text[pos] > '9')
                return false;
            int start = pos;
            while (pos < len && text[pos] >= '0' && text[pos] <= '9')
                pos++;
            return int.TryParse(text.Substring(start, pos - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static BackupArchiveInfo TryReadArchiveInfoFast(FileInfo fi)
        {
            if (fi == null || !fi.Exists || fi.Length <= 0L)
                return null;

            BackupArchiveInfo info = new BackupArchiveInfo();
            info.FilePath = fi.FullName;
            info.FileName = fi.Name;
            info.SizeBytes = fi.Length;
            info.SizeText = FormatFileSize(fi.Length);
            info.CreatedAtText = FormatFallbackTimestamp(fi);

            try
            {
                string head = ReadArchiveText(fi.FullName, FastHeaderProbeBytes);
                if (string.IsNullOrEmpty(head))
                    return null;

                string createdVal = FastJson.ExtractString(head, "\"created_at\"", 0, head.Length);
                if (!string.IsNullOrWhiteSpace(createdVal))
                {
                    info.CreatedAtText = createdVal.Trim();
                }

                int nc;
                int rc;
                if (TryExtractJsonHeaderInt(head, "\"node_count\"", out nc) &&
                    TryExtractJsonHeaderInt(head, "\"record_count\"", out rc))
                {
                    info.NodeCount = Math.Max(0, nc);
                    info.RecordCount = Math.Max(0, rc);
                    return info;
                }

                // 回退完整解析（如手工放入 backups 目录的单节点 JSON 或旧格式归档）
                string fullJson = head.Length < FastHeaderProbeBytes - 64 ? head : ReadArchiveText(fi.FullName, -1);
                List<NodeSnapshot> parsedNodes;
                Dictionary<string, object> dummyPrices;
                Dictionary<string, object> dummyCfg;
                string parsedCreated;
                string err;
                if (TryParseAnySnapshotJson(fullJson, out parsedNodes, out dummyPrices, out dummyCfg, out parsedCreated, out err))
                {
                    if (!string.IsNullOrWhiteSpace(parsedCreated))
                        info.CreatedAtText = parsedCreated.Trim();
                    info.NodeCount = parsedNodes.Count;
                    int sumRec = 0;
                    for (int i = 0; i < parsedNodes.Count; i++)
                    {
                        if (parsedNodes[i] != null && parsedNodes[i].Records != null)
                            sumRec += parsedNodes[i].Records.Count;
                    }
                    info.RecordCount = sumRec;
                    return info;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string FormatFallbackTimestamp(FileInfo fi)
        {
            if (fi != null)
            {
                string d;
                string t;
                if (TryParseBackupStamp(fi.Name ?? "", out d, out t))
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}-{1}-{2} {3}:{4}:{5}",
                        d.Substring(0, 4),
                        d.Substring(4, 2),
                        d.Substring(6, 2),
                        t.Substring(0, 2),
                        t.Substring(2, 2),
                        t.Substring(4, 2));
                }
                return fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return "";
        }

        /// <summary>
        /// 列出本地时光机历史备份列表（按时间倒序排列），供 UI 展示、导出或一键恢复。
        /// </summary>
        public static List<BackupArchiveInfo> ListLocalBackups()
        {
            List<BackupArchiveInfo> list = new List<BackupArchiveInfo>();
            lock (BackupGate)
            {
                try
                {
                    string dir = BackupsDir;
                    if (!Directory.Exists(dir))
                        return list;

                    string[] files = Directory.GetFiles(dir);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string fp = files[i];
                        string fn = Path.GetFileName(fp) ?? "";
                        if (string.IsNullOrEmpty(fn) || fn.IndexOf(".tmp.", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        if (!fn.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
                            !fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            FileInfo fi = new FileInfo(fp);
                            BackupArchiveInfo info = TryReadArchiveInfoFast(fi);
                            if (info != null)
                                list.Add(info);
                        }
                        catch { }
                    }

                    list.Sort(delegate(BackupArchiveInfo a, BackupArchiveInfo b)
                    {
                        int cmp = string.CompareOrdinal(b.CreatedAtText ?? "", a.CreatedAtText ?? "");
                        if (cmp != 0) return cmp;
                        return string.Compare(b.FileName ?? "", a.FileName ?? "", StringComparison.OrdinalIgnoreCase);
                    });
                }
                catch { }
            }
            return list;
        }

        private static string ReadDictString(IDictionary d, string key1, string key2)
        {
            if (d == null) return null;
            if (!string.IsNullOrEmpty(key1) && d.Contains(key1) && d[key1] != null)
                return Convert.ToString(d[key1], CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(key2) && d.Contains(key2) && d[key2] != null)
                return Convert.ToString(d[key2], CultureInfo.InvariantCulture);
            return null;
        }

        private static int ReadDictInt(IDictionary d, string key1, string key2)
        {
            string s = ReadDictString(d, key1, key2);
            if (string.IsNullOrEmpty(s)) return 0;
            int iv;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
                return iv;
            double dv;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                return (int)dv;
            return 0;
        }

        private static long ReadDictLong(IDictionary d, string key1, string key2)
        {
            string s = ReadDictString(d, key1, key2);
            if (string.IsNullOrEmpty(s)) return 0L;
            long lv;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                return lv;
            double dv;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                return (long)dv;
            return 0L;
        }

        private static double ReadDictDouble(IDictionary d, string key)
        {
            return ReadDictDoublePair(d, key, null);
        }

        private static double ReadDictDoublePair(IDictionary d, string key1, string key2)
        {
            string s = ReadDictString(d, key1, key2);
            if (string.IsNullOrEmpty(s)) return 0.0;
            double dv;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
            {
                if (!double.IsNaN(dv) && !double.IsInfinity(dv))
                    return dv;
            }
            return 0.0;
        }

        private static NodeSnapshot ParseNodeFromObject(object rawNode, JavaScriptSerializer js)
        {
            if (rawNode == null) return null;

            string rawStr = rawNode as string;
            if (rawStr != null)
            {
                NodeSnapshot direct = SyncService.DeserializeSnapshot(rawStr, false);
                if (direct != null) return direct;
                try
                {
                    rawNode = js.DeserializeObject(rawStr);
                }
                catch
                {
                    return null;
                }
            }

            IDictionary d = rawNode as IDictionary;
            if (d == null) return null;

            string rawMid = ReadDictString(d, "machine_id", "MachineId");
            if (string.IsNullOrWhiteSpace(rawMid))
                return null;

            NodeSnapshot snap = new NodeSnapshot();
            snap.MachineId = SyncService.SanitizeMachineId(rawMid);
            snap.MachineName = ReadDictString(d, "machine_name", "MachineName") ?? snap.MachineId;
            if (string.IsNullOrWhiteSpace(snap.MachineName))
                snap.MachineName = snap.MachineId;
            snap.MachineType = ReadDictString(d, "machine_type", "MachineType") ?? "vps";
            if (string.IsNullOrWhiteSpace(snap.MachineType))
                snap.MachineType = "vps";
            snap.OsInfo = ReadDictString(d, "os", "OsInfo") ?? "";
            snap.SyncedAt = ReadDictString(d, "synced_at", "SyncedAt") ?? "";
            snap.IsLocal = false;

            object recContainer = null;
            if (d.Contains("records")) recContainer = d["records"];
            else if (d.Contains("Records")) recContainer = d["Records"];

            IList arr = recContainer as IList;
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    object item = arr[i];
                    if (item == null) continue;

                    IList row = item as IList;
                    if (row != null)
                    {
                        if (row.Count < 10) continue;
                        RecordBucket r = new RecordBucket();
                        r.MachineId = snap.MachineId;
                        r.MachineName = snap.MachineName;
                        r.MachineType = snap.MachineType;
                        r.Date = Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? "";
                        r.Tool = Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? "";
                        r.Model = Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? "";
                        r.Turns = Math.Max(0, Convert.ToInt32(row[3], CultureInfo.InvariantCulture));
                        r.FreshInput = Math.Max(0L, Convert.ToInt64(row[4], CultureInfo.InvariantCulture));
                        r.CacheRead = Math.Max(0L, Convert.ToInt64(row[5], CultureInfo.InvariantCulture));
                        r.CacheWrite = Math.Max(0L, Convert.ToInt64(row[6], CultureInfo.InvariantCulture));
                        r.Output = Math.Max(0L, Convert.ToInt64(row[7], CultureInfo.InvariantCulture));
                        r.CostUsd = SanitizeDouble(Math.Max(0.0, Convert.ToDouble(row[8], CultureInfo.InvariantCulture)), -1);
                        r.BaselineUsd = SanitizeDouble(Math.Max(0.0, Convert.ToDouble(row[9], CultureInfo.InvariantCulture)), -1);
                        r.RateLabel = row.Count > 10 ? (Convert.ToString(row[10], CultureInfo.InvariantCulture) ?? "") : "";
                        snap.Records.Add(r);
                        continue;
                    }

                    IDictionary rd = item as IDictionary;
                    if (rd != null)
                    {
                        RecordBucket r = new RecordBucket();
                        r.MachineId = snap.MachineId;
                        r.MachineName = snap.MachineName;
                        r.MachineType = snap.MachineType;
                        r.Date = ReadDictString(rd, "date", "Date") ?? "";
                        r.Tool = ReadDictString(rd, "tool", "Tool") ?? "";
                        r.Model = ReadDictString(rd, "model", "Model") ?? "";
                        r.Turns = Math.Max(0, ReadDictInt(rd, "turns", "Turns"));
                        r.FreshInput = Math.Max(0L, ReadDictLong(rd, "fresh_input", "FreshInput"));
                        r.CacheRead = Math.Max(0L, ReadDictLong(rd, "cache_read", "CacheRead"));
                        r.CacheWrite = Math.Max(0L, ReadDictLong(rd, "cache_write", "CacheWrite"));
                        r.Output = Math.Max(0L, ReadDictLong(rd, "output", "Output"));
                        r.CostUsd = Math.Max(0.0, ReadDictDoublePair(rd, "cost_usd", "CostUsd"));
                        r.BaselineUsd = Math.Max(0.0, ReadDictDoublePair(rd, "baseline_usd", "BaselineUsd"));
                        r.RateLabel = ReadDictString(rd, "rate_label", "RateLabel") ?? "";
                        if (!string.IsNullOrEmpty(r.Date) || !string.IsNullOrEmpty(r.Model))
                            snap.Records.Add(r);
                    }
                }
            }

            return snap;
        }

        private static bool TryParseAnySnapshotJson(
            string json,
            out List<NodeSnapshot> nodes,
            out Dictionary<string, object> customPrices,
            out Dictionary<string, object> sanitizedConfig,
            out string createdAtText,
            out string errorReason)
        {
            nodes = new List<NodeSnapshot>();
            customPrices = null;
            sanitizedConfig = null;
            createdAtText = "";
            errorReason = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                errorReason = "文件内容为空。";
                return false;
            }

            JavaScriptSerializer js = NewJson();
            object parsedRoot;
            try
            {
                parsedRoot = js.DeserializeObject(json);
            }
            catch (Exception ex)
            {
                errorReason = "JSON 格式解析失败（" + ex.Message + "）。";
                return false;
            }

            if (parsedRoot == null)
            {
                errorReason = "无法解析备份数据结构。";
                return false;
            }

            // 形态 A：顶层即为多节点数组 [ {...}, {...} ]
            IList rootArray = parsedRoot as IList;
            if (rootArray != null)
            {
                for (int i = 0; i < rootArray.Count; i++)
                {
                    NodeSnapshot n = ParseNodeFromObject(rootArray[i], js);
                    if (n != null) nodes.Add(n);
                }
                if (nodes.Count > 0) return true;
                errorReason = "JSON 数组中未找到有效的设备节点快照。";
                return false;
            }

            Dictionary<string, object> rootDict = parsedRoot as Dictionary<string, object>;
            if (rootDict == null)
            {
                errorReason = "不受支持的 JSON 顶层结构。";
                return false;
            }

            if (rootDict.ContainsKey("created_at") && rootDict["created_at"] != null)
                createdAtText = Convert.ToString(rootDict["created_at"], CultureInfo.InvariantCulture);
            else if (rootDict.ContainsKey("synced_at") && rootDict["synced_at"] != null)
                createdAtText = Convert.ToString(rootDict["synced_at"], CultureInfo.InvariantCulture);

            if (rootDict.ContainsKey("custom_prices") && rootDict["custom_prices"] is Dictionary<string, object>)
                customPrices = (Dictionary<string, object>)rootDict["custom_prices"];

            if (rootDict.ContainsKey("sanitized_config") && rootDict["sanitized_config"] is Dictionary<string, object>)
                sanitizedConfig = (Dictionary<string, object>)rootDict["sanitized_config"];

            // 形态 B：完整时光机备份包（含 nodes 列表或字典）
            if (rootDict.ContainsKey("nodes") && rootDict["nodes"] != null)
            {
                IList nodeList = rootDict["nodes"] as IList;
                if (nodeList != null)
                {
                    for (int i = 0; i < nodeList.Count; i++)
                    {
                        NodeSnapshot n = ParseNodeFromObject(nodeList[i], js);
                        if (n != null) nodes.Add(n);
                    }
                }
                else
                {
                    IDictionary nodeMap = rootDict["nodes"] as IDictionary;
                    if (nodeMap != null)
                    {
                        foreach (DictionaryEntry entry in nodeMap)
                        {
                            NodeSnapshot n = ParseNodeFromObject(entry.Value, js);
                            if (n != null) nodes.Add(n);
                        }
                    }
                }

                if (nodes.Count > 0)
                    return true;
            }

            // 形态 C：单节点快照 JSON（如内网 Linux 导出的 toklens_node_xxx.json 或 local_highwater_xxx.json）
            if (rootDict.ContainsKey("machine_id") || rootDict.ContainsKey("MachineId"))
            {
                NodeSnapshot single = SyncService.DeserializeSnapshot(json, false);
                if (single == null)
                    single = ParseNodeFromObject(rootDict, js);
                if (single != null)
                {
                    nodes.Add(single);
                    return true;
                }
            }

            // 形态 D：兼容离线导出的 GitHub Gist API 原始响应包（files -> toklens_node_xxx.json -> content）
            if (rootDict.ContainsKey("files") && rootDict["files"] is IDictionary)
            {
                IDictionary gistFiles = (IDictionary)rootDict["files"];
                foreach (DictionaryEntry entry in gistFiles)
                {
                    string fn = entry.Key != null ? Convert.ToString(entry.Key, CultureInfo.InvariantCulture) : "";
                    if (string.IsNullOrEmpty(fn) ||
                        fn.Length <= 18 ||
                        !fn.StartsWith("toklens_node_", StringComparison.OrdinalIgnoreCase) ||
                        !fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    IDictionary meta = entry.Value as IDictionary;
                    if (meta != null && meta.Contains("content") && meta["content"] != null)
                    {
                        string content = Convert.ToString(meta["content"], CultureInfo.InvariantCulture);
                        NodeSnapshot gNode = ParseNodeFromObject(content, js);
                        if (gNode != null)
                            nodes.Add(gNode);
                    }
                }
                if (nodes.Count > 0)
                    return true;
            }

            if (rootDict.ContainsKey("by_tool") && !rootDict.ContainsKey("records") && !rootDict.ContainsKey("nodes"))
            {
                errorReason = "该文件为统计摘要报表，不含分日明细账本，请导入 .toklens.gz 备份包或 toklens_node_*.json 节点快照。";
                return false;
            }

            errorReason = "未在文件中识别到有效的 TokLens 账本节点快照数据。";
            return false;
        }

        private static int MergeImportedCustomPrices(Dictionary<string, object> importedPrices, JavaScriptSerializer js)
        {
            if (importedPrices == null || importedPrices.Count == 0)
                return 0;

            int appliedCount = 0;
            try
            {
                string cfgPricesPath = Path.Combine(SyncService.AppConfigDir, "custom_prices.json");
                Dictionary<string, object> existing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                MergeCustomPricesFileInto(cfgPricesPath, existing, js);

                foreach (KeyValuePair<string, object> kv in importedPrices)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    IDictionary m = kv.Value as IDictionary;
                    if (m == null) continue;
                    double ic = ReadDictDouble(m, "input");
                    double oc = ReadDictDouble(m, "output");
                    double crc = ReadDictDouble(m, "cache_read");
                    double cwc = ReadDictDouble(m, "cache_write");
                    if (ic > 0.0 || oc > 0.0)
                    {
                        Dictionary<string, object> entry = new Dictionary<string, object>();
                        entry["input"] = SanitizeDouble(ic, 6);
                        entry["output"] = SanitizeDouble(oc, 6);
                        entry["cache_read"] = SanitizeDouble(crc, 6);
                        entry["cache_write"] = SanitizeDouble(cwc, 6);
                        existing[kv.Key.Trim()] = entry;
                        appliedCount++;
                    }
                }

                if (appliedCount > 0)
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(js.Serialize(existing));
                    AtomicWriteBytes(cfgPricesPath, bytes);
                }
            }
            catch
            {
                return 0;
            }

            return appliedCount;
        }

        private static void MergeNonSensitiveConfigPreferences(SyncConfig localCfg, Dictionary<string, object> importedSanitizedCfg)
        {
            if (localCfg == null || importedSanitizedCfg == null)
                return;

            bool changed = false;

            if (localCfg.DailyBudgetUsd <= 0.0 && importedSanitizedCfg.ContainsKey("daily_budget_usd"))
            {
                double impBudget = ReadDictDouble(importedSanitizedCfg, "daily_budget_usd");
                if (impBudget > 0.0)
                {
                    localCfg.DailyBudgetUsd = Math.Round(impBudget, 4);
                    changed = true;
                }
            }

            if (importedSanitizedCfg.ContainsKey("node_aliases") && importedSanitizedCfg["node_aliases"] is IDictionary)
            {
                if (localCfg.NodeAliases == null)
                    localCfg.NodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                IDictionary rawAliases = (IDictionary)importedSanitizedCfg["node_aliases"];
                foreach (DictionaryEntry entry in rawAliases)
                {
                    if (entry.Key == null || entry.Value == null) continue;
                    string k = Convert.ToString(entry.Key, CultureInfo.InvariantCulture).Trim();
                    string v = Convert.ToString(entry.Value, CultureInfo.InvariantCulture).Trim();
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v) && !localCfg.NodeAliases.ContainsKey(k))
                    {
                        localCfg.NodeAliases[k] = v;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                try { SyncService.SaveConfig(localCfg); } catch { }
            }
        }

        /// <summary>
        /// 导入并合并外部快照文件（同时支持 .toklens.gz 完整备份包、.toklens.json 及单节点 toklens_node_xxx.json 快照）。
        /// 采用高水位无损合并算法（SyncService.MergeNodeSnapshotsHighWater）安全合并入本地 NodesCacheDir 与历史高水位，
        /// 绝不冲掉任何更高历史数据，并返回成功合并的设备节点数及中文摘要信息。
        /// </summary>
        public static int ImportAndMergeSnapshotBundle(string filePath, PricingEngine pricing, out string summaryMessage)
        {
            summaryMessage = "";
            if (string.IsNullOrWhiteSpace(filePath))
            {
                summaryMessage = "导入失败：未指定快照文件路径。";
                return 0;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath.Trim());
            }
            catch (Exception ex)
            {
                summaryMessage = "导入失败：文件路径无效（" + ex.Message + "）。";
                return 0;
            }

            if (!File.Exists(fullPath))
            {
                summaryMessage = "导入失败：找不到指定的快照文件（" + fullPath + "）。";
                return 0;
            }

            lock (BackupGate)
            {
                string rawJson;
                try
                {
                    rawJson = ReadArchiveText(fullPath, -1);
                }
                catch (Exception ex)
                {
                    summaryMessage = "导入失败：读取或解压备份文件异常（" + ex.Message + "）。";
                    return 0;
                }

                List<NodeSnapshot> rawNodes;
                Dictionary<string, object> importedCustomPrices;
                Dictionary<string, object> importedSanitizedCfg;
                string createdAtText;
                string parseErr;
                if (!TryParseAnySnapshotJson(rawJson, out rawNodes, out importedCustomPrices, out importedSanitizedCfg, out createdAtText, out parseErr))
                {
                    summaryMessage = "导入失败：" + (parseErr ?? "快照格式无法识别。");
                    return 0;
                }

                JavaScriptSerializer js = NewJson();
                string defaultHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                int customPricesMerged = MergeImportedCustomPrices(importedCustomPrices, js);

                PricingEngine effectivePricing = pricing;
                if (effectivePricing == null || customPricesMerged > 0)
                {
                    effectivePricing = new PricingEngine(defaultHome);
                }

                SyncConfig cfg = SyncService.LoadConfig();
                MergeNonSensitiveConfigPreferences(cfg, importedSanitizedCfg);
                string localMid = SyncService.SanitizeMachineId(cfg.MachineId);

                // 先对导入包内同一 MachineId 的节点进行高水位去重预合并
                Dictionary<string, NodeSnapshot> dedupedMap = new Dictionary<string, NodeSnapshot>(StringComparer.OrdinalIgnoreCase);
                List<string> orderedIds = new List<string>();
                for (int i = 0; i < rawNodes.Count; i++)
                {
                    NodeSnapshot n = rawNodes[i];
                    if (n == null || string.IsNullOrWhiteSpace(n.MachineId))
                        continue;
                    n.MachineId = SyncService.SanitizeMachineId(n.MachineId);
                    NodeSnapshot prev;
                    if (dedupedMap.TryGetValue(n.MachineId, out prev))
                    {
                        dedupedMap[n.MachineId] = SyncService.MergeNodeSnapshotsHighWater(n, prev);
                    }
                    else
                    {
                        dedupedMap[n.MachineId] = n;
                        orderedIds.Add(n.MachineId);
                    }
                }

                if (orderedIds.Count == 0)
                {
                    summaryMessage = "导入失败：快照中不包含有效的设备账本节点。";
                    return 0;
                }

                int mergedNodeCount = 0;
                int importedRecordCount = 0;
                int retainedRecordCount = 0;
                bool mergedSelfNode = false;
                int mergedRemoteCount = 0;
                string singleNodeDisplayName = "";

                for (int i = 0; i < orderedIds.Count; i++)
                {
                    string mid = orderedIds[i];
                    NodeSnapshot incoming = dedupedMap[mid];
                    if (incoming == null) continue;

                    effectivePricing.RecalculateRemoteSnapshot(incoming);
                    int inRecs = incoming.Records != null ? incoming.Records.Count : 0;
                    importedRecordCount += inRecs;

                    if (string.Equals(mid, localMid, StringComparison.OrdinalIgnoreCase))
                    {
                        incoming.IsLocal = true;

                        // 1. 与本机高水位持久化快照做高水位无损合并
                        string hwPath = SyncService.GetLocalHighWaterFilePath(localMid);
                        NodeSnapshot existingHw = null;
                        try
                        {
                            if (File.Exists(hwPath))
                            {
                                string hwRaw = File.ReadAllText(hwPath, Encoding.UTF8);
                                existingHw = SyncService.DeserializeSnapshot(hwRaw, true);
                            }
                        }
                        catch { }

                        NodeSnapshot mergedLocal = SyncService.MergeNodeSnapshotsHighWater(incoming, existingHw);

                        // 2. 同步写入 LocalUsageStore (daily_archive) 再回流合并，确保下次增量扫描与瞬时启动均完整保留历史账本
                        if (IsUsingDefaultAppConfigDir() &&
                            !string.IsNullOrWhiteSpace(defaultHome) &&
                            mergedLocal.Records != null &&
                            mergedLocal.Records.Count > 0)
                        {
                            List<RecordBucket> archiveSyncList = new List<RecordBucket>(mergedLocal.Records.Count);
                            for (int rIdx = 0; rIdx < mergedLocal.Records.Count; rIdx++)
                            {
                                if (mergedLocal.Records[rIdx] != null)
                                    archiveSyncList.Add(mergedLocal.Records[rIdx].Clone());
                            }
                            LocalUsageStore.MergeAndSaveDailyArchive(defaultHome, archiveSyncList);

                            NodeSnapshot postDbSnap = new NodeSnapshot();
                            postDbSnap.MachineId = mergedLocal.MachineId;
                            postDbSnap.MachineName = mergedLocal.MachineName;
                            postDbSnap.MachineType = mergedLocal.MachineType;
                            postDbSnap.OsInfo = mergedLocal.OsInfo;
                            postDbSnap.SyncedAt = mergedLocal.SyncedAt;
                            postDbSnap.IsLocal = true;
                            postDbSnap.Records = archiveSyncList;
                            mergedLocal = SyncService.MergeNodeSnapshotsHighWater(postDbSnap, mergedLocal);
                        }

                        mergedLocal = SyncService.MergeWithLocalHighWater(mergedLocal);
                        SyncService.RemoveCachedNode(localMid);

                        retainedRecordCount += (mergedLocal != null && mergedLocal.Records != null) ? mergedLocal.Records.Count : inRecs;
                        mergedSelfNode = true;
                        mergedNodeCount++;
                        singleNodeDisplayName = !string.IsNullOrWhiteSpace(mergedLocal.MachineName) ? mergedLocal.MachineName : localMid;
                    }
                    else
                    {
                        incoming.IsLocal = false;

                        // 远端/其他离线设备节点：读取本地 NodesCacheDir 现有快照，高水位无损合并后落盘
                        string remoteCachePath = Path.Combine(SyncService.NodesCacheDir, "toklens_node_" + mid + ".json");
                        NodeSnapshot existingRemote = null;
                        try
                        {
                            if (File.Exists(remoteCachePath))
                            {
                                string remRaw = File.ReadAllText(remoteCachePath, Encoding.UTF8);
                                existingRemote = SyncService.DeserializeSnapshot(remRaw, false);
                            }
                        }
                        catch { }

                        NodeSnapshot mergedRemote = SyncService.MergeNodeSnapshotsHighWater(incoming, existingRemote);
                        mergedRemote = SyncService.SaveRemoteNodeCache(mergedRemote);

                        retainedRecordCount += (mergedRemote != null && mergedRemote.Records != null) ? mergedRemote.Records.Count : inRecs;
                        mergedRemoteCount++;
                        mergedNodeCount++;
                        singleNodeDisplayName = !string.IsNullOrWhiteSpace(mergedRemote.MachineName) ? mergedRemote.MachineName : mid;
                    }
                }

                StringBuilder sb = new StringBuilder();
                if (mergedNodeCount == 1)
                {
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "已通过高水位算法无损合并 1 台设备「{0}」（导入 {1} 条记录，合并后保留 {2} 条），未覆盖任何更高历史数据。",
                        singleNodeDisplayName,
                        importedRecordCount,
                        retainedRecordCount);
                }
                else
                {
                    string scopeNote = mergedSelfNode
                        ? string.Format(CultureInfo.InvariantCulture, "含本机及 {0} 台外部/远端节点", mergedRemoteCount)
                        : string.Format(CultureInfo.InvariantCulture, "共 {0} 台外部/远端节点", mergedRemoteCount);
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "已通过高水位算法无损合并 {0} 台设备（{1}，导入 {2} 条记录，合并后保留 {3} 条），未覆盖任何更高历史数据。",
                        mergedNodeCount,
                        scopeNote,
                        importedRecordCount,
                        retainedRecordCount);
                }

                if (customPricesMerged > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "同时同步了 {0} 项自定义模型单价。", customPricesMerged);
                }

                summaryMessage = sb.ToString();
                return mergedNodeCount;
            }
        }
    }
}
