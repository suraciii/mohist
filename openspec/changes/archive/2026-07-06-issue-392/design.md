## Context

The epic aggregate has two terminal states — `done` (delivered) and `closed` (abandoned) — but only `Reopen` exits either, and `Reopen` is an explicit user action. Linking an issue to a terminal epic today takes an "archive-only" branch (`EpicGrain.cs:78-117`): `targetIsTerminal` gates active-membership insertion so the link is recorded without touching state. That design lets `done` drift from reality: once a `done` epic receives a new *open* issue, it still reads `done` even though the milestone clearly has live work. Epic #40 hit exactly this — auto-done after #381/#383/#386 terminated, then #387/#388 linked in and the epic stayed `done`, freezing autopilot.

Verified current state (code):

- **Domain transitions are status-aware for terminal-entry, status-agnostic for link.** `Epic.MarkDone` (`Epic.Transitions.cs:111`) already enforces invariant 2 — it throws `EpicNotReadyToMarkDoneException` when any open linked issue exists, and `EnsureNotTerminal` blocks re-entry. `Epic.LinkIssue` (`Epic.Transitions.cs:146`) only mutates `_linkedIssueNumbers` and is blind to status. `Epic.Reopen` (`Epic.Transitions.cs:134`) is the sole terminal→non-terminal path today (done/closed → idle), and re-claims active memberships at the grain level (`EpicGrain.cs:514-526`).
- **The grain owns the terminal-vs-active branching.** `IsTerminalEpicStatus` (`EpicGrain.cs:843`) is `done or closed`. Both `LinkIssueAsync` (L70-134) and `LinkIssuesAsync` (L136-255) compute `targetIsTerminal` once, skip the cross-aggregate ownership check and the `EpicActiveIssues` insert when it is true, and never re-evaluate epic state after a link.
- **"Open issue" is already defined.** `EpicProgress.IsOpen` = `!IsTerminal`; `IsTerminal` = status `done`/`completed`/`cancelled` (`EpicProgress.cs:48,56`). `ComputeOpenLinkedNumbersAsync` (`EpicGrain.cs:742`) already joins `db.Issues` to classify linked issues. So the grain can ask "is this specific issue open?" with existing machinery.
- **Autopilot handoff already exists as a post-transition step.** `StartAsync` (`EpicGrain.cs:404-407`) and `ResumeAsync` (L439-442) call `TryStartNextAsync` / `ReconcileAfterTerminalInternalAsync` after the status commit. `TryStartNextAsync` (L643-680) selects the next startable open issue and calls `IIssueGrain.StartWorkAsync`. Wake-up can reuse this exact path.
- **State enum and schema are untouched.** `EpicStatus` stays `idle/running/paused/done/closed` (`EpicStatus.cs`); no migration is needed for the status field.
- **API error mapping is per-route.** Single link (`EpicRoutes.cs:58-84`) maps `InvalidOperationException` whose message contains "already belongs" → `409 DUPLICATE_EPIC_MEMBERSHIP`. Batch link (`EpicRoutes.cs:281-339`) only maps not-found. `SetStatusAsync` (L140-170) already maps `EpicNotReadyToMarkDoneException` → `409 EPIC_NOT_READY_TO_MARK_DONE` and `EpicAlreadyTerminalException` → `409 EPIC_ALREADY_TERMINAL`.

Constraints / stakeholders: per `design/architecture.md` the epic aggregate owns its state machine and the grain orchestrates persistence + cross-aggregate calls; issue open/terminal is a *fact* read from the issue aggregate, not stored on the epic. Per `design/testing.md`, epic specs run under SQLite with `TimeProvider` injected and fake grains for `IIssueGrain.StartWorkAsync`. AGENTS.md states active development with no version-compatibility obligation, so a single coordinated semantic change is acceptable over a staged rollout.

## Goals / Non-Goals

**Goals:**

- Make epic `done` mean "no open work right now": linking an open issue to a `done` epic wakes it to `running` in the same commit (single + batch); linking an already-terminal issue leaves it `done`.
- Make `closed` a true terminal ("abandoned") state: any link to a `closed` epic is rejected with a distinct domain exception and a distinct `409` error code, for both single and batch link.
- Re-establish the `EpicActiveIssues` row for the newly linked open issue atomically with the wake-up, honoring the cross-aggregate "an issue belongs to at most one non-terminal epic" uniqueness invariant.
- Hand the woken epic to autopilot so the new open issue advances with no caller-issued `start`.
- Pin invariant 2 (`MarkDone` / auto-done rejected while open linked issues exist) with regression tests as the symmetric close of the loop.

