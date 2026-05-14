## Findings

1. Error - `packages/cli/web/src/lib/homepage-attention.ts:65`
The Done attention rule still classifies every Done issue whose `mergeState` is anything other than `'merged'` as `Not merged`, including records where `mergeState` is `undefined`. `Issue.mergeState` is optional in `packages/cli/web/src/lib/types.ts:51`, so older or partial Done records with no merge result will be surfaced as actionable even when the UI does not actually know they are unmerged. That overstates attention work and does not satisfy the spec's requirement to summarize actual actionable items.
Suggested fix: change the predicate to only mark Done items as `Not merged` for explicit non-merged states, for example `issue.mergeState != null && issue.mergeState !== 'merged'`, or enumerate the allowed non-merged terminal states explicitly.

2. Error - `packages/cli/web/src/components/KanbanBoard.tsx:69-184`, `packages/cli/web/src/components/KanbanBoard.tsx:319-355`
The mobile homepage still renders the full priority control row, label picker trigger, and search bar ahead of the stage tabs, with only sort separated into a second row. There is no mobile-specific disclosure, collapse, or condensed active-filter summary for priority and labels. That falls short of the accepted mobile behavior for compact controls that keep issue content visible in the first screen.
Suggested fix: keep the search box and selected-filter summary visible, but move priority, labels, and sort into a mobile disclosure/sheet/accordion so the issue list appears immediately below the stage tabs.

## Spec Compliance

### REQ-WUI-209-001 Homepage is a decision-first work entry
FAIL
Evidence:
- `packages/cli/web/src/components/KanbanBoard.tsx:316-317` renders the `Needs attention` summary above the board.
- `packages/cli/web/src/components/KanbanBoard.tsx:232-242` links each summary item directly to `/issue/{number}`.
- `packages/cli/web/src/lib/homepage-attention.ts:18-73` maps approval, integrate failure, interrupted, blocked, and done cases to user-facing labels.
- `packages/cli/web/src/lib/homepage-attention.ts:65` also treats missing `mergeState` as `Not merged`, so the summary can include false-positive actionable items.

### REQ-WUI-209-002 Desktop and mobile board layouts preserve work visibility
FAIL
Evidence:
- `packages/cli/web/src/components/KanbanBoard.tsx:381-404` restores the desktop board to a horizontal `md:flex flex-row` container.
- `packages/cli/web/src/components/StageColumn.tsx:58-61`, `83-119` visually de-emphasize the Done column while keeping it available.
- `packages/cli/web/src/components/KanbanBoard.tsx:69-184` keeps priority and label controls always visible on mobile instead of collapsing secondary controls.
- `packages/cli/web/src/components/KanbanBoard.tsx:319-355` shows the mobile stage tabs only after the full filter bar, so the implementation does not meet the compact mobile-control requirement.

### REQ-WUI-209-003 Homepage label filtering reaches all labels
PASS
Evidence:
- `packages/cli/web/src/components/KanbanBoard.tsx:105-173` replaces the eight-label slice with a searchable full-label popover.
- `packages/cli/web/src/components/KanbanBoard.tsx:284-289` serializes board state back into the URL.
- `packages/cli/web/src/components/KanbanBoard.tsx:263-267` restores board state from `popstate`.
- `packages/cli/web/src/components/KanbanBoard.tsx:175-182` binds the visible search input directly to restored URL state.

### REQ-WUI-209-004 Homepage regressions are covered by tests
PASS
Evidence:
- `packages/cli/web/src/components/kanban-board-query.test.tsx:388-431` covers the desktop horizontal multi-column contract.
- `packages/cli/web/src/components/kanban-board-query.test.tsx:434-628` covers the `Needs attention` summary wording.
- `packages/cli/web/src/components/kanban-board-query.test.tsx:631-778` covers URL restoration and label reachability beyond the first eight labels.
- Verified locally: `cd packages/cli && npm test -- --run web/src/components/kanban-board-query.test.tsx` passes.
- Verified locally: `cd packages/cli && npm run build` passes.

## Overall

FAIL due to the two error-level issues above.

<promise>FAIL</promise>
