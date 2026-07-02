# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: Review file path in self-review.md `openspec/changes/issue-297/self-review.md:15` listed `stage-population-snapshot/spec.md:116` — the spec text mention of `IssueCancelled` as event name is factually imprecise (the emitted event is `IssueClosed`), but design D5 already resolves this for the implementer. No change needed.
  Verification: N/A (already documented as follow-up in self-review)
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: cleanup
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Events/StagePopulationSnapshotServiceSpecs.cs:438-465` and `:504-531`
  Evidence: Two tests (`SnapshotOnceAsync_NewRunBeforeFirstStage_DoesNotCountOldRunStage` and `SnapshotOnceAsync_NewRunBeforeReplacementStage_DoesNotCountOldRunStage`) test the same scenario — a newer workflow run with no stage events should not count the old run's stage. Both verify the same assertions (0 Build, 0 Plan, 0 Check).
  SuggestedAction: Remove one of the two duplicate tests.
  Status: follow-up

- [ID: item-3]
  Severity: cleanup
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Events/StagePopulationSnapshotServiceSpecs.cs:470-500` and `:536-566`
  Evidence: Two tests (`SnapshotOnceAsync_LateOldRunStage_DoesNotOverrideActiveRun` and `SnapshotOnceAsync_LateOldRunStageDoesNotOverrideActiveRun`) test the same scenario — a late `StageStarted` from an older run should not override the active (newer) run's stage. Both assert the same outcomes (1 Plan, 0 Check).
  SuggestedAction: Remove one of the two duplicate tests.
  Status: follow-up

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Events/StagePopulationSnapshotServiceSpecs.cs`
  Evidence: No snapshot-service integration test seeds a `com.mohist.issue.reopened` event and verifies the issue is re-attributed to `backlog`. The unit-level `IssueStageAttributionSpecs.DoneThenReopened_AttributesAsBacklog` (IssueStageAttributionSpecs.cs:237) covers the pure-function path, but the snapshot service's full pipeline (event seeding → per-issue attribution → snapshot row) is not tested for the reopened path.
  SuggestedAction: Add a snapshot-service test that seeds a `reopened` event after `work-completed` or `closed`, and asserts the issue reappears in the `backlog` bucket.
  Status: follow-up

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Issue/Services/CumulativeFlowQuerier.cs:42`
  Evidence: The trailing-window constant `TrailingWindowDays = 90` has no unit test asserting its value. The integration tests verify the 90-day window behavior through the HTTP endpoint (e.g., `CumulativeFlow_NoSnapshots_ReturnsEmptySeriesWithFixedWindow` checks window bounds), but a direct constant-level test would catch accidental edits.
  SuggestedAction: Add a unit test verifying `CumulativeFlowQuerier.TrailingWindowDays` equals 90, or assert the window length in an existing integration test.
  Status: follow-up

- [ID: item-6]
  Severity: minor
  Scope: `packages/web/src/pages/dashboard/productivity/CumulativeFlowChart.tsx:42`
  Evidence: `STRATUM_FILL_CLASS` reuses `fill-chart-3` for both the `build` and `done` bands. The bands share the same fill token on the two bands farthest apart in the stack, which makes them chromaticaly identical. This is acknowledged by design D8 (disambiguation by stacking order + legend shape/label) and the accessibility wrapper already provides SR summary and non-color legend. Acceptable trade-off.
  SuggestedAction: Consider adding a `--chart-6` theme token in a future palette refinement. No action needed now.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueStageAttribution.cs:132-133`
  Evidence: `_ = stageOrder; _ = dayEndUtc;` discards two parameters that callers allocate and pass. The function's XML doc explains both are intentionally not validated (`stageOrder` is documented but the rule doesn't gate on unknown stages; `dayEndUtc` is caller-documentation). The allocation cost is negligible (small lists, single `DateTimeOffset`).
  SuggestedAction: No action needed — intentional design. Consider removing from the signature in a future refactor if the documentation-only parameters outweigh the traceability benefit.
  Status: pre-existing

- [ID: item-8]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Events/Hosting/StagePopulationSnapshotService.cs:551-558`
  Evidence: `StagePopulationSnapshotCounts` uses public mutable fields (`public int Backlog;`) rather than properties. This is an `internal sealed class` used only by the snapshot service. No encapsulation risk, and the style mirrors the plain-data-holder pattern in sibling code.
  SuggestedAction: No action needed. Optionally convert to auto-properties for consistency with the rest of the codebase.
  Status: pre-existing

<promise>PASS</promise>
