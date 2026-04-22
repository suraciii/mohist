## ADDED Requirements

### Requirement: spawn 失败必须传播错误
当 `spawn('opencode', ['acp'])` 失败时（包括但不限于 ENOENT、EACCES），错误 SHALL 传播到调用方，使得 `createAcpConnection()` 和 `runAcpSession()` 返回失败结果或抛出异常。

#### Scenario: opencode 二进制文件不存在
- **WHEN** spawn 调用因 ENOENT 错误失败
- **THEN** `createAcpConnection()` SHALL throw 一个包含原始错误信息的 Error
- **AND** `runAcpSession()` SHALL 返回 `{ success: false, error: "spawn error message" }`

#### Scenario: opencode 无执行权限
- **WHEN** spawn 调用因 EACCES 错误失败
- **THEN** 错误 SHALL 以相同方式传播，不允许 Promise 挂起

### Requirement: spawn 错误标记为不可重试
spawn 失败的错误信息 SHALL 包含 `[SPAWN_FAILED]` 前缀，使 RalphExecutor 的 failure categorization 将其识别为不可重试错误，避免无意义重试。

#### Scenario: spawn 失败触发 Ralph 重试
- **WHEN** `runAcpSession` 因 spawn 失败返回 `{ success: false, error: "[SPAWN_FAILED] opencode not found" }`
- **THEN** RalphExecutor SHALL 将其归类为不可重试错误
- **AND** 不尝试重试该任务

### Requirement: 进程启动后崩溃检测
如果 opencode 进程成功 spawn 但在 `connection.initialize()` 之前退出（exit code 非 0），系统 SHALL 将其视为启动失败并传播错误。

#### Scenario: opencode 进程立即崩溃
- **WHEN** opencode 进程在 initialize 之前以 exit code 1 退出
- **THEN** 系统 SHALL 抛出错误，错误信息包含 exit code
- **AND** 不允许 initialize 无限等待

#### Scenario: opencode 参数错误导致退出
- **WHEN** opencode 因无法识别的参数或依赖缺失在 initialize 前退出
- **THEN** 错误 SHALL 以相同方式传播

### Requirement: spawn 失败后清理资源
spawn 失败或进程启动后崩溃时，系统 SHALL 清理已创建的资源（管道流、进程引用），确保不泄漏文件描述符。

#### Scenario: spawn 失败后的资源清理
- **WHEN** spawn 因错误失败
- **THEN** 系统 SHALL 调用 cleanup 关闭输入/输出流
- **AND** 标记进程已退出
- **AND** 不留下挂起的 pipe 文件描述符

### Requirement: pipeline 捕获 spawn 错误并回滚 issue
当 ACP session 因 spawn 错误失败时，pipeline SHALL 将 issue 状态回滚到 draft 并标记为 blocked，不允许 issue 进入僵尸状态（active 但无进程运行）。

#### Scenario: plan 阶段 spawn 失败
- **WHEN** issue 处于 plan 阶段且 ACP session spawn 失败
- **THEN** issue 状态 SHALL 变为 blocked
- **AND** issue stage SHALL 回滚到 draft
- **AND** `activeAgents` Map SHALL 移除该 issue 条目
- **AND** 事件 `agent_error` SHALL 被触发

#### Scenario: build 阶段 spawn 失败
- **WHEN** issue 处于 build 阶段且 ACP session spawn 失败
- **THEN** 与 plan 阶段相同的回滚行为 SHALL 生效

### Requirement: catch 块异常隔离
agent runner 的 catch 块中，每个可能抛异常的操作 SHALL 独立包裹 try/catch，确保部分失败不影响其他清理操作。

#### Scenario: DB 状态更新失败
- **WHEN** spawn 失败且 `updateIssueStatus` 抛异常
- **THEN** `updateStage` 和 `clearApprovalState` SHALL 仍尝试执行
- **AND** `eventBus.emit('agent_error')` SHALL 仍尝试执行
- **AND** `activeAgents.delete` SHALL 在 finally 中保证执行

#### Scenario: 事件发射失败
- **WHEN** spawn 失败且 `eventBus.emit` 抛异常
- **THEN** `activeAgents.delete` SHALL 仍执行
- **AND** issue 状态回滚 SHALL 已在前面的步骤中完成