**Non-Goals** (per proposal):

- No change to the five-state enum or any schema/migration.
- No change to autopilot internals (start/pause/resume, serial "at most one in-progress" rule) — wake-up only restores the epic to `running`, from which the existing reconcile path drives.
- No change to auto-done's trigger (open issues exhausted → done); wake-up is purely the reverse direction.
- No change to active-membership uniqueness, `unlink` behavior, or `Reopen`.
- No automated data fix for epic #40 — acceptance only requires confirming a strategy.

## Decisions

### D1. New domain transition `WakeFromDone` (done → running), kept separate from `LinkIssue`.

- `Epic.Transitions.cs` gains `public void WakeFromDone(DateTime? now = null)` requiring `_status == Done`, setting `_status = Running`, `Touch(now)`, recording `EpicStatusChanged("done","running")`. Calling it on a non-done epic throws a new (or reused) `EpicNotDoneException`-style guard so misuse surfaces loudly. The transition targets `running` (not `idle`) because the newly linked open issue is live work.
- `LinkIssue` stays status-agnostic (membership only) and gains a single new guard: `_status == Closed` throws `EpicClosedCannotLinkException` (see D3). It does **not** decide wake-up — the grain does, because "is the linked issue open?" is a cross-aggregate fact the domain does not hold.

Rationale: this mirrors how `MarkDone` / `Reopen` are discrete transitions orchestrated by the grain, and keeps `LinkIssue` pure about membership. Wake-up is a state transition, so it lives beside the other transitions. Event carrier is `EpicStatusChanged(done→running)` — consistent with every other transition that emits `EpicStatusChanged`. We do **not** add a dedicated `EpicWoken` event type: the status-change carrier already records the semantic, the epic event vocabulary stays lean, and no consumer branches on a "woken" fact (they branch on status).

Alternatives considered: **fold wake into `LinkIssue` by passing an `isOpen` flag** (rejected — couples the membership mutation to a cross-aggregate fact and forces every caller to classify; the grain is the only place that can). **Wake to `idle`** (rejected — `idle` does not self-advance, so the epic would stall until a manual `start`; the spec explicitly requires autopilot pickup). **Emit a dedicated `EpicWoken` event** (rejected — no consumer needs it; `EpicStatusChanged` already discriminates).

### D2. Grain orchestrates wake: link → classify → wake → insert active row → hand to autopilot, in one commit.

`LinkIssueAsync` (L70-134) is restructured from a binary `targetIsTerminal` gate to a three-way branch keyed on the *current* epic status:

1. **`closed`** → domain `LinkIssue` throws `EpicClosedCannotLinkException` (D3); grain propagates.
2. **`done`** → domain `LinkIssue` records the link; grain then asks `IsIssueOpenAsync(db, projectId, issueId)` (new helper, reads `db.Issues.Status`, reusing the `EpicProgress.IsTerminal` classification). If open: domain `WakeFromDone(now)`, and the `EpicActiveIssues` row is inserted in the **same** `SaveChangesAsync` (the resulting status is now `running`, i.e. non-terminal). If terminal: no wake, no active row (pure record — the only remaining archive-style case, now restricted to terminal issues).
3. **non-terminal (`idle/running/paused`)** → unchanged: insert active row, no wake.

After a successful wake commit, `LinkIssueAsync` tail-calls `TryStartNextAsync(...)` (the existing autopilot selection at L643-680) so the just-linked open issue is started with no caller `start`. This mirrors `StartAsync`'s post-commit `TryStartNextAsync` (L404-407).

The cross-aggregate ownership check (`GetActiveMembershipOwnerAsync`) is **hoisted to run before any wake**, for both `done`+open and non-terminal targets. Today it is skipped for all terminal targets; once a done epic wakes it becomes a non-terminal owner, so the uniqueness invariant must be enforced before the active row is inserted. The `DbUpdateException` catch-or-reclassify path (L123-132) is preserved for the racing-claim case.

