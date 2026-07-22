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
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain : Grain, IWorkflowGrain, IWorkflowGrainContext
{
    private WorkflowRun? _run;
    private string? _cachedAssignedWorkerId;
    private bool _runDirty;
    private bool _runReloadRequired;
    private readonly IWorkflowRunStore _runStore;
    private readonly WorkflowProfileManager _profileManager;
    private readonly WorkflowRunProfileManager _runProfileManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowGrain> _log;
    private readonly WorkflowReadModel _readModel;
    private readonly WorkflowStageLockCoordinator _stageLockCoordinator;
    private readonly WorkflowStageInitializer _stageInitializer;
    private readonly WorkflowWorkLifecycle _workLifecycle;

    private string GrainKey => string.IsNullOrEmpty(GrainKeyForTest) ? this.GetPrimaryKeyString() : GrainKeyForTest;

    internal string GrainKeyForTest { get; set; } = string.Empty;

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        WorkflowProfileManager profileManager,
        WorkflowRunProfileManager runProfileManager,
        TimeProvider timeProvider,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _profileManager = profileManager;
        _runProfileManager = runProfileManager;
        _timeProvider = timeProvider;
        _log = log;
        _readModel = new WorkflowReadModel(this);
        _stageLockCoordinator = new WorkflowStageLockCoordinator(this);
        _stageInitializer = new WorkflowStageInitializer(this);
        _workLifecycle = new WorkflowWorkLifecycle(this);
    }

    WorkflowRun? IWorkflowGrainContext.RunOrNull => _run;
    string IWorkflowGrainContext.GrainKey => GrainKey;
    WorkflowProfileManager IWorkflowGrainContext.ProfileManager => _profileManager;
    IGrainFactory IWorkflowGrainContext.Grains => GrainFactory;
    ILogger IWorkflowGrainContext.Log => _log;
    DateTimeOffset IWorkflowGrainContext.Now() => Now();
    void IWorkflowGrainContext.CacheAssignedWorkerId(string? workerId) => _cachedAssignedWorkerId = workerId;
    Task IWorkflowGrainContext.SaveAsync() => SaveRunAsync();
    Task IWorkflowGrainContext.SaveAsyncWithEvents(IReadOnlyList<WorkflowEvent> events) => SaveRunAsync(events);
    Task IWorkflowGrainContext.DispatchEvent(WorkflowEvent e) => On(e);
    Task IWorkflowGrainContext.ReleaseCurrentStageLocks(string reason) =>
        _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);
    string IWorkflowGrainContext.GetProjectId() => GetProjectId();
    int? IWorkflowGrainContext.GetIssueNumber() => GetIssueNumber();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);
        _runReloadRequired = false;

        await ClearStoppedRunStaleApprovalGateAsync(ct);

        _cachedAssignedWorkerId = _run?.Assignment?.WorkerId;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        if (!_runDirty || _runReloadRequired || _run is null) return;

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
        RejectIfRunReloadRequired();
        await EnsureCreatedRunAsync(input);
        var events = _run!.Start(Now());

        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run!.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task EnsureStartedAsync(WorkflowIssueContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RejectIfRunReloadRequired();
        if (_run is not null)
        {
            await RefreshIssueContextAsync(context);
            return;
        }

        await EnsureCreatedRunAsync(context);
        var events = _run!.Start(Now());
        await CommitAsync(events);
    }

    /// <summary>
    /// issue-417 T-006 (D4): durable-event startup carrying an immutable
    /// repository/workspace snapshot captured at Issue transaction commit.
    /// The snapshot lands on the run via <see cref="WorkflowRun.Lifecycle"/>'s
    /// <c>EnsureStarted</c>, which is idempotent on identical replay and
    /// refuses conflicting context. When the run is already started with a
    /// matching snapshot this is a no-op; a null snapshot falls back to the
    /// context-only path.
    /// </summary>
    public async Task EnsureStartedAsync(WorkflowIssueContext context, WorkflowStartSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(context);
        RejectIfRunReloadRequired();
        if (snapshot is null)
        {
            await EnsureStartedAsync(context);
            return;
        }
        if (_run is null)
        {
            await EnsureCreatedRunAsync(context);
        }

        var metadata = _run!.Metadata;
        var events = _run.EnsureStarted(snapshot.Repository, snapshot.Workspace, Now(), metadata);
        if (events.Count > 0)
        {
            _log.LogInformation(
                "Workflow {Id} ensured-started with repository snapshot, stage={Stage}",
                GrainKey, _run.CurrentStageId);
            await CommitAsync(events);
        }
        else
        {
            await SaveRunAsync();
        }
    }

    public async Task RefreshIssueContextAsync(WorkflowIssueContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RejectIfRunReloadRequired();
        if (_run is null || _run.Status.IsTerminal()) return;
        RequireIssueContext(context);
        if (WorkflowRunLineage.ContextEquals(_run, context.ProjectId, context.IssueNumber, context.EpicNumber)) return;
        WorkflowRunLineage.ApplyContext(_run, context.ProjectId, context.IssueNumber, context.EpicNumber);
        await SaveRunAsync();
    }

    private void RequireIssueContext(WorkflowIssueContext context)
    {
        var expectedProjectId = GetProjectId();
        var expectedIssueNumber = GetIssueNumber();
        if (!string.Equals(expectedProjectId, context.ProjectId, StringComparison.Ordinal)
            || expectedIssueNumber != context.IssueNumber)
            throw new InvalidOperationException(
                $"Workflow '{GrainKey}' belongs to issue '{expectedProjectId}#{expectedIssueNumber}', not '{context.ProjectId}#{context.IssueNumber}'.");
    }

    private async Task EnsureCreatedRunAsync(WorkflowStartInput? input)
    {
        if (_run is not null) return;
        var metadata = input?.Metadata ?? BuildRunMetadata(input);
        RequireProjectOwnership(metadata);
        var projectId = metadata?.Annotations?.GetValueOrDefault("projectId");
        var issueNumber = metadata?.Annotations?.GetValueOrDefault("issueNumber") is { } rawNumber
            && int.TryParse(rawNumber, out var parsedNumber)
            ? parsedNumber
            : (int?)null;
        var structure = await _profileManager.LoadStructureAsync(GrainKey, projectId, issueNumber);
        _run = WorkflowRun.Create(GrainKey, structure, Now(), metadata);
        await _runProfileManager.EnsureArchiveDefaultAsync(GrainKey);
        _run.Workspace = input?.Workspace;
    }

    private async Task EnsureCreatedRunAsync(WorkflowIssueContext context)
    {
        var metadata = new WorkflowRunMetadata(
            Name: null,
            CreatedAt: Now(),
            Annotations: WorkflowRunLineage.AnnotationsFor(context.ProjectId, context.IssueNumber, context.EpicNumber));
        var structure = await _profileManager.LoadStructureAsync(GrainKey, context.ProjectId, context.IssueNumber);
        _run = WorkflowRun.Create(GrainKey, structure, Now(), metadata);
        await _runProfileManager.EnsureArchiveDefaultAsync(GrainKey);
        _run.Workspace = await _profileManager.LoadIssueWorkspaceAsync(context.ProjectId, context.IssueNumber);
    }

    public async Task ResumeAsync()
    {
        EnsureRun();
        var events = _run.Resume(Now());
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

        if (_run.Status is not (WorkflowRunStatus.Created or WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running or WorkflowRunStatus.AwaitingApproval or WorkflowRunStatus.Paused or WorkflowRunStatus.Failed))
            throw new InvalidOperationException($"Cannot stop workflow in {_run.Status} state");

        var stopEvents = _run.Stop();

        var abandonedEvents = await _workLifecycle.AbandonRunningWorkAsync(_run!, reason ?? "stopped");
        var events = abandonedEvents.Concat(stopEvents).ToArray();

        _log.LogInformation("Workflow {Id} stopped: {Reason}", GrainKey, reason);
        await CommitAsync(events, reason);
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        var events = _run.Approve(Now());
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task<string> RequestChangesAsync(string body)
    {
        EnsureRun();
        var stage = _run.CurrentStage();
        var approval = await _profileManager.LoadApprovalConfigAsync(GrainKey);
        var feedbackTasks = WorkflowRunExtensions.ResolveFeedbackTasks(approval?.Feedback?.Tasks, stage.Id);
        var feedbackId = CreateFeedbackId();
        var events = _run.RequestChanges(body, feedbackId, Now(), feedbackTasks);
        _log.LogInformation("Workflow {Id} requested changes at stage={Stage}: feedback={FeedbackId}", GrainKey, stage.Id, feedbackId);
        await CommitAsync(events);
        return feedbackId;
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        var retriedStageId = _run.CurrentStageId;
        await ReleaseCurrentStageLocksAsync("retried");
        var events = _run.Retry(Now());
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStageId);
        try
        {
            await CommitAsync(events);
        }
        catch
        {
            // The retry did not persist. Re-acquire the lock for the stage we
            // just released so the rolled-back run still holds its sequential
            // lock until a later successful transition releases it.
            if (retriedStageId is not null)
                await AcquireStageLocksIfNeededAsync(retriedStageId);
            throw;
        }
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        var rerunStageId = _run.CurrentStageId;
        await ReleaseCurrentStageLocksAsync("rerun");
        var events = _run.Rerun(Now());
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStageId);
        try
        {
            await CommitAsync(events);
        }
        catch
        {
            if (rerunStageId is not null)
                await AcquireStageLocksIfNeededAsync(rerunStageId);
            throw;
        }
    }

    public async Task<WorkflowControlResult> RerunFromStageAsync(string stageId)
    {
        EnsureRun();
        IReadOnlyList<WorkflowEvent> events;
        try
        {
            events = _run.RerunFromStage(stageId, Now());
        }
        catch (WorkflowControlRejectionException ex)
        {
            return WorkflowControlResult.Rejected(ex.Code, ex.Message, ex.DetailsJson());
        }

        var targetIdx = _run.Stages.FindIndex(s => s.Id == stageId);
        var releasedStages = new List<string>();
        for (var i = targetIdx; i < _run.Stages.Count; i++)
        {
            releasedStages.Add(_run.Stages[i].Id);
            await ReleaseStageLocksAsync(_run.Stages[i].Id, "rerun-from-stage");
        }
        _log.LogInformation("Workflow {Id} rerun-from-stage at stage={Stage}", GrainKey, stageId);
        try
        {
            await CommitAsync(events);
        }
        catch
        {
            // Re-acquire the locks for stages we released; the rerun-from-stage
            // did not persist, so the rolled-back run still requires them.
            foreach (var released in releasedStages)
                await AcquireStageLocksIfNeededAsync(released);
            throw;
        }
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
        var expect = WorkflowDispatchHelpers.ParseWith(task.Expect);
        var events = _run.AddRuntimeTask(
            new TaskDefinition(task.Id, task.Title, task.Uses, with, expect, Recovery: task.Recovery),
            Now(),
            task.Stage,
            task.InvalidateChecks);

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

    public async Task<WorkflowAssignmentResult> AssignWorkerAsync(string workerId)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "missing");
        if (_run.Status.IsTerminal() || _run.Status is WorkflowRunStatus.Created or WorkflowRunStatus.Paused or WorkflowRunStatus.AwaitingApproval)
            return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "not-runnable");
        if (_run.Assignment is not null)
        {
            if (!string.Equals(_run.Assignment.WorkerId, workerId, StringComparison.Ordinal))
                return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, _run.Assignment.WorkerId, "already-assigned");
            return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, workerId);
        }
        if (_run.Status != WorkflowRunStatus.Pending || !_run.HasDispatchableWork())
            return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "not-runnable");
        if (_run.Assignment is null)
        {
            _run.AssignTo(workerId, Now());
            _cachedAssignedWorkerId = workerId;
            await SaveRunAsync();
        }

        return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, workerId);
    }

    public async Task<WorkItem?> ClaimNextAsync(string workerId)
    {
        RejectIfRunReloadRequired();
        if (_run is null || _run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running))
            return null;

        if (!_run.IsAssignedTo(workerId))
            return null;

        var work = _run.NextWork();
        if (work is null)
            return null;

        var item = _workLifecycle.BuildClaimableWorkItem(_run!, work);
        if (item?.Id is null)
            return null;
        var workId = item.Id;

        var stage = _run.CurrentStageId;
        if (stage is not null && !await AcquireStageLocksIfNeededAsync(stage))
            return null;

        var resolvedWorkId = await _workLifecycle.ClaimWorkAsync(
            _run!, workId, workerId, events => CommitAsync(events));

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

            tasksToInsert.Add(new TaskDefinition(t.Id, t.Title, t.Uses, WorkflowDispatchHelpers.ParseWith(t.With), WorkflowDispatchHelpers.ParseWith(t.Expect)));
        }

        var events = _run.AddRuntimeTasks(tasksToInsert, Now());

        _log.LogInformation("Workflow {Id} added {Count} tasks in stage {Stage}",
            GrainKey, tasksToInsert.Count, current.Id);

        await CommitAsync(events);

        return new AddTasksBatchResult(GrainKey, current.Id, tasksToInsert.Count);
    }

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<string?> GetRunStatusAsync()
    {
        RejectIfRunReloadRequired();
        return Task.FromResult(_run?.Status.ToString());
    }

    public Task<bool> IsStoppedOrTerminalAsync()
    {
        RejectIfRunReloadRequired();
        if (_run is null) return Task.FromResult(false);
        return Task.FromResult(_run.IsTerminal());
    }

    public Task<string?> GetAssignedWorkerIdAsync()
    {
        RejectIfRunReloadRequired();
        return Task.FromResult(_run?.Assignment?.WorkerId ?? _cachedAssignedWorkerId);
    }

    public Task<string?> GetCurrentWorkIdAsync()
    {
        RejectIfRunReloadRequired();
        var stage = _run?.CurrentStage();
        if (stage is null) return Task.FromResult<string?>(null);
        return Task.FromResult(stage.RunningTask?.WorkId ?? stage.ChecksWorkId);
    }

    public Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId)
    {
        RejectIfRunReloadRequired();
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

    public Task ReleaseStageLocksAsync(string stage, string reason) =>
        _stageLockCoordinator.ReleaseStageLocksAsync(stage, reason);

    [MemberNotNull(nameof(_run))]
    private void EnsureRun()
    {
        if (_run is null)
            throw new InvalidOperationException($"Workflow '{GrainKey}' has no workflow run");
        if (_runReloadRequired)
            throw new InvalidOperationException($"Workflow '{GrainKey}' must reload after a failed save");
    }

    // For entry points that return a result (not throw) when no run exists, a
    // reload-required activation must still be rejected: the dirty in-memory
    // run must not be mutated/persisted through these paths before the grain
    // reloads from storage.
    private void RejectIfRunReloadRequired()
    {
        if (_runReloadRequired)
            throw new InvalidOperationException($"Workflow '{GrainKey}' must reload after a failed save");
    }

    private async Task CommitAsync(
        IReadOnlyList<WorkflowEvent> events,
        string? reason = null,
        CancellationToken ct = default)
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
            WorkflowRunStopped => Task.CompletedTask,
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

    internal string GetProjectId() =>
        _run?.Metadata?.Annotations?.TryGetValue("projectId", out var v) == true ? v : "";

    internal int? GetIssueNumber() =>
        _run?.Metadata?.Annotations?.TryGetValue("issueNumber", out var v) == true
            && int.TryParse(v, out var number)
            && number > 0
            ? number
            : null;

    private WorkflowRunMetadata? BuildRunMetadata(WorkflowStartInput? input)
    {
        if (input is null) return null;
        return new WorkflowRunMetadata(input.Name, Now(), input.Labels, input.Annotations);
    }

    private void RequireProjectOwnership(WorkflowRunMetadata? metadata)
    {
        if (metadata?.Annotations?.TryGetValue("projectId", out var projectId) == true
            && !string.IsNullOrWhiteSpace(projectId))
            return;

        throw new InvalidOperationException(
            $"Workflow '{GrainKey}' cannot start without the required projectId annotation.");
    }

    private async Task ClearStoppedRunStaleApprovalGateAsync(CancellationToken ct)
    {
        if (_run is null || _run.Status != WorkflowRunStatus.Stopped || !_run.ClearStaleApprovalGate())
            return;

        await _runStore.SaveAsync(_run, ct);
        _runDirty = false;
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
            MarkRunReloadRequired();
            _log.LogWarning(ex,
                "Workflow {Id} save failed because the persisted run ETag changed; deactivating grain to reload state",
                GrainKey);
            DeactivateOnIdle();
            throw;
        }
        catch
        {
            MarkRunReloadRequired();
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
            MarkRunReloadRequired();
            _log.LogWarning(ex,
                "Workflow {Id} save failed because the persisted run ETag changed; deactivating grain to reload state",
                GrainKey);
            DeactivateOnIdle();
            throw;
        }
        catch
        {
            MarkRunReloadRequired();
            DeactivateOnIdle();
            throw;
        }
    }

    private void MarkRunReloadRequired()
    {
        _runDirty = false;
        _runReloadRequired = true;
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

    private static string CreateFeedbackId() => $"fb_{Guid.NewGuid():N}";
}
