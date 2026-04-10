## ADDED Requirements

### Requirement: 统一的 ACP session 管理

系统 SHALL 提供统一的 `runAcpSession()` 函数，管理 opencode acp 子进程的完整生命周期：spawn → initialize → newSession → prompt → cleanup。

函数签名：`runAcpSession(options: AcpSessionOptions): Promise<AcpSessionResult>`

AcpSessionOptions SHALL 包含：cwd、task、timeout（默认 30min）、issueId、projectId、executionId、workflowLogRepo、eventBus、throttleMs（可选，默认 100）。

AcpSessionResult SHALL 包含：text（agent 输出文本）、success（boolean）、error（可选）、acpSessionId（可选）。

#### Scenario: 成功执行 ACP session
- **WHEN** 调用 `runAcpSession({ cwd: "/project", task: "实现功能" })`
- **THEN** spawn `opencode acp` 子进程，通过 ACP 协议完成 initialize → newSession → prompt
- **AND** 返回 `{ text: "agent输出", success: true, acpSessionId: "sess_xxx" }`

#### Scenario: 超时后清理
- **WHEN** ACP session 在 timeout 时间内未完成
- **THEN** 发送 `session/cancel`，kill 子进程（SIGTERM + 5s 后 SIGKILL）
- **AND** cancel stream（readable.cancel + writable.abort）
- **AND** 返回 `{ text: "已累积的文本", success: false, error: "Timed out after Xs" }`

#### Scenario: 进程异常后清理
- **WHEN** opencode acp 子进程崩溃或连接异常
- **THEN** 清理 stream 和子进程
- **AND** 返回 `{ text: "已累积的文本", success: false, error: "错误信息" }`

### Requirement: ACP session 文本截断保护

`runAcpSession` SHALL 对累积的 agent 文本实施 2MB 截断保护。超过限制时保留前 1MB 和后 1MB，中间插入截断标记。

#### Scenario: 文本未超限
- **WHEN** agent 输出文本总长 1MB
- **THEN** 完整返回，不截断

#### Scenario: 文本超过 2MB
- **WHEN** agent 输出文本总长 3MB
- **THEN** 保留前 1MB 和后 1MB，中间插入 `...[truncated X characters]...`
- **AND** 后续 chunk 不再累积（设 truncated 标记）

### Requirement: ACP session 持久化 workflow_log

`runAcpSession` SHALL 在收到每个 sessionUpdate 事件时，如果传入了 workflowLogRepo，将事件记录到 workflow_log 表。

#### Scenario: 记录所有 ACP 事件
- **WHEN** `runAcpSession` 收到 sessionUpdate 事件（agent_message_chunk、tool_call 等）
- **AND** options.workflowLogRepo 存在
- **THEN** 将事件插入 workflow_log，包含 issueId、acpSessionId、eventType、data

#### Scenario: 无 workflowLogRepo 时不报错
- **WHEN** options.workflowLogRepo 未传入
- **THEN** 不尝试写入 workflow_log，其他功能正常

### Requirement: ACP session 环境变量隔离

`runAcpSession` SHALL 在启动子进程时清除 `OPENCODE_SERVER_PASSWORD` 和 `OPENCODE_SERVER_USERNAME` 环境变量。

#### Scenario: 父进程有敏感环境变量
- **WHEN** 父进程环境中有 `OPENCODE_SERVER_PASSWORD=xxx`
- **THEN** 子进程不继承该变量

### Requirement: ACP session 自动授权

`runAcpSession` SHALL 在 `requestPermission` 回调中自动选择 `allow_once` 或 `allow_always` 选项。

#### Scenario: 子进程请求权限
- **WHEN** opencode acp 子进程通过 ACP 协议请求权限
- **THEN** 自动选择 allow 选项（优先 allow_once）

### Requirement: spawn_coder 和 ralph-executor 使用统一的 runAcpSession

`spawn_coder` tool 和 `ralph-executor` 的 task 执行 SHALL 调用统一的 `runAcpSession()` 函数，不再各自实现 ACP session 管理。

#### Scenario: spawn_coder 调用 runAcpSession
- **WHEN** spawn_coder tool 被调用
- **THEN** 内部调用 `runAcpSession({ cwd, task, timeout, issueId, projectId, executionId, workflowLogRepo, eventBus })`
- **AND** 返回值映射为 spawn_coder 的原有输出格式

#### Scenario: ralph task 调用 runAcpSession
- **WHEN** ralph executor 执行一个 task
- **THEN** 内部调用 `runAcpSession({ cwd: worktreePath, task: fullPrompt, issueId, projectId, executionId, eventBus })`
- **AND** 返回值的 success 字段用于判断 task 是否成功

### Requirement: ACP session 事件节流

`runAcpSession` SHALL 支持可选的事件节流机制，通过 `throttleMs` 参数控制 `coder_text_chunk` 事件的推送频率。

#### Scenario: 默认节流 100ms
- **WHEN** 调用 `runAcpSession` 时不指定 throttleMs
- **THEN** `coder_text_chunk` 事件默认每 100ms 最多推送一次
- **AND** 中间累积的文本合并到一个事件中

#### Scenario: 禁用节流
- **WHEN** 调用 `runAcpSession({ throttleMs: 0 })`
- **THEN** `coder_text_chunk` 事件实时推送，无延迟

#### Scenario: 自定义节流间隔
- **WHEN** 调用 `runAcpSession({ throttleMs: 200 })`
- **THEN** `coder_text_chunk` 事件每 200ms 最多推送一次
