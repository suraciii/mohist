# Review Report

## Result: PASS

## Repaired Items

- [ID: item-repair-1]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Events/Hosting/DispatcherActivationService.cs:11
  Evidence: The XML doc comment said "activation under any other key throws on `OnActivateAsync`" but the implementation was changed to silently log+no-op (not throw) in `EventDispatcherGrain.OnActivateAsync`. The stale comment could mislead a future maintainer into expecting an exception that never comes.
  Verification: `dotnet build Mohist.sln` passes with 0 warnings (TreatWarningsAsErrors).
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-fu-1]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Infrastructure/Events/IDeadLetterStore.cs:27 and packages/server/src/Mohist.Server/Infrastructure/Data/Events/DeadLetterStore.cs:92-150
  Evidence: `IDeadLetterStore.RetryAsync` (re-null source row `DispatchedAt` so the dispatcher re-delivers via the normal cycle) is implemented, tested (`DeadLetterStoreSpecs.RetryAsync_ReNullsSourceRow_AndPreservesDeadLetter`, `RetryAsync_RoutesByOrigin`), but never wired to any production API or CLI surface. The operator re-delivery path uses `EventDispatcherService.RedeliverAsync` → `StartRedeliveryAsync` → direct handler dispatch → `ResolveAsync` instead. `RetryAsync` is dead code on the production interface.
  SuggestedAction: When the dead-letter surface is next revisited, either expose `RetryAsync` as an alternative recovery path (re-queue through the dispatcher) or remove it from the interface and its tests.
  Status: follow-up

- [ID: item-fu-2]
  Severity: follow-up
  Scope: openspec/changes/issue-362/specs/event-dispatch/spec.md:22,129,133,138
  Evidence: The `event-dispatch` spec uses `Pulse()` as the name for the immediate-trigger entry point (e.g., "A `Pulse()` entry point SHALL trigger one immediate tick"). The implementation exposes this as `DispatchNowAsync` (`IEventDispatcherGrain.cs:20`). The `event-dispatcher` spec correctly uses `DispatchNowAsync`. The behavior is correctly implemented and tested; only the spec text uses a different name.
  SuggestedAction: Align the `event-dispatch` spec to use `DispatchNowAsync` instead of `Pulse()`, or note the alias explicitly.
  Status: follow-up

- [ID: item-fu-3]
  Severity: follow-up
  Scope: openspec/changes/issue-362/design.md:221,223 (Open Questions 3 and 4)
  Evidence: Open Questions 3 (`FailingHandler` identifier format) and 4 (producer poke wiring) are phrased as unresolved questions, but both have been settled in the implementation: OQ3 uses handler type full name (`EventDispatcherService.cs:293-294`); OQ4 uses direct `IGrainFactory.GetGrain` call (via `EventDispatcherPoke.cs:31-32`). Only OQ2 was marked "Resolved."
  SuggestedAction: Mark OQ3 and OQ4 as resolved with the chosen decision, matching OQ2's format.
  Status: follow-up

- [ID: item-fu-4]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs:48-75
  Evidence: `HandleAsync` catches all non-cancellation exceptions and returns `Task.CompletedTask` (lines 67-74). The `event-dispatcher/spec.md` scenario "Production handler failure reaches the dispatcher" states "the handler SHALL NOT hide the failure by logging-and-returning or detached work." This handler is a best-effort notification dispatcher (the actual delivery runs as fire-and-forget background work via `_dispatcher.Dispatch`), so the catch prevents the dispatcher from retrying transient notification-setup failures. This is a pre-existing design choice that is defensible for a notification handler (notifications are not required domain side effects), but it technically contradicts the spec's letter.
  SuggestedAction: If notification delivery failures should be retryable by the dispatcher, rethrow from `HandleAsync` and let the background dispatcher's own catch handle delivery-side failures. If the current best-effort posture is intentional, document the exception.
  Status: follow-up

- [ID: item-fu-5]
  Severity: follow-up
  Scope: packages/server/tests/Mohist.Server.SpecTests/Support/DispatcherFixture.cs (778 lines) and packages/server/tests/Mohist.Server.UnitTests/SystemSpecs/EventDispatcherSpecs.cs (1004 lines)
  Evidence: Both files exceed the ~700-line / 24 KB comfort zone. The archtest size budget does not currently apply to `Support/` or `SystemSpecs/`, so this is style guidance.
  SuggestedAction: When the next issue touches these specs, split by SUT-prefix or scenario group (e.g., `DispatcherFixture` → silo setup + test handlers + capturing stores; `EventDispatcherSpecs` → per-scenario files).
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-pre-1]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/MohistDbContextModelSnapshot.cs:308-389
  Evidence: `DeadLetters` table in the model snapshot matches the production DbContext + both migrations (`AddDeadLetters`, `HardenDeadLetterRecovery`). Verified by `DeadLettersMigrationSpecs.ModelSnapshot_IncludesDeadLetterRowWithBothIndexes` and `RecoveryMigration_AddsStateAndNaturalKey`. No drift between snapshot and runtime model.
  SuggestedAction: None.
  Status: pre-existing

