using System.Text.Json;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workflow.Infrastructure;

namespace Mohist.Server.Workflow.Grains;

#pragma warning disable CS8602
public class WorkflowGrain : Grain, IWorkflowGrain
{
    private WorkflowRun? _run;
    private List<StageDefinition>? _stageDefinitions;
    private WorkLease? _lease;
    private WorkDispatch? _lastDispatch;
    private WorkflowCorrelationContext? _correlation;
    private WorkflowExecutionContext? _variables;
    private readonly IStateStore<WorkflowGrainState> _stateStore;
    private readonly IEventBus _eventBus;
    private readonly IEventStore _events;
    private readonly IEnumerable<IWorkflowCompletionHook> _completionHooks;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(
        IStateStore<WorkflowGrainState> stateStore,
        IEventBus eventBus,
        IEventStore events,
        IEnumerable<IWorkflowCompletionHook> completionHooks,
        ILogger<WorkflowGrain> log)
    {
        _stateStore = stateStore;
        _eventBus = eventBus;
        _events = events;
        _completionHooks = completionHooks;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var state = await _stateStore.LoadAsync(GrainKey);
        if (state is null) return;

        _stageDefinitions = state.StageDefinitions;
        _lease = state.Lease;
        _lastDispatch = state.LastDispatch;
        _correlation = state.Correlation;
        _variables = state.Variables;
        if (state.Run is not null && _stageDefinitions is not null)
            _run = WorkflowRun.Restore(_stageDefinitions, state.Run);
    }

