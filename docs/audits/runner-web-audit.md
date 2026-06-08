# Runner + Web Audit

> Scope: `packages/runner/src/**` and `packages/runner/tests/**`, `packages/web/src/**` and `packages/web/tests/**`, and the cross-package contract (TS runner ↔ SignalR hubs ↔ Web envelope parsing).
>
> Audit method: read every TS file in scope in full, traced `RunnerHost` lifetime + SignalR `useEventsConnection` + `LiveTaskProvider.handleEvent` via `codegraph_context` / `codegraph_search` / `codegraph_callers` / `codegraph_explore`. Cross-checked the C# wire shape (`Events/Hub/EventBridge.cs`, `CloudEventEnvelope` record) against the TS `CloudEventEnvelope` interface in `LiveTaskProvider.tsx`. Confirmed runner test suite green (165/165) and web test suite state (789/796 — 7 pre-existing failures, see §10).
>
> Out of scope (audited by other agents): .NET server, C# event bus, Orleans grain state machine, REST API surface, issue domain rules.

## Summary table

| # | Severity | Title | File |
|---|----------|-------|------|
| 1 | **P0** | `rebase_started` / `rebase_progress` events from the server are dropped on the Web; `dispatchRebaseEvent` is dead code so `WorktreePanel` never sees rebase step updates | `app/providers/LiveTaskProvider.tsx:126-140, 211-228` + `entities/issue/model/rebase-events.ts:7-11` + `widgets/worktree/ui/WorktreePanel.tsx:43-67` |
| 2 | **P0** | `unwrapEnvelope` duck-types on `'payload' in rawData`; any future server event whose payload contains a `payload` field would be silently mis-interpreted (field lost) | `app/providers/LiveTaskProvider.tsx:37-49` |
| 3 | **P0** | Runner `executeAndReport` `report()` call on shutdown is un-retryable: when SIGINT/SIGTERM aborts the work, the runner cannot tell the server, and the lease has to time out before re-dispatch | `runtime/host.ts:169-176` |
| 4 | **P1** | `AcpSessionManager` cache is reset on every reconnect (host.ts:86), forcing every work item to re-call `ensureWorkflowAgentSession` + `resumeSession` after a transient SignalR drop | `runtime/host.ts:78-87` |
| 5 | **P1** | Runner has no `uncaughtException` / `unhandledRejection` handlers; a single unhandled promise rejection (Node ≥ 18) kills the runner process | `cli.ts:1-33` + `runtime/host.ts:42-59` |
| 6 | **P1** | `initializeSharedConnection` re-tries on every reconnect cycle with no backoff and no circuit breaker; if the opencode binary is missing/broken, the loop spams `console.error` once per `pollIntervalMs` forever | `runtime/host.ts:61-76, 42-59` |
| 7 | **P1** | `LiveTaskProvider.handleEvent` is a 200-line `switch` with no shared "invalidate issues" / "invalidate agent-activity" helper; many cases duplicate `queryClient.invalidateQueries({ queryKey: ['issues'] })` and miss the agent-activity invalidation they should do | `app/providers/LiveTaskProvider.tsx:142-294` |
| 8 | **P1** | The `EventCallback` on the Web (`events-hub.ts:17`) types `data: unknown` and the only consumer unwraps it; there is no test that asserts the full server→hub→web round-trip envelope shape (id, source, specVersion, extensions) | `shared/api/events-hub.ts:17, 30-32` + `tests/live-task-cloud-event.test.tsx:1-34` |
| 9 | **P1** | Runner SignalR `GetDiff` / `GetCommits` / `GetWorktreeStatus` / `GetFileContent` handlers have **no unit tests**; only the pure `resolveWorkspaceQuery` resolver is tested | `server/runner-signalr.ts:26-138` + `tests/runner-signalr.spec.ts:1-28` |
| 10 | **P1** | Runner process-leak risk on reconnect: `initializeSharedConnection` writes `this.sharedAcpConnection = ...` then `shutdownSharedConnection` does `this.sharedAcpConnection = null`, but if `createSharedAcpConnection` rejects mid-init the partially-constructed `ClientSideConnection` may be left with a live child process | `runtime/acp-connection.ts:49-131` + `runtime/host.ts:61-87` |
| 11 | **P2** | `acp-agent.ts:runAcpWorkflowAgentSession` calls `ensureWorkflowAgentSession` twice on the shared path (once unconditionally at line 493, once again at line 504). Result of the first call is discarded. Wasted server round trip per work item | `actions/acp-agent.ts:487-526` |
| 12 | **P2** | `LiveTaskProvider` runs a `setInterval(500ms)` while a `ralph_task_update` is in `started` state, calling `setActiveTaskElapsedMs` every tick. All consumers of `LiveTaskContext` re-render every 500ms for the entire active-task duration | `app/providers/LiveTaskProvider.tsx:107-124, 310-311` |
| 13 | **P2** | `LiveTaskProvider.handleEvent` is *not* memoized by `useCallback` deps that include the active event-name. Every event allocation creates a new `onEvent` reference; the inner `connection.on('OnEvent', ...)` is in a `useEffect([projectId])` so the listener is only re-registered on project change, but the `callbackRef` is — a stale closure risk if a future refactor stops using the ref | `shared/api/events-hub.ts:19-43` + `app/providers/LiveTaskProvider.tsx:98-302` |
| 14 | **P2** | `unwrapEnvelope` returns `{}` for null/undefined or for a non-object `payload`. Switch-case consumers then run `parsed as EventMap['comment_added']` and crash with unhelpful "Cannot read property 'issueId' of undefined" if the event handler is ever reached with an empty envelope | `app/providers/LiveTaskProvider.tsx:37-49, 295-297` |
| 15 | **P2** | Runner does not validate the `projectId` env var at startup; if it is missing, every `ensureWorkflowAgentSession` / `attachWorkflowAgentSession` / `workflowAgentSessionEvents` call hits the server with a project-less URL and the server will reject every event send | `cli.ts:12` + `server/connection.ts:37-58` |
| 16 | **P2** | `runner-signalr.ts:GetDiff` / `GetCommits` / `GetWorktreeStatus` / `GetFileContent` invoke `git` without any bound on result size; a large `git diff` for a long-lived issue could exceed the SignalR message-size limit and trigger a hub-protocol error | `server/runner-signalr.ts:26-138` |
| 17 | **P2** | Web bundle is 2.7MB (507KB gz); `@microsoft/signalr` (3.3MB unpacked) and `lucide-react` (39MB unpacked) are the biggest individual contributors. Tree-shaking is on, but `lucide-react` is imported by 18 files; verify it's tree-shaking the per-icon path | `package.json:18, 23-31` + `dist/assets/index-nAaugI9I.js:2,714.73 kB` |
| 18 | **P2** | `runner-signalr.ts` registers handlers in the constructor, before `.start()`. If `start()` rejects, the handlers are still attached to a never-started connection. On `.start()` retry, the *same* connection is reused but the handlers were registered only once; OK today but the constructor pattern is fragile | `server/runner-signalr.ts:7-15, 25-139` |
| 19 | **P3** | Stale build artifact `packages/runner/dist/runtime/session-pool.js` (no source counterpart) ships in the published `dist/` because `tsc` does not clean before build; harmless at runtime (no import) but bloats installer | `packages/runner/dist/runtime/session-pool.js:1-41` |
| 20 | **P3** | `runner-signalr.ts` uses raw `console.log` / `console.error`; no structured logger; no log level configuration | `runtime/host.ts:46, 51, 72, 74, 100, 163, 173, 191` + `server/runner-signalr.ts` (no logging) |
| 21 | **P3** | `LiveTaskProvider` monkey-patches `window.history.pushState` / `replaceState` globally in a `useEffect` with `[]` deps; the cleanup restores the originals. Brittle if a future component also monkey-patches (order-dependent); should live in a dedicated hook + singleton | `app/providers/LiveTaskProvider.tsx:68-88` |
| 22 | **P3** | `LiveTaskProvider.handleEvent` is wrapped in a single `try { ... } catch { // ignore malformed events }`; an exception in the early `ralph_task_update` branch (e.g., `setActiveTaskElapsedMs(0)`) will swallow subsequent `queryClient.invalidateQueries` calls for the same event | `app/providers/LiveTaskProvider.tsx:100-297` |
| 23 | **P3** | `work-executor.ts` is the file name the audit prompt mentions, but the actual file is `runtime/executor.ts`. The audit prompt also references `runtime/session-manager.ts` (does not exist) and `runtime/index.ts` (does not exist); the runner entry point is `cli.ts`. No functional issue, just stale file references in the prompt | — |

