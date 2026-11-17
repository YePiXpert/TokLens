using System;
using System.Collections.Generic;
using System.Globalization;

namespace TokLens
{
    public static class UsageTiming
    {
        public static bool IsMeasured(string source) { return source == "request" || source == "generation"; }
        public static UsageEvent Measured(UsageEvent ev) { if (ev != null && ev.DurationMs > 0) ev.TimingSource = "request"; return ev; }
        public static long Timestamp(object raw)
        {
            if (raw == null) return 0;
            string text = Convert.ToString(raw, CultureInfo.InvariantCulture); double n;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            {
                if (double.IsNaN(n) || double.IsInfinity(n) || n <= 0) return 0;
                if (n < 100000000000) n *= 1000;
                return n >= 946684800000 && n <= 253402300799000 ? (long)n : 0;
            }
            DateTimeOffset date;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
                ? (long)(date.UtcDateTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds : 0;
        }
        public static long EpochMs(DateTime time) { return (long)(time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        public static int PairedDuration(object start, object end) { long a = Timestamp(start), b = Timestamp(end), delta = b - a; return a > 0 && b > 0 && delta > 0 && delta <= int.MaxValue ? (int)delta : 0; }
        public static string FormatNumber(double tps)
        {
            if (tps <= 0 || double.IsNaN(tps) || double.IsInfinity(tps)) return "—";
            if (tps < 0.1) return tps.ToString("0.###", CultureInfo.InvariantCulture) == "0" ? "<0.001" : tps.ToString("0.###", CultureInfo.InvariantCulture);
            return tps >= 10000 ? tps.ToString("0.##E+0", CultureInfo.InvariantCulture) : tps.ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    public sealed class HudSessionChoice
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public override string ToString() { return Label; }
    }

    // A presentation snapshot. Rebuild against the clock even if no log file changes.
    public sealed class HudTelemetry
    {
        public string Tool = "本机", Model = "等待请求", Cost = "—", Tokens = "—";
        public string SessionId = "", Workspace = "", SelectionKey = "", RequestedServiceTier = "unknown", ActualServiceTier = "unknown", TierEvidence = "";
        public long TimestampMs, Output, UnpricedRequests, Last15UnpricedRequests, Last15PricedRequests, TwoHourUnpricedRequests;
        public int DurationMs;
        public string TimingSource = "";
        public bool Cny, WindowComplete, ContextLimitIsActual, TodayHasPricedAmount;
        public double Rate = ExchangeRateService.DefaultRate;
        public double TodayUsd, LastHourUsd, Last15Usd, LastCostUsd;
        public double CacheHitRate = -1;
        public long TodayTokens, TodayRequests, ContextTokens, ContextLimit;
        public bool Alert;
        public string AlertText = "";
        public double ContextFraction { get { return ContextLimit > 0 && ContextTokens > 0 ? Math.Min(1.0, (double)ContextTokens / ContextLimit) : -1; } }
        public double[] SparkHour = new double[30], SparkTwoHours = new double[24];
        public long[] SparkTwoHoursUnpriced = new long[24];
        public List<RecentRequestRecord> Latest = new List<RecentRequestRecord>();
        public List<HudSessionChoice> Sessions = new List<HudSessionChoice>();
        public double Tps { get { return UsageTiming.IsMeasured(TimingSource) ? FormatterUtil.ComputeTps(Output, DurationMs) : 0; } }

        public static string SessionKey(RecentRequestRecord row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.SessionId)) return "";
            return (row.Tool ?? "") + "\n" + row.SessionId;
        }

        public static HudTelemetry FromScan(ScanResult result, DateTime now, bool cny, double rate)
        { return FromScan(result, now, cny, rate, ""); }

        public static HudTelemetry FromScan(ScanResult result, DateTime now, bool cny, double rate, string selectionKey)
        {
            HudTelemetry h = new HudTelemetry(); h.Cny = cny; h.SelectionKey = selectionKey ?? "";
            h.Rate = rate > 0 && !double.IsNaN(rate) && !double.IsInfinity(rate) ? rate : ExchangeRateService.DefaultRate;
            h.Sessions.Add(new HudSessionChoice { Key = "", Label = "最新请求 · 本机全部工具" });
            if (result == null) return h;
            DateTime localNow = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
            string today = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            long fresh = 0, cacheRead = 0, cacheWrite = 0;
            if (result.Records != null) foreach (RecordBucket r in result.Records)
            {
                if (r == null || r.Date != today) continue;
                bool unpriced = UsagePresentation.IsUnpriced(r);
                if (unpriced) h.UnpricedRequests += Math.Max(1, r.Turns);
                else { h.TodayUsd += SafeCost(r.CostUsd); h.TodayHasPricedAmount = true; }
                h.TodayTokens += Math.Max(0, r.TotalTokens);
                fresh += Math.Max(0, r.FreshInput); cacheRead += Math.Max(0, r.CacheRead); cacheWrite += Math.Max(0, r.CacheWrite);
                h.TodayRequests += Math.Max(0, r.Turns);
            }
            h.Cost = h.UnpricedRequests > 0 && !h.TodayHasPricedAmount ? "—" : FormatterUtil.FormatMoney(h.TodayUsd, cny, h.Rate) + (h.UnpricedRequests > 0 ? " *" : ""); h.Tokens = FormatterUtil.FormatTokens(h.TodayTokens);
            long prompt = fresh + cacheRead + cacheWrite; h.CacheHitRate = prompt > 0 ? (double)cacheRead / prompt : -1;
            long nowMs = UsageTiming.EpochMs(now);
            h.WindowComplete = result.WindowRequestsComplete;
            IList<RecentRequestRecord> window = h.WindowComplete || (result.WindowRequests != null && result.WindowRequests.Count > 0) ? result.WindowRequests : result.RecentRequests;
            if (window != null) foreach (RecentRequestRecord r in window)
            {
                if (!Valid(r, nowMs)) continue;
                long age = Math.Max(0, nowMs - r.TimestampMs); bool unpriced = UsagePresentation.IsUnpriced(r); double cost = unpriced ? 0 : SafeCost(r.CostUsd);
                if (age < 3600000L) { h.LastHourUsd += cost; h.SparkHour[29 - (int)(age / 120000L)] += cost; }
                if (age < 900000L) { h.Last15Usd += cost; if (unpriced) h.Last15UnpricedRequests++; else h.Last15PricedRequests++; }
                if (age < 7200000L) { int bin = 23 - (int)(age / 300000L); h.SparkTwoHours[bin] += cost; if (unpriced) { h.TwoHourUnpricedRequests++; h.SparkTwoHoursUnpriced[bin]++; } }
            }
            List<RecentRequestRecord> candidates = new List<RecentRequestRecord>();
            if (result.RecentRequests != null) candidates.AddRange(result.RecentRequests);
            // RecentRequests is intentionally capped for the main table, not for session discovery.
            if (result.WindowRequests != null) candidates.AddRange(result.WindowRequests);
            candidates.RemoveAll(delegate(RecentRequestRecord r) { return !Valid(r, nowMs); });
            candidates.Sort(delegate(RecentRequestRecord a, RecentRequestRecord b) { return b.TimestampMs.CompareTo(a.TimestampMs); });
            HashSet<string> sessions = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            RecentRequestRecord latest = null;
            foreach (RecentRequestRecord r in candidates)
            {
                string key = SessionKey(r);
                if (key.Length > 0 && sessions.Add(key)) h.Sessions.Add(new HudSessionChoice { Key = key, Label = ShortTool(r.Tool) + " · " + SessionLabel(r.Workspace, r.SessionId) });
                if (h.SelectionKey.Length > 0 && key != h.SelectionKey) continue;
                if (latest == null) latest = r;
                string requestKey = key + "|" + r.Tool + "|" + r.Model + "|" + r.TimestampMs + "|" + r.TotalTokens + "|" + r.CostUsd.ToString("R", CultureInfo.InvariantCulture);
                if (h.Latest.Count < 5 && seen.Add(requestKey)) h.Latest.Add(r);
            }
            if (h.SelectionKey.Length > 0 && !sessions.Contains(h.SelectionKey))
                h.Sessions.Add(new HudSessionChoice { Key = h.SelectionKey, Label = "已固定会话 · 暂无近期记录" });
            if (latest != null)
            {
                h.Tool = latest.Tool; h.Model = latest.Model; h.TimestampMs = latest.TimestampMs; h.Output = latest.Output;
                h.DurationMs = latest.DurationMs; h.TimingSource = latest.TimingSource;
                h.ContextTokens = Math.Max(0, latest.FreshInput) + Math.Max(0, latest.CacheRead) + Math.Max(0, latest.CacheWrite);
                h.LastCostUsd = UsagePresentation.IsUnpriced(latest) ? 0 : SafeCost(latest.CostUsd); h.SessionId = latest.SessionId; h.Workspace = latest.Workspace;
                h.ActualServiceTier = latest.ActualServiceTier; h.RequestedServiceTier = latest.RequestedServiceTier; h.TierEvidence = latest.TierEvidence;
                h.ContextLimitIsActual = latest.ActualContextLimit > 0;
                h.ContextLimit = h.ContextLimitIsActual ? latest.ActualContextLimit : PricingEngine.ContextWindowFor(latest.Model);
            }
            return h;
        }
        private static bool Valid(RecentRequestRecord r, long nowMs) { return r != null && r.TimestampMs > 0 && r.TimestampMs <= nowMs + 1000; }
        private static double SafeCost(double cost) { return cost > 0 && !double.IsNaN(cost) && !double.IsInfinity(cost) ? cost : 0; }
        public bool IsRecent(DateTime utcNow) { long age = UsageTiming.EpochMs(utcNow) - TimestampMs; return TimestampMs > 0 && age >= -1000 && age <= 300000; }
        public string TierLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ActualServiceTier) && ActualServiceTier != "unknown") return "实际档位 · " + TierName(ActualServiceTier);
                if (!string.IsNullOrWhiteSpace(RequestedServiceTier) && RequestedServiceTier != "unknown") return "请求档位 · " + TierName(RequestedServiceTier) + "（实际未知）";
                return "档位未知";
            }
        }
        private static string TierName(string tier) { return tier == "fast" || tier == "priority" ? "Fast" : tier; }
        public static string SessionLabel(string workspace, string session)
        {
            string name = (workspace ?? "").TrimEnd('/', '\\');
            int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (cut >= 0) name = name.Substring(cut + 1);
            string id = session ?? ""; if (id.Length > 8) id = id.Substring(0, 8);
            return name.Length > 0 ? name + (id.Length > 0 ? " · " + id : "") : (id.Length > 0 ? id : "会话未识别");
        }
        public static string ShortTool(string tool)
        {
            string t = tool ?? "本机";
            if (t.StartsWith("OpenAI Codex", StringComparison.OrdinalIgnoreCase)) return "Codex";
            if (t.StartsWith("Google Antigravity", StringComparison.OrdinalIgnoreCase)) return "Antigravity";
            return t.Replace(" (WSL)", " · WSL");
        }
        public static string ShortModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || model == "unknown") return "模型待识别";
            if (model == "gemini-flash-worker-agy") return "Gemini Flash · Worker";
            if (model == "gemini-pro-orchestrator") return "Gemini Pro · 编排";
            return model.Replace("claude-", "Claude ").Replace("gemini-", "Gemini ").Replace("gpt-", "GPT-");
        }
    }
}
