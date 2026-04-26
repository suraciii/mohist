## Context

后端 `packages/cli/src/types/index.ts` 定义了 6 个 Stage 值：`Draft | Explore | Plan | Build | Review | Done`。前端 `web/src/lib/types.ts` 定义了 5 个：`Draft | Plan | Build | Check | Done`——缺少 `Explore` 和 `Review`，多出一个后端不存在的 `Check`。

Kanban 的 `STAGES` 数组只包含前端 enum 中的值。当后端返回 `stage: "review"` 时，`map.get("review")` 返回 `undefined`，issue 被静默丢弃。当前 #4、#5 均处于 `stage: review`，在 Kanban 中不可见。

## Goals / Non-Goals

**Goals:**
- 前端 Stage enum 与后端完全对齐（6 个值）
- Kanban 为每个后端 stage 提供列，不再丢弃 issue
- IssueDetailPage 进度条和 SessionTimeline 阶段标签正确显示

**Non-Goals:**
- 不修改后端 enum 或 API
- 不改变 Kanban 布局或交互逻辑
- 不添加 Kanban 列的动态生成（保持静态 STAGES 数组）

## Decisions

### D1: 前端 Stage enum 直接镜像后端定义

将 `Check = "check"` 替换为 `Review = "review"`，并添加 `Explore = "explore"`。保持与后端 `types/index.ts` 的 enum 值完全一致。

**Alternatives considered:** 前端动态从 API 获取 stage 列表——过度工程，后端 enum 极少变更，不值得增加复杂度。

### D2: Kanban STAGES 数组补齐 Explore 列

在 `KanbanBoard.tsx` 的 STAGES 数组中按后端 STAGE_ORDER 顺序添加 Explore 列。位置：Draft 之后、Plan 之前。

**Alternatives considered:** 不添加 Explore 列（当前无 explore stage 的 issue）——但同样会导致未来 explore issue 被丢弃，应一次性修复。

### D3: Kanban 列顺序遵循后端 STAGE_ORDER

最终顺序：`Draft → Explore → Plan → Build → Review → Done`，与后端 `STAGE_ORDER` 一致。

## Risks / Trade-offs

- [Explore 列初始为空] → 无影响，空列在 Kanban 中正常显示 "No issues"
- [前端 enum 变更需全量搜索引用] → 已通过 grep 确认只有 6 处引用，改动范围可控

## Migration Plan

纯前端改动，无需数据库迁移或 API 变更。部署即生效，现有 `stage: review` 的 issue 立即出现在 Kanban 中。无需回滚策略——如需回滚只需 revert 前端 4 个文件。

## Open Questions

_None_
