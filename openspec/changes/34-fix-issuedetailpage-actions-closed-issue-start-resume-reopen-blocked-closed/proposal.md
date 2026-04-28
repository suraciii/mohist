## Why

前端 IssueStatus 枚举缺少 `Closed` 和 `Completed`，且 Actions 面板按钮逻辑以 Stage 为主轴、不检查 Status，导致 Closed issue 仍显示 Start 按钮、Paused issue 无操作按钮、Blocked 被误标为 Closed 文字、Completed 无终态标识。需要将按钮逻辑改为 Status 优先、补齐枚举、修正 badge，使前端与后端状态模型对齐。

## What Changes

- 补齐前端 `IssueStatus` 枚举：新增 `Closed = 'closed'` 和 `Completed = 'completed'`，与后端 `packages/cli/src/types/index.ts` 对齐
- 重写 `IssueDetailPage` Actions 面板：按钮显示以 Status 为主轴（Closed → Reopen, Paused → Resume + Close, Blocked → Reopen + Close, Completed → 终态提示），Stage 仅作修饰
- 修正 `statusBadge()` 函数：为 Closed（灰色）和 Completed（绿色）添加匹配分支
- 修正 `IssueCard` Badge：Blocked 显示红色 "Blocked" 标签而非灰色 "Closed" 文字，Completed 显示绿色完成标识
- 修正 `IssueDetailPage` 顶部状态区域：Closed issue 显示 Closed 标签，Completed issue 显示完成标记

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- **web-ui** — Actions 面板按钮逻辑从 Stage 优先改为 Status 优先，补齐 Closed/Completed 状态的 UI 表现
- **reopen-resume** — 前端 Reopen 按钮的显示条件从仅 Blocked 扩展到 Closed + Blocked，与后端 reopen API 的适用范围对齐

## Impact

- `packages/cli/web/src/lib/types.ts` — IssueStatus 枚举新增 2 个值
- `packages/cli/web/src/components/IssueDetailPage.tsx` — statusBadge() 补齐分支，Actions 面板按钮逻辑重写，顶部状态标签修正
- `packages/cli/web/src/components/IssueCard.tsx` — Blocked badge 修正，新增 Closed/Completed badge
- 纯前端变更，无 API / 后端改动，无 breaking change
