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

        public static void ExportHtmlReport(List<RecordBucket> records, ScanResult metaRes, double usdToCny, string outPath)
        {
            List<object[]> rows = new List<object[]>(records.Count);
            string latest = metaRes != null ? metaRes.ReferenceDate : "";
            for (int i = 0; i < records.Count; i++)
            {
                RecordBucket r = records[i];
                if (r.Date != "未知日期" && string.CompareOrdinal(r.Date, latest) > 0)
                    latest = r.Date;
                rows.Add(new object[]
                {
                    r.Date,
                    r.Tool,
                    r.Model,
                    r.Turns,
                    r.FreshInput,
                    r.CacheRead,
                    r.CacheWrite,
                    r.Output,
                    Math.Round(r.CostUsd, 6),
                    Math.Round(r.BaselineUsd, 6),
                    r.RateLabel ?? "",
                    string.IsNullOrEmpty(r.MachineName) ? "本机" : r.MachineName
                });
            }

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["home"] = metaRes != null ? metaRes.UserHome : "";
            payload["generatedAt"] = metaRes != null ? metaRes.GeneratedAt : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            payload["refDate"] = string.IsNullOrEmpty(latest) ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : latest;
            payload["pricingCount"] = metaRes != null ? metaRes.PricingModelCount : 0;
            payload["elapsed"] = metaRes != null ? Math.Round(metaRes.ElapsedSeconds, 2) : 0.0;
            payload["rate"] = usdToCny;
            payload["rows"] = rows;

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            string jsonPayload = js.Serialize(payload).Replace("</", "<\\/");

            string html = TEMPLATE.Replace("__TOKLENS_JSON__", jsonPayload);
            File.WriteAllText(outPath, html, new UTF8Encoding(true));
        }

        private const string TEMPLATE = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>知微 · TokLens v2.1 — 多设备与 VPS AI Token 实时监控与账单透镜</title>
