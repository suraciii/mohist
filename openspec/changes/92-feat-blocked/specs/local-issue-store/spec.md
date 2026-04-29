## MODIFIED Requirements

### Requirement: 数据库扩展

系统 SHALL 扩展现有 SQLite schema。

#### Scenario: Issues 表扩展
- **WHEN** 数据库迁移到 schema version 2
- **THEN** issues 表新增 `labels` 列（TEXT，JSON 数组）
- **AND** 现有 issues 的 labels 默认为 `[]`

#### Scenario: Comments 表创建
- **WHEN** 数据库迁移到 schema version 2
- **THEN** 创建 comments 表
- **AND** comments 表包含 id, issue_id, body, created_at 字段

#### Scenario: Issues 表增加 blocked_reason 和 retry_count 列
- **WHEN** 数据库执行 schema migration 增加 blocked UX 支持
- **THEN** issues 表新增 `blocked_reason` 列（TEXT，默认 null）
- **AND** issues 表新增 `retry_count` 列（INTEGER，默认 0）
- **AND** 现有 issues 的 blocked_reason 默认为 null
- **AND** 现有 issues 的 retry_count 默认为 0

## ADDED Requirements

### Requirement: Issue 类型包含 blockedReason 和 retryCount

Issue interface SHALL 包含 `blockedReason` 和 `retryCount` 字段。

#### Scenario: Issue interface 新增字段

- **WHEN** 检查 Issue interface 定义
- **THEN** 包含 `blockedReason?: string` 字段
- **AND** 包含 `retryCount?: number` 字段

#### Scenario: IssueRepo 提供 updateBlockedReason 方法

- **WHEN** 系统需要写入 blocked reason
- **THEN** IssueRepo 提供 `updateBlockedReason(issueId: string, reason: string | null)` 方法
- **AND** 该方法同时更新 `updated_at`

#### Scenario: IssueRepo 提供 updateRetryCount 方法

- **WHEN** 系统需要更新重试计数
- **THEN** IssueRepo 提供 `updateRetryCount(issueId: string, count: number)` 方法

#### Scenario: rowToIssue 映射新增字段

- **WHEN** 从数据库 row 转换为 Issue 对象
- **THEN** `blockedReason` 从 `blocked_reason` 列映射
- **AND** `retryCount` 从 `retry_count` 列映射
- **AND** null 值映射为 undefined
