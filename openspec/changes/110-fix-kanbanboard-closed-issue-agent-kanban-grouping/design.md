## Context

KanbanBoard.tsx 当前使用内联分组逻辑（`map.get(issue.stage)`），忽略了 `IssueStatus.Closed`，导致 closed issue 停留在原始 stage 列。正确的分组逻辑已存在于 `kanban-grouping.ts` 并有测试覆盖，只是未被引用。

KanbanBoard 有两种视图模式：
- **桌面端** (`md:` 以上)：每列渲染 `<StageColumn>`，Done 列额外有归档 UI
- **移动端** (`md:hidden`)：tab 选择器 + 单列 `<IssueCard>` 列表

## Goals / Non-Goals

**Goals:**
- 让 KanbanBoard 使用 `kanban-grouping.ts` 的 `groupIssuesByStage` 替代内联分组
- 恢复 `showClosed` state，Done 列默认隐藏 closed issue
- Done 列显示 "Show closed (N)" toggle

**Non-Goals:**
- 不修改 `kanban-grouping.ts`（STAGES 常量已匹配当前 Stage enum）
- 不修改 StageColumn 的归档/折叠逻辑
- 不修改后端或数据模型

## Decisions

### D1: showClosed toggle 放在 KanbanBoard 层级

`showClosed` state 和 toggle 在 KanbanBoard 中管理，通过 `filterClosedFromDone` 在传入 StageColumn 之前过滤。StageColumn 不感知 closed 过滤逻辑。

**理由**: 保持 StageColumn 职责单一（只负责渲染列内容），closed 过滤是看板级别的展示决策。

**Alternatives considered:** 将 toggle 放入 StageColumn — 增加组件耦合，StageColumn 需要知道 closed 概念。

### D2: "Show closed" toggle 渲染在 StageColumn 的归档栏上方

在 Done 列的 `<StageColumn>` 之后、归档信息之前，KanbanBoard 直接渲染 toggle UI。需要将 StageColumn 的 children 或在 KanbanBoard 层面插入该 toggle。

**理由**: 避免 StageColumn 接收过多 props（showClosed、onToggle、closedCount），同时复用 StageColumn 已有的卡片折叠/归档逻辑。

**Alternatives considered:** 通过 props 传给 StageColumn — 每次需要新 Done 列专属功能都要改 Props 接口。

### D3: getDoneColumnCounts 基于 columns（过滤前）计算

在调用 `filterClosedFromDone` 之前，先用 `getDoneColumnCounts(columns)` 获取 closedCount，确保 toggle 始终显示真实的 closed 数量，不受当前 showClosed 状态影响。

### D4: 移动端 tab 计数使用 displayedColumns

移动端 tab 的 issue 计数 badge 使用过滤后的 `displayedColumns`，这样 tab 计数与用户看到的 issue 列表一致。

## Risks / Trade-offs

- [Risk: StageColumn props 变化可能引入未预期的 Done 列行为变化] → StageColumn props 不变，toggle UI 在 KanbanBoard 层渲染
- [Risk: 移动端 toggle 位置需考虑布局] → 移动端 Done tab 选中时，在 issue 列表上方显示 toggle

## Migration Plan

无迁移需求 — 纯前端展示层修改，不影响后端数据。

## Open Questions

_None_
