using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TokLens
{
    /// <summary>
    /// 桌面迷你胶囊：只看本机请求与本机费用，不受主窗口筛选影响。
    /// 胶囊只放三样：上下文环 + 活动点、今日估值、近 1 小时的 12 根微柱（每根 5 分钟）。
    /// 鼠标停留展开卡片（模型、上下文、近 2 小时、最近请求），单击固定，双击打开主窗口，右键设置。
    /// 视觉上只用单色层级，强调色只出现在最新一根柱子；上下文超过 120K 时环变成琥珀色。
    /// </summary>
    public sealed class MiniHudWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int size);

        private const double CapsuleHeight = 32;
        private const double GlyphSize = 18;
        private const int MicroBars = 12;
        private const double MicroBarWidth = 3, MicroBarGap = 2, MicroBarHeight = 14;
        private const double CardWidth = 272;
        private const int CardBars = 24;
        private const double CardBarWidth = 7, CardBarGap = 3, CardBarHeight = 30;
        private const long ContextWarn = 120000;          // 与会话分析的 /compact 建议一致
        private const double RingFullTokens = 200000;
        private const int IdleSeconds = 300;
        private const double IdleOpacity = 0.55;
        private const double SnapDistance = 24, SnapMargin = 8;

        private readonly Action _restore, _refresh;
        private readonly Grid _root;
        private readonly Border _capsule, _cardShell;
        private readonly Path _glyphTrack, _glyphArc;
        private readonly Ellipse _liveDot;
        private readonly TextBlock _amount;
        private readonly Canvas _bars;
        private readonly Popup _cardPopup;
        private readonly TextBlock _cardModel, _cardAgo, _cardTool, _cardToday, _cardHour, _cardContext, _cardCache, _cardFoot;
        private readonly Canvas _cardChart;
        private readonly StackPanel _recentList;
        private readonly DispatcherTimer _tick, _hoverOpen, _hoverClose;
        private readonly List<Border> _dividers = new List<Border>();

        private HudTelemetry _data = new HudTelemetry();
        private long _lastSeenTs;
        private int _pulses;
        private bool _dark, _locked, _pinned, _idle, _armed, _dragged, _breathing;
        private bool _overCapsule, _overCard;   // 用进出事件记录悬停，比 IsMouseOver 更可靠（窗口从未激活时也成立）
        private double _userOpacity = 1;
        private Point _down;

        public MiniHudWindow(bool isDark, Action onRestoreMainWindow, Action onRefreshRequested)
        {
            _restore = onRestoreMainWindow;
            _refresh = onRefreshRequested;
            Title = "TokLens · 悬浮条";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = DesignSystem.FontUi;

            // 只留一点点阴影空间，窗口几乎就是胶囊本身
            _root = new Grid { Margin = new Thickness(8) };
            Content = _root;
            _capsule = new Border
            {
                Height = CapsuleHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(CapsuleHeight / 2),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 0, 13, 0),
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 1.5, Direction = 270, Opacity = 0.18 }
            };
            System.Windows.Automation.AutomationProperties.SetName(_capsule, "本机用量悬浮条");
            _root.Children.Add(_capsule);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _capsule.Child = row;

            // 上下文环 + 活动点
            Grid glyph = new Grid { Width = GlyphSize, Height = GlyphSize, VerticalAlignment = VerticalAlignment.Center };
            double r = GlyphSize / 2 - 1.5, c = GlyphSize / 2;
            _glyphTrack = new Path { StrokeThickness = 1.6, Data = new EllipseGeometry(new Point(c, c), r, r) };
            _glyphArc = new Path { StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _liveDot = new Ellipse { Width = 5, Height = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.Children.Add(_glyphTrack);
            glyph.Children.Add(_glyphArc);
            glyph.Children.Add(_liveDot);
            row.Children.Add(glyph);

            // 今日估值：货币符号小一号、浅一级
            _amount = new TextBlock { Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center, FontFamily = DesignSystem.FontUi };
            ModernControlsFactory.EnableTabularNumbers(_amount, false);
            row.Children.Add(_amount);

            // 近 1 小时 12 根微柱
            _bars = new Canvas
            {
                Width = MicroBars * MicroBarWidth + (MicroBars - 1) * MicroBarGap,
                Height = MicroBarHeight,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_bars);

            // ---------------------------------------------------------------- 悬停卡片
            Grid cardWrap = new Grid { Margin = new Thickness(8) };
            _cardShell = new Border
            {
                Width = CardWidth,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.2 }
            };
            cardWrap.Children.Add(_cardShell);
            StackPanel card = new StackPanel();
            _cardShell.Child = card;

            DockPanel head = new DockPanel();
            _cardAgo = Txt("", DesignSystem.TextCaption, "FgMuted");
            DockPanel.SetDock(_cardAgo, Dock.Right);
            head.Children.Add(_cardAgo);
            _cardModel = Txt("等待请求", DesignSystem.TextLabel, "FgPrimary");
            _cardModel.FontWeight = FontWeights.SemiBold;
            _cardModel.TextTrimming = TextTrimming.CharacterEllipsis;
            _cardModel.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(_cardModel);
            card.Children.Add(head);
            _cardTool = Txt("", DesignSystem.TextCaption, "FgMuted");
            _cardTool.Margin = new Thickness(0, 2, 0, 0);
            _cardTool.TextTrimming = TextTrimming.CharacterEllipsis;
            card.Children.Add(_cardTool);
            card.Children.Add(Divider());

            Grid kv = new Grid();
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kv.RowDefinitions.Add(new RowDefinition());
            kv.RowDefinitions.Add(new RowDefinition());
            _cardToday = Metric(kv, "今日估值", 0, 0);
            _cardHour = Metric(kv, "近 1 小时", 0, 1);
            _cardContext = Metric(kv, "上下文", 1, 0);
            _cardCache = Metric(kv, "缓存命中", 1, 1);
            card.Children.Add(kv);
            card.Children.Add(Divider());

            DockPanel chartHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            TextBlock perBar = Txt("每格 5 分钟", DesignSystem.TextCaption, "FgMuted");
            DockPanel.SetDock(perBar, Dock.Right);
            chartHead.Children.Add(perBar);
            chartHead.Children.Add(Txt("近 2 小时", DesignSystem.TextCaption, "FgMuted"));
            card.Children.Add(chartHead);
            _cardChart = new Canvas { Width = CardBars * CardBarWidth + (CardBars - 1) * CardBarGap, Height = CardBarHeight, HorizontalAlignment = HorizontalAlignment.Left };
            card.Children.Add(_cardChart);
            card.Children.Add(Divider());

            _recentList = new StackPanel();
            card.Children.Add(_recentList);

            DockPanel foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            TextBlock open = Txt("打开主窗口", DesignSystem.TextCaption, "AccentText");
            open.Cursor = Cursors.Hand;
            open.MouseLeftButtonUp += delegate { CloseCard(); if (_restore != null) _restore(); };
            DockPanel.SetDock(open, Dock.Right);
            foot.Children.Add(open);
            _cardFoot = Txt("", DesignSystem.TextCaption, "FgMuted");
            _cardFoot.TextWrapping = TextWrapping.Wrap;
            _cardFoot.Margin = new Thickness(0, 0, 10, 0);
            foot.Children.Add(_cardFoot);
            card.Children.Add(foot);

            _cardPopup = new Popup { PlacementTarget = _capsule, Placement = PlacementMode.Bottom, VerticalOffset = 6, AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None, Child = cardWrap };
            _cardPopup.Opened += delegate { RenderCard(); };

            // ---------------------------------------------------------------- 交互
            _hoverOpen = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _hoverOpen.Tick += delegate { _hoverOpen.Stop(); if (_overCapsule && !_dragged) OpenCard(); };
            _hoverClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverClose.Tick += delegate
            {
                _hoverClose.Stop();
                if (!_pinned && !_overCapsule && !_overCard) CloseCard();
                ApplyIdle();
            };
            _capsule.MouseEnter += delegate { _overCapsule = true; _hoverClose.Stop(); _hoverOpen.Start(); ApplyIdle(); };
            _capsule.MouseLeave += delegate { _overCapsule = false; _hoverOpen.Stop(); _hoverClose.Start(); };
            cardWrap.MouseEnter += delegate { _overCard = true; _hoverClose.Stop(); };
            cardWrap.MouseLeave += delegate { _overCard = false; _hoverClose.Start(); };

            _capsule.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                _dragged = false;
                _armed = true;
                _down = e.GetPosition(this);
                if (e.ClickCount == 2)
                {
                    _armed = false;
                    _dragged = true;
                    CloseCard();
                    if (_restore != null) _restore();
                    e.Handled = true;
                }
            };
            _capsule.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_armed || _locked || e.LeftButton != MouseButtonState.Pressed) return;
                Point pos = e.GetPosition(this);
                if (Math.Abs(pos.X - _down.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _armed = false;
                _dragged = true;
                _hoverOpen.Stop();
                CloseCard();
                try { DragMove(); SnapToEdges(); SavePosition(); } catch (InvalidOperationException) { }
            };
            _capsule.MouseLeftButtonUp += delegate
            {
                if (_armed && !_dragged) TogglePinned();
                _armed = false;
            };
            BuildMenu();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _tick.Tick += delegate { Render(); if (_cardPopup.IsOpen) RenderCard(); };
            IsVisibleChanged += delegate { if (IsVisible) { Render(); _tick.Start(); } else { _tick.Stop(); CloseCard(); } };
            Closed += delegate { _tick.Stop(); _hoverOpen.Stop(); _hoverClose.Stop(); _cardPopup.IsOpen = false; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { CloseCard(); e.Handled = true; }
                if (e.Key == Key.System && e.SystemKey == Key.F4) { Hide(); e.Handled = true; }
            };
            SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80);   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                try { int corner = 1, none = unchecked((int)0xfffffffe); DwmSetWindowAttribute(h, 33, ref corner, 4); DwmSetWindowAttribute(h, 34, ref none, 4); } catch { }
            };
            Loaded += delegate { RestorePosition(); };

            SetTheme(isDark);
        }

        // ------------------------------------------------------------------ 公共接口

        public void SetTheme(bool dark)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { SetTheme(dark); })); return; }
            _dark = dark;
            DesignSystem.ApplyTheme(this, dark);

            // 单色磨砂：主题底色略透明，发丝边框用前景色的 10%
            Color surface = DesignSystem.ThemeColor("BgElevated", dark);
            Color ink = dark ? Colors.White : Colors.Black;
            Brush glass = new SolidColorBrush(Color.FromArgb(dark ? (byte)0xF6 : (byte)0xFA, surface.R, surface.G, surface.B));
            Brush hairline = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x1F : (byte)0x14, ink.R, ink.G, ink.B));
            _capsule.Background = glass;
            _capsule.BorderBrush = hairline;
            // 卡片完全不透明：信息密度高，透出桌面会影响阅读
            _cardShell.Background = new SolidColorBrush(surface);
            _cardShell.BorderBrush = hairline;
            foreach (Border d in _dividers) d.Background = hairline;
            _glyphTrack.Stroke = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x33 : (byte)0x26, ink.R, ink.G, ink.B));
            ((DropShadowEffect)_capsule.Effect).Opacity = dark ? 0.35 : 0.14;
            ((DropShadowEffect)_cardShell.Effect).Opacity = dark ? 0.4 : 0.16;
            Render();
            if (_cardPopup.IsOpen) RenderCard();
        }

        public void UpdateTelemetry(HudTelemetry telemetry)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { UpdateTelemetry(telemetry); })); return; }
            _data = telemetry ?? new HudTelemetry();
            bool isNew = _lastSeenTs > 0 && _data.TimestampMs > _lastSeenTs;
            if (_data.TimestampMs > _lastSeenTs) _lastSeenTs = _data.TimestampMs;
            Render();
            if (_cardPopup.IsOpen) RenderCard();
            if (isNew) Pulse();
        }

        // ------------------------------------------------------------------ 胶囊

        private void Render()
        {
            long age = AgeSeconds();
            bool live = _data.TimestampMs > 0 && age <= 60;
            _idle = _data.TimestampMs <= 0 || age > IdleSeconds;
            SetLive(live);
            RenderGlyph(_data.TimestampMs > 0 ? _data.ContextTokens : 0);
            RenderAmount();
            RenderBars(_bars, Tail(_data.SparkTwoHours, MicroBars), MicroBarWidth, MicroBarGap, MicroBarHeight, false);
            // 不用提示气泡：悬停卡片已经给出全部信息，两者叠在一起反而挡视线
            System.Windows.Automation.AutomationProperties.SetHelpText(_capsule, BuildTooltip(age));
            ApplyIdle();
        }

        private void RenderAmount()
        {
            string money = Money(_data.TodayUsd);
            _amount.Inlines.Clear();
            Run symbol = new Run(money.Substring(0, 1)) { FontSize = DesignSystem.TextBody };
            symbol.SetResourceReference(TextElement.ForegroundProperty, "FgMuted");
            Run value = new Run(money.Substring(1)) { FontSize = DesignSystem.TextTitle, FontWeight = FontWeights.SemiBold };
            value.SetResourceReference(TextElement.ForegroundProperty, "FgPrimary");
            _amount.Inlines.Add(symbol);
            _amount.Inlines.Add(value);
            if (_data.UnpricedRequests > 0)
            {
                Run star = new Run("*") { FontSize = DesignSystem.TextBody };
                star.SetResourceReference(TextElement.ForegroundProperty, "FgMuted");
                _amount.Inlines.Add(star);
            }
        }

        private void RenderGlyph(long ctx)
        {
            double r = GlyphSize / 2 - 1.5, c = GlyphSize / 2;
            if (ctx <= 0) { _glyphArc.Data = null; return; }
            _glyphArc.SetResourceReference(Shape.StrokeProperty, ctx >= ContextWarn ? "SemanticWarning" : "FgSecondary");
            double fraction = Math.Min(1.0, ctx / RingFullTokens);
            if (fraction >= 0.999)
            {
                _glyphArc.Data = new EllipseGeometry(new Point(c, c), r, r);
                return;
            }
            double angle = Math.Max(0.04, fraction) * 2 * Math.PI;
            PathFigure fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)), new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            PathGeometry geo = new PathGeometry();
            geo.Figures.Add(fig);
            _glyphArc.Data = geo;
        }

        // 柱子一律中性灰，只有最近一格用强调色
        private void RenderBars(Canvas canvas, double[] values, double width, double gap, double height, bool withTooltips)
        {
            canvas.Children.Clear();
            int n = values.Length;
            double max = 0;
            foreach (double v in values) max = Math.Max(max, v);
            Color ink = _dark ? Colors.White : Colors.Black;
            Brush idle = new SolidColorBrush(Color.FromArgb(_dark ? (byte)0x2E : (byte)0x1F, ink.R, ink.G, ink.B));
            Brush used = new SolidColorBrush(Color.FromArgb(_dark ? (byte)0x8C : (byte)0x73, ink.R, ink.G, ink.B));
            DateTime start = DateTime.Now.AddMinutes(-5 * n);
            for (int i = 0; i < n; i++)
            {
                bool has = values[i] > 0 && max > 0;
                double h = has ? Math.Max(3, values[i] / max * height) : 2;
                Rectangle bar = new Rectangle { Width = width, Height = h, RadiusX = width / 2, RadiusY = width / 2 };
                if (has && i == n - 1) bar.SetResourceReference(Shape.FillProperty, "AccentText");
                else bar.Fill = has ? used : idle;
                Canvas.SetLeft(bar, i * (width + gap));
                Canvas.SetTop(bar, height - h);
                if (withTooltips)
                {
                    DateTime from = start.AddMinutes(5 * i);
                    bar.ToolTip = from.ToString("HH:mm", CultureInfo.InvariantCulture) + "–" + from.AddMinutes(5).ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + Money(values[i]);
                }
                canvas.Children.Add(bar);
            }
        }

        private string BuildTooltip(long age)
        {
            if (_data.TimestampMs <= 0) return "本机今日估值 " + Money(_data.TodayUsd) + " · 暂无请求";
            return HudTelemetry.ShortModel(_data.Model) + " · " + Ago(age)
                + "\n今日估值 " + Money(_data.TodayUsd) + " · 近 1 小时 " + Money(_data.LastHourUsd)
                + "\n上下文 " + FormatterUtil.FormatTokens(_data.ContextTokens) + " tokens";
        }

        // ------------------------------------------------------------------ 卡片

        private void RenderCard()
        {
            long age = AgeSeconds();
            bool has = _data.TimestampMs > 0;
            _cardModel.Text = has ? HudTelemetry.ShortModel(_data.Model) : "等待请求";
            _cardModel.ToolTip = has ? _data.Model : null;
            _cardAgo.Text = has ? Ago(age) : "";
            _cardTool.Text = has ? "本机 · " + HudTelemetry.ShortTool(_data.Tool) : "本机暂无请求";

            _cardToday.Text = Money(_data.TodayUsd) + (_data.UnpricedRequests > 0 ? "*" : "");
            _cardHour.Text = Money(_data.LastHourUsd);
            _cardContext.Text = has && _data.ContextTokens > 0 ? FormatterUtil.FormatTokens(_data.ContextTokens) : "—";
            _cardContext.SetResourceReference(TextBlock.ForegroundProperty, has && _data.ContextTokens >= ContextWarn ? "SemanticWarning" : "FgPrimary");
            _cardContext.ToolTip = has && _data.ContextTokens >= ContextWarn ? "最近一次请求的上下文超过 120K，可以考虑 /compact" : "最近一次请求的上下文（输入 + 缓存读写）";
            _cardCache.Text = _data.CacheHitRate >= 0 ? (_data.CacheHitRate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

            RenderBars(_cardChart, Tail(_data.SparkTwoHours, CardBars), CardBarWidth, CardBarGap, CardBarHeight, true);

            _recentList.Children.Clear();
            if (_data.Latest == null || _data.Latest.Count == 0)
                _recentList.Children.Add(Txt("今天还没有请求", DesignSystem.TextCaption, "FgMuted"));
            else
                foreach (RecentRequestRecord rq in _data.Latest) _recentList.Children.Add(RecentRow(rq));

            List<string> notes = new List<string>();
            if (has)
            {
                double tps = _data.Tps;
                notes.Add(tps > 0 ? "最近一次 " + FormatterUtil.FormatDuration(_data.DurationMs) + " · " + FormatterUtil.FormatTps(tps) : "日志没有提供计时");
            }
            if (_data.UnpricedRequests > 0) notes.Add("* " + _data.UnpricedRequests + " 次未定价");
            _cardFoot.Text = string.Join(" · ", notes.ToArray());
            _cardToday.ToolTip = "按 API 单价估算，不是实付";
        }

        private UIElement RecentRow(RecentRequestRecord rq)
        {
            Grid g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            DateTime at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(rq.TimestampMs).ToLocalTime();
            TextBlock time = Txt(at.ToString("HH:mm", CultureInfo.InvariantCulture), DesignSystem.TextCaption, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(time, false);
            g.Children.Add(time);
            TextBlock model = Txt(HudTelemetry.ShortModel(rq.Model), DesignSystem.TextCaption, "FgSecondary");
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            model.ToolTip = (rq.Tool ?? "") + " · " + (rq.Model ?? "") + " · " + at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Grid.SetColumn(model, 1);
            g.Children.Add(model);
            TextBlock tokens = Txt(FormatterUtil.FormatTokens(rq.TotalTokens), DesignSystem.TextCaption, "FgMuted");
            tokens.HorizontalAlignment = HorizontalAlignment.Right;
            ModernControlsFactory.EnableTabularNumbers(tokens, false);
            Grid.SetColumn(tokens, 2);
            g.Children.Add(tokens);
            TextBlock cost = Txt(rq.CostUsd > 0 ? Money(rq.CostUsd) : "—", DesignSystem.TextCaption, "FgPrimary");
            cost.HorizontalAlignment = HorizontalAlignment.Right;
            ModernControlsFactory.EnableTabularNumbers(cost, false);
            Grid.SetColumn(cost, 3);
            g.Children.Add(cost);
            return g;
        }

        private TextBlock Metric(Grid grid, string label, int rowIndex, int column)
        {
            StackPanel sp = new StackPanel { Margin = new Thickness(0, rowIndex == 0 ? 0 : 8, 0, 0) };
            sp.Children.Add(Txt(label, DesignSystem.TextCaption, "FgMuted"));
            TextBlock value = Txt("—", DesignSystem.TextLabel, "FgPrimary");
            value.FontWeight = FontWeights.SemiBold;
            value.Margin = new Thickness(0, 2, 0, 0);
            ModernControlsFactory.EnableTabularNumbers(value, false);
            sp.Children.Add(value);
            Grid.SetRow(sp, rowIndex);
            Grid.SetColumn(sp, column);
            grid.Children.Add(sp);
            return value;
        }

        private Border Divider()
        {
            Border d = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
            _dividers.Add(d);
            return d;
        }

        private void OpenCard()
        {
            if (_cardPopup.IsOpen) return;
            // 胶囊在屏幕右半边时卡片右对齐，避免伸出屏幕
            Rect area = SystemParameters.WorkArea;
            bool rightHalf = Left + ActualWidth / 2 > area.Left + area.Width / 2;
            _cardPopup.HorizontalOffset = rightHalf ? _capsule.ActualWidth - CardWidth : 0;
            _cardPopup.IsOpen = true;
            ApplyIdle();
        }

        private void CloseCard()
        {
            _pinned = false;
            _cardPopup.IsOpen = false;
            ApplyIdle();
        }

        private void TogglePinned()
        {
            if (_cardPopup.IsOpen && _pinned) { CloseCard(); return; }
            _pinned = true;
            OpenCard();
        }

        // ------------------------------------------------------------------ 动效

        // 新请求：数字闪一下
        private void Pulse()
        {
            _pulses++;
            DoubleAnimationUsingKeyFrames flash = new DoubleAnimationUsingKeyFrames();
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            _amount.BeginAnimation(OpacityProperty, flash);
        }

        private void SetLive(bool live)
        {
            _liveDot.SetResourceReference(Shape.FillProperty, live ? "SemanticSuccess" : "FgMuted");
            if (live && !_breathing)
                _liveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(1100)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else if (!live && _breathing)
            {
                _liveDot.BeginAnimation(OpacityProperty, null);
                _liveDot.Opacity = 0.7;
            }
            _breathing = live;
        }

        // 5 分钟没有请求时淡下去；鼠标经过或卡片打开时恢复
        private void ApplyIdle()
        {
            bool dim = _idle && !_overCapsule && !_cardPopup.IsOpen;
            double target = _userOpacity * (dim ? IdleOpacity : 1);
            if (Math.Abs(_root.Opacity - target) < 0.01) return;
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(260)));
        }

        // ------------------------------------------------------------------ 位置：吸附屏幕边缘并记住

        private Rect CurrentWorkArea()
        {
            Rect area = SystemParameters.WorkArea;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                System.Drawing.Rectangle pixels = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
                Matrix m = source.CompositionTarget.TransformFromDevice;
                area = new Rect(m.Transform(new Point(pixels.Left, pixels.Top)), m.Transform(new Point(pixels.Right, pixels.Bottom)));
            }
            return area;
        }

        // 胶囊外有 8px 阴影留白：贴边时让胶囊本身离屏幕边 SnapMargin
        private void SnapToEdges()
        {
            Rect a = CurrentWorkArea();
            double pad = _root.Margin.Left;
            double left = Left, top = Top, w = ActualWidth, h = ActualHeight;
            if (left + pad - a.Left < SnapDistance) left = a.Left + SnapMargin - pad;
            if (a.Right - (left + w - pad) < SnapDistance) left = a.Right - w - SnapMargin + pad;
            if (top + pad - a.Top < SnapDistance) top = a.Top + SnapMargin - pad;
            if (a.Bottom - (top + h - pad) < SnapDistance) top = a.Bottom - h - SnapMargin + pad;
            Left = Math.Max(a.Left - pad, Math.Min(left, a.Right - w + pad));
            Top = Math.Max(a.Top - pad, Math.Min(top, a.Bottom - h + pad));
        }

        private static string PositionFile
        {
            get { return System.IO.Path.Combine(SyncService.AppConfigDir, "hud_position.txt"); }
        }

        private void SavePosition()
        {
            try
            {
                System.IO.Directory.CreateDirectory(SyncService.AppConfigDir);
                System.IO.File.WriteAllText(PositionFile, Left.ToString("0.#", CultureInfo.InvariantCulture) + "," + Top.ToString("0.#", CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private void RestorePosition()
        {
            Rect a = CurrentWorkArea();
            double pad = _root.Margin.Left;
            double left = a.Right - ActualWidth - SnapMargin + pad, top = a.Top + SnapMargin - pad;
            try
            {
                if (System.IO.File.Exists(PositionFile))
                {
                    string[] parts = System.IO.File.ReadAllText(PositionFile).Trim().Split(',');
                    double l, t;
                    if (parts.Length == 2
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out l)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t)
                        && l > a.Left - ActualWidth / 2 && l < a.Right - ActualWidth / 2
                        && t > a.Top - ActualHeight / 2 && t < a.Bottom - ActualHeight / 2)
                    {
                        left = l;
                        top = t;
                    }
                }
            }
            catch { }
            Left = left;
            Top = top;
        }

        // ------------------------------------------------------------------ 小工具

        private TextBlock Txt(string value, double size, string brushKey)
        {
            TextBlock t = new TextBlock { Text = value, FontSize = size, FontFamily = DesignSystem.FontUi, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        private string Money(double usd)
        {
            return FormatterUtil.FormatMoney(usd, _data.Cny, _data.Rate);
        }

        private static double[] Tail(double[] values, int count)
        {
            double[] result = new double[count];
            if (values == null) return result;
            int offset = values.Length - count;
            for (int i = 0; i < count; i++)
            {
                int src = offset + i;
                if (src >= 0 && src < values.Length) result[i] = values[src];
            }
            return result;
        }

        private long AgeSeconds()
        {
            if (_data.TimestampMs <= 0) return long.MaxValue;
            double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return (long)Math.Max(0, (nowMs - _data.TimestampMs) / 1000);
        }

        private string Ago(long seconds)
        {
            if (seconds < 10) return "刚刚";
            if (seconds < 60) return seconds + " 秒前";
            if (seconds < 3600) return (seconds / 60) + " 分钟前";
            DateTime at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(_data.TimestampMs).ToLocalTime();
            return at.Date == DateTime.Today ? at.ToString("HH:mm", CultureInfo.InvariantCulture) : at.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem locked = new MenuItem { Header = "锁定位置", IsCheckable = true };
            locked.Click += delegate { _locked = locked.IsChecked; };
            menu.Items.Add(locked);
            MenuItem opacity = new MenuItem { Header = "透明度" };
            foreach (double n in new double[] { 1, .85, .65 })
            {
                double value = n;
                MenuItem item = new MenuItem { Header = (n * 100).ToString("0") + "%" };
                item.Click += delegate { _userOpacity = value; ApplyIdle(); };
                opacity.Items.Add(item);
            }
            menu.Items.Add(opacity);
            MenuItem refresh = new MenuItem { Header = "重新扫描" };
            refresh.Click += delegate { if (_refresh != null) _refresh(); };
            menu.Items.Add(refresh);
            MenuItem show = new MenuItem { Header = "打开主窗口" };
            show.Click += delegate { if (_restore != null) _restore(); };
            menu.Items.Add(show);
            MenuItem hide = new MenuItem { Header = "隐藏悬浮条 (Ctrl+M)" };
            hide.Click += delegate { Hide(); };
            menu.Items.Add(hide);
            _capsule.ContextMenu = menu;
            menu.Opened += delegate { _hoverOpen.Stop(); CloseCard(); };
        }
    }
}
