## Context

`packages/web/src/pages/session/ui/SessionDetailShell.tsx` renders the session page across `SessionPage` (issue-bound) and `GenericSessionPage` (generic). Today's frame around the transcript is the result of three orthogonal rewrites stacked on top of each other:

- The **header** (`SessionHeader`, lines 544–742) lays out metadata across two rows (`flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-3`), with each metadata item conditioned by a `hidden md:inline` + a `text-muted-foreground/40` `·` divider. The `Cancel session` button sits in the same right-aligned column with `variant="destructive"` (highest visual weight). The session id is rendered as `<span className="font-mono text-muted-foreground text-xs">{meta.sessionId.slice(0, 8)}</span>` — truncated, no copy affordance. The header hosts the `siblingNav` slot (prev/next links) under `data-testid="session-sibling-navigation-slot"`, which duplicates the `SiblingSessionsSidebar` already rendered to the right of the transcript on `xl+`.
- The **sticky identity strip** (`StickySessionTitle`, lines 744–758) is the first child of the transcript scroll container, pinned `sticky top-0 z-20`, always rendered. Because it sits inside the scroll container *below* the outer header, the session name + status appear twice on the first screen.
- The **recovery actions** (`packages/web/src/widgets/coder-session/ui/SessionRecoveryActions.tsx`) expose `data-active="true|false"` plus a `title="Unavailable while session is active"` browser tooltip on disabled buttons; the current reason taxonomy is a single string.
- The **time formatter** is a private function `formatRelativeTime` (`SessionDetailShell.tsx:451`) that always returns a relative string and reads `Date.now()` directly. It is consumed by the header (`lastActivityAt`, `probeSentAt`) and has no test surface of its own.
- The **followup composer** (`packages/web/src/widgets/coder-session/ui/SessionFollowupComposer.tsx`) has two states: `interactive` (placeholder + enabled input + send) and `disabled` (banner "Session is no longer accepting followups."). The `isSending` prop produces a transient `Sent` flash but does not produce a persistent "queued" indicator.
- A `Tooltip` primitive already exists (`packages/web/src/shared/ui/components/tooltip.tsx`) using a hover/focus `<span>` wrapper with `aria-describedby`; it is the right shape for structured disabled-reason tooltips.
- A `useNow({ intervalMs, now, enabled })` hook already exists (`packages/web/src/widgets/session-transcript/model/use-now.ts`); it accepts an injected `now` for deterministic tests and is the project's established time-injection seam.

