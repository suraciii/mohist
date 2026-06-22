## Why

Runner and server maintain two independent channels: a periodic HTTP heartbeat and a long-lived SignalR dispatch connection. The server's "runnerId → connectionId" map is maintained **only** by SignalR connect/disconnect events. When socket turbulence fires `OnDisconnectedAsync` on the server side but the runner client never observes a fresh `OnConnectedAsync` (the client is still mid-reconnect, or the event is lost/reordered), the map stays empty and every workspace materialization returns `runner_unavailable` — even though the runner process is alive, heartbeating, and serving other work. The user has no recovery path. `WorkflowGrain.RunCoreAsync` calls `MaterializeWorkspaceAsync` (SignalR) **before** `AssignRunnerWorkAsync` (grain-level queue), so the recent master fix `e4ffd6f02` (accept work while offline) does not help — the materialization gate short-circuits first. The existing `orphaned-task-recovery` capability handles the opposite case (a genuinely dead runner) and offers no convergence for a runner that is online but misjudged as offline.

## What Changes

- The runner's periodic heartbeat request carries an optional field with the runner's current real-time dispatch `connectionId`.
- The server heartbeat endpoint refreshes the `runnerId → connectionId` map from this field on every heartbeat, making the heartbeat a second source of truth for the map.
- Heartbeats may only **write or update** the map; erasure remains exclusive to the SignalR `OnDisconnectedAsync` path, so the two channels never fight over clearing state.
- The runner client periodically self-checks the real-time dispatch connection's liveness via one lightweight round-trip; on failure it proactively reconnects within the self-check period rather than waiting for the next dispatch to discover the dead connection.
- On a successful reconnect, the runner immediately re-sends one heartbeat carrying the new `connectionId`, instead of waiting for the next heartbeat cycle.
- All protocol additions are optional fields — old runners without the field continue to behave exactly as today.

## Capabilities

### New Capabilities

- `runner-online-convergence`: Heartbeat-driven convergence of the `runnerId → connectionId` map (heartbeat writes/updates, SignalR disconnect is the sole erase), plus a runner-client self-check of the real-time dispatch connection with proactive reconnect and immediate post-reconnect heartbeat.

### Modified Capabilities

- _None._ `orphaned-task-recovery` still holds exactly as written (a genuinely dead runner whose heartbeat times out is still judged unavailable and its tasks still fail with `runner-lost`). The change only adds a convergence mechanism for the runner-online-but-misjudged case; it does not alter any existing requirement.

## Impact

- **Server HTTP layer** (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`): `RunnerHeartbeatRequest` gains an optional `ConnectionId` field; the `/heartbeat` handler writes it through to `RunnerConnectionTracker` on every heartbeat.
- **Server connection registry** (`packages/server/src/Mohist.Server/Runner/Services/SignalR/RunnerConnectionTracker.cs`, `RunnerHub.cs`): the write/update path is reused by both heartbeat and SignalR connect; `Unregister` remains called only from `OnDisconnectedAsync`.
- **Server status projection** (`RunnerStatusService.cs`): unchanged — it already derives `connectionState` from the tracker; convergence simply makes the tracker more accurate.
- **Runner client** (`packages/runner/src/core/types.ts`, `packages/runner/src/server/connection.ts`, `runner-signalr.ts`, `runtime/host.ts`, `cli.ts`): `RunnerRegistration` gains an optional `connectionId` field; `RunnerHost.registrationState()` populates it from `RunnerSignalRClient.getConnectionId()`, so it rides on the existing `heartbeat(state, signal)` call already established by master commit `4b784e443`. `RunnerSignalRClient` exposes `getConnectionId()`, `probeLiveness()` (invoke `Ping` with a bounded timeout), and `forceReconnect()` (stop then start); `onreconnected` is wired so both auto-reconnect and manual `forceReconnect` funnel through one host callback that re-sends a heartbeat immediately. `RunnerHost.run` adds a self-check timer on `DISPATCH_LIVENESS_PROBE_INTERVAL_MS` (default 10000) alongside the existing heartbeat timer; `RunnerOptions` and `cli.ts` carry the new env.
- **Protocol**: backward-compatible optional field; no auth, persistence, workflow runtime, or dispatch-strategy changes.
