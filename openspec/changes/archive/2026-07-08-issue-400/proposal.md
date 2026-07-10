## Why

The issue board is an owner's primary tool to scan the production line, yet at a common desktop width a core status group can be pushed off-screen behind horizontal overflow, the filter/search/sort row can push the board below the useful first screen, and on mobile competing navigation and action surfaces overlap so the user cannot cleanly switch groups, find an issue, and act on it. This change makes the board scannable on desktop and basically usable on mobile. It is needed now because issue 398 landed the shared status/theme baseline the board depends on, so the surface can be tuned for scannability without re-litigating color or status conventions.

## What Changes

- On desktop, the **core** status groups (Backlog, In Progress, Done) are reachable without hiding a full group off-screen by default at a common desktop width. The Cancelled group may remain lower priority or collapsed by default (it is not removed).
- Issue cards stay **compact while preserving** issue number, title, priority, status signal, workflow stage, and health signal, so owner decisions keep the context they need without losing density.
- Filter, search, and sort controls are kept **reachable without pushing the board content below the useful first screen**.
- On mobile, the user can **switch board groups, open an issue, and use the primary board action** with competing navigation/action surfaces reduced so they do not overlap or force excessive horizontal scanning.
- Mobile support is scoped as **basic board use** (group switch, find, open, primary action), not a complete mobile-first workflow.
- No new issue statuses or workflow stages are added; cancelled is not removed. **No backend/workflow/runner change** — only the board's layout, density, and navigation surface.

Non-goals (per issue): no full PWA or push notifications; no issue-detail redesign; no new statuses/stages; no removal of the cancelled group.

## Capabilities

- `issue-board`: The issue board as a scannable surface across desktop and mobile — desktop default reachability of the core status groups (no full core group hidden off-screen by default; cancelled may collapse by default), compact card density that preserves the six decision dimensions (number, title, priority, status signal, workflow stage, health signal), first-screen reachability of filter/search/sort, and basic mobile board navigation (switch groups, open an issue, primary board action) with reduced competing surfaces and no overlap. This **extends** the existing `issue-board` capability (which already covers desktop horizontal-scroll containment, card top-row density convergence, single global sort, priority color strip, and cross-layout regression); the spec gains requirements for default core-group reachability, the six-dimension card-density invariant, first-screen filter/search/sort, and non-overlapping mobile navigation.

## Impact

- **Affected code (Web only, `packages/web/src`):**
  - Board composition & layouts: `widgets/kanban-board/ui/KanbanBoard.tsx` (desktop column row vs. mobile tab/list split, cancelled default-collapse, first-screen layout balance).
  - Column rendering: `widgets/kanban-board/ui/StageColumn.tsx` (column width/flex tuning so core groups fit by default; cancelled collapse affordance).
  - Card density: `widgets/kanban-board/ui/IssueCard.tsx` (preserve number/title/priority/status/stage/health while compacting).
  - Filter bar: the `FilterBar` component in `KanbanBoard.tsx` (keep search/filter/sort reachable without consuming the first screen).
  - Grouping/query models: `widgets/kanban-board/model/kanban-grouping.ts`, `model/board-query.ts` (only if reachability/collapse requires a model seam; behavior stays data-source-unchanged).
- **Tests:** `widgets/kanban-board/ui/kanban-board-*.test.tsx`, `IssueCard.test.tsx` — add/adjust spec tests for default core-group reachability, the six-dimension card-density invariant, first-screen filter/search/sort, and non-overlapping mobile navigation. Preserve existing `data-testid` anchors.
- **APIs / dependencies / systems:** none changed. No server, runner, or CLI impact; no new dependency. All data sourced from existing queries (`useIssues`, `useAgentStatus`).
- **Risk (medium):** this changes a core issue-browsing flow across desktop and mobile breakpoints. Confined to board presentation/layout; mitigated by extending the existing `issue-board` spec rather than splitting capabilities and by asserting every conditional path (cancelled collapse, blocked/approval/running card signals, mobile tab + list) against the new reachability/density invariants.
