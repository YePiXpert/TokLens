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
    /// 近 365 天每日用量日历（按花费 / Token / 请求次数着色），显示在「每日汇总」页。
    /// 数据按设备筛选，不受顶部时间范围影响。
    /// </summary>
    public sealed class UsageHeatmapView : Border
    {
        private const int HeatmapWeeks = 53;
        private const int HeatmapDaysPerWeek = 7;

        // 缓存最新渲染上下文，支持内部切换器局部重绘
        private List<RecordBucket> _allRecords = new List<RecordBucket>();
        private bool _isCny = true;
        private double _usdToCny = ExchangeRateService.DefaultRate;
        private bool _isDark = true;
        private string _referenceDateStr = "";
        private DateTime _refDate = DateTime.Today;

        // 交互状态：0 = 按花费，1 = 按 Token，2 = 按请求次数
        private int _heatmapMode = 0;
        private Button[] _heatmapModeBtns;

        // 365 天热力图预分配单元
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

        public UsageHeatmapView()
        {
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Child = BuildHeatmapSection();
        }

        public void Render(
            List<RecordBucket> allRecords,
            bool isCny,
            double usdToCny,
            bool isDark,
            string referenceDate)
        {
            _allRecords = allRecords != null ? allRecords : new List<RecordBucket>();
            _isCny = isCny;
            _usdToCny = usdToCny > 0.0 ? usdToCny : ExchangeRateService.DefaultRate;
            _isDark = isDark;
            _referenceDateStr = referenceDate ?? "";
            _refDate = ResolveReferenceDate(_referenceDateStr, _allRecords);
            RenderContributionHeatmap();
        }

        private UIElement BuildHeatmapSection()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(18, 16, 18, 14));

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
                Text = "近 365 天每日用量",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = DesignSystem.TextTitle,
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
                    FontSize = DesignSystem.TextCaption,
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
                FontSize = DesignSystem.TextCaption,
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
                FontSize = DesignSystem.TextCaption,
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
                FontSize = DesignSystem.TextCaption,
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
                                FontSize = DesignSystem.TextCaption,
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
            SolidColorBrush[] palette = DesignSystem.HeatmapScale(_isDark, _heatmapMode == 0);
            SolidColorBrush emptyBorderBrush = DesignSystem.ThemeBrush("BorderCard", _isDark);

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
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            kLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(kLbl);

            valOut = new TextBlock
            {
                Text = valText,
                FontSize = DesignSystem.TextCaption,
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
