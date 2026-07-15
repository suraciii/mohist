## Why

The Coder Session page is the surface an owner uses to inspect execution evidence and act during workflow recovery, but on compact phone viewports (375x667, 320x568) the session summary and fixed mobile navigation consume the entire height budget, collapsing the transcript to a thin strip or zero pixels and pushing the Compact/Reset recovery controls out of reach. The evidence-view layout landed in issue #402 established a stable region hierarchy for desktop but added no compact-mobile accommodations, so a user on a small phone cannot read the transcript or reach the available session controls - exactly the moment recovery decisions matter.

## What Changes

- Preserve a usable, scrollable transcript region at compact mobile widths and heights (down to 320x568) instead of letting static summary metadata and the follow-up composer squeeze it to near-zero height.
- Reduce nonessential session-summary density at compact viewports (stacked status, stage, model, turn count, last-activity, files, duration, session id) before reducing the transcript reading surface, while keeping session identity and current status visible.
- Make the existing Compact/Reset recovery controls and other session controls reachable through normal page navigation at compact viewports, rather than hidden below the fold or covered by the fixed mobile bottom navigation.
- Ensure fixed mobile navigation does not occlude transcript content or session controls at any tested compact viewport.
- Desktop and larger mobile layouts (md and up) keep their existing session evidence, navigation, and control behavior unchanged.

Non-goals (per issue): do not build a mobile-first workflow or PWA; do not change session, workflow, status, or transcript persistence semantics; do not redesign Activity, Files, Diff, or unrelated issue pages; do not add new session controls or recovery operations.

## Capabilities

- `coder-session-compact-viewport`: How the Coder Session reading layout behaves at compact mobile viewports - preserving a readable, scrollable transcript region that never collapses to zero height; reducing nonessential summary density before shrinking the transcript while keeping session identity and status visible; keeping existing session controls (Compact/Reset, follow-up composer, cancel) reachable through normal page navigation; and ensuring fixed mobile navigation does not occlude transcript content or controls. Covers the shared `SessionDetailShell` reading layout, its summary/usage/errors regions, the transcript scroll container, the sticky recovery bar, and the follow-up composer, scoped to compact viewports below the `md` breakpoint. Does not change transcript recording, session lifecycle, status semantics, recovery gating, or desktop/larger-mobile layout.

## Impact

- **Web** (`packages/web/src`):
  - `pages/session/ui/SessionDetailShell.tsx` - the main reading layout; its flex height budget, summary region, transcript scroll container, sticky recovery bar, and follow-up composer need compact-viewport accommodations. Currently uses no `useNarrowViewport`/`useIsMobile` branching and reserves no extra bottom padding for in-flow controls.
  - `pages/session/ui/SessionUsageSummary.tsx` - the usage strip that adds ~60-80px above the transcript; candidate for density reduction at compact viewports.
  - `widgets/coder-session/ui/SessionRecoveryActions.tsx` - renders the Compact/Reset buttons inside the sticky recovery bar; on mobile the recovery bar stacks ContextHealthBar above the buttons, adding height inside the starved transcript scroll area.
  - `widgets/coder-session/ui/SessionFollowupComposer.tsx` - `shrink-0` composer (~80-90px) below the transcript; contributes to squeezing the transcript.
  - `widgets/app-shell/ui/MobileBottomNav.tsx` - the fixed `bottom-0` nav (`md:hidden`, `z-40`, h-14) that occludes content; its clearance is reserved globally in `App.tsx` via `pb-[calc(3.5rem+env(safe-area-inset-bottom))]` but the session page does not reserve additional space for its own in-flow controls (unlike the `IssueDetailPage`/`MobileActionBar` precedent).
  - `app/App.tsx` - establishes the `h-svh` + `flex-1 min-h-0` height chain and the global bottom-nav padding; may need session-page-aware clearance if the compact layout adds floating/sticky controls.
- **Precedent**: `pages/issue-detail/ui/IssueDetailPage.tsx` + `MobileActionBar.tsx` show the existing pattern for compact-viewport handling (`useNarrowViewport()` + extra bottom padding + floating action bar above the nav) that the session page does not yet follow.
- **Server / Runner / CLI**: none.
- **Dependencies**: none added.
- **Tests** (`packages/web`): existing `pages/session/ui/SessionPage.*.test.tsx`, `GenericSessionPage.test.tsx`, and `tests/SessionPageHeader.spec.tsx` cover the desktop evidence-view layout and must keep passing; new spec tests assert compact-viewport transcript usability, control reachability, and navigation non-occlusion at 375x667 and 320x568. Per `design/testing.md`, browser/visual viewport assertions run separately and do not enter the default `npm test`.
- **Risk (medium)**: the affected page is used to inspect execution evidence and act during workflow recovery. Mitigated by scoping changes to compact viewports below `md`, preserving desktop/larger-mobile layout and all existing `data-testid` anchors, and not changing recovery gating or session lifecycle semantics.
