# Design: Runner Update Interrupts and Restores Active Agent Work

References: [`proposal.md`](proposal.md) for motivation, [`specs/`](specs) for
required behavior, [`recovery.md`](recovery.md) for the failure-rule matrix and
receipt-contract sketch this design implements.

## Context

Managed `runner`/`full` updates are the primary ops path for this deployment.
Two slices are already landed:

- **Admission fence.** `RunnerGrain.BeginUpdateInterruptAsync()` drains the
  grain, the `POST /api/runner/{id}/update-interrupt` route answers with the
  Runner identity, `status=interrupted`, and the affected work-id inventory,
  and the CLI (`UpdateOperations.InterruptRunnerAsync` +
  `ManagedRuntimeTransaction`) refuses activation/restart on an unconfirmed
  interrupt (see `ManagedRunnerInterruptSpecs`, `RunnerUpdateInterruptSpecs`).
- **Returned-result retention.** `RunnerHost.executeAndTransition` persists a
  returned `WorkItemResult` through `WorkResultJournal` before its first
  report; startup reload replays completed entries with the original work
  identity and never re-executes.

What remains is the gap between those slices: the interrupt confirmation is
grain-memory only (nothing durable names the affected work), the old Runner's
abrupt stop leaves a crash-style `started` fence, and the only downstream path
is the crash path — Workflow tasks drift toward `agent-result-unconfirmed`
blocked states via settlement-deadline expiry, AgentJobs park in
non-dispatchable `Unknown`, and users see raw transport errors
(`session.abort fetch failed`) with no recovery semantics.

Key existing structures this design builds on:

## Interrupt Lease Rollback

The admission fence is a persisted, idempotent lease rather than only the
Runner grain's in-memory `_draining` flag.

1. The CLI generates an opaque interruption id and sends it with
   `POST /api/runner/{runnerId}/update-interrupt`.
2. The Runner grain persists that id as the current pending update fence before
   returning `status=interrupted`. A repeat with the same id returns the same
   fence. A different id cannot replace an active fence; its caller receives
   the existing id and therefore cannot claim it.
3. While a pending id exists, activation and every fresh poll/claim remain
   fenced. Grain activation reconstructs that state, so a Server restart does
   not silently reopen admission.
4. `POST /api/runner/{runnerId}/update-interrupt/{id}/cancel` releases
   admission only when `id` equals the current pending id. It persists the
   released state before reopening admission. Repeating the same cancellation
   is successful and does not mutate work. A delayed repeat of the matching
   `begin` is rejected as already cancelled rather than recreating the fence.
   A stale id is reported as superseded and cannot release a newer fence.
5. A successful Runner registration remains the handoff completion boundary:
   it clears the pending fence durably. A later rollback call for the old id is
   harmless and must not affect a subsequent update.

The managed and plain Runner update paths retain the exact confirmed id. On a
post-confirmation failure, exception, or cancellation they make a best-effort
cancel request with `CancellationToken.None`, after their normal managed
runtime rollback where applicable. A cancellation failure is reported beside
the original update failure; it never turns that failure into success.

This lease does not cause a Runner process to stop, synthesize a result,
redeliver an active work identity, or change Workflow/AgentJob state. It only
prevents a stranded old Runner from remaining permanently unable to claim
after an update transaction fails.

## Non-Goals

Constraints: no new external dependencies; tests deterministic (fake
processes/filesystems, injectable time); the old Runner must stop promptly —
the update never waits for long Agent turns to finish naturally.

## Goals / Non-Goals

**Goals:**

- A confirmed interrupt durably fences every affected active Agent work
  (Workflow Agent tasks and AgentJobs) as *recoverably interrupted* at
  confirmation time — never via disconnect observation or settlement timeout.
- The Runner durably records, per affected work, either the terminal result
  (landed) or an `update-interrupted` receipt with a runtime-confirmed stop,
  and retries delivery until the Server acknowledges.
