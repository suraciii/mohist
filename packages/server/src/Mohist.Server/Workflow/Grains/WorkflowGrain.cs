using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

#pragma warning disable CS8602
public class WorkflowGrain : Grain, IWorkflowGrain
{
    private WorkflowRun? _run;
    private List<StageDefinition>? _stageDefinitions;
    private string? _assignedRunnerId;
    private WorkDispatch? _pendingWork;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(ILogger<WorkflowGrain> log)
    {
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override Task OnActivateAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public async Task StartAsync(WorkflowDefinitionInput? definition = null)
    {
        if (definition is not null)
            _stageDefinitions = MapStageDefinitions(definition);

        if (_run is null && _stageDefinitions is not null)
            _run = new WorkflowRun(GrainKey, _stageDefinitions);

        if (_run is null)
            throw new InvalidOperationException("Cannot start: no workflow definition provided");

        _run.Start();
        _log.LogInformation("Workflow {Id} started, stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        EnsureRun();
        _run.Start();
        _log.LogInformation("Workflow {Id} resumed, stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        return Task.CompletedTask;
    }

    public Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        _run.Pause();
        _log.LogInformation("Workflow {Id} paused: {Reason}", GrainKey, reason);
        return Task.CompletedTask;
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        _run.Approve();
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await ReleaseFromBacklogIfTerminalAsync();
    }

    public async Task RejectAsync(string? reason = null)
    {
        EnsureRun();
        var output = reason is not null
            ? JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(reason))
            : (JsonElement?)null;
        _run.Reject(new ApprovalInput(output));
        _log.LogInformation("Workflow {Id} rejected at stage={Stage}: {Reason}", GrainKey, _run.CurrentStage.Stage, reason);
        await ReleaseFromBacklogIfTerminalAsync();
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        _pendingWork = null;
        _run.Retry();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await RegisterToBacklogAsync();
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        _pendingWork = null;
        _run.Rerun();
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await RegisterToBacklogAsync();
    }

    public Task AssignRunnerAsync(string runnerId)
    {
        _assignedRunnerId = runnerId;
        _log.LogInformation("Workflow {Id} assigned runner {RunnerId}", GrainKey, runnerId);
        return Task.CompletedTask;
    }

    public Task<WorkDispatch?> GetWorkAsync()
    {
        if (_run is null) return Task.FromResult<WorkDispatch?>(null);
        if (_pendingWork is not null) return Task.FromResult<WorkDispatch?>(null);

        var work = _run.GetNextWork();
        if (work is null) return Task.FromResult<WorkDispatch?>(null);

        return Task.FromResult<WorkDispatch?>(PrepareWork(work));
    }

    public async Task ReportResultAsync(string workId, WorkDispatchResult result)
    {
        var pending = _pendingWork;
        if (pending is null || workId != pending.WorkId) return;

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        _pendingWork = null;

        switch (pending.WorkType)
        {
            case "task":
                ProcessTaskResult(result);
                break;
            case "check":
                ProcessCheckResult(result);
                break;
            case "load":
                ProcessLoadResult(result);
                break;
        }

        await ReleaseFromBacklogIfTerminalAsync();
    }

    public Task<WorkflowStatusSnapshot?> GetStatusAsync()
    {
        if (_run is null) return Task.FromResult<WorkflowStatusSnapshot?>(null);

        var stages = _run.Stages.Select(s => new StageStatusSnapshot(
            s.Stage,
            s.Status.ToString(),
            s.Order,
            s.Tasks.Select(t => new TaskStatusSnapshot(t.Id, t.Title, t.Uses, t.Status.ToString())).ToList(),
            s.Checks.Select(c => new CheckStatusSnapshot(c.Name, c.Title, c.Uses, c.Status.ToString(), c.Message)).ToList(),
            s.Approval is not null
                ? new ApprovalStatusSnapshot(s.Approval.Status, s.Approval.Output?.ToString(), s.Approval.RequestedAt, s.Approval.RespondedAt)
                : null,
            s.Failure?.Message
        )).ToList();

        var pending = _pendingWork is not null
            ? new PendingWorkSnapshot(_pendingWork.WorkId, _pendingWork.WorkType, _pendingWork.Stage, _pendingWork.Title, _pendingWork.Uses)
            : null;

        var failure = _run.Failure?.Message;

        return Task.FromResult<WorkflowStatusSnapshot?>(new WorkflowStatusSnapshot(
            _run.Id,
            _run.Status.ToString(),
            _run.CurrentStage.Stage,
            stages,
            pending,
            failure));
    }

    private async Task ReleaseFromBacklogIfTerminalAsync()
    {
        if (_run is null) return;
        if (_run.Status is WorkflowRunStatus.Passed or WorkflowRunStatus.Failed)
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

    private WorkDispatch? PrepareWork(WorkflowWork work)
    {
        switch (work)
        {
            case WorkflowWork.StageInit si:
                var stageDef = RequireStageDefinition(si.Stage);
                if (stageDef.TasksFrom is null)
                {
                    _run.InitTasks(MaterializeTasks(stageDef));
                    return PrepareFromDomain();
                }
                return MakeDispatch(si.Stage, $"load-{si.Stage}", "load", $"Load tasks for {si.Stage}", stageDef.TasksFrom.Uses, stageDef.TasksFrom.With);

            case WorkflowWork.Task t:
                return MakeDispatch(t.Stage, t.Id, "task", t.Title, t.Uses, t.With);

            case WorkflowWork.Check ch:
                return MakeDispatch(ch.Stage, ch.Name, "check", ch.Title, ch.Uses, ch.With);

            default:
                return null;
        }
    }

    private WorkDispatch? PrepareFromDomain()
    {
        var work = _run.GetNextWork();
        return work is not null ? PrepareWork(work) : null;
    }

    private WorkDispatch MakeDispatch(string stage, string logicalId, string workType, string title, string? uses, Dictionary<string, JsonElement?>? with)
    {
        var workId = workType == "task" ? logicalId : $"{logicalId}:{Guid.NewGuid():N}";
        var withStr = with is not null ? JsonSerializer.Serialize(with) : null;
        var dispatch = new WorkDispatch(GrainKey, workId, uses, withStr, workType, stage, title);
        _pendingWork = dispatch;
        return dispatch;
    }

    private void ProcessTaskResult(WorkDispatchResult result)
    {
        if (result.Status == "completed")
            _run.CompleteTask();
        else
            _run.FailTask(new TaskResult("failed", result.Message));
    }

    private void ProcessCheckResult(WorkDispatchResult result)
    {
        var work = _run.CurrentStage;
        var pendingCheck = work.Checks.FirstOrDefault(c => c.Status == CheckRunStatus.Pending);
        var checkName = pendingCheck?.Name ?? "";

        var output = result.Output is not null
            ? JsonSerializer.Deserialize<JsonElement>(result.Output)
            : (JsonElement?)null;
        var checkResult = new CheckResult(checkName, result.Status, result.Message, output);

        switch (result.Status)
        {
            case "pass":
                _run.PassCheck(checkResult);
                break;
            case "pending":
                _run.PendingCheck(checkResult);
                break;
            default:
                var injected = TryInjectRetryTask(work.Stage, checkName, checkResult);
                if (injected)
                {
                    _run.ResetCheck(checkResult);
                    _run.ClearStageFailure();
                }
                else
                {
                    _run.FailCheck(checkResult);
                }
                break;
        }
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
            $"{retry.Task.Id}:{retryCount + 1}",
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

    private static List<StageDefinition> MapStageDefinitions(WorkflowDefinitionInput input) =>
        input.Stages.Select(s => new StageDefinition(
            s.Stage,
            s.Tasks.Select(t => new TaskDefinition(t.Id, t.Title, t.Uses, ParseWith(t.With))).ToList(),
            s.Checks.Select(c => new CheckDefinition(c.Name, c.Title, c.Uses, ParseWith(c.With),
                c.RetryLimit > 0 && c.RetryTask is not null
                    ? new CheckFailureAction(new CheckFailureRetry(c.RetryLimit, new TaskDefinition(c.RetryTask.Id, c.RetryTask.Title, c.RetryTask.Uses, ParseWith(c.RetryTask.With))))
                    : null
            )).ToList(),
            s.TasksFromUses is not null ? new WorkflowTasksFromDefinition(s.TasksFromUses, ParseWith(s.TasksFromWith)) : null,
            s.RequiresApproval
        )).ToList();
}
