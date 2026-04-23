## ADDED Requirements

### Requirement: ACP session stream 关闭时序

ACP session SHALL 在子进程退出后立即销毁 stdin/stdout streams，阻止后续 write 尝试产生 EPIPE 错误。

#### Scenario: 子进程正常退出后 streams 被销毁
- **WHEN** opencode acp 子进程退出（exit code 0）
- **THEN** proc.on('exit') handler SHALL 立即调用 `proc.stdin.destroy()` 和 `proc.stdout.destroy()`
- **AND** 后续 cleanup() 的 abort() 调用 SHALL 不产生 EPIPE 错误

#### Scenario: 子进程异常退出后 streams 被销毁
- **WHEN** opencode acp 子进程被 SIGKILL 或异常退出
- **THEN** proc.on('exit') handler SHALL 立即调用 `proc.stdin.destroy()` 和 `proc.stdout.destroy()`
- **AND** 不产生 EPIPE 错误日志

#### Scenario: Stream destroy 错误被静默处理
- **WHEN** `proc.stdin.destroy()` 或 `proc.stdout.destroy()` 抛出异常
- **THEN** 异常 SHALL 被 catch 并静默忽略（stream 可能已关闭）

#### Scenario: ACP session 正常完成不受影响
- **WHEN** opencode acp 子进程正常完成并退出
- **THEN** session 结果 SHALL 正常返回
- **AND** 所有 stdout 数据 SHALL 已被读取（destroy 在数据读取后执行）
