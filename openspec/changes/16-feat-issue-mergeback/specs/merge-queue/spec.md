## ADDED Requirements

### Requirement: MergeQueue 串行处理 issue 合并

MergeQueue SHALL 作为全局串行队列，issue 完成后自动入队，逐个执行 mergeBack，确保同一时刻只有一个合并操作在执行。

#### Scenario: issue 完成后自动入队

- **WHEN** `agent_completed` 事件触发（issue stage 到达 `done`）
- **AND** issue 存在对应的 worktree
- **THEN** MergeQueue 将该 issue 入队
- **AND** issue 的 `mergeState` 设置为 `pending`
- **AND** EventBus emit `merge_queued` 事件，payload 包含 `{ issueId, issueNumber, projectId }`

#### Scenario: 队列为空时立即开始处理

- **WHEN** issue 入队时队列无正在处理的条目
- **THEN** MergeQueue 立即开始处理该条目
- **AND** issue 的 `mergeState` 设置为 `merging`

#### Scenario: 队列忙时排队等待

- **WHEN** issue 入队时队列正在处理另一个条目
- **THEN** 该 issue 状态保持 `pending`
- **AND** 当前条目处理完成后自动开始处理下一个 pending 条目

#### Scenario: 无 worktree 的 issue 不入队

- **WHEN** `agent_completed` 事件触发
- **AND** issue 不存在对应的 worktree
- **THEN** MergeQueue 不入队该 issue
- **AND** issue 的 `mergeState` 保持为空

#### Scenario: 重复入队幂等

- **WHEN** 同一 issue number 已经在队列中（pending 或 merging 状态）
- **THEN** MergeQueue SHALL NOT 重复入队
- **AND** 日志记录 "Issue #N already in merge queue"

### Requirement: MergeQueue 提供状态查询

MergeQueue SHALL 提供当前队列状态的查询接口，返回所有队列条目及其状态。

#### Scenario: 查询队列状态

- **WHEN** 调用 `mergeQueue.getStatus()`
- **THEN** 返回队列中所有条目列表
- **AND** 每个条目包含 `{ issueNumber, projectId, mergeState, message?, enqueuedAt }`
- **AND** 条目按入队时间排序（最早的在前）

#### Scenario: 空队列返回空列表

- **WHEN** 调用 `mergeQueue.getStatus()`
- **AND** 队列中无任何条目
- **THEN** 返回空数组

### Requirement: MergeQueue 处理完成后清理 worktree

MergeQueue 在 mergeBack 成功后 SHALL 清理 issue 的 worktree 和分支。

#### Scenario: 合并成功后清理

- **WHEN** mergeBack 返回 success
- **AND** 构建验证通过
- **THEN** MergeQueue 调用 WorktreeManager.remove() 清理 worktree
- **AND** issue 的 `mergeState` 设置为 `merged`
- **AND** EventBus emit `merge_completed` 事件

#### Scenario: 合并冲突不清理

- **WHEN** mergeBack 返回 conflict 错误
- **THEN** worktree 保留
- **AND** issue 的 `mergeState` 设置为 `conflict`
- **AND** EventBus emit `merge_failed` 事件，payload 包含 `{ issueNumber, reason: 'conflict', message }`

#### Scenario: 构建失败回滚不清理

- **WHEN** mergeBack 成功但构建验证失败
- **THEN** 执行 `git reset --hard HEAD~1` 回滚合并
- **AND** worktree 保留
- **AND** issue 的 `mergeState` 设置为 `build-failed`
- **AND** EventBus emit `merge_failed` 事件，payload 包含 `{ issueNumber, reason: 'build-failed', message }`

### Requirement: MergeQueue 支持重试失败的合并

MergeQueue SHALL 支持对失败条目（`conflict` 或 `build-failed`）进行重新入队。

#### Scenario: 重试失败的合并

- **WHEN** 调用 `mergeQueue.retry(issueNumber)`
- **AND** 该 issue 的 `mergeState` 为 `conflict` 或 `build-failed`
- **THEN** issue 重新入队，`mergeState` 设置为 `pending`
- **AND** 当队列空闲时开始处理

#### Scenario: 重试不在失败状态的 issue

- **WHEN** 调用 `mergeQueue.retry(issueNumber)`
- **AND** 该 issue 的 `mergeState` 不是 `conflict` 也不是 `build-failed`（如 `pending`、`merging`、`merged` 或空）
- **THEN** 返回错误，不重新入队

### Requirement: Issue 类型包含 mergeState 字段

Issue 模型 SHALL 包含 `mergeState` 字段，记录合并队列中的状态。

#### Scenario: mergeState 可选值

- **WHEN** Issue 的 mergeState 被读取
- **THEN** 可选值为 `pending`、`merging`、`merged`、`build-failed`、`conflict` 或 `undefined`
- **AND** 未进入过合并队列的 issue 的 mergeState 为 `undefined`

#### Scenario: 数据库持久化 mergeState

- **WHEN** issue 的 mergeState 更新
- **THEN** 新值写入 issues 表的 `merge_state` 列
- **AND** 数据库 schema version 递增

### Requirement: MergeQueue 服务端启动时恢复状态

Server 重启时 MergeQueue SHALL 从数据库恢复未完成的合并任务。

#### Scenario: 恢复 merging 状态的条目

- **WHEN** server 启动
- **AND** 数据库中存在 `mergeState = 'merging'` 的 issue
- **THEN** 将这些 issue 重新入队，`mergeState` 重置为 `pending`
- **AND** 日志记录恢复的条目数量

#### Scenario: 恢复 pending 状态的条目

- **WHEN** server 启动
- **AND** 数据库中存在 `mergeState = 'pending'` 的 issue
- **THEN** 将这些 issue 保留在队列中
- **AND** 当队列空闲时自动开始处理
