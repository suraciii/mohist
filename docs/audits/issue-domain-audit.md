# Issue Domain Audit

> Scope: `packages/server/src/Mohist.Server/Issue/`, `Api/IssueRoutes*.cs`, `Api/ProjectResolutionEndpointFilter.cs`, and the cross-grain bus-subscription paths into the workflow lifecycle events.
>
> Audit method: read every file in scope in full, traced emit and subscription sites through `WorkflowRunStore.Publish`, `EventStore.AppendWorkflowEventAsync`, `WorkflowGrain.EmitStageChanged`, `WorkflowGrain.CommitAsync` via `codegraph_context` / `codegraph_search` / `codegraph_callers` / `codegraph_explore`. Cross-checked with the integration spec `IssueWorkflowProductLoopSpecs.IssueStart_RunnerCompletesWorkflow_IssueBecomesDone` and the unit specs `IssueWorkflowAbortSpecs`, `IssueDomainSpecs`, `IssueGrainBusSubscriptionSpecs`.
>
> Out of scope (audited by other agents): WorkflowGrain state machine (workflow-domain-audit), EventBus plumbing (event-bus-audit), SignalR EventBridge (event-bus-audit + event-pipeline-audit), architecture-level grain/Orleans rules.

## Summary table

| # | Severity | Title | File |
|---|----------|-------|------|
| 1 | **P0** | `WorktreeCleanupService` is dead code: terminal workflow events never carry `projectid` / `issueno` extensions | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54-84` + `Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-115` |
| 2 | **P0** | `ExceptionMiddleware` maps every `InvalidOperationException` to 404, masking domain conflicts as "not found" | `Api/ExceptionMiddleware.cs:20-24` + `Api/IssueRoutes.Lifecycle.cs:84-91,149-152,108-113` |
| 3 | **P0** | `/close` (and `/reopen` on archived, etc.) on a Done/archived issue returns 404 instead of 409 because the local `catch(InvalidOperationException)` is too broad | `Api/IssueRoutes.Lifecycle.cs:70-91, 116-153` |
| 4 | **P0** | Fire-and-forget `_ = CompleteWorkAsync(wrId)` exceptions are unobserved; a grain concurrency race that lands the issue in an "almost terminal" state is silent | `Issue/Grains/IssueGrain.cs:89-114, 242-254` |
| 5 | **P1** | Bus handler reads `IssueGrain._issue` from the bus dispatcher's thread (not the grain's call queue) — thread-safety violation, plus wasted round-trip on every miss | `Issue/Grains/IssueGrain.cs:89-114` |
| 6 | **P1** | `OnDeactivateAsync` uses an empty `catch { }` for subscription disposal; can mask real OOM/ThreadAbort-shaped failures during teardown | `Issue/Grains/IssueGrain.cs:79-87` |
| 7 | **P1** | `IssueWorkflowReconciliationService` has no `OrderBy` on the candidate query, processes 500 serially with no parallelism, and cannot handle a deleted workflow row (silent stuck state) | `Issue/Services/IssueWorkflowReconciliationService.cs:69-104` + `Issue/Grains/IssueGrain.cs:324-342` |
| 8 | **P1** | `ReconcileWithWorkflowTerminalStateAsync` returns early when `wfStatus` is null (workflow row deleted / unreadable); the issue stays InProgress forever — the daily sweep cannot rescue it | `Issue/Grains/IssueGrain.cs:324-342` |
| 9 | **P1** | `WorktreeCleanupService.OnWorkflowCompleted` is `async void`; post-`await` exceptions escape the bus dispatch's `try/catch` and can crash the process on a stale SynchronizationContext | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54-84` |
| 10 | **P1** | Worst-case latency between workflow terminal and Issue status: up to 24h (daily sweep) if the bus subscription missed because the IssueGrain was deactivated at emit time | `Issue/Services/IssueWorkflowReconciliationService.cs:24, 40-67` + `Issue/Grains/IssueGrain.cs:63-77, 79-87` |
| 11 | **P1** | `CancelAsync` calls `wfGrain.StopAsync("issue-closed")` and then mutates issue state. If `StopAsync` succeeds but the bus event never reaches IssueGrain (deactivation), the issue is Cancelled but the workflow row is still `Stopped` (incomplete terminal) | `Issue/Grains/IssueGrain.cs:226-240` |
| 12 | **P2** | `Issue.Complete` throws `InvalidOperationException` for "not InProgress" cases (not the `Done` short-circuit); the throw path is reachable from the bus handler's race window | `Issue/Domain/Issue.Transitions.cs:52-62` + `Issue/Grains/IssueGrain.cs:89-96` |
| 13 | **P2** | `WorktreeCleanupService` only listens to `WorkflowRunCompleted`; stopped/failed workflows leave the worktree behind forever (no follow-up `WorkflowRunStopped`/`Failed` subscription) | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:41` |
| 14 | **P2** | `_run.Status.ToString()` projection assumes PascalCase enum names but the Issue/Workflow domain uses `WorkflowRunStatus.Completed` etc. — the string "Completed" must be kept in sync with the mapper | `Issue/Grains/IssueGrain.cs:330-341` + `Workflow/Domain/Run/WorkflowRun.cs:6` + `Workflow/Services/WorkflowStatusMapper.cs:55-58` |
| 15 | **P2** | `IssueStatus` has no `Pending` / `Blocked` state; an issue that is paused (`WorkflowStatus = "Paused"`) shows as `in_progress` + `health: "paused"`, conflating "actively working" and "paused waiting for user" | `Issue/Domain/IssueStatus.cs:3-9` + `Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs:80-93` |
| 16 | **P2** | `CreateIssue` is the only API that does NOT extract project via the endpoint filter — wait, it does (`var project = GetRequiredProject(ctx);` line 39). However, the body still allows the caller to override `projectId` via... actually it doesn't (no `projectId` field on `CreateIssueRequest`). **NIT — verified safe, dropping from list.** | `Api/IssueRoutes.Crud.cs:28-52` + `Api/IssueRoutes.Dtos.cs:6-15` |
| 17 | **P2** | `WorktreeCleanupService.Dispose` does not log on subscription disposal exception | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:48-52` |
| 18 | **P2** | The `Issue` domain transitions that *return* `bool` (`Complete`, `AbortWorkflow`) silently swallow the "wrong state" case; the bus handler relies on this for idempotency but the API surface does not log the false return | `Issue/Domain/Issue.Transitions.cs:52-62, 103-112` |
| 19 | **P2** | The "stuck in InProgress" reconciliation is not exposed as a metric, log line, or health-check signal — operators have no visibility into how many issues the sweep is healing | `Issue/Services/IssueWorkflowReconciliationService.cs:84-103` |
| 20 | **P2** | `IssueWorkflowReconciliationService.ExecuteAsync` always waits the full `ReconciliationPeriod` before the first run; in tests, the period must be patched via the `public static` field (ugly) | `Issue/Services/IssueWorkflowReconciliationService.cs:24, 40-67` |
| 21 | **P3** | `OnDeactivateAsync` returns `Task.CompletedTask` even though the disposal loop is sync; the signature is misleading | `Issue/Grains/IssueGrain.cs:79-87` |
| 22 | **P3** | `IssueStore.SaveAsync` has no ETag / optimistic-concurrency token; a manual DB write racing a grain save can silently overwrite | `Infrastructure/Data/Issue/IssueStore.cs:33-47` |
| 23 | **P3** | `IssueIdentityResolver.GetIdAsync` issues a fresh `db.Issues` query on every call; lifecycle routes do this on every request | `Issue/Services/IssueIdentityResolver.cs:16-20` + `Api/IssueRoutes.Lifecycle.cs:25,57,80,103,134,165` |
| 24 | **P3** | `IssueGrain` constructor has 11 dependencies; adding another bus subscription will push this to 12. Worth a `IWorkflowTerminalSubscriber` facade | `Issue/Grains/IssueGrain.cs:37-59` |
| 25 | **P3** | `IssueRowMapper.ById` and `ByNumber` deserialize every row's full JSON `State` blob, then filter in memory by `ProjectId`; for a single-project sweep this is fine, for `ListAsync` over the whole table it wastes CPU | `Issue/Services/IssueRowMapper.cs:17-25` |
| 26 | **P3** | `Issue.Complete` accepts an optional `now` parameter but the bus path never threads it; `UpdatedAt` will reflect the moment the bus handler ran, not the actual workflow terminal time | `Issue/Domain/Issue.Transitions.cs:52-62` |
| 27 | **P3** | `CreateAsync` accepts a caller-supplied `issueId` (`issue_xxx`); if the caller passes an already-existing id, the second call throws `InvalidOperationException("already exists")` — this is correct but the message leaks the id convention | `Issue/Grains/IssueGrain.cs:344-364` |
| 28 | **P3** | `Aborting` a non-cancelled issue from the bus returns `false` and silently does nothing — the `if (_issue is null) return;` guard in the handler masks the failure with no log line | `Issue/Grains/IssueGrain.cs:89-114, 249-254` |

