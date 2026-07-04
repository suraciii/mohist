## Why

issue-327 把 Session 读侧拆成了 `AgentSessionQuerier` + `AgentActivityFeedAssembler` + `AgentUsageReporter` + `AgentSessionContextRefs` 几个类，但共享逻辑还以 `internal static` 形式挂在原 `AgentSessionQuerier` 上，被 sibling 类调用 25 次。结果「拆分」只减小了文件体积，没减小耦合——改一个 static 会静默影响三处，而且 `Labels` 这种小工具居然被原样重写了一遍（`AgentActivityFeedAssembler.cs:302` 与 `AgentSessionQuerier.Labels` 逐字节相同）没人发现。类名只描述了它三分之一的职责，其余是高 fan-in 垃圾抽屉。现在做是因为纯重排、不改对外契约、sibling 调用点明确，风险评级 low。

## What Changes

- **BREAKING（仅 internal surface）**：`AgentSessionQuerier` 的 13 个 `internal static` 成员全部移除（`BuildLineageDto`、`LoadEventSummariesAsync`、`LoadIssueTitlesAsync`、`IssueTitle`、`Label`、`IssueNumber`、`Annotation`、`ToUsageDto`、`BuildUsageHistoryDto`、`ToEventSummaryDto`、`Labels`、`ToProjection`、`ReconcileActiveSessionsAsync`）。对程序集外不可见，对外契约零变化。
- DTO 映射（`ToUsageDto`/`ToEventSummaryDto`/`BuildLineageDto`/`BuildUsageHistoryDto`/`ToProjection`）集中到独立的 DTO 映射器，三处消费者（querier 列表/详情/元数据路径、`AgentActivityFeedAssembler`、generic summary 路径）调同一映射方法。原本散在 prose 注释里的「字节对齐」不变量变成「调同一个映射器方法」。
- `Label`/`IssueNumber` 这两个对 `AgentSessionRecord` 的纯读取变成 record 的实例方法；`Annotation` 是 `AgentSessionMetadata.Annotation` 的纯转发，删除转发、调用方直接用 `session.Metadata.Annotation(key)`。
- 重复的 `Labels` 辅助消除——只留一处权威实现，`AgentActivityFeedAssembler` 删掉自己的副本，`AgentUsageReporter` 改调权威实现。
- transcript reductions（`LoadEventSummariesAsync`、`ReconcileActiveSessionsAsync`）搬到已有的 transcript 加载器区域（`TranscriptPartLoader` 附近）。
- issue 标题批查询（`LoadIssueTitlesAsync` + `IssueTitle` 回退）移到 Issue 读侧，被 Session 调用。
- 收敛后 `AgentSessionQuerier` 只剩它真正的查询方法。现有响应与行为不变。

## Capabilities

- `agent-session-dto-mapping`: Session DTO 投影（usage / event-summary / lineage / usage-history / transcript-event projection）集中到独立映射器；querier、activity feed、generic summary 三处消费者调同一方法，相同输入产出相同输出，跨消费者字节对齐。
- `agent-session-record-accessors`: 对 `AgentSessionRecord` 的纯标签读取（`Label` 回退到 session metadata、`IssueNumber` 解析）与 `Annotation` 转发收敛为 record/domain 的实例成员，解析语义（record 标签优先，回落 session metadata）保持不变。
- `transcript-reductions`: 基于 transcript 的归约（事件摘要批算 `LoadEventSummariesAsync`、活跃会话对账 `ReconcileActiveSessionsAsync`）从 querier 的 static 移到 transcript 加载区域，归约结果不变。
- `issue-title-batch-lookup`: issue 标题批查询 + `Issue #{n}` 回退解析器归属 Issue 读侧，被 Session 域按 (project, numbers) 调用，回退语义不变。
- `label-filter-builder`: label 过滤字典构造（跳过 null/whitespace key 与 value、ordinal 字典）收敛为单一权威实现，消除 querier / assembler / reporter 三处的重复。

## Impact

- **Server 实现**（`packages/server/src/Mohist.Server/Sessions/Services/`）：
  - `AgentSessionQuerier.cs`——移除全部 `internal static` 成员，保留查询方法；私有 DTO 映射改为调新映射器。
  - 新增 DTO 映射器类型（承载 usage / event-summary / lineage / usage-history / transcript-event 投影）。
  - `AgentSessionQuery.cs` 中 `AgentSessionRecord`——新增 `Label`（带 metadata 回退）/`IssueNumber` 实例方法。
  - `AgentActivityFeedAssembler.cs`——改调映射器与 record 访问器；删除本地 `Labels` 副本。
  - `AgentUsageReporter.cs`——`Labels` 改调权威实现。
  - `AgentSessionContextRefs.cs`——`Label` 改用 record 实例方法。
  - `TranscriptPartLoader.cs`（或同区域新类型）——承接 `LoadEventSummariesAsync` / `ReconcileActiveSessionsAsync`。
  - Issue 读侧（`Issue/Services/`）——承接 issue 标题批查询 + 回退解析器。
- **Server 测试**（`packages/server/tests/Mohist.Server.Tests/Specs/Sessions/`）：现有直接调 `AgentSessionQuerier.BuildLineageDto` / `BuildUsageHistoryDto` / `ToUsageDto` 的 spec（`AgentSessionRecoveryDomainSpecs.cs` 等）改为调映射器；新增断言三处消费者对相同输入产出一致 DTO；行为不变用现有集成 spec 兜底。
- **无 API/契约/存储/Runner/Web/CLI 变化**：所有 HTTP 响应字节不变；`AgentSessionQuerier` 仍是 `IScopedService`，路由注入不变。
- **无迁移、无配置变化**。
