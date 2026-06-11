# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test coverage gap
  Evidence: The desktop toggle in `KanbanBoard.tsx:639-651` is rendered as a child of `headerToggle` inside the `StageColumn` column. `StageColumn.tsx:102-105` shows the order in the header row is: dot indicator → label (`h2`) → total count (`span.ml-auto`) → `headerToggle`. The desktop toggle's `data-testid="cancelled-toggle"` is only set when the column is the Cancelled column. The toggle is locatable via `screen.getByTestId('stage-column-cancelled')` and `within(...).getByTestId('cancelled-toggle')`, which is the exact anchor pattern the test uses (`kanban-board-query.test.tsx:558-559`). No repair needed — the implementation matches the design.
  Verification: `npm test -- kanban-board-query` passes 53/53 tests; `npm test -- kanban-grouping` passes 13/13 tests; the toggle's bidirectional label transitions are pinned by `kanban-board-query.test.tsx:543-577`.
  Status: verified

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: test coverage gap
  Evidence: Issue acceptance criterion "Cancelled issues 的卡片上显示 Cancelled 状态 pill（灰色）" and spec Requirement "Cancelled issue cards display a Cancelled status pill" are both satisfied by the implementation: `IssueCard.tsx:264` no longer excludes `cancelled` from the `StatusPill` render guard, and the existing `StatusPill` branch at `IssueCard.tsx:63-72` renders the pill with `bg-gray-200 text-gray-600` and text `Cancelled`. However, neither `kanban-grouping.test.ts` nor `kanban-board-query.test.tsx` adds a regression test that asserts the pill is actually rendered on a cancelled card. The closest test is `kanban-board-query.test.tsx:543-577`, which asserts the cancellation overlay (`cancelledColumn` and `Cancelled work` text) but never queries `data-testid="status-pill"` to confirm the pill.
  SuggestedAction: Add a small assertion to the existing desktop test (`kanban-board-query.test.tsx:543-577`) that, after toggling to reveal the cancelled issue, the cancelled card has a `data-testid="status-pill"` element whose text reads `Cancelled`. Could also assert the class includes `bg-gray-200`. Tracked separately.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: edge-case UX
  Evidence: `KanbanBoard.tsx:596` renders the mobile in-list toggle when `selectedStage === IssueStatus.Cancelled && cancelledCount > 0`. `cancelledCount` is computed from `allColumns` (pre-filter, pre-toggle) at `KanbanBoard.tsx:498-501`. The body iterates `visibleSelectedColumn.issues`, which is empty whenever the user-applied board filters (`priorities`, `labels`, `search`) exclude all cancelled issues — even with `showCancelled === true`, the list body shows "No issues in Cancelled" while the toggle still reads `Show cancelled (N)` with the unfiltered count. The user can click the toggle and the label flips to `Hide cancelled` but the body remains empty. The design and spec do not address this interaction, and the spec deliberately decouples count from visibility. The behaviour is consistent with the spec; it is a UX wart, not a regression.
  SuggestedAction: Consider rendering the mobile toggle only when the filtered cancelled count is also `> 0`, or change the toggle label to use the filtered count when the filter is active. Decide based on product intent (e.g. should the toggle be informational "there are N cancelled issues hidden by your filter"?). Tracked separately.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: consistency
  Evidence: `KanbanBoard.tsx:645` and `KanbanBoard.tsx:602` use two different idioms for the same `setShowCancelled` call: the desktop toggle uses `setShowCancelled(!showCancelled)` while the mobile in-list toggle uses the functional form `setShowCancelled((value) => !value)`. Both are safe here (the closures are rebuilt on every render), but mixing the two styles in the same file is mildly noisy.
  SuggestedAction: Pick one (functional form is slightly safer) and use it consistently. Tracked separately.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: dead code surface
  Evidence: `kanban-grouping.ts:48-53` keeps `filterCancelledFromColumns` as a documented identity function (per design D2). The JSDoc on `kanban-grouping.ts:29-47` is thorough. However, the only caller is `KanbanBoard.tsx:510-513`, and the helper is now a no-op. Future readers who skim past the JSDoc may be tempted to "simplify" the call site and remove the helper, regressing the documented seam. The spec and design both explicitly preserve the helper as a seam for future URL-persistence work, so this is a documented trade-off, not a bug.
  SuggestedAction: Consider adding a one-line test that asserts the helper is exported and returns its input unchanged, so a future "simplification" attempt trips a test. The existing `kanban-grouping.test.ts:78-113` already does this for showCancelled=true/false; the helper's export/identity contract is covered. Tracked separately.
  Status: follow-up (already covered by existing tests; no action needed)

