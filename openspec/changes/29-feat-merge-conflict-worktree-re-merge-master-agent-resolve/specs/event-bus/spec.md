## ADDED Requirements

### Requirement: EventBus 支持 merge conflict 事件

EventBus SHALL 支持以下 merge conflict 相关事件类型：

- `merge_conflict_requiring_resolution`: payload `{ issueId, projectId, conflictFiles }`
- `merge_blocked`: payload `{ issueId, projectId, retryCount }`

#### Scenario: merge_conflict_requiring_resolution 事件推送

- **WHEN** MergeQueue 检测到冲突并完成 worktree 反向 merge
- **THEN** EventBus emit `merge_conflict_requiring_resolution` 事件
- **AND** SSE 客户端收到该事件
- **AND** payload 包含 `issueId`、`projectId`、`conflictFiles`（冲突文件路径数组）

#### Scenario: merge_blocked 事件推送

- **WHEN** 冲突自动解决重试次数达到上限
- **THEN** EventBus emit `merge_blocked` 事件
- **AND** SSE 客户端收到该事件
- **AND** payload 包含 `issueId`、`projectId`、`retryCount`
