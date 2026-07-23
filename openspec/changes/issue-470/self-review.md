# Self-Review - Issue 470

已对照当前 issue 470 审查 `proposal.md`、`design.md`、`tasks.json` 和三个
capability spec，并核对现有 Server 启动、OTLP 摄取、后台工作与测试边界。

## Verdict

计划尚未达到可构建状态。范围覆盖完整，任务 DAG 与 spec anchors 有效，但以下契约仍会
迫使实现者在互相矛盾或未定义的行为之间自行选择。

## Findings

### F1 - fallback 的最近降级原因契约自相矛盾（高）

D6 规定每次 degradation 激活或刷新都取得更大的 sequence，`latest_degradation` 始终是
最近发生的降级事件（`design.md:136`）。D7 却断言 alternate host 生命周期内没有后续事件
能替换 `collector_bind_failed`（`design.md:150`），T-007 也要求该原因始终可见
（`tasks.json:165`）。

alternate 启动后仍会继续采样进程和存储；任何后续 `process_read_failed`、
`storage_read_failed` 或 protection 事件都会按 D6 获得更大的 sequence，并成为新的
`latest_degradation`。有序 seed 只能保证初始原因，不能保证生命周期内不被后续真实故障
替换。计划必须统一语义：要么只要求 alternate 初始快照显示 bind failure，并允许更新的
故障成为 latest；要么定义明确的原因优先级或单独暴露 collector failure。相应 spec 与
T-007 测试必须锁定同一规则。

### F2 - 摄取取消只能被误报为存储写失败（高）

D1 将写结果限定为 `not_attempted`、`committed`、`rolled_back`（`design.md:53`），并规定
accepted subset 一旦 rollback 就返回 503、激活 `storage_write`（`design.md:55`）。T-002
同时要求每个 batch 恰好发布一个 outcome（`tasks.json:35`）。当前摄取循环会在事务内检查
请求 cancellation token（`packages/server/src/Mohist.Server/Otel/TraceIngester.cs:94`），
因此客户端断开或请求取消同样会 rollback；按现有计划只能把它记录成
`storage_write_failed`，制造并不存在的存储故障，否则又违反每批一个 outcome。

计划必须为请求取消定义独立的 write result、响应传播、计数和 degradation 语义，并增加
事务开始后取消的确定性测试。取消不能清除既有写故障，也不能自行激活存储故障。

### F3 - detached work 的覆盖声明漏掉现有 fire-and-forget 路径（中）

D3 称当前只有两个 fire-and-forget 点，并让 T-005 只迁移
`SystemUpdateService` 与 `BackgroundHermesIssueNotificationDispatcher`
（`design.md:94`、`tasks.json:111-112`）。但 `EventDispatcherPoke.PokeAfterCommit` 也明确
启动不等待的 Orleans 调用和 continuation
（`packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherPoke.cs:16-39`），
且由三个 store 调用。能力 spec 的要求是 detached background execution 不保留请求 scope
（`specs/runtime-observability-metrics/spec.md:17-21`），不是只覆盖 `Task.Run`。

如果该路径依赖 Orleans incoming filter 保证调用时 scope 已清空，计划应明确记录该不变量，
并用测试证明 poke 及其 continuation 不继承或修改请求计数；否则应纳入统一 launcher/suppression
边界。当前两处迁移不足以证明 spec 的全局声明。

### F4 - bind failure classifier 的配置输入不明确（中）

D7 只说 classifier 接收“configured OTLP endpoint”（`design.md:144`）。仓库中同一配置段下
同时存在 collector listener 的 `BindHost`/`Port`
（`packages/server/src/Mohist.Server/Otel/OtelOptions.cs:34-42`）和 outbound exporter 的
`Endpoint`（`packages/server/src/Mohist.Server/Infrastructure/Config/OtelOptions.cs:41-46`）。
现有 fallback 实际按 listener port 分类（`packages/server/src/Mohist.Server/Program.cs:86`）。

实现者若把“endpoint”理解为 exporter URI，会在自定义 exporter 配置下错误分类启动异常。
设计与 T-007 应明确 classifier 接收 collector listener intent/address（至少 `BindHost` 与
`Port`），而不是 outbound exporter endpoint，并锁定两者配置不同时的测试。

## Coverage

除上述问题外，proposal、三个 capability spec 与 issue 的验收面一致；`tasks.json` 是有效
JSON，8 个任务的依赖无环，且覆盖三态状态、有界路由摘要、低基数 Meter、进程与存储压力、
telemetry outcomes、Agent 路径放大、转换日志、自观测隔离、固定状态成本和 core health 独立。

<promise>FAIL</promise>
