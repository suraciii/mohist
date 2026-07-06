### Requirement: Directly-extractable event reducers live in a React-independent module

The session-timeline state machine SHALL move its directly-extractable event reducers — `plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, `usage.updated`, `context_health_update`, `compaction_event`, `com.mohist.agent-session.context-compacted`, `com.mohist.agent-session.context-health-updated` — into a new React-independent module `model/session-timeline-reducer.ts` as pure functions of shape `(prev, detail) => next`. The reducer module MUST NOT import React (`useEffect` / `useRef` / `useState` / `useCallback` / `useQuery`), MUST NOT touch the DOM (`requestAnimationFrame` / `window.setTimeout` / `Date.now`), and MUST NOT hold module-level mutable state. The hook `useSessionTimeline` SHALL become an event-wiring thin layer: it subscribes to `onAgentEvent`, runs the session-scoping predicates, and dispatches each matching event into the extracted reducer. The widget barrel (`widgets/coder-session/index.ts`) SHALL continue to re-export the shared types/helpers (`Round`, `RecoveryStatus`, `PlanProgress`, `ContextHealthState`, `deriveToolCallTitle`, `reconstructRoundsFromEvents`) the rest of the codebase imports today, routing them through the reducer module where they now live.

#### Scenario: Reducer module has no React or DOM imports

- **WHEN** `model/session-timeline-reducer.ts` is inspected
- **THEN** it SHALL NOT import anything from `react` or `@tanstack/react-query`
- **AND** it SHALL NOT reference `requestAnimationFrame`, `window.setTimeout`, or `Date.now()`
- **AND** it SHALL NOT declare module-level `let`/`var` mutable bindings

#### Scenario: Each directly-extractable event has a pure reducer

- **WHEN** the reducer module is inspected
- **THEN** it SHALL export one pure reducer function for each of `plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, `usage.updated`, `context_health_update`, `compaction_event`, `com.mohist.agent-session.context-compacted`, and `com.mohist.agent-session.context-health-updated`
- **AND** each reducer SHALL have the signature `(prev: <state slice>, detail: <event detail>) => <state slice>` returning a new value without mutating `prev`

#### Scenario: Hook dispatches into the extracted reducers

- **WHEN** the hook subscribes to any of the nine directly-extractable events
- **THEN** the subscription body SHALL delegate the state transition to the matching reducer exported from `model/session-timeline-reducer.ts`
- **AND** the hook SHALL retain only the session-scoping predicate and the `setX(updater)` wiring around that call

#### Scenario: Widget barrel still re-exports shared types and helpers

- **WHEN** a downstream module imports `Round`, `RecoveryStatus`, `PlanProgress`, `ContextHealthState`, `deriveToolCallTitle`, or `reconstructRoundsFromEvents` from `widgets/coder-session`
- **THEN** the import SHALL continue to resolve
- **AND** the symbols SHALL be re-exported through the reducer module when they have been relocated there

### Requirement: The plan_session_update flush chain and shared tool-call ref coalescing are preserved

The `plan_session_update` → `flushPlanBuffer` chain SHALL remain observably identical to before this change in three invariant dimensions: (a) the 100ms merge cadence driven by `requestAnimationFrame` and `window.setTimeout` with `FLUSH_INTERVAL = 100`; (b) batch accumulation in `planBufferRef` with a single flush draining the whole batch into one `setRounds` updater call; (c) the `liveToolCallMapRef` `set` / `get` / in-place mutation semantics performed inside that `setRounds` updater for any `tool_call.started` / `tool_call.updated` / `tool_call.completed` session-updates carried inside the batch. The live `tool_call.started` / `tool_call.updated` / `tool_call.completed` events (subscribed directly via `onAgentEvent` and handled by `handleToolEvent`) SHALL continue to share the same `liveToolCallMapRef` as the batched path — entries are stored once on `started` and mutated in place on `updated` / `completed` — and SHALL continue to map the `timeout` state to `failed` via `toToolCallEntryState`. Both the batched flush path and the live `handleToolEvent` path MAY remain inside the hook OR be factored into module functions that receive the refs as injected arguments; either placement MUST produce observably identical state transitions, cadence, and ref side-effects. Neither path SHALL be forced into a pure `(prev, event) => next` reducer shape.

#### Scenario: 100ms merge cadence is preserved

- **WHEN** multiple `plan_session_update` events arrive within a 100ms window
- **THEN** they SHALL be coalesced into a single `setRounds` flush
- **AND** the scheduling SHALL use `requestAnimationFrame` when at least 100ms has elapsed since the last flush, otherwise a `setTimeout` for the remaining `(100 - elapsed)` milliseconds followed by `requestAnimationFrame`

