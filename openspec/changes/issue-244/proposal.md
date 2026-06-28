## Why

Session 列表与发现面目前是碎片化且未完成的：`SessionDetail` 是只渲染 "Session info" 的死桩（`SessionDetail.tsx:7-12`），coder-session widget 下的 `SessionList` 是一套未被任何页面引用的重复实现，真正在 issue 详情页使用的 `WorkflowSessionsPanel`（`IssueDetailPage.tsx:926`）与 `SessionHeader` 又把 10+ 项指标（status、model、tokens、cost、context%、tools、failure、时间、时长、链接）挤进单行；同时列表不支持按 status/stage 筛选、不支持按 tokens/时长排序、session 页也无法在同级 session 间跳转。结果是用户在多 session 的 issue 里无法快速找到、浏览和管理 session。

## What Changes

- 移除 `SessionDetail` 死桩，并删除依赖它的未使用 `SessionList` 组件，将 session 列表收敛到唯一实现（`WorkflowSessionsPanel`）。
- 列表面板支持按 **status**（running / completed / failed / …）与 **stage**（plan / build / check / integrate）筛选。
- 列表面板支持排序，维度至少包含 **createdAt**、**tokens**、**duration**；默认排序与现状一致，用户可切换。
- Session 行信息在窄容器下合理换行，不再把所有指标塞进单行；保留 session 名、状态、关键指标的可读性。
- Session 页内提供「前一个 / 后一个 session」导航，沿同级 session 顺序跳转。
- Session 页侧边栏显示同一 issue 的其他 sessions 列表，支持快速切换。

## Capabilities

### New Capabilities

- `session-list`: session 列表与跨 session 发现契约——列表面板的 status/stage 筛选、createdAt/tokens/duration 排序、窄容器下的行信息换行布局，以及 session 页内「前一个/后一个 session」导航与同级 sessions 侧边栏的行为边界。

### Modified Capabilities

<!-- 现有 agent-session-ui（transcript 阅读）与 session-transcript-navigation（turn 级导航）的 spec 级需求不变；本变更只新增列表/发现面，不改动既有要求。 -->

## Impact

- **web**：
  - `packages/web/src/widgets/coder-session/ui/SessionDetail.tsx`、`SessionList.tsx`：删除死桩与未使用组件。
  - `packages/web/src/widgets/coder-session/ui/SessionHeader.tsx`：重构单行密集布局，窄容器下合理换行（或由列表行组件统一吸收）。
  - `packages/web/src/widgets/issue-workflow/ui/WorkflowSessionsPanel.tsx`：接入筛选、排序控件，调整 `WorkflowSessionRow` 行布局。
  - `packages/web/src/pages/session/ui/SessionPage.tsx`：新增「前一个/后一个 session」导航与同级 sessions 侧边栏编排。
  - `packages/web/src/entities/coder-session/`：复用既有 `useCoderSessions` / `useWorkflowRunSessions` 数据源，新增筛选/排序/相邻 session 派生逻辑。
- **测试**：`packages/web` 扩展筛选、排序、行换行、前/后导航、侧边栏渲染等用例。
- **Server / Runner / CLI / API / 持久化**：无改动；筛选与排序为前端内存计算。
