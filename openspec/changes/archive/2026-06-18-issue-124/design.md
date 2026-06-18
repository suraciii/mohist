## Context

Issue Detail is the primary operator surface for a live issue. Today its layout does not contain its own content:

- The right-rail Details card renders repository metadata inline (`IssueDetailPage.tsx`, the `Repository` row around the `CardSection title="Details"`). A long git URL like `master(https://github.com/suraciii/mohist.git)` sits in a `flex justify-between gap-3` row with no wrap/break/truncate, forcing page-level horizontal scroll at ~1280px.
- The page uses `<div className="grid grid-cols-1 lg:grid-cols-3 gap-6">`; the right column is a single `space-y-4` list.
- The right rail mixes distinct intents in one undifferentiated stack: Details (metadata), LatestArtifactsPanel, Base Drift / Interrupted / Convergence alerts, an Actions card that **also** nests the `IssueModelSelector` (Coder Model + per-stage overrides) behind a divider, Start/Add Prerequisite, then TaskProgressPanel and WorkflowSessionsPanel at the bottom.
- The workflow stage bar (`WorkflowView.tsx`, `StageBar`/`StageBarCell`) renders all five stages (Plan, Build, Check, Integrate, Done) as `flex-1 min-w-0` cells with no responsive variant. At ~390px the labels overflow their squeezed buttons.
- Icon-only controls (e.g. the issue Edit button using `size="icon-sm"`) carry a `title` but no accessible name.

Stack: React + Tailwind v4 (default breakpoints sm 640 / md 768 / lg 1024 / xl 1280), shared `CardSection` primitive (`tone`, `titleAs`), and an existing `useIsMobile()` hook (768px) already used elsewhere. Tests run on vitest + jsdom; `tests/setup.ts` polyfills `window.matchMedia` and defaults `window.innerWidth` to 1280, so desktop/mobile branches are reachable in tests by overriding `innerWidth`.

## Goals / Non-Goals

**Goals:**
- Keep Issue Detail free of page-level horizontal scroll at common desktop widths when repository git URLs are present, by bounding repository metadata (name, base branch, git URL) within its column.
- Make the workflow stage bar readable and operable on mobile.
- Regroup the right rail by user intent: metadata, latest artifacts, runtime/session summary, configuration, and workflow actions.
- Separate safe inspection links from state-changing actions.
- Make icon-only controls accessible with adequate hit targets.
- Add responsive/component test coverage at desktop and mobile widths.

**Non-Goals:**
- No workflow state semantics, recovery projection, or task-progress read-model changes.
- No shared Markdown Reader work (already tracked separately).
- No Settings, Epics, or board redesign.
- No backend, API, persistence, or data-model changes — this is a frontend-only layout/a11y change.

## Decisions

### Decision 1: Containment via layout utilities, not a new component
Bound repository metadata with existing Tailwind overflow utilities rather than introducing new primitives. Concretely:
- Put `min-w-0` on the grid column / flex children so the column is allowed to shrink instead of forcing its intrinsic content width.
- Render the repository name and git URL on separate lines; apply `break-all` (or `break-words` + `overflow-wrap:anywhere`) to the URL span and a `title` tooltip carrying the full URL, plus an optional copy affordance.
- Add `overflow-x-hidden`/`min-w-0` containment to the page content container so a stray long token cannot widen the viewport.

**Alternatives considered:**
- *Truncate with ellipsis + tooltip only.* Rejected as the sole strategy: it hides the URL until hover, which is poor on mobile/touch. Wrap/break keeps it visible; the copy affordance makes the full value reachable.
- *A dedicated `RepositoryMeta` component.* Rejected for now: the metadata rows are simple `dl` entries; containment utilities are enough without new surface area. Can extract later if reused.

### Decision 2: Mobile stage bar as a horizontally scrollable stepper
Reuse the existing `useIsMobile()` hook (768px). On mobile, render the `StageBar` as a horizontally scrollable stepper: each `StageBarCell` gets a sensible fixed `min-w` and the container uses `overflow-x-auto`/`flex-nowrap`, so all five stages remain reachable without compressing labels. On desktop (`md+`), keep the current `flex-1` fill layout unchanged. Selection semantics, the `selectedStage` state, and the `readOnly` gating are unchanged.

**Alternatives considered:**
- *Compact current-stage-only display (segmented/dropdown).* Rejected as the primary approach: it hides non-current stages and changes more behavior. The scrollable stepper is the smallest change that satisfies "no squeezed labels" while keeping every stage one swipe away. Revisit if it still feels cramped in QA.

