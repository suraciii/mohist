## ADDED Requirements

### Requirement: Build/Plan/Review 阶段 rebase 冲突自动解决

`POST /:number/rebase` 在 Build/Plan/Review 阶段遇到 rebase 冲突时，SHALL 自动 spawn coder agent 通过 ACP 协议解决冲突，而非直接 abort 返回 409。

#### Scenario: Build 阶段 rebase 冲突自动解决成功

- **WHEN** 用户请求 `POST /:number/rebase` 且 issue stage 为 Build
- **AND** `rebaseOntoMaster` 检测到冲突（返回 `{ success: false, conflicts: [...] }`）
- **AND** `abortOnConflict` 为 `false`（rebase 中间状态保留）
- **THEN** 系统 emit `rebase_conflict` 事件（status: resolving）
- **AND** 系统 spawn coder agent via ACP 解决冲突文件
- **AND** agent 解决成功后执行 `rebase --continue`
- **AND** 清除 build checkpoint（复用 handleBuildRebase 逻辑）
- **AND** emit `rebase_completed` 事件
- **AND** 返回 200 `{ success: true, data: { rebased: true, message: "Rebase successful, checkpoint cleared, resume pipeline to rebuild", autoResolved: true } }`

#### Scenario: Plan 阶段 rebase 冲突自动解决成功

- **WHEN** 用户请求 `POST /:number/rebase` 且 issue stage 为 Plan
- **AND** `rebaseOntoMaster` 检测到冲突
- **THEN** 系统 spawn coder agent 解决冲突
- **AND** agent 解决成功后执行 `rebase --continue`
- **AND** 触发 plan re-self-review（注入 comment + resumePipeline）
- **AND** 返回 200 `{ success: true, data: { rebased: true, autoResolved: true } }`

#### Scenario: Review 阶段 rebase 冲突自动解决成功

- **WHEN** 用户请求 `POST /:number/rebase` 且 issue stage 为 Review
- **AND** `rebaseOntoMaster` 检测到冲突
- **THEN** 系统 spawn coder agent 解决冲突
- **AND** agent 解决成功后执行 `rebase --continue`
- **AND** 执行 build verification（复用 handleReviewRebase 逻辑）
- **AND** 返回 200 `{ success: true, data: { rebased: true, buildPassed: boolean, autoResolved: true } }`

#### Scenario: Agent 冲突解决失败时降级

- **WHEN** rebase 冲突检测到且 agent 被启动解决冲突
- **AND** agent 返回 `{ success: false }` 或超时或异常
- **THEN** 系统 执行 `rebase --abort` 清理 rebase 中间状态
- **AND** emit `rebase_conflict` 事件（status: failed，包含冲突文件列表）
- **AND** 返回 409 `{ success: false, error: "Rebase aborted: agent failed to resolve conflicts", data: { rebased: false, conflicts: [...], autoResolved: false } }`

#### Scenario: Done 阶段不受影响

- **WHEN** 用户请求 `POST /:number/rebase` 且 issue stage 为 Done
- **THEN** 走 Merge Queue retry 路径，不涉及本次新增的自动解决逻辑
- **AND** Merge Queue 的 resolveConflicts 回调独立调用，不受影响

### Requirement: resolveConflicts 回调注入到 issues API

`createIssueRoutes` SHALL 接收可选的 `resolveConflicts` 回调参数，用于在 rebase 冲突时调用 coder agent 自动解决。回调签名与 MergeQueue 的 resolveConflicts 一致。

#### Scenario: resolveConflicts 回调被注入

- **WHEN** `server/index.ts` 调用 `createIssueRoutes`
- **THEN** 传入 `resolveConflicts` 回调
- **AND** 回调逻辑复用 `buildConflictResolutionPrompt` + `createAcpConnection`

#### Scenario: resolveConflicts 未注入时保持原有行为

- **WHEN** `resolveConflicts` 未传入（为 undefined）
- **AND** rebase 遇到冲突
- **THEN** 执行 `rebase --abort` 并返回 409（与当前行为一致）

### Requirement: rebase 冲突解决 SSE 事件通知

系统 SHALL 在 rebase 冲突自动解决过程中通过 SSE 发送状态通知，使 UI 能实时展示进度。

#### Scenario: 冲突检测时发送 resolving 状态

- **WHEN** rebase 检测到冲突且开始 agent 解决
- **THEN** emit `rebase_conflict` 事件，payload 包含 `{ issueId, projectId, issueNumber, conflicts: string[], status: 'resolving' }`

#### Scenario: 冲突解决完成后发送 completed 事件

- **WHEN** agent 成功解决冲突且 `rebase --continue` 完成
- **THEN** emit `rebase_completed` 事件（与现有事件一致）

#### Scenario: 冲突解决失败时发送失败事件

- **WHEN** agent 解决冲突失败
- **THEN** emit `rebase_conflict` 事件，payload 包含 `{ issueId, projectId, issueNumber, conflicts: string[], status: 'failed' }`