<style>
:root {
  --bg: #0b0f19; --card: #111827; --card2: #1e293b; --border: #1f2937;
  --fg: #f8fafc; --sub: #94a3b8; --muted: #64748b;
  --accent: #38bdf8; --emerald: #10b981; --amber: #f59e0b;
}
* { box-sizing: border-box; margin: 0; padding: 0; }
body {
  font-family: ""Microsoft YaHei UI"", ""Segoe UI"", system-ui, sans-serif;
  background: var(--bg); color: var(--fg); line-height: 1.5; padding: 22px;
}
.wrap { max-width: 1340px; margin: 0 auto; }
.hdr {
  display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center;
  gap: 14px; margin-bottom: 14px; padding: 16px 20px; background: var(--card);
  border: 1px solid var(--border); border-radius: 14px;
}
.brand { display: flex; align-items: center; gap: 12px; }
.logo {
  width: 38px; height: 38px; border-radius: 10px;
  background: linear-gradient(135deg, #0ea5e9, #6366f1);
  display: flex; align-items: center; justify-content: center;
  font-weight: 800; font-size: 18px; color: #fff; box-shadow: 0 4px 14px rgba(56,189,248,.28);
}
.title { font-size: 19px; font-weight: 700; letter-spacing: .3px; }
.meta { font-size: 12px; color: var(--sub); margin-top: 2px; }
.ctrl { display: flex; flex-wrap: wrap; align-items: center; gap: 9px; }
.seg {
  display: inline-flex; background: #0f172a; border: 1px solid var(--border);
  border-radius: 8px; padding: 3px; gap: 2px;
}
.seg button {
  background: transparent; border: none; color: var(--sub); padding: 5px 10px;
  border-radius: 6px; font-size: 12px; cursor: pointer; font-family: inherit;
}
.seg button.on { background: var(--card2); color: var(--fg); font-weight: 600; }
.rate-box {
  display: inline-flex; align-items: center; gap: 6px; background: #0f172a;
  border: 1px solid var(--border); border-radius: 8px; padding: 4px 10px; font-size: 12px; color: var(--sub);
}
.rate-box input {
  width: 58px; background: transparent; border: none; color: var(--fg);
  font-size: 12.5px; font-weight: 600; text-align: right; outline: none;
}
.btn {
  background: #1e293b; color: var(--fg); border: 1px solid #334155;
  border-radius: 8px; padding: 6px 12px; font-size: 12px; cursor: pointer; font-family: inherit;
}
.btn:hover { border-color: var(--accent); color: var(--accent); }
.nodes-bar {
  display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 16px; padding: 10px 14px;
  background: var(--card); border: 1px solid var(--border); border-radius: 12px; align-items: center;
}
.npill {
  padding: 5px 12px; border-radius: 8px; background: #0f172a; border: 1px solid var(--border);
  color: var(--sub); font-size: 12.5px; cursor: pointer;
}
.npill.on { background: rgba(56,189,248,.14); border-color: var(--accent); color: var(--fg); font-weight: 600; }
.kpis { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; margin-bottom: 16px; }
@media (max-width: 960px) { .kpis { grid-template-columns: repeat(2, 1fr); } }
.kpi {
  background: var(--card); border: 1px solid var(--border); border-radius: 12px;
  padding: 15px 18px; position: relative; overflow: hidden;
}
.kpi .lbl { font-size: 12.5px; color: var(--sub); margin-bottom: 5px; }
.kpi .val { font-size: 25px; font-weight: 700; font-variant-numeric: tabular-nums; }
.kpi .sub { font-size: 12px; color: var(--muted); margin-top: 5px; }
.mid { display: grid; grid-template-columns: 1fr 1.35fr; gap: 16px; margin-bottom: 16px; }
@media (max-width: 980px) { .mid { grid-template-columns: 1fr; } }
.panel {
  background: var(--card); border: 1px solid var(--border); border-radius: 12px;
  padding: 15px 18px; display: flex; flex-direction: column;
}
.phdr { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; gap: 8px; }
.ptitle { font-size: 14px; font-weight: 600; }
.tlist { display: flex; flex-direction: column; gap: 7px; max-height: 290px; overflow-y: auto; padding-right: 4px; }
.trow {
  padding: 8px 11px; border-radius: 9px; background: #0f172a; border: 1px solid transparent;
  cursor: pointer; transition: .15s;
}
.trow:hover { border-color: #334155; }
.trow.sel { border-color: var(--accent); background: rgba(56,189,248,.08); }
.trow-top { display: flex; justify-content: space-between; font-size: 12.5px; margin-bottom: 5px; }
.tbar-bg { height: 5px; background: #1e293b; border-radius: 3px; overflow: hidden; }
.tbar-fg { height: 100%; border-radius: 3px; }
.chart-box { flex: 1; min-height: 250px; display: flex; flex-direction: column; justify-content: flex-end; }
.bars { display: flex; align-items: flex-end; gap: 4px; height: 210px; padding-top: 16px; border-bottom: 1px solid var(--border); }
.bcol { flex: 1; height: 100%; display: flex; align-items: flex-end; cursor: pointer; }
.bcol:hover .bfill { filter: brightness(1.25); }
.bfill { width: 100%; background: linear-gradient(180deg, #38bdf8, #6366f1); border-radius: 4px 4px 0 0; min-height: 2px; }
.xlabels { display: flex; justify-content: space-between; font-size: 11.5px; color: var(--muted); margin-top: 6px; }
.tabs { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; }
.tab {
  padding: 6px 14px; border-radius: 8px; background: #0f172a; color: var(--sub);
  border: 1px solid var(--border); cursor: pointer; font-size: 12.5px;
}
.tab.on { background: var(--card2); color: var(--fg); border-color: #334155; font-weight: 600; }
table { width: 100%; border-collapse: collapse; font-size: 12.5px; font-variant-numeric: tabular-nums; }
th { text-align: left; color: var(--sub); font-weight: 600; padding: 9px 10px; border-bottom: 1px solid var(--border); }
td { padding: 8px 10px; border-bottom: 1px solid rgba(31,41,55,.65); }
tr:hover td { background: rgba(30,41,59,.45); }
.r { text-align: right; }
.pill { display: inline-block; padding: 1px 7px; border-radius: 99px; font-size: 11px; background: #1e293b; color: #cbd5e1; }
</style>
</head>
<body>
<div class=""wrap"">
  <div class=""hdr"">
    <div class=""brand"">
      <div class=""logo"">微</div>
      <div>
        <div class=""title"">知微 · TokLens <span style=""font-size:12px;font-weight:500;color:#38bdf8"">多端汇总网页报告</span></div>
        <div class=""meta"" id=""metaLine""></div>
      </div>
    </div>
    <div class=""ctrl"">
      <div class=""seg"" id=""rangeSeg"">
        <button data-r=""all"" class=""on"">全部</button>
        <button data-r=""30"">近30天</button>
        <button data-r=""7"">近7天</button>
        <button data-r=""3"">近3天</button>
        <button data-r=""1"">今日</button>
      </div>
      <div class=""seg"" id=""curSeg"">
        <button data-c=""CNY"" class=""on"">¥ 人民币</button>
        <button data-c=""USD"">$ 美元</button>
      </div>
      <div class=""rate-box"">汇率 <input id=""rateInp"" type=""number"" step=""0.0001"" value=""7.25""><span id=""fxBadge"" title=""点击同步最新在线 USD/CNY 实时汇率"" style=""font-size:11px;padding:1px 6px;border-radius:4px;background:#1e293b;color:#10b981;cursor:pointer;font-weight:600"">实时 ↻</span></div>
      <button class=""btn"" id=""csvBtn"">导出 CSV</button>
    </div>
  </div>

  <div class=""nodes-bar"" id=""nodesBar""></div>

  <div class=""kpis"">
    <div class=""kpi""><div class=""lbl"">API 等效总花费</div><div class=""val"" id=""kCost"" style=""color:#38bdf8"">-</div><div class=""sub"" id=""kCostSub"">-</div></div>
    <div class=""kpi""><div class=""lbl"">累计总 Token</div><div class=""val"" id=""kTok"">-</div><div class=""sub"" id=""kTokSub"">-</div></div>
    <div class=""kpi""><div class=""lbl"">Prompt Cache 命中率</div><div class=""val"" id=""kHit"" style=""color:#f59e0b"">-</div><div class=""sub"" id=""kHitSub"">-</div></div>
    <div class=""kpi""><div class=""lbl"">Prompt Cache 累计省钱</div><div class=""val"" id=""kSave"" style=""color:#10b981"">-</div><div class=""sub"" id=""kSaveSub"">-</div></div>
  </div>

  <div class=""mid"">
    <div class=""panel"">
      <div class=""phdr"">
        <div class=""ptitle"">工具消耗排行 <span id=""clearTool"" style=""display:none;font-size:12px;color:#38bdf8;cursor:pointer;margin-left:8px"">[清除筛选 ×]</span></div>
        <div class=""seg"" id=""metricSeg"">
          <button data-m=""cost"" class=""on"">按花费</button>
          <button data-m=""tok"">按Token</button>
        </div>
      </div>
      <div class=""tlist"" id=""toolList""></div>
    </div>
    <div class=""panel"">
      <div class=""phdr"">
        <div class=""ptitle"">每日消耗趋势</div>
        <div style=""font-size:12px;color:#94a3b8"" id=""hoverInfo"">悬停柱状图查看单日明细</div>
      </div>
      <div class=""chart-box"">
        <div class=""bars"" id=""barBox""></div>
        <div class=""xlabels"" id=""xBox""></div>
      </div>
    </div>
  </div>

  <div class=""panel"">
    <div class=""tabs"">
      <div class=""tab on"" id=""tabNodes"">设备单列对比</div>
      <div class=""tab"" id=""tabM"">模型账单明细</div>
      <div class=""tab"" id=""tabD"">每日账单流水</div>
    </div>
    <div style=""overflow-x:auto"">
      <table id=""mainTbl""></table>
    </div>
  </div>
</div>
<script>
const DATA = __TOKLENS_JSON__;
const COLORS = ['#38bdf8','#818cf8','#34d399','#fbbf24','#f472b6','#fb7185','#a78bfa','#2dd4bf','#f97316','#60a5fa'];
let state = { range: 'all', cur: 'CNY', rate: DATA.rate || 7.25, metric: 'cost', node: null, tool: null, tab: 'nodes' };
document.getElementById('rateInp').value = state.rate;
document.getElementById('metaLine').textContent = `匹配 ${DATA.pricingCount} 个模型单价 · 生成于 ${DATA.generatedAt}`;

function fmtTok(n) {
  if (n >= 1e9) return (n/1e9).toFixed(2) + 'B';
  if (n >= 1e6) return (n/1e6).toFixed(2) + 'M';
  if (n >= 1e3) return (n/1e3).toFixed(1) + 'K';
  return String(n);
}
function fmtMoney(usd, cur) {
  const c = cur || state.cur;
  const v = c === 'CNY' ? usd * state.rate : usd;
  const s = c === 'CNY' ? '¥' : '$';
  if (v >= 100) return s + v.toLocaleString('en-US', {minimumFractionDigits:2, maximumFractionDigits:2});
  if (v >= 1) return s + v.toFixed(2);
  return s + v.toFixed(4);
}
function cutoffDate(days) {
  const p = DATA.refDate.split('-').map(Number);
  const d = new Date(p[0], p[1]-1, p[2]);
  d.setDate(d.getDate() - (days - 1));
  return d.toISOString().slice(0,10);
}
function render() {
  const cut = state.range === 'all' ? '' : cutoffDate(Number(state.range));
  const timeRows = DATA.rows.filter(r => !cut || r[0] >= cut);

  // Build nodes bar
  const nmap = new Map();
  let allNodeCost = 0;
  for (const r of timeRows) {
    const mn = r[11] || '本机';
    let o = nmap.get(mn);
    if (!o) { o = { name: mn, turns:0, fi:0, cr:0, out:0, tok:0, cost:0, tools: new Set() }; nmap.set(mn, o); }
    o.turns += r[3]; o.fi += r[4]; o.cr += r[5]; o.out += r[7];
    o.tok += (r[4]+r[5]+r[6]+r[7]); o.cost += r[8]; o.tools.add(r[1]);
    allNodeCost += r[8];
  }
  const narr = Array.from(nmap.values()).sort((a,b) => b.cost - a.cost);
  const nb = document.getElementById('nodesBar');
  nb.innerHTML = `<span style=""font-size:12px;color:#94a3b8;margin-right:4px"">设备视角：</span>`;
  const allPill = document.createElement('div');
  allPill.className = 'npill' + (!state.node ? ' on' : '');
  allPill.textContent = `🌐 全部设备汇总 (${narr.length}台 · ${fmtMoney(allNodeCost)})`;
  allPill.onclick = () => { state.node = null; render(); };
  nb.appendChild(allPill);
  narr.forEach(n => {
    const p = document.createElement('div');
    p.className = 'npill' + (state.node === n.name ? ' on' : '');
    p.textContent = `🖥 ${n.name} (${fmtMoney(n.cost)})`;
    p.onclick = () => { state.node = state.node === n.name ? null : n.name; render(); };
    nb.appendChild(p);
  });

  const baseRows = state.node ? timeRows.filter(r => (r[11]||'本机') === state.node) : timeRows;
  const rows = state.tool ? baseRows.filter(r => r[1] === state.tool) : baseRows;

  let turns=0, fi=0, cr=0, cw=0, out=0, cost=0, base=0;
  const tset = new Set();
  for (const r of rows) {
    tset.add(r[1]); turns+=r[3]; fi+=r[4]; cr+=r[5]; cw+=r[6]; out+=r[7]; cost+=r[8]; base+=r[9];
  }
  const tot = fi + cr + cw + out;
  const hit = (fi + cr + cw) > 0 ? (cr * 100 / (fi + cr + cw)) : 0;
  const saved = Math.max(0, base - cost);

  document.getElementById('kCost').textContent = fmtMoney(cost);
  document.getElementById('kCostSub').textContent = `折合 ${fmtMoney(cost, state.cur==='CNY'?'USD':'CNY')} · 共 ${turns.toLocaleString()} 次调用`;
  document.getElementById('kTok').textContent = fmtTok(tot);
  document.getElementById('kTokSub').textContent = `新增输入 ${fmtTok(fi)} · 输出 ${fmtTok(out)} · ${tset.size} 个工具`;
  document.getElementById('kHit').textContent = hit.toFixed(1) + '%';
  document.getElementById('kHitSub').textContent = `缓存读 ${fmtTok(cr)} · 缓存写 ${fmtTok(cw)}`;
  document.getElementById('kSave').textContent = fmtMoney(saved);
  document.getElementById('kSaveSub').textContent = `无缓存原价 ${fmtMoney(base)}`;

  const tmap = new Map();
  for (const r of baseRows) {
    let o = tmap.get(r[1]);
    if (!o) { o = { tool: r[1], turns:0, tok:0, cost:0 }; tmap.set(r[1], o); }
    o.turns += r[3]; o.tok += (r[4]+r[5]+r[6]+r[7]); o.cost += r[8];
  }
  const tarr = Array.from(tmap.values()).sort((a,b) => state.metric==='cost' ? b.cost-a.cost : b.tok-a.tok);
  const sumM = tarr.reduce((s,x) => s + (state.metric==='cost'?x.cost:x.tok), 0) || 1;
  const maxM = tarr.length ? (state.metric==='cost'?tarr[0].cost:tarr[0].tok) || 1 : 1;
  document.getElementById('clearTool').style.display = state.tool ? 'inline' : 'none';

  const tl = document.getElementById('toolList');
  tl.innerHTML = '';
  tarr.forEach((t, idx) => {
    const val = state.metric==='cost' ? t.cost : t.tok;
    const pct = (val * 100 / sumM).toFixed(1);
    const w = Math.max(2, val * 100 / maxM);
    const col = COLORS[idx % COLORS.length];
    const div = document.createElement('div');
    div.className = 'trow' + (state.tool === t.tool ? ' sel' : '');
    div.innerHTML = `<div class=""trow-top""><span><b style=""color:${col}"">●</b> ${t.tool} <span style=""color:#64748b;font-size:11.5px"">(${t.turns})</span></span><span><b>${fmtMoney(t.cost)}</b> <span style=""color:#94a3b8;font-size:11.5px"">· ${fmtTok(t.tok)} (${pct}%)</span></span></div><div class=""tbar-bg""><div class=""tbar-fg"" style=""width:${w}%;background:${col}""></div></div>`;
    div.onclick = () => { state.tool = state.tool === t.tool ? null : t.tool; render(); };
    tl.appendChild(div);
  });

  const dmap = new Map();
  for (const r of rows) {
    let d = dmap.get(r[0]);
    if (!d) { d = { date: r[0], turns:0, fi:0, cr:0, out:0, tok:0, cost:0 }; dmap.set(r[0], d); }
    d.turns += r[3]; d.fi += r[4]; d.cr += r[5]; d.out += r[7]; d.tok += (r[4]+r[5]+r[6]+r[7]); d.cost += r[8];
  }
  const darr = Array.from(dmap.values()).sort((a,b) => a.date.localeCompare(b.date));
  const maxD = darr.reduce((m,x) => Math.max(m, state.metric==='cost'?x.cost:x.tok), 0) || 1;
  const bb = document.getElementById('barBox'); bb.innerHTML = '';
  darr.forEach(d => {
    const v = state.metric==='cost' ? d.cost : d.tok;
    const h = Math.max(2, v * 100 / maxD);
    const c = document.createElement('div'); c.className = 'bcol';
    c.innerHTML = `<div class=""bfill"" style=""height:${h}%""></div>`;
    c.onmouseenter = () => {
      document.getElementById('hoverInfo').textContent = `${d.date} · ${fmtMoney(d.cost)} · ${fmtTok(d.tok)} Tokens · ${d.turns} 次调用`;
    };
    bb.appendChild(c);
  });
  const xb = document.getElementById('xBox');
  xb.innerHTML = darr.length ? `<span>${darr[0].date}</span><span>${darr[Math.floor(darr.length/2)].date}</span><span>${darr[darr.length-1].date}</span>` : '';

  const tbl = document.getElementById('mainTbl');
  if (state.tab === 'nodes') {
    let h = `<tr><th>设备名称</th><th>使用工具数</th><th class=""r"">调用次数</th><th class=""r"">纯新增输入</th><th class=""r"">缓存读取</th><th class=""r"">输出Token</th><th class=""r"">总Token</th><th class=""r"">等效美元($)</th><th class=""r"">等效人民币(¥)</th><th class=""r"">全网占比</th></tr>`;
    for (const n of narr) {
      const p = allNodeCost > 0 ? (n.cost*100/allNodeCost).toFixed(1)+'%' : '0.0%';
      h += `<tr><td><b>🖥 ${n.name}</b></td><td><span class=""pill"">${n.tools.size} 个工具</span></td><td class=""r"">${n.turns}</td><td class=""r"">${fmtTok(n.fi)}</td><td class=""r"">${fmtTok(n.cr)}</td><td class=""r"">${fmtTok(n.out)}</td><td class=""r"">${fmtTok(n.tok)}</td><td class=""r"">${fmtMoney(n.cost,'USD')}</td><td class=""r""><b>${fmtMoney(n.cost,'CNY')}</b></td><td class=""r"">${p}</td></tr>`;
    }
    tbl.innerHTML = h;
  } else if (state.tab === 'models') {
    const mmap = new Map();
    for (const r of rows) {
      const k = r[1] + '||' + r[2];
      let m = mmap.get(k);
      if (!m) { m = { tool:r[1], model:r[2], turns:0, fi:0, cr:0, cw:0, out:0, tok:0, cost:0, rate:r[10] }; mmap.set(k, m); }
      m.turns+=r[3]; m.fi+=r[4]; m.cr+=r[5]; m.cw+=r[6]; m.out+=r[7]; m.tok+=(r[4]+r[5]+r[6]+r[7]); m.cost+=r[8];
    }
    const marr = Array.from(mmap.values()).sort((a,b) => b.cost - a.cost);
    let h = `<tr><th>模型名称</th><th>所属工具</th><th class=""r"">调用数</th><th class=""r"">纯新增输入</th><th class=""r"">缓存读取</th><th class=""r"">缓存写入</th><th class=""r"">输出Token</th><th class=""r"">总Token</th><th class=""r"">单价($/M)</th><th class=""r"">等效花费</th><th class=""r"">占比</th></tr>`;
    for (const m of marr) {
      const p = cost > 0 ? (m.cost*100/cost).toFixed(1)+'%' : '0.0%';
      h += `<tr><td><b>${m.model}</b></td><td><span class=""pill"">${m.tool}</span></td><td class=""r"">${m.turns}</td><td class=""r"">${fmtTok(m.fi)}</td><td class=""r"">${fmtTok(m.cr)}</td><td class=""r"">${fmtTok(m.cw)}</td><td class=""r"">${fmtTok(m.out)}</td><td class=""r"">${fmtTok(m.tok)}</td><td class=""r"">${m.rate}</td><td class=""r""><b>${fmtMoney(m.cost)}</b></td><td class=""r"">${p}</td></tr>`;
    }
    tbl.innerHTML = h;
  } else {
    const ddesc = darr.slice().reverse();
    let h = `<tr><th>日期</th><th class=""r"">调用数</th><th class=""r"">纯新增输入</th><th class=""r"">缓存读取</th><th class=""r"">输出Token</th><th class=""r"">总Token</th><th class=""r"">等效美元($)</th><th class=""r"">等效人民币(¥)</th></tr>`;
    for (const d of ddesc) {
      h += `<tr><td><b>${d.date}</b></td><td class=""r"">${d.turns}</td><td class=""r"">${fmtTok(d.fi)}</td><td class=""r"">${fmtTok(d.cr)}</td><td class=""r"">${fmtTok(d.out)}</td><td class=""r"">${fmtTok(d.tok)}</td><td class=""r"">${fmtMoney(d.cost,'USD')}</td><td class=""r""><b>${fmtMoney(d.cost,'CNY')}</b></td></tr>`;
    }
    tbl.innerHTML = h;
  }
}
document.querySelectorAll('#rangeSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#rangeSeg button').forEach(x=>x.classList.remove('on')); b.classList.add('on');
  state.range = b.dataset.r; render();
});
document.querySelectorAll('#curSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#curSeg button').forEach(x=>x.classList.remove('on')); b.classList.add('on');
  state.cur = b.dataset.c; render();
});
document.querySelectorAll('#metricSeg button').forEach(b => b.onclick = () => {
  document.querySelectorAll('#metricSeg button').forEach(x=>x.classList.remove('on')); b.classList.add('on');
  state.metric = b.dataset.m; render();
});
document.getElementById('clearTool').onclick = () => { state.tool = null; render(); };
document.getElementById('rateInp').oninput = e => {
  const v = parseFloat(e.target.value);
  if (v > 0) {
    state.rate = v;
    const fb = document.getElementById('fxBadge');
    fb.textContent = '手动 ↻'; fb.style.color = '#94a3b8';
    render();
  }
};
async function syncLiveRate() {
  const fb = document.getElementById('fxBadge');
  fb.textContent = '同步中…'; fb.style.color = '#f59e0b';
  const apis = [
    ['https://open.er-api.com/v6/latest/USD', d => d && d.rates && d.rates.CNY, 'ExchangeRate-API'],
    ['https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json', d => d && d.usd && d.usd.cny, 'Currency-API CDN'],
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
        fb.textContent = '实时 ↻'; fb.style.color = '#10b981';
        fb.title = `USD/CNY 实时汇率: ${state.rate} · 来源: ${src}（点击刷新）`;
        render();
        return;
      }
    } catch (_) {}
  }
  fb.textContent = '缓存 ↻'; fb.style.color = '#94a3b8';
}
document.getElementById('fxBadge').onclick = syncLiveRate;
function setTab(t, id) {
  state.tab = t;
  ['tabNodes','tabM','tabD'].forEach(x => document.getElementById(x).classList.remove('on'));
  document.getElementById(id).classList.add('on'); render();
}
document.getElementById('tabNodes').onclick = () => setTab('nodes','tabNodes');
document.getElementById('tabM').onclick = () => setTab('models','tabM');
document.getElementById('tabD').onclick = () => setTab('daily','tabD');
document.getElementById('csvBtn').onclick = () => {
  let csv = '\uFEFF日期,所属设备,AI工具,模型,调用数,新增输入,缓存读取,缓存写入,输出,等效美元,等效人民币\r\n';
  for (const r of DATA.rows) {
    csv += `""${r[0]}"",""${r[11]||'本机'}"",""${r[1]}"",""${r[2]}"",${r[3]},${r[4]},${r[5]},${r[6]},${r[7]},${r[8].toFixed(4)},${(r[8]*state.rate).toFixed(4)}\r\n`;
  }
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([csv], {type:'text/csv;charset=utf-8'}));
  a.download = 'TokLens_MultiDevice_Usage.csv'; a.click();
};
render();
syncLiveRate();
</script>
</body>
</html>";
    }
}
