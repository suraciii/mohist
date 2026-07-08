# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs`
  Evidence: A failed event-aware workflow save now marks the activation reload-required at `WorkflowGrain.cs:533-545`, but several read/query entry points still return the dirty in-memory `_run` without checking that flag: `GetRunStatusAsync` at `WorkflowGrain.cs:365-368`, `IsStoppedOrTerminalAsync` at `WorkflowGrain.cs:370-374`, `GetAssignedWorkerIdAsync` at `WorkflowGrain.cs:376-379`, `GetCurrentWorkIdAsync` at `WorkflowGrain.cs:381-386`, and `GetActiveWorkAsync` at `WorkflowGrain.cs:388-391`. After an event append failure rolls back the state/event transaction, callers can observe state that was never committed. A concrete downstream path is `IssueGrain.TryReuseActiveWorkflowAsync` reading `IsStoppedOrTerminalAsync` at `IssueGrain.cs:242-249`; after a rolled-back stop event, it can clear the issue's workflow reference based on the dirty stopped state. [disallowed:product-behavior-change]
  SuggestedAction: Apply the same reload-required guard or reload behavior to all workflow read/query methods that expose `_run`, and add a spec that forces a workflow event append failure, then calls `GetRunStatusAsync` and `IsStoppedOrTerminalAsync` on the same activation and verifies they do not return rolled-back state.
  Verification: Focused server specs passed, but they only cover persisted rollback and deactivation: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~EventStoreScopedAppendSpecs|FullyQualifiedName~EventBusSpecs|FullyQualifiedName~TransactionalEventAppendSpecs|FullyQualifiedName~IssueTransactionalEventAppendSpecs|FullyQualifiedName~AgentSessionTransactionalEventAppendSpecs|FullyQualifiedName~IssueGrainEventSaveFailureSpecs|FullyQualifiedName~WorkflowStateSpecs|FullyQualifiedName~AgentSessionGrainPersistenceSpecs|FullyQualifiedName~IssueWorkflowCompletionHandlerSpecs"` passed 69 tests.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs`
  Evidence: `CommitAsync` and `FlushAsync` quarantine the activation after an event-aware session save fails (`AgentSessionGrain.cs:716-727` and `AgentSessionGrain.cs:623-640`), but `GetAsync` still returns `_session` directly at `AgentSessionGrain.cs:677-680`. Because attach/runtime/recovery transitions mutate the live session object before the store call, a caller can read a runtime binding, usage, or recovery state that the state/event transaction rolled back. `OnDeactivateAsync` also calls `FlushAsync` unconditionally at `AgentSessionGrain.cs:57-63`, so a quarantined activation throws during deactivation instead of cleanly skipping the dirty flush. [disallowed:product-behavior-change]
  SuggestedAction: Reject or reload in `GetAsync` when `_sessionReloadRequired` is set, and make deactivation skip dirty flushes after quarantine. Add a grain spec that injects an event-aware save failure from `AttachPhysicalSessionAsync` or runtime flush, then asserts `GetAsync` does not expose the rolled-back state and deactivation does not attempt another dirty flush.
  Verification: `AgentSessionTransactionalEventAppendSpecs` covers store-level rollback; `AgentSessionGrainPersistenceSpecs` covers fake state-store failure but does not assert `GetAsync`/deactivation behavior after quarantine.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs`
  Evidence: `StartWorkflowAsync` starts and commits the workflow run before the issue records `WorkflowRunId` and appends its issue event rows (`IssueGrain.cs:216-231`). If the new event-aware `SaveIssueAsync` fails at `IssueGrain.cs:635-663`, the issue transaction rolls back but the workflow run and its workflow event rows remain durable. A retry chooses a new `wrId` at `IssueGrain.cs:133-134` because the issue never persisted the first run id, leaving an orphaned active workflow. This is a new realistic failure mode because issue event-row writes now propagate instead of being swallowed. [disallowed:data-safety]
  SuggestedAction: Make workflow start and issue save idempotent around the chosen `wrId`, or persist a recoverable start intent before starting the workflow, or compensate/stop the workflow if the issue state/event transaction fails. Add a spec that fails the `IssueWorkStarted` event append after `wfGrain.StartAsync` succeeds, retries `StartWorkAsync`, and asserts no duplicate or orphan workflow run remains.
  Verification: Existing issue save-failure specs cover quarantining a dirty issue activation after a failed save, but they do not cover the start workflow side effect that happens before the issue save.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs` and `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowWorkLifecycle.cs`
  Evidence: Stage lock mutations still happen outside the workflow state/event transaction. `ClaimNextAsync` acquires a sequential lock before `_workLifecycle.ClaimWorkAsync` persists the task-start event (`WorkflowGrain.cs:312-318`; event-aware save occurs at `WorkflowWorkLifecycle.cs:123-128`). Retry/rerun/stop paths release locks before committing the rollback/retry/stop events (`WorkflowGrain.cs:200-203`, `WorkflowGrain.cs:209-212`, `WorkflowGrain.cs:228-232`, and `WorkflowWorkLifecycle.cs:85-99`). If event append fails, the workflow transaction rolls back but the lock grain has already changed state, so locks can be leaked or released while the persisted workflow still requires them. [disallowed:architectural-judgment]
  SuggestedAction: Move lock acquire/release finalization after successful workflow state/event commit with explicit compensation on commit failure, or make lock changes event-dispatch driven from committed rows. Add failure-injection specs for claim, stop, retry, and rerun that assert lock state remains consistent when event append fails.
  Verification: Existing `WorkflowStateSpecs` cover rollback of workflow state/event rows but do not assert lock-grain state under event append failure.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs` and `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs`
  Evidence: `InboxProjectionHandler` inserts the inbox item first (`InboxProjectionHandler.cs:160-164`) and only then publishes the realtime hint through `IEventPublisher` (`InboxProjectionHandler.cs:166-190`). Under issue-361 semantics that publish appends a durable event row. If hint append fails after the inbox row commits, replay cannot repair it because the duplicate insert path returns before publishing. The hint also uses one global source, `/mohist/inbox` (`InboxProjectionHandler.cs:73`), while `EventStore.NextIdAsync` assigns ids via `MAX(Id)+1` per source (`EventStore.cs:319-331`); concurrent hint appends can race on the `(Source, Id)` key. [disallowed:product-behavior-change]
  SuggestedAction: Persist the inbox row and hint outbox row atomically, or make duplicate replay detect and repair a missing hint. Use per-item/per-project hint sources or DB-generated/retried ids for the global inbox source. Add specs for publish-fails-after-insert replay and concurrent hint appends.
  Verification: Existing realtime hint specs assert one publish and swallowed publish failure with fakes, but they do not exercise the real durable `EventStore` path or replay repair.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs`
  Evidence: The scoped append route falls through to `WorkflowRunEvents` for any source that is not agent-session, issue, or epic (`EventStore.cs:51-121`). `MarkDispatchedAsync` later rejects unrecognized sources except workflow and `/mohist/inbox` (`EventStore.cs:180-215`). A typo or future `IEventPublisher` caller can therefore create an undelivered row that the delivery-progress API cannot mark dispatched. [disallowed:public-contract-change]
  SuggestedAction: Make append routing explicit and reject unknown sources before staging rows, or introduce a first-class generic/inbox origin that append, list, and mark-dispatched all understand. Add a spec that appending an unknown source is rejected or can be listed and marked consistently.
  Verification: `EventStoreDeliveryProgressSpecs.MarkDispatchedAsync_ThrowsWhenSourceIsUnrecognized` covers mark rejection, but no test covers append accepting the same unrecognized source.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/EventBusSpecs.cs`
  Evidence: The typed `IEventPublisher.PublishAsync<TData>` requirement says the row must preserve supplied type, source, subject, data, and extensions. The typed coverage at `EventBusSpecs.cs:27-49` and `EventBusSpecs.cs:109-131` checks no handler invocation and only type/source for one call. The fuller subject/data/extensions assertion is only on the raw `CloudEvent` overload at `EventBusSpecs.cs:79-104`.
  SuggestedAction: Add a typed-overload spec that passes subject, extensions, and a payload property, then asserts the single appended envelope preserves all fields.
  Verification: Focused event bus specs passed, but this field-preservation case is not covered.
  Status: open

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: subscription handler failure semantics
  Evidence: Several handlers still swallow or detach failures (`IssueWorkflowCompletionHandler.cs:86-96`, `WorkflowStageLockReleaseHandler.cs:43-82`, `RunnerWorkflowTerminalStatusHandler.cs:60-93`, `AgentSubscriptionDispatchHandler.cs:77-97`, `InboxProjectionHandler.cs:88-110`). This matches the explicit non-goal of not changing handler reaction logic in issue 361, but a future dispatcher cannot make retry/dead-letter decisions if handlers hide retryable failures internally.
  SuggestedAction: In the dispatcher issue, define which subscriptions are best-effort and which must propagate failures so `DispatchedAt`/DLQ semantics are meaningful.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/CloudEventBusServiceCollectionExtensions.cs`
  Evidence: Generic `ICloudEventHandler<TData>` implementations are not discovered by the initial `handlerTypes` filter because `typeof(ICloudEventHandler<>).IsAssignableFrom(t)` does not match closed generic implementations (`CloudEventBusServiceCollectionExtensions.cs:22-26`). The later closed-interface logic at `CloudEventBusServiceCollectionExtensions.cs:42-50` only runs for types that survived that filter. This appears pre-existing because the file is not changed by issue 361, but it means generic handlers such as `EpicAutoDoneHandler` are not registered for the future dispatcher.
  SuggestedAction: Detect closed generic interfaces in the initial scan and add a registration spec covering generic handlers.
  Status: pre-existing

- [ID: item-10]
  Severity: warning
  Scope: `npm test` / `packages/web`
  Evidence: Full `npm test` fails outside this server-side candidate: .NET suites passed (`Mohist.Cli.Tests` 865 passed; `Mohist.Server.ArchTests` 24 passed, 3 skipped; `Mohist.Server.SpecTests` 4161 passed, 9 skipped), runner passed 1031 tests, but web failed `src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx` because it could not find `08:00:00.000`. No web files are in the issue-361 diff.
  SuggestedAction: Track/fix the web timestamp expectation separately, or rerun after the owning web change is repaired.
  Status: out-of-scope

<promise>FAIL</promise>
