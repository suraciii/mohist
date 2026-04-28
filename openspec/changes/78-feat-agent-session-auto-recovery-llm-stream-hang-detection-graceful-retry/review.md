# Review Report

## Verdict: FAIL

## Dimensions

### Correctness: FAIL

**E1. Missing `recoveryEvents` field in plan_round_start handler (runtime crash)**
- `packages/cli/web/src/hooks/useSessionTimeline.ts:271-285`
- The `plan_round_start` handler creates a `newRound: Round` object with explicit type annotation but omits the required `recoveryEvents: RecoveryEvent[]` field. The `Round` interface was extended with this required field in this change, but the pre-existing handler was not updated.
- At runtime, if a `coder_recovery_status` event fires while the last round was created by `plan_round_start`, the spread `[...lastRound.recoveryEvents, ...]` throws `TypeError: lastRound.recoveryEvents is not iterable` because `recoveryEvents` is `undefined`.
- **Why tsc doesn't catch it:** The backend `tsconfig.json` includes only `src/**/*`. The frontend code in `web/src/` is outside this scope and is never type-checked by `cd packages/cli && npx tsc --noEmit`. The web frontend has its own `web/tsconfig.json` but cannot be type-checked standalone (no local `node_modules`).

**E2. Recovery status cleared on 'failed' — contradicts spec**
- `packages/cli/web/src/hooks/useSessionTimeline.ts:380-381`
- The code sets `setRecoveryStatus(null)` for both `'recovered'` and `'failed'` statuses. Per spec (agent-session-ui/spec.md): "the indicator SHALL remain visible until the session ends or a new event stream begins" when status is `'failed'`.
- The inline `recoveryEvents` in the round are correctly preserved, but the top-level banner (`RecoveryBanner`) is dismissed immediately on failure.

### Complexity: PASS with warnings

- `runPromptWithHangRecovery` is ~197 lines (`acp-session.ts:103-299`), exceeding the 50-line guideline. However, the function has a single clear loop structure with well-separated concerns (idle monitoring, WIP commit, cancel, cooldown, re-prompt). The helper functions (`doWipCommit`, `doCancel`, `doCooldown`, `startIdleMonitor`, `stopIdleMonitor`) keep each concern focused.
- No copy-pasted code — `runAcpSession` and `createAcpConnection` share the recovery function.
- Cyclomatic complexity is moderate (~8-10) — acceptable for this recovery loop.

### Test Coverage: PASS

- 6 new tests in `acp-hang-recovery.test.ts` covering: idle detection → recovery, max attempts exceeded, cancel timeout, normal session (no recovery), hangIdleMs=0 disabled, full recovery lifecycle with SSE + log verification.
- 3 new tests in `ralph-executor.test.ts` for `hang_unrecoverable` category: basic detection, match anywhere in error string, precedence over timeout.
- All 9 new tests pass. The 49 pre-existing test failures are unrelated (merge-queue, priority, recover-issues, pipeline-checkpoint, etc.).

### Security: PASS

- No new user inputs processed.
- WIP commit uses existing `onBeforeKill` callback with 5s timeout.
- No secrets or credentials exposed.
- Recovery hint message is a fixed template string.

### Spec Compliance: FAIL

#### T-001: Register coder_recovery_status in all 4 SSE event arrays — PASS
- [x] `coder_recovery_status` in `EventMap` in `event-bus.ts:32` with correct payload shape
- [x] `coder_recovery_status` in `ALL_EVENT_TYPES` in `events.ts:30`
- [x] `coder_recovery_status` in `AgentDetailEventMap` in `web/src/lib/types.ts:120`
- [x] `coder_recovery_status` in `AGENT_DETAIL_EVENTS` in `agent-events.ts:34`
- [x] `coder_recovery_status` in `eventTypes` in `useSSE.ts:111`
- [x] Typecheck passes (backend only)

#### T-002: Add hang_unrecoverable failure category — PASS
- [x] `FailureCategory` includes `'hang_unrecoverable'` (`ralph-executor.ts:27`)
- [x] `FAILURE_CATEGORY_CONFIGS` has `{ maxAttempts: 1, retryable: false }` (`ralph-executor.ts:40`)
- [x] `categorizeFailure('[HANG_UNRECOVERABLE] cancel timed out')` returns `'hang_unrecoverable'`
- [x] `categorizeFailure('[HANG_UNRECOVERABLE] max recovery attempts exceeded')` returns `'hang_unrecoverable'`
- [x] `[HANG_UNRECOVERABLE]` check placed before timeout check (correct precedence)
- [x] `generateAdjustmentsFromCategory('hang_unrecoverable')` returns 2-item array

