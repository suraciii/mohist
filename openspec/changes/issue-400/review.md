# Review Report

## Result: FAIL

Reviewed issue 400, proposal, design, delta spec, tasks, self-review, progress notes, and the full diff from `master...HEAD`. The candidate is Web-only as scoped. Verification run after review: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 297 test files, 4514 tests passed, and 1 skipped test; `git diff --check master...HEAD` produced no output.

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: test-gap
  Scope: `packages/web/src/widgets/kanban-board/ui/kanban-board-filter-reachability.test.tsx`, `packages/web/src/widgets/kanban-board/ui/kanban-board-mobile-non-overlap.test.tsx`
  Evidence: The visual layout acceptance criteria are not verified by real layout evidence. Desktop first-screen reachability is asserted only by DOM order/classes (`kanban-board-filter-reachability.test.tsx:145`, `:165`), not by viewport height, filter-bar height, or board/card bounding boxes. Mobile non-overlap is asserted by sibling/class checks (`kanban-board-mobile-non-overlap.test.tsx:190`, `:211`, `:223`) and `pointerEvents`, not by bounding boxes at a mobile viewport. This can pass while controls, tabs, cards, or the primary action visually overlap or push content below the useful first screen. [disallowed:requires broader visual/browser coverage]
  SuggestedAction: Add browser-level coverage for a representative desktop viewport with the app sidebar and at least one mobile viewport. Assert core columns/card content are visible, filter/search/sort stay within the first screen, and mobile filter controls, tabs, cards, and primary action bounding boxes do not overlap.
  Verification: Run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and the added browser/layout test command.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/web/src/widgets/kanban-board/ui/StageColumn.tsx`, `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`, `packages/web/src/widgets/kanban-board/ui/kanban-board-query.regression.test.tsx`
  Evidence: The desktop reachability fix depends on `StageColumn` changing from `max-w-[320px]` to `max-w-[420px]` (`StageColumn.tsx:74`) and on the collapsed Cancelled stub staying compact (`KanbanBoard.tsx:651`, `:655`). The updated regression test only checks stub show/hide behavior (`kanban-board-query.regression.test.tsx:101`) and the existing containment test only checks row overflow ownership, so a future regression could restore the old column cap or widen the stub while these tests still pass. [disallowed:test coverage change]
  SuggestedAction: Add a focused regression assertion for the sizing contract, or fold it into the browser layout test from item-1: core stage columns keep `flex-1` with the raised cap, the collapsed stub remains compact, and `kanban-board-row` remains the overflow owner.
  Verification: Run `npm run test:run -w packages/web` after adding the assertion.
  Status: open

- [ID: item-3]
  Severity: cleanup
  Scope: `packages/web/src/widgets/kanban-board/ui/StageColumn.tsx`
  Evidence: `bodyHidden` and `emptyState` remain in the `StageColumn` API and render branch (`StageColumn.tsx:22`, `:34`, `:98`) after the only caller was removed from `KanbanBoard.tsx`. A repo search shows no remaining callers. This leaves dead rendering behavior around the board path that this change intentionally replaced with the collapsed stub.
  SuggestedAction: Remove the unused props and branch, or add a concrete caller if the hidden-body mode is still part of the intended component contract.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-4]
  Severity: cleanup
  Scope: `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`
  Evidence: The new Cancelled stub hard-codes Cancelled colors (`border-red-200`, `border-red-100`, `text-red-700`, `#ef4444`) at `KanbanBoard.tsx:655`, `:657`, `:660`, and `:662` instead of using the existing `getStageColors` path used by `StageColumn` and the mobile tabs. A future status-theme change can update the normal Cancelled column while leaving the collapsed desktop stub visually inconsistent.
  SuggestedAction: Derive the stub accent, label class, and border treatment from `getStageColors(col.key)` or a shared local value in the collapsed branch.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; visually verify the collapsed stub still matches the Cancelled status treatment.
  Status: open

- [ID: item-5]
  Severity: cleanup
  Scope: `packages/web/src/widgets/kanban-board/ui/kanban-board-filter-reachability.test.tsx`
  Evidence: Two adjacent desktop tests assert the same FilterBar-before-board DOM-order condition (`kanban-board-filter-reachability.test.tsx:145` and `:165`). This adds maintenance noise without increasing coverage.
  SuggestedAction: Keep one DOM-order assertion, and use the freed test surface for a stronger layout or sizing assertion.
  Verification: Run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `openspec/changes/issue-400/tasks.json`
  Evidence: T-001, T-002, and T-003 remain marked `"passes": false` (`tasks.json:28`, `:51`, `:74`) even though the commits, `progress.txt`, and current verification show those tasks were implemented and tests pass. This is not a product deliverable issue, but it is a workflow traceability inconsistency in the candidate evidence. [disallowed:workflow artifact state]
  SuggestedAction: Synchronize the task pass flags or document that these flags are not authoritative for Check/Integrate.
  Verification: Re-read `openspec/changes/issue-400/tasks.json` and confirm task status matches the workflow’s expected source of truth.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: info
  Scope: `packages/web` test suite
  Evidence: The full web suite passes with one skipped test: `4514 passed | 1 skipped`. This review did not trace that skipped test because it is outside the issue-400 diff.
  SuggestedAction: Track the skipped test separately if it is not already known.
  Status: out-of-scope

<promise>FAIL</promise>
