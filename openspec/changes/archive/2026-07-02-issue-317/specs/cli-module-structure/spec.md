## ADDED Requirements

### Requirement: Issue 命令按子命令簇 partial 拆分

`IssueCommands` SHALL 声明为 `static partial class`，按子命令簇拆分为多个 partial 分文件。核心 partial SHALL 只包含 `Build()` 公共入口与跨簇共享 helper（`NumberArg`、`ProjectIssuesPath`、`IssueTemplatesPath`、`IsOptionProvided`，以及本变更新增的 `ValidateOutput`、`ResolveProjectId`），对齐 #254 已落地的 `Update.*.cs` / `TableRenderer` partial 先例。各子命令簇（CRUD、生命周期动作、Session、Workflow config、Feedback、Prereq、Comment、Template）SHALL 各自驻留在独立的 partial 分文件中，SHALL NOT 回填到核心 partial。两个无外部调用方的既有 `internal` helper（`ParseLabelsFromIssue`、`PrintCreateGuidance`）SHALL 随所属簇迁移并改回 `private`。

#### Scenario: 核心 partial 只保留 Build 与共享 helper

- **WHEN** 检查 `IssueCommands` 核心 partial 文件的内容
- **THEN** 它 SHALL 只包含 `Build()` 入口与 `NumberArg`、`ProjectIssuesPath`、`IssueTemplatesPath`、`IsOptionProvided`、`ValidateOutput`、`ResolveProjectId` 共享 helper
- **AND** SHALL NOT 包含任何具体子命令的构建方法或处理器

#### Scenario: 子命令簇各自独立成 partial 分文件

- **WHEN** 检查 `IssueCommands` 各子命令构建方法所在文件
- **THEN** CRUD（list/create/show/update）、生命周期动作（start/approve/close/reopen/retry/rerun/rerun-from-stage/force-stop/resume/reject/stop/rebase/archive/unarchive/logs/events/diff/commits）、Session、Workflow config、Feedback、Prereq、Comment、Template 簇 SHALL 各自集中在独立的 partial 分文件中
- **AND** `ParseLabelsFromIssue` 与 `PrintCreateGuidance` SHALL 随所属簇迁移且可见性改回 `private`

#### Scenario: 各分文件脱离 cli 包复杂度前列

- **WHEN** 用 scc 对 `packages/cli/Mohist.Cli/` 按单文件圈复杂度排序
- **THEN** `IssueCommands` 的各 partial 分文件 SHALL 均不在前五
- **AND** 核心 partial 的复杂度 SHALL 显著低于重构前 2268 行的单体 `MohistCliCommands.Issue.cs`

### Requirement: 重复 CLI 惯法收拢为共享 helper

`IssueCommands` 中重复出现的 output-mode 校验惯用法（重构前在各子命令处理器内逐处内联，共约 24 处）SHALL 收拢为单一共享 helper `IssueCommands.ValidateOutput(api, output)`，其签名与返回形状 SHALL 对齐 sibling `EpicCommands.ValidateOutput`。重复出现的 project-id 解析惯用法（重构前内联重复约 31 处）SHALL 同样收拢为共享 helper。这些惯法 SHALL NOT 在各子命令簇 partial 中逐簇内联重复。

#### Scenario: output-mode 校验收拢为单一 helper

- **WHEN** 检查 `IssueCommands` 中 output-mode 校验的调用方式
- **THEN** 各子命令处理器 SHALL 统一调用共享 helper `ValidateOutput(api, output)`
- **AND** 该 helper 的签名与返回形状 SHALL 与 `EpicCommands.ValidateOutput(MohistCliApi api, string? output)` 对齐
- **AND** SHALL NOT 在任何簇 partial 中内联 `MohistCliApi.ValidateOutputMode` 解包逻辑

#### Scenario: project-id 解析收拢为共享 helper

- **WHEN** 检查 `IssueCommands` 中 project-id 解析的调用方式
- **THEN** 各子命令处理器 SHALL 统一通过共享 helper 解析 project-id
- **AND** SHALL NOT 在各簇 partial 中逐处内联重复的解析惯用法

#### Scenario: 簇内不得内联重复惯法

- **WHEN** 检查任一子命令簇 partial 分文件
- **THEN** 该文件 SHALL NOT 内联 output-mode 校验解包逻辑或 project-id 解析惯用法
- **AND** 此类横切惯法 SHALL 统一委托给核心 partial 中的共享 helper
