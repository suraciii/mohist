## Context

本变更为 Mohist Agent 落地三个既定但尚未实装的能力：Readiness（执行定义是否足以执行）、Availability（现在能否开始）、MaxConcurrentRuns（实时并发调度闸门）。当前状态：

- **MaxConcurrentRuns 只存不用**：字段已贯穿 `Agent` 定义、CRUD、CLI（`--max-concurrent-runs`）与读模型，但全仓库无任何调度逻辑读取它（`AgentJobGrain.TryDispatchCoreAsync` 只做 per-Runner slot 检查）。
- **缺 Runner/容量即终态失败**：AgentJob 在 dispatch 退避（1s→60s ×2）超过 `DispatchRetryBound`（10 min）后以终态 `Failed("runner-unavailable")` 结束。对比之下 WorkflowRun 在 `Pending`/`Ready` 无限等待、从不在缺 Runner 时失败。
- **Readiness 不存在**：`AgentConfigSchema.Validate` 只校验形状（合法 key、runtime 枚举）；空 Instructions、null 配置、空 Skills 全部通过；配置缺口只能在启动失败后才看到。唯一阻止启动的既存闸门是 `Status == Archived`（在三个 launch 入口各自重复检查）。`AgentInfo` 只回显原始 `AgentConfig`，无任何派生结论。
- **Availability 只是聚合看板**：`RunnerStatusService` 给出 `RunnerAvailable = runners.Count > 0` 与 used/total slot，未被任何入口当作启动前闸门；Web 的「Runner at capacity」文案在客户端合成。
- **follow-up 完全绕过 AgentJob**：`AgentSessionFollowupRoutes.ExecuteFollowupAsync` → `AgentSessionGrain.BeginFollowupAsync`（每会话至多一条未确认 lease）→ 直接 SignalR `ReceiveFollowup` 投递给 Runner。follow-up 不创建 AgentJob、不创建 `AgentTurnRecord`；`AgentSessionGrain` 不实现 `IRemindable`、无任何 reminder/timer，因此没有「唤醒等待中 follow-up」的机制。
- **Server 看不到 Runtime 凭据**：coder-model 凭据只存在于 Runner 进程环境（opencode CLI / Pi SDK），Server 无任何凭据存储；唯一的服务端模型目录是 `RunnerRegistryGrain` 的**内存**聚合，无 Runner 在线时为空。`design/runtimes/opencode.md` 明确：目录只是配置辅助，不是执行合法性的最终权威，模型发现状态不参与 Runtime readiness。

**约束**：聚合是强一致与事务边界（一事务一聚合 + 其事件）；Server 是状态裁判，Runner 只报告事实；同一 AgentSession 同时最多一次 Runtime 执行（per-session 串行）；已接受的 SessionInput 不得因容量/队列丢失。

## Goals / Non-Goals

**Goals:**
- Readiness 产出 Server 权威的 `Ready` / `Needs setup` / `Unknown` 三态结论，独立于 Runner/容量/并发，并在提交工作前可见；`Needs setup` 阻止新 launch。
- Availability 作为独立于 Readiness 的结论，区分「现在能开始」与「需等待」并给出等待原因（无 Runner / 容量满 / 并发限制）。
- `MaxConcurrentRuns` 成为对所有调用入口（含 launch 与 follow-up）一致的实时调度闸门：达限等待而非失败；调低不停 active 工作、不改写已有 Session；调高让等待工作自动推进。
- 缺 Runner/容量/并发时工作进入可见等待，取代 `runner-unavailable` 终态失败（**BREAKING**）。

**Non-Goals:**
- 定义或改变 Agent 执行定义的内容与快照规则（MaxConcurrentRuns 是调度策略，不进快照）。
- 新建 Server 端 Runtime 凭据/模型探测基础设施（凭据不可见，见 Context；credential 级 Readiness 见 Open Questions）。
- 让未实装的 `mohist/agent` Workflow task 加入本并发闸门（见 Open Questions）。
- 跨 Agent 全局资源调度或优先级队列。
- Web 页面布局与交互细节；Slack Connection 健康状态。
- follow-up 在并发闸门处的**完整排队**（v1 选择可重试拒绝，见决策 D5）。

## Decisions

### D1 — Readiness 是读侧评估：定义完备性 + 执行历史派生的缺口知识

`AgentReadinessService`（IScopedService）对 `AgentInfo` 计算结论，**在读取时派生，不持久化到 Agent 聚合**：

