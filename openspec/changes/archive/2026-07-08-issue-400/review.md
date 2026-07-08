# Review Report

## Result: PASS

Reviewed issue 400, proposal, design, delta spec, tasks, self-review, progress notes, and the full diff from all commits in this branch. The candidate is Web-only as scoped. Verification run after review: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 297 test files, 4513 tests passed, 1 skipped.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: layout coverage
  Evidence: Prior review flagged missing browser-level visual layout coverage. The `issue-board-layout.spec.ts` e2e test was added with bounding-box assertions for desktop (1280px) and mobile (390px) viewports, verifying core column visibility, filter first-screen fit, Cancelled stub width ≤130px, and mobile surface non-overlap.
  Verification: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` pass.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: sizing contract regression
  Evidence: Prior review flagged missing assertion for `max-w-[420px]` cap and stub compactness. The e2e test now asserts `flex-1`, `max-w-[420px]` on all three core columns and `stubBox.width ≤ 130` at a real viewport.
  Verification: `npm run test:run -w packages/web` passes.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: cleanup — `packages/web/src/widgets/kanban-board/ui/StageColumn.tsx`
  Evidence: Prior review flagged `bodyHidden` and `emptyState` as dead props/branches. Both have been removed from the `StageColumn` interface (lines 19-22) and render body (lines 93-117).
  Verification: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` pass; grep confirms zero callers pass `bodyHidden` or `emptyState`.
  Status: resolved

- [ID: item-4]
  Severity: info
  Scope: cleanup — `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`
  Evidence: Prior review flagged hard-coded Cancelled color values (`border-red-200`, `#ef4444`, `text-red-700`). These have been replaced with dynamic values from `getStageColors(col.key)` — `colors.activeBorder`, `colors.accent`, `colors.labelClass` (lines 656, 660, 664, 666).
  Verification: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` pass; the stub now derives all colour tokens from the shared stage-colors scheme.
  Status: resolved

- [ID: item-5]
  Severity: info
  Scope: cleanup — `packages/web/src/widgets/kanban-board/ui/kanban-board-filter-reachability.test.tsx`
  Evidence: Prior review flagged a duplicate DOM-order assertion in the desktop FilterBar section (same `compareDocumentPosition` check in two adjacent tests). The duplicate has been removed; only one DOM-order test remains.
  Verification: `npm run test:run -w packages/web` passes; file has 5 tests in the desktop describe (down from 6).
  Status: resolved

- [ID: item-6]
  Severity: info
  Scope: workflow — `openspec/changes/issue-400/tasks.json`
  Evidence: Prior review flagged `"passes": false` for T-001, T-002, T-003. All four tasks (T-001 through T-004) now carry `"passes": true`.
  Verification: `grep '"passes"' openspec/changes/issue-400/tasks.json` returns four `true` lines.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/web/tests/e2e/issue-board-layout.spec.ts`
  Evidence: The e2e test uses `page.getByTestId('search-input').first()` which depends on the desktop search input preceding the mobile one in DOM order. At 1280px this is correct today, but a reorder could silently break the assertion or assert on the wrong (hidden) element.
  SuggestedAction: Scope the desktop search-input assertion via the desktop FilterBar row rather than relying on `.first()` with global DOM order.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/web/tests/e2e/issue-board-layout.spec.ts`
  Evidence: The mobile card-list parent is located via `page.getByTestId('issue-card').first().locator('xpath=..')` (line 164). This couples the locator to the assumption that `IssueCard` is the direct child of the list container. A future wrapper would break this without a testid-based guard.
  SuggestedAction: Add a `data-testid` on the mobile card list (`flex-1 overflow-y-auto`) so the e2e test can target it directly rather than through parent traversal.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: `packages/web` test suite
  Evidence: The full web suite passes with one skipped test: `4513 passed | 1 skipped`. This review did not trace that skipped test because it is outside the issue-400 diff.
  SuggestedAction: Track the skipped test separately if it is not already known.
  Status: out-of-scope

<promise>PASS</promise>
