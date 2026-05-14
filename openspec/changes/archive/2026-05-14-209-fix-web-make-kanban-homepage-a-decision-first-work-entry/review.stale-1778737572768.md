## Findings

1. Error: Homepage search control does not restore from the URL after browser navigation.
File: `packages/cli/web/src/components/KanbanBoard.tsx:265-276`, `packages/cli/web/src/components/KanbanBoard.tsx:293-298`, `packages/cli/web/src/components/KanbanBoard.tsx:41-56`
Evidence: `KanbanBoard` correctly reparses URL state on `popstate` and updates `localState`, but `FilterBar` keeps a separate `searchValue` state initialized once from `state.search` and never resynchronizes when `state.search` later changes. After using `pushState` for search/filter changes and then navigating back/forward, the board data restores from the URL while the visible search input can still show the previous value.
Impact: This breaks `REQ-WUI-209-003` scenario `Board state remains URL-backed`, because the homepage control surface no longer accurately reflects restored URL state.
Suggested fix: In `packages/cli/web/src/components/KanbanBoard.tsx`, remove the duplicated local search state and drive the input directly from `state.search`, or add a `useEffect(() => setSearchValue(state.search), [state.search])` in `FilterBar`.

## Spec Compliance

- `REQ-WUI-209-001` PASS
Evidence: `NeedsAttentionSummary` renders above the board and links directly to `/issue/:number` in `packages/cli/web/src/components/KanbanBoard.tsx:229-255`, `packages/cli/web/src/components/KanbanBoard.tsx:323-327`. User-facing labels are derived in `packages/cli/web/src/lib/homepage-attention.ts:18-73`. Coverage exists in `packages/cli/web/src/components/kanban-board-query.test.tsx:434-628`.

- `REQ-WUI-209-002` PASS
Evidence: Desktop board now uses a horizontal row container `hidden md:flex flex-row ...` in `packages/cli/web/src/components/KanbanBoard.tsx:390-413`; mobile remains single-stage in `packages/cli/web/src/components/KanbanBoard.tsx:328-388`; Done is visually de-emphasized in `packages/cli/web/src/components/StageColumn.tsx:58-61`, `packages/cli/web/src/components/StageColumn.tsx:83-103`. Regression tests cover the desktop contract in `packages/cli/web/src/components/kanban-board-query.test.tsx:388-432`.

- `REQ-WUI-209-003` FAIL
Evidence: Full label reachability is implemented through the searchable popover in `packages/cli/web/src/components/KanbanBoard.tsx:114-182` and tested in `packages/cli/web/src/components/kanban-board-query.test.tsx:631-745`, but restored URL state is not fully reflected in the control UI because the search input does not resync from `state.search` after `popstate` (`packages/cli/web/src/components/KanbanBoard.tsx:41-56`, `265-276`).

- `REQ-WUI-209-004` PASS
Evidence: Tests cover attention-summary wording, desktop multi-column visibility, and hidden-label selection in `packages/cli/web/src/components/kanban-board-query.test.tsx:388-745`. `npm test` passed for the repo, including `web/src/components/kanban-board-query.test.tsx` (41 tests). `npm run build` passed.

## Quality Notes

- Correctness: One user-visible state-sync bug as described above.
- Complexity: No function reviewed exceeded the stated complexity threshold badly enough to raise a separate issue.
- Test coverage: Good coverage for the new behaviors, but there is no regression test for back/forward URL restoration of the visible search input.
- Security: No security issues found in the reviewed frontend-only change.

## Verification

- `cd packages/cli && npm test` PASS
- `cd packages/cli && npm run build` PASS

<promise>FAIL</promise>
