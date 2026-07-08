# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:590-595
  Evidence: `FlushAsync` catches `Exception` around `_stateStore.SaveAsync(SessionId, _session, pendingEvents, ct)` and only logs it, never rethrowing. Because `_stateStore.SaveAsync` now writes AgentSession lifecycle event rows inside the same transaction as the session state, this catch swallows event-write failures and breaks the acceptance criterion that the three producers have uniform exception handling (“写进行务、不再吞”). The `WorkflowRunStore` and `IssueStore` paths propagate failures; the `AgentSessionGrain` timer-driven flush does not. The `CommitAsync` private method (used for attach/recovery) does propagate, but the main runtime-event path (`AppendRuntimeEventsAsync` → `FlushAsync`) swallows.
  SuggestedAction: Remove the broad catch in `FlushAsync` so the exception propagates to the timer callback / caller, or rethrow after logging. If timer resilience is required, catch only the specific non-fatal exceptions and rethrow the rest; event-write failures must not be swallowed.
  Verification: Add a unit test that injects a failing `IAgentSessionStore` into `AgentSessionGrain` and calls `FlushForTestAsync`; it should throw, but currently returns `false` and swallows the exception. Existing `AgentSessionTransactionalEventAppendSpecs` only cover the store, not the grain.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Events/Subscriptions/IssueWorkflowCompletionHandler.cs:31-38
  Evidence: The XML comment states the handler is “called from inside a workflow-grain publish path” and “Dispatch is synchronous (no background detach)”. After T-002, `InMemoryEventBus.PublishAsync` is write-only and never invokes handlers, so this comment is now misleading. The handler remains registered for the future dispatcher, but it is not synchronously dispatched today.
  SuggestedAction: Update the XML comment to reflect that the handler is dormant until the dispatcher (step 3) lands and is currently invoked only by tests or future replay infrastructure.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:47-50
  Evidence: The comment says “The bus already tolerates handler failures (see InMemoryEventBus)”. The bus no longer calls handlers, so this rationale is stale. The handler is registered but never triggered by publish, meaning inbox projections are not created during the window until the dispatcher lands.
  SuggestedAction: Update the comment to note that the handler is currently dormant and its inbox-hint publish path is also inactive until the dispatcher lands.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionSpecs.cs:473
  Evidence: `RunnerAppendsSessionEvents_StoresAggregateDomainEvents` is skipped with reason “AgentSessionEvent persistence is a no-op stub; event read-back not yet available.” This issue added `AgentSessionEvents` and `IEventStore.ListAgentSessionEventsAsync`, so the skip reason is stale. The test body is empty (`await Task.CompletedTask;`), so it provides no coverage of the grain-level persistence path.
  SuggestedAction: Implement the skipped test or remove it. If implemented, verify that runtime events append through the grain result in durable `AgentSessionEvents` rows.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:511-551
  Evidence: `AgentSessionEventRow` is configured with `IX_AgentSessionEvents_Type_Source_Id` and `IX_AgentSessionEvents_Undelivered`, but unlike `WorkflowRunEventRow`, `IssueEventRow`, and `EpicEventRow`, it lacks a `Type_Time` index. This is a minor schema inconsistency.
  SuggestedAction: Add `IX_AgentSessionEvents_Type_Time` if AgentSession events will be queried by type and time, or document the intentional omission.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:47-122
  Evidence: When `envelope.Extensions` is `null`, `SerializeExtensions` produces the JSON literal `"null"`. Functionally the read path tolerates this (it returns an empty dictionary), but the persisted value is inconsistent with the dictionary case (`"{}"`) and with the non-null expectation of the JSON column.
  SuggestedAction: Make `SerializeExtensions` treat `null` as `"{}"` so the stored JSON is uniform.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: info
  Scope: packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionSpecs.cs:1181
  Evidence: `RunnerReport_WhenAgentWorkFailsBeforeTelemetry_ClosesCreatedSession` is skipped with reason “Requires design decision: report-failed should close session, but current RunnerGrain.ReportAsync does not propagate to session.” This is unrelated to issue-361 and is pre-existing.
  SuggestedAction: Track under a separate issue about RunnerGrain→AgentSession propagation.
  Status: pre-existing

- [ID: item-8]
  Severity: warning
  Scope: Cross-aggregate reactions (IssueWorkflowCompletionHandler, EpicAutoDoneHandler, InboxProjectionHandler, etc.)
  Evidence: Synchronous handler dispatch is removed from `InMemoryEventBus` per the spec, so all `ICloudEventHandler` implementations are dormant until the dispatcher (step 3) lands. This is the dominant risk acknowledged in `design.md` and is intentional for this issue, but it creates a functional gap for auto-completion, inbox projections, and epic auto-done during the window.
  SuggestedAction: Land the dispatcher (step 3) immediately after this change, as documented in the Migration Plan, and explicitly document the suspended-auto-progression window if a gap is unavoidable.
  Status: out-of-scope

<promise>FAIL</promise>
