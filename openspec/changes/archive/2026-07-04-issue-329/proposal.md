# Proposal: 收敛 CLI 消费层：消除重复前奏与 HTTP 样板

## Why

CLI 的消费层（命令前奏 helper、HTTP 客户端、envelope 解析）把同样的逻辑复制了十几份散落在各 partial 文件里：项目解析与输出模式校验在 `IssueCommands` 与 `EpicCommands` 各自定义了一份；按动词区分的 5 个 HTTP 方法各自封装却最终汇入同一段 envelope 打印；`success`/`error`/`code` 提取块在 `MohistCliApi` 里重复了 6 处。改一处共同逻辑要动十几个文件，代码量远超实际需要。本次收敛把这些重复收口到单一实现，同时清理配置服务中一条已被统一 agent 配置取代的 legacy `model` fallback 分支。

## What Changes

- 把命令前奏 helper（项目引用解析 + 输出模式校验）收口到单一共享位置，删除 `IssueCommands.ValidateOutput` 与 `EpicCommands.ValidateOutput` 两份重复定义；统一各 partial（Agent / Workflow / Project / ProjectWorkflow / Server 等）目前直接调用 `MohistCliApi.ValidateOutputMode` 的分散写法。
- 把按动词区分的 5 个 HTTP 方法（`PrintGetAsync` / `PrintPostAsync` / `PrintPutAsync` / `PrintPatchAsync` / `PrintDeleteAsync`，以及对应的 `*WithOutputAsync` 变体）合并为接受 HTTP 方法或请求工厂的单一通用方法，统一汇入既有的 envelope 打印逻辑。
- 把散落的 envelope 解析（提取 `success` / `error` / `code` 字段）收口到单一实现，消除 `MohistCliApi` 内 6 处重复的提取块。
- **移除** `ConfigService.GetAgentConfigAsync` 中从废弃的单字段 `model` 构建 agent 配置的 legacy fallback 路径；统一 agent 配置引入后该路径已被取代。项目已声明无需版本兼容。**BREAKING**（仅对仍只配置 `model` 而未配置 `agent` 的存量 config.jsonc：此前会合成一个 `{ model }` agent 配置，移除后该情形不再产生 agent 配置）。

边界（Non-Goals）：不重新分组命令树、不拆分或合并各资源命令的 partial 文件；不重组 API 层职责边界；不动表格渲染器；不改变任何 CLI 命令的对外行为或输出格式（legacy fallback 移除是唯一例外）。

## Capabilities

- `cli-command-prelude`: 命令层前奏——项目引用解析（`--project` / `--project-id` / 活动项目状态）与输出模式校验（`--output` 的 `table` / `json`）。要求跨所有资源命令（Issue / Epic / Agent / Workflow / Project 等）只保留一份共享实现，退出码与模式语义不变。
- `cli-api-envelope`: `MohistCliApi` 的请求执行与响应 envelope 处理。要求按动词区分的 HTTP 调用合并为单一通用方法，`success` / `error` / `code` 字段提取收口到单一解析实现，对成功/失败/404 的打印与退出码行为不变。
- `agent-config-resolution`: 服务端从 config.jsonc 解析全局 agent 配置。统一 agent 配置引入后，agent 配置仅来源于 `agent` 对象；移除从废弃单字段 `model` 合成 agent 配置的 fallback。

## Impact

- **CLI 代码**:
  - `packages/cli/Mohist.Cli/MohistCliApi.cs` —— 合并 5 个按动词的 `Print*Async` / `*WithOutputAsync` 方法为通用方法；收口 6 处 `success`/`error`/`code` 提取块到单一 envelope 解析（`PrintResponseAsync` / `PrintRawResponseAsync` / `ReadPostResultAsync` / `ReadSuccessDataAsync` / `PrintProjectListAsync` / `PrintRunnerShowAsync` 等）。
  - `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`、`MohistCliCommands.Epic.cs` —— 删除重复的 `ValidateOutput`（及 `ResolveProjectId` wrapper），迁至共享位置。
  - 各命令 partial（Agent / Workflow / Project / ProjectWorkflow / Server / Label / System / Issue.* / Epic 等）—— 统一前奏调用点，消除 `var (mode, exit) = ValidateOutput(...)` 两行样板与直接 `MohistCliApi.ValidateOutputMode` 的分散写法。
- **Server 代码**:
  - `packages/server/src/Mohist.Server/Infrastructure/Config/ConfigService.cs` —— 移除 `GetAgentConfigAsync` 的 `model` fallback 分支（及 schema 中 `model` 条目、`SetAgentModelAsync` 内为避免 shadowing 而保留的 `ClearAsync("model")`、`GetVariables` 文档中对该 fallback 的引用）。
- **测试**:
  - `packages/server/tests/.../ConfigServiceSpecs.cs` —— `GetVariables_OnlyLegacyModelSet_SynthesizesAgentObject` 需改为断言“仅 `model` 不再产生 agent 配置”；其余 CLI spec 需无回归通过。
- **APIs / 依赖**: 无 HTTP 契约变更，无新第三方依赖。
- **系统**: CLI 与 server 配置层；runner / web 不受影响。
