## MODIFIED Requirements

### Requirement: Server 可以作为后台进程启动 [UPDATED]

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。Server 启动时 SHALL 加载 `~/.mohist/config.jsonc` 配置文件。当通过 systemd 启动时，Server SHALL 支持 `--print-logs` 参数将日志输出到 stderr，且不写 PID 文件。Server SHALL 处理 SIGTERM 信号实现优雅退出。

#### Scenario: 启动 server (无 systemd)
- **WHEN** 用户执行 `mo server start`
- **AND** systemd 服务未安装
- **THEN** server 通过 spawn detached process 在 localhost:3456 监听 HTTP 请求
- **AND** server 初始化 Agent Runtime
- **AND** server 加载 `~/.mohist/config.jsonc` 配置（如果存在）
- **AND** server 写 PID 文件到 `~/.mohist/server.pid`
- **AND** server 在后台运行（daemon 模式）

#### Scenario: 启动 server (systemd 已安装)
- **WHEN** 用户执行 `mo server start`
- **AND** systemd 服务已安装（`~/.config/systemd/user/mohist.service` 存在）
- **THEN** CLI 执行 `systemctl --user start mohist.service`
- **AND** CLI 显示 "Server started (systemd)"

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

#### Scenario: Server 使用 --print-logs 启动
- **WHEN** server 启动时传入 `--print-logs` 参数
- **THEN** server 将日志输出到 stderr（供 journald 捕获）
- **AND** server 不写 PID 文件到 `~/.mohist/server.pid`
- **AND** 文件日志仍然正常写入 `~/.mohist/logs/`

#### Scenario: SIGTERM 优雅退出
- **WHEN** server 进程收到 SIGTERM 信号
- **THEN** server 停止接受新请求
- **AND** server 等待当前 agent 完成（最多 30 秒）
- **AND** server 以 exit code 143 退出（128 + SIGTERM=15）

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。当 systemd 服务已安装时，CLI SHALL 通过 systemctl 停止。

#### Scenario: 停止 server (无 systemd)
- **WHEN** 用户执行 `mo server stop`
- **AND** systemd 服务未安装
- **THEN** CLI 发送停止信号给 server（通过 PID 文件）
- **AND** Agent Runtime 优雅停止（等待当前 agent 完成，最多 30 秒）
- **AND** server 进程终止
- **AND** CLI 显示 "Server stopped"

#### Scenario: 停止 server (systemd 已安装)
- **WHEN** 用户执行 `mo server stop`
- **AND** systemd 服务已安装
- **THEN** CLI 执行 `systemctl --user stop mohist.service`
- **AND** CLI 显示 "Server stopped (systemd)"

#### Scenario: 查看 server 状态
- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数

#### Scenario: 查看 server 状态 (systemd 已安装)
- **WHEN** 用户执行 `mo server status`
- **AND** systemd 服务已安装
- **THEN** CLI 使用 `systemctl show mohist.service` 获取 PID 和状态
- **AND** 显示 systemd 服务状态（active/inactive/failed）
- **AND** 如果 active，显示 PID、端口、运行时间

#### Scenario: 查看 server 状态 (systemd 未安装)
- **WHEN** 用户执行 `mo server status`
- **AND** systemd 服务未安装
- **THEN** CLI 从 PID 文件 `~/.mohist/server.pid` 读取 PID
- **AND** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数
