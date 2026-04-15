## ADDED Requirements

### Requirement: Log 模块提供结构化日志 API

系统 SHALL 提供 `Log` 命名空间模块（`src/util/log.ts`），支持 DEBUG、INFO、WARN、ERROR 四个日志级别，默认级别为 INFO。零外部依赖。

#### Scenario: 创建命名 logger
- **WHEN** 调用 `Log.create({ service: "agent-runner" })`
- **THEN** 返回一个 Logger 实例，所有输出自动附带 `service=agent-runner` 标签
- **AND** 相同 service 名称的调用返回缓存的同一实例

#### Scenario: 结构化日志输出
- **WHEN** 调用 `log.info("session created", { sessionId: "abc", issueNumber: 2 })`
- **THEN** 输出格式为 `INFO  <ISO-timestamp> +<ms> service=agent-runner sessionId=abc issueNumber=2 session created`

#### Scenario: 级别过滤
- **WHEN** 日志级别设为 INFO
- **AND** 调用 `log.debug("debug message")`
- **THEN** 该消息不被输出

#### Scenario: Error 对象格式化
- **WHEN** extra 中传入 Error 对象
- **THEN** 输出 Error.message，并递归展开 cause 链（最多 10 层），格式为 `<message> Caused by: <cause message>`

### Requirement: Log 支持文件输出和自动轮转

系统 SHALL 支持日志输出到文件，并自动管理日志文件数量。

#### Scenario: 初始化文件输出
- **WHEN** 调用 `Log.init({ print: false })`（默认模式）
- **THEN** 日志写入 `~/.mohist/logs/<ISO-timestamp>.log`
- **AND** WriteStream 以追加模式打开

#### Scenario: print 模式输出到 stderr
- **WHEN** 调用 `Log.init({ print: true })`
- **THEN** 日志写入 `process.stderr`，不创建文件

#### Scenario: dev 模式使用固定文件名
- **WHEN** 调用 `Log.init({ print: false, dev: true })`
- **THEN** 日志写入 `~/.mohist/logs/dev.log`
- **AND** 每次启动时清空文件

#### Scenario: 自动清理旧日志
- **WHEN** `Log.init()` 执行
- **AND** `~/.mohist/logs/` 中有超过 10 个时间戳命名的日志文件
- **THEN** 删除最旧的文件，保留最近 10 个

### Requirement: Log 支持 tag 链式调用和 clone

系统 SHALL 支持通过 tag 链式调用追加上下文信息。

#### Scenario: tag 追加上下文
- **WHEN** 调用 `log.tag("issueNumber", "2").tag("stage", "plan")`
- **THEN** 后续所有日志输出自动附带 `issueNumber=2 stage=plan`

#### Scenario: clone 创建独立上下文
- **WHEN** 调用 `const child = log.clone()`
- **AND** 对 `child` 调用 `child.tag("key", "value")`
- **THEN** 原始 `log` 实例不受影响

### Requirement: Log 支持 time 自动计时

系统 SHALL 提供 `time()` 方法记录操作耗时。

#### Scenario: 手动计时
- **WHEN** 调用 `const timer = log.time("agent execution", { issueNumber: 2 })`
- **THEN** 立即输出一条 INFO 日志：`status=started`
- **WHEN** 调用 `timer.stop()`
- **THEN** 输出一条 INFO 日志：`status=completed duration=<ms>`

#### Scenario: using 自动计时
- **WHEN** 使用 `using _ = log.time("tool execution")`
- **AND** 作用域结束
- **THEN** 自动输出 `status=completed duration=<ms>` 日志
