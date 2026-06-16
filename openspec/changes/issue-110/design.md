## Context

ACP agent sessions silently fail when the context window fills up. The current system forwards `usage_update` notifications with `contextWindowSize` and `contextWindowUsed` from opencode to the session grain, but does nothing with this data beyond persistence. There is no compaction mechanism, no health visibility in the UI, no failure classification for context exhaustion, and no manual recovery path. Users get misleading errors like "missing artifact file" when the root cause is context exhaustion.

**Current data flow**: `opencode ACP` → `usage_update` notification → `acp-agent.ts:buildUsageUpdatePayload()` → `emitSessionEvent(USAGE_UPDATED_EVENT)` → server `AgentSessionGrain.ApplyUsage()` → persists `contextWindowUsed`/`contextWindowSize` on the session → frontend DTOs include these fields (already present on `CoderSessionSummary`, `SessionMetadata`, etc.) → **no UI renders them**.

**Constraints**:
- The ACP provider is opencode; compaction support depends on opencode's capabilities
- Agent sessions are process-level — compaction cannot happen while a session is actively executing
- Sessions are per-workflow-attempt; retry/re-run creates fresh sessions

## Goals / Non-Goals

**Goals:**
- Make context window usage visible in session pages (usage bar, color coding, warnings)
- Provide manual Compact and Reset actions for recovery
- Classify context exhaustion failures so errors are actionable
- Validate session health before workflow retry
- Pass compaction configuration to opencode sessions where supported

**Non-Goals:**
- Automatic workflow retry on context exhaustion (user decides)
- Multi-session context sharing (each task uses its own session)
- Context persistence across workflow runs
- Implementing opencode-level compaction natively (we configure; opencode executes)

## Decisions

### 1. Compact = Close existing session + open new session with summary

**Decision**: Implement compaction on the Mohist side by closing the existing ACP session and opening a new one initialized with a compacted summary of previous context. This does not require opencode to support compaction natively.

**Rationale**: The opencode ACP protocol has no `compact` notification. Rather than waiting for protocol changes, we orchestrate it from the runner/session manager. The summary is composed from:
- Current task instructions/messages (these must survive)
- Key decisions and artifact outputs from the session
- Error context if the session was in a failure state

This matches the issue's design notes: _"Close current session, Open new session with summary of previous context"_.

**Alternatives considered**:
- Wait for opencode ACP protocol support → blocks the feature indefinitely
- Pass a compact instruction as an agent message → unreliable, consumes tokens for the instruction itself
- Truncate conversation history server-side → could break opencode's message cache assumptions

### 2. Reset = Close + fresh session (no summary)

**Decision**: Reset closes the existing ACP session and opens a new one with only the system prompt. The old session transcript is preserved as historical evidence; the new session reuses the same logical session reference (`agentSessionRef`).

**Rationale**: Simpler than compact — no summarization logic needed. The old session becomes a historical record. The workflow continues using the new session for subsequent task execution.

### 3. Compaction and Reset are post-session operations

**Decision**: Compact and Reset are only available when the session is NOT actively running. The UI disables both buttons during active execution.

**Rationale**: The ACP session process is owned by opencode. We cannot interrupt a running session to compact it safely. After the session completes or fails, the user can compact/reset before retrying.

This means the recovery flow is:
1. Session fails (possibly with context exhaustion)
2. Error message shows "Context window exhausted. Compact or Reset the session."
3. User navigates to session page → clicks Compact or Reset
4. Retry now works with healthy context

### 4. Session health is evaluated at retry dispatch time

**Decision**: Before dispatching a retry, the workflow engine checks the associated session's `contextWindowUsed / contextWindowSize` ratio. Three tiers apply: below 80% — retry proceeds normally; 80-90% — a warning is logged but retry proceeds (user is close to exhaustion but not yet blocked); above 90% — retry is rejected with guidance to compact or reset first.

**Rationale**: Batching the health check into the retry path keeps the workflow engine clean. Checking at every task dispatch would add latency and complexity for the normal (healthy) path. The 90% hard-block threshold vs 80% warn threshold gives users a gradual degradation path — they get a heads-up before they are completely blocked.

