## Context

The current homepage already has the data needed to support a decision-first entry: `issues`, `agentStatus`, `approvalState`, `mergeState`, `blockedReason`, and stage/status fields are all available in the existing Web UI types. The regression is primarily in presentation and composition: `KanbanBoard.tsx` currently mixes board query state, responsive layout, and filter rendering in one component, slices labels to the first eight entries, and uses a desktop container with `md:flex flex-col`, which collapses the board into a vertical stack.

This change should stay frontend-only. Existing `/api/issues`, `/api/agent/status`, and `/api/labels` responses already supply the signals needed for attention surfacing and full label filtering. The design therefore focuses on reorganizing homepage composition without introducing a new backend attention model or a new status taxonomy.

## Goals / Non-Goals

**Goals:**

- Make the homepage answer "what needs my attention now" before asking the user to scan the board.
- Restore a usable desktop Kanban layout with horizontally visible stage columns at `md+` widths.
- Preserve the #198 filter/sort behavior and URL-backed board query state.
- Make mobile controls compact enough that issue content remains visible in the first screen.
- Make all labels reachable from the homepage filter UI.
- Add regression tests for the desktop layout and full-label filtering behavior.

**Non-Goals:**

- No issue detail page redesign.
- No backend stalled/liveness detection beyond current issue and agent status data.
- No removal of filtering/sorting introduced in #198.
- No new persistent status model or broad user-visible taxonomy beyond a few homepage decision labels.

## Decisions

### D1: Keep attention derivation in a homepage-specific selector, not in `IssueCard`

