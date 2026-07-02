## Why

The issue kanban board is the primary surface for triaging work, but today it is a "field-by-field literal translation" UI that fails the first-screen scan: the Cancelled column is pushed off-screen at desktop widths and its horizontal scroll drags the left navigation with it; each card crams seven top-row elements together; every-read text (issue number, timestamp) sits at sub-WCAG-AA contrast; sort controls are duplicated across the top bar and every column header bound to the same state; and the left color strip — meant for quick categorization — collapses to gray for almost every card because it keys off type labels that most issues lack. The board buries what matters instead of surfacing it. This change converges the board from "show everything" to "show what's scannable", with accessibility (WCAG AA) treated as a hard requirement rather than a preference.

## What Changes

- **Desktop layout owns its own scroll**: the four-column board (Backlog / In Progress / Done / Cancelled) is fully visible without clipping at a 1440px viewport and never triggers whole-page horizontal scroll; when columns exceed the content area, scrolling is confined to the board region and does not displace the left navigation.
- **Card top row converged**: the default top row keeps only issue number, priority, and the single dominant status. The full workflow-profile string is no longer rendered by default — it becomes a hover hint (a `title` is retained so the value stays inspectable).
- **Stage and status no longer stack as two pills**: when a card already carries a Running / Approval / Blocked / Drift etc. status pill, the stage information folds into that status expression instead of rendering an independent stage pill. Stage progress numbers render as part of the stage label, not as a standalone unit.
- **WCAG AA contrast enforced**: issue numbers, timestamps, and other per-card auxiliary text reach ≥ 4.5:1 contrast; status pills and priority pills' background/text combinations likewise reach ≥ 4.5:1.
- **Single sort entry**: the board keeps exactly one global sort control (in the top filter bar). The per-column-header sort button groups are removed, eliminating five redundant controls bound to one piece of state.
- **Color strip driven by priority**: the card's left color strip takes its color from the issue priority (e.g. P0 red / P1 orange / P2 yellow / P3 green / P4 gray) instead of from type labels, giving a deterministic, always-distinct categorization dimension.

## Capabilities

### New Capabilities

- `issue-board`: The issue kanban board's visual and interaction layer — desktop four-column layout containment (no whole-page horizontal overflow; board region owns its scroll), card top-row density (hover-revealed workflow profile, stage folded into the status expression rather than stacked as a second pill, progress as part of the stage label), WCAG AA contrast for per-card auxiliary text and for status/priority pills, a single global sort control, and a priority-driven card color strip.

### Modified Capabilities

<!-- None. The behaviors being changed (card top-row contents, per-column sort controls, color-strip source, board scroll containment, per-card text/pill contrast) are not currently governed by any existing spec requirement — the existing `web-ui` stage-progression requirement concerns stage-list correctness (no synthesized Done stage), which is unaffected here. -->

## Impact

- **packages/web (kanban-board widget)**: `widgets/kanban-board/ui/KanbanBoard.tsx` (desktop columns container scroll containment; removal of the per-column sort pass-through to `StageColumn`; filter bar remains the single sort surface), `widgets/kanban-board/ui/StageColumn.tsx` (drop the in-header sort button group), `widgets/kanban-board/ui/IssueCard.tsx` (converged top row: workflow profile to hover-only `title`, stage/status pill deduplication, progress-as-stage-label, elevated text/pill contrast, priority-driven strip color).
- **packages/web (shared lib)**: `shared/lib/label-colors.ts` — add a priority→strip-color mapping (P0 red / P1 orange / P2 yellow / P3 green / P4 gray) and verify/adjust `getPriorityStyle` and the status-pill color pairs for WCAG AA contrast.
- **packages/web (layout)**: `pages/issues/ui/IssuesPage.tsx` and the board's root container — constrain the board region so its horizontal scroll never propagates to the app shell / left navigation.
- **Regression scope**: desktop and mobile layouts both affected; full regression of filtering, sorting, Done collapse, Show/Hide cancelled, Archive / Archive-all-done, and the Needs-Attention banner. No schema migration, no API contract change, no backend change — purely Web visual/interaction layer.
