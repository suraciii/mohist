---
status: wip
---

# Agent API

Agent API 是 Mohist Agent 面向 Web、CLI 与 Agent Connection 的统一调用边界。
它保证一个 Agent 先独立可用，再以同一身份和行为出现在不同入口中。

领域对象与生命周期由 [`agent-execution.md`](agent-execution.md) 定义。本文只记录调用边界和
必须长期成立的设计决策，不规定具体传输协议、存储结构或客户端 SDK。

## 核心决策

| 决策 | 结论 | 原因 |
|---|---|---|
| Agent 是否依赖 Slack | 不依赖 | Web、CLI 或未来客户端都应能直接使用已经配置好的 Agent |
| 不同入口是否有不同执行语义 | 没有 | launch、follow-up、观察和停止必须指向同一组工作与会话对象 |
| Agent 配置由谁提供 | Mohist Agent | 客户端只提供本次任务和上下文，不能覆盖 Instructions、Runtime、Model 或 Skills |
| 工作与对话是否是同一生命周期 | 不是 | AgentJob 表示一次启动工作；AgentSession 可以在首次工作完成后继续对话 |
| 状态由谁裁定 | Mohist Server | 客户端和 adapter 只呈现状态，不从日志或 provider 事件推断结果 |
| 调用是否同步等待完成 | 否 | 接受、排队、执行和结果是不同事实；慢任务不能占住一个聊天或命令请求 |
| 重试是否可能产生重复工作 | 不应产生 | 同一意图的重试必须回到原有工作或输入 |
| 已接受输入能否为新输入让位 | 不能 | 容量不足应拒绝或排队，不能静默丢弃已经确认接受的用户委托 |

```text
Web ────────────┐
CLI ────────────┼── Agent API ── Agent / AgentJob / AgentSession ── Runner
Agent Connection┘
```

Agent API 是应用边界，不是新的领域。它组合 Agent、工作和会话能力，但不拥有另一套 Agent
配置、工作状态或 transcript。

## 调用模型

Agent API 对客户端提供六类能力：

| 能力 | 用户意图 |
|---|---|
| 发现 | 查看 Agent 身份、用途、配置完整性和当前可用性 |
| 启动 | 用一个明确任务创建新的 AgentJob 与 AgentSession |
| 观察 | 读取工作的权威状态、会话活动、回复与可恢复进度 |
| 继续 | 向现有 AgentSession 提交 follow-up，不创建新的 AgentJob |
| 控制 | 在权限允许时停止当前执行，或管理 Session 上下文 |
| 附件 | 把用户明确提供的文件作为本次输入的一部分交给 Agent |

启动一次 Agent 会建立一项工作和一段会话。首次输入及其执行属于这项 AgentJob；首次执行
结束后，AgentSession 仍可继续。follow-up 只增加会话输入和后续执行，不重开 AgentJob。

Follow-up first selects canonical `turnRelation`: a current `running` Turn with an explicitly
steer-capable current binding and no active operation uses `steer`, and the Session transaction
persists one `SessionInput` against that existing `turnId` only. It does not create a second Turn,
dispatch attempt or queue entry, and `queuedTurnCount` does not reject it. If steer is unsupported
or the current Turn is not running, the request uses `new-turn` and only that path checks the Session
queue limit and creates a Turn plus dispatch record. An `outcome_pending`, `unknown` or unresolved
operation is rejected with the original Turn's query next action; unsupported steer alone is not a
rejection when a new Turn can safely queue.

每次 follow-up 都必须由调用方提供稳定的 `requestId`。它不是 Server 生成的 InputId，也不是
可选的客户端 trace id；Server 以 `(sessionId, requestId)` 的唯一约束持久化请求到 Input/Turn
的映射。相同 key 的 response loss 或重复提交返回同一 `InputId`、`TurnId` 和 canonical 状态，
不会创建第二组记录或第二次 dispatch；相同 key 携带不同输入则返回
`rejected(idempotency_key_reused)`。只有不同 key 才代表新的 Input，并重新经过安全准入和队列
上限检查。首个 launch input 使用 `launchRequestId` 作为它的 `requestId`；launch operation
映射仍然单独保留。

如果 queue capacity 已满，Session 在返回 `rejected(queue_full)` 前持久化同一 request map 的
fingerprint tombstone（`inputId=null`、`turnId=null`、稳定 reason/nextAction）。同 requestId
同 fingerprint 的 response-loss retry 永远返回该 rejection；改 payload 永远是
`rejected(idempotency_key_reused)`，不会因容量稍后恢复而被同 key 接受。调用方只能用新
requestId 重试。

因此：

- AgentJob 完成不等于整段对话关闭，也不等于自然语言目标已经完成；
- AgentSession 不承担 Issue 或 Workflow 的业务生命周期；
- 需要持续推进和验收的工作仍应进入 Issue / Workflow；
- Web、CLI 和 Slack 必须用相同方式解释这些状态，不能各自发明“完成”。

