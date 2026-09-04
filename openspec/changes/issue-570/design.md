## Context

Two production incidents (Epic 67, 2026-08-11/13) showed the failure chain this change must break: the kernel OOM-killed `mohist-runner` (peak 9.7–20.2 GB) with several long Agent runs active; after systemd's restart, affected work either terminal-failed as `runner-lost` or surfaced as a context-free `session.abort fetch failed`. Three wedges converted one resource runaway into whole-Runner loss:

- `RunnerHost.runWorkerPool` stops polling entirely while any runtime is unhealthy (`isReadyForClaim` requires the Pi runtime ready and OpenCode ready-or-cold). Presence is refreshed only by poll (`RunnerGrain.TouchPresenceAsync`; the heartbeat route calls `HeartbeatAsync`, a no-op), so a wedged runtime expires the 2-minute presence timeout and the control plane declares the Runner lost.
- A quarantined OpenCode generation waits on `generation.drained` with no deadline (`scheduleRebuild` awaits `generation.drained`), and `server-process.ts` `terminateTree` awaits `dispatcher.close()` — undici's close waits on hung in-flight requests.
- On presence loss, `RunnerGrain.CloseoutLostAsync` terminal-fails non-settlement workflow work via `FailActiveWorkAsync(workerId, "runner-lost")`.

Issue #589 (in integration) already gives Workflow Agent tasks an `AgentResultSettlement` (awaiting-result → unknown → blocked) with identity-fenced report arbitration, `ObserveAgentRunnerDisconnectedAsync`, and `HasUnresolvedAgentResult`; its design deliberately keeps runner-disconnected Agent tasks nonterminal. Issue #562 (done) gives AgentSession stop operations a durable identity and recovery reminder. This change extends that spine: recovery must be factual reconciliation against persisted state, not retry roulette.

Key existing facts the design builds on:

- Runner work identity is `ownerKind:ownerId:workId`; the process-lifetime reported set (`inFlight` / `awaitingAck` maps in `RunnerHost`) is currently **in-memory only** and dies with the process.
- The server already computes redelivery as desired (owner ledgers) minus reported (poll body) in `DispatchService.PollCoreAsync`; workflow dispatch snapshots persist in `IDispatchSnapshotStore`, AgentJob dispatches in `AgentJobLedgerRecord.DispatchJson`. Workflow work with an unresolved Agent settlement is deliberately *not* redelivered.
- The Runner already has durable `.mohist/runner-state/` stores with atomic writes (`TerminalTaskLogDeliveryStoreImpl`, runtime-event outbox, command journals) — a proven pattern for the new work journal.
- `AgentJobGrain` has a job timeout that transitions to `Unknown` (nonterminal, no synthesized verdict), durable recovery reminders, and optimistic-revision ledger fencing.
- One WorkflowRun executes at most one task or checks batch at a time.

## Goals / Non-Goals

**Goals:**

- Presence loss (timeout, unregister, abnormal restart) records a recoverable interruption with reason code, work identity, and time — never a terminal `runner-lost` failure for active work.
- Runner execution facts (work identity, dispatch payload, phase, recorded results, runtime bindings) survive process death in a durable journal; a restarted process re-declares surviving keys and reconciles factually: recorded verdict → report; surviving runtime session → re-attach; otherwise re-execute under the original identity and idempotency boundary.
- Every interruption carries a bounded recovery deadline with exactly one terminal fallback naming the interruption reason when no Runner returns.
- Late reports and observations from a previous Runner generation reconcile idempotently — accepted at most once or acknowledged stale; no untracked dead ends; no second outcome for one identity.
- Control-plane channels (poll, report retries, heartbeat, SignalR) operate independently of runtime health; claiming is gated per runtime; runtime-bound work defers with preserved identity and is never reported as a synthesized failure.
- Quarantine drain, process-tree termination, and transport close complete within bounded, injectable deadlines; wedged generations are destroyed rather than blocking replacements.
- Per-work resource envelopes (deployment-declared cgroup/systemd controls) contain a runaway work item; containment yields a factual terminal result with a resource-containment reason.
- Web, CLI (`mo run`, `mo runner`), and issue attention/inbox expose interrupted/recovering states with actionable reason codes.

