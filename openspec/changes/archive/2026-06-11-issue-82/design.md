## Context

Mohist 的 SignalR 实时推送链路在 issue 82 中从端到端失效。Web 端打开了 `/hubs/events` 连接并监听 `OnEvent`，但后端没有任何事件能跨过 dispatcher 抵达前端。三个互不重叠的断点叠加形成完全静默的故障：

1. **前端不订阅**。`useEventsConnection` (`packages/web/src/shared/api/events-hub.ts:19-43`) 只负责建立 SignalR 连接，从未调用 `connection.invoke('SetSubscriptionsAsync', ...)`。`ConnectionSubscriptionRegistry` 中的订阅集永远为空，dispatcher 对每个连接都返回空集（`UserNotificationDispatcher.ResolveTargetConnectionsAsync` 走的是 `registry.ShouldNotify`，空集 → 不推送）。这是当前最致命的断点。

2. **后端 / 前端命名不匹配**。后端发射 CloudEvents 1.0.2 reverse-DNS 名（`com.mohist.workflow.stage.started` 等），由 `WorkflowEventSerializer.BusType` / `IssueEventSerializer.BusType` 集中映射。前端 `LiveTaskProvider` 的 switch 命中 legacy snake_case 名（`stage_changed`、`approval_requested`）。`EventCatalog.All` 列出 55 个 legacy 名但没人在 publish；reverse-DNS 名是唯一在用的产线命名。

3. **Agent session transcript 推送通道完全缺失**。`coder_text_chunk`、`ralph_task_update`、`agent_liveness_status` 等是观察数据（observation），不改变领域生命周期状态。`AgentSessionEventSerializer` 没有 `BusType`，`IEventPublisher` 不接收它们；runner 把它们 POST 到 `/api/runner/{runnerId}/sessions/.../events`，`AgentSessionGrain.AppendRuntimeEventsAsync` 把行写入 `AgentSessionRuntimeEvents` 表就停在那里。它们不属于 `IEventPublisher` 通路（设计原则 "Realtime events are observation only"），但又确实需要抵达前端。`AgentSessionEventSerializer` 缺失生命周期变体（Started/Completed/Failed/Cancelled/StatusChanged）的 `BusType`，因此 lifecycle 事件也从未通过 `IEventPublisher` 发射——这与"领域生命周期事件"应当走 CloudEventBus 的契约也不符。

附加可见症状：审批 toast 不弹、看板不自动刷新、live task timer 不动、rebase conflict 状态不更新、`ActivityPage` Waiting 列表永远空（`AgentRoutes.MapGet("/activity", ...)` 调用 `sessions.GetActivityAsync` 时没传 `waiting` 数组）。

约束：

- 不改前端 API 契约 / 页面布局。
- 不改 workflow 领域模型核心语义。
- 不把 transcript 观察事件塞进 CloudEventBus。
- 不清理 `EventCatalog` 中的 legacy 名称（保留 back-compat 文档）。
- 5 秒轮询（`agent/activity`、`agent/status`、`workflow/status`）保留作 fallback。

涉及 stakeholders：Web UI 实现者、Server `IEventPublisher` 实现者、`AgentSessionGrain` 维护者、Workflow 域负责人。

## Goals / Non-Goals

**Goals:**

