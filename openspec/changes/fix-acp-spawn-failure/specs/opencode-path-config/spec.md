## ADDED Requirements

### Requirement: opencode bin path 可配置
系统 SHALL 支持通过以下优先级配置 opencode 可执行文件路径（高优先级覆盖低优先级）：
1. 环境变量 `OPENCODE_BIN_PATH`
2. 配置文件 `config.jsonc` 中 `opencode.binPath` 字段
3. 自动探测：依次检查 `~/.opencode/bin/opencode`、系统 PATH 中的 `opencode`

#### Scenario: 通过环境变量指定路径
- **WHEN** 环境变量 `OPENCODE_BIN_PATH` 设置为 `/custom/path/opencode`
- **THEN** 系统 SHALL 使用该路径作为 opencode 可执行文件路径

#### Scenario: 通过配置文件指定路径
- **WHEN** `config.jsonc` 包含 `{"opencode": {"binPath": "/opt/opencode/bin/opencode"}}` 且未设置环境变量
- **THEN** 系统 SHALL 使用配置文件中的路径

#### Scenario: 自动探测路径
- **WHEN** 未设置环境变量且配置文件中没有 `opencode.binPath`
- **THEN** 系统 SHALL 按顺序尝试 `~/.opencode/bin/opencode` 和系统 PATH 中的 `opencode`，使用第一个存在的路径

#### Scenario: 所有路径都不存在
- **WHEN** 环境变量未设置、配置文件无配置、且自动探测均失败
- **THEN** 系统 SHALL 回退到 `opencode`（使用系统 PATH），让 spawn 抛出原始错误

### Requirement: 路径传递到 ACP session
配置解析后的 opencode 路径 SHALL 通过 `AcpConnectionOptions` 接口传递到 `createAcpConnection()` 和 `runAcpSession()`，并在 `spawn()` 调用中使用。

#### Scenario: 使用配置路径 spawn
- **WHEN** `AcpConnectionOptions` 中 `opencodeBinPath` 为 `/home/user/.opencode/bin/opencode`
- **THEN** `spawn()` 调用 SHALL 使用该绝对路径而非裸命令名 `opencode`

#### Scenario: 无配置路径时使用默认值
- **WHEN** `AcpConnectionOptions` 中 `opencodeBinPath` 未设置或为空
- **THEN** `spawn()` 调用 SHALL 使用 `opencode` 作为命令名
