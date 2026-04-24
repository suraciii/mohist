## ADDED Requirements

### Requirement: 用户消息在 LLM 调用前持久化

`POST /api/explore/:id/messages` SHALL 在调用 LLM（`runExploreAgent`）之前将用户消息写入 DB。即使后续 LLM 调用失败或 SSE 流中断，用户消息 SHALL NOT 丢失。

#### Scenario: 用户消息先写 DB 再调 LLM

- **WHEN** 用户发送消息 `POST /api/explore/:id/messages` with `{ content: "..." }`
- **THEN** 系统先调用 `addMessage(sessionId, 'user', content)` 写入 DB
- **AND** 然后才调用 `runExploreAgent()` 发起 LLM 请求

#### Scenario: LLM 调用失败时用户消息仍保留

- **WHEN** 用户消息已写入 DB
- **AND** LLM 调用失败（抛出异常）
- **THEN** 用户消息仍存在于 DB 中
- **AND** 后续 `GET /api/explore/:id` 返回的消息列表包含该用户消息

### Requirement: 助手回复在 SSE 流中断后可靠保存

SSE stream 循环结束后（正常或异常退出），系统 SHALL 将已累积的助手回复内容写入 DB。在 catch 路径中，系统 SHALL 尝试保存 stream 中断前已接收的部分内容。

#### Scenario: SSE 流正常完成时保存完整回复

- **WHEN** SSE stream 循环正常完成（无异常）
- **THEN** 系统将完整的 `finalText` 和 `toolCallRecords` 通过 `addMessage(sessionId, 'assistant', ...)` 写入 DB

#### Scenario: SSE 流中断时保存已接收的部分回复

- **WHEN** SSE stream 循环因异常中断（进入 catch 块）
- **AND** 此时 `assistantContent` 已累积了部分文本（可能为空字符串）
- **THEN** 系统 SHALL 尝试将 `assistantContent` 写入 DB 作为助手消息
- **AND** 如果 `assistantContent` 为空字符串，SHALL NOT 写入空消息

#### Scenario: 消息保存失败不影响错误响应

- **WHEN** SSE 流中断且 catch 块中尝试保存消息时 DB 写入也失败
- **THEN** 系统 SHALL 记录错误日志但不影响 SSE 错误响应的发送
- **AND** 原始错误信息仍通过 SSE `done` 事件发送给客户端

### Requirement: 消息写入不阻塞 SSE 流式体验

用户消息和助手回复的 DB 写入 SHALL NOT 影响 SSE 流的实时推送。用户 SHALL 仍然逐字看到回复内容。

#### Scenario: SSE 流式推送不受 DB 写入影响

- **WHEN** 助手回复正在通过 SSE 流式推送给客户端
- **THEN** DB 写入操作不在 stream 循环的热路径上阻塞
- **AND** 用户看到的流式体验与修改前一致
