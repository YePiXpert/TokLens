# 知微 · TokLens v2.3.0

用量核算修订版（scanner rev 29）：以原始请求和实际后台模型为准，保留有独立生成记录的上下文压缩调用，修正模型档位、请求去重和历史账本回灌。升级前自动备份本地 SQLite；能从源记录核实的历史按分区重算，已丢失源记录的历史不会凭比例补造。Windows 与 VPS / macOS 探针需分别更新；旧版远端节点的汇总无法在本机凭空校正。

**跨 Windows、macOS、WSL2 与 Linux VPS 的全场景 AI Token 实时遥测与 FinOps 智算账单透镜（绿色单文件旗舰版）**

- **Windows 桌面旗舰 + 终端双引擎（单文件零依赖）**：`TokLens.exe`（纯原生 WPF + 系统内置 `winsqlite3.dll` + Win32 Per-Monitor DPI v2，零外部运行时安装依赖，内嵌跨平台 Python 探针）
- **Linux VPS / macOS 零依赖探针**：`vps/toklens_agent.py`（仅依赖系统内置 Python 3 标准库，支持 Linux `crontab` 与 macOS `LaunchAgent` 后台守护自动同步：`vps/install_macos_launchd.sh`）
- **深度文档**：[跨设备同步与 GitHub CLI 令牌原理解析](docs/SYNC_AND_GH_TOKEN_GUIDE.md) · [系统架构与高水位账本白皮书](docs/ARCHITECTURE.md) · [版本演进日志](CHANGELOG.md)

---

## 核心问答：为什么到家自动读取的 `gh` 令牌和单位不一样？

这是 **GitHub CLI (`gh`) 的标准设备独立 OAuth 安全设计**，完全正常且**无需把单位的令牌拷贝到家里**：

1. **每台电脑独立签发 `gho_...` 设备令牌**：当你在单位电脑与家里电脑分别执行 `gh auth login` 时，GitHub OAuth 服务器会为每一台设备单独颁发一串不同的 `gho_...` 授权令牌（便于日后单独吊销某台旧设备而不影响其他电脑）。
2. **认同一个 `@用户名` GitHub 账号，不认同一串密码字符串**：TokLens 多端云同步认的是**令牌背后的 GitHub 账号身份（如 `@YePiXpert`）以及 `gist` 权限范围**。只要家里与单位登录的是同一个 GitHub 账号，两串不同的 `gho_...` 令牌天然拥有读写同一个私有账单 Gist（含 `toklens_sync_v1.json` 锚点文件）的完整权限，Gist ID 留空点击同步即可自动寻回并无损合并！
3. **仅在以下 4 种情况会导致两边没有汇合（应用内已内置自动诊断与一键修复）**：
   - 环境变量 `GH_TOKEN` / `GITHUB_TOKEN` 覆盖了 `gh` 登录态（常见于单位配置了企业内部 Token）；
   - `gh auth status` 登录了多个账号（个人号 + 工作号），当前激活账号不一致（可在「同步与设置」下拉选择目标 `@用户名`）；
   - 某台电脑早期登录 `gh` 时未包含 `gist` 权限（执行 `gh auth refresh -h github.com -s gist` 即可秒级补齐）；
   - 本地残留已失效的旧 `Gist ID` 返回 `404`（v2.2 已支持自动清空失效 ID 并重新寻回/重建私有 Gist）。

> 在软件内任意界面按 **`F1`** 切换到「跨设备同步与令牌原理解析」标签页，或进入 **「设置 › 环境检查 (`Ctrl+0`)」** 即可核验本机令牌指纹、归属账号与 `gist` 权限。

---

## v2.3 界面与费用口径

### 导航

左侧 5 个入口，「用量」和「设置」的子页面直接列在侧栏里：

