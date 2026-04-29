## MODIFIED Requirements

### Requirement: CLI 提供分组式命令界面

CLI SHALL 提供分组式命令界面，与 server 通信。`mo --version` SHALL 输出 `versionString` 格式（如 `0.1.0 (abc1234)`），而非裸版本号。

#### Scenario: 查看 help
- **WHEN** 用户执行 `mo --help`
- **THEN** 显示所有命令组和用法

#### Scenario: 查看子命令 help
- **WHEN** 用户执行 `mo issue --help`
- **THEN** 显示 issue 命令组的所有子命令

#### Scenario: 查看版本号含 git hash
- **WHEN** 用户执行 `mo --version`
- **THEN** 输出格式为 `{version} ({gitHash})`（如 `0.1.0 (abc1234)`）
- **AND** 版本号和 git hash 通过 `getVersionInfo()` 获取

### Requirement: Server status CLI omits task/worker info

The `mo server status` command SHALL NOT display `Workers`, `Running tasks`, or `Queued tasks` lines. The `mo server status` command SHALL display a `Version` line with the full version string. The `fetchServerStatus()` function SHALL NOT reference removed status fields.

#### Scenario: Server status display
- **WHEN** user executes `mo server status` and server is running
- **THEN** the output SHALL NOT include lines about workers, running tasks, or queued tasks
- **AND** the output SHALL include a `Version: {versionString}` line（如 `Version: 0.1.0 (abc1234)`）

#### Scenario: Server status display when server is not running
- **WHEN** user executes `mo server status` and server is not running
- **THEN** the output SHALL NOT include a Version line（无法从 server 获取版本）
