using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using TokLens;

internal static class ProductAccountingTests
{
    private static int checks;
    private static void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static Dictionary<string,object> Obj(params object[] pairs){Dictionary<string,object>d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d;}
    private static SubscriptionPlanConfig Plan(string provider,string currency,double amount,int months)
    { return new SubscriptionPlanConfig {Id="record",EcoId=provider,AccountLabel="main",PlanName="paid plan",Currency=currency,Amount=amount,BillingMonths=months,StartDate="2026-11-01",EndDate="2027-10-31",Enabled=true,FeeConfirmed=true}; }
    private static void Subscriptions(string root)
    {
        SubscriptionPlanConfig old=SubscriptionCatalog.ReadConfig(Obj("eco_id","coding_plan","enabled",true,"plan_name","旧版混合项","monthly_usd",20));
        Check(!old.FeeConfirmed&&!SubscriptionCatalog.IsActive(old,new DateTime(2026,11,14)),"legacy guessed subscription cannot become a payment");
        old=SubscriptionCatalog.ReadConfig(SubscriptionCatalog.WriteConfig(old));
        Check(old.MonthlyUsd==20&&old.PlanName=="旧版混合项"&&!old.FeeConfirmed,"legacy price and name survive migration for review");
        SubscriptionPlanConfig yearly=Plan("kimi","CNY",948,12);
        Check(SubscriptionCatalog.MonthlyUsd(yearly,7.9)==10,"annual total converts to monthly once, in the original currency");
        Check(SubscriptionCatalog.IsActive(yearly,new DateTime(2026,11,1))&&SubscriptionCatalog.IsActive(yearly,new DateTime(2027,10,31))&&!SubscriptionCatalog.IsActive(yearly,new DateTime(2027,11,1)),"inclusive billing dates and expiry");
        SubscriptionPlanConfig annualUsd=Plan("claude","USD",200,12);
        Check(Math.Abs(SubscriptionCatalog.MonthlyUsd(annualUsd,7.9)-200.0/12)<1e-9,"annual fee is not rounded to advertised monthly price");
        SubscriptionPlanConfig free=Plan("openai","USD",0,1);
        Check(SubscriptionCatalog.IsActive(free,new DateTime(2026,11,14))&&SubscriptionCatalog.MonthlyUsd(free,7.9)==0,"free plans do not get replaced by a paid default");
        Check(!SubscriptionCatalog.ValidAmount(double.NaN)&&!SubscriptionCatalog.ValidAmount(double.PositiveInfinity)&&!SubscriptionCatalog.ValidAmount(-1),"invalid fees rejected");
        Dictionary<string,object> broken=SubscriptionCatalog.WriteConfig(yearly);broken["amount"]="not-a-number";
        Check(!SubscriptionCatalog.ReadConfig(broken).FeeConfirmed,"malformed amount never becomes verified zero");
        broken=SubscriptionCatalog.WriteConfig(yearly);broken["billing_months"]=0;
        Check(!SubscriptionCatalog.ReadConfig(broken).FeeConfirmed,"malformed period never becomes a verified monthly fee");
        SubscriptionPlanConfig next=yearly.Clone();next.AccountLabel=" MAIN ";next.StartDate="2027-10-31";
        Check(SubscriptionCatalog.Overlaps(yearly,next),"same account overlap is detected across aliases and same-day boundaries");
        next.StartDate="2027-11-01";next.EndDate="2028-10-31";Check(!SubscriptionCatalog.Overlaps(yearly,next),"renewal after final day does not double count");
        next.StartDate="2026-11-01";next.AccountLabel="work";Check(!SubscriptionCatalog.Overlaps(yearly,next),"independent accounts can coexist");
        Check(!SubscriptionCatalog.Overlaps(Plan("tencent","CNY",39,1),Plan("tencent-hy","CNY",28,1)),"independent Tencent product subscriptions can coexist on the same account");
        SyncConfig config=new SyncConfig();config.Subscriptions.Add(old);config.Subscriptions.Add(yearly);config.Subscriptions.Add(free);SyncService.SaveConfig(config);
        SyncConfig loaded=SyncService.LoadConfig();Check(loaded.Subscriptions.Count==3&&loaded.Subscriptions[1].Amount==948&&loaded.Subscriptions[1].Currency=="CNY"&&loaded.Subscriptions[1].BillingMonths==12&&loaded.Subscriptions[1].AccountLabel=="main","disk config round trip preserves actual fee, currency, account and cycle");
        string cfgPath=SyncService.ConfigFilePath,cfgSaved=File.ReadAllText(cfgPath);
        File.WriteAllText(cfgPath,"{\"minimize_to_tray\":false}");Check(SyncService.LoadConfig().MinimizeToTray,"closing the window keeps TokLens resident by default, also after upgrading from the old opt-in setting");
        File.WriteAllText(cfgPath,"{\"close_to_tray\":false}");Check(!SyncService.LoadConfig().MinimizeToTray,"an explicit choice to quit on close is kept");
        Check(new SyncConfig().MinimizeToTray,"new configs stay resident in the tray when the window is closed");
        File.WriteAllText(cfgPath,cfgSaved);
        string priceCache=PricingEngine.CloudPriceCachePath;Directory.CreateDirectory(Path.GetDirectoryName(priceCache));
        File.WriteAllText(priceCache,"{\"schema\":\"v2.2\",\"models\":{},\"context\":{\"claude-opus-5-5\":1000000,\"gpt-6-astra\":400000}}");
        Check(priceCache.StartsWith(SyncService.AppConfigDir),"price cache follows the config directory");
        Check(PricingEngine.ContextWindowFor("claude-opus-5-5")==1000000&&PricingEngine.ContextWindowFor("anthropic/claude-opus-5-5")==1000000,"context limits come from the price library, provider prefixes ignored");
        Check(PricingEngine.ContextWindowFor("claude-opus-5-5-20261101")==1000000&&PricingEngine.ContextWindowFor("claude-opus-5-5-thinking")==1000000,"dated and suffixed variants reuse the base model limit");
        Check(PricingEngine.ContextWindowFor("mystery-model")==0,"unknown models report no limit instead of a guess");
        File.Delete(priceCache);
        SyncConfig clone=new SyncConfig();clone.CopyFrom(loaded);
        Check(clone.Subscriptions[1].StartDate=="2026-11-01"&&clone.Subscriptions[1].FeeConfirmed,"settings copies preserve subscription history");
        HashSet<string> ids=new HashSet<string>();bool glm=false,kimi=false,ali=false,mini=false;
        foreach(SubscriptionOffer o in SubscriptionCatalog.Offers){Check(ids.Add(o.Id),"unique stable offer ID "+o.Id);Check(o.Months>0&&(!o.Amount.HasValue||SubscriptionCatalog.ValidAmount(o.Amount.Value)),"valid catalogue amount "+o.Id);glm|=o.ProviderId=="glm";kimi|=o.ProviderId=="kimi";ali|=o.ProviderId=="aliyun";mini|=o.ProviderId=="minimax";}
        Check(glm&&kimi&&ali&&mini,"domestic providers are independently selectable");
        Check(SubscriptionCatalog.UsageProvider("Cursor","claude-opus-5-5")=="cursor"&&SubscriptionCatalog.UsageProvider("Claude Code","claude-opus-5-5")=="claude","payback: requests inside a subscribed tool belong to that tool, others to the model vendor");
        Check(SubscriptionCatalog.UsageProvider("Claude Code","glm-5.3")=="glm"&&SubscriptionCatalog.UsageProvider("OpenAI Codex","gpt-6-astra")=="openai","payback: model vendor decides, not the client tool");
        Check(SubscriptionCatalog.UsageProvider("OpenCode","mystery-router-model")==null&&!SubscriptionCatalog.CoversUsage("other")&&SubscriptionCatalog.CoversUsage("kimi"),"payback: unknown models and custom providers are not matched");
        Check(!new SyncConfig().ShowSubscriptions,"subscriptions and payback are off by default");
    }
    private static void Timing(string root)
    {
        Check(Math.Abs(FormatterUtil.ComputeTps(824,10000)-82.4)<1e-9,"output-only TPS uses recorded milliseconds");
        Check(FormatterUtil.ComputeTps(1,0)==0&&FormatterUtil.ComputeTps(0,1000)==0,"missing duration or no output cannot produce speed");
        Check(FormatterUtil.ComputeTps(10,10)==1000&&FormatterUtil.ComputeTps(600,1200000)==.5,"real short and long durations are not suppressed by arbitrary thresholds");
        Check(UsageTiming.PairedDuration("2026-11-14T10:00:00Z","2026-11-14T10:00:10Z")==10000,"paired UTC timestamps");
        Check(UsageTiming.PairedDuration(null,"2026-11-14T10:00:10Z")==0&&UsageTiming.PairedDuration("garbage","2026-11-14T10:00:10Z")==0,"missing timestamps do not use file mtime");
        Check(UsageTiming.PairedDuration("2026-11-14T10:00:10Z","2026-11-14T10:00:00Z")==0,"reversed clock cannot make TPS");
        UsageEvent ev=new UsageEvent("tool","model","2026-11-14",500,824,90000,0,0,1,"id",1,10000);
        string cache=LocalUsageStore.SerializeEvents(new List<UsageEvent>{ev});UsageEvent restored=LocalUsageStore.DeserializeEvents(cache)[0];Check(!UsageTiming.IsMeasured(restored.TimingSource),"legacy or inferred duration is not upgraded to measured");
        ev.TimingSource="request";restored=LocalUsageStore.DeserializeEvents(LocalUsageStore.SerializeEvents(new List<UsageEvent>{ev}))[0];Check(restored.TimingSource=="request"&&restored.Clone().TimingSource=="request","measured timing survives cache and clone");
        string home=Path.Combine(root,"timing-home");string dir=Path.Combine(home,".claude","projects","project");Directory.CreateDirectory(dir);
        List<string> lines=new List<string>();
        lines.Add(Json.Serialize(Obj("type","user","timestamp","2026-11-14T10:00:00Z")));
        lines.Add(Json.Serialize(Obj("type","assistant","timestamp","2026-11-14T10:00:10Z","requestId","r1","message",Obj("id","m1","model","glm-5.3","usage",Obj("input_tokens",100,"output_tokens",824)))));
        lines.Add(Json.Serialize(Obj("type","assistant","timestamp","2026-11-14T10:00:20Z","requestId","r2","durationMs",10000,"message",Obj("id","m2","model","glm-5.3","usage",Obj("input_tokens",100,"output_tokens",824)))));
        File.WriteAllLines(Path.Combine(dir,"timed.jsonl"),lines);
        ScanResult scan=new TokenScanner(home,false).Run();int measured=0,unknown=0;foreach(RecentRequestRecord r in scan.RecentRequests){if(r.DurationMs==10000&&r.TimingSource=="request")measured++;if(r.DurationMs==0)unknown++;}
        Check(measured==1&&unknown==1,"Claude log gap remains unknown while explicit per-request elapsed time produces TPS");
        ScanResult cached=LocalUsageStore.TryLoadInstantSnapshot(home);Check(cached!=null&&cached.RecentRequests.Count==2,"request timings survive snapshot reload");
        DateTime now=new DateTime(2026,11,14,10,0,21,DateTimeKind.Utc);HudTelemetry h=HudTelemetry.FromScan(scan,now,false,7);
        Check(h.Model=="glm-5.3"&&Math.Abs(h.Tps-82.4)<1e-9,"HUD chooses latest local request, independently of dashboard filters");
        Check(h.ContextTokens==100&&h.Latest.Count==2&&h.Latest[0].TimestampMs>h.Latest[1].TimestampMs,"HUD keeps recent requests newest first with the latest context size");
        Check(Math.Abs(h.SparkHour[29]-h.LastHourUsd)<1e-9&&Math.Abs(h.SparkTwoHours[23]-h.LastHourUsd)<1e-9&&Math.Abs(h.Last15Usd-h.LastHourUsd)<1e-9&&h.TodayRequests>=2,"HUD spend curves bucket requests by age");
        Check(h.IsRecent(now)&&!h.IsRecent(now.AddMinutes(6)),"finished request expires to idle instead of pretending to stream");
        h.TimingSource="";Check(h.Tps==0,"HUD refuses unverified timing even if a duration exists");
        scan.Records.Add(new RecordBucket {Date=now.ToString("yyyy-MM-dd"),Model="unknown",Turns=3,RateLabel="未定价"});
        h=HudTelemetry.FromScan(scan,now,false,7);Check(h.UnpricedRequests>=3,"missing prices are explicitly reported instead of silently treated as free");
    }
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_ProductTests_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",Path.Combine(root,"store"));
        try{Subscriptions(root);Timing(root);Console.WriteLine("PASS: "+checks+" product accounting assertions");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