- **概览**（`Ctrl+1`）：用量、缓存、工具和设备汇总。时间范围选「今日」时，趋势图显示近 14 天作参照。
- **用量**：请求明细（`Ctrl+2`）、模型与工具（`Ctrl+3`）、项目（`Ctrl+4`）、会话分析（`Ctrl+7`）、每日汇总（`Ctrl+9`，含近 365 天每日用量日历）。
- **订阅与费用**（`Ctrl+5`）：登记实际订阅、查看到期与续费，按模型来源查看 API 参考估值。
- **设备**（`Ctrl+8`）：本机与 VPS / 其他设备的用量记录。
- **设置**（`Ctrl+,`）：同步与偏好、环境检查（`Ctrl+0`）、计价规则。

设备、时间范围、币种和导出放在页面顶部的工具条里，只出现在受它们影响的页面：项目页固定统计本机全部历史，订阅页在页面内选择时间范围，设置页没有工具条。窗口窄于 1100px 时侧栏自动收成图标。

已删除：平替模拟器（`Ctrl+6`）、每日预算提醒、SVG 分享海报、README 徽章、剪贴板战报卡。

### 轻量胶囊与 TPS

`Ctrl+M` 切换悬浮条：显示本机工具 / 模型、独立的 TPS、今日 API 估值。单击查看完整模型名、输出量、计时口径和时间；拖动移动，右键锁定或调整透明度，双击打开主窗口。深浅主题同步。

TPS 只使用日志明确提供的请求耗时或同一请求的起止时间，分子仅为输出 Token，不计输入或缓存；显示的是**请求均速**，可能包含等待，不能冒充流式实时生成速度。缺少可靠计时显示 `—`，超过五分钟的记录进入空闲状态。旧缓存未记录计时来源的值不会被升级为可靠 TPS。首次升级会备份 SQLite 并重新解析仍存在的日志。

### 订阅记录与价格

登记分两步：先选服务商和套餐，再填实付金额与周期，到期日自动计算。旧版记录放在「待处理」，核对补全后才计入月度合计。目录快照核验于 **2026-11-14**，每个服务商可打开官方价格来源。月付、季付、年付分开记录，金额用原币种，年付填全年扣款总额；地区、促销、税费和团队席位按实际订单填写。每个账户跨设备只登记一次，重叠周期会阻止重复保存；续订新增记录，目录更新不覆盖历史实付。

旧版自动推荐的套餐不作为已购订阅，旧人工配置保留为待核对记录；旧“国产 Coding Plan”必须选定具体服务商后才能计入费用。API 参考估值与订阅费用分开，不再显示“净赚 / 回本率”，也不拿 API 单价推算套餐剩余额度。模型来源与计费渠道分别看待：Claude Code 中的 GLM 仍归为智谱模型，不能证明购买了哪家平台的订阅。

