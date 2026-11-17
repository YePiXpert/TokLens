# TokLens 架构与数据契约

当前实现以本地日志为输入，经来源适配、请求去重、费用口径计算，再写入本地账本和可同步快照。Windows 桌面和 Python 探针使用相同的档位维度；核心核算样例位于 `tests/fixtures/service_tier_cases.json`。

## 数据流

```text
来源日志 / SQLite
  → 来源解析器、文件游标
  → 请求事件：模型、Token 分量、时间、会话、档位证据
  → 请求级计价 / 数据状态
  → 本机明细与 HUD
  → 日期、工具、模型、档位、计价依据聚合
  → SQLite 历史账本 / Gist 或 WebDAV 节点快照
```

来源可包含只有汇总计数、没有真实请求耗时或没有实际模型/档位的日志。解析器不得填造这些字段。已知来源数据读取失败时不能当空数据覆盖其完整历史。

## 档位证据

`RequestedServiceTier` 记录请求设置；`ActualServiceTier` 只由明确响应证据建立。两者的标准化值为 `standard`、`fast`、`ultrafast`、`flex`、`unknown`。`priority → fast`，`default → standard`；无法识别的值保持未知，不推断为普通档位。`TierEvidence` 记录证据所在的来源字段。

Codex 支持 `event_msg → thread_settings_applied → thread_settings` 的嵌套历史设置。字段缺失沿用先前设置，显式 null 或无法识别的值清除先前档位。重放已有 response ID 不得用后来设置改写原请求的档位与模型。

解析会话或 turn 的 `service_tier` 不代表响应实际执行档位；`actual_service_tier`、`response.service_tier` 等明确响应字段才能建立实际值。当前配置文件不用于回填历史。Provider、SessionId、Workspace 用于区分来源和选择会话，未知值不根据界面状态补全。

## 估值依据

| CostBasis | 定义 |
|---|---|
| `reported` | 日志已报告金额，避免二次加价 |
| `estimated_tier_price` | 实际档位已知，找到日期适用的独立档位价格 |
| `estimated_requested_tier` | 只有请求档位证据，按该档位估算；实际未核实 |
| `estimated_standard` | 普通档位的基础价格参考 |
| `estimated_unknown_tier` | 档位都未知，只给 Standard 参考 |
| `unpriced` | 无可用价格，保留用量但不假造费用 |
| `legacy_unknown` | 旧汇总缺少请求证据，保留历史金额与未知属性 |

服务档位价格按 provider、精确模型、tier、生效区间匹配。只在明确配置时应用长上下文分段和缓存写入时长价格。API 单价与订阅额度倍率是两个不同概念。

明确的自定义模型基础价（含零价）优先于内置普通参考价；该覆盖仅用于 Standard 或档位未知的普通参考估值。Fast、Ultrafast、Flex 仍需要各自的档位报价，不套用普通自定义价，也不从中乘倍率推导。日志已经报告的金额优先于上述估值。

`StandardEquivalentUsd` 是同次请求按 Standard 规则计算的参考值；`ServiceTierPremiumUsd` 是可比较估值的非负差额。两者无法建立时保持 null，不能把 null 呈现为节省了零元。`reported` 金额不拆出虚构的 API 档位溢价。

## 价格文件

`pricing/service_tier_prices.json` 是仓库内置快照；每项包含：

```json
{
  "provider": "openai",
  "model": "example-model",
  "tier": "fast",
  "effective_from": "2026-11-17",
  "input_per_m": 4,
  "output_per_m": 20,
  "cache_read_per_m": 0.2,
  "cache_write_per_m": 5,
  "source": "https://example.com/official-pricing",
  "version": "example-only"
}
```

上述是字段示例，不是任何模型的价格推荐。可选字段包括 `effective_until`、`max_prompt_tokens`、`long_context_threshold`、`long_input_per_m`、`long_output_per_m`、`long_cache_read_per_m`、`long_cache_write_per_m`、`cache_write_long_per_m`、`cache_write_long_duration`、`long_cache_write_long_per_m`。超过该档位支持的最大输入或缺少适用规则时，保留未定价。`auto`、`max`、`fast`、`1m` 等可能改变价格的变体不得自动删除后缀后套用基础模型价格；精确配置的零价格仍是有效报价。

Windows 从内嵌资源加载；Python 构建时内嵌同一快照，亦可读取相邻文件。用户配置目录的同名文件可补充已核实规则。构建检查阻止 Python 内嵌快照与源文件漂移。价格版本包含规则版本与来源 URL；历史事件仅使用其日期适用的记录。

## 节点快照

顶层 schema 保持 `toklens.node.v2`；`scanner_rev=31` 标识档位核算版本。`records` 每行沿用前 11 列，追加到 19 列：

| 索引 | 内容 |
|---|---|
| 0–2 | 日期、工具、模型 |
| 3–7 | 请求数、未缓存输入、缓存读取、缓存写入、输出 |
| 8–10 | 估值 USD、无缓存参考 USD、价格标签 |
| 11–13 | 请求档位、实际档位、档位证据 |
| 14–16 | CostBasis、PricingVersion、Provider |
| 17–18 | 可空的 Standard 参考值、档位额外估值 |

聚合键为日期、工具、模型、请求档位、实际档位、CostBasis、PricingVersion、Provider。证据不构成聚合键；同桶不同证据标 `mixed`。旧行缺少新列时统一为未知，历史金额不乘猜测倍率。

完整源分区（日期 + 工具）有 reconciliation proof 时，允许修正旧重复记录并降低错误总量。双方都有证明时优先较新扫描器版本，再比较核算时间。没有完整证明时，按同日期/工具/模型整个汇总组选择一致的高水位版本，避免新旧计价维度相加造成重复计费。未知历史不会被强拆为 Fast。

SQLite 迁移前备份；仍存在的日志重新解析，丢失的日志仅保留此前游标与汇总。新旧节点混跑无法保证档位维度完整，请同步升级所有节点。

## UI 与实时状态

悬浮控件使用明确的本机范围，可跟随最新请求或固定会话。数据年龄、日志读取错误、活跃与空闲不混为一个状态。输入上限优先日志实际值，模型标称值仅作参照。缺官方额度来源时显示未连接；本地 Token 计数不推算账户剩余百分比。

## 版本、测试与分发

`version.txt` 驱动 `BuildVersion.generated.cs`、程序集与 Python 版本。`packaging/sync_version.ps1 -Check` 验证版本和快照一致性。`tests/run_tests.ps1` 运行 C# 套件，Python 使用 unittest，二者共享服务档位样例。

`packaging/package_release.ps1` 统一打包资产、完整版本文件名、SHA256SUMS、Scoop 和 WinGet 清单；`-VerifyOnly` 重验最终产物。此脚本不会提交公共包管理仓库。Release 中必须同时提供 `TokLens.exe` 和 `SHA256SUMS.txt`，供应用内更新校验。