- Server arbitration applies receipts at-most-once: exact duplicates are
  no-ops, mismatches/stale old-turn events are rejected, and a confirmed
  interruption creates exactly one replacement attempt (new recovery
  generation, AgentTurn, and delivery identity) with the original settlement
  frozen.
- After reconnect + reconciliation, interrupted work resumes through its
  replacement dispatch or reaches an explicit terminal state; duplicate
  delivery of an identity never double-executes.
- Interruption/recovery states are visible (sessions, turns, workflow tasks,
  web) with update context; the CLI reports per-work recovered/unresolved
  outcomes and never claims success while work is unresolved.

**Non-Goals:**

- Replaying or guessing results for work with only a `started` fence (prior
  runtime may have performed effects).
- Treating a drain, disconnect, reconnect, idle observation, or transcript
  text as a terminal Workflow result or as an interruption receipt.
- Waiting for long Agent turns to finish naturally before restart.
- Mutating the original `AgentResultSettlement` binding to point at the
  replacement execution.
- Non-update crash recovery: OOM/process loss keeps the existing unresolved
  fence unchanged.

## Decisions

### D1. The fence is a persisted Server update operation, created at confirmation

New durable entity `RunnerUpdateOperation` (stable id, Runner identity,
creation time, per-work entries: owner kind, WorkflowRun/task-attempt/work id
or AgentJob id) persisted via a new `RunnerUpdateOperationGrain` (or a storage
row behind `RunnerGrain`, whichever the existing Orleans persistence layout
favors) plus a storage-backed read model for CLI/UI queries.

Creation protocol, executed by the `/update-interrupt` route:

1. `RunnerGrain.BeginUpdateInterruptAsync()` — as today: gate admission
   (`_draining`) and snapshot active works.
2. Persist the operation record *before* responding, then synchronously
   instruct each owning grain (`WorkflowGrain` per affected run,
   `AgentJobGrain` per affected job) to mark the named work *recoverably
   interrupted*, referencing the operation id. Each owner commits the marking
   as a domain event in its own `CommitAsync` transaction (Workflow:
   interruption state on the settlement/task; AgentJob: a dedicated
   recoverably-interrupted status alongside `Unknown`).
3. Only after every marking is committed does the route return the existing
   `RunnerUpdateInterruptResponse` shape (now carrying the operation id).

Repeats are idempotent: the same Runner + pending operation returns the same
confirmation and completes any partially-marked entries instead of creating a
second operation.

*Why not disconnect-driven marking* (current crash path): the spec requires
the marking to be durable before the old process stops and independent of
disconnect observation — a prompt stop must not race a heartbeat timeout.
*Why not mark inside `RunnerGrain` state alone*: `RunnerGrain` state is
lifecycle memory; the Workflow/AgentJob owners must transition their own
domain state so reconciliation, read models, and arbitration all see the same
durable fact. *Alternative considered*: a single Saga grain orchestrating a
cross-grain transaction — rejected as new machinery; per-owner idempotent
marking with the operation record as source of truth achieves the same
observable contract with existing patterns.

### D2. One receipt journal: extend `WorkResultJournal`, not a sibling file

Add a third entry state `interrupted` to `WorkResultJournal` entries. An
`interrupted` entry carries: the frozen binding (AgentSessionId, AgentTurnId,
Runtime, RuntimeSessionId — the runner knows these from the turn coordinator /
execution envelope), `updateOperationId`, `recoveryGeneration` (0 for the
original attempt), a stable `receiptId`, and the runtime stop confirmation
fact. Exactly like `completed`, it is written atomically before first
delivery, reloaded on startup, and retired only after durable Server
acknowledgement.

A work entry therefore has exactly one terminal record — a returned result or
an interruption receipt — never both, and one ordering/ack lifecycle in one
file. *Alternative considered*: a sibling `RecoveryReceiptJournal` — rejected
because two independently-persisted files for the same work identity allow
interleavings (started in one, terminal in the other) with no atomic order;
the existing single-file `writeChain` already provides the serialization we
need. The payload contract remains the runtime-neutral
`RuntimeRecoveryReceipt` from [`recovery.md`](recovery.md) (exactly one of
`terminal-result` | `update-interrupted`; the latter carries no task outcome).

