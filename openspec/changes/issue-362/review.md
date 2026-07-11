# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/AgentSubscriptionDispatchHandler.cs`
  Evidence: The issue explicitly keeps `AgentSubscriptionDispatchHandler` as a best-effort, exception-swallowing consumer whose contract must not change. This candidate removes its exception boundary: `HandleAsync` delegates directly to `DispatchAsync` at line 77, and a launch failure now escapes from `LaunchAsync` at lines 141-146. The new regression test asserts that changed behavior at `AgentSubscriptionDispatchHandlerSpecs.cs:422-445`. The dispatcher will consequently retry and dead-letter Agent subscription failures, which is a public behavior change and conflicts with the issue's stated Agent-event contract. [disallowed:public-contract]
  SuggestedAction: Restore the handler's best-effort catch-and-log behavior, including a regression test that a launcher failure is swallowed while the source event may later be replayed. If durable retry/dead-letter behavior is intended instead, amend the issue/spec before implementing that contract change.
  Verification: Dispatch an event through a failing `IAgentLauncher`; assert the handler completes successfully, the dispatcher marks the event as settled, and the logged failure remains observable without creating a dead letter.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/DeadLetterRoutes.cs`, operator access boundary
  Evidence: The new operator endpoints authorize solely from `HttpContext.Connection.RemoteIpAddress` at lines 23-24 and 59-60. A remote caller routed through a loopback reverse proxy is therefore seen as `127.0.0.1` and can list event payloads or invoke handler replay. The server supports non-loopback binding in `Program.cs:41-46`, while no authenticated operator boundary exists. This does not meet the requirement that only loopback callers may inspect or redeliver dead letters. [disallowed:security-posture]
  SuggestedAction: Keep these routes unreachable through a proxy/public listener until authentication exists, or add a trusted-proxy-aware client-address policy together with authenticated operator authorization.
  Verification: Put the server behind a loopback reverse proxy and issue a request from a non-loopback client. It must receive `403` and must not trigger a redelivery side effect.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/DispatcherGrainSpecs.cs`
  Evidence: The new reminder/failover test uses `signal.WaitAsync(TimeSpan.FromSeconds(10))` at lines 213-223. `design/testing.md:53-59` forbids wall-clock waits and requires an awaitable signal or injected fake-time progression. This introduces a timing-dependent failure path into the highest-risk recovery coverage.
  SuggestedAction: Make the test complete from the existing deterministic signals and fake-time advancement without a real-time timeout; use the suite's deterministic failure mechanism for diagnostics.
  Verification: Run the dispatcher failover spec repeatedly under CPU contention and confirm it contains no `WaitAsync(TimeSpan)`, `Task.Delay`, or wall-clock deadline.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: server test suite
  Evidence: `dotnet test Mohist.sln -p:SkipWebBuild=true --no-restore` passed, but retained 3 architecture-test skips and 9 server-spec skips. These predate the candidate and did not fail the current test run.
  SuggestedAction: Track and remove the skipped coverage separately, following the repository rule against skipped tests masking uncertainty.
  Status: pre-existing

## Acceptance Criteria Assessment

- Cluster singleton startup and reminder wiring are present in `DispatcherGrain.cs:18-65` and `DispatcherActivationService.cs:15-18`; focused reminder/failover coverage passes in `DispatcherGrainSpecs.cs:139-177`.
- The four-table, single-query pull and origin-aware settlement are implemented in `EventStore.cs:180-268`; serial dispatch and atomic poison settlement are implemented in `EventDispatcherService.cs:82-96` and `197-211`.
- Retry, per-handler isolation, dead-letter persistence/recovery, API, and CLI coverage are present and passed in the focused suites.
- At-least-once deliver-before-mark behavior is covered by `EventDispatcherSpecs.cs:217-253`, and durable Agent job/work identities are covered by `AgentJobGrainPersistenceSpecs.cs:52-84` and `AgentLauncherSpecs.cs:138-203`.
- The Agent consumer contract and loopback-only operator requirement remain unsatisfied by items 1 and 2, so the post-review candidate cannot pass.

## Verification

- `git diff --check origin/master...HEAD` passed.
- Focused dispatcher/dead-letter/Agent server tests passed: 36 unit tests and 63 specs.
- Focused CLI dead-letter tests passed: 5 tests.
- `dotnet test Mohist.sln -p:SkipWebBuild=true --no-restore` passed: CLI 870, server unit 1361, architecture 24 passed / 3 skipped, server specs 2836 passed / 9 skipped.

<promise>FAIL</promise>
