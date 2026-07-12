# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`, Agent-job recovery after Runner activation loss
  Evidence: `OnActivateAsync` restores only slots and outstanding Agent work (`83-89`), leaving `_status` at its Offline initializer and `_info` null. A subsequent poll calls `TouchPresenceAsync`, which returns while `_info` is null (`195-204`), then `DispatchService` obtains null runner info and returns before `ReconcileAgentJobsAsync` (`64-68`). Thus, after the Runner grain/silo is lost, an accepted Agent job is not reoffered by normal empty polls and instead expires through the job timeout (`AgentJobGrain.cs:64-67,568-595`). This violates the required lost-activation recovery and reoffer behavior.
  SuggestedAction: Persist or otherwise recover the Runner identity needed by a poll, and make a post-reactivation poll restore online presence and reconcile outstanding Agent work without requiring a separate heartbeat or registration.
  Verification: Register a runner, accept an Agent job, force-deactivate the Runner and AgentJob grains, then poll with empty `inFlight` and `awaitingAck`. The same stable Agent dispatch must be returned before advancing the fake clock past `JobTimeout`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs` and `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs`, capacity update versus poll
  Evidence: The poll route reads slots before admission to the poll gate (`RunnerRoutes.cs:115-117`). `RunnerGrain.UpdateAsync` can then change the persisted/current slot count independently (`RunnerGrain.cs:503-519`), while `DispatchService` later computes spare capacity from the stale caller-supplied value (`DispatchService.cs:70-85`). For example, a poll that read 2 slots, followed by an update to 1 while one Agent job is active, still computes one spare slot and can claim a workflow. The runner then has two active works against its new one-slot bound. The current gate prevents a new Agent admission during reconciliation, but it does not serialize the slot snapshot and workflow claim with `UpdateAsync`.
  SuggestedAction: Obtain the capacity snapshot as part of the Runner-grain poll admission operation, and serialize a capacity update against that admission so each poll is linearized before or after the update.
  Verification: Start a poll after reading two slots, reduce the Runner to one slot before poll admission, and retain one active Agent work. The poll must not claim or return workflow work.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Support/InboxProjectionTestSupport.cs`, atomic inbox-hint coverage
  Evidence: The new test double's caller-context overload ignores the supplied `MohistDbContext` and invokes an `IEventPublisher` immediately (`324-335`). Consequently, `InboxProjectionHandlerRealtimeHintSpecs` proves rollback only when that publisher throws before the handler's final `SaveChangesAsync`; it does not exercise production `EventStore.AppendAsync(MohistDbContext, ...)`, the persisted hint row, or rollback of both database rows in their shared transaction. The acceptance criterion explicitly requires the projection and durable hint to commit atomically.
  SuggestedAction: Add an SQLite integration spec using the real `EventStore` that asserts one inbox row and one persisted hint after success, and neither after a failure at the shared-transaction commit boundary.
  Verification: Inject a deterministic failure after both rows are staged, replay the source event with the failure removed, and assert zero/zero rows before replay and exactly one/one rows afterward.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: new dispatcher, dead-letter, Agent, and Runner source files
  Evidence: The candidate adds extensive XML comments that restate implementation mechanics, for example `EventDispatcherService.cs:9-43` and `AgentJobGrain.cs:13-24`, despite the repository convention that code should be self-explanatory and comments reserved for non-obvious rationale.
  SuggestedAction: Retain only comments that explain invariants or external constraints and remove descriptive narration in a focused cleanup.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Support/InboxProjectionTestSupport.cs`
  Evidence: Existing shared test helpers read `DateTimeOffset.UtcNow` at lines `49`, `63`, `130-131`, and `196`, contrary to the repository's deterministic-time rule. These lines predate this candidate; the issue change only updates the event-store fake in that file.
  SuggestedAction: Move the helper to a fixed or injected time source when its owning inbox-test cleanup is scheduled.
  Status: pre-existing

## Acceptance Criteria Assessment

- The fixed-key reminder grain and startup activation are present in `DispatcherGrain.cs:18-65` and `DispatcherActivationService.cs:6-20`; the focused reminder/failover specs passed.
- The single four-table pull, `(Source, Id)` ordering, origin-aware marking, retry, and dead-letter settlement are present in `EventStore.cs:180-268`, `EventDispatcherService.cs:82-219`, and `DeadLetterStore.cs:25-73`.
- The required deliver-before-mark redelivery test is present in `EventDispatcherSpecs.cs:275-311`; the focused unit slice passed.
- The durable Agent recovery and shared-capacity acceptance criteria remain unmet by items `item-1` and `item-2`.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore --filter "FullyQualifiedName~EventDispatcherSpecs|FullyQualifiedName~OperatorDiagnosticTests|FullyQualifiedName~OperatorCredentialTests|FullyQualifiedName~MohistServiceGraphRegistrationTests"` passed: 29 tests.
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~DispatcherGrainSpecs|FullyQualifiedName~DispatcherStartupSpecs|FullyQualifiedName~DeadLetterStoreSpecs|FullyQualifiedName~DeadLetterRoutesSpecs|FullyQualifiedName~DeadLettersMigrationSpecs|FullyQualifiedName~EventStoreScopedAppendSpecs|FullyQualifiedName~EventDeliveryIndexSpecs"` passed: 53 tests.
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~AgentJobOwnerKindSpecs|FullyQualifiedName~AgentJobGrainPersistenceSpecs|FullyQualifiedName~AgentLauncherSpecs|FullyQualifiedName~InboxProjectionHandlerRealtimeHintSpecs"` passed: 40 tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore --filter "FullyQualifiedName~CliEventDeadLetterCommandSpecs"` passed: 9 tests.
- `git diff --check 4f190c5bf^..HEAD` and `tasks.json` JSON parsing passed.

<promise>FAIL</promise>
