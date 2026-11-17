using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TokLens
{
    public sealed class AppUpdateWindow : Window
    {
        private readonly string _currentVersion;
        private readonly Action _onReadyToRestart;
        private readonly TextBlock _headline, _status, _notes, _details;
        private readonly ProgressBar _progress;
        private readonly Button _check, _install, _cancel, _releasePage;
        private CancellationTokenSource _operation;
        private AppUpdateRelease _release;
        private bool _busy, _closed, _handoff;

        public AppUpdateWindow(bool isDark, string currentVersion, Action onReadyToRestart)
        {
            _currentVersion = currentVersion; _onReadyToRestart = onReadyToRestart;
            Title = "TokLens · 应用更新"; Width = 580; Height = 600; MinWidth = 480; MinHeight = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = DesignSystem.FontUi; DesignSystem.ApplyTheme(this, isDark); SetResourceReference(BackgroundProperty, "BgWindow");
            Grid root = new Grid { Margin = new Thickness(24) }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _headline = Text("检查应用更新", 21, true); root.Children.Add(_headline);
            _details = Text("当前版本 " + currentVersion + " · 官方稳定发布通道", 12, false); _details.Margin = new Thickness(0, 8, 0, 16); Grid.SetRow(_details, 1); root.Children.Add(_details);
            _notes = Text("正在获取版本信息…", 13, false); _notes.TextWrapping = TextWrapping.Wrap;
            ScrollViewer notesScroll = new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) }; Grid.SetRow(notesScroll, 2); root.Children.Add(notesScroll);
            _progress = new ProgressBar { Height = 5, Margin = new Thickness(0, 16, 0, 10), Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed }; Grid.SetRow(_progress, 3); root.Children.Add(_progress);
            _status = Text("", 11, false); _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 8, 0, 14); Grid.SetRow(_status, 4); root.Children.Add(_status);
            WrapPanel actions = new WrapPanel { Orientation = Orientation.Horizontal };
            _check = Button("重新检查", delegate { CheckAsync(); }); actions.Children.Add(_check);
            _releasePage = Button("官方发布页", OpenRelease); actions.Children.Add(_releasePage);
            _install = Button("下载并重启安装", delegate { InstallAsync(); }); _install.IsEnabled = false; actions.Children.Add(_install);
            _cancel = Button("取消下载", delegate { if (_operation != null) _operation.Cancel(); }); _cancel.Visibility = Visibility.Collapsed; actions.Children.Add(_cancel);
            Grid.SetRow(actions, 5); root.Children.Add(actions);
            Loaded += delegate { CheckAsync(); };
            Closing += delegate { _closed = true; if (_operation != null && !_handoff) _operation.Cancel(); };
        }
        private TextBlock Text(string value, double size, bool strong)
        { TextBlock text = new TextBlock { Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal }; text.SetResourceReference(TextBlock.ForegroundProperty, strong ? "FgPrimary" : "FgSecondary"); return text; }
        private static Button Button(string label, Action action)
        { Button button = ModernControlsFactory.CreateButton(label, label == "下载并重启安装" ? "primary" : "secondary"); button.Height = 32; button.Margin = new Thickness(0, 0, 8, 6); button.Click += delegate { action(); }; return button; }
        private void SetBusy(bool busy, bool downloading)
        {
            _busy = busy; _check.IsEnabled = !busy; _install.IsEnabled = !busy && _release != null && _release.CanInstall;
            _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; _cancel.Content = downloading ? "取消下载" : "取消检查";
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = busy && !downloading; _progress.Value = 0;
        }
        private async void CheckAsync()
        {
            if (_busy || _closed) return;
            _operation = new CancellationTokenSource(); CancellationToken token = _operation.Token; SetBusy(true, false);
            _headline.Text = "检查应用更新"; _status.Text = "连接 GitHub 官方稳定发布通道…";
            try
            {
                AppUpdateRelease release = await Task.Run(delegate { return AppUpdateService.Check(_currentVersion, token); }, token);
                if (_closed) return; _release = release;
                bool aheadOfStable = AppUpdateService.ParseVersion(_currentVersion).CompareTo(AppUpdateService.ParseVersion(release.Version)) > 0;
                _headline.Text = release.IsNewer ? "发现新版本 " + release.Version : (aheadOfStable ? "当前版本高于稳定发布" : "已是最新稳定版本");
                _details.Text = "当前 " + _currentVersion + " · 最新稳定发布 " + release.Version;
                _notes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "此版本未填写发布说明。" : release.Notes;
                _status.Text = release.IsNewer ? (release.CanInstall ? "点击“下载并重启安装”后，下载通过 SHA-256 完整性校验便会重启安装。保留原程序备份，用量账本和个人配置不变。" : release.UnavailableReason) : (aheadOfStable ? "稳定通道尚无更高版本，继续使用当前版本。" : "当前版本与最新稳定发布一致。");
            }
            catch (OperationCanceledException) { if (!_closed) _status.Text = "检查已取消。"; }
            catch (Exception ex) { if (!_closed) { _headline.Text = "暂时无法检查更新"; _status.Text = ex.Message; } }
            finally { if (!_closed) SetBusy(false, false); _operation.Dispose(); _operation = null; }
        }
        private async void InstallAsync()
        {
            if (_busy || _closed || _release == null || !_release.CanInstall) return;
            if (_onReadyToRestart == null) { _status.Text = "当前环境没有配置重启处理，请从官方发布页手动安装。"; return; }
            string executable;
            try
            {
                executable = Process.GetCurrentProcess().MainModule.FileName;
                AppUpdateService.EnsureWritableInstallation(executable);
            }
            catch (Exception ex) { _status.Text = ex.Message; return; }
            _operation = new CancellationTokenSource(); CancellationToken token = _operation.Token; SetBusy(true, true); _status.Text = "准备下载，当前版本继续运行…";
            try
            {
                AppUpdateDownload download = await Task.Run(delegate
                {
                    return AppUpdateService.Download(_release, _currentVersion, token, delegate(AppUpdateProgress progress)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (_closed) return; _progress.IsIndeterminate = progress.Total <= 0; _progress.Value = progress.Percent;
                            _status.Text = progress.Stage + (progress.Total > 0 ? " · " + (progress.Downloaded / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " / " + (progress.Total / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : "");
                        }));
                    });
                }, token);
                token.ThrowIfCancellationRequested(); if (_closed) return;
                string[] original = Environment.GetCommandLineArgs(); string[] restart = new string[Math.Max(0, original.Length - 1)]; if (restart.Length > 0) Array.Copy(original, 1, restart, 0, restart.Length);
                Process current = Process.GetCurrentProcess();
                AppUpdateService.StartInstall(download, _currentVersion, executable, current.Id, current.StartTime.ToUniversalTime().Ticks, restart);
                _handoff = true; _status.Text = "校验通过，正在退出并安装更新…"; _cancel.IsEnabled = false;
                _onReadyToRestart();
            }
            catch (OperationCanceledException) { if (!_closed) _status.Text = "下载已取消，当前版本保持不变。"; }
            catch (Exception ex) { if (!_closed) _status.Text = "更新未安装：" + ex.Message; }
            finally { if (!_closed && !_handoff) SetBusy(false, false); _operation.Dispose(); _operation = null; }
        }
        private void OpenRelease()
        {
            string url = _release != null ? _release.ReleaseUrl : "https://github.com/" + AppUpdateService.Repository + "/releases";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { _status.Text = "无法打开发布页：" + ex.Message; }
        }
    }
}
