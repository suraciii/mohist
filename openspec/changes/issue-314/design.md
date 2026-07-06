## Context

`packages/web/src/widgets/coder-session/model/useSessionTimeline.ts` (832 lines, `scc` complexity 288) is the live observation surface for a coder session. Its `useEffect` body wires 13+ `onAgentEvent` subscriptions, of which nine are pure `(prev, detail) => next` transitions trapped inside inline closures, and four — `plan_session_update` + the `flushPlanBuffer` batch/rAF chain and the three `tool_call.*` events routed through `handleToolEvent` — are batch + rAF + ref side-effect logic that cannot be expressed as pure reducers without altering their semantics. Two of the hook's returned fields (`taskProgress`, `loopProgress`) are `useState` declared without setters and are drilled into `SessionTimeline.tsx` to gate a `TaskProgressPanel` render branch whose `tasks.length` is always zero (the Map is never written).

Current consumers of the module's types/helpers:

- `widgets/coder-session/ui/SessionTimeline.tsx` — imports `Round`, `RecoveryEvent`, `RecoveryStatus`, `PlanProgress`, `ContextHealthState`, `deriveToolCallTitle`.
- `widgets/issue-workflow/ui/PlanProgressPanel.tsx` — cross-widget direct import of `type { PlanProgress }` from `../../coder-session/model/useSessionTimeline` (bypasses the widget barrel).
- `widgets/coder-session/index.ts` — does NOT re-export the hook or these symbols today; they are reachable only via the deep path `widgets/coder-session/model/useSessionTimeline`.

Constraints (from spec `session-timeline-events/spec.md`):

- Hook return shape (after dead-state removal), event→state semantics, the 100ms rAF/setTimeout merge cadence, `liveToolCallMapRef` set/get/in-place mutation, and the per-event session-scoping predicates are all invariant.
- Reducer module MUST NOT import React / `@tanstack/react-query`, MUST NOT touch DOM (`requestAnimationFrame` / `window.setTimeout` / `Date.now()`), MUST NOT hold module-level mutable state.
- Migration is test-first: event-wiring integration tests via `dispatchAgentEvent(...)` exist before code moves; extracted reducers gain co-located unit tests after.

This is part of epic #22 (代码复杂度热点治理) and follows the already-validated #251 playbook (`session-transcript` widget split, where pure state functions were 1:1 relocated to a sibling `model/` module).

## Goals / Non-Goals

**Goals:**

- Move the nine directly-extractable event reducers into a new React-independent `model/session-timeline-reducer.ts` as pure `(prev, detail, env) => next` functions.
- Slim `useSessionTimeline.ts` to an event-wiring thin layer: subscribe, run session-scoping predicate, dispatch into the reducer, and apply the result via `setX(updater)`.
- Remove the dead `taskProgress` / `loopProgress` state from the hook return and drop the corresponding props, render branch, and imports from `SessionTimeline.tsx`.
- Keep every external type/helper import path resolving (re-export through the reducer module from `useSessionTimeline.ts`).
- Establish test-first coverage for the previously-uncovered event wiring, then add direct unit tests for the extracted reducers.

**Non-Goals:**

