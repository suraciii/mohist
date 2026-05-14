## Findings

1. Error: integrate failures are only partially surfaced in the `Needs attention` selector.
File: `packages/cli/web/src/lib/homepage-attention.ts:26-40,49-57`
`deriveAttentionItems` only maps integrate failures to `Integration failed` when the issue is in `Stage.Integrate` and either `status` is `blocked`/`interrupted` or `mergeState === 'build-failed'`. Existing merge failure states also include `conflict` and `blocked` (`packages/cli/web/src/lib/types.ts:51`, `packages/cli/web/src/components/MergeStatePanel.tsx:144-228`), but those are not treated as integrate failures here. A `Stage.Integrate` issue with `mergeState: 'conflict'` is omitted entirely, and `mergeState: 'blocked'` falls through to the generic `Needs action` branch instead of `Integration failed`.
Suggested fix: expand the integrate-failure branch in `packages/cli/web/src/lib/homepage-attention.ts` to include the other merge-failure states used elsewhere in the UI, at minimum `conflict` and `blocked`, and keep the primary summary label as `Integration failed` for all integrate-stage merge failures.

2. Error: mobile controls are not compacted; the page still renders the full priority/filter surface ahead of issue content.
File: `packages/cli/web/src/components/KanbanBoard.tsx:69-184,319-355`
The same always-expanded `FilterBar` is rendered on all breakpoints, with five visible priority chips, labels control, and search input. Mobile also renders a second dedicated sort row below the stage tabs. The design/spec required secondary controls to collapse on mobile so issue content remains visible in the first screen, but this implementation does not add any mobile-only disclosure/sheet/accordion behavior for priority, labels, or sort.
Suggested fix: move mobile secondary controls behind a compact disclosure in `packages/cli/web/src/components/KanbanBoard.tsx`, keeping search and active-filter summary visible while hiding priority/labels/sort until expanded.

## Correctness

- FAIL: `deriveAttentionItems` misses or mislabels valid integrate-failure states (`packages/cli/web/src/lib/homepage-attention.ts:26-57`).

## Complexity

- PASS: the new helper is small (`packages/cli/web/src/lib/homepage-attention.ts:11-77`) and the main component changes stay moderate in size.

## Test Coverage

- PASS with gaps: `packages/cli/web/src/components/kanban-board-query.test.tsx` adds regression tests for desktop row layout, attention wording, label reachability, and URL search restoration.
- FAIL against spec intent for mobile: there is no regression coverage for the required compact mobile-control behavior.
- Evidence: `npm test` passed in `packages/cli`.

## Security

- PASS: no new backend/API surface, no secret exposure, and no obvious injection risk in the reviewed changes.

## Spec Compliance

- PASS: Desktop Kanban columns render horizontally side by side at `md+` widths while preserving filter/sort behavior.
Evidence: `packages/cli/web/src/components/KanbanBoard.tsx:381-404` uses `hidden md:flex flex-row overflow-x-auto`; coverage exists in `packages/cli/web/src/components/kanban-board-query.test.tsx:388-431`.

- PASS: A compact `Needs attention` summary is rendered above the board for actionable items when present.
Evidence: `packages/cli/web/src/components/KanbanBoard.tsx:220-247,314-317`; derivation in `packages/cli/web/src/lib/homepage-attention.ts:11-77`.

- PASS with deviation: The summary uses user-action language such as `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, and `Not merged`.
Evidence: `packages/cli/web/src/lib/homepage-attention.ts:18-72`, tests at `packages/cli/web/src/components/kanban-board-query.test.tsx:434-608`.
Deviation: integrate failures are not comprehensively covered for all merge-failure states; see Finding 1.

- FAIL: Mobile homepage does not compact/collapse secondary controls enough to demonstrate first-screen work visibility.
Evidence: `packages/cli/web/src/components/KanbanBoard.tsx:69-184` renders full filter controls on all breakpoints, and `:350-355` adds another visible mobile sort row. No mobile-compaction test exists.

- PASS: Label filtering can access labels beyond the first eight.
Evidence: label popover searches `allLabels` without slicing in `packages/cli/web/src/components/KanbanBoard.tsx:41-47,105-172`; tests at `packages/cli/web/src/components/kanban-board-query.test.tsx:665-778` cover discovery and selection beyond the first eight.

- PASS: Done/history remains available and visually de-emphasized.
Evidence: `packages/cli/web/src/components/StageColumn.tsx:58-61,83,102-119` applies muted opacity/color treatment while preserving Done content and archive actions.

- PASS: Regression coverage exists for desktop horizontal layout, label reachability beyond eight labels, and attention wording.
Evidence: `packages/cli/web/src/components/kanban-board-query.test.tsx:388-431,434-608,665-778`.

- PASS: Build/test verification completed.
Evidence: `npm test` and `npm run build` both passed in `packages/cli`.

## Overall

- FAIL: the implementation is close, but it does not fully satisfy the mobile compaction requirement and it incompletely classifies integrate failures in the attention summary.

<promise>FAIL</promise>
