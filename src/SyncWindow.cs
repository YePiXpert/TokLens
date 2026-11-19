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
        public Action<string, string, string> ShowToast;
    }

    /// <summary>
    /// 「设置 → 云同步」页：单层精简同步表单 + 按需展开的配对码、VPS 探针与设备清理区。
    /// </summary>
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
                Root = new Border
                {
                    Height = 32,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 0, 4, 0),
                    SnapsToDevicePixels = true
                };
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

                _pwdBox = new PasswordBox
                {
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextBody,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                _pwdBox.SetResourceReference(PasswordBox.ForegroundProperty, "FgPrimary");
                _pwdBox.PasswordChanged += OnPwdChanged;
                _pwdBox.GotKeyboardFocus += OnChildGotFocus;
                _pwdBox.LostKeyboardFocus += OnChildLostFocus;

                _plainBox = new TextBox
                {
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextBody,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
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

        private readonly SyncConfig _liveCfg;
        private SyncConfig _workingCfg;
        private readonly Func<ScanResult> _getLatestScan;
        private readonly SyncSettingsCallbacks _callbacks;

        private bool _suppressUiEvents = false;
        private bool _isWebDav = false;
        private bool _syncBusy = false;
        private int _probeRequestSeq = 0;
        private GitHubProbeResult _lastProbeResult = null;
        private DispatcherTimer _tokenProbeDebounceTimer;

        private Border _inlineBannerBorder;
        private Shapes.Path _inlineBannerIcon;
        private TextBlock _inlineBannerText;

        private Button[] _providerBtns;
        private StackPanel _gistSectionPanel;
        private StackPanel _webdavSectionPanel;

        private Border _ghAuthStatusPill;
        private TextBlock _ghAuthStatusLabel;
        private Shapes.Ellipse _ghAuthStatusDot;
        private Border _ghScopeFixBox;
        private TextBlock _ghScopeFixText;
        private Button _ghReadCliBtn;
        private Popup _ghAccountPopover;

        private ModernSecretField _gistTokenField;
        private TextBox _gistIdBox;
        private Button _autoGistBtn;
        private Button _pickGistCandidateBtn;
        private Popup _gistCandidatesPopover;

        private TextBox _davUrlBox;
        private TextBox _davUserBox;
        private ModernSecretField _davPassField;

        private TextBox _machineNameBox;
        private TextBox _machineIdBox;
        private CheckBox _autoSyncSwitch;
        private Button _saveOnlyBtn;
        private Button _saveAndSyncBtn;

        // 按需展开：VPS 探针与云端设备列表
        private Button _vpsToggleBtn;
        private Border _vpsDrawer;
        private bool _vpsExpanded = false;
        private TextBox _vpsNameBox;
        private TextBox _vpsIntervalBox;
        private TextBox _vpsCmdPreviewBox;
        private TextBlock _vpsOAuthHint;

        private Button _devicesToggleBtn;
        private Border _devicesDrawer;
        private bool _devicesExpanded = false;
        private StackPanel _devicesListPanel;

        public SyncSettingsView(
            SyncConfig liveCfg,
            Func<ScanResult> getLatestScan,
            SyncSettingsCallbacks callbacks)
        {
            _liveCfg = liveCfg ?? new SyncConfig();
            _workingCfg = _liveCfg.Clone();
            _getLatestScan = getLatestScan;
            _callbacks = callbacks ?? new SyncSettingsCallbacks();

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            SetResourceReference(Border.BackgroundProperty, "BgWindow");

            _tokenProbeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
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
            RefreshDeviceListUi();
            if (!_isWebDav && !string.IsNullOrWhiteSpace(_gistTokenField.Value) && _lastProbeResult == null)
                StartGitHubIdentityProbeAsync(false);
        }

        private void OnViewUnloaded(object sender, RoutedEventArgs e)
        {
            if (_tokenProbeDebounceTimer != null) _tokenProbeDebounceTimer.Stop();
        }

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

                UpdateProviderModeVisuals();
                UpdateLocalTokenPreviewState(_gistTokenField.Value);
                RefreshDeviceListUi();
                RefreshVpsCommandPreview();
            }
            finally
            {
                _suppressUiEvents = false;
            }
        }

        public void RefreshDynamicStats()
        {
            RefreshDeviceListUi();
            RefreshVpsCommandPreview();
        }

        private UIElement BuildRootLayout()
        {
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(20, 16, 20, 24)
            };

            StackPanel center = new StackPanel
            {
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // 内联状态反馈条（按需显示）
            _inlineBannerBorder = BuildInlineFeedbackBanner();
            center.Children.Add(_inlineBannerBorder);

            // 主同步表单卡片（扁平单层）
            center.Children.Add(BuildMainSyncCard());

            // 底部按需扩展条：配对码 / VPS 探针 / 已同步设备
            center.Children.Add(BuildSecondaryActionsBar());

            // 折叠区 A：Linux VPS 探针部署
            _vpsDrawer = BuildVpsDrawer();
            center.Children.Add(_vpsDrawer);

            // 折叠区 B：已同步设备列表
            _devicesDrawer = BuildDevicesDrawer();
            center.Children.Add(_devicesDrawer);

            scroll.Content = center;
            return scroll;
        }

        private Border BuildInlineFeedbackBanner()
        {
            Border b = new Border
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(12, 8, 10, 8),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1)
            };
            b.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBg");
            b.SetResourceReference(Border.BorderBrushProperty, "AccentSubtleBorder");

            DockPanel dock = new DockPanel();
            Button dismissBtn = ModernControlsFactory.CreateIconGhostButton("M 3,3 L 13,13 M 13,3 L 3,13", "收起提示");
            dismissBtn.Width = 22;
            dismissBtn.Height = 22;
            dismissBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(dismissBtn, Dock.Right);
            dismissBtn.Click += delegate(object s, RoutedEventArgs e) { b.Visibility = Visibility.Collapsed; };
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

        private Border BuildMainSyncCard()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(20, 16, 20, 16));
            StackPanel sp = new StackPanel();

            // 首行：同步方式切换 + 状态徽章
            DockPanel topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };

            _ghAuthStatusPill = ModernControlsFactory.CreateStatusBadge("未配置", "neutral", out _ghAuthStatusLabel, out _ghAuthStatusDot);
            _ghAuthStatusPill.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_ghAuthStatusPill, Dock.Right);
            topRow.Children.Add(_ghAuthStatusPill);

            Border provBox = ModernControlsFactory.CreateSegmentedContainer();
            provBox.HorizontalAlignment = HorizontalAlignment.Left;
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
            topRow.Children.Add(provBox);
            sp.Children.Add(topRow);

            // --- GitHub Gist 字段 ---
            _gistSectionPanel = new StackPanel();

            // 令牌标题行 + 快捷操作
            DockPanel tokHdr = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            StackPanel tokBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tokBtns, Dock.Right);

            _ghReadCliBtn = ModernControlsFactory.CreateButton("读取 gh", "ghost");
            _ghReadCliBtn.Height = 22;
            _ghReadCliBtn.FontSize = DesignSystem.TextCaption;
            _ghReadCliBtn.Padding = new Thickness(7, 0, 7, 0);
            _ghReadCliBtn.Margin = new Thickness(0, 0, 4, 0);
            _ghReadCliBtn.ToolTip = "自动读取本机 GitHub CLI (gh) 已登录账号的令牌（每台电脑 gho_ 令牌不同属正常，同账号自动互通）";
            _ghReadCliBtn.Click += OnReadGhCliAccountsClick;
            tokBtns.Children.Add(_ghReadCliBtn);

            Button verifyBtn = ModernControlsFactory.CreateButton("校验", "ghost");
            verifyBtn.Height = 22;
            verifyBtn.FontSize = DesignSystem.TextCaption;
            verifyBtn.Padding = new Thickness(7, 0, 7, 0);
            verifyBtn.Margin = new Thickness(0, 0, 4, 0);
            verifyBtn.ToolTip = "连接 GitHub 验证令牌所属账号与 gist 权限";
            verifyBtn.Click += delegate(object s, RoutedEventArgs e) { StartGitHubIdentityProbeAsync(true); };
            tokBtns.Children.Add(verifyBtn);

            Button applyTokBtn = ModernControlsFactory.CreateButton("申请 Token", "ghost");
            applyTokBtn.Height = 22;
            applyTokBtn.FontSize = DesignSystem.TextCaption;
            applyTokBtn.Padding = new Thickness(7, 0, 7, 0);
            applyTokBtn.ToolTip = "在浏览器打开 GitHub 经典 Token 创建页（已预选 gist 权限）";
            applyTokBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                OpenExternalUrlSafe("https://github.com/settings/tokens/new?description=TokLens-Sync&scopes=gist");
            };
            tokBtns.Children.Add(applyTokBtn);
            tokHdr.Children.Add(tokBtns);

            TextBlock tokLabel = MakeFieldLabel("GitHub 访问令牌");
            tokLabel.Margin = new Thickness(0);
            tokLabel.VerticalAlignment = VerticalAlignment.Center;
            tokLabel.ToolTip = "支持 gh CLI 的 gho_ 令牌或勾选了 gist 权限的 PAT；凭据仅用本机 Windows DPAPI 加密存储，同步不含对话正文。";
            tokHdr.Children.Add(tokLabel);
            _gistSectionPanel.Children.Add(tokHdr);

            _gistTokenField = new ModernSecretField("支持 gho_ / ghp_ / github_pat_；每台设备 gh 签发的 gho_ 不同属正常，同一 GitHub 账号自动合并同一 Gist");
            _gistTokenField.ValueChanged += delegate()
            {
                if (_suppressUiEvents) return;
                string tok = _gistTokenField.Value.Trim();
                UpdateLocalTokenPreviewState(tok);
                RefreshVpsCommandPreview();
                _tokenProbeDebounceTimer.Stop();
                if (!string.IsNullOrEmpty(tok) && tok.Length >= 16)
                    _tokenProbeDebounceTimer.Start();
            };
            _gistSectionPanel.Children.Add(_gistTokenField.Root);

            // 缺少 gist 权限时的可操作提示（仅缺权时出现）
            _ghScopeFixBox = new Border
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1)
            };
            _ghScopeFixBox.SetResourceReference(Border.BackgroundProperty, "SemanticWarningBg");
            _ghScopeFixBox.SetResourceReference(Border.BorderBrushProperty, "SemanticWarningBorder");

            DockPanel scopeDock = new DockPanel();
            Button copyFixBtn = ModernControlsFactory.CreateButton("复制补权命令", "secondary");
            copyFixBtn.Height = 24;
            copyFixBtn.FontSize = DesignSystem.TextCaption;
            copyFixBtn.Padding = new Thickness(8, 0, 8, 0);
            copyFixBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(copyFixBtn, Dock.Right);
            copyFixBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                const string cmd = "gh auth refresh -h github.com -s gist";
                if (TrySetClipboardSafe(cmd))
                    NotifyUser("补齐权限命令已复制", "终端执行 " + cmd + " 后再点「读取 gh」。", "success");
            };
            scopeDock.Children.Add(copyFixBtn);

            _ghScopeFixText = new TextBlock
            {
                Text = "当前令牌缺少 gist 权限，请在终端执行 gh auth refresh -h github.com -s gist。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            _ghScopeFixText.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
            scopeDock.Children.Add(_ghScopeFixText);
            _ghScopeFixBox.Child = scopeDock;
            _gistSectionPanel.Children.Add(_ghScopeFixBox);

            // Gist ID 行
            TextBlock gistLbl = MakeFieldLabel("私有 Gist ID");
            gistLbl.Margin = new Thickness(0, 12, 0, 5);
            gistLbl.ToolTip = "首次使用可留空，点击「保存并同步」将自动查找或新建；支持直接粘贴完整 Gist 网址";
            _gistSectionPanel.Children.Add(gistLbl);

            Grid gistGrid = new Grid();
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            gistGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _gistIdBox = ModernControlsFactory.CreateTextInput("", 32);
            _gistIdBox.FontFamily = DesignSystem.FontMono;
            _gistIdBox.ToolTip = "留空则自动查找或新建；可直接粘贴 Gist 链接";
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
                    NormalizeGistIdInputInPlace();
                RefreshDeviceListUi();
                RefreshVpsCommandPreview();
            };
            Grid.SetColumn(_gistIdBox, 0);
            gistGrid.Children.Add(_gistIdBox);

            _pickGistCandidateBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.ChevronDown, "切换库", "secondary");
            _pickGistCandidateBtn.Height = 32;
            _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
            _pickGistCandidateBtn.Click += OnPickGistCandidateClick;
            Grid.SetColumn(_pickGistCandidateBtn, 2);
            gistGrid.Children.Add(_pickGistCandidateBtn);

            _autoGistBtn = ModernControlsFactory.CreateButton("自动查找 / 新建", "secondary");
            _autoGistBtn.Height = 32;
            _autoGistBtn.ToolTip = "查找当前账号下已有的 TokLens 私有同步库；若不存在则自动新建并同步";
            _autoGistBtn.Click += OnAutoFindOrCreateGistClick;
            Grid.SetColumn(_autoGistBtn, 4);
            gistGrid.Children.Add(_autoGistBtn);

            Button openGistBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.ExternalLink, "在浏览器查看该 Gist");
            openGistBtn.Width = 32;
            openGistBtn.Height = 32;
            openGistBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                string gid = SyncService.NormalizeGistId(_gistIdBox.Text);
                if (string.IsNullOrEmpty(gid))
                {
                    NotifyUser("尚未关联 Gist ID", "请先点击「自动查找 / 新建」或执行一次同步。", "warning");
                    return;
                }
                OpenExternalUrlSafe("https://gist.github.com/" + gid);
            };
            Grid.SetColumn(openGistBtn, 6);
            gistGrid.Children.Add(openGistBtn);
            _gistSectionPanel.Children.Add(gistGrid);
            sp.Children.Add(_gistSectionPanel);

            // --- WebDAV 字段 ---
            _webdavSectionPanel = new StackPanel { Visibility = Visibility.Collapsed };
            _webdavSectionPanel.Children.Add(MakeFieldLabel("WebDAV 目录地址"));
            _davUrlBox = ModernControlsFactory.CreateTextInput("", 32);
            _davUrlBox.FontFamily = DesignSystem.FontMono;
            _davUrlBox.ToolTip = "例如：https://dav.jianguoyun.com/dav/TokLens/";
            _davUrlBox.TextChanged += delegate(object s, TextChangedEventArgs e) { if (!_suppressUiEvents) RefreshVpsCommandPreview(); };
            _webdavSectionPanel.Children.Add(_davUrlBox);

            Grid davCredGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            davCredGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            davCredGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            davCredGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel davUserCol = new StackPanel();
            davUserCol.Children.Add(MakeFieldLabel("用户名 / 邮箱"));
            _davUserBox = ModernControlsFactory.CreateTextInput("", 32);
            _davUserBox.TextChanged += delegate(object s, TextChangedEventArgs e) { if (!_suppressUiEvents) RefreshVpsCommandPreview(); };
            davUserCol.Children.Add(_davUserBox);
            Grid.SetColumn(davUserCol, 0);
            davCredGrid.Children.Add(davUserCol);

            StackPanel davPassCol = new StackPanel();
            davPassCol.Children.Add(MakeFieldLabel("应用专用密码"));
            _davPassField = new ModernSecretField("建议使用网盘独立应用密码，本机 DPAPI 加密保存");
            _davPassField.ValueChanged += delegate() { if (!_suppressUiEvents) RefreshVpsCommandPreview(); };
            davPassCol.Children.Add(_davPassField.Root);
            Grid.SetColumn(davPassCol, 2);
            davCredGrid.Children.Add(davPassCol);

            _webdavSectionPanel.Children.Add(davCredGrid);
            sp.Children.Add(_webdavSectionPanel);

            // --- 本机标识（两列并排）---
            Grid localIdGrid = new Grid { Margin = new Thickness(0, 12, 0, 14) };
            localIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            localIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            localIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            StackPanel nameCol = new StackPanel();
            nameCol.Children.Add(MakeFieldLabel("本机显示名称"));
            _machineNameBox = ModernControlsFactory.CreateTextInput("", 32);
            _machineNameBox.ToolTip = "如：办公电脑 / 家里主力机";
            nameCol.Children.Add(_machineNameBox);
            Grid.SetColumn(nameCol, 0);
            localIdGrid.Children.Add(nameCol);

            StackPanel idCol = new StackPanel();
            idCol.Children.Add(MakeFieldLabel("本机设备 ID"));
            _machineIdBox = ModernControlsFactory.CreateTextInput("", 32);
            _machineIdBox.FontFamily = DesignSystem.FontMono;
            _machineIdBox.ToolTip = "各设备须唯一；修改后会自动无损迁移本机账本";
            idCol.Children.Add(_machineIdBox);
            Grid.SetColumn(idCol, 2);
            localIdGrid.Children.Add(idCol);
            sp.Children.Add(localIdGrid);

            // --- 底部操作栏：自动同步开关 + 仅保存 / 保存并同步 ---
            Border footer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 12, 0, 0)
            };
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel footerDock = new DockPanel();
            StackPanel saveBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(saveBtns, Dock.Right);

            _saveOnlyBtn = ModernControlsFactory.CreateButton("仅保存", "ghost");
            _saveOnlyBtn.Height = 30;
            _saveOnlyBtn.Margin = new Thickness(0, 0, 8, 0);
            _saveOnlyBtn.ToolTip = "只保存同步配置，不立即联网";
            _saveOnlyBtn.Click += delegate(object s, RoutedEventArgs e) { CommitSyncSettingsOnly(); };
            saveBtns.Children.Add(_saveOnlyBtn);

            _saveAndSyncBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Refresh, "保存并同步", "primary");
            _saveAndSyncBtn.Height = 30;
            _saveAndSyncBtn.Click += delegate(object s, RoutedEventArgs e) { ExecuteSaveAndSyncFlow(); };
            saveBtns.Children.Add(_saveAndSyncBtn);
            footerDock.Children.Add(saveBtns);

            _autoSyncSwitch = ModernControlsFactory.CreateToggleSwitch("后台自动同步（每 5 分钟）");
            _autoSyncSwitch.VerticalAlignment = VerticalAlignment.Center;
            _autoSyncSwitch.ToolTip = "仅同步按日汇总的脱敏 Token 与金额数字，绝不上传对话正文";
            footerDock.Children.Add(_autoSyncSwitch);

            footer.Child = footerDock;
            sp.Children.Add(footer);

            card.Child = sp;
            return card;
        }

        private UIElement BuildSecondaryActionsBar()
        {
            WrapPanel bar = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button copyPair = ModernControlsFactory.CreateIconTextButton(UiIcons.Copy, "复制配对码", "secondary");
            copyPair.Height = 30;
            copyPair.Margin = new Thickness(0, 0, 8, 6);
            copyPair.ToolTip = "复制含同步凭据的加密配对码，用于快速接入另一台电脑（切勿发到公开群聊）";
            copyPair.Click += delegate(object s, RoutedEventArgs e) { TriggerQuickPairCopy(); };
            bar.Children.Add(copyPair);

            Button pastePair = ModernControlsFactory.CreateIconTextButton(UiIcons.Key, "粘贴配对码", "secondary");
            pastePair.Height = 30;
            pastePair.Margin = new Thickness(0, 0, 8, 6);
            pastePair.ToolTip = "从剪贴板导入另一台电脑生成的配对码";
            pastePair.Click += delegate(object s, RoutedEventArgs e) { TriggerQuickPairPaste(); };
            bar.Children.Add(pastePair);

            _vpsToggleBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Terminal, "VPS 探针部署 ▾", "secondary");
            _vpsToggleBtn.Height = 30;
            _vpsToggleBtn.Margin = new Thickness(0, 0, 8, 6);
            _vpsToggleBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _vpsExpanded = !_vpsExpanded;
                _vpsDrawer.Visibility = _vpsExpanded ? Visibility.Visible : Visibility.Collapsed;
                UpdateToggleLabel(_vpsToggleBtn, _vpsExpanded ? "VPS 探针部署 ▴" : "VPS 探针部署 ▾");
                if (_vpsExpanded) RefreshVpsCommandPreview();
            };
            bar.Children.Add(_vpsToggleBtn);

            _devicesToggleBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Devices, "已同步设备 ▾", "secondary");
            _devicesToggleBtn.Height = 30;
            _devicesToggleBtn.Margin = new Thickness(0, 0, 0, 6);
            _devicesToggleBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                _devicesExpanded = !_devicesExpanded;
                _devicesDrawer.Visibility = _devicesExpanded ? Visibility.Visible : Visibility.Collapsed;
                RefreshDeviceListUi();
            };
            bar.Children.Add(_devicesToggleBtn);

            return bar;
        }

        private Border BuildVpsDrawer()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(18, 14, 18, 14));
            card.Margin = new Thickness(0, 6, 0, 0);
            card.Visibility = Visibility.Collapsed;

            StackPanel sp = new StackPanel();
            Grid cfgGrid = new Grid();
            cfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            cfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            cfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            cfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            cfgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel nameCol = new StackPanel();
            nameCol.Children.Add(MakeFieldLabel("VPS 显示名称"));
            _vpsNameBox = ModernControlsFactory.CreateTextInput("主力 VPS-1", 30);
            _vpsNameBox.TextChanged += delegate(object s, TextChangedEventArgs e) { if (!_suppressUiEvents) RefreshVpsCommandPreview(); };
            nameCol.Children.Add(_vpsNameBox);
            Grid.SetColumn(nameCol, 0);
            cfgGrid.Children.Add(nameCol);

            StackPanel intCol = new StackPanel();
            intCol.Children.Add(MakeFieldLabel("间隔（分钟）"));
            _vpsIntervalBox = ModernControlsFactory.CreateTextInput("30", 30);
            _vpsIntervalBox.FontFamily = DesignSystem.FontMono;
            _vpsIntervalBox.TextChanged += delegate(object s, TextChangedEventArgs e) { if (!_suppressUiEvents) RefreshVpsCommandPreview(); };
            intCol.Children.Add(_vpsIntervalBox);
            Grid.SetColumn(intCol, 2);
            cfgGrid.Children.Add(intCol);

            Button copyCmdBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Copy, "复制 SSH 部署命令", "secondary");
            copyCmdBtn.Height = 30;
            copyCmdBtn.VerticalAlignment = VerticalAlignment.Bottom;
            copyCmdBtn.Click += OnCopyVpsCommandClick;
            Grid.SetColumn(copyCmdBtn, 4);
            cfgGrid.Children.Add(copyCmdBtn);
            sp.Children.Add(cfgGrid);

            _vpsCmdPreviewBox = ModernControlsFactory.CreateTextInput("", 62);
            _vpsCmdPreviewBox.IsReadOnly = true;
            _vpsCmdPreviewBox.TextWrapping = TextWrapping.Wrap;
            _vpsCmdPreviewBox.VerticalContentAlignment = VerticalAlignment.Top;
            _vpsCmdPreviewBox.Padding = new Thickness(9, 7, 9, 7);
            _vpsCmdPreviewBox.FontFamily = DesignSystem.FontMono;
            _vpsCmdPreviewBox.FontSize = DesignSystem.TextCaption;
            _vpsCmdPreviewBox.Margin = new Thickness(0, 10, 0, 8);
            sp.Children.Add(_vpsCmdPreviewBox);

            DockPanel bot = new DockPanel();
            Button exportPyBtn = ModernControlsFactory.CreateIconTextButton(UiIcons.Download, "导出 toklens_agent.py", "ghost");
            exportPyBtn.Height = 26;
            exportPyBtn.Click += OnExportVpsAgentPyClick;
            DockPanel.SetDock(exportPyBtn, Dock.Left);
            bot.Children.Add(exportPyBtn);

            _vpsOAuthHint = new TextBlock
            {
                Text = "提示：若日后在本机注销 gh，使用 gho_ 的 VPS 需改用独立 PAT",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed
            };
            _vpsOAuthHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            bot.Children.Add(_vpsOAuthHint);
            sp.Children.Add(bot);

            card.Child = sp;
            return card;
        }

        private Border BuildDevicesDrawer()
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(16, 10, 16, 10));
            card.Margin = new Thickness(0, 6, 0, 0);
            card.Visibility = Visibility.Collapsed;

            _devicesListPanel = new StackPanel();
            card.Child = _devicesListPanel;
            return card;
        }

        // -------------------- 配对码 / gh CLI / 校验 / 同步逻辑 --------------------

        public void TriggerQuickPairCopy()
        {
            PullSyncFieldsIntoWorkingConfig();
            if (!_workingCfg.IsConfigured)
            {
                NotifyUser("尚未完成云同步配置", "请先填写 GitHub 令牌与 Gist ID（或 WebDAV）后再复制配对码。", "warning");
                return;
            }
            string pairCode = SyncService.GeneratePairingCode(_workingCfg);
            if (TrySetClipboardSafe(pairCode))
                NotifyUser("配对码已复制", "在另一台电脑点「粘贴配对码」导入。配对码含同步凭据，请勿公开分享。", "success");
            else
                NotifyUser("剪贴板占用", "请稍后重试复制配对码。", "warning");
        }

        public void TriggerQuickPairPaste()
        {
            string clipText = TryGetClipboardSafe();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                NotifyUser("剪贴板为空", "请先在已配置电脑点「复制配对码」。", "warning");
                return;
            }

            SyncConfig tempCfg = _workingCfg.Clone();
            string err;
            if (!SyncService.TryApplyPairingCode(tempCfg, clipText, out err))
            {
                NotifyUser("配对码无效", err ?? "剪贴板内容不是有效的 TokLens 配对码。", "error");
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
                UpdateLocalTokenPreviewState(_workingCfg.GistToken);
            }
            finally
            {
                _suppressUiEvents = false;
            }

            RefreshVpsCommandPreview();
            try { _machineNameBox.Focus(); _machineNameBox.SelectAll(); } catch { }
            NotifyUser("已导入配对码（待保存）", "确认「本机显示名称」后点击「保存并同步」即可生效。", "success");
            if (!_isWebDav && !string.IsNullOrWhiteSpace(_workingCfg.GistToken))
                StartGitHubIdentityProbeAsync(false);
        }

        private void OnReadGhCliAccountsClick(object sender, RoutedEventArgs e)
        {
            _ghReadCliBtn.IsEnabled = false;
            SetIdentityBadgeState("读取 gh…", "accent", "正在扫描本机 GitHub CLI 已登录账号");

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
                        SetIdentityBadgeState("未检测到 gh", "warning", "本机未登录 GitHub CLI");
                        NotifyUser("未检测到已登录的 gh CLI", "可在终端运行 gh auth login，或点击「申请 Token」。", "warning");
                        return;
                    }
                    if (accounts.Count == 1 && !hasEnvOverride)
                    {
                        ApplyGhCliAccountSelection(accounts[0].Login, true);
                        return;
                    }
                    ShowGhAccountsPickerPopover(accounts, hasEnvOverride, envVarName);
                }));
            });
        }

        private void ShowGhAccountsPickerPopover(List<GhCliAccountInfo> accounts, bool hasEnvOverride, string envVarName)
        {
            StackPanel menuSp = new StackPanel { MinWidth = 260, MaxWidth = 340 };
            TextBlock hdr = new TextBlock
            {
                Text = "选择 GitHub 账号",
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
                string tag = acc.IsActive ? " (当前)" : "";
                menuSp.Children.Add(MakePopoverMenuItemButton(
                    "@" + login + tag,
                    "读取本机 gh CLI 凭据",
                    delegate()
                    {
                        if (_ghAccountPopover != null) _ghAccountPopover.IsOpen = false;
                        ApplyGhCliAccountSelection(login, true);
                    }));
            }

            if (hasEnvOverride && !string.IsNullOrEmpty(envVarName))
            {
                Border sep = new Border { Height = 1, Margin = new Thickness(6, 5, 6, 5) };
                sep.SetResourceReference(Border.BackgroundProperty, "BorderCard");
                menuSp.Children.Add(sep);
                string capturedEnv = envVarName;
                menuSp.Children.Add(MakePopoverMenuItemButton(
                    "使用环境变量 $" + capturedEnv,
                    "读取当前进程环境变量中的令牌",
                    delegate()
                    {
                        if (_ghAccountPopover != null) _ghAccountPopover.IsOpen = false;
                        ApplyGhCliAccountSelection(null, false);
                    }));
            }

            _ghAccountPopover = ModernControlsFactory.CreateModernPopover(_ghReadCliBtn, menuSp);
            _ghAccountPopover.IsOpen = true;
        }

        private void ApplyGhCliAccountSelection(string username, bool ignoreEnvOverride)
        {
            SetIdentityBadgeState("读取中…", "accent", "正在读取 gh CLI 令牌");
            Task.Run(delegate()
            {
                string tok;
                bool ok = SyncService.TryReadGitHubCliTokenForUser(username, ignoreEnvOverride, out tok);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    if (!ok || string.IsNullOrWhiteSpace(tok))
                    {
                        SetIdentityBadgeState("读取失败", "warning", "无法从 gh CLI 提取有效令牌");
                        NotifyUser("读取 gh 凭据失败", "请检查 gh auth status，或改用网页申请 PAT。", "warning");
                        return;
                    }
                    _suppressUiEvents = true;
                    try
                    {
                        _gistTokenField.Value = tok;
                        UpdateLocalTokenPreviewState(tok);
                    }
                    finally { _suppressUiEvents = false; }
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
                SetIdentityBadgeState("未配置", "neutral", "尚未配置 GitHub 访问令牌");
                _ghScopeFixBox.Visibility = Visibility.Collapsed;
                _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
                RefreshDeviceListUi();
                return;
            }

            int reqId = ++_probeRequestSeq;
            SetIdentityBadgeState("校验中…", "accent", "正在验证 GitHub 账号与 gist 权限");

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

            if (!probe.TokenValid)
            {
                SetIdentityBadgeState("认证失败", "danger", probe.ErrorMessage ?? "令牌无效或网络不可达");
                _ghScopeFixBox.Visibility = Visibility.Collapsed;
                _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
                if (userInitiated) NotifyUser("GitHub 校验未通过", probe.ErrorMessage ?? "请检查令牌或网络。", "error");
                return;
            }

            if (!probe.HasGistScope)
            {
                SetIdentityBadgeState("缺少 gist 权限", "warning", "账号 @" + probe.Login + " 缺少 gist 权限");
                _ghScopeFixText.Text = string.IsNullOrEmpty(probe.ErrorMessage)
                    ? ("已识别 @" + probe.Login + "，但令牌缺少 gist 权限，请执行 gh auth refresh -h github.com -s gist。")
                    : probe.ErrorMessage;
                _ghScopeFixBox.Visibility = Visibility.Visible;
                _pickGistCandidateBtn.Visibility = Visibility.Collapsed;
                if (userInitiated) NotifyUser("缺少 gist 权限", "点击「复制补权命令」后在终端运行即可。", "warning");
                return;
            }

            _ghScopeFixBox.Visibility = Visibility.Collapsed;
            string fp = SyncService.MaskTokenFingerprint(_gistTokenField.Value.Trim());
            SetIdentityBadgeState("已认证 @" + probe.Login, "success",
                "GitHub @" + probe.Login + " · gist 权限就绪" + (string.IsNullOrEmpty(fp) ? "" : (" · " + fp)));

            int privateCandCount = 0;
            if (probe.Candidates != null)
            {
                for (int i = 0; i < probe.Candidates.Count; i++)
                    if (!probe.Candidates[i].IsPublic) privateCandCount++;
            }
            _pickGistCandidateBtn.Visibility = privateCandCount > 1 ? Visibility.Visible : Visibility.Collapsed;

            string currentBoxGist = SyncService.NormalizeGistId(_gistIdBox.Text);
            if (string.IsNullOrEmpty(currentBoxGist) && probe.BestCandidate != null)
            {
                _suppressUiEvents = true;
                _gistIdBox.Text = probe.BestCandidate.GistId;
                _suppressUiEvents = false;
                RefreshVpsCommandPreview();
                ShowInlineBanner("已在 @" + probe.Login + " 名下找到私有同步库 (" + ShortId(probe.BestCandidate.GistId) + ")，点击「保存并同步」即可合并。", "success");
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
                ShowInlineBanner(
                    !string.IsNullOrEmpty(replacement)
                        ? ("原 Gist 不属于 @" + probe.Login + "，已切换为该账号名下同步库 (" + ShortId(replacement) + ")。")
                        : ("原 Gist 不属于 @" + probe.Login + "，已清空；点击「保存并同步」将新建私有 Gist。"),
                    "warning");
            }
            else if (userInitiated)
            {
                NotifyUser("身份校验通过：@" + probe.Login,
                    probe.BestCandidate != null ? ("已关联私有库 " + ShortId(probe.BestCandidate.GistId)) : "保存同步时将自动新建私有库",
                    "success");
            }

            RefreshDeviceListUi();
        }

        private void OnPickGistCandidateClick(object sender, RoutedEventArgs e)
        {
            if (_lastProbeResult == null || _lastProbeResult.Candidates == null || _lastProbeResult.Candidates.Count == 0)
                return;

            StackPanel menuSp = new StackPanel { MinWidth = 280, MaxWidth = 380 };
            for (int i = 0; i < _lastProbeResult.Candidates.Count; i++)
            {
                GitHubGistCandidate cand = _lastProbeResult.Candidates[i];
                if (cand.IsPublic) continue;
                string gid = cand.GistId;
                string title = "Gist " + ShortId(gid) + " · " + cand.NodeIds.Count.ToString(CultureInfo.InvariantCulture) + " 台设备";
                string sub = cand.NodeIds.Count > 0 ? string.Join(", ", cand.NodeIds.ToArray()) : "空同步库";
                menuSp.Children.Add(MakePopoverMenuItemButton(title, sub, delegate()
                {
                    if (_gistCandidatesPopover != null) _gistCandidatesPopover.IsOpen = false;
                    _suppressUiEvents = true;
                    _gistIdBox.Text = gid;
                    _suppressUiEvents = false;
                    RefreshDeviceListUi();
                    RefreshVpsCommandPreview();
                }));
            }
            _gistCandidatesPopover = ModernControlsFactory.CreateModernPopover(_pickGistCandidateBtn, menuSp);
            _gistCandidatesPopover.IsOpen = true;
        }

        private void RefreshDeviceListUi()
        {
            if (_devicesListPanel == null) return;
            _devicesListPanel.Children.Clear();

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

            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            double localUsd = 0.0;
            if (scan != null && scan.Records != null)
            {
                for (int i = 0; i < scan.Records.Count; i++)
                    if (scan.Records[i] != null) localUsd += scan.Records[i].CostUsd;
            }

            List<UIElement> rows = new List<UIElement>();
            rows.Add(MakeCompactDeviceRow(localName + " (本机)", localId, "实时监听 · " + FormatSimpleMoney(localUsd, _liveCfg.PreferCny, usdToCny), true, null));

            HashSet<string> rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            rendered.Add(localId);

            List<NodeSnapshot> cachedRemotes = SyncService.LoadCachedRemoteNodes(localId);
            for (int r = 0; r < cachedRemotes.Count; r++)
            {
                NodeSnapshot rn = cachedRemotes[r];
                if (rn == null || string.IsNullOrEmpty(rn.MachineId)) continue;
                rendered.Add(rn.MachineId);

                double rUsd = 0.0;
                if (rn.Records != null)
                {
                    for (int k = 0; k < rn.Records.Count; k++)
                        if (rn.Records[k] != null) rUsd += rn.Records[k].CostUsd;
                }
                string dispName = string.IsNullOrWhiteSpace(rn.MachineName) ? rn.MachineId : rn.MachineName;
                if (_liveCfg.NodeAliases != null && _liveCfg.NodeAliases.ContainsKey(rn.MachineId) && !string.IsNullOrWhiteSpace(_liveCfg.NodeAliases[rn.MachineId]))
                    dispName = _liveCfg.NodeAliases[rn.MachineId];

                string syncNote = string.IsNullOrWhiteSpace(rn.SyncedAt) ? "已缓存" : rn.SyncedAt;
                string capturedMid = rn.MachineId;
                string capturedName = dispName;
                rows.Add(MakeCompactDeviceRow(
                    dispName,
                    rn.MachineId,
                    syncNote + " · " + FormatSimpleMoney(rUsd, _liveCfg.PreferCny, usdToCny),
                    false,
                    delegate()
                    {
                        SyncService.RemoveCachedNode(capturedMid);
                        RefreshDeviceListUi();
                        if (_callbacks.OnSynced != null) _callbacks.OnSynced(null);
                        NotifyUser("已移除远端节点缓存", capturedName + " (" + capturedMid + ")", "accent");
                    }));
            }

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
                            if (!string.IsNullOrEmpty(cloudNodeId) && rendered.Add(cloudNodeId))
                                rows.Add(MakeCompactDeviceRow(cloudNodeId, cloudNodeId, "云端已发现 · 同步后拉取", false, null));
                        }
                        break;
                    }
                }
            }

            for (int i = 0; i < rows.Count; i++)
            {
                Border b = rows[i] as Border;
                if (b != null && i == rows.Count - 1) b.BorderThickness = new Thickness(0);
                _devicesListPanel.Children.Add(rows[i]);
            }

            UpdateToggleLabel(
                _devicesToggleBtn,
                string.Format(CultureInfo.InvariantCulture, "已同步设备 ({0}) {1}", rendered.Count, _devicesExpanded ? "▴" : "▾"));
        }

        private Border MakeCompactDeviceRow(string title, string machineId, string metaLine, bool isLocal, Action onRemove)
        {
            Border row = new Border
            {
                Padding = new Thickness(2, 7, 2, 7),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            row.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel dock = new DockPanel();
            if (onRemove != null)
            {
                Button rmBtn = ModernControlsFactory.CreateIconGhostButton(UiIcons.Trash, "移除该设备的本地缓存快照");
                rmBtn.Width = 24;
                rmBtn.Height = 24;
                rmBtn.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(rmBtn, Dock.Right);
                rmBtn.Click += delegate(object s, RoutedEventArgs e) { onRemove(); };
                dock.Children.Add(rmBtn);
            }

            TextBlock metaTb = new TextBlock
            {
                Text = metaLine,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            metaTb.SetResourceReference(TextBlock.ForegroundProperty, isLocal ? "AccentText" : "FgSecondary");
            DockPanel.SetDock(metaTb, Dock.Right);
            dock.Children.Add(metaTb);

            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock nameTb = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            left.Children.Add(nameTb);

            TextBlock idTb = new TextBlock
            {
                Text = machineId,
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            idTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            left.Children.Add(idTb);

            dock.Children.Add(left);
            row.Child = dock;
            return row;
        }

        private void OnAutoFindOrCreateGistClick(object sender, RoutedEventArgs e)
        {
            if (_syncBusy) return;
            PullSyncFieldsIntoWorkingConfig();
            if (string.IsNullOrWhiteSpace(_workingCfg.GistToken))
            {
                NotifyUser("请先提供 GitHub 令牌", "点「读取 gh」或粘贴 PAT 令牌后再自动查找/新建。", "warning");
                return;
            }

            SetSyncBusyState(true);
            ShowInlineBanner("正在查找或新建私有同步库并双向同步…", "accent");
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
                        outcome.Message = (reused ? "已关联私有 Gist (" : "已新建私有 Gist (") + ShortId(resolvedGistId) + ") · " + outcome.Message;

                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        SetSyncBusyState(false);
                        _workingCfg.GistId = resolvedGistId;
                        _suppressUiEvents = true;
                        _gistIdBox.Text = resolvedGistId;
                        _suppressUiEvents = false;
                        RefreshDeviceListUi();
                        RefreshVpsCommandPreview();
                        ShowInlineBanner(outcome.Message, outcome.Success ? "success" : "warning");
                        NotifyUser(outcome.Success ? "私有 Gist 同步完成" : "同步提示", outcome.Message, outcome.Success ? "success" : "warning");
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
            RefreshDeviceListUi();
            RefreshVpsCommandPreview();
            ShowInlineBanner("已保存云同步配置。", "success");
            NotifyUser("设置已保存", "本机标识与云同步配置已写入磁盘。", "success");
            if (_callbacks.OnSynced != null) _callbacks.OnSynced(null);
        }

        private void ExecuteSaveAndSyncFlow()
        {
            if (_syncBusy) return;
            PullSyncFieldsIntoWorkingConfig();

            if (!_isWebDav && !string.IsNullOrWhiteSpace(_workingCfg.GistToken) && string.IsNullOrWhiteSpace(_workingCfg.GistId))
            {
                OnAutoFindOrCreateGistClick(this, new RoutedEventArgs());
                return;
            }

            CommitWorkingSyncToLiveConfig();
            RefreshDeviceListUi();
            RefreshVpsCommandPreview();

            if (!_liveCfg.IsConfigured)
            {
                NotifyUser("云同步凭据尚未完整",
                    _isWebDav ? "请填写完整的 WebDAV 地址与账号密码。" : "请先点击「读取 gh」或填写 GitHub 访问令牌。",
                    "warning");
                return;
            }

            SetSyncBusyState(true);
            ShowInlineBanner("正在双向同步多设备账单…", "accent");
            ScanResult scan = _getLatestScan != null ? _getLatestScan() : null;
            NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(scan, _liveCfg);
            SyncConfig cfgSnapshot = _liveCfg.Clone();

            Task.Run(delegate()
            {
                SyncOutcome res = SyncService.SyncNow(cfgSnapshot, localSnap, true);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    SetSyncBusyState(false);
                    RefreshDeviceListUi();
                    ShowInlineBanner(res.Message, res.Success ? "success" : "danger");
                    NotifyUser(res.Success ? "云同步成功" : "云同步异常", res.Message, res.Success ? "success" : "error");
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
                SyncService.MigrateLocalMachineId(oldId, newId);

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

            if (_vpsOAuthHint != null)
                _vpsOAuthHint.Visibility = (!_isWebDav && previewCfg.GistToken.StartsWith("gho_", StringComparison.Ordinal))
                    ? Visibility.Visible : Visibility.Collapsed;

            if (!previewCfg.IsConfigured)
            {
                _vpsCmdPreviewBox.Text = "# 请先在上方填好同步凭据并保存同步一次，此处将自动生成 Linux VPS 一键部署命令。";
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
                NotifyUser("尚未完成云同步配置", "请先保存并同步一次，再复制 VPS 部署命令。", "warning");
                return;
            }
            if (TrySetClipboardSafe(_vpsCmdPreviewBox.Text))
                NotifyUser("VPS 部署命令已复制", "SSH 登录服务器后粘贴运行即可完成探针部署。", "success");
            else
                NotifyUser("复制失败", "剪贴板被占用，请手动复制。", "warning");
        }

        private void OnExportVpsAgentPyClick(object sender, RoutedEventArgs e)
        {
            string py = SyncService.GetEmbeddedVpsAgentScript();
            if (string.IsNullOrEmpty(py))
            {
                NotifyUser("未找到探针脚本资源", "当前构建未内嵌 toklens_agent.py。", "warning");
                return;
            }
            try
            {
                using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
                {
                    sfd.Title = "导出 Linux VPS 采集探针脚本";
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

        // -------------------- Helpers --------------------

        private void UpdateProviderModeVisuals()
        {
            ModernControlsFactory.UpdateSegmentedGroup(_providerBtns, _isWebDav ? 1 : 0);
            if (_gistSectionPanel != null && _webdavSectionPanel != null)
            {
                _gistSectionPanel.Visibility = _isWebDav ? Visibility.Collapsed : Visibility.Visible;
                _webdavSectionPanel.Visibility = _isWebDav ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_ghAuthStatusPill != null)
                _ghAuthStatusPill.Visibility = _isWebDav ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateLocalTokenPreviewState(string rawToken)
        {
            string tok = (rawToken ?? "").Trim();
            if (string.IsNullOrEmpty(tok))
                SetIdentityBadgeState("未配置", "neutral", "尚未配置 GitHub 访问令牌");
            else if (_lastProbeResult == null)
                SetIdentityBadgeState("待校验", "neutral", "已保存令牌，打开此页或点击「校验」将自动验证账号与 gist 权限");
        }

        private void SetIdentityBadgeState(string pillText, string variant, string tooltip)
        {
            if (_ghAuthStatusLabel != null) _ghAuthStatusLabel.Text = pillText ?? "";
            if (_ghAuthStatusPill != null) _ghAuthStatusPill.ToolTip = tooltip;

            string bgKey = "BgCard", bdKey = "BorderCard", fgKey = "FgSecondary";
            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            { bgKey = "SemanticSuccessBg"; bdKey = "SemanticSuccessBorder"; fgKey = "SemanticSuccess"; }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            { bgKey = "SemanticWarningBg"; bdKey = "SemanticWarningBorder"; fgKey = "SemanticWarning"; }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase))
            { bgKey = "SemanticDangerBg"; bdKey = "SemanticDangerBorder"; fgKey = "SemanticDanger"; }
            else if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            { bgKey = "AccentSubtleBg"; bdKey = "AccentSubtleBorder"; fgKey = "AccentText"; }

            if (_ghAuthStatusPill != null)
            {
                _ghAuthStatusPill.SetResourceReference(Border.BackgroundProperty, bgKey);
                _ghAuthStatusPill.SetResourceReference(Border.BorderBrushProperty, bdKey);
            }
            if (_ghAuthStatusLabel != null) _ghAuthStatusLabel.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            if (_ghAuthStatusDot != null) _ghAuthStatusDot.SetResourceReference(Shapes.Shape.FillProperty, fgKey);
        }

        private void ShowInlineBanner(string message, string variant)
        {
            if (_inlineBannerBorder == null || _inlineBannerText == null) return;
            _inlineBannerText.Text = message ?? "";

            string bgKey = "AccentSubtleBg", bdKey = "AccentSubtleBorder", fgKey = "AccentText", iconPath = UiIcons.Activity;
            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            { bgKey = "SemanticSuccessBg"; bdKey = "SemanticSuccessBorder"; fgKey = "SemanticSuccess"; iconPath = UiIcons.Check; }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            { bgKey = "SemanticWarningBg"; bdKey = "SemanticWarningBorder"; fgKey = "SemanticWarning"; iconPath = UiIcons.Alert; }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase) || string.Equals(variant, "error", StringComparison.OrdinalIgnoreCase))
            { bgKey = "SemanticDangerBg"; bdKey = "SemanticDangerBorder"; fgKey = "SemanticDanger"; iconPath = UiIcons.Alert; }

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
                _callbacks.ShowToast(title, detail, variant);
            else
                ShowInlineBanner(string.IsNullOrEmpty(detail) ? title : (title + " — " + detail), variant);
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

        private static void UpdateToggleLabel(Button btn, string text)
        {
            if (btn == null) return;
            StackPanel sp = btn.Content as StackPanel;
            if (sp != null)
            {
                for (int i = 0; i < sp.Children.Count; i++)
                {
                    TextBlock tb = sp.Children[i] as TextBlock;
                    if (tb != null) { tb.Text = text; return; }
                }
            }
        }

        private static string ShortId(string gistId)
        {
            if (string.IsNullOrEmpty(gistId)) return "";
            return gistId.Length <= 10 ? gistId : (gistId.Substring(0, 8) + "…");
        }

        private static string FormatSimpleMoney(double usd, bool preferCny, double rate)
        {
            double val = preferCny ? (usd * rate) : usd;
            return (preferCny ? "¥" : "$") + val.ToString("N2", CultureInfo.InvariantCulture);
        }

        private static bool TrySetClipboardSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try { Clipboard.SetText(text); return true; }
            catch
            {
                try { Clipboard.SetDataObject(text, false); return true; }
                catch { return false; }
            }
        }

        private static string TryGetClipboardSafe()
        {
            try { return Clipboard.ContainsText() ? (Clipboard.GetText() ?? "") : ""; }
            catch { return ""; }
        }

        private static void OpenExternalUrlSafe(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private static TextBlock MakeFieldLabel(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 5)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return tb;
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
            btn.Click += delegate(object sender, RoutedEventArgs e) { if (onClick != null) onClick(); };
            return btn;
        }
    }
}
