# Design: Cleanup Turn Admission Converges on Delivered Terminal Facts

## Context

When an agent-backed Workflow task turn completes with artifacts recorded but a dirty
worktree, the executor's `enforceCleanWorktree` (`packages/runner/src/runtime/worktree-enforcement.ts`)
immediately starts a bounded cleanup follow-up turn on the same Workflow AgentSession
(`buildActionHost(..., cleanupAttempt)` → agent-turn capability). Two admission
authorities reject that turn while the previous turn's terminal close/idle facts are
still in flight:

- **OpenCode path** (`packages/runner/src/runtime/executor-capabilities.ts`): after
  `openWorkflowAgentSession`, the guard `isUnsettledWorkflowSessionStatus(opened.status)`
  fails closed with `session-binding-failed` ("the previous Runtime Session has not
  reached a terminal state") whenever the server projection is still `active`/`unknown`.
- **Pi path** (`packages/runner/src/actions/pi.ts`): the runner proceeds past `open`
  (it has no status guard) and enqueues the `session.cleanup` admission boundary record;
  the server's frozen-binding validation (`AgentSessionGrain.WorkflowCleanup.cs`,
  `ResolveWorkflowCleanupBinding`) rejects with a conflict because the original Agent
  turn is not yet `Completed` server-side.

Root cause: the previous turn's terminal facts are durable in the runner's
`AgentSessionRuntimeEventOutbox` (the reporter's `settle()` awaits local persistence,
not outbound delivery), but outbound delivery is asynchronous and retried
(`drainAll` ticks, 5 s delivery timeout, 2 s network retry). The server-side
AgentSession grain serializes runtime-event processing and cleanup admission, so a
delivery acknowledgement implies the terminal projection has converged — the lag is a
normal property of the ordered outbox, not a fault. Today, admission of a task's own
cleanup turn is decided by the lagging projection instead of the authoritative frozen
execution binding (issues #602, #600).

Constraints:

- The outbox is the shared delivery primitive (one per runner host), already owning
  ordered delivery, acknowledgement policies, deterministic-binding-refusal settlement,
  and recovery. Any new wait must be observational and must not mutate records,
  ordering, batching, or acknowledgement policies.
- No server API/contract change: the `open` route, the cleanup-turn route
  (`RunnerRoutes.WorkflowCleanup.cs`), and grain validation keep their current
  fail-closed behavior.
- The cross-attempt guard (a new task attempt reusing a genuinely pending session)
  must keep failing closed exactly as today.

## Goals / Non-Goals

**Goals:**

- A task's own cleanup follow-up turn (both runtimes) waits — event-driven, bounded —
  for the immediately preceding turn's terminal facts to complete outbound delivery
  before opening the Workflow AgentSession and submitting the cleanup turn. This
  includes a preceding cleanup turn's Session-scoped `session-followup` facts.
- The outbox exposes a delivery-completion wait keyed either by the original Workflow
  turn's scheduling identity or by the preceding cleanup turn's deterministic cleanup
  operation id, with no polling loops and no new server status round-trips.
- Budget-exhausted waits fail the cleanup attempt with structured evidence (awaited
  session, work item, budget) instead of a generic unsettled-session error.
- Runner vitest coverage (fake timers) for admission under delivery lag, preserved
  cross-attempt fail-closed behavior, budget-exhaustion evidence, and non-polling
  semantics.

**Non-Goals:**

- No change to cleanup prompt semantics, the cleanup attempt budget
  (`resolveMaxCleanupAttempts`), or worktree-completion invariants.
- No change to the server's `open` status projection, cleanup admission route, or
  frozen-binding validation.
- No change to outbox acknowledgement policies, delivery ordering, batching, snapshot
  format, or retention.
- No new runner configuration surface (budget is a code constant; see Open Questions).
- No general "wait for delivery" API for non-cleanup turns; non-cleanup admission
  keeps consulting the server projection fail-closed.

## Decisions

### D1: Delivery-completion wait as an outbox primitive, keyed by the immediate predecessor

Add to `AgentSessionRuntimeEventOutbox` (ports + implementation):

```ts
awaitCleanupPredecessorDelivery(
  target: {
    projectId: string
    workflowRunId: string
    sessionName: string
    cleanupAttempt: number
    precedingCleanupOperationId: string | null
  },
  options: { budgetMs: number; signal: AbortSignal },
): Promise<void>
```

The caller passes `precedingCleanupOperationId = null` for cleanup attempt 1. For
attempt N greater than 1 it passes the deterministic operation id for attempt N minus
1 (`workflowCleanupOperationId(workflowRunId, taskRunId, workId, N - 1)`).

- **Completion condition for attempt 1**: no retained `workflow-session` record has
  the Workflow scheduling identity `{projectId, workflowRunId, sessionName}`. This
  covers the original task turn's boundary, deltas, and terminal close/idle facts.
- **Completion condition for attempt 2+**: neither the preceding
  `workflow-cleanup` boundary record with the supplied operation id nor any
  `session-followup` record whose event payload carries that `cleanupOperationId`
  remains retained. `WorkflowAgentSessionReporter.buildRecord` stamps that id on the
  cleanup runtime input and every produced cleanup fact, so this predicate crosses the
  deliberate key-family transition from Workflow target to AgentSession target without
  changing delivery keys or wire records. It covers the preceding cleanup turn's
  terminal activity even though delivery remains keyed by AgentSession id and cleanup
  turn id.
- **Settlement semantics**: a matching record is delivery-complete when it is
  acknowledged and durably removed via its acknowledgement policy, or terminally
  settled by the existing deterministic-binding-refusal path. Existing retention-cap
  behavior is unchanged and is not redefined as acknowledgement; only reconstructible
  streaming deltas are eligible for retention removal, while boundary and terminal
  convergence records remain fail-closed until settled.
- **Event-driven resolution**: the implementation keeps waiters indexed by a stable
  predecessor label (the Workflow scheduling label for attempt 1, otherwise
  `cleanup-operation:<id>`). After a durable record-removal commit, it derives the
  affected predecessor labels from the removed records and resolves a waiter only when
  a fresh in-memory predicate check finds no matching retained record. The same check
  runs after `load()` establishes recovered state. If nothing matching is retained at
  call time, the wait resolves immediately. Registering a waiter kicks delivery once
  (`void this.kick()`), mirroring `awaitInputReceipt`; the kick is the normal delivery
  path, not a status query.
- **Budget and cancellation**: the budget uses the injected `RuntimeEventOutboxTimer`
  (fake-timer friendly) and only ever fires to *fail* the wait — it never wakes to
  re-evaluate retained state. Caller abort rejects promptly. The wait never removes,
  reorders, or mutates records.
- **Error type**: a dedicated exported error (e.g.
  `CleanupPredecessorDeliveryWaitTimeoutError`) carrying the Workflow session identity,
  preceding cleanup operation id when present, cleanup attempt, and exhausted budget,
  so callers can render structured evidence.

Why an absent matching record is safe at call time: both runtimes enqueue all of a
turn's runtime input and terminal facts before the action returns (`reporter.settle()` /
`reportWithTerminalSignal`), and the next cleanup attempt starts strictly after that
return. Thus the preceding cleanup operation's records cannot appear after its wait
has already observed absence.

