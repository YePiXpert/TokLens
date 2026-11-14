using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using TokLens;

internal static class UsageAccountingTests
{
    private static int _checks;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool value, string name) { _checks++; if (!value) throw new Exception(name); }
    private static Dictionary<string, object> Obj(params object[] pairs)
    {
        Dictionary<string, object> obj = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) obj[(string)pairs[i]] = pairs[i + 1];
        return obj;
    }
    private static object Usage(long input, long output, long read, long write)
    { return Obj("input_tokens", input, "output_tokens", output, "cached_input_tokens", read, "cache_write_input_tokens", write, "reasoning_output_tokens", output / 2); }
    private static string Frame(string type, object payload, int second)
    { return Json.Serialize(Obj("type", type, "payload", payload, "timestamp", "2026-11-12T12:00:" + second.ToString("D2") + "Z")); }
    private static string Count(object last, object total, int second)
    { return Frame("event_msg", Obj("type", "token_count", "info", Obj("last_token_usage", last, "total_token_usage", total)), second); }
    private static CodexUsageParser Parser()
    { return new CodexUsageParser("OpenAI Codex", "thread", new DateTime(2026, 11, 12), "", null); }
    private static void Codex()
    {
        CodexUsageParser p = Parser();
        p.OnLine(Frame("turn_context", Obj("model", "model-a"), 0), 0);
        object u = Usage(100, 20, 60, 10);
        p.OnLine(Frame("token_usage_record", Obj("response_id", "response-1", "usage", u), 1), 1);
        p.OnLine(Count(u, u, 1), 2);
        p.OnLine(Count(u, u, 2), 3);
        Check(p.Events.Count == 1, "record/count/repeated snapshot billed once");
        Check(p.Events[0].FreshInput == 30 && p.Events[0].CacheRead == 60 && p.Events[0].CacheWrite == 10 && p.Events[0].Output == 20, "disjoint input/cache/write and reasoning not double counted");
        p.OnLine(Frame("token_usage_record", Obj("response_id", "compact", "usage", Usage(50, 5, 10, 0)), 3), 4);
        p.OnLine(Count(Usage(0, 0, 0, 0), Usage(150, 25, 70, 10), 3), 5);
        Check(p.Events.Count == 2, "compaction record survives a zero last snapshot");
        p.OnLine(Frame("event_msg", Obj("type", "thread_settings_applied", "model", "model-b"), 4), 6);
        p.OnLine(Count(Usage(12, 3, 2, 0), null, 5), 7);
        p.OnLine(Count(Usage(12, 3, 2, 0), null, 5), 8);
        Check(p.Events.Count == 3 && p.Events[2].Model == "model-b", "last-only log deduplicates and reads settings model");
        CodexUsageParser q = new CodexUsageParser("OpenAI Codex", "", DateTime.Now, p.SerializeState(), p.Events);
        q.OnLine(Count(Usage(12, 3, 2, 0), null, 5), 9);
        Check(q.Events.Count == 3, "incremental state round trip does not rebill last-only events");
        p = Parser(); p.OnLine(Count(null, Usage(100, 10, 20, 5), 1), 1); p.OnLine(Count(null, Usage(160, 17, 30, 8), 2), 2);
        Check(p.Events.Count == 2 && p.Events[1].FreshInput == 47 && p.Events[1].CacheWrite == 3, "total-only deltas");
        p = Parser(); p.OnLine(Count(Usage(10, 1, 0, 0), Usage(1000, 100, 500, 0), 1), 1); p.OnLine(Count(Usage(20, 2, 0, 0), Usage(20, 2, 0, 0), 2), 2);
        Check(p.Events.Count == 2 && p.Events[1].FreshInput == 20, "independent cumulative lanes prefer exact last usage");
        p = Parser(); p.OnLine(Count(Usage(10, 1, 50, 50), Usage(10, 1, 50, 50), 1), 1);
        Check(p.Events[0].FreshInput == 0 && p.Events[0].CacheRead == 10 && p.Events[0].CacheWrite == 0, "malformed cache cannot create input tokens");
        p = Parser();
        p.OnLine(Frame("token_usage_record", Obj("response_id", "total-only", "usage", u), 1), 1);
        p = new CodexUsageParser("OpenAI Codex", "thread", DateTime.Now, p.SerializeState(), p.Events);
        p.OnLine(Count(null, u, 1), 2);
        p.OnLine(Frame("token_usage_record", Obj("response_id", "total-only-2", "usage", Usage(60, 7, 10, 3)), 2), 3);
        p.OnLine(Count(null, Usage(160, 27, 70, 13), 2), 4);
        Check(p.Events.Count == 2 && p.Events[1].FreshInput == 47, "request records paired with total-only deltas are not billed twice across restart");
        p = Parser();
        p.OnLine(Count(null, Usage(100, 10, 20, 5), 1), 1);
        p.OnLine(Count(null, Usage(20, 2, 5, 1), 2), 2);
        p.OnLine(Count(null, Usage(30, 3, 7, 2), 3), 3);
        p.OnLine(Count(null, Usage(20, 2, 5, 1), 4), 4);
        p.OnLine(Count(null, Usage(20, 2, 5, 1), 5), 5);
        long resetTotal = 0;
        foreach (UsageEvent ev in p.Events) resetTotal += ev.FreshInput + ev.Output + ev.CacheRead + ev.CacheWrite;
        Check(p.Events.Count == 4 && resetTotal == 165, "total-only resets count the new segment and suppress repeated snapshots");
    }
    private static RecordBucket Bucket(string model, long fresh, long cached, int turns)
    { return new RecordBucket { Date = "2026-11-12", Tool = "OpenAI Codex", Model = model, FreshInput = fresh, CacheRead = cached, Turns = turns, CostUsd = fresh / 100.0, RateLabel = "test" }; }
    private static NodeSnapshot Node(int rev, params RecordBucket[] rows)
    { NodeSnapshot n = new NodeSnapshot { MachineId = "test", ScannerRev = rev }; n.Records.AddRange(rows); return n; }
    private static void Merges()
    {
        NodeSnapshot old = Node(27, Bucket("wrong", 100, 900, 10));
        NodeSnapshot fresh = Node(UsageAccounting.Revision, Bucket("right", 30, 200, 3));
        string partition = UsageAccounting.Partition("2026-11-12", "OpenAI Codex");
        fresh.ReconciledPartitions[partition] = 200;
        NodeSnapshot merged = SyncService.MergeNodeSnapshotsHighWater(fresh, old);
        Check(merged.Records.Count == 1 && merged.Records[0].Model == "right" && merged.Records[0].TotalTokens == 230, "verified lower correction replaces old model partition");
        Check(SyncService.MergeNodeSnapshotsHighWater(old, fresh).Records[0].Model == "right", "old primary cannot resurrect old model");
        Check(SyncService.MergeNodeSnapshotsHighWater(merged, merged).Records.Count == 1, "idempotent corrected merge");
        NodeSnapshot roundtrip = SyncService.DeserializeSnapshot(SyncService.SerializeSnapshot(merged), true);
        Check(roundtrip.ReconciledPartitions[partition] == 200, "proof survives cloud serialization");
        Check(SyncService.MergeNodeSnapshotsHighWater(old, roundtrip).Records.Count == 1, "restored proof blocks stale cloud data");
        NodeSnapshot earlier = Node(UsageAccounting.Revision, Bucket("earlier", 600, 0, 6)); earlier.ReconciledPartitions[partition] = 100;
        Check(SyncService.MergeNodeSnapshotsHighWater(earlier, fresh).Records[0].Model == "right", "per-partition version selects newer measured data");
        NodeSnapshot empty = Node(UsageAccounting.Revision); empty.ReconciledPartitions[partition] = 300;
        Check(SyncService.MergeNodeSnapshotsHighWater(merged, empty).Records.Count == 0, "empty partition tombstone survives merge");
        merged = SyncService.MergeNodeSnapshotsHighWater(Node(27, Bucket("same", 200, 800, 10)), Node(27, Bucket("same", 100, 900, 10)));
        Check(merged.Records[0].TotalTokens == 1000, "whole-row merge cannot synthesize 1100 tokens from two 1000-token rows");
    }
    private static byte[] Varint(ulong n)
    { List<byte> b = new List<byte>(); while (n >= 128) { b.Add((byte)((n & 127) | 128)); n >>= 7; } b.Add((byte)n); return b.ToArray(); }
    private static byte[] Field(int tag, byte[] bytes)
    { List<byte> b = new List<byte>(Varint((ulong)((tag << 3) | 2))); b.AddRange(Varint((ulong)bytes.Length)); b.AddRange(bytes); return b.ToArray(); }
    private static void NativeModels()
    {
        byte[] response = Field(19, Encoding.UTF8.GetBytes("gemini-4-carbon"));
        byte[] tier = Field(21, Encoding.UTF8.GetBytes("Gemini 4 Carbon (High)"));
        List<byte> bytes = new List<byte>(response); bytes.AddRange(tier);
        byte[] nested = Field(1, bytes.ToArray());
        Check(UsageProto.Text(UsageProto.Message(nested, 1), 19) == "gemini-4-carbon", "exact protobuf path for backend");
        Check(UsageAccounting.AntigravityModel("gemini-4-carbon", "Gemini 4 Carbon (High)") == "gemini-4-carbon-high", "preserve model tier");
        Check(UsageAccounting.AntigravityModel("gemini-flash-worker-agy", "Gemini Flash") == "gemini-flash-worker-agy", "generic display cannot hide actual worker");
        Check(UsageProto.Message(new byte[] { 10, 255 }, 1) == null, "truncated protobuf is rejected");
        PricingEngine pricing = new PricingEngine(Path.GetTempPath());
        Check(pricing.Lookup("gemini-4-carbon-high").InputPerM == pricing.Lookup("gemini-4-carbon").InputPerM, "tier uses registered base rate");
        Check(pricing.Lookup("gpt-nonexistent-price").Source == "unpriced" && pricing.Lookup("gpt-nonexistent-price").InputPerM == 0, "unknown model does not borrow arbitrary rate");
        Check(pricing.Lookup("gemini-pro-orchestrator").InputPerM > 0 && pricing.Lookup("gemini-flash-worker-agy").InputPerM > 0, "known orchestrator and worker remain priced");
    }
    private static void ResetStore()
    { typeof(LocalUsageStore).GetField("_memCursors", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null); }
    private static void Integration(string root)
    {
        string nativePath = Path.Combine(root, "native-models.db");
        using (WinSqlite db = WinSqlite.OpenReadWrite(nativePath))
        {
            db.ExecRequired("CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY,data BLOB)");
            db.ExecRequired("CREATE TABLE steps(idx INTEGER PRIMARY KEY,step_type INTEGER,metadata BLOB)");
            byte[] data = Field(1, Field(19, Encoding.UTF8.GetBytes("gemini-4-carbon")));
            db.ExecRequired("INSERT INTO gen_metadata VALUES(9,X'" + BitConverter.ToString(data).Replace("-", "") + "')");
            data = Field(20, new byte[] { 24, 9 });
            db.ExecRequired("INSERT INTO steps VALUES(101,15,X'" + BitConverter.ToString(data).Replace("-", "") + "')");
            data = Field(1, Field(19, Encoding.UTF8.GetBytes("gemini-flash-worker-agy")));
            db.ExecRequired("INSERT INTO gen_metadata VALUES(0,X'" + BitConverter.ToString(data).Replace("-", "") + "')");
            data = Field(20, new byte[0]);
            db.ExecRequired("INSERT INTO steps VALUES(102,23,X'" + BitConverter.ToString(data).Replace("-", "") + "')");
        }
        object[] nativeArgs = new object[] { nativePath, null, null };
        bool found = (bool)typeof(TokenScanner).GetMethod("TryLoadAntigravityDbStepModels", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, nativeArgs);
        Dictionary<string, string> nativeModels = (Dictionary<string, string>)nativeArgs[1];
        Check(found && nativeModels["101"] == "gemini-4-carbon", "native step index does not assume generation index plus one");
        Check(nativeModels["102"] == "gemini-flash-worker-agy", "checkpoint and omitted zero generation reference resolve actual worker");
        string home = Path.Combine(root, "home"), sessions = Path.Combine(home, ".codex", "sessions");
        Directory.CreateDirectory(sessions);
        using (WinSqlite db = WinSqlite.OpenReadWrite(Path.Combine(home, ".codex", "state_5.sqlite")))
        {
            db.ExecRequired("CREATE TABLE threads(id TEXT, model TEXT,tokens_used INTEGER,created_at INTEGER)");
            db.ExecRequired("INSERT INTO threads VALUES('no-source','gpt-6-astra',1000000000,1794484800)");
        }
        string thread = "11111111-1111-1111-1111-111111111111";
        string stream = Frame("session_meta", Obj("id", thread), 0) + "\n" + Frame("turn_context", Obj("model", "gpt-6.1-sol"), 0) + "\n" +
            Frame("token_usage_record", Obj("response_id", "integration", "usage", Usage(100, 20, 60, 10)), 1) + "\n" + Count(Usage(100, 20, 60, 10), Usage(100, 20, 60, 10), 1) + "\n";
        File.WriteAllText(Path.Combine(sessions, "rollout-" + thread + ".jsonl"), stream);
        Directory.CreateDirectory(Path.Combine(home, ".codex", "archived_sessions"));
        File.WriteAllText(Path.Combine(home, ".codex", "archived_sessions", "copy-" + thread + ".jsonl"), stream);
        ResetStore();
        ScanResult scan = new TokenScanner(home, false).Run();
        Check(scan.Records.Count == 1 && scan.Records[0].Turns == 1 && scan.Records[0].TotalTokens == 120, "live/archive file copies plus dual events bill once");
        Check(scan.Records[0].Model == "gpt-6.1-sol", "aggregate-only SQLite archive cannot manufacture cached/output/model usage");
        ScanResult unchanged = new TokenScanner(home, false).Run();
        Check(unchanged.Records[0].TotalTokens == 120, "unchanged cursor reuses request identities");
        string sourcePath = Path.Combine(sessions, "rollout-" + thread + ".jsonl");
        File.AppendAllText(sourcePath, Frame("turn_context", Obj("model", "partial-model"), 2) + "\n" + Frame("token_usage_record", Obj("response_id", "partial", "usage", Usage(400, 20, 0, 0)), 3) + "\n{\"type\":\"token_usage_record\",broken}\n");
        ScanResult partial = new TokenScanner(home, false).Run();
        Check(partial.Warnings.Count > 0 && partial.Records.Count == 1 && partial.Records[0].TotalTokens == 120, "partial scan cannot add a second model bucket to an already archived day");
        File.WriteAllText(sourcePath, stream);
        LocalUsageStore.MergeAndSaveDailyArchive(home, new List<RecordBucket> { Bucket("stale-model", 10000, 90000, 100) });
        scan = new TokenScanner(home, false).Run();
        Check(scan.Records.Count == 1 && scan.Records[0].Turns == 1, "historical repair removes stale model despite much lower corrected totals");
        Check(LocalUsageStore.TryLoadInstantSnapshot(home).ReconciledPartitions.Count == 1, "instant snapshot persists reconciliation even with no dirty files");
        string source = Path.Combine(sessions, "rollout-" + thread + ".jsonl");
        File.WriteAllText(source, stream.Replace("gpt-6.1-sol", "gpt-6.1-lux"));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(home, ".codex", "archived_sessions", "copy-" + thread + ".jsonl"));
        scan = new TokenScanner(home, false).Run();
        Check(scan.Records.Count == 1 && scan.Records[0].Model == "gpt-6.1-lux", "same-size changed log is reparsed instead of seeking past the update");
        File.Delete(Path.Combine(sessions, "rollout-" + thread + ".jsonl"));
        File.Delete(Path.Combine(home, ".codex", "archived_sessions", "copy-" + thread + ".jsonl"));
        using (WinSqlite db = WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath)) db.ExecRequired("UPDATE kv_meta SET v='toklens.store.v2.7' WHERE k='schema'");
        ResetStore(); scan = new TokenScanner(home, false).Run();
        Check(scan.Records[0].TotalTokens == 120, "migration preserves deleted request cursors when no live files remain");
        Check(Directory.GetFiles(Path.GetDirectoryName(LocalUsageStore.DbFilePath), "*.before-v2.10.*.db").Length > 0, "migration creates SQLite-consistent backup");
    }
    private static void StoreFailures(string root)
    {
        string previousDir = Environment.GetEnvironmentVariable("TOKLENS_CONFIG_DIR");
        string store = Path.Combine(root, "failure-store");
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", store);
        Directory.CreateDirectory(store);
        ResetStore();
        try
        {
            byte[] damaged = Encoding.UTF8.GetBytes("deliberately invalid test ledger");
            File.WriteAllBytes(LocalUsageStore.DbFilePath, damaged);
            bool stopped = false;
            try { LocalUsageStore.LoadAllCursors(); } catch (IOException) { stopped = true; }
            Check(stopped && File.ReadAllText(LocalUsageStore.DbFilePath) == Encoding.UTF8.GetString(damaged), "unreadable ledger stops scanning and keeps the original file");
            stopped = false;
            try { LocalUsageStore.LoadAllCursors(); } catch (IOException) { stopped = true; }
            Check(stopped, "failed cursor read is not cached as an empty ledger");
            File.Delete(LocalUsageStore.DbFilePath);
            ResetStore(); LocalUsageStore.LoadAllCursors();
            LocalUsageStore.SaveScanBatch(root, null, new ScanResult());
            using (WinSqlite db = WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath))
                db.ExecRequired("CREATE TRIGGER fail_cursor BEFORE INSERT ON file_cursors BEGIN SELECT RAISE(ABORT,'test write failure'); END;");
            List<FileCursorEntry> cursors = new List<FileCursorEntry> { new FileCursorEntry { FileKey="test-cursor", Events=new List<UsageEvent>(), ExtraMeta="" } };
            stopped = false;
            try { LocalUsageStore.SaveScanBatch(root, cursors, null); } catch (IOException) { stopped = true; }
            Check(stopped && !LocalUsageStore.LoadAllCursors().ContainsKey("test-cursor"), "failed cursor transaction rolls back and cannot advance memory state");
            using (WinSqlite db = WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath)) db.ExecRequired("DROP TRIGGER fail_cursor;");
            LocalUsageStore.SaveScanBatch(root, cursors, null);
            ResetStore();
            Check(LocalUsageStore.LoadAllCursors().ContainsKey("test-cursor"), "cursor write can be retried after the failure clears");
        }
        finally { Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", previousDir); ResetStore(); }
    }
    private static void LongPaths(string root)
    {
        string home = Path.Combine(root, "long-home"), dir = Path.Combine(home, ".codex", "sessions");
        while (dir.Length < 300) dir = Path.Combine(dir, new string('x', 50));
        Directory.CreateDirectory(TokenScanner.ToLongPath(dir));
        string tid="55555555-5555-5555-5555-555555555555";
        File.WriteAllText(TokenScanner.ToLongPath(Path.Combine(dir, tid + ".jsonl")), Frame("session_meta", Obj("id",tid), 0) + "\n" + Frame("token_usage_record", Obj("response_id","long-path","usage",Usage(100,20,60,10)), 1) + "\n");
        ScanResult scan = new TokenScanner(home,false).Run();
        Check(scan.Warnings.Count == 0 && scan.Records.Count == 1 && scan.Records[0].TotalTokens == 120, "recursive traversal reads logs beyond the Windows legacy path limit");
    }
    private static void ForkedHistory(string root)
    {
        string home = Path.Combine(root, "fork-home"), sessions = Path.Combine(home, ".codex", "sessions");
        Directory.CreateDirectory(sessions);
        string parent = "22222222-2222-2222-2222-222222222222", child = "33333333-3333-3333-3333-333333333333";
        string inherited = Count(Usage(100, 20, 60, 10), Usage(100, 20, 60, 10), 1);
        File.WriteAllText(Path.Combine(sessions, parent + ".jsonl"), Frame("session_meta", Obj("id", parent), 0) + "\n" + Frame("turn_context", Obj("model", "gpt-6.1-sol"), 0) + "\n" + inherited + "\n");
        File.WriteAllText(Path.Combine(sessions, child + ".jsonl"), Frame("session_meta", Obj("id", child, "forked_from_id", parent), 2) + "\n" + Frame("turn_context", Obj("model", "gpt-6-astra"), 0) + "\n" + inherited + "\n" + Count(Usage(80, 10, 20, 0), Usage(180, 30, 80, 10), 3) + "\n");
        ScanResult scan = new TokenScanner(home, false).Run();
        int turns = 0; long total = 0;
        foreach (RecordBucket r in scan.Records) { turns += r.Turns; total += r.TotalTokens; }
        Check(turns == 2 && total == 210, "verified fork replay is not a new request or a new model charge");
    }
    private static void ZCodeModels(string root)
    {
        string home = Path.Combine(root, "zcode-home"), path = Path.Combine(home, ".zcode", "cli", "db", "db.sqlite");
        using (WinSqlite db = WinSqlite.OpenReadWrite(path))
        {
            db.ExecRequired("CREATE TABLE message(id TEXT,time_created INTEGER,time_updated INTEGER,data TEXT)");
            object tokens = Obj("input",100,"output",20,"total",120,"cache",Obj("read",60,"write",10));
            string data = Json.Serialize(Obj("role","assistant","modelId","glm-5.3-flash","providerId","openai","time",Obj("created",1794484800000L),"tokens",tokens));
            db.ExecRequired("INSERT INTO message VALUES('zc1',1794484800000,1794484801000,"+WinSqlite.SqlQuote(data)+")");
            data = Json.Serialize(Obj("role","assistant","time",Obj("created",1794484800000L),"tokens",tokens));
            db.ExecRequired("INSERT INTO message VALUES('zc2',1794484800000,1794484801000,"+WinSqlite.SqlQuote(data)+")");
        }
        ScanResult scan = new TokenScanner(home,false).Run();
        Check(scan.Records.Count == 2, "ZCode native modelId casing and missing model stay separate");
        bool known = false, unknown = false;
        foreach (RecordBucket r in scan.Records)
        {
            known |= r.Model == "glm-5.3-flash";
            unknown |= r.Model == "unknown" && r.CostUsd == 0;
            Check(r.FreshInput == 30 && r.CacheWrite == 10 && r.TotalTokens == 120,"ZCode total-inclusive input splits into disjoint counters");
        }
        Check(known && unknown, "ZCode does not invent a default GLM model");
        Check(scan.ReconciledPartitions.Count == 1,"ZCode request IDs certify its day partition");
    }
    public static int Main()
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        string root = Path.Combine(Path.GetTempPath(), "TokLens_UsageTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "store"));
        try { Codex(); Merges(); NativeModels(); Integration(root); ForkedHistory(root); ZCodeModels(root); StoreFailures(root); LongPaths(root); Console.WriteLine("PASS: " + _checks + " accounting assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixtures: " + root); return 1; }
        finally { Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", null); }
    }
}
