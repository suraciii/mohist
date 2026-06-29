## Why

当 workflow 已经走到 `check` 或 `integrate`，但更早的 stage（`plan`/`build`）需要重新执行时——例如 workflow template 更新后想用最新模板从某个 stage 推进，或早期 stage 没产出后续 stage 依赖的运行时变量——用户今天只能 retry 当前失败 task、rerun 当前 stage，或废弃整个 issue 重来。缺少一种「从某个已到达过的 stage 开始重推进后续 workflow」的恢复手段，迫使每次修正都付出全量重跑或状态手工修补的代价。现在做是因为该缺口是 epic #24（Workflow 执行历史与恢复语义收敛）的核心恢复语义，且 `mohist/github-pr` 已具备可重入 action 契约，使得范围重跑安全可行。

## What Changes

- 新增显式 workflow 控制 action `rerun-from-stage`，与现有 `retry`、`rerun` 并列：选择一个**已被该 workflow run 到达过**的 stage，从该 stage 起到结尾的范围重新执行。
- 新增 domain 方法 `RerunFromStage(stageId)`，按下标做范围重置：
  - 目标 stage **之前**的 stage 不动，结果仍是当前进度依据。
  - 目标 stage 替换为新 attempt（`Attempt = old + 1`，`Initialized = false`）。
  - 目标 stage **之后**的 stage 替换为 fresh stage（`Attempt = 1`，`Initialized = false`），advance 到达时正常重新 init。
  - `CurrentStageId` 切到目标 stage，`Failure` 清空，`Status = Running`。
- **纯控制面 invalidation**：workflow run 上的运行时变量（`setVars` 产物）**不清理**；workspace、git、外部副作用（PR、分支、已归档 OpenSpec change）**不自动撤销**；被无效掉的旧 `StageRun` 数据**不保留**（timeline 不展示旧 attempt 历史）。
- 校验：目标必须是已到达过的 stage，否则返回可操作错误；被无效范围内存在 active work（未完成 task/check）时**拒绝**操作并提示先 `stop`/`cancel`。
- 成功路径释放当前已持有的、属于目标 stage 范围的 stage lock（等价现有 `rerun` 的锁释放语义），不隐式停止仍在跑的工作。
- 新增 API 端点（如 `POST /api/issues/{number}/rerun-from-stage`，body 指定 stage）和对应 `mo` CLI 命令。
- 现有 `retry` 和 `rerun` 语义不变；本 issue 不合并三者。

无 breaking change。

## Capabilities

### New Capabilities

- `workflow-stage-rerun`: 从一个已到达过的 stage 重新推进后续 workflow 的控制契约——`rerun-from-stage` action 的范围 invalidation 语义（控制面 only）、前置校验（已到达 stage、active-work 拒绝）、运行时变量与外部副作用的保留/不回退边界、stage lock 一致性，以及对应的 grain 方法、HTTP 端点与 `mo` CLI 命令。

### Modified Capabilities

（无。现有 `retry`/`rerun`/`resume` 等 workflow 控制 action 目前没有 spec 覆盖，本变更不改动其语义；本次只新增 `rerun-from-stage` 这一新能力。）

## Impact

- **Server / Domain**（`packages/server/src/Mohist.Server/Workflow/Domain/Run/`）：新增 `RerunFromStage(stageId)` domain 方法及对应校验（已到达 stage、active-work 检测）。
- **Server / Grain**（`packages/server/src/Mohist.Server/Workflow/Grains/`）：`IWorkflowGrain` / `WorkflowGrain` 新增方法，复用 `InitializeFreshStagesAsync` + `Advance()` 路径初始化后续 stage，并按现有 `rerun` 语义释放目标范围内的 stage lock。
- **Server / API**（`packages/server/src/Mohist.Server/Api/IssueRoutes.WorkflowControl.cs`）：新增 `POST /{number}/rerun-from-stage` 端点，body 指定 stage，错误经现有 conflict/bad-request 通道返回可操作信息。
- **CLI**（`packages/cli/Mohist.Cli/`）：新增 `mo` 子命令，与 `retry`/`rerun` 并列。
- **测试**：domain（范围重置、前置校验、变量保留）、grain（active-work 拒绝、锁释放、advance 重新 init）、API、CLI。
- **无持久化迁移**、**无外部副作用回退**；blast radius 限于 control plane。
