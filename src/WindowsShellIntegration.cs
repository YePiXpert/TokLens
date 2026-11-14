using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TokLens
{
    /// <summary>
    /// Windows 系统级桌面驻留集成中心：
    /// 提供免 UAC 开机静默自启管理、Win10/11 多显示器动态高 DPI 感知初始化、
    /// 安全资源管理器路径定位，以及跨虚拟桌面窗口唤醒与多屏工作区辅助能力。
    /// </summary>
    public static class WindowsShellIntegration
    {
        public const string AutoStartRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string AutoStartRegistryValueName = "TokLens";
        public const string MinimizedStartArgument = "--minimized";

        private const int ERROR_ACCESS_DENIED = 5;
        private const int PROCESS_SYSTEM_DPI_AWARE = 1;
        private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

        // DPI_AWARENESS_CONTEXT 伪句柄常量（指针宽度兼容 x86 / x64）
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new IntPtr(-3);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        #region Win32 & Shcore P/Invoke

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        #endregion

        #region 1. 开机静默自启管理（HKCU 免管理员 UAC）

        /// <summary>
        /// 获取当前可执行文件的完整本地路径。
        /// </summary>
        public static string GetCurrentExecutablePath()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry != null && !string.IsNullOrEmpty(entry.Location))
                {
                    string full = Path.GetFullPath(entry.Location);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
            }
            catch
            {
            }

            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    if (current != null && current.MainModule != null && !string.IsNullOrEmpty(current.MainModule.FileName))
                    {
                        string full = Path.GetFullPath(current.MainModule.FileName);
                        if (File.Exists(full))
                        {
                            return full;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                Assembly exec = Assembly.GetExecutingAssembly();
                if (exec != null && !string.IsNullOrEmpty(exec.Location))
                {
                    string full = Path.GetFullPath(exec.Location);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        /// <summary>
        /// 构造写入注册表 Run 键的标准静默启动命令行："&lt;当前Exe完整路径&gt;" --minimized
        /// </summary>
        public static string BuildAutoStartCommand(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return string.Empty;
            }
            string sanitized = exePath.Trim().Replace("\"", string.Empty);
            return "\"" + sanitized + "\" " + MinimizedStartArgument;
        }

        /// <summary>
        /// 检查当前用户开机自启是否已启用（校验注册表键值存在且目标可执行文件真实存在）。
        /// </summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoStartRegistrySubKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    object rawObj = key.GetValue(AutoStartRegistryValueName, null);
                    string rawVal = rawObj as string;
                    if (string.IsNullOrWhiteSpace(rawVal))
                    {
                        return false;
                    }

                    string exePath = ExtractExecutablePathFromRunValue(rawVal);
                    if (string.IsNullOrEmpty(exePath))
                    {
                        return false;
                    }

                    return File.Exists(exePath);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 开启或关闭开机静默自启：
        /// 开启时自动写入最新 Exe 完整路径与 --minimized 参数；关闭时安全移除注册表键值。
        /// </summary>
        public static bool SetAutoStartEnabled(bool enable, out string message)
        {
            if (enable)
            {
                string exePath = GetCurrentExecutablePath();
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    message = "无法定位当前程序可执行文件路径，开启开机自启失败。";
                    return false;
                }

                string command = BuildAutoStartCommand(exePath);
                try
                {
                    RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoStartRegistrySubKey, true);
                    if (key == null)
                    {
                        key = Registry.CurrentUser.CreateSubKey(AutoStartRegistrySubKey);
                    }

                    if (key == null)
                    {
                        message = "无法打开当前用户注册表启动项，请检查系统权限策略。";
                        return false;
                    }

                    using (key)
                    {
                        key.SetValue(AutoStartRegistryValueName, command, RegistryValueKind.String);
                    }

                    message = "已开启开机静默自启（随系统启动并最小化驻留托盘）。";
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    message = "注册表写入被拒绝，可能受到安全软件或组策略拦截。";
                    return false;
                }
                catch (SecurityException)
                {
                    message = "当前安全上下文无权访问注册表启动项。";
                    return false;
                }
                catch (Exception ex)
                {
                    message = "开启开机自启失败：" + ex.Message;
                    return false;
                }
            }
            else
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoStartRegistrySubKey, true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(AutoStartRegistryValueName, false);
                        }
                    }

                    message = "已关闭开机自启。";
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    message = "移除开机自启项被拒绝，可能受到安全软件拦截。";
                    return false;
                }
                catch (SecurityException)
                {
                    message = "当前安全上下文无权修改注册表启动项。";
                    return false;
                }
                catch (Exception ex)
                {
                    message = "关闭开机自启失败：" + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// 若用户此前已开启开机自启且移动了便携版 Exe 目录，静默同步更新为最新可执行文件路径。
        /// </summary>
        public static bool SyncAutoStartPathIfRegistered()
        {
            try
            {
                string currentExe = GetCurrentExecutablePath();
                if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                {
                    return false;
                }

                string expectedCmd = BuildAutoStartCommand(currentExe);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoStartRegistrySubKey, true))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string existing = key.GetValue(AutoStartRegistryValueName, null) as string;
                    if (string.IsNullOrWhiteSpace(existing))
                    {
                        return false;
                    }

                    if (!string.Equals(existing.Trim(), expectedCmd, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(AutoStartRegistryValueName, expectedCmd, RegistryValueKind.String);
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractExecutablePathFromRunValue(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            string s = rawValue.Trim();
            string candidate = string.Empty;

            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int closeQuote = s.IndexOf('"', 1);
                if (closeQuote > 1)
                {
                    candidate = s.Substring(1, closeQuote - 1).Trim();
                }
            }
            else
            {
                int exeIndex = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex > 0)
                {
                    int endPos = exeIndex + 4;
                    if (endPos == s.Length || char.IsWhiteSpace(s[endPos]))
                    {
                        candidate = s.Substring(0, endPos).Trim();
                    }
                }

                if (string.IsNullOrEmpty(candidate))
                {
                    int spaceIdx = s.IndexOf(' ');
                    candidate = spaceIdx > 0 ? s.Substring(0, spaceIdx).Trim() : s;
                }
            }

            if (string.IsNullOrEmpty(candidate) || candidate.IndexOf('"') >= 0)
            {
                return string.Empty;
            }

            try
            {
                if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    return string.Empty;
                }
                return Path.GetFullPath(candidate);
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion

        #region 2. Win10/11 多显示器动态高 DPI 感知初始化辅助

        /// <summary>
        /// 在 Main() 创建 WPF Application 实例之前安全启用多显示器动态高 DPI 感知：
        /// 优先启用 Per-Monitor V2（Win10 1703+ / Win11），失败时依次回退至 Per-Monitor V1、
        /// Shcore SetProcessDpiAwareness 以及 Win7 SetProcessDPIAware，全程静默容错。
        /// </summary>
        public static void TryEnablePerMonitorDpiAwareness()
        {
            // 第 1 优先级：Windows 10 1703+ / Windows 11 Per-Monitor V2
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    return;
                }

                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED)
                {
                    // 进程已由清单或宿主锁定 DPI 上下文，无需继续降级调用
                    return;
                }

                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE))
                {
                    return;
                }
            }
            catch
            {
                // 旧版 Windows 无 SetProcessDpiAwarenessContext 导出，平滑降级
            }

            // 第 2 优先级：Windows 8.1 / 早期 Windows 10 (shcore.dll)
            try
            {
                int hr = SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
                if (hr == 0 || hr == unchecked((int)0x80070005))
                {
                    return;
                }

                hr = SetProcessDpiAwareness(PROCESS_SYSTEM_DPI_AWARE);
                if (hr == 0)
                {
                    return;
                }
            }
            catch
            {
                // Windows 7 无 shcore.dll，平滑降级
            }

            // 第 3 优先级：Windows Vista / Windows 7 系统级 DPI 感知
            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        #endregion

        #region 3. 安全打开路径与定位文件助手（防命令注入）

        /// <summary>
        /// 安全校验本地文件或目录存在后，调用系统目录 explorer.exe 选中目标文件或直接打开目录。
        /// 采用绝对路径 explorer.exe + UseShellExecute=false 直调进程，严格过滤非法字符与引号，免疫命令行拼接注入。
        /// </summary>
        public static bool RevealInExplorer(string fileOrDirPath)
        {
            if (string.IsNullOrWhiteSpace(fileOrDirPath))
            {
                return false;
            }

            string candidate = fileOrDirPath.Trim();
            if (candidate.Length >= 2 &&
                candidate[0] == '"' &&
                candidate[candidate.Length - 1] == '"')
            {
                candidate = candidate.Substring(1, candidate.Length - 2).Trim();
            }

            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            // 严禁包含双引号、控制字符或协议前缀，彻底阻断参数注入与远程 URI 诱导
            for (int i = 0; i < candidate.Length; i++)
            {
                char ch = candidate[i];
                if (ch == '"' || ch == '\0' || ch == '\r' || ch == '\n')
                {
                    return false;
                }
            }

            if (candidate.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            string fullPath;
            try
            {
                if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    return false;
                }
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(fullPath) || fullPath.IndexOf('"') >= 0)
            {
                return false;
            }

            bool isFile = false;
            bool isDir = false;
            try
            {
                isFile = File.Exists(fullPath);
                if (!isFile)
                {
                    isDir = Directory.Exists(fullPath);
                }
            }
            catch
            {
                return false;
            }

            if (!isFile && !isDir)
            {
                return false;
            }

            string explorerPath = ResolveTrustedExplorerExecutable();
            string arguments = isFile
                ? ("/select,\"" + fullPath + "\"")
                : ("\"" + fullPath + "\"");

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = explorerPath;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false;
                using (Process proc = Process.Start(psi))
                {
                    // explorer.exe 采用单实例 DDE/COM 转发后可能立即退出，进程成功拉起并释放句柄即视为成功
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveTrustedExplorerExecutable()
        {
            try
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(winDir))
                {
                    string candidate = Path.Combine(winDir, "explorer.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }

            try
            {
                string sysDir = Environment.SystemDirectory;
                if (!string.IsNullOrEmpty(sysDir))
                {
                    DirectoryInfo parent = Directory.GetParent(sysDir);
                    if (parent != null)
                    {
                        string candidate = Path.Combine(parent.FullName, "explorer.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
            }

            return "explorer.exe";
        }

        #endregion
    }

    /// <summary>
    /// 系统级全局唤醒快捷键宿主：
    /// 基于 Win32 RegisterHotKey / UnregisterHotKey 与 HwndSource.AddHook 拦截 WM_HOTKEY (0x0312)。
    /// 默认注册 Ctrl+Alt+T，若冲突占用则平滑降级为 Ctrl+Shift+Alt+T，永不抛异常崩溃。
    /// 支持在宿主窗口尚未 Show()（如 --minimized 静默驻留托盘启动）时即刻完成注册与响应。
    /// </summary>
    public sealed class GlobalHotkeyHost : IDisposable
    {
        public const string DefaultShortcutLabel = "Ctrl+Alt+T";
        public const string FallbackShortcutLabel = "Ctrl+Shift+Alt+T";
        public const string OccupiedShortcutLabel = "快捷键被占用";

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_T = 0x54;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private static int _hotkeyIdCounter = 0x4B50;

        private readonly Window _hostWindow;
        private readonly Action _onHotkeyTriggered;
        private readonly int _hotkeyId;
        private readonly HwndSourceHook _wndProcHook;

        private HwndSource _hwndSource;
        private IntPtr _registeredHwnd;
        private bool _ownsHwndSource;
        private bool _hookAdded;
        private bool _sourceInitializedSubscribed;
        private bool _closedSubscribed;
        private bool _disposed;
        private int _lastTriggerTick;
        private bool _hasLastTrigger;

        /// <summary>
        /// 当前生效的全局快捷键文案（如 "Ctrl+Alt+T"、"Ctrl+Shift+Alt+T" 或 "快捷键被占用"）。
        /// </summary>
        public string ActiveShortcutLabel { get; private set; }

        /// <summary>
        /// 指示全局快捷键是否已成功向操作系统注册。
        /// </summary>
        public bool IsRegistered { get; private set; }

        /// <summary>
        /// 指示当前是否因默认快捷键冲突而自动回退到了备选组合键（Ctrl+Shift+Alt+T）。
        /// </summary>
        public bool IsUsingFallback { get; private set; }

        public GlobalHotkeyHost(Window hostWindow, Action onHotkeyTriggered)
        {
            _hostWindow = hostWindow;
            _onHotkeyTriggered = onHotkeyTriggered;

            int seq = Interlocked.Increment(ref _hotkeyIdCounter);
            _hotkeyId = 0x4000 + (seq & 0x3FFF);
            _wndProcHook = new HwndSourceHook(WndProc);
            _registeredHwnd = IntPtr.Zero;

            ActiveShortcutLabel = OccupiedShortcutLabel;
            IsRegistered = false;
            IsUsingFallback = false;

            if (_hostWindow != null)
            {
                try
                {
                    _hostWindow.Closed += OnHostWindowClosed;
                    _closedSubscribed = true;
                }
                catch
                {
                }
            }

            InitializeAndRegister();
        }

        private void InitializeAndRegister()
        {
            if (_disposed)
            {
                return;
            }

            // 1. 若宿主窗口已拥有底层 HWND，直接复用宿主窗口 HwndSource
            if (_hostWindow != null)
            {
                try
                {
                    IntPtr existingHwnd = new WindowInteropHelper(_hostWindow).Handle;
                    if (existingHwnd != IntPtr.Zero)
                    {
                        HwndSource existingSource = HwndSource.FromHwnd(existingHwnd);
                        if (existingSource != null)
                        {
                            AttachToSource(existingSource, false);
                            RegisterWithFallback(existingHwnd);
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            // 2. 若宿主窗口尚未 Show()（如 --minimized 托盘驻留或构造函数阶段），创建零闪烁轻量级 HWND_MESSAGE 消息窗口立即注册
            try
            {
                HwndSourceParameters parameters = new HwndSourceParameters("TokLensGlobalHotkeySink");
                parameters.Width = 0;
                parameters.Height = 0;
                parameters.PositionX = 0;
                parameters.PositionY = 0;
                parameters.WindowStyle = 0;
                parameters.ParentWindow = HWND_MESSAGE;

                HwndSource msgSource = new HwndSource(parameters);
                if (msgSource.Handle != IntPtr.Zero)
                {
                    AttachToSource(msgSource, true);
                    RegisterWithFallback(msgSource.Handle);
                    return;
                }
                msgSource.Dispose();
            }
            catch
            {
            }

            // 3. 兜底回退：若受限环境无法创建独立消息窗口，则挂载宿主窗口 SourceInitialized 事件延迟注册
            if (_hostWindow != null && !_sourceInitializedSubscribed)
            {
                try
                {
                    _hostWindow.SourceInitialized += OnHostWindowSourceInitialized;
                    _sourceInitializedSubscribed = true;
                }
                catch
                {
                }
            }
        }

        private void AttachToSource(HwndSource source, bool ownsSource)
        {
            _hwndSource = source;
            _ownsHwndSource = ownsSource;
            if (_hwndSource != null && !_hookAdded)
            {
                _hwndSource.AddHook(_wndProcHook);
                _hookAdded = true;
            }
        }

        private void OnHostWindowSourceInitialized(object sender, EventArgs e)
        {
            if (_disposed || IsRegistered || _hostWindow == null)
            {
                return;
            }

            try
            {
                IntPtr hwnd = new WindowInteropHelper(_hostWindow).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    HwndSource src = HwndSource.FromHwnd(hwnd);
                    if (src != null)
                    {
                        AttachToSource(src, false);
                        RegisterWithFallback(hwnd);
                    }
                }
            }
            catch
            {
            }
        }

        private void OnHostWindowClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void RegisterWithFallback(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _disposed)
            {
                ActiveShortcutLabel = OccupiedShortcutLabel;
                IsRegistered = false;
                IsUsingFallback = false;
                return;
            }

            // 首选注册：Ctrl + Alt + T
            if (TryRegisterCombo(hwnd, MOD_CONTROL | MOD_ALT, VK_T))
            {
                _registeredHwnd = hwnd;
                IsRegistered = true;
                IsUsingFallback = false;
                ActiveShortcutLabel = DefaultShortcutLabel;
                return;
            }

            // 优雅回退：Ctrl + Shift + Alt + T
            if (TryRegisterCombo(hwnd, MOD_CONTROL | MOD_SHIFT | MOD_ALT, VK_T))
            {
                _registeredHwnd = hwnd;
                IsRegistered = true;
                IsUsingFallback = true;
                ActiveShortcutLabel = FallbackShortcutLabel;
                return;
            }

            // 均被其他应用抢占：标记友好中文状态，绝不抛异常崩溃
            _registeredHwnd = IntPtr.Zero;
            IsRegistered = false;
            IsUsingFallback = false;
            ActiveShortcutLabel = OccupiedShortcutLabel;
        }

        private bool TryRegisterCombo(IntPtr hwnd, uint modifiers, uint virtualKey)
        {
            try
            {
                if (RegisterHotKey(hwnd, _hotkeyId, modifiers | MOD_NOREPEAT, virtualKey))
                {
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_HOTKEY_ALREADY_REGISTERED)
                {
                    return false;
                }

                // 兼容极少数不支持 MOD_NOREPEAT 的旧版内核环境
                if (RegisterHotKey(hwnd, _hotkeyId, modifiers, virtualKey))
                {
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_disposed && msg == WM_HOTKEY)
            {
                long id = wParam.ToInt64();
                if (id == (long)_hotkeyId)
                {
                    handled = true;
                    DispatchHotkeyTriggered();
                }
            }
            return IntPtr.Zero;
        }

        private void DispatchHotkeyTriggered()
        {
            if (_disposed || _onHotkeyTriggered == null)
            {
                return;
            }

            // 80ms 轻量防抖：防止不支持 MOD_NOREPEAT 时长按引发高频闪烁，同时不影响用户快速手动切换
            int now = Environment.TickCount;
            if (_hasLastTrigger && unchecked((uint)(now - _lastTriggerTick)) < 80u)
            {
                return;
            }
            _lastTriggerTick = now;
            _hasLastTrigger = true;

            try
            {
                if (_hostWindow != null && _hostWindow.Dispatcher != null && !_hostWindow.Dispatcher.CheckAccess())
                {
                    _hostWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(InvokeCallbackSafe));
                }
                else
                {
                    InvokeCallbackSafe();
                }
            }
            catch
            {
            }
        }

        private void InvokeCallbackSafe()
        {
            if (_disposed || _onHotkeyTriggered == null)
            {
                return;
            }

            try
            {
                _onHotkeyTriggered();
            }
            catch
            {
            }
        }

        private void UnregisterHotkeyInternal()
        {
            if (IsRegistered && _registeredHwnd != IntPtr.Zero)
            {
                try
                {
                    UnregisterHotKey(_registeredHwnd, _hotkeyId);
                }
                catch
                {
                }
            }

            _registeredHwnd = IntPtr.Zero;
            IsRegistered = false;
            IsUsingFallback = false;
            ActiveShortcutLabel = OccupiedShortcutLabel;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_hostWindow != null)
            {
                if (_sourceInitializedSubscribed)
                {
                    try { _hostWindow.SourceInitialized -= OnHostWindowSourceInitialized; } catch { }
                    _sourceInitializedSubscribed = false;
                }

                if (_closedSubscribed)
                {
                    try { _hostWindow.Closed -= OnHostWindowClosed; } catch { }
                    _closedSubscribed = false;
                }
            }

            UnregisterHotkeyInternal();

            if (_hwndSource != null)
            {
                if (_hookAdded)
                {
                    try { _hwndSource.RemoveHook(_wndProcHook); } catch { }
                    _hookAdded = false;
                }

                if (_ownsHwndSource)
                {
                    try { _hwndSource.Dispose(); } catch { }
                    _ownsHwndSource = false;
                }

                _hwndSource = null;
            }

            GC.SuppressFinalize(this);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
