using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TokLens
{
    // This same gesture classifier is used by routed mouse events and the regression tests.
    internal sealed class HudPointerGesture
    {
        private Point start;
        private bool armed, moved, doubleClick;
        public void Begin(Point point, int clickCount) { start = point; armed = true; moved = false; doubleClick = clickCount > 1; }
        public bool Move(Point point, double xThreshold, double yThreshold)
        {
            if (armed && (Math.Abs(point.X - start.X) >= xThreshold || Math.Abs(point.Y - start.Y) >= yThreshold)) moved = true;
            return armed && moved;
        }
        public bool End() { bool click = armed && !moved && !doubleClick; armed = false; return click; }
        public void Cancel() { armed = false; }
    }

    internal static class HudPlacement
    {
        public static int BestScreen(Rect saved, IList<Rect> areas)
        {
            int best = 0; double bestOverlap = -1, bestDistance = double.MaxValue;
            for (int i = 0; i < areas.Count; i++)
            {
                Rect overlap = Rect.Intersect(saved, areas[i]); double size = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                double x = saved.X + saved.Width / 2, y = saved.Y + saved.Height / 2;
                double dx = Math.Max(areas[i].Left - x, Math.Max(0, x - areas[i].Right));
                double dy = Math.Max(areas[i].Top - y, Math.Max(0, y - areas[i].Bottom));
                double distance = dx * dx + dy * dy;
                if (size > bestOverlap || (size == bestOverlap && distance < bestDistance)) { best = i; bestOverlap = size; bestDistance = distance; }
            }
            return best;
        }
        public static Rect Clamp(Rect bounds, Rect area)
        {
            return new Rect(Math.Max(area.Left, Math.Min(bounds.Left, area.Right - bounds.Width)),
                Math.Max(area.Top, Math.Min(bounds.Top, area.Bottom - bounds.Height)), bounds.Width, bounds.Height);
        }
        public static Rect AtAnchor(Rect area, double width, double height, double x, double y)
        { return Clamp(new Rect(area.Left + Math.Max(0, area.Width - width) * Math.Max(0, Math.Min(1, x)), area.Top + Math.Max(0, area.Height - height) * Math.Max(0, Math.Min(1, y)), width, height), area); }
    }

    public sealed class HudPreferences
    {
        public string Style = "capsule", SessionKey = "", ScreenName = "";
        public bool Locked;
        public double Opacity = 1, X = double.NaN, Y = double.NaN, AnchorX = 1, AnchorY = .15;
        public static string FilePath { get { return System.IO.Path.Combine(SyncService.AppConfigDir, "hud_preferences.json"); } }
        public static HudPreferences Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    HudPreferences p = new JavaScriptSerializer().Deserialize<HudPreferences>(File.ReadAllText(FilePath));
                    if (p != null) { p.Opacity = double.IsNaN(p.Opacity) || double.IsInfinity(p.Opacity) ? 1 : Math.Max(.5, Math.Min(1, p.Opacity)); p.SessionKey = p.SessionKey ?? ""; p.ScreenName = p.ScreenName ?? ""; p.Style = "capsule"; return p; }
                }
            }
            catch { }
            return new HudPreferences();
        }
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SyncService.AppConfigDir);
                // JSON cannot represent NaN; an unpositioned window simply keeps the default anchor.
                if (double.IsNaN(X) || double.IsInfinity(X)) X = 0;
                if (double.IsNaN(Y) || double.IsInfinity(Y)) Y = 0;
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
            }
            catch { }
        }
    }

    /// <summary>A quiet, labelled status capsule.</summary>
    public sealed class MiniHudWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        private readonly Action _restore, _refresh;
        private readonly Border _surface, _cardShell;
        private readonly Grid _capsule;
        private readonly TextBlock _title, _context, _status, _scope;
        private readonly Ellipse _statusDot;
        private readonly Popup _cardPopup;
        private readonly TextBlock _cardModel, _cardScope, _cardContext, _cardTier, _cardTiming, _cardNote, _cardToday, _cardHour, _cardHealth, _cardAlert, _cardCoverage;
        private readonly ComboBox _sessionPicker;
        private readonly Canvas _chart;
        private readonly Button _muteButton;
        private readonly DispatcherTimer _tick;
        private readonly HudPointerGesture _gesture = new HudPointerGesture();
        private readonly HudPreferences _preferences;
        private ScanResult _source;
        private HudTelemetry _data = new HudTelemetry();
        private string _scanError = "", _alertText = "", _sessionChoices = "";
        private DateTime _lastSuccessUtc = DateTime.MinValue, _alertUntilUtc = DateTime.MinValue, _mutedUntilUtc = DateTime.MinValue, _clockUtc = DateTime.UtcNow;
        private bool _dark, _selecting, _dragging, _closed;
        private Point _pointerStart, _windowStart;
        private bool _cny; private double _rate = ExchangeRateService.DefaultRate;

        public MiniHudWindow(bool isDark, Action onRestoreMainWindow, Action onRefreshRequested)
        {
            _restore = onRestoreMainWindow; _refresh = onRefreshRequested; _preferences = HudPreferences.Load();
            Title = "TokLens · 状态胶囊"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true; SnapsToDevicePixels = true; FontFamily = DesignSystem.FontUi;
            _surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand };
            Content = _surface;
            System.Windows.Automation.AutomationProperties.SetName(_surface, "TokLens 状态：单击打开详情，拖动移动");
            _capsule = new Grid { Width = 224, Height = 38 };
            _capsule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _capsule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _capsule.RowDefinitions.Add(new RowDefinition()); _capsule.RowDefinitions.Add(new RowDefinition());
            _title = Text("TokLens", 12, true); _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.Margin = new Thickness(0, 0, 8, 0); _capsule.Children.Add(_title);
            _context = Text("—", 14, true); Grid.SetColumn(_context, 1); _capsule.Children.Add(_context);
            StackPanel statusLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            _statusDot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            statusLine.Children.Add(_statusDot); _status = Text("等待采集", 10, false); statusLine.Children.Add(_status); Grid.SetRow(statusLine, 1); _capsule.Children.Add(statusLine);
            _scope = Text("请求输入", 10, false); _scope.HorizontalAlignment = HorizontalAlignment.Right; _scope.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(_scope, 1); Grid.SetColumn(_scope, 1); _capsule.Children.Add(_scope);

            _cardShell = new Border { Width = 348, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            StackPanel panel = new StackPanel(); _cardShell.Child = panel;
            DockPanel head = new DockPanel(); Button close = Button("关闭", delegate { CloseCard(); }); DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
            head.Children.Add(Text("请求状态", 15, true)); panel.Children.Add(head);
            _sessionPicker = ModernControlsFactory.CreateComboBox();
            _sessionPicker.Margin = new Thickness(0, 10, 0, 8); _sessionPicker.DisplayMemberPath = "Label"; _sessionPicker.SelectedValuePath = "Key";
            System.Windows.Automation.AutomationProperties.SetName(_sessionPicker, "选择并固定会话");
            _sessionPicker.SelectionChanged += delegate
            {
                if (_selecting) return; HudSessionChoice choice = _sessionPicker.SelectedItem as HudSessionChoice; if (choice == null) return;
                _preferences.SessionKey = choice.Key; SavePreferences(); RefreshAt(DateTime.UtcNow);
            };
            panel.Children.Add(_sessionPicker);
            _cardModel = Text("等待请求", 14, true); panel.Children.Add(_cardModel);
            _cardScope = Text("", 11, false); _cardScope.Margin = new Thickness(0, 3, 0, 8); _cardScope.TextWrapping = TextWrapping.Wrap; panel.Children.Add(_cardScope);
            _cardContext = Line(panel, "请求输入上下文"); _cardTier = Line(panel, "服务档位"); _cardTiming = Line(panel, "最近输出速率");
            _cardNote = Text("", 10, false); _cardNote.Margin = new Thickness(0, 5, 0, 10); _cardNote.TextWrapping = TextWrapping.Wrap; panel.Children.Add(_cardNote);
            Border divider = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) }; divider.SetResourceReference(Border.BackgroundProperty, "BorderSubtle"); panel.Children.Add(divider);
            panel.Children.Add(Text("本机全部工具 · 记录 / API 估值", 12, true));
            _cardToday = Line(panel, "今日记录 / 估值"); _cardHour = Line(panel, "近 15 分钟折合每小时");
            TextBlock chartLabel = Text("近 2 小时记录 / API 估值 · 每格 5 分钟", 10, false); chartLabel.Margin = new Thickness(0, 10, 0, 5); panel.Children.Add(chartLabel);
            _chart = new Canvas { Height = 28, Width = 312 }; panel.Children.Add(_chart);
            _cardCoverage = Text("", 10, false); _cardCoverage.TextWrapping = TextWrapping.Wrap; _cardCoverage.Margin = new Thickness(0, 5, 0, 6); panel.Children.Add(_cardCoverage);
            _cardAlert = Text("", 11, true); _cardAlert.TextWrapping = TextWrapping.Wrap; _cardAlert.Margin = new Thickness(0, 8, 0, 4); panel.Children.Add(_cardAlert);
            _muteButton = Button("暂停此类提示 30 分钟", delegate { _mutedUntilUtc = DateTime.UtcNow.AddMinutes(30); RefreshAt(DateTime.UtcNow); }); panel.Children.Add(_muteButton);
            _cardHealth = Text("", 10, false); _cardHealth.TextWrapping = TextWrapping.Wrap; _cardHealth.Margin = new Thickness(0, 10, 0, 8); panel.Children.Add(_cardHealth);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Button("重新采集", delegate { if (_refresh != null) _refresh(); }));
            actions.Children.Add(Button("打开主窗口", delegate { CloseCard(); if (_restore != null) _restore(); })); panel.Children.Add(actions);
            _cardPopup = new Popup { PlacementTarget = _surface, Placement = PlacementMode.Bottom, VerticalOffset = 6, AllowsTransparency = true, StaysOpen = false, Child = _cardShell, PopupAnimation = PopupAnimation.None };
            _cardPopup.Opened += delegate { RenderCard(); };
            _cardShell.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { CloseCard(); e.Handled = true; } };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { CloseCard(); e.Handled = true; } };
            ToolTipService.SetInitialShowDelay(_surface, 600); ToolTipService.SetShowDuration(_surface, 4000);
            BindPointer(); BuildMenu();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += delegate { RefreshAt(DateTime.UtcNow); };
            IsVisibleChanged += delegate { if (IsVisible) { RefreshAt(DateTime.UtcNow); _tick.Start(); } else { _tick.Stop(); CloseCard(); } };
            SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80 | 0x08000000); // TOOLWINDOW | NOACTIVATE
                HwndSource src = HwndSource.FromHwnd(hwnd);
                if (src != null) src.AddHook(delegate(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
                {
                    if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: touching the capsule keeps editor focus.
                    if (msg == 0x02E0 || msg == 0x007E) Dispatcher.BeginInvoke(new Action(delegate { if (!_closed) KeepOnScreen(); }), DispatcherPriority.Loaded);
                    return IntPtr.Zero;
                });
            };
            Loaded += delegate { ApplyStyle(); RestorePosition(); };
            Closed += delegate { _closed = true; _tick.Stop(); CloseCard(); };
            SetTheme(isDark); ApplyStyle(); RefreshAt(DateTime.UtcNow);
        }

        public void SetTheme(bool dark)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { SetTheme(dark); })); return; }
            _dark = dark; DesignSystem.ApplyTheme(this, dark); _cardShell.Resources = Resources;
            Brush bg = new SolidColorBrush(dark ? Color.FromRgb(27, 31, 38) : Color.FromRgb(250, 251, 253));
            Brush border = new SolidColorBrush(dark ? Color.FromRgb(65, 73, 86) : Color.FromRgb(210, 217, 226));
            _surface.Background = bg; _surface.BorderBrush = border; _cardShell.Background = bg; _cardShell.BorderBrush = border;
            Render(); if (_cardPopup.IsOpen) RenderCard();
        }
        public void UpdateSource(ScanResult result, bool cny, double rate, string scanError)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { UpdateSource(result, cny, rate, scanError); })); return; }
            _source = result; _cny = cny; _rate = rate; _scanError = scanError ?? "";
            DateTime generated;
            if (_scanError.Length == 0 && result != null && DateTime.TryParse(result.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out generated)) _lastSuccessUtc = generated.ToUniversalTime();
            else if (_scanError.Length == 0 && result != null) _lastSuccessUtc = DateTime.UtcNow;
            RefreshAt(DateTime.UtcNow);
        }
        public void SetAlert(string message, DateTime expiresUtc)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { SetAlert(message, expiresUtc); })); return; }
            _alertText = message ?? ""; _alertUntilUtc = expiresUtc.ToUniversalTime(); RefreshAt(DateTime.UtcNow);
        }
        // Kept for embedders and deterministic rendering fixtures. Production uses UpdateSource.
        public void UpdateTelemetry(HudTelemetry telemetry)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { UpdateTelemetry(telemetry); })); return; }
            _source = null; _data = telemetry ?? new HudTelemetry(); _lastSuccessUtc = DateTime.UtcNow;
            if (_data.Alert) { _alertText = _data.AlertText; _alertUntilUtc = DateTime.UtcNow.AddMinutes(10); }
            else { _alertText = ""; _alertUntilUtc = DateTime.MinValue; }
            RefreshAt(DateTime.UtcNow);
        }
        internal void RefreshAt(DateTime utcNow)
        {
            _clockUtc = utcNow.ToUniversalTime();
            if (_source != null) _data = HudTelemetry.FromScan(_source, _clockUtc.ToLocalTime(), _cny, _rate, _preferences.SessionKey);
            _data.Alert = _alertText.Length > 0 && _clockUtc < _alertUntilUtc && _clockUtc >= _mutedUntilUtc;
            _data.AlertText = _data.Alert ? _alertText : "";
            Render(); if (_cardPopup.IsOpen) RenderCard();
        }
        private string Health()
        {
            if (_scanError.Length > 0) return "采集异常";
            bool anyError = false, anyFound = false;
            if (_source != null && _source.ProviderHealth != null) foreach (ProviderHealth p in _source.ProviderHealth)
            { if (p == null) continue; anyError |= p.Status == "error"; anyFound |= p.Status != "not_installed"; }
            if (anyError) return "部分采集异常";
            if (_lastSuccessUtc == DateTime.MinValue) return "等待首次采集";
            if ((_clockUtc - _lastSuccessUtc).TotalSeconds > 45) return "采集结果已陈旧";
            if (_data.TimestampMs <= 0) return _preferences.SessionKey.Length > 0 ? "固定会话暂无记录" : (!anyFound && _source != null && _source.ProviderHealth != null && _source.ProviderHealth.Count > 0 ? "未发现工具日志" : "监听中 · 暂无请求");
            long age = AgeSeconds();
            return age <= 60 ? "刚有请求" : (age <= 300 ? "最近 " + (age / 60) + " 分钟前" : "空闲 · " + Ago(age));
        }
        private string CapsuleStatus()
        {
            string health = Health();
            if (_data.Alert) return "消耗提示 · " + health;
            bool stale = _lastSuccessUtc != DateTime.MinValue && (_clockUtc - _lastSuccessUtc).TotalSeconds > 45;
            if (_scanError.Length > 0 || stale || _data.TimestampMs <= 0 || _data.Tps <= 0) return health;
            string tpsShort = UsageTiming.FormatNumber(_data.Tps) + " t/s";
            return _data.IsRecent(_clockUtc) ? (health + " · " + tpsShort) : ("空闲 · " + tpsShort);
        }
        private void Render()
        {
            if (_title == null) return;
            bool has = _data.TimestampMs > 0;
            _title.Text = has ? (_preferences.SessionKey.Length > 0 ? "固定 · " : "最新 · ") + HudTelemetry.ShortTool(_data.Tool) + " · " + HudTelemetry.SessionLabel(_data.Workspace, _data.SessionId) : "TokLens · 本机";
            _context.Text = ContextValue();
            _scope.Text = _data.ContextLimit > 0 ? (_data.ContextLimitIsActual ? "输入 / 会话上限" : "输入 / 标称上限") : "请求输入";
            _status.Text = CapsuleStatus();
            _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.MaxWidth = 142;
            _statusDot.Fill = new SolidColorBrush(_data.Alert || _scanError.Length > 0 ? Color.FromRgb(220, 145, 61) : (_data.IsRecent(_clockUtc) ? Color.FromRgb(62, 173, 140) : Color.FromRgb(136, 148, 163)));
            _surface.Opacity = _preferences.Opacity;
            string scope = _preferences.SessionKey.Length > 0 ? "已固定会话" : "最新请求（本机）";
            string tpsTip = _data.Tps > 0 ? " · 均速 " + FormatterUtil.FormatTps(_data.Tps) : "";
            _surface.ToolTip = scope + " · " + _title.Text + "\n" + _scope.Text + " " + ContextValue() + " · " + Health() + tpsTip + "\n单击详情 · 拖动移动 · 右键设置";
            System.Windows.Automation.AutomationProperties.SetHelpText(_surface, scope + "；" + _title.Text + "；" + _scope.Text + " " + ContextValue() + "；" + Health() + tpsTip);
        }
        private string ContextValue()
        {
            if (_data.ContextTokens <= 0) return "—";
            return _data.ContextFraction >= 0 ? (_data.ContextFraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : CompactTokens(_data.ContextTokens);
        }
        private void RenderCard()
        {
            string choices = string.Join("|", _data.Sessions.ConvertAll(delegate(HudSessionChoice c) { return c.Key + "=" + c.Label; }).ToArray());
            if (choices != _sessionChoices)
            {
                _sessionChoices = choices; _selecting = true; _sessionPicker.ItemsSource = _data.Sessions; _sessionPicker.SelectedValue = _preferences.SessionKey; _selecting = false;
            }
            _cardModel.Text = _data.TimestampMs > 0 ? HudTelemetry.ShortTool(_data.Tool) + " · " + HudTelemetry.ShortModel(_data.Model) : "暂无已采集请求";
            _cardScope.Text = (_preferences.SessionKey.Length > 0 ? "已固定会话 · " : "最新请求 · ") + HudTelemetry.SessionLabel(_data.Workspace, _data.SessionId) + (_data.TimestampMs > 0 ? " · " + Ago(AgeSeconds()) : "");
            _cardScope.ToolTip = (_data.Workspace ?? "") + "\n" + (_data.SessionId ?? "");
            _cardContext.Text = _data.ContextTokens > 0 ? FormatterUtil.FormatTokens(_data.ContextTokens) + (_data.ContextLimit > 0 ? " / " + FormatterUtil.FormatTokens(_data.ContextLimit) : " · 上限未知") : "—";
            _cardContext.ToolTip = _data.ContextLimitIsActual ? "本次请求输入与日志上报的会话上限；不是套餐额度。" : "分子为最新请求输入，分母为价格库的模型标称上限；不代表工具实际可用上下文或套餐额度。";
            _cardTier.Text = _data.TierLabel; _cardTier.ToolTip = _data.TierEvidence;
            if (_data.TimestampMs > 0)
            {
                if (_data.Tps > 0)
                {
                    _cardTiming.Text = FormatterUtil.FormatTps(_data.Tps) + " · " + FormatterUtil.FormatDuration(_data.DurationMs);
                    _cardTiming.ToolTip = "按整次请求输出 Token 与日志实测总耗时计算的均速（含首字/思考等待，非纯流式速率）。";
                }
                else
                {
                    _cardTiming.Text = "未提供可靠计时";
                    _cardTiming.ToolTip = "当前会话日志未提供可靠的单次请求耗时，不进行估算。";
                }
            }
            else
            {
                _cardTiming.Text = "—";
                _cardTiming.ToolTip = null;
            }
            if (_data.TimestampMs > 0 && _data.Tps > 0)
                _cardNote.Text = (_data.ContextLimitIsActual ? "上限由会话日志提供" : "标称上限仅作参照") + "；均速含首字等待（非流式）。";
            else
                _cardNote.Text = _data.ContextLimitIsActual ? "上限由会话日志提供。" : "标称上限仅作参照；未获会话实际配置。";
            _cardToday.Text = _data.UnpricedRequests > 0 && !_data.TodayHasPricedAmount ? "—" : Money(_data.TodayUsd) + (_data.UnpricedRequests > 0 ? " *" : "");
            _cardToday.ToolTip = _data.UnpricedRequests > 0 ? "仅包含已计价部分；今日 " + _data.UnpricedRequests + " 次请求未计价。金额含日志记录和 API 估值，不代表订阅实付。" : "日志记录金额与 API 估值的合计，不代表订阅实付。";
            _cardHour.Text = _data.Last15UnpricedRequests > 0 && _data.Last15PricedRequests == 0 ? "— · 未计价" : Money(_data.Last15Usd * 4) + "/h" + (_data.Last15UnpricedRequests > 0 ? "（已计价部分）" : "");
            _cardCoverage.Text = (_data.WindowComplete ? "完整采集时间窗" : "旧快照或样本不完整，速率与趋势可能低估") + (_data.UnpricedRequests > 0 ? " · 今日 " + _data.UnpricedRequests + " 次未计价" : "") + (_data.TwoHourUnpricedRequests > 0 ? " · 近 2 小时 " + _data.TwoHourUnpricedRequests + " 次未计价，柱图仅含已计价部分" : "");
            _cardAlert.Text = _data.AlertText; _cardAlert.Visibility = _data.Alert ? Visibility.Visible : Visibility.Collapsed; _muteButton.Visibility = _data.Alert ? Visibility.Visible : Visibility.Collapsed;
            _cardHealth.Text = Health() + (_lastSuccessUtc != DateTime.MinValue ? " · 上次采集 " + _lastSuccessUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "") + (_scanError.Length > 0 ? "\n" + _scanError : "");
            RenderChart();
        }
        private void RenderChart()
        {
            // Keep elements stable so moving the clock does not destroy an open tooltip.
            if (_chart.Children.Count != 24) { _chart.Children.Clear(); for (int i = 0; i < 24; i++) _chart.Children.Add(new Rectangle { Width = 9, RadiusX = 2, RadiusY = 2 }); }
            double max = 0; foreach (double value in _data.SparkTwoHours) max = Math.Max(max, value);
            for (int i = 0; i < 24; i++)
            {
                Rectangle bar = (Rectangle)_chart.Children[i]; double value = _data.SparkTwoHours[i]; bar.Height = max > 0 && value > 0 ? Math.Max(2, value / max * 28) : 2;
                bar.Fill = new SolidColorBrush(i == 23 && value > 0 ? Color.FromRgb(62, 173, 140) : (_dark ? Color.FromRgb(100, 114, 131) : Color.FromRgb(163, 174, 190)));
                Canvas.SetLeft(bar, i * 13); Canvas.SetTop(bar, 28 - bar.Height);
                DateTime from = _clockUtc.ToLocalTime().AddMinutes(-5 * (24 - i));
                long unpriced = _data.SparkTwoHoursUnpriced[i];
                bar.ToolTip = from.ToString("HH:mm", CultureInfo.InvariantCulture) + "–" + from.AddMinutes(5).ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + (unpriced > 0 && value == 0 ? "—" : Money(value)) + (unpriced > 0 ? " · " + unpriced + " 次未计价，金额仅含已计价部分" : "");
            }
        }
        private void OpenCard() { if (_cardPopup.IsOpen) return; _cardPopup.IsOpen = true; RenderCard(); }
        private void CloseCard() { if (_cardPopup != null) _cardPopup.IsOpen = false; }
        private void BindPointer()
        {
            _surface.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                _pointerStart = CursorPoint(); Rect bounds = NativeBounds(); _windowStart = new Point(bounds.Left, bounds.Top);
                _gesture.Begin(_pointerStart, e.ClickCount); _dragging = false; _surface.CaptureMouse(); e.Handled = true;
            };
            _surface.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed || !_surface.IsMouseCaptured) return;
                Point point = CursorPoint();
                if (!_gesture.Move(point, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
                _dragging = true; CloseCard();
                if (!_preferences.Locked) MoveNative(_windowStart.X + point.X - _pointerStart.X, _windowStart.Y + point.Y - _pointerStart.Y);
            };
            _surface.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                bool click = _gesture.End(); _surface.ReleaseMouseCapture();
                if (_dragging) { KeepOnScreen(); SavePreferences(); }
                else if (click) { if (_cardPopup.IsOpen) CloseCard(); else OpenCard(); }
                _dragging = false; e.Handled = true;
            };
            _surface.LostMouseCapture += delegate { _gesture.Cancel(); };
        }
        private void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem locked = new MenuItem { Header = "锁定位置", IsCheckable = true, IsChecked = _preferences.Locked };
            locked.Click += delegate { _preferences.Locked = locked.IsChecked; SavePreferences(); }; menu.Items.Add(locked);
            MenuItem opacity = new MenuItem { Header = "透明度" };
            foreach (double n in new double[] { 1, .85, .65 }) { double value = n; MenuItem item = new MenuItem { Header = (n * 100).ToString("0") + "%" }; item.Click += delegate { _preferences.Opacity = value; Render(); SavePreferences(); }; opacity.Items.Add(item); } menu.Items.Add(opacity);
            MenuItem latest = new MenuItem { Header = "跟随最新请求（解除固定）" }; latest.Click += delegate { _preferences.SessionKey = ""; SavePreferences(); _sessionChoices = ""; RefreshAt(DateTime.UtcNow); }; menu.Items.Add(latest);
            MenuItem restore = new MenuItem { Header = "打开主窗口" }; restore.Click += delegate { if (_restore != null) _restore(); }; menu.Items.Add(restore);
            MenuItem hide = new MenuItem { Header = "隐藏（从托盘可恢复）" }; hide.Click += delegate { Hide(); }; menu.Items.Add(hide);
            menu.Opened += delegate { CloseCard(); }; _surface.ContextMenu = menu;
        }
        private void ApplyStyle()
        {
            CloseCard();
            _surface.Child = _capsule;
            _surface.Padding = new Thickness(12, 8, 12, 8); _surface.CornerRadius = new CornerRadius(14);
            if (IsLoaded) Dispatcher.BeginInvoke(new Action(KeepOnScreen), DispatcherPriority.Loaded);
        }
        private void SavePreferences()
        {
            if (IsLoaded)
            {
                Rect bounds = NativeBounds(); System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
                Rect area = Area(screen); _preferences.X = bounds.Left; _preferences.Y = bounds.Top; _preferences.ScreenName = screen.DeviceName;
                _preferences.AnchorX = area.Width > bounds.Width ? (bounds.Left - area.Left) / (area.Width - bounds.Width) : 0;
                _preferences.AnchorY = area.Height > bounds.Height ? (bounds.Top - area.Top) / (area.Height - bounds.Height) : 0;
            }
            _preferences.Save();
        }
        private void RestorePosition()
        {
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens; if (screens.Length == 0) return;
            List<Rect> areas = new List<Rect>(); int selected = -1;
            for (int i = 0; i < screens.Length; i++) { areas.Add(Area(screens[i])); if (screens[i].DeviceName == _preferences.ScreenName) selected = i; }
            if (selected < 0)
            {
                if (!double.IsNaN(_preferences.X) && !double.IsNaN(_preferences.Y)) selected = HudPlacement.BestScreen(new Rect(_preferences.X, _preferences.Y, 240, 54), areas);
                else { selected = 0; for (int i = 0; i < screens.Length; i++) if (screens[i].Primary) selected = i; }
            }
            double dpi = ScreenScale(areas[selected]); double w = 250 * dpi, h = 56 * dpi;
            Rect target = HudPlacement.AtAnchor(areas[selected], w, h, _preferences.AnchorX, _preferences.AnchorY); MoveNative(target.Left, target.Top);
            Dispatcher.BeginInvoke(new Action(KeepOnScreen), DispatcherPriority.Loaded);
        }
        private void KeepOnScreen()
        {
            if (!IsLoaded) return; Rect bounds = NativeBounds(); List<Rect> areas = new List<Rect>(); foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens) areas.Add(Area(screen));
            if (areas.Count == 0) return; Rect next = HudPlacement.Clamp(bounds, areas[HudPlacement.BestScreen(bounds, areas)]); MoveNative(next.Left, next.Top);
        }
        private static Rect Area(System.Windows.Forms.Screen screen) { System.Drawing.Rectangle r = screen.WorkingArea; return new Rect(r.Left, r.Top, r.Width, r.Height); }
        private static double ScreenScale(Rect area)
        {
            try { uint x, y; NativePoint point = new NativePoint { X = (int)(area.Left + area.Width / 2), Y = (int)(area.Top + area.Height / 2) }; if (GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out x, out y) == 0 && x > 0) return x / 96.0; } catch { }
            return 1;
        }
        private Rect NativeBounds() { NativeRect r; IntPtr hwnd = new WindowInteropHelper(this).Handle; return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out r) ? new Rect(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top)) : new Rect(0, 0, 250, 56); }
        private static Point CursorPoint() { NativePoint p; return GetCursorPos(out p) ? new Point(p.X, p.Y) : new Point(); }
        private void MoveNative(double x, double y) { IntPtr h = new WindowInteropHelper(this).Handle; if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, 0x0015); }
        private long AgeSeconds() { return _data.TimestampMs > 0 ? Math.Max(0, (UsageTiming.EpochMs(_clockUtc) - _data.TimestampMs) / 1000) : long.MaxValue; }
        private string Ago(long age) { if (age < 10) return "刚刚"; if (age < 60) return age + " 秒前"; if (age < 3600) return (age / 60) + " 分钟前"; return age < 86400 ? (age / 3600) + " 小时前" : (age / 86400) + " 天前"; }
        private string Money(double usd) { return FormatterUtil.FormatMoney(usd, _data.Cny, _data.Rate); }
        internal static string CompactTokens(long n) { if (n < 1000) return n.ToString(CultureInfo.InvariantCulture); if (n < 1000000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"; return (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M"; }
        private TextBlock Text(string value, double size, bool strong) { TextBlock t = new TextBlock { Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center }; t.SetResourceReference(TextBlock.ForegroundProperty, strong ? "FgPrimary" : "FgSecondary"); return t; }
        private TextBlock Line(Panel panel, string label)
        {
            Grid grid = new Grid { Margin = new Thickness(0, 5, 0, 0) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Text(label, 11, false)); TextBlock value = Text("—", 11, true); value.MaxWidth = 190; value.TextWrapping = TextWrapping.Wrap; value.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(value, 1); grid.Children.Add(value); panel.Children.Add(grid); return value;
        }
        private Button Button(string label, Action action) { Button b = ModernControlsFactory.CreateButton(label, "secondary"); b.Height = 30; b.Margin = new Thickness(0, 0, 6, 0); b.Click += delegate { action(); }; return b; }
    }
}