    public async Task StartAsync(WorkflowDefinition? definition = null, WorkflowCorrelationContext? correlation = null, WorkflowStartInput? input = null)
    {
        var statusBefore = _run?.Status;

        if (definition is not null)
            _stageDefinitions = definition.Stages;
        if (correlation is not null)
            _correlation = correlation;

        if (_run is null && _stageDefinitions is not null)
            _run = new WorkflowRun(GrainKey, _stageDefinitions);

        if (_run is null)
            throw new InvalidOperationException("Cannot start: no workflow definition provided");

        _run.Start();
        if (!string.IsNullOrWhiteSpace(input?.Variables))
            _variables = new WorkflowExecutionContext(input.Variables!, input?.StageVariables);

        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        EmitStageChanged("started");
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_started", "started", "Workflow started");
        await RegisterToBacklogAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task ResumeAsync()
    {
        EnsureRun();
        var statusBefore = _run.Status;
        _run.Start();
        _log.LogInformation("Workflow {Id} resumed, stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        EmitStageChanged("resumed");
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_resumed", "active", "Workflow resumed");
        await RegisterToBacklogIfRunnableAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        var statusBefore = _run.Status;
        _run.Pause();
        _log.LogInformation("Workflow {Id} paused: {Reason}", GrainKey, reason);
        EmitStageChanged("paused", reason);
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_paused", "paused", reason ?? "Workflow paused");
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        var statusBefore = _run.Status;
        _run.Approve();
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        EmitStageChanged("approved");
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_approval_approved", "approved", "Workflow approval approved");
        await RegisterToBacklogIfRunnableAsync();
        await ReleaseFromBacklogIfTerminalAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task RejectAsync(string? reason = null)
    {
        EnsureRun();
        var statusBefore = _run.Status;
        var output = reason is not null
            ? JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(reason))
            : (JsonElement?)null;
        _run.Reject(new ApprovalInput(output));
        _log.LogInformation("Workflow {Id} rejected at stage={Stage}: {Reason}", GrainKey, _run.CurrentStage.Stage, reason);
        EmitStageChanged("rejected", reason);
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_approval_rejected", "rejected", reason ?? "Workflow approval rejected");
        await RegisterToBacklogIfRunnableAsync();
        await ReleaseFromBacklogIfTerminalAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        var statusBefore = _run.Status;
        ClearLease();
        _run.Retry();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_retried", "retry", "Workflow retry requested");
        await RegisterToBacklogAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        var statusBefore = _run.Status;
        ClearLease();
        _run.Rerun();
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_rerun", "rerun", "Workflow stage rerun requested");
        await RegisterToBacklogAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Runtime task requires id");
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException("Runtime task requires title");

        var statusBefore = _run.Status;
        var with = ParseWith(task.With);
        _run.AddRuntimeTask(new LoadedTaskInput(task.Id, task.Title, task.Uses, with), task.Stage, task.InvalidateChecks);

        var stage = _run.CurrentStage.Stage;
        _log.LogInformation("Workflow {Id} added runtime task {TaskId} at stage={Stage}", GrainKey, task.Id, stage);

        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_task_added", "added", $"Workflow task added: {task.Title}", TaskId: task.Id, Payload: task);
        await RegisterToBacklogAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
        return new RuntimeTaskAddedResult(GrainKey, stage, task.Id);
    }

    public Task<bool> HasIncompleteTaskUsingAsync(string uses)
    {
        EnsureRun();
        return Task.FromResult(_run.HasIncompleteTaskUsing(uses));
    }

    public Task<bool> HasIncompleteTaskIdAsync(string id)
    {
        EnsureRun();
        return Task.FromResult(_run.HasIncompleteTaskId(id));
    }

    public async Task<WorkDispatch?> GetWorkAsync(string runnerId)
    {
        if (_run is null) return null;
        if (_lease is not null) return null;

        var work = _run.GetNextWork();
        if (work is null) return null;

        var dispatch = PrepareWork(work, runnerId);
        await PersistAsync();
        if (dispatch is not null)
            await AppendWorkDispatchedEventAsync(dispatch, runnerId);
        return dispatch;
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

        var statusBefore = _run?.Status;
        ClearLease();

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
            case "load":
                ProcessLoadResult(result);
                await AppendWorkflowEventAsync(result.Status is "completed" or "loaded" ? "workflow_tasks_loaded" : "workflow_tasks_load_failed",
                    result.Status,
                    result.Message ?? "Workflow tasks load reported",
                    TaskId: lease.LogicalId,
                    RunnerId: runnerId,
                    Payload: new { result.Status, result.Message, result.Output, result.ExitCode });
                break;
        }

        await PersistAsync();
        await AppendTerminalEventIfNeededAsync();
        await ReleaseFromBacklogIfTerminalAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public async Task FailInFlightWorkAsync(string runnerId, string reason)
    {
        var lease = _lease;
        if (lease is null) return;

        if (lease.RunnerId != runnerId)
        {
            _log.LogWarning("Workflow {Id} ignoring FailInFlight from runner {Caller} — lease owned by {Owner}",
                GrainKey, runnerId, lease.RunnerId);
            return;
        }

        _log.LogWarning("Workflow {Id} failing in-flight work {WorkId} ({WorkType}): {Reason}",
            GrainKey, lease.WorkId, lease.WorkType, reason);

        var statusBefore = _run?.Status;
        ClearLease();
        _run!.FailInFlightWork(lease.WorkType, reason);

        await PersistAsync();
        await AppendWorkflowEventAsync("workflow_work_failed", "failed", reason, TaskId: lease.LogicalId, RunnerId: runnerId, Payload: new { lease.WorkId, lease.WorkType });
        await AppendTerminalEventIfNeededAsync();
        await ReleaseFromBacklogIfTerminalAsync();
        await DispatchCompletedHookIfNeededAsync(statusBefore);
    }

    public Task<WorkflowVariablesSnapshot?> GetVariablesAsync()
    {
        return Task.FromResult(_variables is null
            ? null
            : new WorkflowVariablesSnapshot(_variables.Json, _variables.StageVariables));
    }

    public async Task<WorkflowVariablesSnapshot> PatchVariablesAsync(string section, string patchJson)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(section))
            throw new InvalidOperationException("Workflow variable section is required");

        _variables = (_variables ?? new WorkflowExecutionContext("{}"))
            .PatchSection(section, patchJson);

        await PersistAsync();
        return new WorkflowVariablesSnapshot(_variables.Json, _variables.StageVariables);
    }

    public async Task<WorkflowVariablesSnapshot> PatchStageVariablesAsync(string stage, string section, string patchJson)
    {
        EnsureRun();
        if (string.IsNullOrWhiteSpace(stage))
            throw new InvalidOperationException("Workflow stage is required");
        if (string.IsNullOrWhiteSpace(section))
            throw new InvalidOperationException("Workflow variable section is required");

        _variables = (_variables ?? new WorkflowExecutionContext("{}"))
            .PatchStageSection(stage, section, patchJson);

        await PersistAsync();
        return new WorkflowVariablesSnapshot(_variables.Json, _variables.StageVariables);
    }

    public Task<string?> GetDefinitionYamlAsync()
    {
        if (_stageDefinitions is null) return Task.FromResult<string?>(null);
        var definition = new WorkflowDefinition(GrainKey, _stageDefinitions);
        return Task.FromResult<string?>(WorkflowYamlSerializer.ToYaml(definition));
    }

