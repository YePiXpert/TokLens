using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

    public sealed class GhCliAccountInfo
    {
        public string Login = "";
        public bool IsActive;
        public string Source = "gh CLI";
    }

    public sealed class GitHubGistCandidate
    {
        public string GistId = "";
        public string Description = "";
        public string UpdatedAt = "";
        public bool IsPublic;
        public List<string> NodeIds = new List<string>();
        public bool HasAgentScript;
    }

    public sealed class GitHubProbeResult
    {
        public bool TokenValid;
        public string Login = "";
        public string TokenPrefixMask = "";
        public string TokenKindLabel = "";
        public bool IsDeviceOAuthToken;
        public bool HasGistScope;
        public string ScopesRaw = "";
        public bool CurrentGistOwned;
        public List<GitHubGistCandidate> Candidates = new List<GitHubGistCandidate>();
        public GitHubGistCandidate BestCandidate;
        public string ErrorMessage = "";
    }

    public static class SyncService
    {
        public const string SchemaVersion = "toklens.node.v2";

        private const string DpapiPrefix = "dpapi:";
        private const string WebDavPropfindBody = "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:resourcetype/></D:prop></D:propfind>";
        private const string VpsBootstrapPython =
            "import sys,json,base64,urllib.request,pathlib;" +
            "s=sys.argv[1];" +
            "s=s[9:] if s.lower().startswith(\"toklens1:\") else s;" +
            "c=json.loads(base64.b64decode(s.encode(\"ascii\")).decode(\"utf-8\"));" +
            "dav=str(c.get(\"provider\",\"\")).lower()==\"webdav\";" +
            "ua={\"User-Agent\":\"TokLens-Bootstrap/2.2\"};" +
            "ah={\"Authorization\":\"Basic \"+base64.b64encode((c.get(\"webdav_user\",\"\")+\":\"+c.get(\"webdav_pass\",\"\")).encode(\"utf-8\")).decode(\"ascii\"),\"User-Agent\":\"TokLens-Bootstrap/2.2\"} if dav else {\"Authorization\":\"Bearer \"+c[\"gist_token\"].strip(),\"Accept\":\"application/vnd.github+json\",\"User-Agent\":\"TokLens-Bootstrap/2.2\"};" +
            "url=(c[\"webdav_url\"].strip().rstrip(\"/\")+\"/toklens_agent.py\") if dav else (\"https://api.github.com/gists/\"+c[\"gist_id\"].strip());" +
            "raw=urllib.request.urlopen(urllib.request.Request(url,headers=ah),timeout=15).read().decode(\"utf-8\");" +
            "f=(None if dav else json.loads(raw)[\"files\"][\"toklens_agent.py\"]);" +
            "txt=raw if dav else (f[\"content\"] if (not f.get(\"truncated\") and f.get(\"content\")) else urllib.request.urlopen(urllib.request.Request(f[\"raw_url\"],headers=ua),timeout=15).read().decode(\"utf-8\"));" +
            "p=pathlib.Path(\"~/.toklens/toklens_agent.py\").expanduser();" +
            "p.parent.mkdir(parents=True,exist_ok=True);" +
            "p.write_text(txt,encoding=\"utf-8\")";

        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("TokLens.LocalCredential.v2");
        private static readonly object SyncGate = new object();
        private static readonly object FileIoGate = new object();
        private static readonly HashSet<string> PendingObsoleteMachineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex SafeIdRegex = new Regex(@"[^a-zA-Z0-9_-]+", RegexOptions.Compiled);
        private static readonly Regex CleanGistIdRegex = new Regex(@"^[a-zA-Z0-9]{6,64}$", RegexOptions.Compiled);
        private static readonly Regex HexGistIdSegRegex = new Regex(@"^[a-fA-F0-9]{16,64}$", RegexOptions.Compiled);
        private static readonly Regex GistNodeFileRegex = new Regex(@"^toklens_node_([a-zA-Z0-9_-]+)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WebDavNodeRegex = new Regex(@"(toklens_node_[a-zA-Z0-9_-]+\.json)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AnsiEscapeRegex = new Regex(@"\x1B\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);
        private static readonly Regex GhModernAccountRegex = new Regex(@"Logged in to github\.com account\s+([A-Za-z0-9_.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GhLegacyAccountRegex = new Regex(@"Logged in to github\.com as\s+([A-Za-z0-9_.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GhActiveLineRegex = new Regex(@"Active account:\s*(true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly ConditionalWeakTable<NodeSnapshot, object> NonDefaultHomeSnapshots = new ConditionalWeakTable<NodeSnapshot, object>();

        public static string AppConfigDir
        {
            get
            {
                try
                {
                    string customDir = Environment.GetEnvironmentVariable("TOKLENS_CONFIG_DIR");
                    if (!string.IsNullOrWhiteSpace(customDir))
                        return customDir.Trim();
                }
                catch { }
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

        private static void AtomicWriteAllText(string filePath, string content)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            lock (FileIoGate)
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string tmpPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(tmpPath, content ?? "", new UTF8Encoding(false));
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Replace(tmpPath, filePath, null, true);
                            return;
                        }
                        catch
                        {
                            try { File.Delete(filePath); } catch { }
                        }
                    }
                    File.Move(tmpPath, filePath);
                }
                finally
                {
                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
            }
        }

        private static string ProtectSecret(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plainText);
                byte[] enc = ProtectedData.Protect(raw, DpapiEntropy, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Windows DPAPI 凭据加密失败，已阻止明文落盘保存同步凭据：" + ex.Message, ex);
            }
        }

        private static string UnprotectSecret(string storedValue)
        {
            if (string.IsNullOrEmpty(storedValue)) return "";
            string trimmed = storedValue.Trim();
            if (!trimmed.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                return storedValue;

            string b64 = trimmed.Substring(DpapiPrefix.Length).Trim();
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] enc = Convert.FromBase64String(b64);
                byte[] raw = ProtectedData.Unprotect(enc, DpapiEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                try
                {
                    byte[] encFallback = Convert.FromBase64String(b64);
                    byte[] rawFallback = ProtectedData.Unprotect(encFallback, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(rawFallback);
                }
                catch
                {
                    return "";
                }
            }
        }

        public static string SanitizeMachineId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "win-pc";
            string s = SafeIdRegex.Replace(raw.Trim().ToLowerInvariant(), "-").Trim('-');
            if (s.Length > 48) s = s.Substring(0, 48).Trim('-');
            return string.IsNullOrEmpty(s) ? "win-pc" : s;
        }

        public static string NormalizeGistId(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return "";
            string s = rawInput.Trim();

            int qIdx = s.IndexOf('?');
            if (qIdx >= 0) s = s.Substring(0, qIdx);
            int hIdx = s.IndexOf('#');
            if (hIdx >= 0) s = s.Substring(0, hIdx);
            s = s.Trim().TrimEnd('/');

            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4).TrimEnd('/');

            if (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0)
            {
                string[] parts = s.Replace('\\', '/').Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    string seg = parts[i].Trim();
                    if (HexGistIdSegRegex.IsMatch(seg))
                        return seg;
                }
                if (parts.Length > 0)
                {
                    string last = parts[parts.Length - 1].Trim();
                    if (CleanGistIdRegex.IsMatch(last) &&
                        !string.Equals(last, "gists", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(last, "gist.github.com", StringComparison.OrdinalIgnoreCase))
                    {
                        return last;
                    }
                }
                return "";
            }

            if (CleanGistIdRegex.IsMatch(s))
                return s;

            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length && sb.Length < 64; i++)
            {
                char ch = s[i];
                if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string MaskTokenFingerprint(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            string t = token.Trim();
            string prefix = "";
            if (t.StartsWith("github_pat_", StringComparison.Ordinal))
                prefix = "github_pat_";
            else if (t.StartsWith("gho_", StringComparison.Ordinal) ||
                     t.StartsWith("ghp_", StringComparison.Ordinal) ||
                     t.StartsWith("ghu_", StringComparison.Ordinal) ||
                     t.StartsWith("ghs_", StringComparison.Ordinal) ||
                     t.StartsWith("ghr_", StringComparison.Ordinal))
                prefix = t.Substring(0, 4);
            else if (t.Length >= 12)
                prefix = t.Substring(0, 4);

            if (t.Length <= prefix.Length + 4)
            {
                string tailShort = t.Length >= 2 ? t.Substring(t.Length - 2) : "";
                return prefix + "••••" + tailShort;
            }
            return prefix + "••••" + t.Substring(t.Length - 4);
        }

        public static string DescribeTokenKind(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "未配置令牌";
            string t = token.Trim();
            if (t.StartsWith("gho_", StringComparison.Ordinal))
                return "GitHub CLI 设备独立 OAuth 令牌 (gho_，每台电脑独立签发，同账号自动互通)";
            if (t.StartsWith("ghp_", StringComparison.Ordinal))
                return "经典个人访问令牌 (Classic PAT, ghp_)";
            if (t.StartsWith("github_pat_", StringComparison.Ordinal))
                return "细粒度个人访问令牌 (Fine-grained PAT, github_pat_)";
            if (t.StartsWith("ghu_", StringComparison.Ordinal))
                return "GitHub App 用户授权令牌 (ghu_)";
            if (t.StartsWith("ghs_", StringComparison.Ordinal))
                return "GitHub 服务端集成令牌 (ghs_)";
            return "自定义访问令牌";
        }

        public static string PosixSingleQuote(string value)
        {
            if (value == null) return "''";
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        public static SyncConfig LoadConfig()
        {
            SyncConfig cfg = new SyncConfig();
            string defaultHost = "Windows-PC";
            try { defaultHost = Environment.MachineName; } catch { }
            cfg.MachineId = SanitizeMachineId("win-" + defaultHost);
            cfg.MachineName = defaultHost;
            cfg.IsDarkTheme = true;
            cfg.PreferCny = true;
            cfg.MinimizeToTray = false;
            cfg.DailyBudgetUsd = 0.0;
            if (cfg.NodeAliases == null)
                cfg.NodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string rawJson;
                    lock (FileIoGate)
                    {
                        rawJson = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
                    }
                    JavaScriptSerializer js = NewJson();
                    Dictionary<string, object> d = js.DeserializeObject(rawJson) as Dictionary<string, object>;
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
                            cfg.Provider = Convert.ToString(d["provider"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("auto_sync") && d["auto_sync"] != null)
                            cfg.AutoSync = Convert.ToBoolean(d["auto_sync"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("scan_local_wsl") && d["scan_local_wsl"] != null)
                            cfg.ScanLocalWsl = Convert.ToBoolean(d["scan_local_wsl"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("gist_id") && d["gist_id"] != null)
                            cfg.GistId = NormalizeGistId(Convert.ToString(d["gist_id"], CultureInfo.InvariantCulture));
                        if (d.ContainsKey("gist_token") && d["gist_token"] != null)
                            cfg.GistToken = UnprotectSecret(Convert.ToString(d["gist_token"], CultureInfo.InvariantCulture)).Trim();
                        if (d.ContainsKey("webdav_url") && d["webdav_url"] != null)
                            cfg.WebDavUrl = Convert.ToString(d["webdav_url"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("webdav_user") && d["webdav_user"] != null)
                            cfg.WebDavUser = Convert.ToString(d["webdav_user"], CultureInfo.InvariantCulture).Trim();
                        if (d.ContainsKey("webdav_pass") && d["webdav_pass"] != null)
                            cfg.WebDavPass = UnprotectSecret(Convert.ToString(d["webdav_pass"], CultureInfo.InvariantCulture));

                        if (d.ContainsKey("is_dark_theme") && d["is_dark_theme"] != null)
                            cfg.IsDarkTheme = Convert.ToBoolean(d["is_dark_theme"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("prefer_cny") && d["prefer_cny"] != null)
                            cfg.PreferCny = Convert.ToBoolean(d["prefer_cny"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("minimize_to_tray") && d["minimize_to_tray"] != null)
                            cfg.MinimizeToTray = Convert.ToBoolean(d["minimize_to_tray"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("daily_budget_usd") && d["daily_budget_usd"] != null)
                        {
                            double budget = Convert.ToDouble(d["daily_budget_usd"], CultureInfo.InvariantCulture);
                            if (!double.IsNaN(budget) && !double.IsInfinity(budget) && budget >= 0.0)
                                cfg.DailyBudgetUsd = budget;
                        }
                        if (d.ContainsKey("node_aliases") && d["node_aliases"] is IDictionary)
                        {
                            IDictionary rawAliases = (IDictionary)d["node_aliases"];
                            foreach (DictionaryEntry entry in rawAliases)
                            {
                                if (entry.Key == null || entry.Value == null) continue;
                                string ak = Convert.ToString(entry.Key, CultureInfo.InvariantCulture).Trim();
                                string av = Convert.ToString(entry.Value, CultureInfo.InvariantCulture).Trim();
                                if (!string.IsNullOrEmpty(ak) && !string.IsNullOrEmpty(av))
                                    cfg.NodeAliases[ak] = av;
                            }
                        }
                        if (d.ContainsKey("subscriptions_customized") && d["subscriptions_customized"] != null)
                            cfg.SubscriptionsCustomized = Convert.ToBoolean(d["subscriptions_customized"], CultureInfo.InvariantCulture);
                        if (d.ContainsKey("subscriptions") && d["subscriptions"] is IList)
                        {
                            IList rawSubs = (IList)d["subscriptions"];
                            for (int si = 0; si < rawSubs.Count; si++)
                            {
                                Dictionary<string, object> subMap = rawSubs[si] as Dictionary<string, object>;
                                if (subMap == null) continue;
                                SubscriptionPlanConfig sp = new SubscriptionPlanConfig();
                                sp.EcoId = subMap.ContainsKey("eco_id") && subMap["eco_id"] != null
                                    ? Convert.ToString(subMap["eco_id"], CultureInfo.InvariantCulture).Trim()
                                    : "";
                                sp.Enabled = subMap.ContainsKey("enabled") && subMap["enabled"] != null &&
                                    Convert.ToBoolean(subMap["enabled"], CultureInfo.InvariantCulture);
                                sp.PlanName = subMap.ContainsKey("plan_name") && subMap["plan_name"] != null
                                    ? Convert.ToString(subMap["plan_name"], CultureInfo.InvariantCulture).Trim()
                                    : "";
                                if (subMap.ContainsKey("monthly_usd") && subMap["monthly_usd"] != null)
                                {
                                    double musd = Convert.ToDouble(subMap["monthly_usd"], CultureInfo.InvariantCulture);
                                    if (!double.IsNaN(musd) && !double.IsInfinity(musd) && musd >= 0.0)
                                        sp.MonthlyUsd = musd;
                                }
                                if (!string.IsNullOrEmpty(sp.EcoId))
                                    cfg.Subscriptions.Add(sp);
                            }
                        }
                    }
                }
            }
            catch { }

            return cfg;
        }

        public static void SaveConfig(SyncConfig cfg)
        {
            if (cfg == null) return;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["machine_id"] = SanitizeMachineId(cfg.MachineId);
            d["machine_name"] = string.IsNullOrWhiteSpace(cfg.MachineName) ? Environment.MachineName : cfg.MachineName.Trim();
            d["provider"] = string.IsNullOrWhiteSpace(cfg.Provider) ? "gist" : cfg.Provider.Trim();
            d["auto_sync"] = cfg.AutoSync;
            d["scan_local_wsl"] = cfg.ScanLocalWsl;
            d["gist_id"] = NormalizeGistId(cfg.GistId);
            d["gist_token"] = ProtectSecret((cfg.GistToken ?? "").Trim());
            d["webdav_url"] = (cfg.WebDavUrl ?? "").Trim();
            d["webdav_user"] = (cfg.WebDavUser ?? "").Trim();
            d["webdav_pass"] = ProtectSecret(cfg.WebDavPass ?? "");

            d["is_dark_theme"] = cfg.IsDarkTheme;
            d["prefer_cny"] = cfg.PreferCny;
            d["minimize_to_tray"] = cfg.MinimizeToTray;
            double cleanBudget = (double.IsNaN(cfg.DailyBudgetUsd) || double.IsInfinity(cfg.DailyBudgetUsd) || cfg.DailyBudgetUsd < 0.0)
                ? 0.0
                : Math.Round(cfg.DailyBudgetUsd, 4);
            d["daily_budget_usd"] = cleanBudget;

            Dictionary<string, string> cleanAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cfg.NodeAliases != null)
            {
                foreach (KeyValuePair<string, string> kv in cfg.NodeAliases)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        cleanAliases[kv.Key.Trim()] = kv.Value.Trim();
                }
            }
            d["node_aliases"] = cleanAliases;
            d["subscriptions_customized"] = cfg.SubscriptionsCustomized;
            List<Dictionary<string, object>> subList = new List<Dictionary<string, object>>();
            if (cfg.Subscriptions != null)
            {
                for (int si = 0; si < cfg.Subscriptions.Count; si++)
                {
                    SubscriptionPlanConfig sp = cfg.Subscriptions[si];
                    if (sp == null || string.IsNullOrWhiteSpace(sp.EcoId)) continue;
                    Dictionary<string, object> sm = new Dictionary<string, object>();
                    sm["eco_id"] = sp.EcoId.Trim();
                    sm["enabled"] = sp.Enabled;
                    sm["plan_name"] = sp.PlanName ?? "";
                    sm["monthly_usd"] = (double.IsNaN(sp.MonthlyUsd) || double.IsInfinity(sp.MonthlyUsd) || sp.MonthlyUsd < 0.0)
                        ? 0.0
                        : Math.Round(sp.MonthlyUsd, 4);
                    subList.Add(sm);
                }
            }
            d["subscriptions"] = subList;

            JavaScriptSerializer js = NewJson();
            AtomicWriteAllText(ConfigFilePath, js.Serialize(d));
        }

        public static void MigrateLocalMachineId(string oldMachineId, string newMachineId)
        {
            if (string.IsNullOrWhiteSpace(oldMachineId) || string.IsNullOrWhiteSpace(newMachineId))
                return;
            string oldId = SanitizeMachineId(oldMachineId);
            string newId = SanitizeMachineId(newMachineId);
            if (string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                string oldHwPath = GetLocalHighWaterFilePath(oldId);
                string newHwPath = GetLocalHighWaterFilePath(newId);
                if (File.Exists(oldHwPath))
                {
                    string oldJson;
                    lock (FileIoGate)
                    {
                        oldJson = File.ReadAllText(oldHwPath, Encoding.UTF8);
                    }
                    NodeSnapshot oldSnap = DeserializeSnapshot(oldJson, true);
                    if (oldSnap != null)
                    {
                        oldSnap.MachineId = newId;
                        NodeSnapshot merged = oldSnap;
                        if (File.Exists(newHwPath))
                        {
                            string newJson;
                            lock (FileIoGate)
                            {
                                newJson = File.ReadAllText(newHwPath, Encoding.UTF8);
                            }
                            NodeSnapshot existingNew = DeserializeSnapshot(newJson, true);
                            if (existingNew != null)
                            {
                                existingNew.MachineId = newId;
                                merged = MergeNodeSnapshotsHighWater(existingNew, oldSnap);
                            }
                        }
                        for (int i = 0; i < merged.Records.Count; i++)
                        {
                            if (merged.Records[i] != null)
                                merged.Records[i].MachineId = newId;
                        }
                        AtomicWriteAllText(newHwPath, SerializeSnapshot(merged));
                    }
                    lock (FileIoGate)
                    {
                        try { File.Delete(oldHwPath); } catch { }
                    }
                }
            }
            catch { }

            RemoveCachedNode(oldId);
            RemoveCachedNode(newId);

            lock (SyncGate)
            {
                PendingObsoleteMachineIds.Add(oldId);
                PendingObsoleteMachineIds.Remove(newId);
            }
        }

        public static string GeneratePairingCode(SyncConfig cfg)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["provider"] = (cfg != null && !string.IsNullOrWhiteSpace(cfg.Provider)) ? cfg.Provider.Trim() : "gist";
            p["gist_id"] = cfg != null ? NormalizeGistId(cfg.GistId) : "";
            p["gist_token"] = cfg != null ? (cfg.GistToken ?? "").Trim() : "";
            p["webdav_url"] = cfg != null ? (cfg.WebDavUrl ?? "").Trim() : "";
            p["webdav_user"] = cfg != null ? (cfg.WebDavUser ?? "").Trim() : "";
            p["webdav_pass"] = cfg != null ? (cfg.WebDavPass ?? "") : "";

            int tzOffsetMin = 480;
            try
            {
                tzOffsetMin = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);
            }
            catch { }
            p["tz_offset_min"] = tzOffsetMin;

            JavaScriptSerializer js = NewJson();
            string raw = js.Serialize(p);
            return "toklens1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryApplyPairingCode(SyncConfig targetCfg, string code, out string error)
        {
            error = null;
            if (targetCfg == null)
            {
                error = "配置目标为空";
                return false;
            }
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
                    targetCfg.Provider = Convert.ToString(d["provider"], CultureInfo.InvariantCulture).Trim();
                if (d.ContainsKey("gist_id") && d["gist_id"] != null)
                    targetCfg.GistId = NormalizeGistId(Convert.ToString(d["gist_id"], CultureInfo.InvariantCulture));
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

        private static string ResolveGhCliExecutable()
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] dirs = pathEnv.Split(new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string rawDir = dirs[i].Trim().Trim('"');
                    if (string.IsNullOrEmpty(rawDir)) continue;
                    try
                    {
                        string cand = Path.Combine(rawDir, "gh.exe");
                        if (File.Exists(cand)) return cand;
                    }
                    catch { }
                }
            }
            catch { }

            List<string> fallbacks = new List<string>();
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf))
                    fallbacks.Add(Path.Combine(Path.Combine(pf, "GitHub CLI"), "gh.exe"));
            }
            catch { }
            try
            {
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86))
                    fallbacks.Add(Path.Combine(Path.Combine(pf86, "GitHub CLI"), "gh.exe"));
            }
            catch { }
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localApp))
                {
                    fallbacks.Add(Path.Combine(Path.Combine(Path.Combine(localApp, "Programs"), "GitHub CLI"), "gh.exe"));
                    fallbacks.Add(Path.Combine(Path.Combine(Path.Combine(localApp, "Microsoft"), "WinGet"), @"Links\gh.exe"));
                }
            }
            catch { }
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                    fallbacks.Add(Path.Combine(Path.Combine(Path.Combine(home, "scoop"), "shims"), "gh.exe"));
            }
            catch { }

            for (int i = 0; i < fallbacks.Count; i++)
            {
                try
                {
                    if (File.Exists(fallbacks[i])) return fallbacks[i];
                }
                catch { }
            }
            return "gh";
        }

        private static bool RunGhProcess(string arguments, bool stripEnvTokens, int timeoutMs, out int exitCode, out string stdout, out string stderr)
        {
            exitCode = -1;
            stdout = "";
            stderr = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ResolveGhCliExecutable(), arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                try
                {
                    psi.EnvironmentVariables["NO_COLOR"] = "1";
                    psi.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
                    if (stripEnvTokens)
                    {
                        psi.EnvironmentVariables.Remove("GH_TOKEN");
                        psi.EnvironmentVariables.Remove("GITHUB_TOKEN");
                        psi.EnvironmentVariables.Remove("GH_ENTERPRISE_TOKEN");
                        psi.EnvironmentVariables.Remove("GH_HOST");
                    }
                }
                catch { }

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = p.StandardError.ReadToEndAsync();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    try { Task.WaitAll(new Task[] { outTask, errTask }, 1000); } catch { }
                    exitCode = p.ExitCode;
                    stdout = outTask.Status == TaskStatus.RanToCompletion ? (outTask.Result ?? "") : "";
                    stderr = errTask.Status == TaskStatus.RanToCompletion ? (errTask.Result ?? "") : "";
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool DetectGitHubTokenEnvOverride(out string envVarName, out string envTokenValue)
        {
            envVarName = "";
            envTokenValue = "";
            string[] names = new string[] { "GH_TOKEN", "GITHUB_TOKEN" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    string v = Environment.GetEnvironmentVariable(names[i]);
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        envVarName = names[i];
                        envTokenValue = v.Trim();
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public static List<GhCliAccountInfo> DiscoverGitHubCliAccounts(out bool hasEnvOverride, out string envVarName)
        {
            string envTok;
            hasEnvOverride = DetectGitHubTokenEnvOverride(out envVarName, out envTok);

            List<GhCliAccountInfo> accounts = new List<GhCliAccountInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int code;
            string stdout;
            string stderr;
            if (RunGhProcess("auth status -h github.com", true, 4000, out code, out stdout, out stderr))
            {
                string combined = AnsiEscapeRegex.Replace((stdout ?? "") + "\n" + (stderr ?? ""), "");
                string[] lines = combined.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                GhCliAccountInfo current = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    Match mModern = GhModernAccountRegex.Match(ln);
                    if (mModern.Success)
                    {
                        string login = mModern.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(login) && seen.Add(login))
                        {
                            current = new GhCliAccountInfo();
                            current.Login = login;
                            current.IsActive = false;
                            current.Source = "gh CLI";
                            accounts.Add(current);
                        }
                        continue;
                    }

                    Match mLegacy = GhLegacyAccountRegex.Match(ln);
                    if (mLegacy.Success)
                    {
                        string login = mLegacy.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(login) && seen.Add(login))
                        {
                            current = new GhCliAccountInfo();
                            current.Login = login;
                            current.IsActive = true;
                            current.Source = "gh CLI";
                            accounts.Add(current);
                        }
                        continue;
                    }

                    if (current != null)
                    {
                        Match mActive = GhActiveLineRegex.Match(ln);
                        if (mActive.Success)
                        {
                            current.IsActive = string.Equals(mActive.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }

            if (accounts.Count == 1 && !accounts[0].IsActive)
                accounts[0].IsActive = true;

            accounts.Sort(delegate(GhCliAccountInfo a, GhCliAccountInfo b)
            {
                if (a.IsActive != b.IsActive) return a.IsActive ? -1 : 1;
                return string.Compare(a.Login, b.Login, StringComparison.OrdinalIgnoreCase);
            });

            return accounts;
        }

        private static bool IsPlausibleTokenString(string raw, out string cleaned)
        {
            cleaned = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string t = raw.Trim();
            if (t.Length >= 16 && t.IndexOf(' ') < 0 && t.IndexOf('\n') < 0 && t.IndexOf('\r') < 0 && t.IndexOf('\t') < 0)
            {
                cleaned = t;
                return true;
            }
            return false;
        }

        private static string SanitizeGhUserArg(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return "";
            StringBuilder sb = new StringBuilder(username.Length);
            for (int i = 0; i < username.Length; i++)
            {
                char ch = username[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.')
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        public static bool TryReadGitHubCliTokenForUser(string username, bool ignoreEnvOverride, out string token)
        {
            token = null;
            string cleanUser = SanitizeGhUserArg(username);

            if (!ignoreEnvOverride && string.IsNullOrEmpty(cleanUser))
            {
                string envName;
                string envVal;
                if (DetectGitHubTokenEnvOverride(out envName, out envVal) && IsPlausibleTokenString(envVal, out token))
                    return true;
            }

            int exitCode;
            string stdout;
            string stderr;
            if (!string.IsNullOrEmpty(cleanUser))
            {
                string userArgs = "auth token -h github.com -u " + cleanUser;
                if (RunGhProcess(userArgs, ignoreEnvOverride, 3500, out exitCode, out stdout, out stderr) &&
                    exitCode == 0 && IsPlausibleTokenString(stdout, out token))
                {
                    return true;
                }

                // 兼容不支持 -u 参数的旧版 gh CLI（仅当该账号确为本机唯一已登录账号时回退）
                bool dummyEnv;
                string dummyVar;
                List<GhCliAccountInfo> allAcc = DiscoverGitHubCliAccounts(out dummyEnv, out dummyVar);
                if (allAcc.Count == 1 && string.Equals(allAcc[0].Login, cleanUser, StringComparison.OrdinalIgnoreCase))
                {
                    if (RunGhProcess("auth token -h github.com", ignoreEnvOverride, 3500, out exitCode, out stdout, out stderr) &&
                        exitCode == 0 && IsPlausibleTokenString(stdout, out token))
                    {
                        return true;
                    }
                }
                return false;
            }

            if (RunGhProcess("auth token -h github.com", ignoreEnvOverride, 3500, out exitCode, out stdout, out stderr) &&
                exitCode == 0 && IsPlausibleTokenString(stdout, out token))
            {
                return true;
            }

            if (RunGhProcess("auth token", ignoreEnvOverride, 3500, out exitCode, out stdout, out stderr) &&
                exitCode == 0 && IsPlausibleTokenString(stdout, out token))
            {
                return true;
            }

            return false;
        }

        public static bool TryReadGitHubCliToken(out string token)
        {
            // 优先读取 gh CLI 真实登录的存储令牌（避免被其他项目的受限 GH_TOKEN 环境变量静默劫持）；若未登录 gh 再回退环境变量
            if (TryReadGitHubCliTokenForUser(null, true, out token))
                return true;
            return TryReadGitHubCliTokenForUser(null, false, out token);
        }

        private static int CompareGistCandidates(GitHubGistCandidate a, GitHubGistCandidate b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            if (a.IsPublic != b.IsPublic)
                return a.IsPublic ? 1 : -1;

            int nodeCmp = b.NodeIds.Count.CompareTo(a.NodeIds.Count);
            if (nodeCmp != 0) return nodeCmp;

            if (a.HasAgentScript != b.HasAgentScript)
                return b.HasAgentScript ? 1 : -1;

            return string.CompareOrdinal(b.UpdatedAt ?? "", a.UpdatedAt ?? "");
        }

        private static bool HasScopeToken(string scopesHeader, string targetScope)
        {
            if (string.IsNullOrWhiteSpace(scopesHeader) || string.IsNullOrWhiteSpace(targetScope))
                return false;
            string[] parts = scopesHeader.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string sc = parts[i].Trim();
                if (string.Equals(sc, targetScope, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<GitHubGistCandidate> ScanTokLensGistCandidatesStrict(string token, int maxPages)
        {
            EnsureTls();
            string tok = (token ?? "").Trim();
            if (string.IsNullOrEmpty(tok))
                throw new InvalidOperationException("请先填写 GitHub 访问令牌。");

            JavaScriptSerializer js = NewJson();
            List<GitHubGistCandidate> candidates = new List<GitHubGistCandidate>();
            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int pagesToScan = Math.Max(1, Math.Min(5, maxPages));

            for (int page = 1; page <= pagesToScan; page++)
            {
                WebHeaderCollection headers;
                int statusCode;
                string url = string.Format(CultureInfo.InvariantCulture, "https://api.github.com/gists?per_page=100&page={0}", page);
                string respBody = SendGitHubApi("GET", url, tok, null, out headers, out statusCode);

                if (page == 1 && headers != null && headers["X-OAuth-Scopes"] != null)
                {
                    string scopes = headers["X-OAuth-Scopes"];
                    if (!HasScopeToken(scopes, "gist"))
                        throw new InvalidOperationException("当前 GitHub 令牌缺少 gist 权限（请运行 gh auth refresh -h github.com -s gist 或使用勾选了 gist 的 Token）。");
                }

                IList list = js.DeserializeObject(respBody) as IList;
                if (list == null || list.Count == 0)
                    break;

                for (int i = 0; i < list.Count; i++)
                {
                    Dictionary<string, object> g = list[i] as Dictionary<string, object>;
                    if (g == null || !g.ContainsKey("id") || g["id"] == null) continue;
                    string gid = NormalizeGistId(Convert.ToString(g["id"], CultureInfo.InvariantCulture));
                    if (string.IsNullOrEmpty(gid) || !seenIds.Add(gid)) continue;

                    string desc = g.ContainsKey("description") && g["description"] != null
                        ? Convert.ToString(g["description"], CultureInfo.InvariantCulture)
                        : "";
                    string updatedAt = g.ContainsKey("updated_at") && g["updated_at"] != null
                        ? Convert.ToString(g["updated_at"], CultureInfo.InvariantCulture)
                        : "";
                    bool isPublic = g.ContainsKey("public") && g["public"] != null &&
                                    Convert.ToBoolean(g["public"], CultureInfo.InvariantCulture);

                    List<string> nodeIds = new List<string>();
                    bool hasAgent = false;
                    if (g.ContainsKey("files") && g["files"] is Dictionary<string, object>)
                    {
                        Dictionary<string, object> files = (Dictionary<string, object>)g["files"];
                        foreach (string fn in files.Keys)
                        {
                            if (string.IsNullOrEmpty(fn)) continue;
                            Match mNode = GistNodeFileRegex.Match(fn);
                            if (mNode.Success)
                            {
                                nodeIds.Add(SanitizeMachineId(mNode.Groups[1].Value));
                            }
                            else if (string.Equals(fn, "toklens_agent.py", StringComparison.OrdinalIgnoreCase))
                            {
                                hasAgent = true;
                            }
                        }
                    }

                    bool descMatch = !string.IsNullOrEmpty(desc) && desc.IndexOf("TokLens", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (nodeIds.Count > 0 || hasAgent || descMatch)
                    {
                        nodeIds.Sort(StringComparer.OrdinalIgnoreCase);
                        GitHubGistCandidate cand = new GitHubGistCandidate();
                        cand.GistId = gid;
                        cand.Description = desc;
                        cand.UpdatedAt = updatedAt;
                        cand.IsPublic = isPublic;
                        cand.NodeIds = nodeIds;
                        cand.HasAgentScript = hasAgent;
                        candidates.Add(cand);
                    }
                }

                if (list.Count < 100)
                    break;
            }

            candidates.Sort(CompareGistCandidates);
            return candidates;
        }

        public static GitHubProbeResult VerifyGitHubTokenAndProbeGists(string token, string currentGistId)
        {
            EnsureTls();
            GitHubProbeResult res = new GitHubProbeResult();
            string tok = (token ?? "").Trim();
            res.TokenPrefixMask = MaskTokenFingerprint(tok);
            res.TokenKindLabel = DescribeTokenKind(tok);
            res.IsDeviceOAuthToken = tok.StartsWith("gho_", StringComparison.Ordinal);

            if (string.IsNullOrEmpty(tok))
            {
                res.ErrorMessage = "尚未填写 GitHub 访问令牌";
                return res;
            }

            JavaScriptSerializer js = NewJson();
            try
            {
                WebHeaderCollection userHeaders;
                int userStatus;
                string userBody = SendGitHubApi("GET", "https://api.github.com/user", tok, null, out userHeaders, out userStatus);
                Dictionary<string, object> userObj = js.DeserializeObject(userBody) as Dictionary<string, object>;
                if (userObj != null && userObj.ContainsKey("login") && userObj["login"] != null)
                    res.Login = Convert.ToString(userObj["login"], CultureInfo.InvariantCulture).Trim();

                if (string.IsNullOrEmpty(res.Login))
                {
                    res.ErrorMessage = "无法解析 GitHub 账号信息";
                    return res;
                }
                res.TokenValid = true;

                bool hasOAuthScopesHeader = userHeaders != null && userHeaders["X-OAuth-Scopes"] != null;
                if (hasOAuthScopesHeader)
                {
                    res.ScopesRaw = userHeaders["X-OAuth-Scopes"].Trim();
                    res.HasGistScope = HasScopeToken(res.ScopesRaw, "gist");
                    if (!res.HasGistScope)
                    {
                        res.ErrorMessage = string.Format(
                            CultureInfo.InvariantCulture,
                            "已识别账号 @{0}，但令牌缺少 gist 权限（当前权限：{1}）。可执行 gh auth refresh -h github.com -s gist 一键补齐。",
                            res.Login,
                            string.IsNullOrEmpty(res.ScopesRaw) ? "无" : res.ScopesRaw);
                        return res;
                    }
                }
                else
                {
                    res.ScopesRaw = "fine-grained";
                }

                try
                {
                    List<GitHubGistCandidate> cands = ScanTokLensGistCandidatesStrict(tok, 5);
                    res.HasGistScope = true;
                    res.Candidates = cands;
                    for (int i = 0; i < cands.Count; i++)
                    {
                        if (!cands[i].IsPublic)
                        {
                            res.BestCandidate = cands[i];
                            break;
                        }
                    }
                }
                catch (Exception gEx)
                {
                    if (!hasOAuthScopesHeader)
                    {
                        res.HasGistScope = false;
                        res.ErrorMessage = string.Format(
                            CultureInfo.InvariantCulture,
                            "已识别账号 @{0}，但该细粒度令牌未开启 Gists 读写权限（{1}）。建议改用 gh CLI 登录或勾选了 gist 的经典 PAT。",
                            res.Login,
                            gEx.Message);
                        return res;
                    }
                    res.ErrorMessage = "检索已有 Gist 同步库失败：" + gEx.Message;
                    return res;
                }

                string normCurId = NormalizeGistId(currentGistId);
                if (!string.IsNullOrEmpty(normCurId))
                {
                    for (int i = 0; i < res.Candidates.Count; i++)
                    {
                        if (string.Equals(res.Candidates[i].GistId, normCurId, StringComparison.OrdinalIgnoreCase))
                        {
                            res.CurrentGistOwned = true;
                            break;
                        }
                    }

                    if (!res.CurrentGistOwned)
                    {
                        try
                        {
                            WebHeaderCollection gHeaders;
                            int gCode;
                            string gBody = SendGitHubApi("GET", "https://api.github.com/gists/" + normCurId, tok, null, out gHeaders, out gCode);
                            Dictionary<string, object> gObj = js.DeserializeObject(gBody) as Dictionary<string, object>;
                            if (gObj != null && gObj.ContainsKey("owner") && gObj["owner"] is Dictionary<string, object>)
                            {
                                Dictionary<string, object> ownerObj = (Dictionary<string, object>)gObj["owner"];
                                string ownerLogin = ownerObj.ContainsKey("login") && ownerObj["login"] != null
                                    ? Convert.ToString(ownerObj["login"], CultureInfo.InvariantCulture).Trim()
                                    : "";
                                res.CurrentGistOwned = string.Equals(ownerLogin, res.Login, StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        catch
                        {
                            res.CurrentGistOwned = false;
                        }
                    }
                }

                return res;
            }
            catch (Exception ex)
            {
                res.TokenValid = false;
                res.ErrorMessage = ex.Message;
                return res;
            }
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

        private static RecordBucket CloneRecordBucket(RecordBucket src)
        {
            if (src == null) return null;
            RecordBucket copy = new RecordBucket();
            copy.MachineId = src.MachineId;
            copy.MachineName = src.MachineName;
            copy.MachineType = src.MachineType;
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
            return copy;
        }

        private static NodeSnapshot CloneSnapshotSafe(NodeSnapshot src, string fallbackMachineId)
        {
            NodeSnapshot dst = new NodeSnapshot();
            if (src == null)
            {
                dst.MachineId = SanitizeMachineId(fallbackMachineId);
                dst.MachineName = dst.MachineId;
                dst.MachineType = "windows";
                dst.OsInfo = "";
                dst.SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                dst.IsLocal = true;
                return dst;
            }

            dst.MachineId = SanitizeMachineId(!string.IsNullOrWhiteSpace(src.MachineId) ? src.MachineId : fallbackMachineId);
            dst.MachineName = src.MachineName;
            dst.MachineType = src.MachineType;
            dst.OsInfo = src.OsInfo;
            dst.SyncedAt = src.SyncedAt;
            dst.IsLocal = src.IsLocal;
            dst.ScannerRev = src.ScannerRev;
            if (src.Records != null)
            {
                List<RecordBucket> srcRecords = src.Records;
                int count = srcRecords.Count;
                for (int i = 0; i < count; i++)
                {
                    if (i >= srcRecords.Count) break;
                    RecordBucket rb = CloneRecordBucket(srcRecords[i]);
                    if (rb != null)
                        dst.Records.Add(rb);
                }
            }
            if (IsNonDefaultHomeSnapshot(src))
                MarkNonDefaultHomeSnapshot(dst);
            return dst;
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
            dst.ScannerRev = src.ScannerRev;
            List<RecordBucket> copied = new List<RecordBucket>(src.Records.Count);
            for (int i = 0; i < src.Records.Count; i++)
            {
                RecordBucket rb = CloneRecordBucket(src.Records[i]);
                if (rb != null) copied.Add(rb);
            }
            dst.Records.Clear();
            dst.Records.AddRange(copied);
        }

        private static string PickNewerTimestamp(string primaryTs, string historicalTs)
        {
            string p = (primaryTs ?? "").Trim();
            string h = (historicalTs ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return h;
            if (string.IsNullOrEmpty(h)) return p;
            if (p.Length == 19 && h.Length == 19 && p[4] == '-' && h[4] == '-')
                return string.CompareOrdinal(p, h) >= 0 ? p : h;
            return p;
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
                res.SyncedAt = PickNewerTimestamp(primary.SyncedAt, historical != null ? historical.SyncedAt : null);
                res.IsLocal = primary.IsLocal;
                res.ScannerRev = primary.ScannerRev > 0 ? primary.ScannerRev : (historical != null ? historical.ScannerRev : 27);
            }
            else
            {
                res.MachineId = SanitizeMachineId(historical.MachineId);
                res.MachineName = !string.IsNullOrWhiteSpace(historical.MachineName) ? historical.MachineName : res.MachineId;
                res.MachineType = !string.IsNullOrWhiteSpace(historical.MachineType) ? historical.MachineType : "vps";
                res.OsInfo = historical.OsInfo ?? "";
                res.SyncedAt = historical.SyncedAt ?? "";
                res.IsLocal = historical.IsLocal;
                res.ScannerRev = historical.ScannerRev;
            }

            Dictionary<string, RecordBucket> primaryMap = BuildBucketMap(primary != null ? primary.Records : null, res.MachineId, res.MachineName, res.MachineType);
            Dictionary<string, RecordBucket> histMap = BuildBucketMap(historical != null ? historical.Records : null, res.MachineId, res.MachineName, res.MachineType);

            bool upgradingLegacyScanner = primary != null && primary.ScannerRev >= 27 && (historical == null || historical.ScannerRev < 27);
            bool primHasAgy = false;
            bool primHasCodex = false;
            if (upgradingLegacyScanner)
            {
                foreach (KeyValuePair<string, RecordBucket> kv in primaryMap)
                {
                    string t = kv.Value != null ? (kv.Value.Tool ?? "") : "";
                    if (string.Equals(t, "Google Antigravity", StringComparison.Ordinal))
                        primHasAgy = true;
                    else if (string.Equals(t, "OpenAI Codex", StringComparison.Ordinal))
                        primHasCodex = true;
                }
            }

            // 检测同日同工具模型重分类：避免 local_highwater / Gist 合并时把旧错模型与新准确模型叠加翻倍
            Dictionary<string, int> primDtTurns = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, long> primDtTokens = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, RecordBucket> kv in primaryMap)
            {
                RecordBucket b = kv.Value;
                string dtTool = (b.Date ?? "") + "|" + (b.Tool ?? "");
                int t;
                primDtTurns.TryGetValue(dtTool, out t);
                primDtTurns[dtTool] = t + b.Turns;
                long tok;
                primDtTokens.TryGetValue(dtTool, out tok);
                primDtTokens[dtTool] = tok + b.TotalTokens;
            }

            Dictionary<string, int> histDtTurns = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, long> histDtTokens = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, RecordBucket> kv in histMap)
            {
                RecordBucket b = kv.Value;
                string dtTool = (b.Date ?? "") + "|" + (b.Tool ?? "");
                int t;
                histDtTurns.TryGetValue(dtTool, out t);
                histDtTurns[dtTool] = t + b.Turns;
                long tok;
                histDtTokens.TryGetValue(dtTool, out tok);
                histDtTokens[dtTool] = tok + b.TotalTokens;
            }

            HashSet<string> reclassifiedDtTools = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in histDtTurns)
            {
                string dtTool = kv.Key;
                int ht = kv.Value;
                if (ht <= 0) continue;
                int pt;
                primDtTurns.TryGetValue(dtTool, out pt);
                long htok;
                histDtTokens.TryGetValue(dtTool, out htok);
                long ptok;
                primDtTokens.TryGetValue(dtTool, out ptok);
                if (pt >= ht && (double)ptok >= (double)htok * 0.98)
                {
                    bool hasExcess = false;
                    foreach (KeyValuePair<string, RecordBucket> hkv in histMap)
                    {
                        RecordBucket hb = hkv.Value;
                        if (((hb.Date ?? "") + "|" + (hb.Tool ?? "")) != dtTool) continue;
                        RecordBucket pb;
                        if (!primaryMap.TryGetValue(hkv.Key, out pb) || hb.Turns > pb.Turns || hb.TotalTokens > pb.TotalTokens)
                        {
                            hasExcess = true;
                            break;
                        }
                    }
                    if (hasExcess)
                        reclassifiedDtTools.Add(dtTool);
                }
            }

            foreach (KeyValuePair<string, RecordBucket> kv in primaryMap)
            {
                RecordBucket p = kv.Value;
                string dtTool = (p.Date ?? "") + "|" + (p.Tool ?? "");
                bool bypassLegacy = upgradingLegacyScanner &&
                    ((primHasAgy && string.Equals(p.Tool, "Google Antigravity", StringComparison.Ordinal)) ||
                     (primHasCodex && string.Equals(p.Tool, "OpenAI Codex", StringComparison.Ordinal)));
                if (bypassLegacy || reclassifiedDtTools.Contains(dtTool))
                {
                    res.Records.Add(p);
                    continue;
                }
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

                    bool primaryCoversAll = p.Turns >= h.Turns &&
                                            p.FreshInput >= h.FreshInput &&
                                            p.CacheRead >= h.CacheRead &&
                                            p.CacheWrite >= h.CacheWrite &&
                                            p.Output >= h.Output;

                    m.Turns = Math.Max(p.Turns, h.Turns);
                    m.FreshInput = Math.Max(p.FreshInput, h.FreshInput);
                    m.CacheRead = Math.Max(p.CacheRead, h.CacheRead);
                    m.CacheWrite = Math.Max(p.CacheWrite, h.CacheWrite);
                    m.Output = Math.Max(p.Output, h.Output);

                    long pSumTok = p.FreshInput + p.CacheRead + p.CacheWrite + p.Output;
                    if (primaryCoversAll && (p.CostUsd > 0.0 || pSumTok == 0L))
                    {
                        m.CostUsd = p.CostUsd;
                        m.BaselineUsd = (p.BaselineUsd > 0.0 || pSumTok == 0L) ? p.BaselineUsd : Math.Max(p.CostUsd, h.BaselineUsd);
                        m.RateLabel = !string.IsNullOrWhiteSpace(p.RateLabel) ? p.RateLabel : (h.RateLabel ?? "");
                    }
                    else
                    {
                        m.CostUsd = Math.Max(p.CostUsd, h.CostUsd);
                        m.BaselineUsd = Math.Max(p.BaselineUsd, h.BaselineUsd);
                        m.RateLabel = !string.IsNullOrWhiteSpace(p.RateLabel) ? p.RateLabel : (h.RateLabel ?? "");
                    }
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
                    RecordBucket hb = kv.Value;
                    bool bypassLegacy = upgradingLegacyScanner &&
                        ((primHasAgy && string.Equals(hb.Tool, "Google Antigravity", StringComparison.Ordinal)) ||
                         (primHasCodex && string.Equals(hb.Tool, "OpenAI Codex", StringComparison.Ordinal)));
                    string dtTool = (hb.Date ?? "") + "|" + (hb.Tool ?? "");
                    if (!bypassLegacy && !reclassifiedDtTools.Contains(dtTool))
                        res.Records.Add(hb);
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
                    string hwJson;
                    lock (FileIoGate)
                    {
                        hwJson = File.ReadAllText(hwPath, Encoding.UTF8);
                    }
                    NodeSnapshot cached = DeserializeSnapshot(hwJson, true);
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
                AtomicWriteAllText(hwPath, SerializeSnapshot(merged));
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
                    RecordBucket copy = CloneRecordBucket(src);
                    copy.MachineId = snap.MachineId;
                    copy.MachineName = snap.MachineName;
                    copy.MachineType = snap.MachineType;
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
                if (r == null) continue;
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
            root["scanner_rev"] = snap.ScannerRev > 0 ? snap.ScannerRev : 27;
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
                snap.ScannerRev = 0;
                if (d.ContainsKey("scanner_rev") && d["scanner_rev"] != null)
                {
                    try { snap.ScannerRev = Convert.ToInt32(d["scanner_rev"], CultureInfo.InvariantCulture); }
                    catch { snap.ScannerRev = 0; }
                }

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
                            if (string.Equals(r.Tool, "Google Antigravity", StringComparison.Ordinal) &&
                                string.Equals(r.Model, "orchestrator", StringComparison.OrdinalIgnoreCase))
                            {
                                r.Model = "gemini-pro-orchestrator";
                            }
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
            string cleanLocalId = SanitizeMachineId(localMachineId);
            try
            {
                if (!Directory.Exists(NodesCacheDir)) return list;
                string[] files = Directory.GetFiles(NodesCacheDir, "toklens_node_*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string raw;
                        lock (FileIoGate)
                        {
                            raw = File.ReadAllText(files[i], Encoding.UTF8);
                        }
                        NodeSnapshot snap = DeserializeSnapshot(raw, false);
                        if (snap != null && !string.Equals(snap.MachineId, cleanLocalId, StringComparison.OrdinalIgnoreCase))
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
                string fn = Path.Combine(NodesCacheDir, "toklens_node_" + snap.MachineId + ".json");
                if (File.Exists(fn))
                {
                    string raw;
                    lock (FileIoGate)
                    {
                        raw = File.ReadAllText(fn, Encoding.UTF8);
                    }
                    NodeSnapshot oldCached = DeserializeSnapshot(raw, false);
                    if (oldCached != null)
                    {
                        NodeSnapshot merged = MergeNodeSnapshotsHighWater(snap, oldCached);
                        CopySnapshotInto(merged, snap);
                    }
                }
                AtomicWriteAllText(fn, SerializeSnapshot(snap));
            }
            catch { }
            return snap;
        }

        public static void RemoveCachedNode(string machineId)
        {
            if (string.IsNullOrWhiteSpace(machineId)) return;
            try
            {
                string fn = Path.Combine(NodesCacheDir, "toklens_node_" + SanitizeMachineId(machineId) + ".json");
                lock (FileIoGate)
                {
                    if (File.Exists(fn)) File.Delete(fn);
                }
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
            if (string.IsNullOrWhiteSpace(token)) return null;
            List<GitHubGistCandidate> candidates = ScanTokLensGistCandidatesStrict(token, 5);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].IsPublic)
                    return candidates[i].GistId;
            }
            return null;
        }

        public static string FindOrCreatePrivateGist(string token, NodeSnapshot localSnap, out bool reusedExisting)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("请先填写 GitHub 访问令牌（需具备 gist 权限）。");

            // 严禁吞掉网络/401/403异常：若检索失败将直接向上抛出，绝不在网络抖动时误建第二个空白 Gist
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

            NodeSnapshot safeSnap = CloneSnapshotSafe(localSnap, "win-pc");
            Dictionary<string, object> files = new Dictionary<string, object>();
            string nodeFile = "toklens_node_" + safeSnap.MachineId + ".json";
            Dictionary<string, object> nfObj = new Dictionary<string, object>();
            nfObj["content"] = SerializeSnapshot(safeSnap);
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
            WebHeaderCollection headers;
            int statusCode;
            string respBody = SendGitHubApi("POST", "https://api.github.com/gists", token.Trim(), js.Serialize(payload), out headers, out statusCode);
            Dictionary<string, object> resp = js.DeserializeObject(respBody) as Dictionary<string, object>;
            if (resp == null || !resp.ContainsKey("id") || resp["id"] == null)
                throw new InvalidOperationException("GitHub 返回格式异常，未能获取 Gist ID。");
            return NormalizeGistId(Convert.ToString(resp["id"], CultureInfo.InvariantCulture));
        }

        public static SyncOutcome SyncNow(SyncConfig cfg, NodeSnapshot localSnap, bool uploadAgentScript)
        {
            if (cfg == null) cfg = LoadConfig();
            NodeSnapshot workingSnap = CloneSnapshotSafe(localSnap, cfg.MachineId);

            lock (SyncGate)
            {
                SyncOutcome outcome = new SyncOutcome();
                try
                {
                    if (!cfg.IsConfigured)
                    {
                        outcome.Success = false;
                        outcome.Message = "尚未配置多端同步凭据";
                        outcome.RemoteNodes = LoadCachedRemoteNodes(workingSnap.MachineId);
                        outcome.RemoteNodeCount = outcome.RemoteNodes.Count;
                        return outcome;
                    }

                    if (string.Equals(cfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase))
                    {
                        return SyncWebDav(cfg, workingSnap, uploadAgentScript);
                    }
                    return SyncGist(cfg, workingSnap, uploadAgentScript);
                }
                catch (Exception ex)
                {
                    outcome.Success = false;
                    outcome.Message = "同步失败：" + ex.Message;
                    outcome.RemoteNodes = LoadCachedRemoteNodes(workingSnap.MachineId);
                    outcome.RemoteNodeCount = outcome.RemoteNodes.Count;
                    return outcome;
                }
            }
        }

        private static bool IsTrustedGitHubHost(Uri uri, out bool sendAuthHeader)
        {
            sendAuthHeader = false;
            if (uri == null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;
            string host = (uri.Host ?? "").ToLowerInvariant();
            if (host == "api.github.com")
            {
                sendAuthHeader = true;
                return true;
            }
            if (host == "gist.githubusercontent.com" ||
                host == "raw.githubusercontent.com" ||
                host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))
            {
                sendAuthHeader = false;
                return true;
            }
            return false;
        }

        private static string ReadGistFileContent(Dictionary<string, object> fmeta, string bearerToken)
        {
            if (fmeta == null) return null;
            string content = fmeta.ContainsKey("content") && fmeta["content"] != null
                ? Convert.ToString(fmeta["content"], CultureInfo.InvariantCulture)
                : null;
            bool truncated = fmeta.ContainsKey("truncated") &&
                             fmeta["truncated"] != null &&
                             Convert.ToBoolean(fmeta["truncated"], CultureInfo.InvariantCulture);

            if ((truncated || string.IsNullOrEmpty(content)) && fmeta.ContainsKey("raw_url") && fmeta["raw_url"] != null)
            {
                string rawUrl = Convert.ToString(fmeta["raw_url"], CultureInfo.InvariantCulture).Trim();
                Uri rawUri;
                bool sendAuth;
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out rawUri) && IsTrustedGitHubHost(rawUri, out sendAuth))
                {
                    content = SendHttpJson("GET", rawUri.AbsoluteUri, sendAuth ? bearerToken : null, null, null);
                }
            }
            return content;
        }

        private static Dictionary<string, NodeSnapshot> BuildCachedRemoteMap(string localMachineId)
        {
            Dictionary<string, NodeSnapshot> map = new Dictionary<string, NodeSnapshot>(StringComparer.OrdinalIgnoreCase);
            List<NodeSnapshot> cached = LoadCachedRemoteNodes(localMachineId);
            for (int i = 0; i < cached.Count; i++)
            {
                NodeSnapshot n = cached[i];
                if (n != null && !string.IsNullOrEmpty(n.MachineId))
                    map[n.MachineId] = n;
            }
            return map;
        }

        private static SyncOutcome SyncGist(SyncConfig cfg, NodeSnapshot localSnap, bool uploadAgentScript)
        {
            EnsureTls();
            JavaScriptSerializer js = NewJson();

            localSnap.MachineId = SanitizeMachineId(localSnap.MachineId);
            string gistId = NormalizeGistId(cfg.GistId);
            if (string.IsNullOrEmpty(gistId))
                throw new InvalidOperationException("Gist ID 格式无效，请留空以自动查找/新建，或填写正确的 Gist ID。");

            string localFileName = "toklens_node_" + localSnap.MachineId + ".json";
            string url = "https://api.github.com/gists/" + gistId;
            string token = (cfg.GistToken ?? "").Trim();

            // 1. 拉取远端现有 Gist，校验权限与归属，并在内存中完成高水位预合并（Write-after-Commit，防止跨账号污染本地）
            WebHeaderCollection preHeaders;
            int preCode;
            string preRespText = SendGitHubApi("GET", url, token, null, out preHeaders, out preCode);
            if (preHeaders != null && preHeaders["X-OAuth-Scopes"] != null && !HasScopeToken(preHeaders["X-OAuth-Scopes"], "gist"))
            {
                throw new InvalidOperationException("当前 GitHub 令牌缺少 gist 权限，请执行 gh auth refresh -h github.com -s gist 或更换勾选 gist 的 Token。");
            }

            Dictionary<string, object> preGistObj = js.DeserializeObject(preRespText) as Dictionary<string, object>;
            string preOwnerLogin = "";
            if (preGistObj != null && preGistObj.ContainsKey("owner") && preGistObj["owner"] is Dictionary<string, object>)
            {
                Dictionary<string, object> ownerObj = (Dictionary<string, object>)preGistObj["owner"];
                if (ownerObj.ContainsKey("login") && ownerObj["login"] != null)
                    preOwnerLogin = Convert.ToString(ownerObj["login"], CultureInfo.InvariantCulture).Trim();
            }

            NodeSnapshot uploadSnap = CloneSnapshotSafe(localSnap, localSnap.MachineId);
            List<string> obsoleteFilesToDelete = new List<string>();

            if (preGistObj != null && preGistObj.ContainsKey("files") && preGistObj["files"] is Dictionary<string, object>)
            {
                Dictionary<string, object> preFiles = (Dictionary<string, object>)preGistObj["files"];
                foreach (KeyValuePair<string, object> kv in preFiles)
                {
                    if (string.Equals(kv.Key, localFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> existingMeta = kv.Value as Dictionary<string, object>;
                        try
                        {
                            string existingContent = ReadGistFileContent(existingMeta, token);
                            NodeSnapshot remoteExistingSelf = DeserializeSnapshot(existingContent, true);
                            if (remoteExistingSelf != null)
                                uploadSnap = MergeNodeSnapshotsHighWater(uploadSnap, remoteExistingSelf);
                        }
                        catch { }
                        continue;
                    }

                    Match mNode = GistNodeFileRegex.Match(kv.Key ?? "");
                    if (mNode.Success)
                    {
                        string remoteMid = SanitizeMachineId(mNode.Groups[1].Value);
                        if (PendingObsoleteMachineIds.Contains(remoteMid))
                        {
                            Dictionary<string, object> oldMeta = kv.Value as Dictionary<string, object>;
                            try
                            {
                                string oldContent = ReadGistFileContent(oldMeta, token);
                                NodeSnapshot oldSelfSnap = DeserializeSnapshot(oldContent, true);
                                if (oldSelfSnap != null)
                                {
                                    oldSelfSnap.MachineId = uploadSnap.MachineId;
                                    uploadSnap = MergeNodeSnapshotsHighWater(uploadSnap, oldSelfSnap);
                                }
                            }
                            catch { }
                            obsoleteFilesToDelete.Add(kv.Key);
                        }
                    }
                }
            }

            uploadSnap.SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            // 2. PATCH 上传合并后的完整本机高水位快照
            Dictionary<string, object> files = new Dictionary<string, object>();
            Dictionary<string, object> nf = new Dictionary<string, object>();
            nf["content"] = SerializeSnapshot(uploadSnap);
            files[localFileName] = nf;

            for (int i = 0; i < obsoleteFilesToDelete.Count; i++)
            {
                files[obsoleteFilesToDelete[i]] = null;
            }

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

            string respText;
            try
            {
                WebHeaderCollection patchHeaders;
                int patchCode;
                respText = SendGitHubApi("PATCH", url, token, js.Serialize(body), out patchHeaders, out patchCode);
            }
            catch (InvalidOperationException ioe)
            {
                if (ioe.Message != null && ioe.Message.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string ownerHint = string.IsNullOrEmpty(preOwnerLogin) ? "其他账号" : ("@" + preOwnerLogin);
                    throw new InvalidOperationException(
                        "当前 GitHub 令牌无权写入该 Gist（该库归属于 " + ownerHint + "，或细粒度令牌缺少 Gists 写权限）。若刚切换了账号，请清空 Gist ID 后点保存以自动关联当前账号的同步库。");
                }
                throw;
            }

            // PATCH 成功后再将合并结果落盘至本地高水位，保证本地数据绝对不被跨账号旧 Gist 污染
            if (!IsNonDefaultHomeSnapshot(localSnap))
            {
                MergeWithLocalHighWater(uploadSnap);
            }
            for (int i = 0; i < obsoleteFilesToDelete.Count; i++)
            {
                Match mDel = GistNodeFileRegex.Match(obsoleteFilesToDelete[i]);
                if (mDel.Success)
                    PendingObsoleteMachineIds.Remove(SanitizeMachineId(mDel.Groups[1].Value));
            }

            Dictionary<string, object> gistObj = js.DeserializeObject(respText) as Dictionary<string, object>;
            if (gistObj == null || !gistObj.ContainsKey("files"))
                throw new InvalidOperationException("GitHub Gist 响应缺少 files 字段");

            Dictionary<string, object> remoteFiles = gistObj["files"] as Dictionary<string, object>;
            Dictionary<string, NodeSnapshot> cachedMap = BuildCachedRemoteMap(uploadSnap.MachineId);
            Dictionary<string, NodeSnapshot> resolvedRemotes = new Dictionary<string, NodeSnapshot>(StringComparer.OrdinalIgnoreCase);
            int degradedNodes = 0;

            if (remoteFiles != null)
            {
                foreach (KeyValuePair<string, object> kv in remoteFiles)
                {
                    string fn = kv.Key ?? "";
                    Match mNode = GistNodeFileRegex.Match(fn);
                    if (!mNode.Success) continue;

                    string candMid = SanitizeMachineId(mNode.Groups[1].Value);
                    if (string.Equals(candMid, uploadSnap.MachineId, StringComparison.OrdinalIgnoreCase) ||
                        PendingObsoleteMachineIds.Contains(candMid))
                        continue;

                    Dictionary<string, object> fmeta = kv.Value as Dictionary<string, object>;
                    if (fmeta == null) continue;

                    try
                    {
                        string content = ReadGistFileContent(fmeta, token);
                        NodeSnapshot snap = DeserializeSnapshot(content, false);
                        if (snap != null && !string.Equals(snap.MachineId, uploadSnap.MachineId, StringComparison.OrdinalIgnoreCase))
                        {
                            snap = SaveRemoteNodeCache(snap);
                            resolvedRemotes[snap.MachineId] = snap;
                            continue;
                        }
                    }
                    catch { }

                    degradedNodes++;
                    NodeSnapshot fallbackSnap;
                    if (cachedMap.TryGetValue(candMid, out fallbackSnap))
                        resolvedRemotes[candMid] = fallbackSnap;
                }
            }

            List<NodeSnapshot> remotes = new List<NodeSnapshot>(resolvedRemotes.Values);
            SyncOutcome ok = new SyncOutcome();
            ok.Success = true;
            ok.RemoteNodes = remotes;
            ok.RemoteNodeCount = remotes.Count;
            ok.Message = degradedNodes > 0
                ? string.Format(CultureInfo.InvariantCulture, "已同步（本机 + {0} 台远端设备，{1} 台沿用本地缓存）", remotes.Count, degradedNodes)
                : string.Format(CultureInfo.InvariantCulture, "已同步（本机 + {0} 台远端设备）", remotes.Count);
            return ok;
        }

        private static SyncOutcome SyncWebDav(SyncConfig cfg, NodeSnapshot localSnap, bool uploadAgentScript)
        {
            EnsureTls();
            localSnap.MachineId = SanitizeMachineId(localSnap.MachineId);
            string rawBase = (cfg.WebDavUrl ?? "").Trim();
            Uri baseUri;
            if (!Uri.TryCreate(rawBase, UriKind.Absolute, out baseUri) ||
                (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("WebDAV 地址格式无效，请填写以 https:// 或 http:// 开头的完整目录 URL。");
            }

            string baseUrl = rawBase.TrimEnd('/') + "/";
            string basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes((cfg.WebDavUser ?? "") + ":" + (cfg.WebDavPass ?? "")));

            // 尝试创建目录（已存在时忽略）
            try { SendHttpRaw("MKCOL", baseUrl, basicAuth, null, null, null); } catch { }

            // 1. 先 PROPFIND（附带标准最小 XML 请求体，兼容 Nextcloud / Apache mod_dav / 坚果云）
            Dictionary<string, string> extraHeaders = new Dictionary<string, string>();
            extraHeaders["Depth"] = "1";
            byte[] propBody = Encoding.UTF8.GetBytes(WebDavPropfindBody);
            string propXml = SendHttpRaw("PROPFIND", baseUrl, basicAuth, "application/xml; charset=utf-8", propBody, extraHeaders);

            string decodedXml = propXml ?? "";
            try { decodedXml = Uri.UnescapeDataString(decodedXml); } catch { }

            HashSet<string> nodeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MatchCollection matches = WebDavNodeRegex.Matches(decodedXml);
            for (int i = 0; i < matches.Count; i++)
            {
                nodeFiles.Add(matches[i].Groups[1].Value);
            }
            MatchCollection rawMatches = WebDavNodeRegex.Matches(propXml ?? "");
            for (int i = 0; i < rawMatches.Count; i++)
            {
                nodeFiles.Add(rawMatches[i].Groups[1].Value);
            }

            string localFn = "toklens_node_" + localSnap.MachineId + ".json";
            string existingRemoteSelfFn = null;
            List<string> obsoleteRemoteFns = new List<string>();

            foreach (string fn in nodeFiles)
            {
                if (string.Equals(fn, localFn, StringComparison.OrdinalIgnoreCase))
                {
                    existingRemoteSelfFn = fn;
                    continue;
                }
                Match mNode = GistNodeFileRegex.Match(fn);
                if (mNode.Success && PendingObsoleteMachineIds.Contains(SanitizeMachineId(mNode.Groups[1].Value)))
                {
                    obsoleteRemoteFns.Add(fn);
                }
            }

            NodeSnapshot uploadSnap = CloneSnapshotSafe(localSnap, localSnap.MachineId);

            // 2. 若远端已存在同名本机快照（或刚改名前的旧快照），先在内存中做高水位合并
            if (existingRemoteSelfFn != null)
            {
                try
                {
                    string existingJson = SendHttpRaw("GET", baseUrl + Uri.EscapeDataString(existingRemoteSelfFn), basicAuth, null, null, null);
                    NodeSnapshot remoteExistingSelf = DeserializeSnapshot(existingJson, true);
                    if (remoteExistingSelf != null)
                        uploadSnap = MergeNodeSnapshotsHighWater(uploadSnap, remoteExistingSelf);
                }
                catch { }
            }

            for (int i = 0; i < obsoleteRemoteFns.Count; i++)
            {
                try
                {
                    string oldJson = SendHttpRaw("GET", baseUrl + Uri.EscapeDataString(obsoleteRemoteFns[i]), basicAuth, null, null, null);
                    NodeSnapshot oldSnap = DeserializeSnapshot(oldJson, true);
                    if (oldSnap != null)
                    {
                        oldSnap.MachineId = uploadSnap.MachineId;
                        uploadSnap = MergeNodeSnapshotsHighWater(uploadSnap, oldSnap);
                    }
                }
                catch { }
            }

            uploadSnap.SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            // 3. PUT 上传合并后的完整历史快照，成功后再更新本地高水位
            SendHttpRaw("PUT", baseUrl + Uri.EscapeDataString(localFn), basicAuth, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SerializeSnapshot(uploadSnap)), null);

            if (!IsNonDefaultHomeSnapshot(localSnap))
            {
                MergeWithLocalHighWater(uploadSnap);
            }

            for (int i = 0; i < obsoleteRemoteFns.Count; i++)
            {
                try
                {
                    SendHttpRaw("DELETE", baseUrl + Uri.EscapeDataString(obsoleteRemoteFns[i]), basicAuth, null, null, null);
                    Match mDel = GistNodeFileRegex.Match(obsoleteRemoteFns[i]);
                    if (mDel.Success)
                        PendingObsoleteMachineIds.Remove(SanitizeMachineId(mDel.Groups[1].Value));
                }
                catch { }
            }

            if (uploadAgentScript)
            {
                string agentPy = GetEmbeddedVpsAgentScript();
                if (!string.IsNullOrEmpty(agentPy))
                {
                    try { SendHttpRaw("PUT", baseUrl + "toklens_agent.py", basicAuth, "text/x-python; charset=utf-8", Encoding.UTF8.GetBytes(agentPy), null); } catch { }
                }
            }

            // 4. 拉取其余远端设备快照并与本地缓存做高水位合并，单节点超时则回退本地缓存
            Dictionary<string, NodeSnapshot> cachedMap = BuildCachedRemoteMap(uploadSnap.MachineId);
            Dictionary<string, NodeSnapshot> resolvedRemotes = new Dictionary<string, NodeSnapshot>(StringComparer.OrdinalIgnoreCase);
            int degradedNodes = 0;

            foreach (string fn in nodeFiles)
            {
                if (string.Equals(fn, localFn, StringComparison.OrdinalIgnoreCase)) continue;
                Match mNode = GistNodeFileRegex.Match(fn);
                string candMid = mNode.Success ? SanitizeMachineId(mNode.Groups[1].Value) : "";
                if (!string.IsNullOrEmpty(candMid) &&
                    (string.Equals(candMid, uploadSnap.MachineId, StringComparison.OrdinalIgnoreCase) || PendingObsoleteMachineIds.Contains(candMid)))
                    continue;

                try
                {
                    string json = SendHttpRaw("GET", baseUrl + Uri.EscapeDataString(fn), basicAuth, null, null, null);
                    NodeSnapshot snap = DeserializeSnapshot(json, false);
                    if (snap != null && !string.Equals(snap.MachineId, uploadSnap.MachineId, StringComparison.OrdinalIgnoreCase))
                    {
                        snap = SaveRemoteNodeCache(snap);
                        resolvedRemotes[snap.MachineId] = snap;
                        continue;
                    }
                }
                catch { }

                degradedNodes++;
                NodeSnapshot fallbackSnap;
                if (!string.IsNullOrEmpty(candMid) && cachedMap.TryGetValue(candMid, out fallbackSnap))
                    resolvedRemotes[candMid] = fallbackSnap;
            }

            List<NodeSnapshot> remotes = new List<NodeSnapshot>(resolvedRemotes.Values);
            SyncOutcome ok = new SyncOutcome();
            ok.Success = true;
            ok.RemoteNodes = remotes;
            ok.RemoteNodeCount = remotes.Count;
            ok.Message = degradedNodes > 0
                ? string.Format(CultureInfo.InvariantCulture, "WebDAV 已同步（本机 + {0} 台远端设备，{1} 台沿用本地缓存）", remotes.Count, degradedNodes)
                : string.Format(CultureInfo.InvariantCulture, "WebDAV 已同步（本机 + {0} 台远端设备）", remotes.Count);
            return ok;
        }

        public static string BuildLinuxVpsOneLiner(SyncConfig cfg, string vpsDisplayName, int cronMinutes)
        {
            string pairCode = GeneratePairingCode(cfg);
            string cleanName = (vpsDisplayName ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            string nameArg = string.IsNullOrWhiteSpace(cleanName) ? "" : (" --name " + PosixSingleQuote(cleanName));
            int mins = Math.Max(5, Math.Min(59, cronMinutes));
            string quotedBootstrap = PosixSingleQuote(VpsBootstrapPython);
            string quotedPair = PosixSingleQuote(pairCode);

            return string.Format(
                CultureInfo.InvariantCulture,
                "mkdir -p ~/.toklens && python3 -c {0} {1} && python3 ~/.toklens/toklens_agent.py --pair {1}{2} --sync --install-cron {3}",
                quotedBootstrap,
                quotedPair,
                nameArg,
                mins);
        }

        private static string SendGitHubApi(string method, string url, string bearerToken, string jsonBody, out WebHeaderCollection respHeaders, out int statusCode)
        {
            respHeaders = null;
            statusCode = 0;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.AllowAutoRedirect = false;
            req.UserAgent = "TokLens-Desktop/2.2";
            req.Accept = "application/vnd.github+json, application/json";
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            if (!string.IsNullOrEmpty(bearerToken))
                req.Headers["Authorization"] = "Bearer " + bearerToken;

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
                {
                    statusCode = (int)resp.StatusCode;
                    respHeaders = resp.Headers;
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
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        statusCode = (int)hr.StatusCode;
                        respHeaders = hr.Headers;
                    }
                    try
                    {
                        using (Stream es = wex.Response.GetResponseStream())
                        {
                            if (es != null)
                            {
                                using (StreamReader sr = new StreamReader(es, Encoding.UTF8))
                                {
                                    string errBody = sr.ReadToEnd();
                                    string compact = errBody.Length > 180 ? errBody.Substring(0, 180) : errBody;
                                    if (statusCode == 401)
                                        throw new InvalidOperationException("GitHub 令牌无效或已过期 (HTTP 401)，请重新读取 gh 登录或重新生成 Token。");
                                    throw new InvalidOperationException("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + ": " + compact);
                                }
                            }
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

        private static string SendHttpJson(string method, string url, string bearerToken, string basicAuth, string jsonBody)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.AllowAutoRedirect = false;
            req.UserAgent = "TokLens-Desktop/2.2";
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
            req.AllowAutoRedirect = false;
            req.UserAgent = "TokLens-Desktop/2.2";
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
            if (!string.IsNullOrEmpty(contentType) && body != null)
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
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    int code = hr != null ? (int)hr.StatusCode : 0;
                    if (code == 401 || code == 403)
                        throw new InvalidOperationException("WebDAV 认证失败或无访问权限 (HTTP " + code.ToString(CultureInfo.InvariantCulture) + ")，请检查账号与应用密码。");
                }
                throw;
            }
        }
    }
}
