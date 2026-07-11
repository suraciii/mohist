# Review Report

## Result: FAIL

The post-repair candidate passes the full automated suite, but it does not yet provide safe at-least-once Agent delivery or consistent poison-message settlement.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test-determinism
  Evidence: `DispatcherGrainSpecs.cs` constructed three dispatcher events with `DateTimeOffset.UtcNow`, violating the project's no-wall-clock test rule.
  Verification: Replaced those inputs with fixed `EventTime`. `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~DispatcherGrainSpecs|FullyQualifiedName~DispatcherStartupSpecs|FullyQualifiedName~DeadLetterRoutesSpecs|FullyQualifiedName~DeadLetterStoreSpecs|FullyQualifiedName~DeadLettersMigrationSpecs|FullyQualifiedName~AgentLauncherSpecs"` passed 35 tests; `npm test` passed.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs`, `packages/server/src/Mohist.Server/Infrastructure/Data/Events/DeadLetterStore.cs`, `packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs`
  Evidence: On retry exhaustion, `DeadLetterAsync` commits a dead-letter row at `EventDispatcherService.cs:241-261` / `DeadLetterStore.cs:18-23`; the original event is marked in a later independent commit at `EventDispatcherService.cs:181-185` / `EventStore.cs:180-218`. A mark failure after a successful dead-letter write leaves the source row eligible for another delivery. The next tick can either insert a duplicate dead letter or deliver successfully while leaving the first row falsely unresolved. The schema has no uniqueness key for the event and failing handler at `MohistDbContext.cs:481-530`. The same non-atomicity exists between successful manual handler invocation and `DeleteAsync` at `EventDispatcherService.cs:148-153`. [disallowed:data-safety]
  SuggestedAction: Make poison settlement an atomic persistence operation that marks the source event and creates one handler-keyed dead letter in the same transaction, or introduce a unique natural key plus conflict-safe reconciliation. Define recovery state so a successful manual re-delivery cannot remain unresolved after a delete failure.
  Verification: Inject a mark failure after a successful dead-letter insert, then retry both a still-failing and a recovered handler. Assert one accurate dead-letter row, no stale row after recovery, and no duplicate side effect.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs`, `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs`
  Evidence: `AgentLauncher` submits the stable-keyed job at `AgentLauncher.cs:102-116`, then persists the trigger labels used as the replay claim at `AgentLauncher.cs:118-126`. `AgentJobGrain` explicitly stores its lifecycle only in memory at `AgentJobGrain.cs:10-21`, returns from `SubmitAsync` after detached dispatch at `AgentJobGrain.cs:202-224`, and creates a fresh runner work id after activation at `AgentJobGrain.cs:310-337`. A silo crash after label persistence but before durable runner acceptance causes the replay to return early at `AgentLauncher.cs:78-85`, losing the launch. A crash after runner acceptance but before label persistence can replay through a fresh in-memory job and produce another work id. This fails the issue's required duplicate absorption and missed-launch recovery. [disallowed:data-safety]
  SuggestedAction: Persist a trigger-keyed launch claim and durable job/queue record before acknowledging the subscription handler. Replay must resume that durable record, not infer completion from session labels.
  Verification: Add deterministic crash checkpoints after submit, after trigger-label persistence, and after runner acceptance. Reactivate the job/silo and replay the source event; assert exactly one durable job and one runner work, with no missed launch.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs`, `docs/hermes-notifications.md`
  Evidence: The changed handler now awaits and propagates webhook failures at `HermesIssueNotificationHandler.cs:41-60`, so the dispatcher retries and dead-letters them at `EventDispatcherService.cs:167-171`. The published Hermes contract explicitly says failures are logged and swallowed, with no retry queue or DLQ at `docs/hermes-notifications.md:216-222`; issue design D5 also excludes unrelated best-effort channel convergence. The altered test now asserts propagation at `HermesIssueNotificationTests.cs:154-165`.
  SuggestedAction: Restore the documented best-effort behavior, or explicitly approve and document a durable/retryable Hermes contract including webhook idempotency.
  Verification: Simulate a webhook failure during dispatcher delivery and assert the chosen contract: no retry/DLQ for best-effort, or documented retry plus idempotent receiver behavior for durable delivery.
  Status: unresolved

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/DispatcherGrainSpecs.cs`, `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/DispatcherStartupSpecs.cs`
  Evidence: Delivery specs drive `PulseAsync` directly at `DispatcherGrainSpecs.cs:37-90`; reminder coverage only reads the registration row at `DispatcherGrainSpecs.cs:122-136` and `DispatcherStartupSpecs.cs:23-34`. No test fires a reminder without Pulse, moves the fixed-key activation to another silo after a crash, or proves resumption after failover. This leaves the issue's self-waking and self-healing acceptance criteria unverified.
  SuggestedAction: Use the controllable reminder infrastructure with a multi-silo test to fire the reminder without Pulse, deactivate/crash its hosting silo, and assert delivery resumes from the persisted reminder.
  Verification: The test must deliver an appended event only from a reminder callback, then repeat after host-silo loss without using Pulse.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Api/DeadLetterRoutes.cs`, `packages/server/src/Mohist.Server/Program.cs`
  Evidence: The new routes return full event data, extensions, and server exception stacks at `DeadLetterRoutes.cs:15-45`, and allow any caller to re-run a handler side effect at `DeadLetterRoutes.cs:47-72`. They have no authorization boundary; the server permits a non-loopback bind when `Mohist:Host` is `0.0.0.0` or `*` at `Program.cs:41-46`. This exposes operational payloads and replay capability to any network peer in such a deployment. [disallowed:security-posture]
  SuggestedAction: Restrict the routes to authenticated operators, or reject non-loopback exposure until an operator authorization model exists. Avoid returning raw exception stacks by default.
  Verification: An unauthenticated remote request cannot list payloads or invoke re-delivery, while an authorized operator can perform both actions.
  Status: unresolved

- [ID: item-7]
  Severity: minor
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/CloudEventBusServiceCollectionExtensions.cs`, `packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs`
  Evidence: The reflection change now makes the two closed-generic Epic handlers live. Handler registration is singleton at `CloudEventBusServiceCollectionExtensions.cs:24-47`, but each captures `EpicQuerier` at `EpicAutoDoneHandler.cs:20-25` / `48-53`; `EpicQuerier` is conventionally scoped at `EpicQuerier.cs:16-24`. Scope validation can reject this graph, and otherwise the scoped query service is retained from the root scope for the dispatcher's lifetime.
  SuggestedAction: Resolve `EpicQuerier` inside an async scope per delivery, consistent with the scoped access pattern in `AgentSubscriptionDispatchHandler` and `InboxProjectionHandler`.
  Verification: Build the production service graph with scope validation enabled and dispatch a real `IssueCompleted` event through the closed-generic handler.
  Status: open

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Events/Grains/DispatcherGrain.cs`
  Evidence: `OnActivateAsync` receives a cancellation token but does not pass it to reminder registration at `DispatcherGrain.cs:37-42`.
  SuggestedAction: Propagate the activation cancellation token if supported by the Orleans reminder overload.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs`
  Evidence: Epic mutations save state before attempting event persistence at `EpicGrain.cs:48-67`, and `PersistEpicEventsAsync` intentionally catches and suppresses append failures at `EpicGrain.cs:948-993`. A crash or append failure therefore loses the corresponding Epic event permanently, so the dispatcher cannot recover or deliver all event truth-table changes. The issue-specific commits did not introduce this producer behavior, but the candidate depends on durable events for its guarantees.
  SuggestedAction: Make Epic state and event rows commit in the same transaction, as the Issue, WorkflowRun, and AgentSession stores already do.
  Status: pre-existing

## Acceptance Criteria Assessment

- Cluster-singleton wiring, fixed-key activation, four-table pull, fan-out, retry, and CLI/API recovery are present in `IDispatcherGrain.cs`, `DispatcherGrain.cs`, `EventStore.cs:220-250`, `EventDispatcherService.cs`, `DeadLetterRoutes.cs`, and `MohistCliCommands.Event.cs`.
- Per-stream serial mark ordering is implemented and covered by `EventDispatcherSpecs.cs:76-108` and `250-278`.
- The deliver-before-mark unit path is covered by `EventDispatcherSpecs.cs:213-248`, but it proves only a test recorder's local idempotency. The required production Agent replay behavior is invalidated by item-3.
- Query and manual re-delivery surfaces exist, but item-2 means poison-message state is not reliable enough to satisfy the dead-letter acceptance criterion.
- Reminder registration is covered, but actual reminder-driven delivery and failover are missing as described in item-5.

<promise>FAIL</promise>
