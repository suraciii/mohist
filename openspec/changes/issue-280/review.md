# Review Report

## Result: FAIL

## Repaired Items

_(none)_

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx graph empty-data fallback
  Evidence: The spec requires an empty dependency graph to show a clear user-facing explanation and keep List usable. The real canvas only reports `empty` when `linkedIssues.length < 2` (`packages/web/src/widgets/epic-dependency-graph/ui/DependencyGraphCanvas.tsx:42`-`44`), but the Epic page hides the Graph toggle and never renders the graph region for exactly that state because `graphAvailable` is `linkedIssuesForGraph.length >= 2` (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:563`-`565`) and the graph region is only rendered when `graphSelected` is true (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:954`-`999`). As a result, an epic with zero linked issues only shows `No linked issues yet.` in the list (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:1013`-`1016`), and an epic with one linked issue shows the list with no graph-unavailable explanation. The `empty` banner tests use a mocked widget that reports `empty` despite two linked issues (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:3590`-`3609`), while the real page tests assert the toggle is hidden for zero and one linked issue (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:3184`-`3210`). [disallowed:product-behavior-change]
  SuggestedAction: Decide the intended empty-data UX and implement it consistently: either expose the Graph region/tab for empty graph states so the `Not enough linked issues to draw a graph. Use the list below.` banner is reachable, or adjust the spec/product copy so the list-only empty state is the explicit fallback. Add a real-path component test for zero and one linked issue that verifies the chosen explanation and list usability without mocking the graph widget into an impossible state.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed; `npm run test:e2e -w packages/web -- tests/e2e/epic-detail-mobile-overflow.spec.ts` passed. These runs do not resolve the finding because the empty-graph banner path remains unreachable in the real page state.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: openspec/changes/issue-280/tasks.json
  Evidence: All task entries still have `passes: false` even though implementation commits, self-review, and verification indicate the candidate has been built. This is workflow traceability metadata, not a product deliverable defect.
  SuggestedAction: Update task completion metadata in the workflow stage that owns task tracking if Mohist expects `tasks.json` to reflect the built candidate.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: packages/web Vitest configuration
  Evidence: `npm run test:run -w packages/web` prints `DEPRECATED  test.poolOptions was removed in Vitest 4`. This warning is unrelated to the issue-280 candidate and does not fail the test run.
  SuggestedAction: Migrate the Vitest config away from removed `poolOptions` in a separate maintenance change.
  Status: pre-existing

<promise>FAIL</promise>
