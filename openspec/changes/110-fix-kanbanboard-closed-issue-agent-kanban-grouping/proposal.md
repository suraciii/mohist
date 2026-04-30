## Why

#102 的 agent 在给 KanbanBoard.tsx 添加 archive 功能时整体覆盖了文件，丢弃了对 `kanban-grouping.ts` 的 import（该模块由 #83 正确实现并有测试），导致 closed issue 不再路由到 Done 列而是停留在原始 stage 列，同时丢失了 "Show closed" toggle 和 closed 计数 UI。现有 `kanban-grouping.ts` 模块完好且有测试覆盖，只需让 KanbanBoard 重新使用它。

## What Changes

- KanbanBoard.tsx: 删除内联 `STAGES` 常量和 `useMemo` 分组逻辑，import `kanban-grouping.ts` 的 `groupIssuesByStage`/`filterClosedFromDone`/`getDoneColumnCounts`/`STAGES`
- KanbanBoard.tsx: 恢复 `showClosed` state + Done 列 "Show closed" toggle UI + closed 计数显示
- kanban-grouping.ts: 验证 `STAGES` 常量与当前 Stage enum 一致（当前已一致，无需修改）

## Capabilities

### New Capabilities

_None_

### Modified Capabilities

- `web-ui`: KanbanBoard closed issue 路由行为从退化内联分组恢复为使用 kanban-grouping 模块，重新引入 "Show closed" toggle 交互

## Impact

- `packages/cli/web/src/components/KanbanBoard.tsx` — 主要改动文件
- `packages/cli/web/src/lib/kanban-grouping.ts` — 可能需要更新 STAGES 常量（当前已匹配）
- `packages/cli/web/src/components/StageColumn.tsx` — 无需修改（toggle 在 KanbanBoard 层渲染）
- `packages/cli/web/src/lib/types.ts` — Stage enum（只读参考，预计无修改）