**Alternatives considered:**

- *Use only `runtimeEventSchedulingKey` for every attempt*: rejected because cleanup
  runtime input and terminal activity are emitted as `session-followup` records whose
  scheduling key is AgentSession/turn scoped, so a Workflow-only wait can admit attempt
  2+ while the preceding cleanup turn is still active server-side.
- *Change cleanup follow-up records back to the Workflow producer family*: rejected;
  the runner-scoped Session route deliberately makes the immutable Session turn their
  owner, and changing that wire identity would broaden this runner-only admission fix.
- *Wait on specific terminal-fact record IDs*: the reporter's fact ids are internal and
  batching settles records collectively. The cleanup operation id is already the
  authoritative correlation stamped on the entire cleanup turn, so record-id plumbing
  would be narrower and more invasive.
- *Poll `snapshot()` on a timer from the caller*: explicitly forbidden by the
  `runtime-event-delivery-wait` spec (no polling, no timer-driven re-evaluation).
- *Ask the server (GET session status until terminal)*: adds a new status round-trip,
  forbidden by the spec, and would re-introduce deciding admission on a projection
  rather than on delivery facts.

### D2: The wait runs at the two admission sites, only for a task's own cleanup turn

- **OpenCode** (`executor-capabilities.ts`, `buildAgentTurnCapability.turn`): when
  `cleanupAttempt` is a positive integer and the workflow identity is available
  (`work.projectId`, outbox present), derive the predecessor as `null` for attempt 1 or
  `workflowCleanupOperationId(work.workflowRunId, work.taskRunId, work.workId,
  cleanupAttempt - 1)` for later attempts, then await
  `awaitCleanupPredecessorDelivery(...)` **before** `openWorkflowAgentSession`, with
  `sessionName = request.session ?? work.workId` — the same name the open call uses.
  A cleanup attempt requires the existing non-empty `taskRunId` reporter prerequisite,
  so later-attempt correlation is deterministic.
