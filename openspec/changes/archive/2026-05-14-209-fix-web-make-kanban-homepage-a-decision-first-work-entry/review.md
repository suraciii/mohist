## Review Result

No error-level findings.

## Warnings

1. `packages/cli/web/src/components/kanban-board-query.test.tsx:406`, `:427`, `:761`, `:796`
Suggested change: replace class-name selectors such as `.hidden.md\\:flex.flex-row` and `[class*="origin-top-right"]` with stable `data-testid` hooks on the desktop board row and label popover.
Reason: the current tests do catch the intended regressions, but they are more brittle than the design guidance in `design.md:102` and may fail on harmless Tailwind refactors.

2. `packages/cli/web/src/components/StageColumn.tsx:105-116`
Suggested change: align the Done-column archive copy with the rest of the homepage language, or explicitly confirm bilingual copy is intended.
Reason: this change introduces user-facing Chinese strings (`已归档`, `查看`, `归档所有已完成`) into a page whose surrounding controls and new summary labels are otherwise English.

## Correctness

- PASS: desktop board layout is repaired by switching the desktop container to `hidden md:flex flex-row ...` in `packages/cli/web/src/components/KanbanBoard.tsx:430-453`.
- PASS: attention derivation is centralized in a focused helper instead of being spread through JSX, via `packages/cli/web/src/lib/homepage-attention.ts:13-86`.
- PASS: direct issue navigation from the summary is implemented with links to `/issue/:number` in `packages/cli/web/src/components/KanbanBoard.tsx:281-293`, which matches the app routes in `packages/cli/web/src/App.tsx:105-107`.
- PASS: URL-backed filter state is preserved through `parseBoardQuery`, `serializeBoardQuery`, `pushState`, and `popstate` handling in `packages/cli/web/src/components/KanbanBoard.tsx:306-339`.

## Complexity

- PASS: the new homepage-specific logic is split sensibly across `FilterBar`, `NeedsAttentionSummary`, and `deriveAttentionItems`, keeping the attention selector small and readable.
- PASS: `deriveAttentionItems` remains straightforward, with a single pass over issues and explicit precedence rules in `packages/cli/web/src/lib/homepage-attention.ts:27-85`.

## Test Coverage

- PASS: regression tests cover horizontal desktop board layout in `packages/cli/web/src/components/kanban-board-query.test.tsx:388-432`.
- PASS: regression tests cover user-action wording in the `Needs attention` summary in `packages/cli/web/src/components/kanban-board-query.test.tsx:434-679`.
- PASS: regression tests cover hidden-label reachability and filtering beyond the first eight labels in `packages/cli/web/src/components/kanban-board-query.test.tsx:706-855`.
- PASS: verification succeeded locally with `cd packages/cli && npm test` and `cd packages/cli && npm run build`.

## Security

- PASS: this is a frontend-only change with no new backend surface, no secret handling, and no dynamic HTML injection.
- PASS: label search/filter behavior uses normal React state and rendering rather than unsafe interpolation.

## Spec Compliance

### REQ-WUI-209-001 Homepage is a decision-first work entry

- PASS: the summary renders above the board when actionable items exist because `KanbanBoard` renders `<NeedsAttentionSummary items={attentionItems} />` before the filter bar and board in `packages/cli/web/src/components/KanbanBoard.tsx:364-374`, and `NeedsAttentionSummary` returns `null` only when `items.length === 0` in `packages/cli/web/src/components/KanbanBoard.tsx:275-296`.
- PASS: actionable labels are user-facing (`Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, `Not merged`) in `packages/cli/web/src/lib/homepage-attention.ts:34-80`.
- PASS: summary items open the issue directly via `href={`/issue/${item.issueNumber}`}` in `packages/cli/web/src/components/KanbanBoard.tsx:282-285`.
- PASS: board navigation remains below the summary in `packages/cli/web/src/components/KanbanBoard.tsx:375-453`.

### REQ-WUI-209-002 Desktop and mobile board layouts preserve work visibility

- PASS: desktop columns are side by side in a horizontal board container via `hidden md:flex flex-row gap-4 overflow-x-auto` in `packages/cli/web/src/components/KanbanBoard.tsx:430-453`.
- PASS: stage columns still honor shared filtering/sorting because `displayedColumns` is derived once from `deriveBoardColumns(allColumns, localState)` and reused by both mobile and desktop renders in `packages/cli/web/src/components/KanbanBoard.tsx:319-344`, `:375-453`.
- PASS: mobile keeps the single-stage model with stage tabs plus one selected column body in `packages/cli/web/src/components/KanbanBoard.tsx:375-428`.
- PASS: mobile secondary controls are collapsed behind the `Filters` toggle in `packages/cli/web/src/components/KanbanBoard.tsx:206-233`.
- PASS: Done remains available and de-emphasized with reduced opacity/background treatment in `packages/cli/web/src/components/StageColumn.tsx:58-61` and `:83-103`.

### REQ-WUI-209-003 Homepage label filtering reaches all labels

- PASS: the first-eight-label slice is removed; label search/filter uses the full `allLabels` list in `packages/cli/web/src/components/KanbanBoard.tsx:48-52` and renders `filteredLabels.map(...)` in `:146-168`.
- PASS: selected labels still flow through the existing URL-backed board query model by updating `state.labels` and serializing via `serializeBoardQuery` in `packages/cli/web/src/components/KanbanBoard.tsx:65-72`, `:334-339`.
- PASS: the label control remains compact by using a popover with search instead of inline chips for the whole label set in `packages/cli/web/src/components/KanbanBoard.tsx:112-184`.

### REQ-WUI-209-004 Homepage regressions are covered by tests

- PASS: desktop multi-column regression coverage exists in `packages/cli/web/src/components/kanban-board-query.test.tsx:388-432`.
- PASS: hidden-label regression coverage exists in `packages/cli/web/src/components/kanban-board-query.test.tsx:740-855`.
- PASS: attention-summary wording coverage exists in `packages/cli/web/src/components/kanban-board-query.test.tsx:434-679`.
- PASS: the required test/build verification completed successfully with `npm test` and `npm run build` in `packages/cli`.

## Overall

PASS with warnings.

<promise>PASS</promise>
