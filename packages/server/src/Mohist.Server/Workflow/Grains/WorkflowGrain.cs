using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
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
    /// recovery/reconciliation when the authoritative <see cref="WorkflowRun.Claim"/> is absent.
    /// This is grain infrastructure state, NOT part of the claim domain model, and does NOT
    /// represent an active claim. The authoritative runner identity is always
    /// <c>_run.Claim.RunnerId</c>. When no <see cref="WorkflowRun.Claim"/> exists,
    /// <see cref="WorkflowRun.IsClaimed"/> remains <c>false</c> regardless of this field's value.
    /// </summary>
    private string? _lastKnownRunnerId;
    private bool _runDirty;
    private string? _dispatchedWorkId;
    private IGrainReminder? _workHeartbeatReminder;
    private readonly IWorkflowRunStore _runStore;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly WorkflowProfileManager _profileManager;
    private readonly WorkflowDispatchBuilder _dispatchBuilder;
    private readonly WorkflowSessionHealthService _sessionHealth;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly IRunnerWorkspaceClient _workspaceClient;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        IWorkflowBacklogDirectory backlogs,
        WorkflowProfileManager profileManager,
        WorkflowDispatchBuilder dispatchBuilder,
        WorkflowSessionHealthService sessionHealth,
        IWorkflowArtifactBindService artifactBindService,
        IRunnerWorkspaceClient workspaceClient,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _backlogs = backlogs;
        _profileManager = profileManager;
        _dispatchBuilder = dispatchBuilder;
        _sessionHealth = sessionHealth;
        _artifactBindService = artifactBindService;
        _workspaceClient = workspaceClient;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);

        _lastKnownRunnerId = _run?.Claim?.RunnerId;
        await EnsureWorkHeartbeatAsync();
        await RunCoreAsync();
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

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, WorkHeartbeatReminderName, StringComparison.Ordinal))
            return;

        await EnsureWorkHeartbeatAsync();
    }

    public async Task StartAsync(WorkflowStartInput? input = null)
    {
        if (_run is null)
        {
            var metadata = input?.Metadata;
            var projectId = metadata?.Annotations?.GetValueOrDefault("projectId") ?? GetProjectId();
            var issueId = metadata?.Annotations?.GetValueOrDefault("issueId") ?? GetIssueId();
            var effectiveDefinition = await LoadEffectiveDefinitionAsync(projectId, issueId);
            _run = WorkflowRun.Create(GrainKey, effectiveDefinition, metadata ?? BuildRunMetadata(null));
            _run.Workspace = input?.Workspace;
        }

        var events = _run.Start();

        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    private async Task<WorkflowDefinition> LoadEffectiveDefinitionAsync(
        string? projectId = null,
        string? issueId = null)
    {
        var template = await _profileManager.LoadTemplateAsync(GrainKey, projectId, issueId);
        return template.Structure
            ?? throw new InvalidOperationException($"Workflow '{GrainKey}' has no effective workflow template");
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
        var config = (await LoadEffectiveDefinitionAsync()).Approval?.Feedback?.Task;
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
        var failedTaskId = failure?.TaskId;
        var stageId = _run.CurrentStageId;

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
        if (_run.Claim is not null)
        {
            if (!string.Equals(_run.Claim.RunnerId, runnerId, StringComparison.Ordinal))
                return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Rejected, _run.Claim.RunnerId, "already-assigned");
        }
        if (_run.Claim is null)
        {
            _run.ClaimBy(runnerId, DateTimeOffset.UtcNow);
            _lastKnownRunnerId = runnerId;
            await SaveRunAsync();
        }

        await RunCoreAsync();
        return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, runnerId);
    }

    public async Task<WorkflowStartMaterializationDispatch?> PrepareStartMaterializationAsync(string runnerId)
    {
        if (_run is null || _run.Status != WorkflowRunStatus.Running) return null;
        if (_run.Claim is not null && !string.Equals(_run.Claim.RunnerId, runnerId, StringComparison.Ordinal)) return null;
        var work = _run.NextWork();
        if (work is null) return null;
        var dispatch = await PrepareWorkAsync(work, runnerId, markRunning: false);
        if (dispatch is null) return null;
        return new WorkflowStartMaterializationDispatch(dispatch);
    }

    public async Task RecordStartMaterializationFailureAsync(string runnerId, string? message)
    {
        if (_run is null || _run.Status != WorkflowRunStatus.Running) return;
        if (_run.Claim is not null && !string.Equals(_run.Claim.RunnerId, runnerId, StringComparison.Ordinal)) return;
        if (_run.NextWork() is null) return;

        var failureMessage = FormatWorkspaceMaterializationFailure(message);
        var events = _run.FailStage(failureMessage);
        await CommitAsync(events);
    }

    private async Task RunCoreAsync()
    {
        if (_run is null || _run.Status != WorkflowRunStatus.Running)
            return;

        var runnerId = _run.Claim?.RunnerId;
        if (string.IsNullOrWhiteSpace(runnerId))
        {
            if (_run.NextWork() is not null)
                await RegisterToBacklogAsync();
            return;
        }

        if (await TryRecoverActiveWorkAsync(runnerId))
            return;

        var work = _run.NextWork();
        if (work is null)
            return;

        if (!await AcquireStageLocksIfNeededAsync(work.Stage))
            return;

        if (ShouldMaterializeWorkflowWorkspace())
        {
            var materialization = await PrepareStartMaterializationAsync(runnerId);
            if (materialization is null)
                return;

            var result = await _workspaceClient.MaterializeWorkspaceAsync(GetProjectId(), runnerId, materialization.Dispatch);
            if (!result.Ok)
            {
                await RecordStartMaterializationFailureAsync(runnerId, result.Message);
                return;
            }

            _run.WorkspaceMaterializedAt = DateTimeOffset.UtcNow;
            await SaveRunAsync();
        }

        var dispatch = await PrepareWorkAsync(work, runnerId, markRunning: true);
        if (dispatch is null)
            return;

        await SaveRunAsync();
        await AssignRunnerWorkAsync(runnerId, dispatch);
    }

    private static string FormatWorkspaceMaterializationFailure(string? resultMessage)
    {
        var message = string.IsNullOrWhiteSpace(resultMessage)
            ? "workspace materialization failed"
            : resultMessage.Trim();
        return $"workflow workspace materialization failure (workspace-corrupt): {message}";
    }

    private bool ShouldMaterializeWorkflowWorkspace()
    {
        if (_run is null) return false;
        return _run.WorkspaceMaterializedAt is null;
    }

    private async Task EnsureWorkHeartbeatAsync()
    {
        if (await FailLostRunningTasksAsync())
            return;

        if (IsRunnable())
        {
            _workHeartbeatReminder ??= await this.RegisterOrUpdateReminder(
                WorkHeartbeatReminderName,
                WorkHeartbeatReminderDueTime,
                WorkHeartbeatReminderPeriod);
            await RunCoreAsync();
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
    }

    private bool IsRunnable()
    {
        if (_run?.Status != WorkflowRunStatus.Running)
            return false;

        if (_run.IsClaimed)
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

        var events = _run.InsertRuntimeTasksAfter(tasksToInsert);

        _log.LogInformation("Workflow {Id} added {Count} tasks in stage {Stage}",
            GrainKey, tasksToInsert.Count, current.Id);

        await CommitAsync(events);

        return new AddTasksBatchResult(GrainKey, current.Id, tasksToInsert.Count);
    }

    public async Task ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        if (_run is null || !_run.IsClaimedBy(runnerId)) return;
        var stage = _run.CurrentStage();
        var activeTask = stage.FindRunningTaskByWork(workId, runnerId);
        if (activeTask is null && _dispatchedWorkId is null) return;

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        IReadOnlyList<WorkflowEvent> events = activeTask is not null
            ? await ProcessTaskResultAsync(result, activeTask.Id, workId)
            : await ProcessCheckResultAsync(result);

        if (activeTask is null)
            _dispatchedWorkId = null;

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

    public Task<string?> GetClaimedRunnerIdAsync()
    {
        return Task.FromResult(_run?.Claim?.RunnerId ?? _lastKnownRunnerId);
    }

    public Task<string?> GetCurrentWorkIdAsync()
    {
        var stage = _run?.CurrentStage();
        if (stage is null) return Task.FromResult<string?>(null);
        return Task.FromResult(stage.RunningTask?.WorkId ?? _dispatchedWorkId);
    }

    public Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId)) return Task.FromResult<WorkflowActiveWorkView?>(null);
        var currentStage = _run?.CurrentStage();
        if (currentStage is null) return Task.FromResult<WorkflowActiveWorkView?>(null);
        var activeTask = currentStage.RunningTask;
        if (!string.Equals(activeTask?.WorkId ?? _dispatchedWorkId, workId, StringComparison.Ordinal))
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
            IssueId: issueId));
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
        var definition = await LoadEffectiveDefinitionAsync();
        var stageDef = definition.Stages.Find(s => s.Stage == stage);
        if (stageDef?.LockBehavior is null) return null;
        if (!string.Equals(stageDef.LockBehavior, "sequential", StringComparison.OrdinalIgnoreCase))
            return null;
        return stageDef.Resources?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
    }

    private async Task RegisterToBacklogAsync()
    {
        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;
        _backlogs.RegisterProject(projectId);
        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(projectId));
        await backlog.EnqueueAsync(GrainKey);
        _log.LogInformation("Workflow {Id} registered to workflow backlog", GrainKey);
    }

    private async Task RequeueWorkflowIdAsync(string projectId, string workflowRunId)
    {
        _backlogs.RegisterProject(projectId);
        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(projectId));
        await backlog.EnqueueAsync(workflowRunId);
    }

    private async Task<WorkDispatch?> PrepareWorkAsync(WorkflowWork work, string runnerId, bool markRunning)
    {
        switch (work.WorkType)
        {
            case "stage-init":
                var definition = await LoadEffectiveDefinitionAsync();
                var stageDef = definition.Stages.Find(s => s.Stage == work.Stage)
                    ?? throw new InvalidOperationException($"Workflow '{GrainKey}' has no definition for stage '{work.Stage}'");
                var events = _run!.InitializeStage(stageDef.Tasks, stageDef.Checks);
                if (events.Count > 0)
                    await CommitAsync(events);
                var nextWork = _run!.NextWork();
                return nextWork is not null ? await PrepareWorkAsync(nextWork, runnerId, markRunning) : null;

            case "task":
                var t = (WorkflowWork.TaskData)work.Data;
                var taskWith = t.With is not null
                    ? new Dictionary<string, JsonElement?>(t.With) { ["title"] = JsonSerializer.SerializeToElement(t.Title) }
                    : new Dictionary<string, JsonElement?> { ["title"] = JsonSerializer.SerializeToElement(t.Title) };
                return await MakeDispatchAsync(new WorkDispatchRequest(
                    work.Stage, t.Id, "task", t.Title, t.Uses, taskWith,
                    t.Artifacts, t.Outputs), runnerId, markRunning);

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
                await SaveRunAsync(events);
                foreach (var e in events)
                    await On(e);
            }
        }
        else if (req.WorkType == "checks")
        {
            _dispatchedWorkId = dispatch.WorkId;
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

    private async Task AssignRunnerWorkAsync(string runnerId, WorkDispatch dispatch)
    {
        var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var result = await runner.AssignWorkAsync(dispatch);
        if (result.Status == RunnerWorkAssignmentStatus.Rejected)
        {
            _log.LogWarning("Runner {RunnerId} rejected work {WorkId} for workflow {WorkflowId}: {Reason}",
                runnerId, dispatch.WorkId, GrainKey, result.Reason);
        }
    }

    private async Task<bool> TryRecoverActiveWorkAsync(string runnerId)
    {
        var currentStage = _run!.CurrentStage();
        var runningTask = currentStage.RunningTask;
        if (runningTask is not null)
        {
            if (!string.Equals(runningTask.RunnerId, runnerId, StringComparison.Ordinal))
                return true;

            var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
            if (!await runner.IsAvailableAsync())
            {
                var events = _run!.FailTaskForRunnerLost();
                await CommitAsync(events);
                return false;
            }

            var dispatch = await PrepareWorkAsync(WorkflowWork.Task(
                currentStage.Id,
                runningTask.Id,
                runningTask.Title,
                runningTask.Uses,
                runningTask.WithInput,
                runningTask.Artifacts,
                runningTask.Outputs), runnerId, markRunning: false);
            if (dispatch is not null)
                await AssignRunnerWorkAsync(runnerId, dispatch);
            return true;
        }

        if (_dispatchedWorkId is null)
            return false;

        var checkRunner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        if (!await checkRunner.IsAvailableAsync())
        {
            _dispatchedWorkId = null;
            await SaveRunAsync();
            return false;
        }

        var pendingChecks = currentStage.Checks
            .Where(c => c.Status == StageCheckStatus.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        var checkDispatch = await MakeChecksDispatchAsync(
            currentStage.Id, pendingChecks, runnerId,
            markRunning: false);
        await AssignRunnerWorkAsync(runnerId, checkDispatch);
        return true;
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
        {
            var runner = GrainFactory.GetGrain<IRunnerGrain>(runningTask.RunnerId);
            if (await runner.IsAvailableAsync())
                return false;
        }

        var events = _run.FailTaskForRunnerLost();
        await CommitAsync(events);
        return events.Count > 0;
    }

    private async Task ClearExecutableStateAsync(string reason)
    {
        await ReleaseCurrentStageLocksAsync(reason);
        _dispatchedWorkId = null;

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
            WorkflowDispatchHelpers.CaptureTaskOutputs(run, currentTask, result.CapturedOutputs);
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
        }
        else
        {
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
        var template = await _profileManager.LoadTemplateAsync(GrainKey);
        var independent = await _profileManager.LoadVariablesAsync(GrainKey);
        var embedded = template.EmbeddedVariables ?? VariableBundle.Empty;
        var resolved = VariableBundle.Patch(embedded, independent);

        return WorkflowDispatchHelpers.ResolveEffectiveStageVars(resolved, _run?.CurrentStageId);
    }

    private async Task<IReadOnlyList<WorkflowEvent>> ProcessCheckResultAsync(WorkResult result)
    {
        var checkResults = WorkflowDispatchHelpers.ParseCheckResults(result.Output);
        if (checkResults.Count == 0)
            return [];

        var stage = _run!.CurrentStageId!;
        var definition = await LoadEffectiveDefinitionAsync();
        var stageDef = definition.Stages.Find(s => s.Stage == stage);
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

        var definition = await LoadEffectiveDefinitionAsync();
        var stageDef = definition.Stages.Find(s => s.Stage == failure.Stage);
        var repairTasks = ResolveRepairTasks(stageDef, failure.CheckName, enforceLimit: false);
        if (repairTasks is null)
            return null;

        _dispatchedWorkId = null;
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
            _runDirty = true;
            await SaveRunAsync(events);
        }

        foreach (var e in events)
            await On(e, reason);
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
