using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TokLens
{
    /// <summary>
    /// AI FinOps 智算洞察与订阅回本诊断视图（Bento 四大核心模块）：
    /// 1. 订阅回本等效评估矩阵（Subscription ROI Matrix）
    /// 2. 365 天 AI 算力热力日历（GitHub-Style Contribution Heatmap）
    /// 3. 24 小时编码生物钟与推理速率分布（24h Circadian Activity &amp; TPS Chart）
    /// 4. 智能省钱与缓存效率诊断建议（FinOps Optimization Advisor）
    /// </summary>
    public sealed class InsightsView : Border
    {
        private const int HeatmapWeeks = 53;
        private const int HeatmapDaysPerWeek = 7;
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 缓存最新渲染上下文，支持内部切换器毫秒级局部重绘
        private List<RecordBucket> _allRecords = new List<RecordBucket>();
        private List<RecentRequestRecord> _recentRequests = new List<RecentRequestRecord>();
        private bool _isCny = true;
        private double _usdToCny = ExchangeRateService.DefaultRate;
        private bool _isDark = true;
        private string _referenceDateStr = "";
        private DateTime _refDate = DateTime.Today;

        // 交互状态：0 = 近 30 天，1 = 本自然月
        private int _roiWindowMode = 0;
        private Button[] _roiModeBtns;

        // 交互状态：0 = 按花费，1 = 按 Token，2 = 按请求次数
        private int _heatmapMode = 0;
        private Button[] _heatmapModeBtns;

        // 滚动容器与响应式网格
        private ScrollViewer _scrollViewer;
        private UniformGrid _roiCardsGrid;
        private UniformGrid _advisorCardsGrid;

        // 模块 1：订阅回本矩阵控件引用与用户订阅配置
        private SyncConfig _syncCfg;
        private Action<SyncConfig> _onSaveConfig;
        private TextBlock _roiWindowSummaryText;
        private TextBlock _roiNetVerdictBadgeText;
        private Border _roiNetVerdictBadge;
        private Button _roiConfigBtn;
        private Border _roiUnsubscribedNoticeBorder;
        private TextBlock _roiUnsubscribedNoticeText;
        private Popup _roiConfigPopover;
        private int _roiVisibleCardCount = 3;

        // 模块 2：365 天热力图预分配单元（零 GC 极速刷新）
        private Grid _heatmapMonthHeaderGrid;
        private readonly Border[] _heatmapCells = new Border[HeatmapWeeks * HeatmapDaysPerWeek];
        private readonly string[] _heatmapHoverSummaries = new string[HeatmapWeeks * HeatmapDaysPerWeek];
        private TextBlock _streakValueText;
        private Border _streakPillBorder;
        private TextBlock _peakDayValueText;
        private Border _peakDayPillBorder;
        private TextBlock _activeDaysValueText;
        private TextBlock _heatmapHoverReadout;
        private string _heatmapDefaultFooterText = "";
        private readonly Border[] _heatmapLegendSwatches = new Border[5];

        // 模块 3：24 小时生物钟与 TPS 预分配单元
        private readonly Border[] _hourBars = new Border[24];
        private readonly Ellipse[] _hourTpsDots = new Ellipse[24];
        private readonly TextBlock[] _hourLabels = new TextBlock[24];
        private readonly Border[] _hourColumns = new Border[24];
        private readonly string[] _hourHoverSummaries = new string[24];
        private TextBlock _goldenWindowBadgeText;
        private TextBlock _nightOwlBadgeText;
        private Border _nightOwlBadgeBorder;
        private TextBlock _avgTpsBadgeText;
        private TextBlock _circadianHoverReadout;
        private TextBlock _circadianInsightRightText;
        private string _circadianDefaultFooterText = "";

        // 模块 4：FinOps 智能诊断顾问头部徽章
        private TextBlock _advisorPotentialBadgeText;
        private Border _advisorPotentialBadgeBorder;

        private sealed class EcoBucket
        {
            public string EcoId;
            public string Title;
            public string DefaultSubtitle;
            public string BrandKey;
            public TierSpec[] Tiers;
            public double CostUsd;
            public double BaselineUsd;
            public long TotalTokens;
            public int Turns;
            public Dictionary<string, double> ToolCosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            public EcoBucket(string ecoId, string title, string subtitle, string brandKey, TierSpec[] tiers)
            {
                EcoId = ecoId;
                Title = title;
                DefaultSubtitle = subtitle;
                BrandKey = brandKey;
                Tiers = tiers;
            }
        }

        private struct TierSpec
        {
            public string Name;
            public double MonthlyUsd;

            public TierSpec(string name, double monthlyUsd)
            {
                Name = name;
                MonthlyUsd = monthlyUsd;
            }
        }

        private sealed class DayHeatBucket
        {
            public string DateStr;
            public double CostUsd;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public int Turns;
            public string TopTool;
            public double TopToolCost;

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }
        }

        private sealed class ToolModelAudit
        {
            public string Tool;
            public string Model;
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;
            public double BaselineUsd;
        }

        private sealed class AdvisorCardModel
        {
            public string Title;
            public string BadgeText;
            public string Severity; // "success" | "warning" | "accent"
            public string BodyText;
            public string TelemetryFooter;
            public string IconPath;
        }

        public InsightsView()
        {
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _scrollViewer = new ScrollViewer();
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.CanContentScroll = false;
            _scrollViewer.PanningMode = PanningMode.VerticalOnly;
            _scrollViewer.Focusable = false;
            _scrollViewer.Padding = new Thickness(0, 0, 6, 16);

            StackPanel rootStack = new StackPanel();
            rootStack.Orientation = Orientation.Vertical;

            rootStack.Children.Add(BuildSubscriptionRoiSection());
            rootStack.Children.Add(BuildHeatmapSection());
            rootStack.Children.Add(BuildCircadianSection());
            rootStack.Children.Add(BuildAdvisorSection());

            _scrollViewer.Content = rootStack;
            Child = _scrollViewer;

            SizeChanged += OnInsightsSizeChanged;
        }

        private void OnInsightsSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width;
            if (w <= 0) return;

            if (_roiCardsGrid != null)
            {
                int maxCols = Math.Max(1, Math.Min(3, _roiVisibleCardCount));
                int targetRoiCols = w < 760.0 ? 1 : maxCols;
                if (_roiCardsGrid.Columns != targetRoiCols)
                    _roiCardsGrid.Columns = targetRoiCols;
            }

            if (_advisorCardsGrid != null)
            {
                int targetAdvCols = w < 720.0 ? 1 : 2;
                if (_advisorCardsGrid.Columns != targetAdvCols)
                    _advisorCardsGrid.Columns = targetAdvCols;
            }
        }

        /// <summary>
        /// 绑定用户订阅配置（支持在界面内勾选持有订阅、调整官方档位或自定义月费并持久化）
        /// </summary>
        public void BindSubscriptionConfig(SyncConfig config, Action<SyncConfig> saveCallback)
        {
            _syncCfg = config;
            _onSaveConfig = saveCallback;
        }

        /// <summary>
        /// 刷新全部 AI FinOps 智算洞察模块（保持当前滚动条位置不跳动）
        /// </summary>
        public void RenderInsights(
            List<RecordBucket> allRecords,
            List<RecentRequestRecord> recentRequests,
            bool isCny,
            double usdToCny,
            bool isDark,
            string referenceDate)
        {
            _allRecords = allRecords != null ? allRecords : new List<RecordBucket>();
            _recentRequests = recentRequests != null ? recentRequests : new List<RecentRequestRecord>();
            _isCny = isCny;
            _usdToCny = usdToCny > 0.0 ? usdToCny : ExchangeRateService.DefaultRate;
            _isDark = isDark;
            _referenceDateStr = referenceDate ?? "";
            _refDate = ResolveReferenceDate(_referenceDateStr, _allRecords);

            RenderSubscriptionRoiMatrix();
            RenderContributionHeatmap();
            RenderCircadianChart();
            RenderFinOpsAdvisor();
        }

        // ====================================================================
        // 1. 订阅回本等效评估矩阵（Subscription ROI Matrix）
        // ====================================================================
        private UIElement BuildSubscriptionRoiSection()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(18, 16, 18, 16));
            card.Margin = new Thickness(0, 0, 0, 14);

            StackPanel wrap = new StackPanel();

            // 顶部标题栏与周期切换器
            Grid hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Margin = new Thickness(0, 0, 0, 14);

            StackPanel titleLeft = new StackPanel();
            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Path roiIcon = UiIcons.CreateIcon(UiIcons.Shield, 15, "AccentText");
            roiIcon.Margin = new Thickness(0, 0, 8, 0);
            titleRow.Children.Add(roiIcon);

            TextBlock titleTb = new TextBlock
            {
                Text = "订阅回本等效评估矩阵",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleRow.Children.Add(titleTb);

            _roiNetVerdictBadge = CreateSemanticPill("评估就绪", "success", out _roiNetVerdictBadgeText);
            _roiNetVerdictBadge.Margin = new Thickness(10, 0, 0, 0);
            titleRow.Children.Add(_roiNetVerdictBadge);
            titleLeft.Children.Add(titleRow);

            _roiWindowSummaryText = new TextBlock
            {
                Text = "按官方 API 公开单价（含缓存折扣）折算包月订阅回本率与等效净省金额",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                Margin = new Thickness(23, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _roiWindowSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            titleLeft.Children.Add(_roiWindowSummaryText);

            Grid.SetColumn(titleLeft, 0);
            hdr.Children.Add(titleLeft);

            // 右侧：[自选订阅] 按钮 + [近 30 天 | 本自然月] 切换器
            StackPanel rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

            _roiConfigBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Settings, "自选订阅", "secondary");
            _roiConfigBtn.Height = 28;
            _roiConfigBtn.Padding = new Thickness(10, 0, 10, 0);
            _roiConfigBtn.Margin = new Thickness(0, 0, 8, 0);
            _roiConfigBtn.ToolTip = "勾选自己实际开通的 AI 订阅服务，切换套餐档位或输入自定义月费";
            _roiConfigBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenSubscriptionConfigPopover(_roiConfigBtn);
            };
            rightActions.Children.Add(_roiConfigBtn);

            Border segBox = ModernControlsFactory.CreateSegmentedContainer();
            segBox.VerticalAlignment = VerticalAlignment.Top;
            Panel segPanel = segBox.Child as Panel;
            string[] modeNames = new string[] { "近 30 天", "本自然月" };
            _roiModeBtns = new Button[modeNames.Length];
            for (int i = 0; i < modeNames.Length; i++)
            {
                int capturedIdx = i;
                Button btn = ModernControlsFactory.CreateSegmentedItem(modeNames[i]);
                btn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_roiWindowMode == capturedIdx) return;
                    _roiWindowMode = capturedIdx;
                    ModernControlsFactory.UpdateSegmentedGroup(_roiModeBtns, _roiWindowMode);
                    RenderSubscriptionRoiMatrix();
                };
                _roiModeBtns[i] = btn;
                if (segPanel != null)
                    segPanel.Children.Add(btn);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_roiModeBtns, _roiWindowMode);
            rightActions.Children.Add(segBox);
            Grid.SetColumn(rightActions, 1);
            hdr.Children.Add(rightActions);

            wrap.Children.Add(hdr);

            // 自选/活跃生态并列 Bento 子卡片
            _roiCardsGrid = new UniformGrid();
            _roiCardsGrid.Columns = 3;
            wrap.Children.Add(_roiCardsGrid);

            _roiUnsubscribedNoticeBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 8, 0, 0),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand
            };
            _roiUnsubscribedNoticeBorder.SetResourceReference(Border.BackgroundProperty, "BgCard");
            _roiUnsubscribedNoticeBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            _roiUnsubscribedNoticeText = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.3,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _roiUnsubscribedNoticeText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            _roiUnsubscribedNoticeBorder.Child = _roiUnsubscribedNoticeText;
            _roiUnsubscribedNoticeBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (_roiConfigBtn != null)
                    OpenSubscriptionConfigPopover(_roiConfigBtn);
            };
            wrap.Children.Add(_roiUnsubscribedNoticeBorder);

            card.Child = wrap;
            return card;
        }

        private static EcoBucket[] CreateEcosystemCatalog()
        {
            return new EcoBucket[]
            {
                new EcoBucket(
                    "google",
                    "Google AI / Antigravity",
                    "Google Antigravity · Gemini CLI",
                    "gemini",
                    new TierSpec[]
                    {
                        new TierSpec("Google AI Pro", 20.0),
                        new TierSpec("Google AI Ultra", 250.0)
                    }),
                new EcoBucket(
                    "claude",
                    "Claude 生态",
                    "Claude Code · Claude Desktop",
                    "claude",
                    new TierSpec[]
                    {
                        new TierSpec("Claude Pro", 20.0),
                        new TierSpec("Max 5x", 100.0),
                        new TierSpec("Max 20x", 200.0)
                    }),
                new EcoBucket(
                    "openai",
                    "OpenAI / Codex 生态",
                    "Codex CLI · GPT / o 系列",
                    "codex",
                    new TierSpec[]
                    {
                        new TierSpec("ChatGPT Plus", 20.0),
                        new TierSpec("ChatGPT Pro", 200.0)
                    }),
                new EcoBucket(
                    "cursor",
                    "Cursor 生态",
                    "Cursor AI IDE 独立订阅",
                    "cursor",
                    new TierSpec[]
                    {
                        new TierSpec("Cursor Pro", 20.0),
                        new TierSpec("Cursor Pro+", 60.0),
                        new TierSpec("Cursor Ultra", 200.0)
                    }),
                new EcoBucket(
                    "windsurf",
                    "Windsurf / Copilot / IDE",
                    "Windsurf · Copilot · Trae · Zed",
                    "copilot",
                    new TierSpec[]
                    {
                        new TierSpec("Windsurf Pro", 15.0),
                        new TierSpec("Copilot Pro", 10.0),
                        new TierSpec("Copilot Pro+", 39.0)
                    }),
                new EcoBucket(
                    "coding_plan",
                    "国产 Coding Plan",
                    "ZCode · Kimi Code · 通义 · GLM",
                    "deepseek",
                    new TierSpec[]
                    {
                        new TierSpec("基础 Coding Plan", 6.0),
                        new TierSpec("旗舰 Coding Plan", 20.0),
                        new TierSpec("高阶包月", 60.0)
                    })
            };
        }

        private SubscriptionPlanConfig FindSubConfig(string ecoId)
        {
            if (_syncCfg == null || _syncCfg.Subscriptions == null || string.IsNullOrEmpty(ecoId))
                return null;
            for (int i = 0; i < _syncCfg.Subscriptions.Count; i++)
            {
                SubscriptionPlanConfig sp = _syncCfg.Subscriptions[i];
                if (sp != null && string.Equals(sp.EcoId, ecoId, StringComparison.OrdinalIgnoreCase))
                    return sp;
            }
            return null;
        }

        private SubscriptionPlanConfig EnsureSubConfig(EcoBucket eco)
        {
            if (_syncCfg == null)
                _syncCfg = new SyncConfig();
            if (_syncCfg.Subscriptions == null)
                _syncCfg.Subscriptions = new List<SubscriptionPlanConfig>();
            SubscriptionPlanConfig existing = FindSubConfig(eco.EcoId);
            if (existing != null)
            {
                if (existing.MonthlyUsd <= 0.0 && eco.Tiers != null && eco.Tiers.Length > 0)
                    existing.MonthlyUsd = eco.Tiers[0].MonthlyUsd;
                if (string.IsNullOrEmpty(existing.PlanName) && eco.Tiers != null && eco.Tiers.Length > 0)
                    existing.PlanName = eco.Tiers[0].Name;
                return existing;
            }

            SubscriptionPlanConfig created = new SubscriptionPlanConfig();
            created.EcoId = eco.EcoId;
            created.Enabled = false;
            created.PlanName = (eco.Tiers != null && eco.Tiers.Length > 0) ? eco.Tiers[0].Name : "默认订阅";
            created.MonthlyUsd = (eco.Tiers != null && eco.Tiers.Length > 0) ? eco.Tiers[0].MonthlyUsd : 20.0;
            _syncCfg.Subscriptions.Add(created);
            return created;
        }

        private void PersistSubscriptionConfig()
        {
            if (_syncCfg != null && _onSaveConfig != null)
            {
                try { _onSaveConfig(_syncCfg); } catch { }
            }
        }

        private void OpenSubscriptionConfigPopover(UIElement anchor)
        {
            if (_roiConfigPopover != null && _roiConfigPopover.IsOpen)
            {
                _roiConfigPopover.IsOpen = false;
                return;
            }

            EcoBucket[] catalog = CreateEcosystemCatalog();
            bool isCustomized = _syncCfg != null && _syncCfg.SubscriptionsCustomized;

            // 先统计当前各生态活跃花费以给出智能预选提示
            DateTime windowStart = _roiWindowMode == 1
                ? new DateTime(_refDate.Year, _refDate.Month, 1)
                : _refDate.AddDays(-29);
            for (int i = 0; i < _allRecords.Count; i++)
            {
                RecordBucket r = _allRecords[i];
                if (r == null) continue;
                DateTime dt;
                if (!TryParseYmd(r.Date, out dt) || dt < windowStart || dt > _refDate) continue;
                int ecoIdx = ClassifyEcosystem(r.Tool, r.Model);
                if (ecoIdx >= 0 && ecoIdx < catalog.Length)
                {
                    catalog[ecoIdx].CostUsd += r.CostUsd;
                    catalog[ecoIdx].Turns += r.Turns;
                }
            }

            StackPanel root = new StackPanel { Width = 470 };

            TextBlock title = new TextBlock
            {
                Text = "自选我已开通的 AI 订阅与月费",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            root.Children.Add(title);

            TextBlock desc = new TextBlock
            {
                Text = "仅对勾选持有的订阅核算回本率与净赚金额；点击官方档位可快填，或直接输入实际美元月费",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.2,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 10)
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            root.Children.Add(desc);

            CheckBox[] enabledBoxes = new CheckBox[catalog.Length];
            TextBox[] priceBoxes = new TextBox[catalog.Length];
            string[] selectedPlanNames = new string[catalog.Length];

            for (int i = 0; i < catalog.Length; i++)
            {
                int idx = i;
                EcoBucket eco = catalog[i];
                SubscriptionPlanConfig existing = FindSubConfig(eco.EcoId);

                bool defEnabled = existing != null
                    ? existing.Enabled
                    : (isCustomized ? false : (eco.CostUsd > 0.01 || idx < 3));
                double defUsd = existing != null && existing.MonthlyUsd > 0.0
                    ? existing.MonthlyUsd
                    : eco.Tiers[0].MonthlyUsd;
                string defPlan = existing != null && !string.IsNullOrEmpty(existing.PlanName)
                    ? existing.PlanName
                    : eco.Tiers[0].Name;
                selectedPlanNames[idx] = defPlan;

                Border rowCard = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                rowCard.SetResourceReference(Border.BackgroundProperty, "BgCard");
                rowCard.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

                StackPanel rowSp = new StackPanel();
                Grid topLine = new Grid();
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                CheckBox chk = new CheckBox
                {
                    Content = eco.Title + (eco.CostUsd > 0.01
                        ? string.Format(CultureInfo.InvariantCulture, "（近期用 {0}）", FormatterUtil.FormatMoney(eco.CostUsd, _isCny, _usdToCny))
                        : ""),
                    IsChecked = defEnabled,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                chk.SetResourceReference(CheckBox.ForegroundProperty, "FgPrimary");
                enabledBoxes[idx] = chk;
                Grid.SetColumn(chk, 0);
                topLine.Children.Add(chk);

                StackPanel priceInputPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                TextBlock usdPrefix = new TextBlock
                {
                    Text = "月费 $",
                    FontFamily = DesignSystem.FontUi,
                    FontSize = 11.2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 4, 0)
                };
                usdPrefix.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                priceInputPanel.Children.Add(usdPrefix);

                TextBox priceBox = ModernControlsFactory.CreateTextInput(defUsd.ToString("0.##", CultureInfo.InvariantCulture), 24);
                priceBox.Width = 58;
                priceBox.FontSize = 11.5;
                priceBox.Padding = new Thickness(6, 2, 6, 2);
                priceBox.HorizontalContentAlignment = HorizontalAlignment.Right;
                priceBox.TextChanged += delegate(object s, TextChangedEventArgs e)
                {
                    double typed;
                    if (double.TryParse((priceBox.Text ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out typed))
                    {
                        bool matchedPreset = false;
                        for (int t = 0; t < eco.Tiers.Length; t++)
                        {
                            if (Math.Abs(eco.Tiers[t].MonthlyUsd - typed) < 0.01)
                            {
                                selectedPlanNames[idx] = eco.Tiers[t].Name;
                                matchedPreset = true;
                                break;
                            }
                        }
                        if (!matchedPreset)
                            selectedPlanNames[idx] = "自定义订阅";
                    }
                };
                priceBoxes[idx] = priceBox;
                priceInputPanel.Children.Add(priceBox);

                Grid.SetColumn(priceInputPanel, 1);
                topLine.Children.Add(priceInputPanel);
                rowSp.Children.Add(topLine);

                WrapPanel tierRow = new WrapPanel { Margin = new Thickness(20, 5, 0, 0) };
                for (int t = 0; t < eco.Tiers.Length; t++)
                {
                    TierSpec ts = eco.Tiers[t];
                    Button presetBtn = ModernControlsFactory.CreateButton(
                        string.Format(CultureInfo.InvariantCulture, "{0} ${1:0}", ts.Name, ts.MonthlyUsd),
                        "ghost");
                    presetBtn.Height = 22;
                    presetBtn.FontSize = 10.6;
                    presetBtn.Padding = new Thickness(7, 0, 7, 0);
                    presetBtn.Margin = new Thickness(0, 2, 6, 0);
                    presetBtn.Click += delegate(object s, RoutedEventArgs e)
                    {
                        chk.IsChecked = true;
                        selectedPlanNames[idx] = ts.Name;
                        priceBox.Text = ts.MonthlyUsd.ToString("0.##", CultureInfo.InvariantCulture);
                    };
                    tierRow.Children.Add(presetBtn);
                }
                rowSp.Children.Add(tierRow);
                rowCard.Child = rowSp;
                root.Children.Add(rowCard);
            }

            Grid footerBtns = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            footerBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button resetBtn = ModernControlsFactory.CreateButton("恢复智能匹配", "ghost");
            resetBtn.Height = 28;
            resetBtn.FontSize = 11.5;
            resetBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_syncCfg == null) _syncCfg = new SyncConfig();
                _syncCfg.SubscriptionsCustomized = false;
                if (_syncCfg.Subscriptions != null)
                    _syncCfg.Subscriptions.Clear();
                PersistSubscriptionConfig();
                if (_roiConfigPopover != null) _roiConfigPopover.IsOpen = false;
                RenderSubscriptionRoiMatrix();
            };
            Grid.SetColumn(resetBtn, 0);
            footerBtns.Children.Add(resetBtn);

            Button saveBtn = ModernControlsFactory.CreateButton("保存并重算回本率", "primary");
            saveBtn.Height = 28;
            saveBtn.FontSize = 11.8;
            saveBtn.Padding = new Thickness(14, 0, 14, 0);
            saveBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_syncCfg == null) _syncCfg = new SyncConfig();
                _syncCfg.SubscriptionsCustomized = true;
                _syncCfg.Subscriptions = new List<SubscriptionPlanConfig>(catalog.Length);
                for (int i = 0; i < catalog.Length; i++)
                {
                    double parsedUsd = catalog[i].Tiers[0].MonthlyUsd;
                    double tmpUsd;
                    if (double.TryParse((priceBoxes[i].Text ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out tmpUsd) && tmpUsd > 0.0 && tmpUsd <= 100000.0)
                        parsedUsd = tmpUsd;

                    SubscriptionPlanConfig sp = new SubscriptionPlanConfig();
                    sp.EcoId = catalog[i].EcoId;
                    sp.Enabled = enabledBoxes[i].IsChecked == true;
                    sp.MonthlyUsd = parsedUsd;
                    sp.PlanName = selectedPlanNames[i];
                    _syncCfg.Subscriptions.Add(sp);
                }
                PersistSubscriptionConfig();
                if (_roiConfigPopover != null) _roiConfigPopover.IsOpen = false;
                RenderSubscriptionRoiMatrix();
            };
            Grid.SetColumn(saveBtn, 2);
            footerBtns.Children.Add(saveBtn);

            root.Children.Add(footerBtns);

            _roiConfigPopover = ModernControlsFactory.CreateModernPopover(anchor, root);
            _roiConfigPopover.IsOpen = true;
        }

        private void RenderSubscriptionRoiMatrix()
        {
            if (_roiCardsGrid == null) return;
            _roiCardsGrid.Children.Clear();

            DateTime windowStart;
            string windowDesc;
            if (_roiWindowMode == 1)
            {
                windowStart = new DateTime(_refDate.Year, _refDate.Month, 1);
                windowDesc = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM} 自然月（已统计 {1} 天）",
                    _refDate,
                    _refDate.Day);
            }
            else
            {
                windowStart = _refDate.AddDays(-29);
                windowDesc = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:MM-dd} 至 {1:MM-dd}（近 30 天滚动窗口）",
                    windowStart,
                    _refDate);
            }

            EcoBucket[] catalog = CreateEcosystemCatalog();
            double totalWindowUsd = 0.0;
            for (int i = 0; i < _allRecords.Count; i++)
            {
                RecordBucket r = _allRecords[i];
                if (r == null) continue;
                DateTime dt;
                if (!TryParseYmd(r.Date, out dt)) continue;
                if (dt < windowStart || dt > _refDate) continue;

                totalWindowUsd += r.CostUsd;
                int ecoIdx = ClassifyEcosystem(r.Tool, r.Model);
                if (ecoIdx >= 0 && ecoIdx < catalog.Length)
                {
                    EcoBucket target = catalog[ecoIdx];
                    target.CostUsd += r.CostUsd;
                    target.BaselineUsd += r.BaselineUsd > r.CostUsd ? r.BaselineUsd : r.CostUsd;
                    target.TotalTokens += r.TotalTokens;
                    target.Turns += r.Turns;

                    string tName = string.IsNullOrEmpty(r.Tool) ? "API" : r.Tool;
                    double prevCost;
                    if (target.ToolCosts.TryGetValue(tName, out prevCost))
                        target.ToolCosts[tName] = prevCost + r.CostUsd;
                    else
                        target.ToolCosts[tName] = r.CostUsd;
                }
            }

            bool isCustomized = _syncCfg != null && _syncCfg.SubscriptionsCustomized;
            List<EcoBucket> selectedEcos = new List<EcoBucket>();
            List<EcoBucket> unsubscribedActiveEcos = new List<EcoBucket>();
            Dictionary<string, SubscriptionPlanConfig> activePlans = new Dictionary<string, SubscriptionPlanConfig>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < catalog.Length; i++)
            {
                EcoBucket eco = catalog[i];
                SubscriptionPlanConfig sp = FindSubConfig(eco.EcoId);
                bool enabled;
                if (isCustomized)
                {
                    enabled = sp != null && sp.Enabled;
                }
                else
                {
                    // 默认优先展示所有产生真实消耗的独立生态（至少保证前 3 个占位视图清晰）
                    enabled = sp != null ? sp.Enabled : (eco.CostUsd > 0.01);
                }

                if (enabled)
                {
                    SubscriptionPlanConfig resolvedPlan = sp != null ? sp.Clone() : new SubscriptionPlanConfig();
                    if (string.IsNullOrEmpty(resolvedPlan.EcoId)) resolvedPlan.EcoId = eco.EcoId;
                    if (resolvedPlan.MonthlyUsd <= 0.0) resolvedPlan.MonthlyUsd = eco.Tiers[0].MonthlyUsd;
                    if (string.IsNullOrEmpty(resolvedPlan.PlanName))
                    {
                        TierSpec autoTier = SelectRecommendedTier(eco.CostUsd, eco.Tiers);
                        resolvedPlan.PlanName = autoTier.Name;
                        if (!isCustomized) resolvedPlan.MonthlyUsd = autoTier.MonthlyUsd;
                    }
                    activePlans[eco.EcoId] = resolvedPlan;
                    selectedEcos.Add(eco);
                }
                else if (eco.CostUsd > 0.01)
                {
                    unsubscribedActiveEcos.Add(eco);
                }
            }

            // 如果智能模式下当前窗口全空，展示 Google AI / Antigravity、Claude、OpenAI 三张标准模板卡片
            if (!isCustomized && selectedEcos.Count == 0)
            {
                for (int i = 0; i < 3; i++)
                {
                    EcoBucket eco = catalog[i];
                    SubscriptionPlanConfig defPlan = new SubscriptionPlanConfig();
                    defPlan.EcoId = eco.EcoId;
                    defPlan.Enabled = true;
                    defPlan.PlanName = eco.Tiers[0].Name;
                    defPlan.MonthlyUsd = eco.Tiers[0].MonthlyUsd;
                    activePlans[eco.EcoId] = defPlan;
                    selectedEcos.Add(eco);
                }
            }

            double subscribedCostSumUsd = 0.0;
            double subscribedFeeSumUsd = 0.0;
            int profitableCount = 0;
            for (int i = 0; i < selectedEcos.Count; i++)
            {
                EcoBucket eco = selectedEcos[i];
                SubscriptionPlanConfig sp = activePlans[eco.EcoId];
                subscribedCostSumUsd += eco.CostUsd;
                subscribedFeeSumUsd += sp.MonthlyUsd;
                if (eco.CostUsd >= sp.MonthlyUsd)
                    profitableCount++;
            }

            if (_roiWindowSummaryText != null)
            {
                _roiWindowSummaryText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "统计区间：{0} · 全生态 API 等效总消耗 {1}（各生态已精准解耦，支持按需勾选订阅）",
                    windowDesc,
                    FormatterUtil.FormatMoney(totalWindowUsd, _isCny, _usdToCny));
                ModernControlsFactory.EnableTabularNumbers(_roiWindowSummaryText, false);
                _roiWindowSummaryText.FontFamily = DesignSystem.FontUi;
            }

            if (_roiNetVerdictBadge != null && _roiNetVerdictBadgeText != null)
            {
                double totalNetSavedUsd = subscribedCostSumUsd - subscribedFeeSumUsd;
                if (selectedEcos.Count == 0)
                {
                    ApplyPillVariant(_roiNetVerdictBadge, _roiNetVerdictBadgeText, "neutral");
                    _roiNetVerdictBadgeText.Text = "纯按量 API 模式 · 点击右侧「自选订阅」可勾选对比";
                }
                else if (profitableCount > 0 && totalNetSavedUsd >= 0.0)
                {
                    ApplyPillVariant(_roiNetVerdictBadge, _roiNetVerdictBadgeText, "success");
                    _roiNetVerdictBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "已选 {0} 项订阅 ({1}/月) · 净省 {2}",
                        selectedEcos.Count,
                        FormatterUtil.FormatMoney(subscribedFeeSumUsd, _isCny, _usdToCny),
                        FormatterUtil.FormatMoney(totalNetSavedUsd, _isCny, _usdToCny));
                }
                else if (totalWindowUsd > 0.0)
                {
                    ApplyPillVariant(_roiNetVerdictBadge, _roiNetVerdictBadgeText, "neutral");
                    _roiNetVerdictBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "已选 {0} 项订阅 ({1}/月) · 距全回本差 {2}",
                        selectedEcos.Count,
                        FormatterUtil.FormatMoney(subscribedFeeSumUsd, _isCny, _usdToCny),
                        FormatterUtil.FormatMoney(Math.Max(0.0, subscribedFeeSumUsd - subscribedCostSumUsd), _isCny, _usdToCny));
                }
                else
                {
                    ApplyPillVariant(_roiNetVerdictBadge, _roiNetVerdictBadgeText, "neutral");
                    _roiNetVerdictBadgeText.Text = "等待扫描算力记录";
                }
                ModernControlsFactory.EnableTabularNumbers(_roiNetVerdictBadgeText, false);
                _roiNetVerdictBadgeText.FontFamily = DesignSystem.FontUi;
            }

            _roiVisibleCardCount = Math.Max(1, selectedEcos.Count);
            double curW = _roiCardsGrid.ActualWidth > 0 ? _roiCardsGrid.ActualWidth : ActualWidth;
            if (curW > 0 && curW < 760.0)
                _roiCardsGrid.Columns = 1;
            else
                _roiCardsGrid.Columns = Math.Max(1, Math.Min(3, _roiVisibleCardCount));

            if (selectedEcos.Count == 0)
            {
                Border emptyHint = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(16, 14, 16, 14),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                emptyHint.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                emptyHint.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                TextBlock emptyTb = new TextBlock
                {
                    Text = "当前未勾选任何包月订阅。点击右上角「自选订阅」，勾选你实际使用的 Google AI / Claude / OpenAI / Cursor / Coding Plan 即可评估回本情况。",
                    FontFamily = DesignSystem.FontUi,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                emptyTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                emptyHint.Child = emptyTb;
                _roiCardsGrid.Children.Add(emptyHint);
            }
            else
            {
                int cols = _roiCardsGrid.Columns;
                for (int i = 0; i < selectedEcos.Count; i++)
                {
                    EcoBucket eco = selectedEcos[i];
                    SubscriptionPlanConfig activeSp = activePlans[eco.EcoId];
                    int colIdx = cols > 0 ? (i % cols) : 0;
                    double leftM = (cols <= 1 || colIdx == 0) ? 0.0 : 4.0;
                    double rightM = (cols <= 1 || colIdx == cols - 1) ? 0.0 : 4.0;
                    _roiCardsGrid.Children.Add(BuildSingleEcoCard(eco, activeSp, new Thickness(leftM, 0, rightM, 8)));
                }
            }

            if (_roiUnsubscribedNoticeBorder != null && _roiUnsubscribedNoticeText != null)
            {
                if (unsubscribedActiveEcos.Count > 0)
                {
                    List<string> parts = new List<string>(unsubscribedActiveEcos.Count);
                    for (int i = 0; i < unsubscribedActiveEcos.Count; i++)
                    {
                        EcoBucket u = unsubscribedActiveEcos[i];
                        parts.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} ({1})",
                            u.Title,
                            FormatterUtil.FormatMoney(u.CostUsd, _isCny, _usdToCny)));
                    }
                    _roiUnsubscribedNoticeBorder.Visibility = Visibility.Visible;
                    _roiUnsubscribedNoticeText.Text = "未选订阅但有实际用量的按量生态：" + string.Join(" · ", parts.ToArray()) + "  （点击此处或「自选订阅」可随时开启对比）";
                }
                else
                {
                    _roiUnsubscribedNoticeBorder.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static TierSpec SelectRecommendedTier(double costUsd, TierSpec[] tiers)
        {
            TierSpec chosen = tiers[0];
            for (int i = 0; i < tiers.Length; i++)
            {
                if (costUsd >= tiers[i].MonthlyUsd)
                    chosen = tiers[i];
            }
            return chosen;
        }

        private UIElement BuildSingleEcoCard(EcoBucket eco, SubscriptionPlanConfig activeSp, Thickness margin)
        {
            Border box = new Border();
            box.CornerRadius = new CornerRadius(8);
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(14, 12, 14, 12);
            box.Margin = margin;
            box.SnapsToDevicePixels = true;
            box.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            box.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            string brandHex = DesignSystem.GetProviderColorHex(eco.BrandKey, _isDark);
            SolidColorBrush brandBrush = DesignSystem.BrushHex(brandHex);
            double chosenMonthlyUsd = activeSp != null && activeSp.MonthlyUsd > 0.0 ? activeSp.MonthlyUsd : eco.Tiers[0].MonthlyUsd;
            string chosenPlanName = activeSp != null && !string.IsNullOrEmpty(activeSp.PlanName) ? activeSp.PlanName : eco.Tiers[0].Name;

            StackPanel sp = new StackPanel();

            // 1. 生态标题 + 实际订阅档位回本结论徽章
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = brandBrush,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            nameStack.Children.Add(dot);

            TextBlock nameTb = new TextBlock
            {
                Text = eco.Title,
                FontFamily = DesignSystem.FontUi,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            nameStack.Children.Add(nameTb);
            Grid.SetColumn(nameStack, 0);
            topRow.Children.Add(nameStack);

            string badgeVariant = "neutral";
            string badgeLabel;
            if (eco.CostUsd >= chosenMonthlyUsd)
            {
                badgeVariant = "success";
                double pct = (eco.CostUsd / chosenMonthlyUsd) * 100.0;
                double netSave = eco.CostUsd - chosenMonthlyUsd;
                badgeLabel = string.Format(
                    CultureInfo.InvariantCulture,
                    "已回本 {0:F0}% · 净省 {1}",
                    pct,
                    FormatterUtil.FormatMoney(netSave, _isCny, _usdToCny));
            }
            else if (eco.CostUsd > 0.005)
            {
                badgeVariant = "neutral";
                double pct = (eco.CostUsd / chosenMonthlyUsd) * 100.0;
                badgeLabel = string.Format(CultureInfo.InvariantCulture, "已用 {0:F0}% · 未回本", pct);
            }
            else
            {
                badgeVariant = "neutral";
                badgeLabel = "暂无消耗 · 按需使用";
            }

            TextBlock verdictTb;
            Border verdictPill = CreateSemanticPill(badgeLabel, badgeVariant, out verdictTb);
            ModernControlsFactory.EnableTabularNumbers(verdictTb, false);
            verdictTb.FontFamily = DesignSystem.FontUi;
            Grid.SetColumn(verdictPill, 1);
            topRow.Children.Add(verdictPill);
            sp.Children.Add(topRow);

            // 2. 活跃工具构成副标题 + 快速切换当前订阅档位胶囊
            TextBlock subTb = new TextBlock
            {
                Text = BuildActiveToolsSummary(eco),
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                Margin = new Thickness(15, 2, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(subTb);

            // 3. 核心等效 API 金额大字 + Token / 请求次数
            Grid kpiRow = new Grid();
            kpiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            kpiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiRow.Margin = new Thickness(0, 0, 0, 10);

            TextBlock heroMoney = new TextBlock
            {
                Text = FormatterUtil.FormatMoney(eco.CostUsd, _isCny, _usdToCny),
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            ModernControlsFactory.EnableTabularNumbers(heroMoney, false);
            heroMoney.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            Grid.SetColumn(heroMoney, 0);
            kpiRow.Children.Add(heroMoney);

            string altCurrency = _isCny
                ? ("$" + eco.CostUsd.ToString("F2", CultureInfo.InvariantCulture))
                : ("¥" + (eco.CostUsd * _usdToCny).ToString("F2", CultureInfo.InvariantCulture));
            TextBlock metaRight = new TextBlock
            {
                Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · {1} tok · {2} 次",
                    altCurrency,
                    FormatterUtil.FormatTokens(eco.TotalTokens),
                    eco.Turns.ToString("N0", CultureInfo.InvariantCulture)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(metaRight, true);
            metaRight.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(metaRight, 1);
            kpiRow.Children.Add(metaRight);
            sp.Children.Add(kpiRow);

            // 4. 当前选中订阅进度条（支持卡片内一键切换官方档位）
            WrapPanel tierSwitchBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            for (int t = 0; t < eco.Tiers.Length; t++)
            {
                TierSpec ts = eco.Tiers[t];
                bool isActiveTier = Math.Abs(ts.MonthlyUsd - chosenMonthlyUsd) < 0.01;
                Button tierBtn = ModernControlsFactory.CreateButton(
                    string.Format(CultureInfo.InvariantCulture, "{0} ${1:0}", ts.Name, ts.MonthlyUsd),
                    isActiveTier ? "primary" : "ghost");
                tierBtn.Height = 22;
                tierBtn.FontSize = 10.6;
                tierBtn.Padding = new Thickness(8, 0, 8, 0);
                tierBtn.Margin = new Thickness(0, 0, 5, 4);
                tierBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_syncCfg == null) _syncCfg = new SyncConfig();
                    _syncCfg.SubscriptionsCustomized = true;
                    SubscriptionPlanConfig spTarget = EnsureSubConfig(eco);
                    spTarget.Enabled = true;
                    spTarget.PlanName = ts.Name;
                    spTarget.MonthlyUsd = ts.MonthlyUsd;
                    PersistSubscriptionConfig();
                    RenderSubscriptionRoiMatrix();
                };
                tierSwitchBar.Children.Add(tierBtn);
            }
            sp.Children.Add(tierSwitchBar);

            TierSpec currentTierSpec = new TierSpec(chosenPlanName, chosenMonthlyUsd);
            sp.Children.Add(BuildTierProgressRow(currentTierSpec, eco.CostUsd, brandBrush, true));

            // 5. 无缓存 API 原价与缓存省钱对比微指标
            Border footBox = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 2, 0, 0)
            };
            footBox.SetResourceReference(Border.BackgroundProperty, "BgCard");

            Grid fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            double cacheSaved = Math.Max(0.0, eco.BaselineUsd - eco.CostUsd);
            TextBlock fLeft = new TextBlock
            {
                Text = "无缓存 API 原价折算",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            fLeft.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(fLeft, 0);
            fg.Children.Add(fLeft);

            TextBlock fRight = new TextBlock
            {
                Text = cacheSaved > 0.01
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} (缓存已省 {1})",
                        FormatterUtil.FormatMoney(eco.BaselineUsd, _isCny, _usdToCny),
                        FormatterUtil.FormatMoney(cacheSaved, _isCny, _usdToCny))
                    : FormatterUtil.FormatMoney(eco.BaselineUsd, _isCny, _usdToCny),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(fRight, true);
            fRight.SetResourceReference(TextBlock.ForegroundProperty, cacheSaved > 0.01 ? "SemanticSuccess" : "FgSecondary");
            Grid.SetColumn(fRight, 1);
            fg.Children.Add(fRight);

            footBox.Child = fg;
            sp.Children.Add(footBox);

            box.Child = sp;
            return box;
        }

        private UIElement BuildTierProgressRow(TierSpec tier, double actualUsd, Brush brandBrush, bool bottomSpacing)
        {
            StackPanel rowWrap = new StackPanel();
            if (bottomSpacing)
                rowWrap.Margin = new Thickness(0, 0, 0, 9);

            Grid lblGrid = new Grid();
            lblGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lblGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lblGrid.Margin = new Thickness(0, 0, 0, 4);

            string tierPriceTag = _isCny
                ? string.Format(CultureInfo.InvariantCulture, "${0:F0}/月 (≈¥{1:F0})", tier.MonthlyUsd, tier.MonthlyUsd * _usdToCny)
                : string.Format(CultureInfo.InvariantCulture, "${0:F0}/mo", tier.MonthlyUsd);

            TextBlock leftTb = new TextBlock
            {
                Text = tier.Name + " · " + tierPriceTag,
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.3,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            Grid.SetColumn(leftTb, 0);
            lblGrid.Children.Add(leftTb);

            double roiRatio = tier.MonthlyUsd > 0.0 ? (actualUsd / tier.MonthlyUsd) : 0.0;
            double roiPct = roiRatio * 100.0;
            bool isBreakeven = actualUsd >= tier.MonthlyUsd;

            string rightSummary;
            if (isBreakeven)
            {
                double netGain = actualUsd - tier.MonthlyUsd;
                rightSummary = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F0}% · 净赚 +{1}",
                    roiPct,
                    FormatterUtil.FormatMoney(netGain, _isCny, _usdToCny));
            }
            else if (actualUsd > 0.001)
            {
                double gap = tier.MonthlyUsd - actualUsd;
                rightSummary = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F0}% · 差 {1} 回本",
                    roiPct,
                    FormatterUtil.FormatMoney(gap, _isCny, _usdToCny));
            }
            else
            {
                rightSummary = string.Format(
                    CultureInfo.InvariantCulture,
                    "0% · 按量立省 {0}",
                    FormatterUtil.FormatMoney(tier.MonthlyUsd, _isCny, _usdToCny));
            }

            TextBlock rightTb = new TextBlock
            {
                Text = rightSummary,
                FontSize = 11,
                FontWeight = isBreakeven ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(rightTb, true);
            rightTb.SetResourceReference(TextBlock.ForegroundProperty, isBreakeven ? "SemanticSuccess" : "FgMuted");
            Grid.SetColumn(rightTb, 1);
            lblGrid.Children.Add(rightTb);
            rowWrap.Children.Add(lblGrid);

            // 自适应等比进度条（Star Grid 在任意 DPI/宽度及无头测试下零跳动）
            Border track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                SnapsToDevicePixels = true
            };
            track.SetResourceReference(Border.BackgroundProperty, "BgCard");

            double clamped = roiRatio;
            if (clamped < 0.0) clamped = 0.0;
            if (clamped > 1.0) clamped = 1.0;

            Grid barGrid = new Grid();
            double fillWeight = clamped <= 0.0 ? 0.0 : Math.Max(0.025, clamped);
            double restWeight = 1.0 - fillWeight;
            if (restWeight < 0.0) restWeight = 0.0;

            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fillWeight * 1000.0, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(restWeight * 1000.0, GridUnitType.Star) });

            if (clamped > 0.0)
            {
                Border fill = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3)
                };
                if (isBreakeven)
                    fill.SetResourceReference(Border.BackgroundProperty, "SemanticSuccess");
                else
                    fill.Background = brandBrush;

                Grid.SetColumn(fill, 0);
                barGrid.Children.Add(fill);
            }

            track.Child = barGrid;
            rowWrap.Children.Add(track);
            return rowWrap;
        }

        private static string BuildActiveToolsSummary(EcoBucket eco)
        {
            if (eco == null || eco.ToolCosts == null || eco.ToolCosts.Count == 0)
                return eco != null ? eco.DefaultSubtitle : "";

            List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>(eco.ToolCosts);
            list.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            List<string> topNames = new List<string>();
            int take = Math.Min(3, list.Count);
            for (int i = 0; i < take; i++)
                topNames.Add(list[i].Key);

            if (list.Count > 3)
                topNames.Add("+" + (list.Count - 3).ToString(CultureInfo.InvariantCulture));

            return "活跃来源：" + string.Join(" · ", topNames.ToArray());
        }

        // ====================================================================
        // 2. 365 天 AI 算力热力日历（GitHub-Style Contribution Heatmap）
        // ====================================================================
        private UIElement BuildHeatmapSection()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(18, 16, 18, 14));
            card.Margin = new Thickness(0, 0, 0, 14);

            StackPanel wrap = new StackPanel();

            // 头部：标题 + 切换器 + 右侧三大指标徽章
            Grid hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.Margin = new Thickness(0, 0, 0, 14);

            StackPanel titleLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Path calIcon = UiIcons.CreateIcon(UiIcons.Calendar, 15, "AccentText");
            calIcon.Margin = new Thickness(0, 0, 8, 0);
            titleLeft.Children.Add(calIcon);

            TextBlock titleTb = new TextBlock
            {
                Text = "365 天 AI 算力热力日历",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleLeft.Children.Add(titleTb);
            Grid.SetColumn(titleLeft, 0);
            hdr.Children.Add(titleLeft);

            // 中部：[按花费 | 按 Token | 按请求次数]
            Border modeSeg = ModernControlsFactory.CreateSegmentedContainer();
            modeSeg.Margin = new Thickness(14, 0, 12, 0);
            Panel modePanel = modeSeg.Child as Panel;
            string[] hmModes = new string[] { "按花费", "按 Token", "按请求次数" };
            _heatmapModeBtns = new Button[hmModes.Length];
            for (int i = 0; i < hmModes.Length; i++)
            {
                int capturedIdx = i;
                Button b = ModernControlsFactory.CreateSegmentedItem(hmModes[i]);
                b.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_heatmapMode == capturedIdx) return;
                    _heatmapMode = capturedIdx;
                    ModernControlsFactory.UpdateSegmentedGroup(_heatmapModeBtns, _heatmapMode);
                    RenderContributionHeatmap();
                };
                _heatmapModeBtns[i] = b;
                if (modePanel != null)
                    modePanel.Children.Add(b);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_heatmapModeBtns, _heatmapMode);
            Grid.SetColumn(modeSeg, 1);
            hdr.Children.Add(modeSeg);

            // 右上角：连续活跃 Streak | 年度最高单日峰值 | 活跃总天数
            WrapPanel kpiPills = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            _streakPillBorder = CreateKpiStatChip("连续活跃", "0 天", "SemanticSuccess", out _streakValueText);
            _streakPillBorder.Margin = new Thickness(0, 2, 8, 2);
            kpiPills.Children.Add(_streakPillBorder);

            _peakDayPillBorder = CreateKpiStatChip("单日峰值", "¥0.00", "AccentText", out _peakDayValueText);
            _peakDayPillBorder.Margin = new Thickness(0, 2, 8, 2);
            kpiPills.Children.Add(_peakDayPillBorder);

            Border activeChip = CreateKpiStatChip("活跃总天数", "0 / 365 天", "FgPrimary", out _activeDaysValueText);
            activeChip.Margin = new Thickness(0, 2, 0, 2);
            kpiPills.Children.Add(activeChip);

            Grid.SetColumn(kpiPills, 2);
            hdr.Children.Add(kpiPills);
            wrap.Children.Add(hdr);

            // 热力图舞台容器
            Border stage = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                SnapsToDevicePixels = true
            };
            stage.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            stage.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel stageStack = new StackPanel();

            // 月份刻度行（左留 30px 星期栏宽度，右侧 53 列等分）
            Grid monthOuter = new Grid();
            monthOuter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            monthOuter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            monthOuter.Margin = new Thickness(0, 0, 0, 4);

            _heatmapMonthHeaderGrid = new Grid();
            for (int w = 0; w < HeatmapWeeks; w++)
            {
                _heatmapMonthHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            Grid.SetColumn(_heatmapMonthHeaderGrid, 1);
            monthOuter.Children.Add(_heatmapMonthHeaderGrid);
            stageStack.Children.Add(monthOuter);

            // 星期标签 + 53×7 方格矩阵
            Grid matrixOuter = new Grid();
            matrixOuter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            matrixOuter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            matrixOuter.Height = 112;

            UniformGrid dowLabels = new UniformGrid { Rows = 7, Columns = 1 };
            string[] dowTexts = new string[] { "周一", "", "周三", "", "周五", "", "周日" };
            for (int d = 0; d < 7; d++)
            {
                TextBlock dtb = new TextBlock
                {
                    Text = dowTexts[d],
                    FontFamily = DesignSystem.FontUi,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dtb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                dowLabels.Children.Add(dtb);
            }
            Grid.SetColumn(dowLabels, 0);
            matrixOuter.Children.Add(dowLabels);

            UniformGrid cellsMatrix = new UniformGrid
            {
                Rows = HeatmapDaysPerWeek,
                Columns = HeatmapWeeks,
                SnapsToDevicePixels = true
            };

            for (int r = 0; r < HeatmapDaysPerWeek; r++)
            {
                for (int c = 0; c < HeatmapWeeks; c++)
                {
                    int flatIdx = r * HeatmapWeeks + c;
                    Border cell = new Border
                    {
                        CornerRadius = new CornerRadius(2.5),
                        Margin = new Thickness(1.5),
                        BorderThickness = new Thickness(1),
                        SnapsToDevicePixels = true,
                        Cursor = Cursors.Hand
                    };

                    int capturedFlat = flatIdx;
                    cell.MouseEnter += delegate(object s, MouseEventArgs e)
                    {
                        Border b = s as Border;
                        if (b != null)
                            b.SetResourceReference(Border.BorderBrushProperty, "FgPrimary");
                        string summary = _heatmapHoverSummaries[capturedFlat];
                        if (_heatmapHoverReadout != null && !string.IsNullOrEmpty(summary))
                            _heatmapHoverReadout.Text = summary;
                    };
                    cell.MouseLeave += delegate(object s, MouseEventArgs e)
                    {
                        Border b = s as Border;
                        if (b != null)
                            b.BorderBrush = Brushes.Transparent;
                        if (_heatmapHoverReadout != null)
                            _heatmapHoverReadout.Text = _heatmapDefaultFooterText;
                    };

                    _heatmapCells[flatIdx] = cell;
                    cellsMatrix.Children.Add(cell);
                }
            }
            Grid.SetColumn(cellsMatrix, 1);
            matrixOuter.Children.Add(cellsMatrix);
            stageStack.Children.Add(matrixOuter);

            stage.Child = stageStack;
            wrap.Children.Add(stage);

            // 底部：悬停即时读数 + 5 级自适应强度图例
            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Margin = new Thickness(2, 9, 2, 0);

            _heatmapHoverReadout = new TextBlock
            {
                Text = "鼠标悬停任意日期方格，即时检视当日花费、Token 构成与主力 AI 工具",
                FontSize = 11.3,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_heatmapHoverReadout, true);
            _heatmapHoverReadout.FontFamily = DesignSystem.FontUi;
            _heatmapHoverReadout.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            Grid.SetColumn(_heatmapHoverReadout, 0);
            foot.Children.Add(_heatmapHoverReadout);

            StackPanel legendSp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lessTb = new TextBlock
            {
                Text = "少",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            lessTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            legendSp.Children.Add(lessTb);

            for (int lv = 0; lv < 5; lv++)
            {
                Border sw = new Border
                {
                    Width = 11,
                    Height = 11,
                    CornerRadius = new CornerRadius(2.5),
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _heatmapLegendSwatches[lv] = sw;
                legendSp.Children.Add(sw);
            }

            TextBlock moreTb = new TextBlock
            {
                Text = "多",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            moreTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            legendSp.Children.Add(moreTb);

            Grid.SetColumn(legendSp, 1);
            foot.Children.Add(legendSp);
            wrap.Children.Add(foot);

            card.Child = wrap;
            return card;
        }

        private void RenderContributionHeatmap()
        {
            if (_heatmapCells[0] == null) return;

            Dictionary<string, DayHeatBucket> dayMap = new Dictionary<string, DayHeatBucket>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, double>> dayToolMap = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

            for (int i = 0; i < _allRecords.Count; i++)
            {
                RecordBucket r = _allRecords[i];
                if (r == null || string.IsNullOrEmpty(r.Date) || r.Date.Length < 10) continue;
                string dKey = r.Date.Substring(0, 10);

                DayHeatBucket db;
                if (!dayMap.TryGetValue(dKey, out db))
                {
                    db = new DayHeatBucket();
                    db.DateStr = dKey;
                    dayMap[dKey] = db;
                }
                db.CostUsd += r.CostUsd;
                db.FreshInput += r.FreshInput;
                db.CacheRead += r.CacheRead;
                db.CacheWrite += r.CacheWrite;
                db.Output += r.Output;
                db.Turns += r.Turns;

                string tName = string.IsNullOrEmpty(r.Tool) ? "API" : r.Tool;
                Dictionary<string, double> tDict;
                if (!dayToolMap.TryGetValue(dKey, out tDict))
                {
                    tDict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    dayToolMap[dKey] = tDict;
                }
                double curTCost;
                double nextTCost = (tDict.TryGetValue(tName, out curTCost) ? curTCost : 0.0) + r.CostUsd;
                tDict[tName] = nextTCost;
                if (nextTCost >= db.TopToolCost)
                {
                    db.TopToolCost = nextTCost;
                    db.TopTool = tName;
                }
            }

            // 对齐周一=0 .. 周日=6，最后一列包含 _refDate
            DateTime endDate = _refDate.Date;
            int endDowMon0 = ((int)endDate.DayOfWeek + 6) % 7;
            DateTime startDate = endDate.AddDays(-endDowMon0 - (HeatmapWeeks - 1) * 7);
            DateTime yearWindowStart = endDate.AddDays(-364);

            // 统计 365 天核心指标与最大值
            int activeDays365 = 0;
            int bestStreak365 = 0;
            int runningStreak = 0;
            double maxMetricVal = 0.0;
            double yearTotalUsd = 0.0;
            long yearTotalTokens = 0L;
            int yearTotalTurns = 0;

            DayHeatBucket peakCostBucket = null;
            DayHeatBucket peakModeBucket = null;
            double peakModeVal = 0.0;

            for (int offset = 0; offset < 365; offset++)
            {
                DateTime cur = yearWindowStart.AddDays(offset);
                string key = cur.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DayHeatBucket b;
                bool active = dayMap.TryGetValue(key, out b) && (b.CostUsd > 0.0 || b.TotalTokens > 0L || b.Turns > 0);
                if (active)
                {
                    activeDays365++;
                    runningStreak++;
                    if (runningStreak > bestStreak365)
                        bestStreak365 = runningStreak;

                    yearTotalUsd += b.CostUsd;
                    yearTotalTokens += b.TotalTokens;
                    yearTotalTurns += b.Turns;

                    if (peakCostBucket == null || b.CostUsd > peakCostBucket.CostUsd)
                        peakCostBucket = b;

                    double mv = GetHeatmapMetricValue(b, _heatmapMode);
                    if (mv > maxMetricVal)
                        maxMetricVal = mv;
                    if (peakModeBucket == null || mv > peakModeVal)
                    {
                        peakModeVal = mv;
                        peakModeBucket = b;
                    }
                }
                else
                {
                    runningStreak = 0;
                }
            }

            // 计算当前连续活跃 Streak（若今日尚未产生记录但昨日活跃，则延续昨日不断签）
            int currentStreak = 0;
            DateTime streakCursor = endDate;
            if (!IsDateActive(dayMap, streakCursor) && IsDateActive(dayMap, streakCursor.AddDays(-1)))
                streakCursor = streakCursor.AddDays(-1);

            while (IsDateActive(dayMap, streakCursor) && currentStreak < 3650)
            {
                currentStreak++;
                streakCursor = streakCursor.AddDays(-1);
            }
            if (currentStreak > bestStreak365)
                bestStreak365 = currentStreak;

            // 更新右上角三大统计徽章
            if (_streakValueText != null)
            {
                _streakValueText.Text = currentStreak.ToString(CultureInfo.InvariantCulture) + " 天";
                if (_streakPillBorder != null)
                {
                    _streakPillBorder.ToolTip = string.Format(
                        CultureInfo.InvariantCulture,
                        "当前连续活跃：{0} 天\n近一年最长连续记录：{1} 天",
                        currentStreak,
                        bestStreak365);
                }
            }

            if (_peakDayValueText != null)
            {
                if (peakModeBucket != null)
                {
                    string shortDate = peakModeBucket.DateStr.Length >= 10 ? peakModeBucket.DateStr.Substring(5, 5) : peakModeBucket.DateStr;
                    string peakValLabel;
                    if (_heatmapMode == 1)
                        peakValLabel = FormatterUtil.FormatTokens(peakModeBucket.TotalTokens);
                    else if (_heatmapMode == 2)
                        peakValLabel = peakModeBucket.Turns.ToString("N0", CultureInfo.InvariantCulture) + " 次";
                    else
                        peakValLabel = FormatterUtil.FormatMoney(peakModeBucket.CostUsd, _isCny, _usdToCny);

                    _peakDayValueText.Text = peakValLabel + " (" + shortDate + ")";
                    if (_peakDayPillBorder != null)
                    {
                        _peakDayPillBorder.ToolTip = string.Format(
                            CultureInfo.InvariantCulture,
                            "年度峰值日：{0}\n当日花费：{1}\n当日 Token：{2}\n当日请求：{3} 次",
                            peakModeBucket.DateStr,
                            FormatterUtil.FormatMoney(peakModeBucket.CostUsd, _isCny, _usdToCny),
                            FormatterUtil.FormatTokens(peakModeBucket.TotalTokens),
                            peakModeBucket.Turns);
                    }
                }
                else
                {
                    _peakDayValueText.Text = "—";
                }
            }

            if (_activeDaysValueText != null)
            {
                double activePct = (activeDays365 * 100.0) / 365.0;
                _activeDaysValueText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} / 365 天 ({1:F0}%)",
                    activeDays365,
                    activePct);
            }

            // 渲染顶部月份刻度
            if (_heatmapMonthHeaderGrid != null)
            {
                _heatmapMonthHeaderGrid.Children.Clear();
                int lastPlacedCol = -10;
                int prevMonth = -1;
                for (int c = 0; c < HeatmapWeeks; c++)
                {
                    DateTime colMonday = startDate.AddDays(c * 7);
                    DateTime probe = colMonday > endDate ? endDate : colMonday;
                    int m = probe.Month;
                    if (m != prevMonth)
                    {
                        prevMonth = m;
                        if (c - lastPlacedCol >= 3 && c <= HeatmapWeeks - 2)
                        {
                            TextBlock mtb = new TextBlock
                            {
                                Text = m.ToString(CultureInfo.InvariantCulture) + "月",
                                FontFamily = DesignSystem.FontUi,
                                FontSize = 10.5,
                                HorizontalAlignment = HorizontalAlignment.Left
                            };
                            mtb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                            Grid.SetColumn(mtb, c);
                            _heatmapMonthHeaderGrid.Children.Add(mtb);
                            lastPlacedCol = c;
                        }
                    }
                }
            }

            // 获取 5 级 Obsidian/Paper 自适应色阶（花费=Emerald 翠绿阶，Token/请求=Indigo 靛蓝阶）
            SolidColorBrush[] palette = GetHeatmapLevelBrushes(_isDark, _heatmapMode == 0);
            SolidColorBrush emptyBorderBrush = DesignSystem.BrushHex(_isDark ? "#202028" : "#E1E4E8");

            for (int lv = 0; lv < 5; lv++)
            {
                if (_heatmapLegendSwatches[lv] != null)
                {
                    _heatmapLegendSwatches[lv].Background = palette[lv];
                    _heatmapLegendSwatches[lv].BorderBrush = lv == 0 ? emptyBorderBrush : Brushes.Transparent;
                }
            }

            // 填充 53 × 7 热力方格
            string[] cnDow = new string[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            for (int r = 0; r < HeatmapDaysPerWeek; r++)
            {
                for (int c = 0; c < HeatmapWeeks; c++)
                {
                    int flatIdx = r * HeatmapWeeks + c;
                    Border cell = _heatmapCells[flatIdx];
                    DateTime cellDt = startDate.AddDays(c * 7 + r);

                    if (cellDt > endDate)
                    {
                        cell.Opacity = 0.0;
                        cell.IsHitTestVisible = false;
                        cell.ToolTip = null;
                        _heatmapHoverSummaries[flatIdx] = "";
                        continue;
                    }

                    cell.Opacity = 1.0;
                    cell.IsHitTestVisible = true;
                    string dateKey = cellDt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    DayHeatBucket bucket;
                    bool hasData = dayMap.TryGetValue(dateKey, out bucket) &&
                                   (bucket.CostUsd > 0.0 || bucket.TotalTokens > 0L || bucket.Turns > 0);

                    int level = 0;
                    if (hasData && maxMetricVal > 0.0)
                    {
                        double val = GetHeatmapMetricValue(bucket, _heatmapMode);
                        level = ComputeIntensityLevel(val, maxMetricVal);
                    }

                    cell.Background = palette[level];
                    cell.BorderBrush = level == 0 ? emptyBorderBrush : Brushes.Transparent;

                    if (hasData)
                    {
                        string moneyMain = FormatterUtil.FormatMoney(bucket.CostUsd, _isCny, _usdToCny);
                        string moneyAlt = _isCny
                            ? ("$" + bucket.CostUsd.ToString("F2", CultureInfo.InvariantCulture))
                            : ("¥" + (bucket.CostUsd * _usdToCny).ToString("F2", CultureInfo.InvariantCulture));
                        string topToolStr = string.IsNullOrEmpty(bucket.TopTool) ? "多工具" : bucket.TopTool;

                        cell.ToolTip = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} ({1})\n等效花费：{2} ({3})\n总 Token：{4} (输入 {5} · 缓存 {6} · 输出 {7})\n请求次数：{8} 次 · 主力：{9}",
                            dateKey,
                            cnDow[r],
                            moneyMain,
                            moneyAlt,
                            FormatterUtil.FormatTokens(bucket.TotalTokens),
                            FormatterUtil.FormatTokens(bucket.FreshInput),
                            FormatterUtil.FormatTokens(bucket.CacheRead),
                            FormatterUtil.FormatTokens(bucket.Output),
                            bucket.Turns.ToString("N0", CultureInfo.InvariantCulture),
                            topToolStr);

                        _heatmapHoverSummaries[flatIdx] = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} ({1})  ·  花费 {2}  ·  Token {3}  ·  请求 {4} 次  ·  主力工具：{5}",
                            dateKey,
                            cnDow[r],
                            moneyMain,
                            FormatterUtil.FormatTokens(bucket.TotalTokens),
                            bucket.Turns.ToString("N0", CultureInfo.InvariantCulture),
                            topToolStr);
                    }
                    else
                    {
                        cell.ToolTip = string.Format(CultureInfo.InvariantCulture, "{0} ({1})\n当日无 AI 算力消耗记录", dateKey, cnDow[r]);
                        _heatmapHoverSummaries[flatIdx] = string.Format(CultureInfo.InvariantCulture, "{0} ({1})  ·  当日无 AI 算力消耗记录", dateKey, cnDow[r]);
                    }
                }
            }

            _heatmapDefaultFooterText = string.Format(
                CultureInfo.InvariantCulture,
                "近 365 天累计：{0}  ·  总算力 {1}  ·  总调用 {2} 次（悬停任意方格查看单日明细）",
                FormatterUtil.FormatMoney(yearTotalUsd, _isCny, _usdToCny),
                FormatterUtil.FormatTokens(yearTotalTokens),
                yearTotalTurns.ToString("N0", CultureInfo.InvariantCulture));

            if (_heatmapHoverReadout != null)
                _heatmapHoverReadout.Text = _heatmapDefaultFooterText;
        }

        private static bool IsDateActive(Dictionary<string, DayHeatBucket> map, DateTime dt)
        {
            if (map == null) return false;
            string key = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            DayHeatBucket b;
            return map.TryGetValue(key, out b) && (b.CostUsd > 0.0 || b.TotalTokens > 0L || b.Turns > 0);
        }

        private static double GetHeatmapMetricValue(DayHeatBucket b, int mode)
        {
            if (b == null) return 0.0;
            if (mode == 1) return (double)b.TotalTokens;
            if (mode == 2) return (double)b.Turns;
            return b.CostUsd;
        }

        private static int ComputeIntensityLevel(double val, double maxVal)
        {
            if (val <= 0.0 || maxVal <= 0.0) return 0;
            double ratio = val / maxVal;
            if (ratio > 1.0) ratio = 1.0;
            // 采用平方根伽马映射，避免单日极端峰值压缩日常开发色阶对比度
            double gamma = Math.Sqrt(ratio);
            if (gamma < 0.28) return 1;
            if (gamma < 0.54) return 2;
            if (gamma < 0.78) return 3;
            return 4;
        }

        private static SolidColorBrush[] GetHeatmapLevelBrushes(bool isDark, bool useEmeraldScale)
        {
            if (useEmeraldScale)
            {
                if (isDark)
                {
                    return new SolidColorBrush[]
                    {
                        DesignSystem.BrushHex("#16161B"),
                        DesignSystem.BrushHex("#0E3226"),
                        DesignSystem.BrushHex("#115D43"),
                        DesignSystem.BrushHex("#109668"),
                        DesignSystem.BrushHex("#34D399")
                    };
                }
                return new SolidColorBrush[]
                {
                    DesignSystem.BrushHex("#ECEEF2"),
                    DesignSystem.BrushHex("#D1FAE5"),
                    DesignSystem.BrushHex("#6EE7B7"),
                    DesignSystem.BrushHex("#10B981"),
                    DesignSystem.BrushHex("#047857")
                };
            }

            if (isDark)
            {
                return new SolidColorBrush[]
                {
                    DesignSystem.BrushHex("#16161B"),
                    DesignSystem.BrushHex("#1E2148"),
                    DesignSystem.BrushHex("#313882"),
                    DesignSystem.BrushHex("#525CE0"),
                    DesignSystem.BrushHex("#818CF8")
                };
            }
            return new SolidColorBrush[]
            {
                DesignSystem.BrushHex("#ECEEF2"),
                DesignSystem.BrushHex("#E0E7FF"),
                DesignSystem.BrushHex("#A5B4FC"),
                DesignSystem.BrushHex("#6366F1"),
                DesignSystem.BrushHex("#3730A3")
            };
        }

        // ====================================================================
        // 3. 24 小时编码生物钟分布图（24h Circadian Activity & TPS Chart）
        // ====================================================================
        private UIElement BuildCircadianSection()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(18, 16, 18, 14));
            card.Margin = new Thickness(0, 0, 0, 14);

            StackPanel wrap = new StackPanel();

            // 头部：标题 + 黄金高效时段 / 深夜高频时段 / 平均 TPS 徽章
            Grid hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.Margin = new Thickness(0, 0, 0, 14);

            StackPanel leftTitle = new StackPanel();
            StackPanel row1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Path actIcon = UiIcons.CreateIcon(UiIcons.Activity, 15, "AccentText");
            actIcon.Margin = new Thickness(0, 0, 8, 0);
            row1.Children.Add(actIcon);

            TextBlock titleTb = new TextBlock
            {
                Text = "24 小时编码生物钟与推理速率分布",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            row1.Children.Add(titleTb);
            leftTitle.Children.Add(row1);

            TextBlock subTb = new TextBlock
            {
                Text = "基于实时流式请求时间戳刻画 00:00–23:00 研发节律、峰值心流窗口与云端 API 吞吐速率 (TPS)",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                Margin = new Thickness(23, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            leftTitle.Children.Add(subTb);
            Grid.SetColumn(leftTitle, 0);
            hdr.Children.Add(leftTitle);

            WrapPanel rightBadges = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };

            Border goldenPill = CreateSemanticPill("黄金高效时段 —", "success", out _goldenWindowBadgeText);
            goldenPill.Margin = new Thickness(0, 2, 8, 2);
            rightBadges.Children.Add(goldenPill);

            _nightOwlBadgeBorder = CreateSemanticPill("深夜活跃 —", "neutral", out _nightOwlBadgeText);
            _nightOwlBadgeBorder.Margin = new Thickness(0, 2, 8, 2);
            rightBadges.Children.Add(_nightOwlBadgeBorder);

            Border tpsPill = CreateSemanticPill("平均 TPS —", "accent", out _avgTpsBadgeText);
            tpsPill.Margin = new Thickness(0, 2, 0, 2);
            rightBadges.Children.Add(tpsPill);

            Grid.SetColumn(rightBadges, 1);
            hdr.Children.Add(rightBadges);
            wrap.Children.Add(hdr);

            // 图表舞台
            Border chartStage = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 8),
                SnapsToDevicePixels = true
            };
            chartStage.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            chartStage.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel stageInner = new StackPanel();

            // 顶部微图例说明
            Grid stageLegend = new Grid();
            stageLegend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stageLegend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stageLegend.Margin = new Thickness(2, 0, 2, 8);

            TextBlock zoneNote = new TextBlock
            {
                Text = "时段分区：00–05 深夜极客区  ·  06–18 日间主力研发  ·  19–23 晚间沉浸冲刺",
                FontFamily = DesignSystem.FontUi,
                FontSize = 10.8
            };
            zoneNote.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(zoneNote, 0);
            stageLegend.Children.Add(zoneNote);

            TextBlock dotNote = new TextBlock
            {
                Text = "柱顶圆点 = 该时段平均输出 TPS（翠绿高速 / 靛蓝常规 / 琥珀拥堵）",
                FontFamily = DesignSystem.FontUi,
                FontSize = 10.8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            dotNote.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(dotNote, 1);
            stageLegend.Children.Add(dotNote);
            stageInner.Children.Add(stageLegend);

            // 24 列等分柱状矩阵
            UniformGrid barsGrid = new UniformGrid
            {
                Columns = 24,
                Rows = 1,
                Height = 122,
                SnapsToDevicePixels = true
            };

            for (int h = 0; h < 24; h++)
            {
                int capturedHour = h;
                Border colHost = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(2, 2, 2, 0),
                    Cursor = Cursors.Hand
                };

                Grid colGrid = new Grid();
                colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
                colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
                colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });

                Ellipse tpsDot = new Ellipse
                {
                    Width = 5.5,
                    Height = 5.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Hidden
                };
                Grid.SetRow(tpsDot, 0);
                colGrid.Children.Add(tpsDot);
                _hourTpsDots[h] = tpsDot;

                Border barTrack = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                barTrack.SetResourceReference(Border.BackgroundProperty, "BgCard");

                Border barFill = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                barTrack.Child = barFill;
                Grid.SetRow(barTrack, 1);
                colGrid.Children.Add(barTrack);
                _hourBars[h] = barFill;

                TextBlock hLbl = new TextBlock
                {
                    Text = h.ToString("D2", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ModernControlsFactory.EnableTabularNumbers(hLbl, true);
                hLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                Grid.SetRow(hLbl, 2);
                colGrid.Children.Add(hLbl);
                _hourLabels[h] = hLbl;

                colHost.Child = colGrid;
                colHost.MouseEnter += delegate(object s, MouseEventArgs e)
                {
                    Border b = s as Border;
                    if (b != null)
                        b.SetResourceReference(Border.BackgroundProperty, "BgHover");
                    string msg = _hourHoverSummaries[capturedHour];
                    if (_circadianHoverReadout != null && !string.IsNullOrEmpty(msg))
                        _circadianHoverReadout.Text = msg;
                };
                colHost.MouseLeave += delegate(object s, MouseEventArgs e)
                {
                    Border b = s as Border;
                    if (b != null)
                        b.Background = Brushes.Transparent;
                    if (_circadianHoverReadout != null)
                        _circadianHoverReadout.Text = _circadianDefaultFooterText;
                };

                _hourColumns[h] = colHost;
                barsGrid.Children.Add(colHost);
            }

            stageInner.Children.Add(barsGrid);
            chartStage.Child = stageInner;
            wrap.Children.Add(chartStage);

            // 底部实时读数与节律总结
            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Margin = new Thickness(2, 9, 2, 0);

            _circadianHoverReadout = new TextBlock
            {
                Text = "悬停任意小时柱体查看该时段请求频次、输出 Token 与平均流式 TPS",
                FontSize = 11.3,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(_circadianHoverReadout, true);
            _circadianHoverReadout.FontFamily = DesignSystem.FontUi;
            _circadianHoverReadout.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            Grid.SetColumn(_circadianHoverReadout, 0);
            foot.Children.Add(_circadianHoverReadout);

            _circadianInsightRightText = new TextBlock
            {
                Text = "",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            _circadianInsightRightText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            Grid.SetColumn(_circadianInsightRightText, 1);
            foot.Children.Add(_circadianInsightRightText);

            wrap.Children.Add(foot);
            card.Child = wrap;
            return card;
        }

        private void RenderCircadianChart()
        {
            if (_hourBars[0] == null) return;

            int[] reqCounts = new int[24];
            long[] outTokens = new long[24];
            long[] totTokens = new long[24];
            double[] costUsd = new double[24];
            long[] tpsWeightedOut = new long[24];
            long[] tpsWeightedDurMs = new long[24];

            int totalSampleReqs = 0;
            long overallTpsOut = 0L;
            long overallTpsDurMs = 0L;

            for (int i = 0; i < _recentRequests.Count; i++)
            {
                RecentRequestRecord rq = _recentRequests[i];
                if (rq == null) continue;
                int hr = ExtractLocalHour(rq);
                if (hr < 0 || hr > 23) continue;

                reqCounts[hr]++;
                outTokens[hr] += rq.Output;
                totTokens[hr] += rq.TotalTokens;
                costUsd[hr] += rq.CostUsd;
                totalSampleReqs++;

                double singleTps = FormatterUtil.ComputeTps(rq.Output, rq.DurationMs);
                if (singleTps > 0.0)
                {
                    tpsWeightedOut[hr] += rq.Output;
                    tpsWeightedDurMs[hr] += rq.DurationMs;
                    overallTpsOut += rq.Output;
                    overallTpsDurMs += rq.DurationMs;
                }
            }

            double[] avgTps = new double[24];
            int fastestHour = -1;
            double fastestTps = 0.0;
            for (int h = 0; h < 24; h++)
            {
                if (tpsWeightedDurMs[h] > 0L)
                {
                    avgTps[h] = (tpsWeightedOut[h] * 1000.0) / tpsWeightedDurMs[h];
                    if (reqCounts[h] >= 2 && avgTps[h] > fastestTps)
                    {
                        fastestTps = avgTps[h];
                        fastestHour = h;
                    }
                }
            }
            if (fastestHour < 0)
            {
                for (int h = 0; h < 24; h++)
                {
                    if (avgTps[h] > fastestTps)
                    {
                        fastestTps = avgTps[h];
                        fastestHour = h;
                    }
                }
            }

            double overallMeanTps = overallTpsDurMs > 0L ? ((overallTpsOut * 1000.0) / overallTpsDurMs) : 0.0;

            // 识别连续 3 小时黄金高效心流时段
            int goldenStart = -1;
            double bestWindowScore = 0.0;
            int maxHourReqs = 0;
            long maxHourOut = 0L;
            for (int h = 0; h < 24; h++)
            {
                if (reqCounts[h] > maxHourReqs) maxHourReqs = reqCounts[h];
                if (outTokens[h] > maxHourOut) maxHourOut = outTokens[h];
            }

            if (totalSampleReqs > 0)
            {
                for (int start = 0; start < 24; start++)
                {
                    int wReqs = 0;
                    long wOut = 0L;
                    for (int k = 0; k < 3; k++)
                    {
                        int hh = (start + k) % 24;
                        wReqs += reqCounts[hh];
                        wOut += outTokens[hh];
                    }
                    double reqNorm = maxHourReqs > 0 ? ((double)wReqs / (maxHourReqs * 3.0)) : 0.0;
                    double outNorm = maxHourOut > 0L ? ((double)wOut / (maxHourOut * 3.0)) : 0.0;
                    double score = reqNorm * 0.55 + outNorm * 0.45;
                    if (score > bestWindowScore)
                    {
                        bestWindowScore = score;
                        goldenStart = start;
                    }
                }
            }

            // 识别深夜高频时段（23:00 - 05:59）
            int nightReqs = reqCounts[23];
            int peakNightHour = reqCounts[23] > 0 ? 23 : -1;
            int peakNightVal = reqCounts[23];
            for (int h = 0; h <= 5; h++)
            {
                nightReqs += reqCounts[h];
                if (reqCounts[h] > peakNightVal)
                {
                    peakNightVal = reqCounts[h];
                    peakNightHour = h;
                }
            }
            double nightSharePct = totalSampleReqs > 0 ? ((nightReqs * 100.0) / totalSampleReqs) : 0.0;

            // 更新顶部三枚徽章
            if (_goldenWindowBadgeText != null)
            {
                if (goldenStart >= 0 && totalSampleReqs > 0)
                {
                    int goldenEnd = (goldenStart + 3) % 24;
                    int gReqs = reqCounts[goldenStart] + reqCounts[(goldenStart + 1) % 24] + reqCounts[(goldenStart + 2) % 24];
                    double gShare = (gReqs * 100.0) / totalSampleReqs;
                    _goldenWindowBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "黄金高效时段 {0:D2}:00–{1:D2}:00 ({2:F0}%)",
                        goldenStart,
                        goldenEnd,
                        gShare);
                }
                else
                {
                    _goldenWindowBadgeText.Text = "黄金高效时段 待采样";
                }
                ModernControlsFactory.EnableTabularNumbers(_goldenWindowBadgeText, false);
                _goldenWindowBadgeText.FontFamily = DesignSystem.FontUi;
            }

            if (_nightOwlBadgeBorder != null && _nightOwlBadgeText != null)
            {
                if (totalSampleReqs == 0)
                {
                    ApplyPillVariant(_nightOwlBadgeBorder, _nightOwlBadgeText, "neutral");
                    _nightOwlBadgeText.Text = "深夜节律 待采样";
                }
                else if (nightSharePct >= 22.0)
                {
                    ApplyPillVariant(_nightOwlBadgeBorder, _nightOwlBadgeText, "warning");
                    _nightOwlBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "深夜高频 {0:F0}% (峰值 {1:D2}:00)",
                        nightSharePct,
                        peakNightHour >= 0 ? peakNightHour : 0);
                }
                else if (nightReqs > 0)
                {
                    ApplyPillVariant(_nightOwlBadgeBorder, _nightOwlBadgeText, "neutral");
                    _nightOwlBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "深夜低频 {0:F0}% · 作息稳健",
                        nightSharePct);
                }
                else
                {
                    ApplyPillVariant(_nightOwlBadgeBorder, _nightOwlBadgeText, "success");
                    _nightOwlBadgeText.Text = "23:00–06:00 零熬夜调用";
                }
                ModernControlsFactory.EnableTabularNumbers(_nightOwlBadgeText, false);
                _nightOwlBadgeText.FontFamily = DesignSystem.FontUi;
            }

            if (_avgTpsBadgeText != null)
            {
                _avgTpsBadgeText.Text = overallMeanTps > 0.0
                    ? ("全天均速 " + overallMeanTps.ToString("F1", CultureInfo.InvariantCulture) + " tok/s")
                    : "TPS 速率 待采样";
                ModernControlsFactory.EnableTabularNumbers(_avgTpsBadgeText, false);
                _avgTpsBadgeText.FontFamily = DesignSystem.FontUi;
            }

            // 绘制 24 小时柱体与 TPS 速率圆点
            SolidColorBrush goldenBarBrush = DesignSystem.BrushHex(_isDark ? "#34D399" : "#059669");
            SolidColorBrush nightBarBrush = DesignSystem.BrushHex(_isDark ? "#A78BFA" : "#7C3AED");
            SolidColorBrush dayBarBrush = DesignSystem.BrushHex(_isDark ? "#6366F1" : "#4F46E5");
            SolidColorBrush idleBarBrush = DesignSystem.BrushHex(_isDark ? "#23232B" : "#E4E4E7");

            for (int h = 0; h < 24; h++)
            {
                bool isGolden = goldenStart >= 0 &&
                                (h == goldenStart || h == ((goldenStart + 1) % 24) || h == ((goldenStart + 2) % 24));
                bool isLateNight = (h == 23 || h <= 5);

                double ratio = (maxHourReqs > 0 && reqCounts[h] > 0)
                    ? ((double)reqCounts[h] / maxHourReqs)
                    : 0.0;

                Border bar = _hourBars[h];
                if (reqCounts[h] > 0)
                {
                    bar.Height = Math.Max(6.0, Math.Round(ratio * 84.0));
                    bar.Background = isGolden ? goldenBarBrush : (isLateNight ? nightBarBrush : dayBarBrush);
                }
                else
                {
                    bar.Height = 3.0;
                    bar.Background = idleBarBrush;
                }

                Ellipse dot = _hourTpsDots[h];
                if (avgTps[h] > 0.0)
                {
                    dot.Visibility = Visibility.Visible;
                    if (overallMeanTps > 0.0 && avgTps[h] >= overallMeanTps * 1.12)
                        dot.SetResourceReference(Shape.FillProperty, "SemanticSuccess");
                    else if (overallMeanTps > 0.0 && avgTps[h] <= overallMeanTps * 0.78)
                        dot.SetResourceReference(Shape.FillProperty, "SemanticWarning");
                    else
                        dot.SetResourceReference(Shape.FillProperty, "AccentText");
                }
                else
                {
                    dot.Visibility = Visibility.Hidden;
                }

                TextBlock lbl = _hourLabels[h];
                lbl.FontWeight = isGolden ? FontWeights.Bold : FontWeights.Normal;
                lbl.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    isGolden ? "SemanticSuccess" : (reqCounts[h] > 0 ? "FgSecondary" : "FgMuted"));

                string zoneTag = isGolden ? "黄金高效窗口" : (isLateNight ? "深夜时段" : "常规时段");
                string tpsLabel = avgTps[h] > 0.0 ? FormatterUtil.FormatTps(avgTps[h]) : "—";
                string spendLabel = FormatterUtil.FormatMoney(costUsd[h], _isCny, _usdToCny);

                _hourColumns[h].ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D2}:00 – {0:D2}:59 ({1})\n请求频次：{2} 次\n输出生成：{3} tok (总计 {4} tok)\n时段花费：{5}\n平均吞吐：{6}",
                    h,
                    zoneTag,
                    reqCounts[h],
                    FormatterUtil.FormatTokens(outTokens[h]),
                    FormatterUtil.FormatTokens(totTokens[h]),
                    spendLabel,
                    tpsLabel);

                _hourHoverSummaries[h] = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D2}:00–{0:D2}:59 ({1})  ·  请求 {2} 次  ·  输出 {3} tok  ·  花费 {4}  ·  均速 {5}",
                    h,
                    zoneTag,
                    reqCounts[h],
                    FormatterUtil.FormatTokens(outTokens[h]),
                    spendLabel,
                    tpsLabel);
            }

            if (totalSampleReqs > 0)
            {
                _circadianDefaultFooterText = string.Format(
                    CultureInfo.InvariantCulture,
                    "已分析最近 {0} 条流式请求时间戳样本（悬停柱体查看分时吞吐详情）",
                    totalSampleReqs.ToString("N0", CultureInfo.InvariantCulture));

                if (_circadianInsightRightText != null)
                {
                    _circadianInsightRightText.Text = fastestHour >= 0
                        ? string.Format(
                            CultureInfo.InvariantCulture,
                            "最快响应窗口：{0:D2}:00 ({1:F1} tok/s)",
                            fastestHour,
                            fastestTps)
                        : "";
                }
            }
            else
            {
                _circadianDefaultFooterText = "暂无带精确毫秒时间戳的实时请求样本，发起新对话后将自动点亮 24h 生物钟曲线";
                if (_circadianInsightRightText != null)
                    _circadianInsightRightText.Text = "";
            }

            if (_circadianHoverReadout != null)
                _circadianHoverReadout.Text = _circadianDefaultFooterText;
        }

        private static int ExtractLocalHour(RecentRequestRecord rq)
        {
            if (rq == null) return -1;
            if (rq.TimestampMs > 0L)
            {
                try
                {
                    long ms = rq.TimestampMs < 10000000000L ? (rq.TimestampMs * 1000L) : rq.TimestampMs;
                    return UnixEpochUtc.AddMilliseconds(ms).ToLocalTime().Hour;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(rq.TimeLabel))
            {
                int colon = rq.TimeLabel.IndexOf(':');
                if (colon >= 2)
                {
                    int hr;
                    if (int.TryParse(rq.TimeLabel.Substring(colon - 2, 2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hr))
                    {
                        if (hr >= 0 && hr <= 23)
                            return hr;
                    }
                }
            }
            return -1;
        }

        // ====================================================================
        // 4. 智能省钱与缓存效率诊断建议（FinOps Optimization Advisor）
        // ====================================================================
        private UIElement BuildAdvisorSection()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(18, 16, 18, 16));
            card.Margin = new Thickness(0, 0, 0, 4);

            StackPanel wrap = new StackPanel();

            Grid hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Margin = new Thickness(0, 0, 0, 14);

            StackPanel leftTitle = new StackPanel();
            StackPanel row1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Path advIcon = UiIcons.CreateIcon(UiIcons.Models, 15, "AccentText");
            advIcon.Margin = new Thickness(0, 0, 8, 0);
            row1.Children.Add(advIcon);

            TextBlock titleTb = new TextBlock
            {
                Text = "FinOps 智能省钱与缓存效率诊断建议",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            row1.Children.Add(titleTb);
            leftTitle.Children.Add(row1);

            TextBlock subTb = new TextBlock
            {
                Text = "自动扫描月底账单燃烧率预测、Prompt Cache 命中缺口、旗舰模型短请求错配与 >200K 上下文双倍溢价",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.5,
                Margin = new Thickness(23, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            leftTitle.Children.Add(subTb);

            Grid.SetColumn(leftTitle, 0);
            hdr.Children.Add(leftTitle);

            _advisorPotentialBadgeBorder = CreateSemanticPill("智能诊断就绪", "accent", out _advisorPotentialBadgeText);
            _advisorPotentialBadgeBorder.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(_advisorPotentialBadgeBorder, 1);
            hdr.Children.Add(_advisorPotentialBadgeBorder);

            wrap.Children.Add(hdr);

            _advisorCardsGrid = new UniformGrid();
            _advisorCardsGrid.Columns = 2;
            wrap.Children.Add(_advisorCardsGrid);

            card.Child = wrap;
            return card;
        }

        private void RenderFinOpsAdvisor()
        {
            if (_advisorCardsGrid == null) return;
            _advisorCardsGrid.Children.Clear();

            DateTime start30d = _refDate.AddDays(-29);
            DateTime start7d = _refDate.AddDays(-6);
            DateTime startPrev7d = _refDate.AddDays(-13);
            DateTime endPrev7d = _refDate.AddDays(-7);

            double mtdUsd = 0.0;
            double last7dUsd = 0.0;
            double prev7dUsd = 0.0;
            double total30dUsd = 0.0;
            double baseline30dUsd = 0.0;
            long fresh30d = 0L;
            long cacheRead30d = 0L;
            long cacheWrite30d = 0L;
            long out30d = 0L;
            int turns30d = 0;

            Dictionary<string, ToolModelAudit> audits = new Dictionary<string, ToolModelAudit>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _allRecords.Count; i++)
            {
                RecordBucket r = _allRecords[i];
                if (r == null) continue;
                DateTime dt;
                if (!TryParseYmd(r.Date, out dt)) continue;

                if (dt.Year == _refDate.Year && dt.Month == _refDate.Month && dt <= _refDate)
                    mtdUsd += r.CostUsd;

                if (dt >= start7d && dt <= _refDate)
                    last7dUsd += r.CostUsd;
                else if (dt >= startPrev7d && dt <= endPrev7d)
                    prev7dUsd += r.CostUsd;

                if (dt >= start30d && dt <= _refDate)
                {
                    total30dUsd += r.CostUsd;
                    baseline30dUsd += (r.BaselineUsd > r.CostUsd ? r.BaselineUsd : r.CostUsd);
                    fresh30d += r.FreshInput;
                    cacheRead30d += r.CacheRead;
                    cacheWrite30d += r.CacheWrite;
                    out30d += r.Output;
                    turns30d += r.Turns;

                    string k = (r.Tool ?? "API") + "||" + (r.Model ?? "unknown");
                    ToolModelAudit a;
                    if (!audits.TryGetValue(k, out a))
                    {
                        a = new ToolModelAudit();
                        a.Tool = string.IsNullOrEmpty(r.Tool) ? "API" : r.Tool;
                        a.Model = string.IsNullOrEmpty(r.Model) ? "unknown" : r.Model;
                        audits[k] = a;
                    }
                    a.Turns += r.Turns;
                    a.FreshInput += r.FreshInput;
                    a.CacheRead += r.CacheRead;
                    a.CacheWrite += r.CacheWrite;
                    a.Output += r.Output;
                    a.CostUsd += r.CostUsd;
                    a.BaselineUsd += (r.BaselineUsd > r.CostUsd ? r.BaselineUsd : r.CostUsd);
                }
            }

            List<AdvisorCardModel> cards = new List<AdvisorCardModel>(4);
            double totalActionableSaveUsd = 0.0;

            // --- 卡片 1：当月花费按近 7 日均速的月底预测值（Monthly Burn Forecast）---
            int daysInMonth = DateTime.DaysInMonth(_refDate.Year, _refDate.Month);
            int remainingDays = Math.Max(0, daysInMonth - _refDate.Day);
            double dailyBurn7d = last7dUsd / 7.0;
            double forecastMonthUsd = mtdUsd + (dailyBurn7d * remainingDays);
            double wowDeltaPct = prev7dUsd > 0.05 ? (((last7dUsd - prev7dUsd) / prev7dUsd) * 100.0) : 0.0;

            AdvisorCardModel burnCard = new AdvisorCardModel();
            burnCard.IconPath = UiIcons.Overview;
            burnCard.Title = "月度燃烧率与月底账单预测 (Burn Forecast)";
            burnCard.BadgeText = "月底预测 " + FormatterUtil.FormatMoney(forecastMonthUsd, _isCny, _usdToCny);

            if (wowDeltaPct >= 30.0 && dailyBurn7d >= 1.5)
            {
                double controllableUsd = (dailyBurn7d * 0.25) * remainingDays;
                totalActionableSaveUsd += controllableUsd;
                burnCard.Severity = "warning";
                burnCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "本月累计已消耗 {0}，近 7 日均速达 {1}/天（环比上周 +{2:F0}%）。按此增速预计月底达 {3}，建议为高频工具启用包月封顶或设置每日预算告警，预计余下 {4} 天可控支省下约 {5}。",
                    FormatterUtil.FormatMoney(mtdUsd, _isCny, _usdToCny),
                    FormatterUtil.FormatMoney(dailyBurn7d, _isCny, _usdToCny),
                    wowDeltaPct,
                    FormatterUtil.FormatMoney(forecastMonthUsd, _isCny, _usdToCny),
                    remainingDays,
                    FormatterUtil.FormatMoney(controllableUsd, _isCny, _usdToCny));
            }
            else
            {
                burnCard.Severity = "accent";
                string wowTag = prev7dUsd > 0.05
                    ? string.Format(CultureInfo.InvariantCulture, "环比上周 {0}{1:F0}%", wowDeltaPct >= 0 ? "+" : "", wowDeltaPct)
                    : "增速平稳";
                burnCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "本月前 {0} 天已发生等效账单 {1}，近 7 日平均燃烧率 {2}/天（{3}）。按线性速度外推，{4} 月底总消耗预计收敛于 {5}。",
                    _refDate.Day,
                    FormatterUtil.FormatMoney(mtdUsd, _isCny, _usdToCny),
                    FormatterUtil.FormatMoney(dailyBurn7d, _isCny, _usdToCny),
                    wowTag,
                    _refDate.Month,
                    FormatterUtil.FormatMoney(forecastMonthUsd, _isCny, _usdToCny));
            }
            burnCard.TelemetryFooter = string.Format(
                CultureInfo.InvariantCulture,
                "MTD 实付 {0}  ·  7d 均速 {1}/d  ·  当月剩余 {2} 天",
                FormatterUtil.FormatMoney(mtdUsd, _isCny, _usdToCny),
                FormatterUtil.FormatMoney(dailyBurn7d, _isCny, _usdToCny),
                remainingDays);
            cards.Add(burnCard);

            // --- 卡片 2：Prompt Cache 命中率与低效缓存工具审计 ---
            long totalPrompt30d = fresh30d + cacheRead30d + cacheWrite30d;
            double overallCacheHitPct = totalPrompt30d > 0L ? ((cacheRead30d * 100.0) / totalPrompt30d) : 0.0;
            double alreadySavedByCacheUsd = Math.Max(0.0, baseline30dUsd - total30dUsd);

            ToolModelAudit worstCacheTarget = null;
            double bestCachePotentialUsd = 0.0;
            double worstCacheHitPct = 0.0;

            foreach (KeyValuePair<string, ToolModelAudit> kv in audits)
            {
                ToolModelAudit a = kv.Value;
                long pTok = a.FreshInput + a.CacheRead + a.CacheWrite;
                if (pTok < 80000L || a.CostUsd < 0.60) continue;
                double hit = (a.CacheRead * 100.0) / pTok;
                if (hit < 68.0)
                {
                    // 估算若将命中率提升至 82%（释放重复 FreshInput 的 85% 缓存折扣）可节省金额
                    double targetHit = 0.82;
                    double convertibleRatio = targetHit - (hit / 100.0);
                    if (convertibleRatio > 0.0)
                    {
                        double inputShareApprox = (double)a.FreshInput / Math.Max(1.0, (double)(a.FreshInput + a.Output * 3L));
                        double saveEst = a.CostUsd * inputShareApprox * convertibleRatio * 0.82;
                        if (saveEst > bestCachePotentialUsd)
                        {
                            bestCachePotentialUsd = saveEst;
                            worstCacheTarget = a;
                            worstCacheHitPct = hit;
                        }
                    }
                }
            }

            AdvisorCardModel cacheCard = new AdvisorCardModel();
            cacheCard.IconPath = UiIcons.Shield;
            cacheCard.Title = "Prompt Cache 缓存命中率审计 (Cache Audit)";

            if (worstCacheTarget != null && bestCachePotentialUsd >= 0.40)
            {
                totalActionableSaveUsd += bestCachePotentialUsd;
                cacheCard.Severity = "warning";
                cacheCard.BadgeText = "优化可省 " + FormatterUtil.FormatMoney(bestCachePotentialUsd, _isCny, _usdToCny) + "/月";
                cacheCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "定位到 [{0} · {1}] 缓存命中率仅 {2:F1}%（远低于 85%+ 理想水位），大量上下文作为全价新增 Input 重复计费。保持长会话连续性并固定 System Prompt 前缀，预计每月可再省 {3}。",
                    worstCacheTarget.Tool,
                    worstCacheTarget.Model,
                    worstCacheHitPct,
                    FormatterUtil.FormatMoney(bestCachePotentialUsd, _isCny, _usdToCny));
            }
            else
            {
                cacheCard.Severity = "success";
                cacheCard.BadgeText = "缓存已省 " + FormatterUtil.FormatMoney(alreadySavedByCacheUsd, _isCny, _usdToCny);
                cacheCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "近 30 天全局 Prompt Cache 综合命中率达 {0:F1}%，相比无缓存基准账单（{1}）已自动为您减免 {2} 算力成本，上下文前缀复用率表现优异。",
                    overallCacheHitPct,
                    FormatterUtil.FormatMoney(baseline30dUsd, _isCny, _usdToCny),
                    FormatterUtil.FormatMoney(alreadySavedByCacheUsd, _isCny, _usdToCny));
            }
            cacheCard.TelemetryFooter = string.Format(
                CultureInfo.InvariantCulture,
                "综合命中率 {0:F1}%  ·  缓存读取 {1}  ·  累计已省 {2}",
                overallCacheHitPct,
                FormatterUtil.FormatTokens(cacheRead30d),
                FormatterUtil.FormatMoney(alreadySavedByCacheUsd, _isCny, _usdToCny));
            cards.Add(cacheCard);

            // --- 卡片 3：高单价旗舰模型低复杂度短输出占比诊断（Model Tiering）---
            int flagshipTotalSample = 0;
            int flagshipShortSample = 0;
            double flagshipShortSampleUsd = 0.0;
            string topFlagshipModel = "";
            double topFlagship30dUsd = 0.0;
            double allFlagship30dUsd = 0.0;
            int lowAvgOutFlagshipTurns = 0;
            double lowAvgOutFlagshipUsd = 0.0;

            for (int i = 0; i < _recentRequests.Count; i++)
            {
                RecentRequestRecord rq = _recentRequests[i];
                if (rq == null || !IsFlagshipHighPriceModel(rq.Model)) continue;
                flagshipTotalSample++;
                if (rq.Output > 0L && rq.Output <= 240L)
                {
                    flagshipShortSample++;
                    flagshipShortSampleUsd += rq.CostUsd;
                }
            }

            foreach (KeyValuePair<string, ToolModelAudit> kv in audits)
            {
                ToolModelAudit a = kv.Value;
                if (!IsFlagshipHighPriceModel(a.Model)) continue;
                allFlagship30dUsd += a.CostUsd;
                if (a.CostUsd > topFlagship30dUsd)
                {
                    topFlagship30dUsd = a.CostUsd;
                    topFlagshipModel = a.Model;
                }
                long avgOut = a.Turns > 0 ? (a.Output / a.Turns) : 0L;
                if (a.Turns >= 3 && avgOut > 0L && avgOut <= 260L)
                {
                    lowAvgOutFlagshipTurns += a.Turns;
                    lowAvgOutFlagshipUsd += a.CostUsd;
                }
            }

            double shortTurnRatio = flagshipTotalSample >= 4
                ? ((double)flagshipShortSample / flagshipTotalSample)
                : (allFlagship30dUsd > 0.0 ? (lowAvgOutFlagshipUsd / allFlagship30dUsd) : 0.0);
            if (shortTurnRatio > 0.75) shortTurnRatio = 0.75;

            double estimatedRoutingSaveUsd = allFlagship30dUsd * shortTurnRatio * 0.68;
            if (estimatedRoutingSaveUsd < flagshipShortSampleUsd * 0.68)
                estimatedRoutingSaveUsd = flagshipShortSampleUsd * 0.68;

            AdvisorCardModel tierCard = new AdvisorCardModel();
            tierCard.IconPath = UiIcons.Terminal;
            tierCard.Title = "旗舰模型短输出路由优化 (Smart Tiering)";

            if (estimatedRoutingSaveUsd >= 0.45 && !string.IsNullOrEmpty(topFlagshipModel))
            {
                totalActionableSaveUsd += estimatedRoutingSaveUsd;
                tierCard.Severity = "warning";
                tierCard.BadgeText = "降级可省 " + FormatterUtil.FormatMoney(estimatedRoutingSaveUsd, _isCny, _usdToCny) + "/月";
                tierCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "检测到旗舰高单价模型 [{0}] 约 {1:F0}% 请求属于短输出轻量交互（≤240 tok，如检索确认或简单微调）。将探索与轻量改动分流至 Sonnet / Mini / Flash 模型，预计月省 ~68% 对应成本（约 {2}）。",
                    topFlagshipModel,
                    shortTurnRatio * 100.0,
                    FormatterUtil.FormatMoney(estimatedRoutingSaveUsd, _isCny, _usdToCny));
            }
            else
            {
                tierCard.Severity = "success";
                tierCard.BadgeText = "模型分级合理";
                double flagshipShare = total30dUsd > 0.0 ? ((allFlagship30dUsd * 100.0) / total30dUsd) : 0.0;
                tierCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "近 30 天高单价旗舰模型占总账单 {0:F1}%，未检出大规模低复杂度短请求错配。继续保持「主力架构规划用旗舰、日常编码迭代用主力高性价比模型」的分级策略。",
                    flagshipShare);
            }
            tierCard.TelemetryFooter = string.Format(
                CultureInfo.InvariantCulture,
                "30d 旗舰开销 {0}  ·  短输出占比 {1:F0}%  ·  采样 {2} 次",
                FormatterUtil.FormatMoney(allFlagship30dUsd, _isCny, _usdToCny),
                shortTurnRatio * 100.0,
                flagshipTotalSample > 0 ? flagshipTotalSample : lowAvgOutFlagshipTurns);
            cards.Add(tierCard);

            // --- 卡片 4：超长上下文膨胀与 >200K 阶梯双倍溢价诊断（Context Hygiene）---
            int over200kCount = 0;
            int over120kCount = 0;
            double heavyContextUsd = 0.0;

            for (int i = 0; i < _recentRequests.Count; i++)
            {
                RecentRequestRecord rq = _recentRequests[i];
                if (rq == null) continue;
                long promptTok = rq.FreshInput + rq.CacheRead + rq.CacheWrite;
                if (promptTok > 200000L)
                {
                    over200kCount++;
                    heavyContextUsd += rq.CostUsd;
                }
                else if (promptTok > 120000L)
                {
                    over120kCount++;
                    heavyContextUsd += rq.CostUsd * 0.6;
                }
            }

            double heavyBucket30dUsd = 0.0;
            int heavyBucketTurns = 0;
            foreach (KeyValuePair<string, ToolModelAudit> kv in audits)
            {
                ToolModelAudit a = kv.Value;
                if (a.Turns <= 0) continue;
                long avgPrompt = (a.FreshInput + a.CacheRead + a.CacheWrite) / a.Turns;
                if (avgPrompt > 160000L)
                {
                    heavyBucket30dUsd += a.CostUsd;
                    heavyBucketTurns += a.Turns;
                }
            }

            double baseHeavyUsd = Math.Max(heavyContextUsd, heavyBucket30dUsd);
            double compactSaveUsd = baseHeavyUsd * 0.38;
            long avgPromptPerTurn30d = turns30d > 0 ? (totalPrompt30d / turns30d) : 0L;

            AdvisorCardModel ctxCard = new AdvisorCardModel();
            ctxCard.IconPath = UiIcons.Alert;
            ctxCard.Title = "长上下文膨胀与 200K 溢价预警 (Context Hygiene)";

            if ((over200kCount > 0 || over120kCount >= 3 || heavyBucketTurns >= 5) && compactSaveUsd >= 0.35)
            {
                totalActionableSaveUsd += compactSaveUsd;
                ctxCard.Severity = over200kCount > 0 ? "warning" : "accent";
                ctxCard.BadgeText = "精简可省 " + FormatterUtil.FormatMoney(compactSaveUsd, _isCny, _usdToCny);
                ctxCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "扫描到 {0} 次单轮超 200K Token（触发 Sonnet / Gemini Pro 双倍阶梯单价）及 {1} 次 >120K 重载会话。子任务完成后执行 /compact 压缩或新开会话，预计可削减约 {2} 历史冗余账单。",
                    over200kCount > 0 ? over200kCount : heavyBucketTurns,
                    over120kCount,
                    FormatterUtil.FormatMoney(compactSaveUsd, _isCny, _usdToCny));
            }
            else
            {
                ctxCard.Severity = "success";
                ctxCard.BadgeText = "零双倍阶梯溢价";
                ctxCard.IconPath = UiIcons.Check;
                ctxCard.BodyText = string.Format(
                    CultureInfo.InvariantCulture,
                    "近 30 天单轮平均上下文负载为 {0} Token，始终保持在 200K 超长上下文双倍计费红线以内，单次任务拆分颗粒度与会话卫生习惯极佳。",
                    FormatterUtil.FormatTokens(avgPromptPerTurn30d));
            }
            ctxCard.TelemetryFooter = string.Format(
                CultureInfo.InvariantCulture,
                "单轮均载 {0}  ·  >200K 触发 {1} 次  ·  总轮次 {2}",
                FormatterUtil.FormatTokens(avgPromptPerTurn30d),
                over200kCount,
                turns30d.ToString("N0", CultureInfo.InvariantCulture));
            cards.Add(ctxCard);

            // 更新右上角总优化空间徽章
            if (_advisorPotentialBadgeBorder != null && _advisorPotentialBadgeText != null)
            {
                if (totalActionableSaveUsd >= 0.50)
                {
                    ApplyPillVariant(_advisorPotentialBadgeBorder, _advisorPotentialBadgeText, "warning");
                    _advisorPotentialBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "潜在可优化空间 ≈ {0}/月",
                        FormatterUtil.FormatMoney(totalActionableSaveUsd, _isCny, _usdToCny));
                }
                else
                {
                    ApplyPillVariant(_advisorPotentialBadgeBorder, _advisorPotentialBadgeText, "success");
                    _advisorPotentialBadgeText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "算力结构健康 · 缓存已省 {0}",
                        FormatterUtil.FormatMoney(alreadySavedByCacheUsd, _isCny, _usdToCny));
                }
                ModernControlsFactory.EnableTabularNumbers(_advisorPotentialBadgeText, false);
                _advisorPotentialBadgeText.FontFamily = DesignSystem.FontUi;
            }

            // 渲染 2×2 结构化建议卡片
            for (int c = 0; c < cards.Count; c++)
            {
                Thickness m = (c % 2 == 0)
                    ? new Thickness(0, 0, 7, c < 2 ? 12 : 0)
                    : new Thickness(7, 0, 0, c < 2 ? 12 : 0);
                _advisorCardsGrid.Children.Add(BuildSingleAdvisorCard(cards[c], m));
            }
        }

        private UIElement BuildSingleAdvisorCard(AdvisorCardModel item, Thickness margin)
        {
            Border shell = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Margin = margin,
                Padding = new Thickness(14, 12, 14, 12),
                SnapsToDevicePixels = true
            };
            shell.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            shell.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            string accentResKey = "AccentText";
            if (string.Equals(item.Severity, "success", StringComparison.OrdinalIgnoreCase))
                accentResKey = "SemanticSuccess";
            else if (string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                accentResKey = "SemanticWarning";

            Grid outer = new Grid();
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border stripe = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 1, 0, 1)
            };
            stripe.SetResourceReference(Border.BackgroundProperty, accentResKey);
            Grid.SetColumn(stripe, 0);
            outer.Children.Add(stripe);

            StackPanel bodyStack = new StackPanel { Margin = new Thickness(11, 0, 0, 0) };

            // 标题 + 右侧量化节省徽章
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titleSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Path ic = UiIcons.CreateIcon(item.IconPath, 13.5, accentResKey);
            ic.Margin = new Thickness(0, 0, 7, 0);
            titleSp.Children.Add(ic);

            TextBlock titleTb = new TextBlock
            {
                Text = item.Title,
                FontFamily = DesignSystem.FontUi,
                FontSize = 12.8,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleSp.Children.Add(titleTb);
            Grid.SetColumn(titleSp, 0);
            topRow.Children.Add(titleSp);

            TextBlock badgeTb;
            Border badgePill = CreateSemanticPill(item.BadgeText, item.Severity, out badgeTb);
            badgePill.Margin = new Thickness(8, 0, 0, 0);
            ModernControlsFactory.EnableTabularNumbers(badgeTb, false);
            badgeTb.FontFamily = DesignSystem.FontUi;
            Grid.SetColumn(badgePill, 1);
            topRow.Children.Add(badgePill);

            bodyStack.Children.Add(topRow);

            // 诊断正文
            TextBlock descTb = new TextBlock
            {
                Text = item.BodyText,
                FontFamily = DesignSystem.FontUi,
                FontSize = 11.8,
                LineHeight = 18.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 10)
            };
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            bodyStack.Children.Add(descTb);

            // 底部量化遥测证据栏
            Border teleBar = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5)
            };
            teleBar.SetResourceReference(Border.BackgroundProperty, "BgCard");

            TextBlock teleTb = new TextBlock
            {
                Text = item.TelemetryFooter,
                FontSize = 10.8,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(teleTb, true);
            teleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            teleBar.Child = teleTb;
            bodyStack.Children.Add(teleBar);

            Grid.SetColumn(bodyStack, 1);
            outer.Children.Add(bodyStack);

            shell.Child = outer;
            return shell;
        }

        // ====================================================================
        // 辅助方法：生态归类、旗舰模型识别与语义徽章构建
        // ====================================================================
        private static int ClassifyEcosystem(string tool, string model)
        {
            string t = (tool ?? "").ToLowerInvariant();
            string m = (model ?? "").ToLowerInvariant();

            // 1. 优先按官方独立客户端宿主归属：Google AI / Antigravity 绝不归入 Cursor
            if (t.IndexOf("antigravity", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("gemini cli", StringComparison.Ordinal) >= 0)
            {
                return 0; // Google AI / Antigravity
            }

            if (t.IndexOf("claude code", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("claude desktop", StringComparison.Ordinal) >= 0)
            {
                return 1; // Claude 生态
            }

            if (t.IndexOf("codex", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("openai", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("chatgpt", StringComparison.Ordinal) >= 0)
            {
                return 2; // OpenAI / Codex 生态
            }

            if (t.IndexOf("cursor", StringComparison.Ordinal) >= 0)
            {
                return 3; // Cursor 独立订阅生态
            }

            if (t.IndexOf("windsurf", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("copilot", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("trae", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("zed", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("cline", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("roo code", StringComparison.Ordinal) >= 0)
            {
                return 4; // Windsurf / Copilot / 独立 IDE 生态
            }

            if (t.IndexOf("zcode", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("kimi", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("qwen", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("workbuddy", StringComparison.Ordinal) >= 0)
            {
                return 5; // 国产 Coding Plan
            }

            // 2. 通用代理/脚本来源按底层模型家族归集等效价值
            if (m.IndexOf("gemini", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("orchestrator", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("learnlm", StringComparison.Ordinal) >= 0)
            {
                return 0;
            }

            if (m.IndexOf("claude", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("anthropic", StringComparison.Ordinal) >= 0)
            {
                return 1;
            }

            if (m.IndexOf("gpt-", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("codex", StringComparison.Ordinal) >= 0 ||
                m.StartsWith("o1", StringComparison.Ordinal) ||
                m.StartsWith("o3", StringComparison.Ordinal) ||
                m.StartsWith("o4", StringComparison.Ordinal))
            {
                return 2;
            }

            if (m.IndexOf("glm", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("kimi", StringComparison.Ordinal) >= 0 ||
                m.StartsWith("k3", StringComparison.Ordinal) ||
                m.IndexOf("deepseek", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("qwen", StringComparison.Ordinal) >= 0 ||
                m.IndexOf("minimax", StringComparison.Ordinal) >= 0)
            {
                return 5;
            }

            return -1;
        }

        private static bool IsFlagshipHighPriceModel(string rawModel)
        {
            if (string.IsNullOrEmpty(rawModel)) return false;
            string m = rawModel.ToLowerInvariant();
            return m.IndexOf("opus", StringComparison.Ordinal) >= 0 ||
                   m.IndexOf("fable", StringComparison.Ordinal) >= 0 ||
                   m.IndexOf("mythos", StringComparison.Ordinal) >= 0 ||
                   m.IndexOf("gpt-5.4-pro", StringComparison.Ordinal) >= 0 ||
                   m.IndexOf("gpt-6-astra", StringComparison.Ordinal) >= 0 ||
                   m.IndexOf("o3-pro", StringComparison.Ordinal) >= 0 ||
                   string.Equals(m, "o1", StringComparison.Ordinal) ||
                   m.StartsWith("o1-20", StringComparison.Ordinal);
        }

        private static DateTime ResolveReferenceDate(string refDateStr, List<RecordBucket> records)
        {
            DateTime parsed;
            if (TryParseYmd(refDateStr, out parsed))
                return parsed.Date;

            DateTime latest = DateTime.MinValue;
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    RecordBucket r = records[i];
                    if (r == null) continue;
                    DateTime d;
                    if (TryParseYmd(r.Date, out d) && d > latest)
                        latest = d;
                }
            }

            return latest > DateTime.MinValue ? latest.Date : DateTime.Today;
        }

        private static bool TryParseYmd(string s, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (string.IsNullOrEmpty(s) || s.Length < 10)
                return false;
            string sub = s.Length == 10 ? s : s.Substring(0, 10);
            return DateTime.TryParseExact(sub, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
        }

        private static Border CreateSemanticPill(string text, string variant, out TextBlock labelOut)
        {
            Border pill = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };

            labelOut = new TextBlock
            {
                Text = text ?? "",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };

            ApplyPillVariant(pill, labelOut, variant);
            pill.Child = labelOut;
            return pill;
        }

        private static void ApplyPillVariant(Border pill, TextBlock label, string variant)
        {
            if (pill == null || label == null) return;

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                pill.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
                pill.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
                label.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                pill.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
                pill.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");
                label.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
            }
            else if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            {
                pill.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                pill.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
                label.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            }
            else
            {
                pill.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                pill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                label.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            }
        }

        private static Border CreateKpiStatChip(string labelText, string valText, string valResKey, out TextBlock valOut)
        {
            Border chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 3, 9, 3),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            chip.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            TextBlock kLbl = new TextBlock
            {
                Text = labelText + " ",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            kLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(kLbl);

            valOut = new TextBlock
            {
                Text = valText,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(valOut, true);
            valOut.SetResourceReference(TextBlock.ForegroundProperty, valResKey);
            sp.Children.Add(valOut);

            chip.Child = sp;
            return chip;
        }
    }
}