---

## P0 findings (must fix)

### P0-1. `WorktreeCleanupService` is dead code: terminal workflow events never carry `projectid` / `issueno`

- **Where**:
  - `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54-84`
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-115` (`Publish` is the terminal-event emit site for `WorkflowRunCompleted`/`Failed`/`Stopped` and friends)
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:19-34` (second emit site, same shape)
  - `packages/server/src/Mohist.Server/Infrastructure/Events/CloudEventFactory.cs:14-62` (the `IProjectScoped` lift)
- **What**:
  - `WorktreeCleanupService.OnWorkflowCompleted` requires both the `projectid` and `issueno` extensions (`if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(issueNumberStr)) return;` at line 58). The early return is the only way out if either is missing.
  - The only emit sites for `com.mohist.workflow.run.completed` are `WorkflowRunStore.Publish` (after a `WorkflowRunStore.SaveAsync(run, events)` commit) and `EventStore.AppendWorkflowEventAsync`. **Both call `CloudEventFactory.Create(..., workflowRunId: runId)` with no `projectId` or `issueNumber` arguments** (`WorkflowRunStore.cs:108-112` and `EventStore.cs:27-31`).
  - `CloudEventFactory.Create` *would* auto-lift `ProjectId` from the data payload if it implemented `IProjectScoped` (line 29-32 of `CloudEventFactory.cs`), but the data passed in is `JsonSerializer.SerializeToElement(WorkflowRunCompleted, ...)` (an empty record) and `WorkflowEventSerializer.ToData(e.Payload)` (same). `JsonElement` is a struct, not a reference, so `data is IProjectScoped` is permanently `false`.
  - Net effect: every `com.mohist.workflow.run.completed` reaches `WorktreeCleanupService` with empty `projectid`/`issueno`, the handler silently returns, **and the worktree is never cleaned up**. The class docstring at line 11-18 advertises this as the post-Step-8 replacement for the old `IssueWorkflowCompletionHook` cleanup; in production the cleanup is gone.
  - The handler is also reachable only via the terminal event, but the same data shape affects `EventBridge` (audit-noted as workflow-P0-4: events fall through to `project:global` group).
  - Note: the `IssueGrain` bus subscription is NOT affected because it only reads `workflowrunid`, not `projectid`.
- **Fix**:
  1. Change `WorkflowRunStore.Publish` to look up the projectId and issueNumber from the workflow run's persisted metadata (the `WorkflowRunRow.Metadata.Annotations` already carries `projectId` and `issueId` per `BuildRunMetadata` at `WorkflowGrain.cs:1072-1095` and the computed column at `MohistDbContext.cs:197-199`). Either load the row before emit (cheap — already in scope), or thread the run through `SaveAsync` to `Publish` and pass it.
  2. Pass `projectId` and `issueNumber` to `CloudEventFactory.Create` in both `WorkflowRunStore.Publish` and `EventStore.AppendWorkflowEventAsync`.
  3. Add a unit test `WorktreeCleanupService_ReceivesCompletedEvent_WithoutExtensions_LogsAndReturns` AND `WorktreeCleanupService_ReceivesCompletedEvent_WithExtensions_RemovesWorktree` (the latter should drive a fake `IGitService`).
