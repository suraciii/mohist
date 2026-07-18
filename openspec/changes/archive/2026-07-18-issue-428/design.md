## Context

This change adds a real-time sense of progress to the session page. Today the only live signal on a running session is a single streaming dot pinned to the bottom of the transcript (`StreamingIndicator` in `widgets/session-transcript/ui/SessionTranscriptLayout.tsx`); once the reader scrolls away from the bottom they lose all sense of what the agent is doing and how long it has been doing it. Tool rows only ever display a finalized duration — running rows are explicitly gated to "no live timer" today (`{duration && !isRunning && ...}` in `ui/tool-views/index.tsx:294`).

The change is purely presentational and consumes two contracts produced by prior issues:

- **#427** established the flat line-based transcript: each tool call is one row (`ToolRowView`) carrying a stable `data-tool-call-id` and `data-tool-state` attribute, plus a `startedAt`/`completedAt` pair on the `DisplayToolPart`. This change mounts live duration and the current-activity bar on that row.
- **#426** established the session-liveness gate as the authoritative source for when any activity indicator may render. The `isRunning` flag flows from `useSessionTranscript` → `SessionDataSourceResult` → `SessionDetailShell` → `SessionTranscriptLayout` and is consumed here unmodified.

The four product behaviors are scoped in `proposal.md` and normative requirements are in `specs/`. No data model, event protocol, collection pipeline, or liveness-gate logic changes.

Stakeholders: readers of running sessions (primary), reviewers of long agent transcripts (secondary).

## Goals / Non-Goals

**Goals:**

- Render a once-per-second ticking duration on in-progress tool rows (`pending`/`running`) that freezes at the finalized delta on completion/failure/cancellation.
- Render a persistent bottom-of-transcript current-activity bar (title + live duration) that stays visible at every scroll position and jumps to the active tool row on activation.
- Render a block cursor at the end of assistant text that is open or actively streaming.
- Render an elapsed timer on the thinking indicator while the session is alive and in the thinking state.
- Keep every new behavior gated on the existing `isRunning` flag; never re-derive liveness.

**Non-Goals:**

