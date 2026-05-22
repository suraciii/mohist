using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowGrain : Grain, IWorkflowGrain
{
    private WorkflowRun? _run;
    private List<StageDefinition>? _stageDefinitions;
    private string? _stageRunnerId;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(ILogger<WorkflowGrain> log)
    {
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

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

        await RunLoop();
    }

    public async Task ResumeAsync()
    {
        EnsureRun();
        _run.Start();
        _log.LogInformation("Workflow {Id} resumed, stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await RunLoop();
    }

    public async Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        _run.RequestPause();
        _log.LogInformation("Workflow {Id} pause requested: {Reason}", GrainKey, reason);
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        _run.Approve();
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        _ = Task.Run(() => RunLoop());
    }

    public async Task RejectAsync(string? reason = null)
    {
        EnsureRun();
        var output = reason is not null
            ? JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(reason))
            : (JsonElement?)null;
        _run.Reject(new ApprovalInput(output));
        _log.LogInformation("Workflow {Id} rejected at stage={Stage}: {Reason}", GrainKey, _run.CurrentStage.Stage, reason);
    }

    public async Task RetryAsync()
    {
        EnsureRun();
        _run.Retry();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await RunLoop();
    }

    public async Task RerunAsync()
    {
        EnsureRun();
        _run.Rerun();
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await RunLoop();
    }

    private async Task RunLoop()
    {
        EnsureRun();

        while (true)
        {
            if (_run.PauseRequested)
            {
                _run.Pause();
                await ReleaseStageRunnerAsync();
                _log.LogInformation("Workflow {Id} paused", GrainKey);
                break;
            }

            var work = _run.Next();

            switch (work)
            {
                case WorkflowWork.Complete:
                    await ReleaseStageRunnerAsync();
                    _log.LogInformation("Workflow {Id} completed at stage={Stage}", GrainKey, work.Stage);
                    return;

                case WorkflowWork.Failed f:
                    await ReleaseStageRunnerAsync();
                    _log.LogWarning("Workflow {Id} failed: {Reason}", GrainKey, f.Reason.Message);
                    return;

                case WorkflowWork.Blocked b:
                    await ReleaseStageRunnerAsync();
                    _log.LogWarning("Workflow {Id} blocked at stage={Stage}: {Reason}", GrainKey, b.Stage, b.Reason);
                    return;

                case WorkflowWork.AwaitApproval a:
                    _log.LogInformation("Workflow {Id} awaiting approval at stage={Stage}", GrainKey, a.Stage);
                    return;

                case WorkflowWork.StageInit si:
                    await HandleStageInit(si);
                    if (_run.PauseRequested) { _run.Pause(); await ReleaseStageRunnerAsync(); return; }
                    continue;

                case WorkflowWork.Task t:
                    await HandleTask(t);
                    if (_run.PauseRequested) { _run.Pause(); await ReleaseStageRunnerAsync(); return; }
                    continue;

                case WorkflowWork.Check c:
                    await HandleCheck(c);
                    if (_run.PauseRequested) { _run.Pause(); await ReleaseStageRunnerAsync(); return; }
                    continue;
            }
        }
    }

    private async Task HandleStageInit(WorkflowWork.StageInit work)
    {
        await AssignStageRunnerAsync();
        if (_stageRunnerId is null)
        {
            _run.FailStage("No idle runner available");
            return;
        }

        if (work.TasksFrom is null)
        {
            _run.InitTasks();
            return;
        }

        var result = await DispatchAndWaitAsync(new WorkDispatch(
            GrainKey, work.Stage, $"load-{work.Stage}", "load", work.TasksFrom.Uses, work.TasksFrom.With));

        switch (result.Status)
        {
            case "completed":
                _run.InitTasks();
                break;
            case "loaded":
                _run.InitTasks();
                break;
            default:
                _run.FailStage(result.Message ?? "Task loading failed");
                break;
        }
    }

    private async Task HandleTask(WorkflowWork.Task work)
    {
        if (_stageRunnerId is null)
        {
            _run.FailTask("No runner assigned to stage");
            return;
        }

        var result = await DispatchAndWaitAsync(new WorkDispatch(
            GrainKey, work.Stage, work.Id, "task", work.Uses, work.With));

        if (result.Status == "completed")
            _run.CompleteTask();
        else
            _run.FailTask(new TaskResult("failed", result.Message));
    }

    private async Task HandleCheck(WorkflowWork.Check work)
    {
        if (_stageRunnerId is null)
        {
            _run.FailCheck(new CheckResult(work.Name, "fail", "No runner assigned to stage"));
            return;
        }

        var result = await DispatchAndWaitAsync(new WorkDispatch(
            GrainKey, work.Stage, work.Name, "check", work.Uses, work.With));

        var checkResult = new CheckResult(work.Name, result.Status, result.Message, result.Output);

        switch (result.Status)
        {
            case "pass":
                _run.PassCheck(checkResult);
                break;
            case "pending":
                _run.PendingCheck(checkResult);
                break;
            default:
                var injected = TryInjectRetryTask(work.Stage, work.Name, checkResult);
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

    private async Task AssignStageRunnerAsync()
    {
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(Guid.Empty);
        _stageRunnerId = await registry.FindIdleRunnerAsync(null);

        if (_stageRunnerId is not null)
            _log.LogInformation("Stage runner assigned: {RunnerId}", _stageRunnerId);
        else
            _log.LogWarning("No idle runner available for stage assignment");
    }

    private async Task ReleaseStageRunnerAsync()
    {
        if (_stageRunnerId is null) return;

        var runner = GrainFactory.GetGrain<IRunnerGrain>(_stageRunnerId);
        await runner.ReleaseAsync();
        _log.LogInformation("Stage runner {RunnerId} released", _stageRunnerId);
        _stageRunnerId = null;
    }

    private async Task<WorkDispatchResult> DispatchAndWaitAsync(WorkDispatch work)
    {
        var runner = GrainFactory.GetGrain<IRunnerGrain>(_stageRunnerId!);

        await runner.DispatchAsync(work);
        _log.LogInformation("Dispatched {WorkType} {WorkId} to runner {RunnerId}", work.WorkType, work.WorkId, _stageRunnerId);

        while (true)
        {
            var result = await runner.TryGetResultAsync(work.WorkId);
            if (result is not null)
                return result;

            await Task.Delay(500);
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
        _run.InjectRetryTask(checkName, new LoadedTaskInput(
            $"{retry.Task.Id}:{retryCount + 1}",
            retry.Task.Title,
            retry.Task.Uses,
            MergeWith(retry.Task.With, new Dictionary<string, JsonElement?> { ["failedCheckResult"] = resultJson })));

        return true;
    }

    private void EnsureRun()
    {
        if (_run is null)
            throw new InvalidOperationException($"Workflow '{GrainKey}' has no workflow run");
    }

    private static List<StageDefinition> MapStageDefinitions(WorkflowDefinitionInput input) =>
        input.Stages.Select(s => new StageDefinition(
            s.Stage,
            s.Tasks.Select(t => new TaskDefinition(t.Id, t.Title, t.Uses, t.With)).ToList(),
            s.Checks.Select(c => new CheckDefinition(c.Name, c.Title, c.Uses, c.With,
                c.RetryLimit > 0 && c.RetryTask is not null
                    ? new CheckFailureAction(new CheckFailureRetry(c.RetryLimit, new TaskDefinition(c.RetryTask.Id, c.RetryTask.Title, c.RetryTask.Uses, c.RetryTask.With)))
                    : null
            )).ToList(),
            s.TasksFromUses is not null ? new WorkflowTasksFromDefinition(s.TasksFromUses, s.TasksFromWith) : null,
            s.RequiresApproval
        )).ToList();

    private static Dictionary<string, JsonElement?>? MergeWith(
        Dictionary<string, JsonElement?>? baseDict,
        Dictionary<string, JsonElement?> extra)
    {
        if (baseDict is null) return extra;
        var merged = new Dictionary<string, JsonElement?>(baseDict);
        foreach (var kv in extra) merged[kv.Key] = kv.Value;
        return merged;
    }
}
