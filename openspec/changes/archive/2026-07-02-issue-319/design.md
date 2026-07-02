## Context

The issue kanban board (`packages/web/src/widgets/kanban-board/`) is the primary triage surface, but today it is a "field-by-field literal translation" UI that fails the first-screen scan. Five concrete defects, all in the Web visual/interaction layer:

1. **Desktop horizontal overflow escapes the board.** The desktop columns row already has `overflow-x-auto` (`KanbanBoard.tsx:626`) and each column has `min-w-[280px]` (`StageColumn.tsx:85`), so four columns demand ~1.2–2.0px of intrinsic width. But the containment chain above the row is broken: `SidebarInset`'s `<main>` (`shared/ui/components/sidebar.tsx:310`), the `App.tsx:58` content wrapper, and the `KanbanBoard.tsx:547` root all carry **no `min-w-0` / overflow**, so each flex item's default `min-width: auto` lets the board's intrinsic width propagate up, widening `<main>` past the viewport and producing *page-level* horizontal scroll — which drags the `sidebar-gap` spacer (`sidebar.tsx:222`) with it. (The visible sidebar panel is `position: fixed` so it does not move, but the spacer does, and the whole body scrolls.)
2. **Card top row is too dense.** `IssueCard.tsx:269-314` renders up to seven elements in one row: `#number`, `PriorityChip`, a full **workflow-profile chip** (`bg-gray-100 text-gray-700`, always visible), `DraftPill`, a **stage pill** (`WorkflowStagePill`), a standalone **progress indicator** (`completed/total`), and a **status pill** (`StatusPill`) — stage and status stacking as two independent pills.
3. **Per-card text is below WCAG AA.** Issue number and timestamp use `text-muted-foreground/70` (a theme token at 70% opacity); several status/priority pill pairs use hardcoded Tailwind palette classes whose contrast has never been verified.
4. **Sort controls are duplicated.** A global `SortToggle` lives in the `FilterBar` (`KanbanBoard.tsx:326`) **and** a per-column-header sort button group lives in every `StageColumn` (`StageColumn.tsx:108-126`). Both mutate the *same* `BoardQueryState.sort`, so clicking one column's button silently re-sorts all columns.
5. **The color strip is label-driven and collapses to gray.** `IssueCard.tsx:258` sets `borderLeftColor: getStripColor(issue.labels)`, which matches the first of `['bug','feature','enhancement','tech-debt','performance']` in `labels` (`label-colors.ts:53-65`). Most issues carry none of those type labels, so the strip falls through to `'#6b7280'` (gray) — the categorization dimension does no work.

**Stakeholders / constraints:** Pure Web change; no schema migration, no API contract change, no backend change. Styling is **Tailwind v4** utility classes via shadcn `cn()`; the single responsive breakpoint in play is `md` = 768px (`use-mobile.ts` `MOBILE_BREAKPOINT = 768`). Color sourcing is currently split across three places (`shared/lib/label-colors.ts`, `model/stage-colors.ts`, and inline Tailwind palette classes in `IssueCard.tsx`) — there is no unified color-tokens module, and neutrals are the only theme-variable-aware colors. Risk is **medium**: one component tree, but it touches five interaction surfaces (card render, filter bar, column header, layout container, shared color lib) and requires full desktop+mobile regression.

## Goals / Non-Goals

**Goals:**
- Confine board horizontal scroll to the board region at ≥768px; eliminate page-level horizontal scroll; left nav stays fixed.
- Converge the card top row to `#number` + priority + at most one dominant status; workflow profile becomes hover-only (retain `title`); stage folds into the status pill instead of stacking; progress renders as part of the stage label.
- Bring issue number, timestamp, status pill, and priority pill background/text combinations to ≥4.5:1 contrast (WCAG AA).
- Keep exactly one global sort control (top filter bar); remove the per-column sort group.
- Drive the left color strip from priority (P0 red / P1 orange / P2 yellow / P3 green / P4 gray), deterministic and distinct.

**Non-Goals:**
- No change to workflow-profile display on the issue detail page or anywhere else.
- No new sort dimensions (e.g. by label); only redundant entries are removed.
- No redesign of the label color system (`domain=agent` token styling untouched).
- No "Archive all done" confirmation, no keyboard shortcuts, no presets, no onboarding, no legend.
- No change to data fetching, grouping, or filtering logic — visual/interaction layer only.
- No dark-mode color-token refactor beyond what AA contrast on the targeted text/pills requires.

## Decisions

### Decision 1 — Fix overflow by closing the `min-w-0` containment chain (not by hiding overflow)

Add `min-w-0` to the three flex ancestors above the board row: `SidebarInset` `<main>` (`sidebar.tsx:310`), the `App.tsx:58` content wrapper, and the `KanbanBoard.tsx:547` root. The board row's existing `overflow-x-auto` (`KanbanBoard.tsx:626`) then becomes the *effective* scroll owner because its intrinsic min-content can no longer inflate its ancestors.

