# OpenSpec Capability: reopen-resume

### Requirement: Agent failure rolls back stage to Draft

当 `executeAgent` 中 agent 执行失败（抛出异常）时，系统 SHALL 在将 issue status 设为 `Blocked` 的同时，将 issue stage 回滚到 `Draft` 并清除 `approval_state`。

#### Scenario: Agent fails during execution

- **WHEN** `executeAgent` 内部的 `runMainAgent` 抛出异常
- **THEN** 系统将 issue status 改为 `Blocked`
- **THEN** 系统将 issue stage 回滚到 `Draft`
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除残留的 pending approval
- **THEN** 前端可以看到 stage=Draft + status=Blocked，reopen 后 Start 按钮立即可用

#### Scenario: Agent succeeds but needs pause

- **WHEN** `executeAgent` 成功完成且需要等待审批
- **THEN** stage 保持当前值不变（不回滚），status 保持 `Active`
- **THEN** session 进入 paused 状态

### Requirement: Reopen with paused session auto-resumes agent

当 issue 被 reopen 且内存中存在该 issue 的 pausedSession 时，系统 SHALL 在确认 agent 未运行后自动调用 `agentRunner.resume()` 恢复 agent 执行，无需用户额外操作。

#### Scenario: Reopen issue with paused session available

- **WHEN** 用户对 Blocked 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `true`
- **AND** `agentRunner.isRunning(issue.id)` 返回 `false`
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统自动调用 `agentRunner.resume()` 恢复 agent 执行
- **THEN** API 返回 `message` 包含 "reopened and resumed" 信息

### Requirement: Reopen without paused session resets stage to Draft

当 issue 被 reopen 但内存中无 pausedSession 时（如 server 重启后），系统 SHALL 将 issue stage 重置为 `Draft`，status 改为 `Active`，并清除数据库中残留的 `approval_state`，允许用户通过 `start` 重新发起流程。

#### Scenario: Reopen issue after server restart

- **WHEN** 用户对 Blocked 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `false`
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统将 issue stage 重置为 `Draft`
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除 pending approval
- **THEN** API 返回 `message` 包含 "reopened and reset to draft, use start to begin again" 信息

#### Scenario: Reopen issue in Done stage without paused session

- **WHEN** 用户对 stage 为 `Done` 的 Blocked issue 调用 reopen
- **AND** 无 pausedSession
- **THEN** 系统将 stage 重置为 `Draft`，status 改为 `Active`
- **THEN** 系统清除数据库中的 `approval_state`
- **THEN** 用户可以通过 `start` 重新发起完整流程

### Requirement: Reopen requires agentRunner availability

reopen 端点 SHALL 检查 agentRunner 是否可用，不可用时仅执行 status 恢复。

#### Scenario: Reopen when agentRunner not configured

- **WHEN** 用户调用 reopen 且 `agentRunner` 为 null/undefined
- **THEN** 系统仅将 issue status 改为 `Active`，不尝试 resume 或重置 stage
- **THEN** API 正常返回 reopened 信息

### Requirement: recovery-verbs-contract

Issue recovery verbs SHALL be intent-specific. `reopen` SHALL only reopen a closed issue, while `resume` SHALL recover paused or interrupted work without changing stage or clearing checkpoints.

#### Scenario: Reopen closed issue

- **WHEN** the user invokes reopen for an issue with status `closed`
- **THEN** the system sets the issue status to `active`
- **AND** the current stage remains unchanged
- **AND** the system does not auto-reset the issue to draft or backlog

#### Scenario: Reopen rejected for non-closed issue

- **WHEN** the user invokes reopen for an issue with status `blocked`, `paused`, or `interrupted`
- **THEN** the request is rejected
- **AND** the error explains that reopen is only for closed issues

#### Scenario: Resume paused issue

- **WHEN** the user invokes resume for an issue with status `paused`
- **THEN** the system sets the issue status to `active`
- **AND** the current stage remains unchanged
- **AND** existing checkpoints are preserved

#### Scenario: Resume interrupted issue

- **WHEN** the user invokes resume for an issue with status `interrupted`
- **THEN** the system sets the issue status to `active`
- **AND** the current stage remains unchanged
- **AND** existing checkpoints are preserved

#### Scenario: Resume rejected for failed issue

- **WHEN** the user invokes resume for an issue whose current problem is a failed or needs-action state rather than paused/interrupted recovery
- **THEN** the request is rejected
- **AND** the error directs the user to retry, rerun, or rewind instead of resume

