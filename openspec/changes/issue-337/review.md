# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs
  Evidence: The issue requires incremental appends to be non-destructive and terminal batches to dedup by seq. The current store still loads all existing entries for the same owner/work at lines 56-58, removes them at lines 59-62, and inserts every incoming row at lines 83-96. The old regression test still locks in replacement behavior in packages/server/tests/Mohist.Server.Tests/Specs/Runner/Data/TaskLogStoreSpecs.cs lines 161-187. This means a second incremental upload erases earlier persisted rows until a terminal upload succeeds; if the terminal upload fails, the authoritative store is incomplete. [disallowed:product-behavior/data-safety]
  SuggestedAction: Change TaskLogStore.AppendAsync to query existing seqs in the transaction, insert only missing seqs, and update batch metadata without deleting existing entries. Replace the old replacement spec with union/dedup specs for incremental append and terminal reconciliation.
  Verification: Add tests for append seq 1-2 then seq 3-4 yielding 1-4, and append seq 1-10 then terminal seq 1-15 yielding exactly one row per seq with no unique-index failure. Rerun `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter "FullyQualifiedName~TaskLogStoreSpecs|FullyQualifiedName~TaskLogServicePersistThenPublishSpecs"`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx
  Evidence: The acceptance criterion says users can expand a running task and see logs refresh during execution. The actual task list only sets `canExpand` for failed or completed tasks at line 47, and the TaskLogPanel is mounted only when `expanded && canExpand` at line 95. A running task therefore cannot mount TaskLogPanel and cannot call SubscribeTaskLogAsync, so the live-view product path is unreachable. [disallowed:product-behavior]
  SuggestedAction: Allow expansion for running tasks with a taskId, and ensure the panel receives the running status so it subscribes while the task is active.
  Verification: Add a TaskProgressPanel test rendering a running task, click/expand it, and assert TaskLogPanel is mounted and SubscribeTaskLogAsync is invoked. Rerun `npm run test:run -w packages/web`.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/runner/src/runtime/host.ts
  Evidence: RunnerHost can run multiple work items concurrently through runWorkerPool at lines 264-300, but incremental flushing uses a single host-wide `collectorRef` field at line 60. Each task's flush trigger captures its own owner/work upload endpoint but drains `this.collectorRef` at line 472. When two work items overlap, work A's timer can drain work B's collector and upload those lines to work A's endpoint, advancing B's watermark and causing B to miss those lines in its own incremental stream. [disallowed:product-behavior/data-safety]
  SuggestedAction: Keep the collector in the executeAndReport closure passed to that work item's trigger, or track collectors by owner/work key so timers cannot cross-drain unrelated work.
  Verification: Add a RunnerHost spec with two concurrent long-running work items, distinct log lines, fake timers, and assertions that every incremental upload's owner/work id matches the log source for that work. Rerun `npm test -w packages/runner`.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Events/SignalRTaskLogDeltaPublisher.cs
  Evidence: The new task-log publisher bypasses the existing project isolation gate and checks only type + `(workflowRunId, taskId)` via ShouldNotifyTaskLog at lines 61-68. MohistHub stores per-connection project affinity from the query string at packages/server/src/Mohist.Server/Events/Hub/MohistHub.cs lines 167-179, and UserNotificationDispatcher applies that gate for CloudEvents at packages/server/src/Mohist.Server/Infrastructure/Events/UserNotificationDispatcher.cs lines 371-380, but TaskLogDeltaEnvelope has no project id at lines 34-40 and the task-log publisher cannot reject a connection from another project if it subscribes to a known workflowRunId/taskId pair. [disallowed:security/public-contract]
  SuggestedAction: Stamp projectId on the task-log envelope or validate SubscribeTaskLogAsync against the connection's project affinity, then gate publisher delivery by project before sending OnTaskLogDelta.
  Verification: Add a publisher/hub test with project-A and project-B affinitized connections both subscribed to task-log.delta, publish a project-A delta, and assert only project-A receives it.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: onTaskLogDelta filters only by `taskId` at lines 111-113, then writes into the cache key for the current `workflowRunId` at line 115. If a rerun reuses a task id, a delta for ownerId/workflowRunId `wr-1` can pollute the visible cache for `wr-2`. [disallowed:product-behavior]
  SuggestedAction: Also require envelope.ownerKind/workflow owner and envelope.ownerId to match the current workflowRunId before merging into the query cache.
  Verification: Add a TaskLogPanel test rendered with workflowRunId `wr-2`, emit a delta with the same taskId but ownerId `wr-1`, and assert no line is appended.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: packages/web/src/shared/api/events-hub.ts and packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: MohistHub documents that task-log scope is not durable and the Web must re-invoke SubscribeTaskLogAsync on reconnect at packages/server/src/Mohist.Server/Events/Hub/MohistHub.cs lines 266-272. events-hub.ts onreconnected only reapplies the generic event subscription at lines 127-131. TaskLogPanel suppresses another SubscribeTaskLogAsync while `subscribedRef.current` remains true at lines 145-149, so a reconnect with a cleared server-side task scope can silently stop live log delivery. [disallowed:product-behavior]
  SuggestedAction: Expose reconnect generation/status from useEventsConnection or provide a task-log resubscribe hook so active TaskLogPanel subscriptions are reasserted after reconnect.
  Verification: Simulate reconnect while a TaskLogPanel is subscribed and assert SubscribeTaskLogAsync is invoked again for the active workflowRunId/taskId.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: TaskLogPanel subscribes for any non-terminal status because line 130 only excludes terminal states before subscribing at lines 145-149. Pending or missing taskStatus therefore creates a live subscription even though the requirement is on-demand for expanded running tasks. This can produce unnecessary scope entries and invalid fan-out interest. [disallowed:product-behavior]
  SuggestedAction: Subscribe only when `taskStatus === 'running'`; still allow terminal/non-running panels to read the authoritative query without live subscription.
  Verification: Add tests for `taskStatus="pending"` and undefined taskStatus asserting SubscribeTaskLogAsync is not called, and a running status asserting it is called.
  Status: open