- **`Needs setup`（已确认缺口）**：服务端可确认的结构/一致性缺口——例如 model 引用存在但格式非法（非 `provider/model`）、有 variant 却无 model（当前被静默丢弃）。此外纳入**执行历史派生**的缺口：该 Agent 最近一次执行以配置类原因失败（凭据缺失 `api_key`/`unauthorized`、model 不存在 `model not found`、Pi `model-rejected`/`preflight-rejected` 等已被 Runner 分类的错误码）→ 标记对应缺口并指向唯一设置入口。这些是观测到的事实，不是推测。
- **`Ready`（已确认可执行）**：定义结构完备一致，且无已知缺口；最近一次执行成功（Completed）是「凭据/model 真的可用」的强正向证据。
- **`Unknown`（暂无法确认）**：从未执行、或最近结果非结论性（Unknown），Server 无法确认 Runtime/凭据/model 是否真的可用。

**为何如此**：Server 架构上无法主动探测凭据（Context）。把「无法确认」如实表达为 `Unknown`，把「执行已经揭示的缺口」表达为 `Needs setup`，既诚实又随使用积累价值（首次失败后，用户下次看到「凭据未配置，去 X 修复」而非重复 launch 重复失败）。

**launch 闸门**：在创建工作前检查 Readiness。`Needs setup` → 以缺口 + 设置入口拒绝（与现有 archived 检查同层，对 manual/routed/mention 入口一致）；`Unknown` → 允许提交并等待执行验证。已存在 Session 的 follow-up 不受此闸门约束（沿用其已固定的快照）。

**备选**：（a）用 Runner 内存目录判定 model 是否在册——拒绝，因为目录非权威、无 Runner 时为空、且会随 Runner 上下线在 Ready↔Unknown 间抖动；（b）新建 Server 凭据存储主动探测——超本 issue 范围，列为 Open Question。

### D2 — Availability 是读侧结论：Runner 在线/容量 + 每 Agent 并发态

`AgentAvailabilityService` 组合既有 `RunnerStatusService`（在线 Runner、used/total slot）与 D3 的并发态（该 Agent active 数 vs 当前 MaxConcurrentRuns）产出 `AvailabilityResult(CanStartNow, WaitReason)`：

- `CanStartNow = true` 当存在在线 Runner 且有空闲 slot，且该 Agent 未达并发限制。
- 等待原因三选一：`no-online-runner` / `capacity-full` / `concurrency-limit`。
- 经 agent status API 暴露（扩展 `AgentStatusResponse` 或新增 per-agent 字段）+ 等待工作列表（沿用 `ActivityWaitingCardDto` 的 per-run surfacing 模式，区别于今天的聚合看板）。
- Availability 表示当前新执行能否开始；已有 Pending Job 的等待原因是独立读侧事实。Runner 或容量在 Job 的退避期间恢复时，Availability 可为 `CanStartNow`，该 Job 以 `dispatch-pending` 保持可见，直到下一次 durable dispatch retry。
- Web/CLI 只呈现 Server 结论，删除客户端自合成的「Runner at capacity」等文案。

Availability 不读 Readiness，反之亦然——两者互不折叠（满足 spec 的独立性）。Availability 是提交前的**提示性**结论，不是派发预留：Runner 容量全局共享，两个 Agent 可能同时读到 `CanStartNow` 并在 dispatch 时争用同一 slot；最终能否开始仍由 dispatch 时的 runner/capacity/concurrency 闸门裁定。

### D3 — `AgentConcurrencyGrain`（per-Agent）作为并发许可权威 + FIFO 等待队列

新增按 `(projectId, agentId)` 定键的 grain，是本 Agent 并发计数的**唯一权威**。它只持有调度态：活动许可集合（以「会话+轮次」身份标识的 token）与有序等待者列表（waiter = job/session grain 引用 + 执行身份）；**不存储业务事实**（不缓存 Issue/Run/transcript），MaxConcurrentRuns 作为副本从 Agent 读侧读入（每次 acquire 刷新或带版本校验）。

