using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;

using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowGrain : Grain, IWorkflowGrain, IRemindable
{
    private const string WorkHeartbeatReminderName = "heartbeat";
    private static readonly TimeSpan WorkHeartbeatReminderDueTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkHeartbeatReminderPeriod = TimeSpan.FromMinutes(1);
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
    private bool _heartbeatEnsuredThisCommit;
    private string? _dispatchedWorkId;
    private DateTimeOffset? _dispatchedWorkStartedAt;
    private IGrainReminder? _workHeartbeatReminder;
    private IDisposable? _workCompletionTimer;
    private readonly IWorkflowRunStore _runStore;
    private readonly WorkflowProfileManager _profileManager;
    private readonly WorkflowDispatchBuilder _dispatchBuilder;
    private readonly WorkflowSessionHealthService _sessionHealth;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly WorkflowGrainOptions _options;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        WorkflowProfileManager profileManager,
        WorkflowDispatchBuilder dispatchBuilder,
        WorkflowSessionHealthService sessionHealth,
        IWorkflowArtifactBindService artifactBindService,
        IOptions<WorkflowGrainOptions> options,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _profileManager = profileManager;
        _dispatchBuilder = dispatchBuilder;
        _sessionHealth = sessionHealth;
        _artifactBindService = artifactBindService;
        _options = options.Value;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);

        _lastKnownRunnerId = _run?.Assignment?.RunnerId;
        _dispatchedWorkId = _run?.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started
            ? _run.WorkDelivery.WorkId
            : null;
        _dispatchedWorkStartedAt = _run?.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started
            ? _run.WorkDelivery.StartedAt
            : null;
        await EnsureWorkHeartbeatAsync();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _workCompletionTimer?.Dispose();
        _workCompletionTimer = null;

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

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, WorkHeartbeatReminderName, StringComparison.Ordinal))
            return;

        await FailTimedOutWorkAsync();
        await EnsureWorkHeartbeatAsync();
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

        if (_run.Status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Paused))
            throw new InvalidOperationException($"Cannot stop workflow in {_run.Status} state");

