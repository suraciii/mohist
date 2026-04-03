## ADDED Requirements

### Requirement: spawn_coder 启动 opencode acp oneshot session
系统 SHALL 提供 `spawn_coder` tool，通过 `opencode acp --cwd <worktree>` 启动 oneshot coding agent 子进程，接收 taskTemplate 和 variables，内部完成变量替换后发送给 opencode。

#### Scenario: 成功执行 plan 阶段任务
- **WHEN** Main Agent 调用 `spawn_coder({ taskTemplate: "分析 issue: {issue.title}", variables: { issue: { title: "用户登录" } } })`
- **THEN** 系统将 template 替换为 "分析 issue: 用户登录"，启动 `opencode acp --cwd <worktree>` 子进程，通过 stdio JSON-RPC 发送 `initialize` → `session/new` → `session/prompt(替换后的task)`，等待响应返回结果文本

#### Scenario: coding agent 超时
- **WHEN** coding agent 在配置的超时时间（默认 30 分钟）内未完成
- **THEN** 系统向 opencode acp 发送 `session/cancel`，kill 子进程，返回超时错误信息

#### Scenario: coding agent 进程启动失败
- **WHEN** `opencode acp` 命令不在 PATH 中或启动报错
- **THEN** 返回包含错误信息的失败结果，不崩溃

### Requirement: spawn_coder 通过 ACP 协议通信
系统 SHALL 使用 `@agentclientprotocol/sdk` 的 `Client` 类通过 stdio JSON-RPC 与 opencode acp 子进程通信。

#### Scenario: ACP 连接建立
- **WHEN** spawn_coder 启动 opencode acp 子进程
- **THEN** 通过 Client 的 `connect()` 方法建立 stdio JSON-RPC 连接

#### Scenario: 发送 prompt 并接收结果
- **WHEN** 通过 Client 发送 `session/prompt` 请求（已替换变量的 task message）
- **THEN** 等待响应完成，从响应中提取最终文本结果返回给 Main Agent

### Requirement: spawn_coder 每次执行后清理子进程
系统 SHALL 在每次 spawn_coder 执行完成后（无论成功或失败）kill opencode acp 子进程。

#### Scenario: 正常完成后清理
- **WHEN** coding agent 返回结果
- **THEN** 系统提取结果文本，kill 子进程，返回结果

#### Scenario: 异常后清理
- **WHEN** 发生超时、错误或任何异常
- **THEN** 系统 kill 子进程，确保不留僵尸进程

### Requirement: spawn_coder 支持环境变量隔离
系统 SHALL 在启动 opencode acp 子进程时清除 `OPENCODE_SERVER_PASSWORD` 和 `OPENCODE_SERVER_USERNAME` 环境变量，避免已知的 auth bug。

#### Scenario: 用户环境中有 OPENCODE_SERVER_PASSWORD
- **WHEN** 用户 shell 环境设置了 `OPENCODE_SERVER_PASSWORD`
- **THEN** spawn_coder 启动的子进程不继承该环境变量
