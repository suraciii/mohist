## Context

ACP session spawn 日志的 `taskId` 字段目前存储 `task.slice(0, 100)` — 即完整 prompt 文本的前 100 字符。`task` 参数实际上是 assembled prompt（以 `[Proposal]` 开头的长文本），不是 task ID。`AcpSessionOptions` 接口只有 `task: string`，缺少 `taskId` 字段。

调用方：
- `ralph-executor.ts:437` — 有 task ID（`nextTask.id`），但未传递
- `explore-acp-service.ts:57,87` — 无 task ID（explore 模式），不需要传

## Goals / Non-Goals

**Goals:**
- `taskId` 日志字段存储实际 task ID
- prompt 预览保留在独立的 `promptPreview` 字段
- 向后兼容：`taskId` 为可选字段，无 task 上下文的调用方无需改动

**Non-Goals:**
- 不修改 `workflow_log` 表结构或历史数据
- 不修改 `_acpSessionRunner` 函数签名以外的 ralph-executor 逻辑

## Decisions

### D1: 在 AcpSessionOptions 添加可选 taskId 字段

在接口中添加 `taskId?: string`，由调用方按需传入。`runAcpSession` 解构时提取 `taskId`，日志中用它替换原来的 `task.slice(0, 100)`。

**Alternatives considered:**
- 重命名 `task` 参数为 `prompt` — 改动面大，所有调用方都要改，不必要
- 从 prompt 文本中解析 task ID — 脆弱且不可靠

### D2: 日志字段命名：taskId + promptPreview

spawn 日志和 session log 中：
- `taskId`: 来自 `options.taskId`（可为 undefined）
- `promptPreview`: 来自 `task.slice(0, 100)`

原 `taskId: task.slice(0, 100)` 拆分为两个字段。

**Alternatives considered:**
- 移除 prompt 预览 — 丢失调试信息，不可取
- 保留原 `task` 字段名 — 与 `options.task` 冲突，易混淆

## Risks / Trade-offs

[已有的 workflow_log 中 `acp_session_start` 事件的 `taskId` 字段存的是 prompt] → 无需修复历史数据，新日志自动使用正确值

## Migration Plan

直接部署即可。`taskId` 为可选字段，`explore-acp-service.ts` 的两个调用点无需修改，日志中 `taskId` 自然为 undefined。

## Open Questions

None.