    public Task<WorkflowStatusSnapshot?> GetStatusAsync()
    {
        if (_run is null) return Task.FromResult<WorkflowStatusSnapshot?>(null);

        var stages = _run.Stages.Select(s =>
        {
            var stageFailure = s.Failure is not null
                ? new FailureStatusSnapshot(
                    s.Failure.Reason.ToString(),
                    s.Failure.Stage,
                    s.Failure.TaskId,
                    s.Failure.CheckName,
                    s.Failure.Message)
                : null;

            return new StageStatusSnapshot(
                s.Stage,
                s.Status.ToString(),
                s.Order,
                SnapshotTasks(s),
                SnapshotChecks(s),
                s.Approval is not null
                    ? new ApprovalStatusSnapshot(s.Approval.Status, s.Approval.Output?.ToString(), s.Approval.RequestedAt, s.Approval.RespondedAt)
                    : null,
                stageFailure);
        }).ToList();

        var pending = _lease is not null && _lastDispatch is not null
            ? new PendingWorkSnapshot(_lastDispatch.WorkId, _lastDispatch.WorkType, _lastDispatch.Stage, _lastDispatch.Title, _lastDispatch.Uses)
            : null;

        var failure = _run.Failure is not null
            ? new FailureStatusSnapshot(
                _run.Failure.Reason.ToString(),
                _run.Failure.Stage,
                _run.Failure.TaskId,
                _run.Failure.CheckName,
                _run.Failure.Message)
            : null;

        var actions = BuildAvailableActions();

        return Task.FromResult<WorkflowStatusSnapshot?>(new WorkflowStatusSnapshot(
            _run.Id,
            _run.Status.ToString(),
            _run.CurrentStage.Stage,
            stages,
            pending,
            failure,
            actions));
    }

    private List<TaskStatusSnapshot> SnapshotTasks(StageRunState stage)
    {
        if (stage.Tasks.Count > 0)
            return stage.Tasks.Select(t => new TaskStatusSnapshot(t.Id, t.Title, t.Uses, t.Status.ToString())).ToList();

        var definition = _stageDefinitions?.FirstOrDefault(d => d.Stage == stage.Stage);
        if (definition is null) return [];
        return definition.Tasks
            .Select(t => new TaskStatusSnapshot(t.Id, t.Title, t.Uses, "Pending"))
            .ToList();
    }

    private List<CheckStatusSnapshot> SnapshotChecks(StageRunState stage)
    {
        if (stage.Checks.Count > 0)
            return stage.Checks.Select(c => new CheckStatusSnapshot(c.Name, c.Title, c.Uses, c.Status.ToString(), c.Message)).ToList();

        var definition = _stageDefinitions?.FirstOrDefault(d => d.Stage == stage.Stage);
        if (definition is null) return [];
        return definition.Checks
            .Select(c => new CheckStatusSnapshot(c.Name, c.Title, c.Uses, "Pending", null))
            .ToList();
    }

    private List<AvailableActionSnapshot> BuildAvailableActions()
    {
        if (_run is null) return [];

        var actions = new List<AvailableActionSnapshot>();

        if (_run.Status == WorkflowRunStatus.AwaitingApproval)
        {
            actions.Add(new AvailableActionSnapshot("approve", "Approve", null));
            actions.Add(new AvailableActionSnapshot("reject", "Reject", null));
        }

        if (_run.Status == WorkflowRunStatus.Failed && _run.Failure is not null)
        {
            var failure = _run.Failure;
            if (failure.Reason is FailureReason.TaskFailed && failure.TaskId is not null)
            {
                actions.Add(new AvailableActionSnapshot("retry", "Retry failed task", failure.TaskId));
            }
            else if (failure.Reason is FailureReason.CheckUnrepaired && failure.CheckName is not null)
            {
                actions.Add(new AvailableActionSnapshot("retry", "Retry failed check", failure.CheckName));
            }

            actions.Add(new AvailableActionSnapshot("rerun", "Rerun stage", _run.CurrentStage.Stage));
        }

        return actions;
    }

