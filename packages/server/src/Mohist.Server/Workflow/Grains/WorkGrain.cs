using System.Text.Json;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Workflow.Grains;

public class WorkGrain : Grain, IWorkGrain
{
    private readonly ILogger<WorkGrain> _log;

    public WorkGrain(ILogger<WorkGrain> log)
    {
        _log = log;
    }

    public async Task<WorkResult> ExecuteTaskAsync(TaskWorkItem work)
    {
        var runnerId = await FindRunnerAsync(work.Uses);
        if (runnerId is null)
            return new WorkResult.TaskFailed($"No idle runner for '{work.Uses}'");

        return await DispatchToRunnerAsync(runnerId, new WorkDispatch(
            this.GetPrimaryKeyString(), "", work.TaskId, "task", work.Uses, work.With));
    }

    public async Task<WorkResult> ExecuteCheckAsync(CheckWorkItem work)
    {
        var runnerId = await FindRunnerAsync(work.Uses);
        if (runnerId is null)
            return new WorkResult.CheckFailed(work.CheckName, $"No idle runner for '{work.Uses}'");

        return await DispatchToRunnerAsync(runnerId, new WorkDispatch(
            this.GetPrimaryKeyString(), "", work.CheckName, "check", work.Uses, work.With));
    }

    public async Task<TaskLoadWorkResult> LoadTasksAsync(TaskLoadWorkItem work)
    {
        var runnerId = await FindRunnerAsync(work.Uses);
        if (runnerId is null)
            return new TaskLoadWorkResult.Failed($"No idle runner for '{work.Uses}'");

        var runnerGrain = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var dispatched = await runnerGrain.TryDispatchAsync(new WorkDispatch(
            this.GetPrimaryKeyString(), work.Stage, "", "load", work.Uses, work.With));

        if (!dispatched)
            return new TaskLoadWorkResult.Failed($"Runner '{runnerId}' rejected dispatch");

        _log.LogInformation("Dispatched task-load for stage {Stage} to runner {RunnerId}", work.Stage, runnerId);

        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(1000);
            if (await runnerGrain.IsAvailableAsync())
                break;
        }

        return new TaskLoadWorkResult.Empty();
    }

    private async Task<string?> FindRunnerAsync(string? uses)
    {
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(Guid.Empty);
        return await registry.FindIdleRunnerAsync(uses);
    }

    private async Task<WorkResult> DispatchToRunnerAsync(string runnerId, WorkDispatch dispatch)
    {
        var runnerGrain = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var dispatched = await runnerGrain.TryDispatchAsync(dispatch);

        if (!dispatched)
        {
            _log.LogWarning("Runner {Id} rejected dispatch", runnerId);
            return new WorkResult.TaskFailed($"Runner '{runnerId}' rejected dispatch");
        }

        _log.LogInformation("Dispatched {WorkType} {WorkId} to runner {RunnerId}", dispatch.WorkType, dispatch.WorkId, runnerId);

        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(1000);
            if (await runnerGrain.IsAvailableAsync())
                break;
        }

        return new WorkResult.TaskCompleted();
    }
}
