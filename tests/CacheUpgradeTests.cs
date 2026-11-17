using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using TokLens;

internal static class CacheUpgradeTests
{
    private static int checks;
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static readonly DateTime Start=new DateTime(2026,11,17,1,0,0,DateTimeKind.Utc);
    private static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    private static Dictionary<string,object> Obj(params object[] fields)
    {Dictionary<string,object> value=new Dictionary<string,object>();for(int i=0;i<fields.Length;i+=2)value[(string)fields[i]]=fields[i+1];return value;}
    private static string Frame(string kind,object payload,int second)
    {return Json.Serialize(Obj("type",kind,"payload",payload,"timestamp",Start.AddSeconds(second).ToString("o",CultureInfo.InvariantCulture)));}
    private static object Usage(){return Obj("input_tokens",100,"output_tokens",20,"cached_input_tokens",25);}
    private static string Request(string id,int second)
    {return Frame("token_usage_record",Obj("response_id",id,"usage",Usage()),second);}
    private static string Settings(string tier,int second)
    {return Frame("event_msg",Obj("type","thread_settings_applied","thread_settings",Obj("model","gpt-6.1-sol","model_provider_id","openai","service_tier",tier)),second);}
    private static UsageEvent OldEvent(string id,int second)
    {
        long epoch=(long)(Start.AddSeconds(second)-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
        return new UsageEvent("OpenAI Codex","gpt-6.1-sol","2026-11-17",75,20,25,0,0,1,"codex-response:"+id,epoch,0){SessionId="upgrade-thread"};
    }
    private static RecordBucket UnknownBucket(string day,int turns)
    {return new RecordBucket {Date=day,Tool="OpenAI Codex",Model="gpt-6.1-sol",Turns=turns,FreshInput=75*turns,Output=20*turns,CacheRead=25*turns,CostUsd=turns,RateLabel="旧标准参考",CostBasis="legacy_unknown"};}
    private static void ResetStore(){typeof(LocalUsageStore).GetField("_memCursors",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,null);}
    private static void Upgrade(string root)
    {
        string home=Path.Combine(root,"home"),sessions=Path.Combine(home,".codex","sessions"),store=Path.Combine(root,"store");
        Directory.CreateDirectory(sessions);Directory.CreateDirectory(store);Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",store);ResetStore();
        string live=Path.Combine(sessions,"live.jsonl"),deleted=Path.Combine(sessions,"deleted.jsonl");
        string text=Frame("session_meta",Obj("id","upgrade-thread","model","gpt-6.1-sol"),0)+"\n"+Request("before",1)+"\n"+Settings("priority",2)+"\n"+Request("fast",3)+"\n"+Settings("default",4)+"\n"+Request("standard",5)+"\n";
        File.WriteAllText(live,text,new UTF8Encoding(false));FileInfo file=new FileInfo(live);
        List<FileCursorEntry> oldCursors=new List<FileCursorEntry>{
            new FileCursorEntry {FileKey=live,FileSize=file.Length,MtimeTicks=file.LastWriteTimeUtc.ToFileTimeUtc(),ByteOffset=file.Length,ExtraMeta="",Events=new List<UsageEvent>{OldEvent("before",1),OldEvent("fast",3),OldEvent("standard",5)}},
            new FileCursorEntry {FileKey=deleted,FileSize=100,MtimeTicks=1,ByteOffset=100,ExtraMeta="",Events=new List<UsageEvent>{OldEvent("deleted",6)}}};
        List<RecordBucket> oldRows=new List<RecordBucket>{UnknownBucket("2026-11-17",4),UnknownBucket("2026-11-16",2)};
        LocalUsageStore.MergeAndSaveDailyArchive(home,oldRows);
        LocalUsageStore.SaveScanBatch(home,oldCursors,new ScanResult{UserHome=home,Records=oldRows});
        using(WinSqlite db=WinSqlite.OpenReadWrite(LocalUsageStore.DbFilePath))db.ExecRequired("UPDATE kv_meta SET v='toklens.store.v2.11' WHERE k='schema';");
        ResetStore();Dictionary<string,FileCursorEntry> migrated=LocalUsageStore.LoadAllCursors();
        Check(!migrated.ContainsKey(live),"upgrade invalidates an unchanged live file's old EOF cursor");
        Check(migrated.ContainsKey(deleted)&&migrated[deleted].Events.Count==1,"upgrade retains the cursor of a deleted source");
        Check(migrated[deleted].Events[0].RequestedServiceTier=="unknown","deleted source is not rewritten from today's Fast setting");
        string[] backups=Directory.GetFiles(store,"*"+LocalUsageStore.UpgradeBackupPrefix+"*.db");Check(backups.Length==1,"upgrade creates exactly one pre-migration backup");
        string backupSchema="";long backupCursors=0;
        using(WinSqlite db=WinSqlite.OpenReadOnly(backups[0]))
        {Check(db!=null&&db.IsHealthy(),"backup is a readable SQLite database");db.Query("SELECT v FROM kv_meta WHERE k='schema'",delegate(WinSqlite.SqliteRow row){backupSchema=row.GetString(0);});db.Query("SELECT COUNT(*) FROM file_cursors",delegate(WinSqlite.SqliteRow row){backupCursors=row.GetInt64(0);});}
        Check(backupSchema=="toklens.store.v2.11"&&backupCursors==2,"backup retains both old cursors and the original schema");
        ScanResult scan=new TokenScanner(home,false).Run();int fast=0,standard=0,unknown=0,older=0;long tokens=0;
        foreach(RecordBucket r in scan.Records)
        {
            if(r.Date=="2026-11-16"){older+=r.Turns;Check(r.RequestedServiceTier=="unknown","aggregate-only older history stays unknown");continue;}
            tokens+=r.TotalTokens;if(r.RequestedServiceTier=="fast")fast+=r.Turns;else if(r.RequestedServiceTier=="standard")standard+=r.Turns;else unknown+=r.Turns;
        }
        Check(fast==1&&standard==1&&unknown==2&&tokens==480,"replay applies nested settings in order without inventing earlier or deleted tiers");
        Check(older==2,"aggregate-only history without source survives the upgrade");
        Check(scan.ReconciledPartitions.ContainsKey(UsageAccounting.Partition("2026-11-17","OpenAI Codex")),"the source-proved current day is reconciled");
        FileCursorEntry refreshed=LocalUsageStore.LoadAllCursors()[live];Check(refreshed.Events.Count==3&&refreshed.Events[1].RequestedServiceTier=="fast","new cursor persists the recovered Fast classification");
        ScanResult repeated=new TokenScanner(home,false).Run();int repeatedTurns=0;foreach(RecordBucket r in repeated.Records)repeatedTurns+=r.Turns;
        Check(repeatedTurns==6,"repeated scans do not add old unknown history to the recovered breakdown");
        Check(Directory.GetFiles(store,"*"+LocalUsageStore.UpgradeBackupPrefix+"*.db").Length==1,"unchanged schema does not repeatedly back up and reset cursors");
        NodeSnapshot oldNode=new NodeSnapshot {MachineId="upgrade",ScannerRev=30};oldNode.Records.AddRange(oldRows);oldNode.ReconciledPartitions[UsageAccounting.Partition("2026-11-17","OpenAI Codex")]=long.MaxValue-1;
        NodeSnapshot current=new NodeSnapshot {MachineId="upgrade",ScannerRev=UsageAccounting.Revision};current.Records.AddRange(scan.Records);current.ReconciledPartitions=new Dictionary<string,long>(scan.ReconciledPartitions);
        NodeSnapshot merged=SyncService.MergeNodeSnapshotsHighWater(current,oldNode);fast=0;foreach(RecordBucket r in merged.Records)if(r.RequestedServiceTier=="fast")fast+=r.Turns;
        Check(UsageAccounting.Revision>=31&&fast==1,"older scanner summaries cannot undo the recovered Fast partition");
    }
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_CacheUpgrade_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{Upgrade(root);Console.WriteLine("PASS: "+checks+" cache upgrade assertions");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
