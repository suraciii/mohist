## Context

Once a runner takes a work item, the control plane has no reliable way to fail
that single work if it hangs while the runner process stays alive, or if server
and runner restart in sync. Two gaps combine to produce orphaned `Running` tasks
(see #275 — a `proposal.1` stuck 9+ hours; 8 frozen sessions on 6/26):

1. **Heartbeat loss only catches a fully-dead runner.**
   `CheckHeartbeatAsync` (`RunnerGrain.cs:391`) is a **grain timer** registered in
   `RegisterAsync` (`RunnerGrain.cs:79`). When it fires and
   `now - _lastHeartbeat > 2min`, `HandleTimeoutAsync` synthesizes `runner-lost`
   for every entry in `_outstandingWorkflowWorks` via
   `NotifyTrackedWorkflowRunnersLostAsync` (`RunnerGrain.cs:411`). It does **not**
   cover "runner alive, one work stuck", and because it is a grain timer it does
   **not** survive grain deactivation — exactly the #275 sync-restart failure mode.

2. **Per-work completion supervision was deleted.**
   T-003 (`5f6e8a66e7`) / T-004 (`a14c80b557`) removed the WorkflowGrain-side
   per-work timeout (a persisted Orleans reminder) as "delete control-plane
   supervision", leaving an explicit TODO to follow up. Since then nothing
   enforces a per-work completion deadline.

Additional facts that shape the design:

- `RunnerGrain` (`RunnerGrain.cs:15`) is `[Reentrant]`, string-keyed by
  `RunnerId`, and currently **purely in-memory**: no `TimeProvider` injected, no
  `[PersistentState]`, no reminder. The only persistence touchpoint is
  `RunnerDefinitionStore` (slots).
- Work-take points stamp `DateTimeOffset.UtcNow`:
  `PollOneWorkflowAsync:505` (workflow, `RunnerWorkflowWork.PolledAt`) and
  `AssignAgentJobAsync:169` (agent-job, `RunnerTrackedWork.AssignedAt`).
- `ReportWorkflowResultAsync` (`RunnerGrain.cs:174`) is a **pure relay** — it
  translates the result and calls into `WorkflowGrain`, then only does
  `_outstandingWorkflowWorks.Remove(key)` (line 205). No terminal bookkeeping
  anywhere.
- `RecoverActiveWorkflowWorkAsync:237` rebuilds a `RunnerWorkflowWork` with
  `DateTimeOffset.UtcNow`, resetting the deadline origin on every recovery — a
  latent bug that makes any timeout computation meaningless after reactivation.
- Agent-job work has **no persisted home**: it lives only in the `_agentJobs`
  dict (`RunnerGrain.cs:21`); `WorkflowRuns` has no row for it.
- The building blocks we need are **already wired in production**
  (`MohistSiloRegistration.cs:12-26`): `UseAdoNetReminderService` (SQLite,
  `OrleansRemindersTable`) and `AddAdoNetGrainStorageAsDefault`. `TimeProvider.System`
  is a registered singleton (`MohistServiceRegistration.cs:77`).
- `WorkResult` (`IRunnerGrain.cs:99`) is a free-form `Status` + `Message`; the
  existing `runner-lost` synthesis already reuses it as
  `new WorkResult("failed", "runner-lost")` (`RunnerGrain.cs:427`). No new result
  state is needed for `timeout`.
- Established test pattern: `AgentJobGrainSpecs.cs` injects `FakeTimeProvider`,
  calls `TimeProvider.Advance(...)`, then invokes a manual `CheckTimeoutsAsync()`
  hook on the grain — because the test silo uses
  `UseInMemoryReminderService`/`AddMemoryGrainStorageAsDefault`
  (`GrainTestConfig.cs:42-43`) and reminders are not deterministically tickable.
- `RunnerFailureSpecs.cs:98-126` has a **skipped** test
  (`RunningTask_WithoutReport_TimesOutWithoutQueryingRunner`) left over from the
  T-003/T-004 deletion; this change is what un-skips/replaces that coverage.

Stakeholders: the `workflow-supervision` capability (modified, see
`specs/workflow-supervision/spec.md`). Risk driver `risk=high` comes from
changing workflow runtime supervision behavior + adding a persistence migration;
the main hazard is false-positiving a healthy long task (mitigated by a generous
30min default + configurability).

## Goals / Non-Goals

**Goals:**

- Restore a unified `WorkCompletionTimeout` (default 30min, `Mohist:Workflow`)
  enforced **in `RunnerGrain`** at the point work is taken — the control-plane
  safety net the deleted WorkflowGrain reminder used to provide, but sited at the
  take point per the boundary rule (dispatch view owns it).
- Persist a per-runner `RunnerWorks` ledger (workflow + agent-job) so outstanding
  work is queryable after grain deactivation / silo restart, and so terminal
  state is retained for history.
- Drive timeout detection with a **persisted Orleans reminder** (not a grain
  timer) that re-activates the grain to fire — the mechanism that specifically
  covers the #275 server+runner sync-restart case.
- Synthesize `failed` (`reason=timeout`) through the existing report channel —
  no new result state — and keep it from interfering with the existing
  `runner-lost` path.
- Switch all timeout-related time reads (and the two take points) to an injected
  `TimeProvider` so deadlines are deterministic and testable; self-heal the
  `RecoverActiveWorkflowWorkAsync` clock-reset bug by reloading the original
  `TakenAt` from the ledger.

**Non-Goals:** (per proposal)

- per-task / per-stage differentiated timeouts (unified only).
- Upgrading heartbeat loss (`CheckHeartbeatAsync` grain timer) to a reminder.
- `RunnerWorks` history TTL / cleanup (retain all; growth治理 is a follow-up).
- Unifying `ConfigService.taskTimeout` (runner-process config) with
  `Mohist:Workflow.WorkCompletionTimeout`.

## Decisions

### D1. Persist the ledger as an EF SQL table (`RunnerWorks`), not `[PersistentState]` grain storage

The codebase has two persistence styles: `[PersistentState]` grain storage
(`WorkflowStageLockGrain`, counters — opaque JSON blob per grain, ETag
concurrency, zero schema work) and EF SQL tables (`WorkflowRunRow` + a scoped
`WorkflowRunStore`, queryable rows).

**Decision: EF SQL table.** Add a `RunnerWorkRow` entity + mapping to
`MohistDbContext` and a scoped `RunnerWorkStore`, mirroring the
`WorkflowRunRow`/`WorkflowRunStore` pattern (simpler — no aggregate JSON, no
ETag token; per-row insert/update).

**Rationale:** The ledger has a per-work-row lifecycle and retains history
(TTL is an explicit Non-Goal → unbounded growth). The operations are:

- insert on take (one row),
- update-to-terminal on report/synthesis (one row),
- **filtered read** `WHERE status='outstanding' AND runner_id=@rid` on activation.

`[PersistentState]` reads/writes the **entire** grain state as one blob, so every
activation would load the full retained history and every terminal update would
rewrite the whole blob — unbounded history directly degrades activation latency.
A filtered SQL query loads only the outstanding set (bounded by runner slot
count) on activation. This is the same shape as `WorkflowRunRow` and is
queryable for ops/diagnostics.

**Alternatives considered:**

- `[PersistentState]` + grain storage — rejected for the whole-state read/write
  granularity vs. a growing append-mostly ledger. Better suited to small
  aggregate-like state (locks, counters) than a per-row history. Would also
  require keeping the history blob in memory permanently.
- Keep the ledger purely in-memory and accept that a sync-restart orphans works —
  this is the status quo and the exact bug being fixed.

### D2. Drive timeout detection with a per-runner Orleans reminder; lazy lifecycle

**Decision: `RunnerGrain` implements `IRemindable` and registers a per-runner
reminder named e.g. `"work-timeout"` via `RegisterOrUpdateReminder`.**
`ReceiveReminder` calls the same private scan method used by the test hook. The
scan iterates the **in-memory** active set only (zero DB reads per tick — the
`outstanding` rows are hydrated once in `OnActivateAsync` from the ledger).

**Reminder lifecycle (self-managing, avoids waking idle grains):**

- **Register-or-update on take** — `PollOneWorkflowAsync` / `AssignAgentJobAsync`
  ensure the reminder exists when the first outstanding work appears.
- **Unregister on drain** — after each scan, if the outstanding set is empty,
  unregister the reminder so a quiesced runner grain can stay deactivated.

**Scan period:** target ~1 minute, well under the 30min timeout so detection
latency is bounded. Confirm against the Orleans reminder minimum-period floor at
implementation time; tune if needed.

**Rationale / alternatives:**

- **Grain timer** (like the heartbeat timer at `RunnerGrain.cs:79`) — rejected:
  does not survive grain deactivation, which is precisely the #275 failure mode.
  This is the core reason the spec mandates a reminder.
- **Always-on reminder (never unregister)** — simpler, but re-activates every
  runner grain forever to scan an empty set. Rejected as wasteful; the lazy
  lifecycle is cheap and correct because take always goes through the grain.

Because the reminder re-activates the grain, `OnActivateAsync` runs first
(hydrating the ledger → memory) and only then does `ReceiveReminder` fire — so
the hydration-before-scan ordering is guaranteed.

### D3. Unified outstanding set; both timeout and runner-loss synthesize over it, dispatched by `OwnerKind`

**Decision:** Introduce a unified `RunnerWork` domain record (`OwnerKind`, `OwnerId`,
`WorkId`, `TakenAt`, `Status`, `Reason`, `FinishedAt`) and hydrate the in-memory
active set from the ledger's `outstanding` rows on activation. The reminder scan
and the `runner-lost` path both iterate this unified set.

**Synthesis dispatch branches on `OwnerKind`:**

- `workflow` → existing `ReportWorkflowResultAsync` → translator →
  `IWorkflowGrain.ReportTaskOutcomeAsync` / `ReportCheckOutcomeAsync`
  (unchanged channel; `new WorkResult("failed", "timeout")` vs `"runner-lost"`).
- `agent-job` → the agent-job fail channel (agent-job work now has its only home
  in `RunnerWorks`; confirm the exact cross-grain fail hook on
  `IAgentJobGrain` during implementation — `AgentJobGrain` already has timeout
  failure reasons via its own `CheckTimeoutsAsync`).

**Rationale:** The ledger is the common model for both owner kinds; splitting the
scan by owner kind would duplicate scan/hydrate logic. Unifying also lets the
existing `NotifyTrackedWorkflowRunnersLostAsync` (currently workflow-only,
`RunnerGrain.cs:411`) naturally cover agent-job works, which the spec's
runner-loss requirement calls for ("遍历该 runner 上的所有 work（workflow work +
agent-job work）").

**Alternatives considered:**

- Keep the two existing dicts (`_outstandingWorkflowWorks`, `_agentJobs`) with
  separate value shapes and have the scan iterate both — more conservative (less
  refactor), but doubles the scan/hydrate code and leaves agent-job works out of
  runner-loss synthesis. Rejected in favor of the unified model; the two-dict
  shapes can be preserved as fields inside the unified record if needed.
- Make timeout synthesis workflow-only and leave agent-job timeout to a later
  change — rejected: the ledger covers both by design and the spec's runner-loss
  requirement covers both owner kinds.

### D4. Reentrancy safety: snapshot keys, reconfirm before synthesizing, report's terminal update is authoritative

`RunnerGrain` is `[Reentrant]`, so a reminder tick can interleave with a
concurrent `ReportWorkflowResultAsync`. The existing `runner-lost` path
(`RunnerGrain.cs:411`) snapshots the keys but relies on
`ReportWorkflowResultAsync.Remove(key)` (line 205) as the sole guard.

**Decision:** The timeout scan adopts the snapshot pattern **and** explicitly
reconfirms each entry is still `outstanding` (in memory and/or in the ledger)
before synthesizing. The report path's terminal ledger update is the authority:
once a work is `completed`/`failed` in the ledger, neither timeout nor
`runner-lost` may re-synthesize it. This makes the "超时与 runner-loss 合成互不干扰"
scenario (spec) robust without relying solely on dict removal ordering.

### D5. New `WorkflowOptions` (`Mohist:Workflow`); inject `TimeProvider` + options into `RunnerGrain`

**Decision:**

- Add a `WorkflowOptions` class (`SectionName = "Mohist:Workflow"`,
  `TimeSpan WorkCompletionTimeout = TimeSpan.FromMinutes(30)`), registered via
  `services.Configure<WorkflowOptions>(configuration.GetSection(...))` in
  `MohistServiceRegistration` (mirroring `AgentJobOptions`).
- Inject `TimeProvider` and `IOptions<WorkflowOptions>` into `RunnerGrain`'s
  constructor. All timeout-related time reads (`TakenAt`, scan `now`,
  `FinishedAt`) and the two existing take points (`:505`, `:169`) switch to
  `TimeProvider.GetUtcNow()`.
- `RecoverActiveWorkflowWorkAsync` reloads the original `TakenAt` from the ledger
  instead of `DateTimeOffset.UtcNow` (`:237`), self-healing the clock-reset bug.
  With activation hydration this path is mostly subsumed (the entry is already in
  memory), but it remains as a fallback.

**Rationale:** `TimeProvider.System` is already a registered singleton, so this
is a constructor change only; tests override it with `FakeTimeProvider` via the
silo DI (`GrainTestConfig.cs:63`). The `ConfigService.taskTimeout` config
(runner-process facing, separate system) is deliberately untouched (Non-Goal).

### D6. Testability: expose a `CheckWorkTimeoutsAsync()` test hook

**Decision:** Mirror the `AgentJobGrain.CheckTimeoutsAsync()` pattern — expose a
manual scan method on `IRunnerGrain` that tests call after advancing
`FakeTimeProvider`. Production drives the same scan via `ReceiveReminder`.
Rationale: the test silo uses `UseInMemoryReminderService`
(`GrainTestConfig.cs:42`), so reminders are not deterministically tickable; the
manual hook gives deterministic timeout assertions. The `WorkflowGrainFixture`
does not currently wire a `FakeTimeProvider` — the new RunnerGrain timeout specs
need a fixture (or an extension of it) that injects one, like
`AgentJobGrainFixture`.

## Risks / Trade-offs

- **[False-positive timeout of a healthy long task]** → Generous 30min default
  + configurable (`Mohist:Workflow.WorkCompletionTimeout`); the runner process's
  own liveness judgment remains primary — this is only a safety net. Operators
  raise the config for legitimately long tasks. (The deleted WorkflowGrain
  version used 20min for maxDuration, so 30min is strictly more lenient.)
- **[Double synthesis — timeout + runner-lost both fire for one work]** →
  Snapshot + explicit reconfirm of `outstanding` status before synthesizing
  (D4); terminal ledger update is authoritative; terminal rows never transition
  again.
- **[Reminder service failure leaves orphans undetected]** → The ADO.NET SQLite
  reminder service is already production-wired and is the same mechanism Orleans
  relies on; acceptable. Monitoring of reminder ticks is a follow-up.
- **[Ledger growth — no TTL]** → Explicit Non-Goal; bounded by single-user local
  scale. Growth治理 is a separate follow-up issue. SQLite scale is adequate for
  the target workload.
- **[Reminder minimum-period floor raises detection latency]** → Confirm the
  configured Orleans min period; if it exceeds the desired ~1min scan, either
  tune the floor or accept proportionally higher latency against a 30min
  timeout (low impact).
- **[Activation now does a DB read (hydrate)]** → Bounded by outstanding-work
  count per runner (= slot count, small). Low risk; offset by zero DB reads per
  tick.
- **[Agent-job synthesis channel unconfirmed]** → `AgentJobGrain`'s cross-grain
  fail hook for synthesized timeout/runner-lost must be confirmed during
  implementation; if absent, agent-job synthesis may need a small addition on
  `IAgentJobGrain`.

## Migration Plan

1. **Schema:** Add the `RunnerWorkRow` entity + EF mapping to `MohistDbContext`
   (table `RunnerWorks`, indexes on `RunnerId` + `Status` for the activation
   query). Add a migration (or rely on the dev-startup schema mode — the project
   is in active dev with no version-compat constraint).
2. **Code:** Implement D1–D6 in `RunnerGrain` + new `RunnerWorkStore` +
   `WorkflowOptions` + DI registration. No data backfill is needed.
3. **Existing orphans (e.g. #275's stuck sessions):** Works taken before the
   ledger existed were never inserted, so the new mechanism does **not**
   auto-recover them. Operators manually fail any pre-existing stuck `WorkflowRun`
  (the issue is in active dev; acceptable). Only works taken after deployment are
   covered going forward.
4. **Rollback:** Revert the code; the `RunnerWorks` table becomes unused (no
   destructive impact). Clear any `work-timeout` rows from
   `OrleansRemindersTable` if desired. No irreversible data changes.
5. **Deploy:** `mo update server` (per AGENTS.md — do not `dotnet run`, which
   triggers runner-id drift).

## Open Questions

- **Reminder scan period vs. Orleans min-period floor** — confirm the configured
  minimum and finalize the tick period (proposed ~1min).
- **Agent-job synthesized-fail channel** — confirm the exact
  `IAgentJobGrain` hook to fail an agent-job work on timeout/runner-loss (D3); add
  one if absent.
- **Unified in-memory record vs. keeping the two existing dicts** — recommended
  unification (D3); final shape (e.g. preserving `WorkItem`/`Dispatch` on the
  workflow side) is an implementation detail to settle when writing the code.
- **Whether `RecoverActiveWorkflowWorkAsync` is still needed at all** given
  activation hydration now pre-populates the in-memory set — likely retained as a
  narrow fallback, but verify it isn't dead code after the change.