**Non-Goals:**

- Redesigning the #589 settlement protocol or #562 stop-delivery protocol; this change consumes them.
- Recovering an Agent result by inference from physical state; an outcome that is unknown stays unknown (observation), never replayed.
- Bounding or redesigning workflow-level retry/rerun policy, AgentSession lifetime, or workspace cleanup.
- Cross-runner migration of live in-process execution state beyond what the persisted facts and runtime-session re-attachment support.
- Supporting rolling version compatibility (same coordinated-release model as #589).

## Decisions

### A. Closeout records a run-level interruption instead of failing work

Replace the `FailActiveWorkAsync(..., "runner-lost")` call in `RunnerGrain.CloseoutLostAsync` with a new identity-fenced grain operation `MarkWorkInterruptedAsync(workerId, reasonCode, interruptedAt, recoveryDeadline)`:

```text
WorkflowRun.WorkInterruption        # at most one, on the run aggregate
  workKey            ownerKind:ownerId:workId
  workId, taskRunId?, isChecks
  reasonCode         runner-lost | runner-unregistered
  interruptedAt
  recoveryDeadlineAt = interruptedAt + RunnerRecoveryTimeout
```

Because one run executes at most one active work, a single record on `WorkflowRun` (committed transactionally with events in the existing `SaveAsync(run, events)` boundary) is sufficient — mirroring the settlement decision in #589. The record is written for ordinary tasks and stage checks. Agent tasks with an unresolved settlement keep their existing `unknown` settlement, disconnect reason, and settlement deadline — `ObserveAgentRunnerDisconnectedAsync` already does this and the closeout keeps calling it first; the new interruption record is written only when no settlement fences the work. The dispatch snapshot is **retained** for interrupted work (unlike unknown settlements, redelivery is desired) and deleted only at terminal fallback or report acceptance. Run status stays `Running`; `HasUnresolvedAgentResult` semantics are untouched.

Reason codes are stable strings shared with status surfaces. Deliberate unregister (graceful shutdown path) uses `runner-unregistered`; both are recoverable.

**Alternative considered: reuse `AgentResultSettlement` for ordinary tasks.** Rejected — it carries Agent-specific execution bindings and its unknown state intentionally suppresses redelivery; ordinary work needs the opposite (redeliver under original identity).

**Alternative considered: terminal-fail but mark "recoverable".** Rejected — `Failed` already means a recoverable mid-state in this domain; adding a second failure vocabulary to un-fail later duplicates arbitration and breaks #589's monotonic settlement guarantees.

### B. AgentJob ledgers project an explicit recovery state

`AgentJobState` gains interruption fields (`InterruptionReasonCode`, `InterruptedAt`, `RecoveryDeadlineAt`, set only while `Status == Running`). Presence-loss closeout stamps them on every running ledger assigned to the lost Runner; status projections (`GetRuntimeSnapshotAsync`, read models, `mo run`) derive a `Recovering` state whenever the fields are present and the deadline is unexpired — never silently `Running`. The recovery deadline rides the existing `CheckTimeoutsAsync`/reminder machinery: on expiry with no Runner having accepted or reported, the job enters its terminal state with `FailureReason` naming the interruption (`runner-lost-recovery-expired`), exactly one terminal transition, no invented success.

A recovering AgentJob becomes claimable again by any eligible Runner: the ledger is re-admitted for assignment (same `JobKey`, same `WorkId`, same `DispatchJson`) without allocating a replacement work id or ledger row. Claim arbitration stays on the existing revision/claim fencing, so if the original Runner also returns, exactly one claim wins and the loser's reports are acknowledged stale.

**Alternative considered: leave `Running` and rely on the existing job timeout → `Unknown`.** Rejected — `Unknown` is nonterminal and exists for genuinely unknowable outcomes; the spec requires an explicit, user-visible recovery projection and a bounded terminal fallback.

### C. A durable Runner work journal makes reconciliation factual

Add a `WorkJournal` store in `.mohist/runner-state/`, patterned on `TerminalTaskLogDeliveryStoreImpl` (atomic text writes, serialized write chain, load-on-start, `ready()` gate). One entry per work key:

```text
WorkJournalEntry
  workKey            ownerKind:ownerId:workId
  dispatch           DispatchWorkItem   # verbatim payload
  phase              executing | awaitingAck
  result?            WorkItemResult     # recorded when produced, before first report
  binding?           { runtime, runtimeSessionId?, agentSessionId?, agentTurnId? }
  startedAt, attempts, retryAt?
```

Writes happen only on transitions — accepted (dispatch + phase executing), result produced (phase awaitingAck + result), ack (delete). Deleted entries are gone; the journal is bounded by the slot count. The existing in-memory maps remain the live state; the journal is their durable shadow.

On process start, the host loads the journal before its first poll and re-declares every surviving key in the poll report:

- `awaitingAck` entries reload directly into the map; `retryDueReports()` re-reports the recorded result under the original report key until acked.
- `executing` entries enter a recovering bucket that the reconciliation loop re-drives through `executeAndTransition` under the original key: recorded verdict facts (journal result, terminal task-log record) are reported rather than re-executed; a runtime binding whose session survived the restart (OpenCode server children are separate processes) is re-attached and the turn resumed; otherwise the work re-executes from the original dispatch under the original identity. No outcome is synthesized for an execution whose verdict is unknown — that surfaces through the existing `unknown` observation path.

This is what makes the first poll after restart a factual declaration: the server reconciles against reported facts instead of treating works as unclaimed, and duplicate execution cannot start because the in-memory skip-on-hold already ignores re-delivery of held keys.

**Alternative considered: rely on server redelivery alone (empty first poll).** Rejected — awaiting-ack results would be lost (the server cannot know a verdict that was never reported), and the spec requires the restarted process to reconcile from its own persisted facts rather than re-roll execution.

**Alternative considered: a full embedded database (SQLite) journal.** Rejected for now — the entry count is slot-bounded and the JSON-store pattern is already proven, tested, and consistent with the other runner-state stores.

### D. Redelivery, re-attachment, and at-most-once outcomes under the original identity

The wire protocol does not change shape: `ownerKind:ownerId:workId` remains the idempotency boundary and the poll report remains `inFlight`/`awaitingAck` keys. The changes are semantic:

- Interrupted workflow work stays *desired* for redelivery (`RenderActiveWorkflowAsync` keeps returning the stored snapshot while the interruption record exists), so a reconnected Runner that does not hold the work receives the original dispatch again; a Runner that still holds it (or recovered it from the journal) skips re-execution via the existing held-key check.
- `WorkflowReportService` / `WorkflowGrain` report arbitration (#589's `FindReportableTaskAttempt` / `FindReportableWork`) is extended so a report for a recoverable-interrupted work is accepted at most once and resolves the interruption; a report after the work settled or was reassigned is acknowledged `Stale`. Terminal tasks fence duplicates, so task history cannot gain a second terminal transition.
- The report path never returns an untracked dead end for preserved identities: `"missing-workflow"` and `"not-running"` are made explicit members of the stale-ack family — terminal for Runner retry purposes so the reporting Runner retires its awaiting-ack entry, with no state change on the owner. AgentJob reports keep the existing runner/work identity match plus the recovering-state eligibility.

### E. Bounded terminal fallback via persisted deadlines and reminders

Every interruption records `recoveryDeadlineAt` at closeout time (server configuration `RunnerRecoveryTimeout`, default 15 minutes — presence timeout is 2 minutes and systemd restarts land in seconds; the window must absorb slower operator intervention without stranding work for hours).

- Workflow: `WorkflowGrain` arms one `runner-recovery` reminder from the persisted absolute deadline (same pattern as the settlement reminder: idempotent ensure-on-activation, re-register on tick-before-deadline, unregister after the state commit). On expiry with no Runner having accepted or reported the work, exactly one terminal transition fires — `TaskFailed`/checks failure with reason `runner-lost-recovery-expired` (naming the original interruption) — then snapshot deletion and lock release through the normal paths. A work that recovers before the deadline never sees the fallback.
- AgentJob: the recovery deadline is checked by the existing timeout reconciliation; expiry yields the terminal state from Decision B.
- Agent tasks with unresolved settlements deliberately keep *their* settlement deadline and `blocked` fallback from #589; the presence-loss path does not add a second deadline to them.

**Alternative considered: one global scanner query for expired interruptions.** Rejected — per-owner reminders already exist for the identical deadline problem and keep the transition inside the serialized grain turn.

### F. Control-plane continuity and per-runtime claiming

Restructure the readiness gate in `RunnerHost`:

- The poll loop, heartbeat, SignalR connection, and awaiting-ack report retries run unconditionally for the life of the process. The only admission gates that remain are the local durability gates (task-log delivery store, runtime-event outbox) — a runtime failure alone never pauses polling.
- The poll request gains per-runtime readiness flags (`readyRuntimes: ["opencode", "pi"]` derived from each runtime's `ready()`/diagnostic). `DispatchService` filters claim candidates whose required runtime is not reported ready — the work is simply not claimed this poll and remains available to other Runners or later polls. This moves "runtime-specific gating" to the claim boundary where the server already arbitrates capacity.
- Work the Runner already holds whose runtime is unavailable **defers**: the key stays in the reported set (occupying its slot), identity/payload/report key preserved, no result report is emitted, and the runtime's actionable diagnostic (`server-spawn-failed` / `health-failed` / `server-exit` + recovery suggestion, already produced by `runtime.diagnostic()`) is surfaced as an execution observation — never a synthesized failure. When the runtime recovers, the deferred work executes under its original key.
- The heartbeat endpoint additionally refreshes presence (`TouchPresenceAsync`), so control-plane liveness has two independent proofs.

**Alternative considered: runner-side deferral of all claiming (poll always, defer locally).** Rejected as the primary mechanism — a Runner with a dead runtime would claim and sit on runtime-bound work, starving it from healthy Runners; server-side filtering uses the claim boundary that already exists. Local deferral remains for already-held work, which cannot be handed back without losing identity.

### G. Bounded quarantine drain and bounded teardown

- `RuntimeGeneration` gains an injectable quarantine drain deadline (`deps.quarantineDrainDeadlineMs`, driven by the existing `RuntimeClock` test seam; default 60s). Arming quarantine with active turns arms the timer; expiry resolves `generation.drained` and marks the generation force-destroyed: the shutdown path terminates the OpenCode server process tree and destroys transports (below), turns still unresolved are normalized to interrupted execution observations (reason `runtime-quarantine-destroyed`) through the existing `normalizeInterrupted` path — an observation, never a synthesized result. The replacement generation build then proceeds within the bound.
- `server-process.ts` `terminateTree` becomes bounded: graceful close raced against a timeout; on expiry the undici dispatcher is **destroyed**, not awaited. Process-tree termination escalates SIGTERM → bounded grace → SIGKILL using the escalation pattern already in `system/process.ts`; where the SDK exposes the server pid it is force-killed directly. All runtime shutdown paths (OpenCode, Pi, runner shutdown) route through these bounded helpers so a hung process or transport can never block a replacement generation or Runner shutdown.

**Alternative considered: rely on the per-work resource envelope (Decision I) to kill wedged servers.** Complementary, not sufficient — the cgroup reclaims memory runaways but not logically hung (non-allocating) turns and requests; the deadline is the correctness bound, the envelope is the blast-radius bound.

### H. Resource isolation is declared by deployment, enforced by the kernel

Production Linux deployments place the Runner under a systemd slice hierarchy that is the authoritative source of envelopes:

```text
mohist-runner.service        MemoryMax=<runner ceiling>     # control plane
mohist-runtime.slice         MemoryMax=<runtime ceiling>    # shared OpenCode/Pi server processes
mohist-work.slice            # per-work transient scopes
  mohist-work-<workKey>.scope MemoryMax=<work ceiling>, TasksMax=...
```

- Action/child process trees for a work item launch under a per-work transient scope (`systemd-run --scope`, unit named from the sanitized work key) via a `WorkExecutionLauncher` seam; the shared runtime servers (which host multiple sessions' work and must not die with one work item) run under the runtime slice. The Runner service itself sits outside every per-work envelope.
- Kernel reclamation of a work scope kills only that work's process tree; the Runner observes the child death, escalates containment (bounded kill of any survivors), frees the slot, and reports a terminal result with reason `resource-contained` — a factual verdict, not `runner-lost` and not a silent loss. Sibling work and the control plane continue.
- The Runner control plane's own footprint stays bounded independently of work payloads: the reported set is slot-bounded, task-log buffers and outboxes are file-backed with the existing caps, and the journal (Decision C) is slot-bounded.
- Non-systemd platforms (development, containers without systemd) use the launcher's in-process fallback: isolation is advisory there and the bounded deadlines from Decision G remain the correctness guarantee. The unit files ship in the repository deployment assets so production isolation is declared, not implied.

**Alternative considered: in-process resource policing (RSS watchdog, Promise concurrency caps).** Rejected as the enforcement mechanism — the incidents showed the Node process itself is the runaway; only an external control can reclaim it without taking the control plane with it. A soft RSS watchdog may be added later as telemetry.

### I. User-visible recovery status on every surface

- Server wire: task/status views gain an `interruption` projection `{ state: interrupted | recovering, reasonCode, interruptedAt, recoveryDeadlineAt, nextAction }` derived from the run's interruption record and the AgentJob ledger fields; run status stays `Running` while nonterminal. `WorkflowStatusMapper` derives presentation the same way it derives `blocked`.
- Web workflow/run views render the interrupted task with its reason and deadline; CLI `mo run` / `mo runner` render `recovering` for AgentJobs and `interrupted` for workflow work with reason codes, distinguishable from failed and from blocked unknown settlements; issue attention/inbox projects the interruption with its actionable reason. No surface may render these states as `runner-lost` failure or a context-free `session.abort fetch failed`.
- **Breaking**: `runner-lost` disappears as a terminal failure for active work; consumers keyed on it must switch to the interruption/reason vocabulary. `resource-contained` and `runtime-unavailable`-style deferral diagnostics join the stable reason-code list.

### J. Testability

Server tests use injected `TimeProvider` and Orleans reminder entry points for deadline paths (no wall clock). Runner tests use the existing `RuntimeClock` seam for the quarantine deadline, the file-system seam for the journal, a fake `WorkExecutionLauncher`, and `vi.useFakeTimers` per project convention. The recovery scenarios (OOM kill, reconnect with held work, late reports, deadline expiry) are driven as process-restart tests against the host with fakes, in the style of `execution-envelope.startup.test.ts`.

## Risks / Trade-offs

- [Ordinary tasks re-execute after a restart when no verdict was durably recorded, repeating side effects] -> The journal records execution start and any produced verdict; verdict-backed work is never re-executed. For verdict-less work the spec accepts re-execution under the original identity (one outcome per identity); externally side-effecting actions rely on existing workflow idempotency. The alternative — always fail verdict-less work — recreates the `runner-lost` data loss this change exists to fix.
- [A recovering AgentJob claimed by a second Runner while the original also returns] -> Ledger claim/revision fencing admits exactly one claim; the loser's reports are acknowledged stale; outcomes stay at-most-once per identity.
- [Deferred work occupies a slot on a Runner whose runtime never recovers] -> Runtime rebuild is bounded (Decision G), so deferral is bounded; per-runtime claim filtering stops *new* runtime-bound claims; the diagnostic is user-visible; owner stop remains the escape hatch.
- [Journal write amplification on hot paths] -> Writes occur only on the three transitions (accept / result / ack), atomic and slot-bounded; measurably cheaper than the existing terminal task-log store that already writes per work.
- [Extended nonterminal window delays operator feedback compared to today's instant `runner-lost`] -> The interruption is immediately visible with reason and deadline on every surface; the deadline bounds the window and ends in a named terminal state.
- [New wire fields (`readyRuntimes`, interruption projections) meet older peers] -> Fields are additive on JSON bodies the deserializer already tolerates; still, the release is coordinated (Server → Runner → Web/CLI) per the project's no-rolling-compat policy.
- [Kernel containment of a work scope surfaces as an opaque child death] -> The launcher maps scope-termination back to the work key; the terminal result carries `resource-contained` with the envelope facts; mis-ceilinged deployments are diagnosable from the reason.
- [Isolation is advisory on non-systemd platforms] -> Accepted: production deployments are systemd-declared; correctness bounds (deadlines) do not depend on the envelope. Documented in deployment assets.
- [Two independent deadlines (settlement vs recovery) could race on one task] -> A task has at most one fence: settlement tasks never get an interruption record, ordinary tasks never have settlements; enforced in the closeout branch, pinned by tests.

## Migration Plan

1. **Server, workflow side**: interruption record on `WorkflowRun` + `MarkWorkInterruptedAsync`, snapshot retention for interrupted work, `runner-recovery` reminder and terminal fallback, deadline configuration. Domain tests for closeout, recovery-before-deadline, expiry, and idempotent re-closeout.
2. **Server, AgentJob side**: ledger interruption fields, `Recovering` projection, re-admission of recovering jobs under the original work id, deadline check in the existing timeout reconciliation, report arbitration for recovering/stale identities.
3. **Runner journal**: `WorkJournal` store + host wiring (shadow writes, load-on-start, first-poll declaration, recovering bucket re-drive, verdict/runtime-binding reconciliation). Restart, held-work skip, and redelivery tests.
4. **Runner liveness**: unconditional poll loop, per-runtime readiness flags + `DispatchService` claim filtering, local deferral with observation-only diagnostics, heartbeat presence refresh. Quarantine drain deadline + forced destruction; bounded `terminateTree`/dispatcher destroy; bounded Pi/OpenCode shutdown paths.
5. **Isolation**: `WorkExecutionLauncher` seam (systemd-run default, in-process fallback), containment detection and `resource-contained` terminal reporting, deployment unit/slice assets with envelope documentation.
6. **Surfaces**: wire interruption projections; Web, CLI, and issue attention/inbox rendering; removal of terminal `runner-lost` presentations (breaking consumer sweep).
7. **Release**: deploy Server first (old Runner poll bodies lack `readyRuntimes` — absence degrades to today's claim behavior), then Runners with the systemd slice assets, then Web/CLI. Run `npm run test:fast` and the full `npm run verify` gate with fake time and faked externals.

**Rollback**: take the normal consistent SQLite backup before deployment. Once any run or ledger writes interruption records or new reason codes, rollback to an older binary requires restoring that backup; before the first new-state write the release reverts normally. Runner-side journal files are forward-only artifacts an older Runner simply ignores.

## Open Questions

- Default `RunnerRecoveryTimeout`: 15 minutes proposed (2× the longest observed systemd restart, short enough to bound stranded work) — confirm against operator expectations for long-horizon Runner outages.
- AgentJob recovery-expired terminal is `Failed` with an interruption reason even though the execution outcome is unknowable; the spec mandates the terminal fallback, but the reason wording should make clear the failure is "no Runner returned", not an execution verdict — final reason-code strings to be settled with the status-surface work.
- Does the pinned OpenCode SDK expose the server child pid for direct SIGKILL escalation, or does force-destroy rely solely on the scoped unit kill from Decision H? Verify during implementation of Decision G.
- Pi runtime process placement (in-process turns vs child processes) determines whether Pi-bound work lands in per-work scopes or only under `mohist-runtime.slice`; verify before finalizing the launcher mapping.
- Whether container deployments (docker-compose) get an equivalent envelope mapping (per-service memory limits per Runner, single-work Runners) or are documented as isolation-advised environments.