### Decision 3: Regroup the right rail by intent, reusing `CardSection`
Reorder/relabel the right column into consistent intent groups using the existing `CardSection` (with its `title`), rather than a new `SidebarGroup` component:
1. **Details** — metadata (with containment from Decision 1).
2. **Latest Artifacts** — inspection (existing panel).
3. **Runtime / Sessions** — group `TaskProgressPanel` + `WorkflowSessionsPanel` together as the runtime/session summary.
4. **Configuration** — extract `IssueModelSelector` (Coder Model + per-stage overrides) **out** of the Actions card into its own Configuration group.
5. **Actions** — state-changing workflow actions only (start, stop, force stop, retry, rerun, resume, close, reopen), plus the recovery/interrupted guidance.
6. Drift / Interrupted / Convergence alert cards stay as status alerts adjacent to the runtime context they describe.

**Alternatives considered:**
- *A new `SidebarGroup` wrapper.* Rejected: `CardSection` already provides the labeled, toned, separated group primitive used across the page. Reusing it keeps visual consistency and minimizes new surface.
- *Collapsible/accordion groups.* Rejected for this issue: adds state and complexity beyond containment/IA cleanup.

### Decision 4: Inspection links vs. state-changing actions
Establish the visual contract by placement + styling rather than new components: safe inspection links (Latest Artifacts, session transcript chips, "View files", commit rows) keep their existing link/chip treatment and live outside the Actions card; state-changing actions live only inside the Actions card. After Decision 3 moves `IssueModelSelector` out, the Actions card contains only mutating controls, so inspection and mutation are no longer interleaved.

### Decision 5: Accessibility — accessible names + existing icon-button hit targets
- Add explicit accessible names (`aria-label`) to icon-only controls; the Edit button is the primary example (it currently has only `title`).
- Ensure primary icon-only controls use the project's standard icon-button variant as the hit-target baseline (the existing `icon`/`icon-sm`/`icon-xs` variants); avoid sub-baseline sizes for primary controls on mobile.

**Alternatives considered:**
- *Invent a fixed 44px baseline.* Rejected: the issue specifies the "local UI baseline," which is the project's icon-button variant sizing. Asserting a custom number risks drift from the design system.

### Decision 6: Test strategy that works under jsdom
Because jsdom does not compute real layout, test containment and responsiveness structurally rather than via measured `scrollWidth`:
- Assert the repository URL element carries the containment contract (`data-testid`, break utilities) and that metadata renders within the Details group; use a wide `innerWidth` mock for the desktop branch.
- For the mobile stage bar, override `innerWidth`/`matchMedia` to force the `useIsMobile()` branch and assert the scrollable-stepper variant renders (testid) and that all stage labels are present and readable.
- Assert the right rail renders the intent groups (Details / Configuration / Actions / Runtime-Sessions) and that `IssueModelSelector` is outside the Actions group; assert icon-only controls expose `aria-label`.

**Alternatives considered:**
- *Real browser/visual regression tests.* Heavier than warranted here; structural assertions plus the existing hook are sufficient and keep CI fast.

## Risks / Trade-offs

- [Breaking the git URL with `break-all` can look ugly for short URLs] -> Apply the break utility only to the URL span (not the whole row); short URLs render normally, only long tokens break.
- [A scrollable mobile stepper hides off-screen stages] -> Keep the current/selected stage scrolled into view; selection semantics are unchanged; revisit in QA if stages are missed.
- [Moving `IssueModelSelector` out of Actions changes discoverability] -> Clear "Configuration" label; the underlying issue API and model-override behavior are unchanged, so existing model-override tests still guard it.
- [Right-rail reordering may surprise existing users] -> Pure layout/IA change, no data or action removed; reversible by revert.
- [jsdom can't measure true overflow] -> Mitigated by structural/DOM assertions (Decision 6); accept that pixel-exact overflow is not asserted in unit tests.

## Migration Plan

- Frontend-only change, shipped in a single PR. No backend, persistence, API, or data-model change; no schema migration.
- No feature flag required: the change is layout/IA/a11y with identical behavior and data.
- **Rollback:** revert the PR. No persisted state is affected, so rollback is clean and immediate.

## Open Questions

- Confirm the exact hit-target baseline the project intends (icon variant = 32px?) versus a stricter WCAG 44px target for primary mobile controls — resolve during implementation against the design-system variants.
- Decide whether the full-width merge/diff summary banner above the grid (a long `flex` row with head/base labels) should also be wrapped for containment; it is adjacent to the reported overflow and likely worth containing in the same pass, but is not the primary evidence.
