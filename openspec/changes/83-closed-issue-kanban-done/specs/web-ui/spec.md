## ADDED Requirements

### Requirement: IssueCard 区分 Blocked 和 Closed 的视觉表现

IssueCard SHALL 为 `Blocked` 和 `Closed` 两种 status 提供独立的视觉处理。`Blocked`（pipeline 失败，需要用户处理）SHALL 显示醒目的红色/橙色 "Blocked" badge。`Closed`（用户手动关闭）SHALL 显示灰色 "Closed" badge。两种状态均不使用遮罩覆盖卡片内容。

#### Scenario: Blocked 状态显示醒目 badge

- **WHEN** issue 的 `status` 为 `Blocked`
- **THEN** IssueCard 显示红色或橙色的 "Blocked" badge
- **AND** 卡片内容正常显示（无半透明遮罩）
- **AND** badge 位于卡片右上角区域

#### Scenario: Closed 状态显示低调 badge

- **WHEN** issue 的 `status` 为 `Closed`
- **THEN** IssueCard 显示灰色的 "Closed" badge
- **AND** 卡片内容正常显示（无半透明遮罩）

#### Scenario: 非 Blocked 非 Closed 状态不显示这两种 badge

- **WHEN** issue 的 `status` 不是 `Blocked` 也不是 `Closed`
- **THEN** IssueCard 不显示 "Blocked" 或 "Closed" badge
- **AND** 其他 badge（如 Approval、Running）按原有逻辑显示
