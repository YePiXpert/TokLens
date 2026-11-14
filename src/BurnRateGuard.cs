using System;
using System.Collections.Generic;
using System.Globalization;

namespace TokLens
{
    /// <summary>
    /// 异常燃烧速率告警事件（供 ToastManager 消费）。每日预算功能已移除，这里只剩 Agent 高频燃烧提醒。
    /// </summary>
    public sealed class BurnRateAlert
    {
        private string _message;

        public string RuleCode { get; set; }
        public string Variant { get; set; }
        public bool IsCritical { get; set; }
        public string Title { get; set; }

        public string Message
        {
            get { return _message; }
            set { _message = value ?? string.Empty; }
        }

        public string Detail
        {
            get { return _message; }
            set { _message = value ?? string.Empty; }
        }

        public DateTime TriggeredAt { get; set; }
        public double CurrentCostUsd { get; set; }
        public double BurnRatePerHourUsd { get; set; }
        public string TopTool { get; set; }
        public string TopModel { get; set; }
        public int RecentRequestCount { get; set; }

        public BurnRateAlert()
        {
            _message = string.Empty;
            RuleCode = string.Empty;
            Variant = "warning";
            IsCritical = false;
            Title = string.Empty;
            TriggeredAt = DateTime.Now;
            CurrentCostUsd = 0.0;
            BurnRatePerHourUsd = 0.0;
            TopTool = string.Empty;
            TopModel = string.Empty;
            RecentRequestCount = 0;
        }
    }

    /// <summary>
    /// 异常燃烧速率守卫：近 15 分钟花费远超近 7 日均速时提醒排查 Agent 死循环。
    /// 同时提供报表导出共用的金额 / 汇率 / 时间规整工具方法。
    /// </summary>
    public sealed class BurnRateGuard
    {
        public const double RunawayMin15MinCostUsd = 2.00;
        public const double RunawayMultiplierOver7DayAvg = 4.0;
        public const double RunawayFallbackHourlyUsd = 12.00;

        private const long Window15MinMs = 15L * 60L * 1000L;
        private const long Cooldown30MinMs = 30L * 60L * 1000L;
        private const long ClockSkewToleranceMs = 60L * 1000L;

        private readonly object _syncLock = new object();
        private long _lastRunawayAlertEpochMs = 0L;

        internal sealed class PairSpendStat
        {
            public string Tool;
            public string Model;
            public double CostUsd;
            public long Tokens;
            public int Count;
        }

        internal sealed class EvaluatedMetrics
        {
            public DateTime LocalNow;
            public long NowEpochMs;
            public double Past7DaysCostUsd;
            public double HistHourlyAvgUsd;
            public double Last15MinCostUsd;
            public int Last15MinRequests;
            public long Last15MinTokens;
            public double BurnRate15MinPerHrUsd;
            public bool IsRunawayBurn;
            public PairSpendStat Top15MinPair;
        }

        public void Reset()
        {
            lock (_syncLock)
            {
                _lastRunawayAlertEpochMs = 0L;
            }
        }

        public List<BurnRateAlert> Evaluate(
            IList<RecordBucket> allRecords,
            IList<RecentRequestRecord> recentRequests,
            bool isCny,
            double usdToCny)
        {
            return Evaluate(allRecords, recentRequests, isCny, usdToCny, DateTime.Now);
        }

        public List<BurnRateAlert> Evaluate(
            IList<RecordBucket> allRecords,
            IList<RecentRequestRecord> recentRequests,
            bool isCny,
            double usdToCny,
            DateTime nowLocal)
        {
            List<BurnRateAlert> alerts = new List<BurnRateAlert>();
            double safeRate = FormatterUtil.NormalizeExchangeRate(usdToCny);
            EvaluatedMetrics m = CollectMetrics(allRecords, recentRequests, nowLocal);

            lock (_syncLock)
            {
                // 近 15 分钟算力异常突增 / Runaway Agent 熔断预警（30 分钟冷却防刷屏）
                if (m.IsRunawayBurn)
                {
                    bool inCooldown = _lastRunawayAlertEpochMs > 0L &&
                                      (m.NowEpochMs >= _lastRunawayAlertEpochMs) &&
                                      ((m.NowEpochMs - _lastRunawayAlertEpochMs) < Cooldown30MinMs);

                    if (!inCooldown)
                    {
                        _lastRunawayAlertEpochMs = m.NowEpochMs;

                        string cost15Str = FormatterUtil.FormatMoney(m.Last15MinCostUsd, isCny, safeRate);
                        string burnHrStr = FormatterUtil.FormatMoney(m.BurnRate15MinPerHrUsd, isCny, safeRate);

                        string topTool = m.Top15MinPair != null ? m.Top15MinPair.Tool : "未知工具";
                        string topModel = m.Top15MinPair != null ? m.Top15MinPair.Model : "unknown";
                        double topCostUsd = m.Top15MinPair != null ? m.Top15MinPair.CostUsd : m.Last15MinCostUsd;
                        double topSharePct = m.Last15MinCostUsd > 0.0 ? Math.Min(100.0, (topCostUsd * 100.0) / m.Last15MinCostUsd) : 100.0;
                        string topCostStr = FormatterUtil.FormatMoney(topCostUsd, isCny, safeRate);

                        string compareText;
                        if (m.HistHourlyAvgUsd > 0.0)
                        {
                            compareText = string.Format(
                                CultureInfo.InvariantCulture,
                                "折合 {0}/小时，达近 7 日均速的 {1:F1} 倍",
                                burnHrStr,
                                m.BurnRate15MinPerHrUsd / m.HistHourlyAvgUsd);
                        }
                        else
                        {
                            compareText = string.Format(
                                CultureInfo.InvariantCulture,
                                "折合燃烧速率高达 {0}/小时",
                                burnHrStr);
                        }

                        BurnRateAlert evRunaway = new BurnRateAlert();
                        evRunaway.RuleCode = "RUNAWAY_15M";
                        evRunaway.Variant = "danger";
                        evRunaway.IsCritical = true;
                        evRunaway.Title = "疑似 Agent 高频燃烧预警";
                        evRunaway.Message = string.Format(
                            CultureInfo.InvariantCulture,
                            "近 15 分钟密集触发 {0} 次请求，累计消耗 {1}（{2}）！最大开销来源：{3} + {4}（{5}，占 {6:F0}%），请立即排查是否陷入死循环。",
                            m.Last15MinRequests,
                            cost15Str,
                            compareText,
                            topTool,
                            topModel,
                            topCostStr,
                            topSharePct);
                        evRunaway.TriggeredAt = m.LocalNow;
                        evRunaway.CurrentCostUsd = m.Last15MinCostUsd;
                        evRunaway.BurnRatePerHourUsd = m.BurnRate15MinPerHrUsd;
                        evRunaway.TopTool = topTool;
                        evRunaway.TopModel = topModel;
                        evRunaway.RecentRequestCount = m.Last15MinRequests;
                        alerts.Add(evRunaway);
                    }
                }
            }

            return alerts;
        }