    private async Task ReleaseFromBacklogIfTerminalAsync()
    {
        if (_run is null) return;
        if (_run.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed)
        {
            var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key);
            await backlog.ReleaseAsync(GrainKey);
            _log.LogInformation("Workflow {Id} released from backlog (status={Status})", GrainKey, _run.Status);
        }
    }

    private async Task RegisterToBacklogAsync()
    {
        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key);
        await backlog.RegisterAsync(GrainKey);
        _log.LogInformation("Workflow {Id} registered to backlog", GrainKey);
    }

    private async Task RegisterToBacklogIfRunnableAsync()
    {
        if (_run?.Status != WorkflowRunStatus.Running) return;
        if (_run.GetNextWork() is null) return;
        await RegisterToBacklogAsync();
    }

    private WorkDispatch? PrepareWork(WorkflowWork work, string runnerId)
    {
        switch (work)
        {
            case WorkflowWork.StageInit si:
                var stageDef = RequireStageDefinition(si.Stage);
                if (stageDef.TasksFrom is null)
                {
                    _run.InitTasks(MaterializeTasks(stageDef));
                    return PrepareFromDomain(runnerId);
                }
                return MakeDispatch(si.Stage, $"load-{si.Stage}", "load", $"Load tasks for {si.Stage}", stageDef.TasksFrom.Uses, stageDef.TasksFrom.With, runnerId);

            case WorkflowWork.Task t:
                return MakeDispatch(t.Stage, t.Id, "task", t.Title, t.Uses, t.With, runnerId);

            case WorkflowWork.Checks ch:
                var checksPayload = ch.Items.Select(i => (Dictionary<string, JsonElement?>)new Dictionary<string, JsonElement?>
                {
                    ["name"] = JsonSerializer.SerializeToElement(i.Name),
                    ["title"] = JsonSerializer.SerializeToElement(i.Title),
                    ["uses"] = i.Uses is not null ? JsonSerializer.SerializeToElement(i.Uses) : null,
                    ["with"] = i.With is not null ? JsonSerializer.SerializeToElement(i.With) : null,
                }).ToList();
                return MakeDispatch(ch.Stage, $"checks-{ch.Stage}", "checks", $"Stage checks", uses: null, with: new Dictionary<string, JsonElement?> { ["checks"] = JsonSerializer.SerializeToElement(checksPayload) }, runnerId);

            default:
                return null;
        }
    }

    private WorkDispatch? PrepareFromDomain(string runnerId)
    {
        var work = _run.GetNextWork();
        return work is not null ? PrepareWork(work, runnerId) : null;
    }

    private WorkDispatch MakeDispatch(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with, string runnerId)
    {
        var workId = workType == "task" ? logicalId : $"{logicalId}:{Guid.NewGuid():N}";
        var withStr = with is not null ? JsonSerializer.Serialize(with) : null;
        var attempt = ResolveWorkAttempt(stage, logicalId, workType);
        var variables = _variables?.ToDispatchJson(new WorkflowDispatchContext(GrainKey, workId, workType, stage, title, attempt));
        var dispatch = new WorkDispatch(
            WorkflowRunId: GrainKey,
            WorkId: workId,
            Uses: uses,
            With: withStr,
            Variables: variables,
            WorkType: workType,
            Stage: stage,
            Title: title,
            Issue: _correlation?.OwnerType == "issue" && _correlation.ProjectId is not null && _correlation.OwnerId is not null && _correlation.OwnerNumber is not null
                ? new WorkIssueRef(_correlation.ProjectId, _correlation.OwnerId, _correlation.OwnerNumber.Value)
                : null);
        _lease = new WorkLease(workId, workType, stage, logicalId, runnerId);
        _lastDispatch = dispatch;
        return dispatch;
    }

    private void ClearLease()
    {
        _lease = null;
        _lastDispatch = null;
    }

    private void ProcessTaskResult(WorkDispatchResult result)
    {
        if (result.Status == "completed")
        {
            TryAddRequestedTaskFromCurrentTask();
            _run.CompleteTask();
        }
        else
        {
            if (TryAddRequestedTask(result))
                _run.CompleteTask();
            else
                _run.FailTask(new TaskResult("failed", result.Message));
        }
    }

    private bool TryAddRequestedTask(WorkDispatchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Output)) return false;

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (!TryParseRequestedTask(document.RootElement, out var task)) return false;
            _run.AddRuntimeTask(task);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryAddRequestedTaskFromCurrentTask()
    {
        var with = _run.CurrentStage.CurrentTask?.WithInput;
        if (with is null || !with.TryGetValue("requestedTask", out var requested) || requested is null)
            return false;
        if (requested.Value.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryParseRequestedTask(new Dictionary<string, JsonElement?> { ["requestedTask"] = requested }, out var task))
            return false;

        _run.AddRuntimeTask(task);
        return true;
    }

    private static bool TryParseRequestedTask(JsonElement root, out LoadedTaskInput task)
    {
        task = default!;
        if (!root.TryGetProperty("requestedTask", out var requested) || requested.ValueKind != JsonValueKind.Object)
            return false;

        var id = requested.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return false;

        var title = requested.TryGetProperty("title", out var titleProp)
            ? titleProp.GetString()
            : id;
        var uses = requested.TryGetProperty("uses", out var usesProp)
            ? usesProp.GetString()
            : null;
        var with = requested.TryGetProperty("with", out var withProp) && withProp.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(withProp.GetRawText())
            : null;

        if (requested.TryGetProperty("then", out var thenProp) && thenProp.ValueKind == JsonValueKind.Object)
        {
            with ??= [];
            with["requestedTask"] = thenProp.Clone();
        }

        task = new LoadedTaskInput(id!, title ?? id!, uses, with);
        return true;
    }

    private static bool TryParseRequestedTask(Dictionary<string, JsonElement?> root, out LoadedTaskInput task)
    {
        task = default!;
        if (!root.TryGetValue("requestedTask", out var requested) || requested is null)
            return false;
        return TryParseRequestedTask(JsonSerializer.SerializeToElement(new { requestedTask = requested.Value }), out task);
    }

    private async Task ProcessCheckResultAsync(WorkDispatchResult result)
    {
        var work = _run.CurrentStage;
        var stage = work.Stage;

        var checkResults = ParseCheckResults(result.Output);
        if (checkResults.Count == 0)
            return;

        foreach (var cr in checkResults)
        {
            if (cr.Status == "pass")
            {
                _run.PassCheck(cr);
                await AppendWorkflowEventAsync("workflow_check_passed", "pass", cr.Message ?? $"Check passed: {cr.Name}", CheckName: cr.Name, Payload: cr);
            }
            else if (cr.Status == "pending")
            {
                _run.PendingCheck(cr);
                await AppendWorkflowEventAsync("workflow_check_pending", "pending", cr.Message ?? $"Check pending: {cr.Name}", CheckName: cr.Name, Payload: cr);
            }
            else
            {
                var injected = TryInjectRetryTask(stage, cr.Name, cr);
                if (injected)
                {
                    _run.ResetCheck(cr);
                    _run.ClearStageFailure();
                    await AppendWorkflowEventAsync("workflow_retry_task_injected", "retrying", cr.Message ?? $"Retry task injected for check: {cr.Name}", CheckName: cr.Name, Payload: cr);
                }
                else
                {
                    _run.FailCheck(cr);
                    await AppendWorkflowEventAsync("workflow_check_failed", "fail", cr.Message ?? $"Check failed: {cr.Name}", CheckName: cr.Name, Payload: cr);
                    return;
                }
            }
        }
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

    private void ProcessLoadResult(WorkDispatchResult result)
    {
        switch (result.Status)
        {
            case "completed":
            case "loaded":
                var stageDef = RequireStageDefinition(_run.CurrentStage.Stage);
                _run.InitTasks(MaterializeTasks(stageDef, ParseLoadedTasks(result.Output)));
                break;
            default:
                _run.FailStage(result.Message ?? "Task loading failed");
                break;
        }
    }

    private bool TryInjectRetryTask(string stage, string checkName, CheckResult result)
    {
        var stageDef = _stageDefinitions?.Find(s => s.Stage == stage);
        if (stageDef is null) return false;

        var checkDef = stageDef.Checks.Find(c => c.Name == checkName);
        if (checkDef?.OnFailure?.Retry is not { } retry) return false;

        var retryCount = _run!.RetryCountForCheck(checkName);
        if (retryCount >= retry.Limit) return false;

        var resultJson = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(result));
        var retryWith = retry.Task.With is not null
            ? new Dictionary<string, JsonElement?>(retry.Task.With) { ["failedCheckResult"] = resultJson }
            : new Dictionary<string, JsonElement?> { ["failedCheckResult"] = resultJson };
        _run.InjectRetryTask(checkName, new LoadedTaskInput(
            $"{retry.Task.Id}:{checkName}:{retryCount + 1}",
            retry.Task.Title,
            retry.Task.Uses,
            retryWith));

        return true;
    }

    private void EnsureRun()
    {
        if (_run is null)
            throw new InvalidOperationException($"Workflow '{GrainKey}' has no workflow run");
    }

    private StageDefinition RequireStageDefinition(string stage) =>
        _stageDefinitions?.Find(s => s.Stage == stage)
        ?? throw new InvalidOperationException($"Workflow '{GrainKey}' has no definition for stage '{stage}'");

    private static List<LoadedTaskInput> MaterializeTasks(StageDefinition stage, List<LoadedTaskInput>? dynamicTasks = null)
    {
        var tasks = stage.Tasks
            .Select(t => new LoadedTaskInput(t.Id, t.Title, t.Uses, t.With))
            .ToList();

        if (dynamicTasks is not null)
            tasks.AddRange(dynamicTasks);

        return tasks;
    }

    private static List<LoadedTaskInput> ParseLoadedTasks(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var taskArray = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("tasks", out var tasksProperty) && tasksProperty.ValueKind == JsonValueKind.Array
                ? tasksProperty
                : default;

        if (taskArray.ValueKind != JsonValueKind.Array) return [];

        var tasks = new List<LoadedTaskInput>();
        foreach (var item in taskArray.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProperty)
                ? idProperty.GetString()
                : item.TryGetProperty("taskId", out var taskIdProperty)
                    ? taskIdProperty.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var title = item.TryGetProperty("title", out var titleProperty)
                ? titleProperty.GetString()
                : id;
            var uses = item.TryGetProperty("uses", out var usesProperty)
                ? usesProperty.GetString()
                : null;
            var with = item.TryGetProperty("with", out var withProperty) && withProperty.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(withProperty.GetRawText())
                : null;

            tasks.Add(new LoadedTaskInput(id, title ?? id, uses, with));
        }

        return tasks;
    }

    private static Dictionary<string, JsonElement?>? ParseWith(string? with) =>
        with is not null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(with) : null;

    private int ResolveWorkAttempt(string stage, string logicalId, string workType)
    {
        if (_run is null || workType != "task") return 1;

        var current = _run.Stages.LastOrDefault(s => s.Stage == stage);
        if (current is null) return 1;

        var marker = $"{logicalId}.";
        var task = current.Tasks.LastOrDefault(t => t.Id.StartsWith(marker, StringComparison.Ordinal));
        if (task is null) return 1;

        return int.TryParse(task.Id[marker.Length..], out var attempt) ? attempt : 1;
    }

    private void EmitStageChanged(string action, string? reason = null)
    {
        if (_run is null) return;
        _eventBus.Emit("stage_changed", new
        {
            workflowRunId = GrainKey,
            stage = _run.CurrentStage.Stage,
            status = _run.CurrentStage.Status.ToString(),
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
                "load" => "workflow_tasks_load_started",
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

    private async Task DispatchCompletedHookIfNeededAsync(WorkflowRunStatus? statusBefore)
    {
        if (_run is null) return;
        if (statusBefore == WorkflowRunStatus.Completed || _run.Status != WorkflowRunStatus.Completed) return;

        var status = await GetStatusAsync();
        if (status is null) return;

        var context = new WorkflowCompletionHookContext(GrainKey, _correlation, status);
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
        var context = _correlation;
        if (context is null || _run is null) return Task.CompletedTask;

        return _events.AppendAsync(new EventInput(
            context.ProjectId ?? "",
            context.OwnerNumber ?? 0,
            "workflow",
            type,
            IssueId: context.OwnerType == "issue" ? context.OwnerId : null,
            WorkflowRunId: GrainKey,
            Stage: _run.CurrentStage.Stage,
            TaskId: TaskId,
            CheckName: CheckName,
            RunnerId: RunnerId,
            Status: status,
            Message: message,
            Payload: Payload));
    }

    private Task PersistAsync() => _stateStore.SaveAsync(GrainKey, new WorkflowGrainState(
        _stageDefinitions,
        _run?.Snapshot(),
        _lease,
        _lastDispatch,
        _correlation,
        _variables));

}

[GenerateSerializer]
public sealed record WorkflowGrainState(
    [property: Id(0)] List<StageDefinition>? StageDefinitions,
    [property: Id(1)] WorkflowRunSnapshot? Run,
    [property: Id(2)] WorkLease? Lease,
    [property: Id(3)] WorkDispatch? LastDispatch,
    [property: Id(4)] WorkflowCorrelationContext? Correlation,
    [property: Id(5)] WorkflowExecutionContext? Variables);
