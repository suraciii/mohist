## ADDED Requirements

### Requirement: CLI 提供分组式命令界面

CLI SHALL 提供分组式命令界面，与 server 通信。

#### Scenario: 查看 help
- **WHEN** 用户执行 `crawlph --help`
- **THEN** 显示所有命令组和用法

#### Scenario: 查看子命令 help
- **WHEN** 用户执行 `crawlph issue --help`
- **THEN** 显示 issue 命令组的所有子命令

### Requirement: CLI 是 thin client

CLI SHALL NOT 包含业务逻辑，所有逻辑在 server 侧。

#### Scenario: CLI 调用 server API
- **WHEN** 用户执行 `crawlph issue list`
- **THEN** CLI 调用 `GET /api/issues`
- **AND** CLI 格式化输出 server 返回的数据
- **AND** CLI 不做任何业务决策

#### Scenario: CLI 不存储状态
- **WHEN** CLI 执行任何命令
- **THEN** CLI 不在本地存储任何业务状态
- **AND** 所有状态由 server 管理

### Requirement: CLI 检测 server 状态

CLI SHALL 在执行命令前检测 server 是否运行。

#### Scenario: Server 未运行
- **WHEN** 用户执行需要 server 的命令
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running"
- **AND** CLI 提示 "Start with: crawlph server start"

#### Scenario: Server 运行中
- **WHEN** 用户执行需要 server 的命令
- **AND** server 运行中
- **THEN** CLI 正常调用 server API

### Requirement: CLI 提供美化的输出

CLI SHALL 提供清晰、美化的终端输出。

#### Scenario: status 命令输出
- **WHEN** 用户执行 `crawlph status`
- **THEN** 显示格式化的状态表格
- **AND** 显示当前项目名称
- **AND** 使用颜色区分不同状态

#### Scenario: 错误信息友好
- **WHEN** 命令执行失败
- **THEN** 显示清晰的错误信息
- **AND** 提供可能的解决方案

### Requirement: CLI 支持 server 命令

CLI SHALL 支持 server 管理命令（无需 server 运行）。

#### Scenario: 启动 server
- **WHEN** 用户执行 `crawlph server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

#### Scenario: 停止 server
- **WHEN** 用户执行 `crawlph server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"
