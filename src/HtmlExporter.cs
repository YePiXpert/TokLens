using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TokLens
{
    public static class HtmlExporter
    {
        public static void ExportHtmlReport(ScanResult res, double usdToCny, string outPath)
        {
            ExportHtmlReport(res != null ? res.Records : new List<RecordBucket>(), res, usdToCny, outPath);
        }

        public static void ExportHtmlReport(List<RecordBucket> rows, ScanResult localScan, double usdToCny, string outPath)
        {
            List<RecordBucket> safeRecords = rows ?? new List<RecordBucket>();
            List<object[]> serializedRows = new List<object[]>(safeRecords.Count);
            string latest = (localScan != null && !string.IsNullOrEmpty(localScan.ReferenceDate))
                ? localScan.ReferenceDate
                : "";

            for (int i = 0; i < safeRecords.Count; i++)
            {
                RecordBucket r = safeRecords[i];
                if (r == null) continue;
                string dateStr = string.IsNullOrEmpty(r.Date) ? "未知日期" : r.Date;
                if (dateStr != "未知日期" && string.CompareOrdinal(dateStr, latest) > 0)
                    latest = dateStr;

                serializedRows.Add(new object[]
                {
                    dateStr,
                    r.Tool ?? "未知工具",
                    r.Model ?? "unknown",
                    r.Turns,
                    r.FreshInput,
                    r.CacheRead,
                    r.CacheWrite,
                    r.Output,
                    Math.Round(r.CostUsd, 6),
                    Math.Round(r.BaselineUsd, 6),
                    r.RateLabel ?? "",
                    string.IsNullOrEmpty(r.MachineName) ? "本机" : r.MachineName,
                    string.IsNullOrEmpty(r.MachineType) ? "windows" : r.MachineType
                });
            }

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["version"] = AppConstants.VersionTag;
            payload["generatedAt"] = (localScan != null && !string.IsNullOrEmpty(localScan.GeneratedAt))
                ? localScan.GeneratedAt
                : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            payload["refDate"] = string.IsNullOrEmpty(latest)
                ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : latest;
            payload["pricingCount"] = localScan != null ? localScan.PricingModelCount : 0;
            payload["elapsed"] = localScan != null ? Math.Round(localScan.ElapsedSeconds, 2) : 0.0;
            payload["rate"] = usdToCny > 0 ? Math.Round(usdToCny, 4) : 7.25;
            payload["rows"] = serializedRows;

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            string jsonPayload = js.Serialize(payload)
                .Replace("</", "<\\/")
                .Replace("<!--", "<\\!--");

            string html = TEMPLATE
                .Replace("__TOKLENS_VER__", AppConstants.VersionTag)
                .Replace("__TOKLENS_JSON__", jsonPayload);

            File.WriteAllText(outPath, html, new UTF8Encoding(true));
        }

        private const string TEMPLATE = @"<!DOCTYPE html>
<html lang=""zh-CN"" data-theme=""dark"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>知微 · TokLens __TOKLENS_VER__ — 多设备与 VPS AI Token 实时监控与账单透镜</title>
<style>
:root, [data-theme=""dark""] {
  --bg: #09090b;
  --bg-title: #0c0c0e;
  --card: #16161a;
  --surface: #111114;
  --elevated: #1f1f25;
  --seg-active: #26262e;
  --hover: #1c1c22;
  --hover-strong: #27272f;
  --selected: #1d1e30;
  --border-subtle: #1e1e24;
  --border: #232329;
  --border-strong: #32323b;
  --border-focus: #6366f1;
  --fg: #f4f4f6;
  --sub: #a1a1aa;
  --muted: #71717a;
  --accent: #6366f1;
  --accent-text: #818cf8;
  --accent-bg: #191b2e;
  --accent-bd: #2e3259;
  --btn-primary: #5e6ad2;
  --btn-primary-hover: #6e7ae2;
  --success: #34d399;
  --success-bg: #10261e;
  --success-bd: #1b4334;
  --warning: #fbbf24;
  --warning-bg: #281e0d;
  --warning-bd: #4e3915;
  --bar-fill: #5e6ad2;
  --bar-hover: #818cf8;
  --grid-line: #232329;
  --shadow: 0 10px 28px rgba(0, 0, 0, 0.45);
}

[data-theme=""light""] {
  --bg: #f8f9fa;
  --bg-title: #f8f9fa;
  --card: #ffffff;
  --surface: #f1f2f4;
  --elevated: #ffffff;
  --seg-active: #ffffff;
  --hover: #f4f4f5;
  --hover-strong: #eaeaee;
  --selected: #eef2ff;
  --border-subtle: #f1f1f4;
  --border: #e4e4e7;
  --border-strong: #d4d4d8;
  --border-focus: #4f46e5;
  --fg: #09090b;
  --sub: #52525b;
  --muted: #71717a;
  --accent: #4f46e5;
  --accent-text: #4f46e5;
  --accent-bg: #eef2ff;
  --accent-bd: #c7d2fe;
  --btn-primary: #4f46e5;
  --btn-primary-hover: #4338ca;
  --success: #047857;
  --success-bg: #ecfdf5;
  --success-bd: #a7f3d0;
  --warning: #b45309;
  --warning-bg: #fffbeb;
  --warning-bd: #fde68a;
  --bar-fill: #4f46e5;
  --bar-hover: #6366f1;
  --grid-line: #e4e4e7;
  --shadow: 0 8px 24px rgba(9, 9, 11, 0.10);
}

* { box-sizing: border-box; margin: 0; padding: 0; }
body {
  font-family: ""Segoe UI Variable Text"", ""Segoe UI"", ""Microsoft YaHei UI"", system-ui, -apple-system, sans-serif;
  background: var(--bg);
  color: var(--fg);
  font-size: 13px;
  line-height: 1.5;
  padding: 20px 24px 36px;
  -webkit-font-smoothing: antialiased;
}
.mono {
  font-family: ""Cascadia Mono"", ""JetBrains Mono"", Consolas, ""Microsoft YaHei UI"", monospace;
  font-variant-numeric: tabular-nums lining-nums;
}
.wrap { max-width: 1380px; margin: 0 auto; }

/* Top Header Bar */
.hdr {
  display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center;
  gap: 12px; margin-bottom: 12px; padding: 12px 16px;
  background: var(--card); border: 1px solid var(--border); border-radius: 8px;
}
.brand { display: flex; align-items: center; gap: 12px; }
.logo {
  width: 32px; height: 32px; border-radius: 7px;
  background: var(--btn-primary); border: 1px solid rgba(255,255,255,0.14);
  display: flex; align-items: center; justify-content: center; color: #fff; flex-shrink: 0;
}
.title-row { display: flex; align-items: center; gap: 8px; }
.title { font-size: 15px; font-weight: 600; color: var(--fg); letter-spacing: -0.01em; }
.ver-badge {
  font-size: 11px; font-weight: 500; padding: 1px 6px; border-radius: 4px;
  background: var(--accent-bg); border: 1px solid var(--accent-bd); color: var(--accent-text);
}
.meta { font-size: 11px; color: var(--muted); margin-top: 1px; }

.ctrl { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.seg {
  display: inline-flex; align-items: center; background: var(--surface);
  border: 1px solid var(--border); border-radius: 8px; padding: 2px; gap: 2px; height: 30px;
}
.seg button {
  background: transparent; border: 1px solid transparent; color: var(--sub);
  padding: 0 10px; height: 24px; border-radius: 6px; font-size: 12px; font-weight: 500;
  cursor: pointer; font-family: inherit; transition: color 0.12s, background 0.12s;
}
.seg button:hover { color: var(--fg); }
.seg button.on {
  background: var(--seg-active); border-color: var(--border); color: var(--fg);
}

.rate-box {
  display: inline-flex; align-items: center; gap: 6px; background: var(--surface);
  border: 1px solid var(--border); border-radius: 8px; padding: 0 8px; height: 30px; font-size: 12px; color: var(--sub);
}
.rate-box input {
  width: 56px; height: 22px; background: var(--card); border: 1px solid var(--border-strong);
  border-radius: 4px; color: var(--fg); font-size: 12px; font-weight: 500;
  text-align: center; outline: none; padding: 0 4px;
}
.rate-box input:focus { border-color: var(--border-focus); }
.fx-pill {
  display: inline-flex; align-items: center; gap: 4px; height: 20px; padding: 0 6px;
  border-radius: 4px; font-size: 11px; font-weight: 500; cursor: pointer; user-select: none;
  background: var(--success-bg); border: 1px solid var(--success-bd); color: var(--success);
}
.fx-pill.warn { background: var(--warning-bg); border-color: var(--warning-bd); color: var(--warning); }
.fx-pill.neutral { background: var(--card); border-color: var(--border); color: var(--sub); }

.btn {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  height: 30px; padding: 0 12px; border-radius: 6px; font-size: 12px; font-weight: 500;
  background: var(--card); color: var(--fg); border: 1px solid var(--border-strong);
  cursor: pointer; font-family: inherit; transition: background 0.12s, border-color 0.12s;
}
.btn:hover { background: var(--hover-strong); }
.btn-icon { width: 30px; padding: 0; color: var(--sub); }
.btn-icon:hover { color: var(--fg); }

/* Device Filter Strip */
.nodes-bar {
  display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 12px; padding: 8px 12px;
  background: var(--card); border: 1px solid var(--border); border-radius: 8px; align-items: center;
}
.nodes-lbl { font-size: 11px; font-weight: 500; color: var(--muted); margin-right: 4px; }
.npill {
  display: inline-flex; align-items: center; gap: 6px; height: 28px; padding: 0 10px;
  border-radius: 6px; background: var(--surface); border: 1px solid var(--border);
  color: var(--sub); font-size: 12px; font-weight: 500; cursor: pointer; transition: border-color 0.12s, background 0.12s;
}
.npill:hover { border-color: var(--border-strong); color: var(--fg); }
.npill.on {
  background: var(--selected); border-color: var(--border-focus); color: var(--accent-text);
}
.ndot { width: 6px; height: 6px; border-radius: 50%; flex-shrink: 0; }

/* Bento KPI Grid */
.kpis { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; margin-bottom: 12px; }
@media (max-width: 1020px) { .kpis { grid-template-columns: repeat(2, 1fr); } }
@media (max-width: 560px) { .kpis { grid-template-columns: 1fr; } }
.kpi {
  background: var(--card); border: 1px solid var(--border); border-radius: 8px;
  padding: 14px 16px; display: flex; flex-direction: column; justify-content: space-between; min-height: 102px;
}
.kpi-top { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.kpi .lbl { font-size: 12px; font-weight: 500; color: var(--sub); }
.kpi-badge {
  font-size: 11px; font-weight: 500; padding: 1px 6px; border-radius: 4px;
  background: var(--surface); border: 1px solid var(--border); color: var(--sub);
}
.kpi-badge.good { background: var(--success-bg); border-color: var(--success-bd); color: var(--success); }
.kpi .val {
  font-size: 24px; font-weight: 600; color: var(--fg); margin: 6px 0 4px;
  letter-spacing: -0.02em; font-variant-numeric: tabular-nums lining-nums;
}
.kpi .sub { font-size: 11px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.mix-bar {
  display: flex; height: 4px; border-radius: 2px; overflow: hidden; background: var(--surface);
  margin: 4px 0 5px; gap: 1px;
}
.mix-seg { height: 100%; transition: width 0.2s; }

/* Middle Insights Grid */
.mid { display: grid; grid-template-columns: 1fr 1.45fr; gap: 12px; margin-bottom: 12px; }
@media (max-width: 980px) { .mid { grid-template-columns: 1fr; } }
.panel {
  background: var(--card); border: 1px solid var(--border); border-radius: 8px;
  padding: 14px 16px; display: flex; flex-direction: column;
}
.phdr { display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px; gap: 8px; flex-wrap: wrap; }
.ptitle { font-size: 13px; font-weight: 600; color: var(--fg); display: flex; align-items: center; gap: 8px; }
.clear-chip {
  display: none; align-items: center; gap: 4px; font-size: 11px; font-weight: 500;
  padding: 1px 7px; border-radius: 4px; background: var(--accent-bg);
  border: 1px solid var(--accent-bd); color: var(--accent-text); cursor: pointer;
}

.tlist {
  display: flex; flex-direction: column; gap: 6px; max-height: 248px; overflow-y: auto; padding-right: 4px;
}
.tlist::-webkit-scrollbar { width: 6px; height: 6px; }
.tlist::-webkit-scrollbar-thumb { background: var(--border-strong); border-radius: 3px; }
.trow {
  padding: 8px 10px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border);
  cursor: pointer; transition: border-color 0.12s, background 0.12s;
}
.trow:hover { background: var(--hover); border-color: var(--border-strong); }
.trow.sel { background: var(--selected); border-color: var(--border-focus); }
.trow-top { display: flex; justify-content: space-between; align-items: center; font-size: 12px; margin-bottom: 6px; gap: 8px; }
.trow-name { display: flex; align-items: center; gap: 7px; font-weight: 500; color: var(--fg); min-width: 0; }
.trow-turns { color: var(--muted); font-size: 11px; font-weight: 400; }
.trow-nums { font-size: 12px; color: var(--fg); font-weight: 500; white-space: nowrap; }
.trow-nums span { color: var(--sub); font-size: 11px; font-weight: 400; }
.tbar-bg { height: 4px; background: var(--hover-strong); border-radius: 2px; overflow: hidden; }
.tbar-fg { height: 100%; border-radius: 2px; }

/* Interactive SVG Trend Chart */
.chart-wrap { position: relative; flex: 1; min-height: 236px; display: flex; align-items: stretch; }
.chart-svg { width: 100%; height: 236px; display: block; overflow: visible; }
.chart-tip {
  position: absolute; pointer-events: none; display: none; z-index: 20;
  background: var(--elevated); border: 1px solid var(--border-strong); border-radius: 6px;
  padding: 8px 10px; font-size: 11px; color: var(--fg); box-shadow: var(--shadow);
  min-width: 185px; line-height: 1.6;
}
.chart-tip-date { font-weight: 600; font-size: 12px; margin-bottom: 3px; border-bottom: 1px solid var(--border); padding-bottom: 3px; }
.chart-tip-row { display: flex; justify-content: space-between; gap: 12px; color: var(--sub); }
.chart-tip-row b { color: var(--fg); font-weight: 500; }

/* Bottom Table Section */
.tbl-bar { display: flex; justify-content: space-between; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }
.search-box {
  display: inline-flex; align-items: center; gap: 6px; background: var(--surface);
  border: 1px solid var(--border-strong); border-radius: 6px; padding: 0 8px; height: 30px; min-width: 240px;
}
.search-box:focus-within { border-color: var(--border-focus); background: var(--card); }
.search-box input {
  border: none; background: transparent; color: var(--fg); font-size: 12px; outline: none; flex: 1; font-family: inherit;
}
.search-box input::placeholder { color: var(--muted); }
.search-clear {
  display: none; background: transparent; border: none; color: var(--muted); cursor: pointer; padding: 2px;
}
.search-clear:hover { color: var(--fg); }

.tbl-scroll { overflow-x: auto; }
.tbl-scroll::-webkit-scrollbar { height: 6px; }
.tbl-scroll::-webkit-scrollbar-thumb { background: var(--border-strong); border-radius: 3px; }
table { width: 100%; border-collapse: collapse; font-size: 12px; }
th {
  text-align: left; color: var(--muted); font-weight: 500; padding: 8px 12px;
  border-bottom: 1px solid var(--border); cursor: pointer; user-select: none; white-space: nowrap;
  transition: color 0.12s;
}
th:hover { color: var(--fg); }
th.sorted { color: var(--fg); }
th .sarrow { color: var(--accent-text); margin-left: 3px; font-size: 11px; }
td {
  padding: 8px 12px; border-bottom: 1px solid var(--border-subtle); color: var(--sub); white-space: nowrap;
}
td.pri { color: var(--fg); font-weight: 500; }
tr.clickable { cursor: pointer; }
tbody tr:hover td { background: var(--hover); }
tbody tr.row-sel td { background: var(--selected); }
.r { text-align: right; }

.badge {
  display: inline-flex; align-items: center; gap: 5px; height: 20px; padding: 0 7px;
  border-radius: 4px; font-size: 11px; font-weight: 500;
  background: var(--surface); border: 1px solid var(--border); color: var(--sub);
}
.share-cell { display: inline-flex; align-items: center; justify-content: flex-end; gap: 7px; min-width: 92px; }
.sbar-track { width: 40px; height: 5px; border-radius: 2.5px; background: var(--surface); overflow: hidden; flex-shrink: 0; }
.sbar-fill { height: 100%; border-radius: 2.5px; background: var(--bar-fill); }
.empty-row { text-align: center; padding: 32px 12px !important; color: var(--muted); }
svg.ico { width: 14px; height: 14px; stroke: currentColor; stroke-width: 1.5; fill: none; stroke-linecap: round; stroke-linejoin: round; flex-shrink: 0; }
</style>
</head>
<body>
<div class=""wrap"">
  <!-- Header -->
  <div class=""hdr"">
    <div class=""brand"">
      <div class=""logo"" aria-hidden=""true"">
        <svg class=""ico"" style=""width:17px;height:17px"" viewBox=""0 0 16 16"">
          <path d=""M 8,1.5 L 14,5 L 14,11 L 8,14.5 L 2,11 L 2,5 Z M 2,5 L 8,8.5 L 14,5 M 8,8.5 L 8,14.5""/>
        </svg>
      </div>
      <div>
        <div class=""title-row"">
          <span class=""title"">知微 · TokLens</span>
          <span class=""ver-badge mono"">__TOKLENS_VER__</span>
          <span style=""font-size:12px;color:var(--sub);font-weight:500"">多端汇总分析看板</span>
        </div>
        <div class=""meta"" id=""metaLine""></div>
      </div>
    </div>

    <div class=""ctrl"">
      <div class=""seg"" id=""rangeSeg"">
        <button data-r=""all"" class=""on"">全部</button>
        <button data-r=""30"">30天</button>
        <button data-r=""7"">7天</button>
        <button data-r=""3"">3天</button>
        <button data-r=""1"">今日</button>
      </div>
      <div class=""seg"" id=""curSeg"">
        <button data-c=""CNY"" class=""on"">¥ CNY</button>
        <button data-c=""USD"">$ USD</button>
      </div>
      <div class=""rate-box"">
        <span>汇率</span>
        <input id=""rateInp"" class=""mono"" type=""number"" step=""0.0001"" value=""7.25"">
        <span id=""fxBadge"" class=""fx-pill"" title=""当前使用桌面端导出的汇率（点击可联网拉取最新在线 USD/CNY 汇率）"">
          <span id=""fxText"">已同步</span>
          <svg class=""ico"" style=""width:11px;height:11px"" viewBox=""0 0 16 16""><path d=""M 13.5,8 A 5.5,5.5 0 1 1 12,4.2 M 13.8,2 L 13.8,5.2 L 10.6,5.2""/></svg>
        </span>
      </div>
      <button class=""btn"" id=""csvBtn"" title=""导出当前筛选条件下的账单明细 CSV"">
        <svg class=""ico"" viewBox=""0 0 16 16""><path d=""M 8,2 L 8,10.5 M 4.5,7.5 L 8,11 L 11.5,7.5 M 2.5,13.5 L 13.5,13.5""/></svg>
        <span>导出 CSV</span>
      </button>
      <button class=""btn btn-icon"" id=""themeBtn"" title=""切换深色 / 浅色外观主题""></button>
    </div>
  </div>

  <!-- Device Strip -->
  <div class=""nodes-bar"" id=""nodesBar""></div>

  <!-- Bento KPI Cards -->
  <div class=""kpis"">
    <div class=""kpi"">
      <div class=""kpi-top""><span class=""lbl"">API 等效总花费</span><span class=""kpi-badge mono"" id=""kScopeBadge"">全部设备</span></div>
      <div class=""val mono"" id=""kCost"">—</div>
      <div class=""sub mono"" id=""kCostSub"">—</div>
    </div>
    <div class=""kpi"">
      <div class=""kpi-top""><span class=""lbl"">累计总 Token</span><span class=""kpi-badge mono"" id=""kToolsBadge"">0 工具</span></div>
      <div class=""val mono"" id=""kTok"">—</div>
      <div>
        <div class=""mix-bar"" id=""kMixBar"" title=""Token 构成：新增输入 / 缓存读取 / 输出与缓存写入"">
          <div class=""mix-seg"" id=""mixFi"" style=""width:33%;background:var(--accent)""></div>
          <div class=""mix-seg"" id=""mixCr"" style=""width:34%;background:var(--success)""></div>
          <div class=""mix-seg"" id=""mixOut"" style=""width:33%;background:#38bdf8""></div>
        </div>
        <div class=""sub mono"" id=""kTokSub"">—</div>
      </div>
    </div>
    <div class=""kpi"">
      <div class=""kpi-top""><span class=""lbl"">Prompt Cache 命中率</span><span class=""kpi-badge"" id=""kHitBadge"">缓存复用</span></div>
      <div class=""val mono"" id=""kHit"">—</div>
      <div class=""sub mono"" id=""kHitSub"">—</div>
    </div>
    <div class=""kpi"">
      <div class=""kpi-top""><span class=""lbl"">Prompt Cache 累计省钱</span><span class=""kpi-badge good mono"" id=""kSaveBadge"">省 0.0%</span></div>
      <div class=""val mono"" id=""kSave"">—</div>
      <div class=""sub mono"" id=""kSaveSub"">—</div>
    </div>
  </div>

  <!-- Middle Ranking + SVG Trend -->
  <div class=""mid"">
    <div class=""panel"">
      <div class=""phdr"">
        <div class=""ptitle"">
          <span>工具消耗排行</span>
          <span id=""clearTool"" class=""clear-chip"">
            <span id=""clearToolTxt"">清除筛选</span>
            <svg class=""ico"" style=""width:11px;height:11px"" viewBox=""0 0 16 16""><path d=""M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5""/></svg>
          </span>
        </div>
        <div class=""seg"" id=""metricSeg"">
          <button data-m=""cost"" class=""on"">按花费</button>
          <button data-m=""tok"">按Token</button>
        </div>
      </div>
      <div class=""tlist"" id=""toolList""></div>
    </div>

    <div class=""panel"">
      <div class=""phdr"">
        <div class=""ptitle"" id=""trendTitle"">每日消耗趋势</div>
        <div style=""font-size:11px;color:var(--sub)"" class=""mono"" id=""hoverInfo"">移动光标查看单日详情</div>
      </div>
      <div class=""chart-wrap"" id=""chartWrap"">
        <svg class=""chart-svg"" id=""trendSvg""></svg>
        <div class=""chart-tip"" id=""chartTip""></div>
      </div>
    </div>
  </div>

  <!-- Bottom Interactive DataGrid -->
  <div class=""panel"">
    <div class=""tbl-bar"">
      <div class=""seg"" id=""tabSeg"">
        <button data-tab=""nodes"" class=""on"" id=""tabNodesBtn"">设备单列对比</button>
        <button data-tab=""models"" id=""tabModelsBtn"">模型账单明细</button>
        <button data-tab=""daily"" id=""tabDailyBtn"">每日账单流水</button>
      </div>
      <div class=""search-box"">
        <svg class=""ico"" style=""color:var(--muted)"" viewBox=""0 0 16 16""><path d=""M 7,2.5 A 4.5,4.5 0 1 1 6.99,2.5 M 10.3,10.3 L 13.8,13.8""/></svg>
        <input id=""searchInp"" type=""text"" placeholder=""搜索模型、工具、设备或日期…"">
        <button class=""search-clear"" id=""searchClearBtn"" title=""清空搜索"">
          <svg class=""ico"" style=""width:12px;height:12px"" viewBox=""0 0 16 16""><path d=""M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5""/></svg>
        </button>
      </div>
    </div>
    <div class=""tbl-scroll"">
      <table id=""mainTbl""></table>
    </div>
  </div>
</div>

<script>
const DATA = __TOKLENS_JSON__;
const PALETTE_DARK = ['#818cf8','#38bdf8','#34d399','#a78bfa','#f59e0b','#fb7185','#2dd4bf','#fb923c'];
const PALETTE_LIGHT = ['#4f46e5','#0284c7','#059669','#7c3aed','#d97706','#e11d48','#0d9488','#ea580c'];

const SVG_SUN = '<svg class=""ico"" viewBox=""0 0 16 16""><path d=""M 8,5 A 3,3 0 1 1 7.99,5 M 8,1.5 L 8,3 M 8,13 L 8,14.5 M 1.5,8 L 3,8 M 13,8 L 14.5,8 M 3.4,3.4 L 4.5,4.5 M 11.5,11.5 L 12.6,12.6 M 12.6,3.4 L 11.5,4.5 M 4.5,11.5 L 3.4,12.6""/></svg>';
const SVG_MOON = '<svg class=""ico"" viewBox=""0 0 16 16""><path d=""M 13.5,9.8 A 5.8,5.8 0 1 1 6.2,2.5 A 4.5,4.5 0 0 0 13.5,9.8 Z""/></svg>';
const SVG_GLOBE = '<svg class=""ico"" style=""width:13px;height:13px"" viewBox=""0 0 16 16""><path d=""M 2,2 L 7,2 L 7,9 L 2,9 Z M 9,2 L 14,2 L 14,6 L 9,6 Z M 9,8 L 14,8 L 14,14 L 9,14 Z M 2,11 L 7,11 L 7,14 L 2,14 Z""/></svg>';
const SVG_PC = '<svg class=""ico"" style=""width:13px;height:13px"" viewBox=""0 0 16 16""><path d=""M 1.5,3 L 14.5,3 L 14.5,10.5 L 1.5,10.5 Z M 5,13.5 L 11,13.5 M 8,10.5 L 8,13.5""/></svg>';
const SVG_VPS = '<svg class=""ico"" style=""width:13px;height:13px"" viewBox=""0 0 16 16""><path d=""M 2,3 L 14,3 L 14,13 L 2,13 Z M 4.5,6 L 7,8 L 4.5,10 M 8,10 L 11,10""/></svg>';

let state = {
  theme: 'dark',
  range: 'all',
  cur: 'CNY',
  rate: Number(DATA.rate) > 0 ? Number(DATA.rate) : 7.25,
  metric: 'cost',
  node: null,
  tool: null,
  tab: 'nodes',
  query: '',
  sortCol: 'cost',
  sortDir: -1
};

let lastExportableRows = [];
let lastDailyAsc = [];

function esc(s) {
  return String(s == null ? '' : s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/""/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function pad2(n) { return n < 10 ? '0' + n : String(n); }

// 修复东八区 toISOString() 倒退 1 天的统计 Bug：严格采用本地日历年月日拼接
function cutoffDate(days) {
  const parts = String(DATA.refDate || '').split('-').map(Number);
  const d = (parts.length === 3 && parts[0] > 2000)
    ? new Date(parts[0], parts[1] - 1, parts[2])
    : new Date();
  d.setDate(d.getDate() - (days - 1));
  return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
}

function fmtTok(n) {
  const v = Number(n) || 0;
  if (v >= 1e9) return (v / 1e9).toFixed(2) + 'B';
  if (v >= 1e6) return (v / 1e6).toFixed(2) + 'M';
  if (v >= 1e3) return (v / 1e3).toFixed(1) + 'K';
  return String(Math.round(v));
}

function fmtMoney(usd, cur) {
  const c = cur || state.cur;
  const u = Number(usd) || 0;
  const v = c === 'CNY' ? u * state.rate : u;
  const s = c === 'CNY' ? '¥' : '$';
  if (v >= 100) return s + v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  if (v >= 1) return s + v.toFixed(2);
  if (v === 0) return s + '0.00';
  return s + v.toFixed(4);
}

function providerColor(name) {
  const s = String(name || '').toLowerCase();
  const dark = state.theme === 'dark';
  if (s.includes('claude') || s.includes('anthropic')) return dark ? '#e5856b' : '#c25136';
  if (s.includes('codex') || s.includes('openai') || s.includes('gpt-')) return dark ? '#34d399' : '#059669';
  if (s.includes('cursor')) return dark ? '#818cf8' : '#4f46e5';
  if (s.includes('gemini') || s.includes('antigravity') || s.includes('google')) return dark ? '#60a5fa' : '#2563eb';
  if (s.includes('deepseek') || s.includes('openclaw')) return dark ? '#38bdf8' : '#0284c7';
  if (s.includes('kimi') || s.includes('moonshot')) return dark ? '#a78bfa' : '#7c3aed';
  if (s.includes('qwen')) return dark ? '#fb923c' : '#ea580c';
  if (s.includes('glm') || s.includes('zcode') || s.includes('workbuddy')) return dark ? '#2dd4bf' : '#0d9488';
  let h = 2166136261 >>> 0;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619) >>> 0;
  }
  const pal = dark ? PALETTE_DARK : PALETTE_LIGHT;
  return pal[h % pal.length];
}

function applyTheme(t) {
  state.theme = t === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', state.theme);
  document.getElementById('themeBtn').innerHTML = state.theme === 'dark' ? SVG_SUN : SVG_MOON;
  try { localStorage.setItem('toklens_report_theme', state.theme); } catch (_) {}
}

function compareVals(a, b, col, dir) {
  const av = a[col];
  const bv = b[col];
  if (typeof av === 'string' || typeof bv === 'string') {
    return String(av || '').localeCompare(String(bv || ''), 'zh-CN') * dir;
  }
  return ((Number(av) || 0) - (Number(bv) || 0)) * dir;
}

function thHtml(label, col, right) {
  const isSorted = state.sortCol === col;
  const cls = (right ? 'r ' : '') + (isSorted ? 'sorted' : '');
  const arrow = isSorted ? `<span class=""sarrow"">${state.sortDir > 0 ? '↑' : '↓'}</span>` : '';
  return `<th class=""${cls.trim()}"" data-sort=""${esc(col)}"">${esc(label)}${arrow}</th>`;
}

function shareCellHtml(pct, ratio) {
  const clamped = Math.max(0, Math.min(1, Number(ratio) || 0));
  const w = clamped > 0 ? Math.max(4, Math.round(clamped * 100)) : 0;
  return `<div class=""share-cell mono""><div class=""sbar-track""><div class=""sbar-fill"" style=""width:${w}%""></div></div><span>${pct.toFixed(1)}%</span></div>`;
}

function render() {
  const cut = state.range === 'all' ? '' : cutoffDate(Number(state.range));
  const allRows = Array.isArray(DATA.rows) ? DATA.rows : [];
  const timeRows = allRows.filter(r => !cut || (r[0] !== '未知日期' && r[0] >= cut));

  // 汇总全部设备槽位，且金额联动当前选中的 state.tool！
  const allNodeSlots = new Map();
  for (const r of timeRows) {
    const mn = r[11] || '本机';
    const mt = String(r[12] || 'windows').toLowerCase();
    if (!allNodeSlots.has(mn)) {
      allNodeSlots.set(mn, { name: mn, type: mt, turns: 0, fi: 0, cr: 0, cw: 0, out: 0, tok: 0, cost: 0, tools: new Set() });
    }
  }
  let allNodeCostInTool = 0;
  for (const r of timeRows) {
    if (state.tool && r[1] !== state.tool) continue;
    const mn = r[11] || '本机';
    const o = allNodeSlots.get(mn);
    o.turns += r[3]; o.fi += r[4]; o.cr += r[5]; o.cw += r[6]; o.out += r[7];
    o.tok += (r[4] + r[5] + r[6] + r[7]); o.cost += r[8]; o.tools.add(r[1]);
    allNodeCostInTool += r[8];
  }

  if (state.node && !allNodeSlots.has(state.node)) {
    state.node = null;
  }

  const nodePillArr = Array.from(allNodeSlots.values()).sort((a, b) => b.cost - a.cost);
  const nb = document.getElementById('nodesBar');
  let nbHtml = `<span class=""nodes-lbl"">设备视角</span>`;
  nbHtml += `<button type=""button"" class=""npill${!state.node ? ' on' : ''}"" data-node="""">${SVG_GLOBE}<span>全部设备 (${nodePillArr.length}台)</span><span class=""mono"">${esc(fmtMoney(allNodeCostInTool))}</span></button>`;
  nodePillArr.forEach(n => {
    const isVps = n.type === 'vps' || n.type === 'linux';
    const ico = isVps ? SVG_VPS : SVG_PC;
    const dotCol = isVps ? (state.theme === 'dark' ? '#818cf8' : '#4f46e5') : (state.theme === 'dark' ? '#34d399' : '#059669');
    const onCls = state.node === n.name ? ' on' : '';
    nbHtml += `<button type=""button"" class=""npill${onCls}"" data-node=""${esc(n.name)}""><span class=""ndot"" style=""background:${dotCol}""></span>${ico}<span>${esc(n.name)}</span><span class=""mono"">${esc(fmtMoney(n.cost))}</span></button>`;
  });
  nb.innerHTML = nbHtml;

  // 设备筛选 -> 工具排行基数
  const deviceScopedRows = state.node ? timeRows.filter(r => (r[11] || '本机') === state.node) : timeRows;

  // 工具排行计算
  const tmap = new Map();
  for (const r of deviceScopedRows) {
    let o = tmap.get(r[1]);
    if (!o) { o = { tool: r[1], turns: 0, tok: 0, cost: 0 }; tmap.set(r[1], o); }
    o.turns += r[3]; o.tok += (r[4] + r[5] + r[6] + r[7]); o.cost += r[8];
  }
  if (state.tool && !tmap.has(state.tool)) {
    state.tool = null;
  }

  const tarr = Array.from(tmap.values()).sort((a, b) => state.metric === 'cost' ? b.cost - a.cost : b.tok - a.tok);
  const sumM = tarr.reduce((s, x) => s + (state.metric === 'cost' ? x.cost : x.tok), 0) || 1;
  const maxM = tarr.length ? ((state.metric === 'cost' ? tarr[0].cost : tarr[0].tok) || 1) : 1;

  const clearChip = document.getElementById('clearTool');
  if (state.tool) {
    clearChip.style.display = 'inline-flex';
    document.getElementById('clearToolTxt').textContent = state.tool;
  } else {
    clearChip.style.display = 'none';
  }

  const tl = document.getElementById('toolList');
  if (tarr.length === 0) {
    tl.innerHTML = `<div style=""text-align:center;padding:36px 8px;color:var(--muted);font-size:12px"">当前时间与设备范围内暂无记录</div>`;
  } else {
    let tlHtml = '';
    tarr.forEach(t => {
      const val = state.metric === 'cost' ? t.cost : t.tok;
      const pct = (val * 100 / sumM).toFixed(1);
      const w = Math.max(2, val * 100 / maxM);
      const col = providerColor(t.tool);
      const selCls = state.tool === t.tool ? ' sel' : '';
      tlHtml += `<div class=""trow${selCls}"" data-tool=""${esc(t.tool)}"">` +
        `<div class=""trow-top"">` +
          `<div class=""trow-name""><span class=""ndot"" style=""background:${col}""></span><span>${esc(t.tool)}</span><span class=""trow-turns mono"">${t.turns.toLocaleString()}次</span></div>` +
          `<div class=""trow-nums mono"">${esc(fmtMoney(t.cost))} <span>· ${esc(fmtTok(t.tok))} (${pct}%)</span></div>` +
        `</div>` +
        `<div class=""tbar-bg""><div class=""tbar-fg"" style=""width:${w.toFixed(1)}%;background:${col}""></div></div>` +
      `</div>`;
    });
    tl.innerHTML = tlHtml;
  }

  // 同时应用设备 + 工具筛选后的核心数据
  const coreRows = state.tool ? deviceScopedRows.filter(r => r[1] === state.tool) : deviceScopedRows;

  // 搜索词过滤（支持模型名、工具名、设备名、日期即时匹配）
  const q = state.query.trim().toLowerCase();
  const activeRows = q
    ? coreRows.filter(r =>
        String(r[2] || '').toLowerCase().includes(q) ||
        String(r[1] || '').toLowerCase().includes(q) ||
        String(r[11] || '本机').toLowerCase().includes(q) ||
        String(r[0] || '').toLowerCase().includes(q))
    : coreRows;
  lastExportableRows = activeRows;

  // Bento KPI 统计
  let turns = 0, fi = 0, cr = 0, cw = 0, out = 0, cost = 0, base = 0;
  const tset = new Set();
  for (const r of activeRows) {
    tset.add(r[1]);
    turns += r[3]; fi += r[4]; cr += r[5]; cw += r[6]; out += r[7];
    cost += r[8]; base += r[9];
  }
  const tot = fi + cr + cw + out;
  const promptTot = fi + cr + cw;
  const hit = promptTot > 0 ? (cr * 100 / promptTot) : 0;
  const saved = Math.max(0, base - cost);
  const savePct = base > 0 ? (saved * 100 / base) : 0;

  document.getElementById('kScopeBadge').textContent = state.node ? state.node : `全部 ${nodePillArr.length} 台设备`;
  document.getElementById('kToolsBadge').textContent = `${tset.size} 个工具`;
  document.getElementById('kCost').textContent = fmtMoney(cost);
  document.getElementById('kCostSub').textContent = `折合 ${fmtMoney(cost, state.cur === 'CNY' ? 'USD' : 'CNY')} · ${turns.toLocaleString()} 次调用`;

  document.getElementById('kTok').textContent = fmtTok(tot);
  document.getElementById('kTokSub').textContent = `输入 ${fmtTok(fi)} · 缓存读 ${fmtTok(cr)} · 输出 ${fmtTok(out)}`;
  if (tot > 0) {
    document.getElementById('mixFi').style.width = Math.max(2, (fi * 100 / tot)).toFixed(1) + '%';
    document.getElementById('mixCr').style.width = Math.max(2, (cr * 100 / tot)).toFixed(1) + '%';
    document.getElementById('mixOut').style.width = Math.max(2, ((out + cw) * 100 / tot)).toFixed(1) + '%';
  } else {
    document.getElementById('mixFi').style.width = '0%';
    document.getElementById('mixCr').style.width = '0%';
    document.getElementById('mixOut').style.width = '0%';
  }

  document.getElementById('kHit').textContent = hit.toFixed(1) + '%';
  document.getElementById('kHitBadge').textContent = hit >= 60 ? '高效命中' : (hit > 0 ? '已启用' : '无缓存');
  document.getElementById('kHitBadge').className = 'kpi-badge' + (hit >= 60 ? ' good' : '');
  document.getElementById('kHitSub').textContent = `缓存读 ${fmtTok(cr)} · 缓存写 ${fmtTok(cw)}`;

  document.getElementById('kSave').textContent = fmtMoney(saved);
  document.getElementById('kSaveBadge').textContent = `省 ${savePct.toFixed(1)}%`;
  document.getElementById('kSaveSub').textContent = `无缓存原价 ${fmtMoney(base)}`;

  // 聚合三张数据表（均严格联动工具筛选与搜索词！）
  // 1) 设备单列对比数据（当选中 state.tool 或输入搜索词时同步精确过滤）
  const nodeRowsForTbl = q
    ? (state.tool ? timeRows.filter(r => r[1] === state.tool) : timeRows).filter(r =>
        String(r[2] || '').toLowerCase().includes(q) ||
        String(r[1] || '').toLowerCase().includes(q) ||
        String(r[11] || '本机').toLowerCase().includes(q) ||
        String(r[0] || '').toLowerCase().includes(q))
    : (state.tool ? timeRows.filter(r => r[1] === state.tool) : timeRows);

  const nodeTblMap = new Map();
  let sumNodeCost = 0;
  let maxNodeCost = 0;
  for (const r of nodeRowsForTbl) {
    const mn = r[11] || '本机';
    const mt = String(r[12] || 'windows').toLowerCase();
    let o = nodeTblMap.get(mn);
    if (!o) {
      o = { name: mn, type: (mt === 'vps' || mt === 'linux') ? 'Linux VPS' : 'Windows', isVps: (mt === 'vps' || mt === 'linux'), turns: 0, fi: 0, cr: 0, cw: 0, out: 0, tok: 0, cost: 0, tools: new Set() };
      nodeTblMap.set(mn, o);
    }
    o.turns += r[3]; o.fi += r[4]; o.cr += r[5]; o.cw += r[6]; o.out += r[7];
    o.tok += (r[4] + r[5] + r[6] + r[7]); o.cost += r[8]; o.tools.add(r[1]);
    sumNodeCost += r[8];
  }
  const nodeTblArr = Array.from(nodeTblMap.values()).map(n => {
    n.toolCount = n.tools.size;
    n.share = sumNodeCost > 0 ? (n.cost * 100 / sumNodeCost) : 0;
    if (n.cost > maxNodeCost) maxNodeCost = n.cost;
    return n;
  });

  // 2) 模型账单明细数据
  const mmap = new Map();
  let maxModelCost = 0;
  for (const r of activeRows) {
    const k = r[1] + '||' + r[2];
    let m = mmap.get(k);
    if (!m) {
      m = { tool: r[1], model: r[2], turns: 0, fi: 0, cr: 0, cw: 0, out: 0, tok: 0, cost: 0, rate: r[10] || '' };
      mmap.set(k, m);
    }
    m.turns += r[3]; m.fi += r[4]; m.cr += r[5]; m.cw += r[6]; m.out += r[7];
    m.tok += (r[4] + r[5] + r[6] + r[7]); m.cost += r[8];
  }
  const modelArr = Array.from(mmap.values()).map(m => {
    m.share = cost > 0 ? (m.cost * 100 / cost) : 0;
    if (m.cost > maxModelCost) maxModelCost = m.cost;
    return m;
  });

  // 3) 每日账单流水数据（含缓存写入 cw）
  const dmap = new Map();
  let maxDayCost = 0;
  for (const r of activeRows) {
    let d = dmap.get(r[0]);
    if (!d) {
      d = { date: r[0], turns: 0, fi: 0, cr: 0, cw: 0, out: 0, tok: 0, cost: 0 };
      dmap.set(r[0], d);
    }
    d.turns += r[3]; d.fi += r[4]; d.cr += r[5]; d.cw += r[6]; d.out += r[7];
    d.tok += (r[4] + r[5] + r[6] + r[7]); d.cost += r[8];
  }
  const dailyAsc = Array.from(dmap.values()).sort((a, b) => a.date.localeCompare(b.date));
  dailyAsc.forEach(d => {
    d.share = cost > 0 ? (d.cost * 100 / cost) : 0;
    if (d.cost > maxDayCost) maxDayCost = d.cost;
  });
  lastDailyAsc = dailyAsc;

  // 更新 Tab 按钮实时计数
  document.getElementById('tabNodesBtn').textContent = `设备单列对比 (${nodeTblArr.length})`;
  document.getElementById('tabModelsBtn').textContent = `模型账单明细 (${modelArr.length})`;
  document.getElementById('tabDailyBtn').textContent = `每日账单流水 (${dailyAsc.length})`;

  renderTrendSvg(dailyAsc);
  renderTable(nodeTblArr, maxNodeCost, modelArr, maxModelCost, dailyAsc.slice(), maxDayCost);
}

function renderTrendSvg(dailyAsc) {
  const svg = document.getElementById('trendSvg');
  const tip = document.getElementById('chartTip');
  const info = document.getElementById('hoverInfo');
  tip.style.display = 'none';

  const w = Math.max(320, svg.clientWidth || 560);
  const h = 236;
  svg.setAttribute('viewBox', `0 0 ${w} ${h}`);

  if (!dailyAsc || dailyAsc.length === 0) {
    info.textContent = '当前筛选范围内暂无用量记录';
    svg.innerHTML = `<text x=""${w / 2}"" y=""${h / 2}"" text-anchor=""middle"" fill=""var(--muted)"" font-size=""12"">暂无每日趋势数据</text>`;
    return;
  }

  info.textContent = `共 ${dailyAsc.length} 个活跃日 · 悬停查看单日明细`;

  const padL = 58, padR = 12, padT = 14, padB = 26;
  const plotW = Math.max(40, w - padL - padR);
  const plotH = Math.max(40, h - padT - padB);

  const vals = dailyAsc.map(d => state.metric === 'cost' ? d.cost : d.tok);
  const maxVal = Math.max(...vals, 0) || 1;

  let out = '';
  // 水平参考线与 Y 轴标签
  [0, 0.5, 1].forEach(ratio => {
    const y = (padT + plotH * (1 - ratio)).toFixed(1);
    const dash = ratio === 0 ? '' : ' stroke-dasharray=""3,3""';
    out += `<line x1=""${padL}"" y1=""${y}"" x2=""${padL + plotW}"" y2=""${y}"" stroke=""var(--grid-line)"" stroke-width=""1""${dash}/>`;
    const refVal = maxVal * ratio;
    const lbl = state.metric === 'cost'
      ? (ratio === 0 ? (state.cur === 'CNY' ? '¥0' : '$0') : fmtMoney(refVal))
      : fmtTok(refVal);
    out += `<text x=""${padL - 8}"" y=""${Number(y) + 4}"" text-anchor=""end"" fill=""var(--muted)"" font-size=""11"" class=""mono"">${esc(lbl)}</text>`;
  });

  const n = dailyAsc.length;
  const slotW = plotW / n;
  const gap = n > 45 ? 1.5 : (n > 20 ? 3 : 5);
  const barW = Math.max(2, Math.min(30, slotW - gap));

  out += `<line id=""svgCrosshair"" x1=""0"" y1=""${padT}"" x2=""0"" y2=""${padT + plotH}"" stroke=""var(--border-focus)"" stroke-width=""1"" stroke-dasharray=""3,3"" visibility=""hidden""/>`;

  dailyAsc.forEach((d, i) => {
    const v = state.metric === 'cost' ? d.cost : d.tok;
    const bh = Math.max(2.5, (v / maxVal) * plotH);
    const cx = padL + slotW * i + slotW * 0.5;
    const bx = cx - barW * 0.5;
    const by = padT + plotH - bh;
    const rx = Math.min(3, barW * 0.35);

    out += `<rect class=""svg-bar"" data-idx=""${i}"" x=""${bx.toFixed(1)}"" y=""${by.toFixed(1)}"" width=""${barW.toFixed(1)}"" height=""${bh.toFixed(1)}"" rx=""${rx.toFixed(1)}"" fill=""var(--bar-fill)""/>`;
    out += `<rect class=""svg-hit"" data-idx=""${i}"" x=""${(padL + slotW * i).toFixed(1)}"" y=""${padT}"" width=""${Math.max(slotW, barW).toFixed(1)}"" height=""${plotH}"" fill=""transparent"" style=""cursor:pointer""/>`;
  });

  const ticks = n === 1 ? [0] : (n === 2 ? [0, 1] : [0, Math.floor(n / 2), n - 1]);
  ticks.forEach((idx, ti) => {
    let tx = padL + slotW * idx + slotW * 0.5;
    const anchor = ti === 0 && n > 1 ? 'start' : (ti === ticks.length - 1 && n > 1 ? 'end' : 'middle');
    if (anchor === 'start') tx = padL;
    if (anchor === 'end') tx = padL + plotW;
    out += `<text x=""${tx.toFixed(1)}"" y=""${padT + plotH + 17}"" text-anchor=""${anchor}"" fill=""var(--muted)"" font-size=""11"" class=""mono"">${esc(dailyAsc[idx].date)}</text>`;
  });

  svg.innerHTML = out;

  const cross = document.getElementById('svgCrosshair');
  const bars = svg.querySelectorAll('.svg-bar');
  svg.querySelectorAll('.svg-hit').forEach(hit => {
    hit.addEventListener('mouseenter', e => {
      const idx = Number(hit.dataset.idx);
      const d = dailyAsc[idx];
      if (!d) return;
      bars.forEach((b, bi) => b.setAttribute('fill', bi === idx ? 'var(--bar-hover)' : 'var(--bar-fill)'));
      const cx = padL + slotW * idx + slotW * 0.5;
      cross.setAttribute('x1', cx.toFixed(1));
      cross.setAttribute('x2', cx.toFixed(1));
      cross.setAttribute('visibility', 'visible');

      const dayPrompt = d.fi + d.cr + d.cw;
      const dayHit = dayPrompt > 0 ? (d.cr * 100 / dayPrompt).toFixed(1) + '%' : '0.0%';
      info.textContent = `${d.date} · ${fmtMoney(d.cost)} · ${fmtTok(d.tok)} Tokens · ${d.turns} 次调用`;
      tip.innerHTML =
        `<div class=""chart-tip-date mono"">${esc(d.date)}</div>` +
        `<div class=""chart-tip-row""><span>等效花费</span><b class=""mono"">${esc(fmtMoney(d.cost))} (${esc(fmtMoney(d.cost, state.cur === 'CNY' ? 'USD' : 'CNY'))})</b></div>` +
        `<div class=""chart-tip-row""><span>总 Token / 调用</span><b class=""mono"">${esc(fmtTok(d.tok))} / ${d.turns}次</b></div>` +
        `<div class=""chart-tip-row""><span>新增输入 / 输出</span><b class=""mono"">${esc(fmtTok(d.fi))} / ${esc(fmtTok(d.out))}</b></div>` +
        `<div class=""chart-tip-row""><span>缓存读 (命中率)</span><b class=""mono"">${esc(fmtTok(d.cr))} (${dayHit})</b></div>`;
      tip.style.display = 'block';
    });

    hit.addEventListener('mousemove', e => {
      const wrapRect = document.getElementById('chartWrap').getBoundingClientRect();
      let left = e.clientX - wrapRect.left + 14;
      let top = e.clientY - wrapRect.top - 10;
      const tipW = tip.offsetWidth || 200;
      if (left + tipW > wrapRect.width - 6) left = e.clientX - wrapRect.left - tipW - 14;
      if (top < 4) top = 4;
      tip.style.left = Math.max(4, left) + 'px';
      tip.style.top = top + 'px';
    });

    hit.addEventListener('mouseleave', () => {
      bars.forEach(b => b.setAttribute('fill', 'var(--bar-fill)'));
      cross.setAttribute('visibility', 'hidden');
      tip.style.display = 'none';
      info.textContent = `共 ${dailyAsc.length} 个活跃日 · 悬停查看单日明细`;
    });
  });
}

function renderTable(nodeArr, maxNodeCost, modelArr, maxModelCost, dailyArr, maxDayCost) {
  const tbl = document.getElementById('mainTbl');
  let html = '';

  if (state.tab === 'nodes') {
    const validCols = ['name','type','toolCount','turns','fi','cr','cw','out','tok','cost','share'];
    const col = validCols.includes(state.sortCol) ? state.sortCol : 'cost';
    nodeArr.sort((a, b) => compareVals(a, b, col, state.sortDir));

    html += `<thead><tr>` +
      thHtml('设备名称', 'name', false) +
      thHtml('系统类型', 'type', false) +
      thHtml('活跃工具', 'toolCount', false) +
      thHtml('调用数', 'turns', true) +
      thHtml('新增输入', 'fi', true) +
      thHtml('缓存读取', 'cr', true) +
      thHtml('缓存写入', 'cw', true) +
      thHtml('输出 Token', 'out', true) +
      thHtml('总 Token', 'tok', true) +
      thHtml('等效美元 ($)', 'cost', true) +
      thHtml('等效人民币 (¥)', 'cost', true) +
      thHtml('占比', 'share', true) +
      `</tr></thead><tbody>`;

    if (nodeArr.length === 0) {
      html += `<tr><td colspan=""12"" class=""empty-row"">未找到匹配的设备统计记录</td></tr>`;
    } else {
      for (const n of nodeArr) {
        const selCls = state.node === n.name ? ' row-sel' : '';
        const ico = n.isVps ? SVG_VPS : SVG_PC;
        const ratio = maxNodeCost > 0 ? (n.cost / maxNodeCost) : 0;
        html += `<tr class=""clickable${selCls}"" data-row-node=""${esc(n.name)}"" title=""点击联动筛选该设备"">` +
          `<td class=""pri""><span style=""display:inline-flex;align-items:center;gap:6px"">${ico}<span>${esc(n.name)}</span></span></td>` +
          `<td><span class=""badge"">${esc(n.type)}</span></td>` +
          `<td><span class=""badge mono"">${n.toolCount} 个工具</span></td>` +
          `<td class=""r mono"">${n.turns.toLocaleString()}</td>` +
          `<td class=""r mono"">${esc(fmtTok(n.fi))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(n.cr))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(n.cw))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(n.out))}</td>` +
          `<td class=""r mono pri"">${esc(fmtTok(n.tok))}</td>` +
          `<td class=""r mono"">${esc(fmtMoney(n.cost, 'USD'))}</td>` +
          `<td class=""r mono pri"">${esc(fmtMoney(n.cost, 'CNY'))}</td>` +
          `<td class=""r"">${shareCellHtml(n.share, ratio)}</td>` +
        `</tr>`;
      }
    }
    html += `</tbody>`;
  } else if (state.tab === 'models') {
    const validCols = ['model','tool','turns','fi','cr','cw','out','tok','rate','cost','share'];
    const col = validCols.includes(state.sortCol) ? state.sortCol : 'cost';
    modelArr.sort((a, b) => compareVals(a, b, col, state.sortDir));

    html += `<thead><tr>` +
      thHtml('模型名称', 'model', false) +
      thHtml('所属工具', 'tool', false) +
      thHtml('调用数', 'turns', true) +
      thHtml('纯新增输入', 'fi', true) +
      thHtml('缓存读取', 'cr', true) +
      thHtml('缓存写入', 'cw', true) +
      thHtml('输出 Token', 'out', true) +
      thHtml('总 Token', 'tok', true) +
      thHtml('单价 ($/M)', 'rate', true) +
      thHtml('等效花费', 'cost', true) +
      thHtml('占比', 'share', true) +
      `</tr></thead><tbody>`;

    if (modelArr.length === 0) {
      html += `<tr><td colspan=""11"" class=""empty-row"">未找到匹配的模型账单记录</td></tr>`;
    } else {
      for (const m of modelArr) {
        const colHex = providerColor(m.tool);
        const ratio = maxModelCost > 0 ? (m.cost / maxModelCost) : 0;
        html += `<tr>` +
          `<td class=""pri mono"">${esc(m.model)}</td>` +
          `<td><span class=""badge""><span class=""ndot"" style=""background:${colHex}""></span>${esc(m.tool)}</span></td>` +
          `<td class=""r mono"">${m.turns.toLocaleString()}</td>` +
          `<td class=""r mono"">${esc(fmtTok(m.fi))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(m.cr))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(m.cw))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(m.out))}</td>` +
          `<td class=""r mono pri"">${esc(fmtTok(m.tok))}</td>` +
          `<td class=""r mono"">${esc(m.rate)}</td>` +
          `<td class=""r mono pri"">${esc(fmtMoney(m.cost))}</td>` +
          `<td class=""r"">${shareCellHtml(m.share, ratio)}</td>` +
        `</tr>`;
      }
    }
    html += `</tbody>`;
  } else {
    const validCols = ['date','turns','fi','cr','cw','out','tok','cost','share'];
    const col = validCols.includes(state.sortCol) ? state.sortCol : 'date';
    dailyArr.sort((a, b) => compareVals(a, b, col, state.sortDir));

    html += `<thead><tr>` +
      thHtml('日期', 'date', false) +
      thHtml('调用次数', 'turns', true) +
      thHtml('纯新增输入', 'fi', true) +
      thHtml('缓存读取', 'cr', true) +
      thHtml('缓存写入', 'cw', true) +
      thHtml('输出 Token', 'out', true) +
      thHtml('总 Token', 'tok', true) +
      thHtml('等效美元 ($)', 'cost', true) +
      thHtml('等效人民币 (¥)', 'cost', true) +
      thHtml('单日占比', 'share', true) +
      `</tr></thead><tbody>`;

    if (dailyArr.length === 0) {
      html += `<tr><td colspan=""10"" class=""empty-row"">未找到匹配的每日流水记录</td></tr>`;
    } else {
      for (const d of dailyArr) {
        const ratio = maxDayCost > 0 ? (d.cost / maxDayCost) : 0;
        html += `<tr>` +
          `<td class=""pri mono"">${esc(d.date)}</td>` +
          `<td class=""r mono"">${d.turns.toLocaleString()}</td>` +
          `<td class=""r mono"">${esc(fmtTok(d.fi))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(d.cr))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(d.cw))}</td>` +
          `<td class=""r mono"">${esc(fmtTok(d.out))}</td>` +
          `<td class=""r mono pri"">${esc(fmtTok(d.tok))}</td>` +
          `<td class=""r mono"">${esc(fmtMoney(d.cost, 'USD'))}</td>` +
          `<td class=""r mono pri"">${esc(fmtMoney(d.cost, 'CNY'))}</td>` +
          `<td class=""r"">${shareCellHtml(d.share, ratio)}</td>` +
        `</tr>`;
      }
    }
    html += `</tbody>`;
  }

  tbl.innerHTML = html;
}

// 事件委托与交互绑定
document.getElementById('metaLine').textContent =
  `匹配 ${Number(DATA.pricingCount) || 0} 个模型单价 · 基准日 ${DATA.refDate || '-'} · 生成于 ${DATA.generatedAt || '-'}`;
document.getElementById('rateInp').value = state.rate;

try {
  const savedTheme = localStorage.getItem('toklens_report_theme');
  applyTheme(savedTheme === 'light' ? 'light' : 'dark');
} catch (_) {
  applyTheme('dark');
}

document.getElementById('themeBtn').onclick = () => {
  applyTheme(state.theme === 'dark' ? 'light' : 'dark');
  render();
};

document.querySelectorAll('#rangeSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#rangeSeg button').forEach(x => x.classList.remove('on'));
  b.classList.add('on');
  state.range = b.dataset.r;
  render();
});

document.querySelectorAll('#curSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#curSeg button').forEach(x => x.classList.remove('on'));
  b.classList.add('on');
  state.cur = b.dataset.c;
  render();
});

