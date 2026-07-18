# Review Report — Issue #428

## Result: PASS

## Methodology

Reviewed the four changed product files (`use-now.ts`, `format-duration.ts` additions, `select-active-tool-call.ts`, `CurrentActivityBar.tsx`, `SessionTranscriptLayout.tsx`, `TurnList.tsx`, `AssistantParts.tsx`, `tool-views/index.tsx`) and their tests (`use-now.test.tsx`, `format-duration.test.ts`, `select-active-tool-call.test.ts`, `CurrentActivityBar.spec.tsx`, `tool-row-view-live-duration.spec.tsx`, `SessionTranscriptStreamingCursor.spec.tsx`, `SessionTranscriptThinkingElapsed.spec.tsx`, plus the migrated `SessionTranscriptActivityIndicators.spec.tsx`) against:

- Issue #428 acceptance criteria (5 boxes).
- All four `specs/*/spec.md` normative requirements and scenarios.
- `proposal.md`, `design.md` (D1–D7), `tasks.json` (T-001…T-004).

Verification runs:

- `npm run typecheck -w packages/web` — clean.
- `npm run test:run -w packages/web` — 346 files / 4818 tests pass, 9.53s.
- File-size ratchets (`design/testing.md`): every new unit file < 300 LOC; every new spec file < 800 LOC. All under budget.
- No `while (now<deadline)` / `elapsed < N` polling assertions; fake timers are scoped per-file with `afterEach` restore.

## Traceability — Issue AC → spec → implementation

| Issue AC | Spec requirement | Implementation | Status |
|---|---|---|---|
| 活跃期间任意滚动位置可见当前活动+耗时 | `transcript-current-activity-bar`: "A persistent current-activity bar renders…", "Bar remains visible across scroll positions" | `CurrentActivityBar.tsx` with `className="sticky bottom-0 z-10 …"`; mounted as last child of the scroll content in `SessionTranscriptLayout.tsx:111-117` | ✓ (sticky classes asserted in jsdom; true sticky deferred to browser track per design D5) |
| 运行中耗时每秒更新，完成后定格 | `transcript-live-tool-duration`: "Running row shows a ticking duration", "Completed/Failed/Cancelled row freezes at the finalized duration", "Live duration ticking is gated on session liveness" | `ToolRowView` `liveDuration`/`finalizedDuration` resolution in `tool-views/index.tsx:130-134`; `useNow({ intervalMs: 1000, enabled: isRunning })` in `SessionTranscriptLayout.tsx:73` | ✓ |
| 点击当前活动条跳转 | `transcript-current-activity-bar`: "Clicking the bar scrolls to the active tool row", "Activation targets the stable tool-call identity" | `handleJump` in `CurrentActivityBar.tsx:14-20` queries `[data-tool-call-id="<CSS.escape-id>"]` and calls `scrollIntoView({ block: 'center' })` | ✓ |
| 流式文本光标 | `transcript-streaming-cursor`: "Streaming text part shows a block cursor in a live session", "Block cursor is decorative and hidden from assistive technology" | `AssistantTextPartView` cursor span in `AssistantParts.tsx:33-40`, `aria-hidden="true"`, no role/tabindex | ✓ |
| session 结束后所有指示消失，耗时停跳 | All four specs' liveness-gating + removal-on-end scenarios | Layout gates every consumer on `isRunning`; `useNow` tears down its interval when `enabled` flips false; selector returns `null` when session not running | ✓ |

All four capabilities are exercised by spec tests with deterministic fake time. No wall-clock waits.

## Validation Results

