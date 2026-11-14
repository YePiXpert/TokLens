using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace TokLens
{
    public sealed class SyncSettingsCallbacks
    {
        public Action<SyncOutcome> OnSynced;
        public Action OnThemeOrCurrencyChanged;
        public Action OnFeaturesChanged;
        public Action<string> OnHomeDirectoryChanged;
        public Action OnForceRefreshPricing;
        public Action OnForceRefreshFx;
        public Action<string, string, string> ShowToast;
    }

    public sealed class SyncSettingsView : Border
    {
        private sealed class ModernSecretField
        {
            public Border Root;
            private readonly PasswordBox _pwdBox;
            private readonly TextBox _plainBox;
            private readonly Button _revealBtn;
            private bool _revealed = false;
            private bool _internalSync = false;

            public event Action ValueChanged;

            public ModernSecretField(string placeholderTip)
            {
                Root = new Border();
                Root.Height = 32;
                Root.CornerRadius = new CornerRadius(6);
                Root.BorderThickness = new Thickness(1);
                Root.Padding = new Thickness(8, 0, 4, 0);
                Root.SnapsToDevicePixels = true;
                Root.SetResourceReference(Border.BackgroundProperty, "BgInput");
                Root.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
                if (!string.IsNullOrEmpty(placeholderTip))
                    Root.ToolTip = placeholderTip;

                DockPanel dock = new DockPanel();

                _revealBtn = ModernControlsFactory.CreateButton("显示", "ghost");
                _revealBtn.Height = 22;
                _revealBtn.FontSize = DesignSystem.TextCaption;
                _revealBtn.Padding = new Thickness(7, 0, 7, 0);
                _revealBtn.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(_revealBtn, Dock.Right);
                _revealBtn.Click += OnToggleRevealClick;
                dock.Children.Add(_revealBtn);

                Grid host = new Grid();

                _pwdBox = new PasswordBox();
                _pwdBox.FontFamily = DesignSystem.FontMono;
                _pwdBox.FontSize = DesignSystem.TextBody;
                _pwdBox.BorderThickness = new Thickness(0);
                _pwdBox.Background = Brushes.Transparent;
                _pwdBox.VerticalAlignment = VerticalAlignment.Center;
                _pwdBox.VerticalContentAlignment = VerticalAlignment.Center;
                _pwdBox.SetResourceReference(PasswordBox.ForegroundProperty, "FgPrimary");
                _pwdBox.PasswordChanged += OnPwdChanged;
                _pwdBox.GotKeyboardFocus += OnChildGotFocus;
                _pwdBox.LostKeyboardFocus += OnChildLostFocus;

                _plainBox = new TextBox();
                _plainBox.FontFamily = DesignSystem.FontMono;
                _plainBox.FontSize = DesignSystem.TextBody;
                _plainBox.BorderThickness = new Thickness(0);
                _plainBox.Background = Brushes.Transparent;
                _plainBox.VerticalAlignment = VerticalAlignment.Center;
                _plainBox.VerticalContentAlignment = VerticalAlignment.Center;
                _plainBox.Visibility = Visibility.Collapsed;
                _plainBox.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
                _plainBox.SetResourceReference(TextBox.CaretBrushProperty, "AccentPrimary");
                _plainBox.TextChanged += OnPlainChanged;
                _plainBox.GotKeyboardFocus += OnChildGotFocus;
                _plainBox.LostKeyboardFocus += OnChildLostFocus;

                host.Children.Add(_pwdBox);
                host.Children.Add(_plainBox);
                dock.Children.Add(host);
                Root.Child = dock;
            }

            private void OnChildGotFocus(object sender, KeyboardFocusChangedEventArgs e)
            {
                Root.SetResourceReference(Border.BorderBrushProperty, "BorderFocus");
            }

            private void OnChildLostFocus(object sender, KeyboardFocusChangedEventArgs e)
            {
                Root.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            }

            private void OnToggleRevealClick(object sender, RoutedEventArgs e)
            {
                _revealed = !_revealed;
                _revealBtn.Content = _revealed ? "隐藏" : "显示";
                if (_revealed)
                {
                    _internalSync = true;
                    _plainBox.Text = _pwdBox.Password ?? "";
                    _internalSync = false;
                    _pwdBox.Visibility = Visibility.Collapsed;
                    _plainBox.Visibility = Visibility.Visible;
                    _plainBox.Focus();
                    _plainBox.CaretIndex = _plainBox.Text.Length;
                }
                else
                {
                    _internalSync = true;
                    _pwdBox.Password = _plainBox.Text ?? "";
                    _internalSync = false;
                    _plainBox.Visibility = Visibility.Collapsed;
                    _pwdBox.Visibility = Visibility.Visible;
                    _pwdBox.Focus();
                }
            }

            private void OnPwdChanged(object sender, RoutedEventArgs e)
            {
                if (_internalSync) return;
                if (_revealed)
                {
                    _internalSync = true;
                    _plainBox.Text = _pwdBox.Password ?? "";
                    _internalSync = false;
                }
                if (ValueChanged != null) ValueChanged();
            }

            private void OnPlainChanged(object sender, TextChangedEventArgs e)
            {
                if (_internalSync) return;
                if (_revealed)
                {
                    _internalSync = true;
                    _pwdBox.Password = _plainBox.Text ?? "";
                    _internalSync = false;
                }
                if (ValueChanged != null) ValueChanged();
            }

            public string Value
            {
                get { return _revealed ? (_plainBox.Text ?? "") : (_pwdBox.Password ?? ""); }
                set
                {
                    _internalSync = true;
                    string v = value ?? "";
                    _pwdBox.Password = v;
                    _plainBox.Text = v;
                    _internalSync = false;
                }
            }
        }

        private readonly Window _hostWindow;
        private readonly SyncConfig _liveCfg;
        private SyncConfig _workingCfg;
        private readonly Func<ScanResult> _getLatestScan;
        private readonly Func<string> _getCurrentHomeDir;
        private readonly SyncSettingsCallbacks _callbacks;

        private bool _suppressUiEvents = false;
        private bool _isWebDav = false;
        private bool _syncBusy = false;
        private int _probeRequestSeq = 0;
        private GitHubProbeResult _lastProbeResult = null;
        private DispatcherTimer _tokenProbeDebounceTimer;

        // Top anchor bar & statusbanner
        private Button[] _sectionAnchorBtns;
        private ScrollViewer _mainScroll;
        private Border _secCloudCard;
        private Border _secOnboardCard;
        private Border _secPrefsCard;
        private Border _secHealthCard;

        private Border _inlineBannerBorder;
        private Shapes.Path _inlineBannerIcon;
        private TextBlock _inlineBannerText;

        // Card 1: Cloud Sync & GitHub Identity
        private Button[] _providerBtns;
        private StackPanel _gistSectionPanel;
        private StackPanel _webdavSectionPanel;

        private Border _ghIdentityCard;
        private Border _ghAuthStatusPill;
        private TextBlock _ghAuthStatusLabel;
        private Shapes.Ellipse _ghAuthStatusDot;
        private TextBlock _ghAccountHandleText;
        private TextBlock _ghFingerprintText;
        private TextBlock _ghTokenKindText;
        private Border _ghScopeFixBox;
        private TextBlock _ghScopeFixText;
        private Border _ghOAuthTipBox;
        private Button _ghReadCliBtn;
        private Popup _ghAccountPopover;

        private ModernSecretField _gistTokenField;
        private TextBox _gistIdBox;
        private Button _autoGistBtn;
        private Button _openGistWebBtn;
        private Button _pickGistCandidateBtn;
        private Popup _gistCandidatesPopover;
        private WrapPanel _topologyNodesWrap;
        private TextBlock _topologySummaryText;

        private TextBox _davUrlBox;
        private TextBox _davUserBox;
        private ModernSecretField _davPassField;

        private TextBox _machineNameBox;
        private TextBox _machineIdBox;
        private CheckBox _autoSyncSwitch;
        private Button _saveOnlyBtn;
        private Button _saveAndSyncBtn;
        private StackPanel _headerActionsPanel;

        // Card 2: Second PC & Linux VPS Probe
        private TextBox _vpsNameBox;
        private TextBox _vpsIntervalBox;
        private TextBox _vpsCmdPreviewBox;
        private Border _vpsOAuthNoticeBorder;
        private Button _copyVpsCmdBtn;

        // Card 3: Local Scan & Preferences
        private TextBlock _homeDirPathText;
        private CheckBox _wslSwitch;
        private CheckBox _minToTraySwitch;
        private CheckBox _subsSwitch;
        private Button[] _themeSegBtns;
        private Button[] _currencySegBtns;

        // Card 4: Archive Protection Health & Methodology
        private TextBlock _statDbSizeVal;
        private TextBlock _statDbPathSub;
        private TextBlock _statTrackedFilesVal;
        private TextBlock _statArchivedFilesVal;
        private TextBlock _statPricingModelsVal;
        private StackPanel _methodologyBodyPanel;
        private TextBlock _methodologyToggleLabel;
        private bool _methodologyExpanded = false;

        public SyncSettingsView(
            Window hostWindow,
            SyncConfig liveCfg,
            Func<ScanResult> getLatestScan,
            Func<string> getCurrentHomeDir,
            SyncSettingsCallbacks callbacks)
        {
            _hostWindow = hostWindow;
            _liveCfg = liveCfg ?? new SyncConfig();
            _workingCfg = _liveCfg.Clone();
            _getLatestScan = getLatestScan;
            _getCurrentHomeDir = getCurrentHomeDir;
            _callbacks = callbacks ?? new SyncSettingsCallbacks();

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            SetResourceReference(Border.BackgroundProperty, "BgWindow");

            _tokenProbeDebounceTimer = new DispatcherTimer();
            _tokenProbeDebounceTimer.Interval = TimeSpan.FromMilliseconds(550);
            _tokenProbeDebounceTimer.Tick += delegate(object s, EventArgs e)
            {
                _tokenProbeDebounceTimer.Stop();
                StartGitHubIdentityProbeAsync(false);
            };

            Child = BuildRootLayout();
            ReloadFromConfig();

            Loaded += OnViewLoaded;
            Unloaded += OnViewUnloaded;
        }

        private void OnViewLoaded(object sender, RoutedEventArgs e)
        {
            RefreshDynamicStats();
            if (!_isWebDav && !string.IsNullOrWhiteSpace(_gistTokenField.Value) && _lastProbeResult == null)
            {
                StartGitHubIdentityProbeAsync(false);
            }
        }

        private void OnViewUnloaded(object sender, RoutedEventArgs e)
        {
            if (_tokenProbeDebounceTimer != null)
                _tokenProbeDebounceTimer.Stop();
        }

        // -------------------- Public API Contract --------------------

        public void ReloadFromConfig()
        {
            _suppressUiEvents = true;
            try
            {
                _workingCfg = _liveCfg.Clone();
                _isWebDav = string.Equals(_workingCfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase);

                _machineNameBox.Text = _workingCfg.MachineName ?? "";
                _machineIdBox.Text = _workingCfg.MachineId ?? "";
                _autoSyncSwitch.IsChecked = _workingCfg.AutoSync;

                _gistTokenField.Value = _workingCfg.GistToken ?? "";
                _gistIdBox.Text = _workingCfg.GistId ?? "";
                _davUrlBox.Text = _workingCfg.WebDavUrl ?? "";
                _davUserBox.Text = _workingCfg.WebDavUser ?? "";
                _davPassField.Value = _workingCfg.WebDavPass ?? "";

                _wslSwitch.IsChecked = _liveCfg.ScanLocalWsl;
                _minToTraySwitch.IsChecked = _liveCfg.MinimizeToTray;
                _subsSwitch.IsChecked = _liveCfg.ShowSubscriptions;

                ModernControlsFactory.UpdateSegmentedGroup(_themeSegBtns, _liveCfg.IsDarkTheme ? 0 : 1);
                ModernControlsFactory.UpdateSegmentedGroup(_currencySegBtns, _liveCfg.PreferCny ? 0 : 1);

                UpdateProviderModeVisuals();
                UpdateLocalTokenPreviewLabels(_gistTokenField.Value);
                RefreshTopologyPreviewUi();
                RefreshVpsCommandPreview();
                RefreshDynamicStats();
            }
            finally
            {
                _suppressUiEvents = false;
            }
        }

        public void RefreshDynamicStats()
        {
            string curHome = ResolveCurrentHomeDirectory();
            if (_homeDirPathText != null)
            {
                _homeDirPathText.Text = curHome;
                _homeDirPathText.ToolTip = curHome;
            }

            string dbPath = System.IO.Path.Combine(SyncService.AppConfigDir, "toklens.db");
            bool dbExists = false;
            long dbBytes = 0L;
            try
            {
                if (File.Exists(dbPath))
                {
                    dbExists = true;
                    dbBytes = new FileInfo(dbPath).Length;
                    string walPath = dbPath + "-wal";
                    if (File.Exists(walPath))
                        dbBytes += new FileInfo(walPath).Length;
                }
            }
            catch { }

            if (_statDbSizeVal != null)
            {
                if (dbExists)
                {
                    double kb = dbBytes / 1024.0;
                    _statDbSizeVal.Text = kb >= 1024.0
                        ? string.Format(CultureInfo.InvariantCulture, "{0:F2} MB", kb / 1024.0)
                        : string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", kb);
                }
                else
                {
                    _statDbSizeVal.Text = "待初始化";
                }
            }
            if (_statDbPathSub != null)
            {
                _statDbPathSub.Text = dbPath;
                _statDbPathSub.ToolTip = dbPath;
            }

            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            if (_statTrackedFilesVal != null)
            {
                int tracked = scan != null ? scan.TotalFilesTracked : 0;
                _statTrackedFilesVal.Text = tracked.ToString("N0", CultureInfo.InvariantCulture) + " 个";
            }
            if (_statArchivedFilesVal != null)
            {
                int archived = scan != null ? scan.ArchivedFilesCount : 0;
                _statArchivedFilesVal.Text = archived.ToString("N0", CultureInfo.InvariantCulture) + " 个";
            }
            if (_statPricingModelsVal != null)
            {
                int modelCnt = scan != null ? scan.PricingModelCount : 0;
                _statPricingModelsVal.Text = modelCnt > 0
                    ? (modelCnt.ToString("N0", CultureInfo.InvariantCulture) + " 款")
                    : "就绪";
            }

            RefreshTopologyPreviewUi();
        }

        public void ScrollToSection(int sectionIndex)
        {
            int idx = Math.Max(0, Math.Min(3, sectionIndex));
            ModernControlsFactory.UpdateSegmentedGroup(_sectionAnchorBtns, idx);

            if (_mainScroll == null) return;
            if (idx == 0)
            {
                _mainScroll.ScrollToTop();
                return;
            }

            Border target = _secCloudCard;
            if (idx == 1) target = _secOnboardCard;
            else if (idx == 2) target = _secPrefsCard;
            else if (idx == 3) target = _secHealthCard;

            if (target != null)
            {
                try
                {
                    if (target.IsLoaded && _mainScroll.IsLoaded)
                    {
                        UIElement contentRoot = _mainScroll.Content as UIElement;
                        if (contentRoot != null)
                        {
                            Point pt = target.TranslatePoint(new Point(0, 0), contentRoot);
                            _mainScroll.ScrollToVerticalOffset(Math.Max(0.0, pt.Y - 12.0));
                            return;
                        }
                    }
                    target.BringIntoView();
                }
                catch { }
            }
        }

        public void TriggerQuickPairCopy()
        {
            PullSyncFieldsIntoWorkingConfig();
            if (!_workingCfg.IsConfigured)
            {
                NotifyUser(
                    "尚未完成云同步凭据配置",
                    "请先在下方填写 GitHub Token 并关联/创建私有 Gist（或配置 WebDAV），再复制配对码。",
                    "warning");
                ScrollToSection(0);
                return;
            }

            string pairCode = SyncService.GeneratePairingCode(_workingCfg);
            if (TrySetClipboardSafe(pairCode))
            {
                NotifyUser(
                    "加密配对码已复制到剪贴板",
                    "可在另一台电脑点击「粘贴配对码」一键导入。注意：配对码含同步凭据，请勿发送至公开群聊。",
                    "success");
            }
            else
            {
                NotifyUser("剪贴板正被其他程序占用", "请稍后重试复制配对码。", "warning");
            }
        }

        public void TriggerQuickPairPaste()
        {
            string clipText = TryGetClipboardSafe();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                NotifyUser("剪贴板为空", "请先在已配置好的电脑上点击「复制配对码」。", "warning");
                return;
            }

            SyncConfig tempCfg = _workingCfg.Clone();
            string err;
            if (!SyncService.TryApplyPairingCode(tempCfg, clipText, out err))
            {
                NotifyUser("配对码解析失败", err ?? "剪贴板内容不是有效的 TokLens 配对码。", "error");
                return;
            }

            _suppressUiEvents = true;
            try
            {
                _workingCfg.Provider = tempCfg.Provider;
                _workingCfg.GistId = tempCfg.GistId;
                _workingCfg.GistToken = tempCfg.GistToken;
                _workingCfg.WebDavUrl = tempCfg.WebDavUrl;
                _workingCfg.WebDavUser = tempCfg.WebDavUser;
                _workingCfg.WebDavPass = tempCfg.WebDavPass;
                _workingCfg.AutoSync = true;

                _isWebDav = string.Equals(_workingCfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase);
                _gistTokenField.Value = _workingCfg.GistToken;
                _gistIdBox.Text = _workingCfg.GistId;
                _davUrlBox.Text = _workingCfg.WebDavUrl;
                _davUserBox.Text = _workingCfg.WebDavUser;
                _davPassField.Value = _workingCfg.WebDavPass;
                _autoSyncSwitch.IsChecked = true;
                UpdateProviderModeVisuals();
                UpdateLocalTokenPreviewLabels(_workingCfg.GistToken);
            }
            finally
            {
                _suppressUiEvents = false;
            }

            RefreshVpsCommandPreview();
            ScrollToSection(0);
            try
            {
                _machineNameBox.Focus();
                _machineNameBox.SelectAll();
            }
            catch { }

            NotifyUser(
                "已导入配对码凭据（待保存）",
                "请确认「本机显示名称」（如：家里电脑），随后点击右上角「保存并同步」即可生效。",
                "success");

            if (!_isWebDav && !string.IsNullOrWhiteSpace(_workingCfg.GistToken))
            {
                StartGitHubIdentityProbeAsync(false);
            }
        }

        /// <summary>在页面头部保存按钮左侧加入宿主提供的动作（例如「更多」菜单）。</summary>
        public void AddHeaderAction(FrameworkElement action)
        {
            if (action == null || _headerActionsPanel == null) return;
            action.Margin = new Thickness(0, 0, 8, 0);
            _headerActionsPanel.Children.Insert(0, action);
        }

        // -------------------- UI Construction --------------------

        private UIElement BuildRootLayout()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Row 0: Sticky Sub-Navigation & Quick Actions Bar
            Border subHeader = BuildStickySubHeader();
            Grid.SetRow(subHeader, 0);
            root.Children.Add(subHeader);

            // Row 1: Optional Inline Feedback Strip (inside settings view)
            _inlineBannerBorder = BuildInlineFeedbackBanner();
            Grid.SetRow(_inlineBannerBorder, 1);
            root.Children.Add(_inlineBannerBorder);

            // Row 2: Smooth Centered Scrollable Settings Stream (MaxWidth = 880)
            _mainScroll = new ScrollViewer();
            _mainScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _mainScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _mainScroll.Padding = new Thickness(20, 16, 20, 28);

            StackPanel centerCol = new StackPanel();
            centerCol.MaxWidth = 880;
            centerCol.HorizontalAlignment = HorizontalAlignment.Stretch;

            _secCloudCard = BuildCard1CloudAndIdentity();
            _secCloudCard.Margin = new Thickness(0, 0, 0, 16);
            centerCol.Children.Add(_secCloudCard);

            _secOnboardCard = BuildCard2DeviceAndVpsOnboarding();
            _secOnboardCard.Margin = new Thickness(0, 0, 0, 16);
            centerCol.Children.Add(_secOnboardCard);

            _secPrefsCard = BuildCard3ScanAndPreferences();
            _secPrefsCard.Margin = new Thickness(0, 0, 0, 16);
            centerCol.Children.Add(_secPrefsCard);

            _secHealthCard = BuildCard4ArchiveHealthAndMethodology();
            _secHealthCard.Margin = new Thickness(0);
            centerCol.Children.Add(_secHealthCard);

            _mainScroll.Content = centerCol;
            Grid.SetRow(_mainScroll, 2);
            root.Children.Add(_mainScroll);

            return root;
        }

        private Border BuildStickySubHeader()
        {
            Border bar = new Border();
            bar.Height = 48;
            bar.Padding = new Thickness(20, 0, 20, 0);
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.SetResourceReference(Border.BackgroundProperty, "BgCard");
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel dock = new DockPanel();

            // 右侧：整页唯一的保存入口（配对码复制 / 粘贴在「接入电脑 / VPS」卡片里，不在这里重复）
            _headerActionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_headerActionsPanel, Dock.Right);

            _saveOnlyBtn = ModernControlsFactory.CreateButton("仅保存", "secondary");
            _saveOnlyBtn.Margin = new Thickness(0, 0, 8, 0);
            _saveOnlyBtn.ToolTip = "保存本页设置，不立即同步";
            _saveOnlyBtn.Click += delegate(object s, RoutedEventArgs e) { CommitSyncSettingsOnly(); };
            _headerActionsPanel.Children.Add(_saveOnlyBtn);

            _saveAndSyncBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "保存并同步", "primary");
            _saveAndSyncBtn.ToolTip = "保存本页设置并立即与云端双向同步";
            _saveAndSyncBtn.Click += delegate(object s, RoutedEventArgs e) { ExecuteSaveAndSyncFlow(); };
            _headerActionsPanel.Children.Add(_saveAndSyncBtn);

            dock.Children.Add(_headerActionsPanel);

            // Left segmented section anchors
            Border anchorSeg = ModernControlsFactory.CreateSegmentedContainer();
            anchorSeg.HorizontalAlignment = HorizontalAlignment.Left;
            Panel anchorSp = (Panel)anchorSeg.Child;

            string[] secTitles = new string[]
            {
                "云同步与身份",
                "接入电脑 / VPS",
                "扫描与偏好",
                "归档健康与口径"
            };
            _sectionAnchorBtns = new Button[secTitles.Length];
            for (int i = 0; i < secTitles.Length; i++)
            {
                int capturedIdx = i;
                Button btn = ModernControlsFactory.CreateSegmentedItem(secTitles[i]);
                btn.Click += delegate(object s, RoutedEventArgs e)
                {
                    ScrollToSection(capturedIdx);
                };
                _sectionAnchorBtns[i] = btn;
                anchorSp.Children.Add(btn);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_sectionAnchorBtns, 0);
            dock.Children.Add(anchorSeg);

            bar.Child = dock;
            return bar;
        }

        private Border BuildInlineFeedbackBanner()
        {
            Border b = new Border();
            b.Visibility = Visibility.Collapsed;
            b.MaxWidth = 880;
            b.Margin = new Thickness(20, 12, 20, 0);
            b.Padding = new Thickness(12, 8, 10, 8);
            b.CornerRadius = new CornerRadius(7);
            b.BorderThickness = new Thickness(1);
            b.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            b.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            DockPanel dock = new DockPanel();

            Button dismissBtn = ModernControlsFactory.CreateIconGhostButton("M 3,3 L 13,13 M 13,3 L 3,13", "收起提示");
            dismissBtn.Width = 22;
            dismissBtn.Height = 22;
            dismissBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(dismissBtn, Dock.Right);
            dismissBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                b.Visibility = Visibility.Collapsed;
            };
            dock.Children.Add(dismissBtn);

            _inlineBannerIcon = UiIcons.CreateIcon(UiIcons.Check, 14, "AccentText");
            _inlineBannerIcon.Margin = new Thickness(0, 1, 8, 0);
            _inlineBannerIcon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_inlineBannerIcon, Dock.Left);
            dock.Children.Add(_inlineBannerIcon);

            _inlineBannerText = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            _inlineBannerText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            dock.Children.Add(_inlineBannerText);

            b.Child = dock;
            return b;
        }

        // -------------------- Card 1: Cloud Sync & GitHub Identity --------------------

        private Border BuildCard1CloudAndIdentity()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(20, 18, 20, 18));
            StackPanel sp = new StackPanel();

            // Header with Provider Segmented Switch
            DockPanel hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };

            Border provBox = ModernControlsFactory.CreateSegmentedContainer();
            DockPanel.SetDock(provBox, Dock.Right);
            Panel provPanel = (Panel)provBox.Child;
            string[] provLabels = new string[] { "GitHub 私有 Gist", "WebDAV 网盘" };
            _providerBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button pb = ModernControlsFactory.CreateSegmentedItem(provLabels[i]);
                pb.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_suppressUiEvents) return;
                    _isWebDav = (idx == 1);
                    _workingCfg.Provider = _isWebDav ? "webdav" : "gist";
                    UpdateProviderModeVisuals();
                    RefreshVpsCommandPreview();
                };
                _providerBtns[i] = pb;
                provPanel.Children.Add(pb);
            }
            hdr.Children.Add(provBox);

            hdr.Children.Add(MakeCardHeaderBlock(
                UiIcons.Cloud,
                "1. 云端多设备同步与身份归属",
                "仅同步按日聚合的脱敏 Token 与金额数字，绝不上传任何对话原文；凭据使用本机 Windows DPAPI 加密存储"));
            sp.Children.Add(hdr);

            // --- GitHub Gist Section ---
            _gistSectionPanel = new StackPanel();

            // 1A. GitHub Identity & Credential Verification Sub-Card
            _ghIdentityCard = new Border();
            _ghIdentityCard.CornerRadius = new CornerRadius(7);
            _ghIdentityCard.BorderThickness = new Thickness(1);
            _ghIdentityCard.Padding = new Thickness(14, 12, 14, 12);
            _ghIdentityCard.Margin = new Thickness(0, 0, 0, 12);
            _ghIdentityCard.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            _ghIdentityCard.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel idStack = new StackPanel();

            // Row: Status badge + @username + right actions (Read gh CLI / Verify / Web PAT)
            DockPanel idTopRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            StackPanel ghActionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(ghActionRow, Dock.Right);

            _ghReadCliBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Terminal, "读取本机 gh 登录", "secondary");
            _ghReadCliBtn.Height = 26;
            _ghReadCliBtn.Padding = new Thickness(9, 0, 9, 0);
            _ghReadCliBtn.Margin = new Thickness(0, 0, 6, 0);
            _ghReadCliBtn.ToolTip = "自动探测本机 GitHub CLI (gh) 已登录的所有账号并安全提取凭据";
            _ghReadCliBtn.Click += OnReadGhCliAccountsClick;
            ghActionRow.Children.Add(_ghReadCliBtn);

            Button verifyNowBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Shield, "校验身份", "secondary");
            verifyNowBtn.Height = 26;
            verifyNowBtn.Padding = new Thickness(9, 0, 9, 0);
            verifyNowBtn.Margin = new Thickness(0, 0, 6, 0);
            verifyNowBtn.ToolTip = "立即连接 GitHub 验证当前令牌所属的 @用户名、gist 权限及名下同步库";
            verifyNowBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                StartGitHubIdentityProbeAsync(true);
            };
            ghActionRow.Children.Add(verifyNowBtn);

            Button webTokenBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.ExternalLink, "网页申请 Token", "ghost");
            webTokenBtn.Height = 26;
            webTokenBtn.Padding = new Thickness(8, 0, 8, 0);
            webTokenBtn.ToolTip = "在浏览器打开 GitHub 经典 Token 创建页（已自动预勾选 gist 权限）";
            webTokenBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenExternalUrlSafe("https://github.com/settings/tokens/new?description=TokLens-Sync&scopes=gist");
            };
            ghActionRow.Children.Add(webTokenBtn);

            idTopRow.Children.Add(ghActionRow);

            StackPanel idLeftInfo = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _ghAuthStatusPill = ModernControlsFactory.CreateStatusBadge("未配置令牌", "neutral", out _ghAuthStatusLabel, out _ghAuthStatusDot);
            _ghAuthStatusPill.Margin = new Thickness(0, 0, 8, 0);
            idLeftInfo.Children.Add(_ghAuthStatusPill);

            _ghAccountHandleText = new TextBlock
            {
                Text = "等待读取或输入 GitHub 访问令牌",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _ghAccountHandleText.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            idLeftInfo.Children.Add(_ghAccountHandleText);

            idTopRow.Children.Add(idLeftInfo);
            idStack.Children.Add(idTopRow);

            // Token Secret Input
            idStack.Children.Add(MakeFieldLabel("GitHub 访问令牌（支持 gh CLI 自动授权 gho_ 令牌，或勾选了 gist 权限的个人访问令牌）"));
            _gistTokenField = new ModernSecretField("在此粘贴 ghp_ / gho_ / github_pat_ 开头的令牌，或点击右上角「读取本机 gh 登录」");
            _gistTokenField.ValueChanged += delegate()
            {
                if (_suppressUiEvents) return;
                string tok = _gistTokenField.Value.Trim();
                UpdateLocalTokenPreviewLabels(tok);
                RefreshVpsCommandPreview();
                _tokenProbeDebounceTimer.Stop();
                if (!string.IsNullOrEmpty(tok) && tok.Length >= 16)
                    _tokenProbeDebounceTimer.Start();
            };
            idStack.Children.Add(_gistTokenField.Root);

            // Fingerprint & Token Kind Meta Row
            Grid fpGrid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            fpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            fpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _ghFingerprintText = new TextBlock
            {
                Text = "令牌指纹：—",
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModernControlsFactory.EnableTabularNumbers(_ghFingerprintText, true);
            _ghFingerprintText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            Grid.SetColumn(_ghFingerprintText, 0);
            fpGrid.Children.Add(_ghFingerprintText);

            _ghTokenKindText = new TextBlock
            {
                Text = "凭据类型：未配置",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _ghTokenKindText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            Grid.SetColumn(_ghTokenKindText, 2);
            fpGrid.Children.Add(_ghTokenKindText);
            idStack.Children.Add(fpGrid);

            // Missing 'gist' scope actionable remediation box
            _ghScopeFixBox = new Border();
            _ghScopeFixBox.Visibility = Visibility.Collapsed;
            _ghScopeFixBox.Margin = new Thickness(0, 9, 0, 0);
            _ghScopeFixBox.Padding = new Thickness(10, 7, 10, 7);
            _ghScopeFixBox.CornerRadius = new CornerRadius(6);
            _ghScopeFixBox.BorderThickness = new Thickness(1);
            _ghScopeFixBox.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
            _ghScopeFixBox.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");

            DockPanel scopeDock = new DockPanel();
            Button copyRefreshCmdBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Copy, "复制补权命令", "secondary");
            copyRefreshCmdBtn.Height = 24;
            copyRefreshCmdBtn.FontSize = DesignSystem.TextCaption;
            copyRefreshCmdBtn.Padding = new Thickness(8, 0, 8, 0);
            copyRefreshCmdBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(copyRefreshCmdBtn, Dock.Right);
            copyRefreshCmdBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                const string cmd = "gh auth refresh -h github.com -s gist";
                if (TrySetClipboardSafe(cmd))
                    NotifyUser("补齐权限命令已复制", "请在终端执行：" + cmd + " 完成授权后再点「读取本机 gh 登录」。", "success");
            };
            scopeDock.Children.Add(copyRefreshCmdBtn);

            _ghScopeFixText = new TextBlock
            {
                Text = "当前令牌缺少 gist 权限。请在终端执行 gh auth refresh -h github.com -s gist 一键补齐权限。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            _ghScopeFixText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
            scopeDock.Children.Add(_ghScopeFixText);
            _ghScopeFixBox.Child = scopeDock;
            idStack.Children.Add(_ghScopeFixBox);

            // Friendly educational note explaining per-device gho_ tokens
            _ghOAuthTipBox = MakeSubtleCallout(
                UiIcons.Key,
                "为什么家里电脑和单位电脑自动读取的 gho_ 令牌不一样？\n" +
                "GitHub CLI 在每台电脑登录授权时都会签发该设备专属的独立 OAuth 令牌（gho_...），字符串不同完全正常且更安全。只要两台电脑上方认证显示的 @用户名 相同，TokLens 就会自动找回并双向合并同一个私有 Gist 账本。",
                "AccentSubtleBg",
                "AccentSubtleBorder",
                "AccentText");
            _ghOAuthTipBox.Margin = new Thickness(0, 9, 0, 0);
            idStack.Children.Add(_ghOAuthTipBox);

            _ghIdentityCard.Child = idStack;
            _gistSectionPanel.Children.Add(_ghIdentityCard);

            // 1B. Cloud Gist Ledger & Topology Preview Sub-Card
            Border gistTopologyCard = new Border();
            gistTopologyCard.CornerRadius = new CornerRadius(7);
            gistTopologyCard.BorderThickness = new Thickness(1);
            gistTopologyCard.Padding = new Thickness(14, 12, 14, 12);
            gistTopologyCard.Margin = new Thickness(0, 0, 0, 14);
            gistTopologyCard.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            gistTopologyCard.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel topoSp = new StackPanel();
            topoSp.Children.Add(MakeFieldLabel("私有 Gist ID（首次使用或换新电脑均可留空，保存同步时将自动找回已有库或新建；支持直接粘贴完整 Gist 网址）"));

            Grid gistInputGrid = new Grid();
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            gistInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _gistIdBox = ModernControlsFactory.CreateTextInput("", 32);
            _gistIdBox.FontFamily = DesignSystem.FontMono;
            _gistIdBox.ToolTip = "填写 32 位 Gist ID 或粘贴 https://gist.github.com/... 链接均可自动识别";
            _gistIdBox.LostFocus += delegate(object s, RoutedEventArgs e)
            {
                NormalizeGistIdInputInPlace();
                RefreshVpsCommandPreview();
            };
            _gistIdBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (_suppressUiEvents) return;
                string raw = _gistIdBox.Text ?? "";
                if (raw.IndexOf('/') >= 0 || raw.IndexOf("gist.github.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    NormalizeGistIdInputInPlace();
                }
                RefreshTopologyPreviewUi();
                RefreshVpsCommandPreview();
            };
            Grid.SetColumn(_gistIdBox, 0);
            gistInputGrid.Children.Add(_gistIdBox);

            _pickGistCandidateBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.ChevronDown, "切换已有库", "secondary");
            _pickGistCandidateBtn.Height = 32;
            _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
            _pickGistCandidateBtn.Click += OnPickGistCandidateClick;
            Grid.SetColumn(_pickGistCandidateBtn, 2);
            gistInputGrid.Children.Add(_pickGistCandidateBtn);

            _autoGistBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Search, "自动查找 / 新建", "secondary");
            _autoGistBtn.Height = 32;
            _autoGistBtn.ToolTip = "检索当前账号下已有的 TokLens 私有同步库；若不存在则自动创建私有 Gist 并立即同步";
            _autoGistBtn.Click += OnAutoFindOrCreateGistClick;
            Grid.SetColumn(_autoGistBtn, 4);
            gistInputGrid.Children.Add(_autoGistBtn);

            _openGistWebBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.ExternalLink, "在浏览器中查看该私有 Gist");
            _openGistWebBtn.Width = 32;
            _openGistWebBtn.Height = 32;
            _openGistWebBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                string gid = SyncService.NormalizeGistId(_gistIdBox.Text);
                if (string.IsNullOrEmpty(gid))
                {
                    NotifyUser("尚未关联 Gist ID", "请先点击「自动查找 / 新建」或执行一次同步。", "warning");
                    return;
                }
                OpenExternalUrlSafe("https://gist.github.com/" + gid);
            };
            Grid.SetColumn(_openGistWebBtn, 6);
            gistInputGrid.Children.Add(_openGistWebBtn);

            topoSp.Children.Add(gistInputGrid);

            _topologySummaryText = new TextBlock
            {
                Text = "多端设备账本拓扑预览",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 12, 0, 6)
            };
            _topologySummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            topoSp.Children.Add(_topologySummaryText);

            _topologyNodesWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            topoSp.Children.Add(_topologyNodesWrap);

            gistTopologyCard.Child = topoSp;
            _gistSectionPanel.Children.Add(gistTopologyCard);
            sp.Children.Add(_gistSectionPanel);

            // --- WebDAV Section ---
            _webdavSectionPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 14) };
            Border davCard = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12)
            };
            davCard.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            davCard.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel davSp = new StackPanel();
            davSp.Children.Add(MakeFieldLabel("WebDAV 同步目录地址（例如坚果云 / Nextcloud：https://dav.jianguoyun.com/dav/TokLens/）"));
            _davUrlBox = ModernControlsFactory.CreateTextInput("", 32);
            _davUrlBox.FontFamily = DesignSystem.FontMono;
            _davUrlBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (!_suppressUiEvents) RefreshVpsCommandPreview();
            };
            davSp.Children.Add(_davUrlBox);

            Grid davCredGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            davCredGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            davCredGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            davCredGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel davUserCol = new StackPanel();
            davUserCol.Children.Add(MakeFieldLabel("WebDAV 用户名 / 邮箱"));
            _davUserBox = ModernControlsFactory.CreateTextInput("", 32);
            _davUserBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (!_suppressUiEvents) RefreshVpsCommandPreview();
            };
            davUserCol.Children.Add(_davUserBox);
            Grid.SetColumn(davUserCol, 0);
            davCredGrid.Children.Add(davUserCol);

            StackPanel davPassCol = new StackPanel();
            davPassCol.Children.Add(MakeFieldLabel("WebDAV 应用专用密码"));
            _davPassField = new ModernSecretField("建议使用网盘生成的独立应用密码，本机采用 DPAPI 加密存储");
            _davPassField.ValueChanged += delegate()
            {
                if (!_suppressUiEvents) RefreshVpsCommandPreview();
            };
            davPassCol.Children.Add(_davPassField.Root);
            Grid.SetColumn(davPassCol, 2);
            davCredGrid.Children.Add(davPassCol);

            davSp.Children.Add(davCredGrid);
            davCard.Child = davSp;
            _webdavSectionPanel.Children.Add(davCard);
            sp.Children.Add(_webdavSectionPanel);

            // --- Local Machine Identity & Save Row ---
            Grid localIdGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            localIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            localIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            localIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            StackPanel nameCol = new StackPanel();
            nameCol.Children.Add(MakeFieldLabel("本机显示名称（如：办公电脑 / 家里主力机）"));
            _machineNameBox = ModernControlsFactory.CreateTextInput("", 32);
            nameCol.Children.Add(_machineNameBox);
            Grid.SetColumn(nameCol, 0);
            localIdGrid.Children.Add(nameCol);

            StackPanel idCol = new StackPanel();
            idCol.Children.Add(MakeFieldLabel("本机设备 ID（各电脑须不同，改名会自动迁移本机账本）"));
            _machineIdBox = ModernControlsFactory.CreateTextInput("", 32);
            _machineIdBox.FontFamily = DesignSystem.FontMono;
            idCol.Children.Add(_machineIdBox);
            Grid.SetColumn(idCol, 2);
            localIdGrid.Children.Add(idCol);
            sp.Children.Add(localIdGrid);

            // Footer inside Card 1: AutoSync toggle（保存按钮统一在页面头部）
            Border card1Footer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 12, 0, 0)
            };
            card1Footer.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel actDock = new DockPanel();

            _autoSyncSwitch = ModernControlsFactory.CreateToggleSwitch("启用后台全自动同步（每 5 分钟及本机用量变化后静默合并）");
            _autoSyncSwitch.VerticalAlignment = VerticalAlignment.Center;
            actDock.Children.Add(_autoSyncSwitch);

            card1Footer.Child = actDock;
            sp.Children.Add(card1Footer);

            card.Child = sp;
            return card;
        }

        // -------------------- Card 2: Second PC & Linux VPS Probe --------------------

        private Border BuildCard2DeviceAndVpsOnboarding()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(20, 18, 20, 18));
            StackPanel sp = new StackPanel();

            sp.Children.Add(MakeCardHeaderBlock(
                UiIcons.Devices,
                "2. 接入另一台 Windows 电脑与 Linux VPS 探针",
                "支持多台开发机与远端无界面 Linux 服务器汇总对账，探针仅依赖系统自带 Python 3 标准库"));

            // Sub-section A: Second Windows PC
            Border pcBox = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 12, 0, 12)
            };
            pcBox.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            pcBox.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel pcDock = new DockPanel();
            StackPanel pcBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            DockPanel.SetDock(pcBtns, Dock.Right);

            Button cpPair2 = ModernControlsFactory.CreateIconTextButton(UiIcons.Copy, "复制配对码", "secondary");
            cpPair2.Margin = new Thickness(0, 0, 6, 0);
            cpPair2.Click += delegate(object s, RoutedEventArgs e) { TriggerQuickPairCopy(); };
            pcBtns.Children.Add(cpPair2);

            Button psPair2 = ModernControlsFactory.CreateIconTextButton(UiIcons.Key, "粘贴配对码", "secondary");
            psPair2.Click += delegate(object s, RoutedEventArgs e) { TriggerQuickPairPaste(); };
            pcBtns.Children.Add(psPair2);
            pcDock.Children.Add(pcBtns);

            StackPanel pcTextCol = new StackPanel();
            TextBlock pcTitle = new TextBlock
            {
                Text = "方式 A：接入家里或单位的第二台 Windows 电脑",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            pcTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            pcTextCol.Children.Add(pcTitle);

            TextBlock pcDesc = new TextBlock
            {
                Text = "1) 免配对码自动找回（推荐）：在新电脑登录同一 GitHub 账号的 gh CLI，点击上方「读取本机 gh 登录 -> 保存并同步」即可自动关联同一私有 Gist。\n" +
                       "2) 配对码秒导入：在本机点击「复制配对码」，发给自己的另一台电脑点击「粘贴配对码」。安全提醒：配对码内含云同步令牌，切勿发送至公开群聊。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap
            };
            pcDesc.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            pcTextCol.Children.Add(pcDesc);
            pcDock.Children.Add(pcTextCol);

            pcBox.Child = pcDock;
            sp.Children.Add(pcBox);

            // Sub-section B: Linux VPS Probe
            Border vpsBox = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12)
            };
            vpsBox.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            vpsBox.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel vpsSp = new StackPanel();
            TextBlock vpsTitle = new TextBlock
            {
                Text = "方式 B：接入 Linux VPS（零第三方依赖 Python 3 探针 + Crontab 定时上报）",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            vpsTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            vpsSp.Children.Add(vpsTitle);

            Grid vpsCfgGrid = new Grid();
            vpsCfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            vpsCfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            vpsCfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            vpsCfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            vpsCfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel vnCol = new StackPanel();
            vnCol.Children.Add(MakeFieldLabel("VPS 显示名称（如：香港主力 VPS-1）"));
            _vpsNameBox = ModernControlsFactory.CreateTextInput("主力 VPS-1", 32);
            _vpsNameBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (!_suppressUiEvents) RefreshVpsCommandPreview();
            };
            vnCol.Children.Add(_vpsNameBox);
            Grid.SetColumn(vnCol, 0);
            vpsCfgGrid.Children.Add(vnCol);

            StackPanel viCol = new StackPanel();
            viCol.Children.Add(MakeFieldLabel("同步间隔（分钟）"));
            _vpsIntervalBox = ModernControlsFactory.CreateTextInput("30", 32);
            _vpsIntervalBox.FontFamily = DesignSystem.FontMono;
            _vpsIntervalBox.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (!_suppressUiEvents) RefreshVpsCommandPreview();
            };
            viCol.Children.Add(_vpsIntervalBox);
            Grid.SetColumn(viCol, 2);
            vpsCfgGrid.Children.Add(viCol);

            _copyVpsCmdBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Terminal, "复制 SSH 一键部署命令", "secondary");
            _copyVpsCmdBtn.Height = 32;
            _copyVpsCmdBtn.VerticalAlignment = VerticalAlignment.Bottom;
            _copyVpsCmdBtn.Click += OnCopyVpsCommandClick;
            Grid.SetColumn(_copyVpsCmdBtn, 4);
            vpsCfgGrid.Children.Add(_copyVpsCmdBtn);
            vpsSp.Children.Add(vpsCfgGrid);

            _vpsCmdPreviewBox = ModernControlsFactory.CreateTextInput("", 68);
            _vpsCmdPreviewBox.IsReadOnly = true;
            _vpsCmdPreviewBox.TextWrapping = TextWrapping.Wrap;
            _vpsCmdPreviewBox.VerticalContentAlignment = VerticalAlignment.Top;
            _vpsCmdPreviewBox.Padding = new Thickness(10, 8, 10, 8);
            _vpsCmdPreviewBox.FontFamily = DesignSystem.FontMono;
            _vpsCmdPreviewBox.FontSize = DesignSystem.TextCaption;
            _vpsCmdPreviewBox.Margin = new Thickness(0, 10, 0, 8);
            vpsSp.Children.Add(_vpsCmdPreviewBox);

            _vpsOAuthNoticeBorder = MakeSubtleCallout(
                UiIcons.Shield,
                "温和提示：当前使用的是本机 GitHub CLI 签发的 gho_ 令牌，可直接用于 VPS 探针；若日后在本机执行 gh auth logout 注销，VPS 同步也会随之失效。长期无人值守 VPS 也可使用仅勾选 gist 权限的专用 PAT。",
                "BgCard",
                "BorderCard",
                "FgSecondary");
            _vpsOAuthNoticeBorder.Margin = new Thickness(0, 0, 0, 8);
            _vpsOAuthNoticeBorder.Visibility = Visibility.Collapsed;
            vpsSp.Children.Add(_vpsOAuthNoticeBorder);

            DockPanel vpsBotRow = new DockPanel();
            Button exportPyBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Download, "单独导出 toklens_agent.py 脚本", "secondary");
            exportPyBtn.Height = 28;
            exportPyBtn.Click += OnExportVpsAgentPyClick;
            DockPanel.SetDock(exportPyBtn, Dock.Left);
            vpsBotRow.Children.Add(exportPyBtn);

            TextBlock vpsAssureText = new TextBlock
            {
                Text = "探针内置 ~/.toklens/ledger.db 持久归档保护，VPS 定期清理日志或重启均不丢历史账单",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(12, 0, 0, 0)
            };
            vpsAssureText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
            vpsBotRow.Children.Add(vpsAssureText);
            vpsSp.Children.Add(vpsBotRow);

            vpsBox.Child = vpsSp;
            sp.Children.Add(vpsBox);

            card.Child = sp;
            return card;
        }

        // -------------------- Card 3: Scan Source & Preferences (Auto-Save) --------------------

        private Border BuildCard3ScanAndPreferences()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(20, 18, 20, 18));
            StackPanel sp = new StackPanel();

            sp.Children.Add(MakeCardHeaderBlock(
                UiIcons.Settings,
                "3. 本机扫描源与常驻偏好",
                "以下本机偏好设置修改后即时生效并自动持久化保存，无需额外点击保存按钮"));

            // Row 1: Home Directory Selection
            Border rowHome = MakeSettingRowContainer(true);
            DockPanel homeDock = new DockPanel();
            StackPanel homeBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(homeBtns, Dock.Right);

            Button browseDirBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Folder, "浏览切换...", "secondary");
            browseDirBtn.Margin = new Thickness(0, 0, 6, 0);
            browseDirBtn.Click += OnBrowseHomeDirectoryClick;
            homeBtns.Children.Add(browseDirBtn);

            Button resetDirBtn = ModernControlsFactory.CreateButton("恢复默认", "ghost");
            resetDirBtn.Click += OnResetHomeDirectoryClick;
            homeBtns.Children.Add(resetDirBtn);
            homeDock.Children.Add(homeBtns);

            StackPanel homeTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            homeTextStack.Children.Add(MakeRowTitle("扫描的用户主目录"));
            _homeDirPathText = new TextBlock
            {
                Text = ResolveCurrentHomeDirectory(),
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _homeDirPathText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            homeTextStack.Children.Add(_homeDirPathText);
            homeDock.Children.Add(homeTextStack);
            rowHome.Child = homeDock;
            sp.Children.Add(rowHome);

            // Row 2: WSL2 Scan Toggle
            Border rowWsl = MakeSettingRowContainer(true);
            DockPanel wslDock = new DockPanel();
            _wslSwitch = ModernControlsFactory.CreateToggleSwitch("");
            _wslSwitch.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_wslSwitch, Dock.Right);
            _wslSwitch.Click += OnWslToggleChanged;
            wslDock.Children.Add(_wslSwitch);

            wslDock.Children.Add(MakeRowTitleAndDesc(
                "穿透扫描本机 WSL2 发行版",
                "自动发现并增量监听 \\\\wsl$\\ 下各 Linux 子系统内的 Claude Code、Codex、Gemini 等日志"));
            rowWsl.Child = wslDock;
            sp.Children.Add(rowWsl);

            // Row 3: Minimize to System Tray on Close
            Border rowTray = MakeSettingRowContainer(true);
            DockPanel trayDock = new DockPanel();
            _minToTraySwitch = ModernControlsFactory.CreateToggleSwitch("");
            _minToTraySwitch.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_minToTraySwitch, Dock.Right);
            _minToTraySwitch.Click += OnMinToTrayToggleChanged;
            trayDock.Children.Add(_minToTraySwitch);

            trayDock.Children.Add(MakeRowTitleAndDesc(
                "关闭主窗口时常驻系统托盘",
                "点右上角 ❌ 只隐藏窗口，悬浮条、日志监听和自动同步继续运行；托盘右键「彻底退出」才结束（默认开启）"));
            rowTray.Child = trayDock;
            sp.Children.Add(rowTray);

            // 可选功能：订阅记账与回本比例（默认关闭）
            Border rowSubs = MakeSettingRowContainer(true);
            DockPanel subsDock = new DockPanel();
            _subsSwitch = ModernControlsFactory.CreateToggleSwitch("");
            _subsSwitch.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_subsSwitch, Dock.Right);
            _subsSwitch.Click += OnSubscriptionsToggleChanged;
            subsDock.Children.Add(_subsSwitch);
            subsDock.Children.Add(MakeRowTitleAndDesc(
                "订阅记账与回本比例",
                "在侧栏显示「订阅与费用」：登记订阅实付，并按 API 单价估算本周期回本比例（估算，不是账单）"));
            rowSubs.Child = subsDock;
            sp.Children.Add(rowSubs);

            // Row 4: Theme & Default Currency
            Border rowAppearance = MakeSettingRowContainer(true);
            DockPanel appDock = new DockPanel();

            StackPanel appControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(appControls, Dock.Right);

            Border themeSeg = ModernControlsFactory.CreateSegmentedContainer();
            themeSeg.Margin = new Thickness(0, 0, 10, 0);
            Panel themeSp = (Panel)themeSeg.Child;
            string[] tNames = new string[] { "深色主题", "浅色主题" };
            _themeSegBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button tb = ModernControlsFactory.CreateSegmentedItem(tNames[i]);
                tb.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_suppressUiEvents) return;
                    bool wantDark = (idx == 0);
                    if (_liveCfg.IsDarkTheme == wantDark) return;
                    _liveCfg.IsDarkTheme = wantDark;
                    _workingCfg.IsDarkTheme = wantDark;
                    SyncService.SaveConfig(_liveCfg);
                    ModernControlsFactory.UpdateSegmentedGroup(_themeSegBtns, idx);
                    if (_hostWindow != null)
                        DesignSystem.ApplyTheme(_hostWindow, wantDark);
                    if (_callbacks.OnThemeOrCurrencyChanged != null)
                        _callbacks.OnThemeOrCurrencyChanged();
                };
                _themeSegBtns[i] = tb;
                themeSp.Children.Add(tb);
            }
            appControls.Children.Add(themeSeg);

            Border curSeg = ModernControlsFactory.CreateSegmentedContainer();
            Panel curSp = (Panel)curSeg.Child;
            string[] cNames = new string[] { "人民币 (¥)", "美元 ($)" };
            _currencySegBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button cb = ModernControlsFactory.CreateSegmentedItem(cNames[i]);
                cb.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_suppressUiEvents) return;
                    bool wantCny = (idx == 0);
                    if (_liveCfg.PreferCny == wantCny) return;
                    _liveCfg.PreferCny = wantCny;
                    _workingCfg.PreferCny = wantCny;
                    SyncService.SaveConfig(_liveCfg);
                    ModernControlsFactory.UpdateSegmentedGroup(_currencySegBtns, idx);
                    RefreshTopologyPreviewUi();
                    if (_callbacks.OnThemeOrCurrencyChanged != null)
                        _callbacks.OnThemeOrCurrencyChanged();
                };
                _currencySegBtns[i] = cb;
                curSp.Children.Add(cb);
            }
            appControls.Children.Add(curSeg);

            appDock.Children.Add(appControls);
            appDock.Children.Add(MakeRowTitleAndDesc(
                "界面外观与默认计价币种",
                "深浅色主题与默认显示币种，修改后立即生效"));
            rowAppearance.Child = appDock;
            sp.Children.Add(rowAppearance);

            // Row 6: Cloud Pricing & Live FX Maintenance
            Border rowMaint = MakeSettingRowContainer(false);
            DockPanel maintDock = new DockPanel();
            StackPanel maintBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(maintBtns, Dock.Right);

            Button refreshPriceBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Models, "强制更新模型价格库", "secondary");
            refreshPriceBtn.Margin = new Thickness(0, 0, 8, 0);
            refreshPriceBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_callbacks.OnForceRefreshPricing != null)
                {
                    _callbacks.OnForceRefreshPricing();
                    NotifyUser("已触发云端价格库更新", "正在从 models.dev、LiteLLM 与 OpenRouter 拉取最新单价…", "accent");
                }
                else
                {
                    Task.Run(delegate()
                    {
                        PricingSyncInfo info = PricingEngine.SyncCloudPricing(true);
                        Dispatcher.BeginInvoke(new Action(delegate()
                        {
                            RefreshDynamicStats();
                            if (info != null && info.IsLive)
                                NotifyUser("模型价格库已更新", "已聚合 " + info.CloudModelCount.ToString("N0", CultureInfo.InvariantCulture) + " 款模型单价（" + info.Source + "）", "success");
                        }));
                    });
                }
            };
            maintBtns.Children.Add(refreshPriceBtn);

            Button refreshFxBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "刷新 USD/CNY 汇率", "secondary");
            refreshFxBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_callbacks.OnForceRefreshFx != null)
                {
                    _callbacks.OnForceRefreshFx();
                    NotifyUser("已触发实时汇率更新", "正在拉取最新美元兑人民币在线汇率…", "accent");
                }
                else
                {
                    Task.Run(delegate()
                    {
                        ExchangeRateInfo fx = ExchangeRateService.FetchLatestUsdToCny(true);
                        Dispatcher.BeginInvoke(new Action(delegate()
                        {
                            if (fx != null)
                                NotifyUser("汇率已刷新", "最新 USD/CNY = " + fx.Rate.ToString("0.0000", CultureInfo.InvariantCulture) + " (" + fx.Source + ")", "success");
                        }));
                    });
                }
            };
            maintBtns.Children.Add(refreshFxBtn);

            maintDock.Children.Add(maintBtns);
            maintDock.Children.Add(MakeRowTitleAndDesc(
                "模型价格库与汇率",
                "支持在 ~/.toklens/custom_prices.json 中放置自定义单价覆盖规则"));
            rowMaint.Child = maintDock;
            sp.Children.Add(rowMaint);

            card.Child = sp;
            return card;
        }

        // -------------------- Card 4: Archive Health & Methodology --------------------

        private Border BuildCard4ArchiveHealthAndMethodology()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(20, 18, 20, 18));
            StackPanel sp = new StackPanel();

            DockPanel hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            TextBlock okLbl;
            Shapes.Ellipse okDot;
            Border healthBadge = ModernControlsFactory.CreateStatusBadge("归档保护已开启", "success", out okLbl, out okDot);
            DockPanel.SetDock(healthBadge, Dock.Right);
            hdr.Children.Add(healthBadge);

            hdr.Children.Add(MakeCardHeaderBlock(
                UiIcons.Shield,
                "4. 本地归档与统计口径",
                "即使 AI 工具自动清理旧会话、手动清空日志目录甚至重装系统，已入库与已同步的历史账单永久保留不归零"));
            sp.Children.Add(hdr);

            // 4-Column Health Metrics Grid
            Grid metricsGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int c = 0; c < 4; c++)
            {
                metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (c < 3)
                    metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            }

            Border m0 = MakeMiniMetricTile("本地游标账本 (toklens.db)", out _statDbSizeVal, out _statDbPathSub);
            Grid.SetColumn(m0, 0);
            metricsGrid.Children.Add(m0);

            TextBlock sub1;
            Border m1 = MakeMiniMetricTile("当前活跃跟踪日志", out _statTrackedFilesVal, out sub1);
            sub1.Text = "增量监听中";
            Grid.SetColumn(m1, 2);
            metricsGrid.Children.Add(m1);

            TextBlock sub2;
            Border m2 = MakeMiniMetricTile("已保护被清理历史文件", out _statArchivedFilesVal, out sub2);
            sub2.Text = "原日志删除后自动转入归档";
            Grid.SetColumn(m2, 4);
            metricsGrid.Children.Add(m2);

            TextBlock sub3;
            Border m3 = MakeMiniMetricTile("独立价格库覆盖模型", out _statPricingModelsVal, out sub3);
            sub3.Text = "models.dev + LiteLLM + OR";
            Grid.SetColumn(m3, 6);
            metricsGrid.Children.Add(m3);

            sp.Children.Add(metricsGrid);

            // Collapsible Methodology Accordion
            Border accBorder = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1)
            };
            accBorder.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            accBorder.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel accStack = new StackPanel();

            Border accHeaderHit = new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            DockPanel accHdrDock = new DockPanel();
            Shapes.Path chevIcon = UiIcons.CreateIcon(UiIcons.ChevronDown, 12, "FgSecondary");
            DockPanel.SetDock(chevIcon, Dock.Right);
            accHdrDock.Children.Add(chevIcon);

            _methodologyToggleLabel = new TextBlock
            {
                Text = "展开查看：请求 TPS 计时口径、模型价格库与历史用量合并说明",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium
            };
            _methodologyToggleLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            accHdrDock.Children.Add(_methodologyToggleLabel);
            accHeaderHit.Child = accHdrDock;

            _methodologyBodyPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(14, 0, 14, 14)
            };

            _methodologyBodyPanel.Children.Add(MakeMethodologyItem(
                "1) 免代理实时请求流监控 + SQLite 字节偏移游标引擎",
                "通过日志变更监听和增量扫描读取请求用量。TPS = 输出 Token ÷ 日志记录的请求耗时（秒），可能包含等待时间；仅使用明确的耗时字段或同一请求的起止时间。缺少可靠计时显示 —，不从相邻日志间隔推算，也不代表流式实时生成速度。"));
            _methodologyBodyPanel.Children.Add(MakeMethodologyItem(
                "2) 第一层保护：本地防清理持久归档（toklens.db & local_highwater）",
                "桌面端维护文件级快照与按日高水位账本；当 Claude Code / Codex / Cursor 等工具自动轮转清理旧会话或手动删除缓存日志时，被清理文件的历史累计 Token 与金额自动沉淀至持久归档，绝不倒扣归零。"));
            _methodologyBodyPanel.Children.Add(MakeMethodologyItem(
                "3) 第二层保护：云端 Gist / WebDAV 高水位合并（High-Water-Mark Merge）",
                "每次多端同步前均先拉取云端快照，按「设备 + 日期 + 工具 + 模型」执行高水位无损合并；即便重装电脑操作系统或重建 Linux VPS，只要连回同一私有同步库即可完整恢复历史账单。"));
            _methodologyBodyPanel.Children.Add(MakeMethodologyItem(
                "4) 100% 独立三源模型价格引擎与长上下文阶梯计费",
                "自动聚合 models.dev、LiteLLM 与 OpenRouter 三大公开注册表（2,000+ 模型输入/输出/缓存读写单价），精准识别 >200K 长上下文阶梯倍率，并区分 Claude Desktop 与 Claude Code 入口。"));

            accHeaderHit.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                _methodologyExpanded = !_methodologyExpanded;
                _methodologyBodyPanel.Visibility = _methodologyExpanded ? Visibility.Visible : Visibility.Collapsed;
                _methodologyToggleLabel.Text = _methodologyExpanded
                    ? "收起架构与统计口径说明"
                    : "展开查看：请求 TPS 计时口径、模型价格库与历史用量合并说明";
            };

            accStack.Children.Add(accHeaderHit);
            accStack.Children.Add(_methodologyBodyPanel);
            accBorder.Child = accStack;
            sp.Children.Add(accBorder);

            card.Child = sp;
            return card;
        }

        // -------------------- Interactive Flows & Background Logic --------------------

        private void OnReadGhCliAccountsClick(object sender, RoutedEventArgs e)
        {
            _ghReadCliBtn.IsEnabled = false;
            SetIdentityBadgeState("探测中…", "accent", "正在扫描本机已登录的 GitHub CLI (gh) 账号…");

            Task.Run(delegate()
            {
                bool hasEnvOverride;
                string envVarName;
                List<GhCliAccountInfo> accounts = SyncService.DiscoverGitHubCliAccounts(out hasEnvOverride, out envVarName);

                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _ghReadCliBtn.IsEnabled = true;

                    if (accounts.Count == 0 && !hasEnvOverride)
                    {
                        SetIdentityBadgeState("未检测到 gh 登录", "warning", "本机尚未登录 GitHub CLI (gh)");
                        NotifyUser(
                            "未检测到已登录的 GitHub CLI",
                            "你可在终端运行 gh auth login，或点击右侧「网页申请 Token」，也可直接粘贴另一台电脑复制的配对码。",
                            "warning");
                        return;
                    }

                    // Single gh account without env override -> read directly for instant zero-click UX!
                    if (accounts.Count == 1 && !hasEnvOverride)
                    {
                        ApplyGhCliAccountSelection(accounts[0].Login, true);
                        return;
                    }

                    // Multiple accounts (e.g. work + personal) or GH_TOKEN env override -> show ModernPopover picker!
                    ShowGhAccountsPickerPopover(accounts, hasEnvOverride, envVarName);
                }));
            });
        }

        private void ShowGhAccountsPickerPopover(List<GhCliAccountInfo> accounts, bool hasEnvOverride, string envVarName)
        {
            StackPanel menuSp = new StackPanel { MinWidth = 280, MaxWidth = 360 };

            TextBlock hdr = new TextBlock
            {
                Text = "选择用于同步账单的 GitHub 账号",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 4, 8, 6)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            menuSp.Children.Add(hdr);

            for (int i = 0; i < accounts.Count; i++)
            {
                GhCliAccountInfo acc = accounts[i];
                string login = acc.Login;
                string tag = acc.IsActive ? " (当前激活)" : "";
                Button itemBtn = MakePopoverMenuItemButton(
                    "@" + login + tag,
                    "读取本机 gh CLI 为该账号独立签发的凭据（忽略环境变量干扰）",
                    delegate()
                    {
                        if (_ghAccountPopover != null) _ghAccountPopover.IsOpen = false;
                        ApplyGhCliAccountSelection(login, true);
                    });
                menuSp.Children.Add(itemBtn);
            }

            if (hasEnvOverride && !string.IsNullOrEmpty(envVarName))
            {
                Border sep = new Border { Height = 1, Margin = new Thickness(6, 5, 6, 5) };
                sep.SetResourceReference(Border.BackgroundProperty, "BorderCard");
                menuSp.Children.Add(sep);

                string capturedEnv = envVarName;
                Button envBtn = MakePopoverMenuItemButton(
                    "使用系统环境变量 $" + capturedEnv,
                    "读取当前进程环境变量中的令牌（注意区分工作与个人账号）",
                    delegate()
                    {
                        if (_ghAccountPopover != null) _ghAccountPopover.IsOpen = false;
                        ApplyGhCliAccountSelection(null, false);
                    });
                menuSp.Children.Add(envBtn);
            }

            _ghAccountPopover = ModernControlsFactory.CreateModernPopover(_ghReadCliBtn, menuSp);
            _ghAccountPopover.IsOpen = true;
        }

        private void ApplyGhCliAccountSelection(string username, bool ignoreEnvOverride)
        {
            SetIdentityBadgeState("读取凭据中…", "accent", "正在提取 GitHub CLI 令牌并校验身份…");

            Task.Run(delegate()
            {
                string tok;
                bool ok = SyncService.TryReadGitHubCliTokenForUser(username, ignoreEnvOverride, out tok);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    if (!ok || string.IsNullOrWhiteSpace(tok))
                    {
                        SetIdentityBadgeState("读取失败", "warning", "无法从 GitHub CLI 提取有效令牌");
                        NotifyUser("读取 gh 凭据失败", "请确认终端中 gh auth status 状态正常，或改用网页申请 PAT。", "warning");
                        return;
                    }

                    _suppressUiEvents = true;
                    try
                    {
                        _gistTokenField.Value = tok;
                        UpdateLocalTokenPreviewLabels(tok);
                    }
                    finally
                    {
                        _suppressUiEvents = false;
                    }

                    RefreshVpsCommandPreview();
                    StartGitHubIdentityProbeAsync(true);
                }));
            });
        }

        private void StartGitHubIdentityProbeAsync(bool userInitiated)
        {
            string tok = (_gistTokenField != null ? _gistTokenField.Value : "").Trim();
            string curGistId = SyncService.NormalizeGistId(_gistIdBox != null ? _gistIdBox.Text : "");

            if (string.IsNullOrEmpty(tok))
            {
                _lastProbeResult = null;
                SetIdentityBadgeState("未配置令牌", "neutral", "等待读取或输入 GitHub 访问令牌");
                _ghScopeFixBox.Visibility = Visibility.Collapsed;
                _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
                RefreshTopologyPreviewUi();
                return;
            }

            int reqId = ++_probeRequestSeq;
            SetIdentityBadgeState("校验中…", "accent", "正在向 GitHub 验证令牌归属与私有 Gist 拓扑…");

            Task.Run(delegate()
            {
                GitHubProbeResult probe = SyncService.VerifyGitHubTokenAndProbeGists(tok, curGistId);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    if (reqId != _probeRequestSeq) return;
                    ApplyProbeResultToUi(probe, curGistId, userInitiated);
                }));
            });
        }

        private void ApplyProbeResultToUi(GitHubProbeResult probe, string checkedGistId, bool userInitiated)
        {
            _lastProbeResult = probe;
            if (probe == null) return;

            UpdateLocalTokenPreviewLabels(_gistTokenField.Value);

            if (!probe.TokenValid)
            {
                SetIdentityBadgeState("认证未通过", "danger", probe.ErrorMessage ?? "令牌无效或网络不可达");
                _ghScopeFixBox.Visibility = Visibility.Collapsed;
                _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
                if (userInitiated)
                    NotifyUser("GitHub 凭据校验未通过", probe.ErrorMessage ?? "请检查令牌是否正确或网络连接是否畅通。", "error");
                return;
            }

            if (!probe.HasGistScope)
            {
                SetIdentityBadgeState("缺少 gist 权限", "warning", "已识别账号 @" + probe.Login + " · 需补齐 gist 读写权限");
                _ghScopeFixText.Text = string.IsNullOrEmpty(probe.ErrorMessage)
                    ? ("已识别账号 @" + probe.Login + "，但令牌缺少 gist 权限。请在终端运行 gh auth refresh -h github.com -s gist 补齐。")
                    : probe.ErrorMessage;
                _ghScopeFixBox.Visibility = Visibility.Visible;
                _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
                if (userInitiated)
                    NotifyUser("已识别 @" + probe.Login + "，但缺少 gist 权限", "可点击卡片内的「复制补权命令」在终端一键补齐。", "warning");
                return;
            }

            // Authenticated & gist scope verified!
            _ghScopeFixBox.Visibility = Visibility.Collapsed;
            SetIdentityBadgeState(
                "已认证 @" + probe.Login,
                "success",
                "GitHub 账号 @" + probe.Login + " · gist 私有读写权限已就绪");

            int privateCandCount = 0;
            if (probe.Candidates != null)
            {
                for (int i = 0; i < probe.Candidates.Count; i++)
                {
                    if (!probe.Candidates[i].IsPublic)
                        privateCandCount++;
                }
            }
            _pickGistCandidateBtn.Visibility = privateCandCount > 1 ? Visibility.Visible : Visibility.Collapsed;

            // Auto-reconcile GistId ownership when switching accounts or onboarding a new PC
            string currentBoxGist = SyncService.NormalizeGistId(_gistIdBox.Text);
            if (string.IsNullOrEmpty(currentBoxGist) && probe.BestCandidate != null)
            {
                _suppressUiEvents = true;
                _gistIdBox.Text = probe.BestCandidate.GistId;
                _suppressUiEvents = false;
                RefreshVpsCommandPreview();
                ShowInlineBanner(
                    "已在 @" + probe.Login + " 名下自动找回私有同步库 (" + ShortId(probe.BestCandidate.GistId) + ")，包含 " +
                    probe.BestCandidate.NodeIds.Count.ToString(CultureInfo.InvariantCulture) + " 台已同步设备。点击「保存并同步」即可合并。",
                    "success");
            }
            else if (!string.IsNullOrEmpty(currentBoxGist) &&
                     string.Equals(currentBoxGist, checkedGistId, StringComparison.OrdinalIgnoreCase) &&
                     !probe.CurrentGistOwned)
            {
                _suppressUiEvents = true;
                string replacement = probe.BestCandidate != null ? probe.BestCandidate.GistId : "";
                _gistIdBox.Text = replacement;
                _suppressUiEvents = false;
                RefreshVpsCommandPreview();

                string msg = !string.IsNullOrEmpty(replacement)
                    ? ("原 Gist ID 不属于当前账号 @" + probe.Login + "，已自动切换为该账号名下的私有同步库 (" + ShortId(replacement) + ")。")
                    : ("原 Gist ID 不属于当前账号 @" + probe.Login + "，已自动清空；点击「保存并同步」将为您新建专属私有 Gist。");
                ShowInlineBanner(msg, "warning");
            }
            else if (userInitiated)
            {
                string candHint = probe.BestCandidate != null
                    ? ("已检测到名下私有同步库 " + ShortId(probe.BestCandidate.GistId))
                    : "名下暂无同步库，保存同步时将自动新建";
                NotifyUser("身份校验通过：@" + probe.Login, candHint, "success");
            }

            RefreshTopologyPreviewUi();
        }

        private void OnPickGistCandidateClick(object sender, RoutedEventArgs e)
        {
            if (_lastProbeResult == null || _lastProbeResult.Candidates == null || _lastProbeResult.Candidates.Count == 0)
                return;

            StackPanel menuSp = new StackPanel { MinWidth = 300, MaxWidth = 400 };
            TextBlock hdr = new TextBlock
            {
                Text = "选择 @" + _lastProbeResult.Login + " 名下的 TokLens 私有同步库",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 4, 8, 6)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            menuSp.Children.Add(hdr);

            for (int i = 0; i < _lastProbeResult.Candidates.Count; i++)
            {
                GitHubGistCandidate cand = _lastProbeResult.Candidates[i];
                if (cand.IsPublic) continue;
                string gid = cand.GistId;
                string title = "Gist " + ShortId(gid) + " · " + cand.NodeIds.Count.ToString(CultureInfo.InvariantCulture) + " 台设备";
                string sub = cand.NodeIds.Count > 0
                    ? ("包含节点: " + string.Join(", ", cand.NodeIds.ToArray()))
                    : "空同步库";

                Button b = MakePopoverMenuItemButton(title, sub, delegate()
                {
                    if (_gistCandidatesPopover != null) _gistCandidatesPopover.IsOpen = false;
                    _suppressUiEvents = true;
                    _gistIdBox.Text = gid;
                    _suppressUiEvents = false;
                    RefreshTopologyPreviewUi();
                    RefreshVpsCommandPreview();
                });
                menuSp.Children.Add(b);
            }

            _gistCandidatesPopover = ModernControlsFactory.CreateModernPopover(_pickGistCandidateBtn, menuSp);
            _gistCandidatesPopover.IsOpen = true;
        }

        private void RefreshTopologyPreviewUi()
        {
            if (_topologyNodesWrap == null) return;
            _topologyNodesWrap.Children.Clear();

            string localId = SyncService.SanitizeMachineId(
                string.IsNullOrWhiteSpace(_machineIdBox != null ? _machineIdBox.Text : "")
                    ? _liveCfg.MachineId
                    : _machineIdBox.Text);
            string localName = string.IsNullOrWhiteSpace(_machineNameBox != null ? _machineNameBox.Text : "")
                ? (string.IsNullOrWhiteSpace(_liveCfg.MachineName) ? Environment.MachineName : _liveCfg.MachineName)
                : _machineNameBox.Text.Trim();

            bool recentFx;
            ExchangeRateInfo fx = ExchangeRateService.LoadCachedOrDefault(out recentFx);
            double usdToCny = fx != null && fx.Rate > 0 ? fx.Rate : ExchangeRateService.DefaultRate;

            // 1. Local Node Chip
            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            double localUsd = 0.0;
            if (scan != null && scan.Records != null)
            {
                for (int i = 0; i < scan.Records.Count; i++)
                {
                    if (scan.Records[i] != null)
                        localUsd += scan.Records[i].CostUsd;
                }
            }
            _topologyNodesWrap.Children.Add(MakeTopologyNodeChip(
                localName + " (本机)",
                localId,
                "实时监听 · " + FormatSimpleMoney(localUsd, _liveCfg.PreferCny, usdToCny),
                true,
                null));

            // 2. Cached Remote Nodes
            HashSet<string> renderedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            renderedIds.Add(localId);

            List<NodeSnapshot> cachedRemotes = SyncService.LoadCachedRemoteNodes(localId);
            for (int r = 0; r < cachedRemotes.Count; r++)
            {
                NodeSnapshot rn = cachedRemotes[r];
                if (rn == null || string.IsNullOrEmpty(rn.MachineId)) continue;
                renderedIds.Add(rn.MachineId);

                double rUsd = 0.0;
                if (rn.Records != null)
                {
                    for (int k = 0; k < rn.Records.Count; k++)
                    {
                        if (rn.Records[k] != null)
                            rUsd += rn.Records[k].CostUsd;
                    }
                }
                string dispName = string.IsNullOrWhiteSpace(rn.MachineName) ? rn.MachineId : rn.MachineName;
                if (_liveCfg.NodeAliases != null && _liveCfg.NodeAliases.ContainsKey(rn.MachineId) && !string.IsNullOrWhiteSpace(_liveCfg.NodeAliases[rn.MachineId]))
                    dispName = _liveCfg.NodeAliases[rn.MachineId];

                string syncNote = string.IsNullOrWhiteSpace(rn.SyncedAt) ? "已缓存" : rn.SyncedAt;
                string capturedMid = rn.MachineId;
                string capturedName = dispName;
                _topologyNodesWrap.Children.Add(MakeTopologyNodeChip(
                    dispName,
                    rn.MachineId,
                    syncNote + " · " + FormatSimpleMoney(rUsd, _liveCfg.PreferCny, usdToCny),
                    false,
                    delegate()
                    {
                        SyncService.RemoveCachedNode(capturedMid);
                        RefreshTopologyPreviewUi();
                        if (_callbacks.OnSynced != null) _callbacks.OnSynced(null);
                        NotifyUser("已移除本地远端节点缓存", "已清理节点 " + capturedName + " (" + capturedMid + ") 的本地缓存。", "accent");
                    }));
            }

            // 3. Unsynced Cloud Nodes discovered from GitHub Probe on selected Gist
            string selGistId = SyncService.NormalizeGistId(_gistIdBox != null ? _gistIdBox.Text : "");
            if (_lastProbeResult != null && _lastProbeResult.Candidates != null && !string.IsNullOrEmpty(selGistId))
            {
                for (int i = 0; i < _lastProbeResult.Candidates.Count; i++)
                {
                    GitHubGistCandidate cand = _lastProbeResult.Candidates[i];
                    if (string.Equals(cand.GistId, selGistId, StringComparison.OrdinalIgnoreCase) && cand.NodeIds != null)
                    {
                        for (int n = 0; n < cand.NodeIds.Count; n++)
                        {
                            string cloudNodeId = cand.NodeIds[n];
                            if (!string.IsNullOrEmpty(cloudNodeId) && renderedIds.Add(cloudNodeId))
                            {
                                _topologyNodesWrap.Children.Add(MakeTopologyNodeChip(
                                    cloudNodeId,
                                    cloudNodeId,
                                    "云端已发现 · 保存同步后拉取账单",
                                    false,
                                    null));
                            }
                        }
                        break;
                    }
                }
            }

            if (_topologySummaryText != null)
            {
                _topologySummaryText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "当前拓扑已识别设备 ({0} 台 · 双层高水位防清理保护中)",
                    renderedIds.Count);
            }
        }

        private Border MakeTopologyNodeChip(string title, string machineId, string metaLine, bool isLocal, Action onRemoveCached)
        {
            Border chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 8, 7),
                Margin = new Thickness(0, 0, 8, 8),
                MinWidth = 196,
                MaxWidth = 265
            };
            chip.SetResourceReference(Border.BackgroundProperty, isLocal ? "AccentSubtleBg" : "BgCard");
            chip.SetResourceReference(Border.BorderBrushProperty, isLocal ? "AccentSubtleBorder" : "BorderCard");

            DockPanel dock = new DockPanel();
            if (onRemoveCached != null)
            {
                Button rmBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.Trash, "移除该废弃设备的本地缓存快照");
                rmBtn.Width = 22;
                rmBtn.Height = 22;
                rmBtn.Margin = new Thickness(6, 0, 0, 0);
                rmBtn.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(rmBtn, Dock.Right);
                rmBtn.Click += delegate(object s, RoutedEventArgs e) { onRemoveCached(); };
                dock.Children.Add(rmBtn);
            }

            StackPanel sp = new StackPanel();
            StackPanel topRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Path icon = UiIcons.CreateIcon(UiIcons.Devices, 12, isLocal ? "AccentText" : "FgSecondary");
            icon.Margin = new Thickness(0, 0, 5, 0);
            topRow.Children.Add(icon);

            TextBlock nameTb = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 190
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            topRow.Children.Add(nameTb);
            sp.Children.Add(topRow);

            TextBlock idTb = new TextBlock
            {
                Text = "ID: " + machineId,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 2, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            idTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(idTb);

            TextBlock metaTb = new TextBlock
            {
                Text = metaLine,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            metaTb.SetResourceReference(TextBlock.ForegroundProperty, isLocal ? "AccentText" : "FgSecondary");
            sp.Children.Add(metaTb);

            dock.Children.Add(sp);
            chip.Child = dock;
            return chip;
        }

        private void OnAutoFindOrCreateGistClick(object sender, RoutedEventArgs e)
        {
            if (_syncBusy) return;
            PullSyncFieldsIntoWorkingConfig();

            if (string.IsNullOrWhiteSpace(_workingCfg.GistToken))
            {
                NotifyUser("请先提供 GitHub 访问令牌", "点击「读取本机 gh 登录」或粘贴 PAT 令牌后再执行自动查找/新建。", "warning");
                return;
            }

            SetSyncBusyState(true);
            ShowInlineBanner("正在检索账号下已有的 TokLens 私有同步库（若不存在将自动创建并双向同步）…", "accent");

            CommitWorkingSyncToLiveConfig();
            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(scan, _liveCfg);
            string token = _liveCfg.GistToken;

            Task.Run(delegate()
            {
                try
                {
                    bool reused;
                    string resolvedGistId = SyncService.FindOrCreatePrivateGist(token, localSnap, out reused);
                    _liveCfg.GistId = resolvedGistId;
                    SyncService.SaveConfig(_liveCfg);
                    SyncOutcome outcome = SyncService.SyncNow(_liveCfg, localSnap, true);
                    if (outcome.Success)
                    {
                        string prefix = reused ? "已自动关联已有私有 Gist (" : "已新建专属私有 Gist (";
                        outcome.Message = prefix + ShortId(resolvedGistId) + ") · " + outcome.Message;
                    }

                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        SetSyncBusyState(false);
                        _workingCfg.GistId = resolvedGistId;
                        _suppressUiEvents = true;
                        _gistIdBox.Text = resolvedGistId;
                        _suppressUiEvents = false;

                        RefreshTopologyPreviewUi();
                        RefreshVpsCommandPreview();
                        ShowInlineBanner(outcome.Message, outcome.Success ? "success" : "warning");
                        NotifyUser(outcome.Success ? "私有 Gist 同步完成" : "同步返回提示", outcome.Message, outcome.Success ? "success" : "warning");
                        if (_callbacks.OnSynced != null) _callbacks.OnSynced(outcome);
                        StartGitHubIdentityProbeAsync(false);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        SetSyncBusyState(false);
                        ShowInlineBanner("关联或创建私有 Gist 失败：" + ex.Message, "danger");
                        NotifyUser("关联或创建私有 Gist 失败", ex.Message, "error");
                    }));
                }
            });
        }

        private void CommitSyncSettingsOnly()
        {
            PullSyncFieldsIntoWorkingConfig();
            CommitWorkingSyncToLiveConfig();
            RefreshTopologyPreviewUi();
            RefreshVpsCommandPreview();
            ShowInlineBanner("已保存本机标识与云端同步设置。", "success");
            NotifyUser("设置已保存", "本机名称、设备 ID 与云同步配置已安全写入磁盘。", "success");
            if (_callbacks.OnSynced != null) _callbacks.OnSynced(null);
        }

        private void ExecuteSaveAndSyncFlow()
        {
            if (_syncBusy) return;
            PullSyncFieldsIntoWorkingConfig();

            // Smart zero-friction onboarding: if Gist Token is present and Gist ID is empty, auto-find or create!
            if (!_isWebDav && !string.IsNullOrWhiteSpace(_workingCfg.GistToken) && string.IsNullOrWhiteSpace(_workingCfg.GistId))
            {
                OnAutoFindOrCreateGistClick(this, new RoutedEventArgs());
                return;
            }

            CommitWorkingSyncToLiveConfig();
            RefreshTopologyPreviewUi();
            RefreshVpsCommandPreview();

            if (!_liveCfg.IsConfigured)
            {
                ScrollToSection(0);
                NotifyUser(
                    "云同步凭据尚未完整",
                    _isWebDav
                        ? "请先填写完整的 WebDAV 目录地址与账号密码。"
                        : "请先点击「读取本机 gh 登录」或填入 GitHub 访问令牌。",
                    "warning");
                return;
            }

            SetSyncBusyState(true);
            ShowInlineBanner("正在上传本机高水位快照并拉取合并远端设备账单…", "accent");

            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(scan, _liveCfg);
            SyncConfig cfgSnapshot = _liveCfg.Clone();

            Task.Run(delegate()
            {
                SyncOutcome res = SyncService.SyncNow(cfgSnapshot, localSnap, true);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    SetSyncBusyState(false);
                    RefreshTopologyPreviewUi();
                    ShowInlineBanner(res.Message, res.Success ? "success" : "danger");
                    NotifyUser(res.Success ? "多端云同步成功" : "云端同步异常", res.Message, res.Success ? "success" : "error");
                    if (_callbacks.OnSynced != null) _callbacks.OnSynced(res);
                }));
            });
        }

        private void PullSyncFieldsIntoWorkingConfig()
        {
            NormalizeGistIdInputInPlace();
            string defaultHost = "Windows-PC";
            try { defaultHost = Environment.MachineName; } catch { }

            string cleanName = (_machineNameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(cleanName)) cleanName = defaultHost;

            string rawId = (_machineIdBox.Text ?? "").Trim();
            string cleanId = SyncService.SanitizeMachineId(string.IsNullOrEmpty(rawId) ? ("win-" + defaultHost) : rawId);

            _suppressUiEvents = true;
            _machineIdBox.Text = cleanId;
            _suppressUiEvents = false;

            _workingCfg.MachineName = cleanName;
            _workingCfg.MachineId = cleanId;
            _workingCfg.AutoSync = _autoSyncSwitch.IsChecked == true;
            _workingCfg.Provider = _isWebDav ? "webdav" : "gist";
            _workingCfg.GistToken = (_gistTokenField.Value ?? "").Trim();
            _workingCfg.GistId = SyncService.NormalizeGistId(_gistIdBox.Text);
            _workingCfg.WebDavUrl = (_davUrlBox.Text ?? "").Trim();
            _workingCfg.WebDavUser = (_davUserBox.Text ?? "").Trim();
            _workingCfg.WebDavPass = _davPassField.Value ?? "";
        }

        private void CommitWorkingSyncToLiveConfig()
        {
            string oldId = SyncService.SanitizeMachineId(_liveCfg.MachineId);
            string newId = SyncService.SanitizeMachineId(_workingCfg.MachineId);
            if (!string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
            {
                SyncService.MigrateLocalMachineId(oldId, newId);
            }

            _liveCfg.MachineName = _workingCfg.MachineName;
            _liveCfg.MachineId = newId;
            _liveCfg.AutoSync = _workingCfg.AutoSync;
            _liveCfg.Provider = _workingCfg.Provider;
            _liveCfg.GistToken = _workingCfg.GistToken;
            _liveCfg.GistId = _workingCfg.GistId;
            _liveCfg.WebDavUrl = _workingCfg.WebDavUrl;
            _liveCfg.WebDavUser = _workingCfg.WebDavUser;
            _liveCfg.WebDavPass = _workingCfg.WebDavPass;

            SyncService.SaveConfig(_liveCfg);
        }

        // -------------------- VPS & Preferences Event Handlers --------------------

        private void RefreshVpsCommandPreview()
        {
            if (_vpsCmdPreviewBox == null) return;

            SyncConfig previewCfg = _workingCfg.Clone();
            previewCfg.Provider = _isWebDav ? "webdav" : "gist";
            previewCfg.GistToken = (_gistTokenField != null ? _gistTokenField.Value : "").Trim();
            previewCfg.GistId = SyncService.NormalizeGistId(_gistIdBox != null ? _gistIdBox.Text : "");
            previewCfg.WebDavUrl = (_davUrlBox != null ? _davUrlBox.Text : "").Trim();
            previewCfg.WebDavUser = (_davUserBox != null ? _davUserBox.Text : "").Trim();
            previewCfg.WebDavPass = _davPassField != null ? _davPassField.Value : "";

            if (_vpsOAuthNoticeBorder != null)
            {
                bool showOAuthHint = !_isWebDav && previewCfg.GistToken.StartsWith("gho_", StringComparison.Ordinal);
                _vpsOAuthNoticeBorder.Visibility = showOAuthHint ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!previewCfg.IsConfigured)
            {
                _vpsCmdPreviewBox.Text = "# 请先在上方「1. 云端多设备同步」填好凭据并完成一次保存同步（以将探针脚本自动推送至您的私有库），此处将实时生成 SSH 一键部署命令。";
                return;
            }

            int mins;
            if (!int.TryParse((_vpsIntervalBox != null ? _vpsIntervalBox.Text : "30").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mins))
                mins = 30;
            string vpsName = (_vpsNameBox != null ? _vpsNameBox.Text : "").Trim();
            _vpsCmdPreviewBox.Text = SyncService.BuildLinuxVpsOneLiner(previewCfg, vpsName, mins);
        }

        private void OnCopyVpsCommandClick(object sender, RoutedEventArgs e)
        {
            PullSyncFieldsIntoWorkingConfig();
            RefreshVpsCommandPreview();
            if (!_workingCfg.IsConfigured)
            {
                ScrollToSection(0);
                NotifyUser("尚未完成云同步配置", "请先在上方完成私有 Gist 或 WebDAV 配置并同步一次，再复制 VPS 部署命令。", "warning");
                return;
            }

            if (TrySetClipboardSafe(_vpsCmdPreviewBox.Text))
            {
                NotifyUser("Linux VPS 一键部署命令已复制", "SSH 登录服务器后直接粘贴回车即可完成探针安装与定时任务配置。", "success");
            }
            else
            {
                NotifyUser("复制失败", "系统剪贴板正被其他进程占用，请手动全选命令框复制。", "warning");
            }
        }

        private void OnExportVpsAgentPyClick(object sender, RoutedEventArgs e)
        {
            string py = SyncService.GetEmbeddedVpsAgentScript();
            if (string.IsNullOrEmpty(py))
            {
                NotifyUser("未找到探针脚本资源", "当前构建未内嵌 toklens_agent.py 资源。", "warning");
                return;
            }

            try
            {
                using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
                {
                    sfd.Title = "导出 Linux VPS 零依赖采集探针脚本";
                    sfd.Filter = "Python 脚本 (*.py)|*.py";
                    sfd.FileName = "toklens_agent.py";
                    if (sfd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                    {
                        File.WriteAllText(sfd.FileName, py, new UTF8Encoding(false));
                        NotifyUser("已导出探针脚本", sfd.FileName, "success");
                    }
                }
            }
            catch (Exception ex)
            {
                NotifyUser("导出脚本失败", ex.Message, "error");
            }
        }

        private void OnBrowseHomeDirectoryClick(object sender, RoutedEventArgs e)
        {
            try
            {
                using (WinForms.FolderBrowserDialog dlg = new WinForms.FolderBrowserDialog())
                {
                    dlg.Description = "请选择要扫描的 Windows 用户主目录（例如 C:\\Users\\YourName）";
                    dlg.SelectedPath = ResolveCurrentHomeDirectory();
                    dlg.ShowNewFolderButton = false;
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    {
                        string chosen = dlg.SelectedPath.Trim();
                        _homeDirPathText.Text = chosen;
                        _homeDirPathText.ToolTip = chosen;
                        if (_callbacks.OnHomeDirectoryChanged != null)
                            _callbacks.OnHomeDirectoryChanged(chosen);
                        NotifyUser("已切换扫描目录", chosen, "success");
                    }
                }
            }
            catch (Exception ex)
            {
                NotifyUser("切换扫描目录失败", ex.Message, "error");
            }
        }

        private void OnResetHomeDirectoryClick(object sender, RoutedEventArgs e)
        {
            string defHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(defHome)) return;
            _homeDirPathText.Text = defHome;
            _homeDirPathText.ToolTip = defHome;
            if (_callbacks.OnHomeDirectoryChanged != null)
                _callbacks.OnHomeDirectoryChanged(defHome);
            NotifyUser("已恢复默认用户主目录", defHome, "success");
        }

        private void OnWslToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents) return;
            bool enabled = _wslSwitch.IsChecked == true;
            _liveCfg.ScanLocalWsl = enabled;
            _workingCfg.ScanLocalWsl = enabled;
            SyncService.SaveConfig(_liveCfg);
            if (_callbacks.OnHomeDirectoryChanged != null)
                _callbacks.OnHomeDirectoryChanged(ResolveCurrentHomeDirectory());
            NotifyUser(
                enabled ? "已开启本机 WSL2 穿透扫描" : "已关闭本机 WSL2 穿透扫描",
                enabled ? "正在后台增量扫描 WSL2 发行版日志…" : "仅扫描当前 Windows 用户目录。",
                "accent");
        }

        private void OnMinToTrayToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents) return;
            bool on = _minToTraySwitch.IsChecked == true;
            _liveCfg.MinimizeToTray = on;
            _workingCfg.MinimizeToTray = on;
            SyncService.SaveConfig(_liveCfg);
            NotifyUser(
                on ? "已开启关闭最小化到系统托盘" : "已恢复点击关闭直接退出",
                on ? "点击主窗口右上角关闭时将静默驻留至右下角系统托盘。" : "点击右上角关闭按钮将直接退出程序。",
                "accent");
        }

        private void OnSubscriptionsToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents) return;
            bool on = _subsSwitch.IsChecked == true;
            _liveCfg.ShowSubscriptions = on;
            _workingCfg.ShowSubscriptions = on;
            SyncService.SaveConfig(_liveCfg);
            if (_callbacks.OnFeaturesChanged != null)
                _callbacks.OnFeaturesChanged();
            NotifyUser(
                on ? "已开启订阅记账与回本比例" : "已关闭订阅记账与回本比例",
                on ? "侧栏已显示「订阅与费用」。" : "侧栏不再显示「订阅与费用」，已登记的订阅记录会保留。",
                "accent");
        }

        // -------------------- Visual Helpers & Utilities --------------------

        private void UpdateProviderModeVisuals()
        {
            ModernControlsFactory.UpdateSegmentedGroup(_providerBtns, _isWebDav ? 1 : 0);
            if (_gistSectionPanel != null && _webdavSectionPanel != null)
            {
                _gistSectionPanel.Visibility = _isWebDav ? Visibility.Collapsed : Visibility.Visible;
                _webdavSectionPanel.Visibility = _isWebDav ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateLocalTokenPreviewLabels(string rawToken)
        {
            string tok = (rawToken ?? "").Trim();
            if (_ghFingerprintText != null)
            {
                string fp = SyncService.MaskTokenFingerprint(tok);
                _ghFingerprintText.Text = "令牌指纹：" + (string.IsNullOrEmpty(fp) ? "—" : fp);
            }
            if (_ghTokenKindText != null)
            {
                _ghTokenKindText.Text = "凭据类型：" + SyncService.DescribeTokenKind(tok);
            }
            if (string.IsNullOrEmpty(tok))
            {
                SetIdentityBadgeState("未配置令牌", "neutral", "等待读取或输入 GitHub 访问令牌");
            }
            else if (_lastProbeResult == null)
            {
                // 已有令牌但还没校验：不能沿用初始的「未配置令牌」，也不能提前说「已认证」。
                SetIdentityBadgeState("待校验", "neutral", "已保存令牌，打开此页后会自动校验账号与 gist 权限");
            }
        }

        private void SetIdentityBadgeState(string pillText, string variant, string detailText)
        {
            if (_ghAuthStatusLabel != null)
                _ghAuthStatusLabel.Text = pillText ?? "";
            if (_ghAccountHandleText != null)
                _ghAccountHandleText.Text = detailText ?? "";

            string bgKey = "BgCard";
            string bdKey = "BorderCard";
            string fgKey = "FgSecondary";

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticSuccessBg";
                bdKey = "SemanticSuccessBorder";
                fgKey = "SemanticSuccess";
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticWarningBg";
                bdKey = "SemanticWarningBorder";
                fgKey = "SemanticWarning";
            }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticDangerBg";
                bdKey = "SemanticDangerBorder";
                fgKey = "SemanticDanger";
            }
            else if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "AccentSubtleBg";
                bdKey = "AccentSubtleBorder";
                fgKey = "AccentText";
            }

            if (_ghAuthStatusPill != null)
            {
                _ghAuthStatusPill.SetResourceReference(Border.BackgroundProperty, bgKey);
                _ghAuthStatusPill.SetResourceReference(Border.BorderBrushProperty, bdKey);
            }
            if (_ghAuthStatusLabel != null)
                _ghAuthStatusLabel.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            if (_ghAuthStatusDot != null)
                _ghAuthStatusDot.SetResourceReference(Shapes.Shape.FillProperty, fgKey);
        }

        private void ShowInlineBanner(string message, string variant)
        {
            if (_inlineBannerBorder == null || _inlineBannerText == null) return;
            _inlineBannerText.Text = message ?? "";

            string bgKey = "AccentSubtleBg";
            string bdKey = "AccentSubtleBorder";
            string fgKey = "AccentText";
            string iconPath = UiIcons.Activity;

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticSuccessBg";
                bdKey = "SemanticSuccessBorder";
                fgKey = "SemanticSuccess";
                iconPath = UiIcons.Check;
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticWarningBg";
                bdKey = "SemanticWarningBorder";
                fgKey = "SemanticWarning";
                iconPath = UiIcons.Alert;
            }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(variant, "error", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticDangerBg";
                bdKey = "SemanticDangerBorder";
                fgKey = "SemanticDanger";
                iconPath = UiIcons.Alert;
            }

            _inlineBannerBorder.SetResourceReference(Border.BackgroundProperty, bgKey);
            _inlineBannerBorder.SetResourceReference(Border.BorderBrushProperty, bdKey);
            if (_inlineBannerIcon != null)
            {
                _inlineBannerIcon.Data = Geometry.Parse(iconPath);
                _inlineBannerIcon.SetResourceReference(Shapes.Shape.StrokeProperty, fgKey);
            }
            _inlineBannerBorder.Visibility = Visibility.Visible;
        }

        private void NotifyUser(string title, string detail, string variant)
        {
            if (_callbacks != null && _callbacks.ShowToast != null)
            {
                _callbacks.ShowToast(title, detail, variant);
            }
            else
            {
                string joined = string.IsNullOrEmpty(detail) ? title : (title + " — " + detail);
                ShowInlineBanner(joined, variant);
            }
        }

        private void SetSyncBusyState(bool busy)
        {
            _syncBusy = busy;
            if (_saveAndSyncBtn != null) _saveAndSyncBtn.IsEnabled = !busy;
            if (_saveOnlyBtn != null) _saveOnlyBtn.IsEnabled = !busy;
            if (_autoGistBtn != null) _autoGistBtn.IsEnabled = !busy;
        }

        private void NormalizeGistIdInputInPlace()
        {
            if (_gistIdBox == null) return;
            string raw = _gistIdBox.Text ?? "";
            string norm = SyncService.NormalizeGistId(raw);
            if (!string.Equals(raw, norm, StringComparison.Ordinal))
            {
                _suppressUiEvents = true;
                _gistIdBox.Text = norm;
                _gistIdBox.CaretIndex = norm.Length;
                _suppressUiEvents = false;
            }
        }

        private string ResolveCurrentHomeDirectory()
        {
            if (_getCurrentHomeDir != null)
            {
                string h = _getCurrentHomeDir();
                if (!string.IsNullOrWhiteSpace(h)) return h;
            }
            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            if (scan != null && !string.IsNullOrWhiteSpace(scan.UserHome))
                return scan.UserHome;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static string ShortId(string gistId)
        {
            if (string.IsNullOrEmpty(gistId)) return "";
            return gistId.Length <= 10 ? gistId : (gistId.Substring(0, 8) + "…");
        }

        private static string FormatSimpleMoney(double usd, bool preferCny, double rate)
        {
            double val = preferCny ? (usd * rate) : usd;
            string sym = preferCny ? "¥" : "$";
            return sym + val.ToString("N2", CultureInfo.InvariantCulture);
        }

        private static bool TrySetClipboardSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                try
                {
                    Clipboard.SetDataObject(text, false);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string TryGetClipboardSafe()
        {
            try
            {
                return Clipboard.ContainsText() ? (Clipboard.GetText() ?? "") : "";
            }
            catch
            {
                return "";
            }
        }

        private static void OpenExternalUrlSafe(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        // -------------------- Reusable Structural Builders --------------------

        private static StackPanel MakeCardHeaderBlock(string iconData, string title, string subtitle)
        {
            StackPanel sp = new StackPanel();
            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Border iconBadge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBadge.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            iconBadge.Child = UiIcons.CreateIcon(iconData, 13, "AccentText");
            titleRow.Children.Add(iconBadge);

            TextBlock t = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextTitle,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleRow.Children.Add(t);
            sp.Children.Add(titleRow);

            if (!string.IsNullOrEmpty(subtitle))
            {
                TextBlock sub = new TextBlock
                {
                    Text = subtitle,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextCaption,
                    Margin = new Thickness(32, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                sp.Children.Add(sub);
            }
            return sp;
        }

        private static TextBlock MakeFieldLabel(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return tb;
        }

        private static Border MakeSubtleCallout(string iconData, string text, string bgKey, string bdKey, string iconKey)
        {
            Border b = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8)
            };
            b.SetResourceReference(Border.BackgroundProperty, bgKey);
            b.SetResourceReference(Border.BorderBrushProperty, bdKey);

            DockPanel dock = new DockPanel();
            Shapes.Path icon = UiIcons.CreateIcon(iconData, 13, iconKey);
            icon.Margin = new Thickness(0, 2, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);

            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            dock.Children.Add(tb);

            b.Child = dock;
            return b;
        }

        private static Border MakeSettingRowContainer(bool bottomDivider)
        {
            Border b = new Border
            {
                Padding = new Thickness(0, 12, 0, 12),
                BorderThickness = new Thickness(0, 0, 0, bottomDivider ? 1 : 0)
            };
            if (bottomDivider)
                b.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");
            return b;
        }

        private static TextBlock MakeRowTitle(string title)
        {
            TextBlock tb = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.Medium
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            return tb;
        }

        private static StackPanel MakeRowTitleAndDesc(string title, string desc)
        {
            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(MakeRowTitle(title));
            if (!string.IsNullOrEmpty(desc))
            {
                TextBlock d = new TextBlock
                {
                    Text = desc,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextCaption,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                d.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                sp.Children.Add(d);
            }
            return sp;
        }

        private static Border MakeMiniMetricTile(string label, out TextBlock valOut, out TextBlock subOut)
        {
            Border tile = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10)
            };
            tile.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            tile.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel();
            TextBlock lbl = new TextBlock
            {
                Text = label,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(lbl);

            valOut = new TextBlock
            {
                Text = "—",
                FontSize = DesignSystem.TextTitle,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(valOut, false);
            valOut.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(valOut);

            subOut = new TextBlock
            {
                Text = "",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subOut.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(subOut);

            tile.Child = sp;
            return tile;
        }

        private static Border MakeMethodologyItem(string heading, string body)
        {
            Border b = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 8, 0, 0)
            };
            b.SetResourceReference(Border.BackgroundProperty, "BgCard");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            StackPanel sp = new StackPanel();
            TextBlock h = new TextBlock
            {
                Text = heading,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            };
            h.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(h);

            TextBlock p = new TextBlock
            {
                Text = body,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                LineHeight = 17.5,
                TextWrapping = TextWrapping.Wrap
            };
            p.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            sp.Children.Add(p);

            b.Child = sp;
            return b;
        }

        private static Button MakePopoverMenuItemButton(string title, string subtitle, Action onClick)
        {
            Button btn = new Button
            {
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 7, 10, 7)
            };

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "ItemBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hv = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hv.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverStrong"), "ItemBd"));
            tpl.Triggers.Add(hv);
            btn.Template = tpl;

            StackPanel sp = new StackPanel();
            TextBlock t = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sp.Children.Add(t);

            if (!string.IsNullOrEmpty(subtitle))
            {
                TextBlock s = new TextBlock
                {
                    Text = subtitle,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextCaption,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                s.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                sp.Children.Add(s);
            }

            btn.Content = sp;
            btn.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (onClick != null) onClick();
            };
            return btn;
        }
    }

}
