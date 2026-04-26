## Why

前端 Stage enum 定义了 `Check = "check"` 但后端实际使用 `Review = "review"`，导致所有 `stage: review` 的 issue 在 Kanban 中被静默丢弃——当前 #4 和 #5 完全不可见。前后端 stage 枚举必须对齐才能正确显示所有 issue。

## What Changes

- 前端 `Stage.Check = "check"` → `Stage.Review = "review"`，与后端枚举对齐
- KanbanBoard 列头 label 从 "Check" → "Review"
- IssueDetailPage 进度条阶段引用从 `Stage.Check` → `Stage.Review`
- SessionTimeline 阶段匹配字符串从 `"check"` → `"review"`
- 前端 Stage enum 补齐缺失的 `Explore = "explore"` 阶段（后端已定义，前端遗漏）

## Capabilities

### New Capabilities

_None_

### Modified Capabilities

- `web-ui`: Kanban 和 Issue 详情页必须正确识别后端定义的全部 6 个 stage（含 Review 和 Explore），stage 名称与后端保持一致。

## Impact

- **前端 4 个文件**：`types.ts`、`KanbanBoard.tsx`、`IssueDetailPage.tsx`、`SessionTimeline.tsx`
- 无 API 变更，无 breaking change——纯前端枚举值修正
- 修复后，现有 `stage: review` 的 issue（#4、#5）将自动出现在 Kanban Review 列中
