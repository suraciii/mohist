using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Orleans;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Errors;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Infrastructure.Workflow;
using Mohist.Server.Workflow.Queries;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Views;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowGrain : Grain, IWorkflowGrain, IRemindable
{
    private const string RecoveryReminderName = "workflow-scheduling-recovery";
    private static readonly TimeSpan RecoveryReminderDueTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveryReminderPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleLeaseThreshold = TimeSpan.FromMinutes(5);
    private WorkflowRunProfile? _profile;
    private WorkflowRun? _run;
    private WorkLease? _lease;
    private WorkflowExecutionContext? _variables;
    private string? _lastRunnerId;
    private IGrainReminder? _recoveryReminder;
    private readonly HashSet<string> _announcedWaitingLocks = [];
    private readonly HashSet<string> _announcedAcquiredLocks = [];
    private readonly IStateStore<WorkflowRunProfile> _profileStore;
    private readonly IWorkflowRunStore _runStore;
    private readonly IStateStore<WorkLease> _leaseStore;
    private readonly IStateStore<WorkflowExecutionContext> _variablesStore;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly IEventBus _eventBus;
    private readonly IEventStore _events;
    private readonly IEnumerable<IWorkflowCompletionHook> _completionHooks;
    private readonly WorkflowVariableResolver _variablesResolver;
    private readonly WorkflowProfileManager _profileManager;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(
        IStateStore<WorkflowRunProfile> profileStore,
        IWorkflowRunStore runStore,
        IStateStore<WorkLease> leaseStore,
        IStateStore<WorkflowExecutionContext> variablesStore,
        IWorkflowBacklogDirectory backlogs,
        IEventBus eventBus,
        IEventStore events,
        IEnumerable<IWorkflowCompletionHook> completionHooks,
        WorkflowVariableResolver variablesResolver,
        WorkflowProfileManager profileManager,
        ILogger<WorkflowGrain> log)
    {
        _profileStore = profileStore;
        _runStore = runStore;
        _leaseStore = leaseStore;
        _variablesStore = variablesStore;
        _backlogs = backlogs;
        _eventBus = eventBus;
        _events = events;
        _completionHooks = completionHooks;
        _variablesResolver = variablesResolver;
        _profileManager = profileManager;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _profile = await _profileStore.LoadAsync(GrainKey);
        _run = await _runStore.LoadAsync(GrainKey);
        _lease = await _leaseStore.LoadAsync(GrainKey);
        _variables = await _variablesStore.LoadAsync(GrainKey);

        _lastRunnerId = _run?.Claim?.RunnerId ?? _lease?.RunnerId;
        await EnsureSchedulingRecoveryAsync();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;

        await EnsureSchedulingRecoveryAsync();
    }

    public async Task StartAsync(WorkflowDefinition? definition = null, WorkflowStartInput? input = null)
    {
        var phaseBefore = _run?.Status;

        if (definition is not null)
            _profile = new WorkflowRunProfile(GrainKey, definition);

        if (_run is null)
        {
            if (_profile is null)
                throw new InvalidOperationException("Cannot start: no workflow definition provided");
            _run = WorkflowRun.Create(GrainKey, _profile.Definition,
                input is not null ? new WorkflowRunMetadata(input.Name, DateTimeOffset.MinValue, input.Labels, input.Annotations) : null);
        }

        _run.Start();
        if (!string.IsNullOrWhiteSpace(input?.Variables))
            _variables = new WorkflowExecutionContext(
                input.Variables!,
                input?.StageVariables ?? (_profile is not null ? BuildStageVariablesFromDefinition(_profile.Definition) : null));

        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run.CurrentStageId);
        EmitStageChanged("started");
        await SaveAllAsync();
        await AppendWorkflowEventAsync("workflow_started", "started", "Workflow started");
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task ResumeAsync()
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        _run.Start();
        _log.LogInformation("Workflow {Id} resumed, stage={Stage}", GrainKey, _run.CurrentStageId);
        EmitStageChanged("resumed");
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_resumed", "active", "Workflow resumed");
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        _run.Pause();
        _log.LogInformation("Workflow {Id} paused: {Reason}", GrainKey, reason);
        EmitStageChanged("paused", reason);
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_paused", "paused", reason ?? "Workflow paused");
        await DisableSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task StopAsync(string? reason = null)
    {
        EnsureRun();
        var phaseBefore = _run.Status;

        if (_run.Status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Paused))
            throw new WorkflowDomainException($"Cannot stop workflow in {_run.Status} state");

        await ClearExecutableStateAsync(reason ?? "stopped");
        _run.Stop();

        _log.LogInformation("Workflow {Id} stopped: {Reason}", GrainKey, reason);
        EmitStageChanged("stopped", reason);
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_stopped", "stopped", reason ?? "Workflow stopped");
        await DisableSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        _run.Approve();
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStageId);
        EmitStageChanged("approved");
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_approval_approved", "approved", "Workflow approval approved");
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task RejectAsync(string? reason = null)
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        _run.Reject(reason);
        _log.LogInformation("Workflow {Id} rejected at stage={Stage}: {Reason}", GrainKey, _run.CurrentStageId, reason);
        EmitStageChanged("rejected", reason);
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_approval_rejected", "rejected", reason ?? "Workflow approval rejected");
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        await ReleaseCurrentStageLocksAsync("retried");
        await ClearAndDeleteLeaseAsync();
        if (!TryScheduleRequestedCheckRepair())
            _run.Retry();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStageId);
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_retried", "retry", "Workflow retry requested");
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        await ReleaseCurrentStageLocksAsync("rerun");
        await ClearAndDeleteLeaseAsync();
        _run.Rerun();
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStageId);
        await SaveRunAsync();

        var runnerId = _run.Claim?.RunnerId;
        if (runnerId is not null)
        {
            var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
            await runner.AssignWorkAsync(new WorkDispatch(GrainKey, $"__rerun__{Guid.NewGuid():N}"));
        }

        await AppendWorkflowEventAsync("workflow_rerun", "rerun", "Workflow stage rerun requested");
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Runtime task requires id");
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Runtime task requires title");

        var phaseBefore = _run.Status;
        await ClearChecksLeaseAsync();
        var with = ParseWith(task.With);
        _run.AddRuntimeTask(new TaskDefinition(task.Id, task.Title, task.Uses, with), task.Stage, task.InvalidateChecks);

        var stage = _run.CurrentStageId ?? "unknown";
        _log.LogInformation("Workflow {Id} added runtime task {TaskId} at stage={Stage}", GrainKey, task.Id, stage);

        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_task_added", "added", $"Workflow task added: {task.Title}", TaskId: task.Id, Payload: task);
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
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
            await AppendWorkflowEventAsync("workflow_claimed", "claimed", $"Workflow assigned to runner {runnerId}", RunnerId: runnerId);
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

        var activeLease = await RestoreLeaseAsync();
        if (activeLease is not null)
        {
            if (!string.Equals(activeLease.RunnerId, runnerId, StringComparison.Ordinal))
                return;

            if (IsStaleLease(activeLease))
            {
                _log.LogWarning("Workflow {Id} stale lease detected for {WorkId} (dispatched {Age:N0}s ago), clearing",
                    GrainKey, activeLease.WorkId, (DateTime.UtcNow - activeLease.DispatchedAt!.Value).TotalSeconds);
                await ClearAndDeleteLeaseAsync();
            }
            else
            {
                var restoredDispatch = RestoreDispatch(activeLease);
                if (restoredDispatch is not null)
                    await AssignRunnerWorkAsync(runnerId, restoredDispatch);
            }
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
        await AppendWorkDispatchedEventAsync(dispatch, runnerId);
        await AssignRunnerWorkAsync(runnerId, dispatch);
    }

    private async Task EnsureSchedulingRecoveryAsync()
    {
        if (IsRunnable())
        {
            _recoveryReminder ??= await this.RegisterOrUpdateReminder(
                RecoveryReminderName,
                RecoveryReminderDueTime,
                RecoveryReminderPeriod);
            await RunCoreAsync();
            return;
        }

        await DisableSchedulingRecoveryAsync();
    }

    private async Task DisableSchedulingRecoveryAsync()
    {
        if (_recoveryReminder is null)
            return;

        await this.UnregisterReminder(_recoveryReminder);
        _recoveryReminder = null;
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

        _run.InsertRuntimeTasksAfter(tasksToInsert);

        _log.LogInformation("Workflow {Id} added {Count} tasks in stage {Stage}",
            GrainKey, tasksToInsert.Count, current.Id);

        await SaveRunAsync();
        await AppendWorkflowEventAsync(
            "workflow_tasks_batch_added", "batch_added",
            $"Added {tasksToInsert.Count} tasks to workflow stage {current.Id}",
            Payload: new { Count = tasksToInsert.Count });

        await EnsureSchedulingRecoveryAsync();

        return new AddTasksBatchResult(GrainKey, current.Id, tasksToInsert.Count);
    }

    public async Task ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        if (_run is null || !_run.IsClaimedBy(runnerId)) return;
        var lease = await RestoreReportLeaseAsync(runnerId, workId);
        if (lease is null || workId != lease.WorkId) return;

        if (lease.RunnerId != runnerId)
        {
            _log.LogWarning("Workflow {Id} ignoring report from runner {Caller} — lease owned by {Owner}",
                GrainKey, runnerId, lease.RunnerId);
            return;
        }

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        var phaseBefore = _run?.Status;
        await ClearAndDeleteLeaseAsync();

        switch (lease.WorkType)
        {
            case "task":
                ProcessTaskResult(result);
                await AppendTaskResultEventAsync(lease, result);
                break;
            case "check":
            case "checks":
                await ProcessCheckResultAsync(result);
                break;
        }

        await SaveRunAsync();
        await AppendTerminalEventIfNeededAsync();
        await ReleaseStageLocksIfDoneAsync(lease.Stage);
        await EnsureSchedulingRecoveryAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task PatchVariablesAsync(string section, string patchJson)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(section))
            throw new InvalidOperationException("Workflow variable section is required");

        _variables = (_variables ?? new WorkflowExecutionContext("{}"))
            .PatchSection(section, patchJson);

        await SaveVariablesAsync();
    }

    public async Task PatchStageVariablesAsync(string stage, string section, string patchJson)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(stage))
            throw new InvalidOperationException("Workflow stage is required");
        if (string.IsNullOrWhiteSpace(section))
            throw new InvalidOperationException("Workflow variable section is required");

        _variables = (_variables ?? new WorkflowExecutionContext("{}"))
            .PatchStageSection(stage, section, patchJson);

        await SaveVariablesAsync();
    }

    public async Task UpdateProfileDefinitionAsync(WorkflowDefinition definition)
    {
        _profile = new WorkflowRunProfile(GrainKey, definition);
        await _profileStore.SaveAsync(GrainKey, _profile);
        _log.LogInformation("Workflow {Id} profile definition updated", GrainKey);
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
        var lease = await RestoreLeaseAsync();
        return lease?.WorkId;
    }

    private async Task<bool> AcquireStageLocksIfNeededAsync(string stage)
    {
        var resource = GetSequentialLockResource(stage);
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
            if (_announcedWaitingLocks.Add(eventKey))
                await AppendWorkflowEventAsync(
                    "workflow_stage_lock_waiting",
                    "waiting",
                    $"Waiting for {resource} lock",
                    Payload: new { resource, projectId, result.OwnerWorkflowRunId, result.WaitingCount });
            return false;
        }

        _announcedWaitingLocks.Remove(eventKey);
        if (_announcedAcquiredLocks.Add(eventKey))
            await AppendWorkflowEventAsync(
                "workflow_stage_lock_acquired",
                "acquired",
                $"Acquired {resource} lock",
                Payload: new { resource, projectId, result.WaitingCount });

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

    private async Task ReleaseCurrentStageLocksIfIdleAsync(string reason)
    {
        if (_lease is not null) return;
        await ReleaseCurrentStageLocksAsync(reason);
    }

    private async Task ReleaseStageLocksAsync(string stage, string reason)
    {
        var resource = GetSequentialLockResource(stage);
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

        await AppendWorkflowEventAsync(
            "workflow_stage_lock_released",
            "released",
            $"Released {resource} lock",
            Payload: new { resource, projectId, reason, result.NextWorkflowRunId, result.WaitingCount });

        if (!string.IsNullOrWhiteSpace(result.NextWorkflowRunId))
            await RequeueWorkflowIdAsync(projectId, result.NextWorkflowRunId);
    }

    private string? GetSequentialLockResource(string stage)
    {
        var stageDef = _profile?.Definition.Stages.Find(s => s.Stage == stage);
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

    private async Task RequeueInBacklogAsync()
    {
        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;
        await RequeueWorkflowIdAsync(projectId, GrainKey);
        _log.LogInformation("Workflow {Id} re-queued in workflow backlog", GrainKey);
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
                var stageDef = _profile?.Definition.Stages.Find(s => s.Stage == work.Stage)
                    ?? throw new InvalidOperationException($"Workflow '{GrainKey}' has no definition for stage '{work.Stage}'");
                _run!.InitializeStage(stageDef.Tasks, stageDef.Checks);
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

        // --- 1. Load template + independent vars from manager ---
        var template = await _profileManager.LoadTemplateAsync(GrainKey);
        var projectId = _variables?.String("project", "id");
        var issueNumber = _variables?.String("issue", "number");
        var issueKey = projectId is not null && issueNumber is not null ? $"{projectId}:{issueNumber}" : null;
        var independent = await _profileManager.LoadVariablesAsync(GrainKey, projectId, issueKey);
        var embedded = template.EmbeddedVariables ?? Mohist.Server.Workflow.Domain.VariableBundle.Empty;
        var combinedVars = Mohist.Server.Workflow.Domain.VariableBundle.Patch(embedded, independent);

        // --- 2. Build the complete dispatch payload ---
        // Start from _variables.Json (frozen issue/project/prompts context + initial vars).
        // Then overlay: legacy stage-level vars (_variables.StageVariables),
        //   combined vars (fresh from manager), stage-level combined vars, dispatch scope.
        var payload = WorkflowExecutionContext.ParseObject(_variables?.Json ?? "{}");

        var effectiveVarsJson = payload.TryGetValue("vars", out var existingVars0)
            && existingVars0.HasValue && existingVars0.Value.ValueKind == JsonValueKind.Object
            ? existingVars0.Value
            : JsonSerializer.Deserialize<JsonElement>("{}");

        // 2a. Apply legacy stage-level vars from _variables.StageVariables (for existing workflows)
        if (_variables?.StageVariables is not null
            && !string.IsNullOrWhiteSpace(stage)
            && _variables.StageVariables.TryGetValue(stage, out var legacyStageOverrides)
            && legacyStageOverrides is not null)
        {
            if (legacyStageOverrides.TryGetValue("vars", out var legacyStageJson)
                && !string.IsNullOrWhiteSpace(legacyStageJson))
            {
                var legacyStageEl = JsonSerializer.Deserialize<JsonElement>(legacyStageJson);
                if (legacyStageEl.ValueKind == JsonValueKind.Object)
                {
                    var m = Mohist.Server.Workflow.Domain.VariableBundle.DeepMerge(effectiveVarsJson, legacyStageEl);
                    if (m.HasValue)
                    {
                        effectiveVarsJson = m.Value;
                    }
                    else if (legacyStageEl.ValueKind == JsonValueKind.Object)
                    {
                        effectiveVarsJson = legacyStageEl;
                    }
                }
            }
        }

        // 2b. Overlay fresh global vars from combined manager
        if (combinedVars.Vars.HasValue)
        {
            var overlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(combinedVars.Vars.Value));
            if (overlay.ValueKind == JsonValueKind.Object)
            {
                var merged = Mohist.Server.Workflow.Domain.VariableBundle.DeepMerge(effectiveVarsJson, overlay);
                effectiveVarsJson = merged ?? overlay;
            }
        }

        // 2c. Overlay stage-scoped vars from combined manager
        if (combinedVars.Stages is not null
            && !string.IsNullOrWhiteSpace(stage)
            && combinedVars.Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue)
        {
            var stageOverlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(stageVars.Vars.Value));
            if (stageOverlay.ValueKind == JsonValueKind.Object)
            {
                var stageMerged = Mohist.Server.Workflow.Domain.VariableBundle.DeepMerge(effectiveVarsJson, stageOverlay);
                effectiveVarsJson = stageMerged ?? stageOverlay;
            }
        }

        // Set final vars in payload + inject dispatch scope
        payload["vars"] = effectiveVarsJson;
        payload["workflow"] = JsonSerializer.SerializeToElement(new { runId = GrainKey }, WorkflowVariableJson.Options);
        payload["stage"] = JsonSerializer.SerializeToElement(new { name = stage }, WorkflowVariableJson.Options);
        payload["work"] = JsonSerializer.SerializeToElement(new { id = workId, type = workType, title, attempt }, WorkflowVariableJson.Options);

        var variables = JsonSerializer.Serialize(payload, WorkflowVariableJson.Options);

        // --- DIAGNOSTIC (remove after debug) ---
        if (variables.Contains("project/build-model"))
            File.WriteAllText($"C:\\temp\\diag-{GrainKey}.txt", variables);

        // --- DIAGNOSTIC: log variable state for debugging ---
        var buildStageJson = combinedVars.Stages is not null && combinedVars.Stages.TryGetValue(stage, out var buildStage)
            ? (buildStage.Vars.HasValue ? buildStage.Vars.Value.GetRawText() : "<null>")
            : "<no-stage>";
        var diagMsg = $"[DIAG] MakeDispatchAsync run={GrainKey} stage={stage}\n" +
                      $"  combinedVars.Vars: {(combinedVars.Vars.HasValue ? combinedVars.Vars.Value.GetRawText() : "<null>")}\n" +
                      $"  combinedVars.Stages[{stage}]: {buildStageJson}\n" +
                      $"  effectiveVarsJson: {(effectiveVarsJson.ValueKind == JsonValueKind.Object ? effectiveVarsJson.GetRawText() : $"kind:{effectiveVarsJson.ValueKind}")}\n" +
                      $"  _variables.StageVariables: {(_variables?.StageVariables is null ? "<null>" : string.Join(",", _variables.StageVariables.Keys))}\n";
        _log.LogInformation(diagMsg);
        try { System.IO.File.AppendAllText("C:\\temp\\diag.txt", diagMsg); } catch { }

        // --- 3. Expand task.with (replaces the old ApplyStageAgentDefault) ---
        var effectiveBundle = effectiveVarsJson.ValueKind == JsonValueKind.Object
            ? new Mohist.Server.Workflow.Domain.VariableBundle(effectiveVarsJson)
            : Mohist.Server.Workflow.Domain.VariableBundle.Empty;

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
        _lease = new WorkLease(workId, workType, stage, logicalId, title, runnerId, dispatch, DispatchedAt: DateTime.UtcNow);
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

    private void ClearLease()
    {
        _lease = null;
    }

    private async Task ClearAndDeleteLeaseAsync()
    {
        _lease = null;
        await _leaseStore.DeleteAsync(GrainKey);
    }

    private async Task ClearChecksLeaseAsync()
    {
        var lease = await RestoreLeaseAsync();
        if (lease?.WorkType is "check" or "checks")
            await ClearAndDeleteLeaseAsync();
    }

    private async Task ClearExecutableStateAsync(string reason)
    {
        await ReleaseCurrentStageLocksAsync(reason);

        var lease = await RestoreLeaseAsync();
        if (lease is not null)
            await ClearAndDeleteLeaseAsync();
    }

    private bool IsStaleLease(WorkLease lease)
    {
        var dispatchedAt = lease.DispatchedAt;
        if (dispatchedAt is null)
            return false;

        return DateTime.UtcNow - dispatchedAt.Value > StaleLeaseThreshold;
    }

    private async Task<WorkLease?> RestoreLeaseAsync()
    {
        if (_lease is not null)
            return _lease;

        _lease = await _leaseStore.LoadAsync(GrainKey);
        _lastRunnerId = _lease?.RunnerId;
        return _lease;
    }

    private async Task<WorkLease?> RestoreReportLeaseAsync(string runnerId, string workId)
    {
        var lease = await RestoreLeaseAsync();
        if (lease is null
            || !string.Equals(lease.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal))
            return null;
        return lease;
    }

    private void ProcessTaskResult(WorkResult result)
    {
        if (result.Status == "completed")
            _run!.CompleteTask();
        else
            _run!.FailTask(new TaskResult("failed", result.Message));
    }

    private async Task ProcessCheckResultAsync(WorkResult result)
    {
        var checkResults = ParseCheckResults(result.Output);
        if (checkResults.Count == 0)
            return;

        var stage = _run!.CurrentStageId!;
        var stageDef = _profile?.Definition.Stages.Find(s => s.Stage == stage);
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

        _run!.ProcessCheckResults(actions);

        foreach (var a in actions)
        {
            var (eventType, eventStatus) = a.Action switch
            {
                "pass" => ("workflow_check_passed", "pass"),
                "pending" => ("workflow_check_pending", "pending"),
                "repair" => ("workflow_repair_task_injected", "repairing"),
                "fail" => ("workflow_check_failed", "fail"),
                _ => (a.Action, a.Action)
            };
            await AppendWorkflowEventAsync(eventType, eventStatus, a.Result.Message ?? $"Check {a.Action}: {a.Result.Name}", CheckName: a.Result.Name, Payload: a.Result);
            if (a.Action == "fail") break;
        }
    }

    private IReadOnlyList<TaskDefinition>? ResolveRepairTasks(StageDefinition? stageDef, CheckResult cr)
    {
        var checkDef = stageDef?.Checks.Find(c => c.Name == cr.Name);
        if (checkDef?.OnFailure?.Repair is not { } repair) return null;

        var repairCount = _run!.GetRepairCount(cr.Name);
        if (repairCount >= repair.Limit) return null;

        return BuildRepairTasks(cr.Name, repair, cr);
    }

    private bool TryScheduleRequestedCheckRepair()
    {
        if (_run?.Status != WorkflowRunStatus.Failed)
            return false;

        var failure = _run.Failure;
        if (failure?.Reason != FailureReason.CheckUnrepaired || string.IsNullOrWhiteSpace(failure.CheckName))
            return false;

        var stageDef = _profile?.Definition.Stages.Find(s => s.Stage == failure.Stage);
        var repairTasks = ResolveRequestedCheckRepairTasks(stageDef, failure.CheckName);
        if (repairTasks is null)
            return false;

        _run.ScheduleCheckRepair(failure.CheckName, repairTasks, failure.Message);
        return true;
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

    private void EmitStageChanged(string action, string? reason = null)
    {
        if (_run is null) return;
        var current = _run.Stages.FirstOrDefault(s => s.Id == _run.CurrentStageId);
        _eventBus.Emit("stage_changed", new StageChangedEvent(
            GetProjectId(),
            GrainKey,
            _run.CurrentStageId,
            current?.Status.ToString() ?? "Unknown",
            action,
            reason,
            DateTime.UtcNow.ToString("o")));
    }

    private Task AppendWorkDispatchedEventAsync(WorkDispatch dispatch, string runnerId) =>
        AppendWorkflowEventAsync(
            dispatch.WorkType switch
            {
                "task" => "workflow_task_started",
                "checks" => "workflow_check_started",
                _ => "workflow_work_dispatched",
            },
            "started",
            dispatch.Title ?? "Workflow work dispatched",
            TaskId: dispatch.WorkId,
            RunnerId: runnerId,
            Payload: dispatch);

    private Task AppendTaskResultEventAsync(WorkLease lease, WorkResult result) =>
        AppendWorkflowEventAsync(
            result.Status == "completed" ? "workflow_task_completed" : "workflow_task_failed",
            result.Status,
            result.Message ?? (result.Status == "completed" ? "Workflow task completed" : "Workflow task failed"),
            TaskId: lease.LogicalId,
            RunnerId: lease.RunnerId,
            Payload: new { result.Status, result.Message, result.Output, result.ExitCode });

    private Task AppendTerminalEventIfNeededAsync()
    {
        if (_run is null) return Task.CompletedTask;
        return _run.Status switch
        {
            WorkflowRunStatus.Completed => AppendWorkflowEventAsync("workflow_completed", "completed", "Workflow completed"),
            WorkflowRunStatus.Failed => AppendWorkflowEventAsync("workflow_failed", "failed", _run.Failure?.Message ?? "Workflow failed", Payload: _run.Failure),
            WorkflowRunStatus.AwaitingApproval => AppendWorkflowEventAsync("workflow_approval_requested", "awaiting", "Workflow approval requested"),
            _ => Task.CompletedTask,
        };
    }

    private async Task DispatchCompletedHookIfNeededAsync(WorkflowRunStatus? phaseBefore)
    {
        if (_run is null) return;
        if (phaseBefore == WorkflowRunStatus.Completed || _run.Status != WorkflowRunStatus.Completed) return;

        var (projectId, issueNumber) = GetHookContext();
        var context = new WorkflowCompletionHookContext(GrainKey, projectId, issueNumber);
        foreach (var hook in _completionHooks)
        {
            try
            {
                await hook.OnCompletedAsync(context);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Workflow {WorkflowRunId} completion hook {Hook} failed", GrainKey, hook.GetType().Name);
            }
        }
    }

    private Task AppendWorkflowEventAsync(
        string type,
        string? status,
        string? message,
        string? TaskId = null,
        string? CheckName = null,
        string? RunnerId = null,
        object? Payload = null)
    {
        if (_run is null) return Task.CompletedTask;
        var projectId = GetProjectId();
        if (string.IsNullOrEmpty(projectId)) return Task.CompletedTask;
        var issueNumberStr = _variables?.String("issue", "number");
        var issueNumber = int.TryParse(issueNumberStr, out var n) ? n : 0;
        var issueId = _variables?.String("issue", "id");

        return _events.AppendAsync(new EventInput(
            projectId,
            issueNumber,
            "workflow",
            type,
            IssueId: issueId,
            WorkflowRunId: GrainKey,
            Stage: _run.CurrentStageId!,
            TaskId: TaskId,
            CheckName: CheckName,
            RunnerId: RunnerId,
            Status: status,
            Message: message,
            Payload: Payload));
    }

    private string GetProjectId() => _variables?.String("project", "id") ?? "";

    private (string ProjectId, int? IssueNumber) GetHookContext()
    {
        var projectId = _variables?.String("project", "id") ?? "";
        var numberStr = _variables?.String("issue", "number");
        var issueNumber = int.TryParse(numberStr, out var n) ? n : (int?)null;
        return (projectId, issueNumber);
    }

    private async Task SaveAllAsync()
    {
        if (_profile is not null) await _profileStore.SaveAsync(GrainKey, _profile);
        await SaveRunAsync();
        await SaveLeaseAsync();
        await SaveVariablesAsync();
    }

    private async Task SaveRunAsync()
    {
        if (_run is null) return;

        try
        {
            await _runStore.SaveAsync(_run);
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
        _lease is not null
            ? _leaseStore.SaveAsync(GrainKey, _lease)
            : Task.CompletedTask;

    private Task SaveVariablesAsync() =>
        _variables is not null
            ? _variablesStore.SaveAsync(GrainKey, _variables)
            : Task.CompletedTask;
}