- **Test gap**: zero. `WorktreeCleanupService` has no spec file under `tests/Mohist.Server.Tests/Specs/`. The integration test `IssueStart_RunnerCompletesWorkflow_IssueBecomesDone` at `IssueWorkflowProductLoopSpecs.cs:44-97` does not assert worktree cleanup.

### P0-2. `ExceptionMiddleware` maps every `InvalidOperationException` to 404

- **Where**: `packages/server/src/Mohist.Server/Api/ExceptionMiddleware.cs:20-24`
- **What**:
  ```csharp
  catch (InvalidOperationException ex)
  {
      var response = new ApiResponse<object>(false, Error: ex.Message, Code: "not_found");
      await Results.Json(response, statusCode: 404).ExecuteAsync(context);
  }
  ```
  - The catch is unscoped — every `InvalidOperationException` raised anywhere in the request pipeline (Issue domain, Project domain, WorkflowGrain via cross-grain, etc.) gets a 404. Many of those exceptions mean "operation not allowed in this state" (e.g. `Issue.StartWorkflow` at `Issue/Domain/Issue.Transitions.cs:43-44` throws `InvalidOperationException("Issue #N is Done")`), not "row missing".
  - Most lifecycle routes (`/start`, `/reopen`, `/archive`) have a local `catch (InvalidOperationException) { return ApiResults.Conflict(ex.Message); }` so the local catch fires first and the bug is masked for those — but the local catches themselves map to 404 in other routes (see P0-3).
  - For routes that *don't* have a local catch (the workflow-control routes: `/approve`, `/reject`, `/retry`, `/rerun`, `/resume`, `/force-stop`, `/stop` — see `IssueRoutes.WorkflowControl.cs`), if the workflow grain throws `InvalidOperationException` (e.g. `WorkflowRunExtensions.Approve` only throws `WorkflowDomainException`, not `InvalidOperationException`, so this is currently safe — but the *general* design is wrong: any future `InvalidOperationException` raised in `WorkflowGrain` will silently return 404).
  - The error message in the response body is the raw `ex.Message` which is fine, but the `code: "not_found"` field is a lie whenever the actual cause is a state conflict.
- **Fix**:
  1. Introduce a `DomainConflictException` base class (or reuse `WorkflowDomainException` semantics) for "operation not allowed in current state", and a `DomainNotFoundException` for "row missing".
  2. Change the middleware to map `DomainNotFoundException` → 404, `DomainConflictException` → 409, and let all other `InvalidOperationException` bubble to 500 (or, better, remove the global `InvalidOperationException` catch entirely).
  3. Audit every throw site in the Issue and Project domains: `Issue.StartWorkflow`/`Close`/`Reopen`/`Archive` (`Issue/Domain/Issue.Transitions.cs:41-93`) and `EnsureIssue` (`Issue/Grains/IssueGrain.cs:457-461`) need to use the new types.
- **Test gap**: the middleware is not directly tested. Add `ExceptionMiddlewareSpecs.NotFound_DomainRowMissing_Returns404` and `ExceptionMiddlewareSpecs.Conflict_DomainStateInvalid_Returns409`.

### P0-3. `/close` (and friends) on a Done/archived issue returns 404 instead of 409

- **Where**: `packages/server/src/Mohist.Server/Api/IssueRoutes.Lifecycle.cs:70-91, 116-153, 155-176`
- **What**:
  - `/close`:
    ```csharp
    group.MapPost("/{number:int}/close", async (...) => {
        ...
        try
        {
            await grain.CancelAsync();
            return ApiResults.Ok();
        }
        catch (InvalidOperationException)
        {
            return ApiResults.NotFound($"Issue #{number} not found");
        }
    });
    ```
  - `CancelAsync` (`Issue/Grains/IssueGrain.cs:226-240`) calls `_issue.Close()`, which throws `InvalidOperationException` if `status == Done || _archivedAt != null` (`Issue/Domain/Issue.Transitions.cs:79-86`).
  - The user gets 404 even though the issue exists. The 404 message is literally "Issue #N not found", which is wrong on multiple axes: the issue is found, the operation is invalid.
  - `/unarchive` at `IssueRoutes.Lifecycle.cs:155-176` has the same `catch (InvalidOperationException) { return ApiResults.NotFound(...); }` pattern, but `Issue.Unarchive` does not throw — so the catch is dead. Still wrong shape.
  - `/start` (line 15-44) and `/reopen` (line 93-114) have a *correct* `catch (InvalidOperationException ex) { return ApiResults.Conflict(ex.Message); }` (return 409). The asymmetry is the smoking gun.
- **Fix**:
  1. Same as P0-2: distinguish "not found" from "conflict" exceptions.
  2. Local catch in `/close` should be `catch (IssueDomainConflictException) { return ApiResults.Conflict(ex.Message); } catch (IssueNotFoundException) { return ApiResults.NotFound(...); }` (or rely on the global middleware after fixing it).
- **Test gap**: no spec for `/close` on a Done issue. Add `IssueLifecycleApiSpecs.Close_OnDoneIssue_Returns409`.

### P0-4. Fire-and-forget `_ = CompleteWorkAsync(wrId)` exceptions are unobserved

