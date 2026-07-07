# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test-setup
  Evidence: `useSessionTimeline.test.ts` enabled fake timers with the default Vitest set (`setTimeout`, `setInterval`, `clearTimeout`, `clearInterval`, `Date`) but did not fake `requestAnimationFrame`. The test "plan_session_update uses the rAF branch when at least 100ms has elapsed since the last flush" therefore scheduled a real `requestAnimationFrame` callback that never fired under `vi.advanceTimersByTime`, causing the assertion `agentText === 'first second'` to fail with `'first '`. This is a test-harness mismatch, not an implementation regression.
  Verification: Updated `beforeEach` in `packages/web/src/widgets/coder-session/model/useSessionTimeline.test.ts:366` to `vi.useFakeTimers({ toFake: ['setTimeout', 'setInterval', 'clearTimeout', 'clearInterval', 'Date', 'requestAnimationFrame', 'cancelAnimationFrame'] })`; ran `npm run test:run -w packages/web -- src/widgets/coder-session/model/useSessionTimeline.test.ts` (39 passed) and `npm run test:run -w packages/web` (4410 passed, 1 skipped) and `npm run typecheck -w packages/web` (green).
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/web/src/widgets/coder-session/ui/SessionTimeline.tsx
  Evidence: After dead-state removal, `TaskProgressPanel` (coder-session) and its local helper `TaskStatusIcon` are no longer referenced by any in-tree render branch. They are intentionally retained per design D6 to avoid a contract change in this issue, but they now constitute dead code.
  SuggestedAction: Open a separate cleanup issue to remove the coder-session `TaskProgressPanel`/`TaskStatusIcon` and the `LoopProgress` import they require, once the export-contract impact is reviewed.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: packages/web/src/widgets/issue-workflow/ui/PlanProgressPanel.tsx
  Evidence: `PlanProgressPanel.tsx` imports `type { PlanProgress }` via the deep path `../../coder-session/model/useSessionTimeline`, bypassing the `coder-session` widget barrel.
  SuggestedAction: Normalize the import through the widget barrel in a dedicated FSD-hygiene issue (out of scope for issue-314; noted in design Open Questions).
  Status: follow-up

- [ID: item-4]
  Severity: test-gap
  Scope: packages/web/src/widgets/coder-session/model/useSessionTimeline.test.ts
  Evidence: The new integration tests cover live `tool_call.started`/`updated`/`completed` events (via direct `onAgentEvent` subscription and `handleToolEvent`) and `message.delta` batching inside `plan_session_update`. They do not exercise the batched `tool_call.*` path inside `flushPlanBuffer`, which has its own `liveToolCallMapRef` set/get/in-place mutation logic and is explicitly required by the spec scenario "Batched tool-call session-updates coalesce through liveToolCallMapRef".
  SuggestedAction: Add a `dispatchAgentEvent('plan_session_update', { sessionUpdate: 'tool_call.started' })` → `tool_call.updated` → `tool_call.completed` sequence that coalesces through the flush buffer and asserts the entry is mutated in place and mapped to the last round.
  Status: follow-up

- [ID: item-5]
  Severity: test-gap
  Scope: packages/web/src/widgets/coder-session/model/session-timeline-reducer.test.ts
  Evidence: Several reducer edge cases are not exercised: `planRoundCompleteReducer` when `planProgress` is null (it synthesizes `BASE_PLAN_STEPS`); `contextHealthUpdateReducer`/`contextHealthUpdatedReducer` deduplication through `mergeContextHealth`; `compactionEventReducer`/`contextCompactedReducer` fallback to `env.isoNow` when `recordedAt` is absent; `coderRecoveryStatusReducer` for `detected` status.
  SuggestedAction: Add these cases to the co-located reducer unit tests.
  Status: follow-up

- [ID: item-6]
  Severity: minor
  Scope: packages/web/src/widgets/coder-session/model/useSessionTimeline.ts:30
  Evidence: `export * from './session-timeline-reducer'` re-exports additional symbols that were not previously part of the `useSessionTimeline` module surface (`BASE_PLAN_STEPS`, `toContextHealthStatus`, `mapLivenessToRecoveryStatus`, `mergeContextHealth`, the nine reducers, `SessionTimelineState`, `SessionTimelineEnv`). Existing consumers only import `Round`, `RecoveryEvent`, `RecoveryStatus`, `PlanProgress`, `ContextHealthState`, `deriveToolCallTitle`, and `reconstructRoundsFromEvents`, so nothing breaks, but the public surface is slightly expanded beyond the issue's "no public API change" non-goal.
  SuggestedAction: Consider switching to explicit named re-exports of only the previously-public symbols.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: info
  Scope: openspec/changes/issue-314/tasks.json + design.md D6
  Evidence: T-001 acceptance criterion states "SessionTimeline.tsx ... no longer imports `TaskProgressMap` or `LoopProgress`", while the same criterion requires "`TaskProgressPanel` remains exported from SessionTimeline.tsx". Because the retained exported `TaskProgressPanel` still declares `loopProgress: LoopProgress | null` in its props, `LoopProgress` must remain imported. The code correctly keeps the import to satisfy the higher-priority "keep `TaskProgressPanel` exported" decision; the conflict is in the task/spec wording, not in the implementation.
  SuggestedAction: Update the T-001 acceptance criterion and any matching spec text to state that `LoopProgress` import is retained as long as `TaskProgressPanel` remains exported, or defer cleanup to the follow-up issue that removes the dead panel.
  Status: pre-existing

- [ID: item-8]
  Severity: info
  Scope: packages/web/src/widgets/coder-session/model/useSessionTimeline.ts:234-250
  Evidence: The new `dispatch` helper reads the four state slices from refs and applies reducer output via conditional `setX` calls. This differs from the original code, which used `setX` updater functions that receive React's pending state. The ref-snapshot approach is a documented design trade-off (design D3) and all tests pass; in rapid synchronous multi-event sequences it could observe slightly stale state compared with updater functions. This is an architectural choice, not a regression in observable behavior under normal event dispatch.
  SuggestedAction: If future stress testing reveals ordering issues, consider folding the four slices into a single `useReducer` (already noted as a design Open Question).
  Status: pre-existing

<promise>PASS</promise>