- No change to data flow, event protocol, transcript data model, liveness gate, or collection pipeline (owned by #426 / the data layer).
- No change to the visual form of tool rows beyond the duration slot (owned by #427).
- No desktop notifications, sounds, multi-session views, or page-external alerts.
- No new data-source fields on `SessionDataSourceResult`; thread only what already exists.

## Decisions

### D1: Single layout-level ticking clock, prop-drilled to consumers

A new `useNow({ intervalMs, now }: { intervalMs: number; now?: number })` hook in `widgets/session-transcript/model/use-now.ts` returns a millisecond timestamp. When `now` is `undefined`, it seeds state from `Date.now()` and runs one `setInterval(intervalMs)` that bumps state, cleared on unmount. When `now` is provided (tests), no interval is started and the hook is pure.

`SessionTranscriptLayout` calls `useNow` once and passes the value down via props to `ToolRowView`, `AssistantTextPartView`, `ThinkingPlaceholder`, and the new `CurrentActivityBar`. The interval only runs while `isRunning` is true (effect dependency); when the session ends the interval is torn down and consumers render finalized values.

**Rationale:** one timer drives every consumer — N rows do not start N timers. Prop drilling keeps the surface small and the test seam obvious.

**Alternatives considered:**

- *React context clock.* Avoids prop drilling but adds a provider for only 3–4 leaves; revisit if more consumers appear.
- *Per-row `useEffect` interval.* N timers, harder to test, more GC churn during long sessions. Rejected.

Precedent: `pages/activity/ui/ActivityPage.tsx:133–161` already uses the exact `now?: number` injection pattern; this design reuses that convention rather than inventing a new one.

### D2: Only in-progress rows receive `now`; terminal rows stay memoizable

`TurnList` already uses `contentVisibility: 'auto'` and a long session can have hundreds of rows. Re-rendering every row once per second would be wasteful. The layout passes `now` **only** to rows whose `status` is `pending`/`running`. Terminal rows continue to compute duration through the existing `formatElapsed(startedAt, completedAt)` path and do not receive `now` as a prop, so `React.memo(ToolRowView)` remains effective for them.

**Rationale:** keeps the once-per-second reconciliation cost proportional to the number of in-progress tools (typically one), not to the transcript length.

### D3: Reuse `formatDuration`; add a live-elapsed helper alongside `formatElapsed`

`formatDuration(ms)` and `formatElapsed(startedAt, completedAt)` in `model/format-duration.ts` are widely used and well-tested. They stay unchanged. A new helper `formatElapsedNow(startedAt, nowMs)` (or a small extension that lets the caller pass `completedAt ?? now`) returns `formatDuration(nowMs - startMs)` for the in-progress case. Finalized durations continue to use `formatElapsed`.

**Rationale:** keeps the existing API stable; the new path is unit-testable with fixed `now` and a fixed `startedAt`.

### D4: Active-tool selector is a pure function over `displayTurns`

A new `selectActiveToolCall(turns: DisplayTurn[]): DisplayToolPart | null` walks turns in order and returns the last tool part whose `status` is `pending` or `running`, or `null`. The "last" tiebreak is deterministic; in single-agent sessions there is typically at most one.

**Rationale:** unit-testable without React; the bar consumes the selector's output; no new state.

### D5: Current-activity bar pinned with `position: sticky; bottom: 0`

The bar is rendered inside `SessionTranscriptLayout` as the last child of the scroll content, with `position: sticky; bottom: 0` so it pins to the bottom of the scroll viewport (the nearest scroll ancestor is `SessionDetailShell`'s `scrollContainerRef` div). The bar's visibility conditions:

- render only when `isRunning && activeTool != null`;
- title = active tool's verb-led title (same derivation as the row);
- duration = live elapsed via D3;
- on activation (click or keyboard), query the scroll container for `[data-tool-call-id="<activeToolCallId>"]` and call `scrollIntoView({ block: 'center' })`.

**Rationale:** reuses #427's existing `data-tool-call-id` anchor — no parallel ref registry. Stays inside the transcript widget rather than reaching into `SessionDetailShell`'s layout.

**Alternatives considered:**

- *Fixed bar as sibling of the scroll container in `SessionDetailShell`.* Rejected: would consume layout space below the composer and pull scroll-jump logic up a layer.
- *Ref registry mirroring `turnRefs`.* Rejected: duplicates the anchor contract that `data-tool-call-id` already provides.

### D6: Block cursor replaces the trailing streaming dot glyph

`AssistantTextPartView` currently renders a trailing dot glyph (`assistant-text-streaming-glyph`) when live and the part is open. This change replaces it with a block cursor (an `inline-block` span) appended after the `TranscriptMarkdown` output, rendered when `isRunning && (isIncomplete || isStreaming)`. Blinking is via Tailwind's existing `animate-pulse`; no new CSS keyframes. The cursor is `aria-hidden="true"` and not focusable.

**Rationale:** one in-stream visual, not two. The proposal explicitly states the block cursor "replaces the current trailing dot glyph as the in-stream visual".

### D7: Thinking-elapsed start captured in the layout via a ref

The thinking-state timestamp is captured at the moment `isThinking` transitions from false → true inside `SessionTranscriptLayout` (a `useRef<number | null>` plus an effect on `isThinking`). Reset to `null` when `isThinking` becomes false. The thinking indicator subtracts the captured start from `now` and formats via `formatDuration`.

**Rationale:** the data model does not carry a single "thinking began" field, and the issue explicitly excludes state-derivation changes. A presentation-local timestamp is the minimum that satisfies the spec.

**Alternatives considered:**

- *Use the latest reasoning part's `startedAt`.* Fragile when the thinking state begins before any reasoning part exists (e.g. right after a prompt is sent).
- *Add a `thinkingStartedAt` field to the data source.* Rejected — would violate the non-goal of adding data-source fields.

## Risks / Trade-offs

- **[jsdom has no layout] `position: sticky` cannot be asserted in jsdom.** -> Mitigation: assert the bar's presence/absence conditions and the `position`/`bottom` class strings in jsdom; rely on the existing browser track (`packages/web/tests/browser/`) for true sticky behavior if visual flakiness appears. Do not write `while`/`elapsed` polling assertions.
- **[Once-per-second reconciliation over a long transcript] potential scroll jank.** -> Mitigation: D2 (only in-progress rows receive `now`); `React.memo` on `ToolRowView` keeps terminal rows out of the per-second diff.
- **[Active-tool selector assumes ≤1 in-progress tool] multi-tool parallel sessions would only surface the last.** -> Mitigation: document as a known limitation; the issue scopes to single-agent sessions and multi-session views are a non-goal.
- **[Thinking-elapsed is presentation-local] a page reload mid-think resets the timer to zero.** -> Mitigation: acceptable per the issue's "live progress feel" framing; the spec only requires ticking while live, not persistence across reloads.
- **[Replacing the streaming dot glyph] small visual regression for users accustomed to the dot.** -> Mitigation: the block cursor occupies the same semantic slot and is gated identically; no behavioral change beyond the visual form.

## Migration Plan

**Deployment:**

- Single PR, frontend only. No server / runner / CLI / protocol changes, no migrations, no config flags.
- The new behaviors are active whenever `isRunning` is true on a session detail page; no feature flag is introduced.

**Rollback:**

- Revert the PR. Rows fall back to #427's "no live timer on running rows" behavior; the streaming dot glyph returns; the bar, cursor, and thinking timer disappear. No data or state to clean up.

**Verification:**

- Unit (src-collocated `*.test.ts(x)`): `useNow` (fake timers, fixed `now`), `selectActiveToolCall`, live-elapsed helper.
- Spec (`tests/` dir, `*.spec.tsx`): `SessionTranscriptLayout` rendering and gating for each of the four behaviors; click-to-jump for the current-activity bar via a mocked `scrollIntoView` on the target row.
- All time-dependent assertions use fixed timestamps and `vi.useFakeTimers`; advance by 1000ms to assert ticking; never `elapsed < N` or wall-clock waits (per `design/testing.md`).

## Open Questions

- **Tick rate for the cursor vs. duration.** Current plan: 1000ms for duration; cursor blink via CSS `animate-pulse` (already ~1s) so it consumes no tick. If the cursor feels sluggish, lower the cursor's CSS animation independently rather than raising the tick rate.
- **Visual identity of the current-activity bar.** Should it carry a small animated status dot in addition to title + duration? Default: yes, mirroring the row's `ToolStatusDot` so the eye links the bar to the row. Final visual call deferred to implementation.
- **`scrollIntoView` block argument.** `center` centres the row in the viewport (good for "jump to activity"); `nearest` is less disruptive if the row is already partially visible. Default: `center`; revisit if user testing flags it as jarring.