- **Where**: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:89-114, 242-254`
- **What**:
  - All three handlers (`OnWorkflowCompleted`, `OnWorkflowStopped`, `OnWorkflowFailed`) do `_ = CompleteWorkAsync(wrId)` (or `AbortWorkAsync`).
  - `CompleteWorkAsync` calls `_issue.Complete(wrId)` which throws `InvalidOperationException` if the status is not `InProgress` (and is not the "already Done" return-false case) — see `Issue/Domain/Issue.Transitions.cs:52-62`:
    ```csharp
    if (_status == IssueStatus.Done) return false;
    if (_status != IssueStatus.InProgress)
        throw new InvalidOperationException($"Issue #{Number} is {_status}, only InProgress can complete");
    ```
  - The handler's pre-check `if (_issue.Status != Domain.IssueStatus.InProgress) return;` is a snapshot read from the bus dispatcher's thread (see P1-5). It can pass at that moment, but by the time the grain dispatches the actual `CompleteWorkAsync` call, the issue may have been transitioned to `Backlog` (e.g. user clicked Reopen, or another terminal event arrived first) — `CompleteWorkAsync` then throws.
  - The thrown exception is inside an unobserved `Task`. It surfaces as `TaskScheduler.UnobservedTaskException` on the next GC sweep. Depending on the runtime config (`ThrowUnobservedTaskExceptions`), it can be swallowed or bring down the process. In ASP.NET Core, the default is "swallowed and logged" — but the default logger is `Microsoft.Extensions.Logging` at `Warning` level; the original exception is in `ExceptionObject` and the logger message is generic.
  - The same problem exists for `AbortWorkAsync`, but `Issue.AbortWorkflow` does not throw (it returns `false` for any non-`InProgress` case at `Issue/Domain/Issue.Transitions.cs:103-112`), so the throw path is narrower.
  - Real-world failure mode: workflow runs to completion, emits `WorkflowRunCompleted` while the issue grain is being concurrently restarted (the user re-opened and started a new workflow). The new workflow's `WorkflowRunCompleted` event arrives at the *previous* issue grain instance (which is now InProgress under the *new* run), or the *new* instance under the *old* run. The handler's filter `wrId != _issue.ActiveWorkflowRunId` should reject it — but if the read is racy (P1-5), the call proceeds to `CompleteWorkAsync`, which throws because the active run id no longer matches.
  - There is **no spec** for this race. The integration test `IssueWorkflowProductLoopSpecs.IssueStart_RunnerCompletesWorkflow_IssueBecomesDone` covers the happy path, not the race.
- **Fix**:
  1. Wrap the fire-and-forget in a `ContinueWith` that logs the exception:
     ```csharp
     _ = CompleteWorkAsync(wrId).ContinueWith(t =>
         _log.LogError(t.Exception, "Bus-handler CompleteWorkAsync failed for issue {Key}", GrainKey),
         TaskContinuationOptions.OnlyOnFaulted);
     ```
  2. Or, better, stop pre-reading `_issue` in the handler and let `CompleteWorkAsync` / `AbortWorkAsync` be the single source of truth on the grain's call queue.
  3. Add a spec `IssueGrainSpecs.BusHandler_CompleteAfterStateChange_LogsAndSwallows` that exercises the race with a pre-set state.
- **Test gap**: zero. No spec covers the fire-and-forget exception path.

---

## P1 findings (should fix)

### P1-5. Bus handler reads `IssueGrain._issue` from the bus dispatcher's thread

- **Where**: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:89-114`
- **What**:
  - `OnWorkflowCompleted`/`Stopped`/`Failed` are `Action<CloudEvent>` callbacks invoked by the bus dispatcher in-line from `InMemoryEventBus.DispatchTyped` (`Infrastructure/Events/InMemoryEventBus.cs:130-149`).
  - The dispatcher is running on the **workflow grain's** call thread (because `WorkflowRunStore.Publish` is called from within `WorkflowGrain.SaveRunAsync`, which is on the grain's call queue — Orleans single-threaded activation).
  - The handlers read `IssueGrain._issue` (`if (_issue is null) return; var wrId = TryGetExtension(evt, "workflowrunid"); if (wrId is null || wrId != _issue.ActiveWorkflowRunId) return; if (_issue.Status != Domain.IssueStatus.InProgress) return;`).
  - This is a private field read on a DIFFERENT grain from a different grain's call thread. Orleans' single-threaded activation model guarantees that the *target* grain's state is only mutated on the target grain's thread, but **nothing** prevents the bus dispatcher from reading it concurrently with the target grain's own dispatch.
  - In practice, with `InMemoryEventBus` and the workflow grain being the dispatcher, the read happens synchronously while the workflow grain is mid-call. The IssueGrain's call queue may be processing another request (a user reading the issue, or the user clicking `/start` again on a re-opened issue) at the same wall-clock moment. Without a memory barrier, the read can return torn or stale data.
  - Correctness: `_issue.Complete` / `_issue.AbortWorkflow` are themselves guarded by `if (_activeWorkflowRunId != workflowRunId) return false;`, so the worst case is a wasted round-trip — the cross-grain call returns false and the state stays consistent. **Not a correctness bug, but a memory-model violation and a performance bug** (the grain call is made just to discover the state is wrong).
  - Note that the *mutating* side (`CompleteWorkAsync`/`AbortWorkAsync`) goes through the grain reference, so the actual write IS on the IssueGrain's call queue. The race is on the read.
- **Fix**:
  1. Don't read `_issue` in the handler. Just always call:
     ```csharp
     _ = Task.Run(() => grain.CompleteWorkAsync(wrId))  // already what the fire-and-forget does
     ```
     Wait — but `this.CompleteWorkAsync` goes through the *grain reference* when called from outside. When called from inside the bus handler, `this` is the *current* grain instance (in the new process), not the activation. The `_ =` then re-routes to the grain factory, which queues the call on the IssueGrain's call queue. So the read in the handler is purely an optimization.
  2. Remove the handler read entirely. The `CompleteWorkAsync` body already calls `_issue.Complete(wrId)` which returns `false` for "wrong run id" and "already Done". Just always dispatch.
  3. If you keep the read for fast-fail, at minimum mark it with a comment that it's a snapshot and not authoritative.
