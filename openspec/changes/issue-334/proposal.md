## Why

一个已经 stopped 的 workflow run 仍会被报告为「awaiting approval」。`WorkflowRun.Stop()`（`Workflow/Domain/Run/WorkflowRun.Lifecycle.cs:126`）只把 `run.Status` 翻成 `Stopped`，从不触及当前 stage 的 `ApprovalStatus` 或 `StageRunStatus`，于是停在 `AwaitingApproval` 的 stage 仍带着未决审批字段。下游投影忠实消费这个字段——`MohistDefaultWorkflowProjection.StageApprovals` 对任何 `ApprovalStatus` 非空的 stage 都吐出 `awaiting`——导致一个已 cancel/stop 的 issue 持续在看板上提示「Approval needed」（现场 #331：`workflowStatus: stopped` 与 `approvalState.status: awaiting` 自相矛盾）。一个 stop 了的 workflow 不可能再需要审批，审批门禁只对一个活着、会继续推进的 run 有意义，domain 必须维护这个不变量。

## What Changes

- `WorkflowRun.Stop()` 在终止时清除当前 stage 的残留审批门禁：若当前 stage `IsAwaitingApproval`，置 `ApprovalStatus = null` 并把 `StageRunStatus` 从 `AwaitingApproval` 翻回。镜像 `AddRuntimeTasks`（`WorkflowRun.Work.cs:95-96`）已有的「审批失效」清理模式——stop 是更强的失效语境。
- 清理基于**运行时状态**判定（而非仅附加在新事件上），因此像 #331 这样已持久化成脏数据的 run，下次其 grain 执行 stop 时也会被修正。
- **不**发新事件：stop 是终止，不是审批决策，清理的是残留门禁状态而非「驳回」，不产出 `StageApprovalResolved` 之类。
- **不**改投影层 / 前端 / inbox / `ClearExecutableStateAsync`：它们消费的是 domain 暴露的状态，domain 修正后矛盾自然消失。

无 breaking change。

## Capabilities

### New Capabilities

- `workflow-run-stop`: workflow run 终止时的自洽不变量——一个 stopped run 绝不携带残留的 awaiting-approval 门禁。覆盖 `WorkflowRun.Stop()` 在审批中途被停止时清除当前 stage 的 `ApprovalStatus` 与 `StageRunStatus`，以及该清理基于运行时状态（从而顺带修正已持久化的脏数据），并保证派生的 DTO / 投影状态（`approvalState`、`StageApprovals`）不再呈现 awaiting。

### Modified Capabilities

（无。当前 `openspec/specs/` 下无 workflow run 终止 / 生命周期相关 spec，本次为新建。）

## Impact

- **Server / Domain**（`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Lifecycle.cs`）：`Stop()` 扩展，对当前 stage 做审批门禁清理。
- **测试**：扩展 domain stop spec（如 `WorkflowRunStatusTransitionSpecs.cs` 中的 `Stop_LandsOnStopped`），新增「awaiting-approval 中途被 stop 后 `ApprovalStatus` 为 null、`StageRunStatus` 不再是 `AwaitingApproval`」的断言。
- **下游消费方无需改码**：`WorkflowStatusMapper`、`IssueQuerier` 的 approval 派生、`MohistDefaultWorkflowProjection.StageApprovals`、Web 看板均读取清洗后的 domain 状态，自洽。
- 无持久化迁移；无 API / 数据契约变更；blast radius 限于 domain 方法与其 spec。
