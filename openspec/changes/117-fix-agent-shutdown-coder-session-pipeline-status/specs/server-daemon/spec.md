## MODIFIED Requirements

### Requirement: Server 可以被显式停止

Server SHALL 支持用户显式停止。停止时 SHALL abort 所有 active agent 子进程，清理内存中的 agent 跟踪状态（activeAgents、pendingGates、waitingQuestions），然后终止 server 进程。

#### Scenario: 停止 server 有 active agents

- **WHEN** 用户执行 `mo server stop`
- **AND** `activeAgents` Map 中有正在运行的 agent
- **THEN** 系统对每个 active agent 调用 `abortController.abort()` 发送终止信号
- **THEN** 系统清空 `activeAgents`、`pendingGates`、`waitingQuestions`
- **AND** server 进程终止
- **AND** 所有 agent 子进程不再运行（不是孤儿进程）

#### Scenario: 停止 server 无 active agents

- **WHEN** 用户执行 `mo server stop`
- **AND** `activeAgents` Map 为空
- **THEN** 系统清空 `pendingGates`、`waitingQuestions`
- **AND** server 进程终止

#### Scenario: 查看 server 状态

- **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数