The homepage needs a compact cross-column summary of actionable items, but the existing `IssueCard` is optimized for per-column rendering and inline actions. The design should add a small pure derivation layer, for example `deriveAttentionItems(issues, agentStatus)`, near `KanbanBoard` or in a focused helper module. That selector will translate current issue data into a flat list of attention items with a small, explicit set of labels such as `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, and `Not merged`, plus optional secondary detail text.

This keeps the attention rules in one place and avoids leaking homepage-specific decision wording into every card renderer or stage column.

**Alternatives considered:** add ad hoc filtering inline inside `KanbanBoard` JSX; derive the summary from `IssueCard` badge logic; add a backend `attentionItems` API. Inline JSX logic would make an already crowded component harder to maintain, reusing `IssueCard` badge logic would couple summary behavior to card visuals, and a backend API would add unnecessary surface area for a frontend-only prioritization problem.

### D2: Treat the homepage as three stacked surfaces: attention, controls, board

The page should be explicitly organized into three layers:

1. `Needs attention` summary
2. compact filter/sort controls
3. responsive Kanban board

This pulls complexity downward by giving each surface one job instead of continuing the current board-first composition where filters dominate the top of the page. The attention surface is a summary list, not a replacement for the board. The controls stay URL-backed and continue to drive the board state. The board remains the main browsing surface once the user has a first decision.

**Alternatives considered:** keep a single monolithic board surface and prepend one extra banner; move attention into a separate dashboard page. A one-off banner would not fix the page hierarchy, and a separate dashboard page would make the main homepage less useful rather than more useful.

### D3: Fix desktop layout by making the board row-oriented and letting column width rules work as designed

The immediate regression comes from the desktop board container using a vertical flex direction while `StageColumn` already assumes fixed-width columns (`min-w-[280px] max-w-[320px]`). The desktop container should become a horizontal scrolling row at `md+`, with columns laid out side by side and aligned to the top. Done-column affordances such as `Show closed` remain within that row model.

The design should not invent a more complex CSS grid unless the current column sizing proves insufficient. A row-based flex container matches the current column component contract and is the smallest correct repair.

**Alternatives considered:** rework the board into CSS grid; remove width constraints from `StageColumn`; create separate desktop/mobile column components. Grid would require more layout decisions than needed for this fix, removing width constraints would make card readability less stable, and separate components would duplicate stage rendering behavior.

### D4: Replace the first-eight-label slice with a searchable or expandable full-label picker

The current `allLabels.slice(0, 8)` is the direct reason important labels are unreachable. The filter design should continue to show a compact default surface but allow access to the full label set. The simplest implementation path is a compact label control with either:

- a searchable disclosure/popover listing all labels, or
- an inline expandable section with search and selected-label chips.

The key design constraint is that all labels must remain selectable without turning the top bar into an unbounded wall of chips. The control should keep selected labels visible even when the full list is collapsed.

**Alternatives considered:** render every label chip inline; keep eight visible and add a `more` toggle without search; defer to URL editing for advanced labels. Rendering all chips inline harms mobile and dense desktop layouts, a blind `more` toggle becomes unwieldy for dozens of labels, and URL editing is not acceptable as the primary UI.

### D5: Compact mobile controls by collapsing secondary controls, not by creating a separate feature model

Mobile already has a different board presentation, but the top-of-page controls are too tall. The design should preserve the same query model while changing presentation:

- search remains directly visible
- selected filters remain visible as chips or summary text
- priority, labels, and sort move into a compact disclosure/sheet/accordion area

This keeps feature parity with desktop while reducing first-screen competition. The board content should appear without requiring the user to scroll past a full control matrix.

**Alternatives considered:** remove labels or sort on mobile; create a separate mobile-only query state; hide filters entirely behind a secondary route. Those options either reduce capability or create inconsistent behavior with the URL-backed board model.

### D6: De-emphasize Done through presentation, not data removal

Done/history is still useful, but it should stop competing visually with active work. The design should keep the Done column present while muting its visual weight: lower-contrast column chrome, maintained collapse behavior, and keeping closed-history affordances secondary. This preserves browsing and archive actions without letting done work dominate the homepage.

**Alternatives considered:** hide Done by default; move Done to a separate page; remove archive controls from the homepage. Hiding or relocating Done would reduce continuity with the Kanban model and risks making completed work harder to verify.

### D7: Add regression tests at the component contract level rather than pixel-perfect layout snapshots

The regression risk is behavioral: columns can silently become vertically stacked again, and labels can silently become unreachable again. Tests should therefore assert stable DOM contracts such as:

- the desktop board container uses horizontal layout classes or test ids consistent with side-by-side columns
- all stages render inside the desktop row container
- labels beyond the first eight are discoverable/selectable through the new control
- filtered counts still update correctly after selecting a late-alphabet or otherwise hidden label

This keeps the tests resilient to harmless style changes while still failing on the regression that matters.

**Alternatives considered:** rely only on manual browser verification; use screenshot/golden tests. Manual verification would not protect against future regressions, and screenshot tests are heavier than needed for the class of failures described here.

## Risks / Trade-offs

- [Attention rules become another hidden status system] → Keep the summary labels as a thin translation layer over existing stage/status/approval/merge fields and document the mapping in code near the selector.
- [Homepage logic becomes too concentrated in `KanbanBoard`] → Extract attention derivation and, if needed, the compact label control into small focused helpers/components instead of adding more inline JSX branches.
- [Mobile compaction makes controls harder to discover] → Keep search and active filter summary always visible, and label the collapsed control clearly with counts or selected state.
- [Layout fix depends too much on Tailwind class names in tests] → Use stable test ids or semantic wrappers for the desktop board row and label picker where necessary.
- [Done de-emphasis reduces visibility of archive actions] → Keep archive controls in the Done column and preserve the existing collapse/archive behavior, changing emphasis rather than capability.

## Migration Plan

1. Add or update the homepage spec delta for `web-ui` to define the decision-first attention summary, horizontal desktop layout, compact mobile controls, and full-label reachability.
2. Refactor `KanbanBoard` into clearer surfaces: attention summary, controls, mobile board, desktop board.
3. Introduce a pure attention derivation helper and wire it from existing `issues` and `agentStatus` data.
4. Replace the current label slice behavior with a compact full-label picker that keeps selected labels visible.
5. Fix the desktop board container layout and apply Done-column de-emphasis styling.
6. Extend component tests to cover the desktop row contract and hidden-label selection behavior.
7. Verify manually in desktop and mobile viewports against the live server.

Rollback is straightforward because this is a frontend-only change: revert the homepage component and related tests, restoring the prior board layout and control surface if unforeseen usability or rendering regressions appear.

## Open Questions

- Should the attention summary link each item directly to the issue detail page only, or also offer inline recovery actions such as Resume for interrupted issues? The safer initial design is link-first, with existing inline actions remaining on cards.
- Should full label access use a popover/command-style picker or an inline expandable panel? Both satisfy the requirements; the final choice should follow the repo's existing lightweight interaction patterns to avoid introducing unnecessary UI machinery.