        private static EvaluatedMetrics CollectMetrics(
            IList<RecordBucket> allRecords,
            IList<RecentRequestRecord> recentRequests,
            DateTime nowInput)
        {
            EvaluatedMetrics m = new EvaluatedMetrics();
            FormatterUtil.NormalizeNowTime(nowInput, out m.LocalNow, out m.NowEpochMs);

            string past7StartStr = m.LocalNow.Date.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string yesterdayStr = m.LocalNow.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (allRecords != null)
            {
                for (int i = 0; i < allRecords.Count; i++)
                {
                    RecordBucket r = allRecords[i];
                    if (r == null || string.IsNullOrEmpty(r.Date))
                        continue;

                    if (string.CompareOrdinal(r.Date, past7StartStr) >= 0 &&
                        string.CompareOrdinal(r.Date, yesterdayStr) <= 0)
                    {
                        m.Past7DaysCostUsd += FormatterUtil.SanitizeMoney(r.CostUsd);
                    }
                }
            }

            Dictionary<string, PairSpendStat> recent15PairMap = new Dictionary<string, PairSpendStat>(StringComparer.OrdinalIgnoreCase);
            if (recentRequests != null)
            {
                for (int i = 0; i < recentRequests.Count; i++)
                {
                    RecentRequestRecord rq = recentRequests[i];
                    if (rq == null || rq.TimestampMs <= 0L)
                        continue;

                    long ageMs = m.NowEpochMs - rq.TimestampMs;
                    if (ageMs < -ClockSkewToleranceMs || ageMs > Window15MinMs)
                        continue;

                    double rqCost = FormatterUtil.SanitizeMoney(rq.CostUsd);
                    long rqTokens = Math.Max(0L, rq.TotalTokens);
                    m.Last15MinCostUsd += rqCost;
                    m.Last15MinRequests++;
                    m.Last15MinTokens += rqTokens;
                    AccumulatePair(recent15PairMap, rq.Tool, rq.Model, rqCost, rqTokens, 1);
                }
            }

            m.Top15MinPair = SelectTopPair(recent15PairMap);
            m.BurnRate15MinPerHrUsd = m.Last15MinCostUsd * 4.0;

            // 过去 7 天日均小时速率 = (过去 7 天总花费 / 7 天) / 24 小时
            m.HistHourlyAvgUsd = m.Past7DaysCostUsd > 0.0 ? (m.Past7DaysCostUsd / (7.0 * 24.0)) : 0.0;

            if (m.Last15MinCostUsd >= RunawayMin15MinCostUsd)
            {
                m.IsRunawayBurn = m.HistHourlyAvgUsd > 0.0
                    ? m.BurnRate15MinPerHrUsd > (m.HistHourlyAvgUsd * RunawayMultiplierOver7DayAvg)
                    : m.BurnRate15MinPerHrUsd >= RunawayFallbackHourlyUsd;
            }
            else
            {
                m.IsRunawayBurn = false;
            }

            return m;
        }

        private static void AccumulatePair(
            Dictionary<string, PairSpendStat> map,
            string rawTool,
            string rawModel,
            double costUsd,
            long tokens,
            int count)
        {
            string tool = string.IsNullOrWhiteSpace(rawTool) ? "未知工具" : rawTool.Trim();
            string model = string.IsNullOrWhiteSpace(rawModel) ? "unknown" : rawModel.Trim();
            string key = tool + "||" + model;

            PairSpendStat stat;
            if (!map.TryGetValue(key, out stat))
            {
                stat = new PairSpendStat();
                stat.Tool = tool;
                stat.Model = model;
                map[key] = stat;
            }
            stat.CostUsd += costUsd;
            stat.Tokens += tokens;
            stat.Count += count;
        }

        private static PairSpendStat SelectTopPair(Dictionary<string, PairSpendStat> map)
        {
            if (map == null || map.Count == 0)
                return null;

            PairSpendStat best = null;
            foreach (PairSpendStat cur in map.Values)
            {
                if (best == null)
                {
                    best = cur;
                    continue;
                }
                if (cur.CostUsd > best.CostUsd + 1e-12)
                {
                    best = cur;
                }
                else if (Math.Abs(cur.CostUsd - best.CostUsd) <= 1e-12 && cur.Tokens > best.Tokens)
                {
                    best = cur;
                }
            }
            return best;
        }
    }
}
