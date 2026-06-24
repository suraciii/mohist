## Context

Epics today (`packages/server/src/Mohist.Server/Epic/`) are organizational groupings of issues with a minimal lifecycle: an epic is born `active`, can be `pause`d/`resume`d, and auto-transitions to `done` when all linked issues are delivered (`#177`, shipped). Crucially, **epics never start issues** — a developer must manually `mo issue start` each next issue after the previous one completes. This stalls throughput at every hand-off, especially across async/overnight runs.

Relevant current state:

- `EpicStatus` enum: `Active, Paused, Done, Closed` (`Epic/Domain/EpicStatus.cs`).
- `Epic.Transitions.cs`: `Pause()` (any non-terminal → Paused), `Resume()` (Paused → Active), `MarkDone()`, `Close()`. `Resume` is the only path that currently re-runs `TryAutoMarkDoneAsync`.
- `EpicGrain` (`Epic/Grains/EpicGrain.cs`) owns all state mutations and DB persistence; exposes `PauseAsync`, `ResumeAsync`, `SetStatusAsync`, `AutoMarkDoneIfReadyAsync`. `ResumeAsync` already calls `TryAutoMarkDoneAsync` after resume.
- `EpicAutoDoneHandler` (`Events/Subscriptions/EpicAutoDoneHandler.cs`) subscribes to **only** `com.mohist.issue.work-completed` and calls `AutoMarkDoneIfReadyAsync`.
- `EpicReconciliationService` (`Events/Hosting/`) is a daily safety-net sweep that re-invokes `AutoMarkDoneIfReadyAsync` on `active` epics.
- `EpicProgress` (`Epic/Services/EpicProgress.cs`) already computes the read-model: completed/undelivered split, `SelectStartableNext` (private — picks highest-priority `CanStart && StartBlocker is null` issue, returns null if any `in_progress` exists), `BuildNextIssueReason`, blocked/active in-progress sets.
- HTTP routes (`Api/EpicRoutes.cs`) already expose `POST /{id}/pause`, `/resume`, `/done`, `/close`. There is **no** `/start` and no concept of "self-driving vs. not".
- CLI (`MohistCliCommands.Epic.cs`) mirrors the API: `done`, `close`, `pause`, `resume`, etc., but no `start`.
- Web (`pages/epic-detail/ui/EpicDetailPage.tsx`): header today shows Pause (when not done/closed/paused) + Mark Done + Close. The "Next Issue" card still renders a one-shot `epic-detail-next-start` Start button (`EpicDetailPage.tsx:617`). Each linked-issue row retains an inline `linked-issue-start` (`:119`). `usePauseEpic`/`useResumeEpic` exist in `entities/epic/api/queries.ts`; no `useStartEpic`.

Constraints:

- Workflow core must remain unaware of issue/epic (dependency direction preserved — `design/architecture.md`).
- Single-runner capacity today = 1 → serial invariant "at most one in-progress linked issue".
- Tests must use fakes, never real external systems (`AGENTS.md`); C# relies on `TreatWarningsAsErrors` as lint.

## Goals / Non-Goals

**Goals:**
- Make an epic a self-driving entity: `mo epic start` begins autonomous progression; terminal events on linked issues (`done` **and** `cancelled`) advance the next startable issue or auto-close the epic.
- Introduce `idle` (not self-driving, replaces `active`) and `running` (self-driving) states with a clean state machine and idempotent `start`/`pause`/`resume`.
- Surface lifecycle actions in the Web UI header by state; make the "Next Issue" card information-only; keep per-row inline Start for the manual journey.
- Preserve all existing behavior for non-self-driving epics (auto-done readiness still applies to `idle`).
- Keep progression logic inside `EpicGrain` so Orleans turn-based concurrency makes Pause race-safe with terminal-event-triggered advancement.

**Non-Goals:**
- Multi-runner parallelism (N>1). Modeled as a capacity policy to leave room, but v1 ships N=1.
- Auto-wake when an **external** prerequisite (outside this epic) completes — user can `resume` to re-evaluate.
- Auto-retry/repair of failed issues (they stay `in_progress`/blocked; human intervenes).
- A configurable cancel policy (cancel always means "skip").
- A new epic state for "blocked/needs-attention" — serial invariant + dashboard observability suffice.

## Decisions

### D1. Rename `Active` → `Idle`; add `Running`. Five-state machine.

```
create → idle
idle    --Start-->  running   (+ TryStartNext)
running --Pause-->  paused
paused  --Resume--> running   (+ reconcile: auto-done or TryStartNext)
running --all delivered (#177)--> done
any non-terminal --Close--> closed
```

- `Active` becomes `Idle` semantically: "exists, not yet started". `Running` is the self-driving state.
- `Pause` is valid **only** from `Running` (an `idle` epic has nothing to halt). `Resume` is valid only from `Paused` → `Running` (no longer → `Active`).
- `MarkDone`/auto-done readiness applies to **both** `idle` and `running` (any non-paused, non-terminal). This preserves `#177` behavior for manually-advanced `idle` epics.

