## ADDED Requirements

### Requirement: pipeline_checkpoint table persists stage sub-step progress

系统 SHALL 维护 `pipeline_checkpoint` 表，记录每个 issue 在当前 stage 内的子步骤完成状态。每条记录包含 `issue_number`、`stage`、`completed_steps`（JSON 数组）、`next_step` 和 `updated_at`。

#### Scenario: 写入初始 checkpoint

- **WHEN** pipeline 进入某个 stage 的第一个子步骤
- **THEN** 系统在 `pipeline_checkpoint` 表中插入一条记录
- **AND** `completed_steps` 为空数组 `[]`
- **AND** `next_step` 为第一个子步骤名称

#### Scenario: 子步骤完成后更新 checkpoint

- **WHEN** plan stage 的某个 round（如 `proposal`）成功完成且 `verify()` 通过
- **THEN** 系统将 `proposal` 追加到 `completed_steps` 数组
- **AND** `next_step` 更新为下一个 round 名称（如 `specs`）
- **AND** `updated_at` 更新为当前时间

#### Scenario: build stage 的 checkpoint 记录已完成的 tasks

- **WHEN** build stage 中某个 task 被 RalphExecutor 标记为 passed
- **THEN** 系统将该 task ID 追加到 checkpoint 的 `completed_steps`
- **AND** `next_step` 更新为下一个待执行的 task ID 或 `done`

#### Scenario: 同一 issue + stage 只有一条 checkpoint 记录

- **WHEN** 系统更新 checkpoint
- **THEN** 使用 UPSERT 语义（INSERT ON CONFLICT UPDATE）
- **AND** 同一 `issue_number` + `stage` 组合始终只有一条记录

### Requirement: Checkpoint 在 stage 完成时清除

系统 SHALL 在 stage 正常完成或 issue 完成时删除对应的 checkpoint 记录。

#### Scenario: plan stage 完成清除 checkpoint

- **WHEN** `runPlanStage()` 返回 `success: true`
- **THEN** 系统删除 `issue_number` 对应的 `pipeline_checkpoint` 中 `stage = 'plan'` 的记录

#### Scenario: build stage 完成清除 checkpoint

- **WHEN** `runPipelineBuildStage()` 返回 `success: true`
- **THEN** 系统删除 `issue_number` 对应的 `pipeline_checkpoint` 中 `stage = 'build'` 的记录

#### Scenario: issue 完成（done）清除所有 checkpoint

- **WHEN** issue stage 变为 `done`
- **THEN** 系统删除该 issue 的所有 checkpoint 记录

### Requirement: Plan stage 从 checkpoint 恢复跳过已完成的 round

`runPlanStage()` SHALL 在执行 round 循环前检查 checkpoint，跳过 `completed_steps` 中已记录的 round，从 `next_step` 对应的 round 开始执行。

#### Scenario: 从 checkpoint 恢复跳过已完成的 rounds

- **WHEN** `runPlanStage()` 被调用
- **AND** `pipeline_checkpoint` 存在 `completed_steps: ["proposal", "specs"]`，`next_step: "design"`
- **THEN** 系统跳过 proposal 和 specs round
- **AND** 从 design round 开始执行
- **AND** 不调用 `buildArtifactPrompt` 或 `conn.prompt` 给已完成的 round

#### Scenario: checkpoint 无记录时正常从头执行

- **WHEN** `runPlanStage()` 被调用
- **AND** `pipeline_checkpoint` 中无该 issue 的 plan stage 记录
- **THEN** 系统按现有行为从头执行所有 rounds（proposal → specs → design → tasks → self-review）
- **AND** 在第一个 round 开始前写入初始 checkpoint

#### Scenario: checkpoint 指向的 round 的 artifact 已存在时跳过

- **WHEN** `runPlanStage()` 准备执行某个 round
- **AND** 该 round 的 `verify()` 返回 true（artifact 已存在于磁盘）
- **THEN** 系统仍将其标记为已完成并更新 checkpoint
- **AND** 跳过对该 round 的 `conn.prompt` 调用

#### Scenario: 恢复时不调用 cleanChangeDir

- **WHEN** `runPlanStage()` 检测到存在有效 checkpoint（非空 `completed_steps`）
- **THEN** 系统不调用 `cleanChangeDir()`
- **AND** 保留磁盘上已存在的 artifact 文件

### Requirement: Build stage 从 checkpoint 恢复跳过已完成的 tasks

`runPipelineBuildStage()` SHALL 在 RalphExecutor 执行前检查 checkpoint，将已完成的 tasks 传递给 executor 以跳过。

#### Scenario: build stage 从 checkpoint 恢复

- **WHEN** `runPipelineBuildStage()` 被调用
- **AND** `pipeline_checkpoint` 存在 `completed_steps` 包含已通过的 task ID 列表
- **THEN** RalphExecutor 收到已完成的 task 列表
- **AND** RalphExecutor 跳过这些 tasks，仅执行剩余 tasks

#### Scenario: build stage 无 checkpoint 正常执行

- **WHEN** `runPipelineBuildStage()` 被调用
- **AND** 无该 issue 的 build stage checkpoint
- **THEN** RalphExecutor 按现有行为执行所有 tasks
- **AND** 在执行过程中逐步写入 checkpoint

### Requirement: Checkpoint repo 提供读写删除接口

系统 SHALL 提供 `PipelineCheckpointRepo` 类，封装 `pipeline_checkpoint` 表的 CRUD 操作。

#### Scenario: 读取 checkpoint

- **WHEN** 调用 `checkpointRepo.get(issueNumber, stage)`
- **THEN** 返回该记录的 `{ completedSteps, nextStep, updatedAt }` 或 `null`

#### Scenario: 写入/更新 checkpoint

- **WHEN** 调用 `checkpointRepo.upsert(issueNumber, stage, completedSteps, nextStep)`
- **THEN** 记录被插入或更新
- **AND** `updated_at` 设为当前时间

#### Scenario: 删除单个 stage checkpoint

- **WHEN** 调用 `checkpointRepo.delete(issueNumber, stage)`
- **THEN** 对应记录被删除

#### Scenario: 删除 issue 所有 checkpoint

- **WHEN** 调用 `checkpointRepo.deleteAll(issueNumber)`
- **THEN** 该 issue 的所有 stage checkpoint 记录被删除
