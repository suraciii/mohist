using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

[Reentrant]
public class WorkflowGrain : Grain, IWorkflowGrain
{
    private WorkflowRun? _run;
    private List<StageDefinition>? _stageDefinitions;
    private string? _runnerId;
    private string? _pendingWorkId;
    private string? _pendingWorkType;
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

        if (_runnerId is null)
        {
            var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(Guid.Empty);
            _runnerId = await registry.FindIdleRunnerAsync(null);
        }

        if (_runnerId is null)
            throw new InvalidOperationException("No idle runner available");

        _log.LogInformation("Workflow {Id} assigned to runner {RunnerId}", GrainKey, _runnerId);

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

    public Task PauseAsync(string? reason = null)
    {
        EnsureRun();
        _run.RequestPause();
        _log.LogInformation("Workflow {Id} pause requested: {Reason}", GrainKey, reason);
        return Task.CompletedTask;
    }

    public async Task ApproveAsync()
    {
        EnsureRun();
        _run.Approve();
        _log.LogInformation("Workflow {Id} approved at stage={Stage}", GrainKey, _run.CurrentStage.Stage);
        await RunLoop();
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

    public async Task ReportResultAsync(string workId, WorkDispatchResult result)
    {
        if (workId != _pendingWorkId) return;

        _log.LogInformation("Workflow {Id} received result for {WorkId}: {Status}", GrainKey, workId, result.Status);

        _pendingWorkId = null;
        var workType = _pendingWorkType;
        _pendingWorkType = null;

        switch (workType)
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
                await ReleaseRunnerAsync();
                _log.LogInformation("Workflow {Id} paused", GrainKey);
                return;
            }

            var work = _run.Next();

            switch (work)
            {
                case WorkflowWork.Complete:
                    await ReleaseRunnerAsync();
                    _log.LogInformation("Workflow {Id} completed at stage={Stage}", GrainKey, work.Stage);
                    return;

                case WorkflowWork.Failed f:
                    await ReleaseRunnerAsync();
                    _log.LogWarning("Workflow {Id} failed: {Reason}", GrainKey, f.Reason.Message);
                    return;

                case WorkflowWork.Blocked b:
                    await ReleaseRunnerAsync();
                    _log.LogWarning("Workflow {Id} blocked at stage={Stage}: {Reason}", GrainKey, b.Stage, b.Reason);
                    return;

                case WorkflowWork.AwaitApproval a:
                    _log.LogInformation("Workflow {Id} awaiting approval at stage={Stage}", GrainKey, a.Stage);
                    return;

                case WorkflowWork.StageInit si:
                    if (await HandleStageInitAsync(si))
                        return;
                    if (_run.PauseRequested) { _run.Pause(); await ReleaseRunnerAsync(); return; }
                    continue;

                case WorkflowWork.Task t:
                    await DispatchAsync(work.Id, "task", work.Stage, work.Uses, work.With);
                    return;

                case WorkflowWork.Check c:
                    await DispatchAsync(work.Name, "check", work.Stage, work.Uses, work.With);
                    return;
            }
        }
    }

    private async Task<bool> HandleStageInitAsync(WorkflowWork.StageInit work)
    {
        if (work.TasksFrom is null)
        {
            _run.InitTasks();
            return false;
        }

        await DispatchAsync($"load-{work.Stage}", "load", work.Stage, work.TasksFrom.Uses, work.TasksFrom.With);
        return true;
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

        var checkResult = new CheckResult(checkName, result.Status, result.Message, result.Output);

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
                _run.InitTasks();
                break;
            default:
                _run.FailStage(result.Message ?? "Task loading failed");
                break;
        }
    }

    private async Task DispatchAsync(string workId, string workType, string stage, string? uses, Dictionary<string, JsonElement?>? with)
    {
        _pendingWorkId = workId;
        _pendingWorkType = workType;

        var runner = GrainFactory.GetGrain<IRunnerGrain>(_runnerId!);
        await runner.DispatchAsync(new WorkDispatch(GrainKey, stage, workId, workType, uses, with));

        _log.LogInformation("Dispatched {WorkType} {WorkId} to runner {RunnerId}", workType, workId, _runnerId);
    }

    private async Task ReleaseRunnerAsync()
    {
        if (_runnerId is null) return;

        var runner = GrainFactory.GetGrain<IRunnerGrain>(_runnerId);
        await runner.ReleaseAsync();
        _log.LogInformation("Runner {RunnerId} released", _runnerId);
        _runnerId = null;
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
