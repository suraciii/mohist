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
- Surface a structured disabled-reason tooltip on Compact/Reset via a closed-set `data-disabled-reason` attribute (`active` / `prereq` / `unknown`) rendered through the existing `Tooltip` primitive.
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

Move `formatRelativeTime` from `SessionDetailShell.tsx:451` to a new module `packages/web/src/shared/lib/format-time.ts`. The new helper is `formatSessionTime({ date, statusKind, anchor, now })` returning `{ primary: string; secondary: string }`, where `primary` is what renders inline and `secondary` is what the tooltip shows. Threshold and branches match `session-time-display/spec.md`:

| `statusKind` | `now - anchor` ≥ 1h | primary | secondary |
|---|---|---|---|
| `completed` / `failed` / `stale` | yes | absolute date-time (`Jun 17, 09:52`) | relative (`8h ago`) |
| `completed` / `failed` / `stale` | no | relative (`5m ago`) | absolute |
| `live` / `finalizing` / `probing` | any | relative | absolute |

The "Checking since <relative>" phrasing for `probing` is a separate wrapper that calls the helper and pins the relative form regardless of threshold, so the spec's probing invariant holds. The helper MUST accept `now` as an argument (no implicit `Date.now()`), making `session-time-display/spec.md`'s determinism requirement satisfiable.

**Rationale.** Tests can drive the absolute-vs-relative branch by varying `now` without `vi.useFakeTimers`, matching the project's testing rule ("禁止真实时间"); `formatRelativeTime` is currently untestable. Status-aware branching keeps live sessions readable while fixing the "8h ago" mental arithmetic for finished ones.

**Alternatives considered:**

- *Keep the helper inside `SessionDetailShell.tsx` and just add a `statusKind` parameter.* Rejected — no test surface for a presentational helper that is now policy-bearing; mixing it into the shell makes it harder to reuse from `StickySessionTitle` and any future consumer.
- *Push branching into the call sites.* Rejected — every call site would need to know the threshold and the alt form, scattering the policy.

### D2: Sticky identity strip uses IntersectionObserver against the outer header

The sticky strip's visibility is driven by an `IntersectionObserver` registered on the outer header (`data-testid="session-header"`) with `root: scrollContainerRef.current` and `threshold: 0`. When the header's intersection ratio drops below a small epsilon (≈0.001 — equivalent to "fully scrolled past"), `setEngaged(true)` and the strip transitions from `hidden` to `sticky top-0`. When the header re-enters, `setEngaged(false)` and the strip is removed from layout. A `requestAnimationFrame` flush between the toggle and any potential `scrollTop` measurement ensures the strip never paints in its hidden-then-shown state mid-frame.

