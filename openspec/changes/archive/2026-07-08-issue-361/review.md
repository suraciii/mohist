# Review Report

## Result: PASS

## Repaired Items

- None.

## Blocking Items

- None.

## Warning Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:332-361`
  Evidence: `ClaimNextAsync` acquires a sequential stage lock at line 351 (`AcquireStageLocksIfNeededAsync`) before `_workLifecycle.ClaimWorkAsync` calls `CommitAsync` to persist the claim event via the event-aware save. If the event append fails, `CommitAsync` propagates the exception, `ClaimNextAsync` returns without releasing the lock, leaving a held lock on a stage whose claim transaction rolled back. The same pattern is correctly compensated in `RetryAsync`/`RerunAsync`/`RerunFromStageAsync` (try/catch with lock re-acquisition), and `StopAsync` bundles `AbandonRunningWorkAsync` events into its own commit. `ClaimNextAsync` is the only remaining mutation path that acquires a lock before the event-aware save without compensation on failure.
  SuggestedAction: Add a try/catch around the `CommitAsync` call inside `ClaimWorkAsync` (or `ClaimNextAsync`) that releases the acquired lock if the event-aware save fails, matching the Retry/Rerun/RerunFromStage compensation pattern.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests -p:SkipWebBuild=true --no-build --filter "FullyQualifiedName~WorkflowStateSpecs"` (15 passed, 0 failures). No spec currently injects an event-append failure during claim.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:51-121` vs `EventStore.cs:180-215`
  Evidence: The scoped `AppendAsync` falls through to `WorkflowRunEvents` for any unknown source prefix (no explicit rejection). `MarkDispatchedAsync` rejects unknown sources except workflow and `/mohist/inbox`. An unknown-source event can be appended (landing silently in `WorkflowRunEvents`) but cannot be marked dispatched, creating an undeliverable row. The `/mohist/inbox` source was added to `MarkDispatchedAsync`'s workflow branch, closing that specific gap, but the general inconsistency remains. Pre-existing from T-001.
  SuggestedAction: Either reject unknown sources at append time, or make `MarkDispatchedAsync` accept any source that maps to a known table via the same prefix routing. Add a spec that appending an unrecognized source is rejected or round-trips through mark dispatched.
  Verification: `EventStoreDeliveryProgressSpecs.MarkDispatchedAsync_MarksInboxHintRowsStoredInWorkflowRunEvents` now covers the inbox hint. No test covers append of an arbitrary unknown source.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: handler dormant state
  Evidence: All `ICloudEventHandler` implementations (`IssueWorkflowCompletionHandler`, `WorkflowStageLockReleaseHandler`, `RunnerWorkflowTerminalStatusHandler`, `AgentSubscriptionDispatchHandler`, `InboxProjectionHandler`, `EpicAutoDoneHandler`) are registered but dormant — `InMemoryEventBus.PublishAsync` no longer invokes them. Their doc-comments have been updated to reflect the dormant state. The subscription wiring is intact for the future dispatcher (step 3). This matches the design's explicit acceptance of suspended auto-progression during the gap between this issue and the dispatcher.
  SuggestedAction: Land the dispatcher (step 3) immediately after this change. Handler error semantics (swallow vs propagate) should be revisited when the dispatcher adds retry/DLQ.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/CloudEventBusServiceCollectionExtensions.cs:22-26`
  Evidence: Generic `ICloudEventHandler<TData>` implementations are not discovered by the initial `handlerTypes` filter because `typeof(ICloudEventHandler<>).IsAssignableFrom(t)` does not match closed generic implementations. The later closed-interface logic only runs for types that survived the filter. Pre-existing — not changed by issue 361, but affects whether generic handlers like `EpicAutoDoneHandler` are registered for the future dispatcher.
  SuggestedAction: Detect closed generic interfaces in the initial scan and add a registration spec covering generic handlers.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:88-110`
  Evidence: The handler inserts the inbox item before publishing the realtime hint. Under issue-361 semantics the hint is a durable event row. If the hint append fails after the inbox row committed, replay cannot repair the missing hint because the duplicate-insert path returns early. Per design.md#OQ4 this is deferred to step 4; the inbox hint stays on `IEventPublisher` as a durable row for now.
  SuggestedAction: Address in step 4 (dedicated best-effort channel), or atomically persist inbox + hint in step 3.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:947-960`
  Evidence: `EpicGrain.PersistEpicEventsAsync` still appends events post-commit on a separate path with catch-and-log (best-effort). The doc-comment was updated to reflect that epic producer convergence is out of scope for this issue, matching the Non-Goals.
  Status: out-of-scope

- [ID: item-7]
  Severity: info
  Scope: `npm test` / `packages/web`
  Evidence: Web test suite had a pre-existing failure (`TaskProgressPanel.test.tsx` timestamp expectation) unrelated to the server-side changes. No web files in the diff.
  Status: pre-existing

## Verification Summary

- `dotnet build Mohist.sln`: 0 warnings, 0 errors (TreatWarningsAsErrors active)
- `dotnet test packages/server/tests/Mohist.Server.SpecTests`: 4162 passed, 9 skipped, 0 failures
- `dotnet test packages/server/tests/Mohist.Server.ArchTests`: 24 passed, 3 skipped, 0 failures
- `dotnet test packages/cli/tests/Mohist.Cli.Tests`: 865 passed, 0 failures

## Spec Compliance

All acceptance criteria verified against code at HEAD (`da539973d`):

- **AC1 (三处生产者事件行与状态在同一事务)**: `WorkflowRunStore.SaveAsync` (`WorkflowRunStore.cs`: events-aware overload with shared `DbContext`+`BeginTransactionAsync`), `IssueStore.SaveAsync(key, state, events)` (same pattern), `AgentSessionStore.SaveAsync(key, state, events)` (same pattern). Verified by `TransactionalEventAppendSpecs`, `IssueTransactionalEventAppendSpecs`, `AgentSessionTransactionalEventAppendSpecs`.
- **AC2 (发布接口收敛为写事件行)**: `InMemoryEventBus.PublishAsync` delegates to `IEventStore.AppendAsync(envelope)`, no handler invocation. Verified by `EventBusSpecs` (handler not invoked, row appended).
- **AC3 (异常处理形态一致)**: `WorkflowRunStore` — no bare `catch {}`; `IssueGrain` — `SaveIssueAsync` catch block quarantines, no log-and-swallow; `AgentSessionGrain` — `CommitAsync`/`FlushAsync` catch blocks quarantine, no swallow-`InvalidOperationException` or log-and-swallow. All propagate. Verified by `IssueGrainEventSaveFailureSpecs`, `WorkflowStateSpecs` failure-injection tests.
- **AC4 (崩溃不丢事件)**: `TransactionalEventAppendSpecs.SaveAsync_CrashAfterCommit_*` (3 specs covering WorkflowRun, Issue, AgentSession — fresh `DbContext` read proves durability).

<promise>PASS</promise>