---

## 1. RunnerHost lifetime

`runtime/host.ts` (212 lines) is the entry point. The `RunnerHost` class owns:

- `ServerConnection` (HTTP polling: `register`, `heartbeat`, `poll`, `report`, `unregister`)
- `RunnerSignalRClient` (SignalR push channel: `GetDiff`, `GetCommits`, `GetWorktreeStatus`, `GetFileContent`)
- `WorkspaceManager` (git worktree handling)
- `AcpSessionManager` (cached `(workflowRunId, sessionName) → { sessionId, workDir }`)
- `SharedAcpConnection` (single long-lived opencode ACP child process; per-host after the 197ea02477 refactor)

The lifetime loop in `run(signal)` (lines 42-59):

```
while (!signal.aborted) {
  connectRunner(signal)         // HTTP register + SignalR start
  initializeSharedConnection(signal)  // spawn opencode ACP
  start heartbeat interval
  try {
    runWorkerPool(signal)       // poll + execute + report
  } catch (error) {
    delay(pollIntervalMs, signal)  // reconnect
  } finally {
    clearInterval(heartbeat)
    shutdownSharedConnection()  // close opencode + reset sessionManager
    shutdownConnection()        // HTTP unregister + SignalR stop
  }
}
```

The "Step 10 of design/event-mechanism.md" comment on lines 23-27 makes the intent explicit: the manager + shared connection are *per-host* now, not per-work-item. This is a real correctness win: `runPromptOnExistingWorkflowAgentSession` (acp-agent.ts:528) only fires when `manager.get(key).sessionId === serverRecord.acpSessionId`, and a fresh opencode process loses its in-memory session, so re-using the manager across work items is exactly the right granularity.

### What works
- Per-host opencode process (one child per runner, not per work item).
- AcpSessionManager persists across work items within a single connection cycle.
- Cleanup is symmetric: `shutdownSharedConnection` calls `acpConnection.shutdown()` which kills the child (5s SIGTERM→SIGKILL).
- `runWorkerPool` `Promise.allSettled(inFlight.values())` correctly waits for in-flight work before tearing down.

### What is broken
- **Reconnect resets the cache** (P1-4): `shutdownSharedConnection` does `this.sessionManager = new AcpSessionManager()` (line 86). After a transient SignalR blip, every subsequent work item re-`ensureWorkflowAgentSession` + `runResumedWorkflowAgentSession` against a *new* opencode process whose internal session state is empty. The server returns the same `acpSessionId` it always did, so the resume call lands on a different opencode process that has never seen that session — depending on opencode's ACP implementation, the resume either silently creates a new session (cache poison: now `manager.set(key, { sessionId: 'foo', workDir })` is wrong) or errors out. The "cache stays warm across reconnect" promise the design comment makes is half-true: the field is per-host, but the contents are reset on every reconnect.
- **Reconnect race in `initializeSharedConnection`** (P1-6): if `createSharedAcpConnection` throws (e.g., opencode binary missing), the catch logs and returns. `this.sharedAcpConnection` remains `null`, so the next reconnect cycle calls `initializeSharedConnection` again, which tries again, which fails again, logging once per `pollIntervalMs` (default 1s) forever. No exponential backoff, no circuit breaker, no "shared ACP unavailable" state that suppresses retries for a while.
- **Mid-init process leak** (P1-10): `createSharedAcpConnection` `spawn()`s the child *before* registering `proc.on("error", ...)` and `proc.on("exit", ...)`. If the spawn fails synchronously (e.g., the binary path is invalid and Node throws `ENOENT` from the `spawn` call itself before returning), the `ClientSideConnection` is never created but the caller has no handle to clean up the child. The current code path is OK because Node defers `ENOENT` to the `error` event, but the order of operations leaves a small window where an exception between `spawn` and `proc.on(...)` would leak a zombie child.
- **Shutdown report failure is unrecoverable** (P0-3): `executeAndReport` (lines 140-176) uses the same `signal` for `executor.execute` and `connection.report`. When `signal.aborted`, both throw. The catch block tries to `report(work, { status: "failed", message: String(error) }, signal)`, but that fetch also fails because the signal is aborted. The result is two console errors, no server report, and the server's lease has to time out before the work is re-queued. The fix is a separate "best-effort drain" path: capture the work's last known result *before* the abort propagates, and use a *fresh* AbortSignal (or no signal) for the final report.
- **No `uncaughtException` / `unhandledRejection` handlers** (P1-5): the runner process exits hard on any unhandled rejection (Node ≥ 18 default). One stray throw in a test-instrumented `setAcpProcessFactoryForTest` override, or a single missing `.catch` on an `await`, kills the runner. Process supervisors (systemd) will restart it, but in-flight work is lost.

