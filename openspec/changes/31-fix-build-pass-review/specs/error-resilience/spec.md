## MODIFIED Requirements

### Requirement: Agent catch 块显式处理状态更新异常

Agent 后台 promise 的 catch 块中调用 `stateManager.updateIssueStatus()` SHALL 被显式 try-catch 包裹。

#### Scenario: updateIssueStatus 在 agent catch 中抛出异常
- **WHEN** agent 执行失败进入 catch 块
- **AND** `stateManager.updateIssueStatus()` 调用抛出异常（如 SQLite 锁）
- **THEN** 异常被内层 try-catch 捕获并记录日志
- **AND** 不产生未处理的 Promise rejection

## ADDED Requirements

### Requirement: Build stage all-pass 恢复后自动推进 Review pipeline

当 `recoverBuildStageIssue()` 检测到 `tasks.json` 中所有 task 均 pass 时，系统 SHALL 启动 review stage 的 pipeline（通过 `startPipeline`），使 review agent 正常运行并最终停在 awaiting approval gate 上，而非仅更新 stage 字段后返回。

#### Scenario: Server 重启后恢复已全部 pass 的 Build stage issue
- **WHEN** server 重启执行 `recoverIssues()`
- **AND** issue 状态为 `Active`、stage 为 `Build`、无活跃 agent、无 awaiting approval
- **AND** `tasks.json` 中所有 task 的 `passes` 均为 `true`
- **THEN** 系统 SHALL 调用 `startPipeline()` 启动 review stage
- **AND** issue stage 被推进到 `Review`
- **AND** review agent 正常运行，最终停在 awaiting approval gate 上
- **AND** `pendingGate` 被正确设置

#### Scenario: Start pipeline 失败时的降级处理
- **WHEN** all-pass 分支尝试启动 review pipeline 失败（如并发 agent 满载、`startPipeline` 返回 `{started: false}`）
- **THEN** 系统 SHALL 将 issue 状态设为 `Blocked`
- **AND** 记录失败原因的日志
- **AND** 用户可通过 `reopen` 命令恢复

#### Scenario: Build stage 部分完成时的恢复行为不变
- **WHEN** `tasks.json` 中存在未 pass 的 task
- **THEN** 系统 SHALL 将 issue 状态设为 `Blocked`
- **AND** 不启动任何 pipeline