- `Acquire(token, waiter) → Granted | Waiting`：`MaxConcurrentRuns` 为 null → 直接 Granted（无 per-Agent 限制）；否则活动数 < 限制则发牌，否则入 FIFO 返回 Waiting。
- `Release(token)`：移除许可，随后把空出的名额授予 FIFO 队首，经**持久命令**通知该 waiter「许可就绪，推进」（coordinator→participant 单向，符合架构约束；不在 participant 同步栈回调协调者）。
- 许可与**执行（轮次）生命周期**绑定，而非与 AgentJob 绑定：launch 在 dispatch 时获牌、轮次终态时释放；follow-up 在开始新执行时获牌、轮次结束时释放。
- **孤儿许可兜底**：grain 激活时与周期 reminder 按权威态（该 Agent 的 active AgentJob + 有 active 轮次的 Session）对账并修剪悬空许可；waiter 侧各自保留自重试（D4 的 recovery reminder）作为丢通知的安全网。
- **架构归类**：该 grain 是**共享权威资源 grain**（类比 `RunnerRegistryGrain` / `RunnerGrain` 持有 presence / slots / agent-job ledger），**不是** `design/architecture.md:137-186` 所述的命令串行 process manager（如 `IssueRepositoryCoordinatorGrain`）。process manager 约束（只持久化命令投递 fence、不持业务态）针对的是「跨参与者串行命令」的协调者；本 grain 不串行跨聚合命令，只作为被参与者 acquire/release 的信号量与队列，因此它合法地拥有自己的调度态。它仍须遵守：只拥有调度态（许可 token + 等待者顺序 + MaxConcurrentRuns 副本），不持有 Issue/Run/transcript 等业务事实，不跨聚合写，不成为这些事实的第二权威。grant-on-release 通知**异步**派发（持久 reminder/handler，不在 `Release` 调用栈内同步进入 participant），避免同步回调环；participant 不在该通知栈中回调本 grain。

**为何集中计数**：per-session 串行只在单 Session 内生效；MaxConcurrentRuns 要跨该 Agent 的**所有** Session 收敛并发，必须有单一权威计数点。grain 单激活天然串行化 check-then-grant，避免跨 grain 的 check-then-dispatch 竞态。

**备选（更简）**：无中心队列，AgentJob 自重试（既有 `_dispatchTimer`/`agent-job-recovery`）每次重查并发；优点是无孤儿许可、实现最轻；缺点是延迟至多一个退避间隔（≤60s）才感知释放/调高，且无 FIFO 公平。本设计选 FIFO grain 以满足「排队」「调高后自动推进」的体验要求，自重试作为安全网保留。

### D4 — 取消 `runner-unavailable` 终态失败，改为等待（BREAKING）

在 `AgentJobGrain` 移除 `DispatchRetryBoundExceeded → Failed(runner-unavailable)`（`TryDispatchCoreAsync:984` / `ScheduleNextDispatchAsync:1155` / `CheckTimeoutsAsync:932` 三处）。缺 Runner、容量满或并发满时 AgentJob 保持 `Pending`（等待），由既有 dispatch 定时器与 D3 的 grant 通知推进；用户可显式取消以停止。保留 `JobTimeout` 对**已运行** Job 超时 → `Unknown`（与并发无关，不变）。语义向 WorkflowRun 看齐（无限等待而非失败）。

**等待态唤醒与回收**：等待中的 AgentJob 保持 `Pending`、不终态失败（对齐 WorkflowRun 与 AC #3）。为避免空轮询抖动与孤儿累积，稳定等待的 Job 卸下 dispatch 定时器，仅由 permit-grant（并发释放/调高）或 runner 上线信号唤醒，`agent-job-recovery` reminder 作为丢通知兜底；等待 Job 在 Availability 等待列表中可见、可被用户取消，无隐式超时失败。若日后发现被放弃的等待 Job 造成资源累积，可再加可配置的空闲回收，但默认不做终态失败。

### D5 — follow-up 受闸门约束：v1 达限即以可重试原因拒绝（不做完整排队）

follow-up 若会**开始新执行**（目标 Session 当前 idle），在 SignalR 投递前向 D3 Acquire：Granted 则照常投递并在轮次结束（既有 `session.activity idle` 事件清 lease 处）Release；Waiting 则**以独立的并发原因拒绝**（可重试，调用方用同一幂等身份重投），**不持久化该输入**。follow-up 到**忙碌** Session 不触发闸门（per-session 串行已在会话内排队，不产生新并发）。

