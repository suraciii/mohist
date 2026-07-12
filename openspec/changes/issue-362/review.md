# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/DeadLetterRoutes.cs`
  Evidence: `UsesLoopbackOnlyListener` maps the unauthenticated routes for any specifically bound address, including `http://192.168.1.10:3456`, because only wildcard hosts are considered public at lines 101-113 and 132-133. The per-request proxy defense at lines 90-99 relies on a finite, client-controlled header denylist at lines 118-126. A loopback proxy can omit or strip those headers and forward a remote request with `Host: localhost`, making it appear direct. This violates the local-only and proxy-rejection requirement while exposing event payloads and handler replay. [disallowed:security-posture]
  SuggestedAction: Do not map these routes unless every listener is a real loopback listener. Use a non-proxyable local transport or authenticated operator authorization; request-header checks cannot prove a caller was direct.
  Verification: Bind the server to a non-loopback address behind a loopback proxy that strips forwarding headers. A remote request to list or redeliver must be unreachable and must not invoke the handler.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs`
  Evidence: The stable session is opened without trigger labels at lines 77-83, its durable job is submitted at lines 99-102, and the correlation labels are written only in a second `OpenAsync` at lines 104-112. If that final persistence fails, `AgentSubscriptionDispatchHandler` logs and swallows the error at lines 77-92, so the dispatcher settles the source event while the running job has no trigger event/subscription correlation. This regresses the Agent subscription visibility contract and prevents reliable traceability of replayed launches. [disallowed:public-contract]
  SuggestedAction: Include trigger labels in the initial session open, before submitting the job, and preserve stable identities on replay.
  Verification: Make only the second session write fail after `EnsureSubmittedAsync` succeeds; assert the persisted session still has both trigger labels and the event outcome remains auditable.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs`, `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`
  Evidence: `AgentJobGrain` checks runtime status and capacity before calling the runner at lines 375-402, but `AssignAgentJobAsync` only validates the work shape and duplicate identity at `RunnerGrain.cs:181-191`, then unconditionally persists the work at lines 193-216. A runner can unregister after the snapshot and still accept work; two jobs can both observe an empty one-slot runner and both be accepted. This violates runner availability and configured capacity, and turns the new durable replay path into work that may never run. [disallowed:product-behavior]
  SuggestedAction: Make `AssignAgentJobAsync` the atomic admission boundary: reject offline runners and enforce the slot limit before inserting the work. Keep the caller-side check only as an optimization.
  Verification: Race unregister against assignment and submit two jobs concurrently to a one-slot runner; the first must be admitted and the other must remain pending, with no offline assignment.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: dead-letter diagnostics API
  Evidence: Retry exhaustion stores `Exception.Message` as the response-safe error at `EventDispatcherService.cs:256-292`, and `DeadLetterRoutes.cs:48` and `:77` return that value unchanged. Omitting `ErrorStack` does not redact a stack embedded in an exception message, for example an exception constructed with `Environment.StackTrace`. The route test checks only that `errorStack` is absent at `DeadLetterRoutesSpecs.cs:46`.
  SuggestedAction: Derive a bounded, stack-free diagnostic summary for the operator response and retain raw diagnostics only in server logs or protected storage.
  Verification: Cause a handler to throw a message containing a stack trace; list and redelivery responses must contain no stack frames or file paths.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/TableRenderer.Events.cs`
  Evidence: The API supplies the recovery state at `DeadLetterRoutes.cs:51-52`, but the default `mo event dead-letter list` table renders no `status` column at `TableRenderer.Events.cs:19-32`. A row left `Redelivering` after successful delivery but failed resolution is indistinguishable from a safely retryable `Pending` row, despite the explicit ambiguous-state design.
  SuggestedAction: Render recovery status in the default table and cover `Redelivering` output.
  Verification: List a `Redelivering` row using table output and assert that its state is displayed.
  Status: open

- [ID: item-6]
  Severity: minor
  Scope: `packages/cli/Mohist.Cli/TableRenderer.Events.cs`
  Evidence: Handler-originated dead-letter errors are rendered directly at line 25. `TableRenderer.Truncate` removes only text after `\n` at lines 326-335, leaving carriage returns and ANSI control sequences intact. A poisoned handler error can therefore alter terminal output for a local operator.
  SuggestedAction: Strip or escape terminal control characters from all table cells before rendering.
  Verification: Return an error containing `\r` and ANSI erase/color sequences; table output must display inert text without terminal control effects.
  Status: open

- [ID: item-7]
  Severity: minor
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/CloudEventBusServiceCollectionExtensions.cs`
  Evidence: Reflection registration creates `Subscription` values from attributes without pattern validation at lines 50-61, while the dispatcher marks an event with no matching handler as delivered at `EventDispatcherService.cs:190-204`. The existing validator is private to `InMemoryEventBus` at lines 86-107 and that bus is not a required dispatcher dependency. A malformed static subscription pattern can therefore silently drop a required domain reaction instead of failing during startup.
  SuggestedAction: Validate every discovered subscription pattern during service registration or in `EventDispatcherService` construction.
  Verification: Add a handler with an invalid wildcard pattern and assert that host construction fails before any event can be marked dispatched.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: dispatcher, workflow-handler, and AgentJob recovery coverage
  Evidence: `WorkflowGrainFixture.cs:50-63` adds a subscription to `InMemoryEventBus`, but its silo setup registers neither `EventDispatcherService` nor the corresponding subscription set at `GrainTestConfig.cs:261-279`; the stage-lock specs instead instantiate and invoke the handler directly at `StageLockSpecs.cs:346-367`. The FIFO unit spec records handler observations but asserts only mark order at `EventDispatcherSpecs.cs:83-114`. AgentJob persistence tests deactivate only a config-less `AgentJobInput` at `AgentJobGrainPersistenceSpecs.cs:26-47`, while the `JsonElement` configuration case never deactivates at `AgentJobGrainSpecs.cs:502-545`. These gaps leave actual workflow handler activation, handler ordering, and the previously fragile serialized Agent configuration recovery unproven.
  SuggestedAction: Add deterministic integration coverage that appends a real workflow stage event and observes its registered handler through the dispatcher, assert handler order directly, and deactivate/reactivate a job containing non-null `AgentConfig` before replay.
  Verification: Run the new focused tests repeatedly with fake time and no direct handler invocation, then confirm the source row, side effect, and durable state all converge.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: server test suite
  Evidence: `npm test` passed, but retained 3 architecture-test skips and 9 server-spec skips. The skips predate this candidate and do not fail the current test run.
  SuggestedAction: Remove or replace skipped coverage in its owning issues under the repository's no-skipped-tests policy.
  Status: pre-existing