document.querySelectorAll('#metricSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#metricSeg button').forEach(x => x.classList.remove('on'));
  b.classList.add('on');
  state.metric = b.dataset.m;
  render();
});

document.querySelectorAll('#tabSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#tabSeg button').forEach(x => x.classList.remove('on'));
  b.classList.add('on');
  state.tab = b.dataset.tab;
  state.sortCol = state.tab === 'daily' ? 'date' : 'cost';
  state.sortDir = -1;
  render();
});

document.getElementById('nodesBar').addEventListener('click', e => {
  const pill = e.target.closest('.npill');
  if (!pill) return;
  const n = pill.dataset.node || '';
  state.node = n ? (state.node === n ? null : n) : null;
  render();
});

document.getElementById('toolList').addEventListener('click', e => {
  const row = e.target.closest('.trow');
  if (!row) return;
  const t = row.dataset.tool || '';
  state.tool = state.tool === t ? null : t;
  render();
});

document.getElementById('clearTool').onclick = () => {
  state.tool = null;
  render();
};

document.getElementById('mainTbl').addEventListener('click', e => {
  const th = e.target.closest('th[data-sort]');
  if (th) {
    const col = th.dataset.sort;
    if (state.sortCol === col) {
      state.sortDir = -state.sortDir;
    } else {
      state.sortCol = col;
      state.sortDir = (col === 'name' || col === 'model' || col === 'tool' || col === 'type') ? 1 : -1;
    }
    render();
    return;
  }
  const tr = e.target.closest('tr[data-row-node]');
  if (tr) {
    const n = tr.dataset.rowNode;
    state.node = state.node === n ? null : n;
    render();
  }
});

