## Context

The issue board (`packages/web/src/widgets/kanban-board/`) is an owner's primary tool to scan the production line. Issue 398 landed the shared status/theme baseline the board depends on, so the surface can now be tuned for scannability without re-litigating color or status conventions.

Current state of the board (verified against the code):

- **Desktop** renders a horizontal `flex-row` of `StageColumn`s inside an `overflow-x-auto` row (`KanbanBoard.tsx:644`). Each column is pinned `min-w-[280px] max-w-[320px]` (`StageColumn.tsx:74`). With four status groups (`Backlog`, `In Progress`, `Done`, `Cancelled`) that is ~1120–1280px of columns before the sidebar/content padding. At a common desktop width (≈1280–1440px viewport minus the app sidebar), the trailing group(s) — Cancelled, sometimes Done — get pushed behind the horizontal overflow. The Cancelled column is *body*-hidden by default (`showCancelled=false`, local state) but still **consumes a full `min-w-[280px]` column slot**, so hiding its body does not reclaim space for the core groups.
- **Mobile** already has a full `md:` (768px) CSS split: a snap tab strip of stages + a single selected-stage vertical list (`KanbanBoard.tsx:576`), plus a mobile FilterBar with a disclosure panel (`mobile-filter-toggle` / `mobile-filter-panel`). This is basic board use that mostly works; the gaps are surface overlap and unverified tap-target reachability.
- **Cards** (`IssueCard.tsx`) already render all six decision dimensions via a converged single top row (number + priority + exactly one status pill with the workflow stage *folded in*), a 2-line clamped title, labels, and a footer with Rerun/Resume (`rerun-button`). Health is surfaced through the status pill for blocked/approval/drift/waiting. No structural card change is required; the six-dimension invariant just is not asserted by tests today.
- **State**: filter/search/sort live in URL search params (`board-query.ts`, pushState + popstate sync). `showCancelled` and the mobile `selectedStage` are local component state. Single global `SortMode` applied to all columns. Status groups are fixed at 4 in `STAGES` (`kanban-grouping.ts:9`).
- **Styling**: Tailwind v4, CSS-first config, default breakpoints (`sm` 640 / `md` 768 / `lg` 1024 / `xl` 1280). The app shell (`MobileBottomNav`, `FAB`) and the existing board split use `md`; `IssueDetailPage`/session transcript use `lg` via a JS hook.

This is a **Web-only** change. No server, runner, CLI, API, dependency, or data-source change. All data continues to come from existing queries (`useIssues`, `useAgentStatus`).

## Goals / Non-Goals

**Goals:**

- At a common desktop width, the three core status groups (Backlog, In Progress, Done) are reachable by default — no full core group clipped behind the board's horizontal overflow.
- Cancelled may collapse by default but is never removed; its count stays discoverable and it expands in one action.
- Issue cards preserve all six decision dimensions (number, title, priority, status signal, workflow stage, health signal) while staying compact — locked by regression tests.
- Filter/search/sort stay reachable without pushing board content below the useful first screen.
- Mobile board navigation (switch groups, open an issue, primary action) works with no overlapping surfaces — locked by regression tests.

**Non-Goals:**

- No full PWA, push notifications, or offline workflow.
- No issue-detail redesign.
- No new issue statuses or workflow stages; Cancelled is not removed.
- No backend/runner/CLI/API/dependency change.
- Mobile is scoped to *basic* board use, not a complete mobile-first workflow (no new mobile-only features).

## Decisions

### D1. Default-collapse the Cancelled column to a compact stub, reclaim its slot for core groups

**Choice.** On desktop, when `showCancelled=false` (the default), render the Cancelled group as a **minimal collapsed stub** (count + expand affordance) instead of a full `min-w-[280px]` column with a hidden body. The three core columns then `flex-1` to fill the reclaimed space. Expanding Cancelled restores the full-width column (and, at narrow widths, the horizontal overflow remains as the escape hatch).

**Rationale.** Today the Cancelled column keeps its full width even when body-hidden, so hiding its body does not buy any horizontal room for the core groups — the exact problem the issue names. Collapsing the *slot*, not just the body, is the only way to make core groups reachable by default without shrinking card density. The data-layer identity seam (`filterCancelledFromColumns`, `kanban-grouping.ts:48`) stays untouched — this is purely a render-time concern, which is already the documented contract of that seam.

**Alternatives considered.**

- *Shrink column `min-w` so all four fit.* Rejected: 4 × ~240px still overflows common widths once the sidebar is counted, and it permanently hurts card density / the six-dimension invariant for the common case just to accommodate the rarely-scanned Cancelled group.
- *Always horizontally scroll, accept overflow.* Rejected: directly violates the core reachability acceptance criterion.
- *Drop Cancelled from the default board.* Rejected: explicitly forbidden by the non-goals.

### D2. Keep the breakpoint at `md` (768px), pure CSS, no JS hook

**Choice.** Continue the existing pure-CSS `md:` / `md:hidden` split for both the board and the FilterBar. Do **not** introduce `useIsMobile`/`useNarrowViewport`/`matchMedia` into the board.

