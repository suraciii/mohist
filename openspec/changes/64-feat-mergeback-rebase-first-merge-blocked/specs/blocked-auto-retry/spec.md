## ADDED Requirements

### Requirement: MergeQueue 自动重试 blocked 状态的 issue

MergeQueue SHALL 在 server 启动时启动一个定时检查器，周期性检测 `mergeState=conflict` 或 `mergeState=blocked` 的 issue，当 master 分支有新 commit 时自动重新入队执行 rebase。

#### Scenario: master 有新 commit 时自动重试

- **WHEN** 定时检查器运行（默认每 5 分钟）
- **AND** 存在 `mergeState` 为 `conflict` 或 `blocked` 的 issue
- **AND** master 分支自上次尝试后有了新 commit
- **THEN** 系统自动将该 issue 重新设为 `pending`
- **AND** 发出 `rebase_retry` 事件
- **AND** MergeQueue 重新处理该 issue（rebase + merge）

#### Scenario: master 无新 commit 时不重试

- **WHEN** 定时检查器运行
- **AND** 存在 `mergeState` 为 `conflict` 或 `blocked` 的 issue
- **AND** master 分支自上次尝试后没有新 commit
- **THEN** 系统不重试，等待下次检查周期

#### Scenario: 重试次数上限

- **WHEN** 一个 issue 的自动重试已达到上限（默认 5 次）
- **THEN** 系统将该 issue 的 mergeState 设为 `'blocked'`
- **AND** 停止自动重试
- **AND** 发出 `merge_blocked` 事件，payload 包含重试次数和最后一次冲突信息

#### Scenario: 手动重试重置计数

- **WHEN** 用户通过 API 手动触发重试（`POST /api/issues/:number/retry-merge`）
- **THEN** 重试计数重置为 0
- **AND** mergeState 设为 `pending`
- **AND** MergeQueue 立即处理

#### Scenario: server 启动时恢复重试状态

- **WHEN** server 启动
- **THEN** 系统从数据库加载所有 `mergeState` 为 `conflict` 或 `blocked` 的 issue
- **AND** 启动定时检查器
