## ADDED Requirements

### Requirement: Server 可以作为后台进程启动

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。

#### Scenario: 启动 server
- **WHEN** 用户执行 `crawlph server start`
- **THEN** server 在 localhost:3456 监听 HTTP 请求
- **AND** server 返回健康检查响应
- **AND** server 在后台运行（daemon 模式）

#### Scenario: Server 重启后恢复状态
- **WHEN** server 重启
- **THEN** server 从 `~/.crawlph/projects.json` 加载项目列表
- **AND** server 从 GitHub Labels 恢复所有 Issue 的状态
- **AND** server 继续处理未完成的任务

### Requirement: Server 管理任务队列

Server SHALL 维护一个任务队列，控制并发执行的 agent 数量。

#### Scenario: 任务入队
- **WHEN** 用户启动一个新的 Issue 处理
- **THEN** Issue 被加入任务队列
- **AND** 如果当前并发数 < 8，立即开始执行

#### Scenario: 并发限制
- **WHEN** 已有 8 个 agent 在运行
- **AND** 新任务入队
- **THEN** 新任务保持等待状态
- **AND** 当有 agent 完成时，开始执行等待的任务

### Requirement: Server 提供健康检查接口

Server SHALL 提供健康检查接口，供 CLI 检测 server 是否运行。

#### Scenario: 健康检查
- **WHEN** CLI 请求 `GET /api/health`
- **THEN** server 返回 `{ "status": "ok" }`

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。

#### Scenario: 停止 server
- **WHEN** 用户执行 `crawlph server stop`
- **THEN** server 优雅关闭（等待当前任务完成）
- **AND** server 进程终止

#### Scenario: 查看 server 状态
- **WHEN** 用户执行 `crawlph server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间

### Requirement: Server 不自动启动

Server SHALL NOT 被 CLI 自动启动。

#### Scenario: Server 未运行时执行命令
- **WHEN** 用户执行需要 server 的命令（如 `crawlph issue list`）
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running. Start with: crawlph server start"
- **AND** CLI 不自动启动 server
