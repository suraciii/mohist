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

### Canonical AgentSession, launch, and Turn result projections

`AgentSessionRead` is the only Session admission projection. The durable Session row uses the same
fields; callers use `admission=ready|blocked` and do not invent another safety field.

```text
AgentSessionRead {
  sessionId
  activity = idle | active | unknown
  currentContextActivity = idle | active | unknown
  contextGeneration
  admission = ready | blocked
  reason                    # admission reason; explicit null only when admission=ready
  nextAction                # admission action; explicit inspect_session when ready
  currentBinding: BindingTuple | null
  unresolvedPrevious = [UnresolvedTargetRead]
  unresolvedPreviousCount
  revision
  observedAt
}

AgentJobLaunchRead {
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
}

LaunchWorkspaceRead {
  projectId
  workspaceId
  path
}

TurnResultRead {
  sessionId
  inputId
  turnId
  status = queued | running | outcome_pending | terminal | unknown
  outcome = completed | failed | cancelled | blocked | null
  reason                    # the Turn result reason, distinct from dispatch blockedReason
  nextAction
  result                    # nullable; non-null only for persisted outcome=completed
  dispatch: TurnDispatchRead
  contextGeneration
  revision
  observedAt
}
```

`result` is null for every non-terminal Turn and for terminal failed/cancelled/blocked Turns. A
completed Turn may still have a null result only when the product explicitly defines a result-less
success; otherwise completion requires the persisted result. `Turn.reason` and `Turn.nextAction`
are the only result-level reason/action fields. `TurnDispatchRead.blockedReason` describes only
dispatch refusal and never replaces the Turn result fields.

`AgentJobLaunchRead.workspace` is the resolved workspace actually persisted for the launch, not a
reservation or a caller hint. A non-null workspace has all three non-empty fields and
`workspaceReason=null`; a null workspace has a non-empty `workspaceReason`. `target` is the existing
target resource key (`ResourceKey`), not a reservation or a new target model. An accepted launch must
return non-null `workspace` and `target` with null reasons. Before Session acceptance, or for a rejected
or unresolved launch, either value may be null but its corresponding reason must be non-empty. A non-null
target has `targetReason=null`; a null target has a non-empty `targetReason`. Clients never turn a
reservation ID into either live context value.

### Canonical SessionInput and dispatch schema

All launch and follow-up inputs use the same input identity contract. The first launch input copies
the caller's `launchRequestId` into `requestId`; a follow-up caller must provide its own stable
`requestId`. Server never invents one after a response is lost.

```text
SessionInputRead {
  sessionId
  inputId                   # null unless state=accepted
  requestId                 # caller-provided idempotency identity
  requestFingerprint        # canonical hash of the accepted input envelope
  state = accepted | rejected | unknown
  acceptanceReason
  turnId                    # null unless state=accepted
  turnRelation = new-turn | steer | none
  steerOperationId          # same as requestId when turnRelation=steer; null otherwise
  steerStatus = none | pending | accepted | unknown | terminal
  steerRetryAllowed         # null unless turnRelation=steer
  steerNextAction           # null unless turnRelation=steer
  contextGeneration
  revision
  observedAt
}

TurnDispatchRead {
  sessionId
  inputId
  turnId
  dispatchStatus = queued | retrying | blocked | dispatched | unknown | terminal
  dispatchOperationId       # stable operation identity for this dispatch lifecycle
  dispatchAttemptCount
  dispatchAttemptId         # current/last attempt identity; null before any attempt
  dispatchLastResult = none | accepted | definitely-rejected | unknown
  dispatchDeadline
  expectedBinding: BindingTuple | null
  candidateBinding: BindingTuple | null
  dispatchRetryId           # current durable retry work identity; null when no wake-up exists
  retryAllowed              # true only when durable repair/retry may recreate work
  dispatchRetryKind = none | outbox | command | timer
  dispatchRetryDueAt        # due time for the current durable retry signal; null when none
  dispatchRetryState = none | pending | claimed
  dispatchRetryOwnerId      # null unless dispatchRetryState=claimed
  dispatchRetryClaimGeneration # null unless dispatchRetryState=claimed
  dispatchRetryLeaseUntil   # null unless dispatchRetryState=claimed
  blockedReason             # present for blocked or terminal/outcome=blocked only
  dispatchFence: DispatchFenceToken | null
  nextAction
  revision
  observedAt
}

DispatchFenceToken = {
  sessionId,
  operationId,
  ownerId,
  ownerFence,
  claimGeneration,
  revision,
  dispatchAttemptId,
  dispatchRetryId,
  leaseUntil,
  deadline,
  expectedBinding: BindingTuple | null,
  candidateBinding: BindingTuple | null,
  bindingAtEffect: BindingTuple | null
}
```

