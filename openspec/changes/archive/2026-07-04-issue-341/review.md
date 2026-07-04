# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/web/src/pages/issue-detail/ui/cards/CollapsibleRailCard.tsx`, `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`
  Evidence: The new rail collapse state does not recover when the viewport widens from narrow back to desktop. `CollapsibleRailCard` seeds `expanded` from `!(defaultCollapsed || forceCollapsed)` once (`CollapsibleRailCard.tsx:22`) and its effect only calls `setExpanded(false)` while `forceCollapsed` is true (`CollapsibleRailCard.tsx:24-28`). `IssueDetailPage` passes `forceCollapsed={isNarrowViewport}` to normal desktop-expanded rail cards such as Details, Workflow Profile, Configuration, Actions, Prerequisites, and Readiness (`IssueDetailPage.tsx:409-515`). Therefore, after a user visits or resizes to a narrow viewport, those cards are collapsed; when the same page widens back to desktop, `forceCollapsed` becomes false but no state reset expands non-low-frequency cards again. The post-build tests cover desktop-to-narrow collapse (`IssueDetailPage.reference-rail.test.tsx:476-531`) but do not cover the inverse transition, so the regression is currently unguarded. This conflicts with the approved split where narrow screens render collapsed sections, while desktop renders a right rail with only low-frequency Drift/Convergence default-collapsed (`openspec/changes/issue-341/specs/issue-detail-reference-rail/spec.md:26-48`). [disallowed:product-behavior]
  SuggestedAction: Split "initially collapsed on narrow" from "low-frequency default collapsed." When `forceCollapsed` changes from true to false, restore expansion for cards whose `defaultCollapsed` is false while keeping Drift/Convergence collapsed. Add a regression test that starts narrow, verifies normal rail cards are collapsed, switches the mocked media query to desktop, and asserts Details/Workflow Profile/Configuration/Actions are expanded while Drift/Convergence remain collapsed.
  Verification: `npm run typecheck -w packages/web` passed. `npm run test:run -w packages/web` passed (263 files, 4119 tests passed, 1 skipped), but no existing test exercises narrow-to-desktop restoration.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/pages/issue-detail/ui/StatusHeadline.tsx`, status-header tests
  Evidence: The status headline implementation has presentation branches for all six runtime summaries (`running`, `queued`, `approval-required`, `blocked`, `failed`, `done`) in `StatusHeadline.tsx:34-77`, and reducer-level tests cover queued/blocked/failed classification. The UI status-header tests explicitly assert running, approval-required, and done, while blocked is covered through the interrupted cross-tier path; there is still no direct UI assertion for the failed headline branch.
  SuggestedAction: Add a small status-header UI test for a failed workflow so the red failed headline branch remains protected.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: `openspec/changes/issue-341/`
  Evidence: The proposal, design, task, spec, progress, self-review, and review artifacts are present under `openspec/changes/issue-341/`. Per the candidate boundary, these are workflow context/evidence and are not product deliverables by themselves.
  SuggestedAction: Leave workflow artifacts in place; do not remove them as part of review repair.
  Status: out-of-scope

- [ID: item-4]
  Severity: info
  Scope: `packages/web/tests/live-task-cloud-event.test.tsx`
  Evidence: The full web test run reports `1 skipped` test: `shows approval toast for legacy approval_requested events` at `packages/web/tests/live-task-cloud-event.test.tsx:311`. This skip is outside the issue-detail layout candidate and was not introduced by the reviewed change.
  SuggestedAction: Track separately if legacy approval event coverage still matters.
  Status: pre-existing

Verification performed on the reviewed snapshot:

- `mo issue show 341 --project-id proj_f6c141d63b6243bfbb481737b2243b87` read the current issue and acceptance criteria.
- Read `proposal.md`, `design.md`, `tasks.json`, all three delta specs, `progress.txt`, `self-review.md`, and the changed product/test files under the issue-341 delta.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 263 files, 4119 tests passed, 1 skipped.

<promise>FAIL</promise>
