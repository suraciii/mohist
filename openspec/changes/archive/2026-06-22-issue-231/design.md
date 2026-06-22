## Context

Runner and server exchange state over two physically independent channels:

- **Periodic HTTP heartbeat** (`POST /api/runner/{runnerId}/heartbeat` → `RunnerGrain`) — the liveness signal. Stable, fire-and-forget.
- **Real-time dispatch connection** (SignalR `/hubs/runner`, `RunnerHub`) — the long-lived channel used for workspace materialization and review queries. Socket-sensitive.

Server keeps the `runnerId → connectionId` map in an in-memory singleton (`RunnerConnectionTracker`, registered in `MohistServiceRegistration.cs:153`). `RunnerWorkspaceClient.MaterializeWorkspaceAsync` consults this map before every workspace materialization; a missing entry surfaces as `runner_unavailable`. Today the map is maintained **only** by `RunnerHub.OnConnectedAsync` (writes) and `OnDisconnectedAsync` (erases). `RunnerStatusService.DeriveConnectionState` reads it to project `connectionState`, and `RunnerStatusService.DeriveStatus` now downgrades a runner with the `workspace-query` capability to `offline` when the map entry is absent — widening the blast radius of a stale-empty map beyond dispatch into status display.

The failure mode in issue #231: socket turbulence fires `OnDisconnectedAsync` server-side (erasing the map) while the runner client is still mid-reconnect and never produces a fresh `OnConnectedAsync` that the server observes in time, or the event is lost/reordered. The map stays empty; the runner is online and heartbeating but `WorkflowGrain.RunCoreAsync` fails at the `MaterializeWorkspaceAsync` step (line 344) — which runs **before** `AssignRunnerWorkAsync` (line 360) — and the workflow stage never reaches the grain-level assignment. (Master commit `e4ffd6f02` made `AssignWorkAsync` accept work while offline, but that grain-level queue does not help here because the SignalR materialization gate precedes it and short-circuits on `runner_unavailable`.) The existing `orphaned-task-recovery` capability handles the opposite direction (truly dead runner → fail its tasks) and cannot help here.

