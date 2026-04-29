## Why

Build/Plan/Review 阶段的 UI rebase（`POST /:number/rebase`）遇到冲突时直接 abort 返回 409，而 Merge Queue（Done 阶段）和 Review mergeBack 已有成熟的 ACP agent 自动冲突解决机制。冲突文件通常是 agent 自己写的代码，coder agent 完全有能力自动解决，无需人工介入。

## What Changes

- `executeRebase()`（issues.ts `POST /:number/rebase`）改为 `abortOnConflict: false`，冲突时保留 rebase 中间状态
- 新增 `resolveConflicts` 回调注入到 issues API，复用 `server/index.ts` 中已有的 ACP agent 冲突解决逻辑（`buildConflictResolutionPrompt` + `createAcpConnection`）
- 冲突解决流程：检测冲突 → emit `rebase_conflict`（status: resolving）→ spawn coder agent → `rebase --continue` → 继续阶段后处理（build verify / plan re-self-review / checkpoint clear）
- Agent 解决失败时降级行为：`rebase --abort` → 返回 409（与当前行为一致）
- SSE 事件：`rebase_conflict` 增加 `resolving` 状态展示，复用已有的 `agent_conflict_resolution_started/completed/failed` 事件
- UI：IssueDetailPage 展示冲突解决进度状态

## Capabilities

### New Capabilities

- `rebase-auto-resolve`: Build/Plan/Review 阶段 rebase 冲突自动解决，通过 ACP agent 解决冲突后 continue rebase

### Modified Capabilities

- `http-api`: `POST /:number/rebase` 响应增加冲突解决过程的状态反馈，`createIssueRoutes` 接收 `resolveConflicts` 回调
- `web-ui`: IssueDetailPage 展示 rebase 冲突解决进度（resolving/failed 状态）

## Impact

- `packages/cli/src/api/issues.ts` — executeRebase 核心逻辑改造 + createIssueRoutes 签名新增 resolveConflicts 参数
- `packages/cli/src/server/index.ts` — resolveConflicts 回调注入到 createIssueRoutes
- `packages/cli/src/services/event-bus.ts` — rebase_conflict 事件类型增加 status 字段
- `packages/cli/web/src/lib/types.ts` — 前端 rebase_conflict 事件类型增加 status 字段
- `packages/cli/web/src/components/IssueDetailPage.tsx` — UI 展示冲突解决进度
- `packages/cli/src/git/worktree-manager.ts` — abortOnConflict: false 路径已支持，无需改动
- 不影响 Merge Queue 的现有 resolveConflicts 逻辑（两处独立调用）
