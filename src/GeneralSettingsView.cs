using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace TokLens
{
    public sealed class GeneralSettingsCallbacks
    {
        public Action<string> OnHomeDirectoryChanged;
        public Action OnFeaturesChanged;
        public Action OnCreateBackup;
        public Action<UIElement> OnOpenBackupPopover;
        public Action OnCheckUpdate;
        public Action<string, string, string> ShowToast;
    }

    /// <summary>
    /// 「设置 → 常规」页：只保留高频本机设置，全部即时生效，无保存按钮。
    /// </summary>
    public sealed class GeneralSettingsView : Border
    {
        private readonly SyncConfig _liveCfg;
        private readonly Func<string> _getCurrentHomeDir;
        private readonly GeneralSettingsCallbacks _callbacks;
        private bool _suppressEvents = false;

        private TextBlock _homeDirPathText;
        private CheckBox _wslSwitch;
        private CheckBox _autoStartSwitch;
        private CheckBox _minToTraySwitch;
        private CheckBox _subsSwitch;
        private TextBlock _ledgerSummaryText;
        private Button _restoreBackupBtn;
        private CheckBox _autoUpdateSwitch;

        public GeneralSettingsView(
            SyncConfig liveCfg,
            Func<string> getCurrentHomeDir,
            GeneralSettingsCallbacks callbacks)
        {
            _liveCfg = liveCfg ?? new SyncConfig();
            _getCurrentHomeDir = getCurrentHomeDir;
            _callbacks = callbacks ?? new GeneralSettingsCallbacks();

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            SetResourceReference(Border.BackgroundProperty, "BgWindow");

            Child = BuildLayout();
            Reload();
        }

        public void Reload()
        {
            _suppressEvents = true;
            try
            {
                string home = ResolveHomeDir();
                if (_homeDirPathText != null)
                {
                    _homeDirPathText.Text = home;
                    _homeDirPathText.ToolTip = home;
                }
                if (_wslSwitch != null) _wslSwitch.IsChecked = _liveCfg.ScanLocalWsl;
                if (_autoStartSwitch != null) _autoStartSwitch.IsChecked = WindowsShellIntegration.IsAutoStartEnabled();
                if (_minToTraySwitch != null) _minToTraySwitch.IsChecked = _liveCfg.MinimizeToTray;
                if (_subsSwitch != null) _subsSwitch.IsChecked = _liveCfg.ShowSubscriptions;
                if (_autoUpdateSwitch != null) _autoUpdateSwitch.IsChecked = AppUpdatePreferences.Load().Enabled;
                RefreshLedgerSummary();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        public void RefreshLedgerSummary()
        {
            if (_ledgerSummaryText == null) return;
            string dbPath = Path.Combine(SyncService.AppConfigDir, "toklens.db");
            long bytes = 0L;
            bool exists = false;
            try
            {
                if (File.Exists(dbPath))
                {
                    exists = true;
                    bytes = new FileInfo(dbPath).Length;
                    string wal = dbPath + "-wal";
                    if (File.Exists(wal)) bytes += new FileInfo(wal).Length;
                }
            }
            catch { }

            string sizeText = "待初始化";
            if (exists)
            {
                double kb = bytes / 1024.0;
                sizeText = kb >= 1024.0
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F2} MB", kb / 1024.0)
                    : string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", kb);
            }

            int backupCount = 0;
            try
            {
                List<BackupArchiveInfo> backups = BackupAndMigrationService.ListLocalBackups();
                if (backups != null) backupCount = backups.Count;
            }
            catch { }

            _ledgerSummaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "toklens.db ({0}) · 已保留 {1} 份自动快照",
                sizeText,
                backupCount);
            _ledgerSummaryText.ToolTip = dbPath;
        }

        private UIElement BuildLayout()
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

            // 分组 1：日志扫描
            center.Children.Add(MakeGroupTitle("日志扫描", true));
            Border scanCard = ModernControlsFactory.CreateCard(new Thickness(18, 2, 18, 2));
            StackPanel scanRows = new StackPanel();

            // 1. 扫描主目录
            StackPanel homeBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button browseBtn = ModernControlsFactory.CreateButton("浏览…", "secondary");
            browseBtn.Height = 28;
            browseBtn.Margin = new Thickness(0, 0, 6, 0);
            browseBtn.Click += OnBrowseHomeClick;
            homeBtns.Children.Add(browseBtn);

            Button resetBtn = ModernControlsFactory.CreateButton("恢复默认", "ghost");
            resetBtn.Height = 28;
            resetBtn.Click += OnResetHomeClick;
            homeBtns.Children.Add(resetBtn);

            StackPanel homeLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            homeLeft.Children.Add(MakeRowTitle("扫描用户主目录"));
            _homeDirPathText = new TextBlock
            {
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _homeDirPathText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            homeLeft.Children.Add(_homeDirPathText);
            scanRows.Children.Add(MakeSettingRow(homeLeft, homeBtns, true));

            // 2. WSL2
            _wslSwitch = ModernControlsFactory.CreateToggleSwitch("");
            _wslSwitch.VerticalAlignment = VerticalAlignment.Center;
            _wslSwitch.Click += OnWslToggleClick;
            scanRows.Children.Add(MakeSettingRow(
                MakeRowText("穿透扫描 WSL2", "自动发现并增量监听 \\\\wsl$\\ 下 Linux 子系统中的工具日志"),
                _wslSwitch,
                false));

            scanCard.Child = scanRows;
            center.Children.Add(scanCard);

            // 分组 2：运行与界面
            center.Children.Add(MakeGroupTitle("运行与界面", false));
            Border appCard = ModernControlsFactory.CreateCard(new Thickness(18, 2, 18, 2));
            StackPanel appRows = new StackPanel();

            // 3. 开机静默自启
            _autoStartSwitch = ModernControlsFactory.CreateToggleSwitch("");
            _autoStartSwitch.VerticalAlignment = VerticalAlignment.Center;
            _autoStartSwitch.Click += OnAutoStartToggleClick;
            appRows.Children.Add(MakeSettingRow(
                MakeRowText("开机静默自启", "登录 Windows 后自动在后台启动并常驻托盘"),
                _autoStartSwitch,
                true));

            // 4. 关闭时常驻托盘
            _minToTraySwitch = ModernControlsFactory.CreateToggleSwitch("");
            _minToTraySwitch.VerticalAlignment = VerticalAlignment.Center;
            _minToTraySwitch.Click += OnMinToTrayToggleClick;
            appRows.Children.Add(MakeSettingRow(
                MakeRowText("关闭窗口时常驻系统托盘", "点右上角关闭仅隐藏窗口，右键托盘菜单可彻底退出"),
                _minToTraySwitch,
                true));

            // 5. 订阅与费用页
            _subsSwitch = ModernControlsFactory.CreateToggleSwitch("");
            _subsSwitch.VerticalAlignment = VerticalAlignment.Center;
            _subsSwitch.Click += OnSubsToggleClick;
            appRows.Children.Add(MakeSettingRow(
                MakeRowText("显示「订阅与费用」页", "在侧栏显示可选订阅记账页，按 API 参考估值估算本周期回本比例"),
                _subsSwitch,
                false));

            appCard.Child = appRows;
            center.Children.Add(appCard);

            // 分组 3：数据与更新
            center.Children.Add(MakeGroupTitle("数据与更新", false));
            Border dataCard = ModernControlsFactory.CreateCard(new Thickness(18, 2, 18, 2));
            StackPanel dataRows = new StackPanel();

            // 6. 本地账本与备份
            StackPanel backupBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button snapBtn = ModernControlsFactory.CreateButton("立即备份", "secondary");
            snapBtn.Height = 28;
            snapBtn.Margin = new Thickness(0, 0, 6, 0);
            snapBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_callbacks.OnCreateBackup != null) _callbacks.OnCreateBackup();
                RefreshLedgerSummary();
            };
            backupBtns.Children.Add(snapBtn);

            _restoreBackupBtn = ModernControlsFactory.CreateButton("恢复 / 导入…", "secondary");
            _restoreBackupBtn.Height = 28;
            _restoreBackupBtn.Margin = new Thickness(0, 0, 6, 0);
            _restoreBackupBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_callbacks.OnOpenBackupPopover != null) _callbacks.OnOpenBackupPopover(_restoreBackupBtn);
            };
            backupBtns.Children.Add(_restoreBackupBtn);

            Button openDirBtn = ModernControlsFactory.CreateButton("打开目录", "ghost");
            openDirBtn.Height = 28;
            openDirBtn.ToolTip = SyncService.AppConfigDir;
            openDirBtn.Click += delegate(object s, RoutedEventArgs e) { OpenDataDir(); };
            backupBtns.Children.Add(openDirBtn);

            StackPanel ledgerLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            ledgerLeft.Children.Add(MakeRowTitle("本地账本与时光机备份"));
            _ledgerSummaryText = new TextBlock
            {
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _ledgerSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            ledgerLeft.Children.Add(_ledgerSummaryText);
            dataRows.Children.Add(MakeSettingRow(ledgerLeft, backupBtns, true));

            // 7. 应用更新
            StackPanel updateRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _autoUpdateSwitch = ModernControlsFactory.CreateToggleSwitch("每天检查");
            _autoUpdateSwitch.VerticalAlignment = VerticalAlignment.Center;
            _autoUpdateSwitch.Margin = new Thickness(0, 0, 12, 0);
            _autoUpdateSwitch.ToolTip = "每天后台检查一次新版并仅提示，不自动下载或安装";
            _autoUpdateSwitch.Click += OnAutoUpdateToggleClick;
            updateRight.Children.Add(_autoUpdateSwitch);

            Button checkUpdateBtn = ModernControlsFactory.CreateButton("检查更新", "secondary");
            checkUpdateBtn.Height = 28;
            checkUpdateBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (_callbacks.OnCheckUpdate != null) _callbacks.OnCheckUpdate();
            };
            updateRight.Children.Add(checkUpdateBtn);

            dataRows.Children.Add(MakeSettingRow(
                MakeRowText("应用更新", "当前版本 " + AppConstants.VersionTag + " · 更新前校验 SHA-256，保留配置与账本"),
                updateRight,
                false));

            dataCard.Child = dataRows;
            center.Children.Add(dataCard);

            scroll.Content = center;
            return scroll;
        }

        private void OnBrowseHomeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                using (WinForms.FolderBrowserDialog dlg = new WinForms.FolderBrowserDialog())
                {
                    dlg.Description = "选择要扫描的 Windows 用户主目录（例如 C:\\Users\\YourName）";
                    dlg.SelectedPath = ResolveHomeDir();
                    dlg.ShowNewFolderButton = false;
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    {
                        string chosen = dlg.SelectedPath.Trim();
                        _homeDirPathText.Text = chosen;
                        _homeDirPathText.ToolTip = chosen;
                        if (_callbacks.OnHomeDirectoryChanged != null)
                            _callbacks.OnHomeDirectoryChanged(chosen);
                    }
                }
            }
            catch (Exception ex)
            {
                Toast("切换扫描目录失败", ex.Message, "error");
            }
        }

        private void OnResetHomeClick(object sender, RoutedEventArgs e)
        {
            string defHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(defHome)) return;
            _homeDirPathText.Text = defHome;
            _homeDirPathText.ToolTip = defHome;
            if (_callbacks.OnHomeDirectoryChanged != null)
                _callbacks.OnHomeDirectoryChanged(defHome);
        }

        private void OnWslToggleClick(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool enabled = _wslSwitch.IsChecked == true;
            _liveCfg.ScanLocalWsl = enabled;
            SyncService.SaveConfig(_liveCfg);
            if (_callbacks.OnHomeDirectoryChanged != null)
                _callbacks.OnHomeDirectoryChanged(ResolveHomeDir());
            Toast(enabled ? "已开启 WSL2 扫描" : "已关闭 WSL2 扫描",
                enabled ? "正在后台扫描 WSL2 发行版日志" : "仅扫描当前 Windows 用户目录",
                "accent");
        }

        private void OnAutoStartToggleClick(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool want = _autoStartSwitch.IsChecked == true;
            string msg;
            bool ok = WindowsShellIntegration.SetAutoStartEnabled(want, out msg);
            if (!ok)
            {
                _suppressEvents = true;
                _autoStartSwitch.IsChecked = WindowsShellIntegration.IsAutoStartEnabled();
                _suppressEvents = false;
            }
            Toast(ok ? (want ? "已开启开机静默自启" : "已关闭开机自启") : "开机自启设置失败", msg, ok ? "success" : "warning");
        }

        private void OnMinToTrayToggleClick(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = _minToTraySwitch.IsChecked == true;
            _liveCfg.MinimizeToTray = on;
            SyncService.SaveConfig(_liveCfg);
            Toast(on ? "已开启关闭常驻托盘" : "已改为关闭直接退出",
                on ? "点右上角关闭将隐藏到托盘" : "点右上角关闭将直接退出程序",
                "accent");
        }

        private void OnSubsToggleClick(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = _subsSwitch.IsChecked == true;
            _liveCfg.ShowSubscriptions = on;
            SyncService.SaveConfig(_liveCfg);
            if (_callbacks.OnFeaturesChanged != null) _callbacks.OnFeaturesChanged();
            Toast(on ? "已显示「订阅与费用」页" : "已隐藏「订阅与费用」页",
                on ? "可在侧栏登记订阅实付" : "已登记的订阅数据会保留",
                "accent");
        }

        private void OnAutoUpdateToggleClick(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool want = _autoUpdateSwitch.IsChecked == true;
            AppUpdatePreferences prefs = AppUpdatePreferences.Load();
            prefs.Enabled = want;
            try
            {
                prefs.Save();
            }
            catch (Exception ex)
            {
                _suppressEvents = true;
                _autoUpdateSwitch.IsChecked = !want;
                _suppressEvents = false;
                Toast("无法保存更新偏好", ex.Message, "warning");
            }
        }

        private void OpenDataDir()
        {
            try
            {
                Directory.CreateDirectory(SyncService.AppConfigDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + SyncService.AppConfigDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Toast("无法打开数据目录", ex.Message, "warning");
            }
        }

        private string ResolveHomeDir()
        {
            if (_getCurrentHomeDir != null)
            {
                string h = _getCurrentHomeDir();
                if (!string.IsNullOrWhiteSpace(h)) return h;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void Toast(string title, string detail, string variant)
        {
            if (_callbacks != null && _callbacks.ShowToast != null)
                _callbacks.ShowToast(title, detail, variant);
        }

        private static TextBlock MakeGroupTitle(string text, bool first)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, first ? 0 : 16, 0, 6)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            return tb;
        }

        private static TextBlock MakeRowTitle(string text)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.Medium
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            return tb;
        }

        private static StackPanel MakeRowText(string title, string desc)
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

        private static Border MakeSettingRow(UIElement left, UIElement right, bool divider)
        {
            Border row = new Border
            {
                Padding = new Thickness(0, 12, 0, 12),
                BorderThickness = new Thickness(0, 0, 0, divider ? 1 : 0)
            };
            if (divider) row.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel dock = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
            dock.Children.Add(left);
            row.Child = dock;
            return row;
        }
    }
}
