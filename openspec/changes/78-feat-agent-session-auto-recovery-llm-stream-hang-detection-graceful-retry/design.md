## Context

Mohist spawns `opencode acp` as a subprocess and communicates via the ACP (Agent Client Protocol) JSON-RPC stream. The core session functions are:

- **`runAcpSession()`** (acp-session.ts:84): single-round session — spawns process, initializes ACP, creates session, sends one prompt, waits for completion. Used by `ralph-executor.ts` for each build task.
- **`createAcpConnection()`** (acp-session.ts:459): multi-round connection — spawns process once, returns an `AcpConnection` object whose `prompt()` method can be called multiple times. Used by Plan and Review stages.

Both functions currently race `connection.prompt()` against a simple timeout promise. When the LLM streaming connection dies silently (no exit, no error), the prompt promise never resolves and the session waits until the overall timeout fires (15–30 minutes). There is no idle-event detection.

**Key constraint**: We cannot modify opencode source code. Recovery must use the existing ACP protocol methods: `cancel({ sessionId })` to interrupt a hung prompt, then `prompt()` to re-send.

## Goals / Non-Goals

**Goals:**
- Detect LLM stream hang within 3 minutes of last ACP event
- Recover gracefully via cancel+prompt (preserving session context) up to 2 times
- Degrade cleanly to kill when recovery is impossible
- Surface recovery status to UI via SSE events
- Classify hang failures in ralph-executor for proper retry decisions

**Non-Goals:**
- Modifying opencode source code
- Implementing multi-provider failover (switching to a different LLM on hang)
- Detecting hangs during ACP initialize/newSession phases (only prompt phase)
- Persisting recovery state across server restarts

## Decisions

### D1: Polling-based idle detection via setInterval

Track `lastEventTime` (updated in every `sessionUpdate` callback), run a `setInterval` that checks `Date.now() - lastEventTime > hangIdleMs` every 30 seconds. When idle threshold is exceeded, resolve the idle-detector promise to trigger recovery.

**Why not `setTimeout` reset pattern**: The `setTimeout` approach (reset a timer on every event) would create/destroy many timers in high-throughput streaming sessions. A `setInterval` polling approach creates exactly one timer per prompt round, checks cheaply, and is naturally robust against event storms.

**Alternatives considered:**
- **setTimeout reset on each event**: Works but creates/destroys hundreds of timers per minute during active streaming. More GC pressure.
- **Watchdog thread via worker**: Unnecessary complexity; setInterval is sufficient for 30-second granularity checks.

### D2: Shared `runPromptWithHangRecovery()` function

Extract the prompt-then-wait-with-recovery logic into a single internal function used by both `runAcpSession` and `createAcpConnection.prompt()`. The function signature:

```typescript
interface PromptWithRecoveryParams {
  connection: ClientSideConnection;
  sessionId: string;
  promptText: string;
  timeoutMs: number;
  hangIdleMs: number;
  maxRecoveryAttempts: number;
  // Context for logging/SSE
  cwd: string;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  sseIssueId: string;
  acpSessionId: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  ensureKill: () => void;
  cleanup: () => Promise<void>;
  // Mutable state references (shared with sessionUpdate callback)
  getLastEventTime: () => number;
  setLastEventTime: (t: number) => void;
  setHasRecovered: (v: boolean) => void;
  getRecoveryAttemptCount: () => number;
}

async function runPromptWithHangRecovery(
  params: PromptWithRecoveryParams
): Promise<AcpSessionResult>
```

**Why not a class**: The current code uses closures extensively (mutable `let` variables shared between the `sessionUpdate` callback and the prompt logic). A class would require restructuring both `runAcpSession` and `createAcpConnection`. A function that accepts getter/setter callbacks for mutable state integrates cleanly with the existing closure pattern.

**Alternatives considered:**
- **Class-based approach**: Would be cleaner long-term but requires significant refactoring of both functions. Not justified for this change.
- **Copy-paste recovery logic into each function**: Duplicating ~100 lines of recovery logic across two already-long functions is unmaintainable.

### D3: Recovery flow — cancel then prompt (not kill then respawn)

When hang is detected:
1. WIP commit (5s timeout via `Promise.race`)
2. `connection.cancel({ sessionId })` (5s timeout)
3. 1s cooldown (`setTimeout`)
4. `connection.prompt()` with recovery hint