**Rationale:** This is the standard flex-overflow fix and the codebase already uses it as a proven precedent — `WorkflowView.tsx:292,335,342` and `WorkflowSessionsPanel.tsx` (with a regression test at `WorkflowSessionsPanel.test.tsx:355` asserting `min-w-0` on the row/header). The board row already does its part; only the chain above is broken.

**Alternatives considered:**
- *`overflow-x: hidden` on `html`/`body`.* Rejected: masks legitimate content overflow elsewhere, hides scrollbars silently, and treats the symptom rather than the broken flex chain.
- *Shrinking column `min-w-[280px]`.* Rejected: columns become unreadable; the spec requires scroll containment, not narrower columns.
- *Making the board row `position: relative` with a fixed-height scroll viewport.* Rejected: heavier change, fights the existing `h-[calc(100vh-3rem)]` layout, and `min-w-0` on ancestors is sufficient.

The `md`/768px switch is untouched: below `md` the mobile branch (`KanbanBoard.tsx:558`) already confines horizontal scroll to its tab strip (`:559`).

### Decision 2 — Card top row: workflow profile → hover-only, stage folds into status pill

- **Workflow profile:** Remove the always-visible profile chip (`IssueCard.tsx:276-283`). Move its value into a `title` on the issue-number span (or the card root) so the value remains inspectable on hover without occupying default density. Keep the `data-testid`/`data-workflow-profile` hooks on a hidden/aria node so existing tests that query them keep working (tests will be updated to assert hover availability rather than visible text).
- **Stage folds into status pill:** When a `StatusPill` would render (`indicator` present), pass the stage label into `StatusPill` and render it as a prefix/suffix inside the same pill (e.g. `Running · Build`), instead of rendering a separate `WorkflowStagePill`. When **no** status pill renders (e.g. a plain Backlog issue), the stage pill may still render on its own — the spec only forbids *stacking* the two. The fold is implemented inside `IssueCard` (render-time composition), not by merging the two data sources.
- **Progress as stage label:** `WorkflowStageProgressIndicator`'s `completed/total` renders inline as part of the stage label text (`Build 2/5`) rather than as a standalone unit.

**Rationale:** Keeps all information available (hover + folded label) while collapsing the default row to ≤3 visible elements. Folding at render time avoids touching the `StatusIndicator` derivation logic (`getStatusIndicator`), keeping the change in the visual layer as the Non-Goals require.

**Alternatives considered:**
- *Drop the stage pill entirely.* Rejected: loses at-a-glance stage for in-progress issues; folding preserves it.
- *Merge stage into the `StatusIndicator` enum.* Rejected: crosses into data-derivation logic, violating the "visual layer only" Non-Goal.
- *Tooltip component instead of native `title`.* Rejected: heavier, adds a11y surface area; native `title` is the spec-cited minimal affordance and already in use.

### Decision 3 — Contrast: raise text to full-opacity token, verify/adjust pill pairs to AA

- **Auxiliary text:** Change `text-muted-foreground/70` → `text-muted-foreground` (full opacity) for the issue number (`IssueCard.tsx:270`) and timestamp (`:355`). The theme's `--muted-foreground` is designed to be the readable secondary color; the `/70` was the below-AA culprit.
- **Pills:** Audit each `StatusPill` variant (`IssueCard.tsx:64-131`) and the `PRIORITY_COLORS` pairs (`label-colors.ts:90-100`) against their backgrounds. Adjust any pair below 4.5:1 by darkening the text or the background (preference: darken text to keep the soft-tint aesthetic). Hardcode hexes stay (no token refactor — out of scope), but each must be verifiably ≥4.5:1.
- **Verification:** Compute ratios explicitly in the design record and add a unit test that asserts the contrast ratio for each documented pill pair (pure function over the hex map), so the AA guarantee is checked in CI rather than by eye.

**Rationale:** Lowest-blast-radius path: the theme already provides a readable token; only the `/70` opacity and a few unverified hex pairs need touching. A ratio-asserting test makes the AA requirement executable instead of aspirational.

**Alternatives considered:**
- *Move all pill colors to CSS variables / a tokens module.* Rejected: scope creep into a dark-mode token refactor the Non-Goals exclude.
- *Manual eyeball verification only.* Rejected: regresses silently; the spec calls AA a "hard requirement."

### Decision 4 — Single sort control: delete the per-column sort group

Remove the sort block at `StageColumn.tsx:108-126` and drop the `sort` / `onSortChange` props from `StageColumn`'s interface (`:15-27`). The global `SortToggle` in the `FilterBar` (`KanbanBoard.tsx:326` desktop, `:383` mobile) remains the single source of truth — it already drives the one shared `BoardQueryState.sort`.

**Rationale:** Both controls already mutate the same state, so the column group is pure redundancy. Removing props also simplifies `StageColumn`'s contract.

**Alternatives considered:**
- *Keep column sort but make it reflect/echo the global state (read-only highlight).* Rejected: still five controls, still confusing; the spec wants the entry removed.
- *Keep column sort and remove the global one.* Rejected: the global one is visible without scrolling and serves mobile; column headers are not present on mobile.

