using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using TokLens;

internal static class ForkAccountingTests
{
    private static int _checks;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static CodexUsageParser Parse(string path, bool resume)
    {
        CodexUsageParser p = new CodexUsageParser("OpenAI Codex", "", new DateTime(2026, 11, 1), "", null);
        int line = 0;
        foreach (string text in File.ReadAllLines(path))
        {
            p.OnLine(text, line++);
            if (resume) p = new CodexUsageParser("OpenAI Codex", p.ThreadId, DateTime.UtcNow, p.SerializeState(), p.Events);
        }
        return p;
    }
    private static List<UsageEvent> Dedup(List<UsageEvent> source, params string[] metadata)
    {
        TokenScanner scanner = new TokenScanner(Path.GetTempPath(), false);
        List<UsageEvent> events = (List<UsageEvent>)typeof(TokenScanner).GetField("_events", PrivateInstance).GetValue(scanner);
        foreach (UsageEvent ev in source) events.Add(ev.Clone());
        Dictionary<string, FileCursorEntry> cursors = new Dictionary<string, FileCursorEntry>();
        for (int i = 0; i < metadata.Length; i++) cursors[i.ToString()] = new FileCursorEntry { ExtraMeta = metadata[i] };
        typeof(TokenScanner).GetField("_cursors", PrivateInstance).SetValue(scanner, cursors);
        typeof(TokenScanner).GetMethod("DeduplicateRequests", PrivateInstance).Invoke(scanner, null);
        return events;
    }
    private static string Meta(string child, string parent, long fork)
    {
        return Json.Serialize(new Dictionary<string, object> { { "tid", child }, { "parent", parent }, { "fork_ms", fork } });
    }
    private static UsageEvent Ev(string session, string key, long ms, long input)
    {
        return new UsageEvent { Tool = "OpenAI Codex", Model = "gpt-6-astra", SessionId = session,
            DedupKey = key, TimestampMs = ms, Date = "2026-11-01", FreshInput = input,
            Output = 10, CacheRead = 20, CacheWrite = 5, Turns = 1 };
    }
    private static long Input(IEnumerable<UsageEvent> events)
    {
        long result = 0;
        foreach (UsageEvent ev in events) result += ev.FreshInput + ev.CacheRead + ev.CacheWrite;
        return result;
    }
    private static void SharedFixture()
    {
        // Reproduces the timestamp-rewritten rollout shape from ccusage #2046.
        // Source: https://github.com/ccusage/ccusage/blob/58866ba681343634ab6e5add61f18f3b70aa98f7/rust/adapters/codex/src/aggregate.rs#L1927-L1936
        foreach (bool resume in new bool[] { false, true })
        {
            CodexUsageParser parent = Parse("tests/fixtures/codex_fork_parent.jsonl", resume);
            CodexUsageParser child = Parse("tests/fixtures/codex_fork_child.jsonl", resume);
            List<UsageEvent> rows = new List<UsageEvent>(parent.Events); rows.AddRange(child.Events);
            List<UsageEvent> result = Dedup(rows, parent.SerializeState(), child.SerializeState());
            Check(result.Count == 3 && Input(result) == 1010000, "fork history is counted once while both identical native requests survive, resume=" + resume);
            long output = 0, read = 0; int native = 0;
            foreach (UsageEvent ev in result) { output += ev.Output; read += ev.CacheRead; if (ev.DedupKey.StartsWith("codex-response:")) native++; }
            Check(output == 10200 && read == 4000 && native == 2, "rewritten history keeps disjoint cache/output and distinct response IDs");
            rows.AddRange(parent.Events); rows.AddRange(child.Events);
            Check(Dedup(rows, parent.SerializeState(), child.SerializeState()).Count == 3, "live/archive copies do not change fork prefix accounting");
            result = Dedup(new List<UsageEvent>(child.Events), child.SerializeState());
            Check(result.Count == 3 && Input(result) == 1010000, "missing parent evidence keeps the child's historical usage");
        }
    }
    private static void ConservativeBoundaries()
    {
        List<UsageEvent> rows = new List<UsageEvent> { Ev("parent", "p1", 1000, 100), Ev("child", "c1", 2000, 100) };
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 1, "explicit fork permits exact timestamp rewriting");
        Check(Dedup(rows, Meta("child", "", 2000)).Count == 2, "identical usage without parent metadata remains independent");
        Check(Dedup(rows, Meta("child", "parent", 0)).Count == 2, "unknown fork timestamp does not establish inherited usage");
        Check(Dedup(rows, Meta("child", "missing", 2000)).Count == 2, "parent metadata alone without its ledger cannot delete usage");
        rows[1].TimestampMs = 1999;
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "nearby timestamps are not a fork identity heuristic");
        rows[1].TimestampMs = 2001;
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "post-fork request is independent");
        rows[1].TimestampMs = 1000;
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 1, "original copied timestamps still deduplicate");
        rows[0].DedupKey = "codex-response:parent"; rows[1].DedupKey = "codex-response:child";
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "different response IDs survive even with identical original time and tokens");
        rows[1].DedupKey = "codex-response:parent";
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 1, "replayed native response ID is still one request");

        rows = new List<UsageEvent> { Ev("parent", "p1", 1000, 100), Ev("parent", "p2", 1001, 200),
            Ev("child", "c1", 2000, 100), Ev("child", "c2", 2000, 200) };
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "contiguous parent history proves the entire leading copied prefix");
        rows[3].FreshInput = 300;
        rows.Add(Ev("child", "c3", 2000, 200));
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 4, "first unmatched prefix record stops later same-usage deductions");
        rows[3].FreshInput = 200; rows[3].DedupKey = "codex-response:native"; rows[4].TimestampMs = 1001;
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 4, "first native response stops prefix matching even when its usage equals the parent");
        rows = new List<UsageEvent> { Ev("parent", "p1", 1000, 100), Ev("child", "c1", 1000, 100), Ev("child", "c2", 2000, 100) };
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "one parent request cannot justify removing two child records");
        rows = new List<UsageEvent> { Ev("parent", "p1", 1000, 100), Ev("parent", "p2", 1001, 200), Ev("parent", "p3", 1002, 300),
            Ev("child", "c1", 2000, 100), Ev("child", "c2", 2000, 300) };
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 4, "a gap in parent sequence terminates the copied prefix");
        rows = new List<UsageEvent> { Ev("parent", "p1", 2001, 100), Ev("child", "c1", 2000, 100) };
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "parent's later usage cannot prove child history");
        rows[0].TimestampMs = 1000; rows[1].CacheRead++;
        Check(Dedup(rows, Meta("child", "parent", 2000)).Count == 2, "all four usage dimensions must agree");
    }
    private static void CheckpointIdentity()
    {
        CodexUsageParser p = new CodexUsageParser("OpenAI Codex", "checkpoint-thread", new DateTime(2026, 11, 1), "", null);
        p.OnLine("{\"timestamp\":\"2026-11-01T09:40:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"checkpoint-native\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20},\"thread_token_usage\":{\"input_tokens\":200,\"output_tokens\":40}}}", 0);
        p.OnLine("{\"timestamp\":\"2026-11-01T09:40:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":200,\"output_tokens\":40}}}}", 1);
        List<UsageEvent> rows = Dedup(new List<UsageEvent>(p.Events), p.SerializeState());
        long output = 0; foreach (UsageEvent row in rows) output += row.Output;
        Check(rows.Count == 2 && Input(rows) == 200 && output == 40,
            "same-timestamp cumulative remainder keeps its independent identity even when its usage equals the native request");
    }
    private static void CheckScan(ScanResult result, int expectedTurns, long expectedInput, string label)
    {
        long input = 0; int turns = 0;
        foreach (RecordBucket row in result.Records) { input += row.FreshInput + row.CacheRead + row.CacheWrite; turns += row.Turns; }
        Check(result.Warnings.Count == 0 && turns == expectedTurns && input == expectedInput, label);
    }
    private static void Integration(string root)
    {
        string home = Path.Combine(root, "home"), sessions = Path.Combine(home, ".codex", "sessions"), archive = Path.Combine(home, ".codex", "archived_sessions");
        Directory.CreateDirectory(sessions); Directory.CreateDirectory(archive);
        string parentPath = Path.Combine(sessions, "parent.jsonl"), childPath = Path.Combine(sessions, "child.jsonl");
        File.Copy("tests/fixtures/codex_fork_parent.jsonl", parentPath);
        File.Copy("tests/fixtures/codex_fork_child.jsonl", childPath);
        File.Copy(childPath, Path.Combine(archive, "child-copy.jsonl"));
        CheckScan(new TokenScanner(home, false).Run(), 3, 1010000, "full source scan deduplicates verified rewritten history");
        typeof(LocalUsageStore).GetField("_memCursors", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
        CheckScan(new TokenScanner(home, false).Run(), 3, 1010000, "SQLite cursor reload preserves prefix and native boundary");
        File.AppendAllText(childPath, "{\"timestamp\":\"2026-11-01T09:42:10.923Z\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"fork-native-3\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":10}}}\n");
        CheckScan(new TokenScanner(home, false).Run(), 4, 1011000, "incremental child append adds only new native usage");
        File.Delete(parentPath);
        CheckScan(new TokenScanner(home, false).Run(), 4, 1011000, "retained parent cursor still proves inherited history after source deletion");
    }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "TokLens_ForkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string oldConfig = Environment.GetEnvironmentVariable("TOKLENS_CONFIG_DIR"), oldCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "store"));
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        try { SharedFixture(); ConservativeBoundaries(); CheckpointIdentity(); Integration(root); Console.WriteLine("PASS: " + _checks + " fork accounting assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixtures: " + root); return 1; }
        finally { Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", oldConfig); Environment.SetEnvironmentVariable("CODEX_HOME", oldCodex); }
    }
}
