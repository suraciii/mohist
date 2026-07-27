## Context

AgentSession recovery（`reset` / `compact`）命令通过 `AgentSessionGrain` 的幂等键识别重复投递：同键重试命中同一 reservation 并重放结果，不同键则加入或开启新操作。当前缺省实现（`AgentSessionGrain.cs:512-515`）把所有省略显式键的调用落到同一个常量 `"legacy"`：

```csharp
private const string LegacyRecoveryIdempotencyKey = "legacy";
private static string RecoveryIdempotencyKey(string? value) =>
    string.IsNullOrWhiteSpace(value) ? LegacyRecoveryIdempotencyKey : value;
```

该常量被 `BeginSessionCommandAsync`（生成 reservation 时写入 `IdempotencyKey`）和 `GetCompletedRecoveryAsync`（查询已完成结果时匹配）共用。后果：同一命令种类下两次**不同**的 recovery 操作共享一键——后一次可能被 `GetCompletedRecoveryAsync` 误判为前一次的重复，被吞掉或错误命中前者的 outcome。`"legacy"` 命名还暗示版本兼容兜底，与「本项目无需考虑版本兼容」政策不符。

约束：
- 幂等键是 grain 内部 reservation 上的字符串字段（`AgentSessionResetReservation.IdempotencyKey` / `AdditionalIdempotencyKeys`），无独立持久化 schema，无迁移。
- grain 被多个入口调用：`AgentSessionRecoveryRoutes`、`IssueRoutes.Sessions`（经 `Idempotency-Key` 头注入）、以及 grain 内部恢复路径。入口在缺省时统一传 `null`。
- 现有 spec（`AgentSessionRecoveryGrainSpecs`、`AgentSessionRecoveryApiSpecs`）已覆盖显式键的 replay / join 行为，是安全网。

## Goals / Non-Goals

**Goals:**
- 缺省调用每次获得唯一幂等键，使任意两次不同的 recovery 操作永不共享一键。
- 消除已完成 reservation 被后续缺省调用误重放的路径（issue 的核心 bug）。
- 保留显式键的全部幂等命中语义（同键重放、异键 join、completed 查询命中）。
- 在 `design/agent-execution.md` 记录「缺省生成唯一值」决策。

**Non-Goals:**
- 不强制调用方提供键（决策已定：缺省生成，而非强制）。
- 不改变 `reset` / `compact` 的触发、idle 门控、runtime-binding 替换、transcript 记录流程。
- 不引入持久化迁移、不改外部 API 形状、不改 runner 行为。
- 不改变 in-progress 同命令 reservation 的 join 语义（见 Risks）。

## Decisions

**D1：缺省键由 grain 每次生成唯一值，落在 `RecoveryIdempotencyKey` 内部。**
将 `RecoveryIdempotencyKey` 改为：`value` 非空白时原样返回；否则返回 `Guid.NewGuid().ToString("N")`。删除 `LegacyRecoveryIdempotencyKey` 常量。格式与现有 `OperationId`（`AgentSessionGrain.cs:337`）一致，便于排障。
- *为何在 grain 而非 API 层生成*：grain 是命令幂等的权威，被多入口调用；集中生成保证无论入口（HTTP 头缺省、Issue 路由、grain 内部）都一致。API 层在头缺省时已传 `null`，由 grain 兜底。
- *为何生成唯一值而非抛错*：决策已定（issue）。HTTP 头本就可选，强制提供会破坏现有调用方与 runner 内部路径；生成唯一值风险更低。

**D2：`MatchesRecoveryIdempotencyKey` 与 join 逻辑不变。**
唯一键只改「缺省值的来源」，匹配规则（`IdempotencyKey` 或 `AdditionalIdempotencyKeys` 命中）与 in-progress 异键 join（追加到 `AdditionalIdempotencyKeys`）保持原样。这样显式键的全部契约（同键重放、异键 join、completed 命中）零行为变化；缺省键仅在「已完成 reservation 的跨操作重放」这一条路径上不再误命中。

**D3：`GetCompletedRecoveryAsync` 的缺省查询天然返回 null。**
缺省调用生成的新 GUID 不会命中任何历史 reservation 的键 → 返回 `null` → 调用方落入 `BeginSessionCommandAsync` 开启新操作。无需特判。

**D4：决策落档到 `design/agent-execution.md`（operationId 幂等键段，约 line 234）。**
补一句：recovery 命令幂等键缺省时由 grain 每次生成唯一值；显式键的幂等命中语义不变；省略键的重试不再跨操作幂等。

**已考虑的备选：**
- *强制提供键（null 抛错）*：拒绝。破坏可选 HTTP 头契约与多入口调用方，收益不抵成本。
- *在 API 层为缺省请求生成键*：拒绝。多入口不一致，且 grain 已是权威。
- *给缺省键加 `auto-` 前缀*：拒绝。`OperationId` 用裸 GUID(`N`)，保持一致即可；前缀无额外语义价值。

## Risks / Trade-offs

- **[缺省键重试已完成操作会重新执行]** -> 有意为之。这是安全侧选择：误重新执行优于误吞不同操作。需要重试幂等的调用方必须显式提供键。在 design 文档与 spec 场景中明示。
- **[in-progress 同命令缺省重试仍 join 同一 reservation]** -> 保留。同一 session 同时只能有一次 binding 替换；join 满足重试意图，与显式异键 join 语义对称。仅「已完成 reservation 的跨操作误重放」被消除，那是真正的 bug 路径。
- **[部署瞬间存在残留 `IdempotencyKey="legacy"` 的 in-flight reservation]** -> 无影响。新代码对缺省调用生成新 GUID，不命中任何历史键；残留 reservation 自行完成收敛。无需迁移。
- **[GUID 生成使 `RecoveryIdempotencyKey` 不再是纯函数]** -> 可接受。每个方法体内只调用一次并绑定局部变量，无重复生成；测试不依赖其确定性。

## Migration Plan

- 改动局限：`AgentSessionGrain.cs`（替换 `RecoveryIdempotencyKey`、删常量）、`design/agent-execution.md`（补一句决策）、recovery spec/unit 测试（补缺省唯一键回归）。
- 无 DB schema、无事件形状、无 API 形状变化；直接随下次 server 构建部署。
- 回滚：还原 `RecoveryIdempotencyKey` 与常量即可；无数据回填。

## Open Questions

无。决策（缺省生成唯一值）已由 issue 钉死，实现路径单一。
