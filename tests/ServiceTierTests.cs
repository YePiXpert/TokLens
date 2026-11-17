using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using TokLens;

internal static class ServiceTierTests
{
    private static int checks;
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static double N(Dictionary<string,object> o,string key){return Convert.ToDouble(o[key],CultureInfo.InvariantCulture);}
    private static string S(Dictionary<string,object> o,string key){return Convert.ToString(o[key],CultureInfo.InvariantCulture);}
    private static void Equal(double a,double b,string message){Check(Math.Abs(a-b)<0.000000001,message+": "+a+" != "+b);}
    private static void Optional(double? actual,object expected,string message)
    {if(expected==null)Check(!actual.HasValue,message+" absent");else {Check(actual.HasValue,message+" present");Equal(actual.Value,Convert.ToDouble(expected,CultureInfo.InvariantCulture),message);}}
    private static Dictionary<string,object> O(params object[] pairs)
    {Dictionary<string,object> o=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)o[(string)pairs[i]]=pairs[i+1];return o;}
    private static string Frame(string type,object payload,DateTime when)
    {return Json.Serialize(O("type",type,"payload",payload,"timestamp",when.ToUniversalTime().ToString("o")));}
    private static object Usage(){return O("input_tokens",100,"output_tokens",20,"cached_input_tokens",25);}
    private static void Fixture(string root)
    {
        string path=Path.Combine(Environment.CurrentDirectory,"tests","fixtures","service_tier_cases.json");
        Dictionary<string,object> fixture=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(path));
        PricingEngine engine=new PricingEngine(root);
        foreach(Dictionary<string,object> c in (IList)fixture["cases"])
        {
            string id=S(c,"id");UsageEvent ev=new UsageEvent("OpenAI Codex",S(c,"model"),S(c,"date"),(long)N(c,"fi"),(long)N(c,"output"),(long)N(c,"cache_read"),(long)N(c,"cache_write"),N(c,"reported"),1);
            ev.Provider=S(c,"provider");ev.RequestedServiceTier=S(c,"requested");ev.ActualServiceTier=S(c,"actual");
            double cost,baseline;ModelRate rate;engine.ComputeEventCost(ev,out cost,out baseline,out rate);
            Equal(cost,N(c,"expected_cost"),id+" cost");Check(ev.CostBasis==S(c,"expected_basis"),id+" cost basis "+ev.CostBasis);
            if(id=="fast-cache")Equal(baseline,0.0104,"Fast no-cache baseline uses Fast prices");
            Optional(ev.StandardEquivalentUsd,c["expected_standard"],id+" standard");Optional(ev.ServiceTierPremiumUsd,c["expected_premium"],id+" premium");
            Check(ev.FreshInput==(long)N(c,"fi")&&ev.Output==(long)N(c,"output"),id+" token counters unchanged");
        }
        Check(engine.Lookup("gpt-6.1-sol-fast").Source=="unpriced","fast suffix is never silently stripped into a standard quote");
    }
    private static void Parser()
    {
        DateTime now=DateTime.UtcNow;CodexUsageParser p=new CodexUsageParser("OpenAI Codex","thread",now,"",null);
        p.OnLine(Frame("turn_context",O("model","gpt-6.1-sol","service_tier","priority","cwd","C:/project"),now),0);
        p.OnLine(Frame("token_usage_record",O("response_id","one","usage",Usage(),"actual_service_tier","default","model_context_window",100000),now.AddSeconds(1)),1);
        UsageEvent first=p.Events[0];Check(first.RequestedServiceTier=="fast"&&first.ActualServiceTier=="standard","request intent and actual response downgrade stay separate");
        Check(first.Workspace=="C:/project"&&first.SessionId=="thread"&&first.ActualContextLimit==100000,"workspace/session/context evidence preserved");
        p=new CodexUsageParser("OpenAI Codex","thread",now,p.SerializeState(),p.Events);
        p.OnLine(Frame("token_usage_record",O("response_id","two","usage",Usage()),now.AddSeconds(2)),2);
        Check(p.Events[1].RequestedServiceTier=="fast"&&p.Events[1].ActualServiceTier=="unknown","restart inherits request setting but never previous actual tier");
        p.OnLine(Frame("event_msg",O("type","thread_settings_applied","service_tier","standard"),now.AddSeconds(3)),3);
        p.OnLine(Frame("token_usage_record",O("response_id","three","usage",Usage(),"response",O("service_tier","priority")),now.AddSeconds(4)),4);
        Check(p.Events[2].RequestedServiceTier=="standard"&&p.Events[2].ActualServiceTier=="fast","actual response tier wins after settings change");
        p.OnLine(Frame("token_usage_record",O("response_id","one","usage",Usage()),now.AddSeconds(5)),5);
        Check(p.Events[0].RequestedServiceTier=="fast"&&p.Events[0].ActualServiceTier=="standard","replaying an earlier response cannot inherit the later turn's requested tier");
        string encoded=LocalUsageStore.SerializeEvents(p.Events);List<UsageEvent> decoded=LocalUsageStore.DeserializeEvents(encoded);
        Check(decoded.Count==3&&decoded[0].Workspace=="C:/project"&&decoded[1].ActualServiceTier=="unknown"&&decoded[2].ActualServiceTier=="fast","cursor metadata roundtrip");
        UsageEvent legacy=LocalUsageStore.DeserializeEvents("OpenAI Codex\tgpt-6.1-sol\t2026-11-17\t1\t2\t0\t0\t0\t1")[0];
        Check(legacy.RequestedServiceTier=="unknown"&&legacy.ActualServiceTier=="unknown","old cursor never becomes standard");

        CodexUsageParser desktop=new CodexUsageParser("OpenAI Codex","desktop-thread",now,"",null);
        desktop.OnLine(Frame("turn_context",O("model","gpt-6-astra"),now),0);
        desktop.OnLine(Frame("token_usage_record",O("response_id","before","usage",Usage()),now.AddSeconds(1)),1);
        desktop.OnLine(Frame("event_msg",O("type","thread_settings_applied","thread_settings",O("model","gpt-6.1-sol","model_provider_id","openai","service_tier","priority")),now.AddSeconds(2)),2);
        desktop.OnLine(Frame("token_usage_record",O("response_id","fast","usage",Usage()),now.AddSeconds(3)),3);
        Check(desktop.Events[0].RequestedServiceTier=="unknown","later desktop settings never backfill earlier requests");
        Check(desktop.Events[1].RequestedServiceTier=="fast"&&desktop.Events[1].ActualServiceTier=="unknown"&&desktop.Events[1].TierEvidence=="thread_settings:requested","native desktop nested settings establish requested Fast evidence only");
        Check(desktop.Events[1].Model=="gpt-6.1-sol"&&desktop.Events[1].Provider=="openai","native desktop nested model and provider are used");
        desktop.OnLine(Frame("token_usage_record",O("response_id","before","usage",Usage()),now.AddMilliseconds(3500)),3);
        Check(desktop.Events[0].RequestedServiceTier=="unknown","replaying an originally unknown response after Fast does not backfill it");
        desktop=new CodexUsageParser("OpenAI Codex","desktop-thread",now,desktop.SerializeState(),desktop.Events);
        desktop.OnLine(Frame("token_usage_record",O("response_id","before","usage",Usage()),now.AddMilliseconds(3600)),3);
        Check(desktop.Events[0].RequestedServiceTier=="unknown","incremental cursor replay also preserves an originally unknown intent");
        desktop.OnLine(Frame("event_msg",O("type","thread_settings_applied","thread_settings",O("model","gpt-6-astra")),now.AddSeconds(4)),4);
        desktop.OnLine(Frame("token_usage_record",O("response_id","resumed","usage",Usage(),"response",O("service_tier","default")),now.AddSeconds(5)),5);
        Check(desktop.Events[2].RequestedServiceTier=="fast"&&desktop.Events[2].ActualServiceTier=="standard","resumed desktop settings persist while response tier remains authoritative");
        desktop.OnLine(Frame("event_msg",O("type","thread_settings_applied","thread_settings",O("service_tier",null)),now.AddSeconds(6)),6);
        desktop.OnLine(Frame("token_usage_record",O("response_id","cleared","usage",Usage()),now.AddSeconds(7)),7);
        Check(desktop.Events[3].RequestedServiceTier=="unknown"&&desktop.Events[3].ActualServiceTier=="unknown","explicit desktop null resets intent without inventing Standard");
        desktop.OnLine(Frame("event_msg",O("type","thread_settings_applied","thread_settings",O("service_tier","priority")),now.AddSeconds(8)),8);
        desktop.OnLine(Frame("event_msg",O("type","thread_settings_applied","thread_settings",O("service_tier","new-future-tier")),now.AddSeconds(9)),9);
        desktop.OnLine(Frame("token_usage_record",O("response_id","future","usage",Usage()),now.AddSeconds(10)),10);
        Check(desktop.Events[4].RequestedServiceTier=="unknown","unsupported desktop tier never leaks the prior Fast intent");
        desktop.OnLine(Frame("token_usage_record",O("response_id","fast","usage",Usage()),now.AddSeconds(11)),11);
        Check(desktop.Events[1].RequestedServiceTier=="fast","replayed desktop response keeps its original requested tier");
        desktop.OnLine(Frame("response_item",O("type","thread_settings_applied","thread_settings",O("service_tier","priority","model","gpt-5.5")),now.AddSeconds(12)),12);
        desktop.OnLine(Frame("token_usage_record",O("response_id","non-settings-item","usage",Usage()),now.AddSeconds(13)),13);
        Check(desktop.Events[5].RequestedServiceTier=="unknown"&&desktop.Events[5].Model=="gpt-6-astra","only event_msg may apply thread settings; response-item content cannot change model or tier");
    }
    private static void PriceSupportLimits(string root)
    {
        PricingEngine engine=new PricingEngine(root);
        foreach(string model in new string[]{"gpt-5.4","gpt-5.5"})
        {
            UsageEvent ev=new UsageEvent("OpenAI Codex",model,"2026-11-17",272000,10,0,0,0,1);
            ev.Provider="openai";
            ev.RequestedServiceTier="fast";ev.ActualServiceTier="fast";double cost,baseline;ModelRate rate;
            engine.ComputeEventCost(ev,out cost,out baseline,out rate);
            Equal(cost,model=="gpt-5.4"?1.3603:3.40075,model+" exactly maximum prompt remains priced");
            ev.FreshInput=271999;ev.CacheRead=1;ev.CacheWrite=1;
            engine.ComputeEventCost(ev,out cost,out baseline,out rate);
            Check(cost==0&&ev.CostBasis=="unpriced"&&!ev.ServiceTierPremiumUsd.HasValue,model+" one token over maximum including caches is unpriced");
            Check(ev.FreshInput==271999&&ev.CacheRead==1&&ev.CacheWrite==1,model+" unsupported tier preserves raw token counts");
            ev.ActualServiceTier="unknown";engine.ComputeEventCost(ev,out cost,out baseline,out rate);
            Check(cost==0&&ev.CostBasis=="unpriced",model+" requested-only Fast over maximum cannot fall back to standard price");
            ev.LoggedCostUsd=4.25;engine.ComputeEventCost(ev,out cost,out baseline,out rate);
            Equal(cost,4.25,model+" reported cost still wins above rate-table support limit");Check(ev.CostBasis=="reported",model+" reported cost provenance");
            ev.LoggedCostUsd=0;ev.ActualServiceTier="standard";engine.ComputeEventCost(ev,out cost,out baseline,out rate);
            Check(cost>0&&ev.CostBasis=="estimated_tier_price",model+" actual Standard uses its own supported long-context rate");
        }
    }
    private static void SharedDesktopFixture()
    {
        CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","fixture",DateTime.UtcNow,"",null);
        foreach(string line in File.ReadLines(Path.Combine(Environment.CurrentDirectory,"tests","fixtures","codex_thread_settings.jsonl")))parser.OnLine(line,0);
        string[] expected={"unknown","fast","fast","unknown","standard","unknown"};
        Check(parser.Events.Count==expected.Length,"shared native desktop fixture preserves all six distinct responses");
        for(int i=0;i<expected.Length;i++)
        {
            Check(parser.Events[i].RequestedServiceTier==expected[i],"shared desktop historical requested tier "+i);
            Check(parser.Events[i].ActualServiceTier=="unknown","shared desktop actual tier stays unknown without response evidence "+i);
        }
        Check(parser.Events[1].TierEvidence=="thread_settings:requested"&&parser.Events[1].Model=="gpt-6-astra","shared desktop nested attribution and evidence");
    }
    private static void CounterResetEpoch()
    {
        CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","counter-fixture",DateTime.UtcNow,"",null);
        foreach(string line in File.ReadLines(Path.Combine(Environment.CurrentDirectory,"tests","fixtures","codex_counter_reset.jsonl")))parser.OnLine(line,0);
        long input=0;foreach(UsageEvent ev in parser.Events)input+=ev.FreshInput;
        Check(parser.Events.Count==3&&input==300,"counter reset with exact last usage allows an older signature in the new epoch but deduplicates its repeat");
        DateTime now=DateTime.UtcNow;
        parser=new CodexUsageParser("OpenAI Codex","id-fixture",now,"",null);
        foreach(int i in new[]{0,1,2})
        {
            parser.OnLine(Frame("token_usage_record",O("response_id",i==1?"second":"first","usage",Usage()),now.AddSeconds(i*2)),i*2);
            parser.OnLine(Frame("event_msg",O("type","token_count","info",O("total_token_usage",O("input_tokens",i==1?200:100,"output_tokens",i==1?40:20,"cached_input_tokens",i==1?50:25),"last_token_usage",Usage())),now.AddSeconds(i*2+1)),i*2+1);
        }
        Check(parser.Events.Count==2,"counter epoch reset never resets response-ID deduplication");
    }
    private static void UnchangedCumulativeTotal()
    {
        CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","unchanged-total",DateTime.UtcNow,"",null);
        string[] lines=File.ReadAllLines(Path.Combine(Environment.CurrentDirectory,"tests","fixtures","codex_unchanged_total.jsonl"));
        for(int i=0;i<3;i++)parser.OnLine(lines[i],i);
        Check(parser.Events.Count==1,"changed last usage cannot add a request when cumulative totals are unchanged");
        parser=new CodexUsageParser("OpenAI Codex","unchanged-total",DateTime.UtcNow,parser.SerializeState(),parser.Events);
        for(int i=3;i<lines.Length;i++)parser.OnLine(lines[i],i);
        long input=0,output=0;foreach(UsageEvent ev in parser.Events){input+=ev.FreshInput+ev.CacheRead+ev.CacheWrite;output+=ev.Output;}
        Check(parser.Events.Count==2&&input==200&&output==40,"unchanged totals do not suppress an independently identified response across cursor resume");
        Check(parser.Events[1].ActualServiceTier=="fast","unchanged token snapshot may still enrich the matching response metadata");
    }
    private static void ReplayedResponseModel()
    {
        DateTime now=DateTime.UtcNow;CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","replay-model",now,"",null);
        parser.OnLine(Frame("turn_context",O("model","gpt-6-astra"),now),0);
        parser.OnLine(Frame("token_usage_record",O("response_id","original","usage",Usage()),now.AddSeconds(1)),1);
        parser=new CodexUsageParser("OpenAI Codex","replay-model",now,parser.SerializeState(),parser.Events);
        parser.OnLine(Frame("turn_context",O("model","gpt-6.1-sol"),now.AddSeconds(2)),2);
        parser.OnLine(Frame("token_usage_record",O("response_id","original","usage",Usage()),now.AddSeconds(3)),3);
        Check(parser.Events.Count==1&&parser.Events[0].Model=="gpt-6-astra","replayed response cannot acquire a later turn's model");
        parser.OnLine(Frame("token_usage_record",O("response_id","original","model","gpt-5.5","usage",Usage()),now.AddSeconds(4)),4);
        Check(parser.Events[0].Model=="gpt-5.5","explicit model on the identified response may correct contextual model attribution");
    }
    private static void CompactionCheckpoints()
    {
        foreach(bool incremental in new[]{false,true})foreach(string name in new[]{"codex_compaction_checkpoint.jsonl","codex_compaction_batch.jsonl"})
        {
            CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","checkpoint",DateTime.UtcNow,"",null);
            foreach(string line in File.ReadLines(Path.Combine(Environment.CurrentDirectory,"tests","fixtures",name)))
            {
                parser.OnLine(line,0);
                if(incremental)parser=new CodexUsageParser("OpenAI Codex","checkpoint",DateTime.UtcNow,parser.SerializeState(),parser.Events);
            }
            long input=0,output=0;foreach(UsageEvent ev in parser.Events){input+=ev.FreshInput+ev.CacheRead+ev.CacheWrite;output+=ev.Output;}
            bool batch=name.Contains("batch");
            Check(parser.Events.Count==(batch?2:3)&&input==(batch?400:700)&&output==(batch?50:80),"explicit thread checkpoint subtracts already recorded native batch once: "+name+" incremental="+incremental);
        }
        foreach(bool markerFirst in new[]{false,true})
        {
            DateTime now=DateTime.UtcNow;CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","remote-compaction",now,"",null);
            string marker=Frame("compacted",O("compaction_response_id","remote"),now);
            string native=Frame("token_usage_record",O("response_id","remote","usage",O("input_tokens",300,"output_tokens",30)),now);
            parser.OnLine(markerFirst?marker:native,0);
            parser=new CodexUsageParser("OpenAI Codex","remote-compaction",now,parser.SerializeState(),parser.Events);
            parser.OnLine(markerFirst?native:marker,1);
            parser.OnLine(Frame("event_msg",O("type","token_count","info",O("total_token_usage",O("input_tokens",300,"output_tokens",30),"last_token_usage",O("input_tokens",300,"output_tokens",30))),now.AddSeconds(1)),2);
            long input=0,output=0;foreach(UsageEvent ev in parser.Events){input+=ev.FreshInput;output+=ev.Output;}
            Check(parser.Events.Count==2&&input==600&&output==60,"marked compaction does not consume a different equal-sized normal request; markerFirst="+markerFirst);
        }
        CodexUsageParser fork=new CodexUsageParser("OpenAI Codex","nested",DateTime.UtcNow,"",null);
        fork.OnLine(Json.Serialize(O("type","session_meta","payload",O("id","child","source",O("subagent",O("thread_spawn",O("parent_thread_id","parent")))))),0);
        Check(fork.ForkedFrom=="parent"&&fork.SpawnedSubagent&&fork.ForkedAtMs==0,"nested subagent parent is recorded but missing timestamp never invents a file-time fork boundary");
    }
    private static void EmbeddedCompaction()
    {
        string[] lines=File.ReadAllLines(Path.Combine(Environment.CurrentDirectory,"tests","fixtures","codex_embedded_compaction.jsonl"));
        foreach(bool incremental in new[]{false,true})
        {
            CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","embedded",DateTime.UtcNow,"",null);
            for(int i=0;i<lines.Length;i++)
            {
                parser.OnLine(lines[i],i);
                if(i>=1&&i<lines.Length-1)Check(parser.Events.Count==1,"matching embedded usage is counted once; duplicates and invalid markers add nothing: line="+i+" incremental="+incremental);
                if(incremental)parser=new CodexUsageParser("OpenAI Codex","embedded",DateTime.UtcNow,parser.SerializeState(),parser.Events);
            }
            long input=0,output=0,read=0,write=0;foreach(UsageEvent ev in parser.Events){input+=ev.FreshInput+ev.CacheRead+ev.CacheWrite;output+=ev.Output;read+=ev.CacheRead;write+=ev.CacheWrite;}
            Check(parser.Events.Count==2&&input==400&&output==50&&read==120&&write==60,"embedded checkpoint survives a duplicate top-level response that omits it; incremental="+incremental);
            Check(parser.Events[0].Model=="gpt-6-astra"&&parser.Events[0].ActualServiceTier=="fast","embedded native record retains explicit model and actual tier evidence; incremental="+incremental);
        }
        DateTime now=DateTime.UtcNow;
        foreach(object invalid in new object[]{O("input_tokens","bad","output_tokens",5),O("input_tokens",0,"output_tokens",0),O("input_tokens",1.5,"output_tokens",2),O("input_tokens",100,"output_tokens",10,"cached_input_tokens",-1)})
        {
            CodexUsageParser parser=new CodexUsageParser("OpenAI Codex","invalid-embedded",now,"",null);
            parser.OnLine(Frame("compacted",O("compaction_response_id","invalid","latest_token_usage_record",O("response_id","invalid","usage",invalid)),now),0);
            Check(parser.Events.Count==0,"invalid embedded numeric evidence never becomes a request");
        }
    }
    private static RecordBucket Bucket(string tier,int count,double cost)
    {return new RecordBucket {Date="2026-11-17",Tool="OpenAI Codex",Model="gpt-6.1-sol",Turns=count,FreshInput=count*100,Output=count*20,CostUsd=cost,RequestedServiceTier=tier,ActualServiceTier=tier,Provider="openai",CostBasis="estimated_tier_price",PricingVersion="fixture",StandardEquivalentUsd=cost/2,ServiceTierPremiumUsd=cost/2};}
    private static void Cloud()
    {
        NodeSnapshot node=new NodeSnapshot {MachineId="fixture"};node.Records.Add(Bucket("fast",3,6));node.Records.Add(Bucket("standard",2,2));
        NodeSnapshot decoded=SyncService.DeserializeSnapshot(SyncService.SerializeSnapshot(node),false);
        Check(decoded.Records.Count==2&&decoded.Records[0].ActualServiceTier=="fast","cloud preserves separate tier buckets");
        Equal(decoded.Records[0].ServiceTierPremiumUsd.Value,3,"cloud premium roundtrip");
        NodeSnapshot old=new NodeSnapshot {MachineId="fixture",ScannerRev=29};RecordBucket history=Bucket("unknown",5,10);history.CostBasis="legacy_unknown";old.Records.Add(history);
        NodeSnapshot merged=SyncService.MergeNodeSnapshotsHighWater(node,old);int count=0;foreach(RecordBucket r in merged.Records)count+=r.Turns;
        Check(count==5&&merged.Records.Count==2,"legacy aggregate and tier breakdown do not double bill the same history");
        old.Records[0].Turns=10;old.Records[0].FreshInput=1000;
        merged=SyncService.MergeNodeSnapshotsHighWater(node,old);Check(merged.Records.Count==1&&merged.Records[0].ActualServiceTier=="unknown","partial new evidence does not invent a breakdown for missing history");
        string partition=UsageAccounting.Partition("2026-11-17","OpenAI Codex");node.ReconciledPartitions[partition]=1;old.ReconciledPartitions[partition]=2;
        merged=SyncService.MergeNodeSnapshotsHighWater(node,old);count=0;foreach(RecordBucket r in merged.Records)count+=r.Turns;
        Check(count==5&&merged.Records.Count==2,"an old scanner syncing later cannot replace a newer revision's proved tier correction");
    }
    private static void ResetStore(){typeof(LocalUsageStore).GetField("_memCursors",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,null);}
    private static void Migration(string root)
    {
        string store=Path.Combine(root,"migration-store"),home=Path.Combine(root,"migration-home");Directory.CreateDirectory(store);Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",store);ResetStore();
        using(WinSqlite db=WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath))
        {
            db.ExecRequired("CREATE TABLE kv_meta(k TEXT PRIMARY KEY,v TEXT);");
            db.ExecRequired("INSERT INTO kv_meta VALUES('schema','toklens.store.v2.10');");
            db.ExecRequired("CREATE TABLE daily_archive(home_key TEXT,rec_date TEXT,tool TEXT,model TEXT,turns INTEGER,fi INTEGER,out_tok INTEGER,cr INTEGER,cw INTEGER,cost_usd REAL,base_usd REAL,rate_label TEXT,PRIMARY KEY(home_key,rec_date,tool,model));");
            db.ExecRequired("INSERT INTO daily_archive VALUES("+WinSqlite.SqlQuote(home.ToLowerInvariant())+",'2026-11-17','OpenAI Codex','gpt-6.1-sol',5,500,100,0,0,10,10,'legacy');");
        }
        List<RecordBucket> rows=new List<RecordBucket>();LocalUsageStore.MergeAndSaveDailyArchive(home,rows);
        Check(rows.Count==1&&rows[0].ActualServiceTier=="unknown"&&rows[0].CostBasis=="legacy_unknown","migration preserves original history as unknown, not standard");
        Check(Directory.GetFiles(store,"*"+LocalUsageStore.UpgradeBackupPrefix+"*.db").Length==1,"migration creates a real backup before advancing schema");
        rows=new List<RecordBucket>{Bucket("fast",3,6)};LocalUsageStore.MergeAndSaveDailyArchive(home,rows);
        Check(rows.Count==1&&rows[0].Turns==5&&rows[0].ActualServiceTier=="unknown","partial new source cannot replace missing legacy history");
        rows=new List<RecordBucket>{Bucket("fast",3,6),Bucket("standard",2,2)};LocalUsageStore.MergeAndSaveDailyArchive(home,rows);
        Check(rows.Count==2,"complete same-count breakdown replaces legacy without addition");
        using(WinSqlite db=WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath))db.ExecRequired("UPDATE kv_meta SET v='toklens.store.v2.7' WHERE k='schema';");
        ResetStore();rows=new List<RecordBucket>();LocalUsageStore.MergeAndSaveDailyArchive(home,rows);
        Check(rows.Count==2,"later schema migration cannot resurrect the old legacy table");
    }
    private static void ScanAndWindow(string root)
    {
        string home=Path.Combine(root,"home"),store=Path.Combine(root,"store");Directory.CreateDirectory(home);Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",store);ResetStore();
        string sessions=Path.Combine(home,".codex","sessions");Directory.CreateDirectory(sessions);
        DateTime start=DateTime.UtcNow.AddMinutes(-15);StringBuilder lines=new StringBuilder();
        lines.AppendLine(Frame("session_meta",O("id","many","model","gpt-6.1-sol","cwd","C:/fixture"),start));
        for(int i=0;i<700;i++)lines.AppendLine(Frame("token_usage_record",O("response_id","r"+i,"actual_service_tier",i%2==0?"fast":"standard","usage",Usage()),start.AddSeconds(i)));
        File.WriteAllText(Path.Combine(sessions,"many.jsonl"),lines.ToString());
        ScanResult scan=new TokenScanner(home,false).Run();
        Check(scan.RecentRequests.Count==500&&scan.WindowRequests.Count==700&&scan.WindowRequestsComplete,"full two-hour telemetry is independent from 500-row UI limit");
        Check(scan.RecentRequests[0].SessionId=="many"&&scan.RecentRequests[0].Workspace=="C:/fixture","recent requests retain attribution");
        ScanResult cached=LocalUsageStore.TryLoadInstantSnapshot(home);
        Check(cached!=null&&cached.WindowRequestsComplete&&cached.WindowRequests.Count==700,"restart instant snapshot retains complete time window");
        Check(cached.Records.Count==2,"tier buckets stay separate after SQLite/snapshot persistence");
        string generic=Path.Combine(home,".commandcode","projects");Directory.CreateDirectory(generic);string file=Path.Combine(generic,"bad.jsonl");
        File.WriteAllText(file,"{\"usage\": broken}\n{\"usage\":{\"input_tokens\":10,\"output_tokens\":2},\"timestamp\":\"2026-11-17T01:00:00Z\"}\n");
        scan=new TokenScanner(home,false).Run();ProviderHealth health=null;foreach(ProviderHealth h in scan.ProviderHealth)if(h.Tool=="Command Code")health=h;
        Check(health!=null&&health.Status=="error"&&health.SkippedRecords>0&&!scan.WindowRequestsComplete,"malformed rows are visible and cannot claim a complete time window");
        cached=LocalUsageStore.TryLoadInstantSnapshot(home);Check(!cached.WindowRequestsComplete&&cached.WindowRequests.Count==scan.WindowRequests.Count&&cached.WindowRequests.Count>=700,"partial snapshot preserves its full available window and incompleteness flag");
        Check(!LocalUsageStore.LoadAllCursors().ContainsKey(file),"malformed file never advances its successful cursor");
        foreach(RecordBucket r in scan.Records)if(r.Tool=="Command Code")Check(r.Model=="unknown","missing generic model is not invented");
        File.WriteAllText(file,"{\"model\":\"gpt-6.1-sol\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2},\"actual_service_tier\":\"fast\",\"timestamp\":\"2026-11-17T01:00:00Z\"}\n");
        scan=new TokenScanner(home,false).Run();Check(LocalUsageStore.LoadAllCursors().ContainsKey(file),"fixed source is retried and checkpointed");
        int turns=0;foreach(RecordBucket r in scan.Records)if(r.Tool=="Command Code")turns+=r.Turns;
        File.AppendAllText(file,"{\"usage\": broken}\n");scan=new TokenScanner(home,false).Run();int preserved=0;foreach(RecordBucket r in scan.Records)if(r.Tool=="Command Code")preserved+=r.Turns;
        Check(preserved==turns,"a later malformed source preserves its last successful ledger");
    }
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_TierTests_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",Path.Combine(root,"config"));
        try {Parser();SharedDesktopFixture();CounterResetEpoch();UnchangedCumulativeTotal();ReplayedResponseModel();CompactionCheckpoints();EmbeddedCompaction();Cloud();Migration(root);ScanAndWindow(root);Fixture(root);PriceSupportLimits(root);Console.WriteLine("PASS: "+checks+" service tier assertions");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
