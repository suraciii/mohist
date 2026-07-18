## Context

This change mounts the navigation surface that #427 deliberately deferred and #428 proved the click-to-jump pattern for. The current state, established by #427/#428:

- The transcript is a flat single-column timeline. Each turn (`TurnItem` in `widgets/session-transcript/ui/TurnList.tsx`) carries `data-turn-id` / `data-turn-ref` and a `TurnDivider` with `data-turn-index`. The `turnRefs: Map<number, HTMLDivElement>` (1-based turn index) backs `useTurnKeyboardNav` (j/k/g/G).
- Each tool row (`ToolRowView` in `ui/tool-views/index.tsx`) carries `data-tool-call-id`, `data-tool-state`, `data-tone`. Consecutive exploratory calls collapse into a `ContextGroupView` (`data-testid="context-group-row"`) whose inner `ToolRowView`s only mount when `expanded` (local `useState`).
- The session-level error evidence (`SessionErrorsEvidence` in `pages/session/ui/SessionDetailShell.tsx:49–103`) renders `data-testid="session-errors-region"` with `data-tool-error-count` from `meta.eventSummary.toolErrorCount` — display only, no activation.
- The canonical click-to-jump pattern lives in `CurrentActivityBar.tsx:14–20`: `CSS.escape(id)` + `container.querySelector('[data-tool-call-id="…"]')` + `row.scrollIntoView({ block: 'center' })`. The active-tool selector `select-active-tool-call.ts` already shows the shape that walks turns *and descends into `context-group` parts*.
- `SessionTranscriptLayout` owns one ticking `useNow({ intervalMs, 1000, enabled: isRunning, now })` clock (the time-injection seam established by #428) and threads it only to in-progress rows.
- `TurnList.tsx:26` applies `contentVisibility: 'auto'` to the whole `role="log"` container. The spec for this change pushes that down to per-turn granularity.
- `SessionDetailShell` exposes a `siblingSidebar` slot rendered as an `xl:flex-row` sibling of the transcript column. That slot is already occupied by sibling-session navigation on issue-bound sessions (`useIssueSessionDataSource.tsx:322`), so it cannot host the mini-timeline without collision.

The four product behaviors and normative requirements live in `proposal.md` and `specs/{transcript-mini-timeline,transcript-error-jump,transcript-jump-highlight,transcript-render-performance}/spec.md`. The change is purely presentational: it consumes `displayTurns` and the existing anchors; no data model, event protocol, or liveness-gate change.

Stakeholders: readers returning to long finished sessions (primary), reviewers watching running sessions who need to jump to a failure (secondary).

## Goals / Non-Goals

**Goals:**

- Render a mini timeline that plots turns plus three event kinds (failed / file-change / exploratory read), with single-turn long sessions deriving nodes from events.
- Make the session-errors region activatable to jump to the first `[data-tool-state="failed"]` row, with next-error iteration in document order.
- Apply a transient, dismissable, assistive-tech-decorative highlight to any located row, shared across launchers.
- Locate a target inside a collapsed context group by expanding the group first.
- Move `content-visibility: auto` from the whole transcript container to per-turn granularity.
- Keep every new behavior client-side and derived from `displayTurns` / `meta.eventSummary` already on `SessionDataSourceResult`.

**Non-Goals:**

- No transcript full-text search, no cross-session navigation or comparison (issue Non-Goals).
- No redesign of the narrow-screen turn-directory interaction (issue Non-Goals).
- No data model, event protocol, liveness-gate, collection-pipeline, or `SessionDataSourceResult` contract change.
- No live/running-aware behavior in the mini timeline beyond what `displayTurns` already carries; the mini timeline does not tick (it consumes the projected row states, not `useNow`).
- No mobile placement of the mini timeline (only scroll + keyboard nav below `xl`); dogfooding may revisit.

## Decisions

### D1: Mini-timeline is a sticky left rail inside `SessionTranscriptLayout`, `xl+` only

`SessionTranscriptLayout` becomes a flex row at `xl` (`flex xl:flex-row`): a sticky `position: sticky; top: <header offset>` left rail (≈48–64px) + the existing full-width transcript column (`flex-1`). Below `xl` the rail is not rendered. The rail's height maps to the transcript's scrollable height so vertical position ≈ scroll position; each node is a single dot (colored by kind) at the relative offset of its target row.

**Rationale.** Avoids collision with the right-side `siblingSidebar` (sibling-session nav, occupied on issue-bound sessions). Co-locates the navigation with the transcript it navigates. A vertical rail scales naturally to hundreds of events where a horizontal bar would compress them to sub-pixel width. `xl+`-only matches the existing `xl:flex-row` shell without adding a new breakpoint.

**Alternatives considered:**

- *Horizontal bar at the top of the scroll content.* Simpler (no flex surgery) and works at all breakpoints, but a 100-event session at 1px per event is unreadable; rejected for long sessions.
- *Reuse `SessionDetailShell.siblingSidebar`.* Rejected — already occupied by sibling-session nav on issue sessions; stacking two rails in one slot collides.
- *Floating mini-rail pinned to the right edge of the transcript viewport (overlay).* Visually clashes with `JumpToBottomButton` and `CurrentActivityBar` (both already pinned to the transcript viewport); rejected.

### D2: Event-node projection is a pure function over `displayTurns`

A new `model/timeline-nodes.ts` exports `projectSessionToTimelineNodes(turns: DisplayTurn[]): TimelineNode[]`. A `TimelineNode` is `{ kind: 'turn'|'failed'|'file-change'|'read-explore'; toolCallId?: string; turnId: string; turnIndex: number }`. The walk mirrors `select-active-tool-call.ts`: it iterates turns in order, descends into `context-group.parts.tools[]`, and emits:

- one `turn` node per turn boundary (anchored to `data-turn-id`);
- a `failed` node for each tool with `status === 'failed'` (whether standalone or inside a group), anchored to its `data-tool-call-id`;
- a `file-change` node for each completed tool whose `changedFiles` is non-empty OR whose verb family is `edit` (reusing `deriveVerbFamily` from `ui/tool-views/shared.tsx`);
- a `read-explore` node for each completed tool whose normalized name is in the context-tool set (`CONTEXT_TOOL_NAMES` in `session-transcript-display.ts`, exported for reuse).

Single-turn sessions produce event nodes from that one turn; the "no `turn` collapse" rule in `transcript-mini-timeline/spec.md` is satisfied because event nodes are emitted independently.

**Rationale.** Pure function → unit-testable without React. Reuses the projection's verb-family and context-tool logic; no new classification taxonomy. Keeps the React component a thin renderer over a typed list.

**Alternatives considered:**

- *Compute nodes inline in the component with `useMemo`.* Same logic, harder to unit test. Rejected.
- *Extend `DisplayTurn`/`DisplayToolPart` with a `timelineKind` field.* Rejected — violates the "no data model change" non-goal; classification is a timeline-view concern, not a domain concern.

### D3: One `useTranscriptLocate` hook + two lightweight registries power every jump

A new `model/use-transcript-locate.ts` exposes `useTranscriptLocate({ scrollContainerRef })` returning `{ locate(target) }` where `target` is `{ toolCallId?: string; turnId?: string; groupId?: string }`. The hook owns two registries (plain `Map`s, the same shape as `turnRefs`):

- `expansionRegistry: Map<string, () => void>` — keyed by context-group id; calling the entry expands that group.
- `highlightRegistry: Map<string, (on: boolean) => void>` — keyed by row anchor id (`toolCallId` for tool rows, `turnId` for turn dividers); calling it toggles the row's highlight.

`locate(target)` does, in order:

1. If `target.groupId` is set and present in `expansionRegistry`, call its expander (this sets the group's expanded state in React).
2. `requestAnimationFrame(() => { … })` so React commits the expansion before we query.
3. Inside the rAF: `container.querySelector(anchorSelector)` using `CSS.escape` (matches the `CurrentActivityBar` pattern). If null, return silently (spec: "no exception, no scroll").
4. `row.scrollIntoView({ block: 'center' })` (matches #428's choice).
5. Call `highlightRegistry.get(target.toolCallId ?? target.turnId)?.(true)`; the row's own highlight effect starts its dismiss timer.

`ContextGroupView` and `TurnItem`/`ToolRowView` register into the appropriate registry on mount and unregister on unmount, the same way `TurnItem` registers into `turnRefs` today.

**Rationale.** Single locate path consumed by the mini timeline, the error bar, and `next-error`. Reuses the proven `CSS.escape + querySelector + scrollIntoView` pattern. Keeps each row authoritative for its own expansion and highlight state — no foreign state reaches into a row's internals.

**Alternatives considered:**

- *Controlled expansion context (`ExpansionContext`).* Would lift expand state out of `ContextGroupView`/`ToolRowView` into a parent. Clean, but a large refactor of two components whose local expand state is currently fine for their primary use case. Deferred; revisit if more cross-row state appears.
- *DOM custom-event dispatch (`container.dispatchEvent('transcript-locate', …)`).* Lighter, but adds hidden coupling between launcher and listener; harder to unit-test. Rejected.
- *Locate by walking the DOM and clicking the group's expand button.* Brittle (depends on button markup). Rejected.

### D4: Highlight is a row-local effect with a time-injected auto-dismiss

`ToolRowView` and `TurnItem` each render an overlay (or apply a `data-highlight="on"` attribute + CSS class) when their `highlightOn` registry entry is set to true. The same component owns the auto-dismiss timer via `useNow({ intervalMs: HIGHLIGHT_MS, enabled: isHighlighted, now })`-style seeding: when `highlightOn(true)` is called, the component sets a state and schedules a `setTimeout` (or equivalent) that calls `highlightOn(false)`. Tests inject the timer via `vi.useFakeTimers` and advance by `HIGHLIGHT_MS` — never wall-clock (per `design/testing.md`).

Dismissal inputs (Escape, pointer-down outside the row, a new locate to a different row) all funnel through `highlightOn(false)` on the previously highlighted row. At most one row is highlighted at a time because `locate` clears the previous target before highlighting the new one (the hook remembers `lastHighlightedId`).

The highlight is `aria-hidden="true"` and is NOT announced via `aria-live`. The row's role/name are unchanged.

**Rationale.** Row-local state keeps the highlight co-located with the thing being highlighted; the registry is only a side-channel for "turn it on / turn it off". Reuses the #428 time-injection seam.

**Alternatives considered:**

- *Single layout-level `highlightedId` state prop-drilled to every row.* Re-renders every row on each toggle; rejected for the same reason #428 rejected per-row timers.
- *CSS-only animation on a class set by the locate helper.* No way to auto-dismiss from CSS without a timer anyway; the helper would still need to clear the class.

### D5: `SessionErrorsEvidence` becomes activatable; next-error cycles the projected failed list

`SessionErrorsEvidence` gains an activatable control. The component is currently a pure presentational div in `SessionDetailShell.tsx`; the click/keyboard handler is added there. Activation calls `locate({ toolCallId: firstFailedId })` via the `useTranscriptLocate` hook plumbed through `SessionTranscriptLayout` (or, equivalently, exposed by a thin context). The first-failed id and the ordered list of failed ids are derived from a new pure selector `selectFailedToolCalls(turns: DisplayTurn[]): DisplayToolPart[]` (mirroring `select-active-tool-call.ts`'s shape, descending into context groups).

When `failed.length > 1`, the region renders a "Next error" affordance (`data-testid="session-errors-region-next-error"`). The shell keeps a `lastTargetedFailureId` ref; activating next-error finds the next id in `failed` after `lastTargetedFailureId` (wrapping at the end), calls `locate({ toolCallId: nextId, groupId: containingGroupId })`, and updates the ref.

The existing count/category/reason display is unchanged. The region remains gated on the same `statusKind === 'failed' || failureCategory || toolErrorCount > 0` condition.

**Rationale.** Mirrors the proven `selectActiveToolCall` shape; keeps the error bar purely a launcher, not a stateful navigator. Wrap-around matches reader expectation ("there are 3 errors; show me them in order").

**Alternatives considered:**

- *Lift `lastTargetedFailureId` into a parent store.* Rejected — the bar is the only consumer; a ref is enough.
- *Iterate via DOM order (`querySelectorAll('[data-tool-state="failed"]')`).* Couples iteration to render order; the projection already has the authoritative order. Rejected.

### D6: Lazy-render granularity moves from container to per-turn

`TurnList.tsx:26` drops `style={{ contentVisibility: 'auto' }}` from the `role="log"` container. Each `TurnItem` root div gains `style={{ contentVisibility: 'auto' }}`. No change to `ToolRowView`, `ContextGroupView`, or row expansion. `content-visibility: auto` keeps the element (and its descendants) in the DOM and addressable by `querySelector`; it only skips paint/layout for off-screen elements, so the locate flow (D3) still resolves anchors in off-screen turns, and `scrollIntoView` triggers layout on demand.

**Rationale.** Per-turn is the coarsest granularity that still lets a 100-turn transcript skip painting the off-screen turns. Per-tool-row would be finer but adds no marginal win (a turn already skips its rows when off-screen) and would require touching every row component. Per-turn matches the natural navigation unit (mini-timeline turn nodes, j/k keyboard nav).

**Alternatives considered:**

- *Full virtualization (windowing) of tool rows.* Heavy; would conflict with the existing `turnRefs` map and `useTurnKeyboardNav`, and breaks `querySelector`-based anchors for off-screen rows. Rejected as disproportionate for the AC ("10k+ pixel transcript smooth").
- *Per-tool-row `content-visibility`.* Finer-grained but more elements to maintain; no measurable benefit over per-turn for this issue's AC. Rejected.

### D7: Tests assert structure, not timing or visual smoothness

Per `design/testing.md`, no `elapsed < N` and no wall-clock waits. Tests will:

- Unit-test `projectSessionToTimelineNodes` and `selectFailedToolCalls` as pure functions.
- Spec the mini timeline rendering and activation via `customRender` (`tests/test-utils.tsx`) + a mocked `Element.prototype.scrollIntoView` (same pattern as `CurrentActivityBar.spec.tsx:60–67`).
- Spec the locate-and-highlight flow with `vi.useFakeTimers`: assert the row carries `data-highlight="on"` immediately, and is cleared after advancing the clock by `HIGHLIGHT_MS`.
- Spec lazy-rendering structurally: assert the `role="log"` container no longer carries `contentVisibility`, and that each `TurnItem` does; assert that an off-screen turn's `data-tool-call-id` / `data-turn-id` anchors are still resolvable via `querySelector`.

**Rationale.** Mirrors #428's verification plan and the project's hard rule against wall-clock timing assertions.

## Risks / Trade-offs

- **[`content-visibility: auto` per-turn can cause scroll-jump when an off-screen turn's intrinsic size changes on first paint]** -> Mitigation: each `TurnItem` declares `contain-intrinsic-size` (a small min-height based on turn-divider height) so the scrollbar is stable; covered by a browser-track check, not a jsdom assertion.
- **[Locate resolves a target inside an off-screen, content-visibility-hidden turn]** `scrollIntoView` triggers layout on demand, so this is expected to work, but jsdom cannot exercise it. -> Mitigation: spec the locate flow with the turn already on-screen (jsdom limit); add a browser-track check for the off-screen case.
- **[Mini timeline rail consumes horizontal space at `xl`]** narrows the reading column by ~48–64px. -> Mitigation: only render at `xl+` where the viewport has slack; the rail is sticky (no scroll cost) and hidden below `xl`.
- **[Group-expansion-on-locate adds a registry to `ContextGroupView`]** small surface increase on a previously stateless-local component. -> Mitigation: the registry is a side-channel (register/unregister only); local expand state stays the source of truth.
- **[Next-error wrap-around could surprise readers expecting "no more errors"]** -> Mitigation: keep the count visible in the bar (already required by spec) so the reader knows how many failures exist; wrap is the conventional behavior of "next match" controls.
- **[Highlight timer adds another per-row `setTimeout`]** could create N timers if the user spams jumps. -> Mitigation: at most one row is highlighted at a time (D4), so at most one dismiss timer is live at any moment; previous timer is cleared on `highlightOn(false)`.

## Migration Plan

**Deployment:**

- Single PR, frontend only. No server / runner / CLI / protocol changes, no migrations, no feature flags.
- The new behaviors are active on every session detail page (issue-bound and generic); no gating beyond what already exists.

**Rollback:**

- Revert the PR. The transcript falls back to #427's flat timeline with no visible navigation and the container-level `content-visibility: auto`. The session-errors region reverts to display-only. No data or state to clean up.

**Verification:**

- Unit (src-collocated `*.test.ts(x)`): `projectSessionToTimelineNodes` (turn/failed/file-change/read-explore classification, single-turn derivation, descent into context groups); `selectFailedToolCalls` (document order, context-group descent); `useTranscriptLocate` (locate order: expand → rAF → querySelector → scrollIntoView → highlight; no-op when target absent).
- Spec (`tests/` dir, `*.spec.tsx`): mini-timeline rendering and activation (multi-turn, single-turn, descent into collapsed group); error-bar activation + next-error iteration + wrap-around; highlight apply/auto-dismiss/dismissal; lazy-render structural assertions.
- All time-dependent assertions use `vi.useFakeTimers` and fixed timestamps; never `elapsed < N` or wall-clock waits.

## Open Questions

- **Mini-timeline rail vs. horizontal bar.** Default per D1: left rail at `xl+`. If dogfooding shows the rail steals too much reading width or feels disconnected on shorter sessions, revisit a horizontal bar that switches to a rail only above some turn-count threshold.
- **Highlight duration.** Default 1500ms (matches the typical "I see it" latency). Final value deferred to implementation; exposed as a single named constant so it can be tuned without spec churn.
- **`scrollIntoView` block argument for locate.** #428 used `center` for the current-activity bar (a "jump to activity" semantic). For error-jump and mini-timeline nav, `nearest` may be less disorienting when the row is already partially visible. Default: keep `center` for consistency with #428; revisit if dogfooding flags it.
- **Mini timeline + running sessions.** Should nodes update live as `displayTurns` evolves during a running session (e.g. a new failed-row appears), or should the timeline be a snapshot at session-end? Default: live (it consumes `displayTurns` reactively, like the rest of the transcript); confirm this does not introduce scroll/disorientation during streaming.
- **Turn-node marker shape.** A simple dot per turn is enough to satisfy the spec, but a small ordinal label (1, 2, 3…) may help orientation on long sessions. Default: dot only; defer labels unless dogfooding asks.
