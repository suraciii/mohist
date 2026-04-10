## MODIFIED Requirements

### Requirement: spawn_coder 启动 opencode acp oneshot session
系统 SHALL 提供 `spawn_coder` tool，通过统一的 `runAcpSession()` 函数启动 opencode acp oneshot coding agent 子进程，接收 taskTemplate 和 variables，内部完成变量替换后发送给 opencode。

#### Scenario: 成功执行 plan 阶段任务
- **WHEN** Main Agent 调用 `spawn_coder({ taskTemplate: "分析 issue: {issue.title}", variables: { issue: { title: "用户登录" } } })`
- **THEN** 系统将 template 替换为 "分析 issue: 用户登录"，调用 `runAcpSession({ cwd, task, timeout, issueId, projectId, executionId, workflowLogRepo, eventBus })`
- **AND** 返回结果文本给 Main Agent

#### Scenario: coding agent 超时
- **WHEN** coding agent 在配置的超时时间（默认 30 分钟）内未完成
- **THEN** runAcpSession 内部处理超时和清理，返回错误信息

#### Scenario: coding agent 进程启动失败
- **WHEN** `opencode acp` 命令不在 PATH 中或启动报错
- **THEN** runAcpSession 返回包含错误信息的失败结果，不崩溃

### Requirement: spawn_coder 捕获所有 ACP 事件

spawn_coder 工具 SHALL 通过 `runAcpSession` 捕获 opencode acp 子进程的所有 sessionUpdate 事件，持久化到 workflow_log 表。

#### Scenario: 完整事件捕获
- **WHEN** spawn_coder 执行一次 oneshot session
- **THEN** runAcpSession 将所有 sessionUpdate 事件记录到 workflow_log
- **AND** 返回给 Main Agent 的文本结果格式不变

#### Scenario: 事件关联 issue
- **WHEN** spawn_coder 捕获到一个 ACP 事件
- **THEN** workflow_log 记录包含对应的 issue_id 和 acpSessionId

### Requirement: spawn_coder 通过 EventBus 推送实时事件

spawn_coder 工具 SHALL 通过 `runAcpSession` 的 EventBus 推送实时 agent 行为事件，包括 agent_message_chunk 和 tool_call。

#### Scenario: 推送 agent 文本事件
- **WHEN** opencode acp 报告 agent_message_chunk 事件
- **AND** executionId 存在
- **THEN** EventBus emit `coder_text_chunk` 事件，payload 包含 executionId、acpSessionId 和文本 chunk

#### Scenario: 推送 tool_call 事件
- **WHEN** opencode acp 报告 tool_call 事件
- **AND** executionId 存在
- **THEN** EventBus emit `coder_tool_call` 事件，payload 包含 executionId、acpSessionId、toolName 和 state

### Requirement: spawn_coder 支持环境变量隔离
系统 SHALL 通过 runAcpSession 在启动 opencode acp 子进程时清除 `OPENCODE_SERVER_PASSWORD` 和 `OPENCODE_SERVER_USERNAME` 环境变量。

#### Scenario: 用户环境中有 OPENCODE_SERVER_PASSWORD
- **WHEN** 用户 shell 环境设置了 `OPENCODE_SERVER_PASSWORD`
- **THEN** spawn_coder 启动的子进程不继承该环境变量