// Flip the run status to Stopped before clearing executable state so the
        // TaskFailed event handler (which triggers EnsureWorkHeartbeatAsync →
        // RunCoreAsync) observes a terminal status and short-circuits. Otherwise
        // the next work item (e.g. stage checks) would be auto-dispatched before
        // the stop takes effect.
        var stopEvents = _run.Stop();

        await ClearExecutableStateAsync(reason ?? "stopped");
        var events = new List<WorkflowEvent>(stopEvents);

        // Clearing the running task in ClearExecutableStateAsync can re-trigger
        // EnsureWorkHeartbeatAsync via the On(TaskFailed) handler before the
        // run is transitioned to Stopped above, which may re-dispatch the
        // next pending work (e.g. a check). Strip any new dispatch now that
        // the workflow is terminally stopped so GetCurrentWorkIdAsync() reports
        // no active work.
        _dispatchedWorkId = null;
        _dispatchedWorkStartedAt = null;
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);
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
        var events = await TryScheduleRequestedCheckRepairAsync() ?? _run.Retry();
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

    public async Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Runtime task requires id");
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Runtime task requires title");

        _dispatchedWorkId = null;
        _dispatchedWorkStartedAt = null;
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);
        var with = WorkflowDispatchHelpers.ParseWith(task.With);
        var events = _run.AddRuntimeTask(new TaskDefinition(task.Id, task.Title, task.Uses, with), task.Stage, task.InvalidateChecks);

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
        if (_run.Status != WorkflowRunStatus.Running) return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, Reason: "not-runnable");
        if (_run.Assignment is not null)
        {
            if (!string.Equals(_run.Assignment.RunnerId, runnerId, StringComparison.Ordinal))
                return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, _run.Assignment.RunnerId, "already-assigned");
        }
        if (_run.Assignment is null)
        {
            _run.AssignTo(runnerId, DateTimeOffset.UtcNow);
            _lastKnownRunnerId = runnerId;
            await SaveRunAsync();
        }

        return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, runnerId);
    }

    public async Task<WorkDispatch?> PollWorkAsync(string runnerId)
    {
        if (_run is null || _run.Status != WorkflowRunStatus.Running)
            return null;

        if (!_run.IsAssignedTo(runnerId))
            return null;

        var active = await TryBuildActiveWorkDispatchAsync(runnerId);
        if (active is not null)
            return active;

        var work = _run.NextWork();
        if (work is null)
            return null;

        if (!await AcquireStageLocksIfNeededAsync(work.Stage))
            return null;

        var dispatch = await PrepareWorkAsync(work, runnerId, markRunning: true);
        if (dispatch is null)
            return null;

        await SaveRunAsync();
        return dispatch;
    }

    private Task RunCoreAsync()
    {
        return Task.CompletedTask;
    }

    private async Task EnsureWorkHeartbeatAsync()
    {
        if (_heartbeatEnsuredThisCommit) return;
        _heartbeatEnsuredThisCommit = true;

        if (IsRunnable())
        {
            _workHeartbeatReminder ??= await this.RegisterOrUpdateReminder(
                WorkHeartbeatReminderName,
                WorkHeartbeatReminderDueTime,
                WorkHeartbeatReminderPeriod);
            ArmWorkCompletionTimer();
            return;
        }

        await DisableWorkHeartbeatAsync();
    }

    private async Task DisableWorkHeartbeatAsync()
    {
        if (_workHeartbeatReminder is null)
            return;

        await this.UnregisterReminder(_workHeartbeatReminder);
        _workHeartbeatReminder = null;
        _workCompletionTimer?.Dispose();
        _workCompletionTimer = null;
    }

    private bool IsRunnable()
    {
        if (_run?.Status != WorkflowRunStatus.Running)
            return false;

        if (_run.IsAssigned)
            return true;

        return _run.NextWork() is not null;
    }

    public async Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request)
    {
        EnsureRun();
        if (request.Tasks is null || request.Tasks.Count == 0)
            throw new InvalidOperationException("AddTasksBatchRequest requires at least one task");

        if (_run.CurrentStageId is null)
            throw new InvalidOperationException("Workflow has no current stage");

        _dispatchedWorkId = null;
        _dispatchedWorkStartedAt = null;
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);

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

    public async Task ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        if (_run is null || !_run.IsAssignedTo(runnerId)) return;
        var stage = _run.CurrentStage();
        var activeTask = stage.FindRunningTaskByWork(workId, runnerId);
        var activeDeliveryWorkId = _run.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started
            ? _run.WorkDelivery.WorkId
            : _dispatchedWorkId;
        if (activeTask is null
            && (activeDeliveryWorkId is null || !string.Equals(activeDeliveryWorkId, workId, StringComparison.Ordinal)))
            return;

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        IReadOnlyList<WorkflowEvent> events = activeTask is not null
            ? await ProcessTaskResultAsync(result, activeTask.Id, workId)
            : await ProcessCheckResultAsync(result);

        if (activeTask is not null)
        {
            CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Completed);
        }

        if (activeTask is null)
        {
            CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Completed);
            _dispatchedWorkId = null;
            _dispatchedWorkStartedAt = null;
        }

        await CommitAsync(events);
    }

    public async Task NotifyRunnerLostAsync(string runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId) || _run is null)
            return;

        await FailLostRunningTasksAsync(runnerId);
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
        return Task.FromResult(_run.Status is WorkflowRunStatus.Stopped or WorkflowRunStatus.Completed or WorkflowRunStatus.Failed);
    }

    public Task<string?> GetAssignedRunnerIdAsync()
    {
        return Task.FromResult(_run?.Assignment?.RunnerId ?? _lastKnownRunnerId);
    }

    public Task<string?> GetCurrentWorkIdAsync()
    {
        var stage = _run?.CurrentStage();
        if (stage is null) return Task.FromResult<string?>(null);
        var deliveryWorkId = _run?.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started
            ? _run.WorkDelivery.WorkId
            : _dispatchedWorkId;
        return Task.FromResult(stage.RunningTask?.WorkId ?? deliveryWorkId);
    }

    public Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId)) return Task.FromResult<WorkflowActiveWorkView?>(null);
        var currentStage = _run?.CurrentStage();
        if (currentStage is null) return Task.FromResult<WorkflowActiveWorkView?>(null);
        var activeTask = currentStage.RunningTask;
        var deliveryWorkId = _run?.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started
            ? _run.WorkDelivery.WorkId
            : _dispatchedWorkId;
        if (!string.Equals(activeTask?.WorkId ?? deliveryWorkId, workId, StringComparison.Ordinal))
            return Task.FromResult<WorkflowActiveWorkView?>(null);

        var projectId = GetProjectId();
        var issueId = GetIssueId();
        var stage = _run?.CurrentStageId ?? string.Empty;
        return Task.FromResult<WorkflowActiveWorkView?>(new WorkflowActiveWorkView(
            WorkId: workId,
            WorkType: activeTask is not null ? "task" : "checks",
            Stage: stage,
            TaskRunId: activeTask?.Id ?? $"checks-{stage}",
            Title: activeTask?.Title ?? "Stage checks",
            ProjectId: string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            IssueId: issueId,
            IssueNumber: ResolveIssueNumber()));
    }

    public Task<WorkflowFeedbackRecord?> GetFeedbackAsync(string feedbackId)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(feedbackId))
            return Task.FromResult<WorkflowFeedbackRecord?>(null);

        var feedback = _run!.Feedback.FirstOrDefault(f => string.Equals(f.Id, feedbackId, StringComparison.Ordinal));
        if (feedback is null)
            return Task.FromResult<WorkflowFeedbackRecord?>(null);

        return Task.FromResult<WorkflowFeedbackRecord?>(ToSnapshot(feedback));
    }

    public Task<IReadOnlyList<WorkflowFeedbackRecord>> ListFeedbackAsync()
    {
        EnsureRun();
        var issueNumber = ResolveIssueNumber();
        var snapshots = _run!.Feedback
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => ToSnapshot(f, issueNumber))
            .ToList();
        return Task.FromResult<IReadOnlyList<WorkflowFeedbackRecord>>(snapshots);
    }

    private WorkflowFeedbackRecord ToSnapshot(ApprovalFeedback feedback) =>
        ToSnapshot(feedback, ResolveIssueNumber());

    private WorkflowFeedbackRecord ToSnapshot(ApprovalFeedback feedback, int? issueNumber) =>
        new(
            Id: feedback.Id,
            WorkflowRunId: feedback.WorkflowRunId,
            Stage: feedback.Stage,
            Body: feedback.Body,
            Status: feedback.Status,
            CreatedAt: feedback.CreatedAt,
            Resolution: ToResolution(feedback),
            IssueNumber: issueNumber);

    private static WorkflowFeedbackResolution? ToResolution(ApprovalFeedback feedback) =>
        feedback.Status == ApprovalFeedbackStatus.Resolved
            ? new WorkflowFeedbackResolution(
                ResolutionTaskId: feedback.ResolutionTaskId,
                ResolvedAt: feedback.ResolvedAt,
                ResolutionSummary: feedback.ResolutionSummary)
            : null;

    private int? ResolveIssueNumber() =>
        int.TryParse(GetIssueNumber(), out var number) ? number : null;

    private async Task<bool> AcquireStageLocksIfNeededAsync(string stage)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return true;

        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException($"Workflow '{GrainKey}' stage '{stage}' requires resource '{resource}' but project id is missing");

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = GrainFactory.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.AcquireSequentialAsync(new StageLockRequest(GrainKey, stage, resource, projectId));

        return result.Acquired;
    }

    private async Task ReleaseCurrentStageLocksAsync(string reason)
    {
        if (_run?.CurrentStageId is null) return;
        await ReleaseStageLocksAsync(_run.CurrentStageId, reason);
    }

    private async Task ReleaseStageLocksAsync(string stage, string reason)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return;

        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = GrainFactory.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.ReleaseAsync(new StageLockOwner(GrainKey, stage));

        if (!result.Released) return;

        if (!string.IsNullOrWhiteSpace(result.NextWorkflowRunId))
            await RequeueWorkflowIdAsync(projectId, result.NextWorkflowRunId);
    }

    private async Task<string?> GetSequentialLockResourceAsync(string stage)
    {
        var stageDef = await _profileManager.LoadStageSpecsAsync(GrainKey, stage);
        if (stageDef.LockBehavior is null) return null;
        if (!string.Equals(stageDef.LockBehavior, "sequential", StringComparison.OrdinalIgnoreCase))
            return null;
        return stageDef.Resources?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
    }

    private Task RequeueWorkflowIdAsync(string projectId, string workflowRunId)
    {
        // Pull scheduling has no per-project backlog to mutate here. Once the
        // lock is released, the next runner poll can rediscover the assignable
        // workflow run from persisted workflow state.
        return Task.CompletedTask;
    }

    private async Task<WorkDispatch?> PrepareWorkAsync(WorkflowWork work, string runnerId, bool markRunning)
    {
        switch (work.WorkType)
        {
            case "task":
                var t = (WorkflowWork.TaskData)work.Data;
                return await MakeDispatchAsync(new WorkDispatchRequest(
                    work.Stage, t.Id, "task", t.Title, t.Uses, t.With,
                    t.Artifacts, t.SetVars), runnerId, markRunning);

            case "checks":
                var ch = (WorkflowWork.ChecksData)work.Data;
                return await MakeChecksDispatchAsync(work.Stage, ch.Items, runnerId, markRunning);

            default:
                return null;
        }
    }

    private async Task<WorkDispatch> MakeDispatchAsync(WorkDispatchRequest req, string runnerId, bool markRunning = true)
    {
        if (req.WorkType == "task")
        {
            var workId = req.WorkIdOverride ?? req.LogicalId;
            await _sessionHealth.CheckAndEnforceAsync(
                workId, req.Stage, GrainKey, _run!,
                events => CommitAsync(events), "dispatch", default);
        }

        var dispatch = await _dispatchBuilder.BuildAsync(req, GrainKey, _run!);

        if (!markRunning) return dispatch;
        if (req.WorkType == "task")
        {
            var currentTask = _run!.CurrentStage().Tasks.FirstOrDefault(t => t.Id == req.LogicalId);
            if (currentTask?.Status != TaskRunStatus.Running)
            {
                var events = _run!.StartTask(dispatch.WorkId, runnerId);
                _run!.WorkDelivery = new WorkflowWorkDelivery(
                    dispatch.WorkId,
                    "task",
                    req.Stage,
                    WorkflowWorkDeliveryStatus.Started,
                    currentTask?.StartedAt ?? DateTimeOffset.UtcNow);
                await SaveRunAsync(events);
                foreach (var e in events)
                    await On(e);
            }
        }
        else if (req.WorkType == "checks")
        {
            _dispatchedWorkId = dispatch.WorkId;
            _dispatchedWorkStartedAt ??= DateTimeOffset.UtcNow;
            _run!.WorkDelivery = new WorkflowWorkDelivery(
                dispatch.WorkId,
                "checks",
                req.Stage,
                WorkflowWorkDeliveryStatus.Started,
                _dispatchedWorkStartedAt.Value);
        }
        _lastKnownRunnerId = runnerId;
        return dispatch;
    }

    private Task<WorkDispatch> MakeChecksDispatchAsync(
        string stage,
        IReadOnlyList<CheckItem> items,
        string runnerId,
        bool markRunning = true,
        string? workIdOverride = null)
        => MakeDispatchAsync(
            WorkflowDispatchBuilder.BuildChecksRequest(stage, items, workIdOverride),
            runnerId,
            markRunning);

    private async Task<WorkDispatch?> TryBuildActiveWorkDispatchAsync(string runnerId)
    {
        var run = _run!;
        var currentStage = run.CurrentStage();
        var runningTask = currentStage.RunningTask;
        if (runningTask is not null)
        {
            if (!string.Equals(runningTask.RunnerId, runnerId, StringComparison.Ordinal))
                return null;

            return await PrepareWorkAsync(WorkflowWork.Task(
                currentStage.Id,
                runningTask.Id,
                runningTask.Title,
                runningTask.Uses,
                runningTask.WithInput,
                runningTask.Artifacts,
                runningTask.SetVars), runnerId, markRunning: false);
        }

        var deliveryWorkId = run.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started
            ? run.WorkDelivery.WorkId
            : _dispatchedWorkId;
        if (deliveryWorkId is null)
            return null;
        _dispatchedWorkId = deliveryWorkId;
        _dispatchedWorkStartedAt ??= run.WorkDelivery?.StartedAt ?? DateTimeOffset.UtcNow;

        var pendingChecks = currentStage.Checks
            .Where(c => c.Status == StageCheckStatus.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        return await MakeChecksDispatchAsync(
            currentStage.Id, pendingChecks, runnerId,
            markRunning: false,
            workIdOverride: deliveryWorkId);
    }

    private async Task<bool> FailLostRunningTasksAsync(string? runnerId = null)
    {
        if (_run?.CurrentStageId is null)
            return false;

        var runningTask = _run.CurrentStage().RunningTask;
        if (runningTask is null || string.IsNullOrWhiteSpace(runningTask.RunnerId))
            return false;

        if (!string.IsNullOrWhiteSpace(runnerId)
            && !string.Equals(runningTask.RunnerId, runnerId, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(runnerId))
            return false;

        var events = _run.FailTaskForRunnerLost();
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);
        await CommitAsync(events);
        return events.Count > 0;
    }

    private void ArmWorkCompletionTimer()
    {
        _workCompletionTimer?.Dispose();
        _workCompletionTimer = null;

        var due = WorkCompletionDueTime();
        if (due is null)
            return;

        _workCompletionTimer = this.RegisterGrainTimer(
            _ => OnWorkCompletionTimerAsync(),
            due.Value,
            TimeSpan.FromMilliseconds(-1));
    }

    private TimeSpan? WorkCompletionDueTime()
    {
        if (_options.WorkCompletionTimeout <= TimeSpan.Zero)
            return null;

        var startedAt = ActiveWorkStartedAt();
        if (startedAt is null)
            return null;

        var deadline = startedAt.Value + _options.WorkCompletionTimeout;
        var due = deadline - DateTimeOffset.UtcNow;
        return due <= TimeSpan.Zero ? TimeSpan.Zero : due;
    }

    private DateTimeOffset? ActiveWorkStartedAt()
    {
        var runningTask = _run?.CurrentStage().RunningTask;
        if (runningTask is not null)
            return runningTask.StartedAt;

        if (_run?.WorkDelivery?.Status == WorkflowWorkDeliveryStatus.Started)
            return _run.WorkDelivery.StartedAt;

        return _dispatchedWorkId is null ? null : _dispatchedWorkStartedAt;
    }

    private async Task OnWorkCompletionTimerAsync()
    {
        await FailTimedOutWorkAsync();
        _heartbeatEnsuredThisCommit = false;
        await EnsureWorkHeartbeatAsync();
    }

    private async Task<bool> FailTimedOutWorkAsync()
    {
        if (_run?.CurrentStageId is null || _run.Status != WorkflowRunStatus.Running)
            return false;
        if (_options.WorkCompletionTimeout <= TimeSpan.Zero)
            return false;

        var startedAt = ActiveWorkStartedAt();
        if (startedAt is null || DateTimeOffset.UtcNow - startedAt.Value < _options.WorkCompletionTimeout)
            return false;

        var stage = _run.CurrentStage();
        var runningTask = stage.RunningTask;
        if (runningTask is not null)
        {
            _log.LogWarning(
                "Workflow {Id} work {WorkId} timed out after {Timeout}",
                GrainKey,
                runningTask.WorkId,
                _options.WorkCompletionTimeout);
            var events = _run.FailTask(new TaskResult("failed", "work-timeout"));
            CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);
            await CommitAsync(events);
            return events.Count > 0;
        }

        if (_dispatchedWorkId is null)
            return false;

        _log.LogWarning(
            "Workflow {Id} checks work {WorkId} timed out after {Timeout}",
            GrainKey,
            _dispatchedWorkId,
            _options.WorkCompletionTimeout);
        _dispatchedWorkId = null;
        _dispatchedWorkStartedAt = null;
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);
        var pendingCheck = stage.Checks.FirstOrDefault(check => check.Status == StageCheckStatus.Pending);
        if (pendingCheck is null)
            return false;

        var eventsForChecks = _run.FailCheck(new CheckResult(pendingCheck.Name, "failed", "work-timeout"));
        await CommitAsync(eventsForChecks);
        return eventsForChecks.Count > 0;
    }

    private void CompleteWorkDelivery(WorkflowWorkDeliveryStatus status)
    {
        if (_run?.WorkDelivery is null || _run.WorkDelivery.Status != WorkflowWorkDeliveryStatus.Started)
            return;

        _run.WorkDelivery = _run.WorkDelivery with
        {
            Status = status,
            FinishedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task ClearExecutableStateAsync(string reason)
    {
        await ReleaseCurrentStageLocksAsync(reason);
        _dispatchedWorkId = null;
        _dispatchedWorkStartedAt = null;
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);

        var runningTask = _run?.CurrentStage().RunningTask;
        if (runningTask is not null)
        {
            var events = _run!.FailTaskForStopped(reason);
            await SaveRunAsync(events);
            return;
        }

        await SaveRunAsync();
    }

    private async Task<IReadOnlyList<WorkflowEvent>> ProcessTaskResultAsync(WorkResult result, string taskRunId, string workId)
    {
        var run = _run!;
        var currentStage = run.CurrentStage();
        var currentTask = currentStage?.Tasks.FirstOrDefault(t => t.Id == taskRunId);
        var events = new List<WorkflowEvent>();

        if (result.ArtifactUploadIds is { Length: > 0 })
        {
            var artifactEvents = await BindArtifactUploadsAsync(
                taskRunId, workId, result.ArtifactUploadIds, currentTask?.Artifacts);

            if (artifactEvents is not null)
            {
                events.AddRange(artifactEvents);
            }
            else if (result.Status == "completed")
            {
                _log.LogWarning(
                    "Workflow {Id} task {TaskId}: artifact binding failed, failing task",
                    GrainKey, currentTask?.Id);
                events.AddRange(run.FailTask(
                    new TaskResult("failed", "Required declared artifacts were not uploaded or validated")));
                return events;
            }
        }
        else if (result.Status == "completed"
            && currentTask?.Artifacts is { IsEmpty: false })
        {
            events.AddRange(run.FailTask(
                new TaskResult("failed", "Required declared artifacts were not uploaded")));
            return events;
        }

        if (result.Status == "completed")
        {
            if (currentTask is not null)
                currentTask.Output = ParseOutputToJsonElement(result.Output);
            if (currentTask?.CausedByFeedbackId is { } feedbackId)
            {
                var resolved = run.ResolveFeedback(feedbackId, currentTask.Id, result.Output);
                if (resolved is not null)
                {
                    _log.LogInformation(
                        "Workflow {Id} resolved feedback {FeedbackId} via task {TaskId}",
                        GrainKey, feedbackId, currentTask.Id);
                }
            }
            events.AddRange(run.CompleteTask());

            // Recovery: the task completed and produced recovery tasks to be
            // inserted into the current stage. The engine treats them as
            // ordinary runtime tasks — it does not understand their semantics.
            if (result.RecoveryTasks is { Count: > 0 })
            {
                var recoveryEvents = run.AddRuntimeTasks(
                    result.RecoveryTasks.Select(t => new TaskDefinition(
                        t.Id, t.Title, t.Uses, WorkflowDispatchHelpers.ParseWith(t.With))).ToList());
                events.AddRange(recoveryEvents);
                _log.LogInformation(
                    "Workflow {Id} task {TaskId} produced {Count} recovery tasks",
                    GrainKey, currentTask?.Id, result.RecoveryTasks.Count);
            }
        }
        else
        {
            if (currentTask is not null) currentTask.Output = ParseOutputToJsonElement(result.Output);
            events.AddRange(run.FailTask(new TaskResult("failed", result.Message)));
        }

        return events;
    }

    private async Task<IReadOnlyList<WorkflowEvent>?> BindArtifactUploadsAsync(
        string taskRunId,
        string workId,
        string[] artifactUploadIds,
        TaskArtifactCapture? declaredArtifacts)
    {
        // The runner renders declared artifact `path` strings against
        // the workflow variables before upload. Render the declared
        // paths with the same variables here so the bind service
        // compares resolved paths against resolved paths, not
        // templates against resolved paths.
        var variables = await ResolveBindVariablesAsync();

        var bindResult = await _artifactBindService.BindAsync(
            GrainKey,
            workId,
            taskRunId,
            artifactUploadIds,
            declaredArtifacts,
            variables: variables,
            projectId: GetProjectId(),
            issueId: GetIssueId());

        if (!bindResult.IsSuccess)
        {
            _log.LogWarning(
                "Workflow {Id} artifact binding failed: {Error}",
                GrainKey, bindResult.Error);
            return null;
        }

        return bindResult.ArtifactRecordedEvents
            .Select(a => (WorkflowEvent)a)
            .ToList();
    }

    private async Task<JsonElement?> ResolveBindVariablesAsync()
    {
        var resolved = await _profileManager.ResolveLayeredVariablesAsync(GrainKey);

        return resolved.ResolveStageVars(_run?.CurrentStageId);
    }

    private async Task<IReadOnlyList<WorkflowEvent>> ProcessCheckResultAsync(WorkResult result)
    {
        var checkResults = WorkflowDispatchHelpers.ParseCheckResults(result.Output);
        if (checkResults.Count == 0)
            return [];

        var stage = _run!.CurrentStageId!;
        var stageDef = await _profileManager.LoadStageSpecsAsync(GrainKey, stage);
        var actions = new List<CheckResultAction>(checkResults.Count);

        foreach (var cr in checkResults)
        {
            if (cr.Status == "pass")
            {
                actions.Add(new(cr, "pass"));
            }
            else if (cr.Status == "pending")
            {
                actions.Add(new(cr, "pending"));
            }
            else
            {
                var repairTasks = ResolveRepairTasks(stageDef, cr.Name, cr);
                actions.Add(repairTasks is not null
                    ? new(cr, "repair", repairTasks)
                    : new(cr, "fail"));
                if (repairTasks is not null)
                    break;
            }
        }

        return _run!.ProcessCheckResults(actions);
    }

    private IReadOnlyList<TaskDefinition>? ResolveRepairTasks(
        StageDefinition? stageDef,
        string checkName,
        CheckResult? result = null,
        bool enforceLimit = true)
    {
        var checkDef = stageDef?.Checks.Find(c => c.Name == checkName);
        if (checkDef?.OnFailure?.Repair is not { } repair) return null;

        if (enforceLimit)
        {
            var repairCount = _run!.GetRepairCount(checkName);
            if (repairCount >= repair.Limit) return null;
        }

        return _run!.BuildRepairTasks(checkName, repair, result);
    }

    private async Task<IReadOnlyList<WorkflowEvent>?> TryScheduleRequestedCheckRepairAsync()
    {
        if (_run?.Status != WorkflowRunStatus.Failed)
            return null;

        var failure = _run.Failure;
        if (failure?.Reason != FailureReason.CheckUnrepaired || string.IsNullOrWhiteSpace(failure.CheckName))
            return null;

        var stageDef = await _profileManager.LoadStageSpecsAsync(GrainKey, failure.Stage);
        var repairTasks = ResolveRepairTasks(stageDef, failure.CheckName, enforceLimit: false);
        if (repairTasks is null)
            return null;

        _dispatchedWorkId = null;
        _dispatchedWorkStartedAt = null;
        CompleteWorkDelivery(WorkflowWorkDeliveryStatus.Failed);
        return _run.ScheduleCheckRepair(failure.CheckName, repairTasks, failure.Message);
    }

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
            var resolved = await InitializeFreshStagesAsync(events);
            _runDirty = true;
            await SaveRunAsync(resolved);
            events = resolved;
        }

        _heartbeatEnsuredThisCommit = false;
        foreach (var e in events)
            await On(e, reason);
        _heartbeatEnsuredThisCommit = false;
    }

    /// <summary>
    /// Pre-commit step that materializes any <see cref="StageStarted"/> event
    /// by loading fresh stage specs and calling
    /// <see cref="WorkflowRunExtensions.InitializeStage"/>. Maintains the
    /// invariant <c>StageStarted ⟹ Initialized</c>: a stage is always
    /// initialized before its <see cref="StageStarted"/> is persisted or
    /// surfaced via <see cref="WorkflowRunExtensions.NextWork"/>. The merged
    /// events are returned so the caller commits them in a single batch with
    /// the original events (which preserves event ordering for downstream
    /// subscribers). Loop terminates when no further <see cref="StageStarted"/>
    /// is emitted — each <c>InitializeStage</c> → <c>Advance</c> may auto-skip
    /// an empty stage and emit another <see cref="StageStarted"/> for the
    /// next stage, which must also be initialized.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowEvent>> InitializeFreshStagesAsync(IReadOnlyList<WorkflowEvent> events)
    {
        if (_run is null) return events;

        var materialized = new List<WorkflowEvent>(events);
        var initializedStages = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            StageStarted? pendingStart = null;
            foreach (var e in materialized)
            {
                if (e is StageStarted started
                    && !initializedStages.Contains(started.Stage))
                {
                    var stageRun = _run.Stages.FirstOrDefault(s => string.Equals(s.Id, started.Stage, StringComparison.Ordinal));
                    if (stageRun is { Initialized: false })
                    {
                        pendingStart = started;
                        break;
                    }
                }
            }

            if (pendingStart is null) break;

            initializedStages.Add(pendingStart.Stage);

            var projectId = GetProjectId();
            var issueId = GetIssueId();
            var stageDef = await _profileManager.LoadStageSpecsAsync(
                GrainKey, pendingStart.Stage,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId,
                string.IsNullOrWhiteSpace(issueId) ? null : issueId);
            var initEvents = _run.InitializeStage(stageDef.Tasks, stageDef.Checks);
            materialized.AddRange(initEvents);
        }

        return materialized;
    }

    private Task On(WorkflowEvent e, string? reason = null) =>
        e switch
        {
            null => Task.CompletedTask,
            WorkflowRunStarted => EnsureWorkHeartbeatAsync(),
            WorkflowRunResumed => EnsureWorkHeartbeatAsync(),
            WorkflowRunPaused => DisableWorkHeartbeatAsync(),
            WorkflowRunStopped => OnWorkflowStoppedAsync(),
            WorkflowRunFailed => DisableWorkHeartbeatAsync(),
            WorkflowRunCompleted => DisableWorkHeartbeatAsync(),
            StageStarted => EnsureWorkHeartbeatAsync(),
            StageCompleted x => ReleaseStageLocksAsync(x.Stage, "completed"),
            StageFailed x => ReleaseStageLocksAsync(x.Stage, "failed"),
            StageApprovalRequested => DisableWorkHeartbeatAsync(),
            StageApprovalResolved x => x.Result == ApprovalResult.Approved
                ? EnsureWorkHeartbeatAsync()
                : Task.CompletedTask,
            FeedbackRequested => EnsureWorkHeartbeatAsync(),
            TaskStarted => EnsureWorkHeartbeatAsync(),
            TaskCompleted => EnsureWorkHeartbeatAsync(),
            TaskFailed => EnsureWorkHeartbeatAsync(),
            CheckPassed => EnsureWorkHeartbeatAsync(),
            CheckFailed => Task.CompletedTask,
            CheckPending => EnsureWorkHeartbeatAsync(),
            RepairScheduled => Task.CompletedTask,
            WorkflowArtifactRecorded => Task.CompletedTask,
        };

    private async Task OnWorkflowStoppedAsync()
    {
        await DisableWorkHeartbeatAsync();
        // Side effects now flow through the bus — IssueGrain subscribes
        // to com.mohist.workflow.run.stopped and the workspace cleanup
        // service subscribes to .completed.
    }

    private string GetProjectId() =>
        _run?.Metadata?.Annotations?.TryGetValue("projectId", out var v) == true ? v : "";

    private string? GetIssueId() =>
        _run?.Metadata?.Annotations?.TryGetValue("issueId", out var v) == true ? v : null;

    private string? GetIssueNumber() =>
        _run?.Metadata?.Annotations?.TryGetValue("issueNumber", out var v) == true ? v : null;

    private WorkflowRunMetadata? BuildRunMetadata(WorkflowStartInput? input)
    {
        if (input is null) return null;
        return new WorkflowRunMetadata(input.Name, DateTimeOffset.UtcNow, input.Labels, input.Annotations);
    }

    private static JsonElement? ParseOutputToJsonElement(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.Clone();
        }
        catch
        {
            var wrapped = JsonSerializer.SerializeToElement(output);
            return wrapped;
        }
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
