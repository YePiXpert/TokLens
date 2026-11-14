using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TokLens
{
    /// <summary>
    /// 命令面板单条可执行项数据模型。
    /// </summary>
    public sealed class PaletteCommandItem
    {
        public string Id;
        public string Category;
        public string Title;
        public string Subtitle;
        public string ShortcutText;
        public string IconPath;
        public Action Execute;

        public PaletteCommandItem()
        {
        }

        public PaletteCommandItem(
            string id,
            string category,
            string title,
            string subtitle,
            string shortcutText,
            string iconPath,
            Action execute)
        {
            Id = id;
            Category = category;
            Title = title;
            Subtitle = subtitle;
            ShortcutText = shortcutText;
            IconPath = iconPath;
            Execute = execute;
        }
    }

    /// <summary>
    /// Linear / Raycast / Cursor Ctrl+K 风格键盘优先全局命令面板覆盖层。
    /// </summary>
    public sealed class CommandPaletteOverlay : Grid
    {
        private const string DefaultItemIconPath = "M 4.5,4.5 L 9.5,8 L 4.5,11.5 M 9.5,11.5 L 12.5,11.5";
        private const string ClearCrossIconPath = "M 4,4 L 12,12 M 12,4 L 4,12";

        private sealed class IndexedEntry
        {
            public PaletteCommandItem Item;
            public int OriginalOrder;
            public string CategoryDisplay;
            public string TitleLower;
            public string SubtitleLower;
            public string CategoryLower;
            public string ShortcutLower;
            public string ShortcutCompactLower;
            public string IdLower;
            public string TitleInitials;
            public string FullInitials;
        }

        private sealed class CategoryBucket
        {
            public string CategoryName;
            public int BestScore;
            public int FirstOrder;
            public List<ScoredEntry> Entries = new List<ScoredEntry>();
        }

        private sealed class ScoredEntry
        {
            public IndexedEntry Entry;
            public int Score;
        }

        private sealed class RowVisual
        {
            public PaletteCommandItem Item;
            public int Index;
            public Border RowBorder;
            public Border AccentBar;
            public Border IconWrap;
            public Path IconPath;
            public TextBlock TitleBlock;
            public TextBlock SubtitleBlock;
            public Border ShortcutBadge;
            public TextBlock ShortcutLabel;
        }

        private static readonly Encoding GbkEncoding = TryGetGbkEncoding();
        private static readonly Dictionary<char, char> SpecialPinyinMap = BuildSpecialPinyinMap();

        private readonly List<IndexedEntry> _indexedEntries = new List<IndexedEntry>();
        private readonly List<RowVisual> _rowVisuals = new List<RowVisual>();

        private Border _backdrop;
        private Grid _cardWrapper;
        private ScaleTransform _cardScale;
        private Border _cardBorder;
        private Path _searchIcon;
        private TextBox _searchBox;
        private TextBlock _watermarkText;
        private Border _clearQueryBtn;
        private ScrollViewer _listScroll;
        private StackPanel _itemsPanel;
        private TextBlock _footerCountText;

        private bool _isOpen = false;
        private bool _suppressTextChanged = false;
        private int _selectedIndex = -1;
        private int _mousePressedRowIndex = -1;
        private Point _lastMousePosition = new Point(-9999.0, -9999.0);

        public event EventHandler Closed;

        public CommandPaletteOverlay()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Visibility = Visibility.Collapsed;
            Focusable = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            Panel.SetZIndex(this, 1000);

            BuildOverlayVisualTree();

            PreviewKeyDown += OnOverlayPreviewKeyDown;
            PreviewMouseMove += OnOverlayPreviewMouseMove;
        }

        public bool IsOpen
        {
            get { return _isOpen; }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
        }

        public int VisibleCommandCount
        {
            get { return _rowVisuals.Count; }
        }

        /// <summary>
        /// 打开命令面板：填充数据源、清空搜索框、默认选中第 0 项并自动聚焦输入框。
        /// </summary>
        public void Open(List<PaletteCommandItem> items)
        {
            IndexCommandItems(items);

            _isOpen = true;
            _mousePressedRowIndex = -1;
            _lastMousePosition = new Point(-9999.0, -9999.0);
            Visibility = Visibility.Visible;

            _suppressTextChanged = true;
            _searchBox.Text = string.Empty;
            _suppressTextChanged = false;
            UpdateSearchBarDecorations();

            RebuildFilteredRows(string.Empty);
            PlayOpenEntranceTransition();
            FocusSearchInputImmediateAndDeferred();
        }

        /// <summary>
        /// 关闭命令面板并隐藏遮罩。
        /// </summary>
        public void Close()
        {
            if (!_isOpen && Visibility != Visibility.Visible)
                return;

            _isOpen = false;
            _mousePressedRowIndex = -1;
            Visibility = Visibility.Collapsed;

            EventHandler handler = Closed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void BuildOverlayVisualTree()
        {
            // 1. 全窗口半透明暗化遮罩层 (#88000000)
            _backdrop = new Border();
            _backdrop.HorizontalAlignment = HorizontalAlignment.Stretch;
            _backdrop.VerticalAlignment = VerticalAlignment.Stretch;
            _backdrop.SetResourceReference(Border.BackgroundProperty, "BgScrim");
            _backdrop.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Close();
            };
            Children.Add(_backdrop);

            // 2. 居中偏上悬浮卡片容器 (Margin(0, 76, 0, 0), Width = 560, MaxHeight = 420)
            _cardWrapper = new Grid();
            _cardWrapper.Width = 560.0;
            _cardWrapper.MaxHeight = 420.0;
            _cardWrapper.Margin = new Thickness(0.0, 76.0, 0.0, 0.0);
            _cardWrapper.HorizontalAlignment = HorizontalAlignment.Center;
            _cardWrapper.VerticalAlignment = VerticalAlignment.Top;
            _cardWrapper.RenderTransformOrigin = new Point(0.5, 0.0);
            _cardScale = new ScaleTransform(1.0, 1.0);
            _cardWrapper.RenderTransform = _cardScale;

            // 独立阴影承载层：保证柔和 DropShadowEffect 同时避免子树位图光栅化导致高分屏文字模糊
            Border shadowHost = new Border();
            shadowHost.CornerRadius = new CornerRadius(10.0);
            shadowHost.SetResourceReference(Border.BackgroundProperty, "BgCard");
            shadowHost.IsHitTestVisible = false;
            DropShadowEffect dropShadow = new DropShadowEffect();
            dropShadow.Color = Colors.Black;
            dropShadow.BlurRadius = 38.0;
            dropShadow.ShadowDepth = 12.0;
            dropShadow.Direction = 270.0;
            dropShadow.Opacity = 0.46;
            shadowHost.Effect = dropShadow;
            _cardWrapper.Children.Add(shadowHost);

            // 主面板卡片：CornerRadius(10) + 1px BorderStrong
            _cardBorder = new Border();
            _cardBorder.CornerRadius = new CornerRadius(10.0);
            _cardBorder.BorderThickness = new Thickness(1.0);
            _cardBorder.SetResourceReference(Border.BackgroundProperty, "BgCard");
            _cardBorder.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            _cardBorder.SnapsToDevicePixels = true;
            _cardBorder.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                // 吸收卡片内部点击防止穿透到暗化遮罩误关闭，并将焦点维持在搜索框上
                e.Handled = true;
                if (!_searchBox.IsKeyboardFocused)
                {
                    _searchBox.Focus();
                }
            };

            Grid cardRows = new Grid();
            cardRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48.0) });
            cardRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            cardRows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28.0) });

            Border topBar = BuildTopSearchBar();
            Grid.SetRow(topBar, 0);
            cardRows.Children.Add(topBar);

            _listScroll = BuildCommandListArea();
            Grid.SetRow(_listScroll, 1);
            cardRows.Children.Add(_listScroll);

            Border footerBar = BuildFooterHintBar();
            Grid.SetRow(footerBar, 2);
            cardRows.Children.Add(footerBar);

            _cardBorder.Child = cardRows;
            _cardWrapper.Children.Add(_cardBorder);
            Children.Add(_cardWrapper);
        }

        private Border BuildTopSearchBar()
        {
            Border bar = new Border();
            bar.Height = 48.0;
            bar.Padding = new Thickness(14.0, 0.0, 12.0, 0.0);
            bar.BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0);
            bar.CornerRadius = new CornerRadius(9.0, 9.0, 0.0, 0.0);
            bar.SetResourceReference(Border.BackgroundProperty, "BgCard");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧放大镜图标
            Border iconHost = new Border();
            iconHost.Width = 20.0;
            iconHost.Height = 20.0;
            iconHost.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
            iconHost.VerticalAlignment = VerticalAlignment.Center;
            _searchIcon = UiIcons.CreateIcon(UiIcons.Search, 15.0, "AccentText");
            iconHost.Child = _searchIcon;
            Grid.SetColumn(iconHost, 0);
            g.Children.Add(iconHost);

            // 中部无边框即时搜索输入框 + 水印
            Grid inputLayer = new Grid();
            inputLayer.VerticalAlignment = VerticalAlignment.Stretch;

            _watermarkText = new TextBlock();
            _watermarkText.Text = "输入命令、切换视图、筛选设备或工具...";
            _watermarkText.FontFamily = DesignSystem.FontUi;
            _watermarkText.FontSize = DesignSystem.TextLabel;
            _watermarkText.VerticalAlignment = VerticalAlignment.Center;
            _watermarkText.Margin = new Thickness(2.0, 0.0, 0.0, 0.0);
            _watermarkText.IsHitTestVisible = false;
            _watermarkText.TextTrimming = TextTrimming.CharacterEllipsis;
            _watermarkText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            inputLayer.Children.Add(_watermarkText);

            _searchBox = CreateBorderlessSearchBox();
            _searchBox.TextChanged += delegate(object sender, TextChangedEventArgs e)
            {
                UpdateSearchBarDecorations();
                if (_suppressTextChanged)
                    return;
                RebuildFilteredRows(_searchBox.Text);
            };
            _searchBox.GotKeyboardFocus += delegate(object sender, KeyboardFocusChangedEventArgs e)
            {
                _searchIcon.SetResourceReference(Shape.StrokeProperty, "AccentText");
            };
            _searchBox.LostKeyboardFocus += delegate(object sender, KeyboardFocusChangedEventArgs e)
            {
                _searchIcon.SetResourceReference(Shape.StrokeProperty, "FgSecondary");
            };
            inputLayer.Children.Add(_searchBox);

            Grid.SetColumn(inputLayer, 1);
            g.Children.Add(inputLayer);

            // 有搜索词时显示的快捷清空小按钮
            _clearQueryBtn = new Border();
            _clearQueryBtn.Width = 20.0;
            _clearQueryBtn.Height = 20.0;
            _clearQueryBtn.CornerRadius = new CornerRadius(4.0);
            _clearQueryBtn.Margin = new Thickness(6.0, 0.0, 6.0, 0.0);
            _clearQueryBtn.Background = Brushes.Transparent;
            _clearQueryBtn.VerticalAlignment = VerticalAlignment.Center;
            _clearQueryBtn.Cursor = Cursors.Hand;
            _clearQueryBtn.Visibility = Visibility.Collapsed;
            _clearQueryBtn.ToolTip = "清空搜索词";
            Path clearIcon = UiIcons.CreateIcon(ClearCrossIconPath, 10.0, "FgMuted");
            _clearQueryBtn.Child = clearIcon;
            _clearQueryBtn.MouseEnter += delegate(object sender, MouseEventArgs e)
            {
                _clearQueryBtn.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
                clearIcon.SetResourceReference(Shape.StrokeProperty, "FgPrimary");
            };
            _clearQueryBtn.MouseLeave += delegate(object sender, MouseEventArgs e)
            {
                _clearQueryBtn.Background = Brushes.Transparent;
                clearIcon.SetResourceReference(Shape.StrokeProperty, "FgMuted");
            };
            _clearQueryBtn.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _searchBox.Text = string.Empty;
                _searchBox.Focus();
                Keyboard.Focus(_searchBox);
            };
            Grid.SetColumn(_clearQueryBtn, 2);
            g.Children.Add(_clearQueryBtn);

            // 右侧 ESC 键帽徽章
            Border escCap = new Border();
            escCap.CornerRadius = new CornerRadius(4.0);
            escCap.Padding = new Thickness(6.0, 2.0, 6.0, 2.0);
            escCap.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
            escCap.BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.5);
            escCap.VerticalAlignment = VerticalAlignment.Center;
            escCap.Cursor = Cursors.Hand;
            escCap.ToolTip = "关闭命令面板 (Esc)";
            escCap.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            escCap.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            TextBlock escLabel = new TextBlock();
            escLabel.Text = "ESC";
            escLabel.FontFamily = DesignSystem.FontMono;
            escLabel.FontSize = DesignSystem.TextCaption;
            escLabel.FontWeight = FontWeights.SemiBold;
            escLabel.VerticalAlignment = VerticalAlignment.Center;
            escLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            escCap.Child = escLabel;

            escCap.MouseEnter += delegate(object sender, MouseEventArgs e)
            {
                escCap.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
                escLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            };
            escCap.MouseLeave += delegate(object sender, MouseEventArgs e)
            {
                escCap.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                escLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            };
            escCap.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Close();
            };

            Grid.SetColumn(escCap, 3);
            g.Children.Add(escCap);

            bar.Child = g;
            return bar;
        }

        private static TextBox CreateBorderlessSearchBox()
        {
            TextBox tb = new TextBox();
            tb.FontFamily = DesignSystem.FontUi;
            tb.FontSize = DesignSystem.TextLabel;
            tb.BorderThickness = new Thickness(0.0);
            tb.Background = Brushes.Transparent;
            tb.Padding = new Thickness(0.0);
            tb.Margin = new Thickness(0.0);
            tb.VerticalAlignment = VerticalAlignment.Stretch;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.AcceptsReturn = false;
            tb.AcceptsTab = false;
            tb.SelectionOpacity = 0.30;
            tb.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
            tb.SetResourceReference(TextBox.CaretBrushProperty, "AccentText");
            tb.SetResourceReference(TextBox.SelectionBrushProperty, "BtnPrimaryBg");

            ControlTemplate tpl = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory hostBorder = new FrameworkElementFactory(typeof(Border), "SearchHostBd");
            hostBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            hostBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0.0));
            hostBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory contentHost = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            contentHost.SetValue(FrameworkElement.MarginProperty, new Thickness(0.0));
            contentHost.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentHost.SetValue(Control.FocusableProperty, false);

            hostBorder.AppendChild(contentHost);
            tpl.VisualTree = hostBorder;
            tb.Template = tpl;

            return tb;
        }

        private ScrollViewer BuildCommandListArea()
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Focusable = false;
            sv.Padding = new Thickness(6.0, 6.0, 6.0, 6.0);
            sv.CanContentScroll = false;

            _itemsPanel = new StackPanel();
            _itemsPanel.Orientation = Orientation.Vertical;
            sv.Content = _itemsPanel;

            return sv;
        }

        private Border BuildFooterHintBar()
        {
            Border footer = new Border();
            footer.Height = 28.0;
            footer.Padding = new Thickness(14.0, 0.0, 14.0, 0.0);
            footer.BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0);
            footer.CornerRadius = new CornerRadius(0.0, 0.0, 9.0, 9.0);
            footer.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock hints = new TextBlock();
            hints.Text = "↑↓ 选择  ·  ↵ 执行  ·  Esc 关闭";
            hints.FontFamily = DesignSystem.FontUi;
            hints.FontSize = DesignSystem.TextCaption;
            hints.VerticalAlignment = VerticalAlignment.Center;
            hints.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(hints, 0);
            g.Children.Add(hints);

            _footerCountText = new TextBlock();
            _footerCountText.FontFamily = DesignSystem.FontMono;
            _footerCountText.FontSize = DesignSystem.TextCaption;
            _footerCountText.VerticalAlignment = VerticalAlignment.Center;
            _footerCountText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(_footerCountText, 1);
            g.Children.Add(_footerCountText);

            footer.Child = g;
            return footer;
        }

        private void UpdateSearchBarDecorations()
        {
            bool hasQuery = !string.IsNullOrEmpty(_searchBox.Text);
            _watermarkText.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
            _clearQueryBtn.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PlayOpenEntranceTransition()
        {
            if (Application.Current == null)
            {
                _cardWrapper.Opacity = 1.0;
                _cardScale.ScaleX = 1.0;
                _cardScale.ScaleY = 1.0;
                return;
            }

            try
            {
                Duration dur = new Duration(TimeSpan.FromMilliseconds(110.0));
                CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                DoubleAnimation fadeAnim = new DoubleAnimation(0.0, 1.0, dur);
                fadeAnim.EasingFunction = ease;
                _cardWrapper.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

                DoubleAnimation scaleAnim = new DoubleAnimation(0.98, 1.0, dur);
                scaleAnim.EasingFunction = ease;
                _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
            catch
            {
                _cardWrapper.Opacity = 1.0;
                _cardScale.ScaleX = 1.0;
                _cardScale.ScaleY = 1.0;
            }
        }

        private void FocusSearchInputImmediateAndDeferred()
        {
            try
            {
                _searchBox.Focus();
                Keyboard.Focus(_searchBox);
            }
            catch { }

            if (Dispatcher != null)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(delegate
                    {
                        if (!_isOpen)
                            return;
                        try
                        {
                            _searchBox.Focus();
                            Keyboard.Focus(_searchBox);
                            _searchBox.CaretIndex = _searchBox.Text != null ? _searchBox.Text.Length : 0;
                        }
                        catch { }
                    }));
            }
        }

        // -------------------- 索引预构建与 0ms 中英文/首字母即时过滤 --------------------

        private void IndexCommandItems(List<PaletteCommandItem> items)
        {
            _indexedEntries.Clear();
            if (items == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                PaletteCommandItem item = items[i];
                if (item == null)
                    continue;

                string cat = string.IsNullOrEmpty(item.Category) ? "快捷操作" : item.Category.Trim();
                string title = item.Title ?? string.Empty;
                string sub = item.Subtitle ?? string.Empty;
                string shortcut = item.ShortcutText ?? string.Empty;
                string id = item.Id ?? string.Empty;

                IndexedEntry entry = new IndexedEntry();
                entry.Item = item;
                entry.OriginalOrder = i;
                entry.CategoryDisplay = cat;
                entry.TitleLower = title.ToLowerInvariant();
                entry.SubtitleLower = sub.ToLowerInvariant();
                entry.CategoryLower = cat.ToLowerInvariant();
                entry.ShortcutLower = shortcut.ToLowerInvariant();
                entry.ShortcutCompactLower = entry.ShortcutLower.Replace("+", string.Empty).Replace(" ", string.Empty);
                entry.IdLower = id.ToLowerInvariant();
                entry.TitleInitials = ExtractMixedInitials(title);
                entry.FullInitials = ExtractMixedInitials(cat + " " + title + " " + sub);

                _indexedEntries.Add(entry);
            }
        }

        private void RebuildFilteredRows(string rawQuery)
        {
            _itemsPanel.Children.Clear();
            _rowVisuals.Clear();
            _selectedIndex = -1;
            _mousePressedRowIndex = -1;

            string trimmedQuery = rawQuery != null ? rawQuery.Trim() : string.Empty;
            bool hasQuery = trimmedQuery.Length > 0;

            List<CategoryBucket> buckets = FilterAndGroupEntries(trimmedQuery);

            if (buckets.Count == 0)
            {
                _itemsPanel.Children.Add(BuildEmptyStateElement(trimmedQuery));
                UpdateFooterCount(0, _indexedEntries.Count, hasQuery);
                _listScroll.ScrollToTop();
                return;
            }

            int visualIndex = 0;
            for (int b = 0; b < buckets.Count; b++)
            {
                CategoryBucket bucket = buckets[b];
                if (!string.IsNullOrEmpty(bucket.CategoryName))
                {
                    TextBlock catHeader = BuildCategoryHeader(bucket.CategoryName, b == 0);
                    _itemsPanel.Children.Add(catHeader);
                }

                for (int j = 0; j < bucket.Entries.Count; j++)
                {
                    PaletteCommandItem cmd = bucket.Entries[j].Entry.Item;
                    RowVisual rv = BuildCommandRow(cmd, visualIndex);
                    _rowVisuals.Add(rv);
                    _itemsPanel.Children.Add(rv.RowBorder);
                    visualIndex++;
                }
            }

            UpdateFooterCount(_rowVisuals.Count, _indexedEntries.Count, hasQuery);

            if (_rowVisuals.Count > 0)
            {
                SetSelectedIndex(0, false);
            }
            _listScroll.ScrollToTop();
        }

        private List<CategoryBucket> FilterAndGroupEntries(string trimmedQuery)
        {
            List<CategoryBucket> buckets = new List<CategoryBucket>();
            Dictionary<string, CategoryBucket> bucketMap = new Dictionary<string, CategoryBucket>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(trimmedQuery))
            {
                for (int i = 0; i < _indexedEntries.Count; i++)
                {
                    IndexedEntry entry = _indexedEntries[i];
                    CategoryBucket bucket;
                    if (!bucketMap.TryGetValue(entry.CategoryDisplay, out bucket))
                    {
                        bucket = new CategoryBucket();
                        bucket.CategoryName = entry.CategoryDisplay;
                        bucket.BestScore = 0;
                        bucket.FirstOrder = entry.OriginalOrder;
                        bucketMap[entry.CategoryDisplay] = bucket;
                        buckets.Add(bucket);
                    }
                    ScoredEntry se = new ScoredEntry();
                    se.Entry = entry;
                    se.Score = 0;
                    bucket.Entries.Add(se);
                }
                return buckets;
            }

            string[] tokens = trimmedQuery.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int t = 0; t < tokens.Length; t++)
            {
                tokens[t] = tokens[t].ToLowerInvariant();
            }

            for (int i = 0; i < _indexedEntries.Count; i++)
            {
                IndexedEntry entry = _indexedEntries[i];
                int totalScore = 0;
                bool matchedAll = true;

                for (int t = 0; t < tokens.Length; t++)
                {
                    int tokenScore = ScoreSingleToken(entry, tokens[t]);
                    if (tokenScore <= 0)
                    {
                        matchedAll = false;
                        break;
                    }
                    totalScore += tokenScore;
                }

                if (!matchedAll)
                    continue;

                CategoryBucket bucket;
                if (!bucketMap.TryGetValue(entry.CategoryDisplay, out bucket))
                {
                    bucket = new CategoryBucket();
                    bucket.CategoryName = entry.CategoryDisplay;
                    bucket.BestScore = totalScore;
                    bucket.FirstOrder = entry.OriginalOrder;
                    bucketMap[entry.CategoryDisplay] = bucket;
                    buckets.Add(bucket);
                }
                else if (totalScore > bucket.BestScore)
                {
                    bucket.BestScore = totalScore;
                }

                ScoredEntry se = new ScoredEntry();
                se.Entry = entry;
                se.Score = totalScore;
                bucket.Entries.Add(se);
            }

            // 组内按匹配相关度降序（同分保留原始顺序）
            for (int b = 0; b < buckets.Count; b++)
            {
                buckets[b].Entries.Sort(delegate(ScoredEntry a, ScoredEntry c)
                {
                    int cmp = c.Score.CompareTo(a.Score);
                    if (cmp != 0)
                        return cmp;
                    return a.Entry.OriginalOrder.CompareTo(c.Entry.OriginalOrder);
                });
            }

            // 分组间按该组最高命中得分降序排列，确保最匹配的命令永远排在面板第 0 位
            buckets.Sort(delegate(CategoryBucket a, CategoryBucket c)
            {
                int cmp = c.BestScore.CompareTo(a.BestScore);
                if (cmp != 0)
                    return cmp;
                return a.FirstOrder.CompareTo(c.FirstOrder);
            });

            return buckets;
        }

        private static int ScoreSingleToken(IndexedEntry entry, string token)
        {
            if (string.IsNullOrEmpty(token))
                return 1;

            // 1. 主标题精确相等或前缀匹配
            if (string.Equals(entry.TitleLower, token, StringComparison.Ordinal))
                return 1200;
            if (entry.TitleLower.StartsWith(token, StringComparison.Ordinal))
                return 1000;

            // 2. 主标题子串命中
            int titleIdx = entry.TitleLower.IndexOf(token, StringComparison.Ordinal);
            if (titleIdx >= 0)
                return 860 - Math.Min(titleIdx * 4, 120);

            // 3. 快捷键精确/紧凑命中（如 "f5", "ctrl+1", "ctrl1"）
            if (!string.IsNullOrEmpty(entry.ShortcutLower))
            {
                if (string.Equals(entry.ShortcutLower, token, StringComparison.Ordinal) ||
                    string.Equals(entry.ShortcutCompactLower, token, StringComparison.Ordinal))
                {
                    return 820;
                }
                if (entry.ShortcutLower.IndexOf(token, StringComparison.Ordinal) >= 0 ||
                    entry.ShortcutCompactLower.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    return 680;
                }
            }

            // 4. 主标题中英文/拼音首字母匹配（如 "sx" -> 刷新/筛选, "cc" -> Claude Code）
            if (!string.IsNullOrEmpty(entry.TitleInitials))
            {
                if (entry.TitleInitials.StartsWith(token, StringComparison.Ordinal))
                    return 780;
                int initIdx = entry.TitleInitials.IndexOf(token, StringComparison.Ordinal);
                if (initIdx >= 0)
                    return 700 - Math.Min(initIdx * 5, 80);
            }

            // 5. 副标题或分组标题子串命中
            int subIdx = entry.SubtitleLower.IndexOf(token, StringComparison.Ordinal);
            if (subIdx >= 0)
                return 560 - Math.Min(subIdx * 2, 80);

            int catIdx = entry.CategoryLower.IndexOf(token, StringComparison.Ordinal);
            if (catIdx >= 0)
                return 500;

            // 6. 命令 ID 英文标识命中（方便输入英文搜中文命令）
            if (!string.IsNullOrEmpty(entry.IdLower) &&
                entry.IdLower.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                return 460;
            }

            // 7. 全字段首字母子串或主标题紧凑顺序子序列命中
            if (!string.IsNullOrEmpty(entry.FullInitials) &&
                entry.FullInitials.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                return 400;
            }

            if (token.Length >= 2 && IsOrderedSubsequence(entry.TitleLower, token))
                return 260;

            if (token.Length >= 2 && IsOrderedSubsequence(entry.TitleInitials, token))
                return 220;

            return 0;
        }

        private static bool IsOrderedSubsequence(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
                return false;
            if (pattern.Length > text.Length)
                return false;

            int p = 0;
            for (int i = 0; i < text.Length && p < pattern.Length; i++)
            {
                if (text[i] == pattern[p])
                    p++;
            }
            return p == pattern.Length;
        }

        // -------------------- 列表视觉元素构建与 O(1) 高亮刷新 --------------------

        private static TextBlock BuildCategoryHeader(string categoryName, bool isFirst)
        {
            TextBlock tb = new TextBlock();
            tb.Text = categoryName;
            tb.FontFamily = DesignSystem.FontUi;
            tb.FontSize = DesignSystem.TextCaption;
            tb.FontWeight = FontWeights.SemiBold;
            tb.Padding = new Thickness(10.0, isFirst ? 4.0 : 10.0, 10.0, 4.0);
            tb.IsHitTestVisible = false;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            return tb;
        }

        private RowVisual BuildCommandRow(PaletteCommandItem item, int rowIndex)
        {
            RowVisual rv = new RowVisual();
            rv.Item = item;
            rv.Index = rowIndex;

            bool hasSubtitle = !string.IsNullOrEmpty(item.Subtitle);

            Border rowBd = new Border();
            rowBd.CornerRadius = new CornerRadius(6.0);
            rowBd.BorderThickness = new Thickness(1.0);
            rowBd.BorderBrush = Brushes.Transparent;
            rowBd.Background = Brushes.Transparent;
            rowBd.MinHeight = hasSubtitle ? 44.0 : 36.0;
            rowBd.Padding = new Thickness(8.0, hasSubtitle ? 6.0 : 5.0, 10.0, hasSubtitle ? 6.0 : 5.0);
            rowBd.Margin = new Thickness(0.0, 1.0, 0.0, 1.0);
            rowBd.Cursor = Cursors.Hand;
            rowBd.SnapsToDevicePixels = true;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: 左侧 Linear 风格选中微指示条 (BtnPrimaryBg)
            Border accentBar = new Border();
            accentBar.Width = 2.5;
            accentBar.Height = 16.0;
            accentBar.CornerRadius = new CornerRadius(1.5);
            accentBar.Margin = new Thickness(0.0, 0.0, 7.0, 0.0);
            accentBar.VerticalAlignment = VerticalAlignment.Center;
            accentBar.Opacity = 0.0;
            accentBar.SetResourceReference(Border.BackgroundProperty, "BtnPrimaryBg");
            Grid.SetColumn(accentBar, 0);
            g.Children.Add(accentBar);

            // Col 1: 左侧 14px SVG 图标徽章容器
            Border iconWrap = new Border();
            iconWrap.Width = 24.0;
            iconWrap.Height = 24.0;
            iconWrap.CornerRadius = new CornerRadius(5.0);
            iconWrap.Margin = new Thickness(0.0, 0.0, 10.0, 0.0);
            iconWrap.VerticalAlignment = VerticalAlignment.Center;
            iconWrap.Background = Brushes.Transparent;

            Path iconPath = CreateSafeSvgIcon(item.IconPath, 14.0, "FgSecondary");
            iconWrap.Child = iconPath;
            Grid.SetColumn(iconWrap, 1);
            g.Children.Add(iconWrap);

            // Col 2: 主标题与副标题
            StackPanel textStack = new StackPanel();
            textStack.Orientation = Orientation.Vertical;
            textStack.VerticalAlignment = VerticalAlignment.Center;

            TextBlock titleTb = new TextBlock();
            titleTb.Text = string.IsNullOrEmpty(item.Title) ? "(未命名命令)" : item.Title;
            titleTb.FontFamily = DesignSystem.FontUi;
            titleTb.FontSize = DesignSystem.TextLabel;
            titleTb.FontWeight = FontWeights.Medium;
            titleTb.TextTrimming = TextTrimming.CharacterEllipsis;
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            textStack.Children.Add(titleTb);

            TextBlock subTb = null;
            if (hasSubtitle)
            {
                subTb = new TextBlock();
                subTb.Text = item.Subtitle;
                subTb.FontFamily = DesignSystem.FontUi;
                subTb.FontSize = DesignSystem.TextCaption;
                subTb.Margin = new Thickness(0.0, 1.5, 0.0, 0.0);
                subTb.TextTrimming = TextTrimming.CharacterEllipsis;
                subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                textStack.Children.Add(subTb);
            }

            Grid.SetColumn(textStack, 2);
            g.Children.Add(textStack);

            // Col 3: 右侧等宽快捷键键帽胶囊（如 Ctrl+1、F5、Ctrl+M）
            Border shortcutBadge = null;
            TextBlock shortcutLabel = null;
            if (!string.IsNullOrEmpty(item.ShortcutText))
            {
                shortcutBadge = new Border();
                shortcutBadge.CornerRadius = new CornerRadius(4.0);
                shortcutBadge.Padding = new Thickness(6.5, 2.0, 6.5, 2.0);
                shortcutBadge.Margin = new Thickness(12.0, 0.0, 0.0, 0.0);
                shortcutBadge.BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.5);
                shortcutBadge.VerticalAlignment = VerticalAlignment.Center;
                shortcutBadge.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                shortcutBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

                shortcutLabel = new TextBlock();
                shortcutLabel.Text = item.ShortcutText;
                shortcutLabel.FontFamily = DesignSystem.FontMono;
                shortcutLabel.FontSize = DesignSystem.TextCaption;
                shortcutLabel.VerticalAlignment = VerticalAlignment.Center;
                shortcutLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                shortcutBadge.Child = shortcutLabel;

                Grid.SetColumn(shortcutBadge, 3);
                g.Children.Add(shortcutBadge);
            }

            rowBd.Child = g;

            rv.RowBorder = rowBd;
            rv.AccentBar = accentBar;
            rv.IconWrap = iconWrap;
            rv.IconPath = iconPath;
            rv.TitleBlock = titleTb;
            rv.SubtitleBlock = subTb;
            rv.ShortcutBadge = shortcutBadge;
            rv.ShortcutLabel = shortcutLabel;

            int capturedIndex = rowIndex;
            rowBd.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                Point pos = e.GetPosition(this);
                if (HasMouseActuallyMoved(pos))
                {
                    _lastMousePosition = pos;
                    if (_selectedIndex != capturedIndex)
                    {
                        SetSelectedIndex(capturedIndex, false);
                    }
                }
            };
            rowBd.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _mousePressedRowIndex = capturedIndex;
                SetSelectedIndex(capturedIndex, false);
            };
            rowBd.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (_mousePressedRowIndex == capturedIndex || _mousePressedRowIndex == -1)
                {
                    _mousePressedRowIndex = -1;
                    SetSelectedIndex(capturedIndex, false);
                    ExecuteRowAt(capturedIndex);
                }
                else
                {
                    _mousePressedRowIndex = -1;
                }
            };

            return rv;
        }

        private static UIElement BuildEmptyStateElement(string query)
        {
            StackPanel wrap = new StackPanel();
            wrap.Orientation = Orientation.Vertical;
            wrap.HorizontalAlignment = HorizontalAlignment.Center;
            wrap.Margin = new Thickness(16.0, 32.0, 16.0, 32.0);

            Border iconCircle = new Border();
            iconCircle.Width = 36.0;
            iconCircle.Height = 36.0;
            iconCircle.CornerRadius = new CornerRadius(18.0);
            iconCircle.HorizontalAlignment = HorizontalAlignment.Center;
            iconCircle.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
            iconCircle.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            iconCircle.Child = UiIcons.CreateIcon(UiIcons.Search, 16.0, "FgMuted");
            wrap.Children.Add(iconCircle);

            TextBlock title = new TextBlock();
            title.Text = string.IsNullOrEmpty(query)
                ? "暂无可用命令"
                : "未找到与 \"" + query + "\" 匹配的命令";
            title.FontFamily = DesignSystem.FontUi;
            title.FontSize = DesignSystem.TextLabel;
            title.FontWeight = FontWeights.Medium;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.MaxWidth = 460.0;
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            wrap.Children.Add(title);

            TextBlock hint = new TextBlock();
            hint.Text = "试试输入中文关键词、拼音/英文首字母（如 sx、mx）或快捷键（如 F5）";
            hint.FontFamily = DesignSystem.FontUi;
            hint.FontSize = DesignSystem.TextCaption;
            hint.Margin = new Thickness(0.0, 4.0, 0.0, 0.0);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            wrap.Children.Add(hint);

            return wrap;
        }

        private static Path CreateSafeSvgIcon(string iconPathData, double size, string resourceKey)
        {
            string data = string.IsNullOrEmpty(iconPathData) ? DefaultItemIconPath : iconPathData;
            try
            {
                return UiIcons.CreateIcon(data, size, resourceKey);
            }
            catch
            {
                return UiIcons.CreateIcon(DefaultItemIconPath, size, resourceKey);
            }
        }

        private void UpdateFooterCount(int visibleCount, int totalCount, bool hasQuery)
        {
            if (_footerCountText == null)
                return;

            if (hasQuery)
            {
                _footerCountText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} / {1} 项",
                    visibleCount,
                    totalCount);
            }
            else
            {
                _footerCountText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "共 {0} 项命令",
                    totalCount);
            }
        }

        private void SetSelectedIndex(int newIndex, bool scrollIntoView)
        {
            if (_rowVisuals.Count == 0)
            {
                _selectedIndex = -1;
                return;
            }

            if (newIndex < 0)
                newIndex = 0;
            if (newIndex >= _rowVisuals.Count)
                newIndex = _rowVisuals.Count - 1;

            if (_selectedIndex == newIndex)
            {
                if (scrollIntoView)
                    BringRowIntoView(newIndex);
                return;
            }

            int prevIndex = _selectedIndex;
            _selectedIndex = newIndex;

            if (prevIndex >= 0 && prevIndex < _rowVisuals.Count)
            {
                ApplyRowVisualState(_rowVisuals[prevIndex], false);
            }
            if (_selectedIndex >= 0 && _selectedIndex < _rowVisuals.Count)
            {
                ApplyRowVisualState(_rowVisuals[_selectedIndex], true);
                if (scrollIntoView)
                {
                    BringRowIntoView(_selectedIndex);
                }
            }
        }

        private static void ApplyRowVisualState(RowVisual rv, bool isSelected)
        {
            if (isSelected)
            {
                rv.RowBorder.SetResourceReference(Border.BackgroundProperty, "BgHoverStrong");
                rv.RowBorder.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
                rv.AccentBar.Opacity = 1.0;
                rv.IconWrap.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                rv.IconPath.SetResourceReference(Shape.StrokeProperty, "AccentText");
                rv.TitleBlock.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                if (rv.SubtitleBlock != null)
                {
                    rv.SubtitleBlock.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                }
                if (rv.ShortcutBadge != null)
                {
                    rv.ShortcutBadge.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                    rv.ShortcutBadge.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
                }
                if (rv.ShortcutLabel != null)
                {
                    rv.ShortcutLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                }
            }
            else
            {
                rv.RowBorder.Background = Brushes.Transparent;
                rv.RowBorder.BorderBrush = Brushes.Transparent;
                rv.AccentBar.Opacity = 0.0;
                rv.IconWrap.Background = Brushes.Transparent;
                rv.IconPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary");
                rv.TitleBlock.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                if (rv.SubtitleBlock != null)
                {
                    rv.SubtitleBlock.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                }
                if (rv.ShortcutBadge != null)
                {
                    rv.ShortcutBadge.SetResourceReference(Border.BackgroundProperty, "BgElevated");
                    rv.ShortcutBadge.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
                }
                if (rv.ShortcutLabel != null)
                {
                    rv.ShortcutLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                }
            }
        }

        private void BringRowIntoView(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rowVisuals.Count)
                return;

            if (rowIndex == 0)
            {
                _listScroll.ScrollToTop();
            }
            else
            {
                _rowVisuals[rowIndex].RowBorder.BringIntoView();
            }
        }

        // -------------------- 键盘与鼠标事件路由处理 --------------------

        private void OnOverlayPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isOpen)
                return;
            Point pos = e.GetPosition(this);
            if (_lastMousePosition.X < -1000.0)
            {
                _lastMousePosition = pos;
            }
        }

        private bool HasMouseActuallyMoved(Point currentPos)
        {
            if (_lastMousePosition.X < -1000.0)
                return true;
            return Math.Abs(currentPos.X - _lastMousePosition.X) > 0.5 ||
                   Math.Abs(currentPos.Y - _lastMousePosition.Y) > 0.5;
        }

        private void OnOverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isOpen)
                return;

            // 中文输入法候选框处于激活态时放行按键，确保拼音选词上屏不被打断
            if (e.Key == Key.ImeProcessed)
                return;

            ModifierKeys mods = Keyboard.Modifiers;

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (e.Key == Key.K && (mods & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                bool shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;
                MoveSelectionCyclic(shift ? -1 : 1);
                return;
            }

            if (e.Key == Key.Down || (e.Key == Key.N && mods == ModifierKeys.Control))
            {
                e.Handled = true;
                MoveSelectionCyclic(1);
                return;
            }

            if (e.Key == Key.Up || (e.Key == Key.P && mods == ModifierKeys.Control))
            {
                e.Handled = true;
                MoveSelectionCyclic(-1);
                return;
            }

            if (e.Key == Key.PageDown)
            {
                e.Handled = true;
                MoveSelectionClamped(6);
                return;
            }

            if (e.Key == Key.PageUp)
            {
                e.Handled = true;
                MoveSelectionClamped(-6);
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_selectedIndex >= 0 && _selectedIndex < _rowVisuals.Count)
                {
                    ExecuteRowAt(_selectedIndex);
                }
                return;
            }
        }

        private void MoveSelectionCyclic(int delta)
        {
            int count = _rowVisuals.Count;
            if (count <= 0)
                return;

            int current = _selectedIndex;
            if (current < 0 || current >= count)
            {
                current = 0;
            }
            else
            {
                current = (current + delta) % count;
                if (current < 0)
                    current += count;
            }

            SetSelectedIndex(current, true);
        }

        private void MoveSelectionClamped(int delta)
        {
            int count = _rowVisuals.Count;
            if (count <= 0)
                return;

            int target = (_selectedIndex < 0 ? 0 : _selectedIndex) + delta;
            if (target < 0)
                target = 0;
            if (target >= count)
                target = count - 1;

            SetSelectedIndex(target, true);
        }

        private void ExecuteRowAt(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rowVisuals.Count)
                return;

            PaletteCommandItem item = _rowVisuals[rowIndex].Item;
            Action callback = item != null ? item.Execute : null;

            // 先关闭命令面板释放遮罩与焦点，再执行目标命令回调
            Close();

            if (callback != null)
            {
                callback();
            }
        }

        // -------------------- 中英文首字母与拼音声母提取工具 --------------------

        private static Encoding TryGetGbkEncoding()
        {
            try
            {
                return Encoding.GetEncoding(936);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<char, char> BuildSpecialPinyinMap()
        {
            Dictionary<char, char> m = new Dictionary<char, char>();
            // 针对多音字与桌面高频术语字提供精准首字母覆盖
            m['重'] = 'c'; // 重置 (chong)
            m['调'] = 't'; // 调整/调试 (tiao)
            m['行'] = 'h'; // 排行/行 (hang)
            m['率'] = 'l'; // 汇率/命中率 (lv)
            m['藏'] = 'c'; // 隐藏 (cang)
            m['长'] = 'c'; // 时长 (chang)
            m['单'] = 'd'; // 账单 (dan)
            m['校'] = 'j'; // 校准 (jiao)
            m['阈'] = 'y'; // 阈值 (yu)
            m['浏'] = 'l'; // 浏览 (liu)
            m['览'] = 'l'; // 概览 (lan)
            m['筛'] = 's'; // 筛选 (shai)
            m['帧'] = 'z'; // 帧率 (zhen)
            return m;
        }

        private static string ExtractMixedInitials(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            StringBuilder sb = new StringBuilder(text.Length);
            bool prevIsAsciiAlphaNum = false;
            bool prevIsAsciiLower = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                if (ch >= '\u4e00' && ch <= '\u9fa5')
                {
                    char py = GetChineseInitial(ch);
                    if (py != '\0')
                    {
                        sb.Append(py);
                    }
                    prevIsAsciiAlphaNum = false;
                    prevIsAsciiLower = false;
                }
                else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    bool isUpper = ch >= 'A' && ch <= 'Z';
                    bool isLower = ch >= 'a' && ch <= 'z';

                    // 提取英文词首或驼峰大写边界首字母（例如 "Claude Code" -> "cc", "OpenClaw" -> "oc"）
                    if (!prevIsAsciiAlphaNum || (isUpper && prevIsAsciiLower))
                    {
                        sb.Append(char.ToLowerInvariant(ch));
                    }

                    prevIsAsciiAlphaNum = true;
                    prevIsAsciiLower = isLower;
                }
                else
                {
                    prevIsAsciiAlphaNum = false;
                    prevIsAsciiLower = false;
                }
            }

            return sb.ToString();
        }

        private static char GetChineseInitial(char ch)
        {
            char mapped;
            if (SpecialPinyinMap.TryGetValue(ch, out mapped))
                return mapped;

            if (GbkEncoding == null)
                return '\0';

            byte[] bytes = GbkEncoding.GetBytes(new char[] { ch });
            if (bytes == null || bytes.Length < 2)
                return '\0';

            int code = ((int)bytes[0] << 8) + (int)bytes[1];
            if (code < 0xB0A1 || code > 0xD7F9)
                return '\0';

            if (code <= 0xB0C4) return 'a';
            if (code <= 0xB2C0) return 'b';
            if (code <= 0xB4ED) return 'c';
            if (code <= 0xB6E9) return 'd';
            if (code <= 0xB7A1) return 'e';
            if (code <= 0xB8C0) return 'f';
            if (code <= 0xB9FD) return 'g';
            if (code <= 0xBBF6) return 'h';
            if (code <= 0xBFA5) return 'j';
            if (code <= 0xC0AB) return 'k';
            if (code <= 0xC2E7) return 'l';
            if (code <= 0xC4C2) return 'm';
            if (code <= 0xC5B5) return 'n';
            if (code <= 0xC5BD) return 'o';
            if (code <= 0xC6D9) return 'p';
            if (code <= 0xC8BA) return 'q';
            if (code <= 0xC8F5) return 'r';
            if (code <= 0xCBF9) return 's';
            if (code <= 0xCDD9) return 't';
            if (code <= 0xCEF3) return 'w';
            if (code <= 0xD1B8) return 'x';
            if (code <= 0xD4D0) return 'y';
            return 'z';
        }
    }
}
