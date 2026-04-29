## MODIFIED Requirements

### Requirement: Agent failure rolls back stage to Draft

当 `executeAgent` 中 agent 执行失败（抛出异常）时，系统 SHALL 在将 issue status 设为 `Blocked` 的同时，将 issue stage 回滚到 `Draft` 并清除 `approval_state`。系统 SHALL 同时写入 blockedReason。

#### Scenario: Agent fails during execution

- **WHEN** `executeAgent` 内部的 `runMainAgent` 抛出异常
- **THEN** 系统将 issue status 改为 `Blocked`
- **THEN** 系统将 issue stage 回滚到 `Draft`
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除残留的 pending approval
- **THEN** 系统写入 blockedReason（如 "Agent 在 {stage} 阶段失败：{error message}"）
- **THEN** 前端可以看到 stage=Draft + status=Blocked，使用 retry 或 restart 按钮

#### Scenario: Agent succeeds but needs pause

- **WHEN** `executeAgent` 成功完成且需要等待审批
- **THEN** stage 保持当前值不变（不回滚），status 保持 `Active`
- **THEN** session 进入 paused 状态

### Requirement: Reopen with paused session auto-resumes agent

当 issue 被 reopen 且内存中存在该 issue 的 pausedSession 时，系统 SHALL 在确认 agent 未运行后自动调用 `agentRunner.resume()` 恢复 agent 执行，无需用户额外操作。Reopen 同时 SHALL 清除 blockedReason 并重置 retryCount。

#### Scenario: Reopen issue with paused session available

- **WHEN** 用户对 Blocked 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `true`
- **AND** `agentRunner.isRunning(issue.id)` 返回 `false`
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统清除 blockedReason
- **THEN** 系统重置 retryCount 为 0
- **THEN** 系统自动调用 `agentRunner.resume()` 恢复 agent 执行
- **THEN** API 返回 `message` 包含 "reopened and resumed" 信息

### Requirement: Reopen without paused session resets stage to Draft

当 issue 被 reopen 但内存中无 pausedSession 时（如 server 重启后），系统 SHALL 将 issue stage 重置为 `Draft`，status 改为 `Active`，清除 blockedReason、retryCount 和数据库中残留的 `approval_state`，允许用户通过 `start` 重新发起流程。

#### Scenario: Reopen issue after server restart

- **WHEN** 用户对 Blocked 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `false`
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统将 issue stage 重置为 `Draft`
- **THEN** 系统清除 blockedReason
- **THEN** 系统重置 retryCount 为 0
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除 pending approval
- **THEN** API 返回 `message` 包含 "reopened and reset to draft, use start to begin again" 信息

#### Scenario: Reopen issue in Done stage without paused session

- **WHEN** 用户对 stage 为 `Done` 的 Blocked issue 调用 reopen
- **AND** 无 pausedSession
- **THEN** 系统将 stage 重置为 `Draft`，status 改为 `Active`
- **THEN** 系统清除数据库中的 `approval_state`、blockedReason、retryCount
- **THEN** 用户可以通过 `start` 重新发起完整流程