const searchInp = document.getElementById('searchInp');
const searchClearBtn = document.getElementById('searchClearBtn');
searchInp.addEventListener('input', () => {
  state.query = searchInp.value;
  searchClearBtn.style.display = state.query ? 'inline-flex' : 'none';
  render();
});
searchClearBtn.onclick = () => {
  searchInp.value = '';
  state.query = '';
  searchClearBtn.style.display = 'none';
  render();
};

document.getElementById('rateInp').addEventListener('input', e => {
  const v = parseFloat(e.target.value);
  if (v > 0.1 && v < 100) {
    state.rate = v;
    const fb = document.getElementById('fxBadge');
    fb.className = 'fx-pill neutral';
    document.getElementById('fxText').textContent = '手动';
    render();
  }
});

async function syncLiveRate() {
  const fb = document.getElementById('fxBadge');
  const ft = document.getElementById('fxText');
  fb.className = 'fx-pill warn';
  ft.textContent = '刷新中';
  const apis = [
    ['https://open.er-api.com/v6/latest/USD', d => d && d.rates && d.rates.CNY, 'ExchangeRate-API'],
    ['https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json', d => d && d.usd && d.usd.cny, 'Currency-API'],
    ['https://api.frankfurter.app/latest?from=USD&to=CNY', d => d && d.rates && d.rates.CNY, 'ECB Frankfurter']
  ];
  for (const [url, pick, src] of apis) {
    try {
      const r = await fetch(url);
      if (!r.ok) continue;
      const v = Number(pick(await r.json()));
      if (v > 4 && v < 15) {
        state.rate = Number(v.toFixed(4));
        document.getElementById('rateInp').value = state.rate;
        fb.className = 'fx-pill';
        ft.textContent = '实时';
        fb.title = `USD/CNY 在线实时汇率: ${state.rate} · 来源: ${src}`;
        render();
        return;
      }
    } catch (_) {}
  }
  fb.className = 'fx-pill neutral';
  ft.textContent = '离线';
  fb.title = '当前无法连接外网汇率源，保持当前汇率不变';
}
document.getElementById('fxBadge').onclick = syncLiveRate;

