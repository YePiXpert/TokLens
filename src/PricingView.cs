using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace TokLens
{
    /// <summary>
    /// 「设置 → 模型价格」页：把云端价格同步、全部模型查价、官方多档位核验与普通档自定义覆盖统一在一张表与按需抽屉中。
    /// </summary>
    public sealed class PricingView : Border
    {
        public sealed class PricingRow
        {
            public string ModelId { get; set; }
            public string NormId { get; set; }
            public string Provider { get; set; }
            public bool IsActiveUsed { get; set; }
            public Visibility ActiveBadgeVisibility { get; set; }
            public bool HasTierRules { get; set; }
            public Visibility TierBadgeVisibility { get; set; }
            public bool IsCustom { get; set; }
            public bool InCloudCache { get; set; }
            public string SourceKind { get; set; }
            public int SourceRank { get; set; }
            public string SourceBadgeText { get; set; }
            public Brush SourceBgBrush { get; set; }
            public Brush SourceBorderBrush { get; set; }
            public Brush SourceFgBrush { get; set; }
            public Brush ProviderDotBrush { get; set; }

            public double InputPerM { get; set; }
            public double OutputPerM { get; set; }
            public double CacheReadPerM { get; set; }
            public double CacheWritePerM { get; set; }

            public string InputText { get; set; }
            public string OutputText { get; set; }
            public string CacheReadText { get; set; }
            public string CacheWriteText { get; set; }

            public bool HasOfficialRate { get; set; }
            public double OfficialInput { get; set; }
            public double OfficialOutput { get; set; }
            public double OfficialCacheRead { get; set; }
            public double OfficialCacheWrite { get; set; }
        }

        internal sealed class TierRateRow
        {
            public string Tier { get; set; }
            public string TierLabel { get; set; }
            public ServiceTierPrice Price { get; set; }
            public string InputText { get { return Price == null ? "—" : MoneyCompact(Price.input_per_m); } }
            public string OutputText { get { return Price == null ? "—" : MoneyCompact(Price.output_per_m); } }
            public string ReadText { get { return Price == null ? "—" : MoneyCompact(Price.cache_read_per_m); } }
            public string WriteText { get { return Price == null ? "—" : MoneyCompact(Price.cache_write_per_m); } }
            public string Status { get { return Price == null ? "未收录" : "已收录"; } }
        }

        private sealed class RawRateEntry
        {
            public double Input;
            public double Output;
            public double CacheRead;
            public double CacheWrite;

            public RawRateEntry(double inp, double outp, double cr, double cw)
            {
                Input = inp;
                Output = outp;
                CacheRead = cr;
                CacheWrite = cw;
            }
        }

        private static readonly string[] BuiltinSeedModelIds = new string[]
        {
            "claude-opus-4-6", "claude-opus-4-5", "claude-opus-4-1", "claude-opus-4",
            "claude-sonnet-4-6", "claude-sonnet-4-5", "claude-sonnet-4",
            "claude-3-7-sonnet", "claude-3-5-sonnet", "claude-haiku-4-5", "claude-3-5-haiku",
            "claude-3-opus", "claude-3-haiku",
            "gpt-5.4-pro", "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano",
            "gpt-5.3-codex", "gpt-5.2-codex", "gpt-5.2",
            "gpt-5.1-codex-max", "gpt-5.1-codex", "gpt-5.1-codex-mini", "gpt-5.1",
            "gpt-5-codex", "gpt-5", "gpt-5-mini", "gpt-5-nano",
            "codex-mini", "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano",
            "gpt-4o", "gpt-4o-mini", "o3-pro", "o3", "o3-mini", "o4-mini", "o1", "o1-mini",
            "gemini-3.1-pro", "gemini-3.1-flash", "gemini-3.1-flash-lite",
            "gemini-3-pro", "gemini-3-flash", "gemini-2.5-pro", "gemini-2.5-flash",
            "gemini-2.5-flash-lite", "gemini-2.0-flash", "gemini-2.0-flash-lite",
            "glm-5", "glm-4.7", "glm-4.6", "glm-4.5", "glm-4.5-air", "glm-4.5-flash",
            "kimi-k2.5", "kimi-k2-thinking", "kimi-k2-turbo", "kimi-k2", "kimi-for-coding",
            "deepseek-r1", "deepseek-reasoner", "deepseek-v3.2", "deepseek-v3.1", "deepseek-v3", "deepseek-chat",
            "minimax-m2.5", "minimax-m2.5-highspeed", "minimax-m2.1", "minimax-m2", "minimax-m1",
            "qwen3.5-plus", "qwen3.5-coder", "qwen3-coder-plus", "qwen3-coder", "qwen3-coder-flash",
            "qwen3-max", "qwen3-235b-a22b", "qwen3-32b", "qwq-32b", "qwen-max", "qwen-plus",
            "doubao-seed-code", "doubao-1.5-pro",
            "codestral", "devstral", "mistral-large",
            "grok-4", "grok-3", "grok-code-fast"
        };

        private static Dictionary<string, RawRateEntry> _builtinReferenceMap;

        private string _homeDir;
        private bool _isDark;
        private readonly Action _onPricesUpdated;
        private readonly Action<string, string, string> _showToast;

        private readonly List<string> _activeModelList = new List<string>();
        private readonly HashSet<string> _activeRawSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeNormSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, RawRateEntry> _cloudRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, RawRateEntry> _customRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _customNormToRawKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _tierModelProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _cloudSourceSummary = "";
        private string _cloudUpdatedAt = "";

        private List<PricingRow> _allRows = new List<PricingRow>();
        private PricingRow _selectedRow;
        private int _activeFilterIndex = 0; // 0=全部 1=已用 2=未计价 3=自定义 4=含档位
        private bool _suppressSelectionChanged = false;
        private bool _isSyncingCloud = false;
        private bool _isNewCustomMode = false;

        // 顶部栏
        private TextBox _searchBox;
        private TextBlock _searchPlaceholder;
        private Button _clearSearchBtn;
        private Button[] _filterBtns;
        private TextBlock _summaryText;
        private Button _syncCloudBtn;

        // 主表
        private DataGrid _grid;
        private Border _emptyOverlay;
        private TextBlock _emptyHintText;
        private Button _emptyAddBtn;

        // 底部按需抽屉
        private Border _detailDrawer;
        private TextBlock _drawerTitleText;
        private TextBlock _drawerBaselineHint;
        private TextBox _modelIdInput;
        private StackPanel _modelIdInputCol;
        private ColumnDefinition _modelIdColDef;

        // 抽屉 - 官方档位参考区（按需展示）
        private Border _tierSectionBox;
        private DatePicker _tierDatePicker;
        private Button _tierTodayBtn;
        private DataGrid _tierGrid;
        private TextBlock _tierRangeText;
        private TextBlock _tierLongRatesText;
        private TextBlock _tierCacheText;
        private TextBlock _tierMetaText;
        private Button _tierOpenSourceBtn;
        private string _tierSourceUrl;

        // 抽屉 - 普通档自定义覆盖区
        private TextBox _inputRateBox;
        private TextBox _outputRateBox;
        private TextBox _cacheReadRateBox;
        private TextBox _cacheWriteRateBox;
        private Button _deleteRuleBtn;
        private Button _saveRuleBtn;

        // 底部精简状态提示
        private Shapes.Ellipse _statusDot;
        private TextBlock _statusText;

        public PricingView(
            string homeDir,
            bool isDark,
            IEnumerable<string> activeModels,
            Action onPricesUpdated,
            Action<string, string, string> showToast)
        {
            _homeDir = string.IsNullOrWhiteSpace(homeDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDir;
            _isDark = isDark;
            _onPricesUpdated = onPricesUpdated;
            _showToast = showToast;

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            SetResourceReference(Border.BackgroundProperty, "BgWindow");

            SetActiveModels(activeModels);
            Child = BuildLayout();
            ReloadCatalogAndBind(null, false);
        }

        public void SetTheme(bool isDark)
        {
            if (_isDark == isDark) return;
            _isDark = isDark;
            string keep = _selectedRow != null ? _selectedRow.ModelId : null;
            ReloadCatalogAndBind(keep, false);
        }

        public void Refresh(string homeDir, IEnumerable<string> activeModels, string focusModelId)
        {
            if (!string.IsNullOrWhiteSpace(homeDir))
                _homeDir = homeDir;
            SetActiveModels(activeModels);

            string target = !string.IsNullOrWhiteSpace(focusModelId) ? focusModelId.Trim() : null;
            if (!string.IsNullOrEmpty(target))
            {
                _activeFilterIndex = 0;
                ModernControlsFactory.UpdateSegmentedGroup(_filterBtns, 0);
                if (_searchBox != null) _searchBox.Text = "";
            }
            ReloadCatalogAndBind(target, !string.IsNullOrEmpty(target));
            if (!string.IsNullOrEmpty(target))
            {
                PricingRow found = FindMatchingCatalogRow(target);
                if (found != null)
                {
                    _grid.SelectedItem = found;
                    try { _grid.ScrollIntoView(found); } catch { }
                    OpenDrawerForRow(found);
                }
                else
                {
                    OpenNewCustomDrawer(target);
                }
            }
        }

        public void FocusSearch()
        {
            if (_searchBox != null)
            {
                _searchBox.Focus();
                _searchBox.SelectAll();
            }
        }

        private void SetActiveModels(IEnumerable<string> activeModels)
        {
            _activeModelList.Clear();
            _activeRawSet.Clear();
            _activeNormSet.Clear();
            if (activeModels == null) return;
            foreach (string m in activeModels)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                string trimmed = m.Trim();
                if (_activeRawSet.Add(trimmed))
                    _activeModelList.Add(trimmed);
                string norm = PricingEngine.NormalizeModelId(trimmed, "");
                if (!string.IsNullOrEmpty(norm))
                    _activeNormSet.Add(norm);
            }
        }

        private string CustomHomeOrNull
        {
            get { return IsCustomHome ? _homeDir : null; }
        }

        private bool IsCustomHome
        {
            get
            {
                string realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return !string.IsNullOrWhiteSpace(_homeDir) &&
                       !string.Equals(_homeDir.TrimEnd('\\', '/'), (realHome ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
        }

        private string PrimaryCustomPath
        {
            get
            {
                return IsCustomHome
                    ? Path.Combine(_homeDir, ".toklens", "custom_prices.json")
                    : Path.Combine(SyncService.AppConfigDir, "custom_prices.json");
            }
        }

        private UIElement BuildLayout()
        {
            Grid root = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: 统一工具栏
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 1: 主表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 2: 按需抽屉
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 3: 精简状态行

            UIElement toolbar = BuildToolbar();
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            UIElement tableCard = BuildCatalogTableCard();
            Grid.SetRow(tableCard, 1);
            root.Children.Add(tableCard);

            _detailDrawer = BuildDetailDrawer();
            Grid.SetRow(_detailDrawer, 2);
            root.Children.Add(_detailDrawer);

            UIElement statusBar = BuildStatusRow();
            Grid.SetRow(statusBar, 3);
            root.Children.Add(statusBar);

            return root;
        }

        private UIElement BuildToolbar()
        {
            DockPanel bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            // 右侧：摘要 + 同步云端价格 + 新增自定义
            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(right, Dock.Right);

            _summaryText = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            ModernControlsFactory.EnableTabularNumbers(_summaryText, false);
            _summaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            right.Children.Add(_summaryText);

            _syncCloudBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "同步云端价格", "secondary");
            _syncCloudBtn.Height = 30;
            _syncCloudBtn.Margin = new Thickness(0, 0, 6, 0);
            _syncCloudBtn.ToolTip = "从 models.dev + LiteLLM + OpenRouter 同步普通基准价（Fast / Ultrafast 档位价随内置核验快照更新）";
            _syncCloudBtn.Click += OnSyncCloudPricesClick;
            right.Children.Add(_syncCloudBtn);

            Button addBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Plus, "自定义", "primary");
            addBtn.Height = 30;
            addBtn.ToolTip = "为任意模型或中转/本地部署模型添加普通档自定义单价";
            addBtn.Click += delegate(object s, RoutedEventArgs e) { OpenNewCustomDrawer(_searchBox != null ? _searchBox.Text.Trim() : ""); };
            right.Children.Add(addBtn);
            bar.Children.Add(right);

            // 左侧：搜索框 + 分段筛选
            WrapPanel left = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Grid searchHost = new Grid { Width = 210, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
            _searchBox = ModernControlsFactory.CreateTextInput("", 30);
            _searchBox.Padding = new Thickness(28, 0, 24, 0);
            _searchBox.FontSize = DesignSystem.TextBody;
            _searchBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                bool empty = string.IsNullOrEmpty(_searchBox.Text);
                _searchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                _clearSearchBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                ApplyFiltersAndRefreshGrid(_selectedRow != null ? _selectedRow.ModelId : null);
            };
            searchHost.Children.Add(_searchBox);

            Shapes.Path sIcon = UiIcons.CreateIcon(UiIcons.Search, 13, "FgMuted");
            sIcon.HorizontalAlignment = HorizontalAlignment.Left;
            sIcon.VerticalAlignment = VerticalAlignment.Center;
            sIcon.Margin = new Thickness(9, 0, 0, 0);
            sIcon.IsHitTestVisible = false;
            searchHost.Children.Add(sIcon);

            _searchPlaceholder = new TextBlock
            {
                Text = "搜索模型 (Ctrl+F)",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(28, 0, 24, 0),
                IsHitTestVisible = false
            };
            _searchPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            searchHost.Children.Add(_searchPlaceholder);

            _clearSearchBtn = ModernControlsFactory.CreateIconGhostButton("M 3,3 L 13,13 M 13,3 L 3,13", "清除搜索");
            _clearSearchBtn.Width = 22;
            _clearSearchBtn.Height = 22;
            _clearSearchBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _clearSearchBtn.VerticalAlignment = VerticalAlignment.Center;
            _clearSearchBtn.Margin = new Thickness(0, 0, 4, 0);
            _clearSearchBtn.Visibility = Visibility.Collapsed;
            _clearSearchBtn.Click += delegate(object s, RoutedEventArgs e) { _searchBox.Text = ""; _searchBox.Focus(); };
            searchHost.Children.Add(_clearSearchBtn);
            left.Children.Add(searchHost);

            Border seg = ModernControlsFactory.CreateSegmentedContainer();
            Panel segSp = (Panel)seg.Child;
            string[] labels = new string[] { "全部", "已用", "未计价", "自定义", "含档位" };
            _filterBtns = new Button[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                Button b = ModernControlsFactory.CreateSegmentedItem(labels[i]);
                b.Click += delegate(object s, RoutedEventArgs e)
                {
                    _activeFilterIndex = idx;
                    ModernControlsFactory.UpdateSegmentedGroup(_filterBtns, idx);
                    ApplyFiltersAndRefreshGrid(_selectedRow != null ? _selectedRow.ModelId : null);
                };
                _filterBtns[i] = b;
                segSp.Children.Add(b);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_filterBtns, 0);
            left.Children.Add(seg);
            bar.Children.Add(left);

            return bar;
        }

        private UIElement BuildCatalogTableCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(0));
            card.ClipToBounds = true;

            Grid host = new Grid();
            _grid = ModernDataGridFactory.CreateModernDataGrid();
            _grid.Tag = "pricing-catalog";
            _grid.RowHeight = 34;
            _grid.SelectionChanged += OnGridSelectionChanged;

            _grid.Columns.Add(BuildModelIdColumn());
            _grid.Columns.Add(BuildSourceBadgeColumn());
            DataGridColumn inCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "输入 ($/1M)", "InputText", "InputPerM", new DataGridLength(96), true, true, true);
            DataGridColumn outCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "输出 ($/1M)", "OutputText", "OutputPerM", new DataGridLength(96), true, true, true);
            DataGridColumn crCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "缓存读 ($/1M)", "CacheReadText", "CacheReadPerM", new DataGridLength(102), true, true, false);
            DataGridColumn cwCol = ModernDataGridFactory.MakeTextOrMonoColumn(_grid, "缓存写 ($/1M)", "CacheWriteText", "CacheWritePerM", new DataGridLength(102), true, true, false);
            _grid.Columns.Add(inCol);
            _grid.Columns.Add(outCol);
            _grid.Columns.Add(crCol);
            _grid.Columns.Add(cwCol);
            ModernDataGridFactory.EnableResponsiveColumns(_grid, cwCol, crCol);
            host.Children.Add(_grid);

            _emptyOverlay = new Border
            {
                Visibility = Visibility.Collapsed,
                Background = Brushes.Transparent,
                Padding = new Thickness(24)
            };
            StackPanel empSp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _emptyHintText = new TextBlock
            {
                Text = "没有匹配的模型",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _emptyHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            empSp.Children.Add(_emptyHintText);

            _emptyAddBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Plus, "为此模型添加自定义单价", "secondary");
            _emptyAddBtn.Height = 28;
            _emptyAddBtn.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyAddBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenNewCustomDrawer(_searchBox != null ? _searchBox.Text.Trim() : "");
            };
            empSp.Children.Add(_emptyAddBtn);
            _emptyOverlay.Child = empSp;
            host.Children.Add(_emptyOverlay);

            card.Child = host;
            return card;
        }

        private DataGridTemplateColumn BuildModelIdColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn
            {
                Header = "模型 ID（点击查看档位或改价）",
                SortMemberPath = "ModelId",
                Width = new DataGridLength(1.6, DataGridLengthUnitType.Star),
                MinWidth = 220
            };

            DataTemplate tpl = new DataTemplate();
            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            // 右侧小徽章组：「已用」+「含档位」
            FrameworkElementFactory badges = new FrameworkElementFactory(typeof(StackPanel));
            badges.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            badges.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            badges.SetValue(DockPanel.DockProperty, Dock.Right);

            FrameworkElementFactory tierTag = new FrameworkElementFactory(typeof(Border));
            tierTag.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            tierTag.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
            tierTag.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            tierTag.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            tierTag.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("AccentSubtleBg"));
            tierTag.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("AccentSubtleBorder"));
            tierTag.SetBinding(UIElement.VisibilityProperty, new Binding("TierBadgeVisibility"));
            FrameworkElementFactory tierLbl = new FrameworkElementFactory(typeof(TextBlock));
            tierLbl.SetValue(TextBlock.TextProperty, "含档位");
            tierLbl.SetValue(TextBlock.FontSizeProperty, 10.0);
            tierLbl.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            tierLbl.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentText"));
            tierTag.AppendChild(tierLbl);
            badges.AppendChild(tierTag);

            FrameworkElementFactory usedTag = new FrameworkElementFactory(typeof(Border));
            usedTag.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            usedTag.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
            usedTag.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            usedTag.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            usedTag.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("SemanticSuccessBg"));
            usedTag.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticSuccessBorder"));
            usedTag.SetBinding(UIElement.VisibilityProperty, new Binding("ActiveBadgeVisibility"));
            FrameworkElementFactory usedLbl = new FrameworkElementFactory(typeof(TextBlock));
            usedLbl.SetValue(TextBlock.TextProperty, "已用");
            usedLbl.SetValue(TextBlock.FontSizeProperty, 10.0);
            usedLbl.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            usedLbl.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"));
            usedTag.AppendChild(usedLbl);
            badges.AppendChild(usedTag);
            dock.AppendChild(badges);

            // 左侧圆点 + 模型名
            FrameworkElementFactory leftSp = new FrameworkElementFactory(typeof(StackPanel));
            leftSp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            leftSp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory dot = new FrameworkElementFactory(typeof(Shapes.Ellipse));
            dot.SetValue(FrameworkElement.WidthProperty, 6.0);
            dot.SetValue(FrameworkElement.HeightProperty, 6.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetBinding(Shapes.Shape.FillProperty, new Binding("ProviderDotBrush"));
            leftSp.AppendChild(dot);

            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new Binding("ModelId"));
            txt.SetBinding(FrameworkElement.ToolTipProperty, new Binding("ModelId"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            txt.SetValue(TextBlock.FontSizeProperty, 12.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgPrimary"));
            leftSp.AppendChild(txt);

            dock.AppendChild(leftSp);
            tpl.VisualTree = dock;
            col.CellTemplate = tpl;
            return col;
        }

        private DataGridTemplateColumn BuildSourceBadgeColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn
            {
                Header = "来源",
                SortMemberPath = "SourceRank",
                Width = new DataGridLength(96)
            };

            DataTemplate tpl = new DataTemplate();
            FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border));
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            pill.SetValue(Border.PaddingProperty, new Thickness(7, 2, 7, 2));
            pill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            pill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            pill.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            pill.SetBinding(Border.BackgroundProperty, new Binding("SourceBgBrush"));
            pill.SetBinding(Border.BorderBrushProperty, new Binding("SourceBorderBrush"));

            FrameworkElementFactory lbl = new FrameworkElementFactory(typeof(TextBlock));
            lbl.SetBinding(TextBlock.TextProperty, new Binding("SourceBadgeText"));
            lbl.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            lbl.SetValue(TextBlock.FontSizeProperty, 10.5);
            lbl.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            lbl.SetBinding(TextBlock.ForegroundProperty, new Binding("SourceFgBrush"));
            pill.AppendChild(lbl);

            tpl.VisualTree = pill;
            col.CellTemplate = tpl;
            return col;
        }

        private Border BuildDetailDrawer()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(16, 12, 16, 12));
            card.Margin = new Thickness(0, 10, 0, 0);
            card.Visibility = Visibility.Collapsed;

            StackPanel sp = new StackPanel();

            // 抽屉顶栏：标题 + 官方基准提示 + 关闭按钮
            DockPanel hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            Button closeBtn = ModernControlsFactory.CreateIconGhostButton("M 3,3 L 13,13 M 13,3 L 3,13", "收起详情");
            closeBtn.Width = 24;
            closeBtn.Height = 24;
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(closeBtn, Dock.Right);
            closeBtn.Click += delegate(object s, RoutedEventArgs e) { CloseDrawer(); };
            hdr.Children.Add(closeBtn);

            StackPanel hdrLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _drawerTitleText = new TextBlock
            {
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _drawerTitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            hdrLeft.Children.Add(_drawerTitleText);

            _drawerBaselineHint = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _drawerBaselineHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hdrLeft.Children.Add(_drawerBaselineHint);
            hdr.Children.Add(hdrLeft);
            sp.Children.Add(hdr);

            // 区块 A：官方多档位参考价（按需显示）
            _tierSectionBox = BuildTierSection();
            sp.Children.Add(_tierSectionBox);

            // 区块 B：普通档自定义单价覆盖
            Grid editGrid = new Grid();
            _modelIdColDef = new ColumnDefinition { Width = new GridLength(0) };
            editGrid.ColumnDefinitions.Add(_modelIdColDef);                                                          // Model ID (新建时显示)
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // Input
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // Output
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // Cache Read
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // Cache Write
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // Buttons

            _modelIdInputCol = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            _modelIdInputCol.Children.Add(MakeSmallLabel("模型 ID"));
            _modelIdInput = ModernControlsFactory.CreateTextInput("", 30);
            _modelIdInput.FontFamily = DesignSystem.FontMono;
            _modelIdInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (!_suppressSelectionChanged) UpdateEditorButtons();
            };
            _modelIdInputCol.Children.Add(_modelIdInput);
            Grid.SetColumn(_modelIdInputCol, 0);
            editGrid.Children.Add(_modelIdInputCol);

            StackPanel inCol = MakeRateInputColumn("输入 ($/1M)", out _inputRateBox);
            Grid.SetColumn(inCol, 1);
            editGrid.Children.Add(inCol);

            StackPanel outCol = MakeRateInputColumn("输出 ($/1M)", out _outputRateBox);
            Grid.SetColumn(outCol, 3);
            editGrid.Children.Add(outCol);

            StackPanel crCol = MakeRateInputColumn("缓存读 ($/1M)", out _cacheReadRateBox);
            Grid.SetColumn(crCol, 5);
            editGrid.Children.Add(crCol);

            StackPanel cwCol = MakeRateInputColumn("缓存写 ($/1M)", out _cacheWriteRateBox);
            Grid.SetColumn(cwCol, 7);
            editGrid.Children.Add(cwCol);

            StackPanel btnRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            _deleteRuleBtn = ModernControlsFactory.CreateButton("恢复官方价", "secondary");
            _deleteRuleBtn.Height = 30;
            _deleteRuleBtn.Margin = new Thickness(0, 0, 6, 0);
            _deleteRuleBtn.Click += OnDeleteOverrideClick;
            btnRow.Children.Add(_deleteRuleBtn);

            _saveRuleBtn = ModernControlsFactory.CreateButton("保存自定义", "primary");
            _saveRuleBtn.Height = 30;
            _saveRuleBtn.Click += OnSaveOverrideClick;
            btnRow.Children.Add(_saveRuleBtn);
            Grid.SetColumn(btnRow, 9);
            editGrid.Children.Add(btnRow);

            sp.Children.Add(editGrid);

            TextBlock footNote = new TextBlock
            {
                Text = "自定义仅覆盖普通档位（填 0 表示免费）；Fast / Ultrafast / Flex 始终使用独立档位规则。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 7, 0, 0)
            };
            footNote.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(footNote);

            card.Child = sp;
            return card;
        }

        private Border BuildTierSection()
        {
            Border box = new Border
            {
                Visibility = Visibility.Collapsed,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 10)
            };
            box.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            box.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel();

            DockPanel top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            StackPanel dateBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dateBar, Dock.Right);

            _tierTodayBtn = ModernControlsFactory.CreateButton("查看今日", "ghost");
            _tierTodayBtn.Tag = "tier-current-date";
            _tierTodayBtn.Height = 24;
            _tierTodayBtn.FontSize = DesignSystem.TextCaption;
            _tierTodayBtn.Margin = new Thickness(0, 0, 6, 0);
            _tierTodayBtn.Visibility = Visibility.Collapsed;
            _tierTodayBtn.Click += delegate(object s, RoutedEventArgs e) { _tierDatePicker.SelectedDate = DateTime.Today; };
            dateBar.Children.Add(_tierTodayBtn);

            TextBlock dLbl = new TextBlock
            {
                Text = "适用日期",
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            dLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            dateBar.Children.Add(dLbl);

            _tierDatePicker = new DatePicker { SelectedDate = DateTime.Today, Height = 26, Width = 132, Tag = "tier-date" };
            _tierDatePicker.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
            _tierDatePicker.SetResourceReference(Control.BackgroundProperty, "BgInput");
            _tierDatePicker.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");
            Style dtStyle = new Style(typeof(DatePickerTextBox));
            dtStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("BgInput")));
            dtStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            dtStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("BorderStrong")));
            _tierDatePicker.Resources[typeof(DatePickerTextBox)] = dtStyle;
            _tierDatePicker.SelectedDateChanged += delegate(object s, SelectionChangedEventArgs e) { RefreshTierDetailsForSelection(); };
            dateBar.Children.Add(_tierDatePicker);
            top.Children.Add(dateBar);

            TextBlock tTitle = new TextBlock
            {
                Text = "官方档位参考价（USD / 1M，独立于订阅额度）",
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            tTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            top.Children.Add(tTitle);
            sp.Children.Add(top);

            _tierGrid = ModernDataGridFactory.CreateModernDataGrid();
            _tierGrid.Tag = "tier-prices";
            _tierGrid.IsReadOnly = true;
            _tierGrid.Height = 148;
            _tierGrid.RowHeight = 29;
            _tierGrid.ColumnHeaderHeight = 28;
            _tierGrid.CanUserSortColumns = false;
            _tierGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_tierGrid, "档位", "TierLabel", "TierLabel", new DataGridLength(90), false, false, true));
            _tierGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_tierGrid, "输入", "InputText", "InputText", new DataGridLength(1, DataGridLengthUnitType.Star), true, true, false));
            _tierGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_tierGrid, "输出", "OutputText", "OutputText", new DataGridLength(1, DataGridLengthUnitType.Star), true, true, false));
            _tierGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_tierGrid, "缓存读", "ReadText", "ReadText", new DataGridLength(1, DataGridLengthUnitType.Star), true, true, false));
            _tierGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_tierGrid, "缓存写", "WriteText", "WriteText", new DataGridLength(1, DataGridLengthUnitType.Star), true, true, false));
            _tierGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(_tierGrid, "状态", "Status", "Status", new DataGridLength(74), false, false, false));
            _tierGrid.SelectionChanged += delegate(object s, SelectionChangedEventArgs e) { UpdateSelectedTierEvidence(); };
            sp.Children.Add(_tierGrid);

            _tierRangeText = MakeEvidenceText("FgSecondary", 6);
            _tierLongRatesText = MakeEvidenceText("FgPrimary", 3);
            _tierCacheText = MakeEvidenceText("FgSecondary", 3);
            sp.Children.Add(_tierRangeText);
            sp.Children.Add(_tierLongRatesText);
            sp.Children.Add(_tierCacheText);

            DockPanel metaRow = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            _tierOpenSourceBtn = ModernControlsFactory.CreateButton("打开官方来源", "ghost");
            _tierOpenSourceBtn.Height = 22;
            _tierOpenSourceBtn.FontSize = DesignSystem.TextCaption;
            _tierOpenSourceBtn.Padding = new Thickness(6, 0, 6, 0);
            _tierOpenSourceBtn.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_tierOpenSourceBtn, Dock.Right);
            _tierOpenSourceBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                Uri uri;
                if (Uri.TryCreate(_tierSourceUrl, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
                {
                    try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch { SetStatus("无法打开来源链接", "warning"); }
                }
            };
            metaRow.Children.Add(_tierOpenSourceBtn);

            _tierMetaText = MakeEvidenceText("FgMuted", 0);
            _tierMetaText.VerticalAlignment = VerticalAlignment.Center;
            metaRow.Children.Add(_tierMetaText);
            sp.Children.Add(metaRow);

            box.Child = sp;
            return box;
        }

        private UIElement BuildStatusRow()
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 8, 2, 0)
            };
            _statusDot = new Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            _statusDot.SetResourceReference(Shapes.Shape.FillProperty, "AccentPrimary");
            sp.Children.Add(_statusDot);

            _statusText = new TextBlock
            {
                Text = "点击任意模型行查看官方档位依据或设置普通档自定义单价",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(_statusText);
            return sp;
        }

        // -------------------- 抽屉开关与档位详情 --------------------

        private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            PricingRow row = _grid.SelectedItem as PricingRow;
            _selectedRow = row;
            if (row != null) OpenDrawerForRow(row);
        }

        private void OpenDrawerForRow(PricingRow row)
        {
            if (row == null) return;
            _isNewCustomMode = false;
            _selectedRow = row;

            _suppressSelectionChanged = true;
            try
            {
                _drawerTitleText.Text = row.ModelId;
                _modelIdInputCol.Visibility = Visibility.Collapsed;
                if (_modelIdColDef != null) _modelIdColDef.Width = new GridLength(0);
                _modelIdInput.Text = row.ModelId;
                if (row.SourceKind == "unpriced")
                {
                    _inputRateBox.Text = "";
                    _outputRateBox.Text = "";
                    _cacheReadRateBox.Text = "";
                    _cacheWriteRateBox.Text = "";
                }
                else
                {
                    _inputRateBox.Text = FormatPlainRate(row.InputPerM);
                    _outputRateBox.Text = FormatPlainRate(row.OutputPerM);
                    _cacheReadRateBox.Text = FormatPlainRate(row.CacheReadPerM);
                    _cacheWriteRateBox.Text = FormatPlainRate(row.CacheWritePerM);
                }
            }
            finally { _suppressSelectionChanged = false; }

            UpdateEditorButtons();
            RefreshTierDetailsForSelection();
            _detailDrawer.Visibility = Visibility.Visible;
        }

        private void OpenNewCustomDrawer(string initialModelId)
        {
            _isNewCustomMode = true;
            _suppressSelectionChanged = true;
            try
            {
                _grid.SelectedItem = null;
                _selectedRow = null;
                _drawerTitleText.Text = "新增自定义单价";
                _modelIdInputCol.Visibility = Visibility.Visible;
                if (_modelIdColDef != null) _modelIdColDef.Width = new GridLength(1.5, GridUnitType.Star);
                _modelIdInput.Text = initialModelId ?? "";
                _inputRateBox.Text = "0";
                _outputRateBox.Text = "0";
                _cacheReadRateBox.Text = "0";
                _cacheWriteRateBox.Text = "0";
                _tierSectionBox.Visibility = Visibility.Collapsed;
            }
            finally { _suppressSelectionChanged = false; }

            UpdateEditorButtons();
            _detailDrawer.Visibility = Visibility.Visible;
            try { _modelIdInput.Focus(); _modelIdInput.SelectAll(); } catch { }
        }

        private void CloseDrawer()
        {
            _detailDrawer.Visibility = Visibility.Collapsed;
            _suppressSelectionChanged = true;
            try { _grid.SelectedItem = null; _selectedRow = null; }
            finally { _suppressSelectionChanged = false; }
        }

        private void RefreshTierDetailsForSelection()
        {
            if (_tierSectionBox == null || _selectedRow == null || !_selectedRow.HasTierRules)
            {
                if (_tierSectionBox != null) _tierSectionBox.Visibility = Visibility.Collapsed;
                return;
            }

            _tierSectionBox.Visibility = Visibility.Visible;
            string day = (_tierDatePicker.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string provider = string.IsNullOrWhiteSpace(_selectedRow.Provider)
                ? UsageServiceTier.InferProvider(_selectedRow.ModelId)
                : _selectedRow.Provider;
            string lookupModel = string.IsNullOrEmpty(_selectedRow.NormId) ? _selectedRow.ModelId : _selectedRow.NormId;

            List<TierRateRow> rows = new List<TierRateRow>();
            string[] tiers = new string[] { "standard", "fast", "ultrafast", "flex" };
            for (int i = 0; i < tiers.Length; i++)
            {
                string t = tiers[i];
                ServiceTierPrice price = null;
                try { price = ServiceTierPricing.Find(provider, lookupModel, t, day, CustomHomeOrNull); } catch { }
                string label = t == "standard" ? "Standard" : t == "fast" ? "Fast" : t == "ultrafast" ? "Ultrafast" : "Flex";
                rows.Add(new TierRateRow { Tier = t, TierLabel = label, Price = price });
            }

            TierRateRow prevSel = _tierGrid.SelectedItem as TierRateRow;
            string wantTier = prevSel != null ? prevSel.Tier : "fast";
            _tierGrid.ItemsSource = rows;
            TierRateRow toSelect = null;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Tier == wantTier) { toSelect = rows[i]; break; }
            if (toSelect == null && rows.Count > 0) toSelect = rows[0];
            _tierGrid.SelectedItem = toSelect;
            UpdateSelectedTierEvidence();
        }

        private void UpdateSelectedTierEvidence()
        {
            TierRateRow row = _tierGrid.SelectedItem as TierRateRow;
            if (row == null) return;
            ServiceTierPrice p = row.Price;
            if (p == null)
            {
                _tierRangeText.Text = row.TierLabel + "：所选模型与日期未收录该档位价格（保持未计价，不套用普通价或猜测倍率）。";
                _tierLongRatesText.Text = "";
                _tierLongRatesText.Visibility = Visibility.Collapsed;
                _tierCacheText.Text = "";
                _tierCacheText.Visibility = Visibility.Collapsed;
                _tierMetaText.Text = "可切换适用日期查看最新参考价";
                _tierSourceUrl = null;
                _tierOpenSourceBtn.Visibility = Visibility.Collapsed;
                _tierTodayBtn.Visibility = (_tierDatePicker.SelectedDate ?? DateTime.Today).Date == DateTime.Today
                    ? Visibility.Collapsed : Visibility.Visible;
                return;
            }

            _tierTodayBtn.Visibility = Visibility.Collapsed;
            _tierRangeText.Text = p.max_prompt_tokens > 0
                ? (row.TierLabel + " · 输入上限 ≤ " + p.max_prompt_tokens.ToString("N0", CultureInfo.InvariantCulture) + " token（超出未计价）")
                : (row.TierLabel + " · 无额外输入上下文上限");

            bool hasLong = p.long_context_threshold > 0 && (p.max_prompt_tokens <= 0 || p.max_prompt_tokens > p.long_context_threshold);
            if (hasLong)
            {
                _tierLongRatesText.Text = "长上下文 > " + p.long_context_threshold.ToString("N0", CultureInfo.InvariantCulture) +
                    " token：入 " + MoneyCompact(p.long_input_per_m) + " · 出 " + MoneyCompact(p.long_output_per_m) +
                    " · 读 " + MoneyCompact(p.long_cache_read_per_m) + " · 写 " + MoneyCompact(p.long_cache_write_per_m) + " / 1M";
                _tierLongRatesText.Visibility = Visibility.Visible;
            }
            else
            {
                _tierLongRatesText.Text = "";
                _tierLongRatesText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(p.cache_write_long_duration))
            {
                _tierCacheText.Text = "长期缓存写（" + p.cache_write_long_duration + "）：" + MoneyCompact(p.cache_write_long_per_m) + " / 1M" +
                    (hasLong ? ("；长上下文 " + MoneyCompact(p.long_cache_write_long_per_m) + " / 1M") : "") +
                    (p.cache_write_long_duration == "12h" ? "（当前日志未单独识别 12h）" : "");
                _tierCacheText.Visibility = Visibility.Visible;
            }
            else
            {
                _tierCacheText.Text = "";
                _tierCacheText.Visibility = Visibility.Collapsed;
            }

            _tierMetaText.Text = "生效：" + p.effective_from +
                (string.IsNullOrEmpty(p.effective_until) ? " 起" : (" ~ " + p.effective_until)) +
                " · 版本 " + (p.version ?? "未标注") + " · 来源 " + (p.source ?? "");
            _tierSourceUrl = p.source;
            Uri uri;
            _tierOpenSourceBtn.Visibility = Uri.TryCreate(_tierSourceUrl, UriKind.Absolute, out uri) &&
                (uri.Scheme == "https" || uri.Scheme == "http") ? Visibility.Visible : Visibility.Collapsed;
        }

        // -------------------- 目录加载与筛选 --------------------

        private void ReloadCatalogAndBind(string selectModelId, bool openDrawer)
        {
            LoadCloudCacheFile();
            LoadAllCustomFiles();
            LoadTierCatalogIndex();

            PricingEngine engine = new PricingEngine(_homeDir);
            Dictionary<string, RawRateEntry> builtinRef = GetBuiltinReferenceMap();
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            List<string> orderedCandidates = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, RawRateEntry> kv in _customRates)
            {
                string id = kv.Key.Trim();
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seen.Add(dedup)) orderedCandidates.Add(id);
            }
            for (int i = 0; i < _activeModelList.Count; i++)
            {
                string id = _activeModelList[i];
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seen.Add(dedup)) orderedCandidates.Add(id);
            }
            foreach (KeyValuePair<string, string> kv in _tierModelProviders)
            {
                string id = kv.Key;
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seen.Add(dedup)) orderedCandidates.Add(id);
            }
            for (int i = 0; i < BuiltinSeedModelIds.Length; i++)
            {
                string id = BuiltinSeedModelIds[i];
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seen.Add(dedup)) orderedCandidates.Add(id);
            }
            List<string> cloudKeys = new List<string>(_cloudRates.Keys);
            cloudKeys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cloudKeys.Count; i++)
            {
                string id = cloudKeys[i];
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seen.Add(dedup)) orderedCandidates.Add(id);
            }

            Brush customBg = GetThemeBrush("AccentSubtleBg"), customBd = GetThemeBrush("AccentSubtleBorder"), customFg = GetThemeBrush("AccentText");
            Brush cloudBg = GetThemeBrush("BgSurface"), cloudBd = GetThemeBrush("BorderStrong"), cloudFg = GetThemeBrush("FgPrimary");
            Brush builtinBg = GetThemeBrush("BgSurface"), builtinBd = GetThemeBrush("BorderCard"), builtinFg = GetThemeBrush("FgSecondary");
            Brush warnBg = GetThemeBrush("SemanticWarningBg"), warnBd = GetThemeBrush("SemanticWarningBorder"), warnFg = GetThemeBrush("SemanticWarning");

            List<PricingRow> built = new List<PricingRow>(orderedCandidates.Count);
            for (int i = 0; i < orderedCandidates.Count; i++)
            {
                string displayId = orderedCandidates[i];
                string normId = PricingEngine.NormalizeModelId(displayId, displayId.ToLowerInvariant());
                bool isUsed = _activeRawSet.Contains(displayId) || (!string.IsNullOrEmpty(normId) && _activeNormSet.Contains(normId));

                string tierProvider;
                bool hasTier = _tierModelProviders.TryGetValue(normId, out tierProvider) || _tierModelProviders.TryGetValue(displayId, out tierProvider);
                if (!hasTier) tierProvider = UsageServiceTier.InferProvider(displayId);

                RawRateEntry customEntry = null;
                if (!_customRates.TryGetValue(displayId, out customEntry) && !string.IsNullOrEmpty(normId))
                {
                    string mappedKey;
                    if (_customNormToRawKey.TryGetValue(normId, out mappedKey))
                        _customRates.TryGetValue(mappedKey, out customEntry);
                }

                RawRateEntry cloudEntry = null;
                if (!_cloudRates.TryGetValue(displayId.ToLowerInvariant(), out cloudEntry) && !string.IsNullOrEmpty(normId))
                    _cloudRates.TryGetValue(normId, out cloudEntry);

                RawRateEntry builtinEntry = null;
                if (!builtinRef.TryGetValue(displayId, out builtinEntry) && !string.IsNullOrEmpty(normId))
                    builtinRef.TryGetValue(normId, out builtinEntry);

                ModelRate resolved = engine.Lookup(displayId);
                ServiceTierPrice tierStd = null;
                if (hasTier)
                {
                    try { tierStd = ServiceTierPricing.Find(tierProvider, normId, "standard", today, CustomHomeOrNull); } catch { }
                }

                bool hasOfficial = false;
                double offIn = 0, offOut = 0, offCr = 0, offCw = 0;
                if (cloudEntry != null)
                {
                    hasOfficial = true;
                    offIn = cloudEntry.Input; offOut = cloudEntry.Output;
                    offCr = cloudEntry.CacheRead > 0 ? cloudEntry.CacheRead : (builtinEntry != null && IsSimilarRate(builtinEntry.Input, cloudEntry.Input) ? builtinEntry.CacheRead : 0);
                    offCw = cloudEntry.CacheWrite > 0 ? cloudEntry.CacheWrite : (builtinEntry != null && IsSimilarRate(builtinEntry.Input, cloudEntry.Input) ? builtinEntry.CacheWrite : 0);
                }
                else if (tierStd != null)
                {
                    hasOfficial = true;
                    offIn = tierStd.input_per_m; offOut = tierStd.output_per_m;
                    offCr = tierStd.cache_read_per_m; offCw = tierStd.cache_write_per_m;
                }
                else if (builtinEntry != null)
                {
                    hasOfficial = true;
                    offIn = builtinEntry.Input; offOut = builtinEntry.Output; offCr = builtinEntry.CacheRead; offCw = builtinEntry.CacheWrite;
                }
                else if (resolved != null && !string.Equals(resolved.Source, "custom", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(resolved.Source, "unpriced", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(resolved.Source, "fallback", StringComparison.OrdinalIgnoreCase))
                {
                    hasOfficial = true;
                    offIn = resolved.InputPerM; offOut = resolved.OutputPerM; offCr = resolved.CacheReadPerM; offCw = resolved.CacheWritePerM;
                }

                double curIn, curOut, curCr, curCw;
                string srcKind, badgeText;
                int srcRank;
                Brush sBg, sBd, sFg;

                if (customEntry != null)
                {
                    curIn = customEntry.Input; curOut = customEntry.Output; curCr = customEntry.CacheRead; curCw = customEntry.CacheWrite;
                    srcKind = "custom"; srcRank = 1; badgeText = "自定义";
                    sBg = customBg; sBd = customBd; sFg = customFg;
                }
                else if (cloudEntry != null || (resolved != null && string.Equals(resolved.Source, "cloud", StringComparison.OrdinalIgnoreCase)))
                {
                    curIn = resolved != null ? resolved.InputPerM : offIn;
                    curOut = resolved != null ? resolved.OutputPerM : offOut;
                    curCr = resolved != null ? resolved.CacheReadPerM : offCr;
                    curCw = resolved != null ? resolved.CacheWritePerM : offCw;
                    srcKind = "cloud"; srcRank = 2; badgeText = "云端三源";
                    sBg = cloudBg; sBd = cloudBd; sFg = cloudFg;
                }
                else if (tierStd != null)
                {
                    curIn = tierStd.input_per_m; curOut = tierStd.output_per_m; curCr = tierStd.cache_read_per_m; curCw = tierStd.cache_write_per_m;
                    srcKind = "tier"; srcRank = 2; badgeText = "官方档位";
                    sBg = cloudBg; sBd = cloudBd; sFg = cloudFg;
                }
                else if (resolved == null || string.Equals(resolved.Source, "unpriced", StringComparison.OrdinalIgnoreCase))
                {
                    curIn = curOut = curCr = curCw = 0;
                    srcKind = "unpriced"; srcRank = 5; badgeText = "未计价";
                    sBg = warnBg; sBd = warnBd; sFg = warnFg;
                }
                else if (string.Equals(resolved.Source, "fallback", StringComparison.OrdinalIgnoreCase))
                {
                    curIn = resolved.InputPerM; curOut = resolved.OutputPerM; curCr = resolved.CacheReadPerM; curCw = resolved.CacheWritePerM;
                    srcKind = "fallback"; srcRank = 4; badgeText = "兜底估算";
                    sBg = warnBg; sBd = warnBd; sFg = warnFg;
                }
                else
                {
                    curIn = resolved.InputPerM; curOut = resolved.OutputPerM; curCr = resolved.CacheReadPerM; curCw = resolved.CacheWritePerM;
                    srcKind = "builtin"; srcRank = 3; badgeText = "内置基准";
                    sBg = builtinBg; sBd = builtinBd; sFg = builtinFg;
                }

                PricingRow row = new PricingRow
                {
                    ModelId = displayId,
                    NormId = normId,
                    Provider = tierProvider,
                    IsActiveUsed = isUsed,
                    ActiveBadgeVisibility = isUsed ? Visibility.Visible : Visibility.Collapsed,
                    HasTierRules = hasTier,
                    TierBadgeVisibility = hasTier ? Visibility.Visible : Visibility.Collapsed,
                    IsCustom = (customEntry != null),
                    InCloudCache = (cloudEntry != null) || string.Equals(srcKind, "cloud", StringComparison.OrdinalIgnoreCase),
                    SourceKind = srcKind,
                    SourceRank = srcRank,
                    SourceBadgeText = badgeText,
                    SourceBgBrush = sBg,
                    SourceBorderBrush = sBd,
                    SourceFgBrush = sFg,
                    ProviderDotBrush = DesignSystem.BrushHex(DesignSystem.GetProviderColorHex(displayId, _isDark)),
                    InputPerM = curIn,
                    OutputPerM = curOut,
                    CacheReadPerM = curCr,
                    CacheWritePerM = curCw,
                    InputText = srcKind == "unpriced" ? "—" : FormatDollarPerM(curIn),
                    OutputText = srcKind == "unpriced" ? "—" : FormatDollarPerM(curOut),
                    CacheReadText = srcKind == "unpriced" ? "—" : FormatDollarPerM(curCr),
                    CacheWriteText = srcKind == "unpriced" ? "—" : FormatDollarPerM(curCw),
                    HasOfficialRate = hasOfficial,
                    OfficialInput = offIn,
                    OfficialOutput = offOut,
                    OfficialCacheRead = offCr,
                    OfficialCacheWrite = offCw
                };
                built.Add(row);
            }

            built.Sort(CompareDefaultRows);
            _allRows = built;

            UpdateSummaryText();
            ApplyFiltersAndRefreshGrid(selectModelId);

            if (openDrawer && !string.IsNullOrEmpty(selectModelId))
            {
                PricingRow m = FindMatchingCatalogRow(selectModelId);
                if (m != null) OpenDrawerForRow(m);
            }
        }

        private void UpdateSummaryText()
        {
            if (_summaryText == null) return;
            _summaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} 款 · {1} 自定义",
                _allRows.Count,
                _customRates.Count);
            string tip = "普通基准价来自云端三源（models.dev + LiteLLM + OpenRouter）与内置参考";
            if (!string.IsNullOrEmpty(_cloudSourceSummary))
                tip = "最近云端来源：" + _cloudSourceSummary + (string.IsNullOrEmpty(_cloudUpdatedAt) ? "" : (" · " + _cloudUpdatedAt));
            _summaryText.ToolTip = tip;
        }

        private void ApplyFiltersAndRefreshGrid(string selectModelId)
        {
            if (_grid == null) return;
            string kw = (_searchBox != null && _searchBox.Text != null) ? _searchBox.Text.Trim() : "";
            string kwLower = kw.ToLowerInvariant();
            string kwNorm = PricingEngine.NormalizeModelId(kw, "");

            List<PricingRow> filtered = new List<PricingRow>(_allRows.Count);
            for (int i = 0; i < _allRows.Count; i++)
            {
                PricingRow r = _allRows[i];
                if (_activeFilterIndex == 1 && !r.IsActiveUsed) continue;
                if (_activeFilterIndex == 2 && r.SourceKind != "unpriced") continue;
                if (_activeFilterIndex == 3 && !r.IsCustom) continue;
                if (_activeFilterIndex == 4 && !r.HasTierRules) continue;

                if (!string.IsNullOrEmpty(kwLower))
                {
                    bool hit = r.ModelId.ToLowerInvariant().IndexOf(kwLower, StringComparison.Ordinal) >= 0 ||
                               (!string.IsNullOrEmpty(r.NormId) && r.NormId.IndexOf(kwLower, StringComparison.Ordinal) >= 0) ||
                               (!string.IsNullOrEmpty(kwNorm) && !string.IsNullOrEmpty(r.NormId) && r.NormId.IndexOf(kwNorm, StringComparison.Ordinal) >= 0) ||
                               (!string.IsNullOrEmpty(r.Provider) && r.Provider.ToLowerInvariant().IndexOf(kwLower, StringComparison.Ordinal) >= 0) ||
                               r.SourceBadgeText.ToLowerInvariant().IndexOf(kwLower, StringComparison.Ordinal) >= 0;
                    if (!hit) continue;
                }
                filtered.Add(r);
            }

            _suppressSelectionChanged = true;
            try
            {
                _grid.ItemsSource = filtered;
                PricingRow toSelect = null;
                if (!string.IsNullOrEmpty(selectModelId))
                {
                    string targetNorm = PricingEngine.NormalizeModelId(selectModelId, selectModelId.ToLowerInvariant());
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        if (string.Equals(filtered[i].ModelId, selectModelId, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(targetNorm) && string.Equals(filtered[i].NormId, targetNorm, StringComparison.OrdinalIgnoreCase)))
                        {
                            toSelect = filtered[i];
                            break;
                        }
                    }
                }
                if (toSelect != null)
                {
                    _grid.SelectedItem = toSelect;
                    _selectedRow = toSelect;
                }
            }
            finally { _suppressSelectionChanged = false; }

            bool empty = filtered.Count == 0;
            _emptyOverlay.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                _emptyHintText.Text = string.IsNullOrEmpty(kw)
                    ? "当前筛选下暂无模型"
                    : ("未找到 \"" + kw + "\"");
                _emptyAddBtn.Visibility = string.IsNullOrEmpty(kw) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        // -------------------- 保存 / 删除 / 同步 --------------------

        private void UpdateEditorButtons()
        {
            string curModel = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            PricingRow matched = FindMatchingCatalogRow(curModel);
            bool hasCustom = HasSavedCustomOverride(curModel);

            if (_deleteRuleBtn != null)
                _deleteRuleBtn.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;

            if (_drawerBaselineHint != null)
            {
                if (_isNewCustomMode && string.IsNullOrEmpty(curModel))
                    _drawerBaselineHint.Text = "输入模型 ID 与每百万 Token 美元单价";
                else if (matched != null && matched.HasOfficialRate)
                    _drawerBaselineHint.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "基准：入 ${0} / 出 ${1} / 读 ${2} / 写 ${3}",
                        FormatPlainRate(matched.OfficialInput),
                        FormatPlainRate(matched.OfficialOutput),
                        FormatPlainRate(matched.OfficialCacheRead),
                        FormatPlainRate(matched.OfficialCacheWrite));
                else if (hasCustom)
                    _drawerBaselineHint.Text = "自定义模型";
                else
                    _drawerBaselineHint.Text = "无官方基准价";
            }
        }

        private void OnSaveOverrideClick(object sender, RoutedEventArgs e)
        {
            string modelId = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            if (string.IsNullOrEmpty(modelId))
            {
                SetStatus("模型 ID 不能为空", "warning");
                return;
            }

            double inp, outp, cr, cw;
            if (!TryParseNonNegative(_inputRateBox.Text, out inp) ||
                !TryParseNonNegative(_outputRateBox.Text, out outp) ||
                !TryParseNonNegative(_cacheReadRateBox.Text, out cr) ||
                !TryParseNonNegative(_cacheWriteRateBox.Text, out cw))
            {
                SetStatus("请输入大于或等于 0 的数字单价（USD / 1M）", "danger");
                return;
            }
            if (inp > 10000.0 || outp > 10000.0 || cr > 10000.0 || cw > 10000.0)
            {
                SetStatus("单价超过 $10,000 / 1M，请确认单位是否为每百万 Token", "warning");
                return;
            }

            try
            {
                LoadAllCustomFiles();
                RemoveCustomKeysMatching(modelId);
                _customRates[modelId] = new RawRateEntry(RoundRateClean(inp), RoundRateClean(outp), RoundRateClean(cr), RoundRateClean(cw));
                PersistCustomRatesAtomically();
                ReloadCatalogAndBind(modelId, true);

                if (_onPricesUpdated != null) { try { _onPricesUpdated(); } catch { } }
                string msg = string.Format(CultureInfo.InvariantCulture, "已保存 \"{0}\" 自定义单价（入 ${1} / 出 ${2}）", modelId, FormatPlainRate(inp), FormatPlainRate(outp));
                SetStatus(msg, "success");
                Toast("已保存自定义单价", msg, "success");
            }
            catch (Exception ex)
            {
                SetStatus("保存失败：" + ex.Message, "danger");
            }
        }

        private void OnDeleteOverrideClick(object sender, RoutedEventArgs e)
        {
            string modelId = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            if (string.IsNullOrEmpty(modelId)) return;

            try
            {
                LoadAllCustomFiles();
                if (!RemoveCustomKeysMatching(modelId))
                {
                    UpdateEditorButtons();
                    return;
                }
                PersistCustomRatesAtomically();
                ReloadCatalogAndBind(modelId, true);
                if (_onPricesUpdated != null) { try { _onPricesUpdated(); } catch { } }
                string msg = "已恢复 \"" + modelId + "\" 为官方价格";
                SetStatus(msg, "success");
                Toast("已移除自定义覆盖", msg, "success");
            }
            catch (Exception ex)
            {
                SetStatus("删除失败：" + ex.Message, "danger");
            }
        }

        private void OnSyncCloudPricesClick(object sender, RoutedEventArgs e)
        {
            if (_isSyncingCloud) return;
            _isSyncingCloud = true;
            _syncCloudBtn.IsEnabled = false;
            SetStatus("正在同步 models.dev + LiteLLM + OpenRouter 价格库…", "neutral");

            Task.Run(delegate()
            {
                PricingSyncInfo info = null;
                string err = null;
                try { info = PricingEngine.SyncCloudPricing(true); }
                catch (Exception ex) { err = ex.Message; }

                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _isSyncingCloud = false;
                    _syncCloudBtn.IsEnabled = true;

                    if (!string.IsNullOrEmpty(err))
                    {
                        SetStatus("同步异常：" + err, "warning");
                        Toast("模型价格库同步失败", err, "warning");
                        return;
                    }
                    if (info != null && info.IsLive && info.CloudModelCount > 0)
                    {
                        string keep = _selectedRow != null ? _selectedRow.ModelId : null;
                        ReloadCatalogAndBind(keep, _detailDrawer.Visibility == Visibility.Visible);
                        if (_onPricesUpdated != null) { try { _onPricesUpdated(); } catch { } }
                        string msg = string.Format(CultureInfo.InvariantCulture, "已同步 {0:N0} 款云端单价（{1}）", info.CloudModelCount, info.Source);
                        SetStatus(msg, "success");
                        Toast("模型价格库已更新", msg, "success");
                    }
                    else
                    {
                        SetStatus("无法连接外网价格源，继续使用本地缓存与内置基准", "warning");
                    }
                }));
            });
        }

        // -------------------- 数据读写辅助 --------------------

        private void LoadTierCatalogIndex()
        {
            _tierModelProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                List<ServiceTierPrice> catalog = ServiceTierPricing.Enumerate(CustomHomeOrNull);
                for (int i = 0; i < catalog.Count; i++)
                {
                    ServiceTierPrice p = catalog[i];
                    if (p == null || string.IsNullOrWhiteSpace(p.model)) continue;
                    string norm = PricingEngine.NormalizeModelId(p.model, p.model.ToLowerInvariant());
                    if (!_tierModelProviders.ContainsKey(norm))
                        _tierModelProviders[norm] = p.provider ?? UsageServiceTier.InferProvider(norm);
                }
            }
            catch { }
        }

        private void LoadCloudCacheFile()
        {
            _cloudRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
            _cloudSourceSummary = "";
            _cloudUpdatedAt = "";
            try
            {
                string fp = PricingEngine.CloudPriceCachePath;
                if (!File.Exists(fp)) return;
                JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                Dictionary<string, object> root = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return;
                if (root.ContainsKey("source") && root["source"] != null) _cloudSourceSummary = Convert.ToString(root["source"], CultureInfo.InvariantCulture);
                if (root.ContainsKey("updated_at") && root["updated_at"] != null) _cloudUpdatedAt = Convert.ToString(root["updated_at"], CultureInfo.InvariantCulture);
                if (root.ContainsKey("models"))
                {
                    Dictionary<string, object> models = root["models"] as Dictionary<string, object>;
                    if (models != null)
                    {
                        foreach (KeyValuePair<string, object> kv in models)
                        {
                            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                            IList arr = kv.Value as IList;
                            if (arr == null || arr.Count < 4) continue;
                            double ic = SafeToDouble(arr[0]), oc = SafeToDouble(arr[1]), crc = SafeToDouble(arr[2]), cwc = SafeToDouble(arr[3]);
                            if (ic > 0.0 || oc > 0.0)
                                _cloudRates[kv.Key.Trim().ToLowerInvariant()] = new RawRateEntry(ic, oc, crc, cwc);
                        }
                    }
                }
            }
            catch { }
        }

        private List<string> GetExistingLegacyCustomPaths()
        {
            List<string> paths = new List<string>();
            if (IsCustomHome) return paths;
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                string p1 = Path.Combine(userProfile, ".toklens", "custom_prices.json");
                if (File.Exists(p1)) paths.Add(p1);
            }
            return paths;
        }

        private void LoadAllCustomFiles()
        {
            _customRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
            _customNormToRawKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> legacy = GetExistingLegacyCustomPaths();
            for (int i = 0; i < legacy.Count; i++) MergeCustomFile(legacy[i]);
            MergeCustomFile(PrimaryCustomPath);
        }

        private void MergeCustomFile(string fp)
        {
            if (string.IsNullOrWhiteSpace(fp) || !File.Exists(fp)) return;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                Dictionary<string, object> map = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                if (map == null) return;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    Dictionary<string, object> m = kv.Value as Dictionary<string, object>;
                    if (m == null || (!m.ContainsKey("input") && !m.ContainsKey("output"))) continue;
                    double ic = m.ContainsKey("input") ? SafeToDouble(m["input"]) : 0;
                    double oc = m.ContainsKey("output") ? SafeToDouble(m["output"]) : 0;
                    double crc = m.ContainsKey("cache_read") ? SafeToDouble(m["cache_read"]) : 0;
                    double cwc = m.ContainsKey("cache_write") ? SafeToDouble(m["cache_write"]) : 0;
                    if (ic < 0 || oc < 0 || crc < 0 || cwc < 0) continue;

                    string cleanKey = kv.Key.Trim();
                    string norm = PricingEngine.NormalizeModelId(cleanKey, cleanKey.ToLowerInvariant());
                    string prevRaw;
                    if (!string.IsNullOrEmpty(norm) && _customNormToRawKey.TryGetValue(norm, out prevRaw) &&
                        !string.Equals(prevRaw, cleanKey, StringComparison.OrdinalIgnoreCase))
                        _customRates.Remove(prevRaw);

                    _customRates[cleanKey] = new RawRateEntry(ic, oc, crc, cwc);
                    if (!string.IsNullOrEmpty(norm)) _customNormToRawKey[norm] = cleanKey;
                }
            }
            catch { }
        }

        private bool RemoveCustomKeysMatching(string modelId)
        {
            string targetNorm = PricingEngine.NormalizeModelId(modelId, modelId.ToLowerInvariant());
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, RawRateEntry> kv in _customRates)
            {
                string existingNorm = PricingEngine.NormalizeModelId(kv.Key, kv.Key.ToLowerInvariant());
                if (string.Equals(kv.Key, modelId, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(targetNorm) && string.Equals(existingNorm, targetNorm, StringComparison.OrdinalIgnoreCase)))
                    stale.Add(kv.Key);
            }
            for (int i = 0; i < stale.Count; i++) _customRates.Remove(stale[i]);
            return stale.Count > 0;
        }

        private void PersistCustomRatesAtomically()
        {
            List<string> sortedKeys = new List<string>(_customRates.Keys);
            sortedKeys.Sort(StringComparer.OrdinalIgnoreCase);
            JavaScriptSerializer js = new JavaScriptSerializer();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                string key = sortedKeys[i];
                RawRateEntry r = _customRates[key];
                sb.Append("  ").Append(js.Serialize(key)).Append(": {\n")
                  .Append("    \"input\": ").Append(FormatJsonNumber(r.Input)).Append(",\n")
                  .Append("    \"output\": ").Append(FormatJsonNumber(r.Output)).Append(",\n")
                  .Append("    \"cache_read\": ").Append(FormatJsonNumber(r.CacheRead)).Append(",\n")
                  .Append("    \"cache_write\": ").Append(FormatJsonNumber(r.CacheWrite)).Append("\n")
                  .Append("  }").Append(i < sortedKeys.Count - 1 ? "," : "").Append("\n");
            }
            sb.Append("}\n");
            string payload = sb.ToString();

            WriteFileAtomically(PrimaryCustomPath, payload);
            List<string> legacy = GetExistingLegacyCustomPaths();
            for (int i = 0; i < legacy.Count; i++)
            {
                try { WriteFileAtomically(legacy[i], payload); } catch { }
            }
        }

        private static void WriteFileAtomically(string targetPath, string content)
        {
            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(content);
                    sw.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(targetPath))
                {
                    try { File.Replace(tmp, targetPath, null, true); return; }
                    catch { File.Copy(tmp, targetPath, true); }
                }
                else
                {
                    File.Move(tmp, targetPath);
                    return;
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private PricingRow FindMatchingCatalogRow(string rawModelId)
        {
            if (string.IsNullOrWhiteSpace(rawModelId)) return null;
            string trimmed = rawModelId.Trim();
            string norm = PricingEngine.NormalizeModelId(trimmed, trimmed.ToLowerInvariant());
            for (int i = 0; i < _allRows.Count; i++)
            {
                PricingRow r = _allRows[i];
                if (string.Equals(r.ModelId, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(norm) && string.Equals(r.NormId, norm, StringComparison.OrdinalIgnoreCase)))
                    return r;
            }
            return null;
        }

        private bool HasSavedCustomOverride(string rawModelId)
        {
            if (string.IsNullOrWhiteSpace(rawModelId)) return false;
            string trimmed = rawModelId.Trim();
            if (_customRates.ContainsKey(trimmed)) return true;
            string norm = PricingEngine.NormalizeModelId(trimmed, "");
            return !string.IsNullOrEmpty(norm) && _customNormToRawKey.ContainsKey(norm);
        }

        private static int CompareDefaultRows(PricingRow a, PricingRow b)
        {
            int pa = GetPriorityBucket(a), pb = GetPriorityBucket(b);
            if (pa != pb) return pa.CompareTo(pb);
            return string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetPriorityBucket(PricingRow r)
        {
            if (r.IsActiveUsed && r.SourceKind == "unpriced") return 0;
            if (r.IsCustom && r.IsActiveUsed) return 1;
            if (r.IsCustom) return 2;
            if (r.IsActiveUsed) return 3;
            if (r.HasTierRules) return 4;
            if (r.SourceKind == "builtin") return 5;
            return 6;
        }

        private void SetStatus(string msg, string variant)
        {
            if (_statusText == null || _statusDot == null) return;
            _statusText.Text = msg ?? "";
            string fg = "FgMuted", dot = "AccentPrimary";
            if (variant == "success") { fg = "SemanticSuccess"; dot = "SemanticSuccess"; }
            else if (variant == "warning") { fg = "SemanticWarning"; dot = "SemanticWarning"; }
            else if (variant == "danger") { fg = "SemanticDanger"; dot = "SemanticDanger"; }
            _statusDot.SetResourceReference(Shapes.Shape.FillProperty, dot);
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        }

        private void Toast(string title, string detail, string variant)
        {
            if (_showToast != null) _showToast(title, detail, variant);
        }

        private Brush GetThemeBrush(string key)
        {
            if (Resources != null && Resources.Contains(key))
            {
                Brush b = Resources[key] as Brush;
                if (b != null) return b;
            }
            if (Application.Current != null && Application.Current.Resources != null && Application.Current.Resources.Contains(key))
            {
                Brush b2 = Application.Current.Resources[key] as Brush;
                if (b2 != null) return b2;
            }
            return Brushes.Transparent;
        }

        private static TextBlock MakeSmallLabel(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 0, 0, 4)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return tb;
        }

        private static StackPanel MakeRateInputColumn(string label, out TextBox box)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(MakeSmallLabel(label));
            box = ModernControlsFactory.CreateTextInput("", 30);
            box.FontFamily = DesignSystem.FontMono;
            sp.Children.Add(box);
            return sp;
        }

        private static TextBlock MakeEvidenceText(string fgKey, double topMargin)
        {
            TextBlock tb = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, topMargin, 0, 0)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            return tb;
        }

        private static bool IsSimilarRate(double a, double b)
        {
            return a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(a, b) * 0.25;
        }

        private static double SafeToDouble(object obj)
        {
            if (obj == null) return 0;
            double v;
            if (double.TryParse(Convert.ToString(obj, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) &&
                !double.IsNaN(v) && !double.IsInfinity(v))
                return v;
            return 0;
        }

        private static bool TryParseNonNegative(string raw, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            string cleaned = raw.Trim();
            if (cleaned.StartsWith("$", StringComparison.Ordinal)) cleaned = cleaned.Substring(1).Trim();
            double parsed;
            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0)
                return false;
            val = parsed;
            return true;
        }

        private static double RoundRateClean(double v)
        {
            return (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) ? 0 : Math.Round(v, 6, MidpointRounding.AwayFromZero);
        }

        private static string FormatJsonNumber(double val)
        {
            double cleaned = RoundRateClean(val);
            return cleaned == 0 ? "0" : cleaned.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string FormatPlainRate(double v)
        {
            double c = RoundRateClean(v);
            return c == 0 ? "0" : c.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string MoneyCompact(double v)
        {
            return "$" + v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string FormatDollarPerM(double v)
        {
            double c = RoundRateClean(v);
            if (c == 0) return "$0.00";
            if (c >= 0.1 && Math.Abs(c * 100.0 - Math.Round(c * 100.0)) < 1e-6)
                return "$" + c.ToString("0.00", CultureInfo.InvariantCulture);
            return "$" + c.ToString("0.00####", CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, RawRateEntry> GetBuiltinReferenceMap()
        {
            if (_builtinReferenceMap != null) return _builtinReferenceMap;
            Dictionary<string, RawRateEntry> m = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
            Action<string, double, double, double, double> reg = delegate(string id, double i, double o, double r, double w)
            {
                m[id] = new RawRateEntry(i, o, r, w);
            };
            reg("claude-opus-4-6", 5.0, 25.0, 0.5, 6.25); reg("claude-opus-4.6", 5.0, 25.0, 0.5, 6.25);
            reg("claude-opus-4-5", 5.0, 25.0, 0.5, 6.25); reg("claude-opus-4.5", 5.0, 25.0, 0.5, 6.25);
            reg("claude-opus-4-1", 15.0, 75.0, 1.5, 18.75); reg("claude-opus-4.1", 15.0, 75.0, 1.5, 18.75);
            reg("claude-opus-4", 15.0, 75.0, 1.5, 18.75);
            reg("claude-sonnet-4-6", 3.0, 15.0, 0.3, 3.75); reg("claude-sonnet-4.6", 3.0, 15.0, 0.3, 3.75);
            reg("claude-sonnet-4-5", 3.0, 15.0, 0.3, 3.75); reg("claude-sonnet-4.5", 3.0, 15.0, 0.3, 3.75);
            reg("claude-sonnet-4", 3.0, 15.0, 0.3, 3.75);
            reg("claude-3-7-sonnet", 3.0, 15.0, 0.3, 3.75); reg("claude-3.7-sonnet", 3.0, 15.0, 0.3, 3.75);
            reg("claude-3-5-sonnet", 3.0, 15.0, 0.3, 3.75); reg("claude-3.5-sonnet", 3.0, 15.0, 0.3, 3.75);
            reg("claude-haiku-4-5", 1.0, 5.0, 0.1, 1.25); reg("claude-haiku-4.5", 1.0, 5.0, 0.1, 1.25);
            reg("claude-3-5-haiku", 0.8, 4.0, 0.08, 1.0); reg("claude-3.5-haiku", 0.8, 4.0, 0.08, 1.0);
            reg("claude-3-opus", 15.0, 75.0, 1.5, 18.75); reg("claude-3-haiku", 0.25, 1.25, 0.03, 0.3);
            reg("gpt-5.4-pro", 15.0, 60.0, 1.5, 0.0); reg("gpt-5.4", 2.5, 15.0, 0.25, 0.0);
            reg("gpt-5.4-mini", 0.4, 1.6, 0.04, 0.0); reg("gpt-5.4-nano", 0.1, 0.4, 0.01, 0.0);
            reg("gpt-5.3-codex", 1.75, 14.0, 0.175, 0.0); reg("gpt-5.2-codex", 1.75, 14.0, 0.175, 0.0);
            reg("gpt-5.2", 1.75, 14.0, 0.175, 0.0); reg("gpt-5.1-codex-max", 1.25, 10.0, 0.125, 0.0);
            reg("gpt-5.1-codex", 1.25, 10.0, 0.125, 0.0); reg("gpt-5.1-codex-mini", 0.25, 2.0, 0.025, 0.0);
            reg("gpt-5.1", 1.25, 10.0, 0.125, 0.0); reg("gpt-5-codex", 1.25, 10.0, 0.125, 0.0);
            reg("gpt-5", 1.25, 10.0, 0.125, 0.0); reg("gpt-5-mini", 0.25, 2.0, 0.025, 0.0);
            reg("gpt-5-nano", 0.05, 0.4, 0.005, 0.0); reg("codex-mini", 1.5, 6.0, 0.375, 0.0);
            reg("gpt-4.1", 2.0, 8.0, 0.5, 0.0); reg("gpt-4.1-mini", 0.4, 1.6, 0.1, 0.0);
            reg("gpt-4.1-nano", 0.1, 0.4, 0.025, 0.0); reg("gpt-4o", 2.5, 10.0, 1.25, 0.0);
            reg("gpt-4o-mini", 0.15, 0.6, 0.075, 0.0); reg("o3-pro", 20.0, 80.0, 5.0, 0.0);
            reg("o3", 2.0, 8.0, 0.5, 0.0); reg("o3-mini", 1.1, 4.4, 0.275, 0.0);
            reg("o4-mini", 1.1, 4.4, 0.275, 0.0); reg("o1", 15.0, 60.0, 7.5, 0.0);
            reg("o1-mini", 1.1, 4.4, 0.55, 0.0);
            reg("gemini-3.1-pro", 2.0, 12.0, 0.2, 0.0); reg("gemini-3.1-flash", 0.5, 3.0, 0.05, 0.0);
            reg("gemini-3.1-flash-lite", 0.1, 0.4, 0.025, 0.0); reg("gemini-3-pro", 2.0, 12.0, 0.2, 0.0);
            reg("gemini-3-flash", 0.5, 3.0, 0.05, 0.0); reg("gemini-2.5-pro", 1.25, 10.0, 0.125, 0.0);
            reg("gemini-2.5-flash", 0.3, 2.5, 0.03, 0.0); reg("gemini-2.5-flash-lite", 0.1, 0.4, 0.025, 0.0);
            reg("gemini-2.0-flash", 0.1, 0.4, 0.025, 0.0); reg("gemini-2.0-flash-lite", 0.075, 0.3, 0.01875, 0.0);
            reg("glm-5", 1.0, 3.2, 0.2, 0.0); reg("glm-4.7", 0.6, 2.2, 0.11, 0.0);
            reg("glm-4.6", 0.6, 2.2, 0.11, 0.0); reg("glm-4.5", 0.6, 2.2, 0.11, 0.0);
            reg("glm-4.5-air", 0.2, 1.1, 0.03, 0.0); reg("glm-4.5-flash", 0.1, 0.3, 0.01, 0.0);
            reg("kimi-k2.5", 0.6, 3.0, 0.1, 0.0); reg("kimi-k2-thinking", 0.6, 2.5, 0.15, 0.0);
            reg("kimi-k2-turbo", 0.6, 2.5, 0.15, 0.0); reg("kimi-k2", 0.6, 2.5, 0.15, 0.0);
            reg("kimi-for-coding", 0.95, 4.0, 0.14, 0.0);
            reg("deepseek-r1", 0.55, 2.19, 0.14, 0.0); reg("deepseek-reasoner", 0.55, 2.19, 0.14, 0.0);
            reg("deepseek-v3.2", 0.28, 0.42, 0.028, 0.0); reg("deepseek-v3.1", 0.55, 1.67, 0.055, 0.0);
            reg("deepseek-v3", 0.27, 1.1, 0.07, 0.0); reg("deepseek-chat", 0.27, 1.1, 0.07, 0.0);
            reg("minimax-m2.5", 0.3, 1.2, 0.03, 0.375); reg("minimax-m2.5-highspeed", 0.3, 1.2, 0.03, 0.375);
            reg("minimax-m2.1", 0.3, 1.2, 0.03, 0.375); reg("minimax-m2", 0.3, 1.2, 0.03, 0.375);
            reg("minimax-m1", 0.4, 2.2, 0.04, 0.0);
            reg("qwen3.5-plus", 0.4, 1.2, 0.04, 0.0); reg("qwen3.5-coder", 1.0, 4.0, 0.1, 0.0);
            reg("qwen3-coder-plus", 1.0, 5.0, 0.1, 0.0); reg("qwen3-coder", 0.3, 1.2, 0.03, 0.0);
            reg("qwen3-coder-flash", 0.15, 0.6, 0.015, 0.0); reg("qwen3-max", 1.2, 6.0, 0.12, 0.0);
            reg("qwen3-235b-a22b", 0.3, 1.2, 0.03, 0.0); reg("qwen3-32b", 0.15, 0.6, 0.015, 0.0);
            reg("qwq-32b", 0.15, 0.6, 0.015, 0.0); reg("qwen-max", 1.6, 6.4, 0.16, 0.0);
            reg("qwen-plus", 0.4, 1.2, 0.04, 0.0);
            reg("doubao-seed-code", 0.15, 0.6, 0.03, 0.0); reg("doubao-1.5-pro", 0.15, 0.6, 0.03, 0.0);
            reg("codestral", 0.3, 0.9, 0.03, 0.0); reg("devstral", 0.1, 0.3, 0.01, 0.0);
            reg("mistral-large", 2.0, 6.0, 0.2, 0.0);
            reg("grok-4", 3.0, 15.0, 0.75, 0.0); reg("grok-3", 3.0, 15.0, 0.75, 0.0);
            reg("grok-code-fast", 0.2, 1.5, 0.02, 0.0);
            _builtinReferenceMap = m;
            return _builtinReferenceMap;
        }
    }
}
