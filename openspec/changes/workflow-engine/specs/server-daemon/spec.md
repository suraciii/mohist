## MODIFIED Requirements

### Requirement: Server 可以作为后台进程启动

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。

#### Scenario: 启动 server

- **WHEN** 用户执行 `mo server start`
- **THEN** server 在 localhost:3456 监听 HTTP 请求
- **AND** server 启动 WorkflowEngine
- **AND** WorkflowEngine 开始轮询并执行任务
- **AND** server 在后台运行（daemon 模式）

#### Scenario: Server 重启后恢复状态

- **WHEN** server 重启
- **THEN** server 从 TaskRepo 加载项目列表
- **AND** server 将所有 running 的 Task 标记为 failed
- **AND** WorkflowEngine 启动后处理 pending 的 Task

### Requirement: Server 管理任务队列

Server SHALL 通过 WorkflowEngine 管理任务的调度和执行。

#### Scenario: 任务入队

- **WHEN** 用户启动一个新的 Issue 处理
- **THEN** Task 通过 TaskRepo 持久化创建
- **AND** WorkflowEngine 的 Worker 自动拾取并执行

#### Scenario: 并发限制

- **WHEN** 已有 8 个 agent 运行
- **AND** 新 Task 被创建
- **THEN** 新 Task 保持 pending 状态
- **AND** 当有 Worker 完成当前任务时，拾取下一个 pending 的 Task

### Requirement: Server 提供健康检查接口

Server SHALL 提供健康检查接口，供 CLI 检测 server 是否运行。

#### Scenario: 健康检查

- **WHEN** CLI 请求 `GET /api/health`
- **THEN** server 返回 `{ "status": "ok" }`

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。

#### Scenario: 停止 server

- **WHEN** 用户执行 `mo server stop`
- **THEN** WorkflowEngine 优雅停止（等待当前任务完成，最多 30 秒）
- **AND** server 进程终止

#### Scenario: 查看 server 状态

- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Worker 数、队列中的 Task 数

### Requirement: Server 不自动启动

Server SHALL NOT 被 CLI 自动启动。

#### Scenario: Server 未运行时执行命令

- **WHEN** 用户执行需要 server 的命令（如 `mo issue list`）
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running. Start with: mo server start"
- **AND** CLI 不自动启动 server
