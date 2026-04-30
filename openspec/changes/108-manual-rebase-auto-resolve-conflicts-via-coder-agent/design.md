## Context

Manual rebase (`POST /:number/rebase`) 在 Plan/Build/Review 阶段遇到冲突时，使用 `abortOnConflict: true`（`issues.ts:2194`）直接 abort 并返回 409。而 MergeQueue（Done 阶段）的 `handleConflictResolution()`（`merge-queue.ts:345`）已有完整的 agent 冲突解决流程。

冲突解决的核心依赖已就绪：
- `buildConflictResolutionPrompt()`（`artifact-prompt.ts:225`）
- `conflict-resolution.md` prompt
- `createAcpConnection()`（`acp-session.ts`）
- `resolveConflicts` 闭包（`server/index.ts:139`）— 目前是 MergeQueue 构造时注入的闭包
- SSE 事件类型 `agent_conflict_resolution_started/completed/failed` 已注册（`events.ts:31-33`）
- 前端 `useSSE` 已处理 `rebase_conflict` 的 `status: "resolving" | "failed"`（`useSSE.tsx:122-130`）

前端 `rebaseIssue` API 函数（`api.ts:193`）catch `ApiError` 并返回 `err.data`，意味着 409 响应当前被视为 "data" 展示给用户。`request()` 函数仅检查 `json.success`，不检查 status code — 202 + `{ success: true }` 将正常返回。

## Goals / Non-Goals

**Goals:**
- Plan/Build/Review 阶段 rebase 冲突时异步触发 coder agent 自动解决
- 无冲突路径完全不受影响
- 复用已有冲突解决基础设施（ACP、prompt、SSE 事件）
- 防止冲突解决 agent 运行中重复触发
- Review 阶段冲突解决后跳过重复 build verify

**Non-Goals:**
- 不同 stage 使用不同 conflict resolution prompt
- 冲突解决重试次数限制
- 提取 MergeQueue 为独立服务（仅提取 resolveConflicts 函数）
- Worktree Panel 统一面板

## Decisions

### D1: 提取 `resolveConflicts` 为独立函数而非服务类

从 `server/index.ts:139` 的闭包提取为独立函数 `resolveConflictsViaAgent()`，放置在新的 `services/conflict-resolution.ts` 中。

```typescript
// services/conflict-resolution.ts
export interface ConflictResolutionDeps {
  issueRepo: IssueRepo;
  workflowLogRepo: WorkflowLogRepo;
  coderSessionRepo: CoderSessionRepo;
  eventBus: EventBus;
  opencodeBinPath?: string;
}

export async function resolveConflictsViaAgent(
  deps: ConflictResolutionDeps,
  issueId: string,
  projectId: string,
  worktreePath: string,
  conflictFiles: string[],
): Promise<{ success: boolean; error?: string }>
```

**Alternatives considered:**
- 保留闭包但复制一份到 issues.ts → 代码重复，维护两份相同逻辑
- 提取为 ConflictResolutionService 类 → 过度工程，当前只有一个方法，函数更简洁
- 将 handleConflictResolution 从 MergeQueue 提取出来 → 它包含 MergeQueue 状态管理逻辑（mergeState、eventBus emit），与 MergeQueue 耦合太紧

### D2: 使用内存 `Set<string>` 追踪 conflict-resolution-in-progress

在 `createIssueRoutes` 内部维护 `Set<string>`（issueId），异步流程开始时 add，完成/失败时 delete。rebase 端点入口检查此 Set。

**Alternatives considered:**
- DB 字段 → 需要新增 schema、migration、repo 方法，且需要处理 server 重启后的状态清理。冲突解决是短暂操作（分钟级），内存标记足够
- issue.status 字段 → 引入新的 issue 状态会影响工作流状态机，过度耦合
- AgentRunnerService 追踪 → agent runner 追踪的是 main agent session，冲突解决是独立的 ACP session，语义不同

### D3: 异步流程不 await，在 rebase 端点 handler 内启动

冲突检测后返回 202，异步启动一个自包含的 Promise chain：agent 解决 → verify rebase done → stage-specific handlers → emit events。整个 chain 在 `.catch()` 中处理失败降级。

```
POST handler:
  return 202
  // fire-and-forget (with error handling)
  resolveConflictsFlow(issue, project, ...) 
    .then(() => { post-rebase handlers; emit rebase_completed })
    .catch(() => { abort rebase; emit rebase_conflict failed })
```

**Alternatives considered:**
- 使用 job queue → 过度工程，当前场景是单次触发
- 使用 MergeQueue 重试机制 → MergeQueue 是为 Done 阶段设计的，状态管理与 Plan/Build/Review 不同

### D4: Review 阶段通过标志位跳过 build verify

异步流程成功后调用 stage-specific handler 时，传入 `skipBuildVerify: true` 标志。`handleReviewRebase` 增加可选参数 `skipBuildVerify?: boolean`。

**Alternatives considered:**
- 检查是否有刚完成的 ACP session → 间接推断，不可靠
- 让 agent 在解决冲突后不跑 build → 改变 conflict-resolution.md prompt 语义，影响 MergeQueue 路径

### D5: rebase_conflict 事件用 `status: "failed"` 统一失败语义

前端 `useSSE.tsx:124` 已处理 `rebase_conflict` 的 `status: "resolving"` 和 `status: "failed"`。失败时 emit `rebase_conflict { ..., status: "failed", error: "..." }`，无需新增事件类型。

初始冲突检测: `rebase_conflict { conflicts, status: "resolving" }`
解决失败降级: `rebase_conflict { conflicts, status: "failed", error: "..." }`

前端需扩展 `RebaseConflictState` 和 `rebase_conflict` 类型增加 `error?: string` 字段。

## Risks / Trade-offs

**[Server 重启丢失 in-progress 状态]** → 内存 Set 在 server 重启后清空。此时 rebase 可能仍处于 in-progress 状态（git rebase 未完成）。用户再次点 Rebase 时，不会命中 guard，会重新开始 rebase（git 会检测到已存在的 rebase 状态并报错）。这个降级行为可接受——用户会看到错误，但不会卡死。

**[Agent 解决冲突耗时过长]** → ACP session 有超时机制（默认 30 分钟）。超时后 `createAcpConnection` 会返回失败，异步 chain 进入 catch 路径 abort rebase。

**[fire-and-forget 中的未捕获异常]** → 异步 chain 的 `.catch()` 确保所有异常被处理。最外层 log.error 记录意外错误。

## Migration Plan

1. 创建 `services/conflict-resolution.ts` 共享函数
2. `server/index.ts` MergeQueue 构造改用共享函数
3. `createIssueRoutes` 签名新增 `resolveConflictsDeps` 参数
4. 改造 `POST /:number/rebase` handler
5. 前端 `api.ts` `rebaseIssue` 处理 202 响应的 `status` 字段
6. 前端类型扩展 `RebaseConflictState` 和 `rebase_conflict` 增加 `error` 字段
7. 前端组件展示冲突解决进度（resolving/failed + error message）

Rollback: 将 `abortOnConflict` 改回 `true`，移除异步 chain，恢复 409 响应即可完全回退。

## Open Questions

None.