#### Scenario: Batched tool-call session-updates coalesce through liveToolCallMapRef

- **WHEN** a `tool_call.started` session-update arrives inside a flush batch
- **THEN** the entry SHALL be stored in `liveToolCallMapRef` keyed by `toolCallId` and appended to the last round's `toolCalls`
- **WHEN** a `tool_call.updated` or `tool_call.completed` session-update for a known `toolCallId` arrives inside a later flush batch
- **THEN** the existing entry SHALL be mutated in place (state, title, rawInput, rawOutput) and re-cloned into the last round's `toolCalls` array

#### Scenario: Live tool_call events share the same ref and map timeout to failed

- **WHEN** a live `tool_call.started`, `tool_call.updated`, or `tool_call.completed` event arrives for the current session
- **THEN** the handler SHALL `set` / `get` / mutate the same `liveToolCallMapRef` the batched path uses
- **AND** the `timeout` state on a live `tool_call.started` event SHALL map to `failed`

#### Scenario: Batch + rAF + ref logic may stay in the hook or move to a ref-injected module function

- **WHEN** the implementation chooses where to place `flushPlanBuffer`, `scheduleFlush`, and `handleToolEvent`
- **THEN** the chosen placement SHALL preserve the batch accumulation, the rAF/setTimeout cadence, and the `liveToolCallMapRef` side-effect semantics
- **AND** none of these three SHALL be expressed as a pure `(prev, event) => next` reducer

### Requirement: Dead state taskProgress and loopProgress are removed

The hook SHALL NOT declare `useState` for `taskProgress` (`TaskProgressMap`) or `loopProgress` (`LoopProgress`), and SHALL NOT return either field. The `SessionTimeline` component SHALL NOT accept `taskProgress` or `loopProgress` props, SHALL NOT render the `TaskProgressPanel` (the `currentStage === 'build' && taskEntries.length > 0` render branch), and SHALL NOT import the `TaskProgressMap` or `LoopProgress` types from `entities/coder-session`. Because `taskProgress` was a `useState` declared without a setter and `loopProgress` was only ever `null`, this removal SHALL NOT change any rendered output — the `TaskProgressPanel` never rendered with a non-empty task list.

#### Scenario: Hook return shape no longer carries the dead fields

- **WHEN** the hook's return value is inspected after this change
- **THEN** it SHALL NOT contain a `taskProgress` key
- **AND** it SHALL NOT contain a `loopProgress` key
- **AND** the hook body SHALL NOT contain `useState<TaskProgressMap>` or `useState<LoopProgress>`

#### Scenario: SessionTimeline drops the build-stage panel and the dead imports

- **WHEN** `SessionTimeline.tsx` is inspected after this change
- **THEN** its props interface SHALL NOT mention `taskProgress` or `loopProgress`
- **AND** it SHALL NOT import `TaskProgressMap` or `LoopProgress`
- **AND** the rendered output SHALL NOT include the `TaskProgressPanel` element under any condition

#### Scenario: Removed dead state produces no observable render change

- **WHEN** the session timeline renders under any stage (`plan`, `build`, etc.) before and after this change
- **THEN** the rendered DOM SHALL be identical
- **AND** no test that asserted on rendered output SHALL regress solely because of the dead-state removal

### Requirement: The hook return shape is preserved except for the removed dead state

The hook SHALL return exactly the fields `{ rounds, isLoading, isStreaming, recoveryStatus, planProgress, contextHealth }` with their pre-change semantics. `rounds` SHALL continue to be the reconstructed `Round[]` (history on mount, live-appended thereafter). `isLoading` SHALL remain the constant `false`. `isStreaming` SHALL continue to track whether an agent is running for this issue. `recoveryStatus` SHALL continue to hold the latest recovery banner state (or `null`). `planProgress` SHALL continue to hold the plan-step tracker. `contextHealth` SHALL continue to hold the deduplicated context-health snapshot.

#### Scenario: Hook return shape matches the preserved contract

- **WHEN** the hook is invoked for any issue/session combination
- **THEN** the returned object's keys SHALL be exactly `rounds`, `isLoading`, `isStreaming`, `recoveryStatus`, `planProgress`, `contextHealth`
- **AND** each field SHALL carry the same type and observable semantics it had before this change

### Requirement: Event-to-state transition semantics are preserved bit-for-bit