- [ID: item-6]
  Severity: follow-up
  Scope: design observation
  Evidence: `StageColumn.tsx:105` places `headerToggle` after the count `span.ml-auto`. In the Cancelled column this means the count is to the right of the label and to the left of the toggle. With `text-[11px] font-medium text-muted-foreground` styling on the toggle, this is a tight fit in a `min-w-[280px]` column. The header is still legible; no layout issue.
  SuggestedAction: None. Track only if user feedback later flags visual cramping. Tracked separately.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: pre-existing
  Scope: `packages/web/src/widgets/app-shell/ui/Header.test.tsx` (3 tests)
  Evidence: `npm test` reports 3 failures in `Header.test.tsx` ("shows Epics/Activity/Logs as title on … route"). Verified at the parent commit `5fa59f8489` (immediately before `b5fffbce57` which is the first issue-80 commit): the same 3 tests fail there with the same assertion (`getByRole('heading', { level: 1, name: 'Logs' })`). The issue-80 candidate does not touch `Header.tsx`, the route file, or any related code. The failures are pre-existing and unrelated.
  SuggestedAction: File a separate issue if not already tracked. Out of scope for issue-80.
  Status: pre-existing

- [ID: item-8]
  Severity: pre-existing
  Scope: `packages/web/src/pages/epics/ui/EpicListPage.test.tsx` (1 test)
  Evidence: "navigates to epic detail from a list card" fails at `expect(mockNavigate).toHaveBeenCalledWith('/epic/epic-active')`. Verified at parent commit `5fa59f8489`: the same test fails there. The issue-80 candidate does not touch the Epics page or `mockNavigate` setup. Pre-existing and unrelated.
  SuggestedAction: File a separate issue if not already tracked. Out of scope for issue-80.
  Status: pre-existing

- [ID: item-9]
  Severity: pre-existing
  Scope: `packages/web/tests/SessionPage.test.tsx` (3 tests)
  Evidence: "session header link routes to /issues/:number/session/:sessionId", "showTranscriptLink renders View transcript link instead of full row link", and "session header encodes workflow session names as a single path segment" all fail. Verified at parent commit `5fa59f8489`: the same 3 tests fail there with the same assertion. The issue-80 candidate does not touch the Sessions page. Pre-existing and unrelated.
  SuggestedAction: File a separate issue if not already tracked. Out of scope for issue-80.
  Status: pre-existing

## Spec Compliance Verification

