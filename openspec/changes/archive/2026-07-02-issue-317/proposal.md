## Why

`mo issue …` 是 CLI 里子命令词汇最广的命令，整个命令面挤在 `MohistCliCommands.Issue.cs` 一个文件里（scc Complexity 223 / 2268 行，全仓最大 CLI 文件，约为次大 sibling 的 2.5 倍）。#254 拆 CLI 时把它显式留作后续（当时 Complexity 仅 86），此后一路涨成比 #254 当初三个目标更明显的离群点。它是 epic #22（代码复杂度热点治理）的低风险延续——纯 CLI builder、无可变状态、仅 `Build()` 公开，且高风险簇有逐字节级 spec 守护。

## What Changes

- 把 `IssueCommands` 由单体 `static class` 改为 `static partial class`，按子命令簇拆为多个 partial 分文件（`Issue.Crud.cs` / `Issue.Lifecycle.cs` / `Issue.Session.cs` / `Issue.Workflow.cs` / `Issue.Feedback.cs` / `Issue.Prereq.cs` / `Issue.Comment.cs` / `Issue.Template.cs`），核心 `Issue.cs` 只留 `Build()` + 共享 helper（`NumberArg` / `ProjectIssuesPath` / `IssueTemplatesPath` / `IsOptionProvided`），对齐 #254 的 `Update.*.cs` partial 先例。
- 把重复 **24×** 的 output-mode 校验惯用法收拢为单一 `IssueCommands.ValidateOutput(api, output)` helper（对齐 sibling `EpicCommands.ValidateOutput`）；把重复 **31×** 的 project-id 解析惯用法同样收拢为共享 helper。复杂度下降主要来自这两处收拢，而非单纯分文件。
- 两个 `internal` 但无外部调用方的 helper（`ParseLabelsFromIssue` / `PrintCreateGuidance`）随所属簇迁移并改回 `private`。
- 所有 CLI 可观察行为逐字节不变：命令名/别名、参数/flag 名与形状、HTTP 方法与路径形状、`mo issue update` 的 PATCH 字段省略语义、`--stage-models` 的 `@file` 展开、输出格式与退出码全部不变（继承 `cli-interface/spec.md` 与 #254 的"逐字节保持"要求）。

## Capabilities

### New Capabilities

<!-- 无。本变更是 #254 已建立的 `cli-module-structure` 结构契约向 `IssueCommands` 的延续扩展，不引入新的对外行为契约。 -->

### Modified Capabilities

- `cli-module-structure`: 把"按子命令簇 partial 拆分 + 核心文件只留 `Build()` 与共享 helper"的结构不变式，从 Update 编排 facade / TableRenderer / InfoCollector 扩展到 `IssueCommands`；并新增"重复的 output-mode 校验与 project-id 解析惯法 SHALL 收拢为共享 helper（对齐 `EpicCommands.ValidateOutput`），不得逐簇内联"的要求，使各分文件 scc Complexity 脱离 cli 包前列，避免再次堆出 god class。

## Impact

- **受影响代码**：`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs` 拆为核心 `Issue.cs` + 8 个簇 partial 文件；不改任何命令对外行为、不改 `IssueCommands.Build()` 的公共形状。
- **无影响项**：server / runner / web / CLI 依赖清单 / CLI 运行方式 / 退出码 / 输出格式 / 交互流程；`cli-interface`（覆盖 `mo issue …` 各子命令输出与参数契约）spec 级需求不变，仅文件组织与内部 helper 协作关系改变。
- **测试**：现有 issue 命令 spec（`CliIssueWorkflowConfigSpecs` / `CliIssueSessionSpecs` / `CliIssueLabelSpecs` / `CliIssueTemplateCommandSpecs` / `CliIssueUpdatePatchBodySpecs` / `CliIssuePrereqSpecs` / `CliIssueCommentAndFeedbackSpecs` 等）作为逐字节行为守护，拆分前后全部不变通过；本变更不新增对外行为测试。
