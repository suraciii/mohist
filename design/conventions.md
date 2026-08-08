# Conventions

## 身份

领域身份是能够永久、无歧义地指向一个实体的最小键。它不要求是单个随机 id；当实体
天然属于父级作用域时，父级身份与作用域内编号共同组成身份。

| 概念 | 领域身份 | 示例 |
|---|---|---|
| Project | `ProjectId` | `proj_123` |
| Issue | (`ProjectId`, `IssueNumber`) | (`proj_123`, `42`) |
| Epic | (`ProjectId`, `EpicNumber`) | (`proj_123`, `7`) |
| WorkflowRun | `WorkflowRunId` | `wr_123` |
| Runner | `RunnerId` | `runner_123` |
| AgentSession | `SessionId` | `session_123` |
| SessionOperation | `operationId` | `op_123` |
| Event | `EventId` | `evt_123` |
| Principal | `PrincipalId` | `prin_123` |
| Credential | `CredentialId` | `cred_123` |

- Issue 与 Epic 的 number 是 Project 内永久身份的一部分，不是展示别名；不再为它们
  维护第二个随机 id。
- GrainKey 必须从领域身份无损、统一地编码，并能还原为同一个强类型身份。作用域身份
  使用公共 codec，不在调用点手拼 `projectId:issueNumber` 一类字符串。
- ResourceKey 用于 HTTP 资源路径，也可以作为 CloudEvents `source`；它不作为另一套实体
  身份写入扩展属性、锁或审计字段。
- 外部名称可以解析到身份，但解析结果不得产生另一套实体身份。

## Role suffixes

| Suffix | Scope | Example |
|---|---|---|
| Querier | single-domain read projection | IssueQuerier |
| Assembler | cross-domain read assembly (AgentOps) | AgentActivityFeedAssembler |
| Reporter | cross-domain metrics (AgentOps) | AgentUsageReporter |
| Resolver | external name → canonical resource | ProjectResolver |
| Manager | config or lifecycle policy | WorkflowProfileManager |
| Store | persistence boundary for one shape | WorkflowRunStore |

- No new `*QueryService` names.
- Assembler/Reporter belong to AgentOps. Never in leaf domains like Session.

## ResourceKey

```
/projects/{projectId}
/projects/{projectId}/issues/{issueNumber}
/projects/{projectId}/epics/{epicNumber}
/workflow-runs/{workflowRunId}
```

Leading slash. Plural nouns. URL path segments. No trailing slash.

## Entity map

| Concept | Domain identity | GrainKey source | ResourceKey |
|---|---|---|---|
| Project | projectId | projectId | /projects/{projectId} |
| Issue | projectId + issueNumber | projectId + issueNumber | /projects/{projectId}/issues/{issueNumber} |
| Epic | projectId + epicNumber | projectId + epicNumber | /projects/{projectId}/epics/{epicNumber} |
| WorkflowRun | workflowRunId | workflowRunId | /workflow-runs/{workflowRunId} |
| Runner | runnerId | runnerId | /projects/{projectId}/runners/{runnerId} |
| WorkflowBacklog | — | projectId | /projects/{projectId}/workflow-backlog |
| StageLock | — | internal id | /projects/{projectId}/workflow-stage-locks/{resource} |
| AgentSession | sessionId | sessionId | /projects/{projectId}/agent-sessions/{sessionId} |
| Event | eventId | — | /events/{eventId} |

## AgentSession runtime identity

`sessionId` is Mohist's stable logical AgentSession identity. A runtime-owned physical
Session is identified separately:

Concept ownership and origin rules are defined in
[`agent-execution.md`](agent-execution.md).

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

- Use `runtimeSessionId` for the external physical identity. Never use `acpSessionId` or
  `coderSessionId` as aliases.
- `workflowRunId + sessionName` and `agentId` are origin/lookup references, not AgentSession
  identity. Workflow- and Agent-scoped routes resolve to the canonical `sessionId` resource.
- `runtime` names the execution backend. Do not add a second `kind` field.
- Current runtime binding retains `runnerId`; immutable `workDir` belongs to AgentSession. Together
  they let Session commands survive Runner process restart. A Workflow adapter rejects a request
  whose authoritative workspace differs from the AgentSession workDir; it never silently reuses
  another directory.
- A complete current binding is `(runnerId, runtime, runtimeSessionId, bindingEpoch)`. Every binding
  replacement compares that complete expected binding and the AgentSession workDir. `bindingEpoch`
  is monotonic and changes whenever current binding is replaced; it is part of every command/event
  fence, not a display-only revision.
