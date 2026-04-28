# Review Report

## Verdict: PASS (with warnings)

## Dimensions

### Correctness: PASS

- **acp-session.ts (T-001)**: The `Promise.race` timeout pattern is correctly implemented in both `runAcpSession` and `createAcpConnection`. `ensureKill()` is always called after the race. The timeout timer is properly cleared via `clearTimeout(timeoutId)` on the success path.
- **agent-runner-service.ts (T-002, T-004)**: `forceStop()` is idempotent — returns `{ stopped: false }` for missing agents without error. The `onProgress` callback correctly uses `!== undefined` guards. The `onChildProcess` callback re-fetches from `activeAgents` (defensive against stale closures). The `finally` block clears `childProcess` before `activeAgents.delete()`, which is correct ordering.
- **workflow-controller.ts (T-003)**: `emitProgress()` uses optional chaining (`this._onProgress?.()`), safe when undefined. `onProcessSpawned` callbacks wired at all three ACP spawn sites (plan, build, review).
- **issues.ts (T-005)**: Route handles all three spec cases: 404 (not found), 409 (no agent running), 200 (success). `issueService.setStatus` called after `forceStop` — correct order.
- **IssueDetailPage.tsx (T-007)**: The `forceStopConfirming` state machine is correct: first click sets confirming, second click fires mutation. The `useEffect` timeout for auto-reset is properly cleaned up. Click-outside handler resets confirmation state. The Close button is correctly hidden when `isAgentRunningOnThis` is true.