dispatchFenceRecordMatches(session, dispatch, token) =
  token is a complete DispatchFenceToken
  && session.id == token.sessionId
  && dispatch.sessionId == token.sessionId
  && dispatch.dispatchOperationId == token.operationId
  && dispatch.dispatchAttemptId == token.dispatchAttemptId
  && dispatch.dispatchRetryId == token.dispatchRetryId
  && dispatch.dispatchFence == token

fullDispatchFenceMatches(session, dispatch, token, now) =
  dispatchFenceRecordMatches(session, dispatch, token)
  && fenceMatch(session, dispatch.dispatchFence, token, now)

Every dispatch claim, takeover, enqueue/query, reschedule, blocked write, unknown write, terminal
write, and retry-work repair uses this predicate atomically. `dispatch.dispatchFence` is the same complete
token, not a shortened owner or retry identity.

```text
claimDueOrTakeOver(record, work, previousFence, now, coordinatorId):
  atomically:
    require work.sessionId == record.sessionId
    require work.operationId == record.dispatchOperationId
    require work.dispatchRetryId == record.dispatchRetryId
    require record.dispatchRetryId != null and record.retryAllowed == true
    require record.dispatchStatus in {queued, blocked, retrying, unknown}
    require work.deadline == record.dispatchDeadline
    # Close the bounded dispatch before creating another lease. This branch does not claim work.
    if now >= record.dispatchDeadline:
      return persistDispatchDeadlineOutcomeIfFenceRecordMatches(record, previousFence, now)
    if work.dueAt > now:
      return retry_waiting
    if previousFence == null:
      require record.dispatchFence == null
    else:
      require previousFence is a complete DispatchFenceToken
      require dispatchFenceRecordMatches(session, record, previousFence)
      require previousFence.dispatchRetryId == work.dispatchRetryId
      if previousFence.leaseUntil > now:
        return retry_waiting
      require previousFence.leaseUntil <= now
    claimedWork = work with
      ownerId = coordinatorId
      ownerFence = nextOwnerFence()
      claimGeneration = nextClaimGeneration()
      leaseUntil = min(now + dispatchLease, work.deadline)
    newRevision = nextRevision()
    token = { sessionId = record.sessionId,
      operationId = record.dispatchOperationId,
      ownerId = claimedWork.ownerId, ownerFence = claimedWork.ownerFence,
      claimGeneration = claimedWork.claimGeneration, revision = newRevision,
      dispatchAttemptId = record.dispatchAttemptId,
      dispatchRetryId = record.dispatchRetryId,
      leaseUntil = claimedWork.leaseUntil, deadline = record.dispatchDeadline,
      expectedBinding = record.expectedBinding,
      candidateBinding = record.candidateBinding,
      bindingAtEffect = currentBinding }
    persist claimedWork.revision = newRevision,
      record.revision = newRevision, session.revision = newRevision,
      record.dispatchFence = token
    return { work = claimedWork, dispatchFence = token }

persistDispatchDeadlineOutcomeIfFenceRecordMatches(record, previousFence, now):
  atomically:
    require now >= record.dispatchDeadline
    if previousFence == null:
      require record.dispatchFence == null
    else:
      require previousFence is a complete DispatchFenceToken
      require dispatchFenceRecordMatches(session, record, previousFence)
      require record.dispatchFence == previousFence
    require record.dispatchStatus in {queued, blocked, retrying, unknown}
    if record.dispatchRetryId != null:
      mark DispatchRetryWork(record.dispatchRetryId) = cancelled
    newRevision = nextRevision()
    if record.dispatchAttemptId != null
       and record.dispatchLastResult == definitely-rejected:
      persist dispatchStatus = terminal, dispatchLastResult = definitely-rejected,
        retryAllowed = false, blockedReason = dispatch_retry_exhausted,
        Turn.status = terminal, Turn.outcome = blocked,
        Turn.reason = dispatch_terminal_rejected,
        Turn.nextAction = inspect_or_explicit_requeue, Turn.result = null,
        record.revision = newRevision, session.revision = newRevision,
        dispatchFence = null
      clear dispatchRetryId, dispatchRetryKind, dispatchRetryDueAt,
        dispatchRetryState, dispatchRetryOwnerId, dispatchRetryClaimGeneration,
        dispatchRetryLeaseUntil
      return terminal_blocked
    persist dispatchStatus = unknown, dispatchLastResult = unknown,
      retryAllowed = false, Turn.status = unknown, Turn.outcome = null,
      Turn.reason = dispatch_outcome_unknown,
      Turn.nextAction = query_same_dispatch_attempt_or_manual_reconcile,
      nextAction = query_same_dispatch_attempt_or_manual_reconcile,
      record.revision = newRevision, session.revision = newRevision,
      dispatchFence = null
    clear dispatchRetryId, dispatchRetryKind, dispatchRetryDueAt,
      dispatchRetryState, dispatchRetryOwnerId, dispatchRetryClaimGeneration,
      dispatchRetryLeaseUntil
    return unknown

rescheduleDispatch(record, dispatchFence, work, dueAt):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    require work.sessionId == record.sessionId
    require work.operationId == record.dispatchOperationId
    require work.dispatchRetryId == dispatchFence.dispatchRetryId
    require record.retryAllowed == true
    require dueAt <= record.dispatchDeadline
    newRevision = nextRevision()
    persist the same DispatchRetryWork with
      dueAt = dueAt, ownerId = null, ownerFence = null,
      claimGeneration = null, leaseUntil = null, revision = newRevision,
      dispatchStatus = unknown, dispatchLastResult = unknown
    persist record.dispatchStatus = unknown,
      dispatchLastResult = unknown, dispatchFence = null,
      Turn.status = unknown, Turn.outcome = null,
      Turn.reason = dispatch_outcome_unknown,
      Turn.nextAction = query_same_dispatch_attempt,
      nextAction = query_same_dispatch_attempt,
      revision = newRevision, session.revision = newRevision
    return retry_scheduled
```

