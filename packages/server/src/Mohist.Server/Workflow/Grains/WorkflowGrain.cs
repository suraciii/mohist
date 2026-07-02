using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using System.Text.Json;
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

    /// <summary>
    /// Internal accessor exposing the in-memory run to grain-composed helpers
    /// (notably <see cref="WorkflowReadModel"/>). Exposed as a property rather
    /// than a field so the read model can stay grain-internal without forcing
    /// the field to become <c>internal</c>.
    /// </summary>
    internal WorkflowRun? RunOrNull => _run;

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
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);
        if (_run is not null && _run.ReconcileReadyStatusWithInFlightWork())
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
        var stopEvents = _run.Stop();

        await ClearExecutableStateAsync(reason ?? "stopped");
        var events = new List<WorkflowEvent>(stopEvents);

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

    public async Task<WorkItem?> PollWorkAsync(string runnerId)
    {
        if (_run is null || _run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running))
            return null;

        if (!_run.IsAssignedTo(runnerId))
            return null;

        var active = TryBuildActiveWorkItem(runnerId);
        if (active is not null)
            return active;

        if (_run.Status == WorkflowRunStatus.Running)
            return null;

        var work = _run.NextWork();
        if (work is null)
            return null;

        if (!await AcquireStageLocksIfNeededAsync(work.Stage))
            return null;

        var item = await ToWorkItemAsync(work, runnerId);
        if (item is null)
            return null;

        await SaveRunAsync();
        return item;
    }

    private WorkItem? TryBuildActiveWorkItem(string runnerId)
    {
        var run = _run!;
        var currentStage = run.CurrentStage();
        var runningTask = currentStage.RunningTask;
        if (runningTask is not null)
        {
            if (!string.Equals(runningTask.RunnerId, runnerId, StringComparison.Ordinal))
                return null;

            return WorkItem.Task(
                stage: currentStage.Id,
                id: runningTask.WorkId ?? runningTask.Id,
                title: runningTask.Title,
                uses: runningTask.Uses,
                with: runningTask.WithInput,
                artifacts: runningTask.Artifacts,
                setVars: runningTask.SetVars);
        }

        var checksWorkId = currentStage.ChecksWorkId;
        if (checksWorkId is null)
            return null;

        var pendingChecks = currentStage.Checks
            .Where(c => c.Status == StageCheckStatus.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        return WorkItem.Checks(currentStage.Id, checksWorkId, pendingChecks);
    }

    private async Task<WorkItem?> ToWorkItemAsync(WorkflowWork work, string runnerId)
    {
        switch (work.WorkType)
        {
            case "task":
            {
                var t = (WorkflowWork.TaskData)work.Data;
                var workId = await MarkTaskRunningAsync(t.Id, runnerId);
                if (workId is null) return null;
                return WorkItem.Task(
                    stage: work.Stage,
                    id: workId,
                    title: t.Title,
                    uses: t.Uses,
                    with: t.With,
                    artifacts: t.Artifacts,
                    setVars: t.SetVars,
                    recovery: t.Recovery);
            }
            case "checks":
            {
                var ch = (WorkflowWork.ChecksData)work.Data;
                var checksWorkId = MarkChecksRunning(work.Stage, ch.Items);
                return WorkItem.Checks(work.Stage, checksWorkId, ch.Items);
            }
            default:
                return null;
        }
    }

    private async Task<string?> MarkTaskRunningAsync(string logicalTaskId, string runnerId)
    {
        var current = _run!.CurrentStage();
        await _sessionHealth.CheckAndEnforceAsync(
            logicalTaskId, current.Id, GrainKey, _run!,
            events => CommitAsync(events), "dispatch", default);

        var currentTask = current.Tasks.FirstOrDefault(t => t.Id == logicalTaskId);
        if (currentTask?.Status == TaskRunStatus.Running)
        {
            _lastKnownRunnerId = runnerId;
            return currentTask.WorkId ?? logicalTaskId;
        }

        var workId = logicalTaskId;
        var events = _run!.StartTask(workId, runnerId);
        await SaveRunAsync(events);
        foreach (var e in events)
            await On(e);

        _lastKnownRunnerId = runnerId;
        return workId;
    }

    private string MarkChecksRunning(string stage, IReadOnlyList<CheckItem> items)
    {
        var checksWorkId = $"checks-{stage}:{Guid.NewGuid():N}";
        var currentStage = _run!.CurrentStage();
        currentStage.ChecksWorkId = checksWorkId;
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var check = currentStage.Checks.FirstOrDefault(c => c.Name == item.Name);
            if (check is not null)
            {
                check.Status = StageCheckStatus.Running;
                check.StartedAt = now;
            }
        }
        _run!.Status = WorkflowRunStatus.Running;
        return checksWorkId;
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

    public async Task ReportTaskOutcomeAsync(string runnerId, string workId, TaskOutcome outcome)
    {
        if (_run is null || !_run.IsAssignedTo(runnerId)) return;
        var stage = _run.CurrentStage();
        var activeTask = stage.FindRunningTaskByWork(workId, runnerId);
        if (activeTask is null)
            return;

        _log.LogInformation("Workflow {Id} received task outcome for {WorkId}: {Status} detail={Detail}",
            GrainKey, workId, outcome.Status, outcome.Detail ?? "(none)");

        var events = await ProcessTaskOutcomeAsync(outcome, activeTask.Id, workId);

        await CommitAsync(events);
    }

    public async Task ReportCheckOutcomeAsync(string runnerId, string workId, CheckOutcome outcome)
    {
        if (_run is null || !_run.IsAssignedTo(runnerId)) return;
        var currentStage = _run.CurrentStage();
        if (currentStage.ChecksWorkId is null || !string.Equals(currentStage.ChecksWorkId, workId, StringComparison.Ordinal))
            return;

        _log.LogInformation("Workflow {Id} received check outcome for stage {Stage}: {Count} results",
            GrainKey, outcome.Stage, outcome.Results.Count);

        var events = await ProcessCheckOutcomeAsync(outcome);
        currentStage.ChecksWorkId = null;
        foreach (var ch in currentStage.Checks.Where(c => c.Status == StageCheckStatus.Running))
        {
            ch.Status = StageCheckStatus.Pending;
            ch.StartedAt = null;
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

    /// <summary>
    /// Releases the sequential stage lock owned by this workflow run for the
    /// given stage. Used by both the grain's retry/rerun/stop paths (via
    /// <see cref="ReleaseCurrentStageLocksAsync"/>) and by the bus-side
    /// <c>WorkflowStageLockReleaseHandler</c> that subscribes to
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
    /// </summary>
    public async Task ReleaseStageLocksAsync(string stage, string reason)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return;

        var projectId = GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = GrainFactory.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.ReleaseAsync(new StageLockOwner(GrainKey, stage));

        // The release grain surfaces the next waiter's run id, but pull
        // scheduling rediscovers assignable runs from persisted workflow
        // state — no per-project backlog mutation is required here. The
        // previous RequeueWorkflowIdAsync was a no-op and is deleted.
        _ = result.NextWorkflowRunId;
    }

    private async Task<string?> GetSequentialLockResourceAsync(string stage)
    {
        var stageDef = await _profileManager.LoadStageSpecsAsync(GrainKey, stage);
        if (stageDef.LockBehavior is null) return null;
        if (!string.Equals(stageDef.LockBehavior, "sequential", StringComparison.OrdinalIgnoreCase))
            return null;
        return stageDef.Resources?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
    }

    private async Task<IReadOnlyList<WorkflowEvent>> ProcessTaskOutcomeAsync(
        TaskOutcome outcome, string taskRunId, string workId)
    {
        var run = _run!;
        var currentStage = run.CurrentStage();
        var currentTask = currentStage?.Tasks.FirstOrDefault(t => t.Id == taskRunId);
        var events = new List<WorkflowEvent>();

        if (outcome.Artifacts is { Count: > 0 })
        {
            // The translator already bound artifact uploads; surface each
            // reference as a recorded event so the run history sees it.
            foreach (var a in outcome.Artifacts)
            {
                events.Add(new WorkflowArtifactRecorded(GrainKey, taskRunId, a.Path, DateTimeOffset.UtcNow));
            }
        }

        if (outcome.Status == OutcomeStatus.Passed)
        {
            if (currentTask is not null)
                currentTask.Output = ParseOutputToJsonElement(outcome.Output);
            if (currentTask?.CausedByFeedbackId is { } feedbackId)
            {
                var resolved = run.ResolveFeedback(feedbackId, currentTask.Id, outcome.Output);
                if (resolved is not null)
                {
                    _log.LogInformation(
                        "Workflow {Id} resolved feedback {FeedbackId} via task {TaskId}",
                        GrainKey, feedbackId, currentTask.Id);
                }
            }
            events.AddRange(run.CompleteTask());

            if (outcome.AddTasks is { Count: > 0 } addTasks)
            {
                var current = run.CurrentStage();
                var taskDefs = addTasks.Select(t =>
                {
                    var with = WorkflowDispatchHelpers.ParseWith(t.With);
                    return new TaskDefinition(t.Id, t.Title, t.Uses, with, Recovery: t.Recovery);
                }).ToList();
                var recoveryEvents = run.AddRuntimeTasks(taskDefs);
                events.AddRange(recoveryEvents);
                _log.LogInformation(
                    "Workflow {Id} task {TaskId} produced {Count} recovery tasks",
                    GrainKey, taskRunId, addTasks.Count);
            }
        }
        else
        {
            if (currentTask is not null) currentTask.Output = ParseOutputToJsonElement(outcome.Output);
            var taskResult = new TaskResult("failed", outcome.Detail ?? outcome.Output);
            events.AddRange(run.FailTask(taskResult));
        }

        return events;
    }

    private async Task<IReadOnlyList<WorkflowEvent>> ProcessCheckOutcomeAsync(CheckOutcome outcome)
    {
        var stage = _run!.CurrentStageId!;
        var stageDef = await _profileManager.LoadStageSpecsAsync(GrainKey, stage);
        var actions = new List<CheckResultAction>(outcome.Results.Count);

        foreach (var cr in outcome.Results)
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

        var currentStage = _run.CurrentStage();
        currentStage.ChecksWorkId = null;
        foreach (var ch in currentStage.Checks.Where(c => c.Status == StageCheckStatus.Running))
        {
            ch.Status = StageCheckStatus.Pending;
            ch.StartedAt = null;
        }
        return _run.ScheduleCheckRepair(failure.CheckName, repairTasks, failure.Message);
    }

    private async Task ClearExecutableStateAsync(string reason)
    {
        await ReleaseCurrentStageLocksAsync(reason);

        var currentStage = _run?.CurrentStage();
        if (currentStage is not null)
        {
            currentStage.ChecksWorkId = null;
            foreach (var ch in currentStage.Checks.Where(c => c.Status == StageCheckStatus.Running))
            {
                ch.Status = StageCheckStatus.Pending;
                ch.StartedAt = null;
            }
        }

        var runningTask = _run?.CurrentStage().RunningTask;
        if (runningTask is not null)
        {
            var events = _run!.FailTaskForStopped(reason);
            await SaveRunAsync(events);
            return;
        }

        await SaveRunAsync();
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

        foreach (var e in events)
            await On(e, reason);
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
