# Design: Runner Loss Work Recovery and Resource Isolation

This design covers the remaining scope of issue 570: the
`runner-loss-work-recovery` and `runner-resource-isolation` capabilities. It
builds on the already-landed slices whose designs are preserved in git history
(and summarized under Context): the durable work-result journal (#545), the
runtime readiness witness and per-runtime claim gating (#558), and the Agent
result-settlement arbitration (`Unknown` → `Blocked`). Those protocols are
reused, not redesigned.

## Context

A Runner is a long-lived host process (`mohist-runner`) that polls the server
for dispatches, executes work (workflow tasks, stage checks, AgentJobs) in a
process-lifetime reported set (`inFlight` → `awaitingAck`), and reports
results under a stable work identity (`ownerKind:ownerId:workId`). Presence is
the poll itself; the server's `RunnerGrain` times a runner out after 2 minutes
of silence and runs `CloseoutLostAsync`.

Current behavior on presence loss (`RunnerGrain.CloseoutLostAsync`):

- Workflow tasks and stage checks are terminal-failed via
  `FailActiveWorkAsync(workerId, "runner-lost")` unless the task carries an
  agent result settlement (those keep the `Unknown`/deadline arbitration).
- AgentJob ledgers are deliberately left intact ("durable recovery record"),
  but nothing ever resumes them: a Running AgentJob stays `Running` during the
  outage, the job-timeout path (10 min) moves it to the nonterminal,
  non-dispatchable `Unknown`, and reconnect never re-delivers it because
  `DispatchService.AddMissingRedeliveriesAsync` only considers `Running` rows.
  That is the observed stranding.
- The runner's work-result journal (#545) fences a re-delivered dispatch that
  holds a `started` entry by refusing it (`admission !== 'new'` → skip,
  forever). The refusal is correct for at-most-once execution but produces no
  outcome, so work with a dead execution strands behind the fence until some
  server-side deadline acts.

The same incidents (Epic 67) exposed the blast-radius problem: one runaway
work OOM-killed the shared Runner process taking every in-flight item with it;
a wedged OpenCode generation blocks replacement because
`scheduleRebuild` awaits `generation.drained` unboundedly; shutdown paths are
unbounded (`server-process.terminateTree` awaits `dispatcher.close()` on an
undici `Agent` built with `headersTimeout: 0, bodyTimeout: 0`, so a hung
in-flight request blocks it forever; pi `services.close()` has the same
shape).

## Runner Work-Result Journal Contract

Each Runner work identity has one local journal entry:

- `started`: the exact dispatch was admitted, but no authoritative result was
  durably recorded. A later process must refuse that dispatch unless it is a
  recorded recovery dispatch that can reconcile the existing execution.
- `completed`: the result is durably recorded and may be reported repeatedly
  with the same work identity until the server returns a durable acknowledgement.
- absent: the identity is not held by this process and may be admitted when the
  server dispatches it.

The journal uses a temporary file followed by rename. Corrupt or unreadable
state, and any failed journal write, make the journal unavailable and gate new
claims. A failed completion leaves the work in the process reported set and
does not report the result. A failed acknowledgement keeps the completed
entry and awaiting-ack work, so an accepted result can be replayed safely.

Recovery loads the journal before connecting and claiming work, places
completed entries into the existing `awaitingAck` set with a bounded report
attempt, persists a `started` fence before new execution, persists a result
before reporting, and removes the entry only after the existing durable
`Accepted`/`Stale` acknowledgement contract succeeds.

On process startup, the Runner snapshots durable `started` entries before it
claims any new work. For a Workflow Agent dispatch with a non-empty persisted
task-run identity, it reports `status: unknown` through the existing result
route after connection. That is an explicit result-unconfirmed observation, not
a synthesized `WorkItemResult`: the Server matches the original
`runnerId`/`taskRunId`/`workId` tuple and enters its existing unknown/blocked
settlement. For an AgentJob dispatch, the same receipt retains the original
`runnerId`/`agentJobId`/`workId` tuple and moves the AgentJob to its durable
`Unknown` state; it does not enter the terminal failed path. Both owners retain
their work identity and refuse physical replay.

The Runner retains the `started` fence until the observation gets an Accepted
or Stale acknowledgement, then removes it atomically. A transport or
local-delete failure leaves the original fence durable and retries the same
observation. Only entries loaded before this process begins admitting work are
eligible. Current-process `started` entries, checks, ordinary tasks, and legacy
entries without the complete owner identity stay fenced; they are never
projected as unknown reports. This prevents an active execution from being
mistaken for a lost result and prevents the generic task fallback from turning
an unsupported work type into a failure.

This is identity redelivery, not physical execution replay. It recovers the
result-before-report crash window. A process that died while the physical
execution was still unresolved remains unknown and is not inferred from a
runtime binding, idle observation, or reconnect.

Key invariants already in place that this design relies on:

- Work identity is stable and server-recognized; reports reconcile through
  `FindReportableWork`/`FindReportableTaskAttempt` and settle with
  `ReportAck.Accepted | Stale` (Stale is a durable ack — the runner may drop
  its journal entry).
- The runner durably journals `started` before executing and `completed`
  before reporting; `completed` entries replay through `awaitingAck` after a
  restart (#545).
- Runtime session bindings are probeable (`binding-recovery.ts`): a binding
  can be tested for "session exists / active turn" and re-attached or
  replaced.
- Agent-result-settlement tasks already arbitrate `Unknown` observations with
  a bounded deadline (`AgentResultSettlementTimeout`, default 5 min) ending in
  `Blocked`; presence-loss closeout already feeds that path via
  `ObserveAgentRunnerDisconnectedAsync`.
- `DispatchService` already re-renders active work from persisted facts
  (dispatch snapshots, AgentJob `DispatchJson` ledger) under the original
  identity when a connected runner's poll report omits it.

- Re-running every dispatch returned after reconnect: the original Agent may
  have applied side effects, so this is blind replay.
- Treating Pi session activity or an idle runtime as a Workflow result: those
  are observations and do not contain the authoritative result and side-effect
  boundary required by the Workflow settlement contract.
- Removing `HasUnresolvedAgentResult` from dispatch rendering: this would turn
  unresolved work into duplicate physical execution rather than recovery.

## Server Receipt Admission

The Runner's completed journal entry is the only recovery artifact that can
reconstruct a Workflow result. It carries the original dispatch and full
`WorkItemResult`, including output, error, artifact-upload, and follow-up-task
fields. On restart, Runner places that entry in `awaitingAck` and reports it
through the existing Workflow result route. The Server accepts it only when
the persisted Workflow attempt still matches the original `taskRunId`,
`workId`, and authenticated `runnerId`; an unknown or blocked settlement is
still reportable under that same tuple.

The Server has no safe source for a terminal result from a `started` entry. The
Workflow work projection stores lookup and active-work facts, not a result.
AgentSession terminal observations and the runtime close event carry physical
activity, status, and exit information, but not the complete Workflow result
contract. Terminal task-log ownership is written after Workflow task
settlement and authorizes log upload only; it is not a result receipt. The
Workflow therefore must retain `unknown` or `blocked` when those are the only
facts available.

AgentJob has an explicit durable `Unknown` state for the same boundary. A
recovered AgentJob observation may enter that state only after the Runner has
presented the exact local `started` fence identity. The AgentJob report handler
must validate the current Runner and work identity before recording Unknown;
`status: unknown` must never call the normal success/failure terminalization
path. A subsequent authoritative terminal report is still allowed to resolve
the original Job, while an already-terminal Job returns the existing stale
acknowledgement.

This preserves a single recovery rule:

1. A completed receipt replays the original identity and may settle that
   original attempt exactly once.
2. A started-only record cannot be replayed physically or translated into a
   terminal result.
3. If the original result is permanently unavailable, the current operator
   escape hatch is explicit Workflow stop. This slice deliberately provides no
   replacement-execution command. Any future product capability that schedules
   replacement work after abandonment must allocate a new TaskRun and work
   identity, so a late report for the old attempt remains stale.

Workflow must not synchronously query AgentSession to infer a result. That
would turn an execution observation into an outcome and reintroduce a
cross-owner arbitration path. The Runner result receipt remains the one
authoritative cross-boundary payload.

## Recovery slice: unresolved-agent redelivery + started-fence reconciliation (D2/D4)

This section records the already-landed deadlock-breaker subset of the agreed
runner-loss design in issue #570's run workspace (`runner-loss-work-recovery`,
decisions D2/D4 there): workflow-owned agent tasks whose result reporting was
lost across a runner restart recover without duplicate execution. The broader
interruption-recording and deadline machinery is described below; it reuses the
landed settlement arbitration (`Unknown` → `Blocked`) and work-result journal
unchanged.

Server (`DispatchService`): a run with an unresolved settlement is included
in desired redelivery for the recorded runner only, and only while the
settlement task is still `Running` with a full runtime binding. The rendered
dispatch reuses the translator path (settlement reconcile deletes snapshots)
and carries the recorded binding in a new optional `WorkDispatch.AgentRecovery`
block. Recovery renders do not reserve runner slots — they are probes, not
executions. Without a full binding the work stays absent (the deadline and
explicit-stop paths own it), because a binding-less redelivery to a runner
with no journal fence could not be reconciled and would re-execute.

Runner (`host.ts` admission + `mohist/pi` action): a dispatch carrying
`agentRecovery` never submits a new prompt. The pi action switches to
reconciliation: it inspects the bound session's recorded turn; a terminal
turn is adopted — its recorded outcome becomes the action result and the
normal executor tail (expect/artifacts/worktree/set-vars) runs unchanged; a
missing session or a foreign active turn reports the wire `unknown`, which
the server routes into settlement. A `started` fence hit by a recovery
dispatch re-arms its payload and executes the same reconciliation; fences
without a recovery dispatch still refuse silently. OpenCode recovery
dispatches are not executed in this slice (the OpenCode runtime does not yet
expose an API for adopting a terminal turn's facts); they report `unknown`
and adoption remains future work.

Capacity note: recovery renders do not reserve poll slots, but the runner
grain's claim gate still counts every Running-assigned run, so a runner at
capacity with lingering unresolved runs takes no fresh work until those runs
settle (recovery dispatches themselves are never capacity-gated) or an
operator stops them. Freeing that capacity automatically is the full design's
deadline machinery, out of scope here.

Constraints: single shared OpenCode server process per runner hosts many
works; the runner runs on Linux and macOS hosts under systemd user units or
foreground; no new external dependencies.

## Goals / Non-Goals

**Goals:**

- Presence-loss closeout records a recoverable interruption (reason code,
  affected work identity, timestamp, recovery deadline) instead of
  terminal-failing ordinary workflow tasks and checks as `runner-lost`.
- AgentJobs whose runner is lost project an explicit recovering state with the
  recorded reason (extending nonterminal `Unknown`), are resumable on
  reconnect, and reach a definite terminal state at a bounded deadline.
- Reconnect recovers work identity-preservingly: re-attach to a surviving
  runtime execution, replay a durable unacknowledged result, or re-deliver
  from persisted facts — never a second physical execution.
- The runner's `started` fence reconciles instead of refusing silently:
  re-attach where facts support a live execution, otherwise surface a
  non-terminal interruption/unknown observation under the original identity;
  terminal fallback belongs to the explicit recovery deadline.
- Late reports/observations from previous runner process generations are
  accepted at most once or acked stale; no duplicate outcomes or events.
- Status surfaces (Web, CLI, issue attention) render
  recoverable-interrupted/recovering with the recorded reason.
- Per-work resource containment bounds a runaway work and terminates it
  without killing the runner process or sibling work; quarantined generation
  drain and all runtime shutdown paths are bounded.

**Non-Goals:**

- Cross-runner takeover of in-flight work (work stealing). Recovery targets
  the recorded runner identity; permanent runner loss ends in a definite
  terminal state, not reassignment (rationale in D3).
- Re-designing the dispatch/presence model, the work-result journal, the
  readiness witness, or the settlement arbitration.
- Server-side per-work execution leases or fencing tokens.
- Exactly-once side effects for agent actions; the boundary remains
  at-most-once physical execution plus at-least-once identity redelivery.
- Windows support for containment primitives.
- Preventing runner OOM entirely; containment bounds per-work damage, the
  runner host's own memory budgeting stays deployment-level.

## Decisions

### D1. Interruption is a recorded fact on the work, not a new state-machine state

**Workflow work.** `TaskRun` and the stage-checks representation gain an
optional interruption record
`WorkInterruption { ReasonCode, WorkId, OwnerId, RecordedAt, RecoveryDeadlineAt }`.
An interrupted task/checks stay `Running`; nothing about dispatch eligibility
changes (`CurrentActiveWorkFor` still finds them, reports still reconcile).
Status surfaces derive a `recoverable-interrupted` presentation from
`Interruption != null` plus the reason. A terminal report accepted under the
original identity clears the record (existing report paths already gate on
active work).

**AgentJob.** `AgentJobStatus.Unknown` is already the nonterminal,
non-dispatchable state with a recorded `FailureReason` and the
`agent-job-recovery` Orleans reminder. We extend it rather than add a status:
an Unknown job whose reason is a runner-loss interruption carries
`RecoveryDeadlineAt`, and every surface projects `recovering` + reason while
the deadline is in the future. A first authoritative report settles the job
(Existing arbitration), the deadline produces the definite terminal state.

*Alternatives considered:*

- New `TaskRunStatus.Interrupted` / `AgentJobStatus.Recovering` enum values —
  rejected: `TaskRunStatus` feeds DB computed columns, dispatch eligibility
  checks, translator shapes, and every consumer would need auditing; a new
  AgentJob status would bypass, not extend, the Unknown arbitration the spec
  explicitly preserves. A nullable record keeps the state machine closed.
- Terminal-fail now and auto-retry the task — rejected: a retry mints a new
  attempt/work identity and re-executes an agent action whose side effects may
  have applied; this violates identity preservation and at-most-once.

### D2. Closeout records the interruption; owner grains enforce the deadline

`RunnerGrain.CloseoutLostAsync` replaces the `FailActiveWorkAsync(workerId,
"runner-lost")` call with `IWorkflowGrain.InterruptActiveWorkAsync(workerId,
reason)` (reason code `runner-lost`), which records `WorkInterruption` with
`RecoveryDeadlineAt = now + RunnerLossRecoveryTimeout` on the active task
attempt or running checks. Agent result-settlement tasks are excluded — they
keep flowing through `ObserveAgentRunnerDisconnectedAsync` exactly as today
(the spec forbids bypassing that arbitration). Closeout additionally notifies
`AgentJobGrain`s with Running work for the runner to enter the recovering
projection (reason + `RecoveryDeadlineAt`) via the existing
`MarkUnknownAsync`/`EnterUnknownStateAsync` seam.

Deadline enforcement lives with the state owner, using timers that survive
process restarts:

- `WorkflowGrain` arms its existing reconciliation sweep when an interruption
  is recorded; at `RecoveryDeadlineAt` with no authoritative outcome the task
  or checks reach a definite terminal `Failed` carrying the recorded reason
  (`runner-lost`) and a `TaskInterrupted`-family event. The mechanics mirror
  `BlockUnresolvedAgentResult` (deadline check in a periodic reconcile), so a
  silo restart re-arms from persisted state.
- `AgentJobGrain` extends the `agent-job-recovery` Orleans reminder (already
  durable across silo restarts): while Unknown-with-recovering-reason it
  enforces `RecoveryDeadlineAt` → definite terminal `Failed` carrying the
  reason; a first authoritative report before that settles normally.

`RunnerLossRecoveryTimeout` is one knob (default ≈ 15 min) configured next to
`AgentResultSettlementTimeout`/`JobTimeout`. It must exceed the 2-minute
presence timeout (so interruption is recorded before any deadline can fire)
and it bounds the worst-case nonterminal window after loss.

*Alternatives considered:*

- RunnerGrain-side deadline sweep over workflow runs — rejected: the grain
  deliberately holds no workflow work records; duplicating owner state there
  reintroduces the two-ledger problem the reconciliation model removed.
- Reusing the settlement deadline for everything — rejected: settlement
  arbitration is specific to agent result settlement (5 min, ends `Blocked`);
  ordinary tasks and checks need their own deadline semantics ending `Failed`
  with the interruption reason.

### D3. Re-delivery targets the recovering runner identity; no cross-runner takeover

At-most-once physical execution rests on **runner-local durable facts** (the
work-result journal). A different runner cannot consult the lost runner's
journal, so it cannot distinguish "never executed" from "executing right now
on a partitioned host". Therefore:

- Workflow work stays assigned to the original runner identity; on that
  runner's reconnect, the existing `AddMissingRedeliveriesAsync` path
  re-renders the interrupted work from persisted facts (dispatch snapshot or
  translator) under the original identity. If the old process survived a
  partition it reports the key in its poll report and no re-delivery happens;
  if it died, the re-delivery hits the journal fence and reconciles (D4).
- AgentJobs in the recovering projection are added to the redelivery desired
  set when their recorded runner reconnects: the ledger `DispatchJson`
  re-delivers under the original work identity, deliberately forcing the
  runner to reconcile against its journal (D4) instead of stranding. This
  changes today's "unresolved Agent work is deliberately absent from desired
  redelivery" rule to: absent while the runner is away, included once the
  recorded runner is back and holds neither the key nor a settled outcome.
- If no runner with that identity ever returns, D2's deadline produces the
  definite terminal state. Permanent host loss is a terminal outcome, not a
  takeover.

A partitioned-but-alive old process and a deadline race are safe in both
directions: a late authoritative report after the deadline terminal-fail
reconciles as `Stale` (the work is no longer reportable-active), and the
runner treats `Stale` as a durable ack that retires its journal entry.

*Alternatives considered:*

- Lease-based work stealing (visibility timeout + fencing tokens) — the
  general queue solution, but it requires server-authoritative execution
  leases, fencing on every report, and a redesign of the claim/dispatch model;
  far beyond this change and unnecessary for the incident class (single-runner
  fleets dominate).
- Unassign on presence loss and let any runner claim — unsound: the new runner
  would execute a work whose original execution may still be running on the
  partitioned host (duplicate execution).

### D4. The `started` fence reconciles instead of refusing silently

Today `runWorkerPool` skips a re-delivered dispatch whose journal entry is
`started` (`admission !== 'new'`) forever. New reconciliation on the runner,
evaluated per re-delivery hitting a `started` entry:

1. **Held in-process** (`inFlight`/`awaitingAck` contains the key): skip —
   the existing dedupe; the work is executing or its result is pending ack.
2. **After restart, binding-supported re-attach**: for runtime-backed work,
   probe the persisted runtime binding (server-recorded binding in the
   dispatch/`binding-recovery` probe). If the session exists and shows an
   active turn, re-attach: adopt the execution, wait for the turn's terminal
   state, persist the result in the journal, and report under the original
   identity. The physical turn continues; nothing executes twice.
3. **Otherwise, surface a non-terminal recovery observation**: the execution
   context is not supported by a surviving binding on this host. The runner
   reports the wire `unknown` status under the original identity; the Server
   routes Workflow Agent work into settlement arbitration and records AgentJob
   work as durable `Unknown`. A `started`-only record never supplies a
   terminal result or completes the journal with a fabricated failure. If the
   broader interruption model has recorded a recovery deadline, its owner may
   later apply the definite terminal fallback; the durable observation ack
   then retires the journal entry.

The fence itself never opens: `begin()` still refuses to return `new` for a
`started` identity, so re-delivery can never trigger a second physical
execution no matter how often it repeats; each delivery just re-runs the same
reconciliation decision (idempotent by construction).

*Alternatives considered:*

- Keep refusing and rely on the server deadline (D2) — rejected: it leaves
  work non-dispatchable and invisible for the entire deadline even though the
  runner can immediately record a non-terminal recovery observation; the spec
  explicitly forbids silent indefinite refusal.
- Infer a result from runtime session state (idle session, transcript) —
  rejected in #545 and stays rejected: observations are not authoritative
  results and would fabricate outcomes across the side-effect boundary.

### D5. Late reports and observations are accepted at most once or acked stale

The identity contracts already provide the machinery; this change closes the
gaps and proves them:

- Workflow: `FindReportableWork`/`FindReportableTaskAttempt` match on
  (taskRunId, workId, workerId); once the attempt is terminal there is no
  active work, so a second or late report returns `Stale` — no duplicate
  events. Competing generations: first authoritative report wins; the loser
  gets `Stale`, which the runner honors as a durable ack.
- AgentJob: an Unknown/recovering job accepts exactly one first authoritative
  result (existing settlement seam); subsequent deliveries for a settled job
  are acked `Stale` so the reporter's journal entry retires. Today a report
  against an Unknown job must not be silently dropped — it either settles the
  job or is acked stale, never dead-ends the reporter.
- Observations (`ObserveAgentExecutionAsync`, launch-observation updates)
  reconcile through the settlement/observation update result: rejected =
  `Stale`; an observation never regresses a recovered or settled work.

### D6. Status surfaces render the recorded reason, not a bare failure

The interruption record (D1) is the single source for presentation:

- Server read models (`WorkflowViews`, `WorkflowStatusMapper`, Slack/issue
  attention projections) map `Running + Interruption` to
  `recoverable-interrupted` with reason and deadline; agent-job status/launch
  observation (`AgentLaunchObservationAssembler`) maps Unknown-with-recovering
  reason to a `recovering` turn status with the reason.
- Web workflow and agent-session views render the new states, distinguished
  from healthy running and from terminal failure.
- CLI (`mo run`, agent observation) renders the same.

**Breaking**: `runner-lost` stops being a terminal failure of active work.
Anything that parsed `runner-lost` as terminal must render the new states;
the terminal deadline outcome (D2) is the only failure presentation.

### D7. Per-work containment is deployment-configured and enforcement is split by work kind

A `workResourceLimits` runner configuration block (backed by deployment:
systemd unit environment / config file; default memory bound per work, e.g.
`memoryMb`) bounds each work. Enforcement differs by where the work's memory
actually lives:

- **Action subprocess work**: spawn-time OS limits bound the work's child
  process tree, backed by an aggregate-RSS watchdog and a wall-clock kill.
  Node's `spawn`/`SpawnOptions` exposes no resource-limit support
  (`resourceLimits` exists only for worker threads), so the limits are
  applied through a util-linux `prlimit(1)` wrapper at the `runCommand`
  boundary: `system/process.ts` gains an optional per-command
  resource-limit option on `CommandLineOptions` — layered in exactly like
  `timeoutMs`, omitted ⇒ byte-identical spawn — and the process-action
  spawn path (`actions/built-in-core.ts`) executes
  `prlimit --as=<bytes> --data=<bytes> -- <command> <args…>` through the
  unchanged `ProcessSpawner` seam when the option is set and the `prlimit`
  binary is available (probed once at runner startup). `prlimit` sets its
  own limits and `exec`s the target in place, so the child PID and its
  detached process-group leadership are stable: `killProcess`'s
  `process.kill(-pid)` group kill and `runCommand`'s timeout/`onClose`
  machinery keep working unchanged. Over-limit allocation then fails in the
  kernel — allocation failure or a killed process, no detection latency —
  and the executor maps that abnormal exit to the `resource-containment`
  failure. Where `prlimit` is unavailable (macOS, minimal containers)
  enforcement is watchdog + wall-clock only: the watchdog samples the
  tree's aggregate RSS on a short interval and terminates the process group
  on breach (bounded detection latency; see Risks). Limits are inherited
  per process rather than aggregated per tree, so the OS bound is
  per-process; the watchdog's aggregate sampling covers tree-wide growth.
  Exceeding a bound terminates only that tree.
- **Runtime-backed work (OpenCode/pi turns)**: the heavy state lives in shared
  runtime processes that cannot be RLIMIT-ed per work without process-per-work
  (rejected below). Containment is budget-based: per-work resource watchdog
  (transcript/turn size already capped by the outbox drop guard, extended with
  a per-work turn budget) aborts the offending turn and quarantines its
  generation; a quarantined generation is torn down through the bounded
  teardown paths (D8/D9), which kill the generation's server process — not
  the runner.

A contained work is reported as a definite failed result with reason
`resource-containment` under its original identity, so it settles through the
normal report path (and clears any interruption). Sibling work is untouched:
their executions, journals, and awaiting-ack results are independent of the
terminated tree/generation.

*Alternatives considered:*

- cgroup-v2 systemd scopes per work (`systemd-run --scope -p MemoryMax=…`) —
  the strongest isolation, but it requires systemd system privileges, breaks
  on macOS hosts, and cannot scope the shared OpenCode server that hosts
  multiple works' turns. Noted as future hardening for action work only.
- Process-per-work runtimes — full isolation but a redesign of the shared
  runtime/generation model and a large regression in startup cost.
- Post-spawn `prlimit(2)` applied to the child PID after spawn — rejected: a
  race window between spawn and limit landing exactly when a
  fast-allocating work is hottest, and it needs the same host binary as the
  wrapper form, so the wrapper is strictly better.
- Shell `ulimit` preexec (`sh -c 'ulimit -v …; exec …'`) — rejected: inserts a
  shell whose quoting must survive arbitrary action arguments and whose
  flag semantics vary by shell; the wrapper keeps the direct in-place `exec`
  and a single spawn.
- Watchdog-only enforcement (drop the OS limits) — rejected as the primary
  mechanism: a work allocating faster than one watchdog sample can exhaust
  host memory and let the kernel OOM killer select the runner process
  itself — the exact incident class. Watchdog + wall-clock remain the
  fallback where `prlimit` is absent and the second line of defense
  everywhere. Relying on the util-linux `prlimit` host binary is probed, not
  assumed — consistent with the runner's existing host-tool reliance
  (`git`, `gh`) and the no-new-external-dependencies constraint (no npm or
  native packages are added).

### D8. Quarantined generation drain is deadline-bounded

`scheduleRebuild` awaits `generation.drained` unboundedly. The runtime gains a
`quarantineDrainTimeoutMs` (config, default 60s): the drain promise is raced
against the deadline; on elapse the generation is force-released
(`resolveGenerationDrain` + detaching the server handle through the bounded
teardown path), the replacement generation starts, and each still-"active"
turn's work is reported as a definite failed result with reason
`generation-drain-timeout` under its original identity — sibling
completed-but-unacknowledged results survive in the work-result journal and
report normally. Normal drains (turns end before the deadline) release
promptly, unchanged.

### D9. All runtime shutdown paths are bounded

- `server-process.terminateTree`: race `server.close()` + `dispatcher.close()`
  against a deadline (default ~30s); on elapse, abandon the waits and force
  teardown (dispatcher `destroy()`, SIGKILL the process tree best-effort,
  never await). The undici `Agent`'s disabled timeouts (`headersTimeout: 0`)
  are what make the current close unbounded; the bound lives at the handle
  layer so every caller (shutdown, quarantine teardown, rebuild) inherits it.
- OpenCode process-tree termination: SIGTERM → bounded grace → SIGKILL, and
  the whole termination is itself bounded so a zombie child cannot wedge the
  runner.
- Pi `shutdown()`: `services.close()` raced against the same deadline with
  abandon-and-proceed semantics.

Bounded means the shutdown path *returns* by the deadline; forced teardown is
best-effort and the OS reaps the rest.

## Risks / Trade-offs

- [Interruption deadline too short → work terminal-fails while a slow runner
  legitimately restarts] -> single tuned knob (`RunnerLossRecoveryTimeout`,
  default 15 min ≫ 2-min presence timeout + realistic restart); deadline
  outcome carries the reason so a rerun is a user decision, not a mystery.
- [Same-runnerId host with wiped disk re-executes `started` work after
  re-delivery (journal absent → `begin` returns `new`)] -> accepted residual
  risk: the host asserts it holds no durable facts; identity-redelivery
  semantics assume runner identity ⇒ host identity. Documented in ops docs;
  wiping a runner root while work is recorded against it is an operator
  procedure to avoid.
- [Re-attach (D4.2) adopts a turn that is actually wedged] -> the adopted
  execution is itself subject to per-work containment (D7) and the job
  timeout / settlement deadlines, so a wedged re-attach still terminates
  bounded; re-attach never extends deadlines.
- [Recovering AgentJobs enter the redelivery desired set — a runner that
  cannot reconcile them would loop re-deliveries] -> each delivery either
  re-attaches, replays a journal result, or reports the interruption outcome
  (D4.3), which retires the ledger row; poll-time dedupe plus the
  Accepted/Stale contract prevents unbounded churn. Covered by tests.
- [Shared-runtime works cannot be memory-isolated per work (approximation in
  D7)] -> worst case a single runaway turn still quarantines a generation
  (bounded drain, D8) instead of killing the runner; per-work OS-level
  isolation for runtime work remains future work. The OOM-the-whole-runner
  failure mode is additionally mitigated by deployment-level runner memory
  bounds (systemd `MemoryMax` on the unit) — losing the runner process is now
  recoverable end-to-end, so the blast radius of that residual event is
  bounded by this design.
- [Watchdog-only hosts (no `prlimit` binary): containment detection
  latency] -> the watchdog terminates on its next RSS sample (interval on
  the order of seconds); a work allocating faster than one sample can
  exhaust host free memory first. Residual is deployment-bounded (systemd
  `MemoryMax` on the runner unit) and runner loss is recoverable end-to-end
  by this design. Related knob semantics: RLIMIT_AS bounds virtual address
  space — conservative, large sparse mappings fail at modest RSS;
  RLIMIT_DATA is the closer-to-RSS knob on Linux ≥ 4.7 — so `memoryMb`
  defaults must leave headroom for the toolchain's virtual reservations.
- [Breaking change: consumers treating `runner-lost` as terminal] -> ship
  server + web + CLI in one deployment (they release together); the wire
  never carried `runner-lost` as a distinct terminal code outside failure
  messages, so external blast radius is the rendered strings and the new
  states.
- [Forced generation release orphans in-flight turn state inside the old
  server process] -> forced teardown kills the generation's server process;
  affected works get definite `generation-drain-timeout` failures; sessions on
  that generation are re-created on demand by binding recovery.
- [Deadline timer lost on silo restart] -> WorkflowGrain re-arms from
  persisted interruption state on activation (same pattern as the settlement
  sweep); AgentJobGrain uses the durable Orleans reminder. Both re-derive the
  deadline from persisted `RecoveryDeadlineAt`, never from in-memory clocks
  alone.

## Migration Plan

Server and runner deploy independently (rollout order and rollback):

1. **Server first.** New closeout (interruption recording + recovering
   projection + deadline sweeps) and status surfaces ship together — web and
   CLI are baked into the same release, so the breaking `runner-lost` change
   never meets an old renderer. Old runners keep working: their reports carry
   the original identities and settle interruptions normally; the started
   fence still refuses re-deliveries, but now D2's deadline (not silence)
   resolves them.
2. **Runner second.** Fence reconciliation (D4), containment (D7), bounded
   drain/shutdown (D8/D9). New runner + old server is safe: it only changes
   what the runner does with dispatches it already receives.
3. **Deployment config.** Document `workResourceLimits`,
   `quarantineDrainTimeoutMs`, bounded-shutdown deadlines, and the
   systemd `MemoryMax` recommendation for the runner unit
   (`mo install`/self-host docs).
4. **Rollback.** Re-deploying the previous server re-enables terminal
   `runner-lost` closeout; interrupted records are ignored by old code, but
   tasks stay `Running` and old sweeps do not act on them — so rollback
   includes a one-line ops action: fail open interruptions via the existing
   abandon/fail grain API (or let the AgentJob unknown paths time out). No
   data migration is needed: the interruption record is an additive nullable
   field; old readers skip unknown JSON fields.

## Open Questions

- Default value and per-deployment tuning of `RunnerLossRecoveryTimeout`
  (proposed 15 min): does any fleet run workflows whose runners routinely
  restart slower than that (e.g. build-update restarts)?
- Should the recovering AgentJob's redelivery desired-set inclusion be gated
  on the runner's reported admission health (`AdmissionReady`), so a runner
  with an unavailable journal is not asked to reconcile work it cannot fence?
  (Current proposal: include unconditionally; the journal-unavailable runner
  already gates fresh claims and its reconciliation attempt is a no-op skip.)
- Per-work containment defaults: single fleet-wide `memoryMb`, or per
  work-type bounds (agent turn vs. action subprocess)? Needs input from the
  incident's memory telemetry.
- Whether `TaskInterrupted`-family events need a dedicated workflow-event type
  (visible in run history) or reuse `TaskFailed` with the recorded reason —
  lean toward a dedicated event for unambiguous rendering, cost is one more
  event shape in the read models.