---

## 2. SignalR client (web)

`shared/api/events-hub.ts` (44 lines) — single `useEventsConnection(projectId, onEvent)` hook used by `LiveTaskProvider`. Build uses `withAutomaticReconnect` with exponential backoff `min(1000 * 2^n, 30000)`.

### What works
- Exponential backoff capped at 30s.
- `connection.on('OnEvent', ...)` uses a `callbackRef` so a new `onEvent` does not re-register the listener.
- `useEffect` cleanup calls `connection.stop()`.

### What is missing
- **Reconnect handler does nothing explicit**: when `withAutomaticReconnect` triggers, the SignalR client transparently reconnects. Any in-flight REST request is unaffected. The state is fine, but there is no `connection.onreconnecting` / `connection.onreconnected` hook to surface a "live updates paused" toast in the UI. P3.
- **In-flight query state survives disconnect**: TanStack Query retries on its own (`retry: 1` in `main.tsx:11`). The 1-retry budget is tight for a real reconnect — `retry: 3` or `retryDelay` would be more forgiving. P3.
- **No message-queue / bounded buffer for the events that arrive during disconnect**: SignalR is push-based, so anything sent during the disconnect window is lost (server-side `EventBridge` already broadcast them; the client was just not subscribed). This is correct behavior for fire-and-forget events, but the user sees stale UI until a mutation or refetch. P3 (acceptable trade-off).

---

## 3. WorkExecutor / shared cross-task state

`runtime/executor.ts` (149 lines) dispatches one `WorkItem` to one `ActionHandler`. The `WorkExecutor` is now constructed once per host and holds the `AcpSessionManager` + `SharedAcpConnection` (since the 197ea02477 refactor). This **fixes the previous cross-task state bug** where the per-work-item ephemeral path would create a fresh ACP process every time, never re-using a session.

### What works
- Shared `acpSessionManager` and `acpConnection` are passed through `baseContext` (line 100-102) so every action that needs them gets the same instance.
- `runPromptOnExistingWorkflowAgentSession` (acp-agent.ts:528) checks `manager.get(key).sessionId === serverRecord.acpSessionId` and reuses the opencode session.
- `runResumedWorkflowAgentSession` (acp-agent.ts:593) uses `connection.resumeSession` for the cross-process resume case.
- Timeouts are respected: `DEFAULT_TIMEOUT_MS = 30 * 60 * 1000` (acp-agent.ts:36) is the per-prompt wall clock; `monitorPrompt` (acp-agent.ts:885) races it via `Promise.race([promptOutcome, timeout(remainingMs), aborted, exitFailure])`.
- Cancellation: `aborted(context.signal)` race in the same `Promise.race`. On abort, `cancelAndReturn` (acp-agent.ts:1008) sends `connection.cancel({ sessionId })`.

### What is wrong
- **Double `ensureWorkflowAgentSession` call** (P2-11): `runAcpWorkflowAgentSession` (acp-agent.ts:492-500) always calls `ensureWorkflowAgentSession` once before the conditional block; the conditional block (lines 502-523) calls it again and uses the result. The first call's response is discarded. This doubles the server round-trip per work item on the shared path.
- **Shared connection death not detectable** (related to P1-10): `monitorPrompt` receives `exitFailure ?? new Promise<never>(() => {})` from the shared path, so a `proc.on('exit')` on the shared opencode child does **not** unblock the prompt race. The SDK's `connection.prompt` may hang forever if the opencode process dies mid-prompt. The liveness probe saves the user eventually (`DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5min`), but a work item can sit for 5 minutes before recovery.

---

## 4. Error handling in runner

- **Unhandled promise rejections** (P1-5): none of the top-level `await` sites are protected by a top-level `try/catch` that runs `process.exit(1)`. `run()` itself has `while (!signal.aborted)` and a `try/catch` around `runWorkerPool`, but anything *outside* that (e.g., `connectRunner` failure, `shutdownConnection` race) propagates to the Promise from the top-level `await` in `cli.ts:17`. The process exits with a non-zero code, leaving no log line about why.
- **Crash recovery**: the runner has no persistent checkpoint of in-flight work. If it crashes mid-prompt, the server's lease timeout (5min, see `workflow-domain-audit.md` P2-14) re-queues the work. The runner loses `AcpSessionManager` cache, every resumed session on the next start is a cold `newSession`, and the opencode process is gone. **Acceptable** for now (lease-based recovery) but worth tracking.
- **Logging**: 9 `console.log` / `console.error` sites, all raw. No log level, no structured fields, no rotation. The signalR handlers in `runner-signalr.ts` log nothing on errors (P3-20). P3.

---

## 5. Web envelope parsing (`unwrapEnvelope`)

`app/providers/LiveTaskProvider.tsx:37-49` defines:

```ts
function unwrapEnvelope(rawData: unknown): Record<string, unknown> {
  if (rawData && typeof rawData === 'object' && 'payload' in (rawData as object)) {
    const payload = (rawData as CloudEventEnvelope).payload
    if (payload && typeof payload === 'object') {
      return payload as Record<string, unknown>
    }
    return {}
  }
  if (rawData && typeof rawData === 'object') {
    return rawData as Record<string, unknown>
  }
  return {}
}
```

### P0-2: duck-typing on `'payload' in rawData` is fragile

The discriminator is the presence of a `payload` key. Any future server event whose payload *itself* contains a `payload` field (totally legal CloudEvents JSON) will be mis-interpreted: `unwrapEnvelope` will return the *inner* payload, dropping every field at the outer level. The current server's `CloudEventEnvelope.From` (C# `EventBridge.cs:94-103`) does not produce such events, but the type system permits it.

**Fix**: check for the full CloudEvents discriminator — `type` AND `id` AND `source` AND `specVersion` must all be present. If so, return `.payload`. Otherwise, treat as legacy.

