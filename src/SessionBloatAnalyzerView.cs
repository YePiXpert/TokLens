using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TokLens
{
    /// <summary>
    /// 会话簇内单轮请求的上下文体积与模拟压缩轨迹点
    /// </summary>
    public sealed class SessionTurnPoint
    {
        public int TurnIndex { get; set; }
        public long TimestampMs { get; set; }
        public string TimeLabel { get; set; }
        public string Model { get; set; }
        public long FreshInputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long ContextTokens { get; set; }
        public long CompactedContextTokens { get; set; }
        public long OutputTokens { get; set; }
        public double CacheHitRate { get; set; }
        public double CostUsd { get; set; }

        public SessionTurnPoint()
        {
            TimeLabel = "";
            Model = "";
        }
    }

    /// <summary>
    /// 连续编码会话簇汇总（按 MachineId + Tool 分组且相邻请求间隔 &lt;= 25 分钟聚类）
    /// </summary>
    public sealed class SessionBurstSummary
    {
        public string BurstId { get; set; }
        public string MachineId { get; set; }
        public string MachineName { get; set; }
        public string Tool { get; set; }
        public string ToolDisplayText { get; set; }
        public string PrimaryModel { get; set; }
        public long StartTimestampMs { get; set; }
        public long EndTimestampMs { get; set; }
        public double DurationMinutes { get; set; }
        public string StartTimeText { get; set; }
        public string EndTimeText { get; set; }
        public string TimeRangeText { get; set; }
        /// <summary>表格用的短格式：开始时间 · 时长（完整起止时间放在 TimeRangeText）。</summary>
        public string StartAndDurationText { get; set; }
        public int Turns { get; set; }
        public string TurnsText { get; set; }
        public long StartContextTokens { get; set; }
        public long EndContextTokens { get; set; }
        public long PeakContextTokens { get; set; }
        public int PeakTurnIndex { get; set; }
        public string StartContextText { get; set; }
        public string PeakContextText { get; set; }
        public string ContextClimbText { get; set; }
        public long TotalContextTokens { get; set; }
        public long TotalFreshInputTokens { get; set; }
        public long TotalCacheReadTokens { get; set; }
        public long TotalCacheWriteTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public double BloatMultiplier { get; set; }
        public string BloatMultiplierText { get; set; }
        public double BloatBarRatio { get; set; }
        public double AvgCacheHitRate { get; set; }
        public string AvgCacheHitText { get; set; }
        public double InputOutputRatio { get; set; }
        public double CostUsd { get; set; }
        public string FormattedCost { get; set; }
        public int SnowballLevel { get; set; }
        public string SnowballRating { get; set; }
        public string SnowballVariant { get; set; }
        public double EstimatedCompactSavingsUsd { get; set; }
        public bool HasCompactSavings { get; set; }
        public string SavingsState { get; set; }
        public string FormattedCompactSavings { get; set; }
        public List<SessionTurnPoint> TurnPoints { get; set; }

        public SessionBurstSummary()
        {
            BurstId = "";
            MachineId = "local";
            MachineName = "本机";
            Tool = "";
            ToolDisplayText = "";
            PrimaryModel = "";
            StartTimeText = "";
            EndTimeText = "";
            TimeRangeText = "";
            StartAndDurationText = "";
            TurnsText = "";
            StartContextText = "0";
            PeakContextText = "0";
            ContextClimbText = "0 → 0";
            BloatMultiplier = 1.0;
            BloatMultiplierText = "1.0x";
            AvgCacheHitText = "0.0%";
            FormattedCost = "$0.0000";
            SnowballRating = "正常";
            SnowballVariant = "success";
            SavingsState = "none";
            FormattedCompactSavings = "—";
            TurnPoints = new List<SessionTurnPoint>();
        }
    }

    /// <summary>
    /// 全局各工具上下文读写放大比与未命中缓存裸输入效率诊断项
    /// </summary>
    public sealed class ToolContextEfficiencyRow
    {
        public string Tool { get; set; }
        public int Turns { get; set; }
        public long TotalContextTokens { get; set; }
        public long FreshInputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long OutputTokens { get; set; }
        public long AvgContextPerTurn { get; set; }
        public double AmplificationRatio { get; set; }
        public string AmplificationText { get; set; }
        public double UncachedInputRatio { get; set; }
        public string UncachedInputText { get; set; }
        public double CacheHitRatio { get; set; }
        public string CacheHitText { get; set; }
        public double CostUsd { get; set; }
        public string EfficiencyTag { get; set; }
        public string EfficiencyVariant { get; set; }

        public ToolContextEfficiencyRow()
        {
            Tool = "";
            AmplificationText = "0:1";
            UncachedInputText = "0.0%";
            CacheHitText = "0.0%";
            EfficiencyTag = "读写均衡";
            EfficiencyVariant = "success";
        }
    }

    /// <summary>
    /// 会话上下文膨胀与全局读写效率综合分析报告
    /// </summary>
    public sealed class ContextBloatReport
    {
        public List<SessionBurstSummary> Bursts { get; set; }
        public List<ToolContextEfficiencyRow> ToolDiagnostics { get; set; }
        public long PeakSingleTurnContextTokens { get; set; }
        public bool IsPeakFromArchiveFallback { get; set; }
        public string PeakContextSubtitle { get; set; }
        public long GlobalTotalContextTokens { get; set; }
        public long GlobalTotalOutputTokens { get; set; }
        public long GlobalFreshInputTokens { get; set; }
        public long GlobalCacheReadTokens { get; set; }
        public long GlobalCacheWriteTokens { get; set; }
        public double GlobalAmplificationRatio { get; set; }
        public string GlobalAmplificationText { get; set; }
        public double GlobalUncachedInputRatio { get; set; }
        public double GlobalCacheHitRatio { get; set; }
        public int SnowballBurstCount { get; set; }
        public int ModerateBurstCount { get; set; }
        public int TotalBurstCount { get; set; }
        public double TotalEstimatedCompactSavingsUsd { get; set; }
        public bool IsSavingsFromArchiveFallback { get; set; }

        public ContextBloatReport()
        {
            Bursts = new List<SessionBurstSummary>();
            ToolDiagnostics = new List<ToolContextEfficiencyRow>();
            PeakContextSubtitle = "暂无上下文记录";
            GlobalAmplificationText = "0:1";
        }
    }

    /// <summary>
    /// 会话上下文膨胀与雪球效应核心分析器
    /// </summary>
    public static class ContextBloatAnalyzer
    {
        // 相邻请求间隔 <= 25 分钟视为同一次连续编码会话簇
        public const long SessionGapThresholdMs = 25L * 60L * 1000L;

        // 建议执行 /compact 的上下文体积水位线（60K）
        public const long CompactRecommendTokens = 60000L;

        // 图表长上下文警戒虚线水位线（100K）
        public const long AlertThresholdTokens = 100000L;

        // 连续多轮未压缩触发雪球预警的峰值水位线（120K）
        public const long SnowballWarningTokens = 120000L;

        private static readonly object PricingSyncLock = new object();
        private static PricingEngine _cachedPricing = null;
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private struct IndexedRequest
        {
            public RecentRequestRecord Record;
            public int OriginalIndex;
            public long ContextTokens;
        }

        private static PricingEngine GetPricingEngine()
        {
            lock (PricingSyncLock)
            {
                if (_cachedPricing == null)
                {
                    _cachedPricing = new PricingEngine(null);
                }
                return _cachedPricing;
            }
        }

        public static List<SessionBurstSummary> ClusterSessionBursts(IList<RecentRequestRecord> recentRequests)
        {
            return ClusterSessionBursts(recentRequests, null, true, ExchangeRateService.DefaultRate);
        }

        public static List<SessionBurstSummary> ClusterSessionBursts(
            IList<RecentRequestRecord> recentRequests,
            IList<RecordBucket> allRecords,
            bool isCny,
            double usdToCny)
        {
            double safeFx = (double.IsNaN(usdToCny) || double.IsInfinity(usdToCny) || usdToCny <= 0.0)
                ? ExchangeRateService.DefaultRate
                : usdToCny;

            string machineId;
            string machineName;
            ResolveLocalMachineIdentity(allRecords, out machineId, out machineName);

            List<SessionBurstSummary> bursts = new List<SessionBurstSummary>();
            if (recentRequests == null || recentRequests.Count == 0)
                return bursts;

            // 按 MachineId + Tool 分组（实时请求流源于本机扫描，结合已解析的节点标识形成稳定分组键）
            Dictionary<string, List<IndexedRequest>> grouped = new Dictionary<string, List<IndexedRequest>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < recentRequests.Count; i++)
            {
                RecentRequestRecord rq = recentRequests[i];
                if (rq == null) continue;

                long fi = rq.FreshInput > 0L ? rq.FreshInput : 0L;
                long cr = rq.CacheRead > 0L ? rq.CacheRead : 0L;
                long cw = rq.CacheWrite > 0L ? rq.CacheWrite : 0L;
                long outTok = rq.Output > 0L ? rq.Output : 0L;
                long ctx = fi + cr + cw;
                if (ctx <= 0L && outTok <= 0L) continue;

                string toolName = string.IsNullOrWhiteSpace(rq.Tool) ? "未知工具" : rq.Tool.Trim();
                string groupKey = machineId + "||" + toolName;

                List<IndexedRequest> bucketList;
                if (!grouped.TryGetValue(groupKey, out bucketList))
                {
                    bucketList = new List<IndexedRequest>();
                    grouped[groupKey] = bucketList;
                }

                IndexedRequest item = new IndexedRequest();
                item.Record = rq;
                item.OriginalIndex = i;
                item.ContextTokens = ctx;
                bucketList.Add(item);
            }

            foreach (KeyValuePair<string, List<IndexedRequest>> kv in grouped)
            {
                List<IndexedRequest> items = kv.Value;
                if (items == null || items.Count == 0) continue;

                // 按时间升序排列；若毫秒时间戳完全一致，则按上下文从小到大还原先后轮次
                items.Sort(delegate(IndexedRequest a, IndexedRequest b)
                {
                    int c = a.Record.TimestampMs.CompareTo(b.Record.TimestampMs);
                    if (c != 0) return c;
                    c = a.ContextTokens.CompareTo(b.ContextTokens);
                    if (c != 0) return c;
                    return b.OriginalIndex.CompareTo(a.OriginalIndex);
                });

                List<RecentRequestRecord> currentCluster = new List<RecentRequestRecord>();
                for (int i = 0; i < items.Count; i++)
                {
                    RecentRequestRecord cur = items[i].Record;
                    if (currentCluster.Count == 0)
                    {
                        currentCluster.Add(cur);
                        continue;
                    }

                    RecentRequestRecord prev = currentCluster[currentCluster.Count - 1];
                    bool sameBurst = false;
                    if (cur.TimestampMs > 0L && prev.TimestampMs > 0L)
                    {
                        long deltaMs = cur.TimestampMs - prev.TimestampMs;
                        if (deltaMs >= 0L && deltaMs <= SessionGapThresholdMs)
                        {
                            sameBurst = true;
                        }
                    }

                    if (sameBurst)
                    {
                        currentCluster.Add(cur);
                    }
                    else
                    {
                        bursts.Add(BuildBurstSummary(currentCluster, machineId, machineName, isCny, safeFx, bursts.Count));
                        currentCluster = new List<RecentRequestRecord>();
                        currentCluster.Add(cur);
                    }
                }

                if (currentCluster.Count > 0)
                {
                    bursts.Add(BuildBurstSummary(currentCluster, machineId, machineName, isCny, safeFx, bursts.Count));
                }
            }

            // 计算膨胀倍数进度条归一化比例（以全列表最大膨胀倍数为基准，上限封顶保证视觉区分度）
            double maxMult = 1.0;
            for (int i = 0; i < bursts.Count; i++)
            {
                if (bursts[i].BloatMultiplier > maxMult)
                    maxMult = bursts[i].BloatMultiplier;
            }
            double denomMult = maxMult > 2.0 ? maxMult : 10.0;
            for (int i = 0; i < bursts.Count; i++)
            {
                double r = bursts[i].BloatMultiplier / denomMult;
                if (r < 0.04) r = 0.04;
                if (r > 1.0) r = 1.0;
                bursts[i].BloatBarRatio = r;
            }

            // 默认按雪球严重度 -> 峰值上下文降序 -> 累计费用降序排列，直击头号吞金会话
            bursts.Sort(delegate(SessionBurstSummary a, SessionBurstSummary b)
            {
                int c = b.SnowballLevel.CompareTo(a.SnowballLevel);
                if (c != 0) return c;
                c = b.PeakContextTokens.CompareTo(a.PeakContextTokens);
                if (c != 0) return c;
                c = b.CostUsd.CompareTo(a.CostUsd);
                if (c != 0) return c;
                return b.StartTimestampMs.CompareTo(a.StartTimestampMs);
            });

            return bursts;
        }

        public static ContextBloatReport Analyze(
            IList<RecordBucket> allRecords,
            IList<RecentRequestRecord> recentRequests,
            bool isCny,
            double usdToCny)
        {
            double safeFx = (double.IsNaN(usdToCny) || double.IsInfinity(usdToCny) || usdToCny <= 0.0)
                ? ExchangeRateService.DefaultRate
                : usdToCny;

            ContextBloatReport report = new ContextBloatReport();
            report.Bursts = ClusterSessionBursts(recentRequests, allRecords, isCny, safeFx);
            report.TotalBurstCount = report.Bursts.Count;

            long peakBurstCtx = 0L;
            string peakBurstDesc = "";
            double sumCompactSaved = 0.0;
            int snowballCnt = 0;
            int moderateCnt = 0;

            for (int i = 0; i < report.Bursts.Count; i++)
            {
                SessionBurstSummary b = report.Bursts[i];
                if (b.SnowballLevel >= 2)
                    snowballCnt++;
                else if (b.SnowballLevel == 1)
                    moderateCnt++;

                sumCompactSaved += b.EstimatedCompactSavingsUsd;
                if (b.PeakContextTokens > peakBurstCtx)
                {
                    peakBurstCtx = b.PeakContextTokens;
                    peakBurstDesc = b.Tool + " · " + b.PrimaryModel + " (第 " + b.PeakTurnIndex.ToString(CultureInfo.InvariantCulture) + " 轮)";
                }
            }

            report.SnowballBurstCount = snowballCnt;
            report.ModerateBurstCount = moderateCnt;
            report.TotalEstimatedCompactSavingsUsd = sumCompactSaved;

            // 基于全量 RecordBucket 聚合各工具输入/输出读写放大比与裸输入未命中占比
            Dictionary<string, ToolContextEfficiencyRow> toolMap = new Dictionary<string, ToolContextEfficiencyRow>(StringComparer.OrdinalIgnoreCase);
            long globalFi = 0L;
            long globalCr = 0L;
            long globalCw = 0L;
            long globalOut = 0L;
            long archiveBestAvgCtx = 0L;
            string archiveBestDesc = "";
            double archiveFallbackSavingsUsd = 0.0;
            int archiveHighBucketCount = 0;

            if (allRecords != null && allRecords.Count > 0)
            {
                for (int i = 0; i < allRecords.Count; i++)
                {
                    RecordBucket r = allRecords[i];
                    if (r == null) continue;

                    long fi = r.FreshInput > 0L ? r.FreshInput : 0L;
                    long cr = r.CacheRead > 0L ? r.CacheRead : 0L;
                    long cw = r.CacheWrite > 0L ? r.CacheWrite : 0L;
                    long outTok = r.Output > 0L ? r.Output : 0L;
                    long ctx = fi + cr + cw;
                    if (ctx <= 0L && outTok <= 0L) continue;

                    int turns = r.Turns > 0 ? r.Turns : 1;
                    double cost = (double.IsNaN(r.CostUsd) || double.IsInfinity(r.CostUsd) || r.CostUsd < 0.0) ? 0.0 : r.CostUsd;

                    globalFi += fi;
                    globalCr += cr;
                    globalCw += cw;
                    globalOut += outTok;

                    long avgCtx = ctx / turns;
                    if (avgCtx > archiveBestAvgCtx)
                    {
                        archiveBestAvgCtx = avgCtx;
                        archiveBestDesc = (string.IsNullOrWhiteSpace(r.Tool) ? "未知工具" : r.Tool) + " · " +
                                          (string.IsNullOrWhiteSpace(r.Model) ? "默认模型" : r.Model) + " (日均单轮推算)";
                    }

                    // 当无实时单轮流时，利用高均值长上下文归档桶提供保守节省兜底估算
                    if (avgCtx >= CompactRecommendTokens && turns >= 3 && cost > 0.0)
                    {
                        archiveHighBucketCount++;
                        double excessRatio = (avgCtx - (CompactRecommendTokens * 0.65)) / (double)avgCtx;
                        if (excessRatio > 0.48) excessRatio = 0.48;
                        if (excessRatio > 0.0)
                        {
                            archiveFallbackSavingsUsd += cost * excessRatio * 0.55;
                        }
                    }

                    string toolKey = string.IsNullOrWhiteSpace(r.Tool) ? "未知工具" : r.Tool.Trim();
                    ToolContextEfficiencyRow trow;
                    if (!toolMap.TryGetValue(toolKey, out trow))
                    {
                        trow = new ToolContextEfficiencyRow();
                        trow.Tool = toolKey;
                        toolMap[toolKey] = trow;
                    }

                    trow.Turns += turns;
                    trow.FreshInputTokens += fi;
                    trow.CacheReadTokens += cr;
                    trow.CacheWriteTokens += cw;
                    trow.TotalContextTokens += ctx;
                    trow.OutputTokens += outTok;
                    trow.CostUsd += cost;
                }
            }

            // 如果 allRecords 为空但 recentRequests 非空，回退由 recentRequests 汇总全局读写指标
            if (globalFi + globalCr + globalCw == 0L && globalOut == 0L && report.Bursts.Count > 0)
            {
                for (int i = 0; i < report.Bursts.Count; i++)
                {
                    SessionBurstSummary b = report.Bursts[i];
                    globalFi += b.TotalFreshInputTokens;
                    globalCr += b.TotalCacheReadTokens;
                    globalCw += b.TotalCacheWriteTokens;
                    globalOut += b.TotalOutputTokens;

                    ToolContextEfficiencyRow trow;
                    if (!toolMap.TryGetValue(b.Tool, out trow))
                    {
                        trow = new ToolContextEfficiencyRow();
                        trow.Tool = b.Tool;
                        toolMap[b.Tool] = trow;
                    }
                    trow.Turns += b.Turns;
                    trow.FreshInputTokens += b.TotalFreshInputTokens;
                    trow.CacheReadTokens += b.TotalCacheReadTokens;
                    trow.CacheWriteTokens += b.TotalCacheWriteTokens;
                    trow.TotalContextTokens += b.TotalContextTokens;
                    trow.OutputTokens += b.TotalOutputTokens;
                    trow.CostUsd += b.CostUsd;
                }
            }

            long globalCtx = globalFi + globalCr + globalCw;
            report.GlobalFreshInputTokens = globalFi;
            report.GlobalCacheReadTokens = globalCr;
            report.GlobalCacheWriteTokens = globalCw;
            report.GlobalTotalContextTokens = globalCtx;
            report.GlobalTotalOutputTokens = globalOut;
            report.GlobalAmplificationRatio = globalOut > 0L ? ((double)globalCtx / (double)globalOut) : 0.0;
            report.GlobalAmplificationText = FormatAmplificationRatio(report.GlobalAmplificationRatio);
            report.GlobalUncachedInputRatio = globalCtx > 0L ? ((double)globalFi / (double)globalCtx) : 0.0;
            report.GlobalCacheHitRatio = globalCtx > 0L ? ((double)globalCr / (double)globalCtx) : 0.0;

            if (peakBurstCtx > 0L)
            {
                report.PeakSingleTurnContextTokens = peakBurstCtx;
                report.IsPeakFromArchiveFallback = false;
                report.PeakContextSubtitle = peakBurstDesc;
            }
            else if (archiveBestAvgCtx > 0L)
            {
                report.PeakSingleTurnContextTokens = archiveBestAvgCtx;
                report.IsPeakFromArchiveFallback = true;
                report.PeakContextSubtitle = archiveBestDesc;
            }

            if (report.TotalBurstCount == 0 && archiveHighBucketCount > 0)
            {
                report.TotalEstimatedCompactSavingsUsd = archiveFallbackSavingsUsd;
                report.IsSavingsFromArchiveFallback = true;
                report.ModerateBurstCount = archiveHighBucketCount;
            }

            List<ToolContextEfficiencyRow> toolRows = new List<ToolContextEfficiencyRow>(toolMap.Values);
            for (int i = 0; i < toolRows.Count; i++)
            {
                ToolContextEfficiencyRow tr = toolRows[i];
                tr.AvgContextPerTurn = tr.Turns > 0 ? (tr.TotalContextTokens / tr.Turns) : 0L;
                tr.AmplificationRatio = tr.OutputTokens > 0L ? ((double)tr.TotalContextTokens / (double)tr.OutputTokens) : 0.0;
                tr.AmplificationText = FormatAmplificationRatio(tr.AmplificationRatio);
                tr.UncachedInputRatio = tr.TotalContextTokens > 0L ? ((double)tr.FreshInputTokens / (double)tr.TotalContextTokens) : 0.0;
                tr.UncachedInputText = (tr.UncachedInputRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
                tr.CacheHitRatio = tr.TotalContextTokens > 0L ? ((double)tr.CacheReadTokens / (double)tr.TotalContextTokens) : 0.0;
                tr.CacheHitText = (tr.CacheHitRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

                if (tr.AmplificationRatio >= 85.0 && tr.UncachedInputRatio >= 0.25)
                {
                    tr.EfficiencyTag = "高倍裸读预警";
                    tr.EfficiencyVariant = "danger";
                }
                else if (tr.AmplificationRatio >= 65.0 || tr.AvgContextPerTurn >= 80000L)
                {
                    tr.EfficiencyTag = "长上下文放大";
                    tr.EfficiencyVariant = "warning";
                }
                else if (tr.UncachedInputRatio >= 0.45 && tr.AvgContextPerTurn >= 25000L)
                {
                    tr.EfficiencyTag = "缓存命中偏低";
                    tr.EfficiencyVariant = "warning";
                }
                else
                {
                    tr.EfficiencyTag = "读写效率健康";
                    tr.EfficiencyVariant = "success";
                }
            }

            toolRows.Sort(delegate(ToolContextEfficiencyRow a, ToolContextEfficiencyRow b)
            {
                int c = b.TotalContextTokens.CompareTo(a.TotalContextTokens);
                if (c != 0) return c;
                return b.CostUsd.CompareTo(a.CostUsd);
            });
            report.ToolDiagnostics = toolRows;

            return report;
        }

        public static string FormatAmplificationRatio(double ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0.0)
                return "0:1";
            if (ratio >= 10.0)
                return Math.Round(ratio).ToString("F0", CultureInfo.InvariantCulture) + ":1";
            return ratio.ToString("F1", CultureInfo.InvariantCulture) + ":1";
        }

        private static void ResolveLocalMachineIdentity(IList<RecordBucket> allRecords, out string machineId, out string machineName)
        {
            machineId = "local";
            machineName = "本机";
            if (allRecords == null || allRecords.Count == 0)
                return;

            string firstId = null;
            string firstName = null;
            bool singleMachine = true;

            for (int i = 0; i < allRecords.Count; i++)
            {
                RecordBucket b = allRecords[i];
                if (b == null) continue;

                string mid = string.IsNullOrWhiteSpace(b.MachineId) ? "local" : b.MachineId.Trim();
                string mname = string.IsNullOrWhiteSpace(b.MachineName) ? "本机" : b.MachineName.Trim();
                string mtype = b.MachineType ?? "";

                if (firstId == null)
                {
                    firstId = mid;
                    firstName = mname;
                }
                else if (!string.Equals(firstId, mid, StringComparison.OrdinalIgnoreCase))
                {
                    singleMachine = false;
                }

                if (string.Equals(mname, "本机", StringComparison.OrdinalIgnoreCase) ||
                    mtype.IndexOf("本机", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mtype.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    machineId = mid;
                    machineName = mname;
                    return;
                }
            }

            if (singleMachine && firstId != null)
            {
                machineId = firstId;
                machineName = firstName ?? "本机";
            }
        }

        private static bool IsLongContextSurchargeModel(string rawModel)
        {
            string norm = PricingEngine.NormalizeModelId(rawModel, "");
            if (string.IsNullOrEmpty(norm)) return false;
            return norm.StartsWith("claude-sonnet-4", StringComparison.Ordinal) ||
                   norm.StartsWith("gemini-2.5-pro", StringComparison.Ordinal) ||
                   norm.StartsWith("gemini-3-pro", StringComparison.Ordinal) ||
                   norm.StartsWith("gemini-3.0-pro", StringComparison.Ordinal) ||
                   norm.StartsWith("gemini-3.1-pro", StringComparison.Ordinal);
        }

        private static SessionBurstSummary BuildBurstSummary(
            List<RecentRequestRecord> cluster,
            string machineId,
            string machineName,
            bool isCny,
            double usdToCny,
            int seqIndex)
        {
            SessionBurstSummary s = new SessionBurstSummary();
            s.MachineId = machineId;
            s.MachineName = machineName;

            int n = cluster.Count;
            s.Turns = n;
            s.TurnsText = n.ToString(CultureInfo.InvariantCulture) + " 轮";

            RecentRequestRecord first = cluster[0];
            RecentRequestRecord last = cluster[n - 1];
            s.Tool = string.IsNullOrWhiteSpace(first.Tool) ? "未知工具" : first.Tool.Trim();
            s.ToolDisplayText = s.Tool;

            s.StartTimestampMs = first.TimestampMs;
            s.EndTimestampMs = last.TimestampMs;
            s.BurstId = first.TimestampMs > 0L
                ? (machineId + "|" + s.Tool + "|" + first.TimestampMs.ToString(CultureInfo.InvariantCulture))
                : (machineId + "|" + s.Tool + "|idx_" + seqIndex.ToString(CultureInfo.InvariantCulture));

            if (first.TimestampMs > 0L && last.TimestampMs >= first.TimestampMs)
            {
                s.DurationMinutes = (last.TimestampMs - first.TimestampMs) / 60000.0;
            }

            string startTxt;
            string endTxt;
            string rangeTxt;
            FormatBurstTimeLabels(first, last, n, s.DurationMinutes, out startTxt, out endTxt, out rangeTxt);
            s.StartTimeText = startTxt;
            s.EndTimeText = endTxt;
            s.TimeRangeText = rangeTxt;
            s.StartAndDurationText = string.IsNullOrEmpty(startTxt) ? rangeTxt : startTxt + " · " + FormatDurationShort(s.DurationMinutes);

            PricingEngine pricing = GetPricingEngine();
            Dictionary<string, long> modelWeights = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            long sumFi = 0L;
            long sumCr = 0L;
            long sumCw = 0L;
            long sumCtx = 0L;
            long sumOut = 0L;
            double sumCost = 0.0;

            long startCtx = 0L;
            long endCtx = 0L;
            long peakCtx = 0L;
            int peakTurnIdx = 1;

            int uncompactedHighStreak = 0;
            int maxUncompactedHighStreak = 0;
            long prevRealCtx = 0L;

            List<SessionTurnPoint> points = new List<SessionTurnPoint>(n);

            for (int i = 0; i < n; i++)
            {
                RecentRequestRecord rq = cluster[i];
                long fi = rq.FreshInput > 0L ? rq.FreshInput : 0L;
                long cr = rq.CacheRead > 0L ? rq.CacheRead : 0L;
                long cw = rq.CacheWrite > 0L ? rq.CacheWrite : 0L;
                long outTok = rq.Output > 0L ? rq.Output : 0L;
                long ctx = fi + cr + cw;

                double turnCost = (double.IsNaN(rq.CostUsd) || double.IsInfinity(rq.CostUsd) || rq.CostUsd < 0.0)
                    ? 0.0
                    : rq.CostUsd;
                if (turnCost <= 0.0 && (ctx + outTok) > 0L)
                {
                    double calcUsd;
                    double baseUsd;
                    ModelRate unusedRate;
                    pricing.ComputeCost(rq.Model, fi, outTok, cr, cw, 0.0, out calcUsd, out baseUsd, out unusedRate);
                    turnCost = calcUsd;
                }

                sumFi += fi;
                sumCr += cr;
                sumCw += cw;
                sumCtx += ctx;
                sumOut += outTok;
                sumCost += turnCost;

                if (i == 0) startCtx = ctx;
                if (i == n - 1) endCtx = ctx;
                if (i == 0 || ctx > peakCtx)
                {
                    peakCtx = ctx;
                    peakTurnIdx = i + 1;
                }

                // 统计连续高水位未压缩轮次（用于区分偶发大上下文单发与连续滚雪球）
                if (ctx >= 75000L)
                {
                    if (i == 0 || prevRealCtx <= 0L || ctx >= (long)(prevRealCtx * 0.72))
                        uncompactedHighStreak++;
                    else
                        uncompactedHighStreak = 1;

                    if (uncompactedHighStreak > maxUncompactedHighStreak)
                        maxUncompactedHighStreak = uncompactedHighStreak;
                }
                else if (prevRealCtx > 0L && ctx < (long)(prevRealCtx * 0.65))
                {
                    uncompactedHighStreak = 0;
                }
                prevRealCtx = ctx;

                string mdl = string.IsNullOrWhiteSpace(rq.Model) ? "unknown" : rq.Model.Trim();
                long oldWeight;
                if (!modelWeights.TryGetValue(mdl, out oldWeight))
                    oldWeight = 0L;
                modelWeights[mdl] = oldWeight + ctx + outTok + 1L;

                SessionTurnPoint pt = new SessionTurnPoint();
                pt.TurnIndex = i + 1;
                pt.TimestampMs = rq.TimestampMs;
                pt.TimeLabel = FormatTurnShortTime(rq);
                pt.Model = mdl;
                pt.FreshInputTokens = fi;
                pt.CacheReadTokens = cr;
                pt.CacheWriteTokens = cw;
                pt.ContextTokens = ctx;
                pt.CompactedContextTokens = ctx;
                pt.OutputTokens = outTok;
                pt.CacheHitRate = ctx > 0L ? ((double)cr / (double)ctx) : 0.0;
                pt.CostUsd = turnCost;
                points.Add(pt);
            }

            string bestModel = "unknown";
            long bestWeight = -1L;
            foreach (KeyValuePair<string, long> mkv in modelWeights)
            {
                if (mkv.Value > bestWeight)
                {
                    bestWeight = mkv.Value;
                    bestModel = mkv.Key;
                }
            }

            s.PrimaryModel = bestModel;
            s.StartContextTokens = startCtx;
            s.EndContextTokens = endCtx;
            s.PeakContextTokens = peakCtx;
            s.PeakTurnIndex = peakTurnIdx;
            s.StartContextText = FormatterUtil.FormatTokens(startCtx);
            s.PeakContextText = FormatterUtil.FormatTokens(peakCtx);
            s.ContextClimbText = s.StartContextText + " → " + s.PeakContextText;

            s.TotalFreshInputTokens = sumFi;
            s.TotalCacheReadTokens = sumCr;
            s.TotalCacheWriteTokens = sumCw;
            s.TotalContextTokens = sumCtx;
            s.TotalOutputTokens = sumOut;
            s.CostUsd = sumCost;
            s.FormattedCost = FormatterUtil.FormatMoney(sumCost, isCny, usdToCny);

            // 计算上下文膨胀倍数（对极小预检 Ping 做分母平滑保护，避免 200x 失真）
            if (n <= 1 || peakCtx <= startCtx)
            {
                s.BloatMultiplier = 1.0;
            }
            else
            {
                double safeStart = startCtx >= 800L ? (double)startCtx : (startCtx > 0L ? 800.0 : (double)peakCtx);
                double mult = (double)peakCtx / safeStart;
                if (mult < 1.0) mult = 1.0;
                if (mult > 99.9) mult = 99.9;
                s.BloatMultiplier = mult;
            }
            s.BloatMultiplierText = s.BloatMultiplier.ToString("F1", CultureInfo.InvariantCulture) + "x";

            s.AvgCacheHitRate = sumCtx > 0L ? ((double)sumCr / (double)sumCtx) : 0.0;
            s.AvgCacheHitText = (s.AvgCacheHitRate * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
            s.InputOutputRatio = sumOut > 0L ? ((double)sumCtx / (double)sumOut) : 0.0;

            // 雪球指数三级评定
            bool multiTurnSnowball = (peakCtx >= SnowballWarningTokens) &&
                                     (n >= 2) &&
                                     (maxUncompactedHighStreak >= 2 || (n >= 3 && endCtx >= 95000L));
            if (multiTurnSnowball)
            {
                s.SnowballLevel = 2;
                s.SnowballRating = "过长";
                s.SnowballVariant = "danger";
            }
            else if (peakCtx >= CompactRecommendTokens)
            {
                s.SnowballLevel = 1;
                s.SnowballRating = "偏长";
                s.SnowballVariant = "warning";
            }
            else
            {
                s.SnowballLevel = 0;
                s.SnowballRating = "正常";
                s.SnowballVariant = "success";
            }

            // 模拟在 60K 水位处执行 /compact（压缩一半历史上下文）可节省的后续轮次上下文成本
            double estSavedUsd = SimulateCompactAt60kSavings(points, sumCost, pricing);
            s.EstimatedCompactSavingsUsd = estSavedUsd;
            double minVisibleUsd = isCny ? (0.005 / usdToCny) : 0.0005;
            if (estSavedUsd >= minVisibleUsd)
            {
                s.HasCompactSavings = true;
                s.SavingsState = "positive";
                s.FormattedCompactSavings = "省 " + FormatterUtil.FormatMoney(estSavedUsd, isCny, usdToCny);
            }
            else
            {
                s.HasCompactSavings = false;
                s.SavingsState = "none";
                s.FormattedCompactSavings = "—";
            }

            s.TurnPoints = points;
            return s;
        }

        private static double SimulateCompactAt60kSavings(List<SessionTurnPoint> points, double totalBurstCostUsd, PricingEngine pricing)
        {
            if (points == null || points.Count < 2)
                return 0.0;

            long maxCtx = 0L;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].ContextTokens > maxCtx)
                    maxCtx = points[i].ContextTokens;
            }
            if (maxCtx <= CompactRecommendTokens)
                return 0.0;

            long c0 = points[0].ContextTokens;
            points[0].CompactedContextTokens = c0;
            double simBase = c0 >= CompactRecommendTokens ? (c0 * 0.5) : (double)c0;
            double grossSavedUsd = 0.0;

            for (int i = 1; i < points.Count; i++)
            {
                SessionTurnPoint pt = points[i];
                long prevReal = points[i - 1].ContextTokens;
                long curReal = pt.ContextTokens;

                double simCur;
                if (prevReal > 0L && curReal < (long)(prevReal * 0.68))
                {
                    // 真实会话已主动压缩或重置上下文
                    simCur = curReal < simBase ? (double)curReal : simBase;
                }
                else
                {
                    long delta = curReal > prevReal ? (curReal - prevReal) : 0L;
                    simCur = simBase + (double)delta;
                }

                if (simCur > (double)curReal) simCur = (double)curReal;
                if (simCur < 1000.0 && curReal >= 1000L) simCur = 1000.0;

                pt.CompactedContextTokens = (long)Math.Round(simCur);

                if (curReal > 0L && simCur < (double)curReal && pt.CostUsd > 0.0)
                {
                    ModelRate rate = pricing.Lookup(pt.Model);
                    double wCtx = (pt.FreshInputTokens * rate.InputPerM +
                                   pt.CacheReadTokens * rate.CacheReadPerM +
                                   pt.CacheWriteTokens * rate.CacheWritePerM) / 1000000.0;
                    double wOut = (pt.OutputTokens * rate.OutputPerM) / 1000000.0;
                    double wTot = wCtx + wOut;

                    double turnContextUsd = wTot > 0.0 ? (pt.CostUsd * (wCtx / wTot)) : wCtx;
                    double remainFraction = simCur / (double)curReal;

                    // 若原上下文突破 200K 阶梯涨价线而压缩后回落至 200K 内，额外规避双倍单价溢价
                    if (curReal > 200000L && simCur <= 200000.0 && IsLongContextSurchargeModel(pt.Model))
                    {
                        remainFraction *= 0.5;
                    }

                    double savedFrac = 1.0 - remainFraction;
                    if (savedFrac > 0.0)
                    {
                        grossSavedUsd += turnContextUsd * savedFrac;
                    }
                }

                // 若模拟上下文在当前轮次后达到 60K 且后续仍有轮次，触发 /compact 减半
                if (simCur >= (double)CompactRecommendTokens && i < points.Count - 1)
                {
                    simBase = simCur * 0.5;
                }
                else
                {
                    simBase = simCur;
                }
            }

            // 扣除约 14% 的 /compact 摘要请求自身开销，确保省钱预估审慎可靠
            double netSaved = grossSavedUsd * 0.86;
            double maxCap = totalBurstCostUsd * 0.72;
            if (netSaved > maxCap) netSaved = maxCap;
            return netSaved > 0.0 ? netSaved : 0.0;
        }

        private static string FormatTurnShortTime(RecentRequestRecord rq)
        {
            if (rq == null) return "";
            if (rq.TimestampMs > 946684800000L && rq.TimestampMs < 4102444800000L)
            {
                try
                {
                    DateTime dt = UnixEpochUtc.AddMilliseconds(rq.TimestampMs).ToLocalTime();
                    return dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return rq.TimeLabel ?? "";
        }

        private static string FormatDurationShort(double minutes)
        {
            if (double.IsNaN(minutes) || minutes < 1.0) return "<1分";
            int total = (int)Math.Round(minutes);
            if (total < 60) return total.ToString(CultureInfo.InvariantCulture) + "分";
            int h = total / 60, m = total % 60;
            return h.ToString(CultureInfo.InvariantCulture) + "小时" + (m > 0 ? m.ToString(CultureInfo.InvariantCulture) + "分" : "");
        }

        private static void FormatBurstTimeLabels(
            RecentRequestRecord first,
            RecentRequestRecord last,
            int turns,
            double durationMinutes,
            out string startText,
            out string endText,
            out string rangeText)
        {
            startText = "";
            endText = "";
            if (first != null && first.TimestampMs > 946684800000L && first.TimestampMs < 4102444800000L)
            {
                try
                {
                    DateTime sDt = UnixEpochUtc.AddMilliseconds(first.TimestampMs).ToLocalTime();
                    startText = sDt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                    if (last != null && last.TimestampMs >= first.TimestampMs && last.TimestampMs < 4102444800000L)
                    {
                        DateTime eDt = UnixEpochUtc.AddMilliseconds(last.TimestampMs).ToLocalTime();
                        endText = (sDt.Date == eDt.Date)
                            ? eDt.ToString("HH:mm", CultureInfo.InvariantCulture)
                            : eDt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(startText))
            {
                string d = (first != null && !string.IsNullOrEmpty(first.Date)) ? first.Date : "";
                string t = (first != null && !string.IsNullOrEmpty(first.TimeLabel)) ? first.TimeLabel : "未知时间";
                startText = string.IsNullOrEmpty(d) ? t : (d + " " + t);
            }

            if (turns <= 1 || string.IsNullOrEmpty(endText))
            {
                rangeText = startText + " (单轮)";
            }
            else
            {
                string durLabel = durationMinutes < 1.0
                    ? "<1m"
                    : (Math.Round(durationMinutes).ToString("F0", CultureInfo.InvariantCulture) + "m");
                rangeText = startText + " → " + endText + " (" + durLabel + ")";
            }
        }
    }

    /// <summary>
    /// 会话上下文膨胀与雪球效应显微镜全高视图
    /// </summary>
    public sealed class SessionBloatAnalyzerView : Border
    {
        // 顶部 3 列 Bento 诊断摘要卡控件
        private TextBlock _kpiPeakValue;
        private TextBlock _kpiPeakSub;
        private Border _kpiPeakPill;
        private TextBlock _kpiPeakPillText;
        private Ellipse _kpiPeakPillDot;

        private TextBlock _kpiAmpValue;
        private TextBlock _kpiAmpSub;
        private Border _kpiAmpPill;
        private TextBlock _kpiAmpPillText;
        private Ellipse _kpiAmpPillDot;

        private TextBlock _kpiSavingsValue;
        private TextBlock _kpiSavingsSub;
        private Border _kpiSavingsPill;
        private TextBlock _kpiSavingsPillText;
        private Ellipse _kpiSavingsPillDot;

        // 中部左：选中会话上下文阶梯爬坡曲线图控件
        private TextBlock _chartBurstMetaText;
        private Border _chartSnowballBadge;
        private TextBlock _chartSnowballBadgeText;
        private Ellipse _chartSnowballBadgeDot;
        private Canvas _chartCanvas;
        private Border _chartHoverTip;
        private TextBlock _chartHoverTitle;
        private TextBlock _chartHoverBody;
        private Line _chartHoverLine;
        private Ellipse _chartHoverDot;
        private StackPanel _chartEmptyPanel;

        // 中部右：全局工具读写放大诊断列表
        private StackPanel _toolDiagListPanel;
        private TextBlock _toolDiagSummaryBadge;

        // 底部：连续编码会话簇排行表 + 筛选器 + 优化贴士条
        private TextBlock _tableSubTitle;
        private Button[] _filterButtons;
        private int _activeFilterMode; // 0=全部, 1=膨胀关注(>=60K), 2=雪球预警(>=120K)
        private DataGrid _burstGrid;
        private StackPanel _gridEmptyOverlay;

        // 视图运行时状态
        private ContextBloatReport _currentReport;
        private List<SessionBurstSummary> _filteredBursts;
        private SessionBurstSummary _selectedBurst;
        private string _selectedBurstId;
        private bool _suppressSelectionChanged;
        private bool _isDark;
        private bool _isCny;
        private double _usdToCny;

        // 阶梯图坐标映射缓存（用于鼠标悬停毫秒级命中计算）
        private double[] _plotTurnX;
        private double[] _plotTurnY;
        private double _plotLeft;
        private double _plotRight;
        private double _plotTop;
        private double _plotBottom;

        public SessionBloatAnalyzerView()
        {
            _isDark = true;
            _isCny = true;
            _usdToCny = ExchangeRateService.DefaultRate;
            _filteredBursts = new List<SessionBurstSummary>();

            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            SnapsToDevicePixels = true;

            BuildViewLayout();
        }

        public void RenderBloatAnalysis(
            IList<RecordBucket> allRecords,
            IList<RecentRequestRecord> recentRequests,
            bool isCny,
            double usdToCny,
            bool isDark)
        {
            _isCny = isCny;
            _usdToCny = (double.IsNaN(usdToCny) || double.IsInfinity(usdToCny) || usdToCny <= 0.0)
                ? ExchangeRateService.DefaultRate
                : usdToCny;
            _isDark = isDark;

            _currentReport = ContextBloatAnalyzer.Analyze(allRecords, recentRequests, _isCny, _usdToCny);

            UpdateTopBentoCards(_currentReport);
            UpdateToolEfficiencyPanel(_currentReport);
            UpdateFilterButtonLabels(_currentReport);
            ApplyBurstFilterAndRefreshGrid(true);
        }

        private void BuildViewLayout()
        {
            Grid rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 185 });

            UIElement topBento = BuildTopBentoRow();
            Grid.SetRow(topBento, 0);
            rootGrid.Children.Add(topBento);

            UIElement middleSection = BuildMiddleChartAndEfficiencyRow();
            Grid.SetRow(middleSection, 1);
            rootGrid.Children.Add(middleSection);

            UIElement bottomTable = BuildBottomBurstTableCard();
            Grid.SetRow(bottomTable, 2);
            rootGrid.Children.Add(bottomTable);

            Child = rootGrid;
        }

        private UIElement BuildTopBentoRow()
        {
            Grid bento = new Grid();
            bento.Margin = new Thickness(0, 0, 0, 10);
            bento.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bento.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bento.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bento.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bento.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border card1 = CreateBentoKpiCard(
                UiIcons.Activity,
                "峰值单次上下文 Token",
                "正常",
                out _kpiPeakValue,
                out _kpiPeakSub,
                out _kpiPeakPill,
                out _kpiPeakPillText,
                out _kpiPeakPillDot);
            card1.ToolTip = "单次请求发送给模型的最大上下文（新增输入 + 缓存读取 + 缓存写入）。超过 100K 后每轮费用会快速上升。";
            Grid.SetColumn(card1, 0);
            bento.Children.Add(card1);

            Border card2 = CreateBentoKpiCard(
                UiIcons.Models,
                "全局平均读写放大比",
                "均衡",
                out _kpiAmpValue,
                out _kpiAmpSub,
                out _kpiAmpPill,
                out _kpiAmpPillText,
                out _kpiAmpPillDot);
            card2.ToolTip = "每生成 1 个输出 Token 平均伴随读取的历史上下文输入 Token 倍数，同时展示未命中缓存的裸输入占比。";
            Grid.SetColumn(card2, 2);
            bento.Children.Add(card2);

            Border card3 = CreateBentoKpiCard(
                UiIcons.Shield,
                "长会话与压缩可省",
                "健康",
                out _kpiSavingsValue,
                out _kpiSavingsSub,
                out _kpiSavingsPill,
                out _kpiSavingsPillText,
                out _kpiSavingsPillDot);
            card3.ToolTip = "模拟在连续编码会话上下文突破 60K 时执行 /compact 压缩一半历史体积（扣除摘要请求自身开销后）预计可省下的净费用。";
            Grid.SetColumn(card3, 4);
            bento.Children.Add(card3);

            return bento;
        }

        private static Border CreateBentoKpiCard(
            string iconData,
            string title,
            string initialBadge,
            out TextBlock valueTb,
            out TextBlock subTb,
            out Border badgeBorder,
            out TextBlock badgeText,
            out Ellipse badgeDot)
        {
            Border card = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 11));

            StackPanel stack = new StackPanel();

            DockPanel hdr = new DockPanel();
            hdr.Margin = new Thickness(0, 0, 0, 6);

            badgeBorder = ModernControlsFactory.CreateStatusBadge(initialBadge, "neutral", out badgeText, out badgeDot);
            DockPanel.SetDock(badgeBorder, Dock.Right);
            hdr.Children.Add(badgeBorder);

            StackPanel leftTitle = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Path ic = UiIcons.CreateIcon(iconData, 13, "FgMuted");
            ic.Margin = new Thickness(0, 0, 6, 0);
            leftTitle.Children.Add(ic);

            TextBlock titleTb = new TextBlock
            {
                Text = title,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            leftTitle.Children.Add(titleTb);
            hdr.Children.Add(leftTitle);

            stack.Children.Add(hdr);

            valueTb = new TextBlock
            {
                Text = "—",
                FontSize = DesignSystem.TextHeading,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ModernControlsFactory.EnableTabularNumbers(valueTb, false);
            valueTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            stack.Children.Add(valueTb);

            subTb = new TextBlock
            {
                Text = "等待扫描数据...",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            stack.Children.Add(subTb);

            card.Child = stack;
            return card;
        }

        private UIElement BuildMiddleChartAndEfficiencyRow()
        {
            Grid midGrid = new Grid();
            midGrid.Margin = new Thickness(0, 0, 0, 10);
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.15, GridUnitType.Star) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 265 });

            // 左侧卡片：选中会话上下文爬坡曲线（Canvas/Path 阶梯折线图）
            Border chartCard = ModernControlsFactory.CreateCard(new Thickness(14, 11, 14, 10));
            Grid chartLayout = new Grid();
            chartLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chartLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(166) });

            DockPanel chartHdr = new DockPanel();
            chartHdr.Margin = new Thickness(0, 0, 0, 6);

            // 图例区
            StackPanel legendPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            legendPanel.Children.Add(CreateLegendItem("实际上下文", "AccentPrimary", false));
            legendPanel.Children.Add(CreateLegendItem("60K /compact 模拟", "SemanticSuccess", true));
            legendPanel.Children.Add(CreateLegendItem("100K 警戒线", "SemanticDanger", true));
            DockPanel.SetDock(legendPanel, Dock.Right);
            chartHdr.Children.Add(legendPanel);

            StackPanel chartTitleWrap = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock chartTitle = new TextBlock
            {
                Text = "选中会话上下文爬坡曲线",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            chartTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            chartTitleWrap.Children.Add(chartTitle);

            _chartSnowballBadge = ModernControlsFactory.CreateStatusBadge("待选择", "neutral", out _chartSnowballBadgeText, out _chartSnowballBadgeDot);
            _chartSnowballBadge.Margin = new Thickness(8, 0, 8, 0);
            chartTitleWrap.Children.Add(_chartSnowballBadge);

            _chartBurstMetaText = new TextBlock
            {
                Text = "",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _chartBurstMetaText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            chartTitleWrap.Children.Add(_chartBurstMetaText);

            chartHdr.Children.Add(chartTitleWrap);
            Grid.SetRow(chartHdr, 0);
            chartLayout.Children.Add(chartHdr);

            // 画布宿主
            Grid canvasHost = new Grid();
            canvasHost.ClipToBounds = true;

            _chartCanvas = new Canvas();
            _chartCanvas.Background = Brushes.Transparent;
            _chartCanvas.SnapsToDevicePixels = true;
            _chartCanvas.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                RedrawSelectedBurstChart();
            };
            _chartCanvas.MouseMove += OnChartCanvasMouseMove;
            _chartCanvas.MouseLeave += OnChartCanvasMouseLeave;
            canvasHost.Children.Add(_chartCanvas);

            // 空状态提示层
            _chartEmptyPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Path emptyIcon = UiIcons.CreateIcon(UiIcons.Activity, 22, "FgMuted");
            emptyIcon.Margin = new Thickness(0, 0, 0, 6);
            _chartEmptyPanel.Children.Add(emptyIcon);

            TextBlock emptyTitle = new TextBlock
            {
                Text = "暂无带实时时间戳的连续会话轨迹",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            _chartEmptyPanel.Children.Add(emptyTitle);

            TextBlock emptyDesc = new TextBlock
            {
                Text = "有带时间戳的请求后，点下方任意会话即可查看每一轮上下文的增长；右侧按全部历史统计各工具的读写比。",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(24, 3, 24, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyDesc.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            _chartEmptyPanel.Children.Add(emptyDesc);
            canvasHost.Children.Add(_chartEmptyPanel);

            BuildChartHoverElements();

            Grid.SetRow(canvasHost, 1);
            chartLayout.Children.Add(canvasHost);
            chartCard.Child = chartLayout;
            Grid.SetColumn(chartCard, 0);
            midGrid.Children.Add(chartCard);

            // 右侧卡片：全局各工具读写放大比与未命中缓存裸输入诊断
            Border diagCard = ModernControlsFactory.CreateCard(new Thickness(14, 11, 12, 10));
            Grid diagLayout = new Grid();
            diagLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            diagLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(166) });

            DockPanel diagHdr = new DockPanel();
            diagHdr.Margin = new Thickness(0, 0, 2, 6);

            Border diagPill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            diagPill.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            diagPill.SetResourceReference(Border.BorderBrushProperty, "BorderCard");

            _toolDiagSummaryBadge = new TextBlock
            {
                Text = "全部历史",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            _toolDiagSummaryBadge.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            diagPill.Child = _toolDiagSummaryBadge;
            DockPanel.SetDock(diagPill, Dock.Right);
            diagHdr.Children.Add(diagPill);

            TextBlock diagTitle = new TextBlock
            {
                Text = "工具上下文放大与裸读占比",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            diagTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            diagHdr.Children.Add(diagTitle);

            Grid.SetRow(diagHdr, 0);
            diagLayout.Children.Add(diagHdr);

            ScrollViewer diagSv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 4, 0)
            };
            _toolDiagListPanel = new StackPanel();
            diagSv.Content = _toolDiagListPanel;
            Grid.SetRow(diagSv, 1);
            diagLayout.Children.Add(diagSv);

            diagCard.Child = diagLayout;
            Grid.SetColumn(diagCard, 2);
            midGrid.Children.Add(diagCard);

            return midGrid;
        }

        private static UIElement CreateLegendItem(string text, string brushResourceKey, bool isDashed)
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            Line sample = new Line
            {
                X1 = 0,
                Y1 = 5,
                X2 = 14,
                Y2 = 5,
                Width = 14,
                Height = 10,
                StrokeThickness = 2.0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            if (isDashed)
            {
                sample.StrokeDashArray = new DoubleCollection(new double[] { 2.0, 1.5 });
            }
            sample.SetResourceReference(Shape.StrokeProperty, brushResourceKey);
            sp.Children.Add(sample);

            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            sp.Children.Add(tb);

            return sp;
        }

        private void BuildChartHoverElements()
        {
            _chartHoverLine = new Line
            {
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection(new double[] { 3.0, 2.0 }),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _chartHoverLine.SetResourceReference(Shape.StrokeProperty, "FgMuted");

            _chartHoverDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                StrokeThickness = 2.0,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _chartHoverDot.SetResourceReference(Shape.FillProperty, "BgCard");
            _chartHoverDot.SetResourceReference(Shape.StrokeProperty, "AccentPrimary");

            _chartHoverTip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 6, 9, 6),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _chartHoverTip.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            _chartHoverTip.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            StackPanel tipStack = new StackPanel();
            _chartHoverTitle = new TextBlock
            {
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            };
            _chartHoverTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            tipStack.Children.Add(_chartHoverTitle);

            _chartHoverBody = new TextBlock
            {
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                LineHeight = 15
            };
            _chartHoverBody.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            tipStack.Children.Add(_chartHoverBody);

            _chartHoverTip.Child = tipStack;
        }

        private UIElement BuildBottomBurstTableCard()
        {
            Border tableCard = ModernControlsFactory.CreateCard(new Thickness(0));
            tableCard.Margin = new Thickness(0, 0, 0, 10);

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 表头控制栏
            Border hdrBar = new Border
            {
                Padding = new Thickness(14, 9, 14, 9),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            hdrBar.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

            DockPanel hdrDock = new DockPanel();

            Border segContainer = ModernControlsFactory.CreateSegmentedContainer();
            StackPanel segPanel = (StackPanel)segContainer.Child;
            Button btnAll = ModernControlsFactory.CreateSegmentedItem("全部会话");
            Button btnElevated = ModernControlsFactory.CreateSegmentedItem("膨胀 ≥60K");
            Button btnSnowball = ModernControlsFactory.CreateSegmentedItem("过长 ≥120K");

            _filterButtons = new Button[] { btnAll, btnElevated, btnSnowball };
            for (int i = 0; i < _filterButtons.Length; i++)
            {
                int capturedIdx = i;
                _filterButtons[i].Click += delegate(object s, RoutedEventArgs e)
                {
                    if (_activeFilterMode == capturedIdx) return;
                    _activeFilterMode = capturedIdx;
                    ModernControlsFactory.UpdateSegmentedGroup(_filterButtons, _activeFilterMode);
                    ApplyBurstFilterAndRefreshGrid(false);
                };
                segPanel.Children.Add(_filterButtons[i]);
            }
            ModernControlsFactory.UpdateSegmentedGroup(_filterButtons, 0);
            DockPanel.SetDock(segContainer, Dock.Right);
            hdrDock.Children.Add(segContainer);

            StackPanel titleLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Path tblIcon = UiIcons.CreateIcon(UiIcons.Terminal, 14, "AccentText");
            tblIcon.Margin = new Thickness(0, 0, 7, 0);
            titleLeft.Children.Add(tblIcon);

            TextBlock tblTitle = new TextBlock
            {
                Text = "连续编码会话簇排行",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextLabel,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            tblTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            titleLeft.Children.Add(tblTitle);

            _tableSubTitle = new TextBlock
            {
                Text = "间隔 ≤25 分钟自动聚类 · 点击任意行检视上下文爬坡曲线",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextCaption,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _tableSubTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            titleLeft.Children.Add(_tableSubTitle);

            hdrDock.Children.Add(titleLeft);
            hdrBar.Child = hdrDock;
            Grid.SetRow(hdrBar, 0);
            layout.Children.Add(hdrBar);

            // DataGrid 与空状态叠层
            Grid gridHost = new Grid();
            _burstGrid = ModernDataGridFactory.CreateModernDataGrid();
            _burstGrid.Cursor = Cursors.Hand;

            _burstGrid.Columns.Add(MakeSnowballStatusColumn());
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _burstGrid, "开始 · 时长", "StartAndDurationText", "StartTimestampMs",
                new DataGridLength(206), false, true, false));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeBadgeColumn(
                "AI 工具", "ToolDisplayText", "Tool",
                new DataGridLength(126)));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _burstGrid, "主力模型", "PrimaryModel", "PrimaryModel",
                new DataGridLength(1.15, DataGridLengthUnitType.Star), false, false, false));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _burstGrid, "轮次", "TurnsText", "Turns",
                new DataGridLength(78), true, true, false));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _burstGrid, "上下文 首轮→峰值", "ContextClimbText", "PeakContextTokens",
                new DataGridLength(148), true, true, true));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeShareBarColumn(
                "膨胀倍数", "BloatMultiplierText", "BloatBarRatio", "BloatMultiplier",
                new DataGridLength(92)));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _burstGrid, "缓存命中", "AvgCacheHitText", "AvgCacheHitRate",
                new DataGridLength(86), true, true, false));
            _burstGrid.Columns.Add(ModernDataGridFactory.MakeTextOrMonoColumn(
                _burstGrid, "累计花费", "FormattedCost", "CostUsd",
                new DataGridLength(96), true, true, true));
            _burstGrid.Columns.Add(MakeCompactSavingsColumn(_burstGrid));

            _burstGrid.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (_suppressSelectionChanged) return;
                SessionBurstSummary sel = _burstGrid.SelectedItem as SessionBurstSummary;
                if (sel != null)
                {
                    _selectedBurst = sel;
                    _selectedBurstId = sel.BurstId;
                    RedrawSelectedBurstChart();
                }
            };

            gridHost.Children.Add(_burstGrid);

            _gridEmptyOverlay = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            TextBlock emptyGridTb = new TextBlock
            {
                Text = "当前筛选条件下没有匹配的连续编码会话簇",
                FontFamily = DesignSystem.FontUi,
                FontSize = DesignSystem.TextBody
            };
            emptyGridTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            _gridEmptyOverlay.Children.Add(emptyGridTb);
            gridHost.Children.Add(_gridEmptyOverlay);

            Grid.SetRow(gridHost, 1);
            layout.Children.Add(gridHost);
            tableCard.Child = layout;

            return tableCard;
        }

        private static DataGridTemplateColumn MakeSnowballStatusColumn()
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "状态";
            col.SortMemberPath = "SnowballLevel";
            col.Width = new DataGridLength(96);

            DataTemplate dt = new DataTemplate();

            FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border), "SbBd");
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            pill.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1));
            pill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            pill.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            pill.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            pill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("SemanticSuccessBg"));
            pill.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticSuccessBorder"));

            FrameworkElementFactory sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            sp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory dot = new FrameworkElementFactory(typeof(Ellipse), "SbDot");
            dot.SetValue(FrameworkElement.WidthProperty, 5.0);
            dot.SetValue(FrameworkElement.HeightProperty, 5.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetValue(Shape.FillProperty, new DynamicResourceExtension("SemanticSuccess"));
            sp.AppendChild(dot);

            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock), "SbTxt");
            txt.SetBinding(TextBlock.TextProperty, new Binding("SnowballRating"));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            txt.SetValue(TextBlock.FontSizeProperty, 11.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"));
            sp.AppendChild(txt);

            pill.AppendChild(sp);
            dt.VisualTree = pill;

            DataTrigger warnTrig = new DataTrigger();
            warnTrig.Binding = new Binding("SnowballVariant");
            warnTrig.Value = "warning";
            warnTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SemanticWarningBg"), "SbBd"));
            warnTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticWarningBorder"), "SbBd"));
            warnTrig.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension("SemanticWarning"), "SbDot"));
            warnTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticWarning"), "SbTxt"));
            dt.Triggers.Add(warnTrig);

            DataTrigger dangerTrig = new DataTrigger();
            dangerTrig.Binding = new Binding("SnowballVariant");
            dangerTrig.Value = "danger";
            dangerTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SemanticDangerBg"), "SbBd"));
            dangerTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticDangerBorder"), "SbBd"));
            dangerTrig.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension("SemanticDanger"), "SbDot"));
            dangerTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticDanger"), "SbTxt"));
            dt.Triggers.Add(dangerTrig);

            col.CellTemplate = dt;
            return col;
        }

        private static DataGridTemplateColumn MakeCompactSavingsColumn(DataGrid dg)
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = "压缩后可省";
            col.SortMemberPath = "EstimatedCompactSavingsUsd";
            col.Width = new DataGridLength(122);

            if (dg != null && dg.ColumnHeaderStyle != null)
            {
                Style hdrRight = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), dg.ColumnHeaderStyle);
                hdrRight.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
                col.HeaderStyle = hdrRight;
            }

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory tb = new FrameworkElementFactory(typeof(TextBlock), "SavTxt");
            tb.SetBinding(TextBlock.TextProperty, new Binding("FormattedCompactSavings"));
            tb.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            tb.SetValue(TextBlock.FontSizeProperty, 12.0);
            tb.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            tb.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            tb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tb.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgMuted"));
            dt.VisualTree = tb;

            DataTrigger posTrig = new DataTrigger();
            posTrig.Binding = new Binding("SavingsState");
            posTrig.Value = "positive";
            posTrig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SemanticSuccess"), "SavTxt"));
            posTrig.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold, "SavTxt"));
            dt.Triggers.Add(posTrig);

            col.CellTemplate = dt;
            return col;
        }

        private static void ApplyBadgeVariant(Border pill, TextBlock label, Ellipse dot, string text, string variant)
        {
            if (pill == null || label == null || dot == null) return;

            string bgKey = "BgSurface";
            string bdKey = "BorderCard";
            string fgKey = "FgSecondary";

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticSuccessBg";
                bdKey = "SemanticSuccessBorder";
                fgKey = "SemanticSuccess";
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticWarningBg";
                bdKey = "SemanticWarningBorder";
                fgKey = "SemanticWarning";
            }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticDangerBg";
                bdKey = "SemanticDangerBorder";
                fgKey = "SemanticDanger";
            }
            else if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "AccentSubtleBg";
                bdKey = "AccentSubtleBorder";
                fgKey = "AccentText";
            }

            pill.SetResourceReference(Border.BackgroundProperty, bgKey);
            pill.SetResourceReference(Border.BorderBrushProperty, bdKey);
            dot.SetResourceReference(Shape.FillProperty, fgKey);
            label.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            label.Text = text ?? "";
        }

        private void UpdateTopBentoCards(ContextBloatReport rep)
        {
            if (rep == null) return;

            // 1. 峰值单次上下文 Token
            long peak = rep.PeakSingleTurnContextTokens;
            _kpiPeakValue.Text = peak > 0L ? FormatterUtil.FormatTokens(peak) : "0";
            _kpiPeakSub.Text = !string.IsNullOrEmpty(rep.PeakContextSubtitle) ? rep.PeakContextSubtitle : "暂无上下文请求记录";

            if (peak >= ContextBloatAnalyzer.SnowballWarningTokens)
                ApplyBadgeVariant(_kpiPeakPill, _kpiPeakPillText, _kpiPeakPillDot, "超 120K 预警", "danger");
            else if (peak >= ContextBloatAnalyzer.CompactRecommendTokens)
                ApplyBadgeVariant(_kpiPeakPill, _kpiPeakPillText, _kpiPeakPillDot, "超 60K 膨胀", "warning");
            else if (peak > 0L)
                ApplyBadgeVariant(_kpiPeakPill, _kpiPeakPillText, _kpiPeakPillDot, "紧凑健康", "success");
            else
                ApplyBadgeVariant(_kpiPeakPill, _kpiPeakPillText, _kpiPeakPillDot, "无数据", "neutral");

            // 2. 全局平均读写放大比 & 未命中缓存裸输入占比
            _kpiAmpValue.Text = rep.GlobalAmplificationText;
            if (rep.GlobalTotalContextTokens > 0L)
            {
                string rawPct = (rep.GlobalUncachedInputRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
                string hitPct = (rep.GlobalCacheHitRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
                _kpiAmpSub.Text = "裸输入占比 " + rawPct + " · 缓存命中 " + hitPct + " · 总读 " + FormatterUtil.FormatTokens(rep.GlobalTotalContextTokens);

                if (rep.GlobalAmplificationRatio >= 85.0)
                    ApplyBadgeVariant(_kpiAmpPill, _kpiAmpPillText, _kpiAmpPillDot, "重度放大", "danger");
                else if (rep.GlobalAmplificationRatio >= 45.0)
                    ApplyBadgeVariant(_kpiAmpPill, _kpiAmpPillText, _kpiAmpPillDot, "中度放大", "warning");
                else
                    ApplyBadgeVariant(_kpiAmpPill, _kpiAmpPillText, _kpiAmpPillDot, "读写精简", "success");
            }
            else
            {
                _kpiAmpSub.Text = "等待累计输入与输出 Token 数据";
                ApplyBadgeVariant(_kpiAmpPill, _kpiAmpPillText, _kpiAmpPillDot, "无数据", "neutral");
            }

            // 3. 长上下文雪球会话数 & 预计 /compact 可省金额
            string savedMoney = FormatterUtil.FormatMoney(rep.TotalEstimatedCompactSavingsUsd, _isCny, _usdToCny);
            if (rep.TotalBurstCount > 0)
            {
                _kpiSavingsValue.Text = rep.SnowballBurstCount.ToString(CultureInfo.InvariantCulture) + " 个长会话 · 可省 " + savedMoney;
                _kpiSavingsSub.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "共 {0} 个连续会话簇（{1} 个中度膨胀 ≥60K）· 60K 减半压缩模拟",
                    rep.TotalBurstCount,
                    rep.ModerateBurstCount);

                if (rep.SnowballBurstCount > 0)
                    ApplyBadgeVariant(_kpiSavingsPill, _kpiSavingsPillText, _kpiSavingsPillDot, "建议 /compact", "danger");
                else if (rep.ModerateBurstCount > 0)
                    ApplyBadgeVariant(_kpiSavingsPill, _kpiSavingsPillText, _kpiSavingsPillDot, "存在优化空间", "warning");
                else
                    ApplyBadgeVariant(_kpiSavingsPill, _kpiSavingsPillText, _kpiSavingsPillDot, "无需压缩", "success");
            }
            else if (rep.IsSavingsFromArchiveFallback)
            {
                _kpiSavingsValue.Text = "约省 " + savedMoney;
                _kpiSavingsSub.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "检出 {0} 个高均值日归档桶（≥60K/轮）· 待新请求生成逐轮簇",
                    rep.ModerateBurstCount);
                ApplyBadgeVariant(_kpiSavingsPill, _kpiSavingsPillText, _kpiSavingsPillDot, "归档推算", "warning");
            }
            else
            {
                _kpiSavingsValue.Text = "0 个长会话 · " + savedMoney;
                _kpiSavingsSub.Text = "没有上下文超过 60K 的会话";
                ApplyBadgeVariant(_kpiSavingsPill, _kpiSavingsPillText, _kpiSavingsPillDot, "健康", "success");
            }
        }

        private void UpdateToolEfficiencyPanel(ContextBloatReport rep)
        {
            _toolDiagListPanel.Children.Clear();

            if (rep == null || rep.ToolDiagnostics == null || rep.ToolDiagnostics.Count == 0)
            {
                _toolDiagSummaryBadge.Text = "0 工具";
                TextBlock emptyTb = new TextBlock
                {
                    Text = "暂无各工具全局输入/输出效率统计",
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextCaption,
                    Margin = new Thickness(0, 28, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                emptyTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                _toolDiagListPanel.Children.Add(emptyTb);
                return;
            }

            _toolDiagSummaryBadge.Text = rep.ToolDiagnostics.Count.ToString(CultureInfo.InvariantCulture) + " 个工具";

            for (int i = 0; i < rep.ToolDiagnostics.Count; i++)
            {
                ToolContextEfficiencyRow row = rep.ToolDiagnostics[i];
                Border itemBd = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 7, 9, 7),
                    Margin = new Thickness(0, 0, 0, 6),
                    BorderThickness = new Thickness(1)
                };
                itemBd.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                itemBd.SetResourceReference(Border.BorderBrushProperty, "BorderSubtle");

                StackPanel sp = new StackPanel();

                // 第一行：工具色点 + 名称 + 右侧读写放大比
                DockPanel line1 = new DockPanel();
                line1.Margin = new Thickness(0, 0, 0, 4);

                TextBlock ampTb = new TextBlock
                {
                    Text = "读写比 " + row.AmplificationText,
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (row.AmplificationRatio >= 85.0)
                    ampTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
                else if (row.AmplificationRatio >= 45.0)
                    ampTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
                else
                    ampTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                DockPanel.SetDock(ampTb, Dock.Right);
                line1.Children.Add(ampTb);

                StackPanel toolLeft = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Ellipse colorDot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = DesignSystem.BrushHex(DesignSystem.GetProviderColorHex(row.Tool, _isDark))
                };
                toolLeft.Children.Add(colorDot);

                TextBlock nameTb = new TextBlock
                {
                    Text = row.Tool,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextBody,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                toolLeft.Children.Add(nameTb);
                line1.Children.Add(toolLeft);
                sp.Children.Add(line1);

                // 第二行：裸输入占比进度微条 + 均轮上下文详情
                Grid barTrackGrid = new Grid
                {
                    Height = 4,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                Border trackBg = new Border
                {
                    CornerRadius = new CornerRadius(2)
                };
                trackBg.SetResourceReference(Border.BackgroundProperty, "BorderCard");
                barTrackGrid.Children.Add(trackBg);

                double rawRatio = row.UncachedInputRatio;
                if (rawRatio < 0.0) rawRatio = 0.0;
                if (rawRatio > 1.0) rawRatio = 1.0;

                Grid propGrid = new Grid();
                propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rawRatio, GridUnitType.Star) });
                propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - rawRatio, GridUnitType.Star) });

                Border rawFill = new Border
                {
                    CornerRadius = new CornerRadius(2)
                };
                rawFill.SetResourceReference(Border.BackgroundProperty, rawRatio >= 0.35 ? "SemanticWarning" : "AccentPrimary");
                Grid.SetColumn(rawFill, 0);
                propGrid.Children.Add(rawFill);
                barTrackGrid.Children.Add(propGrid);
                sp.Children.Add(barTrackGrid);

                DockPanel line2 = new DockPanel();
                TextBlock tagTb = new TextBlock
                {
                    Text = row.EfficiencyTag,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = DesignSystem.TextCaption,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (string.Equals(row.EfficiencyVariant, "danger", StringComparison.Ordinal))
                    tagTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticDanger");
                else if (string.Equals(row.EfficiencyVariant, "warning", StringComparison.Ordinal))
                    tagTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticWarning");
                else
                    tagTb.SetResourceReference(TextBlock.ForegroundProperty, "SemanticSuccess");
                DockPanel.SetDock(tagTb, Dock.Right);
                line2.Children.Add(tagTb);

                TextBlock metaTb = new TextBlock
                {
                    Text = "裸输入 " + row.UncachedInputText + " · 均轮 " + FormatterUtil.FormatTokens(row.AvgContextPerTurn),
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                metaTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                line2.Children.Add(metaTb);
                sp.Children.Add(line2);

                itemBd.Child = sp;
                itemBd.ToolTip = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\n总轮次: {1} 轮 · 均轮上下文: {2}\n累计输入上下文: {3} (纯新增裸读 {4} / 缓存命中 {5})\n累计输出: {6} · 读写放大比: {7}",
                    row.Tool,
                    row.Turns,
                    FormatterUtil.FormatTokens(row.AvgContextPerTurn),
                    FormatterUtil.FormatTokens(row.TotalContextTokens),
                    row.UncachedInputText,
                    row.CacheHitText,
                    FormatterUtil.FormatTokens(row.OutputTokens),
                    row.AmplificationText);

                _toolDiagListPanel.Children.Add(itemBd);
            }
        }

        private void UpdateFilterButtonLabels(ContextBloatReport rep)
        {
            if (_filterButtons == null || _filterButtons.Length < 3 || rep == null)
                return;

            int allCnt = rep.TotalBurstCount;
            int elevCnt = rep.SnowballBurstCount + rep.ModerateBurstCount;
            int snowCnt = rep.SnowballBurstCount;

            _filterButtons[0].Content = "全部会话 (" + allCnt.ToString(CultureInfo.InvariantCulture) + ")";
            _filterButtons[1].Content = "膨胀 ≥60K (" + elevCnt.ToString(CultureInfo.InvariantCulture) + ")";
            _filterButtons[2].Content = "过长 ≥120K (" + snowCnt.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private void ApplyBurstFilterAndRefreshGrid(bool keepScrollNearTop)
        {
            List<SessionBurstSummary> nextList = new List<SessionBurstSummary>();
            if (_currentReport != null && _currentReport.Bursts != null)
            {
                for (int i = 0; i < _currentReport.Bursts.Count; i++)
                {
                    SessionBurstSummary b = _currentReport.Bursts[i];
                    if (_activeFilterMode == 1 && b.SnowballLevel < 1) continue;
                    if (_activeFilterMode == 2 && b.SnowballLevel < 2) continue;
                    nextList.Add(b);
                }
            }
            _filteredBursts = nextList;

            _tableSubTitle.Text = _filteredBursts.Count > 0
                ? string.Format(CultureInfo.InvariantCulture, "当前展示 {0} 个连续会话簇（间隔 ≤25m 聚类）· 点击任意行切换上方阶梯图", _filteredBursts.Count)
                : "暂无匹配的连续编码会话簇 · 间隔 ≤25m 自动聚类";

            _gridEmptyOverlay.Visibility = _filteredBursts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _suppressSelectionChanged = true;
            try
            {
                ModernDataGridFactory.UpdateItemsPreservingState(
                    _burstGrid,
                    _filteredBursts,
                    delegate(object obj)
                    {
                        SessionBurstSummary sb = obj as SessionBurstSummary;
                        return sb != null ? sb.BurstId : null;
                    },
                    keepScrollNearTop);

                SessionBurstSummary resolved = _burstGrid.SelectedItem as SessionBurstSummary;
                if (resolved == null && !string.IsNullOrEmpty(_selectedBurstId) && _filteredBursts.Count > 0)
                {
                    for (int i = 0; i < _filteredBursts.Count; i++)
                    {
                        if (string.Equals(_filteredBursts[i].BurstId, _selectedBurstId, StringComparison.Ordinal))
                        {
                            _burstGrid.SelectedItem = _filteredBursts[i];
                            resolved = _filteredBursts[i];
                            break;
                        }
                    }
                }
                if (resolved == null && _filteredBursts.Count > 0)
                {
                    _burstGrid.SelectedIndex = 0;
                    resolved = _burstGrid.SelectedItem as SessionBurstSummary;
                    if (resolved == null)
                        resolved = _filteredBursts[0];
                }

                _selectedBurst = resolved;
                if (resolved != null)
                {
                    _selectedBurstId = resolved.BurstId;
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            RedrawSelectedBurstChart();
        }

        private void RedrawSelectedBurstChart()
        {
            if (_chartCanvas == null) return;

            _chartCanvas.Children.Clear();
            _plotTurnX = null;
            _plotTurnY = null;

            SessionBurstSummary burst = _selectedBurst;
            if (burst == null || burst.TurnPoints == null || burst.TurnPoints.Count == 0)
            {
                _chartEmptyPanel.Visibility = Visibility.Visible;
                _chartBurstMetaText.Text = "";
                ApplyBadgeVariant(_chartSnowballBadge, _chartSnowballBadgeText, _chartSnowballBadgeDot, "暂无会话", "neutral");
                return;
            }

            _chartEmptyPanel.Visibility = Visibility.Collapsed;
            ApplyBadgeVariant(_chartSnowballBadge, _chartSnowballBadgeText, _chartSnowballBadgeDot, burst.SnowballRating, burst.SnowballVariant);
            _chartBurstMetaText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} · {2} 轮 · 峰值 {3} ({4}) · 花费 {5}",
                burst.Tool,
                burst.PrimaryModel,
                burst.Turns,
                burst.PeakContextText,
                burst.BloatMultiplierText,
                burst.FormattedCost);

            double w = _chartCanvas.ActualWidth;
            double h = _chartCanvas.ActualHeight;
            if (w < 60.0 || h < 50.0)
                return;

            double leftPad = 48.0;
            double rightPad = 16.0;
            double topPad = 16.0;
            double bottomPad = 22.0;

            double plotW = w - leftPad - rightPad;
            double plotH = h - topPad - bottomPad;
            if (plotW <= 20.0 || plotH <= 20.0)
                return;

            _plotLeft = leftPad;
            _plotRight = w - rightPad;
            _plotTop = topPad;
            _plotBottom = h - bottomPad;

            // 计算纵轴上限（保证 100K 警戒虚线始终处于可视区间内，直观对比紧凑与膨胀会话）
            long rawMax = burst.PeakContextTokens;
            if (rawMax < 115000L) rawMax = 115000L;
            double maxTokens = ComputeNiceCeiling((double)rawMax * 1.10);

            SolidColorBrush gridBrush = DesignSystem.ThemeBrush("BorderCard", _isDark);
            SolidColorBrush axisFg = DesignSystem.ThemeBrush("FgMuted", _isDark);
            SolidColorBrush warnLineBrush = DesignSystem.ThemeBrush("SemanticDanger", _isDark);
            SolidColorBrush compactLineBrush = DesignSystem.ThemeBrush("ChartCacheRead", _isDark);

            // 1. 背景水平网格线与纵轴刻度（0、50%、100%）
            double[] gridLevels = new double[] { 0.0, maxTokens * 0.5, maxTokens };
            for (int g = 0; g < gridLevels.Length; g++)
            {
                double val = gridLevels[g];
                double y = MapValueToY(val, maxTokens, _plotTop, plotH);

                Line gl = new Line
                {
                    X1 = _plotLeft,
                    Y1 = y,
                    X2 = _plotRight,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1.0,
                    SnapsToDevicePixels = true
                };
                _chartCanvas.Children.Add(gl);

                TextBlock yLbl = new TextBlock
                {
                    Text = FormatterUtil.FormatTokens((long)Math.Round(val)),
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    Foreground = axisFg,
                    Width = leftPad - 6.0,
                    TextAlignment = TextAlignment.Right
                };
                Canvas.SetLeft(yLbl, 0.0);
                Canvas.SetTop(yLbl, y - 7.0);
                _chartCanvas.Children.Add(yLbl);
            }

            // 2. 60K /compact 建议压缩参考虚线（当纵轴上限 <= 320K 时绘制，避免与 100K 重叠拥挤）
            if (maxTokens <= 320000.0)
            {
                double y60k = MapValueToY(ContextBloatAnalyzer.CompactRecommendTokens, maxTokens, _plotTop, plotH);
                Line line60k = new Line
                {
                    X1 = _plotLeft,
                    Y1 = y60k,
                    X2 = _plotRight,
                    Y2 = y60k,
                    Stroke = DesignSystem.ThemeBrush("SemanticWarning", _isDark),
                    StrokeThickness = 1.0,
                    Opacity = _isDark ? 0.4 : 0.55,
                    StrokeDashArray = new DoubleCollection(new double[] { 2.5, 3.0 })
                };
                _chartCanvas.Children.Add(line60k);
            }

            // 3. 100K 长上下文警戒虚线（核心视觉锚点）
            double y100k = MapValueToY(ContextBloatAnalyzer.AlertThresholdTokens, maxTokens, _plotTop, plotH);
            Line alertLine = new Line
            {
                X1 = _plotLeft,
                Y1 = y100k,
                X2 = _plotRight,
                Y2 = y100k,
                Stroke = warnLineBrush,
                StrokeThickness = 1.35,
                StrokeDashArray = new DoubleCollection(new double[] { 5.0, 3.5 }),
                SnapsToDevicePixels = true
            };
            _chartCanvas.Children.Add(alertLine);

            Border alertTag = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Background = DesignSystem.ThemeBrush("SemanticDangerBg", _isDark),
                BorderBrush = DesignSystem.ThemeBrush("SemanticDangerBorder", _isDark),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            TextBlock alertTagTb = new TextBlock
            {
                Text = "100K 警戒线",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Foreground = warnLineBrush
            };
            alertTag.Child = alertTagTb;
            Canvas.SetRight(alertTag, rightPad + 4.0);
            Canvas.SetTop(alertTag, Math.Max(2.0, y100k - 17.0));
            _chartCanvas.Children.Add(alertTag);

            // 4. 计算每一轮的阶梯折线坐标
            List<SessionTurnPoint> pts = burst.TurnPoints;
            int count = pts.Count;
            _plotTurnX = new double[count];
            _plotTurnY = new double[count];
            double[] compactY = new double[count];

            if (count == 1)
            {
                _plotTurnX[0] = _plotLeft + plotW * 0.5;
                _plotTurnY[0] = MapValueToY(pts[0].ContextTokens, maxTokens, _plotTop, plotH);
                compactY[0] = _plotTurnY[0];
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    _plotTurnX[i] = _plotLeft + (plotW * i) / (double)(count - 1);
                    _plotTurnY[i] = MapValueToY(pts[i].ContextTokens, maxTokens, _plotTop, plotH);
                    compactY[i] = MapValueToY(pts[i].CompactedContextTokens, maxTokens, _plotTop, plotH);
                }
            }

            string mainColorHex;
            if (burst.SnowballLevel >= 2)
                mainColorHex = DesignSystem.ThemeHex("SemanticDanger", _isDark);
            else if (burst.SnowballLevel == 1)
                mainColorHex = DesignSystem.ThemeHex("SemanticWarning", _isDark);
            else
                mainColorHex = DesignSystem.GetProviderColorHex(burst.Tool, _isDark);

            Color mainColor = (Color)ColorConverter.ConvertFromString(mainColorHex);
            SolidColorBrush mainStrokeBrush = DesignSystem.BrushHex(mainColorHex);

            // 5. 绘制阶梯折线图（Area 渐变填充 + 主折线 Path + 60K /compact 模拟对比阶梯虚线）
            if (count == 1)
            {
                double xLeft = _plotLeft + plotW * 0.22;
                double xRight = _plotLeft + plotW * 0.78;
                double y0 = _plotTurnY[0];

                StreamGeometry singleAreaGeo = new StreamGeometry();
                using (StreamGeometryContext ctx = singleAreaGeo.Open())
                {
                    ctx.BeginFigure(new Point(xLeft, _plotBottom), true, true);
                    ctx.LineTo(new Point(xLeft, y0), false, false);
                    ctx.LineTo(new Point(xRight, y0), false, false);
                    ctx.LineTo(new Point(xRight, _plotBottom), false, false);
                }
                singleAreaGeo.Freeze();

                Path singleAreaPath = new Path
                {
                    Data = singleAreaGeo,
                    Fill = CreateAreaGradient(mainColor, _isDark),
                    IsHitTestVisible = false
                };
                _chartCanvas.Children.Add(singleAreaPath);

                Line singleLine = new Line
                {
                    X1 = xLeft,
                    Y1 = y0,
                    X2 = xRight,
                    Y2 = y0,
                    Stroke = mainStrokeBrush,
                    StrokeThickness = 2.2,
                    IsHitTestVisible = false
                };
                _chartCanvas.Children.Add(singleLine);
            }
            else
            {
                // 5a. 实测阶梯面积渐变填充
                StreamGeometry areaGeo = new StreamGeometry();
                using (StreamGeometryContext ctx = areaGeo.Open())
                {
                    ctx.BeginFigure(new Point(_plotTurnX[0], _plotBottom), true, true);
                    ctx.LineTo(new Point(_plotTurnX[0], _plotTurnY[0]), false, false);
                    for (int i = 1; i < count; i++)
                    {
                        ctx.LineTo(new Point(_plotTurnX[i], _plotTurnY[i - 1]), false, false);
                        ctx.LineTo(new Point(_plotTurnX[i], _plotTurnY[i]), false, false);
                    }
                    ctx.LineTo(new Point(_plotTurnX[count - 1], _plotBottom), false, false);
                }
                areaGeo.Freeze();

                Path areaPath = new Path
                {
                    Data = areaGeo,
                    Fill = CreateAreaGradient(mainColor, _isDark),
                    IsHitTestVisible = false
                };
                _chartCanvas.Children.Add(areaPath);

                // 5b. 若存在 /compact 优化差值，绘制绿色模拟压缩阶梯轨迹虚线
                if (burst.HasCompactSavings)
                {
                    StreamGeometry simGeo = new StreamGeometry();
                    using (StreamGeometryContext ctx = simGeo.Open())
                    {
                        ctx.BeginFigure(new Point(_plotTurnX[0], compactY[0]), false, false);
                        for (int i = 1; i < count; i++)
                        {
                            ctx.LineTo(new Point(_plotTurnX[i], compactY[i - 1]), true, false);
                            ctx.LineTo(new Point(_plotTurnX[i], compactY[i]), true, false);
                        }
                    }
                    simGeo.Freeze();

                    Path simPath = new Path
                    {
                        Data = simGeo,
                        Stroke = compactLineBrush,
                        StrokeThickness = 1.6,
                        StrokeDashArray = new DoubleCollection(new double[] { 3.5, 2.5 }),
                        Opacity = 0.9,
                        IsHitTestVisible = false
                    };
                    _chartCanvas.Children.Add(simPath);
                }

                // 5c. 实测阶梯主折线 Path
                StreamGeometry strokeGeo = new StreamGeometry();
                using (StreamGeometryContext ctx = strokeGeo.Open())
                {
                    ctx.BeginFigure(new Point(_plotTurnX[0], _plotTurnY[0]), false, false);
                    for (int i = 1; i < count; i++)
                    {
                        ctx.LineTo(new Point(_plotTurnX[i], _plotTurnY[i - 1]), true, false);
                        ctx.LineTo(new Point(_plotTurnX[i], _plotTurnY[i]), true, false);
                    }
                }
                strokeGeo.Freeze();

                Path strokePath = new Path
                {
                    Data = strokeGeo,
                    Stroke = mainStrokeBrush,
                    StrokeThickness = 2.1,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                _chartCanvas.Children.Add(strokePath);
            }

            // 6. 绘制关键轮次圆点与峰值标注
            int markerStride = count <= 28 ? 1 : (int)Math.Ceiling(count / 24.0);
            int peakZeroIdx = burst.PeakTurnIndex - 1;
            if (peakZeroIdx < 0 || peakZeroIdx >= count) peakZeroIdx = count - 1;

            SolidColorBrush cardBgBrush = DesignSystem.ThemeBrush("BgCard", _isDark);
            for (int i = 0; i < count; i++)
            {
                bool isPeak = (i == peakZeroIdx);
                bool isEnds = (i == 0 || i == count - 1);
                if (!isPeak && !isEnds && (i % markerStride != 0))
                    continue;

                double r = isPeak ? 6.5 : 4.5;
                Ellipse dot = new Ellipse
                {
                    Width = r,
                    Height = r,
                    Fill = isPeak ? mainStrokeBrush : cardBgBrush,
                    Stroke = mainStrokeBrush,
                    StrokeThickness = isPeak ? 2.0 : 1.5,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, _plotTurnX[i] - r * 0.5);
                Canvas.SetTop(dot, _plotTurnY[i] - r * 0.5);
                _chartCanvas.Children.Add(dot);
            }

            // 峰值上下文浮标文字
            TextBlock peakCallout = new TextBlock
            {
                Text = "峰值 " + burst.PeakContextText + " (#" + burst.PeakTurnIndex.ToString(CultureInfo.InvariantCulture) + ")",
                FontFamily = DesignSystem.FontMono,
                FontSize = DesignSystem.TextCaption,
                FontWeight = FontWeights.SemiBold,
                Foreground = mainStrokeBrush,
                IsHitTestVisible = false
            };
            double calloutX = _plotTurnX[peakZeroIdx] - 36.0;
            if (calloutX < _plotLeft + 2.0) calloutX = _plotLeft + 2.0;
            if (calloutX > _plotRight - 96.0) calloutX = _plotRight - 96.0;
            double calloutY = _plotTurnY[peakZeroIdx] - 17.0;
            if (calloutY < 1.0) calloutY = _plotTurnY[peakZeroIdx] + 6.0;
            Canvas.SetLeft(peakCallout, calloutX);
            Canvas.SetTop(peakCallout, calloutY);
            _chartCanvas.Children.Add(peakCallout);

            // 7. 横轴轮次刻度（首轮、中位刻度、末轮）
            DrawXAxisTurnTicks(pts, axisFg);

            // 8. 挂载鼠标交互十字线与悬浮卡片层（置于最顶层）
            _chartHoverLine.Visibility = Visibility.Collapsed;
            _chartHoverDot.Visibility = Visibility.Collapsed;
            _chartHoverTip.Visibility = Visibility.Collapsed;
            _chartHoverDot.Stroke = mainStrokeBrush;
            _chartCanvas.Children.Add(_chartHoverLine);
            _chartCanvas.Children.Add(_chartHoverDot);
            _chartCanvas.Children.Add(_chartHoverTip);
        }

        private void DrawXAxisTurnTicks(List<SessionTurnPoint> pts, Brush axisFg)
        {
            int count = pts.Count;
            if (count == 0) return;

            if (count == 1)
            {
                TextBlock singleTick = new TextBlock
                {
                    Text = "第 1 轮 (" + pts[0].TimeLabel + ")",
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    Foreground = axisFg,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(singleTick, _plotTurnX[0] - 42.0);
                Canvas.SetTop(singleTick, _plotBottom + 4.0);
                _chartCanvas.Children.Add(singleTick);
                return;
            }

            int tickCount = Math.Min(count, 6);
            int lastDrawnIdx = -999;
            for (int t = 0; t < tickCount; t++)
            {
                int idx = (t == tickCount - 1)
                    ? (count - 1)
                    : (int)Math.Round((t * (count - 1)) / (double)(tickCount - 1));
                if (idx == lastDrawnIdx) continue;
                lastDrawnIdx = idx;

                string label = "#" + (idx + 1).ToString(CultureInfo.InvariantCulture);
                if (idx == 0 && !string.IsNullOrEmpty(pts[0].TimeLabel))
                    label = "#1 (" + pts[0].TimeLabel + ")";
                else if (idx == count - 1 && !string.IsNullOrEmpty(pts[count - 1].TimeLabel))
                    label = "#" + count.ToString(CultureInfo.InvariantCulture) + " (" + pts[count - 1].TimeLabel + ")";

                TextBlock tb = new TextBlock
                {
                    Text = label,
                    FontFamily = DesignSystem.FontMono,
                    FontSize = DesignSystem.TextCaption,
                    Foreground = axisFg,
                    IsHitTestVisible = false
                };

                double x = _plotTurnX[idx];
                if (idx == 0)
                    Canvas.SetLeft(tb, _plotLeft);
                else if (idx == count - 1)
                    Canvas.SetRight(tb, _chartCanvas.ActualWidth - _plotRight);
                else
                    Canvas.SetLeft(tb, x - 12.0);

                Canvas.SetTop(tb, _plotBottom + 4.0);
                _chartCanvas.Children.Add(tb);
            }
        }

        private void OnChartCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_selectedBurst == null ||
                _selectedBurst.TurnPoints == null ||
                _selectedBurst.TurnPoints.Count == 0 ||
                _plotTurnX == null ||
                _plotTurnY == null)
            {
                return;
            }

            Point pos = e.GetPosition(_chartCanvas);
            if (pos.X < _plotLeft - 10.0 || pos.X > _plotRight + 10.0 || pos.Y < 0.0 || pos.Y > _chartCanvas.ActualHeight)
            {
                HideChartHover();
                return;
            }

            int bestIdx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _plotTurnX.Length; i++)
            {
                double d = Math.Abs(pos.X - _plotTurnX[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            SessionTurnPoint pt = _selectedBurst.TurnPoints[bestIdx];
            double tx = _plotTurnX[bestIdx];
            double ty = _plotTurnY[bestIdx];

            _chartHoverLine.X1 = tx;
            _chartHoverLine.X2 = tx;
            _chartHoverLine.Y1 = _plotTop;
            _chartHoverLine.Y2 = _plotBottom;
            _chartHoverLine.Visibility = Visibility.Visible;

            Canvas.SetLeft(_chartHoverDot, tx - 4.0);
            Canvas.SetTop(_chartHoverDot, ty - 4.0);
            _chartHoverDot.Visibility = Visibility.Visible;

            _chartHoverTitle.Text = string.Format(
                CultureInfo.InvariantCulture,
                "第 {0}/{1} 轮 · {2} · {3}",
                pt.TurnIndex,
                _selectedBurst.Turns,
                string.IsNullOrEmpty(pt.TimeLabel) ? "—" : pt.TimeLabel,
                pt.Model);

            string simLine = "";
            if (pt.CompactedContextTokens < pt.ContextTokens)
            {
                simLine = "\n60K /compact 模拟体积: " + FormatterUtil.FormatTokens(pt.CompactedContextTokens);
            }

            _chartHoverBody.Text = string.Format(
                CultureInfo.InvariantCulture,
                "当轮上下文: {0} (裸读 {1} / 缓存读 {2})\n当轮输出: {3} · 缓存命中 {4:F1}% · 花费 {5}{6}",
                FormatterUtil.FormatTokens(pt.ContextTokens),
                FormatterUtil.FormatTokens(pt.FreshInputTokens),
                FormatterUtil.FormatTokens(pt.CacheReadTokens),
                FormatterUtil.FormatTokens(pt.OutputTokens),
                pt.CacheHitRate * 100.0,
                FormatterUtil.FormatMoney(pt.CostUsd, _isCny, _usdToCny),
                simLine);

            _chartHoverTip.Visibility = Visibility.Visible;
            _chartHoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tipW = _chartHoverTip.DesiredSize.Width;
            double tipH = _chartHoverTip.DesiredSize.Height;

            double tipLeft = tx + 12.0;
            if (tipLeft + tipW > _chartCanvas.ActualWidth - 8.0)
            {
                tipLeft = tx - tipW - 12.0;
            }
            if (tipLeft < _plotLeft) tipLeft = _plotLeft;

            double tipTop = pos.Y - tipH * 0.5;
            if (tipTop < 4.0) tipTop = 4.0;
            if (tipTop + tipH > _plotBottom - 2.0) tipTop = Math.Max(4.0, _plotBottom - tipH - 2.0);

            Canvas.SetLeft(_chartHoverTip, tipLeft);
            Canvas.SetTop(_chartHoverTip, tipTop);
        }

        private void OnChartCanvasMouseLeave(object sender, MouseEventArgs e)
        {
            HideChartHover();
        }

        private void HideChartHover()
        {
            if (_chartHoverLine != null) _chartHoverLine.Visibility = Visibility.Collapsed;
            if (_chartHoverDot != null) _chartHoverDot.Visibility = Visibility.Collapsed;
            if (_chartHoverTip != null) _chartHoverTip.Visibility = Visibility.Collapsed;
        }

        private static double MapValueToY(double val, double maxVal, double topPad, double plotH)
        {
            if (maxVal <= 0.0) return topPad + plotH;
            double ratio = val / maxVal;
            if (ratio < 0.0) ratio = 0.0;
            if (ratio > 1.0) ratio = 1.0;
            return topPad + plotH * (1.0 - ratio);
        }

        private static double ComputeNiceCeiling(double rawMax)
        {
            if (rawMax <= 120000.0) return 120000.0;
            if (rawMax <= 150000.0) return 150000.0;
            if (rawMax <= 200000.0) return 200000.0;
            if (rawMax <= 250000.0) return 250000.0;
            if (rawMax <= 300000.0) return 300000.0;
            if (rawMax <= 400000.0) return 400000.0;
            if (rawMax <= 500000.0) return 500000.0;
            if (rawMax <= 750000.0) return 750000.0;
            if (rawMax <= 1000000.0) return 1000000.0;
            return Math.Ceiling(rawMax / 250000.0) * 250000.0;
        }

        private static LinearGradientBrush CreateAreaGradient(Color c, bool isDark)
        {
            byte topAlpha = isDark ? (byte)68 : (byte)46;
            byte botAlpha = isDark ? (byte)4 : (byte)2;
            LinearGradientBrush lg = new LinearGradientBrush();
            lg.StartPoint = new Point(0, 0);
            lg.EndPoint = new Point(0, 1);
            lg.GradientStops.Add(new GradientStop(Color.FromArgb(topAlpha, c.R, c.G, c.B), 0.0));
            lg.GradientStops.Add(new GradientStop(Color.FromArgb(botAlpha, c.R, c.G, c.B), 1.0));
            lg.Freeze();
            return lg;
        }
    }
}
