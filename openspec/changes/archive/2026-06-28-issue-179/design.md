## Context

Today, closing an epic is destructive: `EpicGrain.ApplyPendingEvents` reacts to the `EpicClosed` domain event by calling `RemoveAllLinkedIssues`, which deletes every `EpicIssueRow` for that epic (`EpicGrain.cs:538-550`). The membership history — "which issues were in this epic" — is lost. Additionally, the cross-aggregate uniqueness invariant ("an issue belongs to at most one epic") is enforced in `EpicGrain.LinkIssueAsync` (`EpicGrain.cs:67-74`) and backed by a **unique DB index** on `EpicIssues (ProjectId, IssueId)` (`MohistDbContext.cs:221`). Together these mean an issue that was in a finished epic can never be re-homed: close wiped the row, and even if it hadn't, the unique index would reject a second row.

Issue #178 (prerequisite, done) already extracted the `Epic` aggregate root with a typed `EpicStatus` (`Idle`/`Running`/`Paused`/`Done`/`Closed`) and transition methods. Notably `Epic.Close()` (`Epic.Transitions.cs:124-132`) is already clean — it only flips status and records `EpicClosed`; the destructive side-effect lives entirely in the grain's event-applier, not the domain. So this change is mostly about removing a grain-level side-effect and making two queries status-aware.

Stakeholders: server epic domain, issue read-model (`IssueQuerier.primaryEpic`), and (read-only) epic progress/list/detail reads. No Web/CLI contract change.

**Constraint discovered during design** (not in proposal): the unique index `IX_EpicIssues_ProjectId_IssueId` (`MohistDbContext.cs:221`, `IsUnique()`) physically prevents an issue from having more than one `EpicIssueRow`. The spec requires an issue to retain its terminal-epic membership *and* acquire a new non-terminal membership on re-homing — i.e. two rows. These are in direct conflict; see Decisions D2.

## Goals / Non-Goals

**Goals:**
- Close an epic without touching its `EpicIssueRow` set (non-destructive close).
- Refine the cross-aggregate invariant to "at most one **non-terminal** epic per issue"; terminal-epic memberships do not block re-homing.
- Make `primaryEpic` projection reflect the issue's non-terminal epic membership (null when only terminal memberships exist).
- Keep epic progress / detail / list reads correct for newly-closed epics that now retain members.
- All changes Fake/test-backed per project rules; no real external systems in tests.

