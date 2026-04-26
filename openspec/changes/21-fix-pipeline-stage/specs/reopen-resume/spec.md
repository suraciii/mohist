## ADDED Requirements

### Requirement: IssueStatus 新增 Interrupted 枚举值

`IssueStatus` 枚举 SHALL 新增 `Interrupted = 'interrupted'` 值，用于标记因服务器重启等原因被中断的 pipeline issue。

#### Scenario: Interrupted 枚举值

- **WHEN** 系统检查 `IssueStatus` 枚举
- **THEN** 枚举包含 `interrupted` 值
- **AND** 该值在数据库中可正常读写

### Requirement: recoverIssues 将无 awaiting 的 issue 标记为 Interrupted

`recoverIssues()` SHALL 将非 awaiting 状态的孤儿 issue 标记为 `Interrupted`（而非 `Blocked`），保留 stage 和 checkpoint 不变。

#### Scenario: 非 awaiting 的孤儿 issue 标记为 Interrupted

- **WHEN** `recoverIssues()` 发现一个 status=active、stage≠draft 的 issue
- **AND** 该 issue 无 `approvalState.status === 'awaiting'`
- **THEN** 系统将该 issue status 改为 `Interrupted`
- **AND** issue stage 保持不变
- **AND** 不清除 checkpoint 数据
- **AND** 日志记录 `{ action: 'status=interrupted, stage preserved, checkpoint preserved' }`

#### Scenario: awaiting 的 issue 行为不变

- **WHEN** `recoverIssues()` 发现一个 `approvalState.status === 'awaiting'` 的 issue
- **THEN** 系统恢复 pendingGate（与现有行为一致）
- **AND** status 保持 `Active`

## MODIFIED Requirements

### Requirement: Reopen without paused session resets stage to Draft

当 issue 被 reopen 但内存中无 pausedSession 时（如 server 重启后），系统 SHALL 根据 issue 的当前 status 决定恢复行为：`Interrupted` 状态的 issue 使用 checkpoint 从中断点恢复；`Blocked` 状态的 issue 重置到 Draft。

#### Scenario: Reopen Interrupted issue with checkpoint resumes from interruption point

- **WHEN** 用户对 `Interrupted` 状态的 issue 调用 reopen
- **AND** 内存中无 pausedSession
- **AND** `pipeline_checkpoint` 存在该 issue 的记录
- **THEN** 系统将 issue status 改为 `Active`
- **AND** issue stage 保持不变（使用 checkpoint 中的 stage）
- **AND** 系统调用 `resumePipeline()` 恢复执行
- **AND** pipeline 从 checkpoint 记录的 `next_step` 继续执行
- **AND** API 返回 `message` 包含 "reopened and resuming from <stage>:<next_step>" 信息

#### Scenario: Reopen Interrupted issue without checkpoint resets to Draft

- **WHEN** 用户对 `Interrupted` 状态的 issue 调用 reopen
- **AND** 内存中无 pausedSession
- **AND** `pipeline_checkpoint` 无该 issue 的记录
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统将 issue stage 重置为 `Draft`
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除 pending approval
- **THEN** API 返回 `message` 包含 "reopened and reset to draft, use start to begin again" 信息

#### Scenario: Reopen Blocked issue without paused session resets to Draft

- **WHEN** 用户对 `Blocked` 状态的 issue 调用 reopen
- **AND** 内存中无 pausedSession
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统将 issue stage 重置为 `Draft`
- **THEN** 系统调用 `issueRepo.clearApprovalState(issue.id)` 清除 pending approval
- **THEN** API 返回 `message` 包含 "reopened and reset to draft, use start to begin again" 信息

#### Scenario: Reopen issue in Done stage without paused session

- **WHEN** 用户对 stage 为 `Done` 的 issue 调用 reopen
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

#### Scenario: Reopen Interrupted issue with agentRunner resumes pipeline

- **WHEN** 用户对 `Interrupted` 状态的 issue 调用 reopen
- **AND** `agentRunner` 可用
- **AND** `agentRunner.isRunning(issue.id)` 返回 `false`
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统调用 `agentRunner.resumePipeline()` 恢复 pipeline
- **AND** pipeline 从 checkpoint 记录的位置继续

### Requirement: Reopen with paused session auto-resumes agent

当 issue 被 reopen 且内存中存在该 issue 的 pausedSession 时，系统 SHALL 在确认 agent 未运行后自动调用 `agentRunner.resume()` 恢复 agent 执行，无需用户额外操作。

#### Scenario: Reopen issue with paused session available

- **WHEN** 用户对 Blocked 或 Interrupted 的 issue 调用 reopen
- **AND** `agentRunner.hasPausedSession(issueNumber)` 返回 `true`
- **AND** `agentRunner.isRunning(issue.id)` 返回 `false`
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统自动调用 `agentRunner.resume()` 恢复 agent 执行
- **THEN** API 返回 `message` 包含 "reopened and resumed" 信息

### Requirement: Frontend displays Interrupted status

前端 SHALL 识别并展示 `interrupted` 状态的 issue，提供恢复引导提示。

#### Scenario: Kanban 显示 Interrupted 状态

- **WHEN** issue status 为 `interrupted`
- **THEN** Kanban 卡片显示 "Interrupted" 标签
- **AND** 显示 "Pipeline was interrupted, click to resume" 提示
- **AND** 提供 reopen/resume 操作按钮

#### Scenario: Issue detail page 显示中断信息

- **WHEN** 用户打开 `interrupted` 状态的 issue 详情页
- **THEN** 页面显示中断时的 stage 信息
- **AND** 显示已完成和待完成的子步骤列表（来自 checkpoint）
- **AND** 提供 "Resume pipeline" 按钮
