## Context

Mohist's Session page (`packages/web/src/pages/session/ui/SessionPage.tsx`) is read-only. Users can watch agent output but cannot intervene mid-run. The existing `POST /api/issues/:number/messages` endpoint only works when an agent is **paused** at a gate; there is no way to inject a message into a **running** session.

opencode's `runLoop` (`packages/opencode/src/session/prompt.ts:1337-1566`) is a `while(true)` loop that reads the latest messages from its DB at each iteration. When `step > 1`, new user messages are wrapped as `<system-reminder>` and injected into the next LLM request. This means mid-turn message injection is natively supported — Mohist just needs to expose the path.

Key gaps in the current codebase:

- **No API endpoint** for sending messages to a running session (`IssueRoutes.Sessions.cs` has only GET + compact/reset).
- **`RunnerSignalRClient`** (`runner-signalr.ts`) handles only workspace/git RPC. It has **no access** to `AcpSessionManager` or `SharedAcpConnection` — both are owned by `RunnerHost` (`host.ts:40-41`) and lazily initialized.
- **`PromptKind.followup`** is defined in `AgentSessionJsonHelper.NormalizePromptKind` and in the web's `PromptKind` type, but never assigned in any code path.
- **`AgentSessionJsonHelper.StatusName(session)`** derives `"active"` when `AgentRuntimeSessionId is not null && LastDataAt within 5 minutes`; otherwise `"inactive"`. There is no stored terminal-state enum.
- **`RunnerConnectionTracker`** (`RunnerConnectionTracker.cs`) is a simple `ConcurrentDictionary<string, string>` mapping `runnerId → connectionId`, already injected into `RunnerHub`.

## Goals / Non-Goals

**Goals:**
- Let users send free-text messages to a running agent from the Session page
- Messages are injected at the next `runLoop` iteration boundary (after current tool call, before next LLM request) — no cancel, no restart
- Multiple rapid messages converge naturally via opencode's DB
- Session terminal state disables input; runner offline produces a clear error
- Existing `sessionUpdate → grain → SSE` pipeline requires zero new event types

**Non-Goals:**
- Message editing/deletion
- Attachments/image upload
- Agent-to-agent communication (mailbox)
- Voice input
- Stop/steering/stage controls (explicitly excluded by the modified `agent-session-ui` spec)

## Decisions

### D1: SignalR push for server → runner delivery

The server forwards followup messages to the runner via the existing SignalR hub (`ReceiveFollowup`), not via HTTP callback.

**Rationale:** `RunnerConnectionTracker` already maps `runnerId → connectionId`. The runner already maintains a persistent SignalR connection. Fire-and-forget `SendAsync` is the natural fit — the server doesn't need delivery confirmation (the message will surface in the transcript via SSE).

**Alternative considered:** HTTP POST to a runner-scoped endpoint. Rejected — the runner doesn't expose an inbound HTTP server, and adding one would duplicate what SignalR already provides.

### D2: Inject a followup-lookup callback into `RunnerSignalRClient`

`RunnerSignalRClient` currently has no reference to `AcpSessionManager` or `SharedAcpConnection`. Both are owned by `RunnerHost` and `SharedAcpConnection` is lazily initialized (may be null at construction time).

**Decision:** Pass a lookup callback from `RunnerHost` into `RunnerSignalRClient`:

```typescript
type FollowupTargetResolver = (workflowRunId: string, sessionName: string) =>
  { connection: ClientSideConnection; sessionId: string } | null
```

`RunnerHost` provides this closure, which captures `this` and reads `this.sessionManager` + `this.sharedAcpConnection` at call time (by then, the connection is initialized):

```typescript
// In RunnerHost constructor, added as 5th constructor arg:
this.signalR = new RunnerSignalRClient(
  options.serverUrl, options.runnerId, options.runnerRoot, this.buildGitHash,
  (wrId, name) => {
    const entry = this.sessionManager.get(this.sessionManager.key(wrId, name))
    if (!entry || !this.sharedAcpConnection) return null
    return { connection: this.sharedAcpConnection.connection, sessionId: entry.sessionId }
  },
)
```

**Alternative considered:** Constructor injection of `AcpSessionManager` + `SharedAcpConnection`. Rejected — `SharedAcpConnection` is null at construction time and would require a mutable setter or nullable dance.

### D3: Active-session guard via `StatusName`

The followup API returns 409 when the session is not active. The server uses `AgentSessionJsonHelper.StatusName(session)` as the guard: `"active"` passes, anything else returns 409.

**Rationale:** `StatusName` returns `"active"` when `AgentRuntimeSessionId is not null && LastDataAt within 5 min`. During normal operation, liveness probing keeps `LastDataAt` fresh, so legitimately running sessions pass. Completed/failed/stale sessions naturally become `"inactive"`.

**Alternative considered:** Check only `AgentRuntimeSessionId is not null` (weaker guard). Rejected — a session that died 30 minutes ago but was never unbound would incorrectly accept the followup. The 5-minute window is a reasonable freshness signal.

### D4: New `ResolveFollowupTargetAsync` on `AgentSessionQuerier`

