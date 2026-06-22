# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: `openspec/changes/issue-231/progress.txt:83`
  Evidence: The workflow progress note still says manual `forceReconnect()` relies on SignalR `HubConnection.start()` firing `onreconnected`; that was true only as a mistaken build note and was repaired in product code by explicitly calling `notifyReconnected()` from `RunnerSignalRClient.forceReconnect()` at `packages/runner/src/server/runner-signalr.ts:77`. Because `progress.txt` is review context/evidence and not a product deliverable, and current code/tests now reflect the corrected behavior, this does not create a product or merge blocker.
  SuggestedAction: Optionally append a later progress note if the team wants the workflow evidence to record the repair rationale.
  Status: out-of-scope

- [ID: item-2]
  Severity: info
  Scope: verification
  Evidence: `dotnet test` runs print existing npm audit output for 9 dependency vulnerabilities during the web build step. This appears in the existing build pipeline and is unrelated to the runner heartbeat/SignalR changes under review.
  SuggestedAction: Track dependency audit remediation separately from issue #231.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: verification
  Evidence: One concurrent review-side `dotnet test` invocation failed with a temporary file-lock error on `rjsmrazor.dswa.cache.json` while another server test build was running. Re-running the affected `RunnerHeartbeatConnectionApiSpecs` test serially passed with 8 tests.
  SuggestedAction: Run server test filters serially when collecting review evidence.
  Status: out-of-scope

## Acceptance Criteria Evidence

- Runner heartbeat carries the current dispatch connection id: `RunnerHost.registrationState()` includes `connectionId: this.signalR.getConnectionId()` at `packages/runner/src/runtime/host.ts:273`, `ServerConnection.heartbeat()` already spreads the state into the heartbeat body, and `HeartbeatCarriesCurrentConnectionId_OnHeartbeatTick` verifies the heartbeat payload in `packages/runner/tests/runner-host.spec.ts:187`.
- Heartbeat refreshes `runnerId -> connectionId` and avoids false `runner_unavailable`: `RunnerRoutes` writes non-empty `ConnectionId` values into `RunnerConnectionTracker` at `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:41`; `RunnerWorkspaceClient.MaterializeWorkspaceAsync` resolves dispatch from that tracker at `packages/server/src/Mohist.Server/Runner/Services/SignalR/RunnerWorkspaceClient.cs:39`; `RunnerHeartbeatConnectionApiSpecs` covers write, overwrite, no-field/no-empty erase avoidance, and repopulation after unregister.
- Runner self-checks dispatch liveness and proactively reconnects: `RunnerHost.run()` starts a separate self-check timer at `packages/runner/src/runtime/host.ts:72`, `runSelfCheck()` probes and calls `forceReconnect()` at `packages/runner/src/runtime/host.ts:92`, and runner tests cover failure-triggered reconnect plus success-no-reconnect paths.
- Successful reconnect sends an immediate heartbeat: `RunnerSignalRClient.forceReconnect()` now calls `notifyReconnected()` after successful manual `start()` at `packages/runner/src/server/runner-signalr.ts:77`; SignalR automatic reconnect also funnels through `notifyReconnected()` at `packages/runner/src/server/runner-signalr.ts:94`; `RunnerHost.onDispatchReconnected()` sends an out-of-cycle heartbeat at `packages/runner/src/runtime/host.ts:105`; runner tests cover both manual and callback paths.
- Genuine runner loss remains unavailable: the heartbeat path never unregisters or marks stale runners online without a fresh heartbeat value; `RunnerStatusService.DeriveStatus()` still returns stale/offline based on runtime heartbeat age before considering connection state at `packages/server/src/Mohist.Server/Runner/Services/RunnerStatusService.cs:137`, and no liveness timeout or orphaned-task recovery behavior is changed.

## Verification

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner -- runner-host.spec.ts runner-signalr.spec.ts` passed: 2 files, 33 tests.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~RunnerHubSpecs` passed: 2 tests.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~RunnerHeartbeatConnectionApiSpecs` passed: 8 tests.

<promise>PASS</promise>