The four product behaviors and normative requirements live in `proposal.md` and `specs/{session-header-meta-line,session-sticky-identity,session-action-weight,session-sibling-nav-dedup,session-time-display,session-followup-state-hints}/spec.md`. Constraints (epic #49): no data model, event protocol, liveness gate (#426), row anchor (#427), live activity (#428), or jump highlight (#429) change. This change is purely the page-frame layer around the transcript widget.

Stakeholders: any reader who opens a Mohist session (primary); reviewers watching running sessions (secondary, for the Cancel affordance); long-after-the-fact readers returning to a finished session (secondary, for absolute time + closed-state copy).

## Goals / Non-Goals

**Goals:**

- Render the session header metadata as a single row of stable, separately testable items, with session id as a one-click copy control that exposes the full id to assistive tech.
- Make the sticky identity strip scroll-engaged: hidden while the outer header is visible, sticky-visible once the header scrolls out, only carrying session name + status + turn count.
- Demote the Cancel session button out of the primary destructive variant and into a secondary slot (icon-only / outline / menu), keeping keyboard reachability and the existing confirm dialog.
- Surface a structured disabled-reason tooltip on Compact/Reset for the existing running-session and mutation-pending disable states, wrapped by the existing `Tooltip` primitive; no new closed-set attribute is introduced.
- Remove the header prev/next sibling slot on wide viewports; keep it as a narrow-viewport fallback so navigation stays reachable when the sidebar is hidden.
- Render absolute date-time as the default for terminal sessions (`completed` / `failed` / `stale`) older than the 1-hour threshold; keep relative for live sessions and for fresh terminal sessions; expose the alt form as a hover/focus tooltip.
- Render three explicit followup states (interactive / queued / closed), with copy that matches real behavior — closed-state copy references the session's `completedAt` and falls back to a generic message when no timestamp is available.

**Non-Goals:**

- No transcript content, row anchor, or row rendering change (#427/#428/#429).
- No `SessionMetadata` field change; no new event type; no liveness-gate change; no `SessionDataSourceResult` contract change.
- No redesign of the `SiblingSessionsSidebar`'s own content (the issue explicitly leaves it alone).
- No application-wide navigation change; only the session page header slot.
- No mobile placement of the sibling sidebar; the sidebar keeps its existing `xl+` visibility.
- No live ticking of the time formatter beyond what `useNow` already provides; the formatter is invoked per-render with an injected `now`.

## Decisions

### D1: Extract the time formatter to `shared/lib/format-time.ts` and make it status-aware

Move `formatRelativeTime` from `SessionDetailShell.tsx:451` to a new module `packages/web/src/shared/lib/format-time.ts`. The new helper is `formatSessionTime({ date, statusKind, now })` returning `{ primary: string; secondary: string }`, where `primary` is what renders inline and `secondary` is what the tooltip shows. The threshold is `now - date ≥ 1h`. Branches:

| `statusKind` | `now - date` ≥ 1h | primary | secondary |
|---|---|---|---|
| `completed` / `failed` / `stale` | yes | absolute date-time (`Jun 17, 09:52`) | relative (`8h ago`) |
| `completed` / `failed` / `stale` | no | relative (`5m ago`) | absolute |
| `live` / `finalizing` / `probing` | any | relative | absolute |

For terminal sessions the helper is invoked with `date = max(completedAt, lastActivityAt)` (the more recent of the two), as `session-time-display/spec.md` requirement 2 defines. The "Checking since <relative>" phrasing for `probing` is a separate call site that pins the relative form regardless of threshold (it does not go through the helper); the helper's probing row guarantees the `relative` arm but the probing indicator chooses not to use `secondary`. The helper MUST accept `now` as an argument (no implicit `Date.now()`), making `session-time-display/spec.md`'s determinism requirement satisfiable.

**Rationale.** Tests can drive the absolute-vs-relative branch by varying `now` without `vi.useFakeTimers`, matching the project's testing rule ("禁止真实时间"); `formatRelativeTime` is currently untestable. Status-aware branching keeps live sessions readable while fixing the "8h ago" mental arithmetic for finished ones.

**Alternatives considered:**

- *Keep the helper inside `SessionDetailShell.tsx` and just add a `statusKind` parameter.* Rejected — no test surface for a presentational helper that is now policy-bearing; mixing it into the shell makes it harder to reuse from `StickySessionTitle` and any future consumer.
- *Push branching into the call sites.* Rejected — every call site would need to know the threshold and the alt form, scattering the policy.
- *Helper signature `formatSessionTime({ date, statusKind, anchor, now })` with a separate `anchor` parameter for the threshold reference.* Rejected — only one timestamp is involved (the one being formatted); two parameters would be redundant or unclear, so Occam drops the second.

### D2: Sticky identity strip uses IntersectionObserver against the outer header; wrapper owns the initial hidden state

The sticky strip's visibility is driven by an `IntersectionObserver` registered on the outer header (`data-testid="session-header"`) with `root: scrollContainerRef.current` and `threshold: 0`. A small wrapper component (call it `ScrollEngagedStickyTitle`) owns the `engaged` boolean: it starts at `engaged=false`, and only flips to `true` after the observer's first callback reports `intersectionRatio < ~0.001` (header fully scrolled past). The wrapper renders `<StickySessionTitle>` only when `engaged=true`; until then it renders `null` (no DOM, no layout contribution, no a11y surface). When the header re-enters, `setEngaged(false)` collapses the strip back to null. A `requestAnimationFrame` flush between the toggle and any potential `scrollTop` measurement avoids mid-frame paint.

The inner `StickySessionTitle` component itself is unchanged — no new prop. Visibility is owned entirely by the wrapper. This guarantees the initial-render hidden invariant from `session-sticky-identity/spec.md` requirement 1.

**Rationale.** IntersectionObserver is the platform-native primitive for "is X visible inside Y"; it avoids the cost of a scroll listener that would have to fire on every scroll tick. Tying visibility to the actual outer-header element (not a pixel offset threshold) survives any header-height change. Rendering `null` (vs `display: none` or `visibility: hidden`) is the strongest no-layout contribution guarantee — the node is not in the DOM at all.

**Alternatives considered:**

- *Pixel-offset threshold tied to `scrollContainerRef.scrollTop`.* Simpler but tied to header height; regresses if the header grows; does not account for header resize.
- *CSS-only `position: sticky` with a sentinel element above the strip.* Pure CSS, but then "hidden on first screen" requires JS anyway to suppress the sticky rendering; the IntersectionObserver is the source of truth for that.
- *A second `ResizeObserver` on the header so the threshold updates if the header height changes.* Defer — IntersectionObserver already returns `intersectionRect` whose bottom relative to the root gives us exactly the "scrolled past" signal.
- *Add an `engaged` prop on `StickySessionTitle` itself, default `true` for backward compatibility.* Rejected — `true` would render the strip visible on first render, directly violating the spec's "hidden on first screen" invariant. The wrapper, not the inner component, owns visibility.

### D3: Header metadata row is a flex wrap with stable test selectors and no `·` dividers

`SessionHeader` collapses its existing `flex flex-col ... sm:flex-row` + scattered `hidden md:inline` `·` separators into one flex row: `<div className="flex flex-wrap items-center gap-x-2 gap-y-1">` (no `sm:` split — wrap is the responsive behavior). Each item is its own element with a stable `data-testid` and a `data-*` value attribute:

| item | testid | value attribute |
|---|---|---|
| session name | `session-header-name` | — |
| status badge | (existing `session-status-badge`) | `data-status-kind` |
| stage chip | `session-header-stage` | `data-stage` |
| model | `session-header-model` | `data-model` |
| turn count | `session-header-turn-count` | `data-turn-count` |
| last activity | `session-header-last-activity` | `data-last-activity` |
| duration | `session-header-duration` | `data-duration-ms` |
| session id | `session-header-session-id` | `data-session-id` (full value) |

Each item keeps its own visual style. The flex row wraps to a second row when the viewport cannot fit; nothing is dropped. The `·` separators are removed entirely.

**Rationale.** Single-row invariant without a width cap matches the issue's "header 单行化" intent. Wrapping to a second row is the responsive behavior that satisfies `session-header-meta-line/spec.md`'s graceful-collapse requirement. Stable `data-testid`/`data-*` make every item individually targetable by tests and embedders without depending on layout order.

**Alternatives considered:**

- *CSS grid with explicit `grid-template-areas` per breakpoint.* Cleaner breakpoints but the items are not aligned on a strict grid; the layout is "row of chips", which `flex flex-wrap` already expresses.
- *Force horizontal scroll on narrow viewports.* Rejected — wrapping is more discoverable than a scrollable metadata bar.

### D4: Session id is a copy-to-clipboard icon button with a transient confirmation

A new small component `SessionIdCopyButton` (or inline in `SessionHeader`) renders an icon button with `aria-label="Copy session id <full id>"`, `data-testid="session-header-session-id"`, and `data-session-id={meta.sessionId}` (full value). On click it writes `meta.sessionId` to the clipboard via `navigator.clipboard.writeText`. The button shows a transient "Copied!" tooltip / state for ~1.5s (uses the same `setTimeout` pattern that `SessionFollowupComposer` already uses for its `Sent` flash; spec requires the timing to be tested via `vi.useFakeTimers`).

The display next to the icon is the first 8 characters of the id (matching today's visual) with `title={meta.sessionId}` as the browser tooltip fallback so users who don't activate the button still see the truncated prefix.

**Rationale.** The icon button is a familiar pattern (matches the existing `CopyFullTextButton` shape); using `navigator.clipboard.writeText` is the platform-native API; the transient confirmation closes the loop without adding a sticky banner.

**Alternatives considered:**

- *Inline text + "copy" link.* Less discoverable than an icon button; the icon pattern already exists in the codebase.
- *Make the entire truncated span the copy target.* Tempting, but conflates "what you see" with "what you can do"; assistive tech would announce the truncated prefix as the actionable name.

### D5: Cancel session lives in a secondary action slot with an icon-only button

The Cancel button moves from inside the metadata row to a small action slot rendered after the metadata row (right-aligned via `flex justify-end` on the slot wrapper). The button uses `variant="ghost"` (or `variant="outline"` if ghost is not available in the design system) and `size="sm"` with the same `CircleStopIcon`. `aria-label="Cancel session"` remains. The existing `AlertDialog` (cancel confirmation) and the cancel mutation are unchanged.

**Rationale.** Removing the destructive variant from the metadata row matches the issue's "操作权重分级" intent. Ghost / outline is the design system's standard "secondary CTA" weight; the icon-only form (label visible inline still — `Cancel session` text is kept for clarity at first release) is the lightest that still keeps the action discoverable without an extra click.

**Alternatives considered:**

- *Push Cancel into a kebab / overflow menu.* Hides the action behind a click; reviewers actively watching a running session may not look for it. Rejected for first release; revisit if dogfooding shows the inline button still over-weights.
- *Replace the label with `aria-label` only.* Rejected — the design system does not yet have a documented icon-only destructive pattern; mixing it in risks accidental weight loss for genuinely destructive one-off actions elsewhere.

### D6: Disabled-reason tooltips go through the existing `Tooltip` primitive; active and pending reasons, no new contract attribute

`SessionRecoveryActions` already exposes `data-active="true"` on Compact/Reset buttons for the running-session case and derives `anyPending` from its existing Compact/Reset mutations. Both cases already disable the controls, so both require a structured reason. `data-active="true"` remains the running-session contract; the pending reason is local presentation derived from the mutation state. No closed-set `data-disabled-reason` attribute and no speculative "prereq" / "unknown" branches are introduced. The structured tooltip uses one of two fixed pairs:

| title | reason sentence |
|---|---|
| "Session is running" | "Finish or cancel the session before compacting or resetting." |
| "Recovery action in progress" | "Wait for the current recovery action to finish before starting another one." |

The wrapper is the existing `Tooltip` primitive (`packages/web/src/shared/ui/components/tooltip.tsx`). It already wraps the child in a `tabIndex={0}` span with `onFocus`/`onBlur` toggling the tooltip — no new a11y surface or new contract attribute is needed for the focus path; the existing wrapper IS the focus mechanism. The button's native `title="Unavailable while session is active"` attribute is removed to avoid double tooltips.

**Rationale.** The existing running-session and mutation-pending inputs already determine whether controls are disabled, so the presentation must explain both. A separate `data-disabled-reason` closed-set would duplicate those inputs without adding a new signal. The wrapper already handles focus — reimplementing the a11y chain would add code for no behavioural gain. Future reasons (genuine missing prerequisites) can be added when their triggers actually exist in the codebase.

**Alternatives considered:**

- *Radix-based tooltip.* Heavier, new dependency; the existing primitive covers the hover/focus + `aria-describedby` contract.
- *Render the reason inline next to the button.* Pollutes the row layout; defeats the "secondary slot" cleanup.
- *Closed-set `data-disabled-reason` with `"active" | "prereq" | "unknown"` for future-proofing.* Rejected — speculative; `prereq` and `unknown` have no concrete detection rule today, so an enumeration would be invented. Adding them when a real trigger appears (e.g. when a "prereq" actually emerges in the codebase) is cheaper than committing now and reconciling later.
- *Custom a11y wrapper that exposes a focusable proxy outside the disabled button.* The existing `Tooltip` primitive already does this — no new mechanism.

### D7: Sibling nav dedup uses a `matchMedia('(min-width: 1280px)')` check; sidebar stays CSS-hidden

`SessionHeader` reads `useMediaQuery('(min-width: 1280px)')` (a tiny new hook in `packages/web/src/shared/lib/use-media-query.ts` returning the current browser match synchronously, with an SSR-safe `false` fallback and a `matchMedia` listener registered on mount). When the query matches, the header does not render the `siblingNav` slot at all. When it doesn't match, it renders the existing prev/next links with a `data-viewport="narrow"` attribute on the slot wrapper so tests can distinguish the fallback from the removed wide-viewport version.

`SiblingSessionsSidebar` visibility is not touched — it remains the same CSS-driven visibility it has today (`xl:flex-row` on the parent at `SessionDetailShell.tsx:396`: `flex flex-col ... xl:flex-row`). No conditional render mechanism is added; the sidebar's data-testid is always present in the DOM, the parent flex layout decides whether it sits beside or below the transcript. This matches the de-facto behaviour the codebase has shipped and avoids an extra render gate whose only effect would be to remove a hidden DOM node.

**Rationale.** `matchMedia('(min-width: 1280px)')` is the same breakpoint the existing shell uses for `xl:flex-row`. The hook is small (one effect, one state, cleanup) and is exactly the shape that other layout branches in the codebase would benefit from later. Keeping the sidebar's visibility mechanism unchanged follows Occam — no new mechanism when the existing one already produces the right user-visible result.

**Alternatives considered:**

- *Container queries.* Tailwind 3 supports `@container`, but the design system has not adopted container queries project-wide; introducing one here would be inconsistent.
- *Pass the visibility decision down from `SessionDetailShell` via a prop.* Couples the shell to the header's viewport logic and forces the shell to read `matchMedia`; worse factoring.
- *Conditionally render `{siblingSidebar}` based on `useMediaQuery` (not just CSS-hide).* Rejected — would add a new render-gate mechanism just to remove a hidden DOM node. CSS-hidden is sufficient.

### D8: Followup composer accepts optional `state` / `endedAt` / `hasQueuedFollowup` props

The composer's existing props (`onSend`, `isSending`, `disabled`, `placeholder`, `className`) are unchanged and backward compatible. Three new optional props are added:

- `endedAt?: string | null` — when `disabled` is true and `endedAt` is set, the closed-state copy references `endedAt` via the same `formatSessionTime` helper (D1) in its relative form. When `endedAt` is absent, the copy falls back to the existing "Session is no longer accepting followups." generic phrasing.
- `hasQueuedFollowup?: boolean` — when true (and the composer is not `disabled`), the composer enters the `queued` state: input disabled, "Queued — waiting for agent..." indicator visible, submit button disabled. Clears back to `interactive` when the prop flips to false AND `isSending` is false.
- `state?: 'interactive' | 'queued' | 'closed'` — explicit override for callers that want to control the state directly. When omitted, the state is derived: `closed` if `disabled`, else `queued` if `isSending || hasQueuedFollowup`, else `interactive`. The `data-state` attribute on `session-followup-composer` reflects the resolved state so tests can assert the rendered state without poking internals.

The `Sent` flash behavior is preserved on the transient `isSending` window only when `hasQueuedFollowup` is not supplied (backward compatibility); when `hasQueuedFollowup` is supplied, the `Sent` flash is replaced by the persistent queued indicator.

**Rationale.** Optional props keep the existing tests green. The `state` prop is the explicit escape hatch for callers (e.g. `SessionDetailShell`) that already know the state from upstream signals (`canFollowup`, `followupIsPending`, last-seen turn). The derived fallback keeps older callers compiling. The `data-state` attribute is the single assertion surface.

**Alternatives considered:**

- *Replace the existing `disabled` and `isSending` props with a single `state` prop.* Breaks existing callers (notably the test suite in `SessionFollowupComposer.test.tsx`); deferred.
- *Auto-detect "queued" purely from the local `isSending` state with no external signal.* Insufficient: the queue persists past `onSend` resolution until the agent responds; the local `isSending` window is too narrow.

## Risks / Trade-offs

- [`IntersectionObserver` in jsdom is not supported by default.] -> Mitigation: the existing test suite already mocks `IntersectionObserver` where needed (the codebase uses it elsewhere); for sticky-identity specs, install the `jsdom` polyfill via `vi.stubGlobal` and assert the strip's `display` / `aria-hidden` / `inert` state via the resolved `engaged` boolean.
- [`matchMedia` in jsdom is not implemented by default.] -> Mitigation: the new `useMediaQuery` hook exposes a `setMatchesForTest(matches: boolean)` test seam (same pattern as `useNow`'s `now` injection) so specs drive the narrow/wide state directly; production behavior is unchanged.
- [Sticky strip's visibility flickers during the IntersectionObserver's initial fire if the strip is rendered before its first observation.] -> Mitigation: initialize `engaged` to `false` and only flip to `true` after the observer's first callback reports `intersectionRatio < 0.001`. Until that first callback, the strip is `display: none`. A rAF flush between state change and DOM commit avoids a paint of the intermediate state.
- [Cancel demotion might hide the action from users who don't recognize the icon.] -> Mitigation: keep the `Cancel session` text label inline next to the icon (not icon-only); the icon becomes a visual anchor, the label remains readable. `aria-label` reinforces for screen readers.
- [Tooltip on disabled buttons breaks for keyboard-only users if the primitive's focus trigger is removed.] -> Mitigation: the existing `Tooltip` primitive already wraps the child in a `tabIndex={0}` element with `onFocus`/`onBlur` toggling the tooltip; that primitive IS the focus mechanism — the disabled button remains operable via screen-reader focus landing on the wrapper span, with no new a11y code needed.
- [`navigator.clipboard.writeText` is not available in jsdom / older browsers.] -> Mitigation: feature-detect in the copy handler; fall back to a hidden `<textarea>` + `document.execCommand('copy')` or to surfacing the full id in a tooltip as a manual-copy fallback. The "transient confirmation" branch is not required when clipboard access fails — instead the tooltip stays open showing the full id so the user can copy manually.
- [Time formatter's `absolute` branch depends on `Intl.DateTimeFormat` which depends on the host locale.] -> Mitigation: pin the absolute format to a stable pattern (`'MMM d, HH:mm'` style, derived via `Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })`) so the rendered string is deterministic in tests and across locales. Tests assert against the formatted string with a frozen locale, never against the raw `Date`.
- [Followup composer's `state` prop can disagree with derived `disabled`/`isSending` if the caller passes all three.] -> Mitigation: `state` always wins when supplied (the prop's JSDoc documents this); derived state only applies when `state` is undefined. Asserted in `SessionFollowupComposer.test.tsx` as part of the three-state migration.
- [Removing the inline `·` separators from the header reduces visual scannability for users who relied on them.] -> Mitigation: each item keeps its own chip / badge styling (`rounded-full border` for stage, distinct colors for status), so the row remains scannable without the dividers.

## Migration Plan

**Deployment:**

1. **Time helper extraction** (`D1`): add `packages/web/src/shared/lib/format-time.ts`; move `formatRelativeTime` body into `formatSessionTime`; replace call sites in `SessionDetailShell.tsx`; add `formatSessionTime.test.ts` asserting all six branches and the determinism rule (same inputs → same output; changing `now` can flip the branch).
2. **Header single-row rewrite** (`D3`): rewrite `SessionHeader`'s metadata block; remove `·` dividers; add stable `data-testid` / `data-*` to each item; ensure existing render specs continue to pass (`SessionPage.test.ts`, `SessionPage.sticky.test.tsx`, `SessionPage.cancel.test.tsx`) by migrating any selector assertions to the new `data-testid`s.
3. **Session id copy** (`D4`): add the icon button + clipboard handler; wire it into `SessionHeader`; assert the full id is exposed via `aria-label` and `data-session-id`.
4. **Sticky strip scroll-engaged** (`D2`): add a `ScrollEngagedStickyTitle` wrapper in `SessionDetailShell.tsx`; its initial state is `engaged=false` (renders `null`); the `IntersectionObserver` flips it to `true` only after the header scrolls fully out. Assert the strip is not in the DOM on first render and after a scroll-back-to-top, and that the inner `StickySessionTitle` itself has no new prop.
5. **Cancel demotion** (`D5`): move the cancel button into the secondary slot; switch `variant` from `destructive` to `ghost`; assert `variant` and slot location; confirm dialog + mutation unchanged.
6. **Disabled-reason tooltip** (`D6`): wrap disabled Compact/Reset buttons with the existing `Tooltip` primitive, render the running-session or mutation-pending explanation, and remove the native `title` attribute. Assert both disabled reasons and the enabled state. No new attribute is added.
7. **Sibling nav dedup** (`D7`): add `useMediaQuery`; remove the wide-viewport `siblingNav` slot; keep the narrow fallback with `data-viewport="narrow"`; assert wide renders no slot, narrow renders the slot, both never render together.
8. **Followup composer three states** (`D8`): extend the composer with the three optional props + the `data-state` attribute; migrate `SessionFollowupComposer.test.tsx` to assert the new states (interactive / queued / closed) and that disabled input + persistent queued indicator match the spec.
9. **Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`** before commit. The change is presentation-only; no server / runner / CLI / protocol changes.

**Rollback:**

- Revert the commits. The header returns to the two-row metadata layout; sticky strip returns to always-visible; Cancel returns to destructive primary; time formatter returns to relative-only; followup composer returns to two states. No data, config, or API changes — pure frontend revert.

**Verification:**

- Unit (`*.test.ts(x)` colocation): `formatSessionTime` (six matrix rows + determinism + threshold flip); `useMediaQuery` (matches the query, updates on `setMatchesForTest`); `SessionRecoveryActions` (structured tooltip on disabled Compact/Reset driven by `data-active`; native `title` removed); `SessionFollowupComposer` (three states + behavior contracts).
- Spec (`*.spec.ts(x)` colocation): sticky strip first-render / scroll-out / scroll-back-to-top; cancel demotion + slot position; session id copy (clipboard spy, transient confirmation, full-id `aria-label`); sibling dedup (wide/narrow via `setMatchesForTest`); followup queued persistence past `isSending`.
- All time-dependent assertions use `vi.useFakeTimers` or the formatter's injected `now`; never `elapsed < N` or wall-clock waits, per `design/testing.md`.

## Open Questions

- **Cancel slot visual weight.** Ghost vs outline vs icon-only — keep text inline next to the icon for first release. If dogfooding shows it still reads as "primary", revisit a kebab menu.
- **Sticky strip offset when header is wider than expected.** The strip sits at `top-0` inside the scroll container; the recovery bar (`data-sticky="true"` at `top-9`) overlays it on scroll. Current ordering (`sticky top-0` strip, `sticky top-9` recovery) keeps the strip behind the recovery bar, which is fine. If dogfooding shows the layering is confusing, swap the offsets so the recovery bar is the visible top element once engaged. Default: keep current ordering.
- **Narrow-viewport sibling slot breakpoint.** The hook uses `min-width: 1280px` to match the existing shell's `xl`. If dogfooding on tablets (≤ 1280px) shows the sidebar collapse + no header slot feels barren, revisit at a wider breakpoint or add a thin horizontal sibling bar.
- **Followup queued signal source.** The spec accepts `hasQueuedFollowup` as the source. The current `useSessionTranscript` does not expose a "first new part since submit" signal; that would need a small `submittedAt` ref tracked by the caller (the data source). For first release the caller passes `hasQueuedFollowup={followupIsPending || (turns last id hasn't advanced since submit)}` — keeping the consumer-side derivation simple and avoiding a data-source contract change. Revisit if the derived signal turns out to flicker.
- **Future Compact/Reset disabled reasons.** Today only "session active / not finished" disables the buttons. If the data layer later gains a real "prerequisite missing" or "status pending" trigger (not invented ones), the structured tooltip can be parameterised by adding a closed-set `data-disabled-reason` attribute or by extending the existing `data-active` semantics. That is deferred to the change that introduces the trigger.
