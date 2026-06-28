## Why

Once a runner takes a work item, nothing reliably fails it if the single work
hangs while the runner process stays alive, or if server and runner restart in
sync. Heartbeat loss (`CheckHeartbeatAsync`) only catches a fully-dead runner;
per-work completion supervision was deleted in T-003 / T-004
(`5f6e8a66e7`, `a14c80b557`) as "delete control-plane supervision", leaving an
explicit TODO to follow up. The result is orphaned `Running` tasks that never
auto-fail or get rescheduled — issue #275's `proposal.1` stuck 9+ hours, and 8
sessions frozen on 6/26. We need a control-plane safety net so a stuck work is
detected and synthesized as failed even when the runner process looks healthy
or has been restarted underneath it.

## What Changes

- Restore a unified **`WorkCompletionTimeout`** (default 30min, `Mohist:Workflow`
  config), enforced **in RunnerGrain** at the point work is taken — not per-task,
  not in WorkflowGrain.
- A timed-out work is synthesized as **`failed`** (`reason=timeout`) through the
  existing `ReportWorkflowResultAsync` channel — no new result state, aligned
  with the existing `passed|failed + detail` contract.
- Add a persistent **`RunnerWorks`** ledger: every work taken on a runner
  (workflow + agent-job) is recorded with `TakenAt`, and terminal rows
  (`completed | failed` + `reason`) are retained for history. Agent-job work now
  has a single home here (it has none in `WorkflowRuns`).
- Drive timeout detection by a per-runner **Orleans reminder** (persisted in
  `OrleansRemindersTable`) that scans the in-memory active set each tick — zero
  DB reads per tick. On grain activation the `outstanding` rows are hydrated
  once into memory. Reminders (not grain timers) survive grain deactivation /
  silo restart and re-activate the grain to fire, which is what covers the
  #275 server+runner sync-restart case.
- `RunnerGrain` injects **`TimeProvider`** for all timeout-related time reads
  (`TakenAt`, scan `now`, `FinishedAt`); the take points
  (`PollOneWorkflowAsync`, `AssignAgentJobAsync`) are switched off
  `DateTimeOffset.UtcNow` so the deadline origin is deterministic and testable
  via `FakeTimeProvider`.
- `RecoverActiveWorkflowWorkAsync` stops resetting the clock with `UtcNow`;
  with the persistent ledger it reloads the original `TakenAt`, self-healing
  that bug.
- Complementary, not overlapping, with heartbeat loss: heartbeat → `runner-lost`
  (whole runner dead); work-completion timeout → `timeout` (runner alive, one
  work stuck).

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `workflow-supervision`: Reverses "work execution timeout is owned solely by
  the runner process" — RunnerGrain now also enforces a control-plane
  per-work `WorkCompletionTimeout` as a safety net, driven by a persisted
  Orleans reminder. The outstanding-work bookkeeping (currently in-memory
  `_agentJobs` / `_outstandingWorkflowWorks`) becomes a persistent
  `RunnerWorks` ledger hydrated on activation, so detection survives grain
  deactivation and silo restart. Partially fulfills the previously-deferred
  "runner-loss detection must be persisted" follow-up, scoped to per-work
  timeout only (heartbeat/runner-loss reminder-ization stays a Non-Goal).

## Impact

- **Server (`packages/server`)**:
  - `RunnerGrain` — register per-runner reminder; reminder tick scans in-memory
    active works and synthesizes `timeout` failures via
    `ReportWorkflowResultAsync`; hydrate outstanding works on
    `OnActivateAsync`; inject `TimeProvider`; switch take points off
    `UtcNow`.
  - Persistence — new `RunnerWorks` table (EF SQL); insert on take,
    update-to-terminal (no delete) on report / synthesis.
  - `RecoverActiveWorkflowWorkAsync` — reload original `TakenAt` from ledger
    instead of `UtcNow`.
  - Config — `Mohist:Workflow.WorkCompletionTimeout` (default 30min).
- **Tests**: `FakeTimeProvider` to deterministically advance time and assert
  timeout synthesis, cross-restart orphan recovery (#275 scenario), and
  non-interference between `timeout` and `runner-lost` paths.
- **Out of scope (per Non-Goals)**: per-task/per-stage differentiated timeouts;
  upgrading heartbeat loss (`CheckHeartbeatAsync` grain timer) to a reminder;
  `RunnerWorks` history TTL/cleanup; unifying `ConfigService.taskTimeout` with
  `WorkCompletionTimeout`.
