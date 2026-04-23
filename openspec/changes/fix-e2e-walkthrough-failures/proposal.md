## Why

E2E walkthrough 暴露了 2 个严重 bug 和 1 个中等问题，导致 Build stage 完全无法执行：

1. **Plan 阶段 self-review agent 修改 tasks.json 的 passes 字段为 true**（严重）— agent 在自审时使用 write_file 工具修改了 tasks.json，将所有任务的 `passes` 改为 `true`。Build stage 的 RalphExecutor 读取后认为所有任务已通过，跳过执行，返回 `completed=0, total=3` → pipeline 失败
2. **Pipeline 失败后完全不可观测**（严重）— `mo issue logs` 返回空、server.log 无错误、API 只返回 `draft/blocked` 状态无错误消息。诊断需要 kill server 加临时 console.error
3. **Server 重启后 opencode 不在 PATH**（中等）— 新 server 进程 spawn opencode 失败（ENOENT）

## What Changes

- 重置 tasks.json 中所有任务的 `passes` 为 `false`，在 Build stage 开始执行前（`runRalphLoop` 入口处）
- Pipeline 失败时将错误消息写入 issue 的 `approvalState`（新增 error 状态），通过 API 和 CLI 暴露
- `mo issue logs` 增加 pipeline 关键事件（build_started, build_completed, build_failed, task_started, task_completed, task_failed）
- 使用 `resolveOpencodeBinPath()` 的绝对路径 spawn opencode，不依赖 PATH

## Capabilities

### New Capabilities

- `build-stage-tasks-reset`: Build stage 启动时强制重置 tasks.json 的 passes 字段，防止 plan stage 产物被 self-review 污染
- `pipeline-error-visibility`: Pipeline 失败时的错误信息存储和暴露（API + CLI）

### Modified Capabilities

- `workflow-log`: `mo issue logs` 命令增加 pipeline 级别事件（build/task 的 start/complete/fail）
- `ralph-task-execution`: ACP session spawn 使用绝对路径

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — 入口处重置 passes、spawn 使用绝对路径
- `packages/cli/src/services/agent-runner-service.ts` — 失败时写入 errorState
- `packages/cli/src/api/issues.ts` — API 返回 errorState、logs 端点增加 pipeline 事件
- `packages/cli/src/cli/commands/issue.ts` — show 命令显示错误信息、logs 命令增加 pipeline 事件
