## ADDED Requirements

### Requirement: Manual rebase 冲突自动触发 coder agent 解决

`POST /:number/rebase` 在 Plan/Build/Review 阶段遇到 rebase 冲突时，SHALL 保留 rebase 中间状态（`abortOnConflict: false`），返回 202，并异步触发 coder agent 自动解决冲突。无冲突时行为 SHALL 保持不变（同步返回 200）。

#### Scenario: Rebase 遇到冲突 — 异步触发 agent
- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue stage 为 Plan、Build 或 Review
- **AND** rebase 产生冲突
- **THEN** 返回 202，body 为 `{ success: true, data: { status: "resolving-conflicts", conflicts: [...] } }`
- **AND** rebase 中间状态被保留（不 abort）
- **AND** 异步触发 coder agent 解决冲突（不 await）

#### Scenario: Rebase 无冲突 — 行为不变
- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** rebase 无冲突（clean rebase 或 already up to date）
- **THEN** 同步返回 200（与当前行为完全一致）

### Requirement: 冲突解决复用已有 ACP 基础设施

冲突解决 SHALL 复用 MergeQueue 已有的 `buildConflictResolutionPrompt` + `createAcpConnection` + `conflict-resolution.md` 基础设施。`resolveConflicts` 闭包 SHALL 被提取为独立共享函数，供 manual rebase 和 MergeQueue 共用。

#### Scenario: 共享函数被两处调用
- **WHEN** manual rebase 或 MergeQueue 需要解决冲突
- **THEN** 两者调用同一个提取后的共享冲突解决函数
- **AND** 共享函数使用 `buildConflictResolutionPrompt` 构建 prompt
- **AND** 共享函数通过 `createAcpConnection` 连接 agent

### Requirement: 冲突解决成功后执行 stage-specific post-rebase handler

Agent 冲突解决成功后 SHALL 执行与无冲突路径相同的 stage-specific post-rebase handler。

#### Scenario: Plan 阶段冲突解决成功
- **WHEN** Plan 阶段 rebase 冲突被 agent 成功解决
- **THEN** 注入 re-evaluate 消息并调用 `resumePipeline`（与 `handlePlanRebase` 一致）

#### Scenario: Build 阶段冲突解决成功
- **WHEN** Build 阶段 rebase 冲突被 agent 成功解决
- **THEN** 清除 build checkpoint（与 `handleBuildRebase` 一致）

#### Scenario: Review 阶段冲突解决成功 — 跳过 build verify
- **WHEN** Review 阶段 rebase 冲突被 agent 成功解决
- **THEN** 跳过 build verify（因 `conflict-resolution.md` prompt 已要求 agent 在解决冲突后跑 `npm run build` 验证）

#### Scenario: Review 阶段无冲突 rebase — build verify 不受影响
- **WHEN** Review 阶段 rebase 无冲突
- **THEN** 仍然执行 build verify（与当前行为一致）

### Requirement: 冲突解决失败的降级处理

Agent 冲突解决失败时 SHALL abort rebase 并通过 SSE 事件通知 UI。

#### Scenario: Agent 冲突解决失败
- **WHEN** coder agent 冲突解决失败（ACP session 错误、agent 返回失败等）
- **THEN** 执行 `git rebase --abort`
- **AND** emit `rebase_conflict` 事件，payload 包含 `{ issueId, projectId, issueNumber, status: "failed", error }`
- **AND** 不执行 stage-specific post-rebase handler

### Requirement: 冲突解决过程产生完整 SSE 事件序列

Manual rebase 冲突解决过程 SHALL 通过 SSE 推送完整事件序列，供 UI 实时展示进度。

#### Scenario: 冲突解决 SSE 事件序列
- **WHEN** manual rebase 检测到冲突并开始 agent 解决
- **THEN** SSE 按序推送：
  1. `rebase_conflict` `{ conflicts: [...], status: "resolving" }`
  2. `agent_conflict_resolution_started` `{ issueId, projectId, issueNumber, conflictFiles }`
  3. agent 工作过程中的 `coder_text_chunk` / `coder_tool_call` 等事件
  4. `agent_conflict_resolution_completed` 或 `agent_conflict_resolution_failed`
  5. `rebase_progress` `{ step: "post-rebase" }`（如需 stage-specific 处理）
  6. `rebase_completed` `{ rebased: true }`

### Requirement: Done 阶段不受影响

Done 阶段的 rebase SHALL 继续走 MergeQueue 路径，不受此变更影响。

#### Scenario: Done 阶段 rebase 走 MergeQueue
- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue stage 为 Done
- **THEN** 走 `mergeQueue.retry()` 路径（与当前行为一致）
- **AND** 不触发 manual rebase 的冲突解决逻辑
