## ADDED Requirements

### Requirement: Server 注册全局 unhandledRejection 处理器

Server SHALL 在启动时注册 `process.on('unhandledRejection')` 处理器，捕获并记录未处理的 Promise rejection，不退出进程。

#### Scenario: Agent 后台 promise catch 块抛出异常
- **WHEN** agent 执行的 fire-and-forget promise 中 catch 块抛出异常
- **THEN** `unhandledRejection` 处理器记录错误信息（含 stack trace）
- **AND** server 进程继续运行

#### Scenario: handler 打印完整上下文
- **WHEN** `unhandledRejection` 触发
- **THEN** 日志包含 rejection reason 和完整 stack trace

### Requirement: Server 注册全局 uncaughtException 处理器

Server SHALL 在启动时注册 `process.on('uncaughtException')` 处理器，捕获并记录未捕获的同步异常，不退出进程。

#### Scenario: 同步代码抛出未捕获异常
- **WHEN** 同步代码路径抛出未被 try-catch 捕获的异常
- **THEN** `uncaughtException` 处理器记录错误信息（含 stack trace）
- **AND** server 进程继续运行

### Requirement: Agent catch 块显式处理状态更新异常

Agent 后台 promise 的 catch 块中调用 `stateManager.updateIssueStatus()` SHALL 被显式 try-catch 包裹。

#### Scenario: updateIssueStatus 在 agent catch 中抛出异常
- **WHEN** agent 执行失败进入 catch 块
- **AND** `stateManager.updateIssueStatus()` 调用抛出异常（如 SQLite 锁）
- **THEN** 异常被内层 try-catch 捕获并记录日志
- **AND** 不产生未处理的 Promise rejection