**Rationale.** `md` is the app-shell mobile boundary (`MobileBottomNav`, `FAB` are `md:hidden`), so the board's mobile/desktop transition already coincides with the rest of the app's mobile chrome. A pure-CSS split keeps both DOM trees present, which lets the existing class-based + testid-based regression tests assert both layouts in one jsdom render without `matchMedia` stubbing. Adding a JS hook would buy nothing here (no layout decision needs JS that CSS can't make) and would add SSR/double-render cost.

**Alternatives considered.**

- *Move the split to `lg` (1024px) like `IssueDetailPage`.* Rejected: the detail page uses `lg` because its reference rail needs ≥1024px to be useful; the board's tab/list mobile layout is comfortable from 768px, and moving it would force tablet-width users onto the cramped desktop column row. `md` also matches the rest of the mobile chrome, avoiding a mismatched transition within the same app.

### D3. Codify the six-dimension card-density and reachability invariants in regression tests (no card restructure)

**Choice.** The card already renders all six dimensions through the converged top row and the folded status pill (`IssueCard.tsx`). Instead of restructuring the card, add regression tests that **assert the invariant**: every card exposes `issue-number`, a title, `priority-chip`, a status signal (`status-pill` or the folded stage), a workflow stage, and a health signal — and that the title stays line-clamped and sibling cards are not pushed out of compact density. Also assert the blocked/approval/waiting conditional paths keep stage folded into the status pill and keep number/title/priority visible (extending the existing `IssueCard.test.tsx` stage-fold cases).

**Rationale.** The structure is already correct; the risk is *regression* over time. Locking it in tests is the cheapest way to satisfy the density criterion without churning a working, already-converged card.

**Alternatives considered.**

- *Add a density/compact-mode toggle.* Rejected: out of scope (no new feature), and the card is already compact. A toggle adds state and test surface for no current need.

### D4. Keep FilterBar as a single compact header row on desktop; keep the mobile disclosure panel

**Choice.** Desktop FilterBar stays one `flex-wrap` row (search + priority chips + label popover + sort) above the board, as today. Add a regression assertion that board column content begins within the useful first screen (FilterBar does not stack into a tall block). Mobile keeps the existing `mobile-filter-toggle` → `mobile-filter-panel` disclosure so filters expand into a panel rather than consuming horizontal space beside the list.

**Rationale.** The FilterBar already satisfies the single-row requirement; the work is asserting it stays that way. The mobile disclosure is the right pattern (panel, not inline) and already exists.

### D5. Keep Cancelled-collapse and mobile-stage selection as local state (no URL persistence)

**Choice.** `showCancelled` and `selectedStage` remain local React state, not URL search params.

**Rationale.** Filter/search/sort are *shareable* query facets (URL-persisted so a link reproduces the view). Collapse state and which tab you happen to be on are *ephemeral* view preferences that a shared link should not impose on the recipient. Persisting them would surprise users and complicate the popstate round-trip tested in `kanban-board-query.regression.test.tsx`.

**Alternatives considered.**

- *Persist collapse state to URL/localStorage.* Rejected: surprising in shared links; not required by any acceptance criterion (the spec only requires the count stay discoverable, which the stub satisfies).

### D6. Preserve every existing `data-testid` anchor; add new ones only for the collapsed stub

**Choice.** Do not rename or remove existing testids (`kanban-board-root`, `kanban-board-row`, `stage-column-{status}`, `cancelled-toggle`, `mobile-stage-tab-{status}`, `mobile-cancelled-toggle`, `issue-card`, `issue-number`, `priority-chip`, `status-pill`, `rerun-button`, etc.). Add a testid for the collapsed cancelled stub (e.g. `cancelled-collapsed-stub`) and its expand affordance so reachability/collapse is assertable without coupling to class names.

**Rationale.** The existing anchors are the contract the test suite (and any external consumers) depend on. New behavior gets a new anchor rather than overloading an old one.

## Risks / Trade-offs

- **[Cancelled collapsed by default reduces at-a-glance cancelled visibility]** → Mitigated by the count chip on the collapsed stub and a one-tap expand; the mobile tab badge already shows the real count independent of collapse state (asserted today). Count discoverability is an explicit acceptance criterion and gets a regression test.
- **[Collapsing the slot changes desktop column flex behavior, possible over-stretch on very wide screens]** → Mitigated by keeping a (raised) `max-w` cap on core columns so they don't stretch into unreadable wide blocks; `flex-1` only reclaims the reclaimed Cancelled slot.
- **[Both DOM trees always render (CSS-only split) → slightly heavier DOM]** → Already the case today; accepted, since it keeps tests simple and avoids JS-breakpoint SSR cost.
- **[No real-device / visual regression testing in CI]** → Mitigated by class-based + testid-based regression assertions for reachability, density, and non-overlap; a11y is a separate track (`vitest.a11y.config.ts`) and is not claimed by this issue.
- **[Changing desktop column layout could shift the existing horizontal-scroll containment contract]** → Mitigated by keeping `overflow-x-auto min-w-0` on `kanban-board-row` and re-running `kanban-board-containment.test.tsx`; horizontal overflow remains the escape hatch below the reachability target width.

## Migration Plan

- **Scope:** Web-only, single PR. No feature flag, no backend, no data migration.
- **Deploy:** merge + standard web deploy. The Cancelled column default-collapses on first render for all users; users who had expanded it re-expand via the same affordance (state is local/per-session, so no cross-session expectation is broken).
- **Rollback:** revert the PR; board returns to the prior always-full-width-column layout. No data or URL-shape change to undo (collapse/tab state was never persisted).
- **Validation:** `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` (kanban-board suite), plus the existing containment/contrast suites.

## Open Questions

- **"Common desktop width" target.** Propose defining it as **1280px** (the `xl` breakpoint), the floor at which a laptop with the app sidebar should show all three core groups without horizontal scroll by default. Confirm against real sidebar widths before locking the column math.
- **Collapsed stub visual.** Compact count chip vs. a thin mini-column header — finalize in implementation against the theme tokens landed in issue 398 (no new colors).
