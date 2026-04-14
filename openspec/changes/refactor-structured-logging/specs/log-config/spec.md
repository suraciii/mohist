## ADDED Requirements

### Requirement: 日志级别配置

系统 SHALL 支持通过 `~/.mohist/config.jsonc` 配置日志级别。级别可选值为 DEBUG、INFO、WARN、ERROR，默认值为 INFO。

#### Scenario: 通过配置文件设置日志级别
- **WHEN** `config.jsonc` 中包含 `{ "log": { "level": "DEBUG" } }`
- **AND** server 启动
- **THEN** `Log.init()` 使用 DEBUG 级别初始化
- **AND** DEBUG 级别的日志被输出到日志文件

#### Scenario: 未配置日志级别
- **WHEN** `config.jsonc` 中不包含 `log` 块
- **AND** server 启动
- **THEN** `Log.init()` 使用默认级别 INFO 初始化

#### Scenario: 通过环境变量覆盖日志级别
- **WHEN** 环境变量 `LOG_LEVEL` 设置为 WARN
- **AND** server 启动
- **THEN** `Log.init()` 使用 WARN 级别初始化
- **AND** config.jsonc 中的配置被环境变量覆盖

### Requirement: 配置 Schema 支持 log 块

`ConfigInfoSchema` SHALL 包含 `log` 字段，`log` 字段 SHALL 包含可选的 `level` 字符串。

#### Scenario: 加载有效配置
- **WHEN** 调用 `loadConfig()` 读取包含 `log.level` 的配置文件
- **THEN** `getLogConfig()` 返回 `{ level: "DEBUG" }`

#### Scenario: 加载缺失 log 块的配置
- **WHEN** 调用 `loadConfig()` 读取不包含 `log` 块的配置文件
- **THEN** `getLogConfig()` 返回 `{ level: "INFO" }`
