using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Handlers;

namespace Mohist.Server.Workflow.Grains;

public class WorkGrain : Grain, IWorkGrain
{
    private readonly IHandlerRegistry _registry;
    private readonly ILogger<WorkGrain> _log;

    public WorkGrain(IHandlerRegistry registry, ILogger<WorkGrain> log)
    {
        _registry = registry;
        _log = log;
    }

    public async Task<WorkResult> ExecuteTaskAsync(TaskWorkItem work)
    {
        var runnerId = await FindRunnerAsync(work.Uses);
        if (runnerId is not null)
        {
            return await DispatchToRunnerAsync(runnerId, new WorkDispatch(
                this.GetPrimaryKeyString(), "", work.TaskId, "task", work.Uses, work.With));
        }

        return await ExecuteTaskLocallyAsync(work);
    }

    public async Task<WorkResult> ExecuteCheckAsync(CheckWorkItem work)
    {
        var runnerId = await FindRunnerAsync(work.Uses);
        if (runnerId is not null)
        {
            return await DispatchToRunnerAsync(runnerId, new WorkDispatch(
                this.GetPrimaryKeyString(), "", work.CheckName, "check", work.Uses, work.With));
        }

        return await ExecuteCheckLocallyAsync(work);
    }

    public async Task<TaskLoadWorkResult> LoadTasksAsync(TaskLoadWorkItem work)
    {
        var loader = _registry.TaskLoader(work.Uses);
        if (loader is null)
        {
            _log.LogWarning("Task loader '{Uses}' not registered", work.Uses);
            return new TaskLoadWorkResult.Failed($"Task loader '{work.Uses}' is not registered");
        }

        try
        {
            var result = await loader.LoadAsync(new TaskLoadInput(work.Stage, work.Uses, work.With));

            return result switch
            {
                TaskLoadResult.Loaded l => new TaskLoadWorkResult.Loaded(
                    l.Tasks.Select(t => new LoadedTaskSnapshot(t.Id, t.Title, t.Uses, t.With)).ToList()),
                TaskLoadResult.Empty => new TaskLoadWorkResult.Empty(),
                TaskLoadResult.Missing m => new TaskLoadWorkResult.Failed(m.Message ?? $"Task loader '{work.Uses}': missing"),
                TaskLoadResult.Invalid inv => new TaskLoadWorkResult.Failed(inv.Message ?? $"Task loader '{work.Uses}': invalid"),
                _ => new TaskLoadWorkResult.Failed($"Task loader '{work.Uses}': unknown result")
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Task loader '{Uses}' threw exception", work.Uses);
            return new TaskLoadWorkResult.Failed(ex.Message);
        }
    }

    private async Task<string?> FindRunnerAsync(string? uses)
    {
        try
        {
            var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(Guid.Empty);
            return await registry.FindIdleRunnerAsync(uses);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No runner registry available for '{Uses}'", uses);
            return null;
        }
    }

    private async Task<WorkResult> DispatchToRunnerAsync(string runnerId, WorkDispatch dispatch)
    {
        var runnerGrain = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var dispatched = await runnerGrain.TryDispatchAsync(dispatch);

        if (!dispatched)
        {
            _log.LogWarning("Runner {Id} rejected dispatch, falling back to local", runnerId);
            return dispatch.WorkType switch
            {
                "task" => await ExecuteTaskLocallyAsync(new TaskWorkItem(dispatch.WorkId, "", dispatch.Uses, dispatch.With)),
                _ => new WorkResult.CheckFailed(dispatch.WorkId, "Runner rejected dispatch")
            };
        }

        _log.LogInformation("Dispatched {WorkType} {WorkId} to runner {RunnerId}", dispatch.WorkType, dispatch.WorkId, runnerId);

        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(1000);
            var available = await runnerGrain.IsAvailableAsync();
            if (available)
                break;
        }

        return new WorkResult.TaskCompleted();
    }

    private async Task<WorkResult> ExecuteTaskLocallyAsync(TaskWorkItem work)
    {
        var handler = _registry.Task(work.Uses);
        if (handler is null)
        {
            _log.LogWarning("Task handler '{Uses}' not registered for task {TaskId}", work.Uses, work.TaskId);
            return new WorkResult.TaskFailed($"Task handler '{work.Uses}' is not registered");
        }

        try
        {
            var result = await handler.RunAsync(new TaskHandlerInput(work.TaskId, work.Title, work.With));

            if (result.Status == "completed")
                return new WorkResult.TaskCompleted();

            return new WorkResult.TaskFailed(result.Reason);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Task {TaskId} handler threw exception", work.TaskId);
            return new WorkResult.TaskFailed(ex.Message);
        }
    }

    private async Task<WorkResult> ExecuteCheckLocallyAsync(CheckWorkItem work)
    {
        var handler = _registry.Check(work.Uses);
        if (handler is null)
        {
            _log.LogWarning("Check handler '{Uses}' not registered for check {CheckName}", work.Uses, work.CheckName);
            return new WorkResult.CheckFailed(work.CheckName, $"Check handler '{work.Uses}' is not registered");
        }

        try
        {
            var result = await handler.RunAsync(new CheckHandlerInput(work.CheckName, work.Title, work.With));

            return result.Status switch
            {
                "pass" => new WorkResult.CheckPassed(result.Message, result.Output),
                "pending" => new WorkResult.CheckPending(result.Message, result.Output),
                _ => new WorkResult.CheckFailed(result.Name, result.Message, result.Output)
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Check {CheckName} handler threw exception", work.CheckName);
            return new WorkResult.CheckFailed(work.CheckName, ex.Message);
        }
    }
}
