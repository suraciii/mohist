## MODIFIED Requirements

### Requirement: Server 可以作为后台进程启动 [UPDATED]

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。Server 启动时 SHALL 加载 `~/.mohist/config.jsonc` 配置文件。Server 启动时 SHALL 初始化 `Log` 模块，日志直接写入 `~/.mohist/logs/` 目录。Daemon 模式 SHALL 保留 stderr 重定向作为 `Log.init()` 之前的兜底错误捕获。

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** server 在 localhost:3456 监听 HTTP 请求
- **AND** server 初始化 Agent Runtime
- **AND** server 加载 `~/.mohist/config.jsonc` 配置（如果存在）
- **AND** server 在后台运行（daemon 模式）
- **AND** server 子进程通过 `Log.init()` 初始化日志，日志写入文件
- **AND** server 子进程 stdout 不再重定向到文件
- **AND** server 子进程 stderr 仍重定向到 `~/.mohist/logs/server.log` 作为兜底

#### Scenario: 查看 server 日志
- **WHEN** 用户执行 `mo server logs`
- **THEN** 读取 `~/.mohist/logs/` 中最新的时间戳日志文件并输出最后 N 行
- **AND** 支持 `--follow` 参数实时追踪最新日志文件

#### Scenario: 查看 server 状态
- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数、最新日志文件路径
