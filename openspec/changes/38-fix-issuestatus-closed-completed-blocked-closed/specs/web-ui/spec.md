## ADDED Requirements

### Requirement: Issue 状态完整覆盖所有后端状态

前端 `IssueStatus` enum SHALL 包含后端定义的全部 6 个状态值：`Active`、`Paused`、`Blocked`、`Interrupted`、`Closed`、`Completed`。IssueCard 和 IssueDetailPage SHALL 为每个状态提供正确的标签文本和视觉样式，且不同状态之间 SHALL 有语义一致且可区分的外观。

#### Scenario: 前端 enum 覆盖全部后端状态

- **WHEN** 检查前端 `IssueStatus` enum 定义
- **THEN** 包含 `Active = 'active'`、`Paused = 'paused'`、`Blocked = 'blocked'`、`Interrupted = 'interrupted'`、`Closed = 'closed'`、`Completed = 'completed'` 共 6 个值
- **AND** 与后端 `packages/cli/src/types/index.ts` 中 `IssueStatus` 枚举完全一致

#### Scenario: IssueCard 显示 Blocked 状态为 "Blocked" 而非 "Closed"

- **WHEN** issue 状态为 `Blocked`
- **THEN** IssueCard 中显示标签文本 "Blocked"
- **AND** 使用红色样式
- **AND** 不显示 "Closed" 文本

#### Scenario: IssueCard 显示 Closed 状态

- **WHEN** issue 状态为 `Closed`
- **THEN** IssueCard 中显示标签文本 "Closed"
- **AND** 使用与 Blocked 不同的视觉样式（如灰色）

#### Scenario: IssueCard 显示 Completed 状态

- **WHEN** issue 状态为 `Completed`
- **THEN** IssueCard 中显示标签文本 "Completed"
- **AND** 使用绿色样式，表示成功完成

#### Scenario: IssueDetailPage 状态徽章区分所有状态

- **WHEN** 用户查看 Issue 详情页
- **AND** issue 状态为 `Blocked`
- **THEN** 状态徽章显示 "blocked" 标签，红色样式

- **WHEN** 用户查看 Issue 详情页
- **AND** issue 状态为 `Closed`
- **THEN** 状态徽章显示 "closed" 标签，灰色样式

- **WHEN** 用户查看 Issue 详情页
- **AND** issue 状态为 `Completed`
- **THEN** 状态徽章显示 "completed" 标签，绿色样式

#### Scenario: IssueDetailPage 为 Closed 和 Completed 提供操作按钮

- **WHEN** issue 状态为 `Closed`
- **THEN** 详情页显示 "Reopen" 操作按钮

- **WHEN** issue 状态为 `Completed`
- **THEN** 详情页显示 "Reopen" 操作按钮