## Session 观察

AgentSession 的稳定 `Session ID` 是跨入口观察和继续操作的唯一身份。Project 是读取边界；
调用方按 Project 和 Session ID 读取时，Workflow、直接启动和 Agent Connection 创建的会话
都使用同一套 summary 与 transcript 语义。

- view 与 transcript 不因会话来自 Agent Connection 而切换到另一套读取模型；它们必须展示同一
  个 Session 的来源、Agent 身份、当前 Runtime 与 activity、输入、Turn 和 transcript。
- 按 Agent 发现会话时，`--agent` 覆盖该 Agent 的直接启动和 Agent Connection 会话；它不是
  只列出手动启动记录的历史筛选器。
- Session ID 存在但属于另一个 Project，或携带未被支持的来源时，读取结果按“不存在”处理，
  不泄露会话事实。

## 执行定义与调用上下文

启动时，Mohist 从 Agent 解析并固定这段 Session 使用的执行定义。已有 Session 不因 Agent
后来被编辑而静默改变；新的启动使用最新配置。Agent 的并发和调度策略由 Mohist 统一执行，
任何入口都不能绕过。

调用方可以提供：

- 当前任务文本或明确附件；
- 与任务有关的 Issue、Epic、Repository 等 Mohist 上下文引用；
- 首次启动所需的、有边界的外部讨论上下文；
- 用于审计和回送结果的来源与发起者身份。

调用方不能提供：

- 替代 Agent 的 Instructions、Runtime、Model、Variant 或 Skills；
- Runner、工作目录或物理 Runtime Session 的选择；
- 伪装成系统指令的聊天平台元数据；
- 仅为了通过校验而生成、但用户没有看到的隐藏 prompt。

Subagent spawn is the one launch form whose caller is an AgentSession. Its caller Session ID and
idempotency key are explicit, while Server inherits both the authoritative workDir and current
Runner binding from that caller; clients cannot provide a substitute path or Runner. The authoritative
contract is [`subagents.md`](subagents.md).

一条输入必须包含可见文本或至少一个可用附件。只含附件的输入是有效输入；普通 URL 保留为
文本，是否访问由 Agent 已有能力决定，Agent API 不替客户端抓取任意链接。

外部讨论只在首次启动时作为背景导入。客户端必须明确它读到了哪些内容；如果完整性对本次
委托有影响而上下文无法可靠取得，客户端应拒绝启动，而不是静默提交残缺背景。

## 状态边界

API 必须把下面几类事实分开呈现：

| 事实 | 回答的问题 |
|---|---|
| Agent Readiness | Agent 的配置是否已知可执行、明确缺失，或暂时无法确认 |
| Agent Availability | 当前是否有 Runner 和容量开始执行 |
| AgentJob status | 这次启动工作是否准备、排队、执行、成功、拒绝、失败或取消 |
| Session activity | 这段会话当前是否正在处理输入 |
| Input acceptance | 用户这条输入是否已经被 Mohist 持久接受 |
| Turn result | Runtime 对这一轮输入的执行结果和派发状态是什么 |

这些事实不能折叠成一个 `Connected`、`Running` 或 `Success`。例如，Connection 可以健康但
Agent 仍需配置；Agent 可以已知可执行但暂时没有容量；Slack 回复发送失败也不能把已完成的
AgentJob 改成失败。

`Unknown` 是正式状态，不等同于 Ready 或 Failed。Mohist 无法确定输入是否已交给 Runtime 时，
应继续核对原输入，而不是复制一条新输入来“保险重试”。

`outcome_pending` 表示 Mohist 已知输入和派发路径被接受，但最终结果尚未记录；它不是成功、
失败或 idle，也不能用新输入绕过。

`cancel`/`stop` 保留现有产品意图，但它不是一个内存标志。调用方必须提供稳定的
`operationId`；Server 用唯一 canonical `SessionOperationRead` 的 `kind=stop` 持久化目标
Turn、完整 `FenceToken`、claim/takeover lease、bounded deadline、reason 和 nextAction。
queued Turn 可以在 Session 事务中取消；running Turn 必须在 `Runtime.stop` 前后重检同一
fence。响应丢失或停止结果未知时，客户端查询或以同一 operationId 做有界 retry，不能创建
第二个 stop，也不能把 unknown 当成 idle 或 cancelled。

Availability 回答现在能否开始一项新的执行；它不替代已有 AgentJob 的调度状态。Runner 或容量在
一个 Pending Job 的退避期间恢复时，Availability 可以显示可启动，而该 Job 仍显示为等待调度，
直到下一次持久化 dispatch retry 实际开始它。客户端必须呈现这两个 Server 结论，不能把等待调度
误报为 Runner 离线或容量已满。

## Canonical read model

