## Findings

1. High: Done-but-not-merged issues with `null`/`undefined` `mergeState` are omitted from the `Needs attention` summary, so REQ-WUI-209-001 is not fully implemented. `homepage-attention.ts` only emits `Not merged` when `issue.stage === 'done' && issue.mergeState && issue.mergeState !== 'merged'`, which excludes the false-done cases already treated elsewhere in the UI as not merged. See `packages/cli/web/src/lib/homepage-attention.ts:52` and the existing false-done contract in `packages/cli/web/src/components/IssueCard.tsx:31-34`. Suggested fix: change the done predicate to include `null` and `undefined`, for example `issue.stage === Stage.Done && issue.mergeState !== 'merged'`, and add a regression test covering `Done` + `mergeState: null`.

2. High: Integrate failures are mislabeled as `Needs action` when the issue is blocked/interrupted in `integrate`, because the generic blocked-status branch runs before the integrate-failure branch. That violates the required decision wording for integrate failures and can hide the primary action label behind a generic one. See `packages/cli/web/src/lib/homepage-attention.ts:25-44`; the current order labels any blocked issue as `Needs action` before the integrate-specific branch is reached. Related UI evidence: integrate blocked/interrupted work is treated as `Integration Failed` elsewhere in `packages/cli/web/src/components/IssueCard.tsx:90-95` and `packages/cli/web/src/components/PipelineView.tsx:1152`. Suggested fix: check integrate blocked/interrupted/build-failed cases before the generic blocked branch, and add a test for `stage: integrate` plus `status: blocked`.

## Spec Compliance

- REQ-WUI-209-001: FAIL
Evidence: `Needs attention` renders above the board and links to `/issue/:number` (`packages/cli/web/src/components/KanbanBoard.tsx:229-255`, `packages/cli/web/src/App.tsx:106`), and tests cover several user-facing labels (`packages/cli/web/src/components/kanban-board-query.test.tsx:434-557`). But done-not-merged items with `mergeState: null|undefined` are not surfaced (`packages/cli/web/src/lib/homepage-attention.ts:52`), and integrate blocked failures are downgraded to `Needs action` because of branch ordering (`packages/cli/web/src/lib/homepage-attention.ts:33-49`).

- REQ-WUI-209-002: PASS
Evidence: desktop board container is horizontal at `md+` via `hidden md:flex flex-row` (`packages/cli/web/src/components/KanbanBoard.tsx:390`), mobile remains single-stage with tabs (`packages/cli/web/src/components/KanbanBoard.tsx:328-388`), and Done is visually de-emphasized with reduced opacity and muted chrome (`packages/cli/web/src/components/StageColumn.tsx:58-61,83,102-119`). Regression tests cover horizontal desktop layout (`packages/cli/web/src/components/kanban-board-query.test.tsx:388-431`).

- REQ-WUI-209-003: PASS
Evidence: label filter no longer slices to eight; it searches `allLabels` directly (`packages/cli/web/src/components/KanbanBoard.tsx:44-48,114-181`). URL-backed state is still parsed/serialized and pushed to history (`packages/cli/web/src/lib/board-query.ts:13-50`, `packages/cli/web/src/components/KanbanBoard.tsx:293-298`). Tests cover discoverability and filtering for labels beyond the first eight (`packages/cli/web/src/components/kanban-board-query.test.tsx:580-694`).

- REQ-WUI-209-004: PASS with warning
Evidence: tests cover desktop horizontal layout, user-action attention wording, and hidden-label reachability (`packages/cli/web/src/components/kanban-board-query.test.tsx:388-694`). Warning: the current suite misses the two failing production cases above, so coverage exists but is incomplete for integrate blocked failures and false-done `mergeState: null`.

## Verification

- `cd packages/cli && npm test` : PASS
- `cd packages/cli && npm run build` : PASS

## Verdict

Overall: FAIL

<promise>FAIL</promise>
