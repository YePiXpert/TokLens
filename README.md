# 知微 · TokLens

TokLens 从 AI 编程工具已有的本地日志中统计 Token 和 API 参考估值。Windows 提供桌面面板、托盘和可选悬浮控件；Linux、macOS 可运行 Python 探针，把统计快照同步到同一账本。

**它能说明已采集记录用了多少；不能仅靠本地 Token 数推算订阅剩余额度，也不把 API 估值当成实际扣款。** 当前版本以 [version.txt](version.txt) 为准。

## 第一次使用

1. 从 [Releases](https://github.com/YePiXpert/TokLens/releases) 下载 **`TokLens.exe`**，双击运行；Windows 只需这一个文件。可选 ZIP 里也只有同一个 EXE。
2. 运行你平时使用的 AI 编程工具，完成一次请求，再启动 TokLens。
3. 在「设置 → 环境检查」核对发现了哪些日志、哪些来源无记录或读取失败。没有记录不等于零用量；日志格式变化、权限不足、远程运行、工具未保存日志都会影响覆盖。
4. 在「请求」页抽查工具、模型、Token 构成、服务档位及估值来源。需要跨设备时再配置 Gist 或 WebDAV；本机统计无需先登录同步服务。

Windows 无需安装；配置、账本与缓存默认保存在 `%APPDATA%\TokLens`，换 EXE 会保留这些数据，移动 EXE 不会一起搬走数据。

Windows 10/11 使用系统 .NET Framework/WPF 与 SQLite。远端探针与 macOS 脚本是单独提供的可选附件，Windows 桌面无需下载它们。远端探针使用 Python 标准库；读取 `.jsonl.zst` 归档需要支持 Zstandard 的 Python 版本，推荐 Python 3.14。较旧 Python 仍可读普通 JSONL，无法解析的压缩文件不会被伪造成零用量。

## 常驻控件

`Ctrl+M` 显示或隐藏悬浮控件（状态胶囊）。单击展开或收起详情；不再把鼠标经过当成打开复杂面板的操作。

- 明确显示本机最新请求，或固定到选定会话。固定会话不会被其他工具的新请求偷偷替换。
- 上下文优先使用会话日志里的上限；只能取得模型标称上限时标为参照。上下文占用不等于订阅额度。
- 详情显示数据更新时间、上下文、服务档位、今日 API 参考估值；未连接官方额度时明确标识，不显示推算百分比。
- 活跃、空闲、过期、读取失败分别展示。右键可调整样式、位置锁定等偏好。
- 可从托盘恢复主窗口。关闭主窗口默认隐藏到托盘；托盘「彻底退出」结束进程。常驻行为可在设置关闭。

日志只证明最近有记录，不证明流式生成仍在进行。请求平均输出速率仅在日志有可靠计时来源时计算；缺失时显示未知，不制造实时 TPS。

## Fast / Priority 与价格口径

服务档位属于每次请求的历史事实，不能用当前 `/fast` 开关回填过去的账单。TokLens 分开保存：

| 字段 | 含义 |
|---|---|
| 请求档位 | 当时请求或会话设置要求的档位 |
| 实际档位 | 响应中明确报告的档位；没有响应证据则未知 |
| 档位证据 | 取自哪个日志字段 |
| 估值依据、价格版本 | 日志报告金额、已知档位参考价、请求档位估算、未知档位参考价或未定价 |

`priority` 归一为 Fast，`default` 归一为 Standard；模型名、推理强度和服务档位分开处理。Claude Code / Claude Desktop 的实际档位取自响应 `usage` 里的 `speed` 与 `service_tier`：`speed = fast` 是 Fast，`service_tier = standard` 是 Standard；Anthropic 的 `priority` 是预留容量，不当作 Fast。只出现 `-fast` 后缀但没有对应价格时，不删掉后缀偷偷套普通价。

- **实际档位已知**：优先用实际档位及请求日期匹配价格。
- **只有请求 Fast 的证据**：可以按该档位估值，但明确标为“按请求档位估算”，不宣称实际已走 Fast。
- **两个档位都未知**：只提供注明限制的 Standard 参考估值，不推断 Fast。
- **明确档位没有匹配价格**：保留 Token，金额标为未定价，不按固定倍率猜测。
- **日志已有报告金额**：保留原金额，不再叠加 Fast 乘数。
- **自定义或代理渠道未提供档位价**：保留日志中的渠道名，用已知模型对应的官方档位价作参考，并注明渠道未报价；不会把 `custom` 改写成官方直连。
- **历史日期缺少适用报价**：可以用当前有效的档位价作参考估值，明确标为“非历史报价”，参考日期与来源保存在价格详情。过期规则、未知模型变体和超出档位输入限制仍不套价。

API 参考估值按输入、输出、缓存读写以及有证据的长上下文规则计算；订阅的额度消耗倍率不用于 API 费用计算。可比较的请求会显示 Standard 参考值与档位额外估值；这些也不是实际扣款。未知值保持未知，不当作零。

内置快照在 [pricing/service_tier_prices.json](pricing/service_tier_prices.json)，每条规则附来源、版本和生效日期。快照从核验日起使用，不自动宣称适用于更早历史。用户可在配置目录放同名 JSON 添加经过核实的规则；不要把套餐额度倍率填成 API 单价。格式见 [架构与数据契约](docs/ARCHITECTURE.md)。

「设置 → 模型价格」在一张表中统一提供云端三源价格同步、全部模型查价、普通档自定义覆盖与官方档位价（Standard / Fast / Ultrafast / Flex）按日期核验；普通档自定义不覆盖 Fast 等专属档位费率。

## 覆盖范围与边界

扫描器包含 Claude Code、Codex、Antigravity、Gemini CLI、Cursor、Windsurf、Cline / Roo / Kilo、OpenCode、ZCode、Qwen、Kimi、Aider、Goose、Crush、Zed 等来源的适配。**适配器存在不代表每个平台、版本和日志字段都已完整覆盖。** 以本机环境检查及可追溯的请求记录为准；未知模型、未定价与读取失败均应保留可见。

Windows 桌面有五个页面：概览、请求、用量（模型与工具、项目、每日汇总）、设备、设置（常规、云同步、模型价格、环境检查）。包含多个视图的页面在标题旁切换。设备页在配置了同步或已有其他设备的数据后出现。订阅登记为可选功能；实付订阅与 API 估值分开。登记订阅不会接通官方额度查询，也无法证明某次日志请求是经该订阅付费。

桌面可导出离线网页看板（HTML）和 CSV 对账明细；命令行另有 `--export-json` 汇总快照，不包含对话正文。来源文件、工作区和会话等本机信息仍可能暴露项目名称，分享导出前应核对内容。

## 多设备与历史

桌面端支持 Gist 和 WebDAV；远端探针的部署命令可在同步设置生成。macOS 可使用 `vps/install_macos_launchd.sh` 配置 LaunchAgent，Linux 可使用定时任务。只部署单个 `toklens_agent.py` 时，其内置价格快照仍可工作。

同一 GitHub 账号在不同设备可以有不同的 `gh` 令牌；同步看账号和权限，不要求令牌字符串相同。确保具有 Gist 权限、设备 ID 唯一，并连接到同一账本。详细说明见 [同步指南](docs/SYNC_AND_GH_TOKEN_GUIDE.md)。

本地 SQLite、请求游标、云端快照和备份帮助保留已经采集的历史；未采集就删除的日志无法恢复。升级解析器前自动备份数据库；源日志仍在时可按完整分区重算，源日志丢失时保留旧汇总及未知档位，不凭比例补造。

**升级时请同时更新 Windows 与所有远端探针。** 新快照在兼容原有字段的基础上追加档位和估值依据；旧客户端不会理解这些维度，混用旧版本可能破坏分类。

## 更新应用

应用内「检查更新」检查 GitHub Release。下载可取消；只有同一 Release 的 `TokLens.exe` 与 `SHA256SUMS.txt` 校验匹配后才准备替换。应用重启完成替换，保留旧程序备份供失败时回滚；配置和历史账本独立保留。

可从设置或托盘菜单打开「检查更新」。默认每天在后台检查一次，仅提示有新版，不自动下载或安装；可在设置中关闭「每天检查应用更新（仅提示）」。

没有发布新版、网络失败或校验不通过时，继续运行当前版本。SHA-256 校验验证下载完整性；它不等同于独立代码签名。Python 探针需要单独更新，不因桌面更新而自动修改远端服务器。

## 快捷键与命令行

| 快捷键 | 操作 |
|---|---|
| `Ctrl+1` – `Ctrl+4` | 概览、请求、用量、设备 |
| `Ctrl+M` | 显示 / 隐藏悬浮控件 |
| `Ctrl+Alt+T` | 从后台恢复主窗口 |
| `Ctrl+,` | 设置 |
| `Ctrl+0` | 环境检查 |
| `F5` / `Ctrl+R` | 刷新扫描与同步 |

```powershell
.\TokLens.exe --help
.\TokLens.exe --version
.\TokLens.exe --summary --days 30
.\TokLens.exe --status
.\TokLens.exe --sync-now
.\TokLens.exe --export-html Report.html
```

## 开发、验证与打包

```powershell
.\build.bat --nopause
powershell -ExecutionPolicy Bypass -File tests\run_tests.ps1
python -m unittest discover -s tests -p "test_*.py" -v
powershell -ExecutionPolicy Bypass -File packaging\package_release.ps1 -SkipBuild -UpdateManifests
powershell -ExecutionPolicy Bypass -File packaging\package_release.ps1 -VerifyOnly
```

`version.txt` 是版本源；构建脚本同步 C#、程序集和 Python 版本，并把同一份档位价格快照嵌入桌面与探针。CI 运行 C# 套件、Python 单元测试和共享核算样例，验证版本、ZIP 内容、SHA-256 与包管理清单，再允许标签发布。

Windows 主下载为单文件 `TokLens.exe`；可选便携包名为 `TokLens-v<完整版本>-win-x64-portable.zip`，内容仅有该 EXE。远端探针、macOS 脚本及校验清单作为独立发布附件。`packaging/scoop` 与 `packaging/winget` 是清单文件；生成或更新文件不代表已经提交到包管理器的公共仓库。发布前须使用最终构建重新生成对应哈希。

更多：[更新日志](CHANGELOG.md) · [架构与数据契约](docs/ARCHITECTURE.md)