```text
DispatchRetryWork {
  sessionId
  inputId
  turnId
  operationId
  dispatchStatus = queued | retrying | blocked | dispatched | unknown | terminal
  dispatchLastResult = none | accepted | definitely-rejected | unknown
  dispatchAttemptId
  dispatchRetryId
  retryAllowed
  dueAt
  attemptCount
  deadline
  ownerId
  ownerFence
  claimGeneration
  leaseUntil
  revision
  expectedBinding: BindingTuple | null
  candidateBinding: BindingTuple | null
  dispatchFence: DispatchFenceToken | null
}
```

The durable request map has a unique constraint on `(sessionId, requestId)` and stores the
`requestFingerprint`, nullable `inputId`, nullable `turnId`, `turnRelation`, acceptance state,
`acceptanceReason`, `nextAction` and current revision. The Session accept transaction creates the
map and the Input/Turn together. A duplicate key with the same fingerprint returns the stored
mapping or stored rejection tombstone; a duplicate key with a different fingerprint is
`rejected(idempotency_key_reused)` and creates nothing. A unique-key race rereads the winner.
When queue capacity is full, this design makes the rejection definitive: before returning
`rejected(queue_full)`, the Session transaction inserts a durable map tombstone with the caller's
fingerprint, `inputId=null`, `turnId=null`, `acceptanceReason=queue_full` and an actionable
`nextAction`. The same request ID and fingerprint therefore always returns the same rejection,
including after response loss; a changed payload can never pass the same key. The caller must use a
new request ID after capacity is available. Different request IDs are different inputs and still
pass the Session's canonical `admission=ready` and queue capacity checks.

For `turnRelation=steer`, the same Session transaction also creates one canonical
`SessionOperationRead(kind=steer)`, a durable effect record/outbox, and the Input mapping. Its
`operationId` is the caller's `requestId`; `inputId` and `turnId` are the other durable effect
identities. The operation is the retry record, so steer creates no second Turn, dispatch attempt,
queue entry, or `dispatchRetryId`. The Input may be returned as `state=accepted` only when that
transaction has committed the complete operation and replayable effect (`steerRetryAllowed=true`);
the Runtime has not yet been claimed by that response. A duplicate follow-up returns `accepted`
when the stored steer operation is already confirmed `outcome=succeeded`, or while a pending or
response-loss operation still has a replayable effect that can be retried with the same operation
identity. If the acceptance transaction cannot be confirmed, the result is `unknown` and no new
Input is created.