### Decision 5 — Priority-driven color strip via a new `getPriorityStripColor`

Add `getPriorityStripColor(priority)` to `shared/lib/label-colors.ts` with a deterministic, **distinct-per-priority** map: P0 red / P1 orange / P2 yellow / P3 green / P4 gray (distinct hues — *not* reusing `PRIORITY_COLORS`, where `p0` and `p1` are currently both red). Change `IssueCard.tsx:258` from `getStripColor(issue.labels)` to `getPriorityStripColor(issue.priority)`, falling back to the gray for `null`/unknown priority.

`getStripColor` (label-based) is **kept** — it is a generic helper and may be used elsewhere; only the *card's* call site changes. This keeps the blast radius to one call site.

**Rationale:** Priority is the user's primary scan dimension, always present (normalized to `p2` via `board-query.ts:53-57`), and its color map is deterministic — fixing defect 5 at its root (label-driven) rather than its symptom (gray).

**Alternatives considered:**
- *Reuse `PRIORITY_COLORS` for the strip.* Rejected: `p0`/`p1` collide (both red), violating the spec's "distinct across priorities."
- *Make `getStripColor` priority-aware.* Rejected: changes a shared helper's contract and risks other call sites; a dedicated function is clearer.
- *Derive strip color from status.* Rejected: status is already conveyed by the column and the status pill; the strip should add an *orthogonal* dimension (priority).

## Risks / Trade-offs

- **[Power users lose always-visible stage/profile]** → Mitigation: stage label is folded into the status pill (still at-a-glance); workflow profile remains available via `title` hover and on the detail page. Net information preserved, default density reduced.
- **[`min-w-0` on `<main>` affects every page, not just the board]** → Mitigation: `min-w-0` only changes behavior when a flex child's intrinsic width would otherwise overflow — pages whose content already fits are unaffected. Verify with a full desktop+mobile regression pass (Decision 1's precedent in `WorkflowSessionsPanel` is already shipping).
- **[Contrast edits may shift visual emphasis / break snapshot tests]** → Mitigation: prefer darkening text over backgrounds to keep the soft-tint look; update any affected tests explicitly (no new/old test coexistence per testing principles).
- **[Hover-only workflow profile is unreachable on touch/mobile]** → Mitigation: the workflow-profile chip was already secondary info; on mobile the value is still in the DOM (`title`) and fully visible on the detail page. Acceptable per Non-Goals.
- **[Priority strip loses the bug/feature type signal]** → Mitigation: type labels still render as chips in the card body; the strip now encodes one consistent, always-present dimension instead of a mostly-empty one.
- **[Stage-fold rendering format is a judgment call]** → Mitigation: pick one format (e.g. `Running · Build 2/5`) and assert it in a unit test so it is pinned.

## Migration Plan

Pure frontend, no schema/API/backend change → no data migration, no feature flag, no staged rollout.

1. Land the five decisions in one PR (they are interdependent — e.g. the top-row change and the strip change both touch `IssueCard.tsx`).
2. Update/add tests in the same PR:
   - `IssueCard.test.tsx`: assert default top row has no visible profile text; assert stage folded into status pill when both apply; assert strip color follows priority.
   - New `label-colors` contrast test: assert ≥4.5:1 for each `PRIORITY_COLORS` and `StatusPill` pair, and assert `getPriorityStripColor` returns distinct hues for p0..p4.
   - `kanban-board-query.test.tsx`: assert no per-column sort buttons render; assert the single global `SortToggle` drives sort.
   - Add a `min-w-0` assertion test for the board→shell chain (mirror `WorkflowSessionsPanel.test.tsx:355`).
3. Manual regression on desktop (1440px and a narrower ≥768px width) and mobile (<768px): filtering, sorting, Done collapse, Show/Hide cancelled, Archive / Archive-all-done, Needs-Attention banner.
4. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.

**Rollback:** Revert the PR. No persistent state, no migration to undo.

## Open Questions

- **Exact hex values for the P0–P4 strip map.** Spec says red/orange/yellow/green/gray. Should we reuse the existing `TYPE_STRIP_COLORS` hexes (bug-red, performance-yellow, etc.) for visual consistency, or pick a dedicated priority palette? *Lean: dedicated palette tuned for strip-on-background contrast, documented in the contrast test.*
- **Stage-fold label format.** `Running · Build`, `Build: Running`, or `Running (Build 2/5)`? *Lean: `Running · Build 2/5` — status-first (the dominant signal), stage/progress as qualifier.*
- **When no status pill renders, should the stage pill still appear independently?** Spec only forbids stacking; an in-progress issue with no health/approval/blocker indicator would otherwise lose its stage. *Lean: yes, render stage pill standalone when no status pill — preserves information.*
- **Should `PRIORITY_COLORS` (the chip) also be deduplicated so p0≠p1, for consistency with the new strip map?** Currently both red. *Lean: yes, align the chip map with the strip map in the same PR to avoid two divergent priority color systems.*
