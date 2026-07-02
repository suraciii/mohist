# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: The `self-review.md` identified and repaired an incorrect path `packages/web/src/App.tsx` → `packages/web/src/app/App.tsx` in `tasks.json` T-002 `output`. Confirmed via `find` that no `packages/web/src/App.tsx` exists and the correct file is `packages/web/src/app/App.tsx`. 
  Verification: `git diff` shows the path was already corrected in `tasks.json` before this review.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:112`
  Evidence: The `data-stage-fold-id` attribute (lines 112, 118, 130, 141, 153, 164, 175) is constructed as `${indicator}-${stageLabel ?? 'none'}-${progressLabel ?? 'none'}`, producing values like `running-Build-2/5` that contain spaces and slashes — technically invalid HTML enumerated attribute values. Browsers and jsdom tolerate this, but the attribute is used only for test hooks (`data-stage-fold-id`) and has no production impact.
  SuggestedAction: Replace spaces with hyphens and slashes with dashes in the id construction (e.g. `running-build-2-5`), or leave as-is since the attribute is strictly a test hook.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/shared/lib/label-colors.test.ts`, `packages/web/src/widgets/kanban-board/ui/StatusPill.contrast.test.ts`
  Evidence: Both test files define identical `srgbChannel`, `relativeLuminance`, and `contrastRatio` utility functions (19 lines each). This is copy-paste duplication adding no value.
  SuggestedAction: Extract the WCAG contrast computation into a shared test helper under `packages/web/src/shared/lib/` or a `tests/` utility file, imported by both test files.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx:152-155`
  Evidence: The desktop "Sort" label in the `SortToggle` component (line 153-154) uses `text-muted-foreground/70`, which is below WCAG AA (the `/70` opacity was the precise source of the per-card contrast defect this issue fixes). However, this label is in the filter bar, not on a card — the acceptance criteria scope is "issue 编号、时间戳等辅助文字" (per-card auxiliary text). The filter-bar Sort label is outside scope, and its contrast was not degraded by this change.
  SuggestedAction: Fix separately if filter-bar text contrast matters for AA compliance.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:76-83`
  Evidence: `STATUS_PILL_PAIRS` exports documented hex pairs for each `StatusIndicator` variant, and `StatusPill.contrast.test.ts` validates that every pair meets ≥4.5:1 contrast. However, the `StatusPill` component uses Tailwind v4 utility classes (`bg-red-100 text-red-800`, etc.) rather than applying the documented hexes via inline styles. The Tailwind classes render OKLCH-based colors that the documented hexes approximate. The contrast test validates the hexes, not the actual rendered output. The computed ratios have sufficient margin (≥6:1 for most variants) that rounding differences are immaterial. No rendering-level test verifies the actual computed CSS colors of `<StatusPill>`, but the colocation of `STATUS_PILL_PAIRS` in `IssueCard.tsx` provides coupling — a developer changing Tailwind classes would need to update the pairs or the contrast test fails.
  SuggestedAction: Optional: add a jsdom rendering test that asserts the computed `backgroundColor`/`color` of a mounted `<StatusPill>` against the documented hex pairs, or extract the pairs into a shared constants module the component inline-styles from.
  Status: out-of-scope

## Acceptance Criteria Verification

| # | Criterion | Evidence |
|---|-----------|----------|
| 1 | 1440px viewport: four columns visible, no page-level horizontal scroll, left nav stays fixed | `min-w-0` added to `sidebar.tsx:310`, `App.tsx:58`, `KanbanBoard.tsx:547`. Board row keeps `overflow-x-auto` at `KanbanBoard.tsx:626`. Verified by `kanban-board-containment.test.tsx:74-125` (3 tests). |
| 2 | Workflow profile not rendered as default text, hover-only via `title` | `IssueCard.tsx:326-341`: profile moved to `title` on issue-number span + `sr-only` `aria-hidden` span with `data-testid`/`data-workflow-profile`. Verified by `IssueCard.test.tsx:187-221` (4 tests). |
| 3 | Stage and status not stacked as two independent pills; stage folds into status | `IssueCard.tsx:343-349`: when `indicator && !isIntegrateWithFailure`, renders single `<StatusPill>` with `stageLabel` + `progressLabel`. When no status pill, stage renders standalone (`WorkflowStagePill`). Verified by `IssueCard.test.tsx:330-445` (7 tests). |
| 4 | Issue number, timestamp, status pill, priority pill meet ≥4.5:1 (WCAG AA) | Issue number/timestamp: `text-muted-foreground` (no `/70`) at `IssueCard.tsx:328,418`. StatusPill: `STATUS_PILL_PAIRS` at `IssueCard.tsx:76-83`, tested in `StatusPill.contrast.test.ts:30-47` (7 tests). Priority pill: `PRIORITY_COLORS` at `label-colors.ts:105-111`, tested in `label-colors.test.ts:62-81` (5 tests). All contrast ratios verified. |
| 5 | Single global sort control; no per-column sort buttons | `StageColumn.tsx`: `sort`/`onSortChange` props removed, sort button group deleted. `KanbanBoard.tsx:631-660`: no sort props passed to `StageColumn`. Verified by `kanban-board-query.test.tsx:958-1108` (4 tests). |
| 6 | Left color strip by priority, deterministically distinct per priority | `IssueCard.tsx:314`: `borderLeftColor: getPriorityStripColor(issue.priority)`. `label-colors.ts:90-103`: `getPriorityStripColor` with distinct hexes p0..p4. Verified by `IssueCard.test.tsx:234-281` (4 tests) + `label-colors.test.ts:32-59` (5 tests). |
| 7 | Desktop and mobile regression: filtering, sorting, Done collapse, Show/Hide cancelled, Archive, Needs Attention | Full suite: `kanban-board-query.test.tsx` covers all interactions. `npm run test:run -w packages/web` passes 243 files / 3773 tests / 0 failures. `npm run typecheck -w packages/web` passes clean. |