### 5. Context exhaustion is classified from session close events

**Decision**: When a session closes with status `failed` and the last known `contextWindowUsed / contextWindowSize` ratio exceeds 90%, the failure is classified as `context_exhaustion`. Additionally, if a session completes in under 10 seconds without producing expected output AND usage was >85%, it's flagged as suspected exhaustion.

**Implementation**: Add a classification step in the session close handler (`AgentSessionGrain.cs`) that inspects the final context metrics and sets `failureCategory = "context_exhaustion"` when conditions are met.

**Note**: The existing `failureCategory` field on session close events (`session.closed` payload already includes `failureCategory?: string | null`) means we can add this classification without schema changes.

### 6. New session recovery endpoints follow the existing session route pattern

**Decision**: Add `POST` endpoints under the existing issue session route group:
- `POST /api/projects/{ref}/issues/{number}/sessions/{name}/compact`
- `POST /api/projects/{ref}/issues/{number}/sessions/{name}/reset`

Both map to a new `IAgentSessionGrain` method and are gated by session activity checks.

**Rationale**: Follows the existing REST pattern (`IssueRoutes.Sessions.cs`). Separates compact/reset from the transcript/list GET endpoints logically.

### 7. Context health UI reuses existing SSE event infrastructure

**Decision**: New SSE events (`compaction_event`, `context_health_update`) flow through the existing `TranscriptEventPublisher` pattern used by `AgentSessionGrain`. Frontend consumes them via the existing `dispatchAgentEvent` / `onAgentEvent` mechanism.

**Rationale**: The SSE pipeline already supports emitting arbitrary typed events. Adding two new event types requires:
- Server: register the event in the `EventBus` / transcript publisher
- Frontend: add to event type registries (`events.ts`, `agent-events.ts`, `useSSE.ts`)

No new infrastructure needed.

### 8. Compact event is a first-class session event type

**Decision**: Compaction events (`compaction`) are persisted as `session_stream_log` rows (like tool calls and text chunks) and rendered in the SessionTimeline as info banners. They are NOT full conversation turns.

**Rationale**: Users need to see when compaction happened for debugging, but compaction isn't a "prompt → response" conversation step. Rendering as a compact info banner (e.g., "Context compacted: 950K → 400K tokens") keeps the transcript readable.

## Architecture

### Data Flow: Auto-Compact (if opencode supports it)

```
opencode [compact triggers] → ACP notification
  → acp-agent.ts: buildUsageUpdatePayload() [extract compact event fields]
  → emitSessionEvent("usage.updated", { ...compaction fields })
  → AgentSessionGrain.ApplyUsage() [update contextWindowUsed post-compaction]
  → TranscriptEventPublisher [emit "compaction_event" SSE to Web UI]
  → Frontend: update usage bar, show compaction timeline entry
```

### Data Flow: Manual Compact

```
Web UI [Compact button click]
  → POST /api/.../sessions/{name}/compact
  → AgentSessionGrain.CompactAsync()
    → Close existing ACP session (if open)
    → Build summary from session messages
    → Open new ACP session with summary as initial context
    → Update coder session record (new acpSessionId)
    → Persist compaction event in stream log
  → Response: { contextWindowSize, contextWindowUsed, contextUsagePercent }
  → Frontend: refresh session page with updated metrics
```

### Data Flow: Context Exhaustion Detection

```
acp-agent.ts [session closes with failure]
  → emitSessionEvent("session.closed", { status: "failed", ... })
  → AgentSessionGrain.RecordTerminalStatus()
    → Check: contextWindowUsed / contextWindowSize > 0.9?
    → Yes → set failureCategory = "context_exhaustion"
    → No  → keep existing failureCategory (probe_timeout, etc.)
  → Frontend: show "Context window exhausted (94%)" with Compact/Reset suggestions
```

### Session Recovery State Machine

```
                 Compact
  [Healthy] ───────────────→ [Healthy, lower usage]
       ↑                          │
       │ Retry succeeds           │
       │                          ▼
  [Failed: context_exhaustion] ← [Exhausted, >90%]
       │                          ▲
       │                          │
       └── Reset ─────────────────┘
```