**Alternatives considered:**
- *Keep `Active` and add `Running`*: rejected — `Active` would be ambiguous ("active but not running?"). The rename is cleaner and the migration is a one-shot data fix.
- *Add a separate `blocked` state*: rejected (Non-Goal) — serial invariant + `nextIssueReason` already express it.

### D2. Own progression in `EpicGrain`; unified `ReconcileAfterTerminalAsync`.

Add a single grain method `ReconcileAfterTerminalAsync()` that supersedes `AutoMarkDoneIfReadyAsync`:

```
ReconcileAfterTerminalAsync():
  if terminal or paused: return        // paused excluded from auto-done + advancement
  if readiness satisfied: MarkDone; return
  if status == Running: TryStartNext()
  // idle: do NOT advance (not self-driving)
```

`TryStartNext()`:
1. Load linked issue DTOs.
2. If any linked issue is `in_progress` → return (serial slot occupied; covers the failed/blocked case).
3. `next = EpicProgress.SelectStartableNext(undelivered)`. If null → return (running-but-idle; `nextIssueReason` already computed by the read model).
4. Call `_grains.GetGrain<IIssueGrain>(...).StartWorkAsync()` for the selected issue.

`StartAsync()` (new grain method) = transition `Idle→Running` + `TryStartNext()`.
`ResumeAsync()` = transition `Paused→Running` + `ReconcileAfterTerminalAsync()`.

Because all of this runs on the `EpicGrain` scheduler, a `PauseAsync` call is serialized strictly before/after any in-flight reconcile — Pause wins the race (spec: "Pause wins over an in-flight terminal event").

**Alternatives considered:**
- *Free-floating saga/orchestration service*: rejected — loses turn-based serialization, reintroduces races, and duplicates grain state reads.
- *Keep `AutoMarkDoneIfReadyAsync` and add a separate `TryStartNextAsync` handler*: rejected — two event handlers calling two grain methods would still serialize per-grain, but a single reconcile method is simpler, idempotent, and mirrors the existing `EpicReconciliationService` sweep pattern.

### D3. Subscribe to both terminal events from one handler.