- **Pi** (`pi.ts`, `piAction`): after `sessionNameFromContext` resolves the name and
  `canBind` holds, derive the same predecessor from the context work identity and wait
  before `openWorkflowAgentSession`, under the same positive-`cleanupAttempt` gate.

Waiting before `open` (not before the outbox enqueue inside the reporter) is what
fixes both observed failures: the OpenCode guard evaluates only after the immediate
predecessor has converged, and the Pi `session.cleanup` admission record is enqueued
only once that predecessor — original turn or prior cleanup turn — is terminal
server-side, so frozen-binding validation passes.

The method is declared optional on the `AgentSessionRuntimeEventOutbox` interface
(mirroring `awaitInputReceipt`) so existing test doubles keep compiling; a missing
implementation degrades to today's no-wait behavior. The real outbox implements it.
Production admission tests cover both predecessor forms through the real outbox so the
Session-scoped attempt-2+ path cannot silently collapse to the Workflow-only case.

**Alternatives considered:**

- *Wait inside `WorkflowAgentSessionReporter.awaitInput`/`awaitCleanupInput`*: too
  late for OpenCode (the guard fails at `open`, before any outbox interaction) and
  buries an admission policy inside a turn-scoped reporter.
- *Wait in `enforceCleanWorktree`*: the worktree layer lacks the runtime-specific
  session identity and would duplicate per-runtime logic.

### D3: Cleanup turns stop consulting the lagging projection; the cross-attempt guard is untouched

For a positive cleanup attempt, the runner-side unsettled-session rejection in
`executor-capabilities.ts` (`isUnsettledWorkflowSessionStatus(opened.status)` →
`session-binding-failed`) is skipped. Admission authority for a task's own cleanup turn
becomes "terminal facts delivered" (D1/D2) plus the server's own frozen-binding
validation, which is unchanged and remains fail-closed. Non-cleanup turns keep the
guard verbatim, so a new task attempt reusing a genuinely pending session still fails
closed with the existing message ("has not reached a terminal state … retry is
fail-closed").

**Alternative considered:** keep the guard for cleanup turns but only evaluate the
post-wait status. Rejected — grain serialization already guarantees the projection
converged when delivery completed, so re-checking adds a second, weaker authority and
a new failure mode (a stale read turning a deliverable cleanup into a generic
unsettled-session error, exactly what this change removes).

### D4: Budget exhaustion fails the cleanup attempt with structured, declared evidence

- The outbox rejects with `CleanupPredecessorDeliveryWaitTimeoutError` (Workflow
  session identity + cleanup attempt + preceding operation id when present + budget).
  Each admission site converts it into an action failure with a new code
  `session-delivery-wait-timeout` and a message that names the awaited Workflow
  session (`projectId/workflowRunId/sessionName`), the work item, and the exhausted
  budget — not the unsettled-session text.
- `runAgentCleanupAttempt` (`worktree-enforcement.ts`) recognizes that code and returns
  a `WorkItemResult` failure preserving the code and evidence message (plus
  `cleanupAttempts`), instead of collapsing it into the generic `worktree-dirty`
  summary. All other cleanup failures keep the existing `dirtyWorktreeFailure` wrap.
- The new code **must be added to the `mohist/opencode` and `mohist/pi` manifests** in
  `packages/runner/src/actions/built-ins.ts`: `result-validation.ts` replaces
  undeclared error codes with `UNDECLARED_RESULT_ERROR_CODE`, which would destroy the
  evidence.

**Alternative considered:** let the timeout surface through the existing
`dirtyWorktreeFailure` wrap (message-only evidence). Rejected — the spec demands the
failure not look like a generic unsettled/worktree error, and a distinct code is what
makes budget exhaustion diagnosable in task results.

### D5: Budget constant and plumbing

Default `CLEANUP_TERMINAL_FACT_DELIVERY_BUDGET_MS = 60_000`, defined beside the
admission helper. The value comfortably spans the outbox's retry cadence (2 s retry /
5 s delivery timeout, several consecutive failures) while staying trivially bounded
relative to turn deadlines (hours). It is plumbed through `ExecutorCapabilityDeps`
(and the `piAction` context) so tests can shrink it; runtime configurability via
runner variables is deferred (see Open Questions). Task success/failure remains
decided by the actual cleanup result; the wait never runs for non-cleanup turns, so
the happy path pays nothing.

