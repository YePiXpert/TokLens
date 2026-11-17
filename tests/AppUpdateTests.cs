using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using TokLens;

internal static class AppUpdateTests
{
    private static int checks;
    private static void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
    private static Dictionary<string, object> Obj(params object[] pairs) { Dictionary<string, object> obj = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) obj[(string)pairs[i]] = pairs[i + 1]; return obj; }
    private static string Release(string tag, bool hash, bool prerelease)
    {
        List<object> assets = new List<object>();
        assets.Add(Obj("name", "TokLens.exe", "browser_download_url", "https://github.com/YePiXpert/TokLens/releases/download/" + tag + "/TokLens.exe", "size", 12345));
        if (hash) assets.Add(Obj("name", "SHA256SUMS.txt", "browser_download_url", "https://github.com/YePiXpert/TokLens/releases/download/" + tag + "/SHA256SUMS.txt", "size", 100));
        return new JavaScriptSerializer().Serialize(Obj("tag_name", tag, "name", "fixture", "body", "release notes", "draft", false, "prerelease", prerelease, "assets", assets));
    }
    private static void VersionsAndUrls()
    {
        Check(AppUpdateService.IsNewerVersion("v2.10.0", "2.9.9"), "semantic version components are compared numerically");
        Check(!AppUpdateService.IsNewerVersion("v2.4.0", "2.4.0.0"), "equivalent three- and four-component versions are not reinstalled");
        Check(!AppUpdateService.IsNewerVersion("v2.3.9", "2.4.0"), "downgrades are rejected");
        foreach (string malformed in new[] { "v2.4.0;calc", "v2.4.0\n", "../../2.4.0", "v2.4.0-beta", "2.4.0$(x)", "999999.4.0", "2.4", " 2.4.0" }) Reject(delegate { AppUpdateService.ParseVersion(malformed); }, "reject malformed or unsafe version: " + malformed.Replace("\n", "\\n"));
        string official = "https://github.com/YePiXpert/TokLens/releases/download/v2.4.0/TokLens.exe";
        Check(AppUpdateService.IsAllowedAssetUrl(official, "v2.4.0", "TokLens.exe"), "official executable asset is allowed");
        foreach (string bad in new[] { official.Replace("https:", "http:"), official.Replace("github.com", "github.com.evil.test"), official.Replace("github.com/", "evil.test@github.com/"), official.Replace("v2.4.0", "v2.3.0"), official + "?redirect=evil", official.Replace("YePiXpert", "SomeoneElse"), official.Replace("TokLens.exe", "installer.ps1") }) Check(!AppUpdateService.IsAllowedAssetUrl(bad, "v2.4.0", "TokLens.exe"), "untrusted or mismatched asset URL rejected");
        Check(AppUpdateService.IsAllowedRedirectUrl("https://release-assets.githubusercontent.com/github-production-release-asset/file?sig=fixture"), "GitHub signed asset CDN redirects are allowed");
        Check(!AppUpdateService.IsAllowedRedirectUrl("https://release-assets.githubusercontent.com.evil.test/file") && !AppUpdateService.IsAllowedRedirectUrl("http://release-assets.githubusercontent.com/file"), "lookalike and insecure redirect hosts are rejected");
        AppUpdateRelease latest = AppUpdateService.ParseRelease(Release("v2.4.0", true, false), "2.3.0");
        Check(latest.IsNewer && latest.CanInstall && latest.Version == "2.4.0" && latest.Notes == "release notes", "release parser binds version notes and same-release assets");
        AppUpdateRelease legacy = AppUpdateService.ParseRelease(Release("v2.4.0", false, false), "2.3.0");
        Check(legacy.IsNewer && !legacy.CanInstall && legacy.UnavailableReason.Contains("SHA256SUMS"), "a legacy release without checksums can be viewed but not installed");
        Check(!AppUpdateService.ParseRelease(Release("v2.3.0", true, false), "2.4.0").CanInstall, "older GitHub stable release does not downgrade a newer local build");
        Reject(delegate { AppUpdateService.ParseRelease(Release("v2.5.0", true, true), "2.4.0"); }, "stable channel rejects prereleases");
        Reject(delegate { AppUpdateService.ParseRelease(Release("v2.4.0", true, false).Replace("/download/v2.4.0/TokLens.exe", "/download/v2.3.0/TokLens.exe"), "2.3.0"); }, "asset from another release is rejected");
    }
    private static void HashesAndStaging(string root)
    {
        string file = Path.Combine(root, "fixture.exe"); File.WriteAllBytes(file, Encoding.UTF8.GetBytes("MZ fixture update"));
        string hash; using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", "").ToLowerInvariant();
        Check(AppUpdateService.ExpectedHash(hash + "  TokLens.exe\n" + new string('0', 64) + "  other.zip") == hash, "checksum manifest chooses the exact executable name");
        Check(AppUpdateService.VerifyFileSha256(file, hash) && AppUpdateService.VerifyFileSha256(file, hash.ToUpperInvariant()), "download digest verification handles manifest hex case");
        Check(!AppUpdateService.VerifyFileSha256(file, new string('0', 64)), "wrong download digest blocks installation");
        Check(!AppUpdateService.VerifyFileSha256(file, hash + "\n"), "digest validation rejects trailing characters");
        Reject(delegate { AppUpdateService.ExpectedHash(hash + "  ../TokLens.exe"); }, "path traversal checksum entry is not an executable match");
        Reject(delegate { AppUpdateService.ExpectedHash(hash + "  TokLens.exe\n" + hash + "  TokLens.exe"); }, "ambiguous duplicate checksum entries are rejected");
        AppUpdateRelease old = AppUpdateService.ParseRelease(Release("v2.3.0", true, false), "2.4.0");
        Reject(delegate { AppUpdateService.Download(old, "2.4.0", CancellationToken.None, null); }, "download rejects downgrade before any network request");
        AppUpdateService.EnsureWritableInstallation(file); Check(true, "writable portable install directory is supported without elevation");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try { Reject(delegate { AppUpdateService.EnsureWritableInstallation(file); }, "read-only install target produces an actionable failure"); }
        finally { File.SetAttributes(file, FileAttributes.Normal); }
        Reject(delegate { AppUpdateService.EnsureWritableInstallation(@"\\server\share\TokLens.exe"); }, "network installation paths are not mutated");
    }
    private static void Helper(string root)
    {
        string source = Path.Combine(root, "O'Brien $ data", "TokLens.exe"), target = @"C:\Apps\O'Brien Tok Lens\TokLens.exe", hash = new string('a', 64);
        string script = AppUpdateService.BuildInstallScript(source, target, hash, 12345, 638000000000000000L, new[] { "--home", @"C:\Users\O'Brien\space $(text)\", "quote\"arg" });
        Check(script.Contains("O''Brien") && script.Contains("[System.IO.File]::OpenRead($literalPath)") && script.Contains("[System.IO.File]::Replace($replacement, $target, $backup"), "helper quotes apostrophes, verifies staged bytes without module dependencies and swaps on the target volume");
        Check(script.Contains("$parent.StartTime.ToUniversalTime().Ticks -ne $parentStart") && script.Contains("AddSeconds(90)"), "helper bounds its wait and avoids PID reuse");
        Check(script.Contains("[System.IO.File]::Replace($backup, $target") && script.Contains("$parentExited"), "failed replacement or failed start restores or reopens the old executable");
        Check(!script.Contains("Remove-Item") && !script.Contains("Invoke-Expression") && !script.Contains("config.json") && !script.Contains("sqlite"), "installer never recursively removes files or touches user data");
        Check(AppUpdateService.PowerShellLiteral("a'b$(x)") == "'a''b$(x)'", "shell variable-like text remains a literal string");
        Check(AppUpdateService.QuoteWindowsArgument(@"C:\with space\") == "\"C:\\with space\\\\\"", "Windows restart arguments preserve trailing backslashes");
        Reject(delegate { AppUpdateService.BuildInstallScript(source, "C:\\Apps\\evil\nTokLens.exe", hash, 1, 1, null); }, "newline injection is rejected in installer paths");
        Reject(delegate { AppUpdateService.BuildInstallScript(source, "C:\\Apps\\TokLens.exe:evil.exe", hash, 1, 1, null); }, "alternate data stream targets are rejected");
        Reject(delegate { AppUpdateService.BuildInstallScript(source, target, hash + "\n", 1, 1, null); }, "malformed installer hash is rejected");
        string scriptPath = Path.Combine(root, "helper-parse-fixture.ps1"); File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        Console.WriteLine("Parse-only helper fixture: " + scriptPath);
    }
    private static string FileHash(string path)
    { using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void RunHidden(string executable, string arguments, int timeout)
    {
        ProcessStartInfo info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        using (Process process = Process.Start(info))
        {
            if (!process.WaitForExit(timeout)) { process.Kill(); throw new Exception("isolated update fixture timed out"); }
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (process.ExitCode != 0) throw new Exception("isolated update fixture failed: " + output);
        }
    }
    private static string CompileChild(string folder, string label, bool remainsOpen)
    {
        Directory.CreateDirectory(folder); string code = Path.Combine(folder, "child.cs"), exe = Path.Combine(folder, "TokLens.exe");
        File.WriteAllText(code, "class UpdateFixture { static int Main(string[] args) { System.IO.File.WriteAllText(args[0], \"" + label + "|\" + args[1]); " + (remainsOpen ? "System.Threading.Thread.Sleep(5000); " : "") + "return 0; } }");
        string compiler = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), "csc.exe");
        RunHidden(compiler, "/nologo /target:winexe /out:" + AppUpdateService.QuoteWindowsArgument(exe) + " " + AppUpdateService.QuoteWindowsArgument(code), 15000);
        return exe;
    }
    private static void HelperTransactions(string root)
    {
        // These are tiny test binaries in a fresh temp directory, never the installed TokLens executable.
        string old = CompileChild(Path.Combine(root, "fixture old"), "old", false);
        string good = CompileChild(Path.Combine(root, "fixture good"), "new", true);
        string early = CompileChild(Path.Combine(root, "fixture early exit"), "early", false);
        Directory.CreateDirectory(Path.GetDirectoryName(AppUpdateService.InstallResultPath));
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        string quotedArgument = "space O'Brien $(literal) quote\" and trailing\\";
        for (int scenario = 0; scenario < 3; scenario++)
        {
            string installDir = Path.Combine(root, "isolated install O'Brien " + scenario); Directory.CreateDirectory(installDir);
            string target = Path.Combine(installDir, "TokLens.exe"), marker = Path.Combine(installDir, "started marker.txt"); File.Copy(old, target);
            Check(Path.GetFullPath(target).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "transaction target remains inside its fresh test directory");
            string source = scenario == 2 ? early : good; string expected = scenario == 1 ? new string('0', 64) : FileHash(source);
            string script = AppUpdateService.BuildInstallScript(source, target, expected, int.MaxValue, 1, new[] { marker, quotedArgument });
            if (scenario == 0) script = "$PSModuleAutoLoadingPreference = 'None'\n" + script;
            RunHidden(powershell, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), 20000);
            string result = AppUpdateService.TakeInstallResult();
            if (scenario == 0)
            {
                Check(result.StartsWith("OK|", StringComparison.Ordinal) && FileHash(target) == FileHash(good), "real helper installs the verified fixture atomically");
                string[] backups = Directory.GetFiles(installDir, ".TokLens.backup-*.exe");
                Check(backups.Length == 1 && FileHash(backups[0]) == FileHash(old), "successful helper keeps an exact previous executable backup");
            }
            else
            {
                Check(result.StartsWith("ERROR|", StringComparison.Ordinal) && FileHash(target) == FileHash(old), scenario == 1 ? "bad staged hash preserves and reopens the original fixture" : "an early zero exit rolls back to the original fixture");
            }
            Check(SpinWait.SpinUntil(delegate { return File.Exists(marker) && File.ReadAllText(marker).StartsWith(scenario == 0 ? "new|" : "old|", StringComparison.Ordinal); }, 3000), "helper starts the expected new or restored fixture");
            Check(File.ReadAllText(marker) == (scenario == 0 ? "new|" : "old|") + quotedArgument, "restart arguments containing spaces, apostrophes, quotes and dollar syntax round-trip literally");
            Check(Directory.GetFiles(installDir, ".TokLens.new-*.exe").Length == 0, "transaction leaves no partially swapped temporary executable");
        }
    }
    private static void Preferences()
    {
        DateTime now = DateTime.UtcNow; AppUpdatePreferences prefs = AppUpdatePreferences.Load();
        Check(prefs.Enabled && prefs.IsDue(now), "daily version checks default on without initiating a download");
        prefs.LastSuccessfulCheckUtc = now.ToString("o"); prefs.Save(); prefs = AppUpdatePreferences.Load();
        Check(!prefs.IsDue(now.AddHours(23)) && prefs.IsDue(now.AddHours(24)), "successful checks are throttled for 24 hours");
        prefs.Enabled = false; prefs.Save();
        Check(!AppUpdatePreferences.Load().IsDue(now.AddDays(2)), "disabled automatic checks remain disabled after restart");
    }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "TokLens_UpdateTests_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "config"));
        try { VersionsAndUrls(); HashesAndStaging(root); Helper(root); Preferences(); HelperTransactions(root); Console.WriteLine("App updates: " + checks + " checks passed"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