Server 是 Job、Session、Input 和 Turn 的唯一 canonical read model。CLI、Web 和 Agent Connection
只适配这份模型，不能从本地日志、HTTP 状态、Runner 事件或 provider 响应重新推导状态。
每个模型都必须带 Server 的 `revision` 与 `observedAt`。

AgentJob 至少返回：

```text
jobId
launchRequestId
launchRequestFingerprint
launchOperationId
status = preparing | queued | running | unknown | terminal
outcome = completed | rejected | failed | cancelled | blocked | null
reason
nextAction
sessionId, sessionIdReason
inputId, inputIdReason
turnId, turnIdReason
workspace: LaunchWorkspaceRead | null
workspaceReason
target: ResourceKey | null
targetReason
revision
observedAt
```

`workspace` and `target` follow the canonical `AgentJobLaunchRead` definition in
[`conventions.md`](conventions.md#canonical-agent-session-launch-and-turn-result-projections):
the workspace is `{ projectId, workspaceId, path }`, and target is an existing `ResourceKey`.
Accepted launches return both non-null with null reasons. A pre-accept, rejected or unresolved
launch may return null values only with non-empty `workspaceReason`/`targetReason`; reservation IDs
never fill these fields.

`sessionId`、`inputId` 或 `turnId` 尚未存在时必须是 `null`，对应 reason 必须非空；reservation
ID 不能冒充 live mapping。Session accept 成功时三个 mapping 一起出现；明确拒绝或不可恢复
失败时 Job 进入 terminal `rejected`/`failed` outcome，mapping 保持 null+reason，launch
tombstone 负责后续先按 `launchRequestId` 找 operation、再按 `launchOperationId` 查询。
若首个 accepted Turn 的 dispatch 达到 attempt/deadline 上限，Job 仍保留同一 live mapping，
但其 launch outcome 收敛为 terminal `blocked`，并使用同一稳定 reason/nextAction；这不改变
Input 的 `accepted` 或 Turn 的 `outcome=blocked`。

Session activity 必须使用 conventions 的唯一 `AgentSessionRead`，同时提供
`admission=ready|blocked`、`reason`、`nextAction`、`activity`、`currentContextActivity`、
`contextGeneration`、`unresolvedPrevious`、`unresolvedPreviousCount`、`revision` 和 `observedAt`。
`unresolvedPrevious` 使用 conventions 的 `UnresolvedTargetRead`，至少包含旧 target kind/id、
旧 operationId、旧 contextGeneration、outcome、superseding operationId 和 nextAction；
当前 activity 只回答当前 logical context，旧 context 的 Unknown 不与当前 active 混合，且在
force-reset 已确认并 supersede 后不阻止当前 generation 的新 Input/Turn。

Input acceptance 与 Turn dispatch 使用 [`conventions.md`](conventions.md#canonical-sessioninput-and-dispatch-schema)
中的唯一 `SessionInputRead` 与 `TurnDispatchRead` schema。它们必须同时返回 caller-provided
`sessionId`、`requestId`、稳定 Input/Turn mapping、`dispatchAttemptCount`、`dispatchDeadline`、当前
`dispatchOperationId`、`dispatchAttemptId`、`dispatchLastResult`、`dispatchRetryId`、
`retryAllowed`、`expectedBinding`、`candidateBinding`、`dispatchRetryKind`、`dispatchRetryDueAt`、
`dispatchRetryState`、`dispatchRetryOwnerId`、`dispatchRetryClaimGeneration`、
`dispatchRetryLeaseUntil`、完整或显式 null 的 `dispatchFence`、`blockedReason`、`nextAction`、
`revision` 和 `observedAt`。`dispatchStatus=unknown` 时 `Turn.status` 也必须是 `unknown`；
`retryAllowed=false` 时 retry identity、due、owner 和 lease 全为 null。

`turnRelation=steer` additionally returns the canonical Input fields `steerOperationId` (equal to
the caller's `requestId`), `steerStatus=none|pending|accepted|unknown|terminal`,
`steerRetryAllowed`, and `steerNextAction`. The steer operation is the durable effect/outbox and
uses the full `SessionOperationRead`/`FenceToken`; it has no second Turn, dispatch attempt, or
`dispatchRetryId`. Input `state=accepted` and command result `accepted` require the operation and
replayable effect to be committed together, or an already confirmed successful operation. A response-loss or restart retry uses the same
operation identity; no duplicate follow-up may report `accepted` when the operation is not already
`outcome=succeeded` and `steerRetryAllowed=false`.

`dispatchStatus=blocked` 只表示同一次 dispatch attempt 已得到 `definitely-rejected` 且当前
仍可重试的临时阻塞，Turn 仍为 `status=queued`；客户端
不能把它当作终态，Server durable coordinator 也不能因读到这个值就返回。它必须在下一次
持久 retry signal 到期时回到 `retrying`。达到 attempt/deadline 上限后，只有存在该 operation
的 `dispatchAttemptId` 且最后结果为 `definitely-rejected` 时，Server 才能原子写入
`dispatchStatus=terminal`、`Turn.status=terminal`、`Turn.outcome=blocked`、稳定 `blockedReason` 和
可执行 `nextAction`，并取消同一 `dispatchRetryId` 的 durable retry work；此后不再自动
retry。Input 仍为 accepted，InputId/TurnId 不变。`dispatchStatus=blocked` 的写入必须与
唯一 `dispatchRetryId`、outbox/command/timer 的同一 identity、`dispatchRetryDueAt`、当前
attempt、固定 deadline 和 claim state 原子提交。Server 重启后 coordinator 扫描这些记录，
按 due signal claim；重复 signal 只消费一次，过期 lease 由新 claim generation 接管。响应
丢失只把 `dispatchStatus` 与 `Turn.status` 同步为 `unknown`，并查询原 `dispatchAttemptId`；
无 attempt、outbox 未送达或结果未知时不得写 `Turn.outcome=blocked`，不能创建新 attempt、Input 或 Turn。
enqueue、same-attempt query、claim/takeover、reschedule、`persistBlockedAndSchedule`、
`terminalizeDispatchBlocked` 和 `retainUnknownWithoutRetry` 都接收并原子校验完整
`DispatchFenceToken`；Owner A 的迟到结果在 Owner B takeover/完成后只能返回
`stale_operation_fence`，不能覆盖状态或重建 retry。

The coordinator checks `now >= dispatchDeadline` before `claimDueOrTakeOver` can create a lease.
The deadline branch atomically matches the current dispatch record fence, increments the Session and
dispatch revision, and cancels retry work: a persisted current attempt whose last result is
`definitely-rejected` goes through `terminalizeDispatchBlocked`; no attempt, missing outbox, or
`unknown` result goes through `retainUnknownWithoutRetry`, preserving the same
`dispatchAttemptId`, setting `dispatchStatus=unknown` and `Turn.status=unknown`, and exposing
`query_same_dispatch_attempt_or_manual_reconcile`. It never mints a lease ending at or after the
deadline and then relies on a failed fence check.

Turn result 使用 conventions 的唯一 `TurnResultRead`：`outcome`、`reason`、`nextAction` 和
可空 `result` 由 Server 持久返回；`TurnDispatchRead.blockedReason` 只表达派发阻塞原因，
不能替代 Turn result，也不能用客户端自造文本替代。`completed` 才能保留 Runtime result；
`failed`、`cancelled` 和 `blocked` 必须持久化 `result=null`，同时保留 `reason` 与 `nextAction`。

CLI/Web 可以把 `reason` 和 `nextAction` 翻译为适合界面的文字，但不能丢失其可行动含义或
自行合并 Job/Session/Input/Turn 状态。

Session operation 也是 canonical read model 的一部分。唯一权威 schema 在
[`conventions.md#canonical-sessionoperationread`](conventions.md#canonical-sessionoperationread)。
本文不重复它的字段列表；API 只定义客户端能看到的 projection 和查询保证。

Operation projection 必须返回 conventions 规定的完整 `SessionOperationRead`，包括其中所有
始终存在的字段和按 kind 规定的显式 null 值。
Job、Session、Input 和 Turn projection 只能引用 `operationId` 或嵌入同一 projection，不能
发明裁剪后含义不同的 operation schema。

Compact、Reset、recovery、handoff、rebind、stop 和 force-reset command 都必须由调用方提供
`operationId`；steer follow-up 使用 caller-provided `requestId` as its operationId。没有 key 时
Server 拒绝，不生成客户端不可见的替代 key。Query 可以按
`operationId` 读取当前或历史 operation；response loss 的 retry 必须重用同一个 key，并返回
同一 phase/outcome/binding/context mapping。`launchRequestId` 只用于 launch 的第一层查找，
不能当作 Session operationId。

## Launch response loss and force-reset

启动调用必须由客户端提供 `launchRequestId` 和 `launchRequestFingerprint`（完整请求 envelope
的 canonical hash）。Server 在第一次 `prepare-job` 中按
`(projectId, agentId, launchRequestId)` 查找：同 fingerprint response loss 返回原 rejection/
operation，改 payload 返回 `idempotency_key_reused`，只有新 requestId 才能继续。第一次才在
AgentJob 事务内按该 key 幂等创建唯一的 `launchOperationId`、Job 和
内部 reservation IDs，并写入唯一的 `accept-session` durable command/outbox；reservation
不是可访问的 Session、Input 或 Turn。Session 事务只提交自己的 Session、Input、Turn、
request map、dispatch record 和 durable `SessionLaunchAccepted`/`SessionLaunchRejected`
event/outbox；明确拒绝时只保留以 `launchOperationId` 为 key 的 Session-side admission tombstone，
不创建可访问 Session/Input/Turn，也不同时更新 AgentJob。协调者随后以 `launchOperationId` 幂等消费该事件，在
另一个 AgentJob 事务中 materialize 三项 live mapping 或写入 terminal rejection。
客户端无需预先知道 Server 生成的 `launchOperationId`；响应丢失时先用 `launchRequestId`
找到 operation，再查询或 retry 原 `launchOperationId`，返回同一 Job 和同一组 live mapping，
不能生成第二个 launch。

Session accept/reject 是 durable outcome。明确 reject 或不可恢复失败返回 terminal Job、
稳定 `reason` 和 `nextAction`；临时不可用或无法确认 side effect 时返回 `unknown`，并要求
查询/等待/人工核对原 operation，而不是一直创建新的 Job。若 Session accept 已成功而 Job
回写失败，Job 暂时仍返回 `sessionId/inputId/turnId=null` 与 `mapping_pending` reason；协调者
按同一 `launchOperationId` 重试回写后才公开 live mapping。reservation ID 永远不能填入这些
字段。协调者重启会扫描 pending command，重复消费、响应丢失、明确拒绝和 unknown 都只
推进同一 operation，最终收敛到一组 mapping 或一个永久 tombstone。

普通 Compact、Reset 和 recovery 被 Unknown 或 operation response loss 阻挡时，客户端先按
调用方提供的原 `operationId` 查原 operation。需要越过 Unknown 的显式产品动作是 force-reset：

```text
force-reset(
  sessionId,
  operationId = new key,
  expectedRevision,
  expectedContextGeneration,
  expectedBinding,
  confirmUnknownSideEffects = true
)
```

`BeginForceReset` 的幂等顺序固定为：

```text
BeginForceReset(sessionId, operationId, request, fingerprint):
  atomically:
    existing = Session.operation(sessionId, operationId)
    if existing != null:
      require existing.kind == force-reset
      require existing.requestFingerprint == fingerprint
      require existing.expectedRevision == request.expectedRevision
      require existing.expectedContextGeneration == request.expectedContextGeneration
      require existing.expectedBinding == request.expectedBinding
      return fullSessionOperationRead(existing)
    require request.confirmUnknownSideEffects == true
    require Session.revision == request.expectedRevision
    require Session.contextGeneration == request.expectedContextGeneration
    targets = collectUnresolvedTargets(current generation)
    require targets is non-empty and every target.outcome == unknown
    for target in targets:
      persist target.supersededByOperationId = operationId
    create ActiveOperation(
      sessionId, operationId, kind = force-reset,
      requestFingerprint = fingerprint,
      expectedRevision = request.expectedRevision,
      expectedContextGeneration = request.expectedContextGeneration,
      expectedBinding = request.expectedBinding,
      candidateBinding = null, bindingAtEffect = request.expectedBinding,
      supersededTargets = the same targets,
      ownerId, ownerFence, claimGeneration, leaseUntil, deadline,
      phase = claimed, outcome = pending),
      Session.admission = blocked,
      reason = force_reset_in_progress,
      nextAction = query_force_reset_operation
  return the stored operation
```

If the operation key, kind, target/fingerprint, expected revision, or expected generation differs,
the command is rejected (`idempotency_key_reused` or `operation_payload_mismatch`); it never creates
a second context. The later candidate CAS uses `compareAndSwapBinding(preToken)` and then uses its
returned `postFence/currentBinding=candidate` for boundary completion and every subsequent effect.

```text
require operation.candidateState == created
require operation.candidateBinding is complete
require operation.candidateBinding.runnerId == operation.targetRunnerId
require operation.candidateBinding.runtime == operation.targetRuntime
require operation.candidateBinding.bindingEpoch == operation.expectedBinding.bindingEpoch + 1
preToken = fenceToken(operation, expectedBinding = operation.expectedBinding,
                      candidateBinding = operation.candidateBinding,
                      bindingAtEffect = operation.expectedBinding)
cas = compareAndSwapBinding(preToken, boundaryKind)
postToken = cas.postFence
complete = effectWithFence(postToken, no_external_call,
  result => completeOperationIfFenceMatches(postToken, result))
```

`complete` and every result write use the post token's new revision and candidate binding; a stale
pre or post owner returns `stale_operation_fence`.

The durable candidate identity is `(operation.candidateKey, operation.candidateBinding)`. A
`createOrGetEmpty` response is classified before persistence as `ready(candidate)`,
`candidate_not_ready`, `definitely-absent`, `definitely-rejected`, `response_lost`, or `unknown`.
`response_lost` is not collapsed into `unknown`: the same fenced operation first calls
`getByKey(operation.candidateKey)` with `candidateBinding=null`. A second response loss or generic
unknown persists `outcome=unknown`, `candidateState=unknown`, `candidateBinding=null`,
`admission=blocked`, and `query_same_candidate_or_manual`; an explicit absent result leaves the
same operation pending with `retry_same_force_reset_candidate`. A ready result is accepted only
when its key is exact, its binding is complete, its runner/runtime match the persisted target, and
its `bindingEpoch` is `expectedBinding.bindingEpoch + 1`; only then is the binding persisted as
`candidateState=created` and reloaded for CAS. No unconfirmed binding is read for authorization and
no CAS is constructed from the raw response. A complete but mismatched candidate is handed to an
independent cleanup fence without CAS with reason `force_reset_candidate_identity_mismatch`; an
incomplete or unclassifiable candidate remains unknown with `operator_reconcile_candidate`.
Restart and duplicate requests repeat this same operation
lookup/reconcile path; they never create another key, candidate, context, or CAS. A discard response
loss remains on the independent cleanup fence with a revisioned `cleanup-pending` result; it never
becomes an unbounded retry or deletes a candidate after the current binding changes.

它必须使用新的 operationId，并明确确认旧 Runtime 可能仍有副作用、旧结果仍未知。Server
要求 `expectedRevision` 与 `expectedContextGeneration` 同时匹配，并在同一 Session 事务中
运行唯一 unresolved-target collector。collector 覆盖当前 generation 的 ActiveOperation
（如有）、unknown Input、unknown Turn、unknown dispatch attempt 和每个已记录的 unknown
Runtime side effect；ActiveOperation 进入同一个 `supersededTargets` 数组，不使用第二个数组。
每个 target 必须使用 conventions 的 `UnresolvedTargetRead` 字段：`targetKind`、稳定
`targetId`、`requestId`、`contextGeneration`、`originalOperationId`、完整或显式 null 的
`expectedBinding`、`nextAction` 和 `supersededByOperationId`。`targetKind=operation` 的
ActiveOperation 使用 `targetId=operationId`、`requestId=null`、`originalOperationId=targetId`；
其它 target 只有已知来源时填写 `originalOperationId`，否则为 null。collector、旧 operation
supersede 标记、新 operation 的完整 fence、`supersededTargets`、`unresolvedPrevious` 和
`admission=blocked` 必须同一事务提交；没有 ActiveOperation 也必须留下这些 durable target。

候选 binding 与新 ContextBoundary 成功提交前，当前 generation 仍不接受新的 Input/Turn；提交
后 `currentContextActivity` 只表示新 generation，旧 operation/input/turn/binding 事实仍保留
为 Unknown，并进入 `unresolvedPrevious`。响应丢失时使用同一 force-reset operationId 查询或
重试，返回同一结果，不创建第二个 context。Begin force-reset 在任何 Runtime effect 前持久化
target runner/runtime、candidateKey、完整 expected binding、空 candidate binding 和
`admission=blocked` 的 pending boundary；创建响应丢失时按 candidateKey reconcile。每个
create、recordCandidate、CAS、cleanup 和 complete 都使用 conventions 的完整 FenceToken，
effect 前后都 recheck；旧 fence 直接 fail closed。

CLI 的调用合同对应为：

```bash
mo session force-reset <session-id> \
  --operation-id <new-operation-key> \
  --expected-revision <revision> \
  --expected-context-generation <generation> \
  --expected-binding <binding> \
  --confirm-unknown-side-effects
```

CLI 必须先展示当前 Session、未决 Input/Turn、旧 operation 的 Unknown 和风险摘要；没有显式
确认、operationId、当前 revision，或 Session 已变化时，Server 拒绝调用。force-reset 完成
后 CLI/Web 只把新 context 的 activity 作为当前活动，同时显示旧未决数量和 nextAction。

`handoff` 与 `rebind` 遵循同一 command/query 合同：handoff 才能改变 Runner，rebind 只能在
同一 Runner 上替换 Runtime binding；Runner 重连、超时或旧事件不能隐式触发任一操作。两者
都必须带 `operationId`、当前 revision、expected binding 和 bounded deadline，并在当前
generation 为 `idle` 且没有未决 side effect 时受理。当前 generation 为 `active`、
`outcome_pending` 或 `unknown` 时拒绝；先完成查询或 force-reset。Server 以 candidate binding
和完整 expected binding 做 CAS，成功后记录 `ContextBoundary.Kind=handoff|rebind` 并递增
`ContextGeneration`；旧 binding 的事件按原 binding fence 拒绝。

Runtime missing 使用唯一状态机：disconnect/timeout/unavailable/非 404 先进入
`ObservationUnknown`，保留 binding；只有同一 Runner 的 probe 明确 `definitely-missing`，且
当前 generation `activity=idle`、没有 unknown Input/Turn/dispatch/runtime effect、Session
`admission=ready` 时才进入 recovery candidate/CAS。若当前 generation 为 `running`、
`outcome_pending` 或 `unknown`，或可能已有旧 side effect，进入
`RecoveryObservationOnly`，保持原 binding/Turn，Session `admission=blocked`，
`nextAction=query_runtime_or_force_reset`；只能 query/observation，不能自动 rebind。Recovery
的 create/get、CAS、post-CAS completion 都使用同一 `FenceToken`，CAS 返回的 post fence 是
后续 token，不能继续使用旧 expected token。

Response loss 的 query/retry 必须回同一 `operationId` 和 conventions 规定的完整 projection，
不能返回只有状态或只有 mapping 的部分确认。Query 不得再次递增 `ContextGeneration`、重新
创建 candidate 或生成新 operation。`outcome=blocked` 的 operation
是终态，调用方必须执行 `nextAction`；它不能被客户端轮询无限保持 pending。

### Stop operation

`mo session cancel`/`stop` 的既有产品语义保持不变：它只针对当前未终结 Turn，queued
Turn 可以取消，running Turn 请求 Runtime 停止，首个 Turn 的取消仍由 AgentJob 裁定，后续
Turn 不改写已终结的 Job。实现统一使用 `SessionOperationRead.kind=stop`，而不是内存
`stopFence`：

```text
BeginStop(sessionId, operationId, turnId, expectedRevision, expectedContextGeneration,
          expectedBinding, ownerId, deadline):
  fingerprint = canonicalFingerprint(
    sessionId, kind = stop, targetTurnId = turnId, expectedRevision,
    expectedContextGeneration, expectedBinding, deadline)
  atomically:
    require caller operationId is stable and reusable
    existing = read operationId
    if existing != null:
      require existing.kind == stop
      require existing.targetTurnId == turnId
      require existing.requestFingerprint == fingerprint
      require existing.expectedRevision == expectedRevision
      require existing.expectedContextGeneration == expectedContextGeneration
      require existing.expectedBinding == expectedBinding
      return full canonical SessionOperationRead(existing)
    require session.revision == expectedRevision
    require session.contextGeneration == expectedContextGeneration
    require current Turn == turnId and Turn is not terminal
    create SessionOperationRead(kind = stop, targetTurnId = turnId,
      requestFingerprint = fingerprint, expectedRevision,
      expectedContextGeneration = expectedContextGeneration,
      expectedBinding = expectedBinding, candidateBinding = null,
      bindingAtEffect = expectedBinding, ownerId, ownerFence, claimGeneration,
      leaseUntil, deadline, phase = claimed, outcome = pending,
      reason = null, nextAction = stop_turn)
    if Turn is queued:
      cancel its durable dispatch retry work
      persist TurnDispatch.dispatchStatus = terminal, dispatchLastResult = none,
        retryAllowed = false, dispatchRetryId = null, dispatchRetryKind = none,
        dispatchRetryDueAt = null, dispatchRetryState = none,
        dispatchRetryOwnerId = null, dispatchRetryClaimGeneration = null,
        dispatchRetryLeaseUntil = null, dispatchFence = null
      persist Turn.status = terminal, Turn.outcome = cancelled,
        Turn.reason = stop_requested, Turn.nextAction = inspect_turn
      complete the same stop operation in this Session transaction

  if Turn is running:
    claim or takeover the operation when its lease expires
    preToken = Server.recheckBeforeExternalEffect(full FenceToken(
      bindingAtEffect = expectedBinding))
    result = Runtime.stop(turnId, fenceToken = preToken)
    Server.recheckBeforePersistingEffectResult(preToken)
    persist Turn terminal/cancelled only if the same complete preToken still matches;
    a stale owner returns stale_operation_fence and cannot overwrite or recreate retry
```

`Runtime.stop` response loss is one fenced Session transaction: it keeps the original
`dispatchAttemptId`, sets `dispatchStatus=unknown`, `dispatchLastResult=unknown`,
`Turn.status=unknown`, `Turn.outcome=null`, `Turn.reason=stop_result_unknown` and a query/manual
`nextAction`, and does not create a new attempt. The durable coordinator first queries the same stop
operation and same dispatch attempt; a bounded retry may reuse that operation's provider idempotency
identity. At an unresolved deadline the operation and Turn remain unknown, never cancelled or idle.
Every response-loss retry returns the complete canonical operation projection with the same
`operationId`, target Turn, binding and attempt.

## 可靠性契约

所有客户端共享以下保证：

- 同一调用意图在超时、断线或重启后重试，仍指向原有工作或输入；
- 输入一旦被确认接受，就不会因进程重启、队列拥塞或新消息到来而消失；
- 客户端可以从已知位置恢复观察，不依赖一直在线的长连接；
- 排队和背压是可见状态，不伪装成执行失败；
- 终态和 transcript 由 Mohist 持久保存，provider 的投递状态不能覆盖它们；
- 外部平台只能得到至少一次投递时，Connection 负责去重，Agent API 不假设平台只发一次。
- launch 的 response loss、Session accept rejection 和不可恢复 failure 都先通过原
  `launchRequestId` 找到对应 `launchOperationId`，再返回 durable outcome；不会留下可执行的
  dangling reservation 或无限等待的 pending Job。

这里承诺的是“同一意图只产生一次 Mohist 领域效果”，不是网络上的 exactly-once。请求结果
无法确认时，客户端应查询或以同一身份重试，不能生成新的调用身份。

队列必须有边界，但具体容量属于运行参数，不是产品模型。达到边界后拒绝新输入并给出可操作
反馈；不能采用丢弃最旧已接受输入的策略。

## 身份与授权

Agent API 区分两类调用者：

- Mohist 操作者通过 Web 或 CLI 直接使用 Agent；
- Agent Connection 代表经过外部平台验证的成员调用一个固定 Agent。

外部成员身份不是 Mohist 管理员身份。Provider adapter 先进入受信任的 Server Connection
boundary，由它根据对应 Connection 核对 workspace、成员与访问策略，再调用 Agent API。
这条边界有调用和观察所需权限，但不能借此编辑 Agent、改变执行配置或管理其它 Project。

第一版的 Connection 凭据是 Mohist 自有服务身份，不是通用第三方 API key。Mohist 控制面
的认证与身份模型见 [`auth.md`](auth.md)；公共开发者平台与多租户授权仍为非目标，不能从
Slack adapter 的权限模型顺手扩展出来。

## 附件边界

外部平台文件在成为 Agent 输入前先进入 Mohist 管理的附件边界。这样可以在不泄露 Slack
凭据和临时下载地址的情况下，让 Web、CLI 和 Connection 使用同一种输入语义。

必须成立的规则是：

- 只处理用户明确附在当前输入或明确导入上下文中的文件；
- 文件来源、名称、类型和可用性对用户可见，读取失败不能被忽略；
- provider token、临时 URL 和原始事件 payload 不进入 Agent 配置或 transcript；
- 附件只归属于接受它的输入，不能被另一个调用方借引用复用；
- 清理、大小和保留策略由 Mohist 统一执行，而不是由每个 adapter 各自决定。

## 错误原则

错误首先帮助调用方决定下一步，而不是暴露内部异常。至少区分：

| 类别 | 调用方动作 |
|---|---|
| 输入无效 | 修改当前任务或附件后再提交 |
| 身份或访问被拒绝 | 使用正确身份，或由 Connection Owner 调整访问策略 |
| Agent 需要配置 | 在 Mohist 修复 Agent；不能靠入口覆盖配置 |
| 暂时不可用 | 保留原调用身份并等待或重试 |
| 容量已满 | 明确显示背压，稍后提交；已接受输入不受影响 |
| 状态冲突或结果未知 | 重新读取权威状态，不盲目发起新的工作 |

消息平台可以隐藏敏感配置细节，但必须给用户一个诚实、可行动的摘要。Owner 和 Mohist 操作者
可以在受控平面查看完整诊断。

## 从 Buzz 借鉴的取舍

Buzz 的实现证明聊天入口需要明确的调用者访问策略和有界队列。Mohist 采用这两个方向，但
保持自己的状态边界：

- 访问策略属于 Agent Connection，不进入 Agent 执行配置；
- adapter 不持久缓存平台事件；Server provider inbox 确定接管或拒绝，结果未知时依赖 provider
  以同一身份重投；
- Server 中的输入队列和 provider 出站 outbox 都有边界，但不能丢弃已经成为 SessionInput 的内容；
- provider conversation mapping 和投递状态属于 Server infrastructure，不是 AgentJob、
  AgentSession 或执行结果的裁判。

## 非目标

- Agent API 不解释 Slack mention、thread、成员目录或平台限流。
- Agent API 不运行 Runtime，也不读取 Runner 日志来猜工作状态。
- Agent API 不替代 Workflow、Issue 或事件路由接口。
- 第一版不承诺公共开发者平台、通用 OAuth 或跨组织租户隔离。
- 本文不固定 HTTP 路径、DTO、数据库表、租约协议或 SDK 版本。

## 实装差距与顺序

当前 Web UI 与 CLI 已有 Agent 创建、启动、查看和继续会话的基础路径，但上述跨入口契约尚未
完整成立，尤其是输入身份、执行轮次、重复请求保护、断线续读和并发调度。命名 Agent 的
执行定义已由 Agent profile 统一拥有，客户端输入不能覆盖它；Skills 随每次执行固定。

实施顺序由产品依赖决定：

1. 先让 Agent API 在 Web 与 CLI 中完整表达启动、观察、继续、停止和附件输入。
2. 再让所有直接入口使用同一状态和可靠性语义，证明 Agent 不依赖 Slack 也能工作。
3. 最后让 Slack Connection 作为普通客户端接入，不通过 shell、日志解析或隐藏配置补能力。

Slack 的身份、访问、thread 路由和投递设计见
[`slack.md`](slack.md)。