Dispatch states are deliberately not interchangeable:

| `dispatchStatus` | Meaning | Automatic transition |
|---|---|---|
| `queued` | accepted Turn has no active attempt, or waits for the Session's serialized predecessor | `retrying` when the durable coordinator claims an attempt |
| `retrying` | one bounded attempt is owned and its result is being obtained | `dispatched`, `blocked`, `unknown`, or terminal `blocked` only after the same attempt is definitely-rejected at the bound |
| `blocked` | the last enqueue attempt was definitely refused temporarily; the Turn is still non-terminal | `retrying` while attempt/deadline budget remains |
| `dispatched` | Runner durably accepted this dispatch identity | Turn execution states decide the next result |
| `unknown` | the current attempt's acceptance cannot yet be confirmed; the Turn is also `status=unknown` | query the same `dispatchAttemptId`; never create a new attempt implicitly |
| `terminal` | the Server stopped automatic dispatch permanently (normally at an attempt/deadline bound, or because a stop operation cancelled a queued Turn) | no retry; Turn is `terminal` with its persisted terminal outcome |

The only valid cross-projection combinations are:

```text
dispatchStatus=queued|blocked  -> Turn.status=queued, outcome=null
dispatchStatus=retrying        -> Turn.status=queued, outcome=null
dispatchStatus=unknown         -> Turn.status=unknown, outcome=null,
                                  Turn.reason and Turn.nextAction are non-empty
dispatchStatus=dispatched      -> Turn.status=queued|running|outcome_pending
dispatchStatus=terminal        -> Turn.status=terminal,
                                  Turn.outcome=completed|failed|cancelled|blocked
```

When a same-attempt query resolves `unknown`, `accepted-before-start` returns to
`dispatchStatus=dispatched, Turn.status=queued`; `accepted-and-started` returns to
`dispatchStatus=dispatched, Turn.status=running`; a terminal Runtime result writes the canonical
terminal Turn result; and `definitely-rejected` returns to temporary `blocked` only when a valid
retry remains. Response loss never creates a new Input, Turn, or attempt.

`blocked` is therefore never a terminal return value. It is legal only after a persisted dispatch
attempt returned `definitely-rejected`, and it must have `retryAllowed=true`, a non-null
`dispatchRetryId`, and a valid future `dispatchRetryDueAt`. A handler seeing it must resume
reconciliation when that durable signal is due; it may not return merely because the stored value is
`blocked`. At the fixed attempt/deadline bound, one atomic write may set
`dispatchStatus=terminal`, `Turn.status=terminal`, `Turn.outcome=blocked`, a stable
`blockedReason`, and an actionable `nextAction`, but only while the current attempt exists and its
persisted `dispatchLastResult=definitely-rejected`. Without an attempt, with a missing outbox, after
response loss, or with `dispatchLastResult=unknown`, the state remains `unknown` and never becomes
terminal blocked.

`dispatchRetryId` is the unique identity of the current durable retry work. The outbox command,
durable timer and coordinator claim all carry this same identity; none may derive a replacement
identity from a delivery attempt. `dispatchRetryDueAt`, `dispatchAttemptCount`, the current
`dispatchAttemptId`, fixed `dispatchDeadline`, `retryAllowed`, the full `DispatchFenceToken`, and
claim fields are persisted in the same Session transaction as every transition to `blocked`,
`retrying`, or `unknown`. Consuming the same retry signal twice is an idempotent no-op after the
first claim; an expired lease is taken over by incrementing ownerFence and claimGeneration before
another effect. `retainUnknownWithoutRetry` persists `retryAllowed=false`,
`dispatchRetryId=null`, `dispatchRetryDueAt=null`, no retry lease/owner, and the query/manual
reconcile `nextAction`. Generic repair may recreate work only when `dispatchRetryId != null`,
`retryAllowed=true`, `dispatchRetryDueAt` is valid, the state permits retry, and the full fence
matches. Reconcile after restart cannot wake an unknown-no-retry record.