| Acceptance criterion | Implementation | Test coverage | Status |
|---|---|---|---|
| UI/code/tests use `cancelled` term consistently | `kanban-grouping.ts:48-67` renames all symbols to `cancelled`; `KanbanBoard.tsx:478` `showCancelled`; tests updated | grep returns 0 matches for old names in `packages/web/`; `kanban-grouping.test.ts:78-156` uses new names | ✓ |
| Cancelled column default collapsed with toggle in column | `StageColumn.tsx:23-26,37-40` accepts `headerToggle`/`bodyHidden`; `KanbanBoard.tsx:625-660` passes toggle + bodyHidden for Cancelled | `kanban-board-query.test.tsx:543-577` asserts toggle inside `stage-column-cancelled`, default label `Show cancelled (1)` | ✓ |
| `Hide cancelled` reachable when shown, bidirectional toggle | `KanbanBoard.tsx:648` `showCancelled ? 'Hide cancelled' : \`Show cancelled (${col.issues.length})\`` | `kanban-board-query.test.tsx:564-577` asserts all three label transitions | ✓ |
| Desktop toggle inside Cancelled column (not next to Done) | `KanbanBoard.tsx:625-660` toggles are passed via `headerToggle` per-column; no off-column toggle | `kanban-board-query.test.tsx:558-559` locates toggle via `within(stage-column-cancelled)` | ✓ |
| Mobile Cancelled tab count reflects real issues, stable on toggle | `KanbanBoard.tsx:498-501` reads `allColumns`; `KanbanBoard.tsx:558-594` tab map iterates `allColumns` | `kanban-board-query.test.tsx:593-634` asserts badge `8` in both toggle states | ✓ |
| Cancelled cards show grey `Cancelled` pill | `IssueCard.tsx:264` no longer excludes `cancelled`; `StatusPill` branch at `:63-72` uses `bg-gray-200 text-gray-600` | **No automated test asserts the pill is rendered** (follow-up item-2). Code path is reachable. | ✓ (impl) / gap (test) |
| `filterCancelledFromColumns` is identity seam | `kanban-grouping.ts:48-53` returns `columns`; JSDoc at `:29-47` documents intent | `kanban-grouping.test.ts:85-102` asserts `expect(result).toBe(columns)` and that `cancelledCol.issues` retains its 2 items | ✓ |
| `getCancelledColumnCount` returns `cancelledCount` (not `closedCount`) | `kanban-grouping.ts:55-66` | `kanban-grouping.test.ts:115-156` uses `cancelledCount` and asserts independence from toggle | ✓ |
| No `showClosed`/`filterClosedFromDone`/`getDoneColumnCounts`/`closedCount` remain in `packages/web/src/widgets/kanban-board/` | grep returns 0 matches | n/a | ✓ |
| No `indicator !== 'cancelled'` StatusPill exclusion in web source | grep returns 0 matches | n/a | ✓ |
| Build is green | `npm run build:web` (tsc -b && vite build) completes with no errors | n/a | ✓ |
| All issue-80-related tests pass | `npm test -- kanban` → 66/66 pass | n/a | ✓ |
| Pre-existing failures unchanged | 7 failures in Header/EpicListPage/SessionPage exist on parent commit `5fa59f8489`; same set fails on `mo/issue-80` | n/a | pre-existing, out of scope |

## Verification Summary

- **Acceptance criteria** — All 6 issue acceptance criteria are met by the implementation. One (cancelled pill) lacks a dedicated automated test (follow-up item-2) but is reachable through the code path verified by manual inspection.
- **Tests** — `kanban-grouping.test.ts` (13/13) and `kanban-board-query.test.tsx` (53/53) pass. Full web test suite reports 7 failures, all in `Header.test.tsx`, `EpicListPage.test.tsx`, and `SessionPage.test.tsx`. Confirmed pre-existing on parent commit `5fa59f8489` — not caused by issue-80.
- **Build** — `npm run build:web` succeeds; `tsc -b` passes with no errors.
- **Spec compliance** — All 5 spec Requirements are addressed:
  1. `Web UI uses cancelled as the user-facing term for closed issues` — all 3 Scenarios pass.
  2. `Desktop Cancelled column renders its own toggle inside the column` — all 3 Scenarios pass.
  3. `Cancelled column body is no longer cleared by the grouping layer` — all 3 Scenarios pass.
  4. `Mobile Cancelled tab count reflects the real number of cancelled issues` — all 3 Scenarios pass.
  5. `Cancelled issue cards display a Cancelled status pill` — implementation is correct; follow-up item-2 captures the missing test assertion.
- **Sibling bugs** — Inspected the rename, the identity-seam refactor, the in-column toggle slot, the mobile tab decoupling, and the StatusPill re-enable. No sibling bugs found. The mobile-in-list-toggle edge case (item-3) is a documented design trade-off per spec, not a regression.
- **Cross-cutting** — No security, data-safety, public-contract, or migration concerns. No backend changes; the change is Web-UI-only as required by the issue's Non-Goals.
- **Repaired items** — No code changes were required. The candidate is consistent with the spec, design, and tasks. All repairs are limited to confirmation via test runs and grep.

<promise>PASS</promise>