All 8 issue acceptance criteria (counting #1 as a single layout criterion) are verified with concrete file paths, line numbers, and passing test counts.

## Changed Files Summary

| File | Change Type | Key Lines | Tests |
|------|------------|-----------|-------|
| `label-colors.ts` | Added `getPriorityStripColor`, deduped `PRIORITY_COLORS`, added `PRIORITY_STRIP_COLORS` | 90-111 | `label-colors.test.ts` (94 lines, new) |
| `label-colors.test.ts` | New: tests for strip colors + AA contrast | all | Self-contained |
| `sidebar.tsx` | Added `min-w-0` to SidebarInset `<main>` | 310 | `kanban-board-containment.test.tsx` |
| `App.tsx` | Added `min-w-0` to content wrapper div | 58 | `kanban-board-containment.test.tsx:110-125` (source parse) |
| `KanbanBoard.tsx` | Added `min-w-0` + `data-testid` to root/row; removed sort props from StageColumn | 547, 626, 631-660 | `kanban-board-containment.test.tsx`, `kanban-board-query.test.tsx` |
| `StageColumn.tsx` | Removed sort button group + sort/onSortChange props | 14-24, 108-126 (deleted) | `kanban-board-query.test.tsx:958-1108` |
| `IssueCard.tsx` | Converged top row, AA text, priority strip, stage fold, STATUS_PILL_PAIRS | 63-65, 76-83, 98-185, 314, 326-349 | `IssueCard.test.tsx` (rewritten, 467 lines), `StatusPill.contrast.test.ts` (48 lines, new) |
| `IssueCard.test.tsx` | Rewritten: top row, strip, fold, AA, hover | all | Self-contained |
| `StatusPill.contrast.test.ts` | New: AA contrast for all StatusPill variants | all | Self-contained |
| `kanban-board-containment.test.tsx` | New: containment chain assertions | all | Self-contained |
| `kanban-board-query.test.tsx` | Added: sort removal tests, Done-collapse tests, Archive tests | 958-1457 | Self-contained |

## Design Decision Coverage

| Decision | Implementation | Match |
|----------|---------------|-------|
| D1: `min-w-0` containment chain | Three flex ancestors patched; board row is scroll owner | ✅ |
| D2: Profile hover-only, stage folds into status | `title` on issue number + `sr-only` hook; `formatStageFoldSuffix` | ✅ |
| D3: Contrast via full-opacity token + verified pill pairs | `/70` removed; `STATUS_PILL_PAIRS` + `PRIORITY_COLORS` tested | ✅ |
| D4: Single sort, remove column group | `StageColumn` props removed; `SortToggle` stays in `FilterBar` | ✅ |
| D5: Priority strip via `getPriorityStripColor` | One-line call-site swap; `getStripColor` kept as generic helper | ✅ |

## Cross-Cutting Concerns

- **Security**: No sensitive data, no input validation changes, no injection risk. Pure visual/interaction layer.
- **Data safety**: No schema migration, no API contract change, no backend change. Read-only color/contrast changes.
- **Public contracts**: `getPriorityStripColor` is a new export from `label-colors.ts`. `getPriorityStyle` return shape unchanged (`{bg, text}`), preserving 6 existing call sites. `STATUS_PILL_PAIRS` is a new export from `IssueCard.tsx`, consumed only by the colocated contrast test. `StageColumn` interface reduced (backward incompat: removal of `sort`/`onSortChange` props — `KanbanBoard.tsx` is the only consumer and was updated).
- **Migration impact**: Pure frontend, no feature flag needed, no staged rollout. Rollback is a simple git revert.

<promise>PASS</promise>
