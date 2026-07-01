# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: `GetStageDurationsAsync` silently excludes delivered issues with a defined positive cycle from the flow-efficiency and wait-breakout populations when `activeWork < 0` or `inactiveGap < 0` (`IssueQuerier.cs:1311-1320`). The spec defines flow efficiency over delivered issues with defined strictly-positive cycle time and wait-breakout averages over delivered issues with a defined cycle time (`openspec/changes/issue-295/specs/workflow-stage-duration-metrics/spec.md:98-122`); it does not define an additional exclusion for inconsistent decomposition data or expose an excluded-sample count. This can make the response show stage-duration samples from an issue while omitting the same issue from flow/wait denominators, overstating efficiency or returning null wait values for a non-empty delivered population. The new tests codify the exclusion (`IssueQuerierSpecs.cs:2628-2687`), so the mismatch is in the candidate behavior, not only missing coverage. [disallowed:product-behavior-change]
  SuggestedAction: Align the implementation and contract. Either compute a defined non-negative decomposition for all positive-cycle delivered issues, or explicitly update the spec/API response to report invalid decomposition exclusions and make the UI distinguish them from an empty/undefined population.
  Verification: Add a regression with two positive-cycle delivered issues where one has inconsistent stage/approval timestamps, and assert the ratio/wait denominator follows the accepted contract instead of silently disappearing.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: Stage order is resolved once from the project default profile with `issueSelection: null` (`IssueQuerier.cs:1131-1137`, `IssueQuerier.cs:1387-1399`), while delivered issues can have issue-specific effective workflow profiles (`IssueQuerier.cs:1120-1122`, `IssueQuerier.cs:1693`). Observed stages not present in the project-default order are appended alphabetically (`IssueQuerier.cs:1275-1279`). For a delivered issue using a custom profile with a different stage order, the endpoint can violate the spec requirement to return stages in workflow stage order. [disallowed:product-behavior-change]
  SuggestedAction: Derive ordering from the effective workflow profile(s) of the delivered issue population, or define and implement a deterministic mixed-profile ordering that still preserves each observed workflow's stage order where possible.
  Verification: Add a server spec with a delivered issue using a non-default workflow profile whose stage order differs from the project default, then assert the response order matches that effective workflow order.
  Status: open

- [ID: item-3]
  Severity: minor
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: Cross-run latest-attempt pairing sorts by `(Time, Id)` only (`IssueQuerier.cs:1433-1440`), but `WorkflowRunEventRow.Id` is a per-source sequence, not a global event sequence. If two workflow runs for the same issue emit same-stage attempts with identical timestamps and identical per-run ids, the chosen latest start depends on the input run/event ordering rather than a durable total order. [disallowed:product-behavior-change]
  SuggestedAction: Add a deterministic tie-breaker that is meaningful across run sources, or make the contract say same-timestamp cross-run attempts are undefined and test that behavior explicitly.
  Verification: Add a cross-run retry test with same timestamp and same per-run sequence ids for competing `StageStarted` events, and assert the selected attempt is deterministic and contract-backed.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/src/pages/dashboard/productivity/StageDurationChart.tsx`
  Evidence: The wait-breakout annotation is placed at `y={MARGIN.top + plotHeight}` (`StageDurationChart.tsx:324-329`), and its first text baseline is `y + 14` (`StageDurationChart.tsx:397-404`). The bottom axis labels render at `axisY + tickLength + 10`, which is also `MARGIN.top + plotHeight + 14` with the default tick length (`ChartAxes.tsx:41-72`). With wait data present, the wait label and bottom tick label share the same baseline at the right edge of the plot, so the UI can render overlapping text. [disallowed:product-ui-behavior-change]
  SuggestedAction: Reserve separate vertical space for wait-breakout labels, increase the bottom margin, or move the annotation away from the bottom axis labels. Add a browser/screenshot or SVG bounding-box regression for the wait-breakout annotation and max tick label.
  Verification: Render `StageDurationChart` with wait data in a browser viewport and assert the max x-axis tick and `wait-breakout-annotation` text bounding boxes do not intersect.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/web/src/pages/dashboard/productivity/StageDurationChart.test.tsx`
  Evidence: The test named `switching to median re-renders bar lengths from per-stage median (no second fetch)` creates `fetchSpy` but never wires it to the query hook or global fetch (`StageDurationChart.test.tsx:212-232`). Because `useStageDuration` is fully mocked (`StageDurationChart.test.tsx:7-10`), the assertion `expect(fetchSpy).not.toHaveBeenCalled()` proves nothing about the acceptance criterion that lens switching performs no second backend read.
  SuggestedAction: Add an integration-style component/hook test with a real `QueryClient` and mocked `fetch`, click the Median lens, and assert `/issues/metrics/stage-duration` was requested exactly once.
  Verification: Run `npm run test:run -w packages/web -- StageDurationChart stage-duration ProductivityZone` after adding the regression.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: server test suite outside issue-295 stage-duration change
  Evidence: Full `npm test` did not complete. `dotnet test Mohist.sln -p:SkipWebBuild=true` failed before completion on two `CompletedAt` detail-read tests: `IssueQuerierSpecs.DetailAsync_ArchivedIssue_ExposesSameCompletedAt` expected `2026-06-25T09:15:00.0000000Z` but got null at `IssueQuerierSpecs.cs:1805`, and `IssueQuerierSpecs.DetailAsync_ForCancelledIssue_IncludesCompletedAt` expected `2026-06-20T14:00:00.0000000Z` but got null at `IssueQuerierSpecs.cs:1750`. The issue-295 diff adds stage-duration tests starting after the existing delivery-time section (`git diff master...HEAD -- IssueQuerierSpecs.cs` shows the stage-duration block inserted after line 2147), so these failures appear outside the reviewed product change.
  SuggestedAction: Fix or quarantine the `CompletedAt` detail-read regression in the owning change so the default server suite can complete.
  Status: pre-existing

## Verification

- `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~GetStageDurationsAsync` passed: 16 tests.
- `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~StageDurationMetrics` passed: 5 tests.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web -- StageDurationChart stage-duration ProductivityZone` passed: 3 files, 40 tests.
- `npm test` did not complete: server test phase hit the two out-of-scope `CompletedAt` failures listed in item-6 and then the command timed out.

<promise>FAIL</promise>