Every event's effect on the timeline state SHALL be observably identical to before this change. Specifically: `plan_round_start` SHALL append a new `Round` (with `roundLabel` or `Round N+1` fallback, `startedAt = now`, empty content arrays) and mark the matching `planProgress` step `running` (appending a new step when the `roundType` is not in `BASE_PLAN_STEPS`). `plan_round_complete` SHALL mark the matching step `completed` or `failed` based on `verdict`, stamp `duration` and `verdict`, and on a `self-review` `FAIL` append `auto-fix` and `re-self-review` pending steps. `coder_recovery_status` SHALL set `recoveryStatus`, append a `recoveryEvents` entry to the last round, and clear `recoveryStatus` to `null` on `recovered` / `failed`. `session.liveness` SHALL map `probing` → `recovering`, `running` → `recovered`, anything else → `failed`, append a recovery event using `activeProbeVersion` / `satisfiedProbeVersion` / `probeVersion` (in that fallback order) as the attempt and the failure/probe-deadline/last-activity as the reason, and clear `recoveryStatus` on `running` / `failed`. `usage.updated` SHALL update `contextHealth` only when at least one of `contextWindowUsed` / `contextWindowSize` / `contextUsagePercent` / `healthStatus` is present, using the server-provided values verbatim with no derivation from a window-size ratio. `context_health_update` SHALL update `contextHealth` with the server-provided values. `compaction_event` and `com.mohist.agent-session.context-compacted` SHALL append a `CompactionEntry` to the last round (synthesizing a placeholder `Compaction` round when no round exists) and update `contextHealth.contextWindowUsed` to the `contextWindowUsedAfter` value with a `null` status. `com.mohist.agent-session.context-health-updated` SHALL update `contextHealth` with the server-provided values.

#### Scenario: plan_round_start appends a round and marks the step running

- **WHEN** a `plan_round_start` event for the current issue passes the scoping predicate
- **THEN** a new `Round` SHALL be appended with `roundLabel` from the event (or `Round N+1`), empty `toolCalls` / `recoveryEvents` / `compactions`, and `completedAt: null`
- **AND** the matching `planProgress` step SHALL be set to `running`, or appended as `running` when its `roundType` is absent from `BASE_PLAN_STEPS`

#### Scenario: plan_round_complete stamps verdict and extends on self-review failure

- **WHEN** a `plan_round_complete` event for the current issue arrives
- **THEN** the step whose `roundType` matches SHALL be set to `completed` (verdict `PASS` or absent) or `failed` (verdict `FAIL`), with `duration` and `verdict` stamped
- **WHEN** the failed step is `self-review`
- **THEN** `auto-fix` and `re-self-review` pending steps SHALL be appended (each at most once)

#### Scenario: coder_recovery_status drives the recovery banner and round events

- **WHEN** a `coder_recovery_status` event arrives for the current issue
- **THEN** `recoveryStatus` SHALL be set from the event, a recovery event SHALL be appended to the last round, and `recoveryStatus` SHALL clear to `null` on `recovered` or `failed`

#### Scenario: session.liveness maps to recovery states with ordered attempt fallback

- **WHEN** a `session.liveness` event for the current session arrives
- **THEN** `probing` SHALL map to `recovering`, `running` SHALL map to `recovered`, any other status SHALL map to `failed`
- **AND** the appended recovery event's `attempt` SHALL equal `activeProbeVersion` if present, else `satisfiedProbeVersion`, else `probeVersion`, else `1`
- **AND** `recoveryStatus` SHALL clear to `null` when the liveness status is `running` or `failed`

#### Scenario: usage.updated applies server-provided context-health values verbatim

- **WHEN** a `usage.updated` event arrives carrying at least one of `contextWindowUsed`, `contextWindowSize`, `contextUsagePercent`, or `healthStatus`
- **THEN** `contextHealth` SHALL update with the server-provided values verbatim
- **AND** the percent and health status SHALL NOT be derived from a `used / size` ratio
- **WHEN** all four fields are absent
- **THEN** `contextHealth` SHALL remain unchanged

#### Scenario: context_health_update applies server-provided values verbatim

- **WHEN** a `context_health_update` event arrives for the current session
- **THEN** `contextHealth` SHALL update with the server-provided values verbatim (no derivation)

#### Scenario: compaction events append an entry and reset contextWindowUsed

- **WHEN** a `compaction_event` or `com.mohist.agent-session.context-compacted` event arrives for the current session/issue
- **THEN** a `CompactionEntry` SHALL be appended to the last round's `compactions`, or to a synthesized placeholder `Compaction` round when no round exists
- **AND** `contextHealth.contextWindowUsed` SHALL become the event's `contextWindowUsedAfter` (or `null`) and `contextHealth.status` SHALL become `null`

#### Scenario: com.mohist.agent-session.context-health-updated applies server-provided values

