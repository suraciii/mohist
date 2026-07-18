# Self Review Report

## Result: PASS

## Methodology

Reviewed `proposal.md`, `design.md`, `tasks.json`, and all four `specs/*/spec.md` files against issue #428's user voice, product shape, acceptance criteria, and non-goals. Cross-validated spec anchors against headings (programmatic), dependency DAG validity (programmatic), and AC-to-spec-to-task traceability (manual). Verified design claims against the live codebase (`packages/web/src/widgets/session-transcript/`, `packages/web/src/pages/session/`).

## Traceability

Every issue Acceptance Criterion maps to spec requirements and tasks:

| Issue AC | Spec | Task |
|---|---|---|
| 活跃期间任意滚动位置可见当前活动+耗时 | `transcript-current-activity-bar`: "A persistent current-activity bar renders...", "Bar remains visible across scroll positions" | T-002 |
| 运行中耗时每秒更新，完成后定格 | `transcript-live-tool-duration`: "Running row shows a ticking duration", "Completed row freezes at the finalized duration" | T-001 |
| 点击当前活动条跳转到工具调用 | `transcript-current-activity-bar`: "Clicking the bar scrolls to the active tool row", "Activation targets the stable tool-call identity" | T-002 |
| 流式文本有光标指示 | `transcript-streaming-cursor`: "Streaming text part shows a block cursor in a live session" | T-003 |
| session 结束后所有指示消失，耗时停跳 | All four specs' liveness-gating requirements + removal-on-session-end scenarios | T-001…T-004 |

All four product-shape bullets map 1:1 to the four capability specs. All four non-goals (no notifications, no multi-session, no tool-form redo, no liveness-gate fix) are respected — the plan explicitly states "No change to data flow or state derivation" and "consumes the #426 liveness gate without re-deriving".

## Validation Results

- **Spec anchors**: all four task `spec` references resolve verbatim to `### Requirement:` headings (programmatic check).
- **DAG**: T-001→{T-002,T-004}, T-003 standalone. No cycles. All `dependsOn` point to existing tasks with strictly lower priority numbers (programmatic check).
- **Spec format**: all requirements use `### Requirement:` / `#### Scenario:` with SHALL/MUST. Every requirement has ≥1 scenario. Scenarios use exactly 4 hashtags. No `## ADDED/MODIFIED/REMOVED` headers. No cross-spec references. Self-contained.
- **Task split**: one task per capability (vertical slice). Shared clock infrastructure (`useNow`, `formatElapsedNow`) coupled with its first consumer (T-001), not split out as a standalone technical step. No standalone test tasks — tests embedded in each task's acceptance criteria. T-003 correctly identified as independent of T-001 (CSS `animate-pulse`, no tick dependency).

## Blocking Items

_None._

## Observations (non-blocking, for implementer awareness)

### OBS-1: D7/T-004 thinking-start capture uses `Date.now()` directly

**Severity:** low
**Scope:** testability consistency

Design D7 and task T-004 specify capturing the thinking-start timestamp via `Date.now()` on the `isThinking` false→true transition, while the tick display uses `now` from the `useNow` hook. In production these read the same wall clock and are consistent. In tests, T-004's acceptance criteria mandate `vi.useFakeTimers`, which controls `Date.now()` globally — so the capture is deterministic under the prescribed test approach.

However, the `useNow` hook (D1) also supports direct `now` prop injection as an alternative to fake timers. If an implementer writes a T-004 test that injects `now` without also faking timers, the thinking-start capture (`Date.now()`) would use real time while the tick uses injected time — producing inconsistent values.

**Recommendation:** the implementer may prefer to capture the thinking-start from the `now` value returned by `useNow` (when available) rather than calling `Date.now()` directly, so both the start and the tick share the same injectable clock. This is a refinement, not a blocker — the prescribed `vi.useFakeTimers` test approach already eliminates the inconsistency.

### OBS-2: Streaming-cursor spec omits "blinking" language

**Severity:** very low
**Scope:** spec completeness

The proposal says "a blinking block cursor" and the design (D6) specifies "Blinking is via Tailwind's existing `animate-pulse`". The spec (`transcript-streaming-cursor`) says "a block cursor SHALL be rendered" without mentioning blink animation. Blinking is a visual/implementation detail that the design correctly owns, so this is not a spec gap — but an implementer reading only the spec might render a static cursor. The task T-003 description and design D6 both specify `animate-pulse`, so the implementer has the guidance.

### OBS-3: `ThinkingPlaceholder` vs `TranscriptEmptyState` scoping

**Severity:** very low
**Scope:** scope boundary

The existing `ThinkingPlaceholder` only renders when `isRunning && isThinking && turns.length > 0`. When `turns.length === 0` and the session is running, `TranscriptEmptyState` renders instead ("Waiting for activity..."). T-004 modifies only `ThinkingPlaceholder`, so the elapsed timer does not appear in the initial pre-turn waiting state. This is a deliberate scope choice (the "Waiting for activity..." state is the session-start state, while the thinking indicator is a mid-session state) and matches the existing component structure. Noting for implementer awareness — if the issue intends the timer to also cover the initial waiting state, T-004's scope would need to extend to `TranscriptEmptyState`.

### OBS-4: Soft overlap with #426's `transcript-activity-indicators`

**Severity:** info
**Scope:** capability boundary

T-003 replaces the streaming dot glyph (whose liveness gating is owned by #426's `transcript-activity-indicators` capability) with a block cursor. The new `transcript-streaming-cursor` spec describes the visual form and its gating; #426's spec describes the gating contract generically ("The streaming glyph rendered on an assistant text part SHALL appear only while..."). Together they fully specify the behavior without conflict — #426 owns the gate, this issue owns the visual form. No action needed; noting the boundary for traceability.

## Summary

The plan is internally consistent, covers all issue acceptance criteria, respects all non-goals, and follows repo conventions (spec-first structure, testing.md time-injection rules, task-splitting principles). The design decisions are well-justified with alternatives, risks have mitigations, and the migration plan is a clean single-PR frontend rollout. All four observations are non-blocking refinements or scope-boundary notes that the implementer can handle during execution without ambiguity. The plan is ready to build.

<promise>PASS</promise>
