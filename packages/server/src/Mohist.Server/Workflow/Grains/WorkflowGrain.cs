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
public class WorkflowGrain : Grain, IWorkflowGrain, IWorkflowGrainContext
{
    private WorkflowRun? _run;
    /// <summary>
    /// Non-authoritative runner cache for recovery/reconciliation. Assignment
    /// remains the only source of truth for active ownership.
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

    private string GrainKey => this.GetPrimaryKeyString();

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

    WorkflowRun? IWorkflowGrainContext.RunOrNull => _run;
    string IWorkflowGrainContext.GrainKey => GrainKey;
    WorkflowProfileManager IWorkflowGrainContext.ProfileManager => _profileManager;
    IGrainFactory IWorkflowGrainContext.Grains => GrainFactory;
    WorkflowSessionHealthService IWorkflowGrainContext.SessionHealthGate => _sessionHealth;
    ILogger IWorkflowGrainContext.Log => _log;
    void IWorkflowGrainContext.SetLastKnownRunnerId(string? runnerId) => _lastKnownRunnerId = runnerId;
    Task IWorkflowGrainContext.SaveAsync() => SaveRunAsync();
    Task IWorkflowGrainContext.SaveAsyncWithEvents(IReadOnlyList<WorkflowEvent> events) => SaveRunAsync(events);
    Task IWorkflowGrainContext.DispatchEvent(WorkflowEvent e) => On(e);
    Task IWorkflowGrainContext.ReleaseCurrentStageLocks(string reason) =>
        _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);
    string IWorkflowGrainContext.GetProjectId() => GetProjectId();
    string? IWorkflowGrainContext.GetIssueId() => GetIssueId();
    string? IWorkflowGrainContext.GetIssueNumber() => GetIssueNumber();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);

        // Old stopped runs may still carry an awaiting-approval gate; repair
        // only that terminal shape so live approval gates are untouched.
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

        // Fail any in-flight task after the run is terminal.
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

        // A healthy gate means a previous context-exhaustion failure is no
        // longer sticky; retry can use the normal task path again.
        if (failure?.Reason == FailureReason.ContextExhaustion
            && _run.ClearContextExhaustionFailure())
        {
            _log.LogInformation(
                "Workflow {Id} retry: session context recovered; demoting ContextExhaustion failure to TaskFailed (task={TaskId}, stage={Stage})",
                GrainKey, failedTaskId ?? "(none)", stageId ?? "(none)");
        }

        await ReleaseCurrentStageLocksAsync("retried");
        var events = _run.Retry();
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

        var work = _run.NextWork();
        if (work is null)
            return null;

        var item = _outcomeProcessor.BuildWorkItem(_run!, work);
        if (item?.Id is null)
            return null;
        var workId = item.Id;

        // Lock and start in the same claim path; a contended lock leaves no
        // workflow-owned lock to roll back.
        var stage = _run.CurrentStageId;
        if (stage is not null && !await AcquireStageLocksIfNeededAsync(stage))
            return null;

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
        // Stale is still an ack; late or duplicate reports are normal under
        // at-least-once delivery. See design/workflow/scheduling.md Report.
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
    /// Interface entrypoint for bus-driven sequential stage lock release.
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
            WorkflowArtifactRecorded => Task.CompletedTask,
        };

    private Task OnWorkflowStoppedAsync()
    {
        // Stopped side effects are owned by event subscribers.
        return Task.CompletedTask;
    }

    internal string GetProjectId() =>
        _run?.Metadata?.Annotations?.TryGetValue("projectId", out var v) == true ? v : "";

    internal string? GetIssueId() =>
        _run?.Metadata?.Annotations?.TryGetValue("issueId", out var v) == true ? v : null;

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