The Turn result projection carries the persisted `Turn.reason` and `Turn.nextAction`; these are
distinct from `TurnDispatchRead.blockedReason`, which is only the dispatch-specific reason. No
client invents a second reason schema.

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
  dispatchAttemptId,
  dispatchRetryId,
  expectedBinding: BindingTuple | null,
  candidateBinding: BindingTuple | null,
  leaseUntil,
  deadline,
  bindingAtEffect: BindingTuple | null
}
```

`dispatchAttemptId` and `dispatchRetryId` are explicit null on non-dispatch operations.
`bindingAtEffect` is explicit: it is the expected binding for a pre-CAS effect and the candidate
binding only for an explicitly post-CAS effect. It is never inferred from the same old token. The
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
  && operationFence.dispatchAttemptId == token.dispatchAttemptId
  && operationFence.dispatchRetryId == token.dispatchRetryId
  && operationFence.expectedBinding == token.expectedBinding
  && operationFence.candidateBinding == token.candidateBinding
  && operationFence.bindingAtEffect == token.bindingAtEffect
  && operationFence.leaseUntil == token.leaseUntil
  && operationFence.deadline == token.deadline
  && operationFence.leaseUntil > now
  && now <= operationFence.deadline
  && session.revision == token.revision
  && session.currentBinding == token.bindingAtEffect
```

The comparison is atomic. `operationFence` may be an ActiveOperation, a cleanup fence, or a
Turn stop or dispatch fence, but all use exactly this token and predicate. `recheckBeforeExternalEffect`
loads the durable fence and Session, runs `fenceMatch`, persists the in-flight attempt identity,
and returns the same token. If it fails, no effect is called. The caller must run the predicate
again before recording the result or completing the operation. A stale pre-CAS or post-CAS owner
returns `stale_operation_fence`; it cannot repair its token by changing only the expected binding.

The only binding replacement protocol is this atomic Server operation:

```text
compareAndSwapBinding(preToken, boundaryKind):
  atomically:
    require preToken.bindingAtEffect == preToken.expectedBinding
    require preToken.expectedBinding != null
    require preToken.candidateBinding != null
    require fenceMatch(session, operationFence, preToken, serverNow)
    require session.currentBinding == preToken.expectedBinding
    require preToken.candidateBinding.runnerId/runtime/runtimeSessionId are complete
    candidate = preToken.candidateBinding
    require candidate.bindingEpoch == preToken.expectedBinding.bindingEpoch + 1
    session.currentBinding = candidate
    session.revision += 1
    persist ContextBoundary(boundaryKind, newContextGenerationIfRequired)
    persist operation phase = rebound and candidateState = adopted
    postToken = preToken with
      revision = session.revision
      candidateBinding = candidate
      bindingAtEffect = candidate
    persist the postToken fields in the operation row
    return { currentBinding = candidate, postFence = postToken }
```

The CAS compares session, operation, ownerFence, claimGeneration, revision, expected binding,
candidate binding, lease and deadline before changing anything. It increments the Server revision
and binding epoch in the same transaction and returns the post fence. Every later phase write,
input write, Runtime submit, result write, and completion uses that returned post token. The old
pre token is never valid after CAS, even when it has the same operationId; a takeover also makes
both old pre and old post tokens fail closed.

Every non-CAS effect follows this shape, including `Runtime.resolve`, `Runtime.createOrGetEmpty`,
`Runtime.submitInputExactlyOnce`, cleanup `Runtime.getByKey` and `Runtime.discardCandidate`,
`recordCandidate`, dispatch enqueue/query, `complete`, Compact, and Cancel/stop. Binding CAS uses
`compareAndSwapBinding` above and does not use `effectWithFence` with a pre-CAS token:

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

`Compact`, `Reset`, confirmed-missing recovery, `force-reset`, `handoff`, `rebind`, `stop` and `steer` are durable
Session operations. Their caller supplies a reusable `operationId`; Server never creates an
unqueryable operation key for a command response. This is the only authoritative
`SessionOperationRead` schema. `agent-api.md` and `agent-execution.md` link to it; they do not
define another operation field list.

