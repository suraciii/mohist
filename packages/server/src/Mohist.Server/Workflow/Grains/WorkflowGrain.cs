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
    private IGrainReminder? _workHeartbeatReminder;
    private readonly HashSet<string> _announcedWaitingLocks = [];
    private readonly HashSet<string> _announcedAcquiredLocks = [];
    private readonly IWorkflowRunStore _runStore;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly WorkflowProfileManager _profileManager;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly AgentSessionQuery _agentSessionQuery;
    private readonly IRunnerWorkspaceClient _workspaceClient;
    private readonly ILogger<WorkflowGrain> _log;

    private readonly record struct SessionContextUsage(double? Percent, string? SessionId)
    {
        public static readonly SessionContextUsage None = new(null, null);
    }

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        IWorkflowBacklogDirectory backlogs,
        WorkflowProfileManager profileManager,
        IWorkflowArtifactBindService artifactBindService,
        AgentSessionQuery agentSessionQuery,
        IRunnerWorkspaceClient workspaceClient,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _backlogs = backlogs;
        _profileManager = profileManager;
        _artifactBindService = artifactBindService;
        _agentSessionQuery = agentSessionQuery;
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

        // Transition to Stopped before clearing executable state so the
        // task-failed heartbeat handler (EnsureWorkHeartbeatAsync) treats the
        // run as non-runnable and does not re-dispatch checks while we are
        // abandoning in-flight work.
        var events = _run.Stop();

        await ClearExecutableStateAsync(reason ?? "stopped");

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

    // Legacy reject entry point. Kept for back-compat with any external
    // integration that still calls this method; the implementation now
    // routes through the feedback loop rather than failing the workflow.
    // Prefer RequestChangesAsync for new code.
    public async Task RejectAsync(string? reason = null)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Reject reason is required");

        _log.LogWarning(
            "Workflow {Id} received legacy RejectAsync(reason); routing through RequestChangesAsync",
            GrainKey);
        await RequestChangesAsync(reason!);
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
        var usage = await ResolveSessionContextUsageAsync(failedTaskId, stageId, ct: default);
        var verdict = WorkflowSessionHealthGate.Evaluate(usage.Percent);
        if (verdict == HealthVerdict.Block)
        {
            await ApplyContextExhaustionBlockAsync(failedTaskId, usage.Percent, stageId);
            return;
        }

        if (verdict == HealthVerdict.Warn)
        {
            _log.LogWarning(
                "Workflow {Id} retry proceeding with elevated session context usage {Percent:0.##}% (task={TaskId}, stage={Stage}, sessionId={SessionId})",
                GrainKey, usage.Percent ?? 0d, failedTaskId ?? "(none)", stageId ?? "(none)", usage.SessionId ?? "(unknown)");
        }

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

        ClearDispatchedChecks();
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

        ClearDispatchedChecks();

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
        var activeTask = FindRunningTaskByWork(workId, runnerId);
        var activeCheck = activeTask is null ? FindDispatchedCheckByWork(workId, runnerId) : null;
        if (activeTask is null && activeCheck is null) return;

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        IReadOnlyList<WorkflowEvent> events = [];

        if (activeTask is not null)
        {
            events = await ProcessTaskResultAsync(result, activeTask.Id, workId);
        }
        else
        {
            ClearCheckDispatch(activeCheck!);
            events = await ProcessCheckResultAsync(result);
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

    public Task<string?> GetClaimedRunnerIdAsync()
    {
        return Task.FromResult(_run?.Claim?.RunnerId ?? _lastKnownRunnerId);
    }

    public Task<string?> GetCurrentWorkIdAsync()
    {
        return Task.FromResult(FindRunningTask()?.WorkId ?? FindDispatchedCheck()?.DispatchWorkId);
    }

    public Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId)) return Task.FromResult<WorkflowActiveWorkView?>(null);
        var activeTask = FindRunningTask();
        var activeCheck = activeTask is null ? FindDispatchedCheck() : null;
        if (!string.Equals(activeTask?.WorkId ?? activeCheck?.DispatchWorkId, workId, StringComparison.Ordinal))
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

    private async Task<SessionContextUsage> ResolveSessionContextUsageAsync(
        string? taskId,
        string? stage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stage))
        {
            return SessionContextUsage.None;
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = GrainKey,
            [AgentSessionQueryMetadataKeys.WorkId] = taskId,
            [AgentSessionQueryMetadataKeys.Stage] = stage,
        };

        AgentSessionRecord? record;
        try
        {
            record = await _agentSessionQuery.FirstByLabelsAsync(labels, ct: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Workflow {Id} session lookup for task {TaskId} stage {Stage} failed; treating as healthy",
                GrainKey, taskId, stage);
            return SessionContextUsage.None;
        }

        if (record is null) return SessionContextUsage.None;

        try
        {
            var info = await GrainFactory.GetGrain<IAgentSessionGrain>(record.Session.Id).GetAsync();
            if (info is null) return SessionContextUsage.None;
            var percent = AgentSessionJsonHelper.ContextUsagePercent(info.ContextWindowUsed, info.ContextWindowSize);
            return new SessionContextUsage(percent, record.Session.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Workflow {Id} session grain lookup for {SessionId} failed; treating as healthy",
                GrainKey, record.Session.Id);
            return SessionContextUsage.None;
        }
    }

    private async Task ApplyContextExhaustionBlockAsync(
        string? taskId,
        double? contextUsagePercent,
        string? stage)
    {
        EnsureRun();
        var blockingEvents = _run.BlockStageWithContextExhaustion(taskId, contextUsagePercent, sessionId: null);
        _log.LogWarning(
            "Workflow {Id} retry blocked: session context at {Percent:0.##}% (task={TaskId}, stage={Stage})",
            GrainKey, contextUsagePercent ?? 0d, taskId ?? "(none)", stage ?? "(none)");
        await CommitAsync(blockingEvents);

        throw new WorkflowSessionContextExhaustedException(
            WorkflowSessionHealthGate.BuildBlockingMessage(contextUsagePercent),
            contextUsagePercent,
            stage,
            taskId);
    }

    private async Task ApplyDispatchContextExhaustionBlockAsync(
        string? taskId,
        double? contextUsagePercent,
        string? stage,
        string? sessionId)
    {
        EnsureRun();
        var blockingEvents = _run.BlockStageWithContextExhaustion(taskId, contextUsagePercent, sessionId);
        _log.LogWarning(
            "Workflow {Id} dispatch blocked: session context at {Percent:0.##}% (task={TaskId}, stage={Stage}, sessionId={SessionId})",
            GrainKey, contextUsagePercent ?? 0d, taskId ?? "(none)", stage ?? "(none)", sessionId ?? "(unknown)");
        await CommitAsync(blockingEvents);

        throw new WorkflowSessionContextExhaustedException(
            WorkflowSessionHealthGate.BuildBlockingMessage(contextUsagePercent),
            contextUsagePercent,
            stage,
            taskId);
    }

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
        var eventKey = $"{stage}:{resource}";

        if (!result.Acquired)
        {
            _announcedWaitingLocks.Add(eventKey);
            return false;
        }

        _announcedWaitingLocks.Remove(eventKey);
        _announcedAcquiredLocks.Add(eventKey);

        return true;
    }

    private async Task ReleaseStageLocksIfDoneAsync(string stage)
    {
        if (_run is null) return;
        var current = _run.Stages.FirstOrDefault(s => s.Id == stage);
        if (current is null) return;
        if (current.Status is not (StageRunStatus.Completed or StageRunStatus.Failed)) return;
        await ReleaseStageLocksAsync(stage, current.Status.ToString().ToLowerInvariant());
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
        var eventKey = $"{stage}:{resource}";
        _announcedWaitingLocks.Remove(eventKey);
        _announcedAcquiredLocks.Remove(eventKey);

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
                return await MakeDispatchAsync(work.Stage, t.Id, "task", t.Title, t.Uses, taskWith, runnerId, t.Artifacts, t.Outputs, markRunning: markRunning);

            case "checks":
                var ch = (WorkflowWork.ChecksData)work.Data;
                var checksPayload = ch.Items.Select(i => (Dictionary<string, JsonElement?>)new Dictionary<string, JsonElement?>
                {
                    ["name"] = JsonSerializer.SerializeToElement(i.Name),
                    ["title"] = JsonSerializer.SerializeToElement(i.Title),
                    ["uses"] = i.Uses is not null ? JsonSerializer.SerializeToElement(i.Uses) : null,
                    ["with"] = i.With is not null ? JsonSerializer.SerializeToElement(i.With) : null,
                }).ToList();
                return await MakeDispatchAsync(work.Stage, $"checks-{work.Stage}", "checks", $"Stage checks", uses: null, with: new Dictionary<string, JsonElement?> { ["checks"] = JsonSerializer.SerializeToElement(checksPayload) }, runnerId, markRunning: markRunning);

            default:
                return null;
        }
    }

    private async Task<WorkDispatch> MakeDispatchAsync(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with, string runnerId, TaskArtifactCapture? artifacts = null, List<TaskOutputDefinition>? outputs = null, string? workIdOverride = null, bool markRunning = true)
    {
        var dispatch = await BuildDispatchAsync(stage, logicalId, workType, title, uses, with, runnerId, artifacts, outputs, workIdOverride);
        if (!markRunning) return dispatch;
        if (workType == "task")
        {
            var currentTask = _run!.CurrentStage().Tasks.FirstOrDefault(t => t.Id == logicalId);
            if (currentTask?.Status != TaskRunStatus.Running)
            {
                var events = _run!.StartTask(dispatch.WorkId, runnerId);
                await SaveRunAsync(events);
                foreach (var e in events)
                    await On(e);
            }
        }
        else if (workType == "checks")
        {
            foreach (var check in _run!.CurrentStage().Checks.Where(c => c.Status == StageCheckStatus.Pending))
            {
                check.DispatchWorkId = dispatch.WorkId;
                check.DispatchRunnerId = runnerId;
                check.DispatchedAt = DateTimeOffset.UtcNow;
            }
            await SaveRunAsync();
        }
        _lastKnownRunnerId = runnerId;
        return dispatch;
    }

    private async Task<WorkDispatch> BuildDispatchAsync(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with, string runnerId, TaskArtifactCapture? artifacts = null, List<TaskOutputDefinition>? outputs = null, string? workIdOverride = null)
    {
        var workId = workIdOverride ?? (workType == "task" ? logicalId : $"{logicalId}:{Guid.NewGuid():N}");
        var attempt = workType == "task" ? WorkflowDispatchHelpers.TaskAttempt(logicalId) : 1;

        if (workType == "task")
        {
            var usage = await ResolveSessionContextUsageAsync(workId, stage, ct: default);
            var verdict = WorkflowSessionHealthGate.Evaluate(usage.Percent);
            if (verdict == HealthVerdict.Block)
            {
                await ApplyDispatchContextExhaustionBlockAsync(workId, usage.Percent, stage, usage.SessionId);
            }
            else if (verdict == HealthVerdict.Warn)
            {
                _log.LogWarning(
                    "Workflow {Id} dispatching task {WorkId} with elevated session context usage {Percent:0.##}% (sessionId={SessionId})",
                    GrainKey, workId, usage.Percent ?? 0d, usage.SessionId ?? "(unknown)");
            }
        }

        // --- 1. Load template + independent vars from manager (per design/workflow-template-variables.md) ---
        var template = await _profileManager.LoadTemplateAsync(GrainKey);
        var independent = await _profileManager.LoadVariablesAsync(GrainKey);
        var embedded = template.EmbeddedVariables ?? VariableBundle.Empty;
        var resolved = VariableBundle.Patch(embedded, independent);

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);

        var effectiveVarsJson = WorkflowDispatchHelpers.ResolveEffectiveStageVars(resolved, stage)
            ?? JsonSerializer.Deserialize<JsonElement>("{}");

        // Spread vars to payload top level (preserves opaque user context like "custom: { answer: 42 }")
        if (effectiveVarsJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in effectiveVarsJson.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        // Inject dispatch scope (workflow/stage/work override any same-named user vars)
        payload["vars"] = effectiveVarsJson;
        payload["workflow"] = JsonSerializer.SerializeToElement(new { runId = GrainKey }, WorkflowVariableJson.Options);
        payload["stage"] = JsonSerializer.SerializeToElement(new { name = stage }, WorkflowVariableJson.Options);
        payload["work"] = JsonSerializer.SerializeToElement(new { id = workId, type = workType, title, attempt }, WorkflowVariableJson.Options);

        // Merge runtime task outputs after dispatch scope so tasks.<id>.outputs.<name>
        // is available in ${{ }} templates and overrides lower-precedence sources.
        if (_run?.RuntimeVariables is { Count: > 0 })
        {
            payload = WorkflowDispatchHelpers.MergeRuntimeVariablesIntoPayload(payload, _run.RuntimeVariables);
        }

        // Inject minimal approvalFeedback context when dispatching a feedback task.
        // Detection is by the dispatching task's CausedByFeedbackId (set when the
        // apply-feedback runtime task was scheduled by RequestChanges).
        if (workType == "task" && _run is not null)
        {
            var stageRun = _run.Stages.FirstOrDefault(s => s.Id == stage);
            var task = stageRun?.Tasks.FirstOrDefault(t => t.Id == logicalId);
            if (task?.CausedByFeedbackId is { } feedbackId)
            {
                var feedback = _run.Feedback.FirstOrDefault(f => f.Id == feedbackId);
                if (feedback is not null)
                {
                    var issueNumber = ResolveIssueNumber();
                    var projectId = GetProjectId();
                    var feedbackObj = new
                    {
                        id = feedback.Id,
                        stage = feedback.Stage,
                        createdAt = feedback.CreatedAt.ToString("O"),
                        summary = WorkflowRunExtensions.BuildFeedbackSummary(feedback.Body),
                        command = WorkflowRunExtensions.BuildFeedbackShowCommand(issueNumber, feedback.Id, projectId),
                    };
                    payload["approvalFeedback"] = JsonSerializer.SerializeToElement(feedbackObj, WorkflowVariableJson.Options);
                }
            }
        }

        // --- 2. Load prompts from profile (independent from vars, same priority chain) ---
        var prompts = await _profileManager.LoadPromptsAsync(GrainKey);
        if (prompts.Count > 0)
        {
            var promptsMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in prompts)
                promptsMap[p.Key] = p.Body;
            payload["prompts"] = JsonSerializer.SerializeToElement(promptsMap, WorkflowVariableJson.Options);
        }

        var variables = JsonSerializer.Serialize(payload, WorkflowVariableJson.Options);

        // --- 3. Expand task.with (replaces the old ApplyStageAgentDefault) ---
        var effectiveBundle = effectiveVarsJson.ValueKind == JsonValueKind.Object
            ? new VariableBundle(effectiveVarsJson)
            : VariableBundle.Empty;

        var dispatchWith = with is not null
            ? new Dictionary<string, JsonElement?>(with, StringComparer.Ordinal)
            : null;
        dispatchWith = WorkflowProfileManager.ExpandTaskWith(effectiveBundle, dispatchWith);

        // --- 4. Inject default agent when task.with has no agent key ---
        if ((dispatchWith is null || !dispatchWith.ContainsKey("agent"))
            && effectiveVarsJson.ValueKind == JsonValueKind.Object
            && effectiveVarsJson.TryGetProperty("agent", out var effectiveAgentEl)
            && effectiveAgentEl.ValueKind == JsonValueKind.Object)
        {
            dispatchWith ??= new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            dispatchWith["agent"] = effectiveAgentEl.Clone();
        }

        var withStr = dispatchWith is not null ? JsonSerializer.Serialize(dispatchWith) : null;
        LogDispatchAgentDiagnostics(stage, workId, dispatchWith, effectiveVarsJson, resolved);

        // --- 4. Extract issueRef from context (issue/project from _variables.Json) ---
        WorkIssueRef? issueRef = WorkflowDispatchHelpers.BuildIssueRef(payload);

        var artifactsStr = artifacts is not null && !artifacts.IsEmpty
            ? JSON.Serialize(artifacts)
            : null;

        var outputsStr = outputs is not null && outputs.Count > 0
            ? JsonSerializer.Serialize(outputs)
            : null;

        return new WorkDispatch(
            WorkflowRunId: GrainKey,
            WorkId: workId,
            Uses: uses,
            With: withStr,
            Variables: variables,
            WorkType: workType,
            Stage: stage,
            Title: title,
            Issue: issueRef,
            Artifacts: artifactsStr,
            Outputs: outputsStr);
    }

    private void LogDispatchAgentDiagnostics(
        string stage,
        string workId,
        Dictionary<string, JsonElement?>? dispatchWith,
        JsonElement effectiveVarsJson,
        VariableBundle resolved)
    {
        var withModel = WorkflowDispatchHelpers.TryReadNestedString(dispatchWith, "agent", "model");
        var varsModel = WorkflowDispatchHelpers.TryReadNestedString(effectiveVarsJson, "agent", "model");
        var stageModel = WorkflowDispatchHelpers.TryReadStageAgentModel(resolved, stage);
        var source = !string.IsNullOrWhiteSpace(withModel)
            ? "with.agent.model"
            : !string.IsNullOrWhiteSpace(varsModel)
                ? "vars.agent.model"
                : !string.IsNullOrWhiteSpace(stageModel.Value)
                    ? "stage.vars.agent.model"
                    : "none";

        _log.LogInformation(
            "Workflow {WorkflowId} dispatch {WorkId} stage={Stage} agent model diagnostics: with={WithModel}, vars={VarsModel}, stageOverride={StageModel}, source={Source}",
            GrainKey,
            workId,
            stage,
            withModel ?? "(null)",
            varsModel ?? "(null)",
            stageModel.Present
                ? stageModel.Value ?? "(null override)"
                : "(missing)",
            source);
    }

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
        var runningTask = FindRunningTask();
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
                _run!.CurrentStage().Id,
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

        var dispatchedCheck = FindDispatchedCheck();
        if (dispatchedCheck is null)
            return false;

        if (!string.Equals(dispatchedCheck.DispatchRunnerId, runnerId, StringComparison.Ordinal))
            return true;

        var checkRunner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        if (!await checkRunner.IsAvailableAsync())
        {
            ClearDispatchedChecks();
            await SaveRunAsync();
            return false;
        }

        var stage = _run!.CurrentStage();
        var pendingChecks = stage.Checks
            .Where(c => c.Status == StageCheckStatus.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        var work = WorkflowWork.Checks(stage.Id, pendingChecks);
        var checksData = (WorkflowWork.ChecksData)work.Data;
        var checksPayload = checksData.Items.Select(i => (Dictionary<string, JsonElement?>)new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement(i.Name),
            ["title"] = JsonSerializer.SerializeToElement(i.Title),
            ["uses"] = i.Uses is not null ? JsonSerializer.SerializeToElement(i.Uses) : null,
            ["with"] = i.With is not null ? JsonSerializer.SerializeToElement(i.With) : null,
        }).ToList();
        var checkDispatch = await MakeDispatchAsync(
            stage.Id,
            $"checks-{stage.Id}",
            "checks",
            "Stage checks",
            uses: null,
            with: new Dictionary<string, JsonElement?> { ["checks"] = JsonSerializer.SerializeToElement(checksPayload) },
            runnerId,
            workIdOverride: dispatchedCheck.DispatchWorkId);
        await AssignRunnerWorkAsync(runnerId, checkDispatch);
        return true;
    }

    private TaskRun? FindRunningTask()
        => _run?.CurrentStage().Tasks.FirstOrDefault(t => t.Status == TaskRunStatus.Running);

    private async Task<bool> FailLostRunningTasksAsync(string? runnerId = null)
    {
        if (_run?.CurrentStageId is null)
            return false;

        var runningTask = FindRunningTask();
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

    private TaskRun? FindRunningTaskByWork(string workId, string runnerId)
        => _run?.CurrentStage().Tasks.FirstOrDefault(t =>
            t.Status == TaskRunStatus.Running
            && string.Equals(t.WorkId, workId, StringComparison.Ordinal)
            && string.Equals(t.RunnerId, runnerId, StringComparison.Ordinal));

    private StageCheck? FindDispatchedCheck()
        => _run?.CurrentStage().Checks.FirstOrDefault(c =>
            c.Status == StageCheckStatus.Pending
            && !string.IsNullOrWhiteSpace(c.DispatchWorkId));

    private StageCheck? FindDispatchedCheckByWork(string workId, string runnerId)
        => _run?.CurrentStage().Checks.FirstOrDefault(c =>
            c.Status == StageCheckStatus.Pending
            && string.Equals(c.DispatchWorkId, workId, StringComparison.Ordinal)
            && string.Equals(c.DispatchRunnerId, runnerId, StringComparison.Ordinal));

    private static void ClearCheckDispatch(StageCheck check)
    {
        check.DispatchWorkId = null;
        check.DispatchRunnerId = null;
        check.DispatchedAt = null;
    }

    private void ClearDispatchedChecks()
    {
        if (_run?.CurrentStageId is null) return;
        foreach (var check in _run.CurrentStage().Checks)
            ClearCheckDispatch(check);
    }

    private async Task ClearExecutableStateAsync(string reason)
    {
        await ReleaseCurrentStageLocksAsync(reason);
        ClearDispatchedChecks();

        var runningTask = FindRunningTask();
        if (runningTask is not null)
        {
            var events = _run!.FailTaskForStopped(reason);
            await SaveRunAsync(events);
            foreach (var e in events)
                await On(e);
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
                var repairTasks = ResolveRepairTasks(stageDef, cr);
                actions.Add(repairTasks is not null
                    ? new(cr, "repair", repairTasks)
                    : new(cr, "fail"));
                if (repairTasks is not null)
                    break;
            }
        }

        return _run!.ProcessCheckResults(actions);
    }

    private IReadOnlyList<TaskDefinition>? ResolveRepairTasks(StageDefinition? stageDef, CheckResult cr)
    {
        var checkDef = stageDef?.Checks.Find(c => c.Name == cr.Name);
        if (checkDef?.OnFailure?.Repair is not { } repair) return null;

        var repairCount = _run!.GetRepairCount(cr.Name);
        if (repairCount >= repair.Limit) return null;

        return BuildRepairTasks(cr.Name, repair, cr);
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
        var repairTasks = ResolveRequestedCheckRepairTasks(stageDef, failure.CheckName);
        if (repairTasks is null)
            return null;

        ClearDispatchedChecks();
        return _run.ScheduleCheckRepair(failure.CheckName, repairTasks, failure.Message);
    }

    private IReadOnlyList<TaskDefinition>? ResolveRequestedCheckRepairTasks(StageDefinition? stageDef, string checkName)
    {
        var checkDef = stageDef?.Checks.Find(c => c.Name == checkName);
        if (checkDef?.OnFailure?.Repair is not { } repair) return null;

        return BuildRepairTasks(checkName, repair);
    }

    private IReadOnlyList<TaskDefinition> BuildRepairTasks(string checkName, CheckFailureRepair repair, CheckResult? result = null)
    {
        var tasks = new List<TaskDefinition> { BuildRepairTask(checkName, repair.Task, result) };
        if (repair.VerifyTask is not null)
            tasks.Add(repair.VerifyTask);
        return tasks;
    }

    private TaskDefinition BuildRepairTask(string checkName, TaskDefinition repairTask, CheckResult? result = null)
    {
        JsonElement? resultJson = result is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(result));
        var repairWith = repairTask.With is not null
            ? new Dictionary<string, JsonElement?>(repairTask.With)
            : new Dictionary<string, JsonElement?>();
        if (resultJson is not null && !string.Equals(checkName, "review-passed", StringComparison.Ordinal))
            repairWith["failedCheckResult"] = resultJson;

        return new TaskDefinition(
            $"{repairTask.Id}:{_run!.GetRepairCount(checkName) + 1}",
            repairTask.Title,
            repairTask.Uses,
            repairWith);
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
            WorkflowRunStarted => OnWorkflowStartedAsync(),
            WorkflowRunResumed => OnWorkflowResumedAsync(),
            WorkflowRunPaused => OnWorkflowPausedAsync(reason),
            WorkflowRunStopped => OnWorkflowStoppedAsync(reason),
            WorkflowRunFailed => OnWorkflowFailedAsync(reason),
            WorkflowRunCompleted => OnWorkflowCompletedAsync(),
            StageStarted => EnsureWorkHeartbeatAsync(),
            StageCompleted x => ReleaseStageLocksAsync(x.Stage, "completed"),
            StageFailed x => ReleaseStageLocksAsync(x.Stage, "failed"),
            StageApprovalRequested => DisableWorkHeartbeatAsync(),
            StageApprovalResolved x => OnApprovalResolvedAsync(x),
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

    private async Task OnWorkflowStartedAsync()
    {
        await EnsureWorkHeartbeatAsync();
    }

    private async Task OnWorkflowResumedAsync()
    {
        await EnsureWorkHeartbeatAsync();
    }

    private async Task OnWorkflowPausedAsync(string? reason)
    {
        await DisableWorkHeartbeatAsync();
    }

    private async Task OnWorkflowStoppedAsync(string? reason)
    {
        await DisableWorkHeartbeatAsync();
        // Step 8 of design/eventbus.md: hook dispatch removed.
        // Side effects now flow through the bus — IssueGrain subscribes
        // to com.mohist.workflow.run.stopped (Step 5) and the
        // workflow workspace cleanup service subscribes to .completed.
    }

    private async Task OnWorkflowFailedAsync(string? reason)
    {
        await DisableWorkHeartbeatAsync();
    }

    private async Task OnWorkflowCompletedAsync()
    {
        await DisableWorkHeartbeatAsync();
    }

    private Task OnApprovalResolvedAsync(StageApprovalResolved e)
    {
        return e.Result switch
        {
            ApprovalResult.Approved => OnApprovalApprovedAsync(),
            _ => Task.CompletedTask,
        };
    }

    private async Task OnApprovalApprovedAsync()
    {
        await EnsureWorkHeartbeatAsync();
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
