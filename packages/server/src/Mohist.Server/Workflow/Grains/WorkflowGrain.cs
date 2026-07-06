using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;

using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

[Reentrant]
public class WorkflowGrain : Grain, IWorkflowGrain
{
    private WorkflowRun? _run;
    /// <summary>
    /// Non-authoritative cache of the most recent runner identity, retained for
    /// recovery/reconciliation when the authoritative <see cref="WorkflowRun.Assignment"/> is absent.
    /// This is grain infrastructure state, NOT part of the assignment domain model, and does NOT
    /// represent an active assignment. The authoritative runner identity is always
    /// <c>_run.Assignment.RunnerId</c>. When no <see cref="WorkflowRun.Assignment"/> exists,
    /// <see cref="WorkflowRun.IsAssigned"/> remains <c>false</c> regardless of this field's value.
    /// </summary>
    private string? _lastKnownRunnerId;
    private bool _runDirty;
    private readonly IWorkflowRunStore _runStore;
    private readonly WorkflowProfileManager _profileManager;
    private readonly WorkflowSessionHealthService _sessionHealth;
    private readonly ILogger<WorkflowGrain> _log;
    private readonly WorkflowReadModel _readModel;
    private readonly WorkflowStageLockCoordinator _stageLockCoordinator;
    private readonly WorkflowStageInitializer _stageInitializer;
    private readonly WorkflowOutcomeProcessor _outcomeProcessor;

    /// <summary>
    /// Internal accessor exposing the in-memory run to grain-composed helpers
    /// (notably <see cref="WorkflowReadModel"/> and
    /// <see cref="WorkflowStageLockCoordinator"/>). Exposed as a property
    /// rather than a field so the helpers can stay grain-internal without
    /// forcing the field to become <c>internal</c>.
    /// </summary>
    internal WorkflowRun? RunOrNull => _run;

    /// <summary>
    /// Internal accessor exposing the grain's primary key string to
    /// grain-composed helpers (notably
    /// <see cref="WorkflowStageLockCoordinator"/>, which composes lock
    /// acquire/release requests against the key).
    /// </summary>
    internal string GrainKey => this.GetPrimaryKeyString();

    /// <summary>
    /// Internal accessor exposing the grain's <see cref="WorkflowProfileManager"/>
    /// to grain-composed helpers (notably
    /// <see cref="WorkflowStageLockCoordinator"/>, which resolves stage
    /// specs through it).
    /// </summary>
    internal WorkflowProfileManager ProfileManager => _profileManager;

    /// <summary>
    /// Internal accessor exposing the inherited <c>GrainFactory</c> to
    /// grain-composed helpers (notably
    /// <see cref="WorkflowStageLockCoordinator"/>, which resolves the
    /// <see cref="IWorkflowStageLockGrain"/> per (project, resource)). The
    /// Orleans base <c>Grain.GrainFactory</c> is <c>protected</c>, so the
    /// coordinator cannot read it directly.
    /// </summary>
    internal IGrainFactory GrainFactoryAccess => GrainFactory;

    /// <summary>
    /// Internal accessor exposing the grain's <see cref="WorkflowSessionHealthService"/>
    /// to <see cref="WorkflowOutcomeProcessor"/>, which forwards the
    /// session-health gate call from <c>MarkTaskRunningAsync</c>.
    /// </summary>
    internal WorkflowSessionHealthService SessionHealthGate => _sessionHealth;

    /// <summary>
    /// Internal accessor exposing the grain's <see cref="ILogger"/> to
    /// <see cref="WorkflowOutcomeProcessor"/>, which logs at the same
    /// level / category the grain used before the outcome cluster was
    /// extracted. The processor logs informational lines that were
    /// previously inline in the grain's outcome methods.
    /// </summary>
    internal ILogger<WorkflowGrain> Log => _log;

    /// <summary>
    /// Internal setter for <c>_lastKnownRunnerId</c>. The cache is
    /// grain infrastructure state (not part of the run aggregate), so the
    /// outcome processor writes it via this accessor instead of holding
    /// a reference to the field.
    /// </summary>
    internal void SetLastKnownRunnerId(string? runnerId) => _lastKnownRunnerId = runnerId;

    /// <summary>
    /// Internal save accessor used by <see cref="WorkflowOutcomeProcessor"/>.
    /// Persists the current run without publishing events; preserves the
    /// ETag conflict → <c>DeactivateOnIdle()</c> reload path.
    /// </summary>
    internal Task SaveAsync() => SaveRunAsync();

