using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TokLens
{
    /// <summary>
    /// 报表导出：CSV 对账明细。
    /// </summary>
    public static class ReportExporter
    {
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
            sb.Append("日期,设备节点,AI工具,模型名称,调用次数,输入Token,输出Token,缓存写入,缓存读取,总Token,记录或估值美元(USD),折算人民币(CNY),计价说明,服务商,请求档位,实际档位,档位证据,金额来源,价格版本,同量Standard估值USD,档位额外估值USD\r\n");

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

                string tierDesc = UsagePresentation.TierLabel(r) + " · " + UsagePresentation.CostLabel(r) + " · " + (string.IsNullOrWhiteSpace(r.RateLabel) ? "价格未记录" : r.RateLabel);

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
                  .Append(UsagePresentation.IsUnpriced(r) ? "" : costUsd.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                  .Append(UsagePresentation.IsUnpriced(r) ? "" : costCny.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                  .Append(EscapeCsvField(tierDesc, true))
                  .Append(',').Append(EscapeCsvField(r.Provider, true))
                  .Append(',').Append(EscapeCsvField(r.RequestedServiceTier, true))
                  .Append(',').Append(EscapeCsvField(r.ActualServiceTier, true))
                  .Append(',').Append(EscapeCsvField(r.TierEvidence, true))
                  .Append(',').Append(EscapeCsvField(r.CostBasis, true))
                  .Append(',').Append(EscapeCsvField(r.PricingVersion, true))
                  .Append(',').Append(r.StandardEquivalentUsd.HasValue ? r.StandardEquivalentUsd.Value.ToString("F6", CultureInfo.InvariantCulture) : "")
                  .Append(',').Append(r.ServiceTierPremiumUsd.HasValue ? r.ServiceTierPremiumUsd.Value.ToString("F6", CultureInfo.InvariantCulture) : "")
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
