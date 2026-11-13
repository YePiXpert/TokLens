using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("知微 TokLens")]
[assembly: AssemblyDescription("多设备与 Linux VPS AI Token 实时监控与账单透镜（绿色便携单文件版）")]
[assembly: AssemblyCompany("TokLens Local-First")]
[assembly: AssemblyProduct("知微 · TokLens v2.1")]
[assembly: AssemblyCopyright("Copyright © 2026 TokLens")]
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

namespace TokLens
{
    internal static class Program
    {
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
                        if (double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out r) && r > 0)
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
                }

                if (selfTestUi)
                {
                    try
                    {
                        Application testApp = new Application();
                        MainWindow testWin = new MainWindow(homeDir, true);
                        testWin.RunHeadlessUiSelfTest();
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        if (!string.IsNullOrEmpty(exportJson))
                        {
                            try { File.WriteAllText(exportJson, ex.ToString()); } catch { }
                        }
                        else
                        {
                            Console.Error.WriteLine(ex.ToString());
                        }
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

                        NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(res, cfg);
                        List<NodeSnapshot> remoteSnaps = SyncService.LoadCachedRemoteNodes(cfg.MachineId);
                        List<RecordBucket> mergedRows = new List<RecordBucket>(localSnap.Records);
                        for (int r = 0; r < remoteSnaps.Count; r++)
                        {
                            if (remoteSnaps[r].Records != null)
                                mergedRows.AddRange(remoteSnaps[r].Records);
                        }

                        if (!string.IsNullOrEmpty(exportJson))
                            FormatterUtil.ExportSummaryJson(res, cliRate, rateSrc, exportJson);
                        if (!string.IsNullOrEmpty(exportCsv))
                            FormatterUtil.ExportCsv(mergedRows, cliRate, exportCsv);
                        if (!string.IsNullOrEmpty(exportHtml))
                            HtmlExporter.ExportHtmlReport(mergedRows, res, cliRate, exportHtml);

                        if (requireFullAccess && res.Warnings.Count > 0)
                            return 2;
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        if (!string.IsNullOrEmpty(exportJson))
                        {
                            try { File.WriteAllText(exportJson + ".err.txt", ex.ToString()); } catch { }
                        }
                        return 1;
                    }
                }
            }

            TryEnableDpiAwareness();
            try { WinForms.Application.EnableVisualStyles(); } catch { }

            Application app = new Application();
            MainWindow win = new MainWindow(homeDir, false);
            app.Run(win);
            return 0;
        }
    }

    public sealed class MainWindow : Window
    {
        private static readonly string[] ToolPalette = new string[]
        {
            "#38BDF8", "#818CF8", "#34D399", "#FBBF24", "#F472B6",
            "#FB7185", "#A78BFA", "#2DD4BF", "#F97316", "#60A5FA",
            "#A3E635", "#E879F9"
        };

        private static readonly int[] PresetRangeDays = new int[] { 0, 30, 7, 3, 1 };

        private string _homeDir;
        private SyncConfig _syncCfg;
        private ScanResult _scanResult;
        private NodeSnapshot _localNode;
        private List<NodeSnapshot> _remoteNodes = new List<NodeSnapshot>();

        private bool _isDark = true;
        private bool _isCny = true;
        private double _usdToCny = ExchangeRateService.DefaultRate;
        private string _fxSource = "内置默认";
        private string _fxUpdatedAt = "";
        private bool _fxIsSyncing = false;
        private bool _priceIsSyncing = false;
        private string _priceSyncSource = "内置+云端缓存";
        private string _priceSyncTime = "";
        private bool _cloudSyncRunning = false;
        private string _lastSyncStatus = "";

        private bool _suppressRateEvent = false;
        private bool _suppressCustomDaysEvent = false;
        private int _rangeDays = 0;
        private bool _rankByCost = true;
        private bool _leftRankByMachine = false;
        private string _selectedTool = null;
        private string _selectedMachineId = null;
        private int _activeTab = 0; // 0 = live requests, 1 = models, 2 = machines, 3 = daily, 4 = methodology
        private bool _isScanning = false;
        private bool _pendingRescan = false;
        private long _lastTopRequestTs = 0L;

        // Real-time background watchers & timers
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private DispatcherTimer _fsDebounceTimer;
        private DispatcherTimer _livePulseTimer;
        private DispatcherTimer _cloudAutoTimer;
        private DispatcherTimer _cloudDebounceTimer;

        // Layout regions (kept for pixel-stability assertions)
        private Border _headerBarBorder;
        private Border _deviceStripBorder;
        private Border _warningBanner;
        private TextBlock _warningText;

        // Header controls
        private Button[] _rangeBtns;
        private TextBox _customDaysInput;
        private Button[] _currencyBtns;
        private TextBox _rateInput;
        private TextBlock _fxStatusBtnText;
        private Border _fxStatusBtnBorder;
        private Button _syncBtn;
        private Button _refreshBtn;
        private Button _moreBtn;
        private MenuItem _themeMenuItem;

        // Device Rail controls
        private StackPanel _devicePillsStack;
        private ScrollViewer _deviceScroll;

        // KPI elements
        private TextBlock _kpiCostVal;
        private TextBlock _kpiCostSub;
        private TextBlock _kpiTokVal;
        private TextBlock _kpiTokSub;
        private TextBlock _kpiHitVal;
        private TextBlock _kpiHitSub;
        private TextBlock _kpiSaveVal;
        private TextBlock _kpiSaveSub;

        // Middle Insights controls
        private Button[] _leftModeBtns;
        private Button[] _metricBtns;
        private StackPanel _toolsStack;
        private Button _clearFilterBtn;
        private TextBlock _trendTitleText;
        private TextBlock _trendHoverText;
        private Canvas _chartCanvas;
        private Border _chartHost;
        private List<DailyRowView> _currentDailyAsc = new List<DailyRowView>();

        // Bottom Table controls
        private Button[] _tabBtns;
        private Button _methodologyLinkBtn;
        private DataGrid _liveGrid;
        private DataGrid _modelGrid;
        private DataGrid _machineGrid;
        private DataGrid _dailyGrid;
        private FrameworkElement _aboutPanel;

        // Bottom Status Bar controls
        private Ellipse _statusPulseDot;
        private TextBlock _statusLeftText;
        private TextBlock _statusLiveTickerText;
        private Border _statusLiveTickerBorder;
        private TextBlock _statusPriceText;
        private Border _statusPriceBorder;
        private TextBlock _statusCloudText;
        private Border _statusCloudBorder;

        public MainWindow(string initialHomeDir) : this(initialHomeDir, false)
        {
        }

        public MainWindow(string initialHomeDir, bool skipAutoScan)
        {
            _homeDir = initialHomeDir;
            _syncCfg = SyncService.LoadConfig();
            _remoteNodes = SyncService.LoadCachedRemoteNodes(_syncCfg.MachineId);

            Title = "知微 · TokLens v2.1 — 多设备与 VPS AI Token 实时监控与账单透镜";

            bool recentCache;
            ExchangeRateInfo initFx = ExchangeRateService.LoadCachedOrDefault(out recentCache);
            _usdToCny = initFx.Rate;
            _fxSource = initFx.Source;
            _fxUpdatedAt = initFx.UpdatedAt;

            Rect workArea = SystemParameters.WorkArea;
            Width = Math.Min(1360.0, Math.Max(1020.0, workArea.Width * 0.92));
            Height = Math.Min(880.0, Math.Max(680.0, workArea.Height * 0.90));
            MinWidth = 1000.0;
            MinHeight = 660.0;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            TryLoadExeIcon();
            ApplyThemeResources();
            SetResourceReference(Window.BackgroundProperty, "BgWindow");
            SetResourceReference(Window.ForegroundProperty, "FgPrimary");

            Content = BuildLayout();
            UpdateFxBadgeUi(initFx.IsLive, false);
            UpdatePriceBadgeUi(false);
            UpdateCloudStatusUi();

            if (!skipAutoScan)
            {
                // 0ms Instant UI Hydration from SQLite Snapshot Store (Zero Modal / Zero Blank Screen!)
                ScanResult instantSnap = LocalUsageStore.TryLoadInstantSnapshot(_homeDir);
                if (instantSnap != null)
                {
                    _scanResult = instantSnap;
                    _localNode = SyncService.BuildLocalSnapshot(instantSnap, _syncCfg);
                    if (instantSnap.RecentRequests != null && instantSnap.RecentRequests.Count > 0)
                        _lastTopRequestTs = instantSnap.RecentRequests[0].TimestampMs;
                    UpdatePriceBadgeUi(false);
                    RefreshDashboardView();
                }

                Loaded += OnMainWindowLoaded;
                Closed += OnMainWindowClosed;
            }
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            SetupRealtimeWatchersAndTimers();
            RunSilentScanAsync(_homeDir, true, _scanResult == null);
            StartExchangeRateSyncAsync(false);
            StartCloudPricingSyncAsync(false);
        }

        private void OnMainWindowClosed(object sender, EventArgs e)
        {
            DisposeWatchers();
            if (_fsDebounceTimer != null) _fsDebounceTimer.Stop();
            if (_livePulseTimer != null) _livePulseTimer.Stop();
            if (_cloudAutoTimer != null) _cloudAutoTimer.Stop();
            if (_cloudDebounceTimer != null) _cloudDebounceTimer.Stop();
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

            // Lightweight 4-second live pulse so active SQLite WAL / unbuffered streams update TPS & tokens dynamically
            _livePulseTimer = new DispatcherTimer();
            _livePulseTimer.Interval = TimeSpan.FromSeconds(4);
            _livePulseTimer.Tick += delegate(object s, EventArgs args)
            {
                if (!_isScanning)
                    RunSilentScanAsync(_homeDir, false, false);
            };
            _livePulseTimer.Start();

            // Cloud sync debounce (35s after local usage changes) + periodic 5-minute pull/push
            _cloudDebounceTimer = new DispatcherTimer();
            _cloudDebounceTimer.Interval = TimeSpan.FromSeconds(35);
            _cloudDebounceTimer.Tick += delegate(object s, EventArgs args)
            {
                _cloudDebounceTimer.Stop();
                if (_syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                    StartBackgroundCloudSync();
            };

            _cloudAutoTimer = new DispatcherTimer();
            _cloudAutoTimer.Interval = TimeSpan.FromMinutes(5);
            _cloudAutoTimer.Tick += delegate(object s, EventArgs args)
            {
                if (_syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                    StartBackgroundCloudSync();
            };
            _cloudAutoTimer.Start();

            RebuildDirectoryWatchers(_homeDir);
        }

        private void RebuildDirectoryWatchers(string targetHome)
        {
            DisposeWatchers();
            string[] relDirs = new string[]
            {
                ".claude",
                ".codex",
                ".gemini",
                ".zcode",
                ".kimi-code",
                ".workbuddy",
                ".openclaw",
                ".qwen"
            };

            for (int i = 0; i < relDirs.Length; i++)
            {
                string dir = System.IO.Path.Combine(targetHome, relDirs[i]);
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    FileSystemWatcher fsw = new FileSystemWatcher(dir);
                    fsw.IncludeSubdirectories = true;
                    fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                    FileSystemEventHandler onFsChange = delegate(object s, FileSystemEventArgs ev)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate()
                        {
                            if (_fsDebounceTimer != null)
                            {
                                _fsDebounceTimer.Stop();
                                _fsDebounceTimer.Start();
                            }
                        }));
                    };
                    fsw.Changed += onFsChange;
                    fsw.Created += onFsChange;
                    fsw.EnableRaisingEvents = true;
                    _watchers.Add(fsw);
                }
                catch { }
            }
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
        }

        internal void RunHeadlessUiSelfTest()
        {
            TokenScanner scanner = new TokenScanner(_homeDir, false);
            _scanResult = scanner.Run();
            _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);

            NodeSnapshot homePc = new NodeSnapshot();
            homePc.MachineId = "win-home-pc";
            homePc.MachineName = "家里电脑";
            homePc.MachineType = "windows";
            homePc.SyncedAt = "2026-11-13 15:10:00";
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
            linuxVps.SyncedAt = "2026-11-13 15:15:00";
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
            vb.RateLabel = "$0.27 / $0.42";
            linuxVps.Records.Add(vb);

            _remoteNodes = new List<NodeSnapshot> { homePc, linuxVps };

            UIElement root = (UIElement)Content;
            root.Measure(new Size(1280, 840));
            root.Arrange(new Rect(0, 0, 1280, 840));

            ApplyFetchedRate(new ExchangeRateInfo(6.6851, "ExchangeRate-API", "15:20", true));
            RefreshDashboardView();
            root.UpdateLayout();

            double h0 = _headerBarBorder.ActualHeight;
            double d0 = _deviceStripBorder.ActualHeight;

            // Click single VPS pill, switch tabs (live -> models -> machines -> daily -> methodology), verify 0.0px layout jump!
            _selectedMachineId = "vps-hk-01";
            _leftRankByMachine = true;
            _activeTab = 2;
            _rangeDays = 7;
            RefreshDashboardView();
            UpdateTabVisibility();
            root.UpdateLayout();

            double h1 = _headerBarBorder.ActualHeight;
            double d1 = _deviceStripBorder.ActualHeight;
            if (Math.Abs(h1 - h0) > 0.05 || Math.Abs(d1 - d0) > 0.05)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Layout jump detected! Header: {0} -> {1}, DeviceStrip: {2} -> {3}",
                    h0, h1, d0, d1));
            }

            // Switch to Local PC pill and Live Request Stream tab
            _selectedMachineId = _localNode.MachineId;
            _activeTab = 0;
            RefreshDashboardView();
            UpdateTabVisibility();
            root.UpdateLayout();

            double h2 = _headerBarBorder.ActualHeight;
            double d2 = _deviceStripBorder.ActualHeight;
            if (Math.Abs(h2 - h0) > 0.05 || Math.Abs(d2 - d0) > 0.05)
                throw new InvalidOperationException("Layout jump detected on local device switch!");

            // Test SyncWindow 2-tab layout
            SyncWindow sw = new SyncWindow(null, _syncCfg, _scanResult, null, 1);
            UIElement swContent = (UIElement)sw.Content;
            swContent.Measure(new Size(620, 520));
            swContent.Arrange(new Rect(0, 0, 620, 520));
            swContent.UpdateLayout();

            // Test Light & Dark theme toggle stability
            _selectedMachineId = null;
            _leftRankByMachine = false;
            _rangeDays = 0;
            OnToggleThemeClick(this, new RoutedEventArgs());
            root.UpdateLayout();
            OnToggleThemeClick(this, new RoutedEventArgs());
            root.UpdateLayout();

            double h3 = _headerBarBorder.ActualHeight;
            double d3 = _deviceStripBorder.ActualHeight;
            if (Math.Abs(h3 - h0) > 0.05 || Math.Abs(d3 - d0) > 0.05)
                throw new InvalidOperationException("Layout jump detected after theme toggle!");
        }

        private void TryLoadExeIcon()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                using (Drawing.Icon ico = Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    if (ico != null)
                    {
                        Icon = Imaging.CreateBitmapSourceFromHIcon(
                            ico.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                }
            }
            catch { }
        }

        private static SolidColorBrush BrushHex(string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void ApplyThemeResources()
        {
            if (_isDark)
            {
                Resources["BgWindow"] = BrushHex("#0B0F19");
                Resources["BgCard"] = BrushHex("#111827");
                Resources["BgSurface"] = BrushHex("#0F172A");
                Resources["BgElevated"] = BrushHex("#1E293B");
                Resources["BgHover"] = BrushHex("#263449");
                Resources["BgRowAlt"] = BrushHex("#131C2E");
                Resources["BgSelected"] = BrushHex("#172B44");
                Resources["BorderCard"] = BrushHex("#1F2937");
                Resources["BorderStrong"] = BrushHex("#334155");
                Resources["FgPrimary"] = BrushHex("#F8FAFC");
                Resources["FgSecondary"] = BrushHex("#94A3B8");
                Resources["FgMuted"] = BrushHex("#64748B");
                Resources["AccentBlue"] = BrushHex("#38BDF8");
                Resources["AccentIndigo"] = BrushHex("#818CF8");
                Resources["AccentEmerald"] = BrushHex("#10B981");
                Resources["AccentAmber"] = BrushHex("#F59E0B");
                Resources["WarnBg"] = BrushHex("#2C1B12");
                Resources["WarnBorder"] = BrushHex("#9A3412");
                Resources["WarnFg"] = BrushHex("#FDBA74");
            }
            else
            {
                Resources["BgWindow"] = BrushHex("#F1F5F9");
                Resources["BgCard"] = BrushHex("#FFFFFF");
                Resources["BgSurface"] = BrushHex("#F8FAFC");
                Resources["BgElevated"] = BrushHex("#E2E8F0");
                Resources["BgHover"] = BrushHex("#CBD5E1");
                Resources["BgRowAlt"] = BrushHex("#F8FAFC");
                Resources["BgSelected"] = BrushHex("#DBEAFE");
                Resources["BorderCard"] = BrushHex("#E2E8F0");
                Resources["BorderStrong"] = BrushHex("#CBD5E1");
                Resources["FgPrimary"] = BrushHex("#0F172A");
                Resources["FgSecondary"] = BrushHex("#475569");
                Resources["FgMuted"] = BrushHex("#64748B");
                Resources["AccentBlue"] = BrushHex("#0284C7");
                Resources["AccentIndigo"] = BrushHex("#4F46E5");
                Resources["AccentEmerald"] = BrushHex("#059669");
                Resources["AccentAmber"] = BrushHex("#D97706");
                Resources["WarnBg"] = BrushHex("#FFF7ED");
                Resources["WarnBorder"] = BrushHex("#FDBA74");
                Resources["WarnFg"] = BrushHex("#9A3412");
            }
        }

        // -------------------- Strict Zero-Jump Fixed-Viewport Layout (NO Outer ScrollViewer, NO Modal Overlay) --------------------
        private UIElement BuildLayout()
        {
            Grid mainRows = new Grid();
            mainRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });  // Row 0: Locked Header Bar
            mainRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });  // Row 1: Locked Device Rail
            mainRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Row 2: Optional Warning Banner
            mainRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });  // Row 3: 4 KPI Cards
            mainRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(226) }); // Row 4: Middle Insights
            mainRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 5: Active Table Card
            mainRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });  // Row 6: Live Status Bar

            _headerBarBorder = BuildHeaderBar();
            Grid.SetRow(_headerBarBorder, 0);
            mainRows.Children.Add(_headerBarBorder);

            _deviceStripBorder = BuildDeviceFilterStrip();
            Grid.SetRow(_deviceStripBorder, 1);
            mainRows.Children.Add(_deviceStripBorder);

            _warningBanner = BuildWarningBanner();
            Grid.SetRow(_warningBanner, 2);
            mainRows.Children.Add(_warningBanner);

            UIElement kpiRow = BuildKpiCards();
            Grid.SetRow(kpiRow, 3);
            mainRows.Children.Add(kpiRow);

            UIElement midRow = BuildMiddleSection();
            Grid.SetRow(midRow, 4);
            mainRows.Children.Add(midRow);

            UIElement botRow = BuildBottomSection();
            Grid.SetRow(botRow, 5);
            mainRows.Children.Add(botRow);

            UIElement statusRow = BuildStatusBar();
            Grid.SetRow(statusRow, 6);
            mainRows.Children.Add(statusRow);

            return mainRows;
        }

        private Border BuildHeaderBar()
        {
            Border bar = new Border();
            bar.Height = 52;
            bar.Padding = new Thickness(18, 0, 18, 0);
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.SetResourceReference(Border.BackgroundProperty, "BgCard");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Static Brand Identity (Never changes width!)
            StackPanel brand = new StackPanel();
            brand.Orientation = Orientation.Horizontal;
            brand.VerticalAlignment = VerticalAlignment.Center;

            Border logoBadge = new Border();
            logoBadge.Width = 30;
            logoBadge.Height = 30;
            logoBadge.CornerRadius = new CornerRadius(8);
            logoBadge.Margin = new Thickness(0, 0, 10, 0);
            LinearGradientBrush lg = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#0EA5E9"),
                (Color)ColorConverter.ConvertFromString("#6366F1"),
                45.0);
            lg.Freeze();
            logoBadge.Background = lg;

            TextBlock logoChar = new TextBlock();
            logoChar.Text = "微";
            logoChar.Foreground = Brushes.White;
            logoChar.FontWeight = FontWeights.Bold;
            logoChar.FontSize = 14.5;
            logoChar.HorizontalAlignment = HorizontalAlignment.Center;
            logoChar.VerticalAlignment = VerticalAlignment.Center;
            logoBadge.Child = logoChar;
            brand.Children.Add(logoBadge);

            TextBlock mainTitle = new TextBlock();
            mainTitle.Text = "知微 · TokLens";
            mainTitle.FontSize = 15.5;
            mainTitle.FontWeight = FontWeights.Bold;
            mainTitle.VerticalAlignment = VerticalAlignment.Center;
            mainTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            brand.Children.Add(mainTitle);

            Border verTag = new Border();
            verTag.CornerRadius = new CornerRadius(4);
            verTag.Padding = new Thickness(6, 1, 6, 1);
            verTag.Margin = new Thickness(8, 1, 0, 0);
            verTag.VerticalAlignment = VerticalAlignment.Center;
            verTag.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            TextBlock verTxt = new TextBlock();
            verTxt.Text = "v2.1";
            verTxt.FontSize = 11;
            verTxt.FontWeight = FontWeights.SemiBold;
            verTxt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
            verTag.Child = verTxt;
            brand.Children.Add(verTag);

            Grid.SetColumn(brand, 0);
            g.Children.Add(brand);

            // Col 1: Centered Segmented Time Filter
            string[] rLabels = new string[] { "全部", "30天", "7天", "3天", "今日" };
            _rangeBtns = new Button[rLabels.Length];
            Border rangeBox = CreateSegContainer();
            rangeBox.HorizontalAlignment = HorizontalAlignment.Center;
            rangeBox.VerticalAlignment = VerticalAlignment.Center;
            StackPanel rangeSp = (StackPanel)rangeBox.Child;
            for (int i = 0; i < rLabels.Length; i++)
            {
                int idx = i;
                Button b = CreateSegButton(rLabels[i]);
                b.Click += delegate(object s, RoutedEventArgs e)
                {
                    _rangeDays = PresetRangeDays[idx];
                    _suppressCustomDaysEvent = true;
                    try { if (_customDaysInput != null) _customDaysInput.Text = ""; }
                    finally { _suppressCustomDaysEvent = false; }
                    UpdateSegVisuals(_rangeBtns, idx);
                    RefreshDashboardView();
                };
                _rangeBtns[i] = b;
                rangeSp.Children.Add(b);
            }
            UpdateSegVisuals(_rangeBtns, 0);

            Border sep = new Border { Width = 1, Height = 14, Margin = new Thickness(5, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            sep.SetResourceReference(Border.BackgroundProperty, "BorderStrong");
            rangeSp.Children.Add(sep);

            TextBlock nearLbl = new TextBlock { Text = "近", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };
            nearLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            rangeSp.Children.Add(nearLbl);

            _customDaysInput = MakeHeaderMiniInput("", 32);
            _customDaysInput.ToolTip = "输入任意天数 N（如 14 或 90），查看过去 N 天总消耗";
            _customDaysInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (_suppressCustomDaysEvent) return;
                int customD;
                if (int.TryParse(_customDaysInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out customD) && customD > 0 && customD <= 3650)
                {
                    _rangeDays = customD;
                    UpdateSegVisuals(_rangeBtns, -1);
                    RefreshDashboardView();
                }
            };
            rangeSp.Children.Add(_customDaysInput);

            TextBlock dayLbl = new TextBlock { Text = "天", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 5, 0) };
            dayLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            rangeSp.Children.Add(dayLbl);

            Grid.SetColumn(rangeBox, 1);
            g.Children.Add(rangeBox);

            // Col 2: Clean Right Controls (Unified Currency/FX Pill + Cloud Sync + Fixed ↻ + Fixed ⋯ Menu)
            StackPanel rightSp = new StackPanel();
            rightSp.Orientation = Orientation.Horizontal;
            rightSp.VerticalAlignment = VerticalAlignment.Center;

            // 1. Unified Currency + FX Pill
            Border fxPill = CreateSegContainer();
            fxPill.Margin = new Thickness(0, 0, 8, 0);
            StackPanel fxInner = (StackPanel)fxPill.Child;

            string[] cLabels = new string[] { "¥", "$" };
            _currencyBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button cb = CreateSegButton(cLabels[i]);
                cb.Padding = new Thickness(8, 3, 8, 3);
                cb.Click += delegate(object s, RoutedEventArgs e)
                {
                    _isCny = (idx == 0);
                    UpdateSegVisuals(_currencyBtns, idx);
                    RefreshDashboardView();
                };
                _currencyBtns[i] = cb;
                fxInner.Children.Add(cb);
            }
            UpdateSegVisuals(_currencyBtns, 0);

            Border fxSep = new Border { Width = 1, Height = 14, Margin = new Thickness(4, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            fxSep.SetResourceReference(Border.BackgroundProperty, "BorderStrong");
            fxInner.Children.Add(fxSep);

            _rateInput = MakeHeaderMiniInput(_usdToCny.ToString("0.00##", CultureInfo.InvariantCulture), 48);
            _rateInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (_suppressRateEvent) return;
                double parsed;
                if (double.TryParse(_rateInput.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) && parsed > 0.1 && parsed < 100.0)
                {
                    _usdToCny = parsed;
                    UpdateFxBadgeUi(false, true);
                    RefreshDashboardView();
                }
            };
            fxInner.Children.Add(_rateInput);

            _fxStatusBtnBorder = new Border();
            _fxStatusBtnBorder.CornerRadius = new CornerRadius(4);
            _fxStatusBtnBorder.Padding = new Thickness(5, 2, 5, 2);
            _fxStatusBtnBorder.Margin = new Thickness(4, 0, 2, 0);
            _fxStatusBtnBorder.VerticalAlignment = VerticalAlignment.Center;
            _fxStatusBtnBorder.Cursor = Cursors.Hand;
            _fxStatusBtnBorder.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            _fxStatusBtnText = new TextBlock { Text = "实时", FontSize = 10.5, FontWeight = FontWeights.Medium };
            _fxStatusBtnText.SetResourceReference(TextBlock.ForegroundProperty, "AccentEmerald");
            _fxStatusBtnBorder.Child = _fxStatusBtnText;
            _fxStatusBtnBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                StartExchangeRateSyncAsync(true);
            };
            fxInner.Children.Add(_fxStatusBtnBorder);
            rightSp.Children.Add(fxPill);

            // 2. Primary Multi-Device Sync Button
            _syncBtn = CreateActionButton("多端同步", true);
            _syncBtn.Height = 30;
            _syncBtn.Margin = new Thickness(0, 0, 6, 0);
            _syncBtn.ToolTip = "配置多台 Windows 电脑与 Linux VPS 自动云同步";
            _syncBtn.Click += delegate(object s, RoutedEventArgs e) { OpenSyncDialog(0); };
            rightSp.Children.Add(_syncBtn);

            // 3. Fixed-Width Silent Refresh Button (Never shifts width!)
            _refreshBtn = CreateActionButton("↻", false);
            _refreshBtn.Width = 32;
            _refreshBtn.Height = 30;
            _refreshBtn.Padding = new Thickness(0);
            _refreshBtn.FontSize = 14;
            _refreshBtn.Margin = new Thickness(0, 0, 6, 0);
            _refreshBtn.ToolTip = "立即执行后台增量刷新（无弹窗遮挡）";
            _refreshBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                RunSilentScanAsync(_homeDir, true, true);
                if (_syncCfg != null && _syncCfg.IsConfigured && !_syncCfg.AutoSync)
                    StartBackgroundCloudSync();
                StartExchangeRateSyncAsync(false);
                StartCloudPricingSyncAsync(false);
            };
            rightSp.Children.Add(_refreshBtn);

            // 4. Fixed-Width More Menu Button (⋯)
            _moreBtn = CreateActionButton("⋯", false);
            _moreBtn.Width = 32;
            _moreBtn.Height = 30;
            _moreBtn.Padding = new Thickness(0);
            _moreBtn.FontSize = 14;
            _moreBtn.ToolTip = "导出报告、目录切换、价格库更新、防清理归档状态与外观主题";
            ContextMenu moreMenu = BuildMoreContextMenu();
            _moreBtn.ContextMenu = moreMenu;
            _moreBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                moreMenu.PlacementTarget = _moreBtn;
                moreMenu.Placement = PlacementMode.Bottom;
                moreMenu.IsOpen = true;
            };
            rightSp.Children.Add(_moreBtn);

            Grid.SetColumn(rightSp, 2);
            g.Children.Add(rightSp);

            bar.Child = g;
            return bar;
        }

        private TextBox MakeHeaderMiniInput(string text, double width)
        {
            TextBox tb = new TextBox();
            tb.Text = text;
            tb.Width = width;
            tb.Height = 22;
            tb.FontSize = 11.5;
            tb.TextAlignment = TextAlignment.Center;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;
            tb.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
            tb.SetResourceReference(TextBox.CaretBrushProperty, "FgPrimary");

            ControlTemplate tpl = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "MiniBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgCard"));
            bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));
            FrameworkElementFactory sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            sv.SetValue(ScrollViewer.MarginProperty, new Thickness(2, 0, 2, 0));
            sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(sv);
            tpl.VisualTree = bd;

            Trigger focusTrig = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focusTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBlue"), "MiniBd"));
            tpl.Triggers.Add(focusTrig);
            tb.Template = tpl;
            return tb;
        }

        private ContextMenu BuildMoreContextMenu()
        {
            ContextMenu cm = new ContextMenu();

            MenuItem mHtml = new MenuItem { Header = "导出交互式网页报告 (HTML)" };
            mHtml.Click += OnOpenWebReportClick;
            cm.Items.Add(mHtml);

            MenuItem mCsv = new MenuItem { Header = "导出多端账单明细表格 (CSV)" };
            mCsv.Click += OnExportCsvClick;
            cm.Items.Add(mCsv);

            cm.Items.Add(new Separator());

            MenuItem mCloudNow = new MenuItem { Header = "立即同步多端云端账单 (Gist / WebDAV)" };
            mCloudNow.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_syncCfg != null && _syncCfg.IsConfigured)
                    StartBackgroundCloudSync();
                else
                    OpenSyncDialog(0);
            };
            cm.Items.Add(mCloudNow);

            MenuItem mPrice = new MenuItem { Header = "立即强制刷新云端模型价格库 (models.dev + LiteLLM + OpenRouter)" };
            mPrice.Click += delegate(object s, RoutedEventArgs e) { StartCloudPricingSyncAsync(true); };
            cm.Items.Add(mPrice);

            MenuItem mFx = new MenuItem { Header = "立即刷新在线美元实时汇率 (USD/CNY)" };
            mFx.Click += delegate(object s, RoutedEventArgs e) { StartExchangeRateSyncAsync(true); };
            cm.Items.Add(mFx);

            MenuItem mDir = new MenuItem { Header = "切换扫描的用户主目录..." };
            mDir.Click += OnChooseDirectoryClick;
            cm.Items.Add(mDir);

            cm.Items.Add(new Separator());

            _themeMenuItem = new MenuItem { Header = "切换为浅色主题" };
            _themeMenuItem.Click += OnToggleThemeClick;
            cm.Items.Add(_themeMenuItem);

            MenuItem mArchive = new MenuItem { Header = "查看防清理归档保护状态" };
            mArchive.Click += OnShowArchiveProtectionStatusClick;
            cm.Items.Add(mArchive);

            MenuItem mAbout = new MenuItem { Header = "查看统计口径与架构说明" };
            mAbout.Click += delegate(object s, RoutedEventArgs e)
            {
                _activeTab = 4;
                UpdateSegVisuals(_tabBtns, -1);
                UpdateTabVisibility();
            };
            cm.Items.Add(mAbout);

            return cm;
        }

        private Border BuildDeviceFilterStrip()
        {
            Border strip = new Border();
            strip.Height = 40;
            strip.Padding = new Thickness(18, 0, 18, 0);
            strip.BorderThickness = new Thickness(0, 0, 0, 1);
            strip.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            strip.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel dock = new DockPanel();

            // Right-docked static Add Device / VPS action link
            Border addBtn = new Border();
            addBtn.CornerRadius = new CornerRadius(6);
            addBtn.Padding = new Thickness(10, 4, 10, 4);
            addBtn.Margin = new Thickness(10, 0, 0, 0);
            addBtn.BorderThickness = new Thickness(1);
            addBtn.VerticalAlignment = VerticalAlignment.Center;
            addBtn.Cursor = Cursors.Hand;
            addBtn.SetResourceReference(Border.BackgroundProperty, "BgCard");
            addBtn.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            addBtn.ToolTip = "打开多端接入向导：生成配对码或复制 Linux VPS 一键部署命令";

            TextBlock addTxt = new TextBlock();
            addTxt.Text = "+ 接入电脑 / Linux VPS";
            addTxt.FontSize = 11.5;
            addTxt.FontWeight = FontWeights.Medium;
            addTxt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
            addBtn.Child = addTxt;
            addBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { OpenSyncDialog(1); };
            addBtn.MouseEnter += delegate(object s, MouseEventArgs e) { addBtn.SetResourceReference(Border.BorderBrushProperty, "AccentBlue"); };
            addBtn.MouseLeave += delegate(object s, MouseEventArgs e) { addBtn.SetResourceReference(Border.BorderBrushProperty, "BorderCard"); };

            DockPanel.SetDock(addBtn, Dock.Right);
            dock.Children.Add(addBtn);

            // Single-row horizontal ScrollViewer (Never wraps to 2nd row -> 0.0px vertical jump!)
            _deviceScroll = new ScrollViewer();
            _deviceScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _deviceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _deviceScroll.VerticalAlignment = VerticalAlignment.Center;
            _deviceScroll.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                if (_deviceScroll.ScrollableWidth > 0)
                {
                    _deviceScroll.ScrollToHorizontalOffset(_deviceScroll.HorizontalOffset - e.Delta * 0.5);
                    e.Handled = true;
                }
            };

            _devicePillsStack = new StackPanel();
            _devicePillsStack.Orientation = Orientation.Horizontal;
            _devicePillsStack.VerticalAlignment = VerticalAlignment.Center;
            _deviceScroll.Content = _devicePillsStack;

            dock.Children.Add(_deviceScroll);
            strip.Child = dock;
            return strip;
        }

        private Border BuildWarningBanner()
        {
            Border b = new Border();
            b.Visibility = Visibility.Collapsed;
            b.Margin = new Thickness(18, 8, 18, 0);
            b.Padding = new Thickness(12, 6, 12, 6);
            b.CornerRadius = new CornerRadius(7);
            b.BorderThickness = new Thickness(1);
            b.SetResourceReference(Border.BackgroundProperty, "WarnBg");
            b.SetResourceReference(Border.BorderBrushProperty, "WarnBorder");

            _warningText = new TextBlock();
            _warningText.FontSize = 11.5;
            _warningText.TextTrimming = TextTrimming.CharacterEllipsis;
            _warningText.SetResourceReference(TextBlock.ForegroundProperty, "WarnFg");
            b.Child = _warningText;
            return b;
        }

        private UIElement BuildKpiCards()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(18, 10, 18, 0);
            for (int i = 0; i < 4; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            g.Children.Add(MakeKpiCard(0, new Thickness(0, 0, 8, 0), "API 等效总花费", "AccentBlue", out _kpiCostVal, out _kpiCostSub));
            g.Children.Add(MakeKpiCard(1, new Thickness(4, 0, 4, 0), "累计总 Token", "FgPrimary", out _kpiTokVal, out _kpiTokSub));
            g.Children.Add(MakeKpiCard(2, new Thickness(4, 0, 4, 0), "Prompt Cache 命中率", "AccentAmber", out _kpiHitVal, out _kpiHitSub));
            g.Children.Add(MakeKpiCard(3, new Thickness(8, 0, 0, 0), "Prompt Cache 累计省钱", "AccentEmerald", out _kpiSaveVal, out _kpiSaveSub));

            return g;
        }

        private Border MakeKpiCard(int col, Thickness margin, string label, string valBrushKey, out TextBlock valBlock, out TextBlock subBlock)
        {
            Border card = new Border();
            card.Margin = margin;
            card.Padding = new Thickness(15, 10, 15, 10);
            card.CornerRadius = new CornerRadius(10);
            card.BorderThickness = new Thickness(1);
            card.SetResourceReference(Border.BackgroundProperty, "BgCard");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            TextBlock lbl = new TextBlock();
            lbl.Text = label;
            lbl.FontSize = 11.5;
            lbl.FontWeight = FontWeights.Medium;
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(lbl);

            valBlock = new TextBlock();
            valBlock.Text = "—";
            valBlock.FontSize = 23;
            valBlock.FontWeight = FontWeights.Bold;
            valBlock.Margin = new Thickness(0, 3, 0, 3);
            valBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            valBlock.SetResourceReference(TextBlock.ForegroundProperty, valBrushKey);
            sp.Children.Add(valBlock);

            subBlock = new TextBlock();
            subBlock.Text = "等待数据加载…";
            subBlock.FontSize = 11;
            subBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            subBlock.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(subBlock);

            card.Child = sp;
            Grid.SetColumn(card, col);
            return card;
        }
        private UIElement BuildMiddleSection()
        {
            Grid mid = new Grid();
            mid.Margin = new Thickness(18, 10, 18, 0);
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });

            // Left Card: Ranking by Tool or Device
            Border leftCard = new Border();
            leftCard.CornerRadius = new CornerRadius(10);
            leftCard.BorderThickness = new Thickness(1);
            leftCard.Padding = new Thickness(14, 10, 14, 10);
            leftCard.SetResourceReference(Border.BackgroundProperty, "BgCard");
            leftCard.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel leftDock = new DockPanel();

            DockPanel leftHdr = new DockPanel();
            leftHdr.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(leftHdr, Dock.Top);

            StackPanel leftSegs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(leftSegs, Dock.Left);

            string[] modeLabels = new string[] { "按工具", "按设备" };
            _leftModeBtns = new Button[2];
            Border modeBox = CreateSegContainer();
            modeBox.Margin = new Thickness(0, 0, 6, 0);
            StackPanel modeSp = (StackPanel)modeBox.Child;
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button mb = CreateSegButton(modeLabels[i]);
                mb.Padding = new Thickness(8, 3, 8, 3);
                mb.Click += delegate(object s, RoutedEventArgs e)
                {
                    _leftRankByMachine = (idx == 1);
                    UpdateSegVisuals(_leftModeBtns, idx);
                    RefreshDashboardView();
                };
                _leftModeBtns[i] = mb;
                modeSp.Children.Add(mb);
            }
            UpdateSegVisuals(_leftModeBtns, 0);
            leftSegs.Children.Add(modeBox);

            string[] metricLabels = new string[] { "按花费", "按Token" };
            _metricBtns = new Button[2];
            Border metricBox = CreateSegContainer();
            StackPanel metricSp = (StackPanel)metricBox.Child;
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button mb = CreateSegButton(metricLabels[i]);
                mb.Padding = new Thickness(8, 3, 8, 3);
                mb.Click += delegate(object s, RoutedEventArgs e)
                {
                    _rankByCost = (idx == 0);
                    UpdateSegVisuals(_metricBtns, idx);
                    RefreshDashboardView();
                };
                _metricBtns[i] = mb;
                metricSp.Children.Add(mb);
            }
            UpdateSegVisuals(_metricBtns, 0);
            leftSegs.Children.Add(metricBox);
            leftHdr.Children.Add(leftSegs);

            _clearFilterBtn = CreateActionButton("清除筛选 ×", false);
            _clearFilterBtn.FontSize = 11;
            _clearFilterBtn.Padding = new Thickness(7, 2, 7, 2);
            _clearFilterBtn.Visibility = Visibility.Collapsed;
            _clearFilterBtn.VerticalAlignment = VerticalAlignment.Center;
            _clearFilterBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _clearFilterBtn.Click += OnClearFilterClick;
            DockPanel.SetDock(_clearFilterBtn, Dock.Right);
            leftHdr.Children.Add(_clearFilterBtn);

            leftDock.Children.Add(leftHdr);

            ScrollViewer toolsSv = new ScrollViewer();
            toolsSv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            toolsSv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _toolsStack = new StackPanel();
            _toolsStack.Margin = new Thickness(0, 0, 4, 0);
            toolsSv.Content = _toolsStack;
            leftDock.Children.Add(toolsSv);

            leftCard.Child = leftDock;
            Grid.SetColumn(leftCard, 0);
            mid.Children.Add(leftCard);

            // Right Card: Daily Trend Bar Chart
            Border rightCard = new Border();
            rightCard.CornerRadius = new CornerRadius(10);
            rightCard.BorderThickness = new Thickness(1);
            rightCard.Padding = new Thickness(14, 10, 14, 10);
            rightCard.SetResourceReference(Border.BackgroundProperty, "BgCard");
            rightCard.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel rightDock = new DockPanel();
            DockPanel rightHdr = new DockPanel();
            rightHdr.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(rightHdr, Dock.Top);

            _trendTitleText = new TextBlock();
            _trendTitleText.Text = "每日消耗趋势";
            _trendTitleText.FontSize = 13;
            _trendTitleText.FontWeight = FontWeights.SemiBold;
            _trendTitleText.VerticalAlignment = VerticalAlignment.Center;
            _trendTitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            DockPanel.SetDock(_trendTitleText, Dock.Left);
            rightHdr.Children.Add(_trendTitleText);

            _trendHoverText = new TextBlock();
            _trendHoverText.Text = "悬停柱状图查看单日详情";
            _trendHoverText.FontSize = 11.5;
            _trendHoverText.VerticalAlignment = VerticalAlignment.Center;
            _trendHoverText.HorizontalAlignment = HorizontalAlignment.Right;
            _trendHoverText.TextTrimming = TextTrimming.CharacterEllipsis;
            _trendHoverText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            DockPanel.SetDock(_trendHoverText, Dock.Right);
            rightHdr.Children.Add(_trendHoverText);
            rightDock.Children.Add(rightHdr);

            _chartHost = new Border();
            _chartHost.Background = Brushes.Transparent;
            _chartCanvas = new Canvas();
            _chartCanvas.ClipToBounds = true;
            _chartHost.Child = _chartCanvas;
            _chartHost.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                RenderTrendChart();
            };
            rightDock.Children.Add(_chartHost);

            rightCard.Child = rightDock;
            Grid.SetColumn(rightCard, 2);
            mid.Children.Add(rightCard);

            return mid;
        }

        private UIElement BuildBottomSection()
        {
            Border card = new Border();
            card.Margin = new Thickness(18, 10, 18, 10);
            card.CornerRadius = new CornerRadius(10);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(14, 10, 14, 10);
            card.SetResourceReference(Border.BackgroundProperty, "BgCard");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel dock = new DockPanel();

            DockPanel tabHdr = new DockPanel();
            tabHdr.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(tabHdr, Dock.Top);

            string[] tLabels = new string[]
            {
                "实时请求流",
                "模型账单明细",
                "设备单列对比",
                "每日账单流水"
            };
            _tabBtns = new Button[tLabels.Length];
            Border tabBox = CreateSegContainer();
            DockPanel.SetDock(tabBox, Dock.Left);
            StackPanel tabSp = (StackPanel)tabBox.Child;
            for (int i = 0; i < tLabels.Length; i++)
            {
                int idx = i;
                Button b = CreateSegButton(tLabels[i]);
                b.Padding = new Thickness(11, 4, 11, 4);
                b.Click += delegate(object s, RoutedEventArgs e)
                {
                    _activeTab = idx;
                    UpdateSegVisuals(_tabBtns, idx);
                    UpdateTabVisibility();
                };
                _tabBtns[i] = b;
                tabSp.Children.Add(b);
            }
            UpdateSegVisuals(_tabBtns, 0);
            tabHdr.Children.Add(tabBox);

            StackPanel rightHintSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(rightHintSp, Dock.Right);

            TextBlock tipRight = new TextBlock();
            tipRight.Text = "本机日志变动毫秒级自动捕获 · 点击左侧工具或顶部设备可联动过滤";
            tipRight.FontSize = 11;
            tipRight.VerticalAlignment = VerticalAlignment.Center;
            tipRight.Margin = new Thickness(0, 0, 10, 0);
            tipRight.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            rightHintSp.Children.Add(tipRight);

            _methodologyLinkBtn = CreateSegButton("口径与说明");
            _methodologyLinkBtn.FontSize = 11.5;
            _methodologyLinkBtn.Padding = new Thickness(8, 3, 8, 3);
            _methodologyLinkBtn.SetResourceReference(Control.ForegroundProperty, "AccentBlue");
            _methodologyLinkBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _activeTab = (_activeTab == 4) ? 0 : 4;
                UpdateSegVisuals(_tabBtns, _activeTab == 4 ? -1 : _activeTab);
                UpdateTabVisibility();
            };
            rightHintSp.Children.Add(_methodologyLinkBtn);
            tabHdr.Children.Add(rightHintSp);

            dock.Children.Add(tabHdr);

            Grid contentArea = new Grid();
            _liveGrid = CreateLiveRequestDataGrid();
            _modelGrid = CreateModelDataGrid();
            _machineGrid = CreateMachineDataGrid();
            _dailyGrid = CreateDailyDataGrid();
            _aboutPanel = BuildMethodologyPanel();

            contentArea.Children.Add(_liveGrid);
            contentArea.Children.Add(_modelGrid);
            contentArea.Children.Add(_machineGrid);
            contentArea.Children.Add(_dailyGrid);
            contentArea.Children.Add(_aboutPanel);
            UpdateTabVisibility();

            dock.Children.Add(contentArea);
            card.Child = dock;
            return card;
        }

        private UIElement BuildStatusBar()
        {
            Border bar = new Border();
            bar.Height = 28;
            bar.Padding = new Thickness(18, 0, 18, 0);
            bar.BorderThickness = new Thickness(0, 1, 0, 0);
            bar.SetResourceReference(Border.BackgroundProperty, "BgCard");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Scope & Scan Performance Summary
            DockPanel leftSp = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            _statusPulseDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            _statusPulseDot.SetResourceReference(Shape.FillProperty, "AccentEmerald");
            DockPanel.SetDock(_statusPulseDot, Dock.Left);
            leftSp.Children.Add(_statusPulseDot);

            _statusLeftText = new TextBlock();
            _statusLeftText.Text = "实时监听已就绪 · 正在加载本地快照…";
            _statusLeftText.FontSize = 11;
            _statusLeftText.VerticalAlignment = VerticalAlignment.Center;
            _statusLeftText.TextTrimming = TextTrimming.CharacterEllipsis;
            _statusLeftText.ToolTip = "双层防清理持久归档保护已启用\n即使定期清理本地或 VPS 日志文件，已入库的历史账单依然永久保留不归零";
            _statusLeftText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            leftSp.Children.Add(_statusLeftText);
            Grid.SetColumn(leftSp, 0);
            g.Children.Add(leftSp);

            // Col 1: Live Latest-Request Ticker (Click jumps straight to 实时请求流 tab!)
            _statusLiveTickerBorder = new Border();
            _statusLiveTickerBorder.CornerRadius = new CornerRadius(4);
            _statusLiveTickerBorder.Padding = new Thickness(8, 2, 8, 2);
            _statusLiveTickerBorder.Margin = new Thickness(8, 0, 8, 0);
            _statusLiveTickerBorder.VerticalAlignment = VerticalAlignment.Center;
            _statusLiveTickerBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _statusLiveTickerBorder.Cursor = Cursors.Hand;
            _statusLiveTickerBorder.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _statusLiveTickerBorder.ToolTip = "点击切换至「实时请求流」查看每条请求的 Token、TPS 与单条金额";
            _statusLiveTickerBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                _activeTab = 0;
                UpdateSegVisuals(_tabBtns, 0);
                UpdateTabVisibility();
            };

            _statusLiveTickerText = new TextBlock();
            _statusLiveTickerText.Text = "最新请求: 暂无记录";
            _statusLiveTickerText.FontSize = 11;
            _statusLiveTickerText.TextTrimming = TextTrimming.CharacterEllipsis;
            _statusLiveTickerText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
            _statusLiveTickerBorder.Child = _statusLiveTickerText;
            Grid.SetColumn(_statusLiveTickerBorder, 1);
            g.Children.Add(_statusLiveTickerBorder);

            // Col 2: Cloud Pricing Status + Multi-Device Sync Status
            StackPanel rightSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _statusPriceBorder = new Border { Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand };
            _statusPriceBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { StartCloudPricingSyncAsync(true); };
            _statusPriceText = new TextBlock { Text = "价格库加载中", FontSize = 11 };
            _statusPriceText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            _statusPriceBorder.Child = _statusPriceText;
            rightSp.Children.Add(_statusPriceBorder);

            TextBlock dotSep = new TextBlock { Text = "·", Margin = new Thickness(4, 0, 4, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            dotSep.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            rightSp.Children.Add(dotSep);

            _statusCloudBorder = new Border { Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand };
            _statusCloudBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { OpenSyncDialog(0); };
            _statusCloudText = new TextBlock { Text = "多端同步未配置", FontSize = 11 };
            _statusCloudText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            _statusCloudBorder.Child = _statusCloudText;
            rightSp.Children.Add(_statusCloudBorder);

            Grid.SetColumn(rightSp, 2);
            g.Children.Add(rightSp);

            bar.Child = g;
            return bar;
        }

        private void UpdateTabVisibility()
        {
            if (_liveGrid == null) return;
            _liveGrid.Visibility = (_activeTab == 0) ? Visibility.Visible : Visibility.Collapsed;
            _modelGrid.Visibility = (_activeTab == 1) ? Visibility.Visible : Visibility.Collapsed;
            _machineGrid.Visibility = (_activeTab == 2) ? Visibility.Visible : Visibility.Collapsed;
            _dailyGrid.Visibility = (_activeTab == 3) ? Visibility.Visible : Visibility.Collapsed;
            _aboutPanel.Visibility = (_activeTab == 4) ? Visibility.Visible : Visibility.Collapsed;

            if (_methodologyLinkBtn != null)
            {
                if (_activeTab == 4)
                    _methodologyLinkBtn.SetResourceReference(Control.BackgroundProperty, "BgElevated");
                else
                    _methodologyLinkBtn.Background = Brushes.Transparent;
            }
        }

        private DataGrid CreateLiveRequestDataGrid()
        {
            DataGrid dg = CreateBaseDataGrid();
            dg.Columns.Add(MakeColumn(dg, "请求时间", "TimeText", "TimestampMs", new DataGridLength(116), false, false));
            dg.Columns.Add(MakeColumn(dg, "所属工具", "Tool", "Tool", new DataGridLength(1.15, DataGridLengthUnitType.Star), false, true));
            dg.Columns.Add(MakeColumn(dg, "模型名称", "Model", "Model", new DataGridLength(1.35, DataGridLengthUnitType.Star), false, true));
            dg.Columns.Add(MakeColumn(dg, "新增输入", "FreshInputText", "FreshInput", new DataGridLength(86), true, false));
            dg.Columns.Add(MakeColumn(dg, "缓存读取", "CacheReadText", "CacheRead", new DataGridLength(88), true, false));
            dg.Columns.Add(MakeColumn(dg, "缓存写入", "CacheWriteText", "CacheWrite", new DataGridLength(82), true, false));
            dg.Columns.Add(MakeColumn(dg, "输出 Token", "OutputText", "Output", new DataGridLength(88), true, false));
            dg.Columns.Add(MakeColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(88), true, true));
            dg.Columns.Add(MakeColumn(dg, "耗时", "DurationText", "DurationMs", new DataGridLength(74), true, false));
            dg.Columns.Add(MakeColumn(dg, "速率 (TPS)", "TpsText", "Tps", new DataGridLength(96), true, true));
            dg.Columns.Add(MakeColumn(dg, "单条金额", "FormattedCost", "CostUsd", new DataGridLength(98), true, true));
            return dg;
        }

        private DataGrid CreateModelDataGrid()
        {
            DataGrid dg = CreateBaseDataGrid();
            dg.Columns.Add(MakeColumn(dg, "模型名称", "Model", "Model", new DataGridLength(1.6, DataGridLengthUnitType.Star), false, true));
            dg.Columns.Add(MakeColumn(dg, "所属工具", "Tool", "Tool", new DataGridLength(1.3, DataGridLengthUnitType.Star), false, false));
            dg.Columns.Add(MakeColumn(dg, "调用数", "Turns", "Turns", new DataGridLength(74), true, false));
            dg.Columns.Add(MakeColumn(dg, "纯新增输入", "FreshInputText", "FreshInput", new DataGridLength(95), true, false));
            dg.Columns.Add(MakeColumn(dg, "缓存读取", "CacheReadText", "CacheRead", new DataGridLength(95), true, false));
            dg.Columns.Add(MakeColumn(dg, "缓存写入", "CacheWriteText", "CacheWrite", new DataGridLength(90), true, false));
            dg.Columns.Add(MakeColumn(dg, "输出 Token", "OutputText", "Output", new DataGridLength(92), true, false));
            dg.Columns.Add(MakeColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(95), true, true));
            dg.Columns.Add(MakeColumn(dg, "单价 ($/M)", "RateLabel", "CostUsd", new DataGridLength(105), true, false));
            dg.Columns.Add(MakeColumn(dg, "等效花费", "FormattedCost", "CostUsd", new DataGridLength(108), true, true));
            dg.Columns.Add(MakeColumn(dg, "占比", "ShareText", "CostUsd", new DataGridLength(72), true, false));
            return dg;
        }

        private DataGrid CreateMachineDataGrid()
        {
            DataGrid dg = CreateBaseDataGrid();
            dg.Columns.Add(MakeColumn(dg, "设备名称", "MachineName", "MachineName", new DataGridLength(1.25, DataGridLengthUnitType.Star), false, true));
            dg.Columns.Add(MakeColumn(dg, "系统类型", "TypeBadge", "TypeBadge", new DataGridLength(95), false, false));
            dg.Columns.Add(MakeColumn(dg, "最后同步", "SyncedAt", "SyncedAt", new DataGridLength(136), false, false));
            dg.Columns.Add(MakeColumn(dg, "已用工具", "ToolsSummary", "ToolCount", new DataGridLength(1.5, DataGridLengthUnitType.Star), false, false));
            dg.Columns.Add(MakeColumn(dg, "调用数", "Turns", "Turns", new DataGridLength(76), true, false));
            dg.Columns.Add(MakeColumn(dg, "新增输入", "FreshInputText", "FreshInput", new DataGridLength(90), true, false));
            dg.Columns.Add(MakeColumn(dg, "缓存读取", "CacheReadText", "CacheRead", new DataGridLength(92), true, false));
            dg.Columns.Add(MakeColumn(dg, "输出 Token", "OutputText", "Output", new DataGridLength(88), true, false));
            dg.Columns.Add(MakeColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(95), true, true));
            dg.Columns.Add(MakeColumn(dg, "等效美元 ($)", "UsdText", "CostUsd", new DataGridLength(102), true, false));
            dg.Columns.Add(MakeColumn(dg, "等效人民币 (¥)", "CnyText", "CostCny", new DataGridLength(110), true, true));
            dg.Columns.Add(MakeColumn(dg, "全网占比", "ShareText", "CostUsd", new DataGridLength(76), true, false));
            dg.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                MachineRowView row = dg.SelectedItem as MachineRowView;
                if (row != null && !string.IsNullOrEmpty(row.MachineId))
                {
                    _selectedMachineId = string.Equals(_selectedMachineId, row.MachineId, StringComparison.OrdinalIgnoreCase) ? null : row.MachineId;
                    RefreshDashboardView();
                }
            };
            return dg;
        }

        private DataGrid CreateDailyDataGrid()
        {
            DataGrid dg = CreateBaseDataGrid();
            dg.Columns.Add(MakeColumn(dg, "日期", "Date", "Date", new DataGridLength(1.2, DataGridLengthUnitType.Star), false, true));
            dg.Columns.Add(MakeColumn(dg, "调用次数", "Turns", "Turns", new DataGridLength(1.0, DataGridLengthUnitType.Star), true, false));
            dg.Columns.Add(MakeColumn(dg, "纯新增输入", "FreshInputText", "FreshInput", new DataGridLength(1.1, DataGridLengthUnitType.Star), true, false));
            dg.Columns.Add(MakeColumn(dg, "缓存读取", "CacheReadText", "CacheRead", new DataGridLength(1.1, DataGridLengthUnitType.Star), true, false));
            dg.Columns.Add(MakeColumn(dg, "输出 Token", "OutputText", "Output", new DataGridLength(1.1, DataGridLengthUnitType.Star), true, false));
            dg.Columns.Add(MakeColumn(dg, "总 Token", "TotalTokensText", "TotalTokens", new DataGridLength(1.1, DataGridLengthUnitType.Star), true, true));
            dg.Columns.Add(MakeColumn(dg, "等效美元 ($)", "UsdText", "CostUsd", new DataGridLength(1.15, DataGridLengthUnitType.Star), true, false));
            dg.Columns.Add(MakeColumn(dg, "等效人民币 (¥)", "CnyText", "CostCny", new DataGridLength(1.2, DataGridLengthUnitType.Star), true, true));
            return dg;
        }

        private DataGrid CreateBaseDataGrid()
        {
            DataGrid dg = new DataGrid();
            dg.AutoGenerateColumns = false;
            dg.IsReadOnly = true;
            dg.CanUserAddRows = false;
            dg.CanUserDeleteRows = false;
            dg.CanUserReorderColumns = false;
            dg.CanUserResizeRows = false;
            dg.HeadersVisibility = DataGridHeadersVisibility.Column;
            dg.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            dg.SelectionMode = DataGridSelectionMode.Single;
            dg.SelectionUnit = DataGridSelectionUnit.FullRow;
            dg.BorderThickness = new Thickness(0);
            dg.RowHeaderWidth = 0;
            dg.FontSize = 12.2;
            dg.EnableRowVirtualization = true;
            ScrollViewer.SetCanContentScroll(dg, true);

            dg.SetResourceReference(DataGrid.BackgroundProperty, "BgCard");
            dg.SetResourceReference(DataGrid.RowBackgroundProperty, "BgCard");
            dg.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "BgRowAlt");
            dg.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderCard");
            dg.SetResourceReference(DataGrid.ForegroundProperty, "FgPrimary");

            ApplyDataGridThemeStyles(dg);
            return dg;
        }

        private void ApplyDataGridThemeStyles(DataGrid dg)
        {
            if (dg == null) return;

            Style hdrStyle = new Style(typeof(DataGridColumnHeader));
            hdrStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgSecondary")));
            hdrStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            hdrStyle.Setters.Add(new Setter(Control.FontSizeProperty, 11.5));
            hdrStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

            ControlTemplate hdrTpl = new ControlTemplate(typeof(DataGridColumnHeader));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "HdrBd");
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
            bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderCard"));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            bd.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));

            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            FrameworkElementFactory sortTxt = new FrameworkElementFactory(typeof(TextBlock), "SortArrow");
            sortTxt.SetValue(DockPanel.DockProperty, Dock.Right);
            sortTxt.SetValue(TextBlock.MarginProperty, new Thickness(4, 0, 0, 0));
            sortTxt.SetValue(TextBlock.FontSizeProperty, 9.5);
            sortTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            sortTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentBlue"));
            dock.AppendChild(sortTxt);

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            dock.AppendChild(cp);

            bd.AppendChild(dock);
            hdrTpl.VisualTree = bd;

            Trigger hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgElevated"), "HdrBd"));
            hdrTpl.Triggers.Add(hoverTrig);

            Trigger ascTrig = new Trigger { Property = DataGridColumnHeader.SortDirectionProperty, Value = ListSortDirection.Ascending };
            ascTrig.Setters.Add(new Setter(TextBlock.TextProperty, "▲", "SortArrow"));
            hdrTpl.Triggers.Add(ascTrig);

            Trigger descTrig = new Trigger { Property = DataGridColumnHeader.SortDirectionProperty, Value = ListSortDirection.Descending };
            descTrig.Setters.Add(new Setter(TextBlock.TextProperty, "▼", "SortArrow"));
            hdrTpl.Triggers.Add(descTrig);

            hdrStyle.Setters.Add(new Setter(Control.TemplateProperty, hdrTpl));
            dg.ColumnHeaderStyle = hdrStyle;

            Style cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            ControlTemplate cellTpl = new ControlTemplate(typeof(DataGridCell));
            FrameworkElementFactory cbd = new FrameworkElementFactory(typeof(Border), "CellBd");
            cbd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            cbd.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
            FrameworkElementFactory ccp = new FrameworkElementFactory(typeof(ContentPresenter));
            ccp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cbd.AppendChild(ccp);
            cellTpl.VisualTree = cbd;

            Trigger selTrig = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgSelected"), "CellBd"));
            selTrig.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            cellTpl.Triggers.Add(selTrig);

            cellStyle.Setters.Add(new Setter(Control.TemplateProperty, cellTpl));
            dg.CellStyle = cellStyle;
        }

        private DataGridTextColumn MakeColumn(DataGrid dg, string header, string bindingPath, string sortPath, DataGridLength width, bool rightAlign, bool bold)
        {
            DataGridTextColumn col = new DataGridTextColumn();
            col.Header = header;
            col.Binding = new Binding(bindingPath);
            col.SortMemberPath = sortPath;
            col.Width = width;

            if (rightAlign)
            {
                Style hdrRight = new Style(typeof(DataGridColumnHeader), dg.ColumnHeaderStyle);
                hdrRight.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
                col.HeaderStyle = hdrRight;
            }

            Style elemStyle = new Style(typeof(TextBlock));
            elemStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            elemStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (rightAlign)
                elemStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            if (bold)
                elemStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));

            col.ElementStyle = elemStyle;
            return col;
        }

        private FrameworkElement BuildMethodologyPanel()
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(4, 4, 8, 8);

            sp.Children.Add(MakeInfoBlock(
                "1. 免代理实时请求流监控（单条 Token / 耗时 / TPS / 金额）+ SQLite 增量游标引擎",
                "• 无需架设任何本地反向代理或依赖外部软件：直接通过 FileSystemWatcher 监听 + 4 秒静默脉冲 + JSONL 字节偏移游标（Seek Cursor）实时捕获每条对话请求。\n" +
                "• 自动从请求发起与结束时间戳精确测算单条响应耗时（Duration）与输出生成速率 TPS（tok/s），并在「实时请求流」与底部状态栏动态滚动刷新。\n" +
                "• 所有文件游标与快照持久化在 %APPDATA%\\TokLens\\toklens.db，冷启动 0ms 秒开界面，日常增量扫描耗时低于 25ms，永不弹窗锁屏。"));

            sp.Children.Add(MakeInfoBlock(
                "2. 双层历史账单防清理持久归档（本地删日志 / 重装系统均不丢账单）",
                "• 第一层（本地持久归档）：Windows 桌面端依托 %APPDATA%\\TokLens\\toklens.db（Linux VPS 探针依托 ~/.toklens/ledger.db）维护文件级游标快照与按日高水位账本；当 AI 工具自动轮转清理旧会话、手动删除日志文件或清空缓存目录时，被清理文件的历史累计用量自动转入持久归档，已入库账单绝不回退扣减。\n" +
                "• 第二层（云端高水位合并保护）：每次与 GitHub 私有 Gist 或 WebDAV 同步时，自动拉取云端已有快照并按「日期 + 工具 + 模型」执行高水位合并（High-Water-Mark Merge）；即使彻底重装电脑或销毁重建 Linux VPS，已同步过的历史账单依然永久保留、永不归零。"));

            sp.Children.Add(MakeInfoBlock(
                "3. 多台 Windows 电脑 + 多台 Linux VPS 自动同步（单列对比 + 汇总统计）",
                "• Windows 电脑运行同一个绿色单文件 TokLens.exe，配置好 GitHub 私有 Secret Gist 或 WebDAV 后，启动时、每 5 分钟及本机用量变化后自动在后台双向同步。\n" +
                "• Linux VPS 仅需在「多端同步 -> 接入其他电脑 / Linux VPS」复制单行命令粘贴至 SSH 即可部署零依赖 Python 3 探针并注册定时上报，不含任何对话原文。"));

            sp.Children.Add(MakeInfoBlock(
                "4. 100% 独立三源云端模型价格库 + 实时美元汇率",
                "• 彻底移除任何外部软件依赖：独立从 models.dev 官方 API、LiteLLM 开源价格注册表、OpenRouter 公开模型 API 三源聚合同步 2,000+ 模型单价（输入/输出/缓存读写），并支持 ~/.toklens/custom_prices.json 自定义覆盖。\n" +
                "• 自动识别 .claude 日志中的 entrypoint 字段，精准区分 Claude Desktop 桌面客户端与 Claude Code 命令行，消除重复拆分。"));

            sv.Content = sp;
            return sv;
        }

        private Border MakeInfoBlock(string heading, string body)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(8);
            b.Padding = new Thickness(14, 10, 14, 10);
            b.Margin = new Thickness(0, 0, 0, 8);
            b.BorderThickness = new Thickness(1);
            b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel();
            TextBlock h = new TextBlock();
            h.Text = heading;
            h.FontSize = 13;
            h.FontWeight = FontWeights.SemiBold;
            h.Margin = new Thickness(0, 0, 0, 4);
            h.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
            sp.Children.Add(h);

            TextBlock p = new TextBlock();
            p.Text = body;
            p.FontSize = 12;
            p.LineHeight = 19;
            p.TextWrapping = TextWrapping.Wrap;
            p.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(p);

            b.Child = sp;
            return b;
        }

        // -------------------- Reusable Control Builders --------------------
        private Border CreateSegContainer()
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(7);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(2);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            b.Child = sp;
            return b;
        }

        private Button CreateSegButton(string text)
        {
            Button btn = new Button();
            btn.Content = text;
            btn.FontSize = 11.8;
            btn.FontWeight = FontWeights.Medium;
            btn.Padding = new Thickness(9, 4, 9, 4);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "SegBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            return btn;
        }

        private void UpdateSegVisuals(Button[] group, int activeIndex)
        {
            if (group == null) return;
            for (int i = 0; i < group.Length; i++)
            {
                Button b = group[i];
                // Keep FontWeight.Medium constant to prevent horizontal pixel jitter on click!
                b.FontWeight = FontWeights.Medium;
                if (i == activeIndex)
                {
                    b.SetResourceReference(Control.BackgroundProperty, "BgElevated");
                    b.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
                }
            }
        }

        private Button CreateActionButton(string text, bool accentHighlight)
        {
            Button btn = new Button();
            btn.Content = text;
            btn.FontSize = 12;
            btn.FontWeight = FontWeights.Medium;
            btn.Padding = new Thickness(11, 4, 11, 4);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;
            if (accentHighlight)
                btn.Foreground = Brushes.White;
            else
                btn.SetResourceReference(Control.ForegroundProperty, "FgPrimary");

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "BtnBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            if (accentHighlight)
            {
                LinearGradientBrush lg = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0284C7"),
                    (Color)ColorConverter.ConvertFromString("#4F46E5"),
                    45.0);
                lg.Freeze();
                bd.SetValue(Border.BackgroundProperty, lg);
                bd.SetValue(Border.BorderBrushProperty, BrushHex("#38BDF8"));
            }
            else
            {
                bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
                bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));
            }
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (!accentHighlight)
                hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgElevated"), "BtnBd"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBlue"), "BtnBd"));
            tpl.Triggers.Add(hover);

            btn.Template = tpl;
            return btn;
        }
        // -------------------- Cloud Pricing, FX, Silent Incremental Scan & Auto-Sync --------------------
        private void StartCloudPricingSyncAsync(bool forceRefresh)
        {
            if (_priceIsSyncing) return;
            _priceIsSyncing = true;
            UpdatePriceBadgeUi(true);

            Task.Run(delegate()
            {
                PricingSyncInfo info = PricingEngine.SyncCloudPricing(forceRefresh);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _priceIsSyncing = false;
                    if (info != null && info.CloudModelCount > 0)
                    {
                        _priceSyncSource = info.Source;
                        _priceSyncTime = info.UpdatedAt;
                        RepriceLocalRecordsWithUpdatedEngine();
                    }
                    UpdatePriceBadgeUi(false);
                    if (_scanResult != null)
                        RefreshDashboardView();
                }));
            });
        }

        private void RepriceLocalRecordsWithUpdatedEngine()
        {
            if (_scanResult == null) return;
            try
            {
                PricingEngine pe = new PricingEngine(_homeDir);
                _scanResult.PricingModelCount = pe.Count;
                for (int i = 0; i < _scanResult.Records.Count; i++)
                {
                    RecordBucket r = _scanResult.Records[i];
                    if (string.Equals(r.RateLabel, "$1 / $4", StringComparison.Ordinal))
                    {
                        double act, bas;
                        ModelRate mr;
                        pe.ComputeCost(r.Model, r.FreshInput, r.Output, r.CacheRead, r.CacheWrite, 0.0, out act, out bas, out mr);
                        r.CostUsd = act;
                        r.BaselineUsd = bas;
                        r.RateLabel = mr.Label;
                    }
                }
                if (_scanResult.RecentRequests != null)
                {
                    for (int i = 0; i < _scanResult.RecentRequests.Count; i++)
                    {
                        RecentRequestRecord rq = _scanResult.RecentRequests[i];
                        if (rq.CostUsd <= 0.0)
                        {
                            double act, bas;
                            ModelRate mr;
                            pe.ComputeCost(rq.Model, rq.FreshInput, rq.Output, rq.CacheRead, rq.CacheWrite, 0.0, out act, out bas, out mr);
                            rq.CostUsd = act;
                        }
                    }
                }
                _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);
            }
            catch { }
        }

        private void UpdatePriceBadgeUi(bool syncing)
        {
            if (_statusPriceText == null || _statusPriceBorder == null) return;
            if (syncing)
            {
                _statusPriceText.Text = "价格库更新中…";
                _statusPriceText.SetResourceReference(TextBlock.ForegroundProperty, "AccentAmber");
                return;
            }
            int totalModels = _scanResult != null ? _scanResult.PricingModelCount : 0;
            if (totalModels > 0)
            {
                _statusPriceText.Text = string.Format(CultureInfo.InvariantCulture, "价格库 {0:N0} 模型", totalModels);
                _statusPriceText.SetResourceReference(TextBlock.ForegroundProperty, "AccentEmerald");
            }
            else
            {
                _statusPriceText.Text = "价格库就绪";
                _statusPriceText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            }
            _statusPriceBorder.ToolTip = string.Format(
                CultureInfo.InvariantCulture,
                "独立三源云端模型价格库（零外部软件依赖）\n当前覆盖模型数: {0}\n数据源: {1}{2}\n点击立即从 models.dev + LiteLLM + OpenRouter 强制同步最新单价",
                totalModels > 0 ? totalModels.ToString("N0", CultureInfo.InvariantCulture) : "加载中",
                string.IsNullOrEmpty(PricingEngine.CurrentSourceLabel) ? _priceSyncSource : PricingEngine.CurrentSourceLabel,
                string.IsNullOrEmpty(_priceSyncTime) ? "" : ("\n更新时间: " + _priceSyncTime));
        }

        private void StartExchangeRateSyncAsync(bool forceRefresh)
        {
            if (_fxIsSyncing) return;
            _fxIsSyncing = true;
            if (_fxStatusBtnText != null)
            {
                _fxStatusBtnText.Text = "…";
                _fxStatusBtnText.SetResourceReference(TextBlock.ForegroundProperty, "AccentAmber");
            }

            Task.Run(delegate()
            {
                ExchangeRateInfo fx = ExchangeRateService.FetchLatestUsdToCny(forceRefresh);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _fxIsSyncing = false;
                    ApplyFetchedRate(fx);
                }));
            });
        }

        private void ApplyFetchedRate(ExchangeRateInfo fx)
        {
            if (fx == null) return;
            _usdToCny = fx.Rate;
            _fxSource = fx.Source;
            _fxUpdatedAt = fx.UpdatedAt;

            _suppressRateEvent = true;
            try
            {
                if (_rateInput != null)
                    _rateInput.Text = _usdToCny.ToString("0.00##", CultureInfo.InvariantCulture);
            }
            finally
            {
                _suppressRateEvent = false;
            }

            UpdateFxBadgeUi(fx.IsLive, false);
            if (_scanResult != null)
                RefreshDashboardView();
        }

        private void UpdateFxBadgeUi(bool isLive, bool isManual)
        {
            if (_fxStatusBtnText == null || _fxStatusBtnBorder == null) return;
            if (isManual)
            {
                _fxStatusBtnText.Text = "手动";
                _fxStatusBtnText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                _fxStatusBtnBorder.ToolTip = "当前使用手动输入汇率: " + _usdToCny.ToString("0.00##", CultureInfo.InvariantCulture) + "\n点击此处恢复并刷新在线实时汇率";
            }
            else if (isLive)
            {
                _fxStatusBtnText.Text = "实时";
                _fxStatusBtnText.SetResourceReference(TextBlock.ForegroundProperty, "AccentEmerald");
                _fxStatusBtnBorder.ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "USD/CNY 自动实时汇率: {0:0.0000}\n数据来源: {1}{2}\n点击立即重新拉取最新汇率",
                    _usdToCny,
                    _fxSource,
                    string.IsNullOrEmpty(_fxUpdatedAt) ? "" : ("\n更新时间: " + _fxUpdatedAt));
            }
            else
            {
                _fxStatusBtnText.Text = "默认";
                _fxStatusBtnText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                _fxStatusBtnBorder.ToolTip = "当前使用缓存/默认汇率 " + _usdToCny.ToString("0.00", CultureInfo.InvariantCulture) + "\n点击此处拉取在线实时汇率";
            }
        }

        // Silent Non-Blocking Incremental Scan (NEVER grays out or locks the screen!)
        private void RunSilentScanAsync(string targetHome, bool triggerCloudSyncIfEnabled, bool forceUiRefresh)
        {
            if (_isScanning)
            {
                _pendingRescan = true;
                return;
            }

            _isScanning = true;
            if (_statusPulseDot != null)
                _statusPulseDot.SetResourceReference(Shape.FillProperty, "AccentAmber");

            bool scanWsl = _syncCfg != null && _syncCfg.ScanLocalWsl;
            bool hadNoPriorResult = (_scanResult == null);

            Task.Run(delegate()
            {
                TokenScanner scanner = new TokenScanner(targetHome, scanWsl);
                ScanResult res = scanner.Run();

                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _isScanning = false;
                    if (_statusPulseDot != null)
                        _statusPulseDot.SetResourceReference(Shape.FillProperty, "AccentEmerald");

                    long newTopTs = (res.RecentRequests != null && res.RecentRequests.Count > 0)
                        ? res.RecentRequests[0].TimestampMs
                        : 0L;

                    int prevArchivedCount = _scanResult != null ? _scanResult.ArchivedFilesCount : -1;
                    bool hasChanges = hadNoPriorResult ||
                                      forceUiRefresh ||
                                      res.ChangedFilesCount > 0 ||
                                      res.ArchivedFilesCount != prevArchivedCount ||
                                      newTopTs != _lastTopRequestTs;

                    _scanResult = res;
                    _lastTopRequestTs = newTopTs;

                    if (hasChanges)
                    {
                        _localNode = SyncService.BuildLocalSnapshot(res, _syncCfg);
                        _remoteNodes = SyncService.LoadCachedRemoteNodes(_syncCfg.MachineId);

                        if (res.Warnings.Count > 0)
                        {
                            _warningBanner.Visibility = Visibility.Visible;
                            _warningText.Text = "部分目录受权限限制无法读取（" + string.Join(", ", res.Warnings.ToArray()) + "）。直接运行 TokLens.exe 可读取全部数据。";
                        }
                        else
                        {
                            _warningBanner.Visibility = Visibility.Collapsed;
                        }

                        UpdatePriceBadgeUi(_priceIsSyncing);
                        RefreshDashboardView();

                        // Schedule debounced cloud push when local files changed in background
                        if (res.ChangedFilesCount > 0 && !triggerCloudSyncIfEnabled && _cloudDebounceTimer != null && _syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                        {
                            _cloudDebounceTimer.Stop();
                            _cloudDebounceTimer.Start();
                        }
                    }

                    if (triggerCloudSyncIfEnabled && _syncCfg != null && _syncCfg.AutoSync && _syncCfg.IsConfigured)
                    {
                        StartBackgroundCloudSync();
                    }

                    if (_pendingRescan)
                    {
                        _pendingRescan = false;
                        RunSilentScanAsync(_homeDir, false, false);
                    }
                }));
            });
        }

        private void StartBackgroundCloudSync()
        {
            if (_cloudSyncRunning || _localNode == null) return;
            _cloudSyncRunning = true;
            _lastSyncStatus = "同步中…";
            UpdateCloudStatusUi();

            NodeSnapshot snapCopy = _localNode;
            SyncConfig cfgCopy = _syncCfg;

            Task.Run(delegate()
            {
                SyncOutcome outcome = SyncService.SyncNow(cfgCopy, snapCopy, false);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _cloudSyncRunning = false;
                    if (outcome != null)
                    {
                        _remoteNodes = outcome.RemoteNodes ?? SyncService.LoadCachedRemoteNodes(_syncCfg.MachineId);
                        _lastSyncStatus = outcome.Success
                            ? ("已同步 " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture))
                            : "同步异常";
                    }
                    UpdateCloudStatusUi();
                    RefreshDashboardView();
                }));
            });
        }

        private void UpdateCloudStatusUi()
        {
            if (_statusCloudText == null || _statusCloudBorder == null) return;
            if (_cloudSyncRunning)
            {
                _statusCloudText.Text = "云端同步中…";
                _statusCloudText.SetResourceReference(TextBlock.ForegroundProperty, "AccentAmber");
            }
            else if (_syncCfg != null && _syncCfg.IsConfigured)
            {
                string ch = string.Equals(_syncCfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase) ? "WebDAV" : "Gist";
                string tail = string.IsNullOrEmpty(_lastSyncStatus) ? "自动同步已开启" : _lastSyncStatus;
                _statusCloudText.Text = ch + " · " + tail;
                _statusCloudText.SetResourceReference(TextBlock.ForegroundProperty, "AccentEmerald");
                _statusCloudBorder.ToolTip = "多端自动同步通道已激活（每 5 分钟及本机变动后自动同步）\n点击打开多端同步设置";
            }
            else
            {
                _statusCloudText.Text = "多端同步: 未配置";
                _statusCloudText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                _statusCloudBorder.ToolTip = "点击配置 GitHub 私有 Gist 或 WebDAV，自动汇总多台电脑与 Linux VPS 账单";
            }
        }

        private List<NodeSnapshot> GetAllNodes()
        {
            List<NodeSnapshot> all = new List<NodeSnapshot>();
            if (_localNode != null)
                all.Add(_localNode);
            if (_remoteNodes != null)
            {
                for (int i = 0; i < _remoteNodes.Count; i++)
                {
                    NodeSnapshot rn = _remoteNodes[i];
                    if (rn != null && (_localNode == null || !string.Equals(rn.MachineId, _localNode.MachineId, StringComparison.OrdinalIgnoreCase)))
                        all.Add(rn);
                }
            }
            return all;
        }

        private List<RecordBucket> GetAllMergedRecords()
        {
            List<NodeSnapshot> nodes = GetAllNodes();
            List<RecordBucket> merged = new List<RecordBucket>();
            for (int n = 0; n < nodes.Count; n++)
            {
                NodeSnapshot snap = nodes[n];
                if (snap.Records == null) continue;
                for (int i = 0; i < snap.Records.Count; i++)
                {
                    RecordBucket r = snap.Records[i];
                    if (string.IsNullOrEmpty(r.MachineId)) r.MachineId = snap.MachineId;
                    if (string.IsNullOrEmpty(r.MachineName)) r.MachineName = snap.MachineName;
                    if (string.IsNullOrEmpty(r.MachineType)) r.MachineType = snap.MachineType;
                    merged.Add(r);
                }
            }
            return merged;
        }

        private string ComputeCutoffDate(List<RecordBucket> allRecords)
        {
            if (_rangeDays <= 0) return null;
            DateTime baseDt = DateTime.Now.Date;
            for (int i = 0; i < allRecords.Count; i++)
            {
                string d = allRecords[i].Date;
                if (!string.IsNullOrEmpty(d) && d != "未知日期")
                {
                    DateTime parsed;
                    if (DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    {
                        if (parsed > baseDt) baseDt = parsed;
                    }
                }
            }
            return baseDt.AddDays(-(_rangeDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string GetMachineTypeBadge(string machineType, bool isLocal)
        {
            string baseType = "Windows";
            if (string.Equals(machineType, "vps", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(machineType, "linux", StringComparison.OrdinalIgnoreCase))
                baseType = "Linux VPS";
            else if (string.Equals(machineType, "macos", StringComparison.OrdinalIgnoreCase))
                baseType = "macOS";
            return isLocal ? (baseType + " · 本机") : baseType;
        }

        // -------------------- Main Dashboard Refresh --------------------
        private void RefreshDashboardView()
        {
            if (_scanResult == null) return;

            List<NodeSnapshot> allNodes = GetAllNodes();
            List<RecordBucket> allRecords = GetAllMergedRecords();
            string cutoff = ComputeCutoffDate(allRecords);

            // 1. Time Window Filter
            List<RecordBucket> timeFiltered = new List<RecordBucket>();
            for (int i = 0; i < allRecords.Count; i++)
            {
                RecordBucket r = allRecords[i];
                if (cutoff == null || (r.Date != "未知日期" && string.CompareOrdinal(r.Date, cutoff) >= 0))
                {
                    if (r.TotalTokens > 0)
                        timeFiltered.Add(r);
                }
            }

            // 2. Per-Machine Summary Map
            Dictionary<string, MachineRowView> machineMap = new Dictionary<string, MachineRowView>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> machineTools = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (int n = 0; n < allNodes.Count; n++)
            {
                NodeSnapshot snap = allNodes[n];
                MachineRowView mv = new MachineRowView();
                mv.MachineId = snap.MachineId;
                mv.MachineName = string.IsNullOrEmpty(snap.MachineName) ? snap.MachineId : snap.MachineName;
                mv.TypeBadge = GetMachineTypeBadge(snap.MachineType, snap.IsLocal);
                mv.SyncedAt = snap.IsLocal ? "实时监听中" : (string.IsNullOrEmpty(snap.SyncedAt) ? "本地缓存" : snap.SyncedAt);
                mv.IsLocal = snap.IsLocal;
                machineMap[snap.MachineId] = mv;
                machineTools[snap.MachineId] = new HashSet<string>(StringComparer.Ordinal);
            }

            double grandCostInRange = 0.0;
            for (int i = 0; i < timeFiltered.Count; i++)
            {
                RecordBucket r = timeFiltered[i];
                MachineRowView mv;
                if (!machineMap.TryGetValue(r.MachineId, out mv))
                {
                    mv = new MachineRowView();
                    mv.MachineId = r.MachineId;
                    mv.MachineName = string.IsNullOrEmpty(r.MachineName) ? r.MachineId : r.MachineName;
                    mv.TypeBadge = GetMachineTypeBadge(r.MachineType, false);
                    mv.SyncedAt = "本地缓存";
                    machineMap[r.MachineId] = mv;
                    machineTools[r.MachineId] = new HashSet<string>(StringComparer.Ordinal);
                }
                mv.Turns += r.Turns;
                mv.FreshInput += r.FreshInput;
                mv.CacheRead += r.CacheRead;
                mv.Output += r.Output;
                mv.TotalTokens += r.TotalTokens;
                mv.CostUsd += r.CostUsd;
                machineTools[r.MachineId].Add(r.Tool);
                grandCostInRange += r.CostUsd;
            }

            if (_selectedMachineId != null && !machineMap.ContainsKey(_selectedMachineId))
                _selectedMachineId = null;

            List<MachineRowView> machineList = new List<MachineRowView>(machineMap.Values);
            machineList.Sort(delegate(MachineRowView a, MachineRowView b)
            {
                int c = _rankByCost ? b.CostUsd.CompareTo(a.CostUsd) : b.TotalTokens.CompareTo(a.TotalTokens);
                if (c != 0) return c;
                if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
                return string.Compare(a.MachineName, b.MachineName, StringComparison.OrdinalIgnoreCase);
            });

            double maxMachMetric = 0.0;
            double sumMachMetric = 0.0;
            for (int i = 0; i < machineList.Count; i++)
            {
                MachineRowView m = machineList[i];
                List<string> tNames = new List<string>(machineTools[m.MachineId]);
                tNames.Sort(StringComparer.OrdinalIgnoreCase);
                m.ToolCount = tNames.Count;
                m.ToolsSummary = tNames.Count > 0 ? string.Join(", ", tNames.ToArray()) : "该时段无记录";
                m.FreshInputText = FormatterUtil.FormatTokens(m.FreshInput);
                m.CacheReadText = FormatterUtil.FormatTokens(m.CacheRead);
                m.OutputText = FormatterUtil.FormatTokens(m.Output);
                m.TotalTokensText = FormatterUtil.FormatTokens(m.TotalTokens);
                m.CostCny = m.CostUsd * _usdToCny;
                m.UsdText = FormatterUtil.FormatMoney(m.CostUsd, false, _usdToCny);
                m.CnyText = FormatterUtil.FormatMoney(m.CostUsd, true, _usdToCny);
                m.FormattedMoney = FormatterUtil.FormatMoney(m.CostUsd, _isCny, _usdToCny);
                m.ColorHex = ToolPalette[i % ToolPalette.Length];

                double metric = _rankByCost ? m.CostUsd : (double)m.TotalTokens;
                sumMachMetric += metric;
                if (metric > maxMachMetric) maxMachMetric = metric;
            }
            for (int i = 0; i < machineList.Count; i++)
            {
                MachineRowView m = machineList[i];
                double metric = _rankByCost ? m.CostUsd : (double)m.TotalTokens;
                m.SharePercent = sumMachMetric > 0 ? (metric * 100.0 / sumMachMetric) : 0.0;
                m.ShareText = m.SharePercent.ToString("F1", CultureInfo.InvariantCulture) + "%";
                m.BarRatio = maxMachMetric > 0 ? Math.Max(0.02, metric / maxMachMetric) : 0.0;
            }

            _machineGrid.ItemsSource = machineList;
            RenderDeviceStrip(allNodes, machineMap, grandCostInRange);
            UpdateCloudStatusUi();

            // 3. Machine Filter
            List<RecordBucket> machineFiltered = new List<RecordBucket>();
            for (int i = 0; i < timeFiltered.Count; i++)
            {
                RecordBucket r = timeFiltered[i];
                if (_selectedMachineId == null || string.Equals(r.MachineId, _selectedMachineId, StringComparison.OrdinalIgnoreCase))
                    machineFiltered.Add(r);
            }

            // 4. Active Tools Ranking
            Dictionary<string, ToolRowView> toolMap = new Dictionary<string, ToolRowView>(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> toolModels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < machineFiltered.Count; i++)
            {
                RecordBucket r = machineFiltered[i];
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
                toolModels[r.Tool].Add(r.Model);
            }

            if (_selectedTool != null && !toolMap.ContainsKey(_selectedTool))
                _selectedTool = null;

            List<ToolRowView> toolList = new List<ToolRowView>(toolMap.Values);
            toolList.Sort(delegate(ToolRowView a, ToolRowView b)
            {
                return _rankByCost ? b.CostUsd.CompareTo(a.CostUsd) : b.TotalTokens.CompareTo(a.TotalTokens);
            });

            double sumMetric = 0.0;
            double maxMetric = 0.0;
            for (int i = 0; i < toolList.Count; i++)
            {
                ToolRowView t = toolList[i];
                t.ModelCount = toolModels[t.Tool].Count;
                t.FormattedMoney = FormatterUtil.FormatMoney(t.CostUsd, _isCny, _usdToCny);
                t.FormattedTokens = FormatterUtil.FormatTokens(t.TotalTokens);
                t.ColorHex = ToolPalette[i % ToolPalette.Length];
                double m = _rankByCost ? t.CostUsd : (double)t.TotalTokens;
                sumMetric += m;
                if (m > maxMetric) maxMetric = m;
            }
            for (int i = 0; i < toolList.Count; i++)
            {
                ToolRowView t = toolList[i];
                double m = _rankByCost ? t.CostUsd : (double)t.TotalTokens;
                t.SharePercent = sumMetric > 0 ? (m * 100.0 / sumMetric) : 0.0;
                t.BarRatio = maxMetric > 0 ? Math.Max(0.02, m / maxMetric) : 0.0;
            }

            // 5. Tool Filter for KPIs, Models, Daily & Live Per-Request Stream
            List<RecordBucket> activeRows = new List<RecordBucket>();
            for (int i = 0; i < machineFiltered.Count; i++)
            {
                if (_selectedTool == null || string.Equals(machineFiltered[i].Tool, _selectedTool, StringComparison.Ordinal))
                    activeRows.Add(machineFiltered[i]);
            }

            int totTurns = 0;
            long totFi = 0, totCr = 0, totCw = 0, totOut = 0;
            double totCost = 0.0, totBase = 0.0;
            HashSet<string> activeToolSet = new HashSet<string>(StringComparer.Ordinal);

            Dictionary<string, ModelRowView> modelMap = new Dictionary<string, ModelRowView>(StringComparer.Ordinal);
            Dictionary<string, DailyRowView> dailyMap = new Dictionary<string, DailyRowView>(StringComparer.Ordinal);

            for (int i = 0; i < activeRows.Count; i++)
            {
                RecordBucket r = activeRows[i];
                activeToolSet.Add(r.Tool);
                totTurns += r.Turns;
                totFi += r.FreshInput;
                totCr += r.CacheRead;
                totCw += r.CacheWrite;
                totOut += r.Output;
                totCost += r.CostUsd;
                totBase += r.BaselineUsd;

                string mKey = r.Tool + "||" + r.Model;
                ModelRowView mv;
                if (!modelMap.TryGetValue(mKey, out mv))
                {
                    mv = new ModelRowView();
                    mv.Model = r.Model;
                    mv.Tool = r.Tool;
                    mv.RateLabel = r.RateLabel;
                    modelMap[mKey] = mv;
                }
                mv.Turns += r.Turns;
                mv.FreshInput += r.FreshInput;
                mv.CacheRead += r.CacheRead;
                mv.CacheWrite += r.CacheWrite;
                mv.Output += r.Output;
                mv.TotalTokens += r.TotalTokens;
                mv.CostUsd += r.CostUsd;

                DailyRowView dv;
                if (!dailyMap.TryGetValue(r.Date, out dv))
                {
                    dv = new DailyRowView();
                    dv.Date = r.Date;
                    dailyMap[r.Date] = dv;
                }
                dv.Turns += r.Turns;
                dv.FreshInput += r.FreshInput;
                dv.CacheRead += r.CacheRead;
                dv.Output += r.Output;
                dv.TotalTokens += r.TotalTokens;
                dv.CostUsd += r.CostUsd;
            }

            long totTokens = totFi + totCr + totCw + totOut;
            long promptTotal = totFi + totCr + totCw;
            double hitPct = promptTotal > 0 ? (totCr * 100.0 / promptTotal) : 0.0;
            double savedUsd = Math.Max(0.0, totBase - totCost);
            double savedPct = totBase > 0 ? (savedUsd * 100.0 / totBase) : 0.0;

            string scopeLabel = "全部 " + allNodes.Count + " 台设备";
            if (_selectedMachineId != null && machineMap.ContainsKey(_selectedMachineId))
                scopeLabel = "单列: " + machineMap[_selectedMachineId].MachineName;
            string rangeDesc = _rangeDays == 0 ? "全部历史" : (_rangeDays == 1 ? "今日" : ("近 " + _rangeDays + " 天"));

            // Update Bottom Status Bar (Fixed 28px height -> 0.0px layout shift!)
            double msElapsed = _scanResult.ElapsedSeconds * 1000.0;
            string perfText = msElapsed <= 0.1
                ? "秒开快照"
                : (msElapsed < 1000.0
                    ? (msElapsed.ToString("F0", CultureInfo.InvariantCulture) + "ms")
                    : (_scanResult.ElapsedSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s"));
            if (_statusLeftText != null)
            {
                string archiveSuffix = _scanResult.ArchivedFilesCount > 0
                    ? string.Format(CultureInfo.InvariantCulture, " · 已保护 {0} 个清理归档", _scanResult.ArchivedFilesCount)
                    : "";
                _statusLeftText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "实时监听中 · {0} ({1}) · 活跃工具 {2} 个 · 扫描 {3}{4}",
                    scopeLabel,
                    rangeDesc,
                    toolList.Count,
                    perfText,
                    archiveSuffix);

                string archiveDetail = _scanResult.ArchivedFilesCount > 0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "已自动保留 {0} 个被清理历史日志文件的累计用量（当前活跃跟踪文件 {1} 个）",
                        _scanResult.ArchivedFilesCount,
                        _scanResult.TotalFilesTracked)
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "当前活跃跟踪文件 {0} 个 · 后续清理任何本地或 VPS 日志文件，已入库账单均会自动归档保留",
                        _scanResult.TotalFilesTracked);

                _statusLeftText.ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "双层防清理持久归档保护已启用\n• 本地保护：依托 %APPDATA%\\TokLens\\toklens.db 游标与按日高水位库，清理本地/VPS 日志文件后已入库账单永久保留\n• 云端保护：Gist / WebDAV 同步采用按日高水位合并，即使重装电脑亦不丢历史账单\n• 归档详情：{0}",
                    archiveDetail);
            }

            // Update KPI Cards
            _kpiCostVal.Text = FormatterUtil.FormatMoney(totCost, _isCny, _usdToCny);
            string altMoney = FormatterUtil.FormatMoney(totCost, !_isCny, _usdToCny);
            _kpiCostSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "折合 {0} · {1:N0} 次请求 · {2}{3}",
                altMoney,
                totTurns,
                scopeLabel,
                _selectedTool != null ? (" [" + _selectedTool + "]") : "");

            _kpiTokVal.Text = FormatterUtil.FormatTokens(totTokens);
            _kpiTokSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "新增输入 {0} · 输出 {1} · {2} 个活跃工具",
                FormatterUtil.FormatTokens(totFi),
                FormatterUtil.FormatTokens(totOut),
                activeToolSet.Count);

            _kpiHitVal.Text = hitPct.ToString("F1", CultureInfo.InvariantCulture) + "%";
            _kpiHitSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "缓存读取 {0} · 缓存写入 {1}",
                FormatterUtil.FormatTokens(totCr),
                FormatterUtil.FormatTokens(totCw));

            _kpiSaveVal.Text = FormatterUtil.FormatMoney(savedUsd, _isCny, _usdToCny);
            _kpiSaveSub.Text = string.Format(
                CultureInfo.InvariantCulture,
                "无缓存原价 {0} · 省下 {1:F1}%",
                FormatterUtil.FormatMoney(totBase, _isCny, _usdToCny),
                savedPct);

            // Render Left Ranking List
            if (_leftRankByMachine)
            {
                _clearFilterBtn.Visibility = (_selectedMachineId != null) ? Visibility.Visible : Visibility.Collapsed;
                if (_selectedMachineId != null && machineMap.ContainsKey(_selectedMachineId))
                    _clearFilterBtn.Content = machineMap[_selectedMachineId].MachineName + " ×";
                RenderMachinesRankingList(machineList);
            }
            else
            {
                _clearFilterBtn.Visibility = (_selectedTool != null) ? Visibility.Visible : Visibility.Collapsed;
                if (_selectedTool != null)
                    _clearFilterBtn.Content = _selectedTool + " ×";
                RenderToolsList(toolList);
            }

            // 6. Build Live Per-Request Stream Rows (Token / Duration / TPS / Cost per turn!)
            List<LiveRequestRowView> liveRows = new List<LiveRequestRowView>();
            bool includeLocalLive = (_selectedMachineId == null ||
                                    (_localNode != null && string.Equals(_selectedMachineId, _localNode.MachineId, StringComparison.OrdinalIgnoreCase)));
            if (includeLocalLive && _scanResult.RecentRequests != null)
            {
                for (int i = 0; i < _scanResult.RecentRequests.Count; i++)
                {
                    RecentRequestRecord rq = _scanResult.RecentRequests[i];
                    if (cutoff != null && rq.Date != "未知日期" && string.CompareOrdinal(rq.Date, cutoff) < 0)
                        continue;
                    if (_selectedTool != null && !string.Equals(rq.Tool, _selectedTool, StringComparison.Ordinal))
                        continue;

                    LiveRequestRowView lv = new LiveRequestRowView();
                    lv.TimestampMs = rq.TimestampMs;
                    lv.Date = rq.Date;
                    lv.TimeText = string.IsNullOrEmpty(rq.TimeLabel) ? rq.Date : rq.TimeLabel;
                    lv.Tool = rq.Tool;
                    lv.Model = rq.Model;
                    lv.FreshInput = rq.FreshInput;
                    lv.FreshInputText = FormatterUtil.FormatTokens(rq.FreshInput);
                    lv.CacheRead = rq.CacheRead;
                    lv.CacheReadText = FormatterUtil.FormatTokens(rq.CacheRead);
                    lv.CacheWrite = rq.CacheWrite;
                    lv.CacheWriteText = FormatterUtil.FormatTokens(rq.CacheWrite);
                    lv.Output = rq.Output;
                    lv.OutputText = FormatterUtil.FormatTokens(rq.Output);
                    lv.TotalTokens = rq.TotalTokens;
                    lv.TotalTokensText = FormatterUtil.FormatTokens(rq.TotalTokens);
                    lv.DurationMs = rq.DurationMs;
                    lv.DurationText = FormatterUtil.FormatDuration(rq.DurationMs);
                    lv.Tps = FormatterUtil.ComputeTps(rq.Output, rq.DurationMs);
                    lv.TpsText = FormatterUtil.FormatTps(lv.Tps);
                    lv.CostUsd = rq.CostUsd;
                    lv.DisplayCost = _isCny ? (rq.CostUsd * _usdToCny) : rq.CostUsd;
                    lv.FormattedCost = FormatterUtil.FormatMoney(rq.CostUsd, _isCny, _usdToCny);
                    liveRows.Add(lv);
                }
            }
            _liveGrid.ItemsSource = liveRows;

            // Update Bottom Live Request Ticker
            if (_statusLiveTickerText != null)
            {
                if (liveRows.Count > 0)
                {
                    LiveRequestRowView top = liveRows[0];
                    string tpsPart = top.Tps > 0 ? (" · " + top.TpsText) : (top.DurationMs >= 150 ? (" · " + top.DurationText) : "");
                    _statusLiveTickerText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "最新 {0} · {1} ({2}) · 输出 {3}{4} · {5}",
                        top.TimeText,
                        top.Tool,
                        top.Model,
                        top.OutputText,
                        tpsPart,
                        top.FormattedCost);
                }
                else if (!includeLocalLive)
                {
                    _statusLiveTickerText.Text = "远端节点仅同步脱敏按日汇总（切回本机或全部可看实时单条请求流）";
                }
                else
                {
                    _statusLiveTickerText.Text = "当前筛选范围内暂无实时单条请求记录";
                }
            }

            // 7. Model Billing Rows
            List<ModelRowView> modelRows = new List<ModelRowView>(modelMap.Values);
            modelRows.Sort(delegate(ModelRowView a, ModelRowView b) { return b.CostUsd.CompareTo(a.CostUsd); });
            for (int i = 0; i < modelRows.Count; i++)
            {
                ModelRowView m = modelRows[i];
                m.FreshInputText = FormatterUtil.FormatTokens(m.FreshInput);
                m.CacheReadText = FormatterUtil.FormatTokens(m.CacheRead);
                m.CacheWriteText = FormatterUtil.FormatTokens(m.CacheWrite);
                m.OutputText = FormatterUtil.FormatTokens(m.Output);
                m.TotalTokensText = FormatterUtil.FormatTokens(m.TotalTokens);
                m.DisplayCost = _isCny ? (m.CostUsd * _usdToCny) : m.CostUsd;
                m.FormattedCost = FormatterUtil.FormatMoney(m.CostUsd, _isCny, _usdToCny);
                m.ShareText = totCost > 0 ? ((m.CostUsd * 100.0 / totCost).ToString("F1", CultureInfo.InvariantCulture) + "%") : "0.0%";
            }
            _modelGrid.ItemsSource = modelRows;

            // 8. Daily Billing Rows
            List<DailyRowView> dailyAsc = new List<DailyRowView>(dailyMap.Values);
            dailyAsc.Sort(delegate(DailyRowView a, DailyRowView b) { return string.CompareOrdinal(a.Date, b.Date); });
            for (int i = 0; i < dailyAsc.Count; i++)
            {
                DailyRowView d = dailyAsc[i];
                d.FreshInputText = FormatterUtil.FormatTokens(d.FreshInput);
                d.CacheReadText = FormatterUtil.FormatTokens(d.CacheRead);
                d.OutputText = FormatterUtil.FormatTokens(d.Output);
                d.TotalTokensText = FormatterUtil.FormatTokens(d.TotalTokens);
                d.CostCny = d.CostUsd * _usdToCny;
                d.UsdText = FormatterUtil.FormatMoney(d.CostUsd, false, _usdToCny);
                d.CnyText = FormatterUtil.FormatMoney(d.CostUsd, true, _usdToCny);
            }
            _currentDailyAsc = dailyAsc;

            List<DailyRowView> dailyDesc = new List<DailyRowView>(dailyAsc);
            dailyDesc.Reverse();
            _dailyGrid.ItemsSource = dailyDesc;

            // Update Clean Tab Titles (Zero noisy emojis!)
            _tabBtns[0].Content = string.Format(CultureInfo.InvariantCulture, "实时请求流 ({0})", liveRows.Count);
            _tabBtns[1].Content = string.Format(CultureInfo.InvariantCulture, "模型账单明细 ({0})", modelRows.Count);
            _tabBtns[2].Content = string.Format(CultureInfo.InvariantCulture, "设备单列对比 ({0})", machineList.Count);
            _tabBtns[3].Content = string.Format(CultureInfo.InvariantCulture, "每日账单流水 ({0})", dailyDesc.Count);

            _trendTitleText.Text = _rangeDays > 0
                ? string.Format(CultureInfo.InvariantCulture, "每日消耗趋势 ({0})", rangeDesc)
                : "每日消耗趋势";

            _trendHoverText.Text = dailyAsc.Count > 0
                ? string.Format(CultureInfo.InvariantCulture, "共 {0} 个活跃日 · 悬停柱状图查看详情", dailyAsc.Count)
                : "当前筛选范围内暂无用量记录";
            RenderTrendChart();
        }

        private void OnClearFilterClick(object sender, RoutedEventArgs e)
        {
            if (_leftRankByMachine)
                _selectedMachineId = null;
            else
                _selectedTool = null;
            RefreshDashboardView();
        }

        // -------------------- Zero-Jump Device Strip Rendering --------------------
        private void RenderDeviceStrip(List<NodeSnapshot> allNodes, Dictionary<string, MachineRowView> machineMap, double grandCostUsd)
        {
            if (_devicePillsStack == null) return;
            _devicePillsStack.Children.Clear();

            bool allSel = (_selectedMachineId == null);
            string allLabel = string.Format(
                CultureInfo.InvariantCulture,
                "全部设备 ({0}台 · {1})",
                allNodes.Count,
                FormatterUtil.FormatMoney(grandCostUsd, _isCny, _usdToCny));
            Border allPill = CreateDevicePill(allLabel, allSel, "#38BDF8", "查看所有 Windows 电脑与 Linux VPS 的合并总消耗");
            allPill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                _selectedMachineId = null;
                RefreshDashboardView();
            };
            _devicePillsStack.Children.Add(allPill);

            List<NodeSnapshot> ordered = new List<NodeSnapshot>(allNodes);
            ordered.Sort(delegate(NodeSnapshot a, NodeSnapshot b)
            {
                if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
                double ca = machineMap.ContainsKey(a.MachineId) ? machineMap[a.MachineId].CostUsd : 0;
                double cb = machineMap.ContainsKey(b.MachineId) ? machineMap[b.MachineId].CostUsd : 0;
                return cb.CompareTo(ca);
            });

            for (int i = 0; i < ordered.Count; i++)
            {
                NodeSnapshot node = ordered[i];
                MachineRowView mv = machineMap.ContainsKey(node.MachineId) ? machineMap[node.MachineId] : null;
                double mCost = mv != null ? mv.CostUsd : 0.0;
                bool isSel = (_selectedMachineId != null && string.Equals(_selectedMachineId, node.MachineId, StringComparison.OrdinalIgnoreCase));

                string rolePrefix = "";
                if (string.Equals(node.MachineType, "vps", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.MachineType, "linux", StringComparison.OrdinalIgnoreCase))
                    rolePrefix = "VPS · ";

                string localSuffix = node.IsLocal ? " [本机]" : "";
                string txt = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}{1}{2} · {3}",
                    rolePrefix,
                    string.IsNullOrEmpty(node.MachineName) ? node.MachineId : node.MachineName,
                    localSuffix,
                    FormatterUtil.FormatMoney(mCost, _isCny, _usdToCny));

                string tip = string.Format(
                    CultureInfo.InvariantCulture,
                    "设备: {0} ({1})\n状态: {2}\n当前时间段花费: {3} ({4} Tokens)\n左键点击单列查看该设备{5}",
                    node.MachineName,
                    node.MachineId,
                    node.IsLocal ? "本机实时监听" : (string.IsNullOrEmpty(node.SyncedAt) ? "本地缓存" : node.SyncedAt),
                    FormatterUtil.FormatMoney(mCost, _isCny, _usdToCny),
                    mv != null ? mv.TotalTokensText : "0",
                    node.IsLocal ? " · 可在「多端同步」中修改显示名" : " · 右键可移除本地缓存");

                string dotHex = node.IsLocal ? "#10B981" : (rolePrefix.Length > 0 ? "#818CF8" : "#38BDF8");
                Border pill = CreateDevicePill(txt, isSel, dotHex, tip);
                string capturedId = node.MachineId;
                pill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    _selectedMachineId = string.Equals(_selectedMachineId, capturedId, StringComparison.OrdinalIgnoreCase) ? null : capturedId;
                    RefreshDashboardView();
                };

                if (!node.IsLocal)
                {
                    string capturedName = node.MachineName;
                    ContextMenu cm = new ContextMenu();
                    MenuItem rmItem = new MenuItem { Header = "移除「" + capturedName + "」的本地缓存" };
                    rmItem.Click += delegate(object s, RoutedEventArgs e)
                    {
                        SyncService.RemoveCachedNode(capturedId);
                        _remoteNodes = SyncService.LoadCachedRemoteNodes(_syncCfg.MachineId);
                        if (string.Equals(_selectedMachineId, capturedId, StringComparison.OrdinalIgnoreCase))
                            _selectedMachineId = null;
                        RefreshDashboardView();
                    };
                    cm.Items.Add(rmItem);
                    pill.ContextMenu = cm;
                }

                _devicePillsStack.Children.Add(pill);
            }
        }

        private Border CreateDevicePill(string text, bool selected, string dotHex, string tooltip)
        {
            Border pill = new Border();
            pill.Height = 28;
            pill.CornerRadius = new CornerRadius(6);
            pill.Padding = new Thickness(10, 0, 10, 0);
            pill.Margin = new Thickness(0, 0, 7, 0);
            pill.BorderThickness = new Thickness(1);
            pill.VerticalAlignment = VerticalAlignment.Center;
            pill.Cursor = Cursors.Hand;
            pill.ToolTip = tooltip;

            if (selected)
            {
                pill.SetResourceReference(Border.BackgroundProperty, "BgSelected");
                pill.SetResourceReference(Border.BorderBrushProperty, "AccentBlue");
            }
            else
            {
                pill.SetResourceReference(Border.BackgroundProperty, "BgCard");
                pill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            }

            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Ellipse dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = BrushHex(dotHex),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = selected ? 1.0 : 0.65
            };
            sp.Children.Add(dot);

            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontSize = 11.8;
            // Constant FontWeight.Medium prevents width shift when clicking device pills!
            tb.FontWeight = FontWeights.Medium;
            tb.VerticalAlignment = VerticalAlignment.Center;
            tb.SetResourceReference(TextBlock.ForegroundProperty, selected ? "AccentBlue" : "FgPrimary");
            sp.Children.Add(tb);

            pill.Child = sp;

            pill.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (!selected)
                    pill.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            };
            pill.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                if (!selected)
                    pill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            };
            return pill;
        }

        // -------------------- Middle Cards & Trend Chart Rendering --------------------
        private void RenderMachinesRankingList(List<MachineRowView> machines)
        {
            _toolsStack.Children.Clear();
            if (machines.Count == 0)
            {
                TextBlock empty = new TextBlock
                {
                    Text = "暂无设备统计记录",
                    Margin = new Thickness(8, 24, 8, 8),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                _toolsStack.Children.Add(empty);
                return;
            }

            for (int i = 0; i < machines.Count; i++)
            {
                MachineRowView m = machines[i];
                bool isSel = (_selectedMachineId != null && string.Equals(_selectedMachineId, m.MachineId, StringComparison.OrdinalIgnoreCase));
                SolidColorBrush dotBrush = BrushHex(m.ColorHex);

                Border rowBd = new Border();
                rowBd.CornerRadius = new CornerRadius(7);
                rowBd.Padding = new Thickness(10, 7, 10, 7);
                rowBd.Margin = new Thickness(0, 0, 0, 5);
                rowBd.BorderThickness = new Thickness(1);
                rowBd.Cursor = Cursors.Hand;
                rowBd.SetResourceReference(Border.BackgroundProperty, isSel ? "BgSelected" : "BgSurface");
                if (isSel)
                    rowBd.BorderBrush = dotBrush;
                else
                    rowBd.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

                StackPanel vStack = new StackPanel();
                DockPanel topLine = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

                StackPanel rightNums = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(rightNums, Dock.Right);

                TextBlock moneyTxt = new TextBlock { Text = m.FormattedMoney, FontWeight = FontWeights.Bold, FontSize = 12.2 };
                moneyTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                rightNums.Children.Add(moneyTxt);

                TextBlock tokShareTxt = new TextBlock
                {
                    Text = string.Format(CultureInfo.InvariantCulture, " · {0} ({1:F1}%)", m.TotalTokensText, m.SharePercent),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                tokShareTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                rightNums.Children.Add(tokShareTxt);
                topLine.Children.Add(rightNums);

                StackPanel leftInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Ellipse dot = new Ellipse { Width = 7, Height = 7, Fill = dotBrush, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                leftInfo.Children.Add(dot);

                TextBlock nameTxt = new TextBlock
                {
                    Text = m.MachineName + (m.IsLocal ? " [本机]" : ""),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12.2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                leftInfo.Children.Add(nameTxt);

                TextBlock subMeta = new TextBlock
                {
                    Text = string.Format(CultureInfo.InvariantCulture, "  {0}工具 · {1}次", m.ToolCount, m.Turns),
                    FontSize = 10.8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                subMeta.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                leftInfo.Children.Add(subMeta);

                topLine.Children.Add(leftInfo);
                vStack.Children.Add(topLine);

                Grid barGrid = new Grid { Height = 4 };
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(m.BarRatio, GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1.0 - m.BarRatio), GridUnitType.Star) });

                Border trackBg = new Border { CornerRadius = new CornerRadius(2) };
                trackBg.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                Grid.SetColumnSpan(trackBg, 2);
                barGrid.Children.Add(trackBg);

                Border trackFill = new Border { CornerRadius = new CornerRadius(2), Background = dotBrush };
                Grid.SetColumn(trackFill, 0);
                barGrid.Children.Add(trackFill);

                vStack.Children.Add(barGrid);
                rowBd.Child = vStack;

                string machKey = m.MachineId;
                rowBd.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    _selectedMachineId = string.Equals(_selectedMachineId, machKey, StringComparison.OrdinalIgnoreCase) ? null : machKey;
                    RefreshDashboardView();
                };
                _toolsStack.Children.Add(rowBd);
            }
        }

        private void RenderToolsList(List<ToolRowView> tools)
        {
            _toolsStack.Children.Clear();
            if (tools.Count == 0)
            {
                TextBlock empty = new TextBlock
                {
                    Text = "当前时间或设备范围内暂无 AI 工具使用记录",
                    Margin = new Thickness(8, 24, 8, 8),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                _toolsStack.Children.Add(empty);
                return;
            }

            for (int i = 0; i < tools.Count; i++)
            {
                ToolRowView t = tools[i];
                bool isSel = (_selectedTool != null && string.Equals(_selectedTool, t.Tool, StringComparison.Ordinal));
                SolidColorBrush dotBrush = BrushHex(t.ColorHex);

                Border rowBd = new Border();
                rowBd.CornerRadius = new CornerRadius(7);
                rowBd.Padding = new Thickness(10, 7, 10, 7);
                rowBd.Margin = new Thickness(0, 0, 0, 5);
                rowBd.BorderThickness = new Thickness(1);
                rowBd.Cursor = Cursors.Hand;
                rowBd.SetResourceReference(Border.BackgroundProperty, isSel ? "BgSelected" : "BgSurface");
                if (isSel)
                    rowBd.BorderBrush = dotBrush;
                else
                    rowBd.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

                StackPanel vStack = new StackPanel();
                DockPanel topLine = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

                StackPanel rightNums = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(rightNums, Dock.Right);

                TextBlock moneyTxt = new TextBlock { Text = t.FormattedMoney, FontWeight = FontWeights.Bold, FontSize = 12.2 };
                moneyTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                rightNums.Children.Add(moneyTxt);

                TextBlock tokShareTxt = new TextBlock
                {
                    Text = string.Format(CultureInfo.InvariantCulture, " · {0} ({1:F1}%)", t.FormattedTokens, t.SharePercent),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                tokShareTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                rightNums.Children.Add(tokShareTxt);
                topLine.Children.Add(rightNums);

                StackPanel leftInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Ellipse dot = new Ellipse { Width = 7, Height = 7, Fill = dotBrush, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                leftInfo.Children.Add(dot);

                TextBlock nameTxt = new TextBlock
                {
                    Text = t.Tool,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12.2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                leftInfo.Children.Add(nameTxt);

                TextBlock turnsTxt = new TextBlock
                {
                    Text = string.Format(CultureInfo.InvariantCulture, "  {0}次", t.Turns),
                    FontSize = 10.8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                turnsTxt.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                leftInfo.Children.Add(turnsTxt);

                topLine.Children.Add(leftInfo);
                vStack.Children.Add(topLine);

                Grid barGrid = new Grid { Height = 4 };
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(t.BarRatio, GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1.0 - t.BarRatio), GridUnitType.Star) });

                Border trackBg = new Border { CornerRadius = new CornerRadius(2) };
                trackBg.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                Grid.SetColumnSpan(trackBg, 2);
                barGrid.Children.Add(trackBg);

                Border trackFill = new Border { CornerRadius = new CornerRadius(2), Background = dotBrush };
                Grid.SetColumn(trackFill, 0);
                barGrid.Children.Add(trackFill);

                vStack.Children.Add(barGrid);
                rowBd.Child = vStack;

                string toolKey = t.Tool;
                rowBd.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    _selectedTool = string.Equals(_selectedTool, toolKey, StringComparison.Ordinal) ? null : toolKey;
                    RefreshDashboardView();
                };
                rowBd.MouseEnter += delegate(object s, MouseEventArgs e)
                {
                    if (!string.Equals(_selectedTool, toolKey, StringComparison.Ordinal))
                        rowBd.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
                };
                rowBd.MouseLeave += delegate(object s, MouseEventArgs e)
                {
                    if (!string.Equals(_selectedTool, toolKey, StringComparison.Ordinal))
                        rowBd.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                };

                _toolsStack.Children.Add(rowBd);
            }
        }

        private void RenderTrendChart()
        {
            if (_chartCanvas == null) return;
            _chartCanvas.Children.Clear();

            double w = _chartHost.ActualWidth;
            double h = _chartHost.ActualHeight;
            if (w <= 60 || h <= 40) return;
            _chartCanvas.Width = w;
            _chartCanvas.Height = h;

            if (_currentDailyAsc == null || _currentDailyAsc.Count == 0)
            {
                TextBlock empty = new TextBlock { Text = "暂无每日趋势数据", FontSize = 12 };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                Canvas.SetLeft(empty, (w - 100) / 2.0);
                Canvas.SetTop(empty, (h - 20) / 2.0);
                _chartCanvas.Children.Add(empty);
                return;
            }

            double leftPad = 56.0;
            double rightPad = 8.0;
            double topPad = 8.0;
            double bottomPad = 22.0;
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

            SolidColorBrush gridBrush = (SolidColorBrush)Resources["BorderCard"];
            SolidColorBrush lblBrush = (SolidColorBrush)Resources["FgMuted"];

            double[] ratios = new double[] { 0.0, 0.5, 1.0 };
            for (int r = 0; r < ratios.Length; r++)
            {
                double y = topPad + plotH * (1.0 - ratios[r]);
                Line ln = new Line
                {
                    X1 = leftPad,
                    X2 = leftPad + plotW,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1.0
                };
                if (r > 0) ln.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
                _chartCanvas.Children.Add(ln);

                double refV = maxVal * ratios[r];
                string yText;
                if (_rankByCost)
                {
                    double usdEq = _isCny ? (refV / _usdToCny) : refV;
                    yText = ratios[r] == 0.0 ? (_isCny ? "¥0" : "$0") : FormatterUtil.FormatMoney(usdEq, _isCny, _usdToCny);
                }
                else
                {
                    yText = FormatterUtil.FormatTokens((long)refV);
                }

                TextBlock yLbl = new TextBlock
                {
                    Text = yText,
                    FontSize = 10.2,
                    Foreground = lblBrush,
                    Width = leftPad - 6,
                    TextAlignment = TextAlignment.Right
                };
                Canvas.SetLeft(yLbl, 0);
                Canvas.SetTop(yLbl, y - 7);
                _chartCanvas.Children.Add(yLbl);
            }

            int n = _currentDailyAsc.Count;
            double slotW = plotW / n;
            double gap = n > 45 ? 1.5 : (n > 20 ? 3.0 : 5.0);
            double barW = Math.Max(2.0, Math.Min(32.0, slotW - gap));

            LinearGradientBrush barGrad = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(_isDark ? "#38BDF8" : "#0EA5E9"),
                (Color)ColorConverter.ConvertFromString(_isDark ? "#6366F1" : "#4F46E5"),
                90.0);
            barGrad.Freeze();

            LinearGradientBrush hoverGrad = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#7DD3FC"),
                (Color)ColorConverter.ConvertFromString("#818CF8"),
                90.0);
            hoverGrad.Freeze();

            for (int i = 0; i < n; i++)
            {
                DailyRowView d = _currentDailyAsc[i];
                double v = _rankByCost ? (_isCny ? d.CostCny : d.CostUsd) : (double)d.TotalTokens;
                double bh = Math.Max(2.5, (v / maxVal) * plotH);
                double xCenter = leftPad + slotW * i + slotW * 0.5;
                double bx = xCenter - barW * 0.5;
                double by = topPad + plotH - bh;

                Border bar = new Border
                {
                    Width = barW,
                    Height = bh,
                    CornerRadius = new CornerRadius(Math.Min(4.0, barW * 0.4), Math.Min(4.0, barW * 0.4), 0, 0),
                    Background = barGrad
                };
                Canvas.SetLeft(bar, bx);
                Canvas.SetTop(bar, by);
                _chartCanvas.Children.Add(bar);

                Border hit = new Border
                {
                    Width = Math.Max(barW, slotW),
                    Height = plotH,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                hit.ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\n等效花费: {1} ({2})\n总 Token: {3}  (请求 {4} 次)\n新增输入: {5}  |  缓存读: {6}  |  输出: {7}",
                    d.Date,
                    d.CnyText,
                    d.UsdText,
                    d.TotalTokensText,
                    d.Turns,
                    d.FreshInputText,
                    d.CacheReadText,
                    d.OutputText);

                DailyRowView captured = d;
                Border capturedBar = bar;
                hit.MouseEnter += delegate(object s, MouseEventArgs e)
                {
                    capturedBar.Background = hoverGrad;
                    _trendHoverText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}  ·  {1}  ·  {2} Tokens  ·  {3} 次请求",
                        captured.Date,
                        _isCny ? captured.CnyText : captured.UsdText,
                        captured.TotalTokensText,
                        captured.Turns);
                };
                hit.MouseLeave += delegate(object s, MouseEventArgs e)
                {
                    capturedBar.Background = barGrad;
                };

                Canvas.SetLeft(hit, leftPad + slotW * i);
                Canvas.SetTop(hit, topPad);
                _chartCanvas.Children.Add(hit);
            }

            int[] tickIdx = n == 1 ? new int[] { 0 } : (n == 2 ? new int[] { 0, 1 } : new int[] { 0, n / 2, n - 1 });
            for (int t = 0; t < tickIdx.Length; t++)
            {
                int idx = tickIdx[t];
                TextBlock xl = new TextBlock
                {
                    Text = _currentDailyAsc[idx].Date,
                    FontSize = 10.2,
                    Foreground = lblBrush
                };
                double lx = leftPad + slotW * idx + slotW * 0.5 - 30.0;
                if (lx < leftPad) lx = leftPad;
                if (lx > leftPad + plotW - 62.0) lx = leftPad + plotW - 62.0;
                Canvas.SetLeft(xl, lx);
                Canvas.SetTop(xl, topPad + plotH + 4.0);
                _chartCanvas.Children.Add(xl);
            }
        }

        // -------------------- Dialog & Menu Handlers --------------------
        private void OpenSyncDialog(int initialTab)
        {
            bool prevWsl = _syncCfg.ScanLocalWsl;
            SyncWindow dlg = new SyncWindow(this, _syncCfg, _scanResult, delegate(SyncOutcome outcome)
            {
                _localNode = SyncService.BuildLocalSnapshot(_scanResult, _syncCfg);
                if (outcome != null)
                {
                    _remoteNodes = outcome.RemoteNodes ?? SyncService.LoadCachedRemoteNodes(_syncCfg.MachineId);
                    _lastSyncStatus = outcome.Success
                        ? ("已同步 " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture))
                        : "同步异常";
                }
                else
                {
                    _remoteNodes = SyncService.LoadCachedRemoteNodes(_syncCfg.MachineId);
                }
                UpdateCloudStatusUi();

                if (_syncCfg.ScanLocalWsl != prevWsl)
                {
                    prevWsl = _syncCfg.ScanLocalWsl;
                    RunSilentScanAsync(_homeDir, false, true);
                }
                else
                {
                    RefreshDashboardView();
                }
            }, initialTab);
            dlg.ShowDialog();
        }

        private void OnChooseDirectoryClick(object sender, RoutedEventArgs e)
        {
            using (WinForms.FolderBrowserDialog dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "请选择要扫描的 Windows 用户主目录（例如 C:\\Users\\YourName）";
                dlg.SelectedPath = _homeDir;
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                {
                    _homeDir = dlg.SelectedPath;
                    _selectedTool = null;
                    RebuildDirectoryWatchers(_homeDir);
                    RunSilentScanAsync(_homeDir, false, true);
                }
            }
        }

        private void OnOpenWebReportClick(object sender, RoutedEventArgs e)
        {
            if (_scanResult == null) return;
            try
            {
                string reportPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TokLens_Report.html");
                HtmlExporter.ExportHtmlReport(GetAllMergedRecords(), _scanResult, _usdToCny, reportPath);
                ProcessStartInfo psi = new ProcessStartInfo(reportPath);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "生成网页报告失败：" + ex.Message, "知微 · TokLens", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnExportCsvClick(object sender, RoutedEventArgs e)
        {
            if (_scanResult == null) return;
            using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
            {
                sfd.Title = "导出多设备 Token 账单明细 CSV";
                sfd.Filter = "CSV 表格文件 (*.csv)|*.csv";
                sfd.FileName = "TokLens_多端Token账单_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".csv";
                if (sfd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                {
                    try
                    {
                        FormatterUtil.ExportCsv(GetAllMergedRecords(), _usdToCny, sfd.FileName);
                        MessageBox.Show(this, "多设备 CSV 账单明细已导出至：\n" + sfd.FileName, "导出成功 · 知微 TokLens", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "导出 CSV 失败：" + ex.Message, "知微 · TokLens", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private int FindPresetRangeIndex(int days)
        {
            for (int i = 0; i < PresetRangeDays.Length; i++)
            {
                if (PresetRangeDays[i] == days) return i;
            }
            return -1;
        }

        private void OnShowArchiveProtectionStatusClick(object sender, RoutedEventArgs e)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string resolvedDbPath = string.IsNullOrEmpty(appData)
                ? @"%APPDATA%\TokLens\toklens.db"
                : System.IO.Path.Combine(System.IO.Path.Combine(appData, "TokLens"), "toklens.db");
            bool dbExists = false;
            long dbBytes = 0L;
            try
            {
                if (!string.IsNullOrEmpty(appData) && File.Exists(resolvedDbPath))
                {
                    dbExists = true;
                    dbBytes = new FileInfo(resolvedDbPath).Length;
                }
            }
            catch { }

            int activeFiles = _scanResult != null ? _scanResult.TotalFilesTracked : 0;
            int archivedFiles = _scanResult != null ? _scanResult.ArchivedFilesCount : 0;
            string dbStatusNote = dbExists
                ? string.Format(CultureInfo.InvariantCulture, "（已就绪，约 {0:F1} KB）", dbBytes / 1024.0)
                : "（首次扫描完成后自动生成）";

            string syncStateText;
            if (_syncCfg != null && _syncCfg.IsConfigured)
            {
                string ch = string.Equals(_syncCfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase)
                    ? "WebDAV 网盘"
                    : "GitHub 私有 Gist";
                syncStateText = "已激活（通道：" + ch + "）";
            }
            else
            {
                syncStateText = "未配置（可在顶部「多端同步」中开启跨重装云端高水位保护）";
            }

            string msg = string.Format(
                CultureInfo.InvariantCulture,
                "【双层历史账单防清理持久归档：已启用】\n\n" +
                "1. 本机 SQLite 游标与按日高水位库\n" +
                "   • 库路径：%APPDATA%\\TokLens\\toklens.db\n" +
                "   • 实际位置：{0} {1}\n" +
                "   • 当前跟踪活跃文件数：{2:N0} 个\n" +
                "   • 已保护的被清理历史文件数：{3:N0} 个\n\n" +
                "2. 云端 Gist / WebDAV 高水位合并保护\n" +
                "   • 多端同步状态：{4}\n" +
                "   • 保护机制：电脑或 Linux VPS（~/.toklens/ledger.db）定期清理会话日志、清空缓存，甚至彻底重装电脑操作系统，本地归档库与云端高水位合并（High-Water-Mark）均会完整保留已入库与已同步的历史账单，永不归零。",
                resolvedDbPath,
                dbStatusNote,
                activeFiles,
                archivedFiles,
                syncStateText);

            MessageBox.Show(
                this,
                msg,
                "防清理归档保护状态 — 知微 · TokLens",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnToggleThemeClick(object sender, RoutedEventArgs e)
        {
            _isDark = !_isDark;
            if (_themeMenuItem != null)
                _themeMenuItem.Header = _isDark ? "切换为浅色主题" : "切换为深色主题";
            ApplyThemeResources();
            UpdateSegVisuals(_rangeBtns, FindPresetRangeIndex(_rangeDays));
            UpdateSegVisuals(_currencyBtns, _isCny ? 0 : 1);
            UpdateSegVisuals(_leftModeBtns, _leftRankByMachine ? 1 : 0);
            UpdateSegVisuals(_metricBtns, _rankByCost ? 0 : 1);
            UpdateSegVisuals(_tabBtns, _activeTab == 4 ? -1 : _activeTab);
            RefreshDashboardView();
        }
    }
}
