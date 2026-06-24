## Context

Today an epic only reaches `done` when a user explicitly invokes "Mark Done" (`EpicGrain.SetStatusAsync("done")` → `Epic.MarkDone`, Epic.Transitions.cs:94). The readiness check already exists: `MarkDone` throws unless every linked issue is complete (`EpicProgress.IsCompleted`, i.e. status `done`/`completed`). The user voice (#177) is that this manual step is redundant — once the last issue completes, the epic should go `done` by itself.

Relevant existing machinery:

- **Issue completion write-path**: `IssueGrain.CompleteWorkAsync` → `Issue.Complete` sets `IssueStatus.Done` and records `IssueWorkCompleted` (Issue.Transitions.cs:149). `SaveIssueAsync` persists **before** publishing (IssueGrain.cs:520→521), so the row is durable when downstream handlers run.
- **Eventing**: issues emit typed CloudEvents on an in-memory bus (`InMemoryEventBus`) + append-only `IEventStore` (`PublishIssueEventsAsync`, IssueGrain.cs:524). Type for completion: `com.mohist.issue.work-completed` (`IssueEventSerializer.BusType`). Handlers are auto-discovered via `[Subscription]` + `AddCloudEventHandlersFromAssembly` (MohistSiloRegistration.cs:36). **No Orleans streams are configured** — the codebase deliberately uses this CloudEvent bus, and only one production handler exists today (`EventBridge` → SignalR).
- **Link table**: `EpicIssueRow` has `IX_EpicIssues_ProjectId_IssueId` (InitialSchema migration:357) but no reverse `issueId → epicId` query method exists — only `epicId → issues`.
- **Grain coupling**: `IssueGrain` and `EpicGrain` currently never reference each other; they share only the `EpicIssues` table.
- **#173 interaction**: `paused` is non-terminal; `MarkDone` explicitly rejects paused (`EpicPausedCannotMarkDoneException`, Epic.Transitions.cs:96). `Resume` (Epic.Transitions.cs:84) only flips paused→active.

Constraints: no DB schema change is required (status + links already persisted); no Web/UI change (board card reflects `done`); must remain eventually consistent with the in-memory bus's at-most-once delivery + swallow-on-failure publish path.

## Goals / Non-Goals

**Goals:**
- Auto-transition `active → done` when the last linked issue completes, reusing the existing readiness check unchanged.
- Explicitly skip `paused` epics; re-evaluate readiness on `resume`.
- Keep manual "Mark Done" working identically for edge cases.
- Preserve today's `cancelled`-issue behavior (no auto-done) with zero regression.
- Idempotent and race-tolerant: duplicate/out-of-order completion signals converge to the correct epic state without errors.

**Non-Goals:**
- Auto-`close` epics, undo of auto-done, done notifications (separate issues).
- Changing readiness semantics (e.g. treating `cancelled` as complete).
- UI/visualization changes (handled by #171's card copy).
- Introducing Orleans streams or a durable outbox (see Open Questions).

## Decisions

### D1 — Event-driven: subscribe to `com.mohist.issue.work-completed`
Add a new `[Subscription(Type = "com.mohist.issue.work-completed")]` handler (e.g. `EpicAutoDoneHandler : ICloudEventHandler`). On the event it (a) resolves the owning epic via a new reverse `issueId → epicId` lookup, then (b) invokes a new `IEpicGrain` method to evaluate and apply auto-done. The event already carries `projectid`/`issueid`/`issueno` in CloudEvent extensions (IssueGrain.cs:529) — no payload change needed.

**Rationale**: reuses the established, auto-discovered CloudEvent-bus pattern; keeps the write side-effect off read paths; no new infra (streams/outbox) introduced.

**Alternatives considered**:
- *Orleans streams*: rejected — no stream provider is configured and the codebase intentionally avoids them; adding one is out of proportion for a single transition.
- *Direct grain-to-grain call from `IssueGrain.CompleteWorkAsync`*: rejected — couples two aggregates that today share only a table, and `CompleteWorkAsync` is also invoked from reconciliation paths where injecting epic logic is awkward. The event bus is the decoupling seam already wired for this.

### D2 — New `IEpicGrain.AutoMarkDoneIfReadyAsync()` method (idempotent)
Introduce a dedicated grain method rather than reusing `SetStatusAsync("done")`. Reason: `SetStatusAsync` → `MarkDone` **throws** when the epic is already terminal (`EnsureNotTerminal`, Epic.Transitions.cs:98) or paused (line 96). The spec requires terminal/paused states to be **no-ops**, not errors.

`AutoMarkDoneIfReadyAsync` semantics:
1. Load epic + links, materialize domain.
2. If status is terminal (`done`/`closed`) or `paused` → return current DTO, no transition.
3. If `active` → compute undelivered linked numbers (reuse `ComputeUndeliveredLinkedNumbersAsync`); if empty, call `domain.MarkDone(...)` and persist; otherwise no-op.

This keeps the readiness definition exactly the existing one (`undelivered.Count == 0`) and centralizes the auto/manual path through the same domain transition.

**Alternative**: modify `MarkDone` to no-op instead of throw. Rejected — `MarkDone`'s throws are a meaningful invariant for the **manual** path (user-facing errors); only the auto path should swallow them.

### D3 — Reverse lookup `issueId → epicId` in `EpicQuerier`
Add a query (e.g. `GetEpicIdForIssueAsync(projectId, issueId)`) hitting the existing `IX_EpicIssues_ProjectId_IssueId` index. The handler uses it to resolve the target grain. Returns null when the issue isn't linked to any epic (handler no-ops).

**Alternative**: denormalize `epicId` onto the issue row. Rejected — duplicates data already in `EpicIssues` and risks drift; the indexed lookup is cheap and authoritative.

### D4 — `Resume` re-evaluates readiness
In `EpicGrain.ResumeAsync`, after a successful paused→active transition, call `AutoMarkDoneIfReadyAsync` logic inline (same turn, same grain activation — no second event needed). This satisfies "resume-then-auto-done" without depending on a fresh issue-completion event arriving. Guarded by the same idempotent checks.

### D5 — Safety net via existing reconciliation, not a new outbox
Because `PublishIssueEventsAsync` swallows publish failures (try/catch→log, IssueGrain.cs:555), a missed `work-completed` event would leave a ready epic in `active`. Rather than introduce a durable outbox (large scope), rely on a lightweight sweep: piggyback on the existing issue-workflow reconciliation surface (`ReconcileWithWorkflowTerminalStateAsync`, IssueGrain.cs:406) style — a periodic/triggered epic readiness reconciliation that calls `AutoMarkDoneIfReadyAsync` for epics whose progress reports `ReadyToMarkDone` but status ≠ `done`. (Exact trigger cadence is an Open Question; the auto-done event handles the common path.)

## Risks / Trade-offs

- **[Missed completion event (bus swallowed / process crash before handler)]** → Mitigation: D5 reconciliation sweep self-heals ready-but-not-done epics; manual "Mark Done" remains as a last-resort escape hatch (explicit non-goal removal).
- **[Duplicate `work-completed` events delivered]** → Mitigation: `AutoMarkDoneIfReadyAsync` treats terminal as no-op (D2); duplicate signals are absorbed without state change or error.
- **[Race: issue completed while epic grain simultaneously transitioning]** → Mitigation: Orleans single-threaded activation serializes calls on `EpicGrain`; the handler always re-reads current epic+links inside the same turn, so it sees the latest status. `IssueGrain` persists before publishing, guaranteeing the completed issue is visible in the DB when the epic recomputes undelivered numbers.
- **[Paused epic silently skipping auto-done, then user forgets to resume]** → Mitigation: by design (paused = don't advance). Acceptable; `resume` re-evaluates automatically (D4).
- **[In-memory bus = at-most-once, not durable]** → Trade-off accepted for this issue; documented as a future-hardening candidate (Open Questions).
- **[New grain method widens the public `IEpicGrain` surface]** → Trade-off: preferable to overloading `SetStatusAsync` with a mode flag, which obscures the throw-vs-no-op contract.

## Migration Plan

1. Implement D1–D4 (handler, grain method, reverse lookup, resume hook). All purely additive; existing manual `SetStatusAsync("done")` behavior and errors are unchanged.
2. Add Fake-based tests (no real DB/OS/external systems per AGENTS.md): all-complete→auto-done; partial→no-op; paused excluded; resume→auto-done; cancelled-issue no-regression; duplicate event idempotent; terminal no-op; manual Mark Done still works.
3. Deploy: no schema migration (status/links already persisted), no config change. New handler is auto-discovered on next silo start.
4. **Rollback**: remove the handler + grain method; epic state already written as `done` is valid and needs no repair (it is the same transition the manual path produces). Paused/manual flows are untouched.

## Open Questions

- **Reconciliation cadence (D5)**: timer-driven sweep, or triggered off an existing periodic job (e.g. alongside issue reconciliation)? Decide during implementation based on what periodic surface is cheapest to reuse.
- **Should `AutoMarkDoneIfReadyAsync` record a distinct `EpicStatusChanged` reason/metadata (auto vs manual)?** Not required for #177 (no UI/notifications in scope), but could aid future observability. Default: reuse existing `EpicStatusChanged` event unchanged.
