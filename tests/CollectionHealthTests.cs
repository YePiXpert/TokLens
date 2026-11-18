using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using TokLens;

// What the overview's collection strip is allowed to say, and where its facts come from:
// the delivered tier Anthropic reports in message.usage, one health row per real tool, and
// a first-launch window that leaves room on the screen.
internal static class CollectionHealthTests
{
    private static int checks;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static void ResetStore() { typeof(LocalUsageStore).GetField("_memCursors", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null); }
    private static Dictionary<string, object> O(params object[] pairs)
    { Dictionary<string, object> o = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) o[(string)pairs[i]] = pairs[i + 1]; return o; }
    private static string Write(string path, string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private static ProviderHealth Row(ScanResult scan, string tool)
    { foreach (ProviderHealth h in scan.ProviderHealth) if (h.Tool == tool) return h; return null; }
    private static bool Listed(ScanResult scan, string tool)
    { ProviderHealth h = Row(scan, tool); return h != null && h.Status != "not_installed"; }
    private static RecentRequestRecord ByOutput(ScanResult scan, long output)
    { foreach (RecentRequestRecord r in scan.RecentRequests) if (r.Tool.StartsWith("Claude", StringComparison.Ordinal) && r.Output == output) return r; throw new Exception("missing Claude request with output " + output); }

    // A home directory holding one log per situation the collection list has to tell apart.
    private static string BuildHome(string root)
    {
        string home = Path.Combine(root, "home");
        string fixture = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "tests", "fixtures", "claude_usage_tiers.jsonl"));
        Write(Path.Combine(home, ".claude", "projects", "C--desktop-project", "desktop-session.jsonl"), fixture);
        Write(Path.Combine(home, ".claude", "projects", "C--cli-project", "cli-session.jsonl"),
            Json.Serialize(O("type", "assistant", "timestamp", "2026-11-17T12:10:05.000Z", "entrypoint", "cli", "requestId", "req_cli",
                "message", O("id", "msg_cli", "model", "claude-opus-5-5", "role", "assistant",
                    "usage", O("input_tokens", 100, "output_tokens", 30, "service_tier", "standard", "speed", "standard")))) + "\n");
        // A directory another tool shares, with no log of this tool in it.
        Directory.CreateDirectory(Path.Combine(home, ".gemini"));
        Directory.CreateDirectory(Path.Combine(home, ".toklens"));
        Directory.CreateDirectory(Path.Combine(home, ".commandcode"));
        // Logs that exist but hold no usage.
        Write(Path.Combine(home, ".dsh", "storages", "session-empty.json"), "{\"record\":{\"rows\":[]}}");
        // A scanner whose label differs from the tool its rows are filed under.
        Write(Path.Combine(home, ".workbuddy", "projects", "demo", "session.jsonl"),
            "{\"model\":\"glm-5.3\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2},\"timestamp\":\"2026-11-17T12:20:00Z\"}\n");
        return home;
    }

    private static void ClaudeDeliveredTier(string root)
    {
        string home = BuildHome(root);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "store")); ResetStore();
        ScanResult scan = new TokenScanner(home, false).Run();

        RecentRequestRecord standard = ByOutput(scan, 20), fast = ByOutput(scan, 21), priority = ByOutput(scan, 22), absent = ByOutput(scan, 23), tierOnly = ByOutput(scan, 24), batch = ByOutput(scan, 25);
        Check(standard.ActualServiceTier == "standard" && standard.TierEvidence == "response:usage.service_tier", "message.usage.service_tier=standard is the delivered tier, with its log field as evidence");
        Check(fast.ActualServiceTier == "fast" && fast.TierEvidence == "response:usage.speed", "message.usage.speed=fast is Fast even though service_tier says standard");
        Check(priority.ActualServiceTier == "unknown" && UsagePresentation.TierLabel(priority) == "档位未知", "Anthropic priority capacity is never relabelled as Fast through the OpenAI alias");
        Check(absent.ActualServiceTier == "unknown" && batch.ActualServiceTier == "unknown", "missing or unrecognised usage fields stay unknown");
        Check(tierOnly.ActualServiceTier == "standard", "older logs with service_tier but no speed field are still Standard");
        Check(standard.RequestedServiceTier == "unknown" && fast.RequestedServiceTier == "unknown", "a response field never invents a request preference");
        Check(standard.Provider == "anthropic" && standard.Tool == "Claude Desktop", "Claude rows fall back to the model's vendor, as the Codex parser and the probe do");
        // Same numbers as tests/test_usage_accounting.py asserts for the Python probe.
        Check(Math.Abs(standard.CostUsd - (100 * 4 + 20 * 20 + 1000 * 0.2 + 50 * 5) / 1e6) < 1e-9 && Math.Abs(fast.CostUsd - (100 * 8 + 21 * 40 + 1000 * 0.4 + 50 * 10) / 1e6) < 1e-9, "the Fast request is priced with the Fast rule, the Standard one with the Standard rule: " + standard.CostUsd + " / " + fast.CostUsd);
        Check(fast.CostBasis == "estimated_tier_price" && standard.CostBasis == "estimated_tier_price", "both are quoted from a rule for their own delivered tier");
        Check(UsagePresentation.CostLabel(standard).IndexOf("档位未知", StringComparison.Ordinal) < 0 && UsagePresentation.CostLabel(standard).IndexOf("渠道未报价", StringComparison.Ordinal) < 0, "a confirmed Standard request is no longer labelled as an unknown tier on an unquoted channel");
        Check(ByOutput(scan, 30).Tool == "Claude Code" && ByOutput(scan, 30).ActualServiceTier == "standard", "the CLI session keeps its own tool name and tier");

        long unknown = 0, known = 0;
        foreach (RecordBucket r in scan.Records) if (r.Tool.StartsWith("Claude", StringComparison.Ordinal)) { if (UsageServiceTier.Effective(r) == "unknown") unknown += r.Turns; else known += r.Turns; }
        Check(known == 4 && unknown == 3, "daily buckets carry the same split: 4 with a delivered tier, 3 without evidence");
    }

    private static void OneRowPerRealTool(string root)
    {
        string home = Path.Combine(root, "home");
        ResetStore();
        ScanResult scan = new TokenScanner(home, false).Run();

        ProviderHealth desktop = Row(scan, "Claude Desktop"), cli = Row(scan, "Claude Code");
        Check(desktop != null && desktop.Status == "ok" && desktop.FilesFound == 1 && desktop.ParsedRequests == 6 && desktop.Source == "Claude Code", "the desktop session's file and requests are reported under Claude Desktop, naming the scanner they came from");
        Check(cli != null && cli.Status == "ok" && cli.FilesFound == 1 && cli.ParsedRequests == 1, "the CLI session's file is counted once, under Claude Code");
        Check(desktop.LastRequestMs > 0 && desktop.LastSuccessMs > 0, "a tool with usage reports when it was last read and last used");
        Check(!Listed(scan, "Gemini CLI") && !Listed(scan, "Custom Harness") && !Listed(scan, "Command Code") && !Listed(scan, "Google Antigravity"), "an existing directory without a single log file is not a discovered log");
        ProviderHealth dsh = Row(scan, "DeepSeek (DSH)");
        Check(dsh != null && dsh.Status == "no_data" && dsh.FilesFound == 1 && dsh.ParsedRequests == 0, "log files without usage are listed as such, under the tool's own name");
        ProviderHealth buddy = Row(scan, "WorkBuddy");
        Check(buddy != null && buddy.Status == "ok" && buddy.FilesFound == 1 && !Listed(scan, "WorkBuddy / CodeBuddy"), "a scanner label is folded into the tool it produced instead of being listed beside it");
        Check(!Listed(scan, "Codex archive"), "internal scanner passes never appear as tools");
        int listed = 0; foreach (ProviderHealth h in scan.ProviderHealth) if (h.Status != "not_installed") listed++;
        Check(listed == 4, "exactly the four tools with log files are listed, found " + listed);

        ScanResult cached = LocalUsageStore.TryLoadInstantSnapshot(home);
        ProviderHealth restored = cached == null ? null : Row(cached, "Claude Desktop");
        Check(restored != null && restored.Source == "Claude Code" && restored.FilesFound == 1 && restored.LastRequestMs == desktop.LastRequestMs, "the instant snapshot keeps the per-tool rows");

        // A failing source stays visible, and is not folded away with its label.
        File.AppendAllText(Path.Combine(home, ".workbuddy", "projects", "demo", "session.jsonl"), "{\"usage\": broken}\n");
        scan = new TokenScanner(home, false).Run();
        Check(Row(scan, "WorkBuddy").Status == "error" && Row(scan, "WorkBuddy").SkippedRecords > 0, "an unreadable record surfaces as an error on the tool it belongs to");
        File.WriteAllText(Path.Combine(home, ".workbuddy", "projects", "demo", "session.jsonl"), "{\"model\":\"glm-5.3\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2},\"timestamp\":\"2026-11-17T12:20:00Z\"}\n");
    }

    // An existing ledger written by the previous parser has every Claude request cached as
    // unknown. Upgrading must re-read the logs that still exist, not append to the old rows.
    private static void UpgradeRereadsClaudeLogs(string root)
    {
        string home = Path.Combine(root, "home");
        ResetStore();
        using (WinSqlite db = WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath))
        {
            db.ExecRequired("UPDATE file_cursors SET events_data=REPLACE(REPLACE(REPLACE(events_data,'\"ActualServiceTier\":\"standard\"','\"ActualServiceTier\":\"unknown\"'),'\"ActualServiceTier\":\"fast\"','\"ActualServiceTier\":\"unknown\"'),'\"Provider\":\"anthropic\"','\"Provider\":\"unknown\"') WHERE file_key LIKE '%.claude%';");
        }
        ResetStore();
        ScanResult old = new TokenScanner(home, false).Run();
        long oldKnown = 0, oldTurns = 0;
        foreach (RecordBucket r in old.Records) if (r.Tool.StartsWith("Claude", StringComparison.Ordinal)) { oldTurns += r.Turns; if (UsageServiceTier.Effective(r) != "unknown") oldKnown += r.Turns; }
        Check(oldTurns == 7 && oldKnown == 0, "precondition: a ledger cached by the previous parser has no Claude tier at all");

        using (WinSqlite db = WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath))
            db.ExecRequired("UPDATE kv_meta SET v='toklens.store.v2.12' WHERE k='schema';");
        ResetStore();
        ScanResult upgraded = new TokenScanner(home, false).Run();
        long turns = 0, standard = 0, fast = 0, unknown = 0;
        foreach (RecordBucket r in upgraded.Records)
        {
            if (!r.Tool.StartsWith("Claude", StringComparison.Ordinal)) continue;
            turns += r.Turns; string tier = UsageServiceTier.Effective(r);
            if (tier == "standard") standard += r.Turns; else if (tier == "fast") fast += r.Turns; else unknown += r.Turns;
        }
        Check(turns == 7, "the upgrade replaces the cached rows instead of adding to them, found " + turns + " requests");
        Check(standard == 3 && fast == 1 && unknown == 3, "existing logs are re-read with their delivered tier after the upgrade");
        Check(Directory.GetFiles(Path.GetDirectoryName(LocalUsageStore.DbFilePath), "*" + LocalUsageStore.UpgradeBackupPrefix + "*.db").Length == 1, "the ledger is backed up before cursors are reset");
        Check(UsageAccounting.Revision >= 33, "the accounting revision advanced with the new evidence, so older summaries cannot undo it");
    }

    private static string Text(DependencyObject root)
    {
        StringBuilder sb = new StringBuilder();
        Collect(root, sb); return sb.ToString();
    }
    private static void Collect(DependencyObject node, StringBuilder sb)
    {
        TextBlock text = node as TextBlock;
        if (text != null) sb.Append(text.Text).Append('\n');
        foreach (object child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject) Collect((DependencyObject)child, sb);
    }

    private static void StripSaysOnlyWhatItKnows()
    {
        RecordBucket confirmed = new RecordBucket { Date = "2026-11-17", Tool = "Claude Desktop", Model = "claude-opus-5-5", Provider = "anthropic", ActualServiceTier = "standard", TierEvidence = "response:usage.service_tier", CostBasis = "estimated_tier_price", Turns = 204, FreshInput = 1000, CostUsd = 18.29 };
        RecordBucket requested = new RecordBucket { Date = "2026-11-17", Tool = "OpenAI Codex", Model = "gpt-6-astra", Provider = "custom", RequestedServiceTier = "fast", TierEvidence = "thread_settings:requested", CostBasis = "estimated_official_reference", Turns = 12, FreshInput = 1000, CostUsd = 3 };
        RecordBucket unknown = new RecordBucket { Date = "2026-11-17", Tool = "Kimi Code", Model = "kimi-k3", CostBasis = "estimated_unknown_tier", Turns = 5, FreshInput = 1000, CostUsd = 1 };

        UsageStatusPanel strip = new UsageStatusPanel(null, null);
        Check(strip.SummaryText.Contains("正在发现"), "before the first scan the strip says it is still looking");

        ScanResult empty = new ScanResult();
        strip.Render(empty, new RecordBucket[0], false, 7, "");
        Check(strip.SummaryText.Contains("尚未发现可读取的用量") && !strip.SummaryText.Contains("读取正常"), "with nothing found the strip explains what to do instead of counting tools");

        ScanResult healthy = new ScanResult { AccountingUpdatedMs = UsageAccounting.UtcMilliseconds() };
        healthy.ProviderHealth.Add(new ProviderHealth { Tool = "Claude Desktop", Status = "ok", FilesFound = 34, ParsedRequests = 4144, Source = "Claude Code", LastRequestMs = healthy.AccountingUpdatedMs });
        healthy.ProviderHealth.Add(new ProviderHealth { Tool = "OpenAI Codex", Status = "ok", FilesFound = 59, ParsedRequests = 3970 });
        healthy.ProviderHealth.Add(new ProviderHealth { Tool = "DeepSeek (DSH)", Status = "no_data", FilesFound = 9 });
        healthy.ProviderHealth.Add(new ProviderHealth { Tool = "Gemini CLI", Status = "not_installed" });
        strip.Render(healthy, new[] { confirmed, requested, unknown }, false, 7, "");
        Check(strip.SummaryText.Contains("2 个工具读取正常") && strip.SummaryText.Contains("1 个暂无用量"), "tools are counted by what was read: two healthy, one with logs but no usage, none for a tool that is not installed");
        Check(strip.SummaryText.Contains("Standard 204") && strip.SummaryText.Contains("Fast（已请求） 12") && strip.SummaryText.Contains("档位未知 5"), "the tier summary keeps confirmed, requested-only and unknown apart");
        Check(strip.SummaryText.IndexOf("Fast（已请求）", StringComparison.Ordinal) < strip.SummaryText.IndexOf("Standard 204", StringComparison.Ordinal), "the tier that costs extra is listed first");

        string details = Text(strip.BuildDetails(700));
        Check(details.Contains("Claude Desktop") && details.Contains("4,144 次") && details.Contains("34 个文件"), "details list each tool with what was read from it");
        Check(details.Contains("DeepSeek (DSH)") && details.Contains("未解析出用量") && !details.Contains("Gemini CLI"), "details separate empty logs from tools that are not there at all");
        Check(details.Contains("221 次请求") && details.Contains("API 估值"), "details total the filtered range and name each amount's basis");

        healthy.ProviderHealth.Add(new ProviderHealth { Tool = "Kimi Code", Status = "error", Error = "读取日志失败: IOException", SkippedRecords = 2 });
        strip.Render(healthy, new[] { confirmed }, false, 7, "");
        Check(strip.SummaryText.Contains("1 个来源读取异常"), "a failing source is the headline");
        Check(Text(strip.BuildDetails(700)).Contains("读取日志失败: IOException"), "and its reason is shown in the details");

        healthy.ProviderHealth.RemoveAt(healthy.ProviderHealth.Count - 1);
        strip.Render(healthy, new[] { confirmed }, false, 7, "IOException: ledger locked");
        Check(strip.SummaryText.Contains("本次扫描失败") && Text(strip.BuildDetails(700)).Contains("ledger locked"), "a failed scan is reported without discarding the previous result");

        foreach (ProviderHealth h in healthy.ProviderHealth) if (h.Status == "ok") h.Status = "stale";
        strip.Render(healthy, new[] { confirmed }, false, 7, "");
        Check(strip.SummaryText.Contains("正在刷新") && !strip.SummaryText.Contains("异常"), "a snapshot waiting for its first rescan is refreshing, not broken");

        strip.Render(healthy, new[] { unknown }, false, 7, "");
        Check(strip.SummaryText.Contains("档位未知 5") && !strip.SummaryText.Contains("Fast"), "no tier evidence is never presented as zero Fast requests");

        Check(confirmed.ServiceTierKind == "standard" && requested.ServiceTierKind == "fast-requested" && unknown.ServiceTierKind == "unknown", "table badges distinguish confirmed, requested-only and unknown tiers");
        Check(requested.ServiceTierHint.Contains("实际执行档位未核实") && requested.ServiceTierHint.Contains("thread_settings:requested") && requested.ServiceTierHint.Contains(requested.CostBasisLabel), "the badge tooltip carries the evidence and the full amount basis");
        Check(confirmed.CostBasisShortLabel == "API 估值" && requested.CostBasisShortLabel == "官方参考" && unknown.CostBasisShortLabel == "标准参考", "short basis labels tell otherwise identical rows apart");
        string stored = UsageServiceTier.Serialize(requested);
        Check(!stored.Contains("ServiceTierKind") && !stored.Contains("ServiceTierHint") && !stored.Contains("CostBasisShortLabel"), "presentation helpers are not written into stored metadata");
    }

    private static void WindowLeavesRoom(string root)
    {
        Size laptop = WindowPlacementStore.DefaultSize(new Rect(0, 0, 1536, 816));
        Check(laptop.Width == 1167 && laptop.Height == 685, "on a 1080p laptop at 125% the first window is 1167x685, not 1400x734: " + laptop);
        Size desktop = WindowPlacementStore.DefaultSize(new Rect(0, 0, 2560, 1400));
        Check(desktop.Width == 1180 && desktop.Height == 760, "large screens get a fixed comfortable size rather than a share of the screen");
        Size tiny = WindowPlacementStore.DefaultSize(new Rect(0, 0, 800, 560));
        Check(tiny.Width == WindowPlacementStore.MinWidth && tiny.Height == WindowPlacementStore.MinHeight, "never smaller than the layout's minimum");

        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "window-store"));
        Size saved; bool maximized;
        Check(!WindowPlacementStore.TryLoad(out saved, out maximized), "nothing is remembered before the window was ever closed");
        WindowPlacementStore.Save(new Size(1024.4, 640.6), true);
        Check(WindowPlacementStore.TryLoad(out saved, out maximized) && saved.Width == 1024 && saved.Height == 641 && maximized, "the last size and maximised state are remembered");
        Check(WindowPlacementStore.Clamp(new Size(5000, 5000), new Rect(0, 0, 1536, 816)) == new Size(1536, 816), "a size saved on a larger screen is clamped to the current work area");
        File.WriteAllText(Path.Combine(root, "window-store", "window.json"), "{ not json");
        Check(!WindowPlacementStore.TryLoad(out saved, out maximized), "a damaged file falls back to the default size");
    }

    [STAThread]
    public static int Main()
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        string root = Path.Combine(Path.GetTempPath(), "TokLens_CollectionTests_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            ClaudeDeliveredTier(root);
            OneRowPerRealTool(root);
            UpgradeRereadsClaudeLogs(root);
            StripSaysOnlyWhatItKnows();
            WindowLeavesRoom(root);
            Console.WriteLine("PASS: " + checks + " collection health assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
