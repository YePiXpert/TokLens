using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace TokLens
{
    /// <summary>
    /// 订阅与费用页（可选功能，默认关闭）：本月支出、下次续费、本周期回本比例三项概要，按状态分组的订阅列表。
    /// 回本比例 = 周期内能对应到该服务商的用量按 API 单价折算的价值 ÷ 本周期实付，只作参考。
    /// 登记 / 核对 / 续订都走两步弹窗：先选服务商和套餐，再填实付。
    /// 订阅按账户记录一次，与设备无关；不会根据用量推测用户买了哪个套餐。
    /// </summary>
    public sealed class SubscriptionCostsView : Border
    {
        private const int TabCurrent = 0;
        private const int TabPending = 1;
        private const int TabHistory = 2;
        private const int ExpiringDays = 7;
        private const int RecentlyExpiredDays = 31;

        private SyncConfig _config;
        private Action<SyncConfig> _save;
        private bool _dark = true, _cny = true;
        private double _rate = ExchangeRateService.DefaultRate;
        private DateTime _date = DateTime.Today;
        private List<RecordBucket> _usage = new List<RecordBucket>();
        private int _tab = -1; // -1：按内容自动选择（有当前订阅看当前，否则看待处理）

        private readonly TextBlock _spendValue;
        private readonly TextBlock _spendSub;
        private readonly TextBlock _renewValue;
        private readonly TextBlock _renewSub;
        private readonly TextBlock _paybackValue;
        private readonly TextBlock _paybackSub;
        private readonly Button[] _tabBtns = new Button[3];
        private readonly StackPanel _rows = new StackPanel();

        private sealed class Entry
        {
            public SubscriptionPlanConfig Plan;
            public int Tab;
            public string Status;
            public string Variant;
            public bool Legacy;
            public bool Conflict;
            public bool Active;
            public bool HasDates;
            public DateTime Start;
            public DateTime End;
            public bool HasPayback;
            public double ValueUsd;
            public double PaidUsd;
            public string PaybackNote;
        }

        /// <summary>可切换选中态的卡片按钮（服务商、套餐选择用）。</summary>
        private sealed class SelectButton : Button
        {
            private bool _selected;

            public SelectButton()
            {
                Cursor = Cursors.Hand;
                FocusVisualStyle = null;
                FontFamily = DesignSystem.FontUi;
                FontSize = DesignSystem.TextBody;
                HorizontalContentAlignment = HorizontalAlignment.Stretch;
                SetResourceReference(ForegroundProperty, "FgPrimary");

                ControlTemplate tpl = new ControlTemplate(typeof(Button));
                FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
                bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
                bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
                bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
                FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp);
                tpl.VisualTree = bd;
                Template = tpl;

                MouseEnter += delegate { if (!_selected) SetResourceReference(BackgroundProperty, "BgHover"); };
                MouseLeave += delegate { if (!_selected) SetResourceReference(BackgroundProperty, "BgCard"); };
                Selected = false;
            }

            public bool Selected
            {
                get { return _selected; }
                set
                {
                    _selected = value;
                    SetResourceReference(BackgroundProperty, value ? "BgSelected" : "BgCard");
                    SetResourceReference(BorderBrushProperty, value ? "BorderFocus" : "BorderCard");
                }
            }
        }

        public SubscriptionCostsView()
        {
            SetResourceReference(BackgroundProperty, "BgWindow");

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                Focusable = false
            };
            StackPanel root = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
            scroll.Content = root;
            Child = scroll;

            // 页头：说明 + 唯一的主按钮
            DockPanel head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            Button add = ModernControlsFactory.CreateIconTextButton(UiIcons.Plus, "登记订阅", "primary");
            add.Click += delegate { EditSubscription(null, null); };
            DockPanel.SetDock(add, Dock.Right);
            head.Children.Add(add);
            TextBlock intro = Text("订阅按账户记一次，与设备无关；金额按实际付款填写，不会根据用量推测你买了哪个套餐。", DesignSystem.TextBody, "FgSecondary");
            intro.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(intro);
            root.Children.Add(head);

            // 概要：本月订阅支出 / 下次续费 / 本周期回本
            UniformGrid kpis = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 16) };
            kpis.Children.Add(Kpi("本月订阅支出（已核对）", new Thickness(0, 0, 10, 0), out _spendValue, out _spendSub));
            kpis.Children.Add(Kpi("下次续费", new Thickness(0, 0, 10, 0), out _renewValue, out _renewSub));
            kpis.Children.Add(Kpi("本周期回本（估算）", new Thickness(0), out _paybackValue, out _paybackSub));
            root.Children.Add(kpis);

            // 我的订阅：当前 / 待处理 / 历史
            DockPanel listHead = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            TextBlock listTitle = Text("我的订阅", DesignSystem.TextTitle, "FgPrimary");
            listTitle.FontWeight = FontWeights.SemiBold;
            listTitle.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(listTitle, Dock.Left);
            listHead.Children.Add(listTitle);
            Border tabs = ModernControlsFactory.CreateSegmentedContainer();
            tabs.Margin = new Thickness(12, 0, 0, 0);
            tabs.HorizontalAlignment = HorizontalAlignment.Left;
            Panel tabPanel = (Panel)tabs.Child;
            string[] tabNames = { "当前", "待处理", "历史" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                Button b = ModernControlsFactory.CreateSegmentedItem(tabNames[i]);
                b.Tag = "tab:" + i.ToString(CultureInfo.InvariantCulture);
                b.Click += delegate { _tab = idx; RenderRegistered(); };
                _tabBtns[i] = b;
                tabPanel.Children.Add(b);
            }
            listHead.Children.Add(tabs);
            root.Children.Add(listHead);

            Border table = ModernControlsFactory.CreateCard(new Thickness(0));
            table.Child = _rows;
            root.Children.Add(table);

            TextBlock note = Text("回本比例 = 本周期内对应用量按 API 单价折算的价值 ÷ 本周期实付。Cursor、Copilot 等工具里的请求算给该工具，其余按模型厂商对应；模型来源不能证明付费渠道，用 API Key 按量付费的请求也会算进去，只作参考。", DesignSystem.TextCaption, "FgMuted");
            note.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(note);
        }

        // ------------------------------------------------------------------ 公共接口

        public void Bind(SyncConfig config, Action<SyncConfig> save)
        {
            _config = config;
            _save = save;
            RenderRegistered();
        }

        /// <param name="usage">全部设备的全部历史用量：订阅按账户计，与设备无关。</param>
        public void Render(List<RecordBucket> usage, bool cny, double rate, bool dark, DateTime date)
        {
            _usage = usage ?? new List<RecordBucket>();
            _cny = cny;
            _rate = rate > 0.1 && !double.IsNaN(rate) && !double.IsInfinity(rate) ? rate : ExchangeRateService.DefaultRate;
            _dark = dark;
            _date = date.Date;
            RenderRegistered();
        }

        // ------------------------------------------------------------------ 分类与概要

        private List<SubscriptionPlanConfig> Plans()
        {
            return _config != null && _config.Subscriptions != null ? _config.Subscriptions : new List<SubscriptionPlanConfig>();
        }

        private List<Entry> Classify()
        {
            List<SubscriptionPlanConfig> subs = Plans();
            List<Entry> list = new List<Entry>();
            foreach (SubscriptionPlanConfig s in subs)
            {
                if (s == null || (!s.Enabled && !s.FeeConfirmed)) continue;
                Entry e = new Entry { Plan = s };
                e.Legacy = !s.FeeConfirmed || s.EcoId == "coding_plan";
                e.HasDates = TryDate(s.StartDate, out e.Start) & TryDate(s.EndDate, out e.End) && e.Start <= e.End;
                e.Active = SubscriptionCatalog.IsActive(s, _date);
                foreach (SubscriptionPlanConfig other in subs)
                {
                    if (!object.ReferenceEquals(s, other) && SubscriptionCatalog.IsActive(other, _date) && SubscriptionCatalog.Overlaps(s, other))
                        e.Conflict = true;
                }

                if (e.Legacy)
                {
                    e.Tab = TabPending;
                    e.Variant = "warning";
                    e.Status = s.EcoId == "coding_plan" ? "需选择具体服务商" : "旧记录 · 未核实";
                }
                else if (!s.Enabled)
                {
                    e.Tab = TabHistory; e.Variant = "neutral"; e.Status = "已停用";
                }
                else if (e.Conflict)
                {
                    e.Tab = TabPending; e.Variant = "danger"; e.Status = "周期重叠";
                }
                else if (e.Active)
                {
                    int left = (e.End - _date).Days;
                    e.Tab = TabCurrent;
                    e.Variant = left <= ExpiringDays ? "warning" : "success";
                    e.Status = left <= ExpiringDays ? "即将到期" : "当前";
                }
                else if (e.HasDates && e.Start > _date)
                {
                    e.Tab = TabCurrent; e.Variant = "accent"; e.Status = "未开始";
                }
                else if (e.HasDates && e.End < _date)
                {
                    bool recent = (_date - e.End).Days <= RecentlyExpiredDays && !HasLaterRecord(s, e.End, subs);
                    e.Tab = recent ? TabPending : TabHistory;
                    e.Variant = recent ? "warning" : "neutral";
                    e.Status = recent ? "已到期" : "已结束";
                }
                else
                {
                    e.Tab = TabPending; e.Variant = "warning"; e.Status = "日期无效";
                }
                list.Add(e);
            }
            ComputePayback(list);
            return list;
        }

        // 回本比例：周期内能对应到该服务商的用量按 API 单价折算的价值 ÷ 本周期实付。
        // 同服务商多个账户的周期重叠时用量平分，因为无法区分是哪个账户产生的。
        private void ComputePayback(List<Entry> list)
        {
            foreach (Entry e in list)
            {
                e.HasPayback = false;
                e.PaybackNote = null;
                if (e.Legacy || !e.HasDates || e.Conflict) continue;
                if (e.Start > _date) { e.PaybackNote = "未开始"; continue; }
                e.PaidUsd = e.Plan.Currency == "CNY" ? e.Plan.Amount / _rate : e.Plan.Amount;
                if (!SubscriptionCatalog.CoversUsage(e.Plan.EcoId)) { e.PaybackNote = "无法对应用量"; continue; }
                if (e.PaidUsd <= 0) { e.PaybackNote = "免费套餐"; continue; }

                DateTime last = e.End < _date ? e.End : _date;
                double value = 0;
                foreach (RecordBucket r in _usage)
                {
                    DateTime day;
                    if (r == null || r.CostUsd <= 0 || !SubscriptionCatalog.ValidAmount(r.CostUsd)) continue;
                    if (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) || day < e.Start || day > last) continue;
                    if (SubscriptionCatalog.UsageProvider(r.Tool, r.Model) == e.Plan.EcoId) value += r.CostUsd;
                }

                int sharing = 1;
                foreach (Entry o in list)
                {
                    if (object.ReferenceEquals(o, e) || o.Legacy || !o.HasDates || o.Conflict || o.Plan.EcoId != e.Plan.EcoId) continue;
                    if (o.Start <= last && e.Start <= o.End) sharing++;
                }
                e.ValueUsd = value / sharing;
                e.HasPayback = true;
                if (sharing > 1) e.PaybackNote = "与同服务商另外 " + (sharing - 1) + " 个账户平分用量";
            }
        }

        private static string Percent(double ratio)
        {
            return (ratio * 100).ToString("N0", CultureInfo.InvariantCulture) + "%";
        }

        private static bool HasLaterRecord(SubscriptionPlanConfig s, DateTime end, List<SubscriptionPlanConfig> subs)
        {
            foreach (SubscriptionPlanConfig o in subs)
            {
                DateTime start;
                if (o == null || object.ReferenceEquals(o, s) || !o.FeeConfirmed || o.EcoId != s.EcoId) continue;
                if (!string.Equals((o.AccountLabel ?? "").Trim(), (s.AccountLabel ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (TryDate(o.StartDate, out start) && start > end) return true;
            }
            return false;
        }

        private void RenderSummary(List<Entry> entries)
        {
            double cny = 0, usd = 0;
            int pending = 0;
            Entry next = null;
            foreach (Entry e in entries)
            {
                if (e.Tab == TabPending) pending++;
                if (!e.Active || e.Conflict || e.Legacy) continue;
                double monthly = e.Plan.Amount / Math.Max(1, e.Plan.BillingMonths);
                if (e.Plan.Currency == "CNY") cny += monthly; else usd += monthly;
                if (next == null || e.End < next.End) next = e;
            }

            // 两种币分别累加，最后按当前汇率折成显示币种；原币明细放在副标题里。
            _spendValue.Text = FormatterUtil.FormatMoney(usd + cny / _rate, _cny, _rate);
            List<string> parts = new List<string>();
            if (cny > 0) parts.Add(SubscriptionCatalog.Money(cny, "CNY"));
            if (usd > 0) parts.Add(SubscriptionCatalog.Money(usd, "USD"));
            string breakdown = parts.Count == 2
                ? string.Join(" + ", parts.ToArray()) + "，按 " + _rate.ToString("0.###", CultureInfo.InvariantCulture) + " 折算"
                : (parts.Count == 1 ? "按月均摊" : "还没有已核对的有效订阅");
            _spendSub.Text = pending > 0 ? breakdown + " · " + pending + " 项待处理未计入" : breakdown;

            if (next != null)
            {
                int left = (next.End - _date).Days;
                _renewValue.Text = DisplayName(next.Plan);
                _renewSub.Text = next.End.ToString("MM-dd", CultureInfo.InvariantCulture) + " 到期 · " + (left == 0 ? "今天" : "剩 " + left + " 天");
                _renewSub.SetResourceReference(TextBlock.ForegroundProperty, left <= ExpiringDays ? "SemanticWarning" : "FgMuted");
            }
            else
            {
                _renewValue.Text = "—";
                _renewSub.Text = "登记或核对订阅后显示到期日";
                _renewSub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            }

            double value = 0, paid = 0;
            foreach (Entry e in entries)
            {
                if (!e.Active || e.Conflict || e.Legacy || !e.HasPayback) continue;
                value += e.ValueUsd;
                paid += e.PaidUsd;
            }
            if (paid > 0)
            {
                double ratio = value / paid;
                _paybackValue.Text = Percent(ratio);
                _paybackValue.SetResourceReference(TextBlock.ForegroundProperty, ratio >= 1 ? "SemanticSuccess" : "FgPrimary");
                _paybackSub.Text = "API 等值 " + FormatterUtil.FormatMoney(value, _cny, _rate) + " / 实付 " + FormatterUtil.FormatMoney(paid, _cny, _rate);
            }
            else
            {
                _paybackValue.Text = "—";
                _paybackValue.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                _paybackSub.Text = "有可对应用量的当前订阅后显示";
            }
        }

        // ------------------------------------------------------------------ 订阅列表

        private void RenderRegistered()
        {
            List<Entry> entries = Classify();
            RenderSummary(entries);

            int[] counts = new int[3];
            foreach (Entry e in entries) counts[e.Tab]++;
            string[] names = { "当前", "待处理", "历史" };
            for (int i = 0; i < 3; i++)
                _tabBtns[i].Content = counts[i] > 0 ? names[i] + " " + counts[i] : names[i];
            if (_tab < 0)
                _tab = counts[TabCurrent] > 0 ? TabCurrent : (counts[TabPending] > 0 ? TabPending : TabCurrent);
            ModernControlsFactory.UpdateSegmentedGroup(_tabBtns, _tab);

            List<Entry> shown = new List<Entry>();
            foreach (Entry e in entries) if (e.Tab == _tab) shown.Add(e);
            shown.Sort(delegate(Entry a, Entry b)
            {
                if (a.Legacy != b.Legacy) return a.Legacy ? -1 : 1;
                if (!a.HasDates || !b.HasDates) return a.HasDates == b.HasDates ? 0 : (a.HasDates ? 1 : -1);
                return _tab == TabHistory ? b.End.CompareTo(a.End) : a.End.CompareTo(b.End);
            });

            _rows.Children.Clear();
            if (shown.Count == 0)
            {
                string empty = _tab == TabCurrent
                    ? (counts[TabPending] > 0 ? "没有已核对的有效订阅。先到「待处理」核对旧记录，或点右上角「登记订阅」。" : "还没有登记订阅。点右上角「登记订阅」，选套餐后填写实付金额。")
                    : (_tab == TabPending ? "没有需要处理的记录。" : "暂无历史记录。");
                TextBlock t = Text(empty, DesignSystem.TextBody, "FgSecondary");
                t.Margin = new Thickness(16, 18, 16, 18);
                _rows.Children.Add(t);
                return;
            }

            _rows.Children.Add(HeaderRow());
            foreach (Entry e in shown) _rows.Children.Add(Row(e));
        }

        private static Grid RowGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            return g;
        }

        private static UIElement HeaderRow()
        {
            Grid g = RowGrid();
            g.Margin = new Thickness(16, 10, 16, 6);
            string[] heads = { "服务商 · 套餐", "周期费用", "周期", "回本", "" };
            for (int i = 0; i < heads.Length; i++)
            {
                TextBlock t = Text(heads[i], DesignSystem.TextCaption, "FgMuted");
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }

        private UIElement Row(Entry e)
        {
            SubscriptionPlanConfig s = e.Plan;
            Border row = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 10) };
            row.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            Grid g = RowGrid();
            row.Child = g;

            // 列 1：服务商 · 套餐，状态与账户
            StackPanel main = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Text(DisplayName(s), DesignSystem.TextLabel, "FgPrimary");
            title.FontWeight = FontWeights.SemiBold;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            main.Children.Add(title);
            StackPanel meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            TextBlock badgeLabel;
            System.Windows.Shapes.Ellipse badgeDot;
            meta.Children.Add(ModernControlsFactory.CreateStatusBadge(e.Status, e.Variant, out badgeLabel, out badgeDot));
            string account = (s.AccountLabel ?? "").Trim();
            TextBlock acct = Text(account.Length > 0 ? "账户 " + account : "默认账户", DesignSystem.TextCaption, "FgMuted");
            acct.Margin = new Thickness(8, 0, 0, 0);
            acct.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(acct);
            main.Children.Add(meta);
            if (e.Conflict) main.Children.Add(Text("与同账户另一条记录的周期重叠，未计入合计。", DesignSystem.TextCaption, "SemanticDanger"));
            g.Children.Add(main);

            // 列 2：周期费用（原币）
            StackPanel fee = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            int months = Math.Max(1, s.BillingMonths);
            if (e.Legacy)
            {
                fee.Children.Add(Text(SubscriptionCatalog.Money(s.MonthlyUsd, "USD") + " / 月", DesignSystem.TextLabel, "FgSecondary"));
                fee.Children.Add(Text("旧版填写，未核实", DesignSystem.TextCaption, "FgMuted"));
            }
            else
            {
                fee.Children.Add(Text(SubscriptionCatalog.Money(s.Amount, s.Currency) + " / " + CycleName(months), DesignSystem.TextLabel, "FgPrimary"));
                if (months > 1) fee.Children.Add(Text("月均 " + SubscriptionCatalog.Money(s.Amount / months, s.Currency), DesignSystem.TextCaption, "FgMuted"));
            }
            Grid.SetColumn(fee, 1);
            g.Children.Add(fee);

            // 列 3：周期进度与剩余天数
            StackPanel period = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            if (e.Legacy || !e.HasDates)
            {
                period.Children.Add(Text("缺少起止日期", DesignSystem.TextBody, "FgMuted"));
            }
            else
            {
                double total = Math.Max(1, (e.End - e.Start).TotalDays + 1);
                double done = Math.Max(0, Math.Min(total, (_date - e.Start).TotalDays + 1));
                Grid bar = new Grid { Height = 4, Margin = new Thickness(0, 2, 0, 5) };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, done), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, total - done), GridUnitType.Star) });
                Border track = new Border { CornerRadius = new CornerRadius(2) };
                track.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
                Grid.SetColumnSpan(track, 2);
                bar.Children.Add(track);
                if (done > 0)
                {
                    Border fill = new Border { CornerRadius = new CornerRadius(2) };
                    fill.SetResourceReference(Border.BackgroundProperty, e.Variant == "warning" ? "SemanticWarning" : (e.Active ? "SemanticSuccess" : "FgMuted"));
                    bar.Children.Add(fill);
                }
                period.Children.Add(bar);
                string text;
                if (e.Start > _date) text = e.Start.ToString("MM-dd", CultureInfo.InvariantCulture) + " 开始 · " + (e.Start - _date).Days + " 天后";
                else if (e.End >= _date) text = "剩 " + (e.End - _date).Days + " 天 · " + e.End.ToString("MM-dd", CultureInfo.InvariantCulture) + " 到期";
                else text = e.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " — " + e.End.ToString("MM-dd", CultureInfo.InvariantCulture);
                period.Children.Add(Text(text, DesignSystem.TextCaption, "FgSecondary"));
            }
            Grid.SetColumn(period, 2);
            g.Children.Add(period);

            // 列 4：本周期回本比例（估算）
            StackPanel pay = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            if (e.HasPayback)
            {
                double ratio = e.ValueUsd / e.PaidUsd;
                TextBlock pct = Text(Percent(ratio), DesignSystem.TextLabel, ratio >= 1 ? "SemanticSuccess" : "FgPrimary");
                pct.FontWeight = FontWeights.SemiBold;
                pct.TextWrapping = TextWrapping.NoWrap;
                ModernControlsFactory.EnableTabularNumbers(pct, false);
                pay.Children.Add(pct);
                TextBlock eq = Text("等值 " + FormatterUtil.FormatMoney(e.ValueUsd, _cny, _rate), DesignSystem.TextCaption, "FgMuted");
                eq.TextWrapping = TextWrapping.NoWrap;
                eq.TextTrimming = TextTrimming.CharacterEllipsis;
                pay.Children.Add(eq);
                DateTime last = e.End < _date ? e.End : _date;
                pay.ToolTip = e.Start.ToString("MM-dd", CultureInfo.InvariantCulture) + " 至 " + last.ToString("MM-dd", CultureInfo.InvariantCulture)
                    + " 的对应用量按 API 单价折算 " + FormatterUtil.FormatMoney(e.ValueUsd, _cny, _rate)
                    + "，本周期实付 " + FormatterUtil.FormatMoney(e.PaidUsd, _cny, _rate)
                    + (e.PaybackNote != null ? "；" + e.PaybackNote : "");
            }
            else
            {
                pay.Children.Add(Text("—", DesignSystem.TextLabel, "FgMuted"));
                string why = e.Legacy ? "核对后计算" : (e.PaybackNote ?? (e.Conflict ? "周期重叠" : "缺少起止日期"));
                pay.Children.Add(Text(why, DesignSystem.TextCaption, "FgMuted"));
            }
            Grid.SetColumn(pay, 3);
            g.Children.Add(pay);

            // 列 4：操作
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (e.Legacy)
            {
                actions.Children.Add(SmallButton("核对补全", "secondary", delegate { EditSubscription(s, null); }));
                actions.Children.Add(SmallButton("删除", "ghost", delegate { DeletePlan(s); }));
            }
            else
            {
                if (e.Conflict)
                    actions.Children.Add(SmallButton("修改周期", "secondary", delegate { EditSubscription(s, null); }));
                else if (s.Enabled && e.HasDates && e.End <= _date.AddDays(ExpiringDays) && !HasLaterRecord(s, e.End, Plans()))
                    actions.Children.Add(SmallButton("续订", "secondary", delegate { EditSubscription(null, s); }));
                actions.Children.Add(MoreButton(s));
            }
            Grid.SetColumn(actions, 4);
            g.Children.Add(actions);
            return row;
        }

        private Button MoreButton(SubscriptionPlanConfig s)
        {
            Button more = SmallButton("⋯", "ghost", null);
            // 只放大加粗符号本身：菜单会从按钮继承字体，按钮整体加粗会让菜单项也变粗
            TextBlock dots = new TextBlock { Text = "⋯", FontSize = DesignSystem.TextTitle, FontWeight = FontWeights.Bold };
            dots.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            more.Content = dots;
            more.ToolTip = "编辑、续订、停用或删除";
            ContextMenu menu = new ContextMenu();
            MenuItem edit = new MenuItem { Header = "编辑" };
            edit.Click += delegate { EditSubscription(s, null); };
            menu.Items.Add(edit);
            if (s.Enabled)
            {
                MenuItem renew = new MenuItem { Header = "续订一个周期" };
                renew.Click += delegate { EditSubscription(null, s); };
                menu.Items.Add(renew);
            }
            MenuItem toggle = new MenuItem { Header = s.Enabled ? "停用" : "重新启用" };
            toggle.Click += delegate { TogglePlan(s); };
            menu.Items.Add(toggle);
            menu.Items.Add(new Separator());
            MenuItem del = new MenuItem { Header = "删除…" };
            del.Click += delegate { DeletePlan(s); };
            menu.Items.Add(del);
            more.ContextMenu = menu;
            more.Click += delegate { ModernControlsFactory.OpenMenuUnder(menu, more); };
            return more;
        }

        // ------------------------------------------------------------------ 记录变更（每次写入前备份配置）

        private bool Commit(List<SubscriptionPlanConfig> next, out string error)
        {
            error = null;
            List<SubscriptionPlanConfig> previous = _config.Subscriptions;
            bool previousCustomized = _config.SubscriptionsCustomized;
            try
            {
                string path = SyncService.ConfigFilePath;
                if (File.Exists(path))
                {
                    string backup = Path.Combine(SyncService.AppConfigDir, "backups", "subscriptions");
                    Directory.CreateDirectory(backup);
                    File.Copy(path, Path.Combine(backup, "config-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + ".json"), false);
                }
                _config.Subscriptions = next;
                _config.SubscriptionsCustomized = true;
                if (_save != null) _save(_config); else SyncService.SaveConfig(_config);
                return true;
            }
            catch (Exception ex)
            {
                _config.Subscriptions = previous;
                _config.SubscriptionsCustomized = previousCustomized;
                error = "保存失败，原记录保留：" + ex.Message;
                return false;
            }
        }

        private void DeletePlan(SubscriptionPlanConfig s)
        {
            if (_config == null) return;
            string name = DisplayName(s);
            if (!Confirm("删除订阅记录", "删除「" + name + "」？这条付款记录会从本机和同步数据中移除（删除前会自动备份配置）。", "删除")) return;
            List<SubscriptionPlanConfig> next = new List<SubscriptionPlanConfig>(Plans());
            next.Remove(s);
            string error;
            if (!Commit(next, out error)) MessageBox.Show(error, "TokLens");
            RenderRegistered();
        }

        private void TogglePlan(SubscriptionPlanConfig s)
        {
            if (_config == null) return;
            SubscriptionPlanConfig updated = s.Clone();
            updated.Enabled = !s.Enabled;
            List<SubscriptionPlanConfig> next = new List<SubscriptionPlanConfig>(Plans());
            if (updated.Enabled)
            {
                foreach (SubscriptionPlanConfig other in next)
                {
                    if (!object.ReferenceEquals(other, s) && SubscriptionCatalog.Overlaps(updated, other))
                    {
                        MessageBox.Show("该账户已有与此周期重叠的记录，先修改其中一条的日期再启用。", "TokLens");
                        return;
                    }
                }
            }
            int index = next.IndexOf(s);
            if (index < 0) return;
            next[index] = updated;
            string error;
            if (!Commit(next, out error)) MessageBox.Show(error, "TokLens");
            RenderRegistered();
        }

        private bool Confirm(string title, string message, string confirmLabel)
        {
            Window dlg = NewDialog(title, 420, 200);
            bool result = false;
            DockPanel shell = new DockPanel { Margin = new Thickness(20) };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            buttons.Children.Add(SmallButton("取消", "secondary", delegate { dlg.Close(); }));
            buttons.Children.Add(SmallButton(confirmLabel, "danger", delegate { result = true; dlg.Close(); }));
            shell.Children.Add(buttons);
            shell.Children.Add(Text(message, DesignSystem.TextLabel, "FgPrimary"));
            dlg.Content = shell;
            dlg.ShowDialog();
            return result;
        }

        // ------------------------------------------------------------------ 登记 / 核对 / 续订弹窗

        /// <summary>
        /// existing != null：编辑或核对已有记录（旧记录核对后才计入合计）；
        /// template 为已核对记录：续订一个周期；template 为目录套餐：从该套餐开始登记；
        /// 两者都为空：新登记，从选择服务商开始。
        /// </summary>
        private void EditSubscription(SubscriptionPlanConfig existing, SubscriptionPlanConfig template)
        {
            if (_config == null) return;
            SubscriptionPlanConfig basis = existing ?? template;
            bool renew = existing == null && template != null && template.FeeConfirmed;
            string title = existing == null ? (renew ? "续订" : "登记订阅") : (existing.FeeConfirmed ? "编辑订阅" : "核对订阅");
            Window dialog = NewDialog(title, 640, 620);

            SubscriptionProvider provider = basis != null && !string.IsNullOrEmpty(basis.EcoId) && basis.EcoId != "coding_plan"
                ? SubscriptionCatalog.FindProvider(basis.EcoId) : null;
            SubscriptionOffer offer = basis != null ? FindOffer(basis.OfferId) : null;
            if (offer != null && provider != null && offer.ProviderId != provider.Id) offer = null;

            DockPanel shell = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
            dialog.Content = shell;

            // 顶部：标题与步骤
            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(header, Dock.Top);
            TextBlock heading = Text(title, DesignSystem.TextTitle, "FgPrimary");
            heading.FontWeight = FontWeights.SemiBold;
            header.Children.Add(heading);
            StackPanel steps = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            TextBlock step1Label = Text("① 选择套餐", DesignSystem.TextBody, "FgMuted");
            TextBlock arrow = Text("  ›  ", DesignSystem.TextBody, "FgMuted");
            TextBlock step2Label = Text("② 填写实付", DesignSystem.TextBody, "FgMuted");
            steps.Children.Add(step1Label); steps.Children.Add(arrow); steps.Children.Add(step2Label);
            header.Children.Add(steps);
            shell.Children.Add(header);

            // 底部：错误提示与按钮
            StackPanel footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            TextBlock error = Text("", DesignSystem.TextBody, "SemanticDanger");
            error.Tag = "error";
            footer.Children.Add(error);
            Action fixAction = null;
            Button fix = SmallButton("", "secondary", delegate { if (fixAction != null) fixAction(); });
            fix.Visibility = Visibility.Collapsed;
            fix.HorizontalAlignment = HorizontalAlignment.Left;
            fix.Margin = new Thickness(0, 6, 0, 0);
            footer.Children.Add(fix);
            DockPanel buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
            Button cancel = SmallButton("取消", "secondary", delegate { dialog.Close(); });
            Button back = SmallButton("上一步", "secondary", null); back.Tag = "back";
            Button next = SmallButton("下一步", "primary", null); next.Tag = "next";
            Button save = SmallButton("保存", "primary", null); save.Tag = "save";
            DockPanel.SetDock(cancel, Dock.Left); buttons.Children.Add(cancel);
            DockPanel.SetDock(save, Dock.Right); buttons.Children.Add(save);
            DockPanel.SetDock(next, Dock.Right); buttons.Children.Add(next);
            DockPanel.SetDock(back, Dock.Right); buttons.Children.Add(back);
            footer.Children.Add(buttons);
            shell.Children.Add(footer);

            Grid body = new Grid();
            shell.Children.Add(body);

            // ---------- 步骤 1：服务商与套餐 ----------
            ScrollViewer step1 = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            StackPanel s1 = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            step1.Content = s1;
            body.Children.Add(step1);

            Border groupSeg = ModernControlsFactory.CreateSegmentedContainer();
            groupSeg.HorizontalAlignment = HorizontalAlignment.Left;
            Panel groupPanel = (Panel)groupSeg.Child;
            string[] groups = { "海外", "国产", "工具", "自定义" };
            Button[] groupBtns = new Button[groups.Length];
            s1.Children.Add(groupSeg);
            WrapPanel providerWrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
            s1.Children.Add(providerWrap);
            DockPanel catalogInfo = new DockPanel { Margin = new Thickness(0, 8, 0, 6) };
            Button officialLink = SmallButton("官方价格 ↗", "ghost", null);
            DockPanel.SetDock(officialLink, Dock.Right);
            catalogInfo.Children.Add(officialLink);
            TextBlock catalogText = Text("", DesignSystem.TextCaption, "FgMuted");
            catalogText.VerticalAlignment = VerticalAlignment.Center;
            catalogInfo.Children.Add(catalogText);
            s1.Children.Add(catalogInfo);
            StackPanel offerList = new StackPanel();
            offerList.Tag = "offers";
            s1.Children.Add(offerList);

            // ---------- 步骤 2：实付与周期 ----------
            ScrollViewer step2 = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            StackPanel s2 = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            step2.Content = s2;
            body.Children.Add(step2);

            DockPanel chosen = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            Button change = SmallButton("更改套餐", "ghost", null);
            DockPanel.SetDock(change, Dock.Right);
            chosen.Children.Add(change);
            StackPanel chosenText = new StackPanel();
            TextBlock chosenTitle = Text("", DesignSystem.TextTitle, "FgPrimary");
            chosenTitle.FontWeight = FontWeights.SemiBold;
            TextBlock chosenSub = Text("", DesignSystem.TextBody, "FgMuted");
            chosenText.Children.Add(chosenTitle); chosenText.Children.Add(chosenSub);
            chosen.Children.Add(chosenText);
            s2.Children.Add(chosen);

            StackPanel nameRow = new StackPanel();
            TextBox nameBox = Field(nameRow, "套餐名称", basis != null ? basis.PlanName : "");
            s2.Children.Add(nameRow);

            s2.Children.Add(Label("本周期实付金额（含税、折扣、席位）"));
            DockPanel amountRow = new DockPanel();
            ComboBox currency = Combo();
            currency.Tag = "currency";
            currency.Width = 96;
            currency.Margin = new Thickness(8, 0, 0, 0);
            currency.ItemsSource = new string[] { "CNY", "USD" };
            DockPanel.SetDock(currency, Dock.Right);
            amountRow.Children.Add(currency);
            TextBox amount = Input("");
            amount.Tag = "amount";
            amount.FontFamily = DesignSystem.FontMono;
            amountRow.Children.Add(amount);
            s2.Children.Add(amountRow);
            TextBlock amountHint = Text("", DesignSystem.TextCaption, "FgMuted");
            amountHint.Margin = new Thickness(0, 4, 0, 10);
            s2.Children.Add(amountHint);

            s2.Children.Add(Label("计费周期"));
            StackPanel cycleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            Border cycleSeg = ModernControlsFactory.CreateSegmentedContainer();
            Panel cyclePanel = (Panel)cycleSeg.Child;
            string[] cycleNames = { "月付", "季付", "年付", "其他" };
            int[] cycleMonths = { 1, 3, 12, 0 };
            Button[] cycleBtns = new Button[4];
            cycleRow.Children.Add(cycleSeg);
            TextBox monthsBox = Input("1");
            monthsBox.Tag = "months";
            monthsBox.Width = 64;
            monthsBox.Margin = new Thickness(10, 0, 6, 0);
            cycleRow.Children.Add(monthsBox);
            TextBlock monthsUnit = Text("个月", DesignSystem.TextBody, "FgSecondary");
            monthsUnit.VerticalAlignment = VerticalAlignment.Center;
            cycleRow.Children.Add(monthsUnit);
            s2.Children.Add(cycleRow);

            s2.Children.Add(Label("开始日期"));
            DockPanel startRow = new DockPanel();
            Button today = SmallButton("今天", "secondary", null);
            today.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(today, Dock.Right);
            startRow.Children.Add(today);
            TextBox startBox = Input("");
            startBox.Tag = "start";
            startBox.FontFamily = DesignSystem.FontMono;
            startRow.Children.Add(startBox);
            s2.Children.Add(startRow);

            DockPanel endRow = new DockPanel { Margin = new Thickness(0, 6, 0, 10) };
            Button editEnd = SmallButton("修改", "ghost", null);
            DockPanel.SetDock(editEnd, Dock.Right);
            endRow.Children.Add(editEnd);
            TextBlock endText = Text("", DesignSystem.TextBody, "FgSecondary");
            endText.VerticalAlignment = VerticalAlignment.Center;
            endRow.Children.Add(endText);
            s2.Children.Add(endRow);
            TextBox endBox = Input("");
            endBox.Tag = "end";
            endBox.FontFamily = DesignSystem.FontMono;
            endBox.Visibility = Visibility.Collapsed;
            endBox.Margin = new Thickness(0, -4, 0, 10);
            s2.Children.Add(endBox);

            StackPanel accountRow = new StackPanel();
            TextBox accountBox = Field(accountRow, "账户（可选，同一账户的订阅只登记一次；留空即默认账户）", basis != null ? basis.AccountLabel : "");
            accountBox.Tag = "account";
            WrapPanel accountChips = new WrapPanel { Margin = new Thickness(0, -4, 0, 10) };
            accountRow.Children.Add(accountChips);
            s2.Children.Add(accountRow);

            Expander moreBox = new Expander { Header = "更多：购买地区、备注", Margin = new Thickness(0, 4, 0, 0) };
            moreBox.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            StackPanel moreFields = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            TextBox regionBox = Field(moreFields, "购买地区", basis != null ? basis.Region : "");
            TextBox noteBox = Field(moreFields, "付款说明 / 历史价格备注", basis != null ? basis.Note : "");
            moreBox.Content = moreFields;
            s2.Children.Add(moreBox);

            // ---------- 交互状态 ----------
            bool endEdited = false;
            bool suppress = false;
            Dictionary<string, SelectButton> providerButtons = new Dictionary<string, SelectButton>();

            Action clearError = delegate { error.Text = ""; fix.Visibility = Visibility.Collapsed; };

            Action updateEnd = delegate
            {
                if (endEdited) return;
                DateTime from; int m;
                if (TryDate(startBox.Text, out from) && int.TryParse(monthsBox.Text, out m) && m >= 1 && m <= 120)
                {
                    endBox.Text = from.AddMonths(m).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    endText.Text = "到期 " + endBox.Text + "（含当日）";
                }
                else
                {
                    endText.Text = "填好开始日期和周期后自动计算到期日";
                }
            };

            Action updateAmountHint = delegate
            {
                double paid;
                bool parsed = double.TryParse((amount.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out paid);
                if (offer == null || !offer.Amount.HasValue)
                {
                    amountHint.Text = "目录里没有核实过的价格，按订单实付填写；免费套餐填 0。";
                    amountHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                }
                else if (parsed && Math.Abs(paid - offer.Amount.Value) < 0.005 && (string)currency.SelectedItem == offer.Currency)
                {
                    amountHint.Text = "与目录价一致。若有折扣、税费或席位，请改成实际付款。";
                    amountHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                }
                else
                {
                    amountHint.Text = "与目录价 " + SubscriptionCatalog.Money(offer.Amount.Value, offer.Currency) + " 不同，将按你填写的实付记录。";
                    amountHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                }
            };

            Action<int> selectCycle = delegate(int months)
            {
                int idx = Array.IndexOf(cycleMonths, months);
                if (idx < 0) idx = 3;
                ModernControlsFactory.UpdateSegmentedGroup(cycleBtns, idx);
                bool other = idx == 3;
                monthsBox.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
                monthsUnit.Visibility = monthsBox.Visibility;
                suppress = true;
                monthsBox.Text = months.ToString(CultureInfo.InvariantCulture);
                suppress = false;
                updateEnd();
            };
            for (int i = 0; i < cycleNames.Length; i++)
            {
                int idx = i;
                Button b = ModernControlsFactory.CreateSegmentedItem(cycleNames[i]);
                b.Tag = "cycle:" + cycleNames[i];
                b.Click += delegate
                {
                    if (cycleMonths[idx] > 0) { selectCycle(cycleMonths[idx]); return; }
                    ModernControlsFactory.UpdateSegmentedGroup(cycleBtns, 3);
                    monthsBox.Visibility = Visibility.Visible;
                    monthsUnit.Visibility = Visibility.Visible;
                    monthsBox.Focus();
                    monthsBox.SelectAll();
                };
                cycleBtns[i] = b;
                cyclePanel.Children.Add(b);
            }

            Action fillAccounts = delegate
            {
                accountChips.Children.Clear();
                if (provider == null) return;
                List<string> seen = new List<string>();
                foreach (SubscriptionPlanConfig p in Plans())
                {
                    string a = p == null ? "" : (p.AccountLabel ?? "").Trim();
                    if (p == null || p.EcoId != provider.Id || a.Length == 0 || seen.Contains(a)) continue;
                    seen.Add(a);
                }
                if (seen.Count == 0) return;
                accountChips.Children.Add(Text("已用过：", DesignSystem.TextCaption, "FgMuted"));
                foreach (string a in seen)
                {
                    string label = a;
                    Button chip = SmallButton(label, "ghost", delegate { accountBox.Text = label; });
                    chip.Height = 22;
                    accountChips.Children.Add(chip);
                }
            };

            Action showStep2 = delegate
            {
                step1.Visibility = Visibility.Collapsed;
                step2.Visibility = Visibility.Visible;
                next.Visibility = Visibility.Collapsed;
                save.Visibility = Visibility.Visible;
                back.Visibility = Visibility.Visible;
                step1Label.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                step2Label.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                clearError();

                chosenTitle.Text = provider.Name + " · " + (offer != null && !IsCustomOffer(offer) ? offer.Name : (basis != null && !string.IsNullOrWhiteSpace(basis.PlanName) ? basis.PlanName : "自定义套餐"));
                chosenSub.Text = offer != null && offer.Amount.HasValue
                    ? "目录价 " + SubscriptionCatalog.Money(offer.Amount.Value, offer.Currency) + " / " + CycleName(offer.Months) + " · " + offer.Region + (string.IsNullOrEmpty(offer.Note) ? "" : " · " + offer.Note)
                    : "没有核实过的目录价，按订单填写";
                bool needsName = offer == null || IsCustomOffer(offer);
                nameRow.Visibility = needsName ? Visibility.Visible : Visibility.Collapsed;
                change.Visibility = existing != null && existing.FeeConfirmed ? Visibility.Collapsed : Visibility.Visible;
                fillAccounts();
                updateAmountHint();
            };

            Action showStep1 = null;
            Action<SubscriptionOffer> pickOffer = delegate(SubscriptionOffer o)
            {
                offer = o;
                foreach (UIElement child in offerList.Children)
                {
                    SelectButton sb = child as SelectButton;
                    if (sb != null) sb.Selected = object.ReferenceEquals(sb.DataContext, o);
                }
                // 选择套餐时预填目录价与周期（编辑已核对记录时不覆盖用户的实付）
                if (existing == null || !existing.FeeConfirmed)
                {
                    suppress = true;
                    if (!IsCustomOffer(o)) nameBox.Text = o.Name;
                    currency.SelectedItem = o.Currency;
                    amount.Text = o.Amount.HasValue ? o.Amount.Value.ToString("0.####", CultureInfo.InvariantCulture) : "";
                    regionBox.Text = o.Region == "按账单" ? "" : o.Region;
                    suppress = false;
                    selectCycle(Math.Max(1, o.Months));
                }
                clearError();
            };

            Action renderOffers = delegate
            {
                offerList.Children.Clear();
                if (provider == null)
                {
                    catalogText.Text = "先选择服务商";
                    officialLink.Visibility = Visibility.Collapsed;
                    return;
                }
                bool verified = false;
                foreach (SubscriptionOffer o in SubscriptionCatalog.Offers) if (o.ProviderId == provider.Id && o.Amount.HasValue) verified = true;
                catalogText.Text = provider.Group + " · " + provider.Name + " · " + (verified ? "目录价核验于 " + SubscriptionOffer.VerifiedOn + "，仅作参考" : "暂无已核实报价，按订单登记");
                officialLink.Visibility = string.IsNullOrEmpty(provider.SourceUrl) ? Visibility.Collapsed : Visibility.Visible;
                foreach (SubscriptionOffer o in SubscriptionCatalog.Offers)
                {
                    if (o.ProviderId != provider.Id) continue;
                    SubscriptionOffer captured = o;
                    SelectButton card = new SelectButton { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), DataContext = o };
                    card.Tag = "offer:" + o.Id;
                    Grid cg = new Grid();
                    cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    StackPanel left = new StackPanel();
                    left.Children.Add(Text(o.Name, DesignSystem.TextLabel, "FgPrimary"));
                    if (!string.IsNullOrEmpty(o.Note)) left.Children.Add(Text(o.Note, DesignSystem.TextCaption, "FgMuted"));
                    cg.Children.Add(left);
                    StackPanel right = new StackPanel { Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                    TextBlock price = Text(o.Amount.HasValue ? SubscriptionCatalog.Money(o.Amount.Value, o.Currency) + " / " + CycleName(o.Months) : "按账单填写", DesignSystem.TextLabel, "FgPrimary");
                    price.HorizontalAlignment = HorizontalAlignment.Right;
                    right.Children.Add(price);
                    TextBlock region = Text(o.Region, DesignSystem.TextCaption, "FgMuted");
                    region.HorizontalAlignment = HorizontalAlignment.Right;
                    right.Children.Add(region);
                    Grid.SetColumn(right, 1);
                    cg.Children.Add(right);
                    card.Content = cg;
                    card.Selected = offer != null && offer.Id == o.Id;
                    card.Click += delegate { pickOffer(captured); };
                    card.MouseDoubleClick += delegate { pickOffer(captured); next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); };
                    offerList.Children.Add(card);
                }
            };

            Action<SubscriptionProvider> pickProvider = delegate(SubscriptionProvider p)
            {
                if (provider == null || provider.Id != p.Id) offer = null;
                provider = p;
                foreach (KeyValuePair<string, SelectButton> kv in providerButtons) kv.Value.Selected = kv.Key == p.Id;
                renderOffers();
                clearError();
            };

            Action<int> showGroup = delegate(int gi)
            {
                ModernControlsFactory.UpdateSegmentedGroup(groupBtns, gi);
                providerWrap.Children.Clear();
                providerButtons.Clear();
                foreach (SubscriptionProvider p in SubscriptionCatalog.Providers)
                {
                    if (p.Id == "coding_plan" || p.Group != groups[gi]) continue;
                    SubscriptionProvider captured = p;
                    SelectButton chip = new SelectButton { Content = p.Name, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6) };
                    chip.Tag = "provider:" + p.Id;
                    chip.Selected = provider != null && provider.Id == p.Id;
                    chip.Click += delegate { pickProvider(captured); };
                    providerButtons[p.Id] = chip;
                    providerWrap.Children.Add(chip);
                }
            };
            for (int i = 0; i < groups.Length; i++)
            {
                int gi = i;
                Button b = ModernControlsFactory.CreateSegmentedItem(groups[i]);
                b.Tag = "group:" + groups[i];
                b.Click += delegate { showGroup(gi); };
                groupBtns[i] = b;
                groupPanel.Children.Add(b);
            }

            showStep1 = delegate
            {
                step1.Visibility = Visibility.Visible;
                step2.Visibility = Visibility.Collapsed;
                next.Visibility = Visibility.Visible;
                save.Visibility = Visibility.Collapsed;
                back.Visibility = Visibility.Collapsed;
                step1Label.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                step2Label.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                int gi = provider != null ? Math.Max(0, Array.IndexOf(groups, provider.Group)) : 0;
                showGroup(gi);
                renderOffers();
                clearError();
            };

            officialLink.Click += delegate { if (provider != null) OpenUrl(provider.SourceUrl); };
            change.Click += delegate { showStep1(); };
            back.Click += delegate { showStep1(); };
            next.Click += delegate
            {
                if (provider == null) { error.Text = "请选择服务商。"; return; }
                if (offer == null) { error.Text = "请选择套餐；找不到对应套餐时选「自定义 / 历史 / 团队套餐」。"; return; }
                showStep2();
            };
            today.Click += delegate { startBox.Text = _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); };
            editEnd.Click += delegate
            {
                endEdited = true;
                endBox.Visibility = Visibility.Visible;
                endText.Text = "到期日（含当日）";
                editEnd.Visibility = Visibility.Collapsed;
                endBox.Focus();
            };
            startBox.TextChanged += delegate { if (!suppress) { updateEnd(); clearError(); } };
            monthsBox.TextChanged += delegate { if (!suppress) updateEnd(); };
            amount.TextChanged += delegate { if (!suppress) { updateAmountHint(); clearError(); } };
            currency.SelectionChanged += delegate { if (!suppress) updateAmountHint(); };

            // ---------- 初始值 ----------
            DateTime start = _date;
            if (existing != null) { DateTime parsed; if (TryDate(existing.StartDate, out parsed)) start = parsed; }
            else if (renew) { DateTime end; if (TryDate(template.EndDate, out end)) start = end.AddDays(1); }
            suppress = true;
            startBox.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (basis != null)
            {
                bool legacy = !basis.FeeConfirmed;
                currency.SelectedItem = legacy ? "USD" : (basis.Currency == "CNY" ? "CNY" : "USD");
                double initialAmount = legacy ? basis.MonthlyUsd : basis.Amount;
                amount.Text = initialAmount.ToString("0.####", CultureInfo.InvariantCulture);
            }
            else
            {
                currency.SelectedItem = "USD";
            }
            suppress = false;
            int initialMonths = basis != null ? Math.Max(1, Math.Min(120, basis.BillingMonths)) : 1;
            selectCycle(initialMonths);
            if (existing != null && existing.FeeConfirmed && !string.IsNullOrEmpty(existing.EndDate))
            {
                DateTime from, to;
                if (TryDate(existing.StartDate, out from) && TryDate(existing.EndDate, out to) && from.AddMonths(initialMonths).AddDays(-1) != to)
                {
                    endEdited = true;
                    endBox.Text = existing.EndDate;
                    endBox.Visibility = Visibility.Visible;
                    endText.Text = "到期日（含当日）";
                    editEnd.Visibility = Visibility.Collapsed;
                }
            }
            if (provider != null && offer == null && basis != null)
            {
                // 旧记录或自定义记录：落到该服务商的「自定义」套餐，保留原套餐名
                foreach (SubscriptionOffer o in SubscriptionCatalog.Offers)
                    if (o.ProviderId == provider.Id && IsCustomOffer(o)) offer = o;
            }

            // ---------- 保存 ----------
            save.Click += delegate
            {
                clearError();
                double paid; int cycle; DateTime from, to;
                string planName = nameRow.Visibility == Visibility.Visible ? (nameBox.Text ?? "").Trim() : (offer != null ? offer.Name : "");
                if (provider == null || offer == null) { error.Text = "请先选择服务商和套餐。"; return; }
                if (planName.Length == 0) { error.Text = "请填写套餐名称。"; return; }
                if (!double.TryParse((amount.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out paid) || !SubscriptionCatalog.ValidAmount(paid)) { error.Text = "请填写有效金额，免费套餐填 0。"; return; }
                if (currency.SelectedItem == null) { error.Text = "请选择币种。"; return; }
                if (!int.TryParse(monthsBox.Text, out cycle) || cycle < 1 || cycle > 120) { error.Text = "周期须为 1 至 120 个月。"; return; }
                if (!TryDate(startBox.Text, out from)) { error.Text = "开始日期格式应为 2026-11-14。"; return; }
                if (!TryDate(endBox.Text, out to) || to < from) { error.Text = "到期日应不早于开始日期，格式 2026-12-13。"; return; }

                SubscriptionPlanConfig updated = new SubscriptionPlanConfig
                {
                    Id = existing != null && !string.IsNullOrEmpty(existing.Id) ? existing.Id : Guid.NewGuid().ToString("N"),
                    EcoId = provider.Id,
                    OfferId = offer.Id,
                    PlanName = planName,
                    AccountLabel = (accountBox.Text ?? "").Trim(),
                    Currency = (string)currency.SelectedItem,
                    Amount = paid,
                    BillingMonths = cycle,
                    Region = (regionBox.Text ?? "").Trim(),
                    StartDate = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    EndDate = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Note = (noteBox.Text ?? "").Trim(),
                    Enabled = existing == null || !existing.FeeConfirmed || existing.Enabled,
                    FeeConfirmed = true
                };

                List<SubscriptionPlanConfig> current = Plans();
                foreach (SubscriptionPlanConfig other in current)
                {
                    if (object.ReferenceEquals(other, existing) || !SubscriptionCatalog.Overlaps(updated, other)) continue;
                    DateTime otherEnd;
                    error.Text = "与「" + DisplayName(other) + "」(" + other.StartDate + " — " + other.EndDate + ") 周期重叠，同一账户不能重复计费。";
                    if (TryDate(other.EndDate, out otherEnd))
                    {
                        DateTime suggested = otherEnd.AddDays(1);
                        fix.Content = "改为 " + suggested.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 开始";
                        fix.Visibility = Visibility.Visible;
                        fixAction = delegate
                        {
                            endEdited = false;
                            endBox.Visibility = Visibility.Collapsed;
                            editEnd.Visibility = Visibility.Visible;
                            startBox.Text = suggested.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        };
                    }
                    return;
                }

                List<SubscriptionPlanConfig> nextPlans = new List<SubscriptionPlanConfig>(current);
                if (existing != null)
                {
                    int index = nextPlans.IndexOf(existing);
                    if (index < 0) { error.Text = "记录已被修改，请关闭后重新打开。"; return; }
                    nextPlans[index] = updated;
                }
                else
                {
                    nextPlans.Add(updated);
                }
                string commitError;
                if (!Commit(nextPlans, out commitError)) { error.Text = commitError; return; }
                if (_tab != TabCurrent && SubscriptionCatalog.IsActive(updated, _date)) _tab = TabCurrent;
                dialog.Close();
                RenderRegistered();
            };

            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                ComboBox openCombo = Mouse.Captured as ComboBox;
                if (openCombo != null && openCombo.IsDropDownOpen) { openCombo.IsDropDownOpen = false; e.Handled = true; return; }
                dialog.Close();
                e.Handled = true;
            };

            bool startAtStep2 = provider != null && offer != null && basis != null;
            if (startAtStep2) showStep2(); else showStep1();
            dialog.ShowDialog();
        }

        // ------------------------------------------------------------------ 小工具

        private Window NewDialog(string title, double width, double height)
        {
            Window dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = Math.Min(width, 460),
                MinHeight = Math.Min(height, 200),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ShowInTaskbar = false,
                FontFamily = DesignSystem.FontUi
            };
            DesignSystem.ApplyTheme(dialog, _dark);
            ModernWindowChrome.PrepareDialog(dialog, _dark);
            dialog.SetResourceReference(Window.BackgroundProperty, "BgWindow");
            dialog.SetResourceReference(Window.ForegroundProperty, "FgPrimary");
            return dialog;
        }

        private static Border Kpi(string label, Thickness margin, out TextBlock value, out TextBlock sub)
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 11));
            card.Margin = margin;
            StackPanel sp = new StackPanel();
            sp.Children.Add(Text(label, DesignSystem.TextBody, "FgMuted"));
            value = Text("—", DesignSystem.TextHeading, "FgPrimary");
            value.FontWeight = FontWeights.SemiBold;
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.Margin = new Thickness(0, 4, 0, 2);
            ModernControlsFactory.EnableTabularNumbers(value, false);
            sp.Children.Add(value);
            sub = Text("", DesignSystem.TextCaption, "FgMuted");
            sub.TextWrapping = TextWrapping.NoWrap;
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(sub);
            card.Child = sp;
            return card;
        }

        private static TextBlock Text(string text, double size, string brushKey)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = size, FontFamily = DesignSystem.FontUi, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        private static TextBlock Label(string text)
        {
            TextBlock t = Text(text, DesignSystem.TextBody, "FgSecondary");
            t.Margin = new Thickness(0, 0, 0, 4);
            return t;
        }

        private static TextBox Input(string value)
        {
            return ModernControlsFactory.CreateTextInput(value ?? "", 32);
        }

        private static TextBox Field(StackPanel parent, string label, string value)
        {
            parent.Children.Add(Label(label));
            TextBox box = Input(value);
            box.Margin = new Thickness(0, 0, 0, 10);
            parent.Children.Add(box);
            return box;
        }

        private static Button SmallButton(string label, string variant, Action action)
        {
            Button b = ModernControlsFactory.CreateButton(label, variant);
            b.Margin = new Thickness(6, 0, 0, 0);
            if (action != null) b.Click += delegate { action(); };
            return b;
        }

        private static string ProviderName(SubscriptionPlanConfig s)
        {
            return SubscriptionCatalog.FindProvider(s.EcoId).Name;
        }

        /// <summary>
        /// 「服务商 · 套餐」，但套餐名已含服务商名时不重复：
        /// ChatGPT / Codex + ChatGPT Pro → ChatGPT Pro；腾讯云 · 通用 Token Plan + 通用 Token Plan Lite → 腾讯云 · 通用 Token Plan Lite。
        /// </summary>
        private static string DisplayName(SubscriptionPlanConfig s)
        {
            string provider = ProviderName(s);
            string plan = (s.PlanName ?? "").Trim();
            if (plan.Length == 0) return provider + " · 未命名套餐";
            string[] segments = provider.Split(new string[] { " / ", " · " }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && plan.StartsWith(segments[0], StringComparison.OrdinalIgnoreCase)) return plan;
            for (int i = 1; i < segments.Length; i++)
            {
                if (plan.StartsWith(segments[i], StringComparison.OrdinalIgnoreCase)) return segments[0] + " · " + plan;
            }
            return provider + " · " + plan;
        }

        private static string CycleName(int months)
        {
            return months == 1 ? "月" : months == 3 ? "季" : months == 12 ? "年" : months.ToString(CultureInfo.InvariantCulture) + " 个月";
        }

        private static bool IsCustomOffer(SubscriptionOffer o)
        {
            return o != null && o.Id.EndsWith("-custom", StringComparison.Ordinal);
        }

        private static SubscriptionOffer FindOffer(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (SubscriptionOffer o in SubscriptionCatalog.Offers) if (o.Id == id) return o;
            return null;
        }

        private static bool TryDate(string text, out DateTime value)
        {
            return DateTime.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { MessageBox.Show("无法打开浏览器：" + url, "TokLens"); }
        }

        private static ComboBox Combo()
        {
            const string markup = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ComboBox}'>
              <Setter Property='Foreground' Value='{DynamicResource FgPrimary}'/><Setter Property='Background' Value='{DynamicResource BgInput}'/>
              <Setter Property='BorderBrush' Value='{DynamicResource BorderStrong}'/>
              <Setter Property='ItemContainerStyle'><Setter.Value><Style TargetType='{x:Type ComboBoxItem}'>
                <Setter Property='Padding' Value='10,7'/><Setter Property='Foreground' Value='{DynamicResource FgPrimary}'/>
                <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'>
                  <Border x:Name='Item' Padding='{TemplateBinding Padding}' Background='Transparent'><ContentPresenter/></Border>
                  <ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Item' Property='Background' Value='{DynamicResource BgSelected}'/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter></Style></Setter.Value></Setter>
              <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'><Grid>
                <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' Foreground='{TemplateBinding Foreground}' Content='{TemplateBinding SelectionBoxItem}'>
                  <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'>
                    <Border CornerRadius='6' BorderThickness='1' BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}' Padding='10,4'>
                      <DockPanel><Path DockPanel.Dock='Right' Data='M 0,0 L 4,4 L 8,0' Stroke='{DynamicResource FgMuted}' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' VerticalAlignment='Center' Margin='12,1,0,0'/><ContentPresenter VerticalAlignment='Center'/></DockPanel>
                    </Border>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False'>
                  <Border Background='{DynamicResource BgCard}' BorderBrush='{DynamicResource BorderStrong}' BorderThickness='1' CornerRadius='6' MinWidth='{TemplateBinding ActualWidth}'>
                    <ScrollViewer MaxHeight='320' CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid></ControlTemplate></Setter.Value></Setter>
            </Style>";
            return new ComboBox { Style = (Style)XamlReader.Parse(markup), Height = 32, FontFamily = DesignSystem.FontUi, FontSize = DesignSystem.TextBody };
        }
    }
}
