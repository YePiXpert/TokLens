# 知微 · TokLens v2.1

**多台 Windows 电脑与 Linux VPS 的 AI Token 实时监控与等效账单透镜（绿色便携单文件版）**

- **主程序（绿色单文件）**：`TokLens.exe`（纯原生 WPF + 系统内置 `winsqlite3.dll`，零运行时安装依赖，内嵌 Linux VPS 探针脚本）
- **Linux VPS 零依赖探针**：`vps/toklens_agent.py`（仅需系统自带 Python 3 标准库，支持一键部署与 `crontab` 定时同步）

---

## v2.1 核心特性

1. **免代理实时请求流监控（单条 Token / 耗时 / TPS / 单条金额）**
   - 无需架设本地反向代理，直接基于 `FileSystemWatcher` 文件变动监听（`750ms` 防抖）+ `4s` 静默脉冲 + JSONL 字节偏移游标（`Seek Cursor`）动态捕获每一条新请求。
   - **「实时请求流」标签页**与**底部实时状态栏**动态滚动显示每条请求的：**请求时间、所属工具、模型名称、新增输入、缓存读取、缓存写入、输出 Token、总 Token、响应耗时、输出速率 `TPS (tok/s)` 与单条等效金额（`¥` / `$`）**。
2. **零跳动固定视口 UI + 永不弹窗锁屏**
   - 锁死顶部导航栏（`52px` 三列 `Grid`）与设备胶囊栏（`40px` 单行横向滚动 + 恒定字重），点击切换任何电脑、VPS、时间段或深浅主题均保持 **0.0px 零位移**。
   - 彻底移除全屏遮罩弹窗：依托 `%APPDATA%\TokLens\toklens.db` 本地 SQLite 快照与文件游标引擎，窗口 **`0ms` 秒开**，后台增量扫描仅需十余毫秒，刷新全程丝滑无感。
3. **100% 独立三源云端模型价格库 + 自动实时汇率（零外部软件依赖）**
   - 不依赖任何第三方本地工具，独立从 **models.dev 官方 API + LiteLLM 开源注册表 + OpenRouter 模型 API** 三源自动拉取并合并 1,800+ 模型单价（含输入、输出、缓存读/写阶梯单价），支持 `%USERPROFILE%\.toklens\custom_prices.json` 自定义覆盖。
   - 自动多源同步 USD/CNY 实时汇率，支持一键切换 `¥` / `$` 或手动输入汇率。
4. **精准区分 Claude Desktop 与 Claude Code + 全量覆盖 26+ 种 AI Harness**
   - 解析 `.claude` 会话中的 `entrypoint` 元数据与工作区拓扑，自动将桌面端会话归入 **Claude Desktop**、终端会话归入 **Claude Code**，未使用的工具自动隐藏。
   - 覆盖 Claude Desktop、Claude Code、OpenAI Codex、Google Antigravity、Gemini CLI、ZCode、Kimi Code、WorkBuddy / CodeBuddy、OpenClaw、Pi / OMP、Qwen Code、Factory Droid、Sourcegraph Amp、Goose、Crush、Aider、OpenCode、MobileClaw、Cursor、Zed、Cline / Roo Code / Kilo Code 等。
5. **本机 + 云端双通道全自动同步（GitHub 私有 Gist / WebDAV）**
   - 开启自动同步后，启动时、每 5 分钟以及本机产生新对话后自动在后台静默推拉多端脱敏按日账单（不含任何 Prompt 或对话原文），支持按「全部设备汇总」或单列查看办公电脑、家里电脑与各台 Linux VPS。
6. **双层历史账单防清理持久归档（删日志 / 清缓存 / 重装 VPS 永不丢账）**
   - **本地持久归档层**：Windows 桌面端依托 `%APPDATA%\TokLens\toklens.db`、Linux VPS 探针依托 `~/.toklens/ledger.db` 维护文件级增量游标与按日高水位账本；当 AI 工具自动轮转清理旧会话、手动删除日志或清空缓存时，被清理文件的历史累计用量会自动转入持久归档，已入库账单绝不回退扣减。
   - **云端高水位合并层**：GitHub 私有 Gist 与 WebDAV 多端同步在推拉快照时自动按「日期 + 工具 + 模型」执行高水位合并（High-Water-Mark Merge）；即使彻底重装 Windows 系统或销毁重建 Linux VPS，已同步过的历史账单依然永久保留、永不归零。

---

## 多端同步快速上手（办公电脑 ↔ 家里电脑 & Linux VPS）

### 1. 第一台电脑（如：办公电脑）初始化
1. 打开 `TokLens.exe` → 点击右上角 **「多端同步」**。
2. 将「显示名称」设为 `办公电脑`（或自定义名称）。
3. 在「GitHub 私有 Gist」下：
   - 若本机已安装并登录 GitHub CLI (`gh`)，直接点击 **「自动读取 gh 登录」**；
   - 或点击 **「网页申请 Token」**（仅需勾选 `gist` 权限）粘贴填入。
4. `私有 Gist ID` 保持留空，直接点击右下角 **「保存并立即同步」** —— 程序将自动在你的账号下创建私有 Secret Gist 并上传本机按日脱敏账单。

### 2. 第二台电脑（如：家里电脑）接入同步（两种方式任选）
- **方式 A（免配对码自动找回 · 最省心）**：
  1. 在家里电脑下载本仓库 Release 中的 `TokLens.exe`（或 `git clone` 后双击 `build.bat` 编译）。
  2. 打开 **「多端同步」**，将「显示名称」改为 `家里电脑`。
  3. 填入同一个 GitHub Token（或点击 **「自动读取 gh 登录」**），**Gist ID 留空**，直接点 **「保存并立即同步」**。
  4. 程序会自动扫描并绑定你在办公电脑创建的私有 Gist，瞬间合并展示 `全部设备 (2台)`、`家里电脑 [本机]` 与 `办公电脑`！
- **方式 B（配对码一键导入）**：
  - 在办公电脑同步窗口右上角点 **「复制配对码」** 发给自己 → 家里电脑点 **「粘贴配对码」** → 确认名称为 `家里电脑` 后点 **「保存并立即同步」**。

### 3. Linux VPS 探针一键接入
- 在同步窗口切换到 **「接入其他电脑 / Linux VPS」** 标签页，输入 VPS 名称（如 `主力 VPS-1`），点击 **「复制一键部署命令」**，SSH 登录 VPS 粘贴回车即可自动完成采集上报与 `crontab` 定时同步。

---

## 快速使用与重新编译

- **直接运行**：双击 `TokLens.exe` 即可打开桌面看板（绿色单文件，可任意拷贝至其他电脑运行）。
- **命令行导出**：
  ```powershell
  .\TokLens.exe --export-json summary.json
  .\TokLens.exe --export-csv  billing.csv
  .\TokLens.exe --export-html TokLens_Report.html
  ```
- **一键重编译**：双击运行 `build.bat`（直接调用 Windows 自带 `.NET Framework 4.x csc.exe` 编译）。
