# Self Review Report

## Result: PASS

All five review dimensions pass. No blocking items. Every key factual claim in the
plan was verified against the live source (`IssueQuerier.cs` 2393 lines, `IssueStore.cs`
95 lines, the 5 metrics methods, the 4 `internal const` event constants, the 4 `ToInfo`
overloads, the inline median at ~L1044 + `ComputeMedian` at L1520 with the self-admitted
"reuse the exact formula" comment, the 5 `IssueRoutes.*Metrics.cs` partials, the
`IScopedService` conventional scan in `ServiceCollectionExtensions.AddMohistConventionalServices`,
the existing `IssueQuerier/Scoped` theory row, `IssueQuerierSpecs.cs` 3591 lines, and the
two `IssueQuerier` subclass stubs in the Epic specs). One safe consistency repair was applied;
two non-blocking follow-ups are noted.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` Impact section stated the DI change as a manual step
  ("DI registration (`MigratedServicesRegistration`) — register `IssueMetricsQuerier`
  as scoped."). This contradicts the actual mechanism: `IssueQuerier` has no hand-written
  registration line and is registered conventionally via the Scrutor assembly scan in
  `ServiceCollectionExtensions.AddMohistConventionalServices` (registers every concrete
  `IScopedService` as scoped). `design.md` D2 already flags and corrects this, and
  `tasks.json` T-002 follows the design — so the inconsistency lived only in the proposal.
  Changed the proposal line to state `IssueMetricsQuerier` implements `IScopedService` and
  is registered conventionally (matching `IssueQuerier`), with only a theory-row addition
  to `MigratedServicesRegistrationSpecs`.
  Verification: re-read the edited `proposal.md` line; confirmed it now matches `design.md`
  D2, `tasks.json` T-002 notes, and the verified source (`AddMohistConventionalServices`
  + `IScopedService` scan; existing `typeof(IssueQuerier), ServiceLifetime.Scoped` row).
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: alignment
  Evidence: The issue body (User Voice, Product Shape, and the Acceptance Criterion)
  counts the "scan `IssueEvents` by project source" loop as duplicated across **3** call
  sites, while the proposal/design correctly identify **4** (completion, quality,
  delivery-time, stage-duration). A source grep confirms 4 distinct methods build the
  project-source prefix via `IssueSourcePrefix` (L392, L600, L1112, L1256), so the design
  is more accurate than the issue undercount. The functional requirement — consolidate
  every duplicated copy into one parameterized `ScanIssueEventsByProjectSourceAsync` — is
  fully met and indeed exceeds the issue's literal count, so this is not a
  misinterpretation.
  SuggestedAction: No change needed. If the issue's acceptance checklist is taken
  literally ("原 3 处调用点"), treat the 4th as in-scope by the same consolidation intent.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: The `issue-read-model-queries` spec is not referenced by any task's `spec`
  field (T-001→`issue-persistence-legacy-cleanup`, T-002→`issue-metrics-aggregation`,
  T-003→`issue-query-shared-loading`). However its three requirements are substantively
  covered by acceptance criteria: "Read-model query ownership" and "No metrics concerns in
  the read-model service" are met by T-002's criterion that `IssueQuerier` contains no
  metrics methods/records/enum/accumulators, and "Single consolidated read-model mapping"
  is met by T-003's criterion that the 4 `ToInfo` overloads merge into one `BuildInfo`. So
  the spec has tasks; only the convenience pointer is absent (the single-`spec`-per-task
  schema makes a clean dual-link awkward since the spec's concerns span two tasks).
  SuggestedAction: Optionally add a one-line note to T-002 acknowledging it also satisfies
  `issue-read-model-queries` (read-model-only ownership). No change required for coverage.
  Status: follow-up

## Dimension Summary

- **alignment**: PASS — every Acceptance Criterion in the issue traces to a "What Changes"
  entry and a spec; the only numeric drift (3 vs 4 scan sites) favors fuller consolidation.
- **completeness**: PASS — all issue requirements have specs; all specs have tasks whose
  acceptance criteria cover them (item-3 is a pointer-only gap, substance covered).
- **consistency**: PASS after item-1 repair — proposal now agrees with design D2, tasks,
  specs, and source on DI registration; naming (`IssueMetricsQuerier`, `IssueReadModelLoader`,
  `LoadProjectedAsync`, `ScanIssueEventsByProjectSourceAsync`, `ComputeMedian`, `BuildInfo`)
  is uniform across all artifacts.
- **feasibility**: PASS — no task is a pure mechanical shim (each is a complete feature
  slice with tests inlined, no standalone "add tests" task, no install/start/stop split);
  granularity is coarse and cohesive; no cycles.
- **dependency_completeness**: PASS — T-001 and T-002 are genuinely independent (different
  files: `IssueStore.cs` vs `IssueQuerier.cs`+new file) with `dependsOn: []`; T-003
  correctly depends on T-002 (must rewire `IssueMetricsQuerier`'s 3 call sites); all
  `dependsOn` point to existing IDs with strictly lower priority (T-002 p2 < T-003 p3); no
  cycles.

<promise>PASS</promise>
