## MODIFIED Requirements

### Requirement: Reopen without paused session resets stage to Draft

当 issue 被 reopen 但内存中无 pausedSession 时（如 server 重启后），系统 SHALL 检查 issue 的当前 stage。如果 stage 为 `review` 且该 stage 是由 `recoverIssues()` 自动推进的（build 全部完成后的智能恢复），系统 SHALL 保持 `review` stage 不变，仅将 status 改为 `Active`。否则，系统 SHALL 将 issue stage 重置为 `Draft`，status 改为 `Active`，并清除数据库中残留的 `approval_state`，允许用户通过 `start` 重新发起流程。

#### Scenario: Reopen issue after server restart — build-stage smart recovery preserved

- **WHEN** 用户对 Blocked 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `false`
- **AND** issue 的 stage 为 `review`（由 recoverIssues 从 build 智能推进）
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统 SHALL 保持 stage 为 `review` 不重置
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除 pending approval
- **THEN** API 返回 `message` 包含 "reopened at review stage, use start to continue" 信息

#### Scenario: Reopen issue after server restart — standard case

- **WHEN** 用户对 Blocked 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `false`
- **AND** issue 的 stage 不是由智能恢复推进的 review
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