#### T-003: Implement runPromptWithHangRecovery — PASS
- [x] Function exported from `acp-session.ts:103` with `PromptWithRecoveryParams` interface
- [x] `AcpSessionOptions.hangIdleMs?: number` (`acp-session.ts:51`)
- [x] `AcpConnectionOptions.hangIdleMs?: number` (`acp-session.ts:672`)
- [x] `hangIdleMs=0` disables monitoring (`acp-session.ts:127, 181`)
- [x] Recovery sequence: detected → WIP commit → cancel → cooldown → re-prompt (`acp-session.ts:250-292`)
- [x] Cancel raced against 5s timeout (`acp-session.ts:160-170`)
- [x] Max 2 recovery attempts; 3rd hang returns `[HANG_UNRECOVERABLE]` (`acp-session.ts:70, 254`)
- [x] `coder_recovery_status` SSE events at all 4 lifecycle points
- [x] `workflow_log` events: `acp_session_hang_detected`, `acp_session_recovery_started`, `acp_session_recovery_succeeded`, `acp_session_recovery_failed`
- [x] Normal session produces no recovery events
- [x] setInterval cleared on all exit paths (`stopIdleMonitor` called after prompt resolves, in catch block)

#### T-004: Integrate into runAcpSession and createAcpConnection — PASS
- [x] `runAcpSession` uses `runPromptWithHangRecovery` (`acp-session.ts:593`)
- [x] `createAcpConnection.prompt()` uses `runPromptWithHangRecovery` (`acp-session.ts:989`)
- [x] `lastEventTime` updated in every `sessionUpdate` callback (`acp-session.ts:428, 810`)
- [x] `lastEventTime` initialized at prompt start (via `setLastEventTime(Date.now())` in `runPromptWithHangRecovery:210`)
- [x] Recovery counter resets per prompt round (fresh `let recoveryAttemptCount = 0` in each `runPromptWithHangRecovery` call)
- [x] `hangIdleMs` passed through from options
- [x] `onBeforeKill` wired through
- [x] Existing behavior unchanged when no hang occurs

#### T-005: SessionTimeline recovery status indicators — FAIL
- [x] `useSessionTimeline` subscribes to `coder_recovery_status` via `onAgentEvent` (`useSessionTimeline.ts:357-396`)
- [x] `recoveryStatus` state updates on detected/recovering/recovered/failed
- [x] Amber warning banner for `'detected'` ("LLM 连接中断，正在尝试恢复...") (`SessionTimeline.tsx:282-288`)
- [x] Blue progress indicator for `'recovering'` ("正在恢复 (attempt N)...") (`SessionTimeline.tsx:291-299`)
- [x] Banner dismissed on `'recovered'` (returns null) (`SessionTimeline.tsx:314`)
- **[FAIL]** Red error banner dismissed immediately on `'failed'` (`useSessionTimeline.ts:380-381`) — spec requires it remain visible
- [x] Historical recovery events rendered in `reconstructRoundsFromLogs` (`useSessionTimeline.ts:134-143`)
- **[FAIL]** `plan_round_start` handler creates `Round` without `recoveryEvents: []` (`useSessionTimeline.ts:271-285`) — will crash at runtime if recovery events hit that round

#### T-006: Unit tests — PASS
- [x] `categorizeFailure` tests cover `hang_unrecoverable` and all existing categories still pass
- [x] Test verifies `coder_recovery_status` with `status='detected'` on idle
- [x] Test verifies max 2 recovery attempts exceeded returns `[HANG_UNRECOVERABLE] max recovery attempts exceeded`
- [x] Test verifies cancel timeout returns `[HANG_UNRECOVERABLE] cancel timed out`
- [x] Test verifies normal session produces no recovery events
- [x] Test verifies `hangIdleMs=0` disables idle monitoring
- [x] All new tests pass (6/6 acp-hang-recovery, 3/3 ralph-executor hang tests)
- [x] Typecheck passes (backend)

## Fix Suggestions

1. **`packages/cli/web/src/hooks/useSessionTimeline.ts:274-285`** — Add `recoveryEvents: []` to the `newRound` object in the `plan_round_start` handler. Also add a web frontend typecheck step to CI (`cd packages/cli/web && npx tsc --noEmit` after `npm install`) so future frontend type errors are caught.

2. **`packages/cli/web/src/hooks/useSessionTimeline.ts:380-381`** — Remove `detail.status === 'failed'` from the condition that clears `recoveryStatus`. Keep only `'recovered'` dismissing the banner:
   ```typescript
   if (detail.status === 'recovered') {
     setRecoveryStatus(null)
   }
   ```
   The failed state should leave the error banner visible. It will naturally disappear when the session ends (component unmounts) or a new event stream begins.