The strip itself stays the same component (`StickySessionTitle`), but its visibility is now controlled by a new `engaged` boolean prop (default `true` for callers that don't yet pass the new prop — backward compatibility for the existing widget public API) and its wrapper is `inert` + `aria-hidden="true"` while disengaged. When disengaged, the strip renders `display: none` (not `visibility: hidden`) so it does not contribute height.

**Rationale.** IntersectionObserver is the platform-native primitive for "is X visible inside Y"; it avoids the cost of a scroll listener that would have to fire on every scroll tick. Tying visibility to the actual outer-header element (not a pixel offset threshold) survives any header-height change. `display: none` (vs `visibility: hidden`) is what guarantees the no-layout contribution required by the spec.

**Alternatives considered:**

- *Pixel-offset threshold tied to `scrollContainerRef.scrollTop`.* Simpler but tied to header height; regresses if the header grows; does not account for header resize.
- *CSS-only `position: sticky` with a sentinel element above the strip.* Pure CSS, but then "hidden on first screen" requires JS anyway to suppress the sticky rendering; the IntersectionObserver is the source of truth for that.
- *A second `ResizeObserver` on the header so the threshold updates if the header height changes.* Defer — IntersectionObserver already returns `intersectionRect` whose bottom relative to the root gives us exactly the "scrolled past" signal.

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

### D6: Disabled-reason tooltips go through the existing `Tooltip` primitive

`SessionRecoveryActions` exposes `data-disabled-reason="active" | "prereq" | "unknown"` on every disabled button. The button is then wrapped in the existing `Tooltip` primitive (`packages/web/src/shared/ui/components/tooltip.tsx`), whose content is a small node: a short title line + a longer reason sentence, derived from a reason map keyed by `data-disabled-reason`:

| reason | title | reason sentence |
|---|---|---|
| `active` | "Session is running" | "Finish or cancel the session before compacting or resetting." |
| `prereq` | "Prerequisite not met" | (derived from the missing prerequisite; default "Session is not yet in a state that supports this action.") |
| `unknown` | "Status not confirmed" | "Retry shortly; the session status could not be confirmed." |

The wrapper sets `aria-describedby` when the tooltip is open (matches the existing primitive). The button's native `title` attribute is removed to avoid double tooltips.

**Rationale.** Reuses the project's existing tooltip primitive (no new dependency, no new a11y surface). The closed-set reason attribute is the stable contract spec relies on; the parent renders the human copy from a map. `aria-describedby` is the screen-reader hook the primitive already provides.

**Alternatives considered:**

- *Radix-based tooltip.* Heavier, new dependency; the existing primitive covers the hover/focus + `aria-describedby` contract.
- *Render the reason inline next to the button.* Pollutes the row layout; defeats the "secondary slot" cleanup.

### D7: Sibling nav dedup uses a `matchMedia('(min-width: 1280px)')` check

`SessionHeader` reads `useMediaQuery('(min-width: 1280px)')` (a tiny new hook in `packages/web/src/shared/lib/use-media-query.ts` returning the current match state, with an SSR-safe initial value of `false` and a `matchMedia` listener registered on mount). When the query matches, the header does not render the `siblingNav` slot at all. When it doesn't match, it renders the existing prev/next links with a `data-viewport="narrow"` attribute on the slot wrapper so tests can distinguish the fallback from the removed wide-viewport version.

**Rationale.** `matchMedia('(min-width: 1280px)')` is the same breakpoint the existing shell uses for `xl:flex-row`. The hook is small (one effect, one state, cleanup) and is exactly the shape that other layout branches in the codebase would benefit from later.

**Alternatives considered:**

- *Container queries.* Tailwind 3 supports `@container`, but the design system has not adopted container queries project-wide; introducing one here would be inconsistent.
- *Pass the visibility decision down from `SessionDetailShell` via a prop.* Couples the shell to the header's viewport logic and forces the shell to read `matchMedia`; worse factoring.

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
- [Tooltip on disabled buttons breaks for keyboard-only users if the primitive's focus trigger is removed.] -> Mitigation: the existing `Tooltip` primitive already wraps the child in a `tabIndex={0}` element with `onFocus`/`onBlur` toggling the tooltip; spec ensures the disabled button remains focusable (`aria-disabled` rather than `disabled` where the underlying mutation must still see the click — or `disabled` plus a separate focusable wrapper; spec leaves this as a final-decision note, see Open Questions).
- [`navigator.clipboard.writeText` is not available in jsdom / older browsers.] -> Mitigation: feature-detect in the copy handler; fall back to a hidden `<textarea>` + `document.execCommand('copy')` or to surfacing the full id in a tooltip as a manual-copy fallback. The "transient confirmation" branch is not required when clipboard access fails — instead the tooltip stays open showing the full id so the user can copy manually.
- [Time formatter's `absolute` branch depends on `Intl.DateTimeFormat` which depends on the host locale.] -> Mitigation: pin the absolute format to a stable pattern (`'MMM d, HH:mm'` style, derived via `Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })`) so the rendered string is deterministic in tests and across locales. Tests assert against the formatted string with a frozen locale, never against the raw `Date`.
- [Followup composer's `state` prop can disagree with derived `disabled`/`isSending` if the caller passes all three.] -> Mitigation: `state` always wins when supplied; derived state only applies when `state` is undefined. Documented in the prop's JSDoc and asserted in a test.
- [Removing the inline `·` separators from the header reduces visual scannability for users who relied on them.] -> Mitigation: each item keeps its own chip / badge styling (`rounded-full border` for stage, distinct colors for status), so the row remains scannable without the dividers.

## Migration Plan

**Deployment:**

1. **Time helper extraction** (`D1`): add `packages/web/src/shared/lib/format-time.ts`; move `formatRelativeTime` body into `formatSessionTime`; replace call sites in `SessionDetailShell.tsx`; add `formatSessionTime.test.ts` asserting all six branches and the determinism rule (same inputs → same output; changing `now` can flip the branch).
2. **Header single-row rewrite** (`D3`): rewrite `SessionHeader`'s metadata block; remove `·` dividers; add stable `data-testid` / `data-*` to each item; ensure existing render specs continue to pass (`SessionPage.test.ts`, `SessionPage.sticky.test.tsx`, `SessionPage.cancel.test.tsx`) by migrating any selector assertions to the new `data-testid`s.
3. **Session id copy** (`D4`): add the icon button + clipboard handler; wire it into `SessionHeader`; assert the full id is exposed via `aria-label` and `data-session-id`.
4. **Sticky strip scroll-engaged** (`D2`): wrap `StickySessionTitle` with the IntersectionObserver; pass the resolved `engaged` boolean into the strip; assert the strip is `display: none` + `inert` + `aria-hidden="true"` on first render and after a scroll-back-to-top.
5. **Cancel demotion** (`D5`): move the cancel button into the secondary slot; switch `variant` from `destructive` to `ghost`; assert `variant` and slot location; confirm dialog + mutation unchanged.
6. **Disabled-reason tooltip** (`D6`): extend `SessionRecoveryActions` to expose `data-disabled-reason`; wrap the disabled button with the existing `Tooltip`; assert the three reasons render distinct content; assert native `title` is removed.
7. **Sibling nav dedup** (`D7`): add `useMediaQuery`; remove the wide-viewport `siblingNav` slot; keep the narrow fallback with `data-viewport="narrow"`; assert wide renders no slot, narrow renders the slot, both never render together.
8. **Followup composer three states** (`D8`): extend the composer with the three optional props + the `data-state` attribute; migrate `SessionFollowupComposer.test.tsx` to assert the new states (interactive / queued / closed) and that disabled input + persistent queued indicator match the spec.
9. **Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`** before commit. The change is presentation-only; no server / runner / CLI / protocol changes.

**Rollback:**

- Revert the commits. The header returns to the two-row metadata layout; sticky strip returns to always-visible; Cancel returns to destructive primary; time formatter returns to relative-only; followup composer returns to two states. No data, config, or API changes — pure frontend revert.

**Verification:**

- Unit (`*.test.ts(x)` colocation): `formatSessionTime` (six branches + determinism + threshold flip); `useMediaQuery` (matches the query, updates on `setMatchesForTest`); `SessionRecoveryActions` (reason attribute closed-set, three reason contents); `SessionFollowupComposer` (three states + behavior contracts).
- Spec (`*.spec.ts(x)` colocation): sticky strip first-render / scroll-out / scroll-back-to-top; cancel demotion + slot position; session id copy (clipboard spy, transient confirmation, full-id `aria-label`); sibling dedup (wide/narrow via `setMatchesForTest`); followup queued persistence past `isSending`.
- All time-dependent assertions use `vi.useFakeTimers` or the formatter's injected `now`; never `elapsed < N` or wall-clock waits, per `design/testing.md`.

## Open Questions

- **Disabled button + tooltip focus chain.** The existing `Tooltip` primitive wraps the child in a focusable span. When the child is a `<button disabled>`, the child itself is not focusable, so the span wrapper becomes the focusable proxy. This is fine for screen readers, but `Tab` lands on the span and not the button — does that match expectations? Alternative: use `aria-disabled` on an enabled `<button>` and guard the click in the handler, so the focus chain stays on the button. Default for first release: keep `disabled` + focusable wrapper; revisit if a11y review flags it.
- **Cancel slot visual weight.** Ghost vs outline vs icon-only — keep text inline next to the icon for first release. If dogfooding shows it still reads as "primary", revisit a kebab menu.
- **Sticky strip offset when header is wider than expected.** The strip sits at `top-0` inside the scroll container; the recovery bar (`data-sticky="true"` at `top-9`) overlays it on scroll. Current ordering (`sticky top-0` strip, `sticky top-9` recovery) keeps the strip behind the recovery bar, which is fine. If dogfooding shows the layering is confusing, swap the offsets so the recovery bar is the visible top element once engaged. Default: keep current ordering.
- **Narrow-viewport sibling slot breakpoint.** The hook uses `min-width: 1280px` to match the existing shell's `xl`. If dogfooding on tablets (≤ 1280px) shows the sidebar collapse + no header slot feels barren, revisit at a wider breakpoint or add a thin horizontal sibling bar.
- **Followup queued signal source.** The spec accepts `hasQueuedFollowup` as the source. The current `useSessionTranscript` does not expose a "first new part since submit" signal; that would need a small `submittedAt` ref tracked by the caller (the data source). For first release the caller passes `hasQueuedFollowup={followupIsPending || (turns last id hasn't advanced since submit)}` — keeping the consumer-side derivation simple and avoiding a data-source contract change. Revisit if the derived signal turns out to flicker.
