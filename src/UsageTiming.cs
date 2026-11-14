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
        public double Tps { get { return UsageTiming.IsMeasured(TimingSource)?FormatterUtil.ComputeTps(Output,DurationMs):0; } }
        public static HudTelemetry FromScan(ScanResult result,DateTime now,bool cny,double rate)
        {
            HudTelemetry h=new HudTelemetry(); if(result==null)return h;
            double cost=0;long tokens=0;string today=now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(result.Records!=null)foreach(RecordBucket r in result.Records)if(r!=null&&r.Date==today){cost+=r.CostUsd;tokens+=r.TotalTokens;if(r.CostUsd<=0&&r.RateLabel=="未定价")h.UnpricedRequests+=Math.Max(1,r.Turns);}
            h.Cost=FormatterUtil.FormatMoney(cost,cny,rate);h.Tokens=FormatterUtil.FormatTokens(tokens);
            RecentRequestRecord latest=null;
            long nowMs=(long)(now.ToUniversalTime()-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
            if(result.RecentRequests!=null)foreach(RecentRequestRecord r in result.RecentRequests)
                if(r!=null&&r.TimestampMs>0&&r.TimestampMs<=nowMs+1000&&(latest==null||r.TimestampMs>latest.TimestampMs))latest=r;
            if(latest!=null){h.Tool=latest.Tool;h.Model=latest.Model;h.TimestampMs=latest.TimestampMs;h.Output=latest.Output;h.DurationMs=latest.DurationMs;h.TimingSource=latest.TimingSource;}
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