## Acceptance Criteria Assessment

- Cluster-singleton reminder wiring and startup activation are present in `DispatcherGrain.cs:18-65` and `DispatcherActivationService.cs:15-18`; reminder registration is covered by `DispatcherStartupSpecs.cs:23-34`.
- The single four-table undelivered query and persisted-origin marking are implemented in `EventStore.cs:180-268`. Serial fan-out, retry, per-row settlement, and atomic poison routing are implemented in `EventDispatcherService.cs:82-256` and `DeadLetterStore.cs:25-73`.
- Deliver-before-mark redelivery and idempotent absorption are covered in `EventDispatcherSpecs.cs:248-283`; dead-letter persistence, querying, and recovery-state storage are covered in `DeadLetterStoreSpecs.cs:164-233`.
- The required manual recovery surface exists in `DeadLetterRoutes.cs:19-85` and `MohistCliCommands.Event.cs:18-84`, but its access boundary remains unsafe (item-1). Agent replay durability is incomplete due to correlation loss and runner-admission defects (items 2-3), with coverage gaps in item-8.

## Verification

- `git diff --check e594b8c4f^..HEAD` passed.
- `npm test` passed: CLI 870, server unit 1363, architecture 24 passed / 3 skipped, server specs 2843 passed / 9 skipped, Web 4596, runner 1007.

<promise>FAIL</promise>
