using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace TokLens
{
    public sealed class AppUpdateRelease
    {
        public string Version = "", Tag = "", Name = "", Notes = "", ReleaseUrl = "", ExecutableUrl = "", ChecksumsUrl = "", UnavailableReason = "";
        public long ExecutableBytes;
        public bool IsNewer;
        public bool CanInstall { get { return IsNewer && UnavailableReason.Length == 0 && ExecutableUrl.Length > 0 && ChecksumsUrl.Length > 0; } }
    }
    public sealed class AppUpdateProgress
    {
        public long Downloaded, Total;
        public string Stage;
        public double Percent { get { return Total > 0 ? Math.Min(100, Downloaded * 100.0 / Total) : 0; } }
    }
    public sealed class AppUpdateDownload
    {
        public string FilePath, Sha256, Version;
    }

    public static class AppUpdateService
    {
        public const string Repository = "YePiXpert/TokLens";
        public const string LatestReleaseApi = "https://api.github.com/repos/YePiXpert/TokLens/releases/latest";
        public const long MaximumExecutableBytes = 128L * 1024 * 1024;
        private static readonly Regex VersionPattern = new Regex(@"\Av?(\d{1,4})\.(\d{1,4})\.(\d{1,4})(?:\.(\d{1,4}))?\z", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);
        public static string InstallResultPath { get { return Path.Combine(SyncService.AppConfigDir, "updates", "last-install-result.txt"); } }
        public static string TakeInstallResult()
        { try { if (!File.Exists(InstallResultPath)) return ""; string value = File.ReadAllText(InstallResultPath); File.Delete(InstallResultPath); return value; } catch { return ""; } }

        public static Version ParseVersion(string text)
        {
            Match match = VersionPattern.Match(text ?? "");
            if (!match.Success) throw new InvalidDataException("版本号格式无效。");
            return new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0);
        }
        public static bool IsNewerVersion(string candidate, string current) { return ParseVersion(candidate).CompareTo(ParseVersion(current)) > 0; }

        public static bool IsAllowedAssetUrl(string url, string tag, string fileName)
        {
            if (fileName != "TokLens.exe" && fileName != "SHA256SUMS.txt") return false;
            try { ParseVersion(tag); } catch { return false; }
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !SafeHttps(uri) || uri.Host != "github.com") return false;
            return uri.AbsolutePath == "/" + Repository + "/releases/download/" + tag + "/" + fileName && uri.Query.Length == 0 && uri.Fragment.Length == 0;
        }
        public static bool IsAllowedRedirectUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !SafeHttps(uri) || uri.Fragment.Length != 0) return false;
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com" || uri.Host == "github-releases.githubusercontent.com";
        }
        private static bool SafeHttps(Uri uri) { return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0; }

        public static AppUpdateRelease Check(string currentVersion, CancellationToken token)
        {
            ParseVersion(currentVersion);
            string json = ReadText(LatestReleaseApi, 2 * 1024 * 1024, false, token);
            return ParseRelease(json, currentVersion);
        }
        public static AppUpdateRelease ParseRelease(string json, string currentVersion)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("GitHub 版本响应无效。");
            if (Bool(root, "draft") || Bool(root, "prerelease")) throw new InvalidDataException("稳定更新通道不安装草稿或预览版。");
            AppUpdateRelease release = new AppUpdateRelease(); release.Tag = Value(root, "tag_name");
            Version version = ParseVersion(release.Tag); release.Version = version.Revision == 0 ? version.ToString(3) : version.ToString(4);
            release.IsNewer = IsNewerVersion(release.Version, currentVersion); release.Name = Value(root, "name"); release.Notes = Value(root, "body");
            release.ReleaseUrl = "https://github.com/" + Repository + "/releases/tag/" + release.Tag;
            object rawAssets; IEnumerable assets = root.TryGetValue("assets", out rawAssets) ? rawAssets as IEnumerable : null;
            if (assets != null) foreach (object raw in assets)
            {
                Dictionary<string, object> asset = raw as Dictionary<string, object>; if (asset == null) continue;
                string name = Value(asset, "name"), url = Value(asset, "browser_download_url");
                if (name != "TokLens.exe" && name != "SHA256SUMS.txt") continue;
                if (!IsAllowedAssetUrl(url, release.Tag, name)) throw new InvalidDataException("更新资产不是此版本的官方 GitHub 下载地址。");
                if (name == "TokLens.exe")
                {
                    if (release.ExecutableUrl.Length > 0) throw new InvalidDataException("版本包含重复的可执行文件资产。");
                    release.ExecutableUrl = url; long size; if (!long.TryParse(Value(asset, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out size) || size <= 0 || size > MaximumExecutableBytes) throw new InvalidDataException("更新文件大小无效或超过 128 MB。"); release.ExecutableBytes = size;
                }
                else { if (release.ChecksumsUrl.Length > 0) throw new InvalidDataException("版本包含重复的校验文件资产。"); release.ChecksumsUrl = url; }
            }
            if (release.ExecutableUrl.Length == 0) release.UnavailableReason = "这个版本没有提供 TokLens.exe 更新资产。";
            else if (release.ChecksumsUrl.Length == 0) release.UnavailableReason = "这个版本没有提供 SHA256SUMS.txt，不能在应用内校验安装。可打开发布页手动更新。";
            return release;
        }

        public static string ExpectedHash(string checksums)
        {
            string result = null;
            foreach (string raw in (checksums ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match line = Regex.Match(raw, @"^([a-fA-F0-9]{64})[ \t]+\*?TokLens\.exe[ \t]*$", RegexOptions.CultureInvariant);
                if (!line.Success) continue;
                if (result != null) throw new InvalidDataException("校验文件包含重复的 TokLens.exe 条目。");
                result = line.Groups[1].Value.ToLowerInvariant();
            }
            if (result == null) throw new InvalidDataException("校验文件缺少有效的 TokLens.exe SHA-256。");
            return result;
        }
        public static bool VerifyFileSha256(string filePath, string expected)
        {
            if (!HashPattern.IsMatch(expected ?? "")) return false;
            string actual;
            using (FileStream file = File.OpenRead(filePath)) using (SHA256 sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            int diff = 0; for (int i = 0; i < actual.Length; i++) diff |= actual[i] ^ char.ToLowerInvariant(expected[i]);
            return diff == 0;
        }
        public static AppUpdateDownload Download(AppUpdateRelease release, string currentVersion, CancellationToken token, Action<AppUpdateProgress> progress)
        {
            if (release == null || !release.CanInstall || !IsNewerVersion(release.Version, currentVersion)) throw new InvalidOperationException("没有可安装的新版本；不会降级或重装当前版本。");
            if (ParseVersion(release.Tag).CompareTo(ParseVersion(release.Version)) != 0) throw new InvalidDataException("发布标签与版本号不一致。");
            if (!IsAllowedAssetUrl(release.ExecutableUrl, release.Tag, "TokLens.exe") || !IsAllowedAssetUrl(release.ChecksumsUrl, release.Tag, "SHA256SUMS.txt")) throw new InvalidDataException("更新下载地址无效。");
            Report(progress, "下载校验清单", 0, release.ExecutableBytes);
            string expected = ExpectedHash(ReadText(release.ChecksumsUrl, 256 * 1024, true, token));
            string folder = Path.Combine(SyncService.AppConfigDir, "updates", release.Version, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string partial = Path.Combine(folder, "TokLens.exe.download"), final = Path.Combine(folder, "TokLens.exe");
            try
            {
                using (FileStream stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    Transfer(release.ExecutableUrl, stream, MaximumExecutableBytes, true, token, delegate(long received, long total) { Report(progress, "下载更新", received, release.ExecutableBytes); });
                token.ThrowIfCancellationRequested();
                if (new FileInfo(partial).Length != release.ExecutableBytes) throw new InvalidDataException("下载文件大小与版本清单不一致。");
                Report(progress, "校验 SHA-256", release.ExecutableBytes, release.ExecutableBytes);
                if (!VerifyFileSha256(partial, expected)) throw new InvalidDataException("SHA-256 校验失败，未安装更新。请重试或打开官方发布页。");
                using (FileStream stream = File.OpenRead(partial)) if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("下载内容不是 Windows 可执行文件。");
                string embeddedVersion = FileVersionInfo.GetVersionInfo(partial).FileVersion;
                if (ParseVersion(embeddedVersion).CompareTo(ParseVersion(release.Version)) != 0) throw new InvalidDataException("可执行文件内的版本号与发布标签不一致。");
                token.ThrowIfCancellationRequested(); File.Move(partial, final);
                return new AppUpdateDownload { FilePath = final, Sha256 = expected, Version = release.Version };
            }
            catch { if (File.Exists(partial)) File.Delete(partial); throw; }
        }

        public static void EnsureWritableInstallation(string executablePath)
        {
            string target = LocalExecutablePath(executablePath); string directory = Path.GetDirectoryName(target);
            if (!File.Exists(target)) throw new IOException("找不到当前运行的安装文件。");
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new IOException("当前程序文件为只读。请先移到可写目录，再从该副本运行并更新。");
            string probe = Path.Combine(directory, ".toklens-update-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { using (FileStream file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) file.WriteByte(0); }
            catch (Exception ex) { throw new IOException("当前安装目录不可写。请把 TokLens.exe 移到有写入权限的目录再更新，或从官方发布页手动安装；程序不会自动请求管理员权限。", ex); }
        }
        private static string LocalExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new InvalidDataException("更新目标必须是本机完整文件路径。");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (!Regex.IsMatch(root ?? "", @"\A[A-Za-z]:\\\z") || full.Substring(root.Length).IndexOf(':') >= 0) throw new InvalidDataException("不支持设备路径或备用数据流作为更新目标。");
            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新目标必须是 exe 文件。");
            return full;
        }
        public static void StartInstall(AppUpdateDownload download, string currentVersion, string executablePath, int parentId, long parentStartedTicks, string[] restartArgs)
        {
            if (download == null || !IsNewerVersion(download.Version, currentVersion)) throw new InvalidOperationException("拒绝安装相同或更旧的版本。");
            string target = LocalExecutablePath(executablePath); EnsureWritableInstallation(target);
            string staging = Path.GetFullPath(Path.Combine(SyncService.AppConfigDir, "updates")) + Path.DirectorySeparatorChar;
            string source = Path.GetFullPath(download.FilePath);
            if (!source.StartsWith(staging, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(source) != "TokLens.exe" || !VerifyFileSha256(source, download.Sha256)) throw new InvalidDataException("待安装文件路径或校验值无效。");
            string script = BuildInstallScript(source, target, download.Sha256, parentId, parentStartedTicks, restartArgs);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(source), "install-update.ps1"), script, new UTF8Encoding(true));
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo start = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(start); if (process == null) throw new IOException("无法启动更新安装助手；当前版本保持不变。"); process.Dispose();
        }
        public static string PowerShellLiteral(string value) { return "'" + (value ?? "").Replace("'", "''") + "'"; }
        public static string QuoteWindowsArgument(string value)
        {
            value = value ?? ""; StringBuilder result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }
        public static string BuildInstallScript(string downloadedPath, string executablePath, string hash, int parentId, long parentStartedTicks, string[] restartArgs)
        {
            string target = LocalExecutablePath(executablePath), source = LocalExecutablePath(downloadedPath);
            if (!HashPattern.IsMatch(hash ?? "") || parentId <= 0 || parentStartedTicks <= 0) throw new InvalidDataException("更新助手参数无效。");
            List<string> args = new List<string>(); if (restartArgs != null) foreach (string arg in restartArgs) args.Add(QuoteWindowsArgument(arg));
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("$target = " + PowerShellLiteral(target)); script.AppendLine("$source = " + PowerShellLiteral(source));
            script.AppendLine("$expectedHash = " + PowerShellLiteral(hash.ToLowerInvariant()));
            script.AppendLine("$parentId = " + parentId.ToString(CultureInfo.InvariantCulture)); script.AppendLine("$parentStart = " + parentStartedTicks.ToString(CultureInfo.InvariantCulture));
            script.AppendLine("$restartArguments = " + PowerShellLiteral(string.Join(" ", args.ToArray())));
            script.AppendLine("$resultFile = " + PowerShellLiteral(InstallResultPath));
            script.Append(@"
$directory = [System.IO.Path]::GetDirectoryName($target)
$log = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($source), 'install-result.txt')
$replacement = [System.IO.Path]::Combine($directory, '.TokLens.new-' + [Guid]::NewGuid().ToString('N') + '.exe')
$backup = [System.IO.Path]::Combine($directory, '.TokLens.backup-' + [Guid]::NewGuid().ToString('N') + '.exe')
$failed = [System.IO.Path]::Combine($directory, '.TokLens.failed-' + [Guid]::NewGuid().ToString('N') + '.exe')
$replaced = $false
$parentExited = $false
function Start-TokLens {
    $launch = [System.Activator]::CreateInstance([System.Diagnostics.ProcessStartInfo])
    $launch.FileName = $target
    $launch.WorkingDirectory = $directory
    $launch.Arguments = $restartArguments
    $launch.UseShellExecute = $false
    return [System.Diagnostics.Process]::Start($launch)
}
function Write-UpdateResult([string]$kind, [string]$message) {
    try { [System.IO.File]::WriteAllText($log, $message) } catch { }
    try { [System.IO.File]::WriteAllText($resultFile, $kind + '|' + $message) } catch { }
}
function Get-UpdateHash([string]$literalPath) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($literalPath)
    try { return [System.BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ($true) {
        try { $parent = [System.Diagnostics.Process]::GetProcessById($parentId) } catch { $parent = $null }
        if ($null -eq $parent) { break }
        if ($parent.StartTime.ToUniversalTime().Ticks -ne $parentStart) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'TokLens did not exit within 90 seconds. Update cancelled.' }
        [System.Threading.Thread]::Sleep(250)
    }
    $parentExited = $true
    if ((Get-UpdateHash $source) -ne $expectedHash) { throw 'Staged update checksum mismatch.' }
    [System.IO.File]::Copy($source, $replacement, $false)
    if ((Get-UpdateHash $replacement) -ne $expectedHash) { throw 'Copied update checksum mismatch.' }
    [System.IO.File]::Replace($replacement, $target, $backup, $true)
    $replaced = $true
    $child = Start-TokLens
    if ($null -eq $child) { throw 'Could not start the updated application.' }
    [System.Threading.Thread]::Sleep(3000)
    $child.Refresh()
    if ($child.HasExited) { throw 'Updated application exited before its startup check completed.' }
    $success = 'Update installed. Previous executable backup: ' + $backup
    Write-UpdateResult 'OK' $success
} catch {
    $failure = $_.Exception.Message
    if ($replaced -and [System.IO.File]::Exists($backup)) {
        try {
            [System.IO.File]::Replace($backup, $target, $failed, $true)
            [void](Start-TokLens)
            $failure = $failure + ' Previous version restored.'
        } catch { $failure = $failure + ' Rollback requires manual recovery from: ' + $backup + '. ' + $_.Exception.Message }
    }
    elseif ($parentExited -and [System.IO.File]::Exists($target)) {
        try { [void](Start-TokLens) } catch { $failure = $failure + ' Could not reopen the previous version.' }
    }
    Write-UpdateResult 'ERROR' $failure
} finally {
    try { if ([System.IO.File]::Exists($replacement)) { [System.IO.File]::Delete($replacement) } } catch { }
}
");
            return script.ToString();
        }

        private static string ReadText(string url, long maximum, bool asset, CancellationToken token)
        { using (MemoryStream stream = new MemoryStream()) { Transfer(url, stream, maximum, asset, token, null); return Encoding.UTF8.GetString(stream.ToArray()); } }
        private static void Transfer(string url, Stream output, long maximum, bool asset, CancellationToken token, Action<long, long> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Uri current = new Uri(url); DateTime deadline = DateTime.UtcNow.AddMinutes(asset ? 10 : 1);
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                token.ThrowIfCancellationRequested();
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(current); request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
                request.UserAgent = "TokLens-Updater"; request.Accept = asset ? "application/octet-stream" : "application/vnd.github+json";
                using (token.Register(delegate { request.Abort(); }))
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status <= 399)
                            {
                                Uri next = new Uri(current, response.Headers["Location"] ?? "");
                                if (!asset || !IsAllowedRedirectUrl(next.AbsoluteUri)) throw new InvalidDataException("更新服务器跳转到了未允许的地址。");
                                current = next; continue;
                            }
                            if (status != 200) throw new IOException("更新服务器返回 HTTP " + status + "。");
                            if (response.ContentLength > maximum) throw new InvalidDataException("更新响应超过允许的大小。");
                            byte[] buffer = new byte[65536]; long total = 0;
                            using (Stream input = response.GetResponseStream())
                            {
                                int count;
                                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested(); if (DateTime.UtcNow > deadline) throw new TimeoutException("更新下载超时，请稍后重试。");
                                    total += count; if (total > maximum) throw new InvalidDataException("更新文件超过允许的大小。");
                                    output.Write(buffer, 0, count); if (progress != null) progress(total, response.ContentLength);
                                }
                            }
                            return;
                        }
                    }
                    catch (WebException ex)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        HttpWebResponse error = ex.Response as HttpWebResponse;
                        if (error != null && error.StatusCode == HttpStatusCode.NotFound) throw new IOException("官方仓库还没有可用的稳定发布版本。", ex);
                        if (error != null && error.StatusCode == HttpStatusCode.Forbidden) throw new IOException("GitHub 暂时限制了更新检查频率，请稍后重试。", ex);
                        throw;
                    }
                    catch (IOException) { if (token.IsCancellationRequested) throw new OperationCanceledException(token); throw; }
                    catch (ObjectDisposedException) { if (token.IsCancellationRequested) throw new OperationCanceledException(token); throw; }
                }
            }
            throw new IOException("更新下载跳转次数过多。");
        }
        private static string Value(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static bool Bool(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value is bool && (bool)value; }
        private static void Report(Action<AppUpdateProgress> progress, string stage, long downloaded, long total) { if (progress != null) progress(new AppUpdateProgress { Stage = stage, Downloaded = downloaded, Total = total }); }
    }
}
