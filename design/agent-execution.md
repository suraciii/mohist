# Agent 执行模型

本文定义 Workflow、Agent、Session、Runner 与 Runtime adapter 共享的抽象边界。
Runtime 特有行为放在 [`runtimes/`](runtimes/README.md)，例如
[`runtimes/opencode.md`](runtimes/opencode.md)。

## 层次

| 层次 | 概念 | 所有者 | 权威状态 |
|---|---|---|---|
| 定义 | Mohist Agent | Agent context | 身份、instructions、config、skills、状态 |
| 工作 | TaskRun | Workflow context | Workflow task 生命周期、结果、输出、恢复 |
| 工作 | AgentJob | Agent context | 一次 Mohist Agent 工作的生命周期与结果 |
| 执行契约 | Action | Workflow context | 一次工作 dispatch 的 `uses` / `with` 输入输出契约 |
| 会话 | AgentSession | Session context | transcript、context、usage、activity、当前 Runtime binding |
| Runtime | Runtime Session | 外部 Runtime | 物理会话与 provider 执行状态 |
| Adapter | OpenCodeRuntime、PiRuntime | Runner 进程 | protocol、进程、事件、状态核对、错误 |

`Inline Agent` 是产品使用方式，不是另一个实体或 bounded context。它表示 Workflow
TaskRun 直接选择 Runtime 特有的 Action 并提供输入，不解析 Mohist Agent。`Agent
定义引用`（`uses: mohist/agent`）同样不是实体：TaskRun 引用 Mohist Agent 的定义
快照执行，工作所有权与 Session 来源不变。

跨上下文统一定义见 [`../CONTEXT.md`](../CONTEXT.md)。本文只定义这些概念的生命周期、
所有权、事件契约和模块边界，不建立第二套术语。

## 调用路径

| 路径 | 工作所有者 | Runner 入口 | AgentSession 来源 |
|---|---|---|---|
| Workflow 直接调用 | TaskRun | Runtime Action adapter | Workflow |
| 启动 Mohist Agent | AgentJob | AgentJob executor | Agent launch（Web、CLI、Agent Connection、事件或提及） |

```text
Workflow: TaskRun -> Runtime Action adapter --+
                                             +-> Runtime adapter -> Runtime Session
Agent: Mohist Agent -> AgentJob executor -----+
```

两条路径共享 Runner 执行能力和 Session 基础设施，但不共享工作所有者：TaskRun 对
Workflow 工作负责，AgentJob 对 Mohist Agent 工作负责。每个入口把已经解析好的
AgentSession 目标交给 Runtime adapter，Runtime 事实写回该 Session。共享 Runtime
代码不能制造 Workflow -> Agent 的领域依赖。

Web、CLI、Agent Connection、事件路由和评论提及只是“启动 Mohist Agent”这条路径的不同
调用来源，不增加第三条执行路径。交互客户端经 [`agent-api.md`](agent-api.md) 提交任务和
上下文；Agent context 统一解析定义并创建 AgentJob，Session context 统一持有会话。Slack
Bot 等 provider adapter 不能自行 snapshot Agent、创建 Runtime Session 或拥有工作结果。
Agent Connection 来源的 Session 与直接启动来源的 Session 共享稳定 Session ID 的观察、
transcript 和后续输入语义。

## Action 语义

`mohist/opencode` 和 `mohist/pi` 是 Runtime 特有的 Action，回答“用这个 Runtime 执行
本次输入”。它们不接收 Agent ID，不解析 Agent 名称，不读取 Agent 定义，也不创建
AgentJob。因此 Workflow 直接使用它们时形成 Inline Agent。

`mohist/agent` 是 Agent 定义引用 Action：task 用 `with.name` 引用 Project 内的
Mohist Agent，dispatch 时由 server 应用层把名字解析为指令与配置快照，task 按
Inline Agent 同一套机制执行。它不是 Runtime 别名，也不是 AgentJob 的 dispatch
通道：工作所有者是 TaskRun，Session 是 Workflow 来源，不创建 AgentJob。Workflow
领域只持有名字 token；解析经 Agent 读侧在 dispatch 应用层完成，Workflow 不引用
Agent 领域类型。解析失败（不存在或已归档）即 task dispatch 失败；每次 dispatch
重新解析，retry 拿到当时的定义。产品契约见
[`../docs/actions/agent.md`](../docs/actions/agent.md)。

AgentJob 路径不能通过公开的 Workflow Action 契约 dispatch。Agent 定义完成解析和
snapshot 后，其 executor 接收由 Agent 拥有的 execution request。Workflow Action
adapter 与 AgentJob executor 可以调用同一个 Runtime 深模块。复用点是 Runtime 实现，
不是 Action。

manual 启动的 AgentJob 省略 workspace 时由 CLI/Web 入口解析当前 Project 的默认
Workspace，并将实际 Workspace 身份写入 Session 与 launch response。dispatch 一旦提供
workspace，`workspace.path` 就必须是非空字符串；畸形 workspace 是无效输入，不能回退到
默认目录。

## 工作生命周期与会话

TaskRun 与 AgentJob 拥有以下决策：

- pending / running / terminal 状态；
- 成功、失败与结果；
- retry、recovery 或 Workflow 推进。

AgentSession 拥有以下事实：

- 按顺序记录的 SessionInput、AgentTurn、回复、tool calls 与 Runtime 状态；
- context 与 usage；
- model / Runtime observations；
- 当前 activity 与 Runtime binding。

Workflow Action adapter 向 TaskRun 报告工作结果，AgentJob executor 向 AgentJob 报告
工作结果；两者都向 AgentSession 报告会话事实。AgentSession 事件不会推进 Workflow，
也不会让 AgentJob 进入终态。工作失败可以成为 transcript 中的诊断，但 Session 不是
工作结果的裁判。

Session 命令不是工作 dispatch。Follow-up 只向现有 AgentSession 追加 SessionInput，不创建
TaskRun 或 AgentJob。它由当前执行处理，或在同一 Session 中形成后续 AgentTurn。Compact
与 Reset 同样只改变 Session；它们不轮换 AgentSession ID。

AgentJob 关联 launch 创建的首个 SessionInput 与 AgentTurn。`Completed` 表示这次 launch
工作成功返回，不表示 AgentSession 关闭，也不对自然语言任务作语义完成判断。首次回复
可以是澄清问题；之后的 Follow-up 由新的 SessionInput 和相应 AgentTurn 记录，不重开或
改写原 AgentJob。需要业务生命周期的输出必须进入 Issue / Workflow，而不是让 AgentJob
等待整段对话结束。

Agent launch 时固定 Instructions、Runtime、Model、Variant 与 Skills，并由该 AgentSession
的后续输入继续使用。Agent 的并发与调度策略由 Mohist 统一执行；入口不能绕过，策略变化
也不强行改写已经开始的执行。

## AgentSession 模型

AgentSession 的结构靠近 Runtime 的物理会话，但拥有 Mohist 的稳定身份：

