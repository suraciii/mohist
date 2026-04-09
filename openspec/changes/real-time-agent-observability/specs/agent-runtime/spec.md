## MODIFIED Requirements

### Requirement: LLM tool loop
The system SHALL implement an LLM tool loop using Vercel AI SDK v5 `streamText()` with `maxSteps`. The loop SHALL support: tool definition with Zod schema, automatic tool calling cycle (LLM returns tool_call → execute → feed result back → continue), and text generation until LLM stops.

`runAgentLoop` SHALL 遍历 `streamText` 的 `fullStream`（替代 `consumeStream()`），逐事件处理并推送观测事件到 EventBus。

函数签名新增可选参数：`eventBus?: EventBus`、`eventContext?: { issueId: string; projectId: string }`。

#### Scenario: Tool calling cycle
- **WHEN** the LLM returns a tool_call
- **THEN** the runtime SHALL execute the tool and feed the result back to the LLM
- **THEN** the LLM SHALL continue generating (call more tools or produce text)

#### Scenario: Max steps reached
- **WHEN** the LLM tool calling cycle reaches maxSteps without producing a final text response
- **THEN** the runtime SHALL stop and return the last assistant message

#### Scenario: fullStream 遍历替代 consumeStream
- **WHEN** runAgentLoop 被调用
- **THEN** 使用 `for await (const part of result.fullStream)` 遍历事件
- **AND** 对 text-delta 推送 `agent_text_chunk` 事件，包含 stepIndex
- **AND** 对 tool-call 推送 `main_tool_call(state='started')` 事件
- **AND** 对 tool-result 推送 `main_tool_call(state='completed')` 事件，包含 duration
- **AND** 对 tool-execute-error 推送 `main_tool_call(state='failed')` 事件，包含 error
- **AND** result.text / result.steps / result.finishReason 在流结束后仍可正常 await

#### Scenario: stepIndex 在多次 LLM 调用中递增
- **WHEN** maxSteps > 1 且 LLM 在第一轮调用 tool 后继续生成
- **THEN** 第二轮 LLM 调用产生的 text-delta 和 tool-call 事件携带 stepIndex=1
- **AND** 第三轮携带 stepIndex=2，以此类推

#### Scenario: 无 EventBus 时向后兼容
- **WHEN** runAgentLoop 未传入 eventBus
- **THEN** fullStream 遍历仍正常工作（不 emit 事件）
- **AND** 返回值结构不变

#### Scenario: Tool execute 异常时推送 failed 状态
- **WHEN** tool execute 函数抛出异常
- **THEN** 捕获异常并推送 `main_tool_call(state='failed')` 事件
- **AND** 将异常信息作为 tool result 返回给 LLM（保持现有行为）