**为何拒绝而非排队**：完整 follow-up 排队需要为 follow-up 物化 `AgentTurnRecord`、给无 reminder 的 `AgentSessionGrain` 加唤醒机制、并做跨 Session 协调，显著超 medium 范围。可重试拒绝满足 spec（「受闸门约束、不绕过」）与可靠性契约（达边界后拒绝新输入；已接受输入不受影响）。完整 follow-up 排队列为本 issue 的明确取舍（见 Non-Goals / Open Questions）。

### D6 — 闸门放置与聚合边界

- **launch**：`AgentJobGrain.TryDispatchCoreAsync` 在 Runner 分配前先 Acquire 并发许可（Granted 才继续分配 Runner；分配失败则 Release 并重试），轮次终态时 Release。
- **follow-up**：`AgentSessionFollowupRoutes.ExecuteFollowupAsync`（或 `BeginFollowupAsync`）在投递前 Acquire。
- **MaxConcurrentRuns 不进执行定义快照**：闸门实时读 Agent 定义的当前值；编辑 MaxConcurrentRuns 不触碰任何已有 Session 的快照或 Runtime binding（结构上已成立，闸门只需读活值）。调低只影响之后提交的工作；调高由 D3 的 grant 让等待者推进。
- Readiness/Availability 为读侧派生，不在 Agent 聚合事务内写入；并发许可态归属 D3 grain 自身事务，不跨聚合写。

## Risks / Trade-offs

- **[孤儿许可（grain 崩溃未 Release）导致并发被低估]** → D3 激活与周期 reminder 对账权威态修剪悬空许可；waiter 自重试兜底；许可视为可回收租约。
- **[Readiness 对从未执行的 Agent 多为 Unknown，初期价值有限]** → 属设计预期；首次执行后即收敛（成功→Ready，配置类失败→Needs setup）；结构缺口即时可见。已在 docs/agents.md 实装差距中承认。
- **[BREAKING：缺 Runner 不再失败而是无限等待]** → 与 WorkflowRun 一致；用户可取消；可在 D4 加 feature-flag 回退到旧「超时失败」以便灰度。
- **[follow-up 达限被拒绝，UX 弱于 launch 排队]** → v1 取舍；多 Session 且向 idle Session 追问的边缘场景才命中；调用方可重试。完整排队列为后续。
- **[FIFO grain 的状态持久化 vs 性能]** → waiter 列表持久化保证重启不丢排队顺序，但增加写放大；可先持久化活动许可 + 等待者身份的最小集，重启后由对账重建顺序（见 Open Questions）。
- **[Runner 内存目录非权威导致 model 可用性无法进入 Readiness]** → 有意排除；model/凭据可用性由执行历史反映，而非目录探测。

## Migration Plan

1. **Server 先行**：落 D3 `AgentConcurrencyGrain`；在 `AgentJobGrain` dispatch 加并发 Acquire/Release 与 D4 的「等待取代失败」；在 follow-up 路径加 D5 的 Acquire/拒绝；落 D1/D2 读侧服务并接入 agent view/status API 与 launch 闸门。
2. **Web/CLI**：呈现 Readiness 结论与缺口、Availability 结论与等待原因、等待中工作列表；移除客户端自合成的容量文案。
3. **灰度/回退**：D4 的「等待取代失败」可用配置开关回退到旧超时失败；Readiness/Availability 为读侧，禁用即回退到不显示；并发闸门在 MaxConcurrentRuns 为 null 时自动短路为无限制（不改变无配置 Agent 的行为）。
4. **无数据迁移**：MaxConcurrentRuns 已持久化；许可与等待队列为 ephemeral 调度态（持久化仅为顺序恢复），不需要迁移历史数据。

## Open Questions

- **Server 端 Runtime/凭据配置视图**：若要 Readiness 主动（而非执行后）确认凭据/model 可用性，需要新的服务端配置可见性基础设施——本 issue 不交付，作为后续增强；届时 `Unknown` 的覆盖面会收窄。
- **`mohist/agent` Workflow task 是否并入本并发闸门**：该 Action 尚未实装；实装时需决定其 attempt 是否计入、复用同一 per-Agent 许可。
- **许可/等待态的持久化粒度与对账频率**：活动许可集合与 FIFO 顺序的最小持久集、reminder 对账 cadence，在 task 切分时定。
- **CLI/Web DTO 中等待原因与 Readiness 缺口的具体字段形态**：随 task 落地，遵循现有 `ActivityWaitingCardDto` / `AgentStatusResponse` 扩展惯例。
