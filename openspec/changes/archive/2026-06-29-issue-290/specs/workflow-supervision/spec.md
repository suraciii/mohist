## MODIFIED Requirements

### Requirement: Work 完成超时检测由持久 reminder 驱动

RunnerGrain SHALL 注册一个 per-runner 的 Orleans reminder（如 `work-timeout`）驱动 work 完成超时检测，SHALL NOT 使用 grain timer。reminder 持久化进 `OrleansRemindersTable`，在 grain 去活或 silo 重启后 SHALL 重新激活 grain 来 fire，从而覆盖 server+runner 同步重启后 outstanding work 成为孤儿的场景（issue #275）。

该 reminder SHALL 以 register-if-absent 语义维护：仅在 outstanding work 从零变为非零时注册一次（先 `GetReminder` 确认不存在再注册），SHALL NOT 在 reminder 已存在时于后续 work 分配（`PollOneWorkflowAsync` / `AssignAgentJobAsync`）中再次 `RegisterOrUpdateReminder` 以重置 due-time。reminder SHALL 在仍有任何 pending 或 running work 时保持稳定的 runner 级周期；每条 work 的完成 deadline SHALL 继续由其自身的 `TakenAt` 派生，而非由最近一次 reminder 注册时刻派生。reminder SHALL 仅在一次扫描观察到无 pending 或 running work 时被注销/停止（drain 行为保留）。合成路径（`WorkResult(status="failed", reason="timeout")` 经 `ReportWorkflowResultAsync`）保持不变。

#### Scenario: 使用持久 reminder 而非 grain timer

- **WHEN** RunnerGrain 需要周期性扫描超时
- **THEN** SHALL 注册 Orleans reminder（持久化于 `OrleansRemindersTable`）
- **AND** SHALL NOT 使用 grain timer（grain timer 不跨去活）

#### Scenario: 跨重启检测孤儿 work

- **WHEN** server 与 runner 同步重启，某 outstanding work 在重启前未上报结果
- **THEN** reminder SHALL 在 silo 恢复后重新激活持有该 work 的 RunnerGrain
- **AND** 激活时 hydrate 的 outstanding 集 SHALL 包含该孤儿 work
- **AND** reminder tick SHALL 检测到该 work 超时并合成 `failed(reason=timeout)`

#### Scenario: 扫描零 DB 读

- **WHEN** reminder tick 触发
- **THEN** SHALL 只遍历内存 active 集判断超时
- **AND** SHALL NOT 在 tick 内执行 DB 读

#### Scenario: Reentrant 下扫描与合成的一致性

- **WHEN** RunnerGrain 标记为 `[Reentrant]` 且扫描期间并发发生 report
- **THEN** 扫描 SHALL 对内存 active 集 key 做 snapshot
- **AND** 合成前 SHALL 再次确认条目仍在（report 的 update 是权威）
- **AND** 已被 report 移除的 work SHALL NOT 被合成

#### Scenario: reminder 仅在 outstanding work 出现时注册一次

- **WHEN** RunnerGrain 通过 `PollOneWorkflowAsync` 或 `AssignAgentJobAsync` 取用 work，且当前不存在 `work-timeout` reminder
- **THEN** SHALL 以 register-if-absent 语义注册该 reminder（先 `GetReminder` 确认不存在再注册）
- **AND** SHALL NOT 使用会重置 due-time 的 `RegisterOrUpdateReminder` 来"确保"reminder 存在

#### Scenario: 后续 work 分配不重置 reminder due-time

- **WHEN** 一个 work 被取用时 `work-timeout` reminder 已存在（仍有 outstanding work）
- **THEN** 取用路径 SHALL NOT 调用 `RegisterOrUpdateReminder`（或任何会重置 due-time 的操作）
- **AND** 该 reminder 的下一次 fire 时刻 SHALL NOT 因新分配而被推迟
- **AND** 测试 SHALL 断言 reminder 调度行为（register-once vs. re-register）而非仅直接调用 `CheckWorkTimeoutsAsync`

#### Scenario: 老 outstanding work 不被新分配推迟超时判定

- **WHEN** runner 上已有一条 outstanding work W1 接近其 `TakenAt + WorkCompletionTimeout` deadline，且此时另一条新 work W2 被分配（在 W1 deadline 到达之前）
- **THEN** W1 的下一次超时扫描时刻 SHALL NOT 因 W2 的分配而被推迟
- **AND** 下一个稳定 scan tick SHALL 仍按 W1 自身的 `TakenAt` 判定
- **AND** 当 `now - W1.TakenAt > WorkCompletionTimeout` 时 SHALL 经 `ReportWorkflowResultAsync` 合成 `WorkResult(status="failed", reason="timeout")`

#### Scenario: reminder 仅在 outstanding work 清空时注销

- **WHEN** 一次 `work-timeout` 扫描观察到无 pending 或 running work（所有 work 已 report 或被合成终态）
- **THEN** SHALL 注销/停止该 reminder（保留现有 drain 行为）
- **AND** SHALL NOT 在仍有 outstanding work 时提前注销

#### Scenario: drain 后新 work 重新注册 reminder

- **WHEN** reminder 因 outstanding work 清空被注销后，又有新 work 被取用（outstanding 从零变为非零）
- **THEN** SHALL 重新注册 `work-timeout` reminder（register-if-absent）
- **AND** 该 reminder SHALL 从新注册时刻起按稳定周期 fire，覆盖新 work 的 deadline 判定
