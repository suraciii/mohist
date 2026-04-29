## ADDED Requirements

### Requirement: KanbanBoard 将 closed issue 归入 Done 列

KanbanBoard 分组逻辑 SHALL 在将 issues 分配到各 stage 列时，把 `status=Closed` 的 issue 强制放入 Done 列，无论其 `stage` 字段值是什么。此行为仅作用于展示层，后端 `stage` 字段保持不变。

#### Scenario: Closed issue 从活跃列移入 Done 列

- **WHEN** 一个 issue 的 `status` 为 `Closed`
- **AND** 该 issue 的 `stage` 为 `Plan`（或任意非 Done 的 stage）
- **THEN** KanbanBoard 将该 issue 放入 Done 列显示
- **AND** 该 issue 不出现在 Plan 列或其他活跃列中
- **AND** 该 issue 的后端 `stage` 字段不被修改

#### Scenario: Reopen 后 issue 回到原 stage 列

- **WHEN** 一个 previously closed issue 被 reopen（`status` 从 `Closed` 变为 `Active`）
- **AND** 该 issue 的 `stage` 仍为 `Plan`
- **THEN** KanbanBoard 将该 issue 放回 Plan 列显示
- **AND** 不需要任何后端 stage 修改

#### Scenario: 非 Closed issue 不受分组逻辑影响

- **WHEN** 一个 issue 的 `status` 不是 `Closed`（如 `Active`、`Paused`、`Blocked`）
- **THEN** KanbanBoard 按其 `stage` 字段值正常分配到对应列

### Requirement: Done 列默认隐藏 closed issue 并提供 toggle 控制

KanbanView SHALL 在看板顶部提供一个 "Show closed" toggle 控件，默认处于关闭状态。当 toggle 关闭时，Done 列中 `status=Closed` 的 issue SHALL 被隐藏。当 toggle 开启时，Done 列 SHALL 显示所有 closed issue。Done 列中非 Closed 状态的 issue（如正常 workflow 完成的 issue）不受此 toggle 影响。

#### Scenario: 默认状态下 closed issue 不可见

- **WHEN** 用户打开看板页面
- **AND** "Show closed" toggle 处于默认关闭状态
- **AND** 存在 `status=Closed` 的 issue
- **THEN** Done 列中不显示 closed issue
- **AND** Done 列中正常完成的 issue（`status=Completed`）仍然显示

#### Scenario: 开启 toggle 后 closed issue 可见

- **WHEN** 用户点击 "Show closed" toggle 将其开启
- **AND** Done 列中存在 `status=Closed` 的 issue
- **THEN** 这些 closed issue 在 Done 列中显示

#### Scenario: toggle 不影响非 Closed 的 issue

- **WHEN** "Show closed" toggle 处于关闭状态
- **AND** Done 列中存在 `status=Completed` 的 issue
- **THEN** 这些 Completed issue 正常显示，不受 toggle 影响

#### Scenario: toggle 状态不持久化

- **WHEN** 用户刷新页面或重新导航到看板
- **THEN** "Show closed" toggle 重置为默认关闭状态
