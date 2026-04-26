## ADDED Requirements

### Requirement: Start 按钮按 issue 粒度判断禁用状态

IssueDetailPage 的 Start 按钮 SHALL 基于 per-issue 判断而非全局布尔锁来决定禁用状态。按钮 SHALL 在以下情况禁用：该 issue 已有 agent 在跑，或并发上限已满。

#### Scenario: 其他 issue 有 agent 在跑但并发未满
- **WHEN** agent 正在运行 issue #3
- **AND** 用户查看 issue #5（无 agent 在跑）
- **AND** `activeAgents.length < maxConcurrentAgents`
- **THEN** issue #5 的 Start 按钮为 **可用** 状态

#### Scenario: 该 issue 已有 agent 在跑
- **WHEN** agent 正在运行 issue #3
- **AND** 用户查看 issue #3
- **THEN** issue #3 的 Start 按钮为 **禁用** 状态
- **AND** 按钮文本显示 "Agent running..."

#### Scenario: 并发上限已满
- **WHEN** `activeAgents.length >= maxConcurrentAgents`
- **AND** 用户查看一个无 agent 在跑的 issue
- **THEN** Start 按钮为 **禁用** 状态
- **AND** 按钮文本显示 "Capacity full..." 或类似提示

#### Scenario: 无 agent 运行
- **WHEN** 没有任何 agent 在运行
- **AND** 用户查看 issue #5
- **THEN** issue #5 的 Start 按钮为 **可用** 状态
- **AND** 按钮文本显示 "Start"

### Requirement: Approve 按钮不受全局锁限制

Approve & Continue 按钮 SHALL 只在该 issue 已有 agent 正常运行时禁用（防止重复操作），不受其他 issue 的 agent 状态影响。

#### Scenario: 其他 issue 有 agent 在跑
- **WHEN** agent 正在运行 issue #3
- **AND** 用户查看 issue #5 的审批面板
- **THEN** Approve 按钮为 **可用** 状态

#### Scenario: 该 issue 的 agent 正在运行
- **WHEN** agent 正在运行 issue #5
- **AND** 用户查看 issue #5 的审批面板
- **THEN** Approve 按钮为 **禁用** 状态

### Requirement: IssueCard 按 per-issue 显示 running 指示器

IssueCard SHALL 根据 `activeAgents` 数组判断该 issue 是否有 agent 在运行，显示 "Running" 指示器，而非依赖全局 `running` 布尔值。

#### Scenario: 该 issue 有 agent 在跑
- **WHEN** agent 正在运行 issue #3
- **AND** 看板渲染 issue #3 的卡片
- **THEN** 卡片显示蓝色脉冲 "Running" 标记

#### Scenario: 其他 issue 有 agent 在跑
- **WHEN** agent 正在运行 issue #3
- **AND** 看板渲染 issue #5 的卡片
- **THEN** 卡片 **不** 显示 "Running" 标记

#### Scenario: 多个 issue 同时有 agent 在跑
- **WHEN** agent 同时运行 issue #3 和 issue #7
- **THEN** issue #3 和 issue #7 的卡片都显示 "Running" 标记
- **AND** 其他 issue 的卡片不显示 "Running" 标记

### Requirement: KanbanBoard 传递完整 activeAgents 信息

KanbanBoard SHALL 将 `activeAgents` 数组和 `maxConcurrentAgents` 传递给子组件（StageColumn、IssueCard），以支持 per-issue 的状态判断。

#### Scenario: 多 agent 状态正确传递
- **WHEN** agent 同时运行 issue #3 和 issue #7
- **AND** KanbanBoard 渲染看板
- **THEN** 每个 IssueCard 接收到完整的 `activeAgents` 数组
- **AND** 每个 IssueCard 可独立判断自己的 running 状态
