## Context

当前 `server/index.ts:142-159` 监听 `agent_completed` 事件后直接调用 `worktreeManager.mergeBack()`。这是 fire-and-forget 模式：无队列、无并发保护、无构建验证、无状态持久化。当多个 issue 并行完成时，并发 `git checkout` + `merge` 会互相冲突；合并后若引入不可构建代码（如 #13），master 构建失败但无自动回滚。

**关键代码现状：**
- `WorktreeManager.mergeBack()` (`git/worktree-manager.ts:167-243`)：完整的 git merge 流程，但合并后立即 `remove()` 清理 worktree，无构建验证
- `EventBus` (`services/event-bus.ts`)：强类型 EventMap，26 个事件类型，通过 `emit()` / `on()` 使用
- `IssueRepo` (`db/issue-repo.ts`)：通用 `update()` 方法支持动态 SET 子句，可扩展 `mergeState`
- DB schema version 当前为 13，迁移模式为 `migrateToVersionN()` + `ALTER TABLE`
- Web SSE hook (`web/src/hooks/useSSE.ts`)：订阅 EventName 列表，收到事件后 invalidate React Query cache
- Web types (`web/src/lib/types.ts`)：前端 `Issue` 接口需同步添加 `mergeState`

## Goals / Non-Goals

**Goals:**
- 串行合并队列：`agent_completed` → enqueue → 逐个 processNext，消除并发 merge race
- 合并后构建验证：`npm run build` 通过后才清理 worktree，失败则 `git reset --hard HEAD~1` 回滚
- `mergeState` 持久化到 DB：`pending | merging | merged | build-failed | conflict`
- API + SSE 实时通知前端合并状态变化
- Server 重启后从 DB 恢复未完成的合并任务

**Non-Goals:**
- 不做并发合并（串行队列是核心设计约束）
- 不做 GitHub PR merge（仅本地 git merge）
- 不做自定义构建命令配置（固定 `npm run build`）
- 不做合并队列优先级调度（FIFO）
- 不做合并失败的自动修复（仅提供重试按钮）

## Decisions

### D1: MergeQueue 作为独立 class，不嵌入 WorktreeManager

`MergeQueue` 独立于 `WorktreeManager`，职责单一：管理队列状态和编排 merge + build verify 流程。`WorktreeManager` 仍然负责底层 git 操作（mergeBack、remove），MergeQueue 调用它们。

**理由：** WorktreeManager 是纯 git 操作层，不应知道队列和构建验证。MergeQueue 是编排层，两者关注点不同。

**Alternatives considered:**
- 在 WorktreeManager 内部加队列逻辑 → 违反 SRP，git 操作和队列状态耦合
- 在 server/index.ts 内联实现 → 无法测试，逻辑散落在启动代码中

### D2: 内存队列 + DB 状态持久化，不引入外部队列

队列条目存在内存 Map 中（快速查找），`mergeState` 同步写入 DB 的 `merge_state` 列。Server 重启时从 DB 扫描 `merge_state IN ('pending', 'merging')` 恢复。

**理由：** mohist 是单进程应用，无分布式需求。内存队列简单高效，DB 持久化保证重启不丢状态。

**Alternatives considered:**
- 纯 DB 队列（每条 SELECT + UPDATE）→ 每次状态变更都要 DB 查询，延迟高
- SQLite-backed job queue 库（如 better-queue、bullmq）→ 引入新依赖，过度设计

### D3: mergeBack 不再自动清理 worktree，由 MergeQueue 决定

修改 `WorktreeManager.mergeBack()` 的行为：合并成功后**不**调用 `this.remove()`，改为返回成功结果，由 MergeQueue 在构建验证通过后调用 `worktreeManager.remove()`。

**理由：** 当前 mergeBack 在合并成功后立即清理 worktree。但构建验证需要在合并后、清理前执行。如果 mergeBack 先清理了 worktree，构建失败时无法重试（worktree 已删）。

**Alternatives considered:**
- 在 MergeQueue 中重新 checkout 分支 → 不必要，保留 worktree 更简单
- mergeBack 加参数控制是否清理 → API 不清晰，职责分离更好

### D4: 构建验证用 `execFile('npm', ['run', 'build'])` + 超时

直接在 `project.path` 上执行 `npm run build`，5 分钟超时，失败则 `git reset --hard HEAD~1` 回滚到合并前状态。

**理由：** `npm run build` 是项目已有的构建入口，不需要额外配置。在 project.path 上执行因为 mergeBack 已经将代码合并到 baseBranch。

**Alternatives considered:**
- 配置化构建命令 → 当前只有一个项目，YAGNI
- Docker 内构建 → 太重，构建失败回滚是本地场景
- 只跑 TypeScript 类型检查而非完整构建 → 类型检查不覆盖所有构建失败场景

