using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowGrain : Grain, IWorkflowGrain
{
    private WorkflowRun? _run;
    private List<StageDefinition>? _stageDefinitions;
    private readonly ILogger<WorkflowGrain> _log;

    public WorkflowGrain(ILogger<WorkflowGrain> log)
    {
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();
    private IWorkGrain WorkGrain => GrainFactory.GetGrain<IWorkGrain>(GrainKey);

    public async Task StartAsync(WorkflowDefinitionInput? definition = null)
    {
        if (definition is not null)
        {
            _stageDefinitions = MapStageDefinitions(definition);
        }

        if (_run is null && _stageDefinitions is not null)
        {
            _run = new WorkflowRun(GrainKey, _stageDefinitions);
        }

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
                _log.LogInformation("Workflow {Id} paused", GrainKey);
                break;
            }

            var work = _run.Next();

            switch (work)
            {
                case WorkflowWork.Complete:
                    _log.LogInformation("Workflow {Id} completed at stage={Stage}", GrainKey, work.Stage);
                    return;

                case WorkflowWork.Failed f:
                    _log.LogWarning("Workflow {Id} failed: {Reason}", GrainKey, f.Reason.Message);
                    return;

                case WorkflowWork.Blocked b:
                    _log.LogWarning("Workflow {Id} blocked at stage={Stage}: {Reason}", GrainKey, b.Stage, b.Reason);
                    return;

                case WorkflowWork.AwaitApproval a:
                    _log.LogInformation("Workflow {Id} awaiting approval at stage={Stage}", GrainKey, a.Stage);
                    return;

                case WorkflowWork.StageInit si:
                    await HandleStageInit(si);
                    if (_run.PauseRequested) { _run.Pause(); return; }
                    continue;

                case WorkflowWork.Task t:
                    await HandleTask(t);
                    if (_run.PauseRequested) { _run.Pause(); return; }
                    continue;

                case WorkflowWork.Check c:
                    await HandleCheck(c);
                    if (_run.PauseRequested) { _run.Pause(); return; }
                    continue;
            }
        }
    }

    private async Task HandleStageInit(WorkflowWork.StageInit work)
    {
        if (work.TasksFrom is null)
        {
            _run.InitTasks();
            return;
        }

        var result = await WorkGrain.LoadTasksAsync(new TaskLoadWorkItem(
            work.Stage,
            work.TasksFrom.Uses,
            work.TasksFrom.With));

        switch (result)
        {
            case TaskLoadWorkResult.Loaded l:
                _run.InitTasks(l.Tasks.Select(t => new LoadedTaskInput(t.Id, t.Title, t.Uses, t.With)).ToList());
                break;
            case TaskLoadWorkResult.Empty:
                _run.InitTasks();
                break;
            case TaskLoadWorkResult.Failed f:
                _run.FailStage(f.Message);
                break;
        }
    }

    private async Task HandleTask(WorkflowWork.Task work)
    {
        var result = await WorkGrain.ExecuteTaskAsync(new TaskWorkItem(
            work.Id,
            work.Title,
            work.Uses,
            work.With));

        switch (result)
        {
            case WorkResult.TaskCompleted:
                _run.CompleteTask();
                break;
            case WorkResult.TaskFailed f:
                _run.FailTask(new TaskResult("failed", f.Reason));
                break;
        }
    }

    private async Task HandleCheck(WorkflowWork.Check work)
    {
        var result = await WorkGrain.ExecuteCheckAsync(new CheckWorkItem(
            work.Name,
            work.Title,
            work.Uses,
            work.With));

        switch (result)
        {
            case WorkResult.CheckPassed p:
                _run.PassCheck(new CheckResult(work.Name, "pass", p.Message, p.Output));
                break;

            case WorkResult.CheckPending p:
                _run.PendingCheck(new CheckResult(work.Name, "pending", p.Message, p.Output));
                break;

            case WorkResult.CheckFailed f:
                var checkResult = new CheckResult(work.Name, "fail", f.Message, f.Output);
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

    private bool TryInjectRetryTask(string stage, string checkName, CheckResult result)
    {
        var stageDef = _stageDefinitions?.Find(s => s.Stage == stage);
        if (stageDef is null) return false;

        var checkDef = stageDef.Checks.Find(c => c.Name == checkName);
        if (checkDef?.OnFailure?.Retry is not { } retry) return false;

        var retryCount = _run.RetryCountForCheck(checkName);
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
