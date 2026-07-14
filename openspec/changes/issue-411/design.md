## Context

The Coder Session page (`SessionDetailShell.tsx`) uses a flex-column split layout: a fixed `SessionHeader` (shrink-0), an in-flow `SessionUsageSummary` strip, an optional `SessionErrorsEvidence` strip, a `flex-1` transcript scroll container (the only scrollable region), and a fixed `SessionFollowupComposer` (shrink-0). The height chain is `h-svh` -> `SidebarInset` (flex-col) -> content div (`flex-1 min-h-0`, `pb-[calc(3.5rem+env(safe-area-inset-bottom))]` on mobile) -> shell root -> main column -> children. The fixed `MobileBottomNav` (`md:hidden`, `z-40`, h-14) occupies the bottom 56px; its clearance is reserved once, globally, by the content div's bottom padding.

The problem: below `sm` (640px) the `SessionHeader` metadata cluster switches to `flex-col`, stacking every metadata item on its own line — status badge, stage chip, model, turn count, last-activity, probe-sent, changed-files, duration, session id, and the cancel button. At 375x667 this consumes ~200-280px of the ~563px available height (667 - 48px app header - 56px nav). Combined with the usage strip (~60-80px) and the composer (~80-90px), the `flex-1` transcript is squeezed to 41-57px. At 320x568 it reaches 0px. Inside that zero-height transcript, the sticky `StickySessionTitle` (top-0) and sticky recovery bar (top-9, which itself stacks `ContextHealthBar` above the Compact/Reset buttons below `sm`) are invisible, making the recovery controls unreachable.

The evidence-view layout from issue #402 established the region hierarchy but added no compact-mobile accommodations: no `useNarrowViewport`/`useIsMobile` branching, no `min-h` on the transcript, no density reduction. The `IssueDetailPage` precedent (`useNarrowViewport()` + `pb-[calc(8rem+...)]` + floating `MobileActionBar`) solves a similar problem for a different layout model (single scroll container + floating bar). The session page's split layout (fixed header + scrollable transcript + fixed composer) is the right model for desktop; it just needs density reduction at compact viewports, not a layout-model change.

Constraints: no session/workflow/status/transcript persistence changes; no new controls or recovery operations; no Activity/Files/Diff changes; desktop (md+) must be unchanged; existing `data-testid` anchors and recovery gating must be preserved; tests follow `design/testing.md` (MSW, no `vi.mock`, fake time, `data-testid`/`data-*` assertions; browser tests separate).

## Goals / Non-Goals

**Goals:**
- Preserve a readable, scrollable transcript region at compact viewports (below `md`), down to 320x568, that never collapses to zero height.
- Reduce nonessential session-summary density at compact viewports so the summary regions do not consume the transcript's space, while keeping session identity and current status visible.
- Keep the existing Compact/Reset recovery controls, follow-up composer, and cancel control reachable at compact viewports.
- Ensure fixed mobile navigation does not occlude transcript content or session controls.
- Preserve the desktop and larger-mobile (md+) layout, region order, density, and control behavior unchanged.