### P2-14: empty object fallback loses information silently

`return {}` on null/undefined or non-object payload is followed by `parsed as EventMap['comment_added']` and direct field access. The `try { ... } catch { // ignore malformed events }` wrapper (line 295-297) catches the resulting `TypeError`, but the user sees nothing — no toast, no console.warn, no broken-UI detection. A developer debugging "why didn't my event fire?" has no breadcrumb.

**Fix**: at minimum, `console.warn('[LiveTaskProvider] dropped malformed event', eventName, rawData)` in the catch block.

### Test coverage

`tests/live-task-cloud-event.test.tsx` (34 lines, 4 tests) covers the happy path of `unwrapEnvelope` (envelope, back-compat raw, null/undefined, non-object payload). It does **not** cover:

- The actual switch-case logic in `handleEvent` (no test that feeds a `stage_changed` event and asserts `queryClient.invalidateQueries({ queryKey: ['issues'] })` was called).
- The `ralph_task_update` timer setup / teardown.
- The `toast.info` / `toast.error` calls in `agent_paused` / `agent_error` / `merge_completed` / etc.
- The `setRebaseConflict` state transitions in `rebase_conflict` / `agent_conflict_resolution_*`.

The full `handleEvent` is therefore untested as a unit. P1-8.

---

## 6. Web state management (TanStack Query + SignalR)

`LiveTaskProvider.handleEvent` is the only place where SignalR events meet TanStack Query. The pattern is:

1. Receive event name + envelope.
2. Unwrap envelope → `parsed`.
3. For agent-detail events, `dispatchAgentEvent` to the in-process EventTarget (consumed by `useSessionTranscript`, `useCoderSessions`, etc.).
4. For `ralph_task_update` start, set local state + start 500ms timer.
5. For a fixed set of event names, `queryClient.invalidateQueries({ queryKey: ['agent-activity'] })`.
6. Switch on event name, invalidate more queries, optionally toast.

### P1-7: 200-line switch with no shared invalidation helper

Many cases duplicate the same `queryClient.invalidateQueries({ queryKey: ['issues'] })` call. Some cases (e.g., `comment_added`, `merge_queued`) only invalidate `['issues']` but should arguably also invalidate `['issues', issueId]` or `['agent-activity']`. The `ralph_task_update` invalidation in step 5 is the only one that touches `agent-activity` outside the switch — but the switch has no `default` case, so unknown event names do nothing.

**Fix**: extract a `getInvalidationKeys(eventName, parsed): Array<QueryKey>` helper. Switch becomes a single call to the helper.

### P2-12: 500ms timer re-renders the whole `LiveTaskContext` tree

`setActiveTaskElapsedMs` is called every 500ms while a `ralph_task_update` is in `started` state. The `LiveTaskContext.Provider value={state}` re-renders all consumers. Today the only consumers are `WorktreePanel` and `BranchBar`, both of which only read `rebaseConflict` (line 38 of `WorktreePanel.tsx`, line 20 of `BranchBar.tsx`). But because `state` is a new object on every `setActiveTaskElapsedMs` call, both components re-render unnecessarily.

**Fix**: split `LiveTaskState` into three contexts: `activeTaskContext` (high-frequency), `rebaseConflictContext` (low-frequency), or move the elapsed-ms state into a separate `useElapsedMs` hook consumed only by the UI that actually displays it.

### P2-13: callbackRef is a fragile pattern

`useEventsConnection` keeps a `callbackRef.current = onEvent` and the SignalR `on('OnEvent', ...)` handler calls `callbackRef.current(eventName, data)`. This works *because* `useCallback` in `LiveTaskProvider.useLiveEvents` re-creates `handleEvent` on every `[queryClient, clearLiveTimer]` change, and `callbackRef.current` always points to the latest. But the comment on line 23-24 of `events-hub.ts` says "always-on" — there's no defensive logic if a future refactor stops using the ref. A test that mounts / unmounts the provider rapidly and asserts the latest `onEvent` is called would catch a regression.

---

## 7. Web test coverage

- **Unit**: `tests/live-task-cloud-event.test.tsx` covers `unwrapEnvelope` (4 cases). `tests/api-client.test.ts` covers the REST client. `tests/events-hub.test.tsx` mocks SignalR.
- **Integration**: `tests/SessionPage.test.tsx` (4394 lines) is the giant. `tests/SessionPage.live-transcript.test.tsx` covers the `coder_text_chunk` / `coder_thought_chunk` / `coder_tool_call` → transcript update path via `dispatchAgentEvent`. `tests/SessionLiveness.test.tsx` covers `coder_session_status_changed`. `tests/runner-status.test.tsx` covers the runner status widget.
- **Missing** (P1-8): the full `LiveTaskProvider.handleEvent` switch is not tested end-to-end. Each `case` should have a "given event X, expect invalidation Y and toast Z" test.
- **Pre-existing failures** (7): `Header.test.tsx` (3), `SessionPage.test.tsx` (3), `EpicListPage.test.tsx` (1). All 7 are unrelated to the recent changes (verified in the 51edea2081 commit message: "7 pre-existing failures (Header test class + SessionHeader + EpicListPage) unrelated to this change — verified on master HEAD"). They are caused by:
  - `<Header>` rendering test uses `MemoryRouter` with no `ProjectProvider` initial projects, so `useProject` returns `{ currentProject: null }` and `usePageTitle` returns `'Mohist'` instead of the expected route-specific title. Pre-existing in master.
  - `SessionHeader` test expects `transcriptPath` to start with `/issues/...` but `toProjectPath` prepends `/Test%20Project/...`. Pre-existing.
  - `EpicListPage` test expects `mockNavigate` to be called with `/epic/epic-active` but the production code navigates to `/epics/epic-active` (singular vs plural). Pre-existing.

These are not from the audit scope. Listed here for completeness.

---

## 8. Cross-package contract

### C# `CloudEventEnvelope` (EventBridge.cs:83-104)

```csharp
public sealed record CloudEventEnvelope(
    string Type,
    object? Payload,
    string Id,
    string Source,
    string SpecVersion,
    string? Subject,
    string? Time,
    string? DataContentType,
    Dictionary<string, object?>? Extensions);
```

### TS `CloudEventEnvelope` (LiveTaskProvider.tsx:25-35)

