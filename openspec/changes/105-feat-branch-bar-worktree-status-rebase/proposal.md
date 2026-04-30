## Why

Worktree status（ahead/behind/synced）在前端完全不可见——`WorktreeManager` 没有 `getWorktreeStatus()` 方法、API 无端点、前端无 hook、UI 无组件。同时 rebase 按钮散落在 IssueDetailPage 的 Actions panel（Build stage）、Plan approval gate、Review approval gate 三处，各自维护 mutation 逻辑，无复用，用户心智模型混乱。需要一个统一的 Branch Bar 组件恢复 worktree 可视化并集中 rebase 操作。

## What Changes

- 新增 `WorktreeManager.getWorktreeStatus()` 方法，返回 ahead/behind 计数和 rebase 状态
- 新增 `GET /api/issues/:number/worktree-status` 端点
- 新增 `api.getWorktreeStatus()` 前端 API client 方法
- 新增 `useWorktreeStatus` hook（30s 轮询）
- 新增 `BranchBar` 组件：左列顶层 context 元素，显示分支名、ahead/behind 状态、rebase 按钮
- 移除 IssueDetailPage 中 Build stage 的 rebase 按钮（Actions panel，lines 640-660）
- 移除 IssueDetailPage 中 Review approval gate 的 rebase 按钮（lines 803-835）
- 移除 IssueDetailPage 中 Plan approval gate 的 rebase 按钮（lines 855-886）
- 移除 `rebaseMutation` 和 `rebaseResult` state（由 BranchBar 自行管理）
- Approval gate（Plan/Review）简化为纯粹的审批决策 UI

## Capabilities

### New Capabilities

- `branch-bar`: 统一的 Branch Bar 组件，展示 worktree 分支状态（ahead/behind/synced）并提供集中的 rebase 操作入口

### Modified Capabilities

- `http-api`: 新增 `GET /api/issues/:number/worktree-status` 端点
- `web-ui`: 移除散落三处的 rebase 按钮和逻辑，Approval gate 简化为纯审批

## Impact

- `packages/cli/src/git/worktree-manager.ts` — 新增 `getWorktreeStatus()` 方法和 `WorktreeStatus` 接口
- `packages/cli/src/api/issues.ts` — 新增 worktree-status 端点
- `packages/cli/web/src/lib/api.ts` — 新增 `getWorktreeStatus()` 方法
- `packages/cli/web/src/hooks/useQueries.ts` — 新增 `useWorktreeStatus` hook
- `packages/cli/web/src/components/BranchBar.tsx` — 新组件
- `packages/cli/web/src/components/IssueDetailPage.tsx` — 移除散落 rebase 逻辑，添加 BranchBar