The followup endpoint needs `runnerId`, `workflowRunId`, `sessionName`, and active status from a `(projectId, issueNumber, sessionName)` triple. The existing `ResolveIssueSessionIdAsync` returns only the session ID. `GetSessionMetadataAsync` deliberately omits `runnerId`/`workflowRunId`.

**Decision:** Add a new lightweight method that reuses `FindCurrentSessionAsync` (which already loads the full `AgentSessionRecord` with `Row.RunnerId` and `WorkflowRunId` label) and returns a `FollowupTarget?` record:

```csharp
public sealed record FollowupTarget(
    string RunnerId, string WorkflowRunId, string SessionName, bool IsActive)
```

This avoids loading transcript data (unlike `GetSessionMetadataAsync`) — the followup path never needs transcript content.

### D5: Fire-and-forget `connection.prompt()` in the `ReceiveFollowup` handler

The runner's `ReceiveFollowup` handler calls `connection.prompt({ sessionId, prompt: [{ type: "text", text }] })` and **does not await** the promise. The `.catch(() => {})` swallows rejections silently.

```typescript
this.connection.on("ReceiveFollowup", (payload: { workflowRunId: string; sessionName: string; text: string }) => {
  const target = this.resolveFollowupTarget(payload.workflowRunId, payload.sessionName)
  if (!target) return
  target.connection.prompt({
    sessionId: target.sessionId,
    prompt: [{ type: "text", text: payload.text }],
  }).catch(() => {})
})
```

**Rationale:** opencode's `Runner.ensureRunning()` blocks the second `prompt()` caller (state is `"Running"`) — the message is written to DB by `createUserMessage()` before the block. The running `runLoop` picks it up at the next iteration boundary. No queue needed.

### D6: `followup` PromptKind tagging

When the `ReceiveFollowup` handler calls `connection.prompt()`, the resulting transcript turn must be tagged with `PromptKind: "followup"`. The handler SHALL emit a session event (via the existing `ServerConnection` event-reporting path used by `acp-agent.ts`) carrying `promptKind: "followup"` metadata **before** calling `connection.prompt()`. This lets the server's `TranscriptAccumulator` open a new turn with the correct kind before streaming output arrives.

The exact event name and payload shape depend on how `acp-agent.ts` currently signals prompt-start to the server. During implementation, trace the `emitSessionEvent` calls in `monitorPrompt` and mirror the pattern with `promptKind: "followup"`.

### D7: Web composer placement

A `SessionFollowupComposer` component is rendered at the bottom of `SessionPage`'s return (after the transcript scroll container, before `JumpToBottomButton`):

```tsx
<div className="flex-1 overflow-y-auto"> ...transcript... </div>
<SessionFollowupComposer
  issueNumber={issueNumber}
  sessionName={routeSessionKey}
  disabled={!isRunning}
/>
{newContentAvailable && <JumpToBottomButton ... />}
```

The composer uses a `useMutation` hook calling a new `postFollowup(number, name, text, projectId)` API client function. No optimistic transcript insertion — the message surfaces via SSE when opencode processes it.

## Risks / Trade-offs

- **[5-minute active window rejects valid long operations]** → Mitigation: liveness probing in `acp-agent.ts` sends periodic probe prompts that update `LastDataAt`, keeping active sessions fresh. If a session legitimately exceeds the quiet threshold, the liveness system would already be probing/recovering it.
- **[Fire-and-forget means no delivery confirmation]** → Mitigation: the user sees the agent's response to the followup via SSE. If the message is silently dropped (unknown session, crashed process), the transcript simply doesn't change — the user can resend. This is acceptable for a steering input, not a command-critical path.
- **[SignalR disconnect loses in-flight followups]** → Mitigation: the server checks `RunnerConnectionTracker` connectivity before pushing and returns 503 if offline. If the connection drops between the check and the push, the message is lost — but the user will see no transcript change and can resend.
- **[PromptKind tagging mechanism unverified]** → Mitigation: D6 outlines the approach but the exact event plumbing needs tracing during implementation. If event-based tagging proves complex, fallback: tag the turn client-side by detecting the `<system-reminder>` wrapper in the sessionUpdate stream.

## Migration Plan

**Deploy:** This is purely additive — no existing endpoints, events, or UI behaviors change. The three layers (server endpoint, runner handler, web composer) can ship together.

**Rollback:** Remove the followup endpoint from `IssueRoutes.Sessions.cs`, remove the `ReceiveFollowup` handler from `runner-signalr.ts`, and remove the composer component from `SessionPage.tsx`. No database migration needed — `PromptKind.followup` already exists in the schema and normalizer.

## Open Questions

- **PromptKind event plumbing**: D6 assumes the runner can emit a prompt-start event with `promptKind` metadata before calling `connection.prompt()`. Need to verify the exact mechanism in `acp-agent.ts`'s `emitSessionEvent` / `ServerConnection.workflowAgentSessionRuntimeEvents` path during implementation.
- **Composer UX details**: Should the composer show a "delivery failed" toast on 409/503, or just disable + show inline error? Decide during web implementation based on existing error patterns in the session page.
