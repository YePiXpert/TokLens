# 知微 · TokLens v2.2

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

> 在软件内任意界面按 **`F1`** 切换到「跨设备同步与令牌原理解析」标签页，或进入 **「AI 环境体检医生 (`Ctrl+0`)」** 即可实时核验本机令牌指纹、归属账号与 `gist` 权限。

---

## v2.2 全方位重构亮点

### 1. Obsidian / Paper Zinc 旗舰级工作台（11 大全高视图 · `0.0px` 零跳动）
对标 **Linear、Vercel Geist、OpenRouter Activity、Helicone 与 OrbStack** 的现代开发者工具审美：
- **一体化无边框窗口外壳**：原生支持 Windows 11 Snap Layouts 贴靠布局、DWM 沉浸式暗黑/纸白双主题联动、Per-Monitor V2 跨屏高分屏矢量渲染、可折叠侧边导航栏（`200px` 展开 / `58px` 图标态）与严格锁定 `48px` 的全局上下文顶栏。
- **11 大全高专业视图**：
  1. **概览看板 (`Ctrl+1`)**：四维 Bento KPI（含日预算进度与 30 天滚动预测）、工具/设备排行、十字准星平滑面积趋势图（支持点击单日钻取）与实时流快览。
  2. **实时请求流 (`Ctrl+2`)**：免代理捕获单笔请求耗时与生成速率（`tok/s`），单击任意请求即可滑出右侧 **单笔账单四维计费瀑布流透视抽屉 (`RequestInspectorPanel`)**。
  3. **模型与工具 (`Ctrl+3`)**：多色堆叠占比条、百万输入单价精准排序，内置 **模型单价浏览器与私有中转自定义费率工作台 (`PricingStudioWindow`)**。
  4. **项目归因 (`Ctrl+4`)**：100% 本机离线解析 Claude Code、Codex、Cursor 等工作区与代码仓库算力投入，支持一键在资源管理器定位仓库（云端同步零路径泄露）。
  5. **智算洞察 (`Ctrl+5`)**：2026 主流订阅（Claude Pro/Max、ChatGPT Plus/Pro、Cursor Pro/Ultra）回本等效评估矩阵、GitHub 风格 365 天算力热力图与 24 小时昼夜节律分析。
  6. **平替模拟器 (`Ctrl+6`)**：基于本机真实历史四维 Token 画像（新增输入 / 缓存读 / 缓存写 / 输出），交互式推演跨模型迁移后的真实账单差价与省钱幅度。
  7. **会话显微镜 (`Ctrl+7`)**：自动聚类识别长对话上下文「滚雪球膨胀」与低缓存命中异常会话，给出精准开新会话优化建议。
  8. **设备与集群 (`Ctrl+8`)**：OrbStack 风格多设备状态卡片与跨 Windows / Linux VPS / macOS 集群对比表，支持设备本地备注名与离线节点清理。
  9. **每日流水与导出 (`Ctrl+9`)**：补齐 `缓存写入 (CacheWrite)` 对账列的完整日流水，集成 **7 种专业报表与社交战报导出**。
  10. **环境体检医生 (`Ctrl+0`)**：一键巡检 26 项本地 AI 客户端、IDE 数据库、WSL2 发行版与 GitHub CLI 令牌健康度。
  11. **同步与设置 (`Ctrl+,`)**：GitHub Gist / WebDAV 多端同步、一键加密配对码、Linux/macOS 部署脚本生成、**时光机本地备份恢复** 与 **Windows 登录静默自启**。

### 2. 全局命令面板 (`Ctrl+K`) + 桌面置顶微胶囊 (`Ctrl+M` / `Ctrl+Alt+T`)
- **全局命令面板 (`CommandPaletteOverlay`)**：支持中文、英文及**中文拼音首字母**极速模糊检索（如输入 `pt` 秒跳「平替模拟器」、输入 `gh` 秒开令牌排障指南）。
- **Dynamic Island 风格桌面微胶囊 (`MiniHudWindow`)**：无焦点抢占置顶悬浮展示「今日花费 · 今日 Token · 最新模型与 TPS」，支持三档透明度与「锁定位置 + 鼠标穿透」模式，写代码全屏盯盘零遮挡。

### 3. 全覆盖 26+ AI 工具生态 + 双层高水位防清理账本 + 时光机备份
- **免代理全量采集**：原生支持 Claude Code、Claude Desktop、OpenAI Codex、Google Antigravity、Gemini CLI、Cursor、Windsurf、VS Code (Cline / Roo Code / Kilo Code / Continue)、ZCode、Kimi Code、WorkBuddy / CodeBuddy、OpenClaw、Qwen Code、Factory Droid、Sourcegraph Amp、Goose、Crush、Aider、OpenCode、Zed，以及本机 **WSL2** 与 **Remote SSH** 会话。
- **永不归零的高水位账本 + 时光机**：本地 SQLite 游标库 + 云端 `MAX(...)` 高水位无损合并 + 每日自动生成 `.toklens.gz` 时光机压缩快照（自动保留最近 10 份），清空日志或重装系统均不丢失历史账单。

### 4. 七维 FinOps 财务与极客分享导出体系
支持一键导出：**交互式离线 HTML 看板**、**GitHub GFM Markdown 深度战报（含 ASCII 趋势图）**、**UTF-8 BOM Excel 财务对账 CSV**、**840×460 高清矢量 Bento 极客分享海报 (`.svg`)**、**GitHub 个人主页 README 动态徽章 (`.svg`)**、**飞书/企微纯文本战报卡（剪贴板）** 与 **100% 脱敏 JSON 汇总快照**。

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
| `Ctrl+1` ~ `Ctrl+9`, `Ctrl+0` | 秒切左侧 10 大业务与诊断视图 |
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
.\TokLens.exe --today                        # 终端彩色卡片展示今日实时算力账单
.\TokLens.exe --summary --days 30            # 展示最近 30 天工具与设备消耗对齐表
.\TokLens.exe --models --days 7              # 查看最近 7 天模型细粒度消耗排行
.\TokLens.exe --status                       # 单行极简今日花费输出（适合嵌入 Prompt / 状态栏）
.\TokLens.exe --doctor                       # 在终端运行本机 AI 工具链与 gh 令牌健康巡检
.\TokLens.exe --sync-now                     # 无头触发本地增量扫描与 Gist / WebDAV 双向同步
.\TokLens.exe --export-md   FinOps.md        # 导出 GitHub GFM Markdown 深度战报
.\TokLens.exe --export-svg  Poster.svg       # 导出 840x460 矢量极客算力海报
.\TokLens.exe --export-badge badge.svg       # 导出 GitHub README 个人主页算力徽章
.\TokLens.exe --export-html Report.html      # 导出独立单文件交互式网页看板
```