**Why**: `cancel()` interrupts the hung LLM call inside opencode without destroying the session state. The subsequent `prompt()` reuses the same ACP session, so the agent retains conversation history. This is much cheaper than kill+respawn (which loses all context).

**Recovery hint message**: `"The previous LLM streaming connection was interrupted (idle for ${idleMs}ms). Your session context is preserved. Please continue from where you left off."`

**Alternatives considered:**
- **Kill then respawn**: Simpler but loses all session context. The agent would start from scratch.
- **Just cancel, no re-prompt**: Leaves the session in a state where ralph-executor sees failure. Better to attempt continuation.

### D4: Detection of successful recovery

After a recovery prompt is issued, the first `sessionUpdate` event received in the `sessionUpdate` callback sets a `hasRecovered` flag. The `runPromptWithHangRecovery` function checks this flag to emit `coder_recovery_status: 'recovered'` and log `acp_session_recovery_succeeded`.

**Why**: The prompt promise from the recovery `connection.prompt()` call resolves when the agent finishes its turn. The `sessionUpdate` callback fires as events stream in. By setting a flag on first event post-recovery, we get immediate confirmation that recovery worked.

### D5: Hang idle timer only active during prompt phase

The `setInterval` idle checker is created when `connection.prompt()` is called and cleared when the prompt promise resolves (or when recovery triggers kill). No idle monitoring during ACP initialize, newSession, or between rounds in multi-round connections.

**Why**: The issue only manifests during active LLM streaming (prompt phase). Initialize and newSession are quick protocol handshakes that either succeed quickly or fail.

### D6: `hang_unrecoverable` failure category — non-retryable

Add `'hang_unrecoverable'` to `FailureCategory` with `{ maxAttempts: 1, retryable: false }`. When `categorizeFailure()` sees `[HANG_UNRECOVERABLE]` in the error string, it returns this category. The ralph-executor loop will pause and ask the user.

**Why non-retryable**: If all 2 internal recovery attempts failed, the LLM provider is likely having persistent issues. Immediate retry by ralph-executor would likely fail the same way. Better to surface to the user.

**Alternatives considered:**
- **Retryable with 1 attempt**: Could work if the issue is transient, but risks wasting another task execution on a consistently broken provider.

## Risks / Trade-offs

**[False positive hang detection during slow tool execution]** → Some tool calls (e.g., large file reads, long test runs) may not emit events for >3 minutes while legitimately working. **Mitigation**: The 3-minute default is conservative; ACP agents typically emit `agent_thought_chunk` events even during long tool execution. The `hangIdleMs` option allows per-call tuning. If false positives prove common, the default can be increased.

**[Cancel may not work on all opencode versions]** → `connection.cancel()` relies on the opencode ACP server handling cancel correctly. **Mitigation**: 5-second timeout on cancel; if it fails, fall back to kill (existing behavior). The recovery is best-effort.

**[Recovery prompt confuses agent context]** → The agent receives a new user message ("session was interrupted") mid-conversation, which could alter its behavior. **Mitigation**: The recovery hint is brief and explicitly instructs the agent to continue, not restart.

**[Timer resources leaked on abnormal exit]** → If the process crashes without going through cleanup, the `setInterval` could leak. **Mitigation**: The interval is stored in a variable and cleared in both the success and error paths of `runPromptWithHangRecovery`. Process exit also naturally cleans up intervals.

## Migration Plan

1. Add `coder_recovery_status` to all 4 event registration arrays (backend + frontend)
2. Add `hang_unrecoverable` to `FailureCategory` and `categorizeFailure()` in ralph-executor
3. Implement `runPromptWithHangRecovery()` in acp-session.ts
4. Refactor `runAcpSession` prompt phase to use the shared function
5. Refactor `createAcpConnection.prompt()` to use the shared function
6. Add frontend SessionTimeline recovery status rendering
7. No database migration needed (recovery events use existing `workflow_log` table with new `event_type` strings)

**Rollback**: If recovery causes issues, set `hangIdleMs: 0` to disable hang detection. All sessions revert to the current timeout-only behavior.

## Open Questions

- **Should we track `hang_unrecoverable` as a new failure category with its own learning adjustments in `generateAdjustmentsFromCategory()`?** Currently the spec says non-retryable (like `dependency`). We could add specific adjustments like "LLM provider may be unstable, consider switching models" but this is a detail for implementation.
