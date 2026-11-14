using System;
using System.Collections.Generic;
using System.Globalization;

namespace TokLens
{
    public static class UsageTiming
    {
        public static bool IsMeasured(string source) { return source=="request" || source=="generation"; }
        public static UsageEvent Measured(UsageEvent ev)
        { if(ev!=null && ev.DurationMs>0)ev.TimingSource="request"; return ev; }
        public static long Timestamp(object raw)
        {
            if(raw==null)return 0; string text=Convert.ToString(raw,CultureInfo.InvariantCulture);double n;
            if(double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out n))
            { if(double.IsNaN(n)||double.IsInfinity(n)||n<=0)return 0;if(n<100000000000)n*=1000;return n>=946684800000&&n<=253402300799000?(long)n:0; }
            DateTimeOffset date;
            if(DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out date))return (long)(date.UtcDateTime-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
            return 0;
        }
        public static int PairedDuration(object start,object end)
        { long a=Timestamp(start),b=Timestamp(end),delta=b-a;return a>0&&b>0&&delta>0&&delta<=int.MaxValue?(int)delta:0; }
        public static string FormatNumber(double tps)
        {
            if(tps<=0 || double.IsNaN(tps)||double.IsInfinity(tps))return "—";
            if(tps<0.1)return tps.ToString("0.###",CultureInfo.InvariantCulture)=="0"?"<0.001":tps.ToString("0.###",CultureInfo.InvariantCulture);
            if(tps>=10000)return tps.ToString("0.##E+0",CultureInfo.InvariantCulture);
            return tps.ToString("F1",CultureInfo.InvariantCulture);
        }
    }

    public sealed class HudTelemetry
    {
        public string Tool="本机", Model="等待请求", Cost="—", Tokens="—";
        public long TimestampMs, Output, UnpricedRequests;
        public int DurationMs;
        public string TimingSource="";
        // 悬浮球用的数值（美元口径，显示时按 Cny / Rate 换算）
        public bool Cny;
        public double Rate=ExchangeRateService.DefaultRate;
        public double TodayUsd, LastHourUsd, Last15Usd, LastCostUsd;
        public double CacheHitRate=-1;
        public long TodayTokens, TodayRequests, ContextTokens;
        public long ContextLimit;                       // 最近一次请求所用模型的上下文上限，未知为 0
        public bool Alert;                              // Agent 失控（高频消耗）提醒期间为 true
        public string AlertText = "";
        public double ContextFraction { get { return ContextLimit > 0 && ContextTokens > 0 ? Math.Min(1.0, (double)ContextTokens / ContextLimit) : -1; } }
        public double[] SparkHour=new double[30];      // 近 60 分钟，每 2 分钟一格
        public double[] SparkTwoHours=new double[24];  // 近 2 小时，每 5 分钟一格
        public List<RecentRequestRecord> Latest=new List<RecentRequestRecord>(); // 最近 5 次请求，新的在前
        public double Tps { get { return UsageTiming.IsMeasured(TimingSource)?FormatterUtil.ComputeTps(Output,DurationMs):0; } }
        public static HudTelemetry FromScan(ScanResult result,DateTime now,bool cny,double rate)
        {
            HudTelemetry h=new HudTelemetry(); h.Cny=cny; h.Rate=rate>0&&!double.IsNaN(rate)&&!double.IsInfinity(rate)?rate:ExchangeRateService.DefaultRate; if(result==null)return h;
            double cost=0;long tokens=0,fresh=0,cacheRead=0,cacheWrite=0,turns=0;string today=now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(result.Records!=null)foreach(RecordBucket r in result.Records)if(r!=null&&r.Date==today){
                cost+=r.CostUsd;tokens+=r.TotalTokens;fresh+=r.FreshInput;cacheRead+=r.CacheRead;cacheWrite+=r.CacheWrite;turns+=Math.Max(0,r.Turns);
                if(r.CostUsd<=0&&r.RateLabel=="未定价")h.UnpricedRequests+=Math.Max(1,r.Turns);}
            h.Cost=FormatterUtil.FormatMoney(cost,cny,rate);h.Tokens=FormatterUtil.FormatTokens(tokens);
            h.TodayUsd=cost;h.TodayTokens=tokens;h.TodayRequests=turns;
            long prompt=fresh+cacheRead+cacheWrite;h.CacheHitRate=prompt>0?(double)cacheRead/prompt:-1;
            RecentRequestRecord latest=null;
            List<RecentRequestRecord> recent=new List<RecentRequestRecord>();
            long nowMs=(long)(now.ToUniversalTime()-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
            if(result.RecentRequests!=null)foreach(RecentRequestRecord r in result.RecentRequests)
            {
                if(r==null||r.TimestampMs<=0||r.TimestampMs>nowMs+1000)continue;
                recent.Add(r);
                if(latest==null||r.TimestampMs>latest.TimestampMs)latest=r;
                long age=Math.Max(0,nowMs-r.TimestampMs);
                double c=r.CostUsd>0&&!double.IsNaN(r.CostUsd)&&!double.IsInfinity(r.CostUsd)?r.CostUsd:0;
                if(age<3600000L){h.LastHourUsd+=c;h.SparkHour[29-(int)(age/120000L)]+=c;}
                if(age<900000L)h.Last15Usd+=c;
                if(age<7200000L)h.SparkTwoHours[23-(int)(age/300000L)]+=c;
            }
            if(latest!=null){h.Tool=latest.Tool;h.Model=latest.Model;h.TimestampMs=latest.TimestampMs;h.Output=latest.Output;h.DurationMs=latest.DurationMs;h.TimingSource=latest.TimingSource;
                h.ContextTokens=latest.FreshInput+latest.CacheRead+latest.CacheWrite;h.LastCostUsd=latest.CostUsd>0?latest.CostUsd:0;
                h.ContextLimit=PricingEngine.ContextWindowFor(latest.Model);}
            recent.Sort(delegate(RecentRequestRecord a,RecentRequestRecord b){return b.TimestampMs.CompareTo(a.TimestampMs);});
            if(recent.Count>5)recent.RemoveRange(5,recent.Count-5);
            h.Latest=recent;
            return h;
        }
        public bool IsRecent(DateTime utcNow)
        { double age=(utcNow-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds-TimestampMs;return TimestampMs>0&&age>=-1000&&age<=300000; }
        public static string ShortTool(string tool)
        {
            string t=tool??"本机";
            if(t.StartsWith("OpenAI Codex",StringComparison.OrdinalIgnoreCase))return "Codex";
            if(t.StartsWith("Google Antigravity",StringComparison.OrdinalIgnoreCase))return "Antigravity";
            return t.Replace(" (WSL)"," · WSL");
        }
        public static string ShortModel(string model)
        {
            if(string.IsNullOrWhiteSpace(model)||model=="unknown")return "模型待识别";
            // Only presentation aliases. The original ID remains in the detail panel.
            if(model=="gemini-flash-worker-agy")return "Gemini Flash · Worker";
            if(model=="gemini-pro-orchestrator")return "Gemini Pro · 编排";
            string value=model.Replace("claude-","Claude ").Replace("gemini-","Gemini ").Replace("gpt-","GPT-");
            return value;
        }
    }
}
