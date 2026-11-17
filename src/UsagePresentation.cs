using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

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

        public static string CostLabel(UsageRecordMetadata record)
        {
            string basis = record == null ? "" : record.CostBasis;
            if (basis == "reported") return "日志记录金额";
            if (basis == "unpriced") return "未计价";
            if (basis == "estimated_requested_tier") return "按请求档位估值 · 实际档位未核实";
            if (basis == "estimated_unknown_tier") return "标准档位参考估值 · 档位未知";
            if (basis == "legacy_unknown" || string.IsNullOrEmpty(basis)) return "历史金额 · 来源未记录";
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

        public static string Markdown(IEnumerable<RecordBucket> records, bool cny, double rate)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("### 服务档位与计价证据");
            sb.AppendLine();
            sb.AppendLine("| 档位 | 金额来源 | 请求数 | Token | 金额 | 未计价请求 | 档位额外估值 |");
            sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (UsageTierTotal r in Group(records)) sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1} | {2} | {3} | {4} | {5} | {6} |", r.Label, r.BasisLabel, r.Requests, r.Tokens, r.UnpricedRequests == r.Requests && r.Requests > 0 ? "—" : FormatterUtil.FormatMoney(r.CostUsd, cny, rate), r.UnpricedRequests,
                r.ComparableRequests > 0 ? FormatterUtil.FormatMoney(r.PremiumUsd, cny, rate) + "（" + r.ComparableRequests + " 次可比）" : "—"));
            sb.AppendLine();
            sb.AppendLine("已请求档位不证明实际执行档位。未知档位保留标准参考估值；未计价部分不包含在金额中。Token 数不乘 Fast 倍率；以上金额不代表订阅扣款或剩余额度。");
            sb.AppendLine();
            return sb.ToString();
        }
    }

    public sealed class UsageStatusPanel : Border
    {
        private readonly TextBlock _header, _intro, _scope;
        private readonly StackPanel _health, _tiers;
        private readonly Expander _expander;
        private bool _initialState = true;

        public UsageStatusPanel(Action refresh, Action diagnose)
        {
            CornerRadius = new CornerRadius(8); Padding = new Thickness(10, 8, 10, 8);
            BorderThickness = new Thickness(1); Margin = new Thickness(0, 0, 0, 10);
            SetResourceReference(BackgroundProperty, "BgCard"); SetResourceReference(BorderBrushProperty, "BorderCard");
            _header = Text("正在发现本机工具与日志…", false);
            _expander = new Expander { Header = _header };
            StackPanel body = new StackPanel { Margin = new Thickness(4, 8, 4, 2) };
            _intro = Text("无需登录即可读取本机用量；跨设备同步可以稍后配置。", true); body.Children.Add(_intro);
            _health = new StackPanel { Margin = new Thickness(0, 8, 0, 8) }; body.Children.Add(_health);
            _scope = Text("服务档位 · 当前筛选范围", false); body.Children.Add(_scope);
            _tiers = new StackPanel { Margin = new Thickness(0, 4, 0, 6) }; body.Children.Add(_tiers);
            body.Children.Add(Text("API 估值与日志记录金额分开标记；订阅实付在「订阅与费用」登记。官方额度：未连接。", true));
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            Button scan = ModernControlsFactory.CreateButton("重新扫描", "secondary"); scan.Click += delegate { if (refresh != null) refresh(); }; actions.Children.Add(scan);
            Button doctor = ModernControlsFactory.CreateButton("检查日志路径", "ghost"); doctor.Margin = new Thickness(8, 0, 0, 0); doctor.Click += delegate { if (diagnose != null) diagnose(); }; actions.Children.Add(doctor);
            body.Children.Add(actions);
            _expander.Content = new ScrollViewer { Content = body, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Child = _expander;
        }

        private static TextBlock Text(string text, bool muted)
        {
            TextBlock t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = DesignSystem.TextCaption };
            t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "FgMuted" : "FgPrimary"); return t;
        }

        public void Render(ScanResult result, IEnumerable<RecordBucket> scopedRows, bool cny, double rate, string scanError)
        {
            _health.Children.Clear(); _tiers.Children.Clear();
            int found = 0, errors = 0; bool hasUsage = result != null && result.Records != null && result.Records.Count > 0;
            if (result != null && result.ProviderHealth != null) foreach (ProviderHealth h in result.ProviderHealth)
            {
                if (h.Status == "not_installed") continue;
                found++;
                string state = h.Status == "ok" ? "读取正常" : h.Status == "no_data" ? "发现日志，暂无用量" : h.Status == "stale" ? "数据已过期" : h.Status == "error" ? "读取异常" : "未检查";
                if (h.Status == "error" || h.Status == "stale") errors++;
                string detail = h.Tool + " · " + state + " · " + h.FilesFound + " 个文件";
                if (h.SkippedRecords > 0) detail += " · " + h.SkippedRecords + " 条未能解析";
                if (h.LastSuccessMs > 0) detail += " · 成功于 " + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(h.LastSuccessMs).ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(h.Error)) detail += " · " + h.Error;
                TextBlock t = Text(detail, false); t.Margin = new Thickness(0, 0, 0, 4); _health.Children.Add(t);
            }
            if (!string.IsNullOrEmpty(scanError)) { errors++; _health.Children.Add(Text("本次扫描失败 · " + scanError + " · 保留上次数据", false)); }
            if (found == 0 && result != null) _health.Children.Add(Text("尚未发现可读取的用量。先在工具中发起一次请求；使用自定义目录时，请在环境检查中核对路径。", true));
            List<UsageTierTotal> groups = UsagePresentation.Group(scopedRows);
            long fast = 0, unknown = 0, total = 0;
            foreach (UsageTierTotal g in groups) total += g.Requests;
            foreach (UsageTierTotal g in groups)
            {
                if (g.Label.StartsWith("Fast", StringComparison.Ordinal) || g.Label.StartsWith("Ultrafast", StringComparison.Ordinal)) fast += g.Requests;
                if (g.Label == "档位未知") unknown += g.Requests;
                _tiers.Children.Add(Text(string.Format(CultureInfo.InvariantCulture, "{0}  ·  {1:N0} 次 ({5:0.#}%)  ·  {2} tokens  ·  {3}{4}",
                    g.Label, g.Requests, FormatterUtil.FormatTokens(g.Tokens), g.UnpricedRequests == g.Requests && g.Requests > 0 ? "未计价" : FormatterUtil.FormatMoney(g.CostUsd, cny, rate), g.UnpricedRequests > 0 ? "（" + g.UnpricedRequests + " 次未计价）" : "", total > 0 ? 100.0 * g.Requests / total : 0), false));
                _tiers.Children.Add(Text(g.BasisLabel + (g.ComparableRequests > 0 ? " · 相同用量额外估值 " + FormatterUtil.FormatMoney(g.PremiumUsd, cny, rate) + "（" + g.ComparableRequests + " 次可比）" : ""), true));
            }
            if (groups.Count == 0) _tiers.Children.Add(Text("当前范围暂无用量。", true));
            string tierCoverage = total > 0 && unknown == total ? "当前范围 Fast 暂不可判定 · " + unknown.ToString("N0", CultureInfo.InvariantCulture) + " 次无可用档位信息" : string.Format(CultureInfo.InvariantCulture, "已识别 Fast / Ultrafast {0:N0} 次 · 档位未知 {1:N0} 次", fast, unknown);
            _header.Text = result == null ? "正在发现本机工具与日志…" : string.Format(CultureInfo.InvariantCulture,
                "本机采集：{0}  ·  {1} 个工具  |  {2}", errors > 0 ? "需要检查" : found > 0 ? "已读取" : "等待日志", found, tierCoverage);
            _intro.Text = hasUsage ? "本机采集状态与当前筛选用量分别展示。Fast（已请求）表示日志尚未证明实际执行档位。" : "无需登录即可读取本机用量；先完成一次 AI 请求，再查看采集结果。跨设备同步可以稍后配置。";
            _scope.Text = "服务档位 · 当前筛选范围 · " + total.ToString("N0", CultureInfo.InvariantCulture) + " 次请求";
            if (_initialState && result != null) { _expander.IsExpanded = !hasUsage || errors > 0; _initialState = false; }
        }
    }
}