- No change to hook return shape (except the two removed dead fields), event→state semantics, the 100ms cadence, ref-mutation semantics, session-scoping predicates, or `AgentDetailEventMap` contract.
- No change to `SessionTimeline.tsx` rendering or interaction beyond dropping the never-rendered `TaskProgressPanel` branch and its props.
- No forcing of `plan_session_update` / `flushPlanBuffer` / `handleToolEvent` into a pure reducer shape.
- No new dependencies, no server/runner/CLI changes, no performance optimization, no public API change.
- No re-authoring into a single `reducer(state, action)` with a discriminated union (deferred, mirroring #251's decision).

## Decisions

### D1. Reducer module owns the nine pure transitions plus the shared types/helpers

`model/session-timeline-reducer.ts` will hold:

- Types currently declared in `useSessionTimeline.ts`: `Round`, `RecoveryEvent`, `RecoveryStatus`, `PlanStep`, `PlanProgress`, `ContextHealthState`, `ContextHealthStatus`, `CompactionEntry`.
- Pure helpers used only by the reducers: `toContextHealthStatus`, `mapLivenessToRecoveryStatus`, `BASE_PLAN_STEPS`.
- Nine pure reducers: `planRoundStartReducer`, `planRoundCompleteReducer`, `coderRecoveryStatusReducer`, `sessionLivenessReducer`, `usageUpdatedReducer`, `contextHealthUpdateReducer`, `compactionEventReducer`, `contextCompactedReducer` (for `com.mohist.agent-session.context-compacted`), `contextHealthUpdatedReducer` (for `com.mohist.agent-session.context-health-updated`).
- The existing `deriveToolCallTitle` and `reconstructRoundsFromEvents` — both are already pure (no React/DOM/`Date.now`) and are consumed by `SessionTimeline.tsx` and `PlanProgressPanel.tsx`. Relocating them with the other pure logic gives the widget a single `model/` home for "things you can unit-test without React".

**Rationale:** the spec requires the reducer module to be React- and DOM-free; the types and helpers are part of the same cohesive state model. Co-locating them keeps the reducer module self-contained (no upward import into the hook) and gives `deriveToolCallTitle` / `reconstructRoundsFromEvents` the same direct-unit-test benefit.

**Alternative considered:** keep `deriveToolCallTitle` / `reconstructRoundsFromEvents` in `useSessionTimeline.ts`. Rejected — splits pure logic across two files and forces the reducer module to import from the hook (layer inversion).

**Alternative considered:** split types into `model/types.ts`. Rejected — over-fragmentation for a widget-internal module; types and the reducers that consume them change together.

### D2. Reducer signature: `(prev, detail, env) => next`, where `env` carries `now` and `random`

Three of the nine reducers (`plan_round_start`, `coder_recovery_status`, `session.liveness`) currently call `new Date().toISOString()` / `Date.now()`; the two compaction reducers also call `Math.random().toString(36)` to mint an id. Pure reducers cannot touch the wall clock or non-deterministic sources (testing.md §2).

The reducer signature is therefore:

```ts
interface SessionTimelineEnv {
  now: number            // epoch ms — hook passes Date.now()
  isoNow: string         // hook passes new Date().toISOString()
  randomId: () => string // hook passes () => Math.random().toString(36).slice(2, 8)
}

type Reducer<D> = (prev: SessionTimelineState, detail: D, env: SessionTimelineEnv) => SessionTimelineState
```

Where `SessionTimelineState` is `{ rounds, planProgress, recoveryStatus, contextHealth }` — the four state slices the nine reducers touch. The hook owns the clock and the id source; it constructs a single `env` object per event dispatch (or once per effect run — `Date.now` / `Math.random` are stable references) and passes it in.

**Rationale:** the env-injection shape preserves the exact `(prev, detail) => next` transition semantics required by the spec while keeping the reducers deterministic and directly unit-testable (the test passes a fixed `env`). It is the same pattern `runner/WorkspaceRegistry` uses (`now: () => number`).

**Alternative considered:** thread `now` / `randomId` only into the three reducers that need them, leaving the other six as `(prev, detail) => next`. Rejected — the spec says "each reducer SHALL have the signature `(prev, detail) => next`" and a mixed signature set is harder to call uniformly from the hook; the env is cheap to thread everywhere.

**Alternative considered:** inject a `TimeProvider`-style object (`{ now(): number; isoNow(): string }`). Rejected — over-engineered for a single widget; a plain value object suffices and is simpler to construct and assert.

**Alternative considered:** have the hook pre-stamp `now` / `id` onto the `detail` before calling the reducer. Rejected — mutates the event payload (which is also delivered to other subscribers) and obscures the reducer's dependency on time.

### D3. Reducer returns the full state slice object; hook merges via one `setUpdater` per slice

Each reducer receives `{ rounds, planProgress, recoveryStatus, contextHealth }` (the four live slices) and returns the same shape, mutating only the slices it owns. The hook applies the result with one batch of conditional updaters:

```ts
const next = reducer(prev, detail, env)
if (next.rounds !== prev.rounds) setRounds(next.rounds)
if (next.planProgress !== prev.planProgress) setPlanProgress(next.planProgress)
if (next.recoveryStatus !== prev.recoveryStatus) setRecoveryStatus(next.recoveryStatus)
if (next.contextHealth !== prev.contextHealth) setContextHealth(next.contextHealth)
```

The hook reads the four slices into a stable snapshot inside the subscription closure (or via refs that mirror state, matching the existing `setRoundsRef` pattern) so the reducer sees a consistent `prev`.

**Rationale:** `plan_round_start` and `plan_round_complete` each touch two slices (`rounds` + `planProgress`); `coder_recovery_status` and `session.liveness` touch `recoveryStatus` + `rounds`; the compaction reducers touch `rounds` + `contextHealth`. A single state-object in / state-object out keeps each reducer a single atomic transition (matches how the closures behave today) and avoids the hook having to know which slices each event touches.

**Alternative considered:** slice-specific reducers (`planRoundStartRounds(prev: Round[], detail)`, `planRoundStartPlan(prev: PlanProgress | null, detail)`). Rejected — splits one logical transition across two functions that must be called in lockstep, and the hook has to know the per-event slice pairing (re-introducing the coupling we're removing).

**Alternative considered:** a single `setSessionTimelineState` reducer wrapping all four slices (so the hook calls one updater). Rejected — changes the hook's `useState` shape (four independent states → one combined state) which is a larger refactor than this issue scopes, and the existing behavioral tests pin observable outputs, not the internal state layout. Noted as an open question for a possible follow-up.

### D4. `applyContextHealth` dedup becomes a pure `mergeContextHealth(prev, next)` helper

The existing `applyContextHealth` closure does:

```ts
setContextHealth((prev) => {
  if (!prev) return next
  if (prev.status === next.status && prev.contextWindowUsed === next.contextWindowUsed
      && prev.contextWindowSize === next.contextWindowSize
      && prev.contextUsagePercent === next.contextUsagePercent) return prev
  return next
})
```

This dedup is pure. It moves into the reducer module as `mergeContextHealth(prev: ContextHealthState | null, next: ContextHealthState): ContextHealthState | null` and is called inside the four context-health reducers (`usageUpdatedReducer`, `contextHealthUpdateReducer`, `compactionEventReducer`, `contextCompactedReducer`, `contextHealthUpdatedReducer`) — so `contextHealth` returned from each reducer is already deduplicated against the prev slice, and the hook's `if (next.contextHealth !== prev.contextHealth) setContextHealth(next.contextHealth)` line applies it.

**Rationale:** the dedup is part of the transition semantics, not the React wiring; relocating it with the reducer makes the equality contract directly unit-testable and removes the last piece of inline closure logic from the four context-health events.

### D5. `plan_session_update` / `flushPlanBuffer` / `scheduleFlush` / `handleToolEvent` stay in the hook

These four are explicitly excluded from the pure-reducer extraction by both the proposal and the spec (Requirement: "The plan_session_update flush chain and shared tool-call ref coalescing are preserved" — Scenario: "Batch + rAF + ref logic may stay in the hook or move to a ref-injected module function"). They stay in the hook.

**Rationale:**

- They are batch + rAF + `liveToolCallMapRef` side-effect logic, not `(prev, event) => next` transitions. Factoring them into a ref-injected module function adds ref-passing ceremony and indirection without unlocking direct unit testing — the rAF/`setTimeout` cadence is itself the behavior under test, and that already requires a fake-timer integration harness driving the hook.
- The hook is already the React coupling layer; keeping these as the wiring layer's intrinsic mechanism matches the issue's "thin layer" framing.
- `liveToolCallMapRef` is shared between the batched `flushPlanBuffer` path and the live `handleToolEvent` path (entries stored on `started`, mutated in place on `updated`/`completed`). Both paths must continue to share the same ref instance; keeping them in the hook makes the sharing explicit.

**Alternative considered:** factor `flushPlanBuffer` into `model/session-timeline-batch.ts` receiving `{ planBufferRef, liveToolCallMapRef, setRounds, mountedRef, now }`. Rejected for this issue — preserves semantics but adds a new module + injection wiring that isn't required by any spec scenario, and the batch/rAF path's primary risk (the 100ms cadence) is guarded by an integration test that drives the hook either way. Deferred to a follow-up if the hook's remaining size warrants it.

### D6. Dead state removal: delete hook fields, hook return, and `SessionTimeline.tsx` props/branch/imports

Concrete deletions:

- `useSessionTimeline.ts:209-210` — remove `const [taskProgress] = useState<TaskProgressMap>(new Map())` and `const [loopProgress] = useState<LoopProgress | null>(null)`.
- `useSessionTimeline.ts:826-827` — remove `taskProgress` / `loopProgress` from the returned object.
- `useSessionTimeline.ts:7` — drop `TaskProgressMap` / `LoopProgress` from the `entities/coder-session` import.
- `SessionTimeline.tsx:3` — drop `TaskProgressMap` / `LoopProgress` from the `entities/coder-session` import.
- `SessionTimeline.tsx:18-19` — remove the two props from `SessionTimelineProps`.
- `SessionTimeline.tsx:504-505` — remove the two props from the destructured parameter list.
- `SessionTimeline.tsx:528` — remove the `taskEntries` derivation.
- `SessionTimeline.tsx:579-581` — remove the `currentStage === 'build' && taskEntries.length > 0` render branch and the `TaskProgressPanel` usage.
- Keep `TaskProgressPanel` and `TaskStatusIcon` in the file — they are exported components that may still be referenced elsewhere; removing them is a separate decision (see Open Questions). The render branch is dead because its gate (`taskEntries.length > 0`) was always false.

**Rationale:** the panel never rendered with a non-empty task list (the Map is never written), so the rendered DOM is observably identical before/after. `SessionTimeline.test.tsx` already passes `taskProgress={new Map()}` / `loopProgress={null}` in every fixture; those props are dropped in the same change.

**Alternative considered:** also delete `TaskProgressPanel` and `TaskStatusIcon` from the file. Deferred — they are `export function`s and may have external references; a quick `rg` confirms no current import, but deleting exports is a contract change beyond this issue's dead-state scope.

### D7. External type/helper import paths preserved via re-export from `useSessionTimeline.ts`

`useSessionTimeline.ts` will `export * from './session-timeline-reducer'` (or named re-exports of the symbols the rest of the codebase imports: `Round`, `RecoveryStatus`, `PlanProgress`, `ContextHealthState`, `RecoveryEvent`, `deriveToolCallTitle`, `reconstructRoundsFromEvents`). This keeps:

- `SessionTimeline.tsx` importing from `../model/useSessionTimeline` — unchanged.
- `PlanProgressPanel.tsx` importing `type { PlanProgress }` from `../../coder-session/model/useSessionTimeline` — unchanged.

The widget barrel `widgets/coder-session/index.ts` is unchanged in this issue (it does not re-export these symbols today).

**Rationale:** the spec requires the import paths to keep resolving ("Widget barrel still re-Exports shared types and helpers"). Re-exporting from the original module path is the lowest-risk way to honor that while the symbols physically live in the reducer module.

**Alternative considered:** add the symbols to the widget barrel and update `PlanProgressPanel.tsx` to import through `@/widgets/coder-session`. Rejected for this issue — it's a cross-widget import normalization that's out of scope (Non-Goal: "No public-API change"). Noted as Open Question.

### D8. Step ordering by ascending risk, test-first

1. **Dead-state removal** — delete the two `useState` + hook return fields + `SessionTimeline.tsx` props/branch/imports + drop the props from `SessionTimeline.test.tsx` fixtures. Zero behavioral risk (panel never rendered). Establishes the slimmed return shape the rest of the work targets.
2. **Event-wiring integration tests** — add `dispatchAgentEvent(...)`-driven tests in `useSessionTimeline.test.ts` covering the previously-uncovered wiring: `plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, `compaction_event`, `com.mohist.agent-session.context-compacted`, `com.mohist.agent-session.context-health-updated`, `tool_call.started` / `updated` / `completed`, plus a `plan_session_update` batch + 100ms cadence assertion (using `vi.useFakeTimers()` + `vi.setSystemTime()`, per testing.md §2). This is the regression net for step 3.
3. **Extract pure reducers** — create `model/session-timeline-reducer.ts` with the types/helpers (D1), the `env`-injected signature (D2), the state-object in/out shape (D3), and the `mergeContextHealth` dedup (D4). Rewrite each of the nine subscription closures to delegate `setX(updater => reducer(snapshot, detail, env).slice)`.
4. **Reducer unit tests** — add `model/session-timeline-reducer.test.ts` exercising each reducer as a pure function with a fixed `env`.

Each step ends with `npm run typecheck -w packages/web` + `npm run test:run -w packages/web`. Commit per step for bisectability.

**Rationale:** mirrors #251's D4 — the riskiest semantic change (reducer extraction) lands on top of a freshly-green integration test baseline; the lowest-risk deletion goes first to slim the surface area.

## Risks / Trade-offs

- **[Reducer changes semantics subtly during relocation]** -> Each of the nine closures is moved 1:1 into a named reducer with the same transition body; the new integration tests (step 2) plus the pre-existing `usage.updated` / `context_health_update` / `compaction_event` behavioral cases in `useSessionTimeline.test.ts` pin event→state semantics. Step 4 adds direct reducer unit tests for the same transitions.
- **[`now` / `randomId` env injection changes a timestamp or id seen downstream]** -> The hook passes `Date.now()`, `new Date().toISOString()`, and `Math.random().toString(36).slice(2, 8)` verbatim into `env`; the reducer uses them at the same call site as today. The integration tests assert the resulting `rounds`/`recoveryEvents`/`compactions` entries carry the injected `now`/`id` (test controls `env` or `vi.setSystemTime`).
- **[`liveToolCallMapRef` sharing broken between batched and live paths]** -> `flushPlanBuffer` and `handleToolEvent` stay co-located in the hook (D5) and continue to share the same `useRef<Map>` instance; the integration test for `tool_call.started` → `updated` → `completed` (step 2) asserts the in-place mutation and the `timeout` → `failed` mapping.
- **[100ms cadence regressed]** -> `scheduleFlush` is untouched; the step-2 integration test uses `vi.useFakeTimers()` + `vi.advanceTimersByTimeAsync()` to assert that two `plan_session_update` events within a 100ms window coalesce into a single `setRounds` flush, and that the rAF/setTimeout choice matches the elapsed-since-last-flush branch.
- **[External import path breaks after type relocation]** -> D7 re-export from `useSessionTimeline.ts` keeps `PlanProgressPanel.tsx` and `SessionTimeline.tsx` resolving; typecheck catches any miss.
- **[`TaskProgressPanel` left as dead export after branch removal]** -> Acceptable; the export contract is unchanged. A follow-up cleanup issue can remove the now-unused component if no consumer surfaces.
- **[Snapshot read in subscription closure sees stale state]** -> The existing `setRoundsRef` pattern already reads state inside the updater; for the reducer's `prev`, the hook either (a) reads the four slices via a ref mirror updated on each render, or (b) calls the reducer inside the `setUpdater` body so `prev` is the live React snapshot. Decision per reducer: option (b) where the reducer only touches one slice, option (a) where it touches two (`plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, compaction reducers). Step 3 picks per-reducer and documents in code.

## Migration Plan

Pure internal refactor — no data, protocol, API, or config change; no feature flags; no server deployment coupling.

- **Deploy:** ships as a normal web build. Single PR with one commit per step (D8) so any regression bisects to the exact change.
- **Rollback:** revert the PR/commits; no data migration or state cleanup required.
- **Verification gate at every step:** `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` green. Manual smoke (out of band): drive a live coder session and confirm (a) round reconstruction, (b) live streaming deltas, (c) tool-call tracking, (d) recovery banner, (e) context-health bar, (f) plan-progress steps all render identically to `master`.

## Open Questions

- **`TaskProgressPanel` / `TaskStatusIcon` after branch removal:** they remain as `export function`s in `SessionTimeline.tsx` with no remaining in-tree consumer. Delete in this issue, or defer to a follow-up cleanup issue? Current recommendation: defer (out of dead-state scope; removing exports is a contract change).
- **Normalize `PlanProgressPanel` import path through the widget barrel:** currently bypasses the barrel with a direct `../../coder-session/model/useSessionTimeline` path. Out of scope here (Non-Goal: no public-API change); flag for a separate FSD-hygiene issue if desired.
- **Combined `SessionTimelineState` reducer wrapper (D3 alternative):** folding the four `useState` slices into one `useReducer` would let the hook call a single `dispatch` per event. Semantically equivalent but a larger internal refactor than this issue scopes; defer to a follow-up if the hook's wiring layer is still too thick after this extraction.
- **Relocate `flushPlanBuffer` / `scheduleFlush` / `handleToolEvent` into `model/session-timeline-batch.ts` (D5 alternative):** revisit if a future issue wants to shrink the hook further. Not required for the complexity-reduction target of this issue.
