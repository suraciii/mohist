## MODIFIED Requirements

### Requirement: Server 可以作为后台进程启动

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** server 在 localhost:3456 监听 HTTP 请求
- **AND** server 返回健康检查响应
- **AND** server 在后台运行（daemon 模式）

#### Scenario: Server 重启后恢复状态
- **WHEN** server 重启
- **THEN** server 从 `~/.mohist/projects.json` 加载项目列表
- **AND** server 从 GitHub Labels 恢复所有 Issue 的状态
- **AND** server 继续处理未完成的任务

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** server 优雅关闭（等待当前任务完成）
- **AND** server 进程终止

#### Scenario: 查看 server 状态
- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间

### Requirement: Server 不自动启动

Server SHALL NOT 被 CLI 自动启动。

#### Scenario: Server 未运行时执行命令
- **WHEN** 用户执行需要 server 的命令（如 `mo issue list`）
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running. Start with: mo server start"
- **AND** CLI 不自动启动 server
