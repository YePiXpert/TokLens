using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TokLens
{
    /// <summary>
    /// 报表导出：Markdown 报告与 CSV 对账明细。
    /// </summary>
    public static class ReportExporter
    {
        private static readonly char[] SparkBlocks = new char[] { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
        private const int TrendBarSlots = 24;

        private sealed class MachineAggRow
        {
            public string Key;
            public string MachineName;
            public string MachineType;
            public HashSet<string> Tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;
            public double SavedUsd;

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }

            public double CacheHitPercent
            {
                get
                {
                    long prompt = FreshInput + CacheRead + CacheWrite;
                    return prompt > 0L ? (CacheRead * 100.0 / prompt) : 0.0;
                }
            }
        }

        private sealed class ToolAggRow
        {
            public string Tool;
            public HashSet<string> Models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;
            public double SavedUsd;

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }
        }

        private sealed class ModelAggRow
        {
            public string Model;
            public Dictionary<string, double> ToolCosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public int Turns;
            public long FreshInput;
            public long CacheRead;
            public long CacheWrite;
            public long Output;
            public double CostUsd;
            public double SavedUsd;
            public string RateLabel;

            public long TotalTokens
            {
                get { return FreshInput + CacheRead + CacheWrite + Output; }
            }

            public double CacheHitPercent
            {
                get
                {
                    long prompt = FreshInput + CacheRead + CacheWrite;
                    return prompt > 0L ? (CacheRead * 100.0 / prompt) : 0.0;
                }
            }

            public string PrimaryTool
            {
                get
                {
                    if (ToolCosts.Count == 0)
                        return "未知工具";

                    string bestTool = null;
                    double bestCost = -1.0;
                    foreach (KeyValuePair<string, double> kv in ToolCosts)
                    {
                        if (kv.Value > bestCost)
                        {
                            bestCost = kv.Value;
                            bestTool = kv.Key;
                        }
                    }
                    if (ToolCosts.Count > 1)
                        return bestTool + " 等(" + ToolCosts.Count.ToString(CultureInfo.InvariantCulture) + ")";
                    return bestTool;
                }
            }
        }

        private sealed class DayTrendPoint
        {
            public string DateStr;
            public int Turns;
            public long Tokens;
            public double CostUsd;
        }

        // =====================================================================
        // 1. Markdown 报告
        // =====================================================================

        public static string BuildMarkdownReport(ScanResult scanResult, bool isCny, double usdToCny)
        {
            IList<RecordBucket> rows = scanResult != null ? (IList<RecordBucket>)scanResult.Records : new List<RecordBucket>();
            return BuildMarkdownReport(rows, isCny, usdToCny, null, DateTime.Now);
        }

        public static string BuildMarkdownReport(IList<RecordBucket> records, bool isCny, double usdToCny)
        {
            return BuildMarkdownReport(records, isCny, usdToCny, null, DateTime.Now);
        }

        public static string BuildMarkdownReport(IList<RecordBucket> records, bool isCny, double usdToCny, string scopeTitle)
        {
            return BuildMarkdownReport(records, isCny, usdToCny, scopeTitle, DateTime.Now);
        }

        public static string BuildMarkdownReport(
            IList<RecordBucket> records,
            bool isCny,
            double usdToCny,
            string scopeTitle,
            DateTime nowLocal)
        {
            double safeRate = FormatterUtil.NormalizeExchangeRate(usdToCny);
            DateTime localNow;
            long dummyEpoch;
            FormatterUtil.NormalizeNowTime(nowLocal, out localNow, out dummyEpoch);

            long totFi = 0L;
            long totCr = 0L;
            long totCw = 0L;
            long totOut = 0L;
            int totTurns = 0;
            double totCostUsd = 0.0;
            double totBaselineUsd = 0.0;
            double totSavedUsd = 0.0;

            string minDate = null;
            string maxDate = null;
            HashSet<string> activeDates = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, DayTrendPoint> dayMap = new Dictionary<string, DayTrendPoint>(StringComparer.Ordinal);
            Dictionary<string, MachineAggRow> machineMap = new Dictionary<string, MachineAggRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ToolAggRow> toolMap = new Dictionary<string, ToolAggRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ModelAggRow> modelMap = new Dictionary<string, ModelAggRow>(StringComparer.OrdinalIgnoreCase);

            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    RecordBucket r = records[i];
                    if (r == null)
                        continue;

                    long fi = Math.Max(0L, r.FreshInput);
                    long cr = Math.Max(0L, r.CacheRead);
                    long cw = Math.Max(0L, r.CacheWrite);
                    long outTok = Math.Max(0L, r.Output);
                    int turns = Math.Max(0, r.Turns);
                    double costUsd = FormatterUtil.SanitizeMoney(r.CostUsd);
                    double baseUsd = Math.Max(costUsd, FormatterUtil.SanitizeMoney(r.BaselineUsd));
                    double savedUsd = Math.Max(0.0, baseUsd - costUsd);

                    if ((fi + cr + cw + outTok) <= 0L && costUsd <= 0.0 && turns <= 0)
                        continue;

                    totFi += fi;
                    totCr += cr;
                    totCw += cw;
                    totOut += outTok;
                    totTurns += turns;
                    totCostUsd += costUsd;
                    totBaselineUsd += baseUsd;
                    totSavedUsd += savedUsd;

                    string dStr = (r.Date ?? string.Empty).Trim();
                    DateTime parsedDt;
                    if (TryParseIsoDate(dStr, out parsedDt))
                    {
                        activeDates.Add(dStr);
                        if (minDate == null || string.CompareOrdinal(dStr, minDate) < 0)
                            minDate = dStr;
                        if (maxDate == null || string.CompareOrdinal(dStr, maxDate) > 0)
                            maxDate = dStr;

                        DayTrendPoint dp;
                        if (!dayMap.TryGetValue(dStr, out dp))
                        {
                            dp = new DayTrendPoint();
                            dp.DateStr = dStr;
                            dayMap[dStr] = dp;
                        }
                        dp.Turns += turns;
                        dp.Tokens += (fi + cr + cw + outTok);
                        dp.CostUsd += costUsd;
                    }

                    // 设备聚合
                    string mName = string.IsNullOrWhiteSpace(r.MachineName) ? "本机节点" : r.MachineName.Trim();
                    string mId = string.IsNullOrWhiteSpace(r.MachineId) ? ("name:" + mName) : r.MachineId.Trim();
                    string mType = string.IsNullOrWhiteSpace(r.MachineType) ? "Windows" : r.MachineType.Trim();
                    MachineAggRow mRow;
                    if (!machineMap.TryGetValue(mId, out mRow))
                    {
                        mRow = new MachineAggRow();
                        mRow.Key = mId;
                        mRow.MachineName = mName;
                        mRow.MachineType = mType;
                        machineMap[mId] = mRow;
                    }
                    string toolName = string.IsNullOrWhiteSpace(r.Tool) ? "未知工具" : r.Tool.Trim();
                    string modelName = string.IsNullOrWhiteSpace(r.Model) ? "unknown" : r.Model.Trim();
                    mRow.Tools.Add(toolName);
                    mRow.Turns += turns;
                    mRow.FreshInput += fi;
                    mRow.CacheRead += cr;
                    mRow.CacheWrite += cw;
                    mRow.Output += outTok;
                    mRow.CostUsd += costUsd;
                    mRow.SavedUsd += savedUsd;

                    // 工具聚合
                    ToolAggRow tRow;
                    if (!toolMap.TryGetValue(toolName, out tRow))
                    {
                        tRow = new ToolAggRow();
                        tRow.Tool = toolName;
                        toolMap[toolName] = tRow;
                    }
                    tRow.Models.Add(modelName);
                    tRow.Turns += turns;
                    tRow.FreshInput += fi;
                    tRow.CacheRead += cr;
                    tRow.CacheWrite += cw;
                    tRow.Output += outTok;
                    tRow.CostUsd += costUsd;
                    tRow.SavedUsd += savedUsd;

                    // 模型聚合
                    ModelAggRow mdRow;
                    if (!modelMap.TryGetValue(modelName, out mdRow))
                    {
                        mdRow = new ModelAggRow();
                        mdRow.Model = modelName;
                        mdRow.RateLabel = string.IsNullOrWhiteSpace(r.RateLabel) ? "标准档位" : r.RateLabel.Trim();
                        modelMap[modelName] = mdRow;
                    }
                    else if (string.Equals(mdRow.RateLabel, "标准档位", StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(r.RateLabel))
                    {
                        mdRow.RateLabel = r.RateLabel.Trim();
                    }
                    double prevToolCost;
                    if (!mdRow.ToolCosts.TryGetValue(toolName, out prevToolCost))
                        prevToolCost = 0.0;
                    mdRow.ToolCosts[toolName] = prevToolCost + costUsd;
                    mdRow.Turns += turns;
                    mdRow.FreshInput += fi;
                    mdRow.CacheRead += cr;
                    mdRow.CacheWrite += cw;
                    mdRow.Output += outTok;
                    mdRow.CostUsd += costUsd;
                    mdRow.SavedUsd += savedUsd;
                }
            }

            long totTokens = totFi + totCr + totCw + totOut;
            long totPrompt = totFi + totCr + totCw;
            double cacheHitPct = totPrompt > 0L ? (totCr * 100.0 / totPrompt) : 0.0;
            double savedPct = totBaselineUsd > 0.0 ? (totSavedUsd * 100.0 / totBaselineUsd) : 0.0;

            int activeDayCount = Math.Max(1, activeDates.Count);
            double avgActiveDayUsd = totCostUsd / activeDayCount;

            string periodDisplay;
            if (!string.IsNullOrWhiteSpace(scopeTitle))
            {
                if (minDate != null && maxDate != null)
                    periodDisplay = scopeTitle.Trim() + " (" + minDate + " ~ " + maxDate + "，活跃 " + activeDates.Count.ToString(CultureInfo.InvariantCulture) + " 天)";
                else
                    periodDisplay = scopeTitle.Trim();
            }
            else if (minDate != null && maxDate != null)
            {
                periodDisplay = minDate + " ~ " + maxDate + "（活跃 " + activeDates.Count.ToString(CultureInfo.InvariantCulture) + " 天）";
            }
            else
            {
                periodDisplay = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "（暂无消耗记录）";
            }

            StringBuilder sb = new StringBuilder(8192);

            // 标题与顶部徽章式摘要
            sb.AppendLine("# " + AppConstants.ProductTitle + " · AI 用量报告");
            sb.AppendLine();
            sb.Append("`📅 统计周期: ").Append(EscapeInlineCode(periodDisplay)).Append("` ");
            sb.Append("`⚡ 总 Token: ").Append(FormatterUtil.FormatTokens(totTokens)).Append("` ");
            sb.Append("`🎯 缓存命中率: ").Append(cacheHitPct.ToString("F1", CultureInfo.InvariantCulture)).Append("%` ");
            sb.Append("`💰 API 等价总成本: ").Append(FormatDualMoney(totCostUsd, isCny, safeRate)).Append("` ");
            sb.Append("`🛡️ 缓存节省: ").Append(FormatterUtil.FormatMoney(totSavedUsd, isCny, safeRate)).Append("` ");
            sb.AppendLine("`📈 日均消耗: " + FormatterUtil.FormatMoney(avgActiveDayUsd, isCny, safeRate) + "/活跃日`");
            sb.AppendLine();

            sb.AppendLine("> **生成时间**：`" + localNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "` ｜ " +
                          "**参考汇率**：`1 USD = " + safeRate.ToString("F4", CultureInfo.InvariantCulture) + " CNY` ｜ " +
                          "**总调用轮次**：`" + totTurns.ToString("N0", CultureInfo.InvariantCulture) + " 次`");
            sb.AppendLine();

            // 核心指标矩阵卡片表
            sb.AppendLine("## 📌 核心算力与成本摘要");
            sb.AppendLine();
            sb.AppendLine("| 指标维度 | 精算数值 | 结构明细与说明 |");
            sb.AppendLine("| :--- | ---: | :--- |");
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| **总 Token 吞吐量** | **{0}** (`{1:N0}`) | 活跃设备 `{2}` 台 · 覆盖 AI 工具 `{3}` 款 · 模型 `{4}` 个 |",
                FormatterUtil.FormatTokens(totTokens),
                totTokens,
                machineMap.Count,
                toolMap.Count,
                modelMap.Count));
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| **输入 / 输出 / 缓存明细** | **命中率 {0:F1}%** | 新增输入 `{1}` · 缓存读取 `{2}` · 缓存写入 `{3}` · 输出 `{4}` |",
                cacheHitPct,
                FormatterUtil.FormatTokens(totFi),
                FormatterUtil.FormatTokens(totCr),
                FormatterUtil.FormatTokens(totCw),
                FormatterUtil.FormatTokens(totOut)));
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| **API 等价总成本** | **{0}** | 折合副币种 `{1}`（无缓存基准价 `{2}`） |",
                FormatterUtil.FormatMoney(totCostUsd, isCny, safeRate),
                FormatterUtil.FormatMoney(totCostUsd, !isCny, safeRate),
                FormatterUtil.FormatMoney(totBaselineUsd, isCny, safeRate)));
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| **Prompt 缓存节省金额** | **{0}** | 相比全量输入单价立省 **{1:F1}%**（折合 `{2}`） |",
                FormatterUtil.FormatMoney(totSavedUsd, isCny, safeRate),
                savedPct,
                FormatterUtil.FormatMoney(totSavedUsd, !isCny, safeRate)));
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| **日均消耗水平** | **{0} / 天** | 按 `{1}` 个活跃日折算（单次调用均价 `{2}`） |",
                FormatterUtil.FormatMoney(avgActiveDayUsd, isCny, safeRate),
                activeDates.Count > 0 ? activeDates.Count : 1,
                FormatterUtil.FormatMoney(totTurns > 0 ? (totCostUsd / totTurns) : 0.0, isCny, safeRate)));
            sb.AppendLine();

            // 近 14 天每日成本 ASCII / Unicode 迷你柱状趋势图
            DateTime anchorDate = ResolveTrendAnchorDate(localNow.Date, maxDate);
            Append14DayTrendSection(sb, dayMap, anchorDate, isCny, safeRate);

            // 设备矩阵表
            AppendMachineMatrixSection(sb, machineMap, totCostUsd, isCny, safeRate);

            // AI 工具排行表
            AppendToolRankingSection(sb, toolMap, totCostUsd, isCny, safeRate);

            // Top 10 模型精算表
            AppendTopModelsSection(sb, modelMap, totCostUsd, isCny, safeRate);

            sb.AppendLine("---");
            sb.AppendLine("*本报告由 **" + AppConstants.ProductTitle + "** 本地离线精算引擎自动生成，原始对话内容零上传。*");

            return sb.ToString();
        }

        public static void ExportMarkdownFile(IList<RecordBucket> records, bool isCny, double usdToCny, string outPath)
        {
            ExportMarkdownFile(records, isCny, usdToCny, null, outPath);
        }

        public static void ExportMarkdownFile(string outPath, IList<RecordBucket> records, bool isCny, double usdToCny)
        {
            ExportMarkdownFile(records, isCny, usdToCny, null, outPath);
        }

        public static void ExportMarkdownFile(
            IList<RecordBucket> records,
            bool isCny,
            double usdToCny,
            string scopeTitle,
            string outPath)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                throw new ArgumentException("导出文件路径不能为空。", "outPath");

            EnsureParentDirectory(outPath);
            string md = BuildMarkdownReport(records, isCny, usdToCny, scopeTitle, DateTime.Now);
            File.WriteAllText(outPath, md, new UTF8Encoding(false));
        }

        // =====================================================================
        // 2. 财务报销与数据分析级 UTF-8 BOM CSV 导出（RFC 4180 + 公式注入防护）
        // =====================================================================

        public static string BuildCsvReport(ScanResult scanResult, double usdToCny)
        {
            IList<RecordBucket> rows = scanResult != null ? (IList<RecordBucket>)scanResult.Records : new List<RecordBucket>();
            return BuildCsvReport(rows, usdToCny);
        }

        public static string BuildCsvReport(IList<RecordBucket> records, double usdToCny)
        {
            double safeRate = FormatterUtil.NormalizeExchangeRate(usdToCny);
            List<RecordBucket> sorted = new List<RecordBucket>();
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i] != null)
                        sorted.Add(records[i]);
                }
            }

            sorted.Sort(delegate(RecordBucket a, RecordBucket b)
            {
                int c = string.CompareOrdinal(b.Date ?? string.Empty, a.Date ?? string.Empty);
                if (c != 0) return c;
                c = FormatterUtil.SanitizeMoney(b.CostUsd).CompareTo(FormatterUtil.SanitizeMoney(a.CostUsd));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Tool ?? string.Empty, b.Tool ?? string.Empty);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Model ?? string.Empty, b.Model ?? string.Empty);
            });

            StringBuilder sb = new StringBuilder(sorted.Count * 128 + 256);
            sb.Append("日期,设备节点,AI工具,模型名称,调用次数,输入Token,输出Token,缓存写入,缓存读取,总Token,API折算美元(USD),折算人民币(CNY),计价档位说明\r\n");

            for (int i = 0; i < sorted.Count; i++)
            {
                RecordBucket r = sorted[i];
                string dateText = string.IsNullOrWhiteSpace(r.Date) ? "未知日期" : r.Date.Trim();
                string nodeName = string.IsNullOrWhiteSpace(r.MachineName) ? "本机节点" : r.MachineName.Trim();
                string toolName = string.IsNullOrWhiteSpace(r.Tool) ? "未知工具" : r.Tool.Trim();
                string modelName = string.IsNullOrWhiteSpace(r.Model) ? "unknown" : r.Model.Trim();

                int turns = Math.Max(0, r.Turns);
                long fi = Math.Max(0L, r.FreshInput);
                long outTok = Math.Max(0L, r.Output);
                long cw = Math.Max(0L, r.CacheWrite);
                long cr = Math.Max(0L, r.CacheRead);
                long totalTok = fi + outTok + cw + cr;

                double costUsd = FormatterUtil.SanitizeMoney(r.CostUsd);
                double costCny = costUsd * safeRate;
                double baseUsd = Math.Max(costUsd, FormatterUtil.SanitizeMoney(r.BaselineUsd));

                string tierDesc = BuildPricingTierNote(r.RateLabel, turns, fi + cr + cw, costUsd, baseUsd);

                sb.Append(EscapeCsvField(dateText, true)).Append(',')
                  .Append(EscapeCsvField(nodeName, true)).Append(',')
                  .Append(EscapeCsvField(toolName, true)).Append(',')
                  .Append(EscapeCsvField(modelName, true)).Append(',')
                  .Append(turns.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(fi.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(outTok.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(cw.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(cr.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(totalTok.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(costUsd.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                  .Append(costCny.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                  .Append(EscapeCsvField(tierDesc, true))
                  .Append("\r\n");
            }

            return sb.ToString();
        }

        public static void ExportCsvFile(ScanResult scanResult, double usdToCny, string outPath)
        {
            IList<RecordBucket> rows = scanResult != null ? (IList<RecordBucket>)scanResult.Records : new List<RecordBucket>();
            ExportCsvFile(rows, usdToCny, outPath);
        }

        public static void ExportCsvFile(string outPath, IList<RecordBucket> records, double usdToCny)
        {
            ExportCsvFile(records, usdToCny, outPath);
        }

        public static void ExportCsvFile(IList<RecordBucket> records, double usdToCny, string outPath)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                throw new ArgumentException("导出文件路径不能为空。", "outPath");

            EnsureParentDirectory(outPath);
            string csv = BuildCsvReport(records, usdToCny);
            // 严格写入 UTF-8 BOM，确保中文 Windows Excel 双击直接打开无乱码
            File.WriteAllText(outPath, csv, new UTF8Encoding(true));
        }

        public static string EscapeCsvField(string raw, bool alwaysQuote)
        {
            if (string.IsNullOrEmpty(raw))
                return alwaysQuote ? "\"\"" : string.Empty;

            string cleaned = raw.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            bool formulaDanger = StartsWithFormulaTrigger(cleaned);
            if (formulaDanger)
            {
                cleaned = "'" + cleaned;
            }

            bool mustQuote = alwaysQuote ||
                             formulaDanger ||
                             cleaned.IndexOf(',') >= 0 ||
                             cleaned.IndexOf('"') >= 0;

            if (!mustQuote)
                return cleaned;

            return "\"" + cleaned.Replace("\"", "\"\"") + "\"";
        }

        private static bool StartsWithFormulaTrigger(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;

            char c0 = s[0];
            if (c0 == '=' || c0 == '+' || c0 == '-' || c0 == '@' || c0 == '\t')
                return true;

            int idx = 0;
            while (idx < s.Length && (s[idx] == ' ' || s[idx] == '\t'))
                idx++;

            if (idx < s.Length)
            {
                char ct = s[idx];
                if (ct == '=' || ct == '+' || ct == '-' || ct == '@')
                    return true;
            }
            return false;
        }

        // =====================================================================
        // 内部排版与计算辅助方法
        // =====================================================================

        private static void Append14DayTrendSection(
            StringBuilder sb,
            Dictionary<string, DayTrendPoint> dayMap,
            DateTime anchorDate,
            bool isCny,
            double safeRate)
        {
            sb.AppendLine("## 📈 近 14 天每日成本趋势图");
            sb.AppendLine();

            List<DayTrendPoint> series = new List<DayTrendPoint>(14);
            double peakUsd = 0.0;
            double sum14Usd = 0.0;
            long sum14Tokens = 0L;
            string peakDate = string.Empty;

            for (int offset = 13; offset >= 0; offset--)
            {
                string dKey = anchorDate.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DayTrendPoint pt;
                if (!dayMap.TryGetValue(dKey, out pt))
                {
                    pt = new DayTrendPoint();
                    pt.DateStr = dKey;
                }
                series.Add(pt);
                sum14Usd += pt.CostUsd;
                sum14Tokens += pt.Tokens;
                if (pt.CostUsd > peakUsd)
                {
                    peakUsd = pt.CostUsd;
                    peakDate = pt.DateStr;
                }
            }

            StringBuilder sparkSb = new StringBuilder(14);
            for (int i = 0; i < series.Count; i++)
            {
                double c = series[i].CostUsd;
                if (c <= 0.0 || peakUsd <= 0.0)
                {
                    sparkSb.Append('·');
                }
                else
                {
                    int idx = (int)Math.Round((c / peakUsd) * (SparkBlocks.Length - 1));
                    if (idx < 0) idx = 0;
                    if (idx >= SparkBlocks.Length) idx = SparkBlocks.Length - 1;
                    sparkSb.Append(SparkBlocks[idx]);
                }
            }

            sb.AppendLine("```text");
            sb.AppendLine("日期         │    Token │   当日成本 │ 成本分布条形图 (█▓▒░)");
            sb.AppendLine("─────────────┼──────────┼────────────┼────────────────────────────────────");

            for (int i = 0; i < series.Count; i++)
            {
                DayTrendPoint pt = series[i];
                string tokCell = FormatterUtil.FormatTokens(pt.Tokens).PadLeft(8, ' ');
                string costCell = FormatFixedWidthMoney(pt.CostUsd, isCny, safeRate, 10);
                string bar = RenderUnicodeGradientBar(pt.CostUsd, peakUsd, TrendBarSlots);
                double ratioPct = peakUsd > 0.0 ? (pt.CostUsd * 100.0 / peakUsd) : 0.0;
                string pctCell = ratioPct.ToString("F0", CultureInfo.InvariantCulture).PadLeft(3, ' ') + "%";
                string peakMark = (peakUsd > 0.0 && string.Equals(pt.DateStr, peakDate, StringComparison.Ordinal)) ? " ★ 峰值" : string.Empty;

                sb.Append(pt.DateStr)
                  .Append("   │ ")
                  .Append(tokCell)
                  .Append(" │ ")
                  .Append(costCell)
                  .Append(" │ ")
                  .Append(bar)
                  .Append(' ')
                  .Append(pctCell)
                  .AppendLine(peakMark);
            }

            sb.AppendLine("─────────────┴──────────┴────────────┴────────────────────────────────────");
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "14日迷你脉搏: {0}  ｜  14日合计: {1} ({2} Tokens)",
                sparkSb.ToString(),
                FormatterUtil.FormatMoney(sum14Usd, isCny, safeRate),
                FormatterUtil.FormatTokens(sum14Tokens)));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        private static string RenderUnicodeGradientBar(double value, double maxVal, int width)
        {
            if (value <= 0.0 || maxVal <= 0.0)
                return new string('░', width);

            double ratio = value / maxVal;
            if (ratio >= 1.0)
                return new string('█', width);
            if (ratio < 0.0)
                return new string('░', width);

            double exact = ratio * width;
            int full = (int)Math.Floor(exact);
            double frac = exact - full;

            StringBuilder sb = new StringBuilder(width);
            if (full == 0)
            {
                sb.Append(frac >= 0.5 ? '▓' : '▒');
                sb.Append('░', width - 1);
                return sb.ToString();
            }

            sb.Append('█', full);
            int used = full;
            if (used < width)
            {
                if (frac >= 0.66)
                {
                    sb.Append('▓');
                    used++;
                }
                else if (frac >= 0.33)
                {
                    sb.Append('▒');
                    used++;
                }
            }
            if (used < width)
            {
                sb.Append('░', width - used);
            }
            return sb.ToString();
        }

        private static void AppendMachineMatrixSection(
            StringBuilder sb,
            Dictionary<string, MachineAggRow> machineMap,
            double totalCostUsd,
            bool isCny,
            double safeRate)
        {
            sb.AppendLine("## 🖥️ 设备节点算力矩阵");
            sb.AppendLine();
            sb.AppendLine("| 排名 | 设备节点 | 节点类型 | 活跃工具数 | 调用次数 | 总 Token | 缓存命中率 | API 等价成本 | 成本占比 |");
            sb.AppendLine("| :---: | :--- | :---: | ---: | ---: | ---: | ---: | ---: | ---: |");

            if (machineMap.Count == 0)
            {
                sb.AppendLine("| - | 暂无设备记录 | - | 0 | 0 | 0 | 0.0% | " + FormatterUtil.FormatMoney(0.0, isCny, safeRate) + " | 0.0% |");
                sb.AppendLine();
                return;
            }

            List<MachineAggRow> list = new List<MachineAggRow>(machineMap.Values);
            list.Sort(delegate(MachineAggRow a, MachineAggRow b)
            {
                int c = b.CostUsd.CompareTo(a.CostUsd);
                if (c != 0) return c;
                return b.TotalTokens.CompareTo(a.TotalTokens);
            });

            for (int i = 0; i < list.Count; i++)
            {
                MachineAggRow m = list[i];
                double share = totalCostUsd > 0.0 ? (m.CostUsd * 100.0 / totalCostUsd) : 0.0;
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | **{1}** | `{2}` | {3} | {4:N0} | {5} | {6:F1}% | **{7}** | {8:F1}% |",
                    i + 1,
                    EscapeMdCell(m.MachineName),
                    EscapeMdCell(m.MachineType),
                    m.Tools.Count,
                    m.Turns,
                    FormatterUtil.FormatTokens(m.TotalTokens),
                    m.CacheHitPercent,
                    FormatterUtil.FormatMoney(m.CostUsd, isCny, safeRate),
                    share));
            }
            sb.AppendLine();
        }

        private static void AppendToolRankingSection(
            StringBuilder sb,
            Dictionary<string, ToolAggRow> toolMap,
            double totalCostUsd,
            bool isCny,
            double safeRate)
        {
            sb.AppendLine("## 🛠️ AI 工具算力排行");
            sb.AppendLine();
            sb.AppendLine("| 排名 | AI 工具 | 模型数 | 调用次数 | 新增输入 | 缓存读取 | 输出 Token | 总 Token | API 等价成本 | 成本占比 |");
            sb.AppendLine("| :---: | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

            if (toolMap.Count == 0)
            {
                sb.AppendLine("| - | 暂无工具记录 | 0 | 0 | 0 | 0 | 0 | 0 | " + FormatterUtil.FormatMoney(0.0, isCny, safeRate) + " | 0.0% |");
                sb.AppendLine();
                return;
            }

            List<ToolAggRow> list = new List<ToolAggRow>(toolMap.Values);
            list.Sort(delegate(ToolAggRow a, ToolAggRow b)
            {
                int c = b.CostUsd.CompareTo(a.CostUsd);
                if (c != 0) return c;
                return b.TotalTokens.CompareTo(a.TotalTokens);
            });

            for (int i = 0; i < list.Count; i++)
            {
                ToolAggRow t = list[i];
                double share = totalCostUsd > 0.0 ? (t.CostUsd * 100.0 / totalCostUsd) : 0.0;
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | **{1}** | {2} | {3:N0} | {4} | {5} | {6} | {7} | **{8}** | {9:F1}% |",
                    i + 1,
                    EscapeMdCell(t.Tool),
                    t.Models.Count,
                    t.Turns,
                    FormatterUtil.FormatTokens(t.FreshInput),
                    FormatterUtil.FormatTokens(t.CacheRead),
                    FormatterUtil.FormatTokens(t.Output),
                    FormatterUtil.FormatTokens(t.TotalTokens),
                    FormatterUtil.FormatMoney(t.CostUsd, isCny, safeRate),
                    share));
            }
            sb.AppendLine();
        }

        private static void AppendTopModelsSection(
            StringBuilder sb,
            Dictionary<string, ModelAggRow> modelMap,
            double totalCostUsd,
            bool isCny,
            double safeRate)
        {
            sb.AppendLine("## 🧠 Top 10 模型精算表");
            sb.AppendLine();
            sb.AppendLine("| 排名 | 模型名称 | 主力工具 | 调用次数 | 总 Token | 缓存命中率 | 计价档位 ($/M) | API 等价成本 | 缓存节省 | 成本占比 |");
            sb.AppendLine("| :---: | :--- | :--- | ---: | ---: | ---: | :--- | ---: | ---: | ---: |");

            if (modelMap.Count == 0)
            {
                sb.AppendLine("| - | 暂无模型记录 | - | 0 | 0 | 0.0% | - | " + FormatterUtil.FormatMoney(0.0, isCny, safeRate) + " | " + FormatterUtil.FormatMoney(0.0, isCny, safeRate) + " | 0.0% |");
                sb.AppendLine();
                return;
            }

            List<ModelAggRow> list = new List<ModelAggRow>(modelMap.Values);
            list.Sort(delegate(ModelAggRow a, ModelAggRow b)
            {
                int c = b.CostUsd.CompareTo(a.CostUsd);
                if (c != 0) return c;
                return b.TotalTokens.CompareTo(a.TotalTokens);
            });

            int topN = Math.Min(10, list.Count);
            for (int i = 0; i < topN; i++)
            {
                ModelAggRow m = list[i];
                double share = totalCostUsd > 0.0 ? (m.CostUsd * 100.0 / totalCostUsd) : 0.0;
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | `{1}` | {2} | {3:N0} | {4} | {5:F1}% | `{6}` | **{7}** | {8} | {9:F1}% |",
                    i + 1,
                    EscapeInlineCode(m.Model),
                    EscapeMdCell(m.PrimaryTool),
                    m.Turns,
                    FormatterUtil.FormatTokens(m.TotalTokens),
                    m.CacheHitPercent,
                    EscapeInlineCode(m.RateLabel),
                    FormatterUtil.FormatMoney(m.CostUsd, isCny, safeRate),
                    FormatterUtil.FormatMoney(m.SavedUsd, isCny, safeRate),
                    share));
            }

            if (list.Count > 10)
            {
                int tailCount = list.Count - 10;
                int tailTurns = 0;
                long tailTokens = 0L;
                long tailPrompt = 0L;
                long tailCr = 0L;
                double tailCost = 0.0;
                double tailSaved = 0.0;

                for (int i = 10; i < list.Count; i++)
                {
                    ModelAggRow tail = list[i];
                    tailTurns += tail.Turns;
                    tailTokens += tail.TotalTokens;
                    tailPrompt += (tail.FreshInput + tail.CacheRead + tail.CacheWrite);
                    tailCr += tail.CacheRead;
                    tailCost += tail.CostUsd;
                    tailSaved += tail.SavedUsd;
                }

                double tailHit = tailPrompt > 0L ? (tailCr * 100.0 / tailPrompt) : 0.0;
                double tailShare = totalCostUsd > 0.0 ? (tailCost * 100.0 / totalCostUsd) : 0.0;
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| … | *其余 {0} 个长尾模型合计* | 多工具 | {1:N0} | {2} | {3:F1}% | 混合档位 | {4} | {5} | {6:F1}% |",
                    tailCount,
                    tailTurns,
                    FormatterUtil.FormatTokens(tailTokens),
                    tailHit,
                    FormatterUtil.FormatMoney(tailCost, isCny, safeRate),
                    FormatterUtil.FormatMoney(tailSaved, isCny, safeRate),
                    tailShare));
            }

            sb.AppendLine();
        }

        private static string BuildPricingTierNote(string rawRateLabel, int turns, long promptTokens, double costUsd, double baseUsd)
        {
            string baseLabel = string.IsNullOrWhiteSpace(rawRateLabel) ? "内置默认档位" : rawRateLabel.Trim();
            int safeTurns = turns > 0 ? turns : 1;
            long avgPrompt = promptTokens / safeTurns;

            List<string> tags = new List<string>(2);
            if (avgPrompt > 200000L)
                tags.Add(">200K长上下文档");
            if (baseUsd > costUsd + 1e-6)
                tags.Add("含缓存折扣");

            if (tags.Count == 0)
                return baseLabel + " (标准API等价)";

            return baseLabel + " (" + string.Join("/", tags.ToArray()) + ")";
        }

        private static DateTime ResolveTrendAnchorDate(DateTime localToday, string maxDateStr)
        {
            DateTime maxDt;
            if (!TryParseIsoDate(maxDateStr, out maxDt))
                return localToday;

            if (maxDt > localToday)
                return maxDt;

            if ((localToday - maxDt).TotalDays <= 13.0)
                return localToday;

            return maxDt;
        }

        private static bool TryParseIsoDate(string dateStr, out DateTime parsed)
        {
            parsed = DateTime.MinValue;
            if (string.IsNullOrEmpty(dateStr) || dateStr.Length != 10)
                return false;
            return DateTime.TryParseExact(
                dateStr,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed);
        }

        private static string FormatDualMoney(double usd, bool primaryIsCny, double safeRate)
        {
            string pri = FormatterUtil.FormatMoney(usd, primaryIsCny, safeRate);
            string sec = FormatterUtil.FormatMoney(usd, !primaryIsCny, safeRate);
            return pri + " (" + sec + ")";
        }

        private static string FormatFixedWidthMoney(double usd, bool isCny, double safeRate, int totalWidth)
        {
            string s = FormatterUtil.FormatMoney(usd, isCny, safeRate);
            if (s.Length >= totalWidth)
                return s;
            return s.PadLeft(totalWidth, ' ');
        }

        private static string EscapeMdCell(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "-";
            return s.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|").Trim();
        }

        private static string EscapeInlineCode(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "-";
            return s.Replace("`", "'").Replace("\r", "").Replace("\n", " ").Replace("|", "/").Trim();
        }

        private static void EnsureParentDirectory(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