- Web 端在 SignalR `start()` 之后以及 `onreconnected` 时都调用 `SetSubscriptionsAsync`，订阅 union(`EventName`, `AGENT_DETAIL_EVENTS`) 的全集，使 dispatcher 不再因空订阅集过滤掉所有 emit。
- 后端产线命名（reverse-DNS）与前端 switch 命中的命名（legacy snake_case）之间的鸿沟在 Web 端通过扩展 switch 来桥接，而不是改后端命名映射或清理 legacy 别名。
- `AgentSessionGrain.AppendRuntimeEventsAsync` 在状态机产生 lifecycle 转换时（first row → Started；`agent_session_terminal` → Completed/Failed/Cancelled；`agent_liveness_status` 状态变化 → StatusChanged）通过 `IEventPublisher` 发布对应 reverse-DNS 领域事件，并按 `IConnectionSubscriptionGrain`/`ConnectionSubscriptionRegistry` 已发出去过的事实做 dedup。
- 引入独立的非领域推送通道 `ITranscriptEventPublisher` + `OnTranscriptEvent` SignalR 方法，使 transcript 观察事件（`coder_text_chunk`、`coder_thought_chunk`、`coder_tool_call`、`ralph_task_update`、`ralph_loop_progress`、`agent_liveness_status`、`agent_usage_update`、`agent_session_model_resolved`）写入 `AgentSessionRuntimeEvents` 行后被实时转发给订阅了相应 transcript 类型的连接。
- `AgentRoutes.MapGet("/activity", ...)` 在调用 `AgentSessionQuerier.GetActivityAsync` 前构建 `waiting` 数组（`IssueStatus.InProgress` + workflow 当前 stage `AwaitingApproval`），传入现有 DTO 槽位，使 `ActivityPage` Waiting 区域不再为空。
- 5 秒轮询不被修改或移除，继续作为 SignalR 不可用 / 订阅集尚未协商完成时的安全网。

**Non-Goals:**

- 不清理 `EventCatalog` 中的 legacy snake_case 名称（保留为 back-compat 文档）。
- 不重命名 reverse-DNS 类型字符串、不动后端类型映射。
- 不实现事件历史查看 UI（这是另外的 issue；后端读 API 已存在）。
- 不在 transcript 通道中混入 `IEventPublisher` 调用的副作用——`OnTranscriptEvent` 与 `OnEvent` 物理分开。
- 不修改 workflow 域模型核心语义（`StageRun` / `TaskRun` / `StageCheck` 等不变）。
- 不引入 Orleans Streams；继续走 in-process `IEventBus` + SignalR。
- 不改前端 API 契约或页面布局。

## Decisions

### D1. Web 端 `SetSubscriptionsAsync` 在 `start()` 与 `onreconnected` 双触发

**决定**：`useEventsConnection` 在 `connection.start().then(() => connection.invoke('SetSubscriptionsAsync', EVENT_TYPES))` 注册，订阅失败时打 warning 但不破坏连接；同时在 `connection.onreconnected` 上也调一遍。

**理由**：`SetSubscriptionsAsync` 是 idempotent 的（`MohistHub.SetSubscriptionsAsync` 把整个 set 替换为新值），重复调用是 no-op。`onreconnected` 之后 dispatcher 还没收到 emit，订阅集必须已就位。`LiveTaskProvider` 维护一个 `EVENT_TYPES` 常量作为 single source of truth，导出给 `useEventsConnection` 引用，避免订阅集和 switch 漂移。

**考虑过的替代**：

- *替代 A：仅在 `start()` 触发*。问题：SignalR 重连后 dispatcher 拿到的可能是 reconnect 前的 connectionId（如果 server 没换 grain）或者空集；replay-on-reconnect 靠 `IConnectionSubscriptionGrain` 重建，但 client 显式 re-invoke 更直接可靠。
- *替代 B：把订阅集放到 sessionStorage，reconnect 时按需合并*。增加复杂度且没有收益——`SetSubscriptionsAsync` 本来就 idempotent。

### D2. 命名鸿沟在 Web 端通过扩展 switch 解决，而不是改后端

**决定**：`LiveTaskProvider` 的 `EventMap` 与 `AgentDetailEventMap` union 上加入 reverse-DNS 名（如 `com.mohist.workflow.stage.started`、`com.mohist.workflow.stage.approval-requested` 等），每个 reverse-DNS 名都映射到与 legacy 名相同的下游行为（query 失效 + toast）。

**理由**：

- 后端 CloudEvents 类型映射（`IssueEventSerializer.BusType`、`WorkflowEventSerializer.BusType`）已经在用 reverse-DNS，移除/重命名会破坏可观测性工具和未来的后端消费者。
- `EventCatalog` 里的 legacy 名没有发射源——把后端改回 legacy 名等于把"已删掉的产线"复活，会污染 audit log。
- Web 端 switch 是当前唯一需要消费新事件类型的地方。让它兼容 reverse-DNS 是单边最小修改。
- 双名共存（同一逻辑事件的 legacy 与 reverse-DNS 都进 switch）保留对未来 unmigrated producer 的容忍，匹配"不清理 legacy 名"的非目标。

