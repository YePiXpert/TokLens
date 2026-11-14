# 更新日志 (Changelog)

本文件记录 **知微 · TokLens** 的所有重要架构演进、新增特性、缺陷修复与安全加固。

日志格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范，版本号遵循 [语义化版本 (Semantic Versioning)](https://semver.org/lang/zh-CN/)。

---

## [Unreleased]

### 变更
- 导航重组：侧栏 5 个入口，「用量」「设置」的子页面直接列在侧栏，去掉内容区顶部的第二行 tab；页面在侧栏、标题、命令面板中使用同一个名字（概览、请求明细、模型与工具、项目、订阅与费用、会话分析、设备、每日汇总、环境检查、同步与偏好）。
- 顶栏只保留页面标题、搜索 / 跳转（Ctrl+K）和同步状态；设备、时间范围、币种、导出移到页面工具条，只在受影响的页面出现，不受全局筛选影响的页面写明自己的统计范围。
- 时间范围改为 今日 / 7 天 / 30 天 / 全部 / 自定义…；侧栏底部的价格库与汇率输入框改为两行状态，点汇率可手动调整或同步。
- 订阅与费用页重写，并改为可选功能（默认关闭，在「同步与偏好 → 扫描与偏好」开启，关闭时侧栏、命令面板和 Ctrl+5 都不出现）：本月订阅支出、下次续费、本周期回本比例三项概要，每条订阅显示回本比例（周期内对应用量的 API 等值 ÷ 实付，估算）；订阅按「当前 / 待处理 / 历史」分组并显示周期进度；登记改为「选套餐 → 填实付」两步，到期日自动计算，账户可选，周期重叠时给出可用的开始日期；旧版记录在「待处理」中核对补全或删除。
- 悬浮条重做为迷你胶囊（约 170×32，原来 404×60）：去掉多数时候只能显示「无计时」的 TPS 大字，只放上下文小环、今日估值和近 1 小时 12 根微柱，单色设计；悬停展开卡片显示模型、近 1 小时、上下文、缓存命中、近 2 小时柱状图和最近 5 次请求，计时只在日志提供时显示；空闲 5 分钟自动变淡，拖到屏幕边缘吸附并记住位置。
- 点 ❌ 默认常驻系统托盘（悬浮条与监控继续运行），托盘菜单「彻底退出」才结束进程；配置改用新键 `close_to_tray`，从旧版升级也按常驻处理，之后的选择会被记住。关机 / 注销时不再拦截关闭。
- 汇率缓存 `fx_cache.json` 改存到配置目录（`TOKLENS_CONFIG_DIR`），默认位置不变。
- 近 365 天用量日历移到「每日汇总」页；概览在时间范围为「今日」时趋势图显示近 14 天；项目页把 Claude 临时 scratch 工作区合并为一行；窗口窄于 1100px 时侧栏自动收起。
- 表格：请求明细时间与工具列不再截断，无计时数据时隐藏「耗时 / 速率」；设备与每日汇总的金额合并为一列并跟随币种；会话分析显示「开始 · 时长」。
- 设置页只保留一排保存按钮（更多 / 仅保存 / 保存并同步），时光机备份与开机自启收进「更多」。
- 文案改为平实说法（如「环境检查」「Markdown 报告」「长会话」），并修正 F1 指南中与实际按钮不一致的名称。
- 字号统一为 6 档（11 / 12 / 13 / 15 / 20 / 28，`DesignSystem.Text*`），替换原先 28 种字号；界面颜色全部来自主题色表（`DesignSystem.ThemeBrush`），悬浮胶囊与主窗口使用同一套主题色，Token 构成在概览和单笔透视中配色一致。

### 删除
- 每日预算提醒（概览进度条、顶栏告警、设置项、悬浮胶囊琥珀色、80% / 超支提醒），配置中的 `daily_budget_usd` 不再读写；保留与预算无关的 Agent 高频燃烧提醒。
- 订阅页的省钱建议卡片与 24 小时作息统计、会话分析页的固定小贴士。
- 订阅页的「API 参考估值」概要与「按模型来源」估值区（由每条订阅的回本比例取代）；该页不再显示设备选择。
- SVG 分享海报、README 徽章、剪贴板战报卡三种导出；README 中从未实现的命令行参数（`--export-svg`、`--export-badge`、`--export-md`、`--today`、`--models`、`--doctor`）。
- 独立的 `SyncWindow` 窗口类及其他未被调用的方法。

### 修复
- 设备页顶部卡片内容没有显示。
- `--self-test-ui` 会把测试用的预算值写进用户的 `config.json`；自测期间不再保存配置。
- 项目页把 Claude Code 同一请求的多行日志重复计费（实测约 3.7 倍），现按 message.id + requestId 去重，与主扫描器口径一致；「占本机账单」对比改用本机全部历史。
- 环境检查在尚未运行时显示「健康就绪」、已保存令牌在校验前显示「未配置令牌」，现分别显示「未检查」「待校验」。
- 侧栏版本号被截断。
- 趋势图横轴最后两个日期标签重叠。
- 浅色主题下侧栏悬停色与侧栏底色几乎相同，看不出悬停；时间范围等分段按钮没有悬停反馈。
- 浮层按 Esc 不能关闭；靠右的导出菜单、订阅行「⋯」菜单会伸出窗口，靠底的汇率浮层会伸出窗口下沿。浮层与菜单现在会按锚点位置右对齐或向上弹出。
- 订阅编辑与删除确认对话框带最小化 / 最大化按钮；下拉框展开时按 Esc 会直接关掉整个对话框；下拉箭头显示成字母「v」。
- 快捷键指南把 Ctrl+S 写成「云端同步」，实际是导出对账明细；导出按钮提示仍提到已删除的 SVG 海报。
- 环境检查把桌面端启动的 Claude Code 会话显示为「Claude Desktop 暂无日志」，与请求明细矛盾，现标注「并入 Claude Code」。
- 会话分析的时长、环境检查的凭据状态、计价规则表头、设置页设备 ID 标签被截断或错位；设备卡金额缺千分位。

## [2.3.0] - 2026-11-14

- 采用轻量胶囊悬浮条，工具 / 模型、TPS、费用分区固定排版，长模型名不再挤掉 TPS；点击查看完整计时详情，深浅主题同步。
- 请求 TPS 仅使用有来源的耗时，排除相邻日志间隔推断；只计输出 Token，明确标记请求均速，缺计时显示未知。修正悬浮条混用跨设备费用与本机模型、将扫描状态误作生成状态的问题。
- 订阅目录按海外、国产、工具分别维护，补齐主要个人套餐与已核实的季付 / 年付价格，附币种、地区、来源、核验日期和优惠说明。
- 按账户保存实付、周期和历史续订；阻止重复周期，保存前备份配置；旧记录保留待核对，不再自动猜测已购套餐。移除订阅净赚 / 回本率的误导比较。
- 主导航收拢为概览、用量明细、订阅与费用、设备、设置；模型 / 工具归入用量明细，计价规则归入设置；彻底删除平替模拟器及其入口。
- SQLite 格式升级至 v2.10，升级前一致性备份；补充计时来源，不改变原始用量计数或核算修订号 29。
- 增加订阅迁移、币种与账期、计时来源、悬浮条布局和编辑器交互的回归检查。

## [2.2.1] - 2026-11-13

- 核算修订号升级至 29。按原始请求记录核算，不将 CC Switch 的缺价 `$0` 当作真实费用；保留有独立生成记录的上下文压缩调用。
- Codex 支持 `token_usage_record` / `token_count` 双事件去重、响应 ID、缓存写入、仅累计/仅单次计数、模型切换、日志副本与可验证的分叉历史去重；移除 `state_5.sqlite` 固定比例估算。
- Antigravity 通过 `steps.metadata → generation reference → gen_metadata` 的实际 protobuf 字段关联模型，保留 High 等档位，区分实际后台 worker；未知模型不再继承父会话或默认 Gemini 模型。
- 历史账本按可核实的“日期＋工具”分区事务重算。同步快照携带分区修订时间，防止旧模型和旧高值回灌；缺少源记录的历史保留，不进行猜测式修正。
- SQLite 迁移前创建一致性备份；仅重扫仍存在的文件，保留已删除文件的请求游标；Windows 与 VPS 探针同步修复。
- 未知价格明确显示“未定价”，不借用同系列其他模型的价格。已有可识别模型和 Orchestrator/worker 的 TokLens 定价保留。
- 修复 VPS 探针加载已有价格缓存时的变量名错误，覆盖缓存与自定义价格覆盖的回归场景。
- 修复 ZCode 原生 `modelId` / `providerId` 字段大小写不匹配导致落入默认 GLM 模型的问题；无模型证据时保留“unknown”，并支持按请求重算 ZCode 历史分区。
- 补齐 Codex 请求记录与仅累计统计的去重、累计计数重置、等长日志改写；账本或请求游标读写失败时停止本次扫描和同步，保留原账本并允许重试。
- 修复 Windows 递归遍历深层长路径时静默漏扫；部分源文件解析失败时保留已有日期分区，避免新旧模型桶叠加。
- 验证：`tests/run_tests.ps1` 和 `python -m unittest discover -s tests -p 'test_*.py'`。

## [2.2.0] - 2026-11-13

`v2.2.0` 是一次面向专业高频开发者的旗舰级全栈重构版本：桌面端全面升级为 **Obsidian / Paper Zinc 一体化六视图架构**，引入全局命令面板（`Ctrl+K`）、单条请求深度取证抽屉、桌面极简悬浮胶囊 HUD 与可视化模型定价工作台，并在云同步层彻底打通 **GitHub CLI 多账号感知、令牌归属预检与 DPAPI 凭据加密体系**。

### Added（新增）

- **Obsidian / Paper Zinc 六大全高主视图架构**：
  - 重构主界面骨架为可折叠侧边导航栏（`200px` 展开态 / `58px` 紧凑图标态）+ 固定高度全局顶栏 + 全高独立内容区，划分为 **概览看板、实时请求流、模型与工具、设备与集群、每日流水与导出、同步与设置** 六大专职视图（`Ctrl+1` ~ `Ctrl+6` 一键直达）。
  - 新增平滑面积趋势图的十字准星悬停浮层、单日点击下钻筛选、近 7 日均线当月预测花费测算以及每日预算超标柔和提醒（`DailyBudgetUsd`）。
- **`CommandPaletteOverlay`（`Ctrl+K` 全局命令面板）**：
  - 对标 Linear / Raycast / Cursor 的键盘优先命令中心，内置 GBK 中文拼音首字母提取与多字段权重打分引擎，支持通过中文、英文、拼音首字母或快捷键毫秒级检索并执行视图跳转、设备过滤、主题切换、云同步与报表导出。
- **`RequestInspectorPanel`（实时请求深度透视侧栏）**：
  - 在「实时请求流」中新增 `300px` 右侧取证抽屉（Request Forensics Drawer），支持透视单次请求的 `¥` / `$` 双币成本、单价来源徽章、Prompt Cache 节省金额与百分比、四维 Token（纯新增输入 / 缓存读 / 缓存写 / 输出）比例瀑布条、精确毫秒级延迟与 `tok/s` 吞吐档位仪表，并提供一键复制单条 JSON 明细与同模型过滤。
- **`MiniHudWindow`（桌面极简悬浮胶囊 HUD）**：
  - 新增常驻桌面的三段式等宽胶囊悬浮窗（实时展示「今日等效花费 · 总 Token · 最新活跃模型与 TPS」），内置空闲/活跃双态呼吸光晕、拖拽与点击防抖分离、位置锁定、三档透明度（`100%` / `85%` / `65%`）及预算超标琥珀色预警，采用 Win32 `WS_EX_TOOLWINDOW` 样式避免污染 `Alt+Tab` 窗口切换列表。
- **`PricingStudioWindow`（可视化模型定价工作台）**：
  - 支持一站式检索、筛选与对比 2,000+ 云端与内置模型的百万 Token 输入/输出/缓存读写单价，直观标识「使用中」「自定义覆盖」「云端同步」「内置基准」来源，支持可视化增删改 `~/.toklens/custom_prices.json` 并一键恢复官方基准单价。
- **GitHub CLI (`gh`) 多账号感知与透明化云端预检体系**：
  - 新增 `SyncService.DiscoverGitHubCliAccounts` 与 `TryReadGitHubCliTokenForUser`，自动发现本机 `gh auth status` 下登录的多个 GitHub 账号并支持按 `@用户名` 精准提取凭据。
  - 新增 `SyncService.VerifyGitHubTokenAndProbeGists`、`MaskTokenFingerprint` 与 `DescribeTokenKind`，在线校验令牌有效性、`X-OAuth-Scopes` 中的 `gist` 权限、当前 `@用户名` 归属以及账号下已有的私有 TokLens Gist 节点拓扑，清晰阐明不同设备 `gho_...` 设备独立 OAuth 令牌同账号自动互通机制。
- **设备改名无损迁移与节点别名管理**：
  - 新增 `SyncService.MigrateLocalMachineId` 与 `PendingObsoleteMachineIds` 队列：修改本机 `machine_id` 时自动将本地及云端旧节点历史账单按高水位无损合并至新节点，并在云端原子清理废弃旧节点文件。
  - 支持为远端电脑或 VPS 配置本地友好显示别名（`NodeAliases`）。
- **系统托盘常驻、单实例唤醒与非模态 `ToastManager`**：
  - 新增系统托盘图标常驻、「关闭窗口最小化到托盘」偏好持久化及托盘悬停今日花费速览；新增单实例互斥与跨进程前台唤醒，防止重复双击多开争抢数据库锁。
  - 新增右下角轻量滑入式 `ToastManager` 通知栈（最多并列 3 条，支持快捷操作按钮与平滑淡出），全面替代阻塞式弹窗。
- **专项技术文档体系**：
  - 新增跨设备云同步与令牌排障指南（`docs/SYNC_AND_GH_TOKEN_GUIDE.md`）与系统架构白皮书（`docs/ARCHITECTURE.md`）。

### Changed（重构与优化）

- **`DesignSystem` 与 `ModernWindowChrome` 视觉底层重构**：
  - 建立统一的 Obsidian（暗色）与 Paper Zinc（浅色）动态资源字典令牌体系，引入冻结画刷缓存池 `BrushCache` 与供应商专属语义色板（FNV-1a 确定性回退）。
  - 采用无边框一体化圆角窗口，直连 `dwmapi.dll` 同步沉浸式暗黑模式与 Win11 硬件级 `1px` 边框色，并通过 `WM_NCHITTEST` (`HTMAXBUTTON`) 消息钩子完整保留无边框最大化按钮上的 **Windows 11 原生 Snap Layouts 分屏悬停菜单**。
- **`ModernDataGridFactory` 零打断表格刷新引擎**：
  - 全量重构数据表格样式（极细 `4px -> 6px` 胶囊滚动条、无网格噪点表头、等宽 Tabular 数字列、内嵌柔和占比微条）。
  - 实现 `UpdateItemsPreservingState`：实时流与后台增量扫描刷新数据源时，完整保留用户当前的列排序方向、选中行焦点与横纵向滚动像素偏移，支持实时请求流一键暂停/恢复与 `Ctrl+F` 即时过滤。
- **`WinSqlite.OpenReadOnly` 四级零拷贝只读穿透升级**：
  - 升级外部锁死 SQLite 数据库读取管线为「直接无锁只读 → `file:///...?mode=ro` 零拷贝 URI → `≤ 256MB` 共享流私有临时副本回放 `-wal/-shm` → `> 256MB` `immutable=1` 零拷贝兜底」，大幅降低扫描超大 Cursor `state.vscdb` 时的磁盘拷贝开销与内存峰值。
- **`HtmlExporter` 交互式多端汇总报表升级**：
  - 导出的单文件离线 HTML 报表全面对齐 v2.2 Obsidian / Paper Zinc 双主题设计系统与色彩令牌。
- **偏好设置全面持久化**：
  - 深浅色主题（`is_dark_theme`）、人民币/美元显示偏好（`prefer_cny`）、最小化到托盘（`minimize_to_tray`）、每日预算（`daily_budget_usd`）与环境变量自定义配置目录（`TOKLENS_CONFIG_DIR`）全面接入原子持久化。

### Fixed（修复与加固）

- **修复外部 `GH_TOKEN` / `GITHUB_TOKEN` 环境变量静默劫持问题**：调用本机 `gh` CLI 读取账号与令牌时自动剥离子进程中的受限环境变量覆盖，优先读取 Windows 凭据管理器中的真实登录凭据。
- **修复跨 GitHub 账号切换残留旧 `Gist ID` 导致 `PATCH 404` 难以排查的问题**：新增远端 Gist Owner `@用户名` 预识别与明确修复引导，并严格执行 Write-after-Commit（仅在远端 `PATCH` / `PUT` 成功后才更新本地高水位），彻底杜绝跨账号旧 Gist 污染本机账单。
- **修复网络波动误建重复空白 Gist 的隐患**：`FindOrCreatePrivateGist` 升级为严格异常传播模式，当检索已有 Gist 遇到网络超时、`401` 或 `403` 时直接中止并报错，绝不在网络抖动期间盲目新建第二个空白 Gist。
- **修复 WebDAV 跨服务端兼容性与单节点超时中断问题**：`PROPFIND` 请求统一附带标准 `<D:propfind>` XML 请求体与 `Depth: 1` 头以兼容 Nextcloud / Apache `mod_dav` / 坚果云；拉取多台远端节点时新增单节点异常隔离与本地缓存平滑降级（`degradedNodes`）。
- **修复 Zed AI 跨天会话重复计费与截断回退问题**：`ScanZedAt` 升级为按 `thread_id@date` 追踪每日已结算水位切片，精准计算跨日净增量并保留已删会话分片。
- **修复 Claude 次版本号点号/连字符前缀误匹配与超长上下文计费精度**：优化 `PricingEngine` 中 `claude-opus-4.6` / `claude-opus-4-6` 等双向别名转换与前缀边界判定，新增 `>200K` 超长上下文阶梯计费（`ApplyLongContextTierIfApplicable`）。
- **加固本地 `toklens.db` 异常断电损坏自愈**：`LocalUsageStore.OpenInitializedDb` 在检测到极端断电导致的 SQLite 头损坏时，自动备份 `.corrupt.<timestamp>` 并重建健康库。

### Security（安全与隐私）

- **本地同步凭据 DPAPI 加密**：`%APPDATA%\TokLens\config.json` 中的 `gist_token` 与 `webdav_pass` 全面采用 Windows DPAPI（`ProtectedData.Protect` + `DataProtectionScope.CurrentUser` + 专属熵 `TokLens.LocalCredential.v2`）加密落盘；Linux/macOS 探针端 `config.json` 与 `ledger.db` 强制执行 `chmod 0600`。
- **SSH 部署命令防注入加固**：`BuildLinuxVpsOneLiner` 全面采用 `PosixSingleQuote` 单引号闭合转义，`gh` 用户名参数强制执行字符白名单清洗。
- **GitHub API 跨源凭据隔离**：新增 `IsTrustedGitHubHost` 严格校验 `https://` 协议与主机名，拉取 `*.githubusercontent.com` 截断原始文件时自动剥离 `Authorization` 请求头，防止访问令牌跨源泄露。
- **导出 HTML 防脚本逃逸**：`HtmlExporter` 对内联 JSON 载荷中的 `</` 与 `<!--` 执行安全转义。

---

## [2.1.0] - 2026-05-01

### Added（新增）

- **免代理实时请求流监控**：基于 `FileSystemWatcher` 与 `4s` 脉冲定时器毫秒级追踪单条对话请求的输入/缓存/输出 Token、响应耗时、`tok/s` 吞吐速率与等效金额。
- **100% 独立三源云端价格引擎（`PricingEngine`）**：自动融合 **models.dev + OpenRouter + LiteLLM** 超过 2,000 款模型的实时定价，支持远端节点快照单价自动校准（`RecalculateRemoteSnapshot`）与 `~/.toklens/custom_prices.json` 本地覆盖。
- **双层高水位防清理持久归档**：Windows 端引入 `%APPDATA%\TokLens\toklens.db`（`file_cursors` 增量字节游标 + `daily_archive` 按日归档 + `0ms` 秒开快照水合），Linux VPS 探针引入 `~/.toklens/ledger.db` 本地账本与云端 `MergeNodeSnapshotsHighWater` 双向高水位合并，日志清理或重装系统均不丢历史账单。
- **Claude Desktop 与 Claude Code 精准分流及 26+ AI Harness 覆盖**：支持自动区分 Claude 桌面端与 CLI 会话，新增或完善 OpenAI Codex（JSONL + `state_5.sqlite`）、Google Antigravity、Gemini CLI、Cursor、Windsurf / Cline / Roo Code / Kilo Code、ZCode、Kimi Code、WorkBuddy / CodeBuddy、OpenClaw、Qwen Code、Factory Droid、Amp、Goose、Crush、Aider、Continue.dev、OpenHands、Zed AI 及本机 WSL2 / Remote SSH 扫描支持。
- **三源实时汇率自动同步（`ExchangeRateService`）**：支持 `ExchangeRate-API`、`Currency-API CDN` 与 `ECB Frankfurter` 自动故障转移及本地缓存。

---

## [2.0.0] - 2026-03-15

### Added（新增）

- **多设备与 Linux VPS 无锁云同步架构（`toklens.node.v2`）**：支持通过 **GitHub 私有 Secret Gist** 或 **WebDAV 网盘** 按设备节点文件（`toklens_node_<machine_id>.json`）隔离同步多台电脑与服务器账单。
- **零依赖跨平台探针 `vps/toklens_agent.py`**：纯 Python 3 标准库实现，内嵌于 `TokLens.exe` 资源中，支持 `toklens1:...` 配对码一键导入与 Linux `crontab` 自动定时上报。
- **设备筛选胶囊栏与多端合并导出**：支持按单台电脑/VPS 或全局汇总切换统计视图，支持导出包含全部设备维度的 CSV 明细与交互式 HTML 报表。

---

## [1.0.0] - 2026-01-20

### Added（新增）

- **知微 · TokLens 首个正式版发布**：基于 Windows 原生 `.NET Framework 4.x` + WPF + 系统 `winsqlite3.dll` 打造绿色便携单文件 `TokLens.exe`，零运行时安装依赖。
- 支持本地主流 AI 编程工具会话日志扫描、Prompt Cache 命中率与省钱金额核算、深浅双色主题切换，以及 `--export-json` / `--export-csv` / `--export-html` 无头命令行导出。
