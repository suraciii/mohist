# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal's "What Changes" bullet said the presentational sub-components go "into their own files" (literal one-file-per-chip reading), but design D1 and task T-001 deliberately consolidate the five <30-line pills into a single cohesive `ui/pills.tsx` module. The design documented this as a considered deviation in its "Alternatives considered", but the proposal wording itself was ambiguous and could be read as contradicting the design/tasks.
  Verification: Edited `proposal.md` bullet 1 to state pills move "into their own modules" with `WorkflowYamlDialog` getting its own file and the five pills consolidated into one `ui/pills.tsx` (cross-referencing design D1). proposal ↔ design D1 ↔ tasks.json T-001 now agree on a single module layout. No acceptance criterion or issue requirement was affected (the issue only requires the pieces leave the god file, which all three artifacts already mandated).

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-002 and T-003 declare `dependsOn: []` yet are not the first task and each edit `IssueDetailPage.tsx` (which T-001 also edits). This is not a logical dependency — both create standalone `model/` files — but it is a sequential file-edit coupling. The tasks correctly explain the sequencing rationale in their `notes` ("Ordered after T-001 only to keep the page-file edits sequential" / "does not consume T-001/T-002 output"), and priority ordering (T-001=1, T-002=2, T-003=3) guarantees AFK execution order, so there is no real risk of conflicting parallel edits.
  SuggestedAction: Optional — if a stricter discipline is desired, the convention could note that priority ordering (not `dependsOn`) is the intended mechanism for sequencing independent file-touching tasks. No change required for correctness.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: This is a pure behavior-invariant refactor, so the proposal correctly declares "New Capabilities: None" and "Modified Capabilities: None" and intentionally produces no `specs/` deltas (specs describe capabilities; no capability changes here). Acceptance is entirely structural and guarded by the 4 existing component suites. This is consistent with the openspec model for refactors, so no spec files are required.
  SuggestedAction: None required — confirmed acceptable. The plan's `spec` fields correctly point at `design.md` decision anchors (D2–D6, goals) rather than nonexistent capability specs.
  Status: follow-up

## Verification Summary

Cross-checked every factual claim in the artifacts against the live source (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`, 1375 lines):

| Claim (proposal/design/tasks) | Source evidence | Status |
|---|---|---|
| 1375 lines, scc Complexity 211 | `wc -l` = 1375 | ✓ |
| 13 mutations | 13 `mutationFn` defs (L256–385) | ✓ |
| 9 inline presentational pieces + helpers | `PriorityChip`/`WorkflowStagePill`/`HealthPill`/`DraftPill`/`ArchivedPill`/`WorkflowYamlDialog` + 5 helpers, all inline L29–205 | ✓ |
| Two duplicated click-outside + 5s effects | forceStop effect L222–235, stop effect L319–332; both `setTimeout(..., 5000)` + `mousedown` listener | ✓ |
| Actions card ~300-line state machine | card refs/branching span ~L1003–1301 | ✓ |
| 46 `data-testid` | grep count = 46 (matches design.md risk note) | ✓ |
| `isCapacityFull` from `agentStatus.capacity` (issue-300) | L410 `!!capacity && capacity.max > 0 && capacity.active >= capacity.max` | ✓ |
| Query keys `['issues']` / `['issues', issueNumber]` / `['agent-status']` | all 13 `invalidateQueries` calls match | ✓ |
| Density test (issue-180) asserts space-y-8 / gap-8 / `:scope > .lg\:col-span-2` | `IssueDetailPage.test.tsx:762` describe, assertions L785–816 | ✓ |
| 4 component suites as regression guard | all 4 files present (67 `it/test` blocks) | ✓ |
| `epic-detail/model/` precedent | exists with pure `.ts` + `.test.ts` pairs (incl. `primaryLifecycleAction.ts` cited in D3) | ✓ |

### Alignment
Every "What Changes" entry traces to an issue acceptance criterion: split into sub-components (AC1), merge duplicated click-outside (AC2), data-testid/DOM unchanged + density guard (AC3), capacity-gating + nav URLs unchanged (AC4), 4 suites green (AC5). No issue requirement missing or misinterpreted. Non-Goals match.

### Completeness
All requirements covered. No capability specs are needed (pure refactor → no new/modified capabilities), which is the correct openspec treatment. Edge cases (density wrappers, capacity gating, query-key drift, testid inventory) are explicitly addressed via design D2 and per-task acceptance criteria.

### Consistency
Naming is uniform across proposal/design/tasks (`IssueDescriptionSection`, `IssueDetailsCard`, `useConfirmOutsideClick`, `useIssueDetailMutations`, `computeActionsState`). Design decisions D1–D6 each map to exactly one task's `spec` field. The single proposal/design wording gap (item-1) was repaired.

### Feasibility
Dependencies resolve and point forward-only: T-004 ← {T-001,T-002,T-003}; T-005 ← {T-001,T-003}; T-006 ← {T-001,T-003}; T-007 ← {T-004,T-005,T-006}. No cycles. Granularity is appropriate — each task is a complete functional slice (foundation / hook / mutations / actions-card / sibling-cards / sections / final REVIEW gate); none is a bare "define interface" or "create file" micro-task, and tests are embedded in each implementation task's acceptance criteria rather than split out.

### Dependency Completeness
Every non-first task carries an appropriate `dependsOn` (T-002/T-003 intentionally empty for genuinely independent `model/` files, sequenced by priority — see item-2). All `dependsOn` IDs exist and have strictly lower priority values.

<promise>PASS</promise>