**Non-Goals:**
- Do not change the split layout model (fixed header + scrollable transcript + fixed composer) to a single-scroll or floating-bar model.
- Do not move the recovery bar outside the transcript scroll container (the #402 spec pins it there; moving it would break the region contract).
- Do not add a JS viewport hook (`useIsMobile`/`useNarrowViewport`) — all accommodations are pure CSS `md:` breakpoint classes.
- Do not build a PWA or mobile-first workflow.
- Do not change session lifecycle, status semantics, recovery gating, or transcript recording.
- Do not add new session controls or recovery operations.

## Decisions

### D1 - Hide nonessential SessionHeader metadata below `md` via `hidden md:inline`

The metadata cluster (`SessionDetailShell.tsx:605`) stacks all items vertically below `sm` (640px). At compact viewports this is the primary height consumer. Add `hidden md:inline` (or `hidden md:flex` for flex children) to each nonessential metadata item and its preceding separator dot, so they are hidden below `md` and restored at `md+`:

- **Hide below md:** model, turn count (and its `·` separator), last-activity, probe-sent, changed-files summary, duration, session id.
- **Keep visible at all viewports:** session name (`<h1>`), `StatusBadge`, `session-stage-chip`, cancel button.

The `StickySessionTitle` inside the transcript scroll container already renders session name + status badge + turn count, so the owner does not lose turn-count information — it is visible while scrolling the transcript. The stage chip stays because it provides workflow-stage context alongside the status badge; it is a single small chip (~one line).

Separators (`<span className="text-muted-foreground/40">·</span>`) must be hidden alongside their paired item to avoid dangling dots. Each separator+item pair gets the same `hidden md:inline` class.

**Alternatives considered:**
- *Collapse metadata into a single truncating row.* Rejected: truncation hides status-critical info unpredictably and is hard to assert in tests.
- *Add a disclosure/expand toggle for metadata.* Rejected: adds interaction complexity for a small-effort bug; the spec says "reduce density," not "add a toggle."
- *Use `useIsMobile()` to conditionally render metadata.* Rejected: unnecessary JS when pure CSS `md:` classes achieve the same result with simpler tests (no `matchMedia` mocking needed). Elements remain in the DOM (hidden via CSS), so existing `getByText` assertions in `SessionPageHeader.spec.tsx` remain green.

### D2 - Compact SessionUsageSummary below `md`

The usage strip (`SessionUsageSummary.tsx:34`) renders token in/out/total/cached/thought, cost, context window used/size/%, and a health indicator in a `flex-wrap` row. At compact widths this wraps to 2-3 lines (~60-80px). Reduce density below `md`:

- Hide secondary token breakdowns (input, output, cached, thought) with `hidden md:inline`; keep total tokens.
- Keep context % and cost (single-line essentials).
- Reduce vertical padding from `py-2` to `py-1 md:py-2`.

This brings the strip to ~24-30px at compact viewports. The detailed breakdowns remain visible at `md+`.

**Alternatives considered:**
- *Hide the entire usage summary below md.* Rejected: context health % is useful at compact viewports, especially during recovery. Keeping a compact single-line form preserves the signal.
- *Move usage into a collapsible.* Rejected: same reasoning as D1 — adds interaction complexity.

### D3 - Add `min-h` floor on the transcript scroll container below `md`

The transcript is `flex-1 overflow-y-auto` with no `min-height`. When siblings consume all available height, it collapses to zero. Add `min-h-[120px] md:min-h-0` to the transcript scroll container (`SessionDetailShell.tsx:357-361`).

At 320x568 (worst tested case), after D1+D2 density reduction:
- Available: 568 - 48 (app header) - 56 (nav) = 464px
- Reduced header (~104px) + compact usage (~28px) + errors (~40px, conditional) + composer (~85px, active) = ~257px
- Transcript gets ~207px — well above the 120px floor, so `min-h` is satisfied without overflow.

The 120px floor is a safety net: density reduction (D1+D2) is the primary fix, but `min-h` provides a structural guarantee that the transcript never reaches zero even in edge cases (long session name wrapping, errors + recovery bar both present). The value is chosen to be below the worst-case available height (~207px) to avoid pushing the composer below the viewport.

**Alternatives considered:**
- *Make the main column scrollable at compact viewports.* Rejected: creates nested scroll containers (main column + transcript), which is confusing and breaks the split-layout model where only the transcript scrolls.
- *Use `min-h` without density reduction.* Rejected: without D1+D2, `min-h` large enough to be useful (~150px) would overflow the viewport at 320x568 and push the composer below the nav — the exact problem being fixed.

### D4 - Compact the recovery bar at compact viewports

The sticky recovery bar (`SessionDetailShell.tsx:367-375`) sits inside the transcript scroll container as `sticky top-9`. Its inner content (lines 176-203) uses `flex flex-col gap-2 sm:flex-row` — below `sm` the `ContextHealthBar` stacks above the Compact/Reset buttons, adding ~80px of sticky height inside the already-starved transcript. Reduce this at compact viewports:

- Change the inner layout from `flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between` to `flex flex-row gap-2 items-start justify-between` (always horizontal). The `ContextHealthBar` (`flex-1 min-w-0`) shrinks; the buttons (`shrink-0`) stay visible. At 320px width (288px after px-4 padding), the health bar gets ~140px and the buttons ~150px — tight but workable.
- Reduce the recovery bar wrapper padding from `px-4 py-3` to `px-4 py-2 md:py-3`.

This reduces the recovery bar's sticky height from ~80px to ~50px, freeing ~30px of transcript content area.

**Alternatives considered:**
- *Keep `flex-col` below `sm` and only reduce padding.* Rejected: the stacked layout is the main height consumer (~80px vs ~50px horizontal); padding reduction alone saves only ~8px.
- *Move the recovery bar outside the transcript at compact viewports.* Rejected: the #402 spec test (`CoderSessionEvidence.spec.tsx:736-756`) asserts the recovery bar is inside the transcript scroll container, and this change's spec requires the region contract to remain intact at all viewports. Moving it would break both.

### D5 - Pure CSS `md:` breakpoint classes, no JS viewport hook

All accommodations (D1-D4) use Tailwind `md:` prefixed classes. No `useIsMobile()` or `useNarrowViewport()` hook is introduced. Rationale:

- The `MobileBottomNav` is `md:hidden` (visible below 768px), so `md` is the natural breakpoint for "compact viewport" in this context.
- Pure CSS classes don't require `matchMedia` mocking in tests. Elements remain in the DOM (hidden via CSS), so existing `getByText`/`getByTestId` assertions in `SessionPageHeader.spec.tsx` and `CoderSessionEvidence.spec.tsx` remain green without modification.
- The existing `SessionHeader` already uses `sm:` breakpoint classes (`sm:flex-row`, `sm:items-center`), so adding `md:` classes is consistent with the established pattern.
- Desktop (md+) is automatically unaffected: all compact classes are scoped below `md`, and `md:min-h-0` / `md:py-3` / `md:inline` restore the desktop behavior.

**Alternatives considered:**
- *Use `useIsMobile()` with conditional rendering/classNames.* Rejected: adds `matchMedia` dependency and test complexity for no behavioral gain. The `IssueDetailPage` uses this pattern because it conditionally renders a `MobileActionBar` component and reserves variable bottom padding — structural changes that CSS alone can't express. This change's accommodations are purely presentational (show/hide elements, change padding), so CSS suffices.

### D6 - Tests: spec tests for structural contract, browser tests for pixel verification

Per `design/testing.md`, two test tracks:

- **Spec tests** (`packages/web/tests/`, `.spec.tsx`): assert the structural contract that guarantees compact-viewport behavior. These use MSW (existing handlers) and render the page in jsdom (no layout engine). Assertions verify:
  - Nonessential metadata items carry `hidden md:inline` class (present in DOM but hidden below md).
  - Session name, status badge, and stage chip do NOT carry `hidden` (always visible).
  - Transcript scroll container carries `min-h` class with `md:min-h-0`.
  - Recovery bar inner layout uses `flex-row` (always horizontal).
  - All existing `data-testid` anchors are preserved (session-header, session-transcript-scroll-container, session-sticky-title, session-recovery-bar, session-followup-composer, session-sibling-sidebar).
  - Desktop layout: existing `CoderSessionEvidence.spec.tsx` and `SessionPageHeader.spec.tsx` assertions remain green (elements still in DOM, classes still present).

- **Browser tests** (`packages/web/tests/browser/`, separate `npm run test:browser`): assert actual pixel-level behavior at 375x667 and 320x568 — transcript height > 0, transcript is scrollable, recovery controls are reachable (not covered by nav), fixed nav does not overlap transcript or controls. These do not enter the default `npm test`.

**Existing test compatibility:** jsdom does not apply CSS, so `hidden md:inline` elements remain in the DOM with their text content. All existing `getByText` assertions (model, turn count, duration, changed-files) in `SessionPageHeader.spec.tsx` remain green. The metadata cluster className assertion (`flex-col`, `sm:flex-row`) is on the cluster container, not individual items, so it is unaffected by D1. The recovery-bar-inside-transcript assertion in `CoderSessionEvidence.spec.tsx` is unaffected by D4 (inner layout changes, not containment).

## Risks / Trade-offs

- **[min-h could cause overflow on extreme edge cases]** -> Mitigated by D1+D2 density reduction, which frees ~120-180px at compact viewports. The 120px floor is calibrated below the worst-case available height (~207px at 320x568 with errors + active composer). If an unexpected combination arises, the floor prevents zero-height (the more dangerous failure) at the cost of potential minor overflow, which is the lesser evil.
- **[Hiding metadata reduces information density on mobile]** -> Mitigated by the `StickySessionTitle` inside the transcript, which shows session name + status + turn count while scrolling. The owner sees identity and status at all times; detailed metadata (model, duration, session id) is available on desktop or by rotating to landscape (md+ width). The spec explicitly lists these as "nonessential" at compact viewports.
- **[Recovery bar horizontal layout at 320px width]** -> The `ContextHealthBar` gets ~140px in a horizontal layout at 320px. Its label (`formatUsageLabel`) is a single line of mono text (`12K / 32K tokens (37%)`) that truncates gracefully. The bar itself is `w-full` of its `flex-1` container. Browser tests at 320x568 verify the layout doesn't break.
- **[D4 changes recovery bar layout from sm: to always flex-row]** -> This changes the visual layout at 640-767px (sm to md) from stacked to horizontal. This is intentional: the stacked layout was the height problem, and horizontal is more compact. The change is purely visual; recovery gating and button behavior are unchanged.
- **[No JS viewport hook means no runtime viewport-aware logic]** -> All accommodations are static CSS. If future needs require runtime viewport awareness (e.g., conditional rendering, dynamic padding), a hook can be added then. For this change, CSS-only is simpler and sufficient.

## Migration Plan

Frontend-only change; no server, runner, or CLI changes. No new dependencies. Deploy = merge to `master`; rollback = revert the merge commit.

Implementation order (each step lands green before the next):

1. **D1 - Header metadata density.** Add `hidden md:inline` to nonessential metadata items and their separators in `SessionDetailShell.tsx` `SessionHeader`. Run `npm run typecheck -w packages/web` + `npm run test:run -w packages/web` — existing tests must remain green.
2. **D2 - Usage summary compact.** Add `hidden md:inline` to secondary token breakdowns and `py-1 md:py-2` padding in `SessionUsageSummary.tsx`. Run typecheck + tests.
3. **D3 - Transcript min-h floor.** Add `min-h-[120px] md:min-h-0` to the transcript scroll container in `SessionDetailShell.tsx`. Run typecheck + tests.
4. **D4 - Recovery bar compact.** Change inner layout to always `flex-row` and reduce wrapper padding to `py-2 md:py-3` in `SessionDetailShell.tsx`. Run typecheck + tests.
5. **Spec tests.** Add `packages/web/tests/CoderSessionCompactViewport.spec.tsx` asserting the structural contract (class presence, anchor preservation, desktop layout unchanged). Run `npm run test:run -w packages/web`.
6. **Browser tests.** Add `packages/web/tests/browser/` tests for pixel-level verification at 375x667 and 320x568. Run `npm run test:browser` (separate, not in default `npm test`).

Rollback strategy: revert the merge commit; no data migration, no schema, no server state. The session page returns to its pre-issue form.

## Open Questions

- **Recovery bar at very narrow widths (below 320px).** The horizontal recovery bar layout (D4) gives the `ContextHealthBar` ~140px at 320px. Below 320px (not a tested viewport but possible on some devices), the bar may be too cramped. Deferred: the spec tests 375x667 and 320x568; narrower devices are out of scope. If needed, a future refinement could conditionally stack below a lower breakpoint (e.g., `flex-col max-[360px]:flex-col`).
- **Transcript min-h value.** 120px is calibrated for the tested viewports. If future content changes (e.g., a taller sticky title or additional sticky elements inside the transcript) reduce the effective content area, the floor may need adjustment. The value is a single class constant, easy to tune.
