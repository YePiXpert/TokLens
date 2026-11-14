using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace TokLens
{
    /// <summary>
    /// 快捷键速查面板与多设备令牌机制可视化向导对话框。
    /// 支持按 F1 / ? 呼出，按 Esc 一键关闭，内置「快捷键速查」与「跨设备同步与令牌原理解析」双视图。
    /// </summary>
    public sealed class ShortcutAndGuideWindow : Window
    {
        // 内部扩展矢量图标路径（16x16 坐标系，与 UiIcons 规范一致）
        private const string IconKeyboard = "M 1.5,4 L 14.5,4 L 14.5,12.5 L 1.5,12.5 Z M 4,6.8 L 5,6.8 M 7.5,6.8 L 8.5,6.8 M 11,6.8 L 12,6.8 M 4.5,9.8 L 11.5,9.8";
        private const string IconBolt     = "M 9,1.8 L 3.5,8.8 L 7.8,8.8 L 7,14.2 L 12.5,7.2 L 8.2,7.2 Z";
        private const string IconHelp     = "M 8,2 A 6,6 0 1 1 7.99,2 M 6.3,6.1 C 6.3,5.0 7.1,4.4 8.1,4.4 C 9.1,4.4 9.8,5.0 9.8,5.9 C 9.8,6.7 9.2,7.1 8.5,7.5 C 8.1,7.8 8.0,8.1 8.0,8.7 M 8,11.2 L 8,11.4";

        private bool _isDark;
        private int _activeTabIndex;

        private Border _rootShellBorder;
        private Button _tabShortcutsBtn;
        private Button _tabTokenGuideBtn;
        private Path _tabShortcutsIcon;
        private Path _tabTokenGuideIcon;
        private TextBlock _tabShortcutsText;
        private TextBlock _tabTokenGuideText;
        private TextBlock _headerSubHintText;

        private ScrollViewer _shortcutsScroll;
        private ScrollViewer _tokenGuideScroll;
        private Button _switchTabFooterBtn;

        /// <summary>
        /// 获取当前激活的标签页索引（0：快捷键速查，1：跨设备同步与令牌原理解析）。
        /// </summary>
        public int SelectedTabIndex
        {
            get { return _activeTabIndex; }
        }

        public ShortcutAndGuideWindow()
            : this(null, true, 0)
        {
        }

        public ShortcutAndGuideWindow(Window owner, bool isDark)
            : this(owner, isDark, 0)
        {
        }

        /// <summary>
        /// 初始化快捷键速查与同步原理解析窗口。
        /// </summary>
        /// <param name="owner">父窗口实例（可为 null）。</param>
        /// <param name="isDark">是否启用深色主题。</param>
        /// <param name="initialTabIndex">初始页签索引：0 为快捷键速查页，1 为令牌与同步原理页。</param>
        public ShortcutAndGuideWindow(Window owner, bool isDark, int initialTabIndex)
        {
            _isDark = isDark;
            _activeTabIndex = (initialTabIndex == 1) ? 1 : 0;

            Title = "快捷键速查与同步原理指南 — " + AppConstants.ProductTitle;
            Width = 760;
            Height = 580;
            MinWidth = 680;
            MinHeight = 480;
            ResizeMode = ResizeMode.CanResize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            AttachOwnerSafely(owner);

            DesignSystem.ApplyTheme(this, _isDark);
            SetResourceReference(Window.BackgroundProperty, "BgWindow");
            SetResourceReference(Window.ForegroundProperty, "FgPrimary");
            FontFamily = DesignSystem.FontUi;

            BuildLayout();
            SelectTab(_activeTabIndex);

            PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        /// <summary>
        /// 切换到指定标签页（0：快捷键速查，1：跨设备同步与令牌原理解析）。
        /// </summary>
        public void SelectTab(int tabIndex)
        {
            _activeTabIndex = (tabIndex == 1) ? 1 : 0;

            if (_shortcutsScroll != null)
            {
                _shortcutsScroll.Visibility = (_activeTabIndex == 0) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_tokenGuideScroll != null)
            {
                _tokenGuideScroll.Visibility = (_activeTabIndex == 1) ? Visibility.Visible : Visibility.Collapsed;
            }

            UpdateTabVisualState(_tabShortcutsBtn, _tabShortcutsIcon, _tabShortcutsText, _activeTabIndex == 0);
            UpdateTabVisualState(_tabTokenGuideBtn, _tabTokenGuideIcon, _tabTokenGuideText, _activeTabIndex == 1);

            if (_headerSubHintText != null)
            {
                _headerSubHintText.Text = (_activeTabIndex == 0)
                    ? "双列键盘速查矩阵 · 全局加速"
                    : "GitHub OAuth 安全规范 · 多机账本拓扑";
            }

            if (_switchTabFooterBtn != null)
            {
                _switchTabFooterBtn.Content = (_activeTabIndex == 0)
                    ? "查看同步与令牌原理 →"
                    : "← 返回快捷键速查";
            }
        }

        private void AttachOwnerSafely(Window owner)
        {
            if (owner != null)
            {
                try
                {
                    IntPtr ownerHwnd = new WindowInteropHelper(owner).Handle;
                    if (ownerHwnd != IntPtr.Zero && owner.IsVisible)
                    {
                        Owner = owner;
                        ShowInTaskbar = false;
                        WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        return;
                    }
                }
                catch
                {
                }
            }

            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e == null) return;

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            ModifierKeys mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None)
            {
                if (e.Key == Key.D1 || e.Key == Key.NumPad1 || e.Key == Key.Left)
                {
                    e.Handled = true;
                    SelectTab(0);
                    return;
                }
                if (e.Key == Key.D2 || e.Key == Key.NumPad2 || e.Key == Key.Right)
                {
                    e.Handled = true;
                    SelectTab(1);
                    return;
                }
            }
            else if (mods == ModifierKeys.Control && e.Key == Key.Tab)
            {
                e.Handled = true;
                SelectTab(_activeTabIndex == 0 ? 1 : 0);
            }
        }

        private void BuildLayout()
        {
            _rootShellBorder = new Border();
            _rootShellBorder.BorderThickness = new Thickness(1);
            _rootShellBorder.SnapsToDevicePixels = true;
            _rootShellBorder.SetResourceReference(Border.BackgroundProperty, "BgWindow");
            _rootShellBorder.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

            Button maxBtn;
            Path maxIconPath;
            Border titleBar = BuildTitleBar(out maxBtn, out maxIconPath);
            Grid.SetRow(titleBar, 0);
            mainGrid.Children.Add(titleBar);

            Border tabStrip = BuildTopTabStrip();
            Grid.SetRow(tabStrip, 1);
            mainGrid.Children.Add(tabStrip);

            Grid contentHost = new Grid();
            _shortcutsScroll = BuildShortcutsTab();
            _tokenGuideScroll = BuildTokenGuideTab();
            contentHost.Children.Add(_shortcutsScroll);
            contentHost.Children.Add(_tokenGuideScroll);
            Grid.SetRow(contentHost, 2);
            mainGrid.Children.Add(contentHost);

            Border footerBar = BuildFooterBar();
            Grid.SetRow(footerBar, 3);
            mainGrid.Children.Add(footerBar);

            _rootShellBorder.Child = mainGrid;
            Content = _rootShellBorder;

            ModernWindowChrome.Attach(
                this,
                _rootShellBorder,
                maxBtn,
                maxIconPath,
                42.0,
                delegate() { return _isDark; });
        }

        private Border BuildTitleBar(out Button maxBtnOut, out Path maxIconPathOut)
        {
            Border bar = new Border();
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.Padding = new Thickness(16, 0, 6, 0);
            bar.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel dock = new DockPanel();
            dock.LastChildFill = true;

            StackPanel capBtns = ModernWindowChrome.CreateCaptionButtons(this, false, out maxBtnOut, out maxIconPathOut);
            DockPanel.SetDock(capBtns, Dock.Right);
            dock.Children.Add(capBtns);

            StackPanel leftBrand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Border iconBadge = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBadge.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            iconBadge.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            iconBadge.Child = UiIcons.CreateIcon(IconHelp, 13, "AccentText");
            leftBrand.Children.Add(iconBadge);

            TextBlock titleText = new TextBlock
            {
                Text = "快捷键速查与同步原理指南",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            leftBrand.Children.Add(titleText);

            TextBlock verLabel;
            Ellipse verDot;
            Border verPill = ModernControlsFactory.CreateStatusBadge("TokLens " + AppConstants.VersionTag, "accent", out verLabel, out verDot);
            verDot.Visibility = Visibility.Collapsed;
            verLabel.Margin = new Thickness(0);
            leftBrand.Children.Add(verPill);

            dock.Children.Add(leftBrand);
            bar.Child = dock;
            return bar;
        }

        private Border BuildTopTabStrip()
        {
            Border strip = new Border();
            strip.BorderThickness = new Thickness(0, 0, 0, 1);
            strip.Padding = new Thickness(18, 0, 18, 0);
            strip.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
            strip.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel dock = new DockPanel();
            dock.LastChildFill = false;

            // 左侧分段页签胶囊
            Border segBox = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3),
                VerticalAlignment = VerticalAlignment.Center
            };
            segBox.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            segBox.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            WindowChrome.SetIsHitTestVisibleInChrome(segBox, true);

            StackPanel segRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _tabShortcutsBtn = CreateTabSwitchButton(
                IconKeyboard,
                "快捷键速查",
                out _tabShortcutsIcon,
                out _tabShortcutsText);
            _tabShortcutsBtn.Click += delegate(object s, RoutedEventArgs e) { SelectTab(0); };
            _tabShortcutsBtn.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (_activeTabIndex != 0)
                {
                    _tabShortcutsBtn.SetResourceReference(Control.BackgroundProperty, "BgHoverStrong");
                    _tabShortcutsText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                }
            };
            _tabShortcutsBtn.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                UpdateTabVisualState(_tabShortcutsBtn, _tabShortcutsIcon, _tabShortcutsText, _activeTabIndex == 0);
            };

            _tabTokenGuideBtn = CreateTabSwitchButton(
                UiIcons.Key,
                "跨设备同步与令牌原理解析",
                out _tabTokenGuideIcon,
                out _tabTokenGuideText);
            _tabTokenGuideBtn.Margin = new Thickness(3, 0, 0, 0);
            _tabTokenGuideBtn.Click += delegate(object s, RoutedEventArgs e) { SelectTab(1); };
            _tabTokenGuideBtn.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (_activeTabIndex != 1)
                {
                    _tabTokenGuideBtn.SetResourceReference(Control.BackgroundProperty, "BgHoverStrong");
                    _tabTokenGuideText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                }
            };
            _tabTokenGuideBtn.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                UpdateTabVisualState(_tabTokenGuideBtn, _tabTokenGuideIcon, _tabTokenGuideText, _activeTabIndex == 1);
            };

            segRow.Children.Add(_tabShortcutsBtn);
            segRow.Children.Add(_tabTokenGuideBtn);
            segBox.Child = segRow;

            DockPanel.SetDock(segBox, Dock.Left);
            dock.Children.Add(segBox);

            // 右侧副提示文案
            _headerSubHintText = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            _headerSubHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(_headerSubHintText, Dock.Right);
            dock.Children.Add(_headerSubHintText);

            strip.Child = dock;
            return strip;
        }

        private static Button CreateTabSwitchButton(
            string iconPathData,
            string title,
            out Path iconOut,
            out TextBlock textOut)
        {
            Button btn = new Button();
            btn.Height = 24;
            btn.Padding = new Thickness(11, 0, 11, 0);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;
            btn.Background = Brushes.Transparent;
            btn.BorderBrush = Brushes.Transparent;
            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            iconOut = UiIcons.CreateIcon(iconPathData, 13, "FgSecondary");
            iconOut.Margin = new Thickness(0, 0, 6, 0);
            sp.Children.Add(iconOut);

            textOut = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            textOut.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(textOut);

            btn.Content = sp;

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "TabPillBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            btn.Template = tpl;
            return btn;
        }

        private static void UpdateTabVisualState(Button btn, Path icon, TextBlock label, bool isActive)
        {
            if (btn == null) return;

            if (isActive)
            {
                btn.SetResourceReference(Control.BackgroundProperty, "BgSegActive");
                btn.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");
                if (icon != null)
                {
                    icon.SetResourceReference(Shape.StrokeProperty, "AccentText");
                }
                if (label != null)
                {
                    label.FontWeight = FontWeights.SemiBold;
                    label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                }
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
                if (icon != null)
                {
                    icon.SetResourceReference(Shape.StrokeProperty, "FgMuted");
                }
                if (label != null)
                {
                    label.FontWeight = FontWeights.Medium;
                    label.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                }
            }
        }

        // =====================================================================
        // 标签页 1：全局与视图快捷键速查矩阵（双列 Bento 键盘按键帽排版）
        // =====================================================================
        private ScrollViewer BuildShortcutsTab()
        {
            ScrollViewer sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(18, 16, 18, 16)
            };

            StackPanel root = new StackPanel();

            // 1. 双列 Bento 矩阵
            Grid bentoGrid = new Grid();
            bentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            bentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左列卡片：全局命令与视图导航
            Border leftCard = ModernControlsFactory.CreateCard(new Thickness(15, 13, 15, 11));
            StackPanel leftStack = new StackPanel();
            leftStack.Children.Add(BuildBentoSectionHeader(
                UiIcons.Search,
                "AccentText",
                "导航与面板控制",
                "视图直达与浮层交互"));

            leftStack.Children.Add(BuildShortcutRow(
                "呼出全局命令面板",
                "模糊检索全部动作、切换主题与诊断工具",
                new string[][] { new string[] { "Ctrl", "K" } },
                true));

            leftStack.Children.Add(BuildShortcutRow(
                "切换页面",
                "数字键直达侧栏各页",
                new string[][] { new string[] { "Ctrl", "1–5 / 7–9 / 0" } },
                true));

            leftStack.Children.Add(BuildShortcutRow(
                "打开快捷键与同步原理指南",
                "随时呼出本速查面板与多设备令牌图解",
                new string[][] { new string[] { "F1" }, new string[] { "?" } },
                true));

            leftStack.Children.Add(BuildShortcutRow(
                "收起抽屉 / 命令面板 / 对话框",
                "逐层关闭当前激活的侧边抽屉或模态浮层",
                new string[][] { new string[] { "Esc" } },
                false));

            leftCard.Child = leftStack;
            Grid.SetColumn(leftCard, 0);
            bentoGrid.Children.Add(leftCard);

            // 右列卡片：数据扫描、云端同步与监控工具
            Border rightCard = ModernControlsFactory.CreateCard(new Thickness(15, 13, 15, 11));
            StackPanel rightStack = new StackPanel();
            rightStack.Children.Add(BuildBentoSectionHeader(
                UiIcons.Activity,
                "SemanticSuccess",
                "扫描、同步与输出",
                "算力刷新、云账本与悬浮窗"));

            rightStack.Children.Add(BuildShortcutRow(
                "立即增量扫描本机日志",
                "只读取各工具日志的新增部分",
                new string[][] { new string[] { "Ctrl", "R" }, new string[] { "F5" } },
                true));

            rightStack.Children.Add(BuildShortcutRow(
                "导出对账明细 (.csv)",
                "按日期、设备、工具和模型逐行",
                new string[][] { new string[] { "Ctrl", "S" } },
                true));

            rightStack.Children.Add(BuildShortcutRow(
                "导出网页看板 (.html)",
                "生成单文件离线交互图表与模型成本明细账单",
                new string[][] { new string[] { "Ctrl", "E" } },
                true));

            rightStack.Children.Add(BuildShortcutRow(
                "显示 / 隐藏桌面悬浮球",
                "外圈是上下文占用，悬停看今日概况与最近请求",
                new string[][] { new string[] { "Ctrl", "M" } },
                false));

            rightCard.Child = rightStack;
            Grid.SetColumn(rightCard, 2);
            bentoGrid.Children.Add(rightCard);

            root.Children.Add(bentoGrid);

            // 2. 底部通栏高亮 Bento 卡：跨应用全局唤醒快捷键
            Border globalHotkeyCard = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(16, 13, 16, 13),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            globalHotkeyCard.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            globalHotkeyCard.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            Grid globalGrid = new Grid();
            globalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            globalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            globalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border boltBox = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            boltBox.SetResourceReference(Border.BackgroundProperty, "BgCard");
            boltBox.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            boltBox.Child = UiIcons.CreateIcon(IconBolt, 16, "AccentText");
            Grid.SetColumn(boltBox, 0);
            globalGrid.Children.Add(boltBox);

            StackPanel globalTextCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            StackPanel globalTitleRow = new StackPanel { Orientation = Orientation.Horizontal };

            TextBlock globalTitle = new TextBlock
            {
                Text = "系统全局跨应用唤醒快捷键",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            globalTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            globalTitleRow.Children.Add(globalTitle);

            TextBlock sysTagText;
            Ellipse sysTagDot;
            Border sysTagBadge = ModernControlsFactory.CreateStatusBadge("跨应用生效", "accent", out sysTagText, out sysTagDot);
            globalTitleRow.Children.Add(sysTagBadge);
            globalTextCol.Children.Add(globalTitleRow);

            TextBlock globalDesc = new TextBlock
            {
                Text = "无论当前焦点位于 Cursor、VS Code、终端还是浏览器，按下组合键即可从后台或系统托盘瞬间唤醒/隐藏主窗口。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 4, 12, 0),
                TextWrapping = TextWrapping.Wrap
            };
            globalDesc.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            globalTextCol.Children.Add(globalDesc);

            Grid.SetColumn(globalTextCol, 1);
            globalGrid.Children.Add(globalTextCol);

            FrameworkElement globalKeys = CreateKeyChordGroup(new string[][]
            {
                new string[] { "Ctrl", "Alt", "T" }
            });
            globalKeys.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(globalKeys, 2);
            globalGrid.Children.Add(globalKeys);

            globalHotkeyCard.Child = globalGrid;
            root.Children.Add(globalHotkeyCard);

            sv.Content = root;
            return sv;
        }

        private static UIElement BuildBentoSectionHeader(
            string iconPathData,
            string iconColorKey,
            string title,
            string subtitle)
        {
            Border headerWrap = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 10),
                Margin = new Thickness(0, 0, 0, 4)
            };
            headerWrap.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel dock = new DockPanel();

            TextBlock subTb = new TextBlock
            {
                Text = subtitle,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(subTb, Dock.Right);
            dock.Children.Add(subTb);

            StackPanel left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Border iconBox = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBox.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            iconBox.Child = UiIcons.CreateIcon(iconPathData, 12, iconColorKey);
            left.Children.Add(iconBox);

            TextBlock titleTb = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            left.Children.Add(titleTb);

            dock.Children.Add(left);
            headerWrap.Child = dock;
            return headerWrap;
        }

        private static UIElement BuildShortcutRow(
            string actionTitle,
            string actionSubDesc,
            string[][] keyAlternatives,
            bool showBottomDivider)
        {
            Border dividerWrap = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, showBottomDivider ? 1 : 0),
                Padding = new Thickness(0, 2, 0, 2)
            };
            if (showBottomDivider)
            {
                dividerWrap.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            }

            Border rowBd = new Border
            {
                Padding = new Thickness(6, 7, 6, 7),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent
            };
            rowBd.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                rowBd.SetResourceReference(Border.BackgroundProperty, "BgHover");
            };
            rowBd.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                rowBd.Background = Brushes.Transparent;
            };

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            TextBlock mainTb = new TextBlock
            {
                Text = actionTitle,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            mainTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            textStack.Children.Add(mainTb);

            if (!string.IsNullOrEmpty(actionSubDesc))
            {
                TextBlock subTb = new TextBlock
                {
                    Text = actionSubDesc,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextCaption,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                textStack.Children.Add(subTb);
            }

            Grid.SetColumn(textStack, 0);
            g.Children.Add(textStack);

            FrameworkElement kbdGroup = CreateKeyChordGroup(keyAlternatives);
            kbdGroup.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(kbdGroup, 1);
            g.Children.Add(kbdGroup);

            rowBd.Child = g;
            dividerWrap.Child = rowBd;
            return dividerWrap;
        }

        /// <summary>
        /// 构建精致立体按键帽组（支持 Ctrl + K 组合键及 Ctrl + R / F5 多选键展示）。
        /// </summary>
        private static FrameworkElement CreateKeyChordGroup(string[][] alternatives)
        {
            StackPanel outer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (alternatives == null) return outer;

            for (int a = 0; a < alternatives.Length; a++)
            {
                if (a > 0)
                {
                    TextBlock slash = new TextBlock
                    {
                        Text = "/",
                        FontFamily = DesignSystem.FontMono,
                        FontSize = DesignSystem.TextCaption,
                        Margin = new Thickness(5, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    slash.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                    outer.Children.Add(slash);
                }

                string[] chord = alternatives[a];
                if (chord == null) continue;

                for (int k = 0; k < chord.Length; k++)
                {
                    if (k > 0)
                    {
                        TextBlock plus = new TextBlock
                        {
                            Text = "+",
                            FontFamily = DesignSystem.FontMono,
                            FontSize = DesignSystem.TextCaption,
                            Margin = new Thickness(3, 0, 3, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        plus.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                        outer.Children.Add(plus);
                    }

                    outer.Children.Add(CreateKeyCap(chord[k]));
                }
            }

            return outer;
        }

        /// <summary>
        /// 制作单个立体感键盘按键帽（&lt;kbd&gt; 视觉效果：BgElevated 底色 + 1px BorderStrong + 底部微立体衬沿 + FontMono 11px）。
        /// </summary>
        private static Border CreateKeyCap(string keyLabel)
        {
            // 外层提供 1px 底部立体按键底座感
            Border keyBase = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(0, 0, 0, 1),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            keyBase.SetResourceReference(Border.BackgroundProperty, "BorderStrong");

            Border keyTop = new Border
            {
                MinWidth = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                SnapsToDevicePixels = true
            };
            keyTop.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            keyTop.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            TextBlock tb = new TextBlock
            {
                Text = keyLabel ?? "",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

            keyTop.Child = tb;
            keyBase.Child = keyTop;
            return keyBase;
        }

        // =====================================================================
        // 标签页 2：家里与单位 gh 令牌差异及云同步拓扑可视化图解
        // =====================================================================
        private ScrollViewer BuildTokenGuideTab()
        {
            ScrollViewer sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(18, 14, 18, 14)
            };

            StackPanel root = new StackPanel();

            // 1. 核心结论高亮卡
            root.Children.Add(BuildCoreConclusionBanner());

            // 2. 家里电脑 ⇄ 私有 Gist 账本 ⇄ 单位电脑 原生矢量拓扑流向图
            root.Children.Add(BuildTopologyDiagramCard());

            // 3. 3 种真正导致不同步的情况及 10 秒排障对策
            root.Children.Add(BuildTroubleshootingCard());

            // 4. 隐私安全承诺卡
            root.Children.Add(BuildPrivacyCommitmentCard());

            sv.Content = root;
            return sv;
        }

        private static UIElement BuildCoreConclusionBanner()
        {
            Border card = new Border
            {
                Padding = new Thickness(14, 11, 14, 11),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            card.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            card.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border iconBox = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 1, 11, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            iconBox.SetResourceReference(Border.BackgroundProperty, "BgCard");
            iconBox.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            iconBox.Child = UiIcons.CreateIcon(UiIcons.Key, 15, "AccentText");
            Grid.SetColumn(iconBox, 0);
            g.Children.Add(iconBox);

            StackPanel textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            TextBlock titleTb = new TextBlock
            {
                Text = "核心结论：两台电脑的 gh 令牌本就不一样，千万无需手动复制粘贴令牌！",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            textCol.Children.Add(titleTb);

            TextBlock bodyTb = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                LineHeight = 18,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            bodyTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");

            bodyTb.Inlines.Add(new Run("家里电脑 "));
            bodyTb.Inlines.Add(MakeMonoRun("gho_AAAA...", "AccentText"));
            bodyTb.Inlines.Add(new Run(" ≠ 单位电脑 "));
            bodyTb.Inlines.Add(MakeMonoRun("gho_BBBB...", "AccentText"));
            bodyTb.Inlines.Add(new Run(" 是 GitHub 官方"));
            bodyTb.Inlines.Add(MakeBoldRun("每设备独立 OAuth 令牌", "FgPrimary"));
            bodyTb.Inlines.Add(new Run("的安全设计！只要两台电脑登录同一个 "));
            bodyTb.Inlines.Add(MakeMonoRun("@GitHub账号", "SemanticSuccess"));
            bodyTb.Inlines.Add(new Run("，它们天然读写同一个私有 Gist 账本，无需手动复制粘贴令牌。"));

            textCol.Children.Add(bodyTb);
            Grid.SetColumn(textCol, 1);
            g.Children.Add(textCol);

            card.Child = g;
            return card;
        }

        private static UIElement BuildTopologyDiagramCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 11));
            card.Margin = new Thickness(0, 10, 0, 0);

            StackPanel stack = new StackPanel();

            // 拓扑卡标题行
            DockPanel headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };

            TextBlock badgeLabel;
            Ellipse badgeDot;
            Border statusPill = ModernControlsFactory.CreateStatusBadge("按机器 ID 分片合并 · 互不覆盖", "success", out badgeLabel, out badgeDot);
            DockPanel.SetDock(statusPill, Dock.Right);
            headerRow.Children.Add(statusPill);

            StackPanel titleLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Path topoIcon = UiIcons.CreateIcon(UiIcons.Cloud, 14, "AccentText");
            topoIcon.Margin = new Thickness(0, 0, 7, 0);
            titleLeft.Children.Add(topoIcon);

            TextBlock topoTitle = new TextBlock
            {
                Text = "跨设备 OAuth 独立令牌与云端私有账本拓扑图解",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            topoTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleLeft.Children.Add(topoTitle);
            headerRow.Children.Add(titleLeft);

            stack.Children.Add(headerRow);

            // 5 列拓扑结构：家里电脑 ⇄ 私有 Gist 云账本 ⇄ 单位电脑
            Grid topoGrid = new Grid();
            topoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            topoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.18, GridUnitType.Star) });
            topoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            topoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement homeNode = BuildDeviceTopologyNode(
                "家里电脑 (Home)",
                "gho_AAAA...",
                "设备独立 OAuth 令牌 #1",
                "@同一GitHub账号");
            Grid.SetColumn(homeNode, 0);
            topoGrid.Children.Add(homeNode);

            UIElement leftArrow = BuildBidirectionalArrow("双向同步", "HTTPS");
            Grid.SetColumn(leftArrow, 1);
            topoGrid.Children.Add(leftArrow);

            UIElement hubNode = BuildCloudGistHubNode();
            Grid.SetColumn(hubNode, 2);
            topoGrid.Children.Add(hubNode);

            UIElement rightArrow = BuildBidirectionalArrow("双向同步", "HTTPS");
            Grid.SetColumn(rightArrow, 3);
            topoGrid.Children.Add(rightArrow);

            UIElement officeNode = BuildDeviceTopologyNode(
                "单位电脑 (Office)",
                "gho_BBBB...",
                "设备独立 OAuth 令牌 #2",
                "@同一GitHub账号");
            Grid.SetColumn(officeNode, 4);
            topoGrid.Children.Add(officeNode);

            stack.Children.Add(topoGrid);
            card.Child = stack;
            return card;
        }

        private static UIElement BuildDeviceTopologyNode(
            string deviceTitle,
            string tokenSample,
            string tokenNote,
            string accountHandle)
        {
            Border box = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                SnapsToDevicePixels = true
            };
            box.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            box.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            StackPanel sp = new StackPanel();

            StackPanel head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Path devIcon = UiIcons.CreateIcon(UiIcons.Devices, 13, "FgSecondary");
            devIcon.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(devIcon);

            TextBlock devTb = new TextBlock
            {
                Text = deviceTitle,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            devTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            head.Children.Add(devTb);
            sp.Children.Add(head);

            // 令牌展示胶囊
            Border tokenBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 0, 4)
            };
            tokenBadge.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            tokenBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel tokenCol = new StackPanel();
            TextBlock tokCode = new TextBlock
            {
                Text = tokenSample,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold
            };
            tokCode.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            tokenCol.Children.Add(tokCode);

            TextBlock tokSub = new TextBlock
            {
                Text = tokenNote,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 1, 0, 0)
            };
            tokSub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tokenCol.Children.Add(tokSub);

            tokenBadge.Child = tokenCol;
            sp.Children.Add(tokenBadge);

            // 登录身份校验行
            StackPanel acctRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 2, 0, 0)
            };
            Path chk = UiIcons.CreateIcon(UiIcons.Check, 10, "SemanticSuccess");
            chk.Margin = new Thickness(0, 0, 4, 0);
            acctRow.Children.Add(chk);

            TextBlock acctTb = new TextBlock
            {
                Text = accountHandle,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            acctTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            acctRow.Children.Add(acctTb);

            sp.Children.Add(acctRow);
            box.Child = sp;
            return box;
        }

        private static UIElement BuildCloudGistHubNode()
        {
            Border hub = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 8, 11, 8),
                SnapsToDevicePixels = true
            };
            hub.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            hub.SetResourceReference(Border.BorderBrushProperty, "BorderFocus");

            StackPanel sp = new StackPanel();

            StackPanel head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Path cloudIcon = UiIcons.CreateIcon(UiIcons.Cloud, 14, "AccentText");
            cloudIcon.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(cloudIcon);

            TextBlock titleTb = new TextBlock
            {
                Text = "同一私有 Gist 账本",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            head.Children.Add(titleTb);
            sp.Children.Add(head);

            Border gistBox = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 0, 5)
            };
            gistBox.SetResourceReference(Border.BackgroundProperty, "BgCard");
            gistBox.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            StackPanel gistInner = new StackPanel();
            TextBlock gistFileTb = new TextBlock
            {
                Text = "Secret Gist · 多设备汇总库",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            gistFileTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            gistInner.Children.Add(gistFileTb);

            TextBlock gistRuleTb = new TextBlock
            {
                Text = "自动识别账号名下已有账本",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            gistRuleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            gistInner.Children.Add(gistRuleTb);

            gistBox.Child = gistInner;
            sp.Children.Add(gistBox);

            TextBlock ownerAnchor = new TextBlock
            {
                Text = "唯一归属锚点：@同一GitHub账号",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ownerAnchor.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            sp.Children.Add(ownerAnchor);

            hub.Child = sp;
            return hub;
        }

        private static UIElement BuildBidirectionalArrow(string topLabel, string bottomLabel)
        {
            StackPanel sp = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            TextBlock topTb = new TextBlock
            {
                Text = topLabel,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3)
            };
            topTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(topTb);

            Path arrowPath = new Path
            {
                Data = Geometry.Parse("M 6.5,2 L 1.5,7 L 6.5,12 M 2,7 L 48,7 M 43,2 L 48,7 L 43,12"),
                Width = 50,
                Height = 14,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true
            };
            arrowPath.SetResourceReference(Shape.StrokeProperty, "AccentText");
            sp.Children.Add(arrowPath);

            TextBlock botTb = new TextBlock
            {
                Text = bottomLabel,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            botTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(botTb);

            return sp;
        }

        private static UIElement BuildTroubleshootingCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            card.Margin = new Thickness(0, 10, 0, 0);

            StackPanel stack = new StackPanel();

            StackPanel header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Path warnIcon = UiIcons.CreateIcon(UiIcons.Alert, 14, "SemanticWarning");
            warnIcon.Margin = new Thickness(0, 0, 7, 0);
            header.Children.Add(warnIcon);

            TextBlock titleTb = new TextBlock
            {
                Text = "3 种真正会导致数据不同步的情况及 10 秒排障对策",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            header.Children.Add(titleTb);
            stack.Children.Add(header);

            // 情况 1
            TextBlock item1 = CreateTroubleshootTextBlock();
            item1.Inlines.Add(MakeBoldRun("登录了不同 GitHub 账号", "FgPrimary"));
            item1.Inlines.Add(new Run("（如 "));
            item1.Inlines.Add(MakeMonoRun("@公司号", "SemanticWarning"));
            item1.Inlines.Add(new Run(" vs "));
            item1.Inlines.Add(MakeMonoRun("@个人号", "SemanticWarning"));
            item1.Inlines.Add(new Run("） → 在「设置 › 同步与偏好」页点击「"));
            item1.Inlines.Add(MakeBoldRun("校验身份", "AccentText"));
            item1.Inlines.Add(new Run("」核对 "));
            item1.Inlines.Add(MakeMonoRun("@用户名", "FgPrimary"));
            item1.Inlines.Add(new Run(" 或切换账号。"));
            stack.Children.Add(BuildNumberedTroubleshootRow("1", item1, true));

            // 情况 2
            TextBlock item2 = CreateTroubleshootTextBlock();
            item2.Inlines.Add(new Run("系统环境变量 "));
            item2.Inlines.Add(MakeMonoRun("GH_TOKEN", "SemanticWarning"));
            item2.Inlines.Add(new Run(" / "));
            item2.Inlines.Add(MakeMonoRun("GITHUB_TOKEN", "SemanticWarning"));
            item2.Inlines.Add(new Run(" 覆盖了 "));
            item2.Inlines.Add(MakeMonoRun("gh", "FgPrimary"));
            item2.Inlines.Add(new Run(" 默认凭据 → "));
            item2.Inlines.Add(MakeBoldRun("TokLens " + AppConstants.VersionTag + " 已自动绕过干扰并高亮预警", "SemanticSuccess"));
            item2.Inlines.Add(new Run("。"));
            stack.Children.Add(BuildNumberedTroubleshootRow("2", item2, true));

            // 情况 3
            TextBlock item3 = CreateTroubleshootTextBlock();
            item3.Inlines.Add(new Run("旧版残留了另一账号的 Gist ID 导致 "));
            item3.Inlines.Add(MakeMonoRun("404", "SemanticDanger"));
            item3.Inlines.Add(new Run(" → 点击「"));
            item3.Inlines.Add(MakeBoldRun("自动查找 / 新建", "AccentText"));
            item3.Inlines.Add(new Run("」一键吸附真实多设备库。"));
            stack.Children.Add(BuildNumberedTroubleshootRow("3", item3, false));

            card.Child = stack;
            return card;
        }

        private static TextBlock CreateTroubleshootTextBlock()
        {
            TextBlock tb = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                LineHeight = 17.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return tb;
        }

        private static UIElement BuildNumberedTroubleshootRow(string numText, TextBlock contentTb, bool showDivider)
        {
            Border row = new Border
            {
                Padding = new Thickness(2, 6, 2, 6),
                BorderThickness = new Thickness(0, 0, 0, showDivider ? 1 : 0)
            };
            if (showDivider)
            {
                row.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            }

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border numBadge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 1, 9, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            numBadge.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
            numBadge.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");

            TextBlock numTb = new TextBlock
            {
                Text = numText,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            numTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
            numBadge.Child = numTb;

            Grid.SetColumn(numBadge, 0);
            g.Children.Add(numBadge);

            Grid.SetColumn(contentTb, 1);
            g.Children.Add(contentTb);

            row.Child = g;
            return row;
        }

        private static UIElement BuildPrivacyCommitmentCard()
        {
            Border card = new Border
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            card.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
            card.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border shieldWrap = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 11, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            shieldWrap.SetResourceReference(Border.BackgroundProperty, "BgCard");
            shieldWrap.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
            shieldWrap.Child = UiIcons.CreateIcon(UiIcons.Shield, 15, "SemanticSuccess");
            Grid.SetColumn(shieldWrap, 0);
            g.Children.Add(shieldWrap);

            StackPanel textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            TextBlock titleTb = new TextBlock
            {
                Text = "隐私安全承诺：本地增量游标 + 系统级凭据加密 + 100% 零正文采集",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            textCol.Children.Add(titleTb);

            TextBlock descTb = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            descTb.Inlines.Add(new Run("采用本地 "));
            descTb.Inlines.Add(MakeMonoRun("ledger.db", "FgPrimary"));
            descTb.Inlines.Add(new Run(" 增量字节游标 + "));
            descTb.Inlines.Add(MakeBoldRun("Windows DPAPI 凭据加密", "FgPrimary"));
            descTb.Inlines.Add(new Run(" + "));
            descTb.Inlines.Add(MakeBoldRun("100% 零对话/代码正文采集", "SemanticSuccess"));
            descTb.Inlines.Add(new Run("（仅汇总日期、模型名与 Token 计数值，绝不触碰任何 Prompt 或源码内容）。"));
            textCol.Children.Add(descTb);

            Grid.SetColumn(textCol, 1);
            g.Children.Add(textCol);

            card.Child = g;
            return card;
        }

        // =====================================================================
        // 底部导航与快捷关闭栏
        // =====================================================================
        private Border BuildFooterBar()
        {
            Border footer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 0, 18, 0)
            };
            footer.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel dock = new DockPanel();
            dock.LastChildFill = false;

            // 右侧操作按钮组
            StackPanel rightBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _switchTabFooterBtn = ModernControlsFactory.CreateButton("查看同步与令牌原理 →", "secondary");
            _switchTabFooterBtn.Margin = new Thickness(0, 0, 8, 0);
            _switchTabFooterBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                SelectTab(_activeTabIndex == 0 ? 1 : 0);
            };
            rightBtns.Children.Add(_switchTabFooterBtn);

            Button closeBtn = ModernControlsFactory.CreateButton("关闭指南", "primary");
            closeBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                Close();
            };
            rightBtns.Children.Add(closeBtn);

            DockPanel.SetDock(rightBtns, Dock.Right);
            dock.Children.Add(rightBtns);

            // 左侧按键小贴士
            StackPanel leftHint = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock tipPrefix = new TextBlock
            {
                Text = "按 ",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            };
            tipPrefix.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            leftHint.Children.Add(tipPrefix);

            leftHint.Children.Add(CreateKeyCap("Esc"));

            TextBlock tipMid = new TextBlock
            {
                Text = " 一键关闭  ·  按 ",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 0)
            };
            tipMid.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            leftHint.Children.Add(tipMid);

            leftHint.Children.Add(CreateKeyCap("1"));

            TextBlock tipSlash = new TextBlock
            {
                Text = "/",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            tipSlash.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            leftHint.Children.Add(tipSlash);

            leftHint.Children.Add(CreateKeyCap("2"));

            TextBlock tipSuffix = new TextBlock
            {
                Text = " 快速切换上方标签页",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            };
            tipSuffix.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            leftHint.Children.Add(tipSuffix);

            DockPanel.SetDock(leftHint, Dock.Left);
            dock.Children.Add(leftHint);

            footer.Child = dock;
            return footer;
        }

        private static Run MakeMonoRun(string text, string foregroundResourceKey)
        {
            Run r = new Run(text ?? "");
            r.FontFamily = DesignSystem.FontMono;
            r.FontSize = DesignSystem.TextCaption;
            r.FontWeight = FontWeights.SemiBold;
            if (!string.IsNullOrEmpty(foregroundResourceKey))
            {
                r.SetResourceReference(TextElement.ForegroundProperty, foregroundResourceKey);
            }
            return r;
        }

        private static Run MakeBoldRun(string text, string foregroundResourceKey)
        {
            Run r = new Run(text ?? "");
            r.FontWeight = FontWeights.SemiBold;
            if (!string.IsNullOrEmpty(foregroundResourceKey))
            {
                r.SetResourceReference(TextElement.ForegroundProperty, foregroundResourceKey);
            }
            return r;
        }
    }
}
