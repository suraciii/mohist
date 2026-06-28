# Review Report

## Result: FAIL

## Repaired Items

_(none)_

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx graph fallback/recovery
  Evidence: After a selected graph reports an unrenderable state, `graphUnrenderable` becomes true from retained state (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:602`-`604`) and the graph widget is no longer rendered (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:965`-`976`). Because no effect resets `graphRenderable` when `epic.linkedIssues` changes, a user can use the fallback list to remove/add links that fix a cycle or empty/error condition while Graph remains selected, but the stale banner/list fallback stays stuck and the canvas never remounts to re-evaluate the now-renderable graph. [disallowed:product-behavior-change]
  SuggestedAction: Reset graph renderability/error state when the linked issue graph input changes, or keep a renderability probe mounted so changed data can report the new state. Add a regression test that starts with cyclic/empty/error graph state, updates `useEpic` to renderable linked issues while Graph is selected, and verifies the banner clears and the canvas appears.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but current tests only cover error recovery by switching tabs (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:3753`) and do not cover cyclic/empty recovery after data changes.
  Status: open

- [ID: item-2]
  Severity: minor
  Scope: packages/web/src/pages/epic-detail/model/startBlockerReason.ts
  Evidence: `EpicDetailPage` computes `hasInProgress` globally for all linked issues (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:646`) and passes it to every row (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:1006`). `deriveStartBlockerReason` then returns `Another issue is in progress` before considering the current row (`packages/web/src/pages/epic-detail/model/startBlockerReason.ts:19`-`21`). The current in-progress issue therefore shows a false blocker reason saying another issue is in progress, even though the row itself is the in-progress issue. This conflicts with the design's intended non-self single-in-progress blocker and makes the mobile row less understandable. [disallowed:product-copy/behavior-change]
  SuggestedAction: Pass a true sibling-only in-progress flag per row, or make the reason derivation distinguish `issue.status === in_progress` from sibling blocking. Add a component/model test for a current in-progress row plus a separate backlog row.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but the current sibling test only asserts that some row contains `Another issue is in progress` (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:2710`-`2725`) and does not assert that the running row avoids that misleading reason.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: mobile 320px/390px/430px horizontal overflow acceptance criteria
  Evidence: The acceptance criteria require concrete evidence that linked issue rows do not create horizontal scrolling at 320px, 390px, and 430px. The candidate adds structural jsdom tests for wrapping classes and DOM order (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:2520`-`2608`) but there is no recorded browser/manual evidence or automated viewport test that checks `documentElement.scrollWidth <= documentElement.clientWidth` at those widths. [disallowed:test-scope decision]
  SuggestedAction: Add a focused Playwright/browser smoke test for the Epic linked-issues section at 320/390/430, or record manual verification evidence in the workflow artifact if the team intentionally keeps this out of CI.
  Verification: `npm run test:run -w packages/web` passed; it cannot prove the pixel-level overflow criterion because jsdom does not compute layout.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: openspec/changes/issue-280/tasks.json
  Evidence: All task entries still have `passes: false` even though implementation commits and self-review mark the candidate complete. This is not a product deliverable issue, but it weakens traceability between the build work and the review evidence.
  SuggestedAction: Update task status metadata during the workflow stage that owns task completion tracking, if Mohist expects these flags to reflect the built candidate.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: packages/web Vitest configuration
  Evidence: `npm run test:run -w packages/web` prints `DEPRECATED  test.poolOptions was removed in Vitest 4`. This appears unrelated to the issue-280 candidate and did not fail the test run.
  SuggestedAction: Migrate the Vitest config away from removed `poolOptions` in a separate maintenance change.
  Status: pre-existing

<promise>FAIL</promise>
