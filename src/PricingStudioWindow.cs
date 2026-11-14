using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Shapes = System.Windows.Shapes;

namespace TokLens
{
    public sealed class PricingStudioWindow : Window
    {
        public sealed class PricingStudioRow
        {
            public string ModelId { get; set; }
            public string NormId { get; set; }
            public bool IsActiveUsed { get; set; }
            public Visibility ActiveBadgeVisibility { get; set; }
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
            public string InputText { get; set; }
            public double OutputPerM { get; set; }
            public string OutputText { get; set; }
            public double CacheReadPerM { get; set; }
            public string CacheReadText { get; set; }
            public double CacheWritePerM { get; set; }
            public string CacheWriteText { get; set; }

            public bool HasOfficialRate { get; set; }
            public double OfficialInput { get; set; }
            public double OfficialOutput { get; set; }
            public double OfficialCacheRead { get; set; }
            public double OfficialCacheWrite { get; set; }
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
            // Anthropic Claude
            "claude-opus-4-6",
            "claude-sonnet-4-6",
            "claude-opus-4-5",
            "claude-sonnet-4-5",
            "claude-haiku-4-5",
            "claude-opus-4-1",
            "claude-opus-4",
            "claude-sonnet-4",
            "claude-3-7-sonnet",
            "claude-3-5-sonnet",
            "claude-3-5-haiku",
            "claude-3-opus",
            "claude-3-haiku",

            // OpenAI GPT / Codex / Reasoning
            "gpt-5.4-pro",
            "gpt-5.4",
            "gpt-5.4-mini",
            "gpt-5.4-nano",
            "gpt-5.3-codex",
            "gpt-5.2-codex",
            "gpt-5.2",
            "gpt-5.1-codex-max",
            "gpt-5.1-codex",
            "gpt-5.1-codex-mini",
            "gpt-5.1",
            "gpt-5-codex",
            "gpt-5",
            "gpt-5-mini",
            "gpt-5-nano",
            "codex-mini-latest",
            "gpt-4.1",
            "gpt-4.1-mini",
            "gpt-4.1-nano",
            "gpt-4o",
            "gpt-4o-mini",
            "o3-pro",
            "o3",
            "o3-mini",
            "o4-mini",
            "o1",
            "o1-mini",

            // Google Gemini
            "gemini-3.1-pro",
            "gemini-3.1-flash",
            "gemini-3.1-flash-lite",
            "gemini-3-pro",
            "gemini-3-flash",
            "gemini-2.5-pro",
            "gemini-2.5-flash",
            "gemini-2.5-flash-lite",
            "gemini-2.0-flash",
            "gemini-2.0-flash-lite",

            // DeepSeek / Kimi / Qwen / GLM / MiniMax / Doubao / Mistral / xAI
            "deepseek-r1",
            "deepseek-reasoner",
            "deepseek-v3.2",
            "deepseek-v3.1",
            "deepseek-v3",
            "deepseek-chat",
            "kimi-k2.5",
            "kimi-k2-thinking",
            "kimi-k2-turbo",
            "kimi-k2",
            "kimi-for-coding",
            "qwen3.5-plus",
            "qwen3.5-coder",
            "qwen3-coder-plus",
            "qwen3-coder",
            "qwen3-coder-flash",
            "qwen3-max",
            "qwen3-235b-a22b",
            "qwen3-32b",
            "qwq-32b",
            "qwen-max",
            "qwen-plus",
            "glm-5",
            "glm-4.7",
            "glm-4.6",
            "glm-4.5",
            "glm-4.5-air",
            "glm-4.5-flash",
            "minimax-m2.5",
            "minimax-m2.5-highspeed",
            "minimax-m2.1",
            "minimax-m2",
            "minimax-m1",
            "doubao-seed-code",
            "doubao-1.5-pro",
            "codestral",
            "devstral",
            "mistral-large",
            "grok-4",
            "grok-3",
            "grok-code-fast"
        };

        private static Dictionary<string, RawRateEntry> _builtinReferenceMap;
        private static readonly object BuiltinMapLock = new object();

        private readonly string _homeDir;
        private readonly Action _onPricesUpdated;
        private bool _isDark;

        private readonly List<string> _activeModelList = new List<string>();
        private readonly HashSet<string> _activeRawSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeNormSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, RawRateEntry> _cloudRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, RawRateEntry> _customRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _customNormToRawKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string _cloudSourceSummary = "";
        private string _cloudUpdatedAt = "";
        private bool _isSyncingCloud = false;

        private List<PricingStudioRow> _allRows = new List<PricingStudioRow>();
        private int _activeFilterIndex = 0; // 0 = 全部, 1 = 当前已用, 2 = 自定义覆盖, 3 = 云端三源
        private bool _suppressSelectionChanged = false;
        private bool _editorTouchedManually = false;
        private PricingStudioRow _selectedRow = null;

        // UI Elements
        private Border _rootShellBorder;
        private TextBlock _headerSummaryBadgeText;
        private Shapes.Ellipse _headerSummaryDot;
        private Button _syncCloudBtn;

        private TextBox _searchBox;
        private TextBlock _searchPlaceholder;
        private Button _clearSearchBtn;
        private Button[] _filterBtns;
        private TextBlock _matchCountText;

        private DataGrid _grid;
        private Border _emptyOverlay;
        private TextBlock _emptyTitleText;
        private TextBlock _emptySubText;
        private Button _emptyFillEditorBtn;

        private TextBlock _officialHintText;
        private Button _restoreOfficialValBtn;
        private TextBox _modelIdInput;
        private TextBox _inputRateBox;
        private TextBox _outputRateBox;
        private TextBox _cacheReadRateBox;
        private TextBox _cacheWriteRateBox;
        private Button _deleteRuleBtn;
        private Button _saveRuleBtn;

        private Shapes.Ellipse _statusDot;
        private TextBlock _statusText;
        private TextBlock _footerRightMetaText;

        public PricingStudioWindow(
            Window owner,
            string homeDir,
            bool isDark,
            IEnumerable<string> activeModels,
            Action onPricesUpdated)
        {
            _homeDir = string.IsNullOrWhiteSpace(homeDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDir;
            _isDark = isDark;
            _onPricesUpdated = onPricesUpdated;

            if (activeModels != null)
            {
                foreach (string raw in activeModels)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string trimmed = raw.Trim();
                    if (_activeRawSet.Add(trimmed))
                        _activeModelList.Add(trimmed);
                    string norm = PricingEngine.NormalizeModelId(trimmed, "");
                    if (!string.IsNullOrEmpty(norm))
                        _activeNormSet.Add(norm);
                }
            }

            if (owner != null)
            {
                try { Icon = owner.Icon; } catch { }
                if (owner.IsVisible || owner.IsLoaded)
                {
                    try { Owner = owner; } catch { }
                }
            }

            Title = "计价规则 — " + AppConstants.ProductTitle;
            Width = 940;
            Height = 620;
            MinWidth = 700;
            MinHeight = 480;
            WindowStartupLocation = (Owner != null) ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            FontFamily = DesignSystem.FontUi;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            DesignSystem.ApplyTheme(this, _isDark);
            SetResourceReference(Window.BackgroundProperty, "BgWindow");
            SetResourceReference(Window.ForegroundProperty, "FgPrimary");

            Button maxBtn;
            Shapes.Path maxIconPath;
            Content = BuildWindowShell(out maxBtn, out maxIconPath);

            ModernWindowChrome.Attach(
                this,
                _rootShellBorder,
                maxBtn,
                maxIconPath,
                44.0,
                delegate() { return _isDark; });

            PreviewKeyDown += OnWindowPreviewKeyDown;

            ReloadCatalogAndBind(null, true);
        }

        // -------------------- Window Shell & Layout --------------------

        private UIElement BuildWindowShell(out Button maxBtnOut, out Shapes.Path maxPathOut)
        {
            _rootShellBorder = new Border();
            _rootShellBorder.BorderThickness = new Thickness(1);
            _rootShellBorder.SnapsToDevicePixels = true;
            _rootShellBorder.SetResourceReference(Border.BackgroundProperty, "BgWindow");
            _rootShellBorder.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            UIElement titleBar = BuildTitleBar(out maxBtnOut, out maxPathOut);
            Grid.SetRow(titleBar, 0);
            mainGrid.Children.Add(titleBar);

            UIElement workspace = BuildWorkspaceBody();
            Grid.SetRow(workspace, 1);
            mainGrid.Children.Add(workspace);

            UIElement statusBar = BuildStatusBar();
            Grid.SetRow(statusBar, 2);
            mainGrid.Children.Add(statusBar);

            _rootShellBorder.Child = mainGrid;
            return _rootShellBorder;
        }

        private UIElement BuildTitleBar(out Button maxBtnOut, out Shapes.Path maxPathOut)
        {
            Border bar = new Border();
            bar.Height = 44;
            bar.Padding = new Thickness(14, 0, 6, 0);
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: Brand Icon + Studio Title + Count Badge
            StackPanel leftSp = new StackPanel();
            leftSp.Orientation = Orientation.Horizontal;
            leftSp.VerticalAlignment = VerticalAlignment.Center;

            Border iconBadge = new Border();
            iconBadge.Width = 24;
            iconBadge.Height = 24;
            iconBadge.CornerRadius = new CornerRadius(6);
            iconBadge.BorderThickness = new Thickness(1);
            iconBadge.Margin = new Thickness(0, 0, 10, 0);
            iconBadge.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            iconBadge.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");
            iconBadge.Child = UiIcons.CreateIcon(UiIcons.Models, 13, "AccentText");
            leftSp.Children.Add(iconBadge);

            TextBlock titleTb = new TextBlock();
            titleTb.Text = "计价规则";
            titleTb.FontSize = DesignSystem.TextLabel;
            titleTb.FontWeight = FontWeights.SemiBold;
            titleTb.VerticalAlignment = VerticalAlignment.Center;
            titleTb.Margin = new Thickness(0, 0, 10, 0);
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            leftSp.Children.Add(titleTb);

            Border summaryBadge = ModernControlsFactory.CreateStatusBadge(
                "加载中...",
                "accent",
                out _headerSummaryBadgeText,
                out _headerSummaryDot);
            ModernControlsFactory.EnableTabularNumbers(_headerSummaryBadgeText, false);
            leftSp.Children.Add(summaryBadge);

            Grid.SetColumn(leftSp, 0);
            g.Children.Add(leftSp);

            // Right: Cloud Sync Button + Window Caption Buttons
            StackPanel rightSp = new StackPanel();
            rightSp.Orientation = Orientation.Horizontal;
            rightSp.VerticalAlignment = VerticalAlignment.Center;

            _syncCloudBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "同步云端三源", "ghost");
            _syncCloudBtn.Height = 26;
            _syncCloudBtn.Padding = new Thickness(8, 0, 8, 0);
            _syncCloudBtn.Margin = new Thickness(0, 0, 6, 0);
            _syncCloudBtn.ToolTip = "立即从 models.dev + LiteLLM + OpenRouter 聚合拉取最新官方模型单价";
            _syncCloudBtn.Click += OnSyncCloudPricesClick;
            WindowChrome.SetIsHitTestVisibleInChrome(_syncCloudBtn, true);
            rightSp.Children.Add(_syncCloudBtn);

