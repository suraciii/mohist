# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` risk note said the investment section "can be wired to Epic progress + snapshot first" if D is not ready. That is semantically wrong — the investment panel shows usage, not completion/Epic data — and contradicted `design.md` D7 and task T-004, which both degrade the panel to a labeled empty state while keeping the collapse/caliber structure wire-ready. Changed the sentence to state the empty-state degradation so the proposal aligns with the authoritative design and tasks.
  Verification: Re-read `proposal.md` Impact → Risk line; wording now matches design D7 ("degrades to a labeled empty state ... wire-ready") and T-004 acceptance criteria ("renders an explicitly labeled 'data unavailable' empty state ... wire-ready"). No architectural change, wording only.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Issue #166 (Agent/Session usage, source D) is marked `done` as a prerequisite, but a worktree search found no project-level usage aggregation endpoint or hook — only the per-session `AgentSessionUsage` type (`packages/web/src/entities/coder-session/model/types.ts:5`). The design (D7, Open Question 1) and T-004 already handle this by degrading the investment panel to an empty state, so the plan is robust; but if D's surface truly is absent, the investment panel will ship showing "data unavailable" until a follow-up wires it.
  SuggestedAction: Before/during implementation of T-004, confirm whether #166 landed an aggregation surface under an unexpected name. If genuinely absent, file a follow-up issue to add the Agent/Session usage snapshot + time-series so the investment panel can be populated.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Spec requirement "Investment section is collapsed by default with caliber annotation" has an expand scenario that reads unconditionally ("SHALL display the usage figures alongside an annotation"). At launch, with D unavailable (item-2), that populated path is not exercised; the empty-state path is covered by the separate "Productivity zone provides per-section empty states" requirement (which explicitly lists "no Agent/Session usage data"). The spec set is therefore coherent, but the expand scenario's conditionality is implicit.
  SuggestedAction: Optionally clarify the investment expand scenario as the populated-state contract (e.g. "WHEN usage figures are available and a user expands ..."), leaving the empty-state requirement to cover the D-unavailable case. Non-blocking; current specs + design are internally consistent.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: completeness
  Evidence: The issue's Non-Goals (no streak / badges / ranking / cross-project aggregation) are listed in the proposal but not encoded as spec requirements. The "no configurable time range" Non-Goal IS encoded (trend spec scenario "Trend does not offer a configurable time range"). The others are enforceable through review rather than an explicit spec line.
  SuggestedAction: Optionally add a brief Non-Goals requirement to `dashboard-productivity` for defensiveness. Non-blocking — the proposal Non-Goals and task scope already constrain the work.
  Status: follow-up

## Review Notes

Cross-checks performed (all passed):
- **Alignment**: every issue Acceptance Criterion maps to a task — AC1→T-001, AC2→T-002, AC3→T-003, AC4→T-004, AC5 (no new domain query)→cross-cutting, verified in each task's AC and reinforced by T-005's grep check. All Non-Goals respected.
- **Completeness**: all 7 `dashboard-productivity` requirements + the MODIFIED `dashboard-shell` requirement are covered. The two cross-cutting requirements ("mounts in the productivity slot", "composes existing data sources without new queries", "per-section empty states") are satisfied by T-005 plus each section task's own empty-state AC.
- **Consistency**: proposal Capabilities (new `dashboard-productivity`, modified `dashboard-shell`) each have a spec file; tasks reference correct spec paths; every task `spec` anchor matches the exact requirement header text on disk; naming is uniform.
- **Feasibility / granularity**: 5 tasks, each a complete feature slice with co-located tests (no bare "define interface", "register DI", "test only", or move/rename tasks). T-003's new `useCompletionTrend` is a new web *consumer* of the already-shipped C endpoint — explicitly permitted by the "no new backend endpoint / domain query" Non-Goal, and T-003's AC verifies no server-side file is added.
- **Dependency completeness**: T-001..T-004 are priority 1 with no deps (parallelizable); T-005 is priority 2 and depends on T-001..T-004. `dependsOn` entries all reference existing IDs with strictly lower priority; no cycles (DAG verified programmatically).

<promise>PASS</promise>
