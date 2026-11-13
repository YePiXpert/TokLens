using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TokLens
{
    /// <summary>
    /// 纯矢量算力战报分享海报与 GitHub Profile README 徽章导出器。
    /// 零外部渲染依赖，严格输出符合 XML 1.0 / SVG 1.1 标准的无损矢量图形。
    /// </summary>
    public static class SvgBadgeAndCardExporter
    {
        private const double DefaultFxRate = 7.25;

        private const string FontStackUi = "-apple-system, BlinkMacSystemFont, 'Segoe UI Variable Display', 'Segoe UI', 'PingFang SC', 'Microsoft YaHei UI', 'Microsoft YaHei', sans-serif";
        private const string FontStackMono = "'JetBrains Mono', 'Cascadia Mono', 'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace";

        private static readonly string[] PaletteDark = new string[]
        {
            "#818CF8", "#38BDF8", "#34D399", "#A78BFA",
            "#F59E0B", "#FB7185", "#2DD4BF", "#FB923C"
        };

        private static readonly string[] PaletteLight = new string[]
        {
            "#4F46E5", "#0284C7", "#059669", "#7C3AED",
            "#D97706", "#E11D48", "#0D9488", "#EA580C"
        };

        private static readonly Regex ModelDateTailRegex = new Regex(@"-(?:20\d{6}|20\d{2}-\d{2}-\d{2})$", RegexOptions.Compiled);

        private sealed class AggregatedStats
        {
            public int Days;
            public string PeriodBadgeText;
            public string GeneratedDateText;
            public int TotalTurns;
            public long FreshInput;
            public long Output;
            public long CacheRead;
            public long CacheWrite;
            public long TotalTokens;
            public double TotalCostUsd;
            public double TotalBaselineUsd;
            public double SavedUsd;
            public double CacheHitPercent;
            public double SavedPercent;
            public int TotalToolCount;
            public int TotalModelCount;
            public List<RankedItem> TopTools = new List<RankedItem>();
            public List<RankedItem> TopModels = new List<RankedItem>();
            public List<DailyPulsePoint> PulseDays = new List<DailyPulsePoint>();
            public string PulseRangeLabel = "";
            public string PulsePeakLabel = "";
        }

        private sealed class RankedItem
        {
            public string Name;
            public long Tokens;
            public double CostUsd;
            public double SharePercent;
            public string ColorHex;
        }

        private sealed class DailyPulsePoint
        {
            public string DateKey;
            public string ShortLabel;
            public long Tokens;
            public double CostUsd;
        }

        private sealed class PosterPalette
        {
            public bool IsDark;
            public string BgStart;
            public string BgEnd;
            public string OuterBorder;
            public string DotPatternFill;
            public string DotPatternOpacity;
            public string GlowIndigoOpacity;
            public string GlowEmeraldOpacity;
            public string CardBgStart;
            public string CardBgEnd;
            public string CardBorderStart;
            public string CardBorderEnd;
            public string CardShadowOpacity;
            public string DividerStroke;
            public string GridLineStroke;
            public string TrackBg;
            public string FgPrimary;
            public string FgSecondary;
            public string FgMuted;
            public string AccentPrimary;
            public string AccentText;
            public string AccentPillBg;
            public string AccentPillBorder;
            public string SavingsBg;
            public string SavingsBorder;
            public string SavingsFg;
            public string ColorInput;
            public string ColorOutput;
            public string ColorCache;
            public string ChipBg;
            public string ChipBorder;
            public string PulseBarTop;
            public string PulseBarBottom;
            public string PulsePeakTop;
            public string PulsePeakBottom;
            public string PulseLine;

            public static PosterPalette Create(bool isDark)
            {
                PosterPalette p = new PosterPalette();
                p.IsDark = isDark;
                if (isDark)
                {
                    p.BgStart = "#09090B";
                    p.BgEnd = "#0D0D12";
                    p.OuterBorder = "#272730";
                    p.DotPatternFill = "#FFFFFF";
                    p.DotPatternOpacity = "0.035";
                    p.GlowIndigoOpacity = "0.13";
                    p.GlowEmeraldOpacity = "0.08";
                    p.CardBgStart = "#16161B";
                    p.CardBgEnd = "#111115";
                    p.CardBorderStart = "#343442";
                    p.CardBorderEnd = "#1F1F27";
                    p.CardShadowOpacity = "0.32";
                    p.DividerStroke = "#23232B";
                    p.GridLineStroke = "#22222A";
                    p.TrackBg = "#22222B";
                    p.FgPrimary = "#F4F4F6";
                    p.FgSecondary = "#A1A1AA";
                    p.FgMuted = "#71717A";
                    p.AccentPrimary = "#6366F1";
                    p.AccentText = "#818CF8";
                    p.AccentPillBg = "#191B30";
                    p.AccentPillBorder = "#313663";
                    p.SavingsBg = "#0E261D";
                    p.SavingsBorder = "#1B4B38";
                    p.SavingsFg = "#34D399";
                    p.ColorInput = "#818CF8";
                    p.ColorOutput = "#F59E0B";
                    p.ColorCache = "#34D399";
                    p.ChipBg = "#1B1B22";
                    p.ChipBorder = "#2B2B36";
                    p.PulseBarTop = "#6366F1";
                    p.PulseBarBottom = "#312E81";
                    p.PulsePeakTop = "#34D399";
                    p.PulsePeakBottom = "#059669";
                    p.PulseLine = "#818CF8";
                }
                else
                {
                    p.BgStart = "#F8FAFC";
                    p.BgEnd = "#F1F5F9";
                    p.OuterBorder = "#CBD5E1";
                    p.DotPatternFill = "#0F172A";
                    p.DotPatternOpacity = "0.04";
                    p.GlowIndigoOpacity = "0.07";
                    p.GlowEmeraldOpacity = "0.06";
                    p.CardBgStart = "#FFFFFF";
                    p.CardBgEnd = "#FCFDFF";
                    p.CardBorderStart = "#E2E8F0";
                    p.CardBorderEnd = "#CBD5E1";
                    p.CardShadowOpacity = "0.05";
                    p.DividerStroke = "#E2E8F0";
                    p.GridLineStroke = "#E2E8F0";
                    p.TrackBg = "#E2E8F0";
                    p.FgPrimary = "#09090B";
                    p.FgSecondary = "#475569";
                    p.FgMuted = "#64748B";
                    p.AccentPrimary = "#4F46E5";
                    p.AccentText = "#4F46E5";
                    p.AccentPillBg = "#EEF2FF";
                    p.AccentPillBorder = "#C7D2FE";
                    p.SavingsBg = "#ECFDF5";
                    p.SavingsBorder = "#A7F3D0";
                    p.SavingsFg = "#047857";
                    p.ColorInput = "#4F46E5";
                    p.ColorOutput = "#D97706";
                    p.ColorCache = "#059669";
                    p.ChipBg = "#F1F5F9";
                    p.ChipBorder = "#CBD5E1";
                    p.PulseBarTop = "#6366F1";
                    p.PulseBarBottom = "#A5B4FC";
                    p.PulsePeakTop = "#10B981";
                    p.PulsePeakBottom = "#34D399";
                    p.PulseLine = "#4F46E5";
                }
                return p;
            }
        }

        // ==================== 1. 战报分享海报 API ====================

        public static string BuildSharePosterSvg(IList<RecordBucket> records, int days, bool isCny, double usdToCny)
        {
            return BuildSharePosterSvg(records, days, isCny, usdToCny, true);
        }

        public static string BuildSharePosterSvg(IList<RecordBucket> records, int days, bool isCny, double usdToCny, bool isDark)
        {
            double fx = SanitizeRate(usdToCny);
            PosterPalette pal = PosterPalette.Create(isDark);
            AggregatedStats stats = AggregateRecords(records, days, isDark);

            StringBuilder sb = new StringBuilder(16384);

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"840\" height=\"480\" viewBox=\"0 0 840 480\" fill=\"none\" role=\"img\" aria-labelledby=\"toklensPosterTitle toklensPosterDesc\">\n");

            string posterTitle = "知微 · TokLens AI 算力账单海报 (" + stats.PeriodBadgeText + ")";
            string posterDesc = string.Format(
                CultureInfo.InvariantCulture,
                "统计周期 {0}，折算总金额 {1}，总消耗 {2} Tokens",
                stats.PeriodBadgeText,
                FormatPosterMoney(stats.TotalCostUsd, isCny, fx),
                FormatCompactTokens(stats.TotalTokens));

            sb.Append("  <title id=\"toklensPosterTitle\">").Append(EscapeXml(posterTitle)).Append("</title>\n");
            sb.Append("  <desc id=\"toklensPosterDesc\">").Append(EscapeXml(posterDesc)).Append("</desc>\n");

            AppendPosterDefs(sb, pal);

            // 背景底板与环境微光
            sb.Append("  <g clip-path=\"url(#tlCanvasClip)\">\n");
            sb.Append("    <rect x=\"0\" y=\"0\" width=\"840\" height=\"480\" fill=\"url(#tlBgGrad)\"/>\n");
            sb.Append("    <circle cx=\"130\" cy=\"50\" r=\"280\" fill=\"url(#tlGlowIndigo)\"/>\n");
            sb.Append("    <circle cx=\"740\" cy=\"430\" r=\"260\" fill=\"url(#tlGlowEmerald)\"/>\n");
            sb.Append("    <rect x=\"0\" y=\"0\" width=\"840\" height=\"480\" fill=\"url(#tlDotGrid)\"/>\n");

            // 1. 顶部品牌头 (Y: 18..60)
            AppendPosterHeader(sb, pal, stats, isCny, fx);

            // 2. 左侧主视觉 KPI 大卡 (X: 24, Y: 70, W: 368, H: 364)
            AppendLeftHeroCard(sb, pal, stats, isCny, fx);

            // 3. 右上卡片：Top 4 AI 编程工具占比条 (X: 406, Y: 70, W: 410, H: 175)
            AppendTopRightToolsCard(sb, pal, stats, isCny, fx);

            // 4. 右下卡片：近 14 天算力脉冲 + Top 3 主力模型 (X: 406, Y: 259, W: 410, H: 175)
            AppendBottomRightPulseCard(sb, pal, stats);

            // 5. 底部极客签名栏 (Y: 444..470)
            AppendPosterFooter(sb, pal);

            sb.Append("  </g>\n");

            // 外框高光描边
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "  <rect x=\"0.5\" y=\"0.5\" width=\"839\" height=\"479\" rx=\"17.5\" stroke=\"{0}\" stroke-width=\"1\"/>\n",
                pal.OuterBorder);

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        public static void ExportSharePosterSvg(string filePath, IList<RecordBucket> records, int days, bool isCny, double usdToCny)
        {
            ExportSharePosterSvg(filePath, records, days, isCny, usdToCny, true);
        }

        public static void ExportSharePosterSvg(string filePath, IList<RecordBucket> records, int days, bool isCny, double usdToCny, bool isDark)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("导出路径不能为空。", "filePath");

            string svg = BuildSharePosterSvg(records, days, isCny, usdToCny, isDark);
            WriteUtf8SvgFile(filePath, svg);
        }

        // ==================== 2. GitHub Profile README 徽章 API ====================

        public static string BuildReadmeShieldSvg(IList<RecordBucket> records, int days, bool isCny, double usdToCny)
        {
            double fx = SanitizeRate(usdToCny);
            AggregatedStats stats = AggregateRecords(records, days, true);

            string leftLabel = (days > 0 && days < 36500)
                ? string.Format(CultureInfo.InvariantCulture, "TokLens {0}d", days)
                : "TokLens all";

            string tokPart = FormatShieldTokens(stats.TotalTokens) + " tok";
            string moneyPart = FormatShieldMoney(stats.TotalCostUsd, isCny, fx);
            string rightValue = tokPart + " \u00B7 " + moneyPart;

            double leftTextPx = MeasureTextWidthPx(leftLabel, 11.0, false);
            double rightTextPx = MeasureTextWidthPx(rightValue, 11.0, true);

            // 左侧含 11px 棱镜徽标 + 间距，右侧含对称内边距
            int leftWidth = Math.Max(68, (int)Math.Ceiling(23.0 + leftTextPx + 9.0));
            int rightWidth = Math.Max(84, (int)Math.Ceiling(rightTextPx + 20.0));
            int totalWidth = leftWidth + rightWidth;

            double leftTextX = 21.0 + (leftWidth - 21.0) * 0.5;
            double rightTextX = leftWidth + rightWidth * 0.5;

            string fullTitle = leftLabel + ": " + rightValue;

            StringBuilder sb = new StringBuilder(2048);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0}\" height=\"22\" viewBox=\"0 0 {0} 22\" role=\"img\" aria-label=\"{1}\">\n",
                totalWidth,
                EscapeXml(fullTitle));
            sb.Append("  <title>").Append(EscapeXml(fullTitle)).Append("</title>\n");

            sb.Append("  <defs>\n");
            sb.Append("    <linearGradient id=\"tlShieldLeft\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">\n");
            sb.Append("      <stop offset=\"0%\" stop-color=\"#27272A\"/>\n");
            sb.Append("      <stop offset=\"100%\" stop-color=\"#18181B\"/>\n");
            sb.Append("    </linearGradient>\n");
            sb.Append("    <linearGradient id=\"tlShieldRight\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">\n");
            sb.Append("      <stop offset=\"0%\" stop-color=\"#6366F1\"/>\n");
            sb.Append("      <stop offset=\"100%\" stop-color=\"#4338CA\"/>\n");
            sb.Append("    </linearGradient>\n");
            sb.Append("    <linearGradient id=\"tlShieldSheen\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">\n");
            sb.Append("      <stop offset=\"0%\" stop-color=\"#FFFFFF\" stop-opacity=\"0.12\"/>\n");
            sb.Append("      <stop offset=\"100%\" stop-color=\"#000000\" stop-opacity=\"0.10\"/>\n");
            sb.Append("    </linearGradient>\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <clipPath id=\"tlShieldClip\"><rect x=\"0\" y=\"0\" width=\"{0}\" height=\"22\" rx=\"4\"/></clipPath>\n",
                totalWidth);
            sb.Append("  </defs>\n");

            sb.Append("  <g clip-path=\"url(#tlShieldClip)\">\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"0\" y=\"0\" width=\"{0}\" height=\"22\" fill=\"url(#tlShieldLeft)\"/>\n",
                leftWidth);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"{0}\" y=\"0\" width=\"{1}\" height=\"22\" fill=\"url(#tlShieldRight)\"/>\n",
                leftWidth,
                rightWidth);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"0\" y=\"0\" width=\"{0}\" height=\"22\" fill=\"url(#tlShieldSheen)\"/>\n",
                totalWidth);

            // 左侧微型棱镜图标 (X: 7..18, Y: 5.5..16.5)
            sb.Append("    <g transform=\"translate(7, 5.5)\">\n");
            sb.Append("      <polygon points=\"5.5,0.4 10.4,3.2 10.4,7.8 5.5,10.6 0.6,7.8 0.6,3.2\" fill=\"#818CF8\" fill-opacity=\"0.22\" stroke=\"#818CF8\" stroke-width=\"1.1\" stroke-linejoin=\"round\"/>\n");
            sb.Append("      <polyline points=\"0.8,3.3 5.5,5.9 10.2,3.3\" fill=\"none\" stroke=\"#A5B4FC\" stroke-width=\"0.95\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
            sb.Append("      <line x1=\"5.5\" y1=\"5.9\" x2=\"5.5\" y2=\"10.4\" stroke=\"#A5B4FC\" stroke-width=\"0.95\" stroke-linecap=\"round\"/>\n");
            sb.Append("    </g>\n");

            // 左侧标签文字（带 1px 阴影层）
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <g font-family=\"{0}\" font-size=\"11\" font-weight=\"600\" text-anchor=\"middle\">\n",
                FontStackUi);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <text x=\"{0}\" y=\"15.8\" fill=\"#000000\" fill-opacity=\"0.35\">{1}</text>\n",
                Fmt(leftTextX),
                EscapeXml(leftLabel));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <text x=\"{0}\" y=\"14.8\" fill=\"#F4F4F6\">{1}</text>\n",
                Fmt(leftTextX),
                EscapeXml(leftLabel));
            sb.Append("    </g>\n");

            // 右侧数值文字（等宽数字栈 + 1px 阴影层）
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <g font-family=\"{0}\" font-size=\"11\" font-weight=\"700\" text-anchor=\"middle\">\n",
                FontStackMono);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <text x=\"{0}\" y=\"15.8\" fill=\"#09090B\" fill-opacity=\"0.32\">{1}</text>\n",
                Fmt(rightTextX),
                EscapeXml(rightValue));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <text x=\"{0}\" y=\"14.8\" fill=\"#FFFFFF\">{1}</text>\n",
                Fmt(rightTextX),
                EscapeXml(rightValue));
            sb.Append("    </g>\n");

            sb.Append("  </g>\n");

            // 极淡内描边增强明暗背景下的轮廓清晰度
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "  <rect x=\"0.5\" y=\"0.5\" width=\"{0}\" height=\"21\" rx=\"3.5\" fill=\"none\" stroke=\"#FFFFFF\" stroke-opacity=\"0.14\" stroke-width=\"1\"/>\n",
                totalWidth - 1);

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        public static void ExportReadmeShieldSvg(string filePath, IList<RecordBucket> records, int days, bool isCny, double usdToCny)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("导出路径不能为空。", "filePath");

            string svg = BuildReadmeShieldSvg(records, days, isCny, usdToCny);
            WriteUtf8SvgFile(filePath, svg);
        }

        // ==================== 海报区块构建逻辑 ====================

        private static void AppendPosterDefs(StringBuilder sb, PosterPalette pal)
        {
            sb.Append("  <defs>\n");
            sb.Append("    <clipPath id=\"tlCanvasClip\"><rect x=\"0\" y=\"0\" width=\"840\" height=\"480\" rx=\"18\"/></clipPath>\n");
            sb.Append("    <clipPath id=\"tlTokMixClip\"><rect x=\"46\" y=\"304\" width=\"324\" height=\"10\" rx=\"5\"/></clipPath>\n");

            sb.Append("    <linearGradient id=\"tlBgGrad\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"{0}\"/>\n", pal.BgStart);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"100%\" stop-color=\"{0}\"/>\n", pal.BgEnd);
            sb.Append("    </linearGradient>\n");

            sb.Append("    <radialGradient id=\"tlGlowIndigo\" cx=\"50%\" cy=\"50%\" r=\"50%\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"#6366F1\" stop-opacity=\"{0}\"/>\n", pal.GlowIndigoOpacity);
            sb.Append("      <stop offset=\"100%\" stop-color=\"#6366F1\" stop-opacity=\"0\"/>\n");
            sb.Append("    </radialGradient>\n");

            sb.Append("    <radialGradient id=\"tlGlowEmerald\" cx=\"50%\" cy=\"50%\" r=\"50%\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"#10B981\" stop-opacity=\"{0}\"/>\n", pal.GlowEmeraldOpacity);
            sb.Append("      <stop offset=\"100%\" stop-color=\"#10B981\" stop-opacity=\"0\"/>\n");
            sb.Append("    </radialGradient>\n");

            sb.Append("    <pattern id=\"tlDotGrid\" width=\"20\" height=\"20\" patternUnits=\"userSpaceOnUse\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <circle cx=\"2\" cy=\"2\" r=\"0.85\" fill=\"{0}\" fill-opacity=\"{1}\"/>\n", pal.DotPatternFill, pal.DotPatternOpacity);
            sb.Append("    </pattern>\n");

            sb.Append("    <linearGradient id=\"tlCardBg\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"{0}\"/>\n", pal.CardBgStart);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"100%\" stop-color=\"{0}\"/>\n", pal.CardBgEnd);
            sb.Append("    </linearGradient>\n");

            sb.Append("    <linearGradient id=\"tlCardBorder\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"{0}\"/>\n", pal.CardBorderStart);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"55%\" stop-color=\"{0}\"/>\n", pal.CardBorderEnd);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"100%\" stop-color=\"{0}\"/>\n", pal.CardBorderStart);
            sb.Append("    </linearGradient>\n");

            sb.Append("    <linearGradient id=\"tlBrandLogo\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">\n");
            sb.Append("      <stop offset=\"0%\" stop-color=\"#6366F1\"/>\n");
            sb.Append("      <stop offset=\"100%\" stop-color=\"#4338CA\"/>\n");
            sb.Append("    </linearGradient>\n");

            sb.Append("    <linearGradient id=\"tlPulseBar\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"{0}\" stop-opacity=\"0.88\"/>\n", pal.PulseBarTop);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"100%\" stop-color=\"{0}\" stop-opacity=\"0.28\"/>\n", pal.PulseBarBottom);
            sb.Append("    </linearGradient>\n");

            sb.Append("    <linearGradient id=\"tlPulsePeak\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"{0}\" stop-opacity=\"0.96\"/>\n", pal.PulsePeakTop);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"100%\" stop-color=\"{0}\" stop-opacity=\"0.38\"/>\n", pal.PulsePeakBottom);
            sb.Append("    </linearGradient>\n");

            sb.Append("    <linearGradient id=\"tlPulseArea\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"0%\" stop-color=\"{0}\" stop-opacity=\"0.22\"/>\n", pal.PulseLine);
            sb.AppendFormat(CultureInfo.InvariantCulture, "      <stop offset=\"100%\" stop-color=\"{0}\" stop-opacity=\"0.0\"/>\n", pal.PulseLine);
            sb.Append("    </linearGradient>\n");

            sb.Append("  </defs>\n");
        }

        private static void AppendBentoCardFrame(StringBuilder sb, PosterPalette pal, double x, double y, double w, double h)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"14\" fill=\"#000000\" fill-opacity=\"{4}\"/>\n",
                Fmt(x),
                Fmt(y + 2.0),
                Fmt(w),
                Fmt(h),
                pal.CardShadowOpacity);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"14\" fill=\"url(#tlCardBg)\" stroke=\"url(#tlCardBorder)\" stroke-width=\"1\"/>\n",
                Fmt(x),
                Fmt(y),
                Fmt(w),
                Fmt(h));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{1}\" stroke=\"#FFFFFF\" stroke-opacity=\"{3}\" stroke-width=\"1\"/>\n",
                Fmt(x + 16.0),
                Fmt(y + 0.5),
                Fmt(x + w - 16.0),
                pal.IsDark ? "0.08" : "0.65");
        }

        private static void AppendPosterHeader(StringBuilder sb, PosterPalette pal, AggregatedStats stats, bool isCny, double fx)
        {
            // 品牌徽标底座 (24, 19, 30x30)
            sb.Append("    <rect x=\"24\" y=\"19\" width=\"30\" height=\"30\" rx=\"8\" fill=\"url(#tlBrandLogo)\"/>\n");
            sb.Append("    <rect x=\"24.5\" y=\"19.5\" width=\"29\" height=\"29\" rx=\"7.5\" stroke=\"#FFFFFF\" stroke-opacity=\"0.22\" stroke-width=\"1\"/>\n");
            // 棱镜几何矢量标志
            sb.Append("    <g transform=\"translate(31, 26)\">\n");
            sb.Append("      <polygon points=\"8,0.8 14.8,4.6 14.8,11.4 8,15.2 1.2,11.4 1.2,4.6\" fill=\"#FFFFFF\" fill-opacity=\"0.14\" stroke=\"#FFFFFF\" stroke-width=\"1.4\" stroke-linejoin=\"round\"/>\n");
            sb.Append("      <polyline points=\"1.4,4.8 8,8.5 14.6,4.8\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"1.25\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
            sb.Append("      <line x1=\"8\" y1=\"8.5\" x2=\"8\" y2=\"15\" stroke=\"#FFFFFF\" stroke-width=\"1.25\" stroke-linecap=\"round\"/>\n");
            sb.Append("    </g>\n");

            // 主品牌标题
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"64\" y=\"39.5\" font-family=\"{0}\" font-size=\"15.5\" font-weight=\"700\" fill=\"{1}\">知微 · TokLens AI 算力账单海报</text>\n",
                FontStackUi,
                pal.FgPrimary);

            // 统计周期胶囊 (X: 322，留足中英文混排安全间距)
            string periodTxt = stats.PeriodBadgeText;
            double pillW = Math.Max(70.0, MeasureTextWidthPx(periodTxt, 11.5, false) + 28.0);
            double pillX = 322.0;
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"{0}\" y=\"23\" width=\"{1}\" height=\"22\" rx=\"11\" fill=\"{2}\" stroke=\"{3}\" stroke-width=\"1\"/>\n",
                Fmt(pillX),
                Fmt(pillW),
                pal.AccentPillBg,
                pal.AccentPillBorder);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <circle cx=\"{0}\" cy=\"34\" r=\"3\" fill=\"{1}\"/>\n",
                Fmt(pillX + 11.0),
                pal.AccentText);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"{0}\" y=\"38\" font-family=\"{1}\" font-size=\"11.5\" font-weight=\"600\" fill=\"{2}\">{3}</text>\n",
                Fmt(pillX + 19.0),
                FontStackUi,
                pal.AccentText,
                EscapeXml(periodTxt));

            // 右侧生成日期与币种汇率微标
            string fxNote = isCny
                ? string.Format(CultureInfo.InvariantCulture, "CNY (汇率 {0:F2}) · {1}", fx, stats.GeneratedDateText)
                : string.Format(CultureInfo.InvariantCulture, "USD Billing · {0}", stats.GeneratedDateText);

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"816\" y=\"38.5\" text-anchor=\"end\" font-family=\"{0}\" font-size=\"11.5\" font-weight=\"500\" fill=\"{1}\">{2}</text>\n",
                FontStackMono,
                pal.FgMuted,
                EscapeXml(fxNote));

            // 顶部分割细线
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"24\" y1=\"59\" x2=\"816\" y2=\"59\" stroke=\"{0}\" stroke-width=\"1\"/>\n",
                pal.DividerStroke);
        }

        private static void AppendLeftHeroCard(StringBuilder sb, PosterPalette pal, AggregatedStats stats, bool isCny, double fx)
        {
            AppendBentoCardFrame(sb, pal, 24.0, 70.0, 368.0, 364.0);

            // 卡片顶部小标
            sb.Append("    <g transform=\"translate(46, 90)\">\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <rect x=\"0\" y=\"1\" width=\"12\" height=\"12\" rx=\"3.5\" fill=\"{0}\" fill-opacity=\"0.18\" stroke=\"{0}\" stroke-width=\"1.1\"/>\n",
                pal.AccentText);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <circle cx=\"6\" cy=\"7\" r=\"2.2\" fill=\"{0}\"/>\n",
                pal.AccentText);
            sb.Append("    </g>\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"64\" y=\"101\" font-family=\"{0}\" font-size=\"12\" font-weight=\"600\" fill=\"{1}\">等效算力总支出</text>\n",
                FontStackUi,
                pal.FgSecondary);

            string secondaryMoney = isCny
                ? ("≈ " + FormatPosterMoney(stats.TotalCostUsd, false, fx))
                : ("≈ " + FormatPosterMoney(stats.TotalCostUsd, true, fx));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"370\" y=\"101\" text-anchor=\"end\" font-family=\"{0}\" font-size=\"11\" font-weight=\"500\" fill=\"{1}\">{2}</text>\n",
                FontStackMono,
                pal.FgMuted,
                EscapeXml(secondaryMoney));

            // 主视觉大字号折算总金额
            string heroMoney = FormatPosterMoney(stats.TotalCostUsd, isCny, fx);
            int heroFontSize = 40;
            if (heroMoney.Length > 13)
                heroFontSize = 28;
            else if (heroMoney.Length > 10)
                heroFontSize = 33;

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"46\" y=\"152\" font-family=\"{0}\" font-size=\"{1}\" font-weight=\"700\" letter-spacing=\"-1\" fill=\"{2}\">{3}</text>\n",
                FontStackMono,
                heroFontSize,
                pal.FgPrimary,
                EscapeXml(heroMoney));

            // 缓存省钱绿标徽章 (Y: 170..194)
            string savingsText;
            if (stats.SavedUsd >= 0.0005)
            {
                savingsText = string.Format(
                    CultureInfo.InvariantCulture,
                    "缓存已省 {0} (-{1:F1}%) · 命中 {2:F1}%",
                    FormatPosterMoney(stats.SavedUsd, isCny, fx),
                    stats.SavedPercent,
                    stats.CacheHitPercent);
                if (MeasureTextWidthPx(savingsText, 11.0, false) + 34.0 > 324.0)
                {
                    savingsText = string.Format(
                        CultureInfo.InvariantCulture,
                        "已省 {0} (-{1:F0}%) · 命中 {2:F1}%",
                        FormatPosterMoney(stats.SavedUsd, isCny, fx),
                        stats.SavedPercent,
                        stats.CacheHitPercent);
                }
            }
            else
            {
                savingsText = string.Format(
                    CultureInfo.InvariantCulture,
                    "Prompt 缓存命中率 {0:F1}% · 原生精算计费",
                    stats.CacheHitPercent);
            }

            savingsText = TruncateByPixelWidth(savingsText, 290.0, 11.0, false);
            double savW = Math.Min(324.0, Math.Max(180.0, MeasureTextWidthPx(savingsText, 11.0, false) + 34.0));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"46\" y=\"170\" width=\"{0}\" height=\"24\" rx=\"12\" fill=\"{1}\" stroke=\"{2}\" stroke-width=\"1\"/>\n",
                Fmt(savW),
                pal.SavingsBg,
                pal.SavingsBorder);

            // 绿标内盾牌对勾图标
            sb.Append("    <g transform=\"translate(55, 176)\">\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <path d=\"M6,0.6 L10.8,2.4 L10.8,5.8 C10.8,8.6 8.6,10.5 6,11.4 C3.4,10.5 1.2,8.6 1.2,5.8 L1.2,2.4 Z\" fill=\"none\" stroke=\"{0}\" stroke-width=\"1.2\" stroke-linejoin=\"round\"/>\n",
                pal.SavingsFg);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <polyline points=\"3.8,6.0 5.3,7.5 8.3,4.3\" fill=\"none\" stroke=\"{0}\" stroke-width=\"1.2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n",
                pal.SavingsFg);
            sb.Append("    </g>\n");

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"71\" y=\"186\" font-family=\"{0}\" font-size=\"11\" font-weight=\"600\" fill=\"{1}\">{2}</text>\n",
                FontStackUi,
                pal.SavingsFg,
                EscapeXml(savingsText));

            // 水平分割细线
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"46\" y1=\"212\" x2=\"370\" y2=\"212\" stroke=\"{0}\" stroke-width=\"1\"/>\n",
                pal.DividerStroke);

            // 双栏核心吞吐量指标 (Y: 228..282)
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"46\" y=\"234\" font-family=\"{0}\" font-size=\"11\" font-weight=\"600\" fill=\"{1}\">总 TOKEN 消耗</text>\n",
                FontStackUi,
                pal.FgMuted);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"46\" y=\"264\" font-family=\"{0}\" font-size=\"23\" font-weight=\"700\" fill=\"{1}\">{2}</text>\n",
                FontStackMono,
                pal.FgPrimary,
                EscapeXml(FormatCompactTokens(stats.TotalTokens)));

            string exactTokHint = stats.TotalTokens > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:N0} tok", stats.TotalTokens)
                : "0 tok";
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"46\" y=\"281\" font-family=\"{0}\" font-size=\"10\" fill=\"{1}\">{2}</text>\n",
                FontStackMono,
                pal.FgMuted,
                EscapeXml(exactTokHint));

            // 竖向分隔短线
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"224\" y1=\"224\" x2=\"224\" y2=\"282\" stroke=\"{0}\" stroke-width=\"1\"/>\n",
                pal.DividerStroke);

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"240\" y=\"234\" font-family=\"{0}\" font-size=\"11\" font-weight=\"600\" fill=\"{1}\">对话交互轮次</text>\n",
                FontStackUi,
                pal.FgMuted);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"240\" y=\"264\" font-family=\"{0}\" font-size=\"22\" font-weight=\"700\" fill=\"{1}\">{2}</text>\n",
                FontStackMono,
                pal.FgPrimary,
                EscapeXml(stats.TotalTurns.ToString("N0", CultureInfo.InvariantCulture)));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"240\" y=\"281\" font-family=\"{0}\" font-size=\"10\" fill=\"{1}\">API / Agent Turns</text>\n",
                FontStackMono,
                pal.FgMuted);

            // 三色比例圆角进度条 (输入 / 输出 / 缓存命中) (X: 46, Y: 304, W: 324, H: 10)
            long inputCombined = stats.FreshInput + stats.CacheWrite;
            long outputTok = stats.Output;
            long cacheHitTok = stats.CacheRead;
            long sumTok = inputCombined + outputTok + cacheHitTok;

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <rect x=\"46\" y=\"304\" width=\"324\" height=\"10\" rx=\"5\" fill=\"{0}\"/>\n",
                pal.TrackBg);

            double pctIn = 0.0;
            double pctOut = 0.0;
            double pctCache = 0.0;

            if (sumTok > 0)
            {
                pctIn = inputCombined * 100.0 / sumTok;
                pctOut = outputTok * 100.0 / sumTok;
                pctCache = Math.Max(0.0, 100.0 - pctIn - pctOut);

                double wIn, wOut, wCache;
                ComputeThreeSegmentWidths(324.0, inputCombined, outputTok, cacheHitTok, out wIn, out wOut, out wCache);

                sb.Append("    <g clip-path=\"url(#tlTokMixClip)\">\n");
                double curX = 46.0;
                if (wIn > 0.0)
                {
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "      <rect x=\"{0}\" y=\"304\" width=\"{1}\" height=\"10\" fill=\"{2}\"/>\n",
                        Fmt(curX),
                        Fmt(wIn),
                        pal.ColorInput);
                    curX += wIn + ((wOut > 0.0 || wCache > 0.0) ? 1.5 : 0.0);
                }
                if (wOut > 0.0)
                {
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "      <rect x=\"{0}\" y=\"304\" width=\"{1}\" height=\"10\" fill=\"{2}\"/>\n",
                        Fmt(curX),
                        Fmt(wOut),
                        pal.ColorOutput);
                    curX += wOut + (wCache > 0.0 ? 1.5 : 0.0);
                }
                if (wCache > 0.0)
                {
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "      <rect x=\"{0}\" y=\"304\" width=\"{1}\" height=\"10\" fill=\"{2}\"/>\n",
                        Fmt(curX),
                        Fmt(Math.Max(0.0, 370.0 - curX)),
                        pal.ColorCache);
                }
                sb.Append("    </g>\n");
            }

            // 三行构成明细图例 (Y: 338, 366, 394)
            AppendTokenBreakdownRow(sb, pal, 338.0, pal.ColorInput, "输入 (新增 + 缓存写)", inputCombined, pctIn);
            AppendTokenBreakdownRow(sb, pal, 366.0, pal.ColorOutput, "输出 (推理 + 代码生成)", outputTok, pctOut);
            AppendTokenBreakdownRow(sb, pal, 394.0, pal.ColorCache, "缓存命中 (上下文复用)", cacheHitTok, pctCache);
        }

        private static void AppendTokenBreakdownRow(
            StringBuilder sb,
            PosterPalette pal,
            double baselineY,
            string dotColor,
            string label,
            long tokens,
            double pct)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <circle cx=\"51\" cy=\"{0}\" r=\"4\" fill=\"{1}\"/>\n",
                Fmt(baselineY - 4.0),
                dotColor);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"62\" y=\"{0}\" font-family=\"{1}\" font-size=\"11.5\" font-weight=\"500\" fill=\"{2}\">{3}</text>\n",
                Fmt(baselineY),
                FontStackUi,
                pal.FgSecondary,
                EscapeXml(label));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"312\" y=\"{0}\" text-anchor=\"end\" font-family=\"{1}\" font-size=\"11.5\" font-weight=\"600\" fill=\"{2}\">{3}</text>\n",
                Fmt(baselineY),
                FontStackMono,
                pal.FgPrimary,
                EscapeXml(FormatCompactTokens(tokens)));
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"370\" y=\"{0}\" text-anchor=\"end\" font-family=\"{1}\" font-size=\"11.5\" font-weight=\"600\" fill=\"{2}\">{3:F1}%</text>\n",
                Fmt(baselineY),
                FontStackMono,
                pal.FgMuted,
                pct);
        }

        private static void AppendTopRightToolsCard(StringBuilder sb, PosterPalette pal, AggregatedStats stats, bool isCny, double fx)
        {
            AppendBentoCardFrame(sb, pal, 406.0, 70.0, 410.0, 175.0);

            // 标题栏
            sb.Append("    <g transform=\"translate(426, 87)\">\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <rect x=\"0\" y=\"1\" width=\"13\" height=\"11\" rx=\"2.5\" fill=\"none\" stroke=\"{0}\" stroke-width=\"1.2\"/>\n",
                pal.AccentText);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <polyline points=\"3.2,4.5 5.6,6.5 3.2,8.5\" fill=\"none\" stroke=\"{0}\" stroke-width=\"1.2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n",
                pal.AccentText);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <line x1=\"6.8\" y1=\"8.5\" x2=\"9.8\" y2=\"8.5\" stroke=\"{0}\" stroke-width=\"1.2\" stroke-linecap=\"round\"/>\n",
                pal.AccentText);
            sb.Append("    </g>\n");

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"445\" y=\"98\" font-family=\"{0}\" font-size=\"12\" font-weight=\"600\" fill=\"{1}\">Top 4 AI 编程工具占比</text>\n",
                FontStackUi,
                pal.FgSecondary);

            string toolCountBadge = stats.TotalToolCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "共识别 {0} 款工具", stats.TotalToolCount)
                : "等待会话接入";
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"796\" y=\"98\" text-anchor=\"end\" font-family=\"{0}\" font-size=\"11\" font-weight=\"500\" fill=\"{1}\">{2}</text>\n",
                FontStackUi,
                pal.FgMuted,
                EscapeXml(toolCountBadge));

            if (stats.TopTools.Count == 0)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <rect x=\"426\" y=\"118\" width=\"370\" height=\"104\" rx=\"8\" fill=\"{0}\" fill-opacity=\"0.35\"/>\n",
                    pal.TrackBg);
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"611\" y=\"166\" text-anchor=\"middle\" font-family=\"{0}\" font-size=\"12\" font-weight=\"500\" fill=\"{1}\">当前统计周期内暂无 AI 编程工具消耗记录</text>\n",
                    FontStackUi,
                    pal.FgMuted);
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"611\" y=\"186\" text-anchor=\"middle\" font-family=\"{0}\" font-size=\"10.5\" fill=\"{1}\">支持 Claude Code · Cursor · Codex · Antigravity 等全量自动扫描</text>\n",
                    FontStackUi,
                    pal.FgMuted);
                return;
            }

            // 最多渲染 Top 4 行 (Y: 113, 142, 171, 200)
            int count = Math.Min(4, stats.TopTools.Count);
            for (int i = 0; i < count; i++)
            {
                RankedItem item = stats.TopTools[i];
                double rowTop = 113.0 + i * 29.0;
                double textY = rowTop + 13.5;

                // 品牌语义色圆角指示条
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <rect x=\"426\" y=\"{0}\" width=\"5\" height=\"15\" rx=\"2.5\" fill=\"{1}\"/>\n",
                    Fmt(rowTop + 1.5),
                    item.ColorHex);

                // 工具名（像素级宽度防撞截断，保证不覆盖 X=558 进度条）
                string displayTool = TruncateByPixelWidth(item.Name, 112.0, 12.0, false);
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"438\" y=\"{0}\" font-family=\"{1}\" font-size=\"12\" font-weight=\"600\" fill=\"{2}\">{3}</text>\n",
                    Fmt(textY),
                    FontStackUi,
                    pal.FgPrimary,
                    EscapeXml(displayTool));

                // 语义色占比条轨道 (X: 558..672, 宽 114px)
                double trackX = 558.0;
                double trackW = 114.0;
                double barY = rowTop + 5.5;
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"7\" rx=\"3.5\" fill=\"{3}\"/>\n",
                    Fmt(trackX),
                    Fmt(barY),
                    Fmt(trackW),
                    pal.TrackBg);

                double ratio = Math.Max(0.0, Math.Min(1.0, item.SharePercent / 100.0));
                double fillW = ratio > 0.0 ? Math.Max(5.0, trackW * ratio) : 0.0;
                if (fillW > trackW) fillW = trackW;
                if (fillW > 0.0)
                {
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "    <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"7\" rx=\"3.5\" fill=\"{3}\"/>\n",
                        Fmt(trackX),
                        Fmt(barY),
                        Fmt(fillW),
                        item.ColorHex);
                }

                // 金额或 Token 值 (右对齐 X: 742)
                string metricVal = stats.TotalCostUsd > 1e-9
                    ? FormatPosterMoney(item.CostUsd, isCny, fx)
                    : FormatCompactTokens(item.Tokens);
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"742\" y=\"{0}\" text-anchor=\"end\" font-family=\"{1}\" font-size=\"11\" font-weight=\"500\" fill=\"{2}\">{3}</text>\n",
                    Fmt(textY),
                    FontStackMono,
                    pal.FgSecondary,
                    EscapeXml(metricVal));

                // 占比百分比 (右对齐 X: 796)
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"796\" y=\"{0}\" text-anchor=\"end\" font-family=\"{1}\" font-size=\"11.5\" font-weight=\"700\" fill=\"{2}\">{3:F1}%</text>\n",
                    Fmt(textY),
                    FontStackMono,
                    pal.FgPrimary,
                    item.SharePercent);
            }
        }

        private static void AppendBottomRightPulseCard(StringBuilder sb, PosterPalette pal, AggregatedStats stats)
        {
            AppendBentoCardFrame(sb, pal, 406.0, 259.0, 410.0, 175.0);

            // 标题栏
            sb.Append("    <g transform=\"translate(426, 274)\">\n");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "      <polyline points=\"0.5,7 3.5,7 5.5,2 8.2,12 10.2,7 13,7\" fill=\"none\" stroke=\"{0}\" stroke-width=\"1.35\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n",
                pal.SavingsFg);
            sb.Append("    </g>\n");

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"445\" y=\"285\" font-family=\"{0}\" font-size=\"12\" font-weight=\"600\" fill=\"{1}\">近 14 天算力脉冲</text>\n",
                FontStackUi,
                pal.FgSecondary);

            string pulseSub = !string.IsNullOrEmpty(stats.PulsePeakLabel)
                ? (stats.PulsePeakLabel + " · " + stats.PulseRangeLabel)
                : stats.PulseRangeLabel;
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"796\" y=\"285\" text-anchor=\"end\" font-family=\"{0}\" font-size=\"10.5\" font-weight=\"500\" fill=\"{1}\">{2}</text>\n",
                FontStackMono,
                pal.FgMuted,
                EscapeXml(pulseSub));

            // 脉冲图绘图区 (X: 426..796, W: 370, Y: 296..348, H: 52)
            double chartX = 426.0;
            double chartW = 370.0;
            double chartTop = 296.0;
            double chartBottom = 348.0;
            double chartH = chartBottom - chartTop;

            // 水平参考虚线
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{1}\" stroke=\"{3}\" stroke-width=\"1\" stroke-dasharray=\"3,3\"/>\n",
                Fmt(chartX),
                Fmt(chartTop),
                Fmt(chartX + chartW),
                pal.GridLineStroke);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{1}\" stroke=\"{3}\" stroke-width=\"1\" stroke-dasharray=\"3,3\"/>\n",
                Fmt(chartX),
                Fmt((chartTop + chartBottom) * 0.5),
                Fmt(chartX + chartW),
                pal.GridLineStroke);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{1}\" stroke=\"{3}\" stroke-width=\"1\"/>\n",
                Fmt(chartX),
                Fmt(chartBottom),
                Fmt(chartX + chartW),
                pal.GridLineStroke);

            int nDays = stats.PulseDays.Count;
            if (nDays > 0)
            {
                bool useCostMetric = false;
                double maxVal = 0.0;
                int peakIdx = -1;

                for (int i = 0; i < nDays; i++)
                {
                    if (stats.PulseDays[i].CostUsd > 1e-6)
                    {
                        useCostMetric = true;
                        break;
                    }
                }

                for (int i = 0; i < nDays; i++)
                {
                    double v = useCostMetric ? stats.PulseDays[i].CostUsd : (double)stats.PulseDays[i].Tokens;
                    if (v > maxVal)
                    {
                        maxVal = v;
                        peakIdx = i;
                    }
                }

                double slotW = chartW / nDays;
                double barW = 13.0;
                double[] ptX = new double[nDays];
                double[] ptY = new double[nDays];

                for (int i = 0; i < nDays; i++)
                {
                    DailyPulsePoint dp = stats.PulseDays[i];
                    double v = useCostMetric ? dp.CostUsd : (double)dp.Tokens;
                    double norm = maxVal > 0.0 ? (v / maxVal) : 0.0;
                    double barH = v > 0.0 ? Math.Max(4.5, norm * (chartH - 4.0)) : 2.0;
                    double bx = chartX + i * slotW + (slotW - barW) * 0.5;
                    double by = chartBottom - barH;

                    ptX[i] = bx + barW * 0.5;
                    ptY[i] = v > 0.0 ? by : (chartBottom - 1.0);

                    if (v > 0.0)
                    {
                        string fillRef = (i == peakIdx) ? "url(#tlPulsePeak)" : "url(#tlPulseBar)";
                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "    <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"3\" fill=\"{4}\"/>\n",
                            Fmt(bx),
                            Fmt(by),
                            Fmt(barW),
                            Fmt(barH),
                            fillRef);
                    }
                    else
                    {
                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "    <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"2\" rx=\"1\" fill=\"{3}\"/>\n",
                            Fmt(bx),
                            Fmt(chartBottom - 2.0),
                            Fmt(barW),
                            pal.TrackBg);
                    }
                }

                // 叠加面积渐变与脉冲折线
                if (maxVal > 0.0)
                {
                    StringBuilder areaPts = new StringBuilder(256);
                    StringBuilder linePts = new StringBuilder(256);
                    areaPts.Append(Fmt(ptX[0])).Append(',').Append(Fmt(chartBottom));
                    for (int i = 0; i < nDays; i++)
                    {
                        if (i > 0) linePts.Append(' ');
                        linePts.Append(Fmt(ptX[i])).Append(',').Append(Fmt(ptY[i]));
                        areaPts.Append(' ').Append(Fmt(ptX[i])).Append(',').Append(Fmt(ptY[i]));
                    }
                    areaPts.Append(' ').Append(Fmt(ptX[nDays - 1])).Append(',').Append(Fmt(chartBottom));

                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "    <polygon points=\"{0}\" fill=\"url(#tlPulseArea)\"/>\n",
                        areaPts.ToString());
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "    <polyline points=\"{0}\" fill=\"none\" stroke=\"{1}\" stroke-width=\"1.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n",
                        linePts.ToString(),
                        pal.PulseLine);

                    if (peakIdx >= 0)
                    {
                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "    <circle cx=\"{0}\" cy=\"{1}\" r=\"3.2\" fill=\"{2}\" stroke=\"{3}\" stroke-width=\"1.2\"/>\n",
                            Fmt(ptX[peakIdx]),
                            Fmt(ptY[peakIdx]),
                            pal.PulsePeakTop,
                            pal.CardBgStart);
                    }
                }

                // 首、中、尾日期刻度 (Y: 360)
                int midIdx = nDays / 2;
                int[] labelIndices = new int[] { 0, midIdx, nDays - 1 };
                for (int k = 0; k < labelIndices.Length; k++)
                {
                    int idx = labelIndices[k];
                    if (idx >= 0 && idx < nDays)
                    {
                        string anchor = (k == 0) ? "start" : (k == 2 ? "end" : "middle");
                        double lx = (k == 0) ? chartX : (k == 2 ? (chartX + chartW) : ptX[idx]);
                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "    <text x=\"{0}\" y=\"360\" text-anchor=\"{1}\" font-family=\"{2}\" font-size=\"9.5\" fill=\"{3}\">{4}</text>\n",
                            Fmt(lx),
                            anchor,
                            FontStackMono,
                            pal.FgMuted,
                            EscapeXml(stats.PulseDays[idx].ShortLabel));
                    }
                }
            }

            // 分隔细线与主力模型 Top 3 标签区 (Y: 368..422，整行 370px 宽容纳完整模型名)
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <line x1=\"426\" y1=\"368\" x2=\"796\" y2=\"368\" stroke=\"{0}\" stroke-width=\"1\"/>\n",
                pal.DividerStroke);

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"426\" y=\"383\" font-family=\"{0}\" font-size=\"10\" font-weight=\"600\" fill=\"{1}\">主力模型 TOP 3</text>\n",
                FontStackUi,
                pal.FgMuted);

            if (stats.TotalModelCount > 0)
            {
                string modelHint = string.Format(CultureInfo.InvariantCulture, "共调用 {0} 款模型", stats.TotalModelCount);
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"796\" y=\"383\" text-anchor=\"end\" font-family=\"{0}\" font-size=\"10\" fill=\"{1}\">{2}</text>\n",
                    FontStackUi,
                    pal.FgMuted,
                    EscapeXml(modelHint));
            }

            if (stats.TopModels.Count == 0)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <rect x=\"426\" y=\"391\" width=\"370\" height=\"24\" rx=\"6\" fill=\"{0}\" stroke=\"{1}\" stroke-width=\"1\"/>\n",
                    pal.ChipBg,
                    pal.ChipBorder);
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"611\" y=\"407\" text-anchor=\"middle\" font-family=\"{0}\" font-size=\"10.5\" fill=\"{1}\">暂无模型调用分布</text>\n",
                    FontStackUi,
                    pal.FgMuted);
                return;
            }

            int mCount = Math.Min(3, stats.TopModels.Count);
            double gap = 7.0;
            double chipW = (370.0 - gap * (mCount - 1)) / mCount;

            for (int i = 0; i < mCount; i++)
            {
                RankedItem m = stats.TopModels[i];
                double chipX = 426.0 + i * (chipW + gap);
                string pctStr = string.Format(CultureInfo.InvariantCulture, "{0:F0}%", m.SharePercent);
                double pctW = MeasureTextWidthPx(pctStr, 9.8, true);
                double nameMaxPx = Math.Max(36.0, chipW - 16.0 - pctW - 12.0);
                string cleanModel = CleanModelDisplayId(m.Name);
                string shortName = TruncateByPixelWidth(cleanModel, nameMaxPx, 9.8, true);

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <rect x=\"{0}\" y=\"391\" width=\"{1}\" height=\"24\" rx=\"6\" fill=\"{2}\" stroke=\"{3}\" stroke-width=\"1\"/>\n",
                    Fmt(chipX),
                    Fmt(chipW),
                    pal.ChipBg,
                    pal.ChipBorder);

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <circle cx=\"{0}\" cy=\"403\" r=\"3.2\" fill=\"{1}\"/>\n",
                    Fmt(chipX + 9.0),
                    m.ColorHex);

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"{0}\" y=\"406.5\" font-family=\"{1}\" font-size=\"9.8\" font-weight=\"600\" fill=\"{2}\">{3}</text>\n",
                    Fmt(chipX + 16.0),
                    FontStackMono,
                    pal.FgPrimary,
                    EscapeXml(shortName));

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "    <text x=\"{0}\" y=\"406.5\" text-anchor=\"end\" font-family=\"{1}\" font-size=\"9.8\" font-weight=\"700\" fill=\"{2}\">{3}</text>\n",
                    Fmt(chipX + chipW - 7.0),
                    FontStackMono,
                    m.ColorHex,
                    EscapeXml(pctStr));
            }
        }

        private static void AppendPosterFooter(StringBuilder sb, PosterPalette pal)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <circle cx=\"30\" cy=\"456\" r=\"3.5\" fill=\"{0}\"/>\n",
                pal.SavingsFg);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"40\" y=\"460\" font-family=\"{0}\" font-size=\"11\" font-weight=\"600\" fill=\"{1}\">Local-First AI Token Telemetry · Zero Prompt Collection</text>\n",
                FontStackMono,
                pal.FgMuted);

            string verTag = string.IsNullOrEmpty(AppConstants.VersionTag) ? "v2.2" : AppConstants.VersionTag;
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    <text x=\"816\" y=\"460\" text-anchor=\"end\" font-family=\"{0}\" font-size=\"11\" font-weight=\"500\" fill=\"{1}\">TokLens {2} · Pure Vector Telemetry</text>\n",
                FontStackMono,
                pal.FgMuted,
                EscapeXml(verTag));
        }

        // ==================== 数据过滤与统计聚合 ====================

        private static AggregatedStats AggregateRecords(IList<RecordBucket> records, int days, bool isDark)
        {
            AggregatedStats stats = new AggregatedStats();
            stats.Days = days;
            DateTime nowLocal = DateTime.Now;
            DateTime today = nowLocal.Date;
            stats.GeneratedDateText = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (days <= 0 || days >= 36500)
                stats.PeriodBadgeText = "全量累计";
            else if (days == 1)
                stats.PeriodBadgeText = "今日账单";
            else
                stats.PeriodBadgeText = string.Format(CultureInfo.InvariantCulture, "近 {0} 天", days);

            bool filterByDays = (days > 0 && days < 36500);
            DateTime cutoffDate = filterByDays ? today.AddDays(-(days - 1)) : DateTime.MinValue;
            DateTime upperBound = today.AddDays(1);

            Dictionary<string, RankedItem> toolMap = new Dictionary<string, RankedItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RankedItem> modelMap = new Dictionary<string, RankedItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DailyPulsePoint> allDailyMap = new Dictionary<string, DailyPulsePoint>(StringComparer.Ordinal);
            DateTime latestValidRecordDate = DateTime.MinValue;

            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    RecordBucket r = records[i];
                    if (r == null) continue;

                    long fi = Math.Max(0L, r.FreshInput);
                    long outTok = Math.Max(0L, r.Output);
                    long cr = Math.Max(0L, r.CacheRead);
                    long cw = Math.Max(0L, r.CacheWrite);
                    long totTok = fi + outTok + cr + cw;
                    int turns = Math.Max(0, r.Turns);
                    double cost = SafeDouble(r.CostUsd);
                    double baseCost = Math.Max(cost, SafeDouble(r.BaselineUsd));

                    if (totTok <= 0L && cost <= 0.0 && turns <= 0)
                        continue;

                    DateTime recDate;
                    bool hasValidDate = TryParseRecordDate(r.Date, out recDate);
                    if (hasValidDate && recDate <= upperBound)
                    {
                        if (recDate > latestValidRecordDate)
                            latestValidRecordDate = recDate;

                        string dKey = recDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        DailyPulsePoint dp;
                        if (!allDailyMap.TryGetValue(dKey, out dp))
                        {
                            dp = new DailyPulsePoint();
                            dp.DateKey = dKey;
                            dp.ShortLabel = recDate.ToString("MM-dd", CultureInfo.InvariantCulture);
                            allDailyMap[dKey] = dp;
                        }
                        dp.Tokens += totTok;
                        dp.CostUsd += cost;
                    }

                    if (filterByDays)
                    {
                        if (!hasValidDate || recDate < cutoffDate || recDate > upperBound)
                            continue;
                    }

                    stats.TotalTurns += turns;
                    stats.FreshInput += fi;
                    stats.Output += outTok;
                    stats.CacheRead += cr;
                    stats.CacheWrite += cw;
                    stats.TotalTokens += totTok;
                    stats.TotalCostUsd += cost;
                    stats.TotalBaselineUsd += baseCost;

                    string toolName = string.IsNullOrWhiteSpace(r.Tool) ? "Unknown Tool" : r.Tool.Trim();
                    RankedItem toolItem;
                    if (!toolMap.TryGetValue(toolName, out toolItem))
                    {
                        toolItem = new RankedItem();
                        toolItem.Name = toolName;
                        toolItem.ColorHex = GetBrandColorHex(toolName, isDark);
                        toolMap[toolName] = toolItem;
                    }
                    toolItem.Tokens += totTok;
                    toolItem.CostUsd += cost;

                    string modelName = string.IsNullOrWhiteSpace(r.Model) ? "unknown" : r.Model.Trim();
                    RankedItem modelItem;
                    if (!modelMap.TryGetValue(modelName, out modelItem))
                    {
                        modelItem = new RankedItem();
                        modelItem.Name = modelName;
                        modelItem.ColorHex = GetBrandColorHex(modelName, isDark);
                        modelMap[modelName] = modelItem;
                    }
                    modelItem.Tokens += totTok;
                    modelItem.CostUsd += cost;
                }
            }

            stats.SavedUsd = Math.Max(0.0, stats.TotalBaselineUsd - stats.TotalCostUsd);
            long totalPrompt = stats.FreshInput + stats.CacheRead + stats.CacheWrite;
            stats.CacheHitPercent = totalPrompt > 0L ? (stats.CacheRead * 100.0 / totalPrompt) : 0.0;
            stats.SavedPercent = stats.TotalBaselineUsd > 1e-9 ? (stats.SavedUsd * 100.0 / stats.TotalBaselineUsd) : 0.0;

            stats.TotalToolCount = toolMap.Count;
            stats.TotalModelCount = modelMap.Count;
            stats.TopTools = SortAndComputeShares(toolMap.Values, stats.TotalCostUsd, stats.TotalTokens, 4);
            stats.TopModels = SortAndComputeShares(modelMap.Values, stats.TotalCostUsd, stats.TotalTokens, 3);

            // 构建近 14 天算力脉冲序列
            DateTime pulseEnd = today;
            if (!filterByDays && latestValidRecordDate != DateTime.MinValue)
            {
                bool anyRecent14 = false;
                for (int d = 0; d < 14; d++)
                {
                    string checkKey = today.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (allDailyMap.ContainsKey(checkKey))
                    {
                        anyRecent14 = true;
                        break;
                    }
                }
                if (!anyRecent14)
                    pulseEnd = latestValidRecordDate;
            }

            DateTime pulseStart = pulseEnd.AddDays(-13);
            long peakDayTokens = 0L;
            for (int d = 0; d < 14; d++)
            {
                DateTime cur = pulseStart.AddDays(d);
                string key = cur.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DailyPulsePoint pt;
                if (!allDailyMap.TryGetValue(key, out pt))
                {
                    pt = new DailyPulsePoint();
                    pt.DateKey = key;
                    pt.ShortLabel = cur.ToString("MM-dd", CultureInfo.InvariantCulture);
                }
                if (pt.Tokens > peakDayTokens)
                    peakDayTokens = pt.Tokens;
                stats.PulseDays.Add(pt);
            }

            stats.PulseRangeLabel = pulseStart.ToString("MM/dd", CultureInfo.InvariantCulture)
                + "-" + pulseEnd.ToString("MM/dd", CultureInfo.InvariantCulture);
            if (peakDayTokens > 0L)
                stats.PulsePeakLabel = "峰值 " + FormatCompactTokens(peakDayTokens);

            return stats;
        }

        private static List<RankedItem> SortAndComputeShares(
            IEnumerable<RankedItem> items,
            double grandCostUsd,
            long grandTokens,
            int topLimit)
        {
            List<RankedItem> list = new List<RankedItem>(items);
            bool byCost = grandCostUsd > 1e-9;

            list.Sort(delegate(RankedItem a, RankedItem b)
            {
                int cmp = byCost ? b.CostUsd.CompareTo(a.CostUsd) : b.Tokens.CompareTo(a.Tokens);
                if (cmp != 0) return cmp;
                cmp = b.Tokens.CompareTo(a.Tokens);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < list.Count; i++)
            {
                RankedItem it = list[i];
                if (byCost)
                    it.SharePercent = Math.Max(0.0, Math.Min(100.0, it.CostUsd * 100.0 / grandCostUsd));
                else if (grandTokens > 0L)
                    it.SharePercent = Math.Max(0.0, Math.Min(100.0, it.Tokens * 100.0 / grandTokens));
                else
                    it.SharePercent = 0.0;
            }

            if (list.Count > topLimit)
                list.RemoveRange(topLimit, list.Count - topLimit);

            return list;
        }

        // ==================== 格式化、配色、测量与 XML 安全转义 ====================

        private static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            StringBuilder sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '&':
                        sb.Append("&amp;");
                        break;
                    case '<':
                        sb.Append("&lt;");
                        break;
                    case '>':
                        sb.Append("&gt;");
                        break;
                    case '"':
                        sb.Append("&quot;");
                        break;
                    case '\'':
                        sb.Append("&apos;");
                        break;
                    default:
                        if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20)
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void WriteUtf8SvgFile(string filePath, string svgContent)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, svgContent, new UTF8Encoding(false));
        }

        private static double SanitizeRate(double usdToCny)
        {
            if (double.IsNaN(usdToCny) || double.IsInfinity(usdToCny) || usdToCny <= 0.0)
                return DefaultFxRate;
            return usdToCny;
        }

        private static double SafeDouble(double val)
        {
            if (double.IsNaN(val) || double.IsInfinity(val) || val < 0.0)
                return 0.0;
            return val;
        }

        private static string Fmt(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "0";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static bool TryParseRecordDate(string rawDate, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(rawDate))
                return false;
            string s = rawDate.Trim();
            if (s.Length >= 10)
                s = s.Substring(0, 10);
            return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
        }

        private static string FormatCompactTokens(long n)
        {
            if (n <= 0L) return "0";
            if (n >= 1000000000L) return (n / 1e9).ToString("F2", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000L) return (n / 1e6).ToString("F2", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000L) return (n / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatShieldTokens(long n)
        {
            if (n <= 0L) return "0";
            if (n >= 1000000000L) return (n / 1e9).ToString("F1", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000L) return (n / 1e6).ToString("F1", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000L) return (n / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatPosterMoney(double usd, bool isCny, double fxRate)
        {
            double val = SafeDouble(isCny ? (usd * fxRate) : usd);
            string sym = isCny ? "\u00A5" : "$";
            if (val <= 0.0) return sym + "0.00";
            if (val >= 100.0) return sym + val.ToString("N2", CultureInfo.InvariantCulture);
            if (val >= 0.01) return sym + val.ToString("F2", CultureInfo.InvariantCulture);
            return sym + val.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string FormatShieldMoney(double usd, bool isCny, double fxRate)
        {
            double val = SafeDouble(isCny ? (usd * fxRate) : usd);
            string sym = isCny ? "\u00A5" : "$";
            if (val <= 0.0) return sym + "0.00";
            if (val >= 1000.0) return sym + val.ToString("N2", CultureInfo.InvariantCulture);
            if (val >= 0.01) return sym + val.ToString("F2", CultureInfo.InvariantCulture);
            return sym + val.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static void ComputeThreeSegmentWidths(
            double totalWidth,
            long v1,
            long v2,
            long v3,
            out double w1,
            out double w2,
            out double w3)
        {
            w1 = 0.0;
            w2 = 0.0;
            w3 = 0.0;
            long sum = v1 + v2 + v3;
            if (sum <= 0L)
                return;

            int active = (v1 > 0 ? 1 : 0) + (v2 > 0 ? 1 : 0) + (v3 > 0 ? 1 : 0);
            double gaps = (active > 1) ? (active - 1) * 1.5 : 0.0;
            double usable = Math.Max(12.0, totalWidth - gaps);

            w1 = v1 > 0 ? Math.Max(4.0, usable * v1 / sum) : 0.0;
            w2 = v2 > 0 ? Math.Max(4.0, usable * v2 / sum) : 0.0;
            w3 = v3 > 0 ? Math.Max(4.0, usable * v3 / sum) : 0.0;

            double alloc = w1 + w2 + w3;
            if (alloc > usable && alloc > 0.0)
            {
                double scale = usable / alloc;
                w1 *= scale;
                w2 *= scale;
                w3 *= scale;
            }
        }

        private static string CleanModelDisplayId(string rawModel)
        {
            if (string.IsNullOrWhiteSpace(rawModel))
                return "unknown";

            string s = rawModel.Trim();
            int slash = s.LastIndexOf('/');
            if (slash >= 0 && slash < s.Length - 1)
                s = s.Substring(slash + 1);

            return ModelDateTailRegex.Replace(s, "");
        }

        private static string TruncateByPixelWidth(string s, double maxPx, double fontSize, bool isMono)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            if (MeasureTextWidthPx(s, fontSize, isMono) <= maxPx)
                return s;

            double ellipsisW = MeasureTextWidthPx("\u2026", fontSize, isMono);
            double budget = Math.Max(4.0, maxPx - ellipsisW);
            double acc = 0.0;
            int keep = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double cw = MeasureCharWidthPx(s[i], fontSize, isMono);
                if (acc + cw > budget && i > 0)
                    break;
                acc += cw;
                keep = i + 1;
            }
            return s.Substring(0, keep) + "\u2026";
        }

        private static double MeasureTextWidthPx(string text, double fontSize, bool isMono)
        {
            if (string.IsNullOrEmpty(text))
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < text.Length; i++)
            {
                total += MeasureCharWidthPx(text[i], fontSize, isMono);
            }
            return total;
        }

        private static double MeasureCharWidthPx(char c, double fontSize, bool isMono)
        {
            double scale = fontSize / 11.0;
            if (c > 0x2E80 && c != '\u2026')
                return 11.2 * scale;
            if (isMono)
            {
                if (c == ' ')
                    return 6.2 * scale;
                return 6.65 * scale;
            }

            if (c == ' ') return 3.6 * scale;
            if (c == '.' || c == ',' || c == ':' || c == ';' || c == '|' || c == '!') return 3.2 * scale;
            if (c == '-' || c == '(' || c == ')' || c == '\u00B7') return 4.3 * scale;
            if (c == 'I' || c == 'i' || c == 'l' || c == 'j' || c == 't' || c == 'f' || c == 'r') return 4.2 * scale;
            if (c == 'M' || c == 'W' || c == 'm' || c == 'w' || c == '%') return 8.6 * scale;
            if (c >= 'A' && c <= 'Z') return 7.1 * scale;
            if (c >= '0' && c <= '9') return 6.5 * scale;
            if (c == '$' || c == '\u00A5' || c == '\u2026') return 6.9 * scale;
            return 6.2 * scale;
        }

        private static string GetBrandColorHex(string toolOrModel, bool isDark)
        {
            if (string.IsNullOrEmpty(toolOrModel))
                return isDark ? "#818CF8" : "#4F46E5";

            string s = toolOrModel.ToLowerInvariant();

            if (s.IndexOf("claude", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("anthropic", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#E5856B" : "#C25136";
            }

            if (s.IndexOf("codex", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("openai", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("gpt-", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("chatgpt", StringComparison.Ordinal) >= 0 ||
                s.StartsWith("o1", StringComparison.Ordinal) ||
                s.StartsWith("o3", StringComparison.Ordinal) ||
                s.StartsWith("o4", StringComparison.Ordinal))
            {
                return isDark ? "#34D399" : "#059669";
            }

            if (s.IndexOf("cursor", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#818CF8" : "#4F46E5";
            }

            if (s.IndexOf("gemini", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("antigravity", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("google", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#60A5FA" : "#2563EB";
            }

            if (s.IndexOf("deepseek", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("openclaw", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("cline", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("roo", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#38BDF8" : "#0284C7";
            }

            if (s.IndexOf("kimi", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("moonshot", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#A78BFA" : "#7C3AED";
            }

            if (s.IndexOf("qwen", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("tongyi", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#FB923C" : "#EA580C";
            }

            if (s.IndexOf("glm", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("zhipu", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("zcode", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("workbuddy", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#2DD4BF" : "#0D9488";
            }

            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= (uint)s[i];
                hash *= 16777619u;
            }
            string[] pal = isDark ? PaletteDark : PaletteLight;
            return pal[(int)(hash % (uint)pal.Length)];
        }
    }
}
