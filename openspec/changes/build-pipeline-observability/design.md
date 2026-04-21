## Context

mohist 的 build 管线在执行时出现了静默失败：issue #2 的 build 阶段在 2ms 内完成，但用户看不到任何线索，只能看到 issue 卡在 "build" stage。深入代码审查发现更广泛的观测性问题：

1. **Build 阶段对用户完全黑盒** — 用户只能看到 "Pipeline started/completed"，不知道：
   - 有多少个 task 需要执行
   - 当前执行到第几个 task
   - 每个 task 是成功还是失败
   - 为什么 build "完成"了但什么都没做

2. **28 个 swallow catch** — 错误被静默吞掉，包括 eventBus 发射失败、进程清理失败等

3. **日志与审计分离** — 关键事件要么只进文件日志，要么只进 DB，没有统一策略

4. **EventBus 事件易失** — 通过 SSE 发送的事件在客户端断线时丢失

当前已有基础设施：
- `Log.create({ service })` — 文件日志
- `WorkflowLogRepo` — DB 审计日志
- `EventBus` — SSE 事件
- `GET /api/issues/:number/logs` — 查询 workflow_log

但缺少：
- 实时的 build 进度 API
- task 级别的状态查询
- build 阶段专用的事件类型

## Goals / Non-Goals

**Goals:**
- 让用户可以实时查看 build 阶段进度（类似 GitHub Actions 的 job 视图）
- 在 build 阶段的关键决策路径上添加结构化日志（文件 + DB）
- 修复关键 swallow catch，确保错误不被静默吞掉
- 统一事件持久化策略：关键事件同时进入 SSE 和 workflow_log
- 支持前端展示 build 进度条和 task 列表

**Non-Goals:**
- 不修改 ACP session 或 agent 的行为逻辑
- 不修改 tasks.json 的 schema
- 不实现前端 UI（只提供 API 和事件）
- 不修复所有 28 个 swallow catch（只修复可能隐藏 bug 的关键路径）
- 不引入新的日志库或基础设施

## Decisions

### 1. 三层观测策略

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            信息分层策略                                       │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  第一层：实时状态（SSE + API）← 用户最需要                                     │
│  ├── Build 阶段开始/结束（build_stage_started/completed）                    │
│  ├── Task 执行进度（build_tasks_snapshot：total/pending/passed）             │
│  ├── 当前正在执行的 task（ralph_task_update）                                 │
│  └── 错误摘要（build_stage_failed）                                          │
│                                                                              │
│  第二层：历史记录（API 查询）← 用户需要时可以查看                               │
│  ├── GET /api/issues/:number/build-status — 当前状态快照                      │
│  ├── GET /api/issues/:number/tasks — tasks.json 当前状态                      │
│  └── GET /api/issues/:number/logs — workflow_log 历史                         │
│                                                                              │
│  第三层：内部调试（文件日志）← 只有开发者需要                                  │
│  ├── 所有 swallow catch 的详细信息                                            │
│  ├── eventBus 发射失败                                                        │
│  └── 完整的调用栈                                                             │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2. 用户可见的数据结构

```typescript
// GET /api/issues/:number/build-status 返回
{
  "stage": "build",
  "status": "running", // "pending", "running", "completed", "failed"
  "progress": {
    "completed": 3,
    "failed": 0,
    "total": 7,
    "currentTask": "T-004"
  },
  "tasks": [
    { "id": "T-001", "title": "...", "status": "completed", "passes": true },
    { "id": "T-002", "title": "...", "status": "completed", "passes": true },
    { "id": "T-003", "title": "...", "status": "running", "startedAt": "..." },
    // ...
  ]
}
```

### 3. 选择性修复 swallow catch

**修复（添加日志）：**
- `workflow-controller.ts:100, 126` — eventBus.emit 失败（可能丢失重要事件）
- `ralph-executor.ts:295, 311` — eventBus.emit 失败
- `writeTasksFile` — 写入失败（可能丢失进度）

**不修复（预期行为）：**
- `proc.kill()` — 进程已退出是正常的
- `stream.cancel()` — 流已关闭是正常的

### 4. 零工作检测放在 workflow-controller 层

**决策：** 在 `runPipelineBuildStage` 返回前检查 `completed === 0 && total > 0`，返回 `success: false`。

**理由：** "build 成功了但什么都没做"是业务层面异常，应由 controller 判断并返回明确的错误信息给用户。

## Risks / Trade-offs

- **[API 响应时间]** → build-status 需要读取 tasks.json，但文件很小（<20KB），影响可忽略
- **[SSE 事件频率]** → ralph_task_update 可能在短时间内发送多次，需要节流（已有 throttleMs）
- **[零工作检测改变语义]** → 有意为之，防止 issue 错误进入 review 阶段
- **[DB 写入开销]** → workflow_log 是同步写入，但只在关键事件（开始/结束/失败）时写入，不影响性能

## API 设计

### GET /api/issues/:number/build-status

返回当前 build 状态的完整快照，用于页面刷新或轮询。

### GET /api/issues/:number/tasks

返回 tasks.json 的当前状态（只读），用于展示 task 列表。

### SSE 事件扩展

```typescript
// 新事件类型
build_stage_started: { issueId, stage: 'build', changePath, tasksCount, timestamp }
build_tasks_snapshot: { issueId, total, pending, passed, tasks: [...] }
build_stage_completed: { issueId, completed, failed, total, duration, timestamp }
build_stage_failed: { issueId, reason, details, timestamp }
```
