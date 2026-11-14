using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TokLens
{
    internal sealed class ProjectCostEntry
    {
        public string ProjectName;
        public string FullPath;
        public string PrimaryTool;
        public List<string> Tools = new List<string>();
        public int SessionCount;
        public long Turns;
        public long FreshInput;
        public long CacheRead;
        public long CacheWrite;
        public long Output;
        public double CostUsd;
        public DateTime LastActiveLocal;
        public bool IsUnattributedBucket;

        public long TotalTokens
        {
            get { return FreshInput + CacheRead + CacheWrite + Output; }
        }

        public double CacheHitRatio
        {
            get
            {
                long denom = FreshInput + CacheRead + CacheWrite;
                if (denom <= 0L) return 0.0;
                return (double)CacheRead * 100.0 / (double)denom;
            }
        }
    }

    /// <summary>
    /// 项目与本地工作区成本归因视图（Project &amp; Workspace Cost Attribution）：
    /// 仅解析本地工作区目录名与会话头尾元数据（Claude Code / Codex CLI / Cursor），
    /// 严守零代码、零 Prompt 读取隐私红线，定位高消耗代码仓库。
    /// </summary>
    public sealed class ProjectAttributionView : Border
    {
        private static readonly Regex RxCwdField = new Regex(
            "\"cwd\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxModelField = new Regex(
            "\"model\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxFolderUri = new Regex(
            "\"folder\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxUsageIn = new Regex(
            "\"input_tokens\"\\s*:\\s*([0-9]{1,15})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxUsageOut = new Regex(
            "\"output_tokens\"\\s*:\\s*([0-9]{1,15})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxUsageCr = new Regex(
            "\"(?:cache_read_input_tokens|cached_input_tokens)\"\\s*:\\s*([0-9]{1,15})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxUsageCw = new Regex(
            "\"cache_creation_input_tokens\"\\s*:\\s*([0-9]{1,15})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxMessageId = new Regex(
            "\"message\"\\s*:\\s*\\{\\s*\"id\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxRequestId = new Regex(
            "\"requestId\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Claude Code 的一次回复会按内容块写多行日志，每行都带同一份 usage。
        // 与主扫描器一致：按 message.id + requestId 去重，同一请求保留用量最大的一行。
        private sealed class ClaudeRequestUsage
        {
            public long Fi, Cr, Cw, Out;
            public string Model;
        }

        private string _homeDir;
        private readonly Action<string, string, string> _toastCallback;

        private IList<RecordBucket> _lastRecords = new List<RecordBucket>();
        private PricingEngine _lastPricing;
        private bool _isCny = true;
        private double _usdToCny = ExchangeRateService.DefaultRate;
        private bool _isDark = true;
        private string _searchQuery = "";
        private DateTime _lastScanUtc = DateTime.MinValue;
        private string _lastScannedHome = "";
        private List<ProjectCostEntry> _cachedProjects = new List<ProjectCostEntry>();

        private TextBox _searchBox;
        private TextBlock _kpiRepoCountVal;
        private TextBlock _kpiRepoCountSub;
        private TextBlock _kpiTopRepoVal;
        private TextBlock _kpiTopRepoSub;
        private TextBlock _kpiAttrCostVal;
        private TextBlock _kpiAttrCostSub;
        private TextBlock _kpiBestCacheVal;
        private TextBlock _kpiBestCacheSub;
        private StackPanel _rowsHost;
        private TextBlock _footerNoteText;

        public ProjectAttributionView(string homeDir, Action<string, string, string> toastCallback)
        {
            _homeDir = string.IsNullOrEmpty(homeDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDir;
            _toastCallback = toastCallback;

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Child = BuildLayout();
        }

        public void SetHomeDirectory(string homeDir)
        {
            if (!string.IsNullOrEmpty(homeDir) && !string.Equals(_homeDir, homeDir, StringComparison.OrdinalIgnoreCase))
            {
                _homeDir = homeDir;
                _lastScanUtc = DateTime.MinValue;
            }
        }

        public void RefreshAttribution(
            IList<RecordBucket> allRecords,
            PricingEngine pricing,
            bool isCny,
            double usdToCny,
            bool isDark)
        {
            _lastRecords = allRecords ?? new List<RecordBucket>();
            _lastPricing = pricing;
            _isCny = isCny;
            _usdToCny = usdToCny > 0.01 ? usdToCny : ExchangeRateService.DefaultRate;
            _isDark = isDark;

            bool needRescan = (DateTime.UtcNow - _lastScanUtc).TotalSeconds > 20.0
                || !string.Equals(_lastScannedHome, _homeDir ?? "", StringComparison.OrdinalIgnoreCase);

            if (needRescan)
            {
                _cachedProjects = ScanWorkspaceAttribution(_homeDir, _lastPricing);
                _lastScanUtc = DateTime.UtcNow;
                _lastScannedHome = _homeDir ?? "";
            }

            RenderEntries();
        }

        private UIElement BuildLayout()
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Padding = new Thickness(0, 0, 4, 18);

            StackPanel root = new StackPanel();
            root.Margin = new Thickness(0);

            // 1. 顶部说明与工具栏卡片
            Border headerCard = CreateSurfaceCard(new Thickness(16, 14, 16, 14), new Thickness(0, 0, 0, 12));
            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titleStack = new StackPanel();
            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock titleTb = new TextBlock
            {
                Text = "按工作区统计用量",
                FontFamily = DesignSystem.FontDisplay,
                FontSize = DesignSystem.TextTitle,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleRow.Children.Add(titleTb);

            Border privBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            privBadge.SetResourceReference(Border.BackgroundProperty, "SemanticSuccessBg");
            privBadge.SetResourceReference(Border.BorderBrushProperty, "SemanticSuccessBorder");
            TextBlock privTb = new TextBlock
            {
                Text = "只读取目录路径，不读对话内容",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium
            };
            privTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            privBadge.Child = privTb;
            titleRow.Children.Add(privBadge);
            titleStack.Children.Add(titleRow);

            TextBlock subTb = new TextBlock
            {
                Text = "汇总 Claude Code、Codex CLI 与 Cursor 在本机各工作区的用量，同一请求只计一次。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 4, 12, 0),
                TextWrapping = TextWrapping.Wrap
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            titleStack.Children.Add(subTb);
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            StackPanel toolRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Border searchWrap = new Border
            {
                Width = 210,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 8, 0)
            };
            searchWrap.SetResourceReference(Border.BackgroundProperty, "BgInput");
            searchWrap.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            DockPanel searchDock = new DockPanel { LastChildFill = true };
            UIElement searchIcon = UiIcons.CreateIcon(UiIcons.Search, 12, "FgMuted");
            DockPanel.SetDock(searchIcon, Dock.Left);
            searchDock.Children.Add(searchIcon);

            _searchBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "按工作区名、路径或工具过滤"
            };
            _searchBox.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
            _searchBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                _searchQuery = (_searchBox.Text ?? "").Trim();
                RenderEntries();
            };
            searchDock.Children.Add(_searchBox);
            searchWrap.Child = searchDock;
            toolRow.Children.Add(searchWrap);

            Button rescanBtn = ModernControlsFactory.CreateButton("重新扫描", "secondary");
            rescanBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _lastScanUtc = DateTime.MinValue;
                _cachedProjects = ScanWorkspaceAttribution(_homeDir, _lastPricing);
                _lastScanUtc = DateTime.UtcNow;
                _lastScannedHome = _homeDir ?? "";
                RenderEntries();
                if (_toastCallback != null)
                {
                    _toastCallback("工作区用量已刷新", "已完成本地工作区路径与会话元数据重扫。", "success");
                }
            };
            toolRow.Children.Add(rescanBtn);

            Grid.SetColumn(toolRow, 1);
            headerGrid.Children.Add(toolRow);
            headerCard.Child = headerGrid;
            root.Children.Add(headerCard);

            // 2. 四宫格 KPI 指标条
            UniformGrid kpiGrid = new UniformGrid
            {
                Columns = 4,
                Rows = 1,
                Margin = new Thickness(0, 0, 0, 12)
            };
            kpiGrid.Children.Add(CreateKpiCard("工作区", out _kpiRepoCountVal, out _kpiRepoCountSub, new Thickness(0, 0, 8, 0)));
            kpiGrid.Children.Add(CreateKpiCard("用量最多", out _kpiTopRepoVal, out _kpiTopRepoSub, new Thickness(0, 0, 8, 0)));
            kpiGrid.Children.Add(CreateKpiCard("可归到工作区的费用", out _kpiAttrCostVal, out _kpiAttrCostSub, new Thickness(0, 0, 8, 0)));
            kpiGrid.Children.Add(CreateKpiCard("缓存命中最高", out _kpiBestCacheVal, out _kpiBestCacheSub, new Thickness(0)));
            root.Children.Add(kpiGrid);

            // 3. 仓库归因排行列表卡片
            Border listCard = CreateSurfaceCard(new Thickness(16, 14, 16, 14), new Thickness(0));
            StackPanel listStack = new StackPanel();

            Grid listHeader = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock listTitle = new TextBlock
            {
                Text = "工作区用量排行（按估算费用）",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold
            };
            listTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            Grid.SetColumn(listTitle, 0);
            listHeader.Children.Add(listTitle);

            _footerNoteText = new TextBlock
            {
                Text = "",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            _footerNoteText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(_footerNoteText, 1);
            listHeader.Children.Add(_footerNoteText);
            listStack.Children.Add(listHeader);

            _rowsHost = new StackPanel();
            listStack.Children.Add(_rowsHost);

            listCard.Child = listStack;
            root.Children.Add(listCard);

            sv.Content = root;
            return sv;
        }

        private Border CreateSurfaceCard(Thickness padding, Thickness margin)
        {
            Border bd = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = padding,
                Margin = margin,
                SnapsToDevicePixels = true
            };
            bd.SetResourceReference(Border.BackgroundProperty, "BgCard");
            bd.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            return bd;
        }

        private Border CreateKpiCard(string label, out TextBlock valTb, out TextBlock subTb, Thickness margin)
        {
            Border bd = CreateSurfaceCard(new Thickness(14, 12, 14, 12), margin);
            StackPanel sp = new StackPanel();
            TextBlock lbl = new TextBlock
            {
                Text = label,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(lbl);

            valTb = new TextBlock
            {
                Text = "—",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextHeading,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 6, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            valTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(valTb);

            subTb = new TextBlock
            {
                Text = "等待扫描",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(subTb);

            bd.Child = sp;
            return bd;
        }

        private const string ScratchGroupName = "临时工作区";

        private static bool IsScratchWorkspace(ProjectCostEntry e)
        {
            return e != null && !e.IsUnattributedBucket && e.ProjectName != null && e.FullPath != null &&
                e.ProjectName.StartsWith("scratch-", StringComparison.OrdinalIgnoreCase) &&
                e.FullPath.IndexOf("scratch-workspaces", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsScratchGroup(ProjectCostEntry e)
        {
            return e != null && e.ProjectName != null && e.ProjectName.StartsWith(ScratchGroupName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Claude Desktop 每次新对话都会生成一个 scratch-日期-哈希 临时目录，单独列出会挤满排行榜，
        /// 这里合并成一行「临时工作区（N 个）」；定位目录打开它们共同的 scratch-workspaces 父目录。
        /// </summary>
        private static List<ProjectCostEntry> GroupScratchWorkspaces(List<ProjectCostEntry> source)
        {
            List<ProjectCostEntry> result = new List<ProjectCostEntry>();
            ProjectCostEntry group = null;
            int scratchCount = 0;
            Dictionary<string, int> toolVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectCostEntry e in source)
            {
                if (!IsScratchWorkspace(e)) { result.Add(e); continue; }
                scratchCount++;
                if (group == null)
                {
                    group = new ProjectCostEntry();
                    int cut = e.FullPath.IndexOf("scratch-workspaces", StringComparison.OrdinalIgnoreCase);
                    group.FullPath = e.FullPath.Substring(0, cut + "scratch-workspaces".Length);
                    group.LastActiveLocal = e.LastActiveLocal;
                }
                group.SessionCount += e.SessionCount;
                group.Turns += e.Turns;
                group.FreshInput += e.FreshInput;
                group.CacheRead += e.CacheRead;
                group.CacheWrite += e.CacheWrite;
                group.Output += e.Output;
                group.CostUsd += e.CostUsd;
                if (e.LastActiveLocal > group.LastActiveLocal) group.LastActiveLocal = e.LastActiveLocal;
                foreach (string t in e.Tools) if (!group.Tools.Contains(t)) group.Tools.Add(t);
                string primary = e.PrimaryTool ?? "";
                int votes;
                toolVotes.TryGetValue(primary, out votes);
                toolVotes[primary] = votes + 1;
            }
            if (group != null)
            {
                group.ProjectName = ScratchGroupName + "（" + scratchCount.ToString(CultureInfo.InvariantCulture) + " 个）";
                int best = -1;
                foreach (KeyValuePair<string, int> kv in toolVotes)
                {
                    if (kv.Value > best) { best = kv.Value; group.PrimaryTool = kv.Key; }
                }
                result.Add(group);
                result.Sort(delegate(ProjectCostEntry a, ProjectCostEntry b) { return b.CostUsd.CompareTo(a.CostUsd); });
            }
            return result;
        }

        private void RenderEntries()
        {
            if (_rowsHost == null) return;
            _rowsHost.Children.Clear();

            double globalLedgerUsd = 0.0;
            long globalTokens = 0L;
            if (_lastRecords != null)
            {
                for (int i = 0; i < _lastRecords.Count; i++)
                {
                    RecordBucket r = _lastRecords[i];
                    if (r == null) continue;
                    if (!double.IsNaN(r.CostUsd) && !double.IsInfinity(r.CostUsd) && r.CostUsd > 0)
                        globalLedgerUsd += r.CostUsd;
                    globalTokens += Math.Max(0L, r.TotalTokens);
                }
            }

            List<ProjectCostEntry> working = new List<ProjectCostEntry>();
            double attrUsd = 0.0;
            long attrTokens = 0L;
            ProjectCostEntry bestCacheEntry = null;
            List<ProjectCostEntry> projects = GroupScratchWorkspaces(_cachedProjects);

            for (int i = 0; i < projects.Count; i++)
            {
                ProjectCostEntry e = projects[i];
                attrUsd += e.CostUsd;
                attrTokens += e.TotalTokens;
                if (e.TotalTokens >= 10000L && (bestCacheEntry == null || e.CacheHitRatio > bestCacheEntry.CacheHitRatio))
                {
                    bestCacheEntry = e;
                }
                working.Add(e);
            }

            if (globalLedgerUsd > attrUsd + 0.02 || globalTokens > attrTokens + 5000L)
            {
                ProjectCostEntry unattr = new ProjectCostEntry();
                unattr.ProjectName = "未绑定目录的会话";
                unattr.FullPath = "本机纯聊天窗口，以及日志里不含工作目录字段的工具";
                unattr.PrimaryTool = "Multi-Source";
                unattr.Tools.Add("全局与云端汇总");
                unattr.CostUsd = Math.Max(0.0, globalLedgerUsd - attrUsd);
                unattr.FreshInput = Math.Max(0L, globalTokens - attrTokens);
                unattr.IsUnattributedBucket = true;
                working.Add(unattr);
            }

            int realRepoCount = 0;
            for (int i = 0; i < projects.Count; i++) if (!IsScratchGroup(projects[i])) realRepoCount++;
            _kpiRepoCountVal.Text = realRepoCount.ToString(CultureInfo.InvariantCulture) + " 个";
            _kpiRepoCountSub.Text = "不含临时工作区";

            if (projects.Count > 0)
            {
                // 榜首取真实项目；只有临时工作区时才显示临时工作区
                ProjectCostEntry top = projects[0];
                for (int i = 0; i < projects.Count; i++)
                {
                    if (!IsScratchGroup(projects[i])) { top = projects[i]; break; }
                }
                _kpiTopRepoVal.Text = top.ProjectName;
                _kpiTopRepoSub.Text = FormatMoney(top.CostUsd) + " · " + FormatterUtil.FormatTokens(top.TotalTokens) + " tok";
            }
            else
            {
                _kpiTopRepoVal.Text = "暂无工作区记录";
                _kpiTopRepoSub.Text = "当前主目录下未检出带路径会话";
            }

            _kpiAttrCostVal.Text = FormatMoney(attrUsd);
            double pctOfGlobal = globalLedgerUsd > 0.0001 ? Math.Min(100.0, attrUsd * 100.0 / globalLedgerUsd) : (attrUsd > 0 ? 100.0 : 0.0);
            _kpiAttrCostSub.Text = string.Format(CultureInfo.InvariantCulture, "占本机账单 {0:0.0}% · {1} tok", pctOfGlobal, FormatterUtil.FormatTokens(attrTokens));

            if (bestCacheEntry != null)
            {
                _kpiBestCacheVal.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", bestCacheEntry.CacheHitRatio);
                _kpiBestCacheSub.Text = bestCacheEntry.ProjectName;
            }
            else
            {
                _kpiBestCacheVal.Text = "—";
                _kpiBestCacheSub.Text = "样本量满 10K Token 后评估";
            }

            List<ProjectCostEntry> filtered = new List<ProjectCostEntry>();
            for (int i = 0; i < working.Count; i++)
            {
                ProjectCostEntry item = working[i];
                if (!string.IsNullOrEmpty(_searchQuery))
                {
                    bool hit = (item.ProjectName != null && item.ProjectName.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (item.FullPath != null && item.FullPath.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (item.PrimaryTool != null && item.PrimaryTool.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!hit) continue;
                }
                filtered.Add(item);
            }

            _footerNoteText.Text = string.Format(CultureInfo.InvariantCulture, "展示 {0} / {1} 项", filtered.Count, working.Count);

            if (filtered.Count == 0)
            {
                Border emptyBd = new Border
                {
                    Padding = new Thickness(24, 32, 24, 32),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1)
                };
                emptyBd.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                emptyBd.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
                TextBlock emptyTb = new TextBlock
                {
                    Text = string.IsNullOrEmpty(_searchQuery)
                        ? "当前扫描主目录下暂未发现包含工作区路径元数据的本地会话记录。"
                        : "未找到匹配「" + _searchQuery + "」的工作区，清空过滤词后重试。",
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextBody,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                emptyTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                emptyBd.Child = emptyTb;
                _rowsHost.Children.Add(emptyBd);
                return;
            }

            double maxCost = 0.000001;
            long maxTokens = 1L;
            for (int i = 0; i < filtered.Count; i++)
            {
                if (filtered[i].CostUsd > maxCost) maxCost = filtered[i].CostUsd;
                if (filtered[i].TotalTokens > maxTokens) maxTokens = filtered[i].TotalTokens;
            }

            int maxRender = Math.Min(filtered.Count, 80);
            for (int i = 0; i < maxRender; i++)
            {
                _rowsHost.Children.Add(BuildProjectRow(filtered[i], i + 1, maxCost, maxTokens));
            }
        }

        private UIElement BuildProjectRow(ProjectCostEntry entry, int rank, double maxCost, long maxTokens)
        {
            Border rowBd = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                SnapsToDevicePixels = true
            };
            rowBd.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            rowBd.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 列 0：排名徽章
            Border rankBd = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            rankBd.SetResourceReference(Border.BackgroundProperty, rank <= 3 && !entry.IsUnattributedBucket ? "AccentBg" : "BgElevated");
            TextBlock rankTb = new TextBlock
            {
                Text = entry.IsUnattributedBucket ? "Σ" : ("#" + rank.ToString(CultureInfo.InvariantCulture)),
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            rankTb.SetResourceReference(TextBlock.ForegroundProperty, rank <= 3 && !entry.IsUnattributedBucket ? "AccentPrimary" : "FgMuted");
            rankBd.Child = rankTb;
            Grid.SetColumn(rankBd, 0);
            g.Children.Add(rankBd);

            // 列 1：项目名称 + 完整路径 + 工具胶囊 + 占比进度条
            StackPanel infoSp = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            WrapPanel nameWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock nameTb = new TextBlock
            {
                Text = entry.ProjectName,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            nameWrap.Children.Add(nameTb);

            for (int t = 0; t < entry.Tools.Count; t++)
            {
                string toolName = entry.Tools[t];
                string hex = DesignSystem.GetProviderColorHex(toolName, _isDark);
                Border toolPill = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 5, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = DesignSystem.BrushHex(hex),
                    VerticalAlignment = VerticalAlignment.Center
                };
                toolPill.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                TextBlock toolTb = new TextBlock
                {
                    Text = toolName,
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    Foreground = DesignSystem.BrushHex(hex)
                };
                toolPill.Child = toolTb;
                nameWrap.Children.Add(toolPill);
            }
            infoSp.Children.Add(nameWrap);

            TextBlock pathTb = new TextBlock
            {
                Text = entry.FullPath,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 3, 0, 5),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = entry.FullPath
            };
            pathTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            infoSp.Children.Add(pathTb);

            double ratio = maxCost > 0.0001
                ? Math.Max(0.02, Math.Min(1.0, entry.CostUsd / maxCost))
                : Math.Max(0.02, Math.Min(1.0, (double)entry.TotalTokens / (double)maxTokens));

            Grid barTrack = new Grid { Height = 4 };
            barTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            barTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1.0 - ratio), GridUnitType.Star) });
            Border barBg = new Border { CornerRadius = new CornerRadius(2) };
            barBg.SetResourceReference(Border.BackgroundProperty, "BorderSubtle");
            Grid.SetColumnSpan(barBg, 2);
            barTrack.Children.Add(barBg);

            Border barFill = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = DesignSystem.BrushHex(DesignSystem.GetProviderColorHex(entry.PrimaryTool, _isDark))
            };
            Grid.SetColumn(barFill, 0);
            barTrack.Children.Add(barFill);
            infoSp.Children.Add(barTrack);

            Grid.SetColumn(infoSp, 1);
            g.Children.Add(infoSp);

            // 列 2：Token 规模与缓存命中率
            StackPanel tokSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            TextBlock tokMain = new TextBlock
            {
                Text = FormatterUtil.FormatTokens(entry.TotalTokens) + " tok",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold
            };
            tokMain.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            tokSp.Children.Add(tokMain);

            string subMeta = entry.IsUnattributedBucket
                ? "非单目录直绑流量"
                : string.Format(CultureInfo.InvariantCulture, "{0} 会话 · 缓存 {1:0.0}%", Math.Max(1, entry.SessionCount), entry.CacheHitRatio);
            TextBlock tokSub = new TextBlock
            {
                Text = subMeta,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 2, 0, 0)
            };
            tokSub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tokSp.Children.Add(tokSub);
            Grid.SetColumn(tokSp, 2);
            g.Children.Add(tokSp);

            // 列 3：总花费
            StackPanel costSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
            TextBlock costTb = new TextBlock
            {
                Text = FormatMoney(entry.CostUsd),
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            costTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            costSp.Children.Add(costTb);

            if (!entry.IsUnattributedBucket && entry.LastActiveLocal > DateTime.MinValue)
            {
                TextBlock activeTb = new TextBlock
                {
                    Text = entry.LastActiveLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                activeTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                costSp.Children.Add(activeTb);
            }
            Grid.SetColumn(costSp, 3);
            g.Children.Add(costSp);

            // 列 4：定位与复制按钮
            StackPanel actSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!entry.IsUnattributedBucket)
            {
                string capturedPath = entry.FullPath;
                Button openBtn = ModernControlsFactory.CreateButton("定位目录", "ghost");
                openBtn.Height = 26;
                openBtn.Padding = new Thickness(8, 0, 8, 0);
                openBtn.ToolTip = "在 Windows 资源管理器中打开该工作区目录";
                openBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    bool ok = WindowsShellIntegration.RevealInExplorer(capturedPath);
                    if (!ok && _toastCallback != null)
                    {
                        _toastCallback("目录不存在或已移动", "本地路径未找到：" + capturedPath, "warning");
                    }
                };
                actSp.Children.Add(openBtn);

                Button copyBtn = ModernControlsFactory.CreateButton("复制", "ghost");
                copyBtn.Height = 26;
                copyBtn.Padding = new Thickness(8, 0, 8, 0);
                copyBtn.Margin = new Thickness(4, 0, 0, 0);
                copyBtn.ToolTip = "复制完整工作区路径";
                copyBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    try
                    {
                        Clipboard.SetText(capturedPath ?? "");
                        if (_toastCallback != null)
                            _toastCallback("已复制工作区路径", capturedPath, "success");
                    }
                    catch { }
                };
                actSp.Children.Add(copyBtn);
            }
            Grid.SetColumn(actSp, 4);
            g.Children.Add(actSp);

            rowBd.Child = g;
            return rowBd;
        }

        private string FormatMoney(double usd)
        {
            if (double.IsNaN(usd) || double.IsInfinity(usd) || usd < 0.0) usd = 0.0;
            double v = _isCny ? (usd * _usdToCny) : usd;
            string sym = _isCny ? "¥" : "$";
            return sym + v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static List<ProjectCostEntry> ScanWorkspaceAttribution(string homeDir, PricingEngine pricing)
        {
            Dictionary<string, ProjectCostEntry> map = new Dictionary<string, ProjectCostEntry>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(homeDir) || !Directory.Exists(homeDir))
                return new List<ProjectCostEntry>();

            // 1. Claude Code: ~/.claude/projects/<encoded-folder>/*.jsonl
            try
            {
                string claudeProjectsDir = Path.Combine(Path.Combine(homeDir, ".claude"), "projects");
                if (Directory.Exists(claudeProjectsDir))
                {
                    string[] subDirs = Directory.GetDirectories(claudeProjectsDir);
                    for (int i = 0; i < subDirs.Length; i++)
                    {
                        string projDir = subDirs[i];
                        try
                        {
                            DirectoryInfo di = new DirectoryInfo(projDir);
                            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                            string[] jsonlFiles = Directory.GetFiles(projDir, "*.jsonl", SearchOption.TopDirectoryOnly);
                            if (jsonlFiles.Length == 0) continue;

                            string resolvedCwd = null;
                            long fi = 0L, cr = 0L, cw = 0L, outTok = 0L, turns = 0L;
                            double costUsd = 0.0;
                            DateTime lastWrite = DateTime.MinValue;
                            Dictionary<string, ClaudeRequestUsage> requests = new Dictionary<string, ClaudeRequestUsage>(StringComparer.Ordinal);

                            for (int f = 0; f < jsonlFiles.Length; f++)
                            {
                                string fp = jsonlFiles[f];
                                FileInfo fiInfo = new FileInfo(fp);
                                if (fiInfo.LastWriteTime > lastWrite) lastWrite = fiInfo.LastWriteTime;
                                ScanClaudeFileMetadata(fp, ref resolvedCwd, requests);
                            }

                            foreach (ClaudeRequestUsage u in requests.Values)
                            {
                                fi += u.Fi; cr += u.Cr; cw += u.Cw; outTok += u.Out; turns += 1L;
                                if (pricing != null)
                                {
                                    double actualUsd, baseUsd;
                                    ModelRate rateObj;
                                    pricing.ComputeCost(u.Model, u.Fi, u.Out, u.Cr, u.Cw, 0.0, out actualUsd, out baseUsd, out rateObj);
                                    costUsd += actualUsd;
                                }
                                else
                                {
                                    costUsd += (u.Fi * 3.0 + u.Cr * 0.3 + u.Cw * 3.75 + u.Out * 15.0) / 1000000.0;
                                }
                            }

                            if (string.IsNullOrEmpty(resolvedCwd))
                            {
                                resolvedCwd = DecodeClaudeProjectFolderName(di.Name);
                            }
                            AddOrMergeProject(map, resolvedCwd, "Claude Code", jsonlFiles.Length, turns, fi, cr, cw, outTok, costUsd, lastWrite);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 2. Codex CLI: ~/.codex/sessions/**/*.jsonl (仅读取首行 cwd 与尾部 16KB 汇总计数)
            try
            {
                string codexDir = Path.Combine(Path.Combine(homeDir, ".codex"), "sessions");
                if (Directory.Exists(codexDir))
                {
                    List<string> codexFiles = CollectFilesSafeBounded(codexDir, "*.jsonl", 6, 400);
                    for (int i = 0; i < codexFiles.Count; i++)
                    {
                        string fp = codexFiles[i];
                        try
                        {
                            string cwd;
                            long fi, cr, outTok;
                            double costUsd;
                            DateTime lw = File.GetLastWriteTime(fp);
                            if (TryReadCodexHeadTailMetadata(fp, pricing, out cwd, out fi, out cr, out outTok, out costUsd) && !string.IsNullOrEmpty(cwd))
                            {
                                AddOrMergeProject(map, cwd, "Codex", 1, 1L, fi, cr, 0L, outTok, costUsd, lw);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 3. Cursor IDE: AppData/Roaming/Cursor/User/workspaceStorage/*/workspace.json
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string wsStorage = Path.Combine(Path.Combine(Path.Combine(appData, "Cursor"), "User"), "workspaceStorage");
                if (Directory.Exists(wsStorage))
                {
                    string[] wsDirs = Directory.GetDirectories(wsStorage);
                    int cap = Math.Min(wsDirs.Length, 250);
                    for (int i = 0; i < cap; i++)
                    {
                        try
                        {
                            string wsJson = Path.Combine(wsDirs[i], "workspace.json");
                            if (!File.Exists(wsJson)) continue;
                            string text = ReadBoundedText(wsJson, 4096);
                            Match m = RxFolderUri.Match(text);
                            if (!m.Success) continue;
                            string localFolder = DecodeFileUriToLocalPath(UnescapeJsonString(m.Groups[1].Value));
                            if (string.IsNullOrEmpty(localFolder)) continue;

                            string vscdb = Path.Combine(wsDirs[i], "state.vscdb");
                            DateTime lw = File.Exists(vscdb) ? File.GetLastWriteTime(vscdb) : File.GetLastWriteTime(wsJson);
                            AddOrMergeProject(map, localFolder, "Cursor", 1, 0L, 0L, 0L, 0L, 0L, 0.0, lw);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            List<ProjectCostEntry> result = new List<ProjectCostEntry>(map.Values);
            result.Sort(delegate(ProjectCostEntry a, ProjectCostEntry b)
            {
                int cmpCost = b.CostUsd.CompareTo(a.CostUsd);
                if (cmpCost != 0) return cmpCost;
                int cmpTok = b.TotalTokens.CompareTo(a.TotalTokens);
                if (cmpTok != 0) return cmpTok;
                return b.LastActiveLocal.CompareTo(a.LastActiveLocal);
            });
            return result;
        }

        private static void ScanClaudeFileMetadata(
            string filePath,
            ref string resolvedCwd,
            Dictionary<string, ClaudeRequestUsage> requests)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true, 16384))
            {
                int lineCount = 0;
                string line;
                while ((line = sr.ReadLine()) != null && lineCount < 6000)
                {
                    lineCount++;
                    if (line.Length > 262144) continue;

                    if (string.IsNullOrEmpty(resolvedCwd) && lineCount <= 25 && line.IndexOf("\"cwd\"", StringComparison.Ordinal) >= 0)
                    {
                        Match mc = RxCwdField.Match(line);
                        if (mc.Success)
                        {
                            resolvedCwd = UnescapeJsonString(mc.Groups[1].Value);
                        }
                    }

                    if (line.IndexOf("\"input_tokens\"", StringComparison.Ordinal) < 0)
                        continue;

                    long fi = MatchPositiveLong(RxUsageIn, line);
                    long outTok = MatchPositiveLong(RxUsageOut, line);
                    long cr = MatchPositiveLong(RxUsageCr, line);
                    long cw = MatchPositiveLong(RxUsageCw, line);
                    if (fi + outTok + cr + cw <= 0L) continue;

                    string model = "claude-sonnet-4-6";
                    Match mm = RxModelField.Match(line);
                    if (mm.Success && !string.IsNullOrEmpty(mm.Groups[1].Value))
                    {
                        model = mm.Groups[1].Value;
                    }

                    Match mid = RxMessageId.Match(line);
                    Match rid = RxRequestId.Match(line);
                    string key;
                    if (mid.Success && rid.Success) key = mid.Groups[1].Value + ":" + rid.Groups[1].Value;
                    else if (mid.Success) key = "m:" + mid.Groups[1].Value;
                    else if (rid.Success) key = "r:" + rid.Groups[1].Value;
                    else key = filePath + ":" + lineCount.ToString(CultureInfo.InvariantCulture);

                    // 与主扫描器相同：同一请求保留用量合计最大的一行（流式过程中前几行可能是部分用量）
                    ClaudeRequestUsage prev;
                    if (!requests.TryGetValue(key, out prev) || fi + cr + cw + outTok >= prev.Fi + prev.Cr + prev.Cw + prev.Out)
                        requests[key] = new ClaudeRequestUsage { Fi = fi, Cr = cr, Cw = cw, Out = outTok, Model = model };
                }
            }
        }

        private static bool TryReadCodexHeadTailMetadata(
            string filePath,
            PricingEngine pricing,
            out string cwd,
            out long fi,
            out long cr,
            out long outTok,
            out double costUsd)
        {
            cwd = null;
            fi = 0L;
            cr = 0L;
            outTok = 0L;
            costUsd = 0.0;

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length <= 0L) return false;

                int headBytes = (int)Math.Min(8192L, fs.Length);
                byte[] headBuf = new byte[headBytes];
                int headRead = fs.Read(headBuf, 0, headBytes);
                string headStr = Encoding.UTF8.GetString(headBuf, 0, headRead);

                Match mc = RxCwdField.Match(headStr);
                if (mc.Success)
                {
                    cwd = UnescapeJsonString(mc.Groups[1].Value);
                }
                string model = "gpt-5.2-codex";
                Match mm = RxModelField.Match(headStr);
                if (mm.Success && !string.IsNullOrEmpty(mm.Groups[1].Value))
                {
                    model = mm.Groups[1].Value;
                }

                long tailOffset = Math.Max(0L, fs.Length - 24576L);
                fs.Seek(tailOffset, SeekOrigin.Begin);
                int tailBytes = (int)(fs.Length - tailOffset);
                byte[] tailBuf = new byte[tailBytes];
                int tailRead = fs.Read(tailBuf, 0, tailBytes);
                string tailStr = Encoding.UTF8.GetString(tailBuf, 0, tailRead);

                int idxTotal = tailStr.LastIndexOf("\"total_token_usage\"", StringComparison.Ordinal);
                if (idxTotal >= 0)
                {
                    string slice = tailStr.Substring(idxTotal, Math.Min(600, tailStr.Length - idxTotal));
                    long rawIn = MatchPositiveLong(RxUsageIn, slice);
                    cr = MatchPositiveLong(RxUsageCr, slice);
                    outTok = MatchPositiveLong(RxUsageOut, slice);
                    fi = rawIn > cr ? (rawIn - cr) : rawIn;

                    if (pricing != null)
                    {
                        double actualUsd, baseUsd;
                        ModelRate rateObj;
                        pricing.ComputeCost(model, fi, outTok, cr, 0L, 0.0, out actualUsd, out baseUsd, out rateObj);
                        costUsd = actualUsd;
                    }
                    else
                    {
                        costUsd = (fi * 1.75 + cr * 0.175 + outTok * 14.0) / 1000000.0;
                    }
                }
                return !string.IsNullOrEmpty(cwd);
            }
        }

        private static void AddOrMergeProject(
            Dictionary<string, ProjectCostEntry> map,
            string rawPath,
            string toolName,
            int sessions,
            long turns,
            long fi,
            long cr,
            long cw,
            long outTok,
            double costUsd,
            DateTime lastWrite)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return;
            string cleanPath = NormalizeWorkspacePath(rawPath);
            if (string.IsNullOrEmpty(cleanPath)) return;

            ProjectCostEntry entry;
            if (!map.TryGetValue(cleanPath, out entry))
            {
                entry = new ProjectCostEntry();
                entry.FullPath = cleanPath;
                entry.ProjectName = ExtractProjectDisplayName(cleanPath);
                entry.PrimaryTool = toolName;
                map[cleanPath] = entry;
            }

            bool hasTool = false;
            for (int i = 0; i < entry.Tools.Count; i++)
            {
                if (string.Equals(entry.Tools[i], toolName, StringComparison.OrdinalIgnoreCase))
                {
                    hasTool = true;
                    break;
                }
            }
            if (!hasTool) entry.Tools.Add(toolName);

            entry.SessionCount += Math.Max(0, sessions);
            entry.Turns += Math.Max(0L, turns);
            entry.FreshInput += Math.Max(0L, fi);
            entry.CacheRead += Math.Max(0L, cr);
            entry.CacheWrite += Math.Max(0L, cw);
            entry.Output += Math.Max(0L, outTok);
            if (!double.IsNaN(costUsd) && !double.IsInfinity(costUsd) && costUsd > 0.0)
            {
                entry.CostUsd += costUsd;
            }
            if (lastWrite > entry.LastActiveLocal)
            {
                entry.LastActiveLocal = lastWrite;
            }
        }

        private static string NormalizeWorkspacePath(string path)
        {
            string s = (path ?? "").Trim();
            if (s.Length == 0) return "";
            s = s.Replace('/', '\\');
            while (s.Length > 3 && s.EndsWith("\\", StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - 1);
            }
            return s;
        }

        private static string ExtractProjectDisplayName(string normPath)
        {
            try
            {
                int idx = normPath.LastIndexOf('\\');
                if (idx >= 0 && idx + 1 < normPath.Length)
                    return normPath.Substring(idx + 1);
            }
            catch { }
            return normPath;
        }

        private static string DecodeClaudeProjectFolderName(string encodedName)
        {
            if (string.IsNullOrEmpty(encodedName)) return "";
            if (encodedName.Length >= 3 && char.IsLetter(encodedName[0]) && encodedName[1] == '-' && encodedName[2] == '-')
            {
                return encodedName[0] + ":\\" + encodedName.Substring(3).Replace('-', '\\');
            }
            if (encodedName.StartsWith("-", StringComparison.Ordinal))
            {
                return "/" + encodedName.Substring(1).Replace('-', '/');
            }
            return encodedName;
        }

        private static string DecodeFileUriToLocalPath(string uriStr)
        {
            if (string.IsNullOrEmpty(uriStr)) return "";
            if (!uriStr.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return uriStr;
            try
            {
                Uri u = new Uri(uriStr);
                if (u.IsFile) return u.LocalPath;
            }
            catch { }
            string s = Uri.UnescapeDataString(uriStr.Substring(7));
            if (s.Length >= 3 && s[0] == '/' && char.IsLetter(s[1]) && s[2] == ':')
                s = s.Substring(1);
            return s;
        }

        private static string UnescapeJsonString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return raw.Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\\"", "\"");
        }

        private static long MatchPositiveLong(Regex rx, string text)
        {
            Match m = rx.Match(text);
            if (!m.Success) return 0L;
            long val;
            if (long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out val) && val > 0L)
                return val;
            return 0L;
        }

        private static string ReadBoundedText(string path, int maxBytes)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int n = (int)Math.Min((long)maxBytes, fs.Length);
                if (n <= 0) return "";
                byte[] buf = new byte[n];
                int r = fs.Read(buf, 0, n);
                return Encoding.UTF8.GetString(buf, 0, r);
            }
        }

        private static List<string> CollectFilesSafeBounded(string rootDir, string pattern, int maxDepth, int maxFiles)
        {
            List<string> results = new List<string>();
            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(rootDir, 0));

            while (stack.Count > 0 && results.Count < maxFiles)
            {
                KeyValuePair<string, int> cur = stack.Pop();
                try
                {
                    string[] files = Directory.GetFiles(cur.Key, pattern, SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < files.Length && results.Count < maxFiles; i++)
                    {
                        results.Add(files[i]);
                    }
                    if (cur.Value < maxDepth)
                    {
                        string[] subDirs = Directory.GetDirectories(cur.Key);
                        for (int i = 0; i < subDirs.Length; i++)
                        {
                            try
                            {
                                if ((File.GetAttributes(subDirs[i]) & FileAttributes.ReparsePoint) != 0)
                                    continue;
                                stack.Push(new KeyValuePair<string, int>(subDirs[i], cur.Value + 1));
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return results;
        }
    }
}
