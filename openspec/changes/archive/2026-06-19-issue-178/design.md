## Context

The Epic module is an anemic model. There is no `Epic/Domain/` layer: `EpicGrain` (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs`, 200 lines) holds all business logic and operates directly on `EpicRow` + `EpicIssueRow` via `IDbContextFactory<MohistDbContext>`. Status and priority are bare magic strings (`"active"`/`"done"`/`"closed"`, `"p0"`..`"p4"`). The locked invariants live as procedural branches inside the grain:

- **Terminal guard + mark-done readiness + close side effect** all sit in `EpicGrain.SetStatusAsync` (lines 98-131). `IsTerminal`/`IsCompleted` are duplicated between the grain and the static `EpicProgress`.
- **The close→unlink-all side effect** (lines 118-125) is an invisible DB mutation with no domain representation — it caused the Status/Health confusion bug and hides what close actually does.
- `EpicProgress` (`Epic/Services/EpicProgress.cs`, 23 lines) is a reasonable pure projection but mixes two concerns: terminality (an Epic-status concept) and completion (an issue-status concept), both expressed as string literals.

The reference structure to mirror is `Issue/Domain/`: `Issue.cs` (partial aggregate root, private state fields + `RecordEvent`/`PendingEvents`/`ClearPendingEvents`), `Issue.Transitions.cs` (each transition = precondition guard + state change + `RecordEvent`), `IssueStatus.cs` (enum), `IssuePriority.cs` (`readonly record struct` + `From()` + `Default`), `Events/IssueEvent.cs` (`public union IssueEvent(...)` + per-event `sealed record`s). `IssueGrain` demonstrates the target grain shape: load cached domain object → call domain method → `SaveXxxAsync` (persist + publish pending events). The `Issue.Start(undeliveredPrerequisites)` pattern is the model for `Epic.MarkDone(undeliveredLinkedNumbers)`: external state computed by the grain is passed *into* the transition as a parameter, keeping the domain free of DB/IO.

**Constraint**: behavior-preserving. DTOs (`Epic/Services/EpicDtos.cs`), API routes, and persistence shape (`EpicRow`/`EpicIssueRow` columns, `IDbContextFactory`) are unchanged. Epic does **not** adopt `IssueGrain`'s `IStateStore<T>`/event-store machinery — that is a Non-Goal (no event sourcing / persistence redesign). The domain layer is extracted *in front of* the existing DbContext persistence.

Stakeholders: upcoming issues #173 (Paused), #177 (auto-done), #179 (close unlink refinement), #171 (read-side ordering, reuses `EpicPriority`).

## Goals / Non-Goals

**Goals:**
- Introduce `Epic/Domain/` mirroring `Issue/Domain/`: `Epic` aggregate root (partial), `Epic.Transitions.cs`, `EpicStatus` enum, `EpicPriority` value object, `Events/EpicEvent.cs`.
- Encapsulate the locked invariants (state machine, mark-done readiness, no-duplicate links, terminal refusal) inside `Epic.Transitions`, replacing magic strings with types.
- Thin `EpicGrain` to load → call domain method → persist (EpicRow mapping), with the close→unlink side effect driven by the `EpicClosed` event into grain persistence (explicit and describable).
- Refactor `EpicProgress` into a pure projection whose `IsTerminal`/`IsCompleted` are built on the typed model, eliminating Status/Health confusion at the source.
- Preserve all observable behavior (transitions, guards, exceptions, side effects) and all API/DTO contracts; existing Epic tests including `EpicApiSpecs` pass unchanged.

**Non-Goals:**
- No Paused status (#173), no auto-done (#177), no close-unlink *semantics* change (#179 — close still unlinks here).
- No Epic DTO / API route / contract changes.
- No event-sourcing / persistence-model redesign; `EpicRow`/`EpicIssueRow` remain the persistence model. Epic does not adopt `IStateStore<T>`.
- No cross-epic uniqueness (I4) refinement — stays in the application layer (grain DB query), #179 owns it.
- No issue-side domain changes; no read-side/presentation work (#171).

## Decisions

### D1: `Epic` aggregate root owns its linked-issue set, materialized by the grain
The grain loads `EpicRow` **and** its `EpicIssueRow` list, maps both into the `Epic` aggregate (identity + private status/priority/title/description fields + an internal link set). This lets invariant I3 (no duplicate links) be enforced inside `LinkIssue` on the aggregate and lets `EpicIssueLinked`/`EpicIssueUnlinked` be recorded as domain events.
- **Alternative**: keep links out of the aggregate and check duplicates in the grain (status quo). Rejected — it leaves I3 undescribable and prevents clean `#179` refinement later.
- **Trade-off**: the grain must load two tables per command. Acceptable — Epic is low-volume and the same read already happens today.

### D2: External state passed into transitions (`Issue.Start` pattern), not DB access in the domain
`MarkDone(IReadOnlySet<int> undeliveredLinkedNumbers)` takes the undelivered set computed by the grain (which queries linked issue statuses via `BuildLinkedIssueDtosAsync`/`EpicProgress`). The transition only guards: non-terminal **and** empty undelivered set, else `EpicNotReadyToMarkDoneException(epicId, count)`. This keeps the domain pure (no `IDbContextFactory` in `Domain/`).
- **Alternative**: inject a readiness checker interface into the domain. Rejected — adds indirection and splits the single completion judgment ("`link` completed = issue status `done|completed`, health ignored") that must live in exactly one place.
- **Completion judgment固化**: `EpicProgress.IsCompleted(LinkedIssueDto)` remains the single source; `EpicProgress.Build` (now consuming typed `EpicStatus`) feeds both the read projection and the grain's readiness query, unchanged in semantics.

### D3: `EpicStatus` enum + `EpicPriority` value object, mirroring `Issue` types
`EpicStatus { Active, Done, Closed }` (Paused is #173, not added here). `EpicPriority : readonly record struct` with `From(string?)` + `Default = "p2"`, same shape as `IssuePriority`. The grain maps `EpicRow.Status`/`Priority` (still strings in the row) ↔ typed values at load/save. Magic strings no longer appear in domain code.
- **Alternative**: keep `Status` as a string in the domain. Rejected — the whole point is type-replacing-magic-strings; a typed enum also makes the terminal refusal (`Active→Done`/`Active→Closed` only) expressible as a switch.

### D4: `EpicEvent` union + close side effect driven by `EpicClosed`
Mirror `IssueEvent`: `public union EpicEvent(EpicCreated, EpicUpdated, EpicPriorityChanged, EpicIssueLinked, EpicIssueUnlinked, EpicStatusChanged, EpicClosed)` with one `sealed record` per event. `Close()` records `EpicClosed`. The grain, on `SaveAsync`, drains `PendingEvents`; **when it sees `EpicClosed`, it removes all `EpicIssueRow` for the epic** (the preserved unlink-all side effect, now explicit and event-driven). This matches AC: "close 的解绑通过 `EpicClosed` 事件驱动、显式可述".
- **Alternative**: keep the unlink as a direct call in `Close()` (domain triggers persistence). Rejected — the domain must stay IO-free; the event is the contract between domain decision and grain persistence, exactly as `IssueGrain.PublishIssueEventsAsync` drains pending events post-save.
- **Scope guard**: only `EpicClosed` carries a grain-level persistence side effect; other events are recorded but their grain handling is just row mapping (no extra IO). Event publishing to an event bus is **not** introduced here (Non-Goal: no event sourcing) — `PendingEvents` is drained locally to drive the close unlink and is otherwise discarded in this issue.

### D5: Exceptions relocate to `Domain/`; grain shrinks to load→call→persist
`EpicLifecycleExceptions.cs` moves from `Grains/` to `Domain/` (namespace `Mohist.Server.Epic.Domain`). `EpicGrain` loses `IsReadyToMarkDoneAsync`/`CountUndeliveredAsync`/inline guards — those become a single readiness computation feeding `MarkDone(undelivered)`. The grain's per-command body becomes: load rows → materialize `Epic` → call transition → map back to rows + apply `EpicClosed` unlink → `SaveChangesAsync`. `EpicProgress.Build`/`IsCompleted` stay in `Services/` and are consumed by both the read path and the grain's undelivered-set computation.

## Risks / Trade-offs

- **[Transfer/guard regression silently breaks mark-done/close]** → Mitigation: the existing `EpicApiSpecs` and Epic unit tests are the regression net; they must pass unchanged. Add focused domain-level tests on `Epic.Transitions` (terminal refusal, mark-done guard, link dedup) since the grain no longer holds that logic. The risk is why the issue is rated medium.
- **[Close unlink moves from direct DB call to event-driven persist]** → Mitigation: `EpicClosed` handling is the only event with a persistence side effect; covered by the existing close-then-list test (links gone after close). If the event is dropped, the test fails loudly.
- **[Grain loads two tables per command]** → Mitigation: low-volume aggregate; same data already read today. No new query cost beyond coalescing the existing reads.
- **[Two `IsTerminal`/`IsCompleted` definitions during transition]** → Mitigation: delete the grain-level string `IsTerminal` and route through `EpicStatus`/`EpicProgress`; avoid leaving both to prevent the original confusion from recurring.

## Migration Plan

No data migration, no schema change, no API contract change — purely a code-internal refactor within `packages/server/src/Mohist.Server/Epic/`. Rollout is the normal build + deploy of the server package.

- **Order**: (1) add `Epic/Domain/` (types, transitions, events, relocated exceptions) with unit tests; (2) refactor `EpicGrain` to consume the domain; (3) refactor `EpicProgress` to consume `EpicStatus`; (4) delete dead grain logic.
- **Verification**: `dotnet build` + `dotnet test` for the server test project; in particular `EpicApiSpecs` must pass without modification. Manual smoke: create → link → mark-done (blocked then ready) → close (links removed) → list.
- **Rollback**: pure revert of the Epic/ changes; no DB state to unwind.

## Open Questions

- Should `EpicEvent` reuse the same source generator as `IssueEvent`'s `public union ...` syntax, or be a plain sealed-record hierarchy? Default: mirror `IssueEvent` exactly (same generator) for consistency — confirm the generator is available to the Epic namespace without new wiring.
- Whether to drain `PendingEvents` to the bus for the non-close events now or leave that entirely to a future event-sourcing issue. Default: **not now** (Non-Goal); events are recorded but only `EpicClosed` is consumed locally. Confirm this matches #173/#177 expectations.