Generalize `EpicAutoDoneHandler` to also subscribe to `com.mohist.issue.closed` (cancel). Both subscriptions call the same `ReconcileAfterTerminalAsync`. Rationale (from the issue's Domain Model): both terminal events clear the single in-progress slot the serial rule waits on; cancel **must** trigger re-evaluation or the epic deadlocks on a cancelled in-progress issue.

**Alternatives considered:**
- *Subscribe only to `work-completed`*: rejected — violates AC #4 (cancel of in-progress issue must let epic advance).
- *Two separate handler classes*: rejected — duplicates the CloudEvent extension parsing (`projectid`/`issueid`) and epic lookup; one class with two `[Subscription]` attributes (or a thin shared base) is cleaner.

### D4. Factor `SelectStartableNext` out of `EpicProgress` for shared use.

`EpicProgress.SelectStartableNext` is currently `private`. Promote it (and the cancel-skip + priority ordering it already encodes) to a reusable internal method so the grain's `TryStartNext` and the read-model share identical selection semantics — no drift between "what the dashboard says is next" and "what the grain starts".

**Alternatives considered:**
- *Duplicate the ordering in the grain*: rejected — divergence risk; the issue explicitly calls out shared semantics.

### D5. "At most one in-progress" is a capacity policy, not an aggregate invariant.

`TryStartNext` checks for any `in_progress` linked issue before starting another. This is expressed as a runtime check (capacity N=1) rather than a hardcoded `Epic` aggregate rule, so multi-runner parallelism is a future policy change without domain-model edits.

### D6. Schema migration: `active` → `idle`.

Add an EF Core migration that `UPDATE Epics SET Status = 'idle' WHERE Status = 'active'`. The `EpicStatus` enum gains `Idle`, `Running`; `StatusName`/`ParseStatus` map accordingly. Legacy `active` is treated as `idle` on read for safety (belt-and-suspenders).

Post-migration behavior does not regress: legacy `active` epics were never self-driving, and `idle` epics are not self-driving until explicitly started.

### D7. Idempotency at the domain layer.

`Start()`/`Pause()`/`Resume()` become no-ops (no exception, no event) when the epic is already in the target state. Terminal-state attempts still throw `EpicAlreadyTerminalException` (existing behavior) — but per AC #11 the lifecycle commands should be no-ops on already-target-state. The grain methods catch and return the current DTO rather than erroring, so the API/CLI surface is idempotent while the domain stays strict.

**Alternatives considered:**
- *Make the domain methods themselves no-op on terminal*: rejected — keeps domain invariants loud; idempotency is a UX/API concern handled at the grain boundary.

### D8. HTTP API + CLI: add `start`; keep `pause`/`resume`.

- `POST /api/projects/{projectRef}/epics/{id}/start` → `StartAsync`.
- `mo epic start {id|number}` (mirrors existing `pause`/`resume`/`done` commands).
- `pause`/`resume` endpoints and commands stay; their semantics shift (Pause now requires `running`; Resume targets `running`). Error mapping: `EpicAlreadyTerminalException` → 409 (existing); add 409 for "pause on non-running" via a new `EpicNotRunningException` → `EPIC_NOT_RUNNING`.

### D9. Web UI: header lifecycle actions; demote Next Issue card to info-only.

- Extend `EpicStatus` enum in `entities/epic` with `Idle`, `Running` (rename `Active`→`Idle`).
- Header action area becomes state-driven: `idle` → "Start Epic" (primary); `running` → "Pause"; `paused` → "Resume"; `done`/`closed` → none. The existing Mark Done / Close actions remain.
- Add `useStartEpic` mutation (`POST .../start`) alongside `usePauseEpic`/`useResumeEpic`; invalidate the epic query on success.
- **Remove** the `epic-detail-next-start` button block (`EpicDetailPage.tsx:617` region). The card keeps the "Next Issue" label, the next-issue identity, and `next-issue-reason`.
- **Keep** per-row `linked-issue-start` (`:119`) — supports the manual single-issue journey without committing the epic to autonomy.

## Risks / Trade-offs

- **[Risk] Cancel of in-progress issue deadlocks the epic if the `closed` event is lost** → Mitigation: `EpicReconciliationService` sweep is generalized to call `ReconcileAfterTerminalAsync` (not just `AutoMarkDoneIfReadyAsync`) on `running` epics, recovering from missed events. The sweep already exists for `active`; extend its candidate set to `running`.
- **[Risk] EpicGrain calls IIssueGrain.StartWorkAsync — grain-to-grain call inside a grain method** → Mitigation: this is idiomatic Orleans; the call is fire-and-forget-from-epic-perspective (issue grain owns its own start semantics). If `StartWorkAsync` throws (e.g. start blocker appeared between selection and start), the exception is caught, logged, and the epic remains `running-but-idle`; the next reconcile retry will re-evaluate.
- **[Risk] Schema migration renames a user-visible status value** → Mitigation: one-shot back-fill migration; CLI/Web tests updated; legacy `"active"` still parses to `Idle` defensively.
- **[Risk] Pause-vs-advance race** → Mitigation: D2 — both operations are grain methods on the same `EpicGrain`, serialized by Orleans turn-based execution. No external lock needed.
- **[Risk] Web UI breaks for users mid-flow on upgrade** → Mitigation: any epic previously `active` becomes `idle`; the page header shows "Start Epic" instead of "Pause", which is the intended new default. No data loss.
- **[Trade-off] `idle` epics no longer show "Pause" in the header** → acceptable: an `idle` epic is not advancing, so halting is meaningless. Inline per-issue Start still works on `idle` epics.

## Migration Plan

1. **Schema**: EF migration adding `Idle`/`Running` enum support + `UPDATE Epics SET Status='idle' WHERE Status='active'`. Deploy with the server release.
2. **Server**: ship `EpicStatus` changes, new `EpicGrain.StartAsync`/`ReconcileAfterTerminalAsync`/`TryStartNext`, generalized `EpicAutoDoneHandler` (both terminal events), generalized `EpicReconciliationService`.
3. **API**: add `POST /epics/{id}/start`; update `pause`/`resume` semantics + error codes.
4. **CLI**: add `mo epic start`; update status display strings.
5. **Web**: ship header lifecycle actions, `useStartEpic`, Next Issue card demotion.
6. **Rollback**: revert the deploy. The data migration is one-way (`active`→`idle`); a rollback migration (`idle`→`active`) is trivial if needed, but legacy `active` parsing remains supported, so a partial rollback (code only) is safe even with migrated data.

## Open Questions

- **Q1**: Should `EpicReconciliationService`'s sweep period (currently daily) be shortened for `running` epics, so a missed terminal event recovers faster than 24h? *Lean: yes — use a shorter cadence (e.g. 5–15 min) for `running` candidates, keep daily for `idle`.*
- **Q2**: When `TryStartNext` calls `IIssueGrain.StartWorkAsync`, do we need to pass a `WorkflowProjectContext`? The existing `/issues/{n}/start` route builds one; the grain-to-grain call may need the same resolution path. *To confirm during implementation — likely reuse the existing repo-resolution helper inside `IssueGrain.StartWorkAsync`.*
- **Q3**: Should the Web UI show a confirm modal for "Start Epic" (as it does for Pause), or start immediately? *Lean: immediate — Start is non-destructive and reversible via Pause.*
