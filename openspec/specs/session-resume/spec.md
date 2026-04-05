## Requirements

### Requirement: Session 按 issueId 查找
SessionManager SHALL 提供 `findByIssueId(issueId: number)` 方法，返回该 issue 对应的 session（active 或 paused 状态）。如果没有匹配的 session，返回 undefined。

#### Scenario: 查找存在的 session
- **WHEN** 调用 `sessionManager.findByIssueId(42)`
- **AND** 存在 issueId=42 的 active 或 paused session
- **THEN** 返回该 session

#### Scenario: 查找不存在的 session
- **WHEN** 调用 `sessionManager.findByIssueId(99)`
- **AND** 没有 issueId=99 的 session
- **THEN** 返回 undefined

### Requirement: Session 支持 paused 状态
Session SHALL 支持 `paused` 状态。SessionManager SHALL 提供 `pause(sessionId)` 方法，将 session 标记为 paused。paused session 保留 messages，可以 appendMessage，但不被视为 active session。

#### Scenario: 暂停 session
- **WHEN** 调用 `sessionManager.pause(sessionId)`
- **THEN** session 状态变为 paused
- **AND** session.messages 保持不变

#### Scenario: 向 paused session 追加消息
- **WHEN** 调用 `sessionManager.appendMessage(sessionId, message)`
- **AND** session 处于 paused 状态
- **THEN** 消息被成功追加到 session.messages

#### Scenario: 恢复 paused session
- **WHEN** 调用 `sessionManager.resume(sessionId)`
- **THEN** session 状态变为 active
- **AND** session.messages 保持不变

### Requirement: Agent Loop 在已有 session 上继续
runAgentLoop SHALL 支持在已有 session 的消息历史上继续执行 LLM tool loop，而非仅在新空 session 上启动。

#### Scenario: 在已有 session 上继续
- **WHEN** 调用 `runAgentLoop(existingSession, ...)` 且 session 已有 5 条历史消息
- **THEN** LLM 收到的 messages 包含这 5 条历史消息
- **AND** LLM 在此基础上继续生成和调用工具

#### Scenario: 新消息追加到已有 session
- **WHEN** runAgentLoop 在已有 session 上完成一个 step
- **THEN** 新产生的 assistant/tool messages 被追加到已有 session.messages 中

### Requirement: AgentRunner 支持 resume
AgentRunnerService SHALL 提供 `resume(issueId, message)` 方法，找到该 issue 的 paused session，注入用户消息，恢复 agent loop。

#### Scenario: resume 已暂停的 issue
- **WHEN** 调用 `agentRunner.resume(issueId, "User approved. Continue.")`
- **AND** 该 issue 有 paused session
- **THEN** 用户消息被追加到 session
- **AND** session 被恢复为 active
- **AND** agent loop 在该 session 上继续执行

#### Scenario: resume 不存在的 session
- **WHEN** 调用 `agentRunner.resume(issueId, message)`
- **AND** 该 issue 没有 paused session
- **THEN** 抛出错误 "No paused session found for issue"

#### Scenario: resume 时 agent 已在运行
- **WHEN** 调用 `agentRunner.resume(issueId, message)`
- **AND** agentRunner 已有 active issue 在运行
- **THEN** 抛出错误 "Agent already running"

#### Scenario: agent 完成后清理 paused session
- **WHEN** agent loop 在 resumed session 上正常完成（done）
- **THEN** session 被关闭
- **AND** paused session 映射被清除

#### Scenario: agent 出错后清理 paused session
- **WHEN** agent loop 在 resumed session 上抛出异常
- **THEN** session 被关闭
- **AND** paused session 映射被清除

### Requirement: Main Agent 支持 resume 模式
runMainAgent SHALL 接受可选的已有 session 参数。当提供 session 时，在该 session 上继续；否则创建新 session。

#### Scenario: resume 模式
- **WHEN** 调用 `runMainAgent(context, sessionManager, existingSession)`
- **AND** existingSession 有历史消息
- **THEN** 不创建新 session
- **AND** agent loop 在 existingSession 上继续

#### Scenario: start 模式
- **WHEN** 调用 `runMainAgent(context, sessionManager)` 不传 session
- **THEN** 创建新 session（与当前行为一致）

#### Scenario: gate 暂停时不关闭 session
- **WHEN** Main Agent 在 approval gate 停止（LLM finishReason = "stop"）
- **THEN** session 不被关闭
- **AND** session 被标记为 paused
- **AND** session 被存入 AgentRunnerService 的 paused map

### Requirement: mo approve CLI 命令
系统 SHALL 提供 `mo issue approve <number>` CLI 命令，调用 `POST /api/issues/:number/approve` 恢复暂停的 issue。

#### Scenario: 审批通过
- **WHEN** 用户执行 `mo issue approve 1`
- **AND** issue #1 在 approval gate 暂停
- **THEN** agent 恢复执行
- **AND** 输出 "Issue #1 approved, agent resumed"

#### Scenario: 审批失败（非 gate）
- **WHEN** 用户执行 `mo issue approve 1`
- **AND** issue #1 不在 approval gate
- **THEN** 输出错误信息

#### Scenario: 审批失败（agent 已在运行）
- **WHEN** 用户执行 `mo issue approve 1`
- **AND** 另一个 issue 的 agent 正在运行
- **THEN** 输出错误信息 "Another issue is already running"
