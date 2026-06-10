using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Orleans;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
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
    private readonly IPersistentState<WorkflowExecutionContext> _variablesState;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly WorkflowProfileManager _profileManager;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        [PersistentState("lease")] IPersistentState<WorkLease> leaseState,
        [PersistentState("variables")] IPersistentState<WorkflowExecutionContext> variablesState,
        IWorkflowBacklogDirectory backlogs,
        WorkflowProfileManager profileManager,
        ILogger<WorkflowGrain> log)
    {
        _runStore = runStore;
        _leaseState = leaseState;
        _variablesState = variablesState;
        _backlogs = backlogs;
        _profileManager = profileManager;
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

        await CheckLeaseAgeAsync();
        await EnsureWorkHeartbeatAsync();
    }

    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Detect a stuck lease (no runner report, no heartbeat from the runner,
    /// the workflow is still waiting on the lease) and emit a
    /// <c>com.mohist.workflow.lease-expired</c> event so subscribers
    /// (AgentSessionRunnerBridge, etc.) can mark the in-flight session
    /// failed and the workflow can re-dispatch or surface a task-failed
    /// transition.
    /// </summary>
    private async Task CheckLeaseAgeAsync()
    {
        if (_leaseState.State?.DispatchedAt is not { } dispatchedAt) return;
        var age = DateTime.UtcNow - dispatchedAt;
        if (age < LeaseTimeout) return;

        var runId = GrainKey;
        var workId = _leaseState.State.WorkId;
        var runnerId = _leaseState.State.RunnerId;
        var reason = $"lease expired after {age.TotalSeconds:F0}s without runner report";

        _log.LogWarning(
            "Workflow {RunId} lease for work {WorkId} (runner {RunnerId}) has been pending for {AgeSeconds}s; failing the run",
            runId, workId, runnerId, age.TotalSeconds);

        var current = _run?.CurrentStage();
        if (current is null) return;
        if (_run!.Status == WorkflowRunStatus.Completed
            || _run.Status == WorkflowRunStatus.Failed
            || _run.Status == WorkflowRunStatus.Stopped)
        {
            return;
        }

        current.Failure = new FailureDetails(
            FailureReason.LeaseExpired,
            current.Id,
            Message: reason);
        current.Status = StageRunStatus.Failed;
        _run.Failure = current.Failure;
        _run.Status = WorkflowRunStatus.Failed;
        _leaseState.State = null!;

        await SaveRunAsync([
            new StageFailed(current.Id, reason),
            new WorkflowRunFailed(reason),
        ]);
    }

    public async Task StartAsync(WorkflowStartInput? input = null)
    {
        var pendingVariables = !string.IsNullOrWhiteSpace(input?.Variables)
            ? new WorkflowExecutionContext(input.Variables!, input?.StageVariables)
            : null;

        if (_run is null)
        {
            _variablesState.State = pendingVariables!;
            var effectiveDefinition = await LoadEffectiveDefinitionAsync(
                input?.ProjectId ?? GetProjectId(),
                input?.IssueId ?? GetIssueId());
            _run = WorkflowRun.Create(GrainKey, effectiveDefinition,
                BuildRunMetadata(input));
        }

        var events = _run.Start();
        if (pendingVariables is not null)
            _variablesState.State = pendingVariables!;

        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run.CurrentStageId);
        await CommitAsync(events, saveVariables: pendingVariables is not null);
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

    public async Task RejectAsync(string? reason = null)
    {
        EnsureRun();
        var events = _run.Reject(reason);
        _log.LogInformation("Workflow {Id} rejected at stage={Stage}: {Reason}", GrainKey, _run.CurrentStageId, reason);
        await CommitAsync(events);
    }

    public async Task RetryAsync()
    {
        EnsureRun();
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

        await ClearAndDeleteLeaseAsync();
        IReadOnlyList<WorkflowEvent> events = [];

        switch (lease.WorkType)
        {
            case "task":
                events = ProcessTaskResult(result);
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

    public Task<string?> GetClaimedRunnerIdAsync()
    {
        return Task.FromResult(_run?.Claim?.RunnerId ?? _lastRunnerId);
    }

    public async Task<string?> GetCurrentWorkIdAsync()
    {
        return _leaseState?.State is { WorkId: not null and not "" } l ? l.WorkId : null;
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
                return await MakeDispatchAsync(work.Stage, t.Id, "task", t.Title, t.Uses, taskWith, runnerId);

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

    private async Task<WorkDispatch> MakeDispatchAsync(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with, string runnerId)
    {
        var workId = workType == "task" ? logicalId : $"{logicalId}:{Guid.NewGuid():N}";
        var attempt = workType == "task" ? TaskAttempt(logicalId) : 1;

        // --- 1. Load template + independent vars from manager (per design/workflow-template-variables.md) ---
        var template = await _profileManager.LoadTemplateAsync(GrainKey);
        var independent = await _profileManager.LoadVariablesAsync(GrainKey);
        var embedded = template.EmbeddedVariables ?? VariableBundle.Empty;
        var resolved = VariableBundle.Patch(embedded, independent);

        // --- 2. Build the complete dispatch payload ---
        // Start from _variables.Json (frozen issue/project/prompts context + initial template vars).
        // Then: merge resolved.Vars/Stages on top, inject dispatch scope.
        var payload = WorkflowExecutionContext.ParseObject(_variablesState.State?.Json ?? "{}");

        var baseVars = payload.TryGetValue("vars", out var existingVars0)
            && existingVars0.HasValue && existingVars0.Value.ValueKind == JsonValueKind.Object
            ? existingVars0.Value
            : JsonSerializer.Deserialize<JsonElement>("{}");

        // Apply resolved.Vars (3-layer independent: project + issue + workflow-run, merged with embedded template vars)
        var effectiveVarsJson = baseVars;
        if (resolved.Vars.HasValue && resolved.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            var overlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(resolved.Vars.Value));
            effectiveVarsJson = VariableBundle.DeepMerge(baseVars, overlay) ?? overlay;
        }

        // Apply resolved stage-scoped vars
        if (resolved.Stages is not null
            && !string.IsNullOrWhiteSpace(stage)
            && resolved.Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            var stageOverlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(stageVars.Vars.Value));
            effectiveVarsJson = VariableBundle.DeepMerge(effectiveVarsJson, stageOverlay) ?? stageOverlay;
        }

        // Set final vars in payload + inject dispatch scope
        payload["vars"] = effectiveVarsJson;
        payload["workflow"] = JsonSerializer.SerializeToElement(new { runId = GrainKey }, WorkflowVariableJson.Options);
        payload["stage"] = JsonSerializer.SerializeToElement(new { name = stage }, WorkflowVariableJson.Options);
        payload["work"] = JsonSerializer.SerializeToElement(new { id = workId, type = workType, title, attempt }, WorkflowVariableJson.Options);

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

        // --- 4. Extract issueRef from context (issue/project from _variables.Json) ---
        WorkIssueRef? issueRef = BuildIssueRef(payload);

        var dispatch = new WorkDispatch(
            WorkflowRunId: GrainKey,
            WorkId: workId,
            Uses: uses,
            With: withStr,
            Variables: variables,
            WorkType: workType,
            Stage: stage,
            Title: title,
            Issue: issueRef);
        _leaseState.State = new WorkLease(workId, workType, stage, logicalId, title, runnerId, dispatch, DispatchedAt: DateTime.UtcNow);
        _lastRunnerId = runnerId;
        return dispatch;
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

    private IReadOnlyList<WorkflowEvent> ProcessTaskResult(WorkResult result)
    {
        return result.Status == "completed"
            ? _run!.CompleteTask()
            : _run!.FailTask(new TaskResult("failed", result.Message));
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

    private async Task CommitAsync(IReadOnlyList<WorkflowEvent> events, string? reason = null, bool saveVariables = false, CancellationToken ct = default)
    {
        if (_run is not null)
        {
            _runDirty = true;
            await SaveRunAsync(events);
            if (saveVariables)
                await SaveVariablesAsync();
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
            TaskCompleted => EnsureWorkHeartbeatAsync(),
            TaskFailed => Task.CompletedTask,
            CheckPassed => EnsureWorkHeartbeatAsync(),
            CheckFailed => Task.CompletedTask,
            CheckPending => EnsureWorkHeartbeatAsync(),
            RepairScheduled => Task.CompletedTask,
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
        // worktree cleanup service subscribes to .completed.
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
            ApprovalResult.Rejected => OnApprovalRejectedAsync(e.Reason),
            _ => Task.CompletedTask,
        };
    }

    private async Task OnApprovalApprovedAsync()
    {
        await EnsureWorkHeartbeatAsync();
    }

    private Task OnApprovalRejectedAsync(string? reason)
    {
        return Task.CompletedTask;
    }

    private string GetProjectId() => _variablesState.State?.String("project", "id") ?? "";

    private string? GetIssueId() => _variablesState.State?.String("issue", "id");

    private string? GetIssueNumber() => _variablesState.State?.String("issue", "number");

    private WorkflowRunMetadata? BuildRunMetadata(WorkflowStartInput? input)
    {
        if (input is null) return null;

        Dictionary<string, string>? annotations = input.Annotations is not null
            ? new Dictionary<string, string>(input.Annotations, StringComparer.Ordinal)
            : null;

        var projectId = input.ProjectId ?? GetProjectId();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            annotations["projectId"] = projectId;
        }

        var issueId = input.IssueId ?? GetIssueId();
        if (!string.IsNullOrWhiteSpace(issueId))
        {
            annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            annotations["issueId"] = issueId;
        }

        var issueNumber = GetIssueNumber();
        if (!string.IsNullOrWhiteSpace(issueNumber))
        {
            annotations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            annotations["issueNumber"] = issueNumber;
        }

        return new WorkflowRunMetadata(input.Name, DateTimeOffset.UtcNow, input.Labels, annotations);
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

    private Task SaveVariablesAsync() =>
        _variablesState.State is not null
            ? _variablesState.WriteStateAsync()
            : Task.CompletedTask;
}