**考虑过的替代**：

- *替代 A：写一个 reverse-DNS → legacy 的 alias map，把所有 reverse-DNS 事件翻译回 legacy 再分发*。这增加了不必要的中转层；`unwrapEnvelope` 已经把 CloudEvent `type` 暴露出来，直接在 switch 命中即可。
- *替代 B：后端同时 publish 两份（legacy + reverse-DNS）*。双发对 dispatcher 是双倍噪音，违反"single source of truth"。

### D3. Lifecycle 事件走 CloudEventBus（带 BusType 映射），transcript 事件走独立通道

**决定**：

- `AgentSessionEventSerializer` 新增 `BusType` 方法，按 `EventCatalog.ReverseDns.AgentSession*` 常量返回 reverse-DNS 名。`AgentSessionStarted` / `Activated` / `Completed` / `Failed` / `Cancelled` / `StatusChanged` 各自有 BusType。
- `AgentSessionGrain.AppendRuntimeEventsAsync` 在以下时机调用 `IEventPublisher.PublishAsync`：
  - 第一次有 row 写入且 `!wasTerminal` 且没有已发的 Started 标记 → 发 `com.mohist.agent-session.started`。
  - 收到 `agent_session_terminal` 且 `status ∈ {completed, failed, cancelled}` 且没有已发的对应 terminal 标记 → 发对应 reverse-DNS 事件。
  - 收到 `agent_liveness_status` 且 `MarkActive` 实际改变了状态 → 发 `com.mohist.agent-session.status-changed`（deduped by current status string）。
- transcript 观察事件（`coder_text_chunk`、`coder_thought_chunk`、`coder_tool_call`、`ralph_task_update`、`ralph_loop_progress`、`agent_liveness_status`、`agent_usage_update`、`agent_session_model_resolved`）通过新的 `ITranscriptEventPublisher` 转发，**不**经过 `IEventPublisher`。

**理由**：

- Lifecycle 事件改变状态机（started / completed / failed / cancelled），属于"领域事件 = 改变领域状态的事实"，符合 CloudEventBus 契约。
- Transcript 事件是观察数据（observation），不改变 lifecycle，符合 "Realtime events are observation only" 设计原则。
- 复用 `ConnectionSubscriptionRegistry` 做 transcript 通道的过滤：connections 通过 `SetSubscriptionsAsync('coder_text_chunk', ...)` 显式订阅。`ITranscriptEventPublisher` 的内部实现走 `IHubContext<MohistHub, IEventsClient>.Clients.Client(connectionId).OnTranscriptEvent(...)` 走 SignalR 直推，与 `OnEvent` 物理分开。
- dedup：lifecycle 事件按 `(sessionId, transitionName)` 维度去重。如果 `AppendRuntimeEventsAsync` 被并发调用（Grain 单线程激活模型其实不会发生，但保留 dedup 是好习惯）也不重复发。已发状态在 `_session` 状态机的现有字段里追踪（如 `Status.Phase`、`Status.LastDataAt`）——避免引入新的持久化字段。

**考虑过的替代**：

- *替代 A：所有 transcript + lifecycle 都过 IEventPublisher*。spec 明确反对："Transcript events SHALL NOT be published through IEventPublisher"。
- *替代 B：用 Orleans Streams 而非 in-process 通道*。与现有架构决策矛盾；in-process `IHubContext.Clients` 已经是项目内 SignalR fan-out 的成熟模式。
- *替代 C：为 transcript 新建一个独立的 SignalR hub*。增加部署复杂度；相同的 `ConnectionSubscriptionRegistry` 还可以共用，物理复用 `MohistHub` 即可，方法名 `OnTranscriptEvent` 与 `OnEvent` 是不同 SignalR method 互不冲突。

### D4. `ITranscriptEventPublisher` 复用 `ConnectionSubscriptionRegistry`，独立于 `IUserNotificationDispatcher`