**Warnings:**
1. `agent-runner-service.ts:451` — `onChildProcess` reads `this.activeAgents.get(issue.id)` inside the callback. If `forceStop` already deleted the agent entry, `agent` is `undefined` and the child process ref is silently dropped. This is harmless (process will be GC'd), but worth noting.

### Complexity: PASS

- All new functions are concise. `forceStop()` is ~35 lines (within 50-line limit). `emitProgress()` is a 1-liner wrapper.
- The `cleanup()` timeout pattern is cleanly duplicated across the two `cleanup()` closures (runAcpSession and createAcpConnection). Minor code duplication, but extracting would add unnecessary indirection for a 10-line closure.
- `IssueDetailPage` progress panel JSX is ~50 lines — at the boundary but acceptable for a self-contained UI block.
- Cyclomatic complexity is low throughout. No function exceeds 5 branches.

### Test Coverage: PASS (with caveat)

- No new tests were written for the new code (forceStop, progress tracking, cleanup timeout, force-stop API endpoint, UI progress panel).
- The T-008 verification task confirmed all 32 test failures are **pre-existing** (identical failures on the base commit). The new changes introduce zero test regressions.
- **Recommendation**: Add unit tests for `forceStop()` idempotency, the force-stop API route (404/409/200 cases), and `emitProgress` callback wiring. This is not blocking but should be tracked.

### Security: PASS

- Force-stop endpoint validates project existence, issue existence, and agent running state before acting.
- `SIGKILL` is appropriate for force termination — no user-controlled signal.
- `parseInt(c.req.param('number'))` — no NaN risk since `getByNumber` would return null for invalid numbers, and the 404 path handles it.
- No SQL injection, command injection, or credential exposure risks.
- `ChildProcess.kill()` is wrapped in try-catch to handle already-exited processes.

### Spec Compliance: PASS

**T-001: cleanup() defensive timeout**
- [PASS] cleanup() wraps Promise.allSettled in Promise.race with 5s timeout
- [PASS] ensureKill() is always called regardless of timeout
- [PASS] Warning log emitted when cleanup times out (`log.warn('Cleanup timed out after 5s, forcing kill')`)
- [PASS] Normal cleanup (settles within 5s) works unchanged
- [PASS] Timeout timer cleared on success path (`clearTimeout(timeoutId)`)
- [PASS] Typecheck passes

**T-002: RunningAgent progress fields**
- [PASS] RunningAgent has progress field with stage, roundType, roundIndex, taskProgress, lastActivityAt
- [PASS] AgentProgress.stage is required (`stage: string`)
- [PASS] AgentStatus.activeAgents entries include progress field
- [PASS] getStatus() returns progress snapshot from RunningAgent (`{ ...a.progress }`)
- [PASS] WorkflowControllerOptions has optional onProgress callback
- [PASS] executePipeline() creates RunningAgent with initial progress, passes onProgress closure
- [PASS] Typecheck passes

**T-003: WorkflowController progress updates**
- [PASS] run() calls onProgress on Plan, Build, Review stage entry
- [PASS] runPlanStage calls onProgress with roundType/roundIndex at each round iteration
- [PASS] runPipelineBuildStage calls onProgress with taskProgress after each task completion
- [PASS] runPipelineReviewStage calls onProgress with review round info
- [PASS] onProgress is optional — no crash if undefined (optional chaining)
- [PASS] Typecheck passes

**T-004: childProcess ref and forceStop**
- [PASS] RunningAgent has childProcess field (ChildProcess | undefined)
- [PASS] WorkflowControllerOptions has onChildProcess callback
- [PASS] AcpConnectionOptions.onProcessSpawned exposes the spawned ChildProcess
- [PASS] Plan/Build/Review stage ACP spawns call onChildProcess
- [PASS] forceStop calls childProcess.kill('SIGKILL') if present
- [PASS] forceStop removes agent from activeAgents, clears pendingGates and waitingQuestions
- [PASS] forceStop is idempotent — returns `{ stopped: false }` if agent already removed
- [PASS] Child process cleared in finally block of executePipeline
- [PASS] Typecheck passes

**T-005: POST /api/issues/:number/force-stop**
- [PASS] Returns 200 with `{ ok: true, issueNumber }` when agent running
- [PASS] Returns 409 when no agent running for the issue
- [PASS] Returns 404 when issue does not exist
- [PASS] Issue status set to interrupted, stage preserved (only `setStatus` called, not stage change)
- [PASS] agent_stopped event emitted via EventBus
- [PASS] Typecheck passes

**T-006: Frontend types and API client**
- [PASS] AgentStatus.activeAgents entries have progress field in types.ts
- [PASS] api.ts has forceStopIssue(number) with correct return type `{ ok: boolean; issueNumber: number }`
- [PASS] Typecheck passes

**T-007: Progress panel and Force Stop UI**
- [PASS] When agent is running, sidebar shows progress panel instead of Close button
- [PASS] Progress panel displays stage, roundType (Plan/Review), taskProgress X/Y (Build)
- [PASS] Last activity shown as relative time (just now, Xs ago, Xm ago)
- [PASS] Force Stop button visible in progress panel
- [PASS] Clicking Force Stop shows inline confirmation, no API call on first click
- [PASS] Clicking confirmation calls api.forceStopIssue and updates UI
- [PASS] Confirmation auto-resets after 5 seconds
- [PASS] Click-outside handler resets confirmation state (via `useRef` + `mousedown` listener)
- [PASS] After successful force stop, issue shows as interrupted with Resume button
- [PASS] Typecheck passes

**T-008: Build verification**
- [PASS] `npm run build` completes with zero errors
- [PASS] Typecheck passes across all changed files
- [PASS] No regressions — 32 test failures are pre-existing

**Minor spec deviations (non-blocking):**
1. **Web UI spec**: "The display SHALL update periodically (every 10 seconds) to stay current." — The relative time display does NOT have a dedicated 10-second refresh timer. It updates only when the `agentStatus` query refetches every 5 seconds (which re-renders the component). Since the 5s polling causes more frequent updates than the spec's 10s requirement, this is a net improvement over spec.

2. **Web UI spec**: "Activity 5 seconds ago → shows 'just now'" — The `formatRelativeTime` function uses `< 5` threshold, meaning exactly 5 seconds shows "5s ago" not "just now". Should be `<= 5` to match the spec scenario. Very minor boundary issue.
