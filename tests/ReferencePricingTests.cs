using System;
using System.Collections.Generic;
using System.IO;
using TokLens;

internal static class ReferencePricingTests
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static void Equal(double actual,double expected,string message){Check(Math.Abs(actual-expected)<0.00000001,message+": "+actual+" != "+expected);}
    private static UsageEvent Event(string provider,string date,string model)
    {return new UsageEvent("OpenAI Codex",model,date,100000,100,50000,0,0,1){Provider=provider,RequestedServiceTier="fast",ActualServiceTier="unknown"};}
    private static void Core(string root)
    {
        PricingEngine prices=new PricingEngine(root);double cost,baseline;ModelRate rate;
        UsageEvent ev=Event("custom","2026-11-17","gpt-6-astra");
        prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Equal(cost,2.11,"custom routed Astra uses official Fast reference including cache");
        Equal(ev.StandardEquivalentUsd.Value,1.055,"reference comparison uses official Standard from the same date");
        Equal(ev.ServiceTierPremiumUsd.Value,1.055,"reference premium is comparable");
        Check(ev.Provider=="custom"&&ev.Model=="gpt-6-astra"&&ev.Date=="2026-11-17"&&ev.ActualServiceTier=="unknown"&&ev.RequestedServiceTier=="fast","routing and historical evidence stay intact");
        Check(ev.CostBasis=="estimated_official_reference"&&ev.PricingVersion.StartsWith("reference:official:openai/gpt-6-astra/fast@2026-11-17|"),"reference has explicit provider/model/tier/date provenance");
        ev.ActualServiceTier="standard";prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Equal(cost,1.055,"actual Standard downgrade wins over requested Fast");
        ev.ActualServiceTier="unknown";ev.RequestedServiceTier="unknown";prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Check(ev.CostBasis=="estimated_official_reference"&&!ev.StandardEquivalentUsd.HasValue&&UsagePresentation.CostLabel(ev).Contains("档位未知"),"unknown tier reference is labelled and not given a fabricated premium");
        ev=Event("custom","2026-11-05","gpt-6-astra");prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Equal(cost,2.11,"historical usage gets a separately labelled current reference");
        Check(ev.CostBasis=="estimated_current_reference"&&ev.Date=="2026-11-05"&&ev.Provider=="custom"&&ev.PricingVersion.StartsWith("reference:current:openai/gpt-6-astra/fast@"),"current reference never changes original date or route");
        ev=Event("openai","2026-11-17","gpt-6-astra");prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Check(ev.CostBasis=="estimated_requested_tier","direct dated quote keeps its existing basis");
        ev=Event("unknown","2026-11-17","gpt-6-astra");prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Check(ev.Provider=="unknown"&&ev.CostBasis=="estimated_official_reference","missing route is not rewritten into an observed provider");
        ev.LoggedCostUsd=9.5;prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Check(cost==9.5&&ev.CostBasis=="reported"&&!ev.StandardEquivalentUsd.HasValue,"reported amount wins without added reference premium");
        foreach(string model in new string[]{"gpt-6-astra-high","gpt-6-astra-2026-11-01","claude-opus-5.5"})
        {ServiceTierPriceMatch m=ServiceTierPricing.Resolve("custom",model,"fast","2026-11-17",root,"2026-11-17");Check(m!=null&&m.IsProviderReference,model+" safe alias resolves to its exact known model");}
        foreach(string model in new string[]{"gpt-6-astra-auto","gpt-6-astra-max","gpt-6-astra-fast","gpt-made-up","unknown"})
        {Check(ServiceTierPricing.Resolve("custom",model,"fast","2026-11-17",root,"2026-11-17")==null,model+" cannot borrow a different price");}
        ev=Event("custom","2026-11-05","gpt-5.5");ev.FreshInput=272001;ev.CacheRead=0;prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Check(ev.CostBasis=="unpriced"&&cost==0,"historical reference cannot bypass Fast prompt cap");
        Check(ServiceTierPricing.Resolve("custom","gpt-5.6-sol","fast","2026-11-05",root,"2026-12-01")==null,"expired promotion is not a current reference");
        Check(ServiceTierPricing.Resolve("custom","gpt-6-astra","fast","2026-11-05",root,"2026-11-16")==null,"future verified rule cannot be borrowed early");
    }
    private static void Overrides(string root)
    {
        string home=Path.Combine(root,"custom");Directory.CreateDirectory(Path.Combine(home,".toklens"));
        File.WriteAllText(Path.Combine(home,".toklens","service_tier_prices.json"),"[{\"provider\":\"custom\",\"model\":\"gpt-6-astra\",\"tier\":\"fast\",\"effective_from\":\"2026-11-01\",\"input_per_m\":3,\"output_per_m\":9,\"cache_read_per_m\":1,\"source\":\"https://example.com/relay-price\",\"version\":\"fixture-relay\"}]");
        PricingEngine prices=new PricingEngine(home);UsageEvent ev=Event("custom","2026-11-17","gpt-6-astra");double cost,baseline;ModelRate rate;
        prices.ComputeEventCost(ev,out cost,out baseline,out rate);
        Equal(cost,0.3509,"explicit relay Fast quote wins over official reference");
        Check(ev.CostBasis=="estimated_requested_tier"&&ev.PricingVersion=="fixture-relay|https://example.com/relay-price"&&!ev.StandardEquivalentUsd.HasValue,"relay without own Standard quote does not mix in maker comparison");
        NodeSnapshot snap=new NodeSnapshot();snap.Records=new List<RecordBucket>();
        RecordBucket record=new RecordBucket{Model="gpt-6-astra",FreshInput=100000,Output=100,CostUsd=2.11,CostBasis="estimated_official_reference",PricingVersion="reference:official:openai/gpt-6-astra/standard@2026-11-17|fixture",Provider="custom",RequestedServiceTier="unknown",ActualServiceTier="unknown"};
        snap.Records.Add(record);prices.RecalculateRemoteSnapshot(snap);
        Check(record.CostUsd==2.11&&record.CostBasis=="estimated_official_reference","remote recalculation preserves a complete reference quote and its provenance");
    }
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_ReferencePrices_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",Path.Combine(root,"store"));
        try{Core(root);Overrides(root);Console.WriteLine("PASS: "+checks+" reference pricing assertions");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",null);}
    }
}
