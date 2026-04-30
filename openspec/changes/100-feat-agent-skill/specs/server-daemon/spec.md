## MODIFIED Requirements

### Requirement: Server 可以作为后台进程启动 [UPDATED]

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。Server 启动时 SHALL 加载 `~/.mohist/config.jsonc` 配置文件。Server 启动时 SHALL 初始化 SchedulerService 并恢复所有持久化的 skill 调度。

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

#### Scenario: Server 重启后恢复调度

- **WHEN** server 重启
- **THEN** server 初始化 SchedulerService
- **AND** SchedulerService 从 `agent_skill_schedules` 加载所有 enabled 调度
- **AND** 对 `next_run_at` 在过去的调度触发补跑执行
- **AND** 为所有 enabled 调度设置 timer

#### Scenario: 配置文件不存在

- **WHEN** `~/.mohist/config.jsonc` 不存在
- **THEN** server 使用默认配置（无 model 指定，依赖环境变量）
- **AND** server 正常启动

#### Scenario: 配置文件格式错误

- **WHEN** `~/.mohist/config.jsonc` 存在但格式错误
- **THEN** server 打印明确的解析错误
- **AND** server 以非零 exit code 退出

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。

#### Scenario: 停止 server

- **WHEN** 用户执行 `mo server stop`
- **THEN** SchedulerService 停止所有活跃 timer
- **AND** Agent Runtime 优雅停止（等待当前 agent 完成，最多 30 秒）
- **AND** server 进程终止

#### Scenario: 查看 server 状态

- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数
