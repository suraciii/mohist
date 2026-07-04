## Context

The issue detail page serves two opposite high-frequency mobile needs — *glance at status* and *act in a hurry* — and today both fail on phones: the primary action is buried under a long scroll, and confirming a destructive stop covers the very status the user needs to see.

Two prerequisite issues already landed the foundation this change builds on, so this is a **narrow-viewport-only relocation** of existing, converged building blocks:

- **#340** converged the single primary-action model: `deriveRuntimeDecision` now emits one `decision.primary` plus the shared `decision.stopRecoverable` / `decision.approvalStage` state and the existing mutations in `useIssueDetailMutations`. This change consumes that output verbatim — **no new data source, no second decision surface, no re-convergence**.
- **#341** delivered the read-only sticky `StatusHeadline` (`packages/web/src/pages/issue-detail/ui/StatusHeadline.tsx:107`, already `sticky top-0 z-20`, `data-sticky="true"`, strictly read-only) and the reading-flow / reference-rail layering with the `lg` grid split.

### Current state of the relevant code

- **Layout** (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`): three tiers. Tier 1 `status-header-tier` contains `StatusHeadline` + the back button + the issue header + **`RuntimeDecisionSurface`** (`IssueDetailPage.tsx:265-279`, inside the header tier — not in the grid). Tiers 2 & 3 are a `grid grid-cols-1 lg:grid-cols-3 gap-8` (`IssueDetailPage.tsx:282`): reading flow `lg:col-span-2`, reference rail `lg:col-span-1`. The scroll container is `issue-detail-page-container` (`IssueDetailPage.tsx:157`, `flex-1 overflow-y-auto`) — sticky elements bind to it.
- **`RuntimeDecisionSurface`** (`packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`): renders `decision.primary` first then secondary actions; uses **inline** confirmation — a two-click stop (`stopConfirming` state, `data-testid="runtime-stop-confirmation-copy"` at lines 341-350, copy chosen by `decision.stopRecoverable`) and an inline send-back form (`data-testid="runtime-send-back-form"`, lines 295-339). No dialog today.
- **`useNarrowViewport`** (`packages/web/src/shared/lib/use-narrow-viewport.ts`): returns a plain boolean for `(max-width: 1023.98px)` — i.e. narrow === below Tailwind `lg`/1024px. SSR-safe.
- **`MobileBottomNav`** (`packages/web/src/widgets/app-shell/ui/MobileBottomNav.tsx:86`): `fixed bottom-0 inset-x-0 z-40`, `h-14` (56px) + `env(safe-area-inset-bottom)`, visibility gated by **`md:hidden` → visible below `md`/768px**. This is a *different* breakpoint from `useNarrowViewport`.
- **App shell** (`packages/web/src/app/App.tsx:59`): the route wrapper already carries `pb-[calc(3.5rem+env(safe-area-inset-bottom))] md:pb-0` — global nav space is reserved *outside* the page's scroll container.
- **`CollapsibleRailCard`** (`packages/web/src/pages/issue-detail/ui/cards/CollapsibleRailCard.tsx`): the existing accordion primitive; the rail already forces `forceCollapsed={isNarrowViewport}` below `lg` and defaults drift/convergence to collapsed.
- **Shared UI primitives available**: `dialog.tsx` and `alert-dialog.tsx` only — **no `sheet`/`drawer`**. Tailwind v4 (CSS-first), default breakpoints (`sm` 640 / `md` 768 / `lg` 1024 / …), `tw-animate-css` imported, touch-target convention `min-h-[44px]`.

### Constraint summary
- **Viewport band math matters**: narrow is `<1024px`, but the global bottom nav is only present below `md`/768px. The 768–1024px band is "narrow page, no global nav" — any nav-aware offset must be conditional on nav *presence*, not on narrow-ness.
- The scroll container is `issue-detail-page-container`; padding to defeat the fixed bar must live *inside* it (the App-level nav padding lives outside, on the wrapper).
- `z-20` = in-page sticky headline; `z-40` = global fixed chrome (nav/FAB). New elements must choose deliberately within/above this range.

## Goals / Non-Goals

**Goals**
- Narrow viewport: surface the single `decision.primary` in a thumb-reachable bottom floating bar that sits above `MobileBottomNav` (when present) and renders only when a primary action exists.
- Narrow viewport: confirm destructive actions (stop, send-back) via a bottom-sliding drawer that **keeps the top sticky headline visible** and reuses the exact consequence copy / mutations.
- Narrow viewport: make the status-header tier strictly read-only by stripping `RuntimeDecisionSurface` from it.
- Reserve bottom padding so the last reading-flow item is never obscured by the bar (nav space is already handled by the shell).
- Pin the reference-rail narrow collapse invariant and its ordering after the reading flow (mostly already delivered by #341).
- `lg`+ fully restores the desktop layout — no mobile-only element renders.

**Non-Goals**
- Re-converging the action surface or redefining the status-headline content (#340 / #341).
- Redesigning the global `MobileBottomNav` or its tab crowding.
- Any server / runner / CLI model, DTO, query, or projection change.
- Global command palette / keyboard shortcuts.
- Surfacing *secondary* runtime actions (retry / resume / rerun) on narrow viewports (see Open Questions).

## Decisions

### D1 — Page-level conditional split, not an internal branch in `RuntimeDecisionSurface`
On narrow viewports the page **does not render `RuntimeDecisionSurface` in the header tier at all**; it renders the new `MobileActionBar` instead. Desktop/tablet (`lg`+) keep `RuntimeDecisionSurface` in the header tier, inline confirmation and all, byte-for-byte.

- *Why*: the spec mandates the header tier contain *no* action surface on narrow and that the desktop invariant (#341) is preserved. A clean swap at the `IssueDetailPage` level keeps each surface simple and avoids a forest of `if (narrow)` branches inside `RuntimeDecisionSurface`. It also makes the "no competing primary control" scenario trivially true (the desktop surface isn't in the DOM on narrow).
- *Alternatives considered*: (a) make `RuntimeDecisionSurface` viewport-aware internally — rejected, it would entangle two confirmation UX models (inline vs drawer) in one component and regress the many desktop paths listed in the proposal's risk note; (b) keep both mounted and hide one with CSS — rejected, it violates "SHALL NOT render" and leaves two primary controls in the DOM.

### D2 — Extract shared, pure action wiring so desktop and narrow cannot diverge
Introduce a small pure module (e.g. `runtime-action-handlers.ts` next to `derive-runtime-decision.ts`) exposing:
- `getStopConsequenceCopy(stopRecoverable: boolean | null): { title, body }` — the single source of the recoverable/irreversible wording, consumed by the desktop inline block **and** the narrow drawer.
- `invokeAction(kind, { decision, mutations })` — the `kind → mutation.mutate(...)` map (including the `stopRecoverable ? forceStop : stop` split and `sendBack`'s `{ stage: decision.approvalStage, body }`).

- *Why*: both surfaces must call the identical mutations with identical payloads and show identical consequence copy. Centralizing removes the highest-risk duplication (the proposal flags the many conditional action paths as the medium-risk area).
- *Alternative*: duplicate the switch in each surface — rejected; the two would drift, exactly the regression the risk note warns about.

### D3 — New components live under `pages/issue-detail/ui/`, composed of a presentational drawer primitive
- `MobileActionBar.tsx` — owns the narrow action + confirmation state as one cohesive unit. Renders (1) the fixed bottom bar with the single primary button and (2) the `ConfirmationDrawer` when a destructive confirmation is open. Local state: `confirmKind: 'stop' | 'send-back' | null`.
- `ConfirmationDrawer.tsx` — a minimal bottom-sheet primitive (`fixed inset-x-0 bottom-0`, content-sized height, slides via `translate-y-full` ⇄ `translate-y-0` with `tw-animate-css`). `role="dialog"`, `aria-modal="true"`, Esc-to-close, focus moves to the drawer on open. Children: consequence copy + Confirm/Cancel (and the send-back `<Textarea>` in the send-back variant).

- *Why a custom drawer, not `Dialog`/`AlertDialog`*: the shared kit has **no `sheet`/`drawer`** (verified), and `AlertDialog` renders a centered full-screen overlay that would (a) cover the sticky headline — violating the core spec requirement — and (b) fight the "bottom-sliding" requirement. A purpose-built bottom sheet is small and exactly fits the constraint that the headline stays visible.
- *No dimming scrim*: a full-viewport scrim would sit above the `z-20` headline and obscure it. Instead the sheet is focal on its own; dismissal is via Cancel, Esc, or a transparent click-catcher limited to the region below the headline. (See Open Questions for the scrim nuance.)
- *Alternatives considered*: (a) add a shadcn `sheet` dependency — rejected, adds a dependency for one consumer and still needs the no-scrim headline carve-out; (b) reuse `AlertDialog` repositioned to the bottom — rejected, its overlay semantics are wrong for "headline visible".

### D4 — z-index layering
| Element | z | Rationale |
|---|---|---|
| `StatusHeadline` (sticky) | `z-20` | unchanged |
| `MobileActionBar` (fixed bottom) | `z-30` | above page content, below global chrome; does not overlap nav spatially |
| `ConfirmationDrawer` (open) | `z-50` | claims the bottom region (incl. over the bar and the global nav) while open; content-sized so it never reaches the top headline |
| `MobileBottomNav` / `FAB` (global) | `z-40` | unchanged |

The drawer sits above global chrome only while open and only at the bottom edge; the headline remains visible by geometry (drawer is bottom-anchored and short) **and** by z-order it never extends to the top.

### D5 — Nav-offset and rendering gate use *two different* viewport signals, by design
- **Existence** of the bar/drawer is React-gated by `useNarrowViewport()` (`<1024px`) so they are absent from the DOM at `lg`+ ("SHALL NOT render").
- **Vertical offset** of the bar is pure CSS keyed on `md` (the nav's own breakpoint): `bottom-[calc(3.5rem+env(safe-area-inset-bottom))] md:bottom-0`. So below `md` (nav present) the bar clears the nav; in the 768–1024px band (narrow page, nav hidden) it anchors flush to the bottom edge. No second JS hook is needed.

- *Why split*: the spec's nav-offset requirement is keyed on *nav presence* (`md`), while "render only on narrow" is keyed on `lg`. Encoding both in one boolean would mis-handle the 768–1024px band. CSS-on-`md` mirrors exactly how `MobileBottomNav` itself is gated, so the two stay in lockstep.
- *Alternative*: a `useMobileBottomNavVisible()` hook — rejected as redundant; the nav already declares its own breakpoint via `md:hidden`, and CSS is the single source of truth for it.

### D6 — Bottom-padding reservation lives inside the scroll container and is conditional on the bar
Add the reservation to the inner content column inside `issue-detail-page-container` (the `max-w-4xl … py-6` block): when `isNarrowViewport && decision.primary`, promote `pb` to clear the bar height; otherwise leave the default. The global nav's space is already reserved by `App.tsx:59` on the *wrapper*, so the scroll container does not re-reserve it.

- *Why here*: the bar is `fixed` to the viewport and overlays the scroll region; only padding *inside* the scroll container lets the last comment scroll clear of it. Making it conditional on `decision.primary` satisfies "no padding reserved for the bar when no primary action exists; nav-only padding still applies".

### D7 — Reference rail: pin, don't rebuild
The rail already collapses to stacked `CollapsibleRailCard`s below `lg` with `forceCollapsed={isNarrowViewport}` and drift/convergence `defaultCollapsed` (#341). This change **locks the invariant** (rail after reading flow in document order; low-frequency items collapsed by default on every viewport; `lg`+ right column restored; no mobile-only element at `lg`+) primarily via tests, plus a document-order audit to confirm every rail section follows the last reading-flow item. No structural rail change expected unless the audit finds otherwise.

## Risks / Trade-offs

- **[Regression across conditional action paths]** The relocation touches every runtime-summary branch (approval, failed/blocked, queued/backlog, running, done, archived). → *Mitigation*: D2 single-sources mutation calls and copy; assert every runtime-summary path renders correctly under **both** narrow and desktop layouts (port the existing `RuntimeDecisionSurface` + `IssueDetailPage` scenarios to a narrow-viewport matrix).
- **[Secondary actions vanish on narrow]** Stripping `RuntimeDecisionSurface` on narrow drops retry/resume/rerun from the page (only `decision.primary` is in the bar). → *Mitigation*: acceptable per the issue's single-primary thesis and Non-Goals; tracked in Open Questions for a follow-up overflow affordance if usage shows demand.
- **[768–1024px band surprises]** The "narrow-but-no-nav" band is easy to mis-test. → *Mitigation*: D5 makes it pure CSS on `md`; add an explicit spec test at ~900px asserting flush-bottom anchorage and no reserved nav space.
- **[Custom drawer a11y]** A hand-built sheet must match the focus-trap / Esc / aria semantics shadcn gives for free. → *Mitigation*: model `role="dialog"` + `aria-modal` + focus management on the existing `dialog.tsx`/`alert-dialog.tsx`; cover with axe/manual checks.
- **[Fixed-bar overlay vs. iOS toolbars]** Browser chrome / safe-area changes can shift the fixed bar. → *Mitigation*: reuse `env(safe-area-inset-bottom)` like `MobileBottomNav` does.
- **[Two confirmation UX models to maintain]** Desktop inline + narrow drawer now coexist. → *Mitigation*: D2 shares copy + invocation; the divergence is purely presentational and scoped by viewport.

## Migration Plan

This is a front-end-only, flag-free change behind viewport width — no data migration, no API contract change.

1. Land D2's shared pure module first (`getStopConsequenceCopy`, `invokeAction`) and refactor the existing desktop `RuntimeDecisionSurface` to consume it — behavior-identical, covered by the existing `RuntimeDecisionSurface.test.tsx`.
2. Add `ConfirmationDrawer` + `MobileActionBar`; wire narrow rendering + CSS offsets + scroll-padding in `IssueDetailPage.tsx`.
3. Add the spec-test matrix (narrow × every runtime summary; 768–1024px band; `lg`+ desktop restoration; rail ordering/drift-convergence collapse; headline-visible-while-drawer-open).
4. Verify `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` pass.

**Rollback**: revert the `IssueDetailPage.tsx` render branch (and remove the two new files + the shared module). Desktop behavior is untouched by step 1's refactor (it is a pure extraction), so rollback restores the exact prior surface. No server/runner/CLI coordination required.

## Open Questions

1. **Secondary runtime actions on narrow** — retry / resume / rerun are not in the bar (spec scopes the bar to `decision.primary` only). Do they need a narrow home (e.g. a bar overflow menu, or relocation into the reference rail)? *Deferred* until usage data justifies it; flagged as a known trade-off, not a blocker.
2. **Scrim semantics for the drawer** — currently no dimming scrim (to keep the headline readable). Is a *light*, bottom-anchored scrim (bounded so it never reaches the headline) desirable for focus, or is the bare sheet enough? Decide during visual QA.
3. **Focus-trap scope** — for the send-back drawer with a `<Textarea>`, should the trap be confined to the drawer (Radix-like) or allow scroll-through to peek at the headline? Lean: trap inside the drawer, but keep the headline visible above it so the user can still read status without interacting.
4. **Animation polish** — bar appear (subtle slide-up vs. instant) and drawer easing curve; confirm against `tw-animate-css` tokens during implementation.