### D3. Runtime stop confirmation reuses the existing `stopConfirmed` protocol

On shutdown, `RunnerHost` performs a bounded cooperative interruption of each
in-flight turn: call the runtime adapter's stop path (Pi: `cancel`-style
`session.abort()` + `isStreaming` watch → `stopConfirmed`; OpenCode: its
lifecycle stop, confirmed via its event subscription) and wait within a fixed
budget. Outcomes map to the receipt contract:

- Turn returned a result before/after the abort signal → `terminal-result`
  receipt (D2/landed path).
- Runtime confirms the bound turn is no longer executing →
  `update-interrupted` receipt referencing the update operation.
- Stop unconfirmed, runtime unreachable, or budget expired → **no receipt**;
  the entry stays a `started` fence and the work is honestly reported
  unresolved. An idle observation or transcript text never manufactures a
  receipt.

Update-caused stop failures (e.g. `session.abort` transport failure while the
host tears down) are classified in this layer as update-interruption context,
not surfaced as raw fetch errors (see D6).

### D4. Receipt delivery rides a dedicated acknowledge-and-retry endpoint

`POST /api/runner/{id}/recovery-receipt` accepts one receipt, routes to the
owning grain (`WorkflowGrain` for workflow-owned work, `AgentJobGrain` for
jobs), and returns a durable acknowledgement carrying the applied `receiptId`.
Accepted, stale, and rejected-mismatch are all *terminal* acknowledgements for
runner retry purposes (mirroring `/report`'s accepted/stale semantics);
transport failure retains the receipt and the runner retries the exact same
`receiptId` and payload after restart via journal reload.

### D5. Arbitration and the replacement attempt live in the owning grain's transaction

`WorkflowGrain` arbitrates a receipt in the same persistence transaction as
task state:

- `terminal-result`: validate identity + binding + fingerprint against the
  recorded settlement; apply through the existing authoritative result
  settlement exactly once; then ack (retiring the runner journal entry).
- `update-interrupted`: require that the durable update operation (D1) names
  exactly this work and binding; then record the original attempt as
  interrupted history, allocate `recoveryGeneration + 1`, a new AgentTurn,
  and — through `WorkflowWorkLifecycle` — a **new work id** (delivery
  identity), making exactly one replacement dispatch eligible. Commit first,
  then ack. The original `AgentResultSettlement` stays frozen: late events or
  reports carrying the original AgentTurn/work identity are rejected as
  stale and can never settle the replacement.
- Exact duplicate of an applied receipt → same ack, no effect. Any mismatch,
  terminal task, stopped execution, or different binding → reject, settlement
  unchanged.

`AgentJobGrain` follows the same rule: a confirmed interruption moves the job
out of recoverably-interrupted into a fresh dispatch (or an explicit terminal
state if the job cannot continue), with the original work identity fenced.

Resumption then needs no new mechanism: reconciliation/poll offers the
replacement dispatch to the reconnected Runner, `WorkResultJournal.begin`
fences it like any dispatch, and the `HasUnresolvedAgentResult()`
redelivery-suppression naturally ends once the replacement settles.

### D6. Visibility is derived, projected once, and carried in DTOs

Workflow/task and session/turn domain events already drive read models; add
interruption lifecycle events at the durable transitions — *interrupting /
interrupted* at fence creation (D1 marking), *recovering* at replacement
allocation (D5), *recovered* at replacement settlement — each carrying
`updateOperationId`, work identity, recovery generation, and replacement turn
identity. Projections are keyed by (work identity, recovery generation) so
replayed events, duplicate receipts, and repeated reconciliation produce one
transition and never oscillate. Session/turn and workflow-task API DTOs gain
the interruption fields; the web session/workflow views render the state with
its update context. Stop failures for fenced work are surfaced from the
update-operation state (actionable: which update, which work, what recovery
path), never as raw transport text.

### D7. The CLI's recovery wait is bounded polling over the operation read model

After activation and restart verification, the CLI polls the Server's
update-operation recovery endpoint (per-work: receipt-acked / replacement
settled / unresolved) on a fixed interval up to a bounded deadline. When the
bound expires — or the old Runner was lost pre-receipt — outstanding work is
reported unresolved with identity and state; `UpdateOutcomeReporter` /
`RunnerRefreshOutcome` gain a per-work recovered/unresolved listing and the
exit code is non-successful while any affected work is unresolved, even when
activation and verification succeeded. Zero affected work ⇒ no recovery
claims.

## Risks / Trade-offs

- [Fence creation spans several grains; a crash mid-marking leaves a partial operation] -> The operation record is persisted first and every marking call is idempotent; retry of `/update-interrupt` completes the marking set; the confirmation (and restart authorization) is only returned once all markings are committed.
- [Runtime refuses or cannot confirm a stop within the budget] -> No receipt is written; the `started` fence stands; the update reports that work unresolved. Honest failure over a guessed receipt — this is the core safety property.
- [Old Runner still running pre-change code during first rollout] -> It sends no receipts; the fence still exists and the work is reported unresolved; the legacy `agent-result-unconfirmed` path remains the backstop. Safe degradation, no rollback needed.
- [Journal file grows with retained interrupted entries awaiting ack] -> Entries retire on acknowledgement exactly like `completed` today; bounded by the number of affected works per update.
- [Late old-turn events racing the replacement] -> Original settlement frozen; arbitration rejects any event or report carrying the original AgentTurn/work identity once a replacement exists; stale is a terminal ack.
- [Duplicate delivery across restarts double-executes] -> `WorkResultJournal` `started` fence refuses replay per identity; only a distinct replacement identity may execute; server-side suppression of already-held work ids during reconciliation.
- [Bounded CLI wait chosen too short ⇒ noisy unresolved reports] -> Bound is configurable; unresolved is an explicit, actionable state with identities, and later recovery still happens server-side regardless of CLI reporting.
- [Two runtime adapters must agree on the stop-confirmation contract] -> The receipt contract is runtime-neutral and adapters emit only it; Pi already provides `stopConfirmed`; OpenCode parity is validated by contract tests before the slice ships.

## Migration Plan

Four independently shippable slices (each lands with its deterministic tests;
see `recovery.md` delivery slices):

1. **Server fence + receipt port** — durable `RunnerUpdateOperation`, marking
   protocol, `/recovery-receipt` endpoint with validation/idempotent replay.
2. **Runner receipt journal + ack loop** — `WorkResultJournal.interrupted`
   state, shutdown-time cooperative stop with runtime confirmation, retry
   loop. (Landed returned-result behavior is the degenerate first case.)
3. **Replacement attempts** — arbitration creating new recovery generation,
   AgentTurn, and delivery identity; stale old-turn rejection; AgentJob
   resume; reconciliation offering the replacement.
4. **Reporting + visibility** — interruption states in read models/DTOs/web;
   CLI bounded recovery wait, per-work outcome, exit semantics.

Rollout: additive storage migration for the operation record and new
read-model columns; no existing states are repurposed (`Unknown` AgentJob and
`agent-result-unconfirmed` keep their meanings). Rollback: revert CLI to the
landed admission-fence behavior (update still gates on confirmed interrupt);
remaining server/runner pieces are inert without receipts. Work already marked
recoverably interrupted resolves through the legacy unresolved path after the
settlement deadline, matching pre-change behavior.

## Open Questions

- Default and maximum values for the cooperative stop budget (Runner) and the
  CLI recovery-wait bound — start with constants, make them configurable
  where ops needs vary?
- Should `AgentJob` replacement dispatch reuse the job's prompt/input as-is,
  or is a fresh input envelope required for turn-coordinator bookkeeping?
- Does the OpenCode stop confirmation need a new runtime-side surface, or is
  its existing event-subscription lifecycle sufficient to prove
  turn-not-executing?
- Where does the update-operation recovery read model live for CLI/UI polls —
  a dedicated query endpoint or an extension of the existing runner status
  API?