```ts
interface CloudEventEnvelope {
  type: string
  payload: unknown
  id: string
  source: string
  specVersion: string
  subject?: string
  time?: string
  dataContentType?: string
  extensions?: Record<string, unknown>
}
```

### JSON shape

System.Text.Json default in ASP.NET Core uses `JsonSerializerDefaults.Web` (camelCase). Both `AddSignalR()` and the inbound `JsonOptions` confirm this. The wire shape is:

```json
{
  "type": "stage_changed",
  "payload": { ... },
  "id": "uuid",
  "source": "/mohist/...",
  "specVersion": "1.0",
  "subject": "...",
  "time": "2026-...",
  "dataContentType": "...",
  "extensions": { "projectid": "...", "workflowrunid": "...", "issueno": "..." }
}
```

Field names match. The `extensions` field carries the routing metadata the server attaches (`projectid`, `workflowrunid`, `issueno`) — the Web's `LiveTaskProvider` does not currently read `extensions`; it relies on the SignalR `project:{projectId}` group filter on the server side. This is a valid trade-off (SignalR is already group-scoped), but means a future "show me the workflow that this event came from" UI feature would need to re-parse the envelope.

### What is *not* tested (P1-8)

There is no test that:
- Mocks a real server `CloudEventEnvelope` JSON blob.
- Feeds it through the SignalR `OnEvent` callback.
- Verifies that `LiveTaskProvider.handleEvent` correctly unwraps and dispatches.

The only envelope test (`live-task-cloud-event.test.tsx`) tests `unwrapEnvelope` in isolation with synthetic inputs. A round-trip test (server JSON → consumer) is missing.

### Wire shape delta vs. consumer expectations