Constraints (from the issue's Non-Goals): no change to the dispatch protocol/transport, no cross-process consistent map store, no change to heartbeat frequency or liveness timeout.

## Goals / Non-Goals

**Goals:**

- Make the periodic heartbeat a second source of truth for the `runnerId → connectionId` map so divergence from the SignalR channel self-heals within one heartbeat cycle.
- Add a runner-side self-check of the dispatch connection that detects a half-open/dead socket and proactively reconnects, instead of waiting for a dispatch to expose the failure.
- On any successful (re)connect, refresh the map immediately via an out-of-cycle heartbeat carrying the new connection id.
- Preserve the existing invariant that erasing the map is exclusive to the SignalR disconnect path.

**Non-Goals:**

- Linearizable ordering between the heartbeat channel and the SignalR channel. Convergence is eventually-correct within seconds, not strictly serializable.
- Cross-silo or persistent connection registry. Single-process deployment only.
- Changes to workflow dispatch strategy, auth, persistence, or the SignalR protocol/handshake itself.
- Tuning heartbeat frequency or liveness timeout (defaults unchanged).
- Handling actual runner crash / OOM / kill (covered by `orphaned-task-recovery`).

## Decisions

### 1. Heartbeat writes the map directly via the tracker singleton

The `/heartbeat` handler (RunnerRoutes.cs:40) injects `RunnerConnectionTracker` (the same singleton `RunnerHub` already uses) and calls `Register(runnerId, connectionId)` when the request carries a non-empty connection id. `RunnerGrain` continues to own only the liveness timestamp; it does not learn about the connection id.

**Alternatives considered:** Route the connection id through `RunnerGrain.HeartbeatAsync(connectionId)`. Rejected — the grain is `[Reentrant]` and timer-driven; coupling it to the SignalR singleton would re-introduce cross-channel ordering through the back door, and the grain has no legitimate ownership of connection-map state. Keeping two write paths (hub + HTTP handler) that both terminate at the same singleton is the simplest faithful reflection of "two sources of truth".

### 2. No new tracker method — `Register` is the single write entry point

`RunnerConnectionTracker.Register` already performs `_connections[runnerId] = connectionId` (idempotent overwrite). Both the SignalR connect path and the new heartbeat path call it unchanged. The "heartbeat never erases" invariant is enforced at the **handler** level (the heartbeat path simply never calls `Unregister`), not by adding a guarded API.

**Alternatives considered:** Add `UpdateIfPresent` / `WriteIfNewer` with versioning. Rejected — would imply ordering semantics the proposal explicitly disclaims, and would duplicate the hub's write path for no behavioral gain.

### 3. `RunnerHeartbeatRequest` gains an optional `ConnectionId` field

Master commit `4b784e443` already expanded `RunnerHeartbeatRequest` (RunnerRoutes.cs:283) to carry full runner state (`Capabilities`, `ProjectId`, `Hostname`, `CoderModels`, `MaxWorkflowSlots`, `BuildGitHash`, `CoderModelVariants`). We append `string? ConnectionId = null` to the same record. The handler (RunnerRoutes.cs:40) treats null/empty/whitespace as "field absent" and skips the tracker write entirely, preserving spec requirement 3 (legacy-runner backward compat, and never erase).

### 4. Connection id rides on `RunnerRegistration` (no heartbeat signature change)

Master commit `4b784e443` already changed `ServerConnection.heartbeat(state: RunnerRegistration, signal)` to spread `...state` into the POST body, and `RunnerHost.registrationState()` is already called fresh on every heartbeat tick (host.ts:64). We add `connectionId?: string | null` to `RunnerRegistration` and have `RunnerHost.registrationState()` populate it from `this.signalR.getConnectionId()`. The connection id then flows through `...state` into the heartbeat body with **zero signature change** to `heartbeat()`. `RunnerSignalRClient.getConnectionId()` returns `this.connection.connectionId` (null before start).

**Alternatives considered:**
- *Inject `RunnerSignalRClient` into `ServerConnection` / pass a `() => string | null` thunk.* Rejected — `heartbeat()` already receives a freshly-constructed `RunnerRegistration` on every tick, so a thunk would duplicate what `registrationState()` already provides. Reusing the existing state object keeps the HTTP layer decoupled from the SignalR client.
- *Add `connectionId` only to the heartbeat body, not to `RunnerRegistration`.* Rejected — `registrationState()` is already shared between `connect()` and `heartbeat()`; adding the field to the shared type is the single wiring point and keeps both calls honest. Sending it in the register body is harmless (the server's `RunnerRegisterRequest` has no such field and ignores unknown JSON properties).

### 5. Liveness probe = invoke a new `Ping` hub method

Add `public Task<string> Ping() => Task.FromResult(Context.ConnectionId!)` to `RunnerHub`. The client invokes `"Ping"` with a bounded timeout (e.g. 5 s). Reject or timeout ⇒ connection considered dead.

**Alternatives considered:**
- *Rely on signalr's automatic keepalive + inspect `connection.state`.* Rejected — does not catch half-open sockets where local state still shows `Connected`; the issue explicitly requires a round-trip.
- *Invoke an existing hub method (e.g. `GetWorkspaceStatus` on a trivial query).* Rejected — those methods do real git work; a probe must be near-zero-cost.

Returning `Context.ConnectionId` is a free sanity check — the client can assert its local id matches the server's view.

### 6. Reconnect = `stop()` + `start()`, with `onreconnected` wired for both paths

On probe failure while `connection.state === Connected` (the suspect half-open case): call `connection.stop()` then `connection.start()`. This fires `OnDisconnectedAsync` (erase) then `OnConnectedAsync` (write with a new id) on the server — a clean reset that respects "erase is exclusive to disconnect".

signalr's `withAutomaticReconnect([0, 2000, 5000, 10000, 30000])` already handles transport-detected failures. We register `HubConnection.onreconnected(() => host.onDispatchReconnected())` so that **both** the auto-reconnect path and our manual restart funnel through the same callback, which issues the immediate out-of-cycle heartbeat.

**Alternatives considered:** Reuse the suspect connection (skip `stop()`). Rejected — if the probe failed the connection cannot be trusted; a clean restart is more robust than hoping auto-reconnect will repair it.

### 7. Self-check timer in `RunnerHost.run`, separate from the heartbeat timer

The existing heartbeat timer is at host.ts:64 (`setInterval(() => this.connection.heartbeat(this.registrationState(), signal), this.options.heartbeatIntervalMs)`). A second `setInterval(probeAndHeal, selfCheckIntervalMs)` runs alongside it. `probeAndHeal`: `await signalR.probeLiveness(signal)`; on failure `await signalR.forceReconnect(signal)`; on any successful (re)connect, the `onDispatchReconnected` hook fires `connection.heartbeat(this.registrationState(), signal)` immediately — `registrationState()` reads the new connection id at that moment via Decision 4.

New optional env `DISPATCH_LIVENESS_PROBE_INTERVAL_MS`, default `10_000` (shorter than the 15 s heartbeat to bound recovery; longer than the 1 s poll to keep cost low). Added to `RunnerOptions` (core/types.ts) and wired in cli.ts alongside the existing `heartbeatIntervalMs`/`pollIntervalMs` env reads.

**Alternatives considered:** Reuse `heartbeatIntervalMs` for both timers. Rejected — couples two concerns and would double heartbeat cost if an operator tunes the heartbeat interval.

## Risks / Trade-offs

- **[Stale heartbeat overwrites a fresher reconnect id]** → A heartbeat sent earlier (over connection A) arriving after a reconnect (connection B) overwrites B with A until the next cycle. *Mitigation:* window bounded by one heartbeat interval (≤ 15 s); the immediate post-reconnect heartbeat from decision 6 shortens the typical case to seconds. No linearizability guarantee — matches the proposal's "no cross-process consistent storage" boundary.
- **[Probe false-positive triggers needless reconnect]** → A transient network blip causes `stop()+start()` even though the connection was recoverable. *Mitigation:* bounded by the self-check interval; reconnect restores the map within seconds via the immediate heartbeat; churn is acceptable and self-correcting.
- **[Probe `Ping` cost on a hot path]** → *Mitigation:* near-zero (returns `Context.ConnectionId`, no I/O); invoked at most once per self-check interval per runner.
- **[`stop()+start()` races in-flight dispatch RPCs]** → A `MaterializeWorkspace` call mid-reconnect may fail. *Mitigation:* the workflow already treats dispatch failure as a normal failure/retry path; the next dispatch after reconnect succeeds. No data corruption — workspace materialization is idempotent per the `workspace-materialization` capability.
- **[Heartbeat handler now depends on the SignalR singleton]** → Introduces a compile-time dependency from the HTTP layer onto the SignalR layer. *Mitigation:* both already live in the same process and are wired in `MohistServiceRegistration`; no new cross-process coupling. The dependency is the existing tracker, not a new abstraction.

## Migration Plan

- **Roll-forward (safe ordering):**
  1. Deploy **server** first. The new `ConnectionId` field is optional; absent-field heartbeats behave exactly as today. The `Ping` hub method is additive and unused by old runners.
  2. Deploy **runner**. New runners start sending the field and probing; convergence activates.
- **Mixed versions:** Old runner vs. new server — field omitted, current behavior preserved. New runner vs. old server — server's default JSON deserialization ignores the unknown field; no breakage.
- **Roll-back:** Revert runner to the prior version (field not sent). Server-side revert is optional — the handler is a no-op when the field is absent. The map returns to its prior (SignalR-only) behavior. No data migration: the map is in-memory and ephemeral.

## Open Questions

- **Default self-check interval.** Proposing `10_000` ms. If operators prefer a single knob, it could be derived as `min(heartbeatIntervalMs, 10_000)`; leaning toward a dedicated env to keep the two concerns decoupled.
- **Whether the post-reconnect heartbeat should also re-send runner registration.** Current design: no — `RunnerHost.connectRunner` owns registration at startup and is independent of the dispatch connection; a reconnect does not change runner identity or capabilities. Revisit if registration fields ever become connection-scoped.
