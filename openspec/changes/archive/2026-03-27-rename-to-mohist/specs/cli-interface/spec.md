## MODIFIED Requirements

### Requirement: CLI 提供分组式命令界面

CLI SHALL 提供分组式命令界面，与 server 通信。

#### Scenario: 查看 help
- **WHEN** 用户执行 `mo --help`
- **THEN** 显示所有命令组和用法

#### Scenario: 查看子命令 help
- **WHEN** 用户执行 `mo issue --help`
- **THEN** 显示 issue 命令组的所有子命令

### Requirement: CLI 检测 server 状态

CLI SHALL 在执行命令前检测 server 是否运行。

#### Scenario: Server 未运行
- **WHEN** 用户执行需要 server 的命令
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running"
- **AND** CLI 提示 "Start with: mo server start"

### Requirement: CLI 支持 server 命令

CLI SHALL 支持 server 管理命令（无需 server 运行）。

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"
