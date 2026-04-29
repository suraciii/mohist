## ADDED Requirements

### Requirement: Server restart 时自动重试瞬态故障

当 `recoverIssues()` 检测到 build stage 的 orphaned issue 且有部分 task 完成时，系统 SHALL 先尝试自动重试（恢复 pipeline 执行），而非直接标记 blocked。重试最多 3 次（每次 server 重启最多重试一次，retryCount 持久化跨重启累计）。

#### Scenario: 部分任务完成时自动重试

- **WHEN** server 重启检测到 issue 在 build stage，status 为 active
- **AND** tasks.json 中部分 task 已 passes=true
- **AND** issue 的 retryCount < 3
- **THEN** 系统递增 retryCount
- **THEN** 系统写入 blockedReason（如 "Build 中断 — 完成了 2/8 个任务，正在自动重试 (第 2/3 次)"）
- **THEN** 系统从断点恢复 pipeline（保留已完成的 task 进度）
- **THEN** issue status 保持 active

#### Scenario: 重试次数耗尽后标记 blocked

- **WHEN** issue 的 retryCount 已达到 3
- **THEN** 系统将 issue status 设为 blocked
- **THEN** 系统写入 blockedReason（如 "Build 中断 — 完成了 2/8 个任务，已自动重试 3 次仍失败，需要人工介入"）
- **THEN** 系统通过 EventBus emit `agent_blocked` 事件

#### Scenario: 不可重试的故障直接标记 blocked

- **WHEN** recover 逻辑检测到的故障属于不可重试类型（project 不存在、worktree 不存在、tasks.json 缺失或损坏）
- **THEN** 系统直接标记 blocked，不消耗重试次数
- **THEN** 系统写入对应的 blockedReason

### Requirement: Pipeline 执行失败时写入 blockedReason

当 pipeline 执行过程中抛出异常或返回未完成结果时（非 gate 审批场景），系统 SHALL 写入人话格式的 blockedReason 并 emit agent_blocked 事件。pipeline 执行中不自动重试（由 ralph executor 内部处理循环），仅在 server restart recover 路径自动重试。

#### Scenario: pipeline 执行失败写入 blockedReason

- **WHEN** pipeline 执行中抛出异常或返回未完成结果
- **THEN** 系统将 issue status 设为 blocked
- **THEN** 系统写入 blockedReason（如 "Agent 在 {stage} 阶段失败：{error message}"）
- **THEN** 系统 emit agent_blocked 事件

#### Scenario: gate 审批不触发重试

- **WHEN** pipeline 正常完成并进入 gate 等待审批
- **THEN** 系统不触发自动重试
- **AND** issue status 保持 active

### Requirement: 重试计数持久化

retryCount SHALL 存储在 issue 记录中，server 重启后仍可读取。

#### Scenario: 重试计数存储

- **WHEN** 系统执行一次自动重试
- **THEN** issue 的 retryCount 字段递增 1
- **AND** 该值被持久化到数据库

#### Scenario: 重试计数在成功后重置

- **WHEN** issue 的 pipeline 成功推进到下一个 stage
- **THEN** retryCount 重置为 0

#### Scenario: 重试计数在用户 retry 时重置

- **WHEN** 用户通过 retry API 手动触发重试
- **THEN** retryCount 重置为 0
