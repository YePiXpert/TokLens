using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TokLens
{
    public sealed class UsageTierTotal
    {
        public string Label;
        public string BasisLabel;
        public long Requests, Tokens, UnpricedRequests;
        public long ComparableRequests;
        public double CostUsd, PremiumUsd;
    }

    // Presentation and export share the same grouping, including the evidence boundary.
    public static class UsagePresentation
    {
        public static bool IsUnpriced(UsageRecordMetadata record)
        { return record != null && record.CostBasis == "unpriced"; }

        public static object ExportMoney(double amount, UsageRecordMetadata record)
        { return IsUnpriced(record) ? null : (object)Math.Round(FormatterUtil.SanitizeMoney(amount), 6); }

        public static string TierName(string raw)
        {
            string tier = UsageServiceTier.Normalize(raw);
            if (tier == "standard") return "Standard";
            if (tier == "fast") return "Fast";
            if (tier == "ultrafast") return "Ultrafast";
            if (tier == "flex") return "Flex";
            if (tier == "batch") return "Batch";
            return "档位未知";
        }

        public static string TierLabel(UsageRecordMetadata record)
        {
            if (record == null) return "档位未知";
            if (UsageServiceTier.Normalize(record.ActualServiceTier) != "unknown") return TierName(record.ActualServiceTier);
            if (UsageServiceTier.Normalize(record.RequestedServiceTier) != "unknown") return TierName(record.RequestedServiceTier) + "（已请求）";
            return "档位未知";
        }

        // Tooltip for the tier badge: what the tier is, which log field supports it, and how the
        // amount was derived. Short lines, because the tooltip does not wrap.
        public static string TierHint(UsageRecordMetadata record)
        {
            if (record == null) return "";
            string tier = UsageServiceTier.Effective(record);
            StringBuilder hint = new StringBuilder();
            if (tier == "unknown") hint.Append("档位未知：日志没有记录这次请求的服务档位");
            else if (UsageServiceTier.Normalize(record.ActualServiceTier) != "unknown") hint.Append("实际档位：").Append(TierName(tier)).Append("（响应中明确报告）");
            else hint.Append("请求档位：").Append(TierName(tier)).Append("（实际执行档位未核实）");
            if (tier != "unknown" && !string.IsNullOrEmpty(record.TierEvidence)) hint.Append("\n证据：").Append(record.TierEvidence);
            return hint.Append("\n金额来源：").Append(CostLabel(record)).ToString();
        }

        public static string CostLabel(UsageRecordMetadata record)
        {
            string basis = record == null ? "" : record.CostBasis;
            if (basis == "reported") return "日志记录金额";
            if (basis == "unpriced") return "未计价";
            if (basis == "estimated_official_reference" || basis == "estimated_current_reference")
            {
                string tier = UsageServiceTier.Effective(record);
                string label = TierName(tier == "unknown" ? "standard" : tier) +
                    (basis == "estimated_official_reference" ? " 官方参考估值 · 渠道未报价" : " 现行参考估值 · 非历史报价");
                if (tier == "unknown") label += " · 档位未知";
                else if (UsageServiceTier.Normalize(record.ActualServiceTier) == "unknown") label += " · 实际档位未核实";
                return label;
            }
            if (basis == "estimated_requested_tier") return "按请求档位估值 · 实际档位未核实";
            if (basis == "estimated_unknown_tier") return "标准档位参考估值 · 档位未知";
            if (basis == "legacy_unknown" || string.IsNullOrEmpty(basis)) return "历史金额 · 来源未记录";
            return "API 估值";
        }

        // Table-cell form of CostLabel: enough to tell two rows of the same model and tier
        // apart. The full label, with its caveats, stays in the cell's tooltip and in exports.
        public static string CostShortLabel(UsageRecordMetadata record)
        {
            string basis = record == null ? "" : record.CostBasis;
            if (basis == "reported") return "日志金额";
            if (basis == "unpriced") return "未计价";
            if (basis == "estimated_official_reference") return "官方参考";
            if (basis == "estimated_current_reference") return "现行参考";
            if (basis == "estimated_requested_tier") return "按请求档位";
            if (basis == "estimated_unknown_tier") return "标准参考";
            if (basis == "legacy_unknown" || string.IsNullOrEmpty(basis)) return "历史金额";
            return "API 估值";
        }

        public static List<UsageTierTotal> Group(IEnumerable<RecordBucket> records)
        {
            Dictionary<string, UsageTierTotal> map = new Dictionary<string, UsageTierTotal>(StringComparer.Ordinal);
            if (records != null) foreach (RecordBucket r in records)
            {
                if (r == null) continue;
                string label = TierLabel(r);
                string key = label + "|" + CostLabel(r);
                UsageTierTotal total;
                if (!map.TryGetValue(key, out total)) { total = new UsageTierTotal { Label = label, BasisLabel = CostLabel(r) }; map.Add(key, total); }
                total.Requests += Math.Max(0, r.Turns);
                total.Tokens += r.TotalTokens;
                if (!IsUnpriced(r)) total.CostUsd += FormatterUtil.SanitizeMoney(r.CostUsd);
                if (IsUnpriced(r)) total.UnpricedRequests += Math.Max(0, r.Turns);
                if (r.ServiceTierPremiumUsd.HasValue) { total.PremiumUsd += r.ServiceTierPremiumUsd.Value; total.ComparableRequests += Math.Max(0, r.Turns); }
            }
            List<UsageTierTotal> rows = new List<UsageTierTotal>(map.Values);
            rows.Sort(delegate(UsageTierTotal a, UsageTierTotal b) { return b.Requests.CompareTo(a.Requests); });
            return rows;
        }
    }

    // 概览页顶部的采集状态条：一行摘要（本机采集是否正常 + 当前筛选范围的服务档位构成），
    // 点击后在浮层里展开每个工具的读取情况与档位明细。浮层不占版面，展开时不会把下方内容顶走。
    public sealed class UsageStatusPanel : Border
    {
        private readonly Action _refresh, _diagnose;
        private readonly Func<bool> _isDark;
        private readonly Ellipse _stateDot;
        private readonly TextBlock _status, _hint, _tierCaption, _updated, _moreTiers;
        private readonly StackPanel _alerts, _tierChips;
        private readonly Grid _fill;
        private readonly Border _divider;
        private readonly Button _toggle;
        private Popup _details;
        private Border _detailsHost;
        private DateTime _detailsClosedAt = DateTime.MinValue;
        private bool _dismissedByPressOnStrip;

        private ScanResult _result;
        private List<ProviderHealth> _sources = new List<ProviderHealth>();
        private List<UsageTierTotal> _groups = new List<UsageTierTotal>();
        private long _requests;
        private bool _cny;
        private double _rate;
        private string _scanError = "";

        /// <summary>摘要条上当前显示的文字；同时作为无障碍名称。</summary>
        internal string SummaryText { get; private set; }

        public UsageStatusPanel(Action refresh, Action diagnose) : this(refresh, diagnose, null) { }

        public UsageStatusPanel(Action refresh, Action diagnose, Func<bool> isDark)
        {
            _refresh = refresh; _diagnose = diagnose; _isDark = isDark;
            SummaryText = "";
            Height = 38; CornerRadius = new CornerRadius(8); BorderThickness = new Thickness(1);
            Padding = new Thickness(12, 0, 6, 0); Margin = new Thickness(0, 0, 0, 10);
            Cursor = Cursors.Hand; SnapsToDevicePixels = true;
            ToolTip = "查看每个工具的读取情况与服务档位明细";
            SetResourceReference(BackgroundProperty, "BgCard"); SetResourceReference(BorderBrushProperty, "BorderCard");

            DockPanel row = new DockPanel { LastChildFill = true };

            _toggle = ModernControlsFactory.CreateIconGhostButton(UiIcons.ChevronDown, "展开采集与档位明细");
            _toggle.Width = 26; _toggle.Height = 26; _toggle.VerticalAlignment = VerticalAlignment.Center;
            _toggle.Click += delegate(object s, RoutedEventArgs e) { ToggleDetails(); };
            DockPanel.SetDock(_toggle, Dock.Right); row.Children.Add(_toggle);

            _updated = Caption("", "FgMuted");
            _updated.Margin = new Thickness(10, 0, 6, 0);
            ModernControlsFactory.EnableTabularNumbers(_updated, false);
            DockPanel.SetDock(_updated, Dock.Right); row.Children.Add(_updated);

            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _stateDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _stateDot.SetResourceReference(Shape.FillProperty, "FgMuted");
            left.Children.Add(_stateDot);
            TextBlock title = new TextBlock { Text = "本机采集", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            left.Children.Add(title);
            _status = new TextBlock { FontSize = DesignSystem.TextBody, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _status.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            ModernControlsFactory.EnableTabularNumbers(_status, false);
            left.Children.Add(_status);
            _alerts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_alerts);
            DockPanel.SetDock(left, Dock.Left); row.Children.Add(left);

            _divider = new Border { Width = 1, Height = 14, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            _divider.SetResourceReference(Border.BackgroundProperty, "BorderStrong");
            DockPanel.SetDock(_divider, Dock.Left); row.Children.Add(_divider);

            _tierCaption = Caption("服务档位", "FgMuted");
            _tierCaption.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(_tierCaption, Dock.Left); row.Children.Add(_tierCaption);

            // 余下宽度：档位胶囊（放不下的整枚收起并显示「+N」，完整明细在浮层里），或没有工具时的引导文字
            _fill = new Grid { ClipToBounds = true };
            _tierChips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            _fill.Children.Add(_tierChips);
            _moreTiers = Caption("", "FgMuted");
            _moreTiers.Visibility = Visibility.Collapsed;
            _hint = Caption("", "FgMuted");
            _hint.TextTrimming = TextTrimming.CharacterEllipsis; _hint.Visibility = Visibility.Collapsed;
            _fill.Children.Add(_hint);
            _fill.SizeChanged += delegate { FitToWidth(); };
            row.Children.Add(_fill);

            Child = row;
            // 明细打开时，按下鼠标这一下被浮层收走（用来关闭它），条本身只会收到随后的抬起。
            // 所以由浮层关闭时「鼠标正按在条上」来判断这次点击已经用掉，而不是靠计时；
            // 条自己收到按下，说明此刻浮层是关着的，标记作废。
            PreviewMouseLeftButtonDown += delegate { _dismissedByPressOnStrip = false; };
            MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { ToggleDetails(); e.Handled = true; };
            MouseEnter += delegate { SetResourceReference(BorderBrushProperty, "BorderStrong"); };
            MouseLeave += delegate { SetResourceReference(BorderBrushProperty, "BorderCard"); };
            SizeChanged += delegate { FitToWidth(); };
            Render(null, null, false, 1.0, "");
        }

        private static TextBlock Caption(string text, string brushKey)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = DesignSystem.TextCaption, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey); return t;
        }

        private static string Count(long value) { return value.ToString("N0", CultureInfo.InvariantCulture); }

        // 服务档位的配色：Fast 系是加价档，用警示色；Standard 用主色；未知用灰。只请求未证实的档位降低不透明度。
        private static string TierBrushKey(string label)
        {
            if (label.StartsWith("Ultrafast", StringComparison.Ordinal)) return "SemanticDanger";
            if (label.StartsWith("Fast", StringComparison.Ordinal)) return "SemanticWarning";
            if (label.StartsWith("Flex", StringComparison.Ordinal)) return "SemanticSuccess";
            if (label.StartsWith("Standard", StringComparison.Ordinal)) return "AccentPrimary";
            if (label.StartsWith("Batch", StringComparison.Ordinal)) return "ChartFresh";
            return "ChartOther";
        }
        private static bool IsRequestedOnly(string label) { return label.EndsWith("（已请求）", StringComparison.Ordinal); }
        private static int TierOrder(string label)
        {
            if (label.StartsWith("Ultrafast", StringComparison.Ordinal)) return 0;
            if (label.StartsWith("Fast", StringComparison.Ordinal)) return 1;
            if (label.StartsWith("Flex", StringComparison.Ordinal)) return 2;
            if (label.StartsWith("Batch", StringComparison.Ordinal)) return 3;
            if (label.StartsWith("Standard", StringComparison.Ordinal)) return 4;
            return 9;
        }

        // 只留下对人有意义的来源，并按「先异常、再按请求量、最后是没有用量的」排序。
        private static List<ProviderHealth> SourceRows(ScanResult result)
        {
            List<ProviderHealth> rows = new List<ProviderHealth>();
            if (result != null && result.ProviderHealth != null)
                foreach (ProviderHealth h in result.ProviderHealth)
                    if (h != null && h.Status != "not_installed") rows.Add(h);
            rows.Sort(delegate(ProviderHealth a, ProviderHealth b)
            {
                int ra = a.Status == "error" ? 0 : a.Status == "no_data" ? 2 : 1, rb = b.Status == "error" ? 0 : b.Status == "no_data" ? 2 : 1;
                if (ra != rb) return ra.CompareTo(rb);
                if (a.ParsedRequests != b.ParsedRequests) return b.ParsedRequests.CompareTo(a.ParsedRequests);
                return string.CompareOrdinal(a.Tool, b.Tool);
            });
            return rows;
        }

        public void Render(ScanResult result, IEnumerable<RecordBucket> scopedRows, bool cny, double rate, string scanError)
        {
            _result = result; _cny = cny; _rate = rate; _scanError = scanError ?? "";
            _sources = SourceRows(result);
            _groups = UsagePresentation.Group(scopedRows);
            _groups.Sort(delegate(UsageTierTotal a, UsageTierTotal b)
            {
                int oa = TierOrder(a.Label), ob = TierOrder(b.Label);
                return oa != ob ? oa.CompareTo(ob) : b.Requests.CompareTo(a.Requests);
            });
            _requests = 0; foreach (UsageTierTotal g in _groups) _requests += g.Requests;

            int ok = 0, idle = 0, bad = 0, waiting = 0;
            foreach (ProviderHealth h in _sources)
            {
                if (h.Status == "error") bad++;
                else if (h.Status == "no_data") idle++;
                else if (h.Status == "stale") waiting++;
                else ok++;
            }

            // 1) 采集状态：一句话 + 需要注意时的提示胶囊
            string dot, status, hint = "";
            _alerts.Children.Clear();
            if (result == null) { dot = "FgMuted"; status = "正在发现本机工具与日志…"; }
            else if (bad > 0 || _scanError.Length > 0)
            {
                dot = "SemanticDanger";
                status = bad > 0 ? Count(bad) + " 个来源读取异常" : "本次扫描失败，已保留上次数据";
                if (ok + waiting > 0) _alerts.Children.Add(Pill(Count(ok + waiting) + " 个正常", null, "neutral"));
            }
            else if (_sources.Count == 0)
            {
                dot = "FgMuted"; status = "尚未发现可读取的用量";
                hint = "在任一 AI 编程工具里完成一次请求后会自动出现，无需登录或配置";
            }
            else if (ok + waiting == 0)
            {
                dot = "SemanticWarning"; status = "发现 " + Count(idle) + " 个工具的日志，但其中没有用量记录";
            }
            else
            {
                dot = waiting > 0 && ok == 0 ? "FgMuted" : "SemanticSuccess";
                status = waiting > 0 && ok == 0 ? "正在刷新 " + Count(waiting) + " 个工具…" : Count(ok + waiting) + " 个工具读取正常";
                if (idle > 0) _alerts.Children.Add(Pill(Count(idle) + " 个暂无用量", null, "neutral"));
            }
            _stateDot.SetResourceReference(Shape.FillProperty, dot);
            _status.Text = status;

            // 2) 当前筛选范围的服务档位构成：同名档位合并成一枚胶囊，区分证据的明细在浮层里
            _tierChips.Children.Clear();
            List<string> order = new List<string>();
            Dictionary<string, long> byLabel = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (UsageTierTotal g in _groups)
            {
                long n;
                if (!byLabel.TryGetValue(g.Label, out n)) order.Add(g.Label);
                byLabel[g.Label] = n + g.Requests;
            }
            StringBuilder spoken = new StringBuilder();
            foreach (string label in order)
            {
                _tierChips.Children.Add(Pill(label, Count(byLabel[label]), label == "档位未知" ? "muted" : TierBrushKey(label)));
                spoken.Append(spoken.Length > 0 ? " · " : "").Append(label).Append(' ').Append(Count(byLabel[label]));
            }
            _tierChips.Children.Add(_moreTiers);
            bool showTiers = result != null && _sources.Count + order.Count > 0;
            if (showTiers && order.Count == 0) hint = "当前范围暂无用量";
            _divider.Visibility = showTiers ? Visibility.Visible : (hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
            _tierCaption.Visibility = showTiers ? Visibility.Visible : Visibility.Collapsed;
            _tierChips.Visibility = order.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _hint.Text = hint; _hint.Visibility = (hint.Length > 0 && order.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

            _updated.Text = result != null && result.AccountingUpdatedMs > 0 ? "更新于 " + ClockTime(result.AccountingUpdatedMs, true) : "";
            FitToWidth();

            SummaryText = "本机采集：" + status + (idle > 0 && bad == 0 && ok + waiting > 0 ? "，" + Count(idle) + " 个暂无用量" : "") +
                (order.Count > 0 ? "；服务档位：" + spoken : (hint.Length > 0 ? "；" + hint : ""));
            AutomationProperties.SetName(this, SummaryText);
            if (_details != null && _details.IsOpen && _detailsHost != null) _detailsHost.Child = BuildDetails(_detailsHost.Width);
        }

        // 窄窗口里把更新时间让给档位胶囊（时间在浮层里也有）；胶囊仍放不下时，整枚收起并显示「+N」，
        // 不把某一枚从中间裁开。
        private void FitToWidth()
        {
            bool roomy = ActualWidth <= 0 || ActualWidth >= 860;
            _updated.Visibility = _updated.Text.Length > 0 && roomy ? Visibility.Visible : Visibility.Collapsed;

            double available = _fill.ActualWidth;
            int pills = _tierChips.Children.Count - (_tierChips.Children.Contains(_moreTiers) ? 1 : 0);
            double total = 0;
            for (int i = 0; i < pills; i++)
            {
                FrameworkElement pill = (FrameworkElement)_tierChips.Children[i];
                pill.Visibility = Visibility.Visible;
                pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                total += pill.DesiredSize.Width;
            }
            _moreTiers.Visibility = Visibility.Collapsed;
            if (available <= 0 || total <= available) return;
            double budget = available - 30, used = 0; int hidden = 0;
            for (int i = 0; i < pills; i++)
            {
                FrameworkElement pill = (FrameworkElement)_tierChips.Children[i];
                // 一旦有一枚放不下，后面的也不再显示：保持「先重要后次要」的顺序，不跳着显示
                if (hidden == 0 && used + pill.DesiredSize.Width <= budget) used += pill.DesiredSize.Width;
                else { pill.Visibility = Visibility.Collapsed; hidden++; }
            }
            _moreTiers.Text = "+" + hidden.ToString(CultureInfo.InvariantCulture);
            _moreTiers.ToolTip = "还有 " + hidden.ToString(CultureInfo.InvariantCulture) + " 个档位，点击查看明细";
            _moreTiers.Visibility = Visibility.Visible;
        }

        private static string ClockTime(long unixMs, bool seconds)
        {
            DateTime local = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(unixMs).ToLocalTime();
            string time = local.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
            return local.Date == DateTime.Today ? time : local.ToString("MM-dd ", CultureInfo.InvariantCulture) + time;
        }

        // 小胶囊：tone 为 neutral / muted，或某个主题色键（档位配色）。
        private static Border Pill(string label, string value, string tone)
        {
            bool neutral = tone == "neutral", muted = tone == "muted";
            Border pill = new Border { Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 9, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, "BgSurface"); pill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            if (neutral) pill.Margin = new Thickness(8, 0, 0, 0);
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!neutral && !muted)
            {
                Ellipse dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = IsRequestedOnly(label) ? 0.55 : 1.0 };
                dot.SetResourceReference(Shape.FillProperty, tone);
                sp.Children.Add(dot);
            }
            TextBlock text = new TextBlock { Text = label, FontSize = DesignSystem.TextCaption, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "FgMuted" : "FgSecondary");
            sp.Children.Add(text);
            if (!string.IsNullOrEmpty(value))
            {
                TextBlock number = new TextBlock { Text = value, FontSize = DesignSystem.TextCaption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                number.SetResourceReference(TextBlock.ForegroundProperty, muted ? "FgSecondary" : "FgPrimary");
                ModernControlsFactory.EnableTabularNumbers(number, true);
                sp.Children.Add(number);
            }
            pill.Child = sp; return pill;
        }

        private void ToggleDetails()
        {
            if (_details != null && _details.IsOpen) { _details.IsOpen = false; return; }
            // 这次点击按下时已经把浮层关掉了：抬起时不要再把它打开（按多久都一样）。
            // 计时判断只是兜底，防止按下事件在某些系统上仍被送到条上。
            if (_dismissedByPressOnStrip) { _dismissedByPressOnStrip = false; return; }
            if ((DateTime.UtcNow - _detailsClosedAt).TotalMilliseconds < 250) return;
            double width = Math.Max(440, Math.Min(780, ActualWidth - 18));
            _detailsHost = new Border { Width = width, Child = BuildDetails(width) };
            _details = ModernControlsFactory.CreateModernPopover(this, _detailsHost);
            _details.Closed += delegate
            {
                Point pointer = Mouse.GetPosition(this);
                _dismissedByPressOnStrip = Mouse.LeftButton == MouseButtonState.Pressed &&
                    pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= ActualWidth && pointer.Y <= ActualHeight;
                _detailsClosedAt = DateTime.UtcNow; SetExpanded(false);
            };
            SetExpanded(true);
            _details.IsOpen = true;
        }

        private void SetExpanded(bool expanded)
        {
            System.Windows.Shapes.Path icon = _toggle.Content as System.Windows.Shapes.Path;
            if (icon != null) icon.RenderTransform = expanded ? (Transform)new RotateTransform(180, icon.Width / 2, icon.Height / 2) : Transform.Identity;
            _toggle.ToolTip = expanded ? "收起明细" : "展开采集与档位明细";
        }

        /// <summary>明细浮层的内容：数据来源 + 当前筛选范围的服务档位。</summary>
        internal FrameworkElement BuildDetails(double width)
        {
            bool dark = _isDark != null && _isDark();
            StackPanel root = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

            int files = 0; foreach (ProviderHealth h in _sources) files += h.FilesFound;
            string meta = _result == null ? "" : Count(files) + " 个日志文件" + (_result.AccountingUpdatedMs > 0 ? " · 更新于 " + ClockTime(_result.AccountingUpdatedMs, true) : "");
            root.Children.Add(SectionHeader("数据来源", meta));
            if (_scanError.Length > 0) root.Children.Add(Notice("本次扫描失败，已保留上次数据：" + _scanError));
            if (_sources.Count == 0)
                root.Children.Add(Note(_result == null ? "正在发现本机工具与日志…" : "尚未发现可读取的用量。在任一 AI 编程工具里完成一次请求后会自动出现；使用了自定义日志目录时，请到「环境检查」核对路径。"));
            else
            {
                UniformGrid grid = new UniformGrid { Columns = width >= 600 ? 2 : 1, Margin = new Thickness(0, 0, -6, 0) };
                foreach (ProviderHealth h in _sources) grid.Children.Add(SourceCard(h, dark));
                root.Children.Add(grid);
            }

            Border rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 12) };
            rule.SetResourceReference(Border.BackgroundProperty, "BorderSubtle");
            root.Children.Add(rule);

            root.Children.Add(SectionHeader("服务档位 · 当前筛选范围", Count(_requests) + " 次请求"));
            if (_groups.Count == 0) root.Children.Add(Note("当前范围暂无用量。"));
            else
            {
                Border track = new Border { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 8) };
                track.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
                Grid segments = new Grid();
                for (int i = 0; i < _groups.Count; i++)
                {
                    UsageTierTotal g = _groups[i];
                    if (g.Requests <= 0) continue;
                    segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(g.Requests, GridUnitType.Star) });
                    Border segment = new Border { Opacity = IsRequestedOnly(g.Label) ? 0.55 : 1.0, Margin = new Thickness(0, 0, 1, 0) };
                    segment.SetResourceReference(Border.BackgroundProperty, TierBrushKey(g.Label));
                    Grid.SetColumn(segment, segments.ColumnDefinitions.Count - 1);
                    segments.Children.Add(segment);
                }
                track.Child = segments;
                root.Children.Add(track);
                foreach (UsageTierTotal g in _groups) root.Children.Add(TierRow(g));
            }

            DockPanel footer = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button scan = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "重新扫描", "secondary");
            scan.Click += delegate { if (_refresh != null) _refresh(); };
            actions.Children.Add(scan);
            Button doctor = ModernControlsFactory.CreateButton("环境检查", "ghost");
            doctor.Margin = new Thickness(6, 0, 0, 0);
            doctor.Click += delegate { if (_details != null) _details.IsOpen = false; if (_diagnose != null) _diagnose(); };
            actions.Children.Add(doctor);
            DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
            TextBlock note = Caption("已请求的档位不代表实际执行档位。金额是 API 参考估值，不是订阅扣款。", "FgMuted");
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 0, 12, 0);
            footer.Children.Add(note);
            root.Children.Add(footer);
            return root;
        }

        private static FrameworkElement SectionHeader(string title, string meta)
        {
            DockPanel head = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
            TextBlock right = Caption(meta ?? "", "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(right, false);
            DockPanel.SetDock(right, Dock.Right); head.Children.Add(right);
            TextBlock left = new TextBlock { Text = title, FontSize = DesignSystem.TextBody, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            left.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            head.Children.Add(left);
            return head;
        }

        private static FrameworkElement Note(string text)
        {
            TextBlock t = Caption(text, "FgMuted"); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 0, 0, 4); return t;
        }

        private static FrameworkElement Notice(string text)
        {
            Border box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8) };
            box.SetResourceReference(Border.BackgroundProperty, "SemanticDangerBg"); box.SetResourceReference(Border.BorderBrushProperty, "SemanticDangerBorder");
            TextBlock t = Caption(text, "SemanticDanger"); t.TextWrapping = TextWrapping.Wrap;
            box.Child = t; return box;
        }

        // 一个工具一张小卡：名称、读取到的请求数，以及文件数 / 最近请求时间或异常原因。
        private static FrameworkElement SourceCard(ProviderHealth h, bool dark)
        {
            Border card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 6, 6) };
            card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Ellipse dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = DesignSystem.BrushHex(DesignSystem.GetProviderColorHex(h.Tool, dark)) };
            grid.Children.Add(dot);

            TextBlock name = new TextBlock { Text = h.Tool, FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            Grid.SetColumn(name, 1); grid.Children.Add(name);

            FrameworkElement right;
            if (h.Status == "ok" || h.Status == "stale")
            {
                TextBlock requests = new TextBlock { Text = Count(h.ParsedRequests) + " 次", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                requests.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                ModernControlsFactory.EnableTabularNumbers(requests, true);
                right = requests;
            }
            else
            {
                TextBlock label; Ellipse badgeDot;
                right = ModernControlsFactory.CreateStatusBadge(h.Status == "error" ? "读取异常" : "暂无用量", h.Status == "error" ? "danger" : "neutral", out label, out badgeDot);
            }
            right.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(right, 2); grid.Children.Add(right);

            string detail;
            if (h.Status == "error") detail = string.IsNullOrWhiteSpace(h.Error) ? "部分记录无法读取，已保留历史数据" : h.Error;
            else
            {
                detail = Count(h.FilesFound) + " 个文件";
                if (h.Status == "no_data") detail += " · 未解析出用量";
                else if (h.LastRequestMs > 0) detail += " · 最近请求 " + ClockTime(h.LastRequestMs, false);
                if (h.Status == "stale") detail += " · 等待刷新";
                if (h.SkippedRecords > 0) detail += " · " + Count(h.SkippedRecords) + " 条未能解析";
            }
            TextBlock caption = Caption(detail, h.Status == "error" ? "SemanticDanger" : "FgMuted");
            caption.TextTrimming = TextTrimming.CharacterEllipsis; caption.Margin = new Thickness(0, 3, 0, 0);
            caption.ToolTip = h.Status == "error" ? detail : (string.IsNullOrEmpty(h.Source) ? null : "来自 " + h.Source + " 的日志");
            ModernControlsFactory.EnableTabularNumbers(caption, false);
            Grid.SetRow(caption, 1); Grid.SetColumn(caption, 1); Grid.SetColumnSpan(caption, 2); grid.Children.Add(caption);

            card.Child = grid; return card;
        }

        // 档位明细的一行：档位 + 金额依据，右侧是请求数、占比、Token 与金额，数字列等宽右对齐。
        private FrameworkElement TierRow(UsageTierTotal g)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            Ellipse dot = new Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Opacity = IsRequestedOnly(g.Label) ? 0.55 : 1.0 };
            dot.SetResourceReference(Shape.FillProperty, TierBrushKey(g.Label));
            row.Children.Add(dot);

            // 档位名定宽，后面的金额依据才能逐行对齐；依据放不下时省略，完整内容在悬停提示里
            Grid names = new Grid { VerticalAlignment = VerticalAlignment.Center };
            names.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 118 });
            names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock label = new TextBlock { Text = g.Label, FontSize = DesignSystem.TextBody, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            names.Children.Add(label);
            string basis = g.BasisLabel + (g.UnpricedRequests > 0 && g.UnpricedRequests < g.Requests ? " · " + Count(g.UnpricedRequests) + " 次未计价" : "") +
                (g.ComparableRequests > 0 && g.PremiumUsd > 0 ? " · 较 Standard 多 " + FormatterUtil.FormatMoney(g.PremiumUsd, _cny, _rate) : "");
            TextBlock basisText = Caption(basis, "FgMuted");
            basisText.Margin = new Thickness(6, 0, 8, 0); basisText.TextTrimming = TextTrimming.CharacterEllipsis; basisText.ToolTip = basis;
            Grid.SetColumn(basisText, 1); names.Children.Add(basisText);
            Grid.SetColumn(names, 1); row.Children.Add(names);

            double share = _requests > 0 ? 100.0 * g.Requests / _requests : 0;
            row.Children.Add(Cell(Count(g.Requests) + " 次", 2, "FgPrimary"));
            row.Children.Add(Cell(share.ToString(share >= 99.95 || share < 0.05 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%", 3, "FgMuted"));
            row.Children.Add(Cell(FormatterUtil.FormatTokens(g.Tokens), 4, "FgSecondary"));
            row.Children.Add(Cell(g.UnpricedRequests == g.Requests && g.Requests > 0 ? "未计价" : FormatterUtil.FormatMoney(g.CostUsd, _cny, _rate), 5, "FgPrimary"));
            return row;
        }

        private static TextBlock Cell(string text, int column, string brushKey)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = DesignSystem.TextBody, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            ModernControlsFactory.EnableTabularNumbers(t, true);
            Grid.SetColumn(t, column); return t;
        }
    }
}
