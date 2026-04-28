## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

## ADDED Requirements

### Requirement: 前端 IssueStatus 枚举与后端对齐

前端 `IssueStatus` 枚举 SHALL 包含后端定义的全部值：`Active`、`Paused`、`Blocked`、`Interrupted`、`Closed`、`Completed`。

#### Scenario: 前端枚举包含 Closed 和 Completed

- **WHEN** 前端代码导入 `IssueStatus`
- **THEN** `IssueStatus.Closed` 等于 `'closed'`
- **AND** `IssueStatus.Completed` 等于 `'completed'`

### Requirement: Actions 面板以 Status 为主轴显示按钮

IssueDetailPage 的 Actions 面板 SHALL 以 issue.status 为主要判断条件决定显示哪些按钮，issue.stage 仅作为修饰条件。

#### Scenario: Closed issue 显示 Reopen 按钮
- **WHEN** issue.status === `IssueStatus.Closed`
- **THEN** Actions 面板仅显示 "Reopen" 按钮
- **AND** 不显示 Start、Explore、Close 按钮

#### Scenario: Closed issue 不显示 Start 按钮
- **WHEN** issue.status === `IssueStatus.Closed`
- **AND** issue.stage === `Stage.Draft`
- **THEN** Actions 面板不显示 Start 按钮

#### Scenario: Completed issue 显示终态提示
- **WHEN** issue.status === `IssueStatus.Completed`
- **THEN** Actions 面板显示完成提示文字（如 "This issue has been completed"）
- **AND** 不显示任何操作按钮

#### Scenario: Paused issue 显示 Resume 和 Close 按钮
- **WHEN** issue.status === `IssueStatus.Paused`
- **THEN** Actions 面板显示 "Resume" 按钮
- **AND** Actions 面板显示 "Close" 按钮

#### Scenario: Blocked issue 显示 Reopen 和 Close 按钮
- **WHEN** issue.status === `IssueStatus.Blocked`
- **THEN** Actions 面板显示 "Reopen" 按钮
- **AND** Actions 面板显示 "Close" 按钮

#### Scenario: Interrupted issue 显示 Resume Pipeline 和 Close 按钮
- **WHEN** issue.status === `IssueStatus.Interrupted`
- **THEN** Actions 面板显示 "Resume Pipeline" 按钮（橙色主色调）
- **AND** Actions 面板显示 "Close" 按钮

#### Scenario: Active + Draft 显示 Start 和 Explore
- **WHEN** issue.status === `IssueStatus.Active`
- **AND** issue.stage === `Stage.Draft`
- **THEN** Actions 面板显示 "Start" 按钮
- **AND** Actions 面板显示 "Explore" 按钮

#### Scenario: Active + 非 Draft 显示 Close 按钮
- **WHEN** issue.status === `IssueStatus.Active`
- **AND** issue.stage !== `Stage.Draft`
- **THEN** Actions 面板显示 "Close" 按钮
- **AND** 不显示 Start 或 Explore 按钮

### Requirement: statusBadge 为全部状态提供视觉样式

`statusBadge()` 函数 SHALL 为 `IssueStatus` 的全部 6 个枚举值返回对应的 CSS 类，无 fallback 到默认灰色。

#### Scenario: Closed 状态灰色 badge
- **WHEN** 调用 `statusBadge(IssueStatus.Closed)`
- **THEN** 返回灰色系 CSS 类（如 `text-gray-700 bg-gray-50`）

#### Scenario: Completed 状态绿色 badge
- **WHEN** 调用 `statusBadge(IssueStatus.Completed)`
- **THEN** 返回绿色系 CSS 类（如 `text-green-700 bg-green-50`）

### Requirement: IssueCard Badge 正确区分 Blocked 和 Closed

IssueCard SHALL 为不同 status 显示语义正确的 badge，Blocked（自动阻塞）和 Closed（用户关闭）SHALL 使用不同的视觉表现。

#### Scenario: Blocked 显示红色 Blocked 标签
- **WHEN** issue.status === `IssueStatus.Blocked`
- **THEN** IssueCard 显示红色 "Blocked" 文字标签
- **AND** 不显示灰色 "Closed" 文字

#### Scenario: Closed 显示灰色 Closed 标签
- **WHEN** issue.status === `IssueStatus.Closed`
- **THEN** IssueCard 显示灰色 "Closed" 文字标签

#### Scenario: Completed 显示绿色完成标识
- **WHEN** issue.status === `IssueStatus.Completed`
- **THEN** IssueCard 显示绿色完成标识（如勾号图标 + "Completed"）

#### Scenario: Paused 显示 Paused 标签
- **WHEN** issue.status === `IssueStatus.Paused`
- **THEN** IssueCard 显示 "Paused" 文字标签

### Requirement: Closed/Completed issue 详情页顶部状态标识

IssueDetailPage 顶部状态区域 SHALL 在 Closed 和 Completed 状态下显示明确的视觉标识。

#### Scenario: Closed issue 顶部显示 Closed 标签
- **WHEN** issue.status === `IssueStatus.Closed`
- **THEN** 页面顶部状态区域显示灰色 "Closed" 标签

#### Scenario: Completed issue 顶部显示完成标识
- **WHEN** issue.status === `IssueStatus.Completed`
- **THEN** 页面顶部状态区域显示绿色完成标识（如勾号 + "Completed"）
