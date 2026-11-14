using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace TokLens
{
    /// <summary>
    /// 终端无头 CLI 模式与脚本自动化引擎。
    /// 支持自动附加父终端控制台、UTF-8 无 BOM 管道重定向、Unicode 对齐表格渲染、
    /// 状态栏极速摘要、静默云同步以及无头 HTML/JSON 报表导出。
    /// </summary>
    public static class CliRunner
    {
        private const int ATTACH_PARENT_PROCESS = -1;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;
        private const uint FILE_TYPE_DISK = 0x0001;
        private const uint FILE_TYPE_CHAR = 0x0002;
        private const uint FILE_TYPE_PIPE = 0x0003;
        private const uint CP_UTF8 = 65001;

        private static readonly char[] SparkBlocks = new char[]
        {
            '\u2581', '\u2582', '\u2583', '\u2584', '\u2585', '\u2586', '\u2587'
        };

        private static readonly string[] WeekdayLabels = new string[]
        {
            "周日", "周一", "周二", "周三", "周四", "周五", "周六"
        };

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        private sealed class CliOptions
        {
            public bool ShowHelp;
            public bool ShowVersion;
            public bool ShowSummary;
            public bool ShowStatus;
            public bool SyncNow;
            public string ExportHtmlPath;
            public string ExportJsonPath;
            public int Days;
            public bool DaysExplicit;
            public bool UseCny;
            public bool UseUsdExplicit;
            public bool OutputJson;
            public bool ForceWsl;
            public string CustomHome;
            public string ParseError;

            public CliOptions()
            {
                ExportHtmlPath = null;
                ExportJsonPath = null;
                Days = 30;
                CustomHome = null;
                ParseError = null;
            }
        }

        private sealed class ConsoleBinding : IDisposable
        {
            public TextWriter OutWriter;
            public TextWriter ErrWriter;
            public bool AttachedToParent;
            public bool IsOutputRedirected;

            public ConsoleBinding()
            {
                OutWriter = TextWriter.Null;
                ErrWriter = TextWriter.Null;
                AttachedToParent = false;
                IsOutputRedirected = false;
            }

            public void Dispose()
            {
                try { OutWriter.Flush(); } catch { }
                try { ErrWriter.Flush(); } catch { }
                if (AttachedToParent)
                {
                    try { FreeConsole(); } catch { }
                    AttachedToParent = false;
                }
            }
        }

        private sealed class AggregatedDataset
        {
            public SyncConfig Config;
            public ScanResult LocalScan;
            public NodeSnapshot LocalNode;
            public List<NodeSnapshot> RemoteNodes;
            public List<NodeSnapshot> AllNodes;
            public List<RecordBucket> AllRecords;
            public ExchangeRateInfo ExchangeRate;
            public SyncOutcome SyncResult;

            public AggregatedDataset()
            {
                RemoteNodes = new List<NodeSnapshot>();
                AllNodes = new List<NodeSnapshot>();
                AllRecords = new List<RecordBucket>();
            }
        }

        private sealed class NodeAggRow
        {
            public string MachineId;
            public string MachineName;
            public string MachineType;
            public bool IsLocal;
            public string SyncedAt;
            public HashSet<string> Tools;
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;

            public NodeAggRow()
            {
                Tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }
        }

        private sealed class ToolAggRow
        {
            public string Tool;
            public HashSet<string> Models;
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;

            public ToolAggRow()
            {
                Models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }
        }

        private sealed class ModelAggRow
        {
            public string Model;
            public Dictionary<string, double> ToolCostMap;
            public Dictionary<string, long> ToolTokMap;
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;

            public ModelAggRow()
            {
                ToolCostMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                ToolTokMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }

            public string PrimaryTool
            {
                get
                {
                    string best = "--";
                    double bestCost = -1.0;
                    long bestTok = -1L;
                    foreach (KeyValuePair<string, double> kv in ToolCostMap)
                    {
                        long tok = 0L;
                        ToolTokMap.TryGetValue(kv.Key, out tok);
                        if (kv.Value > bestCost + 1e-9 || (Math.Abs(kv.Value - bestCost) <= 1e-9 && tok > bestTok))
                        {
                            bestCost = kv.Value;
                            bestTok = tok;
                            best = kv.Key;
                        }
                    }
                    if (ToolCostMap.Count > 1)
                        return best + " (+" + (ToolCostMap.Count - 1).ToString(CultureInfo.InvariantCulture) + ")";
                    return best;
                }
            }
        }

        private sealed class DayAggRow
        {
            public string Date;
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;
            public double BaselineUsd;

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }
        }

        /// <summary>
        /// 判断命令行参数是否包含无头 CLI 子命令（排除 --minimized / --self-test-ui 等桌面 GUI 参数）。
        /// </summary>
        public static bool IsCliInvocation(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string arg = raw.Trim();
                // 显式排除桌面 GUI 自检或最小化参数
                if (EqualsIgnoreCase(arg, "--minimized") ||
                    EqualsIgnoreCase(arg, "-minimized") ||
                    EqualsIgnoreCase(arg, "/minimized") ||
                    EqualsIgnoreCase(arg, "--self-test-ui"))
                {
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--help") ||
                    EqualsIgnoreCase(arg, "-h") ||
                    EqualsIgnoreCase(arg, "/h") ||
                    EqualsIgnoreCase(arg, "/help") ||
                    EqualsIgnoreCase(arg, "/?") ||
                    EqualsIgnoreCase(arg, "-?") ||
                    EqualsIgnoreCase(arg, "--version") ||
                    EqualsIgnoreCase(arg, "-v") ||
                    EqualsIgnoreCase(arg, "/v") ||
                    EqualsIgnoreCase(arg, "/version") ||
                    EqualsIgnoreCase(arg, "--cli") ||
                    EqualsIgnoreCase(arg, "--summary") ||
                    EqualsIgnoreCase(arg, "--status") ||
                    EqualsIgnoreCase(arg, "--statusline") ||
                    EqualsIgnoreCase(arg, "--sync-now") ||
                    EqualsIgnoreCase(arg, "--export-html") ||
                    EqualsIgnoreCase(arg, "--export-json") ||
                    StartsWithOptionValue(arg, "--export-html=") ||
                    StartsWithOptionValue(arg, "--export-json="))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// CLI 主入口：若命中 CLI 子命令则附加终端（或绑定 UTF-8 无 BOM 重定向流）并执行完毕返回 true；
        /// 若无匹配的 CLI 参数或属于 GUI 启动参数，返回 false 交由 WPF 桌面主程序处理。
        /// </summary>
        public static bool TryHandleCommandLine(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (!IsCliInvocation(args))
                return false;

            using (ConsoleBinding binding = BindConsoleOrRedirectedStreams())
            {
                try
                {
                    // 在交互式父终端直接打印多行表格前留出清晰视觉间距（单行 --status 与 JSON/重定向除外）
                    bool isSingleLineStatus = HasStatusOnlyFlag(args);
                    if (binding.AttachedToParent && !binding.IsOutputRedirected && !isSingleLineStatus)
                    {
                        binding.OutWriter.WriteLine();
                    }

                    exitCode = Execute(args, binding.OutWriter, binding.ErrWriter);
                }
                catch (Exception ex)
                {
                    try
                    {
                        TextWriter errTarget = binding.ErrWriter != TextWriter.Null ? binding.ErrWriter : binding.OutWriter;
                        errTarget.WriteLine("[TokLens 错误] 执行命令行任务时发生未预期异常：" + ex.Message);
                    }
                    catch { }
                    exitCode = 1;
                }
            }
            return true;
        }

        /// <summary>
        /// 使用指定的输出流执行无头 CLI 命令逻辑，返回进程退出码（0 表示成功）。
        /// </summary>
        public static int Execute(string[] args, TextWriter stdOut, TextWriter stdErr)
        {
            TextWriter outWriter = stdOut ?? TextWriter.Null;
            TextWriter errWriter = (stdErr != null && stdErr != TextWriter.Null) ? stdErr : outWriter;

            CliOptions opts = ParseArguments(args);
            if (!string.IsNullOrEmpty(opts.ParseError))
            {
                errWriter.WriteLine("[TokLens 参数错误] " + opts.ParseError);
                errWriter.WriteLine("提示：运行 TokLens.exe --help 查看完整中文参数说明。");
                return 2;
            }

            if (opts.ShowHelp)
            {
                WriteHelp(outWriter);
                return 0;
            }

            if (opts.ShowVersion)
            {
                WriteVersion(outWriter, opts.OutputJson);
                return 0;
            }

            // 执行数据扫描与多节点快照合并（若包含 --sync-now 则一并执行云端同步）
            AggregatedDataset dataset = CollectDataset(opts);
            int finalExitCode = 0;

            if (opts.SyncNow)
            {
                bool syncOk = ReportSyncOutcome(outWriter, errWriter, dataset);
                if (!syncOk)
                    finalExitCode = 1;
            }

            if (!string.IsNullOrEmpty(opts.ExportHtmlPath))
            {
                bool htmlOk = ExportHtmlHeadless(outWriter, errWriter, dataset, opts.ExportHtmlPath, !opts.ShowStatus);
                if (!htmlOk)
                    finalExitCode = 1;
            }

            if (!string.IsNullOrEmpty(opts.ExportJsonPath))
            {
                bool jsonOk = ExportJsonHeadless(outWriter, errWriter, dataset, opts.ExportJsonPath, !opts.ShowStatus);
                if (!jsonOk)
                    finalExitCode = 1;
            }

            if (opts.ShowStatus)
            {
                WriteStatusLine(outWriter, dataset, opts);
            }
            else if (opts.ShowSummary)
            {
                WriteSummaryTables(outWriter, dataset, opts);
            }

            return finalExitCode;
        }

        private static bool HasStatusOnlyFlag(string[] args)
        {
            if (args == null) return false;
            bool hasStatus = false;
            bool hasMultiLine = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (EqualsIgnoreCase(a, "--status") || EqualsIgnoreCase(a, "--statusline") || EqualsIgnoreCase(a, "--json"))
                    hasStatus = true;
                if (EqualsIgnoreCase(a, "--cli") || EqualsIgnoreCase(a, "--summary") || EqualsIgnoreCase(a, "--help") || EqualsIgnoreCase(a, "-h"))
                    hasMultiLine = true;
            }
            return hasStatus && !hasMultiLine;
        }

        private static bool IsValidHandle(IntPtr handle)
        {
            return handle != IntPtr.Zero && handle != new IntPtr(-1);
        }

        private static bool IsHandleRedirected(IntPtr handle)
        {
            if (!IsValidHandle(handle))
                return false;
            try
            {
                uint ft = GetFileType(handle);
                if (ft == FILE_TYPE_DISK || ft == FILE_TYPE_PIPE)
                    return true;
            }
            catch { }
            return false;
        }

        private static bool IsCharConsoleHandle(IntPtr handle)
        {
            if (!IsValidHandle(handle))
                return false;
            try
            {
                return GetFileType(handle) == FILE_TYPE_CHAR;
            }
            catch
            {
                return false;
            }
        }

        private static ConsoleBinding BindConsoleOrRedirectedStreams()
        {
            ConsoleBinding binding = new ConsoleBinding();
            UTF8Encoding utf8NoBom = new UTF8Encoding(false);

            IntPtr hOutBefore = IntPtr.Zero;
            IntPtr hErrBefore = IntPtr.Zero;
            try { hOutBefore = GetStdHandle(STD_OUTPUT_HANDLE); } catch { }
            try { hErrBefore = GetStdHandle(STD_ERROR_HANDLE); } catch { }

            bool stdoutRedirected = IsHandleRedirected(hOutBefore);
            bool stderrRedirected = IsHandleRedirected(hErrBefore);
            try
            {
                if (!stdoutRedirected && Console.IsOutputRedirected)
                    stdoutRedirected = true;
            }
            catch { }

            binding.IsOutputRedirected = stdoutRedirected;

            if (stdoutRedirected)
            {
                // 标准输出已被管道或文件重定向：不调用 AttachConsole，直接以 UTF-8 无 BOM 流式写出
                try
                {
                    Stream rawOut = Console.OpenStandardOutput();
                    if (rawOut != null && rawOut != Stream.Null)
                    {
                        StreamWriter swOut = new StreamWriter(rawOut, utf8NoBom);
                        swOut.AutoFlush = true;
                        binding.OutWriter = swOut;
                    }
                }
                catch { }

                if (stderrRedirected)
                {
                    try
                    {
                        Stream rawErr = Console.OpenStandardError();
                        if (rawErr != null && rawErr != Stream.Null)
                        {
                            StreamWriter swErr = new StreamWriter(rawErr, utf8NoBom);
                            swErr.AutoFlush = true;
                            binding.ErrWriter = swErr;
                        }
                    }
                    catch { }
                }

                if (binding.ErrWriter == TextWriter.Null)
                    binding.ErrWriter = binding.OutWriter;

                return binding;
            }

            // 标准输出未重定向：若当前是 GUI 子系统且无控制台，自动附加到父终端进程
            bool hasConsole = IsCharConsoleHandle(hOutBefore);
            if (!hasConsole)
            {
                try
                {
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                    {
                        binding.AttachedToParent = true;
                        hasConsole = true;
                    }
                }
                catch { }
            }

            if (hasConsole)
            {
                try { SetConsoleOutputCP(CP_UTF8); } catch { }
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }

                try
                {
                    Stream stdOut = Console.OpenStandardOutput();
                    if (stdOut != null && stdOut != Stream.Null)
                    {
                        StreamWriter swOut = new StreamWriter(stdOut, utf8NoBom);
                        swOut.AutoFlush = true;
                        Console.SetOut(swOut);
                        binding.OutWriter = swOut;
                    }
                }
                catch { }

                try
                {
                    Stream stdErr = Console.OpenStandardError();
                    if (stdErr != null && stdErr != Stream.Null)
                    {
                        StreamWriter swErr = new StreamWriter(stdErr, utf8NoBom);
                        swErr.AutoFlush = true;
                        Console.SetError(swErr);
                        binding.ErrWriter = swErr;
                    }
                }
                catch { }

                if (binding.ErrWriter == TextWriter.Null)
                    binding.ErrWriter = binding.OutWriter;
            }
            else if (stderrRedirected)
            {
                // 仅重定向了标准错误的后台任务场景
                try
                {
                    Stream rawErrOnly = Console.OpenStandardError();
                    if (rawErrOnly != null && rawErrOnly != Stream.Null)
                    {
                        StreamWriter swErrOnly = new StreamWriter(rawErrOnly, utf8NoBom);
                        swErrOnly.AutoFlush = true;
                        binding.ErrWriter = swErrOnly;
                    }
                }
                catch { }
            }

            return binding;
        }

        private static CliOptions ParseArguments(string[] args)
        {
            CliOptions opts = new CliOptions();
            if (args == null) return opts;

            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string arg = raw.Trim();

                if (EqualsIgnoreCase(arg, "--help") || EqualsIgnoreCase(arg, "-h") || EqualsIgnoreCase(arg, "/h") || EqualsIgnoreCase(arg, "/help") || EqualsIgnoreCase(arg, "/?") || EqualsIgnoreCase(arg, "-?"))
                {
                    opts.ShowHelp = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--version") || EqualsIgnoreCase(arg, "-v") || EqualsIgnoreCase(arg, "/v") || EqualsIgnoreCase(arg, "/version"))
                {
                    opts.ShowVersion = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--cli") || EqualsIgnoreCase(arg, "--summary"))
                {
                    opts.ShowSummary = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--status") || EqualsIgnoreCase(arg, "--statusline"))
                {
                    opts.ShowStatus = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--sync-now"))
                {
                    opts.SyncNow = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--cny"))
                {
                    opts.UseCny = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--usd"))
                {
                    opts.UseUsdExplicit = true;
                    opts.UseCny = false;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--json"))
                {
                    opts.OutputJson = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--wsl"))
                {
                    opts.ForceWsl = true;
                    continue;
                }

                if (EqualsIgnoreCase(arg, "--all"))
                {
                    opts.Days = 0;
                    opts.DaysExplicit = true;
                    continue;
                }

                string inlineVal;
                if (TryExtractOptionValue(args, ref i, "--days", out inlineVal))
                {
                    if (string.IsNullOrWhiteSpace(inlineVal))
                    {
                        opts.ParseError = "选项 --days 需要指定天数（例如 --days 30，或 --days 0 表示全部历史）。";
                        return opts;
                    }
                    if (EqualsIgnoreCase(inlineVal.Trim(), "all"))
                    {
                        opts.Days = 0;
                        opts.DaysExplicit = true;
                        continue;
                    }
                    int parsedDays;
                    if (!int.TryParse(inlineVal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedDays) || parsedDays < 0 || parsedDays > 36500)
                    {
                        opts.ParseError = "选项 --days 的值无效（\"" + inlineVal + "\"），请输入 0 ~ 36500 之间的整数。";
                        return opts;
                    }
                    opts.Days = parsedDays;
                    opts.DaysExplicit = true;
                    continue;
                }

                if (TryExtractOptionValue(args, ref i, "--export-html", out inlineVal))
                {
                    if (string.IsNullOrWhiteSpace(inlineVal) || inlineVal.Trim().StartsWith("-", StringComparison.Ordinal))
                    {
                        opts.ParseError = "选项 --export-html 需要指定输出文件路径（例如 --export-html .\\toklens-report.html）。";
                        return opts;
                    }
                    opts.ExportHtmlPath = inlineVal.Trim();
                    continue;
                }

                if (TryExtractOptionValue(args, ref i, "--export-json", out inlineVal))
                {
                    if (string.IsNullOrWhiteSpace(inlineVal) || inlineVal.Trim().StartsWith("-", StringComparison.Ordinal))
                    {
                        opts.ParseError = "选项 --export-json 需要指定输出文件路径（例如 --export-json .\\toklens-snapshot.json）。";
                        return opts;
                    }
                    opts.ExportJsonPath = inlineVal.Trim();
                    continue;
                }

                if (TryExtractOptionValue(args, ref i, "--home", out inlineVal))
                {
                    if (string.IsNullOrWhiteSpace(inlineVal))
                    {
                        opts.ParseError = "选项 --home 需要指定有效的用户主目录路径。";
                        return opts;
                    }
                    opts.CustomHome = inlineVal.Trim();
                    continue;
                }
            }

            return opts;
        }

        private static bool TryExtractOptionValue(string[] args, ref int index, string optionName, out string value)
        {
            value = null;
            string current = (args[index] ?? "").Trim();
            string prefix = optionName + "=";

            if (StartsWithOptionValue(current, prefix))
            {
                value = current.Substring(prefix.Length).Trim().Trim('"');
                return true;
            }

            if (EqualsIgnoreCase(current, optionName))
            {
                if (index + 1 < args.Length)
                {
                    index++;
                    value = (args[index] ?? "").Trim().Trim('"');
                }
                else
                {
                    value = "";
                }
                return true;
            }

            return false;
        }

        private static bool EqualsIgnoreCase(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartsWithOptionValue(string text, string prefix)
        {
            return text != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static AggregatedDataset CollectDataset(CliOptions opts)
        {
            AggregatedDataset ds = new AggregatedDataset();
            SyncConfig cfg = SyncService.LoadConfig();
            ds.Config = cfg;

            string homeDir = !string.IsNullOrWhiteSpace(opts.CustomHome)
                ? opts.CustomHome
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            bool scanWsl = opts.ForceWsl || cfg.ScanLocalWsl;
            TokenScanner scanner = new TokenScanner(homeDir, scanWsl);
            ScanResult scanRes = scanner.Run();
            ds.LocalScan = scanRes;

            NodeSnapshot localNode = SyncService.BuildLocalSnapshot(scanRes, cfg);
            List<NodeSnapshot> remotes;

            if (opts.SyncNow)
            {
                SyncOutcome outcome = SyncService.SyncNow(cfg, localNode, false);
                ds.SyncResult = outcome;
                // SyncNow 可能将云端本机历史高水位合并回本地，重新构建一次以包含完整记录
                localNode = SyncService.BuildLocalSnapshot(scanRes, cfg);
                remotes = outcome.RemoteNodes ?? new List<NodeSnapshot>();
            }
            else
            {
                remotes = SyncService.LoadCachedRemoteNodes(localNode.MachineId);
            }

            PricingEngine pricing = new PricingEngine(homeDir);
            ApplyNodeDisplayAlias(localNode, cfg);
            ds.LocalNode = localNode;
            ds.AllNodes.Add(localNode);

            for (int i = 0; i < remotes.Count; i++)
            {
                NodeSnapshot rn = remotes[i];
                if (rn == null) continue;
                pricing.RecalculateRemoteSnapshot(rn);
                ApplyNodeDisplayAlias(rn, cfg);
                ds.RemoteNodes.Add(rn);
                ds.AllNodes.Add(rn);
            }

            for (int n = 0; n < ds.AllNodes.Count; n++)
            {
                NodeSnapshot node = ds.AllNodes[n];
                if (node == null || node.Records == null) continue;
                string mid = string.IsNullOrEmpty(node.MachineId) ? "local" : node.MachineId;
                string mname = string.IsNullOrEmpty(node.MachineName) ? (node.IsLocal ? "本机" : mid) : node.MachineName;
                string mtype = string.IsNullOrEmpty(node.MachineType) ? (node.IsLocal ? "windows" : "vps") : node.MachineType;

                for (int r = 0; r < node.Records.Count; r++)
                {
                    RecordBucket b = node.Records[r];
                    if (b == null) continue;
                    RecordBucket copy = b.Clone();
                    copy.MachineId = mid;
                    copy.MachineName = mname;
                    copy.MachineType = mtype;
                    copy.CostUsd = SafeDouble(copy.CostUsd);
                    copy.BaselineUsd = SafeDouble(copy.BaselineUsd);
                    ds.AllRecords.Add(copy);
                }
            }

            bool isRecentFx;
            ds.ExchangeRate = ExchangeRateService.LoadCachedOrDefault(out isRecentFx);
            return ds;
        }

        private static void ApplyNodeDisplayAlias(NodeSnapshot snap, SyncConfig cfg)
        {
            if (snap == null) return;
            string displayName = snap.MachineName;
            if (cfg != null && cfg.NodeAliases != null && !string.IsNullOrEmpty(snap.MachineId))
            {
                string alias;
                if (cfg.NodeAliases.TryGetValue(snap.MachineId, out alias) && !string.IsNullOrWhiteSpace(alias))
                    displayName = alias.Trim();
            }
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = snap.IsLocal ? "本机" : snap.MachineId;

            snap.MachineName = displayName;
            if (snap.Records != null)
            {
                for (int i = 0; i < snap.Records.Count; i++)
                {
                    if (snap.Records[i] != null)
                        snap.Records[i].MachineName = displayName;
                }
            }
        }

        private static void WriteHelp(TextWriter w)
        {
            w.WriteLine(AppConstants.ProductTitle + " — 终端无头 CLI 与脚本自动化模式");
            w.WriteLine("多设备与 Linux VPS 的 AI Coding Token 实时监控、状态栏摘要与账单透镜");
            w.WriteLine();
            w.WriteLine("用法:");
            w.WriteLine("  TokLens.exe [子命令] [修饰选项]      (无参数启动时进入桌面可视化看板)");
            w.WriteLine();

            string[] cmdHeaders = new string[] { "子命令 / 参数", "功能说明" };
            bool[] cmdRight = new bool[] { false, false };
            List<string[]> cmdRows = new List<string[]>();
            cmdRows.Add(new string[] { "--cli, --summary", "极速扫描并合并多节点快照，打印终端 Unicode 边框账单表" });
            cmdRows.Add(new string[] { "--status, --statusline", "极速输出单行状态栏摘要 (适配 WT / WezTerm / Tmux / Neovim)" });
            cmdRows.Add(new string[] { "--sync-now", "无头扫描并立即同步云端节点 (适合放入 Windows 任务计划程序)" });
            cmdRows.Add(new string[] { "--export-html <path>", "无头导出单文件交互式 HTML 多端汇总看板到指定路径" });
            cmdRows.Add(new string[] { "--export-json <path>", "无头导出完整多端聚合 JSON 快照到指定路径 (UTF-8 无 BOM)" });
            cmdRows.Add(new string[] { "--version, -v", "查看程序版本、运行时环境及本地配置与数据库目录" });
            cmdRows.Add(new string[] { "--help, -h", "显示本中文 CLI 参数速查表" });
            RenderBoxTable(w, cmdHeaders, cmdRight, cmdRows, null);

            w.WriteLine();
            string[] optHeaders = new string[] { "修饰选项", "默认值", "适用范围与说明" };
            bool[] optRight = new bool[] { false, false, false };
            List<string[]> optRows = new List<string[]>();
            optRows.Add(new string[] { "--days <N>", "30", "限定 --cli / --summary 统计最近 N 天 (传 0 或 --all 统计全部)" });
            optRows.Add(new string[] { "--cny / --usd", "USD ($)", "切换终端表格与状态栏的显示币种 (--cny 使用人民币 ¥)" });
            optRows.Add(new string[] { "--json", "关闭", "配合 --status 或 --version 输出紧凑单行 JSON 供脚本解析" });
            optRows.Add(new string[] { "--wsl", "跟随配置", "扫描时额外穿透统计本机 WSL Linux 发行版日志目录" });
            optRows.Add(new string[] { "--home <dir>", "当前用户", "指定扫描的目标用户主目录路径" });
            RenderBoxTable(w, optHeaders, optRight, optRows, null);

            w.WriteLine();
            w.WriteLine("常用示例:");
            w.WriteLine("  TokLens.exe --cli --days 30 --cny");
            w.WriteLine("  TokLens.exe --status");
            w.WriteLine("  TokLens.exe --status --json > out.json");
            w.WriteLine("  TokLens.exe --sync-now");
            w.WriteLine("  TokLens.exe --export-html .\\toklens-report.html --export-json .\\toklens.json");
        }

        private static void WriteVersion(TextWriter w, bool asJson)
        {
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string clrVer = Environment.Version.ToString();
            string osVer = Environment.OSVersion.VersionString;

            if (asJson)
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["product"] = AppConstants.ProductTitle;
                payload["version"] = AppConstants.Version;
                payload["schema"] = SyncService.SchemaVersion;
                payload["clr"] = clrVer;
                payload["arch"] = arch;
                payload["os"] = osVer;
                payload["config_dir"] = SyncService.AppConfigDir;
                payload["config_file"] = SyncService.ConfigFilePath;
                payload["nodes_cache_dir"] = SyncService.NodesCacheDir;
                payload["db_file"] = LocalUsageStore.DbFilePath;
                payload["pricing_cache_file"] = PricingEngine.CloudPriceCachePath;
                JavaScriptSerializer js = NewJson();
                w.WriteLine(js.Serialize(payload));
                return;
            }

            w.WriteLine(AppConstants.ProductTitle);
            w.WriteLine("  同步协议版本 : " + SyncService.SchemaVersion);
            w.WriteLine("  .NET 运行时  : CLR " + clrVer + " (" + arch + ") / " + osVer);
            w.WriteLine("  配置根目录   : " + SyncService.AppConfigDir);
            w.WriteLine("  主配置文件   : " + SyncService.ConfigFilePath);
            w.WriteLine("  节点缓存目录 : " + SyncService.NodesCacheDir);
            w.WriteLine("  本地游标库   : " + LocalUsageStore.DbFilePath);
            w.WriteLine("  云端价格缓存 : " + PricingEngine.CloudPriceCachePath);
        }

        private static bool ReportSyncOutcome(TextWriter stdOut, TextWriter stdErr, AggregatedDataset ds)
        {
            SyncConfig cfg = ds.Config;
            SyncOutcome outcome = ds.SyncResult;
            string providerLabel = (cfg != null && EqualsIgnoreCase(cfg.Provider, "webdav"))
                ? "WebDAV"
                : "GitHub Gist";

            int localBucketCount = (ds.LocalNode != null && ds.LocalNode.Records != null) ? ds.LocalNode.Records.Count : 0;
            string elapsedText = ds.LocalScan != null
                ? ds.LocalScan.ElapsedSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s"
                : "0.00s";

            if (outcome == null)
            {
                stdErr.WriteLine("[TokLens 同步] 未能获取同步执行结果。");
                return false;
            }

            if (!outcome.Success)
            {
                stdErr.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "[TokLens 同步未完成] 通道: {0} | 原因: {1} | 本地扫描耗时: {2} (本机记录桶: {3}, 本地缓存远程设备: {4} 台)",
                    providerLabel,
                    outcome.Message ?? "未知错误",
                    elapsedText,
                    localBucketCount,
                    outcome.RemoteNodeCount));
                return false;
            }

            stdOut.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[TokLens 同步成功] 通道: {0} | {1} | 远程设备数: {2} 台 | 本地扫描耗时: {3} (本机记录桶: {4})",
                providerLabel,
                outcome.Message ?? "已完成同步",
                outcome.RemoteNodeCount,
                elapsedText,
                localBucketCount));

            if (ds.RemoteNodes != null && ds.RemoteNodes.Count > 0)
            {
                for (int i = 0; i < ds.RemoteNodes.Count; i++)
                {
                    NodeSnapshot rn = ds.RemoteNodes[i];
                    if (rn == null) continue;
                    double nodeUsd = 0.0;
                    long nodeTok = 0L;
                    if (rn.Records != null)
                    {
                        for (int k = 0; k < rn.Records.Count; k++)
                        {
                            nodeUsd += SafeDouble(rn.Records[k].CostUsd);
                            nodeTok += rn.Records[k].TotalTokens;
                        }
                    }
                    stdOut.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "  - 远程节点 [{0}] ({1}) : 累计 {2} tok, ${3:F2}, 最近同步 {4}",
                        rn.MachineName ?? rn.MachineId,
                        rn.MachineType ?? "vps",
                        FormatterUtil.FormatTokens(nodeTok),
                        nodeUsd,
                        string.IsNullOrEmpty(rn.SyncedAt) ? "--" : rn.SyncedAt));
                }
            }

            return true;
        }

        private static string ResolveOutputFilePath(string rawPath, string defaultFileName)
        {
            string fullPath = Path.GetFullPath(rawPath);
            if (Directory.Exists(fullPath))
                fullPath = Path.Combine(fullPath, defaultFileName);

            string parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            return fullPath;
        }

        private static bool ExportHtmlHeadless(TextWriter stdOut, TextWriter stdErr, AggregatedDataset ds, string rawPath, bool verbose)
        {
            try
            {
                string defaultName = "toklens-report-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".html";
                string outPath = ResolveOutputFilePath(rawPath, defaultName);
                double rate = (ds.ExchangeRate != null && ds.ExchangeRate.Rate > 0.0) ? ds.ExchangeRate.Rate : ExchangeRateService.DefaultRate;

                HtmlExporter.ExportHtmlReport(ds.AllRecords, ds.LocalScan, rate, outPath);

                if (verbose)
                {
                    long bytes = 0L;
                    try { bytes = new FileInfo(outPath).Length; } catch { }
                    stdOut.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "[TokLens 导出完成] HTML 看板已生成: {0} ({1:F1} KB, 聚合 {2} 台设备 / {3} 条记录桶)",
                        outPath,
                        bytes / 1024.0,
                        ds.AllNodes.Count,
                        ds.AllRecords.Count));
                }
                return true;
            }
            catch (Exception ex)
            {
                stdErr.WriteLine("[TokLens 导出失败] 无法写入 HTML 看板 (" + rawPath + ")：" + ex.Message);
                return false;
            }
        }

        private static bool ExportJsonHeadless(TextWriter stdOut, TextWriter stdErr, AggregatedDataset ds, string rawPath, bool verbose)
        {
            try
            {
                string defaultName = "toklens-snapshot-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json";
                string outPath = ResolveOutputFilePath(rawPath, defaultName);
                string json = BuildAggregateJsonSnapshot(ds);
                File.WriteAllText(outPath, json, new UTF8Encoding(false));

                if (verbose)
                {
                    long bytes = 0L;
                    try { bytes = new FileInfo(outPath).Length; } catch { }
                    stdOut.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "[TokLens 导出完成] JSON 聚合快照已生成: {0} ({1:F1} KB, 聚合 {2} 台设备 / {3} 条记录桶)",
                        outPath,
                        bytes / 1024.0,
                        ds.AllNodes.Count,
                        ds.AllRecords.Count));
                }
                return true;
            }
            catch (Exception ex)
            {
                stdErr.WriteLine("[TokLens 导出失败] 无法写入 JSON 快照 (" + rawPath + ")：" + ex.Message);
                return false;
            }
        }

        private static string BuildAggregateJsonSnapshot(AggregatedDataset ds)
        {
            double rate = (ds.ExchangeRate != null && ds.ExchangeRate.Rate > 0.0) ? ds.ExchangeRate.Rate : ExchangeRateService.DefaultRate;
            string rateSource = ds.ExchangeRate != null ? (ds.ExchangeRate.Source ?? "") : "内置默认";
            string todayStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string monthPrefix = DateTime.Now.ToString("yyyy-MM-", CultureInfo.InvariantCulture);

            int totTurns = 0;
            long totFi = 0L, totCr = 0L, totCw = 0L, totOut = 0L;
            double totCost = 0.0, totBase = 0.0;
            double todayUsd = 0.0, monthUsd = 0.0;
            long todayTokens = 0L, monthTokens = 0L;

            Dictionary<string, ToolAggRow> toolMap = new Dictionary<string, ToolAggRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ModelAggRow> modelMap = new Dictionary<string, ModelAggRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DayAggRow> dayMap = new Dictionary<string, DayAggRow>(StringComparer.Ordinal);
            List<Dictionary<string, object>> recordItems = new List<Dictionary<string, object>>(ds.AllRecords.Count);

            for (int i = 0; i < ds.AllRecords.Count; i++)
            {
                RecordBucket r = ds.AllRecords[i];
                if (r == null) continue;

                string date = string.IsNullOrEmpty(r.Date) ? "未知日期" : r.Date;
                string tool = string.IsNullOrEmpty(r.Tool) ? "未知工具" : r.Tool;
                string model = string.IsNullOrEmpty(r.Model) ? "unknown" : r.Model;
                double cost = SafeDouble(r.CostUsd);
                double baseCost = SafeDouble(r.BaselineUsd);
                long recTot = r.TotalTokens;

                totTurns += r.Turns;
                totFi += r.FreshInput;
                totCr += r.CacheRead;
                totCw += r.CacheWrite;
                totOut += r.Output;
                totCost += cost;
                totBase += baseCost;

                if (string.Equals(date, todayStr, StringComparison.Ordinal))
                {
                    todayUsd += cost;
                    todayTokens += recTot;
                }
                if (date.StartsWith(monthPrefix, StringComparison.Ordinal))
                {
                    monthUsd += cost;
                    monthTokens += recTot;
                }

                ToolAggRow ta;
                if (!toolMap.TryGetValue(tool, out ta))
                {
                    ta = new ToolAggRow();
                    ta.Tool = tool;
                    toolMap[tool] = ta;
                }
                ta.Models.Add(model);
                ta.Turns += r.Turns;
                ta.FreshInput += r.FreshInput;
                ta.CacheRead += r.CacheRead;
                ta.CacheWrite += r.CacheWrite;
                ta.Output += r.Output;
                ta.CostUsd += cost;

                ModelAggRow ma;
                if (!modelMap.TryGetValue(model, out ma))
                {
                    ma = new ModelAggRow();
                    ma.Model = model;
                    modelMap[model] = ma;
                }
                ma.Turns += r.Turns;
                ma.FreshInput += r.FreshInput;
                ma.CacheRead += r.CacheRead;
                ma.CacheWrite += r.CacheWrite;
                ma.Output += r.Output;
                ma.CostUsd += cost;
                double prevToolCost = 0.0;
                ma.ToolCostMap.TryGetValue(tool, out prevToolCost);
                ma.ToolCostMap[tool] = prevToolCost + cost;
                long prevToolTok = 0L;
                ma.ToolTokMap.TryGetValue(tool, out prevToolTok);
                ma.ToolTokMap[tool] = prevToolTok + recTot;

                DayAggRow da;
                if (!dayMap.TryGetValue(date, out da))
                {
                    da = new DayAggRow();
                    da.Date = date;
                    dayMap[date] = da;
                }
                da.Turns += r.Turns;
                da.FreshInput += r.FreshInput;
                da.CacheRead += r.CacheRead;
                da.CacheWrite += r.CacheWrite;
                da.Output += r.Output;
                da.CostUsd += cost;
                da.BaselineUsd += baseCost;

                Dictionary<string, object> rd = new Dictionary<string, object>();
                rd["date"] = date;
                rd["machine_id"] = r.MachineId ?? "";
                rd["machine_name"] = r.MachineName ?? "本机";
                rd["machine_type"] = r.MachineType ?? "windows";
                rd["tool"] = tool;
                rd["model"] = model;
                rd["turns"] = r.Turns;
                rd["fresh_input"] = r.FreshInput;
                rd["cache_read"] = r.CacheRead;
                rd["cache_write"] = r.CacheWrite;
                rd["output"] = r.Output;
                rd["total_tokens"] = recTot;
                rd["cost_usd"] = Math.Round(cost, 6);
                rd["baseline_usd"] = Math.Round(baseCost, 6);
                rd["rate_label"] = r.RateLabel ?? "";
                recordItems.Add(rd);
            }

            List<Dictionary<string, object>> nodeList = new List<Dictionary<string, object>>(ds.AllNodes.Count);
            for (int i = 0; i < ds.AllNodes.Count; i++)
            {
                NodeSnapshot n = ds.AllNodes[i];
                if (n == null) continue;
                int nTurns = 0;
                long nTok = 0L;
                double nCost = 0.0;
                if (n.Records != null)
                {
                    for (int k = 0; k < n.Records.Count; k++)
                    {
                        nTurns += n.Records[k].Turns;
                        nTok += n.Records[k].TotalTokens;
                        nCost += SafeDouble(n.Records[k].CostUsd);
                    }
                }
                Dictionary<string, object> nd = new Dictionary<string, object>();
                nd["machine_id"] = n.MachineId ?? "";
                nd["machine_name"] = n.MachineName ?? "本机";
                nd["machine_type"] = n.MachineType ?? "windows";
                nd["is_local"] = n.IsLocal;
                nd["os"] = n.OsInfo ?? "";
                nd["synced_at"] = n.SyncedAt ?? "";
                nd["turns"] = nTurns;
                nd["total_tokens"] = nTok;
                nd["cost_usd"] = Math.Round(nCost, 4);
                nd["cost_cny"] = Math.Round(nCost * rate, 2);
                nodeList.Add(nd);
            }

            List<ToolAggRow> sortedTools = new List<ToolAggRow>(toolMap.Values);
            sortedTools.Sort(CompareToolAggDesc);
            List<Dictionary<string, object>> byTool = new List<Dictionary<string, object>>(sortedTools.Count);
            for (int i = 0; i < sortedTools.Count; i++)
            {
                ToolAggRow t = sortedTools[i];
                Dictionary<string, object> td = new Dictionary<string, object>();
                td["tool"] = t.Tool;
                td["model_count"] = t.Models.Count;
                td["turns"] = t.Turns;
                td["fresh_input"] = t.FreshInput;
                td["cache_read"] = t.CacheRead;
                td["cache_write"] = t.CacheWrite;
                td["output"] = t.Output;
                td["total_tokens"] = t.TotalTokens;
                td["cost_usd"] = Math.Round(t.CostUsd, 4);
                td["cost_cny"] = Math.Round(t.CostUsd * rate, 2);
                td["share_pct"] = totCost > 0.0 ? Math.Round(t.CostUsd * 100.0 / totCost, 2) : 0.0;
                byTool.Add(td);
            }

            List<ModelAggRow> sortedModels = new List<ModelAggRow>(modelMap.Values);
            sortedModels.Sort(CompareModelAggDesc);
            List<Dictionary<string, object>> byModel = new List<Dictionary<string, object>>(sortedModels.Count);
            for (int i = 0; i < sortedModels.Count; i++)
            {
                ModelAggRow m = sortedModels[i];
                long mPrompt = m.FreshInput + m.CacheRead + m.CacheWrite;
                double mHit = mPrompt > 0L ? (m.CacheRead * 100.0 / mPrompt) : 0.0;
                Dictionary<string, object> md = new Dictionary<string, object>();
                md["model"] = m.Model;
                md["primary_tool"] = m.PrimaryTool;
                md["turns"] = m.Turns;
                md["fresh_input"] = m.FreshInput;
                md["cache_read"] = m.CacheRead;
                md["cache_write"] = m.CacheWrite;
                md["output"] = m.Output;
                md["total_tokens"] = m.TotalTokens;
                md["cache_hit_pct"] = Math.Round(mHit, 1);
                md["cost_usd"] = Math.Round(m.CostUsd, 4);
                md["cost_cny"] = Math.Round(m.CostUsd * rate, 2);
                md["share_pct"] = totCost > 0.0 ? Math.Round(m.CostUsd * 100.0 / totCost, 2) : 0.0;
                byModel.Add(md);
            }

            List<DayAggRow> sortedDays = new List<DayAggRow>(dayMap.Values);
            sortedDays.Sort(delegate(DayAggRow a, DayAggRow b)
            {
                return string.CompareOrdinal(b.Date, a.Date);
            });
            List<Dictionary<string, object>> byDate = new List<Dictionary<string, object>>(sortedDays.Count);
            for (int i = 0; i < sortedDays.Count; i++)
            {
                DayAggRow d = sortedDays[i];
                Dictionary<string, object> dd = new Dictionary<string, object>();
                dd["date"] = d.Date;
                dd["turns"] = d.Turns;
                dd["fresh_input"] = d.FreshInput;
                dd["cache_read"] = d.CacheRead;
                dd["cache_write"] = d.CacheWrite;
                dd["output"] = d.Output;
                dd["total_tokens"] = d.TotalTokens;
                dd["cost_usd"] = Math.Round(d.CostUsd, 4);
                dd["cost_cny"] = Math.Round(d.CostUsd * rate, 2);
                byDate.Add(dd);
            }

            List<Dictionary<string, object>> recentSample = new List<Dictionary<string, object>>();
            if (ds.LocalScan != null && ds.LocalScan.RecentRequests != null)
            {
                int limit = Math.Min(ds.LocalScan.RecentRequests.Count, 50);
                for (int i = 0; i < limit; i++)
                {
                    RecentRequestRecord rq = ds.LocalScan.RecentRequests[i];
                    if (rq == null) continue;
                    Dictionary<string, object> rqd = new Dictionary<string, object>();
                    rqd["timestamp_ms"] = rq.TimestampMs;
                    rqd["date"] = rq.Date ?? "";
                    rqd["time"] = rq.TimeLabel ?? "";
                    rqd["tool"] = rq.Tool ?? "";
                    rqd["model"] = rq.Model ?? "";
                    rqd["fresh_input"] = rq.FreshInput;
                    rqd["cache_read"] = rq.CacheRead;
                    rqd["cache_write"] = rq.CacheWrite;
                    rqd["output"] = rq.Output;
                    rqd["total_tokens"] = rq.TotalTokens;
                    int measuredDuration=UsageTiming.IsMeasured(rq.TimingSource)?rq.DurationMs:0;
                    double requestTps=FormatterUtil.ComputeTps(rq.Output,measuredDuration);
                    rqd["duration_ms"] = measuredDuration;
                    rqd["tps"] = requestTps;
                    rqd["timing_source"] = rq.TimingSource;
                    rqd["tps_kind"] = requestTps > 0 ? "request_average" : "unavailable";
                    rqd["cost_usd"] = Math.Round(SafeDouble(rq.CostUsd), 6);
                    recentSample.Add(rqd);
                }
            }

            long totTokens = totFi + totCr + totCw + totOut;
            long totPrompt = totFi + totCr + totCw;
            double cacheHitPct = totPrompt > 0L ? (totCr * 100.0 / totPrompt) : 0.0;
            double savedUsd = Math.Max(0.0, totBase - totCost);

            Dictionary<string, object> summary = new Dictionary<string, object>();
            summary["node_count"] = ds.AllNodes.Count;
            summary["tool_count"] = toolMap.Count;
            summary["model_count"] = modelMap.Count;
            summary["turns"] = totTurns;
            summary["fresh_input"] = totFi;
            summary["cache_read"] = totCr;
            summary["cache_write"] = totCw;
            summary["output"] = totOut;
            summary["total_tokens"] = totTokens;
            summary["cache_hit_pct"] = Math.Round(cacheHitPct, 2);
            summary["cost_usd"] = Math.Round(totCost, 4);
            summary["cost_cny"] = Math.Round(totCost * rate, 2);
            summary["baseline_usd"] = Math.Round(totBase, 4);
            summary["saved_usd"] = Math.Round(savedUsd, 4);
            summary["today_usd"] = Math.Round(todayUsd, 4);
            summary["today_tokens"] = todayTokens;
            summary["month_usd"] = Math.Round(monthUsd, 4);
            summary["month_tokens"] = monthTokens;

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = "toklens.aggregate.v2.2";
            root["app"] = AppConstants.ProductTitle;
            root["version"] = AppConstants.Version;
            root["generated_at"] = ds.LocalScan != null ? ds.LocalScan.GeneratedAt : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            root["reference_date"] = ds.LocalScan != null ? ds.LocalScan.ReferenceDate : todayStr;
            root["elapsed_seconds"] = ds.LocalScan != null ? Math.Round(ds.LocalScan.ElapsedSeconds, 4) : 0.0;
            root["exchange_rate"] = Math.Round(rate, 4);
            root["exchange_rate_source"] = rateSource;
            root["summary"] = summary;
            root["nodes"] = nodeList;
            root["by_tool"] = byTool;
            root["by_model"] = byModel;
            root["by_date"] = byDate;
            root["records"] = recordItems;
            root["recent_requests"] = recentSample;

            JavaScriptSerializer js = NewJson();
            return js.Serialize(root);
        }

        private static void WriteStatusLine(TextWriter w, AggregatedDataset ds, CliOptions opts)
        {
            string todayStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string monthPrefix = DateTime.Now.ToString("yyyy-MM-", CultureInfo.InvariantCulture);
            double rate = (ds.ExchangeRate != null && ds.ExchangeRate.Rate > 0.0) ? ds.ExchangeRate.Rate : ExchangeRateService.DefaultRate;

            double todayUsd = 0.0;
            long todayTokens = 0L;
            long todayFi = 0L, todayCr = 0L, todayCw = 0L;

            double monthUsd = 0.0;
            long monthFi = 0L, monthCr = 0L, monthCw = 0L;

            long allFi = 0L, allCr = 0L, allCw = 0L;

            Dictionary<string, double> todayToolCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> todayToolTok = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> todayModelCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> todayModelTok = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, double> monthToolCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> monthToolTok = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> monthModelCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> monthModelTok = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, double> allToolCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> allToolTok = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> allModelCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> allModelTok = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ds.AllRecords.Count; i++)
            {
                RecordBucket r = ds.AllRecords[i];
                if (r == null) continue;

                string date = r.Date ?? "";
                string tool = string.IsNullOrEmpty(r.Tool) ? "未知工具" : r.Tool;
                string model = string.IsNullOrEmpty(r.Model) ? "unknown" : r.Model;
                double cost = SafeDouble(r.CostUsd);
                long tok = r.TotalTokens;

                allFi += r.FreshInput;
                allCr += r.CacheRead;
                allCw += r.CacheWrite;
                AccumulateRank(allToolCost, allToolTok, tool, cost, tok);
                AccumulateRank(allModelCost, allModelTok, model, cost, tok);

                if (date.StartsWith(monthPrefix, StringComparison.Ordinal))
                {
                    monthUsd += cost;
                    monthFi += r.FreshInput;
                    monthCr += r.CacheRead;
                    monthCw += r.CacheWrite;
                    AccumulateRank(monthToolCost, monthToolTok, tool, cost, tok);
                    AccumulateRank(monthModelCost, monthModelTok, model, cost, tok);
                }

                if (string.Equals(date, todayStr, StringComparison.Ordinal))
                {
                    todayUsd += cost;
                    todayTokens += tok;
                    todayFi += r.FreshInput;
                    todayCr += r.CacheRead;
                    todayCw += r.CacheWrite;
                    AccumulateRank(todayToolCost, todayToolTok, tool, cost, tok);
                    AccumulateRank(todayModelCost, todayModelTok, model, cost, tok);
                }
            }

            string topToolToday = PickTopRankKey(todayToolCost, todayToolTok);
            string topTool = !string.IsNullOrEmpty(topToolToday)
                ? topToolToday
                : (!string.IsNullOrEmpty(PickTopRankKey(monthToolCost, monthToolTok))
                    ? PickTopRankKey(monthToolCost, monthToolTok)
                    : PickTopRankKey(allToolCost, allToolTok));

            string topModelToday = PickTopRankKey(todayModelCost, todayModelTok);
            string topModel = !string.IsNullOrEmpty(topModelToday)
                ? topModelToday
                : (!string.IsNullOrEmpty(PickTopRankKey(monthModelCost, monthModelTok))
                    ? PickTopRankKey(monthModelCost, monthModelTok)
                    : PickTopRankKey(allModelCost, allModelTok));

            long todayPrompt = todayFi + todayCr + todayCw;
            long monthPrompt = monthFi + monthCr + monthCw;
            long allPrompt = allFi + allCr + allCw;
            double cacheHitPct = 0.0;
            if (todayPrompt > 0L)
                cacheHitPct = todayCr * 100.0 / todayPrompt;
            else if (monthPrompt > 0L)
                cacheHitPct = monthCr * 100.0 / monthPrompt;
            else if (allPrompt > 0L)
                cacheHitPct = allCr * 100.0 / allPrompt;

            string moneyText = FormatStatusMoney(todayUsd, opts.UseCny, rate);
            string tokText = FormatCompactStatusTokens(todayTokens);
            string badgeTool = !string.IsNullOrEmpty(topToolToday) ? topToolToday : topTool;
            string lineText = string.IsNullOrEmpty(badgeTool)
                ? string.Format(CultureInfo.InvariantCulture, "\u26A1 今日 {0} \u00B7 {1} tok", moneyText, tokText)
                : string.Format(CultureInfo.InvariantCulture, "\u26A1 今日 {0} \u00B7 {1} tok ({2})", moneyText, tokText, badgeTool);

            if (opts.OutputJson)
            {
                Dictionary<string, object> obj = new Dictionary<string, object>();
                obj["today_usd"] = Math.Round(SafeDouble(todayUsd), 4);
                obj["today_tokens"] = todayTokens;
                obj["month_usd"] = Math.Round(SafeDouble(monthUsd), 4);
                obj["top_tool"] = topTool ?? "";
                obj["top_model"] = topModel ?? "";
                obj["cache_hit_pct"] = Math.Round(SafeDouble(cacheHitPct), 1);
                obj["today_cny"] = Math.Round(SafeDouble(todayUsd * rate), 2);
                obj["month_cny"] = Math.Round(SafeDouble(monthUsd * rate), 2);
                obj["text"] = lineText;
                JavaScriptSerializer js = NewJson();
                w.WriteLine(js.Serialize(obj));
                return;
            }

            w.WriteLine(lineText);
        }

        private static void AccumulateRank(Dictionary<string, double> costMap, Dictionary<string, long> tokMap, string key, double cost, long tok)
        {
            if (string.IsNullOrEmpty(key)) return;
            double c = 0.0;
            costMap.TryGetValue(key, out c);
            costMap[key] = c + cost;

            long t = 0L;
            tokMap.TryGetValue(key, out t);
            tokMap[key] = t + tok;
        }

        private static string PickTopRankKey(Dictionary<string, double> costMap, Dictionary<string, long> tokMap)
        {
            string bestKey = "";
            double bestCost = -1.0;
            long bestTok = -1L;
            foreach (KeyValuePair<string, double> kv in costMap)
            {
                long tok = 0L;
                tokMap.TryGetValue(kv.Key, out tok);
                if (kv.Value > bestCost + 1e-9 || (Math.Abs(kv.Value - bestCost) <= 1e-9 && tok > bestTok))
                {
                    bestCost = kv.Value;
                    bestTok = tok;
                    bestKey = kv.Key;
                }
            }
            return (bestCost > 0.0 || bestTok > 0L) ? bestKey : "";
        }

        private static string FormatStatusMoney(double usd, bool useCny, double rate)
        {
            double v = SafeDouble(useCny ? (usd * rate) : usd);
            string sym = useCny ? "\u00A5" : "$";
            if (v <= 0.0) return sym + "0.00";
            if (v >= 1000.0) return sym + v.ToString("N2", CultureInfo.InvariantCulture);
            if (v >= 0.01) return sym + v.ToString("F2", CultureInfo.InvariantCulture);
            return sym + v.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string FormatTableMoney(double usd, bool useCny, double rate)
        {
            double cleanUsd = SafeDouble(usd);
            if (cleanUsd <= 0.0)
                return (useCny ? "\u00A5" : "$") + "0.00";
            return FormatterUtil.FormatMoney(cleanUsd, useCny, rate);
        }

        private static string FormatCompactStatusTokens(long n)
        {
            if (n <= 0L) return "0";
            if (n >= 1000000000L) return (n / 1e9).ToString("F1", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000L) return (n / 1e6).ToString("F1", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000L) return (n / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        private static void WriteSummaryTables(TextWriter w, AggregatedDataset ds, CliOptions opts)
        {
            DateTime nowLocal = DateTime.Now;
            string todayStr = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string monthPrefix = nowLocal.ToString("yyyy-MM-", CultureInfo.InvariantCulture);
            string cutoffDate = opts.Days > 0
                ? nowLocal.Date.AddDays(-(opts.Days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "";

            bool useCny = opts.UseCny;
            double rate = (ds.ExchangeRate != null && ds.ExchangeRate.Rate > 0.0) ? ds.ExchangeRate.Rate : ExchangeRateService.DefaultRate;

            int scopeTurns = 0;
            long scopeFi = 0L, scopeCr = 0L, scopeCw = 0L, scopeOut = 0L;
            double scopeCost = 0.0, scopeBase = 0.0;

            double todayCost = 0.0, monthCost = 0.0;
            long todayTok = 0L, monthTok = 0L;

            Dictionary<string, NodeAggRow> nodeAggMap = new Dictionary<string, NodeAggRow>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ds.AllNodes.Count; i++)
            {
                NodeSnapshot ns = ds.AllNodes[i];
                if (ns == null) continue;
                string nKey = string.IsNullOrEmpty(ns.MachineId) ? ("node-" + i.ToString(CultureInfo.InvariantCulture)) : ns.MachineId;
                if (!nodeAggMap.ContainsKey(nKey))
                {
                    NodeAggRow nr = new NodeAggRow();
                    nr.MachineId = nKey;
                    nr.MachineName = ns.MachineName ?? (ns.IsLocal ? "本机" : nKey);
                    nr.MachineType = ns.MachineType ?? (ns.IsLocal ? "windows" : "vps");
                    nr.IsLocal = ns.IsLocal;
                    nr.SyncedAt = ns.SyncedAt ?? "";
                    nodeAggMap[nKey] = nr;
                }
            }

            Dictionary<string, ToolAggRow> toolMap = new Dictionary<string, ToolAggRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ModelAggRow> modelMap = new Dictionary<string, ModelAggRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DayAggRow> allDayMap = new Dictionary<string, DayAggRow>(StringComparer.Ordinal);

            for (int i = 0; i < ds.AllRecords.Count; i++)
            {
                RecordBucket r = ds.AllRecords[i];
                if (r == null) continue;

                string date = string.IsNullOrEmpty(r.Date) ? "未知日期" : r.Date;
                string tool = SanitizeCellText(string.IsNullOrEmpty(r.Tool) ? "未知工具" : r.Tool);
                string model = SanitizeCellText(string.IsNullOrEmpty(r.Model) ? "unknown" : r.Model);
                double cost = SafeDouble(r.CostUsd);
                double baseCost = SafeDouble(r.BaselineUsd);
                long recTok = r.TotalTokens;

                // 全局今日/本月与近 7 日趋势不受 --days 截断，始终准确反映自然日历
                if (string.Equals(date, todayStr, StringComparison.Ordinal))
                {
                    todayCost += cost;
                    todayTok += recTok;
                }
                if (date.StartsWith(monthPrefix, StringComparison.Ordinal))
                {
                    monthCost += cost;
                    monthTok += recTok;
                }

                if (date != "未知日期")
                {
                    DayAggRow dRow;
                    if (!allDayMap.TryGetValue(date, out dRow))
                    {
                        dRow = new DayAggRow();
                        dRow.Date = date;
                        allDayMap[date] = dRow;
                    }
                    dRow.Turns += r.Turns;
                    dRow.FreshInput += r.FreshInput;
                    dRow.CacheRead += r.CacheRead;
                    dRow.CacheWrite += r.CacheWrite;
                    dRow.Output += r.Output;
                    dRow.CostUsd += cost;
                    dRow.BaselineUsd += baseCost;
                }

                // 判断是否落在 --days 统计窗口内
                if (!string.IsNullOrEmpty(cutoffDate))
                {
                    if (date == "未知日期" || string.CompareOrdinal(date, cutoffDate) < 0)
                        continue;
                }

                scopeTurns += r.Turns;
                scopeFi += r.FreshInput;
                scopeCr += r.CacheRead;
                scopeCw += r.CacheWrite;
                scopeOut += r.Output;
                scopeCost += cost;
                scopeBase += baseCost;

                string nKey = string.IsNullOrEmpty(r.MachineId) ? "local" : r.MachineId;
                NodeAggRow na;
                if (!nodeAggMap.TryGetValue(nKey, out na))
                {
                    na = new NodeAggRow();
                    na.MachineId = nKey;
                    na.MachineName = string.IsNullOrEmpty(r.MachineName) ? "本机" : r.MachineName;
                    na.MachineType = string.IsNullOrEmpty(r.MachineType) ? "windows" : r.MachineType;
                    nodeAggMap[nKey] = na;
                }
                na.Tools.Add(tool);
                na.Turns += r.Turns;
                na.FreshInput += r.FreshInput;
                na.CacheRead += r.CacheRead;
                na.CacheWrite += r.CacheWrite;
                na.Output += r.Output;
                na.CostUsd += cost;

                ToolAggRow ta;
                if (!toolMap.TryGetValue(tool, out ta))
                {
                    ta = new ToolAggRow();
                    ta.Tool = tool;
                    toolMap[tool] = ta;
                }
                ta.Models.Add(model);
                ta.Turns += r.Turns;
                ta.FreshInput += r.FreshInput;
                ta.CacheRead += r.CacheRead;
                ta.CacheWrite += r.CacheWrite;
                ta.Output += r.Output;
                ta.CostUsd += cost;

                ModelAggRow ma;
                if (!modelMap.TryGetValue(model, out ma))
                {
                    ma = new ModelAggRow();
                    ma.Model = model;
                    modelMap[model] = ma;
                }
                ma.Turns += r.Turns;
                ma.FreshInput += r.FreshInput;
                ma.CacheRead += r.CacheRead;
                ma.CacheWrite += r.CacheWrite;
                ma.Output += r.Output;
                ma.CostUsd += cost;
                double prevTc = 0.0;
                ma.ToolCostMap.TryGetValue(tool, out prevTc);
                ma.ToolCostMap[tool] = prevTc + cost;
                long prevTt = 0L;
                ma.ToolTokMap.TryGetValue(tool, out prevTt);
                ma.ToolTokMap[tool] = prevTt + recTok;
            }

            // 准备近 7 天连续数组（T-6 .. 今日）
            DayAggRow[] sevenDays = new DayAggRow[7];
            double[] sevenValues = new double[7];
            double maxDayMetric = 0.0;
            bool allDaysZeroCost = true;
            for (int d = 0; d < 7; d++)
            {
                DateTime dt = nowLocal.Date.AddDays(-(6 - d));
                string dStr = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DayAggRow found;
                if (!allDayMap.TryGetValue(dStr, out found))
                {
                    found = new DayAggRow();
                    found.Date = dStr;
                }
                sevenDays[d] = found;
                if (found.CostUsd > 0.0)
                    allDaysZeroCost = false;
            }
            for (int d = 0; d < 7; d++)
            {
                double metric = allDaysZeroCost ? (double)sevenDays[d].TotalTokens : sevenDays[d].CostUsd;
                sevenValues[d] = metric;
                if (metric > maxDayMetric)
                    maxDayMetric = metric;
            }
            string spark7 = BuildSparkline(sevenValues);

            // 标题与扫描信息栏
            string rangeTitle = opts.Days > 0
                ? string.Format(CultureInfo.InvariantCulture, "近 {0} 天 ({1} ~ {2})", opts.Days, cutoffDate, todayStr)
                : string.Format(CultureInfo.InvariantCulture, "全部历史 (截至 {0})", todayStr);
            string curLabel = useCny
                ? string.Format(CultureInfo.InvariantCulture, "CNY (\u00A5, 汇率 {0:F4})", rate)
                : string.Format(CultureInfo.InvariantCulture, "USD ($, 参考汇率 {0:F4})", rate);
            double elapsedSec = ds.LocalScan != null ? ds.LocalScan.ElapsedSeconds : 0.0;
            int trackedFiles = ds.LocalScan != null ? ds.LocalScan.TotalFilesTracked : 0;

            w.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0}  |  统计范围: {1}  |  币种: {2}",
                AppConstants.ProductTitle,
                rangeTitle,
                curLabel));
            w.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "聚合节点: {0} 台设备 (本机 + {1} 台远端)  |  扫描耗时: {2:F2}s (追踪 {3} 个日志文件)",
                ds.AllNodes.Count,
                ds.RemoteNodes.Count,
                elapsedSec,
                trackedFiles));
            w.WriteLine();

            // 1. 汇总概览表
            long scopeTotTok = scopeFi + scopeCr + scopeCw + scopeOut;
            long scopePrompt = scopeFi + scopeCr + scopeCw;
            double scopeHitPct = scopePrompt > 0L ? (scopeCr * 100.0 / scopePrompt) : 0.0;
            double scopeSavedUsd = Math.Max(0.0, scopeBase - scopeCost);
            double scopeSavePct = scopeBase > 0.0 ? (scopeSavedUsd * 100.0 / scopeBase) : 0.0;

            w.WriteLine("[1] 汇总概览 (" + (opts.Days > 0 ? ("近 " + opts.Days.ToString(CultureInfo.InvariantCulture) + " 天") : "全部历史") + ")");
            string[] sumHeaders = new string[] { "统计指标", "数值明细", "对比指标", "数值明细" };
            bool[] sumRight = new bool[] { false, true, false, true };
            List<string[]> sumRows = new List<string[]>();
            sumRows.Add(new string[]
            {
                "区间等效总花费",
                FormatTableMoney(scopeCost, useCny, rate) + " (" + FormatTableMoney(scopeCost, !useCny, rate) + ")",
                "今日实时消耗",
                FormatTableMoney(todayCost, useCny, rate) + " / " + FormatterUtil.FormatTokens(todayTok)
            });
            sumRows.Add(new string[]
            {
                "区间累计 Token",
                FormatterUtil.FormatTokens(scopeTotTok) + " (" + scopeTurns.ToString("N0", CultureInfo.InvariantCulture) + " 次调用)",
                "本月累计消耗",
                FormatTableMoney(monthCost, useCny, rate) + " / " + FormatterUtil.FormatTokens(monthTok)
            });
            sumRows.Add(new string[]
            {
                "Token 构成明细",
                "入 " + FormatterUtil.FormatTokens(scopeFi) + " / 缓 " + FormatterUtil.FormatTokens(scopeCr) + " / 出 " + FormatterUtil.FormatTokens(scopeOut),
                "Prompt 缓存命中率",
                scopeHitPct.ToString("F1", CultureInfo.InvariantCulture) + "% (写 " + FormatterUtil.FormatTokens(scopeCw) + ")"
            });
            sumRows.Add(new string[]
            {
                "缓存累计省钱",
                FormatTableMoney(scopeSavedUsd, useCny, rate) + " (省 " + scopeSavePct.ToString("F1", CultureInfo.InvariantCulture) + "%)",
                "近 7 天迷你趋势",
                spark7 + " (7日)"
            });
            RenderBoxTable(w, sumHeaders, sumRight, sumRows, null);
            w.WriteLine();

            // 2. 设备消耗排行表
            List<NodeAggRow> sortedNodes = new List<NodeAggRow>(nodeAggMap.Values);
            sortedNodes.Sort(delegate(NodeAggRow a, NodeAggRow b)
            {
                int c = b.CostUsd.CompareTo(a.CostUsd);
                if (c != 0) return c;
                c = b.TotalTokens.CompareTo(a.TotalTokens);
                if (c != 0) return c;
                if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
                return string.Compare(a.MachineName, b.MachineName, StringComparison.OrdinalIgnoreCase);
            });

            w.WriteLine("[2] 设备消耗排行 (" + sortedNodes.Count.ToString(CultureInfo.InvariantCulture) + " 台设备)");
            string[] nodeHeaders = new string[] { "#", "设备名称", "平台类型", "工具数", "调用次数", "总 Token", "缓存命中", "等效花费", "占比" };
            bool[] nodeRight = new bool[] { true, false, false, true, true, true, true, true, true };
            List<string[]> nodeRows = new List<string[]>(sortedNodes.Count);
            for (int i = 0; i < sortedNodes.Count; i++)
            {
                NodeAggRow n = sortedNodes[i];
                string badgeName = SanitizeCellText(n.MachineName) + (n.IsLocal ? " [本机]" : "");
                string typeText = IsVpsType(n.MachineType) ? "Linux VPS" : "Windows";
                long nPrompt = n.FreshInput + n.CacheRead + n.CacheWrite;
                double nHit = nPrompt > 0L ? (n.CacheRead * 100.0 / nPrompt) : 0.0;
                double nShare = scopeCost > 0.0
                    ? (n.CostUsd * 100.0 / scopeCost)
                    : (scopeTotTok > 0L ? (n.TotalTokens * 100.0 / scopeTotTok) : 0.0);

                nodeRows.Add(new string[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    TruncateDisplayWidth(badgeName, 26),
                    typeText,
                    n.Tools.Count.ToString(CultureInfo.InvariantCulture),
                    n.Turns.ToString("N0", CultureInfo.InvariantCulture),
                    FormatterUtil.FormatTokens(n.TotalTokens),
                    nPrompt > 0L ? (nHit.ToString("F1", CultureInfo.InvariantCulture) + "%") : "--",
                    FormatTableMoney(n.CostUsd, useCny, rate),
                    nShare.ToString("F1", CultureInfo.InvariantCulture) + "%"
                });
            }
            string[] nodeFooter = sortedNodes.Count > 1
                ? new string[]
                {
                    "*",
                    "全部设备合计",
                    sortedNodes.Count.ToString(CultureInfo.InvariantCulture) + " 台",
                    toolMap.Count.ToString(CultureInfo.InvariantCulture),
                    scopeTurns.ToString("N0", CultureInfo.InvariantCulture),
                    FormatterUtil.FormatTokens(scopeTotTok),
                    scopePrompt > 0L ? (scopeHitPct.ToString("F1", CultureInfo.InvariantCulture) + "%") : "--",
                    FormatTableMoney(scopeCost, useCny, rate),
                    "100.0%"
                }
                : null;
            RenderBoxTable(w, nodeHeaders, nodeRight, nodeRows, nodeFooter);
            w.WriteLine();

            // 3. Top 8 AI 工具排行表
            List<ToolAggRow> sortedTools = new List<ToolAggRow>(toolMap.Values);
            sortedTools.Sort(CompareToolAggDesc);
            int topToolLimit = Math.Min(sortedTools.Count, 8);

            w.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[3] Top {0} AI 工具消耗排行 (共检测到 {1} 款活跃工具)",
                8,
                sortedTools.Count));
            string[] toolHeaders = new string[] { "#", "AI 工具", "模型数", "调用次数", "新增输入", "缓存读取", "输出 Token", "总 Token", "等效花费", "占比" };
            bool[] toolRight = new bool[] { true, false, true, true, true, true, true, true, true, true };
            List<string[]> toolRows = new List<string[]>();
            if (topToolLimit == 0)
            {
                toolRows.Add(new string[] { "-", "当前统计范围内暂无记录", "0", "0", "0", "0", "0", "0", FormatTableMoney(0.0, useCny, rate), "0.0%" });
            }
            else
            {
                for (int i = 0; i < topToolLimit; i++)
                {
                    ToolAggRow t = sortedTools[i];
                    double share = scopeCost > 0.0
                        ? (t.CostUsd * 100.0 / scopeCost)
                        : (scopeTotTok > 0L ? (t.TotalTokens * 100.0 / scopeTotTok) : 0.0);

                    toolRows.Add(new string[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        TruncateDisplayWidth(t.Tool, 22),
                        t.Models.Count.ToString(CultureInfo.InvariantCulture),
                        t.Turns.ToString("N0", CultureInfo.InvariantCulture),
                        FormatterUtil.FormatTokens(t.FreshInput),
                        FormatterUtil.FormatTokens(t.CacheRead),
                        FormatterUtil.FormatTokens(t.Output),
                        FormatterUtil.FormatTokens(t.TotalTokens),
                        FormatTableMoney(t.CostUsd, useCny, rate),
                        share.ToString("F1", CultureInfo.InvariantCulture) + "%"
                    });
                }
            }
            RenderBoxTable(w, toolHeaders, toolRight, toolRows, null);
            w.WriteLine();

            // 4. Top 8 模型排行表
            List<ModelAggRow> sortedModels = new List<ModelAggRow>(modelMap.Values);
            sortedModels.Sort(CompareModelAggDesc);
            int topModelLimit = Math.Min(sortedModels.Count, 8);

            w.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[4] Top {0} 模型账单排行 (共调用 {1} 种模型)",
                8,
                sortedModels.Count));
            string[] modelHeaders = new string[] { "#", "模型名称", "主力工具", "调用次数", "总 Token", "缓存命中", "等效花费", "占比" };
            bool[] modelRight = new bool[] { true, false, false, true, true, true, true, true };
            List<string[]> modelRows = new List<string[]>();
            if (topModelLimit == 0)
            {
                modelRows.Add(new string[] { "-", "当前统计范围内暂无记录", "--", "0", "0", "--", FormatTableMoney(0.0, useCny, rate), "0.0%" });
            }
            else
            {
                for (int i = 0; i < topModelLimit; i++)
                {
                    ModelAggRow m = sortedModels[i];
                    long mPrompt = m.FreshInput + m.CacheRead + m.CacheWrite;
                    double mHit = mPrompt > 0L ? (m.CacheRead * 100.0 / mPrompt) : 0.0;
                    double share = scopeCost > 0.0
                        ? (m.CostUsd * 100.0 / scopeCost)
                        : (scopeTotTok > 0L ? (m.TotalTokens * 100.0 / scopeTotTok) : 0.0);

                    modelRows.Add(new string[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        TruncateDisplayWidth(m.Model, 30),
                        TruncateDisplayWidth(m.PrimaryTool, 18),
                        m.Turns.ToString("N0", CultureInfo.InvariantCulture),
                        FormatterUtil.FormatTokens(m.TotalTokens),
                        mPrompt > 0L ? (mHit.ToString("F1", CultureInfo.InvariantCulture) + "%") : "--",
                        FormatTableMoney(m.CostUsd, useCny, rate),
                        share.ToString("F1", CultureInfo.InvariantCulture) + "%"
                    });
                }
            }
            RenderBoxTable(w, modelHeaders, modelRight, modelRows, null);
            w.WriteLine();

            // 5. 近 7 天每日趋势表（含垂直脉冲字符条 ▇▆▅▃ 与水平分布条）
            w.WriteLine("[5] 近 7 天消耗趋势  " + spark7);
            string[] dayHeaders = new string[] { "日期", "星期", "调用次数", "总 Token", "当日花费", "7日峰值比例趋势" };
            bool[] dayRight = new bool[] { false, false, true, true, true, false };
            List<string[]> dayRows = new List<string[]>(7);
            int sum7Turns = 0;
            long sum7Tok = 0L;
            double sum7Cost = 0.0;

            for (int d = 0; d < 7; d++)
            {
                DayAggRow dr = sevenDays[d];
                DateTime dt = nowLocal.Date.AddDays(-(6 - d));
                string wk = WeekdayLabels[(int)dt.DayOfWeek];
                if (d == 6)
                    wk = wk + " (今)";

                sum7Turns += dr.Turns;
                sum7Tok += dr.TotalTokens;
                sum7Cost += dr.CostUsd;

                char levelChar = PickSingleSparkBlock(sevenValues[d], maxDayMetric);
                string hBar = BuildHorizontalTrendBar(sevenValues[d], maxDayMetric, 16);
                double pctOfPeak = maxDayMetric > 0.0 ? (sevenValues[d] * 100.0 / maxDayMetric) : 0.0;
                string trendCell = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} {2,5:F1}%",
                    levelChar,
                    hBar,
                    pctOfPeak);

                dayRows.Add(new string[]
                {
                    dr.Date,
                    wk,
                    dr.Turns.ToString("N0", CultureInfo.InvariantCulture),
                    FormatterUtil.FormatTokens(dr.TotalTokens),
                    FormatTableMoney(dr.CostUsd, useCny, rate),
                    trendCell
                });
            }
            string[] dayFooter = new string[]
            {
                "近 7 天合计",
                "7 天",
                sum7Turns.ToString("N0", CultureInfo.InvariantCulture),
                FormatterUtil.FormatTokens(sum7Tok),
                FormatTableMoney(sum7Cost, useCny, rate),
                "波峰走势: " + spark7
            };
            RenderBoxTable(w, dayHeaders, dayRight, dayRows, dayFooter);
        }

        private static int CompareToolAggDesc(ToolAggRow a, ToolAggRow b)
        {
            int c = b.CostUsd.CompareTo(a.CostUsd);
            if (c != 0) return c;
            c = b.TotalTokens.CompareTo(a.TotalTokens);
            if (c != 0) return c;
            return string.Compare(a.Tool, b.Tool, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareModelAggDesc(ModelAggRow a, ModelAggRow b)
        {
            int c = b.CostUsd.CompareTo(a.CostUsd);
            if (c != 0) return c;
            c = b.TotalTokens.CompareTo(a.TotalTokens);
            if (c != 0) return c;
            return string.Compare(a.Model, b.Model, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVpsType(string machineType)
        {
            if (string.IsNullOrEmpty(machineType)) return false;
            return EqualsIgnoreCase(machineType, "vps") || EqualsIgnoreCase(machineType, "linux");
        }

        private static string BuildSparkline(double[] values)
        {
            if (values == null || values.Length == 0)
                return "";

            double maxVal = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > maxVal)
                    maxVal = values[i];
            }

            StringBuilder sb = new StringBuilder(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                sb.Append(PickSingleSparkBlock(values[i], maxVal));
            }
            return sb.ToString();
        }

        private static char PickSingleSparkBlock(double val, double maxVal)
        {
            if (maxVal <= 0.0 || val <= 0.0)
                return SparkBlocks[0];

            double ratio = val / maxVal;
            if (ratio < 0.0) ratio = 0.0;
            if (ratio > 1.0) ratio = 1.0;

            // 有消耗时映射到 ▂..▇ (索引 1..6)，与 0 值 ▁ 形成清晰呼吸感与视觉区分
            int span = SparkBlocks.Length - 2;
            int idx = 1 + (int)Math.Round(ratio * span);
            if (idx < 1) idx = 1;
            if (idx >= SparkBlocks.Length) idx = SparkBlocks.Length - 1;
            return SparkBlocks[idx];
        }

        private static string BuildHorizontalTrendBar(double val, double maxVal, int width)
        {
            if (width <= 0) return "";
            if (maxVal <= 0.0 || val <= 0.0)
                return new string('-', width);

            double ratio = val / maxVal;
            if (ratio < 0.0) ratio = 0.0;
            if (ratio > 1.0) ratio = 1.0;

            double scaled = ratio * width;
            int fullCells = (int)Math.Floor(scaled);
            double frac = scaled - fullCells;

            StringBuilder sb = new StringBuilder(width);
            for (int i = 0; i < fullCells && sb.Length < width; i++)
            {
                sb.Append('\u2587'); // ▇
            }
            if (sb.Length < width && (frac >= 0.25 || fullCells == 0))
            {
                if (frac >= 0.70)
                    sb.Append('\u2586'); // ▆
                else if (frac >= 0.45)
                    sb.Append('\u2585'); // ▅
                else
                    sb.Append('\u2583'); // ▃
            }
            while (sb.Length < width)
            {
                sb.Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 渲染具备东亚宽字符（CJK）自适应列宽对齐的圆角 Unicode 边框表格。
        /// </summary>
        private static void RenderBoxTable(TextWriter w, string[] headers, bool[] rightAlign, List<string[]> rows, string[] footerRow)
        {
            if (headers == null || headers.Length == 0)
                return;

            int colCount = headers.Length;
            int[] widths = new int[colCount];

            for (int c = 0; c < colCount; c++)
            {
                headers[c] = SanitizeCellText(headers[c]);
                int hw = GetDisplayWidth(headers[c]);
                if (hw > widths[c]) widths[c] = hw;
            }

            if (rows != null)
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    string[] row = rows[r];
                    if (row == null) continue;
                    for (int c = 0; c < colCount && c < row.Length; c++)
                    {
                        row[c] = SanitizeCellText(row[c]);
                        int cw = GetDisplayWidth(row[c]);
                        if (cw > widths[c]) widths[c] = cw;
                    }
                }
            }

            if (footerRow != null)
            {
                for (int c = 0; c < colCount && c < footerRow.Length; c++)
                {
                    footerRow[c] = SanitizeCellText(footerRow[c]);
                    int fw = GetDisplayWidth(footerRow[c]);
                    if (fw > widths[c]) widths[c] = fw;
                }
            }

            w.WriteLine(BuildHorizontalBorder(widths, '\u256D', '\u252C', '\u256E')); // ╭─┬─╮
            w.WriteLine(BuildTableDataRow(headers, widths, rightAlign));
            w.WriteLine(BuildHorizontalBorder(widths, '\u251C', '\u253C', '\u2524')); // ├─┼─┤

            if (rows != null)
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    if (rows[r] == null) continue;
                    w.WriteLine(BuildTableDataRow(rows[r], widths, rightAlign));
                }
            }

            if (footerRow != null)
            {
                w.WriteLine(BuildHorizontalBorder(widths, '\u251C', '\u253C', '\u2524')); // ├─┼─┤
                w.WriteLine(BuildTableDataRow(footerRow, widths, rightAlign));
            }

            w.WriteLine(BuildHorizontalBorder(widths, '\u2570', '\u2534', '\u256F')); // ╰─┴─╯
        }

        private static string BuildHorizontalBorder(int[] widths, char left, char mid, char right)
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append(left);
            for (int c = 0; c < widths.Length; c++)
            {
                if (c > 0) sb.Append(mid);
                sb.Append('\u2500', widths[c] + 2); // ─
            }
            sb.Append(right);
            return sb.ToString();
        }

        private static string BuildTableDataRow(string[] cells, int[] widths, bool[] rightAlign)
        {
            StringBuilder sb = new StringBuilder(112);
            sb.Append('\u2502'); // │
            for (int c = 0; c < widths.Length; c++)
            {
                string val = (cells != null && c < cells.Length && cells[c] != null) ? cells[c] : "";
                bool alignRight = rightAlign != null && c < rightAlign.Length && rightAlign[c];
                sb.Append(' ');
                sb.Append(PadDisplayWidth(val, widths[c], alignRight));
                sb.Append(' ');
                sb.Append('\u2502'); // │
            }
            return sb.ToString();
        }

        private static string SanitizeCellText(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            if (s.IndexOf('\r') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\t') < 0)
                return s;
            return s.Replace("\r", "").Replace("\n", " ").Replace("\t", " ");
        }

        private static int GetDisplayWidth(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;
            int width = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    width += 2;
                    i++;
                    continue;
                }
                width += GetCharDisplayWidth(ch);
            }
            return width;
        }

        private static int GetCharDisplayWidth(char ch)
        {
            if (ch < 0x0020 || ch == 0x007F)
                return 0;
            if (ch <= 0x007E)
                return 1;

            // 组合音标、零宽字符与变体选择符
            if ((ch >= 0x0300 && ch <= 0x036F) ||
                (ch >= 0x200B && ch <= 0x200F) ||
                (ch >= 0xFE00 && ch <= 0xFE0F) ||
                ch == 0xFEFF)
            {
                return 0;
            }

            // Unicode 制表框线与方块元素 (U+2500..U+259F) 固定占 1 列
            if (ch >= 0x2500 && ch <= 0x259F)
                return 1;

            // 东亚宽字符 (CJK 表意文字、标点、假名、谚文与全角符号) 固定占 2 列
            if ((ch >= 0x1100 && ch <= 0x115F) ||
                (ch >= 0x2E80 && ch <= 0x303E) ||
                (ch >= 0x3040 && ch <= 0xA4CF) ||
                (ch >= 0xAC00 && ch <= 0xD7A3) ||
                (ch >= 0xF900 && ch <= 0xFAFF) ||
                (ch >= 0xFE10 && ch <= 0xFE19) ||
                (ch >= 0xFE30 && ch <= 0xFE6F) ||
                (ch >= 0xFF01 && ch <= 0xFF60) ||
                (ch >= 0xFFE0 && ch <= 0xFFE6))
            {
                return 2;
            }

            return 1;
        }

        private static string TruncateDisplayWidth(string text, int maxCols)
        {
            if (string.IsNullOrEmpty(text) || maxCols <= 0)
                return "";
            if (GetDisplayWidth(text) <= maxCols)
                return text;
            if (maxCols <= 2)
                return new string('.', maxCols);

            int limit = maxCols - 2;
            StringBuilder sb = new StringBuilder(text.Length);
            int cur = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                int cw;
                bool isPair = char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                if (isPair)
                    cw = 2;
                else
                    cw = GetCharDisplayWidth(ch);

                if (cur + cw > limit)
                    break;

                sb.Append(ch);
                if (isPair)
                {
                    sb.Append(text[i + 1]);
                    i++;
                }
                cur += cw;
            }
            sb.Append("..");
            return sb.ToString();
        }

        private static string PadDisplayWidth(string text, int targetCols, bool alignRight)
        {
            string safe = text ?? "";
            int w = GetDisplayWidth(safe);
            int pad = targetCols - w;
            if (pad <= 0)
                return safe;
            string spaces = new string(' ', pad);
            return alignRight ? (spaces + safe) : (safe + spaces);
        }

        private static double SafeDouble(double val)
        {
            if (double.IsNaN(val) || double.IsInfinity(val))
                return 0.0;
            return val;
        }

        private static JavaScriptSerializer NewJson()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;
            return js;
        }
    }
}
