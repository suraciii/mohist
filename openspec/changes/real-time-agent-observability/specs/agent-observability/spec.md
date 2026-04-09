## ADDED Requirements

### Requirement: Main Agent 流式推送思考文本

`runAgentLoop` SHALL 遍历 `streamText` 的 `fullStream`，对每个 `text-delta` 事件通过 EventBus 推送 `agent_text_chunk` 事件。

事件 payload：`{ issueId, projectId, text, stepIndex }`

#### Scenario: Main Agent 思考时 SSE 客户端收到文本
- **WHEN** Main Agent 的 LLM 输出文本 "Let me read the workflow..."
- **THEN** EventBus emit `agent_text_chunk`，payload 包含 `{ text: "Let me read the workflow..." }`
- **AND** SSE 客户端实时收到该事件

#### Scenario: 无 EventBus 时不推送
- **WHEN** runAgentLoop 未传入 eventBus 参数
- **THEN** 不尝试 emit 事件，agent loop 正常执行

### Requirement: Main Agent 推送 tool call 生命周期

`runAgentLoop` SHALL 对 fullStream 的 `tool-call` 和 `tool-result` 事件推送 `main_tool_call` 事件。

事件 payload：
- started：`{ issueId, projectId, executionId, toolName, state: 'started', args, stepIndex }`
- completed：`{ issueId, projectId, executionId, toolName, state: 'completed', result, duration, stepIndex }`

#### Scenario: tool call 开始
- **WHEN** Main Agent 的 LLM 决定调用 `spawn_coder` tool
- **THEN** EventBus emit `main_tool_call`，state 为 `started`，executionId 为 AI SDK 的 toolCallId

#### Scenario: tool call 完成
- **WHEN** `spawn_coder` tool execute 返回结果
- **THEN** EventBus emit `main_tool_call`，state 为 `completed`，result 为 tool 返回值
- **AND** duration 为从 started 到 completed 的毫秒数

### Requirement: ACP session 推送 agent 文本

`runAcpSession` SHALL 在收到 `agent_message_chunk` 事件时，通过 EventBus 推送 `coder_text_chunk` 事件。

事件 payload：`{ issueId, projectId, executionId, acpSessionId, text }`

#### Scenario: spawn_coder 内部 agent 输出文本
- **WHEN** spawn_coder 的 ACP session 收到 agent_message_chunk
- **AND** eventBus 和 executionId 存在
- **THEN** EventBus emit `coder_text_chunk`，payload 包含文本 chunk 和 executionId

#### Scenario: ralph task 内部 agent 输出文本
- **WHEN** ralph task 的 ACP session 收到 agent_message_chunk
- **AND** eventBus 和 executionId 存在
- **THEN** EventBus emit `coder_text_chunk`，payload 包含文本 chunk 和 executionId

#### Scenario: 无 executionId 时不推送文本事件
- **WHEN** runAcpSession 未收到 executionId
- **THEN** agent 文本只累积到返回值中，不推送 coder_text_chunk 事件

### Requirement: ACP session 推送 tool call

`runAcpSession` SHALL 在收到 `tool_call` 类型的 sessionUpdate 时，通过 EventBus 推送 `coder_tool_call` 事件。

事件 payload：`{ issueId, projectId, executionId, acpSessionId, toolName, state, args?, result? }`

#### Scenario: spawn_coder 内部 agent 调用工具
- **WHEN** spawn_coder 的 ACP session 报告 tool_call 事件
- **AND** eventBus 和 executionId 存在
- **THEN** EventBus emit `coder_tool_call`，payload 包含 toolName 和 state

#### Scenario: ralph task 内部 agent 调用工具
- **WHEN** ralph task 的 ACP session 报告 tool_call 事件
- **AND** eventBus 和 executionId 存在
- **THEN** EventBus emit `coder_tool_call`，payload 包含 toolName 和 state

### Requirement: Ralph loop 推送 task 级别进度

ralph loop SHALL 在每个 task 开始、完成、失败时推送 `ralph_task_update` 事件，并在每次 task 状态变化时推送 `ralph_loop_progress` 事件。

ralph_task_update payload：`{ issueId, projectId, executionId, taskId, taskIndex, totalTasks, status, attempt, output?, error? }`

ralph_loop_progress payload：`{ issueId, projectId, executionId, completed, failed, total }`

#### Scenario: task 开始执行
- **WHEN** ralph loop 开始执行 task T-003（共 5 个 task）
- **THEN** EventBus emit `ralph_task_update`，status 为 `started`，taskIndex 为 2，totalTasks 为 5

#### Scenario: task 完成
- **WHEN** task T-003 成功完成
- **THEN** EventBus emit `ralph_task_update`，status 为 `completed`
- **AND** EventBus emit `ralph_loop_progress`，completed 为 3（前两个 + 当前）

#### Scenario: task 失败重试
- **WHEN** task T-003 第 1 次失败，准备重试
- **THEN** EventBus emit `ralph_task_update`，status 为 `retrying`，attempt 为 1

#### Scenario: task 最终失败
- **WHEN** task T-003 达到最大重试次数后仍失败
- **THEN** EventBus emit `ralph_task_update`，status 为 `failed`，error 包含失败原因

### Requirement: executionId 层级关联

所有 L1/L2 事件 SHALL 携带 `executionId`，与 L0 的 `main_tool_call` 事件的 executionId 对应，使 UI 能建立事件层级关系。

#### Scenario: UI 根据 executionId 归属事件
- **WHEN** UI 收到 `main_tool_call(executionId="call_abc", toolName="spawn_coder", state="started")`
- **AND** 随后收到多个 `coder_text_chunk(executionId="call_abc")` 事件
- **THEN** UI 能将这些 coder_text_chunk 事件归组到该 spawn_coder 调用下

#### Scenario: executionId fallback
- **WHEN** ToolRegistry 的 executionId slot 在 tool execute 时为空
- **THEN** tool execute 自行生成 UUID 作为 executionId
- **AND** 后续所有事件使用该 ID
