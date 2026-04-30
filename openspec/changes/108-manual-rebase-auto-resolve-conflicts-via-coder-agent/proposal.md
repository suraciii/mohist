## Why

Manual rebase (`POST /:number/rebase`) 遇到冲突时直接 abort 返回 409，用户只能看到冲突文件列表后手动处理或重试。冲突文件通常是 agent 自己写的代码，coder agent 完全有能力自动解决。MergeQueue 和 Review mergeBack 已有成熟的 ACP agent 冲突解决基础设施，应该复用到 manual rebase 路径。

## What Changes

- `POST /:number/rebase` 改为 `abortOnConflict: false`，冲突时保留 rebase 中间状态，返回 202（而非 409）
- 异步触发 coder agent 解决冲突，复用 `buildConflictResolutionPrompt` + `conflict-resolution.md` + `createAcpConnection` 已有基础设施
- 将 `resolveConflicts` 闭包（`server/index.ts:139`）提取为共享函数，供 manual rebase 和 MergeQueue 共用
- Agent 解决成功后执行 stage-specific post-rebase handler（与无冲突路径一致）
- Agent 解决失败 → abort rebase → emit `rebase_conflict` 事件含 error → UI 通过 SSE 展示
- Review 阶段冲突解决成功后跳过 build verify（agent 已在解决过程中验证过）
- 新增 conflict-resolution-in-progress 状态追踪，防止重复触发（用户再次点 Rebase 返回 409）
- 无冲突时行为完全不变（同步返回 200）
- SSE 事件序列：`rebase_conflict { resolving }` → `agent_conflict_resolution_started` → ... → `agent_conflict_resolution_completed/failed` → `rebase_progress` → `rebase_completed`
- UI 处理 202 响应 + SSE 冲突解决进度展示

## Capabilities

### New Capabilities

- `rebase-auto-resolve`: Manual rebase 冲突时异步触发 coder agent 自动解决，复用已有 ACP 基础设施，成功后继续 stage-specific post-rebase handler，失败降级 abort

### Modified Capabilities

- `http-api`: `POST /:number/rebase` 冲突时返回 202 而非 409；新增 conflict-resolution-in-progress guard（409）
- `event-bus`: `rebase_conflict` 事件增加 resolving 状态，冲突解决过程 emit 完整 SSE 事件序列
- `web-ui`: IssueDetailPage 处理 202 响应 + 展示 rebase 冲突解决进度（resolving/failed 状态）

## Impact

- `packages/cli/src/api/issues.ts` — rebase 端点核心逻辑改造（abortOnConflict: false、异步 agent、202 响应、guard）
- `packages/cli/src/server/index.ts` — 提取 resolveConflicts 闭包为共享函数，注入到 createIssueRoutes
- `packages/cli/src/git/merge-queue.ts` — handleConflictResolution 可能提取为可复用方法
- `packages/cli/src/api/events.ts` — 确保 agent_conflict_resolution 事件类型已注册
- `packages/cli/web/src/components/IssueDetailPage.tsx` — UI 展示冲突解决进度
- `packages/cli/web/src/lib/types.ts` — 前端事件类型适配
- 不影响 Merge Queue 的现有 resolveConflicts 逻辑（两处独立调用）