- [ID: item-pre-2]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Infrastructure/Security/OperatorCredential.cs:1-140
  Evidence: Operator credential handling follows established security conventions: `ISingletonService`, file mode 0600 by default on Unix, symlink rejection (`FileAttributes.ReparsePoint`), fixed-time token comparison (`CryptographicOperations.FixedTimeEquals`), minimum 32-char token. The CLI counterpart checks for loopback `baseAddress` before sending the credential (`MohistCliCommands.Event.cs:101-107`). Reasonable security posture.
  SuggestedAction: None.
  Status: pre-existing

- [ID: item-pre-3]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Events/Hosting/DispatcherActivationService.cs:1-28
  Evidence: `IHostedService` pattern from `EpicReconciliationService`. `StartAsync` is registered after `UseOrleans` in `MohistServiceRegistration.cs:97`, so the silo is up before the activation service calls the grain factory. Verified by `DispatcherStartupSpecs.HostStartup_ActivatesDispatcherAndRegistersReminder`.
  SuggestedAction: None.
  Status: pre-existing

- [ID: item-pre-4]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:86 and packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistSiloRegistration.cs:53-54
  Evidence: `EventDispatcherOptions` is bound from configuration in both host DI (`MohistServiceRegistration`) and silo DI (`MohistSiloRegistration`). The silo path is needed because grain constructors resolve options via silo-scoped DI; the host path covers non-grain consumers (`EventDispatcherService` singleton). Slight redundancy but harmless — `IOptions<>` resolves consistently.
  SuggestedAction: None.
  Status: pre-existing

## Spec Compliance Check

For each acceptance criterion in the issue:

- ✅ **Cluster singleton + single undelivered query**: `EventDispatcherGrain` cluster-singleton via fixed key `__global__` + persistent Orleans reminder. `IEventStore.ListUndeliveredAsync` is a single `UNION ALL` query across `WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents` ordered by `(Source, Id)`. Verified by `DispatcherGrainSpecs.EventDispatcherGrain_DispatchNowRegistersReminderAndRunsCycle`, `OnActivateAsync_RegistersPersistedReminderWithConfiguredCadence`, `DispatcherStartupSpecs.HostStartup_ActivatesDispatcherAndRegistersReminder`.

- ✅ **Per-event-type fan-out + retry + dead letter**: `EventDispatcherService.DispatchOneAsync` matches via `CloudEventTypeMatcher` (exact / `|` / `*` / `prefix.*`), retries with cross-tick exponential backoff governed by `TimeProvider`, dead-letters on exhaustion. Verified by `EventDispatcherSpecs.DispatchAsync_PerHandlerRetry_RecoversOnSecondAttempt_StillMarksDelivered`, `DispatchAsync_ExhaustionWritesDeadLetter_MarksDispatched_AndStopsRetrying`, `DispatchAsync_CrossTickBackoffAndRetryBudgetArePerHandler`.

- ✅ **Per-row marking + per-stream FIFO**: `EventDispatcherService.DispatchAsync` processes rows serially in `(Source, Id)` order, breaks on mark failure. Verified by `EventDispatcherSpecs.DispatchAsync_DispatchesPerStreamFifo_NoSkipNoReorder`, `DispatchAsync_MarkFailure_StopsBeforeNextEventInSameStream`.

- ✅ **At-least-once test coverage**: Verified by `EventDispatcherSpecs.DispatchAsync_DeliverBeforeMarkCrash_RowStaysUndelivered_AndIsRedeliveredOnNextTick` and `DispatcherGrainSpecs.ReminderCallback_DeliversBeforeAndAfterHostingSiloLoss`.

- ✅ **Poison → dead letter, queryable, manually re-deliverable**: Dead letter write (`SettleAsync`), query (`QueryAsync`/`ListByHandlerAsync`/`ListByTimeRangeAsync`), and manual re-delivery (`RedeliverAsync` → `StartRedeliveryAsync` → handler → `ResolveAsync`) all implemented. Operator surface is loopback-only + credential-gated (`DeadLetterRoutes.cs`). CLI surface implemented (`mo event dead-letter list`, `mo event dead-letter redeliver`). Verified by `DeadLetterStoreSpecs`, `DeadLetterRoutesSpecs.Redeliver_RetriesRecordedHandlerAndResolvesRow`, `Redeliver_RejectsProxyCallerWithoutCredentialAndHasNoSideEffect`, `CliEventDeadLetterCommandSpecs`.

<promise>PASS</promise>
