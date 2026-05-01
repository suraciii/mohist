## Why

AgentRunnerService 当前使用三个分散的内存态并发控制结构（`activeAgents` Map、`pendingGates` Map、`conflictResolutionInProgress` Set），存在两个根本性问题：（1）竞态窗口 — 某些入口点（如 `startPipeline`）不检查 `conflictResolutionInProgress`，rebase 和 pipeline 可同时操作同一 issue 的 worktree；（2）状态易失 — 所有并发状态纯内存态，服务器重启后 running/pending 信息全部丢失，无法恢复。此外 API 层每个 endpoint（`/start`、`/approve`、`/reopen`、`/rebase`、`/propose`）重复 ~30 行 `isRunning()` + `maxConcurrentAgents` 检查逻辑，维护成本高且容易遗漏。

## What Changes

- 新增 `issue_task_queue` SQLite 表，持久化 per-issue 任务队列状态
- 新增 `IssueTaskQueueRepo` 数据访问层
- AgentRunnerService 新增 per-issue FIFO 任务队列能力（`enqueue`、`cancel`、`cancelAll`、`getQueueStatus`），替代 `activeAgents` Map 和 `pendingGates` Map
- 任务类型：`start-pipeline`、`resume-pipeline`、`rebase`，其中 rebase 内部包含 conflict resolution 子步骤
- 全局并发槽位（max 8）限制同时执行的任务数
- API endpoints（`/start`、`/approve`、`/reopen`、`/rebase`、`/propose`）改为调用 `enqueue()` 返回 202 `{ taskId, status }`
- 新增 `GET /issues/:number/queue` 和 `DELETE /issues/:number/queue/:taskId` API
- `/force-stop` 改为 `cancelAll()`
- 服务器重启时从 DB 恢复队列：running tasks 标记完成/失败，pending tasks 重新入队
- **BREAKING**: `/start`、`/approve`、`/reopen`、`/rebase`、`/propose` 返回 202 替代原来的 200
- 移除 `activeAgents` Map、`pendingGates` Map、`conflictResolutionInProgress` Set
- 移除 API 层分散的 `isRunning()` / `maxConcurrentAgents` 检查（~30 行/endpoint → ~5 行 enqueue）

## Capabilities

### New Capabilities

- **issue-task-queue**: Per-issue FIFO 任务队列，支持优先级插入、DB 持久化、重启恢复。任务类型包括 start-pipeline、resume-pipeline、rebase。全局并发槽位控制。

### Modified Capabilities

- **agent-pool**: 现有的 Concurrent agent execution 和 Per-issue agent tracking 需求将被 issue-task-queue 替代。`activeAgents` Map → 队列的 `runningSlots`，`isRunning()` → `getQueueStatus()`。Queue processing 的 FIFO 语义从全局改为 per-issue + 全局槽位。
- **http-api**: `/start`、`/approve`、`/reopen`、`/rebase`、`/propose` 返回值从 200 改为 202；新增 queue 查询和取消 endpoints；移除每个 endpoint 中的并发检查代码。
- **agent-runtime**: AgentRunnerService 新增 enqueue/cancel/getQueueStatus 方法；移除 `activeAgents` Map 和 `pendingGates` Map；`getStatus()` 返回值结构变化。

## Impact

- **packages/cli/src/services/agent-runner-service.ts**: 核心变更 — 新增队列逻辑，移除 `activeAgents`/`pendingGates` 内存结构
- **packages/cli/src/api/issues.ts**: 移除 ~30 行/endpoint 的并发检查，改为 enqueue 调用；移除 `conflictResolutionInProgress` Set
- **packages/cli/src/api/propose.ts**: 同上，改为 enqueue
- **packages/cli/src/db/**: 新增 `issue_task_queue` 表 migration 和 `IssueTaskQueueRepo`
- **packages/cli/src/server/index.ts**: 重启恢复逻辑，从 DB 加载 pending/running tasks
- **Web UI**: Issue Detail 页面需要适配 202 响应和新的 queue 状态展示
- **CLI**: `mo issue start` 等命令需要适配 202 响应格式
