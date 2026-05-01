## ADDED Requirements

### Requirement: Reopen check-stage issue with completed tasks sets approval gate directly

When a user reopens an issue in `check` stage where all tasks have passed (`isReviewRecovery=true`), the system SHALL set the approval gate directly instead of re-launching the agent. This avoids re-running the check stage which may hang again.

#### Scenario: Reopen check-stage issue with all tasks passed

- **WHEN** 用户对 `check` 阶段的 Interrupted issue 调用 reopen
- **AND** `isReviewRecovery` 为 `true`（tasks.json 中所有任务状态为 pass）
- **THEN** 系统将 issue status 改为 `Active`
- **THEN** 系统直接设置 `approvalState` 为 pending（不需要重新运行 agent）
- **THEN** 前端显示 Approve 按钮
- **THEN** API 返回 `message` 包含 "reopened with approval gate set" 信息

#### Scenario: Reopen check-stage issue with incomplete tasks

- **WHEN** 用户对 `check` 阶段的 Interrupted issue 调用 reopen
- **AND** `isReviewRecovery` 为 `false`（存在未完成的任务）
- **THEN** 系统按现有逻辑处理（reset to Draft 或 resume pipeline）