### D5: EventBus 新增 4 个 merge 事件类型

在 `EventMap` 中添加 `merge_queued`、`merge_started`、`merge_completed`、`merge_failed`，复用现有 SSE 基础设施推送到前端。

**理由：** 前端已通过 `useSSE` hook 监听事件并 invalidate React Query cache，新增 merge 事件只需：
1. 后端 EventMap 加 4 个条目
2. SSE events API 的 `ALL_EVENT_TYPES` 加 4 个事件名
3. 前端 `types.ts` 的 EventMap 加 4 个条目
4. 前端 `useSSE.ts` 的 eventTypes 列表加 4 个名称 + switch case invalidate queries

**Alternatives considered:**
- 复用 `stage_changed` 事件 → mergeState 不是 stage，语义不同
- 轮询 API → 实时性差，SSE 已是项目标准模式

### D6: API 路由放在 issue routes 内，不单独建 merge-queue routes

`GET /api/issues/merge-queue/status` 和 `POST /api/issues/:number/retry-merge` 挂在 `createIssueRoutes` 下（server mounts at `/api/issues`），实际完整路径为 `/api/issues/merge-queue/status` 和 `/api/issues/:number/retry-merge`。

**理由：** merge 操作的核心实体是 issue，不需要独立路由模块。`createIssueRoutes` 已经接收 worktreeManager 参数，再加 mergeQueue 参数自然。

**Alternatives considered:**
- 独立 `createMergeQueueRoutes()` → 只有两个端点，不值得独立文件
- 放在 server/index.ts 内联 → 不可测试

### D7: DB migration 为 schema version 14

添加 `migrateToVersion14()`：`ALTER TABLE issues ADD COLUMN merge_state TEXT`。遵循现有迁移模式。

**理由：** 最小化变更。`merge_state` 为 nullable TEXT，无需默认值，不影响现有数据。

## Risks / Trade-offs

**[并发 merge race 已消除但仍需注意 git 工作目录状态]** → MergeQueue 串行确保同一时刻只有一个 merge 操作，但 `WorktreeManager.mergeBack()` 会 `git checkout baseBranch`，这期间用户若在 project.path 上手动 git 操作会冲突。Mitigation：mergeBack 已有 stash 逻辑处理 dirty working directory。

**[构建验证增加合并延迟]** → 每个 issue 合并后需等 npm run build 完成（可能 30s-5min），队列中后续 issue 必须等待。Mitigation：这是正确性保证的代价，串行确保 base branch 可构建。构建超时 5 分钟防止卡住。

**[git reset --hard HEAD~1 假设 merge commit 是最后一个 commit]** → 如果 baseBranch 在 mergeBack 后被外部 push 了新 commit，`HEAD~1` 会回滚错误的 commit。Mitigation：mohist 管理的项目是本地仓库，正常使用不会有外部 push。可以在 reset 前验证 HEAD commit message 包含 merge 信息。

**[Server 重启时 merging 状态的原子性]** → 如果 server 在 mergeBack 执行过程中崩溃，DB 状态为 `merging`，重启后重置为 `pending` 重新入队。但此时 git 可能处于中间状态（已 checkout baseBranch 但未完成 merge）。Mitigation：`mergeBack()` 开头先 `git checkout baseBranch` 再 `git merge`，如果 merge 已完成则重试时 `git merge` 会说 already up-to-date；如果 merge 未完成则 `git merge --abort` 清理。在 MergeQueue.processItem 的 mergeBack 调用前加 `git merge --abort` 预清理。

## Migration Plan

1. **DB schema 14**：新增 `merge_state` 列，nullable TEXT，不影响现有数据
2. **替换 server/index.ts 的 agent_completed handler**：从直接调用 `mergeBack()` 改为 `mergeQueue.enqueue()`
3. **修改 WorktreeManager.mergeBack()**：移除末尾的 `this.remove()` 调用，改为返回成功让 MergeQueue 决定何时清理
4. **保留现有 `POST /:number/merge` 手动端点**：改为也通过 MergeQueue 执行（enqueue），保持 API 兼容
5. **前端无 breaking change**：`mergeState` 字段新增，不删除现有字段；新 SSE 事件增量添加

**Rollback:** 如果 MergeQueue 有问题，可以临时回退到直接调用 `worktreeManager.mergeBack()` 的逻辑。DB 的 `merge_state` 列为 nullable，不影响回滚。

## Open Questions

- 构建验证的 npm run build 是否需要在 worktree 路径（而非主仓库路径）执行？当前设计选择主仓库路径因为 mergeBack 已合并代码。如果项目有 monorepo 结构（如当前 mohist 的 `packages/cli`），需要验证 `npm run build` 在项目根目录能正确构建所有子包。
