# Self Review Report

## Result: PASS

## Repaired Items

None. No artifacts required repair. All load-bearing factual claims in
`design.md` were verified against the codebase:

- Server returns dense trailing-30-day `bucket=day` buckets with per-bucket
  `Completed`/`Failed`, bucketed by terminal-event time (`IssueQuerier.cs:236-294`),
  pinned by `IssueMetricsApiSpecs.cs:34-52` and `:87-114` — so no server task is
  needed and spec Req 4 is satisfied by existing behavior.
- `BarSeries` cannot stack (`BarSeries.tsx:3-60`); the new `SegmentedBarSeries`
  primitive (design D2) is the correct, regression-isolated remedy.
- `fetchCompletionTrend` hardcodes `bucket=week` and the query key is exactly
  `['issues','metrics','completion','week', projectId]` (`completion-trend.ts:19,26`),
  pinned by `completion-trend.test.ts:32,42,74-76` — the default-`'week'` refactor
  (design D3) preserves this contract.
- `CostTrendChart` tokens (`chart-2`/`chart-5`), dual axes, `hasUsageData`, and
  server-computed trend are all as cited; `ChartContainer`/`ChartLegend`/
  `ChartAccessibility` behave as referenced.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: Spec Req 1 (`spec.md:5,17`) says "Each bar SHALL stack a darker
  failed segment" / "stacked within that day's bar", which reads as cumulative
  stacking. Design D1 (`design.md:33-48`) instead implements an independent
  bottom-anchored overlay on a single shared count axis (bar height strictly =
  completed; failed protrudes above completed when `failed > completed`). The
  design's choice is correct and defensible — it is forced by the stronger
  SHALL "each bar's height encoding that day's completed-issue count"
  (`spec.md:5`), which cumulative stacking would violate — but the spec's
  "stack/within" wording is in mild tension with the chosen semantics.
  SuggestedAction: Align spec wording with the chosen overlay semantics
  (e.g., "overlay a darker failed segment anchored at the same base on a shared
  count axis") to remove ambiguity for implementers and reviewers.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: Under D1's overlap model (`design.md:41`), when `failed > completed`
  and the darker failed rect is drawn atop the completed rect, the completed
  (light) segment is fully occluded and the visible bar top sits at
  failed-height — momentarily contradicting "bar height encodes completed" for
  that day. The protrusion is acknowledged in D1 but render order is not pinned,
  and no widget test asserts the `failed > completed` case (T-003 ACs at
  `tasks.json:57-58` cover `completed=0` + failures and the shared max, but not
  `failed > completed > 0`). `failed > completed` is atypical in practice.
  SuggestedAction: In T-001/T-003, pin a render order that keeps the completed
  segment visible (e.g., draw completed above failed, or clip the failed segment
  to the completed cap with a distinct protrusion marker) and add a widget test
  for the `failed > completed` day.
  Status: follow-up

<promise>PASS</promise>
