using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
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
    private string? _lastRunnerId;
    private bool _runDirty;
    private IGrainReminder? _workHeartbeatReminder;
    private readonly HashSet<string> _announcedWaitingLocks = [];
    private readonly HashSet<string> _announcedAcquiredLocks = [];
    private readonly IWorkflowRunStore _runStore;
    private readonly IPersistentState<WorkLease> _leaseState;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly WorkflowProfileManager _profileManager;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly AgentSessionQuery _agentSessionQuery;
    private readonly ILogger<WorkflowGrain> _log;

    private readonly record struct SessionContextUsage(double? Percent, string? SessionId)
    {
        public static readonly SessionContextUsage None = new(null, null);
    }

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        [PersistentState("lease")] IPersistentState<WorkLease> leaseState,
        IWorkflowBacklogDirectory backlogs,
        WorkflowProfileManager profileManager,
        IWorkflowArtifactBindService artifactBindService,
        AgentSessionQuery agentSessionQuery,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _leaseState = leaseState;
        _backlogs = backlogs;
        _profileManager = profileManager;
        _artifactBindService = artifactBindService;
        _agentSessionQuery = agentSessionQuery;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);

        _lastRunnerId = _run?.Claim?.RunnerId ?? _leaseState.State?.RunnerId;
        await EnsureWorkHeartbeatAsync();
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

        await ClearExecutableStateAsync(reason ?? "stopped");
        var events = _run.Stop();

        _log.LogInformation("Workflow {Id} stopped: {Reason}", GrainKey, reason);
        await CommitAsync(events, reason);
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
        await ClearAndDeleteLeaseAsync();
        var events = await TryScheduleRequestedCheckRepairAsync() ?? _run.Retry();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events);
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        await ReleaseCurrentStageLocksAsync("rerun");
        await ClearAndDeleteLeaseAsync();
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

        await ClearChecksLeaseAsync();
        var with = ParseWith(task.With);
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
        else
        {
            _run.ClaimBy(runnerId, DateTimeOffset.UtcNow);
            _lastRunnerId = runnerId;
            await SaveRunAsync();
        }

        await RunCoreAsync();
        return new WorkflowAssignmentResult(WorkflowAssignmentStatus.Assigned, runnerId);
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

        var activeLease = _leaseState?.State;
        if (!string.IsNullOrWhiteSpace(activeLease?.WorkId))
        {
            if (!string.Equals(activeLease.RunnerId, runnerId, StringComparison.Ordinal))
                return;

            var restoredDispatch = RestoreDispatch(activeLease);
            if (restoredDispatch is not null)
                await AssignRunnerWorkAsync(runnerId, restoredDispatch);
            return;
        }

        var work = _run.NextWork();
        if (work is null)
            return;

        if (!await AcquireStageLocksIfNeededAsync(work.Stage))
            return;

        var dispatch = await PrepareWorkAsync(work, runnerId);
        if (dispatch is null)
            return;

        await SaveRunAsync();
        await SaveLeaseAsync();
        await AssignRunnerWorkAsync(runnerId, dispatch);
    }

    private async Task EnsureWorkHeartbeatAsync()
    {
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

        await ClearChecksLeaseAsync();

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

            tasksToInsert.Add(new TaskDefinition(t.Id, t.Title, t.Uses, ParseWith(t.With)));
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
        var lease = _leaseState?.State;
        if (string.IsNullOrWhiteSpace(lease?.WorkId)
            || !string.Equals(lease.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal))
            return;

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        var capturedWorkType = lease.WorkType;
        var capturedLogicalId = lease.LogicalId;
        var capturedWorkId = lease.WorkId;

        await ClearAndDeleteLeaseAsync();
        IReadOnlyList<WorkflowEvent> events = [];

        switch (capturedWorkType)
        {
            case "task":
                events = await ProcessTaskResultAsync(result, capturedLogicalId, capturedWorkId);
                break;
            case "check":
            case "checks":
                events = await ProcessCheckResultAsync(result);
                break;
        }

        await CommitAsync(events);
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
        return Task.FromResult(_run?.Claim?.RunnerId ?? _lastRunnerId);
    }

    public async Task<string?> GetCurrentWorkIdAsync()
    {
        return _leaseState?.State is { WorkId: not null and not "" } l ? l.WorkId : null;
    }

    public Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId)) return Task.FromResult<WorkflowActiveWorkView?>(null);
        var lease = _leaseState?.State;
        if (lease is null
            || string.IsNullOrWhiteSpace(lease.WorkId)
            || !string.Equals(lease.WorkId, workId, StringComparison.Ordinal))
        {
            return Task.FromResult<WorkflowActiveWorkView?>(null);
        }

        var projectId = GetProjectId();
        var issueId = GetIssueId();
        return Task.FromResult<WorkflowActiveWorkView?>(new WorkflowActiveWorkView(
            WorkId: lease.WorkId,
            WorkType: lease.WorkType,
            Stage: lease.Stage,
            TaskRunId: lease.LogicalId,
            Title: lease.Title,
            ProjectId: string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            IssueId: issueId));
    }

    public Task<WorkflowFeedbackSnapshot?> GetFeedbackAsync(string feedbackId)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(feedbackId))
            return Task.FromResult<WorkflowFeedbackSnapshot?>(null);

        var feedback = _run!.Feedback.FirstOrDefault(f => string.Equals(f.Id, feedbackId, StringComparison.Ordinal));
        if (feedback is null)
            return Task.FromResult<WorkflowFeedbackSnapshot?>(null);

        return Task.FromResult<WorkflowFeedbackSnapshot?>(ToSnapshot(feedback));
    }

    public Task<IReadOnlyList<WorkflowFeedbackSnapshot>> ListFeedbackAsync()
    {
        EnsureRun();
        var issueNumber = ResolveIssueNumber();
        var snapshots = _run!.Feedback
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => ToSnapshot(f, issueNumber))
            .ToList();
        return Task.FromResult<IReadOnlyList<WorkflowFeedbackSnapshot>>(snapshots);
    }

    private WorkflowFeedbackSnapshot ToSnapshot(ApprovalFeedback feedback) =>
        ToSnapshot(feedback, ResolveIssueNumber());

    private WorkflowFeedbackSnapshot ToSnapshot(ApprovalFeedback feedback, int? issueNumber) =>
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

    private async Task<WorkDispatch?> PrepareWorkAsync(WorkflowWork work, string runnerId)
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
                return nextWork is not null ? await PrepareWorkAsync(nextWork, runnerId) : null;

            case "task":
                var t = (WorkflowWork.TaskData)work.Data;
                var taskWith = t.With is not null
                    ? new Dictionary<string, JsonElement?>(t.With) { ["title"] = JsonSerializer.SerializeToElement(t.Title) }
                    : new Dictionary<string, JsonElement?> { ["title"] = JsonSerializer.SerializeToElement(t.Title) };
                return await MakeDispatchAsync(work.Stage, t.Id, "task", t.Title, t.Uses, taskWith, runnerId, t.Artifacts, t.Outputs);

            case "checks":
                var ch = (WorkflowWork.ChecksData)work.Data;
                var checksPayload = ch.Items.Select(i => (Dictionary<string, JsonElement?>)new Dictionary<string, JsonElement?>
                {
                    ["name"] = JsonSerializer.SerializeToElement(i.Name),
                    ["title"] = JsonSerializer.SerializeToElement(i.Title),
                    ["uses"] = i.Uses is not null ? JsonSerializer.SerializeToElement(i.Uses) : null,
                    ["with"] = i.With is not null ? JsonSerializer.SerializeToElement(i.With) : null,
                }).ToList();
                return await MakeDispatchAsync(work.Stage, $"checks-{work.Stage}", "checks", $"Stage checks", uses: null, with: new Dictionary<string, JsonElement?> { ["checks"] = JsonSerializer.SerializeToElement(checksPayload) }, runnerId);

            default:
                return null;
        }
    }

    private async Task<WorkDispatch> MakeDispatchAsync(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with, string runnerId, TaskArtifactCapture? artifacts = null, List<TaskOutputDefinition>? outputs = null)
    {
        var workId = workType == "task" ? logicalId : $"{logicalId}:{Guid.NewGuid():N}";
        var attempt = workType == "task" ? TaskAttempt(logicalId) : 1;

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

        var effectiveVarsJson = resolved.Vars.HasValue && resolved.Vars.Value.ValueKind == JsonValueKind.Object
            ? resolved.Vars.Value
            : JsonSerializer.Deserialize<JsonElement>("{}");

        // Apply resolved stage-scoped vars. Nulls in a stage override mean "inherit"
        // for dispatch-time variables; the persistent profile still keeps null as
        // the user's cleared override.
        if (resolved.Stages is not null
            && !string.IsNullOrWhiteSpace(stage)
            && resolved.Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            var stageOverlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(stageVars.Vars.Value));
            effectiveVarsJson = DeepMergeSkippingNulls(effectiveVarsJson, stageOverlay) ?? stageOverlay;
        }

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
            payload = MergeRuntimeVariablesIntoPayload(payload, _run.RuntimeVariables);
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
        WorkIssueRef? issueRef = BuildIssueRef(payload);

        var artifactsStr = artifacts is not null && !artifacts.IsEmpty
            ? JSON.Serialize(artifacts)
            : null;

        var outputsStr = outputs is not null && outputs.Count > 0
            ? JsonSerializer.Serialize(outputs)
            : null;

        var dispatch = new WorkDispatch(
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
        _leaseState.State = new WorkLease(workId, workType, stage, logicalId, title, runnerId, dispatch, DispatchedAt: DateTime.UtcNow);
        _lastRunnerId = runnerId;
        return dispatch;
    }

    private static JsonElement? DeepMergeSkippingNulls(JsonElement? @base, JsonElement? overlay)
    {
        if (!overlay.HasValue) return @base;
        if (overlay.Value.ValueKind == JsonValueKind.Null) return @base;
        if (!@base.HasValue) return overlay.Value.Clone();

        if (@base.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();
        if (overlay.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();

        using var baseDoc = JsonDocument.Parse(@base.Value.GetRawText());
        using var overlayDoc = JsonDocument.Parse(overlay.Value.GetRawText());
        var merged = MergeObjectsSkippingNulls(baseDoc.RootElement, overlayDoc.RootElement);
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(merged, WorkflowVariableJson.Options));
    }

    private static Dictionary<string, object?> MergeObjectsSkippingNulls(JsonElement @base, JsonElement overlay)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in @base.EnumerateObject())
            result[property.Name] = JsonElementToObject(property.Value);

        foreach (var property in overlay.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (property.Value.ValueKind == JsonValueKind.Object
                && @base.TryGetProperty(property.Name, out var existing)
                && existing.ValueKind == JsonValueKind.Object)
            {
                result[property.Name] = MergeObjectsSkippingNulls(existing, property.Value);
                continue;
            }

            result[property.Name] = JsonElementToObject(property.Value);
        }

        return result;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number when element.TryGetDouble(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static JsonElement BuildRuntimeVariablesElement(IReadOnlyDictionary<string, JsonElement> runtimeVariables)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in runtimeVariables)
        {
            var segments = key.Split('.');
            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!current.TryGetValue(segments[i], out var existing) || existing is not Dictionary<string, object?> dict)
                {
                    dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current[segments[i]] = dict;
                }
                current = dict;
            }
            current[segments[^1]] = JsonElementToObject(value.Clone());
        }
        return JsonSerializer.SerializeToElement(root, WorkflowVariableJson.Options);
    }

    private static Dictionary<string, JsonElement?> JsonElementToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    internal static Dictionary<string, JsonElement?> MergeRuntimeVariablesIntoPayload(
        Dictionary<string, JsonElement?> payload,
        IReadOnlyDictionary<string, JsonElement> runtimeVariables)
    {
        var runtimeElement = BuildRuntimeVariablesElement(runtimeVariables);
        var payloadElement = JsonSerializer.SerializeToElement(payload, WorkflowVariableJson.Options);
        var merged = DeepMergeSkippingNulls(payloadElement, runtimeElement) ?? payloadElement;
        return JsonElementToDictionary(merged);
    }

    private void LogDispatchAgentDiagnostics(
        string stage,
        string workId,
        Dictionary<string, JsonElement?>? dispatchWith,
        JsonElement effectiveVarsJson,
        VariableBundle resolved)
    {
        var withModel = TryReadNestedString(dispatchWith, "agent", "model");
        var varsModel = TryReadNestedString(effectiveVarsJson, "agent", "model");
        var stageModel = TryReadStageAgentModel(resolved, stage);
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

    private static string? TryReadNestedString(Dictionary<string, JsonElement?>? values, string key, string nestedKey)
    {
        if (values is null || !values.TryGetValue(key, out var value) || !value.HasValue)
            return null;
        return TryReadNestedString(value.Value, nestedKey);
    }

    private static string? TryReadNestedString(JsonElement value, string key, string nestedKey)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var nested))
            return null;
        return TryReadNestedString(nested, nestedKey);
    }

    private static string? TryReadNestedString(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var nested))
            return null;
        return nested.ValueKind == JsonValueKind.String ? nested.GetString() : null;
    }

    private static (bool Present, string? Value) TryReadStageAgentModel(VariableBundle resolved, string stage)
    {
        if (resolved.Stages is null || !resolved.Stages.TryGetValue(stage, out var stageVars) || !stageVars.Vars.HasValue)
            return (false, null);

        var vars = stageVars.Vars.Value;
        if (vars.ValueKind != JsonValueKind.Object
            || !vars.TryGetProperty("agent", out var agent)
            || agent.ValueKind != JsonValueKind.Object
            || !agent.TryGetProperty("model", out var model))
            return (false, null);

        return model.ValueKind == JsonValueKind.String
            ? (true, model.GetString())
            : (true, null);
    }

    private static WorkIssueRef? BuildIssueRef(Dictionary<string, JsonElement?> payload)
    {
        if (!payload.TryGetValue("project", out var projectEl) || !projectEl.HasValue) return null;
        if (!payload.TryGetValue("issue", out var issueEl) || !issueEl.HasValue) return null;
        if (projectEl.Value.ValueKind != JsonValueKind.Object) return null;
        if (issueEl.Value.ValueKind != JsonValueKind.Object) return null;

        if (!projectEl.Value.TryGetProperty("id", out var projectIdEl)) return null;
        if (!issueEl.Value.TryGetProperty("id", out var issueIdEl)) return null;
        if (!issueEl.Value.TryGetProperty("number", out var numberEl)) return null;

        var projectId = projectIdEl.ValueKind == JsonValueKind.String ? projectIdEl.GetString() : projectIdEl.GetRawText();
        var issueId = issueIdEl.ValueKind == JsonValueKind.String ? issueIdEl.GetString() : issueIdEl.GetRawText();
        var numberStr = numberEl.ValueKind == JsonValueKind.Number ? numberEl.GetRawText() : numberEl.GetString();

        if (projectId is null || issueId is null || !int.TryParse(numberStr, out var num))
            return null;

        return new WorkIssueRef(projectId, issueId, num);
    }

    private WorkDispatch? RestoreDispatch(WorkLease lease)
    {
        if (lease.Dispatch is not null)
            return lease.Dispatch;

        if (string.IsNullOrWhiteSpace(lease.WorkId))
            return null;

        return new WorkDispatch(
            WorkflowRunId: GrainKey,
            WorkId: lease.WorkId,
            WorkType: lease.WorkType,
            Stage: lease.Stage,
            Title: lease.Title);
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

    private static int TaskAttempt(string taskRunId)
    {
        var lastDot = taskRunId.LastIndexOf('.');
        return lastDot >= 0 && int.TryParse(taskRunId[(lastDot + 1)..], out var attempt)
            ? attempt
            : 1;
    }

    private async Task ClearAndDeleteLeaseAsync()
    {
        if (_leaseState is null) return;
        _leaseState.State = null!;
        await _leaseState.WriteStateAsync();
    }

    private async Task ClearChecksLeaseAsync()
    {
        if (_leaseState?.State is { WorkId: not null, WorkType: "check" or "checks" })
            await ClearAndDeleteLeaseAsync();
    }

    private async Task ClearExecutableStateAsync(string reason)
    {
        await ReleaseCurrentStageLocksAsync(reason);

        if (_leaseState?.State is { WorkId: not null and not "" })
            await ClearAndDeleteLeaseAsync();
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
            CaptureTaskOutputs(run, currentTask, result.CapturedOutputs);
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

    internal static void CaptureTaskOutputs(WorkflowRun run, TaskRun? task, Dictionary<string, JsonElement>? capturedOutputs)
    {
        if (task is null || capturedOutputs is null || capturedOutputs.Count == 0)
            return;

        var declaredNames = task.Outputs?.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (declaredNames is null || declaredNames.Count == 0)
            return;

        var validated = capturedOutputs
            .Where(kv => declaredNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        run.CaptureTaskOutputs(task.DefinitionId, validated);
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
        // Mirror the variable resolution path used at dispatch time
        // (MakeDispatchAsync) so the bind service sees the same
        // resolved variables the runner used. Stage-scoped vars from
        // the current stage overlay on top of the workflow-level
        // vars.
        var template = await _profileManager.LoadTemplateAsync(GrainKey);
        var independent = await _profileManager.LoadVariablesAsync(GrainKey);
        var embedded = template.EmbeddedVariables ?? VariableBundle.Empty;
        var resolved = VariableBundle.Patch(embedded, independent);

        if (!resolved.Vars.HasValue && resolved.Stages is null) return null;

        JsonElement effective = resolved.Vars.HasValue && resolved.Vars.Value.ValueKind == JsonValueKind.Object
            ? resolved.Vars.Value
            : JsonSerializer.Deserialize<JsonElement>("{}");

        var currentStage = _run?.CurrentStageId;
        if (currentStage is not null
            && resolved.Stages is not null
            && resolved.Stages.TryGetValue(currentStage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            var stageOverlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(stageVars.Vars.Value));
            effective = VariableBundle.DeepMerge(effective, stageOverlay) ?? stageOverlay;
        }

        return effective;
    }

    private async Task<IReadOnlyList<WorkflowEvent>> ProcessCheckResultAsync(WorkResult result)
    {
        var checkResults = ParseCheckResults(result.Output);
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

    private static List<CheckResult> ParseCheckResults(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return root.EnumerateArray().Select(ParseSingleCheckResult).Where(r => r is not null).Cast<CheckResult>().ToList();

            var single = ParseSingleCheckResult(root);
            return single is not null ? [single] : [];
        }
        catch
        {
            return [];
        }
    }

    private static CheckResult? ParseSingleCheckResult(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var status = element.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "fail" : "fail";
        var message = element.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
        JsonElement? output = element.TryGetProperty("output", out var outProp) ? outProp.Clone() : null;

        return new CheckResult(name!, status, message, output);
    }

    [MemberNotNull(nameof(_run))]
    private void EnsureRun()
    {
        if (_run is null)
            throw new InvalidOperationException($"Workflow '{GrainKey}' has no workflow run");
    }

    private static Dictionary<string, JsonElement?>? ParseWith(string? with) =>
        with is not null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(with) : null;

    private static Dictionary<string, Dictionary<string, string>>? BuildStageVariablesFromDefinition(WorkflowDefinition definition)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in definition.Stages)
        {
            if (stage.Variables is null || stage.Variables.Count == 0) continue;
            result[stage.Stage] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vars"] = JsonSerializer.Serialize(
                    stage.Variables.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? JsonSerializer.Deserialize<object?>(kv.Value.Value.GetRawText(), WorkflowVariableJson.Options) : null),
                    WorkflowVariableJson.Options)
            };
        }
        return result.Count == 0 ? null : result;
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

    private Task SaveLeaseAsync() =>
        _leaseState.State is not null
            ? _leaseState.WriteStateAsync()
            : Task.CompletedTask;
}
