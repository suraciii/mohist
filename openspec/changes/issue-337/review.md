# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs; packages/runner/src/runtime/task-log.ts
  Evidence: Incremental uploads make the authoritative store retain head lines that the terminal collector has already discarded. The runner buffer is capped at 5000 lines and drops the head on overflow (`MAX_TASK_LOG_LINES` at `packages/runner/src/runtime/task-log.ts:36`, overflow drop at `packages/runner/src/runtime/task-log.ts:274`), while incremental `drain()` sends lines before the terminal snapshot (`packages/runner/src/runtime/task-log.ts:348`). The terminal `flush()` later returns only the currently retained tail (`packages/runner/src/runtime/task-log.ts:372`). On the server, `AppendAsync` now only queries existing seqs and inserts missing rows (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:56`, `packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:82`) and never deletes rows that are absent from the terminal reconciliation batch. `QueryAsync` then returns all stored rows in ascending seq order (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:137`). A long task that incrementally uploads seq 1..6000 and whose final runner buffer retains only seq 1001..6000 will still have seq 1..6000 in the authoritative store; the final Web query can show old head lines instead of the retained tail. This violates the issue's capacity constraint (drop head, keep tail), the terminal authoritative reconciliation contract, and the acceptance criterion that terminal Web display match the authoritative terminal query. [disallowed:product-behavior-change]
  SuggestedAction: Make terminal reconciliation authoritative for retained rows. For example, distinguish terminal batches from incremental batches and, on terminal, remove persisted rows for the work item that are outside the terminal retained seq set before committing the final batch/truncated flag. Add a server spec that appends early incremental rows, appends a truncated terminal tail, and asserts the store/query contain only the tail.
  Verification: `npm test` passed, `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~TaskLogStoreSpecs|FullyQualifiedName~TaskLogServicePersistThenPublishSpecs|FullyQualifiedName~SignalRTaskLogDeltaPublisherSpecs|FullyQualifiedName~ConnectionSubscriptionRegistryTaskLogScopeSpecs"` passed; neither suite covers this over-capacity incremental-plus-terminal scenario.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: The live Web cache is unbounded even though the panel uses a retained-log limit of 5000. `TASK_LOG_RETAINED_LIMIT` is 5000 (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:21`), but each delta is merged by concatenating all unseen entries and sorting (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:70`, `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:83`) without trimming to the retained tail or updating pagination. `setQueryData` applies this on every live delta (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:116`). A long-running task can grow the browser cache and DOM far past the retained limit during execution, and late low-seq deltas can reintroduce old head lines that the terminal query would not retain. This violates the capacity constraint for the real-time path and can make the live display diverge from the final authoritative tail until terminal refetch. [disallowed:product-behavior-change]
  SuggestedAction: Enforce the retained-tail limit in `mergeTaskLogDelta` after merging, preserving the highest seq entries and setting `truncated` when older entries are dropped. Add component/unit tests for live appending more than 5000 lines and for late low-seq deltas after the cache already contains a newer retained tail.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed. Existing `TaskLogPanel` tests cover dedup/sort and terminal invalidation but not retained-tail enforcement.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx; packages/web/src/shared/api/events-hub.ts; packages/server/src/Mohist.Server/Infrastructure/Events/SignalRTranscriptEventPublisher.cs
  Evidence: Each expanded `TaskLogPanel` creates its own SignalR connection (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:120`). `useEventsConnection` always invokes `SetSubscriptionsAsync` with the full `EVENT_TYPES` set (`packages/web/src/shared/api/events-hub.ts:62`), and `EVENT_TYPES` includes transcript event types (`packages/web/src/shared/lib/canonical-event-types.ts:47`, `packages/web/src/shared/lib/canonical-event-types.ts:82`). The transcript publisher sends every transcript envelope to every connection subscribed to that transcript type (`packages/server/src/Mohist.Server/Infrastructure/Events/SignalRTranscriptEventPublisher.cs:47`). As a result, opening a task log panel creates an extra no-op connection that is also subscribed to unrelated domain and transcript traffic. That means task-log viewing increases agent-session transcript fan-out and does not leave the existing agent-session real-time channel unaffected as required. [disallowed:architectural-judgment]
  SuggestedAction: Reuse the existing app-level events connection via context, or add a task-log-only connection/subscription mode that does not call `SetSubscriptionsAsync([...EVENT_TYPES])`. Add a test proving task-log subscription does not cause `SignalRTranscriptEventPublisher` to send to the task-log panel connection.
  Verification: `npm run test:run -w packages/web -- TaskLogPanel.test.tsx TaskProgressPanel.test.tsx events-hub.test.tsx` passed, but the tests mock the connection and do not assert transcript fan-out isolation for panel-created connections.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs
  Evidence: `TaskLogService` uses a static process-wide `workId -> taskId` cache keyed by owner id and work id (`packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs:51`). The code comment explicitly states a stale entry can leak a delta to a previous task's subscribers (`packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs:43`). That is a cross-task data-safety risk in the real-time rail: a stale scope stamp can send log lines to the wrong expanded task even though the authoritative store remains correct. [disallowed:data-safety]
  SuggestedAction: Avoid a static unbounded cache for publish scope, or key it with a run/task state version that cannot collide across restage/retry semantics. If caching is retained, clear it on terminal or when the workflow run/task mapping changes, and add a regression test for reused work ids or restaged task mappings.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~TaskLogServicePersistThenPublishSpecs"` passed, but the tests only prove cache reuse and retry after missing mapping; they do not cover stale remapping/leakage.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: packages/runner/src/runtime/host.ts
  Evidence: Incremental flush uploads can overlap because `startTaskLogFlushTrigger` calls `void flush()` on every interval/threshold fire (`packages/runner/src/runtime/host.ts:609`) without tracking an in-flight upload. The current watermark design makes this mostly correct for uniqueness, and the terminal batch is still authoritative, but slow uploads can create concurrent HTTP requests for the same work item.
  SuggestedAction: Consider serializing incremental uploads per collector or documenting that overlapping uploads are accepted. If serialized, keep terminal flush ordering explicit.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: verification
  Evidence: Full verification passed despite the blockers: `npm test` passed; `npm test -w packages/runner` passed; `npm run test:run -w packages/web` passed; `npm run typecheck -w packages/runner` passed; `npm run typecheck -w packages/web` passed; `git diff --check master...HEAD` passed. The failures above are uncovered product/edge-case gaps, not red test output.
  SuggestedAction: Add the missing over-capacity reconciliation, live-retention, and channel-isolation tests before re-review.
  Status: out-of-scope

<promise>FAIL</promise>