**Non-Goals:**
- Done semantics (terminal, irreversible) — unchanged.
- Close reopen / undo, automatic close — out of scope.
- Paused semantics (#173) — untouched.
- Web/CLI contract changes — none required.
- Backfilling historical closed epics (their link sets were already deleted pre-change) — not pursued.

## Decisions

### D1 — Remove the close → unlink side-effect at the grain, keep the domain clean
Delete the `EpicClosed` branch in `EpicGrain.ApplyPendingEvents` and the now-unused `RemoveAllLinkedIssues` helper (`EpicGrain.cs:532-550`). `ApplyPendingEvents` becomes a no-op drain (kept as the extension point for future events). The domain `Epic.Close()` is unchanged — it already only sets status and records `EpicClosed`.

The `EpicClosed` event is still emitted (audit/future use) but currently has no subscriber; removing the side-effect is safe (verified: `EpicClosedReconcileHandler` subscribes to `IssueClosed`, an issue event, not `EpicClosed`).

- *Alternative considered:* push the "do not unlink" rule into the domain. Rejected — the domain never owned the unlink side-effect (it was grain infrastructure reacting to an event); the destructive write to `EpicIssueRow` is a persistence concern and belongs at the grain. This keeps the #178 boundary intact.

### D2 — Relax the historical-link index and add an active-membership slot (schema change)
The spec's "terminal membership retained + new non-terminal link succeeds" requirement needs **multiple `EpicIssueRow` per issue** (>=1 terminal + exactly 1 non-terminal). The existing `IsUnique()` index (`MohistDbContext.cs:221`) makes that impossible at the historical-link level — the second `db.EpicIssues.Add(...)` would throw on `SaveChangesAsync`.

Decision: drop the uniqueness on `IX_EpicIssues_ProjectId_IssueId` (keep it as a regular non-unique index for link lookups) and add `EpicActiveIssues`, keyed by `(ProjectId, IssueId)`, via a new EF Core migration. `EpicIssues` remains the historical membership table; `EpicActiveIssues` is the current non-terminal ownership slot. The migration backfills `EpicActiveIssues` from existing `EpicIssues` rows whose owning epic status is `idle`/`running`/`paused`, and intentionally skips `done`/`closed` owners so terminal history does not block re-homing.

The "at most one non-terminal epic per issue" invariant is therefore still hard-enforced at the database boundary, while terminal memberships can coexist in `EpicIssues` with a new active slot.

This contradicts the proposal's "No DB schema changes" claim; that claim held only for *existing* rows (already-empty terminal link sets stay valid) but did not account for the re-homing write path. This design surfaces and resolves that gap.

- *Alternative A — partial unique index on a denormalized terminal flag:* add an `OwnerTerminal` bool to `EpicIssueRow` and a SQLite partial unique index `... WHERE OwnerTerminal = 0`. Rejected for this change: it adds a denormalized column that must be flipped for every link row when the owning epic closes (a write amplification we are explicitly trying to avoid). The separate active-slot table provides the same hard uniqueness guarantee without mutating retained terminal history rows on close.
- *Alternative B — "move the slot" on re-homing (delete terminal link, insert non-terminal):* rejected — it contradicts the spec scenario "Re-homing … SHALL NOT reference the terminal epic" while implying the terminal membership remains, and degrades closed-epic history the moment an issue is re-homed.

### D3 — Status-aware duplicate check in `LinkIssueAsync` (active-slot based)
Replace the current "first existing link wins" lookup (`EpicGrain.cs:67-74`, a single `FirstOrDefaultAsync`) with an active-slot lookup against `EpicActiveIssues`, then:
- If the active slot exists and belongs to a different epic -> reject with the existing `InvalidOperationException("Issue already belongs to Epic ...")` (mapped to `DUPLICATE_EPIC_MEMBERSHIP` in `EpicRoutes.cs:79`, unchanged).
- If the active slot belongs to this epic, or an `EpicIssueRow` already links this epic and issue, return idempotently without creating duplicate rows.
- Otherwise, insert the historical `EpicIssueRow`; when this epic is non-terminal, also insert the `EpicActiveIssueRow` slot.

The active-slot query is required because, post-D2, an issue may hold several terminal-epic rows; `FirstOrDefaultAsync` over `EpicIssues` would only inspect one. Terminal-epic owners are absent from `EpicActiveIssues`, so they do not participate in conflict detection.

The check stays at the grain (not the domain `Epic.LinkIssue`): cross-aggregate visibility of other epics' status is a persistence-boundary concern, matching the existing placement. `Epic.LinkIssue` remains responsible only for intra-aggregate idempotency and the per-epic duplicate-number guard.

### D4 — `primaryEpic` projection skips terminal-epic memberships
In `IssueQuerier.cs:1258-1281`, filter the join so `issue.PrimaryEpic` is assigned only from a link whose owning epic is **non-terminal**. Because D3 guarantees at most one non-terminal membership per issue, the loop's "last write wins" naturally resolves to that single non-terminal epic; an issue with only terminal memberships projects `null`. Reuse `EpicProgress.IsTerminal(string)` for the status test to avoid a second definition of "terminal".

- *Alternative:* order links by epic status and pick explicitly. Rejected as unnecessary given the invariant guarantees uniqueness of the non-terminal membership.

### D5 — No change to progression / reconcile paths
`ReconcileAfterTerminalInternalAsync` and `TryStartNextAsync` already short-circuit on `Done`/`Closed` (`EpicGrain.cs:329-332`, `371-372`). Retained links on a closed epic therefore never trigger autonomous advancement. `EpicProgress.Build` reads `EpicIssues` joined to `Issues` and is status-agnostic; closed epics will now (correctly) show their preserved member list and progress instead of an empty set. No code change needed here — only test expectations change.

## Risks / Trade-offs

- **[Active-slot migration correctness]** -> The new `EpicActiveIssues` table is the hard enforcer for non-terminal uniqueness, so its backfill must faithfully represent existing non-terminal memberships during upgrade. *Mitigation:* migration SQL joins `EpicIssues` to `Epics` and inserts only `idle`/`running`/`paused` owners; regression coverage migrates a pre-issue-179 database and verifies active owners are backfilled while `done`/`closed` owners remain historical-only.
- **[Behavioural break for callers relying on empty post-close link sets]** -> List/detail/progress reads for closed epics will now show members. *Mitigation:* this is the intended product behaviour; update affected tests (see Migration Plan). No external API contract changes.
- **[Proposal/schema drift]** -> The proposal asserted "no DB schema changes"; D2 introduces one. *Mitigation:* explicit migration + snapshot update; called out here so reviewers can challenge.
- **[Stale `EpicClosed` event with no subscriber]** -> After D1 the event is emitted but unused. *Mitigation:* harmless; retained for audit/future projections rather than deleted to avoid churn in the domain event union.

## Migration Plan

1. **Code**: apply D1 (drop side-effect + helper), D3 (active-slot-aware duplicate check), D4 (projection filter).
2. **Schema**: add an EF Core migration that drops the unique constraint on `IX_EpicIssues_ProjectId_IssueId`, keeping the index as non-unique; create `EpicActiveIssues` keyed by `(ProjectId, IssueId)`; backfill it from existing non-terminal epic memberships. Update `MohistDbContextModelSnapshot`.
3. **Tests**: 
   - Rewrite `Close_SetsStatusToClosedAndRemovesEpicIssueLinks` (`EpicLifecycleSpecs.cs:187`) → `Close_SetsStatusToClosedAndRetainsEpicIssueLinks` (assert member list + progress preserved).
   - Fix the `Assert.Empty(detail.LinkedIssues)` assertion in `Close_DoesNotChangeIssueStatus…` (`EpicLifecycleSpecs.cs:234`) → assert the linked issue is still present.
   - Add Fake-based specs covering: (a) close keeps links, (b) re-home from terminal epic into a non-terminal epic succeeds, (c) second non-terminal link still raises `DUPLICATE_EPIC_MEMBERSHIP`, (d) explicit single unlink still works, (e) progress/detail readable post-close, (f) `primaryEpic` follows the non-terminal epic / null when only terminal, (g) migration backfills active slots only for non-terminal owners.
4. **Verify**: `npm test` (server, TreatWarningsAsErrors acts as lint); typecheck/tests for web & runner are not affected (no contract change).
5. **Data**: backfill only the active-slot table from existing non-terminal memberships. Do not backfill historical closed epic link sets; existing closed epics have empty link sets because pre-change behaviour already deleted them, and they remain valid empty sets. Only newly-closed epics retain links.
6. **Rollback**: revert the migration (re-create the unique index — safe because retained-link rows from the new behaviour would need to be reconciled first if duplicates exist) and revert code. In practice rollback is only clean if no issue has been re-homed under the new code; otherwise orphan duplicate-link cleanup may be required.

## Open Questions

- Should the active-slot table eventually grow foreign keys to `Epics`/`Issues`, or is the existing explicit cleanup in the grain sufficient for the current local-first model? Lean: keep the current explicit cleanup and avoid migration churn unless integrity drift appears.
- Should listing epics (`EpicQuerier.ListAsync`) visually distinguish retained members of closed epics (e.g. a "closed" badge on the member rows), or is surfacing the preserved set as-is sufficient? Out of scope here but worth a follow-up in the Web layer.
