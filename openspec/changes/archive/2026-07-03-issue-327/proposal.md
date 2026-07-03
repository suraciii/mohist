## Why

`AgentSessionQuerier`（`Sessions/Services/AgentSessionQuerier.cs`，1635 行）把 7 个正交的读侧关注点揉在一个类里：workflow/generic session 查询、followup/cancel 目标解析、activity feed 装配、usage/cost 报表、lineage 兜底、run-session 关联校对。维护时无法快速定位"改 activity feed 要看哪段"。文件里还沉淀了明确可删的死代码（零调用的 `ToAgentSessionDto` + 其返回类型 `AgentSessionDto`，全文仅此一处定义/构造）、5 份逐字复制的 transcript 加载样板（turns → turnIds → parts → sessionByTurnId 字典）、两个逐字符相同的上下文引用信封构造方法（仅返回类型不同），以及 transcript 事件名一半点号（`session.closed`）一半下划线（`session_closed`）导致读取侧被迫硬编码"接受两种拼写"的双判逻辑。现在收口是因为 issue #330 已把 Session 读侧物理归拢到 `Sessions/` 目录，目录前置依赖扫清，可以安全做内部职责拆分；且这些都是纯重构、零行为变更，风险低。

## What Changes

- 按读侧关注点拆分 `AgentSessionQuerier`：usage/cost 报表（`GetUsageTimeseriesAsync` / `GetCostRollupAsync` / `GetCostWindowedAsync` 及其 private 辅助）与 activity feed 装配（`GetActivityAsync` 及其 private 辅助）各自独立成服务或方法群，核心查询类回到只管查询。followup/cancel 目标解析属已知 smell 但不在本次 AC 范围，保持原位。
- 删除零调用的死代码 DTO 映射方法 `ToAgentSessionDto` 及其返回类型 `AgentSessionDto`（该 DTO 全仓仅被此方法构造，无任何路由/测试消费）。
- 收口重复样板：把文件内 5 处复制的 transcript 加载序列（`LoadLatestEventsAsync`、`LoadEventSummariesAsync`、`LoadTerminalFactsAsync`、`GetGenericSessionSummaryAsync`、`BuildSessionMetadataDtoAsync` 内联的 turns → turnIds → parts → sessionByTurnId 字典）提取为单一加载方法，原调用点共享。
- 合并两个逐字相同的上下文引用信封构造方法 `BuildAgentSessionListContextRefs` 与 `BuildGenericSessionSummaryContextRefs`（读同样的四个 label，全空返回 null，仅返回类型 `AgentSessionListContextRefsDto` / `GenericAgentSessionSummaryContextRefsDto` 不同）为一处共享构造。
- 统一 transcript 事件命名：移除下划线常量（`TranscriptPartTypes.SessionClosed = "session_closed"`），全链路只认点号格式 `session.closed`（与 `RuntimeEventTypes.SessionClosed` 及 runner 发射侧一致）；读取侧移除 `p.Type == "session_closed" || p.Type == "session.closed"` 形式的双判硬编码（`AgentSessionQuerier.ReadTerminalStateAsync`）。
- 纯重构，无 **BREAKING** 变更：不改 transcript 存储模型（turns/parts 表结构）、不改 lineage 兜底合成逻辑、不改 session label key 字符串值、不改任何对外 API 契约与 DTO 字段形状。

## Capabilities

- `session-querier-decomposition`: 读侧关注点分离——usage/cost 报表与 activity feed 装配各自独立于核心 session 查询服务；零调用的死 DTO 映射（`ToAgentSessionDto` / `AgentSessionDto`）被删除。
- `session-read-assembly-helpers`: 共享的读侧装配——transcript turns/parts 加载序列与上下文引用信封构造各自只在唯一一处定义并被全部调用点复用（收敛 5 份复制样板与 2 份相同信封构造），可观测行为不变。
- `session-transcript-event-naming`: transcript 事件类型词表单一——只保留点号分隔形式（`session.closed`），下划线常量移除，读取侧不再承载"接受两种拼写"的判断。

## Impact

- **Server 源码**（`packages/server/src/Mohist.Server/Sessions/`）：
  - `Services/AgentSessionQuerier.cs`（1635 行，主目标）：拆出 usage/cost 与 activity feed 职责、删 `ToAgentSessionDto`、收敛 transcript 加载与信封构造、移除事件名双判。
  - `AgentSessionReadModels.cs`：删除死 DTO `AgentSessionDto`。
  - `Services/TranscriptEventTypes.cs`：移除下划线常量 `TranscriptPartTypes.SessionClosed`，统一到点号词表（与 `RuntimeEventTypes` 对齐）。
  - `Services/TranscriptAccumulator.cs`：`ToTranscriptPartType` 的 `SessionClosed` 映射跟随词表统一（写入侧与读取侧一致）。
  - 读取侧消费方改用统一常量：`Services/SessionTranscriptBuilder.cs`、`Services/AgentSessionSummaryBuilder.cs`、`Services/TranscriptEventSummaryProjector.cs`（含其中以字面量 `"session_closed"` 比较的位点）。
  - 新增 usage/cost 与 activity feed 服务（放置与命名由 design 决定，预期落 `Sessions/Services/`）。
- **API 路由 / DI**（`packages/server/src/Mohist.Server/Api/`）：`AgentRoutes.cs`（activity/usage/cost）、`WorkflowSessionRoutes.cs`、`IssueRoutes.Sessions.cs`、`AgentSessionListRoutes.cs`、`AgentSessionFollowupRoutes.cs`、`AgentSessionCancelRoutes.cs`、`AgentSessionContextAssociationRoutes.cs`、`AgentRoutes.cs` 的 DI 注入与新服务边界同步更新。
- **Server 测试**（`packages/server/tests/`）：断言字面量 `"session_closed"` 的 spec（`AgentSessionSpecs.cs`、`AgentSessionReadApiSpecs.cs`、`GenericAgentSessionSummarySpecs.cs` 等）与 DI 注册检查 spec 同步更新；既有 Session 相关 spec（约 29 个）全绿无回归。
- **无 schema 迁移、无依赖增减、无对外 API 契约变更**。`TreatWarningsAsErrors` + 既有 spec 兜底拦截遗漏引用与行为回归。
- **风险**（medium，承自 issue）：拆分波及面广且事件名词表统一触及写入侧映射，但每一步均为零行为变更的机械重构；主要风险是漏改比较位点与新服务 DI 注册，由编译 + spec 兜底。