- A binding operation carries an operation fence. `ownerFence` and `claimGeneration` are
  independent monotonic values; an unqualified `generation` in an implementation means
  `claimGeneration`, never `ContextGeneration`. The single `FenceToken` contract below is used
  by every phase write, candidate create/get/discard/cleanup, binding CAS, completion, Compact,
  and Cancel/stop. Before any external effect, Server atomically rechecks that token, the current
  owner lease, and the current binding, then passes the same token to Runtime/provider. A stale
  owner fails closed before the effect and before its result is persisted.
- Confirmed-missing recovery stays on the bound Runner and only replaces `runtimeSessionId` while
  incrementing `ContextGeneration` for the new logical context. `rebind` cannot change `runnerId`;
  Runner handoff is an explicit `handoff` operation and is not missing recovery. An adopted candidate
  is current binding and cannot be removed by an old cleanup.
- AgentSession stores only the current binding. It does not expose or persist a physical Session
  history model.
- Compact does not change `runtimeSessionId` or `ContextGeneration`; it persists a ContextBoundary
  and operation result. Reset, runtime change, confirmed missing recovery, or force-reset replaces
  the current binding while preserving `sessionId` and starts a new `ContextGeneration`. A work
  directory change requires a new logical Session identity.

### Canonical SessionInput and dispatch schema

All launch and follow-up inputs use the same input identity contract. The first launch input copies
the caller's `launchRequestId` into `requestId`; a follow-up caller must provide its own stable
`requestId`. Server never invents one after a response is lost.

```text
SessionInputRead {
  sessionId
  inputId
  requestId                 # caller-provided idempotency identity
  requestFingerprint        # canonical hash of the accepted input envelope
  state = accepted | rejected | unknown
  acceptanceReason
  turnId                    # null unless state=accepted
  turnRelation = new-turn | steer | none
  contextGeneration
  revision
  observedAt
}

TurnDispatchRead {
  inputId
  turnId
  dispatchStatus = queued | retrying | blocked | dispatched | unknown | terminal
  dispatchAttemptCount
  dispatchAttemptId         # current attempt, null when none is active
  dispatchDeadline
  blockedReason             # present for blocked or terminal/outcome=blocked only
  nextAction
}
```

The durable request map has a unique constraint on `(sessionId, requestId)` and stores the
`requestFingerprint`, `inputId`, `turnId`, `turnRelation`, acceptance state and current revision.
The Session accept transaction creates the map and the Input/Turn together. A duplicate key with
the same fingerprint returns the stored mapping; a duplicate key with a different fingerprint is
`rejected(idempotency_key_reused)` and creates nothing. A unique-key race rereads the winner.
Different request IDs are different inputs and still pass the normal `safeAdmission` and queue
capacity checks.

Dispatch states are deliberately not interchangeable:

| `dispatchStatus` | Meaning | Automatic transition |
|---|---|---|
| `queued` | accepted Turn has no active attempt, or waits for the Session's serialized predecessor | `retrying` when the durable coordinator claims an attempt |
| `retrying` | one bounded attempt is owned and its result is being obtained | `dispatched`, `blocked`, `unknown`, or terminal `blocked` |
| `blocked` | the last enqueue attempt was definitely refused temporarily; the Turn is still non-terminal | `retrying` while attempt/deadline budget remains |
| `dispatched` | Runner durably accepted this dispatch identity | Turn execution states decide the next result |
| `unknown` | the current attempt's acceptance cannot yet be confirmed | query the same `dispatchAttemptId`; never create a new attempt implicitly |
| `terminal` | the Server stopped automatic dispatch at an attempt/deadline bound | no retry; Turn is `terminal` with `outcome=blocked` |

`blocked` is therefore never a terminal return value. A handler seeing a non-terminal `blocked`
record must resume reconciliation when its durable retry signal is due; it may not return merely
because the stored value is `blocked`. At the fixed attempt/deadline bound, one atomic write sets
`dispatchStatus=terminal`, `Turn.status=terminal`, `Turn.outcome=blocked`, a stable
`blockedReason`, and an actionable `nextAction`. This is the only dispatch transition after which
automatic retry is forbidden, so an accepted Input cannot remain queued forever.

### Canonical effect fence

`BindingTuple` is either `null` or the complete tuple below. `null` is explicit and is not the same
as an omitted field.

