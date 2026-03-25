## ADDED Requirements

### Requirement: Poller 定时检查 GitHub 状态

Server SHALL 定时轮询 GitHub，检查 Issues 和 PRs 的状态变化。

#### Scenario: 定时轮询
- **WHEN** server 运行中
- **THEN** 每 60 秒检查一次 GitHub
- **AND** 获取所有 crawlph 管理的 Issues
- **AND** 获取所有相关 PRs 的状态

#### Scenario: 检测到 PR 被批准
- **WHEN** poller 发现 PR 状态变为 approved
- **THEN** 触发对应的下一阶段
- **AND** 更新 Issue 的 GitHub Label

#### Scenario: 检测到 PR 被合并
- **WHEN** poller 发现 PR 状态变为 merged
- **THEN** 更新 Issue Label 为 done
- **AND** 从运行队列中移除该 Issue

### Requirement: Poller 检测新的待处理 Issues

Server SHALL 检测新创建或新启动的 Issues。

#### Scenario: 检测到新的 draft Issue
- **WHEN** poller 发现 Issue 带有 `crawlph:stage/draft` 标签
- **AND** 该 Issue 尚未在队列中
- **THEN** 将 Issue 加入待处理队列

#### Scenario: 检测到用户启动的 Issue
- **WHEN** 用户通过 CLI 执行 `crawlph start 123`
- **THEN** Issue 被立即加入队列
- **AND** 无需等待下次轮询

### Requirement: Poller 处理 API 限流

Poller SHALL 优雅地处理 GitHub API 限流。

#### Scenario: 遇到 API 限流
- **WHEN** GitHub API 返回 429 (rate limit)
- **THEN** poller 等待 `Retry-After` 指定的时间
- **AND** 重试请求
- **AND** 记录警告日志

#### Scenario: 使用条件请求减少 API 调用
- **WHEN** poller 请求 GitHub API
- **THEN** 使用 ETag / If-Modified-Since
- **AND** 如果资源未变化，跳过处理
