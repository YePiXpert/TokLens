using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace TokLens
{
    public sealed class SyncWindow : Window
    {
        private sealed class SecretField
        {
            public Border Root;
            private PasswordBox _pwdBox;
            private TextBox _plainBox;
            private Button _toggleBtn;
            private bool _revealed = false;
            private bool _syncing = false;

            public SecretField(SyncWindow owner)
            {
                Root = new Border();
                Root.CornerRadius = new CornerRadius(6);
                Root.BorderThickness = new Thickness(1);
                Root.Padding = new Thickness(8, 4, 6, 4);
                Root.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                Root.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

                DockPanel dock = new DockPanel();

                _toggleBtn = owner.MakeSubtleInlineBtn("显示");
                _toggleBtn.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(_toggleBtn, Dock.Right);
                _toggleBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    _revealed = !_revealed;
                    _toggleBtn.Content = _revealed ? "隐藏" : "显示";
                    if (_revealed)
                    {
                        _syncing = true;
                        _plainBox.Text = _pwdBox.Password;
                        _syncing = false;
                        _pwdBox.Visibility = Visibility.Collapsed;
                        _plainBox.Visibility = Visibility.Visible;
                        _plainBox.Focus();
                        _plainBox.CaretIndex = _plainBox.Text.Length;
                    }
                    else
                    {
                        _syncing = true;
                        _pwdBox.Password = _plainBox.Text;
                        _syncing = false;
                        _plainBox.Visibility = Visibility.Collapsed;
                        _pwdBox.Visibility = Visibility.Visible;
                        _pwdBox.Focus();
                    }
                };
                dock.Children.Add(_toggleBtn);

                Grid host = new Grid();
                _pwdBox = new PasswordBox();
                _pwdBox.FontSize = 12.5;
                _pwdBox.BorderThickness = new Thickness(0);
                _pwdBox.Background = Brushes.Transparent;
                _pwdBox.Padding = new Thickness(0, 2, 0, 2);
                _pwdBox.VerticalContentAlignment = VerticalAlignment.Center;
                _pwdBox.SetResourceReference(PasswordBox.ForegroundProperty, "FgPrimary");
                _pwdBox.PasswordChanged += delegate(object s, RoutedEventArgs e)
                {
                    if (_syncing) return;
                    if (_revealed)
                    {
                        _syncing = true;
                        _plainBox.Text = _pwdBox.Password;
                        _syncing = false;
                    }
                };

                _plainBox = new TextBox();
                _plainBox.FontSize = 12.5;
                _plainBox.BorderThickness = new Thickness(0);
                _plainBox.Background = Brushes.Transparent;
                _plainBox.Padding = new Thickness(0, 2, 0, 2);
                _plainBox.VerticalContentAlignment = VerticalAlignment.Center;
                _plainBox.Visibility = Visibility.Collapsed;
                _plainBox.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
                _plainBox.SetResourceReference(TextBox.CaretBrushProperty, "AccentBlue");
                _plainBox.TextChanged += delegate(object s, TextChangedEventArgs e)
                {
                    if (_syncing) return;
                    if (_revealed)
                    {
                        _syncing = true;
                        _pwdBox.Password = _plainBox.Text;
                        _syncing = false;
                    }
                };

                host.Children.Add(_pwdBox);
                host.Children.Add(_plainBox);
                dock.Children.Add(host);
                Root.Child = dock;
            }

            public string Value
            {
                get { return _revealed ? (_plainBox.Text ?? "") : (_pwdBox.Password ?? ""); }
                set
                {
                    _syncing = true;
                    string v = value ?? "";
                    _pwdBox.Password = v;
                    _plainBox.Text = v;
                    _syncing = false;
                }
            }
        }

        private readonly SyncConfig _cfg;
        private readonly ScanResult _localScan;
        private readonly Action<SyncOutcome> _onSynced;

        private Button[] _navTabBtns;
        private UIElement _pageCloud;
        private UIElement _pageDevices;
        private int _activePage = 0;

        private Button[] _providerBtns;
        private bool _isWebDav = false;

        private TextBox _nameBox;
        private TextBox _idBox;
        private CheckBox _autoSyncChk;
        private CheckBox _wslChk;

        private StackPanel _gistPanel;
        private StackPanel _webdavPanel;

        private SecretField _gistTokenField;
        private TextBox _gistIdBox;
        private TextBox _davUrlBox;
        private TextBox _davUserBox;
        private SecretField _davPassField;

        private TextBox _vpsNameBox;
        private TextBox _vpsIntervalBox;
        private TextBox _vpsCmdPreview;
        private TextBlock _statusText;
        private Button _syncNowBtn;
        private Button _createGistBtn;

        public SyncWindow(Window owner, SyncConfig cfg, ScanResult localScan, Action<SyncOutcome> onSynced)
            : this(owner, cfg, localScan, onSynced, 0)
        {
        }

        public SyncWindow(Window owner, SyncConfig cfg, ScanResult localScan, Action<SyncOutcome> onSynced, int initialTab)
        {
            Owner = owner;
            _cfg = cfg;
            _localScan = localScan;
            _onSynced = onSynced;
            _activePage = initialTab == 1 ? 1 : 0;

            if (owner != null)
            {
                Resources = owner.Resources;
                Icon = owner.Icon;
            }

            Title = "多端同步与设备接入 — 知微 · TokLens";
            Width = 620;
            Height = 520;
            MinWidth = 560;
            MinHeight = 480;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            SetResourceReference(Window.BackgroundProperty, "BgWindow");
            SetResourceReference(Window.ForegroundProperty, "FgPrimary");

            Content = BuildUi();
            LoadFromConfig();
            SwitchNavTab(_activePage);
        }

        private UIElement BuildUi()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(18, 16, 18, 16);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0: Top Segmented Navigation
            DockPanel topBar = new DockPanel();
            topBar.Margin = new Thickness(0, 0, 0, 14);

            Border navBox = CreateSegBox();
            DockPanel.SetDock(navBox, Dock.Left);
            StackPanel navSp = (StackPanel)navBox.Child;
            string[] tabs = new string[] { "本机与云端配置", "接入其他电脑 / Linux VPS" };
            _navTabBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button b = CreateSegBtn(tabs[i]);
                b.Padding = new Thickness(14, 5, 14, 5);
                b.Click += delegate(object s, RoutedEventArgs e) { SwitchNavTab(idx); };
                _navTabBtns[i] = b;
                navSp.Children.Add(b);
            }
            topBar.Children.Add(navBox);

            StackPanel quickPair = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(quickPair, Dock.Right);
            Button cpBtn = MakeBtn("复制配对码", false);
            cpBtn.FontSize = 11.5;
            cpBtn.Padding = new Thickness(9, 4, 9, 4);
            cpBtn.Margin = new Thickness(0, 0, 6, 0);
            cpBtn.ToolTip = "复制加密配对码，供家里电脑或 Linux VPS 一键导入云同步配置";
            cpBtn.Click += OnCopyPairCodeClick;
            Button psBtn = MakeBtn("粘贴配对码", false);
            psBtn.FontSize = 11.5;
            psBtn.Padding = new Thickness(9, 4, 9, 4);
            psBtn.ToolTip = "从剪贴板导入另一台电脑复制的配对码（不会覆盖本机名称）";
            psBtn.Click += OnPastePairCodeClick;
            quickPair.Children.Add(cpBtn);
            quickPair.Children.Add(psBtn);
            topBar.Children.Add(quickPair);

            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // Row 1: Tab Pages
            Grid pageHost = new Grid();
            _pageCloud = BuildCloudConfigPage();
            _pageDevices = BuildDeviceOnboardPage();
            pageHost.Children.Add(_pageCloud);
            pageHost.Children.Add(_pageDevices);
            Grid.SetRow(pageHost, 1);
            root.Children.Add(pageHost);

            // Row 2: Footer Bar
            Border footer = new Border();
            footer.BorderThickness = new Thickness(0, 1, 0, 0);
            footer.Padding = new Thickness(0, 12, 0, 0);
            footer.Margin = new Thickness(0, 10, 0, 0);
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            DockPanel botDock = new DockPanel();
            StackPanel btnRight = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(btnRight, Dock.Right);

            Button saveOnlyBtn = MakeBtn("仅保存设置", false);
            saveOnlyBtn.Margin = new Thickness(0, 0, 8, 0);
            saveOnlyBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                PullToConfig();
                SyncService.SaveConfig(_cfg);
                SetStatus("已保存本机与同步设置", false);
                if (_onSynced != null) _onSynced(null);
            };
            btnRight.Children.Add(saveOnlyBtn);

            _syncNowBtn = MakeBtn("保存并立即同步", true);
            _syncNowBtn.Margin = new Thickness(0, 0, 8, 0);
            _syncNowBtn.Click += OnSaveAndSyncClick;
            btnRight.Children.Add(_syncNowBtn);

            Button closeBtn = MakeBtn("关闭", false);
            closeBtn.Click += delegate(object s, RoutedEventArgs e) { Close(); };
            btnRight.Children.Add(closeBtn);
            botDock.Children.Add(btnRight);

            _statusText = new TextBlock();
            _statusText.Text = _cfg.IsConfigured ? "同步凭据已就绪，后台自动同步已启用" : "配置 GitHub Gist 或粘贴配对码后即可开启自动多端同步";
            _statusText.FontSize = 12;
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _statusText.Margin = new Thickness(0, 0, 12, 0);
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            botDock.Children.Add(_statusText);

            footer.Child = botDock;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            return root;
        }

        private UIElement BuildCloudConfigPage()
        {
            StackPanel sp = new StackPanel();

            // Card 1: Local Identity
            Border card1 = MakeCard();
            StackPanel c1 = new StackPanel();
            c1.Children.Add(MakeSectionTitle("本机标识与自动化"));

            Grid g1 = new Grid();
            g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            StackPanel pName = new StackPanel();
            pName.Children.Add(MakeLabel("显示名称（如：办公电脑 / 家里电脑）"));
            _nameBox = MakeThemedTextBox();
            pName.Children.Add(_nameBox);
            Grid.SetColumn(pName, 0);
            g1.Children.Add(pName);

            StackPanel pId = new StackPanel();
            pId.Children.Add(MakeLabel("设备唯一 ID（不同电脑保持不同）"));
            _idBox = MakeThemedTextBox();
            pId.Children.Add(_idBox);
            Grid.SetColumn(pId, 2);
            g1.Children.Add(pId);
            c1.Children.Add(g1);

            StackPanel chkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _autoSyncChk = new CheckBox { Content = "后台全自动同步（每 5 分钟及用量变化后静默同步）", Margin = new Thickness(0, 0, 20, 0), FontSize = 12 };
            _autoSyncChk.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            _wslChk = new CheckBox { Content = "扫描本机 WSL2", FontSize = 12 };
            _wslChk.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            chkRow.Children.Add(_autoSyncChk);
            chkRow.Children.Add(_wslChk);
            c1.Children.Add(chkRow);

            card1.Child = c1;
            sp.Children.Add(card1);

            // Card 2: Cloud Provider
            Border card2 = MakeCard();
            card2.Margin = new Thickness(0);
            StackPanel c2 = new StackPanel();

            DockPanel hdr2 = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            TextBlock title2 = MakeSectionTitle("云端同步通道（仅上传按日脱敏统计数字）");
            title2.Margin = new Thickness(0);
            title2.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(title2, Dock.Left);
            hdr2.Children.Add(title2);

            Border provBox = CreateSegBox();
            DockPanel.SetDock(provBox, Dock.Right);
            provBox.HorizontalAlignment = HorizontalAlignment.Right;
            StackPanel provSp = (StackPanel)provBox.Child;
            string[] pNames = new string[] { "GitHub 私有 Gist", "WebDAV 网盘" };
            _providerBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                Button b = CreateSegBtn(pNames[i]);
                b.Padding = new Thickness(10, 3, 10, 3);
                b.FontSize = 11.5;
                b.Click += delegate(object s, RoutedEventArgs e)
                {
                    _isWebDav = (idx == 1);
                    UpdateProviderVisuals();
                };
                _providerBtns[i] = b;
                provSp.Children.Add(b);
            }
            hdr2.Children.Add(provBox);
            c2.Children.Add(hdr2);

            // Gist Form
            _gistPanel = new StackPanel();
            DockPanel tokHdr = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            StackPanel tokActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tokActions, Dock.Right);

            Button ghAutoBtn = MakeSubtleInlineBtn("自动读取 gh 登录");
            ghAutoBtn.ToolTip = "若本机已安装并登录 GitHub CLI (gh)，一键读取含 gist 权限的凭据，免手动复制";
            ghAutoBtn.Margin = new Thickness(0, 0, 6, 0);
            ghAutoBtn.Click += OnReadGhCliTokenClick;
            tokActions.Children.Add(ghAutoBtn);

            Button webPatBtn = MakeSubtleInlineBtn("网页申请 Token");
            webPatBtn.ToolTip = "在浏览器打开 GitHub Token 创建页（已自动预填 gist 权限）";
            webPatBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("https://github.com/settings/tokens/new?description=TokLens-Sync&scopes=gist");
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch { }
            };
            tokActions.Children.Add(webPatBtn);
            tokHdr.Children.Add(tokActions);

            TextBlock tokLbl = MakeLabel("GitHub Personal Access Token（仅需勾选 gist 权限）");
            tokLbl.Margin = new Thickness(0);
            tokLbl.VerticalAlignment = VerticalAlignment.Center;
            tokHdr.Children.Add(tokLbl);
            _gistPanel.Children.Add(tokHdr);

            _gistTokenField = new SecretField(this);
            _gistPanel.Children.Add(_gistTokenField.Root);

            Grid gistRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            gistRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gistRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            gistRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel gidLeft = new StackPanel();
            gidLeft.Children.Add(MakeLabel("私有 Gist ID（首次或换新电脑可留空，自动查找已有或新建）"));
            _gistIdBox = MakeThemedTextBox();
            gidLeft.Children.Add(_gistIdBox);
            Grid.SetColumn(gidLeft, 0);
            gistRow.Children.Add(gidLeft);

            _createGistBtn = MakeBtn("自动查找 / 创建 Gist", false);
            _createGistBtn.VerticalAlignment = VerticalAlignment.Bottom;
            _createGistBtn.Padding = new Thickness(12, 6, 12, 6);
            _createGistBtn.Click += OnCreatePrivateGistClick;
            Grid.SetColumn(_createGistBtn, 2);
            gistRow.Children.Add(_createGistBtn);

            _gistPanel.Children.Add(gistRow);
            c2.Children.Add(_gistPanel);

            // WebDAV Form
            _webdavPanel = new StackPanel { Visibility = Visibility.Collapsed };
            _webdavPanel.Children.Add(MakeLabel("WebDAV 文件夹地址（如 https://dav.jianguoyun.com/dav/TokLens/）"));
            _davUrlBox = MakeThemedTextBox();
            _webdavPanel.Children.Add(_davUrlBox);

            Grid davGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            davGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            davGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            davGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel du = new StackPanel();
            du.Children.Add(MakeLabel("WebDAV 账号"));
            _davUserBox = MakeThemedTextBox();
            du.Children.Add(_davUserBox);
            Grid.SetColumn(du, 0);
            davGrid.Children.Add(du);

            StackPanel dp = new StackPanel();
            dp.Children.Add(MakeLabel("WebDAV 应用密码"));
            _davPassField = new SecretField(this);
            dp.Children.Add(_davPassField.Root);
            Grid.SetColumn(dp, 2);
            davGrid.Children.Add(dp);
            _webdavPanel.Children.Add(davGrid);
            c2.Children.Add(_webdavPanel);

            Border cloudArchiveNote = MakeArchiveAssuranceNote(
                "防清理保护已开启：电脑或 VPS 定期清理日志甚至重装系统，已同步过的历史账单均永久保留不归零。");
            cloudArchiveNote.Margin = new Thickness(0, 10, 0, 0);
            c2.Children.Add(cloudArchiveNote);

            card2.Child = c2;
            sp.Children.Add(card2);

            return sp;
        }

        private UIElement BuildDeviceOnboardPage()
        {
            StackPanel sp = new StackPanel();

            // Card 1: Second Windows PC
            Border pcCard = MakeCard();
            StackPanel pcSp = new StackPanel();
            pcSp.Children.Add(MakeSectionTitle("接入第二台 Windows 电脑（家里电脑 / 办公电脑）"));
            TextBlock pcDesc = new TextBlock();
            pcDesc.Text = "• 方式一（配对码秒导入）：本机配好后点右上角「复制配对码」→ 家里电脑点「粘贴配对码」，改名为「家里电脑」后保存同步。\n• 方式二（免配对码自动找回）：家里电脑直接填入同一 GitHub Token（或点「自动读取 gh 登录」），Gist ID 留空直接点「保存并立即同步」，自动关联已有 Gist 并双向合并。";
            pcDesc.FontSize = 11.8;
            pcDesc.LineHeight = 18.5;
            pcDesc.TextWrapping = TextWrapping.Wrap;
            pcDesc.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            pcSp.Children.Add(pcDesc);
            pcCard.Child = pcSp;
            sp.Children.Add(pcCard);

            // Card 2: Linux VPS Probe
            Border vpsCard = MakeCard();
            vpsCard.Margin = new Thickness(0);
            StackPanel vpsSp = new StackPanel();
            vpsSp.Children.Add(MakeSectionTitle("接入 Linux VPS（零依赖 Python 3 探针 + Crontab 自动上报）"));

            Grid vpsGrid = new Grid();
            vpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            vpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            vpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            vpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            vpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel vn = new StackPanel();
            vn.Children.Add(MakeLabel("VPS 显示名称（如：香港主力 VPS）"));
            _vpsNameBox = MakeThemedTextBox();
            _vpsNameBox.Text = "主力 VPS-1";
            _vpsNameBox.TextChanged += delegate(object s, TextChangedEventArgs e) { RefreshVpsCommandPreview(); };
            vn.Children.Add(_vpsNameBox);
            Grid.SetColumn(vn, 0);
            vpsGrid.Children.Add(vn);

            StackPanel vi = new StackPanel();
            vi.Children.Add(MakeLabel("间隔(分钟)"));
            _vpsIntervalBox = MakeThemedTextBox();
            _vpsIntervalBox.Text = "30";
            _vpsIntervalBox.TextChanged += delegate(object s, TextChangedEventArgs e) { RefreshVpsCommandPreview(); };
            vi.Children.Add(_vpsIntervalBox);
            Grid.SetColumn(vi, 2);
            vpsGrid.Children.Add(vi);

            Button genCopyBtn = MakeBtn("复制一键部署命令", true);
            genCopyBtn.VerticalAlignment = VerticalAlignment.Bottom;
            genCopyBtn.Padding = new Thickness(13, 6, 13, 6);
            genCopyBtn.Click += OnCopyVpsCommandClick;
            Grid.SetColumn(genCopyBtn, 4);
            vpsGrid.Children.Add(genCopyBtn);
            vpsSp.Children.Add(vpsGrid);

            _vpsCmdPreview = MakeThemedTextBox();
            _vpsCmdPreview.IsReadOnly = true;
            _vpsCmdPreview.TextWrapping = TextWrapping.Wrap;
            _vpsCmdPreview.Height = 64;
            _vpsCmdPreview.VerticalContentAlignment = VerticalAlignment.Top;
            _vpsCmdPreview.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            _vpsCmdPreview.FontSize = 11.5;
            _vpsCmdPreview.Margin = new Thickness(0, 9, 0, 7);
            vpsSp.Children.Add(_vpsCmdPreview);

            DockPanel botLine = new DockPanel();
            Button exportPyBtn = MakeBtn("单独导出 toklens_agent.py 脚本", false);
            exportPyBtn.FontSize = 11.5;
            exportPyBtn.Padding = new Thickness(10, 4, 10, 4);
            exportPyBtn.HorizontalAlignment = HorizontalAlignment.Left;
            exportPyBtn.Click += OnExportAgentPyClick;
            botLine.Children.Add(exportPyBtn);

            TextBlock tip = new TextBlock();
            tip.Text = "SSH 登录 VPS 后粘贴回车即可自动采集并定时同步";
            tip.FontSize = 11.5;
            tip.VerticalAlignment = VerticalAlignment.Center;
            tip.HorizontalAlignment = HorizontalAlignment.Right;
            tip.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            DockPanel.SetDock(tip, Dock.Right);
            botLine.Children.Insert(0, tip);
            vpsSp.Children.Add(botLine);

            Border vpsArchiveNote = MakeArchiveAssuranceNote(
                "防清理保护已开启：探针内置 ~/.toklens/ledger.db 归档与云端高水位合并，VPS 删日志或重装均不丢账单。");
            vpsArchiveNote.Margin = new Thickness(0, 8, 0, 0);
            vpsSp.Children.Add(vpsArchiveNote);

            vpsCard.Child = vpsSp;
            sp.Children.Add(vpsCard);

            return sp;
        }

        private void SwitchNavTab(int idx)
        {
            _activePage = idx;
            UpdateSegGroup(_navTabBtns, idx);
            _pageCloud.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            _pageDevices.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (idx == 1) RefreshVpsCommandPreview();
        }

        private void UpdateProviderVisuals()
        {
            UpdateSegGroup(_providerBtns, _isWebDav ? 1 : 0);
            if (_gistPanel != null && _webdavPanel != null)
            {
                _gistPanel.Visibility = _isWebDav ? Visibility.Collapsed : Visibility.Visible;
                _webdavPanel.Visibility = _isWebDav ? Visibility.Visible : Visibility.Collapsed;
            }
            RefreshVpsCommandPreview();
        }

        private void SetStatus(string msg, bool isError)
        {
            if (_statusText == null) return;
            _statusText.Text = msg;
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "AccentAmber" : "AccentBlue");
        }

        private void LoadFromConfig()
        {
            _nameBox.Text = _cfg.MachineName;
            _idBox.Text = _cfg.MachineId;
            _autoSyncChk.IsChecked = _cfg.AutoSync;
            _wslChk.IsChecked = _cfg.ScanLocalWsl;

            _isWebDav = string.Equals(_cfg.Provider, "webdav", StringComparison.OrdinalIgnoreCase);
            _gistTokenField.Value = _cfg.GistToken ?? "";
            _gistIdBox.Text = _cfg.GistId ?? "";
            _davUrlBox.Text = _cfg.WebDavUrl ?? "";
            _davUserBox.Text = _cfg.WebDavUser ?? "";
            _davPassField.Value = _cfg.WebDavPass ?? "";
            UpdateProviderVisuals();
            RefreshVpsCommandPreview();
        }

        private void PullToConfig()
        {
            _cfg.MachineName = string.IsNullOrWhiteSpace(_nameBox.Text) ? Environment.MachineName : _nameBox.Text.Trim();
            _cfg.MachineId = SyncService.SanitizeMachineId(string.IsNullOrWhiteSpace(_idBox.Text) ? ("win-" + Environment.MachineName) : _idBox.Text);
            _idBox.Text = _cfg.MachineId;
            _cfg.AutoSync = _autoSyncChk.IsChecked == true;
            _cfg.ScanLocalWsl = _wslChk.IsChecked == true;
            _cfg.Provider = _isWebDav ? "webdav" : "gist";
            _cfg.GistToken = _gistTokenField.Value.Trim();
            _cfg.GistId = _gistIdBox.Text.Trim();
            _cfg.WebDavUrl = _davUrlBox.Text.Trim();
            _cfg.WebDavUser = _davUserBox.Text.Trim();
            _cfg.WebDavPass = _davPassField.Value.Trim();
        }

        private void RefreshVpsCommandPreview()
        {
            if (_vpsCmdPreview == null) return;
            PullToConfig();
            if (!_cfg.IsConfigured)
            {
                _vpsCmdPreview.Text = "# 请先在「本机与云端配置」标签页填好 GitHub Gist（或自动创建）或 WebDAV 凭据，此处将自动生成一键接入命令";
                return;
            }
            int mins;
            if (!int.TryParse(_vpsIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mins))
                mins = 30;
            _vpsCmdPreview.Text = SyncService.BuildLinuxVpsOneLiner(_cfg, _vpsNameBox.Text.Trim(), mins);
        }

        private void OnCopyPairCodeClick(object sender, RoutedEventArgs e)
        {
            PullToConfig();
            if (!_cfg.IsConfigured)
            {
                MessageBox.Show(this, "请先填写完整的同步凭据（若尚无 Gist ID，填好 Token 后点击「自动创建私有 Gist」即可）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string code = SyncService.GeneratePairingCode(_cfg);
            Clipboard.SetText(code);
            SetStatus("配对码已复制到剪贴板，可在另一台电脑点击「粘贴配对码」导入", false);
        }

        private void OnReadGhCliTokenClick(object sender, RoutedEventArgs e)
        {
            string tok;
            if (SyncService.TryReadGitHubCliToken(out tok))
            {
                _gistTokenField.Value = tok;
                SetStatus("已自动读取本机 GitHub CLI (gh) 凭据，点击「保存并立即同步」即可自动关联或新建私有 Gist", false);
            }
            else
            {
                MessageBox.Show(
                    this,
                    "未检测到本机已登录的 GitHub CLI (gh)。\n\n你可以：\n1. 点击旁边的「网页申请 Token」花 20 秒创建一个勾选 gist 权限的 Token；\n2. 或在已配置好的另一台电脑点击「复制配对码」，在本机点击「粘贴配对码」。",
                    "读取 GitHub CLI 凭据",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void OnPastePairCodeClick(object sender, RoutedEventArgs e)
        {
            string clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            string err;
            PullToConfig();
            if (!SyncService.TryApplyPairingCode(_cfg, clip, out err))
            {
                MessageBox.Show(this, "导入失败：" + err + "\n请先在已配置的电脑上点击「复制配对码」。", "导入配对码", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadFromConfig();
            SwitchNavTab(0);
            _nameBox.Focus();
            _nameBox.SelectAll();
            SetStatus("已导入云同步凭据！请确认上方「显示名称」（如：家里电脑），再点「保存并立即同步」", false);
        }

        private void OnCreatePrivateGistClick(object sender, RoutedEventArgs e)
        {
            PullToConfig();
            if (string.IsNullOrWhiteSpace(_cfg.GistToken))
            {
                MessageBox.Show(this, "请先输入 GitHub Personal Access Token（或点击「自动读取 gh 登录」）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _createGistBtn.IsEnabled = false;
            _syncNowBtn.IsEnabled = false;
            SetStatus("正在检索账号下已有的 TokLens 私有同步库（若无则自动创建）...", false);

            NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(_localScan, _cfg);
            string token = _cfg.GistToken;

            Task.Run(delegate()
            {
                try
                {
                    bool reused;
                    string resolvedGistId = SyncService.FindOrCreatePrivateGist(token, localSnap, out reused);
                    _cfg.GistId = resolvedGistId;
                    SyncService.SaveConfig(_cfg);
                    SyncOutcome outc = SyncService.SyncNow(_cfg, localSnap, true);
                    if (outc.Success)
                    {
                        string prefix = reused ? "已自动找回已有私有 Gist (" : "已新建私有 Gist (";
                        outc.Message = prefix + resolvedGistId.Substring(0, Math.Min(8, resolvedGistId.Length)) + "...) · " + outc.Message;
                    }

                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        _gistIdBox.Text = resolvedGistId;
                        RefreshVpsCommandPreview();
                        SetStatus(outc.Message, !outc.Success);
                        _createGistBtn.IsEnabled = true;
                        _syncNowBtn.IsEnabled = true;
                        if (_onSynced != null) _onSynced(outc);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        _createGistBtn.IsEnabled = true;
                        _syncNowBtn.IsEnabled = true;
                        SetStatus("关联/创建失败：" + ex.Message, true);
                        MessageBox.Show(this, "关联或创建私有 Gist 失败：\n" + ex.Message, "同步错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
                }
            });
        }

        private void OnSaveAndSyncClick(object sender, RoutedEventArgs e)
        {
            PullToConfig();

            // 智能体验：若用户填好了 GitHub Token 但 Gist ID 留空（无论首次使用还是换了家里新电脑），直接自动查找已有 Gist 或新建并完成双向同步！
            if (!_isWebDav && !string.IsNullOrWhiteSpace(_cfg.GistToken) && string.IsNullOrWhiteSpace(_cfg.GistId))
            {
                OnCreatePrivateGistClick(sender, e);
                return;
            }

            SyncService.SaveConfig(_cfg);
            RefreshVpsCommandPreview();

            if (!_cfg.IsConfigured)
            {
                SwitchNavTab(0);
                MessageBox.Show(this, "尚未填写完整同步信息。如果使用 GitHub Gist，填好 Token（或点「自动读取 gh 登录」）后直接点「保存并立即同步」即可。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _syncNowBtn.IsEnabled = false;
            SetStatus("正在同步本机快照并拉取远端设备数据...", false);
            NodeSnapshot localSnap = SyncService.BuildLocalSnapshot(_localScan, _cfg);

            Task.Run(delegate()
            {
                SyncOutcome res = SyncService.SyncNow(_cfg, localSnap, true);
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _syncNowBtn.IsEnabled = true;
                    SetStatus(res.Message, !res.Success);
                    if (_onSynced != null) _onSynced(res);
                }));
            });
        }

        private void OnCopyVpsCommandClick(object sender, RoutedEventArgs e)
        {
            RefreshVpsCommandPreview();
            if (!_cfg.IsConfigured)
            {
                SwitchNavTab(0);
                MessageBox.Show(this, "请先完成云同步配置并同步一次（以将探针脚本写入私有同步库），再复制 VPS 一键命令。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SyncService.SaveConfig(_cfg);
            Clipboard.SetText(_vpsCmdPreview.Text);
            SetStatus("Linux VPS 一键部署命令已复制，粘贴到 SSH 终端回车即可", false);
        }

        private void OnExportAgentPyClick(object sender, RoutedEventArgs e)
        {
            string py = SyncService.GetEmbeddedVpsAgentScript();
            if (string.IsNullOrEmpty(py))
            {
                MessageBox.Show(this, "未找到内置探针脚本资源。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            using (WinForms.SaveFileDialog sfd = new WinForms.SaveFileDialog())
            {
                sfd.Title = "导出 Linux VPS 采集探针脚本";
                sfd.Filter = "Python 脚本 (*.py)|*.py";
                sfd.FileName = "toklens_agent.py";
                if (sfd.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                {
                    File.WriteAllText(sfd.FileName, py, new System.Text.UTF8Encoding(false));
                    SetStatus("已导出探针脚本至 " + sfd.FileName, false);
                }
            }
        }

        // -------------------- Clean Modern Control Builders --------------------
        private Border MakeCard()
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(15, 11, 15, 11);
            b.Margin = new Thickness(0, 0, 0, 10);
            b.SetResourceReference(Border.BackgroundProperty, "BgCard");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            return b;
        }

        private Border MakeArchiveAssuranceNote(string text)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(6);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(9, 5, 9, 5);
            b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11.2;
            t.LineHeight = 16.5;
            t.TextWrapping = TextWrapping.Wrap;
            t.SetResourceReference(TextBlock.ForegroundProperty, "AccentEmerald");
            b.Child = t;
            return b;
        }

        private TextBlock MakeSectionTitle(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 13;
            t.FontWeight = FontWeights.SemiBold;
            t.Margin = new Thickness(0, 0, 0, 10);
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            return t;
        }

        private TextBlock MakeLabel(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11.5;
            t.Margin = new Thickness(0, 0, 0, 4);
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            return t;
        }

        private TextBox MakeThemedTextBox()
        {
            TextBox tb = new TextBox();
            tb.FontSize = 12.5;
            tb.Padding = new Thickness(8, 5, 8, 5);
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
            tb.SetResourceReference(TextBox.CaretBrushProperty, "AccentBlue");

            ControlTemplate tpl = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "TbBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
            bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));

            FrameworkElementFactory sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            sv.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            bd.AppendChild(sv);
            tpl.VisualTree = bd;

            Trigger focusTrig = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focusTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBlue"), "TbBd"));
            tpl.Triggers.Add(focusTrig);

            tb.Template = tpl;
            return tb;
        }

        private Border CreateSegBox()
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(7);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(2.5);
            b.SetResourceReference(Border.BackgroundProperty, "BgCard");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
            b.Child = sp;
            return b;
        }

        private Button CreateSegBtn(string text)
        {
            Button btn = new Button();
            btn.Content = text;
            btn.FontSize = 12;
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "SegBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            return btn;
        }

        private static void UpdateSegGroup(Button[] group, int activeIdx)
        {
            if (group == null) return;
            for (int i = 0; i < group.Length; i++)
            {
                Button b = group[i];
                if (i == activeIdx)
                {
                    b.SetResourceReference(Control.BackgroundProperty, "BgElevated");
                    b.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
                    b.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
                    b.FontWeight = FontWeights.Normal;
                }
            }
        }

        private Button MakeSubtleInlineBtn(string text)
        {
            Button btn = new Button();
            btn.Content = text;
            btn.FontSize = 11;
            btn.Padding = new Thickness(6, 1, 6, 1);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;
            btn.SetResourceReference(Control.ForegroundProperty, "FgSecondary");

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "InBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgElevated"));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hv = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hv.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            tpl.Triggers.Add(hv);

            btn.Template = tpl;
            return btn;
        }

        private Button MakeBtn(string text, bool primary)
        {
            Button btn = new Button();
            btn.Content = text;
            btn.FontSize = 12.5;
            btn.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
            btn.Padding = new Thickness(13, 6, 13, 6);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;

            if (primary)
            {
                btn.Foreground = Brushes.White;
            }
            else
            {
                btn.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
            }

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "B");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            if (primary)
            {
                LinearGradientBrush lg = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0284C7"),
                    (Color)ColorConverter.ConvertFromString("#4F46E5"),
                    45.0);
                lg.Freeze();
                bd.SetValue(Border.BackgroundProperty, lg);
                bd.SetValue(Border.BorderBrushProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")));
            }
            else
            {
                bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgCard"));
                bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));
            }
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hv = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (!primary)
                hv.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgElevated"), "B"));
            hv.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBlue"), "B"));
            tpl.Triggers.Add(hv);

            Trigger dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "B"));
            tpl.Triggers.Add(dis);

            btn.Template = tpl;
            return btn;
        }
    }
}