```text
BindingTuple = {
  runnerId,
  runtime,
  runtimeSessionId,
  bindingEpoch
}

FenceToken = {
  sessionId,
  operationId,
  ownerId,
  ownerFence,
  claimGeneration,
  revision,
  expectedBinding: BindingTuple | null,
  candidateBinding: BindingTuple | null,
  leaseUntil,
  deadline,
  currentBindingForEffect: BindingTuple | null
}
```

`currentBindingForEffect` is the binding observed at the effect boundary; it is `expectedBinding`
before adoption and the adopted `candidateBinding` only for an explicitly post-CAS effect. The
following predicate is the only `fenceMatch` definition:

```text
fenceMatch(session, operationFence, token, now) =
  session.id == token.sessionId
  && operationFence.sessionId == token.sessionId
  && operationFence.operationId == token.operationId
  && operationFence.ownerId == token.ownerId
  && operationFence.ownerFence == token.ownerFence
  && operationFence.claimGeneration == token.claimGeneration
  && operationFence.revision == token.revision
  && operationFence.expectedBinding == token.expectedBinding
  && operationFence.candidateBinding == token.candidateBinding
  && operationFence.leaseUntil == token.leaseUntil
  && operationFence.deadline == token.deadline
  && operationFence.leaseUntil > now
  && now <= operationFence.deadline
  && session.revision == token.revision
  && session.currentBinding == token.currentBindingForEffect
```

The comparison is atomic. `operationFence` may be an ActiveOperation, a cleanup fence, or a
Turn stop fence, but all three use exactly this token and predicate. `recheckBeforeExternalEffect`
loads the durable fence and Session, runs `fenceMatch`, persists the in-flight attempt identity,
and returns the same token. If it fails, no effect is called. The caller must run the predicate
again before recording the result or completing the operation.

Every effect follows this shape, including `Runtime.resolve`, `Runtime.createOrGetEmpty`,
`Runtime.submitInputExactlyOnce`, cleanup `Runtime.getByKey` and `Runtime.discardCandidate`,
`recordCandidate`, binding CAS, `complete`, Compact, and Cancel/stop:

```text
effectWithFence(token, effect):
  token = Server.recheckBeforeExternalEffect(token)
  result = effect(token)
  Server.recheckBeforePersistingEffectResult(token)
  return Server.persistEffectResultIfFenceMatches(token, result)
```

An expired operation or a binding CAS change never renews the original fence. It creates an
independent bounded cleanup fence with a new `operationId`, owner, ownerFence, claimGeneration,
revision, expected/candidate binding, leaseUntil and cleanup `deadline`. Cleanup first fences
`getByKey`, then fences `discardCandidate`; if the candidate is adopted or current binding changed,
cleanup returns `already_adopted` and never discards it. If cleanup cannot decide before its own
deadline, it remains a durable `cleanup-pending` unresolved target, while the original operation
is terminal `blocked` and a new binding operation may proceed.

### Canonical SessionOperationRead

`Compact`, `Reset`, confirmed-missing recovery, `force-reset`, `handoff` and `rebind` are durable
Session operations. Their caller supplies a reusable `operationId`; Server never creates an
unqueryable operation key for a command response. This is the only authoritative
`SessionOperationRead` schema. `agent-api.md` and `agent-execution.md` link to it; they do not
define another operation field list.

```text
SessionOperationRead {
  sessionId
  operationId
  kind = compact | reset | recovery | force-reset | handoff | rebind
  phase = claimed | resolving | candidate-created | cas-pending | rebound | completed |
          superseded | expired | failed | cleanup-pending
  outcome = pending | succeeded | rejected | failed | unknown | blocked
  ownerId
  ownerFence
  claimGeneration
  leaseUntil
  revision
  deadline
  contextGeneration
  expectedBinding
  candidateBinding
  candidateKey
  candidateState = none | created | adopted | orphan | cleanup-pending | discarded | unknown
  targetRunnerId
  targetRuntime
  supersededTargets = [UnresolvedTargetRead]
  supersededByOperationId
  cleanupFence = null | FenceToken
  admission = blocked | ready
  nextAction
}

UnresolvedTargetRead {
  targetKind = operation | input | turn | dispatch | runtime-effect
  targetId
  requestId
  contextGeneration
  originalOperationId
  outcome = unknown | blocked
  expectedBinding
  nextAction
  supersededByOperationId
}
```

`expectedBinding` and `candidateBinding` are always present in a read. They are either `null` or
the complete `(runnerId, runtime, runtimeSessionId, bindingEpoch)` tuple; omitted identity is not
equivalent to `null`. `candidateKey`, `targetRunnerId` and `targetRuntime` are persisted before
any Runtime effect. `candidateState=orphan` means a candidate exists but was never adopted;
`candidateState=cleanup-pending` belongs to an independent cleanup fence, not to a live recovery
owner.

