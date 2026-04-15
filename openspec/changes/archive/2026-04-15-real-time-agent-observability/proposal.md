## Why

当用户打开 WebUI 查看正在运行的 issue 时，只能看到阶段变更、tool call 名称和状态指示灯——agent 的思考过程和 spawn_coder 内部的具体行为完全是黑盒。用户无法实时看到 agent 在说什么、在调用什么工具、改了什么文件，只能等到整个 tool call 结束后才能看到结果。这导致用户对 agent 行为缺乏信任感，也无法在早期发现 agent 走偏。

## What Changes

- **统一 ACP session 管理**：将 `spawn-coder.ts` 的 `runAcpOneshot` 和 `ralph-executor.ts` 的 `executeTaskWithSpawn` 合并为 `agent-runtime/acp-session.ts` 的 `runAcpSession`，消除重复代码，统一进程生命周期管理、文本截断保护和事件推送
- **Main Agent 流式事件**：改造 `runAgentLoop` 从 `consumeStream()` 改为遍历 `fullStream`，逐事件推送 LLM 的思考文本（text-delta）和 tool call 生命周期（started/completed）到 EventBus
- **ACP session 实时事件推送**：在统一的 `runAcpSession` 里，将 ACP 的 `agent_message_chunk` 和 `tool_call` 事件实时推送到 EventBus（当前前者只累积不推送，后者只有 spawn_coder 推送而 ralph task 不推送）
- **Ralph loop 进度事件**：ralph loop 的 task 开始/完成/失败事件、loop 级别进度推送到 EventBus
- **层级关联机制**：为每个 tool call 生成 `executionId`，使 L1/L2 事件能关联到 L0 的具体 tool call

## Capabilities

### New Capabilities
- `acp-session`: 统一的 ACP session 管理，合并 runAcpOneshot 和 executeTaskWithSpawn，提供进程生命周期、文本截断、workflowLog 持久化和实时事件推送
- `agent-observability`: 三层（Main Agent / ACP session / Ralph task）实时事件体系，通过 EventBus 推送完整的 agent 思考文本、tool call 生命周期和任务进度

### Modified Capabilities
- `agent-runtime`: runAgentLoop 改为 fullStream 遍历，新增 eventBus 和 eventContext 参数
- `event-bus`: 新增 agent_text_chunk、main_tool_call、coder_text_chunk、coder_tool_call、ralph_task_update、ralph_loop_progress 六种事件类型
- `spawn-coder`: 改为调用统一的 runAcpSession，接收 executionId 参数
- `ralph-task-execution`: executeTaskWithSpawn 改为调用统一的 runAcpSession，接入 EventBus 推送 task 进度事件

## Impact

- **核心文件改造**：`agent-loop.ts`（fullStream 替换 consumeStream）、`spawn-coder.ts`（调用 runAcpSession）、`ralph-executor.ts`（调用 runAcpSession + 接入 EventBus）
- **新增文件**：`agent-runtime/acp-session.ts`（统一 ACP session 管理）
- **事件系统扩展**：`event-bus.ts` 新增 6 种事件类型，SSE 端点自动支持
- **不改 DB schema**：事件仅通过 EventBus/SSE 实时推送，不新增持久化表
- **不改 API 路由**：SSE 端点已自动转发所有 EventBus 事件
- **向后兼容**：现有事件类型和行为不变，新增事件为纯增量