- [ID: item-8]
  Severity: warning
  Scope: packages/runner/src/runtime/host.ts
  Evidence: The fallback executor path used when the shared ACP executor is unavailable calls `executeWithLog(work, signal, null)` at line 443 and only performs terminal `flushTaskLog` at line 445. The normal path pre-creates a collector and starts the incremental trigger at lines 471-485. Long-running fallback executions therefore do not stream during execution, contrary to the runner acceptance criterion. [disallowed:product-behavior]
  SuggestedAction: Create and wire a TaskLogCollector plus startTaskLogFlushTrigger in the fallback path as well, or document and test that fallback is intentionally outside the feature scope.
  Verification: Simulate shared ACP initialization failure, run a long fallback task that emits logs before resolving, advance fake timers, and assert an incremental upload occurs before report.
  Status: open

- [ID: item-9]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs
  Evidence: ResolveTaskIdAsync caches failed lookups as `string.Empty` at lines 231 and 248, and later returns any cached value directly at lines 220-222. Subsequent publishes then stamp an empty taskId, and ShouldNotifyTaskLog rejects empty task ids at packages/server/src/Mohist.Server/Infrastructure/Events/UserNotificationDispatcher.cs lines 257-262. If the workflow-run task mapping appears after the first append in the same process, that work item can permanently stop fanning out live deltas. [disallowed:product-behavior]
  SuggestedAction: Do not cache negative workId-to-taskId lookups, or expire/retry them when publishing later batches for the same work item.
  Verification: Add a TaskLogService test where the first append happens before the workflow-run mapping exists, then the mapping is persisted and the next append publishes with the real taskId.
  Status: open

- [ID: item-10]
  Severity: minor
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: mergeTaskLogDelta drops any unseen entry with `seq <= maxCachedSeq` at lines 71-77. This is intentional per the design for out-of-order late deltas, but it means a live seq 2 arriving after seq 3 is hidden until terminal reconciliation even though it is not a duplicate. The test at packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx lines 449-462 asserts this behavior, but the issue asks for continuous refresh during execution and does not require dropping all out-of-order unseen entries. [disallowed:ambiguous-tradeoff]
  SuggestedAction: Decide whether live merge should accept unseen lower seqs and sort, or keep the current drop-until-terminal behavior and document the user-visible delay explicitly.
  Verification: Add a test for cached seq [1,3] plus delta seq [2] matching the chosen behavior.
  Status: open

- [ID: item-11]
  Severity: test-gap
  Scope: packages/runner/tests/runner-host-task-log.spec.ts
  Evidence: The Phase 2 runner tests at lines 287-498 mostly instantiate TaskLogCollector and startTaskLogFlushTrigger directly. They validate primitives but do not cover the actual RunnerHost incremental wiring for upload-before-completion, concurrent in-flight routing, owner/work scoping, or fallback path behavior.
  SuggestedAction: Add host-level specs using deferred actions and fake timers to assert incremental upload before action completion/report, correct owner/work routing under concurrency, and fallback path streaming.
  Verification: Rerun `npm test -w packages/runner` and confirm the new tests fail before the implementation fix and pass after.
  Status: open

- [ID: item-12]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx and packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx
  Evidence: TaskProgressPanel tests only expand failed/completed tasks; there is no running-task expansion test. TaskLogPanel tests cover taskId matching and append/reconcile behavior, but not workflowRunId/ownerId mismatch, reconnect resubscribe, or non-running subscription suppression.
  SuggestedAction: Add web tests for running task expansion, ownerId mismatch ignored, reconnect resubscribe, and pending/undefined status no-subscribe.
  Verification: Rerun `npm run test:run -w packages/web`.
  Status: open

- [ID: item-13]
  Severity: cleanup
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx
  Evidence: The test name at line 471 says it "returns the original page reference", but mergeTaskLogDelta copies the lines array at packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx line 70 and returns a new object at lines 87-91. The assertions at lines 477-478 check equality, not reference identity. [disallowed:cleanup-not-worth-changing-with-blockers]
  SuggestedAction: Rename the test or change the implementation/assertion if identity is actually intended.
  Verification: Rerun `npm run test:run -w packages/web -- TaskLogPanel.test.tsx`.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-14]
  Severity: info
  Scope: dependency audit output
  Evidence: During the targeted server test run, npm audit output reported 9 vulnerabilities (3 moderate, 3 high, 3 critical). package.json/package-lock changes are not part of this candidate range, so this is not attributed to issue 337.
  SuggestedAction: Triage dependency audit separately from this feature review.
  Status: out-of-scope

## Verification

- `mo issue show 337 --project-id proj_f6c141d63b6243bfbb481737b2243b87` read before review.
- Read openspec proposal, design, tasks, self-review, and delta specs under openspec/changes/issue-337/.
- Inspected candidate range `master...HEAD` and changed runner/server/web files plus adjacent TaskLogStore persistence path.
- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 63 files, 893 tests.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 260 files, 4093 passed, 1 skipped.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter "FullyQualifiedName~TaskLogStoreSpecs|FullyQualifiedName~TaskLogServicePersistThenPublishSpecs|FullyQualifiedName~SignalRTaskLogDeltaPublisherSpecs|FullyQualifiedName~ConnectionSubscriptionRegistryTaskLogScopeSpecs"` passed: 41 tests.

<promise>FAIL</promise>
