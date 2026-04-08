## Requirements

### Requirement: Server 可以作为后台进程启动 [UPDATED]

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。Server 启动时 SHALL 加载 `~/.mohist/config.jsonc` 配置文件。

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** server 在 localhost:3456 监听 HTTP 请求
- **AND** server 初始化 Agent Runtime
- **AND** server 加载 `~/.mohist/config.jsonc` 配置（如果存在）
- **AND** server 在后台运行（daemon 模式）

#### Scenario: Server 重启后恢复状态
- **WHEN** server 重启
- **THEN** server 从 SQLite 加载项目列表
- **AND** server 重新加载 `~/.mohist/config.jsonc` 配置
- **AND** Agent Runtime 就绪后等待新的 issue 启动请求

#### Scenario: 配置文件不存在
- **WHEN** `~/.mohist/config.jsonc` 不存在
- **THEN** server 使用默认配置（无 model 指定，依赖环境变量）
- **AND** server 正常启动

#### Scenario: 配置文件格式错误
- **WHEN** `~/.mohist/config.jsonc` 存在但格式错误
- **THEN** server 打印明确的解析错误
- **AND** server 以非零 exit code 退出

### Requirement: Server 管理 Issue pipeline

Server SHALL 管理 Issue pipeline 的调度和执行。

#### Scenario: Issue 启动
- **WHEN** 用户启动一个 Issue 处理
- **THEN** Issue stage 从 `draft` 变为 `plan`
- **AND** Agent Runtime 为该 Issue 创建 Main Agent session

#### Scenario: 并发限制
- **WHEN** 已有 maxConcurrentAgents 个 agent 运行
- **AND** 新 Issue 被启动
- **THEN** 新 Issue 保持 `plan` stage 但等待 agent 可用
- **AND** 当有 agent 完成时，下一个等待的 Issue 开始执行

### Requirement: Server 提供健康检查接口

Server SHALL 提供健康检查接口，供 CLI 检测 server 是否运行。

#### Scenario: 健康检查
- **WHEN** CLI 请求 `GET /api/health`
- **THEN** server 返回 `{ "status": "ok" }`

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** Agent Runtime 优雅停止（等待当前 agent 完成，最多 30 秒）
- **AND** server 进程终止

#### Scenario: 查看 server 状态
- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数

### Requirement: Server 不自动启动

Server SHALL NOT 被 CLI 自动启动。

#### Scenario: Server 未运行时执行命令
- **WHEN** 用户执行需要 server 的命令（如 `mo issue list`）
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running. Start with: mo server start"
- **AND** CLI 不自动启动 server

### Requirement: Server 管理 Issue pipeline

Server SHALL 管理 Issue pipeline 的调度和执行。

#### Scenario: Issue 启动
- **WHEN** 用户启动一个 Issue 处理
- **THEN** Issue stage 从 `draft` 变为 `plan`
- **AND** Agent Runtime 为该 Issue 创建 Main Agent session

#### Scenario: 并发限制
- **WHEN** 已有 maxConcurrentAgents 个 agent 运行
- **AND** 新 Issue 被启动
- **THEN** 新 Issue 保持 `plan` stage 但等待 agent 可用
- **AND** 当有 agent 完成时，下一个等待的 Issue 开始执行

#### Scenario: 关闭有运行中 agent 的 issue
- **WHEN** 用户请求关闭一个 issue
- **AND** 该 issue 有正在运行的 agent
- **THEN** server 返回 409 Conflict
- **AND** 错误信息包含 "agent is running" 及解决方案提示

#### Scenario: 关闭无运行中 agent 的 issue
- **WHEN** 用户请求关闭一个 issue
- **AND** 该 issue 没有运行中的 agent
- **THEN** server 将 issue status 设为 `blocked`
- **AND** 返回成功响应