`EventBusEventTypes.All` (server's `EventCatalog.cs:14-71`) is the catalog the EventBridge subscribes to. The Web's `EventMap` (`entities/issue/@x/events.ts:3-33`) is a union of `EventMap` (28 snake_case events) and `AgentDetailEventMap` (21 agent events). These two sets are not the same:

| Server `EventCatalog.All` | Web `EventMap ∪ AgentDetailEventMap` |
|---|---|
| 52 names (snake_case legacy + reverse-DNS) | 28+21=49 names (snake_case only) |

The reverse-DNS names (e.g., `com.mohist.workflow.run.completed`, `com.mohist.workflow.task.started`, `com.mohist.runner.disconnected`, `com.mohist.workflow.lease-expired`) are not consumed by the Web. They are emitted by the server's bus (and would be forwarded to the Web by EventBridge), but the Web's `handleEvent` switch would silently drop them. This is a P2 cross-package contract gap: the server publishes events the Web does not know about. Today the reverse-DNS names are mostly dead (most emitters still use snake_case per `WorkflowEventSerializer.cs:21-37` mapping), but the bridge is now wide open and the Web is unprepared.

---

## 9. Build & tooling

- **`packages/runner/tsconfig.json`**: target ES2022, module NodeNext, strict, no DOM. Clean `tsc -p tsconfig.json --noEmit`. `npm test` 165/165 pass. `npm run build` succeeds.
- **`packages/web/tsconfig.json`**: target ES2022, module ESNext, bundler, jsx react-jsx, strict, noUnusedLocals, noUnusedParameters, noFallthroughCasesInSwitch. `tsc -b` succeeds. `npm run build` (`tsc -b && vite build`) succeeds. Bundle 2.7MB / 507KB gz.
- **`vitest.workspace.ts`**: only includes the web. The runner's `vitest run` must be invoked via `npm test -w packages/runner`. The root `npm test` runs `dotnet test`, not vitest. Minor config issue.
- **No ESLint / Prettier**: the repo has no `eslint` or `prettier` config. TypeScript is the only static check. Style consistency depends on developer discipline.

### Stale build artifact (P3-19)

`packages/runner/dist/runtime/session-pool.js` is a 41-line file that has no source counterpart (the class `AcpSessionPool` does not exist in `packages/runner/src/**` — the source uses `AcpSessionManager`). No file imports it. It is harmless at runtime but should be cleaned. The runner's `package.json:10` does `tsc -p tsconfig.json` which does not `clean` before build. Add `"prebuild": "rm -rf dist"` or migrate to `tsc --build` with a `clean` script.

### Web `tsconfig.tsbuildinfo` is committed

`packages/web/tsconfig.tsbuildinfo` is in the repo (visible in the workspace tree). The standard pattern is to gitignore it. Not a functional issue, just hygiene.

---

## 10. Performance

### LiveTaskProvider re-renders (P2-12, see §6)

`setActiveTaskElapsedMs` every 500ms while a `ralph_task_update` is `started`. The `LiveTaskContext.Provider value={state}` re-creates the state object on every tick, so all consumers re-render. With the current 2 consumers (WorktreePanel + BranchBar) reading only `rebaseConflict`, this is wasted work. If a future consumer reads `activeTaskElapsedMs` directly (for a "30s elapsed" indicator), the re-render becomes useful; until then, split the context.

### Memo / useCallback

`LiveTaskProvider.useLiveEvents` is well-memoized: `clearLiveTimer` (useCallback, line 90), `handleEvent` (useCallback, line 98), with `[queryClient, clearLiveTimer]` deps. The `useEffect` for history-pushState is `[]`-deps. The cleanup effect for `clearLiveTimer` is `[clearLiveTimer]`-deps. No obvious gaps.

### Bundle size (P2-17)

`dist/assets/index-nAaugI9I.js` is 2.7MB (507KB gz). Top contributors (rough, by `node_modules` unpacked size):

- `@microsoft/signalr` 3.3MB → most of this is the WebSocket / long-polling fallback; with Vite tree-shaking, only the WebSocket transport + the hub protocol should land in the bundle. The 2.7MB output suggests signalr is fully bundled.
- `react-dom` 7.2MB, `react` 260KB → client + server renderers; client is what's used, server renderer is dead weight in a CSR app. `react-dom/server` should be marked as external or `vite.config.ts` should configure `optimizeDeps.exclude: ['react-dom/server']`.
- `@base-ui/react` 17MB → tree-shakeable per-component import (`@base-ui/react/dialog`), which the codebase already does. 8 imports across `shared/ui/components/*`.
- `lucide-react` 39MB → 18 files import named icons (`import { PlusIcon } from 'lucide-react'`). Lucide exports per-icon, so tree-shaking should work; verify the build output excludes unused icons.
- `@tanstack/react-query` and friends (4.9MB) → expected.
- `react-markdown` 88KB → used in 4 files for issue bodies / review reports. Heavy plugin chain (`remark-gfm`).

No immediate red flags. The 2.7MB output is acceptable for a feature-rich SPA, but a `vite-plugin-visualizer` run with the threshold tuned to 50KB chunks would identify optimization targets.

---

## 11. Detailed findings

### P0-1. `rebase_started` / `rebase_progress` events dropped on the Web

- **Where**: `app/providers/LiveTaskProvider.tsx:126-140, 211-228` (switch has cases for `rebase_completed` and `rebase_conflict` but not `rebase_started` or `rebase_progress`) + `entities/issue/model/rebase-events.ts:9-11` (`dispatchRebaseEvent` is exported but never called) + `widgets/worktree/ui/WorktreePanel.tsx:43-67` (subscribes to `onRebaseEvent`, but the bridge is dead).
- **What**: Pre-SSE-migration (commit 6fb8b239a5, 2026-06-01), `useSSEInner` had `case 'rebase_started': dispatchRebaseEvent(...)` and `case 'rebase_progress': dispatchRebaseEvent(...)`. The SignalR migration dropped those cases. The `rebase-events.ts` file survived but `dispatchRebaseEvent` is now dead code. `WorktreePanel` listens via `onRebaseEvent` but no one dispatches; the `rebaseStep` state is never updated; the `STEP_LABELS` UI ("Fetching latest...", "Checking fast-forward...", "Rebasing onto master...", "Verifying build...") is unreachable.
- **Why P0**: The rebase step indicator is a documented user-facing feature ("Rebasing onto master..." spinner). It's been broken for a week (since the SignalR migration). Any user who triggers a rebase sees only the "rebase mutation in flight" hint, never the step labels.
- **Fix**:
  ```ts
  // In LiveTaskProvider.handleEvent, inside the ralph_task_update block / new if block:
  if (eventName === 'rebase_started' || eventName === 'rebase_progress') {
    const d = parsed as EventMap['rebase_started'] | EventMap['rebase_progress']
    if (eventName === 'rebase_started') {
      dispatchRebaseEvent({ type: 'rebase_started', issueNumber: d.issueNumber })
    } else {
      dispatchRebaseEvent({ type: 'rebase_progress', issueNumber: d.issueNumber, step: d.step })
    }
  }
  ```
  And add a `case 'rebase_started':` / `case 'rebase_progress':` that also invalidates the worktree-status query for the affected issue.

### P0-2. `unwrapEnvelope` duck-types on a single key

- **Where**: `app/providers/LiveTaskProvider.tsx:37-49`
- **What**: The check is `'payload' in rawData`. If a server event's payload object happens to have a `payload` field (e.g., a `MohistConfig` update event whose body is `{ payload: 'new-config' }`), the unwrap returns the *string* `'new-config'`, and the calling `parsed as EventMap['comment_added']` returns a `Record<string, unknown>` that is actually a string — subsequent field access crashes inside the `try { ... } catch { // ignore }` (P2-22), so the user sees nothing.
- **Fix**: check the full CloudEvents shape: `rawData && typeof rawData === 'object' && 'type' in rawData && 'id' in rawData && 'source' in rawData && 'specVersion' in rawData`. Only then treat as an envelope.

### P0-3. Runner `executeAndReport` cannot report on shutdown

- **Where**: `runtime/host.ts:140-176`
- **What**: On SIGINT/SIGTERM, the abort signal fires, `runWorkerPool` propagates, `executeAndReport` is in its catch block trying to `report` a `failed` status, but the fetch fails because the signal is aborted. The work stays "running" server-side until the lease times out (~5 min by server config).
- **Fix**: in `executeAndReport`, use the *original* `signal` for the executor, but a *fresh* `AbortSignal` (or a 5s-timeout `AbortController`) for the final `report`. Always attempt the report; if it fails, log and move on. The server will lease-expire the work eventually, but at least the runner can drain cleanly.

### P1-4. AcpSessionManager cache reset on reconnect

- **Where**: `runtime/host.ts:78-87` (`shutdownSharedConnection` does `this.sessionManager = new AcpSessionManager()`)
- **What**: The comment on line 23-27 says the manager is per-host, but `shutdownSharedConnection` resets it. After a transient SignalR reconnect, every work item is a cold `runResumedWorkflowAgentSession` against a new opencode process.
- **Fix**: only reset the manager when the opencode process actually died. The shared connection has its own health signal (the `exitFailure` promise from `acp-connection.ts:70-78`). If `exitFailure` rejected, reset; otherwise keep the cache.

### P1-5. No `uncaughtException` / `unhandledRejection` handlers

- **Where**: `cli.ts:1-33`
- **What**: Node 18+ default behavior on unhandled promise rejection is to crash the process. A single unhandled rejection in any of the runner's many `await` sites (e.g., the `runEphemeralWorkflowAgentSession` path, the report callbacks, the heartbeat interval) kills the runner.
- **Fix**:
  ```ts
  process.on("unhandledRejection", (reason) => {
    console.error("unhandledRejection", reason)
  })
  process.on("uncaughtException", (error) => {
    console.error("uncaughtException", error)
    controller.abort()
    // Give the host a moment to drain, then exit.
    setTimeout(() => process.exit(1), 10_000).unref()
  })
  ```

### P1-6. `initializeSharedConnection` has no backoff

- **Where**: `runtime/host.ts:42-76`
- **What**: The outer `run` loop retries every `pollIntervalMs` (default 1s). If `createSharedAcpConnection` consistently fails (opencode missing from PATH), the loop logs once per second forever.
- **Fix**: add a circuit breaker. Track `lastSharedConnectionFailureAt`; on the 3rd consecutive failure, switch to exponential backoff up to 60s.

### P1-7. 200-line `handleEvent` switch with no shared helper

- **Where**: `app/providers/LiveTaskProvider.tsx:142-294`
- **What**: Each case repeats `queryClient.invalidateQueries({ queryKey: ['issues'] })`. Some cases miss invalidations they should do (e.g., `merge_completed` invalidates `['issues']` but not the specific issue's `['issues', number]` or `['agent-activity']`).
- **Fix**: extract `const invalidations = getInvalidationKeys(eventName, parsed): QueryKey[][]`. Switch becomes `for (const key of invalidations) queryClient.invalidateQueries({ queryKey: key })`. Test the helper exhaustively.

### P1-8. No round-trip envelope test

- **Where**: `tests/live-task-cloud-event.test.tsx:1-34` (4 tests, all synthetic) + `tests/events-hub.test.tsx:38-62` (mocks SignalR `on`)
- **What**: A real server `CloudEventEnvelope` JSON blob is never parsed through the SignalR `OnEvent` callback. The field-name contract (camelCase from System.Text.Json, all 9 fields, extensions) is not asserted.
- **Fix**: add a test in `events-hub.test.tsx` that takes a real server JSON blob, invokes the registered `OnEvent` handler, and asserts the callback received the unwrapped payload + the `eventName` is the `type` field.

### P1-9. Runner SignalR handlers untested

- **Where**: `server/runner-signalr.ts:26-138` (`GetDiff`, `GetCommits`, `GetWorktreeStatus`, `GetFileContent`) + `tests/runner-signalr.spec.ts:1-28` (only tests `resolveWorkspaceQuery`)
- **What**: The four handlers invoke `git` subprocesses and parse stdout. They have no unit tests. A regression in the diff parser (e.g., `parseDiffFiles` mishandles binary files) would only surface in production.
- **Fix**: extract the parsers (`parseDiffFiles`, `parseCommits`, `parseAheadBehind`, `parseNumstatTotal`) and unit-test them. Add a fake `git` invoker (`runCommand` is already injectable) and test the end-to-end handler with a known fixture.

### P1-10. Shared opencode child process leak on mid-init failure

- **Where**: `runtime/acp-connection.ts:49-131` + `runtime/host.ts:61-87`
- **What**: `createSharedAcpConnection` `spawn()`s the child, sets up the stream, and constructs the `ClientSideConnection`. If anything between `spawn` and `proc.on("error", ...)` throws synchronously, the child process is leaked (no handle to kill it). The current code does not throw between those points, but the order is fragile.
- **Fix**: wrap the setup in a try/catch that calls `killProcess(proc)` on error. Or use Node's `child_process.spawn` with `{ detached: false }` and verify the child's PID is reachable.

### P2-11. Double `ensureWorkflowAgentSession` call

- **Where**: `actions/acp-agent.ts:487-500, 502-523`
- **What**: `runAcpWorkflowAgentSession` calls `ensureWorkflowAgentSession` once before the conditional block, then again inside the block (on the shared path). The first call's return value is discarded.
- **Fix**: delete lines 492-500, use the call at line 504 only.

### P2-12. 500ms re-render of `LiveTaskContext` tree

- **Where**: `app/providers/LiveTaskProvider.tsx:107-124, 310-311`
- **What**: `setActiveTaskElapsedMs` every 500ms creates a new `state` object, propagating re-renders to all `useLiveTask()` consumers.
- **Fix**: split the context into `ActiveTaskContext` (high-frequency) and `RebaseConflictContext` (low-frequency), or move the elapsed-ms state to a sibling hook used only by the UI that displays it.

### P2-13. `callbackRef` pattern is fragile

- **Where**: `shared/api/events-hub.ts:19-43`
- **What**: The hook assumes `callbackRef.current` is always the latest `onEvent`. If a future refactor stops updating the ref (e.g., moves the assignment out of the render body), stale closures will be used.
- **Fix**: add a test that mounts/unmounts `useEventsConnection` rapidly with changing `onEvent` references and asserts the latest is called.

### P2-14. Empty `parsed` falls through silently

- **Where**: `app/providers/LiveTaskProvider.tsx:37-49, 295-297`
- **What**: `unwrapEnvelope` returns `{}` on null/non-object payload; subsequent field access in the switch throws inside the outer `try` and is silently swallowed.
- **Fix**: `console.warn('[LiveTaskProvider] dropped event', eventName, rawData)` in the catch block.

### P2-15. `projectId` not validated at startup

- **Where**: `cli.ts:12` (`env("PROJECT_ID") ?? env("ProjectId")` — both can be undefined)
- **What**: If `PROJECT_ID` is unset, the runner registers with no projectId, and every `ensureWorkflowAgentSession` URL has an empty project segment. The server will reject the calls (or worse, route them to the global group).
- **Fix**: log a clear warning at startup if `projectId` is unset. Optionally make it required for non-development deployments.

### P2-16. `git` output size unbounded

- **Where**: `server/runner-signalr.ts:26-138`
- **What**: `GetDiff` returns the full diff text in the SignalR invocation result. A long-lived issue's diff can be megabytes. SignalR's default max message size is 32KB; large diffs trigger a protocol error.
- **Fix**: either chunk the response (e.g., stream the file list with separate file-content calls) or configure SignalR to allow larger messages. Document the size limit.

### P2-17. Bundle 2.7MB; check for `react-dom/server`

- **Where**: `package.json:18, 23-31` + `dist/assets/index-nAaugI9I.js:2,714.73 kB`
- **What**: The bundle is reasonable for a feature-rich SPA, but verify that `react-dom/server` (unused) is not bundled, that `@microsoft/signalr` tree-shakes to just the WebSocket transport, and that `lucide-react` per-icon imports are working (18 files import named icons).
- **Fix**: run `npx vite-bundle-visualizer` and inspect the treemap. Mark `react-dom/server` as external if it shows up.

### P2-18. Handlers registered before `.start()`

- **Where**: `server/runner-signalr.ts:7-15, 25-139`
- **What**: The constructor calls `registerHandlers()`, which calls `this.connection.on("GetDiff", ...)`. If `start()` rejects, the handlers are attached to a never-started connection; on retry, the same connection is reused and the handlers fire on the next `.start()`.
- **Fix**: not a bug today, but add a comment. If a future refactor moves handler registration to after `start()`, it breaks.

### P3-19. Stale `session-pool.js` in `runner/dist`

- **Where**: `packages/runner/dist/runtime/session-pool.js:1-41`
- **What**: A 41-line file with class `AcpSessionPool` (no source counterpart) is in the dist. No file imports it. `tsc` does not clean before build.
- **Fix**: `"prebuild": "rm -rf dist"` in `packages/runner/package.json`.

### P3-20. Raw `console.log` / no logger

- **Where**: 9 sites in `host.ts`, 0 in `runner-signalr.ts`
- **What**: No log level, no structured fields, no rotation. A noisy reconnect cycle spams stderr.
- **Fix**: introduce a `Logger` interface (`debug`, `info`, `warn`, `error`) with a `pino`-style structured output. Default to JSON, fall back to pretty-print in dev.

### P3-21. Global monkey-patch of `history.pushState`

- **Where**: `app/providers/LiveTaskProvider.tsx:68-88`
- **What**: The provider monkey-patches `window.history.pushState` and `.replaceState` globally to update `viewedIssueRef`. Cleanup restores the originals. Brittle if a future component also monkey-patches.
- **Fix**: extract to a `useViewedIssueNumberRef()` hook; use a single source of truth (e.g., the React Router `useLocation` hook in the provider).

### P3-22. Single `try/catch` swallows all exceptions in `handleEvent`

- **Where**: `app/providers/LiveTaskProvider.tsx:100-297`
- **What**: A throw in the `ralph_task_update` branch prevents the subsequent `queryClient.invalidateQueries({ queryKey: ['agent-activity'] })` from running for the same event.
- **Fix**: split into multiple try blocks per concern (agent dispatch, local state, query invalidation, toast).

### P3-23. Stale file references in audit prompt

- **Where**: prompt mentions `runtime/work-executor.ts` (actual: `runtime/executor.ts`), `runtime/session-manager.ts` (does not exist; functionality is in `acp-connection.ts`), `runtime/index.ts` (does not exist; entry is `cli.ts`), `packages/runner/src/index.ts` (does not exist).
- **What**: The audit prompt's file list is out of date. Not a code issue, just a documentation drift. The runner has been refactored (the per-host shared connection refactor in 197ea02477).

---

## Executive summary

### Production-ready?

**Mostly yes, with two P0 fixes needed before claiming so.** The TS runner + Web UI is in good shape:

- 165/165 runner tests pass.
- 789/796 web tests pass; 7 failures are pre-existing and unrelated to the audit scope (verified in the 51edea2081 commit message).
- Runner typecheck clean, build clean.
- Web typecheck clean, build clean (2.7MB / 507KB gz).
- The per-host shared opencode connection (197ea02477) is correctly implemented and tests cover the major paths.
- The CloudEvents envelope adoption (51edea2081) is correctly implemented; the wire shape matches the C# `CloudEventEnvelope` record; `unwrapEnvelope` has 4 unit tests.

The two P0 issues are:

1. **`rebase_started` / `rebase_progress` events are dropped** (P0-1). The rebase step indicator in `WorktreePanel` has been broken since the SignalR migration (6fb8b239a5, 2026-06-01). `dispatchRebaseEvent` is dead code. This is a user-visible regression that has been latent for a week.

2. **`unwrapEnvelope` is fragile** (P0-2). A future server event with a nested `payload` field would silently lose its top-level fields. The check needs to validate the full CloudEvents shape, not just one key.

3. **Runner shutdown report race** (P0-3). On SIGINT, the runner cannot report final work results to the server. The lease timeout re-queues the work after 5 min, but operators see no breadcrumb.

### Top 3 risks

1. **Reconnect cold-start of opencode sessions** (P1-4 + P1-6). After any transient reconnect, every work item re-spawns a session against a new opencode process. The cache reset on shutdown means the "cross-task cache" the design promised is only warm within a single connection cycle, not across reconnects. Combined with no backoff on the shared-connection init (P1-6), a flaky opencode binary can cause hot loops and wasted spawn attempts.

2. **Unhandled promise rejections kill the runner** (P1-5). The runner has no top-level safety net. A single unhandled rejection in any code path (the SDK, the spawn failure, the cleanup, the report) crashes the process. Process supervisors restart, but in-flight work is lost.

3. **LiveTaskProvider re-render storm + fragile envelope parsing** (P0-2 + P2-12). The 500ms `setActiveTaskElapsedMs` re-renders all `useLiveTask()` consumers; the duck-typed envelope parser will silently mishandle any future event whose payload contains a `payload` field. Both are reachable in production.

### Top 3 quick wins

1. **Re-add the rebase event cases in `LiveTaskProvider.handleEvent`** (P0-1, 30 min):
   ```ts
   case 'rebase_started': {
     const d = parsed as EventMap['rebase_started']
     dispatchRebaseEvent({ type: 'rebase_started', issueNumber: d.issueNumber })
     queryClient.invalidateQueries({ queryKey: ['issues', d.issueNumber, projectId, 'worktree-status'] })
     break
   }
   case 'rebase_progress': {
     const d = parsed as EventMap['rebase_progress']
     dispatchRebaseEvent({ type: 'rebase_progress', issueNumber: d.issueNumber, step: d.step })
     break
   }
   ```
   Add a test in `live-task-cloud-event.test.tsx` that asserts `dispatchRebaseEvent` was called for each case.

2. **Add `uncaughtException` + `unhandledRejection` handlers** (P1-5, 15 min):
   ```ts
   process.on("unhandledRejection", (reason) => {
     console.error("[mohist-runner] unhandledRejection", reason)
   })
   process.on("uncaughtException", (error) => {
     console.error("[mohist-runner] uncaughtException", error)
     controller.abort()
     setTimeout(() => process.exit(1), 10_000).unref()
   })
   ```

3. **Tighten `unwrapEnvelope` to validate the full CloudEvents shape** (P0-2, 10 min):
   ```ts
   function unwrapEnvelope(rawData: unknown): Record<string, unknown> {
     if (
       rawData && typeof rawData === 'object' &&
       'type' in rawData && 'id' in rawData &&
       'source' in rawData && 'specVersion' in rawData &&
       'payload' in rawData
     ) {
       const payload = (rawData as CloudEventEnvelope).payload
       if (payload && typeof payload === 'object') return payload as Record<string, unknown>
     }
     if (rawData && typeof rawData === 'object') return rawData as Record<string, unknown>
     return {}
   }
   ```
   Update the existing 4 tests to assert the new check rejects single-`payload`-key objects.

### Follow-up audits (out of scope here)

- **End-to-end SignalR envelope round-trip test** — needs a small server fixture; would close P1-8 and P2-17 simultaneously.
- **Web bundle optimization pass** — `vite-bundle-visualizer` + `react-dom/server` externalization; would address P2-17.
- **Runner circuit breaker / reconnect policy** — would address P1-4 and P1-6 together; needs a design pass on "what is a transient vs. permanent connection failure".
- **Cross-package event contract test suite** — every `EventCatalog.All` entry should have a corresponding Web `EventMap` case; would close the reverse-DNS / snake_case naming drift.
