using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

// 程序集版本与元数据（知微 · TokLens v2.2 / 2.2.0.0）已集中声明于 src\AssemblyInfo.cs，避免全目录 *.cs 编译产生 CS0579 重复特性。

namespace TokLens
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\TokLens_GUI_SingleInstance";
        private const string SingleInstanceActivateEventName = @"Local\TokLens_GUI_ActivateEvent";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static void TryEnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    return;
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
                AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            }
            catch { }

            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string exportJson = null;
            string exportCsv = null;
            string exportHtml = null;
            bool requireFullAccess = false;
            bool selfTestUi = false;
            bool startMinimized = false;
            bool customCliRate = false;
            double cliRate = ExchangeRateService.DefaultRate;

            if (args != null && args.Length > 0)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.Equals(a, "--home", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        homeDir = args[++i];
                    }
                    else if (string.Equals(a, "--export-json", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        exportJson = args[++i];
                    }
                    else if (string.Equals(a, "--export-csv", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        exportCsv = args[++i];
                    }
                    else if (string.Equals(a, "--export-html", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        exportHtml = args[++i];
                    }
                    else if (string.Equals(a, "--rate", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        double r;
                        if (double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out r) && r > 0.0)
                        {
                            cliRate = r;
                            customCliRate = true;
                        }
                    }
                    else if (string.Equals(a, "--require-full-access", StringComparison.OrdinalIgnoreCase))
                    {
                        requireFullAccess = true;
                    }
                    else if (string.Equals(a, "--self-test-ui", StringComparison.OrdinalIgnoreCase))
                    {
                        selfTestUi = true;
                    }
                    else if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimized = true;
                    }
                }

                if (selfTestUi)
                {
                    try
                    {
                        Application testApp = Application.Current ?? new Application();
                        MainWindow testWin = new MainWindow(homeDir, true);
                        testWin.RunHeadlessUiSelfTest();
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        if (!string.IsNullOrEmpty(exportJson))
                        {
                            try { File.WriteAllText(exportJson, ex.ToString(), new UTF8Encoding(false)); } catch { }
                        }
                        try { Console.Error.WriteLine(ex.ToString()); } catch { }
                        return 3;
                    }
                }

                if (!string.IsNullOrEmpty(exportJson) || !string.IsNullOrEmpty(exportCsv) || !string.IsNullOrEmpty(exportHtml))
                {
                    try
                    {
                        try { PricingEngine.SyncCloudPricing(false); } catch { }

                        string rateSrc = "自定义汇率";
                        if (!customCliRate)
                        {
                            ExchangeRateInfo fx = ExchangeRateService.FetchLatestUsdToCny(false);
                            cliRate = fx.Rate;
                            rateSrc = fx.Source;
                        }

                        SyncConfig cfg = SyncService.LoadConfig();
                        TokenScanner scanner = new TokenScanner(homeDir, cfg.ScanLocalWsl);
                        ScanResult res = scanner.Run();
                        PricingEngine pricing = new PricingEngine(homeDir);

                        NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(res, cfg);
                        ApplyNodeAliasToSnapshot(localSnap, cfg);

                        List<NodeSnapshot> remoteSnaps = SyncService.LoadCachedRemoteNodes(cfg.MachineId);
                        List<RecordBucket> mergedRows = new List<RecordBucket>(localSnap.Records.Count + 64);
                        mergedRows.AddRange(localSnap.Records);

                        for (int r = 0; r < remoteSnaps.Count; r++)
                        {
                            NodeSnapshot rs = remoteSnaps[r];
                            if (rs == null) continue;
                            pricing.RecalculateRemoteSnapshot(rs);
                            ApplyNodeAliasToSnapshot(rs, cfg);
                            if (rs.Records != null)
                                mergedRows.AddRange(rs.Records);
                        }

                        if (!string.IsNullOrEmpty(exportJson))
                            MainWindow.WriteMergedSummaryJson(res, mergedRows, 1 + remoteSnaps.Count, cliRate, rateSrc, exportJson);
                        if (!string.IsNullOrEmpty(exportCsv))
                            FormatterUtil.ExportCsv(mergedRows, cliRate, exportCsv);
                        if (!string.IsNullOrEmpty(exportHtml))
                            HtmlExporter.ExportHtmlReport(mergedRows, res, cliRate, exportHtml);

                        if (requireFullAccess && res.Warnings != null && res.Warnings.Count > 0)
                            return 2;
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        try { Console.Error.WriteLine(ex.ToString()); } catch { }
                        if (!string.IsNullOrEmpty(exportJson))
                        {
                            try { File.WriteAllText(exportJson + ".err.txt", ex.ToString(), new UTF8Encoding(false)); } catch { }
                        }
                        return 1;
                    }
                }

                int cliExitCode;
                if (CliRunner.TryHandleCommandLine(args, out cliExitCode))
                {
                    return cliExitCode;
                }
            }

            // GUI 模式：单实例互斥锁 + 跨进程唤醒（允许从最小化或系统托盘瞬间恢复置顶）
            bool createdNew = true;
            Mutex instanceMutex = null;
            try
            {
                instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            }
            catch
            {
                createdNew = true;
            }

            if (!createdNew)
            {
                try
                {
                    using (EventWaitHandle wakeEvt = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceActivateEventName))
                    {
                        wakeEvt.Set();
                    }
                }
                catch { }
                if (instanceMutex != null)
                {
                    try { instanceMutex.Dispose(); } catch { }
                }
                return 0;
            }

            WindowsShellIntegration.TryEnablePerMonitorDpiAwareness();
            TryEnableDpiAwareness();
            try { WinForms.Application.EnableVisualStyles(); } catch { }

            EventWaitHandle listenEvt = null;
            ManualResetEvent stopWakeThreadEvt = new ManualResetEvent(false);

            try
            {
                Application app = new Application();
                MainWindow win = new MainWindow(homeDir, false);
                if (startMinimized)
                {
                    win.WindowState = WindowState.Minimized;
                    win.ShowInTaskbar = false;
                }

                try
                {
                    listenEvt = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceActivateEventName);
                    Thread wakeThread = new Thread(delegate()
                    {
                        WaitHandle[] handles = new WaitHandle[] { listenEvt, stopWakeThreadEvt };
                        while (true)
                        {
                            int signaled;
                            try
                            {
                                signaled = WaitHandle.WaitAny(handles);
                            }
                            catch
                            {
                                break;
                            }
                            if (signaled != 0)
                                break;
                            try
                            {
                                win.Dispatcher.BeginInvoke(new Action(delegate()
                                {
                                    win.RestoreAndActivateFromTray();
                                }));
                            }
                            catch { }
                        }
                    });
                    wakeThread.IsBackground = true;
                    wakeThread.Name = "TokLens-SingleInstanceWake";
                    wakeThread.Start();
                }
                catch { }

                app.Run(win);
                return 0;
            }
            finally
            {
                try { stopWakeThreadEvt.Set(); } catch { }
                if (listenEvt != null)
                {
                    try { listenEvt.Dispose(); } catch { }
                }
                try { stopWakeThreadEvt.Dispose(); } catch { }
                if (instanceMutex != null)
                {
                    try { instanceMutex.ReleaseMutex(); } catch { }
                    try { instanceMutex.Dispose(); } catch { }
                }
            }
        }

        internal static void ApplyNodeAliasToSnapshot(NodeSnapshot snap, SyncConfig cfg)
        {
            if (snap == null || cfg == null || cfg.NodeAliases == null || string.IsNullOrEmpty(snap.MachineId))
                return;
            string alias;
            if (cfg.NodeAliases.TryGetValue(snap.MachineId, out alias) && !string.IsNullOrWhiteSpace(alias))
            {
                string trimmed = alias.Trim();
                snap.MachineName = trimmed;
                if (snap.Records != null)
                {
                    for (int i = 0; i < snap.Records.Count; i++)
                    {
                        if (snap.Records[i] != null)
                            snap.Records[i].MachineName = trimmed;
                    }
                }
            }
        }
    }

    public sealed class MainWindow : Window
    {
        // 十一大独立全高旗舰视图索引
        private const int ViewOverview = 0;
        private const int ViewLiveStream = 1;
        private const int ViewModels = 2;
        private const int ViewProjects = 3;
        private const int ViewInsights = 4;
        private const int ViewBloat = 5;
        private const int ViewDevices = 6;
        private const int ViewDailyExport = 7;
        private const int ViewDoctor = 8;
        private const int ViewSettings = 9;
        private const int TotalViewCount = 10;

        private const double SidebarExpandedWidth = 200.0;
        private const double SidebarCollapsedWidth = 58.0;
        private const double TopBarLockedHeight = 48.0;

        // 其他天数走「自定义…」，避免预设和自由输入框并排占用顶栏空间。
        private static readonly int[] PresetRangeDays = new int[] { 1, 7, 30, 0 };

        // 每个页面只有一个名字：侧栏、页面标题、命令面板、快捷键提示都用这里的叫法。
        private static readonly string[] ViewTitles = new string[]
        {
            "概览",
            "请求明细",
            "模型与工具",
            "项目",
            "订阅与费用",
            "会话分析",
            "设备",
            "每日汇总",
            "环境检查",
            "同步与偏好"
        };

        // 核心运行状态与配置
        private string _homeDir;
        private readonly bool _skipAutoScan;
        private SyncConfig _syncCfg;
        private ScanResult _scanResult;
        private NodeSnapshot _localNode;
        private List<NodeSnapshot> _remoteNodes = new List<NodeSnapshot>();

        private bool _isDark = true;
        private bool _isCny = true;
        private double _usdToCny = ExchangeRateService.DefaultRate;
        private string _fxSource = "内置默认";
        private string _fxUpdatedAt = "";
        private bool _fxIsLive = false;
        private bool _fxIsSyncing = false;

        private bool _priceIsSyncing = false;
        private string _priceSyncSource = "内置+云端缓存";
        private string _priceSyncTime = "";

        private bool _cloudSyncRunning = false;
        private bool _cloudHasAttempted = false;
        private bool _cloudLastOk = true;
        private string _lastSyncStatus = "";

        private bool _suppressRateEvent = false;
        private DateTime? _lastCloudSyncAt = null;

        // 筛选与视图状态
        private int _activeView = ViewOverview;
        private bool _sidebarCollapsed = false;
        private int _rangeDays = 1;
        private string _selectedDate = null;
        private string _selectedTool = null;
        private string _selectedMachineId = null;

        private bool _rankByCost = true;
        private bool _leftRankByMachine = false;
        private bool _chartLineMode = true;

        private bool _livePaused = false;
        private int _pausedBufferedCount = 0;
        private bool _hasUnreadLivePulse = false;
        private long _lastTopRequestTs = 0L;

        private string _liveSearchQuery = "";
        private string _modelSearchQuery = "";
        private string _dailySearchQuery = "";

        // 并发安全扫描合并标志
        private bool _isScanning = false;
        private bool _pendingRescan = false;
        private bool _pendingForceUi = false;
        private bool _pendingTriggerCloud = false;

        // 实时监听与后台定时器
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> _watchedDirPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _watcherTickCounter = 0;
        private DispatcherTimer _fsDebounceTimer;
        private DispatcherTimer _livePulseTimer;
        private DispatcherTimer _cloudAutoTimer;
        private DispatcherTimer _cloudDebounceTimer;

        // 系统托盘与桌面集成（悬浮球、全局热键、异常燃烧提醒）
        private Drawing.Icon _nativeExeIcon;
        private WinForms.NotifyIcon _trayIcon;
        private WinForms.ContextMenuStrip _trayMenu;
        private WinForms.ToolStripMenuItem _trayTodaySummaryItem;
        private WinForms.ToolStripMenuItem _trayHudToggleItem;
        private WinForms.ToolStripMenuItem _trayMinimizeToggleItem;
        private bool _explicitExitRequested = false;
        private bool _trayHintShownOnce = false;
        private MiniHudWindow _miniHud;
        private GlobalHotkeyHost _globalHotkey;
        private readonly BurnRateGuard _burnGuard = new BurnRateGuard();
        // 失控提醒在悬浮球上保持 10 分钟（变红并显示「!」）
        private DateTime _hudAlertUntilUtc = DateTime.MinValue;
        private string _hudAlertText = "";

        // 布局核心骨架（供 0.0px 零跳动无头自测断言）与全局命令面板覆盖层
        private Border _rootShellBorder;
        private ToastManager _toastManager;
        private CommandPaletteOverlay _commandPalette;
        private ColumnDefinition _sidebarColDef;
        private Border _sidebarBorder;
        private Border _topContextBarBorder;
        private Button _maxCaptionBtn;
        private Shapes.Path _maxCaptionPath;

        // 左侧导航栏控件
        private StackPanel _brandTextPanel;
        private Shapes.Ellipse _sidebarPulseDot;
        private Border[] _navItemBorders;      // 顶层 5 项
        private Border[] _navActiveBars;
        private TextBlock[] _navTitleLabels;
        private Border[] _navChildBorders;     // 按视图索引的子页面行
        private Border[] _navChildBars;
        private StackPanel[] _navChildPanels;  // [用量, 设置]
        private Border _navPricingChild;
        private Shapes.Ellipse _navLiveNewDot;
        private Border _navDeviceCountBadge;
        private TextBlock _navDeviceCountText;
        private Border _sidebarDataPanel;
        private TextBlock _sidebarWatchText;
        private TextBlock _sidebarPriceText;
        private TextBlock _sidebarFxText;
        private TextBox _rateInput;            // 仅在汇率浮层打开时存在
        private Button _themeToggleBtn;
        private Shapes.Path _themeToggleIcon;
        private Button _sidebarRefreshBtn;
        private Button _sidebarHudBtn;
        private Button _sidebarHelpBtn;
        private Button _sidebarCollapseBtn;

        // 顶栏上下文与全局筛选控件
        private TextBlock _viewGroupText;
        private TextBlock _viewTitleText;
        private TextBlock _viewSubContextText;
        private Border _inlineAlertBadge;
        private TextBlock _inlineAlertText;
        private Button _syncStatusBtn;
        private Shapes.Path _syncStatusIcon;
        private TextBlock _syncStatusText;

        // 页面工具条（设备 / 时间范围 / 币种 / 导出）
        private DockPanel _pageToolbar;
        private Button _deviceSelectorBtn;
        private Shapes.Ellipse _deviceSelectorDot;
        private TextBlock _deviceSelectorText;
        private Border _rangeContainer;
        private Button[] _rangeBtns;
        private Button[] _currencyBtns;
        private Button _exportMenuBtn;
        private TextBlock _pageScopeText;

        // 设置页「更多」菜单里的开机自启项
        private MenuItem _autoStartMenuItem;

        // 十一大全高视图根节点与专项分析引擎视图
        private Grid _workspaceHostGrid;
        private UIElement[] _viewPages;
        private ProjectAttributionView _projectView;
        private SubscriptionCostsView _subscriptionView;
        private UsageHeatmapView _heatmapView;
        private SessionBloatAnalyzerView _bloatView;
        private DiagnosticsDoctorView _doctorView;
        private SyncSettingsView _settingsView;
        private bool _doctorLoadedOnce = false;

        // 视图 0（概览看板）控件
        private TextBlock _kpiCostVal;
        private Border _kpiTodayPill;
        private TextBlock _kpiTodayPillText;
        private TextBlock _kpiCostSub;
        private Border _kpiCardCost;

        private TextBlock _kpiTokVal;
        private TextBlock _kpiTokSub;
        private ColumnDefinition _tokSegInputCol;
        private ColumnDefinition _tokSegCacheCol;
        private ColumnDefinition _tokSegOutputCol;

        private TextBlock _kpiHitVal;
        private TextBlock _kpiHitSub;

        private TextBlock _kpiSaveVal;
        private TextBlock _kpiSaveSub;
        private Border _kpiSavePctBadge;
        private TextBlock _kpiSavePctText;

        private Button[] _leftModeBtns;
        private Button _clearFilterBtn;
        private StackPanel _toolsStack;

        private TextBlock _trendTitleText;
        private Border _selectedDatePill;
        private TextBlock _selectedDatePillText;
        private Button[] _chartTypeBtns;
        private Button[] _metricBtns;
        private Grid _chartInteractiveHost;
        private Canvas _chartCanvas;
        private Canvas _chartOverlayCanvas;
        private Shapes.Line _chartCrosshairLine;
        private Shapes.Ellipse _chartHoverDot;
        private Border _chartFloatingCard;
        private TextBlock _chartFloatingDateText;
        private TextBlock _chartFloatingPrimaryText;
        private TextBlock _chartFloatingSubText;
        private List<DailyRowView> _currentDailyAsc = new List<DailyRowView>();

        private TextBlock _overviewLiveTickerText;
        private DataGrid _overviewLiveGrid;
        private Border _overviewLiveEmpty;

        // 视图 1（实时请求流）控件与右侧单笔账单透视抽屉
        private TextBox _liveSearchInput;
        private Button _liveClearFilterBtn;
        private Button _livePauseBtn;
        private Shapes.Path _livePauseIcon;
        private TextBlock _livePauseBtnText;
        private Button _toggleInspectorBtn;
        private TextBlock _liveStreamSummaryText;
        private DataGrid _liveGrid;
        private Border _liveEmptyOverlay;
        private TextBlock _liveEmptyTitle;
        private ColumnDefinition _requestInspectorGapCol;
        private RequestInspectorPanel _requestInspector;

        // 视图 2（模型与工具账单）控件
        private Grid _modelStackedBarGrid;
        private WrapPanel _modelLegendWrap;
        private TextBox _modelSearchInput;
        private TextBlock _modelCountSummaryText;
        private DataGrid _modelGrid;
        private Border _modelEmptyOverlay;

        // 视图 7（设备与 VPS 集群）控件
        private StackPanel _deviceCardsStack;
        private TextBlock _deviceClusterSummaryText;
        private DataGrid _machineGrid;

        // 视图 8（每日流水与导出）控件
        private TextBox _dailySearchInput;
        private TextBlock _dailyFilteredSummaryText;
        private DataGrid _dailyGrid;
        private Border _dailyEmptyOverlay;

        // 缓存的最新视图列表供导出、单价工作台、专项分析与桌面 HUD 复用
        private List<MachineRowView> _latestMachineViews = new List<MachineRowView>();
        private List<RecordBucket> _latestExportBuckets = new List<RecordBucket>();
        private List<RecordBucket> _latestSpecialistMachineRows = new List<RecordBucket>();
        private List<RecordBucket> _latestSpecialistActiveRows = new List<RecordBucket>();
        private List<RecordBucket> _latestFullHistoryMachineRows = new List<RecordBucket>();
        private List<RecordBucket> _latestAllHistoryRows = new List<RecordBucket>();
        private List<RecordBucket> _latestFullHistoryScopedRows = new List<RecordBucket>();
        private List<string> _latestUniqueModelNames = new List<string>();
        private string _latestReferenceDate = "";
        private double _latestTodayUsd = 0.0;
        private long _latestTodayTokens = 0L;

        public MainWindow(string initialHomeDir) : this(initialHomeDir, false)
        {
        }

        public MainWindow(string initialHomeDir, bool skipAutoScan)
        {
            _homeDir = string.IsNullOrWhiteSpace(initialHomeDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : initialHomeDir;
            _skipAutoScan = skipAutoScan;

            _syncCfg = SyncService.LoadConfig();
            if (_syncCfg.NodeAliases == null)
                _syncCfg.NodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _isDark = _syncCfg.IsDarkTheme;
            _isCny = _syncCfg.PreferCny;

            _remoteNodes = LoadAndRecalculateRemoteNodes(_syncCfg.MachineId, _homeDir, _syncCfg);

            Title = AppConstants.ProductTitle + " — 多设备与 VPS AI Token 实时监控与账单透镜";

            bool recentCache;
            ExchangeRateInfo initFx = ExchangeRateService.LoadCachedOrDefault(out recentCache);
            _usdToCny = initFx.Rate;
            _fxSource = initFx.Source;
            _fxUpdatedAt = initFx.UpdatedAt;
            _fxIsLive = initFx.IsLive;

            Rect workArea = SystemParameters.WorkArea;
            Width = Math.Min(1400.0, Math.Max(1060.0, workArea.Width * 0.92));
            Height = Math.Min(900.0, Math.Max(700.0, workArea.Height * 0.90));
            MinWidth = 1020.0;
            MinHeight = 660.0;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            FontFamily = DesignSystem.FontUi;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            TryLoadExeIcon();
            DesignSystem.ApplyTheme(this, _isDark);
            SetResourceReference(Window.BackgroundProperty, "BgWindow");
            SetResourceReference(Window.ForegroundProperty, "FgPrimary");

            Content = BuildRootShellLayout();

            ModernWindowChrome.Attach(
                this,
                _rootShellBorder,
                _maxCaptionBtn,
                _maxCaptionPath,
                TopBarLockedHeight,
                delegate() { return _isDark; });

            PreviewKeyDown += OnMainWindowPreviewKeyDown;
            Closing += OnMainWindowClosing;
            // 关机 / 注销时不拦截关闭，否则「关闭时常驻托盘」会挡住系统结束会话
            if (Application.Current != null)
                Application.Current.SessionEnding += delegate { _explicitExitRequested = true; };
            SizeChanged += delegate(object s, SizeChangedEventArgs e) { ApplyResponsiveSidebar(e.NewSize.Width); };

            UpdateFxUiState(_fxIsLive, false);
            UpdatePriceUiState(false);
            UpdateCloudStatusUi();
            ApplySubscriptionFeature();
            SwitchActiveView(ViewOverview);

            if (!_skipAutoScan)
            {
                InitializeSystemTray();

                // 0ms 秒开：从本地 SQLite 快照库直接水合首屏数据（零遮罩、零白屏）
                ScanResult instantSnap = LocalUsageStore.TryLoadInstantSnapshot(_homeDir);
                if (instantSnap != null)
                {
                    _scanResult = instantSnap;
                    _localNode = SyncService.BuildLocalSnapshot(instantSnap, _syncCfg);
                    Program.ApplyNodeAliasToSnapshot(_localNode, _syncCfg);
                    if (instantSnap.RecentRequests != null && instantSnap.RecentRequests.Count > 0)
                        _lastTopRequestTs = instantSnap.RecentRequests[0].TimestampMs;
                    UpdatePriceUiState(false);
                    RefreshDashboardView();
                }
                else
                {
                    RefreshDashboardView();
                }

                Loaded += OnMainWindowLoaded;
                Closed += OnMainWindowClosed;
            }
        }

        private static List<NodeSnapshot> LoadAndRecalculateRemoteNodes(string localMachineId, string homeDir, SyncConfig cfg)
        {
            List<NodeSnapshot> nodes = SyncService.LoadCachedRemoteNodes(localMachineId);
            if (nodes == null) return new List<NodeSnapshot>();
            try
            {
                PricingEngine pe = new PricingEngine(homeDir);
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i] == null) continue;
                    pe.RecalculateRemoteSnapshot(nodes[i]);
                    Program.ApplyNodeAliasToSnapshot(nodes[i], cfg);
                }
            }
            catch { }
            return nodes;
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            SetupRealtimeWatchersAndTimers();
            try
            {
                _globalHotkey = new GlobalHotkeyHost(this, delegate()
                {
                    OnGlobalHotkeyTriggered();
                });
            }
            catch { }

            Task.Run(delegate()
            {
                try { WindowsShellIntegration.SyncAutoStartPathIfRegistered(); } catch { }
                try { BackupAndMigrationService.EnsureDailyAutoBackup(); } catch { }
            });

            RunSilentScanAsync(_homeDir, true, _scanResult == null);
            StartExchangeRateSyncAsync(false);
            StartCloudPricingSyncAsync(false);
        }

        private void OnMainWindowClosing(object sender, CancelEventArgs e)
        {
            if (_skipAutoScan || _explicitExitRequested)
                return;

            if (_syncCfg != null && _syncCfg.MinimizeToTray && _trayIcon != null)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShownOnce)
                {
                    _trayHintShownOnce = true;
                    try
                    {
                        _trayIcon.BalloonTipTitle = AppConstants.ProductTitle;
                        _trayIcon.BalloonTipText = "TokLens 仍在后台运行，悬浮球和用量监控不受影响。双击托盘图标或按 Ctrl+Alt+T 打开；要退出请在托盘图标右键选「彻底退出」。";
                        _trayIcon.ShowBalloonTip(2500);
                    }
                    catch { }
                }
            }
        }

        private void OnMainWindowClosed(object sender, EventArgs e)
        {
            if (_globalHotkey != null)
            {
                try { _globalHotkey.Dispose(); } catch { }
                _globalHotkey = null;
            }
            if (_miniHud != null)
            {
                try { _miniHud.Close(); } catch { }
                _miniHud = null;
            }
            DisposeWatchers();
            if (_fsDebounceTimer != null) _fsDebounceTimer.Stop();
            if (_livePulseTimer != null) _livePulseTimer.Stop();
            if (_cloudAutoTimer != null) _cloudAutoTimer.Stop();
            if (_cloudDebounceTimer != null) _cloudDebounceTimer.Stop();
            DisposeSystemTray();
        }

        public void RestoreAndActivateFromTray()
        {
            try
            {
                if (!IsVisible)
                    Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
            }
            catch { }
        }

        private void InitializeSystemTray()
        {
            try
            {
                _trayMenu = new WinForms.ContextMenuStrip();

                WinForms.ToolStripMenuItem openItem = new WinForms.ToolStripMenuItem("打开 知微 · TokLens 主界面");
                openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
                openItem.Click += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(RestoreAndActivateFromTray));
                };
                _trayMenu.Items.Add(openItem);

                _trayTodaySummaryItem = new WinForms.ToolStripMenuItem("今日实时: 计算中...");
                _trayTodaySummaryItem.Enabled = false;
                _trayMenu.Items.Add(_trayTodaySummaryItem);

                _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

                WinForms.ToolStripMenuItem syncRefreshItem = new WinForms.ToolStripMenuItem("立即扫描并同步多端数据");
                syncRefreshItem.Click += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        RunSilentScanAsync(_homeDir, true, true);
                    }));
                };
                _trayMenu.Items.Add(syncRefreshItem);

                _trayHudToggleItem = new WinForms.ToolStripMenuItem("显示 / 隐藏桌面悬浮球 (Ctrl+M)");
                _trayHudToggleItem.Click += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        ToggleMiniHudWindow();
                    }));
                };
                _trayMenu.Items.Add(_trayHudToggleItem);

                WinForms.ToolStripMenuItem guideItem = new WinForms.ToolStripMenuItem("快捷键与 GitHub 令牌原理解析 (F1)");
                guideItem.Click += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        RestoreAndActivateFromTray();
                        OpenShortcutAndGuideDialog(1);
                    }));
                };
                _trayMenu.Items.Add(guideItem);

                _trayMinimizeToggleItem = new WinForms.ToolStripMenuItem("关闭窗口时最小化到系统托盘");
                _trayMinimizeToggleItem.Checked = _syncCfg != null && _syncCfg.MinimizeToTray;
                _trayMinimizeToggleItem.Click += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        if (_syncCfg == null) return;
                        _syncCfg.MinimizeToTray = !_syncCfg.MinimizeToTray;
                        _trayMinimizeToggleItem.Checked = _syncCfg.MinimizeToTray;
                        PersistConfig(_syncCfg);
                        if (_settingsView != null) _settingsView.ReloadFromConfig();
                        ShowToastSafe(
                            _syncCfg.MinimizeToTray ? "已开启关闭最小化至托盘" : "已关闭最小化至托盘",
                            _syncCfg.MinimizeToTray ? "点击右上角关闭按钮时将后台常驻系统托盘" : "点击右上角关闭按钮将直接退出程序",
                            "info");
                    }));
                };
                _trayMenu.Items.Add(_trayMinimizeToggleItem);

                _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

                WinForms.ToolStripMenuItem exitItem = new WinForms.ToolStripMenuItem("彻底退出 TokLens");
                exitItem.Click += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        _explicitExitRequested = true;
                        Close();
                    }));
                };
                _trayMenu.Items.Add(exitItem);

                _trayIcon = new WinForms.NotifyIcon();
                _trayIcon.Icon = _nativeExeIcon ?? Drawing.SystemIcons.Application;
                _trayIcon.ContextMenuStrip = _trayMenu;
                _trayIcon.Text = TruncateTrayText(AppConstants.ProductTitle + " · 实时监控中");
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += delegate(object s, EventArgs e)
                {
                    Dispatcher.BeginInvoke(new Action(RestoreAndActivateFromTray));
                };
            }
            catch { }
        }

        private static string TruncateTrayText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "TokLens v2.3";
            // .NET Framework 4.x NotifyIcon.Text 严格要求长度 < 64，截断保护在 60 字符以内
            if (raw.Length <= 60) return raw;
            return raw.Substring(0, 59) + "…";
        }

        private void UpdateSystemTrayStats(double todayUsd, long todayTokens)
        {
            _latestTodayUsd = todayUsd;
            _latestTodayTokens = todayTokens;
            if (_trayIcon == null) return;
            try
            {
                string money = FormatterUtil.FormatMoney(todayUsd, _isCny, _usdToCny);
                string tok = FormatterUtil.FormatTokens(todayTokens);
                string tip = string.Format(CultureInfo.InvariantCulture, "知微 TokLens · 今日 {0} ({1})", money, tok);
                _trayIcon.Text = TruncateTrayText(tip);

                if (_trayTodaySummaryItem != null)
                {
                    _trayTodaySummaryItem.Text = string.Format(CultureInfo.InvariantCulture, "今日花费: {0}  |  Token: {1}", money, tok);
                }
                if (_trayMinimizeToggleItem != null && _syncCfg != null)
                {
                    _trayMinimizeToggleItem.Checked = _syncCfg.MinimizeToTray;
                }
            }
            catch { }
        }

        private void DisposeSystemTray()
        {
            if (_trayIcon != null)
            {
                try
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                catch { }
                _trayIcon = null;
            }
            if (_trayMenu != null)
            {
                try { _trayMenu.Dispose(); } catch { }
                _trayMenu = null;
            }
            if (_nativeExeIcon != null)
            {
                try { _nativeExeIcon.Dispose(); } catch { }
                _nativeExeIcon = null;
            }
        }

        private void TryLoadExeIcon()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                Drawing.Icon ico = Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (ico != null)
                {
                    _nativeExeIcon = ico;
                    Icon = Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch { }
        }

        private static bool IsSameDirectoryPath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
            try
            {
                string na = Path.GetFullPath(a.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string nb = Path.GetFullPath(b.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsRelevantUsageFilePath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string lower = fullPath.ToLowerInvariant();
            if (lower.EndsWith(".jsonl", StringComparison.Ordinal) ||
                lower.EndsWith(".json", StringComparison.Ordinal) ||
                lower.EndsWith(".sqlite", StringComparison.Ordinal) ||
                lower.EndsWith(".sqlite3", StringComparison.Ordinal) ||
                lower.EndsWith(".db", StringComparison.Ordinal) ||
                lower.EndsWith(".vscdb", StringComparison.Ordinal) ||
                lower.EndsWith("-wal", StringComparison.Ordinal) ||
                lower.EndsWith(".log", StringComparison.Ordinal) ||
                lower.EndsWith(".md", StringComparison.Ordinal))
            {
                return true;
            }
            return false;
        }

        private void SetupRealtimeWatchersAndTimers()
        {
            _fsDebounceTimer = new DispatcherTimer();
            _fsDebounceTimer.Interval = TimeSpan.FromMilliseconds(750);
            _fsDebounceTimer.Tick += delegate(object s, EventArgs args)
            {
                _fsDebounceTimer.Stop();
                RunSilentScanAsync(_homeDir, false, false);
            };

            _livePulseTimer = new DispatcherTimer();
            _livePulseTimer.Interval = TimeSpan.FromSeconds(4);
            _livePulseTimer.Tick += delegate(object s, EventArgs args)
            {
                _watcherTickCounter++;
                if (_watcherTickCounter % 8 == 0)
                {
                    EnsureCandidateWatchersAttached(_homeDir);
                }
                if (!_isScanning)
                    RunSilentScanAsync(_homeDir, false, false);
            };
            _livePulseTimer.Start();

            _cloudDebounceTimer = new DispatcherTimer();
            _cloudDebounceTimer.Interval = TimeSpan.FromSeconds(35);
            _cloudDebounceTimer.Tick += delegate(object s, EventArgs args)
            {
                _cloudDebounceTimer.Stop();
                if (_syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                    StartBackgroundCloudSync(false);
            };

            _cloudAutoTimer = new DispatcherTimer();
            _cloudAutoTimer.Interval = TimeSpan.FromMinutes(5);
            _cloudAutoTimer.Tick += delegate(object s, EventArgs args)
            {
                if (_syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                    StartBackgroundCloudSync(false);
            };
            _cloudAutoTimer.Start();

            RebuildDirectoryWatchers(_homeDir);
        }

        private List<string> BuildWatchCandidateDirectories(string targetHome)
        {
            List<string> candidates = new List<string>(24);
            if (!string.IsNullOrWhiteSpace(targetHome))
            {
                string[] homeRelDirs = new string[]
                {
                    ".claude",
                    ".codex",
                    ".gemini",
                    ".zcode",
                    ".kimi-code",
                    ".workbuddy",
                    ".openclaw",
                    ".qwen",
                    ".aider",
                    ".continue",
                    ".opencode",
                    ".factory",
                    ".amp",
                    ".cline",
                    ".roo"
                };
                for (int i = 0; i < homeRelDirs.Length; i++)
                {
                    try { candidates.Add(Path.Combine(targetHome, homeRelDirs[i])); } catch { }
                }

                string roaming = Path.Combine(targetHome, "AppData", "Roaming");
                string[] ideRelDirs = new string[]
                {
                    @"Code\User\globalStorage",
                    @"Code - Insiders\User\globalStorage",
                    @"Cursor\User\globalStorage",
                    @"Windsurf\User\globalStorage",
                    @"Trae\User\globalStorage",
                    @"VSCodium\User\globalStorage"
                };
                for (int i = 0; i < ideRelDirs.Length; i++)
                {
                    try { candidates.Add(Path.Combine(roaming, ideRelDirs[i])); } catch { }
                }
            }
            return candidates;
        }

        private void RebuildDirectoryWatchers(string targetHome)
        {
            DisposeWatchers();
            EnsureCandidateWatchersAttached(targetHome);
        }

        private void EnsureCandidateWatchersAttached(string targetHome)
        {
            List<string> candidates = BuildWatchCandidateDirectories(targetHome);
            for (int i = 0; i < candidates.Count; i++)
            {
                string dir = candidates[i];
                if (string.IsNullOrEmpty(dir) || _watchedDirPaths.Contains(dir))
                    continue;

                try
                {
                    if (!Directory.Exists(dir)) continue;

                    FileSystemWatcher fsw = new FileSystemWatcher(dir);
                    fsw.IncludeSubdirectories = true;
                    fsw.InternalBufferSize = 65536;
                    fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;

                    FileSystemEventHandler onFsChange = delegate(object s, FileSystemEventArgs ev)
                    {
                        if (ev == null || !IsRelevantUsageFilePath(ev.FullPath))
                            return;
                        TriggerWatcherDebounceOnUi();
                    };

                    RenamedEventHandler onFsRenamed = delegate(object s, RenamedEventArgs ev)
                    {
                        if (ev == null || (!IsRelevantUsageFilePath(ev.FullPath) && !IsRelevantUsageFilePath(ev.OldFullPath)))
                            return;
                        TriggerWatcherDebounceOnUi();
                    };

                    string capturedDir = dir;
                    ErrorEventHandler onFsError = delegate(object s, ErrorEventArgs errEv)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate()
                        {
                            try
                            {
                                _watchedDirPaths.Remove(capturedDir);
                                FileSystemWatcher broken = s as FileSystemWatcher;
                                if (broken != null)
                                {
                                    _watchers.Remove(broken);
                                    try { broken.EnableRaisingEvents = false; broken.Dispose(); } catch { }
                                }
                                EnsureCandidateWatchersAttached(_homeDir);
                                RunSilentScanAsync(_homeDir, false, false);
                            }
                            catch { }
                        }));
                    };

                    fsw.Changed += onFsChange;
                    fsw.Created += onFsChange;
                    fsw.Renamed += onFsRenamed;
                    fsw.Error += onFsError;
                    fsw.EnableRaisingEvents = true;

                    _watchers.Add(fsw);
                    _watchedDirPaths.Add(dir);
                }
                catch { }
            }
        }

        private void TriggerWatcherDebounceOnUi()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    if (_fsDebounceTimer != null)
                    {
                        _fsDebounceTimer.Stop();
                        _fsDebounceTimer.Start();
                    }
                }));
            }
            catch { }
        }

        private void DisposeWatchers()
        {
            for (int i = 0; i < _watchers.Count; i++)
            {
                try
                {
                    _watchers[i].EnableRaisingEvents = false;
                    _watchers[i].Dispose();
                }
                catch { }
            }
            _watchers.Clear();
            _watchedDirPaths.Clear();
        }

        private void RunSilentScanAsync(string targetHome, bool forceUiRefresh, bool triggerCloudSyncIfEnabled)
        {
            if (_isScanning)
            {
                _pendingRescan = true;
                _pendingForceUi = _pendingForceUi || forceUiRefresh;
                _pendingTriggerCloud = _pendingTriggerCloud || triggerCloudSyncIfEnabled;
                return;
            }

            _isScanning = true;
            if (_sidebarRefreshBtn != null && forceUiRefresh)
                _sidebarRefreshBtn.IsEnabled = false;

            bool scanWsl = _syncCfg != null && _syncCfg.ScanLocalWsl;
            SyncConfig cfgSnapshot = _syncCfg != null ? _syncCfg.Clone() : SyncService.LoadConfig();

            Task.Run(delegate()
            {
                try
                {
                    TokenScanner scanner = new TokenScanner(targetHome, scanWsl);
                    ScanResult res = scanner.Run();
                    NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(res, cfgSnapshot);
                    Program.ApplyNodeAliasToSnapshot(localSnap, cfgSnapshot);

                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        bool staleHome = !IsSameDirectoryPath(targetHome, _homeDir);
                        _isScanning = false;
                        if (_sidebarRefreshBtn != null)
                            _sidebarRefreshBtn.IsEnabled = true;

                        if (!staleHome)
                        {
                            long topTs = (res.RecentRequests != null && res.RecentRequests.Count > 0)
                                ? res.RecentRequests[0].TimestampMs
                                : 0L;
                            bool hasChanges = forceUiRefresh ||
                                              _scanResult == null ||
                                              res.ChangedFilesCount > 0 ||
                                              topTs != _lastTopRequestTs;

                            bool gotNewTopRequest = _lastTopRequestTs > 0L && topTs > _lastTopRequestTs;
                            _scanResult = res;
                            _localNode = localSnap;
                            _lastTopRequestTs = topTs;
                            UpdatePriceUiState(false);

                            if (gotNewTopRequest)
                            {
                                if (_livePaused)
                                    _pausedBufferedCount++;
                                if (_activeView != ViewLiveStream)
                                {
                                    _hasUnreadLivePulse = true;
                                    if (_navLiveNewDot != null)
                                        _navLiveNewDot.Visibility = Visibility.Visible;
                                }
                            }

                            if (hasChanges)
                            {
                                RefreshDashboardView();
                                if (_settingsView != null)
                                    _settingsView.RefreshDynamicStats();

                                if (res.ChangedFilesCount > 0 && _cloudDebounceTimer != null)
                                {
                                    _cloudDebounceTimer.Stop();
                                    _cloudDebounceTimer.Start();
                                }
                            }
                            else
                            {
                                UpdateTopSubContextText();
                            }

                            if (triggerCloudSyncIfEnabled && _syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                            {
                                StartBackgroundCloudSync(false);
                            }
                        }

                        if (_pendingRescan)
                        {
                            bool nextForce = _pendingForceUi;
                            bool nextCloud = _pendingTriggerCloud;
                            _pendingRescan = false;
                            _pendingForceUi = false;
                            _pendingTriggerCloud = false;
                            RunSilentScanAsync(_homeDir, nextForce, nextCloud);
                        }
                    }));
                }
                catch
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        _isScanning = false;
                        if (_sidebarRefreshBtn != null)
                            _sidebarRefreshBtn.IsEnabled = true;

                        if (_pendingRescan)
                        {
                            bool nextForce = _pendingForceUi;
                            bool nextCloud = _pendingTriggerCloud;
                            _pendingRescan = false;
                            _pendingForceUi = false;
                            _pendingTriggerCloud = false;
                            RunSilentScanAsync(_homeDir, nextForce, nextCloud);
                        }
                    }));
                }
            });
        }

        private void StartBackgroundCloudSync(bool manualTrigger)
        {
            if (_cloudSyncRunning || _localNode == null || _syncCfg == null || !_syncCfg.IsConfigured)
                return;

            _cloudSyncRunning = true;
            UpdateCloudStatusUi();

            SyncConfig cfgCopy = _syncCfg.Clone();
            NodeSnapshot localCopy = _localNode.Clone();
            string homeSnapshot = _homeDir;

            Task.Run(delegate()
            {
                SyncOutcome outcome = SyncService.SyncNow(cfgCopy, localCopy, false);
                if (outcome != null && outcome.RemoteNodes != null)
                {
                    try
                    {
                        PricingEngine pe = new PricingEngine(homeSnapshot);
                        for (int i = 0; i < outcome.RemoteNodes.Count; i++)
                        {
                            if (outcome.RemoteNodes[i] != null)
                                pe.RecalculateRemoteSnapshot(outcome.RemoteNodes[i]);
                        }
                    }
                    catch { }
                }

                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _cloudSyncRunning = false;
                    _cloudHasAttempted = true;
                    _cloudLastOk = outcome != null && outcome.Success;
                    if (_cloudLastOk) _lastCloudSyncAt = DateTime.Now;
                    _lastSyncStatus = outcome != null ? (outcome.Message ?? "") : "同步未返回状态";

                    if (outcome != null && outcome.RemoteNodes != null)
                    {
                        for (int i = 0; i < outcome.RemoteNodes.Count; i++)
                        {
                            Program.ApplyNodeAliasToSnapshot(outcome.RemoteNodes[i], _syncCfg);
                        }
                        _remoteNodes = outcome.RemoteNodes;
                    }

                    UpdateCloudStatusUi();
                    RefreshDashboardView();
                    if (_settingsView != null)
                        _settingsView.RefreshDynamicStats();

                    if (manualTrigger)
                    {
                        if (_cloudLastOk)
                            ShowToastSafe("多端云同步完成", _lastSyncStatus, "success");
                        else
                            ShowToastSafe("云端同步遇到问题", _lastSyncStatus, "warning");
                    }
                }));
            });
        }

        private void StartCloudPricingSyncAsync(bool forceRefresh)
        {
            if (_priceIsSyncing) return;
            _priceIsSyncing = true;
            UpdatePriceUiState(true);

            Task.Run(delegate()
            {
                PricingSyncInfo info = PricingEngine.SyncCloudPricing(forceRefresh);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _priceIsSyncing = false;
                    if (info != null)
                    {
                        _priceSyncSource = info.Source;
                        _priceSyncTime = info.UpdatedAt;
                    }
                    UpdatePriceUiState(false);

                    // 使用最新价格库重算远端缓存快照与本机账单
                    if (_remoteNodes != null && _remoteNodes.Count > 0)
                    {
                        try
                        {
                            PricingEngine pe = new PricingEngine(_homeDir);
                            for (int i = 0; i < _remoteNodes.Count; i++)
                            {
                                if (_remoteNodes[i] != null)
                                    pe.RecalculateRemoteSnapshot(_remoteNodes[i]);
                            }
                        }
                        catch { }
                    }

                    if ((info != null && info.CloudModelCount > 0) || forceRefresh)
                    {
                        RunSilentScanAsync(_homeDir, true, false);
                        if (forceRefresh)
                        {
                            int modelTotal = Math.Max(info != null ? info.CloudModelCount : 0, _scanResult != null ? _scanResult.PricingModelCount : 0);
                            ShowToastSafe(
                                "云端价格库已刷新",
                                string.Format(CultureInfo.InvariantCulture, "来源: {0}（已覆盖 {1:N0}+ 模型单价）", _priceSyncSource, modelTotal),
                                "success");
                        }
                    }
                }));
            });
        }

        private void StartExchangeRateSyncAsync(bool forceRefresh)
        {
            if (_fxIsSyncing) return;
            _fxIsSyncing = true;
            UpdateFxUiState(_fxIsLive, true);

            Task.Run(delegate()
            {
                ExchangeRateInfo fx = ExchangeRateService.FetchLatestUsdToCny(forceRefresh);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _fxIsSyncing = false;
                    ApplyFetchedRate(fx);
                    if (forceRefresh)
                    {
                        ShowToastSafe(
                            "USD/CNY 汇率已更新",
                            string.Format(CultureInfo.InvariantCulture, "当前汇率: {0:0.####}（来源: {1}）", fx.Rate, fx.Source),
                            fx.IsLive ? "success" : "info");
                    }
                }));
            });
        }

        private void ApplyFetchedRate(ExchangeRateInfo fx)
        {
            _usdToCny = fx.Rate;
            _fxSource = fx.Source;
            _fxUpdatedAt = fx.UpdatedAt;
            _fxIsLive = fx.IsLive;
            _suppressRateEvent = true;
            try
            {
                if (_rateInput != null)
                    _rateInput.Text = _usdToCny.ToString("0.####", CultureInfo.InvariantCulture);
            }
            finally
            {
                _suppressRateEvent = false;
            }
            UpdateFxUiState(fx.IsLive, false);
            RefreshDashboardView();
            if (_settingsView != null)
                _settingsView.RefreshDynamicStats();
        }

        // 自测会改动主题等内存配置；这些改动只能留在本次进程里，不能写回用户的 config.json。
        private bool _configWritesSuppressed = false;

        private void PersistConfig(SyncConfig cfg)
        {
            if (_configWritesSuppressed || cfg == null) return;
            SyncService.SaveConfig(cfg);
        }

        internal void RunHeadlessUiSelfTest()
        {
            _configWritesSuppressed = true;
            // 自测要覆盖全部页面：在内存里临时打开可选的订阅页（不写配置），结束时恢复
            bool subscriptionsBefore = _syncCfg.ShowSubscriptions;
            _syncCfg.ShowSubscriptions = true;
            ApplySubscriptionFeature();
            TokenScanner scanner = new TokenScanner(_homeDir, false);
            _scanResult = scanner.Run();
            _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);

            NodeSnapshot homePc = new NodeSnapshot();
            homePc.MachineId = "win-home-pc";
            homePc.MachineName = "家里电脑";
            homePc.MachineType = "windows";
            homePc.OsInfo = "Windows 11 Pro 24H2";
            homePc.SyncedAt = DateTime.Now.AddMinutes(-12).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            homePc.IsLocal = false;
            RecordBucket hb = new RecordBucket();
            hb.MachineId = homePc.MachineId;
            hb.MachineName = homePc.MachineName;
            hb.MachineType = homePc.MachineType;
            hb.Date = _scanResult.ReferenceDate;
            hb.Tool = "Claude Desktop";
            hb.Model = "claude-sonnet-4-6";
            hb.Turns = 42;
            hb.FreshInput = 125000;
            hb.CacheRead = 1850000;
            hb.CacheWrite = 95000;
            hb.Output = 48000;
            hb.CostUsd = 2.0112;
            hb.BaselineUsd = 6.915;
            hb.RateLabel = "$3 / $15";
            homePc.Records.Add(hb);

            NodeSnapshot linuxVps = new NodeSnapshot();
            linuxVps.MachineId = "vps-hk-01";
            linuxVps.MachineName = "香港 VPS-1";
            linuxVps.MachineType = "vps";
            linuxVps.OsInfo = "Ubuntu 24.04 LTS x86_64";
            linuxVps.SyncedAt = DateTime.Now.AddHours(-30).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            linuxVps.IsLocal = false;
            RecordBucket vb = new RecordBucket();
            vb.MachineId = linuxVps.MachineId;
            vb.MachineName = linuxVps.MachineName;
            vb.MachineType = linuxVps.MachineType;
            vb.Date = _scanResult.ReferenceDate;
            vb.Tool = "OpenClaw";
            vb.Model = "deepseek-v3.2";
            vb.Turns = 88;
            vb.FreshInput = 360000;
            vb.CacheRead = 2400000;
            vb.CacheWrite = 0;
            vb.Output = 92000;
            vb.CostUsd = 0.2072;
            vb.BaselineUsd = 0.8114;
            vb.RateLabel = "$0.28 / $0.42";
            linuxVps.Records.Add(vb);

            PricingEngine pe = new PricingEngine(_homeDir);
            pe.RecalculateRemoteSnapshot(homePc);
            pe.RecalculateRemoteSnapshot(linuxVps);

            _remoteNodes = new List<NodeSnapshot> { homePc, linuxVps };

            UIElement root = (UIElement)Content;
            root.Measure(new Size(1320, 860));
            root.Arrange(new Rect(0, 0, 1320, 860));

            ApplyFetchedRate(new ExchangeRateInfo(7.1852, "ExchangeRate-API", "15:20", true));
            RefreshDashboardView();
            root.UpdateLayout();

            double baseTopH = _topContextBarBorder.ActualHeight;
            double baseSideW = _sidebarBorder.ActualWidth;

            // 遍历全部 11 大全高旗舰视图，断言顶栏 48px 与侧栏 200px 实现 0.0px 零跳动
            for (int v = 0; v < TotalViewCount; v++)
            {
                SwitchActiveView(v);
                if (v == ViewDevices)
                {
                    _selectedMachineId = "vps-hk-01";
                    _rangeDays = 7;
                }
                else if (v == ViewOverview)
                {
                    _selectedDate = _scanResult.ReferenceDate;
                }
                RefreshDashboardView();
                root.UpdateLayout();

                double curTopH = _topContextBarBorder.ActualHeight;
                double curSideW = _sidebarBorder.ActualWidth;
                if (Math.Abs(curTopH - baseTopH) > 0.05 || Math.Abs(curSideW - baseSideW) > 0.05)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Layout jump detected on view {0}! TopBar: {1} -> {2}, Sidebar: {3} -> {4}",
                        v, baseTopH, curTopH, baseSideW, curSideW));
                }
            }

            // 验证全局 Ctrl+K 命令面板开关与单笔请求账单透视抽屉无头渲染稳定性
            OpenCommandPalette();
            root.UpdateLayout();
            if (_commandPalette != null && _commandPalette.IsOpen)
                _commandPalette.Close();
            root.UpdateLayout();

            SwitchActiveView(ViewLiveStream);
            if (_liveGrid != null && _liveGrid.Items.Count > 0)
            {
                LiveRequestRowView firstLive = _liveGrid.Items[0] as LiveRequestRowView;
                if (firstLive != null)
                    OpenRequestInspectorFor(firstLive);
            }
            root.UpdateLayout();
            CloseRequestInspector();
            root.UpdateLayout();

            // 测试侧边栏折叠 (58px) 与展开 (200px) 稳定性
            ToggleSidebarCollapsed();
            root.UpdateLayout();
            if (Math.Abs(_sidebarBorder.ActualWidth - SidebarCollapsedWidth) > 0.05 ||
                Math.Abs(_topContextBarBorder.ActualHeight - baseTopH) > 0.05)
            {
                throw new InvalidOperationException("Sidebar collapse geometry mismatch!");
            }
            ToggleSidebarCollapsed();
            root.UpdateLayout();
            if (Math.Abs(_sidebarBorder.ActualWidth - baseSideW) > 0.05)
            {
                throw new InvalidOperationException("Sidebar expand geometry mismatch!");
            }

            // 测试深浅色主题往返切换下的 0.0px 稳定性
            _selectedMachineId = null;
            _selectedDate = null;
            _rangeDays = 0;
            SwitchActiveView(ViewOverview);
            ToggleThemeFromUi();
            root.UpdateLayout();
            ToggleThemeFromUi();
            root.UpdateLayout();

            if (Math.Abs(_topContextBarBorder.ActualHeight - baseTopH) > 0.05 ||
                Math.Abs(_sidebarBorder.ActualWidth - baseSideW) > 0.05)
            {
                throw new InvalidOperationException("Layout jump detected after theme toggle & budget alert badge!");
            }

            _syncCfg.ShowSubscriptions = subscriptionsBefore;
            ApplySubscriptionFeature();
        }

        // =================================================================================================
        // 1. 无边框一体化窗口外壳 + 左侧紧凑导航栏 (200px / 58px) + 右侧 48px 上下文顶栏
        // =================================================================================================
        private UIElement BuildRootShellLayout()
        {
            _toastManager = new ToastManager();

            Grid outerLayer = new Grid();
            outerLayer.SetResourceReference(Panel.BackgroundProperty, "BgWindow");

            _rootShellBorder = new Border();
            _rootShellBorder.BorderThickness = new Thickness(1);
            _rootShellBorder.SetResourceReference(Border.BackgroundProperty, "BgWindow");
            _rootShellBorder.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid shellCols = new Grid();
            _sidebarColDef = new ColumnDefinition { Width = new GridLength(SidebarExpandedWidth) };
            shellCols.ColumnDefinitions.Add(_sidebarColDef);
            shellCols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            shellCols.Children.Add(sidebar);

            Grid rightArea = new Grid();
            rightArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TopBarLockedHeight) });
            rightArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            UIElement topContextBar = BuildTopContextBar();
            Grid.SetRow(topContextBar, 0);
            rightArea.Children.Add(topContextBar);

            _workspaceHostGrid = new Grid();
            _workspaceHostGrid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            _workspaceHostGrid.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            UIElement pageToolbar = BuildPageToolbar();
            Grid.SetRow(pageToolbar, 0);
            _workspaceHostGrid.Children.Add(pageToolbar);
            _workspaceHostGrid.SetResourceReference(Panel.BackgroundProperty, "BgWindow");
            _viewPages = new UIElement[TotalViewCount];
            _viewPages[ViewOverview] = BuildOverviewView();
            _viewPages[ViewLiveStream] = BuildLiveStreamView();
            _viewPages[ViewModels] = BuildModelsView();
            _projectView = new ProjectAttributionView(_homeDir, ShowToastSafe);
            _projectView.Margin = new Thickness(16, 12, 16, 14); // 与其他页面同一套页边距
            _viewPages[ViewProjects] = _projectView;
            _subscriptionView = new SubscriptionCostsView();
            _subscriptionView.Bind(_syncCfg, delegate(SyncConfig updatedCfg)
            {
                PersistConfig(updatedCfg);
                if (_settingsView != null) _settingsView.ReloadFromConfig();
            });
            _viewPages[ViewInsights] = _subscriptionView;
            _bloatView = new SessionBloatAnalyzerView();
            _bloatView.Margin = new Thickness(16, 12, 16, 14);
            _viewPages[ViewBloat] = _bloatView;
            _viewPages[ViewDevices] = BuildDevicesView();
            _viewPages[ViewDailyExport] = BuildDailyExportView();
            _doctorView = new DiagnosticsDoctorView(_homeDir, ShowToastSafe);
            _viewPages[ViewDoctor] = _doctorView;
            _viewPages[ViewSettings] = BuildSettingsView();

            for (int i = 0; i < _viewPages.Length; i++)
            {
                if (_viewPages[i] != null)
                {
                    _viewPages[i].Visibility = (i == _activeView) ? Visibility.Visible : Visibility.Collapsed;
                    Grid.SetRow(_viewPages[i],1);
                    _workspaceHostGrid.Children.Add(_viewPages[i]);
                }
            }

            Grid.SetRow(_workspaceHostGrid, 1);
            rightArea.Children.Add(_workspaceHostGrid);

            Grid.SetColumn(rightArea, 1);
            shellCols.Children.Add(rightArea);

            _rootShellBorder.Child = shellCols;
            outerLayer.Children.Add(_rootShellBorder);

            _commandPalette = new CommandPaletteOverlay();
            Panel.SetZIndex(_commandPalette, 950);
            outerLayer.Children.Add(_commandPalette);

            UIElement toastHost = _toastManager.HostElement;
            Panel.SetZIndex(toastHost, 999);
            outerLayer.Children.Add(toastHost);

            return outerLayer;
        }

        // 侧栏：5 个顶层入口；「用量」「设置」是分组，子页面直接挂在侧栏里（取代原先内容区顶部的第二行 tab）。
        private static readonly int[] NavTopViews = new int[] { ViewOverview, ViewLiveStream, ViewInsights, ViewDevices, ViewSettings };
        private static readonly string[] NavTopLabels = new string[] { "概览", "用量", "订阅与费用", "设备", "设置" };
        private static readonly int[] NavUsageChildViews = new int[] { ViewLiveStream, ViewModels, ViewProjects, ViewBloat, ViewDailyExport };
        private static readonly int[] NavSettingsChildViews = new int[] { ViewSettings, ViewDoctor };
        private const int NavTopUsage = 1;
        private const int NavTopSettings = 4;

        private static readonly string[] ViewShortcuts = new string[]
        {
            "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+7", "Ctrl+8", "Ctrl+9", "Ctrl+0", "Ctrl+,"
        };

        private UIElement BuildSidebar()
        {
            _sidebarBorder = new Border();
            _sidebarBorder.Width = SidebarExpandedWidth;
            _sidebarBorder.BorderThickness = new Thickness(0, 0, 1, 0);
            _sidebarBorder.SetResourceReference(Border.BackgroundProperty, "BgSidebar");
            _sidebarBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TopBarLockedHeight) }); // Row 0: 品牌
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 1: 导航
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // Row 2: 运行状态
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42.0) });                 // Row 3: 工具条

            // Row 0: 品牌（实时监听状态点已移到底部状态区，给版本号留出完整宽度）
            Border brandHeader = new Border();
            brandHeader.Height = TopBarLockedHeight;
            brandHeader.BorderThickness = new Thickness(0, 0, 0, 1);
            brandHeader.Padding = new Thickness(13, 0, 8, 0);
            brandHeader.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            StackPanel brandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Border logoMark = new Border();
            logoMark.Width = 28;
            logoMark.Height = 28;
            logoMark.CornerRadius = new CornerRadius(7);
            logoMark.BorderThickness = new Thickness(1);
            logoMark.VerticalAlignment = VerticalAlignment.Center;
            logoMark.ToolTip = AppConstants.ProductTitle + " " + AppConstants.VersionTag;
            logoMark.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            logoMark.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            Shapes.Path prismPath = new Shapes.Path();
            prismPath.Data = Geometry.Parse("M 8,2 L 14.2,8 L 8,14 L 1.8,8 Z M 8,4.8 L 11.2,8 L 8,11.2 L 4.8,8 Z");
            prismPath.Width = 15;
            prismPath.Height = 15;
            prismPath.Stretch = Stretch.Uniform;
            prismPath.StrokeThickness = 1.45;
            prismPath.StrokeLineJoin = PenLineJoin.Round;
            prismPath.HorizontalAlignment = HorizontalAlignment.Center;
            prismPath.VerticalAlignment = VerticalAlignment.Center;
            prismPath.SetResourceReference(Shapes.Shape.StrokeProperty, "AccentText");
            logoMark.Child = prismPath;
            brandRow.Children.Add(logoMark);

            _brandTextPanel = new StackPanel();
            _brandTextPanel.Orientation = Orientation.Horizontal;
            _brandTextPanel.VerticalAlignment = VerticalAlignment.Center;
            _brandTextPanel.Margin = new Thickness(9, 0, 0, 0);

            TextBlock titleTb = new TextBlock();
            titleTb.Text = "知微 TokLens";
            titleTb.FontSize = DesignSystem.TextLabel;
            titleTb.FontWeight = FontWeights.SemiBold;
            titleTb.VerticalAlignment = VerticalAlignment.Center;
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            _brandTextPanel.Children.Add(titleTb);

            Border verBadge = new Border();
            verBadge.CornerRadius = new CornerRadius(4);
            verBadge.Padding = new Thickness(4.5, 1, 4.5, 1);
            verBadge.Margin = new Thickness(6, 0, 0, 0);
            verBadge.VerticalAlignment = VerticalAlignment.Center;
            verBadge.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            verBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            verBadge.BorderThickness = new Thickness(1);

            TextBlock verTb = new TextBlock();
            verTb.Text = AppConstants.VersionTag;
            verTb.FontFamily = DesignSystem.FontMono;
            verTb.FontSize = DesignSystem.TextCaption;
            verTb.FontWeight = FontWeights.Medium;
            verTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            verBadge.Child = verTb;
            _brandTextPanel.Children.Add(verBadge);

            brandRow.Children.Add(_brandTextPanel);
            brandHeader.Child = brandRow;
            Grid.SetRow(brandHeader, 0);
            g.Children.Add(brandHeader);

            // Row 1: 导航（顶层 5 项，用量 / 设置下的子页面始终展开）
            ScrollViewer navScroll = new ScrollViewer();
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            navScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.PanningMode = PanningMode.VerticalOnly;

            StackPanel navStack = new StackPanel();
            navStack.Margin = new Thickness(8, 7, 8, 6);

            string[] topIcons = new string[] { UiIcons.Overview, UiIcons.Activity, UiIcons.Sparkles, UiIcons.Devices, UiIcons.Settings };

            _navItemBorders = new Border[NavTopViews.Length];
            _navActiveBars = new Border[NavTopViews.Length];
            _navTitleLabels = new TextBlock[NavTopViews.Length];
            _navChildBorders = new Border[TotalViewCount];
            _navChildBars = new Border[TotalViewCount];
            _navChildPanels = new StackPanel[2];

            for (int i = 0; i < NavTopViews.Length; i++)
            {
                int targetView = NavTopViews[i];
                string tip;
                if (i == NavTopUsage) tip = "用量：请求明细、模型与工具、项目、会话分析、每日汇总";
                else if (i == NavTopSettings) tip = "设置：同步与偏好、环境检查、计价规则 (Ctrl+,)";
                else tip = NavTopLabels[i] + " (" + ViewShortcuts[targetView] + ")";

                Border topBar;
                TextBlock topLabel;
                Grid topGrid;
                Border topRow = BuildNavRow(topIcons[i], NavTopLabels[i], tip, false,
                    delegate() { SwitchActiveView(targetView); }, out topBar, out topLabel, out topGrid);
                _navItemBorders[i] = topRow;
                _navActiveBars[i] = topBar;
                _navTitleLabels[i] = topLabel;
                navStack.Children.Add(topRow);

                if (targetView == ViewDevices)
                {
                    _navDeviceCountBadge = new Border();
                    _navDeviceCountBadge.CornerRadius = new CornerRadius(8);
                    _navDeviceCountBadge.Padding = new Thickness(6, 1, 6, 1);
                    _navDeviceCountBadge.Margin = new Thickness(4, 0, 8, 0);
                    _navDeviceCountBadge.VerticalAlignment = VerticalAlignment.Center;
                    _navDeviceCountBadge.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                    _navDeviceCountBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                    _navDeviceCountBadge.BorderThickness = new Thickness(1);

                    _navDeviceCountText = new TextBlock();
                    _navDeviceCountText.Text = "1";
                    _navDeviceCountText.FontFamily = DesignSystem.FontMono;
                    _navDeviceCountText.FontSize = DesignSystem.TextCaption;
                    _navDeviceCountText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                    _navDeviceCountBadge.Child = _navDeviceCountText;

                    Grid.SetColumn(_navDeviceCountBadge, 3);
                    topGrid.Children.Add(_navDeviceCountBadge);
                }

                if (i != NavTopUsage && i != NavTopSettings)
                    continue;

                StackPanel children = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                int[] childViews = (i == NavTopUsage) ? NavUsageChildViews : NavSettingsChildViews;
                for (int c = 0; c < childViews.Length; c++)
                {
                    int childView = childViews[c];
                    Border childBar;
                    TextBlock childLabel;
                    Grid childGrid;
                    Border childRow = BuildNavRow(null, ViewTitles[childView], ViewTitles[childView] + " (" + ViewShortcuts[childView] + ")", true,
                        delegate() { SwitchActiveView(childView); }, out childBar, out childLabel, out childGrid);
                    _navChildBorders[childView] = childRow;
                    _navChildBars[childView] = childBar;
                    children.Children.Add(childRow);

                    if (childView == ViewLiveStream)
                    {
                        _navLiveNewDot = new Shapes.Ellipse();
                        _navLiveNewDot.Width = 6;
                        _navLiveNewDot.Height = 6;
                        _navLiveNewDot.Margin = new Thickness(4, 0, 10, 0);
                        _navLiveNewDot.VerticalAlignment = VerticalAlignment.Center;
                        _navLiveNewDot.Visibility = Visibility.Collapsed;
                        _navLiveNewDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticSuccess");
                        Grid.SetColumn(_navLiveNewDot, 3);
                        childGrid.Children.Add(_navLiveNewDot);
                    }
                }

                if (i == NavTopSettings)
                {
                    // 计价规则是独立窗口，不是视图；放在设置分组里，和其他设置项在同一处找到。
                    Border pricingBar;
                    TextBlock pricingLabel;
                    Grid pricingGrid;
                    _navPricingChild = BuildNavRow(null, "计价规则", "模型单价与自定义费率（打开计价规则窗口）", true,
                        delegate() { OpenPricingStudioDialog(null); }, out pricingBar, out pricingLabel, out pricingGrid);
                    children.Children.Add(_navPricingChild);
                }

                _navChildPanels[i == NavTopUsage ? 0 : 1] = children;
                navStack.Children.Add(children);
            }

            navScroll.Content = navStack;
            Grid.SetRow(navScroll, 1);
            g.Children.Add(navScroll);

            // Row 2: 运行状态（两行只读状态，点开再操作；取代原先常驻的价格库 / 汇率输入面板）
            _sidebarDataPanel = new Border();
            _sidebarDataPanel.Margin = new Thickness(10, 4, 10, 8);
            _sidebarDataPanel.Padding = new Thickness(6, 4, 6, 4);

            StackPanel statusSp = new StackPanel();

            StackPanel watchRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = "实时监听与本地归档保护（点击查看详情）"
            };
            _sidebarPulseDot = new Shapes.Ellipse();
            _sidebarPulseDot.Width = 6;
            _sidebarPulseDot.Height = 6;
            _sidebarPulseDot.Margin = new Thickness(1, 0, 7, 0);
            _sidebarPulseDot.VerticalAlignment = VerticalAlignment.Center;
            _sidebarPulseDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticSuccess");
            watchRow.Children.Add(_sidebarPulseDot);

            _sidebarWatchText = new TextBlock();
            _sidebarWatchText.Text = "实时监听就绪";
            _sidebarWatchText.FontSize = DesignSystem.TextCaption;
            _sidebarWatchText.VerticalAlignment = VerticalAlignment.Center;
            _sidebarWatchText.TextTrimming = TextTrimming.CharacterEllipsis;
            _sidebarWatchText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            ModernControlsFactory.EnableTabularNumbers(_sidebarWatchText, false);
            watchRow.Children.Add(_sidebarWatchText);
            watchRow.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                OpenArchiveStatusPopover(watchRow);
            };
            statusSp.Children.Add(watchRow);

            StackPanel dataRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };

            _sidebarPriceText = new TextBlock();
            _sidebarPriceText.Text = "价格库";
            _sidebarPriceText.FontSize = DesignSystem.TextCaption;
            _sidebarPriceText.VerticalAlignment = VerticalAlignment.Center;
            ModernControlsFactory.EnableTabularNumbers(_sidebarPriceText, false);
            MakeSidebarStatusLink(_sidebarPriceText, delegate() { OpenPricingStudioDialog(null); });
            dataRow.Children.Add(_sidebarPriceText);

            TextBlock dotSep = new TextBlock { Text = " · ", FontSize = DesignSystem.TextCaption, VerticalAlignment = VerticalAlignment.Center };
            dotSep.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            dataRow.Children.Add(dotSep);

            _sidebarFxText = new TextBlock();
            _sidebarFxText.Text = "汇率 " + _usdToCny.ToString("0.###", CultureInfo.InvariantCulture);
            _sidebarFxText.FontSize = DesignSystem.TextCaption;
            _sidebarFxText.VerticalAlignment = VerticalAlignment.Center;
            ModernControlsFactory.EnableTabularNumbers(_sidebarFxText, false);
            MakeSidebarStatusLink(_sidebarFxText, delegate() { OpenFxRatePopover(_sidebarFxText); });
            dataRow.Children.Add(_sidebarFxText);

            statusSp.Children.Add(dataRow);
            _sidebarDataPanel.Child = statusSp;
            Grid.SetRow(_sidebarDataPanel, 2);
            g.Children.Add(_sidebarDataPanel);

            // Row 3: 底部工具条（深浅主题切换、立即扫描刷新、悬浮球、帮助、折叠侧边栏）
            Border footerBar = new Border();
            footerBar.BorderThickness = new Thickness(0, 1, 0, 0);
            footerBar.Padding = new Thickness(10, 0, 10, 0);
            footerBar.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel utilDock = new DockPanel();
            utilDock.VerticalAlignment = VerticalAlignment.Center;

            _sidebarCollapseBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.SidebarToggle, "折叠 / 展开左侧导航栏");
            _sidebarCollapseBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _sidebarAutoCollapsed = false; // 手动切换后不再自动展开 / 收起
                ToggleSidebarCollapsed();
            };
            DockPanel.SetDock(_sidebarCollapseBtn, Dock.Right);
            utilDock.Children.Add(_sidebarCollapseBtn);

            StackPanel leftUtils = new StackPanel();
            leftUtils.Orientation = Orientation.Horizontal;
            leftUtils.VerticalAlignment = VerticalAlignment.Center;

            _themeToggleBtn = ModernControlsFactory.CreateIconGhostButton(_isDark ? UiIcons.Sun : UiIcons.Moon, _isDark ? "切换至浅色主题 (Paper Zinc)" : "切换至深色主题 (Obsidian Zinc)");
            _themeToggleIcon = _themeToggleBtn.Content as Shapes.Path;
            _themeToggleBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                ToggleThemeFromUi();
            };
            leftUtils.Children.Add(_themeToggleBtn);

            _sidebarRefreshBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.Refresh, "立即增量扫描本地日志并刷新多端看板 (F5)");
            _sidebarRefreshBtn.Margin = new Thickness(3, 0, 0, 0);
            _sidebarRefreshBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                RunSilentScanAsync(_homeDir, true, true);
                StartExchangeRateSyncAsync(false);
            };
            leftUtils.Children.Add(_sidebarRefreshBtn);

            _sidebarHudBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.Capsule, "显示 / 隐藏桌面悬浮球 (Ctrl+M)");
            _sidebarHudBtn.Margin = new Thickness(3, 0, 0, 0);
            _sidebarHudBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                ToggleMiniHudWindow();
            };
            leftUtils.Children.Add(_sidebarHudBtn);

            _sidebarHelpBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.Help, "快捷键速查与跨设备 GitHub 令牌原理解析 (F1)");
            _sidebarHelpBtn.Margin = new Thickness(3, 0, 0, 0);
            _sidebarHelpBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenShortcutAndGuideDialog(0);
            };
            leftUtils.Children.Add(_sidebarHelpBtn);

            utilDock.Children.Add(leftUtils);
            footerBar.Child = utilDock;
            Grid.SetRow(footerBar, 3);
            g.Children.Add(footerBar);

            _sidebarBorder.Child = g;
            return _sidebarBorder;
        }

        private Border BuildNavRow(
            string icon,
            string label,
            string tooltip,
            bool isChild,
            Action onClick,
            out Border activeBar,
            out TextBlock labelText,
            out Grid rowGrid)
        {
            Border itemBd = new Border();
            itemBd.Height = isChild ? 28.0 : 31.0;
            itemBd.CornerRadius = new CornerRadius(6);
            itemBd.Margin = new Thickness(0, 0, 0, isChild ? 1.0 : 2.0);
            itemBd.Background = Brushes.Transparent;
            itemBd.Cursor = Cursors.Hand;
            itemBd.ToolTip = tooltip;
            itemBd.Tag = false;
            itemBd.SetResourceReference(TextElement.ForegroundProperty, "FgSecondary");

            rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            activeBar = new Border();
            activeBar.Width = 3;
            activeBar.Height = isChild ? 13 : 15;
            activeBar.CornerRadius = new CornerRadius(1.5);
            activeBar.VerticalAlignment = VerticalAlignment.Center;
            activeBar.Visibility = Visibility.Hidden;
            activeBar.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
            Grid.SetColumn(activeBar, 0);
            rowGrid.Children.Add(activeBar);

            if (!string.IsNullOrEmpty(icon))
            {
                Shapes.Path iconEl = UiIcons.CreateIcon(icon, 14.5, null);
                iconEl.HorizontalAlignment = HorizontalAlignment.Center;
                iconEl.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(iconEl, 1);
                rowGrid.Children.Add(iconEl);
            }

            labelText = new TextBlock();
            labelText.Text = label;
            labelText.FontSize = isChild ? DesignSystem.TextBody : DesignSystem.TextLabel;
            labelText.FontWeight = isChild ? FontWeights.Normal : FontWeights.Medium;
            labelText.VerticalAlignment = VerticalAlignment.Center;
            labelText.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(labelText, 2);
            rowGrid.Children.Add(labelText);

            itemBd.Child = rowGrid;

            Border captured = itemBd;
            itemBd.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (!IsNavRowHighlighted(captured))
                    captured.SetResourceReference(Border.BackgroundProperty, "BgSidebarHover");
            };
            itemBd.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                if (!IsNavRowHighlighted(captured))
                    captured.Background = Brushes.Transparent;
            };
            itemBd.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (onClick != null) onClick();
            };
            return itemBd;
        }

        private static bool IsNavRowHighlighted(Border row)
        {
            return row != null && row.Tag is bool && (bool)row.Tag;
        }

        private static void MakeSidebarStatusLink(TextBlock tb, Action onClick)
        {
            tb.Cursor = Cursors.Hand;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tb.MouseEnter += delegate(object s, MouseEventArgs e) { tb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary"); };
            tb.MouseLeave += delegate(object s, MouseEventArgs e) { tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted"); };
            tb.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { if (onClick != null) onClick(); };
        }

        // 窗口窄于此宽度时侧栏自动收成图标，加宽后自动恢复（用户手动收起的不自动展开）
        private const double SidebarAutoCollapseWidth = 1100.0;
        private bool _sidebarAutoCollapsed = false;

        private void ApplyResponsiveSidebar(double windowWidth)
        {
            if (windowWidth <= 0 || _sidebarBorder == null) return;
            if (windowWidth < SidebarAutoCollapseWidth && !_sidebarCollapsed)
            {
                ToggleSidebarCollapsed();
                _sidebarAutoCollapsed = true;
            }
            else if (windowWidth >= SidebarAutoCollapseWidth && _sidebarCollapsed && _sidebarAutoCollapsed)
            {
                ToggleSidebarCollapsed();
                _sidebarAutoCollapsed = false;
            }
        }

        private void ToggleSidebarCollapsed()
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            double targetW = _sidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;
            _sidebarColDef.Width = new GridLength(targetW);
            _sidebarBorder.Width = targetW;

            Visibility expandedVis = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            if (_brandTextPanel != null) _brandTextPanel.Visibility = expandedVis;
            if (_sidebarDataPanel != null) _sidebarDataPanel.Visibility = expandedVis;
            if (_sidebarRefreshBtn != null) _sidebarRefreshBtn.Visibility = expandedVis;
            if (_sidebarHudBtn != null) _sidebarHudBtn.Visibility = expandedVis;
            if (_sidebarHelpBtn != null) _sidebarHelpBtn.Visibility = expandedVis;
            if (_themeToggleBtn != null) _themeToggleBtn.Visibility = expandedVis;
            if (_navDeviceCountBadge != null) _navDeviceCountBadge.Visibility = expandedVis;

            // 折叠后只剩图标，子页面没有图标可显示，收起；当前所在分组改由顶层图标高亮。
            if (_navChildPanels != null)
            {
                for (int i = 0; i < _navChildPanels.Length; i++)
                {
                    if (_navChildPanels[i] != null)
                        _navChildPanels[i].Visibility = expandedVis;
                }
            }

            if (_navTitleLabels != null)
            {
                for (int i = 0; i < _navTitleLabels.Length; i++)
                {
                    if (_navTitleLabels[i] != null)
                        _navTitleLabels[i].Visibility = expandedVis;
                }
            }

            UpdateNavVisuals();
        }

        // -------------------- 顶栏：只放页面标题、搜索 / 跳转与同步状态；筛选和导出移到页面工具条 --------------------
        private UIElement BuildTopContextBar()
        {
            _topContextBarBorder = new Border();
            _topContextBarBorder.Height = TopBarLockedHeight;
            _topContextBarBorder.MinHeight = TopBarLockedHeight;
            _topContextBarBorder.MaxHeight = TopBarLockedHeight;
            _topContextBarBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            _topContextBarBorder.Padding = new Thickness(16, 0, 0, 0);
            _topContextBarBorder.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
            _topContextBarBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel topDock = new DockPanel();
            topDock.LastChildFill = true;

            // 最右侧：窗口控制按钮（最小化 / 最大化 / 关闭）
            StackPanel captionBtns = ModernWindowChrome.CreateCaptionButtons(this, true, out _maxCaptionBtn, out _maxCaptionPath);
            captionBtns.Margin = new Thickness(8, 0, 0, 0);
            // 关闭按钮的提示跟随「关闭时常驻托盘」设置
            Button closeCaptionBtn = captionBtns.Children.Count > 0 ? captionBtns.Children[captionBtns.Children.Count - 1] as Button : null;
            if (closeCaptionBtn != null)
            {
                closeCaptionBtn.ToolTip = "关闭";
                closeCaptionBtn.ToolTipOpening += delegate(object s, ToolTipEventArgs e)
                {
                    closeCaptionBtn.ToolTip = (_syncCfg != null && _syncCfg.MinimizeToTray && _trayIcon != null)
                        ? "关闭到托盘（后台继续运行）"
                        : "退出 TokLens";
                };
            }
            DockPanel.SetDock(captionBtns, Dock.Right);
            topDock.Children.Add(captionBtns);

            StackPanel rightGroup = new StackPanel();
            rightGroup.Orientation = Orientation.Horizontal;
            rightGroup.VerticalAlignment = VerticalAlignment.Center;
            WindowChrome.SetIsHitTestVisibleInChrome(rightGroup, true);

            // 搜索 / 跳转入口（Ctrl+K 命令面板）
            Button searchBtn = ModernControlsFactory.CreateButton("", "secondary");
            searchBtn.Height = 28;
            searchBtn.Width = 196;
            searchBtn.Padding = new Thickness(9, 0, 8, 0);
            searchBtn.ToolTip = "搜索页面、切换主题币种、导出报表或打开帮助 (Ctrl+K)";
            DockPanel searchDock = new DockPanel { Width = 177, LastChildFill = true };
            TextBlock searchKey = new TextBlock
            {
                Text = "Ctrl+K",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            searchKey.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(searchKey, Dock.Right);
            searchDock.Children.Add(searchKey);
            Shapes.Path searchIc = UiIcons.CreateIcon(UiIcons.Search, 12.5, "FgMuted");
            searchIc.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(searchIc, Dock.Left);
            searchDock.Children.Add(searchIc);
            TextBlock searchHint = new TextBlock
            {
                Text = "搜索或跳转…",
                FontSize = DesignSystem.TextBody,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            searchHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            searchDock.Children.Add(searchHint);
            searchBtn.Content = searchDock;
            searchBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenCommandPalette();
            };
            WindowChrome.SetIsHitTestVisibleInChrome(searchBtn, true);
            rightGroup.Children.Add(searchBtn);

            // 同步状态（点击进入同步设置）
            _syncStatusBtn = ModernControlsFactory.CreateButton("", "ghost");
            _syncStatusBtn.Height = 28;
            _syncStatusBtn.Padding = new Thickness(8, 0, 8, 0);
            _syncStatusBtn.Margin = new Thickness(6, 0, 0, 0);
            StackPanel syncSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _syncStatusIcon = UiIcons.CreateIcon(UiIcons.Cloud, 13.5, "FgMuted");
            _syncStatusIcon.Margin = new Thickness(0, 0, 6, 0);
            syncSp.Children.Add(_syncStatusIcon);
            _syncStatusText = new TextBlock { Text = "未开启同步", FontSize = DesignSystem.TextBody, VerticalAlignment = VerticalAlignment.Center };
            _syncStatusText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(_syncStatusText, false);
            syncSp.Children.Add(_syncStatusText);
            _syncStatusBtn.Content = syncSp;
            _syncStatusBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                SwitchActiveView(ViewSettings);
            };
            WindowChrome.SetIsHitTestVisibleInChrome(_syncStatusBtn, true);
            rightGroup.Children.Add(_syncStatusBtn);

            DockPanel.SetDock(rightGroup, Dock.Right);
            topDock.Children.Add(rightGroup);

            // 左侧：分组 / 页面标题 + 告警胶囊 + 可截断的说明文字（标题本身永不截断）
            DockPanel leftTitleDock = new DockPanel();
            leftTitleDock.LastChildFill = true;
            leftTitleDock.VerticalAlignment = VerticalAlignment.Center;
            leftTitleDock.Margin = new Thickness(0, 0, 12, 0);

            _viewGroupText = new TextBlock();
            _viewGroupText.FontSize = DesignSystem.TextTitle;
            _viewGroupText.Margin = new Thickness(0, 0, 6, 0);
            _viewGroupText.VerticalAlignment = VerticalAlignment.Center;
            _viewGroupText.Visibility = Visibility.Collapsed;
            _viewGroupText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(_viewGroupText, Dock.Left);
            leftTitleDock.Children.Add(_viewGroupText);

            _viewTitleText = new TextBlock();
            _viewTitleText.Text = ViewTitles[0];
            _viewTitleText.FontSize = DesignSystem.TextTitle;
            _viewTitleText.FontWeight = FontWeights.SemiBold;
            _viewTitleText.VerticalAlignment = VerticalAlignment.Center;
            _viewTitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            DockPanel.SetDock(_viewTitleText, Dock.Left);
            leftTitleDock.Children.Add(_viewTitleText);

            _inlineAlertBadge = new Border();
            _inlineAlertBadge.Height = 22;
            _inlineAlertBadge.CornerRadius = new CornerRadius(11);
            _inlineAlertBadge.BorderThickness = new Thickness(1);
            _inlineAlertBadge.Padding = new Thickness(8, 0, 8, 0);
            _inlineAlertBadge.Margin = new Thickness(10, 0, 0, 0);
            _inlineAlertBadge.VerticalAlignment = VerticalAlignment.Center;
            _inlineAlertBadge.Cursor = Cursors.Hand;
            _inlineAlertBadge.Visibility = Visibility.Collapsed;
            _inlineAlertBadge.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
            _inlineAlertBadge.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");
            WindowChrome.SetIsHitTestVisibleInChrome(_inlineAlertBadge, true);

            StackPanel alertSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Path alertIcon = UiIcons.CreateIcon(UiIcons.Alert, 12.0, "SemanticWarning");
            alertIcon.Margin = new Thickness(0, 0, 4, 0);
            alertSp.Children.Add(alertIcon);

            _inlineAlertText = new TextBlock();
            _inlineAlertText.Text = "告警";
            _inlineAlertText.FontSize = DesignSystem.TextCaption;
            _inlineAlertText.FontWeight = FontWeights.Medium;
            _inlineAlertText.VerticalAlignment = VerticalAlignment.Center;
            _inlineAlertText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
            ModernControlsFactory.EnableTabularNumbers(_inlineAlertText, false);
            alertSp.Children.Add(_inlineAlertText);

            _inlineAlertBadge.Child = alertSp;
            _inlineAlertBadge.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                OpenInlineAlertPopover(_inlineAlertBadge);
            };
            DockPanel.SetDock(_inlineAlertBadge, Dock.Left);
            leftTitleDock.Children.Add(_inlineAlertBadge);

            _viewSubContextText = new TextBlock();
            _viewSubContextText.Text = "";
            _viewSubContextText.FontSize = DesignSystem.TextCaption;
            _viewSubContextText.Margin = new Thickness(10, 1, 0, 0);
            _viewSubContextText.TextTrimming = TextTrimming.CharacterEllipsis;
            _viewSubContextText.VerticalAlignment = VerticalAlignment.Center;
            _viewSubContextText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(_viewSubContextText, false);
            leftTitleDock.Children.Add(_viewSubContextText);

            topDock.Children.Add(leftTitleDock);
            _topContextBarBorder.Child = topDock;
            return _topContextBarBorder;
        }

        // -------------------- 页面工具条：设备 / 时间范围 / 币种 / 导出，只在受这些筛选影响的页面出现 --------------------
        private UIElement BuildPageToolbar()
        {
            _pageToolbar = new DockPanel();
            _pageToolbar.Margin = new Thickness(16, 10, 16, 0);
            _pageToolbar.LastChildFill = true;

            // 右侧：导出
            _exportMenuBtn = ModernControlsFactory.CreateButton("", "secondary");
            _exportMenuBtn.Height = 28;
            _exportMenuBtn.Padding = new Thickness(9, 0, 8, 0);
            _exportMenuBtn.ToolTip = "按当前设备与时间范围导出 HTML 看板、Markdown、CSV 或 JSON 快照";

            StackPanel expSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Path expIcon = UiIcons.CreateIcon(UiIcons.Download, 13.5, null);
            expIcon.Margin = new Thickness(0, 0, 5, 0);
            expSp.Children.Add(expIcon);
            expSp.Children.Add(new TextBlock { Text = "导出", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
            Shapes.Path expChev = UiIcons.CreateIcon(UiIcons.ChevronDown, 11.5, "FgMuted");
            expChev.Margin = new Thickness(4, 1, 0, 0);
            expSp.Children.Add(expChev);

            _exportMenuBtn.Content = expSp;
            _exportMenuBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenExportMenuPopover(_exportMenuBtn);
            };
            DockPanel.SetDock(_exportMenuBtn, Dock.Right);
            _pageToolbar.Children.Add(_exportMenuBtn);

            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 1) 设备范围选择器 [全部设备 (N) ▾]
            _deviceSelectorBtn = ModernControlsFactory.CreateButton("", "secondary");
            _deviceSelectorBtn.Height = 28;
            _deviceSelectorBtn.Padding = new Thickness(9, 0, 8, 0);
            _deviceSelectorBtn.Margin = new Thickness(0, 0, 8, 0);
            _deviceSelectorBtn.ToolTip = "查看全部设备或单台电脑 / Linux VPS，管理设备备注与离线缓存";

            StackPanel devBtnSp = new StackPanel();
            devBtnSp.Orientation = Orientation.Horizontal;
            devBtnSp.VerticalAlignment = VerticalAlignment.Center;

            _deviceSelectorDot = new Shapes.Ellipse();
            _deviceSelectorDot.Width = 6;
            _deviceSelectorDot.Height = 6;
            _deviceSelectorDot.Margin = new Thickness(0, 0, 6, 0);
            _deviceSelectorDot.VerticalAlignment = VerticalAlignment.Center;
            _deviceSelectorDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticSuccess");
            devBtnSp.Children.Add(_deviceSelectorDot);

            _deviceSelectorText = new TextBlock();
            _deviceSelectorText.Text = "全部设备 (1)";
            _deviceSelectorText.FontSize = DesignSystem.TextBody;
            _deviceSelectorText.FontWeight = FontWeights.Medium;
            _deviceSelectorText.MaxWidth = 148;
            _deviceSelectorText.TextTrimming = TextTrimming.CharacterEllipsis;
            _deviceSelectorText.VerticalAlignment = VerticalAlignment.Center;
            ModernControlsFactory.EnableTabularNumbers(_deviceSelectorText, false);
            devBtnSp.Children.Add(_deviceSelectorText);

            Shapes.Path devChev = UiIcons.CreateIcon(UiIcons.ChevronDown, 12.0, "FgMuted");
            devChev.Margin = new Thickness(5, 1, 0, 0);
            devChev.VerticalAlignment = VerticalAlignment.Center;
            devBtnSp.Children.Add(devChev);

            _deviceSelectorBtn.Content = devBtnSp;
            _deviceSelectorBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenDeviceSelectorPopover(_deviceSelectorBtn);
            };
            left.Children.Add(_deviceSelectorBtn);

            // 2) 时间范围 [今日 | 7天 | 30天 | 全部 | 自定义…]
            _rangeContainer = ModernControlsFactory.CreateSegmentedContainer();
            _rangeContainer.Margin = new Thickness(0, 0, 8, 0);
            StackPanel rangeSp = (StackPanel)_rangeContainer.Child;

            string[] rLabels = new string[] { "今日", "7天", "30天", "全部", "自定义…" };
            _rangeBtns = new Button[rLabels.Length];
            for (int i = 0; i < rLabels.Length; i++)
            {
                int idx = i;
                Button rb = ModernControlsFactory.CreateSegmentedItem(rLabels[i]);
                rb.Padding = new Thickness(8, 0, 8, 0);
                if (idx < PresetRangeDays.Length)
                {
                    rb.Click += delegate(object s, RoutedEventArgs e)
                    {
                        _rangeDays = PresetRangeDays[idx];
                        _selectedDate = null;
                        UpdateRangeSegmentVisuals();
                        RefreshDashboardView();
                    };
                }
                else
                {
                    rb.ToolTip = "查看最近 N 天（1~3650）";
                    rb.Click += delegate(object s, RoutedEventArgs e)
                    {
                        OpenCustomRangePopover(_rangeBtns[idx]);
                    };
                }
                _rangeBtns[i] = rb;
                rangeSp.Children.Add(rb);
            }
            left.Children.Add(_rangeContainer);

            // 3) 币种 [¥ | $]
            Border curContainer = ModernControlsFactory.CreateSegmentedContainer();
            curContainer.Margin = new Thickness(0, 0, 8, 0);
            curContainer.ToolTip = "金额显示币种（人民币 / 美元）";
            StackPanel curSp = (StackPanel)curContainer.Child;

            _currencyBtns = new Button[2];
            string[] curSyms = new string[] { "¥", "$" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button cb = ModernControlsFactory.CreateSegmentedItem(curSyms[i]);
                cb.Padding = new Thickness(8, 0, 8, 0);
                cb.FontFamily = DesignSystem.FontMono;
                cb.Click += delegate(object s, RoutedEventArgs e)
                {
                    bool preferCny = (idx == 0);
                    if (_isCny == preferCny) return;
                    _isCny = preferCny;
                    if (_syncCfg != null)
                    {
                        _syncCfg.PreferCny = _isCny;
                        PersistConfig(_syncCfg);
                    }
                    ModernControlsFactory.UpdateSegmentedGroup(_currencyBtns, _isCny ? 0 : 1);
                    RefreshDashboardView();
                    if (_settingsView != null)
                        _settingsView.ReloadFromConfig();
                };
                _currencyBtns[i] = cb;
                curSp.Children.Add(cb);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_currencyBtns, _isCny ? 0 : 1);
            left.Children.Add(curContainer);

            // 4) 不受全局筛选影响的页面，在这里说明它自己的数据范围
            _pageScopeText = new TextBlock();
            _pageScopeText.FontSize = DesignSystem.TextCaption;
            _pageScopeText.Margin = new Thickness(4, 0, 0, 0);
            _pageScopeText.VerticalAlignment = VerticalAlignment.Center;
            _pageScopeText.TextTrimming = TextTrimming.CharacterEllipsis;
            _pageScopeText.Visibility = Visibility.Collapsed;
            _pageScopeText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            left.Children.Add(_pageScopeText);

            _pageToolbar.Children.Add(left);
            UpdateRangeSegmentVisuals();
            return _pageToolbar;
        }

        private void UpdateRangeSegmentVisuals()
        {
            if (_rangeBtns == null || _rangeBtns.Length == 0) return;
            int idx = FindPresetRangeIndex(_rangeDays);
            Button customBtn = _rangeBtns[_rangeBtns.Length - 1];
            if (idx < 0)
            {
                idx = _rangeBtns.Length - 1;
                customBtn.Content = string.Format(CultureInfo.InvariantCulture, "近 {0} 天", _rangeDays);
            }
            else
            {
                customBtn.Content = "自定义…";
            }
            ModernControlsFactory.UpdateSegmentedGroup(_rangeBtns, idx);
        }

        private void ApplyPageToolbarScope()
        {
            if (_pageToolbar == null) return;
            int v = _activeView;
            bool settingsGroup = (v == ViewSettings || v == ViewDoctor);
            _pageToolbar.Visibility = settingsGroup ? Visibility.Collapsed : Visibility.Visible;
            if (settingsGroup) return;

            // 项目页读取本机工作区全部历史；订阅按账户记实付，与设备和时间范围都无关。它们不跟随顶部筛选，
            // 所以不显示时间范围和导出（导出内容按时间筛选生成，放在这里会让人误以为导出的是本页数据）。
            bool ownScope = (v == ViewProjects || v == ViewInsights);
            _deviceSelectorBtn.Visibility = ownScope ? Visibility.Collapsed : Visibility.Visible;
            _rangeContainer.Visibility = ownScope ? Visibility.Collapsed : Visibility.Visible;
            _exportMenuBtn.Visibility = ownScope ? Visibility.Collapsed : Visibility.Visible;

            string scope = "";
            if (v == ViewProjects) scope = "范围：本机工作区 · 全部历史";
            _pageScopeText.Text = scope;
            _pageScopeText.Visibility = string.IsNullOrEmpty(scope) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OpenCustomRangePopover(UIElement target)
        {
            StackPanel sp = new StackPanel { Width = 232 };
            Popup pop = null;

            TextBlock title = new TextBlock { Text = "最近 N 天", FontSize = DesignSystem.TextLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(title);

            DockPanel row = new DockPanel();
            Button applyBtn = ModernControlsFactory.CreateButton("应用", "secondary");
            applyBtn.Height = 28;
            applyBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(applyBtn, Dock.Right);
            row.Children.Add(applyBtn);

            TextBlock unit = new TextBlock { Text = "天", FontSize = DesignSystem.TextBody, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            unit.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            DockPanel.SetDock(unit, Dock.Right);
            row.Children.Add(unit);

            bool isCustom = FindPresetRangeIndex(_rangeDays) < 0;
            TextBox daysBox = ModernControlsFactory.CreateTextInput(isCustom ? _rangeDays.ToString(CultureInfo.InvariantCulture) : "", 28.0);
            daysBox.FontFamily = DesignSystem.FontMono;
            row.Children.Add(daysBox);
            sp.Children.Add(row);

            TextBlock hint = new TextBlock { Text = "输入 1~3650 之间的整数", FontSize = DesignSystem.TextCaption, Margin = new Thickness(0, 6, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(hint);

            Action apply = delegate()
            {
                int d;
                string txt = (daysBox.Text ?? "").Trim();
                if (!int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out d) || d < 1 || d > 3650)
                {
                    hint.Text = "请输入 1~3650 之间的整数";
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
                    return;
                }
                _rangeDays = d;
                _selectedDate = null;
                if (pop != null) pop.IsOpen = false;
                UpdateRangeSegmentVisuals();
                RefreshDashboardView();
            };
            applyBtn.Click += delegate(object s, RoutedEventArgs e) { apply(); };
            daysBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; apply(); }
            };
            daysBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                hint.Text = "输入 1~3650 之间的整数";
                hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            };

            pop = ModernControlsFactory.CreateModernPopover(target, sp);
            pop.IsOpen = true;
            daysBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate() { daysBox.Focus(); daysBox.SelectAll(); }));
        }

        private void OpenFxRatePopover(UIElement target)
        {
            StackPanel sp = new StackPanel { Width = 268 };
            Popup pop = null;

            TextBlock title = new TextBlock { Text = "USD / CNY 汇率", FontSize = DesignSystem.TextLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(title);

            TextBlock src = new TextBlock { FontSize = DesignSystem.TextCaption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            src.Text = "来源：" + _fxSource + (string.IsNullOrEmpty(_fxUpdatedAt) ? "" : (" · 更新于 " + _fxUpdatedAt));
            src.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(src);

            DockPanel row = new DockPanel();
            Button syncBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "同步实时汇率", "secondary");
            syncBtn.Height = 28;
            syncBtn.Margin = new Thickness(8, 0, 0, 0);
            syncBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                StartExchangeRateSyncAsync(true);
            };
            DockPanel.SetDock(syncBtn, Dock.Right);
            row.Children.Add(syncBtn);

            _rateInput = ModernControlsFactory.CreateTextInput(_usdToCny.ToString("0.####", CultureInfo.InvariantCulture), 28.0);
            _rateInput.FontFamily = DesignSystem.FontMono;
            _rateInput.HorizontalContentAlignment = HorizontalAlignment.Right;
            _rateInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (_suppressRateEvent || _rateInput == null) return;
                double nr;
                if (double.TryParse(_rateInput.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out nr) && nr > 0.1 && nr < 200.0)
                {
                    _usdToCny = nr;
                    _fxSource = "手动输入";
                    UpdateFxUiState(false, false);
                    RefreshDashboardView();
                }
            };
            row.Children.Add(_rateInput);
            sp.Children.Add(row);

            TextBlock hint = new TextBlock { Text = "手动输入立即生效；同步实时汇率会覆盖手动值。", FontSize = DesignSystem.TextCaption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(hint);

            pop = ModernControlsFactory.CreateModernPopover(target, sp);
            pop.Closed += delegate(object s, EventArgs e) { _rateInput = null; };
            pop.IsOpen = true;
        }

        private void UpdateSidebarWatchText()
        {
            if (_sidebarWatchText == null) return;
            int files = _scanResult != null ? _scanResult.TotalFilesTracked : 0;
            _sidebarWatchText.Text = files > 0
                ? string.Format(CultureInfo.InvariantCulture, "实时监听 · {0:N0} 文件", files)
                : "实时监听就绪";
            if (_scanResult != null)
            {
                _sidebarWatchText.ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "最近一次增量扫描 {0:F2}s · 跟踪 {1:N0} 个文件（点击查看归档保护详情）",
                    _scanResult.ElapsedSeconds,
                    files);
            }
        }

        // -------------------- 四个现代无阻塞 Popover 浮层（零 MessageBox！） --------------------
        private void OpenArchiveStatusPopover(UIElement target)
        {
            StackPanel sp = new StackPanel();
            sp.Width = 340;

            StackPanel hdr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            Shapes.Path shIcon = UiIcons.CreateIcon(UiIcons.Shield, 15.0, "SemanticSuccess");
            shIcon.Margin = new Thickness(0, 0, 6, 0);
            hdr.Children.Add(shIcon);
            TextBlock hdrTxt = new TextBlock
            {
                Text = "双层防清理高水位归档保护已启用",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            hdrTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            hdr.Children.Add(hdrTxt);
            sp.Children.Add(hdr);

            string dbPath = LocalUsageStore.DbFilePath;
            int activeFiles = _scanResult != null ? _scanResult.TotalFilesTracked : 0;
            int archivedFiles = _scanResult != null ? _scanResult.ArchivedFilesCount : 0;

            TextBlock body = new TextBlock();
            body.FontSize = DesignSystem.TextBody;
            body.LineHeight = 18;
            body.TextWrapping = TextWrapping.Wrap;
            body.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            body.Text = string.Format(
                CultureInfo.InvariantCulture,
                "1. 本机 SQLite 增量高水位库\n   • 路径：{0}\n   • 实时监听活跃目录：{1} 个  |  跟踪文件：{2:N0} 个\n   • 已保护被清理历史文件：{3:N0} 个\n\n2. 多端 Gist / WebDAV 高水位合并\n   • 即使清空工具对话缓存或重装系统，历史已入库账单均永久保留不归零。",
                string.IsNullOrEmpty(dbPath) ? "%APPDATA%\\TokLens\\toklens.db" : dbPath,
                _watchers.Count,
                activeFiles,
                archivedFiles);
            sp.Children.Add(body);

            Popup pop = ModernControlsFactory.CreateModernPopover(target, sp);

            StackPanel btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Button openFolderBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Folder, "打开归档目录", "secondary");
            openFolderBtn.Height = 26;
            openFolderBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                pop.IsOpen = false;
                OpenInExplorerSelect(string.IsNullOrEmpty(dbPath) ? SyncService.ConfigFilePath : dbPath);
            };
            btnRow.Children.Add(openFolderBtn);
            sp.Children.Add(btnRow);

            pop.IsOpen = true;
        }

        private void OpenInlineAlertPopover(UIElement target)
        {
            StackPanel sp = new StackPanel();
            sp.Width = 360;

            TextBlock title = new TextBlock
            {
                Text = "目录扫描诊断",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(title);

            Popup pop = null;

            if (_scanResult != null && _scanResult.Warnings != null && _scanResult.Warnings.Count > 0)
            {
                ScrollViewer sv = new ScrollViewer { MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                StackPanel warnList = new StackPanel();
                for (int i = 0; i < _scanResult.Warnings.Count; i++)
                {
                    TextBlock wt = new TextBlock
                    {
                        Text = "• " + _scanResult.Warnings[i],
                        FontSize = DesignSystem.TextCaption,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    wt.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                    warnList.Children.Add(wt);
                }
                sv.Content = warnList;
                sp.Children.Add(sv);
            }

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            Button doctorBtn = ModernControlsFactory.CreateButton("前往环境检查", "secondary");
            doctorBtn.Height = 26;
            doctorBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                SwitchActiveView(ViewDoctor);
            };
            actions.Children.Add(doctorBtn);
            sp.Children.Add(actions);

            pop = ModernControlsFactory.CreateModernPopover(target, sp);
            pop.IsOpen = true;
        }

        private void OpenDeviceSelectorPopover(UIElement target)
        {
            StackPanel rootSp = new StackPanel();
            rootSp.Width = 336;

            Popup pop = null;

            TextBlock hdr = new TextBlock
            {
                Text = "选择设备账单视角",
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 6, 6)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            rootSp.Children.Add(hdr);

            // 全部设备汇总行
            double allUsd = 0.0;
            for (int i = 0; i < _latestMachineViews.Count; i++)
                allUsd += _latestMachineViews[i].CostUsd;

            Border allItem = BuildPopoverDeviceItem(
                null,
                string.Format(CultureInfo.InvariantCulture, "全部设备汇总 ({0} 台)", Math.Max(1, _latestMachineViews.Count)),
                "所有设备合计",
                FormatterUtil.FormatMoney(allUsd, _isCny, _usdToCny),
                true,
                true,
                _selectedMachineId == null,
                delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    _selectedMachineId = null;
                    RefreshDashboardView();
                },
                null,
                null);
            rootSp.Children.Add(allItem);

            Border sep1 = new Border { Height = 1, Margin = new Thickness(4, 5, 4, 5) };
            sep1.SetResourceReference(Border.BackgroundProperty, "BorderSubtle");
            rootSp.Children.Add(sep1);

            ScrollViewer listSv = new ScrollViewer();
            listSv.MaxHeight = 260;
            listSv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            StackPanel nodeStack = new StackPanel();
            for (int i = 0; i < _latestMachineViews.Count; i++)
            {
                MachineRowView mv = _latestMachineViews[i];
                bool isSel = _selectedMachineId != null && string.Equals(_selectedMachineId, mv.MachineId, StringComparison.OrdinalIgnoreCase);

                Action onSelect = delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    _selectedMachineId = mv.MachineId;
                    RefreshDashboardView();
                };

                Action onAlias = delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    OpenDeviceAliasPopover(target, mv);
                };

                Action onRemove = null;
                if (!mv.IsLocal)
                {
                    onRemove = delegate()
                    {
                        if (pop != null) pop.IsOpen = false;
                        RemoveOfflineRemoteNodeCache(mv);
                    };
                }

                Border rowBd = BuildPopoverDeviceItem(
                    mv.MachineId,
                    mv.MachineName,
                    mv.IsLocal ? "本机实时" : mv.StatusText,
                    mv.FormattedMoney,
                    mv.IsOnline,
                    mv.IsLocal,
                    isSel,
                    onSelect,
                    onAlias,
                    onRemove);
                nodeStack.Children.Add(rowBd);
            }
            listSv.Content = nodeStack;
            rootSp.Children.Add(listSv);

            Border sep2 = new Border { Height = 1, Margin = new Thickness(4, 6, 4, 6) };
            sep2.SetResourceReference(Border.BackgroundProperty, "BorderSubtle");
            rootSp.Children.Add(sep2);

            Button addDevBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Plus, "接入新电脑 / Linux VPS", "ghost");
            addDevBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            addDevBtn.Height = 30;
            addDevBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                SwitchActiveView(ViewSettings);
                if (_settingsView != null)
                    _settingsView.ScrollToSection(1);
            };
            rootSp.Children.Add(addDevBtn);

            pop = ModernControlsFactory.CreateModernPopover(target, rootSp);
            pop.IsOpen = true;
        }

        private Border BuildPopoverDeviceItem(
            string machineId,
            string title,
            string subBadge,
            string moneyText,
            bool isOnline,
            bool isLocal,
            bool isSelected,
            Action onSelect,
            Action onEditAlias,
            Action onRemoveCache)
        {
            Border bd = new Border();
            bd.CornerRadius = new CornerRadius(6);
            bd.Padding = new Thickness(8, 6, 8, 6);
            bd.Margin = new Thickness(0, 0, 0, 2);
            bd.Cursor = Cursors.Hand;
            if (isSelected)
                bd.SetResourceReference(Border.BackgroundProperty, "BgSelected");
            else
                bd.Background = Brushes.Transparent;

            bd.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (!isSelected) bd.SetResourceReference(Border.BackgroundProperty, "BgHover");
            };
            bd.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                if (!isSelected) bd.Background = Brushes.Transparent;
            };

            DockPanel dock = new DockPanel();

            StackPanel rightSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock moneyTb = new TextBlock
            {
                Text = moneyText,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0)
            };
            moneyTb.SetResourceReference(TextBlock.ForegroundProperty, isSelected ? "AccentText" : "FgPrimary");
            rightSp.Children.Add(moneyTb);

            if (onEditAlias != null)
            {
                Button aliasBtn = ModernControlsFactory.CreateButton("备注", "ghost");
                aliasBtn.Height = 22;
                aliasBtn.FontSize = DesignSystem.TextCaption;
                aliasBtn.Padding = new Thickness(5, 0, 5, 0);
                aliasBtn.ToolTip = "设置该设备在本机的备注显示名";
                aliasBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    e.Handled = true;
                    onEditAlias();
                };
                rightSp.Children.Add(aliasBtn);
            }

            if (onRemoveCache != null && !isLocal)
            {
                Button delBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.Trash, "从本地移除该远端设备的缓存快照");
                delBtn.Width = 22;
                delBtn.Height = 22;
                delBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    e.Handled = true;
                    onRemoveCache();
                };
                rightSp.Children.Add(delBtn);
            }

            DockPanel.SetDock(rightSp, Dock.Right);
            dock.Children.Add(rightSp);

            StackPanel leftSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Ellipse dot = new Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shapes.Shape.FillProperty, isOnline ? "SemanticSuccess" : "SemanticWarning");
            leftSp.Children.Add(dot);

            StackPanel textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock nameTb = new TextBlock
            {
                Text = title,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                MaxWidth = 145,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            textCol.Children.Add(nameTb);

            TextBlock subTb = new TextBlock
            {
                Text = subBadge,
                FontSize = DesignSystem.TextCaption
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, isOnline ? "FgMuted" : "SemanticWarning");
            textCol.Children.Add(subTb);

            leftSp.Children.Add(textCol);
            dock.Children.Add(leftSp);

            bd.Child = dock;
            bd.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (onSelect != null) onSelect();
            };
            return bd;
        }

        private void OpenDeviceAliasPopover(UIElement anchor, MachineRowView targetMachine)
        {
            if (targetMachine == null || string.IsNullOrEmpty(targetMachine.MachineId))
                return;

            StackPanel sp = new StackPanel { Width = 280 };
            Popup pop = null;

            TextBlock hdr = new TextBlock
            {
                Text = "设置设备备注名称",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(hdr);

            TextBlock sub = new TextBlock
            {
                Text = "节点 ID: " + targetMachine.MachineId + "（留空保存可恢复原始主机名）",
                FontSize = DesignSystem.TextCaption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(sub);

            string existingAlias = "";
            if (_syncCfg != null && _syncCfg.NodeAliases != null)
                _syncCfg.NodeAliases.TryGetValue(targetMachine.MachineId, out existingAlias);

            TextBox input = ModernControlsFactory.CreateTextInput(existingAlias ?? targetMachine.MachineName, 30.0);
            sp.Children.Add(input);

            StackPanel btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Button cancelBtn = ModernControlsFactory.CreateButton("取消", "ghost");
            cancelBtn.Height = 28;
            cancelBtn.Margin = new Thickness(0, 0, 6, 0);
            cancelBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
            };
            btnRow.Children.Add(cancelBtn);

            Button saveBtn = ModernControlsFactory.CreateButton("保存备注", "primary");
            saveBtn.Height = 28;
            saveBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                SaveMachineAlias(targetMachine.MachineId, input.Text);
            };
            btnRow.Children.Add(saveBtn);
            sp.Children.Add(btnRow);

            pop = ModernControlsFactory.CreateModernPopover(anchor, sp);
            pop.IsOpen = true;
            input.Focus();
            input.SelectAll();
        }

        private void SaveMachineAlias(string machineId, string rawAlias)
        {
            if (_syncCfg == null || string.IsNullOrEmpty(machineId)) return;
            if (_syncCfg.NodeAliases == null)
                _syncCfg.NodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = (rawAlias ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
                _syncCfg.NodeAliases.Remove(machineId);
            else
                _syncCfg.NodeAliases[machineId] = trimmed;

            PersistConfig(_syncCfg);

            // 重新加载快照原始名称并应用新别名
            if (_scanResult != null)
            {
                _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);
                Program.ApplyNodeAliasToSnapshot(_localNode, _syncCfg);
            }
            _remoteNodes = LoadAndRecalculateRemoteNodes(_syncCfg.MachineId, _homeDir, _syncCfg);
            RefreshDashboardView();
            ShowToastSafe("设备备注已更新", string.IsNullOrEmpty(trimmed) ? "已恢复原始设备名称" : ("显示名称已设为: " + trimmed), "success");
        }

        private void RemoveOfflineRemoteNodeCache(MachineRowView mv)
        {
            if (mv == null || mv.IsLocal || string.IsNullOrEmpty(mv.MachineId))
                return;

            SyncService.RemoveCachedNode(mv.MachineId);
            if (_remoteNodes != null)
            {
                for (int i = _remoteNodes.Count - 1; i >= 0; i--)
                {
                    if (_remoteNodes[i] != null && string.Equals(_remoteNodes[i].MachineId, mv.MachineId, StringComparison.OrdinalIgnoreCase))
                        _remoteNodes.RemoveAt(i);
                }
            }
            if (_selectedMachineId != null && string.Equals(_selectedMachineId, mv.MachineId, StringComparison.OrdinalIgnoreCase))
                _selectedMachineId = null;

            RefreshDashboardView();
            ShowToastSafe("已移除本地节点缓存", "设备 " + mv.MachineName + " 的本地缓存快照已清理", "info");
        }

        private void OpenExportMenuPopover(UIElement target)
        {
            StackPanel sp = new StackPanel { Width = 310 };
            Popup pop = null;

            sp.Children.Add(MakePopoverActionRow(
                UiIcons.ExternalLink,
                "网页看板 (.html)",
                "单文件离线网页，导出后自动打开 (Ctrl+E)",
                delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    ExportAndOpenHtmlReport();
                }));

            sp.Children.Add(MakePopoverActionRow(
                UiIcons.Sparkles,
                "Markdown 报告 (.md)",
                "表格与趋势摘要，适合放进周报或 GitHub",
                delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    ExportMarkdownWithDialog();
                }));

            sp.Children.Add(MakePopoverActionRow(
                UiIcons.Download,
                "对账明细 (.csv)",
                "按日期、设备、工具和模型逐行 (Ctrl+S)",
                delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    ExportCsvWithDialog();
                }));

            sp.Children.Add(MakePopoverActionRow(
                UiIcons.Shield,
                "汇总快照 (.json)",
                "只含聚合用量与费用，不含对话内容",
                delegate()
                {
                    if (pop != null) pop.IsOpen = false;
                    ExportJsonWithDialog();
                }));

            pop = ModernControlsFactory.CreateModernPopover(target, sp);
            pop.IsOpen = true;
        }

        private Border MakePopoverActionRow(string iconData, string title, string subtitle, Action onClick)
        {
            Border bd = new Border();
            bd.CornerRadius = new CornerRadius(6);
            bd.Padding = new Thickness(9, 7, 9, 7);
            bd.Margin = new Thickness(0, 0, 0, 2);
            bd.Background = Brushes.Transparent;
            bd.Cursor = Cursors.Hand;

            bd.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                bd.SetResourceReference(Border.BackgroundProperty, "BgHover");
            };
            bd.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                bd.Background = Brushes.Transparent;
            };

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            Shapes.Path icon = UiIcons.CreateIcon(iconData, 14.5, "AccentText");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 9, 0);
            row.Children.Add(icon);

            StackPanel col = new StackPanel();
            TextBlock t1 = new TextBlock { Text = title, FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            col.Children.Add(t1);

            TextBlock t2 = new TextBlock { Text = subtitle, FontSize = DesignSystem.TextCaption, Margin = new Thickness(0, 1, 0, 0) };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            col.Children.Add(t2);

            row.Children.Add(col);
            bd.Child = row;

            bd.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (onClick != null) onClick();
            };
            return bd;
        }

        // =================================================================================================
        // 2. 六大独立全高视图构建（同格挂载于 _workspaceHostGrid，Visibility 零开销切换）
        // =================================================================================================

        // -------------------- 视图 0：概览看板 --------------------
        private UIElement BuildOverviewView()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(16, 12, 16, 14);
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110.0) }); // Row 0: 4 KPI 卡片（主数字 28px）
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(234.0) }); // Row 1: Bento Ranking + Smooth Trend Chart
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 2: Compact Live Stream Preview

            // Row 0: 顶部 4 张 Bento KPI 卡片（大数字统一 24px SemiBold FgPrimary + 等宽数字，告别彩虹红绿灯）
            UniformGrid kpiGrid = new UniformGrid { Columns = 4 };

            // KPI 1: 累计等效账单 + 今日实时微胶囊
            _kpiCardCost = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            _kpiCardCost.Margin = new Thickness(0, 0, 10, 0);
            StackPanel c1Sp = new StackPanel();

            DockPanel c1Head = new DockPanel();
            _kpiTodayPill = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 1, 6, 1),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            _kpiTodayPill.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _kpiTodayPill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            _kpiTodayPillText = new TextBlock { Text = "今日 —", FontSize = DesignSystem.TextCaption, FontFamily = DesignSystem.FontMono };
            _kpiTodayPillText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            _kpiTodayPill.Child = _kpiTodayPillText;
            DockPanel.SetDock(_kpiTodayPill, Dock.Right);
            c1Head.Children.Add(_kpiTodayPill);

            TextBlock c1Lbl = new TextBlock { Text = "累计等效账单", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            c1Lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c1Head.Children.Add(c1Lbl);
            c1Sp.Children.Add(c1Head);

            _kpiCostVal = CreateKpiHeroValue("¥0.00");
            c1Sp.Children.Add(_kpiCostVal);

            _kpiCostSub = CreateKpiSubText("0 次模型调用 · 悬停查看月度预测");
            c1Sp.Children.Add(_kpiCostSub);
            _kpiCardCost.Child = c1Sp;
            kpiGrid.Children.Add(_kpiCardCost);

            // KPI 2: Token 吞吐总量 + 4px 三色输入/缓存/输出构成微条
            Border kpiCardTok = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            kpiCardTok.Margin = new Thickness(0, 0, 10, 0);
            StackPanel c2Sp = new StackPanel();

            TextBlock c2Lbl = new TextBlock { Text = "Token 吞吐总量", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium };
            c2Lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c2Sp.Children.Add(c2Lbl);

            _kpiTokVal = CreateKpiHeroValue("0");
            c2Sp.Children.Add(_kpiTokVal);

            Border ratioStrip = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 4, 0, 3),
                ClipToBounds = true
            };
            ratioStrip.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
            Grid ratioCols = new Grid();
            _tokSegInputCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _tokSegCacheCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _tokSegOutputCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            ratioCols.ColumnDefinitions.Add(_tokSegInputCol);
            ratioCols.ColumnDefinitions.Add(_tokSegCacheCol);
            ratioCols.ColumnDefinitions.Add(_tokSegOutputCol);

            Border segIn = new Border();
            segIn.SetResourceReference(Border.BackgroundProperty, "ChartFresh");
            Grid.SetColumn(segIn, 0);
            ratioCols.Children.Add(segIn);

            Border segCr = new Border();
            segCr.SetResourceReference(Border.BackgroundProperty, "ChartCacheRead");
            Grid.SetColumn(segCr, 1);
            ratioCols.Children.Add(segCr);

            Border segOut = new Border();
            segOut.SetResourceReference(Border.BackgroundProperty, "ChartOutput");
            Grid.SetColumn(segOut, 2);
            ratioCols.Children.Add(segOut);

            ratioStrip.Child = ratioCols;
            c2Sp.Children.Add(ratioStrip);

            _kpiTokSub = CreateKpiSubText("新增 0 · 缓存读 0 · 输出 0");
            c2Sp.Children.Add(_kpiTokSub);
            kpiCardTok.Child = c2Sp;
            kpiGrid.Children.Add(kpiCardTok);

            // KPI 3: 上下文缓存命中率
            Border kpiCardHit = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            kpiCardHit.Margin = new Thickness(0, 0, 10, 0);
            StackPanel c3Sp = new StackPanel();

            TextBlock c3Lbl = new TextBlock { Text = "上下文缓存命中率", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium };
            c3Lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c3Sp.Children.Add(c3Lbl);

            _kpiHitVal = CreateKpiHeroValue("0.0%");
            c3Sp.Children.Add(_kpiHitVal);

            _kpiHitSub = CreateKpiSubText("缓存读取 0 / 总提示词 0");
            _kpiHitSub.Margin = new Thickness(0, 6, 0, 0);
            c3Sp.Children.Add(_kpiHitSub);
            kpiCardHit.Child = c3Sp;
            kpiGrid.Children.Add(kpiCardHit);

            // KPI 4: 缓存累计省下费用 + 右上角 SemanticSuccess 省钱比例胶囊
            Border kpiCardSave = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            StackPanel c4Sp = new StackPanel();

            DockPanel c4Head = new DockPanel();
            _kpiSavePctBadge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 1, 6, 1),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            _kpiSavePctBadge.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
            _kpiSavePctBadge.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
            _kpiSavePctText = new TextBlock { Text = "省 0%", FontSize = DesignSystem.TextCaption, FontWeight = FontWeights.SemiBold, FontFamily = DesignSystem.FontMono };
            _kpiSavePctText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            _kpiSavePctBadge.Child = _kpiSavePctText;
            DockPanel.SetDock(_kpiSavePctBadge, Dock.Right);
            c4Head.Children.Add(_kpiSavePctBadge);

            TextBlock c4Lbl = new TextBlock { Text = "缓存累计省下费用", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            c4Lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c4Head.Children.Add(c4Lbl);
            c4Sp.Children.Add(c4Head);

            _kpiSaveVal = CreateKpiHeroValue("¥0.00");
            c4Sp.Children.Add(_kpiSaveVal);

            _kpiSaveSub = CreateKpiSubText("较全价输入基准累计节省");
            _kpiSaveSub.Margin = new Thickness(0, 6, 0, 0);
            c4Sp.Children.Add(_kpiSaveSub);
            kpiCardSave.Child = c4Sp;
            kpiGrid.Children.Add(kpiCardSave);

            Grid.SetRow(kpiGrid, 0);
            g.Children.Add(kpiGrid);

            // Row 1: 中部 Bento 双栏（左侧工具/设备消耗排行 + 右侧平滑面积折线 / 圆角柱状趋势图）
            Grid midGrid = new Grid();
            midGrid.Margin = new Thickness(0, 10, 0, 10);
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });

            // 左卡：工具 / 设备消耗构成排行
            Border rankCard = ModernControlsFactory.CreateCard(new Thickness(13, 10, 13, 10));
            Grid rankGrid = new Grid();
            rankGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            rankGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            DockPanel rankHead = new DockPanel();
            Border leftModeBox = ModernControlsFactory.CreateSegmentedContainer();
            StackPanel leftModeSp = (StackPanel)leftModeBox.Child;
            _leftModeBtns = new Button[2];
            string[] lmNames = new string[] { "按工具", "按设备" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button b = ModernControlsFactory.CreateSegmentedItem(lmNames[i]);
                b.Padding = new Thickness(8, 0, 8, 0);
                b.Click += delegate(object s, RoutedEventArgs e)
                {
                    _leftRankByMachine = (idx == 1);
                    ModernControlsFactory.UpdateSegmentedGroup(_leftModeBtns, idx);
                    RefreshDashboardView();
                };
                _leftModeBtns[i] = b;
                leftModeSp.Children.Add(b);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_leftModeBtns, 0);
            DockPanel.SetDock(leftModeBox, Dock.Right);
            rankHead.Children.Add(leftModeBox);

            _clearFilterBtn = ModernControlsFactory.CreateButton("清除筛选 ×", "ghost");
            _clearFilterBtn.Height = 24;
            _clearFilterBtn.FontSize = DesignSystem.TextCaption;
            _clearFilterBtn.Padding = new Thickness(7, 0, 7, 0);
            _clearFilterBtn.Margin = new Thickness(0, 0, 6, 0);
            _clearFilterBtn.Visibility = Visibility.Collapsed;
            _clearFilterBtn.SetResourceReference(Control.ForegroundProperty, "AccentText");
            _clearFilterBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _selectedTool = null;
                _selectedMachineId = null;
                _selectedDate = null;
                RefreshDashboardView();
            };
            DockPanel.SetDock(_clearFilterBtn, Dock.Right);
            rankHead.Children.Add(_clearFilterBtn);

            TextBlock rankTitle = new TextBlock
            {
                Text = "消耗构成排行",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            rankTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            rankHead.Children.Add(rankTitle);

            Grid.SetRow(rankHead, 0);
            rankGrid.Children.Add(rankHead);

            ScrollViewer rankSv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 6, 0, 0)
            };
            _toolsStack = new StackPanel();
            rankSv.Content = _toolsStack;
            Grid.SetRow(rankSv, 1);
            rankGrid.Children.Add(rankSv);

            rankCard.Child = rankGrid;
            Grid.SetColumn(rankCard, 0);
            midGrid.Children.Add(rankCard);

            // 右卡：平滑面积折线 / 圆角细柱趋势图（带十字准星虚线 + 跟随式浮层详情卡 + 点击单日钻取过滤）
            Border chartCard = ModernControlsFactory.CreateCard(new Thickness(14, 10, 14, 10));
            Grid chartGrid = new Grid();
            chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            chartGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            DockPanel chartHead = new DockPanel();

            StackPanel chartRightTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Border metricBox = ModernControlsFactory.CreateSegmentedContainer();
            StackPanel metricSp = (StackPanel)metricBox.Child;
            _metricBtns = new Button[2];
            string[] mNames = new string[] { "金额", "Token" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button mb = ModernControlsFactory.CreateSegmentedItem(mNames[i]);
                mb.Padding = new Thickness(8, 0, 8, 0);
                mb.Click += delegate(object s, RoutedEventArgs e)
                {
                    _rankByCost = (idx == 0);
                    ModernControlsFactory.UpdateSegmentedGroup(_metricBtns, idx);
                    RefreshDashboardView();
                };
                _metricBtns[i] = mb;
                metricSp.Children.Add(mb);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_metricBtns, 0);

            Border typeBox = ModernControlsFactory.CreateSegmentedContainer();
            typeBox.Margin = new Thickness(0, 0, 6, 0);
            StackPanel typeSp = (StackPanel)typeBox.Child;
            _chartTypeBtns = new Button[2];
            string[] ctNames = new string[] { "面积", "柱状" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button cb = ModernControlsFactory.CreateSegmentedItem(ctNames[i]);
                cb.Padding = new Thickness(7, 0, 7, 0);
                cb.Click += delegate(object s, RoutedEventArgs e)
                {
                    _chartLineMode = (idx == 0);
                    ModernControlsFactory.UpdateSegmentedGroup(_chartTypeBtns, idx);
                    RedrawChart();
                };
                _chartTypeBtns[i] = cb;
                typeSp.Children.Add(cb);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_chartTypeBtns, 0);

            chartRightTools.Children.Add(typeBox);
            chartRightTools.Children.Add(metricBox);
            DockPanel.SetDock(chartRightTools, Dock.Right);
            chartHead.Children.Add(chartRightTools);

            StackPanel chartLeftSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _trendTitleText = new TextBlock
            {
                Text = "每日消耗趋势",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _trendTitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            chartLeftSp.Children.Add(_trendTitleText);

            _selectedDatePill = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = "点击取消单日钻取过滤 (Esc)"
            };
            _selectedDatePill.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            _selectedDatePill.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            _selectedDatePillText = new TextBlock { Text = "", FontSize = DesignSystem.TextCaption, FontFamily = DesignSystem.FontMono };
            _selectedDatePillText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            _selectedDatePill.Child = _selectedDatePillText;
            _selectedDatePill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                _selectedDate = null;
                RefreshDashboardView();
            };
            chartLeftSp.Children.Add(_selectedDatePill);

            chartHead.Children.Add(chartLeftSp);
            Grid.SetRow(chartHead, 0);
            chartGrid.Children.Add(chartHead);

            // 趋势图画布与交互浮层
            _chartInteractiveHost = new Grid();
            _chartInteractiveHost.Margin = new Thickness(0, 6, 0, 0);
            _chartInteractiveHost.Background = Brushes.Transparent;
            _chartInteractiveHost.ClipToBounds = true;

            _chartCanvas = new Canvas();
            _chartInteractiveHost.Children.Add(_chartCanvas);

            _chartOverlayCanvas = new Canvas { IsHitTestVisible = false };

            _chartCrosshairLine = new Shapes.Line
            {
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection(new double[] { 3.0, 3.0 }),
                Visibility = Visibility.Collapsed
            };
            _chartCrosshairLine.SetResourceReference(Shapes.Shape.StrokeProperty, "BorderFocus");
            _chartOverlayCanvas.Children.Add(_chartCrosshairLine);

            _chartHoverDot = new Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed
            };
            _chartHoverDot.SetResourceReference(Shapes.Shape.FillProperty, "AccentPrimary");
            _chartHoverDot.SetResourceReference(Shapes.Shape.StrokeProperty, "BgCard");
            _chartOverlayCanvas.Children.Add(_chartHoverDot);

            _chartFloatingCard = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 6, 9, 6),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _chartFloatingCard.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            _chartFloatingCard.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            StackPanel fcSp = new StackPanel();
            _chartFloatingDateText = new TextBlock { FontSize = DesignSystem.TextCaption, FontFamily = DesignSystem.FontMono, FontWeight = FontWeights.SemiBold };
            _chartFloatingDateText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            fcSp.Children.Add(_chartFloatingDateText);

            _chartFloatingPrimaryText = new TextBlock { FontSize = DesignSystem.TextBody, FontFamily = DesignSystem.FontMono, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 1) };
            _chartFloatingPrimaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            fcSp.Children.Add(_chartFloatingPrimaryText);

            _chartFloatingSubText = new TextBlock { FontSize = DesignSystem.TextCaption };
            _chartFloatingSubText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            fcSp.Children.Add(_chartFloatingSubText);

            _chartFloatingCard.Child = fcSp;
            _chartOverlayCanvas.Children.Add(_chartFloatingCard);

            _chartInteractiveHost.Children.Add(_chartOverlayCanvas);
            _chartInteractiveHost.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                RedrawChart();
            };
            _chartInteractiveHost.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                HideChartCrosshair();
            };

            Grid.SetRow(_chartInteractiveHost, 1);
            chartGrid.Children.Add(_chartInteractiveHost);

            chartCard.Child = chartGrid;
            Grid.SetColumn(chartCard, 2);
            midGrid.Children.Add(chartCard);

            Grid.SetRow(midGrid, 1);
            g.Children.Add(midGrid);

            // Row 2: 底部最新实时请求快览卡片 + 右上角「查看完整实时请求流 →」一键切至视图 1
            Border recentCard = ModernControlsFactory.CreateCard(new Thickness(0));
            Grid recentGrid = new Grid();
            recentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            recentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border recentHeadBd = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 0, 10, 0)
            };
            recentHeadBd.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel recentHead = new DockPanel();
            Button jumpFullLiveBtn = ModernControlsFactory.CreateButton("查看完整实时请求流 →", "ghost");
            jumpFullLiveBtn.Height = 26;
            jumpFullLiveBtn.FontSize = DesignSystem.TextCaption;
            jumpFullLiveBtn.SetResourceReference(Control.ForegroundProperty, "AccentText");
            jumpFullLiveBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                SwitchActiveView(ViewLiveStream);
            };
            DockPanel.SetDock(jumpFullLiveBtn, Dock.Right);
            recentHead.Children.Add(jumpFullLiveBtn);

            StackPanel recentLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Path actIcon = UiIcons.CreateIcon(UiIcons.Activity, 13.5, "AccentText");
            actIcon.Margin = new Thickness(0, 0, 6, 0);
            recentLeft.Children.Add(actIcon);

            TextBlock recentTitle = new TextBlock
            {
                Text = "最新实时请求快览",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            recentTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            recentLeft.Children.Add(recentTitle);

            _overviewLiveTickerText = new TextBlock
            {
                Text = "免代理监听各工具实时单笔 Token、耗时与 TPS",
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(12, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _overviewLiveTickerText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(_overviewLiveTickerText, false);
            recentLeft.Children.Add(_overviewLiveTickerText);

            recentHead.Children.Add(recentLeft);
            recentHeadBd.Child = recentHead;
            Grid.SetRow(recentHeadBd, 0);
            recentGrid.Children.Add(recentHeadBd);

            Grid recentBody = new Grid();
            _overviewLiveGrid = CreateLiveStreamDataGrid(true);
            recentBody.Children.Add(_overviewLiveGrid);

            TextBlock dummyEmptyTitle;
            _overviewLiveEmpty = CreateEmptyStateOverlay(
                UiIcons.Activity,
                "尚无单笔实时请求流记录",
                "在任意受支持的 AI 编程工具中发起一次对话后，单条请求耗时、TPS 与花费将自动在此滚动呈现",
                out dummyEmptyTitle);
            recentBody.Children.Add(_overviewLiveEmpty);

            Grid.SetRow(recentBody, 1);
            recentGrid.Children.Add(recentBody);

            recentCard.Child = recentGrid;
            Grid.SetRow(recentCard, 2);
            g.Children.Add(recentCard);

            return g;
        }

        private static TextBlock CreateKpiHeroValue(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontDisplay,
                FontSize = DesignSystem.TextDisplay,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(tb, false);
            return tb;
        }

        private static TextBlock CreateKpiSubText(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(tb, false);
            return tb;
        }

        // -------------------- 视图 1：实时请求流（100% 全高视口，同屏可看 18~24 行） --------------------
        private UIElement BuildLiveStreamView()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(16, 12, 16, 14);
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 顶部工具栏：即时搜索框 (Ctrl+F) + 暂停/恢复刷新按钮 + 最新请求动态摘要
            DockPanel toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            StackPanel rightBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _liveStreamSummaryText = new TextBlock
            {
                Text = "实时监听中",
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                MaxWidth = 420,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _liveStreamSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            ModernControlsFactory.EnableTabularNumbers(_liveStreamSummaryText, false);
            rightBar.Children.Add(_liveStreamSummaryText);

            _livePauseBtn = ModernControlsFactory.CreateButton("", "secondary");
            _livePauseBtn.Height = 30;
            _livePauseBtn.Padding = new Thickness(10, 0, 10, 0);
            _livePauseBtn.ToolTip = "暂停或恢复实时请求流表格自动滚动刷新（暂停期间后台仍持续采集）";

            StackPanel pauseSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _livePauseIcon = UiIcons.CreateIcon(UiIcons.Pause, 13.0, null);
            _livePauseIcon.Margin = new Thickness(0, 0, 5, 0);
            pauseSp.Children.Add(_livePauseIcon);

            _livePauseBtnText = new TextBlock { Text = "暂停刷新", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            ModernControlsFactory.EnableTabularNumbers(_livePauseBtnText, false);
            pauseSp.Children.Add(_livePauseBtnText);
            _livePauseBtn.Content = pauseSp;

            _livePauseBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _livePaused = !_livePaused;
                if (!_livePaused)
                {
                    _pausedBufferedCount = 0;
                    UpdateLivePauseButtonVisual();
                    RefreshDashboardView();
                }
                else
                {
                    UpdateLivePauseButtonVisual();
                }
            };
            rightBar.Children.Add(_livePauseBtn);

            _toggleInspectorBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Layers, "单笔账单透视", "secondary");
            _toggleInspectorBtn.Height = 30;
            _toggleInspectorBtn.Margin = new Thickness(8, 0, 0, 0);
            _toggleInspectorBtn.ToolTip = "展开或收起右侧单笔请求四维计费瀑布流与缓存省钱透视抽屉";
            _toggleInspectorBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_requestInspector != null && _requestInspector.Visibility == Visibility.Visible)
                {
                    CloseRequestInspector();
                }
                else
                {
                    LiveRequestRowView sel = _liveGrid != null ? (_liveGrid.SelectedItem as LiveRequestRowView) : null;
                    if (sel == null && _liveGrid != null && _liveGrid.Items.Count > 0)
                        sel = _liveGrid.Items[0] as LiveRequestRowView;
                    OpenRequestInspectorFor(sel);
                }
            };
            rightBar.Children.Add(_toggleInspectorBtn);

            DockPanel.SetDock(rightBar, Dock.Right);
            toolbar.Children.Add(rightBar);

            StackPanel leftBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border searchWrap = CreateSearchInputWithIcon("过滤工具、模型名称或请求时间... (Ctrl+F)", 260, out _liveSearchInput);
            _liveSearchInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                _liveSearchQuery = (_liveSearchInput.Text ?? "").Trim();
                bool prevPause = _livePaused;
                _livePaused = false;
                RefreshDashboardView();
                _livePaused = prevPause;
            };
            leftBar.Children.Add(searchWrap);

            _liveClearFilterBtn = ModernControlsFactory.CreateButton("重置筛选 ×", "ghost");
            _liveClearFilterBtn.Height = 28;
            _liveClearFilterBtn.Margin = new Thickness(8, 0, 0, 0);
            _liveClearFilterBtn.Visibility = Visibility.Collapsed;
            _liveClearFilterBtn.SetResourceReference(Control.ForegroundProperty, "AccentText");
            _liveClearFilterBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _selectedTool = null;
                _selectedDate = null;
                if (_liveSearchInput != null) _liveSearchInput.Text = "";
                _liveSearchQuery = "";
                RefreshDashboardView();
            };
            leftBar.Children.Add(_liveClearFilterBtn);

            toolbar.Children.Add(leftBar);
            Grid.SetRow(toolbar, 0);
            g.Children.Add(toolbar);

            // 全高主从分栏区：左侧 DataGrid + 右侧可折叠 310px 单笔账单透视抽屉
            Grid contentSplit = new Grid();
            contentSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _requestInspectorGapCol = new ColumnDefinition { Width = new GridLength(0) };
            contentSplit.ColumnDefinitions.Add(_requestInspectorGapCol);
            contentSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border tableCard = ModernControlsFactory.CreateCard(new Thickness(0));
            Grid body = new Grid();
            _liveGrid = CreateLiveStreamDataGrid(false);
            _liveGrid.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                LiveRequestRowView picked = _liveGrid.SelectedItem as LiveRequestRowView;
                if (picked != null)
                {
                    OpenRequestInspectorFor(picked);
                }
            };
            body.Children.Add(_liveGrid);

            _liveEmptyOverlay = CreateEmptyStateOverlay(
                UiIcons.Activity,
                "暂无匹配的实时请求记录",
                "当前仅展示本机带精确时间戳的单笔 API/工具交互记录；单击任意行可唤起右侧四维计费瀑布流透视",
                out _liveEmptyTitle);
            body.Children.Add(_liveEmptyOverlay);

            tableCard.Child = body;
            Grid.SetColumn(tableCard, 0);
            contentSplit.Children.Add(tableCard);

            _requestInspector = new RequestInspectorPanel(
                _homeDir,
                delegate()
                {
                    CloseRequestInspector();
                },
                delegate(string modelId)
                {
                    if (_liveSearchInput != null)
                        _liveSearchInput.Text = modelId ?? "";
                });
            _requestInspector.Visibility = Visibility.Collapsed;
            Grid.SetColumn(_requestInspector, 2);
            contentSplit.Children.Add(_requestInspector);

            Grid.SetRow(contentSplit, 1);
            g.Children.Add(contentSplit);
            return g;
        }

        private void OpenRequestInspectorFor(LiveRequestRowView row)
        {
            if (_requestInspector == null) return;
            _requestInspector.Visibility = Visibility.Visible;
            if (_requestInspectorGapCol != null)
                _requestInspectorGapCol.Width = new GridLength(10);
            _requestInspector.Inspect(row, _isCny, _usdToCny, _isDark);
        }

        private void CloseRequestInspector()
        {
            if (_requestInspector == null) return;
            _requestInspector.Visibility = Visibility.Collapsed;
            if (_requestInspectorGapCol != null)
                _requestInspectorGapCol.Width = new GridLength(0);
        }

        private void UpdateLivePauseButtonVisual()
        {
            if (_livePauseBtn == null || _livePauseBtnText == null || _livePauseIcon == null) return;
            if (_livePaused)
            {
                _livePauseIcon.Data = Geometry.Parse(UiIcons.Play);
                _livePauseBtnText.Text = _pausedBufferedCount > 0
                    ? string.Format(CultureInfo.InvariantCulture, "恢复刷新 (+{0})", _pausedBufferedCount)
                    : "已暂停 · 点击恢复";
                _livePauseBtn.SetResourceReference(Control.ForegroundProperty, "SemanticWarning");
            }
            else
            {
                _livePauseIcon.Data = Geometry.Parse(UiIcons.Pause);
                _livePauseBtnText.Text = "暂停刷新";
                _livePauseBtn.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
            }
        }

        // -------------------- 视图 2：模型与工具账单（100% 全高） --------------------
        private UIElement BuildModelsView()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(16, 12, 16, 14);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 顶部：Top 模型多色堆叠占比分布条卡片
            Border distCard = ModernControlsFactory.CreateCard(new Thickness(14, 10, 14, 10));
            distCard.Margin = new Thickness(0, 0, 0, 10);
            StackPanel distSp = new StackPanel();

            DockPanel distHead = new DockPanel();
            _modelCountSummaryText = new TextBlock
            {
                Text = "0 个模型",
                FontSize = DesignSystem.TextCaption,
                FontFamily = DesignSystem.FontMono,
                VerticalAlignment = VerticalAlignment.Center
            };
            _modelCountSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(_modelCountSummaryText, Dock.Right);
            distHead.Children.Add(_modelCountSummaryText);

            TextBlock distTitle = new TextBlock
            {
                Text = "主力模型账单构成分布",
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            distTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            distHead.Children.Add(distTitle);
            distSp.Children.Add(distHead);

            Border barFrame = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 8, 0, 7),
                ClipToBounds = true
            };
            barFrame.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
            _modelStackedBarGrid = new Grid();
            barFrame.Child = _modelStackedBarGrid;
            distSp.Children.Add(barFrame);

            _modelLegendWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            distSp.Children.Add(_modelLegendWrap);

            distCard.Child = distSp;
            Grid.SetRow(distCard, 0);
            g.Children.Add(distCard);

            // 中部工具栏：模型/工具即时搜索过滤框 + 模型单价工作台入口
            DockPanel toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            StackPanel modelRightTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            TextBlock rateHint = new TextBlock
            {
                Text = "点击「单价 ($/M)」可按百万输入单价排序",
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            rateHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            modelRightTools.Children.Add(rateHint);



            DockPanel.SetDock(modelRightTools, Dock.Right);
            toolbar.Children.Add(modelRightTools);

            Border searchWrap = CreateSearchInputWithIcon("搜索模型 ID 或所属 AI 工具... (Ctrl+F)", 260, out _modelSearchInput);
            searchWrap.HorizontalAlignment = HorizontalAlignment.Left;
            _modelSearchInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                _modelSearchQuery = (_modelSearchInput.Text ?? "").Trim();
                RefreshDashboardView();
            };
            toolbar.Children.Add(searchWrap);

            Grid.SetRow(toolbar, 1);
            g.Children.Add(toolbar);

            // 底部全高模型明细表
            Border tableCard = ModernControlsFactory.CreateCard(new Thickness(0));
            Grid body = new Grid();
            _modelGrid = CreateModelDataGrid();
            body.Children.Add(_modelGrid);

            TextBlock dummy;
            _modelEmptyOverlay = CreateEmptyStateOverlay(
                UiIcons.Models,
                "当前筛选范围内暂无模型账单记录",
                "尝试调整顶部时间段、设备范围或清空模型搜索关键字",
                out dummy);
            body.Children.Add(_modelEmptyOverlay);

            tableCard.Child = body;
            Grid.SetRow(tableCard, 2);
            g.Children.Add(tableCard);
            return g;
        }

        // -------------------- 视图 3：设备与 VPS 集群（100% 全高，OrbStack 风格卡片 + 对比表） --------------------
        private UIElement BuildDevicesView()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(16, 12, 16, 14);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 顶部：OrbStack 风格设备状态卡片横向滚动列
            ScrollViewer cardsSv = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _deviceCardsStack = new StackPanel { Orientation = Orientation.Horizontal };
            cardsSv.Content = _deviceCardsStack;
            Grid.SetRow(cardsSv, 0);
            g.Children.Add(cardsSv);

            // 中部标题与集群摘要行
            DockPanel bar = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
            _deviceClusterSummaryText = new TextBlock
            {
                Text = "双击任意设备行即可锁定查看该节点独立账单；右键可修改本地备注名或清理废弃离线节点缓存",
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            _deviceClusterSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(_deviceClusterSummaryText, Dock.Right);
            bar.Children.Add(_deviceClusterSummaryText);

            TextBlock listHdr = new TextBlock
            {
                Text = "多设备与 Linux VPS 节点账单对比明细",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            listHdr.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            bar.Children.Add(listHdr);
            Grid.SetRow(bar, 1);
            g.Children.Add(bar);

            // 底部全高设备对比 DataGrid
            Border tableCard = ModernControlsFactory.CreateCard(new Thickness(0));
            _machineGrid = CreateMachineDataGrid();
            tableCard.Child = _machineGrid;
            Grid.SetRow(tableCard, 2);
            g.Children.Add(tableCard);

            return g;
        }

        // -------------------- 视图 4：每日流水与导出（100% 全高，含 3 张导出动作卡 + 完整按日流水含 CacheWrite） --------------------
        private UIElement BuildDailyExportView()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(16, 12, 16, 14);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });

            // 顶部：近 365 天每日用量日历（导出统一走页面工具条右侧的「导出」菜单）
            _heatmapView = new UsageHeatmapView();
            _heatmapView.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(_heatmapView, 0);
            g.Children.Add(_heatmapView);

            // 中部工具栏：月份/日期即时过滤 + 汇总微提示
            DockPanel toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _dailyFilteredSummaryText = new TextBlock
            {
                Text = "共 0 天记录",
                FontSize = DesignSystem.TextCaption,
                FontFamily = DesignSystem.FontMono,
                VerticalAlignment = VerticalAlignment.Center
            };
            _dailyFilteredSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            DockPanel.SetDock(_dailyFilteredSummaryText, Dock.Right);
            toolbar.Children.Add(_dailyFilteredSummaryText);

            Border searchWrap = CreateSearchInputWithIcon("按月份或日期过滤（例如 2026-11）... (Ctrl+F)", 260, out _dailySearchInput);
            searchWrap.HorizontalAlignment = HorizontalAlignment.Left;
            _dailySearchInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                _dailySearchQuery = (_dailySearchInput.Text ?? "").Trim();
                RefreshDashboardView();
            };
            toolbar.Children.Add(searchWrap);

            Grid.SetRow(toolbar, 1);
            g.Children.Add(toolbar);

            // 底部全高按日流水 DataGrid（补齐 CacheWrite 缓存写入列！）
            Border tableCard = ModernControlsFactory.CreateCard(new Thickness(0));
            Grid body = new Grid();
            _dailyGrid = CreateDailyDataGrid();
            body.Children.Add(_dailyGrid);

            TextBlock dummy;
            _dailyEmptyOverlay = CreateEmptyStateOverlay(
                UiIcons.Calendar,
                "暂无符合筛选条件的每日流水记录",
                "清空上方日期过滤词或切换时间范围即可查看完整历史日账单",
                out dummy);
            body.Children.Add(_dailyEmptyOverlay);

            tableCard.Child = body;
            Grid.SetRow(tableCard, 2);
            g.Children.Add(tableCard);
            return g;
        }

        // -------------------- 视图 5：同步与设置（直接内嵌 SyncSettingsView） --------------------
        private UIElement BuildSettingsView()
        {
            SyncSettingsCallbacks callbacks = new SyncSettingsCallbacks();

            callbacks.OnSynced = delegate(SyncOutcome outcome)
            {
                if (_scanResult != null)
                {
                    _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);
                    Program.ApplyNodeAliasToSnapshot(_localNode, _syncCfg);
                }
                if (outcome != null)
                {
                    _cloudHasAttempted = true;
                    _cloudLastOk = outcome.Success;
                    if (_cloudLastOk) _lastCloudSyncAt = DateTime.Now;
                    _lastSyncStatus = outcome.Message ?? "";
                    if (outcome.RemoteNodes != null)
                    {
                        try
                        {
                            PricingEngine pe = new PricingEngine(_homeDir);
                            for (int i = 0; i < outcome.RemoteNodes.Count; i++)
                            {
                                if (outcome.RemoteNodes[i] != null)
                                {
                                    pe.RecalculateRemoteSnapshot(outcome.RemoteNodes[i]);
                                    Program.ApplyNodeAliasToSnapshot(outcome.RemoteNodes[i], _syncCfg);
                                }
                            }
                        }
                        catch { }
                        _remoteNodes = outcome.RemoteNodes;
                    }
                }
                else
                {
                    _remoteNodes = LoadAndRecalculateRemoteNodes(_syncCfg.MachineId, _homeDir, _syncCfg);
                }

                ApplyPreferencesFromConfig();
                UpdateCloudStatusUi();
                RefreshDashboardView();
            };

            callbacks.OnThemeOrCurrencyChanged = delegate()
            {
                ApplyPreferencesFromConfig();
                RefreshDashboardView();
            };

            callbacks.OnFeaturesChanged = delegate()
            {
                ApplySubscriptionFeature();
            };

            callbacks.OnHomeDirectoryChanged = delegate(string newHomeDir)
            {
                if (!string.IsNullOrWhiteSpace(newHomeDir) && Directory.Exists(newHomeDir))
                {
                    _homeDir = newHomeDir.Trim();
                    _lastTopRequestTs = 0L;
                    _doctorLoadedOnce = false;
                    if (_projectView != null)
                        _projectView.SetHomeDirectory(_homeDir);
                    RebuildDirectoryWatchers(_homeDir);
                    RunSilentScanAsync(_homeDir, true, false);
                    ShowToastSafe("已切换扫描主目录", _homeDir, "info");
                }
            };

            callbacks.OnForceRefreshPricing = delegate()
            {
                StartCloudPricingSyncAsync(true);
            };

            callbacks.OnForceRefreshFx = delegate()
            {
                StartExchangeRateSyncAsync(true);
            };

            callbacks.ShowToast = delegate(string title, string detail, string variant)
            {
                ShowToastSafe(title, detail, variant);
            };

            _settingsView = new SyncSettingsView(
                this,
                _syncCfg,
                delegate() { return _scanResult; },
                delegate() { return _homeDir; },
                callbacks);
            _settingsView.AddHeaderAction(BuildSettingsMoreButton());

            return _settingsView;
        }

        // -------------------- 现代表格与通用辅助构建器 --------------------
        private static Border CreateSearchInputWithIcon(string placeholder, double width, out TextBox textBoxOut)
        {
            Grid host = new Grid { Width = width, Height = 30 };

            TextBox tb = ModernControlsFactory.CreateTextInput("", 30.0);
            tb.Padding = new Thickness(28, 0, 8, 0);
            tb.FontSize = DesignSystem.TextBody;
            host.Children.Add(tb);

            Shapes.Path icon = UiIcons.CreateIcon(UiIcons.Search, 13.5, "FgMuted");
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(9, 0, 0, 0);
            icon.IsHitTestVisible = false;
            host.Children.Add(icon);

            TextBlock watermark = new TextBlock
            {
                Text = placeholder,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(29, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            watermark.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            host.Children.Add(watermark);

            TextBox capturedTb = tb;
            tb.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                watermark.Visibility = string.IsNullOrEmpty(capturedTb.Text) ? Visibility.Visible : Visibility.Collapsed;
            };

            Border wrapper = new Border { Child = host };
            textBoxOut = tb;
            return wrapper;
        }

        private static Border CreateEmptyStateOverlay(string iconPath, string title, string subtitle, out TextBlock titleOut)
        {
            Border bd = new Border
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            StackPanel sp = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24)
            };

            Border iconCircle = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            iconCircle.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            iconCircle.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            iconCircle.Child = UiIcons.CreateIcon(iconPath, 18.0, "FgMuted");
            sp.Children.Add(iconCircle);

            TextBlock t1 = new TextBlock
            {
                Text = title,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(t1);

            TextBlock t2 = new TextBlock
            {
                Text = subtitle,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 4, 0, 0),
                MaxWidth = 440,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(t2);

            bd.Child = sp;
            titleOut = t1;
            return bd;
        }

        private DataGrid CreateLiveStreamDataGrid(bool compactOverviewMode)
        {
            DataGrid dg = ModernDataGridFactory.CreateModernDataGrid();

            if (compactOverviewMode)
            {
                // 概览只做快览：明细列（缓存、耗时、速率）留给「请求明细」页
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "时间", "TimeText", "TimestampMs", new DataGridLength(92), false, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeBadgeColumn("AI 工具", "Tool", "Tool", new DataGridLength(140)));
                DataGridColumn modelCol = ModernDataGridFactory.MakeTextOrMonoColumn(dg, "模型", "Model", "Model", new DataGridLength(1.45, DataGridLengthUnitType.Star), false, true, true);
                modelCol.MinWidth = 140.0;
                dg.Columns.Add(modelCol);
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "输出", "OutputText", "Output", new DataGridLength(80), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(96), true, true, true));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "花费", "FormattedCost", "CostUsd", new DataGridLength(92), true, true, true));
            }
            else
            {
                // 完整实时流需兼容右侧 310px 透视抽屉同时展开，精简列头并确保数字与花费列绝不截断
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "时间", "TimeText", "TimestampMs", new DataGridLength(92), false, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeBadgeColumn("AI 工具", "Tool", "Tool", new DataGridLength(136)));
                DataGridColumn modelCol = ModernDataGridFactory.MakeTextOrMonoColumn(dg, "模型名称", "Model", "Model", new DataGridLength(1.3, DataGridLengthUnitType.Star), false, true, true);
                modelCol.MinWidth = 112.0;
                dg.Columns.Add(modelCol);
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "新输入", "FreshInputText", "FreshInput", new DataGridLength(64), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "缓存读", "CacheReadText", "CacheRead", new DataGridLength(68), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "缓存写", "CacheWriteText", "CacheWrite", new DataGridLength(70), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "输出", "OutputText", "Output", new DataGridLength(60), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "总计", "TotalTokensText", "TotalTokens", new DataGridLength(68), true, true, true));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "耗时", "DurationText", "DurationMs", new DataGridLength(54), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "速率", "TpsText", "Tps", new DataGridLength(74), true, true, false));
                dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "花费", "FormattedCost", "CostUsd", new DataGridLength(76), true, true, true));
            }

            ContextMenu cm = new ContextMenu();
            cm.Resources = Resources;
            cm.Opened += delegate(object s, RoutedEventArgs e) { cm.Resources = Resources; };

            MenuItem copyModelItem = new MenuItem { Header = "复制模型名称" };
            copyModelItem.Click += delegate(object s, RoutedEventArgs e)
            {
                LiveRequestRowView row = dg.SelectedItem as LiveRequestRowView;
                if (row != null && TrySetClipboardText(row.Model))
                    ShowToastSafe("已复制模型名称", row.Model, "success");
            };
            cm.Items.Add(copyModelItem);

            MenuItem copyTsvItem = new MenuItem { Header = "复制整行明细 (TSV)" };
            copyTsvItem.Click += delegate(object s, RoutedEventArgs e)
            {
                LiveRequestRowView r = dg.SelectedItem as LiveRequestRowView;
                if (r != null)
                {
                    string tsv = string.Join("\t", new string[]
                    {
                        r.TimeText ?? "", r.Tool ?? "", r.Model ?? "",
                        r.FreshInputText ?? "", r.CacheReadText ?? "", r.OutputText ?? "",
                        r.TotalTokensText ?? "", r.DurationText ?? "", r.TpsText ?? "", r.FormattedCost ?? ""
                    });
                    if (TrySetClipboardText(tsv))
                        ShowToastSafe("已复制单条请求明细", r.Tool + " · " + r.Model, "success");
                }
            };
            cm.Items.Add(copyTsvItem);

            cm.Items.Add(new Separator());

            MenuItem filterToolItem = new MenuItem { Header = "仅筛选该 AI 工具" };
            filterToolItem.Click += delegate(object s, RoutedEventArgs e)
            {
                LiveRequestRowView r = dg.SelectedItem as LiveRequestRowView;
                if (r != null && !string.IsNullOrEmpty(r.Tool))
                {
                    _selectedTool = r.Tool;
                    RefreshDashboardView();
                }
            };
            cm.Items.Add(filterToolItem);

            MenuItem filterModelItem = new MenuItem { Header = "在实时流中搜索该模型" };
            filterModelItem.Click += delegate(object s, RoutedEventArgs e)
            {
                LiveRequestRowView r = dg.SelectedItem as LiveRequestRowView;
                if (r != null && !string.IsNullOrEmpty(r.Model))
                {
                    SwitchActiveView(ViewLiveStream);
                    if (_liveSearchInput != null) _liveSearchInput.Text = r.Model;
                }
            };
            cm.Items.Add(filterModelItem);

            MenuItem inspectRowItem = new MenuItem { Header = "打开单笔账单瀑布流透视抽屉" };
            inspectRowItem.Click += delegate(object s, RoutedEventArgs e)
            {
                LiveRequestRowView r = dg.SelectedItem as LiveRequestRowView;
                if (r != null)
                {
                    SwitchActiveView(ViewLiveStream);
                    OpenRequestInspectorFor(r);
                }
            };
            cm.Items.Add(inspectRowItem);

            dg.ContextMenu = cm;
            if (compactOverviewMode)
            {
                dg.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
                {
                    LiveRequestRowView r = dg.SelectedItem as LiveRequestRowView;
                    SwitchActiveView(ViewLiveStream);
                    if (r != null)
                        OpenRequestInspectorFor(r);
                };
            }
            return dg;
        }

        private DataGrid CreateModelDataGrid()
        {
            DataGrid dg = ModernDataGridFactory.CreateModernDataGrid();

            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "模型名称", "Model", "Model", new DataGridLength(1.65, DataGridLengthUnitType.Star), false, true, true));
            dg.Columns.Add(ModernDataGridFactory.MakeBadgeColumn("所属工具", "Tool", "Tool", new DataGridLength(138)));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "调用次数", "Turns", "Turns", new DataGridLength(80), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "纯新增输入", "FreshInputText", "FreshInput", new DataGridLength(96), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "缓存读取", "CacheReadText", "CacheRead", new DataGridLength(96), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "输出 Token", "OutputText", "Output", new DataGridLength(92), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "缓存命中", "CacheHitText", "CacheRead", new DataGridLength(82), true, true, false));
            // 关键修复：单价 ($/M) 列的 SortMemberPath 严格绑定 InputPerM，彻底修正旧版错排为 CostUsd 的 Bug！
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "单价 ($/M)", "RateLabel", "InputPerM", new DataGridLength(108), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "等效账单", "FormattedCost", "CostUsd", new DataGridLength(104), true, true, true));
            dg.Columns.Add(ModernDataGridFactory.MakeShareBarColumn("账单占比", "ShareText", "BarRatio", "BarRatio", new DataGridLength(122)));

            ContextMenu cm = new ContextMenu();
            cm.Resources = Resources;
            cm.Opened += delegate(object s, RoutedEventArgs e) { cm.Resources = Resources; };

            MenuItem copyMdl = new MenuItem { Header = "复制模型 ID" };
            copyMdl.Click += delegate(object s, RoutedEventArgs e)
            {
                ModelRowView r = dg.SelectedItem as ModelRowView;
                if (r != null && TrySetClipboardText(r.Model))
                    ShowToastSafe("已复制模型 ID", r.Model, "success");
            };
            cm.Items.Add(copyMdl);

            MenuItem copyRow = new MenuItem { Header = "复制整行账单明细 (TSV)" };
            copyRow.Click += delegate(object s, RoutedEventArgs e)
            {
                ModelRowView r = dg.SelectedItem as ModelRowView;
                if (r != null)
                {
                    string tsv = string.Join("\t", new string[]
                    {
                        r.Model ?? "", r.Tool ?? "", r.Turns.ToString(CultureInfo.InvariantCulture),
                        r.FreshInputText ?? "", r.CacheReadText ?? "", r.OutputText ?? "",
                        r.RateLabel ?? "", r.FormattedCost ?? "", r.ShareText ?? ""
                    });
                    if (TrySetClipboardText(tsv))
                        ShowToastSafe("已复制模型账单行", r.Model + " · " + r.FormattedCost, "success");
                }
            };
            cm.Items.Add(copyRow);

            cm.Items.Add(new Separator());

            MenuItem filterByTool = new MenuItem { Header = "仅看该 AI 工具" };
            filterByTool.Click += delegate(object s, RoutedEventArgs e)
            {
                ModelRowView r = dg.SelectedItem as ModelRowView;
                if (r != null && !string.IsNullOrEmpty(r.Tool))
                {
                    _selectedTool = r.Tool;
                    RefreshDashboardView();
                }
            };
            cm.Items.Add(filterByTool);

            MenuItem jumpLive = new MenuItem { Header = "在实时请求流中筛选该模型" };
            jumpLive.Click += delegate(object s, RoutedEventArgs e)
            {
                ModelRowView r = dg.SelectedItem as ModelRowView;
                if (r != null && !string.IsNullOrEmpty(r.Model))
                {
                    SwitchActiveView(ViewLiveStream);
                    if (_liveSearchInput != null) _liveSearchInput.Text = r.Model;
                }
            };
            cm.Items.Add(jumpLive);

            MenuItem openStudioItem = new MenuItem { Header = "在模型单价工作台中自定义覆盖费率..." };
            openStudioItem.Click += delegate(object s, RoutedEventArgs e)
            {
                ModelRowView r = dg.SelectedItem as ModelRowView;
                OpenPricingStudioDialog(r != null ? r.Model : null);
            };
            cm.Items.Add(openStudioItem);

            dg.ContextMenu = cm;
            return dg;
        }

        private DataGrid CreateMachineDataGrid()
        {
            DataGrid dg = ModernDataGridFactory.CreateModernDataGrid();

            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "设备名称", "MachineName", "MachineName", new DataGridLength(1.25, DataGridLengthUnitType.Star), false, false, true));
            dg.Columns.Add(ModernDataGridFactory.MakeBadgeColumn("节点类型", "TypeBadge", "TypeBadge", new DataGridLength(106)));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "在线状态", "StatusText", "IsOnline", new DataGridLength(96), false, false, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "操作系统", "OsInfo", "OsInfo", new DataGridLength(1.15, DataGridLengthUnitType.Star), false, false, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "活跃工具", "ToolsSummary", "ToolCount", new DataGridLength(1.3, DataGridLengthUnitType.Star), false, false, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "最后同步", "SyncedAt", "SyncedAt", new DataGridLength(136), false, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "调用数", "Turns", "Turns", new DataGridLength(74), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(94), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "等效花费", "FormattedMoney", "CostUsd", new DataGridLength(110), true, true, true));
            dg.Columns.Add(ModernDataGridFactory.MakeShareBarColumn("集群占比", "ShareText", "BarRatio", "BarRatio", new DataGridLength(116)));

            dg.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                MachineRowView row = dg.SelectedItem as MachineRowView;
                if (row != null && !string.IsNullOrEmpty(row.MachineId))
                {
                    _selectedMachineId = (_selectedMachineId != null && string.Equals(_selectedMachineId, row.MachineId, StringComparison.OrdinalIgnoreCase))
                        ? null
                        : row.MachineId;
                    RefreshDashboardView();
                }
            };

            ContextMenu cm = new ContextMenu();
            cm.Resources = Resources;

            MenuItem filterNodeItem = new MenuItem { Header = "锁定 / 取消仅看该设备" };
            filterNodeItem.Click += delegate(object s, RoutedEventArgs e)
            {
                MachineRowView r = dg.SelectedItem as MachineRowView;
                if (r != null && !string.IsNullOrEmpty(r.MachineId))
                {
                    _selectedMachineId = (_selectedMachineId != null && string.Equals(_selectedMachineId, r.MachineId, StringComparison.OrdinalIgnoreCase))
                        ? null
                        : r.MachineId;
                    RefreshDashboardView();
                }
            };
            cm.Items.Add(filterNodeItem);

            MenuItem aliasItem = new MenuItem { Header = "设置设备本地备注名称..." };
            aliasItem.Click += delegate(object s, RoutedEventArgs e)
            {
                MachineRowView r = dg.SelectedItem as MachineRowView;
                if (r != null)
                    OpenDeviceAliasPopover(_deviceSelectorBtn ?? (UIElement)dg, r);
            };
            cm.Items.Add(aliasItem);

            MenuItem removeCacheItem = new MenuItem { Header = "移除该远端设备的本地缓存快照" };
            removeCacheItem.Click += delegate(object s, RoutedEventArgs e)
            {
                MachineRowView r = dg.SelectedItem as MachineRowView;
                if (r != null && !r.IsLocal)
                    RemoveOfflineRemoteNodeCache(r);
            };
            cm.Items.Add(removeCacheItem);

            cm.Opened += delegate(object s, RoutedEventArgs e)
            {
                cm.Resources = Resources;
                MachineRowView r = dg.SelectedItem as MachineRowView;
                removeCacheItem.IsEnabled = (r != null && !r.IsLocal);
            };

            dg.ContextMenu = cm;
            return dg;
        }

        private DataGrid CreateDailyDataGrid()
        {
            DataGrid dg = ModernDataGridFactory.CreateModernDataGrid();

            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "日期", "Date", "Date", new DataGridLength(1.15, DataGridLengthUnitType.Star), false, true, true));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "调用次数", "Turns", "Turns", new DataGridLength(0.9, DataGridLengthUnitType.Star), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "纯新增输入", "FreshInputText", "FreshInput", new DataGridLength(1.05, DataGridLengthUnitType.Star), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "缓存读取", "CacheReadText", "CacheRead", new DataGridLength(1.05, DataGridLengthUnitType.Star), true, true, false));
            // 补齐 CacheWrite（缓存写入）列，消除大缓存写入与总 Token 对账差异！
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "缓存写入", "CacheWriteText", "CacheWrite", new DataGridLength(1.0, DataGridLengthUnitType.Star), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "输出 Token", "OutputText", "Output", new DataGridLength(1.05, DataGridLengthUnitType.Star), true, true, false));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(1.1, DataGridLengthUnitType.Star), true, true, true));
            dg.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(dg, "等效花费", "FormattedCost", "CostUsd", new DataGridLength(1.15, DataGridLengthUnitType.Star), true, true, true));

            dg.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                DailyRowView r = dg.SelectedItem as DailyRowView;
                if (r != null && !string.IsNullOrEmpty(r.Date))
                {
                    _selectedDate = (_selectedDate != null && string.Equals(_selectedDate, r.Date, StringComparison.Ordinal))
                        ? null
                        : r.Date;
                    RefreshDashboardView();
                    if (_selectedDate != null)
                        ShowToastSafe("已锁定单日视角", "当前仅展示 " + _selectedDate + " 当日账单（按 Esc 恢复）", "info");
                }
            };

            ContextMenu cm = new ContextMenu();
            cm.Resources = Resources;
            cm.Opened += delegate(object s, RoutedEventArgs e) { cm.Resources = Resources; };

            MenuItem drillItem = new MenuItem { Header = "按该日期钻取过滤全站数据" };
            drillItem.Click += delegate(object s, RoutedEventArgs e)
            {
                DailyRowView r = dg.SelectedItem as DailyRowView;
                if (r != null && !string.IsNullOrEmpty(r.Date))
                {
                    _selectedDate = r.Date;
                    RefreshDashboardView();
                    SwitchActiveView(ViewOverview);
                }
            };
            cm.Items.Add(drillItem);

            MenuItem copyDailyRow = new MenuItem { Header = "复制当日流水明细 (TSV)" };
            copyDailyRow.Click += delegate(object s, RoutedEventArgs e)
            {
                DailyRowView r = dg.SelectedItem as DailyRowView;
                if (r != null)
                {
                    string tsv = string.Join("\t", new string[]
                    {
                        r.Date ?? "", r.Turns.ToString(CultureInfo.InvariantCulture),
                        r.FreshInputText ?? "", r.CacheReadText ?? "", r.CacheWriteText ?? "",
                        r.OutputText ?? "", r.TotalTokensText ?? "", r.UsdText ?? "", r.CnyText ?? ""
                    });
                    if (TrySetClipboardText(tsv))
                        ShowToastSafe("已复制当日流水", r.Date + " · " + (_isCny ? r.CnyText : r.UsdText), "success");
                }
            };
            cm.Items.Add(copyDailyRow);

            dg.ContextMenu = cm;
            return dg;
        }

        // =================================================================================================
        // 3. 视图状态切换、多端聚合刷新引擎、平滑面积/柱状趋势图、导出与全局快捷键
        // =================================================================================================
        private static bool IsUsageView(int view)
        { return view==ViewLiveStream || view==ViewModels || view==ViewProjects || view==ViewBloat || view==ViewDailyExport; }

        private static int NavTopIndexOf(int view)
        {
            if (IsUsageView(view)) return NavTopUsage;
            if (view == ViewSettings || view == ViewDoctor) return NavTopSettings;
            for (int i = 0; i < NavTopViews.Length; i++)
            {
                if (NavTopViews[i] == view) return i;
            }
            return 0;
        }

        private void SwitchActiveView(int viewIdx)
        {
            if (viewIdx < 0 || viewIdx >= TotalViewCount)
                viewIdx = ViewOverview;
            if (viewIdx == ViewInsights && !SubscriptionsEnabled)
                viewIdx = ViewOverview;

            _activeView = viewIdx;

            if (_viewPages != null)
            {
                for (int i = 0; i < _viewPages.Length; i++)
                {
                    if (_viewPages[i] != null)
                        _viewPages[i].Visibility = (i == _activeView) ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (_activeView == ViewLiveStream)
            {
                _hasUnreadLivePulse = false;
                if (_navLiveNewDot != null)
                    _navLiveNewDot.Visibility = Visibility.Collapsed;
            }
            else if (_activeView == ViewSettings)
            {
                UpdateAutoStartButtonLabel();
                if (_settingsView != null)
                    _settingsView.RefreshDynamicStats();
            }

            if (_viewTitleText != null && _activeView >= 0 && _activeView < ViewTitles.Length)
                _viewTitleText.Text = ViewTitles[_activeView];

            if (_viewGroupText != null)
            {
                int top = NavTopIndexOf(_activeView);
                bool grouped = (top == NavTopUsage || top == NavTopSettings);
                _viewGroupText.Text = grouped ? (NavTopLabels[top] + " /") : "";
                _viewGroupText.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
            }

            ApplyPageToolbarScope();
            UpdateNavVisuals();
            UpdateTopSubContextText();
            RefreshActiveSpecialistView(false);

            if (_activeView == ViewOverview)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RedrawChart));
            }
        }

        private void UpdateNavVisuals()
        {
            if (_navLiveNewDot != null)
            {
                _navLiveNewDot.Visibility = (_hasUnreadLivePulse && _activeView != ViewLiveStream)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_navItemBorders == null) return;

            // 展开时高亮落在子页面上，分组标题只加深文字；折叠后子页面不可见，改由分组图标高亮。
            int top = NavTopIndexOf(_activeView);
            for (int i = 0; i < _navItemBorders.Length; i++)
            {
                bool isGroup = (i == NavTopUsage || i == NavTopSettings);
                bool inThis = (i == top);
                bool highlight = inThis && (!isGroup || _sidebarCollapsed);
                ApplyNavRowState(_navItemBorders[i], _navActiveBars[i], highlight, inThis);
            }

            if (_navChildBorders != null)
            {
                for (int v = 0; v < _navChildBorders.Length; v++)
                {
                    if (_navChildBorders[v] == null) continue;
                    bool active = (v == _activeView);
                    ApplyNavRowState(_navChildBorders[v], _navChildBars[v], active, active);
                }
            }
            ApplyNavRowState(_navPricingChild, null, false, false);
        }

        private static void ApplyNavRowState(Border row, Border bar, bool highlight, bool emphasize)
        {
            if (row == null) return;
            row.Tag = highlight;
            if (highlight)
                row.SetResourceReference(Border.BackgroundProperty, "BgSelected");
            else
                row.Background = Brushes.Transparent;
            row.SetResourceReference(TextElement.ForegroundProperty, emphasize ? "FgPrimary" : "FgSecondary");
            if (bar != null)
                bar.Visibility = highlight ? Visibility.Visible : Visibility.Hidden;
        }

        private bool SubscriptionsEnabled
        {
            get { return _syncCfg != null && _syncCfg.ShowSubscriptions; }
        }

        // 订阅记账与回本比例是可选功能（同步与偏好 → 扫描与偏好），默认关闭：
        // 关闭时侧栏、命令面板和 Ctrl+5 都不出现该页，已登记的订阅记录保留。
        private void ApplySubscriptionFeature()
        {
            int idx = Array.IndexOf(NavTopViews, ViewInsights);
            if (_navItemBorders != null && idx >= 0 && idx < _navItemBorders.Length && _navItemBorders[idx] != null)
                _navItemBorders[idx].Visibility = SubscriptionsEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_activeView == ViewInsights)
            {
                if (SubscriptionsEnabled) RefreshDashboardView();
                else SwitchActiveView(ViewOverview);
            }
        }

        private void ApplyPreferencesFromConfig()
        {
            if (_syncCfg == null) return;

            bool themeChanged = (_isDark != _syncCfg.IsDarkTheme);
            _isDark = _syncCfg.IsDarkTheme;
            _isCny = _syncCfg.PreferCny;

            if (themeChanged)
            {
                DesignSystem.ApplyTheme(this, _isDark);
                ModernWindowChrome.SyncDwmTheme(this, _isDark);
                if (_themeToggleIcon != null)
                    _themeToggleIcon.Data = Geometry.Parse(_isDark ? UiIcons.Sun : UiIcons.Moon);
                if (_themeToggleBtn != null)
                    _themeToggleBtn.ToolTip = _isDark ? "切换至浅色主题 (Paper Zinc)" : "切换至深色主题 (Obsidian Zinc)";
                if (_miniHud != null)
                    _miniHud.SetTheme(_isDark);
                if (_requestInspector != null)
                    _requestInspector.SetTheme(_isDark);
            }

            ModernControlsFactory.UpdateSegmentedGroup(_currencyBtns, _isCny ? 0 : 1);
            UpdateRangeSegmentVisuals();
            ModernControlsFactory.UpdateSegmentedGroup(_leftModeBtns, _leftRankByMachine ? 1 : 0);
            ModernControlsFactory.UpdateSegmentedGroup(_metricBtns, _rankByCost ? 0 : 1);
            ModernControlsFactory.UpdateSegmentedGroup(_chartTypeBtns, _chartLineMode ? 0 : 1);
            UpdateNavVisuals();
            UpdateCloudStatusUi();
            ApplySubscriptionFeature();

            if (_trayMinimizeToggleItem != null)
                _trayMinimizeToggleItem.Checked = _syncCfg.MinimizeToTray;
        }

        private void ToggleThemeFromUi()
        {
            _isDark = !_isDark;
            if (_syncCfg != null)
            {
                _syncCfg.IsDarkTheme = _isDark;
                PersistConfig(_syncCfg);
            }

            DesignSystem.ApplyTheme(this, _isDark);
            ModernWindowChrome.SyncDwmTheme(this, _isDark);

            if (_themeToggleIcon != null)
                _themeToggleIcon.Data = Geometry.Parse(_isDark ? UiIcons.Sun : UiIcons.Moon);
            if (_themeToggleBtn != null)
                _themeToggleBtn.ToolTip = _isDark ? "切换至浅色主题 (Paper Zinc)" : "切换至深色主题 (Obsidian Zinc)";
            if (_miniHud != null)
                _miniHud.SetTheme(_isDark);
            if (_requestInspector != null)
                _requestInspector.SetTheme(_isDark);

            ModernControlsFactory.UpdateSegmentedGroup(_currencyBtns, _isCny ? 0 : 1);
            UpdateRangeSegmentVisuals();
            ModernControlsFactory.UpdateSegmentedGroup(_leftModeBtns, _leftRankByMachine ? 1 : 0);
            ModernControlsFactory.UpdateSegmentedGroup(_metricBtns, _rankByCost ? 0 : 1);
            ModernControlsFactory.UpdateSegmentedGroup(_chartTypeBtns, _chartLineMode ? 0 : 1);
            UpdateNavVisuals();
            UpdateCloudStatusUi();

            RefreshDashboardView();
            if (_settingsView != null)
                _settingsView.ReloadFromConfig();
        }

        private void UpdateFxUiState(bool isLive, bool isSyncing)
        {
            string timePart = string.IsNullOrEmpty(_fxUpdatedAt) ? "" : (" · 更新于 " + _fxUpdatedAt);
            string tip = string.Format(
                CultureInfo.InvariantCulture,
                "USD/CNY {0:0.####}（来源：{1}{2}{3}）· 点击手动调整或同步",
                _usdToCny,
                _fxSource,
                timePart,
                isLive ? " · 实时" : "");

            if (_sidebarFxText != null)
            {
                _sidebarFxText.Text = isSyncing
                    ? "汇率同步中…"
                    : ("汇率 " + _usdToCny.ToString("0.###", CultureInfo.InvariantCulture));
                _sidebarFxText.ToolTip = tip;
            }
            if (_rateInput != null)
                _rateInput.ToolTip = tip;
        }

        private void UpdatePriceUiState(bool isSyncing)
        {
            if (_sidebarPriceText == null) return;
            int cnt = _scanResult != null ? _scanResult.PricingModelCount : 0;
            if (isSyncing)
                _sidebarPriceText.Text = "价格库同步中…";
            else if (cnt > 0)
                _sidebarPriceText.Text = string.Format(CultureInfo.InvariantCulture, "价格库 {0:N0}", cnt);
            else
                _sidebarPriceText.Text = "价格库";

            string tInfo = string.IsNullOrEmpty(_priceSyncTime) ? "" : (" · " + _priceSyncTime);
            _sidebarPriceText.ToolTip = string.Format(
                CultureInfo.InvariantCulture,
                "模型价格库 {0:N0} 个模型（{1}{2}）· 点击打开计价规则",
                cnt,
                _priceSyncSource,
                tInfo);
        }

        private void UpdateCloudStatusUi()
        {
            if (_syncStatusText == null) return;

            string text;
            string colorKey;
            string iconKey;
            string tip;

            if (_cloudSyncRunning)
            {
                text = "同步中…";
                colorKey = "AccentText";
                iconKey = "AccentText";
                tip = "正在同步本机与远端设备账单";
            }
            else if (_syncCfg == null || !_syncCfg.IsConfigured)
            {
                text = "未开启同步";
                colorKey = "FgMuted";
                iconKey = "FgMuted";
                tip = "尚未配置 GitHub Gist 或 WebDAV 多端同步，点击前往设置";
            }
            else
            {
                bool isDav = string.Equals(_syncCfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase);
                string provName = isDav ? "WebDAV" : "Gist";
                int remCnt = _remoteNodes != null ? _remoteNodes.Count : 0;

                if (_cloudHasAttempted && !_cloudLastOk)
                {
                    text = "同步异常";
                    colorKey = "SemanticWarning";
                    iconKey = "SemanticWarning";
                    tip = string.IsNullOrEmpty(_lastSyncStatus) ? "上次云同步失败，点击前往设置排查" : _lastSyncStatus;
                }
                else
                {
                    if (_lastCloudSyncAt.HasValue)
                    {
                        DateTime at = _lastCloudSyncAt.Value;
                        text = "已同步 " + at.ToString(at.Date == DateTime.Today ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        text = provName + " 已就绪";
                    }
                    colorKey = "FgSecondary";
                    iconKey = "SemanticSuccess";
                    tip = provName + " 多端同步已启用"
                        + (remCnt > 0 ? string.Format(CultureInfo.InvariantCulture, " · 另有 {0} 台设备", remCnt) : "")
                        + (string.IsNullOrEmpty(_lastSyncStatus) ? "" : ("\n" + _lastSyncStatus));
                }
            }

            _syncStatusText.Text = text;
            _syncStatusText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            if (_syncStatusIcon != null)
                _syncStatusIcon.SetResourceReference(Shapes.Shape.StrokeProperty, iconKey);
            if (_syncStatusBtn != null)
                _syncStatusBtn.ToolTip = tip;
        }

        private void UpdateTopSubContextText()
        {
            UpdateSidebarWatchText();
            if (_viewSubContextText == null) return;

            // 标题右侧只放「当前看的是什么」：钻取条件或页面自己的口径。扫描统计已移到侧栏状态区。
            string sub = "";
            if (_activeView == ViewInsights)
            {
                sub = "按实际付款记录，回本比例为估算";
            }
            else if (_activeView != ViewSettings && _activeView != ViewDoctor && _activeView != ViewProjects)
            {
                List<string> tags = new List<string>();
                if (_selectedDate != null)
                    tags.Add("单日 " + _selectedDate);
                if (_selectedTool != null)
                    tags.Add("工具 " + _selectedTool);
                sub = string.Join(" · ", tags.ToArray());
            }
            _viewSubContextText.Text = sub;
        }

        private static int FindPresetRangeIndex(int days)
        {
            for (int i = 0; i < PresetRangeDays.Length; i++)
            {
                if (PresetRangeDays[i] == days)
                    return i;
            }
            return -1;
        }

        private static bool ParseOnlineState(NodeSnapshot snap, out string statusLabel)
        {
            if (snap == null)
            {
                statusLabel = "未知";
                return false;
            }
            if (snap.IsLocal)
            {
                statusLabel = "本机实时";
                return true;
            }
            DateTime parsed;
            if (!string.IsNullOrWhiteSpace(snap.SyncedAt) &&
                DateTime.TryParseExact(
                    snap.SyncedAt.Trim(),
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out parsed))
            {
                double hours = (DateTime.Now - parsed).TotalHours;
                if (hours <= 24.0)
                {
                    statusLabel = "在线";
                    return true;
                }
                int days = (int)Math.Floor(hours / 24.0);
                statusLabel = days >= 2
                    ? string.Format(CultureInfo.InvariantCulture, "离线 ({0}天前)", days)
                    : "离线 (>24h)";
                return false;
            }
            statusLabel = "远端缓存";
            return true;
        }

        private void RefreshDashboardView()
        {
            if (_kpiCostVal == null) return;

            PricingEngine pricing = null;
            try { pricing = new PricingEngine(_homeDir); } catch { }

            // 0. 组装全集群节点列表（本机 + 远端缓存节点）并合并全量记录桶
            List<NodeSnapshot> allNodes = new List<NodeSnapshot>();
            if (_localNode != null)
            {
                Program.ApplyNodeAliasToSnapshot(_localNode, _syncCfg);
                allNodes.Add(_localNode);
            }
            else
            {
                NodeSnapshot placeholderLocal = new NodeSnapshot();
                placeholderLocal.MachineId = _syncCfg != null ? SyncService.SanitizeMachineId(_syncCfg.MachineId) : "win-local";
                placeholderLocal.MachineName = (_syncCfg != null && !string.IsNullOrWhiteSpace(_syncCfg.MachineName)) ? _syncCfg.MachineName : "本机";
                placeholderLocal.MachineType = "windows";
                placeholderLocal.OsInfo = Environment.OSVersion.VersionString;
                placeholderLocal.SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                placeholderLocal.IsLocal = true;
                Program.ApplyNodeAliasToSnapshot(placeholderLocal, _syncCfg);
                allNodes.Add(placeholderLocal);
            }

            if (_remoteNodes != null)
            {
                for (int i = 0; i < _remoteNodes.Count; i++)
                {
                    if (_remoteNodes[i] != null)
                    {
                        Program.ApplyNodeAliasToSnapshot(_remoteNodes[i], _syncCfg);
                        allNodes.Add(_remoteNodes[i]);
                    }
                }
            }

            if (_selectedMachineId != null)
            {
                bool foundSelected = false;
                for (int i = 0; i < allNodes.Count; i++)
                {
                    if (string.Equals(allNodes[i].MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase))
                    {
                        foundSelected = true;
                        break;
                    }
                }
                if (!foundSelected)
                    _selectedMachineId = null;
            }

            List<RecordBucket> allRecords = new List<RecordBucket>(256);
            string latestKnownDate = (_scanResult != null && !string.IsNullOrEmpty(_scanResult.ReferenceDate))
                ? _scanResult.ReferenceDate
                : "";
            string todayDateStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            double globalTodayUsd = 0.0;
            long globalTodayTokens = 0L;

            for (int n = 0; n < allNodes.Count; n++)
            {
                NodeSnapshot node = allNodes[n];
                if (node.Records == null) continue;
                for (int r = 0; r < node.Records.Count; r++)
                {
                    RecordBucket rb = node.Records[r];
                    if (rb == null) continue;
                    rb.MachineId = node.MachineId;
                    rb.MachineName = node.MachineName;
                    rb.MachineType = node.MachineType;
                    allRecords.Add(rb);

                    if (!string.IsNullOrEmpty(rb.Date) && rb.Date != "未知日期" && string.CompareOrdinal(rb.Date, latestKnownDate) > 0)
                        latestKnownDate = rb.Date;

                    if (string.Equals(rb.Date, todayDateStr, StringComparison.Ordinal))
                    {
                        globalTodayUsd += rb.CostUsd;
                        globalTodayTokens += rb.TotalTokens;
                    }
                }
            }

            _latestExportBuckets = allRecords;
            if (string.IsNullOrEmpty(latestKnownDate))
                latestKnownDate = todayDateStr;

            // 更新系统托盘悬浮提示与菜单（带 <=60 字符截断保护）
            UpdateSystemTrayStats(globalTodayUsd, globalTodayTokens);

            // 更新顶栏 22px 无跳动内联告警胶囊（目录访问告警）
            int dirWarnCount = (_scanResult != null && _scanResult.Warnings != null) ? _scanResult.Warnings.Count : 0;
            if (_inlineAlertBadge != null && _inlineAlertText != null)
            {
                if (dirWarnCount > 0)
                {
                    _inlineAlertText.Text = string.Format(CultureInfo.InvariantCulture, "诊断提示 ({0})", dirWarnCount);
                    _inlineAlertBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    _inlineAlertBadge.Visibility = Visibility.Collapsed;
                }
            }

            // 1. 时间范围过滤（基于自然日 DateTime.Now.Date，避免历史锚点偏移）
            DateTime cutoffDate = DateTime.MinValue;
            string cutoffStr = null;
            if (_rangeDays > 0)
            {
                cutoffDate = DateTime.Now.Date.AddDays(-(_rangeDays - 1));
                cutoffStr = cutoffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            List<RecordBucket> timeFiltered = new List<RecordBucket>(allRecords.Count);
            for (int i = 0; i < allRecords.Count; i++)
            {
                RecordBucket rb = allRecords[i];
                if (cutoffStr == null)
                {
                    timeFiltered.Add(rb);
                }
                else if (!string.IsNullOrEmpty(rb.Date) && rb.Date != "未知日期" && string.CompareOrdinal(rb.Date, cutoffStr) >= 0)
                {
                    timeFiltered.Add(rb);
                }
            }

            // 2. 构建设备聚合视图列表 MachineRowView（供顶栏选择器、侧栏徽章、概览按设备排行与视图 3 集群卡片共用）
            string[] palette = DesignSystem.GetChartPalette(_isDark);
            List<MachineRowView> machineViews = new List<MachineRowView>(allNodes.Count);
            double sumMachineMetric = 0.0;
            double maxMachineMetric = 0.0;

            for (int i = 0; i < allNodes.Count; i++)
            {
                NodeSnapshot ns = allNodes[i];
                MachineRowView mv = new MachineRowView();
                mv.MachineId = ns.MachineId;
                mv.MachineName = string.IsNullOrWhiteSpace(ns.MachineName) ? ns.MachineId : ns.MachineName;
                bool isVps = string.Equals(ns.MachineType, "vps", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ns.MachineType, "linux", StringComparison.OrdinalIgnoreCase);
                mv.TypeBadge = ns.IsLocal ? "Windows 本机" : (isVps ? "Linux VPS" : "远端电脑");
                mv.OsInfo = string.IsNullOrWhiteSpace(ns.OsInfo) ? (isVps ? "Linux" : "Windows") : ns.OsInfo;
                string statusLbl;
                mv.IsOnline = ParseOnlineState(ns, out statusLbl);
                mv.StatusText = statusLbl;
                mv.SyncedAt = ns.IsLocal ? "实时增量同步" : (string.IsNullOrWhiteSpace(ns.SyncedAt) ? "—" : ns.SyncedAt);
                mv.IsLocal = ns.IsLocal;
                mv.ColorHex = palette[i % palette.Length];

                HashSet<string> toolsOnMachine = new HashSet<string>(StringComparer.Ordinal);
                for (int r = 0; r < timeFiltered.Count; r++)
                {
                    RecordBucket rb = timeFiltered[r];
                    if (!string.Equals(rb.MachineId, ns.MachineId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    mv.Turns += rb.Turns;
                    mv.FreshInput += rb.FreshInput;
                    mv.CacheRead += rb.CacheRead;
                    mv.Output += rb.Output;
                    mv.TotalTokens += rb.TotalTokens;
                    mv.CostUsd += rb.CostUsd;
                    if (!string.IsNullOrEmpty(rb.Tool) && rb.TotalTokens > 0)
                        toolsOnMachine.Add(rb.Tool);
                }

                mv.ToolCount = toolsOnMachine.Count;
                if (toolsOnMachine.Count == 0)
                {
                    mv.ToolsSummary = "暂无时间段内记录";
                }
                else
                {
                    List<string> tNames = new List<string>(toolsOnMachine);
                    tNames.Sort(StringComparer.OrdinalIgnoreCase);
                    mv.ToolsSummary = tNames.Count <= 3
                        ? string.Join(", ", tNames.ToArray())
                        : string.Format(CultureInfo.InvariantCulture, "{0}, {1} 等 {2} 款", tNames[0], tNames[1], tNames.Count);
                }

                mv.CostCny = mv.CostUsd * _usdToCny;
                mv.FreshInputText = FormatterUtil.FormatTokens(mv.FreshInput);
                mv.CacheReadText = FormatterUtil.FormatTokens(mv.CacheRead);
                mv.OutputText = FormatterUtil.FormatTokens(mv.Output);
                mv.TotalTokensText = FormatterUtil.FormatTokens(mv.TotalTokens);
                mv.UsdText = FormatterUtil.FormatMoney(mv.CostUsd, false, _usdToCny);
                mv.CnyText = FormatterUtil.FormatMoney(mv.CostUsd, true, _usdToCny);
                mv.FormattedMoney = FormatterUtil.FormatMoney(mv.CostUsd, _isCny, _usdToCny);

                double metricVal = _rankByCost ? mv.CostUsd : (double)mv.TotalTokens;
                sumMachineMetric += metricVal;
                if (metricVal > maxMachineMetric) maxMachineMetric = metricVal;

                machineViews.Add(mv);
            }

            machineViews.Sort(delegate(MachineRowView a, MachineRowView b)
            {
                if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
                return b.CostUsd.CompareTo(a.CostUsd);
            });

            for (int i = 0; i < machineViews.Count; i++)
            {
                MachineRowView mv = machineViews[i];
                double m = _rankByCost ? mv.CostUsd : (double)mv.TotalTokens;
                mv.SharePercent = sumMachineMetric > 0.0 ? (m * 100.0 / sumMachineMetric) : 0.0;
                mv.ShareText = mv.SharePercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
                mv.BarRatio = maxMachineMetric > 0.0 ? Math.Max(0.02, m / maxMachineMetric) : 0.0;
            }
            _latestMachineViews = machineViews;

            // 更新侧边栏节点数徽章与顶栏设备选择胶囊
            if (_navDeviceCountText != null)
                _navDeviceCountText.Text = machineViews.Count.ToString(CultureInfo.InvariantCulture);

            if (_deviceSelectorText != null && _deviceSelectorDot != null)
            {
                if (_selectedMachineId == null)
                {
                    _deviceSelectorText.Text = string.Format(CultureInfo.InvariantCulture, "全部设备 ({0})", machineViews.Count);
                    _deviceSelectorDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticSuccess");
                }
                else
                {
                    for (int i = 0; i < machineViews.Count; i++)
                    {
                        if (string.Equals(machineViews[i].MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase))
                        {
                            _deviceSelectorText.Text = machineViews[i].MachineName;
                            _deviceSelectorDot.SetResourceReference(Shapes.Shape.FillProperty, machineViews[i].IsOnline ? "SemanticSuccess" : "SemanticWarning");
                            break;
                        }
                    }
                }
            }

            // 3. 设备范围过滤
            List<RecordBucket> machineFiltered = new List<RecordBucket>(timeFiltered.Count);
            for (int i = 0; i < timeFiltered.Count; i++)
            {
                RecordBucket rb = timeFiltered[i];
                if (_selectedMachineId == null || string.Equals(rb.MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase))
                    machineFiltered.Add(rb);
            }

            // 4. 构建 AI 工具排行（当有单日钻取 _selectedDate 时，反映该日工具构成）
            Dictionary<string, ToolRowView> toolMap = new Dictionary<string, ToolRowView>(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> toolModels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < machineFiltered.Count; i++)
            {
                RecordBucket r = machineFiltered[i];
                if (_selectedDate != null && !string.Equals(r.Date, _selectedDate, StringComparison.Ordinal))
                    continue;
                if (r.TotalTokens <= 0) continue;

                ToolRowView tv;
                if (!toolMap.TryGetValue(r.Tool, out tv))
                {
                    tv = new ToolRowView();
                    tv.Tool = r.Tool;
                    toolMap[r.Tool] = tv;
                    toolModels[r.Tool] = new HashSet<string>(StringComparer.Ordinal);
                }
                tv.Turns += r.Turns;
                tv.FreshInput += r.FreshInput;
                tv.CacheRead += r.CacheRead;
                tv.CacheWrite += r.CacheWrite;
                tv.Output += r.Output;
                tv.TotalTokens += r.TotalTokens;
                tv.CostUsd += r.CostUsd;
                if (!string.IsNullOrEmpty(r.Model))
                    toolModels[r.Tool].Add(r.Model);
            }

            if (_selectedTool != null && !toolMap.ContainsKey(_selectedTool))
                _selectedTool = null;

            List<ToolRowView> toolList = new List<ToolRowView>(toolMap.Values);
            toolList.Sort(delegate(ToolRowView a, ToolRowView b)
            {
                return _rankByCost ? b.CostUsd.CompareTo(a.CostUsd) : b.TotalTokens.CompareTo(a.TotalTokens);
            });

            double sumToolMetric = 0.0;
            double maxToolMetric = 0.0;
            for (int i = 0; i < toolList.Count; i++)
            {
                ToolRowView t = toolList[i];
                t.ModelCount = toolModels[t.Tool].Count;
                t.FormattedMoney = FormatterUtil.FormatMoney(t.CostUsd, _isCny, _usdToCny);
                t.FormattedTokens = FormatterUtil.FormatTokens(t.TotalTokens);
                t.ColorHex = DesignSystem.GetProviderColorHex(t.Tool, _isDark);
                double m = _rankByCost ? t.CostUsd : (double)t.TotalTokens;
                sumToolMetric += m;
                if (m > maxToolMetric) maxToolMetric = m;
            }
            for (int i = 0; i < toolList.Count; i++)
            {
                ToolRowView t = toolList[i];
                double m = _rankByCost ? t.CostUsd : (double)t.TotalTokens;
                t.SharePercent = sumToolMetric > 0.0 ? (m * 100.0 / sumToolMetric) : 0.0;
                t.BarRatio = maxToolMetric > 0.0 ? Math.Max(0.02, m / maxToolMetric) : 0.0;
            }

            // 5. 工具过滤（用于构造完整趋势图序列，保留上下文并支持高亮 _selectedDate）
            List<RecordBucket> chartSourceRows = new List<RecordBucket>(machineFiltered.Count);
            for (int i = 0; i < machineFiltered.Count; i++)
            {
                RecordBucket r = machineFiltered[i];
                if (_selectedTool == null || string.Equals(r.Tool, _selectedTool, StringComparison.Ordinal))
                    chartSourceRows.Add(r);
            }

            // 选「今日」时趋势图只有一根柱子，没有参照意义：趋势图改看近 14 天（KPI 与表格仍只算今日）。
            // 不按小时分桶，是因为单笔请求只保留最近 300 条且远端设备只同步按日汇总，分时数据会不完整。
            bool todayTrendMode = (_rangeDays == 1 && _selectedDate == null);
            List<RecordBucket> trendRows = chartSourceRows;
            if (todayTrendMode)
            {
                string trendStart = DateTime.Now.Date.AddDays(-13).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                trendRows = new List<RecordBucket>();
                for (int i = 0; i < allRecords.Count; i++)
                {
                    RecordBucket r = allRecords[i];
                    if (r == null || string.IsNullOrEmpty(r.Date) || string.CompareOrdinal(r.Date, trendStart) < 0) continue;
                    if (_selectedMachineId != null && !string.Equals(r.MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_selectedTool != null && !string.Equals(r.Tool, _selectedTool, StringComparison.Ordinal)) continue;
                    trendRows.Add(r);
                }
            }
            if (_trendTitleText != null)
                _trendTitleText.Text = todayTrendMode ? "近 14 天消耗趋势" : "每日消耗趋势";

            Dictionary<string, DailyRowView> dailyMap = new Dictionary<string, DailyRowView>(StringComparer.Ordinal);
            for (int i = 0; i < trendRows.Count; i++)
            {
                RecordBucket r = trendRows[i];
                string dt = string.IsNullOrEmpty(r.Date) ? "未知日期" : r.Date;
                DailyRowView dv;
                if (!dailyMap.TryGetValue(dt, out dv))
                {
                    dv = new DailyRowView();
                    dv.Date = dt;
                    dailyMap[dt] = dv;
                }
                dv.Turns += r.Turns;
                dv.FreshInput += r.FreshInput;
                dv.CacheRead += r.CacheRead;
                dv.CacheWrite += r.CacheWrite;
                dv.Output += r.Output;
                dv.TotalTokens += r.TotalTokens;
                dv.CostUsd += r.CostUsd;
            }

            List<DailyRowView> dailyDesc = new List<DailyRowView>(dailyMap.Values);
            dailyDesc.Sort(delegate(DailyRowView a, DailyRowView b)
            {
                return string.CompareOrdinal(b.Date, a.Date);
            });

            _currentDailyAsc = new List<DailyRowView>();
            for (int i = dailyDesc.Count - 1; i >= 0; i--)
            {
                DailyRowView d = dailyDesc[i];
                d.CostCny = d.CostUsd * _usdToCny;
                d.FreshInputText = FormatterUtil.FormatTokens(d.FreshInput);
                d.CacheReadText = FormatterUtil.FormatTokens(d.CacheRead);
                d.CacheWriteText = FormatterUtil.FormatTokens(d.CacheWrite);
                d.OutputText = FormatterUtil.FormatTokens(d.Output);
                d.TotalTokensText = FormatterUtil.FormatTokens(d.TotalTokens);
                d.UsdText = FormatterUtil.FormatMoney(d.CostUsd, false, _usdToCny);
                d.CnyText = FormatterUtil.FormatMoney(d.CostUsd, true, _usdToCny);
                d.FormattedCost = FormatterUtil.FormatMoney(d.CostUsd, _isCny, _usdToCny);
                if (d.Date != "未知日期")
                    _currentDailyAsc.Add(d);
            }

            // 6. 单日钻取过滤后的活跃行（驱动 KPI、模型账单表）
            List<RecordBucket> activeRows = new List<RecordBucket>(chartSourceRows.Count);
            for (int i = 0; i < chartSourceRows.Count; i++)
            {
                if (_selectedDate == null || string.Equals(chartSourceRows[i].Date, _selectedDate, StringComparison.Ordinal))
                    activeRows.Add(chartSourceRows[i]);
            }

            int totTurns = 0;
            long totFi = 0L, totCr = 0L, totCw = 0L, totOut = 0L;
            double totCostUsd = 0.0, totBaseUsd = 0.0;
            double scopedTodayUsd = 0.0;
            double recent7dUsd = 0.0;
            string sevenDaysAgoStr = DateTime.Now.Date.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            for (int i = 0; i < machineFiltered.Count; i++)
            {
                RecordBucket r = machineFiltered[i];
                if (_selectedTool != null && !string.Equals(r.Tool, _selectedTool, StringComparison.Ordinal))
                    continue;
                if (string.Equals(r.Date, todayDateStr, StringComparison.Ordinal))
                    scopedTodayUsd += r.CostUsd;
            }

            // 基于当前设备视角的全时间段记录计算近 7 日平均与当月（30天）预测
            for (int i = 0; i < allRecords.Count; i++)
            {
                RecordBucket r = allRecords[i];
                if (_selectedMachineId != null && !string.Equals(r.MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_selectedTool != null && !string.Equals(r.Tool, _selectedTool, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(r.Date) && r.Date != "未知日期" && string.CompareOrdinal(r.Date, sevenDaysAgoStr) >= 0)
                    recent7dUsd += r.CostUsd;
            }

            Dictionary<string, ModelRowView> modelMap = new Dictionary<string, ModelRowView>(StringComparer.Ordinal);
            HashSet<string> uniqueModels = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < activeRows.Count; i++)
            {
                RecordBucket r = activeRows[i];
                totTurns += r.Turns;
                totFi += r.FreshInput;
                totCr += r.CacheRead;
                totCw += r.CacheWrite;
                totOut += r.Output;
                totCostUsd += r.CostUsd;
                totBaseUsd += r.BaselineUsd;
                if (!string.IsNullOrEmpty(r.Model))
                    uniqueModels.Add(r.Model);

                string mKey = (r.Tool ?? "") + "||" + (r.Model ?? "");
                ModelRowView mv;
                if (!modelMap.TryGetValue(mKey, out mv))
                {
                    mv = new ModelRowView();
                    mv.Tool = r.Tool ?? "未知工具";
                    mv.Model = r.Model ?? "unknown";
                    mv.RateLabel = r.RateLabel ?? "";
                    if (pricing != null)
                    {
                        ModelRate mr = pricing.Lookup(mv.Model);
                        mv.InputPerM = mr.InputPerM;
                        if (string.IsNullOrWhiteSpace(mv.RateLabel))
                            mv.RateLabel = mr.Label;
                    }
                    modelMap[mKey] = mv;
                }
                mv.Turns += r.Turns;
                mv.FreshInput += r.FreshInput;
                mv.CacheRead += r.CacheRead;
                mv.CacheWrite += r.CacheWrite;
                mv.Output += r.Output;
                mv.TotalTokens += r.TotalTokens;
                mv.CostUsd += r.CostUsd;
            }

            // 7. 更新 4 张 Bento KPI 卡片
            long totAllTokens = totFi + totCr + totCw + totOut;
            long totPromptTokens = totFi + totCr + totCw;
            double cacheHitPct = totPromptTokens > 0 ? (totCr * 100.0 / totPromptTokens) : 0.0;
            double savedUsd = Math.Max(0.0, totBaseUsd - totCostUsd);
            double savedPct = totBaseUsd > 0.0001 ? Math.Min(99.9, savedUsd * 100.0 / totBaseUsd) : 0.0;

            _kpiCostVal.Text = FormatterUtil.FormatMoney(totCostUsd, _isCny, _usdToCny);
            _kpiTodayPillText.Text = "今日 " + FormatterUtil.FormatMoney(scopedTodayUsd, _isCny, _usdToCny);
            // 时间范围本身就是「今日」时，右上角的「今日 $X」与主数字重复，隐藏
            _kpiTodayPill.Visibility = (_rangeDays == 1 && _selectedDate == null) ? Visibility.Collapsed : Visibility.Visible;

            double avgDailyUsd = recent7dUsd / 7.0;
            double forecast30dUsd = avgDailyUsd * 30.0;
            _kpiCardCost.ToolTip = string.Format(
                CultureInfo.InvariantCulture,
                "等效账单与月度预测\n• 当前筛选合计：{0} ({1})\n• 今日实时消耗：{2}\n• 近 7 天日均：{3} / 天\n• 30 天滚动预测：{4}",
                FormatterUtil.FormatMoney(totCostUsd, true, _usdToCny),
                FormatterUtil.FormatMoney(totCostUsd, false, _usdToCny),
                FormatterUtil.FormatMoney(scopedTodayUsd, _isCny, _usdToCny),
                FormatterUtil.FormatMoney(avgDailyUsd, _isCny, _usdToCny),
                FormatterUtil.FormatMoney(forecast30dUsd, _isCny, _usdToCny));

            _kpiCostSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} 次请求 · {1} 个模型 · 月预测 {2}",
                totTurns,
                uniqueModels.Count,
                FormatterUtil.FormatMoney(forecast30dUsd, _isCny, _usdToCny));

            _kpiTokVal.Text = FormatterUtil.FormatTokens(totAllTokens);
            _kpiTokSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "新增 {0} · 缓存读 {1} · 输出 {2}",
                FormatterUtil.FormatTokens(totFi + totCw),
                FormatterUtil.FormatTokens(totCr),
                FormatterUtil.FormatTokens(totOut));

            if (totAllTokens > 0)
            {
                double rIn = Math.Max(0.04, (double)(totFi + totCw) / totAllTokens);
                double rCr = Math.Max(0.04, (double)totCr / totAllTokens);
                double rOut = Math.Max(0.04, (double)totOut / totAllTokens);
                _tokSegInputCol.Width = new GridLength(rIn, GridUnitType.Star);
                _tokSegCacheCol.Width = new GridLength(rCr, GridUnitType.Star);
                _tokSegOutputCol.Width = new GridLength(rOut, GridUnitType.Star);
            }
            else
            {
                _tokSegInputCol.Width = new GridLength(1, GridUnitType.Star);
                _tokSegCacheCol.Width = new GridLength(1, GridUnitType.Star);
                _tokSegOutputCol.Width = new GridLength(1, GridUnitType.Star);
            }

            _kpiHitVal.Text = cacheHitPct.ToString("F1", CultureInfo.InvariantCulture) + "%";
            _kpiHitSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "命中 {0} / 提示词总计 {1}",
                FormatterUtil.FormatTokens(totCr),
                FormatterUtil.FormatTokens(totPromptTokens));

            _kpiSaveVal.Text = FormatterUtil.FormatMoney(savedUsd, _isCny, _usdToCny);
            _kpiSavePctText.Text = string.Format(CultureInfo.InvariantCulture, "省 {0:F0}%", savedPct);
            _kpiSaveSub.Text = "无缓存基准 " + FormatterUtil.FormatMoney(totBaseUsd, _isCny, _usdToCny);

            // 8. 更新中部消耗排行与单日胶囊
            bool anyFilterActive = (_selectedTool != null || _selectedMachineId != null || _selectedDate != null);
            if (_clearFilterBtn != null)
                _clearFilterBtn.Visibility = anyFilterActive ? Visibility.Visible : Visibility.Collapsed;

            if (_selectedDatePill != null && _selectedDatePillText != null)
            {
                if (_selectedDate != null)
                {
                    _selectedDatePillText.Text = "已锁定: " + _selectedDate + " ×";
                    _selectedDatePill.Visibility = Visibility.Visible;
                }
                else
                {
                    _selectedDatePill.Visibility = Visibility.Collapsed;
                }
            }

            RenderOverviewRankingList(toolList, machineViews);

            // 9. 更新视图 2：模型明细列表与顶部多色占比条
            List<ModelRowView> allModels = new List<ModelRowView>(modelMap.Values);
            allModels.Sort(delegate(ModelRowView a, ModelRowView b)
            {
                return b.CostUsd.CompareTo(a.CostUsd);
            });

            double maxModelCost = 0.0;
            for (int i = 0; i < allModels.Count; i++)
            {
                if (allModels[i].CostUsd > maxModelCost)
                    maxModelCost = allModels[i].CostUsd;
            }

            List<ModelRowView> filteredModels = new List<ModelRowView>(allModels.Count);
            for (int i = 0; i < allModels.Count; i++)
            {
                ModelRowView m = allModels[i];
                m.FreshInputText = FormatterUtil.FormatTokens(m.FreshInput);
                m.CacheReadText = FormatterUtil.FormatTokens(m.CacheRead);
                m.CacheWriteText = FormatterUtil.FormatTokens(m.CacheWrite);
                m.OutputText = FormatterUtil.FormatTokens(m.Output);
                m.TotalTokensText = FormatterUtil.FormatTokens(m.TotalTokens);

                long mPrompt = m.FreshInput + m.CacheRead + m.CacheWrite;
                m.CacheHitText = mPrompt > 0
                    ? ((m.CacheRead * 100.0 / mPrompt).ToString("F1", CultureInfo.InvariantCulture) + "%")
                    : "—";

                m.DisplayCost = _isCny ? (m.CostUsd * _usdToCny) : m.CostUsd;
                m.FormattedCost = FormatterUtil.FormatMoney(m.CostUsd, _isCny, _usdToCny);
                double share = totCostUsd > 0.0 ? (m.CostUsd * 100.0 / totCostUsd) : 0.0;
                m.ShareText = share.ToString("F1", CultureInfo.InvariantCulture) + "%";
                m.BarRatio = maxModelCost > 0.0 ? Math.Max(0.02, m.CostUsd / maxModelCost) : 0.0;

                if (string.IsNullOrEmpty(_modelSearchQuery) ||
                    (m.Model != null && m.Model.IndexOf(_modelSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (m.Tool != null && m.Tool.IndexOf(_modelSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    filteredModels.Add(m);
                }
            }

            RenderModelStackedBar(allModels, totCostUsd);
            if (_modelCountSummaryText != null)
            {
                _modelCountSummaryText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} / {1} 个模型组合",
                    filteredModels.Count,
                    allModels.Count);
            }
            ModernDataGridFactory.UpdateItemsPreservingState(
                _modelGrid,
                filteredModels,
                delegate(object obj)
                {
                    ModelRowView mr = obj as ModelRowView;
                    return mr != null ? (mr.Tool + "||" + mr.Model) : "";
                },
                false);
            if (_modelEmptyOverlay != null)
                _modelEmptyOverlay.Visibility = filteredModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 10. 更新视图 3：设备集群卡片与表格
            RenderDeviceClusterCards(machineViews);
            ModernDataGridFactory.UpdateItemsPreservingState(
                _machineGrid,
                machineViews,
                delegate(object obj)
                {
                    MachineRowView mrv = obj as MachineRowView;
                    return mrv != null ? mrv.MachineId : "";
                },
                false);

            // 11. 更新视图 4：每日流水表（支持月份/日期搜索过滤）
            List<DailyRowView> filteredDaily = new List<DailyRowView>(dailyDesc.Count);
            double sumDailyFilteredUsd = 0.0;
            long sumDailyFilteredTok = 0L;
            for (int i = 0; i < dailyDesc.Count; i++)
            {
                DailyRowView d = dailyDesc[i];
                if (_selectedDate != null && !string.Equals(d.Date, _selectedDate, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(_dailySearchQuery) &&
                    (d.Date == null || d.Date.IndexOf(_dailySearchQuery, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                filteredDaily.Add(d);
                sumDailyFilteredUsd += d.CostUsd;
                sumDailyFilteredTok += d.TotalTokens;
            }

            if (_dailyFilteredSummaryText != null)
            {
                _dailyFilteredSummaryText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "共 {0} 天 · 合计 {1} · {2} Tokens",
                    filteredDaily.Count,
                    FormatterUtil.FormatMoney(sumDailyFilteredUsd, _isCny, _usdToCny),
                    FormatterUtil.FormatTokens(sumDailyFilteredTok));
            }
            ModernDataGridFactory.UpdateItemsPreservingState(
                _dailyGrid,
                filteredDaily,
                delegate(object obj)
                {
                    DailyRowView dr = obj as DailyRowView;
                    return dr != null ? dr.Date : "";
                },
                false);
            if (_dailyEmptyOverlay != null)
                _dailyEmptyOverlay.Visibility = filteredDaily.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 12. 更新视图 0 快览流与视图 1 实时请求流（同时刷新右侧单笔透视抽屉币种）
            if (_requestInspector != null)
                _requestInspector.SetCurrency(_isCny, _usdToCny);
            RefreshLiveRequestGrids(cutoffStr);

            // 13. 缓存专项分析视图所需数据集并按需同步当前活跃视图
            List<RecordBucket> fullHistoryMachineRows = new List<RecordBucket>(allRecords.Count);
            List<RecordBucket> fullHistoryScopedRows = new List<RecordBucket>(allRecords.Count);
            for (int i = 0; i < allRecords.Count; i++)
            {
                RecordBucket rb = allRecords[i];
                if (rb == null) continue;
                if (_selectedMachineId != null && !string.Equals(rb.MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase))
                    continue;
                fullHistoryMachineRows.Add(rb);
                if (_selectedTool == null || string.Equals(rb.Tool, _selectedTool, StringComparison.Ordinal))
                    fullHistoryScopedRows.Add(rb);
            }
            _latestFullHistoryMachineRows = fullHistoryMachineRows;
            _latestAllHistoryRows = allRecords;
            _latestFullHistoryScopedRows = fullHistoryScopedRows;
            _latestSpecialistMachineRows = machineFiltered;
            _latestSpecialistActiveRows = activeRows;
            _latestReferenceDate = latestKnownDate;

            HashSet<string> allKnownModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < allRecords.Count; i++)
            {
                if (allRecords[i] != null && !string.IsNullOrWhiteSpace(allRecords[i].Model))
                    allKnownModelIds.Add(allRecords[i].Model.Trim());
            }
            _latestUniqueModelNames = new List<string>(allKnownModelIds);

            RefreshActiveSpecialistView(true);

            // 14. 同步桌面悬浮球与异常燃烧速率提醒
            if (_miniHud != null)
            {
                RefreshMiniHud();
            }

            if (!_skipAutoScan && _scanResult != null)
            {
                try
                {
                    List<BurnRateAlert> alerts = _burnGuard.Evaluate(
                        allRecords,
                        _scanResult.RecentRequests,
                        _isCny,
                        _usdToCny);
                    if (alerts != null)
                    {
                        for (int a = 0; a < alerts.Count; a++)
                        {
                            BurnRateAlert ev = alerts[a];
                            if (ev != null)
                            {
                                ShowToastSafe(ev.Title, ev.Message, ev.Variant);
                                _hudAlertUntilUtc = DateTime.UtcNow.AddMinutes(10);
                                _hudAlertText = ev.Title + "：" + ev.Message;
                                RefreshMiniHud();
                            }
                        }
                    }
                }
                catch { }
            }

            // 15. 更新顶栏副文案与重绘趋势图
            UpdateTopSubContextText();
            RedrawChart();
        }

        private static void SetTimingColumnsVisible(DataGrid dg, bool visible)
        {
            if (dg == null) return;
            foreach (DataGridColumn c in dg.Columns)
            {
                if (c.SortMemberPath == "DurationMs" || c.SortMemberPath == "Tps")
                    c.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshLiveRequestGrids(string cutoffDateStr)
        {
            string localMid = _localNode != null ? _localNode.MachineId : (_syncCfg != null ? _syncCfg.MachineId : "");
            bool viewingRemoteOnly = (_selectedMachineId != null && !string.Equals(_selectedMachineId, localMid, StringComparison.OrdinalIgnoreCase));

            List<LiveRequestRowView> allLiveRows = new List<LiveRequestRowView>();
            List<LiveRequestRowView> overviewPreviewRows = new List<LiveRequestRowView>();

            if (!viewingRemoteOnly && _scanResult != null && _scanResult.RecentRequests != null)
            {
                string todayDateStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                List<RecentRequestRecord> rawList = _scanResult.RecentRequests;
                for (int i = 0; i < rawList.Count; i++)
                {
                    RecentRequestRecord rq = rawList[i];
                    if (rq == null) continue;
                    if (cutoffDateStr != null && !string.IsNullOrEmpty(rq.Date) && string.CompareOrdinal(rq.Date, cutoffDateStr) < 0)
                        continue;
                    if (_selectedDate != null && !string.Equals(rq.Date, _selectedDate, StringComparison.Ordinal))
                        continue;
                    if (_selectedTool != null && !string.Equals(rq.Tool, _selectedTool, StringComparison.Ordinal))
                        continue;

                    string formattedTime = rq.TimeLabel ?? "";
                    if (string.Equals(rq.Date, todayDateStr, StringComparison.Ordinal))
                    {
                        int spIdx = formattedTime.IndexOf(' ');
                        if (spIdx >= 0 && spIdx + 1 < formattedTime.Length)
                            formattedTime = formattedTime.Substring(spIdx + 1);
                    }

                    int measuredDuration=UsageTiming.IsMeasured(rq.TimingSource)?rq.DurationMs:0;
                    double tps = FormatterUtil.ComputeTps(rq.Output, measuredDuration);
                    LiveRequestRowView row = new LiveRequestRowView();
                    row.TimestampMs = rq.TimestampMs;
                    row.Date = rq.Date;
                    row.TimeText = formattedTime;
                    row.Tool = rq.Tool;
                    row.Model = rq.Model;
                    row.FreshInput = rq.FreshInput;
                    row.FreshInputText = FormatterUtil.FormatTokens(rq.FreshInput);
                    row.CacheRead = rq.CacheRead;
                    row.CacheReadText = FormatterUtil.FormatTokens(rq.CacheRead);
                    row.CacheWrite = rq.CacheWrite;
                    row.CacheWriteText = FormatterUtil.FormatTokens(rq.CacheWrite);
                    row.Output = rq.Output;
                    row.OutputText = FormatterUtil.FormatTokens(rq.Output);
                    row.TotalTokens = rq.TotalTokens;
                    row.TotalTokensText = FormatterUtil.FormatTokens(rq.TotalTokens);
                    row.DurationMs = measuredDuration;
                    row.TimingSource = rq.TimingSource;
                    row.DurationText = FormatterUtil.FormatDuration(measuredDuration);
                    row.Tps = tps;
                    row.TpsText = FormatterUtil.FormatTps(tps);
                    row.CostUsd = rq.CostUsd;
                    row.DisplayCost = _isCny ? (rq.CostUsd * _usdToCny) : rq.CostUsd;
                    row.FormattedCost = FormatterUtil.FormatMoney(rq.CostUsd, _isCny, _usdToCny);

                    if (overviewPreviewRows.Count < 30)
                        overviewPreviewRows.Add(row);

                    if (string.IsNullOrEmpty(_liveSearchQuery) ||
                        (row.Model != null && row.Model.IndexOf(_liveSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (row.Tool != null && row.Tool.IndexOf(_liveSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (row.TimeText != null && row.TimeText.IndexOf(_liveSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (rq.TimeLabel != null && rq.TimeLabel.IndexOf(_liveSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (rq.Date != null && rq.Date.IndexOf(_liveSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        allLiveRows.Add(row);
                    }
                }
            }

            // 更新顶部最新一笔动态摘要与 HUD 微胶囊模型速率文案
            if (overviewPreviewRows.Count > 0)
            {
                LiveRequestRowView top = overviewPreviewRows[0];
                // 没有可靠计时的请求不显示速率占位「—」
                bool hasTps = top.DurationMs > 0 && !string.IsNullOrEmpty(top.TpsText) && top.TpsText != "—";
                string summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "最新 {0} · {1} ({2}) · 输出 {3}{4} · {5}",
                    top.TimeText,
                    top.Tool,
                    top.Model,
                    top.OutputText,
                    hasTps ? " · " + top.TpsText : "",
                    top.FormattedCost);
                if (_overviewLiveTickerText != null) _overviewLiveTickerText.Text = summary;
                if (_liveStreamSummaryText != null) _liveStreamSummaryText.Text = summary;

            }
            else
            {
                string fallback = viewingRemoteOnly
                    ? "当前已锁定远端节点（云同步仅传输按日聚合脱敏账单，不含单笔请求时间戳）"
                    : "本机日志监听中 · 仅在日志提供可靠计时时显示请求均速";
                if (_overviewLiveTickerText != null) _overviewLiveTickerText.Text = fallback;
                if (_liveStreamSummaryText != null) _liveStreamSummaryText.Text = fallback;
            }

            if (_liveClearFilterBtn != null)
            {
                bool hasLiveFilter = (!string.IsNullOrEmpty(_liveSearchQuery) || _selectedTool != null || _selectedDate != null);
                _liveClearFilterBtn.Visibility = hasLiveFilter ? Visibility.Visible : Visibility.Collapsed;
            }

            ModernDataGridFactory.UpdateItemsPreservingState(
                _overviewLiveGrid,
                overviewPreviewRows,
                delegate(object obj)
                {
                    LiveRequestRowView lr = obj as LiveRequestRowView;
                    return lr != null ? (lr.TimestampMs.ToString(CultureInfo.InvariantCulture) + "|" + lr.Tool + "|" + lr.Model + "|" + lr.TotalTokens.ToString(CultureInfo.InvariantCulture)) : "";
                },
                true);
            if (_overviewLiveEmpty != null)
                _overviewLiveEmpty.Visibility = overviewPreviewRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateLivePauseButtonVisual();
            if (!_livePaused)
            {
                // 列表里没有任何一条带可靠计时时，「耗时 / 速率」整列都是「—」，直接隐藏
                bool anyTiming = false;
                foreach (LiveRequestRowView lr in allLiveRows)
                {
                    if (lr.DurationMs > 0) { anyTiming = true; break; }
                }
                SetTimingColumnsVisible(_liveGrid, anyTiming);

                ModernDataGridFactory.UpdateItemsPreservingState(
                    _liveGrid,
                    allLiveRows,
                    delegate(object obj)
                    {
                        LiveRequestRowView lr = obj as LiveRequestRowView;
                        return lr != null ? (lr.TimestampMs.ToString(CultureInfo.InvariantCulture) + "|" + lr.Tool + "|" + lr.Model + "|" + lr.TotalTokens.ToString(CultureInfo.InvariantCulture)) : "";
                    },
                    true);
                if (_liveEmptyOverlay != null)
                {
                    if (_liveEmptyTitle != null)
                        _liveEmptyTitle.Text = viewingRemoteOnly ? "远端节点仅同步按日脱敏汇总账单" : "暂无匹配的实时单笔请求记录";
                    _liveEmptyOverlay.Visibility = allLiveRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void RenderOverviewRankingList(List<ToolRowView> tools, List<MachineRowView> machines)
        {
            if (_toolsStack == null) return;
            _toolsStack.Children.Clear();

            if (_leftRankByMachine)
            {
                if (machines == null || machines.Count == 0)
                {
                    _toolsStack.Children.Add(MakeMutedHintRow("暂无多设备账单数据"));
                    return;
                }

                for (int i = 0; i < machines.Count; i++)
                {
                    MachineRowView mv = machines[i];
                    bool isSel = (_selectedMachineId != null && string.Equals(_selectedMachineId, mv.MachineId, StringComparison.OrdinalIgnoreCase));
                    string rightVal = _rankByCost ? mv.FormattedMoney : mv.TotalTokensText;

                    Border item = CreateInteractiveRankRow(
                        mv.MachineName,
                        mv.TypeBadge,
                        rightVal,
                        mv.ShareText,
                        mv.BarRatio,
                        mv.ColorHex,
                        isSel,
                        delegate()
                        {
                            _selectedMachineId = isSel ? null : mv.MachineId;
                            RefreshDashboardView();
                        });
                    _toolsStack.Children.Add(item);
                }
            }
            else
            {
                if (tools == null || tools.Count == 0)
                {
                    _toolsStack.Children.Add(MakeMutedHintRow("当前时间与设备范围内暂无活跃工具记录"));
                    return;
                }

                for (int i = 0; i < tools.Count; i++)
                {
                    ToolRowView tv = tools[i];
                    bool isSel = (_selectedTool != null && string.Equals(_selectedTool, tv.Tool, StringComparison.Ordinal));
                    string rightVal = _rankByCost ? tv.FormattedMoney : tv.FormattedTokens;
                    string pctTxt = tv.SharePercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
                    string subBadge = string.Format(CultureInfo.InvariantCulture, "{0} 模型", tv.ModelCount);

                    Border item = CreateInteractiveRankRow(
                        tv.Tool,
                        subBadge,
                        rightVal,
                        pctTxt,
                        tv.BarRatio,
                        tv.ColorHex,
                        isSel,
                        delegate()
                        {
                            _selectedTool = isSel ? null : tv.Tool;
                            RefreshDashboardView();
                        });
                    _toolsStack.Children.Add(item);
                }
            }
        }

        private Border CreateInteractiveRankRow(
            string name,
            string subBadge,
            string valueText,
            string shareText,
            double barRatio,
            string dotHex,
            bool isSelected,
            Action onClick)
        {
            Border bd = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 2, 4),
                Cursor = Cursors.Hand
            };

            if (isSelected)
            {
                bd.SetResourceReference(Border.BackgroundProperty, "BgSelected");
                bd.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            }
            else
            {
                bd.Background = Brushes.Transparent;
                bd.BorderBrush = Brushes.Transparent;
            }

            bd.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (!isSelected)
                {
                    bd.SetResourceReference(Border.BackgroundProperty, "BgHover");
                    bd.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                }
            };
            bd.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                if (!isSelected)
                {
                    bd.Background = Brushes.Transparent;
                    bd.BorderBrush = Brushes.Transparent;
                }
            };
            bd.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (onClick != null) onClick();
            };

            StackPanel sp = new StackPanel();
            DockPanel topRow = new DockPanel();

            StackPanel rightSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock valTb = new TextBlock
            {
                Text = valueText,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            valTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            rightSp.Children.Add(valTb);

            TextBlock pctTb = new TextBlock
            {
                Text = shareText,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Width = 44,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            rightSp.Children.Add(pctTb);

            DockPanel.SetDock(rightSp, Dock.Right);
            topRow.Children.Add(rightSp);

            StackPanel leftSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Ellipse dot = new Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = DesignSystem.BrushHex(dotHex),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            leftSp.Children.Add(dot);

            TextBlock nameTb = new TextBlock
            {
                Text = name,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                MaxWidth = 148,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            leftSp.Children.Add(nameTb);

            if (!string.IsNullOrEmpty(subBadge))
            {
                TextBlock badgeTb = new TextBlock
                {
                    Text = subBadge,
                    FontSize = DesignSystem.TextCaption,
                    Margin = new Thickness(6, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badgeTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                leftSp.Children.Add(badgeTb);
            }

            topRow.Children.Add(leftSp);
            sp.Children.Add(topRow);

            Grid barTrack = new Grid { Height = 3.5, Margin = new Thickness(14, 5, 0, 0) };
            barTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, barRatio), GridUnitType.Star) });
            barTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1.0 - barRatio), GridUnitType.Star) });

            Border bgRail = new Border { CornerRadius = new CornerRadius(1.8) };
            bgRail.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
            Grid.SetColumnSpan(bgRail, 2);
            barTrack.Children.Add(bgRail);

            Border fgFill = new Border
            {
                CornerRadius = new CornerRadius(1.8),
                Background = DesignSystem.BrushHex(dotHex)
            };
            Grid.SetColumn(fgFill, 0);
            barTrack.Children.Add(fgFill);

            sp.Children.Add(barTrack);
            bd.Child = sp;
            return bd;
        }

        private static TextBlock MakeMutedHintRow(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontSize = DesignSystem.TextBody,
                Margin = new Thickness(8, 22, 8, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            return tb;
        }

        private void RenderModelStackedBar(List<ModelRowView> models, double totalCostUsd)
        {
            if (_modelStackedBarGrid == null || _modelLegendWrap == null) return;
            _modelStackedBarGrid.Children.Clear();
            _modelStackedBarGrid.ColumnDefinitions.Clear();
            _modelLegendWrap.Children.Clear();

            if (models == null || models.Count == 0 || totalCostUsd <= 0.0)
            {
                _modelLegendWrap.Children.Add(new TextBlock
                {
                    Text = "暂无模型账单占比数据",
                    FontSize = DesignSystem.TextCaption,
                    Foreground = DesignSystem.ThemeBrush("FgMuted", _isDark)
                });
                return;
            }

            string[] palette = DesignSystem.GetChartPalette(_isDark);
            int topCount = Math.Min(6, models.Count);
            double coveredUsd = 0.0;

            for (int i = 0; i < topCount; i++)
            {
                ModelRowView m = models[i];
                double ratio = Math.Max(0.005, m.CostUsd / totalCostUsd);
                coveredUsd += m.CostUsd;
                string hex = DesignSystem.GetProviderColorHex(m.Model + " " + m.Tool, _isDark);
                if (i > 0 && i < palette.Length && string.Equals(hex, DesignSystem.GetProviderColorHex(models[i - 1].Model + " " + models[i - 1].Tool, _isDark), StringComparison.OrdinalIgnoreCase))
                {
                    hex = palette[i % palette.Length];
                }

                _modelStackedBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
                Border seg = new Border
                {
                    Background = DesignSystem.BrushHex(hex),
                    Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0),
                    ToolTip = m.Model + " (" + m.Tool + ") · " + m.FormattedCost + " · " + m.ShareText
                };
                Grid.SetColumn(seg, i);
                _modelStackedBarGrid.Children.Add(seg);

                _modelLegendWrap.Children.Add(MakeLegendChip(hex, m.Model, m.ShareText));
            }

            double restUsd = Math.Max(0.0, totalCostUsd - coveredUsd);
            if (models.Count > topCount && restUsd > 0.0001)
            {
                double restRatio = Math.Max(0.01, restUsd / totalCostUsd);
                _modelStackedBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(restRatio, GridUnitType.Star) });
                Border otherSeg = new Border
                {
                    Background = DesignSystem.ThemeBrush("ChartOther", _isDark),
                    Margin = new Thickness(1.5, 0, 0, 0)
                };
                Grid.SetColumn(otherSeg, topCount);
                _modelStackedBarGrid.Children.Add(otherSeg);

                string restPct = (restUsd * 100.0 / totalCostUsd).ToString("F1", CultureInfo.InvariantCulture) + "%";
                _modelLegendWrap.Children.Add(MakeLegendChip(DesignSystem.ThemeHex("ChartOther", _isDark), string.Format(CultureInfo.InvariantCulture, "其余 {0} 个模型", models.Count - topCount), restPct));
            }
        }

        private static UIElement MakeLegendChip(string colorHex, string label, string pct)
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 14, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(new Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = DesignSystem.BrushHex(colorHex),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            TextBlock nameTb = new TextBlock
            {
                Text = label,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(nameTb);

            TextBlock pctTb = new TextBlock
            {
                Text = pct,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(4, 0, 0, 0)
            };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(pctTb);
            return sp;
        }

        private void RenderDeviceClusterCards(List<MachineRowView> machines)
        {
            if (_deviceCardsStack == null) return;
            _deviceCardsStack.Children.Clear();

            if (machines != null)
            {
                for (int i = 0; i < machines.Count; i++)
                {
                    MachineRowView mv = machines[i];
                    bool isSelected = (_selectedMachineId != null && string.Equals(_selectedMachineId, mv.MachineId, StringComparison.OrdinalIgnoreCase));

                    Border card = ModernControlsFactory.CreateCard(new Thickness(13, 11, 13, 10));
                    card.Width = 238;
                    card.Margin = new Thickness(0, 0, 10, 2);
                    card.Cursor = Cursors.Hand;

                    if (isSelected)
                    {
                        card.SetResourceReference(Border.BackgroundProperty, "BgSelected");
                        card.SetResourceReference(Border.BorderBrushProperty, "BorderFocus");
                    }

                    StackPanel sp = new StackPanel();

                    // 顶行：OS 图标 + 设备名 + 在线胶囊
                    DockPanel top = new DockPanel();
                    TextBlock badgeLbl;
                    Shapes.Ellipse badgeDot;
                    Border statusPill = ModernControlsFactory.CreateStatusBadge(
                        mv.StatusText,
                        mv.IsOnline ? "success" : "warning",
                        out badgeLbl,
                        out badgeDot);
                    DockPanel.SetDock(statusPill, Dock.Right);
                    top.Children.Add(statusPill);

                    StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    bool isLinux = mv.TypeBadge != null && mv.TypeBadge.IndexOf("Linux", StringComparison.OrdinalIgnoreCase) >= 0;
                    Shapes.Path nodeIcon = UiIcons.CreateIcon(isLinux ? UiIcons.Terminal : UiIcons.Devices, 14.5, "AccentText");
                    nodeIcon.Margin = new Thickness(0, 0, 6, 0);
                    titleRow.Children.Add(nodeIcon);

                    TextBlock nameTb = new TextBlock
                    {
                        Text = mv.MachineName,
                        FontSize = DesignSystem.TextLabel,
                        FontWeight = FontWeights.SemiBold,
                        MaxWidth = 112,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                    titleRow.Children.Add(nameTb);
                    top.Children.Add(titleRow);
                    sp.Children.Add(top);

                    // OS 与工具信息
                    TextBlock osTb = new TextBlock
                    {
                        Text = mv.OsInfo + " · " + mv.ToolCount.ToString(CultureInfo.InvariantCulture) + " 款活跃工具",
                        FontSize = DesignSystem.TextCaption,
                        Margin = new Thickness(0, 6, 0, 6),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    osTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                    sp.Children.Add(osTb);

                    // 金额与占比
                    DockPanel bot = new DockPanel();
                    TextBlock shareTb = new TextBlock
                    {
                        Text = "占比 " + mv.ShareText,
                        FontFamily = DesignSystem.FontMono,
                        FontSize = DesignSystem.TextCaption,
                        VerticalAlignment = VerticalAlignment.Bottom
                    };
                    shareTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                    DockPanel.SetDock(shareTb, Dock.Right);
                    bot.Children.Add(shareTb);

                    TextBlock costTb = new TextBlock
                    {
                        Text = mv.FormattedMoney,
                        FontFamily = DesignSystem.FontDisplay,
                        FontSize = DesignSystem.TextHeading,
                        FontWeight = FontWeights.SemiBold
                    };
                    costTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                    ModernControlsFactory.EnableTabularNumbers(costTb, false);
                    bot.Children.Add(costTb);
                    sp.Children.Add(bot);
                    card.Child = sp;

                    MachineRowView captured = mv;
                    card.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                    {
                        _selectedMachineId = isSelected ? null : captured.MachineId;
                        RefreshDashboardView();
                    };

                    _deviceCardsStack.Children.Add(card);
                }
            }

            // 末尾引导卡片：+ 接入新电脑 / Linux VPS
            Border addCard = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            addCard.Width = 200;
            addCard.Margin = new Thickness(0, 0, 4, 2);
            addCard.Cursor = Cursors.Hand;
            addCard.SetResourceReference(Border.BackgroundProperty, "BgSurface");

            StackPanel addSp = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Shapes.Path plusIc = UiIcons.CreateIcon(UiIcons.Plus, 18.0, "AccentText");
            plusIc.HorizontalAlignment = HorizontalAlignment.Center;
            addSp.Children.Add(plusIc);

            TextBlock addTitle = new TextBlock
            {
                Text = "接入新电脑 / Linux VPS",
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            addTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            addSp.Children.Add(addTitle);

            TextBlock addSub = new TextBlock
            {
                Text = "配对码或一行命令自动同步",
                FontSize = DesignSystem.TextCaption,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            addSub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            addSp.Children.Add(addSub);

            addCard.Child = addSp;
            addCard.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                SwitchActiveView(ViewSettings);
                if (_settingsView != null)
                    _settingsView.ScrollToSection(1);
            };
            _deviceCardsStack.Children.Add(addCard);
        }

        // -------------------- 平滑面积折线 / 圆角柱状趋势图（含十字准星虚线 + 跟随式浮层详情卡片 + 点击单日钻取） --------------------
        private void HideChartCrosshair()
        {
            if (_chartCrosshairLine != null) _chartCrosshairLine.Visibility = Visibility.Collapsed;
            if (_chartHoverDot != null) _chartHoverDot.Visibility = Visibility.Collapsed;
            if (_chartFloatingCard != null) _chartFloatingCard.Visibility = Visibility.Collapsed;
        }

        private void ShowChartCrosshair(DailyRowView day, double xCenter, double yPoint, double topPad, double plotH, double canvasW)
        {
            if (day == null || _chartCrosshairLine == null || _chartFloatingCard == null)
                return;

            _chartCrosshairLine.X1 = xCenter;
            _chartCrosshairLine.X2 = xCenter;
            _chartCrosshairLine.Y1 = topPad;
            _chartCrosshairLine.Y2 = topPad + plotH;
            _chartCrosshairLine.Visibility = Visibility.Visible;

            if (_chartHoverDot != null)
            {
                Canvas.SetLeft(_chartHoverDot, xCenter - 4.0);
                Canvas.SetTop(_chartHoverDot, yPoint - 4.0);
                _chartHoverDot.Visibility = Visibility.Visible;
            }

            _chartFloatingDateText.Text = day.Date + (_selectedDate == day.Date ? " (当前锁定)" : " · 点击锁定该日");
            _chartFloatingPrimaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0}  ({1})",
                _isCny ? day.CnyText : day.UsdText,
                day.TotalTokensText + " Tokens");
            _chartFloatingSubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} 次调用 · 缓存读 {1} · 输出 {2}",
                day.Turns,
                day.CacheReadText,
                day.OutputText);

            _chartFloatingCard.Visibility = Visibility.Visible;
            _chartFloatingCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cardW = Math.Max(168.0, _chartFloatingCard.DesiredSize.Width);
            double leftX = (xCenter + 14.0 + cardW <= canvasW - 6.0)
                ? (xCenter + 12.0)
                : Math.Max(8.0, xCenter - cardW - 12.0);
            Canvas.SetLeft(_chartFloatingCard, leftX);
            Canvas.SetTop(_chartFloatingCard, Math.Max(4.0, topPad));
        }

        private void RedrawChart()
        {
            if (_chartCanvas == null || _chartInteractiveHost == null) return;
            _chartCanvas.Children.Clear();
            HideChartCrosshair();

            double w = _chartInteractiveHost.ActualWidth;
            double h = _chartInteractiveHost.ActualHeight;
            if (w < 80.0 || h < 60.0) return;

            if (_currentDailyAsc == null || _currentDailyAsc.Count == 0)
            {
                TextBlock empty = new TextBlock
                {
                    Text = "当前筛选范围内暂无按日趋势数据",
                    FontSize = DesignSystem.TextBody
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                Canvas.SetLeft(empty, Math.Max(16.0, w * 0.5 - 92.0));
                Canvas.SetTop(empty, Math.Max(16.0, h * 0.5 - 10.0));
                _chartCanvas.Children.Add(empty);
                return;
            }

            double leftPad = 52.0;
            double rightPad = 12.0;
            double topPad = 10.0;
            double bottomPad = 24.0;
            double plotW = Math.Max(20.0, w - leftPad - rightPad);
            double plotH = Math.Max(20.0, h - topPad - bottomPad);

            double maxVal = 0.0;
            for (int i = 0; i < _currentDailyAsc.Count; i++)
            {
                DailyRowView d = _currentDailyAsc[i];
                double v = _rankByCost ? (_isCny ? d.CostCny : d.CostUsd) : (double)d.TotalTokens;
                if (v > maxVal) maxVal = v;
            }
            if (maxVal <= 0.0) maxVal = 1.0;

            Brush gridBrush = DesignSystem.ThemeBrush("BorderSubtle", _isDark);
            Brush axisTextBrush = DesignSystem.ThemeBrush("FgMuted", _isDark);
            Color accentColor = DesignSystem.ThemeColor("AccentText", _isDark);
            Brush strokeBrush = DesignSystem.ThemeBrush("AccentText", _isDark);
            Brush barBrush = DesignSystem.ThemeBrush("ChartBarFill", _isDark);
            Brush barHoverBrush = TryFindResource("ChartBarHover") as Brush ?? strokeBrush;

            // Y 轴水平参考线与刻度标签
            int yTicks = 3;
            for (int t = 0; t <= yTicks; t++)
            {
                double frac = (double)t / yTicks;
                double y = topPad + plotH * (1.0 - frac);

                Shapes.Line gl = new Shapes.Line
                {
                    X1 = leftPad,
                    X2 = leftPad + plotW,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1.0
                };
                _chartCanvas.Children.Add(gl);

                double tickVal = maxVal * frac;
                string yLabelText;
                if (_rankByCost)
                {
                    string sym = _isCny ? "¥" : "$";
                    yLabelText = tickVal >= 100.0
                        ? (sym + tickVal.ToString("F0", CultureInfo.InvariantCulture))
                        : (sym + tickVal.ToString("0.##", CultureInfo.InvariantCulture));
                }
                else
                {
                    yLabelText = FormatterUtil.FormatTokens((long)Math.Round(tickVal));
                }

                TextBlock yLbl = new TextBlock
                {
                    Text = yLabelText,
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    Foreground = axisTextBrush,
                    Width = leftPad - 6.0,
                    TextAlignment = TextAlignment.Right
                };
                Canvas.SetLeft(yLbl, 0.0);
                Canvas.SetTop(yLbl, y - 7.0);
                _chartCanvas.Children.Add(yLbl);
            }

            int n = _currentDailyAsc.Count;
            double slotW = plotW / n;
            Point[] pts = new Point[n];

            for (int i = 0; i < n; i++)
            {
                DailyRowView d = _currentDailyAsc[i];
                double v = _rankByCost ? (_isCny ? d.CostCny : d.CostUsd) : (double)d.TotalTokens;
                double ratio = Math.Max(0.0, Math.Min(1.0, v / maxVal));
                double px = (n == 1)
                    ? (leftPad + plotW * 0.5)
                    : (leftPad + slotW * i + slotW * 0.5);
                double py = topPad + plotH * (1.0 - ratio);
                pts[i] = new Point(px, py);
            }

            if (_chartLineMode && n >= 2)
            {
                // 平滑三次贝塞尔面积填充 + 描边折线
                PathFigure areaFig = new PathFigure { StartPoint = new Point(pts[0].X, topPad + plotH), IsClosed = true, IsFilled = true };
                areaFig.Segments.Add(new LineSegment(pts[0], false));

                PathFigure strokeFig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };

                for (int i = 0; i < n - 1; i++)
                {
                    Point p0 = pts[i];
                    Point p1 = pts[i + 1];
                    double dx = (p1.X - p0.X) * 0.42;
                    Point c1 = new Point(p0.X + dx, p0.Y);
                    Point c2 = new Point(p1.X - dx, p1.Y);
                    areaFig.Segments.Add(new BezierSegment(c1, c2, p1, true));
                    strokeFig.Segments.Add(new BezierSegment(c1, c2, p1, true));
                }
                areaFig.Segments.Add(new LineSegment(new Point(pts[n - 1].X, topPad + plotH), false));

                LinearGradientBrush areaFill = new LinearGradientBrush();
                areaFill.StartPoint = new Point(0, 0);
                areaFill.EndPoint = new Point(0, 1);
                areaFill.GradientStops.Add(new GradientStop(Color.FromArgb(_isDark ? (byte)76 : (byte)52, accentColor.R, accentColor.G, accentColor.B), 0.0));
                areaFill.GradientStops.Add(new GradientStop(Color.FromArgb(2, accentColor.R, accentColor.G, accentColor.B), 1.0));
                areaFill.Freeze();

                PathGeometry areaGeo = new PathGeometry();
                areaGeo.Figures.Add(areaFig);
                Shapes.Path areaPath = new Shapes.Path { Data = areaGeo, Fill = areaFill };
                _chartCanvas.Children.Add(areaPath);

                PathGeometry strokeGeo = new PathGeometry();
                strokeGeo.Figures.Add(strokeFig);
                Shapes.Path linePath = new Shapes.Path
                {
                    Data = strokeGeo,
                    Stroke = strokeBrush,
                    StrokeThickness = 2.0,
                    StrokeLineJoin = PenLineJoin.Round
                };
                _chartCanvas.Children.Add(linePath);

                // 数据点圆点（单日选中态高亮放大）
                if (n <= 45 || _selectedDate != null)
                {
                    for (int i = 0; i < n; i++)
                    {
                        bool isLockedDay = (_selectedDate != null && string.Equals(_currentDailyAsc[i].Date, _selectedDate, StringComparison.Ordinal));
                        if (n > 45 && !isLockedDay) continue;

                        double rSize = isLockedDay ? 8.0 : 5.0;
                        Shapes.Ellipse dot = new Shapes.Ellipse
                        {
                            Width = rSize,
                            Height = rSize,
                            Fill = isLockedDay ? DesignSystem.ThemeBrush("SemanticWarning", _isDark) : strokeBrush
                        };
                        Canvas.SetLeft(dot, pts[i].X - rSize * 0.5);
                        Canvas.SetTop(dot, pts[i].Y - rSize * 0.5);
                        _chartCanvas.Children.Add(dot);
                    }
                }
            }
            else
            {
                // 圆角细柱图（或仅 1 天数据时的优雅柱状展示）
                double gap = n > 45 ? 1.5 : (n > 20 ? 3.0 : 5.0);
                double barW = Math.Max(2.5, Math.Min(26.0, slotW - gap));

                for (int i = 0; i < n; i++)
                {
                    DailyRowView d = _currentDailyAsc[i];
                    bool isLockedDay = (_selectedDate != null && string.Equals(d.Date, _selectedDate, StringComparison.Ordinal));
                    double bh = Math.Max(2.5, (topPad + plotH) - pts[i].Y);
                    double bx = pts[i].X - barW * 0.5;
                    double by = topPad + plotH - bh;

                    Border bar = new Border
                    {
                        Width = barW,
                        Height = bh,
                        CornerRadius = new CornerRadius(Math.Min(4.0, barW * 0.45), Math.Min(4.0, barW * 0.45), 0, 0),
                        Background = isLockedDay ? DesignSystem.ThemeBrush("SemanticWarning", _isDark) : barBrush,
                        Opacity = (_selectedDate != null && !isLockedDay) ? 0.45 : 1.0
                    };
                    Canvas.SetLeft(bar, bx);
                    Canvas.SetTop(bar, by);
                    _chartCanvas.Children.Add(bar);
                }
            }

            // X 轴自适应稀疏日期标签（零重叠）
            int labelStep = Math.Max(1, (int)Math.Ceiling(n / Math.Max(2.0, Math.Floor(plotW / 66.0))));
            for (int i = 0; i < n; i++)
            {
                if (i != 0 && i != n - 1 && (i % labelStep != 0))
                    continue;
                // 离最后一个日期不足一个步长的标签跳过，避免与末尾标签重叠
                if (i != n - 1 && (n - 1 - i) < labelStep)
                    continue;

                string rawDate = _currentDailyAsc[i].Date ?? "";
                string shortDate = rawDate.Length >= 10 ? rawDate.Substring(5) : rawDate;
                TextBlock xLbl = new TextBlock
                {
                    Text = shortDate,
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    Foreground = axisTextBrush
                };
                Canvas.SetLeft(xLbl, Math.Max(leftPad, Math.Min(w - 38.0, pts[i].X - 16.0)));
                Canvas.SetTop(xLbl, topPad + plotH + 5.0);
                _chartCanvas.Children.Add(xLbl);
            }

            // 全宽透明命中柱：悬停显示十字准星与跟随卡片，左键点击按单日钻取过滤！
            for (int i = 0; i < n; i++)
            {
                DailyRowView capturedDay = _currentDailyAsc[i];
                Point capturedPt = pts[i];
                double hitX = leftPad + slotW * i;

                Border hitCol = new Border
                {
                    Width = Math.Max(4.0, slotW),
                    Height = plotH,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                Canvas.SetLeft(hitCol, hitX);
                Canvas.SetTop(hitCol, topPad);

                hitCol.MouseEnter += delegate(object s, MouseEventArgs e)
                {
                    ShowChartCrosshair(capturedDay, capturedPt.X, capturedPt.Y, topPad, plotH, w);
                };
                hitCol.MouseMove += delegate(object s, MouseEventArgs e)
                {
                    ShowChartCrosshair(capturedDay, capturedPt.X, capturedPt.Y, topPad, plotH, w);
                };
                hitCol.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    _selectedDate = (_selectedDate != null && string.Equals(_selectedDate, capturedDay.Date, StringComparison.Ordinal))
                        ? null
                        : capturedDay.Date;
                    RefreshDashboardView();
                };

                _chartCanvas.Children.Add(hitCol);
            }
        }

        // -------------------- 报表导出与系统交互（全流程 ToastManager 非阻塞通知，零 MessageBox） --------------------
        private void ExportAndOpenHtmlReport()
        {
            try
            {
                string fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "TokLens_Report_{0}.html",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                string outDir = Path.Combine(SyncService.AppConfigDir, "exports");
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, fileName);

                HtmlExporter.ExportHtmlReport(_latestExportBuckets, _scanResult, _usdToCny, outPath);
                try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); } catch { }

                if (_toastManager != null)
                {
                    _toastManager.ShowWithAction(
                        "交互式网页看板已生成",
                        fileName,
                        "success",
                        "打开所在文件夹",
                        delegate() { OpenInExplorerSelect(outPath); });
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("导出 HTML 看板失败", ex.Message, "danger");
            }
        }

        private void ExportCsvWithDialog()
        {
            try
            {
                using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
                {
                    sfd.Title = "导出多设备 AI Token 财务对账明细 (.csv)";
                    sfd.Filter = "CSV 表格文件 (*.csv)|*.csv";
                    sfd.FileName = string.Format(
                        CultureInfo.InvariantCulture,
                        "TokLens_Billing_{0}.csv",
                        DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture));

                    if (sfd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                    {
                        string outPath = sfd.FileName;
                        FormatterUtil.ExportCsv(_latestExportBuckets, _usdToCny, outPath);
                        if (_toastManager != null)
                        {
                            _toastManager.ShowWithAction(
                                "CSV 财务明细导出成功",
                                Path.GetFileName(outPath),
                                "success",
                                "打开所在文件夹",
                                delegate() { OpenInExplorerSelect(outPath); });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("导出 CSV 失败", ex.Message, "danger");
            }
        }

        private void ExportJsonWithDialog()
        {
            try
            {
                using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
                {
                    sfd.Title = "导出脱敏账单汇总快照 (.json)";
                    sfd.Filter = "JSON 文件 (*.json)|*.json";
                    sfd.FileName = string.Format(
                        CultureInfo.InvariantCulture,
                        "TokLens_Summary_{0}.json",
                        DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture));

                    if (sfd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                    {
                        string outPath = sfd.FileName;
                        WriteMergedSummaryJson(
                            _scanResult,
                            _latestExportBuckets,
                            Math.Max(1, _latestMachineViews.Count),
                            _usdToCny,
                            _fxSource,
                            outPath);

                        if (_toastManager != null)
                        {
                            _toastManager.ShowWithAction(
                                "JSON 脱敏汇总导出成功",
                                Path.GetFileName(outPath),
                                "success",
                                "打开所在文件夹",
                                delegate() { OpenInExplorerSelect(outPath); });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("导出 JSON 失败", ex.Message, "danger");
            }
        }

        internal static void WriteMergedSummaryJson(
            ScanResult localScan,
            List<RecordBucket> mergedRows,
            int deviceCount,
            double rate,
            string rateSource,
            string outPath)
        {
            ScanResult synthetic = new ScanResult();
            if (localScan != null)
            {
                synthetic.UserHome = localScan.UserHome;
                synthetic.GeneratedAt = localScan.GeneratedAt;
                synthetic.ReferenceDate = localScan.ReferenceDate;
                synthetic.PricingModelCount = localScan.PricingModelCount;
                synthetic.ElapsedSeconds = localScan.ElapsedSeconds;
                synthetic.ChangedFilesCount = localScan.ChangedFilesCount;
                synthetic.TotalFilesTracked = localScan.TotalFilesTracked;
                synthetic.ArchivedFilesCount = localScan.ArchivedFilesCount;
                synthetic.RecentRequests = localScan.RecentRequests;
                if (localScan.Warnings != null)
                    synthetic.Warnings.AddRange(localScan.Warnings);
            }
            else
            {
                synthetic.GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                synthetic.ReferenceDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            synthetic.Records = mergedRows ?? new List<RecordBucket>();
            FormatterUtil.ExportSummaryJson(synthetic, rate, rateSource, outPath);

            // 补充 v2.2 产品版本号与多设备节点计数元数据
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> dict = js.DeserializeObject(File.ReadAllText(outPath, Encoding.UTF8)) as Dictionary<string, object>;
                if (dict != null)
                {
                    dict["app"] = AppConstants.ProductTitle;
                    dict["version"] = AppConstants.VersionTag;
                    dict["device_count"] = Math.Max(1, deviceCount);
                    File.WriteAllText(outPath, js.Serialize(dict), new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private static void OpenInExplorerSelect(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                if (File.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + filePath + "\"") { UseShellExecute = true });
                    return;
                }
                string dir = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                }
            }
            catch { }
        }

        private void ShowToastSafe(string title, string detail, string variant)
        {
            if (_toastManager == null) return;
            try
            {
                _toastManager.Show(title, detail, string.IsNullOrEmpty(variant) ? "info" : variant);
            }
            catch { }
        }

        private static bool TrySetClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch
            {
                try
                {
                    WinForms.Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        // -------------------- 专项引擎同步、命令面板、桌面胶囊 HUD、时光机与多格式导出 --------------------
        private void RefreshActiveSpecialistView(bool fromDataRefresh)
        {
            try
            {
                List<RecordBucket> machineRows = _latestSpecialistMachineRows ?? new List<RecordBucket>();
                List<RecordBucket> activeRows = _latestSpecialistActiveRows ?? new List<RecordBucket>();
                List<RecentRequestRecord> recentList = (_scanResult != null && _scanResult.RecentRequests != null)
                    ? _scanResult.RecentRequests
                    : new List<RecentRequestRecord>();

                if (_activeView == ViewProjects && _projectView != null)
                {
                    // 项目页统计的是本机工作区的全部历史，对账基数也要用本机全部历史，
                    // 否则「占本机账单」比例会拿全历史项目去比当前时间范围的账单。
                    List<RecordBucket> localLedger = (_localNode != null && _localNode.Records != null) ? _localNode.Records : machineRows;
                    PricingEngine pe = new PricingEngine(_homeDir);
                    _projectView.RefreshAttribution(localLedger, pe, _isCny, _usdToCny, _isDark);
                }
                else if (_activeView == ViewInsights && _subscriptionView != null)
                {
                    _subscriptionView.Render(_latestAllHistoryRows, _isCny, _usdToCny, _isDark, DateTime.Today);
                }
                else if (_activeView == ViewDailyExport && _heatmapView != null)
                {
                    // 日历固定看近 365 天：按设备筛选，不跟随顶部时间范围。
                    List<RecordBucket> heatRows = _latestFullHistoryMachineRows ?? machineRows;
                    _heatmapView.Render(heatRows, _isCny, _usdToCny, _isDark, _latestReferenceDate);
                }
                else if (_activeView == ViewBloat && _bloatView != null)
                {
                    _bloatView.RenderBloatAnalysis(activeRows, recentList, _isCny, _usdToCny, _isDark);
                }
                else if (_activeView == ViewDoctor && _doctorView != null)
                {
                    if (_skipAutoScan)
                    {
                        _doctorView.RenderReport(new SystemHealthReport(), _isDark);
                    }
                    else if (!_doctorLoadedOnce || !fromDataRefresh)
                    {
                        _doctorLoadedOnce = true;
                        _doctorView.RefreshDoctor(_isDark);
                    }
                }
            }
            catch { }
        }

        private void OpenCommandPalette()
        {
            if (_commandPalette == null) return;
            if (_commandPalette.IsOpen)
            {
                _commandPalette.Close();
                return;
            }

            List<PaletteCommandItem> items = new List<PaletteCommandItem>(28);

            // 1. 十一大旗舰视图跳转
            items.Add(new PaletteCommandItem("nav-overview", "视图导航", "概览", "KPI、消耗构成与趋势图", "Ctrl+1", UiIcons.Overview, delegate() { SwitchActiveView(ViewOverview); }));
            items.Add(new PaletteCommandItem("nav-live", "视图导航", "用量 / 请求明细", "逐笔查看请求用量、耗时与单笔账单", "Ctrl+2", UiIcons.Activity, delegate() { SwitchActiveView(ViewLiveStream); }));
            items.Add(new PaletteCommandItem("nav-models", "视图导航", "用量 / 模型与工具", "按模型和工具汇总，查看单价与账单占比", "Ctrl+3", UiIcons.Models, delegate() { SwitchActiveView(ViewModels); }));
            items.Add(new PaletteCommandItem("nav-projects", "视图导航", "用量 / 项目", "本机各代码仓库与工作区的用量归因", "Ctrl+4", UiIcons.Folder, delegate() { SwitchActiveView(ViewProjects); }));
            if (SubscriptionsEnabled)
                items.Add(new PaletteCommandItem("nav-insights", "视图导航", "订阅与费用", "订阅记录、到期续费与回本比例", "Ctrl+5", UiIcons.Sparkles, delegate() { SwitchActiveView(ViewInsights); }));
            items.Add(new PaletteCommandItem("nav-bloat", "视图导航", "用量 / 会话分析", "长对话上下文膨胀与缓存命中异常的会话", "Ctrl+7", UiIcons.Layers, delegate() { SwitchActiveView(ViewBloat); }));
            items.Add(new PaletteCommandItem("nav-devices", "视图导航", "设备", "各台电脑与 Linux / macOS 节点的状态与账单", "Ctrl+8", UiIcons.Devices, delegate() { SwitchActiveView(ViewDevices); }));
            items.Add(new PaletteCommandItem("nav-daily", "视图导航", "用量 / 每日汇总", "按日汇总的用量与费用（导出在工具条右侧）", "Ctrl+9", UiIcons.Calendar, delegate() { SwitchActiveView(ViewDailyExport); }));
            items.Add(new PaletteCommandItem("nav-doctor", "视图导航", "设置 / 环境检查", "检查本机 AI 工具日志、WSL2 与 GitHub CLI 状态", "Ctrl+0", UiIcons.Doctor, delegate() { SwitchActiveView(ViewDoctor); }));
            items.Add(new PaletteCommandItem("nav-settings", "视图导航", "设置 / 同步与偏好", "Gist / WebDAV 同步、配对码、扫描与显示偏好", "Ctrl+,", UiIcons.Settings, delegate() { SwitchActiveView(ViewSettings); }));

            // 2. 效率工具与工作台
            items.Add(new PaletteCommandItem("tool-hud", "桌面工作台", "显示 / 隐藏桌面悬浮球", "外圈上下文占用，球心可切换今日估值 / 速率，悬停看详情", "Ctrl+M", UiIcons.Capsule, delegate() { ToggleMiniHudWindow(); }));
            items.Add(new PaletteCommandItem("tool-pricing", "桌面工作台", "设置 / 计价规则", "浏览模型单价，或配置私有中转的自定义费率", "", UiIcons.Models, delegate() { OpenPricingStudioDialog(null); }));
            items.Add(new PaletteCommandItem("tool-token-guide", "帮助与排障", "为什么家里和单位自动读取的 gh 令牌不一样？", "深度图解 GitHub 设备独立 OAuth 令牌 (gho_...) 与同账号 Gist 汇聚原理", "F1", UiIcons.Key, delegate() { OpenShortcutAndGuideDialog(1); }));
            items.Add(new PaletteCommandItem("tool-shortcuts", "帮助与排障", "打开全局键盘快捷键速查表", "双列键盘速查矩阵，支持全键盘流极速操控", "F1", UiIcons.Help, delegate() { OpenShortcutAndGuideDialog(0); }));
            items.Add(new PaletteCommandItem("tool-scan", "数据与同步", "立即增量扫描本地日志并触发多端同步", "增量扫描本机日志，并刷新汇率", "F5", UiIcons.Refresh, delegate() { RunSilentScanAsync(_homeDir, true, true); StartExchangeRateSyncAsync(false); }));
            items.Add(new PaletteCommandItem("tool-backup-now", "数据与同步", "立即创建本地时光机压缩快照 (.toklens.gz)", "将本机与缓存多节点高水位账本安全归档至 backups 目录", "", UiIcons.History, delegate() { CreateManualTimeMachineSnapshot(); }));
            items.Add(new PaletteCommandItem("tool-theme", "界面偏好", _isDark ? "切换至浅色主题 (Paper Zinc)" : "切换至深色主题 (Obsidian Zinc)", "同时切换窗口标题栏与悬浮球详情卡片", "", _isDark ? UiIcons.Sun : UiIcons.Moon, delegate() { ToggleThemeFromUi(); }));
            items.Add(new PaletteCommandItem("tool-currency", "界面偏好", _isCny ? "切换为美元计价 ($ USD)" : "切换为人民币计价 (¥ CNY)", "全局 KPI、图表、模型表与单笔透视同步切换", "", UiIcons.Swap, delegate()
            {
                _isCny = !_isCny;
                if (_syncCfg != null) { _syncCfg.PreferCny = _isCny; PersistConfig(_syncCfg); }
                ModernControlsFactory.UpdateSegmentedGroup(_currencyBtns, _isCny ? 0 : 1);
                RefreshDashboardView();
                if (_settingsView != null) _settingsView.ReloadFromConfig();
            }));

            // 3. 七大导出与分享动作
            items.Add(new PaletteCommandItem("exp-html", "导出", "导出网页看板 (.html)", "单文件离线网页，导出后自动打开", "Ctrl+E", UiIcons.ExternalLink, delegate() { ExportAndOpenHtmlReport(); }));
            items.Add(new PaletteCommandItem("exp-md", "导出", "导出 Markdown 报告 (.md)", "表格与趋势摘要", "", UiIcons.Sparkles, delegate() { ExportMarkdownWithDialog(); }));
            items.Add(new PaletteCommandItem("exp-csv", "导出", "导出对账明细 (.csv)", "Excel 可直接打开", "Ctrl+S", UiIcons.Download, delegate() { ExportCsvWithDialog(); }));
            items.Add(new PaletteCommandItem("exp-json", "导出", "导出汇总快照 (.json)", "只含聚合用量与费用", "", UiIcons.Shield, delegate() { ExportJsonWithDialog(); }));

            _commandPalette.Open(items);
        }

        private void RefreshMiniHud()
        {
            if (_miniHud == null) return;
            HudTelemetry t = HudTelemetry.FromScan(_scanResult, DateTime.Now, _isCny, _usdToCny);
            if (DateTime.UtcNow < _hudAlertUntilUtc) { t.Alert = true; t.AlertText = _hudAlertText; }
            _miniHud.UpdateTelemetry(t);
        }

        private void EnsureMiniHudCreated()
        {
            if (_miniHud != null) return;
            _miniHud = new MiniHudWindow(
                _isDark,
                delegate()
                {
                    RestoreAndActivateFromTray();
                },
                delegate()
                {
                    RunSilentScanAsync(_homeDir, true, true);
                });

            RefreshMiniHud();
        }

        private void ToggleMiniHudWindow()
        {
            if (_skipAutoScan) return;
            try
            {
                EnsureMiniHudCreated();
                if (_miniHud.IsVisible)
                {
                    _miniHud.Hide();
                    if (_trayHudToggleItem != null)
                        _trayHudToggleItem.Checked = false;
                }
                else
                {
                    _miniHud.SetTheme(_isDark);
                    _miniHud.Show();
                    if (_trayHudToggleItem != null)
                        _trayHudToggleItem.Checked = true;
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("切换桌面悬浮球失败", ex.Message, "warning");
            }
        }

        private void OnGlobalHotkeyTriggered()
        {
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                RestoreAndActivateFromTray();
            }
            else if (!IsActive)
            {
                RestoreAndActivateFromTray();
            }
            else
            {
                ToggleMiniHudWindow();
            }
        }

        private void OpenShortcutAndGuideDialog(int initialTabIndex)
        {
            if (_skipAutoScan) return;
            try
            {
                ShortcutAndGuideWindow dlg = new ShortcutAndGuideWindow(this, _isDark, initialTabIndex);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowToastSafe("打开帮助指南失败", ex.Message, "warning");
            }
        }

        private void OpenPricingStudioDialog(string initialModelId)
        {
            if (_skipAutoScan) return;
            try
            {
                List<string> models = new List<string>();
                if (!string.IsNullOrWhiteSpace(initialModelId))
                    models.Add(initialModelId.Trim());
                if (_latestUniqueModelNames != null)
                {
                    for (int i = 0; i < _latestUniqueModelNames.Count; i++)
                        models.Add(_latestUniqueModelNames[i]);
                }

                PricingStudioWindow dlg = new PricingStudioWindow(
                    this,
                    _homeDir,
                    _isDark,
                    models,
                    delegate()
                    {
                        try
                        {
                            PricingEngine pe = new PricingEngine(_homeDir);
                            if (_scanResult != null && _scanResult.Records != null)
                            {
                                for (int i = 0; i < _scanResult.Records.Count; i++)
                                {
                                    RecordBucket rb = _scanResult.Records[i];
                                    if (rb == null) continue;
                                    double cost;
                                    double baseline;
                                    ModelRate mr;
                                    pe.ComputeBucketCost(rb.Model, rb.Turns, rb.FreshInput, rb.Output, rb.CacheRead, rb.CacheWrite, 0.0, out cost, out baseline, out mr);
                                    rb.CostUsd = cost;
                                    rb.BaselineUsd = baseline;
                                    rb.RateLabel = mr.Label;
                                }
                                _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);
                                Program.ApplyNodeAliasToSnapshot(_localNode, _syncCfg);
                            }
                            _remoteNodes = LoadAndRecalculateRemoteNodes(_syncCfg.MachineId, _homeDir, _syncCfg);
                        }
                        catch { }

                        UpdatePriceUiState(false);
                        RefreshDashboardView();
                        RunSilentScanAsync(_homeDir, true, false);
                        ShowToastSafe("自定义模型单价已生效", "全站本地与远端节点等效账单已完成重算", "success");
                    });
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowToastSafe("打开模型单价工作台失败", ex.Message, "danger");
            }
        }

        private void CreateManualTimeMachineSnapshot()
        {
            try
            {
                BackupArchiveInfo info = BackupAndMigrationService.CreateSnapshotBundle(null, true);
                if (info != null && _toastManager != null)
                {
                    _toastManager.ShowWithAction(
                        "时光机本地快照已生成",
                        string.Format(CultureInfo.InvariantCulture, "{0} ({1}, {2} 节点)", info.FileName, info.SizeText, info.NodeCount),
                        "success",
                        "打开备份目录",
                        delegate() { OpenInExplorerSelect(info.FilePath); });
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("创建时光机备份失败", ex.Message, "danger");
            }
        }

        private void OpenTimeMachineBackupPopover(UIElement anchor)
        {
            StackPanel sp = new StackPanel { Width = 368 };
            Popup pop = null;

            TextBlock hdr = new TextBlock
            {
                Text = "时光机账本备份与跨机迁移 (.toklens.gz)",
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 2, 4, 4)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(hdr);

            TextBlock desc = new TextBlock
            {
                Text = "每日启动自动保留最近 10 份高压缩无敏快照；支持高水位无损合并外部备份包或离线 VPS 节点快照，绝不冲掉更高历史账单。",
                FontSize = DesignSystem.TextCaption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 8)
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(desc);

            StackPanel topBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 8) };
            Button snapNowBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Plus, "立即生成快照", "primary");
            snapNowBtn.Height = 28;
            snapNowBtn.Margin = new Thickness(0, 0, 6, 0);
            snapNowBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                CreateManualTimeMachineSnapshot();
            };
            topBtns.Children.Add(snapNowBtn);

            Button importBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Folder, "导入合并备份包...", "secondary");
            importBtn.Height = 28;
            importBtn.Margin = new Thickness(0, 0, 6, 0);
            importBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                ImportTimeMachineBundleWithDialog();
            };
            topBtns.Children.Add(importBtn);

            Button openDirBtn = ModernControlsFactory.CreateButton("目录", "ghost");
            openDirBtn.Height = 28;
            openDirBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (pop != null) pop.IsOpen = false;
                try { Directory.CreateDirectory(BackupAndMigrationService.BackupsDir); } catch { }
                OpenInExplorerSelect(BackupAndMigrationService.BackupsDir);
            };
            topBtns.Children.Add(openDirBtn);
            sp.Children.Add(topBtns);

            List<BackupArchiveInfo> backups = BackupAndMigrationService.ListLocalBackups();
            if (backups != null && backups.Count > 0)
            {
                Border sep = new Border { Height = 1, Margin = new Thickness(4, 2, 4, 6) };
                sep.SetResourceReference(Border.BackgroundProperty, "BorderSubtle");
                sp.Children.Add(sep);

                ScrollViewer sv = new ScrollViewer { MaxHeight = 196, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                StackPanel listSp = new StackPanel();
                int take = Math.Min(6, backups.Count);
                for (int i = 0; i < take; i++)
                {
                    BackupArchiveInfo bi = backups[i];
                    Border rowBd = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 6, 8, 6),
                        Margin = new Thickness(0, 0, 0, 3)
                    };
                    rowBd.SetResourceReference(Border.BackgroundProperty, "BgSurface");

                    DockPanel rowDock = new DockPanel();
                    Button restoreBtn = ModernControlsFactory.CreateButton("无损恢复", "secondary");
                    restoreBtn.Height = 24;
                    restoreBtn.FontSize = DesignSystem.TextCaption;
                    restoreBtn.Padding = new Thickness(8, 0, 8, 0);
                    string capturedPath = bi.FilePath;
                    restoreBtn.Click += delegate(object s, RoutedEventArgs e)
                    {
                        if (pop != null) pop.IsOpen = false;
                        ApplyTimeMachineImportPath(capturedPath);
                    };
                    DockPanel.SetDock(restoreBtn, Dock.Right);
                    rowDock.Children.Add(restoreBtn);

                    StackPanel metaCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    TextBlock t1 = new TextBlock { Text = bi.CreatedAtText, FontFamily = DesignSystem.FontMono, FontSize = DesignSystem.TextCaption, FontWeight = FontWeights.SemiBold };
                    t1.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                    metaCol.Children.Add(t1);

                    TextBlock t2 = new TextBlock
                    {
                        Text = string.Format(CultureInfo.InvariantCulture, "{0} 台设备 · {1} 条记录 · {2}", bi.NodeCount, bi.RecordCount, bi.SizeText),
                        FontSize = DesignSystem.TextCaption
                    };
                    t2.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                    metaCol.Children.Add(t2);

                    rowDock.Children.Add(metaCol);
                    rowBd.Child = rowDock;
                    listSp.Children.Add(rowBd);
                }
                sv.Content = listSp;
                sp.Children.Add(sv);
            }

            pop = ModernControlsFactory.CreateModernPopover(anchor, sp);
            pop.IsOpen = true;
        }

        private void ImportTimeMachineBundleWithDialog()
        {
            try
            {
                using (WinForms.OpenFileDialog ofd = new WinForms.OpenFileDialog())
                {
                    ofd.Title = "选择 TokLens 时光机备份包或节点快照进行高水位无损合并";
                    ofd.Filter = "TokLens 备份与快照 (*.gz;*.json)|*.gz;*.json|所有文件 (*.*)|*.*";
                    if (Directory.Exists(BackupAndMigrationService.BackupsDir))
                        ofd.InitialDirectory = BackupAndMigrationService.BackupsDir;

                    if (ofd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(ofd.FileName))
                    {
                        ApplyTimeMachineImportPath(ofd.FileName);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("导入备份包失败", ex.Message, "danger");
            }
        }

        private void ApplyTimeMachineImportPath(string filePath)
        {
            try
            {
                PricingEngine pe = new PricingEngine(_homeDir);
                string summary;
                int mergedNodes = BackupAndMigrationService.ImportAndMergeSnapshotBundle(filePath, pe, out summary);
                if (mergedNodes > 0)
                {
                    _syncCfg = SyncService.LoadConfig();
                    _remoteNodes = LoadAndRecalculateRemoteNodes(_syncCfg.MachineId, _homeDir, _syncCfg);
                    if (_scanResult != null)
                    {
                        _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);
                        Program.ApplyNodeAliasToSnapshot(_localNode, _syncCfg);
                    }
                    if (_settingsView != null)
                        _settingsView.ReloadFromConfig();
                    RefreshDashboardView();
                    ShowToastSafe("时光机高水位无损合并完成", summary, "success");
                }
                else
                {
                    ShowToastSafe("未能合并备份包", summary, "warning");
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("恢复备份异常", ex.Message, "danger");
            }
        }

        private void UpdateAutoStartButtonLabel()
        {
            if (_autoStartMenuItem == null) return;
            bool enabled = WindowsShellIntegration.IsAutoStartEnabled();
            _autoStartMenuItem.Header = enabled ? "关闭开机自启" : "开启开机自启";
        }

        // 设置页右上角「更多」：不常用的本机动作收在这里，页面头部只留保存类按钮。
        private Button BuildSettingsMoreButton()
        {
            Button moreBtn = ModernControlsFactory.CreateButton("更多", "secondary");
            moreBtn.ToolTip = "时光机备份、开机自启";

            ContextMenu menu = new ContextMenu();
            MenuItem backupItem = new MenuItem { Header = "时光机备份…" };
            backupItem.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenTimeMachineBackupPopover(moreBtn);
            };
            menu.Items.Add(backupItem);

            _autoStartMenuItem = new MenuItem { Header = "开启开机自启" };
            _autoStartMenuItem.Click += delegate(object s, RoutedEventArgs e)
            {
                ToggleAutoStartFromUi();
            };
            menu.Items.Add(_autoStartMenuItem);

            moreBtn.ContextMenu = menu;
            moreBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                UpdateAutoStartButtonLabel();
                ModernControlsFactory.OpenMenuUnder(menu, moreBtn);
            };
            UpdateAutoStartButtonLabel();
            return moreBtn;
        }

        private void ToggleAutoStartFromUi()
        {
            bool cur = WindowsShellIntegration.IsAutoStartEnabled();
            string msg;
            bool ok = WindowsShellIntegration.SetAutoStartEnabled(!cur, out msg);
            UpdateAutoStartButtonLabel();
            ShowToastSafe(
                ok ? (!cur ? "已开启登录静默自启" : "已关闭开机自启") : "开机自启设置失败",
                msg,
                ok ? "success" : "warning");
        }

        private void ExportMarkdownWithDialog()
        {
            try
            {
                using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
                {
                    sfd.Title = "导出 Markdown 报告 (.md)";
                    sfd.Filter = "Markdown 文档 (*.md)|*.md";
                    sfd.FileName = string.Format(
                        CultureInfo.InvariantCulture,
                        "TokLens_Report_{0}.md",
                        DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture));

                    if (sfd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                    {
                        string outPath = sfd.FileName;
                        ReportExporter.ExportMarkdownFile(outPath, _latestExportBuckets, _isCny, _usdToCny);
                        if (_toastManager != null)
                        {
                            _toastManager.ShowWithAction(
                                "Markdown 报告已导出",
                                Path.GetFileName(outPath),
                                "success",
                                "打开所在文件夹",
                                delegate() { OpenInExplorerSelect(outPath); });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToastSafe("导出 Markdown 报告失败", ex.Message, "danger");
            }
        }

        // -------------------- 全局键盘快捷键支持 --------------------
        private void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e == null) return;

            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrlOnly = (mods == ModifierKeys.Control);

            if (ctrlOnly && e.Key == Key.K)
            {
                e.Handled = true;
                OpenCommandPalette();
                return;
            }

            if (_commandPalette != null && _commandPalette.IsOpen)
                return;

            if (e.Key == Key.F1)
            {
                e.Handled = true;
                OpenShortcutAndGuideDialog(0);
                return;
            }

            if (e.Key == Key.OemQuestion && (mods == ModifierKeys.Shift || mods == ModifierKeys.None))
            {
                if (!(Keyboard.FocusedElement is TextBox) && !(Keyboard.FocusedElement is PasswordBox))
                {
                    e.Handled = true;
                    OpenShortcutAndGuideDialog(0);
                    return;
                }
            }

            if (e.Key == Key.F5 || (ctrlOnly && e.Key == Key.R))
            {
                e.Handled = true;
                RunSilentScanAsync(_homeDir, true, true);
                StartExchangeRateSyncAsync(false);
                ShowToastSafe("正在刷新本地与多端账单", "增量扫描已在后台触发", "info");
                return;
            }

            if (ctrlOnly)
            {
                if (e.Key == Key.D1 || e.Key == Key.NumPad1) { e.Handled = true; SwitchActiveView(ViewOverview); return; }
                if (e.Key == Key.D2 || e.Key == Key.NumPad2) { e.Handled = true; SwitchActiveView(ViewLiveStream); return; }
                if (e.Key == Key.D3 || e.Key == Key.NumPad3) { e.Handled = true; SwitchActiveView(ViewModels); return; }
                if (e.Key == Key.D4 || e.Key == Key.NumPad4) { e.Handled = true; SwitchActiveView(ViewProjects); return; }
                if (e.Key == Key.D5 || e.Key == Key.NumPad5) { e.Handled = true; if (SubscriptionsEnabled) SwitchActiveView(ViewInsights); return; }
                if (e.Key == Key.D7 || e.Key == Key.NumPad7) { e.Handled = true; SwitchActiveView(ViewBloat); return; }
                if (e.Key == Key.D8 || e.Key == Key.NumPad8) { e.Handled = true; SwitchActiveView(ViewDevices); return; }
                if (e.Key == Key.D9 || e.Key == Key.NumPad9) { e.Handled = true; SwitchActiveView(ViewDailyExport); return; }
                if (e.Key == Key.D0 || e.Key == Key.NumPad0) { e.Handled = true; SwitchActiveView(ViewDoctor); return; }
                if (e.Key == Key.OemComma) { e.Handled = true; SwitchActiveView(ViewSettings); return; }

                if (e.Key == Key.M)
                {
                    e.Handled = true;
                    ToggleMiniHudWindow();
                    return;
                }

                if (e.Key == Key.F)
                {
                    e.Handled = true;
                    if (_activeView == ViewModels && _modelSearchInput != null)
                    {
                        _modelSearchInput.Focus();
                        _modelSearchInput.SelectAll();
                    }
                    else if (_activeView == ViewDailyExport && _dailySearchInput != null)
                    {
                        _dailySearchInput.Focus();
                        _dailySearchInput.SelectAll();
                    }
                    else
                    {
                        SwitchActiveView(ViewLiveStream);
                        if (_liveSearchInput != null)
                        {
                            _liveSearchInput.Focus();
                            _liveSearchInput.SelectAll();
                        }
                    }
                    return;
                }

                if (e.Key == Key.S)
                {
                    e.Handled = true;
                    ExportCsvWithDialog();
                    return;
                }

                if (e.Key == Key.E)
                {
                    e.Handled = true;
                    ExportAndOpenHtmlReport();
                    return;
                }
            }

            if (e.Key == Key.Escape && mods == ModifierKeys.None)
            {
                TextBox focusedTb = Keyboard.FocusedElement as TextBox;
                if (focusedTb != null)
                {
                    if (!string.IsNullOrEmpty(focusedTb.Text) &&
                        (ReferenceEquals(focusedTb, _liveSearchInput) ||
                         ReferenceEquals(focusedTb, _modelSearchInput) ||
                         ReferenceEquals(focusedTb, _dailySearchInput)))
                    {
                        focusedTb.Text = "";
                    }
                    Keyboard.ClearFocus();
                    e.Handled = true;
                    return;
                }

                if (_activeView == ViewLiveStream && _requestInspector != null && _requestInspector.Visibility == Visibility.Visible)
                {
                    CloseRequestInspector();
                    e.Handled = true;
                    return;
                }

                if (_selectedDate != null || _selectedTool != null)
                {
                    _selectedDate = null;
                    _selectedTool = null;
                    RefreshDashboardView();
                    e.Handled = true;
                }
            }
        }
    }
}