下列是领域持久记录的最小形状，不是第二套公共 read schema；Input acceptance 和 dispatch 的
公共字段、状态枚举及 null 规则唯一见 [`conventions.md`](conventions.md#canonical-sessioninput-and-dispatch-schema)。

```text
AgentSession
  Id
  Source
  WorkDir
  Activity
  ContextGeneration
  CurrentBinding?
  ActiveOperation?
  Inputs
  Turns
  Transcript
  Context
  Usage

SessionInput
  Id
  Sequence
  RequestId                 # caller-provided; unique with SessionId
  RequestFingerprint
  Text?
  TurnId
  Route = new-turn | steer
  ContextGeneration
  Source
  Attachments

SessionInputRequestMap
  SessionId
  RequestId
  RequestFingerprint
  InputId
  TurnId
  State
  Revision

TurnDispatch
  canonical projection = TurnDispatchRead in conventions.md

AgentTurn
  Id
  Sequence
  Status
  ContextGeneration
  InputIds

RuntimeBinding
  RunnerId
  Runtime
  RuntimeSessionId
  BindingEpoch

ContextBoundary
  Kind = compact | reset | runtime-change | missing-recovery | force-reset | handoff | rebind
  ContextGeneration
  OperationId
  Result = pending | succeeded | failed | unknown
  ObservedAt
```

`SessionOperationFence` 是写侧的内部 fencing 记录；它只能持久化足以生成
[`SessionOperationRead`](conventions.md#canonical-sessionoperationread) 的数据，以及
`SessionId`、候选创建键和外部调用所需的 target runner/runtime。它不是第二个 read schema。
`ContextBoundary` 和 operation result 是 AgentSession 已有持久状态中的记录形状，不是新的
业务实体。历史 operation 按 `operationId` 保留，以便 response loss 后查询原 operation。
`operationId` 必须由调用方提供并可重用，Server 不生成客户端不可见的 operation key。

Compact、Reset、recovery、handoff、rebind 和 force-reset 的 command 都必须带调用方持有的
`operationId`；同一 `operationId` 是 response loss 后的重试和查询身份。内部 recovery
coordinator 也必须在发出 command 前持有并持久化这个身份，不能让客户端只能等待一个
不可见的 Server key。只有 launch 使用不同的两级身份：客户端提供 `launchRequestId`，Server
在第一次 prepare 中创建 `launchOperationId`；两者的映射必须出现在 canonical launch read
model 中，不能混用。

以下是不变量：

- `Id`、`Source` 与 `WorkDir` 在 AgentSession 生命周期内不变。
- Session parentage is an optional `SessionParentLink` owned separately from immutable `Source`.
  It can only be established for a newly launched child Session and later detached; it never turns
  an Agent launch Source into another Source. The complete tree contract is
  [`subagents.md`](subagents.md).
- `CurrentBinding` 是当前路由事实，包含完整的 `runnerId`、`runtime`、`runtimeSessionId`
  与单调递增的 `bindingEpoch`；它可以整体替换，但 AgentSession 不保存物理 Session 历史。
- `Transcript` 是一个按 AgentSession 顺序追加的会话记录，不按物理 Session 或其它
  子实体拆分。
- `Context` 描述当前 Runtime Session 的上下文；binding 替换后从空开始。`Transcript`
  与累计 `Usage` 不随 binding 替换清空。
- `ContextGeneration` 标识当前逻辑上下文，和 operation fence 的 `ClaimGeneration` 完全不同。
  `ContextGeneration` 从 1 开始；普通 Compact 不递增它，但必须持久化 ContextBoundary 与
  operation result。普通 Reset、Runtime change、missing-recovery、force-reset、handoff 和
  rebind 开始新的
  logical context，并在同一个 Session 事务中递增它。旧 Input/Turn 保留创建时的
  `ContextGeneration`，不能被移动到新上下文。本文任何未限定的 `generation` 只可理解为
  `ClaimGeneration` 的简称，不能指代 `ContextGeneration`。
- 同一 AgentSession 同时最多有一次 Runtime 执行；该串行约束使 transcript 的 Session
  内顺序足以表达会话。
- 一个被接受的 Input 恰好关联一个 Turn；一个 Turn 可以包含多个 Input，但只有 Runtime
  明确支持 steer 时，后续 Input 才能关联已经 running 的当前 Turn。普通后续 Input 创建
  新 Turn，不能改写已有 Input 的 `TurnId`。
- 每个 SessionInput 都必须保存调用方提供的 `RequestId`。Server 以 `(SessionId, RequestId)`
  的唯一约束持久化 `SessionInputRequestMap`；同 key 同 fingerprint 返回原 Input/Turn，同 key
  不同 fingerprint 返回 `rejected(idempotency_key_reused)` 且不写入新记录。response loss 和重复
  提交都只能重读该 mapping；不同 key 才能创建新的 Input。
- 已经接受的 Input 不会因容量限制被丢弃、覆盖或换 ID；容量不足时在受理前拒绝新的
  `new-turn` 输入。`#382` 负责跨 Session 的 capacity claim/release 与容量视图。
- 同一 Session 的有界排队上限是本设计的受理不变量，具体数值是运行参数，不复制 `#382`
  的全局容量策略。
- AgentSession 至多有一个 `ActiveOperation`。它是恢复、Compact、Reset、force-reset、handoff
  或 rebind 的
  持久 operation fence，不是新的业务实体；操作明确完成或拒绝后才能清除。
- 用户输入必须包含可见文本或明确附件；attachment-only 输入不生成隐藏 prompt。
- AgentSession 没有 `completed`、`failed`、`stopped` 或 `closed` 生命周期。

`CurrentBinding` 允许初始为空。首次 launch 的绑定建立由 launch coordinator 推进；只有
绑定和初始 Session 受理事实按各自聚合事务提交后，Runner 才能收到初始 dispatch。

## Activity 与 transcript

### Activity

AgentSession 的活动状态只有：

| 值 | 含义 |
|---|---|
| `idle` | 没有任何非终结 Turn，且没有未完成或结果不确定的 Session operation；这是唯一安全空闲状态 |
| `active` | 至少有一个 `queued`、`running` 或 `outcome_pending` Turn，或有已知仍在推进的 Session operation |
| `unknown` | 任一 Turn、输入受理、Runtime side effect、binding 或 operation 的结果无法确认；不得当作安全空闲 |

新 AgentSession 的初始 activity 是 `idle`，此时允许 `CurrentBinding` 为空。

状态转换为：

```text
idle + input accepted                    -> active
active + known execution or operation ends
  + all Turns terminal                   -> idle
active + no runtime execution, no final result -> active (Turn = outcome_pending)
active + acceptance/side effect uncertain -> unknown
unknown + authoritative evidence        -> active | idle | unknown
unknown + explicit force-reset          -> unknown (old Turn remains unknown)
```

`activity` 的计算只看当前 `ContextGeneration`：旧 `ContextGeneration` 的 unknown 不会与当前
`ContextGeneration` 的 queued、running 或 `outcome_pending` 合并。它们仍通过 canonical read model 的
`unresolvedPrevious`（`unresolvedPreviousCount` 可作辅助）与 `nextAction` 暴露；当前
`ContextGeneration` 的事实单独返回 `currentContextActivity`。

一次执行完成、失败或取消，只有在 Server 已持久化最终结果时才会让对应 Turn 进入
`terminal`；Session 仍需检查是否还有其它非终结 Turn 或 operation。Runtime 进程退出、缓存
回收、HTTP 超时或持久化文件保留同样不能推导 Session 已关闭。

所有会产生新副作用的普通命令先使用同一个按上下文计算的安全准入谓词：

```text
currentContextActivity(session) =
  summarize only Turns, inputs, side effects and operations
  whose ContextGeneration == session.ContextGeneration

unresolvedPrevious(session) =
  all unresolved Turns, inputs, side effects and operation results
  whose ContextGeneration < session.ContextGeneration

safeAdmission(session) =
  currentContextActivity(session) == idle
  && every current-generation Turn is terminal
  && no current-generation unresolved external side effect
  && session.ActiveOperation == null
```

`safeAdmission` 同时门控普通新 Turn、Compact、Reset 和 missing recovery。它禁止在
当前 generation 为 `outcome_pending` 或 `unknown` 时用新 ID 掩盖未知副作用。旧 generation
的 unknown 只有在显式 force-reset 已确认并 supersede 旧 operation、且新 context/binding
边界已提交后，才进入 `unresolvedPrevious` 而不再阻止当前 generation 的新 Input/Turn。
在该边界提交前，当前 generation 仍是 blocked。已知 `running` Turn 上的 steer 是唯一输入
例外：只有当前 Runtime 明确支持 steer、完整 binding 仍匹配且没有未决 operation 时才可受理；
它不会把未知状态变成安全空闲。`force-reset` 是显式的产品逃生路径，不是普通 Reset，规则在
本文后面定义。

### Transcript 契约

SessionInput 与 AgentTurn 是 AgentSession 拥有的子记录，不是可独立寻址和修改的聚合。
Session 是输入顺序、Turn 归属和状态转换的唯一写入权威。Transcript 仍是扁平、按 Session
顺序追加的会话事实；Input 与 Turn ID 只提供稳定关联，不建立第二份消息树或物理 Session
历史。

每条被接受的输入都对应一个稳定 SessionInput，同一调用重试不能复制输入。Input 的 `Route`
为 `new-turn` 或 `steer`：前者创建一个新的 Turn，后者关联当前已经 running 的 Turn。
一个 Turn 可以有多个 steer Input，但每个 Input 只能有一个 TurnId；任何 Input 一旦持久化，
就不能改绑到另一个 Turn。消息、reasoning、tool、usage、model、provider retry、compaction
和状态事实继续按发生顺序进入同一 transcript。

Input 是否已被 Server 接受、Turn 是否仍在执行、Runtime 是否已经产生副作用，都由 Server
记录。`outcome_pending` 表示受理和提交路径已知，但没有最终结果；`unknown` 表示受理、side
effect 或结果本身无法确认。两者都不能安全接受普通新 Turn 或执行上下文操作，也不能自动 replay。

已有 binding 被替换时，`session.context_reset` 是 transcript 中的用户可见边界：

```json
{
  "type": "session.context_reset",
  "payload": {
    "reason": "reset | runtime-change | missing-recovery | force-reset | handoff | rebind",
    "contextGeneration": 2,
    "operationId": "op_...",
    "observedAt": "2026-07-22T10:03:00Z"
  }
}
```

该事实只表达“后续 Runtime 上下文从空开始”，不携带旧或新物理 Session ID，也不建立
binding 历史。首次从无 binding 建立物理 Session 时不写该事实。Reset、Runtime change、
missing-recovery、force-reset、handoff 和 rebind 必须在同一 Session 事务中递增 `ContextGeneration`、持久化
ContextBoundary、替换 binding（如需要）并写入 `session.context_reset`；该事实在替换后的
下一条 `session.input` 之前。普通 Compact 不替换 binding、不递增 `ContextGeneration`，但
必须持久化同一 `ContextGeneration` 的 ContextBoundary 和 operation result；Compact 成功边界
之后的输入仍记录该 `ContextGeneration`。

`session.closed` 不属于目标 DSL：一次执行结束不关闭 Session。
`session.followup_completed` / `session.followup_failed` 同样不属于目标 DSL：Input 与 Turn
分别表达受理和执行，不能用一个 follow-up 事件混合两者。

消费者不能从历史错误、完成或停止事实推导当前 activity。当前 activity 由 Session 状态、
最新 Runtime 证据和 operation fence 决定。

## Follow-up 与 Cancel

Follow-up 的产品语义保留两种路径。空闲 Session 收到 Follow-up 时，以 `Route=new-turn`
受理并开始新的 Turn。已知 `running` Session 在 Runtime 明确支持 steer 时，以
`Route=steer` 把 Input 加入当前 Turn；不支持 steer 时，以 `Route=new-turn` 按顺序放入
后续 queued Turn。`outcome_pending`、`unknown` 或恢复/上下文 operation 期间拒绝普通
Follow-up，不能猜测它属于旧 Turn 还是新 Turn。

```text
acceptFollowUp(session, requestId, inputEnvelope):
  require requestId is caller-provided and non-empty
  fingerprint = canonicalFingerprint(inputEnvelope)
  existing = RequestMap.find(session.id, requestId)
  if existing != null:
    if existing.requestFingerprint != fingerprint:
      return rejected(idempotency_key_reused)
    return readInputAndTurn(existing.inputId, existing.turnId)

  require safeAdmission(session) or supportedRunningSteer(session)
  atomically:
    insert RequestMap(session.id, requestId, fingerprint)
      with unique(session.id, requestId)
    materialize one SessionInput and one Turn
    persist the mapping, Input, Turn and dispatch record together
  if unique constraint loses a concurrent race:
    return read the winning mapping
  return accepted(inputId, turnId)
```

The first launch uses `requestId=launchRequestId` when materializing its first Input; its
`launchOperationId` remains a separate cross-aggregate identity.

`new-turn` 受理前检查 Session 内有界的 queued Turn 上限；达到上限时返回
`rejected(queue_full)`，不持久化 Input。这个上限只约束单个 Session 的排队受理；`#382`
负责跨 Session 的 max-concurrent-runs、capacity claim/release 和容量视图，不在本设计复制
它的调度策略。

受理事务先持久化 Input、Turn 关系和 canonical dispatch record，再异步入队。若 event/outbox
写入明确失败，整个 Session 受理事务失败，Input 不对外报告 accepted；若 Session 事务已
提交但后续入队失败，Input 仍保持 `accepted=true`，Turn 保持非终态 `queued`，并记录
`dispatchStatus=blocked` 与原因。这个 blocked 值表示临时可重试，durable handler 必须按
attempt/deadline signal 继续协调，不能看到 blocked 就返回或让 Turn 永久 queued。入队结果
不确定时保留同一 `dispatchAttemptId` 查询；不能换一个请求身份或 attempt 再投递。

定时输入是到点才投递的一次性 follow-up：Server 在到期时经同一受理路径把一条普通
`SessionInput` 追加给目标会话，不创建新输入类别、调度器或 Session 终态。完整契约见
[`subagents.md`](subagents.md) 的「定时输入」节。

Follow-up 命令只需要三种同步结果：

- `accepted`：Mohist 已持久接受 SessionInput，它可能仍在排队；
- `rejected`：Mohist 已确认没有接受输入；
- `unknown`：无法确认是否接受，不能自动重新发送。

调用幂等键用于找到同一个 SessionInput，不是 AgentSession 内的另一个领域实体，也不用于
把 transcript 分组。`unknown` 后只能使用同一调用身份核对或重试；创建新身份重新发送可能
产生重复副作用。

Compact、Reset、recovery、handoff、rebind 和 force-reset 的调用方必须显式提供
`operationId`；没有 key 的 command 在受理前拒绝，不由 grain 生成客户端不可见的替代 key。
同一 `operationId` 重放返回同一 operation，异 key 不能 join 或覆盖另一个 active operation；
已完成 operation 仍可按同一 key 查询，response loss 不会开启第二个 operation。

Cancel 只针对当前未终结 Turn。等待中的 Turn 可以在 Session 事务中取消；正在执行的 Turn
必须先建立带完整 `FenceToken` 的 Turn stop fence，再请求 Runtime 停止。无法确认停止结果
时，Turn 进入 `unknown`，Session activity 也保持 `unknown`，不能伪造 `idle`。首个 Turn 的
结果由 AgentJob 裁定，后续 Turn 的取消不改写已经终结的 AgentJob。

```text
stopTurn(stopFence, turnId):
  require stopFence targets the current Turn and binding
  token = Server.recheckBeforeExternalEffect(stopFence.token)
  result = Runtime.stop(turnId, fenceToken = token)
  Server.recheckBeforePersistingEffectResult(token)
  persist stop result only if fenceMatch(stopFence, token)
  if result is unknown: persist Turn.status = unknown
```

Compact uses the same before/after fence gates around `Runtime.compact`; neither Runtime effect
may run after lease expiry, binding change, or operation takeover.

## AgentSession 来源

每个 AgentSession 有且只有一个不可变来源。

### Workflow 来源

使用 `(projectId, workflowRunId, sessionName)` 寻址。同一 WorkflowRun 内复用相同名称
会继续逻辑会话。省略显式名称时使用 Work ID，避免无关 task 意外共享 context。

### Agent launch 来源

每次启动 Mohist Agent 时创建，并关联已解析的 Agent ID。一个 Mohist Agent 可以创建
多个 AgentJob 和 AgentSession。之后编辑或归档 Agent，不改变 Session 来源或启动时的
执行 snapshot。

相同 prompt、model、Runtime、workspace 或配置不会合并两个来源。Session 不能从
Workflow 来源迁移为 Agent 来源，反之亦然。

来源特有的 route 只是查询和便利入口，最终都解析为以 `sessionId` 标识的规范
AgentSession 资源。Follow-up、Compact、Reset、transcript 与查询都作用于该资源，
不能实现第二套 Session 生命周期。

## 当前 Runtime binding

AgentSession ID 是逻辑会话的稳定身份。Runtime Session 身份是外部物理维度：

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_...",
  "bindingEpoch": 7
}
```

正常执行、retry、Follow-up、Compact 和 Runner 重启都复用当前 binding。Reset、Runtime
变化和已确认的 Runtime Session 缺失恢复可以整体替换 binding，但不能改变 AgentSession
身份、来源或工作目录。每次替换递增 `bindingEpoch`，并以完整的 `runnerId`、`runtime`、
`runtimeSessionId`、`bindingEpoch` 作为 fence。

AgentSession 只保存 `CurrentBinding`。旧 binding 不进入 aggregate、DTO 或独立查询模型；
已有 transcript 也不会按 binding 拆分。Reset、缺失恢复或 Runtime 变化只在 transcript
记录一次 `session.context_reset`，说明后续 Runtime 上下文从空开始，不记录物理 Session
沿革。

替换使用完整 expected binding 做 compare-and-swap：

```text
replaceBinding(expected, candidate, operationId, ownerId, ownerFence,
               claimGeneration, deadline):
  require safeAdmission(session) unless operation kind is force-reset
  require currentBinding == expected
  require candidate was created for AgentSession.workDir
  require candidate is the complete
    (runnerId, runtime, runtimeSessionId, bindingEpoch) tuple
  token = fenceToken(fence, expectedBinding = expected,
                     candidateBinding = candidate,
                     currentBindingForEffect = expected)
  require token.operationId == operationId
  require token.ownerId == ownerId
  require token.ownerFence == ownerFence
  require token.claimGeneration == claimGeneration
  require token.deadline == deadline
  require fenceMatch(token)
  require current binding and ActiveOperation are still unchanged in this transaction
  require candidate.bindingEpoch == currentBinding.bindingEpoch + 1
  currentBinding = candidate
  if boundary reason is reset | runtime-change | missing-recovery | force-reset | handoff | rebind:
    increment ContextGeneration
  persist ContextBoundary and binding atomically
```

Runtime command/event 必须携带完整的 runner/runtime/runtimeSessionId/bindingEpoch tuple，以及
关联 InputId/TurnId 和适用时的 operationId。它不等于 current binding 时，Server 拒绝该
事件；旧物理 Session、旧 Runner 或旧 operation 的迟到事件不能改变当前 activity、Turn 或
transcript。

物理 Session 的缓存、文件、进程资源与保留策略属于 Runtime adapter。binding 被替换
不要求 Mohist 删除、关闭或继续查询旧物理 Session。

## Runtime Session 缺失恢复

缺失恢复是 Session 对当前 binding 的修复，不是 Prompt retry，也不是 Workflow
recovery。它只能在 `safeAdmission(session)` 成立时启动。正在执行的 Turn 若因 Runtime
消失而可能产生副作用，先变成 `unknown` 或 `outcome_pending`，然后保持 recovery blocked；
不能用恢复建立的新 Session 掩盖旧 Turn。

### Durable recovery operation

RecoveryWindow 不是内存窗口，而是 AgentSession 的一个持久 `SessionOperationFence`，且
同一 Session 同时最多一个 active fence。它的公共字段和按 kind 的 null/required 规则只见
[`conventions.md#canonical-sessionoperationread`](conventions.md#canonical-sessionoperationread)。
写侧另外持久化 `SessionId`、candidate identity、target runner/runtime 和 cleanup fence
关联；这些值用于 fencing，不形成第二个 read schema。`ownerFence`、`claimGeneration`、
`expectedBinding`、`candidateKey`、lease 和 deadline 必须能在重启后恢复。

`claimGeneration` 是 recovery claim 的裸整数代数，不是 `ContextGeneration`、
`bindingEpoch` 或 candidate 版本。任何实现或命令若只写 `generation`，其语义必须是
`claimGeneration`；公共合同和持久字段使用完整名称。`ownerFence` 与 `claimGeneration`
都只能递增，不能在重启、cleanup 或 operation result 写入时重置。

Recovery 的每一个持久写入和 Runtime/provider 副作用都使用
[`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence) 的唯一
`FenceToken` 和 `fenceMatch`。实现不得在本节或其它模块定义缩短版 fence。Recovery 的
`resolve`、candidate `createOrGetEmpty`、`submitInputExactlyOnce`、`recordCandidate`、
binding CAS 和 `complete` 都必须按下面的可执行顺序；cleanup 也使用同一顺序，只是 token
来自独立 cleanup fence：

```text
runFenced(token, effect, persistResult):
  token = Server.recheckBeforeExternalEffect(token)
  result = effect(token)
  Server.recheckBeforePersistingEffectResult(token)
  return persistResultIfFenceMatches(token, result)

resolve = runFenced(resolveToken,
  token => Runtime.resolve(expectedBinding, fenceToken = token),
  result => Session.writePhase(resolveToken, result))

candidate = runFenced(createToken,
  token => Runtime.createOrGetEmpty(candidateKey, fenceToken = token),
  result => Session.recordCandidate(createToken, result))

submit = runFenced(submitToken,
  token => Runtime.submitInputExactlyOnce(inputId, turnId, fenceToken = token),
  result => Session.recordSubmit(submitToken, result))

replace = runFenced(candidateToken,
  token => Session.compareAndSwapBinding(token.expectedBinding, token.candidateBinding, token),
  result => Session.persistBindingAndBoundary(candidateToken, result))

complete = runFenced(completionToken,
  token => no_external_call(),
  result => Session.completeOperation(completionToken, result))
```

`expectedBinding` 和 `candidateBinding` 都是完整 tuple 或显式 `null`；`sessionId`、
`operationId`、`ownerId`、`ownerFence`、`claimGeneration`、`revision`、`leaseUntil` 和
`deadline` 不能省略。Runtime/provider 在实际副作用边界也必须验证同一个 token；token 过期、
lease 被 takeover 或 current binding 改变时返回 `stale_operation_fence`，不创建、提交、
丢弃或完成。任何 phase write、candidate `getByKey`、candidate `discardCandidate`、CAS 和
complete 的 persist 失败都不改变旧 owner 的事实。

创建/claim fence 先于 candidate create：

```text
BeginRecovery(expected, operationId, ownerId, deadline):
  if same operationId and leaseUntil > serverNow and deadline >= serverNow:
    return the existing fence idempotently
  if same operationId and leaseUntil <= serverNow and deadline >= serverNow:
    atomically increment ownerFence and claimGeneration,
      replace ownerId and leaseUntil, and retain operationId, expectedBinding, candidateKey
  if current operation has another operationId and its lease is live:
    return recovery_in_progress
  if the only remaining work is a cleanup fence with a terminal original operation:
    allow a new binding operation; cleanup continues under its own bounded fence
  if current operation deadline < serverNow:
    reconcile candidate; if it is not adopted, transfer cleanup to an independent cleanup fence
    return recovery_expired
  otherwise:
    atomically create the fence with:
      candidateKey = stableCandidateKey(operationId, recovery)
      targetRunnerId = expected.runnerId
      targetRuntime = expected.runtime
      expectedBinding = expected
      candidateBinding = null
      ownerFence and claimGeneration both incremented
    persist the complete operation fence before Runtime.resolve
```

首次 claim、lease takeover、phase 变更、candidate 记录和 operation result 都与 fence
一起持久化。`deadline` 是固定上界，不能靠无限轮询、lease renew 或客户端重试延长。
进程或 Server 重启后，Session 激活和 durable handler 读取已持久化的 operation、ownerFence、
claimGeneration、phase、candidateKey 和 expectedBinding：未到 deadline 时只能用当前 tuple
继续；owner lease 已过期时先 takeover，再继续；旧 owner 的重试即使携带同一 operationId
也因旧 fence fail closed。到 deadline 后先 reconcile candidate 与 current binding。没有候选或
已确认采用时 operation 可进入 `succeeded`，已确认不能采用时进入 `failed`；候选仍存在但
清理结果不确定时，原 operation 进入终态 `outcome=blocked`、`phase=cleanup-pending`。
这只终止原 operation，不永久占住 Session 的 binding admission。候选清理由独立、有限的
cleanup fence 接管；新的 binding operation 可以在 current binding 未等于该候选时开始。

### 触发条件

以下条件必须同时成立：

1. `safeAdmission(session)` 成立；
2. 执行位于当前 binding 的 `runnerId`，Runtime 与工作目录仍匹配；
3. 该 Runner 上的 Runtime adapter 用确定性证据确认 `runtimeSessionId` 已不存在；
4. 本次输入尚未写入 transcript，也尚未向 Runtime 提交；
5. 替换 binding 和随后记录输入时，Server 看到的 expected binding 都仍是 current；
6. recovery operation fence 已在 candidate 创建前被唯一 owner 持久 claim。

Runtime 不可用、超时、权限失败、响应不兼容、数据损坏或任何无法区分“暂时无法读取”
和“确定不存在”的结果都不满足条件。请求落在另一个 Runner 也不是 missing；它必须
路由回 binding 所属 Runner 或明确失败，不能借缺失恢复迁移 Runner。

### 解析与替换顺序

```text
expected = AgentSession.currentBinding
fence = Session.beginRecovery(expected, operationId, ownerId, deadline)
resolveToken = fenceToken(fence, expectedBinding = expected, candidateBinding = null,
                           currentBindingForEffect = expected)
resolved = runFenced(resolveToken,
  token => Runtime.resolve(expected, fenceToken = token),
  result => Session.writePhaseIfFenceMatches(resolveToken, result))

if resolved is ready:
    fence = Session.reloadCurrentFence(fence.operationId)
    selected = expected
else if resolved is definitely-missing and fence is owned:
    createToken = fenceToken(
      fence,
      expectedBinding = expected,
      candidateBinding = fence.candidateBinding,
      currentBindingForEffect = expected)
    candidate = runFenced(createToken,
      token => Runtime.createOrGetEmpty(
        workDir = AgentSession.workDir,
        candidateKey = fence.candidateKey,
        fenceToken = token),
      result => Session.recordCandidateIfFenceMatches(createToken, result))
    fence = Session.reloadCurrentFence(fence.operationId)
    if create response is lost:
      getFence = Session.reloadCurrentFence(fence.operationId)
      getToken = fenceToken(getFence, expectedBinding = expected,
                            candidateBinding = null,
                            currentBindingForEffect = expected)
      candidate = runFenced(getToken,
        token => Runtime.getByKey(getFence.candidateKey, fenceToken = token),
        result => Session.recordCandidateIfFenceMatches(getToken, result))
      if response is still unknown: reconcileBindingOperation(fence)
    candidateToken = fenceToken(fence, expectedBinding = expected,
                                candidateBinding = candidate.binding,
                                currentBindingForEffect = expected)
    selected = runFenced(candidateToken,
      token => Session.compareAndSwapBinding(token.expectedBinding,
                                              token.candidateBinding, token),
      result => Session.persistBindingAndBoundaryIfFenceMatches(candidateToken, result))
    if selected is CAS failure:
      beginCleanupFence(fence, candidate)
else:
    fail without changing the binding

postBindingFence = Session.reloadCurrentFence(fence.operationId)
inputToken = fenceToken(postBindingFence, expectedBinding = expected,
                        candidateBinding = selected,
                        currentBindingForEffect = selected)
Session.recordInputIfFenceMatches(inputToken, input = input)
# The protected write advances the operation/session revision; the submit token
# is built from that new fence revision.
submitFence = Session.reloadCurrentFence(fence.operationId)
submitToken = fenceToken(
  submitFence,
  expectedBinding = expected,
  candidateBinding = selected,
  currentBindingForEffect = selected)
submit = runFenced(submitToken,
  token => Runtime.submitInputExactlyOnce(binding = selected, input = input,
                                          fenceToken = token),
  result => Session.recordSubmitIfFenceMatches(submitToken, result))
completeFence = Session.reloadCurrentFence(fence.operationId)
completeToken = fenceToken(completeFence, expectedBinding = expected,
                           candidateBinding = selected,
                           currentBindingForEffect = selected)
complete = runFenced(completeToken,
  token => no_external_call(),
  result => Session.completeOperationIfFenceMatches(completeToken, result))
```

同一 `candidateKey` 的 create/get 必须返回同一候选或明确失败；创建响应丢失时按 key 查询，
不能创建第二个候选。若 binding CAS 因 Reset、handoff 或另一已提交变化失败，candidate
成为 `orphan`，Server 不采用它，并把清理转交给独立 cleanup fence。每一次 create/get 都先
以原 operation 的 `fenceMatch` 做 Server 条件检查，并把 `candidateBinding` 一起持久化；
不能用 candidate key 单独授权操作。

```text
recordCandidate(operation, candidate):
  token = Server.recheckBeforeExternalEffect(
    fenceToken(operation, expectedBinding = operation.expectedBinding,
               candidateBinding = null,
               currentBindingForEffect = operation.expectedBinding))
  atomically:
    Server.recheckBeforePersistingEffectResult(token)
    require fenceMatch(token)
    require operation.candidateBinding == null
    require candidate.key == operation.candidateKey
    persist candidateBinding = candidate.binding
    persist candidateState = created
    persist phase = candidate-created and advance revision
  return Session.reloadCurrentFence(operation.operationId)

reconcileBindingOperation(operation):
  if currentBinding.adoptedCandidateKey == operation.candidateKey:
    mark operation succeeded, candidateState = adopted, phase = completed
    return adopted
  if currentBinding is not operation.expectedBinding:
    beginCleanupFence(operation, candidate = operation.candidateBinding or unknown)
    return orphaned
  if operation.deadline is expired:
    beginCleanupFence(operation, candidate = operation.candidateBinding or unknown)
    return expired
  if operation.candidateBinding == null:
    getToken = fenceToken(operation, expectedBinding = operation.expectedBinding,
                          candidateBinding = null,
                          currentBindingForEffect = currentBinding)
    candidate = runFenced(getToken,
      token => Runtime.getByKey(operation.candidateKey, fenceToken = token),
      result => recordCandidateIfFenceMatches(getToken, result))
    if candidate is definitely absent: mark failed with nextAction = retry_binding_operation
    if response is unknown: beginCleanupFence(operation, candidate = unknown)
  if operation.candidateBinding exists:
    casToken = fenceToken(operation, expectedBinding = operation.expectedBinding,
                          candidateBinding = operation.candidateBinding,
                          currentBindingForEffect = currentBinding)
    cas = runFenced(casToken,
      token => Session.compareAndSwapBinding(token.expectedBinding,
                                              token.candidateBinding, token),
      result => Session.persistBindingAndBoundaryIfFenceMatches(casToken, result))
    if cas succeeds: mark adopted and complete operation
    else: beginCleanupFence(operation, candidate = operation.candidateBinding)

beginCleanupFence(operation, candidate):
  atomically:
    if candidate is known and currentBinding.adoptedCandidateKey == candidate.key:
      mark operation succeeded, candidateState = adopted, phase = completed
      return already_adopted
    cleanupId = stableCleanupId(operation.operationId, candidate.key or unknown)
    persist a distinct cleanup fence using the canonical FenceToken with:
      operationId = cleanupId
      sessionId = operation.sessionId
      expectedBinding = operation.expectedBinding
      candidateBinding = candidate.binding or operation.candidateBinding or null
      currentBindingForEffect = currentBinding observed at handoff
      ownerId, ownerFence, claimGeneration, revision, leaseUntil
      deadline = min(serverNow + cleanupBudget, operation.deadline + grace)
      candidateKey and candidate identity
    mark operation outcome = blocked, phase = cleanup-pending,
      candidateState = orphan or cleanup-pending,
      nextAction = retry_candidate_cleanup or operator_reconcile_candidate
    release the operation's Session admission slot
```

The cleanup fence has its own `operationId`, owner and monotonic `ownerFence`/`claimGeneration`;
it is not a renewal of the expired operation. Its attempts and cleanup `deadline` are durable and
bounded. A binding CAS change always starts or takes over this independent fence with the newly
observed `currentBindingForEffect`; it never mutates the original fence to make the old owner fit.
It may be claimed after a process restart or lease takeover, but it cannot be renewed past its
fixed deadline. `candidateState=orphan` means the candidate was found and was never adopted;
`candidateState=cleanup-pending` means the cleanup result is not yet authoritative.

Cleanup calls use the independent fence and compare the candidate identity, not only the key:

```text
cleanupCandidate(cleanupFence):
  token = cleanupFence.token(currentBindingForEffect = currentBinding)
  token = Server.recheckBeforeExternalEffect(token)
  atomically:
    require fenceMatch(token)
    require cleanupFence is current owner and serverNow <= cleanupFence.deadline
    require cleanupFence.attempts < cleanupFence.maxAttempts
    increment attempts and persist cleanup phase before any Runtime effect
    if currentBinding.adoptedCandidateKey == cleanupFence.candidateKey:
      mark cleanup fence succeeded and candidateState = adopted
      return already_adopted
    if cleanupFence.candidateIdentity is unknown:
      getToken = cleanupFence.token(currentBindingForEffect = currentBinding)
      candidate = runFenced(getToken,
        token => Runtime.getByKey(cleanupFence.candidateKey, fenceToken = token),
        result => persistCandidateIdentityIfFenceMatches(getToken, result))
      cleanupFence = reloadCleanupFence(cleanupFence.operationId)
      if candidate is definitely absent:
        mark cleanup fence succeeded and candidateState = discarded
        return already_discarded
      if response is unknown:
        persist cleanup-pending and nextAction = retry_candidate_cleanup
        return unknown
    token = cleanupFence.token(currentBindingForEffect = currentBinding)
  discarded = runFenced(token,
    t => Runtime.discardCandidate(cleanupFence.candidateKey,
                                  candidateIdentity = cleanupFence.candidateIdentity,
                                  fenceToken = t),
    result => persistCleanupResultIfFenceMatches(token, result))
  reconcile cleanup response by the same `token.operationId` and candidateKey
  if discarded: mark cleanup succeeded and original candidateState = discarded
  if response is unknown and attempts < maxAttempts and now < cleanupFence.deadline:
    persist cleanup-pending and nextAction = retry_candidate_cleanup
  else if response is unknown or attempts >= maxAttempts or now >= cleanupFence.deadline:
    mark cleanup outcome = blocked and candidateState = cleanup-pending,
      nextAction = operator_reconcile_candidate
```

Before both `getByKey` and `discardCandidate`, the cleanup fence rechecks every field of the
canonical token, including owner lease, revision, expected/candidate binding and the observed
current binding. `currentBinding` may have changed since the original CAS: the old cleanup then
fails closed and a new bounded cleanup fence is created with the changed binding. If the current
binding is the candidate, or the stored adopted-candidate key matches, cleanup returns
`already_adopted` and never deletes it. An old cleanup therefore cannot delete an adopted binding
or a newer binding that reused the same Runtime session. A cleanup response loss is reconciled by
the same cleanup identity; it is never converted into an unbounded retry.

Lease takeover increments `ownerFence` and `claimGeneration` before issuing a new token. An old
owner's create, submit, discard, cleanup, phase write, binding CAS or complete therefore fails the
Server recheck and the Runtime/provider token check, even when it reuses the same `operationId`.
旧 operation 的 cleanup 也不能绕过独立 cleanup fence；清理响应丢失时仍按同 cleanup identity
查询，不能只写日志或静默遗留。

candidate 一旦被 `replaceBinding` 采用，Server 在同一事务把 phase 置为 `rebound`，并把
current binding 的完整 tuple（含新的 `bindingEpoch`）写入 `candidateBinding`，并记录
`adoptedCandidateKey`。此后旧 owner 的 cleanup 即使在 Runtime 侧仍能定位旧 candidate，也
必须先发现 candidate 已 adopted、adopted key 相同或 current binding 已等于该 candidate；
cleanup 只能返回 `already_adopted`，不得 discard、删除或回收 current binding。未被采用的
candidate 只能在匹配其独立 cleanup fence 的 `cleanup-pending` 中 discard。

`replaceBinding` 之后，fence 中的 `expectedBinding` 仍是本次操作开始时固定的旧 tuple，不会
被改写成新 binding。任何后续 phase、输入记录、结果归档或 `complete` 都必须同时携带该
`expectedBinding` 和已采用的 `candidateBinding`，并要求当前 binding 等于 candidate；这样既
保留旧 binding 的 CAS 条件，也不会把新 binding 误判成 stale。未采用 candidate 的 discard/
cleanup 则要求当前 binding 仍不等于该 candidate。

Runner 只报告 Runtime 的 resolve / create / get / discard 事实；`replaceBinding` 与
`recordInput` 都由 Server 裁决。每个 command/event 带完整 runner/runtime/session/epoch
tuple，且每次 phase write、candidate write、binding CAS、input write 和 complete 都比较
operationId、ownerId、ownerFence、claimGeneration、expected binding 与 lease，避免过期恢复
覆盖 Reset、Runtime 变化或另一轮恢复。只有输入持久化成功后 Runner 才能提交给 Runtime。

### 操作边界

| 操作 | 确认缺失时自动替换 | 原因 |
|---|---:|---|
| TaskRun 或 AgentJob 提交新输入 | 是 | 输入尚未提交，可以在空上下文继续 |
| AgentSession 空闲时的 Follow-up | 是 | 它将开始新的执行，使用相同提交顺序 |
| 执行中的 Follow-up | 否 | 输入目标是当前物理执行，替换后语义不同 |
| Compact | 否 | 缺失的上下文无法压缩 |
| Cancel | 否 | 新物理 Session 不是原执行目标 |
| Reset | 不属于自动恢复 | 普通 Reset 需要 safeAdmission；Unknown 只能显式 force-reset |

自动恢复不从 Mohist transcript 重放消息、Prompt 或 tool call。Transcript 是审计与展示
记录，不是重建 Runtime 上下文的命令来源。

## Runtime 变化与 Reset

Runtime change、普通 Reset、handoff 和 rebind 只在当前 generation 的 `safeAdmission(session)`
成立时执行，并持有独立的 `SessionOperationFence`。当前 generation 为 `active`、
`outcome_pending` 或 `unknown` 时全部拒绝；active 必须先等 Turn 终结，unknown 必须先查询
或显式 force-reset，不能用 handoff/rebind 绕过未决副作用。旧 generation 已被 supersede 的
unknown 只进入 `unresolvedPrevious`，不阻止新 generation 的安全准入。

该 fence 必须在任何 Runtime/provider effect 前持久化 canonical
[`SessionOperationRead`](conventions.md#canonical-sessionoperationread) 所需的字段，并额外
持久化 `SessionId`、`candidateKey`、`targetRunnerId` 和 `targetRuntime`。`expectedBinding`、
`candidateBinding`、target 和 candidate key 的 null/required 语义按 canonical schema 执行。
它的每个 phase write、candidate write、CAS 和 complete 都使用 recovery 相同的 `fenceMatch`
与外部 effect recheck。Runner/provider 创建候选前必须携带同一个 token：

```text
BeginBindingOperation(session, kind, operationId, ownerId, expectedRevision,
                      requestedRunnerId, requestedRuntime, deadline):
  atomically:
    require session.revision == expectedRevision
    require safeAdmission(session)
    expected = session.currentBinding
    require expected != null
    require kind in {reset, recovery, force-reset, handoff, rebind}
    require targetRule(kind, expected, requestedRunnerId, requestedRuntime)
    candidateKey = stableCandidateKey(operationId, kind)
    fence = create ActiveOperation(
      operationId = operationId,
      kind = kind,
      ContextGeneration = session.ContextGeneration,
      ownerId = ownerId,
      ownerFence = nextOwnerFence(),
      claimGeneration = nextClaimGeneration(),
      revision = nextRevision(),
      expectedBinding = expected,
      candidateBinding = null,
      candidateKey = candidateKey,
      targetRunnerId = requestedRunnerId,
      targetRuntime = requestedRuntime,
      candidateState = none,
      leaseUntil = leaseFor(deadline),
      deadline = deadline,
      phase = claimed,
      outcome = pending)

  persist fence before calling Runtime/provider

candidateToken = fenceToken(fence, expectedBinding = expected,
                            candidateBinding = null,
                            currentBindingForEffect = expected)
candidate = runFenced(candidateToken,
  token => Runtime.createOrGetEmpty(
    workDir = session.workDir,
    candidateKey = fence.candidateKey,
    fenceToken = token),
  result => Session.recordCandidateIfFenceMatches(candidateToken, result))
fence = Session.reloadCurrentFence(fence.operationId)
candidateToken = fenceToken(fence, expectedBinding = expected,
                            candidateBinding = candidate.binding,
                            currentBindingForEffect = expected)
replace = runFenced(candidateToken,
  token => Session.compareAndSwapBinding(token.expectedBinding,
                                          token.candidateBinding, token),
  result => Session.persistBindingAndBoundaryIfFenceMatches(
               candidateToken, boundaryKind = kind, result = result))
```

`targetRule` is checked and persisted before the external call: `reset` keeps the current
runner/runtime, `recovery` keeps the current runner/runtime after a confirmed missing result,
`rebind` keeps the runner but may change runtime, and `handoff` changes runner only through the
explicit target. `candidateKey = stableCandidateKey(operationId, kind)` is unique to that
operation. A create response loss is reconciled with `Runtime.getByKey(candidateKey)` and
`recordCandidate`; it never calls create with a new key.

`replaceBinding` 的最后一步在同一 Session 事务重新比较完整 `expectedBinding`、当前 owner
lease/generation、`candidateBinding`、revision 和 operationId；成功才递增 `bindingEpoch` 与
`ContextGeneration`、持久化 `ContextBoundary.Kind = handoff | rebind` 和 `session.context_reset`。
新 Session 建立失败、CAS 冲突或 provider response loss 时保留原 binding，operation 进入
`unknown` 或明确失败；调用方只能查询或用同一 `operationId` 重试，不隐式重复创建。
查询到已成功的 operation 必须返回原 `ContextGeneration`、boundary、bindingEpoch 和 result，
不得再次递增或创建物理 Session。

Compact 使用同一完整 fence，即使它不替换 binding：

```text
BeginCompact(session, operationId, ownerId, expectedRevision, deadline):
  atomically:
    require session.revision == expectedRevision
    require safeAdmission(session)
    expected = session.currentBinding
    fence = create ActiveOperation(kind = compact,
      sessionId = session.id, operationId, ownerId,
      ownerFence = nextOwnerFence(), claimGeneration = nextClaimGeneration(),
      revision = nextRevision(), expectedBinding = expected, candidateBinding = null,
      leaseUntil = leaseFor(deadline), deadline, contextGeneration = session.ContextGeneration,
      phase = claimed, outcome = pending)
    persist fence before Runtime.compact

token = fenceToken(fence, expectedBinding = expected, candidateBinding = null,
                   currentBindingForEffect = expected)
result = runFenced(token,
  t => Runtime.compact(binding = expected, fenceToken = t),
  r => Session.persistCompactBoundaryIfFenceMatches(
         token, contextGeneration = fence.contextGeneration, result = r))
```

Compact completion keeps the same binding and `ContextGeneration`; a stale or lost result remains
the same operation's `unknown`/queryable outcome. It must not be treated as a successful boundary
or retried with a new operationId.

Reset 不改变 Runtime；Runtime change 的 `rebind` 可以改变 `runtime`，但不能改变
`runnerId`；RunnerId 变化只能由显式 `handoff` durable operation 完成，不能由 recovery、
Runner reconnect 或旧 event 推断。两者都不能改变
AgentSession 工作目录。两者都保留已有 transcript，不迁移或重放 Runtime 上下文，
也不建立物理 Session 历史。普通 Reset、Runtime change 和 confirmed missing recovery 都
开始新的 `ContextGeneration`；普通 Compact 是唯一不递增 `ContextGeneration` 的上下文边界。

`SessionOperationFence.Kind` 的含义固定如下：

| Kind | 允许的 binding 变化 | 用途 |
|---|---|---|
| `recovery` | 只在同一 Runner 上从 confirmed missing 重建 Runtime Session | 不能迁移 Runner |
| `rebind` | 同一 Runner 的显式 Runtime/binding 替换 | 不能改变 `runnerId`，也不能把未知事实当作 missing |
| `handoff` | 从旧 Runner 切到明确指定的新 Runner | 只能由显式 handoff operation 发起，不能由 Runner 重启或事件推断 |

`handoff` 与 same-runner missing recovery 是两条不同路径。handoff 必须在 fence 中记录
完整 `candidateBinding`、目标 Runner、CAS 的 `expectedBinding`、owner、ownerFence、
claimGeneration、phase、deadline 和 lease；只有完成该 operation 后 `runnerId` 才能变化。
旧 Runner 的每个 command/event 必须携带完整 `(runnerId, runtime, runtimeSessionId,
bindingEpoch)` tuple、适用的 `operationId` 与 `claimGeneration`；任一字段不匹配 current
binding 或 operation fence 时 fail closed，不能更新 Turn、activity、transcript 或清理新
binding。

默认策略是保留原 Turn 的 `unknown` 事实，不自动 replay，也不把它改成成功、失败或 `idle`。
用户明确确认风险并提供新的 `force-reset` operationId 后，Server 才可建立新的 context
boundary。force-reset 是带风险确认的 supersede/takeover operation，不是普通 Reset：它
有自己的 `operationId`、`ownerId`、`ownerFence`、`claimGeneration`、`deadline`、lease、
phase、`expectedBinding` 和 `candidateBinding`，并记录被取代的旧 operation ID（如有）。
旧 provider operation 保留为 `unknown`；Server 不把它补写成成功、失败或已停止，也不伪造
旧 binding 已消失。

force-reset 只有同时满足以下条件才能 claim：

1. 当前 `ContextGeneration` 的 canonical activity 或 targeting-current-generation ActiveOperation
   确实是 `unknown`，且普通 admission 已被该未决事实阻止；仅有旧 generation 的
   `unresolvedPrevious` 不足以再次 force-reset；
2. 调用方提供新的 force-reset operationId、明确的风险确认和失败/重复副作用仍可能发生的
   acknowledgement；
3. 请求携带当前 Session revision（以及同一读取中观察到的 context generation）；revision
   或当前 binding 已变化时拒绝并要求重新读取，不能覆盖并发操作；
4. Server 能保留旧 Input/Turn、旧 operation result 和旧 binding tuple，并为新 operation
   选择新的 context/binding；旧 Input/Turn 不重写、不重放。

force-reset 不调用普通 `BeginRecovery`，而是执行一次原子的 supersede/takeover：

```text
BeginForceReset(session, newOperationId, ownerId, expectedRevision,
                expectedContextGeneration, confirmation, deadline):
  require session.revision == expectedRevision
  require session.ContextGeneration == expectedContextGeneration
  targets = unresolvedTargets(session, contextGeneration = expectedContextGeneration)
  require targets is not empty
  require every target.outcome == unknown
  require ActiveOperation == null
          || (ActiveOperation.ContextGeneration == expectedContextGeneration
              && ActiveOperation.outcome == unknown)
  require confirmation acknowledges possible old side effects and duplicate work

  atomically:
    old = ActiveOperation targeting expectedContextGeneration
    if old != null:
      persist old.operationId with outcome = unknown, phase = superseded,
        supersededByOperationId = newOperationId
    for target in targets:
      persist UnresolvedPrevious(targetId = target.id,
        targetKind = target.kind,
        originalOperationId = target.operationId,
        contextGeneration = target.contextGeneration,
        outcome = target.outcome,
        expectedBinding = target.expectedBinding,
        supersededByOperationId = newOperationId,
        nextAction = inspect_or_explicit_requeue)
    create new ActiveOperation with:
      sessionId = session.id
      operationId = newOperationId
      ContextGeneration = expectedContextGeneration
      ownerId = ownerId
      ownerFence = nextOwnerFence()
      claimGeneration = nextClaimGeneration()
      revision = nextRevision()
      expectedBinding = session.currentBinding
      candidateBinding = null
      candidateKey = stableCandidateKey(newOperationId, force-reset)
      targetRunnerId = session.currentBinding.runnerId
      targetRuntime = session.currentBinding.runtime
      candidateState = none
      deadline = deadline
      supersededTargets = targets
      admission = blocked
      phase = claimed
      outcome = pending
    persist the complete operation fence and ContextBoundary(kind = force-reset,
      result = pending) before any Runtime effect
    mark session.safeAdmission = false until force-reset binding commit
  return the new fence

candidateToken = fenceToken(newFence,
  expectedBinding = newFence.expectedBinding,
  candidateBinding = null,
  currentBindingForEffect = newFence.expectedBinding)
candidate = runFenced(candidateToken,
  token => Runtime.createOrGetEmpty(
    workDir = session.workDir,
    candidateKey = newFence.candidateKey,
    fenceToken = token),
  result => Session.recordCandidateIfFenceMatches(candidateToken, result))
newFence = Session.reloadCurrentFence(newOperationId)
if candidate response is lost:
  getToken = fenceToken(newFence,
    expectedBinding = newFence.expectedBinding,
    candidateBinding = newFence.candidateBinding,
    currentBindingForEffect = newFence.expectedBinding)
  candidate = runFenced(getToken,
    token => Runtime.getByKey(newFence.candidateKey, fenceToken = token),
    result => Session.recordCandidateIfFenceMatches(getToken, result))

finalizeToken = fenceToken(newFence,
  expectedBinding = newFence.expectedBinding,
  candidateBinding = candidate.binding,
  currentBindingForEffect = newFence.expectedBinding)
finalized = runFenced(finalizeToken,
  token => no_external_call(),
  result => atomically:
    require fenceMatch(token)
    require session.currentBinding == token.expectedBinding
    adopt candidate as current binding with next bindingEpoch
    increment ContextGeneration exactly once
    persist ContextBoundary(kind = force-reset, result = succeeded),
      session.context_reset, new currentContextActivity and operation outcome
    set operation.admission = ready
    clear ActiveOperation
    set safeAdmission = true)
if finalized is not committed:
  beginCleanupFence(newFence, candidate)
```

若旧 `ActiveOperation` 不存在，`targets` 仍会把 unknown Input/Turn/dispatch/Runtime
side effect 变成持久 `UnresolvedPrevious`，并在 `SessionOperationRead.supersededTargets`
中暴露；“没有 ActiveOperation”不能跳过 supersede identity。若旧 operation 仍是已知
pending 而非 unknown，force-reset 被拒绝。旧 operation 的 `unknown` 结果、
`supersededByOperationId` 和旧 binding tuple 在其持久 operation 记录中保留，新的 fence 只能
在自己的完整条件元组下推进；它不能把旧 operation 标为完成，也不能把旧 binding 声称为已
停止或已删除。finalize 失败或 response loss 时新 operation 保持 `pending`/`unknown`，且
`admission=blocked`；旧 operation 和所有 targets 仍可按原 identity 查询。

新的 binding 稳定选定并新 ContextBoundary 提交后，才允许新的 Input/Turn 使用当前
`ContextGeneration`。force-reset 响应丢失时，使用同一 operationId 查询或重试，返回同一
新 context、bindingEpoch 和映射；不能再建第二个 context。旧 `ContextGeneration` 的 unknown、旧
binding 的迟到事实和潜在副作用保持可查询，并通过 `unresolvedPrevious`、
`unresolvedPreviousCount`、`currentContextActivity`、`nextAction` 和风险提示暴露。

## 模块所有权

- Workflow 拥有 TaskRun 和 Workflow Action 契约，不解释 Session transcript。
- Agent 拥有 Mohist Agent 与 AgentJob，不解释 Session activity。
- Session 拥有 AgentSession 身份、source、workDir、SessionInput、AgentTurn、activity、current
  binding、transcript、context 与 usage。
- Runner 执行已经解析的工作，创建或恢复 Runtime Session，并报告物理事实。
- Runtime adapter 隐藏 SDK / protocol、缓存、进程、文件、事件核对和错误分类。
- Web 和 CLI 只消费 Server 给出的 activity、current binding 与 transcript，不自行从
  历史结果推导 Session 状态。

Server 是 binding 与 activity 的唯一状态裁判。Runner 不能自行决定 current binding
已经改变，也不能因为 Runtime 进程退出就关闭 AgentSession。

## 测试边界

默认测试不访问真实 Runtime、网络、进程、文件系统 Session 或墙钟。至少覆盖：

- 同一 AgentSession 跨 task、retry、Follow-up 与 Runner 重启复用 current binding；
- SessionInput 与 AgentTurn 的身份、route、归属和顺序在进程重启后保持不变，steer 不复制 Turn；
- 相同输入重试不产生重复记录，已接受输入不会因背压丢失；
- binding 替换与 `session.context_reset` 原子持久化，且事件不包含物理 Session 沿革；
- `outcome_pending` 和 `unknown` 不被映射成 Turn `idle`；activity=idle 时没有任何未终结 Turn；
- `safeAdmission` 阻止未知副作用时的新 Turn、Compact、Reset 和 recovery；
- queue full 在受理前拒绝，accepted 后 enqueue failure 保留 queued 事实并由 durable handler 重试；
- 停止或输入受理不确定时进入 `unknown`，不会自动重放；
- Reset、Runtime 变化和 confirmed missing 在 safe admission 下用 operation identity 原子替换 current binding；
- concurrent recovery 只有一个 owner，Server restart 能 reconcile、过期和清理 candidate；
- lease expiry takeover 递增 ownerFence/claimGeneration，旧 owner 的 phase/write/delete/complete
  全部 fail closed；candidate CAS failure 不采用候选并完成幂等 cleanup，已采用 candidate 不被旧
  cleanup 删除；创建/submit 响应丢失不隐式重放；
- Runner handoff 与 same-runner missing recovery 分开，stale tuple event 被拒绝；
- `force-reset` 保留旧 unknown，并能在新 context 明确创建新的 Input/Turn，风险和 next action 可查询；
- launch Session rejection 和 permanent failure 都有 durable Job outcome、reason、nextAction、
  null+reason mapping 与 tombstone；
- stale expected binding 与旧 Runtime Session 事件被拒绝；
- binding 替换不创建新 AgentSession、不保存物理 Session history，也不复制物理会话数据；
- TaskRun / AgentJob 结果与 AgentSession activity 互不覆盖。

## #378 目标 spec：Input/Turn 生命周期与 Runtime 恢复

本节是 AgentSession 执行契约的目标定义。它补充并细化前文的 AgentSession、Input、Turn
和 binding 模型；下文定义的公共投影优先于实现内部的 `executing`、`completed` 或
`failed` 等词。它不引入新的 endpoint、CLI 语法或事件 DSL。CLI 与 Web 只消费同一份
Server canonical 状态，Workflow 后续复用这份状态，不在客户端各自推导生命周期。

### 对象关系与责任

| 对象 | 领域责任 | 与其它对象的关系 |
|---|---|---|
| Agent | 可长期复用的命名实体，保存定义与配置引用 | 一个 Agent 可产生多个 AgentJob 和 AgentSession |
| AgentJob | 一次 launch 工作的拥有者与结果裁判 | 关联一个首个 Input/Turn；不拥有 follow-up 的生命周期 |
| AgentSession | 一个可继续交互的公共逻辑会话 | 稳定拥有 Inputs、Turns、transcript、context、usage 和 current binding |
| Input | 一次用户或系统提交的执行意图 | 持有稳定 Input ID，恰好关联一个 Turn；重试不创建副本 |
| Turn | 一个 Input 的排队、执行、观察和结果记录 | 持有稳定 Turn ID；同一 Session 的 Turn 串行 |
| Runtime Session | Runner/Runtime 中的物理执行上下文 | 可消失、重建或换绑；不改变 AgentSession 身份 |
| AgentJob executor | 将 launch 意图交给 Session 与 Runner | 不绕过 Session 记录，也不把 Runtime 事件当成公共结果 |

AgentSession 是公共逻辑身份，不是 Runtime Session 的别名，也不是 AgentJob 的别名。
AgentJob 只代表 launch 的一次工作；follow-up 追加到原 AgentSession，不新建 AgentJob。
Agent 的 Instructions、Runtime、Model、Variant 与 Skills 在 launch 时解析并快照。Persona
不是 #378 新造的领域对象；若 #377 以后定义该概念，本设计只消费已解析的 execution
snapshot，不定义其字段或配置体验。`target` 只指现有 launch context 的目标引用，不在
#378 新造 target 模型。快照的变更不回写已创建 Session。Runtime Session 的丢失、重建、Runner
重启或换绑都必须映射回同一个 AgentSession、Input 和 Turn 记录。

每次 launch 或 follow-up 都必须拥有明确的 Workspace 和 target，且它们进入持久记录与
canonical 返回值。CLI 省略 Workspace 时，入口先解析当前 Project 的真实默认范围，再
返回和持久化该范围；不得返回空值、仅返回“默认”或让 Web 再猜目录。

### 稳定身份与 canonical 投影

客户端必须先提供 `launchRequestId`（本次 launch 的 idempotency key）。Server 在受理事务中按
`(projectId, agentId, launchRequestId)` 查找既有请求；第一次 prepare 才生成并持久化
`launchOperationId`、`AgentJobId`、`AgentSessionId`、`InputId` 和 `TurnId` reservation；只有 Session
accept 成功后，Session/Input/Turn ID 才成为可寻址的 live mapping。已 materialize 的 ID
一经持久化不因排队、Runner 重启、binding 替换、重试、Compact、Reset 或 force-reset 改变。
RuntimeSessionId 只标识当前物理 binding；它可以替换，不能作为公共逻辑 Session 的
身份。`launchRequestId` 永久映射到唯一 `launchOperationId`；客户端不能把 Server 生成的
`launchOperationId` 当作首次请求身份。重复提交通过同一请求身份命中原 Input/Turn，不能创建第二份
输入或第二个副作用。

Server 是 canonical read model 与 command result 的权威来源。CLI、Web 和其它入口都是适配器，
不存在“先固化 CLI JSON、再由 Web 复用”的权威方向。每个返回的事实至少带 Server 产生的
`revision` 和 `observedAt`；revision 在对应 Job/Session 权威状态变化时单调递增，observedAt
是 Server 观察该事实的时间。外部续读 cursor 和认证/传输语义仍属于 #387，不在本 issue 新造
endpoint。

| canonical 事实 | 必须回答的问题 | 必须存在的字段 |
|---|---|---|
| AgentJob status | 这次 launch 工作排队、执行、成功、拒绝或失败了吗 | `jobId`、`launchRequestId`、`launchOperationId`、`status`、`outcome`、`reason`、`nextAction`、`sessionId`、`sessionIdReason`、`inputId`、`inputIdReason`、`turnId`、`turnIdReason`、`revision`、`observedAt` |
| Session activity | 这段会话当前 context 能否继续、过去是否仍有未知事实 | `sessionId`、`activity`、`currentContextActivity`、`contextGeneration`、`unresolvedPrevious`、`unresolvedPreviousCount`、`nextAction`、`revision`、`observedAt` |
| Input acceptance | 这条输入是否已由 Mohist 持久接受，以及属于哪一轮 | canonical `SessionInputRead`：`sessionId`、`inputId`、`requestId`、`requestFingerprint`、`state`、`acceptanceReason`、`turnId`、`turnRelation`、`contextGeneration`、`revision`、`observedAt` |
| Turn result | 这一轮的执行状态、派发状态和结果是什么 | `turnId`、`status`、`result`、canonical `TurnDispatchRead`、`contextGeneration`、`revision`、`observedAt` |
| Session operation | Compact/Reset/recovery/handoff/rebind/force-reset 当前或历史如何收敛 | 完整 [`SessionOperationRead`](conventions.md#canonical-sessionoperationread)，不在此处复制字段列表 |
| launch context | 这次工作实际绑定了哪个 Workspace 与现有 target | `workspace`、`target`、`launchRequestId`、`launchOperationId`、`revision`、`observedAt` |

`unresolvedPrevious` 是旧 `ContextGeneration` 中仍未知的 operation/Input/Turn/side effect
摘要；它必须保留 operationId、contextGeneration、outcome 和 nextAction，不能混入
`currentContextActivity`。Job 的 `sessionId`、`inputId`、`turnId` 是 canonical mapping 字段。它们不存在时必须为
`null`，对应的 `...Reason` 必须是非空稳定值，例如 `session_rejected`、`input_not_accepted`
或 `launch_failed_before_session`；不能返回 reservation ID 冒充 live mapping。Session accept
一旦 durable 成功，三个 mapping 一起 materialize；不会出现只有 Session 或只有 Input 的
半映射。

Input 的 `state`、`requestId`、唯一 mapping 和 `turnRelation` 遵循
[`conventions.md#canonical-sessioninput-and-dispatch-schema`](conventions.md#canonical-sessioninput-and-dispatch-schema)。
Turn 的 `dispatchStatus` 只能使用 canonical 的 `queued | retrying | blocked | dispatched |
unknown | terminal`：`blocked` 是临时可重试，`terminal` 才是达 attempt/deadline 上限后的
终态。`blockedReason` 在 `blocked` 或 `terminal/outcome=blocked` 时可非空；未知派发不能伪造
为 blocked 或 terminal。`nextAction`、attempt count、attempt identity 和 deadline 都是必需
的，客户端必须能区分继续协调、查询同一 attempt 和终止后的显式 requeue。

`accepted` 是 Input 的受理事实，不能替代 Job status、Session activity 或 Turn result。
`result` 只有 `terminal` 且 Server 已持久最终结果时才填充；`queued`、`running`、
`outcome_pending` 和 `unknown` 不能伪造成功、失败或取消。`error` 与 `nextAction` 使用用户
可理解的稳定语义，不暴露 provider event name。CLI/Web 只能把这些 canonical 事实翻译成各自
呈现，不得根据本地日志、HTTP 状态或 Runtime 事件另算“完成”。

`accepted` 是 Input 的受理事实，`status` 是 Turn 的观察事实。一个已接受的 Input 可以
暂时处于 `queued`、`retrying` 或 `blocked`，但 durable dispatch deadline 到达时必须转为
`dispatchStatus=terminal` 与 Turn `outcome=blocked`，不能永久 queued。它也可以因 Runner
不可用进入 `unknown`；只有执行已被 Runtime 确认开始时才是 `running`。Session 级 `activity`
仍可为 `idle`、`active` 或 `unknown`，但它
不能替代 Turn status：当前 `ContextGeneration` 的 `activity=idle` 要求没有任何非终结 Turn
和未完成 operation；它不
表示最近一个 Runtime 进程空闲，也不能与未归档结果共存。

Turn status 的定义如下：

| 状态 | 进入条件 | 语义与允许转移 |
|---|---|---|
| `queued` | Input 已接受且 Turn 已持久化，尚未被 Runtime 确认开始；dispatch 可同时是 `queued`、`retrying` 或临时 `blocked` | 可转 `running`；dispatch 达上限时转 `terminal/outcome=blocked`；若 side effect 事实不确定则转 `unknown` |
| `running` | 当前完整 binding 的 Runtime 已确认接收并执行该 Turn | 可转 `outcome_pending`、`terminal` 或因结果不确定转 `unknown` |
| `outcome_pending` | Server 知道 Input/submit 已受理，但 Runtime 暂无执行且最终结果尚未归档 | 只能由权威结果转 `terminal` 或在观察失去确定性时转 `unknown`；不能当作成功或空闲 |
| `terminal` | Server 已持久化不可逆的成功、失败、取消，或 dispatch attempt/deadline 达限后的 `blocked` 结果 | 终态，不再自动重放、不再改变结果 |
| `unknown` | 无法确认 Input acceptance、side effect 或执行结果 | 非终态；只能由权威观察、人工确认或显式 force-reset 处理，不能自动重投 |

Turn 没有 `idle` 状态。`activity=idle` 仅在 Session 没有任何非终结 Turn 且没有未完成
operation 时成立。`outcome_pending` 与 `unknown` 都阻止普通新 Turn、Compact、Reset 和
recovery；它们的区别只在于 Server 已知的事实范围，不改变安全门。内部 `Executing` 映射
为公共 `running`；内部 Completed/Failed/Cancelled 只有在结果已持久化时才映射为 `terminal`，
不能把内部枚举直接暴露给 CLI/Web。

AgentJob 的 canonical 状态是 `preparing | queued | running | unknown | terminal`；
`terminal` 的 `outcome` 是 `completed | rejected | failed | cancelled`。`rejected` 表示
Server 确认本次 launch 没有被 Session 接受，`failed` 表示已确认不可恢复的 launch/执行
失败；二者都必须是 terminal，并带稳定 `reason` 与 `nextAction`。只要 acceptance、dispatch
或 Runtime side effect 仍无法确认，就保留 `unknown`，不能为了清除等待而伪造 `failed`；
Unknown 不是 queued，也必须带查询原 operation、人工核对或 force-reset 等 next action。

### launch 与 follow-up 生命周期

两类调用都经过同一条逻辑序列，差别只有 AgentJob 是否在 launch 时创建：

```text
request -> prepare-job -> accept-session -> queue -> execute -> result
```

1. `request` 校验 Agent execution-definition snapshot、Workspace、现有 target/context、
   输入身份和当前 Session。这一步不承诺 Runtime 已可用；launch 必须带调用方生成的
   `launchRequestId`。它是客户端在首次请求和所有 retry 中保存的唯一 idempotency key。
2. `prepare-job` 在 AgentJob 自己的事务中按 `launchRequestId` 幂等查找；首次请求才生成并
   持久化 `launchOperationId`、JobId、
   SessionId、首个 InputId/TurnId reservation、固定的 launch `deadline`，以及
   `sessionAcceptance=pending` 和 durable outbox event；它不写 Session。reservation 只是幂等
   占位，不是可寻址的 Session、Input 或 Turn。
3. `accept-session` 由 launch coordinator 发送带 `launchOperationId` 和 reservation IDs
   的幂等命令。Session accept/reject 的结果必须持久化为 `pending | accepted | rejected`；
   accept 时 Session 自己的事务一次性 materialize Session、Input、Turn 关系及 durable
   dispatch event，并将 Job 的 acceptance 记录更新为 `accepted`。成功返回 `accepted=true`
   和稳定 InputId/TurnId；此时状态通常为 `queued`，不能写成 `running`。rejected 也必须写
   durable reason，不能只在同步响应里返回。
4. `queue` 由 Session event/outbox 推进有序执行队列。相同 Session 严格只有一个被确认
   执行的 Turn；follow-up 在前一 Turn 未终结时排队，不插队、不合并、不覆盖。Session
   queue 上限属于本设计；跨 Session capacity claim/release 和视图属于 `#382`。
5. `execute` 由绑定 Runner 核对或恢复 Runtime。只有收到当前完整 binding 的开始事实后，
   Turn 才转为 `running`。不同 AgentSession 可并行。
6. `result` 只由 Server 根据当前 binding、关联 InputId/TurnId 和权威结果归档。已知
   最终结果使 Turn 进入 `terminal`；Runtime 先返回空闲而结果仍未确定时保持
   `outcome_pending`，不能写成 `idle`。

launch 为一次新的 AgentJob 创建首个 Input/Turn，并将 JobId、SessionId、InputId、TurnId
一起返回；若 Session 尚未接受，则这三个 mapping 字段返回 `null` 及各自 reason，不能返回
reservation ID。AgentJob、AgentSession、Input 和 Turn 不共享跨聚合事务：AgentJob
事件/outbox 驱动 Session 的幂等命令，Session 事件再驱动首 Turn dispatch。follow-up 不创建
AgentJob，只追加 Input/Turn。

launch coordinator 必须按以下结果收敛：

| 事实 | Job 结果 | 映射与保留 |
|---|---|---|
| Session 明确拒绝 | `terminal / rejected` | `sessionId`、`inputId`、`turnId` 全为 null + reason；reservation 只留在 launch tombstone |
| prepare/outbox/Session 的不可恢复失败，且确认没有 Session side effect | `terminal / failed` | 未 materialize 的映射全为 null + reason；保留 launch tombstone，不留可执行 outbox |
| Session 已接受 | `queued` 或后续 `running/terminal` | 三个 live mapping 一起返回，后续只更新状态，不更换 ID |
| response loss 或 acceptance/side effect 不确定 | `unknown` 或已知当前状态 | 先用原 `launchRequestId` 找到 `launchOperationId`，再查询/重试原 operation；不得新建 Job、Session、Input、Turn 或 submit |

`reason` 和 `nextAction` 是 Server canonical 的稳定产品语义，例如
`agent_archived`、`needs_setup`、`invalid_input`、`queue_full`、`session_conflict`、
`launch_persistence_failed`；暂时不可用必须继续同一 operation，不得提前标为 permanent
failure。若 deadline 到达，只有在 Server 已证明 Session 未接受且没有可能的 Runtime side
effect 时才能转 `terminal / failed`；否则保持 `unknown` 并给出人工核对或 force-reset 的
next action，不制造假终态。

响应丢失或 Server 重启后的查询必须先按 `launchRequestId` 找到唯一
`launchOperationId`，再读取 Job 的 durable acceptance 记录：`accepted` 返回原三项 mapping，
`rejected` 返回 terminal reason，`pending` 重投同一 accept command。若 Session 已保存同一
operation 的 accept 记录，则以 Session 记录补回 Job，若没有记录才允许同一 command 重试。
这样原 launch operation 永远只对应一组 reservation 和最多一组 live mapping，不产生 dangling
live ID 或无限 pending；客户端不需要猜测或预先知道 `launchOperationId`。

### accepted 后的 durable dispatch retry

`accept-session` 提交 Input/Turn 后，入队不是瞬时副作用。Session 同一事务必须创建
[`conventions.md#canonical-sessioninput-and-dispatch-schema`](conventions.md#canonical-sessioninput-and-dispatch-schema)
中的 `TurnDispatchRead`，固定 `dispatchDeadline`，并写入 durable outbox event。每次 retry
先在 Server 原子递增 `dispatchAttemptCount`、生成唯一 `dispatchAttemptId` 并将状态置为
`retrying`，再使用完整 `FenceToken` 调用 Runner；response loss 只按同一
`dispatchAttemptId` 查询，不创建第二次 attempt。

```text
reconcileAcceptedDispatch(record):
  if record.dispatchStatus in {dispatched, terminal}:
    return the stored result
  if record.dispatchStatus == blocked:
    if now >= record.dispatchDeadline or record.dispatchAttemptCount >= maxDispatchAttempts:
      return terminalizeDispatchBlocked(record, reason = dispatch_retry_exhausted)
    atomically persist dispatchStatus = retrying and nextAction = dispatch_now
  if record.dispatchStatus == unknown:
    result = effectWithFence(record.dispatchFence,
      token => queryRunnerDispatch(record.dispatchAttemptId, fenceToken = token))
    if result is unknown and bounds remain:
      persist dispatchStatus = unknown, nextAction = query_same_dispatch_attempt
      return retry_scheduled
    if result is accepted:
      return persistDispatchedIfFenceMatches(record, result)
    if result is definitely_rejected:
      persist dispatchStatus = blocked and schedule durable retry
  if now >= record.dispatchDeadline or record.dispatchAttemptCount >= maxDispatchAttempts:
    return terminalizeDispatchBlocked(record, reason = dispatch_retry_exhausted)
  atomically:
    require Input.acceptance == accepted
    increment dispatchAttemptCount
    dispatchAttemptId = stableAttemptId(record.turnId, dispatchAttemptCount)
    dispatchFence = claimTurnDispatchFence(record, dispatchAttemptId,
      deadline = record.dispatchDeadline)
    persist dispatchStatus = retrying, dispatchAttemptId, dispatchFence
  result = effectWithFence(dispatchFence,
    token => Runner.enqueue(record.inputId, record.turnId,
                            record.dispatchAttemptId, token))
  if result is accepted:
    persistDispatchedIfFenceMatches(record, result)
    return dispatched
  if result is definitely_rejected:
    if now < record.dispatchDeadline && record.dispatchAttemptCount < maxDispatchAttempts:
      atomically persist dispatchStatus = blocked,
        blockedReason = temporary_enqueue_blocked,
        nextAction = retry_dispatch_at_durable_signal
      return retry_scheduled
    return terminalizeDispatchBlocked(record, reason = dispatch_retry_exhausted)
  persist dispatchStatus = unknown, nextAction = query_same_dispatch_attempt
  return reconcile_same_attempt

terminalizeDispatchBlocked(record, reason):
  atomically:
    require Input.acceptance == accepted
    require record.dispatchStatus != terminal
    persist dispatchStatus = terminal
    persist Turn.status = terminal, Turn.outcome = blocked
    persist blockedReason = reason
    persist nextAction = inspect_or_explicit_requeue
  return terminal_blocked
```

The accepted Input and its Turn are never deleted, replaced, or changed to `rejected` by this
path. A temporary `blocked` record is non-terminal and must schedule another coordinator wake-up;
the coordinator cannot return just because it read `blocked`. A permanent enqueue failure is
`dispatchStatus=terminal` with Turn `status=terminal`, `outcome=blocked`, a durable
`blockedReason`, fixed attempt/deadline evidence and `nextAction`; no automatic retry follows.
A later explicit requeue is a new bounded operation that references the same Input and Turn, and
cannot silently create another Input, Turn, or launch.

Rejected/failed Job、`launchRequestId`、launch operation、reason、nextAction 和未 materialize reservation
以最小 tombstone 形式永久保留；完整 transcript 或大 payload 可以按既有保留策略清理，但
`launchRequestId`、`launchOperationId`、JobId、三项 reservation/live mapping、terminal outcome、reason、
nextAction 和 revision 不能删除。reservation IDs 永不回收复用，也不创建空
Session/Input/Turn；旧 `launchRequestId` 和 `launchOperationId` 永远只能返回原 outcome 或明确
的永久 tombstone，不能被新 launch 重新解释为另一组 mapping。

同一 Session 的串行约束是生命周期事实，不是全局容量策略：前一 Turn 处于 `queued`、
`running`、`outcome_pending` 或 `unknown` 时，后续 Input 只能按 steer/queue 语义处理或被
明确拒绝，不能并行提交到同一 Runtime。#382 负责跨 Session 的 max-concurrent-runs、
capacity claim/release 与容量视图；#378 只定义 Session queue 上限、顺序和状态事实。

### Runtime 恢复状态机

恢复状态机由 Server 裁判状态、Runner 事实和当前 binding CAS 组成。它不依赖客户端
猜测，也不把 HTTP 超时当作 Runtime 缺失：

```text
Bound
  | disconnect / runner restart / probe unavailable
  v
ObservationUnknown -- authoritative present --> Bound
  | authoritative definitely-missing + safeAdmission
  v
RecoveryClaimed -> CandidateCreated -> CAS Rebound
  | concurrent claim / deadline / candidate cleanup failure
  v
RecoveryInProgress | RecoveryFailed | RecoveryExpired | CleanupPending
```

- `Bound`：当前 binding 是唯一目标。正常 retry、follow-up、Compact、Reset 和 Runner
  重启都先复用它；Runner 重连后重新报告当前 physical session 的事实。
- `ObservationUnknown`：断线、超时、Runner 不可达、权限错误、非 404 错误、格式错误或
  任何不能证明“不存在”的结果。保留原 binding，不创建候选，不 replay，不自动换绑。
- `RecoveryClaimed`：Runner 已明确报告当前 Runtime Session 不存在，Server 先持久化唯一
  operation owner、ownerFence、claimGeneration、deadline 和 candidate key，再为同一
  AgentSession 创建空 Session 并用完整 expected binding 做 CAS。第二个 recovery 只得到
  `recovery_in_progress`。
- `Rebound`：候选 Runtime Session 创建成功、binding 原子替换成功，并写入一次
  `session.context_reset(reason=missing-recovery)`，同时递增 `ContextGeneration`。旧
  Input/Turn 的 mapping、Workspace、target 和公共 transcript 保持不变；后续新 Input/Turn
  只使用新 `ContextGeneration`。
- `RecoveryFailed` / `RecoveryExpired`：创建、Runner 路由、CAS、deadline 或持久化失败。
  原 binding 不被伪造替换；candidate 未绑定时必须按同一 key 幂等 discard。清理结果不确定
  时保留 `cleanup_pending`，不允许第二个 recovery。

恢复触发必须满足“权威确认缺失”。Runner 重启本身、连接断开本身和读超时都不满足；
它们只进入 `ObservationUnknown`，待同一 Runner 的 probe 给出 `present` 或
`definitely-missing`。物理 Session 存在时必须继续使用它，不能因重连而新建。

确认缺失、且 `safeAdmission(session)` 成立时，可自动创建空 Runtime Session、换绑，并让
后续输入继续执行。若当前 Turn 已经可能提交到旧 Runtime，该 Turn 保持 `outcome_pending`
或 `unknown`，recovery blocked；恢复仍可为同一个 AgentSession 重建和换绑，但绝不把旧
Input、prompt、tool call 或 side effect 自动 replay 到新 Runtime。新的输入必须等 recovery
和安全准入完成，不能借新 InputId 掩盖旧 Turn。

恢复成功不等于原 Turn 成功。它只说明公共 AgentSession 获得了新的可用 Runtime 上下文。
原 Turn 只有收到旧 binding 的权威终态才可进入 `terminal`；旧 binding 的迟到事件因
RuntimeSessionId 不匹配被丢弃。恢复失败也不关闭 AgentSession，不生成新的 SessionId，
不把错误压缩成 `Session failed`。

missing-recovery 不是 Unknown Turn 的例外。若旧 Turn 已可能产生 side effect，Server
保持 `unknown` 或 `outcome_pending`，不允许 recovery、Compact、普通 Reset 或新 Turn 通过
旧 Turn 的空档进入 Runtime。只有用户明确选择 `force-reset`，才可建立新的 context boundary；
原 Turn 与副作用风险保留，完整 binding tuple、ownerFence 和 claimGeneration 仍保护迟到事件。

状态查询或相同请求身份的重试只做 observation：

- queued、running、outcome_pending、terminal、unknown 的重复请求只返回原记录，不重新 dispatch；
- unknown 只能被权威事件或显式用户操作推进，不能因为客户端重试而变成 queued；
- side effect 结果不确定时不自动 replay；用户应先查询，仍不确定时按 `next action` 选择
  Reset 或人工核对。

### 断线、重复提交与恢复窗口的确定性规则

| 场景 | 保留事实 | 自动动作 | 公共结果 |
|---|---|---|---|
| 请求后客户端断线 | 已持久化的 Input/Turn 与原请求身份 | 不重发；重连后查询 | 原 InputId/TurnId，状态按 Server 当前事实 |
| 重复提交同一请求身份 | 原 Input/Turn | 返回原记录；仅 queued 可继续同一 dispatch | `accepted` 不重复，副作用至多一次受理 |
| 不同身份再次提交相同文本 | 原记录与新请求身份不同 | 不推断等价，不自动去重 | 新请求按正常校验，必要时拒绝或产生新 Input |
| Runner 重启 | current binding | Runner 重连 probe；存在则复用 | 不改变 Session/Input/Turn ID |
| 连接断开/超时 | current binding 与可能的活动事实 | 保留 binding，进入 observation unknown | 活动中的 Turn 为 `unknown`，不当作 idle |
| 明确确认 Runtime 缺失且无未决副作用 | Session 记录 | 创建空 Runtime、CAS 换绑并开始新 ContextGeneration | 同一 Session，后续 Turn 可 `queued` |
| 明确确认缺失但旧 Turn 可能已提交 | 旧 Turn 与其副作用不确定性 | 不自动换绑；只允许显式 force-reset | 旧 Turn `unknown`，给查询/force-reset next action |
| 恢复失败 | 原 binding 与原记录 | 不换绑、不关闭 Session | `recovery_failed`，原因具体且可行动 |

恢复窗口不能靠墙钟轮询制造结论。若实现需要 deadline，必须注入 `TimeProvider` 或等价
fake，并把“窗口过期”作为持久化的恢复结果；测试不能用 sleep 或当前时间碰运气。

恢复失败的公共错误至少区分以下可行动语义；字段名沿用现有 canonical error/result
外壳，不把这些值实现成新的命令语法：

| 原因 | 用户可观察含义 | next action |
|---|---|---|
| `recovery_in_progress` | 同一 Session 已有恢复操作占用窗口 | 查询原 Session/Input/Turn，等待该操作给出结果，不重复提交 |
| `runtime_unavailable` | Runner 或 Runtime 暂不可达，不能证明缺失 | 等待 Runner ready 后用原请求身份查询；不要新建请求 |
| `runtime_missing_unconfirmed` | probe 结果不足以证明 physical session 不存在 | 继续查询或让 Runner 重连 probe；不要 Reset 代替事实判断 |
| `recovery_failed` | 已确认缺失但 create/CAS/持久化失败 | 检查 Runner、Workspace 和权限；在 safe admission 下显式 Reset，仍保留原 Session 诊断 |
| `turn_outcome_unknown` | 原 Turn 可能已产生副作用但没有权威结果 | 查询原 Turn；仍 Unknown 时人工核对或显式 force-reset，绝不自动 replay |

### Compact、Reset 与公共上下文边界

Compact 与 Reset 都是 AgentSession 的上下文边界操作，不是 Workflow Action，也不创建
新的 AgentSession、AgentJob、Input 或 Turn。两者保留已有公共 transcript、稳定 IDs、
Workspace、target 和累计 usage；它们只改变后续 Runtime context 的边界，并各自持有
`operationId`、expected binding、owner、ownerFence、claimGeneration 和 bounded deadline 的
operation fence。

- Compact 在 Session 可安全进入边界时请求 Runtime 压缩当前上下文；成功后继续使用同一
  binding，不递增 `ContextGeneration`，但必须持久化同一 `ContextGeneration` 的 ContextBoundary
  和 operation result。后续输入从该成功边界继续。
- Reset 建立空上下文；必要时创建新的 Runtime Session 并整体替换 current binding，
  递增 `ContextGeneration`，但保留旧 transcript、旧 Input/Turn 映射和逻辑 Session 身份。
- Runtime change 和 confirmed missing recovery 与 Reset 一样递增 `ContextGeneration`；
  新 Input/Turn 只写新 `ContextGeneration`，旧 Input/Turn 的 `ContextGeneration`、TurnId 和
  结果永不改写。
- 运行中、`outcome_pending`、Unknown 或 recovery fence 内不能假装已经完成 Compact/Reset；
  返回当前状态和 next action。provider 已执行但响应丢失时 operation 为 `unknown`，只能
  查询同一 operation 或显式用同一幂等 key retry，不能隐式重复。
- 默认保留 Unknown 原事实，不自动 replay。用户明确确认风险并提供新的 `force-reset`
  operationId 后，才可递增 `ContextGeneration`、建立新的 context boundary 和新 binding；
  旧 Turn 仍为 `unknown`，新 context 可创建新的 Input/Turn，二者不能互相替换。旧 binding
  的迟到事件按完整 runner/runtime/session/epoch tuple 与 operation fence 丢弃，重复副作用
  风险和 next action 必须可见。
- Compact/Reset/force-reset 响应丢失时，按原 operationId 查询持久 operation result；已完成
  的 operation 返回原 ContextGeneration、boundary、binding 与映射，未完成的 operation 继续
  原 phase。查询不得因为客户端失联而再次递增 `ContextGeneration`。
- 边界记录是公共领域事实，不把 provider 的 raw event、内部 session ID、tool 细节或
  重建诊断直接写入公共 transcript。公共 transcript 的默认投影由 #384 负责；历史和
  Session timeline 展示由 #385 负责，本设计只规定“旧记录保留、边界可观察、内部事件不
  外泄”。

### 六条验收标准与场景/实施映射

六条 AC 不是六套实现；每条都对应可观察事实、失败场景和一个最小实施批次：

| AC | 可观察验收标准 | 场景映射 | 实施批次 |
|---|---|---|---|
| AC1 稳定受理身份 | launch/follow-up 都有 caller `requestId`；`(SessionId, requestId)` 唯一映射稳定返回同一 Input/Turn；accepted 只表示 Input 已接受 | harness、正常 launch、相同 key 重试/response loss、不同 key、key 重用改 payload、accept rejection | Batch 1 |
| AC2 Turn 与输入语义 | queued/retrying/blocked/dispatched/terminal/unknown 与 Turn status 自洽；blocked 可重试、terminal blocked 不再 retry；steer 关联 existing Turn；queue full 在受理前拒绝 | 正常 follow-up、steer、follow-up 排队、queue full、accepted 后入队失败、permanent dispatch failure | Batch 2 |
| AC3 Server canonical 事实 | Job status、Session activity、Input acceptance、Turn result 分开返回；Input/dispatch 使用 conventions 唯一 schema；Job 映射缺失时 null+reason；attempt/deadline/reason/nextAction/revision/observedAt 可追踪 | canonical read、stale event、Job/Session 结果分离、force-reset 后 current activity | Batch 3 |
| AC4 Launch 跨聚合收敛 | AgentJob/Session 不共享事务；durable coordinator/outbox 用幂等命令把部分失败收敛到唯一 Job/首 Input/Turn 映射 | launch partial failure、Server restart、Runner submit response loss | Batch 4 |
| AC5 Binding 与 recovery fence | 同一 Session 只有一个 recovery owner；每个 effect 比较完整 `FenceToken` 的 session/operation/owner/fence/generation/revision/expected/candidate/lease/deadline；candidate get/discard、CAS、cleanup deadline/CAS changed 和旧 owner fail closed 均可观察 | concurrent recovery、lease expiry old owner、Server restart、candidate CAS failure、cleanup binding changed、Runner handoff、旧事件 | Batch 5 |
| AC6 Context operation 与 Unknown | Compact、Cancel/stop 和 binding operation 都有 effect 前后 fence；Compact 不递增 generation；Reset/Runtime change/missing-recovery/force-reset 递增；force-reset 原子保留 superseded targets，binding 提交前 `admission=blocked` | Compact success/response loss、provider already executed、cancel stop uncertain、force-reset with ActiveOperation、force-reset with only unknown Turn/Input/dispatch | Batch 6 |

### 可观察不变量与场景矩阵

实现必须能通过 Server fake 和 Runner/Runtime fake 观察以下不变量：

1. `accepted=true` 必有持久 InputId/TurnId；`(SessionId, requestId)` map 的同一 key 永远指向同一对 ID，不同 key 才创建新 Input。
2. 一个 Input 恰好属于一个 Turn；一个 Session 内 Turn 的顺序持久且不可被重排。
3. `running` 只来自当前 binding 的执行事实；旧 Runtime 事件不能改变当前状态。
4. Turn 不存在 `idle`；`outcome_pending` 与 `unknown` 都不能被误报为终态或安全空闲；
   activity=idle 时没有未终结 Turn。
5. `safeAdmission` 阻止未知副作用时的新 Turn、Compact、Reset 和 recovery；steer 只有在
   已知 running 且 Runtime 明确支持时例外。
6. 一个 Session 同时最多一个 confirmed Runtime execution；不同 Session 可以并行；不出现
   `#382` 的全局容量断言。
7. confirmed missing 才能在同一 Runner/runtime 上换绑；handoff 有独立 operation；不确定
   错误保持 binding，side effect 不自动 replay。
8. recovery/operation/cleanup/stop fence 的唯一 owner、ownerFence、claimGeneration、revision、
   expected/candidate binding、leaseUntil/deadline 在 concurrent、lease expiry、restart、CAS
   failure 后仍可查询；裸 generation 不与 ContextGeneration 混用，所有 Runtime effect 前后都
   recheck 完整 token。
9. Launch partial failure、accepted 后 enqueue failure 和 response loss 都保留已提交事实；
   `blocked` retry 不提前返回，attempt/deadline 上限原子产生 terminal blocked，不能创建第二个
   Input/Turn/side effect。
10. Compact/Reset/force-reset 保留旧 transcript 与稳定 Session ID；force-reset 不改变旧
    unknown Turn，旧事件按 fence 丢弃，风险与 next action 可见。
11. 换绑不改变 AgentSession、Input、Turn、Workspace、target 或既有 transcript；RunnerId 变化
    只能由 handoff operation 完成。
12. Compact 成功不递增 ContextGeneration 但有持久 ContextBoundary 和 operation result；
    Reset、Runtime change、missing-recovery 和 force-reset 递增它，旧 Input/Turn 映射不变。
13. Compact/Reset response loss、active operation force-reset 和旧 `ContextGeneration` unknown 都
    可查询；内部 Runtime 事件不会直接成为公共消息，current activity 不混入旧 unknown。
14. cancel/stop 的既有语义不变；停止结果不确定时保持 Unknown，不自动重投；恢复失败包含
    具体 reason 与 next action，且 AgentSession 仍可查询和诊断。

| 场景 | 初始条件 | 关键断言 |
|---|---|---|
| 正常 launch | 无 binding、空 Session | 返回稳定 Job/Session/Input/Turn，accepted 后先 queued，再 running，最终 terminal |
| 正常 follow-up | Session idle、已有 transcript | 不新建 Job；同一 Session 新 Input/Turn 串行执行 |
| steer | 当前 Turn running，Runtime 明确支持 steer | 新 Input 关联 existing Turn，不创建第二个 Turn |
| follow-up 排队 | 前一 Turn running | 后一 Turn 保持 queued；前一 Turn 终结后才可 running |
| queue full | Session queued 上限已达 | rejected(queue_full)，不持久化新 Input |
| harness | Server store、Runner/Runtime fake、outbox 和可注入时间按固定事件顺序运行 | 不访问真实网络、进程、Runtime、DB 或墙钟；每个断言能观察持久状态和副作用次数 |
| accepted 后入队失败 | Session 事务已提交，outbox/queue 失败 | accepted 保留，Turn queued + `dispatchStatus=blocked`（非终态），durable retry signal 推进到 retrying，不丢输入 |
| permanent dispatch failure | dispatch attempt 达到最大次数或 deadline | Input/Turn 保留；`dispatchStatus=terminal`、Turn terminal + outcome=blocked、attempt count/deadline/reason/nextAction 持久化，之后不再 retry |
| 断线 | Turn running，Runner 不可达 | binding 保留，Turn unknown；恢复前不重发 |
| duplicate submit | 相同 `(SessionId, requestId)` response loss/重复提交，及不同 key | 相同 key 返回原 Input/Turn 且不重复 dispatch；不同 key 才创建新 Input；payload 改变的相同 key 被拒绝 |
| Runtime disappear | probe 明确 missing | 空 Runtime 创建、CAS 换绑、同 Session context boundary |
| ambiguous disappear | timeout/非 404/Runner restart | 不换绑、不 replay；进入 observation unknown |
| recovery success | candidate 与 CAS 成功 | 公共 Session 可继续查询；原未决 Turn 仍按事实为 terminal 或 unknown |
| recovery failure | create/CAS/Runner 失败 | 保留原 binding，返回具体错误与 next action |
| concurrent recovery | 同一 binding 同时收到两次 missing | 只有一个 durable owner/candidate；另一个得到 recovery_in_progress |
| recovery restart/expiry | fence 在重启前未完成 | 按 phase reconcile；超 deadline 后原 operation terminal blocked/cleanup-pending，独立 cleanup fence 有界推进，新 recovery 不被永久阻塞 |
| lease expiry old owner | lease 到期后旧 owner 仍提交 phase、discard 或 complete | takeover 递增 ownerFence/claimGeneration；旧写入全部 stale_operation_fence，不能删除 adopted candidate |
| candidate CAS failure | candidate 已创建但 expected binding 已变化 | candidate 标为 orphan；独立 cleanup fence 比较 candidate identity 与 adopted/current binding，安全 discard 或进入 terminal cleanup-pending |
| launch partial failure | Job 已提交但 Session response 丢失 | 原 operation 重试返回同一映射；不创建第二个 Session/首 Turn |
| accept rejection | prepare-job 已保留 reservation，Session 明确拒绝 | Job terminal/rejected；Session/Input/Turn null+reason；tombstone 保留，不留 dangling live ID |
| submit response loss | Runner 可能已接受首 Turn | Turn 查询为 terminal 或 unknown；不隐式再次 submit |
| Compact | 可安全边界 | transcript 保留，后续 context 有边界，无 raw Runtime event 外泄 |
| Compact success boundary | Compact provider 成功且 response 可确认 | binding 与 ContextGeneration 不变；持久 ContextBoundary + operation result，后续输入沿用该 `ContextGeneration` |
| Reset | 可安全边界 | 同一 Session/IDs，空 context；绑定替换按 operation/CAS |
| Compact/Reset response loss | provider 可能已执行，Server 未确认 | operation unknown；查询/显式 retry 同一 operation，不隐式重复 |
| cancel/stop 回归 | queued/running/unknown 各一例 | Runtime stop 前后都比较完整 stop fence；不确定停止保持 unknown，不自动重投 |
| Unknown force-reset | 原 Turn unknown，无 ActiveOperation 但存在 Input/dispatch side effect | 原 target 持久为 `UnresolvedTargetRead` 并进入 `supersededTargets`/`unresolvedPrevious`；新 operation `admission=blocked`，binding/boundary 提交后才允许新 Input/Turn |
| active operation force-reset | Compact/Reset response loss 使 ActiveOperation unknown | 风险确认后原子 supersede operation 和所有 unresolved targets；旧 operation 仍 unknown，新的 context/binding 由独立 fence 建立 |
| handoff | Session safe idle，用户指定新 Runner | 只有显式 handoff operation 能改变 Runner；target、candidate、expected binding 先持久化，旧事件被拒绝 |
| rebind | Session safe idle，同一 Runner | 只替换同 Runner 的 Runtime binding；未知或跨 Runner 请求被拒绝 |

### 架构、测试与实现分批

Server 持有受理、ID、Session queue 上限、binding/operation CAS、状态投影和恢复裁判；
Launch coordinator 只推进跨 AgentJob/AgentSession 的 durable event/outbox 和幂等命令；
Runner 只执行、probe、create/get/discard、submit，并发出带完整 binding tuple 的事实；
Runtime adapter 将 SDK/文件/协议错误归类为 present、definitely-missing 或不确定失败。
CLI/Web 不解析内部事件名，也不根据时间戳或本地轮询自行拼接生命周期。

测试使用注入的 Server store、Runner registry、Runtime probe/create/get/discard/submit seam、
事件 outbox、idempotency store 和 `TimeProvider` fake。禁止真实网络、进程、Runtime SDK、
文件系统 Session、数据库或墙钟；每个场景都能固定输入事件顺序并断言持久状态、canonical
投影、revision/observedAt 和副作用调用次数。spec 测试覆盖跨组件行为，unit/architecture
测试覆盖状态机、binding/operation CAS、投影映射和依赖边界；测试时长遵循 `design/testing.md`。

建议按可独立验收的价值分六批，并与上面的 AC 一一对应：

1. **Batch 1 / AC1 稳定受理记录**：实现 AgentJob launch intent、Session 幂等受理、稳定
   Job/Session/Input/Turn 映射和 duplicate observation。
2. **Batch 2 / AC2 串行 Turn 与 follow-up**：实现 queued/running/outcome_pending/terminal/
   unknown、steer 与 new-turn 关系、Session queue 上限、accepted 后 durable enqueue failure。
3. **Batch 3 / AC3 canonical projection**：Server 提供四类分离事实、revision/observedAt
   和 next action；CLI/Web 只适配同一 read model。
4. **Batch 4 / AC4 launch coordinator**：用 AgentJob 自己的事件和 durable outbox 驱动
   Session 幂等命令、首 Turn dispatch 和部分失败/Server restart 收敛；不跨聚合共享事务。
5. **Batch 5 / AC5 deterministic recovery**：加入完整 binding epoch、same-runner
   confirmed-missing、唯一 owner/deadline/restart reconcile、candidate idempotency、
   CAS failure cleanup 与 handoff fence。
6. **Batch 6 / AC6 context boundary and Unknown**：实现 Compact/Reset operation fence、
   response-loss unknown、stop uncertain 和显式 force-reset；保留旧 transcript/unknown，
   不自动 replay。

#378 依赖既有 Agent 配置与启动契约，但不包含 #377 的 Agent 配置/启动体验，也不定义
Persona 或 target 新模型。#382 单独负责跨 Session max-concurrent-runs 的 capacity claim/release、
容量排队视图和其策略测试；#384
单独负责默认 transcript 公共投影；#385 负责历史/Session timeline 展示；#387 负责
外部 API 的认证、幂等、断线续读。#378 只提供这些边界可复用的内部 canonical 状态，
不提前设计它们的 endpoint 或 UI。

### 方案比较与选择

方案 A 是“Runtime Session 作为公共 Session”：客户端直接以 RuntimeSessionId 查询，
Runtime 丢失就创建新物理 Session 并重放旧 transcript。它实现短期恢复简单，但会让
provider ID 泄露到公共契约，Runner 重启改变逻辑身份，且重放会在 side effect 不确定时
产生重复操作；也无法稳定关联历史 Input/Turn。

方案 B 是“AgentSession 逻辑身份 + current binding + Server canonical 状态”：Input/Turn
和结果始终归 AgentSession，RuntimeSessionId 只作当前物理路由；只有权威 confirmed missing
才按 expected binding CAS 换绑，旧副作用不重放，结果不确定保留 Unknown。它需要额外的
probe 分类、恢复窗口和 CAS 测试，但能保持稳定 ID、同 Session 串行和 CLI/Web 一致，
也能在 Runner 重启时复用实际存在的 Runtime。#378 选择方案 B；其主要失败模式是 Runner
不可达、probe 不确定、候选创建失败和 CAS 冲突，均通过保留旧 binding、明确 Unknown 或
actionable recovery failure 处理，而不是猜测成功。

## Current gap

当前实现已有 SessionInput、AgentTurn、部分 activity/Unknown、launch/follow-up 与
Runtime binding 基础，但尚未让本文的目标契约成为所有入口共同的 canonical 行为：

- launch 仍需把客户端 `launchRequestId` 到 Server `launchOperationId` 的持久映射、
  Job/Session/Input/Turn reservation、Session accept/reject durable outcome、null+reason
  mapping 和 rejection/failed tombstone 收敛到同一持久流程；response loss 不能依赖客户端猜测。
- Input/Turn 的公共状态仍需统一为本文的 accepted、turn relation、dispatch status/blocked
  reason、outcome_pending、terminal 与 Unknown 语义；Job、Session、Input、Turn 的 revision、
  observedAt 和 nextAction 仍需由 Server canonical read model 一次给出。
- Runtime missing recovery 仍需补齐 ownerFence/claimGeneration、lease takeover、完整
  expected binding CAS、candidate create/get/discard/cleanup、adopted candidate 保护、重启
  reconcile 和旧 owner fail-closed；same-runner recovery 与 Runner handoff 必须分开。
- Compact、Reset、Runtime change、missing-recovery 和 force-reset 仍需统一为本文的
  ContextGeneration/ContextBoundary 规则，并让 ActiveOperation 的 operationId、kind、phase、
  outcome、owner/revision/deadline/nextAction 进入 canonical read model；Compact 不递增
  generation，其他新 logical context 递增，force-reset 后旧 Unknown 不得混入当前 activity。
- Web 与 CLI 仍需只适配 Server canonical 状态，并把 explicit force-reset 的风险确认和
  原 operation 查询/重试暴露出来；不能从本地日志、HTTP 状态或 provider event 推导结果。

这些是目标落地差距，不改变边界：#377 负责 Agent 配置与启动体验，不在本文新增配置模型；
#382 负责跨 Session max-concurrent-runs、capacity claim/release 和容量视图；#384 负责默认
transcript 公共投影；#385 负责历史/Session timeline 展示；#387 负责外部 API 的认证、幂等
和断线续读。#378 只提供这些边界可复用的生命周期与 canonical 状态合同。
