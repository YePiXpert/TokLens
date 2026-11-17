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
            System.Windows.Controls.TextBlock coverageTitle=(System.Windows.Controls.TextBlock)typeof(UsageStatusPanel).GetField("_header",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(coverage);
            Check(coverageTitle.Text.Contains("Fast 暂不可判定"),"no tier evidence cannot be presented as zero Fast requests");
            coverage.Render(new ScanResult(),new[]{requested,historical},false,7,"");
            Check(coverageTitle.Text.Contains("已识别 Fast / Ultrafast 1 次")&&coverageTitle.Text.Contains("档位未知 3 次"),"known requested Fast and unresolved coverage are both visible");
            string csv=ReportExporter.BuildCsvReport(records,7);
            Check(csv.Contains("实际档位")&&csv.Contains("requested")&&csv.Contains("official-2026-11-17"),"CSV carries tier evidence and price revision");
            string markdown=ReportExporter.BuildMarkdownReport(records,false,7);
            Check(markdown.Contains("Fast（已请求）")&&markdown.Contains("历史金额")&&markdown.Contains("额外估值"),"Markdown exposes evidence and premium reference");
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
            string missingMd=UsagePresentation.Markdown(new[]{unpriced},false,7);
            Check(missingMd.Contains("| — |")&&!missingMd.Contains("$0.00"),"Markdown does not display missing price as zero cost");
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
            Check(ReportExporter.BuildMarkdownReport(new[]{unpriced},false,7).Contains("已计价金额: 未计价"),"all-unpriced Markdown headline does not imply a free bill");
            datasetType.GetField("AllRecords").SetValue(dataset,new List<RecordBucket>{delivered,delivered.Clone()});
            aggregateMap=(Dictionary<string,object>)serializer.DeserializeObject((string)typeof(CliRunner).GetMethod("BuildAggregateJsonSnapshot",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{dataset}));
            Check(Math.Abs(Convert.ToDouble(((Dictionary<string,object>)((IList)aggregateMap["by_model"])[0])["service_tier_premium_usd"])-.02)<.0000001,"model comparison premiums aggregate every record");
            HtmlExporter.ExportHtmlReport(records,null,7,html);page=File.ReadAllText(html);
            int dataStart=page.IndexOf("const DATA = ",StringComparison.Ordinal)+13;
            int dataEnd=page.IndexOf(";",dataStart,StringComparison.Ordinal);
            Dictionary<string,object> htmlData=(Dictionary<string,object>)serializer.DeserializeObject(page.Substring(dataStart,dataEnd-dataStart));
            IList htmlRows=(IList)htmlData["rows"];
            Check(((IList)htmlRows[4])[8]==null&&Convert.ToString(((IList)htmlRows[1])[16])=="unknown","HTML data preserves unpriced null and requested-only actual unknown");
            Check(page.Contains("实际未核实")&&page.Contains("csvCell(r[18])")&&page.Contains("unpricedTurns"),"HTML display and its CSV export retain evidence and incomplete coverage");
            RequestInspectorPanel inspector=new RequestInspectorPanel(root,null,null);
            LiveRequestRowView request=new LiveRequestRowView {Date="2026-11-17",Tool="OpenAI Codex",Model="gpt-6.1-sol",Provider="openai",RequestedServiceTier="fast",ActualServiceTier="fast",CostBasis="reported",CostUsd=9,FreshInput=1000,CacheRead=500,CacheWrite=100,Output=200};
            MethodInfo forensics=typeof(RequestInspectorPanel).GetMethod("BuildForensicsJson",BindingFlags.NonPublic|BindingFlags.Instance);
            Dictionary<string,object> requestMap=(Dictionary<string,object>)serializer.DeserializeObject((string)forensics.Invoke(inspector,new object[]{request}));
            Dictionary<string,object> requestCost=(Dictionary<string,object>)requestMap["cost"];
            Check(Math.Abs(Convert.ToDouble(requestCost["cache_saved_usd"])-.0018)<.0000001,"inspector JSON compares like-for-like reference prices, not historical reported amount");
            Check(requestCost["standard_equivalent_usd"]==null&&requestCost["service_tier_premium_usd"]==null,"inspector does not invent premium for reported charges");
            request.Model="no-such-model";request.CostBasis="unpriced";request.CostUsd=0;
            requestMap=(Dictionary<string,object>)serializer.DeserializeObject((string)forensics.Invoke(inspector,new object[]{request}));
            requestCost=(Dictionary<string,object>)requestMap["cost"];
            Check(requestCost["recorded_or_estimated_usd"]==null&&requestCost["recalculated_reference_usd"]==null&&requestCost["cache_saved_usd"]==null,"inspector JSON missing price remains null throughout");
            Console.WriteLine("PASS: "+checks+" presentation/export assertions");return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