    /// <summary>
    /// Internal save accessor used by <see cref="WorkflowOutcomeProcessor"/>.
    /// Persists the current run with the given events (which the store
    /// also publishes to the bus). Preserves the ETag conflict →
    /// <c>DeactivateOnIdle()</c> reload path.
    /// </summary>
    internal Task SaveAsyncWithEvents(IReadOnlyList<WorkflowEvent> events) => SaveRunAsync(events);

    /// <summary>
    /// Internal event-dispatch accessor used by
    /// <see cref="WorkflowOutcomeProcessor"/> after
    /// <see cref="WorkflowRunExtensions.StartTask"/>. Forwards to the
    /// grain's <c>On()</c> dispatcher so any grain-side reactions
    /// observe the new event with the same semantics as the pre-extraction
    /// inline path.
    /// </summary>
    internal Task DispatchEvent(WorkflowEvent e) => On(e);

    /// <summary>
    /// Internal accessor exposing the grain's release-current-stage-lock
    /// path to <see cref="WorkflowOutcomeProcessor.ClearExecutableStateAsync"/>.
    /// Delegates to the T-001 <see cref="WorkflowStageLockCoordinator"/>
    /// so all lock-touching logic stays in one composed service.
    /// </summary>
    internal Task ReleaseCurrentStageLocks(string reason) =>
        _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        WorkflowProfileManager profileManager,
        WorkflowSessionHealthService sessionHealth,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _profileManager = profileManager;
        _sessionHealth = sessionHealth;
        _log = log;
        _readModel = new WorkflowReadModel(this);
        _stageLockCoordinator = new WorkflowStageLockCoordinator(this);
        _stageInitializer = new WorkflowStageInitializer(this);
        _outcomeProcessor = new WorkflowOutcomeProcessor(this);
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);

        // Self-heal persisted dirty state (#331-class): a run previously
        // stopped under the pre-fix code may have a Stopped run status with a
        // dangling awaiting-approval gate on its current stage. Stop() cannot
        // repair this (it throws on a terminal run), so we correct it on grain
        // reactivation. The reconcile is idempotent — repeated activations are
        // no-ops once the gate is cleared — and the Stopped-only scope keeps a
        // live run genuinely awaiting approval untouched.
        if (_run is not null && _run.Status == WorkflowRunStatus.Stopped && _run.ReconcileStoppedApprovalGate())
        {
            await _runStore.SaveAsync(_run, ct);
            _runDirty = false;
        }

        _lastKnownRunnerId = _run?.Assignment?.RunnerId;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        if (!_runDirty || _run is null) return;

        try
        {
            await _runStore.SaveAsync(_run, ct);
            _runDirty = false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Workflow {Id} flush on deactivation failed; in-memory mutations will be lost until next activation reloads state",
                GrainKey);
        }
    }

    public async Task StartAsync(WorkflowStartInput? input = null)
    {
        if (_run is null)
        {
            var metadata = input?.Metadata;
            var projectId = metadata?.Annotations?.GetValueOrDefault("projectId");
            var issueId = metadata?.Annotations?.GetValueOrDefault("issueId");
            var structure = await _profileManager.LoadStructureAsync(GrainKey, projectId, issueId);
            _run = WorkflowRun.Create(GrainKey, structure, metadata ?? BuildRunMetadata(null));
            _run.Workspace = input?.Workspace;
        }

        var events = _run.Start();

        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task ResumeAsync()
    {
        EnsureRun();
        var events = _run.Resume();
        _log.LogInformation("Workflow {Id} resumed, stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        var events = _run.Pause();
        _log.LogInformation("Workflow {Id} paused: {Reason}", GrainKey, reason);
        await CommitAsync(events, reason);
    }

    public async Task StopAsync(string? reason = null)
    {
        EnsureRun();

        if (_run.Status is not (WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running or WorkflowRunStatus.AwaitingApproval or WorkflowRunStatus.Paused))
            throw new InvalidOperationException($"Cannot stop workflow in {_run.Status} state");

        // Flip the run status to Stopped before clearing executable state so the
        // TaskFailed event handler observes a terminal status and short-circuits.
        _ = _run.Stop();

        await _outcomeProcessor.ClearExecutableStateAsync(_run!, reason ?? "stopped");

        await SaveRunAsync();

        _log.LogInformation("Workflow {Id} stopped: {Reason}", GrainKey, reason);
        await CommitAsync([new WorkflowRunStopped()], reason);
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        var events = _run.Approve();
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task<string> RequestChangesAsync(string body)
    {
        EnsureRun();
        var stage = _run.CurrentStage();
        var approval = await _profileManager.LoadApprovalConfigAsync(GrainKey);
        var config = approval?.Feedback?.Task;
        var feedbackTask = config is null
            ? WorkflowRunExtensions.BuildDefaultFeedbackTask(stage.Id)
            : WorkflowRunExtensions.ResolveFeedbackTask(config, stage.Id);
        var events = _run.RequestChanges(body, feedbackTask);
        var feedbackId = _run.Feedback.Last().Id;
        _log.LogInformation("Workflow {Id} requested changes at stage={Stage}: feedback={FeedbackId}", GrainKey, stage.Id, feedbackId);
        await CommitAsync(events);
        return feedbackId;
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        var failure = _run.Failure;
        var stageId = _run.CurrentStageId;
        var failedTaskId = failure?.TaskId
            ?? (stageId is not null
                ? _run.Stages.FirstOrDefault(s => s.Id == stageId)?.Tasks.LastOrDefault(t => t.Status == TaskRunStatus.Failed)?.Id
                : null);

        await _sessionHealth.CheckAndEnforceAsync(
            failedTaskId, stageId, GrainKey, _run,
            events => CommitAsync(events), "retry", default);

        // If a previous attempt was blocked by context exhaustion, the user
        // may have recovered the session (via compact/reset) by now. The gate
        // reports a healthy context, so demote the sticky failure reason
        // back to TaskFailed and let the regular retry path re-run the task.
        if (failure?.Reason == FailureReason.ContextExhaustion
            && _run.ClearContextExhaustionFailure())
        {
            _log.LogInformation(
                "Workflow {Id} retry: session context recovered; demoting ContextExhaustion failure to TaskFailed (task={TaskId}, stage={Stage})",
                GrainKey, failedTaskId ?? "(none)", stageId ?? "(none)");
        }

        await ReleaseCurrentStageLocksAsync("retried");
        var events = await _outcomeProcessor.TryScheduleRequestedCheckRepairAsync(_run!) ?? _run.Retry();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        await ReleaseCurrentStageLocksAsync("rerun");
        var events = _run.Rerun();
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task<WorkflowControlResult> RerunFromStageAsync(string stageId)
    {
        EnsureRun();
        IReadOnlyList<WorkflowEvent> events;
        try
        {
            events = _run.RerunFromStage(stageId);
        }
        catch (WorkflowControlRejectionException ex)
        {
            return WorkflowControlResult.Rejected(ex.Code, ex.Message, ex.DetailsJson());
        }

        var targetIdx = _run.Stages.FindIndex(s => s.Id == stageId);
        for (var i = targetIdx; i < _run.Stages.Count; i++)
            await ReleaseStageLocksAsync(_run.Stages[i].Id, "rerun-from-stage");
        _log.LogInformation("Workflow {Id} rerun-from-stage at stage={Stage}", GrainKey, stageId);
        await CommitAsync(events);
        return WorkflowControlResult.Ok();
    }

    public async Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Runtime task requires id");
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Runtime task requires title");

        var with = WorkflowDispatchHelpers.ParseWith(task.With);
        var events = _run.AddRuntimeTask(new TaskDefinition(task.Id, task.Title, task.Uses, with, Recovery: task.Recovery), task.Stage, task.InvalidateChecks);

        var stage = _run.CurrentStageId ?? "unknown";
        _log.LogInformation("Workflow {Id} added runtime task {TaskId} at stage={Stage}", GrainKey, task.Id, stage);

        await CommitAsync(events);
        return new RuntimeTaskAddedResult(GrainKey, stage, task.Id);
    }

    public Task<bool> HasIncompleteTaskWithUsesAsync(string uses)
    {
        EnsureRun();
        return Task.FromResult(_run.HasIncompleteTaskWithUses(uses));
    }

    public Task<bool> HasIncompleteTaskByIdAsync(string id)
    {
        EnsureRun();
        return Task.FromResult(_run.HasIncompleteTaskById(id));
    }

    public async Task<WorkflowAssignmentResult> AssignRunnerAsync(string runnerId)
    {
        if (_run is null) return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "missing");
        if (_run.Status.IsTerminal() || _run.Status is WorkflowRunStatus.Created or WorkflowRunStatus.Paused or WorkflowRunStatus.AwaitingApproval)
            return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "not-runnable");
        if (_run.Assignment is not null)
        {
            if (!string.Equals(_run.Assignment.RunnerId, runnerId, StringComparison.Ordinal))
                return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, _run.Assignment.RunnerId, "already-assigned");
            return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, runnerId);
        }
        if (_run.Status != WorkflowRunStatus.Pending || !_run.HasDispatchableWork())
            return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "not-runnable");
        if (_run.Assignment is null)
        {
            _run.AssignTo(runnerId, DateTimeOffset.UtcNow);
            _lastKnownRunnerId = runnerId;
            await SaveRunAsync();
        }

        return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, runnerId);
    }

    public async Task<WorkItem?> ClaimNextAsync(string runnerId)
    {
        if (_run is null || _run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running))
            return null;

        if (!_run.IsAssignedTo(runnerId))
            return null;

        // Pick the next dispatchable work. No work means the run has nothing
        // outstanding (idle / gated / between stages) — nothing to claim.
        var work = _run.NextWork();
        if (work is null)
            return null;

        // Project to a WorkItem up front to resolve the work id (a task's
        // definition id, or the deterministic checks-{stage} id) that the claim
        // and the eventual report key on. BuildWorkItem is a pure projection —
        // it does not mutate, so calling it before the claim is safe.
        var item = _outcomeProcessor.BuildWorkItem(_run!, work);
        if (item?.Id is null)
            return null;
        var workId = item.Id;

        // Acquire the sequential stage lock as part of the claim. A failed
        // claim (lock contended) must not leave the workflow holding a lock on
        // a stage it never started, so the lock is taken here, in the same
        // single write that starts the work — there is no separate offer phase
        // whose failure would need a rollback.
        var stage = _run.CurrentStageId;
        if (stage is not null && !await AcquireStageLocksIfNeededAsync(stage))
            return null;

        // Single atomic write: mark the work Running, persist, dispatch events.
        // ClaimWorkItemAsync is idempotent for an already-Running work (re-claim
        // after a lost dispatch response): it returns the in-flight work id
        // without re-transitioning. Returns null when the workId no longer maps
        // to offerable work (the run advanced between NextWork and the claim).
        var resolvedWorkId = await _outcomeProcessor.ClaimWorkItemAsync(
            _run!, workId, runnerId, events => CommitAsync(events));

        if (resolvedWorkId is null)
            return null;

        return item;
    }

    public async Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request)
    {
        EnsureRun();
        if (request.Tasks is null || request.Tasks.Count == 0)
            throw new InvalidOperationException("AddTasksBatchRequest requires at least one task");

        if (_run.CurrentStageId is null)
            throw new InvalidOperationException("Workflow has no current stage");

        var current = _run.CurrentStage();
        if (!current.Initialized)
            throw new InvalidOperationException("Cannot add tasks before stage is initialized");

        var tasksToInsert = new List<TaskDefinition>();
        foreach (var t in request.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Id))
                throw new InvalidOperationException("Task ID is required");
            if (string.IsNullOrWhiteSpace(t.Title))
                throw new InvalidOperationException("Task title is required");

            tasksToInsert.Add(new TaskDefinition(t.Id, t.Title, t.Uses, WorkflowDispatchHelpers.ParseWith(t.With)));
        }

        var events = _run.AddRuntimeTasks(tasksToInsert);

        _log.LogInformation("Workflow {Id} added {Count} tasks in stage {Stage}",
            GrainKey, tasksToInsert.Count, current.Id);

        await CommitAsync(events);

        return new AddTasksBatchResult(GrainKey, current.Id, tasksToInsert.Count);
    }

    public async Task<ReportAck> ReportTaskOutcomeAsync(string runnerId, string workId, TaskOutcome outcome)
    {
        // Stale (an ack, not an error) covers every "no longer current" case:
        // the run is gone, not assigned to this runner, or the workId no longer
        // maps to a Running task the runner owns (already terminal, superseded
        // by a rerun, advanced past). At-least-once reports make late/duplicate
        // reports normal; the runner retires the work from awaitingAck on either
        // Accepted or Stale. See design/workflow/scheduling.md §Report.
        if (_run is null || !_run.IsAssignedTo(runnerId)) return ReportAck.Stale;
        var stage = _run.CurrentStage();
        var activeTask = stage.FindRunningTaskByWork(workId, runnerId);
        if (activeTask is null)
            return ReportAck.Stale;

        _log.LogInformation("Workflow {Id} received task outcome for {WorkId}: {Status} detail={Detail}",
            GrainKey, workId, outcome.Status, outcome.Detail ?? "(none)");

        var events = await _outcomeProcessor.ProcessTaskOutcomeAsync(_run!, outcome, activeTask.Id, workId);

        await CommitAsync(events);
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> ReportCheckOutcomeAsync(string runnerId, string workId, CheckOutcome outcome)
    {
        if (_run is null || !_run.IsAssignedTo(runnerId)) return ReportAck.Stale;
        var currentStage = _run.CurrentStage();
        if (currentStage.ChecksWorkId is null || !string.Equals(currentStage.ChecksWorkId, workId, StringComparison.Ordinal))
            return ReportAck.Stale;

        _log.LogInformation("Workflow {Id} received check outcome for stage {Stage}: {Count} results",
            GrainKey, outcome.Stage, outcome.Results.Count);

        var events = await _outcomeProcessor.ProcessCheckOutcomeAsync(_run!, outcome);
        _outcomeProcessor.ResetChecksRunningState(_run!);

        await CommitAsync(events);
        return ReportAck.Accepted;
    }

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<string?> GetRunStatusAsync()
    {
        return Task.FromResult(_run?.Status.ToString());
    }

    public Task<bool> IsStoppedOrTerminalAsync()
    {
        if (_run is null) return Task.FromResult(true);
        return Task.FromResult(_run.IsTerminal());
    }

    public Task<string?> GetAssignedRunnerIdAsync()
    {
        return Task.FromResult(_run?.Assignment?.RunnerId ?? _lastKnownRunnerId);
    }

    public Task<string?> GetCurrentWorkIdAsync()
    {
        var stage = _run?.CurrentStage();
        if (stage is null) return Task.FromResult<string?>(null);
        return Task.FromResult(stage.RunningTask?.WorkId ?? stage.ChecksWorkId);
    }

    public Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId)
    {
        // Snapshot projection — delegated to the in-grain composed
        // WorkflowReadModel so the write path stays focused on the state
        // machine and read snapshots don't interleave with state transitions.
        return Task.FromResult(_readModel.GetActiveWork(workId));
    }

    public Task<WorkflowFeedbackRecord?> GetFeedbackAsync(string feedbackId)
    {
        EnsureRun();
        return Task.FromResult(_readModel.GetFeedback(feedbackId));
    }

    public Task<IReadOnlyList<WorkflowFeedbackRecord>> ListFeedbackAsync()
    {
        EnsureRun();
        return Task.FromResult(_readModel.ListFeedback());
    }

    private async Task<bool> AcquireStageLocksIfNeededAsync(string stage) =>
        await _stageLockCoordinator.AcquireStageLocksIfNeededAsync(stage);

    private Task ReleaseCurrentStageLocksAsync(string reason) =>
        _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);

    /// <summary>
    /// Releases the sequential stage lock owned by this workflow run for the
    /// given stage. Used by both the grain's retry/rerun/stop paths (via
    /// the composed <see cref="WorkflowStageLockCoordinator"/>) and by the
    /// bus-side <c>WorkflowStageLockReleaseHandler</c> that subscribes to
    /// <c>com.mohist.workflow.stage.{completed,failed}</c> events.
    ///
    /// The grain's <c>On()</c> dispatch used to call this synchronously after
    /// emitting a <see cref="StageCompleted"/>/<see cref="StageFailed"/>
    /// event; the lock release now flows through the event bus so the
    /// handler runs as part of the same in-process dispatch that
    /// <c>WorkflowRunStopped</c> already rides. Pull-scheduling (T-005
    /// cleanup D8) means a successful release no longer requires the
    /// previously-no-op <c>RequeueWorkflowIdAsync</c>: the next runner poll
    /// rediscovers the assignable workflow run from persisted state.
    ///
    /// The grain keeps this method as the <see cref="IWorkflowGrain"/>
    /// interface contract; the body delegates to the lock coordinator so
    /// the acquire/release implementation lives in one composed service.
    /// </summary>
    public Task ReleaseStageLocksAsync(string stage, string reason) =>
        _stageLockCoordinator.ReleaseStageLocksAsync(stage, reason);

    [MemberNotNull(nameof(_run))]
    private void EnsureRun()
    {
        if (_run is null)
            throw new InvalidOperationException($"Workflow '{GrainKey}' has no workflow run");
    }

    private async Task CommitAsync(IReadOnlyList<WorkflowEvent> events, string? reason = null, CancellationToken ct = default)
    {
        if (_run is not null)
        {
            var resolved = await _stageInitializer.InitializeFreshStagesAsync(events);
            _runDirty = true;
            await SaveRunAsync(resolved);
            events = resolved;
        }

        foreach (var e in events)
            await On(e, reason);
    }

    private Task On(WorkflowEvent e, string? reason = null) =>
        e switch
        {
            null => Task.CompletedTask,
            WorkflowRunStarted => Task.CompletedTask,
            WorkflowRunResumed => Task.CompletedTask,
            WorkflowRunPaused => Task.CompletedTask,
            WorkflowRunStopped => OnWorkflowStoppedAsync(),
            WorkflowRunFailed => Task.CompletedTask,
            WorkflowRunCompleted => Task.CompletedTask,
            StageStarted => Task.CompletedTask,
            StageCompleted => Task.CompletedTask,
            StageFailed => Task.CompletedTask,
            StageApprovalRequested => Task.CompletedTask,
            StageApprovalResolved x => Task.CompletedTask,
            FeedbackRequested => Task.CompletedTask,
            TaskStarted => Task.CompletedTask,
            TaskCompleted => Task.CompletedTask,
            TaskFailed => Task.CompletedTask,
            CheckPassed => Task.CompletedTask,
            CheckFailed => Task.CompletedTask,
            CheckPending => Task.CompletedTask,
            RepairScheduled => Task.CompletedTask,
            WorkflowArtifactRecorded => Task.CompletedTask,
        };

    private Task OnWorkflowStoppedAsync()
    {
        // Side effects now flow through the bus — IssueGrain subscribes
        // to com.mohist.workflow.run.stopped and the workspace cleanup
        // service subscribes to .completed. Sequential stage lock release
        // has likewise been migrated to a bus subscription (see
        // WorkflowStageLockReleaseHandler) so this grain no longer holds
        // any workflow-event-aware side effect that crosses the lock-grain
        // boundary.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Project id from the workflow run's metadata annotations. Used by the
    /// grain's lock coordinator and exposed to the in-grain composed
    /// <see cref="WorkflowReadModel"/>.
    /// </summary>
    internal string GetProjectId() =>
        _run?.Metadata?.Annotations?.TryGetValue("projectId", out var v) == true ? v : "";

    /// <summary>
    /// Issue id from the workflow run's metadata annotations. Exposed to the
    /// in-grain composed <see cref="WorkflowReadModel"/>.
    /// </summary>
    internal string? GetIssueId() =>
        _run?.Metadata?.Annotations?.TryGetValue("issueId", out var v) == true ? v : null;

    /// <summary>
    /// Issue number from the workflow run's metadata annotations. Exposed to
    /// the in-grain composed <see cref="WorkflowReadModel"/>.
    /// </summary>
    internal string? GetIssueNumber() =>
        _run?.Metadata?.Annotations?.TryGetValue("issueNumber", out var v) == true ? v : null;

    private WorkflowRunMetadata? BuildRunMetadata(WorkflowStartInput? input)
    {
        if (input is null) return null;
        return new WorkflowRunMetadata(input.Name, DateTimeOffset.UtcNow, input.Labels, input.Annotations);
    }

    private async Task SaveRunAsync()
    {
        if (_run is null) return;

        try
        {
            await _runStore.SaveAsync(_run);
            _runDirty = false;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _log.LogWarning(ex,
                "Workflow {Id} save failed because the persisted run ETag changed; deactivating grain to reload state",
                GrainKey);
            DeactivateOnIdle();
            throw;
        }
    }

    private async Task SaveRunAsync(IReadOnlyList<WorkflowEvent> events)
    {
        if (_run is null) return;

        try
        {
            await _runStore.SaveAsync(_run, events);
            _runDirty = false;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _log.LogWarning(ex,
                "Workflow {Id} save failed because the persisted run ETag changed; deactivating grain to reload state",
                GrainKey);
            DeactivateOnIdle();
            throw;
        }
    }
}
