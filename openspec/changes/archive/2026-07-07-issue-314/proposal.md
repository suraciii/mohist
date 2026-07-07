## Why

The `coder-session` timeline hook (`useSessionTimeline.ts`, 832 lines / scc complexity 288) is one cohesive state machine, but ~400 lines of event-reducer logic are trapped inside a single large `useEffect`, where 13+ `onAgentEvent` inline closures can't be read or tested individually. Two of its returned fields (`taskProgress`, `loopProgress`) are dead state — `useState` declared with no setter, never written — that props-drill into `SessionTimeline.tsx` to gate a panel that can never render (the Map is always empty). This is part of the "代码复杂度热点治理" epic (#22); the hook is the next hotspot blocking safe iteration on live session timeline presentation. Follows the already-validated #251 playbook.

## What Changes

- Extract the directly-extractable event reducers (pure `(prev, detail) => next` transitions) into a new React-independent `model/session-timeline-reducer.ts`: `plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, `usage.updated`, `context_health_update`, `compaction_event`, `com.mohist.agent-session.context-compacted`, `com.mohist.agent-session.context-health-updated`.
- Converge `useSessionTimeline.ts` into a thin event-wiring layer: subscribe to `onAgentEvent`, run session-scoping predicates, and dispatch to the extracted reducers.
- Leave the `plan_session_update` → `flushPlanBuffer` batch + rAF/setTimeout 100ms merge + `liveToolCallMapRef` side-effect chain (and the `tool_call.started/updated/completed` events routed through `handleToolEvent` → `flushPlanBuffer`) in the hook, or factor into a ref-injected module function — not forced into a pure reducer. The merge cadence and ref-mutation semantics are invariant.
- **Remove** the dead state `taskProgress` / `loopProgress` from the hook's return and coordinate `SessionTimeline.tsx` (drop the props, the `TaskProgressPanel` render gate, and the `TaskProgressMap` / `LoopProgress` imports). No **BREAKING** change to end users: the panel never rendered, so the change is internal-contract only.
- Add event-wiring integration tests (`dispatchAgentEvent(...)` driven through the hook) *before* migrating code, then add direct unit tests for the extracted pure reducers.
- No change to the hook's remaining return shape, event→state-transition semantics (incl. issue-247's `usage.updated` / `context_health_update` split), the 100ms merge cadence, or the session-scoping predicates.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

_None._ This is a pure internal restructuring. Existing timeline behavior — round reconstruction, live streaming deltas, tool-call tracking, recovery banners, context-health / compaction presentation, plan-progress steps — is preserved bit-for-bit. The `agent-session-ui` / `session-timeline-ui` behavior specs describe user-visible behavior, not implementation layout, and no spec-level requirement changes. All acceptance is structural (file placement, complexity reduction) and behavioral-preservation (unchanged render output / hook output), guarded by tests.

## Impact

- **Code** (`packages/web/src/widgets/coder-session/`):
  - `model/useSessionTimeline.ts` — slimmed; loses ~400 lines of pure reducer logic and the two dead-state `useState` declarations; becomes an event-wiring thin layer.
  - New file: `model/session-timeline-reducer.ts` — pure reducer functions for the directly-extractable events plus the shared types/helpers they own.
  - `ui/SessionTimeline.tsx` — drops `taskProgress` / `loopProgress` props, the `TaskProgressPanel` render gate, and the `TaskProgressMap` / `LoopProgress` imports; render output unchanged.
  - Widget barrel `index.ts` — unchanged; the hook is widget-internal (not exported), and the module's re-exports (`Round`, `RecoveryStatus`, `PlanProgress`, `ContextHealthState`, `deriveToolCallTitle`, `reconstructRoundsFromEvents`) remain available, possibly re-exported through the new reducer module.
- **Tests**: existing `useSessionTimeline.test.ts` (esp. the `usage.updated` / `context_health_update` / `compaction_event` behavioral cases) and `SessionTimeline.test.tsx` are the regression guard. New event-wiring integration tests are added first to cover the previously-uncovered `plan_round_start` / `plan_round_complete` / `coder_recovery_status` / `session.liveness` / compaction / `tool_call.*` wiring, then the extracted reducers gain direct unit tests. `SessionTimeline.test.tsx` drops its removed `taskProgress` / `loopProgress` props.
- **APIs / Dependencies / Systems**: none. No server, runner, or CLI changes; no new dependencies; no SSE/protocol changes.
- **Risk**: medium — the hook drives the primary live session-observation surface, so behavioral regressions are possible but contained by test-first migration and the existing behavioral tests pinning event→state semantics. The `plan_session_update` / `flushPlanBuffer` batch+rAF chain is the highest-risk surface and is explicitly left non-pure to preserve its semantics.
