## Context

Mohist 的 agent 系统有三层执行结构：

```
L0: Main Agent (runAgentLoop, streamText)
L1: spawn_coder / ralph_loop (ACP session)
L2: ralph task (ACP session, 循环执行)
```

当前 L0 的 `runAgentLoop` 用 `consumeStream()` 等待 streamText 完成，中间过程完全不可见。L1/L2 的 ACP session 通过 `sessionUpdate` 回调接收事件，但只在 `spawn_coder` 里推送 `tool_call` 事件到 EventBus，`agent_message_chunk` 只累积不推送，`ralph-executor` 里完全不推送。两处 ACP session 管理（`runAcpOneshot` 和 `executeTaskWithSpawn`）是重复实现，进程清理和文本截断策略不一致。

已有基础设施：EventBus + SSE 端点（自动转发所有事件）、workflow_log 表（只 spawn_coder 写入）、explore.ts 里的 fullStream 先例。

## Goals / Non-Goals

**Goals:**

- 用户在 WebUI 能实时看到 Main Agent 的思考文本和 tool call 生命周期
- 用户能实时看到 spawn_coder / ralph task 内部的 agent 文本和 tool call
- 用户能看到 ralph loop 的 task 级别进度（哪个 task 在跑、完成几个、失败几个）
- 所有事件有层级关联，UI 能把 L1/L2 事件归属到 L0 的具体 tool call
- 统一 ACP session 管理，消除代码重复

**Non-Goals:**

- 用户中途介入（取消、重试、追加指令）——本次不实现，但数据模型预留 executionId
- Session 消息持久化到 SQLite——本次只推事件，不改持久化策略
- WebUI 前端实现——本次只做后端数据管道
- 历史回放——事件是实时的，重启后不保留在 EventBus 中（workflow_log 仍可查历史）

## Decisions

### Decision 1: runAgentLoop 从 consumeStream 改为 fullStream 遍历

**选择：** 一步到位替换 `consumeStream()` 为 `for await (const part of result.fullStream)`

**理由：**
- `consumeStream()` 只是消费 textStream 等待结束，替换为 fullStream 是等价操作
- 项目已有 fullStream 先例（explore.ts:191 遍历 fullStream 处理 text-delta / tool-call / tool-result）
- `result.text` / `result.steps` / `result.finishReason` 这些 Promise 在流结束后自然 resolve，不受影响
- 渐进式没有意义——不改 consumeStream 就拿不到事件，等于没做

**替代方案：** 在 consumeStream 外加 hook → 无法做到，consumeStream 内部不暴露事件

### Decision 2: 统一 ACP session 管理

**选择：** 新建 `agent-runtime/acp-session.ts`，导出 `runAcpSession()`，合并两者的能力

**理由：**
- `runAcpOneshot`（spawn-coder.ts）和 `executeTaskWithSpawn`（ralph-executor.ts）是同一件事的两份实现
- 两者都需要改造 sessionUpdate 回调以推送实时事件，统一后只改一处
- `executeTaskWithSpawn` 有更好的清理（stream cancel）和截断保护（2MB），`runAcpOneshot` 有 workflowLog 和 EventBus 接入，合并后兼得
- 返回值统一为 `{ text, success, error?, acpSessionId? }`

**替代方案：** 各改各的 → 维护两份几乎相同的 sessionUpdate 回调改造，差距持续扩大

### Decision 3: executionId 关联通过 ToolRegistry slot 传递

**选择：** ToolRegistry 增加 executionId slot，runAgentLoop 在 fullStream 的 tool-call 事件时设置，tool execute 内部读取

**理由：**
- AI SDK 的 tool execute 函数是纯 `async (params) => string`，无法从外部注入 toolCallId
- ToolRegistry 已经是 tool 系统的核心对象，加一个 slot 是最小侵入
- 时序依赖：AI SDK 的 fullStream `tool-call` 事件在 `execute()` 之前触发（LLM 先输出参数，再执行），所以设置时序是正确的
- fallback：如果 execute 内部读不到 executionId，自行 generateId()，保证不阻塞

**替代方案：**
- 全局变量 → 并发不安全（虽然当前 mohist main agent 是顺序的）
- 时间窗口匹配 → 不可靠
- 改造 AI SDK tool 定义增加 context 参数 → 过度改造

### Decision 4: 事件类型设计

新增 6 种事件类型，保持与现有事件格式一致（issueId + projectId）：

| 事件 | 层级 | 触发时机 | 核心字段 |
|------|------|----------|----------|
| `agent_text_chunk` | L0 | streamText text-delta | text, stepIndex |
| `main_tool_call` | L0 | streamText tool-call / tool-result / tool-error | executionId, toolName, state, args, result, error, duration |
| `coder_text_chunk` | L1/L2 | ACP agent_message_chunk | executionId, acpSessionId, text |
| `coder_tool_call` | L1/L2 | ACP tool_call | executionId, acpSessionId, toolName, state |
| `ralph_task_update` | L1 | ralph task 开始/完成/失败/重试 | executionId, taskId, taskIndex, totalTasks, status, attempt, error |
| `ralph_loop_progress` | L1 | ralph loop 进度变化 | executionId, completed, failed, total |

**状态设计：**
- `main_tool_call`: `started` | `completed` | `failed`
- `coder_tool_call`: `started` | `completed`
- `ralph_task_update`: `started` | `completed` | `failed` | `retrying`

**设计原则：** L1/L2 事件都带 `executionId`，UI 通过它关联到 L0 的 `main_tool_call` 事件。

### Decision 5: 高频事件节流（可选优化）

**选择：** 在 T-003 中实现可选的节流机制，默认 100ms

**理由：**
- agent_message_chunk 可能每秒产生数十个事件
- 100ms 节流可将事件量减少 10 倍，用户体验无明显损失
- 可在运行时通过 options 控制，默认开启

**实现方式：**
```typescript
let lastEmitTime = 0;
const THROTTLE_MS = options.throttleMs ?? 100;

if (now - lastEmitTime > THROTTLE_MS) {
  eventBus.emit('coder_text_chunk', { ... });
  lastEmitTime = now;
}
```

## Risks / Trade-offs

**[Risk] fullStream 遍历改变消息收集方式** → `result.steps` 在流结束后仍可 await，消息收集可改为从 steps 取（与现在一样），fullStream 只用于事件推送，不影响最终结果。

**[Risk] executionId 时序依赖 AI SDK 行为** → 如果 AI SDK 改变了 tool-call 事件和 execute 的触发顺序，fallback 机制（读不到就 generateId）保证不崩溃，只是关联失效，UI 回退到时间窗口匹配。

**[Risk] 高频 agent_text_chunk 事件增加 SSE 负载** → Decision 5 的节流机制缓解此问题。现阶段展示全部信息是需求，未来可在 SSE 端加 client-side 过滤或节流，不影响后端。

**[Trade-off] 不改 DB schema** → 历史回放仍依赖 workflow_log（只有 spawn_coder 写入，ralph task 不写）。未来如需完整回放，需要扩展 workflow_log 或新增表，但本次聚焦实时推送。