## Component Changes

### Backend (Server + Runner)

| Layer | File | Change |
|---|---|---|
| Runner | `acp-agent.ts` | Add `compaction` field to `AgentConfig`; pass to ACP connection options |
| Runner | `acp-agent.ts` | Add compaction event extraction from ACP notifications |
| Server | `AgentSessionGrain.cs` | Add `CompactAsync()`, `ResetAsync()` grain methods |
| Server | `AgentSessionGrain.cs` | Add context exhaustion classification in close handler |
| Server | `AgentSessionGrain.cs` | Add `compaction` event type to transcript publisher |
| Server | `IssueRoutes.Sessions.cs` | Add `POST .../compact` and `POST .../reset` endpoints |
| Server | `AgentSessionReadModels.cs` | Add `ContextUsagePercent` to DTOs |
| Server | `WorkflowGrain.cs` | Add session health check before retry |
| Server | `WorkflowRun.Failure.cs` | Accept `context_exhaustion` as a recognized failure reason |
| Server | `Shared.cs` | Add `ContextExhaustion` to `FailureReason` enum (or use string-based `failureCategory`) |

### Frontend

| Layer | File | Change |
|---|---|---|
| Types | `coder-session/model/types.ts` | Add `contextUsagePercent` to relevant interfaces |
| Types | `agent/model/types.ts` | Add `compaction_event` and `context_health_update` to `AgentDetailEventMap` |
| Events | `agent/model/events.ts` | Register new event types |
| API | `coder-session/api/client.ts` | Add `compactSession()`, `resetSession()` functions |
| Widget | `SessionTimeline.tsx` | Render compaction timeline entries |
| Widget | `useSessionTimeline.ts` | Handle compaction events in round reconstruction |
| Widget | New: `ContextHealthBar.tsx` | Usage bar with color coding |
| Widget | New: `SessionRecoveryActions.tsx` | Compact/Reset buttons + confirmation dialog |
| Page | Session detail page | Integrate `ContextHealthBar` and `SessionRecoveryActions` |
| Page | Session list views | Show compact health indicators per session |

## Risks / Trade-offs

- **[Risk] Open a new ACP session for compact/reset reuses the runner process** → The `AcpSessionManager` already supports multiple sessions per shared connection. Compact/reset closes the old session handle and opens a new one through the same `SharedAcpConnection`.
- **[Risk] Summary-based compact may lose critical context** → Mitigation: the summary explicitly preserves task instructions, key decisions, error messages, and session memories. The compact action is user-initiated (manual) or threshold-triggered (auto), so the user has agency.
- **[Risk] Reset deletes conversation history irreversibly** → Mitigation: confirmation dialog with clear warning. The old session transcript remains in the database as historical evidence.
- **[Risk] Context window metrics may be stale at retry-check time** → Mitigation: `usage_update` events are among the most frequent ACP notifications. The 5-minute liveness quiet threshold means data should be fresh within 5 minutes. If no data exists, assume healthy (don't block).
- **[Risk] `context_exhaustion` classification may have false positives** → Mitigation: only classify when usage >90% AND session failed. If the session completed successfully with high usage, it's not exhaustion. The rapid-completion heuristic (>10s without output) is a secondary signal, not the primary classifier.

## Open Questions

1. **Does opencode support native compaction?** If yes, we can forward config directly. If no, we implement the close+new-session approach described above. The design supports both paths — the runner forwards config, and the server implements the fallback orchestration.

2. **What exactly goes into the compaction summary?** We need to decide the summarization algorithm. Candidate approach: extract the last N user/assistant message pairs that contain task instructions and key decisions, plus session memory insights. This can be refined iteratively.

3. **Should we show context health on the Kanban/board view?** The specs scope this to session pages and workflow views. Board-level indicators could be a follow-up if context exhaustion becomes a common failure mode visible from issue cards.

4. **What is the exact token threshold for different models?** opencode sessions may use models with different context window sizes (Claude: 200K, GPT: 128K, etc.). The 80% warning threshold and 90% exhaustion threshold are model-agnostic percentages. This should work across models.
