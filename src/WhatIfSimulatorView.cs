using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace TokLens
{
    /// <summary>
    /// What-If 模型平替精算沙盒视图（对标 OpenRouter / Helicone 成本模拟实验室）。
    /// 基于用户真实历史的输入 / 输出 / 缓存写入 / 缓存命中四维 Token 结构，
    /// 实时模拟按任意比例将高单价流量切换至平替模型后的净节省金额与全候选模型横向比武。
    /// </summary>
    public sealed class WhatIfSimulatorView : Border
    {
        // ==================== 矩阵表数据绑定行模型（公开嵌套类以支持 WPF 反射绑定） ====================
        public sealed class BenchmarkRowItem
        {
            public string ModelId { get; set; }
            public string ModelDisplay { get; set; }
            public Brush ProviderDotBrush { get; set; }
            public bool IsCurrentTarget { get; set; }
            public string TierLabel { get; set; }
            public double InputPerM { get; set; }
            public string InputRateText { get; set; }
            public double OutputPerM { get; set; }
            public string OutputRateText { get; set; }
            public double CacheReadPerM { get; set; }
            public string CacheReadRateText { get; set; }
            public double SimulatedUsd { get; set; }
            public string SimulatedTotalText { get; set; }
            public double DeltaUsd { get; set; }
            public string DeltaText { get; set; }
            public int DeltaState { get; set; } // -1: 节省(绿), 0: 持平(灰), 1: 增加(红)
            public double ReductionPct { get; set; }
            public double BarRatio { get; set; }
            public string ReductionPctText { get; set; }
            public string RowTooltip { get; set; }
        }

        // ==================== 进度条像素宽度转换器 ====================
        private sealed class SimBarWidthConverter : IValueConverter
        {
            private readonly double _maxWidth;

            public SimBarWidthConverter(double maxWidth)
            {
                _maxWidth = maxWidth;
            }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value == null) return 0.0;
                double r = 0.0;
                try
                {
                    r = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return 0.0;
                }

                if (double.IsNaN(r) || double.IsInfinity(r) || r <= 0.0)
                    return 0.0;

                if (r > 1.0) r = 1.0;
                double px = r * _maxWidth;
                return px < 2.0 ? 2.0 : px;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        // ==================== 内部候选模型元数据与源聚合项 ====================
        private sealed class CandidateModelSpec
        {
            public readonly string ModelId;
            public readonly string DisplayName;
            public readonly string TierLabel;
            public readonly bool ShowInQuickPresets;

            public CandidateModelSpec(string modelId, string displayName, string tierLabel, bool showInQuickPresets)
            {
                ModelId = modelId;
                DisplayName = displayName;
                TierLabel = tierLabel;
                ShowInQuickPresets = showInQuickPresets;
            }
        }

        private sealed class SourceModelSummary
        {
            public string CanonicalKey;
            public string DisplayName;
            public double CostUsd;
            public long TotalTokens;
            public int Turns;
        }

        // ==================== 常量与静态配置 ====================
        private const string ScopeHighCostKey = "__HIGH_COST__";
        private const double ProSubscriptionMonthlyUsd = 20.0;

        private static readonly Regex DateTailStripRegex = new Regex(@"-(?:20\d{6}|20\d{2}-\d{2}-\d{2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ClaudeMinorDotNormalizeRegex = new Regex(@"^(claude-(?:opus|sonnet|haiku|fable|mythos)-\d+)\.(\d+.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly CandidateModelSpec[] CandidateSpecs = new CandidateModelSpec[]
        {
            new CandidateModelSpec("deepseek-v4",       "deepseek-v4",       "极致平替", true),
            new CandidateModelSpec("deepseek-v4.1-flash","deepseek-v4.1-flash","闪电低价", true),
            new CandidateModelSpec("deepseek-v4-pro",   "deepseek-v4-pro",   "深度推理", true),
            new CandidateModelSpec("gemini-4-neon",     "gemini-4-neon",     "高速主力", true),
            new CandidateModelSpec("gemini-4-carbon",   "gemini-4-carbon",   "全能旗舰", true),
            new CandidateModelSpec("gemini-3.1-flash",  "gemini-3.1-flash",  "极速吞吐", true),
            new CandidateModelSpec("claude-sonnet-5-5", "claude-sonnet-5-5", "编码主力", true),
            new CandidateModelSpec("gpt-6.1-terra",     "gpt-6.1-terra",     "均衡编码", true),
            new CandidateModelSpec("gpt-6-luna",        "gpt-6-luna",        "超轻极速", true),
            new CandidateModelSpec("kimi-k3",           "kimi-k3",           "国产编码", true),
            new CandidateModelSpec("glm-5.3-flash",     "glm-5.3-flash",     "普惠闪速", true),
            new CandidateModelSpec("qwen3.5-coder",     "qwen3.5-coder",     "代码专精", true),
            new CandidateModelSpec("glm-5.3",           "glm-5.3",           "智谱旗舰", false),
            new CandidateModelSpec("kimi-k3.1-pro",     "kimi-k3.1-pro",     "Kimi 旗舰", false),
            new CandidateModelSpec("gpt-6.1-sol",       "gpt-6.1-sol",       "OpenAI 旗舰", false),
            new CandidateModelSpec("claude-opus-5-5",   "claude-opus-5-5",   "高阶架构", false)
        };

        private static readonly int[] QuickRatioPresets = new int[] { 25, 50, 75, 100 };

        private static ControlTemplate _cachedPillButtonTemplate;

        // ==================== 运行时状态字段 ====================
        private IList<RecordBucket> _allRecords;
        private PricingEngine _pricing;
        private PricingEngine _fallbackPricing;
        private bool _isCny = true;
        private double _usdToCny = ExchangeRateService.DefaultRate;
        private bool _isDark = true;
        private bool _hasAutoAdjustedInitialPeriod = false;

        // 沙盒交互参数状态
        private int _periodIndex = 1; // 0: 近 7 天, 1: 近 30 天, 2: 全部历史
        private string _selectedSourceKey = ScopeHighCostKey;
        private string _selectedTargetModel = "deepseek-v4";
        private int _migrationPercent = 50; // 5 ~ 100
        private Popup _modelPickerPopover;

        // ==================== UI 控件引用 ====================
        private ScrollViewer _outerScroll;
        private Button[] _periodBtns;
        private WrapPanel _sourcePillsPanel;
        private TextBlock _sourceHintText;
        private WrapPanel _targetPillsPanel;
        private TextBlock _targetRateHintText;
        private Button[] _ratioBtns;

        // 自定义平滑拖拽滑杆元素
        private Grid _sliderHost;
        private Border _sliderFillBar;
        private Border _sliderThumb;
        private bool _isDraggingSlider = false;
        private TextBlock _ratioBadgeText;

        // 四维 Token 负载解剖微条
        private TextBlock _anatTurnsVal;
        private TextBlock _anatFreshVal;
        private TextBlock _anatCacheReadVal;
        private TextBlock _anatCacheWriteVal;
        private TextBlock _anatOutputVal;

        // Bento 卡片 1：当前实际账单
        private TextBlock _card1BadgeLabel;
        private TextBlock _card1MainValue;
        private TextBlock _card1SubText;
        private TextBlock _card1BaselineText;
        private TextBlock _card1CacheSavedText;
        private Border _card1RatioFill;
        private Grid _card1BarTrackGrid;

        // Bento 卡片 2：迁移后模拟账单
        private TextBlock _card2BadgeLabel;
        private TextBlock _card2MainValue;
        private TextBlock _card2SubText;
        private TextBlock _card2RetainedText;
        private TextBlock _card2MigratedText;
        private Border _card2RatioFill;
        private Grid _card2BarTrackGrid;

        // Bento 卡片 3：预计净节省与降幅
        private Border _card3Border;
        private Border _card3BadgePill;
        private Ellipse _card3BadgeDot;
        private TextBlock _card3BadgeLabel;
        private TextBlock _card3TitleText;
        private TextBlock _card3MainValue;
        private TextBlock _card3AnnualDualCurrencyText;
        private Border _card3ProSubPill;
        private Ellipse _card3ProSubDot;
        private TextBlock _card3ProSubText;

        // 底部全候选模型横向大比武矩阵表
        private TextBlock _matrixSubtitleText;
        private DataGrid _matrixGrid;
        private bool _suppressGridSelection = false;

        // 缓存当前周期重算所需快照宽度比
        private double _lastCard1FillRatio = 1.0;
        private double _lastCard2FillRatio = 0.5;

        public WhatIfSimulatorView()
        {
            this.Background = Brushes.Transparent;
            this.SnapsToDevicePixels = true;
            BuildLayout();
        }

        /// <summary>
        /// 渲染并刷新 What-If 模型切换模拟器与全候选横向比武矩阵。
        /// </summary>
        public void RenderSimulator(IList<RecordBucket> allRecords, PricingEngine pricing, bool isCny, double usdToCny, bool isDark)
        {
            _allRecords = allRecords;
            if (pricing != null)
            {
                _pricing = pricing;
            }
            _isCny = isCny;
            _usdToCny = (double.IsNaN(usdToCny) || double.IsInfinity(usdToCny) || usdToCny <= 0.0)
                ? ExchangeRateService.DefaultRate
                : usdToCny;
            _isDark = isDark;

            // 首次注入有效数据时：若默认的「近 30 天」为空但「全部历史」有记录，自动切至「全部历史」避免空白体验
            if (!_hasAutoAdjustedInitialPeriod && _allRecords != null && _allRecords.Count > 0)
            {
                _hasAutoAdjustedInitialPeriod = true;
                int spanDummy;
                List<RecordBucket> p30 = FilterRecordsByPeriod(_allRecords, 1, out spanDummy);
                if (p30.Count == 0)
                {
                    _periodIndex = 2;
                    ModernControlsFactory.UpdateSegmentedGroup(_periodBtns, _periodIndex);
                }
            }

            RefreshAllCalculations();
        }

        // ==================== UI 骨架构建 ====================

        private void BuildLayout()
        {
            _outerScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Padding = new Thickness(0, 0, 4, 16)
            };

            StackPanel rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            // 1. 顶部控制卡
            Border controlCard = BuildTopControlCard();
            controlCard.Margin = new Thickness(0, 0, 0, 12);
            rootStack.Children.Add(controlCard);

            // 2. 中部 3 张精算对比 Bento 卡片
            Grid bentoGrid = BuildMiddleBentoCards();
            bentoGrid.Margin = new Thickness(0, 0, 0, 12);
            rootStack.Children.Add(bentoGrid);

            // 3. 底部全候选模型横向大比武矩阵表
            Border matrixCard = BuildBottomMatrixCard();
            rootStack.Children.Add(matrixCard);

            _outerScroll.Content = rootStack;
            this.Child = _outerScroll;
        }

        private Border BuildTopControlCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(16, 14, 16, 14));
            StackPanel stack = new StackPanel { Orientation = Orientation.Vertical };

            // ---- 顶栏：沙盒标题 + 周期选择胶囊 + 重置按钮 ----
            DockPanel headerDock = new DockPanel { LastChildFill = true };

            StackPanel rightControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(rightControls, Dock.Right);

            Border periodSeg = ModernControlsFactory.CreateSegmentedContainer();
            StackPanel periodHost = (StackPanel)periodSeg.Child;
            string[] periodLabels = new string[] { "近 7 天", "近 30 天", "全部历史" };
            _periodBtns = new Button[periodLabels.Length];
            for (int i = 0; i < periodLabels.Length; i++)
            {
                int capturedIdx = i;
                Button btn = ModernControlsFactory.CreateSegmentedItem(periodLabels[i]);
                btn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_periodIndex == capturedIdx) return;
                    _periodIndex = capturedIdx;
                    ModernControlsFactory.UpdateSegmentedGroup(_periodBtns, _periodIndex);
                    RefreshAllCalculations();
                };
                _periodBtns[i] = btn;
                periodHost.Children.Add(btn);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_periodBtns, _periodIndex);
            rightControls.Children.Add(periodSeg);

            Button resetBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "重置", "ghost");
            resetBtn.Height = 28;
            resetBtn.Margin = new Thickness(8, 0, 0, 0);
            resetBtn.ToolTip = "恢复默认模拟参数（近 30 天 · 全部高单价流量 · deepseek-v4 · 50% 流量迁移）";
            resetBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _periodIndex = 1;
                _selectedSourceKey = ScopeHighCostKey;
                _selectedTargetModel = "deepseek-v4";
                _migrationPercent = 50;
                ModernControlsFactory.UpdateSegmentedGroup(_periodBtns, _periodIndex);
                RefreshAllCalculations();
            };
            rightControls.Children.Add(resetBtn);
            headerDock.Children.Add(rightControls);

            // 左侧标题组
            StackPanel titleLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Border iconBadge = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBadge.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            iconBadge.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            iconBadge.Child = UiIcons.CreateIcon(UiIcons.Models, 15, "AccentText");
            titleLeft.Children.Add(iconBadge);

            StackPanel titleTexts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock mainTitle = new TextBlock
            {
                Text = "What-If 模型平替精算沙盒",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            mainTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleTexts.Children.Add(mainTitle);

            TextBlock subTitle = new TextBlock
            {
                Text = "基于真实历史输入 / 输出 / 缓存写入 / 缓存命中 Token 结构，精确模拟流量切分后的账单降幅",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            titleTexts.Children.Add(subTitle);

            titleLeft.Children.Add(titleTexts);
            headerDock.Children.Add(titleLeft);
            stack.Children.Add(headerDock);

            // ---- 真实负载四维 Token 结构解剖条 ----
            Border anatomyStrip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 12, 0, 12)
            };
            anatomyStrip.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            anatomyStrip.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            WrapPanel anatWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock anatHeader = new TextBlock
            {
                Text = "锁定源负载结构：",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2)
            };
            anatHeader.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            anatWrap.Children.Add(anatHeader);

            anatWrap.Children.Add(CreateAnatomyMetricItem("请求轮次", out _anatTurnsVal));
            anatWrap.Children.Add(CreateAnatomyDivider());
            anatWrap.Children.Add(CreateAnatomyMetricItem("新输入 (Fresh)", out _anatFreshVal));
            anatWrap.Children.Add(CreateAnatomyDivider());
            anatWrap.Children.Add(CreateAnatomyMetricItem("缓存命中 (Read)", out _anatCacheReadVal));
            anatWrap.Children.Add(CreateAnatomyDivider());
            anatWrap.Children.Add(CreateAnatomyMetricItem("缓存写入 (Write)", out _anatCacheWriteVal));
            anatWrap.Children.Add(CreateAnatomyDivider());
            anatWrap.Children.Add(CreateAnatomyMetricItem("生成输出 (Out)", out _anatOutputVal));

            anatomyStrip.Child = anatWrap;
            stack.Children.Add(anatomyStrip);

            // ---- 参数操控三行表单 ----
            Grid formGrid = new Grid();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 第 1 行：源工作负载选择
            TextBlock lblSource = CreateFormRowLabel("源工作负载");
            lblSource.Margin = new Thickness(0, 5, 8, 0);
            Grid.SetRow(lblSource, 0);
            Grid.SetColumn(lblSource, 0);
            formGrid.Children.Add(lblSource);

            StackPanel sourceRightStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _sourcePillsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            sourceRightStack.Children.Add(_sourcePillsPanel);

            _sourceHintText = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                Margin = new Thickness(2, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _sourceHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sourceRightStack.Children.Add(_sourceHintText);

            Grid.SetRow(sourceRightStack, 0);
            Grid.SetColumn(sourceRightStack, 1);
            formGrid.Children.Add(sourceRightStack);

            // 第 2 行：目标平替模型快捷预设胶囊组
            TextBlock lblTarget = CreateFormRowLabel("目标平替模型");
            lblTarget.Margin = new Thickness(0, 5, 8, 0);
            Grid.SetRow(lblTarget, 1);
            Grid.SetColumn(lblTarget, 0);
            formGrid.Children.Add(lblTarget);

            StackPanel targetRightStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _targetPillsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            targetRightStack.Children.Add(_targetPillsPanel);

            _targetRateHintText = new TextBlock
            {
                FontFamily = DesignSystem.FontMono,
                FontSize = 11,
                Margin = new Thickness(2, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _targetRateHintText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            targetRightStack.Children.Add(_targetRateHintText);

            Grid.SetRow(targetRightStack, 1);
            Grid.SetColumn(targetRightStack, 1);
            formGrid.Children.Add(targetRightStack);

            // 第 3 行：流量迁移比例分档胶囊 + 丝滑滑块
            TextBlock lblRatio = CreateFormRowLabel("流量迁移比例");
            lblRatio.VerticalAlignment = VerticalAlignment.Center;
            lblRatio.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetRow(lblRatio, 2);
            Grid.SetColumn(lblRatio, 0);
            formGrid.Children.Add(lblRatio);

            WrapPanel ratioRowWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Border ratioSeg = ModernControlsFactory.CreateSegmentedContainer();
            ratioSeg.Margin = new Thickness(0, 2, 12, 2);
            StackPanel ratioSegHost = (StackPanel)ratioSeg.Child;
            _ratioBtns = new Button[QuickRatioPresets.Length];
            for (int i = 0; i < QuickRatioPresets.Length; i++)
            {
                int pctVal = QuickRatioPresets[i];
                Button rBtn = ModernControlsFactory.CreateSegmentedItem(pctVal.ToString(CultureInfo.InvariantCulture) + "%");
                rBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_migrationPercent == pctVal) return;
                    _migrationPercent = pctVal;
                    SyncRatioControlsVisual();
                    RefreshAllCalculations();
                };
                _ratioBtns[i] = rBtn;
                ratioSegHost.Children.Add(rBtn);
            }
            ratioRowWrap.Children.Add(ratioSeg);

            // 自定义平滑比例拖拽滑杆（5% ~ 100%，5% 步进，零延迟重算）
            UIElement customSlider = BuildCustomRatioSlider();
            ratioRowWrap.Children.Add(customSlider);

            // 精确比例状态胶囊
            Border ratioBadge = new Border
            {
                Height = 24,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(10, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            ratioBadge.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            ratioBadge.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            _ratioBadgeText = new TextBlock
            {
                FontFamily = DesignSystem.FontMono,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(_ratioBadgeText, true);
            _ratioBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            ratioBadge.Child = _ratioBadgeText;
            ratioRowWrap.Children.Add(ratioBadge);

            Grid.SetRow(ratioRowWrap, 2);
            Grid.SetColumn(ratioRowWrap, 1);
            formGrid.Children.Add(ratioRowWrap);

            stack.Children.Add(formGrid);
            card.Child = stack;

            SyncRatioControlsVisual();
            return card;
        }

        private UIElement BuildCustomRatioSlider()
        {
            _sliderHost = new Grid
            {
                Width = 156,
                Height = 26,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "左右拖动或点击任意位置微调流量迁移比例（5% ~ 100%，步进 5%）"
            };
            WindowChrome.SetIsHitTestVisibleInChrome(_sliderHost, true);

            Border sliderTrackBg = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(7, 0, 7, 0)
            };
            sliderTrackBg.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            sliderTrackBg.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            _sliderHost.Children.Add(sliderTrackBg);

            _sliderFillBar = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(7, 0, 0, 0),
                Width = 70
            };
            _sliderFillBar.SetResourceReference(Border.BackgroundProperty, "BtnPrimaryBg");
            _sliderHost.Children.Add(_sliderFillBar);

            _sliderThumb = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(2),
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _sliderThumb.SetResourceReference(Border.BorderBrushProperty, "BtnPrimaryBg");
            _sliderHost.Children.Add(_sliderThumb);

            _sliderHost.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                _isDraggingSlider = true;
                _sliderHost.CaptureMouse();
                UpdateSliderFromPoint(e.GetPosition(_sliderHost));
                e.Handled = true;
            };

            _sliderHost.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_isDraggingSlider) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _isDraggingSlider = false;
                    _sliderHost.ReleaseMouseCapture();
                    return;
                }
                UpdateSliderFromPoint(e.GetPosition(_sliderHost));
            };

            _sliderHost.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (_isDraggingSlider)
                {
                    _isDraggingSlider = false;
                    _sliderHost.ReleaseMouseCapture();
                    e.Handled = true;
                }
            };

            _sliderHost.LostMouseCapture += delegate(object s, MouseEventArgs e)
            {
                _isDraggingSlider = false;
            };

            _sliderHost.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                SyncRatioControlsVisual();
            };

            return _sliderHost;
        }

        private void UpdateSliderFromPoint(Point pt)
        {
            double usableWidth = _sliderHost.ActualWidth - 14.0;
            if (usableWidth <= 1.0) usableWidth = 142.0;

            double rawX = pt.X - 7.0;
            double ratio = rawX / usableWidth;
            if (ratio < 0.05) ratio = 0.05;
            if (ratio > 1.0) ratio = 1.0;

            int snapped = (int)Math.Round(ratio * 20.0) * 5;
            if (snapped < 5) snapped = 5;
            if (snapped > 100) snapped = 100;

            if (snapped != _migrationPercent)
            {
                _migrationPercent = snapped;
                SyncRatioControlsVisual();
                RefreshAllCalculations();
            }
        }

        private void SyncRatioControlsVisual()
        {
            int matchedSegIdx = -1;
            for (int i = 0; i < QuickRatioPresets.Length; i++)
            {
                if (QuickRatioPresets[i] == _migrationPercent)
                {
                    matchedSegIdx = i;
                    break;
                }
            }
            ModernControlsFactory.UpdateSegmentedGroup(_ratioBtns, matchedSegIdx);

            double totalW = (_sliderHost != null && _sliderHost.ActualWidth > 20.0) ? _sliderHost.ActualWidth : 156.0;
            double trackW = Math.Max(10.0, totalW - 14.0);
            double norm = _migrationPercent / 100.0;
            if (norm < 0.0) norm = 0.0;
            if (norm > 1.0) norm = 1.0;

            double fillPx = trackW * norm;
            if (_sliderFillBar != null)
            {
                _sliderFillBar.Width = Math.Max(2.0, fillPx);
            }
            if (_sliderThumb != null)
            {
                _sliderThumb.Margin = new Thickness(fillPx, 0, 0, 0);
            }
            if (_ratioBadgeText != null)
            {
                int remainPct = 100 - _migrationPercent;
                _ratioBadgeText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "切流 {0}% · 原模型保留 {1}%",
                    _migrationPercent,
                    remainPct);
            }
        }

        private Grid BuildMiddleBentoCards()
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---------------- Bento Card 1：当前实际 API 账单 ----------------
            Border card1 = ModernControlsFactory.CreateCard(new Thickness(16, 14, 16, 14));
            StackPanel c1Stack = new StackPanel { Orientation = Orientation.Vertical };

            DockPanel c1Top = new DockPanel();
            Ellipse c1DotDummy;
            Border c1Badge = ModernControlsFactory.CreateStatusBadge("缓存已省 0%", "accent", out _card1BadgeLabel, out c1DotDummy);
            DockPanel.SetDock(c1Badge, Dock.Right);
            c1Top.Children.Add(c1Badge);

            TextBlock c1Title = new TextBlock
            {
                Text = "当前实际 API 账单",
                FontFamily = DesignSystem.FontUi,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            c1Title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            c1Top.Children.Add(c1Title);
            c1Stack.Children.Add(c1Top);

            _card1MainValue = new TextBlock
            {
                Text = "¥0.00",
                FontSize = 25,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_card1MainValue, false);
            _card1MainValue.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            c1Stack.Children.Add(_card1MainValue);

            _card1SubText = new TextBlock
            {
                Text = "折合 $0.00 · 含真实缓存折扣",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _card1SubText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c1Stack.Children.Add(_card1SubText);

            _card1BarTrackGrid = new Grid
            {
                Height = 6,
                Margin = new Thickness(0, 12, 0, 8)
            };
            Border c1TrackBg = new Border { CornerRadius = new CornerRadius(3) };
            c1TrackBg.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _card1BarTrackGrid.Children.Add(c1TrackBg);

            _card1RatioFill = new Border
            {
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            _card1RatioFill.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
            _card1BarTrackGrid.Children.Add(_card1RatioFill);
            _card1BarTrackGrid.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                UpdateBentoBarsWidth();
            };
            c1Stack.Children.Add(_card1BarTrackGrid);

            DockPanel c1Foot = new DockPanel();
            _card1CacheSavedText = new TextBlock
            {
                Text = "缓存已立省 ¥0.00",
                FontFamily = DesignSystem.FontMono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(_card1CacheSavedText, true);
            _card1CacheSavedText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            DockPanel.SetDock(_card1CacheSavedText, Dock.Right);
            c1Foot.Children.Add(_card1CacheSavedText);

            _card1BaselineText = new TextBlock
            {
                Text = "无缓存原价 ¥0.00",
                FontFamily = DesignSystem.FontMono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_card1BaselineText, true);
            _card1BaselineText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c1Foot.Children.Add(_card1BaselineText);
            c1Stack.Children.Add(c1Foot);

            card1.Child = c1Stack;
            Grid.SetColumn(card1, 0);
            grid.Children.Add(card1);

            // ---------------- Bento Card 2：迁移后模拟账单 ----------------
            Border card2 = ModernControlsFactory.CreateCard(new Thickness(16, 14, 16, 14));
            StackPanel c2Stack = new StackPanel { Orientation = Orientation.Vertical };

            DockPanel c2Top = new DockPanel();
            Ellipse c2DotDummy;
            Border c2Badge = ModernControlsFactory.CreateStatusBadge("50% 切至平替", "default", out _card2BadgeLabel, out c2DotDummy);
            DockPanel.SetDock(c2Badge, Dock.Right);
            c2Top.Children.Add(c2Badge);

            TextBlock c2Title = new TextBlock
            {
                Text = "迁移后模拟账单",
                FontFamily = DesignSystem.FontUi,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            c2Title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            c2Top.Children.Add(c2Title);
            c2Stack.Children.Add(c2Top);

            _card2MainValue = new TextBlock
            {
                Text = "¥0.00",
                FontSize = 25,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_card2MainValue, false);
            _card2MainValue.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            c2Stack.Children.Add(_card2MainValue);

            _card2SubText = new TextBlock
            {
                Text = "折合 $0.00 · 源与目标四维单价混合重算",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _card2SubText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c2Stack.Children.Add(_card2SubText);

            _card2BarTrackGrid = new Grid
            {
                Height = 6,
                Margin = new Thickness(0, 12, 0, 8)
            };
            Border c2TrackBg = new Border { CornerRadius = new CornerRadius(3) };
            c2TrackBg.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _card2BarTrackGrid.Children.Add(c2TrackBg);

            _card2RatioFill = new Border
            {
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            _card2RatioFill.SetResourceReference(Border.BackgroundProperty, "SemanticSuccess");
            _card2BarTrackGrid.Children.Add(_card2RatioFill);
            _card2BarTrackGrid.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                UpdateBentoBarsWidth();
            };
            c2Stack.Children.Add(_card2BarTrackGrid);

            DockPanel c2Foot = new DockPanel();
            _card2MigratedText = new TextBlock
            {
                Text = "平替流量 ¥0.00",
                FontFamily = DesignSystem.FontMono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(_card2MigratedText, true);
            _card2MigratedText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            DockPanel.SetDock(_card2MigratedText, Dock.Right);
            c2Foot.Children.Add(_card2MigratedText);

            _card2RetainedText = new TextBlock
            {
                Text = "保留流量 ¥0.00",
                FontFamily = DesignSystem.FontMono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_card2RetainedText, true);
            _card2RetainedText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            c2Foot.Children.Add(_card2RetainedText);
            c2Stack.Children.Add(c2Foot);

            card2.Child = c2Stack;
            Grid.SetColumn(card2, 2);
            grid.Children.Add(card2);

            // ---------------- Bento Card 3：预计净节省金额与降幅百分比 ----------------
            _card3Border = ModernControlsFactory.CreateCard(new Thickness(16, 14, 16, 14));
            StackPanel c3Stack = new StackPanel { Orientation = Orientation.Vertical };

            DockPanel c3Top = new DockPanel();
            _card3BadgePill = ModernControlsFactory.CreateStatusBadge("↓ 节省 0.0%", "success", out _card3BadgeLabel, out _card3BadgeDot);
            DockPanel.SetDock(_card3BadgePill, Dock.Right);
            c3Top.Children.Add(_card3BadgePill);

            _card3TitleText = new TextBlock
            {
                Text = "预计净节省金额",
                FontFamily = DesignSystem.FontUi,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            _card3TitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            c3Top.Children.Add(_card3TitleText);
            c3Stack.Children.Add(c3Top);

            _card3MainValue = new TextBlock
            {
                Text = "¥0.00",
                FontSize = 25,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_card3MainValue, false);
            _card3MainValue.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            c3Stack.Children.Add(_card3MainValue);

            _card3AnnualDualCurrencyText = new TextBlock
            {
                Text = "相当于每年节省 $0.00 / ¥0.00",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _card3AnnualDualCurrencyText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            c3Stack.Children.Add(_card3AnnualDualCurrencyText);

            _card3ProSubPill = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _card3ProSubPill.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
            _card3ProSubPill.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");

            StackPanel proSubRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _card3ProSubDot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _card3ProSubDot.SetResourceReference(Shape.FillProperty, "SemanticSuccess");
            proSubRow.Children.Add(_card3ProSubDot);

            _card3ProSubText = new TextBlock
            {
                Text = "可覆盖 0.0 个月 Pro 订阅（$20/月）",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _card3ProSubText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            proSubRow.Children.Add(_card3ProSubText);

            _card3ProSubPill.Child = proSubRow;
            c3Stack.Children.Add(_card3ProSubPill);

            _card3Border.Child = c3Stack;
            Grid.SetColumn(_card3Border, 4);
            grid.Children.Add(_card3Border);

            return grid;
        }

        private Border BuildBottomMatrixCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(0));
            card.ClipToBounds = true;

            StackPanel stack = new StackPanel { Orientation = Orientation.Vertical };

            // 矩阵头部标题栏
            Border hdrBar = new Border
            {
                Padding = new Thickness(16, 12, 16, 11),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            hdrBar.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel hdrDock = new DockPanel { LastChildFill = true };

            _matrixSubtitleText = new TextBlock
            {
                Text = "单击任意候选模型行可一键设为上方沙盒目标",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            _matrixSubtitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(_matrixSubtitleText, Dock.Right);
            hdrDock.Children.Add(_matrixSubtitleText);

            StackPanel hdrLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock matTitle = new TextBlock
            {
                Text = "全候选模型横向大比武矩阵",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            matTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            hdrLeft.Children.Add(matTitle);

            TextBlock badgeLabelDummy;
            Ellipse badgeDotDummy;
            Border countPill = ModernControlsFactory.CreateStatusBadge("16 款前沿模型 · 100% 负载重放", "accent", out badgeLabelDummy, out badgeDotDummy);
            countPill.Margin = new Thickness(10, 0, 0, 0);
            hdrLeft.Children.Add(countPill);

            hdrDock.Children.Add(hdrLeft);
            hdrBar.Child = hdrDock;
            stack.Children.Add(hdrBar);

            // 创建现代 DataGrid
            _matrixGrid = ModernDataGridFactory.CreateModernDataGrid();
            _matrixGrid.RowHeight = 36;
            _matrixGrid.Margin = new Thickness(0, 0, 0, 4);
            _matrixGrid.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _matrixGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            // 将内部滚轮平滑冒泡给外层 ScrollViewer，防止鼠标停在表格上无法滚动整页
            _matrixGrid.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                if (!e.Handled && _outerScroll != null)
                {
                    e.Handled = true;
                    MouseWheelEventArgs bubbleArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta);
                    bubbleArg.RoutedEvent = UIElement.MouseWheelEvent;
                    bubbleArg.Source = s;
                    _outerScroll.RaiseEvent(bubbleArg);
                }
            };

            // 1. 候选模型列（品牌圆点 + 模型名 + 当前目标高亮标签）
            _matrixGrid.Columns.Add(BuildModelNameColumn());

            // 2. 定位梯队徽章列
            _matrixGrid.Columns.Add(ModernDataGridFactory.MakeBadgeColumn(
                "定位梯队",
                "TierLabel",
                "TierLabel",
                new DataGridLength(90)));

            // 3. 输入单价列
            _matrixGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _matrixGrid,
                "输入单价 (/1M)",
                "InputRateText",
                "InputPerM",
                new DataGridLength(114),
                true,
                true,
                false));

            // 4. 输出单价列
            _matrixGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _matrixGrid,
                "输出单价 (/1M)",
                "OutputRateText",
                "OutputPerM",
                new DataGridLength(114),
                true,
                true,
                false));

            // 5. 缓存读取单价列
            _matrixGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _matrixGrid,
                "缓存读取 (/1M)",
                "CacheReadRateText",
                "CacheReadPerM",
                new DataGridLength(116),
                true,
                true,
                false));

            // 6. 模拟总账单列
            _matrixGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _matrixGrid,
                "模拟总账单",
                "SimulatedTotalText",
                "SimulatedUsd",
                new DataGridLength(126),
                true,
                true,
                true));

            // 7. 相比当前增减额（动态红绿语义色）
            _matrixGrid.Columns.Add(BuildSemanticDeltaColumn());

            // 8. 成本缩减比例条（动态红绿双语义微进度条 + 等宽百分比）
            _matrixGrid.Columns.Add(BuildSemanticReductionBarColumn());

            // 单击任意行联动切换顶部平替目标模型
            _matrixGrid.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (_suppressGridSelection) return;
                BenchmarkRowItem row = _matrixGrid.SelectedItem as BenchmarkRowItem;
                if (row != null && !string.IsNullOrEmpty(row.ModelId))
                {
                    if (!string.Equals(_selectedTargetModel, row.ModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedTargetModel = row.ModelId;
                        RefreshAllCalculations();
                    }
                }
            };

            stack.Children.Add(_matrixGrid);
            card.Child = stack;
            return card;
        }

        private DataGridTemplateColumn BuildModelNameColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "候选模型";
            col.SortMemberPath = "ModelDisplay";
            col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            col.MinWidth = 190;

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory rowPanel = new FrameworkElementFactory(typeof(StackPanel));
            rowPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            rowPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            rowPanel.SetBinding(FrameworkElement.ToolTipProperty, new Binding("RowTooltip"));

            FrameworkElementFactory dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.SetValue(FrameworkElement.WidthProperty, 7.0);
            dot.SetValue(FrameworkElement.HeightProperty, 7.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetBinding(Shape.FillProperty, new Binding("ProviderDotBrush"));
            rowPanel.AppendChild(dot);

            FrameworkElementFactory nameTxt = new FrameworkElementFactory(typeof(TextBlock));
            nameTxt.SetBinding(TextBlock.TextProperty, new Binding("ModelDisplay"));
            nameTxt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            nameTxt.SetValue(TextBlock.FontSizeProperty, 12.0);
            nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            nameTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            nameTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgPrimary"));
            rowPanel.AppendChild(nameTxt);

            FrameworkElementFactory targetPill = new FrameworkElementFactory(typeof(Border), "TargetTag");
            targetPill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            targetPill.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
            targetPill.SetValue(Border.MarginProperty, new Thickness(8, 0, 0, 0));
            targetPill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            targetPill.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            targetPill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("AccentSubtleBg"));
            targetPill.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("AccentSubtleBorder"));
            targetPill.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            FrameworkElementFactory targetTagTxt = new FrameworkElementFactory(typeof(TextBlock));
            targetTagTxt.SetValue(TextBlock.TextProperty, "当前目标");
            targetTagTxt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            targetTagTxt.SetValue(TextBlock.FontSizeProperty, 10.0);
            targetTagTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            targetTagTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentText"));
            targetPill.AppendChild(targetTagTxt);
            rowPanel.AppendChild(targetPill);

            dt.VisualTree = rowPanel;

            DataTrigger isTargetTrig = new DataTrigger();
            isTargetTrig.Binding = new Binding("IsCurrentTarget");
            isTargetTrig.Value = true;
            isTargetTrig.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "TargetTag"));
            dt.Triggers.Add(isTargetTrig);

            col.CellTemplate = dt;
            return col;
        }

        private DataGridTemplateColumn BuildSemanticDeltaColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "相比当前增减额";
            col.SortMemberPath = "DeltaUsd";
            col.Width = new DataGridLength(128);

            if (_matrixGrid != null && _matrixGrid.ColumnHeaderStyle != null)
            {
                Style hdrRight = new Style(typeof(DataGridColumnHeader), _matrixGrid.ColumnHeaderStyle);
                hdrRight.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
                col.HeaderStyle = hdrRight;
            }

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock), "DeltaTb");
            txt.SetBinding(TextBlock.TextProperty, new Binding("DeltaText"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            txt.SetValue(TextBlock.FontSizeProperty, 12.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            txt.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            txt.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            txt.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgMuted"));
            dt.VisualTree = txt;

            DataTrigger savedTrig = new DataTrigger { Binding = new Binding("DeltaState"), Value = -1 };
            savedTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"), "DeltaTb"));
            dt.Triggers.Add(savedTrig);

            DataTrigger extraTrig = new DataTrigger { Binding = new Binding("DeltaState"), Value = 1 };
            extraTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticDanger"), "DeltaTb"));
            dt.Triggers.Add(extraTrig);

            col.CellTemplate = dt;
            return col;
        }

        private DataGridTemplateColumn BuildSemanticReductionBarColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "成本缩减比例";
            col.SortMemberPath = "ReductionPct";
            col.Width = new DataGridLength(168);

            if (_matrixGrid != null && _matrixGrid.ColumnHeaderStyle != null)
            {
                Style hdrRight = new Style(typeof(DataGridColumnHeader), _matrixGrid.ColumnHeaderStyle);
                hdrRight.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
                col.HeaderStyle = hdrRight;
            }

            const double barMaxW = 56.0;
            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dock.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            FrameworkElementFactory pctTxt = new FrameworkElementFactory(typeof(TextBlock), "PctTb");
            pctTxt.SetValue(DockPanel.DockProperty, Dock.Right);
            pctTxt.SetValue(FrameworkElement.MinWidthProperty, 62.0);
            pctTxt.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            pctTxt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            pctTxt.SetValue(TextBlock.FontSizeProperty, 12.0);
            pctTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            pctTxt.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            pctTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgMuted"));
            pctTxt.SetBinding(TextBlock.TextProperty, new Binding("ReductionPctText"));
            dock.AppendChild(pctTxt);

            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Border));
            track.SetValue(FrameworkElement.WidthProperty, barMaxW);
            track.SetValue(FrameworkElement.HeightProperty, 6.0);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.0));
            track.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            track.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));

            FrameworkElementFactory fill = new FrameworkElementFactory(typeof(Border), "BarFill");
            fill.SetValue(FrameworkElement.HeightProperty, 6.0);
            fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.0));
            fill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            fill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BorderStrong"));

            Binding wBind = new Binding("BarRatio");
            wBind.Converter = new SimBarWidthConverter(barMaxW);
            fill.SetBinding(FrameworkElement.WidthProperty, wBind);

            track.AppendChild(fill);
            dock.AppendChild(track);
            dt.VisualTree = dock;

            DataTrigger savedTrig = new DataTrigger { Binding = new Binding("DeltaState"), Value = -1 };
            savedTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SemanticSuccess"), "BarFill"));
            savedTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"), "PctTb"));
            dt.Triggers.Add(savedTrig);

            DataTrigger extraTrig = new DataTrigger { Binding = new Binding("DeltaState"), Value = 1 };
            extraTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SemanticDanger"), "BarFill"));
            extraTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticDanger"), "PctTb"));
            dt.Triggers.Add(extraTrig);

            col.CellTemplate = dt;
            return col;
        }

        // ==================== 核心精算与 UI 实时刷新引擎 ====================

        private PricingEngine GetActivePricing()
        {
            if (_pricing != null) return _pricing;
            if (_fallbackPricing == null)
                _fallbackPricing = new PricingEngine(null);
            return _fallbackPricing;
        }

        private void RefreshAllCalculations()
        {
            PricingEngine pricing = GetActivePricing();
            int activeSpanDays;
            List<RecordBucket> periodRecords = FilterRecordsByPeriod(_allRecords, _periodIndex, out activeSpanDays);

            // 1. 提取当前周期 Top 5 消耗源模型并统计高单价模型集合
            double totalActualUsd = 0.0;
            double totalBaselineUsd = 0.0;
            Dictionary<string, SourceModelSummary> modelMap = new Dictionary<string, SourceModelSummary>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> highCostKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < periodRecords.Count; i++)
            {
                RecordBucket r = periodRecords[i];
                double rAct, rBase;
                GetBucketActualAndBaselineUsd(pricing, r, out rAct, out rBase);
                totalActualUsd += rAct;
                totalBaselineUsd += rBase;

                string canonKey = CanonicalizeModelKey(r.Model);
                SourceModelSummary sum;
                if (!modelMap.TryGetValue(canonKey, out sum))
                {
                    sum = new SourceModelSummary();
                    sum.CanonicalKey = canonKey;
                    sum.DisplayName = canonKey;
                    modelMap[canonKey] = sum;
                }
                sum.CostUsd += rAct;
                sum.TotalTokens += r.TotalTokens;
                sum.Turns += (r.Turns > 0 ? r.Turns : 1);

                ModelRate srcRate = pricing.Lookup(r.Model);
                if (IsHighUnitPriceModel(srcRate))
                {
                    highCostKeys.Add(canonKey);
                }
            }

            List<SourceModelSummary> topModels = new List<SourceModelSummary>(modelMap.Values);
            topModels.Sort(delegate(SourceModelSummary a, SourceModelSummary b)
            {
                int cmp = b.CostUsd.CompareTo(a.CostUsd);
                if (cmp != 0) return cmp;
                return b.TotalTokens.CompareTo(a.TotalTokens);
            });
            if (topModels.Count > 5)
            {
                topModels.RemoveRange(5, topModels.Count - 5);
            }

            // 校验当前选中的源模型在当前周期是否仍然存在，若不存在则平滑回退至「全部高单价模型流量」
            if (!string.Equals(_selectedSourceKey, ScopeHighCostKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!modelMap.ContainsKey(_selectedSourceKey))
                {
                    _selectedSourceKey = ScopeHighCostKey;
                }
            }

            // 若当前周期无满足高单价阈值的模型，则「全部高单价模型流量」智能覆盖当前全部有消耗的模型
            bool fallbackAllForHighCost = (highCostKeys.Count == 0 && modelMap.Count > 0);

            // 2. 刷新顶部源工作负载胶囊与目标平替预设胶囊
            RebuildSourcePills(topModels, totalActualUsd, highCostKeys, fallbackAllForHighCost);
            RebuildTargetPresetPills(pricing);

            // 3. 逐桶精确模拟迁移后账单（完整保留 FreshInput / Output / CacheRead / CacheWrite 四维结构）
            double migrationRatio = _migrationPercent / 100.0;
            double totalSimulatedUsd = 0.0;
            double retainedPortionUsd = 0.0;
            double migratedTargetPortionUsd = 0.0;
            double scopedOriginalUsd = 0.0;

            int scopedTurns = 0;
            long scopedFi = 0L;
            long scopedCr = 0L;
            long scopedCw = 0L;
            long scopedOut = 0L;

            string normTargetKey = CanonicalizeModelKey(_selectedTargetModel);

            for (int i = 0; i < periodRecords.Count; i++)
            {
                RecordBucket r = periodRecords[i];
                double rAct, rBase;
                GetBucketActualAndBaselineUsd(pricing, r, out rAct, out rBase);

                string canonKey = CanonicalizeModelKey(r.Model);
                bool inScope;
                if (string.Equals(_selectedSourceKey, ScopeHighCostKey, StringComparison.OrdinalIgnoreCase))
                {
                    inScope = fallbackAllForHighCost || highCostKeys.Contains(canonKey);
                }
                else
                {
                    inScope = string.Equals(canonKey, _selectedSourceKey, StringComparison.OrdinalIgnoreCase);
                }

                if (!inScope)
                {
                    totalSimulatedUsd += rAct;
                    retainedPortionUsd += rAct;
                    continue;
                }

                scopedOriginalUsd += rAct;
                scopedTurns += (r.Turns > 0 ? r.Turns : 1);
                scopedFi += r.FreshInput;
                scopedCr += r.CacheRead;
                scopedCw += r.CacheWrite;
                scopedOut += r.Output;

                double fullTargetUsd;
                if (string.Equals(canonKey, normTargetKey, StringComparison.OrdinalIgnoreCase))
                {
                    fullTargetUsd = rAct;
                }
                else
                {
                    fullTargetUsd = SimulateBucketOnTargetModel(pricing, r, _selectedTargetModel);
                }

                double keepPart = rAct * (1.0 - migrationRatio);
                double migPart = fullTargetUsd * migrationRatio;

                retainedPortionUsd += keepPart;
                migratedTargetPortionUsd += migPart;
                totalSimulatedUsd += (keepPart + migPart);
            }

            // 4. 更新四维 Token 解剖条
            UpdateAnatomyStrip(scopedTurns, scopedFi, scopedCr, scopedCw, scopedOut);

            // 5. 更新中部 3 张精算对比 Bento 卡片
            UpdateMiddleBentoCards(
                totalActualUsd,
                totalBaselineUsd,
                scopedOriginalUsd,
                totalSimulatedUsd,
                retainedPortionUsd,
                migratedTargetPortionUsd,
                activeSpanDays);

            // 6. 构建并刷新底部 12 款主流候选模型 100% 负载横向比武矩阵表
            UpdateBenchmarkMatrix(pricing, periodRecords, totalActualUsd);
        }

        private void RebuildSourcePills(
            List<SourceModelSummary> topModels,
            double totalActualUsd,
            HashSet<string> highCostKeys,
            bool fallbackAllForHighCost)
        {
            _sourcePillsPanel.Children.Clear();

            // 第一颗固定胶囊：全部高单价模型流量
            bool isHighActive = string.Equals(_selectedSourceKey, ScopeHighCostKey, StringComparison.OrdinalIgnoreCase);
            Button allHighBtn = CreateSelectablePillButton(
                null,
                "全部高单价模型流量",
                isHighActive,
                "自动锁定输入 >= $1.5/M 或输出 >= $8/M 的主力旗舰模型流量进行降本模拟");
            allHighBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (string.Equals(_selectedSourceKey, ScopeHighCostKey, StringComparison.OrdinalIgnoreCase)) return;
                _selectedSourceKey = ScopeHighCostKey;
                RefreshAllCalculations();
            };
            _sourcePillsPanel.Children.Add(allHighBtn);

            // 紧随列出用户实际消耗 Top 5 模型供单选
            for (int i = 0; i < topModels.Count; i++)
            {
                SourceModelSummary item = topModels[i];
                string capturedKey = item.CanonicalKey;
                bool active = string.Equals(_selectedSourceKey, capturedKey, StringComparison.OrdinalIgnoreCase);
                double sharePct = totalActualUsd > 1e-9 ? (item.CostUsd * 100.0 / totalActualUsd) : 0.0;
                string label = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · {1:0.#}%",
                    item.DisplayName,
                    sharePct);
                string tip = string.Format(
                    CultureInfo.InvariantCulture,
                    "仅迁移 {0} 的真实工作负载\n周期消耗：{1}（{2} 轮 · {3} Tokens）",
                    item.DisplayName,
                    FormatMoney(item.CostUsd, _isCny, _usdToCny),
                    item.Turns.ToString("N0", CultureInfo.InvariantCulture),
                    FormatCompactTokens(item.TotalTokens));

                string dotHex = DesignSystem.GetProviderColorHex(item.DisplayName, _isDark);
                Button mBtn = CreateSelectablePillButton(dotHex, label, active, tip);
                mBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (string.Equals(_selectedSourceKey, capturedKey, StringComparison.OrdinalIgnoreCase)) return;
                    _selectedSourceKey = capturedKey;
                    RefreshAllCalculations();
                };
                _sourcePillsPanel.Children.Add(mBtn);
            }

            // 底部辅助范围说明
            if (totalActualUsd <= 1e-9 && topModels.Count == 0)
            {
                _sourceHintText.Text = "当前所选时间周期内暂无模型消耗记录，可尝试切换至「全部历史」";
            }
            else if (string.Equals(_selectedSourceKey, ScopeHighCostKey, StringComparison.OrdinalIgnoreCase))
            {
                if (fallbackAllForHighCost)
                {
                    _sourceHintText.Text = "当前周期未检测到高溢价旗舰模型，已自动纳入全部活跃模型流量参与平替模拟";
                }
                else
                {
                    _sourceHintText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "已自动锁定 {0} 款高单价主力模型参与模拟，其余轻量背景模型账单保持不变",
                        highCostKeys.Count);
                }
            }
            else
            {
                _sourceHintText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "定向精算模式：仅对「{0}」按比例切流，其余所有模型保持原账单不变",
                    _selectedSourceKey);
            }
        }

        private void RebuildTargetPresetPills(PricingEngine pricing)
        {
            _targetPillsPanel.Children.Clear();
            bool selectedSeen = false;

            for (int i = 0; i < CandidateSpecs.Length; i++)
            {
                CandidateModelSpec spec = CandidateSpecs[i];
                bool isSel = string.Equals(_selectedTargetModel, spec.ModelId, StringComparison.OrdinalIgnoreCase);
                if (!spec.ShowInQuickPresets && !isSel)
                    continue;

                if (isSel) selectedSeen = true;

                ModelRate rate = pricing.Lookup(spec.ModelId);
                string capturedModel = spec.ModelId;
                string dotHex = DesignSystem.GetProviderColorHex(spec.ModelId, _isDark);
                string tip = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}（{1}）\n输入单价：{2} / 1M\n输出单价：{3} / 1M\n缓存命中：{4} / 1M",
                    spec.DisplayName,
                    spec.TierLabel,
                    FormatUnitRate(rate.InputPerM, false, _usdToCny),
                    FormatUnitRate(rate.OutputPerM, false, _usdToCny),
                    FormatUnitRate(rate.CacheReadPerM > 0 ? rate.CacheReadPerM : rate.InputPerM, false, _usdToCny));

                Button btn = CreateSelectablePillButton(dotHex, spec.DisplayName, isSel, tip);
                btn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (string.Equals(_selectedTargetModel, capturedModel, StringComparison.OrdinalIgnoreCase)) return;
                    _selectedTargetModel = capturedModel;
                    RefreshAllCalculations();
                };
                _targetPillsPanel.Children.Add(btn);
            }

            if (!selectedSeen && !string.IsNullOrEmpty(_selectedTargetModel))
            {
                string dotHex = DesignSystem.GetProviderColorHex(_selectedTargetModel, _isDark);
                Button customBtn = CreateSelectablePillButton(dotHex, _selectedTargetModel, true, "当前选中的自定义平替目标模型");
                _targetPillsPanel.Children.Add(customBtn);
            }

            Button morePickerBtn = CreateSelectablePillButton(_isDark ? "#94A3B8" : "#64748B", "+ 自选模型…", false, "从价格库全部模型中实时搜索并指定任意目标平替模型");
            morePickerBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenTargetModelPickerPopover(morePickerBtn, pricing);
            };
            _targetPillsPanel.Children.Add(morePickerBtn);

            ModelRate activeTargetRate = pricing.Lookup(_selectedTargetModel);
            double effCr = activeTargetRate.CacheReadPerM > 0.0 ? activeTargetRate.CacheReadPerM : activeTargetRate.InputPerM;
            _targetRateHintText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "目标实时费率 ({0})：输入 {1}/1M · 输出 {2}/1M · 缓存读取 {3}/1M{4}",
                _selectedTargetModel,
                FormatUnitRate(activeTargetRate.InputPerM, _isCny, _usdToCny),
                FormatUnitRate(activeTargetRate.OutputPerM, _isCny, _usdToCny),
                FormatUnitRate(effCr, _isCny, _usdToCny),
                activeTargetRate.CacheWritePerM > 0.0
                    ? (" · 缓存写入 " + FormatUnitRate(activeTargetRate.CacheWritePerM, _isCny, _usdToCny) + "/1M")
                    : " · 缓存写入免溢价");
        }

        private void OpenTargetModelPickerPopover(UIElement anchor, PricingEngine pricing)
        {
            if (_modelPickerPopover != null && _modelPickerPopover.IsOpen)
            {
                _modelPickerPopover.IsOpen = false;
                return;
            }

            PricingEngine pe = pricing ?? _pricing ?? new PricingEngine("");
            List<string> allModels = pe.GetAllModelIds();

            StackPanel root = new StackPanel { Width = 360 };
            TextBlock hdr = new TextBlock
            {
                Text = string.Format(CultureInfo.InvariantCulture, "从价格库搜索任意模型（共 {0} 款）", allModels.Count),
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            root.Children.Add(hdr);

            TextBox searchBox = ModernControlsFactory.CreateTextInput("", 28);
            searchBox.Margin = new Thickness(0, 0, 0, 8);
            searchBox.ToolTip = "输入关键词过滤模型（如 deepseek / gemini-4 / claude / qwen3.5 / glm-5.3）";
            root.Children.Add(searchBox);

            ScrollViewer listScroll = new ScrollViewer
            {
                MaxHeight = 280,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            StackPanel listHost = new StackPanel();
            listScroll.Content = listHost;
            root.Children.Add(listScroll);

            Action<string> populate = delegate(string q)
            {
                listHost.Children.Clear();
                string needle = (q ?? "").Trim().ToLowerInvariant();
                int added = 0;
                for (int i = 0; i < allModels.Count && added < 80; i++)
                {
                    string mid = allModels[i];
                    if (!string.IsNullOrEmpty(needle) && mid.ToLowerInvariant().IndexOf(needle, StringComparison.Ordinal) < 0)
                        continue;

                    ModelRate r = pe.Lookup(mid);
                    double cr = r.CacheReadPerM > 0.0 ? r.CacheReadPerM : r.InputPerM;
                    Button rowBtn = ModernControlsFactory.CreateButton(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}   ·   入 {1} / 出 {2} / 缓 {3}",
                            mid,
                            FormatUnitRate(r.InputPerM, _isCny, _usdToCny),
                            FormatUnitRate(r.OutputPerM, _isCny, _usdToCny),
                            FormatUnitRate(cr, _isCny, _usdToCny)),
                        string.Equals(_selectedTargetModel, mid, StringComparison.OrdinalIgnoreCase) ? "primary" : "ghost");
                    rowBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
                    rowBtn.Height = 26;
                    rowBtn.FontSize = 11.2;
                    rowBtn.Margin = new Thickness(0, 0, 0, 2);
                    string chosen = mid;
                    rowBtn.Click += delegate(object s, RoutedEventArgs e)
                    {
                        _selectedTargetModel = chosen;
                        if (_modelPickerPopover != null) _modelPickerPopover.IsOpen = false;
                        RefreshAllCalculations();
                    };
                    listHost.Children.Add(rowBtn);
                    added++;
                }
                if (added == 0)
                {
                    TextBlock noneTb = new TextBlock
                    {
                        Text = "未找到匹配模型，请尝试输入 deepseek / claude / gemini / gpt / qwen / glm",
                        FontFamily = DesignSystem.FontUi,
                        FontSize = 11.2,
                        Margin = new Thickness(4, 8, 4, 8),
                        TextWrapping = TextWrapping.Wrap
                    };
                    noneTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                    listHost.Children.Add(noneTb);
                }
            };

            populate("");
            searchBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                populate(searchBox.Text);
            };

            _modelPickerPopover = ModernControlsFactory.CreateModernPopover(anchor, root);
            _modelPickerPopover.IsOpen = true;
            searchBox.Focus();
        }

        private void UpdateAnatomyStrip(int turns, long fi, long cr, long cw, long outTok)
        {
            long totalPrompt = fi + cr + cw;
            double hitPct = totalPrompt > 0L ? (cr * 100.0 / totalPrompt) : 0.0;

            _anatTurnsVal.Text = turns.ToString("N0", CultureInfo.InvariantCulture) + " 轮";
            _anatFreshVal.Text = FormatCompactTokens(fi);
            _anatCacheReadVal.Text = string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.#}%)", FormatCompactTokens(cr), hitPct);
            _anatCacheWriteVal.Text = FormatCompactTokens(cw);
            _anatOutputVal.Text = FormatCompactTokens(outTok);
        }

        private void UpdateMiddleBentoCards(
            double totalActualUsd,
            double totalBaselineUsd,
            double scopedOriginalUsd,
            double totalSimulatedUsd,
            double retainedPortionUsd,
            double migratedTargetPortionUsd,
            int activeSpanDays)
        {
            double cacheSavedUsd = Math.Max(0.0, totalBaselineUsd - totalActualUsd);
            double cacheSavedPct = totalBaselineUsd > 1e-9 ? (cacheSavedUsd * 100.0 / totalBaselineUsd) : 0.0;
            double scopedSharePct = totalActualUsd > 1e-9 ? (scopedOriginalUsd * 100.0 / totalActualUsd) : 0.0;

            // ---- Card 1: 当前实际 API 账单 ----
            _card1BadgeLabel.Text = string.Format(CultureInfo.InvariantCulture, "缓存已省 {0:0.#}%", cacheSavedPct);
            _card1MainValue.Text = FormatMoney(totalActualUsd, _isCny, _usdToCny);
            _card1SubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "折合 {0} · 锁定源负载占 {1:0.#}% ({2})",
                FormatAltCurrency(totalActualUsd, _isCny, _usdToCny),
                scopedSharePct,
                FormatMoney(scopedOriginalUsd, _isCny, _usdToCny));
            _card1BaselineText.Text = "无缓存基准 " + FormatMoney(totalBaselineUsd, _isCny, _usdToCny);
            _card1CacheSavedText.Text = "缓存立省 " + FormatMoney(cacheSavedUsd, _isCny, _usdToCny);
            _lastCard1FillRatio = totalBaselineUsd > 1e-9 ? (totalActualUsd / totalBaselineUsd) : (totalActualUsd > 0 ? 1.0 : 0.0);

            // ---- Card 2: 迁移后模拟账单 ----
            _card2BadgeLabel.Text = string.Format(CultureInfo.InvariantCulture, "{0}% 切至 {1}", _migrationPercent, ShortenModelBadgeName(_selectedTargetModel));
            _card2MainValue.Text = FormatMoney(totalSimulatedUsd, _isCny, _usdToCny);
            _card2SubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "折合 {0} · 保持完整四维读写比混合重算",
                FormatAltCurrency(totalSimulatedUsd, _isCny, _usdToCny));
            _card2RetainedText.Text = "原模型保留 " + FormatMoney(retainedPortionUsd, _isCny, _usdToCny);
            _card2MigratedText.Text = "平替承接 " + FormatMoney(migratedTargetPortionUsd, _isCny, _usdToCny);
            _lastCard2FillRatio = totalActualUsd > 1e-9
                ? Math.Min(1.0, totalSimulatedUsd / totalActualUsd)
                : 0.0;

            UpdateBentoBarsWidth();

            // ---- Card 3: 预计净节省金额与降幅百分比 ----
            double netSavedUsd = totalActualUsd - totalSimulatedUsd;
            double reductionPct = totalActualUsd > 1e-9 ? (netSavedUsd * 100.0 / totalActualUsd) : 0.0;
            int safeDays = activeSpanDays > 0 ? activeSpanDays : 30;
            double annualSavedUsd = (netSavedUsd / (double)safeDays) * 365.0;
            double annualSavedCny = annualSavedUsd * _usdToCny;

            if (totalActualUsd <= 1e-9)
            {
                ApplyCard3SemanticTheme("neutral");
                _card3TitleText.Text = "预计净节省金额";
                _card3BadgeLabel.Text = "等待流量 0.0%";
                _card3MainValue.Text = FormatMoney(0.0, _isCny, _usdToCny);
                _card3AnnualDualCurrencyText.Text = "当前周期暂无消耗记录，下方可浏览全候选模型单价基准";
                _card3ProSubText.Text = "产生 API 调用后自动折算年化收益与 Pro 订阅覆盖月数";
            }
            else if (netSavedUsd >= -1e-7)
            {
                double cleanSavedUsd = Math.Max(0.0, netSavedUsd);
                double cleanPct = Math.Max(0.0, reductionPct);
                double cleanAnnualUsd = Math.Max(0.0, annualSavedUsd);
                double cleanAnnualCny = Math.Max(0.0, annualSavedCny);
                double proMonthsAnnual = cleanAnnualUsd / ProSubscriptionMonthlyUsd;

                ApplyCard3SemanticTheme(cleanSavedUsd > 1e-6 ? "success" : "neutral");
                _card3TitleText.Text = "预计净节省金额";
                _card3BadgeLabel.Text = string.Format(CultureInfo.InvariantCulture, "↓ 降幅 {0:F1}%", cleanPct);
                _card3MainValue.Text = FormatMoney(cleanSavedUsd, _isCny, _usdToCny);
                _card3AnnualDualCurrencyText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "相当于每年节省 {0} / {1}",
                    FormatMoney(cleanAnnualUsd, false, _usdToCny),
                    FormatMoney(cleanAnnualCny > 0 ? cleanAnnualUsd : 0.0, true, _usdToCny));

                if (cleanSavedUsd >= ProSubscriptionMonthlyUsd)
                {
                    double periodMonths = cleanSavedUsd / ProSubscriptionMonthlyUsd;
                    _card3ProSubText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "本周期省出 {0:F1} 个月 Pro 订阅 · 年化可覆盖 {1:F1} 个月",
                        periodMonths,
                        proMonthsAnnual);
                }
                else
                {
                    _card3ProSubText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "年化节省可覆盖 {0:F1} 个月 Pro 订阅（按 $20/月 折算）",
                        proMonthsAnnual);
                }
            }
            else
            {
                // 反向模拟：从低价模型切换到更高单价模型导致账单增加
                double extraUsd = Math.Abs(netSavedUsd);
                double extraPct = Math.Abs(reductionPct);
                double annualExtraUsd = Math.Abs(annualSavedUsd);

                ApplyCard3SemanticTheme("danger");
                _card3TitleText.Text = "预计新增支出（升配溢价）";
                _card3BadgeLabel.Text = string.Format(CultureInfo.InvariantCulture, "↑ 增幅 +{0:F1}%", extraPct);
                _card3MainValue.Text = "+" + FormatMoney(extraUsd, _isCny, _usdToCny);
                _card3AnnualDualCurrencyText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "目标单价高于源模型，折算每年多支出 {0} / {1}",
                    FormatMoney(annualExtraUsd, false, _usdToCny),
                    FormatMoney(annualExtraUsd, true, _usdToCny));
                _card3ProSubText.Text = "提示：选择更轻量的目标平替模型即可实现正向降本";
            }
        }

        private void ApplyCard3SemanticTheme(string mode)
        {
            if (string.Equals(mode, "success", StringComparison.Ordinal))
            {
                _card3Border.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                _card3BadgePill.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
                _card3BadgePill.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                _card3BadgeDot.SetResourceReference(Shape.FillProperty, "SemanticSuccess");
                _card3BadgeLabel.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
                _card3MainValue.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
                _card3ProSubPill.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
                _card3ProSubPill.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                _card3ProSubDot.SetResourceReference(Shape.FillProperty, "SemanticSuccess");
                _card3ProSubText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            }
            else if (string.Equals(mode, "danger", StringComparison.Ordinal))
            {
                _card3Border.SetResourceReference(Border.BorderBrushProperty, "SemanticDangerBorder");
                _card3BadgePill.SetResourceReference(Border.BackgroundProperty, "SemanticDangerBg");
                _card3BadgePill.SetResourceReference(Border.BorderBrushProperty, "SemanticDangerBorder");
                _card3BadgeDot.SetResourceReference(Shape.FillProperty, "SemanticDanger");
                _card3BadgeLabel.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
                _card3MainValue.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
                _card3ProSubPill.SetResourceReference(Border.BackgroundProperty, "SemanticDangerBg");
                _card3ProSubPill.SetResourceReference(Border.BorderBrushProperty, "SemanticDangerBorder");
                _card3ProSubDot.SetResourceReference(Shape.FillProperty, "SemanticDanger");
                _card3ProSubText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
            }
            else
            {
                _card3Border.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                _card3BadgePill.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                _card3BadgePill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                _card3BadgeDot.SetResourceReference(Shape.FillProperty, "FgMuted");
                _card3BadgeLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                _card3MainValue.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                _card3ProSubPill.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                _card3ProSubPill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                _card3ProSubDot.SetResourceReference(Shape.FillProperty, "FgMuted");
                _card3ProSubText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            }
        }

        private void UpdateBentoBarsWidth()
        {
            if (_card1BarTrackGrid != null && _card1RatioFill != null)
            {
                double w1 = _card1BarTrackGrid.ActualWidth;
                if (w1 > 0.0)
                {
                    double r1 = Math.Max(0.0, Math.Min(1.0, _lastCard1FillRatio));
                    _card1RatioFill.Width = r1 > 0.0 ? Math.Max(4.0, w1 * r1) : 0.0;
                }
            }
            if (_card2BarTrackGrid != null && _card2RatioFill != null)
            {
                double w2 = _card2BarTrackGrid.ActualWidth;
                if (w2 > 0.0)
                {
                    double r2 = Math.Max(0.0, Math.Min(1.0, _lastCard2FillRatio));
                    _card2RatioFill.Width = r2 > 0.0 ? Math.Max(4.0, w2 * r2) : 0.0;
                }
            }
        }

        private void UpdateBenchmarkMatrix(PricingEngine pricing, List<RecordBucket> periodRecords, double totalActualUsd)
        {
            List<CandidateModelSpec> matrixSpecs = new List<CandidateModelSpec>(CandidateSpecs.Length + 1);
            bool hasSelectedInMatrix = false;
            for (int c = 0; c < CandidateSpecs.Length; c++)
            {
                if (string.Equals(CandidateSpecs[c].ModelId, _selectedTargetModel, StringComparison.OrdinalIgnoreCase))
                    hasSelectedInMatrix = true;
                matrixSpecs.Add(CandidateSpecs[c]);
            }
            if (!hasSelectedInMatrix && !string.IsNullOrEmpty(_selectedTargetModel))
            {
                matrixSpecs.Insert(0, new CandidateModelSpec(_selectedTargetModel, _selectedTargetModel, "自选目标", false));
            }

            List<BenchmarkRowItem> rows = new List<BenchmarkRowItem>(matrixSpecs.Count);

            long totalPeriodTokens = 0L;
            for (int i = 0; i < periodRecords.Count; i++)
            {
                totalPeriodTokens += periodRecords[i].TotalTokens;
            }

            for (int c = 0; c < matrixSpecs.Count; c++)
            {
                CandidateModelSpec spec = matrixSpecs[c];
                ModelRate rate = pricing.Lookup(spec.ModelId);
                double effCr = rate.CacheReadPerM > 0.0 ? rate.CacheReadPerM : rate.InputPerM;

                double simTotalUsd = 0.0;
                for (int rIdx = 0; rIdx < periodRecords.Count; rIdx++)
                {
                    RecordBucket bucket = periodRecords[rIdx];
                    if (string.Equals(CanonicalizeModelKey(bucket.Model), CanonicalizeModelKey(spec.ModelId), StringComparison.OrdinalIgnoreCase))
                    {
                        double bAct, bBase;
                        GetBucketActualAndBaselineUsd(pricing, bucket, out bAct, out bBase);
                        simTotalUsd += bAct;
                    }
                    else
                    {
                        simTotalUsd += SimulateBucketOnTargetModel(pricing, bucket, spec.ModelId);
                    }
                }

                double deltaUsd = simTotalUsd - totalActualUsd;
                double reductionPct = 0.0;
                int deltaState = 0;
                double barRatio = 0.0;
                string deltaText;
                string pctText;

                if (totalActualUsd <= 1e-9)
                {
                    deltaState = 0;
                    deltaText = "—";
                    pctText = "0.0%";
                    barRatio = 0.0;
                }
                else if (Math.Abs(deltaUsd) <= 1e-6)
                {
                    deltaState = 0;
                    deltaText = "持平";
                    pctText = "0.0%";
                    barRatio = 0.0;
                }
                else if (deltaUsd < 0.0)
                {
                    double savedAbs = Math.Abs(deltaUsd);
                    reductionPct = (savedAbs / totalActualUsd) * 100.0;
                    deltaState = -1;
                    deltaText = "-" + FormatMoney(savedAbs, _isCny, _usdToCny);
                    pctText = string.Format(CultureInfo.InvariantCulture, "-{0:F1}%", reductionPct);
                    barRatio = Math.Min(1.0, reductionPct / 100.0);
                }
                else
                {
                    double extraPct = (deltaUsd / totalActualUsd) * 100.0;
                    reductionPct = -extraPct;
                    deltaState = 1;
                    deltaText = "+" + FormatMoney(deltaUsd, _isCny, _usdToCny);
                    pctText = string.Format(CultureInfo.InvariantCulture, "+{0:F1}%", extraPct);
                    barRatio = Math.Min(1.0, extraPct / 100.0);
                }

                BenchmarkRowItem item = new BenchmarkRowItem();
                item.ModelId = spec.ModelId;
                item.ModelDisplay = spec.DisplayName;
                item.ProviderDotBrush = DesignSystem.BrushHex(DesignSystem.GetProviderColorHex(spec.ModelId, _isDark));
                item.IsCurrentTarget = string.Equals(_selectedTargetModel, spec.ModelId, StringComparison.OrdinalIgnoreCase);
                item.TierLabel = spec.TierLabel;
                item.InputPerM = rate.InputPerM;
                item.InputRateText = FormatUnitRate(rate.InputPerM, _isCny, _usdToCny);
                item.OutputPerM = rate.OutputPerM;
                item.OutputRateText = FormatUnitRate(rate.OutputPerM, _isCny, _usdToCny);
                item.CacheReadPerM = effCr;
                item.CacheReadRateText = FormatUnitRate(effCr, _isCny, _usdToCny);
                item.SimulatedUsd = simTotalUsd;
                item.SimulatedTotalText = FormatMoney(simTotalUsd, _isCny, _usdToCny);
                item.DeltaUsd = deltaUsd;
                item.DeltaText = deltaText;
                item.DeltaState = deltaState;
                item.ReductionPct = reductionPct;
                item.BarRatio = barRatio;
                item.ReductionPctText = pctText;
                item.RowTooltip = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}（{1}）\n官方基准：输入 ${2:0.####} / 输出 ${3:0.####} / 缓存读取 ${4:0.####} (/1M)\n100% 负载重放模拟账单：{5}（{6}）\n点击此行立即设为上方沙盒目标平替模型",
                    spec.DisplayName,
                    spec.TierLabel,
                    rate.InputPerM,
                    rate.OutputPerM,
                    effCr,
                    FormatMoney(simTotalUsd, true, _usdToCny),
                    FormatMoney(simTotalUsd, false, _usdToCny));

                rows.Add(item);
            }

            // 默认按模拟总账单由低到高排列（最省钱模型置顶）
            rows.Sort(delegate(BenchmarkRowItem a, BenchmarkRowItem b)
            {
                int cmp = a.SimulatedUsd.CompareTo(b.SimulatedUsd);
                if (cmp != 0) return cmp;
                return a.InputPerM.CompareTo(b.InputPerM);
            });

            _matrixSubtitleText.Text = totalPeriodTokens > 0L
                ? string.Format(CultureInfo.InvariantCulture, "已将当前周期 {0} 真实负载 100% 重放对比 · 单击任意行可设为模拟目标", FormatCompactTokens(totalPeriodTokens))
                : "当前周期无流量，以下展示各候选模型实时单价基准 · 单击任意行可设为模拟目标";

            _suppressGridSelection = true;
            try
            {
                bool hadPriorItems = (_matrixGrid.ItemsSource != null);
                ModernDataGridFactory.UpdateItemsPreservingState(
                    _matrixGrid,
                    rows,
                    delegate(object obj)
                    {
                        BenchmarkRowItem br = obj as BenchmarkRowItem;
                        return br != null ? br.ModelId : null;
                    },
                    true);

                if (!hadPriorItems && _matrixGrid.Columns.Count > 5)
                {
                    _matrixGrid.Columns[5].SortDirection = ListSortDirection.Ascending;
                }

                for (int i = 0; i < _matrixGrid.Items.Count; i++)
                {
                    BenchmarkRowItem br = _matrixGrid.Items[i] as BenchmarkRowItem;
                    if (br != null && br.IsCurrentTarget)
                    {
                        _matrixGrid.SelectedItem = br;
                        break;
                    }
                }
            }
            finally
            {
                _suppressGridSelection = false;
            }
        }

        // ==================== 定价模拟精算与数据过滤辅助方法 ====================

        /// <summary>
        /// 将单个历史记录桶完整重放到指定目标模型上。
        /// 精确对齐跨厂商缓存口径：若目标模型无单独的 CacheWrite 溢价（CacheWritePerM == 0），
        /// 原记录中的首次缓存写入 Token 按目标模型常规输入单价计费，绝不漏算！
        /// </summary>
        private static double SimulateBucketOnTargetModel(PricingEngine pricing, RecordBucket r, string targetModelId)
        {
            if (r == null || r.TotalTokens <= 0L) return 0.0;

            ModelRate targetRate = pricing.Lookup(targetModelId);
            long simFi = r.FreshInput;
            long simCr = r.CacheRead;
            long simCw = r.CacheWrite;

            if (targetRate.CacheWritePerM <= 0.0 && simCw > 0L)
            {
                simFi += simCw;
                simCw = 0L;
            }
            if (targetRate.CacheReadPerM <= 0.0 && simCr > 0L)
            {
                simFi += simCr;
                simCr = 0L;
            }

            double simActualUsd, simBaseUsd;
            ModelRate appliedRate;
            pricing.ComputeBucketCost(
                targetModelId,
                r.Turns > 0 ? r.Turns : 1,
                simFi,
                r.Output,
                simCr,
                simCw,
                0.0,
                out simActualUsd,
                out simBaseUsd,
                out appliedRate);

            return simActualUsd;
        }

        private static void GetBucketActualAndBaselineUsd(PricingEngine pricing, RecordBucket r, out double actualUsd, out double baselineUsd)
        {
            actualUsd = 0.0;
            baselineUsd = 0.0;
            if (r == null) return;

            if (r.CostUsd > 0.0 && !double.IsNaN(r.CostUsd) && !double.IsInfinity(r.CostUsd))
            {
                actualUsd = r.CostUsd;
                baselineUsd = (r.BaselineUsd > actualUsd && !double.IsNaN(r.BaselineUsd) && !double.IsInfinity(r.BaselineUsd))
                    ? r.BaselineUsd
                    : actualUsd;
                return;
            }

            if (r.TotalTokens > 0L)
            {
                ModelRate rateDummy;
                pricing.ComputeBucketCost(
                    r.Model,
                    r.Turns > 0 ? r.Turns : 1,
                    r.FreshInput,
                    r.Output,
                    r.CacheRead,
                    r.CacheWrite,
                    0.0,
                    out actualUsd,
                    out baselineUsd,
                    out rateDummy);
            }
        }

        private static bool IsHighUnitPriceModel(ModelRate rate)
        {
            if (rate == null) return false;
            return rate.InputPerM >= 1.5 || rate.OutputPerM >= 8.0;
        }

        private static string CanonicalizeModelKey(string rawModel)
        {
            string norm = PricingEngine.NormalizeModelId(rawModel, "unknown");
            string noDate = DateTailStripRegex.Replace(norm, "");
            Match m = ClaudeMinorDotNormalizeRegex.Match(noDate);
            if (m.Success)
            {
                noDate = m.Groups[1].Value + "-" + m.Groups[2].Value;
            }
            return string.IsNullOrEmpty(noDate) ? "unknown" : noDate;
        }

        private static List<RecordBucket> FilterRecordsByPeriod(IList<RecordBucket> allRecords, int periodIdx, out int spanDays)
        {
            List<RecordBucket> result = new List<RecordBucket>();
            if (periodIdx == 0) spanDays = 7;
            else if (periodIdx == 1) spanDays = 30;
            else spanDays = 30;

            if (allRecords == null || allRecords.Count == 0)
                return result;

            DateTime today = DateTime.Now.Date;
            DateTime cutoff = DateTime.MinValue;
            if (periodIdx == 0)
            {
                cutoff = today.AddDays(-6);
            }
            else if (periodIdx == 1)
            {
                cutoff = today.AddDays(-29);
            }

            DateTime minDate = DateTime.MaxValue;
            DateTime maxDate = DateTime.MinValue;

            for (int i = 0; i < allRecords.Count; i++)
            {
                RecordBucket r = allRecords[i];
                if (r == null) continue;
                if (r.TotalTokens <= 0L && r.CostUsd <= 0.0) continue;

                DateTime recDate;
                bool hasValidDate = TryParseRecordDate(r.Date, out recDate);

                if (periodIdx != 2)
                {
                    if (!hasValidDate || recDate < cutoff)
                        continue;
                }

                result.Add(r);
                if (hasValidDate)
                {
                    if (recDate < minDate) minDate = recDate;
                    if (recDate > maxDate) maxDate = recDate;
                }
            }

            if (periodIdx == 2 && minDate <= maxDate && minDate != DateTime.MaxValue)
            {
                DateTime endAnchor = today > maxDate ? today : maxDate;
                int diff = (int)(endAnchor - minDate).TotalDays + 1;
                spanDays = diff < 1 ? 1 : diff;
            }

            return result;
        }

        private static bool TryParseRecordDate(string dateStr, out DateTime parsed)
        {
            parsed = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(dateStr)) return false;
            string s = dateStr.Trim();
            if (s.Length > 10) s = s.Substring(0, 10);
            return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }

        // ==================== UI 视觉小组件工厂与格式化工具 ====================

        private static TextBlock CreateFormRowLabel(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = 12,
                FontWeight = FontWeights.Medium
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return tb;
        }

        private static StackPanel CreateAnatomyMetricItem(string label, out TextBlock valueOut)
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };

            TextBlock lbl = new TextBlock
            {
                Text = label + " ",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(lbl);

            valueOut = new TextBlock
            {
                Text = "0",
                FontFamily = DesignSystem.FontMono,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(valueOut, true);
            valueOut.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(valueOut);

            return sp;
        }

        private static Border CreateAnatomyDivider()
        {
            Border div = new Border
            {
                Width = 1,
                Height = 12,
                Margin = new Thickness(10, 2, 10, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            div.SetResourceReference(Border.BackgroundProperty, "BorderStrong");
            return div;
        }

        private static ControlTemplate GetOrCreatePillButtonTemplate()
        {
            if (_cachedPillButtonTemplate != null)
                return _cachedPillButtonTemplate;

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "PillBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.92, "PillBd"));
            tpl.Triggers.Add(hover);

            _cachedPillButtonTemplate = tpl;
            return _cachedPillButtonTemplate;
        }

        private static Button CreateSelectablePillButton(string dotColorHex, string text, bool isSelected, string tooltip)
        {
            Button btn = new Button
            {
                Height = 26,
                Padding = new Thickness(9, 0, 10, 0),
                Margin = new Thickness(0, 2, 6, 4),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                ToolTip = tooltip,
                Template = GetOrCreatePillButtonTemplate()
            };
            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            if (isSelected)
            {
                btn.SetResourceReference(Control.BackgroundProperty, "AccentSubtleBg");
                btn.SetResourceReference(Control.BorderBrushProperty, "AccentPrimary");
                btn.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
            }
            else
            {
                btn.SetResourceReference(Control.BackgroundProperty, "BgSurface");
                btn.SetResourceReference(Control.BorderBrushProperty, "BorderCard");
                btn.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            }

            StackPanel contentRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrEmpty(dotColorHex))
            {
                Ellipse dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = DesignSystem.BrushHex(dotColorHex),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                contentRow.Children.Add(dot);
            }
            else
            {
                Path icon = UiIcons.CreateIcon(UiIcons.Activity, 11, isSelected ? "AccentText" : "FgMuted");
                icon.Margin = new Thickness(0, 0, 5, 0);
                contentRow.Children.Add(icon);
            }

            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = string.IsNullOrEmpty(dotColorHex) ? DesignSystem.FontUi : DesignSystem.FontMono,
                FontSize = 11.5,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isSelected)
                tb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            else
                tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");

            contentRow.Children.Add(tb);
            btn.Content = contentRow;

            if (!isSelected)
            {
                btn.MouseEnter += delegate(object s, MouseEventArgs e)
                {
                    btn.SetResourceReference(Control.BackgroundProperty, "BgHoverStrong");
                    btn.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");
                };
                btn.MouseLeave += delegate(object s, MouseEventArgs e)
                {
                    btn.SetResourceReference(Control.BackgroundProperty, "BgSurface");
                    btn.SetResourceReference(Control.BorderBrushProperty, "BorderCard");
                };
            }

            return btn;
        }

        private static string ShortenModelBadgeName(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return "平替模型";
            if (modelId.Length <= 18) return modelId;
            return modelId.Substring(0, 16) + "..";
        }

        private static string FormatMoney(double usd, bool isCny, double usdToCny)
        {
            if (double.IsNaN(usd) || double.IsInfinity(usd) || usd < 0.0)
                usd = 0.0;

            double val = isCny ? (usd * usdToCny) : usd;
            string sym = isCny ? "¥" : "$";

            if (val <= 0.0)
                return sym + "0.00";
            if (val < 0.01)
                return sym + val.ToString("0.0000", CultureInfo.InvariantCulture);
            return sym + val.ToString("N2", CultureInfo.InvariantCulture);
        }

        private static string FormatAltCurrency(double usd, bool currentIsCny, double usdToCny)
        {
            return FormatMoney(usd, !currentIsCny, usdToCny);
        }

        private static string FormatUnitRate(double usdPerM, bool isCny, double usdToCny)
        {
            if (double.IsNaN(usdPerM) || double.IsInfinity(usdPerM) || usdPerM <= 0.0)
                return isCny ? "¥0.00" : "$0.00";

            double val = isCny ? (usdPerM * usdToCny) : usdPerM;
            string sym = isCny ? "¥" : "$";

            return sym + val.ToString("0.00##", CultureInfo.InvariantCulture);
        }

        private static string FormatCompactTokens(long tokens)
        {
            if (tokens <= 0L) return "0";
            if (tokens >= 1000000000L)
                return (tokens / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (tokens >= 1000000L)
                return (tokens / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (tokens >= 1000L)
                return (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return tokens.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
