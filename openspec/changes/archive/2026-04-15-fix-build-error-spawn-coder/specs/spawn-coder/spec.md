## MODIFIED Requirements

### Requirement: spawn_coder 启动 opencode acp oneshot session
系统 SHALL 提供 `spawn_coder` tool 和 `executeCoderTask` 函数，通过 `runAcpSession` 启动 opencode acp coding agent 子进程，接收 taskTemplate 和 variables，内部完成变量替换后发送给 opencode。所有调用统一使用 `runAcpSession({ cwd, task, timeout, ...options })` 接口。

#### Scenario: executeCoderTask 成功执行
- **WHEN** 调用 `executeCoderTask(cwd, task, { timeout, issueId, projectId, workflowLogRepo, eventBus })`
- **THEN** 系统通过 `runAcpSession` 启动 ACP session，传入对应参数，等待结果并返回 `CoderTaskResult`

#### Scenario: 成功执行 plan 阶段任务
- **WHEN** Main Agent 调用 `spawn_coder({ taskTemplate: "分析 issue: {issue.title}", variables: { issue: { title: "用户登录" } } })`
- **THEN** 系统将 template 替换为 "分析 issue: 用户登录"，启动 `opencode acp` 子进程，通过 ACP 协议发送 prompt，等待响应返回结果文本

#### Scenario: coding agent 超时
- **WHEN** coding agent 在配置的超时时间（默认 30 分钟）内未完成
- **THEN** 系统向 opencode acp 发送 `session/cancel`，kill 子进程，返回超时错误信息

#### Scenario: coding agent 进程启动失败
- **WHEN** `opencode acp` 命令不在 PATH 中或启动报错
- **THEN** 返回包含错误信息的失败结果，不崩溃
