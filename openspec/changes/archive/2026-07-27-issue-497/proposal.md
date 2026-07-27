## Why

AgentSession recovery（reset / compact）允许调用方省略显式幂等键。当前缺省实现把所有缺省调用落到同一个常量 `"legacy"`，导致同一命令种类下两次不同的 recovery 操作共享同一个幂等键——后一次操作可能被误判为前一次的重复投递，被吞掉或错误命中前者的结果。`"legacy"` 的命名还暗示版本兼容兜底，与「本项目无需考虑版本兼容」的政策不符。

## What Changes

- 移除 `AgentSessionGrain` 中的 `LegacyRecoveryIdempotencyKey = "legacy"` 常量；缺省幂等键不再退化为固定值。
- 缺省调用时由 grain 每次生成一个唯一值，使任意两次不同的 recovery 操作永不共享幂等键。
- 显式提供幂等键时的语义不变：同键重试仍幂等命中、附加到同一 reservation、重放同一结果。
- 缺省调用不再幂等：不提供键的重试每次视为新操作，由调用方自行承担重复执行风险（这是有意的安全侧选择，优于误吞不同操作）。
- 把「缺省生成唯一值」的决策写入 `design/agent-execution.md` 的 reset/compact 命令幂等键约定。
- 更新现有 Sessions recovery spec 与 unit 测试覆盖缺省唯一键路径，并补一条回归：不同缺省调用不共享幂等键。

## Capabilities

- `agent-session-recovery-idempotency`: AgentSession 的 reset / compact recovery 命令在调用方省略显式幂等键时，每次生成唯一键，保证不同操作互不误判；显式键的幂等命中语义保持不变。

## Impact

- **Server Session grain:** `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs` 的 `RecoveryIdempotencyKey` 由「缺省返回常量」改为「缺省每次生成唯一值」，移除 `LegacyRecoveryIdempotencyKey` 常量；`BeginSessionCommandAsync`、`GetCompletedRecoveryAsync` 受影响。
- **API 入口:** `packages/server/src/Mohist.Server/Api/AgentSessionRecoveryRoutes.cs` 与 `IssueRoutes.Sessions.cs` 经由 `Idempotency-Key` 头注入键，逻辑不变；缺省时改由 grain 兜底。
- **设计文档:** `design/agent-execution.md` 记录缺省唯一键决策。
- **测试:** `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionRecoveryGrainSpecs.cs` 与 `AgentSessionRecoveryApiSpecs.cs` 增补缺省唯一键的回归覆盖。
- **风险:** low——单一子系统，现有测试覆盖；不改变显式键的幂等命中语义，无外部 API 形状变化、无持久化迁移、无 runner 行为变化。