- **WHEN** a `com.mohist.agent-session.context-health-updated` event arrives for the current issue
- **THEN** `contextHealth` SHALL update with the server-provided values verbatim

### Requirement: Session-scoping predicates are preserved

The per-event session-scoping rules SHALL remain unchanged. The `plan_round_start` and `plan_session_update` events SHALL gate on `detail.issueId === issueId` AND, when a session is bound, on a strict coder-session match (`detail.coderSessionId === session.id`, or — when `coderSessionId` is absent — `detail.acpSessionId === session.acpSessionId`, dropping the event when both are absent). The `plan_round_complete`, `coder_recovery_status`, `com.mohist.agent-session.context-compacted`, and `com.mohist.agent-session.context-health-updated` events SHALL gate on `detail.issueId === issueId` only. The `message.delta`, `reasoning.delta`, `tool_call.started` / `updated` / `completed`, `session.liveness`, `usage.updated`, `context_health_update`, and `compaction_event` events SHALL gate on the `isCurrentSessionEvent` predicate (matching `acpSessionId === session.acpSessionId`, or `coderSessionId === session.id`, or `sessionId === session.id`; accepting the event when no session is bound). Every subscription SHALL drop the event when `mountedRef.current` is `false`.

#### Scenario: plan_round_start and plan_session_update use strict coder-session scoping

- **WHEN** a session is bound and a `plan_round_start` or `plan_session_update` event for the current issue arrives
- **THEN** the event SHALL be processed only when `detail.coderSessionId === session.id`, or — `coderSessionId` absent — `detail.acpSessionId === session.acpSessionId`
- **AND** the event SHALL be dropped when both `coderSessionId` and `acpSessionId` are absent

#### Scenario: plan_round_complete, coder_recovery_status, and the two domain events gate on issueId only

- **WHEN** a `plan_round_complete`, `coder_recovery_status`, `com.mohist.agent-session.context-compacted`, or `com.mohist.agent-session.context-health-updated` event arrives
- **THEN** the event SHALL be processed when `detail.issueId === issueId`, regardless of any bound session
- **AND** it SHALL be dropped otherwise

#### Scenario: Live-delta and tool_call events use isCurrentSessionEvent

- **WHEN** a `message.delta`, `reasoning.delta`, `tool_call.started` / `updated` / `completed`, `session.liveness`, `usage.updated`, `context_health_update`, or `compaction_event` event arrives
- **THEN** the event SHALL be processed only when `isCurrentSessionEvent(detail)` is true (or when no session is bound)
- **AND** it SHALL NOT consult `detail.issueId`

#### Scenario: Unmounted hook drops every event

- **WHEN** any subscribed event arrives after the hook's cleanup has run (`mountedRef.current === false`)
- **THEN** the event SHALL be dropped without mutating state

### Requirement: Migration is test-first

Event-wiring integration tests that drive the hook via `dispatchAgentEvent(...)` SHALL be added BEFORE the reducer extraction begins. These tests SHALL cover the previously-uncovered wiring for `plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, `compaction_event`, `com.mohist.agent-session.context-compacted`, `com.mohist.agent-session.context-health-updated`, and the `tool_call.started` / `updated` / `completed` events, plus a `plan_session_update` batch + 100ms cadence assertion. After extraction, the pure reducers in `model/session-timeline-reducer.ts` SHALL gain direct unit tests. The pre-existing `useSessionTimeline.test.ts` (including the `usage.updated` / `context_health_update` / `compaction_event` behavioral cases pinned by issue-247) and `SessionTimeline.test.tsx` SHALL continue to pass, with `SessionTimeline.test.tsx` updated to drop the removed `taskProgress` / `loopProgress` props.

#### Scenario: Event-wiring integration tests exist before migration

- **WHEN** the reducer extraction commit is inspected
- **THEN** a prior commit SHALL have added `dispatchAgentEvent`-driven integration tests covering `plan_round_start`, `plan_round_complete`, `coder_recovery_status`, `session.liveness`, the two `com.mohist.agent-session.*` events, and the `tool_call.*` events
- **AND** those tests SHALL assert the resulting hook state for each event

#### Scenario: Extracted reducers gain direct unit tests

- **WHEN** the reducer extraction is complete
- **THEN** `model/session-timeline-reducer.ts` SHALL have a co-located unit test file exercising each exported reducer as a pure `(prev, detail) => next` function

#### Scenario: Pre-existing behavioral tests remain green

- **WHEN** the test suite runs after this change
- **THEN** `useSessionTimeline.test.ts` SHALL pass unchanged in its behavioral assertions
- **AND** `SessionTimeline.test.tsx` SHALL pass after dropping the `taskProgress` and `loopProgress` props from its render fixtures
