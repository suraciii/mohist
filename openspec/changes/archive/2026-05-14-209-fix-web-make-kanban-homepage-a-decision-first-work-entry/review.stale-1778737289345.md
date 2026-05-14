## Findings

1. Error - `packages/cli/web/src/lib/homepage-attention.ts:65`
The Done attention rule now treats every Done issue whose `mergeState` is anything other than `'merged'` as `Not merged`, including `undefined`. `Issue.mergeState` is optional in `packages/cli/web/src/lib/types.ts:51`, so older or partial records with no merge result will be surfaced as actionable even when the system has not actually determined that they are unmerged. This is broader than the spec's "done issues that are not merged" requirement and will create false-positive attention items.
Suggested fix: tighten the predicate to explicit non-merged terminal values such as `'build-failed' | 'conflict' | 'blocked' | 'pending' | 'merging' | 'rebasing' | 'resolving'`, or normalize missing merge state before deriving attention.

2. Error - `packages/cli/web/src/components/KanbanBoard.tsx:41-56`, `packages/cli/web/src/components/KanbanBoard.tsx:272-276`
The board state is not fully restored from the URL for the search control. `FilterBar` copies `state.search` into local `searchValue` once, but never re-syncs it when `localState` changes on `popstate`. The board results will change after browser back/forward navigation, while the visible search input can still show the stale previous value. That breaks the URL-backed restoration contract for the homepage controls.
Suggested fix: either remove the extra `searchValue` state and bind the input directly to `state.search`, or add an effect that updates `searchValue` whenever `state.search` changes.

## Spec Compliance

### REQ-WUI-209-001 Homepage is a decision-first work entry
PASS
Evidence:
- `packages/cli/web/src/components/KanbanBoard.tsx:229-255` renders `NeedsAttentionSummary` above the board.
- `packages/cli/web/src/components/KanbanBoard.tsx:241-244` links summary items directly to `/issue/{number}`.
- `packages/cli/web/src/lib/homepage-attention.ts:18-73` maps actionable cases to user-facing labels including `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, and `Not merged`.
Note:
- The Done-item false-positive issue above still affects correctness of the summary contents.

### REQ-WUI-209-002 Desktop and mobile board layouts preserve work visibility
PASS with warning
Evidence:
- `packages/cli/web/src/components/KanbanBoard.tsx:390-413` uses `hidden md:flex flex-row ... overflow-x-auto`, restoring a horizontal desktop board row.
- `packages/cli/web/src/components/KanbanBoard.tsx:328-388` preserves the mobile single-stage model.
- `packages/cli/web/src/components/StageColumn.tsx:58-61`, `83-119` visually de-emphasize Done via lower-opacity chrome and muted footer styling while keeping it available.
Warning:
- Mobile compaction is only partially addressed in code. Search, priority, and label controls are still always visible in `packages/cli/web/src/components/KanbanBoard.tsx:78-193`, with only sort moved into a secondary row.

### REQ-WUI-209-003 Homepage label filtering reaches all labels
FAIL
Evidence:
- `packages/cli/web/src/components/KanbanBoard.tsx:114-181` replaces the first-eight slice with a searchable full-label popover, so label reachability is implemented.
- `packages/cli/web/src/components/KanbanBoard.tsx:293-298` writes board state to the URL.
- `packages/cli/web/src/components/KanbanBoard.tsx:272-276` restores `localState` from URL on `popstate`.
- `packages/cli/web/src/components/KanbanBoard.tsx:41-56` keeps a separate `searchValue` that is not synchronized with restored URL state, so the search control can display stale state after navigation.

### REQ-WUI-209-004 Homepage regressions are covered by tests
PASS with warning
Evidence:
- `packages/cli/web/src/components/kanban-board-query.test.tsx:388-431` covers the desktop horizontal multi-column contract.
- `packages/cli/web/src/components/kanban-board-query.test.tsx:434-628` covers attention-summary user wording.
- `packages/cli/web/src/components/kanban-board-query.test.tsx:631-745` covers label reachability beyond the first eight labels.
- Verified locally: `cd packages/cli && npm test -- --run web/src/components/kanban-board-query.test.tsx` passes; `cd packages/cli && npm run build` passes.
Warning:
- No test covers URL restoration of the visible search input after back/forward navigation, which is where the current regression escapes.

## Overall

FAIL due to the two error-level issues above.

<promise>FAIL</promise>
