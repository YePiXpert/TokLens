using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TokLens
{
    /// <summary>
    /// 单条实时请求深度透视侧栏（Request Forensics Drawer）。
    /// 对标 Helicone / Langfuse / Datadog Trace Inspector，提供双币成本与缓存省钱拆解、
    /// 四维 Token 构成瀑布条、毫秒级延迟与 TPS 吞吐遥测以及一键 JSON 导出与模型过滤。
    /// </summary>
    public sealed class RequestInspectorPanel : Border
    {
        private const double DrawerFixedWidth = 300.0;

        // 四维 Token 专属语义色板（暗色 / 亮色主题自适应）
        private const string ColorFreshDark  = "#38BDF8";
        private const string ColorFreshLight = "#0284C7";
        private const string ColorCrDark     = "#34D399";
        private const string ColorCrLight    = "#059669";
        private const string ColorCwDark     = "#FBBF24";
        private const string ColorCwLight    = "#D97706";
        private const string ColorOutDark    = "#818CF8";
        private const string ColorOutLight   = "#4F46E5";

        private readonly string _homeDir;
        private readonly Action _onCloseRequested;
        private readonly Action<string> _onFilterByModel;

        private PricingEngine _pricing;
        private string _lastPricingStamp;

        private LiveRequestRowView _currentRow;
        private bool _currentIsCny;
        private double _currentUsdToCny;
        private bool _currentIsDark;
        private string _lastInspectedSignature;

        // 顶部栏控件
        private Border _toolBadgeBorder;
        private Ellipse _toolBadgeDot;
        private TextBlock _toolBadgeText;
        private Border _modelPillBorder;
        private TextBlock _modelPillText;
        private Button _closeBtn;

        // 空态与详情主容器切换
        private FrameworkElement _emptyStatePanel;
        private ScrollViewer _detailScrollViewer;
        private StackPanel _detailContentStack;

        // 1. 费用与缓存省钱拆解卡控件
        private Border _priceSourceBadge;
        private TextBlock _priceSourceText;
        private TextBlock _primaryCostText;
        private TextBlock _secondaryCostText;
        private TextBlock _rateInValText;
        private TextBlock _rateOutValText;
        private TextBlock _rateCrValText;
        private TextBlock _rateCwValText;
        private Border _savingsBannerBorder;
        private Path _savingsIconPath;
        private TextBlock _savingsHeadlineText;
        private Border _savingsPctPill;
        private TextBlock _savingsPctText;
        private TextBlock _savingsSubText;

        // 2. 四维 Token 构成瀑布条控件
        private TextBlock _totalTokensBadgeText;
        private Border _waterfallEmptyTrack;
        private Grid _waterfallBarGrid;
        private ColumnDefinition[] _waterfallCols;
        private Border[] _waterfallSegBorders;
        private Ellipse[] _dimColorDots;
        private TextBlock[] _dimCountTexts;
        private TextBlock[] _dimPctTexts;
        private ColumnDefinition[] _dimMiniFillCols;
        private ColumnDefinition[] _dimMiniRestCols;
        private Border[] _dimMiniFillBorders;
        private Border[] _dimRowContainers;
        private TextBlock _contextSummaryText;

        // 3. 性能遥测仪表控件
        private Border _perfTierBadge;
        private Ellipse _perfTierDot;
        private TextBlock _perfTierText;
        private TextBlock _exactTimestampText;
        private TextBlock _durationMainText;
        private TextBlock _durationSubText;
        private TextBlock _tpsMainText;
        private ColumnDefinition _tpsGaugeFillCol;
        private ColumnDefinition _tpsGaugeRestCol;
        private Border _tpsGaugeFillBorder;
        private TextBlock _tpsSubHintText;

        // 4. 底部快捷操作栏控件
        private Border _footerBorder;
        private Button _copyJsonBtn;
        private Path _copyBtnIcon;
        private TextBlock _copyBtnLabel;
        private Button _filterModelBtn;
        private DispatcherTimer _copyFeedbackTimer;

        public LiveRequestRowView SelectedRequest
        {
            get { return _currentRow; }
        }

        public bool HasSelection
        {
            get { return _currentRow != null; }
        }

        public RequestInspectorPanel(string homeDir, Action onCloseRequested, Action<string> onFilterByModel)
        {
            _homeDir = string.IsNullOrWhiteSpace(homeDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDir;
            _onCloseRequested = onCloseRequested;
            _onFilterByModel = onFilterByModel;

            _currentIsCny = true;
            _currentUsdToCny = ExchangeRateService.DefaultRate;
            _currentIsDark = true;
            _lastInspectedSignature = "";

            EnsurePricingEngine();

            Width = DrawerFixedWidth;
            MinWidth = DrawerFixedWidth;
            MaxWidth = DrawerFixedWidth;
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            SetResourceReference(Border.BackgroundProperty, "BgCard");
            SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            _copyFeedbackTimer = new DispatcherTimer();
            _copyFeedbackTimer.Interval = TimeSpan.FromMilliseconds(1600);
            _copyFeedbackTimer.Tick += OnCopyFeedbackTimerTick;

            Unloaded += OnPanelUnloaded;

            Child = BuildRootStructure();
            ApplyEmptyState(_currentIsDark);
        }

        private void OnPanelUnloaded(object sender, RoutedEventArgs e)
        {
            if (_copyFeedbackTimer != null)
            {
                _copyFeedbackTimer.Stop();
            }
        }

        private void EnsurePricingEngine()
        {
            string curStamp = PricingEngine.CurrentUpdatedLabel ?? "";
            if (_pricing == null || !string.Equals(_lastPricingStamp, curStamp, StringComparison.Ordinal))
            {
                _pricing = new PricingEngine(_homeDir);
                _lastPricingStamp = curStamp;
            }
        }

        // ==================== 视图骨架构建 ====================

        private UIElement BuildRootStructure()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement header = BuildHeaderBar();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Grid bodyHost = new Grid();
            _emptyStatePanel = BuildEmptyGuideState();
            _detailScrollViewer = BuildDetailScrollSection();
            bodyHost.Children.Add(_emptyStatePanel);
            bodyHost.Children.Add(_detailScrollViewer);
            Grid.SetRow(bodyHost, 1);
            root.Children.Add(bodyHost);

            _footerBorder = BuildFooterActions();
            Grid.SetRow(_footerBorder, 2);
            root.Children.Add(_footerBorder);

            return root;
        }

        private UIElement BuildHeaderBar()
        {
            Border hdr = new Border();
            hdr.Padding = new Thickness(12, 10, 8, 10);
            hdr.BorderThickness = new Thickness(0, 0, 0, 1);
            hdr.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel stack = new StackPanel();

            // 第一行：工具彩色徽章 + 右侧关闭按钮 ×
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _toolBadgeBorder = new Border();
            _toolBadgeBorder.CornerRadius = new CornerRadius(5);
            _toolBadgeBorder.Padding = new Thickness(8, 3, 8, 3);
            _toolBadgeBorder.BorderThickness = new Thickness(1);
            _toolBadgeBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _toolBadgeBorder.VerticalAlignment = VerticalAlignment.Center;
            _toolBadgeBorder.MaxWidth = 236.0;

            StackPanel toolSp = new StackPanel();
            toolSp.Orientation = Orientation.Horizontal;
            toolSp.VerticalAlignment = VerticalAlignment.Center;

            _toolBadgeDot = new Ellipse();
            _toolBadgeDot.Width = 6.0;
            _toolBadgeDot.Height = 6.0;
            _toolBadgeDot.Margin = new Thickness(0, 0, 6, 0);
            _toolBadgeDot.VerticalAlignment = VerticalAlignment.Center;
            toolSp.Children.Add(_toolBadgeDot);

            _toolBadgeText = new TextBlock();
            _toolBadgeText.Text = "请求链路透视";
            _toolBadgeText.FontFamily = DesignSystem.FontUi;
            _toolBadgeText.FontSize = 11.5;
            _toolBadgeText.FontWeight = FontWeights.SemiBold;
            _toolBadgeText.VerticalAlignment = VerticalAlignment.Center;
            _toolBadgeText.TextTrimming = TextTrimming.CharacterEllipsis;
            toolSp.Children.Add(_toolBadgeText);

            _toolBadgeBorder.Child = toolSp;
            Grid.SetColumn(_toolBadgeBorder, 0);
            topRow.Children.Add(_toolBadgeBorder);

            _closeBtn = ModernControlsFactory.CreateIconGhostButton(
                "M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5",
                "关闭请求透视侧栏");
            _closeBtn.Width = 24.0;
            _closeBtn.Height = 24.0;
            _closeBtn.VerticalAlignment = VerticalAlignment.Center;
            _closeBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_onCloseRequested != null)
                {
                    _onCloseRequested();
                }
            };
            Grid.SetColumn(_closeBtn, 1);
            topRow.Children.Add(_closeBtn);

            stack.Children.Add(topRow);

            // 第二行：模型等宽胶囊
            _modelPillBorder = new Border();
            _modelPillBorder.Margin = new Thickness(0, 7, 4, 0);
            _modelPillBorder.Padding = new Thickness(8, 4, 8, 4);
            _modelPillBorder.CornerRadius = new CornerRadius(5);
            _modelPillBorder.BorderThickness = new Thickness(1);
            _modelPillBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            _modelPillBorder.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _modelPillBorder.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            DockPanel modelDock = new DockPanel();
            modelDock.VerticalAlignment = VerticalAlignment.Center;

            Path modelIcon = UiIcons.CreateIcon(UiIcons.Models, 12.0, "AccentText");
            modelIcon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(modelIcon, Dock.Left);
            modelDock.Children.Add(modelIcon);

            _modelPillText = new TextBlock();
            _modelPillText.FontFamily = DesignSystem.FontMono;
            _modelPillText.FontSize = 11.5;
            _modelPillText.FontWeight = FontWeights.Medium;
            _modelPillText.VerticalAlignment = VerticalAlignment.Center;
            _modelPillText.TextTrimming = TextTrimming.CharacterEllipsis;
            _modelPillText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            modelDock.Children.Add(_modelPillText);

            _modelPillBorder.Child = modelDock;
            stack.Children.Add(_modelPillBorder);

            hdr.Child = stack;
            return hdr;
        }

        private FrameworkElement BuildEmptyGuideState()
        {
            Border wrap = new Border();
            wrap.Padding = new Thickness(20, 28, 20, 24);
            wrap.VerticalAlignment = VerticalAlignment.Center;

            StackPanel sp = new StackPanel();
            sp.HorizontalAlignment = HorizontalAlignment.Center;

            Border iconHalo = new Border();
            iconHalo.Width = 44.0;
            iconHalo.Height = 44.0;
            iconHalo.CornerRadius = new CornerRadius(12);
            iconHalo.BorderThickness = new Thickness(1);
            iconHalo.HorizontalAlignment = HorizontalAlignment.Center;
            iconHalo.Margin = new Thickness(0, 0, 0, 14);
            iconHalo.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            iconHalo.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            iconHalo.Child = UiIcons.CreateIcon(UiIcons.Activity, 20.0, "AccentText");
            sp.Children.Add(iconHalo);

            TextBlock title = new TextBlock();
            title.Text = "单条请求链路深度透视";
            title.FontFamily = DesignSystem.FontUi;
            title.FontSize = 13.0;
            title.FontWeight = FontWeights.SemiBold;
            title.TextAlignment = TextAlignment.Center;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(title);

            TextBlock desc = new TextBlock();
            desc.Text = "在左侧「实时请求流」中点选任意一条对话请求，即可查看双币成本拆解、缓存省钱比例与 Token 瀑布详情。";
            desc.FontFamily = DesignSystem.FontUi;
            desc.FontSize = 11.5;
            desc.LineHeight = 18.0;
            desc.TextAlignment = TextAlignment.Center;
            desc.TextWrapping = TextWrapping.Wrap;
            desc.Margin = new Thickness(0, 6, 0, 16);
            desc.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(desc);

            // 三条微缩特性提示胶囊
            string[] hints = new string[]
            {
                "¥ / $ 双币账单与 Prompt Cache 省钱透视",
                "Fresh / Read / Write / Output 四维瀑布",
                "毫秒级耗时遥测、TPS 评级与 JSON 导出"
            };
            string[] icons = new string[]
            {
                UiIcons.Shield,
                UiIcons.Overview,
                UiIcons.Terminal
            };

            for (int i = 0; i < hints.Length; i++)
            {
                Border item = new Border();
                item.CornerRadius = new CornerRadius(6);
                item.Padding = new Thickness(10, 6, 10, 6);
                item.Margin = new Thickness(0, 0, 0, 6);
                item.BorderThickness = new Thickness(1);
                item.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                item.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

                DockPanel dp = new DockPanel();
                Path ic = UiIcons.CreateIcon(icons[i], 12.0, "FgMuted");
                ic.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(ic, Dock.Left);
                dp.Children.Add(ic);

                TextBlock ht = new TextBlock();
                ht.Text = hints[i];
                ht.FontFamily = DesignSystem.FontUi;
                ht.FontSize = 11.0;
                ht.VerticalAlignment = VerticalAlignment.Center;
                ht.TextTrimming = TextTrimming.CharacterEllipsis;
                ht.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                dp.Children.Add(ht);

                item.Child = dp;
                sp.Children.Add(item);
            }

            wrap.Child = sp;
            return wrap;
        }

        private ScrollViewer BuildDetailScrollSection()
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Focusable = false;

            _detailContentStack = new StackPanel();
            _detailContentStack.Margin = new Thickness(10, 10, 10, 10);

            _detailContentStack.Children.Add(BuildCostAndSavingsCard());
            _detailContentStack.Children.Add(BuildTokenWaterfallCard());
            _detailContentStack.Children.Add(BuildTelemetryCard());

            sv.Content = _detailContentStack;
            return sv;
        }

        // -------------------- 模块 1：单次费用与缓存省钱拆解卡 --------------------

        private UIElement BuildCostAndSavingsCard()
        {
            Border card = CreateSectionSurfaceCard();
            card.Margin = new Thickness(0, 0, 0, 8);

            StackPanel sp = new StackPanel();

            // 标题行 + 价格库来源小徽章
            DockPanel hdr = new DockPanel();
            hdr.Margin = new Thickness(0, 0, 0, 6);

            _priceSourceBadge = new Border();
            _priceSourceBadge.CornerRadius = new CornerRadius(4);
            _priceSourceBadge.Padding = new Thickness(6, 1, 6, 1);
            _priceSourceBadge.BorderThickness = new Thickness(1);
            _priceSourceBadge.VerticalAlignment = VerticalAlignment.Center;
            _priceSourceBadge.SetResourceReference(Border.BackgroundProperty, "BgCard");
            _priceSourceBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            _priceSourceText = new TextBlock();
            _priceSourceText.Text = "内置基准";
            _priceSourceText.FontFamily = DesignSystem.FontUi;
            _priceSourceText.FontSize = 10.5;
            _priceSourceText.FontWeight = FontWeights.Medium;
            _priceSourceText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            _priceSourceBadge.Child = _priceSourceText;
            DockPanel.SetDock(_priceSourceBadge, Dock.Right);
            hdr.Children.Add(_priceSourceBadge);

            TextBlock secTitle = CreateSectionTitle("单次费用与缓存省钱拆解");
            hdr.Children.Add(secTitle);
            sp.Children.Add(hdr);

            // 主副双币对照大字区
            WrapPanel moneyRow = new WrapPanel();
            moneyRow.Orientation = Orientation.Horizontal;
            moneyRow.VerticalAlignment = VerticalAlignment.Bottom;
            moneyRow.Margin = new Thickness(0, 0, 0, 8);

            _primaryCostText = new TextBlock();
            _primaryCostText.Text = "¥0.0000";
            _primaryCostText.FontSize = 21.0;
            _primaryCostText.FontWeight = FontWeights.Bold;
            _primaryCostText.Margin = new Thickness(0, 0, 8, 0);
            _primaryCostText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(_primaryCostText, true);
            moneyRow.Children.Add(_primaryCostText);

            Border secPill = new Border();
            secPill.CornerRadius = new CornerRadius(4);
            secPill.Padding = new Thickness(6, 2, 6, 2);
            secPill.Margin = new Thickness(0, 0, 0, 2);
            secPill.VerticalAlignment = VerticalAlignment.Bottom;
            secPill.SetResourceReference(Border.BackgroundProperty, "BgCard");
            secPill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            secPill.BorderThickness = new Thickness(1);

            _secondaryCostText = new TextBlock();
            _secondaryCostText.Text = "≈ $0.0000 USD";
            _secondaryCostText.FontSize = 11.0;
            _secondaryCostText.FontWeight = FontWeights.Medium;
            _secondaryCostText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            ModernControlsFactory.EnableTabularNumbers(_secondaryCostText, true);
            secPill.Child = _secondaryCostText;
            moneyRow.Children.Add(secPill);

            sp.Children.Add(moneyRow);

            // 模型匹配单价 2x2 精密矩阵
            Border rateGridWrap = new Border();
            rateGridWrap.CornerRadius = new CornerRadius(5);
            rateGridWrap.Padding = new Thickness(8, 6, 8, 6);
            rateGridWrap.Margin = new Thickness(0, 0, 0, 8);
            rateGridWrap.BorderThickness = new Thickness(1);
            rateGridWrap.SetResourceReference(Border.BackgroundProperty, "BgCard");
            rateGridWrap.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            Grid rateGrid = new Grid();
            rateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            rateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rateGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rateGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            rateGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement cellIn = CreateRateCell("输入 In", out _rateInValText);
            Grid.SetRow(cellIn, 0);
            Grid.SetColumn(cellIn, 0);
            rateGrid.Children.Add(cellIn);

            UIElement cellOut = CreateRateCell("输出 Out", out _rateOutValText);
            Grid.SetRow(cellOut, 0);
            Grid.SetColumn(cellOut, 2);
            rateGrid.Children.Add(cellOut);

            UIElement cellCr = CreateRateCell("缓存读 CR", out _rateCrValText);
            Grid.SetRow(cellCr, 2);
            Grid.SetColumn(cellCr, 0);
            rateGrid.Children.Add(cellCr);

            UIElement cellCw = CreateRateCell("缓存写 CW", out _rateCwValText);
            Grid.SetRow(cellCw, 2);
            Grid.SetColumn(cellCw, 2);
            rateGrid.Children.Add(cellCw);

            rateGridWrap.Child = rateGrid;
            sp.Children.Add(rateGridWrap);

            // Prompt Cache 节省金额与节省比例高亮框
            _savingsBannerBorder = new Border();
            _savingsBannerBorder.CornerRadius = new CornerRadius(6);
            _savingsBannerBorder.Padding = new Thickness(8, 7, 8, 7);
            _savingsBannerBorder.BorderThickness = new Thickness(1);
            _savingsBannerBorder.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
            _savingsBannerBorder.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");

            StackPanel savSp = new StackPanel();

            DockPanel savTop = new DockPanel();
            savTop.VerticalAlignment = VerticalAlignment.Center;

            _savingsPctPill = new Border();
            _savingsPctPill.CornerRadius = new CornerRadius(4);
            _savingsPctPill.Padding = new Thickness(5, 1, 5, 1);
            _savingsPctPill.Margin = new Thickness(6, 0, 0, 0);
            _savingsPctPill.VerticalAlignment = VerticalAlignment.Center;
            _savingsPctPill.SetResourceReference(Border.BackgroundProperty, "BgCard");

            _savingsPctText = new TextBlock();
            _savingsPctText.Text = "-0.0%";
            _savingsPctText.FontSize = 11.0;
            _savingsPctText.FontWeight = FontWeights.Bold;
            _savingsPctText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            ModernControlsFactory.EnableTabularNumbers(_savingsPctText, true);
            _savingsPctPill.Child = _savingsPctText;
            DockPanel.SetDock(_savingsPctPill, Dock.Right);
            savTop.Children.Add(_savingsPctPill);

            _savingsIconPath = UiIcons.CreateIcon(UiIcons.Shield, 12.5, "SemanticSuccess");
            _savingsIconPath.Margin = new Thickness(0, 0, 5, 0);
            DockPanel.SetDock(_savingsIconPath, Dock.Left);
            savTop.Children.Add(_savingsIconPath);

            _savingsHeadlineText = new TextBlock();
            _savingsHeadlineText.Text = "缓存省下 ¥0.0000";
            _savingsHeadlineText.FontFamily = DesignSystem.FontUi;
            _savingsHeadlineText.FontSize = 11.5;
            _savingsHeadlineText.FontWeight = FontWeights.SemiBold;
            _savingsHeadlineText.VerticalAlignment = VerticalAlignment.Center;
            _savingsHeadlineText.TextTrimming = TextTrimming.CharacterEllipsis;
            _savingsHeadlineText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            savTop.Children.Add(_savingsHeadlineText);

            savSp.Children.Add(savTop);

            _savingsSubText = new TextBlock();
            _savingsSubText.Text = "对比无缓存全按 Input 计费原价";
            _savingsSubText.FontFamily = DesignSystem.FontUi;
            _savingsSubText.FontSize = 10.5;
            _savingsSubText.Margin = new Thickness(0, 4, 0, 0);
            _savingsSubText.TextTrimming = TextTrimming.CharacterEllipsis;
            _savingsSubText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            savSp.Children.Add(_savingsSubText);

            _savingsBannerBorder.Child = savSp;
            sp.Children.Add(_savingsBannerBorder);

            card.Child = sp;
            return card;
        }

        private UIElement CreateRateCell(string label, out TextBlock valTextOut)
        {
            DockPanel dp = new DockPanel();
            dp.VerticalAlignment = VerticalAlignment.Center;

            valTextOut = new TextBlock();
            valTextOut.Text = "$0/M";
            valTextOut.FontSize = 10.8;
            valTextOut.FontWeight = FontWeights.SemiBold;
            valTextOut.TextAlignment = TextAlignment.Right;
            valTextOut.VerticalAlignment = VerticalAlignment.Center;
            valTextOut.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(valTextOut, true);
            DockPanel.SetDock(valTextOut, Dock.Right);
            dp.Children.Add(valTextOut);

            TextBlock lbl = new TextBlock();
            lbl.Text = label;
            lbl.FontFamily = DesignSystem.FontUi;
            lbl.FontSize = 10.5;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.TextTrimming = TextTrimming.CharacterEllipsis;
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            dp.Children.Add(lbl);

            return dp;
        }

        // -------------------- 模块 2：四维 Token 构成瀑布条 --------------------

        private UIElement BuildTokenWaterfallCard()
        {
            Border card = CreateSectionSurfaceCard();
            card.Margin = new Thickness(0, 0, 0, 8);

            StackPanel sp = new StackPanel();

            // 标题行 + 总 Token 胶囊
            DockPanel hdr = new DockPanel();
            hdr.Margin = new Thickness(0, 0, 0, 8);

            Border totPill = new Border();
            totPill.CornerRadius = new CornerRadius(4);
            totPill.Padding = new Thickness(6, 1, 6, 1);
            totPill.BorderThickness = new Thickness(1);
            totPill.VerticalAlignment = VerticalAlignment.Center;
            totPill.SetResourceReference(Border.BackgroundProperty, "BgCard");
            totPill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            _totalTokensBadgeText = new TextBlock();
            _totalTokensBadgeText.Text = "共 0 tok";
            _totalTokensBadgeText.FontSize = 10.8;
            _totalTokensBadgeText.FontWeight = FontWeights.SemiBold;
            _totalTokensBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(_totalTokensBadgeText, true);
            totPill.Child = _totalTokensBadgeText;
            DockPanel.SetDock(totPill, Dock.Right);
            hdr.Children.Add(totPill);

            hdr.Children.Add(CreateSectionTitle("四维 Token 构成瀑布"));
            sp.Children.Add(hdr);

            // 横向四色分段进度条轨道
            Border barTrack = new Border();
            barTrack.Height = 10.0;
            barTrack.CornerRadius = new CornerRadius(5);
            barTrack.Margin = new Thickness(0, 0, 0, 10);
            barTrack.ClipToBounds = true;
            barTrack.SetResourceReference(Border.BackgroundProperty, "BgCard");

            Grid barHost = new Grid();

            _waterfallEmptyTrack = new Border();
            _waterfallEmptyTrack.CornerRadius = new CornerRadius(5);
            _waterfallEmptyTrack.SetResourceReference(Border.BackgroundProperty, "BorderCard");
            _waterfallEmptyTrack.Opacity = 0.55;
            barHost.Children.Add(_waterfallEmptyTrack);

            _waterfallBarGrid = new Grid();
            _waterfallCols = new ColumnDefinition[4];
            _waterfallSegBorders = new Border[4];

            for (int i = 0; i < 4; i++)
            {
                ColumnDefinition col = new ColumnDefinition();
                col.Width = new GridLength(1, GridUnitType.Star);
                _waterfallCols[i] = col;
                _waterfallBarGrid.ColumnDefinitions.Add(col);

                Border seg = new Border();
                seg.Height = 10.0;
                seg.CornerRadius = new CornerRadius(2);
                seg.SnapsToDevicePixels = true;
                _waterfallSegBorders[i] = seg;
                Grid.SetColumn(seg, i);
                _waterfallBarGrid.Children.Add(seg);
            }
            barHost.Children.Add(_waterfallBarGrid);
            barTrack.Child = barHost;
            sp.Children.Add(barTrack);

            // 四行精确数值与百分比明细
            string[] cnNames = new string[]
            {
                "纯新增输入",
                "缓存命中读取",
                "缓存创建写入",
                "模型生成输出"
            };
            string[] enSubNames = new string[]
            {
                "Fresh Input",
                "Cache Read",
                "Cache Write",
                "Output"
            };

            _dimColorDots = new Ellipse[4];
            _dimCountTexts = new TextBlock[4];
            _dimPctTexts = new TextBlock[4];
            _dimMiniFillCols = new ColumnDefinition[4];
            _dimMiniRestCols = new ColumnDefinition[4];
            _dimMiniFillBorders = new Border[4];
            _dimRowContainers = new Border[4];

            for (int i = 0; i < 4; i++)
            {
                Border rowWrap = new Border();
                rowWrap.CornerRadius = new CornerRadius(4);
                rowWrap.Padding = new Thickness(6, 4, 6, 5);
                rowWrap.Margin = new Thickness(0, 0, 0, i < 3 ? 4 : 0);
                rowWrap.SetResourceReference(Border.BackgroundProperty, "BgCard");
                _dimRowContainers[i] = rowWrap;

                StackPanel rowSp = new StackPanel();

                Grid line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

                Ellipse dot = new Ellipse();
                dot.Width = 7.0;
                dot.Height = 7.0;
                dot.Margin = new Thickness(0, 0, 6, 0);
                dot.VerticalAlignment = VerticalAlignment.Center;
                _dimColorDots[i] = dot;
                Grid.SetColumn(dot, 0);
                line.Children.Add(dot);

                StackPanel nameSp = new StackPanel();
                nameSp.Orientation = Orientation.Horizontal;
                nameSp.VerticalAlignment = VerticalAlignment.Center;

                TextBlock cnTb = new TextBlock();
                cnTb.Text = cnNames[i];
                cnTb.FontFamily = DesignSystem.FontUi;
                cnTb.FontSize = 11.2;
                cnTb.FontWeight = FontWeights.Medium;
                cnTb.VerticalAlignment = VerticalAlignment.Center;
                cnTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                nameSp.Children.Add(cnTb);

                TextBlock enTb = new TextBlock();
                enTb.Text = enSubNames[i];
                enTb.FontFamily = DesignSystem.FontUi;
                enTb.FontSize = 9.8;
                enTb.Margin = new Thickness(4, 1, 0, 0);
                enTb.VerticalAlignment = VerticalAlignment.Center;
                enTb.TextTrimming = TextTrimming.CharacterEllipsis;
                enTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                nameSp.Children.Add(enTb);

                Grid.SetColumn(nameSp, 1);
                line.Children.Add(nameSp);

                TextBlock cntTb = new TextBlock();
                cntTb.Text = "0";
                cntTb.FontSize = 11.2;
                cntTb.FontWeight = FontWeights.SemiBold;
                cntTb.TextAlignment = TextAlignment.Right;
                cntTb.VerticalAlignment = VerticalAlignment.Center;
                cntTb.Margin = new Thickness(6, 0, 0, 0);
                cntTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                ModernControlsFactory.EnableTabularNumbers(cntTb, true);
                _dimCountTexts[i] = cntTb;
                Grid.SetColumn(cntTb, 2);
                line.Children.Add(cntTb);

                TextBlock pctTb = new TextBlock();
                pctTb.Text = "0.0%";
                pctTb.FontSize = 10.8;
                pctTb.TextAlignment = TextAlignment.Right;
                pctTb.VerticalAlignment = VerticalAlignment.Center;
                pctTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                ModernControlsFactory.EnableTabularNumbers(pctTb, true);
                _dimPctTexts[i] = pctTb;
                Grid.SetColumn(pctTb, 3);
                line.Children.Add(pctTb);

                rowSp.Children.Add(line);

                // 单维微缩比例进度线
                Border miniTrack = new Border();
                miniTrack.Height = 2.5;
                miniTrack.CornerRadius = new CornerRadius(1.25);
                miniTrack.Margin = new Thickness(13, 4, 0, 0);
                miniTrack.SetResourceReference(Border.BackgroundProperty, "BgSurface");

                Grid miniGrid = new Grid();
                ColumnDefinition fillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
                ColumnDefinition restCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                _dimMiniFillCols[i] = fillCol;
                _dimMiniRestCols[i] = restCol;
                miniGrid.ColumnDefinitions.Add(fillCol);
                miniGrid.ColumnDefinitions.Add(restCol);

                Border miniFill = new Border();
                miniFill.Height = 2.5;
                miniFill.CornerRadius = new CornerRadius(1.25);
                _dimMiniFillBorders[i] = miniFill;
                Grid.SetColumn(miniFill, 0);
                miniGrid.Children.Add(miniFill);

                miniTrack.Child = miniGrid;
                rowSp.Children.Add(miniTrack);

                rowWrap.Child = rowSp;
                sp.Children.Add(rowWrap);
            }

            // 上下文命中覆盖率小计
            _contextSummaryText = new TextBlock();
            _contextSummaryText.Text = "输入上下文合计 0 · 缓存命中覆盖 0.0%";
            _contextSummaryText.FontFamily = DesignSystem.FontUi;
            _contextSummaryText.FontSize = 10.5;
            _contextSummaryText.Margin = new Thickness(2, 7, 2, 0);
            _contextSummaryText.TextTrimming = TextTrimming.CharacterEllipsis;
            _contextSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(_contextSummaryText);

            card.Child = sp;
            return card;
        }

        // -------------------- 模块 3：性能遥测仪表 --------------------

        private UIElement BuildTelemetryCard()
        {
            Border card = CreateSectionSurfaceCard();

            StackPanel sp = new StackPanel();

            // 标题行 + 性能评级徽章
            DockPanel hdr = new DockPanel();
            hdr.Margin = new Thickness(0, 0, 0, 8);

            _perfTierBadge = ModernControlsFactory.CreateStatusBadge(
                "无耗时遥测",
                "neutral",
                out _perfTierText,
                out _perfTierDot);
            DockPanel.SetDock(_perfTierBadge, Dock.Right);
            hdr.Children.Add(_perfTierBadge);

            hdr.Children.Add(CreateSectionTitle("性能遥测仪表"));
            sp.Children.Add(hdr);

            // 精确日期与毫秒时间栏
            Border tsBox = new Border();
            tsBox.CornerRadius = new CornerRadius(5);
            tsBox.Padding = new Thickness(8, 5, 8, 5);
            tsBox.Margin = new Thickness(0, 0, 0, 8);
            tsBox.BorderThickness = new Thickness(1);
            tsBox.SetResourceReference(Border.BackgroundProperty, "BgCard");
            tsBox.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel tsDock = new DockPanel();
            tsDock.VerticalAlignment = VerticalAlignment.Center;

            Path calIcon = UiIcons.CreateIcon(UiIcons.Calendar, 11.5, "FgMuted");
            calIcon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(calIcon, Dock.Left);
            tsDock.Children.Add(calIcon);

            _exactTimestampText = new TextBlock();
            _exactTimestampText.Text = "—";
            _exactTimestampText.FontSize = 11.0;
            _exactTimestampText.FontWeight = FontWeights.Medium;
            _exactTimestampText.TextAlignment = TextAlignment.Right;
            _exactTimestampText.VerticalAlignment = VerticalAlignment.Center;
            _exactTimestampText.TextTrimming = TextTrimming.CharacterEllipsis;
            _exactTimestampText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(_exactTimestampText, true);
            DockPanel.SetDock(_exactTimestampText, Dock.Right);
            tsDock.Children.Add(_exactTimestampText);

            TextBlock tsLabel = new TextBlock();
            tsLabel.Text = "请求时间";
            tsLabel.FontFamily = DesignSystem.FontUi;
            tsLabel.FontSize = 10.8;
            tsLabel.VerticalAlignment = VerticalAlignment.Center;
            tsLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tsDock.Children.Add(tsLabel);

            tsBox.Child = tsDock;
            sp.Children.Add(tsBox);

            // 双栏核心遥测块：响应总耗时 + 输出生成速率 TPS
            Grid metricsGrid = new Grid();
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左块：响应总耗时
            Border latTile = new Border();
            latTile.CornerRadius = new CornerRadius(6);
            latTile.Padding = new Thickness(9, 7, 9, 8);
            latTile.BorderThickness = new Thickness(1);
            latTile.SetResourceReference(Border.BackgroundProperty, "BgCard");
            latTile.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            StackPanel latSp = new StackPanel();
            TextBlock latHdr = new TextBlock();
            latHdr.Text = "响应总耗时";
            latHdr.FontFamily = DesignSystem.FontUi;
            latHdr.FontSize = 10.5;
            latHdr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            latSp.Children.Add(latHdr);

            _durationMainText = new TextBlock();
            _durationMainText.Text = "—";
            _durationMainText.FontSize = 15.5;
            _durationMainText.FontWeight = FontWeights.Bold;
            _durationMainText.Margin = new Thickness(0, 3, 0, 2);
            _durationMainText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(_durationMainText, true);
            latSp.Children.Add(_durationMainText);

            _durationSubText = new TextBlock();
            _durationSubText.Text = "未记录起止毫秒差";
            _durationSubText.FontFamily = DesignSystem.FontMono;
            _durationSubText.FontSize = 10.2;
            _durationSubText.TextTrimming = TextTrimming.CharacterEllipsis;
            _durationSubText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            latSp.Children.Add(_durationSubText);

            latTile.Child = latSp;
            Grid.SetColumn(latTile, 0);
            metricsGrid.Children.Add(latTile);

            // 右块：输出生成速率 TPS
            Border tpsTile = new Border();
            tpsTile.CornerRadius = new CornerRadius(6);
            tpsTile.Padding = new Thickness(9, 7, 9, 8);
            tpsTile.BorderThickness = new Thickness(1);
            tpsTile.SetResourceReference(Border.BackgroundProperty, "BgCard");
            tpsTile.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            StackPanel tpsSp = new StackPanel();
            TextBlock tpsHdr = new TextBlock();
            tpsHdr.Text = "生成速率 TPS";
            tpsHdr.FontFamily = DesignSystem.FontUi;
            tpsHdr.FontSize = 10.5;
            tpsHdr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tpsSp.Children.Add(tpsHdr);

            _tpsMainText = new TextBlock();
            _tpsMainText.Text = "—";
            _tpsMainText.FontSize = 15.5;
            _tpsMainText.FontWeight = FontWeights.Bold;
            _tpsMainText.Margin = new Thickness(0, 3, 0, 4);
            _tpsMainText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            ModernControlsFactory.EnableTabularNumbers(_tpsMainText, true);
            tpsSp.Children.Add(_tpsMainText);

            // TPS 视觉量程槽（0 ~ 120 tok/s 满格映射）
            Border gaugeTrack = new Border();
            gaugeTrack.Height = 4.0;
            gaugeTrack.CornerRadius = new CornerRadius(2);
            gaugeTrack.SetResourceReference(Border.BackgroundProperty, "BgSurface");

            Grid gaugeGrid = new Grid();
            _tpsGaugeFillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            _tpsGaugeRestCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            gaugeGrid.ColumnDefinitions.Add(_tpsGaugeFillCol);
            gaugeGrid.ColumnDefinitions.Add(_tpsGaugeRestCol);

            _tpsGaugeFillBorder = new Border();
            _tpsGaugeFillBorder.Height = 4.0;
            _tpsGaugeFillBorder.CornerRadius = new CornerRadius(2);
            _tpsGaugeFillBorder.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
            Grid.SetColumn(_tpsGaugeFillBorder, 0);
            gaugeGrid.Children.Add(_tpsGaugeFillBorder);

            gaugeTrack.Child = gaugeGrid;
            tpsSp.Children.Add(gaugeTrack);

            _tpsSubHintText = new TextBlock();
            _tpsSubHintText.Text = "tok/s 吞吐遥测";
            _tpsSubHintText.FontFamily = DesignSystem.FontUi;
            _tpsSubHintText.FontSize = 9.8;
            _tpsSubHintText.Margin = new Thickness(0, 3, 0, 0);
            _tpsSubHintText.TextTrimming = TextTrimming.CharacterEllipsis;
            _tpsSubHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tpsSp.Children.Add(_tpsSubHintText);

            tpsTile.Child = tpsSp;
            Grid.SetColumn(tpsTile, 2);
            metricsGrid.Children.Add(tpsTile);

            sp.Children.Add(metricsGrid);

            card.Child = sp;
            return card;
        }

        // -------------------- 模块 4：底部快捷操作栏 --------------------

        private Border BuildFooterActions()
        {
            Border footer = new Border();
            footer.Padding = new Thickness(10, 8, 10, 10);
            footer.BorderThickness = new Thickness(0, 1, 0, 0);
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // [复制 JSON 详情]
            _copyJsonBtn = ModernControlsFactory.CreateButton("", "secondary");
            _copyJsonBtn.Height = 30.0;
            _copyJsonBtn.Padding = new Thickness(8, 0, 8, 0);
            _copyJsonBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            _copyJsonBtn.ToolTip = "将当前单条请求的完整成本、单价、四维 Token 与遥测详情复制为格式化 JSON";

            StackPanel copySp = new StackPanel();
            copySp.Orientation = Orientation.Horizontal;
            copySp.VerticalAlignment = VerticalAlignment.Center;

            _copyBtnIcon = UiIcons.CreateIcon(UiIcons.Copy, 12.5, null);
            _copyBtnIcon.Margin = new Thickness(0, 0, 5, 0);
            copySp.Children.Add(_copyBtnIcon);

            _copyBtnLabel = new TextBlock();
            _copyBtnLabel.Text = "复制 JSON 详情";
            _copyBtnLabel.FontFamily = DesignSystem.FontUi;
            _copyBtnLabel.FontSize = 11.5;
            _copyBtnLabel.VerticalAlignment = VerticalAlignment.Center;
            copySp.Children.Add(_copyBtnLabel);

            _copyJsonBtn.Content = copySp;
            _copyJsonBtn.Click += OnCopyJsonClicked;
            Grid.SetColumn(_copyJsonBtn, 0);
            g.Children.Add(_copyJsonBtn);

            // [按此模型过滤]
            _filterModelBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Search, "按此模型过滤", "primary");
            _filterModelBtn.Height = 30.0;
            _filterModelBtn.FontSize = 11.5;
            _filterModelBtn.Padding = new Thickness(8, 0, 8, 0);
            _filterModelBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            _filterModelBtn.ToolTip = "在列表中仅筛选当前选中的模型名称";
            _filterModelBtn.Click += OnFilterByModelClicked;
            Grid.SetColumn(_filterModelBtn, 2);
            g.Children.Add(_filterModelBtn);

            footer.Child = g;
            return footer;
        }

        private static Border CreateSectionSurfaceCard()
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(7);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(10, 9, 10, 9);
            b.SnapsToDevicePixels = true;
            b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            return b;
        }

        private static TextBlock CreateSectionTitle(string text)
        {
            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontFamily = DesignSystem.FontUi;
            tb.FontSize = 11.2;
            tb.FontWeight = FontWeights.SemiBold;
            tb.VerticalAlignment = VerticalAlignment.Center;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return tb;
        }

        // ==================== 公开数据绑定与计算逻辑 ====================

        public void SetTheme(bool isDark)
        {
            Inspect(_currentRow, _currentIsCny, _currentUsdToCny, isDark);
        }

        public void SetCurrency(bool isCny, double usdToCny)
        {
            Inspect(_currentRow, isCny, usdToCny, _currentIsDark);
        }

        public void Inspect(LiveRequestRowView row, bool isCny, double usdToCny, bool isDark)
        {
            _currentRow = row;
            _currentIsCny = isCny;
            _currentUsdToCny = SanitizeRate(usdToCny);
            _currentIsDark = isDark;

            ResetCopyFeedbackVisual();

            if (row == null)
            {
                _lastInspectedSignature = "";
                ApplyEmptyState(isDark);
                return;
            }

            EnsurePricingEngine();

            _emptyStatePanel.Visibility = Visibility.Collapsed;
            _detailScrollViewer.Visibility = Visibility.Visible;
            _copyJsonBtn.IsEnabled = true;
            _filterModelBtn.IsEnabled = !string.IsNullOrWhiteSpace(row.Model);

            string toolName = string.IsNullOrWhiteSpace(row.Tool) ? "未知工具" : row.Tool.Trim();
            string modelName = string.IsNullOrWhiteSpace(row.Model) ? "unknown" : row.Model.Trim();

            // 1. 顶部工具品牌彩色徽章与等宽模型胶囊
            ApplyToolBrandBadge(toolName, isDark);
            _modelPillBorder.Visibility = Visibility.Visible;
            _modelPillText.Text = modelName;
            _modelPillBorder.ToolTip = "模型标识: " + modelName + "\n所属工具: " + toolName;

            // 2. 单次费用、匹配单价与 Prompt Cache 节省拆解
            long fi = Math.Max(0L, row.FreshInput);
            long cr = Math.Max(0L, row.CacheRead);
            long cw = Math.Max(0L, row.CacheWrite);
            long outTok = Math.Max(0L, row.Output);
            long totalTok = fi + cr + cw + outTok;

            ModelRate lookupRate = _pricing.Lookup(modelName);
            double actualUsd;
            double baselineUsd;
            ModelRate computedRate;
            _pricing.ComputeCost(modelName, fi, outTok, cr, cw, row.CostUsd, out actualUsd, out baselineUsd, out computedRate);
            if (lookupRate == null)
            {
                lookupRate = computedRate ?? new ModelRate(1.0, 4.0, 0.1, 0.0, "fallback");
            }

            double actualCny = actualUsd * _currentUsdToCny;
            double baselineCny = baselineUsd * _currentUsdToCny;

            if (isCny)
            {
                _primaryCostText.Text = FormatHighPrecisionMoney(actualCny, "¥");
                _secondaryCostText.Text = "≈ " + FormatHighPrecisionMoney(actualUsd, "$") + " USD";
            }
            else
            {
                _primaryCostText.Text = FormatHighPrecisionMoney(actualUsd, "$");
                _secondaryCostText.Text = "≈ " + FormatHighPrecisionMoney(actualCny, "¥") + " CNY";
            }

            _priceSourceText.Text = FormatRateSourceLabel(lookupRate.Source, fi + cr + cw);
            _rateInValText.Text = FormatRatePerMillion(lookupRate.InputPerM);
            _rateOutValText.Text = FormatRatePerMillion(lookupRate.OutputPerM);
            _rateCrValText.Text = lookupRate.CacheReadPerM > 0.0 ? FormatRatePerMillion(lookupRate.CacheReadPerM) : "—";
            _rateCwValText.Text = lookupRate.CacheWritePerM > 0.0 ? FormatRatePerMillion(lookupRate.CacheWritePerM) : "—";

            double savedUsd = Math.Max(0.0, baselineUsd - actualUsd);
            double savedCny = savedUsd * _currentUsdToCny;
            double savedPct = (baselineUsd > 1e-9 && savedUsd > 1e-8)
                ? Math.Min(99.9, savedUsd * 100.0 / baselineUsd)
                : 0.0;

            BindCacheSavingsBanner(fi, cr, cw, baselineUsd, baselineCny, savedUsd, savedCny, savedPct, isCny);

            // 3. 四维 Token 构成瀑布条
            BindTokenWaterfall(fi, cr, cw, outTok, totalTok, lookupRate, isCny, _currentUsdToCny, isDark);

            // 4. 性能遥测仪表（精确日期毫秒时间、耗时、TPS 与性能评级徽章）
            BindTelemetryMetrics(row, outTok);

            // 切换到不同请求时触发平滑微淡入（同一请求 4s 脉冲更新则保持静止无闪烁）
            string sig = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                row.TimestampMs,
                toolName,
                modelName);
            if (!string.Equals(_lastInspectedSignature, sig, StringComparison.Ordinal))
            {
                _lastInspectedSignature = sig;
                PlayGentleRevealIfLoaded();
            }
        }

        private void ApplyEmptyState(bool isDark)
        {
            _emptyStatePanel.Visibility = Visibility.Visible;
            _detailScrollViewer.Visibility = Visibility.Collapsed;

            _modelPillBorder.Visibility = Visibility.Collapsed;
            _copyJsonBtn.IsEnabled = false;
            _filterModelBtn.IsEnabled = false;

            string neutralHex = isDark ? "#818CF8" : "#4F46E5";
            ApplyTintedPillBrush(_toolBadgeBorder, _toolBadgeDot, _toolBadgeText, neutralHex, isDark);
            _toolBadgeText.Text = "请求链路透视";
        }

        private void ApplyToolBrandBadge(string toolName, bool isDark)
        {
            string brandHex = DesignSystem.GetProviderColorHex(toolName, isDark);
            ApplyTintedPillBrush(_toolBadgeBorder, _toolBadgeDot, _toolBadgeText, brandHex, isDark);
            _toolBadgeText.Text = toolName;
        }

        private static void ApplyTintedPillBrush(Border pill, Ellipse dot, TextBlock label, string colorHex, bool isDark)
        {
            Color baseColor;
            try
            {
                baseColor = (Color)ColorConverter.ConvertFromString(colorHex);
            }
            catch
            {
                baseColor = isDark ? Color.FromRgb(129, 140, 248) : Color.FromRgb(79, 70, 229);
            }

            SolidColorBrush fgBrush = DesignSystem.BrushHex(colorHex);
            SolidColorBrush bgBrush = new SolidColorBrush(Color.FromArgb(isDark ? (byte)36 : (byte)24, baseColor.R, baseColor.G, baseColor.B));
            bgBrush.Freeze();
            SolidColorBrush bdBrush = new SolidColorBrush(Color.FromArgb(isDark ? (byte)92 : (byte)68, baseColor.R, baseColor.G, baseColor.B));
            bdBrush.Freeze();

            pill.Background = bgBrush;
            pill.BorderBrush = bdBrush;
            dot.Fill = fgBrush;
            label.Foreground = fgBrush;
        }

        private void BindCacheSavingsBanner(
            long fi,
            long cr,
            long cw,
            double baselineUsd,
            double baselineCny,
            double savedUsd,
            double savedCny,
            double savedPct,
            bool isCny)
        {
            string origPrimary = isCny
                ? FormatHighPrecisionMoney(baselineCny, "¥")
                : FormatHighPrecisionMoney(baselineUsd, "$");
            string origSecondary = isCny
                ? FormatHighPrecisionMoney(baselineUsd, "$")
                : FormatHighPrecisionMoney(baselineCny, "¥");

            if (savedUsd > 1e-7)
            {
                _savingsBannerBorder.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
                _savingsBannerBorder.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                _savingsIconPath.Data = Geometry.Parse(UiIcons.Shield);
                _savingsIconPath.SetResourceReference(Shape.StrokeProperty, "SemanticSuccess");
                _savingsHeadlineText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
                _savingsPctText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");

                string savePrimary = isCny
                    ? FormatHighPrecisionMoney(savedCny, "¥")
                    : FormatHighPrecisionMoney(savedUsd, "$");
                string saveSecondary = isCny
                    ? FormatHighPrecisionMoney(savedUsd, "$")
                    : FormatHighPrecisionMoney(savedCny, "¥");

                _savingsHeadlineText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "Prompt Cache 省下 {0}",
                    savePrimary);
                _savingsPctPill.Visibility = Visibility.Visible;
                _savingsPctText.Text = "-" + savedPct.ToString("F1", CultureInfo.InvariantCulture) + "%";
                _savingsSubText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "折合 {0} · 无缓存原价 {1} ({2})",
                    saveSecondary,
                    origPrimary,
                    origSecondary);
            }
            else if (cw > 0 && cr == 0)
            {
                _savingsBannerBorder.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
                _savingsBannerBorder.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
                _savingsIconPath.Data = Geometry.Parse(UiIcons.Activity);
                _savingsIconPath.SetResourceReference(Shape.StrokeProperty, "AccentText");
                _savingsHeadlineText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                _savingsPctText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");

                _savingsHeadlineText.Text = isCny
                    ? "缓存预热写入轮 · 省 ¥0.0000"
                    : "缓存预热写入轮 · 省 $0.0000";
                _savingsPctPill.Visibility = Visibility.Visible;
                _savingsPctText.Text = "0.0%";
                _savingsSubText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "已建库 {0} tok · 无缓存原价 {1}",
                    FormatterUtil.FormatTokens(cw),
                    origPrimary);
            }
            else
            {
                _savingsBannerBorder.SetResourceReference(Border.BackgroundProperty, "BgCard");
                _savingsBannerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                _savingsIconPath.Data = Geometry.Parse(UiIcons.Shield);
                _savingsIconPath.SetResourceReference(Shape.StrokeProperty, "FgMuted");
                _savingsHeadlineText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                _savingsPctText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

                string zeroStr = isCny ? "¥0.0000" : "$0.0000";
                _savingsHeadlineText.Text = "本次缓存节省 " + zeroStr;
                _savingsPctPill.Visibility = Visibility.Visible;
                _savingsPctText.Text = "0.0%";
                _savingsSubText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "无缓存全按 Input 原价 {0} ({1})",
                    origPrimary,
                    origSecondary);
            }
        }

        private void BindTokenWaterfall(
            long fi,
            long cr,
            long cw,
            long outTok,
            long totalTok,
            ModelRate rate,
            bool isCny,
            double fx,
            bool isDark)
        {
            _totalTokensBadgeText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "共 {0:N0} tok",
                totalTok);

            string[] hexes = isDark
                ? new string[] { ColorFreshDark, ColorCrDark, ColorCwDark, ColorOutDark }
                : new string[] { ColorFreshLight, ColorCrLight, ColorCwLight, ColorOutLight };

            long[] counts = new long[] { fi, cr, cw, outTok };
            double[] unitRates = new double[]
            {
                rate.InputPerM,
                rate.CacheReadPerM,
                rate.CacheWritePerM,
                rate.OutputPerM
            };
            string[] dimTitles = new string[]
            {
                "纯新增输入 (Fresh Input)",
                "缓存命中读取 (Cache Read)",
                "缓存创建写入 (Cache Write)",
                "模型生成输出 (Output)"
            };

            if (totalTok <= 0L)
            {
                _waterfallEmptyTrack.Visibility = Visibility.Visible;
                _waterfallBarGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                _waterfallEmptyTrack.Visibility = Visibility.Collapsed;
                _waterfallBarGrid.Visibility = Visibility.Visible;

                int nonZeroCount = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (counts[i] > 0L) nonZeroCount++;
                }

                int seenNonZero = 0;
                for (int i = 0; i < 4; i++)
                {
                    SolidColorBrush segBrush = DesignSystem.BrushHex(hexes[i]);
                    _waterfallSegBorders[i].Background = segBrush;

                    if (counts[i] > 0L)
                    {
                        seenNonZero++;
                        double ratio = (double)counts[i] / (double)totalTok;
                        _waterfallCols[i].Width = new GridLength(Math.Max(0.001, ratio), GridUnitType.Star);
                        _waterfallSegBorders[i].Visibility = Visibility.Visible;
                        _waterfallSegBorders[i].Margin = new Thickness(
                            0,
                            0,
                            (nonZeroCount > 1 && seenNonZero < nonZeroCount) ? 1.5 : 0.0,
                            0);
                        double pctSeg = ratio * 100.0;
                        _waterfallSegBorders[i].ToolTip = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}: {1:N0} tok ({2:F1}%)",
                            dimTitles[i],
                            counts[i],
                            pctSeg);
                    }
                    else
                    {
                        _waterfallCols[i].Width = new GridLength(0.0, GridUnitType.Pixel);
                        _waterfallSegBorders[i].Visibility = Visibility.Collapsed;
                    }
                }
            }

            // 填充四行精确数值、百分比与单项微缩进度线
            for (int i = 0; i < 4; i++)
            {
                SolidColorBrush dimBrush = DesignSystem.BrushHex(hexes[i]);
                _dimColorDots[i].Fill = dimBrush;
                _dimMiniFillBorders[i].Background = dimBrush;

                long c = counts[i];
                double ratio = totalTok > 0L ? ((double)c / (double)totalTok) : 0.0;
                if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 0.0)
                    ratio = 0.0;
                if (ratio > 1.0)
                    ratio = 1.0;

                double pct = ratio * 100.0;
                _dimCountTexts[i].Text = c.ToString("N0", CultureInfo.InvariantCulture);
                _dimPctTexts[i].Text = pct.ToString("F1", CultureInfo.InvariantCulture) + "%";

                _dimMiniFillCols[i].Width = new GridLength(ratio, GridUnitType.Star);
                _dimMiniRestCols[i].Width = new GridLength(Math.Max(0.0, 1.0 - ratio), GridUnitType.Star);

                double dimUsd = (c * unitRates[i]) / 1000000.0;
                double dimCny = dimUsd * fx;
                _dimRowContainers[i].ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\n精确数量: {1:N0} tok ({2})\n占总请求比: {3:F2}%\n按标准单价折算: {4} / {5}",
                    dimTitles[i],
                    c,
                    FormatterUtil.FormatTokens(c),
                    pct,
                    FormatHighPrecisionMoney(dimCny, "¥"),
                    FormatHighPrecisionMoney(dimUsd, "$"));
            }

            long promptTotal = fi + cr + cw;
            double cacheHitPct = promptTotal > 0L ? (cr * 100.0 / promptTotal) : 0.0;
            _contextSummaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "输入上下文 {0} · 缓存命中率 {1:F1}%",
                FormatterUtil.FormatTokens(promptTotal),
                cacheHitPct);
        }

        private void BindTelemetryMetrics(LiveRequestRowView row, long outTok)
        {
            // 1. 精确日期与毫秒时间
            _exactTimestampText.Text = FormatExactMillisecondTimestamp(row.TimestampMs, row.Date, row.TimeText);

            // 2. 响应总耗时 Duration
            int durMs = row.DurationMs;
            bool validDuration = (durMs >= 150 && durMs <= 600000);
            if (validDuration)
            {
                string dt = !string.IsNullOrWhiteSpace(row.DurationText) && row.DurationText != "—"
                    ? row.DurationText
                    : FormatterUtil.FormatDuration(durMs);
                _durationMainText.Text = dt;
                _durationSubText.Text = string.Format(CultureInfo.InvariantCulture, "{0:N0} ms", durMs);
            }
            else
            {
                _durationMainText.Text = "—";
                _durationSubText.Text = "无起止毫秒差";
            }

            // 3. 输出生成速率 TPS 及性能评级徽章
            double tps = row.Tps > 0.0 ? row.Tps : FormatterUtil.ComputeTps(outTok, durMs);
            if (double.IsNaN(tps) || double.IsInfinity(tps) || tps < 0.0)
                tps = 0.0;

            if (tps > 0.0)
            {
                _tpsMainText.Text = tps.ToString("F1", CultureInfo.InvariantCulture);
                _tpsSubHintText.Text = "tok/s 实时吞吐";

                double gaugeRatio = Math.Min(1.0, tps / 120.0);
                _tpsGaugeFillCol.Width = new GridLength(Math.Max(0.04, gaugeRatio), GridUnitType.Star);
                _tpsGaugeRestCol.Width = new GridLength(Math.Max(0.0, 1.0 - gaugeRatio), GridUnitType.Star);

                if (tps >= 80.0)
                {
                    ApplyStatusBadgeVariant(_perfTierBadge, _perfTierText, _perfTierDot, "极速 ≥80 tok/s", "success");
                    _tpsGaugeFillBorder.SetResourceReference(Border.BackgroundProperty, "SemanticSuccess");
                }
                else if (tps >= 35.0)
                {
                    ApplyStatusBadgeVariant(_perfTierBadge, _perfTierText, _perfTierDot, "流畅 35–80 tok/s", "accent");
                    _tpsGaugeFillBorder.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
                }
                else
                {
                    ApplyStatusBadgeVariant(_perfTierBadge, _perfTierText, _perfTierDot, "深度推理", "warning");
                    _tpsGaugeFillBorder.SetResourceReference(Border.BackgroundProperty, "SemanticWarning");
                }
            }
            else
            {
                _tpsMainText.Text = "—";
                _tpsSubHintText.Text = "单点日志快照";
                _tpsGaugeFillCol.Width = new GridLength(0.0, GridUnitType.Star);
                _tpsGaugeRestCol.Width = new GridLength(1.0, GridUnitType.Star);
                ApplyStatusBadgeVariant(_perfTierBadge, _perfTierText, _perfTierDot, "静态快照", "neutral");
            }
        }

        private static void ApplyStatusBadgeVariant(Border pill, TextBlock label, Ellipse dot, string text, string variant)
        {
            string bgKey = "BgCard";
            string bdKey = "BorderCard";
            string fgKey = "FgMuted";

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticSuccessBg";
                bdKey = "SemanticSuccessBorder";
                fgKey = "SemanticSuccess";
            }
            else if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "AccentSubtleBg";
                bdKey = "AccentSubtleBorder";
                fgKey = "AccentText";
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticWarningBg";
                bdKey = "SemanticWarningBorder";
                fgKey = "SemanticWarning";
            }

            pill.SetResourceReference(Border.BackgroundProperty, bgKey);
            pill.SetResourceReference(Border.BorderBrushProperty, bdKey);
            dot.SetResourceReference(Shape.FillProperty, fgKey);
            label.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            label.Text = text;
        }

        private void PlayGentleRevealIfLoaded()
        {
            if (_detailContentStack == null || !IsLoaded)
            {
                if (_detailContentStack != null)
                {
                    _detailContentStack.Opacity = 1.0;
                }
                return;
            }

            DoubleAnimation fade = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromMilliseconds(140)));
            _detailContentStack.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // ==================== 底部快捷操作回调 ====================

        private void OnCopyJsonClicked(object sender, RoutedEventArgs e)
        {
            if (_currentRow == null) return;

            string json = BuildForensicsJson(_currentRow);
            bool copied = false;
            try
            {
                Clipboard.SetDataObject(json, true);
                copied = true;
            }
            catch
            {
                try
                {
                    Clipboard.SetText(json);
                    copied = true;
                }
                catch { }
            }

            if (copied)
            {
                _copyBtnIcon.Data = Geometry.Parse(UiIcons.Check);
                _copyBtnIcon.SetResourceReference(Shape.StrokeProperty, "SemanticSuccess");
                _copyBtnLabel.Text = "已复制 JSON";
                _copyBtnLabel.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
                _copyFeedbackTimer.Stop();
                _copyFeedbackTimer.Start();
            }
        }

        private void OnCopyFeedbackTimerTick(object sender, EventArgs e)
        {
            _copyFeedbackTimer.Stop();
            ResetCopyFeedbackVisual();
        }

        private void ResetCopyFeedbackVisual()
        {
            if (_copyBtnIcon == null || _copyBtnLabel == null) return;
            _copyBtnIcon.Data = Geometry.Parse(UiIcons.Copy);
            _copyBtnIcon.ClearValue(Shape.StrokeProperty);
            System.Windows.Data.Binding fgBinding = new System.Windows.Data.Binding();
            fgBinding.Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty);
            fgBinding.RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self);
            _copyBtnIcon.SetBinding(Shape.StrokeProperty, fgBinding);

            _copyBtnLabel.Text = "复制 JSON 详情";
            _copyBtnLabel.ClearValue(TextBlock.ForegroundProperty);
        }

        private void OnFilterByModelClicked(object sender, RoutedEventArgs e)
        {
            if (_currentRow == null || string.IsNullOrWhiteSpace(_currentRow.Model))
                return;
            if (_onFilterByModel != null)
            {
                _onFilterByModel(_currentRow.Model);
            }
        }

        // ==================== JSON 序列化与格式化工具 ====================

        private string BuildForensicsJson(LiveRequestRowView row)
        {
            EnsurePricingEngine();

            string tool = string.IsNullOrWhiteSpace(row.Tool) ? "未知工具" : row.Tool.Trim();
            string model = string.IsNullOrWhiteSpace(row.Model) ? "unknown" : row.Model.Trim();
            long fi = Math.Max(0L, row.FreshInput);
            long cr = Math.Max(0L, row.CacheRead);
            long cw = Math.Max(0L, row.CacheWrite);
            long outTok = Math.Max(0L, row.Output);
            long totalTok = fi + cr + cw + outTok;

            ModelRate rate = _pricing.Lookup(model);
            double actualUsd;
            double baselineUsd;
            ModelRate computedRate;
            _pricing.ComputeCost(model, fi, outTok, cr, cw, row.CostUsd, out actualUsd, out baselineUsd, out computedRate);
            if (rate == null)
                rate = computedRate ?? new ModelRate(1.0, 4.0, 0.1, 0.0, "fallback");

            double fx = SanitizeRate(_currentUsdToCny);
            double savedUsd = Math.Max(0.0, baselineUsd - actualUsd);
            double savedPct = (baselineUsd > 1e-9 && savedUsd > 1e-8) ? (savedUsd * 100.0 / baselineUsd) : 0.0;
            double tps = row.Tps > 0.0 ? row.Tps : FormatterUtil.ComputeTps(outTok, row.DurationMs);
            string tier = tps >= 80.0 ? "极速 (>=80 tok/s)" : (tps >= 35.0 ? "流畅 (35-80 tok/s)" : (tps > 0.0 ? "深度推理 (<35 tok/s)" : "静态快照"));
            string exactTime = FormatExactMillisecondTimestamp(row.TimestampMs, row.Date, row.TimeText);

            StringBuilder sb = new StringBuilder(640);
            sb.AppendLine("{");
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"timestamp_ms\": {0},\r\n", row.TimestampMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"timestamp_local\": \"{0}\",\r\n", JsonEscape(exactTime));
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"date\": \"{0}\",\r\n", JsonEscape(row.Date ?? ""));
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"tool\": \"{0}\",\r\n", JsonEscape(tool));
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"model\": \"{0}\",\r\n", JsonEscape(model));
            sb.AppendLine("  \"tokens\": {");
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"fresh_input\": {0},\r\n", fi);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_read\": {0},\r\n", cr);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_write\": {0},\r\n", cw);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"output\": {0},\r\n", outTok);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"total\": {0}\r\n", totalTok);
            sb.AppendLine("  },");
            sb.AppendLine("  \"pricing_per_million_usd\": {");
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"source\": \"{0}\",\r\n", JsonEscape(rate.Source ?? "builtin"));
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"input\": {0:G},\r\n", rate.InputPerM);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"output\": {0:G},\r\n", rate.OutputPerM);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_read\": {0:G},\r\n", rate.CacheReadPerM);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_write\": {0:G}\r\n", rate.CacheWritePerM);
            sb.AppendLine("  },");
            sb.AppendLine("  \"cost\": {");
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"actual_usd\": {0:F6},\r\n", actualUsd);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"actual_cny\": {0:F6},\r\n", actualUsd * fx);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"baseline_no_cache_usd\": {0:F6},\r\n", baselineUsd);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_saved_usd\": {0:F6},\r\n", savedUsd);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_saved_cny\": {0:F6},\r\n", savedUsd * fx);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"cache_saved_percent\": {0:F2},\r\n", savedPct);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"usd_to_cny_rate\": {0:F4}\r\n", fx);
            sb.AppendLine("  },");
            sb.AppendLine("  \"telemetry\": {");
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"duration_ms\": {0},\r\n", row.DurationMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"duration_text\": \"{0}\",\r\n", JsonEscape(FormatterUtil.FormatDuration(row.DurationMs)));
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"tps\": {0:F2},\r\n", tps);
            sb.AppendFormat(CultureInfo.InvariantCulture, "    \"performance_tier\": \"{0}\"\r\n", JsonEscape(tier));
            sb.AppendLine("  }");
            sb.Append("}");
            return sb.ToString();
        }

        private static double SanitizeRate(double rate)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0.01)
                return ExchangeRateService.DefaultRate;
            return rate;
        }

        private static string FormatHighPrecisionMoney(double val, string symbol)
        {
            if (double.IsNaN(val) || double.IsInfinity(val) || val <= 0.0)
                return symbol + "0.0000";
            if (val >= 100.0)
                return symbol + val.ToString("N2", CultureInfo.InvariantCulture);
            if (val >= 1.0)
                return symbol + val.ToString("F3", CultureInfo.InvariantCulture);
            if (val >= 0.001)
                return symbol + val.ToString("F4", CultureInfo.InvariantCulture);
            return symbol + val.ToString("F5", CultureInfo.InvariantCulture);
        }

        private static string FormatRatePerMillion(double perM)
        {
            return string.Format(CultureInfo.InvariantCulture, "${0:G}/M", perM);
        }

        private static string FormatRateSourceLabel(string src, long promptTokens)
        {
            string baseText;
            if (string.Equals(src, "custom", StringComparison.OrdinalIgnoreCase))
                baseText = "自定义单价";
            else if (string.Equals(src, "cloud", StringComparison.OrdinalIgnoreCase))
                baseText = "云端价格库";
            else if (string.Equals(src, "fallback", StringComparison.OrdinalIgnoreCase))
                baseText = "兜底估算价";
            else
                baseText = "内置基准价";

            if (promptTokens > 200000L && !string.Equals(src, "custom", StringComparison.OrdinalIgnoreCase))
                return baseText + " · >200K";
            return baseText;
        }

        private static string FormatExactMillisecondTimestamp(long timestampMs, string fallbackDate, string fallbackTime)
        {
            if (timestampMs > 946684800000L && timestampMs < 4102444800000L)
            {
                try
                {
                    DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(timestampMs);
                    DateTime local = utc.ToLocalTime();
                    return local.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }
                catch { }
            }

            string d = !string.IsNullOrWhiteSpace(fallbackDate) ? fallbackDate.Trim() : "";
            string t = !string.IsNullOrWhiteSpace(fallbackTime) ? fallbackTime.Trim() : "";
            if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(t))
            {
                if (t.IndexOf(d, StringComparison.Ordinal) >= 0)
                    return t;
                return d + " " + t;
            }
            if (!string.IsNullOrEmpty(t)) return t;
            if (!string.IsNullOrEmpty(d)) return d;
            return "—";
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
