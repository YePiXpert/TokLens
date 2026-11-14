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
    /// 桌面悬浮球（玻璃光环）：只看本机请求，不受主窗口筛选影响。
    /// 外圈永远是上下文占用（最近一次请求的上下文 ÷ 该模型上限），中间数字单击可在
    /// 上下文 / 今日估值 / 速率之间切换。运行中光环呼吸，空闲 5 分钟变灰变淡，Agent 失控时变红并显示「!」。
    /// 鼠标停留展开详情卡片，双击打开主窗口，右键设置；拖到屏幕左右边缘会缩进一半，悬停滑出。
    /// </summary>
    public sealed class MiniHudWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int size);

        private const double BallSize = 58, RingRadius = 22.5, RingStroke = 3;
        private const double GlowRoom = 18;              // 球外留给光晕的空间
        private const double CardWidth = 272;
        private const int CardBars = 24;
        private const double CardBarWidth = 7, CardBarGap = 3, CardBarHeight = 30;
        private const double WarnFraction = 0.8, DangerFraction = 0.95;
        private const int IdleSeconds = 300;
        private const double IdleOpacity = 0.75;
        private const double SnapDistance = 28, EdgeGap = 6;

        private const int ModeContext = 0, ModeToday = 1, ModeRate = 2;
        private static readonly string[] ModeNames = { "上下文", "今日估值", "速率" };

        private enum Dock { None, Left, Right }

        private readonly Action _restore, _refresh;
        private readonly Grid _root, _ball;
        private readonly Ellipse _orb;
        private readonly DropShadowEffect _orbEffect;
        private readonly Path _arc;
        private readonly Ellipse _track;
        private readonly TextBlock _prefix, _value, _suffix, _modeLabel;
        private readonly Border _cardShell;
        private readonly Popup _cardPopup;
        private readonly TextBlock _cardAlert, _cardModel, _cardAgo, _cardTool, _cardToday, _cardHour, _cardContext, _cardCache, _cardFoot;
        private readonly Canvas _cardChart;
        private readonly StackPanel _recentList;
        private readonly DispatcherTimer _tick, _hoverOpen, _hoverClose, _tuck;
        private readonly List<Border> _dividers = new List<Border>();
        private readonly List<MenuItem> _modeItems = new List<MenuItem>();

        private HudTelemetry _data = new HudTelemetry();
        private long _lastSeenTs;
        private int _pulses, _mode = ModeContext;
        private string _glowState = "";
        private bool _dark, _locked, _idle, _armed, _dragged, _tucked;
        private bool _overBall, _overCard;
        private double _userOpacity = 1;
        private Dock _dock = Dock.None;
        private Point _down;

        public MiniHudWindow(bool isDark, Action onRestoreMainWindow, Action onRefreshRequested)
        {
            _restore = onRestoreMainWindow;
            _refresh = onRefreshRequested;
            Title = "TokLens · 悬浮球";
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

            _root = new Grid { Margin = new Thickness(GlowRoom) };
            Content = _root;

            // ---------------------------------------------------------------- 球体
            // 球固定在窗口左上角：系统对窗口有最小宽度，窗口可能比内容宽，多出的部分在右侧且完全透明
            _ball = new Grid { Width = BallSize, Height = BallSize, Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            System.Windows.Automation.AutomationProperties.SetName(_ball, "本机用量悬浮球");
            _orbEffect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 };
            _orb = new Ellipse { StrokeThickness = 1, Effect = _orbEffect };
            _ball.Children.Add(_orb);
            _track = new Ellipse { Width = RingRadius * 2, Height = RingRadius * 2, StrokeThickness = RingStroke, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _ball.Children.Add(_track);
            _arc = new Path { StrokeThickness = RingStroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = BallSize, Height = BallSize };
            _ball.Children.Add(_arc);

            StackPanel center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            StackPanel line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _prefix = BallText(10.5, false);
            _prefix.VerticalAlignment = VerticalAlignment.Top;
            _prefix.Margin = new Thickness(0, 3, 1, 0);
            _value = BallText(18, true);
            _suffix = BallText(10.5, false);
            _suffix.VerticalAlignment = VerticalAlignment.Bottom;
            _suffix.Margin = new Thickness(1, 0, 0, 3);
            line.Children.Add(_prefix);
            line.Children.Add(_value);
            line.Children.Add(_suffix);
            // 数字过长时整体缩小，始终留在光环内
            center.Children.Add(new Viewbox { Child = line, MaxWidth = (RingRadius - RingStroke) * 2 - 2, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
            _modeLabel = BallText(9.5, false);
            _modeLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _modeLabel.Opacity = 0;
            _modeLabel.Visibility = Visibility.Collapsed;
            center.Children.Add(_modeLabel);
            _ball.Children.Add(center);
            _root.Children.Add(_ball);

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

            _cardAlert = Txt("", DesignSystem.TextCaption, "SemanticDanger");
            _cardAlert.TextWrapping = TextWrapping.Wrap;
            _cardAlert.FontWeight = FontWeights.SemiBold;
            _cardAlert.Margin = new Thickness(0, 0, 0, 8);
            _cardAlert.Visibility = Visibility.Collapsed;
            card.Children.Add(_cardAlert);

            DockPanel head = new DockPanel();
            _cardAgo = Txt("", DesignSystem.TextCaption, "FgMuted");
            DockPanel.SetDock(_cardAgo, System.Windows.Controls.Dock.Right);
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
            _cardContext = Metric(kv, "上下文", 0, 0);
            _cardToday = Metric(kv, "今日估值", 0, 1);
            _cardHour = Metric(kv, "速率（近 15 分钟）", 1, 0);
            _cardCache = Metric(kv, "缓存命中", 1, 1);
            card.Children.Add(kv);
            card.Children.Add(Divider());

            DockPanel chartHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            TextBlock perBar = Txt("每格 5 分钟", DesignSystem.TextCaption, "FgMuted");
            DockPanel.SetDock(perBar, System.Windows.Controls.Dock.Right);
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
            DockPanel.SetDock(open, System.Windows.Controls.Dock.Right);
            foot.Children.Add(open);
            _cardFoot = Txt("", DesignSystem.TextCaption, "FgMuted");
            _cardFoot.TextWrapping = TextWrapping.Wrap;
            _cardFoot.Margin = new Thickness(0, 0, 10, 0);
            foot.Children.Add(_cardFoot);
            card.Children.Add(foot);

            _cardPopup = new Popup { PlacementTarget = _ball, Placement = PlacementMode.Bottom, VerticalOffset = 8, AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None, Child = cardWrap };
            _cardPopup.Opened += delegate { RenderCard(); };

            // ---------------------------------------------------------------- 交互
            _hoverOpen = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _hoverOpen.Tick += delegate { _hoverOpen.Stop(); if (_overBall && !_dragged) OpenCard(); };
            _hoverClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverClose.Tick += delegate
            {
                _hoverClose.Stop();
                if (!_overBall && !_overCard) { CloseCard(); _tuck.Start(); }
                ApplyIdle();
            };
            _tuck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _tuck.Tick += delegate { _tuck.Stop(); _tuck.Interval = TimeSpan.FromMilliseconds(1200); if (!_overBall && !_overCard && !_cardPopup.IsOpen) SetTucked(true); };

            _ball.MouseEnter += delegate { _overBall = true; _tuck.Stop(); _hoverClose.Stop(); SetTucked(false); _hoverOpen.Start(); ApplyIdle(); };
            _ball.MouseLeave += delegate { _overBall = false; _hoverOpen.Stop(); _hoverClose.Start(); };
            cardWrap.MouseEnter += delegate { _overCard = true; _hoverClose.Stop(); _tuck.Stop(); };
            cardWrap.MouseLeave += delegate { _overCard = false; _hoverClose.Start(); };

            _ball.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
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
            _ball.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_armed || _locked || e.LeftButton != MouseButtonState.Pressed) return;
                Point pos = e.GetPosition(this);
                if (Math.Abs(pos.X - _down.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _armed = false;
                _dragged = true;
                _hoverOpen.Stop();
                CloseCard();
                BeginAnimation(LeftProperty, null);
                try { DragMove(); SnapToEdges(); SavePosition(); } catch (InvalidOperationException) { }
            };
            _ball.MouseLeftButtonUp += delegate
            {
                if (_armed && !_dragged) CycleMode();
                _armed = false;
            };
            BuildMenu();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _tick.Tick += delegate { Render(); if (_cardPopup.IsOpen) RenderCard(); };
            IsVisibleChanged += delegate { if (IsVisible) { Render(); _tick.Start(); } else { _tick.Stop(); CloseCard(); } };
            Closed += delegate { _tick.Stop(); _hoverOpen.Stop(); _hoverClose.Stop(); _tuck.Stop(); _cardPopup.IsOpen = false; };
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
            // 刚出现时完整显示 3 秒再缩进，让人看到它在哪
            Loaded += delegate { RestorePosition(); if (_dock != Dock.None) { _tuck.Interval = TimeSpan.FromSeconds(3); _tuck.Start(); } };

            SetTheme(isDark);
        }

        // ------------------------------------------------------------------ 公共接口

        public void SetTheme(bool dark)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { SetTheme(dark); })); return; }
            _dark = dark;
            DesignSystem.ApplyTheme(this, dark);

            // 球体：深色玻璃，左上角一点高光，边缘一圈由亮到暗的细边
            _orb.Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(DesignSystem.ThemeColor("OrbCore", dark), 0),
                new GradientStop(DesignSystem.ThemeColor("OrbMid", dark), 0.55),
                new GradientStop(DesignSystem.ThemeColor("OrbEdge", dark), 1)
            }) { GradientOrigin = new Point(0.32, 0.22), Center = new Point(0.42, 0.36), RadiusX = 0.78, RadiusY = 0.78 };
            _orb.Stroke = new LinearGradientBrush(Color.FromArgb(0x48, 255, 255, 255), Color.FromArgb(0x06, 255, 255, 255), 90);
            _track.Stroke = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));

            // 卡片跟随主题
            Color surface = DesignSystem.ThemeColor("BgElevated", dark);
            Color ink = dark ? Colors.White : Colors.Black;
            Brush hairline = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x1F : (byte)0x14, ink.R, ink.G, ink.B));
            _cardShell.Background = new SolidColorBrush(surface);
            _cardShell.BorderBrush = hairline;
            foreach (Border d in _dividers) d.Background = hairline;
            ((DropShadowEffect)_cardShell.Effect).Opacity = dark ? 0.4 : 0.16;
            _glowState = "";
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

        // ------------------------------------------------------------------ 球体

        private void Render()
        {
            long age = AgeSeconds();
            bool has = _data.TimestampMs > 0;
            bool running = has && age <= 60;
            _idle = !has || age > IdleSeconds;

            string state = _data.Alert ? "alert" : (running ? "running" : (_idle ? "idle" : "calm"));
            RenderRing(state);
            RenderCenter();
            SetGlow(state);
            System.Windows.Automation.AutomationProperties.SetHelpText(_ball, BuildSummary(age));
            ApplyIdle();
        }

        private void RenderRing(string state)
        {
            double fraction = _data.ContextFraction;
            if (state == "alert") fraction = 1;
            if (fraction < 0) { _arc.Data = null; return; }

            Brush stroke;
            if (state == "idle") stroke = new SolidColorBrush(Color.FromArgb(0x5A, 255, 255, 255));
            else
            {
                string a = "HaloStart", b = "HaloEnd";
                if (state == "alert") { a = "HaloAlertStart"; b = "HaloAlertEnd"; }
                else if (fraction >= DangerFraction) { a = "HaloAlertStart"; b = "HaloAlertEnd"; }
                else if (fraction >= WarnFraction) { a = "HaloWarnStart"; b = "HaloWarnEnd"; }
                stroke = new LinearGradientBrush(DesignSystem.ThemeColor(a, _dark), DesignSystem.ThemeColor(b, _dark), new Point(0, 0), new Point(1, 1));
            }
            _arc.Stroke = stroke;
            double c = BallSize / 2;
            if (fraction >= 0.999) { _arc.Data = new EllipseGeometry(new Point(c, c), RingRadius, RingRadius); return; }
            double angle = Math.Max(0.03, fraction) * 2 * Math.PI;
            PathFigure fig = new PathFigure { StartPoint = new Point(c, c - RingRadius), IsClosed = false };
            fig.Segments.Add(new ArcSegment(new Point(c + RingRadius * Math.Sin(angle), c - RingRadius * Math.Cos(angle)), new Size(RingRadius, RingRadius), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            PathGeometry geo = new PathGeometry();
            geo.Figures.Add(fig);
            _arc.Data = geo;
        }

        private void RenderCenter()
        {
            _value.FontSize = 18;
            _value.Foreground = Brushes.White;
            if (_data.Alert)
            {
                Set("", "!", "");
                _value.FontSize = 22;
                _value.Foreground = new SolidColorBrush(DesignSystem.ThemeColor("HaloAlertStart", _dark));
                return;
            }
            if (_mode == ModeToday)
            {
                string[] m = SplitMoney(_data.TodayUsd);
                Set(m[0], m[1] + (_data.UnpricedRequests > 0 ? "*" : ""), "");
            }
            else if (_mode == ModeRate)
            {
                string[] m = SplitMoney(_data.Last15Usd * 4);
                Set(m[0], m[1], "/h");
            }
            else
            {
                double f = _data.ContextFraction;
                if (f >= 0) Set("", ((int)Math.Round(f * 100)).ToString(CultureInfo.InvariantCulture), "%");
                else if (_data.TimestampMs > 0 && _data.ContextTokens > 0)
                {
                    string t = CompactTokens(_data.ContextTokens);
                    Set("", t.Substring(0, t.Length - 1), t.Substring(t.Length - 1));
                }
                else Set("", "—", "");
            }
        }

        private void Set(string prefix, string value, string suffix)
        {
            _prefix.Text = prefix;
            _prefix.Visibility = prefix.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _value.Text = value;
            _suffix.Text = suffix;
            _suffix.Visibility = suffix.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 光晕：运行中强调色呼吸；失控红色快速呼吸；其余是普通投影
        private void SetGlow(string state)
        {
            if (state == _glowState) return;
            _glowState = state;
            _orbEffect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            if (state == "running" || state == "alert")
            {
                bool alert = state == "alert";
                _orbEffect.Color = DesignSystem.ThemeColor(alert ? "HaloAlertStart" : "HaloStart", _dark);
                _orbEffect.ShadowDepth = 0;
                _orbEffect.BlurRadius = 24;
                _orbEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(alert ? 0.95 : 0.7, alert ? 0.45 : 0.3, TimeSpan.FromMilliseconds(alert ? 700 : 1600))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            else
            {
                _orbEffect.Color = Colors.Black;
                _orbEffect.ShadowDepth = 3;
                _orbEffect.BlurRadius = 14;
                _orbEffect.Opacity = 0.45;
            }
        }

        // 新请求：光环亮一下
        private void Pulse()
        {
            _pulses++;
            DoubleAnimationUsingKeyFrames flash = new DoubleAnimationUsingKeyFrames();
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            _arc.BeginAnimation(OpacityProperty, flash);
        }

        // 5 分钟没有请求时淡下去；鼠标经过或卡片打开时恢复
        private void ApplyIdle()
        {
            bool dim = _idle && !_data.Alert && !_overBall && !_cardPopup.IsOpen;
            double target = _userOpacity * (dim ? IdleOpacity : 1);
            if (Math.Abs(_root.Opacity - target) < 0.01) return;
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(260)));
        }

        // ------------------------------------------------------------------ 模式

        private void CycleMode()
        {
            SetMode((_mode + 1) % ModeNames.Length, true);
        }

        private void SetMode(int mode, bool announce)
        {
            _mode = mode;
            for (int i = 0; i < _modeItems.Count; i++) _modeItems[i].IsChecked = i == mode;
            RenderCenter();
            SavePosition();
            if (!announce) return;
            // 切换时在数字下方短暂显示当前内容的名称
            _modeLabel.Text = ModeNames[mode];
            _modeLabel.Visibility = Visibility.Visible;
            DoubleAnimationUsingKeyFrames show = new DoubleAnimationUsingKeyFrames();
            show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
            show.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
            show.Completed += delegate { if (_modeLabel.Opacity <= 0.01) _modeLabel.Visibility = Visibility.Collapsed; };
            _modeLabel.BeginAnimation(OpacityProperty, show);
        }

        // ------------------------------------------------------------------ 卡片

        private void RenderCard()
        {
            long age = AgeSeconds();
            bool has = _data.TimestampMs > 0;
            _cardAlert.Visibility = _data.Alert ? Visibility.Visible : Visibility.Collapsed;
            _cardAlert.Text = _data.Alert ? "! " + (string.IsNullOrEmpty(_data.AlertText) ? "Agent 消耗异常" : _data.AlertText) : "";
            _cardModel.Text = has ? HudTelemetry.ShortModel(_data.Model) : "等待请求";
            _cardModel.ToolTip = has ? _data.Model : null;
            _cardAgo.Text = has ? Ago(age) : "";
            _cardTool.Text = has ? "本机 · " + HudTelemetry.ShortTool(_data.Tool) : "本机暂无请求";

            double f = _data.ContextFraction;
            _cardContext.Text = !has || _data.ContextTokens <= 0 ? "—"
                : (f >= 0 ? ((int)Math.Round(f * 100)).ToString(CultureInfo.InvariantCulture) + "% · " + FormatterUtil.FormatTokens(_data.ContextTokens)
                          : FormatterUtil.FormatTokens(_data.ContextTokens));
            _cardContext.SetResourceReference(TextBlock.ForegroundProperty, f >= WarnFraction ? "SemanticWarning" : "FgPrimary");
            _cardContext.ToolTip = f >= 0
                ? "最近一次请求的上下文 " + _data.ContextTokens.ToString("N0", CultureInfo.InvariantCulture) + " / " + _data.ContextLimit.ToString("N0", CultureInfo.InvariantCulture) + " tokens" + (f >= WarnFraction ? "，可以考虑 /compact" : "")
                : "价格库里没有这个模型的上下文上限，只显示绝对值";
            _cardToday.Text = Money(_data.TodayUsd) + (_data.UnpricedRequests > 0 ? "*" : "");
            _cardToday.ToolTip = "按 API 单价估算，不是实付";
            _cardHour.Text = Money(_data.Last15Usd * 4) + "/h";
            _cardCache.Text = _data.CacheHitRate >= 0 ? (_data.CacheHitRate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

            RenderBars(_cardChart, Tail(_data.SparkTwoHours, CardBars), CardBarWidth, CardBarGap, CardBarHeight);

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
        }

        // 柱子一律中性灰，只有最近一格用强调色
        private void RenderBars(Canvas canvas, double[] values, double width, double gap, double height)
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
                bool hasValue = values[i] > 0 && max > 0;
                double h = hasValue ? Math.Max(3, values[i] / max * height) : 2;
                Rectangle bar = new Rectangle { Width = width, Height = h, RadiusX = width / 2, RadiusY = width / 2 };
                if (hasValue && i == n - 1) bar.SetResourceReference(Shape.FillProperty, "AccentText");
                else bar.Fill = hasValue ? used : idle;
                Canvas.SetLeft(bar, i * (width + gap));
                Canvas.SetTop(bar, height - h);
                DateTime from = start.AddMinutes(5 * i);
                bar.ToolTip = from.ToString("HH:mm", CultureInfo.InvariantCulture) + "–" + from.AddMinutes(5).ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + Money(values[i]);
                canvas.Children.Add(bar);
            }
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
            // 球在屏幕右半边时卡片右对齐，避免伸出屏幕
            Rect area = CurrentWorkArea();
            bool rightHalf = Left + ActualWidth / 2 > area.Left + area.Width / 2;
            _cardPopup.HorizontalOffset = rightHalf ? BallSize - CardWidth : 0;
            _cardPopup.IsOpen = true;
            ApplyIdle();
        }

        private void CloseCard()
        {
            _cardPopup.IsOpen = false;
            ApplyIdle();
        }

        // ------------------------------------------------------------------ 位置：贴边吸附、半隐藏、记住位置

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

        // 窗口外圈有 GlowRoom 的光晕留白：贴边时让球本身离屏幕边 EdgeGap
        private double ShownLeft(Rect a)
        {
            return _dock == Dock.Left ? a.Left + EdgeGap - GlowRoom : a.Right - BallSize - EdgeGap - GlowRoom;
        }

        private double TuckedLeft(Rect a)
        {
            return _dock == Dock.Left ? a.Left - GlowRoom - BallSize / 2 : a.Right - GlowRoom - BallSize / 2;
        }

        private void SnapToEdges()
        {
            Rect a = CurrentWorkArea();
            double ballLeft = Left + GlowRoom, ballTop = Top + GlowRoom;
            _dock = Dock.None;
            if (ballLeft - a.Left < SnapDistance) _dock = Dock.Left;
            else if (a.Right - (ballLeft + BallSize) < SnapDistance) _dock = Dock.Right;
            if (_dock != Dock.None) Left = ShownLeft(a);
            if (ballTop - a.Top < SnapDistance) Top = a.Top + EdgeGap - GlowRoom;
            else if (a.Bottom - (ballTop + BallSize) < SnapDistance) Top = a.Bottom - BallSize - EdgeGap - GlowRoom;
            Top = Math.Max(a.Top - GlowRoom, Math.Min(Top, a.Bottom - BallSize - GlowRoom));
            _tucked = false;
            if (_dock != Dock.None) _tuck.Start();
        }

        // 贴在左右边缘时，空闲状态下缩进一半；鼠标移上来滑出
        private void SetTucked(bool tucked)
        {
            if (_dock == Dock.None || tucked == _tucked) return;
            _tucked = tucked;
            Rect a = CurrentWorkArea();
            double target = tucked ? TuckedLeft(a) : ShownLeft(a);
            DoubleAnimation slide = new DoubleAnimation(Left, target, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            slide.Completed += delegate { BeginAnimation(LeftProperty, null); Left = target; };
            BeginAnimation(LeftProperty, slide);
        }

        private static string PositionFile
        {
            get { return System.IO.Path.Combine(SyncService.AppConfigDir, "hud_position.txt"); }
        }

        // 位置文件：left,top,显示内容,贴边方向
        private void SavePosition()
        {
            try
            {
                if (!IsLoaded) return;
                System.IO.Directory.CreateDirectory(SyncService.AppConfigDir);
                Rect a = CurrentWorkArea();
                double left = _dock != Dock.None ? ShownLeft(a) : Left;
                System.IO.File.WriteAllText(PositionFile, string.Join(",", new string[]
                {
                    left.ToString("0.#", CultureInfo.InvariantCulture),
                    Top.ToString("0.#", CultureInfo.InvariantCulture),
                    _mode.ToString(CultureInfo.InvariantCulture),
                    ((int)_dock).ToString(CultureInfo.InvariantCulture)
                }));
            }
            catch { }
        }

        private void RestorePosition()
        {
            Rect a = CurrentWorkArea();
            _dock = Dock.Right;
            double left = ShownLeft(a), top = a.Top + 80;
            try
            {
                if (System.IO.File.Exists(PositionFile))
                {
                    string[] parts = System.IO.File.ReadAllText(PositionFile).Trim().Split(',');
                    double l, t;
                    int mode, dock;
                    if (parts.Length >= 2
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out l)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t)
                        && l > a.Left - ActualWidth && l < a.Right && t > a.Top - ActualHeight && t < a.Bottom)
                    {
                        left = l;
                        top = t;
                        _dock = Dock.None;
                    }
                    if (parts.Length >= 3 && int.TryParse(parts[2], out mode) && mode >= 0 && mode < ModeNames.Length) { _mode = mode; for (int i = 0; i < _modeItems.Count; i++) _modeItems[i].IsChecked = i == mode; RenderCenter(); }
                    if (parts.Length >= 4 && int.TryParse(parts[3], out dock) && dock >= 0 && dock <= 2) _dock = (Dock)dock;
                    if (_dock != Dock.None) left = ShownLeft(a);
                }
            }
            catch { }
            Left = left;
            Top = Math.Max(a.Top - GlowRoom, Math.Min(top, a.Bottom - BallSize - GlowRoom));
        }

        // ------------------------------------------------------------------ 小工具

        private TextBlock BallText(double size, bool strong)
        {
            TextBlock t = new TextBlock { FontFamily = DesignSystem.FontUi, FontSize = size, TextWrapping = TextWrapping.NoWrap };
            t.Foreground = strong ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255));
            if (strong) t.FontWeight = FontWeights.SemiBold;
            ModernControlsFactory.EnableTabularNumbers(t, false);
            return t;
        }

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

        // 球心只有 40 像素宽：金额取 3 位有效数字，货币符号单独放小号
        private string[] SplitMoney(double usd)
        {
            double v = _data.Cny ? usd * _data.Rate : usd;
            string symbol = _data.Cny ? "¥" : "$";
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return new string[] { symbol, "0" };
            string text;
            if (v >= 10000) text = (v / 1000).ToString("0", CultureInfo.InvariantCulture) + "k";
            else if (v >= 1000) text = (v / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            else if (v >= 100) text = v.ToString("0", CultureInfo.InvariantCulture);
            else if (v >= 10) text = v.ToString("0.0", CultureInfo.InvariantCulture);
            else text = v.ToString("0.00", CultureInfo.InvariantCulture);
            return new string[] { symbol, text };
        }

        internal static string CompactTokens(long n)
        {
            if (n < 1000) return n.ToString(CultureInfo.InvariantCulture) + " ";
            if (n < 1000000) return (n / 1000).ToString(CultureInfo.InvariantCulture) + "K";
            return (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
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

        private string BuildSummary(long age)
        {
            if (_data.TimestampMs <= 0) return "本机今日估值 " + Money(_data.TodayUsd) + " · 暂无请求";
            return HudTelemetry.ShortModel(_data.Model) + " · " + Ago(age) + " · 上下文 " + FormatterUtil.FormatTokens(_data.ContextTokens)
                + " · 今日 " + Money(_data.TodayUsd);
        }

        private void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem show = new MenuItem { Header = "球心显示" };
            for (int i = 0; i < ModeNames.Length; i++)
            {
                int mode = i;
                MenuItem item = new MenuItem { Header = ModeNames[i], IsCheckable = true, IsChecked = i == _mode };
                item.Click += delegate { SetMode(mode, true); };
                show.Items.Add(item);
                _modeItems.Add(item);
            }
            menu.Items.Add(show);
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
            menu.Items.Add(new Separator());
            MenuItem refresh = new MenuItem { Header = "重新扫描" };
            refresh.Click += delegate { if (_refresh != null) _refresh(); };
            menu.Items.Add(refresh);
            MenuItem main = new MenuItem { Header = "打开主窗口" };
            main.Click += delegate { if (_restore != null) _restore(); };
            menu.Items.Add(main);
            MenuItem hide = new MenuItem { Header = "隐藏悬浮球 (Ctrl+M)" };
            hide.Click += delegate { Hide(); };
            menu.Items.Add(hide);
            _ball.ContextMenu = menu;
            menu.Opened += delegate { _hoverOpen.Stop(); _tuck.Stop(); CloseCard(); };
            menu.Closed += delegate { if (!_overBall) _tuck.Start(); };
        }
    }
}