- **Test gap**: no memory-model / threading spec (and there can't be a reliable one for race conditions in a unit test). The fix is mechanical.

### P1-6. `OnDeactivateAsync` uses an empty `catch { }` for subscription disposal

- **Where**: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:79-87`
- **What**:
  ```csharp
  public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
  {
      foreach (var sub in _subscriptions)
      {
          try { sub.Dispose(); } catch { /* swallow — best effort during deactivation */ }
      }
      _subscriptions.Clear();
      return Task.CompletedTask;
  }
  ```
  - The `catch { }` is unscoped. Catches everything: `OutOfMemoryException`, `StackOverflowException`, `ThreadAbortException`, even `AccessViolationException` from native code (which would otherwise propagate to the runtime as an unhandled exception).
  - The intent is reasonable ("best effort during teardown"), but swallowing OOM/AV makes post-mortem debugging impossible. A more honest version is:
    ```csharp
    catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
    {
        _log.LogWarning(ex, "Failed to dispose bus subscription during deactivation");
    }
    ```
  - This is the same shape as the issue noted in workflow-audit P1-8 but for the IssueGrain. The fix should be applied to both.
- **Fix**: see above; add a log line and exclude the truly fatal exceptions.
- **Test gap**: not testable in a unit test for the actual fatal-exception case. The fix is mechanical.

### P1-7. `IssueWorkflowReconciliationService` cannot handle 10K+ stuck issues and a deleted workflow row

- **Where**: `packages/server/src/Mohist.Server/Issue/Services/IssueWorkflowReconciliationService.cs:69-104`
- **What**:
  - The candidate query is `db.Issues.AsNoTracking().Where(i => i.WorkflowRunId != null).Select(...).Take(500).ToListAsync(ct);` (line 81-85). No `OrderBy`. SQLite without an `ORDER BY` for a `Take` is non-deterministic — the same 500 rows may be picked on every sweep until the table churns.
  - The grain calls are sequential: `foreach (var row in stuck) { ... await grain.GetWorkflowStatusAsync(); }` (line 90-103). Each call round-trips to the workflow querier (which hits the DB via `WorkflowQuerier.GetStatusAsync` at `Workflow/Services/WorkflowQuerier.cs:35-59`, doing 3 DB reads: `WorkflowRuns`, `WorkflowLeases`, plus the template loader). For 500 stuck issues, this is at least 1500 DB reads per sweep, all serial.
  - At `ReconciliationPeriod = 24h` and `Take(500)`, sweeping 10,000 stuck issues takes 20 days. During that window the board shows 10K issues stuck in `in_progress`.
  - Cancellation: the per-iteration `await grain.GetWorkflowStatusAsync()` does not take a `CancellationToken`. If the host shuts down mid-sweep, the in-flight grain call may run to completion (acceptable) or fail (logged at `Warn` at line 101).
  - The `if (ct.IsCancellationRequested) break;` at line 92 is fine, but the `await grain.GetWorkflowStatusAsync()` itself is not cancellable.
- **Fix**:
  1. Add `OrderBy(i => i.IssueId)` (or `OrderBy(i => i.UpdatedAt)`) so each sweep deterministically advances.
  2. Bump `Take(500)` to `Take(2000)` (still cheap) and add a parallelism cap (`Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 8`). Measure first.
  3. Pass `ct` to the grain call (Orleans respects the call's CancellationToken, so this requires the grain method signature to accept one — but `IIssueGrain.GetWorkflowStatusAsync` does not, so this is a refactor).
  4. Add a metric or counter (`_log.LogInformation("Reconciled {Done}/{Total} in {Ms}ms", done, total, sw.ElapsedMilliseconds)`) so operators can see progress.
- **Test gap**: no spec for the service. Add a fixture-backed test that creates 50 stuck issues, runs `ReconcileStuckIssuesAsync` directly, and asserts all 50 transitions.

### P1-8. `ReconcileWithWorkflowTerminalStateAsync` cannot rescue a deleted workflow row

- **Where**: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:324-342`
- **What**:
  ```csharp
  private async Task ReconcileWithWorkflowTerminalStateAsync(string workflowRunId, WorkflowStatusView? wfStatus)
  {
      if (_issue is null || wfStatus is null) return;  // <-- early return for null
      ...
  }
  ```
  - `WorkflowQuerier.GetStatusAsync` returns `null` when the workflow row is missing (`Workflow/Services/WorkflowQuerier.cs:43`).
  - In that case the reconciliation is a no-op, the issue stays `InProgress` forever, and the daily `IssueWorkflowReconciliationService` sweep also calls `GetWorkflowStatusAsync` which also returns null and does nothing.
  - Net effect: a row-deletion accident (e.g. manual `DELETE FROM WorkflowRuns WHERE WorkflowRunId = 'wr_xxx'`) makes the issue permanently stuck.
- **Fix**:
  1. When `wfStatus is null` and the issue is `InProgress` and `ActiveWorkflowRunId` is set, transition the issue to `Cancelled` (best-effort, since we don't know the terminal state — Cancelled is the safest because the issue is no longer runnable).
  2. Log a warning: `_log.LogWarning("Workflow run {WrId} for issue {Key} is missing; transitioning to Cancelled", workflowRunId, GrainKey)`.
  3. Add a spec `IssueGrainSpecs.Reconcile_MissingWorkflowRow_TransitionsToCancelled`.
- **Test gap**: zero coverage for the deleted-workflow-row scenario.

### P1-9. `WorktreeCleanupService.OnWorkflowCompleted` is `async void`

- **Where**: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54-84`
- **What**:
  - `async void` handlers cannot be awaited by the bus dispatcher. The `try/catch` in `InMemoryEventBus.DispatchTyped` (`Infrastructure/Events/InMemoryEventBus.cs:130-149`) only catches *synchronously-thrown* exceptions. An `async void` method that throws *after* an `await` (which is the common case here) escapes the catch and goes to the SynchronizationContext.
  - In ASP.NET Core, an unhandled exception from `async void` is captured by the request SynchronizationContext (or, in a hosted-service context, by the thread-pool's `UnhandledException` event). Either way, the process can crash.
  - The current handler has a `try/catch (Exception ex)` around the awaits (line 61-83), so the *body* is safe. But the *method signature* is still `async void` — if the try/catch is ever refactored, exceptions escape silently.
  - Also, `async void` blocks structured concurrency — you can't `await` it, can't observe it with `ContinueWhenAll`, can't test it in isolation.
- **Fix**:
  1. Change the signature to `private async Task OnWorkflowCompletedAsync(CloudEvent evt)` and bridge:
     ```csharp
     _bus.OnType(EventCatalog.ReverseDns.WorkflowRunCompleted, evt => OnWorkflowCompletedAsync(evt));
     ```
     The bus still calls a sync `Action<CloudEvent>`; inside, the lambda kicks off the Task and **observes it**:
     ```csharp
     _ = OnWorkflowCompletedAsync(evt).ContinueWith(t => _log.LogError(t.Exception, "..."), TaskContinuationOptions.OnlyOnFaulted);
     ```
  2. Add a test that throws inside the git call and asserts the bus dispatch continues.
- **Test gap**: no spec for the cleanup service; the `async void` shape is not testable without rewriting it.

### P1-10. Worst-case latency between workflow terminal and Issue status is up to 24h

- **Where**: `Issue/Services/IssueWorkflowReconciliationService.cs:24, 40-67` + `Issue/Grains/IssueGrain.cs:63-77, 79-87`
- **What**:
  - The IssueGrain bus subscription only fires while the activation is alive. If the activation deactivates between the workflow terminal event's emit and the next emit (Orleans deactivates after `CollectionAgeLimit`, default 2h of inactivity), the bus subscription is gone.
  - The new activation will subscribe on its next `OnActivateAsync`, but events emitted during the deactivation window are lost (in-memory bus, no outbox).
  - The `IssueWorkflowReconciliationService` is the safety net, with `ReconciliationPeriod = 24h`. So a missed event stays unresolved for up to 24h until the next sweep picks the issue.
  - 24h of "stuck in_progress" is a long time for a board UI. The user-visible symptom: an issue is "in progress" with a workflow that has actually been `Completed`/`Failed` for a day.
  - The `GetWorkflowStatusAsync` read path is the second safety net — any user opening the issue detail page triggers the lazy reconciliation. So *visible* issues reconcile in seconds; only issues nobody opens (the long tail) wait for the daily sweep.
- **Fix**:
  1. Add a per-WorkflowGrain completion event queue (the simplest "outbox" pattern) so terminal events are persisted alongside the run state and re-played on reactivation. The `EventStore` table is already populated by `WorkflowEventPersistence.StageAsync` — read it on `OnActivateAsync` of the IssueGrain and replay any terminal events for the current run id that haven't been seen. This is the **durable outbox** the bus was supposed to provide but doesn't.
  2. Or, shrink the sweep period. `ReconciliationPeriod` is a `public static` field (line 24) that tests can patch; production should set it to `TimeSpan.FromMinutes(15)` for a worst-case 15-minute window. The current 24h is set for "daily batch" semantics; reconcile semantics should be sub-hourly.
  3. Log the count of issues reconciled per sweep so operators can size the period based on observed stuck-issuetraffic.
- **Test gap**: no spec for the worst-case latency. The integration test for the bus path is happy-path only.

### P1-11. `CancelAsync` does not atomically transition issue + workflow terminal

- **Where**: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:226-240`
- **What**:
  - `CancelAsync` calls `wfGrain.StopAsync("issue-closed")`, awaits it, then calls `_issue.Close()`.
  - `StopAsync` is on a *different* grain. The workflow grain emits `WorkflowRunStopped` (via `WorkflowRunStore.Publish`) which the IssueGrain bus subscription picks up and calls `AbortWorkAsync`.
  - `AbortWorkAsync` calls `_issue.AbortWorkflow(wrId)` which checks `if (_activeWorkflowRunId != workflowRunId) return false;` (line 105 of `Issue.Transitions.cs`). After `_issue.Close()` (line 238), `_activeWorkflowRunId` is `null` and `_status` is `Cancelled`.
  - The race: between `StopAsync` returning and `_issue.Close()` running, the bus dispatch fires `OnWorkflowStopped` on the *same* IssueGrain (because the bus dispatch is synchronous from `WorkflowRunStore.Publish`). The handler does `if (_issue.Status != InProgress) return;` — status is still `InProgress` at that moment, so the handler proceeds to `AbortWorkAsync` which transitions the issue to `Cancelled` and clears `ActiveWorkflowRunId`.
  - **Then** `_issue.Close()` runs. `Close()` checks `if (_status == Done || _archivedAt != null) throw` (line 81 of `Issue.Transitions.cs`); status is `Cancelled`, neither condition is true, so `Close()` proceeds and transitions to `Cancelled` *again* — but `Cancelled -> Cancelled` is a no-op for status and clears `ActiveWorkflowRunId` (already null). Safe in this case.
  - But the order is racy: if the bus dispatch is delayed (e.g., the IssueGrain is concurrently processing a user request), `_issue.Close()` may run *first*. Then the bus handler sees `Status == Cancelled` and exits early. Safe.
  - The real concern: comment in `CancelAsync` at line 234-237 says "*The bus subscription below (workflow.run.stopped) will also call AbortWorkAsync for the same Stopped event; AbortWorkflow is idempotent (no-op if already Cancelled), so the double dispatch is safe.*" The double-dispatch is *not* guaranteed — it depends on whether the IssueGrain's bus handler runs before `_issue.Close()` mutates state. If the bus handler wins, the close-path's `_issue.Close()` is a redundant no-op; if the close-path wins, the bus handler's pre-check `Status != InProgress` is true and the handler skips. **Both orderings are safe today**, but the design is fragile.
  - Worse: if the workflow grain's `StopAsync` itself fails (e.g. `WorkflowDomainException` for already-stopped), the `await wfGrain.StopAsync` throws, the issue state is not mutated, and the user's `/close` returns 409. That's correct — but the existing `catch (InvalidOperationException) { return ApiResults.NotFound(...); }` in the route maps `WorkflowDomainException` (which extends `InvalidOperationException`?) to 404 incorrectly. **Wait** — let me check. `WorkflowDomainException` is at `Workflow/Domain/`. If it does not extend `InvalidOperationException`, the local catch misses it and the global middleware (`ExceptionMiddleware.cs:15-19`) catches it → 409. Verify.
- **Fix**:
  1. Make `CancelAsync`'s comment match reality: the bus dispatch is on the workflow grain's thread, which is the same thread that returned from `StopAsync` *if and only if* Orleans has not switched activations. The double-dispatch is best-effort.
  2. Document the race explicitly in `CancelAsync`'s xmldoc.
  3. Verify `WorkflowDomainException` does NOT extend `InvalidOperationException` (so the global middleware catches it correctly); if it does, refactor to a different base.
- **Test gap**: no spec for the close + bus interaction.

---

## P2 findings (improve)

### P2-12. `Issue.Complete` throws `InvalidOperationException` for the "not InProgress" case

- **Where**: `Issue/Domain/Issue.Transitions.cs:52-62`
- **What**: Already covered as part of P0-4. From a design standpoint, the domain mixes "returns false" (idempotent guard for "wrong run id" and "already Done") with "throws" (idempotent guard for "in some other terminal state"). The two should be unified. `AbortWorkflow` already returns false for all non-`InProgress` cases (`Issue.Transitions.cs:103-112`); `Complete` should follow the same shape:
  ```csharp
  public bool Complete(string workflowRunId, DateTime? now = null)
  {
      if (_activeWorkflowRunId != workflowRunId) return false;
      if (_status != IssueStatus.InProgress) return false;  // <-- not throw
      ...
  }
  ```
  This eliminates the throw path in `Complete` entirely, which removes P0-4's fire-and-forget exception vector.

### P2-13. `WorktreeCleanupService` only listens to `WorkflowRunCompleted`

- **Where**: `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:41`
- **What**: The same workflow-audit finding (P1-20). A workflow that ends in `Stopped` or `Failed` (e.g., user clicks `/stop`, or the workflow fails on a check) leaves the worktree behind. The worktree is not the user's concern at that point, but disk usage grows. Subscribe to `WorkflowRunStopped` and `WorkflowRunFailed` as well.

### P2-14. String-based workflow status mapping is fragile

- **Where**: `Issue/Grains/IssueGrain.cs:330-341` + `Workflow/Domain/Run/WorkflowRun.cs:6` + `Workflow/Services/WorkflowStatusMapper.cs:55-58`
- **What**:
  - `ReconcileWithWorkflowTerminalStateAsync` switches on the string `"Completed"`, `"Failed"`, `"Stopped"`. The strings come from `_run.Status.ToString()` of the `WorkflowRunStatus` enum: `Pending, Running, AwaitingApproval, Paused, Stopped, Completed, Failed`.
  - If the enum is renamed (e.g., `Completed` -> `Complete`) the strings change and the switch silently misses. The compiler cannot help.
  - Fix: change `WorkflowStatusView.Status` to a typed `WorkflowRunStatus` enum (it's currently `string`) and have the consumer switch on the enum.

### P2-15. `IssueStatus` lacks a `Paused` state — pause conflates with active

- **Where**: `Issue/Domain/IssueStatus.cs:3-9` + `Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs:80-93`
- **What**: A workflow in `WorkflowRunStatus.Paused` (e.g. the user clicked `/force-stop`) keeps the issue in `IssueStatus.InProgress` and the projection sets `health: "paused"`. The issue shows as `in_progress` on the board, but its `health` is `paused` — the user has to look at `health` to know it's not actively running. This is information-design poor. A new `IssueStatus.Paused` (and a back-mapping in the projection) would make the board clearer.

### P2-16. ~~ProjectId override in CreateIssueRequest~~ — verified safe

- **Where**: `Api/IssueRoutes.Crud.cs:28-52` + `Api/IssueRoutes.Dtos.cs:6-15`
- **What**: `CreateIssueRequest` does NOT have a `projectId` field (only `Title`, `Body`, `Labels`, `Priority`, `Model`, `AgentConfig`, `StageModels`, `WorkflowProfileId`, `RepositoryName`). The `projectId` is taken from the route (`var project = GetRequiredProject(ctx);` line 39). The test data in the integration specs uses an explicit `projectId` in the body (e.g. `IssueWorkflowProductLoopSpecs.cs:48` `new { title = "...", projectId = project.Id }`) but that field is silently ignored by the DTO deserializer (no setter). The call ends up using the route-resolved project. Verified safe. **Dropping from final report.**

### P2-17. `WorktreeCleanupService.Dispose` swallows subscription disposal errors silently

- **Where**: `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:48-52`
- **What**: Same shape as P1-6, less severe (Dispose runs at host shutdown, not per-grain). Log the exception.

### P2-18. `Complete` / `AbortWorkflow` silent `false` returns are not logged

- **Where**: `Issue/Domain/Issue.Transitions.cs:52-62, 103-112` + `Issue/Grains/IssueGrain.cs:242-254`
- **What**: The bus handler does `if (!_issue.Complete(workflowRunId)) return;` (line 245). The `false` return is silent. For debugging "why didn't my issue transition to Done", a log line at `Debug` level on the false return would help.

### P2-19. No visibility into reconciliation health

- **Where**: `Issue/Services/IssueWorkflowReconciliationService.cs:84-103`
- **What**: The service logs the candidate count at the start and the per-issue failures at `Warn`, but there is no summary of how many were actually transitioned. An operator cannot tell "the sweep is healing 10 issues per day" vs "the sweep is broken and healing 0".

### P2-20. `ReconciliationPeriod` is a `public static` field — testable but ugly

- **Where**: `Issue/Services/IssueWorkflowReconciliationService.cs:24`
- **What**: The comment at line 19-21 says "the period is tunable for tests via the ReconciliationPeriod static field". A `public static` mutable field is global state; tests that patch it can race. Better: take an `IOptions<ReconciliationOptions>` or a constructor parameter, and let the test fixture override the DI registration.

---

## P3 findings (nitpick)

### P3-21. `OnDeactivateAsync` returns `Task.CompletedTask` while doing sync work

- **Where**: `Issue/Grains/IssueGrain.cs:79-87`
- **What**: The disposal loop is synchronous; the return is correct, but a reader may expect an `await` in the method body. Add a comment.

### P3-22. `IssueStore.SaveAsync` has no optimistic concurrency

- **Where**: `Infrastructure/Data/Issue/IssueStore.cs:33-47`
- **What**: The `WorkflowRunRow` has an `ETag` (line 196 of `MohistDbContext.cs`); the `IssueRow` does not. A concurrent writer (e.g. a manual SQL fix-up script) can race a grain save and silently overwrite. The same pattern was noted in the workflow audit (P1-9). Add `[ConcurrencyCheck]` or a row version.

### P3-23. `IssueIdentityResolver.GetIdAsync` re-queries on every call

- **Where**: `Issue/Services/IssueIdentityResolver.cs:16-20` + every `IssueRoutes.Lifecycle.cs:25,57,80,103,134,165` call
- **What**: For a single API request, `GetIssueGrainAsync` calls `GetIdAsync` once, but every issue-route handler resolves a fresh issue identity per request. A `IAsyncCache<string, string?>` or a per-request memo would help; for the volume mohist handles today, it's noise.

### P3-24. `IssueGrain` constructor has 11 dependencies

- **Where**: `Issue/Grains/IssueGrain.cs:37-59`
- **What**: Adding a 12th dependency (a 4th bus subscriber) will make this even more unwieldy. A `IBusSubscriptions` or `IWorkflowTerminalSubscriber` facade would group the 3 `_eventBus.OnType` calls (line 74-76) and the `_subscriptions` list into a single class.

### P3-25. `IssueRowMapper.ById` and `ByNumber` deserialize the full State JSON per row

- **Where**: `Issue/Services/IssueRowMapper.cs:17-25`
- **What**: The `IssueRow.ProjectId` and `IssueRow.Number` are computed columns (line 181-186 of `MohistDbContext.cs`) — but the mapper ignores them and re-deserializes the JSON. For `ListAsync` over the whole table, this is N JSON parses. Filter in SQL using the computed columns, then deserialize the survivors.

### P3-26. `Issue.Complete(now:)` is not threaded by the bus path

- **Where**: `Issue/Domain/Issue.Transitions.cs:52-62` + `Issue/Grains/IssueGrain.cs:89-114, 242-247`
- **What**: The bus handler doesn't pass the workflow terminal timestamp; `UpdatedAt` is set to "now" inside `Complete`. The audit log loses the actual terminal time.

### P3-27. `CreateAsync` exposes a caller-supplied `issueId`

- **Where**: `Issue/Grains/IssueGrain.cs:344-364`
- **What**: The caller can pass any `issue_xxx` id. The `if (_issue is not null) throw` guard makes double-creation safe, but the error message leaks the id convention. Cosmetic.

### P3-28. `Aborting` non-cancelled issue silently does nothing — no log

- **Where**: `Issue/Grains/IssueGrain.cs:89-114, 249-254`
- **What**: `AbortWorkAsync` returns silently when the issue is not in `InProgress`. The bus handler's pre-check covers the common case, but a corner case (issue is `Backlog` due to concurrent Reopen, then terminal event arrives) hits the `false` return with no diagnostic.

---

## Executive summary

### Production-ready?

**No — three P0 bugs need to ship before this code can be considered safe for general use.** The two most important are:

1. **`WorktreeCleanupService` is dead code** (P0-1): terminal workflow events do not carry the `projectid` / `issueno` extensions the service requires. Worktrees from completed workflows leak on disk. This silently regresses the Step-8 design cleanup.

2. **`/close` (and a class of similar lifecycle routes) returns 404 instead of 409** when the user tries to close a Done/archived issue (P0-3). Combined with the `ExceptionMiddleware` mapping every `InvalidOperationException` to 404 (P0-2), domain state-conflict errors are routinely misreported as "not found" — confusing for users and bad for monitoring.

3. **Fire-and-forget bus-handler exceptions are unobserved** (P0-4). When the bus subscription races a state transition, the exception goes to `TaskScheduler.UnobservedTaskException` and is lost.

### Top 3 risks

1. **Stuck-in-`InProgress` issues** are the most likely user-visible failure mode. The causes are stacked:
   - Bus subscription can miss the event when the IssueGrain is deactivated (P1-10).
   - Daily sweep is best-effort, 500/24h, no `OrderBy` (P1-7).
   - Sweep cannot handle a deleted workflow row (P1-8).
   - Net worst-case latency: 24h+ for a never-opened issue, plus permanent stuck state for deleted rows.

2. **Data loss on workflow cancel** if the bus dispatch and `CancelAsync` race (P1-11). The current code is accidentally safe, but the design is fragile.

3. **Worktree leaks** because `WorktreeCleanupService` never receives the `projectid` it needs (P0-1) and only listens to `Completed` (P2-13). On a long-running Mohist deployment, `~/.mohist/workspaces/issues/` will accumulate stale worktrees.

### Top 3 quick wins

1. **Fix the `projectid`/`issueno` extension on terminal workflow events** (P0-1, 1-2 hours): thread `WorkflowRunRow.Metadata.Annotations["projectId"]` and `["issueId"]` into the `CloudEventFactory.Create` call in `WorkflowRunStore.Publish`. This unblocks the entire `WorktreeCleanupService` and is a one-line change per call site.

2. **Distinguish "not found" from "conflict" exceptions** (P0-2, P0-3, 2-3 hours): introduce `IssueNotFoundException` and `IssueDomainConflictException`; have `EnsureIssue` throw the former and domain transitions throw the latter; update the middleware to map them to 404 / 409 respectively; update the local lifecycle route catches.

3. **Stop pre-reading `_issue` in the bus handler and add a `ContinueWith` to observe the fire-and-forget exception** (P1-5 + P0-4, 1 hour):
   ```csharp
   private void OnWorkflowCompleted(CloudEvent evt)
   {
       var wrId = TryGetExtension(evt, "workflowrunid");
       if (wrId is null) return;
       _ = CompleteWorkAsync(wrId).ContinueWith(t =>
           _log.LogError(t.Exception, "Bus subscription CompleteWorkAsync failed for issue {Key}", GrainKey),
           TaskContinuationOptions.OnlyOnFaulted);
   }
   ```
   This removes the thread-safety violation (P1-5) and the unobserved-exception vector (P0-4) in one change. `CompleteWorkAsync` already short-circuits on the wrong-state path, so the extra grain call is cheap.

### Follow-up audits (out of scope here)

- **Workflow** state machine coverage of `Approve` / `Reject` edge cases — see workflow-audit.md P0-1/P0-2.
- **EventBus** dispatch model (sync vs async) — see event-bus-audit.md.
- **SignalR / Web** board refresh latency for cross-grain state transitions — see event-pipeline-audit.md.
