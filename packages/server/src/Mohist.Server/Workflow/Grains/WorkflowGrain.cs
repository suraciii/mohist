using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain : Grain, IWorkflowGrain, IWorkflowGrainContext, IRemindable
{
    private WorkflowRun? _run;
    private string? _cachedAssignedWorkerId;
    private bool _runDirty;
    private bool _runReloadRequired;
    private readonly IWorkflowRunStore _runStore;
    private readonly IDispatchSnapshotStore _dispatchSnapshotStore;
    private readonly WorkflowDefinitionResolver _definitionResolver;
    private readonly WorkflowVariableResolver _variableResolver;
    private readonly IWorkflowArtifactBindService _artifactBindService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowGrain> _log;
    private readonly WorkflowReadModel _readModel;
    private readonly WorkflowStageLockCoordinator _stageLockCoordinator;
    private readonly WorkflowStageInitializer _stageInitializer;
    private readonly WorkflowWorkLifecycle _workLifecycle;
    private readonly WorkflowItemTranslator? _workflowItemTranslator;
    private readonly TimeSpan _agentResultSettlementTimeout;
    private readonly TimeSpan _runnerLossRecoveryTimeout;

    internal WorkflowGrain(
        Orleans.Runtime.IGrainContext context,
        Orleans.Runtime.IGrainRuntime runtime,
        IWorkflowRunStore runStore,
        IDispatchSnapshotStore dispatchSnapshotStore,
        WorkflowDefinitionResolver definitionResolver,
        WorkflowVariableResolver variableResolver,
        IWorkflowArtifactBindService artifactBindService,
        IOptions<WorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<WorkflowGrain> log,
        WorkflowItemTranslator? workflowItemTranslator = null)
        : base(context, runtime)
    {
        _runStore = runStore;
        _dispatchSnapshotStore = dispatchSnapshotStore;
        _definitionResolver = definitionResolver;
        _variableResolver = variableResolver;
        _artifactBindService = artifactBindService;
        _timeProvider = timeProvider;
        _log = log;
        _readModel = new WorkflowReadModel(this);
        _stageLockCoordinator = new WorkflowStageLockCoordinator(this);
        _stageInitializer = new WorkflowStageInitializer(this);
        _workLifecycle = new WorkflowWorkLifecycle(this);
        _workflowItemTranslator = workflowItemTranslator;
        _agentResultSettlementTimeout = ValidateSettlementTimeout(options.Value.AgentResultSettlementTimeout);
        _runnerLossRecoveryTimeout = ValidateRunnerLossRecoveryTimeout(options.Value.RunnerLossRecoveryTimeout);
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public WorkflowGrain(
        IWorkflowRunStore runStore,
        IDispatchSnapshotStore dispatchSnapshotStore,
        WorkflowDefinitionResolver definitionResolver,
        WorkflowVariableResolver variableResolver,
        IWorkflowArtifactBindService artifactBindService,
        IOptions<WorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<WorkflowGrain> log,
        WorkflowItemTranslator? workflowItemTranslator = null)
    {
        _runStore = runStore;
        _dispatchSnapshotStore = dispatchSnapshotStore;
        _definitionResolver = definitionResolver;
        _variableResolver = variableResolver;
        _artifactBindService = artifactBindService;
        _timeProvider = timeProvider;
        _log = log;
        _readModel = new WorkflowReadModel(this);
        _stageLockCoordinator = new WorkflowStageLockCoordinator(this);
        _stageInitializer = new WorkflowStageInitializer(this);
        _workLifecycle = new WorkflowWorkLifecycle(this);
        _workflowItemTranslator = workflowItemTranslator;
        _agentResultSettlementTimeout = ValidateSettlementTimeout(options.Value.AgentResultSettlementTimeout);
        _runnerLossRecoveryTimeout = ValidateRunnerLossRecoveryTimeout(options.Value.RunnerLossRecoveryTimeout);
    }

    WorkflowRun? IWorkflowGrainContext.RunOrNull => _run;
    string IWorkflowGrainContext.GrainKey => GrainKey;
    string? IWorkflowGrainContext.GetWorkflowProfileId() => _run?.WorkflowProfileId;
    WorkflowDefinitionResolver IWorkflowGrainContext.DefinitionResolver => _definitionResolver;
    WorkflowVariableResolver IWorkflowGrainContext.VariableResolver => _variableResolver;
    IGrainFactory IWorkflowGrainContext.Grains => GrainFactory;
    ILogger IWorkflowGrainContext.Log => _log;
    DateTimeOffset IWorkflowGrainContext.Now() => Now();
    void IWorkflowGrainContext.CacheAssignedWorkerId(string? workerId) => _cachedAssignedWorkerId = workerId;
    Task IWorkflowGrainContext.SaveAsync() => SaveRunAsync();
    Task IWorkflowGrainContext.SaveAsyncWithEvents(IReadOnlyList<WorkflowEvent> events) => SaveRunAsync(events);
    Task IWorkflowGrainContext.ReleaseCurrentStageLocks(string reason) =>
        _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);
    string IWorkflowGrainContext.GetProjectId() => GetProjectId();
    int? IWorkflowGrainContext.GetIssueNumber() => GetIssueNumber();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _run = await _runStore.LoadAsync(GrainKey);
        _runReloadRequired = false;

        await ClearStoppedRunStaleApprovalGateAsync(ct);

        await DeliverPendingSessionInterruptionAsync();
        await ReconcileAgentResultSettlementAsync();
        await ReconcileRunnerLossRecoveryAsync();

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
            if (_run.Status == WorkflowRunStatus.Created)
            {
                var resumedEvents = _run.Start(Now());
                await CommitAsync(resumedEvents);
            }
            return;
        }

        await EnsureCreatedRunAsync(context);
        var events = _run!.Start(Now());
        await CommitAsync(events);
    }

    /// <summary>
    /// durable-event startup carrying an immutable
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
            await EnsureCreatedRunAsync(context, snapshot.Workspace);

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
        var metadata = WorkflowRunLineage.NormalizeStartMetadata(input?.Metadata ?? BuildRunMetadata(input));
        RequireProjectOwnership(metadata);
        var projectId = metadata?.ProjectId;
        var issueNumber = metadata?.IssueNumber;
        await BindInitialRunAsync(
            projectId!,
            issueNumber,
            metadata?.EpicNumber,
            explicitProfileId: null,
            metadata!,
            input?.Workspace);
    }

    private async Task EnsureCreatedRunAsync(WorkflowIssueContext context, WorkspaceIdentity? workspace = null)
    {
        if (_run is not null) return;
        var metadata = WorkflowRunLineage.ForIssue(
            context.ProjectId,
            context.IssueNumber,
            context.EpicNumber,
            new WorkflowRunMetadata(null, Now()));
        workspace ??= await _variableResolver.LoadIssueWorkspaceAsync(context.ProjectId, context.IssueNumber);
        await BindInitialRunAsync(
            context.ProjectId,
            context.IssueNumber,
            context.EpicNumber,
            context.WorkflowProfileId,
            metadata,
            workspace);
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
        await CommitAsync(events);
    }

    public async Task StopAsync(string? reason = null)
    {
        EnsureRun();

        if (_run.Status == WorkflowRunStatus.Stopped
            && _run.FindCancelledAgentResultSettlementTask() is not null)
        {
            await ReconcileAgentResultSettlementCleanupAsync();
            return;
        }

        if (_run.Status is not (WorkflowRunStatus.Created or WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running or WorkflowRunStatus.AwaitingApproval or WorkflowRunStatus.Paused or WorkflowRunStatus.Failed))
            throw new InvalidOperationException($"Cannot stop workflow in {_run.Status} state");

        var cancelled = _run.CancelUnresolvedAgentTaskForStop(Now());
        if (cancelled.Count > 0)
        {
            var cancellationStopEvents = _run.Stop();
            var cancellationEvents = cancelled.Concat(cancellationStopEvents).ToArray();

            _log.LogInformation("Workflow {Id} stopped with unresolved Agent execution: {Reason}", GrainKey, reason);
            await CommitAsync(cancellationEvents);
            await ReconcileAgentResultSettlementCleanupAsync();
            return;
        }

        var stopEvents = _run.Stop();

        var abandonedWorkId = _run.CurrentStage().RunningTask?.WorkId;
        var abandonedEvents = await _workLifecycle.AbandonRunningWorkAsync(_run!, reason ?? "stopped");
        var events = abandonedEvents.Concat(stopEvents).ToArray();

        _log.LogInformation("Workflow {Id} stopped: {Reason}", GrainKey, reason);
        await CommitAsync(events);
        if (abandonedWorkId is not null)
            await DeleteSnapshotBestEffortAsync(abandonedWorkId);
    }

    public async Task ApproveAsync(string? decidedBy = null, string? displayName = null)
    {
        EnsureRun();
        var normalizedOperator = ApprovalOperatorValidation.Normalize(decidedBy);
        var normalizedDisplayName = ApprovalOperatorValidation.Normalize(displayName);
        var events = _run.Approve(Now(), normalizedOperator, normalizedDisplayName);
        _log.LogInformation("Workflow {Id} approved at stage={Stage} by {Operator}", GrainKey, _run.CurrentStageId, normalizedOperator);
        await CommitAsync(events);
    }

    public async Task<string> RequestChangesAsync(string body, string? decidedBy = null, string? displayName = null)
    {
        EnsureRun();
        var normalizedOperator = ApprovalOperatorValidation.Normalize(decidedBy);
        var normalizedDisplayName = ApprovalOperatorValidation.Normalize(displayName);
        var stage = _run.CurrentStage();
        var approval = await _definitionResolver.LoadApprovalConfigAsync(GrainKey);
        var feedbackTasks = WorkflowRunExtensions.ResolveFeedbackTasks(approval?.Feedback?.Tasks, stage.Id);
        var feedbackId = CreateFeedbackId();
        var events = _run.RequestChanges(body, feedbackId, Now(), normalizedOperator, feedbackTasks, normalizedDisplayName);
        _log.LogInformation("Workflow {Id} requested changes at stage={Stage} by {Operator}: feedback={FeedbackId}", GrainKey, stage.Id, normalizedOperator, feedbackId);
        await CommitAsync(events);
        return feedbackId;
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        ThrowIfAgentResultUnresolved();
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
        ThrowIfAgentResultUnresolved();
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
        if (_run.HasUnresolvedAgentResult())
            return WorkflowControlResult.Rejected(
                "agent_result_unresolved",
                "Cannot rerun while an Agent result remains unresolved.");
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
        ThrowIfAgentResultUnresolved();
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Runtime task requires id");
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Runtime task requires title");

        var with = WorkflowDispatchHelpers.ParseWith(task.With);
        var expect = WorkflowDispatchHelpers.ParseWith(task.Expect);
        var events = _run.AddRuntimeTask(
            new TaskDefinition(task.Id, task.Title, task.Uses ?? string.Empty, with, expect, Recovery: task.Recovery),
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

    public async Task<WorkDispatch?> StoreActiveWorkDispatchAsync(string workerId, string workId, WorkDispatch dispatch)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return null;
        if (_run.HasUnresolvedAgentResult()) return null;
        var active = _run.FindActiveWork(workId, workerId);
        if (active is null || !active.IsTask) return null;
        if (!string.Equals(dispatch.WorkflowRunId, GrainKey, StringComparison.Ordinal)
            || !string.Equals(dispatch.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(dispatch.OwnerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal)
            || dispatch.AgentJobId is not null)
            return null;

        var task = _run.CurrentStage().RunningTask;
        if (task is null || !string.Equals(task.WorkId, workId, StringComparison.Ordinal)) return null;

        var winnerJson = await _dispatchSnapshotStore.SaveFirstJsonAsync(
            GrainKey, workId, JSON.Serialize(dispatch));
        return JSON.Deserialize<WorkDispatch>(winnerJson);
    }

    public async Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request)
    {
        EnsureRun();
        ThrowIfAgentResultUnresolved();
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

            tasksToInsert.Add(new TaskDefinition(t.Id, t.Title, t.Uses ?? string.Empty, WorkflowDispatchHelpers.ParseWith(t.With), WorkflowDispatchHelpers.ParseWith(t.Expect)));
        }

        var events = _run.AddRuntimeTasks(tasksToInsert, Now());

        _log.LogInformation("Workflow {Id} added {Count} tasks in stage {Stage}",
            GrainKey, tasksToInsert.Count, current.Id);

        await CommitAsync(events);

        return new AddTasksBatchResult(GrainKey, current.Id, tasksToInsert.Count);
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
        // A blocked Agent settlement has already crossed the release boundary:
        // the attempt stops being active work even though its task stays
        // Running for late-result arbitration.
        if (_run is null || _run.HasBlockedAgentResult())
            return Task.FromResult<string?>(null);
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

    private void ThrowIfAgentResultUnresolved()
    {
        if (_run?.HasUnresolvedAgentResult() == true)
            throw new InvalidOperationException("agent_result_unresolved");
    }

    private async Task CommitAsync(
        IReadOnlyList<WorkflowEvent> events,
        CancellationToken ct = default)
    {
        if (_run is not null)
        {
            IReadOnlyList<WorkflowEvent> resolved;
            try
            {
                resolved = await _stageInitializer.InitializeFreshStagesAsync(events);
            }
            catch (WorkflowDefinitionResolutionException ex)
            {
                resolved = _run.FailDefinitionResolution(ex.Message);
                _runDirty = true;
                await SaveRunAsync(resolved);
                throw;
            }
            _runDirty = true;
            await SaveRunAsync(resolved);
            events = resolved;
        }
    }

    internal string GetProjectId() =>
        _run?.Metadata?.ProjectId ?? "";

    internal int? GetIssueNumber() =>
        _run?.Metadata?.IssueNumber is > 0 ? _run.Metadata.IssueNumber : null;

    private async Task BindInitialRunAsync(
        string projectId,
        int? issueNumber,
        int? epicNumber,
        string? explicitProfileId,
        WorkflowRunMetadata metadata,
        WorkspaceIdentity? workspace)
    {
        var result = await GrainFactory
            .GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId)
            .BindWorkflowRunAsync(
                new WorkflowProfileCommandPayload.BindWorkflowRun(
                    projectId,
                    GrainKey,
                    issueNumber,
                    epicNumber,
                    explicitProfileId,
                    metadata,
                    workspace),
                $"workflow-run:{GrainKey}:start",
                expectedRevision: null);
        if (!result.IsApplied)
        {
            throw new InvalidOperationException(result.Message ?? $"Unable to create WorkflowRun '{GrainKey}'");
        }
        _run = await _runStore.LoadAsync(GrainKey)
            ?? throw new InvalidOperationException($"WorkflowRun '{GrainKey}' binding committed without a persisted Run");
        _runDirty = false;
    }

    private WorkflowRunMetadata? BuildRunMetadata(WorkflowStartInput? input)
    {
        if (input is null) return null;
        return new WorkflowRunMetadata(input.Name, Now(), input.Labels, input.Annotations);
    }

    private void RequireProjectOwnership(WorkflowRunMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.ProjectId))
            return;

        throw new InvalidOperationException(
            $"Workflow '{GrainKey}' cannot start without the required project context.");
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

    private async Task RemoveUncommittedRunAsync()
    {
        if (_run is null) return;

        try
        {
            await _runStore.DeleteAsync(GrainKey);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "run {run} cleanup after failed startup could not remove the persisted run", GrainKey);
        }
        finally
        {
            _run = null;
            _runDirty = false;
        }
    }

    private void MarkRunReloadRequired()
    {
        _runDirty = false;
        _runReloadRequired = true;
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

    private static TimeSpan ValidateSettlementTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Workflow AgentResultSettlementTimeout must be positive.");
        return timeout;
    }

    private static TimeSpan ValidateRunnerLossRecoveryTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.FromMinutes(2))
            throw new InvalidOperationException(
                "Workflow RunnerLossRecoveryTimeout must be longer than the two-minute runner presence timeout.");
        return timeout;
    }

    private static string CreateFeedbackId() => $"fb_{Guid.NewGuid():N}";
}
