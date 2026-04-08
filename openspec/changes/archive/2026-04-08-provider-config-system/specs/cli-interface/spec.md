## MODIFIED Requirements

### Requirement: CLI 提供分组式命令界面

CLI SHALL 提供分组式命令界面，与 server 通信。CLI SHALL 新增 `providers` 命令组，包含 `list`、`login`、`logout` 子命令。

#### Scenario: 查看 help
- **WHEN** 用户执行 `mo --help`
- **THEN** 显示所有命令组（含 `providers`）和用法

#### Scenario: 查看子命令 help
- **WHEN** 用户执行 `mo issue --help`
- **THEN** 显示 issue 命令组的所有子命令

#### Scenario: 查看 providers help
- **WHEN** 用户执行 `mo providers --help`
- **THEN** 显示 providers 命令组描述和子命令（list, login, logout）

### Requirement: CLI 是 thin client

CLI SHALL NOT 包含业务逻辑，所有逻辑在 server 侧。**例外**: `mo providers` 命令组 SHALL 直接读写 `~/.mohist/config.jsonc`，不需要 server 运行。

#### Scenario: CLI 调用 server API
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 调用 `GET /api/issues`
- **AND** CLI 格式化输出 server 返回的数据
- **AND** CLI 不做任何业务决策

#### Scenario: CLI providers 命令不依赖 server
- **WHEN** 用户执行 `mo providers list`
- **THEN** CLI 直接读取 `~/.mohist/config.jsonc`
- **AND** 不调用 server API

#### Scenario: CLI 不存储状态
- **WHEN** CLI 执行任何命令
- **THEN** CLI 不在本地存储任何业务状态
- **AND** 所有状态由 server 管理

### Requirement: CLI 检测 server 状态

CLI SHALL 在执行命令前检测 server 是否运行。所有需要 server 的 CLI 命令 SHALL 在执行前检查 server 是否可用。**例外**: `mo providers` 命令组不需要此检测。server 不可用时 SHALL 打印友好错误信息并退出，而非抛出 ECONNREFUSED。

#### Scenario: Server 未运行
- **WHEN** 用户执行需要 server 的命令（非 providers 命令）
- **AND** server 未运行
- **THEN** CLI 输出 "Server is not running. Start with: mo server start" 并以非零 exit code 退出
- **AND** 不输出 Node.js 的 ECONNREFUSED 堆栈信息

#### Scenario: Server 运行中
- **WHEN** 用户执行需要 server 的命令
- **AND** server 运行中
- **THEN** CLI 正常调用 server API

#### Scenario: providers 命令不检测 server
- **WHEN** 用户执行 `mo providers list`
- **AND** server 未运行
- **THEN** CLI 正常执行 providers 命令
- **AND** 不显示 "Server is not running" 错误