function csvCell(val) {
  let s = String(val == null ? '' : val);
  if (/^[=+\-@\t\r]/.test(s)) s = '\'' + s;
  return `""${s.replace(/""/g, '""""')}""`;
}

// 导出当前筛选条件（时间 + 设备 + 工具 + 搜索词）下的精准账单明细 CSV
document.getElementById('csvBtn').onclick = () => {
  let csv = '\uFEFF日期,所属设备,系统类型,AI工具,模型,调用数,新增输入,缓存读取,缓存写入,输出Token,总Token,等效美元(USD),等效人民币(CNY)\r\n';
  for (const r of lastExportableRows) {
    const totTok = (r[4] || 0) + (r[5] || 0) + (r[6] || 0) + (r[7] || 0);
    const usd = Number(r[8]) || 0;
    csv += [
      csvCell(r[0]),
      csvCell(r[11] || '本机'),
      csvCell(r[12] || 'windows'),
      csvCell(r[1]),
      csvCell(r[2]),
      r[3] || 0,
      r[4] || 0,
      r[5] || 0,
      r[6] || 0,
      r[7] || 0,
      totTok,
      usd.toFixed(6),
      (usd * state.rate).toFixed(4)
    ].join(',') + '\r\n';
  }
  const suffix = (state.range === 'all' ? 'All' : (state.range + 'd')) +
    (state.node ? ('_' + state.node.replace(/[\\/:*?""<>|]/g, '_')) : '') +
    (state.tool ? ('_' + state.tool.replace(/[\\/:*?""<>|]/g, '_')) : '');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  a.download = `TokLens_Filtered_${suffix}.csv`;
  a.click();
};

window.addEventListener('resize', () => {
  renderTrendSvg(lastDailyAsc);
});

// 初始化直接渲染桌面端传入的精准数据与汇率，不自动强刷外网接口
render();
</script>
</body>
</html>";
    }
}