- **Time-injection rule (`design/testing.md` §2).** Every new tick path is injectable (`useNow({ now })` for tests, `vi.useFakeTimers()` for the auto-ticking path). `SessionTranscriptLayout` exposes a `now?: number` prop, matching the `pages/activity/ui/ActivityPage.tsx:133-161` precedent cited in D1.
- **Liveness gate (`design/architecture.md`).** All four new behaviors are gated on the existing `isRunning` flag; no re-derivation of session liveness, no new data-source fields. `selectActiveToolCall` is gated on `isRunning` at the call site (`SessionTranscriptLayout.tsx:75`), so the bar disappears on session end even if a tool part is still marked `running` in the stale data.
- **Anchor reuse (`design.md` D5).** Click-to-jump consumes #427's existing `data-tool-call-id` row anchor; no parallel ref registry introduced. `CSS.escape` is used because `toolCallId` is server-supplied and may contain `.`, `:`, `[` (`CurrentActivityBar.tsx:17`).
- **Stable formatting.** `formatElapsedNow` shares `formatDuration` with `formatElapsed`, so live and finalized durations use identical tier boundaries (sub-second `ms`, `4.7s`, `2m 03s`, `1h 05m`). Verified by `tool-row-view-live-duration.spec.tsx` and `SessionTranscriptThinkingElapsed.spec.tsx` at seconds and minutes tiers.
- **`enabled` flag addition.** `useNow` adds an `enabled` parameter not named in D1; this is necessary to satisfy D1's invariant ("the interval only runs while isRunning is true") and is documented in `progress.txt:34-35`. Behavior is consistent: injected `now` always wins over `enabled` (test `'still respects an injected now value when enabled is false'`).
- **Memoization of terminal rows.** Only `pending`/`running` rows receive `now` as a prop (`AssistantParts.tsx:138`, `tool-views/index.tsx:404`), so terminal rows render `liveDuration=null` and fall through to the finalized delta — see Finding OBS-1 below for the one divergence from the design's stated rationale.

## Findings

### Blocking

None. All five issue acceptance criteria are satisfied, all four spec capabilities are implemented and tested with deterministic fake time, and `typecheck` + `test:run` are clean.

### Non-blocking observations (a separate task may address these)

#### OBS-1: Design D2 / `progress.txt` cite `React.memo(ToolRowView)`, but no such memo exists

**Where:** `design.md:50` ("`React.memo(ToolRowView)` remains effective for them"), `progress.txt:22` ("terminal rows are passed `undefined` so `React.memo(ToolRowView)` stays effective (D2)"), `tasks.json:25` notes ("`React.memo` on `ToolRowView` keeps terminal rows out of the per-second diff").

**What is wrong.** `grep -rn "React.memo\|memo(" packages/web/src/widgets/session-transcript/ packages/web/src/pages/session/` returns no matches. `ToolRowView` is a plain function component (`packages/web/src/widgets/session-transcript/ui/tool-views/index.tsx:120`). The D2 perf rationale — "keeps the once-per-second reconciliation cost proportional to the number of in-progress tools, not to the transcript length" — therefore does not materialize: every row re-renders on every tick because `AssistantParts` (which consumes `now`) re-renders, and `ToolRowView` is not memoized.

**Functional impact.** None. Terminal rows still render a frozen duration because they receive `now: undefined` and resolve `duration = liveDuration ?? finalizedDuration = finalizedDuration`. The `data-duration-mode="frozen"` assertion in `tool-row-view-live-duration.spec.tsx:299-302` confirms the value is stable across ticks. The issue is purely the missing optimization that the design uses to justify D2.

**Recommendation.** Either (a) wrap `ToolRowView` in `React.memo` so the design's claim becomes true, or (b) drop the `React.memo` claim from `design.md` D2, `progress.txt`, and `tasks.json` notes so the artifacts don't assert an optimization that isn't there. The `now: undefined` pass-down already isolates terminal rows' outputs; memoization is a perf refinement, not a correctness gap.

#### OBS-2: `CurrentActivityBar` has a redundant `onKeyDown` that may double-fire activation in real browsers

**Where:** `packages/web/src/widgets/session-transcript/ui/CurrentActivityBar.tsx:22-27, 42-43`.

**What is wrong.** The bar renders a Base UI `<Button>` (`@base-ui/react/button`), which renders a native `<button>`. Native `<button>` elements already synthesize a `click` event on Enter (keydown) and Space (keyup) activation, so `onClick={handleJump}` is sufficient for keyboard accessibility. The added `onKeyDown` handler calls `event.preventDefault()` and `handleJump()` for both Enter and Space on top of the native behavior. Depending on the browser, this can dispatch `handleJump` twice for Enter (once from the manual handler, once from the synthesized `click`). The existing tests (`CurrentActivityBar.spec.tsx:151-191`) only fire `fireEvent.keyDown(...)` and never simulate the subsequent native `click`, so they cannot detect the duplication.

