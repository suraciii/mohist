# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:630
  Evidence: `SaveIssueAsync` snapshots pending issue events, clears them at line 639, and only then calls the transactional `_issueStore.SaveAsync(_issue.Id, _issue, pending)` at line 640. If the event-row append fails inside `IssueStore.SaveAsync`, the database transaction rolls back, but the active grain keeps the already-mutated `_issue` with its pending events permanently cleared. `CreateAsync` also assigns `_issue` before this save at lines 522 and 551, so a create failure can leave the activation rejecting retries as "already exists" even though no issue row committed. This breaks the acceptance intent that event-write failures leave no state/event split; a later command on the same activation can persist state through a no-event save without the original `IssueEvents` row. [disallowed:data-safety]
  SuggestedAction: Do not clear pending issue events until the event-aware save succeeds, and on any failed event-aware save either restore the pre-save aggregate state or mark/deactivate the grain so subsequent calls reload from storage before they can save again.
  Verification: Add a grain-level spec with an `IIssueStore`/`IEventStore` that fails during event append: call `CreateAsync` or `CompleteWorkAsync`, retry or perform another saving command on the same activation, and assert the issue cannot persist state without the original event row.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:103
  Evidence: `AttachPhysicalSessionAsync` mutates the loaded `_session` object before `CommitAsync` persists the state/event transaction. `CommitAsync` at lines 658-661 does not retain the emitted events or force a reload if `_stateStore.SaveAsync(SessionId, session, events)` throws. Because the mutation was applied in memory, retrying the same attach sees the runtime binding/model as already applied in `AgentSession.Transitions.cs:41-63`, emits no `RuntimeBound`/`ModelChanged` event, and can save the bound state through the no-events overload at lines 109-113. `CompactAsync`/`ResetAsync` have the same shape through `PersistRecoveryAsync` at lines 262-279. [disallowed:data-safety]
  SuggestedAction: Treat failed event-aware AgentSession saves like failed durable commits: keep transition events pending until commit succeeds, or deactivate/reload/restore the session before accepting another call.
  Verification: Fail the first `AgentSessionStore.SaveAsync(..., events)` append, call `AttachPhysicalSessionAsync`, retry the same command, and assert a bound session cannot be persisted without the matching `AgentSessionRuntimeBound` row.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:421
  Evidence: `SaveRunAsync(events)` marks `_runReloadRequired` and deactivates on event-aware save failure at lines 521-525, but several public entry points do not call `EnsureRun()` and therefore do not check the reload-required guard before operating on the dirty in-memory run. Examples include `AssignWorkerAsync` at line 270, `ClaimNextAsync` at line 293, and report paths in `WorkflowGrain.Reports.cs:7`, `:34`, and `:50`. After a rolled-back event append, one of these paths can process and save state from the same mutated activation before a reload occurs. [disallowed:data-safety]
  SuggestedAction: Centralize the reload-required guard so every public workflow mutation/read path that depends on `_run` fails after an event-aware save failure until the activation reloads.
  Verification: Force `WorkflowRunStore.SaveAsync(run, events)` to fail, then call `AssignWorkerAsync`, `ClaimNextAsync`, or a report method before deactivation completes; the call should fail with the reload-required error and must not persist rolled-back state.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:580
  Evidence: `FlushAsync` saves session state plus pending lifecycle events at line 587, then saves transcript rows at line 604. If the state/event transaction commits but transcript save fails, the catch at lines 607-612 logs and returns `false`; `_pendingDomainEvents` is only cleared inside the `stateSaved && transcriptSaved` block at lines 615-622. The next flush reuses the same pending domain events and appends duplicate durable `AgentSessionEvents` rows. Existing transcript-failure specs use only `session.input`/`message.delta` at `AgentSessionGrainPersistenceSpecs.cs:152-157`, which produce no domain events, so this duplicate-event path is untested. [disallowed:behavioral-tradeoff]
  SuggestedAction: Split transcript retry state from domain-event retry state, or make the state/event save and transcript save retry semantics idempotent so a transcript failure does not re-append already-committed lifecycle events.
  Verification: Make the transcript store fail once after appending a `usage.updated` runtime event, call `FlushForTestAsync` twice, and assert only one `AgentSessionUsageRecorded` row exists.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Events/DeadLetterStore.cs:73
  Evidence: This change adds `EventOrigin.AgentSession` and `EventStore.ListUndeliveredAsync` can now return AgentSession rows, but `DeadLetterStore.ParseOrigin` only recognizes `WorkflowRun`, `Issue`, and `Epic` at lines 73-79. `WriteAsync` persists `record.Origin.ToString()` at line 22, so an AgentSession dead letter is writable as `Origin = "AgentSession"` but `ListAsync` will throw `Unknown event origin 'AgentSession'`. [disallowed:data-safety]
  SuggestedAction: Add the `AgentSession` parse case and a `DeadLetterStoreTests` round-trip for `EventOrigin.AgentSession`.
  Verification: Write and list a `DeadLetterRecord` with `Origin = EventOrigin.AgentSession`; it should round-trip without throwing.
  Status: open

