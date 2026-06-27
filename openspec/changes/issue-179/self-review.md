# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal's Impact section claimed "No DB schema changes: `EpicIssueRow` already persists independently of epic status; no migration needed". This contradicts `design.md` D2 and `tasks.json` T-001, which introduce an EF Core migration that relaxes the unique index `IX_EpicIssues_ProjectId_IssueId` (`MohistDbContext.cs:221`, `IsUnique()`) to non-unique — required so a retained terminal-epic membership and a new non-terminal membership can coexist for the same issue. The design already flagged this as "Proposal/schema drift" (Risks). Updated `proposal.md` line 31 to state the index-relaxation schema change and cross-reference `design.md` D2.
  Verification: Verified `MohistDbContext.cs:221` is `entity.HasIndex(e => new { e.ProjectId, e.IssueId }).IsUnique();`; confirmed T-001 acceptance criteria require the migration + snapshot update; re-read the updated `proposal.md` Impact section — it now aligns with design D2 and tasks T-001.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: T-001 implements changes described in BOTH spec files (`epic-issue-membership/spec.md` — close retains links, non-terminal uniqueness — AND `epic-lifecycle/spec.md` — the non-destructive Close transition), but its `spec` field points only to `specs/epic-issue-membership/spec.md#Membership retained across epic close`. The close behaviour is fully captured by T-001's acceptance criteria, so this is a doc-pointer gap only.
  SuggestedAction: Consider letting the `spec` field accept multiple anchors (e.g. also reference `specs/epic-lifecycle/spec.md#Close is non-destructive`), or note the epic-lifecycle coverage in the task description.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: `epic-lifecycle/spec.md` is a MODIFIED requirement that restates the entire lifecycle state machine, including legacy `active`→`idle` migration and the "at most one in-progress linked issue" execution-plane rule, which belong to #178/#177. This is acceptable for OpenSpec's MODIFIED format (full requirement restated with the close modification integrated) but is broader than this issue's delta.
  SuggestedAction: At integrate time, confirm the canonical owner of the lifecycle requirement is #178 and that restating it here does not create conflicting ownership.
  Status: follow-up

## Review Notes

- **Alignment**: Every issue "What Changes" entry and Acceptance Criterion traces to a spec requirement and a task. Close-retains-links (AC1) → T-001 + `epic-issue-membership#Membership retained across epic close`; re-home from terminal epic (AC2) → T-001 + `Terminal-epic membership does not block a new non-terminal link`; two-non-terminal rejection (AC3) → T-001 + `Second non-terminal epic membership is rejected`; unlink (AC4) → T-001 + `Explicit unlink of a single membership`; progress readable post-close (AC5) → T-001 acceptance criterion; primaryEpic non-terminal (AC6) → T-002 + `primaryEpic projection reflects non-terminal membership`. The issue body's "active/paused" terminology correctly maps to #178's typed `idle`/`running`/`paused` non-terminal set used throughout the specs.
- **Feasibility grounding verified against source**: close side-effect lives at `EpicGrain.cs:532-549` (`ApplyPendingEvents` → `EpicClosed` branch → `RemoveAllLinkedIssues` → `RemoveRange`); unique index at `MohistDbContext.cs:221`; `LinkIssueAsync` duplicate check at `EpicGrain.cs:58-74` (single `FirstOrDefaultAsync`); primaryEpic projection at `IssueQuerier.cs:1271`; `EpicProgress.IsTerminal(string)` available at `EpicProgress.cs:44` for reuse (D3/D4). Design line references are accurate.
- **Granularity**: T-001 is one coherent feature slice (close side-effect removal + index relaxation + status-aware duplicate check are mutually dependent — the index drop is required for the refined uniqueness to function). T-002 is the read-model projection slice. Neither title is a pure technical action; no separate test/install/migration tasks; tests are inline in each task's acceptance criteria. Appropriate.
- **Dependencies**: T-001 (priority 1, `dependsOn: []`) → T-002 (priority 2, `dependsOn: ["T-001"]`). No cycles; T-002 correctly waits on the invariant T-001 establishes so its "last write wins" projection resolves to the single non-terminal epic.

<promise>PASS</promise>