Rationale: keeping the wake atomic with the link + active row in one transaction satisfies the "fails → epic stays `done`, retryable as a whole" requirement (spec: "Wake-up that fails to persist rolls back the status change"). Reusing `TryStartNextAsync` avoids a second reconcile mechanism.

Alternatives considered: **emit an event that triggers `ReconcileAfterTerminalAsync`** (rejected — adds async indirection and a hidden event-dependency for a synchronous user action; direct `TryStartNextAsync` matches `StartAsync`). **Insert active row for *all* pre-existing links on wake** (rejected — `Reopen` does that because it is an explicit bulk revive; wake is scoped to the single new open issue, and pre-existing links in a `done` epic were already terminal, so they have no active work to reclaim).

### D3. `closed` link rejection is a domain rule, surfaced as a distinct 409.

- `EpicLifecycleExceptions.cs` gains `EpicClosedCannotLinkException : InvalidOperationException` (carrying `EpicId`).
- `Epic.LinkIssue` (L146) gains a leading guard: `if (_status is EpicStatus.Closed) throw new EpicClosedCannotLinkException(Id);` before the duplicate check. This puts the rule in the aggregate, where every other status-rule lives.
- `EpicRoutes.cs` single-link route (L73-82) adds a `catch (EpicClosedCannotLinkException ex)` → `409 Conflict` with code `EPIC_CLOSED_CANNOT_LINK`, alongside the existing `DUPLICATE_EPIC_MEMBERSHIP` arm.
- Batch link: `LinkIssuesAsync` checks `row.Status == closed` **once, before the loop**, and throws `EpicClosedCannotLinkException`; the batch route's catch maps it to the same `409 EPIC_CLOSED_CANNOT_LINK`. No per-item outcomes are produced for a closed target, per spec ("rejected as a whole").

Rationale: a domain invariant ("closed is abandoned; nothing can be linked") belongs in the aggregate, not in the grain or route. The distinct error code lets callers distinguish "closed, reopen first" from "already a member" — both are 409 but mean different things.

