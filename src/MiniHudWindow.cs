using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TokLens
{
    /// <summary>
    /// 知微 · TokLens 桌面极简悬浮胶囊 HUD（对标 CodexBar / iStat Menus / Raycast HUD）。
    /// 单行三段式等宽数字排版，0.0px 零布局抖动，支持拖拽、位置锁定、多档透明度与深浅色无缝切换。
    /// </summary>
    public sealed class MiniHudWindow : Window
    {
        // Win32 扩展样式与 DWM 透明边框清理，避免污染 Alt+Tab 列表与 Win11 透明矩形外框
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        // Obsidian 深色与 Paper 浅色胶囊专属色值契约
        private const string DarkPillBgHex = "#F0121216";
        private const string DarkPillBorderHex = "#32323B";
        private const string DarkPillHoverBorderHex = "#4A4A58";
        private const string DarkPillBudgetBorderHex = "#6E4F1D";
        private const string DarkDividerHex = "#2B2B34";

        private const string LightPillBgHex = "#F5FFFFFF";
        private const string LightPillBorderHex = "#D4D4D8";
        private const string LightPillHoverBorderHex = "#A1A1AA";
        private const string LightPillBudgetBorderHex = "#F59E0B";
        private const string LightDividerHex = "#E4E4E7";

        // 呼吸状态指示色
        private const string IdleEmeraldHex = "#34D399";
        private const string ActiveIndigoHex = "#818CF8";

        // 预算超标柔和琥珀色点缀
        private const string DarkAmberFgHex = "#FBBF24";
        private const string DarkAmberPillBgHex = "#24F59E0B";
        private const string LightAmberFgHex = "#B45309";
        private const string LightAmberPillBgHex = "#1FD97706";

        // 右键菜单补充矢量图标路径（16x16 视口，与 UiIcons 风格一致）
        private const string IconLockOpen = "M 4.5,7.5 L 11.5,7.5 L 11.5,13.5 L 4.5,13.5 Z M 6,7.5 L 6,5.0 A 2,2 0 0 1 10,4.6";
        private const string IconOpacityRing = "M 8,2.5 A 5.5,5.5 0 1 1 7.99,2.5 M 8,2.5 L 8,13.5";
        private const string IconEyeOff = "M 2,2.5 L 14,13.5 M 4.2,5.2 C 2.8,6.2 1.9,7.6 1.5,8 C 2.6,10.5 5.1,12.2 8,12.2 C 9.2,12.2 10.3,11.9 11.3,11.3 M 6.6,4.0 C 7.0,3.9 7.5,3.8 8,3.8 C 10.9,3.8 13.4,5.5 14.5,8 C 14.1,8.9 13.5,9.7 12.7,10.3";

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private readonly Action _onRestoreMainWindow;
        private readonly Action _onRefreshRequested;

        private bool _isDark;
        private bool _isActivePulse;
        private bool _isOverBudget;
        private bool _isPointerHovering;
        private bool _isPositionLocked;
        private double _hudOpacity;

        // 动画状态去重，确保高频 UpdateTelemetry 时呼吸动画连续丝滑不跳帧
        private int _appliedAnimMode; // 0 = 停止, 1 = 空闲静息微光, 2 = 活跃高频脉冲

        // 左键点击与拖拽分离阈值控制（确保双击左键 100% 灵敏且拖拽跟手）
        private bool _leftButtonArmed;
        private Point _leftButtonDownPos;

        // 核心视觉元素
        private Border _pillShadowLayer;
        private Border _pillBorder;
        private DropShadowEffect _pillShadow;
        private Ellipse _pulseHalo;
        private ScaleTransform _haloScale;
        private Ellipse _statusDot;
        private TextBlock _todayLabel;
        private TextBlock _costText;
        private Border _sepLeft;
        private TextBlock _tokensText;
        private Border _sepRight;
        private Border _modelAccentCapsule;
        private TextBlock _modelAndTpsText;
        private TextBlock _tooltipTitleText;
        private TextBlock _tooltipHintText;

        // 右键菜单引用
        private ContextMenu _contextMenu;
        private MenuItem _lockPositionMenuItem;
        private MenuItem _opacity100MenuItem;
        private MenuItem _opacity85MenuItem;
        private MenuItem _opacity65MenuItem;

        public MiniHudWindow(bool isDark, Action onRestoreMainWindow, Action onRefreshRequested)
        {
            _isDark = isDark;
            _onRestoreMainWindow = onRestoreMainWindow;
            _onRefreshRequested = onRefreshRequested;
            _isActivePulse = false;
            _isOverBudget = false;
            _isPointerHovering = false;
            _isPositionLocked = false;
            _hudOpacity = 1.0;
            _appliedAnimMode = 0;
            _leftButtonArmed = false;

            Title = "知微 · TokLens HUD";
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

            PositionAtDefaultTopRight();

            BuildVisualTree();
            BuildContextMenu();

            SetTheme(_isDark);
            ApplyTelemetryVisuals();
            UpdateInteractiveCursorAndTooltip();

            SourceInitialized += OnHudSourceInitialized;
            IsVisibleChanged += OnHudIsVisibleChanged;
            PreviewKeyDown += OnHudPreviewKeyDown;
            Closed += OnHudClosed;
        }

        /// <summary>
        /// 实时更新胶囊三段式遥测指标与脉冲/预算状态（线程安全，0.0px 零抖动）。
        /// </summary>
        public void UpdateTelemetry(
            string todayCostText,
            string todayTokensText,
            string latestModelAndTpsText,
            bool isActivePulse,
            bool isOverBudget)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    UpdateTelemetry(todayCostText, todayTokensText, latestModelAndTpsText, isActivePulse, isOverBudget);
                }));
                return;
            }

            string safeCost = string.IsNullOrEmpty(todayCostText) ? "¥0.00" : todayCostText;
            string safeTokens = string.IsNullOrEmpty(todayTokensText) ? "0 tok" : todayTokensText;
            string safeModel = string.IsNullOrEmpty(latestModelAndTpsText) ? "待命 · 0 次" : latestModelAndTpsText;

            if (!string.Equals(_costText.Text, safeCost, StringComparison.Ordinal))
            {
                _costText.Text = safeCost;
            }

            if (!string.Equals(_tokensText.Text, safeTokens, StringComparison.Ordinal))
            {
                _tokensText.Text = safeTokens;
            }

            if (!string.Equals(_modelAndTpsText.Text, safeModel, StringComparison.Ordinal))
            {
                _modelAndTpsText.Text = safeModel;
            }

            bool stateChanged = (_isActivePulse != isActivePulse) || (_isOverBudget != isOverBudget);
            _isActivePulse = isActivePulse;
            _isOverBudget = isOverBudget;

            if (stateChanged || _appliedAnimMode == 0)
            {
                ApplyTelemetryVisuals();
            }

            UpdateTooltipSummary(safeCost, safeTokens, safeModel);
        }

        /// <summary>
        /// 同步 Obsidian 深色 / Paper 浅色主题资源与胶囊拟物描边投影。
        /// </summary>
        public void SetTheme(bool isDark)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetTheme(isDark);
                }));
                return;
            }

            _isDark = isDark;
            DesignSystem.ApplyTheme(this, _isDark);
            SuppressWin11LayeredFrame();

            if (_contextMenu != null)
            {
                _contextMenu.Resources = Resources;
            }

            ApplyPillChromeColors();
            ApplyTelemetryVisuals();
            RefreshMenuIndicators();
        }

        private void PositionAtDefaultTopRight()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Rect workArea = SystemParameters.WorkArea;
            double targetLeft = workArea.Right - 310.0;
            double targetTop = workArea.Top + 24.0;

            if (targetLeft < workArea.Left + 8.0)
            {
                targetLeft = workArea.Left + 8.0;
            }
            if (targetTop < workArea.Top + 8.0)
            {
                targetTop = workArea.Top + 8.0;
            }

            Left = Math.Round(targetLeft);
            Top = Math.Round(targetTop);
        }

        private void BuildVisualTree()
        {
            // 外层预留透明阴影缓冲区，确保柔和 DropShadowEffect 绝不被窗口边界硬裁剪
            Grid shadowHost = new Grid();
            shadowHost.Background = Brushes.Transparent;
            shadowHost.SnapsToDevicePixels = true;
            shadowHost.UseLayoutRounding = true;

            _pillShadowLayer = new Border();
            _pillShadowLayer.Height = 36.0;
            _pillShadowLayer.CornerRadius = new CornerRadius(18.0);
            _pillShadowLayer.Margin = new Thickness(8.0, 6.0, 8.0, 10.0);
            _pillShadowLayer.IsHitTestVisible = false;
            _pillShadowLayer.Background = DesignSystem.BrushHex(_isDark ? DarkPillBgHex : LightPillBgHex);

            _pillShadow = new DropShadowEffect();
            _pillShadow.Direction = 270.0;
            _pillShadow.BlurRadius = 14.0;
            _pillShadow.ShadowDepth = 3.0;
            _pillShadow.Color = Colors.Black;
            _pillShadow.Opacity = 0.42;
            _pillShadowLayer.Effect = _pillShadow;

            _pillBorder = new Border();
            _pillBorder.Height = 36.0;
            _pillBorder.CornerRadius = new CornerRadius(18.0);
            _pillBorder.Padding = new Thickness(12.0, 0.0, 14.0, 0.0);
            _pillBorder.BorderThickness = new Thickness(1.0);
            _pillBorder.Margin = new Thickness(8.0, 6.0, 8.0, 10.0);
            _pillBorder.SnapsToDevicePixels = true;
            _pillBorder.UseLayoutRounding = true;
            RenderOptions.SetClearTypeHint(_pillBorder, ClearTypeHint.Enabled);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.SnapsToDevicePixels = true;

            // ---------------- 左段：呼吸状态圆点 + 今日 + 今日总花费 ----------------
            Grid dotHost = new Grid();
            dotHost.Width = 14.0;
            dotHost.Height = 14.0;
            dotHost.VerticalAlignment = VerticalAlignment.Center;
            dotHost.Margin = new Thickness(0.0, 0.0, 5.0, 0.0);
            dotHost.IsHitTestVisible = false;

            _pulseHalo = new Ellipse();
            _pulseHalo.Width = 13.0;
            _pulseHalo.Height = 13.0;
            _pulseHalo.HorizontalAlignment = HorizontalAlignment.Center;
            _pulseHalo.VerticalAlignment = VerticalAlignment.Center;
            _pulseHalo.RenderTransformOrigin = new Point(0.5, 0.5);
            _haloScale = new ScaleTransform(1.0, 1.0);
            _pulseHalo.RenderTransform = _haloScale;
            _pulseHalo.Opacity = 0.22;

            _statusDot = new Ellipse();
            _statusDot.Width = 7.0;
            _statusDot.Height = 7.0;
            _statusDot.HorizontalAlignment = HorizontalAlignment.Center;
            _statusDot.VerticalAlignment = VerticalAlignment.Center;

            dotHost.Children.Add(_pulseHalo);
            dotHost.Children.Add(_statusDot);
            row.Children.Add(dotHost);

            _todayLabel = new TextBlock();
            _todayLabel.Text = "今日";
            _todayLabel.FontFamily = DesignSystem.FontUi;
            _todayLabel.FontSize = 11.0;
            _todayLabel.FontWeight = FontWeights.Medium;
            _todayLabel.VerticalAlignment = VerticalAlignment.Center;
            _todayLabel.Margin = new Thickness(0.0, 0.0, 5.0, 0.0);
            _todayLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            row.Children.Add(_todayLabel);

            _costText = new TextBlock();
            _costText.Text = "¥0.00";
            _costText.FontFamily = DesignSystem.FontMono;
            _costText.FontSize = 12.5;
            _costText.FontWeight = FontWeights.SemiBold;
            _costText.VerticalAlignment = VerticalAlignment.Center;
            _costText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ConfigureTabularDigits(_costText);
            row.Children.Add(_costText);

            // ---------------- 分隔细竖线 1 ----------------
            _sepLeft = CreateVerticalDivider();
            row.Children.Add(_sepLeft);

            // ---------------- 中段：今日总 Token ----------------
            _tokensText = new TextBlock();
            _tokensText.Text = "0 tok";
            _tokensText.FontFamily = DesignSystem.FontMono;
            _tokensText.FontSize = 12.0;
            _tokensText.FontWeight = FontWeights.Normal;
            _tokensText.VerticalAlignment = VerticalAlignment.Center;
            _tokensText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            ConfigureTabularDigits(_tokensText);
            row.Children.Add(_tokensText);

            // ---------------- 分隔细竖线 2 ----------------
            _sepRight = CreateVerticalDivider();
            _sepRight.Margin = new Thickness(10.0, 0.0, 6.0, 0.0);
            row.Children.Add(_sepRight);

            // ---------------- 右段：最新活跃模型简称 + 实时速率 TPS ----------------
            // 固定内边距微胶囊容器：常态透明，预算超标时点亮柔和琥珀底衬，切换状态 0.0px 不抖动
            _modelAccentCapsule = new Border();
            _modelAccentCapsule.CornerRadius = new CornerRadius(9.0);
            _modelAccentCapsule.Padding = new Thickness(5.0, 1.5, 5.0, 1.5);
            _modelAccentCapsule.Margin = new Thickness(0.0, 0.0, -4.0, 0.0);
            _modelAccentCapsule.VerticalAlignment = VerticalAlignment.Center;
            _modelAccentCapsule.Background = Brushes.Transparent;

            _modelAndTpsText = new TextBlock();
            _modelAndTpsText.Text = "待命 · 0 次";
            _modelAndTpsText.FontFamily = DesignSystem.FontMono;
            _modelAndTpsText.FontSize = 11.5;
            _modelAndTpsText.FontWeight = FontWeights.Medium;
            _modelAndTpsText.VerticalAlignment = VerticalAlignment.Center;
            _modelAndTpsText.MaxWidth = 230.0;
            _modelAndTpsText.TextTrimming = TextTrimming.CharacterEllipsis;
            _modelAndTpsText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            ConfigureTabularDigits(_modelAndTpsText);

            _modelAccentCapsule.Child = _modelAndTpsText;
            row.Children.Add(_modelAccentCapsule);

            _pillBorder.Child = row;
            _pillBorder.ToolTip = BuildRichToolTip();

            // 鼠标交互绑定
            _pillBorder.MouseEnter += OnPillMouseEnter;
            _pillBorder.MouseLeave += OnPillMouseLeave;
            _pillBorder.MouseLeftButtonDown += OnPillMouseLeftButtonDown;
            _pillBorder.MouseMove += OnPillMouseMove;
            _pillBorder.MouseLeftButtonUp += OnPillMouseLeftButtonUp;

            shadowHost.Children.Add(_pillShadowLayer);
            shadowHost.Children.Add(_pillBorder);
            Content = shadowHost;
        }

        private static void ConfigureTabularDigits(TextBlock tb)
        {
            if (tb == null) return;
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            Typography.SetNumeralStyle(tb, FontNumeralStyle.Lining);
        }

        private static Border CreateVerticalDivider()
        {
            Border sep = new Border();
            sep.Width = 1.0;
            sep.Height = 13.0;
            sep.VerticalAlignment = VerticalAlignment.Center;
            sep.Margin = new Thickness(10.0, 0.0, 10.0, 0.0);
            sep.SnapsToDevicePixels = true;
            return sep;
        }

        private ToolTip BuildRichToolTip()
        {
            ToolTip tip = new ToolTip();
            StackPanel sp = new StackPanel();

            _tooltipTitleText = new TextBlock();
            _tooltipTitleText.FontFamily = DesignSystem.FontMono;
            _tooltipTitleText.FontSize = 11.5;
            _tooltipTitleText.FontWeight = FontWeights.Medium;
            _tooltipTitleText.Text = "今日 ¥0.00 · 0 tok · 待命";
            _tooltipTitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(_tooltipTitleText);

            _tooltipHintText = new TextBlock();
            _tooltipHintText.FontFamily = DesignSystem.FontUi;
            _tooltipHintText.FontSize = 11.0;
            _tooltipHintText.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
            _tooltipHintText.Text = "双击打开主控面板 · 按住拖拽 · 右键更多设置";
            _tooltipHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(_tooltipHintText);

            tip.Content = sp;
            return tip;
        }

        private void BuildContextMenu()
        {
            // 自动继承 DesignSystem 现代 Popover ContextMenu 与 MenuItem 样式
            _contextMenu = new ContextMenu();
            _contextMenu.PlacementTarget = _pillBorder;
            _contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _contextMenu.VerticalOffset = 2.0;

            MenuItem openMainItem = new MenuItem();
            openMainItem.Header = "打开主控面板";
            openMainItem.InputGestureText = "双击";
            openMainItem.Icon = UiIcons.CreateIcon(UiIcons.Overview, 13.5, "AccentText");
            openMainItem.Click += delegate(object s, RoutedEventArgs e)
            {
                InvokeRestoreMainWindow();
            };
            _contextMenu.Items.Add(openMainItem);

            MenuItem refreshItem = new MenuItem();
            refreshItem.Header = "立即静默刷新";
            refreshItem.Icon = UiIcons.CreateIcon(UiIcons.Refresh, 13.5, "FgSecondary");
            refreshItem.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_onRefreshRequested != null)
                {
                    _onRefreshRequested();
                }
            };
            _contextMenu.Items.Add(refreshItem);

            _contextMenu.Items.Add(new Separator());

            _lockPositionMenuItem = new MenuItem();
            _lockPositionMenuItem.Header = "锁定悬浮窗位置";
            _lockPositionMenuItem.IsCheckable = false;
            _lockPositionMenuItem.Click += delegate(object s, RoutedEventArgs e)
            {
                _isPositionLocked = !_isPositionLocked;
                UpdateInteractiveCursorAndTooltip();
                RefreshMenuIndicators();
            };
            _contextMenu.Items.Add(_lockPositionMenuItem);

            _contextMenu.Items.Add(new Separator());

            _opacity100MenuItem = CreateOpacityMenuItem("透明度：100%", 1.0);
            _opacity85MenuItem = CreateOpacityMenuItem("透明度：85%", 0.85);
            _opacity65MenuItem = CreateOpacityMenuItem("透明度：65%", 0.65);
            _contextMenu.Items.Add(_opacity100MenuItem);
            _contextMenu.Items.Add(_opacity85MenuItem);
            _contextMenu.Items.Add(_opacity65MenuItem);

            _contextMenu.Items.Add(new Separator());

            MenuItem hideHudItem = new MenuItem();
            hideHudItem.Header = "隐藏桌面悬浮窗";
            hideHudItem.Icon = UiIcons.CreateIcon(IconEyeOff, 13.5, "FgSecondary");
            hideHudItem.Click += delegate(object s, RoutedEventArgs e)
            {
                Hide();
            };
            _contextMenu.Items.Add(hideHudItem);

            _contextMenu.Opened += delegate(object s, RoutedEventArgs e)
            {
                RefreshMenuIndicators();
            };

            _pillBorder.ContextMenu = _contextMenu;
        }

        private MenuItem CreateOpacityMenuItem(string headerText, double opacityVal)
        {
            MenuItem item = new MenuItem();
            item.Header = headerText;
            double capturedOpacity = opacityVal;
            item.Click += delegate(object s, RoutedEventArgs e)
            {
                SetHudOpacity(capturedOpacity);
            };
            return item;
        }

        private void SetHudOpacity(double targetOpacity)
        {
            if (targetOpacity < 0.35) targetOpacity = 0.35;
            if (targetOpacity > 1.0) targetOpacity = 1.0;

            _hudOpacity = targetOpacity;
            Opacity = _hudOpacity;
            RefreshMenuIndicators();
        }

        private void RefreshMenuIndicators()
        {
            if (_lockPositionMenuItem != null)
            {
                _lockPositionMenuItem.IsChecked = _isPositionLocked;
                if (_isPositionLocked)
                {
                    _lockPositionMenuItem.Icon = UiIcons.CreateIcon(UiIcons.Check, 13.5, "SemanticSuccess");
                    _lockPositionMenuItem.InputGestureText = "已锁定";
                }
                else
                {
                    _lockPositionMenuItem.Icon = UiIcons.CreateIcon(IconLockOpen, 13.5, "FgSecondary");
                    _lockPositionMenuItem.InputGestureText = "";
                }
            }

            UpdateOpacityItemVisual(_opacity100MenuItem, Math.Abs(_hudOpacity - 1.0) < 0.02);
            UpdateOpacityItemVisual(_opacity85MenuItem, Math.Abs(_hudOpacity - 0.85) < 0.02);
            UpdateOpacityItemVisual(_opacity65MenuItem, Math.Abs(_hudOpacity - 0.65) < 0.02);
        }

        private static void UpdateOpacityItemVisual(MenuItem item, bool isSelected)
        {
            if (item == null) return;
            item.IsChecked = isSelected;
            if (isSelected)
            {
                item.Icon = UiIcons.CreateIcon(UiIcons.Check, 13.5, "AccentText");
                item.InputGestureText = "当前";
            }
            else
            {
                item.Icon = UiIcons.CreateIcon(IconOpacityRing, 13.5, "FgMuted");
                item.InputGestureText = "";
            }
        }

        private void ApplyPillChromeColors()
        {
            if (_pillBorder == null) return;

            if (_isDark)
            {
                SolidColorBrush pillBg = DesignSystem.BrushHex(DarkPillBgHex);
                _pillBorder.Background = pillBg;
                if (_pillShadowLayer != null) _pillShadowLayer.Background = pillBg;
                SolidColorBrush divBrush = DesignSystem.BrushHex(DarkDividerHex);
                _sepLeft.Background = divBrush;
                _sepRight.Background = divBrush;

                if (_pillShadow != null)
                {
                    _pillShadow.Color = Colors.Black;
                    _pillShadow.BlurRadius = 15.0;
                    _pillShadow.ShadowDepth = 3.5;
                    _pillShadow.Opacity = 0.46;
                }
            }
            else
            {
                SolidColorBrush pillBg = DesignSystem.BrushHex(LightPillBgHex);
                _pillBorder.Background = pillBg;
                if (_pillShadowLayer != null) _pillShadowLayer.Background = pillBg;
                SolidColorBrush divBrush = DesignSystem.BrushHex(LightDividerHex);
                _sepLeft.Background = divBrush;
                _sepRight.Background = divBrush;

                if (_pillShadow != null)
                {
                    _pillShadow.Color = Color.FromRgb(15, 23, 42);
                    _pillShadow.BlurRadius = 13.0;
                    _pillShadow.ShadowDepth = 2.5;
                    _pillShadow.Opacity = 0.14;
                }
            }

            UpdatePillBorderStroke();
        }

        private void UpdatePillBorderStroke()
        {
            if (_pillBorder == null) return;

            if (_isDark)
            {
                if (_isPointerHovering)
                {
                    _pillBorder.BorderBrush = DesignSystem.BrushHex(
                        _isOverBudget ? DarkPillBudgetBorderHex : DarkPillHoverBorderHex);
                }
                else
                {
                    _pillBorder.BorderBrush = DesignSystem.BrushHex(DarkPillBorderHex);
                }
            }
            else
            {
                if (_isPointerHovering)
                {
                    _pillBorder.BorderBrush = DesignSystem.BrushHex(
                        _isOverBudget ? LightPillBudgetBorderHex : LightPillHoverBorderHex);
                }
                else
                {
                    _pillBorder.BorderBrush = DesignSystem.BrushHex(LightPillBorderHex);
                }
            }
        }

        private void ApplyTelemetryVisuals()
        {
            if (_statusDot == null || _pulseHalo == null || _modelAndTpsText == null)
                return;

            // 1. 左侧呼吸状态圆点色值与脉冲动画
            string dotHex = _isActivePulse ? ActiveIndigoHex : IdleEmeraldHex;
            SolidColorBrush dotBrush = DesignSystem.BrushHex(dotHex);
            _statusDot.Fill = dotBrush;
            _pulseHalo.Fill = dotBrush;

            SyncPulseAnimation();

            // 2. 右段模型速率与预算超标柔和琥珀色点缀（固定微胶囊尺寸，0.0px 零抖动）
            if (_isOverBudget)
            {
                _modelAccentCapsule.Background = DesignSystem.BrushHex(
                    _isDark ? DarkAmberPillBgHex : LightAmberPillBgHex);
                _modelAndTpsText.Foreground = DesignSystem.BrushHex(
                    _isDark ? DarkAmberFgHex : LightAmberFgHex);
            }
            else
            {
                _modelAccentCapsule.Background = Brushes.Transparent;
                if (_isActivePulse)
                {
                    _modelAndTpsText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                }
                else
                {
                    _modelAndTpsText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                }
            }

            UpdatePillBorderStroke();
        }

        private void SyncPulseAnimation()
        {
            if (_pulseHalo == null || _haloScale == null)
                return;

            if (!IsVisible)
            {
                if (_appliedAnimMode != 0)
                {
                    _pulseHalo.BeginAnimation(UIElement.OpacityProperty, null);
                    _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    _appliedAnimMode = 0;
                }
                return;
            }

            int desiredMode = _isActivePulse ? 2 : 1;
            if (_appliedAnimMode == desiredMode)
                return;

            _appliedAnimMode = desiredMode;

            SineEase sineEase = new SineEase();
            sineEase.EasingMode = EasingMode.EaseInOut;

            if (desiredMode == 2)
            {
                // 活跃生成中（#818CF8 靛蓝）：灵动高频呼吸律动（仅变换 Opacity 与中心 Scale，0.0px 零布局抖动）
                DoubleAnimation opAnim = new DoubleAnimation(0.18, 0.66, new Duration(TimeSpan.FromMilliseconds(780)));
                opAnim.AutoReverse = true;
                opAnim.RepeatBehavior = RepeatBehavior.Forever;
                opAnim.EasingFunction = sineEase;

                DoubleAnimation scaleAnim = new DoubleAnimation(0.82, 1.16, new Duration(TimeSpan.FromMilliseconds(780)));
                scaleAnim.AutoReverse = true;
                scaleAnim.RepeatBehavior = RepeatBehavior.Forever;
                scaleAnim.EasingFunction = sineEase;

                _pulseHalo.BeginAnimation(UIElement.OpacityProperty, opAnim);
                _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
            else
            {
                // 空闲待命（#34D399 翠绿）：克制静息柔光微呼吸
                DoubleAnimation idleOpAnim = new DoubleAnimation(0.12, 0.28, new Duration(TimeSpan.FromMilliseconds(2400)));
                idleOpAnim.AutoReverse = true;
                idleOpAnim.RepeatBehavior = RepeatBehavior.Forever;
                idleOpAnim.EasingFunction = sineEase;

                _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _haloScale.ScaleX = 1.0;
                _haloScale.ScaleY = 1.0;
                _pulseHalo.BeginAnimation(UIElement.OpacityProperty, idleOpAnim);
            }
        }

        private void UpdateInteractiveCursorAndTooltip()
        {
            if (_pillBorder != null)
            {
                _pillBorder.Cursor = _isPositionLocked ? Cursors.Hand : Cursors.SizeAll;
            }

            if (_tooltipHintText != null)
            {
                _tooltipHintText.Text = _isPositionLocked
                    ? "位置已锁定 · 双击打开主控面板 · 右键更多设置"
                    : "双击打开主控面板 · 按住拖拽移动 · 右键更多设置";
            }
        }

        private void UpdateTooltipSummary(string cost, string tokens, string modelAndTps)
        {
            if (_tooltipTitleText == null) return;
            string prefix = _isOverBudget ? "今日 · 超支 " : "今日 ";
            _tooltipTitleText.Text = prefix + cost + " · " + tokens + " · " + modelAndTps;
        }

        // ---------------- 鼠标拖拽与双击交互 ----------------

        private void OnPillMouseEnter(object sender, MouseEventArgs e)
        {
            _isPointerHovering = true;
            UpdatePillBorderStroke();
        }

        private void OnPillMouseLeave(object sender, MouseEventArgs e)
        {
            _isPointerHovering = false;
            _leftButtonArmed = false;
            UpdatePillBorderStroke();
        }

        private void OnPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.ClickCount >= 2)
            {
                _leftButtonArmed = false;
                e.Handled = true;
                InvokeRestoreMainWindow();
                return;
            }

            if (!_isPositionLocked)
            {
                _leftButtonArmed = true;
                _leftButtonDownPos = e.GetPosition(this);
            }
        }

        private void OnPillMouseMove(object sender, MouseEventArgs e)
        {
            if (!_leftButtonArmed || _isPositionLocked)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _leftButtonArmed = false;
                return;
            }

            Point currentPos = e.GetPosition(this);
            double dx = Math.Abs(currentPos.X - _leftButtonDownPos.X);
            double dy = Math.Abs(currentPos.Y - _leftButtonDownPos.Y);

            // 超过 3px 阈值再交由系统 DWM 原生 DragMove，完美兼顾 120Hz 跟手拖拽与原地灵敏双击
            if (dx >= 3.0 || dy >= 3.0)
            {
                _leftButtonArmed = false;
                try
                {
                    DragMove();
                    ClampToVirtualScreenBounds();
                }
                catch
                {
                }
            }
        }

        private void OnPillMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _leftButtonArmed = false;
        }

        private void InvokeRestoreMainWindow()
        {
            if (_onRestoreMainWindow != null)
            {
                _onRestoreMainWindow();
            }
        }

        private void ClampToVirtualScreenBounds()
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vRight = vLeft + SystemParameters.VirtualScreenWidth;
            double vBottom = vTop + SystemParameters.VirtualScreenHeight;

            double winW = ActualWidth > 0 ? ActualWidth : 260.0;
            double winH = ActualHeight > 0 ? ActualHeight : 52.0;

            double newLeft = Left;
            double newTop = Top;

            if (newLeft + winW < vLeft + 48.0) newLeft = vLeft + 48.0 - winW;
            if (newLeft > vRight - 48.0) newLeft = vRight - 48.0;
            if (newTop < vTop - 4.0) newTop = vTop - 4.0;
            if (newTop > vBottom - winH + 4.0) newTop = vBottom - winH + 4.0;

            if (Math.Abs(newLeft - Left) > 0.5) Left = Math.Round(newLeft);
            if (Math.Abs(newTop - Top) > 0.5) Top = Math.Round(newTop);
        }

        // ---------------- Win32 窗口句柄与生命周期管理 ----------------

        private void OnHudSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
                }
            }
            catch
            {
            }

            SuppressWin11LayeredFrame();
            SyncPulseAnimation();
        }

        private void SuppressWin11LayeredFrame()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int cornerPref = DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                int noneBorder = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noneBorder, sizeof(int));
            }
            catch
            {
            }
        }

        private void OnHudIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SyncPulseAnimation();
        }

        private void OnHudPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 拦截 Alt+F4 转为隐藏悬浮窗，防止销毁单例句柄导致主窗口后续唤醒异常
            if (e.Key == Key.System && e.SystemKey == Key.F4)
            {
                e.Handled = true;
                Hide();
            }
        }

        private void OnHudClosed(object sender, EventArgs e)
        {
            if (_pulseHalo != null)
            {
                _pulseHalo.BeginAnimation(UIElement.OpacityProperty, null);
            }
            if (_haloScale != null)
            {
                _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            _appliedAnimMode = 0;
        }
    }
}