```text
SessionOperationRead {
  sessionId
  operationId
  kind = compact | reset | recovery | force-reset | handoff | rebind | stop | steer
  requestFingerprint       # canonical hash of the complete command envelope
  phase = claimed | resolving | candidate-created | cas-pending | stopping | rebound |
          steer-queued | steer-dispatching | steer-accepted | completed | superseded |
          expired | failed | cleanup-pending
  outcome = pending | succeeded | rejected | failed | unknown | blocked
  reason                    # explicit null while no stable reason is known
  ownerId
  ownerFence
  claimGeneration
  leaseUntil
  revision
  expectedRevision
  expectedContextGeneration
  deadline
  contextGeneration
  expectedBinding
  candidateBinding
  bindingAtEffect
  candidateKey
  candidateState = none | created | adopted | orphan | cleanup-pending | discarded | unknown
  retryAllowed               # required for steer; false when no replay is safe
  targetRunnerId
  targetRuntime
  targetInputId              # required for kind=steer, otherwise explicit null
  targetTurnId               # required for kind=stop|steer, otherwise explicit null
  supersededTargets = [UnresolvedTargetRead]
  supersededByOperationId
  cleanupFence = null | FenceToken
  admission = blocked | ready
  nextAction
  observedAt
}

UnresolvedTargetRead {
  targetKind = operation | input | turn | dispatch-attempt | runtime-effect
  targetId
  requestId                 # required for input/turn/dispatch when known; null for operation
  contextGeneration
  originalOperationId       # operation target points to itself; null only when no origin is known
  outcome = unknown | blocked
  expectedBinding
  nextAction
  supersededByOperationId
}
```

`expectedBinding`, `candidateBinding` and `bindingAtEffect` are always present in a read. They are either `null` or
the complete `(runnerId, runtime, runtimeSessionId, bindingEpoch)` tuple; omitted identity is not
equivalent to `null`. `candidateKey`, `targetRunnerId` and `targetRuntime` are persisted before
any Runtime effect. `candidateState=orphan` means a candidate exists but was never adopted;
`candidateState=cleanup-pending` belongs to an independent cleanup fence, not to a live recovery
owner.

`kind=steer` is the durable runtime effect for one accepted steer Input. Its `operationId` is the
same caller-provided `requestId`, `targetInputId` and `targetTurnId` are fixed before dispatch, and
its complete `FenceToken` carries explicit null `dispatchAttemptId`/`dispatchRetryId` values. The
operation phases are `steer-queued` (durable replay record exists), `steer-dispatching` (one fenced
owner is invoking the adapter), `steer-accepted` (the adapter confirmed the Runtime accepted the
effect), and `completed`/`failed`/`superseded` as applicable. `outcome=succeeded` maps to
`steerStatus=accepted`; `outcome=unknown` maps to `steerStatus=unknown`; a definitive refusal or a
bounded failure maps to `steerStatus=terminal`. `retryAllowed` is persisted, and `nextAction` is
not a retry signal by itself. The OpenCode `session.promptAsync` and Pi `session.steer` calls are
adapter internals behind this effect identity; they do not define a second idempotency contract.

The steer effect result has three externally distinct meanings. `accepted` means the Input and
the durable effect are confirmed or replayable; a replayable pending effect does not claim that the
provider has already accepted the text. `unknown` means Input acceptance or Runtime acceptance cannot be confirmed; admission stays
blocked and the same operation may be queried or retried only while its complete fence and
`retryAllowed=true` remain valid. `terminal` means the effect is definitively not going to be
retried; an already accepted Input remains accepted, while the operation exposes a stable reason
and next action. A response-loss or restart reconciliation never creates a new Input, Turn,
operation, or binding. It first queries the same effect identity, then retries the same operation
only when the adapter can reconcile/replay it idempotently; otherwise it persists `unknown` with
`retryAllowed=false` and must not report `accepted`.

The canonical candidate identity is the pair `(SessionOperationRead.candidateKey,
SessionOperationRead.candidateBinding)`. A candidate is ready for adoption only when its key is the
operation's `candidateKey`, its binding is complete, and `candidateState=created`. The atomic
adopted predicate is:

```text
candidateIsCurrent(operation, candidate, session) =
  candidate.key == operation.candidateKey
  && operation.candidateBinding == candidate.binding
  && candidate.binding is complete
  && operation.candidateState in {created, adopted}
  && session.currentBinding == operation.candidateBinding
```

Cleanup stores the same candidate key and complete candidate binding in its owning operation read,
and its `cleanupFence` protects that pair. It compares the pair atomically before `getByKey` and
`discardCandidate`; `BindingTuple` has no separate adopted-candidate field. A changed current
binding or an already-current candidate therefore fails closed without deleting the candidate.

In cleanup pseudocode, `cleanupFence` means the durable cleanup operation read plus its
`cleanupFence: FenceToken`; `candidateKey`, `candidateBinding` and `candidateState` come from that
operation read, while lease, owner, revision, expected binding and `bindingAtEffect` come from the
token. Reloading a cleanup fence reloads both parts. This is an internal projection of the one
canonical `SessionOperationRead`, not a second public candidate schema.