**Functional impact.** Idempotent: `scrollIntoView({ block: 'center' })` called twice with the same target produces the same end state as once. No visible bug. The issue is dead/redundant code that the next reader has to reason about.

**Recommendation.** Remove the `onKeyDown` handler entirely; rely on the native button's keyboard activation through `onClick`. If keyboard scroll-into-view needs to fire on keydown rather than keyup, use a `div`/`span` with `role="button" tabIndex={0}` instead of a native `<button>`.

#### OBS-3: Thinking elapsed display is structurally absent in the pre-turn waiting state

**Where:** `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx:107` (gate `turns.length > 0`).

**What is wrong.** `ThinkingPlaceholder` is rendered only when `turns.length > 0`. When the session is freshly started but no turn has landed yet, `TranscriptEmptyState` ("Waiting for activity...") renders instead and the elapsed timer is unavailable — even though the session is alive and `isThinking` may be true. The `transcript-thinking-elapsed` spec says "While the hosting session is alive (running) and in the thinking state, the thinking indicator SHALL display the elapsed time" without excluding the empty-transcript case.

**Mitigations already in place.** The `turns.length > 0` gate is pre-existing (carried verbatim from #427 — `git show d6a5f3730:packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx` shows `{isRunning && isThinking && turns.length > 0 && <ThinkingPlaceholder />}` at the pre-change baseline). Self-review OBS-3 flags this explicitly as a deliberate scope choice. The issue body scopes T-004 to the `ThinkingPlaceholder` component ("`ThinkingPlaceholder` elapsed timer"), not to `TranscriptEmptyState`.

**Recommendation.** If the issue intends the elapsed timer to also cover the initial waiting state, file a follow-up that extends the timer to `TranscriptEmptyState`. Otherwise no action — the implementation matches the task's stated scope.

#### OBS-4: `useNow`'s `enabled` flag is silently overridden by an injected `now`

**Where:** `packages/web/src/widgets/session-transcript/model/use-now.ts:9-22`.

**What is wrong.** The hook's return path is `if (now !== undefined) return now; if (!enabled) return undefined; return tick`. An injected `now` therefore bypasses `enabled`. This is intentional and tested (`use-now.test.tsx:151-157` "still respects an injected now value when enabled is false"), but it creates a footgun: a caller that injects `now` while `isRunning=false` would still pass a defined `now` to consumers, and a `running`-status row in stale data would then render a `live` duration even though the session has ended — violating the spec scenario "Already-ended session renders no ticking duration". No current caller does this (`SessionDetailShell` never injects `now`; only tests do), so production behavior is correct.

**Recommendation.** Optional: have `SessionTranscriptLayout` clamp `now` to `undefined` when `!isRunning` regardless of injection, so the liveness gate is defensive at the layout boundary rather than only at the hook boundary. Or document the invariant in `use-now.ts` that callers are responsible for not injecting `now` while the consumer is meant to be disabled.

#### OBS-5: Spec omits "blinking" language for the streaming cursor

**Where:** `openspec/changes/issue-428/specs/transcript-streaming-cursor/spec.md`.

**What is wrong.** The proposal says "a blinking block cursor" and design D6 specifies "Blinking is via Tailwind's existing `animate-pulse`". The spec only says "a block cursor SHALL be rendered" without referencing blink. An implementer reading only the spec could ship a static cursor. This is acknowledged in self-review OBS-2; the implementation does apply `animate-pulse` (`AssistantParts.tsx:38`), so there is no behavioral gap.

**Recommendation.** Optional: add "blinking" to the spec requirement or to one scenario's "THEN" clause so the spec is self-describing without cross-referencing the design.

## Summary

The change ships all four capabilities the issue requires, with deterministic fake-time tests, correct liveness gating, accessible decorative cursor, stable duration formatting shared between live and frozen paths, and proper teardown of the per-second interval on session end. Typecheck and the full web test suite are green. The five observations above are non-blocking refinements (a missing memoization claimed by the design, a redundant keyboard handler on the bar, a pre-existing scope limit on the thinking timer, a defensive-clamping footgun in `useNow`, and a spec wording gap) — none of them block merge, but each is described with enough context for a follow-up fixer task to act on.

<promise>PASS</promise>
