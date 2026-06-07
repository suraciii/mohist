# Direct Orchestration Coupling Audit

**Scope**: mohist .NET backend (`packages/server/src/Mohist.Server/`).
**Goal**: Inventory every place a Grain reaches into another Grain (or service) via direct method call, and evaluate whether each coupling should be an event-driven subscription instead.
**Method**: Static read of every Grain, every API route, and every event-bus touch point. Direct calls were confirmed by `grep` of `GrainFactory.GetGrain<...>(...)` and `_grains.GetGrain<...>(...)` patterns; event-bus subscribers were confirmed by `grep` of `_eventBus.On(...)` / `_bus.On(...)` (only `EventBridge.cs:34` exists).

---

## 1. Per-grain cross-call map

### 1.1 `WorkflowGrain` (`Workflow/Grains/WorkflowGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IWorkflowStageLockGrain` | `AcquireSequentialAsync` | `RunCoreAsync` → `AcquireStageLocksIfNeededAsync` when dispatching a new stage that has `lockBehavior: sequential` | `WorkflowGrain.cs:422-423` |
| `IWorkflowStageLockGrain` | `ReleaseAsync` | `RunCoreAsync` releases lock on `StageCompleted` / `StageFailed`; also on retry/rerun/clear-executable-state | `WorkflowGrain.cs:462-463` |
| `IWorkflowBacklogGrain` | `EnqueueAsync` (own wrId) | `RegisterToBacklogAsync` — when a workflow is runnable but no runner is yet claimed | `WorkflowGrain.cs:489-490` |
| `IWorkflowBacklogGrain` | `EnqueueAsync` (next wfId) | `RequeueWorkflowIdAsync` — when a stage lock is released and the next waiter in `WorkflowStageLockGrain` should be enqueued | `WorkflowGrain.cs:497-498` |
| `IRunnerGrain` | `AssignWorkAsync` (returns `RunnerWorkAssignmentResult`) | `AssignRunnerWorkAsync` after `PrepareWorkAsync` builds a `WorkDispatch` | `WorkflowGrain.cs:665-666` |
| `IWorkflowRunStore` (DB, not a grain) | `SaveAsync(run)` / `SaveAsync(run, events)` | every `CommitAsync`; ETag-optimistic | `WorkflowGrain.cs:1052, 1071` |
| `IStateStore<WorkLease>` (DB) | `SaveAsync` / `DeleteAsync` | every lease acquisition/clear | `WorkflowGrain.cs:1090-1093` |
| `IStateStore<WorkflowExecutionContext>` (DB) | `SaveAsync` | first start (when `pendingVariables != null`) | `WorkflowGrain.cs:1095-1098` |
| `IWorkflowBacklogDirectory` (singleton) | `RegisterProject(projectId)` | before enqueueing | `WorkflowGrain.cs:488, 496` |
| `IEventBus` | `Emit("stage_changed", ...)` | `EmitStageChanged` after every state transition | `WorkflowGrain.cs:892-899` |
| `IEnumerable<IWorkflowCompletionHook>` | `OnCompletedAsync(ctx)` | when `WorkflowRunCompleted` event is observed by `On` | `WorkflowGrain.cs:995-1012` (via `OnWorkflowCompletedAsync` at `967-971`) |
| `IWorkflowRunStore` (transitively) | `Emit(dto.Type, dto)` for every persisted `WorkflowEvent` | inside `WorkflowRunStore.SaveAsync` after commit | `WorkflowRunStore.cs:102-108` |

**Outgoing event-bus emits** (in `WorkflowGrain.cs` itself, not via the store):
- `"stage_changed"` — `WorkflowGrain.cs:892` (called from `OnWorkflowStartedAsync`/`ResumedAsync`/`PausedAsync`/`StoppedAsync`/`OnApprovalApprovedAsync`/`OnApprovalRejectedAsync`).