Alternatives considered: **enforce in the grain only** (rejected — the rule would be invisible to any future direct-domain caller and inconsistent with where `MarkDone`/`Reopen` rules live). **Treat `closed` the same as `done` (wakeable)** (rejected by the issue body — `closed` means "abandoned milestone", and a single stray link resurrecting it is semantically wrong; the issue's default is rejection, and the product voice backs it).

### D4. Batch wake fires on the first open link and flips the in-memory status for the rest of the loop.

`LinkIssuesAsync` (L136-255) restructures symmetrically:

- `closed` → reject before the loop (D3).
- The current `targetIsTerminal` snapshot (L149) is replaced by a per-item decision based on the **live** `row.Status`, because `MapToRow(domain, row, now)` updates `row.Status` in memory after each commit. Concretely, each item: if `row.Status == done` and the item is open → wake in that item's commit (sets in-memory `row.Status = running`); subsequent items in the same batch then see a `running` epic and take the normal non-terminal path. If every linked item is terminal, `row.Status` stays `done` for the whole batch.
- The first open link thus wakes exactly once; later open links in the same batch are plain non-terminal links. This satisfies "batch containing at least one open issue wakes the done epic to running" and "only-terminal batch stays `done`".
- Ownership check and active-row insert follow the same rules as single link per item.

Rationale: the batch already persists per-item (a single failure must not roll back later successes). Re-evaluating status per item from the in-memory `row` (which each commit refreshes via `MapToRow`) makes wake-once behavior fall out naturally without a pre-pass. `TryStartNextAsync` is invoked once after the loop if the epic ended up `running` and was `done` at entry.

Alternatives considered: **pre-classify the whole batch, wake once up front** (rejected — requires two passes and a synthetic status mutation before any link is durably persisted, breaking the per-item atomicity guarantee). **Wake on every open item** (harmless but wasteful — `WakeFromDone` would throw on the second call once status is `running`; gating on `row.Status == done` avoids that).

### D5. Invariant 2 is already implemented — pin with tests, no code change.

`Epic.MarkDone` (L116) throws `EpicNotReadyToMarkDoneException` when `openLinkedNumbers.Count > 0`; `TryAutoMarkDoneAsync` (L693) and `ReconcileAfterTerminalInternalAsync` (L609) are no-ops when `open.Count > 0`. No production change is needed. New spec scenarios in `EpicLifecycleSpecs.cs` / `EpicAutoDoneSpecs.cs` freeze this as the symmetric pin to wake-up.

### D6. Wake-up requires no event-subscriber change.

The wake emits only `EpicStatusChanged(done→running)`. Existing epic-event consumers (`EpicAutoDoneHandler`, inbox projection, reconcile service) key off issue-terminal events and epic status literals; none of them fire *into* wake, and none branch on a "woken" fact. The auto-done direction (issue terminal → epic reconcile) is unchanged. No new subscription is introduced.

## Risks / Trade-offs

- **[BREAKING: archive-link to `closed` removed]** Any caller that linked issues to a `closed` epic as a record-keeping gesture now receives `409 EPIC_CLOSED_CANNOT_LINK`. -> Mitigation: distinct error code + message naming `Reopen` as the exit; documented in the issue body as expected/intentional breakage. The `done`-with-terminal-issue archive path is preserved.
- **[Batch partial wake]** Per-item persistence means a batch to a `done` epic that wakes on item 1 then fails on item 3 leaves the epic `running` with items 2 linked and 4 unlinked. -> Mitigation: this is correct behavior (the epic *does* have open work); the failed item returns a conflict/not-found outcome and the caller can retry just that item. Acceptable per the existing batch semantics that "a single failure does not roll back later successes."
- **[Ownership check reordering]** Hoisting `GetActiveMembershipOwnerAsync` to run before a done-epic wake (instead of skipping it for terminal targets) changes the order in which the uniqueness invariant is enforced. -> Mitigation: the wake path now needs the check to be safe; the `DbUpdateException` catch-all for racing claims (L123-132) is retained as the second line of defense.
- **[Autopilot handoff inside `LinkIssueAsync`]** Calling `TryStartNextAsync` from within link makes link potentially invoke `IIssueGrain.StartWorkAsync`, which is a new cross-aggregate call on the link path. -> Mitigation: `TryStartNextAsync` already swallows and logs `StartWorkAsync` failures, leaving the epic `running-but-idle` for the next reconcile retry — wake-up does not fail if the issue cannot start.
- **[Historical epic #40 stays `done`]** Wake triggers only on *new* links; #40 already has #387/#388 linked. -> Mitigation: see Migration Plan — operator revives via `unlink`+`relink` or `reopen`+`start`. No silent mass state rewrite.

## Migration Plan

1. **Deploy order:** single coordinated release (server only). The change is server-side domain + grain + route; CLI and web require no change (`mo epic link` already routes through the same endpoints; the status badge already reflects grain output).
2. **No schema migration.** `EpicStatus` and all tables are unchanged; no `EpicActiveIssues` backfill is needed because wake inserts rows only for newly linked open issues going forward.
3. **Historical epic #40 (and any sibling `done` epics with open linked issues):** wake does not retro-fire. Confirmed handling strategy (satisfies the acceptance item without an automated fix): an operator revives such an epic by either (a) `mo epic unlink <epic> <issue>` then `mo epic link <epic> <issue>` on each open issue — the relink triggers wake; or (b) `mo epic reopen <epic>` then `mo epic start <epic>`. Both use existing commands. No data-corruption risk: the linked rows are intact, only the status is stale.
4. **Rollback:** revert the code change. `done` epics return to archive-link semantics; `closed` accepts links again. No data written by the forward change needs cleanup — wake only inserts standard `EpicActiveIssues` rows for issues that are genuinely linked and open, which is the correct invariant-state for a non-terminal epic.

## Open Questions

- Should wake-up emit a dedicated `EpicWoken` event for timeline readability, or is the `EpicStatusChanged(done→running)` carrier sufficient? Default decision: carrier is sufficient (D1); revisit if the web timeline wants to render "revived" distinctly from "status changed".
- For epic #40 specifically, does the team prefer the `unlink`+`relink` recipe or an explicit `reopen`+`start` as the documented recovery? Both work today; pick one for the issue comment / docs note. (Acceptance only requires confirming a strategy, which this design does.)
