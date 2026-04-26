## Why

ACP session spawn 日志的 `taskId` 字段存储了完整 prompt 文本（`task.slice(0, 100)`）而非实际 task ID（如 `T-001`），导致日志无法按 task 维度追踪和过滤 ACP session 执行。这是一个数据正确性 bug — 字段名暗示存储的是 ID，实际存储的是 prompt 内容。

## What Changes

- 在 `AcpSessionOptions` 中添加可选 `taskId?: string` 字段，允许调用方传入实际 task ID
- `ralph-executor.ts` 调用 `runAcpSession()` 时传入 `taskId: nextTask.id`
- `acp-session.ts` 日志和 `writeSessionLog` 中使用 `taskId` 记录实际 task ID，原 `task.slice(0, 100)` 改为 `promptPreview` 字段名

## Capabilities

### New Capabilities

### Modified Capabilities

- `agent-runtime`: ACP session 日志字段的语义修正（`taskId` 存储实际 ID，新增 `promptPreview` 字段）
- `ralph-task-execution`: task 执行时将 task ID 传递给 ACP session

## Impact

- `packages/cli/src/agent-runtime/acp-session.ts` — `AcpSessionOptions` 类型、spawn 日志、session log 写入
- `packages/cli/src/openspec/ralph-executor.ts` — `_acpSessionRunner` 调用参数
- 其他调用 `runAcpSession()` 的位置需确认是否受影响（向后兼容，`taskId` 为可选字段）
