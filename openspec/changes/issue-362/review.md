# Review Report

## Result: PASS

The post-repair snapshot resolves all three blocking issues from the prior review (dead-letter write propagation, idempotent absorption test, ErrorStack capture). All 5058 tests pass (865 CLI + 1356 unit + 24 arch + 2813 spec), zero build warnings.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `DispatcherGrain.cs:78` (`RegisterOrUpdateReminder`)
  Evidence: `OnActivateAsync` receives a `CancellationToken ct` but does not propagate it to `RegisterOrUpdateReminder`. The Orleans API accepts an optional `CancellationToken` parameter. If activation is cancelled, the reminder registration still proceeds.
  SuggestedAction: Pass `ct` to `RegisterOrUpdateReminder(ReminderName, _options.ReminderDueTime, _options.ReminderPeriod)`.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `NoopDeadLetterStore` duplication
  Evidence: Two identical copies exist: `Mohist.Server.UnitTests.Support.NoopDeadLetterStore` and `Mohist.Server.SpecTests.Support.NoopDeadLetterStore`. The test projects follow a pattern of per-project fake duplication (same as `NoopEventStore`), so this is not a defect but a maintenance burden.
  SuggestedAction: Consider a shared test support project, or accept the duplication as intentional per-project isolation (consistent with existing `NoopEventStore` pattern).
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: Reminder-driven delivery test gap
  Evidence: All spec tests drive ticks via `PulseAsync` (immediate). The `OnActivateAsync_RegistersPersistedReminderWithConfiguredCadence` test verifies reminder registration but does not verify the reminder actually fires and triggers delivery. The design (D1) calls for at most one integration spec asserting the reminder fires.
  SuggestedAction: Add an integration spec that waits for a real reminder tick (using the in-memory reminder table's fire mechanism) and asserts delivery occurred without an explicit `PulseAsync` call.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: `event-dispatch` spec mentions three truth tables; implementation covers four
  Evidence: Issue body says three (`WorkflowRunEvents` + `IssueEvents` + `EpicEvents`); the implementation covers all four (including `AgentSessionEvents`). `design.md` D3 documents this and resolves it in favor of four. `ListUndeliveredAsync` already UNIONs all four. No code diverges from specs.
  SuggestedAction: Confirm with the issue author that AgentSession delivery is desired (safe per `[Subscription(Type="*")]` contract).
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: `ReceiveReminder` passes `CancellationToken.None`
  Evidence: `IRemindable.ReceiveReminder` does not accept a `CancellationToken` parameter, so `CancellationToken.None` is the only option. The Orleans reminder API does not provide a shutdown token to `ReceiveReminder`.
  Status: by-design

## Acceptance Criteria Verification

| AC | Criterion | Evidence |
|----|-----------|----------|
| AC1 | Cluster-singleton grain, self-waking, single-query over all tables | `IDispatcherGrain.cs:17` (fixed key "dispatcher"), `DispatcherGrain.cs:18-83` (RegisterOrUpdateReminder + ReceiveReminder), `EventDispatcherService.cs:84` (ListUndeliveredAsync — single UNION), `DispatcherGrainSpecs.cs:96-103` (resolve by fixed key), `DispatcherGrainSpecs.cs:119-133` (reminder registered), `MohistSiloRegistration.cs:66` (singleton registration) |
| AC2 | Fan-out by type, retry, dead-letter on exhaustion | `EventDispatcherService.cs:144-156` (fan-out loop + DeadLetterAsync), `EventDispatcherService.cs:192-227` (InvokeWithRetryAsync with attempt cap), `EventDispatcherSpecs.cs:138-178` (exhaustion writes DL + marks dispatched + stops retrying), `DispatcherGrainSpecs.cs:138-159` (spec-level poison → dead-letter) |
| AC3 | Per-row mark; per-stream FIFO, no reorder, no skip | `EventDispatcherService.cs:171-183` (MarkDispatchedAsync after all settled), `EventDispatcherSpecs.cs:76-108` (FIFO test: events enqueued out of order, marked in (Source, Id) order), `FakeEventStore.cs:92-94` (OrderBy Source, Id) |
| AC4 | At-least-once with crash recovery; handler idempotent absorption | `EventDispatcherSpecs.cs:213-247` (ThrowOnMark → row stays undelivered → re-delivered next tick; IdempotentRecorder proves Handler invoked twice but side effect once) |
| AC5 | Poison → dead-letter, queryable, manually re-deliverable | `IDeadLetterStore.cs:12-19` (WriteAsync/QueryAsync/GetAsync), `DeadLetterStore.cs:18-57` (Write + Query with handler filter), `DeadLetterStoreSpecs.cs` (write/get/query with and without filter), `EventDispatcherService.cs:107-136` (RedeliverAsync), `EventDispatcherSpecs.cs:389-422` (RedeliverAsync re-dispatches to matching handlers), `DeadLettersMigrationSpecs.cs` (table + indexes verified) |

<promise>PASS</promise>