`supersededTargets` is present on every operation read (an empty array when none were superseded).
It is the durable target mapping for force-reset, including unknown Input, Turn, dispatch, Runtime
effect, and ActiveOperation records. ActiveOperation is included in this same array as
`targetKind=operation`, `targetId=ActiveOperation.operationId`, `requestId=null`, and
`originalOperationId=targetId`; it is not a separate supersession field. `unresolvedPrevious` in the Session read is the same
`UnresolvedTargetRead` shape, not a second summary shape. When force-reset has no ActiveOperation,
these targets still make the supersession and old facts queryable.

The required/null rules are:

`targetInputId` is explicitly null unless `kind=steer`; `targetTurnId` is explicitly null unless
`kind=stop|steer`; `retryAllowed` is a boolean only for `kind=steer` and is explicitly null for
other operation kinds.

| kind | required values | explicitly null before completion |
|---|---|---|
| `compact` | `expectedBinding`, `contextGeneration`, `ownerId`, `ownerFence`, `claimGeneration`, `deadline`, `nextAction` | `candidateBinding`, `candidateKey`, `targetRunnerId`, `targetRuntime`, `supersededByOperationId` |
| `reset` | `expectedBinding`, `candidateKey`, `targetRunnerId`, `targetRuntime`, `contextGeneration` | `candidateBinding` until recorded; `supersededByOperationId` unless it supersedes an operation |
| `recovery` | `expectedBinding`, `candidateKey`, target runner/runtime equal to the expected binding, `contextGeneration` | `candidateBinding` until recorded; `supersededByOperationId` unless explicitly superseded |
| `force-reset` | `expectedBinding`, `candidateKey`, `targetRunnerId`, `targetRuntime`, `contextGeneration` and a new `operationId` | `candidateBinding` until recorded; `supersededByOperationId` when no old operation was superseded |
| `handoff` | `expectedBinding`, `candidateKey`, target runner/runtime, with target runner different from expected | `candidateBinding` until recorded; `supersededByOperationId` unless explicitly superseded |
| `rebind` | `expectedBinding`, `candidateKey`, target runner equal to expected and target runtime | `candidateBinding` until recorded; `supersededByOperationId` unless explicitly superseded |
| `stop` | `targetTurnId`, `ownerId`, `ownerFence`, `claimGeneration`, `deadline`, `nextAction`; `expectedBinding` and target runner/runtime when a binding exists | `expectedBinding`, `targetRunnerId`, `targetRuntime` when the queued Turn has no binding, `candidateBinding`, `candidateKey`, `supersededByOperationId` unless explicitly superseded |
| `steer` | `targetInputId`, `targetTurnId`, `expectedBinding`, `targetRunnerId`, `targetRuntime`, `ownerId`, `ownerFence`, `claimGeneration`, `deadline`, `nextAction`, `retryAllowed` | `candidateBinding`, `candidateKey`, `supersededByOperationId` unless explicitly superseded |

`sessionId`, `ownerId`, `ownerFence`, `claimGeneration`, `leaseUntil`, `revision`, `deadline`,
`expectedRevision`, `expectedContextGeneration`, `contextGeneration`, `supersededTargets`,
`cleanupFence`, `admission`, `reason` and `nextAction` are present on every
operation read. `reason` is explicitly nullable while an operation is pending or succeeded; a
rejected, failed, blocked or unknown operation must expose a stable non-empty reason. `outcome=blocked` is terminal for the
operation that can no longer make progress under its original deadline. A `cleanup-pending`
candidate may still have a separate cleanup fence; that fence never keeps a new binding operation
from being claimed after the original operation is terminal.

`kind=stop` uses the operation row itself as the durable Turn-stop fence. Its `targetTurnId`,
`expectedBinding`, complete owner/claim/deadline fields and `reason` are queryable; there is no
in-memory `stopFence` model. A queued target can be cancelled in the same Session transaction.
For a running target, the owner claims or takes over this operation, rechecks the complete
`FenceToken` immediately before and immediately after `Runtime.stop`, and only then persists the
Turn outcome. A lost or unknown Runtime response keeps the operation and Turn `unknown`, with an
actionable `nextAction` to query the same `operationId` or perform bounded same-operation retry;
it never creates a new stop operation or claims success.

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