**决定**：新建 `ITranscriptEventPublisher` 单方法接口（`PublishAsync(TranscriptEnvelope)`），实现类 `SignalRTranscriptEventPublisher` 直接持有 `ConnectionSubscriptionRegistry` + `IHubContext<MohistHub, IEventsClient>`，在 DI 中 `AddSingleton<ITranscriptEventPublisher, SignalRTranscriptEventPublisher>()`。

**理由**：

- `ConnectionSubscriptionRegistry` 是 process-local 的 hot-path mirror，本来就给 dispatcher 用——`ITranscriptEventPublisher` 复用它意味着"客户端订阅了某个 transcript 类型"这个事实本来就在 hot-path 上，过滤成本是 O(1) hash 查找。
- 保持 `OnTranscriptEvent` 与 `OnEvent` 两条独立通道：transcript 不进 `EventBridge`、不经过 `IEventPublisher`、不写到 `IEventStore`。`ITranscriptEventPublisher` 不是 `IEventPublisher` 的 wrapper——这是 spec 明确要求。
- 订阅集与 `SetSubscriptionsAsync` 共享同一 registry，Web 端不需要新增 `SetTranscriptSubscriptionsAsync`；只要 Web 在订阅集里放上 `coder_text_chunk` / `ralph_task_update` 等 transcript 类型，hot path 就放行。

**考虑过的替代**：

- *替代 A：transcript 事件经过 `IEventPublisher`，由 `EventBridge` 过滤分发*。spec 明确排除——会把 observation 数据混入 domain event 流。
- *替代 B：transcript 走 SignalR group*。SignalR group 是 process-local 的，跨 silo 不行；registry + grain pair 才是 Mohist 的可移植模式。

### D5. `ActivityPage` Waiting 列表在 `AgentRoutes` 端填充

**决定**：`AgentRoutes.MapGet("/activity", ...)` 在调用 `AgentSessionQuerier.GetActivityAsync` 前先调用 `IssueQuerier`（或新加的轻量查询方法）取所有 `IssueStatus.InProgress` 且 workflow 当前 stage `Status == "AwaitingApproval"` 的 issue，构建 `IReadOnlyList<ActivityWaitingCardDto>`，传进 `GetActivityAsync(... waiting: waiting)`。`ActivityWaitingCardDto` 已有（`AgentSessionReadModels.cs:181`），`ActivityDto.Waiting` 槽位已存在（`AgentSessionReadModels.cs:141`）。

**理由**：

- `ActivityPage` 已经消费 `waitingCards`（来自 `useActivityCards`），UI 不需要改。
- 复用现有 DTO 与槽位，不引入新 shape——spec 明确"DTO shape SHALL NOT change"。
- 重复：5 秒轮询已经在前端拉这套数据；后端现在同样把这份数据加进 `/activity` 的响应，可以让前端的轮询 fallback 与 SignalR 推送走同一条数据通路。

**考虑过的替代**：

- *替代 A：让前端 ActivityPage 自己查 approval-pending 列表*。spec 明确"AgentRoutes SHALL build the waiting array"。
- *替代 B：在 `AgentSessionQuerier.GetActivityAsync` 内部查 `IssueQuerier`*。querier 不知道项目级 approval 状态——这属于 issue/workflow 域。让 routes 层组装两个域的产物更符合"querier 是只读"原则。

### D6. 测试：spec 路径用 Specs/，领域规则用单元测试

**决定**：

- `Specs/Events/`：
  - `EventBridge` 在订阅了 legacy 别名或 reverse-DNS 名的 connection 上都能转发生命周期事件。
  - `ITranscriptEventPublisher` 在订阅了 transcript 类型的 connection 上转发行事件，未订阅的不转发。
  - `AgentSessionGrain` 在 lifecycle 转换时**至多**发一次对应 reverse-DNS 事件。
- `Specs/Web/`（unit test of `useEventsConnection`）：`start()` 后调 `invoke('SetSubscriptionsAsync', EVENT_TYPES)`，且 `onreconnected` 时再次调用。
- 单元测试：`AgentSessionEventSerializer.BusType` 对每个 lifecycle variant 返回正确 reverse-DNS 常量。