**Outgoing emits via the store** (declared by `WorkflowEventPersistence.StageAsync`):
- All 16 `WorkflowEvent` subtypes listed in `WorkflowEventSerializer.FromData` (`WorkflowEventSerializer.cs:15-35`) — `WorkflowRunStarted`, `WorkflowRunResumed`, `WorkflowRunPaused`, `WorkflowRunStopped`, `WorkflowRunCompleted`, `WorkflowRunFailed`, `StageStarted`, `StageCompleted`, `StageFailed`, `StageApprovalRequested`, `StageApprovalResolved`, `TaskCompleted`, `TaskFailed`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled`. Each is emitted in `WorkflowRunStore.Publish` (`WorkflowRunStore.cs:102-108`) right after the DB commit.

**In-grain `_reactions`-style dispatch** (`WorkflowGrain.cs:920-941`): a single `On(WorkflowEvent e, string? reason)` switch routes every domain event produced by `_run` to one of the `On…` handlers listed below. All callers reach it from `CommitAsync` at `WorkflowGrain.cs:917`.

| Event | Reaction | File:line |
|---|---|---|
| `WorkflowRunStarted` | `OnWorkflowStartedAsync` → `EmitStageChanged("started")` + `EnsureWorkHeartbeatAsync` | `On:924`, `943-947` |
| `WorkflowRunResumed` | `OnWorkflowResumedAsync` → `EmitStageChanged("resumed")` + heartbeat | `On:925`, `949-953` |
| `WorkflowRunPaused` | `OnWorkflowPausedAsync` → `EmitStageChanged("paused", reason)` + `DisableWorkHeartbeatAsync` | `On:926`, `955-959` |
| `WorkflowRunStopped` | `OnWorkflowStoppedAsync` → `EmitStageChanged("stopped", reason)` + disable heartbeat | `On:927`, `961-965` |
| `WorkflowRunFailed` | `DisableWorkHeartbeatAsync` (no `stage_changed` emit) | `On:928` |
| `WorkflowRunCompleted` | `OnWorkflowCompletedAsync` → disable heartbeat + `DispatchCompletedHooksAsync` | `On:929`, `967-971`, `995-1012` |
| `StageStarted` | `EnsureWorkHeartbeatAsync` | `On:930` |
| `StageCompleted` | `ReleaseStageLocksAsync(stage, "completed")` | `On:931` |
| `StageFailed` | `ReleaseStageLocksAsync(stage, "failed")` | `On:932` |
| `StageApprovalRequested` | `DisableWorkHeartbeatAsync` | `On:933` |
| `StageApprovalResolved` | `OnApprovalResolvedAsync` → `OnApprovalApprovedAsync` (emit `approved`, heartbeat) / `OnApprovalRejectedAsync` (emit `rejected`) | `On:934`, `973-993` |
| `TaskCompleted` | `EnsureWorkHeartbeatAsync` | `On:935` |
| `TaskFailed` | none (no-op) | `On:936` |
| `CheckPassed` | `EnsureWorkHeartbeatAsync` | `On:937` |
| `CheckFailed` | none | `On:938` |
| `CheckPending` | `EnsureWorkHeartbeatAsync` | `On:939` |
| `RepairScheduled` | none | `On:940` |

### 1.2 `IssueGrain` (`Issue/Grains/IssueGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IProjectGrain` | `GetAsync()` | `ResolveRepositoryRefAsync` (called from `CreateAsync` when no `repositoryRef` is supplied) | `IssueGrain.cs:67-68` |
| `IProjectGrain` | `GetAsync()` | `ResolveIssueRepositoryAtStartAsync` (every `StartWorkAsync` resolves the default repo) | `IssueGrain.cs:74-75` |
| `IProjectGrain` | `GetAsync()` | `StartWorkAsync` — second call, after `StartWorkflow` to build the `WorkflowProjectContext` | `IssueGrain.cs:105-106` |
| `IWorkflowGrain` | `StartAsync(input)` | `StartWorkAsync` — first stage of every issue start | `IssueGrain.cs:122-127` |
| `IWorkflowGrain` | `StopAsync("issue-closed")` | `CancelAsync` — when the user closes the issue while a workflow is active | `IssueGrain.cs:165-166` |
| `IStateStore<Issue>` (DB) | `LoadAsync` / `SaveAsync` | state hydration + every `SaveIssueAsync` | `IssueGrain.cs:59, 347` |
| `IssueWorkflowProfileRegistry` (singleton) | `_profiles.Get(IssueWorkflowProfiles.DefaultId)` | for default workflow structure and projection | `IssueGrain.cs:113, 115, 222` |
| `WorkflowQuerier` (scoped service, reads DB) | `GetStatusAsync(wrId)` | `GetWorkflowStatusAsync` | `IssueGrain.cs:221` |
| `ProjectWorkflowProfileManager` (scoped) | `GetDefaultTemplateAsync` / `SetDefaultTemplateAsync` | if no default template yet, `StartWorkAsync` sets one | `IssueGrain.cs:109-110` |
| `WorkflowProfileManager` (scoped) | `LoadTemplateAsync(wrId, projectId, issueId)` | every start — resolves effective template (custom or inherited) | `IssueGrain.cs:112` |
| `IssueIdentityResolver` (scoped) | `GetIdAsync(projectId, number)` | `LoadIssueSummaryAsync` for prerequisite lookups | `IssueGrain.cs:361` |
| `IDbContextFactory<MohistDbContext>` | `CreateDbContextAsync` for `IssueComments` | `AddCommentAsync` | `IssueGrain.cs:385` |

**Outgoing event-bus emits**: **none** from `IssueGrain` itself.

**In-grain reactions / state transitions** (in `Issue/Domain/Issue.Transitions.cs`):
- `StartWorkflow(wrId)` — sets `ActiveWorkflowRunId` + `Status = InProgress` (`Issue.Transitions.cs:41-50`).
- `Complete(workflowRunId)` — `Status = Done` (returns false silently if the id doesn't match) (`Issue.Transitions.cs:52-61`).
- `Close()` — `Status = Cancelled` (`Issue.Transitions.cs:78-84`).
- `Archive()` / `Unarchive()` / `Reopen()` — operate on `ArchivedAt` and reset `Status` to `Backlog` (`Issue.Transitions.cs:63-92`).

**DB writes that other readers depend on** (no grain subscriber, see §2):
- `IssueStore` row (table `Issues`, JSON column `State`) — read by `IssueQuerier` and every REST list endpoint. Issue.Status in the row only ever moves via the grain methods above; "blocked" / "completed" UI states are derived by `MohistDefaultWorkflowProjection.ProjectWorkflowState` (see `IssueQuerier.cs:215-232`).

### 1.3 `IssueWorkflowCompletionHook` (`Issue/Services/WorkflowProfiles/IssueWorkflowCompletionHook.cs`)

This is a singleton `IWorkflowCompletionHook` (registered at `MohistServiceRegistration.cs:77`). The only hook implementation.

| Target | Method | Trigger | File:line |
|---|---|---|---|
| `IIssueGrain` | `CompleteWorkAsync(workflowRunId)` | `WorkflowGrain.DispatchCompletedHooksAsync` calls `OnCompletedAsync` for **every** `WorkflowRunCompleted` | `IssueWorkflowCompletionHook.cs:28-29` |
| `ProjectQuerier` (singleton) | `GetByIdAsync(projectId)` | same — to look up the project for worktree cleanup | `IssueWorkflowCompletionHook.cs:31` |
| `IGitService` | `RemoveWorktreeAsync(project.Path, project.Name, issueNumber)` | same — best-effort cleanup of the agent worktree | `IssueWorkflowCompletionHook.cs:42` |

**Outgoing emits**: none.

**Reactions / subscribers**: this *is* a subscriber — registered via `IEnumerable<IWorkflowCompletionHook>` constructor injection into `WorkflowGrain` (`WorkflowGrain.cs:36, 46, 55`).

### 1.4 `RunnerGrain` (`Runner/Grains/RunnerGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IRunnerRegistryGrain` (previous key) | `UnregisterAsync` | `RegisterAsync` when the runner's project scope changes | `RunnerGrain.cs:61-62` |
| `IRunnerRegistryGrain` (current key) | `RegisterAsync` | every `RegisterAsync` and every `TouchPresenceAsync` (heartbeat) | `RunnerGrain.cs:65-66, 229-230` |
| `IRunnerRegistryGrain` | `UnregisterAsync` | `UnregisterAsync` and `HandleTimeoutAsync` | `RunnerGrain.cs:81-82, 218-219` |
| `IWorkflowGrain` | `ReportResultAsync(runnerId, workId, result)` | `ReportResultAsync` on the runner — the result of `POST /api/runner/{id}/report` | `RunnerGrain.cs:172-173` |
| `IWorkflowGrain` | `GetRunStatusAsync` | `ReportResultAsync` — to compose the response | `RunnerGrain.cs:174` |
| `IWorkflowBacklogGrain` | `ClaimAsync(RunnerId)` | `ClaimFromBacklogAsync` — for each project the runner can serve | `RunnerGrain.cs:264-265` |
| `IWorkflowBacklogDirectory` (singleton) | `ListProjects()` | `BacklogProjectIdsAsync` (the "global" runner) | `RunnerGrain.cs:238` |
| `IDbContextFactory<MohistDbContext>` | `CreateDbContextAsync` for `Projects` | `BacklogProjectIdsAsync` for the global runner to enumerate all known projects | `RunnerGrain.cs:239-243` |
| `IWorkflowGrain` | `GetClaimedRunnerIdAsync` / `GetRunStatusAsync` / `GetCurrentWorkIdAsync` | `IsWorkRunnableAsync` — when picking a queued work to actually dispatch | `RunnerGrain.cs:310-321` |

**Outgoing event-bus emits**: **none**.

**In-grain timer/reminder reactions**:
- `RegisterGrainTimer` every 10s calls `CheckHeartbeatAsync` (`RunnerGrain.cs:67-70`); if elapsed > 2min, `HandleTimeoutAsync` runs (`RunnerGrain.cs:202-220`).
- `HandleTimeoutAsync` clears `_works`, sets `_status = Offline`, and unregisters from the registry — but does **not** notify the workflow or the agent session (see §4 scenario 6).

**DB writes that other readers depend on**:
- `RunnerRegistryGrain` is itself a non-persistent in-memory dictionary (`RunnerRegistryGrain.cs:8`); RunnerGrain's writes are the registration record. There is **no DB persistence** of the registry — registry state is lost on Silo restart.

### 1.5 `AgentSessionGrain` (`Sessions/Grains/AgentSessionGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IAgentSessionStore` (DB) | `SaveAsync` / `LoadAsync` | every state mutation; re-loads from DB on `OnActivateAsync` | `AgentSessionGrain.cs:31, 56, 277, 271` |
| `IDbContextFactory<MohistDbContext>` | `CreateDbContextAsync` | for `AgentSessions` and `AgentSessionRuntimeEvents` rows (the "projection" tables) | `AgentSessionGrain.cs:84-92, 121-126, 250-253, 419-423, 464-466` |
| `IEventBus` | `Emit("coder_session_started", …)` | `AttachAgentAsync` | `AgentSessionGrain.cs:284-294` |
| `IEventBus` | `Emit("coder_text_chunk", …)` / `"coder_thought_chunk"` / `"coder_tool_call"` | `EmitTranscriptEntry` for each persisted runtime event | `AgentSessionGrain.cs:296-343` |
| `IEventBus` | `Emit("coder_session_status_changed", …)` | whenever a `agent_liveness_status` runtime event lands | `AgentSessionGrain.cs:345-352` |
| `IEventBus` | `Emit("coder_session_completed" | "coder_session_failed" | "coder_session_cancelled", …)` | when the `agent_session_terminal` runtime event lands | `AgentSessionGrain.cs:354-365` |

**Outgoing direct grain calls**: **none**. `AgentSessionGrain` does not call any other grain.

**In-grain state reactions** (no `_reactions` list; reactions are inlined in the command methods):
- `EnsureAsync` — emits a projection update via `UpdateProjectionAsync`; no event.
- `AppendRuntimeEventsAsync` — on `agent_liveness_status` updates the session state and emits `coder_session_status_changed`; on `agent_session_terminal` updates the session and emits the terminal event; on `agent_usage_update` aggregates usage.
- `FailIfRunningAsync` — terminal `Fail` + `EmitTerminal("failed")`. Currently has **no callers** other than the route layer (no `RunnerGrain` ever calls it; see §4 scenario 6).
- `AttachAgentAsync` — emits `coder_session_started`.

### 1.6 `WorkflowBacklogGrain` (`Workflow/Grains/WorkflowBacklogGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IWorkflowGrain` | `AssignRunnerAsync(runnerId)` | `ClaimAsync` — for each waiting wfId the runner pulled | `WorkflowBacklogGrain.cs:63-64` |
| `IWorkflowBacklogDirectory` (singleton) | `RegisterProject(GrainKey)` | `OnActivateAsync` | `WorkflowBacklogGrain.cs:26` |
| `IStateStore<WorkflowBacklogState>` (DB) | `LoadAsync` / `SaveAsync` | every mutation | `WorkflowBacklogGrain.cs:27, 41, 69, 81, 111` |

**Outgoing event-bus emits**: **none**.

**In-grain logic**: the entire grain is a state machine over a `Queue<string>` + `HashSet<string>`. `ClaimAsync` is the *only* consumer-side method; it loops through `_waiting`, calls `AssignRunnerAsync` on each `IWorkflowGrain` synchronously, removes successful ones from `_all` and breaks out.

### 1.7 `WorkflowStageLockGrain` (`Workflow/Grains/WorkflowStageLockGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IStateStore<WorkflowStageLockState>` (DB) | `LoadAsync` / `SaveAsync` | every mutation | `WorkflowStageLockGrain.cs:22, 35, 44, 52, 65, 71, 100` |

**Outgoing direct grain calls**: **none**. Pure state machine; callers (`WorkflowGrain`) interpret the `nextWorkflowRunId` field on the response and re-enqueue the next waiter themselves.

### 1.8 `IssueCounterGrain` (`Issue/Grains/IssueCounterGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IStateStore<IssueCounterState>` (DB) | `LoadAsync` / `SaveAsync` | `OnActivateAsync`; every `NextAsync` | `IssueCounterGrain.cs:20, 27` |

**Outgoing direct grain calls / emits**: **none**.

### 1.9 `EpicGrain` (`Epic/Grains/EpicGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IEpicCounterGrain` | `NextAsync()` | `CreateAsync` only | `EpicGrain.cs:28` |
| `IDbContextFactory<MohistDbContext>` | `CreateDbContextAsync` for `Epics` + `EpicIssues` | every method | `EpicGrain.cs:30, 51, 83, 100, 154-178, 182, 192` |

**Outgoing direct grain calls / emits**: only the counter call above.

### 1.10 `ProjectGrain` (`Project/Grains/ProjectGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IDbContextFactory<MohistDbContext>` | `CreateDbContextAsync` for `Projects` + `ProjectWorkflowProfiles` | every method | `ProjectGrain.cs:33, 69, 96, 111, 192, 242` |

**Outgoing direct grain calls / emits**: **none**. (Note: many callers go through `ProjectGrain.GetAsync` — see `IssueGrain.cs:67, 74, 105`.)

### 1.11 `RunnerRegistryGrain` (`Runner/Grains/RunnerRegistryGrain.cs`)

| Target grain / service | Method called | Trigger | File:line |
|---|---|---|---|
| `IRunnerRegistryGrain` (the other key) | `ListAllAsync()` | `ListEligibleRunnersAsync` — when the global registry asks the project registry and vice-versa | `RunnerRegistryGrain.cs:73-79` |

**Outgoing emits**: **none**. Pure in-memory map keyed by runner id.

---

## 2. Direct-coupling hot spots (synchronous A → B → C chains)

The following paths form request-time synchronous chains that exceed one hop. Each is a candidate for a state machine + event reaction model.

### Chain 1 — User clicks "Start" on an issue
`Api/IssueRoutes.Lifecycle.cs:33` → `IssueGrain.StartWorkAsync` → `IProjectGrain.GetAsync` (`IssueGrain.cs:67, 74, 105`) → `IWorkflowGrain.StartAsync` (`IssueGrain.cs:122-127`).
3 grain calls per click, plus DB lookups. Latency is fine in single-Silo tests; the synchronous dependency on `ProjectGrain` means a `ProjectGrain` reactivation on every start is on the hot path.

### Chain 2 — User closes an issue while a workflow is running
`Api/IssueRoutes.Lifecycle.cs:84` → `IssueGrain.CancelAsync` → `IWorkflowGrain.StopAsync("issue-closed")` (`IssueGrain.cs:165-166`).
2 grain calls. The flow is: caller → IssueGrain → WorkflowGrain. The reverse direction (WorkflowGrain → IssueGrain) only fires on `WorkflowRunCompleted` via the hook, not on `WorkflowRunStopped`. See scenario 2 in §4.

### Chain 3 — User approves / rejects / retries / reruns
`Api/IssueRoutes.WorkflowControl.cs:23, 37, 52, 66, 80, 96, 114` → `IWorkflowGrain.{Resume|Approve|Reject|Retry|Rerun|Pause|Stop}Async`. All single-hop; no follow-up to other grains inside the same call (only the `On` reaction inside the WorkflowGrain).

### Chain 4 — User adds a runtime task (rebase, etc.)
`Api/IssueRoutes.Rebase.cs:46` → `IWorkflowGrain.AddTaskAsync` (→ `IWorkflowGrain.AddTasksAsync` for batch in `WorkflowRoutes.cs:74`). Single hop.

### Chain 5 — Runner reports a task result
REST `POST /api/runner/{id}/report` → `Api/RunnerRoutes.cs:86-87` → `IRunnerGrain.ReportResultAsync` → `IWorkflowGrain.ReportResultAsync(runnerId, workId, result)` (`RunnerGrain.cs:172-173`) → `IWorkflowGrain.ReportResultAsync` does the lease lookup + state advance (`WorkflowGrain.cs:358-388`).
3-hop chain: HTTP → RunnerGrain → WorkflowGrain. The reverse direction (WorkflowGrain → RunnerGrain) fires from `AssignRunnerWorkAsync` (`WorkflowGrain.cs:665-666`) for *next* work assignment. This is a tightly coupled round trip.

### Chain 6 — Runner polls for work
REST `POST /api/runner/{id}/poll` → `Api/RunnerRoutes.cs:62-63` → `IRunnerGrain.PollAsync` → `DequeueAssignedWorkAsync` (in-memory) → if none, `ClaimFromBacklogAsync` → for each project: `IWorkflowBacklogGrain.ClaimAsync(runnerId)` (`RunnerGrain.cs:264-265`) → for each waiting wfId: `IWorkflowGrain.AssignRunnerAsync(runnerId)` (`WorkflowBacklogGrain.cs:63-64`).
A single poll call can perform N×M grain calls (N projects × M waiting workflows). For 5 projects × 3 waitings that's up to 15 grain hops per poll. The "is work still runnable" check then does 3 more grain calls per candidate (`RunnerGrain.cs:310-321`).

### Chain 7 — WorkflowGrain dispatches work to a Runner
`RunCoreAsync` → `PrepareWorkAsync` → `AssignRunnerWorkAsync` → `IRunnerGrain.AssignWorkAsync` (`WorkflowGrain.cs:665-666`). Single hop, but the runner then has to react to the assignment, which today is just a return value (`RunnerWorkAssignmentResult`).

### Chain 8 — WorkflowGrain acquires a stage lock
`AcquireStageLocksIfNeededAsync` → `IWorkflowStageLockGrain.AcquireSequentialAsync` (`WorkflowGrain.cs:422-423`). Single hop, but on rejection the `WorkflowGrain` re-enqueues itself to the backlog on the next `IsRunnable()` tick (`WorkflowGrain.cs:255-256`). This is the closest the current design has to a "wait for resource" queue.

### Chain 9 — WorkflowGrain releases a stage lock and re-enqueues the next waiter
`ReleaseStageLocksAsync` → `IWorkflowStageLockGrain.ReleaseAsync` → if `result.NextWorkflowRunId` is non-empty → `RequeueWorkflowIdAsync` → `IWorkflowBacklogGrain.EnqueueAsync(workflowRunId)` (`WorkflowGrain.cs:462-498`).
This is the only place a "next waiter" notification is propagated — it's a 3-hop synchronous chain across grains, with the contract encoded in the return value rather than an event.

### Chain 10 — `IssueWorkflowCompletionHook` reacts to completion
`WorkflowGrain.DispatchCompletedHooksAsync` → `IssueWorkflowCompletionHook.OnCompletedAsync` → `IIssueGrain.CompleteWorkAsync` (`IssueWorkflowCompletionHook.cs:28-29`) → optional `IGitService.RemoveWorktreeAsync` (`IssueWorkflowCompletionHook.cs:42`).
2 grain hops + a side-effect service call. Failure of the cleanup is logged but not retried.

---

## 3. Existing event reactions (in-grain `_reactions` / `On(...)` dispatch)

The codebase has **two** distinct dispatch patterns for in-grain reactions, both of them internal to the same grain.

### 3.1 `WorkflowGrain.On(WorkflowEvent e, string? reason)` switch

`WorkflowGrain.cs:920-941` — full table in §1.1. The reactions are inlined in the grain and run synchronously after every state mutation that produces events. This is a classic "post-commit reaction" model: `WorkflowRun` produces events, `WorkflowGrain` reacts. The `IEventBus.Emit("stage_changed", …)` and `IEnumerable<IWorkflowCompletionHook>` are the only fan-out from this dispatch; everything else is a local method call.

### 3.2 `AgentSessionGrain` runtime-event reactions

`AgentSessionGrain.cs:108-230` — `AppendRuntimeEventsAsync` maps the raw `agent_liveness_status` / `agent_session_terminal` / `agent_usage_update` events into `AgentSessionEvent` state changes *before* persisting. The reactions are tightly coupled to the type switch on the runtime event (`AgentSessionGrain.cs:131-179`); they mutate `_session` via `session.MarkActive` / `session.Complete` / `session.Cancel` / `session.Fail` and then call `_stateStore.SaveAsync`. The `_eventBus.Emit(...)` calls happen *after* the state save in the same method, so they are post-commit fan-out.

### 3.3 Subscribers to the in-process `IEventBus`

**Only one subscriber** in the entire server: `Events/Hub/EventBridge.cs:34`. It subscribes to all 45 event types in `EventBusEventTypes.All` and forwards them as SignalR messages to the `project:{projectId}` group. **No grain in `Mohist.Server` subscribes to the bus for in-process reactions.**

That means the entire `_eventBus.Emit(...)` surface today is a "wide broadcast to the web UI" mechanism; it is not used for any cross-grain coordination. This is the most important structural finding of this audit.

### 3.4 `WorkflowRunStore.Publish` fan-out

`WorkflowRunStore.cs:102-108` re-emits every persisted `WorkflowEvent` to the bus after the DB transaction commits. So in practice, the bus is also receiving *all* workflow domain events, but only `EventBridge` listens.

---

## 4. DB-as-eventbus patterns (write-through-DB → re-read)

These are flows where Grain A writes a row, and Grain B / a service / the UI re-reads the row on every request to detect the change. They are de-facto "events" being carried by the database.

### 4.1 `WorkflowRun.State` (JSON column) → `WorkflowQuerier` and `IssueQuerier`

- **Write**: `WorkflowRunStore.SaveAsync` (`WorkflowRunStore.cs:43-60`) inside the same EF transaction that stages the `EventRow`s.
- **Read**: `WorkflowQuerier.GetStatusAsync` (`WorkflowQuerier.cs:35-59`) — used by `IssueQuerier.GetInfoAsync`, `IssueQuerier.LoadWorkflowStatesAsync` (`IssueQuerier.cs:179-211`), `AgentSessionQuerier.BuildTaskProgressMapAsync` (`AgentSessionQuerier.cs:230-264`), `WorkflowActivityQuerier.ListActiveAgentsAsync` (`WorkflowActivityQuerier.cs:22-69`), and REST endpoints `WorkflowRoutes.GetYaml`, `GetVariables`, `AgentRoutes.Status/Activity/Activity`, `IssueRoutes.WorkflowProfile.GetStatus`, `WorkflowEventRoutes.GetEvents`.
- **Why this is a DB-as-eventbus pattern**: every reader fetches the row on each request. There is no in-process invalidation; the bus emission in `WorkflowRunStore.Publish` is only used to notify the UI, not to invalidate caches or wake other grains.

### 4.2 `WorkflowLeases.State` (JSON column) → `WorkflowGrain` and `WorkflowActivityQuerier`

- **Write**: `WorkflowLeaseStore.SaveAsync` (`WorkflowLeaseStore.cs:27-37`), called from `WorkflowGrain.SaveLeaseAsync` and from `WorkflowGrain.ClearAndDeleteLeaseAsync` (`WorkflowGrain.cs:1090-1093, 682-686`).
- **Read**: `WorkflowGrain.RestoreLeaseAsync` (`WorkflowGrain.cs:704-712`), `RunnerGrain.IsWorkRunnableAsync` (`RunnerGrain.cs:308-321`), `WorkflowActivityQuerier.LoadLeasesAsync` (`WorkflowActivityQuerier.cs:71-79`), `AgentSessionQuerier.LoadLeasesAsync` (`AgentSessionQuerier.cs:550-556`), `IssueQuerier.LoadWorkflowStatesAsync` (`IssueQuerier.cs:179-211`).

### 4.3 `RunnerRegistryGrain._runners` (in-memory, not DB) → `AgentRoutes`, `OpencodeRoutes`, `RunnerStatusRoutes`

- **Write**: every `RegisterAsync` from `RunnerGrain` (`RunnerGrain.cs:65, 229`) and every `UnregisterAsync` (`RunnerGrain.cs:81, 218`).
- **Read**: `Api/AgentRoutes.cs:43-44` reads both `RunnerRegistry(projectId)` and `RunnerRegistryKeys.Global` on every `/api/projects/{ref}/agent/status` and `/agent/activity` call; `Api/OpencodeRoutes.cs:22-26` reads both on every `/api/projects/{ref}/opencode/models`; `RunnerStatusService.GetRunnersAsync` (`RunnerStatusService.cs:20-34`) reads them via `ListEligibleRunnersAsync` (`RunnerRegistryGrain.cs:65-94`).
- **Why this is a DB-as-eventbus pattern**: there is no push notification; each HTTP request pays the cost of fanning out to N+1 grains (project registry + global registry) and per-runner `GetRuntimeStateAsync` to project the state.

### 4.4 `RunnerConnectionTracker._connections` (in-memory) → `RunnerStatusService`

- **Write**: `RunnerHub.OnConnectedAsync` / `OnDisconnectedAsync` (`RunnerHub.cs:14-32`).
- **Read**: `RunnerStatusService.DeriveConnectionState` (`RunnerStatusService.cs:104-108`).
- This is a thin in-process broadcast — fine as-is, but it's a side-channel that bypasses both the event bus and the registry grain.

### 4.5 `AgentSessions` + `AgentSessionRuntimeEvents` tables → `AgentSessionQuerier`

- **Write**: `AgentSessionGrain.UpdateProjectionAsync` (`AgentSessionGrain.cs:82-92`) and the runtime-event rows inlined in `AppendRuntimeEventsAsync` (`AgentSessionGrain.cs:127-210`).
- **Read**: every `AgentSessionQuerier.ListByWorkflowAsync`, `GetByWorkflowAsync`, `ListByIssueAsync`, `ListCurrentAsync`, `GetSessionMetadataAsync`, `GetSessionEventsAsync`, `GetActivityAsync` (`AgentSessionQuerier.cs:25-228`).
- **Why this is a DB-as-eventbus pattern**: the `coder_session_*` events emitted by `AgentSessionGrain` are *duplicated* as persisted rows. Both representations exist; the bus version is for the UI, the table is for everything else (querier, API, `WorkflowActivityQuerier`).

### 4.6 `WorkflowBacklogDirectory._projects` (in-memory) → `RunnerGrain` and `WorkflowGrain`

- **Write**: `WorkflowBacklogGrain.OnActivateAsync` (`WorkflowBacklogGrain.cs:26`) and `WorkflowGrain.RegisterToBacklogAsync` / `RequeueWorkflowIdAsync` (`WorkflowGrain.cs:488, 496`).
- **Read**: `RunnerGrain.BacklogProjectIdsAsync` (`RunnerGrain.cs:233-251`) on every poll, plus a DB fallback (`Projects` table) to fill missing entries.
- **Why this is a DB-as-eventbus pattern**: a singleton in-memory dictionary is the "discoverable project set" for runners, but if the Silo restarts the set has to be rebuilt by reading the projects table — and there is no explicit re-hydration call.

### 4.7 `Events` (table) → `IEventStore.ListWorkflowEventsAsync` → `WorkflowEventRoutes`

- **Write**: `WorkflowEventPersistence.StageAsync` inside `WorkflowRunStore.SaveAsync` and `EventStore.AppendWorkflowEventAsync` (`EventStore.cs:19-28`).
- **Read**: `WorkflowEventRoutes.MapGet("/events")` (`WorkflowEventRoutes.cs:14-24`) and `MapGet("/api/workflow-runs/{id}/events")` (`WorkflowEventRoutes.cs:26-30`).
- This is a true audit-log pattern, not a coordination bus; leave as-is.

---

## 5. Per-route grain calls (`packages/server/src/Mohist.Server/Api/`)

| Route file | Endpoint | Grains called | Event-driven alternative? |
|---|---|---|---|
| `IssueRoutes.Lifecycle.cs:15-44` | `POST /api/projects/{ref}/issues/{n}/start` | `IIssueGrain.GetStartEligibilityAsync` → `StartWorkAsync` (chains to `IProjectGrain.GetAsync` ×2, `IWorkflowGrain.StartAsync`) | No — direct request/response |
| `IssueRoutes.Lifecycle.cs:46-68` | `POST …/comments` | `IIssueGrain.AddCommentAsync` (writes `IssueComments` row only) | No — direct |
| `IssueRoutes.Lifecycle.cs:70-91` | `POST …/close` | `IIssueGrain.CancelAsync` (chains to `IWorkflowGrain.StopAsync` if active) | No — direct |
| `IssueRoutes.Lifecycle.cs:93-114` | `POST …/reopen` | `IIssueGrain.ReopenAsync` | No — direct |
| `IssueRoutes.Lifecycle.cs:116-153` | `POST …/archive` | `IIssueGrain.ArchiveAsync` + `IGitService.RemoveWorktreeAsync` | No — direct |
| `IssueRoutes.Lifecycle.cs:155-176` | `POST …/unarchive` | `IIssueGrain.UnarchiveAsync` | No — direct |
| `IssueRoutes.Lifecycle.cs:178-222` | `POST /archive-completed` | for each completed: `IIssueGrain.ArchiveAsync` + git cleanup | No — direct batch |
| `IssueRoutes.Crud.cs:13-26` | `GET …/issues` | none (IssueQuerier only) | No |
| `IssueRoutes.Crud.cs:28-52` | `POST …/issues` | `IIssueCounterGrain.NextAsync` → `IIssueGrain.CreateAsync` | No — direct |
| `IssueRoutes.Crud.cs:54-63` | `GET …/issues/{n}` | none (IssueQuerier) | No |
| `IssueRoutes.Crud.cs:65-89` | `PATCH …/issues/{n}` | `IIssueGrain.UpdateFullAsync` | No — direct |
| `IssueRoutes.Prerequisites.cs:11-37` | `POST …/prerequisites` | `IIssueGrain.AddPrerequisiteAsync` | No — direct |
| `IssueRoutes.Prerequisites.cs:39-62` | `DELETE …/prerequisites/{n}` | `IIssueGrain.RemovePrerequisiteAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:13-25` | `POST …/resume` | `IWorkflowGrain.ResumeAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:27-39` | `POST …/approve` | `IWorkflowGrain.ApproveAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:41-54` | `POST …/reject` | `IWorkflowGrain.RejectAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:56-68` | `POST …/retry` | `IWorkflowGrain.RetryAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:70-82` | `POST …/rerun` | `IWorkflowGrain.RerunAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:86-98` | `POST …/force-stop` | `IWorkflowGrain.PauseAsync` | No — direct |
| `IssueRoutes.WorkflowControl.cs:102-121` | `POST …/stop` | `IWorkflowGrain.StopAsync` | No — direct |
| `IssueRoutes.Rebase.cs:14-57` | `POST …/rebase` | `IWorkflowGrain.HasIncompleteTaskWithUsesAsync` → `AddTaskAsync` | No — direct |
| `IssueRoutes.WorkflowProfile.cs:199-220` | `GET …/workflow/status` | `IIssueGrain.GetWorkflowStatusAsync` | No — direct |
| `WorkflowRoutes.cs:37-57` | `POST /api/workflow-runs/{id}/tasks` | `IWorkflowGrain.AddTaskAsync` | No — direct |
| `WorkflowRoutes.cs:59-77` | `POST /api/workflow-runs/{id}/tasks/batch` | `IWorkflowGrain.AddTasksAsync` | No — direct |
| `WorkflowRoutes.cs:11-19` | `GET /api/workflow-runs/{id}/yaml` | none (WorkflowQuerier) | No |
| `WorkflowRoutes.cs:21-35` | `GET /api/workflow-runs/{id}/variables/effective` | none (WorkflowQuerier + WorkflowProfileManager) | No |
| `WorkflowEventRoutes.cs:14-24` | `GET /api/projects/{ref}/issues/{n}/events` | none (IEventStore) | No |
| `WorkflowEventRoutes.cs:26-30` | `GET /api/workflow-runs/{id}/events` | none (IEventStore) | No |
| `WorkflowSessionRoutes.cs:10-11` | `GET /api/workflow-runs/{id}/sessions` | none (AgentSessionQuerier) | No |
| `WorkflowSessionRoutes.cs:13-17` | `GET /api/workflow-runs/{id}/sessions/{name}` | none (AgentSessionQuerier) | No |
| `WorkflowSessionRoutes.cs:22-26` | `GET /api/projects/{ref}/issues/{n}/workflow-sessions` | none (AgentSessionQuerier) | No |
| `RunnerRoutes.cs:15-26` | `POST /api/runner/{id}/register` | `IRunnerGrain.RegisterAsync` | No — direct |
| `RunnerRoutes.cs:28-33` | `POST /api/runner/{id}/unregister` | `IRunnerGrain.UnregisterAsync` | No — direct |
| `RunnerRoutes.cs:35-58` | `POST /api/runner/{id}/heartbeat` | `IRunnerGrain.HeartbeatAsync` (or `HeartbeatRepairAsync`) | No — direct |
| `RunnerRoutes.cs:60-78` | `POST /api/runner/{id}/poll` | `IRunnerGrain.PollAsync` (chains to N+1 grains, see §2 chain 6) | Partial — `PollAsync` could be replaced by a push from `WorkflowGrain` via event |
| `RunnerRoutes.cs:80-90` | `POST /api/runner/{id}/report` | `IRunnerGrain.ReportResultAsync` (chains to `IWorkflowGrain.ReportResultAsync`) | No — direct |
| `RunnerRoutes.cs:92-101` | `POST /api/runner/{id}/sessions/.../ensure` | `IAgentSessionGrain.EnsureAsync` | No — direct |
| `RunnerRoutes.cs:103-110` | `POST /api/runner/{id}/sessions/.../attach` | `IAgentSessionGrain.AttachAgentAsync` | No — direct |
| `RunnerRoutes.cs:112-121` | `POST /api/runner/{id}/sessions/.../events` | `IAgentSessionGrain.AppendRuntimeEventsAsync` | No — direct |
| `AgentRoutes.cs:16-23` | `GET /api/projects/{ref}/agent/status` | `IRunnerRegistryGrain` (project + global) + N `IRunnerGrain.IsAvailableAsync` | Partial — could subscribe to `coder_session_*` for the active set |
| `AgentRoutes.cs:25-29` | `GET /api/projects/{ref}/agent/sessions` | none (AgentSessionQuerier) | No |
| `AgentRoutes.cs:31-36` | `GET /api/projects/{ref}/agent/activity` | same fan-out as `agent/status` + AgentSessionQuerier | Partial — same |
| `OpencodeRoutes.cs:18-34` | `GET /api/projects/{ref}/opencode/models` | `IRunnerRegistryGrain` × 2 | Partial |
| `OpencodeRoutes.cs:36-49` | `GET /api/opencode/runtime` | none (ConfigService) | No |
| `ProjectRoutes.cs:24-46` | `POST /api/projects` | `IProjectGrain.CreateAsync` | No — direct |
| `ProjectRoutes.cs:66-72` | `PATCH /api/projects/{ref}` | `IProjectGrain.UpdateAsync` | No — direct |
| `ProjectRoutes.cs:74-80` | `DELETE /api/projects/{ref}` | `IProjectGrain.DeleteAsync` | No — direct |
| `ProjectRoutes.cs:88-108` | `POST /api/projects/{ref}/repositories` | `IProjectGrain.AddRepositoryAsync` | No — direct |
| `ProjectRoutes.cs:110-120` | `PATCH /api/projects/{ref}/repositories/{name}` | `IProjectGrain.SetDefaultRepositoryAsync` | No — direct |
| `ProjectRoutes.cs:122-128` | `DELETE /api/projects/{ref}/repositories/{name}` | `IProjectGrain.RemoveRepositoryAsync` | No — direct |
| `EpicRoutes.cs:21-30` | `POST /api/projects/{ref}/epics` | `IEpicGrain.CreateAsync` (chains to `IEpicCounterGrain.NextAsync`) | No — direct |
| `EpicRoutes.cs:41-53` | `PATCH /api/projects/{ref}/epics/{id}` | `IEpicGrain.UpdateAsync` | No — direct |
| `EpicRoutes.cs:55-81` | `POST /api/projects/{ref}/epics/{id}/issues` | `IEpicGrain.LinkIssueAsync` | No — direct |
| `EpicRoutes.cs:83-93` | `DELETE /api/projects/{ref}/epics/{id}/issues/{issueId}` | `IEpicGrain.UnlinkIssueAsync` | No — direct |
| `EpicRoutes.cs:95-98` | `POST /api/projects/{ref}/epics/{id}/done|close` | `IEpicGrain.SetStatusAsync` | No — direct |
| `RunnerStatusRoutes.cs:13-19` | `GET /api/projects/{ref}/runners` | `RunnerStatusService` → `IRunnerRegistryGrain` + N `IRunnerGrain.GetRuntimeStateAsync` | Partial |
| `WorkspaceRoutes.cs:195-231` | `POST /api/projects/{ref}/issues/{n}/cleanup` | `IIssueGrain.GetWorkflowStatusAsync` + git | No — direct |

**Summary**: of 60+ routes, ~50 are direct user-action handlers that map 1:1 to a grain method. The remaining ~10 (polling, listing, status, models) are read-side fan-outs over the registry or session tables.

---

## 6. Specific coupling evaluation

### 6.1 `WorkflowRunCompleted` → `IssueGrain` transitions `Issue.Status = Done`

- **Current path**: `WorkflowGrain.OnWorkflowCompletedAsync` (`WorkflowGrain.cs:967-971`) → `DispatchCompletedHooksAsync` (`WorkflowGrain.cs:995-1012`) → `IWorkflowCompletionHook.OnCompletedAsync` → `IssueWorkflowCompletionHook.OnCompletedAsync` (`IssueWorkflowCompletionHook.cs:24-50`) → `IIssueGrain.CompleteWorkAsync(workflowRunId)` (`IssueWorkflowCompletionHook.cs:28-29`) → `Issue.Complete(workflowRunId)` (`Issue.Transitions.cs:52-61`).
- **Mechanism today**: this is already a "completion hook" interface (`IWorkflowCompletionHook`) — a poor man's event. The bus is **not** used here. The hook is a singleton injected into `WorkflowGrain` (`WorkflowGrain.cs:36, 46, 55`).
- **Alternative (event-driven)**: `WorkflowGrain` emits a `WorkflowRunCompleted` event (it already does, via `WorkflowRunStore.Publish` at `WorkflowRunStore.cs:102-108`); `IssueGrain` subscribes on `OnActivateAsync` and calls its own `CompleteWorkAsync`. The hook would dissolve into the bus.
- **Trade-offs**:
  - **Latency**: identical. The hook is invoked synchronously *inside* `OnWorkflowCompletedAsync`; an event subscriber would also be invoked synchronously by the in-process bus (the bus is in-process, single-threaded w.r.t. the publishing grain's activation).
  - **Testability**: better. The spec at `tests/.../Issue/Api/IssueCreationSpecs.cs:382` and `tests/.../Epic/Domain/EpicLifecycleSpecs.cs:276` both call `CompleteWorkAsync` *directly on the grain*, bypassing the hook. The hook has no direct test (`grep "IWorkflowCompletionHook"` returns only the interface declaration and the singleton registration). With an event subscription, the spec could instead emit the event and assert the projection.
  - **Reliability**: the hook swallows exceptions (`WorkflowGrain.cs:1003-1010`) and continues looping over the rest; an event subscriber on the bus would have the same swallow-and-log behavior (`InMemoryEventBus.cs:46-53`). Tie.
  - **If `IssueGrain` is down**: today, a failed `CompleteWorkAsync` is logged and forgotten. An event subscriber would have the same outcome (in-process bus with no persistence). Tie.
  - **Discoverability**: better. Today, "what reacts to a workflow completing?" is hidden inside `IssueWorkflowCompletionHook` (51 lines, one impl). With an event, an `On("WorkflowRunCompleted", ...)` subscription on `IssueGrain` would be co-located with the rest of the Issue lifecycle.

### 6.2 `WorkflowRunStopped` → `IssueGrain` transitions `Issue.Status = Cancelled`

- **Current path**: **does not exist as an automatic transition**. The only `WorkflowRunStopped` → Issue reaction today is the *inverse* direction: `IssueGrain.CancelAsync` calls `IWorkflowGrain.StopAsync("issue-closed")` (`IssueGrain.cs:165-166`). The user-driven path: `POST /api/projects/{ref}/issues/{n}/close` → `IssueGrain.CancelAsync` → which (a) calls `wfGrain.StopAsync(...)` and (b) calls `_issue.Close()` (sets `Status = Cancelled`) and saves. Both happen in the same method body. So today, the issue's status is set *unconditionally* in `CancelAsync` (`IssueGrain.cs:168-169`), not in reaction to the workflow being stopped.
- **Failure modes**:
  - If a user calls `POST …/stop` directly on a workflow (without `/close`), the issue stays in `InProgress`. The user has to call `/close` separately. There is no current path that would automatically transition the issue on a workflow stop.
  - If the workflow `StopAsync` rejects (e.g., the run is already `Completed`), `CancelAsync` will still go on to close the issue (`IssueGrain.cs:165-169`) — `wfGrain.StopAsync` is awaited and the exception is not caught. Wait, re-reading: `await wfGrain.StopAsync("issue-closed")` is the first statement; if it throws, `_issue.Close()` is never reached. So the issue is only closed on a successful stop. That's reasonable.
- **Alternative (event-driven)**: `WorkflowGrain` emits `WorkflowRunStopped`; `IssueGrain` subscribes and calls a new `MarkCancelledAsync(workflowRunId)` that calls `_issue.Close()` only if `ActiveWorkflowRunId == workflowRunId`.
- **Trade-offs**:
  - **Latency**: identical (in-process).
  - **Testability**: better — you could write a spec that emits a `WorkflowRunStopped` event with a fake `WorkflowGrain` and asserts the issue's status changes, no need to spin up the whole workflow just to test the close transition.
  - **Behaviour change**: today, the only way the issue ever reaches `Cancelled` is via `CancelAsync`. An event-driven path would also reach it from any other code that calls `WorkflowGrain.StopAsync` (e.g., the future "system stops the workflow because something is wrong" path). That's a *feature* — it removes a class of bugs where the workflow is stopped but the issue is still showing `InProgress`.
  - **Idempotency**: `Issue.Close` (`Issue.Transitions.cs:78-84`) throws if `_status == Done || _archivedAt != null`, so a duplicate event would throw. A subscriber would need a try/catch or an idempotent guard.

### 6.3 `WorkflowRunFailed` → ???

- **Current path**: **does not exist**. `WorkflowRunFailed` is handled in `WorkflowGrain.On` at `WorkflowGrain.cs:928`: the only reaction is `DisableWorkHeartbeatAsync()`. There is no transition on `Issue.Status`. Searching for `Failed` in `IssueGrain.cs` and `IssueWorkflowCompletionHook.cs` returns zero matches. Searching for `WorkflowRunFailed` in the whole `Issue/` directory returns zero matches. The issue stays in `InProgress` forever.
- **UI compensation**: `MohistDefaultWorkflowProjection.ProjectAttention` (`MohistDefaultWorkflowProjection.cs:67-74`) detects `workflow.Status == "Failed"` and emits `WorkflowAttention.Blocked`. `MohistDefaultWorkflowProjection.RuntimeStatus` (`ProjectWorkflowState.cs:80-93`) returns `"blocked"` for the *UI* health. But the persisted `Issue.Status` is unchanged and any querier that does not re-project from the workflow state will still see `in_progress`.
- **Alternative (event-driven)**: `WorkflowGrain` emits `WorkflowRunFailed` (it already does, via the `WorkflowRunStore.Publish` path); `IssueGrain` subscribes and calls `MarkFailedAsync(workflowRunId)` which sets a new `IssueStatus.Failed` (would need to be added to the enum). Or, more conservatively, leave `Issue.Status` as `InProgress` and surface the failure only via the workflow projection — but document this explicitly.
- **Trade-offs**:
  - **Latency**: identical.
  - **Testability**: identical.
  - **Semantics**: a "failed" workflow is not the same as a "cancelled" or "done" issue. The current enum is `Backlog | InProgress | Done | Cancelled` (`IssueStatus.cs:3-8`). A `Failed` value would need to be added, and any querier/UI code that maps status names would need to handle it.
  - **If `IssueGrain` is down**: same as today — the workflow state is in DB, the projection picks it up next read, the UI shows "blocked". The event-driven version doesn't change this.

### 6.4 `AgentSessionGrain` signals "agent finished" → `WorkflowGrain` marks task `Completed`

- **Current path** (full trace):
  1. Runner calls `POST /api/runner/{id}/sessions/{p}/{wr}/{name}/events` with a `agent_session_terminal` event of `status: "completed"` (`Api/RunnerRoutes.cs:112-121`).
  2. `IAgentSessionGrain.AppendRuntimeEventsAsync` is called (`AgentSessionGrain.cs:108`).
  3. The `agent_session_terminal` branch at `AgentSessionGrain.cs:139-152` produces `AgentSessionEvent`s via `session.Complete(now, exitCode)` and persists.
  4. `EmitTerminal(session, "completed")` is called (`AgentSessionGrain.cs:227`), which emits `"coder_session_completed"` to the bus (`AgentSessionGrain.cs:354-365`).
  5. **The bus is only consumed by `EventBridge`** (`Events/Hub/EventBridge.cs:34`), which forwards to SignalR for the UI.
  6. **There is no path from this event to `WorkflowGrain`**.
- The path that *does* mark a task `Completed` is the runner calling `POST /api/runner/{id}/report` (`Api/RunnerRoutes.cs:80-90`) with a `WorkResult` of `Status="completed"`. That goes through `IRunnerGrain.ReportResultAsync` → `IWorkflowGrain.ReportResultAsync` (`RunnerGrain.cs:172-173`), which is the synchronous path described in §2 chain 5.
- So the `AgentSessionGrain` and the `WorkflowGrain` are two parallel state machines: the runner drives the workflow state, the agent session state is informational/transcript. The agent finishing is *not* the trigger for the workflow task to be marked complete — the runner's `/report` call is.
- **Trade-off**: this is actually a clean separation; the agent may finish many times in a session (e.g., re-attach) and the workflow task is the unit of work the runner committed to, so having the runner report task completion makes sense. The `AgentSessionGrain` events are observational. **No change recommended.**

### 6.5 `AgentSessionGrain` signals "agent failed" → `WorkflowGrain` marks task `Failed`

- Same as 6.4: the path is **runner → `/report` with `Status="failed"`** → `IWorkflowGrain.ReportResultAsync` → `ProcessTaskResult` (`WorkflowGrain.cs:724-729`) → `_run.FailTask(...)`. The `AgentSessionGrain` only marks itself failed via `session.Fail` (`AgentSessionGrain.cs:147, 237`), which is informational.
- The audit hypothesis that "the path exists for completion but is weak for failure" is accurate for `AgentSessionGrain` (it does have a `FailIfRunningAsync` method at `AgentSessionGrain.cs:232-241`), but that method has no callers from `RunnerGrain` or any other grain — only the route layer (none of the REST routes call it; `grep` returns only the definition and its spec).
- **Trade-off**: same as 6.4. The runner, not the session, is the authority on task outcome. Leaving as-is is correct.

### 6.6 `RunnerGrain.HeartbeatTimeout` → `AgentSessionGrain.FailIfRunningAsync`

- **Current path**: **does not exist**. `RunnerGrain.CheckHeartbeatAsync` (`RunnerGrain.cs:202-212`) on timeout calls `HandleTimeoutAsync` (`RunnerGrain.cs:214-220`), which:
  1. Clears `_works` (the in-memory `Dictionary<string, RunnerTrackedWork>`).
  2. Sets `_status = RunnerStatus.Offline`.
  3. Calls `IRunnerRegistryGrain.UnregisterAsync(RunnerId)`.
- It does **not**:
  - Notify the `IWorkflowGrain` whose lease is held. The lease row in `WorkflowLeases` is still there; on next activation the workflow grain will see the stale lease but the workflow's `IsClaimed` and `Claim.RunnerId` are persisted in `WorkflowRun.State`, so the next poll or `RunCoreAsync` heartbeat will attempt to call back into the (offline) runner.
  - Notify the `IAgentSessionGrain` for any active session. The session grain's `_lastHeartbeat` and `_status` are independent; sessions remain "running" forever.
  - Fail the in-flight task. The `WorkResult` with `Status="failed"` is never sent.
- **Consequence**: a runner crash mid-work leaves the workflow in a state where the only recovery is a user action (`retry` / `rerun` / `force-stop`) — see `tests/.../Runner/Grain/RunnerFailureSpecs.cs` for the explicit test `RegisteredButUnavailableLeaseOwner_RemainsRecoveryBlocked` (`RunnerFailureSpecs.cs:75-100`) which asserts the workflow *stays* blocked. This is a deliberate design choice (the runner may come back).
- **Alternative (event-driven)**: `RunnerGrain` emits `RunnerOffline` (or `RunnerHeartbeatTimeout`); the workflow grain subscribes and (a) clears its claim, (b) optionally fails the in-flight task with a synthetic "runner timeout" `WorkResult`, (c) re-enqueues the workflow. The agent session grain subscribes and calls `FailIfRunningAsync` for any session whose `Runtime.RunnerId == this`.
- **Trade-offs**:
  - **Latency**: identical.
  - **Testability**: a spec could emit a fake `RunnerOffline` event and assert the workflow reverts to "queued" / "failed". Today, the only way to test this is to actually stop a runner (which is what `RunnerFailureSpecs.cs` does).
  - **Behaviour change**: today, the system is *deliberately* conservative — a runner restart doesn't lose work. An event-driven timeout would change this to a more aggressive recovery model. The choice depends on the product's SLO: a user who restarts their runner within 5 minutes expects work to resume; a user whose runner process crashed expects a fast fail.
  - **Where the policy lives**: today, the policy is "do nothing, let the user decide". Moving it to event subscribers would split the policy across the workflow grain and the agent session grain, with no single source of truth. A dedicated `IRunnerLifecycleObserver` (subscribed to the bus) would centralize the decision.

### 6.7 `WorkflowGrain` dispatches a task → `RunnerGrain` picks it up

- **Current path** (full trace):
  1. `WorkflowGrain.RunCoreAsync` (`WorkflowGrain.cs:247-285`) finds work via `_run.NextWork()` and prepares a dispatch.
  2. `RegisterToBacklogAsync` (`WorkflowGrain.cs:484-492`) → `IWorkflowBacklogGrain.EnqueueAsync(GrainKey)` (this is the *queueing* path, when no runner is yet claimed).
  3. **OR**, if the workflow is already claimed, `AssignRunnerWorkAsync` (`WorkflowGrain.cs:663-672`) → `IRunnerGrain.AssignWorkAsync(dispatch)`.
  4. The runner's `AssignWorkAsync` (`RunnerGrain.cs:134-160`) records the work in `_works` keyed by `${workflowRunId}\u001f{workId}` and returns `Assigned`.
- The runner only *delivers* the work when it next calls `POST /api/runner/{id}/poll` and `DequeueAssignedWorkAsync` finds an `Assigned` entry.
- **Alternative (event-driven)**: `WorkflowGrain` emits `WorkReady(runnerId, dispatch)`; the runner subscribes and adds it to `_works`. Polling becomes fallback only.
- **Trade-offs**:
  - **Latency**: events are in-process, so the latency is the same. The only difference is the runner doesn't have to poll to know there's work.
  - **Polling cost saved**: every poll currently costs `1 grain call` to `RunnerGrain` + N grain calls to `WorkflowBacklogGrain` for each project + M grain calls to `WorkflowGrain.AssignRunnerAsync` for each waiting work + 3 grain calls per `IsWorkRunnableAsync` check. For a busy system, removing the polling-driven path would let the bus be the trigger.
  - **Reentrancy**: the runner is `[Reentrant]` (`RunnerGrain.cs:10`) precisely to allow the event-driven assignment to come in while a poll is in flight. If you switched to event-driven, the `[Reentrant]` attribute could potentially be removed.
  - **Backwards compatibility**: the polling endpoints are REST and external — the runner process is in `packages/runner/`. If you make events the primary path, the runner still needs `/poll` for liveness heartbeats and for crash recovery (re-enumerate assigned work after restart). Don't remove it; just demote it to a recovery probe.
  - **Test impact**: `tests/.../Runner/Grain/RunnerBindingSpecs.cs` and `RunnerFailureSpecs.cs` exercise the poll path. If you add an event subscription, you'd want a spec that asserts a `WorkReady` event causes `_works` to gain an entry without any poll.

### 6.8 `IssueGrain.StartWorkflow` → `WorkflowGrain.StartAsync`

- **Current path**: direct call inside `IssueGrain.StartWorkAsync` (`IssueGrain.cs:122-127`). The call is awaited, so the HTTP request waits for the workflow to start. After the call, the issue grain saves itself (`IssueGrain.cs:129`).
- **Alternative (event-driven)**: `IssueGrain` emits `IssueStartRequested(wrId, projectId, issueId, …)`; a `WorkflowGrain.OnActivateAsync` (or a subscriber) picks it up and calls `StartAsync` itself. Or `IssueGrain` doesn't even create a `wrId`; a workflow is created lazily when the first work is needed.
- **Trade-offs**:
  - **Latency**: better with the direct call (the response includes the `wrId` so the UI can navigate to the workflow). With an event, the issue would return with a placeholder, then the workflow would appear later.
  - **Testability**: the direct call is simple to test (issue.create → workflow.start with a known id). The event version needs a synchronous event bus (the in-process bus already is) plus an orchestration step that resolves the `wrId`.
  - **Error propagation**: today, if the workflow's `StartAsync` throws (e.g., `MissingPromptsException` at `IssueGrain.cs:36-39`), the exception bubbles back to the HTTP handler and the issue is left in a half-state (`StartWorkflow` was called on the domain, the issue moved to `InProgress`, but the workflow grain never `StartAsync`'d). The catch only catches `MissingPromptsException` and `InvalidOperationException`. With an event, the same half-state risk exists, but you could add compensating actions (issue stays in `Backlog` if workflow start fails).
  - **Verdict**: **keep direct**. The request/response is part of the user action.

### 6.9 User approves a stage → `WorkflowGrain.ApproveAsync` → next stage dispatch

- **Current path**: REST `POST …/approve` → `IWorkflowGrain.ApproveAsync` (`Api/IssueRoutes.WorkflowControl.cs:37` → `WorkflowGrain.ApproveAsync` at `WorkflowGrain.cs:160-166`). Inside `ApproveAsync`, the run advances via `_run.Approve()` (a domain call) and the resulting `StageApprovalResolved` event triggers `OnApprovalResolvedAsync` → `OnApprovalApprovedAsync` (`WorkflowGrain.cs:973-987`) which emits `stage_changed` and `EnsureWorkHeartbeatAsync`. The next-stage dispatch happens inside the same grain activation in `EnsureWorkHeartbeatAsync` → `RunCoreAsync` → `AssignRunnerWorkAsync` / `RegisterToBacklogAsync`.
- **Alternative (event-driven)**: same as today, just route through the bus instead of the in-grain `On` switch. No external subscriber needed.
- **Trade-offs**:
  - **Latency**: identical.
  - **Correctness**: identical (the in-grain `On` switch is already an in-process event reactor).
  - **Verdict**: **keep direct**. The user-action → grain-method → run-state-advance → schedule-next-work is a single request, and breaking it into bus messages would only add ceremony.

### 6.10 Workflow stage auto-advances → Web UI sees the new state

- **Current path**:
  1. `WorkflowGrain` saves the run state in `WorkflowRunStore.SaveAsync` (`WorkflowRunStore.cs:43-60`).
  2. After the transaction commits, `Publish(stagedEvents)` (`WorkflowRunStore.cs:53`) calls `_eventBus.Emit(dto.Type, dto)` for every persisted `WorkflowEvent`.
  3. `EventBridge` (`Events/Hub/EventBridge.cs:34`) forwards each event to the SignalR group `project:{projectId}`.
  4. The web UI receives the event and updates the board.
- Additionally, `WorkflowGrain.EmitStageChanged` (`WorkflowGrain.cs:888-900`) emits a second, higher-level `"stage_changed"` event with a friendlier payload (`StageChangedEvent`), and `EventBridge` forwards that too.
- So today, **the UI is *not* polling**; it's listening. The polling you see in the routes is for read-after-write (e.g., `IssueRoutes.Lifecycle.cs:33` returns `Ok()` then the UI refreshes from the bus event).
- **Trade-offs**:
  - The web UI does poll some things: `AgentRoutes.Status` and `AgentRoutes.Activity` are fetched on tab load (no SignalR mirror for those). But for the workflow run itself, the bus is the source of truth.

---

## 7. The "should be event-driven" list (prioritized)

These are the top 5–10 couplings to consider converting. Each is rated by blast radius (low / med / high) and benefit.

### 7.1 `WorkflowRunCompleted` → `IssueGrain.CompleteWorkAsync`
- **Current**: `IWorkflowCompletionHook` injected into `WorkflowGrain`, called synchronously from `OnWorkflowCompletedAsync` (`WorkflowGrain.cs:995-1012`) → `IssueWorkflowCompletionHook.OnCompletedAsync` (`IssueWorkflowCompletionHook.cs:24-30`).
- **Proposed**: replace the `IWorkflowCompletionHook` chain with an in-process subscription: `IssueGrain` registers `_eventBus.On("WorkflowRunCompleted", data => { … })` in `OnActivateAsync`.
- **Subscriber**: `IssueGrain` (singleton per issue). The event already fires via `WorkflowRunStore.Publish` (`WorkflowRunStore.cs:102-108`).
- **Pros**: removes the indirection; makes the trigger visible at the point of consumption; lets the same event drive other future consumers (e.g., a Slack notifier, a metrics emitter) without code changes in `WorkflowGrain`.
- **Cons**: subscriber error handling becomes your problem; today the hook loop catches per-hook (`WorkflowGrain.cs:1003-1010`). The bus's `Emit` catches per-handler (`InMemoryEventBus.cs:46-53`), so the safety is the same.
- **Blast radius**: medium (touches `IssueWorkflowCompletionHook`, `MohistServiceRegistration.cs:77`, the spec at `IssueCreationSpecs.cs:382` and `EpicLifecycleSpecs.cs:276` which call `CompleteWorkAsync` directly — these specs *should* still work because the spec calls the grain method, not the hook).
- **Verdict**: **yes, do it**.

### 7.2 `WorkflowRunStopped` → `IssueGrain.MarkCancelledAsync` (new)
- **Current**: no path. `IssueGrain.CancelAsync` sets `_issue.Close()` directly (`IssueGrain.cs:168`) and *then* calls `wfGrain.StopAsync` (`IssueGrain.cs:165`). Only the `/close` endpoint triggers this.
- **Proposed**: `IssueGrain` subscribes to `WorkflowRunStopped`; on event, calls `_issue.Close()` if `ActiveWorkflowRunId == workflowRunId`. The explicit `CancelAsync` path remains for the user-driven case but becomes a thin wrapper that calls `wfGrain.StopAsync` and lets the event do the rest.
- **Pros**: any code path that stops a workflow (today: `/stop` route, `CancelAsync` itself, future "system stops the workflow because X") will get the issue side-effect for free.
- **Cons**: idempotency — `Issue.Close` throws on `Done`/`ArchivedAt != null`. The subscriber would need a guard. The current `CancelAsync` flow doesn't have this guard because it short-circuits on `_activeWorkflowRunId` (only acts if the issue has an active workflow).
- **Blast radius**: low (new method, no existing specs to break).
- **Verdict**: **yes, do it** — but add `IssueStatus.Failed` while you're at it, to satisfy 7.3.

### 7.3 `WorkflowRunFailed` → `IssueGrain.MarkFailedAsync` (new)
- **Current**: no path. The issue stays in `InProgress` forever, the UI shows "blocked" via the projection.
- **Proposed**: `IssueGrain` subscribes to `WorkflowRunFailed`; sets `IssueStatus.Failed` (new enum value). The UI projection updates. Archived/Done checks need updating.
- **Pros**: consistent with 7.2.
- **Cons**: requires the `IssueStatus.Failed` value, schema migration of the JSON-serialized `Issue.State` column (or a default-to-Failed fallback), and a UI story.
- **Blast radius**: medium (touches `IssueStatus.cs`, `Issue.Transitions.cs`, `MohistDefaultWorkflowProjection.cs`, the read model DTO, the web UI).
- **Verdict**: **yes, but defer** until the team decides whether "failed issue" deserves first-class state or whether "blocked health" is enough.

### 7.4 `RunnerOffline` / `RunnerHeartbeatTimeout` → multiple subscribers
- **Current**: `RunnerGrain.HandleTimeoutAsync` (`RunnerGrain.cs:214-220`) only unregisters from the registry. The workflow grain and the agent session grain never find out.
- **Proposed**: `RunnerGrain` emits `RunnerOffline(runnerId)`; the workflow grain subscribes and (a) clears its claim if `_lastRunnerId == runnerId` or the lease's `RunnerId == runnerId`, (b) optionally re-enqueues to the backlog. The agent session grain subscribes and calls `FailIfRunningAsync` for any session whose `Runtime.RunnerId == runnerId`.
- **Pros**: closes the "runner crash leaves work stuck" hole that the current `RunnerFailureSpecs.cs:75-100` test is locking in.
- **Cons**: this is a *behaviour change*. The current test `RegisteredButUnavailableLeaseOwner_RemainsRecoveryBlocked` would need to be replaced with one that asserts the new recovery semantics.
- **Blast radius**: high (touches every "did the workflow do something recently" check, every poll path).
- **Verdict**: **defer to a design spike**. The current "let the user decide" stance is defensible; switching to automatic recovery is a product decision.

### 7.5 `WorkflowGrain` task ready → `RunnerGrain` (replace polling)
- **Current**: runner calls `POST /api/runner/{id}/poll` to fetch work. The poll walks every project, calls `IWorkflowBacklogGrain.ClaimAsync` for each, then `IWorkflowGrain.AssignRunnerAsync` for each waiting work. See §2 chain 6.
- **Proposed**: `WorkflowGrain` emits `WorkReady(runnerId, dispatch)` after `AssignRunnerWorkAsync`. `RunnerGrain` subscribes and pushes into `_works`. Polling becomes a recovery probe (re-enumerate after restart) only.
- **Pros**: removes the N×M grain-call amplification in the poll path. The runner can react to a single event.
- **Cons**: requires the runner process to be reliably listening. Today, the runner is a separate process (`packages/runner/`) that talks to the server over HTTP. It does *not* have a SignalR connection to the server; it polls. Switching to "the server pushes" would mean adding a server-pushes-to-runner channel (SignalR, gRPC stream, or long-polling). The runner is the one that knows when it's free, so the bus-as-trigger model would need a "I'm free, give me work" signal from the runner, which is the current poll — just inverted.
- **Blast radius**: high (touches `packages/runner/` and the runner's network model).
- **Verdict**: **probably not**, unless the runner architecture is being changed for other reasons (e.g., to support long-lived WebSocket connections).

### 7.6 `WorkflowBacklogGrain` claim → push to runner
- **Current**: `WorkflowBacklogGrain.ClaimAsync` synchronously walks `_waiting` and calls `IWorkflowGrain.AssignRunnerAsync` (`WorkflowBacklogGrain.cs:63-64`). Up to N×M grain hops per call.
- **Proposed**: keep `EnqueueAsync` synchronous (it's a single write), but replace the `ClaimAsync` call from `RunnerGrain` with a bus subscription: `RunnerGrain` subscribes to `WorkflowEnqueued(projectId)`; the subscription is filtered by `RunnerInfo.ProjectId` or "all projects". The runner then drives the assignment itself.
- **Pros**: removes the hot loop inside `ClaimAsync`. Decouples the backlog grain from any individual runner.
- **Cons**: the current "first runner to ask, gets the work" semantics would change to "every subscriber gets notified, first to call back wins" — which is the same in effect, but harder to test. Plus, the `WorkflowBacklogGrain` state is what lets a runner restart pick up the same work; that requires persistent subscriptions or a re-poll on restart.
- **Blast radius**: medium.
- **Verdict**: **defer**. The current synchronous claim is simple to reason about and the optimization is marginal.

### 7.7 `IssueLifecycle` events (start/close/reopen) → `WorkflowGrain` reaction for telemetry
- **Current**: `IssueGrain` calls `IWorkflowGrain.StartAsync` / `StopAsync` directly. There's no machine-readable audit of the trigger; only logs.
- **Proposed**: emit `"issue_workflow_started"` / `"issue_workflow_stopped"` etc. with `issueId`, `wrId`, `reason`. The bus already supports this pattern; `IssueGrain` would just call `_eventBus.Emit(...)` after the grain call.
- **Pros**: free observability for the web UI / future consumers.
- **Cons**: the bus events are 1:1 with the grain call; no functional change. Mostly noise.
- **Verdict**: **no** unless there's a real consumer.

### 7.8 `RunnerRegistry` changes → invalidate agent status projection
- **Current**: every `GET /api/projects/{ref}/agent/status` re-reads the registry (`Api/AgentRoutes.cs:43-44`) and calls `IRunnerGrain.IsAvailableAsync` for every runner (`Api/AgentRoutes.cs:53`). Same for `/runners` and `/opencode/models`.
- **Proposed**: `RunnerGrain` emits `RunnerRegistered` / `RunnerUnregistered`; a singleton service (or a hosted `BackgroundService`) subscribes and caches the merged eligible-runner list. Reads are served from the cache; the bus is the invalidation.
- **Pros**: removes the per-request fan-out, which is the dominant cost on busy projects.
- **Cons**: cache invalidation adds complexity; the current "always re-read" model is correct-by-construction.
- **Blast radius**: medium.
- **Verdict**: **defer** until profiling shows this is a real bottleneck.

### 7.9 `AgentSessionTerminal` event → `WorkflowGrain` task outcome
- **Current**: agent session terminal events are observational only. Task outcome is driven by the runner's `/report` endpoint.
- **Proposed**: have `AgentSessionGrain` emit `"coder_session_failed"` / `"coder_session_cancelled"` and have the workflow grain subscribe as a backstop in case the runner never reports.
- **Pros**: defense in depth — if the runner process is killed between "agent finished" and "report sent", the workflow won't hang.
- **Cons**: the runner is supposed to be the source of truth. If both the runner and the session report, you have a race. Today, the runner wins (because it reports the task result); the session is observational.
- **Blast radius**: medium-high.
- **Verdict**: **no** — keep the runner as the single writer. The right fix is 7.4 (runner death → fail the in-flight task), not a second writer.

### 7.10 `WorkflowRunApprovalRequested` → `IssueGrain` projection
- **Current**: `WorkflowGrain` does not emit `"approval_requested"` (it's in the `EventBusEventTypes.All` list at `EventBusEventTypes.cs:13` but no one emits it; the comment in `AGENTS.md` says it's "dead registration").
- **Proposed**: have `WorkflowGrain` emit it from `On(StageApprovalRequested, …)`. The UI already listens for it; an `IssueGrain` subscriber would let the issue status reflect "awaiting approval" in the persisted row instead of being projected on every read.
- **Pros**: makes the `IssueStatus` row authoritative for "waiting on human", which simplifies the UI logic.
- **Cons**: requires either (a) making `Issue.Status` richer (e.g., `AwaitingApproval`) or (b) a new field. Either is a migration.
- **Blast radius**: medium.
- **Verdict**: **defer** — the current projection-on-read model is correct, just inefficient.

---

## 8. The "should stay direct" list

These are the couplings that should remain synchronous method calls. Each is part of a request/response or a request-time invariant.

| Coupling | Why direct | File:line |
|---|---|---|
| `IssueGrain.CancelAsync` → `IWorkflowGrain.StopAsync` | User-action request/response. The user clicked "close"; the issue should reflect that synchronously. The event-driven alternative (7.2) replaces the *WorkflowGrain → IssueGrain* direction, not this one. | `IssueGrain.cs:165-166` |
| `IssueGrain.StartWorkAsync` → `IWorkflowGrain.StartAsync` | The HTTP response includes the `wrId`; the workflow start must complete before the response. | `IssueGrain.cs:122-127` |
| `IssueGrain` → `IProjectGrain.GetAsync` (3× in `StartWorkAsync`) | Read-side fan-out. The issue's start depends on resolving the project's default repo. Same for `ResolveRepositoryRefAsync`. | `IssueGrain.cs:67, 74, 105` |
| `RunnerGrain.ReportResultAsync` → `IWorkflowGrain.ReportResultAsync` | The runner is the source of truth for task outcome; the report is a single user-action-like call. | `RunnerGrain.cs:172-173` |
| `RunnerGrain.PollAsync` → `IWorkflowBacklogGrain.ClaimAsync` (×N projects) | Recovery probe. The runner needs to know if there's work for it; today polling is the only way. | `RunnerGrain.cs:264-265` |
| `WorkflowGrain` → `IWorkflowStageLockGrain.{Acquire,Release}Async` | Synchronous resource acquisition. The lock's `NextWorkflowRunId` is consumed in the same call site. | `WorkflowGrain.cs:422, 462` |
| `WorkflowGrain` → `IWorkflowBacklogGrain.EnqueueAsync` | Write-side single-row append. No fan-out; no need for a bus. | `WorkflowGrain.cs:489, 497` |
| `WorkflowGrain` → `IRunnerGrain.AssignWorkAsync` | Direct request/response; the assignment result determines whether the workflow re-schedules. | `WorkflowGrain.cs:665-666` |
| `WorkflowBacklogGrain.ClaimAsync` → `IWorkflowGrain.AssignRunnerAsync` | The "did this assignment succeed?" return value is consumed immediately. | `WorkflowBacklogGrain.cs:63-64` |
| All `/api/runner/{id}/*` routes (register, heartbeat, unregister) | Direct device-control endpoints; the runner is a remote device. | `RunnerRoutes.cs:17, 30, 37, 51, 55` |
| All `/api/projects/.../issues/{n}/{action}` routes | User action. The route resolves to a single grain method, returns 200. | `IssueRoutes.*.cs` |
| All `/api/projects/.../epics/...` routes | User action. | `EpicRoutes.cs` |
| All `/api/projects/...` routes (Project CRUD) | User action. | `ProjectRoutes.cs` |

The general rule: **the coupling between an API request and a single grain method should stay direct**. The couplings that are candidates for event-driven are the ones that cross grain boundaries in response to a state change (i.e., workflow done → issue done, or runner died → session failed), not the ones that initiate a user action.

---

## 9. Test impact

| Direct call | Test that exercises it | What changes if converted to event |
|---|---|---|
| `IssueWorkflowCompletionHook.OnCompletedAsync` → `IIssueGrain.CompleteWorkAsync` | `IssueCreationSpecs.cs:382` (`CompletedPrerequisite_AllowsDependentIssueToStart`) and `EpicLifecycleSpecs.cs:276` (`CompleteIssueAsync`) — both call `CompleteWorkAsync` directly. | The spec would emit a `WorkflowRunCompleted` event instead. But the spec is testing the *behaviour* of the issue, not the trigger, so a direct call to `CompleteWorkAsync` is fine even after the conversion. **No change needed** as long as the grain method is still public. |
| `IssueGrain.CancelAsync` → `IWorkflowGrain.StopAsync` | (none directly — covered via the `/close` route spec) | The event-driven version adds `MarkCancelledAsync` to `IssueGrain`; the spec would emit a `WorkflowRunStopped` event and assert the issue moves to `Cancelled`. The existing `/close` spec would still pass because it goes through the route. |
| `IssueGrain` → `IProjectGrain.GetAsync` | (no spec) | If the bus replaced this, every Issue spec would need an in-memory bus fixture that emits `ProjectResolved`. Not worth it. |
| `WorkflowGrain.On` reaction for `WorkflowRunCompleted` → `DispatchCompletedHooksAsync` | (no spec exercises the hook directly; `grep "IWorkflowCompletionHook" packages/server/tests` returns no matches) | The conversion to an event subscriber would let you write a spec that emits the event and asserts the issue transitions. **This is a new test, not a converted one.** |
| `WorkflowGrain.On` reaction for `StageApprovalRequested` | `ApprovalGateSpecs.cs` exercises the full stage lifecycle through the grain method. | The event is a notification, not a state transition; the spec would not need to change. |
| `RunnerGrain.HandleTimeoutAsync` | `RunnerFailureSpecs.cs:75-100` — `RegisteredButUnavailableLeaseOwner_RemainsRecoveryBlocked` deliberately asserts the workflow stays blocked. | **The most affected test.** The "stay blocked" behaviour would change to "auto-fail" if 7.4 is implemented. The test name itself says this is intentional. |
| `RunnerGrain.ReportResultAsync` → `IWorkflowGrain.ReportResultAsync` | `RunnerRoutes` test in `Issue/Api/IssueWorkflowProductLoopSpecs.cs:378` and `Issue/Profile/IssueWorkflowProfileApiSpecs.cs:425` use `POST /api/runner/{id}/report`; `BacklogSpecs.cs:203, 208, 243, 267, 274, 279, 316` call `workflow.ReportResultAsync` directly. | All these specs assert behaviour of the workflow given a report. The report path is direct request/response; converting to event would mean the test emits an `agent_session_terminal` event and the workflow subscribes. Most of these specs are about the workflow's reaction to a result, not about the report mechanism. **No change needed** if the grain method stays public. |
| `RunnerGrain.PollAsync` | `RunnerBindingSpecs.cs`, `RunnerFailureSpecs.cs:17-37, 42-53, 58-71` all exercise the poll path. | The poll path stays. If 7.5 is implemented (event-driven dispatch), the poll becomes a recovery probe and a new spec would assert the event-driven path. **Existing tests still pass.** |
| `WorkflowBacklogGrain.ClaimAsync` | `BacklogSpecs.cs` (the entire file). | If 7.6 is implemented, the spec would need an in-memory bus. The current test is integration-style and exercises the grain method directly, so it would still work as a unit test of the grain's claim logic. |
| `EventBridge` forwarding to SignalR | `EventBridgeSpecs.cs:15-37, 41-56`. | Independent of any of the above. The conversion doesn't change the bridge. |

**Summary**: the only test that would *break* with a 7.4-style change is `RegisteredButUnavailableLeaseOwner_RemainsRecoveryBlocked`. The other tests are stable across all the proposed conversions.

---

## 10. Summary

**Today's coupling health**:
- **The event bus is a one-way broadcast to the web UI.** `EventBridge` is its only in-process subscriber; no grain reacts to any bus event. The bus does not coordinate grain behaviour.
- **Cross-grain coordination is via direct `GrainFactory.GetGrain<...>(...).Method()` calls.** Every cross-grain boundary is a synchronous method invocation. The single in-process "event reactor" is `WorkflowGrain.On(WorkflowEvent e, …)`, which is a switch statement in the same grain — not a bus subscription.
- **The `IWorkflowCompletionHook` is the only "subscriber" abstraction that exists** — a `IEnumerable<...>` injected into `WorkflowGrain` and called synchronously from `OnWorkflowCompletedAsync`. It's used to transition the issue to `Done` and clean up the worktree.
- **DB-as-eventbus is rampant** for read-side coordination: `WorkflowRun.State`, `WorkflowLeases`, `AgentSessions`, `AgentSessionRuntimeEvents`, `Events`, and the in-memory `RunnerRegistryGrain._runners` and `RunnerConnectionTracker._connections` are all re-read on every relevant request, with no push invalidation.
- **Some cross-grain couplings are missing entirely**, not just weak:
  - `WorkflowRunStopped` → no automatic `Issue.Status = Cancelled` (the inverse direction is direct but the forward direction doesn't exist outside of `CancelAsync`).
  - `WorkflowRunFailed` → no path at all; the issue stays in `InProgress` and the UI shows "blocked" via projection.
  - `RunnerOffline` → the workflow and the agent session are never notified; the workflow stays in a "runner not coming back" state until the user intervenes (`RunnerFailureSpecs.cs:75-100` asserts this).

**What a fully event-driven design would look like**:
1. Every `WorkflowEvent` produced by `WorkflowRun` is published to the bus.
2. The `IEventBus` becomes the sole coordination mechanism. Grains subscribe in `OnActivateAsync` to the events they care about.
3. `WorkflowGrain`'s in-grain `On` switch is dissolved; each reaction becomes a subscription.
4. The `IWorkflowCompletionHook` is dissolved; `IssueGrain` subscribes to `WorkflowRunCompleted` / `WorkflowRunStopped` / `WorkflowRunFailed` directly.
5. `RunnerGrain` emits `RunnerOffline`; `WorkflowGrain` and `AgentSessionGrain` subscribe.
6. The user-action request handlers stay direct (one API call → one grain method → one response), but the second-order effects (issue close on workflow stop, issue fail on workflow fail, session fail on runner die) become bus-driven.
7. The DB writes (lease, run state, session projection) remain the source of truth for reads; the bus is the source of truth for *coordination* (i.e., "X happened, Y needs to react").
8. Read-side projections that today re-read the DB on every request (`IssueQuerier`, `AgentSessionQuerier`, `WorkflowQuerier`) keep doing so, but a per-grain cache (hydrated by the bus) could be added as an optimization.

**Realistic recommendation**: ship 7.1 and 7.2 (the `WorkflowRunCompleted` and `WorkflowRunStopped` → `IssueGrain` event subscriptions, replacing the `IWorkflowCompletionHook` mechanism). 7.3 is a natural follow-up. 7.4 (runner offline) deserves a design spike. The rest are premature given the runner's pull-based architecture.

**Files that would be touched by the recommended changes**:
- Delete: `Issue/Services/WorkflowProfiles/IssueWorkflowCompletionHook.cs` (replaced by event subscription in `IssueGrain`).
- Modify: `Issue/Grains/IssueGrain.cs` — add `OnActivateAsync` `_eventBus.On("WorkflowRunCompleted", …)` and `On("WorkflowRunStopped", …)` subscriptions; remove nothing.
- Modify: `Infrastructure/Hosting/MohistServiceRegistration.cs:77` — remove the `AddSingleton<IWorkflowCompletionHook, IssueWorkflowCompletionHook>()` line.
- Modify: `Workflow/Grains/WorkflowGrain.cs:36, 46, 55, 995-1012` — remove `_completionHooks` field, constructor parameter, and `DispatchCompletedHooksAsync` call in `OnWorkflowCompletedAsync`. The "post-commit reaction" still happens because the bus subscription on `IssueGrain` will fire when `WorkflowRunStore.Publish` emits the `WorkflowRunCompleted` event.
- Modify (for 7.3): `Issue/Domain/IssueStatus.cs` — add `Failed`. `Issue/Domain/Issue.Transitions.cs` — add `MarkFailed`. `Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs` — handle the new status.
- New test files (for 7.1, 7.2): a spec that creates a workflow, lets it run to completion, and asserts the issue's persisted status. Currently no spec exists for the `IWorkflowCompletionHook` chain (`grep` returns zero); the new spec would close that gap.
- The `IssueCreationSpecs.cs:382` and `EpicLifecycleSpecs.cs:276` direct calls to `CompleteWorkAsync` stay; they're testing the issue side-effect, not the trigger.

**One observation that cuts across the design**: the `IEventBus` is registered as a singleton (`MohistServiceRegistration.cs:86`). That means all bus subscribers see all events for the lifetime of the Silo, with no isolation per project. The current `EventBridge` correctly filters by `ProjectId` in its `ExtractProjectId` helper (`EventBridge.cs:52-68`); any future grain subscription (7.1, 7.2, 7.3) will need the same per-`WorkflowRunId` / per-`IssueId` filtering inside the handler, because the bus is a single shared topic per event name.
