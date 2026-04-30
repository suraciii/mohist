## ADDED Requirements

### Requirement: KanbanBoard 使用 kanban-grouping 模块分组

KanbanBoard SHALL 使用 `kanban-grouping.ts` 模块的 `groupIssuesByStage` 函数进行 issue 分组，SHALL NOT 使用内联分组逻辑。`IssueStatus.Closed` 的 issue SHALL 路由到 Done 列而非停留在其原始 stage 列。

#### Scenario: Closed issue 路由到 Done 列

- **WHEN** issue 的 `status` 为 `IssueStatus.Closed` 且 `stage` 为 `Plan`
- **AND** KanbanBoard 渲染分组
- **THEN** 该 issue 出现在 Done 列
- **AND** 该 issue 不出现在 Plan 列

#### Scenario: Active issue 保持在原 stage 列

- **WHEN** issue 的 `status` 为 `IssueStatus.Active` 且 `stage` 为 `Build`
- **AND** KanbanBoard 渲染分组
- **THEN** 该 issue 出现在 Build 列

#### Scenario: 不存在内联分组逻辑

- **WHEN** KanbanBoard.tsx 源码被检查
- **THEN** 不存在内联的 `STAGES` 常量定义
- **AND** 不存在内联的 `new Map<Stage, Issue[]>()` 分组逻辑
- **AND** import 了 `groupIssuesByStage`、`filterClosedFromDone`、`getDoneColumnCounts`、`STAGES` from `kanban-grouping.ts`

### Requirement: Done 列默认隐藏 closed issue

KanbanBoard SHALL 默认从 Done 列过滤掉 `IssueStatus.Closed` 的 issue，使用 `kanban-grouping.ts` 的 `filterClosedFromDone` 函数。

#### Scenario: 默认状态隐藏 closed issue

- **WHEN** Done 列包含 3 个 closed issue 和 2 个 active issue
- **AND** showClosed 为默认值（false）
- **THEN** Done 列只显示 2 个 active issue

#### Scenario: toggle 打开后显示 closed issue

- **WHEN** showClosed 为 false
- **AND** 用户点击 Done 列的 "Show closed" toggle
- **THEN** showClosed 变为 true
- **AND** Done 列显示全部 5 个 issue（含 3 个 closed）

### Requirement: Done 列显示 closed 计数和 toggle

KanbanBoard 的 Done 列 SHALL 使用 `getDoneColumnCounts` 显示 closed issue 数量，并提供 "Show closed" toggle 控件。

#### Scenario: 有 closed issue 时显示 toggle 和计数

- **WHEN** Done 列（过滤前）包含 3 个 closed issue 和 2 个 active issue
- **AND** showClosed 为 false
- **THEN** Done 列显示 "Show closed (3)" toggle

#### Scenario: 无 closed issue 时不显示 toggle

- **WHEN** Done 列不包含任何 closed issue
- **THEN** 不显示 "Show closed" toggle
