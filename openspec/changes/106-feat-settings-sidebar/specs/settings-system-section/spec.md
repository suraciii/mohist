## ADDED Requirements

### Requirement: Log Level 可编辑

System section SHALL 提供 Log Level 下拉选择器，允许用户修改日志级别。可选级别为：DEBUG、INFO、WARN、ERROR。

#### Scenario: 显示当前 log level
- **WHEN** 当前 log level 为 INFO
- **THEN** Log Level 下拉显示 "INFO"

#### Scenario: 修改 log level
- **WHEN** 用户从下拉中选择 "DEBUG"
- **THEN** 调用 API 更新 `config.log.level` 为 "DEBUG"
- **AND** 成功后下拉显示 "DEBUG"

#### Scenario: Log level 修改失败
- **WHEN** API 返回错误
- **THEN** 下拉恢复为修改前的值
- **AND** 显示错误提示

#### Scenario: Log level 未配置时显示默认值
- **WHEN** `config.log.level` 未配置
- **THEN** 下拉显示默认值 "INFO"

### Requirement: Log 路径只读显示

System section SHALL 在 Log Level 下方显示日志文件路径，只读。

#### Scenario: 显示 log 路径
- **WHEN** System section 加载
- **THEN** 显示日志路径 `~/.mohist/logs/`
- **AND** 路径不可编辑

### Requirement: About 区域只读信息

System section SHALL 显示 About 区域，展示以下只读系统信息：Mohist 版本、Git hash、Server 地址和状态、Database 路径、Config 路径、Opencode 二进制路径。Server host/port 为只读展示，不提供编辑功能。

#### Scenario: 显示完整 About 信息
- **WHEN** System section 加载
- **AND** `GET /api/system/info` 返回成功
- **THEN** 显示以下信息：
  - Mohist v{version} · Git {hash}
  - Server {host}:{port} · {status}
  - Database {dbPath}
  - Config {configPath}
  - Opencode {opencodeBinPath}

#### Scenario: Server 运行中状态显示
- **WHEN** server 状态为 running
- **THEN** Server 行显示 "Running" 状态标签（绿色）

#### Scenario: Server 未运行时
- **WHEN** `GET /api/system/info` 请求失败（server 未运行）
- **THEN** Server 行显示 "Stopped" 状态标签（灰色）
- **AND** 其他本地路径信息仍可显示（使用默认值）

#### Scenario: 显示配置修改提示
- **WHEN** About 区域显示
- **THEN** 底部显示警告文本 "⚠ 修改服务器配置请编辑 config.jsonc 并重启"

#### Scenario: About 信息不可编辑
- **WHEN** 检查 About 区域 DOM
- **THEN** 所有字段为纯文本展示
- **AND** 不包含任何 input 或可编辑元素
