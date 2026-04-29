## ADDED Requirements

### Requirement: Review 阶段两段式流程

Review 阶段 SHALL 分为两段：前半段执行 review agent 审查代码并设置 approval gate；后半段在用户审批通过后执行 mergeBack。mergeBack 成功后 issue 才进入 Done 阶段。

#### Scenario: Review 前半段——执行 review agent

- **WHEN** issue 进入 Review 阶段
- **AND** issue 的 `approvalState.status` 不是 `approved`
- **THEN** WorkflowController 执行 review agent（`runPipelineReviewStage`）
- **AND** review 完成后设置 `approvalState.status = awaiting`
- **AND** pipeline 暂停等待用户审批
- **AND** issue 保持在 Review 阶段

#### Scenario: Review 后半段——审批后执行 mergeBack

- **WHEN** issue 在 Review 阶段
- **AND** issue 的 `approvalState.status === 'approved'`
- **THEN** WorkflowController 跳过 review agent
- **AND** 直接执行 `mergeBack()`
- **AND** mergeBack 成功后设置 `mergeState = Merged` + `stage = Done`
- **AND** pipeline 完成

#### Scenario: mergeBack 失败触发冲突解决

- **WHEN** Review 阶段审批后执行 mergeBack
- **AND** mergeBack 失败
- **THEN** 系统 SHALL 在 worktree 中反向 merge master
- **AND** 将 issue 回退到 Build 阶段，`mergeState = Resolving`
- **AND** 启动 agent pipeline 解决冲突
- **AND** issue 保持在 Review 阶段（而非 Done）直到 mergeBack 最终成功

#### Scenario: Resolving 状态完成后的 mergeBack 重试

- **WHEN** issue 的 `mergeState === Resolving` 且 conflict resolution agent 完成
- **AND** pipeline 进入 Review 阶段
- **THEN** 跳过 review agent 和审批
- **AND** 直接执行 mergeBack
- **AND** 成功后设 `stage = Done`
- **AND** 冲突解决最多重试 3 次，超过后标记 `mergeState = Blocked`

### Requirement: Done 阶段是真正的终端状态

Issue 进入 Done 阶段 SHALL 意味着 mergeBack 已成功完成。Done 阶段进入后不需要任何后续操作（merge、build、conflict resolution 等）。

#### Scenario: Done 阶段不需要后续 merge

- **WHEN** issue 的 `stage === Done`
- **THEN** issue 的 `mergeState` SHALL 为 `Merged`
- **AND** 不存在待执行的 mergeBack 操作

#### Scenario: mergeBack 未成功不能进入 Done

- **WHEN** mergeBack 执行失败或未执行
- **THEN** issue SHALL NOT 进入 Done 阶段
- **AND** issue SHALL 保持在 Review 阶段（或回退到 Build 进行冲突解决）

### Requirement: agent_completed 事件不处理 merge

`agent_completed` 事件处理器 SHALL NOT 包含 mergeBack 逻辑。mergeBack 是 Review 阶段 pipeline 内的操作，由 WorkflowController 负责。

#### Scenario: agent 完成后不触发 merge

- **WHEN** agent session 完成，系统 emit `agent_completed` 事件
- **THEN** 事件处理器 SHALL NOT 调用 `worktreeManager.mergeBack()`
- **AND** mergeBack 由 WorkflowController 在 Review 阶段的已审批分支中执行