### D6: Server contract untouched

No server code changes. The design relies only on an existing invariant: the
runtime-events route processes a batch inside the session grain before
acknowledging, and cleanup admission validates inside the same grain — so predecessor
delivery-complete ⇒ immediately preceding turn terminal ⇒ admission succeeds.
`needsFreshRuntimeSession` and open semantics are unaffected (a completed turn never
sets the fresh-session flag).

## Risks / Trade-offs

- [Delivery slower than the budget turns a recoverable cleanup into a failed task] ->
  The failure carries session/work/budget evidence and the task can be retried as a
  new attempt; the budget is generous relative to the retry cadence and only the
  cleanup path waits.
- [A predecessor record is enqueued after the wait observes none] -> The preceding
  action awaits reporter settlement before returning, and cleanup attempts are strictly
  sequential, so all original-turn or prior-cleanup records exist before the next wait
  starts. Tests assert this ordering for attempt 2+.
- [Waiter resolution missed on an unhandled settlement path] -> Resolution hooks are
  placed after durable receipt settlement, deterministic-refusal settlement, and
  recovered-state `load`; a missed hook degrades to budget-timeout failure, never to
  incorrect admission.
- [Outbox unhealthy (snapshot write failing) while the wait is pending] -> `kick()`
  no-ops but local-retry recovery continues; if health never returns, the budget
  expires and the cleanup fails with evidence rather than hanging the executor.
- [Skipping the runner-side guard for cleanup turns masks a genuine server-side
  inconsistency] -> The server's cleanup admission (frozen binding + terminal turn)
  remains the authoritative fail-closed check and rejects with a conflict exactly as
  before; the runner-side outbox drain is a strictly stronger precondition than the
  projection it replaces.
- [Optional interface method hides a missing implementation in a future outbox
  replacement] -> The real outbox implements it and admission tests exercise it
  through the real implementation; a missing method only degrades test doubles.

## Migration Plan

1. Land the outbox primitive (`runtime-event-outbox-ports.ts`, `runtime-event-outbox.ts`,
   predecessor-label/correlation helpers in `runtime-event-outbox-identity.ts`) with
   unit tests for both original-turn and prior-cleanup predecessor sets; no behavior
   change for existing callers.
2. Wire the admission wait into `executor-capabilities.ts` and `pi.ts` with the
   cleanup-attempt gate and deterministic prior-operation derivation, skip the
   runner-side unsettled guard for cleanup turns, add the
   `session-delivery-wait-timeout` code to both manifests, and teach
   `runAgentCleanupAttempt` to preserve it.
3. Verify with the full runner vitest suite (fake timers for first-attempt and
   attempt-2+ lag in both runtimes, budget exhaustion, cross-attempt fail-closed
   preservation, maximum-attempt accounting, actual cleanup outcomes, and non-polling
   semantics).

Deployment: runner-only release; no server, API, schema, snapshot-format, or
configuration changes — old runners and new servers (and vice versa) remain
compatible. Rollback: revert the runner; the outbox snapshot and wire contract are
unchanged, so rollback is clean with no state migration.

## Open Questions

- Should the wait budget be runtime-configurable (e.g. `runner.cleanup.terminalFactDeliveryWaitMs`,
  following `resolveStaleIndexLockMs`), or stay a code constant? Plumbing variables
  into the capability deps is the cost; no operational need has been demonstrated yet.
- Is 60 s the right default, or should it scale with observed outbox drain times
  (e.g. a multiple of `retryDelayMs × refusal threshold`)? Decide with production
  telemetry from the budget-exhaustion failures.
- Failure-code naming: `session-delivery-wait-timeout` vs. something scoped to
  cleanup admission (e.g. `cleanup-admission-wait-timeout`). The former describes the
  mechanism; the latter the caller. Lean mechanism-named since the wait is outbox-owned.
