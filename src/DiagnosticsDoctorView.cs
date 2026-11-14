using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;
using IoPath = System.IO.Path;
using Shape = System.Windows.Shapes.Shape;
using WpfPath = System.Windows.Shapes.Path;

namespace TokLens
{
    /// <summary>
    /// 单个 AI 数据源或基础设施探针的体检结果条目。
    /// </summary>
    public sealed class DataSourceProbeItem
    {
        public const string CategoryNativeCli = "原生桌面/CLI";
        public const string CategoryIdeDb = "IDE 扩展与数据库";
        public const string CategoryWsl = "WSL2 Linux 子系统";
        public const string CategoryCloudStore = "云同步与本地存储";

        public const string StatusActive = "Active";
        public const string StatusInstalledEmpty = "InstalledEmpty";
        public const string StatusNotInstalled = "NotInstalled";
        public const string StatusWarning = "Warning";

        public string Category { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string StatusLabel { get; set; }
        public string TargetPath { get; set; }
        public int FileCount { get; set; }
        public bool FileCountCapped { get; set; }
        public string FileCountText { get; set; }
        public long TotalBytes { get; set; }
        public string TotalSizeText { get; set; }
        public long LastModifiedTicks { get; set; }
        public string LastModifiedText { get; set; }
        public string DiagnosticHint { get; set; }
        public int StatusRank { get; set; }
        public bool CanOpenDirectory { get; set; }
        public string TooltipDetail { get; set; }

        public DataSourceProbeItem()
        {
            Category = CategoryNativeCli;
            Name = "";
            Status = StatusNotInstalled;
            StatusLabel = "未安装";
            TargetPath = "";
            FileCount = 0;
            FileCountCapped = false;
            FileCountText = "—";
            TotalBytes = 0L;
            TotalSizeText = "—";
            LastModifiedTicks = 0L;
            LastModifiedText = "—";
            DiagnosticHint = "";
            StatusRank = 3;
            CanOpenDirectory = false;
            TooltipDetail = "";
        }
    }

    /// <summary>
    /// 本机 AI 遥测环境与数据源完整体检报告。
    /// </summary>
    public sealed class SystemHealthReport
    {
        public string GeneratedAt { get; set; }
        public string HomeDirectory { get; set; }
        public int ActiveSourceCount { get; set; }
        public int TotalSourceCount { get; set; }
        public int WarningCount { get; set; }
        public long TotalLogBytes { get; set; }
        public string TotalLogSizeText { get; set; }
        public List<string> WslDistroNames { get; set; }
        public List<string> WslMountedDistros { get; set; }
        public bool ScanLocalWslEnabled { get; set; }
        public string LedgerDbPath { get; set; }
        public long LedgerDbBytes { get; set; }
        public string LedgerDbSizeText { get; set; }
        public bool LedgerDbHealthy { get; set; }
        public int TrackedCursorCount { get; set; }
        public int DailyArchiveRowCount { get; set; }
        public string CustomPricesPath { get; set; }
        public int CustomOverrideCount { get; set; }
        public int CustomHarnessRuleCount { get; set; }
        public bool GhCliFound { get; set; }
        public string GhCliExecutablePath { get; set; }
        public List<string> GhAccounts { get; set; }
        public string GhActiveAccount { get; set; }
        public string GhEnvOverrideWarning { get; set; }
        public string SyncTokenKindSummary { get; set; }
        public long ScanElapsedMs { get; set; }
        public List<DataSourceProbeItem> Probes { get; set; }

        public SystemHealthReport()
        {
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            HomeDirectory = "";
            ActiveSourceCount = 0;
            TotalSourceCount = 0;
            WarningCount = 0;
            TotalLogBytes = 0L;
            TotalLogSizeText = "0 B";
            WslDistroNames = new List<string>();
            WslMountedDistros = new List<string>();
            ScanLocalWslEnabled = false;
            LedgerDbPath = "";
            LedgerDbBytes = 0L;
            LedgerDbSizeText = "0 B";
            LedgerDbHealthy = true;
            TrackedCursorCount = 0;
            DailyArchiveRowCount = 0;
            CustomPricesPath = "";
            CustomOverrideCount = 0;
            CustomHarnessRuleCount = 0;
            GhCliFound = false;
            GhCliExecutablePath = "";
            GhAccounts = new List<string>();
            GhActiveAccount = "";
            GhEnvOverrideWarning = "";
            SyncTokenKindSummary = "";
            ScanElapsedMs = 0L;
            Probes = new List<DataSourceProbeItem>();
        }
    }

    /// <summary>
    /// 本机 AI 环境与数据源极速探针扫描器（全异常安全兜底，单探针文件采样上限防卡顿）。
    /// </summary>
    public static class TelemetryDoctorScanner
    {
        public const int MaxFilesPerProbe = 500;
        private const int MaxDirectoriesPerRoot = 1200;

        private sealed class ProbeAccum
        {
            public bool AnyRootExists;
            public bool AccessDenied;
            public string BestExistingPath;
            public string FallbackPath;
            public int FileCount;
            public bool Capped;
            public long TotalBytes;
            public long LatestMtimeTicks;

            public ProbeAccum(string fallbackPath)
            {
                AnyRootExists = false;
                AccessDenied = false;
                BestExistingPath = "";
                FallbackPath = fallbackPath ?? "";
                FileCount = 0;
                Capped = false;
                TotalBytes = 0L;
                LatestMtimeTicks = 0L;
            }
        }

        public static SystemHealthReport RunDiagnostics()
        {
            string home = "";
            try
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch { }
            return RunDiagnostics(home);
        }