**理由**：与项目分层约定一致（见 AGENTS.md："Workflow 相关 spec 优先通过 WorkflowGrain、Runner、API 编排等产品路径验证"）。

**考虑过的替代**：

- *替代 A：只写 spec，不写单元测试*。`BusType` 是纯映射规则，单元测试给出"契约"比 spec 更直接。
- *替代 B：端到端跑 SignalR*。成本高、慢、不稳定，spec 路径足以覆盖订阅/转发的契约。

## Risks / Trade-offs

- [R1] 反向 DNS 名与 legacy snake_case 在 Web switch 双份维护 → 当后端**新增**一个 reverse-DNS 类型时，Web 必须同步扩展 switch。**Mitigation**：在 `useEventsConnection` 的 `EVENT_TYPES` 常量旁写一份 `// server emits both legacy and reverse-DNS` 注释；CI 跑 `Spec` 覆盖关键映射。

- [R2] `ITranscriptEventPublisher` 与 `IEventPublisher` 是两条物理通道，前端需要知道哪个走 `OnTranscriptEvent`、哪个走 `OnEvent`。**Mitigation**：SignalR `OnEvent` 已经被 `LiveTaskProvider` 处理（domain 事件），`OnTranscriptEvent` 是新方法；后端 dispatcher 只对 transcript 类型调用 `ITranscriptEventPublisher`，对 lifecycle 类型调用 `IEventPublisher`，命名空间在 spec 里固定下来。

- [R3] `AgentSessionGrain.AppendRuntimeEventsAsync` 当前是单 thread（Grain 激活模型），但 dedup 状态在 in-memory `_session` 上是 fragile 的（理论上 Grain deactivation → reactivation 后状态会重置）。**Mitigation**：当前 dedup 用的字段是 `_session.Status.Phase` 与 `_session.Status.LastDataAt`，已经持久化到 `AgentSessionRow` / `AgentSessionStore`；lifecycle dedup 通过持久化状态判断。deactivation 后 reactivation 时 `OnActivateAsync` 会 reload，已发状态不丢。

- [R4] transcript 事件高频（`coder_text_chunk` 可达数十次/秒），每个 event 走 `SignalR` 推送 + 单连接 hash 查找在低并发下没问题，N=100 个连接 × 100 evt/s = 10K evt/s，每个 O(N) 查找 = 1M hash/s，仍然可接受；如果 transcript 频率激增到 1K+ evt/s × 200 连接会成为瓶颈。**Mitigation**：现有 dispatcher 已经是 O(N) per emit，transcript 通道不引入新的 cost shape；如果未来成瓶颈，考虑 (a) SignalR batching，(b) per-connection throttling。先按当前规模实现。

- [R5] `SetSubscriptionsAsync` 接受任意字符串数组，前端传 `EVENT_TYPES` 常量有 70+ 项，每次 reconnect 重发是 trivial 开销。**Mitigation**：idempotent，SignalR invoke 是有 ack 的单次 RPC，开销可忽略。

- [R6] `ActivityRoutes` 端新增 `IssueQuerier` 依赖会增加 routes 层复杂度。**Mitigation**：`AgentRoutes` 已经在用 `IGrainFactory` 和 `WorkflowActivityQuerier`，多注入一个 `IssueQuerier` 不变架构。复用 `IssueQuerier` 现成的 `ListInProgressIssuesOnApprovalGateAsync` 风格的查询方法；不发明新 API。

- [R7] 扩展 `LiveTaskProvider` switch 增加 ~15 个新 arm 增大了函数体。**Mitigation**：可以拆成 `dispatchWorkflowStageEvent`、`dispatchIssueLifecycleEvent`、`dispatchAgentSessionLifecycleEvent` 三个小函数，每个包一个 switch；保留 `useLiveEvents.handleEvent` 总入口。当前规模（~200 行）尚可接受，refactor 留作 follow-up。

## Migration Plan

**部署**：