            StackPanel capBtns = ModernWindowChrome.CreateCaptionButtons(this, true, out maxBtnOut, out maxPathOut);
            rightSp.Children.Add(capBtns);

            Grid.SetColumn(rightSp, 2);
            g.Children.Add(rightSp);

            bar.Child = g;
            return bar;
        }

        private UIElement BuildWorkspaceBody()
        {
            Grid body = new Grid();
            body.Margin = new Thickness(16, 12, 16, 10);
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement filterRow = BuildSearchAndFilterToolbar();
            Grid.SetRow(filterRow, 0);
            body.Children.Add(filterRow);

            UIElement tableCard = BuildCatalogTableCard();
            Grid.SetRow(tableCard, 1);
            body.Children.Add(tableCard);

            UIElement editorCard = BuildOverrideEditorCard();
            Grid.SetRow(editorCard, 2);
            body.Children.Add(editorCard);

            return body;
        }

        private UIElement BuildSearchAndFilterToolbar()
        {
            Grid bar = new Grid();
            bar.Margin = new Thickness(0, 0, 0, 10);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(246) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Col 0: Instant Search Box with Icon & Clear Button
            Border searchShell = new Border();
            searchShell.Height = 28;
            searchShell.CornerRadius = new CornerRadius(6);
            searchShell.BorderThickness = new Thickness(1);
            searchShell.Padding = new Thickness(8, 0, 4, 0);
            searchShell.SnapsToDevicePixels = true;
            searchShell.SetResourceReference(Border.BackgroundProperty, "BgInput");
            searchShell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            Grid sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Shapes.Path sIcon = UiIcons.CreateIcon(UiIcons.Search, 13, "FgMuted");
            sIcon.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(sIcon, 0);
            sg.Children.Add(sIcon);

            Grid inputHost = new Grid();
            _searchPlaceholder = new TextBlock();
            _searchPlaceholder.Text = "搜索模型 ID 或厂商 (Ctrl+F)...";
            _searchPlaceholder.FontSize = DesignSystem.TextBody;
            _searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _searchPlaceholder.IsHitTestVisible = false;
            _searchPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            inputHost.Children.Add(_searchPlaceholder);

            _searchBox = new TextBox();
            _searchBox.FontFamily = DesignSystem.FontUi;
            _searchBox.FontSize = DesignSystem.TextBody;
            _searchBox.BorderThickness = new Thickness(0);
            _searchBox.Background = Brushes.Transparent;
            _searchBox.Padding = new Thickness(0);
            _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            _searchBox.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
            _searchBox.SetResourceReference(TextBox.CaretBrushProperty, "AccentPrimary");
            _searchBox.GotKeyboardFocus += delegate(object s, KeyboardFocusChangedEventArgs e)
            {
                searchShell.SetResourceReference(Border.BorderBrushProperty, "BorderFocus");
            };
            _searchBox.LostKeyboardFocus += delegate(object s, KeyboardFocusChangedEventArgs e)
            {
                searchShell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            };
            _searchBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                bool hasText = !string.IsNullOrEmpty(_searchBox.Text);
                _searchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
                _clearSearchBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
                ApplyFiltersAndRefreshGrid(null);
            };
            inputHost.Children.Add(_searchBox);

            Grid.SetColumn(inputHost, 1);
            sg.Children.Add(inputHost);