- [ID: item-6]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/EventBusSpecs.cs:13
  Evidence: The event-publisher spec requires both `PublishAsync(CloudEvent)` and the typed overload to append exactly one row and to never let matching handler exceptions affect publish. Current changed tests cover typed publish no-dispatch at lines 26-48 and a typed filtered case at lines 53-73, but `PublishAsync_NoSubscriber_DoesNotThrow` uses `NoopEventStore` at lines 13-21 and does not assert a row append. I did not find coverage for the raw `CloudEvent` overload preserving source/type/subject/data/extensions, or for a matching handler that would throw if dispatch were still invoked.
  SuggestedAction: Add explicit `PublishAsync(CloudEvent)` append coverage and a matching throwing-handler test that proves publish only appends and does not dispatch.
  Verification: Run the added event-publisher specs plus `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~EventBusSpecs"`.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.SpecTests/Specs/Events/AgentSessionEventsMigrationSpecs.cs:21
  Evidence: A second migration now creates `IX_AgentSessionEvents_Type_Time` at `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260708113254_AddAgentSessionEventsTypeTimeIndex.cs:13`, and the model configures the same index in `MohistDbContext.cs:548`. The migration specs only assert table creation, `DispatchedAt`, and the undelivered index at lines 21-31; line 52 still has a negative source-text assertion that the first migration lacks the type/time index. There is no test proving a fully migrated database has the final AgentSession type/time index.
  SuggestedAction: Add a full-migration assertion for `IX_AgentSessionEvents_Type_Time`, and rename/update the stale negative test so it is clearly scoped to the first migration only.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~AgentSessionEventsMigrationSpecs"`.
  Status: open

- [ID: item-8]
  Severity: cleanup
  Scope: event publisher and handler comments
  Evidence: Several changed comments still describe the pre-issue-361 bus semantics. `IEventPublisher.cs:4` says it "Publishes events to the in-process event bus" even though the interface is now write-only append. `InboxProjectionHandler.cs:30-31` says issue event identity is stamped by `IssueGrain`, but stamping moved to `IssueStore`; lines 100-104 still say "The bus already swallows handler exceptions by design" even though the bus no longer invokes handlers. `WorkflowStageLockReleaseHandler.cs:71-73` repeats the same stale bus-swallowing rationale. These comments are now misleading around the exact semantic boundary this issue changes.
  SuggestedAction: Update the comments to say `IEventPublisher` appends event rows and that handler exception swallowing is local to the handler/future dispatcher path, not the publish path.
  Verification: Re-read the comments and run `dotnet build Mohist.sln -p:SkipWebBuild=true`.
  Status: open

## Follow-up Items

- [ID: item-9]
  Severity: follow-up
  Scope: openspec/changes/issue-361/tasks.json
  Evidence: All task entries still have `"passes": false` even though the branch contains implementation commits for T-001 through T-005 and the server tests pass. This does not block the product deliverable, but it weakens workflow traceability for reviewers or integrators reading the issue artifacts.
  SuggestedAction: Update the task status metadata if Mohist uses `passes` as completion evidence, or document that the field is not maintained after build.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: openspec/changes/issue-361/progress.txt
  Evidence: The progress notes still state that `AgentSessionEvents` has no `IX_AgentSessionEvents_Type_Time` index at lines 70-75 and list a migration spec proving that negative assertion at lines 120-122. The candidate later added `20260708113254_AddAgentSessionEventsTypeTimeIndex`, so this evidence is stale.
  SuggestedAction: Amend the progress notes or add a short addendum explaining that the later review-fix migration added the type/time index.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: warning
  Scope: npm test / packages/web and packages/runner
  Evidence: `npm test` ran the .NET solution successfully (`Mohist.Server.SpecTests`: 4152 passed, 9 skipped; `Mohist.Server.ArchTests`: 24 passed, 3 skipped; `Mohist.Cli.Tests`: 865 passed), then failed in unchanged workspaces. `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx` had 1 failing test looking for timestamp text `08:00:00.000`, and `packages/runner/tests/runner-host-task-log.spec.ts` had 3 failing tests (`RoutesConcurrentIncrementalUploadsToEachWorkItemCollector`, `FallbackExecutorPathStreamsIncrementalLogsBeforeReport`, `FlushesCapturedLogViaUploadTaskLogBeforeReport`). No files under `packages/web` or `packages/runner` are part of `git diff origin/master...HEAD`, so these failures are out of scope for issue-361.
  SuggestedAction: Track separately or rebase onto a base where web/runner tests are green; they should not be used as evidence that the server-side candidate itself failed to compile/test.
  Status: out-of-scope

- [ID: item-12]
  Severity: info
  Scope: cross-aggregate reactions
  Evidence: `InMemoryEventBus.PublishAsync` no longer invokes handlers, so `IssueWorkflowCompletionHandler`, `InboxProjectionHandler`, `EpicAutoDoneHandler`, `WorkflowStageLockReleaseHandler`, `AgentSubscriptionDispatchHandler`, and similar subscribers are dormant until the dispatcher lands. This is explicitly called out in `openspec/changes/issue-361/design.md:103-110` and is intentional for this issue, but it creates a temporary functional gap for auto-completion, inbox projection, epic reconciliation, runner terminal status push, and agent subscriptions.
  SuggestedAction: Land the dispatcher issue immediately after this change, or document the suspended-auto-progression window operationally.
  Status: out-of-scope

<promise>FAIL</promise>
