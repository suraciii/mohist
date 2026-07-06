# Self Review Report

## Result: PASS

The plan (proposal, design, specs, tasks) for issue #389 is internally coherent and faithfully addresses every Acceptance Criterion in the issue. Design claims were verified against the actual codebase (`InsightsPage.tsx`, `CycleTimeChart.tsx:84` default lens, `QualityPanel.tsx:144` "Last 7 days", `IssueRoutes.Dtos.cs:308` dual-window DTO, `IssueMetricsQuerier.cs:451` `GetQualityAsync`, `pages/insights/index.ts` verdict re-exports, the `*-chart-window` badge coverage across panels, and the `model/quality.ts:34` read of `window30d` that motivates T-003 → T-001 ordering). No blocking issues found; one minor follow-up noted.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The `insights-chart-presentation` spec says "Every chart retained on the Insights page MUST source its data from the page's selected time range," while design D4 explicitly scopes `InvestmentPanel` and `EpicProgressList` out of the audit (companion-issue territory). `EpicProgressList` is structurally not range-driven (no `range` prop), so the spec wording is slightly over-broad relative to the design's defensible scope cut. The intent is clear and the design resolves it, so no change was required; flagged here for traceability.
  Verification: Read `EpicProgressList.tsx:83` (`export function EpicProgressList()` — no range param) and `InvestmentPanel.tsx:26` (already range-driven with empty-state). Confirmed design D4's scope decision matches reality; T-004's acceptance criteria correctly limit the audit to the time-windowed charts.
  Status: resolved (no edit needed — design already documents the scope decision)

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `insights-chart-presentation/spec.md` Requirement 1 ("Every retained chart's data window matches the selected range") does not explicitly exclude non-windowed panels. A future strict reader could read it as requiring `EpicProgressList` to consume `range`, which would be a product change, not a presentation fix.
  SuggestedAction: Optionally add a one-line scope note to the spec clarifying that the floor applies to time-windowed metric charts only (panels that already consume `range`); structural lists like `EpicProgressList` are out of scope. This is wording polish, not a behavior change — the design already enforces the intended scope.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Design D1 says `model/insights-range.ts` is kept because "the charts and entity hooks import `InsightsRange`/`DEFAULT_INSIGHTS_RANGE` through it." Verified that the chart *components* import the type via `../model/insights-range`, but the entity API files (e.g. `entities/issue/api/quality-metrics.ts:4`) import directly from `entities/shared/insights-range`. The decision (keep the file) is correct; only the rationale's "entity hooks import through it" half is imprecise.
  SuggestedAction: Optional wording tweak in design D1: "the chart *components* import `InsightsRange` through `model/insights-range.ts`" (drop the "entity hooks" clause, since entity hooks import from `entities/shared/insights-range` directly). No implementation impact.
  Status: follow-up

## Cross-check summary

- **Alignment**: All 7 issue Acceptance Criteria map to proposal "What Changes" entries → 4 Capabilities → 4 specs → 4 tasks (T-001..T-004). No issue requirement missing or misinterpreted. Signal Summary removal, title-vs-default-lens alignment, quality single-window contract (incl. breaking HTTP change), and the cross-cutting window/empty-state floor are all covered.
- **Completeness**: Every spec scenario has a corresponding task acceptance bullet. Edge cases covered: omitted `range` defaults to 30d, zero-sample empty state, lens-switch-back title restore, trend/previous-window scaling with range, aggregation algorithm preserved.
- **Consistency**: Field naming (`window` / `Window`) is consistent across server DTO, web DTO, and QualityPanel. Spec forbids `window30d`-style names; design's `window` choice satisfies this. Task `spec` paths all exist and match the capability names.
- **Feasibility**: Each task is one complete feature slice (removal / title-tracking / single-window contract / audit). No over-fine tasks — no standalone "define interface", "register DI", "extract class", or separate "add tests" tasks; tests are inline in each implementation task's acceptance criteria. Server+web DTO change correctly bundled in T-003 to avoid a build-broken window.
- **Dependency completeness**: T-001 and T-002 have empty `dependsOn` (priority 1, independent — they touch disjoint files). T-003 depends on T-001 (justified: `model/quality.ts:34` reads `window30d`, deleted by T-001, so T-003 doesn't migrate dead code). T-004 depends on T-001/T-002/T-003 (audit must run after all three land). All `dependsOn` point to existing IDs with strictly lower `priority`. No cycles.

<promise>PASS</promise>
