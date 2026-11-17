using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace TokLens
{
    // These fields describe evidence, not account entitlement. Token counters are never multiplied.
    public class UsageRecordMetadata
    {
        public string Provider { get; set; }
        public string SessionId { get; set; }
        public string Workspace { get; set; }
        public string RequestedServiceTier { get; set; }
        public string ActualServiceTier { get; set; }
        public string TierEvidence { get; set; }
        public string CostBasis { get; set; }
        public string PricingVersion { get; set; }
        public double? StandardEquivalentUsd { get; set; }
        public double? ServiceTierPremiumUsd { get; set; }
        public long ActualContextLimit { get; set; }
        public string ContextLimitEvidence { get; set; }
        public string ServiceTierLabel { get { return UsagePresentation.TierLabel(this); } }
        public string CostBasisLabel { get { return UsagePresentation.CostLabel(this); } }
        public UsageRecordMetadata()
        {
            Provider="unknown"; SessionId=""; Workspace=""; RequestedServiceTier="unknown";
            ActualServiceTier="unknown"; TierEvidence=""; CostBasis="legacy_unknown";
            PricingVersion=""; ContextLimitEvidence="";
        }
        public void CopyMetadataTo(UsageRecordMetadata target)
        {
            target.Provider=Provider; target.SessionId=SessionId; target.Workspace=Workspace;
            target.RequestedServiceTier=RequestedServiceTier; target.ActualServiceTier=ActualServiceTier;
            target.TierEvidence=TierEvidence; target.CostBasis=CostBasis; target.PricingVersion=PricingVersion;
            target.StandardEquivalentUsd=StandardEquivalentUsd;target.ServiceTierPremiumUsd=ServiceTierPremiumUsd;
            target.ActualContextLimit=ActualContextLimit; target.ContextLimitEvidence=ContextLimitEvidence;
        }
    }

    public static class UsageServiceTier
    {
        public static string Normalize(string value)
        {
            switch((value??"").Trim().ToLowerInvariant())
            {
                case "standard": case "default": return "standard";
                case "fast": case "priority": return "fast";
                case "ultrafast": return "ultrafast";
                case "flex": return "flex";
                default: return "unknown";
            }
        }
        public static string Effective(UsageRecordMetadata value)
        {
            if(value==null)return "unknown";
            string actual=Normalize(value.ActualServiceTier);
            return actual!="unknown" ? actual : Normalize(value.RequestedServiceTier);
        }
        public static string InferProvider(string model)
        {
            string m=(model??"").ToLowerInvariant();
            if(m.StartsWith("gpt-")||m.StartsWith("o1")||m.StartsWith("o3")||m.StartsWith("o4"))return "openai";
            if(m.StartsWith("claude-"))return "anthropic";
            if(m.StartsWith("gemini-"))return "google";
            return "unknown";
        }
        private static string S(Dictionary<string,object> d,string key)
        { object v;return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToString(v,CultureInfo.InvariantCulture):null; }
        private static Dictionary<string,object> D(Dictionary<string,object> d,string key)
        { object v;return d!=null&&d.TryGetValue(key,out v)?v as Dictionary<string,object>:null; }
        public static void ReadEvidence(UsageRecordMetadata target,Dictionary<string,object> data,string source)
        {
            if(target==null||data==null)return;
            Dictionary<string,object> response=D(data,"response");
            string actual=Normalize(S(data,"actual_service_tier")??S(response,"service_tier"));
            string requestedRaw=S(data,"requested_service_tier")??S(data,"service_tier");
            string requested=Normalize(requestedRaw);
            if(requested=="unknown")
            {
                string fast=S(data,"fast_mode");
                if(string.Equals(fast,"true",StringComparison.OrdinalIgnoreCase))requested="fast";
                else if(string.Equals(fast,"false",StringComparison.OrdinalIgnoreCase))requested="standard";
            }
            if(requested!="unknown"||requestedRaw!=null)
            {target.RequestedServiceTier=requested;if(Normalize(target.ActualServiceTier)=="unknown")target.TierEvidence=source+":requested";}
            if(actual!="unknown") {target.ActualServiceTier=actual;target.TierEvidence="response:actual";}
            string provider=S(data,"provider")??S(data,"model_provider")??S(data,"model_provider_id");
            if(!string.IsNullOrWhiteSpace(provider))target.Provider=provider.ToLowerInvariant();
            string cwd=S(data,"cwd")??S(data,"workspace"); if(!string.IsNullOrWhiteSpace(cwd))target.Workspace=cwd;
            string session=S(data,"session_id")??S(data,"conversation_id");if(!string.IsNullOrWhiteSpace(session))target.SessionId=session;
            long limit;
            if(long.TryParse(S(data,"model_context_window")??S(data,"context_window")??S(data,"token_context_window"),out limit)&&limit>0)
            {target.ActualContextLimit=limit;target.ContextLimitEvidence=source+":context_window";}
        }
        public static string DimensionKey(UsageRecordMetadata value)
        {return Normalize(value.RequestedServiceTier)+"||"+Normalize(value.ActualServiceTier)+"||"+(value.CostBasis??"legacy_unknown")+"||"+(value.PricingVersion??"")+"||"+(value.Provider??"unknown");}
        public static string BucketKey(RecordBucket value)
        {return UsageAccounting.Partition(value.Date,value.Tool)+"||"+(value.Model??"")+"||"+DimensionKey(value);}
        public static string GroupKey(RecordBucket value)
        {return UsageAccounting.Partition(value.Date,value.Tool)+"||"+(value.Model??"");}
        public static bool PreferCurrentGroup(List<RecordBucket> current,List<RecordBucket> historical)
        {
            if(current==null||current.Count==0)return false;if(historical==null||historical.Count==0)return true;
            long ct=0,ht=0;double cn=0,hn=0;
            foreach(RecordBucket r in current){ct+=r.Turns;cn+=r.TotalTokens;}
            foreach(RecordBucket r in historical){ht+=r.Turns;hn+=r.TotalTokens;}
            return ct!=ht?ct>ht:cn>=hn;
        }
        public static Dictionary<string,List<RecordBucket>> Groups(IEnumerable<RecordBucket> rows)
        {
            Dictionary<string,List<RecordBucket>> groups=new Dictionary<string,List<RecordBucket>>(StringComparer.Ordinal);
            if(rows!=null)foreach(RecordBucket row in rows){string key=GroupKey(row);List<RecordBucket> group;if(!groups.TryGetValue(key,out group))groups[key]=group=new List<RecordBucket>();group.Add(row);}
            return groups;
        }
        public static string Serialize(UsageRecordMetadata value)
        {UsageRecordMetadata copy=new UsageRecordMetadata();value.CopyMetadataTo(copy);return new JavaScriptSerializer().Serialize(copy);}
        public static void DeserializeInto(string json,UsageRecordMetadata target)
        {
            if(string.IsNullOrEmpty(json))return;
            UsageRecordMetadata value=new JavaScriptSerializer().Deserialize<UsageRecordMetadata>(json);
            if(value!=null)value.CopyMetadataTo(target);
        }
        public static void ReadCloud(IList row,UsageRecordMetadata target)
        {
            if(row.Count<=11)return;
            target.RequestedServiceTier=Normalize(Convert.ToString(row[11],CultureInfo.InvariantCulture));
            if(row.Count>12)target.ActualServiceTier=Normalize(Convert.ToString(row[12],CultureInfo.InvariantCulture));
            if(row.Count>13)target.TierEvidence=Convert.ToString(row[13],CultureInfo.InvariantCulture);
            if(row.Count>14)target.CostBasis=Convert.ToString(row[14],CultureInfo.InvariantCulture);
            if(row.Count>15)target.PricingVersion=Convert.ToString(row[15],CultureInfo.InvariantCulture);
            if(row.Count>16)target.Provider=Convert.ToString(row[16],CultureInfo.InvariantCulture);
            if(row.Count>17&&row[17]!=null)target.StandardEquivalentUsd=Convert.ToDouble(row[17],CultureInfo.InvariantCulture);
            if(row.Count>18&&row[18]!=null)target.ServiceTierPremiumUsd=Convert.ToDouble(row[18],CultureInfo.InvariantCulture);
        }
    }

    public sealed class ServiceTierPrice
    {
        public string provider { get; set; }
        public string model { get; set; }
        public string tier { get; set; }
        public string effective_from { get; set; }
        public string effective_until { get; set; }
        public double input_per_m { get; set; }
        public double output_per_m { get; set; }
        public double cache_read_per_m { get; set; }
        public double cache_write_per_m { get; set; }
        public string source { get; set; }
        public string version { get; set; }
        public long long_context_threshold { get; set; }
        public long max_prompt_tokens { get; set; }
        public double long_input_per_m { get; set; }
        public double long_output_per_m { get; set; }
        public double long_cache_read_per_m { get; set; }
        public double long_cache_write_per_m { get; set; }
        public double cache_write_long_per_m { get; set; }
        public string cache_write_long_duration { get; set; }
        public double long_cache_write_long_per_m { get; set; }
    }

    public static class ServiceTierPricing
    {
        private static readonly object Gate=new object();
        private static readonly Dictionary<string,List<ServiceTierPrice>> Cache=new Dictionary<string,List<ServiceTierPrice>>(StringComparer.OrdinalIgnoreCase);
        public static bool Supports(ServiceTierPrice price,UsageEvent ev)
        {
            double prompt=(double)Math.Max(0,ev.FreshInput)+Math.Max(0,ev.CacheRead)+Math.Max(0,ev.CacheWrite);
            return price!=null&&(price.max_prompt_tokens<=0||prompt<=price.max_prompt_tokens);
        }
        public static double Quote(ServiceTierPrice p,UsageEvent ev,out ModelRate rate)
        {
            if(!Supports(p,ev))throw new InvalidOperationException("请求上下文超过此档位已核验价格的支持范围");
            long fresh=Math.Max(0,ev.FreshInput),generated=Math.Max(0,ev.Output),cached=Math.Max(0,ev.CacheRead),written=Math.Max(0,ev.CacheWrite);
            bool large=p.long_context_threshold>0&&(double)fresh+cached+written>p.long_context_threshold;
            double input=large?p.long_input_per_m:p.input_per_m,output=large?p.long_output_per_m:p.output_per_m;
            double read=large?p.long_cache_read_per_m:p.cache_read_per_m,write=large?p.long_cache_write_per_m:p.cache_write_per_m;
            long longer=p.cache_write_long_duration=="1h"?Math.Min(written,Math.Max(0,ev.CacheWrite1h)):0;
            double longWrite=large?p.long_cache_write_long_per_m:p.cache_write_long_per_m;
            rate=new ModelRate(input,output,read,write,"tier:"+UsageServiceTier.Normalize(p.tier));
            return Math.Min(1000000000000.0,(fresh*input+generated*output+cached*read+(written-longer)*write+longer*longWrite)/1000000.0);
        }
        public static ServiceTierPrice Find(string provider,string model,string tier,string date,string customHome)
        {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pricing","service_tier_prices.json");
            // A fixture/custom home never reads the real user's configuration.
            string config=string.IsNullOrEmpty(customHome)?SyncService.AppConfigDir:Path.Combine(customHome,".toklens");
            string customPath=Path.Combine(config,"service_tier_prices.json");
            string cacheKey=path+"|"+(File.Exists(path)?File.GetLastWriteTimeUtc(path).Ticks:0)+"|"+customPath+"|"+(File.Exists(customPath)?File.GetLastWriteTimeUtc(customPath).Ticks:0);
            List<ServiceTierPrice> prices;
            lock(Gate)
            {
                if(!Cache.TryGetValue(cacheKey,out prices))
                {
                    prices=new List<ServiceTierPrice>();
                    using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("TokLens.service_tier_prices.json"))
                        if(stream!=null)using(StreamReader reader=new StreamReader(stream))Append(prices,reader.ReadToEnd());
                    if(File.Exists(path))Append(prices,File.ReadAllText(path));
                    if(File.Exists(customPath))Append(prices,File.ReadAllText(customPath));
                    if(Cache.Count>16)Cache.Clear();Cache[cacheKey]=prices;
                }
            }
            ServiceTierPrice best=null;
            foreach(ServiceTierPrice p in prices)
            {
                DateTime starts;
                if(p==null||!DateTime.TryParseExact(p.effective_from,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out starts)||string.IsNullOrWhiteSpace(p.source))continue;
                if(!string.Equals(provider,p.provider,StringComparison.OrdinalIgnoreCase)||!string.Equals(model,p.model,StringComparison.OrdinalIgnoreCase)||tier!=UsageServiceTier.Normalize(p.tier)||string.CompareOrdinal(p.effective_from,date)>0)continue;
                if(!string.IsNullOrEmpty(p.effective_until))
                {DateTime until;if(!DateTime.TryParseExact(p.effective_until,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out until)||string.CompareOrdinal(date,p.effective_until)>0)continue;}
                double[] values={p.input_per_m,p.output_per_m,p.cache_read_per_m,p.cache_write_per_m,p.long_input_per_m,p.long_output_per_m,p.long_cache_read_per_m,p.long_cache_write_per_m,p.cache_write_long_per_m,p.long_cache_write_long_per_m};bool valid=true;
                foreach(double value in values)if(value<0||value>1000000000000.0||double.IsNaN(value)||double.IsInfinity(value))valid=false;
                bool needsLongRates=p.long_context_threshold>0&&(p.max_prompt_tokens<=0||p.max_prompt_tokens>p.long_context_threshold);
                if(needsLongRates&&((p.input_per_m>0&&p.long_input_per_m<=0)||(p.output_per_m>0&&p.long_output_per_m<=0)))valid=false;
                if(p.max_prompt_tokens<0)valid=false;
                if(valid&&(best==null||string.CompareOrdinal(p.effective_from,best.effective_from)>=0))best=p;
            }
            return best;
        }
        private static void Append(List<ServiceTierPrice> target,string json)
        {
            // Invalid custom price data must never silently become a standard-tier quote.
            ServiceTierPrice[] values=new JavaScriptSerializer().Deserialize<ServiceTierPrice[]>(json);
            if(values!=null)target.AddRange(values);
        }
    }
}