            _clearSearchBtn = ModernControlsFactory.CreateIconGhostButton("M 3,3 L 13,13 M 13,3 L 3,13", "清空搜索");
            _clearSearchBtn.Width = 20;
            _clearSearchBtn.Height = 20;
            _clearSearchBtn.Visibility = Visibility.Collapsed;
            _clearSearchBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _searchBox.Text = "";
                _searchBox.Focus();
            };
            Grid.SetColumn(_clearSearchBtn, 2);
            sg.Children.Add(_clearSearchBtn);

            searchShell.Child = sg;
            Grid.SetColumn(searchShell, 0);
            bar.Children.Add(searchShell);

            // Col 2: Source Segmented Filter Pills
            Border segContainer = ModernControlsFactory.CreateSegmentedContainer();
            StackPanel segSp = (StackPanel)segContainer.Child;
            string[] filterLabels = new string[] { "全部", "当前已用", "自定义覆盖", "云端三源" };
            _filterBtns = new Button[filterLabels.Length];
            for (int i = 0; i < filterLabels.Length; i++)
            {
                int idx = i;
                Button btn = ModernControlsFactory.CreateSegmentedItem(filterLabels[i]);
                btn.Click += delegate(object s, RoutedEventArgs e)
                {
                    _activeFilterIndex = idx;
                    ModernControlsFactory.UpdateSegmentedGroup(_filterBtns, _activeFilterIndex);
                    ApplyFiltersAndRefreshGrid(null);
                };
                _filterBtns[i] = btn;
                segSp.Children.Add(btn);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_filterBtns, _activeFilterIndex);
            Grid.SetColumn(segContainer, 2);
            bar.Children.Add(segContainer);

            // Col 3: Right Match Counter
            _matchCountText = new TextBlock();
            _matchCountText.Text = "";
            _matchCountText.FontSize = DesignSystem.TextCaption;
            _matchCountText.HorizontalAlignment = HorizontalAlignment.Right;
            _matchCountText.VerticalAlignment = VerticalAlignment.Center;
            _matchCountText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(_matchCountText, true);
            Grid.SetColumn(_matchCountText, 3);
            bar.Children.Add(_matchCountText);

            return bar;
        }

        private UIElement BuildCatalogTableCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(0));
            card.ClipToBounds = true;

            Grid host = new Grid();

            _grid = ModernDataGridFactory.CreateModernDataGrid();
            _grid.RowHeight = 32;
            _grid.ColumnHeaderHeight = 30;

            _grid.Columns.Add(BuildModelIdColumn());
            _grid.Columns.Add(BuildSourceBadgeColumn());
            _grid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _grid, "输入 ($/1M)", "InputText", "InputPerM",
                new DataGridLength(98), true, true, true));
            _grid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _grid, "输出 ($/1M)", "OutputText", "OutputPerM",
                new DataGridLength(98), true, true, true));
            _grid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _grid, "缓存读 ($/1M)", "CacheReadText", "CacheReadPerM",
                new DataGridLength(108), true, true, false));
            _grid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _grid, "缓存写 ($/1M)", "CacheWriteText", "CacheWritePerM",
                new DataGridLength(108), true, true, false));

            _grid.SelectionChanged += OnGridSelectionChanged;
            host.Children.Add(_grid);

            // Empty search state overlay with quick-create shortcut
            _emptyOverlay = new Border();
            _emptyOverlay.Visibility = Visibility.Collapsed;
            _emptyOverlay.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyOverlay.VerticalAlignment = VerticalAlignment.Center;
            _emptyOverlay.Padding = new Thickness(20, 16, 20, 16);

            StackPanel emptySp = new StackPanel();
            emptySp.HorizontalAlignment = HorizontalAlignment.Center;

            _emptyTitleText = new TextBlock();
            _emptyTitleText.Text = "未找到匹配的模型";
            _emptyTitleText.FontSize = DesignSystem.TextLabel;
            _emptyTitleText.FontWeight = FontWeights.Medium;
            _emptyTitleText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyTitleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            emptySp.Children.Add(_emptyTitleText);

            _emptySubText = new TextBlock();
            _emptySubText.Text = "可切换上方筛选条件，或直接为任意自部署/私有模型创建自定义单价";
            _emptySubText.FontSize = DesignSystem.TextBody;
            _emptySubText.Margin = new Thickness(0, 4, 0, 10);
            _emptySubText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptySubText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            emptySp.Children.Add(_emptySubText);

            _emptyFillEditorBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Plus, "填入下方编辑器新建单价", "secondary");
            _emptyFillEditorBtn.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyFillEditorBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                string kw = (_searchBox != null && _searchBox.Text != null) ? _searchBox.Text.Trim() : "";
                if (!string.IsNullOrEmpty(kw))
                {
                    _selectedRow = null;
                    _modelIdInput.Text = kw;
                    _inputRateBox.Text = "0";
                    _outputRateBox.Text = "0";
                    _cacheReadRateBox.Text = "0";
                    _cacheWriteRateBox.Text = "0";
                    _editorTouchedManually = true;
                    UpdateEditorActionState();
                    _inputRateBox.Focus();
                    _inputRateBox.SelectAll();
                    SetStatus("已将新模型 \"" + kw + "\" 填入编辑器，设置四维单价后点击「保存覆盖规则」即可生效", "neutral");
                }
            };
            emptySp.Children.Add(_emptyFillEditorBtn);

            _emptyOverlay.Child = emptySp;
            host.Children.Add(_emptyOverlay);

            card.Child = host;
            return card;
        }

        private DataGridTemplateColumn BuildModelIdColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "模型 ID";
            col.SortMemberPath = "ModelId";
            col.Width = new DataGridLength(1.85, DataGridLengthUnitType.Star);

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            // Left provider color dot
            FrameworkElementFactory dot = new FrameworkElementFactory(typeof(Shapes.Ellipse));
            dot.SetValue(DockPanel.DockProperty, Dock.Left);
            dot.SetValue(FrameworkElement.WidthProperty, 6.0);
            dot.SetValue(FrameworkElement.HeightProperty, 6.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetBinding(Shapes.Shape.FillProperty, new Binding("ProviderDotBrush"));
            dock.AppendChild(dot);

            // Optional "已用" micro pill on the right when model is active in scanned logs
            FrameworkElementFactory usedPill = new FrameworkElementFactory(typeof(Border));
            usedPill.SetValue(DockPanel.DockProperty, Dock.Right);
            usedPill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            usedPill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            usedPill.SetValue(Border.PaddingProperty, new Thickness(5, 0, 5, 0));
            usedPill.SetValue(FrameworkElement.HeightProperty, 18.0);
            usedPill.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            usedPill.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            usedPill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("SemanticSuccessBg"));
            usedPill.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticSuccessBorder"));
            usedPill.SetBinding(UIElement.VisibilityProperty, new Binding("ActiveBadgeVisibility"));

            FrameworkElementFactory usedTxt = new FrameworkElementFactory(typeof(TextBlock));
            usedTxt.SetValue(TextBlock.TextProperty, "已用");
            usedTxt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            usedTxt.SetValue(TextBlock.FontSizeProperty, 11.0);
            usedTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            usedTxt.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            usedTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"));
            usedPill.AppendChild(usedTxt);
            dock.AppendChild(usedPill);

            // Monospace Model ID
            FrameworkElementFactory idTxt = new FrameworkElementFactory(typeof(TextBlock));
            idTxt.SetBinding(TextBlock.TextProperty, new Binding("ModelId"));
            idTxt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            idTxt.SetValue(TextBlock.FontSizeProperty, 12.0);
            idTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            idTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            idTxt.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            idTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgPrimary"));
            dock.AppendChild(idTxt);

            dt.VisualTree = dock;
            col.CellTemplate = dt;
            return col;
        }

        private DataGridTemplateColumn BuildSourceBadgeColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "来源";
            col.SortMemberPath = "SourceRank";
            col.Width = new DataGridLength(96);

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border));
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            pill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            pill.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1));
            pill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            pill.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            pill.SetBinding(Border.BackgroundProperty, new Binding("SourceBgBrush"));
            pill.SetBinding(Border.BorderBrushProperty, new Binding("SourceBorderBrush"));

            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new Binding("SourceBadgeText"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            txt.SetValue(TextBlock.FontSizeProperty, 11.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetBinding(TextBlock.ForegroundProperty, new Binding("SourceFgBrush"));

            pill.AppendChild(txt);
            dt.VisualTree = pill;
            col.CellTemplate = dt;
            return col;
        }

        private UIElement BuildOverrideEditorCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(14, 10, 14, 10));
            card.Margin = new Thickness(0, 10, 0, 0);

            StackPanel root = new StackPanel();

            // Row 0: Editor Title + Official Reference Hint + Quick Multiplier Chips
            DockPanel hdr = new DockPanel();
            hdr.Margin = new Thickness(0, 0, 0, 8);

            StackPanel multiplierRow = new StackPanel();
            multiplierRow.Orientation = Orientation.Horizontal;
            multiplierRow.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(multiplierRow, Dock.Right);

            TextBlock quickLbl = new TextBlock();
            quickLbl.Text = "快捷倍率:";
            quickLbl.FontSize = DesignSystem.TextCaption;
            quickLbl.VerticalAlignment = VerticalAlignment.Center;
            quickLbl.Margin = new Thickness(0, 0, 6, 0);
            quickLbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            multiplierRow.Children.Add(quickLbl);

            Button halfBtn = MakeCompactChipButton("×0.5 五折", "按五折自动计算四维单价（适合半价 API 中转站或 Batch 批处理）");
            halfBtn.Margin = new Thickness(0, 0, 6, 0);
            halfBtn.Click += delegate(object s, RoutedEventArgs e) { ApplyQuickMultiplier(0.5, "×0.5 五折"); };
            multiplierRow.Children.Add(halfBtn);

            Button twentyPctBtn = MakeCompactChipButton("×0.2 二折", "按二折自动计算四维单价（适合高折扣聚合中转渠道）");
            twentyPctBtn.Margin = new Thickness(0, 0, 6, 0);
            twentyPctBtn.Click += delegate(object s, RoutedEventArgs e) { ApplyQuickMultiplier(0.2, "×0.2 二折"); };
            multiplierRow.Children.Add(twentyPctBtn);

            Button zeroFreeBtn = MakeCompactChipButton("设为 0 免费", "将四维单价全部设为 $0（适合 Ollama / vLLM 本地自部署或免费包月模型）");
            zeroFreeBtn.Margin = new Thickness(0, 0, 6, 0);
            zeroFreeBtn.Click += delegate(object s, RoutedEventArgs e) { ApplyZeroFreeRate(); };
            multiplierRow.Children.Add(zeroFreeBtn);

            _restoreOfficialValBtn = MakeCompactChipButton("填入官方价", "将输入框数值还原为该模型的云端/内置官方基准价");
            _restoreOfficialValBtn.IsEnabled = false;
            _restoreOfficialValBtn.Click += delegate(object s, RoutedEventArgs e) { FillOfficialBaselineToInputs(); };
            multiplierRow.Children.Add(_restoreOfficialValBtn);

            hdr.Children.Add(multiplierRow);

            DockPanel titleLeft = new DockPanel();
            titleLeft.VerticalAlignment = VerticalAlignment.Center;

            Shapes.Path editIcon = UiIcons.CreateIcon(UiIcons.Settings, 13, "AccentText");
            editIcon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(editIcon, Dock.Left);
            titleLeft.Children.Add(editIcon);

            TextBlock editorTitle = new TextBlock();
            editorTitle.Text = "自定义单价覆盖编辑器";
            editorTitle.FontSize = DesignSystem.TextBody;
            editorTitle.FontWeight = FontWeights.SemiBold;
            editorTitle.VerticalAlignment = VerticalAlignment.Center;
            editorTitle.Margin = new Thickness(0, 0, 10, 0);
            editorTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            DockPanel.SetDock(editorTitle, Dock.Left);
            titleLeft.Children.Add(editorTitle);

            _officialHintText = new TextBlock();
            _officialHintText.Text = "单击上方任意模型行即可载入四维单价";
            _officialHintText.FontSize = DesignSystem.TextCaption;
            _officialHintText.VerticalAlignment = VerticalAlignment.Center;
            _officialHintText.TextTrimming = TextTrimming.CharacterEllipsis;
            _officialHintText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ModernControlsFactory.EnableTabularNumbers(_officialHintText, true);
            titleLeft.Children.Add(_officialHintText);

            hdr.Children.Add(titleLeft);
            root.Children.Add(hdr);

            // Row 1: 5-Column Form Fields (Model ID + Input + Output + CacheRead + CacheWrite)
            Grid formGrid = new Grid();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.85, GridUnitType.Star) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            _modelIdInput = CreateMonoEditorField(formGrid, 0, "模型 ID（支持新建自部署模型名）", "");
            _inputRateBox = CreateMonoEditorField(formGrid, 2, "输入 ($ / 1M)", "0");
            _outputRateBox = CreateMonoEditorField(formGrid, 4, "输出 ($ / 1M)", "0");
            _cacheReadRateBox = CreateMonoEditorField(formGrid, 6, "缓存读取 ($ / 1M)", "0");
            _cacheWriteRateBox = CreateMonoEditorField(formGrid, 8, "缓存写入 ($ / 1M)", "0");

            _modelIdInput.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (_suppressSelectionChanged) return;
                UpdateEditorActionState();
            };

            TextChangedEventHandler onRateTyped = delegate(object s, TextChangedEventArgs e)
            {
                if (_suppressSelectionChanged) return;
                _editorTouchedManually = true;
            };
            _inputRateBox.TextChanged += onRateTyped;
            _outputRateBox.TextChanged += onRateTyped;
            _cacheReadRateBox.TextChanged += onRateTyped;
            _cacheWriteRateBox.TextChanged += onRateTyped;

            KeyEventHandler onEnterSave = delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    OnSaveOverrideClick(s, new RoutedEventArgs());
                }
            };
            _modelIdInput.KeyDown += onEnterSave;
            _inputRateBox.KeyDown += onEnterSave;
            _outputRateBox.KeyDown += onEnterSave;
            _cacheReadRateBox.KeyDown += onEnterSave;
            _cacheWriteRateBox.KeyDown += onEnterSave;

            root.Children.Add(formGrid);

            // Row 2: Persistence Path Note + Delete & Save Actions
            DockPanel actionRow = new DockPanel();
            actionRow.Margin = new Thickness(0, 10, 0, 0);

            StackPanel actionBtns = new StackPanel();
            actionBtns.Orientation = Orientation.Horizontal;
            actionBtns.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(actionBtns, Dock.Right);

            _deleteRuleBtn = ModernControlsFactory.CreateIconTextButton(
                UiIcons.Trash,
                "删除该自定义规则恢复官方价",
                "danger");
            _deleteRuleBtn.Height = 28;
            _deleteRuleBtn.Margin = new Thickness(0, 0, 8, 0);
            _deleteRuleBtn.IsEnabled = false;
            _deleteRuleBtn.Click += OnDeleteOverrideClick;
            actionBtns.Children.Add(_deleteRuleBtn);

            _saveRuleBtn = ModernControlsFactory.CreateIconTextButton(
                UiIcons.Check,
                "保存覆盖规则",
                "primary");
            _saveRuleBtn.Height = 28;
            _saveRuleBtn.Padding = new Thickness(14, 0, 14, 0);
            _saveRuleBtn.Click += OnSaveOverrideClick;
            actionBtns.Children.Add(_saveRuleBtn);

            actionRow.Children.Add(actionBtns);

            TextBlock pathNote = new TextBlock();
            pathNote.Text = "持久化至 %APPDATA%\\TokLens\\custom_prices.json（若 ~/.toklens 同名配置存在将自动同步），保存后全局账单即时重算";
            pathNote.FontSize = DesignSystem.TextCaption;
            pathNote.VerticalAlignment = VerticalAlignment.Center;
            pathNote.Margin = new Thickness(0, 0, 12, 0);
            pathNote.TextTrimming = TextTrimming.CharacterEllipsis;
            pathNote.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            actionRow.Children.Add(pathNote);

            root.Children.Add(actionRow);

            card.Child = root;
            return card;
        }

        private TextBox CreateMonoEditorField(Grid parent, int colIndex, string labelText, string initialText)
        {
            StackPanel sp = new StackPanel();
            TextBlock lbl = new TextBlock();
            lbl.Text = labelText;
            lbl.FontSize = DesignSystem.TextCaption;
            lbl.FontWeight = FontWeights.Medium;
            lbl.Margin = new Thickness(0, 0, 0, 4);
            lbl.TextTrimming = TextTrimming.CharacterEllipsis;
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(lbl);

            TextBox tb = ModernControlsFactory.CreateTextInput(initialText, 30);
            tb.FontFamily = DesignSystem.FontMono;
            tb.FontSize = DesignSystem.TextBody;
            sp.Children.Add(tb);

            Grid.SetColumn(sp, colIndex);
            parent.Children.Add(sp);
            return tb;
        }

        private Button MakeCompactChipButton(string text, string tooltip)
        {
            Button btn = ModernControlsFactory.CreateButton(text, "secondary");
            btn.Height = 22;
            btn.FontSize = DesignSystem.TextCaption;
            btn.Padding = new Thickness(8, 0, 8, 0);
            btn.ToolTip = tooltip;
            return btn;
        }

        private UIElement BuildStatusBar()
        {
            Border bar = new Border();
            bar.Height = 30;
            bar.Padding = new Thickness(16, 0, 16, 0);
            bar.BorderThickness = new Thickness(0, 1, 0, 0);
            bar.SetResourceReference(Border.BackgroundProperty, "BgTitleBar");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel dock = new DockPanel();

            _footerRightMetaText = new TextBlock();
            _footerRightMetaText.Text = "Ctrl+F 搜索 · Enter 保存 · Esc 关闭";
            _footerRightMetaText.FontSize = DesignSystem.TextCaption;
            _footerRightMetaText.VerticalAlignment = VerticalAlignment.Center;
            _footerRightMetaText.Margin = new Thickness(12, 0, 0, 0);
            _footerRightMetaText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(_footerRightMetaText, Dock.Right);
            dock.Children.Add(_footerRightMetaText);

            DockPanel leftSp = new DockPanel();
            leftSp.VerticalAlignment = VerticalAlignment.Center;

            _statusDot = new Shapes.Ellipse();
            _statusDot.Width = 6;
            _statusDot.Height = 6;
            _statusDot.Margin = new Thickness(0, 0, 8, 0);
            _statusDot.VerticalAlignment = VerticalAlignment.Center;
            _statusDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticSuccess");
            DockPanel.SetDock(_statusDot, Dock.Left);
            leftSp.Children.Add(_statusDot);

            _statusText = new TextBlock();
            _statusText.Text = "已汇总三源云端价格库、内置模型与自定义单价规则";
            _statusText.FontSize = DesignSystem.TextCaption;
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            leftSp.Children.Add(_statusText);

            dock.Children.Add(leftSp);
            bar.Child = dock;
            return bar;
        }

        // -------------------- Catalog Loading & Merging Engine --------------------

        private static Dictionary<string, RawRateEntry> GetBuiltinReferenceMap()
        {
            lock (BuiltinMapLock)
            {
                if (_builtinReferenceMap != null)
                    return _builtinReferenceMap;

                Dictionary<string, RawRateEntry> m = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);

                // Anthropic Claude
                RegRef(m, "claude-opus-4-6", 5.0, 25.0, 0.5, 6.25);
                RegRef(m, "claude-opus-4.6", 5.0, 25.0, 0.5, 6.25);
                RegRef(m, "claude-opus-4-5", 5.0, 25.0, 0.5, 6.25);
                RegRef(m, "claude-opus-4.5", 5.0, 25.0, 0.5, 6.25);
                RegRef(m, "claude-opus-4-1", 15.0, 75.0, 1.5, 18.75);
                RegRef(m, "claude-opus-4.1", 15.0, 75.0, 1.5, 18.75);
                RegRef(m, "claude-opus-4", 15.0, 75.0, 1.5, 18.75);
                RegRef(m, "claude-sonnet-4-6", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-sonnet-4.6", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-sonnet-4-5", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-sonnet-4.5", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-sonnet-4", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-3-7-sonnet", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-3.7-sonnet", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-3-5-sonnet", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-3.5-sonnet", 3.0, 15.0, 0.3, 3.75);
                RegRef(m, "claude-haiku-4-5", 1.0, 5.0, 0.1, 1.25);
                RegRef(m, "claude-haiku-4.5", 1.0, 5.0, 0.1, 1.25);
                RegRef(m, "claude-3-5-haiku", 0.8, 4.0, 0.08, 1.0);
                RegRef(m, "claude-3.5-haiku", 0.8, 4.0, 0.08, 1.0);
                RegRef(m, "claude-3-opus", 15.0, 75.0, 1.5, 18.75);
                RegRef(m, "claude-3-haiku", 0.25, 1.25, 0.03, 0.3);

                // OpenAI / Codex
                RegRef(m, "gpt-5.4-pro", 15.0, 60.0, 1.5, 0.0);
                RegRef(m, "gpt-5.4", 2.5, 15.0, 0.25, 0.0);
                RegRef(m, "gpt-5.4-mini", 0.4, 1.6, 0.04, 0.0);
                RegRef(m, "gpt-5.4-nano", 0.1, 0.4, 0.01, 0.0);
                RegRef(m, "gpt-5.3-codex", 1.75, 14.0, 0.175, 0.0);
                RegRef(m, "gpt-5.2-codex", 1.75, 14.0, 0.175, 0.0);
                RegRef(m, "gpt-5.2", 1.75, 14.0, 0.175, 0.0);
                RegRef(m, "gpt-5.1-codex-max", 1.25, 10.0, 0.125, 0.0);
                RegRef(m, "gpt-5.1-codex", 1.25, 10.0, 0.125, 0.0);
                RegRef(m, "gpt-5.1-codex-mini", 0.25, 2.0, 0.025, 0.0);
                RegRef(m, "gpt-5.1", 1.25, 10.0, 0.125, 0.0);
                RegRef(m, "gpt-5-codex", 1.25, 10.0, 0.125, 0.0);
                RegRef(m, "gpt-5", 1.25, 10.0, 0.125, 0.0);
                RegRef(m, "gpt-5-mini", 0.25, 2.0, 0.025, 0.0);
                RegRef(m, "gpt-5-nano", 0.05, 0.4, 0.005, 0.0);
                RegRef(m, "codex-mini", 1.5, 6.0, 0.375, 0.0);
                RegRef(m, "codex-mini-latest", 1.5, 6.0, 0.375, 0.0);
                RegRef(m, "gpt-4.1", 2.0, 8.0, 0.5, 0.0);
                RegRef(m, "gpt-4.1-mini", 0.4, 1.6, 0.1, 0.0);
                RegRef(m, "gpt-4.1-nano", 0.1, 0.4, 0.025, 0.0);
                RegRef(m, "gpt-4o", 2.5, 10.0, 1.25, 0.0);
                RegRef(m, "gpt-4o-mini", 0.15, 0.6, 0.075, 0.0);
                RegRef(m, "o3-pro", 20.0, 80.0, 5.0, 0.0);
                RegRef(m, "o3", 2.0, 8.0, 0.5, 0.0);
                RegRef(m, "o3-mini", 1.1, 4.4, 0.275, 0.0);
                RegRef(m, "o4-mini", 1.1, 4.4, 0.275, 0.0);
                RegRef(m, "o1", 15.0, 60.0, 7.5, 0.0);
                RegRef(m, "o1-mini", 1.1, 4.4, 0.55, 0.0);

                // Google Gemini
                RegRef(m, "gemini-3.1-pro", 2.0, 12.0, 0.2, 0.0);
                RegRef(m, "gemini-3.1-flash", 0.5, 3.0, 0.05, 0.0);
                RegRef(m, "gemini-3.1-flash-lite", 0.1, 0.4, 0.025, 0.0);
                RegRef(m, "gemini-3-pro", 2.0, 12.0, 0.2, 0.0);
                RegRef(m, "gemini-3.0-pro", 2.0, 12.0, 0.2, 0.0);
                RegRef(m, "gemini-3-flash", 0.5, 3.0, 0.05, 0.0);
                RegRef(m, "gemini-3.0-flash", 0.5, 3.0, 0.05, 0.0);
                RegRef(m, "gemini-2.5-pro", 1.25, 10.0, 0.125, 0.0);
                RegRef(m, "gemini-2.5-flash", 0.3, 2.5, 0.03, 0.0);
                RegRef(m, "gemini-2.5-flash-lite", 0.1, 0.4, 0.025, 0.0);
                RegRef(m, "gemini-2.0-flash", 0.1, 0.4, 0.025, 0.0);
                RegRef(m, "gemini-2.0-flash-lite", 0.075, 0.3, 0.01875, 0.0);

                // DeepSeek / Kimi / Qwen / GLM / MiniMax / Doubao / Mistral / xAI
                RegRef(m, "glm-5", 1.0, 3.2, 0.2, 0.0);
                RegRef(m, "glm-4.7", 0.6, 2.2, 0.11, 0.0);
                RegRef(m, "glm-4.6", 0.6, 2.2, 0.11, 0.0);
                RegRef(m, "glm-4.5", 0.6, 2.2, 0.11, 0.0);
                RegRef(m, "glm-4.5-air", 0.2, 1.1, 0.03, 0.0);
                RegRef(m, "glm-4.5-flash", 0.1, 0.3, 0.01, 0.0);
                RegRef(m, "kimi-k2.5", 0.6, 3.0, 0.1, 0.0);
                RegRef(m, "kimi-k2-thinking", 0.6, 2.5, 0.15, 0.0);
                RegRef(m, "kimi-k2-turbo", 0.6, 2.5, 0.15, 0.0);
                RegRef(m, "kimi-k2", 0.6, 2.5, 0.15, 0.0);
                RegRef(m, "kimi-for-coding", 0.95, 4.0, 0.14, 0.0);
                RegRef(m, "deepseek-r1", 0.55, 2.19, 0.14, 0.0);
                RegRef(m, "deepseek-reasoner", 0.55, 2.19, 0.14, 0.0);
                RegRef(m, "deepseek-v3.2", 0.28, 0.42, 0.028, 0.0);
                RegRef(m, "deepseek-v3.1", 0.55, 1.67, 0.055, 0.0);
                RegRef(m, "deepseek-v3", 0.27, 1.1, 0.07, 0.0);
                RegRef(m, "deepseek-chat", 0.27, 1.1, 0.07, 0.0);
                RegRef(m, "minimax-m2.5", 0.3, 1.2, 0.03, 0.375);
                RegRef(m, "minimax-m2.5-highspeed", 0.3, 1.2, 0.03, 0.375);
                RegRef(m, "minimax-m2.1", 0.3, 1.2, 0.03, 0.375);
                RegRef(m, "minimax-m2", 0.3, 1.2, 0.03, 0.375);
                RegRef(m, "minimax-m1", 0.4, 2.2, 0.04, 0.0);
                RegRef(m, "qwen3.5-plus", 0.4, 1.2, 0.04, 0.0);
                RegRef(m, "qwen3.5-coder", 1.0, 4.0, 0.1, 0.0);
                RegRef(m, "qwen3-coder-plus", 1.0, 5.0, 0.1, 0.0);
                RegRef(m, "qwen3-coder", 0.3, 1.2, 0.03, 0.0);
                RegRef(m, "qwen3-coder-flash", 0.15, 0.6, 0.015, 0.0);
                RegRef(m, "qwen3-max", 1.2, 6.0, 0.12, 0.0);
                RegRef(m, "qwen3-235b-a22b", 0.3, 1.2, 0.03, 0.0);
                RegRef(m, "qwen3-32b", 0.15, 0.6, 0.015, 0.0);
                RegRef(m, "qwq-32b", 0.15, 0.6, 0.015, 0.0);
                RegRef(m, "qwen-max", 1.6, 6.4, 0.16, 0.0);
                RegRef(m, "qwen-plus", 0.4, 1.2, 0.04, 0.0);
                RegRef(m, "doubao-seed-code", 0.15, 0.6, 0.03, 0.0);
                RegRef(m, "doubao-1.5-pro", 0.15, 0.6, 0.03, 0.0);
                RegRef(m, "codestral", 0.3, 0.9, 0.03, 0.0);
                RegRef(m, "devstral", 0.1, 0.3, 0.01, 0.0);
                RegRef(m, "mistral-large", 2.0, 6.0, 0.2, 0.0);
                RegRef(m, "grok-4", 3.0, 15.0, 0.75, 0.0);
                RegRef(m, "grok-3", 3.0, 15.0, 0.75, 0.0);
                RegRef(m, "grok-code-fast", 0.2, 1.5, 0.02, 0.0);

                _builtinReferenceMap = m;
                return _builtinReferenceMap;
            }
        }

        private static void RegRef(Dictionary<string, RawRateEntry> map, string id, double inp, double outp, double cr, double cw)
        {
            map[id] = new RawRateEntry(inp, outp, cr, cw);
        }

        private string PrimaryAppDataCustomPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(appData))
                    appData = _homeDir;
                return Path.Combine(appData, "TokLens", "custom_prices.json");
            }
        }

        private List<string> GetExistingLegacyCustomPaths()
        {
            List<string> paths = new List<string>();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                string p1 = Path.Combine(userProfile, ".toklens", "custom_prices.json");
                if (File.Exists(p1))
                    paths.Add(p1);
            }

            if (!string.IsNullOrWhiteSpace(_homeDir) &&
                !string.Equals(_homeDir.TrimEnd('\\', '/'), (userProfile ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                string p2 = Path.Combine(_homeDir, ".toklens", "custom_prices.json");
                if (File.Exists(p2))
                    paths.Add(p2);
            }
            return paths;
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

                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return;

                if (root.ContainsKey("source") && root["source"] != null)
                    _cloudSourceSummary = Convert.ToString(root["source"], CultureInfo.InvariantCulture);
                if (root.ContainsKey("updated_at") && root["updated_at"] != null)
                    _cloudUpdatedAt = Convert.ToString(root["updated_at"], CultureInfo.InvariantCulture);

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
                            double ic = SafeToDouble(arr[0]);
                            double oc = SafeToDouble(arr[1]);
                            double crc = SafeToDouble(arr[2]);
                            double cwc = SafeToDouble(arr[3]);
                            if (ic > 0.0 || oc > 0.0)
                            {
                                _cloudRates[kv.Key.Trim().ToLowerInvariant()] = new RawRateEntry(ic, oc, crc, cwc);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadAllCustomFiles()
        {
            _customRates = new Dictionary<string, RawRateEntry>(StringComparer.OrdinalIgnoreCase);
            _customNormToRawKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            List<string> legacy = GetExistingLegacyCustomPaths();
            for (int i = 0; i < legacy.Count; i++)
            {
                MergeCustomFileIntoMap(legacy[i]);
            }
            MergeCustomFileIntoMap(PrimaryAppDataCustomPath);
        }

        private void MergeCustomFileIntoMap(string fp)
        {
            if (string.IsNullOrWhiteSpace(fp)) return;
            try
            {
                if (!File.Exists(fp)) return;
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> map = js.DeserializeObject(File.ReadAllText(fp, Encoding.UTF8)) as Dictionary<string, object>;
                if (map == null) return;

                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    Dictionary<string, object> m = kv.Value as Dictionary<string, object>;
                    if (m == null) continue;
                    if (!m.ContainsKey("input") && !m.ContainsKey("output")) continue;

                    double ic = m.ContainsKey("input") ? SafeToDouble(m["input"]) : 0.0;
                    double oc = m.ContainsKey("output") ? SafeToDouble(m["output"]) : 0.0;
                    double crc = m.ContainsKey("cache_read") ? SafeToDouble(m["cache_read"]) : 0.0;
                    double cwc = m.ContainsKey("cache_write") ? SafeToDouble(m["cache_write"]) : 0.0;
                    if (ic < 0.0 || oc < 0.0 || crc < 0.0 || cwc < 0.0) continue;

                    string cleanKey = kv.Key.Trim();
                    string norm = PricingEngine.NormalizeModelId(cleanKey, cleanKey.ToLowerInvariant());

                    string prevRaw;
                    if (!string.IsNullOrEmpty(norm) && _customNormToRawKey.TryGetValue(norm, out prevRaw))
                    {
                        if (!string.Equals(prevRaw, cleanKey, StringComparison.OrdinalIgnoreCase))
                            _customRates.Remove(prevRaw);
                    }

                    _customRates[cleanKey] = new RawRateEntry(ic, oc, crc, cwc);
                    if (!string.IsNullOrEmpty(norm))
                        _customNormToRawKey[norm] = cleanKey;
                }
            }
            catch { }
        }

        private void ReloadCatalogAndBind(string selectModelId, bool autoSelectFirstIfNone)
        {
            LoadCloudCacheFile();
            LoadAllCustomFiles();

            PricingEngine engine = new PricingEngine(_homeDir);
            Dictionary<string, RawRateEntry> builtinRef = GetBuiltinReferenceMap();

            // Ordered candidate discovery:
            // 1. Custom overrides  2. Active scanned models  3. Builtin seed models  4. Cloud 1,800+ models
            List<string> orderedCandidates = new List<string>();
            HashSet<string> seenKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, RawRateEntry> kv in _customRates)
            {
                string id = kv.Key.Trim();
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seenKey.Add(dedup))
                    orderedCandidates.Add(id);
            }

            for (int i = 0; i < _activeModelList.Count; i++)
            {
                string id = _activeModelList[i];
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seenKey.Add(dedup))
                    orderedCandidates.Add(id);
            }

            for (int i = 0; i < BuiltinSeedModelIds.Length; i++)
            {
                string id = BuiltinSeedModelIds[i];
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seenKey.Add(dedup))
                    orderedCandidates.Add(id);
            }

            List<string> cloudKeys = new List<string>(_cloudRates.Keys);
            cloudKeys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cloudKeys.Count; i++)
            {
                string id = cloudKeys[i];
                string dedup = PricingEngine.NormalizeModelId(id, id.ToLowerInvariant());
                if (seenKey.Add(dedup))
                    orderedCandidates.Add(id);
            }

            Brush customBg = GetThemeBrush("AccentSubtleBg");
            Brush customBd = GetThemeBrush("AccentSubtleBorder");
            Brush customFg = GetThemeBrush("AccentText");

            Brush cloudBg = GetThemeBrush("BgSurface");
            Brush cloudBd = GetThemeBrush("BorderStrong");
            Brush cloudFg = GetThemeBrush("FgPrimary");

            Brush builtinBg = GetThemeBrush("BgSurface");
            Brush builtinBd = GetThemeBrush("BorderCard");
            Brush builtinFg = GetThemeBrush("FgSecondary");

            Brush fallbackBg = GetThemeBrush("SemanticWarningBg");
            Brush fallbackBd = GetThemeBrush("SemanticWarningBorder");
            Brush fallbackFg = GetThemeBrush("SemanticWarning");

            List<PricingStudioRow> built = new List<PricingStudioRow>(orderedCandidates.Count);

            for (int i = 0; i < orderedCandidates.Count; i++)
            {
                string displayId = orderedCandidates[i];
                string normId = PricingEngine.NormalizeModelId(displayId, displayId.ToLowerInvariant());

                bool isUsed = _activeRawSet.Contains(displayId) || (!string.IsNullOrEmpty(normId) && _activeNormSet.Contains(normId));

                RawRateEntry customEntry = null;
                if (!_customRates.TryGetValue(displayId, out customEntry) && !string.IsNullOrEmpty(normId))
                {
                    string mappedKey;
                    if (_customNormToRawKey.TryGetValue(normId, out mappedKey))
                        _customRates.TryGetValue(mappedKey, out customEntry);
                }

                RawRateEntry cloudEntry = null;
                if (!_cloudRates.TryGetValue(displayId.ToLowerInvariant(), out cloudEntry) && !string.IsNullOrEmpty(normId))
                {
                    _cloudRates.TryGetValue(normId, out cloudEntry);
                }

                RawRateEntry builtinEntry = null;
                if (!builtinRef.TryGetValue(displayId, out builtinEntry) && !string.IsNullOrEmpty(normId))
                {
                    builtinRef.TryGetValue(normId, out builtinEntry);
                }

                ModelRate resolved = engine.Lookup(displayId);

                // Official (non-custom) baseline resolution
                bool hasOfficial = false;
                double offIn = 0.0;
                double offOut = 0.0;
                double offCr = 0.0;
                double offCw = 0.0;

                if (cloudEntry != null)
                {
                    hasOfficial = true;
                    offIn = cloudEntry.Input;
                    offOut = cloudEntry.Output;
                    offCr = cloudEntry.CacheRead > 0.0
                        ? cloudEntry.CacheRead
                        : (builtinEntry != null && IsSimilarRate(builtinEntry.Input, cloudEntry.Input) ? builtinEntry.CacheRead : 0.0);
                    offCw = cloudEntry.CacheWrite > 0.0
                        ? cloudEntry.CacheWrite
                        : (builtinEntry != null && IsSimilarRate(builtinEntry.Input, cloudEntry.Input) ? builtinEntry.CacheWrite : 0.0);
                }
                else if (builtinEntry != null)
                {
                    hasOfficial = true;
                    offIn = builtinEntry.Input;
                    offOut = builtinEntry.Output;
                    offCr = builtinEntry.CacheRead;
                    offCw = builtinEntry.CacheWrite;
                }
                else if (resolved != null &&
                         !string.Equals(resolved.Source, "custom", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(resolved.Source, "fallback", StringComparison.OrdinalIgnoreCase))
                {
                    hasOfficial = true;
                    offIn = resolved.InputPerM;
                    offOut = resolved.OutputPerM;
                    offCr = resolved.CacheReadPerM;
                    offCw = resolved.CacheWritePerM;
                }

                double curIn;
                double curOut;
                double curCr;
                double curCw;
                string srcKind;
                int srcRank;
                string badgeText;
                Brush sBg;
                Brush sBd;
                Brush sFg;

                if (customEntry != null)
                {
                    curIn = customEntry.Input;
                    curOut = customEntry.Output;
                    curCr = customEntry.CacheRead;
                    curCw = customEntry.CacheWrite;
                    srcKind = "custom";
                    srcRank = 1;
                    badgeText = "自定义覆盖";
                    sBg = customBg;
                    sBd = customBd;
                    sFg = customFg;
                }
                else if (cloudEntry != null || (resolved != null && string.Equals(resolved.Source, "cloud", StringComparison.OrdinalIgnoreCase)))
                {
                    curIn = resolved != null ? resolved.InputPerM : offIn;
                    curOut = resolved != null ? resolved.OutputPerM : offOut;
                    curCr = resolved != null ? resolved.CacheReadPerM : offCr;
                    curCw = resolved != null ? resolved.CacheWritePerM : offCw;
                    srcKind = "cloud";
                    srcRank = 2;
                    badgeText = "云端三源";
                    sBg = cloudBg;
                    sBd = cloudBd;
                    sFg = cloudFg;
                }
                else if (resolved != null && string.Equals(resolved.Source, "fallback", StringComparison.OrdinalIgnoreCase))
                {
                    curIn = resolved.InputPerM;
                    curOut = resolved.OutputPerM;
                    curCr = resolved.CacheReadPerM;
                    curCw = resolved.CacheWritePerM;
                    srcKind = "fallback";
                    srcRank = 4;
                    badgeText = "兜底估算";
                    sBg = fallbackBg;
                    sBd = fallbackBd;
                    sFg = fallbackFg;
                }
                else
                {
                    curIn = resolved != null ? resolved.InputPerM : offIn;
                    curOut = resolved != null ? resolved.OutputPerM : offOut;
                    curCr = resolved != null ? resolved.CacheReadPerM : offCr;
                    curCw = resolved != null ? resolved.CacheWritePerM : offCw;
                    srcKind = "builtin";
                    srcRank = 3;
                    badgeText = "内置基准";
                    sBg = builtinBg;
                    sBd = builtinBd;
                    sFg = builtinFg;
                }

                PricingStudioRow row = new PricingStudioRow();
                row.ModelId = displayId;
                row.NormId = normId;
                row.IsActiveUsed = isUsed;
                row.ActiveBadgeVisibility = isUsed ? Visibility.Visible : Visibility.Collapsed;
                row.IsCustom = (customEntry != null);
                row.InCloudCache = (cloudEntry != null) || string.Equals(srcKind, "cloud", StringComparison.OrdinalIgnoreCase);
                row.SourceKind = srcKind;
                row.SourceRank = srcRank;
                row.SourceBadgeText = badgeText;
                row.SourceBgBrush = sBg;
                row.SourceBorderBrush = sBd;
                row.SourceFgBrush = sFg;
                row.ProviderDotBrush = DesignSystem.BrushHex(DesignSystem.GetProviderColorHex(displayId, _isDark));

                row.InputPerM = curIn;
                row.InputText = FormatDollarPerM(curIn);
                row.OutputPerM = curOut;
                row.OutputText = FormatDollarPerM(curOut);
                row.CacheReadPerM = curCr;
                row.CacheReadText = FormatDollarPerM(curCr);
                row.CacheWritePerM = curCw;
                row.CacheWriteText = FormatDollarPerM(curCw);

                row.HasOfficialRate = hasOfficial;
                row.OfficialInput = offIn;
                row.OfficialOutput = offOut;
                row.OfficialCacheRead = offCr;
                row.OfficialCacheWrite = offCw;

                built.Add(row);
            }

            // Default smart ordering: Active & Custom first -> Custom -> Active -> Builtin -> Cloud alphabetical
            built.Sort(CompareDefaultRows);
            _allRows = built;

            UpdateHeaderAndFooterMeta();
            ApplyFiltersAndRefreshGrid(selectModelId);

            if (autoSelectFirstIfNone && _selectedRow == null && _grid.Items.Count > 0)
            {
                _grid.SelectedIndex = 0;
            }
        }

        private static int CompareDefaultRows(PricingStudioRow a, PricingStudioRow b)
        {
            int prioA = GetPriorityBucket(a);
            int prioB = GetPriorityBucket(b);
            if (prioA != prioB)
                return prioA.CompareTo(prioB);
            return string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetPriorityBucket(PricingStudioRow r)
        {
            if (r.IsCustom && r.IsActiveUsed) return 0;
            if (r.IsCustom) return 1;
            if (r.IsActiveUsed) return 2;
            if (string.Equals(r.SourceKind, "builtin", StringComparison.Ordinal)) return 3;
            return 4;
        }

        private void UpdateHeaderAndFooterMeta()
        {
            if (_headerSummaryBadgeText != null)
            {
                _headerSummaryBadgeText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:N0} 款模型 · {1} 项自定义",
                    _allRows.Count,
                    _customRates.Count);
            }

            if (_footerRightMetaText != null)
            {
                if (!string.IsNullOrEmpty(_cloudSourceSummary))
                {
                    string upd = !string.IsNullOrEmpty(_cloudUpdatedAt) ? (" · " + _cloudUpdatedAt) : "";
                    _footerRightMetaText.Text = "三源: " + _cloudSourceSummary + upd;
                }
                else
                {
                    _footerRightMetaText.Text = "Ctrl+F 搜索 · Enter 保存 · Esc 关闭";
                }
            }
        }

        private void ApplyFiltersAndRefreshGrid(string targetSelectModelId)
        {
            if (_grid == null) return;

            string kw = (_searchBox != null && _searchBox.Text != null) ? _searchBox.Text.Trim() : "";
            string kwLower = kw.ToLowerInvariant();
            string kwNorm = PricingEngine.NormalizeModelId(kw, "");

            List<PricingStudioRow> filtered = new List<PricingStudioRow>(_allRows.Count);
            for (int i = 0; i < _allRows.Count; i++)
            {
                PricingStudioRow r = _allRows[i];
                if (_activeFilterIndex == 1 && !r.IsActiveUsed) continue;
                if (_activeFilterIndex == 2 && !r.IsCustom) continue;
                if (_activeFilterIndex == 3 && !r.InCloudCache) continue;

                if (!string.IsNullOrEmpty(kwLower))
                {
                    bool hit = (r.ModelId != null && r.ModelId.IndexOf(kwLower, StringComparison.OrdinalIgnoreCase) >= 0) ||
                               (!string.IsNullOrEmpty(r.NormId) && r.NormId.IndexOf(kwLower, StringComparison.OrdinalIgnoreCase) >= 0) ||
                               (!string.IsNullOrEmpty(kwNorm) && !string.IsNullOrEmpty(r.NormId) && r.NormId.IndexOf(kwNorm, StringComparison.OrdinalIgnoreCase) >= 0) ||
                               (r.SourceBadgeText != null && r.SourceBadgeText.IndexOf(kwLower, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!hit) continue;
                }

                filtered.Add(r);
            }

            if (_matchCountText != null)
            {
                _matchCountText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "显示 {0:N0} / {1:N0} 款",
                    filtered.Count,
                    _allRows.Count);
            }

            _suppressSelectionChanged = true;
            try
            {
                ModernDataGridFactory.UpdateItemsPreservingState(
                    _grid,
                    filtered,
                    delegate(object item)
                    {
                        PricingStudioRow row = item as PricingStudioRow;
                        return row != null ? row.ModelId : null;
                    },
                    false);

                if (!string.IsNullOrEmpty(targetSelectModelId))
                {
                    string targetNorm = PricingEngine.NormalizeModelId(targetSelectModelId, targetSelectModelId.ToLowerInvariant());
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        PricingStudioRow cand = filtered[i];
                        if (string.Equals(cand.ModelId, targetSelectModelId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cand.NormId, targetNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            _grid.SelectedItem = cand;
                            _selectedRow = cand;
                            PopulateEditorFromRow(cand);
                            break;
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            if (_emptyOverlay != null)
            {
                if (filtered.Count == 0)
                {
                    _emptyOverlay.Visibility = Visibility.Visible;
                    if (!string.IsNullOrEmpty(kw))
                    {
                        _emptyTitleText.Text = "未找到匹配 \"" + kw + "\" 的已有模型";
                        _emptySubText.Text = "如果是中转站别名或本地自部署模型，可直接将其添加为自定义单价规则";
                        _emptyFillEditorBtn.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _emptyTitleText.Text = _activeFilterIndex == 2
                            ? "当前尚无自定义单价覆盖规则"
                            : (_activeFilterIndex == 1 ? "当前尚无已扫描到的活跃模型" : "暂无符合筛选条件的模型");
                        _emptySubText.Text = "可在「全部」或「云端三源」中选择模型并点击下方「保存覆盖规则」";
                        _emptyFillEditorBtn.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    _emptyOverlay.Visibility = Visibility.Collapsed;
                }
            }
        }

        // -------------------- Editor Selection & Quick Multipliers --------------------

        private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            PricingStudioRow row = _grid.SelectedItem as PricingStudioRow;
            if (row == null) return;
            _selectedRow = row;
            PopulateEditorFromRow(row);
        }

        private void PopulateEditorFromRow(PricingStudioRow row)
        {
            if (row == null) return;
            _suppressSelectionChanged = true;
            try
            {
                _modelIdInput.Text = row.ModelId;
                _inputRateBox.Text = FormatPlainRate(row.InputPerM);
                _outputRateBox.Text = FormatPlainRate(row.OutputPerM);
                _cacheReadRateBox.Text = FormatPlainRate(row.CacheReadPerM);
                _cacheWriteRateBox.Text = FormatPlainRate(row.CacheWritePerM);
                _editorTouchedManually = false;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
            UpdateEditorActionState();
        }

        private PricingStudioRow FindMatchingCatalogRow(string rawModelId)
        {
            if (string.IsNullOrWhiteSpace(rawModelId)) return null;
            string trimmed = rawModelId.Trim();
            string norm = PricingEngine.NormalizeModelId(trimmed, trimmed.ToLowerInvariant());

            for (int i = 0; i < _allRows.Count; i++)
            {
                PricingStudioRow r = _allRows[i];
                if (string.Equals(r.ModelId, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(norm) && string.Equals(r.NormId, norm, StringComparison.OrdinalIgnoreCase)))
                {
                    return r;
                }
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

        private void UpdateEditorActionState()
        {
            string curModel = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            PricingStudioRow matched = FindMatchingCatalogRow(curModel);
            bool hasCustom = HasSavedCustomOverride(curModel);

            if (_deleteRuleBtn != null)
                _deleteRuleBtn.IsEnabled = hasCustom;

            if (_restoreOfficialValBtn != null)
                _restoreOfficialValBtn.IsEnabled = (matched != null && matched.HasOfficialRate);

            if (_officialHintText != null)
            {
                if (string.IsNullOrEmpty(curModel))
                {
                    _officialHintText.Text = "单击上方任意模型行即可载入四维单价";
                }
                else if (matched != null && matched.HasOfficialRate)
                {
                    _officialHintText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "官方基准: 入 ${0} · 出 ${1} · 读 ${2} · 写 ${3}",
                        FormatPlainRate(matched.OfficialInput),
                        FormatPlainRate(matched.OfficialOutput),
                        FormatPlainRate(matched.OfficialCacheRead),
                        FormatPlainRate(matched.OfficialCacheWrite));
                }
                else if (hasCustom)
                {
                    _officialHintText.Text = "当前为自定义新建模型（无云端官方基准）";
                }
                else
                {
                    _officialHintText.Text = "新模型 ID（保存后将直接加入自定义价格库）";
                }
            }
        }

        private void ApplyQuickMultiplier(double factor, string factorLabel)
        {
            string curModel = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            if (string.IsNullOrEmpty(curModel))
            {
                SetStatus("请先在上方表格点击选择一个模型，或输入模型 ID 后再使用快捷倍率", "warning");
                return;
            }

            PricingStudioRow matched = FindMatchingCatalogRow(curModel);

            double baseIn;
            double baseOut;
            double baseCr;
            double baseCw;
            string basisNote;

            // If user selected a model with a known official baseline and hasn't typed custom numbers manually yet,
            // compute the discount cleanly against the official baseline (avoids compounding 0.5x * 0.2x accidentally).
            if (matched != null && matched.HasOfficialRate && !_editorTouchedManually)
            {
                baseIn = matched.OfficialInput;
                baseOut = matched.OfficialOutput;
                baseCr = matched.OfficialCacheRead;
                baseCw = matched.OfficialCacheWrite;
                basisNote = "官方基准价";
            }
            else
            {
                if (!TryParseNonNegative(_inputRateBox.Text, out baseIn) ||
                    !TryParseNonNegative(_outputRateBox.Text, out baseOut) ||
                    !TryParseNonNegative(_cacheReadRateBox.Text, out baseCr) ||
                    !TryParseNonNegative(_cacheWriteRateBox.Text, out baseCw))
                {
                    SetStatus("当前输入框内包含无效数字，请检查后再应用快捷倍率", "danger");
                    return;
                }
                basisNote = "当前输入值";
            }

            double nextIn = RoundRateClean(baseIn * factor);
            double nextOut = RoundRateClean(baseOut * factor);
            double nextCr = RoundRateClean(baseCr * factor);
            double nextCw = RoundRateClean(baseCw * factor);

            _suppressSelectionChanged = true;
            try
            {
                _inputRateBox.Text = FormatPlainRate(nextIn);
                _outputRateBox.Text = FormatPlainRate(nextOut);
                _cacheReadRateBox.Text = FormatPlainRate(nextCr);
                _cacheWriteRateBox.Text = FormatPlainRate(nextCw);
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            SetStatus(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "已按{0}折算 {1}（${2} / ${3}），点击「保存覆盖规则」立即生效",
                    basisNote,
                    factorLabel,
                    FormatPlainRate(nextIn),
                    FormatPlainRate(nextOut)),
                "neutral");
        }

        private void ApplyZeroFreeRate()
        {
            string curModel = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            _suppressSelectionChanged = true;
            try
            {
                _inputRateBox.Text = "0";
                _outputRateBox.Text = "0";
                _cacheReadRateBox.Text = "0";
                _cacheWriteRateBox.Text = "0";
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            if (string.IsNullOrEmpty(curModel))
            {
                SetStatus("已将四维单价设为 $0（免费），请填写或选择模型 ID 后点击「保存覆盖规则」", "neutral");
            }
            else
            {
                SetStatus("已将 \"" + curModel + "\" 设为 $0 免费单价，点击「保存覆盖规则」立即生效", "neutral");
            }
        }

        private void FillOfficialBaselineToInputs()
        {
            string curModel = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            PricingStudioRow matched = FindMatchingCatalogRow(curModel);
            if (matched == null || !matched.HasOfficialRate)
            {
                SetStatus("未找到该模型的官方基准单价", "warning");
                return;
            }

            _suppressSelectionChanged = true;
            try
            {
                _inputRateBox.Text = FormatPlainRate(matched.OfficialInput);
                _outputRateBox.Text = FormatPlainRate(matched.OfficialOutput);
                _cacheReadRateBox.Text = FormatPlainRate(matched.OfficialCacheRead);
                _cacheWriteRateBox.Text = FormatPlainRate(matched.OfficialCacheWrite);
                _editorTouchedManually = false;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            SetStatus("已填回 \"" + matched.ModelId + "\" 的官方基准单价", "neutral");
        }

        // -------------------- Atomic Save & Delete Custom Rules --------------------

        private void OnSaveOverrideClick(object sender, RoutedEventArgs e)
        {
            string modelId = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            if (string.IsNullOrEmpty(modelId))
            {
                SetStatus("模型 ID 不能为空，请先点击表中模型或输入要覆盖的模型名称", "warning");
                if (_modelIdInput != null) _modelIdInput.Focus();
                return;
            }

            double inp;
            double outp;
            double cr;
            double cw;

            if (!TryParseNonNegative(_inputRateBox.Text, out inp))
            {
                SetStatus("「输入 ($/1M)」单价格式无效，请输入大于或等于 0 的数字", "danger");
                _inputRateBox.Focus();
                _inputRateBox.SelectAll();
                return;
            }
            if (!TryParseNonNegative(_outputRateBox.Text, out outp))
            {
                SetStatus("「输出 ($/1M)」单价格式无效，请输入大于或等于 0 的数字", "danger");
                _outputRateBox.Focus();
                _outputRateBox.SelectAll();
                return;
            }
            if (!TryParseNonNegative(_cacheReadRateBox.Text, out cr))
            {
                SetStatus("「缓存读取 ($/1M)」单价格式无效，请输入大于或等于 0 的数字", "danger");
                _cacheReadRateBox.Focus();
                _cacheReadRateBox.SelectAll();
                return;
            }
            if (!TryParseNonNegative(_cacheWriteRateBox.Text, out cw))
            {
                SetStatus("「缓存写入 ($/1M)」单价格式无效，请输入大于或等于 0 的数字", "danger");
                _cacheWriteRateBox.Focus();
                _cacheWriteRateBox.SelectAll();
                return;
            }

            if (inp > 10000.0 || outp > 10000.0 || cr > 10000.0 || cw > 10000.0)
            {
                SetStatus("单价数值超出合理范围（> $10,000 / 1M），请核对单位是否为每百万 Token 美元单价", "warning");
                return;
            }

            try
            {
                LoadAllCustomFiles();

                string targetNorm = PricingEngine.NormalizeModelId(modelId, modelId.ToLowerInvariant());
                List<string> staleKeys = new List<string>();
                foreach (KeyValuePair<string, RawRateEntry> kv in _customRates)
                {
                    string existingNorm = PricingEngine.NormalizeModelId(kv.Key, kv.Key.ToLowerInvariant());
                    if (string.Equals(kv.Key, modelId, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetNorm) && string.Equals(existingNorm, targetNorm, StringComparison.OrdinalIgnoreCase)))
                    {
                        staleKeys.Add(kv.Key);
                    }
                }
                for (int i = 0; i < staleKeys.Count; i++)
                {
                    _customRates.Remove(staleKeys[i]);
                }

                _customRates[modelId] = new RawRateEntry(
                    RoundRateClean(inp),
                    RoundRateClean(outp),
                    RoundRateClean(cr),
                    RoundRateClean(cw));

                PersistCustomRatesAtomically();
                ReloadCatalogAndBind(modelId, false);

                if (_onPricesUpdated != null)
                {
                    try { _onPricesUpdated(); } catch { }
                }

                SetStatus(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "已保存 \"{0}\" 自定义单价（入 ${1} / 出 ${2} / 读 ${3} / 写 ${4}），主界面账单已实时重算",
                        modelId,
                        FormatPlainRate(inp),
                        FormatPlainRate(outp),
                        FormatPlainRate(cr),
                        FormatPlainRate(cw)),
                    "success");
            }
            catch (Exception ex)
            {
                SetStatus("保存自定义价格配置失败：" + ex.Message, "danger");
            }
        }

        private void OnDeleteOverrideClick(object sender, RoutedEventArgs e)
        {
            string modelId = (_modelIdInput != null && _modelIdInput.Text != null) ? _modelIdInput.Text.Trim() : "";
            if (string.IsNullOrEmpty(modelId))
            {
                SetStatus("请先选择要移除自定义覆盖规则的模型", "warning");
                return;
            }

            try
            {
                LoadAllCustomFiles();

                string targetNorm = PricingEngine.NormalizeModelId(modelId, modelId.ToLowerInvariant());
                List<string> keysToRemove = new List<string>();
                foreach (KeyValuePair<string, RawRateEntry> kv in _customRates)
                {
                    string existingNorm = PricingEngine.NormalizeModelId(kv.Key, kv.Key.ToLowerInvariant());
                    if (string.Equals(kv.Key, modelId, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetNorm) && string.Equals(existingNorm, targetNorm, StringComparison.OrdinalIgnoreCase)))
                    {
                        keysToRemove.Add(kv.Key);
                    }
                }

                if (keysToRemove.Count == 0)
                {
                    SetStatus("该模型当前没有自定义覆盖规则", "warning");
                    UpdateEditorActionState();
                    return;
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _customRates.Remove(keysToRemove[i]);
                }

                PersistCustomRatesAtomically();
                ReloadCatalogAndBind(modelId, false);

                if (_onPricesUpdated != null)
                {
                    try { _onPricesUpdated(); } catch { }
                }

                SetStatus("已删除 \"" + modelId + "\" 的自定义单价覆盖规则，已恢复官方价格并实时重算全局账单", "success");
            }
            catch (Exception ex)
            {
                SetStatus("删除自定义规则失败：" + ex.Message, "danger");
            }
        }

        private void PersistCustomRatesAtomically()
        {
            string jsonPayload = SerializeCustomRatesFormatted(_customRates);

            // 1. Always write atomically to primary %APPDATA%\TokLens\custom_prices.json
            WriteFileAtomically(PrimaryAppDataCustomPath, jsonPayload);

            // 2. If %USERPROFILE%\.toklens\custom_prices.json (or _homeDir\.toklens\custom_prices.json) exists, sync it too
            List<string> legacyExisting = GetExistingLegacyCustomPaths();
            for (int i = 0; i < legacyExisting.Count; i++)
            {
                try
                {
                    WriteFileAtomically(legacyExisting[i], jsonPayload);
                }
                catch { }
            }
        }

        private static string SerializeCustomRatesFormatted(Dictionary<string, RawRateEntry> rates)
        {
            List<string> sortedKeys = new List<string>(rates.Keys);
            sortedKeys.Sort(StringComparer.OrdinalIgnoreCase);

            JavaScriptSerializer js = new JavaScriptSerializer();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                string key = sortedKeys[i];
                RawRateEntry r = rates[key];
                sb.Append("  ");
                sb.Append(js.Serialize(key));
                sb.Append(": {\n");
                sb.Append("    \"input\": ").Append(FormatJsonNumber(r.Input)).Append(",\n");
                sb.Append("    \"output\": ").Append(FormatJsonNumber(r.Output)).Append(",\n");
                sb.Append("    \"cache_read\": ").Append(FormatJsonNumber(r.CacheRead)).Append(",\n");
                sb.Append("    \"cache_write\": ").Append(FormatJsonNumber(r.CacheWrite)).Append("\n");
                sb.Append("  }");
                if (i < sortedKeys.Count - 1)
                    sb.Append(",");
                sb.Append("\n");
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        private static string FormatJsonNumber(double val)
        {
            if (double.IsNaN(val) || double.IsInfinity(val) || val < 0.0)
                return "0";
            double cleaned = RoundRateClean(val);
            if (cleaned == 0.0)
                return "0";
            return cleaned.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static void WriteFileAtomically(string targetPath, string content)
        {
            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(content);
                    sw.Flush();
                    fs.Flush(true);
                }

                if (File.Exists(targetPath))
                {
                    try
                    {
                        File.Replace(tmpPath, targetPath, null, true);
                        return;
                    }
                    catch
                    {
                        File.Copy(tmpPath, targetPath, true);
                    }
                }
                else
                {
                    File.Move(tmpPath, targetPath);
                    return;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                catch { }
            }
        }

        // -------------------- Cloud Three-Source Sync --------------------

        private void OnSyncCloudPricesClick(object sender, RoutedEventArgs e)
        {
            if (_isSyncingCloud) return;
            _isSyncingCloud = true;
            if (_syncCloudBtn != null) _syncCloudBtn.IsEnabled = false;
            SetStatus("正在从 models.dev + LiteLLM + OpenRouter 聚合拉取最新三源模型价格库...", "neutral");

            Task.Run(delegate()
            {
                PricingSyncInfo info = null;
                string err = null;
                try
                {
                    info = PricingEngine.SyncCloudPricing(true);
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _isSyncingCloud = false;
                    if (_syncCloudBtn != null) _syncCloudBtn.IsEnabled = true;

                    if (!string.IsNullOrEmpty(err))
                    {
                        SetStatus("云端三源价格库同步异常：" + err, "warning");
                        return;
                    }

                    if (info != null && info.IsLive && info.CloudModelCount > 0)
                    {
                        string keepModel = (_modelIdInput != null && !string.IsNullOrWhiteSpace(_modelIdInput.Text))
                            ? _modelIdInput.Text.Trim()
                            : null;
                        ReloadCatalogAndBind(keepModel, false);
                        if (_onPricesUpdated != null)
                        {
                            try { _onPricesUpdated(); } catch { }
                        }
                        SetStatus(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "已成功同步 {0:N0} 款云端模型单价（来源: {1}）",
                                info.CloudModelCount,
                                info.Source),
                            "success");
                    }
                    else
                    {
                        SetStatus("未能连接外网价格源，当前继续使用本地缓存与内置价格库", "warning");
                    }
                }));
            });
        }

        // -------------------- Keyboard Shortcuts & Helpers --------------------

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_searchBox != null)
                {
                    _searchBox.Focus();
                    _searchBox.SelectAll();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (_searchBox != null && _searchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(_searchBox.Text))
                {
                    _searchBox.Text = "";
                    e.Handled = true;
                }
                else
                {
                    e.Handled = true;
                    Close();
                }
            }
        }

        private void SetStatus(string message, string variant)
        {
            if (_statusText == null || _statusDot == null) return;
            _statusText.Text = message ?? "";

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                _statusDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticSuccess");
                _statusText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                _statusDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticWarning");
                _statusText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
            }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase))
            {
                _statusDot.SetResourceReference(Shapes.Shape.FillProperty, "SemanticDanger");
                _statusText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
            }
            else
            {
                _statusDot.SetResourceReference(Shapes.Shape.FillProperty, "AccentPrimary");
                _statusText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            }
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

        private static bool IsSimilarRate(double a, double b)
        {
            if (a <= 0.0 || b <= 0.0) return false;
            return Math.Abs(a - b) <= Math.Max(a, b) * 0.25;
        }

        private static double SafeToDouble(object obj)
        {
            if (obj == null) return 0.0;
            double v;
            if (double.TryParse(Convert.ToString(obj, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
            {
                if (!double.IsNaN(v) && !double.IsInfinity(v))
                    return v;
            }
            return 0.0;
        }

        private static bool TryParseNonNegative(string raw, out double val)
        {
            val = 0.0;
            if (string.IsNullOrWhiteSpace(raw))
                return true;

            string cleaned = raw.Trim();
            if (cleaned.StartsWith("$", StringComparison.Ordinal))
                cleaned = cleaned.Substring(1).Trim();

            double parsed;
            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return false;
            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0.0)
                return false;

            val = parsed;
            return true;
        }

        private static double RoundRateClean(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0.0)
                return 0.0;
            return Math.Round(v, 6, MidpointRounding.AwayFromZero);
        }

        private static string FormatPlainRate(double v)
        {
            double c = RoundRateClean(v);
            if (c == 0.0) return "0";
            return c.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string FormatDollarPerM(double v)
        {
            double c = RoundRateClean(v);
            if (c == 0.0) return "$0.00";
            if (c >= 0.1 && Math.Abs(c * 100.0 - Math.Round(c * 100.0)) < 1e-6)
                return "$" + c.ToString("0.00", CultureInfo.InvariantCulture);
            return "$" + c.ToString("0.00####", CultureInfo.InvariantCulture);
        }
    }
}
