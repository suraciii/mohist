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
  for the previous turn's terminal facts to complete outbound delivery before opening
  the Workflow AgentSession and submitting the cleanup turn.
- The outbox exposes a delivery-completion wait keyed by the Workflow session's
  scheduling identity (project, workflow run, session name), with no polling loops and
  no new server status round-trips.
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

### D1: Delivery-completion wait as an outbox primitive, keyed by scheduling identity

Add to `AgentSessionRuntimeEventOutbox` (ports + implementation):

```ts
awaitWorkflowSessionDelivery(
  target: { projectId: string; workflowRunId: string; sessionName: string },
  options: { budgetMs: number; signal: AbortSignal },
): Promise<void>
```

- **Completion condition**: no record retained in the outbox shares the session's
  scheduling identity. This is exactly `runtimeEventSchedulingKey` for both workflow
  families (`workflow-session` and `workflow-cleanup` collapse to the same
  `{family: 'workflow-session', projectId, workflowRunId, sessionName}` label), so the
  wait covers *every* retained record for the logical session — terminal close/idle
  facts, undelivered streaming deltas, and any prior cleanup boundary record — not a
  hand-picked subset. A record counts as delivery-complete when it is no longer
  retained: acknowledged and removed via its acknowledgement policy, terminally
  settled by the existing deterministic-binding-refusal settlement, or dropped by the
  existing retention cap.
- **Event-driven resolution**: the implementation keeps
  `deliveryWaiters: Map<schedulingLabel, Set<waiter>>`. Waiters are resolved at the
  points where records are removed — after the removal snapshot write commits in
  `settleDeliveryReceipts`, after deterministic-binding-refusal settlement commits, and
  after retention-cap drops / `load()`. If nothing is retained at call time, the wait
  resolves immediately (zero added delay for the already-delivered case). Registering
  a waiter kicks delivery once (`void this.kick()`), mirroring `awaitInputReceipt` —
  the kick is the normal delivery path, not a status query.
- **Budget and cancellation**: the budget uses the injected `RuntimeEventOutboxTimer`
  (fake-timer friendly) and only ever fires to *fail* the wait — it never wakes to
  re-evaluate retained state. Caller abort rejects promptly. The wait never removes,
  reorders, or mutates records.
- **Error type**: a dedicated exported error (e.g.
  `WorkflowSessionDeliveryWaitTimeoutError`) carrying the session identity and the
  exhausted budget, so callers can render structured evidence.

Why the caller-visible completion check is safe: both runtimes enqueue all of a turn's
terminal facts before the action returns (`reporter.settle()` /
`reportWithTerminalSignal`), and the cleanup attempt starts strictly after the original
action returns. So "nothing retained for the session" at wait time genuinely means
"delivered", never "not yet enqueued".

**Alternatives considered:**

- *Wait on specific terminal-fact record IDs*: the reporter's record IDs are
  internal, batching settles records collectively, and the server needs the whole
  session's retained set drained (a prior cleanup boundary record also gates
  admission). Rejected as both mechanically awkward and semantically narrower than the
  spec.
- *Poll `snapshot()` on a timer from the caller*: explicitly forbidden by the
  `runtime-event-delivery-wait` spec (no polling, no timer-driven re-evaluation).
- *Ask the server (GET session status until terminal)*: adds a new status round-trip,
  forbidden by the spec, and would re-introduce deciding admission on a projection
  rather than on delivery facts.

### D2: The wait runs at the two admission sites, only for a task's own cleanup turn

- **OpenCode** (`executor-capabilities.ts`, `buildAgentTurnCapability.turn`): when
  `cleanupAttempt` is a positive integer and the workflow identity is available
  (`work.projectId`, outbox present), await
  `awaitWorkflowSessionDelivery({projectId, workflowRunId, sessionName}, …)` **before**
  `openWorkflowAgentSession`, with `sessionName = request.session ?? work.workId` —
  the same name the open call uses (`buildCleanupWith` preserves an explicit
  `session`, otherwise both turns fall back to `workId`, so the identities match).
- **Pi** (`pi.ts`, `piAction`): after `sessionNameFromContext` resolves the name and
  `canBind` holds, before `openWorkflowAgentSession`, under the same
  positive-`cleanupAttempt` gate.

Waiting before `open` (not before the outbox enqueue inside the reporter) is what
fixes both observed failures: the OpenCode guard evaluates the post-wait projection,
and the Pi `session.cleanup` admission record is only enqueued once the original turn
is terminal server-side, so frozen-binding validation passes.

The method is declared optional on the `AgentSessionRuntimeEventOutbox` interface
(mirroring `awaitInputReceipt`) so existing test doubles keep compiling; a missing
implementation degrades to today's no-wait behavior. The real outbox implements it.

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

- The outbox rejects with `WorkflowSessionDeliveryWaitTimeoutError` (session identity +
  budget). Each admission site converts it into an action failure with a new code
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
acknowledging, and the cleanup admission validates inside the same grain — so
delivery-complete ⇒ original turn terminal ⇒ admission succeeds. `needsFreshRuntimeSession`
and open semantics are unaffected (a completed turn never sets the fresh-session flag).

## Risks / Trade-offs

- [Delivery slower than the budget turns a recoverable cleanup into a failed task] ->
  The failure carries session/work/budget evidence and the task can be retried as a
  new attempt; the budget is generous relative to the retry cadence and only the
  cleanup path waits.
- [New records for the same session enqueued during the wait extend it] -> The wait
  covers every retained record for the scheduling identity; the previous turn is
  already settled and later cleanup turns are strictly sequential, so the retained set
  is finite and the budget bounds the wait regardless.
- [Waiter resolution missed on an unhandled removal path] -> Resolution hooks are
  placed at every removal site (receipt settlement, deterministic-refusal settlement,
  retention-cap drop, `load`); a missed hook degrades to budget-timeout failure, never
  to incorrect admission.
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
   shared scheduling-label helper in `runtime-event-outbox-identity.ts`) with unit
   tests; no behavior change for existing callers.
2. Wire the admission wait into `executor-capabilities.ts` and `pi.ts` with the
   cleanup-attempt gate, skip the runner-side unsettled guard for cleanup turns, add
   the `session-delivery-wait-timeout` code to both manifests, and teach
   `runAgentCleanupAttempt` to preserve it.
3. Verify with the full runner vitest suite (fake timers for lag, budget exhaustion,
   cross-attempt fail-closed preservation, non-polling semantics).

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
