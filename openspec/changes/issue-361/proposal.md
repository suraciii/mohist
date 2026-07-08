## Why

Aggregate state persistence and event emission are split across two separate operations — and for AgentSession, events aren't persisted at all. A process crash between state commit and event write silently loses the event: the state transition is durable but downstream subscribers never learn it happened. Each of the three producers (workflow run, issue, agent session) swallows publish failures differently (bare `catch {}`, log-and-swallow, or swallow-specific-exception), and the in-memory bus dispatches handlers synchronously inside the producer's own call stack — forcing workarounds like detached grain calls and reverse DB lookups to recover identity the producer already knew but didn't stamp onto the event.

This is landing step 2 of the event-bus v2 roadmap (`design/eventbus-v2.md:229`): #360 laid the storage foundation (delivery-progress column, DLQ table, undelivered query); this issue converges the producers onto it.

## What Changes

- **Event rows move inside the state transaction.** WorkflowRun, Issue, and AgentSession state saves append their event rows in the same EF Core database transaction as the aggregate state. Commit makes both durable atomically; a crash after commit loses nothing.
- **AgentSession lifecycle events become durable.** AgentSession currently publishes lifecycle events through the in-memory bus post-commit with zero persistence. After this change those events are written as rows within the state transaction, giving `AgentSubscriptionDispatchHandler` the persisted-event prerequisite for reliable replay once the dispatcher lands.
- **BREAKING — `IEventPublisher.PublishAsync` converges to "append one event row."** It no longer synchronously dispatches to `ICloudEventHandler` implementations. The synchronous fan-out previously performed by `InMemoryEventBus` during publish is removed from the publish path. Notification becomes the dispatcher's responsibility (landing step 3, future issue). Until the dispatcher lands, cross-aggregate reactions driven by synchronous dispatch (e.g. `WorkflowRunCompleted → CompleteIssue`) are temporarily untriggered in-process — the events are durable, so the dispatcher will pick them up.
- **Exception handling converges to one form.** The three producers' divergent patterns — bare `catch {}` in `WorkflowRunStore`, log-and-swallow in `IssueGrain`, swallow-`InvalidOperationException` + log-and-swallow in `AgentSessionGrain` — are replaced: event write failures propagate and roll back the transaction, never silently swallowed.
- **Identity stamped at write time.** `projectid` and `issueid` are stamped into event extensions when the event row is written. `WorkflowRunStore` currently stamps only `projectid` (read from `run.Metadata.Annotations`) and omits `issueid`; after this change it stamps both, eliminating reverse DB lookups like `IssueWorkflowCompletionHandler` querying the DB to recover the owning issue from annotations.

## Capabilities

- `transactional-event-append`: Event rows for WorkflowRun, Issue, and AgentSession state changes SHALL be appended within the same database transaction as aggregate state persistence. Write failures SHALL propagate (rolling back the transaction) rather than being swallowed. Identity (`projectid`, `issueid`) SHALL be stamped into event extensions at append time.
- `event-publisher`: `IEventPublisher.PublishAsync` SHALL converge to appending one event row to the event store. It SHALL NOT synchronously dispatch to `ICloudEventHandler` implementations. The synchronous fan-out previously performed by `InMemoryEventBus` during publish SHALL be removed.

## Impact

- **Producer code paths**:
  - `Infrastructure/Data/Workflow/WorkflowRunStore.cs:43-79` — event append moves from post-commit loop (separate `DbContext` via `EventStore.AppendAsync`) into the state transaction; bare `catch {}` at line 74 removed; `issueid` added to identity stamping (`ToCloudEvent`, lines 81-97).
  - `Issue/Grains/IssueGrain.cs:638-687` — event append moves into the state transaction; `IssueStore.SaveAsync` gains a transaction envelope or accepts event rows; log-and-swallow at line 683 removed.
  - `Sessions/Grains/AgentSessionGrain.cs:669-708` — lifecycle events written as rows within `AgentSessionStore`'s existing transaction instead of bus-published post-commit; log-and-swallow at lines 684-689 removed.
  - `Infrastructure/Data/Sessions/AgentSessionStore.cs:44-59` — event row writes added to the existing transaction.
- **Event infrastructure**:
  - `Infrastructure/Events/IEventPublisher.cs` — semantics change (write-only, no dispatch).
  - `Infrastructure/Events/InMemoryEventBus.cs:25-29,71-93` — `PublishAsync` / `DispatchAsync` synchronous fan-out removed from the publish path.
  - `Infrastructure/Data/Events/EventStore.cs:20-27` — `AppendAsync` currently creates its own `DbContext`, which prevents sharing the producer's transaction. A transaction-scoped write entry point is needed (either a new `IEventStore` overload accepting a `DbContext`, or the stores write event rows directly).
  - `Infrastructure/Events/IEventStore.cs` — likely new transactional write member.
- **Handler dispatch gap**: All `ICloudEventHandler` implementations (`IssueWorkflowCompletionHandler`, `RunnerWorkflowTerminalStatusHandler`, `EpicAutoDoneHandler`, `AgentSubscriptionDispatchHandler`, `InboxProjectionHandler`, `EventBridge`, `WorkflowStageLockReleaseHandler`, `HermesIssueNotificationHandler`) lose their synchronous trigger path. They will be re-wired when the dispatcher lands (step 3). The Non-Goals explicitly defer both the dispatcher and handler logic changes.
- **Handler identity reads**: `Events/Subscriptions/IssueWorkflowCompletionHandler.cs:77-114` — reverse DB lookup for `issueId` can be replaced by reading `extensions["issueid"]` once `WorkflowRunStore` stamps it. (Handler reaction logic unchanged per Non-Goals; identity-source change only.)
- **Test fakes**: `RecordingIEventPublisher`, `NoopEventStore`, `RecordingEventStore`, and nested test implementations in `InboxProjectionTestSupport.cs` need updates for changed interfaces.
- **No web / CLI / runner changes** — pure server-side; no HTTP contract change, no new external dependencies.