1. 先合入 server 端 `ITranscriptEventPublisher` + `OnTranscriptEvent` + `AgentSessionEventSerializer.BusType` + lifecycle 事件发布逻辑（不依赖前端）。
2. 合入 `AgentRoutes` 的 `waiting` 数组填充（不依赖前端）。
3. 合入 Web 端 `SetSubscriptionsAsync` 调用 + `LiveTaskProvider` switch 扩展。
4. CI 跑 spec + 单元测试。

每一步都向后兼容：

- Step 1：transcript 通道接入后，没有 connection 订阅 transcript 类型，所以 `OnTranscriptEvent` 不会被调用——对现有 Web 客户端无影响。
- Step 2：`ActivityDto.Waiting` 字段已存在，前端无变化；只是值从空变非空。
- Step 3：Web 端从"不订阅"变为"订阅全集"，reverse-DNS 名现在能命中 switch；legacy 名也被订阅，兼容老后端。

**回滚**：

- Web 端 `SetSubscriptionsAsync` 改动可独立 revert（前端 fallback 5 秒轮询一直在）。
- Server 端 lifecycle 事件发布可独立 revert（依然 transcript 行依然写入表）。
- `ITranscriptEventPublisher` 改动 revert 时需要同步去掉 `AgentSessionGrain.AppendRuntimeEventsAsync` 的新调用，保留旧行为。

**Feature flag**：本期不上 feature flag，因为是修复性 issue 且影响可观察（前端会立即获得实时事件）。如果生产环境出现回归，回滚按 commit 粒度即可。

## Open Questions

- **Q1**：`AgentSessionGrain.AppendRuntimeEventsAsync` 写完 row 后调用 `ITranscriptEventPublisher.PublishAsync` 是同步还是 fire-and-forget？目前计划同步 await（保证推送失败被记录），但 transcript 事件失败不应该阻塞 grain 主流程——是否需要 try/catch + log？这是实现细节，spec 没规定。

- **Q2**：Web 端 `EVENT_TYPES` 常量是否要分两个：`DOMAIN_EVENTS`（走 `OnEvent`）与 `TRANSCRIPT_EVENTS`（走 `OnTranscriptEvent`）？当前 spec 说一个 `SetSubscriptionsAsync` 即可，registry 共享；transcript 走 `OnTranscriptEvent` 还是要 Web 端 `On('OnTranscriptEvent', ...)` 注册处理器。实现时把 transcript 类型放到一个常量数组，并在 `LiveTaskProvider` 的 `useEffect` 中 `connection.on('OnTranscriptEvent', handler)` 即可。

- **Q3**：`ActivityPage` Waiting 列表的数据契约已经在 `ActivityDto.Waiting` 槽位上；UI 是消费 `useActivityCards().waitingCards`。需要确认 `useActivityCards` 是否会随 data 变化自动重渲染——这是 React Query 的标准行为，但需要 spec 测试覆盖。预期由现有 `useQuery(['agent-activity'])` 自动重渲染。

- **Q4**：reverse-DNS 类型（如 `com.mohist.workflow.stage.started`）的 payload shape 在 Web `EventMap` 中没定义（现有 `EventMap` 描述的是 legacy snake_case 的 payload 字段）。是否要为 reverse-DNS 类型新增 payload shape？当前 proposal 假设"server emits the same payload shape for both legacy and reverse-DNS types"——这是合理假设（type 字符串变，data 字段不变），但需要 spec 测试覆盖：后端 `CloudEventEnvelope.Data` 在两种 type 下结构相同。

- **Q5**：5 秒轮询的当前实现是不是 `useQuery({ refetchInterval: 5000 })`？如果 SignalR 在线 + 推送即时到达，轮询仍然会拉一遍。这在产品形态上是"可观察到的双倍网络"，需要前端在收到 `OnEvent` / `OnTranscriptEvent` 后短时间禁用轮询吗？proposal 明确说"polls remain as a fallback"，所以保持轮询不变——但 spec 测试要不要加"在收到 realtime 事件后 1 秒内不轮询"？当前选择不引入新逻辑。
