## Context

`POST /:number/rebase`（issues.ts:2063-2068）在 Build/Plan/Review 阶段使用 `abortOnConflict: true`，冲突时直接 abort 返回 409。而 MergeQueue 和 mergeBack 路径已有成熟的 ACP agent 冲突解决机制（`buildConflictResolutionPrompt` + `createAcpConnection`）。

关键代码路径：
- `server/index.ts:137-172` — MergeQueue 的 `resolveConflicts` 回调，使用 `buildConflictResolutionPrompt` + `createAcpConnection`
- `worktree-manager.ts:253-311` — `rebaseOntoMaster()` 已支持 `abortOnConflict: false`，会保留 rebase 中间状态
- `worktree-manager.ts:337-345` — `rebaseContinue()` 已存在，可执行 `git rebase --continue`
- `worktree-manager.ts:314-321` — `abortRebase()` 已存在
- `issues.ts:2063-2083` — 当前 rebase handler 冲突处理逻辑
- `issues.ts:1935-2001` — 已有的阶段后处理函数（handleReviewRebase, handlePlanRebase, handleBuildRebase）

## Goals / Non-Goals

**Goals:**
- Build/Plan/Review 阶段 rebase 冲突时自动 spawn coder agent 解决
- 复用已有的 ACP 冲突解决逻辑（`buildConflictResolutionPrompt` + `createAcpConnection`）
- Agent 失败时降级为 abort + 409（与当前行为一致）
- UI 实时展示冲突解决进度

**Non-Goals:**
- 不修改 MergeQueue 的现有 resolveConflicts 逻辑
- 不修改 `worktree-manager.ts`（`abortOnConflict: false` 路径已完整支持）
- 不修改 Done 阶段的 rebase 路径（仍走 MergeQueue retry）

## Decisions

### D1: resolveConflicts 通过函数参数注入，不通过 MergeQueue

在 `createIssueRoutes` 签名末尾新增可选参数 `resolveConflicts?: (...) => Promise<...>`，与 MergeQueue 的回调独立。不直接传入 MergeQueue 实例调用其 resolveConflicts，因为两者的上下文不同（MergeQueue 使用 MergeEntry，rebase 使用 Issue）。

**注入点：** `server/index.ts:240` 的 `createIssueRoutes(...)` 调用，在末尾传入新的回调。回调逻辑直接复用 `buildConflictResolutionPrompt` + `createAcpConnection`，与 MergeQueue 的 resolveConflicts 回调（server/index.ts:137-172）结构一致。

**Alternatives considered:**
- 直接在 issues.ts 内部 import 并调用 `buildConflictResolutionPrompt` — 违反依赖注入模式，增加 issues.ts 与 agent 层的耦合
- 传入整个 MergeQueue 实例 — MergeQueue 的 resolveConflicts 接收 MergeEntry 而非 Issue，签名不匹配

### D2: 复用 `rebase_conflict` 事件，通过 `status` 字段区分状态

在 `rebase_conflict` 事件的 payload 中新增可选 `status?: 'resolving' | 'failed'` 字段。不新增独立事件类型。后端 event-bus 类型更新为 `{ ..., conflicts: string[], status?: 'resolving' | 'failed' }`。

**Alternatives considered:**
- 新增 `rebase_resolving` 事件类型 — 增加 ALL_EVENT_TYPES 列表和前端 SSE 监听的维护成本
- 复用 `rebase_progress` step: 'resolving' — 语义上 resolving 不是 progress step，是冲突状态

### D3: 冲突解决流程在 rebase handler 内部同步执行

冲突检测 → agent 解决 → `rebase --continue` → 阶段后处理，全部在同一个 HTTP 请求 handler 中 `await` 串行完成。不拆分为异步 job。

理由：rebase 操作需要独占 worktree，并发没有收益。handler 内串行保证状态一致。如果 agent 超时（ACP session 有默认超时），handler 仍能正常降级。

**Alternatives considered:**
- 异步 job + 轮询 — 增加状态管理复杂度，而 rebase 操作天然串行，无并发需求

## Risks / Trade-offs

- [长时间请求占用连接] → agent 解决冲突可能需要数分钟，HTTP 请求可能超时。缓解：前端 SSE 事件提供进度反馈，API 请求本身等待完成。如需进一步优化可后续改为异步 job。
- [agent 解决冲突后 build 仍失败] → Review 阶段有 build verification 兜底。Build 阶段会清除 checkpoint 让 pipeline 重新构建。Plan 阶段 re-self-review 会重新检查设计。
- [resolveConflicts 回调未注入时无自动解决能力] → 降级行为与当前一致（abort + 409），不引入功能回退。

## Migration Plan

1. 后端：修改 `createIssueRoutes` 签名 + rebase handler 逻辑 + `server/index.ts` 注入回调
2. SSE：更新 `event-bus.ts` 的 `rebase_conflict` 类型，增加 `status` 字段
3. 前端：更新 `types.ts` 的 `rebase_conflict` 类型，`IssueDetailPage.tsx` 监听 `rebase_conflict` 的 `status` 展示进度
4. 无数据库迁移，无 API 破坏性变更

## Open Questions

- 无。设计方案与现有代码模式一致，无需额外决策。
