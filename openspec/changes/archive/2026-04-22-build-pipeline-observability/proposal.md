## Why

Build 阶段在 2ms 内静默完成（`completed: false`），issue 卡在 `build` stage，但用户看不到任何线索。深入调查发现整个 mohist 存在系统性观测性不足：

1. **Build 阶段对用户完全黑盒** — 用户只能看到 "Pipeline started/completed"，不知道 task 执行进度
2. **28 个 swallow catch** — 错误被静默吞掉，可能隐藏 bug（如 eventBus 发射失败）
3. **日志与审计分离** — 关键事件只写文件日志或只写 DB，没有统一策略
4. **EventBus 事件易失** — `ralph_task_update` 等事件只通过 SSE 发送，断线即丢失

## What Changes

### Build 阶段观测（核心）
- 在 `runPipelineBuildStage` 中添加 `detectOpenSpecChange` 结果日志、tasks 快照日志、ralph loop 返回值日志
- 在 `runRalphLoop` 中添加 tasks 读取状态日志、`findNextPendingTask` 结果日志、循环退出原因日志
- 添加 build 结果 sanity check：当 `completed === 0 && total > 0` 时返回失败并记录 WARN 日志
- 将 build 阶段关键事件同时写入 workflow_log（DB 审计）和文件日志

### 用户可见性改进
- 新增 SSE 事件：`build_stage_started`, `build_tasks_snapshot`, `build_stage_completed`, `build_stage_failed`
- 新增 API 端点：`GET /api/issues/:number/build-status` — 返回实时 build 进度和 task 状态
- 新增 API 端点：`GET /api/issues/:number/tasks` — 返回 tasks.json 的当前状态（只读）

### 系统性观测改善
- 修复关键 swallow catch：在 `workflow-controller.ts` 和 `ralph-executor.ts` 的 eventBus 发射失败点添加日志
- 统一事件持久化：创建 `emitPersistent` 包装器，关键事件同时写入 eventBus 和 workflow_log
- 添加 ACP session 生命周期日志：启动、完成、超时、错误

## Capabilities

### New Capabilities
- `build-stage-logging`: Build 阶段关键路径的结构化日志，覆盖 change 检测、tasks 读取、ralph 循环执行、结果返回全流程
- `build-stage-visibility`: 用户可实时查看 build 进度、task 执行状态、历史记录
- `event-persistence`: 统一的事件持久化策略，关键事件同时进入 SSE 和 DB

### Modified Capabilities
- `ralph-task-execution`: 在 `runRalphLoop` 入口和退出点添加日志，记录 tasks 状态和循环退出原因
- `workflow-execution`: 修复 swallow catch，添加 eventBus 发射失败日志
- `http-api`: 新增 `/api/issues/:number/build-status` 和 `/api/issues/:number/tasks` 端点

## Impact

- `packages/cli/src/workflow/workflow-controller.ts` — build 阶段日志、swallow catch 修复
- `packages/cli/src/openspec/ralph-executor.ts` — ralph 循环日志、swallow catch 修复
- `packages/cli/src/agent-runtime/acp-session.ts` — ACP session 生命周期日志
- `packages/cli/src/services/event-bus.ts` — 添加 `emitPersistent` 方法、新事件类型
- `packages/cli/src/api/issues.ts` — 新增 build-status 和 tasks API 端点
- 前端可消费新 SSE 事件展示 build 进度条
