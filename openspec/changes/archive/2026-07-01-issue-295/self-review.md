# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: feasibility
  Evidence: T-001's note instructed "Add the required `Time` column to the stage-event loader projection", but the existing `LoadWorkflowRunEventFactsAsync` (`IssueQuerier.cs:722`) also filters by `QualityWorkflowEventTypes` (`IssueQuerier.cs:27-34`), which includes `StageStarted` but OMITS `StageCompleted`. An implementer who only added `Time` would never load `StageCompleted` events and could not pair `StageStarted`→`StageCompleted` at all. Clarified the task note to require a stage-duration loader selecting `Source/Id/Type/Time/Data` over BOTH `StageStarted` and `StageCompleted` types, explicitly stating the existing loader cannot be reused as-is. This aligns the task with design D4 ("A stage-duration loader ... is required") and makes the task self-sufficient.
  Verification: Confirmed `QualityWorkflowEventTypes` contents (`StageStarted`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled` — no `StageCompleted`) at `IssueQuerier.cs:27-34`; confirmed `LoadWorkflowRunEventFactsAsync` projection omits `Time` at `IssueQuerier.cs:723`; re-validated `tasks.json` parses as JSON after the edit.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: The median odd/even formula, cycle decomposition, and population-weighted ratio are mandated to reuse the exact math from `GetApprovalWaitAsync` / `GetDeliveryTimesAsync`. The plan correctly references this, but the per-issue `activeWork` (Σ stage durations − approvalGateWait) could in principle go negative if an approval's `respondedAt`/`requestedAt` timestamps are inconsistent with stage boundaries (e.g. an approval recorded outside the stage span). The spec asserts the three components are non-negative "by construction" given correct event ordering.
  SuggestedAction: During T-001 implementation, add a spec case asserting `activeWork >= 0` and `inactiveGap >= 0` hold (clamp via `Math.Max(0, ...)` only if a genuine edge arises, and document it). Not blocking — the design's sum invariant already guarantees correctness under well-ordered events.
  Status: follow-up

## Review Notes

All five review dimensions pass:

- **Alignment**: Every "What Changes" entry traces to an issue requirement. Stage-duration distribution (avg/median), flow-efficiency ratio, and wait breakout (approval-gate + inactive) map 1:1 to the three Acceptance Criteria. The runner-slot wait mentioned in the User Voice is correctly subsumed under "inactive gap" (issue Product Shape explicitly scopes wait to "审批等待、无活动等待"). Non-Goals (no per-issue drill-down, no per-label grouping) are mirrored in proposal, design, and specs.

- **Completeness**: Both capabilities (`workflow-stage-duration-metrics`, `dashboard-stage-durations`) have full specs, and every spec has a task. Edge cases are covered: multi-run/retry latest-attempt (D3/D4), started-but-never-completed undefined sample, genuine-zero vs empty distinguishability (D5), approval double-counting via net-of-wait activeWork (D6), pending-approval exclusion, undefined/zero-cycle exclusion from the ratio, and window membership keyed on completion.

- **Consistency**: Spec capability names match proposal `## Capabilities`. Both task spec anchors resolve to existing headings (`#backend-exposes-...` at spec line 185; `#stage-duration-distribution-chart-renders-...` at spec line 3). Design D6 decomposition matches the spec scenario math exactly (6h active + 1h wait + 3h gap = 10h cycle). Naming is uniform across artifacts.

- **Feasibility**: All load-bearing codebase assumptions verified against source — 30-day completion-anchored window (`IssueQuerier.cs:895`), in-memory aggregation idiom (EF Core SQLite TEXT-column limitation), `ApprovalWaitMetricsResponse` nullable DTO shape, `StageApprovals` projection with `RequestedAt`/`RespondedAt`, vertical-only `BarSeries` (`scaleY`), local `PercentileOverlay` precedent, and absence of any `HorizontalBarSeries`. Two tasks are cohesive feature slices (not over-decomposed): each is a complete vertical read with tests inline; no "define interface"/"register DI"/standalone-test tasks exist.

- **Dependency completeness**: T-001 (`dependsOn: []`, priority 1) → T-002 (`dependsOn: ["T-001"]`, priority 2). All `dependsOn` target existing lower-priority IDs. No cycles.

<promise>PASS</promise>
