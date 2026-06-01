using System.Diagnostics.CodeAnalysis;
using Mohist.Server.Infrastructure.Orleans;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Workflow.Queries;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowGrain : Grain, IWorkflowGrain
{
    private const string RunnerHeartbeatTimeoutReason = "Runner heartbeat timeout after 120s";
    private WorkflowRunProfile? _profile;
    private WorkflowRun? _run;
    private WorkLease? _lease;
    private WorkflowExecutionContext? _variables;
    private string? _lastRunnerId;
    private readonly HashSet<string> _announcedWaitingLocks = [];
    private readonly HashSet<string> _announcedAcquiredLocks = [];
    private readonly IStateStore<WorkflowRunProfile> _profileStore;
    private readonly IWorkflowRunStore _runStore;
    private readonly IStateStore<WorkLease> _leaseStore;
    private readonly IStateStore<WorkflowExecutionContext> _variablesStore;
    private readonly IEventBus _eventBus;
    private readonly IEventStore _events;
    private readonly IEnumerable<IWorkflowCompletionHook> _completionHooks;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(
        IStateStore<WorkflowRunProfile> profileStore,
        IWorkflowRunStore runStore,
        IStateStore<WorkLease> leaseStore,
        IStateStore<WorkflowExecutionContext> variablesStore,
        IEventBus eventBus,
        IEventStore events,
        IEnumerable<IWorkflowCompletionHook> completionHooks,
        ILogger<WorkflowGrain> log)
    {
        _profileStore = profileStore;
        _runStore = runStore;
        _leaseStore = leaseStore;
        _variablesStore = variablesStore;
        _eventBus = eventBus;
        _events = events;
        _completionHooks = completionHooks;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _profile = await _profileStore.LoadAsync(GrainKey);
        _run = await _runStore.LoadAsync(GrainKey);
        _lease = await _leaseStore.LoadAsync(GrainKey);
        _variables = await _variablesStore.LoadAsync(GrainKey);

        _lastRunnerId = _lease?.RunnerId;

        if (_lease is not null)
        {
            if (string.IsNullOrWhiteSpace(_lease.RunnerId) || string.IsNullOrWhiteSpace(_lease.WorkId))
            {
                _log.LogWarning("Workflow {Id} activated with incomplete lease (runner={Runner}, work={Work}). Workflow remains non-dispatchable.",
                    GrainKey, _lease.RunnerId, _lease.WorkId);
            }
            else
            {
                _log.LogInformation("Workflow {Id} activated with existing lease for runner {Runner}, work {Work}",
                    GrainKey, _lease.RunnerId, _lease.WorkId);
            }
        }
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
        await RegisterToBacklogAsync();
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
        await RegisterToBacklogIfRunnableAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        var phaseBefore = _run.Status;
        await ReleaseCurrentStageLocksIfIdleAsync("paused");
        _run.Pause();
        _log.LogInformation("Workflow {Id} paused: {Reason}", GrainKey, reason);
        EmitStageChanged("paused", reason);
        await SaveRunAsync();
        await UnscheduleInternalAsync(reason ?? "Workflow paused");
        await AppendWorkflowEventAsync("workflow_paused", "paused", reason ?? "Workflow paused");
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public Task UnscheduleAsync(string reason)
    {
        EnsureRun();
        return UnscheduleInternalAsync(reason);
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
        await RegisterToBacklogIfRunnableAsync();
        await ReleaseFromBacklogIfTerminalAsync();
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
        await RegisterToBacklogIfRunnableAsync();
        await ReleaseFromBacklogIfTerminalAsync();
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
        await RegisterToBacklogAsync();
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
        await AppendWorkflowEventAsync("workflow_rerun", "rerun", "Workflow stage rerun requested");
        await RegisterToBacklogAsync();
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
        var with = ParseWith(task.With);
        _run.AddRuntimeTask(new TaskDefinition(task.Id, task.Title, task.Uses, with), task.Stage, task.InvalidateChecks);

        var stage = _run.CurrentStageId ?? "unknown";
        _log.LogInformation("Workflow {Id} added runtime task {TaskId} at stage={Stage}", GrainKey, task.Id, stage);

        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_task_added", "added", $"Workflow task added: {task.Title}", TaskId: task.Id, Payload: task);
        await RegisterToBacklogAsync();
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

    public async Task<WorkDispatch?> GetWorkAsync(string runnerId)
    {
        if (_run is null) return null;
        if (_run.Status == WorkflowRunStatus.Paused) return null;

        var activeLease = await ReconcileLeaseAsync(runnerId);
        if (activeLease is not null)
        {
            if (!string.Equals(activeLease.RunnerId, runnerId, StringComparison.Ordinal))
            {
                _log.LogInformation(
                    "Workflow {Id} dispatch blocked for runner {Runner} by active lease owned by {Owner} for work {WorkId}",
                    GrainKey,
                    runnerId,
                    activeLease.RunnerId,
                    activeLease.WorkId);
            }
            else
            {
                _log.LogDebug(
                    "Workflow {Id} runner {Runner} polled while lease for work {WorkId} is still active; no duplicate assignment created",
                    GrainKey,
                    runnerId,
                    activeLease.WorkId);
            }

            return null;
        }

        var work = _run.NextWork();
        if (work is null) return null;

        if (!await AcquireStageLocksIfNeededAsync(work.Stage))
            return null;

        var dispatch = PrepareWork(work, runnerId);
        if (dispatch is not null)
        {
            await SaveLeaseAsync();
            await AppendWorkDispatchedEventAsync(dispatch, runnerId);
        }
        return dispatch;
    }

    private async Task<WorkLease?> ReconcileLeaseAsync(string pollingRunnerId)
    {
        var activeLease = await RestoreLeaseAsync();
        if (activeLease is null) return null;

        if (string.IsNullOrWhiteSpace(activeLease.RunnerId) || string.IsNullOrWhiteSpace(activeLease.WorkId))
            return activeLease;

        if (string.Equals(activeLease.RunnerId, pollingRunnerId, StringComparison.Ordinal))
            return activeLease;

        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogWarning(
                "Workflow {Id} cannot determine liveness for leased work {WorkId} owned by {Runner}; workflow remains recovery-blocked",
                GrainKey,
                activeLease.WorkId,
                activeLease.RunnerId);
            return activeLease;
        }

        var runner = GrainFactory.GetGrain<IRunnerGrain>(activeLease.RunnerId);
        if (await runner.IsAvailableAsync())
            return activeLease;

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(projectId));
        var runnerIds = await registry.ListRunnerIdsAsync();
        if (runnerIds.Count == 0)
        {
            _log.LogWarning(
                "Workflow {Id} cannot reconcile leased work {WorkId} owned by {Runner}; no registered runners are visible yet, workflow remains recovery-blocked",
                GrainKey,
                activeLease.WorkId,
                activeLease.RunnerId);
            return activeLease;
        }

        if (runnerIds.Contains(activeLease.RunnerId, StringComparer.Ordinal))
        {
            _log.LogWarning(
                "Workflow {Id} could not reconcile liveness for leased work {WorkId} owned by {Runner}; runner is still registered but unavailable, workflow remains recovery-blocked",
                GrainKey,
                activeLease.WorkId,
                activeLease.RunnerId);
            return activeLease;
        }

        _log.LogWarning(
            "Workflow {Id} recovering stale lease for work {WorkId} from offline runner {Runner} through timeout abandonment path",
            GrainKey,
            activeLease.WorkId,
            activeLease.RunnerId);
        await AbandonCurrentWorkAsync(activeLease.RunnerId, RunnerHeartbeatTimeoutReason);
        return _lease;
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

            var with = ApplyStageAgentDefault(ParseWith(t.With), current.Id);
            tasksToInsert.Add(new TaskDefinition(t.Id, t.Title, t.Uses, with));
        }

        _run.InsertRuntimeTasksAfter(tasksToInsert);

        _log.LogInformation("Workflow {Id} added {Count} tasks in stage {Stage}",
            GrainKey, tasksToInsert.Count, current.Id);

        await SaveRunAsync();
        await AppendWorkflowEventAsync(
            "workflow_tasks_batch_added", "batch_added",
            $"Added {tasksToInsert.Count} tasks to workflow stage {current.Id}",
            Payload: new { Count = tasksToInsert.Count });

        await RegisterToBacklogAsync();

        return new AddTasksBatchResult(GrainKey, current.Id, tasksToInsert.Count);
    }

    public async Task ReportResultAsync(string runnerId, string workId, WorkDispatchResult result)
    {
        var lease = _lease;
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
        await ReleaseFromBacklogIfTerminalAsync();
        await DispatchCompletedHookIfNeededAsync(phaseBefore);
    }

    public async Task AbandonCurrentWorkAsync(string runnerId, string reason)
    {
        var lease = _lease;
        if (lease is null) return;

        if (lease.RunnerId != runnerId)
        {
            _log.LogWarning("Workflow {Id} ignoring abandon from runner {Caller} — lease owned by {Owner}",
                GrainKey, runnerId, lease.RunnerId);
            return;
        }

        _log.LogWarning("Workflow {Id} abandoning in-flight work {WorkId} ({WorkType}): {Reason}",
            GrainKey, lease.WorkId, lease.WorkType, reason);

        await ClearAndDeleteLeaseAsync();
        await SaveRunAsync();
        await AppendWorkflowEventAsync("workflow_work_abandoned", "abandoned", reason, TaskId: lease.LogicalId, RunnerId: runnerId, Payload: new { lease.WorkId, lease.WorkType });
        await ReleaseStageLocksAsync(lease.Stage, "abandoned");
        await RegisterToBacklogIfRunnableAsync();
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

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<string?> GetRunStatusAsync()
    {
        return Task.FromResult(_run?.Status.ToString());
    }

    public Task<string?> GetAssignedRunnerIdAsync()
    {
        return GetAssignedRunnerIdCoreAsync();
    }

    private async Task<string?> GetAssignedRunnerIdCoreAsync()
    {
        await RestoreLeaseAsync();
        var runnerId = _lease?.RunnerId ?? _lastRunnerId;
        return runnerId;
    }

    public async Task<string?> GetAssignedWorkIdAsync()
    {
        await RestoreLeaseAsync();
        return _lease?.WorkId;
    }

    private async Task ReleaseFromBacklogIfTerminalAsync()
    {
        if (_run is null) return;
        if (_run.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed)
        {
            await UnscheduleInternalAsync($"Workflow {_run.Status.ToString().ToLowerInvariant()}");
            _log.LogInformation("Workflow {Id} released from scheduling state (status={Status})", GrainKey, _run.Status);
        }
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
        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.WorkflowBacklog(projectId));
        await backlog.RegisterAsync(GrainKey);
        _log.LogInformation("Workflow {Id} registered to backlog", GrainKey);
    }

    private async Task RegisterToBacklogIfRunnableAsync()
    {
        if (_run?.Status != WorkflowRunStatus.Running) return;
        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;
        if (_run.NextWork() is null) return;
        await RegisterToBacklogAsync();
    }

    private WorkDispatch? PrepareWork(WorkflowWork work, string runnerId)
    {
        switch (work.WorkType)
        {
            case "stage-init":
                var stageDef = _profile?.Definition.Stages.Find(s => s.Stage == work.Stage)
                    ?? throw new InvalidOperationException($"Workflow '{GrainKey}' has no definition for stage '{work.Stage}'");
                _run!.InitializeStage(stageDef.Tasks, stageDef.Checks);
                var nextWork = _run!.NextWork();
                return nextWork is not null ? PrepareWork(nextWork, runnerId) : null;

            case "task":
                var t = (WorkflowWork.TaskData)work.Data;
                var taskWith = t.With is not null
                    ? new Dictionary<string, JsonElement?>(t.With) { ["title"] = JsonSerializer.SerializeToElement(t.Title) }
                    : new Dictionary<string, JsonElement?> { ["title"] = JsonSerializer.SerializeToElement(t.Title) };
                return MakeDispatch(work.Stage, t.Id, "task", t.Title, t.Uses, taskWith, runnerId);

            case "checks":
                var ch = (WorkflowWork.ChecksData)work.Data;
                var checksPayload = ch.Items.Select(i => (Dictionary<string, JsonElement?>)new Dictionary<string, JsonElement?>
                {
                    ["name"] = JsonSerializer.SerializeToElement(i.Name),
                    ["title"] = JsonSerializer.SerializeToElement(i.Title),
                    ["uses"] = i.Uses is not null ? JsonSerializer.SerializeToElement(i.Uses) : null,
                    ["with"] = i.With is not null ? JsonSerializer.SerializeToElement(i.With) : null,
                }).ToList();
                return MakeDispatch(work.Stage, $"checks-{work.Stage}", "checks", $"Stage checks", uses: null, with: new Dictionary<string, JsonElement?> { ["checks"] = JsonSerializer.SerializeToElement(checksPayload) }, runnerId);

            default:
                return null;
        }
    }

    private WorkDispatch MakeDispatch(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with, string runnerId)
    {
        var workId = workType == "task" ? logicalId : $"{logicalId}:{Guid.NewGuid():N}";
        var dispatchWith = with is not null
            ? new Dictionary<string, JsonElement?>(with, StringComparer.Ordinal)
            : null;
        var attempt = workType == "task" ? TaskAttempt(logicalId) : 1;
        if (workType == "task")
        {
            dispatchWith = ApplyStageAgentDefault(dispatchWith, stage);
        }
        var withStr = dispatchWith is not null ? JsonSerializer.Serialize(dispatchWith) : null;
        var variables = _variables?.ToDispatchJson(new WorkflowDispatchContext(GrainKey, workId, workType, stage, title, attempt));
        var projectId = _variables?.String("project", "id");
        var issueId = _variables?.String("issue", "id");
        var numberStr = _variables?.String("issue", "number");
        WorkIssueRef? issueRef = projectId is not null && issueId is not null && numberStr is not null && int.TryParse(numberStr, out var num)
            ? new WorkIssueRef(projectId, issueId, num) : null;
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
        _lease = new WorkLease(workId, workType, stage, logicalId, title, runnerId);
        _lastRunnerId = runnerId;
        return dispatch;
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

    private async Task UnscheduleInternalAsync(string reason)
    {
        var activeLease = await RestoreLeaseAsync();
        await ReleaseBacklogAsync();

        if (activeLease is null)
        {
            await ClearAndDeleteLeaseAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(activeLease.RunnerId))
        {
            var runner = GrainFactory.GetGrain<IRunnerGrain>(activeLease.RunnerId);
            await runner.ReleaseAsync(GrainKey);
        }

        await ClearAndDeleteLeaseAsync();
        await AppendWorkflowEventAsync(
            "workflow_work_abandoned",
            "abandoned",
            reason,
            TaskId: activeLease.LogicalId,
            RunnerId: activeLease.RunnerId,
            Payload: new { activeLease.WorkId, activeLease.WorkType });
    }

    private async Task ReleaseBacklogAsync()
    {
        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(Mohist.Server.Infrastructure.Orleans.GrainKey.WorkflowBacklog(projectId));
        await backlog.ReleaseAsync(GrainKey);
    }

    private async Task<WorkLease?> RestoreLeaseAsync()
    {
        if (_lease is not null)
            return _lease;

        var restored = await _leaseStore.LoadAsync(GrainKey);
        if (restored is null)
            return null;

        _lease = restored;
        _lastRunnerId = restored.RunnerId;
        return restored;
    }


    private void ProcessTaskResult(WorkDispatchResult result)
    {
        if (result.Status == "completed")
            _run!.CompleteTask();
        else
            _run!.FailTask(new TaskResult("failed", result.Message));
    }

    private async Task ProcessCheckResultAsync(WorkDispatchResult result)
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
        if (resultJson is not null)
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

    private Dictionary<string, JsonElement?>? ApplyStageAgentDefault(Dictionary<string, JsonElement?>? with, string stage)
    {
        if (with is not null && with.TryGetValue("agent", out var existingAgent) && existingAgent.HasValue)
            return with;

        var agent = _variables?.StageNestedSection(stage, "vars", "agent") ?? _variables?.NestedSection("vars", "agent");
        if (!agent.HasValue)
            return with;

        with ??= new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        with["agent"] = agent.Value;
        return with;
    }

    private void EmitStageChanged(string action, string? reason = null)
    {
        if (_run is null) return;
        var current = _run.Stages.FirstOrDefault(s => s.Id == _run.CurrentStageId);
        _eventBus.Emit("stage_changed", new
        {
            workflowRunId = GrainKey,
            stage = _run.CurrentStageId,
            status = current?.Status.ToString() ?? "Unknown",
            action,
            reason,
            timestamp = DateTime.UtcNow.ToString("o"),
        });
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

    private Task AppendTaskResultEventAsync(WorkLease lease, WorkDispatchResult result) =>
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

    private Task SaveRunAsync() =>
        _run is not null
            ? _runStore.SaveAsync(_run)
            : Task.CompletedTask;

    private Task SaveLeaseAsync() =>
        _lease is not null
            ? _leaseStore.SaveAsync(GrainKey, _lease)
            : Task.CompletedTask;

    private Task SaveVariablesAsync() =>
        _variables is not null
            ? _variablesStore.SaveAsync(GrainKey, _variables)
            : Task.CompletedTask;
}
