## Why

Issue 完成后 `mergeBack()` 由 `agent_completed` 事件直接触发，无队列、无构建验证。当多个 issue 并行完成时，并发 merge 会互相冲突（git checkout race）；合并后也不验证构建，导致如 #13 修改了 AgentStatus 类型但 App.tsx 未同步，master 构建失败。需要一个串行合并队列，合并后自动验证构建，失败则回滚，确保 master 始终可构建。

## What Changes

- 新增 `MergeQueue` 串行队列服务，`agent_completed` 事件触发时 enqueue 替代直接 mergeBack
- 合并后执行 `npm run build`（CLI+Web），构建失败则 `git reset --hard HEAD~1` 回滚
- Issue 类型新增 `mergeState` 字段（`pending | merging | merged | build-failed | conflict`）
- 替换 `server/index.ts` 中的即时 mergeBack 为队列入队
- 新增 API：`GET /api/issues/merge-queue/status`、`POST /api/issues/:number/retry-merge`
- 新增 SSE 事件：`merge_queued`、`merge_started`、`merge_completed`、`merge_failed`
- Web UI 展示合并队列状态和失败原因，支持重试

## Capabilities

### New Capabilities

- `merge-queue`: 串行合并队列 — enqueue / processNext / getStatus，issue 完成后自动入队，逐个 mergeBack + 构建验证
- `merge-build-verification`: 合并后构建验证 — npm run build 验证，失败自动 git reset --hard 回滚，确保 base branch 始终可构建

### Modified Capabilities

- `worktree-manager`: mergeBack 触发方式从即时调用改为队列驱动，mergeState 状态由队列管理
- `http-api`: 新增 merge-queue status 和 retry-merge 端点，Issue 响应增加 mergeState 字段
- `event-bus`: 新增 merge 相关 SSE 事件（merge_queued、merge_started、merge_completed、merge_failed）
- `web-ui`: Issue 详情页展示 mergeState 状态和失败信息，提供重试按钮

## Impact

- `packages/cli/src/git/merge-queue.ts`（新增）
- `packages/cli/src/git/worktree-manager.ts`（mergeBack 不再被直接调用）
- `packages/cli/src/server/index.ts`（agent_completed handler 改为 enqueue）
- `packages/cli/src/api/issues.ts`（新增 retry-merge 路由，新增 merge-queue status 路由）
- `packages/cli/src/types/index.ts`（Issue 增加 mergeState 字段）
- `packages/cli/src/db/`（issues 表增加 merge_state 列）
- `packages/cli/web/src/`（队列状态展示 + 失败重试 UI）