已核验预设来源：[OpenAI](https://learn.chatgpt.com/docs/pricing)、[Claude](https://claude.com/pricing)、[Google AI 美国区](https://gemini.google/us/subscriptions/?hl=en)、[智谱 GLM](https://www.bigmodel.cn/glm-coding)、[Kimi](https://www.kimi.com/help/membership/membership-pricing)、[阿里云 Token Plan](https://help.aliyun.com/zh/model-studio/token-plan-personal-overview)、[MiniMax M Plan](https://www.minimax.cn/m-plan)、[Cursor](https://cursor.com/pricing)、[Devin / Windsurf](https://devin.ai/pricing)、[GitHub Copilot](https://github.com/features/copilot/plans)、[TRAE](https://www.trae.ai/pricing)、[Zed](https://zed.dev/pricing)、[腾讯云 Token Plan](https://cloud.tencent.com/document/product/1823/130060/)、[CodeBuddy 中国版](https://www.codebuddy.cn/pricing/)、[百度千帆](https://cloud.baidu.com/doc/qianfan/s/Dmrabu8b6)。火山方舟的动态价格本次未能核实，保留套餐名称和官方入口，按订单填写金额。其他平台、地区或历史方案支持自定义登记，不预填未核实价格。

### 全局命令面板

`Ctrl+K` 支持中文、英文及拼音首字母搜索。计价规则入口位于“设置”，保留用量行的价格编辑快捷入口。

### 3. 全覆盖 26+ AI 工具生态 + 双层高水位防清理账本 + 时光机备份
- **免代理全量采集**：原生支持 Claude Code、Claude Desktop、OpenAI Codex、Google Antigravity、Gemini CLI、Cursor、Windsurf、VS Code (Cline / Roo Code / Kilo Code / Continue)、ZCode、Kimi Code、WorkBuddy / CodeBuddy、OpenClaw、Qwen Code、Factory Droid、Sourcegraph Amp、Goose、Crush、Aider、OpenCode、Zed，以及本机 **WSL2** 与 **Remote SSH** 会话。
- **永不归零的高水位账本 + 时光机**：本地 SQLite 游标库 + 云端 `MAX(...)` 高水位无损合并 + 每日自动生成 `.toklens.gz` 时光机压缩快照（自动保留最近 10 份），清空日志或重装系统均不丢失历史账单。

### 4. 导出
支持导出：**离线 HTML 看板**、**Markdown 报告（含趋势摘要）**、**Excel 可直接打开的 CSV 对账明细** 与 **只含聚合数据的 JSON 汇总快照**。导出入口在页面工具条右侧的「导出」。

---

## 快速安装与多端接入

### Windows 桌面端运行与包管理器支持
- **绿色单文件直开**：双击运行 `TokLens.exe`（或从 Releases 解压便携包）；附带标准清单支持 [WinGet](packaging/winget/YePiXpert.TokLens.yaml) 与 [Scoop](packaging/scoop/toklens.json)。
- **源码零依赖一键编译**：在 Windows 10/11 下直接运行 `build.bat`（自动调用系统内置 `.NET Framework 4.x csc.exe`，无需安装 Visual Studio）。

### Linux VPS 与 macOS 守护进程一键接入
- **Linux VPS (`crontab`)**：在桌面端「同步与设置」输入服务器名称并点击「复制一键部署命令」，SSH 粘贴执行即可完成历史扫描与定时同步。
- **macOS (`LaunchAgent`)**：在 Mac 终端运行 `bash vps/install_macos_launchd.sh` 即可配置原生 `launchd` 后台低功耗定时同步。

---

## 全局快捷键速查

| 快捷键 | 功能说明 |
| :--- | :--- |
| `Ctrl+K` | 唤起全局命令面板（支持中文 / 拼音首字母 / 英文极速检索） |
| `Ctrl+1` ~ `Ctrl+5`、`Ctrl+7` ~ `Ctrl+9`、`Ctrl+0` | 切换对应页面；`Ctrl+6` 已移除 |
| `Ctrl+,` | 打开「同步与设置」视图 |
| `Ctrl+M` | 显示 / 隐藏桌面置顶微胶囊悬浮窗 (`MiniHudWindow`) |
| `Ctrl+Alt+T` | 系统级全局热键：从后台/托盘唤起主窗口或切换桌面微胶囊 |
| `F1` / `?` | 打开「快捷键速查 & GitHub 跨设备令牌原理解析」交互指南 |
| `F5` / `Ctrl+R` | 立即执行后台增量扫描并同步云端账单与实时汇率 |
| `Ctrl+F` | 聚焦当前视图搜索过滤框 |
| `Ctrl+E` / `Ctrl+S` | 导出并打开交互式 HTML 看板 / 导出多端 CSV 财务对账单 |
| `Esc` | 清空搜索框、收起右侧单笔账单透视抽屉或重置单日/工具筛选 |

---

## 终端无头 CLI 引擎 (`TokLens.exe`)

支持在 PowerShell / CMD / Windows Terminal 中直接作为极客 CLI 工具运行（自动通过 Win32 `AttachConsole` 输出彩色 ANSI 表格，零弹窗）：

```powershell
.\TokLens.exe --help                         # 查看完整 CLI 命令帮助
.\TokLens.exe --version                      # 查看版本号与运行环境信息
.\TokLens.exe --summary --days 30            # 展示最近 30 天工具与设备消耗对齐表
.\TokLens.exe --status                       # 单行极简今日花费输出（适合嵌入 Prompt / 状态栏）
.\TokLens.exe --sync-now                     # 无头触发本地增量扫描与 Gist / WebDAV 双向同步
.\TokLens.exe --export-html Report.html      # 导出独立单文件交互式网页看板
```
