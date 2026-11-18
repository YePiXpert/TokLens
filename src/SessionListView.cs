using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TokLens
{
    /// <summary>
    /// 一行会话：同一工具、同一会话标识下的请求汇总。只陈述日志里有的事实，不做估算，也不给建议。
    /// </summary>
    public sealed class SessionRow
    {
        public string Tool { get; set; }
        public string SessionId { get; set; }
        public string Workspace { get; set; }
        public string Label { get; set; }
        public string LabelHint { get; set; }
        public string Model { get; set; }
        public string ModelHint { get; set; }
        public int Requests { get; set; }
        public int UnpricedRequests { get; set; }
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public long PeakContext { get; set; }
        public double CacheHitRate { get; set; }
        public double CostUsd { get; set; }
        public string TimeText { get; set; }
        public string RequestsText { get; set; }
        public string PeakContextText { get; set; }
        public string CacheHitText { get; set; }
        public string FormattedCost { get; set; }
        public string CostHint { get; set; }
    }

    public static class SessionList
    {
        // 日志没有会话标识时，同一工具里间隔不超过 25 分钟的请求归为一段。
        public const long GapMs = 25L * 60L * 1000L;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static List<SessionRow> Build(IList<RecentRequestRecord> requests, bool isCny, double usdToCny)
        {
            List<SessionRow> rows = new List<SessionRow>();
            if (requests == null || requests.Count == 0) return rows;

            Dictionary<string, List<RecentRequestRecord>> identified = new Dictionary<string, List<RecentRequestRecord>>(StringComparer.Ordinal);
            Dictionary<string, List<RecentRequestRecord>> unidentified = new Dictionary<string, List<RecentRequestRecord>>(StringComparer.Ordinal);
            for (int i = 0; i < requests.Count; i++)
            {
                RecentRequestRecord r = requests[i];
                if (r == null || (Prompt(r) <= 0 && r.Output <= 0)) continue;
                string tool = ToolName(r);
                string session = (r.SessionId ?? "").Trim();
                Dictionary<string, List<RecentRequestRecord>> target = session.Length > 0 ? identified : unidentified;
                string key = session.Length > 0 ? tool + "\n" + session : tool;
                List<RecentRequestRecord> list;
                if (!target.TryGetValue(key, out list)) { list = new List<RecentRequestRecord>(); target[key] = list; }
                list.Add(r);
            }

            foreach (List<RecentRequestRecord> list in identified.Values) rows.Add(Summarize(list, isCny, usdToCny));
            foreach (List<RecentRequestRecord> list in unidentified.Values)
            {
                list.Sort(delegate(RecentRequestRecord a, RecentRequestRecord b) { return a.TimestampMs.CompareTo(b.TimestampMs); });
                List<RecentRequestRecord> run = new List<RecentRequestRecord>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (run.Count > 0 && list[i].TimestampMs - run[run.Count - 1].TimestampMs > GapMs)
                    {
                        rows.Add(Summarize(run, isCny, usdToCny));
                        run = new List<RecentRequestRecord>();
                    }
                    run.Add(list[i]);
                }
                if (run.Count > 0) rows.Add(Summarize(run, isCny, usdToCny));
            }

            // 最近活动的排在前面
            rows.Sort(delegate(SessionRow a, SessionRow b)
            {
                int c = b.EndMs.CompareTo(a.EndMs);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Tool, b.Tool);
                return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
            });
            return rows;
        }

        private static string ToolName(RecentRequestRecord r)
        {
            return string.IsNullOrWhiteSpace(r.Tool) ? "未知工具" : r.Tool.Trim();
        }

        // 一次请求送给模型的上下文：新输入 + 缓存读 + 缓存写
        private static long Prompt(RecentRequestRecord r)
        {
            return Math.Max(0L, r.FreshInput) + Math.Max(0L, r.CacheRead) + Math.Max(0L, r.CacheWrite);
        }

        private static SessionRow Summarize(List<RecentRequestRecord> list, bool isCny, double usdToCny)
        {
            SessionRow row = new SessionRow();
            Dictionary<string, int> models = new Dictionary<string, int>(StringComparer.Ordinal);
            long prompt = 0L, read = 0L, start = long.MaxValue, end = 0L;
            for (int i = 0; i < list.Count; i++)
            {
                RecentRequestRecord r = list[i];
                long p = Prompt(r);
                prompt += p;
                read += Math.Max(0L, r.CacheRead);
                if (p > row.PeakContext) row.PeakContext = p;
                if (r.TimestampMs > 0L && r.TimestampMs < start) start = r.TimestampMs;
                if (r.TimestampMs > end) end = r.TimestampMs;
                if (string.Equals(r.CostBasis, "unpriced", StringComparison.Ordinal)) row.UnpricedRequests++;
                else if (r.CostUsd > 0.0 && !double.IsNaN(r.CostUsd) && !double.IsInfinity(r.CostUsd)) row.CostUsd += r.CostUsd;
                string model = string.IsNullOrWhiteSpace(r.Model) ? "未知模型" : r.Model.Trim();
                int seen;
                models.TryGetValue(model, out seen);
                models[model] = seen + 1;
                if (string.IsNullOrEmpty(row.Workspace) && !string.IsNullOrWhiteSpace(r.Workspace)) row.Workspace = r.Workspace.Trim();
            }

            row.Tool = ToolName(list[0]);
            row.SessionId = (list[0].SessionId ?? "").Trim();
            row.Requests = list.Count;
            row.StartMs = start == long.MaxValue ? 0L : start;
            row.EndMs = end;
            row.CacheHitRate = prompt > 0L ? (double)read / prompt : double.NaN;

            // 主力模型取请求数最多的；并列时按名称取前者，保证结果稳定
            List<KeyValuePair<string, int>> ranked = new List<KeyValuePair<string, int>>(models);
            ranked.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            row.Model = ranked[0].Key + (ranked.Count > 1 ? " +" + (ranked.Count - 1).ToString(CultureInfo.InvariantCulture) : "");
            if (ranked.Count > 1)
            {
                List<string> lines = new List<string>();
                for (int i = 0; i < ranked.Count; i++)
                    lines.Add(ranked[i].Key + "  " + ranked[i].Value.ToString("N0", CultureInfo.InvariantCulture) + " 次");
                row.ModelHint = string.Join("\n", lines.ToArray());
            }

            row.Label = HudTelemetry.SessionLabel(row.Workspace, row.SessionId);
            row.LabelHint = row.SessionId.Length > 0
                ? (string.IsNullOrEmpty(row.Workspace) ? "" : row.Workspace + "\n") + "会话 " + row.SessionId
                : "日志里没有会话标识。同一工具里间隔不超过 25 分钟的请求归为一段。";
            row.TimeText = FormatSpan(row.StartMs, row.EndMs);
            row.RequestsText = row.Requests.ToString("N0", CultureInfo.InvariantCulture);
            row.PeakContextText = FormatterUtil.FormatTokens(row.PeakContext);
            row.CacheHitText = double.IsNaN(row.CacheHitRate) ? "—" : (row.CacheHitRate * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
            row.FormattedCost = row.UnpricedRequests == row.Requests ? "未计价" : FormatterUtil.FormatMoney(row.CostUsd, isCny, usdToCny);
            if (row.UnpricedRequests > 0 && row.UnpricedRequests < row.Requests)
                row.CostHint = "其中 " + row.UnpricedRequests.ToString("N0", CultureInfo.InvariantCulture) + " 次请求未计价，金额只含已计价部分";
            return row;
        }

        internal static string FormatSpan(long startMs, long endMs)
        {
            if (endMs <= 0L) return "—";
            DateTime end = Epoch.AddMilliseconds(endMs).ToLocalTime();
            string endFull = end.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (startMs <= 0L || startMs >= endMs) return endFull;
            DateTime start = Epoch.AddMilliseconds(startMs).ToLocalTime();
            string startFull = start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (startFull == endFull) return startFull;
            return startFull + " → " + (start.Date == end.Date ? end.ToString("HH:mm", CultureInfo.InvariantCulture) : endFull);
        }
    }

    /// <summary>
    /// 「请求」页的会话视图：把最近的请求按会话汇总成一张表。
    /// </summary>
    public sealed class SessionListView : Border
    {
        private readonly DataGrid _grid;
        private readonly TextBlock _caption;
        private readonly StackPanel _empty;

        public SessionListView()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _caption = new TextBlock
            {
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(2, 2, 0, 10),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _caption.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            root.Children.Add(_caption);

            Border card = ModernControlsFactory.CreateCard(new Thickness(0));
            Grid body = new Grid();
            _grid = ModernDataGridFactory.CreateModernDataGrid();

            // 起止时间、会话、请求数和金额始终保留；窗口变窄时依次收起缓存命中、峰值上下文、主力模型、工具。
            _grid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "起止时间", "TimeText", "EndMs", new DataGridLength(172), false, true, false));
            DataGridColumn toolCol = ModernDataGridFactory.MakeBadgeColumn("AI 工具", "Tool", "Tool", new DataGridLength(124));
            _grid.Columns.Add(toolCol);
            DataGridTextColumn labelCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "会话", "Label", "Label", new DataGridLength(1.2, DataGridLengthUnitType.Star), false, false, true);
            labelCol.MinWidth = 132.0;
            labelCol.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("LabelHint")));
            _grid.Columns.Add(labelCol);
            DataGridTextColumn modelCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "主力模型", "Model", "Model", new DataGridLength(1, DataGridLengthUnitType.Star), false, true, false);
            modelCol.MinWidth = 128.0;
            modelCol.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("ModelHint")));
            _grid.Columns.Add(modelCol);
            _grid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "请求", "RequestsText", "Requests", new DataGridLength(62), true, true, false));
            DataGridColumn peakCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "峰值上下文", "PeakContextText", "PeakContext", new DataGridLength(96), true, true, false);
            _grid.Columns.Add(peakCol);
            DataGridColumn hitCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "缓存命中", "CacheHitText", "CacheHitRate", new DataGridLength(78), true, true, false);
            _grid.Columns.Add(hitCol);
            DataGridTextColumn costCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "金额", "FormattedCost", "CostUsd", new DataGridLength(92), true, true, true);
            costCol.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("CostHint")));
            _grid.Columns.Add(costCol);
            ModernDataGridFactory.EnableResponsiveColumns(_grid, hitCol, peakCol, modelCol, toolCol);
            body.Children.Add(_grid);

            _empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            TextBlock emptyTitle = new TextBlock { Text = "还没有可以汇总的请求", FontSize = DesignSystem.TextBody, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            emptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            TextBlock emptySub = new TextBlock { Text = "本机有带时间戳的请求后，这里按会话列出", FontSize = DesignSystem.TextCaption, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            emptySub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            _empty.Children.Add(emptyTitle);
            _empty.Children.Add(emptySub);
            body.Children.Add(_empty);

            card.Child = body;
            Grid.SetRow(card, 1);
            root.Children.Add(card);
            Child = root;
        }

        internal string CaptionText { get { return _caption.Text; } }

        public void Render(IList<RecentRequestRecord> recent, bool isCny, double usdToCny)
        {
            List<SessionRow> rows = SessionList.Build(recent, isCny, usdToCny);
            int requests = 0;
            for (int i = 0; i < rows.Count; i++) requests += rows[i].Requests;
            _grid.ItemsSource = rows;
            _grid.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            // 这张表只覆盖扫描器保留的最近请求，如实说明范围，免得被当成全部历史。
            _caption.Text = rows.Count == 0 ? "" :
                "最近 " + requests.ToString("N0", CultureInfo.InvariantCulture) + " 次请求，归入 " +
                rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " 个会话；更早的请求不在这张表里。";
        }
    }
}