        public static SystemHealthReport RunDiagnostics(string homeDir)
        {
            Stopwatch sw = Stopwatch.StartNew();
            SystemHealthReport report = new SystemHealthReport();

            string home = homeDir;
            if (string.IsNullOrWhiteSpace(home))
            {
                try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
                catch { home = ""; }
            }
            report.HomeDirectory = home ?? "";

            string roaming = "";
            string localApp = "";
            try { roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { }
            try { localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }

            if (string.IsNullOrWhiteSpace(roaming) && !string.IsNullOrEmpty(home))
                roaming = IoPath.Combine(IoPath.Combine(home, "AppData"), "Roaming");
            if (string.IsNullOrWhiteSpace(localApp) && !string.IsNullOrEmpty(home))
                localApp = IoPath.Combine(IoPath.Combine(home, "AppData"), "Local");

            SyncConfig syncCfg = null;
            try { syncCfg = SyncService.LoadConfig(); } catch { }
            if (syncCfg != null)
            {
                report.ScanLocalWslEnabled = syncCfg.ScanLocalWsl;
                report.SyncTokenKindSummary = SyncService.DescribeTokenKind(syncCfg.GistToken);
            }

            List<DataSourceProbeItem> probes = new List<DataSourceProbeItem>(28);

            // ==================== 1. 原生桌面 / CLI 探针 (12 项) ====================
            TryAddProbe(probes, delegate() { return ProbeClaudeCode(home); });
            TryAddProbe(probes, delegate() { return ProbeClaudeDesktop(home, roaming, localApp); });
            NoteDesktopSessionsUnderClaudeCode(probes);
            TryAddProbe(probes, delegate() { return ProbeCodexCli(home); });
            TryAddProbe(probes, delegate() { return ProbeGeminiCli(home); });
            TryAddProbe(probes, delegate() { return ProbeKimiCode(home); });
            TryAddProbe(probes, delegate() { return ProbeZCode(home); });
            TryAddProbe(probes, delegate() { return ProbeQwenCode(home); });
            TryAddProbe(probes, delegate() { return ProbeAider(home); });
            TryAddProbe(probes, delegate() { return ProbeOpenClawFamily(home); });
            TryAddProbe(probes, delegate() { return ProbeWorkBuddyFamily(home); });
            TryAddProbe(probes, delegate() { return ProbeDshAndCommandCode(home); });
            TryAddProbe(probes, delegate() { return ProbeEmergingAgentClis(home, roaming, localApp); });

            // ==================== 2. IDE 扩展与数据库 探针 (11 项) ====================
            TryAddProbe(probes, delegate() { return ProbeAntigravityIde(home); });
            TryAddProbe(probes, delegate() { return ProbeCursorStateDb(roaming); });
            TryAddProbe(probes, delegate() { return ProbeWindsurfIde(home, roaming); });
            TryAddProbe(probes, delegate() { return ProbeVsixExtensionAcrossIdes(roaming, "Cline 扩展 (多 IDE 穿透)", "saoudrizwan.claude-dev"); });
            TryAddProbe(probes, delegate() { return ProbeVsixExtensionAcrossIdes(roaming, "Roo Code 扩展 (多 IDE 穿透)", "rooveterinaryinc.roo-cline"); });
            TryAddProbe(probes, delegate() { return ProbeVsixExtensionAcrossIdes(roaming, "Kilo Code 扩展 (多 IDE 穿透)", "kilocode.kilo-code"); });
            TryAddProbe(probes, delegate() { return ProbeOpenCode(home, localApp); });
            TryAddProbe(probes, delegate() { return ProbeZedEditor(localApp); });
            TryAddProbe(probes, delegate() { return ProbeContinueDev(home); });
            TryAddProbe(probes, delegate() { return ProbeCharmCrush(home, localApp); });
            TryAddProbe(probes, delegate() { return ProbeTraeKiroQoder(roaming); });

            // ==================== 3. WSL2 Linux 子系统穿透探针 ====================
            try
            {
                InspectWslSubsystem(report, probes);
            }
            catch { }

            // ==================== 4. 云同步与本地存储基础设施体检 ====================
            try
            {
                DataSourceProbeItem ledgerProbe = InspectLocalSqliteLedger(report);
                if (ledgerProbe != null) probes.Add(ledgerProbe);
            }
            catch { }

            try
            {
                DataSourceProbeItem priceProbe = InspectCustomPricingAndHarnesses(home, roaming, report);
                if (priceProbe != null) probes.Add(priceProbe);
            }
            catch { }

            try
            {
                DataSourceProbeItem ghProbe = InspectGitHubCliAndEnv(roaming, syncCfg, report);
                if (ghProbe != null) probes.Add(ghProbe);
            }
            catch { }

            // ==================== 5. 汇总统计 ====================
            int activeTools = 0;
            int totalTools = 0;
            int warningCount = 0;
            long totalLogBytes = 0L;

            for (int i = 0; i < probes.Count; i++)
            {
                DataSourceProbeItem p = probes[i];
                if (p == null) continue;

                FinalizeProbeDisplayFields(p);

                if (string.Equals(p.Status, DataSourceProbeItem.StatusWarning, StringComparison.Ordinal))
                    warningCount++;

                bool isInfra = string.Equals(p.Category, DataSourceProbeItem.CategoryCloudStore, StringComparison.Ordinal);
                if (!isInfra)
                {
                    totalTools++;
                    if (string.Equals(p.Status, DataSourceProbeItem.StatusActive, StringComparison.Ordinal))
                        activeTools++;
                    if (p.TotalBytes > 0L)
                        totalLogBytes += p.TotalBytes;
                }
            }

            sw.Stop();
            report.GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            report.ActiveSourceCount = activeTools;
            report.TotalSourceCount = totalTools;
            report.WarningCount = warningCount;
            report.TotalLogBytes = totalLogBytes;
            report.TotalLogSizeText = FormatBytes(totalLogBytes);
            report.ScanElapsedMs = sw.ElapsedMilliseconds;
            report.Probes = probes;

            return report;
        }

        private static void TryAddProbe(List<DataSourceProbeItem> list, Func<DataSourceProbeItem> factory)
        {
            if (list == null || factory == null) return;
            try
            {
                DataSourceProbeItem item = factory();
                if (item != null)
                    list.Add(item);
            }
            catch { }
        }

        // -------------------- 各数据源精细探针实现 --------------------

        private static DataSourceProbeItem ProbeClaudeCode(string home)
        {
            string dotClaude = CombineSafe(home, ".claude");
            ProbeAccum acc = new ProbeAccum(dotClaude);

            ScanDirIntoAccum(acc, CombineSafe(dotClaude, "projects"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(dotClaude, "transcripts"), "*.jsonl", true, null);
            string xdgClaude = CombineSafe(CombineSafe(home, ".config"), "claude");
            ScanDirIntoAccum(acc, CombineSafe(xdgClaude, "projects"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(xdgClaude, "transcripts"), "*.jsonl", true, null);

            if (!acc.AnyRootExists && SafeDirectoryExists(dotClaude, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = dotClaude;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Claude Code",
                acc,
                "通过字节偏移游标 (Seek Cursor) 增量捕获 projects/*.jsonl，按 requestId 自动去重并还原耗时",
                "已检测到 ~/.claude 配置目录，尚未产生 projects/*.jsonl 本地对话日志",
                "未检测到 ~/.claude 目录；安装 @anthropic-ai/claude-code 并执行对话后将自动纳入统计");
        }

        // 在桌面端里运行的 Claude Code 会话写在 ~/.claude，界面里按「Claude Desktop」显示，但文件由 Claude Code 一行统计。
        // 桌面端自有目录为空时说明数据去向，避免与请求明细里的 Claude Desktop 记录互相矛盾。
        private static void NoteDesktopSessionsUnderClaudeCode(List<DataSourceProbeItem> probes)
        {
            DataSourceProbeItem code = probes.Find(delegate(DataSourceProbeItem p) { return p != null && p.Name == "Claude Code"; });
            DataSourceProbeItem desk = probes.Find(delegate(DataSourceProbeItem p) { return p != null && p.Name == "Claude Desktop"; });
            if (code == null || desk == null) return;
            if (code.Status != DataSourceProbeItem.StatusActive || desk.Status != DataSourceProbeItem.StatusInstalledEmpty) return;
            desk.StatusLabel = "并入 Claude Code";
            desk.DiagnosticHint = "桌面端里运行的 Claude Code 会话写在 ~/.claude，已由上方 Claude Code 一行统计，请求明细中显示为 Claude Desktop；桌面端自有目录暂无本地会话";
        }

        private static DataSourceProbeItem ProbeClaudeDesktop(string home, string roaming, string localApp)
        {
            string stdClaude = CombineSafe(roaming, "Claude");
            ProbeAccum acc = new ProbeAccum(stdClaude);

            string[] subNames = new string[] { "projects", "transcripts", "claude-code-sessions", "local-agent-mode-sessions" };
            for (int i = 0; i < subNames.Length; i++)
            {
                ScanDirIntoAccum(acc, CombineSafe(stdClaude, subNames[i]), "*.jsonl", true, null);
            }
            if (!acc.AnyRootExists && SafeDirectoryExists(stdClaude, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = stdClaude;
            }

            // 探测 Windows Store MSIX 沙盒目录 Packages\Claude_*
            string pkgBase = CombineSafe(localApp, "Packages");
            bool dummyDenied;
            if (SafeDirectoryExists(pkgBase, out dummyDenied))
            {
                try
                {
                    foreach (string pkgDir in Directory.EnumerateDirectories(TokenScanner.ToLongPath(pkgBase), "Claude_*"))
                    {
                        string cleanPkg = StripLongPathPrefix(pkgDir);
                        string lcRoaming = CombineSafe(CombineSafe(cleanPkg, "LocalCache"), "Roaming");
                        string lcClaude = CombineSafe(lcRoaming, "Claude");
                        bool subDenied;
                        if (SafeDirectoryExists(lcClaude, out subDenied))
                        {
                            acc.AnyRootExists = true;
                            if (string.IsNullOrEmpty(acc.BestExistingPath))
                                acc.BestExistingPath = lcClaude;
                        }
                        for (int i = 0; i < subNames.Length; i++)
                        {
                            ScanDirIntoAccum(acc, CombineSafe(lcClaude, subNames[i]), "*.jsonl", true, null);
                        }
                        ScanDirIntoAccum(acc, CombineSafe(CombineSafe(lcRoaming, ".claude"), "projects"), "*.jsonl", true, null);
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(home))
            {
                string altRoaming = CombineSafe(CombineSafe(CombineSafe(home, "AppData"), "Roaming"), "Claude");
                if (!string.Equals(altRoaming, stdClaude, StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < subNames.Length; i++)
                        ScanDirIntoAccum(acc, CombineSafe(altRoaming, subNames[i]), "*.jsonl", true, null);
                }
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Claude Desktop",
                acc,
                "自动识别标准安装包 (%APPDATA%\\Claude) 与微软商店 MSIX 沙盒目录的 Code / 本地 Agent 会话",
                "已安装 Claude 桌面端，但尚未开启本地 Code / Agent 模式（纯云端网页流式聊天不在本地落盘 JSONL）",
                "未检测到 Claude Desktop 本地存储目录");
        }

        private static DataSourceProbeItem ProbeCodexCli(string home)
        {
            string codexDir = CombineSafe(home, ".codex");
            ProbeAccum acc = new ProbeAccum(codexDir);

            ScanDirIntoAccum(acc, CombineSafe(codexDir, "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(codexDir, "archived_sessions"), "*.jsonl", true, null);
            ScanSqliteFileIntoAccum(acc, CombineSafe(codexDir, "state_5.sqlite"));

            if (!acc.AnyRootExists && SafeDirectoryExists(codexDir, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = codexDir;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Codex CLI (OpenAI)",
                acc,
                "双通道采集：实时追踪 sessions/*.jsonl 增量流，并对已清理会话自动回退读取 state_5.sqlite 归档库",
                "已检测到 ~/.codex 目录，暂无活跃 JSONL 会话或 SQLite 归档记录",
                "未检测到 ~/.codex 目录；运行 OpenAI Codex CLI 后自动捕获 Token 与缓存命中明细");
        }

        private static DataSourceProbeItem ProbeGeminiCli(string home)
        {
            string geminiRoot = CombineSafe(home, ".gemini");
            string tmpDir = CombineSafe(geminiRoot, "tmp");
            ProbeAccum acc = new ProbeAccum(tmpDir);

            ScanDirIntoAccum(acc, tmpDir, "*.json", true, delegate(string fp)
            {
                return fp.IndexOf("\\chats\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       fp.IndexOf("/chats/", StringComparison.OrdinalIgnoreCase) >= 0;
            });

            bool denied;
            if (!acc.AnyRootExists && SafeDirectoryExists(geminiRoot, out denied))
            {
                acc.AnyRootExists = true;
                acc.AccessDenied = acc.AccessDenied || denied;
                acc.BestExistingPath = geminiRoot;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Gemini CLI",
                acc,
                "扫描 ~/.gemini/tmp/**/chats/*.json 会话快照，自动合并 thoughts 思维链与 cached 缓存读取 Token",
                "已检测到 ~/.gemini 目录，但 tmp/**/chats 下暂无 Gemini CLI 本地对话文件",
                "未检测到 ~/.gemini/tmp 目录");
        }

        private static DataSourceProbeItem ProbeKimiCode(string home)
        {
            string kimiCode = CombineSafe(home, ".kimi-code");
            string kimiAlt = CombineSafe(home, ".kimi");
            ProbeAccum acc = new ProbeAccum(kimiCode);

            ScanDirIntoAccum(acc, CombineSafe(kimiCode, "sessions"), "wire.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(kimiAlt, "sessions"), "wire.jsonl", true, null);

            bool d1, d2;
            if (!acc.AnyRootExists && SafeDirectoryExists(kimiCode, out d1))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = kimiCode;
            }
            else if (!acc.AnyRootExists && SafeDirectoryExists(kimiAlt, out d2))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = kimiAlt;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Kimi Code",
                acc,
                "流式解析 sessions/**/wire.jsonl 中的 llm.request 与 usage.record 通信帧，精准还原缓存读写",
                "已检测到 Kimi CLI 目录，尚未生成 wire.jsonl 会话记录",
                "未检测到 ~/.kimi-code 或 ~/.kimi 目录");
        }

        private static DataSourceProbeItem ProbeZCode(string home)
        {
            string zRoot = CombineSafe(home, ".zcode");
            string zDb = CombineSafe(CombineSafe(CombineSafe(zRoot, "cli"), "db"), "db.sqlite");
            ProbeAccum acc = new ProbeAccum(zRoot);

            ScanSqliteFileIntoAccum(acc, zDb);
            if (!acc.AnyRootExists && SafeDirectoryExists(zRoot, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = zRoot;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "ZCode (智谱 CLI)",
                acc,
                "增量只读查询 ~/.zcode/cli/db/db.sqlite 消息库（含 WAL 热日志合并统计）",
                "已检测到 ~/.zcode 目录，尚未初始化 db.sqlite 消息记录",
                "未检测到 ~/.zcode 目录");
        }

        private static DataSourceProbeItem ProbeQwenCode(string home)
        {
            string qwenRoot = CombineSafe(home, ".qwen");
            ProbeAccum acc = new ProbeAccum(qwenRoot);

            ScanDirIntoAccum(acc, CombineSafe(qwenRoot, "projects"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(qwenRoot, "tmp"), "*.json", true, delegate(string fp)
            {
                return fp.IndexOf("chats", StringComparison.OrdinalIgnoreCase) >= 0;
            });

            if (!acc.AnyRootExists && SafeDirectoryExists(qwenRoot, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = qwenRoot;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Qwen Code",
                acc,
                "联合扫描 ~/.qwen/projects/*.jsonl 与 ~/.qwen/tmp/**/chats/*.json，支持思维链计费",
                "已检测到 ~/.qwen 目录，暂无项目 JSONL 或对话缓存记录",
                "未检测到 ~/.qwen 目录");
        }

        private static DataSourceProbeItem ProbeAider(string home)
        {
            string aiderDir = CombineSafe(home, ".aider");
            ProbeAccum acc = new ProbeAccum(aiderDir);

            ScanSingleFileIntoAccum(acc, CombineSafe(aiderDir, "analytics.jsonl"));
            ScanSingleFileIntoAccum(acc, CombineSafe(home, ".aider.analytics.jsonl"));

            if (!acc.AnyRootExists && SafeDirectoryExists(aiderDir, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = aiderDir;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Aider",
                acc,
                "解析 ~/.aider/analytics.jsonl 遥测流水中的 prompt_tokens、completion_tokens 与实际成本",
                "已检测到 ~/.aider 目录但无 analytics.jsonl；请在 Aider 配置中开启 analytics-log 本地落盘",
                "未检测到 Aider 本地遥测日志 (~/.aider/analytics.jsonl)");
        }

        private static DataSourceProbeItem ProbeOpenClawFamily(string home)
        {
            string defOpenClaw = CombineSafe(home, ".openclaw");
            ProbeAccum acc = new ProbeAccum(defOpenClaw);

            if (!string.IsNullOrEmpty(home))
            {
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(TokenScanner.ToLongPath(home), ".openclaw*"))
                    {
                        string clean = StripLongPathPrefix(dir);
                        acc.AnyRootExists = true;
                        if (string.IsNullOrEmpty(acc.BestExistingPath))
                            acc.BestExistingPath = clean;
                        ScanDirIntoAccum(acc, CombineSafe(clean, "agents"), "*.jsonl", true, null);
                        ScanDirIntoAccum(acc, CombineSafe(clean, "sessions"), "*.jsonl", true, null);
                    }
                }
                catch { }
            }

            string mcDir = CombineSafe(CombineSafe(CombineSafe(CombineSafe(home, ".local"), "share"), "mobileclaw"), "workspaces");
            ScanDirIntoAccum(acc, mcDir, "*.json", true, delegate(string fp)
            {
                return fp.IndexOf("\\message\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       fp.IndexOf("/message/", StringComparison.OrdinalIgnoreCase) >= 0;
            });

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "OpenClaw / MobileClaw",
                acc,
                "支持 ~/.openclaw* 多实例通配穿透与 MobileClaw 工作区 storage/message 自动去重",
                "已检测到 OpenClaw / MobileClaw 目录，暂无会话日志文件",
                "未检测到 ~/.openclaw* 或 MobileClaw 工作区目录");
        }

        private static DataSourceProbeItem ProbeWorkBuddyFamily(string home)
        {
            string wb = CombineSafe(home, ".workbuddy");
            ProbeAccum acc = new ProbeAccum(wb);

            string[] roots = new string[]
            {
                wb,
                CombineSafe(home, ".workbuddy-ai"),
                CombineSafe(home, ".codebuddy")
            };
            for (int i = 0; i < roots.Length; i++)
            {
                bool denied;
                if (SafeDirectoryExists(roots[i], out denied))
                {
                    acc.AnyRootExists = true;
                    acc.AccessDenied = acc.AccessDenied || denied;
                    if (string.IsNullOrEmpty(acc.BestExistingPath))
                        acc.BestExistingPath = roots[i];
                }
                ScanDirIntoAccum(acc, CombineSafe(roots[i], "projects"), "*.jsonl", true, null);
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "WorkBuddy / CodeBuddy",
                acc,
                "读取 settings.json 默认模型映射并递归解析 projects/*.jsonl 对话日志",
                "已检测到 WorkBuddy / CodeBuddy 目录，projects 下暂无会话文件",
                "未检测到 ~/.workbuddy 或 ~/.codebuddy 目录");
        }

        private static DataSourceProbeItem ProbeDshAndCommandCode(string home)
        {
            string dsh = CombineSafe(home, ".dsh");
            string cmdCode = CombineSafe(home, ".commandcode");
            ProbeAccum acc = new ProbeAccum(dsh);

            ScanDirIntoAccum(acc, CombineSafe(dsh, "storages"), "session-*.json", true, null);
            ScanDirIntoAccum(acc, CombineSafe(cmdCode, "projects"), "*.jsonl", true, null);

            bool d1, d2;
            if (!acc.AnyRootExists && SafeDirectoryExists(dsh, out d1))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = dsh;
            }
            else if (!acc.AnyRootExists && SafeDirectoryExists(cmdCode, out d2))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = cmdCode;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "DeepSeek DSH / Command Code",
                acc,
                "捕获 ~/.dsh/storages/session-*.json 与 ~/.commandcode/projects/*.jsonl 用量事件",
                "已检测到目录，尚未生成本地会话 JSON/JSONL 记录",
                "未检测到 ~/.dsh 或 ~/.commandcode 目录");
        }

        private static DataSourceProbeItem ProbeEmergingAgentClis(string home, string roaming, string localApp)
        {
            string factoryDir = CombineSafe(CombineSafe(home, ".factory"), "sessions");
            ProbeAccum acc = new ProbeAccum(CombineSafe(home, ".factory"));

            // Factory Droid
            ScanDirIntoAccum(acc, factoryDir, "*.jsonl", true, null);
            ScanDirIntoAccum(acc, factoryDir, "*.json", true, null);

            // Sourcegraph Amp
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(CombineSafe(CombineSafe(home, ".local"), "share"), "amp"), "threads"), "*.json", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(home, ".amp"), "threads"), "*.json", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(localApp, "amp"), "threads"), "*.json", true, null);

            // Block Goose
            string gooseUnix = CombineSafe(CombineSafe(CombineSafe(CombineSafe(home, ".local"), "share"), "goose"), "sessions");
            string gooseWin = CombineSafe(CombineSafe(CombineSafe(CombineSafe(roaming, "Block"), "goose"), "data"), "sessions");
            ScanDirIntoAccum(acc, gooseUnix, "*.jsonl", true, null);
            ScanSqliteFileIntoAccum(acc, CombineSafe(gooseUnix, "sessions.db"));
            ScanDirIntoAccum(acc, gooseWin, "*.jsonl", true, null);
            ScanSqliteFileIntoAccum(acc, CombineSafe(gooseWin, "sessions.db"));

            // Pi / Oh My Pi / Mistral Vibe / OpenHands / Hermes
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(CombineSafe(home, ".pi"), "agent"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(home, ".pi"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(CombineSafe(home, ".omp"), "agent"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(home, ".omp"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(CombineSafe(home, ".vibe"), "logs"), "session"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(home, ".openhands"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(home, ".hermes"), "sessions"), "*.jsonl", true, null);

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryNativeCli,
                "Factory / Amp / Goose / Pi 等",
                acc,
                "聚合探测 Factory Droid、Sourcegraph Amp、Block Goose、Pi Agent、OpenHands 与 Mistral Vibe",
                "已检测到部分新兴 Agent CLI 目录，暂无有效会话日志文件",
                "未检测到 Factory Droid / Amp / Goose / Pi / OpenHands 本地会话目录");
        }

        private static DataSourceProbeItem ProbeAntigravityIde(string home)
        {
            string agyRoot = CombineSafe(CombineSafe(home, ".gemini"), "antigravity");
            string brainDir = CombineSafe(agyRoot, "brain");
            ProbeAccum acc = new ProbeAccum(brainDir);

            bool denied;
            if (SafeDirectoryExists(brainDir, out denied))
            {
                acc.AnyRootExists = true;
                acc.AccessDenied = denied;
                acc.BestExistingPath = brainDir;
                try
                {
                    int convScanned = 0;
                    foreach (string convDir in Directory.EnumerateDirectories(TokenScanner.ToLongPath(brainDir)))
                    {
                        if (acc.FileCount >= MaxFilesPerProbe || convScanned >= MaxDirectoriesPerRoot)
                        {
                            if (acc.FileCount >= MaxFilesPerProbe) acc.Capped = true;
                            break;
                        }
                        convScanned++;
                        string cleanConv = StripLongPathPrefix(convDir);
                        string tf = CombineSafe(CombineSafe(CombineSafe(cleanConv, ".system_generated"), "logs"), "transcript.jsonl");
                        long sz;
                        long mt;
                        if (TokenScanner.TryGetFileStatInternal(tf, out sz, out mt))
                        {
                            acc.FileCount++;
                            acc.TotalBytes += sz;
                            if (mt > acc.LatestMtimeTicks) acc.LatestMtimeTicks = mt;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    acc.AccessDenied = true;
                }
                catch { }
            }
            else if (SafeDirectoryExists(agyRoot, out denied))
            {
                acc.AnyRootExists = true;
                acc.AccessDenied = denied;
                acc.BestExistingPath = agyRoot;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Antigravity IDE",
                acc,
                "穿透扫描 brain/*/.system_generated/logs/transcript.jsonl，支持模型切换回填与子智能体模型继承",
                "已检测到 ~/.gemini/antigravity 目录，尚未生成 transcript.jsonl 会话轨迹",
                "未检测到 Google Antigravity IDE 本地会话目录 (~/.gemini/antigravity/brain)");
        }

        private static DataSourceProbeItem ProbeCursorStateDb(string roaming)
        {
            string cursorUser = CombineSafe(CombineSafe(roaming, "Cursor"), "User");
            string globalStorage = CombineSafe(cursorUser, "globalStorage");
            string vscdb = CombineSafe(globalStorage, "state.vscdb");
            ProbeAccum acc = new ProbeAccum(globalStorage);

            ScanSqliteFileIntoAccum(acc, vscdb);
            if (!acc.AnyRootExists && SafeDirectoryExists(globalStorage, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = globalStorage;
            }

            string activeHint = "采用 SQLite URI 零拷贝只读穿透 (mode=ro / immutable=1)，无需关闭运行中的 Cursor 即可无锁读取 cursorDiskKV 气泡明细";
            if (acc.TotalBytes >= 120L * 1024L * 1024L)
            {
                activeHint = string.Format(
                    CultureInfo.InvariantCulture,
                    "检测到大型数据库 ({0})：已自动启用 SQLite URI 零拷贝只读穿透，避免 %TEMP% 临时拷贝膨胀且不阻塞 IDE",
                    FormatBytes(acc.TotalBytes));
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Cursor (state.vscdb)",
                acc,
                activeHint,
                "已检测到 Cursor 目录，尚未生成 globalStorage\\state.vscdb 状态数据库",
                "未检测到 %APPDATA%\\Cursor 目录");
        }

        private static DataSourceProbeItem ProbeWindsurfIde(string home, string roaming)
        {
            string wsGlobal = CombineSafe(CombineSafe(CombineSafe(roaming, "Windsurf"), "User"), "globalStorage");
            string codeiumWs = CombineSafe(CombineSafe(home, ".codeium"), "windsurf");
            ProbeAccum acc = new ProbeAccum(wsGlobal);

            bool d1, d2;
            bool hasWs = SafeDirectoryExists(wsGlobal, out d1);
            bool hasCodeium = SafeDirectoryExists(codeiumWs, out d2);
            acc.AccessDenied = d1 || d2;

            if (hasWs)
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = wsGlobal;
            }
            else if (hasCodeium)
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = codeiumWs;
            }

            // 探测 Windsurf 内安装的开源计费插件日志
            string[] extIds = new string[] { "saoudrizwan.claude-dev", "rooveterinaryinc.roo-cline", "kilocode.kilo-code" };
            for (int i = 0; i < extIds.Length; i++)
            {
                ScanDirIntoAccum(acc, CombineSafe(CombineSafe(wsGlobal, extIds[i]), "tasks"), "ui_messages.json", true, null);
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Windsurf IDE",
                acc,
                "已捕获 Windsurf 内置扩展任务日志；注：官方 Cascade 原生对话由云端闭源计费，不在本地落盘明文 Token",
                "已安装 Windsurf；官方原生 Cascade 对话仅在云端计费不落盘明文 Token（安装 Cline/Roo 插件可本地统计）",
                "未检测到 %APPDATA%\\Windsurf 或 ~/.codeium/windsurf 目录");
        }

        private static DataSourceProbeItem ProbeVsixExtensionAcrossIdes(string roaming, string displayName, string extFolderId)
        {
            string[] ides = new string[] { "Code", "Cursor", "Windsurf", "Antigravity" };
            string defaultFallback = CombineSafe(CombineSafe(CombineSafe(CombineSafe(roaming, "Code"), "User"), "globalStorage"), extFolderId);
            ProbeAccum acc = new ProbeAccum(defaultFallback);
            List<string> foundIdes = new List<string>();

            for (int i = 0; i < ides.Length; i++)
            {
                string extDir = CombineSafe(CombineSafe(CombineSafe(CombineSafe(roaming, ides[i]), "User"), "globalStorage"), extFolderId);
                bool denied;
                if (SafeDirectoryExists(extDir, out denied))
                {
                    acc.AnyRootExists = true;
                    acc.AccessDenied = acc.AccessDenied || denied;
                    foundIdes.Add(ides[i]);
                    if (string.IsNullOrEmpty(acc.BestExistingPath))
                        acc.BestExistingPath = extDir;

                    int before = acc.FileCount;
                    ScanDirIntoAccum(acc, CombineSafe(extDir, "tasks"), "ui_messages.json", true, null);
                    if (acc.FileCount > before)
                        acc.BestExistingPath = extDir;
                }
            }

            string ideJoined = foundIdes.Count > 0 ? string.Join(" / ", foundIdes.ToArray()) : "VS Code / Cursor / Windsurf / AGY";
            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                displayName,
                acc,
                string.Format(CultureInfo.InvariantCulture, "已穿透宿主编辑器 ({0}) 提取 tasks/*/ui_messages.json 的 api_req_started 精确账单", ideJoined),
                string.Format(CultureInfo.InvariantCulture, "已在 {0} 检测到扩展目录 ({1})，暂无历史任务记录", ideJoined, extFolderId),
                string.Format(CultureInfo.InvariantCulture, "未在 VS Code / Cursor / Windsurf / Antigravity 中检测到 {0} 扩展存储", extFolderId));
        }

        private static DataSourceProbeItem ProbeOpenCode(string home, string localApp)
        {
            string unixOc = CombineSafe(CombineSafe(CombineSafe(home, ".local"), "share"), "opencode");
            string winOc = CombineSafe(localApp, "opencode");
            ProbeAccum acc = new ProbeAccum(unixOc);

            string[] bases = new string[] { unixOc, winOc };
            for (int b = 0; b < bases.Length; b++)
            {
                bool denied;
                if (!SafeDirectoryExists(bases[b], out denied))
                    continue;
                acc.AnyRootExists = true;
                acc.AccessDenied = acc.AccessDenied || denied;
                if (string.IsNullOrEmpty(acc.BestExistingPath))
                    acc.BestExistingPath = bases[b];

                try
                {
                    foreach (string dbFile in Directory.EnumerateFiles(TokenScanner.ToLongPath(bases[b]), "opencode*.db"))
                    {
                        ScanSqliteFileIntoAccum(acc, StripLongPathPrefix(dbFile));
                    }
                }
                catch { }

                ScanDirIntoAccum(acc, CombineSafe(CombineSafe(bases[b], "storage"), "message"), "*.json", true, null);
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "OpenCode",
                acc,
                "双路并发读取 opencode*.db SQLite 消息库与 storage/message/*.json，按消息 ID 严格去重",
                "已检测到 OpenCode 存储目录，暂无 assistant 消息 Token 记录",
                "未检测到 ~/.local/share/opencode 或 %LOCALAPPDATA%\\opencode 目录");
        }

        private static DataSourceProbeItem ProbeZedEditor(string localApp)
        {
            string zedRoot = CombineSafe(localApp, "Zed");
            string zedDb = CombineSafe(CombineSafe(zedRoot, "threads"), "threads.db");
            ProbeAccum acc = new ProbeAccum(zedRoot);

            ScanSqliteFileIntoAccum(acc, zedDb);
            if (!acc.AnyRootExists && SafeDirectoryExists(zedRoot, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = zedRoot;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Zed (threads.db)",
                acc,
                "只读穿透 %LOCALAPPDATA%\\Zed\\threads\\threads.db，提取 cumulative_token_usage 四维用量",
                "已安装 Zed 编辑器，但尚未产生 AI Assistant 本地线程数据库 (threads.db)",
                "未检测到 %LOCALAPPDATA%\\Zed 目录");
        }

        private static DataSourceProbeItem ProbeContinueDev(string home)
        {
            string contRoot = CombineSafe(home, ".continue");
            string sessDir = CombineSafe(contRoot, "sessions");
            ProbeAccum acc = new ProbeAccum(contRoot);

            ScanDirIntoAccum(acc, sessDir, "*.json", true, null);
            if (!acc.AnyRootExists && SafeDirectoryExists(contRoot, out acc.AccessDenied))
            {
                acc.AnyRootExists = true;
                acc.BestExistingPath = contRoot;
            }

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Continue.dev 扩展",
                acc,
                "扫描 ~/.continue/sessions/*.json 对话历史中的 promptLogs 调用记录（兼容 VS Code 与 JetBrains）",
                "已检测到 ~/.continue 目录，sessions 下暂无包含 promptLogs 的会话文件",
                "未检测到 ~/.continue 目录");
        }

        private static DataSourceProbeItem ProbeCharmCrush(string home, string localApp)
        {
            string unixDb = CombineSafe(CombineSafe(CombineSafe(CombineSafe(home, ".local"), "share"), "crush"), "crush.db");
            string winDb = CombineSafe(CombineSafe(localApp, "crush"), "crush.db");
            ProbeAccum acc = new ProbeAccum(CombineSafe(localApp, "crush"));

            ScanSqliteFileIntoAccum(acc, unixDb);
            ScanSqliteFileIntoAccum(acc, winDb);

            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Charm Crush (crush.db)",
                acc,
                "只读查询 crush.db 本地 SQLite sessions 表的 prompt_tokens、completion_tokens 与花费",
                "已检测到 Crush 目录，暂无有效数据库文件",
                "未检测到 Charm Crush 本地数据库 (crush.db)");
        }

        private static DataSourceProbeItem ProbeTraeKiroQoder(string roaming)
        {
            string[] names = new string[] { "Trae", "Kiro", "Qoder" };
            ProbeAccum acc = new ProbeAccum(CombineSafe(roaming, "Trae"));
            List<string> installed = new List<string>();

            for (int i = 0; i < names.Length; i++)
            {
                string ideRoot = CombineSafe(roaming, names[i]);
                string gs = CombineSafe(CombineSafe(ideRoot, "User"), "globalStorage");
                bool denied;
                if (SafeDirectoryExists(gs, out denied) || SafeDirectoryExists(ideRoot, out denied))
                {
                    acc.AnyRootExists = true;
                    acc.AccessDenied = acc.AccessDenied || denied;
                    installed.Add(names[i]);
                    if (string.IsNullOrEmpty(acc.BestExistingPath))
                        acc.BestExistingPath = SafeDirectoryExists(gs, out denied) ? gs : ideRoot;

                    // 探测内部是否安装了兼容 Cline 生态的扩展
                    string[] extIds = new string[] { "saoudrizwan.claude-dev", "rooveterinaryinc.roo-cline", "kilocode.kilo-code" };
                    for (int e = 0; e < extIds.Length; e++)
                    {
                        ScanDirIntoAccum(acc, CombineSafe(CombineSafe(gs, extIds[e]), "tasks"), "ui_messages.json", true, null);
                    }
                }
            }

            string instLabel = installed.Count > 0 ? string.Join(" / ", installed.ToArray()) : "Trae / Kiro / Qoder";
            return BuildStandardProbe(
                DataSourceProbeItem.CategoryIdeDb,
                "Trae / Kiro / Qoder",
                acc,
                string.Format(CultureInfo.InvariantCulture, "已在 {0} 内捕获第三方计费插件日志；其内置原生对话为云端私有计费不落盘明文 Token", instLabel),
                string.Format(CultureInfo.InvariantCulture, "已检测到 {0} 安装目录；其官方内置对话由云端闭源计费不在本地落盘 Token，自定义日志可配 ~/.toklens/harnesses.json", instLabel),
                "未检测到 Trae / Kiro / Qoder 本地 AppData 目录");
        }

        // -------------------- WSL2 注册表 Lxss + \\wsl.localhost\<distro> 安全穿透 --------------------

        private static void InspectWslSubsystem(SystemHealthReport report, List<DataSourceProbeItem> probes)
        {
            List<string> regDistros = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 注意：.NET Framework 4.x 中绝对不能直接对根 \\wsl.localhost 调用 Directory.Exists，
            // 必须先读取 HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss 下的 DistributionName，再拼接 \\wsl.localhost\<name> 验证！
            try
            {
                using (RegistryKey lxssKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss", false))
                {
                    if (lxssKey != null)
                    {
                        string[] subKeys = lxssKey.GetSubKeyNames();
                        for (int i = 0; i < subKeys.Length; i++)
                        {
                            try
                            {
                                using (RegistryKey distroKey = lxssKey.OpenSubKey(subKeys[i], false))
                                {
                                    if (distroKey == null) continue;
                                    string dName = distroKey.GetValue("DistributionName") as string;
                                    if (string.IsNullOrWhiteSpace(dName)) continue;
                                    dName = dName.Trim();

                                    // 过滤 Docker Desktop / Rancher 内部无用户日志的数据容器卷
                                    if (string.Equals(dName, "docker-desktop-data", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(dName, "docker-desktop", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(dName, "rancher-desktop-data", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    if (seen.Add(dName))
                                        regDistros.Add(dName);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            if (regDistros.Count == 0)
            {
                DataSourceProbeItem noneItem = new DataSourceProbeItem();
                noneItem.Category = DataSourceProbeItem.CategoryWsl;
                noneItem.Name = "WSL2 发行版穿透";
                noneItem.Status = DataSourceProbeItem.StatusNotInstalled;
                noneItem.StatusLabel = "未启用 WSL2";
                noneItem.TargetPath = @"\\wsl.localhost";
                noneItem.DiagnosticHint = "本机注册表 HKCU\\...\\Lxss 未检测到已安装的 WSL2 Linux 发行版；纯 Windows 原生用户无需配置";
                probes.Add(noneItem);
                return;
            }

            for (int d = 0; d < regDistros.Count; d++)
            {
                string distro = regDistros[d];
                report.WslDistroNames.Add(distro);

                string uncModern = @"\\wsl.localhost\" + distro;
                string uncLegacy = @"\\wsl$\" + distro;
                string activeRoot = "";

                bool denied = false;
                if (SafeDirectoryExists(uncModern + @"\home", out denied) || SafeDirectoryExists(uncModern + @"\etc", out denied))
                {
                    activeRoot = uncModern;
                }
                else if (SafeDirectoryExists(uncLegacy + @"\home", out denied) || SafeDirectoryExists(uncLegacy + @"\etc", out denied))
                {
                    activeRoot = uncLegacy;
                }

                if (string.IsNullOrEmpty(activeRoot))
                {
                    DataSourceProbeItem sleepingItem = new DataSourceProbeItem();
                    sleepingItem.Category = DataSourceProbeItem.CategoryWsl;
                    sleepingItem.Name = "WSL2 · " + distro;
                    sleepingItem.Status = DataSourceProbeItem.StatusInstalledEmpty;
                    sleepingItem.StatusLabel = "休眠未挂载";
                    sleepingItem.TargetPath = uncModern;
                    sleepingItem.DiagnosticHint = string.Format(
                        CultureInfo.InvariantCulture,
                        "已从注册表 Lxss 识别发行版 {0}，当前 9P 网络路径 ({1}) 处于休眠未唤醒状态；打开一次 WSL 终端后即可穿透扫描",
                        distro,
                        uncModern);
                    probes.Add(sleepingItem);
                    continue;
                }

                report.WslMountedDistros.Add(distro);
                string wslHomeBase = activeRoot + @"\home";
                ProbeAccum acc = new ProbeAccum(wslHomeBase);
                acc.AnyRootExists = true;
                acc.BestExistingPath = SafeDirectoryExists(wslHomeBase, out denied) ? wslHomeBase : activeRoot;

                try
                {
                    if (SafeDirectoryExists(wslHomeBase, out denied))
                    {
                        foreach (string userHomeRaw in Directory.EnumerateDirectories(wslHomeBase))
                        {
                            string wh = StripLongPathPrefix(userHomeRaw);
                            ScanWslUserHomeIntoAccum(acc, wh);
                            if (acc.FileCount >= MaxFilesPerProbe) break;
                        }
                    }
                }
                catch { }

                DataSourceProbeItem wslProbe = new DataSourceProbeItem();
                wslProbe.Category = DataSourceProbeItem.CategoryWsl;
                wslProbe.Name = "WSL2 · " + distro;
                wslProbe.TargetPath = !string.IsNullOrEmpty(acc.BestExistingPath) ? acc.BestExistingPath : uncModern;
                wslProbe.FileCount = acc.FileCount;
                wslProbe.FileCountCapped = acc.Capped;
                wslProbe.TotalBytes = acc.TotalBytes;
                wslProbe.LastModifiedTicks = acc.LatestMtimeTicks;

                if (acc.FileCount > 0)
                {
                    if (!report.ScanLocalWslEnabled)
                    {
                        wslProbe.Status = DataSourceProbeItem.StatusWarning;
                        wslProbe.StatusLabel = string.Format(
                            CultureInfo.InvariantCulture,
                            "开关未开 ({0}{1} 个文件)",
                            acc.FileCount,
                            acc.Capped ? "+" : "");
                        wslProbe.DiagnosticHint = string.Format(
                            CultureInfo.InvariantCulture,
                            "已在 {0} 发现 {1} 个 AI 日志文件，但当前未勾选「扫描本机 WSL2 子系统」！请在云同步/设置中开启开关以纳入统计",
                            distro,
                            acc.FileCount);
                    }
                    else
                    {
                        wslProbe.Status = DataSourceProbeItem.StatusActive;
                        wslProbe.StatusLabel = string.Format(
                            CultureInfo.InvariantCulture,
                            "已穿透 ({0}{1} 个文件)",
                            acc.FileCount,
                            acc.Capped ? "+" : "");
                        wslProbe.DiagnosticHint = string.Format(
                            CultureInfo.InvariantCulture,
                            "已通过 {0}\\home 跨子系统无感穿透扫描 Claude Code / Codex / Qwen / OpenClaw / Aider 等 Linux 端日志",
                            activeRoot);
                    }
                }
                else
                {
                    wslProbe.Status = DataSourceProbeItem.StatusInstalledEmpty;
                    wslProbe.StatusLabel = "已挂载 · 暂无日志";
                    wslProbe.DiagnosticHint = string.Format(
                        CultureInfo.InvariantCulture,
                        "WSL2 发行版 {0} 9P 路径连通正常，/home 下暂未发现 Claude / Codex / Qwen / Aider 等 AI 会话文件",
                        distro);
                }

                probes.Add(wslProbe);
            }
        }

        private static void ScanWslUserHomeIntoAccum(ProbeAccum acc, string wh)
        {
            if (string.IsNullOrEmpty(wh)) return;
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(wh, ".claude"), "projects"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(wh, ".codex"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(wh, ".qwen"), "projects"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(wh, ".factory"), "sessions"), "*.jsonl", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(CombineSafe(CombineSafe(wh, ".local"), "share"), "amp"), "threads"), "*.json", true, null);
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(CombineSafe(CombineSafe(wh, ".local"), "share"), "goose"), "sessions"), "*.jsonl", true, null);
            ScanSingleFileIntoAccum(acc, CombineSafe(CombineSafe(wh, ".aider"), "analytics.jsonl"));
            ScanDirIntoAccum(acc, CombineSafe(CombineSafe(wh, ".pi"), "sessions"), "*.jsonl", true, null);
            try
            {
                foreach (string ocDir in Directory.EnumerateDirectories(wh, ".openclaw*"))
                {
                    ScanDirIntoAccum(acc, CombineSafe(ocDir, "agents"), "*.jsonl", true, null);
                    ScanDirIntoAccum(acc, CombineSafe(ocDir, "sessions"), "*.jsonl", true, null);
                }
            }
            catch { }
        }

        // -------------------- 云同步与本地存储健康探针 --------------------

        private static DataSourceProbeItem InspectLocalSqliteLedger(SystemHealthReport report)
        {
            string primaryDb = LocalUsageStore.DbFilePath;
            string altLedgerDb = CombineSafe(SyncService.AppConfigDir, "ledger.db");
            string chosenPath = primaryDb;

            long mainSz = 0L;
            long mainMt = 0L;
            bool exists = TokenScanner.TryGetFileStatInternal(primaryDb, out mainSz, out mainMt);
            if (!exists && TokenScanner.TryGetFileStatInternal(altLedgerDb, out mainSz, out mainMt))
            {
                chosenPath = altLedgerDb;
                exists = true;
            }

            long combinedBytes = mainSz;
            long maxMt = mainMt;
            if (exists)
            {
                string[] sidecars = new string[] { "-wal", "-shm" };
                for (int i = 0; i < sidecars.Length; i++)
                {
                    long sSz;
                    long sMt;
                    if (TokenScanner.TryGetFileStatInternal(chosenPath + sidecars[i], out sSz, out sMt))
                    {
                        combinedBytes += sSz;
                        if (sMt > maxMt) maxMt = sMt;
                    }
                }
            }

            report.LedgerDbPath = chosenPath;
            report.LedgerDbBytes = combinedBytes;
            report.LedgerDbSizeText = FormatBytes(combinedBytes);

            DataSourceProbeItem item = new DataSourceProbeItem();
            item.Category = DataSourceProbeItem.CategoryCloudStore;
            item.Name = "本地 SQLite 账本引擎";
            item.TargetPath = exists ? chosenPath : SyncService.AppConfigDir;
            item.TotalBytes = combinedBytes;
            item.LastModifiedTicks = maxMt;

            if (!exists)
            {
                report.LedgerDbHealthy = true;
                item.Status = DataSourceProbeItem.StatusInstalledEmpty;
                item.StatusLabel = "待初始化";
                item.FileCount = 0;
                item.DiagnosticHint = "首次扫描落盘后将在 %APPDATA%\\TokLens 自动创建 WAL 模式增量游标与日归档账本数据库";
                return item;
            }

            item.FileCount = 1;
            bool healthy = false;
            int cursorRows = 0;
            int archiveRows = 0;

            try
            {
                using (WinSqlite db = WinSqlite.OpenReadOnly(chosenPath))
                {
                    if (db != null && db.IsHealthy())
                    {
                        healthy = true;
                        db.Query("SELECT COUNT(1) FROM file_cursors", delegate(WinSqlite.SqliteRow r)
                        {
                            cursorRows = (int)r.GetInt64(0);
                        });
                        db.Query("SELECT COUNT(1) FROM daily_archive", delegate(WinSqlite.SqliteRow r)
                        {
                            archiveRows = (int)r.GetInt64(0);
                        });
                    }
                }
            }
            catch { }

            report.LedgerDbHealthy = healthy;
            report.TrackedCursorCount = cursorRows;
            report.DailyArchiveRowCount = archiveRows;

            if (healthy)
            {
                item.Status = DataSourceProbeItem.StatusActive;
                item.StatusLabel = "健康 (WAL 模式)";
                item.DiagnosticHint = string.Format(
                    CultureInfo.InvariantCulture,
                    "SQLite 引擎读写正常 · 已持久追踪 {0} 个文件游标与 {1} 条高水位历史日归档（源日志清理后账单不丢失）",
                    cursorRows,
                    archiveRows);
            }
            else
            {
                item.Status = DataSourceProbeItem.StatusWarning;
                item.StatusLabel = "需关注 (校验异常)";
                item.DiagnosticHint = "账本数据库当前被独占锁定或校验失败；为保护历史已停止覆盖，可稍后重试或从时光机备份恢复";
            }

            return item;
        }

        private static DataSourceProbeItem InspectCustomPricingAndHarnesses(string home, string roaming, SystemHealthReport report)
        {
            string appDir = SyncService.AppConfigDir;
            string p1 = CombineSafe(CombineSafe(home, ".toklens"), "custom_prices.json");
            string p2 = CombineSafe(appDir, "custom_prices.json");
            string p3 = !string.IsNullOrEmpty(roaming) ? CombineSafe(CombineSafe(roaming, "TokLens"), "custom_prices.json") : "";
            string harnessPath = CombineSafe(CombineSafe(home, ".toklens"), "harnesses.json");
            string cloudCachePath = PricingEngine.CloudPriceCachePath;

            HashSet<string> customModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string activeCustomFile = "";
            long totalBytes = 0L;
            long latestMt = 0L;
            int fileCount = 0;

            string[] priceFiles = new string[] { p1, p2, p3 };
            HashSet<string> checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JavaScriptSerializer js = null;

            for (int i = 0; i < priceFiles.Length; i++)
            {
                string fp = priceFiles[i];
                if (string.IsNullOrWhiteSpace(fp) || !checkedPaths.Add(fp)) continue;
                long sz;
                long mt;
                if (!TokenScanner.TryGetFileStatInternal(fp, out sz, out mt)) continue;

                fileCount++;
                totalBytes += sz;
                if (mt > latestMt) latestMt = mt;
                activeCustomFile = fp;

                try
                {
                    if (js == null)
                    {
                        js = new JavaScriptSerializer();
                        js.MaxJsonLength = int.MaxValue;
                    }
                    Dictionary<string, object> map = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                    if (map != null)
                    {
                        foreach (KeyValuePair<string, object> kv in map)
                        {
                            Dictionary<string, object> m = kv.Value as Dictionary<string, object>;
                            if (m == null) continue;
                            double ic = m.ContainsKey("input") && m["input"] != null ? Convert.ToDouble(m["input"], CultureInfo.InvariantCulture) : 0.0;
                            double oc = m.ContainsKey("output") && m["output"] != null ? Convert.ToDouble(m["output"], CultureInfo.InvariantCulture) : 0.0;
                            if (ic > 0.0 || oc > 0.0)
                                customModels.Add(kv.Key.Trim());
                        }
                    }
                }
                catch { }
            }

            int harnessCount = 0;
            long hSz;
            long hMt;
            if (TokenScanner.TryGetFileStatInternal(harnessPath, out hSz, out hMt))
            {
                fileCount++;
                totalBytes += hSz;
                if (hMt > latestMt) latestMt = hMt;
                try
                {
                    if (js == null)
                    {
                        js = new JavaScriptSerializer();
                        js.MaxJsonLength = int.MaxValue;
                    }
                    IList list = js.DeserializeObject(File.ReadAllText(harnessPath, Encoding.UTF8)) as IList;
                    if (list != null) harnessCount = list.Count;
                }
                catch { }
            }

            bool hasCloudCache = false;
            long cSz;
            long cMt;
            if (TokenScanner.TryGetFileStatInternal(cloudCachePath, out cSz, out cMt))
            {
                hasCloudCache = true;
                fileCount++;
                totalBytes += cSz;
                if (cMt > latestMt) latestMt = cMt;
            }

            report.CustomPricesPath = !string.IsNullOrEmpty(activeCustomFile) ? activeCustomFile : p2;
            report.CustomOverrideCount = customModels.Count;
            report.CustomHarnessRuleCount = harnessCount;

            DataSourceProbeItem item = new DataSourceProbeItem();
            item.Category = DataSourceProbeItem.CategoryCloudStore;
            item.Name = "价格基准与自定义规则";
            item.TargetPath = !string.IsNullOrEmpty(activeCustomFile) ? activeCustomFile : appDir;
            item.FileCount = fileCount;
            item.TotalBytes = totalBytes;
            item.LastModifiedTicks = latestMt;

            if (customModels.Count > 0 || harnessCount > 0)
            {
                item.Status = DataSourceProbeItem.StatusActive;
                item.StatusLabel = string.Format(CultureInfo.InvariantCulture, "自定义生效 ({0} 条)", customModels.Count + harnessCount);
                item.DiagnosticHint = string.Format(
                    CultureInfo.InvariantCulture,
                    "已加载 {0} 条自定义模型单价覆盖 (custom_prices.json) 与 {1} 条自定义目录采集规则 (harnesses.json)",
                    customModels.Count,
                    harnessCount);
            }
            else if (hasCloudCache)
            {
                item.Status = DataSourceProbeItem.StatusActive;
                item.StatusLabel = "云端价格库就绪";
                item.DiagnosticHint = "已同步 LiteLLM / OpenRouter / models.dev 云端模型价格缓存；如需私有中转站改价可创建 custom_prices.json";
            }
            else
            {
                item.Status = DataSourceProbeItem.StatusInstalledEmpty;
                item.StatusLabel = "内置基准模式";
                item.DiagnosticHint = "当前使用内置官方模型单价基准；支持在 %APPDATA%\\TokLens\\custom_prices.json 覆盖自定义价格";
            }

            return item;
        }

        private static DataSourceProbeItem InspectGitHubCliAndEnv(string roaming, SyncConfig syncCfg, SystemHealthReport report)
        {
            // 1. 优先快速检测环境变量 GH_TOKEN / GITHUB_TOKEN / GH_ENTERPRISE_TOKEN
            string[] envNames = new string[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN" };
            List<string> hitEnvDetails = new List<string>();
            List<string> hitEnvKeys = new List<string>();

            for (int i = 0; i < envNames.Length; i++)
            {
                try
                {
                    string val = Environment.GetEnvironmentVariable(envNames[i]);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        hitEnvKeys.Add(envNames[i]);
                        hitEnvDetails.Add(envNames[i] + "=" + SyncService.MaskTokenFingerprint(val));
                    }
                }
                catch { }
            }

            // 2. 快速定位 gh.exe 路径，避免未安装时无谓拉起子进程
            string ghExe = FindGhExecutableFast();
            report.GhCliFound = !string.IsNullOrEmpty(ghExe);
            report.GhCliExecutablePath = ghExe ?? "";

            bool syncSvcEnvOverride = false;
            string syncSvcEnvVar = "";
            List<GhCliAccountInfo> accounts = new List<GhCliAccountInfo>();

            if (report.GhCliFound)
            {
                try
                {
                    accounts = SyncService.DiscoverGitHubCliAccounts(out syncSvcEnvOverride, out syncSvcEnvVar);
                }
                catch { }
            }

            if (syncSvcEnvOverride && !string.IsNullOrEmpty(syncSvcEnvVar) && !hitEnvKeys.Contains(syncSvcEnvVar))
            {
                hitEnvKeys.Add(syncSvcEnvVar);
                hitEnvDetails.Add(syncSvcEnvVar);
            }

            for (int i = 0; i < accounts.Count; i++)
            {
                GhCliAccountInfo acc = accounts[i];
                if (acc == null || string.IsNullOrEmpty(acc.Login)) continue;
                report.GhAccounts.Add(acc.Login);
                if (acc.IsActive && string.IsNullOrEmpty(report.GhActiveAccount))
                    report.GhActiveAccount = acc.Login;
            }
            if (string.IsNullOrEmpty(report.GhActiveAccount) && report.GhAccounts.Count > 0)
                report.GhActiveAccount = report.GhAccounts[0];

            if (hitEnvKeys.Count > 0)
            {
                string joinedKeys = string.Join(" / ", hitEnvKeys.ToArray());
                string joinedDetails = string.Join(", ", hitEnvDetails.ToArray());
                report.GhEnvOverrideWarning = string.Format(
                    CultureInfo.InvariantCulture,
                    "检测到环境变量 {0} ({1})，它会强行覆盖 gh CLI 默认安全凭据库 (keyring)，常导致家里与单位电脑令牌不一致或缺少 gist 权限！",
                    joinedKeys,
                    joinedDetails);
            }
            else
            {
                report.GhEnvOverrideWarning = "";
            }

            string ghHostsDir = CombineSafe(roaming, "GitHub CLI");
            string targetPath = SafeDirectoryExistsSimple(ghHostsDir)
                ? ghHostsDir
                : (!string.IsNullOrEmpty(ghExe) ? ghExe : SyncService.AppConfigDir);

            DataSourceProbeItem item = new DataSourceProbeItem();
            item.Category = DataSourceProbeItem.CategoryCloudStore;
            item.Name = "GitHub CLI 与环境变量";
            item.TargetPath = targetPath;

            long cfgSz;
            long cfgMt;
            if (TokenScanner.TryGetFileStatInternal(SyncService.ConfigFilePath, out cfgSz, out cfgMt))
            {
                item.FileCount = 1;
                item.TotalBytes = cfgSz;
                item.LastModifiedTicks = cfgMt;
            }

            if (!string.IsNullOrEmpty(report.GhEnvOverrideWarning))
            {
                item.Status = DataSourceProbeItem.StatusWarning;
                item.StatusLabel = "环境变量覆盖告警";
                item.DiagnosticHint = report.GhEnvOverrideWarning;
                return item;
            }

            if (report.GhCliFound && report.GhAccounts.Count > 0)
            {
                item.Status = DataSourceProbeItem.StatusActive;
                if (report.GhAccounts.Count > 1)
                {
                    item.StatusLabel = string.Format(CultureInfo.InvariantCulture, "多账号就绪 ({0} 个)", report.GhAccounts.Count);
                    item.DiagnosticHint = string.Format(
                        CultureInfo.InvariantCulture,
                        "已识别 {0} 个账号（当前活跃: @{1}），无环境变量覆盖干扰；注：家里与单位电脑通过 gh 登录会各自生成独立的 gho_ 设备令牌，同账号下自动互通同一 Gist",
                        report.GhAccounts.Count,
                        report.GhActiveAccount);
                }
                else
                {
                    item.StatusLabel = "凭据健康";
                    item.DiagnosticHint = string.Format(
                        CultureInfo.InvariantCulture,
                        "gh CLI 登录正常 (@{0}) 且无 GH_TOKEN 环境变量劫持；注：不同电脑签发的 gho_ 设备令牌字面不同属正常安全机制，同 GitHub 账号天然互通",
                        report.GhActiveAccount);
                }
                return item;
            }

            if (syncCfg != null && syncCfg.IsConfigured)
            {
                item.Status = DataSourceProbeItem.StatusActive;
                item.StatusLabel = "云同步凭据已配置";
                string kind = SyncService.DescribeTokenKind(syncCfg.GistToken);
                item.DiagnosticHint = string.Equals(syncCfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase)
                    ? "当前使用 WebDAV 私有云同步（凭据已由 Windows DPAPI 本机加密保护），无环境变量冲突"
                    : ("当前使用独立配置的 GitHub Gist 凭据（" + kind + "），已由 Windows DPAPI 加密保护");
                return item;
            }

            if (report.GhCliFound)
            {
                item.Status = DataSourceProbeItem.StatusInstalledEmpty;
                item.StatusLabel = "gh 未登录";
                item.DiagnosticHint = "已检测到 gh.exe，尚未执行 gh auth login 登录账号；本机离线统计不受影响，配置多设备同步时可一键授权";
                return item;
            }

            item.Status = DataSourceProbeItem.StatusNotInstalled;
            item.StatusLabel = "未安装 gh CLI";
            item.DiagnosticHint = "未检测到 GitHub CLI (gh.exe)，且未发现异常 GH_TOKEN 环境变量；支持手动填入 PAT 或配对码开启多设备云同步";
            return item;
        }

        private static string FindGhExecutableFast()
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] dirs = pathEnv.Split(new char[] { IoPath.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string rawDir = dirs[i].Trim().Trim('"');
                    if (string.IsNullOrEmpty(rawDir)) continue;
                    try
                    {
                        string cand = IoPath.Combine(rawDir, "gh.exe");
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
                    fallbacks.Add(IoPath.Combine(IoPath.Combine(pf, "GitHub CLI"), "gh.exe"));
            }
            catch { }
            try
            {
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86))
                    fallbacks.Add(IoPath.Combine(IoPath.Combine(pf86, "GitHub CLI"), "gh.exe"));
            }
            catch { }
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localApp))
                {
                    fallbacks.Add(IoPath.Combine(IoPath.Combine(IoPath.Combine(localApp, "Programs"), "GitHub CLI"), "gh.exe"));
                    fallbacks.Add(IoPath.Combine(IoPath.Combine(IoPath.Combine(localApp, "Microsoft"), "WinGet"), @"Links\gh.exe"));
                }
            }
            catch { }
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                    fallbacks.Add(IoPath.Combine(IoPath.Combine(IoPath.Combine(home, "scoop"), "shims"), "gh.exe"));
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
            return "";
        }

        // -------------------- 快速上限控制目录枚举与格式化辅助 --------------------

        private static DataSourceProbeItem BuildStandardProbe(
            string category,
            string name,
            ProbeAccum acc,
            string activeHint,
            string emptyHint,
            string missingHint)
        {
            DataSourceProbeItem item = new DataSourceProbeItem();
            item.Category = category;
            item.Name = name;
            item.TargetPath = !string.IsNullOrEmpty(acc.BestExistingPath) ? acc.BestExistingPath : acc.FallbackPath;
            item.FileCount = acc.FileCount;
            item.FileCountCapped = acc.Capped;
            item.TotalBytes = acc.TotalBytes;
            item.LastModifiedTicks = acc.LatestMtimeTicks;

            if (acc.AccessDenied && acc.FileCount == 0)
            {
                item.Status = DataSourceProbeItem.StatusWarning;
                item.StatusLabel = "权限受限";
                item.DiagnosticHint = "目录访问被系统拒绝 (UnauthorizedAccessException)，请检查当前账户读写权限或安全软件隔离规则";
            }
            else if (acc.FileCount > 0)
            {
                item.Status = DataSourceProbeItem.StatusActive;
                item.StatusLabel = string.Format(
                    CultureInfo.InvariantCulture,
                    "已捕获 ({0}{1} 个文件)",
                    acc.FileCount,
                    acc.Capped ? "+" : "");
                item.DiagnosticHint = activeHint;
            }
            else if (acc.AnyRootExists)
            {
                item.Status = DataSourceProbeItem.StatusInstalledEmpty;
                item.StatusLabel = "暂无日志";
                item.DiagnosticHint = emptyHint;
            }
            else
            {
                item.Status = DataSourceProbeItem.StatusNotInstalled;
                item.StatusLabel = "未安装";
                item.DiagnosticHint = missingHint;
            }

            return item;
        }

        private static void FinalizeProbeDisplayFields(DataSourceProbeItem item)
        {
            if (item == null) return;

            if (string.Equals(item.Status, DataSourceProbeItem.StatusWarning, StringComparison.Ordinal))
                item.StatusRank = 0;
            else if (string.Equals(item.Status, DataSourceProbeItem.StatusActive, StringComparison.Ordinal))
                item.StatusRank = 1;
            else if (string.Equals(item.Status, DataSourceProbeItem.StatusInstalledEmpty, StringComparison.Ordinal))
                item.StatusRank = 2;
            else
                item.StatusRank = 3;

            if (item.FileCount > 0)
            {
                item.FileCountText = item.FileCount.ToString(CultureInfo.InvariantCulture) + (item.FileCountCapped ? "+" : "");
                item.TotalSizeText = FormatBytes(item.TotalBytes);
            }
            else
            {
                item.FileCountText = "—";
                item.TotalSizeText = item.TotalBytes > 0L ? FormatBytes(item.TotalBytes) : "—";
            }

            item.LastModifiedText = FormatFileTimeRelative(item.LastModifiedTicks);
            item.CanOpenDirectory = !string.IsNullOrWhiteSpace(ResolveOpenableDirectoryOrParent(item.TargetPath));

            string pathLine = string.IsNullOrWhiteSpace(item.TargetPath) ? "（未指定路径）" : item.TargetPath;
            item.TooltipDetail = string.Format(
                CultureInfo.InvariantCulture,
                "{0}  [{1}]\n探测路径：{2}\n诊断说明：{3}",
                item.Name,
                item.StatusLabel,
                pathLine,
                item.DiagnosticHint ?? "");
        }

        private static void ScanSingleFileIntoAccum(ProbeAccum acc, string filePath)
        {
            if (acc == null || string.IsNullOrWhiteSpace(filePath) || acc.FileCount >= MaxFilesPerProbe)
                return;

            long sz;
            long mt;
            if (TokenScanner.TryGetFileStatInternal(filePath, out sz, out mt))
            {
                acc.AnyRootExists = true;
                acc.FileCount++;
                acc.TotalBytes += sz;
                if (mt > acc.LatestMtimeTicks) acc.LatestMtimeTicks = mt;
                if (string.IsNullOrEmpty(acc.BestExistingPath))
                    acc.BestExistingPath = filePath;
            }
        }

        private static void ScanSqliteFileIntoAccum(ProbeAccum acc, string dbPath)
        {
            if (acc == null || string.IsNullOrWhiteSpace(dbPath) || acc.FileCount >= MaxFilesPerProbe)
                return;

            long sz;
            long mt;
            if (!TokenScanner.TryGetFileStatInternal(dbPath, out sz, out mt))
                return;

            long combSz = sz;
            long maxMt = mt;
            string[] sidecars = new string[] { "-wal", "-shm" };
            for (int i = 0; i < sidecars.Length; i++)
            {
                long sSz;
                long sMt;
                if (TokenScanner.TryGetFileStatInternal(dbPath + sidecars[i], out sSz, out sMt))
                {
                    combSz += sSz;
                    if (sMt > maxMt) maxMt = sMt;
                }
            }

            acc.AnyRootExists = true;
            acc.FileCount++;
            acc.TotalBytes += combSz;
            if (maxMt > acc.LatestMtimeTicks) acc.LatestMtimeTicks = maxMt;
            acc.BestExistingPath = dbPath;
        }

        private static void ScanDirIntoAccum(
            ProbeAccum acc,
            string rootDir,
            string pattern,
            bool recursive,
            Func<string, bool> fileFilter)
        {
            if (acc == null || string.IsNullOrWhiteSpace(rootDir)) return;
            if (acc.FileCount >= MaxFilesPerProbe)
            {
                acc.Capped = true;
                return;
            }

            bool denied;
            if (!SafeDirectoryExists(rootDir, out denied))
            {
                if (denied)
                {
                    acc.AnyRootExists = true;
                    acc.AccessDenied = true;
                    if (string.IsNullOrEmpty(acc.BestExistingPath))
                        acc.BestExistingPath = rootDir;
                }
                return;
            }

            acc.AnyRootExists = true;
            if (string.IsNullOrEmpty(acc.BestExistingPath))
                acc.BestExistingPath = rootDir;

            Stack<string> stack = new Stack<string>();
            stack.Push(TokenScanner.ToLongPath(rootDir));
            int visitedDirs = 0;

            while (stack.Count > 0)
            {
                if (acc.FileCount >= MaxFilesPerProbe)
                {
                    acc.Capped = true;
                    break;
                }
                if (visitedDirs >= MaxDirectoriesPerRoot)
                    break;

                string current = stack.Pop();
                visitedDirs++;

                try
                {
                    foreach (string rawFile in Directory.EnumerateFiles(current, pattern))
                    {
                        string cleanFile = StripLongPathPrefix(rawFile);
                        if (cleanFile.EndsWith(".checkpoints.jsonl", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (fileFilter != null && !fileFilter(cleanFile))
                            continue;

                        long fSz;
                        long fMt;
                        if (TokenScanner.TryGetFileStatInternal(cleanFile, out fSz, out fMt))
                        {
                            acc.FileCount++;
                            acc.TotalBytes += fSz;
                            if (fMt > acc.LatestMtimeTicks)
                                acc.LatestMtimeTicks = fMt;
                            if (acc.FileCount >= MaxFilesPerProbe)
                            {
                                acc.Capped = true;
                                break;
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    acc.AccessDenied = true;
                }
                catch { }

                if (recursive && acc.FileCount < MaxFilesPerProbe)
                {
                    try
                    {
                        foreach (string subDir in Directory.EnumerateDirectories(current))
                        {
                            int slash = subDir.LastIndexOf('\\');
                            string name = slash >= 0 ? subDir.Substring(slash + 1) : subDir;
                            if (ShouldSkipHeavySubdir(name))
                                continue;
                            stack.Push(subDir);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        acc.AccessDenied = true;
                    }
                    catch { }
                }
            }
        }

        private static bool ShouldSkipHeavySubdir(string dirName)
        {
            if (string.IsNullOrEmpty(dirName)) return false;
            return string.Equals(dirName, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "Cache", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "Code Cache", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "GPUCache", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "DawnCache", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "CachedData", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "Crashpad", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "blob_storage", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "__pycache__", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, ".venv", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "venv", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SafeDirectoryExists(string dirPath, out bool accessDenied)
        {
            accessDenied = false;
            if (string.IsNullOrWhiteSpace(dirPath)) return false;
            if (string.Equals(dirPath.TrimEnd('\\', '/'), @"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dirPath.TrimEnd('\\', '/'), @"\\wsl$", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                string lp = TokenScanner.ToLongPath(dirPath);
                if (!Directory.Exists(lp))
                    return false;

                // 轻量试探枚举首项以识别权限拒绝目录
                try
                {
                    using (IEnumerator<string> en = Directory.EnumerateFileSystemEntries(lp).GetEnumerator())
                    {
                        en.MoveNext();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    accessDenied = true;
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeDirectoryExistsSimple(string dirPath)
        {
            bool denied;
            return SafeDirectoryExists(dirPath, out denied);
        }

        private static string StripLongPathPrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }

        private static string CombineSafe(string basePath, string child)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return "";
            if (string.IsNullOrWhiteSpace(child)) return basePath;
            try { return IoPath.Combine(basePath, child); }
            catch { return basePath.TrimEnd('\\', '/') + "\\" + child.TrimStart('\\', '/'); }
        }

        public static string ResolveOpenableDirectoryOrParent(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return "";
            string p = targetPath.Trim();
            if (string.Equals(p.TrimEnd('\\', '/'), @"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.TrimEnd('\\', '/'), @"\\wsl$", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            try
            {
                long sz;
                long mt;
                if (TokenScanner.TryGetFileStatInternal(p, out sz, out mt) && File.Exists(TokenScanner.ToLongPath(p)))
                    return p;
                if (Directory.Exists(TokenScanner.ToLongPath(p)))
                    return p;

                string cur = p;
                for (int depth = 0; depth < 5; depth++)
                {
                    string parent = IoPath.GetDirectoryName(cur);
                    if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase))
                        break;
                    if (string.Equals(parent.TrimEnd('\\', '/'), @"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(parent.TrimEnd('\\', '/'), @"\\wsl$", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (Directory.Exists(TokenScanner.ToLongPath(parent)))
                        return parent;
                    cur = parent;
                }
            }
            catch { }
            return "";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0L) return "0 B";
            if (bytes < 1024L)
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatFileTimeRelative(long fileTimeTicks)
        {
            if (fileTimeTicks <= 0L) return "—";
            DateTime localDt;
            try
            {
                localDt = DateTime.FromFileTime(fileTimeTicks);
            }
            catch
            {
                return "—";
            }

            TimeSpan diff = DateTime.Now - localDt;
            if (diff.TotalSeconds < -60)
                return localDt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (diff.TotalSeconds < 60)
                return "刚刚";
            if (diff.TotalMinutes < 60)
                return ((int)Math.Max(1.0, Math.Floor(diff.TotalMinutes))).ToString(CultureInfo.InvariantCulture) + " 分钟前";
            if (diff.TotalHours < 24)
                return ((int)Math.Max(1.0, Math.Floor(diff.TotalHours))).ToString(CultureInfo.InvariantCulture) + " 小时前";
            if (diff.TotalDays < 7)
                return ((int)Math.Max(1.0, Math.Floor(diff.TotalDays))).ToString(CultureInfo.InvariantCulture) + " 天前";
            return localDt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        public static string SanitizePathForShare(string rawText, string homeDir)
        {
            if (string.IsNullOrEmpty(rawText)) return "";
            string result = rawText;

            try
            {
                if (!string.IsNullOrWhiteSpace(homeDir))
                {
                    string cleanHome = homeDir.TrimEnd('\\', '/');
                    if (cleanHome.Length > 3)
                    {
                        result = ReplaceIgnoreCase(result, cleanHome, @"C:\Users\<USER>");
                    }
                }
            }
            catch { }

            try
            {
                string sysProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(sysProfile))
                {
                    string cleanSys = sysProfile.TrimEnd('\\', '/');
                    if (cleanSys.Length > 3)
                        result = ReplaceIgnoreCase(result, cleanSys, @"C:\Users\<USER>");
                }
            }
            catch { }

            try
            {
                string userName = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 2)
                {
                    result = ReplaceIgnoreCase(result, @"\" + userName + @"\", @"\<USER>\");
                    result = ReplaceIgnoreCase(result, "/" + userName + "/", "/<USER>/");
                }
            }
            catch { }

            return result;
        }

        private static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
                return input;

            StringBuilder sb = new StringBuilder(input.Length);
            int pos = 0;
            while (pos < input.Length)
            {
                int idx = input.IndexOf(oldValue, pos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    sb.Append(input, pos, input.Length - pos);
                    break;
                }
                sb.Append(input, pos, idx - pos);
                sb.Append(newValue);
                pos = idx + oldValue.Length;
            }
            return sb.ToString();
        }

        private static string MaskAccountName(string login)
        {
            if (string.IsNullOrWhiteSpace(login)) return "<未登录>";
            string s = login.Trim();
            if (s.Length <= 2) return s[0] + "*";
            return s.Substring(0, 1) + "***" + s.Substring(s.Length - 1, 1);
        }

        /// <summary>
        /// 生成一键复制的脱敏纯文本体检摘要（自动隐藏 Windows 用户名与敏感凭据段）。
        /// </summary>
        public static string BuildSanitizedDiagnosticSummary(SystemHealthReport report)
        {
            if (report == null)
                return "[知微 · TokLens 环境检查] 暂无检查数据";

            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("============================================================");
            sb.AppendLine(AppConstants.ProductTitle + " · 环境检查摘要（已脱敏）");
            sb.AppendLine("============================================================");
            sb.AppendFormat(CultureInfo.InvariantCulture, "生成时间   : {0}\r\n", report.GeneratedAt);
            sb.AppendFormat(CultureInfo.InvariantCulture, "操作系统   : {0} ({1}-bit)\r\n", Environment.OSVersion.VersionString, Environment.Is64BitOperatingSystem ? "64" : "32");
            sb.AppendFormat(CultureInfo.InvariantCulture, "探测耗时   : {0} ms\r\n", report.ScanElapsedMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "活跃数据源 : {0} / {1} 个 AI 工具活跃 (日志总计 {2})\r\n",
                report.ActiveSourceCount, report.TotalSourceCount, report.TotalLogSizeText);

            string wslText = (report.WslDistroNames != null && report.WslDistroNames.Count > 0)
                ? (string.Join(", ", report.WslDistroNames.ToArray()) + (report.ScanLocalWslEnabled ? " [跨子系统扫描: 已开启]" : " [跨子系统扫描: 未开启]"))
                : "未启用 WSL2";
            sb.AppendFormat(CultureInfo.InvariantCulture, "WSL2 穿透  : {0}\r\n", wslText);

            sb.AppendFormat(CultureInfo.InvariantCulture, "本地账本库 : {0} ({1}, 游标 {2} 条 / 日归档 {3} 条, 单价覆盖 {4} 条)\r\n",
                report.LedgerDbHealthy ? "健康" : "需关注",
                report.LedgerDbSizeText,
                report.TrackedCursorCount,
                report.DailyArchiveRowCount,
                report.CustomOverrideCount);

            string ghSummary;
            if (!string.IsNullOrEmpty(report.GhEnvOverrideWarning))
            {
                ghSummary = "告警 - " + SanitizePathForShare(report.GhEnvOverrideWarning, report.HomeDirectory);
            }
            else if (report.GhCliFound && report.GhAccounts != null && report.GhAccounts.Count > 0)
            {
                ghSummary = string.Format(CultureInfo.InvariantCulture, "正常 (已安装 gh CLI，登录 {0} 个账号，活跃账号 @{1}，无环境变量劫持)",
                    report.GhAccounts.Count, MaskAccountName(report.GhActiveAccount));
            }
            else if (report.GhCliFound)
            {
                ghSummary = "已安装 gh CLI (当前未登录)";
            }
            else
            {
                ghSummary = "未安装 gh CLI (无异常环境变量覆盖)";
            }
            sb.AppendFormat(CultureInfo.InvariantCulture, "GitHub 环境: {0}\r\n", ghSummary);
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[数据源探针明细]");

            if (report.Probes != null)
            {
                for (int i = 0; i < report.Probes.Count; i++)
                {
                    DataSourceProbeItem p = report.Probes[i];
                    if (p == null) continue;
                    string safePath = SanitizePathForShare(p.TargetPath, report.HomeDirectory);
                    string safeHint = SanitizePathForShare(p.DiagnosticHint, report.HomeDirectory);
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "- [{0}] {1} ({2}) | 文件: {3} | 体积: {4} | 更新: {5}\r\n  路径: {6}\r\n  诊断: {7}\r\n",
                        p.StatusLabel,
                        p.Name,
                        p.Category,
                        p.FileCountText,
                        p.TotalSizeText,
                        p.LastModifiedText,
                        safePath,
                        safeHint);
                }
            }
            sb.AppendLine("============================================================");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 环境体检与数据源诊断中心视图（对标 Raycast Diagnostics / OrbStack System Info / brew doctor）。
    /// 支持深浅双主题动态资源无缝切换、分类胶囊过滤、关键词实时检索、一键复制脱敏报告与资源管理器定位。
    /// </summary>
    public sealed class DiagnosticsDoctorView : Border
    {
        private readonly string _homeDir;
        private readonly Action<string, string, string> _toastCallback;

        private bool _isDark;
        private int _isRefreshingFlag;
        private int _activeCategoryIndex;
        private string _searchKeyword;
        private SystemHealthReport _currentReport;
        private List<DataSourceProbeItem> _filteredItems;

        // 顶部健康度总览横幅控件
        private Border _bannerBorder;
        private Border _bannerIconBox;
        private WpfPath _bannerIconPath;
        private TextBlock _bannerHeadlineText;
        private TextBlock _bannerSubText;
        private Border _bannerElapsedPill;
        private TextBlock _bannerElapsedText;
        private Border _bannerStatusBadge;
        private Ellipse _bannerStatusDot;
        private TextBlock _bannerStatusText;

        // 4 张核心系统探针卡片控件
        private TextBlock _cardSourcesMainText;
        private TextBlock _cardSourcesSubText;
        private TextBlock _cardWslMainText;
        private TextBlock _cardWslSubText;
        private TextBlock _cardLedgerMainText;
        private TextBlock _cardLedgerSubText;
        private Button _openConfigDirBtn;
        private TextBlock _cardGhMainText;
        private TextBlock _cardGhSubText;
        private Border _cardGhBorder;

        // 中部筛选与动作栏控件
        private Button[] _categorySegButtons;
        private TextBox _searchInputBox;
        private TextBlock _searchWatermarkText;
        private Button _searchClearBtn;
        private TextBlock _matchCountText;
        private Button _refreshBtn;
        private TextBlock _refreshBtnText;
        private Button _copySummaryBtn;
        private WpfPath _copyBtnIcon;
        private TextBlock _copyBtnText;
        private DispatcherTimer _copyResetTimer;

        // 底部探针矩阵表与空状态层
        private DataGrid _probeDataGrid;
        private Border _emptyFilterOverlay;
        private TextBlock _emptyFilterHintText;

        public SystemHealthReport CurrentReport
        {
            get { return _currentReport; }
        }

        public int FilteredItemCount
        {
            get { return _filteredItems != null ? _filteredItems.Count : 0; }
        }

        public DiagnosticsDoctorView()
            : this(null, null)
        {
        }

        public DiagnosticsDoctorView(string homeDir)
            : this(homeDir, null)
        {
        }

        public DiagnosticsDoctorView(string homeDir, Action<string, string, string> toastCallback)
        {
            _homeDir = string.IsNullOrWhiteSpace(homeDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDir;
            _toastCallback = toastCallback;
            _isDark = true;
            _isRefreshingFlag = 0;
            _activeCategoryIndex = 0;
            _searchKeyword = "";
            _filteredItems = new List<DataSourceProbeItem>();

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Background = Brushes.Transparent;
            Padding = new Thickness(16, 12, 16, 12);

            _copyResetTimer = new DispatcherTimer();
            _copyResetTimer.Interval = TimeSpan.FromMilliseconds(1800);
            _copyResetTimer.Tick += OnCopyResetTimerTick;

            Child = BuildRootLayout();
        }

        /// <summary>
        /// 异步后台执行环境体检并在完成后自动切回 UI 线程渲染报告。
        /// </summary>
        public void RefreshDoctor(bool isDark)
        {
            _isDark = isDark;

            if (Interlocked.CompareExchange(ref _isRefreshingFlag, 1, 0) != 0)
                return;

            SetRefreshingVisualState(true);

            string targetHome = _homeDir;
            bool capturedDark = isDark;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                SystemHealthReport freshReport = null;
                try
                {
                    freshReport = TelemetryDoctorScanner.RunDiagnostics(targetHome);
                }
                catch
                {
                    freshReport = new SystemHealthReport();
                }

                try
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate()
                    {
                        try
                        {
                            RenderReport(freshReport, capturedDark);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _isRefreshingFlag, 0);
                            SetRefreshingVisualState(false);
                        }
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref _isRefreshingFlag, 0);
                }
            });
        }

        /// <summary>
        /// 同步渲染给定的系统体检报告（同时作为 --self-test-ui 无头自检直接验证入口）。
        /// </summary>
        public void RenderReport(SystemHealthReport report, bool isDark)
        {
            _isDark = isDark;
            _currentReport = report ?? new SystemHealthReport();

            UpdateHealthBanner(_currentReport);
            UpdateProbeSummaryCards(_currentReport);
            ApplyFilterAndRefreshGrid();
        }

        public void SetCategoryFilter(int categoryIndex)
        {
            if (categoryIndex < 0 || categoryIndex > 4)
                categoryIndex = 0;
            _activeCategoryIndex = categoryIndex;
            ModernControlsFactory.UpdateSegmentedGroup(_categorySegButtons, _activeCategoryIndex);
            ApplyFilterAndRefreshGrid();
        }

        public void SetSearchKeyword(string keyword)
        {
            string clean = keyword ?? "";
            _searchKeyword = clean.Trim();
            if (_searchInputBox != null && !string.Equals(_searchInputBox.Text, clean, StringComparison.Ordinal))
                _searchInputBox.Text = clean;
            UpdateSearchBoxAdornments();
            ApplyFilterAndRefreshGrid();
        }

        // ==================== UI 构建 ====================

        private UIElement BuildRootLayout()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: 顶部健康度总览横幅
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: 4 张系统探针卡片
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 2: 中部筛选与动作工具栏
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 3: 底部探针矩阵表

            UIElement banner = BuildHealthBanner();
            Grid.SetRow(banner, 0);
            root.Children.Add(banner);

            UIElement cards = BuildFourSummaryCards();
            Grid.SetRow(cards, 1);
            root.Children.Add(cards);

            UIElement filterBar = BuildFilterToolbar();
            Grid.SetRow(filterBar, 2);
            root.Children.Add(filterBar);

            UIElement matrixCard = BuildProbeMatrixCard();
            Grid.SetRow(matrixCard, 3);
            root.Children.Add(matrixCard);

            return root;
        }

        private UIElement BuildHealthBanner()
        {
            _bannerBorder = ModernControlsFactory.CreateCard(new Thickness(14, 10, 14, 10));
            _bannerBorder.Margin = new Thickness(0, 0, 0, 10);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧状态盾牌/警示图标框
            _bannerIconBox = new Border();
            _bannerIconBox.Width = 34;
            _bannerIconBox.Height = 34;
            _bannerIconBox.CornerRadius = new CornerRadius(8);
            _bannerIconBox.BorderThickness = new Thickness(1);
            _bannerIconBox.Margin = new Thickness(0, 0, 12, 0);
            _bannerIconBox.VerticalAlignment = VerticalAlignment.Center;
            _bannerIconBox.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
            _bannerIconBox.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");

            _bannerIconPath = UiIcons.CreateIcon(UiIcons.Shield, 16, "SemanticSuccess");
            _bannerIconBox.Child = _bannerIconPath;
            Grid.SetColumn(_bannerIconBox, 0);
            g.Children.Add(_bannerIconBox);

            // 中间主副标题
            StackPanel titleStack = new StackPanel();
            titleStack.VerticalAlignment = VerticalAlignment.Center;

            _bannerHeadlineText = new TextBlock();
            _bannerHeadlineText.Text = "环境检查";
            _bannerHeadlineText.FontFamily = DesignSystem.FontUi;
            _bannerHeadlineText.FontSize = DesignSystem.TextLabel;
            _bannerHeadlineText.FontWeight = FontWeights.SemiBold;
            _bannerHeadlineText.TextTrimming = TextTrimming.CharacterEllipsis;
            _bannerHeadlineText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleStack.Children.Add(_bannerHeadlineText);

            _bannerSubText = new TextBlock();
            _bannerSubText.Text = "尚未检查。会检查本机 AI 工具日志目录、WSL2、本地账本与 GitHub CLI 环境变量。";
            _bannerSubText.FontFamily = DesignSystem.FontUi;
            _bannerSubText.FontSize = DesignSystem.TextCaption;
            _bannerSubText.Margin = new Thickness(0, 2, 0, 0);
            _bannerSubText.TextTrimming = TextTrimming.CharacterEllipsis;
            _bannerSubText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            titleStack.Children.Add(_bannerSubText);

            Grid.SetColumn(titleStack, 1);
            g.Children.Add(titleStack);

            // 右侧耗时胶囊 + 健康状态徽章
            StackPanel rightBadges = new StackPanel();
            rightBadges.Orientation = Orientation.Horizontal;
            rightBadges.VerticalAlignment = VerticalAlignment.Center;
            rightBadges.Margin = new Thickness(12, 0, 0, 0);

            _bannerElapsedPill = new Border();
            _bannerElapsedPill.Height = 20;
            _bannerElapsedPill.CornerRadius = new CornerRadius(4);
            _bannerElapsedPill.Padding = new Thickness(7, 0, 7, 0);
            _bannerElapsedPill.Margin = new Thickness(0, 0, 8, 0);
            _bannerElapsedPill.BorderThickness = new Thickness(1);
            _bannerElapsedPill.VerticalAlignment = VerticalAlignment.Center;
            _bannerElapsedPill.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _bannerElapsedPill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            _bannerElapsedText = new TextBlock();
            _bannerElapsedText.Text = "耗时 — ms";
            _bannerElapsedText.FontSize = DesignSystem.TextCaption;
            _bannerElapsedText.VerticalAlignment = VerticalAlignment.Center;
            ModernControlsFactory.EnableTabularNumbers(_bannerElapsedText, true);
            _bannerElapsedText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            _bannerElapsedPill.Child = _bannerElapsedText;
            rightBadges.Children.Add(_bannerElapsedPill);

            _bannerStatusBadge = ModernControlsFactory.CreateStatusBadge("未检查", "neutral", out _bannerStatusText, out _bannerStatusDot);
            rightBadges.Children.Add(_bannerStatusBadge);

            Grid.SetColumn(rightBadges, 2);
            g.Children.Add(rightBadges);

            _bannerBorder.Child = g;
            return _bannerBorder;
        }

        private UIElement BuildFourSummaryCards()
        {
            UniformGrid ug = new UniformGrid();
            ug.Columns = 4;
            ug.Rows = 1;
            ug.Margin = new Thickness(0, 0, 0, 10);

            // 卡片 1：已激活 AI 数据源
            Border card1 = CreateMetricCardShell(
                UiIcons.Activity,
                "AccentText",
                "已激活 AI 数据源",
                null,
                out _cardSourcesMainText,
                out _cardSourcesSubText);
            card1.Margin = new Thickness(0, 0, 5, 0);
            _cardSourcesMainText.Text = "0 / 24 个工具活跃";
            _cardSourcesSubText.Text = "累计日志体积：0 B";
            ug.Children.Add(card1);

            // 卡片 2：WSL2 跨子系统穿透
            Border card2 = CreateMetricCardShell(
                UiIcons.Terminal,
                "SemanticSuccess",
                "WSL2 跨子系统穿透",
                null,
                out _cardWslMainText,
                out _cardWslSubText);
            card2.Margin = new Thickness(5, 0, 5, 0);
            _cardWslMainText.Text = "未启用 WSL2";
            _cardWslSubText.Text = "注册表 Lxss 未检测到发行版";
            ug.Children.Add(card2);

            // 卡片 3：本地 SQLite 账本引擎（带「打开配置目录」快捷按钮）
            _openConfigDirBtn = ModernControlsFactory.CreateButton("打开配置目录", "secondary");
            _openConfigDirBtn.Height = 22;
            _openConfigDirBtn.FontSize = DesignSystem.TextCaption;
            _openConfigDirBtn.Padding = new Thickness(7, 0, 7, 0);
            _openConfigDirBtn.ToolTip = "在资源管理器中打开 TokLens 本地账本与配置目录 (%APPDATA%\\TokLens)";
            _openConfigDirBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenPathInExplorer(SyncService.AppConfigDir);
            };

            Border card3 = CreateMetricCardShell(
                UiIcons.Models,
                "AccentText",
                "本地 SQLite 账本引擎",
                _openConfigDirBtn,
                out _cardLedgerMainText,
                out _cardLedgerSubText);
            card3.Margin = new Thickness(5, 0, 5, 0);
            _cardLedgerMainText.Text = "toklens.db · 0 B";
            _cardLedgerSubText.Text = "自定义单价覆盖：0 条";
            ug.Children.Add(card3);

            // 卡片 4：GitHub CLI 与环境变量体检
            _cardGhBorder = CreateMetricCardShell(
                UiIcons.Key,
                "SemanticSuccess",
                "GitHub CLI 与环境变量",
                null,
                out _cardGhMainText,
                out _cardGhSubText);
            _cardGhBorder.Margin = new Thickness(5, 0, 0, 0);
            _cardGhMainText.Text = "待检测凭据环境";
            _cardGhSubText.Text = "自动排查 GH_TOKEN 覆盖与多设备差异";
            ug.Children.Add(_cardGhBorder);

            return ug;
        }

        private static Border CreateMetricCardShell(
            string iconData,
            string iconColorKey,
            string title,
            UIElement topRightAction,
            out TextBlock mainValueOut,
            out TextBlock subHintOut)
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(13, 10, 13, 10));

            StackPanel sp = new StackPanel();

            Grid headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            WpfPath icon = UiIcons.CreateIcon(iconData, 13, iconColorKey);
            icon.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(icon, 0);
            headerRow.Children.Add(icon);

            TextBlock titleTb = new TextBlock();
            titleTb.Text = title;
            titleTb.FontFamily = DesignSystem.FontUi;
            titleTb.FontSize = DesignSystem.TextCaption;
            titleTb.FontWeight = FontWeights.Medium;
            titleTb.VerticalAlignment = VerticalAlignment.Center;
            titleTb.TextTrimming = TextTrimming.CharacterEllipsis;
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            Grid.SetColumn(titleTb, 1);
            headerRow.Children.Add(titleTb);

            if (topRightAction != null)
            {
                Grid.SetColumn(topRightAction, 2);
                headerRow.Children.Add(topRightAction);
            }

            sp.Children.Add(headerRow);

            mainValueOut = new TextBlock();
            mainValueOut.FontSize = DesignSystem.TextTitle;
            mainValueOut.FontWeight = FontWeights.Bold;
            mainValueOut.Margin = new Thickness(0, 7, 0, 3);
            mainValueOut.TextTrimming = TextTrimming.CharacterEllipsis;
            ModernControlsFactory.EnableTabularNumbers(mainValueOut, false);
            mainValueOut.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(mainValueOut);

            subHintOut = new TextBlock();
            subHintOut.FontFamily = DesignSystem.FontUi;
            subHintOut.FontSize = DesignSystem.TextCaption;
            subHintOut.TextTrimming = TextTrimming.CharacterEllipsis;
            subHintOut.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(subHintOut);

            card.Child = sp;
            return card;
        }

        private UIElement BuildFilterToolbar()
        {
            Grid bar = new Grid();
            bar.Margin = new Thickness(0, 0, 0, 10);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 1. 分类筛选胶囊组（全部 / 仅已激活 / 原生 CLI / IDE 与插件 / WSL2）
            string[] segLabels = new string[] { "全部", "仅已激活", "原生 CLI", "IDE 与插件", "WSL2" };
            _categorySegButtons = new Button[segLabels.Length];
            Border segBox = ModernControlsFactory.CreateSegmentedContainer();
            StackPanel segSp = (StackPanel)segBox.Child;

            for (int i = 0; i < segLabels.Length; i++)
            {
                int capturedIdx = i;
                Button segBtn = ModernControlsFactory.CreateSegmentedItem(segLabels[i]);
                segBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    SetCategoryFilter(capturedIdx);
                };
                _categorySegButtons[i] = segBtn;
                segSp.Children.Add(segBtn);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_categorySegButtons, 0);
            Grid.SetColumn(segBox, 0);
            bar.Children.Add(segBox);

            // 2. 关键词实时搜索框（带图标、水印与一键清空按钮）
            Grid searchWrap = new Grid();
            searchWrap.Width = 230;
            searchWrap.Height = 28;
            searchWrap.Margin = new Thickness(10, 0, 0, 0);
            searchWrap.VerticalAlignment = VerticalAlignment.Center;

            _searchInputBox = ModernControlsFactory.CreateTextInput("", 28);
            _searchInputBox.Padding = new Thickness(26, 0, 24, 0);
            _searchInputBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                _searchKeyword = (_searchInputBox.Text ?? "").Trim();
                UpdateSearchBoxAdornments();
                ApplyFilterAndRefreshGrid();
            };
            searchWrap.Children.Add(_searchInputBox);

            WpfPath searchIcon = UiIcons.CreateIcon(UiIcons.Search, 12, "FgMuted");
            searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
            searchIcon.Margin = new Thickness(9, 0, 0, 0);
            searchIcon.IsHitTestVisible = false;
            searchWrap.Children.Add(searchIcon);

            _searchWatermarkText = new TextBlock();
            _searchWatermarkText.Text = "搜索工具名、路径、排障说明...";
            _searchWatermarkText.FontFamily = DesignSystem.FontUi;
            _searchWatermarkText.FontSize = DesignSystem.TextCaption;
            _searchWatermarkText.Margin = new Thickness(28, 0, 24, 0);
            _searchWatermarkText.VerticalAlignment = VerticalAlignment.Center;
            _searchWatermarkText.IsHitTestVisible = false;
            _searchWatermarkText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            searchWrap.Children.Add(_searchWatermarkText);

            _searchClearBtn = ModernControlsFactory.CreateIconGhostButton("M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5", "清空搜索词");
            _searchClearBtn.Width = 22;
            _searchClearBtn.Height = 22;
            _searchClearBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _searchClearBtn.Margin = new Thickness(0, 0, 3, 0);
            _searchClearBtn.Visibility = Visibility.Collapsed;
            _searchClearBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                SetSearchKeyword("");
            };
            searchWrap.Children.Add(_searchClearBtn);

            Grid.SetColumn(searchWrap, 1);
            bar.Children.Add(searchWrap);

            // 3. 右侧匹配条数 +「重新体检」+「复制诊断摘要」按钮
            StackPanel actionStack = new StackPanel();
            actionStack.Orientation = Orientation.Horizontal;
            actionStack.VerticalAlignment = VerticalAlignment.Center;

            _matchCountText = new TextBlock();
            _matchCountText.Text = "共 0 项探针";
            _matchCountText.FontFamily = DesignSystem.FontUi;
            _matchCountText.FontSize = DesignSystem.TextCaption;
            _matchCountText.Margin = new Thickness(0, 0, 12, 0);
            _matchCountText.VerticalAlignment = VerticalAlignment.Center;
            _matchCountText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            actionStack.Children.Add(_matchCountText);

            _refreshBtn = ModernControlsFactory.CreateButton("", "primary");
            StackPanel refContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            WpfPath refIcon = UiIcons.CreateIcon(UiIcons.Refresh, 13, null);
            refIcon.Margin = new Thickness(0, 0, 6, 0);
            refContent.Children.Add(refIcon);
            _refreshBtnText = new TextBlock { Text = "重新检查", VerticalAlignment = VerticalAlignment.Center };
            refContent.Children.Add(_refreshBtnText);
            _refreshBtn.Content = refContent;
            _refreshBtn.Margin = new Thickness(0, 0, 8, 0);
            _refreshBtn.ToolTip = "重新探测所有 AI 数据源目录、WSL2 子系统与环境变量状态";
            _refreshBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                RefreshDoctor(_isDark);
            };
            actionStack.Children.Add(_refreshBtn);

            _copySummaryBtn = ModernControlsFactory.CreateButton("", "secondary");
            StackPanel copyContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _copyBtnIcon = UiIcons.CreateIcon(UiIcons.Copy, 13, null);
            _copyBtnIcon.Margin = new Thickness(0, 0, 6, 0);
            copyContent.Children.Add(_copyBtnIcon);
            _copyBtnText = new TextBlock { Text = "复制诊断摘要", VerticalAlignment = VerticalAlignment.Center };
            copyContent.Children.Add(_copyBtnText);
            _copySummaryBtn.Content = copyContent;
            _copySummaryBtn.ToolTip = "复制检查结果（已隐藏用户名与令牌）";
            _copySummaryBtn.Click += OnCopySummaryClicked;
            actionStack.Children.Add(_copySummaryBtn);

            Grid.SetColumn(actionStack, 3);
            bar.Children.Add(actionStack);

            return bar;
        }

        private UIElement BuildProbeMatrixCard()
        {
            Border shell = ModernControlsFactory.CreateCard(new Thickness(0));
            shell.ClipToBounds = true;

            Grid host = new Grid();

            _probeDataGrid = ModernDataGridFactory.CreateModernDataGrid();
            _probeDataGrid.RowHeight = 36;
            _probeDataGrid.MouseDoubleClick += OnDataGridMouseDoubleClick;

            // 列 1：状态圆点徽章（动态主题感知 DataTrigger）
            _probeDataGrid.Columns.Add(CreateStatusPillColumn());

            // 列 2：数据源名称（悬浮显示完整路径与排障详情）
            _probeDataGrid.Columns.Add(CreateProbeNameColumn());

            // 列 3：分类胶囊
            _probeDataGrid.Columns.Add(ModernDataGridFactory.MakeBadgeColumn(
                "分类",
                "Category",
                "Category",
                new DataGridLength(132)));

            // 列 4：文件数
            _probeDataGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _probeDataGrid,
                "文件数",
                "FileCountText",
                "FileCount",
                new DataGridLength(74),
                true,
                true,
                false));

            // 列 5：占用体积
            _probeDataGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _probeDataGrid,
                "占用体积",
                "TotalSizeText",
                "TotalBytes",
                new DataGridLength(92),
                true,
                true,
                false));

            // 列 6：最近更新
            _probeDataGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _probeDataGrid,
                "最近更新",
                "LastModifiedText",
                "LastModifiedTicks",
                new DataGridLength(116),
                true,
                true,
                false));

            // 列 7：诊断说明（自适应剩余空间 + 悬浮完整详情 ToolTip）
            _probeDataGrid.Columns.Add(CreateDiagnosticHintColumn());

            // 列 8：快捷打开目录按钮
            _probeDataGrid.Columns.Add(CreateOpenFolderActionColumn());

            host.Children.Add(_probeDataGrid);

            // 空筛选结果覆盖提示
            _emptyFilterOverlay = new Border();
            _emptyFilterOverlay.Background = Brushes.Transparent;
            _emptyFilterOverlay.IsHitTestVisible = false;
            _emptyFilterOverlay.Visibility = Visibility.Collapsed;

            StackPanel emptySp = new StackPanel();
            emptySp.HorizontalAlignment = HorizontalAlignment.Center;
            emptySp.VerticalAlignment = VerticalAlignment.Center;

            WpfPath emptyIcon = UiIcons.CreateIcon(UiIcons.Search, 24, "FgMuted");
            emptyIcon.Margin = new Thickness(0, 0, 0, 8);
            emptySp.Children.Add(emptyIcon);

            _emptyFilterHintText = new TextBlock();
            _emptyFilterHintText.Text = "当前筛选条件下没有匹配的数据源探针";
            _emptyFilterHintText.FontFamily = DesignSystem.FontUi;
            _emptyFilterHintText.FontSize = DesignSystem.TextLabel;
            _emptyFilterHintText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyFilterHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            emptySp.Children.Add(_emptyFilterHintText);

            _emptyFilterOverlay.Child = emptySp;
            host.Children.Add(_emptyFilterOverlay);

            shell.Child = host;
            return shell;
        }

        private static DataGridTemplateColumn CreateStatusPillColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "状态";
            col.SortMemberPath = "StatusRank";
            col.Width = new DataGridLength(148);

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border), "StatusPill");
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            pill.SetValue(Border.PaddingProperty, new Thickness(7, 2, 7, 2));
            pill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            pill.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            pill.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            pill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
            pill.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderCard"));

            FrameworkElementFactory sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            sp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory dot = new FrameworkElementFactory(typeof(Ellipse), "StatusDot");
            dot.SetValue(FrameworkElement.WidthProperty, 6.0);
            dot.SetValue(FrameworkElement.HeightProperty, 6.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetValue(Shape.FillProperty, new DynamicResourceExtension("FgMuted"));
            sp.AppendChild(dot);

            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock), "StatusTxt");
            txt.SetBinding(TextBlock.TextProperty, new Binding("StatusLabel"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            txt.SetValue(TextBlock.FontSizeProperty, 11.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgMuted"));
            txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            sp.AppendChild(txt);

            pill.AppendChild(sp);
            dt.VisualTree = pill;

            // Active -> 翡翠绿
            DataTrigger activeTrig = new DataTrigger();
            activeTrig.Binding = new Binding("Status");
            activeTrig.Value = DataSourceProbeItem.StatusActive;
            activeTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SemanticSuccessBg"), "StatusPill"));
            activeTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticSuccessBorder"), "StatusPill"));
            activeTrig.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension("SemanticSuccess"), "StatusDot"));
            activeTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"), "StatusTxt"));
            dt.Triggers.Add(activeTrig);

            // Warning -> 琥珀黄
            DataTrigger warnTrig = new DataTrigger();
            warnTrig.Binding = new Binding("Status");
            warnTrig.Value = DataSourceProbeItem.StatusWarning;
            warnTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SemanticWarningBg"), "StatusPill"));
            warnTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticWarningBorder"), "StatusPill"));
            warnTrig.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension("SemanticWarning"), "StatusDot"));
            warnTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticWarning"), "StatusTxt"));
            dt.Triggers.Add(warnTrig);

            // InstalledEmpty -> 柔和靛蓝
            DataTrigger emptyTrig = new DataTrigger();
            emptyTrig.Binding = new Binding("Status");
            emptyTrig.Value = DataSourceProbeItem.StatusInstalledEmpty;
            emptyTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("AccentSubtleBg"), "StatusPill"));
            emptyTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentSubtleBorder"), "StatusPill"));
            emptyTrig.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension("AccentText"), "StatusDot"));
            emptyTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentText"), "StatusTxt"));
            dt.Triggers.Add(emptyTrig);

            col.CellTemplate = dt;
            return col;
        }

        private static DataGridTemplateColumn CreateProbeNameColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "数据源名称";
            col.SortMemberPath = "Name";
            col.Width = new DataGridLength(176);

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            txt.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TooltipDetail"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            txt.SetValue(TextBlock.FontSizeProperty, 12.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgPrimary"));

            dt.VisualTree = txt;
            col.CellTemplate = dt;
            return col;
        }

        private static DataGridTemplateColumn CreateDiagnosticHintColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "诊断说明与排障提示 (双击行打开路径)";
            col.SortMemberPath = "DiagnosticHint";
            col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock), "HintTxt");
            txt.SetBinding(TextBlock.TextProperty, new Binding("DiagnosticHint"));
            txt.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TooltipDetail"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            txt.SetValue(TextBlock.FontSizeProperty, 11.5);
            txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgSecondary"));

            DataTrigger warnTrig = new DataTrigger();
            warnTrig.Binding = new Binding("Status");
            warnTrig.Value = DataSourceProbeItem.StatusWarning;
            warnTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticWarning"), "HintTxt"));
            warnTrig.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Medium, "HintTxt"));

            DataTrigger notInstTrig = new DataTrigger();
            notInstTrig.Binding = new Binding("Status");
            notInstTrig.Value = DataSourceProbeItem.StatusNotInstalled;
            notInstTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgMuted"), "HintTxt"));

            dt.VisualTree = txt;
            dt.Triggers.Add(warnTrig);
            dt.Triggers.Add(notInstTrig);
            col.CellTemplate = dt;
            return col;
        }

        private DataGridTemplateColumn CreateOpenFolderActionColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "操作";
            col.CanUserSort = false;
            col.Width = new DataGridLength(86);

            ControlTemplate rowBtnTpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "RowActBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.PaddingProperty, new Thickness(7, 2, 7, 2));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
            bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderCard"));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            rowBtnTpl.VisualTree = bd;

            Trigger hv = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hv.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverStrong"), "RowActBd"));
            hv.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"), "RowActBd"));
            hv.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentText")));
            rowBtnTpl.Triggers.Add(hv);

            Trigger dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.38, "RowActBd"));
            rowBtnTpl.Triggers.Add(dis);

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory btn = new FrameworkElementFactory(typeof(Button));
            btn.SetValue(ContentControl.ContentProperty, "打开目录");
            btn.SetValue(Control.TemplateProperty, rowBtnTpl);
            btn.SetValue(Control.FontFamilyProperty, DesignSystem.FontUi);
            btn.SetValue(Control.FontSizeProperty, 11.0);
            btn.SetValue(Control.FontWeightProperty, FontWeights.Medium);
            btn.SetValue(Control.ForegroundProperty, new DynamicResourceExtension("FgSecondary"));
            btn.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            btn.SetValue(Control.FocusVisualStyleProperty, null);
            btn.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            btn.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            btn.SetBinding(UIElement.IsEnabledProperty, new Binding("CanOpenDirectory"));
            btn.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TargetPath"));
            btn.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRowOpenFolderClick));

            dt.VisualTree = btn;
            col.CellTemplate = dt;
            return col;
        }

        // ==================== 数据渲染与交互逻辑 ====================

        private void UpdateHealthBanner(SystemHealthReport r)
        {
            if (r == null) return;

            // 空报告（尚未跑过体检，或体检过程抛错）不能显示成「健康就绪」，否则用户会把没检查当成检查通过。
            // 真实体检至少会探测一个数据源，所以 TotalSourceCount == 0 即视为未运行。
            if (r.TotalSourceCount == 0)
            {
                _bannerElapsedText.Text = "未运行";
                _bannerIconBox.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                _bannerIconBox.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                _bannerIconPath.Data = Geometry.Parse(UiIcons.Doctor);
                _bannerIconPath.SetResourceReference(Shape.StrokeProperty, "FgMuted");

                _bannerStatusBadge.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                _bannerStatusBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                _bannerStatusDot.SetResourceReference(Shape.FillProperty, "FgMuted");
                _bannerStatusText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                _bannerStatusText.Text = _isRefreshingFlag != 0 ? "检查中" : "未检查";

                _bannerHeadlineText.Text = "尚未完成环境检查";
                _bannerSubText.Text = "点击「重新检查」扫描本机 AI 工具日志、WSL2 与 GitHub CLI 状态";
                return;
            }

            _bannerElapsedText.Text = string.Format(CultureInfo.InvariantCulture, "耗时 {0} ms", r.ScanElapsedMs);

            if (r.WarningCount > 0)
            {
                _bannerIconBox.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
                _bannerIconBox.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");
                _bannerIconPath.Data = Geometry.Parse(UiIcons.Alert);
                _bannerIconPath.SetResourceReference(Shape.StrokeProperty, "SemanticWarning");

                _bannerStatusBadge.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
                _bannerStatusBadge.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");
                _bannerStatusDot.SetResourceReference(Shape.FillProperty, "SemanticWarning");
                _bannerStatusText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
                _bannerStatusText.Text = string.Format(CultureInfo.InvariantCulture, "{0} 项需关注", r.WarningCount);

                _bannerHeadlineText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "检查完成 · {0} 项需要处理",
                    r.WarningCount);

                if (!string.IsNullOrEmpty(r.GhEnvOverrideWarning))
                {
                    _bannerSubText.Text = r.GhEnvOverrideWarning;
                }
                else
                {
                    _bannerSubText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "已激活 {0}/{1} 个 AI 工具源（日志总计 {2}）· 请查看下方标黄条目完成排障优化",
                        r.ActiveSourceCount,
                        r.TotalSourceCount,
                        r.TotalLogSizeText);
                }
            }
            else
            {
                _bannerIconBox.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
                _bannerIconBox.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                _bannerIconPath.Data = Geometry.Parse(UiIcons.Shield);
                _bannerIconPath.SetResourceReference(Shape.StrokeProperty, "SemanticSuccess");

                _bannerStatusBadge.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
                _bannerStatusBadge.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                _bannerStatusDot.SetResourceReference(Shape.FillProperty, "SemanticSuccess");
                _bannerStatusText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
                _bannerStatusText.Text = "正常";

                _bannerHeadlineText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "环境正常 · {0} 个数据源在监听",
                    r.ActiveSourceCount);

                _bannerSubText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "最近检查：{0} · 日志读取权限正常，本地账本可用，未发现环境变量冲突",
                    r.GeneratedAt);
            }
        }

        private void UpdateProbeSummaryCards(SystemHealthReport r)
        {
            if (r == null) return;

            // 1. 已激活 AI 数据源卡片
            _cardSourcesMainText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} / {1} 个工具活跃",
                r.ActiveSourceCount,
                r.TotalSourceCount);
            _cardSourcesSubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "累计日志体积：{0}",
                r.TotalLogSizeText);

            // 2. WSL2 跨子系统穿透卡片
            int distroCount = r.WslDistroNames != null ? r.WslDistroNames.Count : 0;
            if (distroCount <= 0)
            {
                _cardWslMainText.Text = "未启用 WSL2";
                _cardWslSubText.Text = "注册表 Lxss 未检测到 Linux 发行版";
            }
            else
            {
                string joined = string.Join(", ", r.WslDistroNames.ToArray());
                _cardWslMainText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} 个发行版 ({1})",
                    distroCount,
                    joined);
                int mounted = r.WslMountedDistros != null ? r.WslMountedDistros.Count : 0;
                string switchNote = r.ScanLocalWslEnabled ? "跨子系统扫描已开启" : "扫描开关未开启";
                _cardWslSubText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "已挂载 {0}/{1} 个 · {2}",
                    mounted,
                    distroCount,
                    switchNote);
                _cardWslMainText.ToolTip = "已探测发行版：" + joined;
            }

            // 3. 本地 SQLite 账本引擎卡片
            string dbFileName = !string.IsNullOrWhiteSpace(r.LedgerDbPath)
                ? IoPath.GetFileName(r.LedgerDbPath)
                : "toklens.db";
            _cardLedgerMainText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1}",
                dbFileName,
                r.LedgerDbSizeText);
            _cardLedgerSubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "自定义单价覆盖：{0} 条 · 游标 {1} 项",
                r.CustomOverrideCount,
                r.TrackedCursorCount);
            _cardLedgerMainText.ToolTip = r.LedgerDbPath;

            // 4. GitHub CLI 与环境变量体检卡片
            if (!string.IsNullOrEmpty(r.GhEnvOverrideWarning))
            {
                _cardGhMainText.Text = "检测到环境变量覆盖";
                _cardGhMainText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
                _cardGhSubText.Text = "GH_TOKEN / GITHUB_TOKEN 会覆盖 keyring";
                _cardGhBorder.ToolTip = r.GhEnvOverrideWarning;
            }
            else
            {
                _cardGhMainText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                if (r.GhCliFound && r.GhAccounts != null && r.GhAccounts.Count > 0)
                {
                    if (r.GhAccounts.Count > 1)
                    {
                        _cardGhMainText.Text = string.Format(
                            CultureInfo.InvariantCulture,
                            "gh 已登录 ({0} 个账号)",
                            r.GhAccounts.Count);
                    }
                    else
                    {
                        _cardGhMainText.Text = "gh 已就绪 (@" + r.GhActiveAccount + ")";
                    }
                    _cardGhSubText.Text = "无环境变量劫持 · 设备令牌 gho_ 同号互通";
                    _cardGhBorder.ToolTip = "已登录账号：" + string.Join(", ", r.GhAccounts.ToArray()) + "\n提示：不同电脑签发的 gho_ 令牌不同属正常现象，同一账号下自动互通同一 Gist。";
                }
                else if (r.GhCliFound)
                {
                    _cardGhMainText.Text = "gh 已安装 (未登录)";
                    _cardGhSubText.Text = "无 GH_TOKEN 环境变量干扰";
                    _cardGhBorder.ToolTip = r.GhCliExecutablePath;
                }
                else
                {
                    _cardGhMainText.Text = "未安装 gh CLI";
                    _cardGhSubText.Text = "无环境变量冲突 · 支持 PAT / 配对码同步";
                    _cardGhBorder.ToolTip = "本机未安装 GitHub CLI，不影响本地统计与手动 PAT 同步。";
                }
            }
        }

        private void ApplyFilterAndRefreshGrid()
        {
            List<DataSourceProbeItem> sourceList = (_currentReport != null && _currentReport.Probes != null)
                ? _currentReport.Probes
                : new List<DataSourceProbeItem>();

            List<DataSourceProbeItem> result = new List<DataSourceProbeItem>(sourceList.Count);

            for (int i = 0; i < sourceList.Count; i++)
            {
                DataSourceProbeItem item = sourceList[i];
                if (item == null) continue;

                if (!MatchesCategoryFilter(item, _activeCategoryIndex))
                    continue;
                if (!MatchesSearchKeyword(item, _searchKeyword))
                    continue;

                result.Add(item);
            }

            // 默认按状态优先级（Warning -> Active -> InstalledEmpty -> NotInstalled）及占用体积降序排列
            result.Sort(delegate(DataSourceProbeItem a, DataSourceProbeItem b)
            {
                int cmpRank = a.StatusRank.CompareTo(b.StatusRank);
                if (cmpRank != 0) return cmpRank;
                int cmpBytes = b.TotalBytes.CompareTo(a.TotalBytes);
                if (cmpBytes != 0) return cmpBytes;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            _filteredItems = result;

            if (_matchCountText != null)
            {
                _matchCountText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "显示 {0} / {1} 项",
                    result.Count,
                    sourceList.Count);
            }

            if (_emptyFilterOverlay != null)
            {
                _emptyFilterOverlay.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            ModernDataGridFactory.UpdateItemsPreservingState(
                _probeDataGrid,
                result,
                delegate(object obj)
                {
                    DataSourceProbeItem p = obj as DataSourceProbeItem;
                    return p != null ? (p.Category + "::" + p.Name) : "";
                },
                true);
        }

        private static bool MatchesCategoryFilter(DataSourceProbeItem item, int categoryIndex)
        {
            if (categoryIndex == 0)
                return true;
            if (categoryIndex == 1)
            {
                return string.Equals(item.Status, DataSourceProbeItem.StatusActive, StringComparison.Ordinal) ||
                       string.Equals(item.Status, DataSourceProbeItem.StatusWarning, StringComparison.Ordinal);
            }
            if (categoryIndex == 2)
                return string.Equals(item.Category, DataSourceProbeItem.CategoryNativeCli, StringComparison.Ordinal);
            if (categoryIndex == 3)
                return string.Equals(item.Category, DataSourceProbeItem.CategoryIdeDb, StringComparison.Ordinal);
            if (categoryIndex == 4)
                return string.Equals(item.Category, DataSourceProbeItem.CategoryWsl, StringComparison.Ordinal);
            return true;
        }

        private static bool MatchesSearchKeyword(DataSourceProbeItem item, string kw)
        {
            if (string.IsNullOrWhiteSpace(kw))
                return true;

            return ContainsIgnoreCase(item.Name, kw) ||
                   ContainsIgnoreCase(item.Category, kw) ||
                   ContainsIgnoreCase(item.StatusLabel, kw) ||
                   ContainsIgnoreCase(item.TargetPath, kw) ||
                   ContainsIgnoreCase(item.DiagnosticHint, kw);
        }

        private static bool ContainsIgnoreCase(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
                return false;
            return text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateSearchBoxAdornments()
        {
            bool hasText = _searchInputBox != null && !string.IsNullOrEmpty(_searchInputBox.Text);
            if (_searchWatermarkText != null)
                _searchWatermarkText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            if (_searchClearBtn != null)
                _searchClearBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetRefreshingVisualState(bool refreshing)
        {
            if (_refreshBtn != null)
                _refreshBtn.IsEnabled = !refreshing;
            if (_refreshBtnText != null)
                _refreshBtnText.Text = refreshing ? "检查中…" : "重新检查";
            if (refreshing && _bannerStatusText != null &&
                (_currentReport == null || _currentReport.TotalSourceCount == 0))
                _bannerStatusText.Text = "检查中";
        }

        private void OnCopySummaryClicked(object sender, RoutedEventArgs e)
        {
            SystemHealthReport report = _currentReport ?? TelemetryDoctorScanner.RunDiagnostics(_homeDir);
            string summary = TelemetryDoctorScanner.BuildSanitizedDiagnosticSummary(report);

            bool copied = false;
            try
            {
                Clipboard.SetDataObject(summary, true);
                copied = true;
            }
            catch
            {
                try
                {
                    Clipboard.SetText(summary);
                    copied = true;
                }
                catch { }
            }

            if (copied)
            {
                if (_copyBtnIcon != null)
                    _copyBtnIcon.Data = Geometry.Parse(UiIcons.Check);
                if (_copyBtnText != null)
                    _copyBtnText.Text = "已复制脱敏摘要";
                _copyResetTimer.Stop();
                _copyResetTimer.Start();

                if (_toastCallback != null)
                {
                    try { _toastCallback("已复制诊断摘要", "已自动隐藏用户名与敏感令牌信息，可直接粘贴排障", "success"); }
                    catch { }
                }
            }
        }

        private void OnCopyResetTimerTick(object sender, EventArgs e)
        {
            _copyResetTimer.Stop();
            if (_copyBtnIcon != null)
                _copyBtnIcon.Data = Geometry.Parse(UiIcons.Copy);
            if (_copyBtnText != null)
                _copyBtnText.Text = "复制诊断摘要";
        }

        private void OnRowOpenFolderClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            FrameworkElement fe = sender as FrameworkElement;
            if (fe == null) return;
            DataSourceProbeItem item = fe.DataContext as DataSourceProbeItem;
            if (item == null) return;
            OpenPathInExplorer(item.TargetPath);
        }

        private void OnDataGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_probeDataGrid == null) return;
            DataSourceProbeItem item = _probeDataGrid.SelectedItem as DataSourceProbeItem;
            if (item == null) return;
            OpenPathInExplorer(item.TargetPath);
        }

        private void OpenPathInExplorer(string targetPath)
        {
            string resolved = TelemetryDoctorScanner.ResolveOpenableDirectoryOrParent(targetPath);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                if (_toastCallback != null)
                {
                    try { _toastCallback("目录尚未创建", "该数据源在本机尚未安装或尚未生成本地目录", "warning"); }
                    catch { }
                }
                return;
            }

            try
            {
                long sz;
                long mt;
                bool isFile = TokenScanner.TryGetFileStatInternal(resolved, out sz, out mt) &&
                              File.Exists(TokenScanner.ToLongPath(resolved));

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = isFile
                    ? ("/select,\"" + resolved.Replace("\"", "") + "\"")
                    : ("\"" + resolved.Replace("\"", "") + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }
    }
}
