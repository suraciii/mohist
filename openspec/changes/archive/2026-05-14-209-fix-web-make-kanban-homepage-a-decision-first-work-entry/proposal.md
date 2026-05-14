## Why

The Mohist homepage is the default operational surface, but it currently fails to answer the user's first question: what needs attention now. After #198, the page gained stronger filter and sort controls while regressing its desktop Kanban layout and still leading with configuration instead of next-action decisions, so the homepage now feels unreliable exactly where users decide what to do next.

## What Changes

- Restore the desktop Kanban layout so stage columns remain horizontally visible side by side at `md+` widths instead of stacking vertically.
- Reframe the homepage as a decision-first work entry by adding a compact `Needs attention` summary above the board for actionable states such as approval needed, integration failed, interrupted, blocked/needs action, and done-but-not-merged work.
- Preserve the #198 filter and sort model while making it secondary to the attention summary and compact enough on mobile that issue content is visible in the first screen.
- Expand label filtering so users can reach all project labels rather than only the first eight surfaced in the current filter bar.
- Visually de-emphasize done/history compared with active and attention work without removing the Kanban board as the main browsing surface.
- Add regression coverage for desktop horizontal column visibility and for label filtering behavior beyond the first eight labels.

## Capabilities

### New Capabilities

_None_

### Modified Capabilities

- `web-ui` — the homepage Kanban experience changes from a raw board-first surface to a decision-first work entry with an attention summary, compact mobile controls, full label reachability, horizontally visible desktop columns, and stronger regression guarantees around homepage usability.

## Impact

- `packages/cli/web/src/components/KanbanBoard.tsx` — homepage information hierarchy, filter presentation, label selection behavior, mobile compaction, and desktop board container layout.
- `packages/cli/web/src/components/StageColumn.tsx` — column presentation and done/history emphasis within the desktop board.
- `packages/cli/web/src/components/IssueCard.tsx` and related Web UI helpers/types — likely touchpoints for attention-state wording and compact homepage surfacing.
- `packages/cli/web/src/hooks/useQueries.ts` and existing labels data flow — reused to expose all project labels in the homepage filter UI without backend endpoint changes.
- `packages/cli/web/src/components/kanban-board-query.test.tsx` and related component tests — expanded to catch horizontal layout regressions and hidden-label filter regressions.
- No new backend, database, or dependency requirements are expected; existing issue and label APIs already provide the data needed for this homepage change.
