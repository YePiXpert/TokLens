using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using TokLens;
internal static class UsagePresentationTests
{
    private static int checks;
    private static void Check(bool ok,string message) { checks++;if(!ok)throw new Exception(message); }
    private static void ReferenceEvidence(string root, JavaScriptSerializer serializer)
    {
        RecordBucket official=new RecordBucket { Date="2026-11-17",Tool="OpenAI Codex",Model="gpt-6-astra",Provider="custom",RequestedServiceTier="fast",ActualServiceTier="unknown",TierEvidence="thread_settings:requested",CostBasis="estimated_official_reference",PricingVersion="reference|provider:openai|model:gpt-6-astra|tier:fast|asof:2026-11-17|official-2026-11-17|https://example.test/pricing",Turns=1,FreshInput=1000,CostUsd=.02 };
        RecordBucket current=official.Clone();current.Date="2026-11-01";current.CostBasis="estimated_current_reference";
        RecordBucket actual=current.Clone();actual.ActualServiceTier="standard";actual.PricingVersion=actual.PricingVersion.Replace("tier:fast","tier:standard");
        RecordBucket unknown=actual.Clone();unknown.RequestedServiceTier="unknown";unknown.ActualServiceTier="unknown";unknown.CostUsd=0;
        Check(UsagePresentation.CostLabel(official)=="Fast 官方参考估值 · 渠道未报价 · 实际档位未核实","provider reference names the requested tier without claiming the channel billed it");
        Check(UsagePresentation.CostLabel(current)=="Fast 现行参考估值 · 非历史报价 · 实际档位未核实","current quote applied to history exposes both date and delivery uncertainty");
        Check(UsagePresentation.CostLabel(actual)=="Standard 现行参考估值 · 非历史报价","an actual tier overrides a conflicting requested tier in reference labels");
        Check(UsagePresentation.CostLabel(unknown)=="Standard 现行参考估值 · 非历史报价 · 档位未知"&&UsagePresentation.TierLabel(unknown)=="档位未知","unknown execution tier remains unknown while the monetary reference is Standard");
        List<RecordBucket> references=new List<RecordBucket>{official,current,actual,unknown};
        List<UsageTierTotal> groups=UsagePresentation.Group(references);
        long unpriced=0;double total=0;foreach(UsageTierTotal group in groups){unpriced+=group.UnpricedRequests;total+=group.CostUsd;}
        Check(groups.Count==4&&unpriced==0&&Math.Abs(total-.06)<.00000001,"reference basis and actual/requested uncertainty stay separated in amount groups");
        Check(UsagePresentation.ExportMoney(official.CostUsd,official)!=null&&UsagePresentation.ExportMoney(0,unknown)!=null,"priced reference and explicit zero reference export as numbers");
        string csv=ReportExporter.BuildCsvReport(references,7);
        Check(csv.Contains("estimated_official_reference")&&csv.Contains("estimated_current_reference")&&csv.Contains("custom")&&csv.Contains("asof:2026-11-17"),"CSV keeps the observed provider distinct from reference provenance");
        NodeSnapshot source=new NodeSnapshot {MachineId="reference-test"};source.Records.AddRange(references);
        NodeSnapshot restored=SyncService.DeserializeSnapshot(SyncService.SerializeSnapshot(source),true);
        Check(restored.Records.Count==4&&restored.Records[0].Provider=="custom"&&restored.Records[0].ActualServiceTier=="unknown"&&restored.Records[0].CostBasis==official.CostBasis&&restored.Records[0].PricingVersion==official.PricingVersion&&Math.Abs(restored.Records[0].CostUsd-.02)<.00000001,"cloud roundtrip preserves reference money, original provider and evidence");
        string path=Path.Combine(root,"reference.html");HtmlExporter.ExportHtmlReport(references,null,7,path);string page=File.ReadAllText(path);
        int start=page.IndexOf("const DATA = ",StringComparison.Ordinal)+13,end=page.IndexOf(";",start,StringComparison.Ordinal);
        Dictionary<string,object> data=(Dictionary<string,object>)serializer.DeserializeObject(page.Substring(start,end-start));IList rows=(IList)data["rows"];
        for(int i=0;i<references.Count;i++)
        {
            IList row=(IList)rows[i];
            Check(row[8]!=null&&Convert.ToString(row[22])==UsagePresentation.CostLabel(references[i])&&Convert.ToString(row[14])=="custom"&&Convert.ToString(row[19])==references[i].PricingVersion,"HTML uses the shared label and preserves reference metadata, row "+i);
        }
        Check(page.Contains("basisLabel: r[22]")&&page.Contains("esc(m.basisLabel)"),"HTML renders the shared cost label instead of an independent basis switch");
    }
    [STAThread]
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_PresentationTests_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",root);
        try {
            RecordBucket delivered=new RecordBucket { Date="2026-11-17",Tool="OpenAI Codex",Model="gpt-6.1-sol",Provider="openai",RequestedServiceTier="fast",ActualServiceTier="fast",TierEvidence="response:actual",CostBasis="estimated",PricingVersion="official-2026-11-17",Turns=2,FreshInput=1000,CostUsd=.02,StandardEquivalentUsd=.01,ServiceTierPremiumUsd=.01 };
            RecordBucket requested=delivered.Clone();requested.ActualServiceTier="unknown";requested.TierEvidence="request:requested";requested.CostBasis="estimated_requested_tier";requested.Turns=1;
            RecordBucket historical=delivered.Clone();historical.RequestedServiceTier="unknown";historical.ActualServiceTier="unknown";historical.CostBasis="legacy_unknown";historical.Turns=3;
            RecordBucket reported=delivered.Clone();reported.CostBasis="reported";reported.CostUsd=.5;
            List<RecordBucket> records=new List<RecordBucket>{delivered,requested,historical,reported};
            Check(UsagePresentation.Group(records).Count==4,"actual, requested, unknown and reported amounts remain separate groups");
            Check(UsagePresentation.TierLabel(requested).Contains("已请求")&&UsagePresentation.TierLabel(historical)=="档位未知","unknown cannot masquerade as standard or delivered Fast");
            Check(UsagePresentation.CostLabel(historical).Contains("来源未记录"),"legacy money has no invented provenance");
            UsageStatusPanel coverage=new UsageStatusPanel(null,null);
            coverage.Render(new ScanResult(),new[]{historical},false,7,"");
            Check(coverage.SummaryText.Contains("档位未知 3")&&!coverage.SummaryText.Contains("Fast"),"no tier evidence cannot be presented as zero Fast requests");
            coverage.Render(new ScanResult(),new[]{requested,historical},false,7,"");
            Check(coverage.SummaryText.Contains("Fast（已请求） 1")&&coverage.SummaryText.Contains("档位未知 3"),"known requested Fast and unresolved coverage are both visible");
            string csv=ReportExporter.BuildCsvReport(records,7);
            Check(csv.Contains("实际档位")&&csv.Contains("requested")&&csv.Contains("official-2026-11-17"),"CSV carries tier evidence and price revision");
            string html=Path.Combine(root,"report.html");HtmlExporter.ExportHtmlReport(records,null,7,html);string page=File.ReadAllText(html);
            Check(page.Contains("estimated_requested_tier")&&page.Contains("official-2026-11-17")&&page.Contains("JSON.stringify([r[1], r[2], r[13]"),"HTML preserves tier grouping and provenance");
            Dictionary<string,object> json=new Dictionary<string,object>();
            typeof(CliRunner).GetMethod("AddUsageEvidence",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{json,requested});
            Check((string)json["actual_service_tier"]=="unknown"&&(string)json["requested_service_tier"]=="fast","CLI export distinguishes requested and actual tier");
            Check(!json.ContainsKey("Workspace")&&!json.ContainsKey("SessionId"),"aggregate metadata does not leak local project paths");
            RecordBucket unpriced=delivered.Clone();unpriced.Model="no-such-model";unpriced.CostBasis="unpriced";unpriced.CostUsd=0;unpriced.RateLabel="未定价";unpriced.StandardEquivalentUsd=null;unpriced.ServiceTierPremiumUsd=null;
            reported.RateLabel="未定价";reported.StandardEquivalentUsd=null;reported.ServiceTierPremiumUsd=null;
            Check(UsagePresentation.Group(new[]{reported})[0].UnpricedRequests==0,"a reported amount is still known when no API rate exists");
            Check(UsagePresentation.ExportMoney(0,unpriced)==null,"unpriced export is unknown, not zero");
            string missingCsv=ReportExporter.BuildCsvReport(new[]{unpriced},7);
            Check(missingCsv.Contains(",,,\"Fast")&&!missingCsv.Contains("0.000000"),"CSV leaves unpriced monetary cells empty and keeps tier evidence");
            records.Add(unpriced);
            Type datasetType=typeof(CliRunner).GetNestedType("AggregatedDataset",BindingFlags.NonPublic);
            object dataset=Activator.CreateInstance(datasetType,true);
            datasetType.GetField("AllRecords").SetValue(dataset,records);
            string aggregate=(string)typeof(CliRunner).GetMethod("BuildAggregateJsonSnapshot",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{dataset});
            JavaScriptSerializer serializer=new JavaScriptSerializer();
            Dictionary<string,object> aggregateMap=(Dictionary<string,object>)serializer.DeserializeObject(aggregate);
            IList exported=(IList)aggregateMap["records"], byModel=(IList)aggregateMap["by_model"];
            Check(((Dictionary<string,object>)exported[4])["cost_usd"]==null,"CLI record JSON retains null for unpriced cost");
            Dictionary<string,object> summary=(Dictionary<string,object>)aggregateMap["summary"];
            Check(!(bool)summary["costs_complete"]&&Convert.ToInt64(summary["unpriced_requests"])==unpriced.Turns,"CLI summary marks partial price coverage");
            Check(byModel.Count==5,"CLI model summary keeps actual, requested, unknown, reported and unpriced groups separate");
            Check(Convert.ToInt32(summary["model_count"])==2,"model count is distinct models, not tier groups");
            datasetType.GetField("AllRecords").SetValue(dataset,new List<RecordBucket>{unpriced});
            aggregateMap=(Dictionary<string,object>)serializer.DeserializeObject((string)typeof(CliRunner).GetMethod("BuildAggregateJsonSnapshot",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{dataset}));
            Check(((Dictionary<string,object>)aggregateMap["summary"])["cost_usd"]==null,"all-unpriced CLI total is unknown, not zero");
            datasetType.GetField("AllRecords").SetValue(dataset,new List<RecordBucket>{delivered,delivered.Clone()});
            aggregateMap=(Dictionary<string,object>)serializer.DeserializeObject((string)typeof(CliRunner).GetMethod("BuildAggregateJsonSnapshot",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{dataset}));
            Check(Math.Abs(Convert.ToDouble(((Dictionary<string,object>)((IList)aggregateMap["by_model"])[0])["service_tier_premium_usd"])-.02)<.0000001,"model comparison premiums aggregate every record");
            HtmlExporter.ExportHtmlReport(records,null,7,html);page=File.ReadAllText(html);
            int dataStart=page.IndexOf("const DATA = ",StringComparison.Ordinal)+13;
            int dataEnd=page.IndexOf(";",dataStart,StringComparison.Ordinal);
            Dictionary<string,object> htmlData=(Dictionary<string,object>)serializer.DeserializeObject(page.Substring(dataStart,dataEnd-dataStart));
            IList htmlRows=(IList)htmlData["rows"];
            Check(((IList)htmlRows[4])[8]==null&&Convert.ToString(((IList)htmlRows[1])[16])=="unknown","HTML data preserves unpriced null and requested-only actual unknown");
            Check(page.Contains("实际档位未核实")&&page.Contains("csvCell(r[18])")&&page.Contains("unpricedTurns"),"HTML display and its CSV export retain evidence and incomplete coverage");
            ReferenceEvidence(root,serializer);
            Console.WriteLine("PASS: "+checks+" presentation/export assertions");return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