`supersededTargets` is present on every operation read (an empty array when none were superseded).
It is the durable target mapping for force-reset, including unknown Input, Turn, dispatch, Runtime
effect, and ActiveOperation records. `unresolvedPrevious` in the Session read is the same
`UnresolvedTargetRead` shape, not a second summary shape. When force-reset has no ActiveOperation,
these targets still make the supersession and old facts queryable.

The required/null rules are:

| kind | required values | explicitly null before completion |
|---|---|---|
| `compact` | `expectedBinding`, `contextGeneration`, `ownerId`, `ownerFence`, `claimGeneration`, `deadline`, `nextAction` | `candidateBinding`, `candidateKey`, `targetRunnerId`, `targetRuntime`, `supersededByOperationId` |
| `reset` | `expectedBinding`, `candidateKey`, `targetRunnerId`, `targetRuntime`, `contextGeneration` | `candidateBinding` until recorded; `supersededByOperationId` unless it supersedes an operation |
| `recovery` | `expectedBinding`, `candidateKey`, target runner/runtime equal to the expected binding, `contextGeneration` | `candidateBinding` until recorded; `supersededByOperationId` unless explicitly superseded |
| `force-reset` | `expectedBinding`, `candidateKey`, `targetRunnerId`, `targetRuntime`, `contextGeneration` and a new `operationId` | `candidateBinding` until recorded; `supersededByOperationId` when no old operation was superseded |
| `handoff` | `expectedBinding`, `candidateKey`, target runner/runtime, with target runner different from expected | `candidateBinding` until recorded; `supersededByOperationId` unless explicitly superseded |
| `rebind` | `expectedBinding`, `candidateKey`, target runner equal to expected and target runtime | `candidateBinding` until recorded; `supersededByOperationId` unless explicitly superseded |

`sessionId`, `ownerId`, `ownerFence`, `claimGeneration`, `leaseUntil`, `revision`, `deadline`,
`contextGeneration`, `supersededTargets`, `cleanupFence`, `admission` and `nextAction` are present on every
operation read. `outcome=blocked` is terminal for the
operation that can no longer make progress under its original deadline. A `cleanup-pending`
candidate may still have a separate cleanup fence; that fence never keeps a new binding operation
from being claimed after the original operation is terminal.

The launch identities are separate. The caller provides `launchRequestId`; Server creates
`launchOperationId` exactly once and durably maps `launchRequestId -> launchOperationId`. Neither
identity is a Session operation ID.

Every operation projection returns all fields above, plus the current Server `revision` and
`observedAt` used to read it. Job, Session, Input and Turn projections may reference an operation
by `operationId`, but must not invent a second operation shape. A response-loss query or retry must
return the same `operationId`, `kind`, `phase`, `outcome`, `ownerFence`, `claimGeneration`,
`revision`, `deadline`, `contextGeneration`, expected/candidate bindings, supersession link and
`nextAction`; a missing response never creates a new operation or a partial acknowledgement.

`ContextBoundary.Kind` is `compact | reset | runtime-change | missing-recovery | force-reset |
handoff | rebind`. Compact keeps `ContextGeneration`; reset, runtime change, missing recovery,
force-reset, handoff and rebind increment it after their binding/context commit. A RunnerId change
requires a durable `handoff` operation; same-Runner replacement requires `rebind` or confirmed
missing `recovery`. Reconnect, timeout, or a stale event cannot infer either operation.

## WorkflowRun metadata

```
WorkflowRun.Metadata
  ProjectId
  IssueNumber
  EpicNumber?
```

这三个值是 WorkflowRun 在本地保存的 Issue 上下文，不是 Issue 或 Epic 的第二份权威
状态。Issue 启动 WorkflowRun 时提供当前上下文；归属后来变化时，持久事件触发幂等命令
刷新 `EpicNumber`。刷新前已经产生的事件保留生产者当时持有的上下文。

不增加 lineage revision、binding 状态或通用 owner/controller 引用。跨聚合重投递时，
handler 重新读取 Issue 当前状态，再把完整上下文交给 WorkflowRun；旧事件因此不会把旧
归属重新写回。

## Dispatch namespaces

Runtime context、Workflow Variables、Project Prompts 和 Project Repository resources 具有
不同所有者和生命周期，不合并成一个 config 或 Variables document。各命名空间的解析时机
以 [`workflow/task-dispatch.md`](workflow/task-dispatch.md) 为准。
