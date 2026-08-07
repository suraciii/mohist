using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;
using Orleans;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

public abstract class AgentJobGrainTestSupport
{
    protected readonly AgentJobGrainFixture _fixture;

    protected AgentJobGrainTestSupport(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.DispatchObserver.Reset();
        _fixture.LaunchFaults.ClearObservations();
    }

    protected IGrainFactory Grains => _fixture.Grains;

    protected IAgentJobGrain JobGrain(string key) => Grains.GetGrain<IAgentJobGrain>(key);

    protected static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        TimeSpan step,
        string description)
        => await TestWait.ForAsync(probe, done, timeout, step, description);

    protected async Task WaitForStatusAsync(IAgentJobGrain job, AgentJobStatus expected, TimeSpan timeout)
    {
        if (expected == AgentJobStatus.Running)
        {
            await WaitForRunningAsync(job);
            return;
        }

        var convergenceTimeout = timeout < TimeSpan.FromSeconds(30)
            ? TimeSpan.FromSeconds(30)
            : timeout;

        await WaitForAsync(
            () => job.GetStatusAsync(),
            s => s == expected,
            convergenceTimeout,
            TimeSpan.FromMilliseconds(25),
            $"status == {expected}",
            () => job.CheckTimeoutsAsync());
    }

    protected async Task WaitForRunningAsync(IAgentJobGrain job)
    {
        var runnerId = await WaitForAssignedRunnerAsync(job);
        await PollRunnerAsync(runnerId);
        await _fixture.DispatchObserver.WaitForRunnerAcceptedAsync();
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

    private async Task<string> WaitForAssignedRunnerAsync(IAgentJobGrain job)
    {
        await WaitForAsync(
            () => job.GetRuntimeSnapshotAsync(),
            snapshot => !string.IsNullOrWhiteSpace(snapshot.RunnerId)
                || snapshot.Status is AgentJobStatus.Completed
                    or AgentJobStatus.Failed
                    or AgentJobStatus.Unknown,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "AgentJob receives a runner assignment or terminates");
        var snapshot = await job.GetRuntimeSnapshotAsync();
        if (string.IsNullOrWhiteSpace(snapshot.RunnerId))
        {
            throw new InvalidOperationException(
                $"AgentJob {job.GetPrimaryKeyString()} never received a runner assignment before running (status={snapshot.Status})");
        }
        return snapshot.RunnerId!;
    }

    private Task PollRunnerAsync(string runnerId)
    {
        var dispatch = _fixture.Cluster
            .GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<DispatchService>();
        return dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
    }

    protected static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        TimeSpan step,
        string description,
        Func<Task> advance)
        => await TestWait.ForAsync(probe, done, timeout, step, description, advance);

    protected async Task<(string RunnerId, string ProjectId)> RegisterAgentJobRunnerAsync(
        string runnerId,
        string? projectId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        // Every agent-job spec shares the in-memory backlog directory and
        // global runner registry with the rest of the [Collection("RunnerGrain")]
        // cluster. Without a reset here, a stale runner from a prior spec
        // assigns this job before the new runner can, which makes the
        // assertions on snapshot.RunnerId non-deterministic. Clear both
        // before each registration.
        await ClearBacklogAsync();

        var pid = projectId ?? $"agent-job-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            pid));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }

        await WaitForAsync(
            () => runner.GetRuntimeStateAsync(),
            state => state.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"runner {runnerId} is online");

        await WaitForAsync(
            () => Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListEligibleRunnersAsync(pid),
            runners => runners.Any(info => string.Equals(info.RunnerId, runnerId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"runner {runnerId} is eligible for project {pid}");

        return (runnerId, pid);
    }

    protected async Task ClearBacklogAsync()
    {
        await ClearGlobalRunnerRegistryAsync();
    }

    protected async Task ClearGlobalRunnerRegistryAsync()
    {
        await _fixture.ClearActiveAgentJobsAsync();
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var ids = await registry.ListRunnerIdsAsync();
        foreach (var id in ids)
            await registry.UnregisterAsync(id);
    }

    protected static AgentJobInput MakeInput(string prompt, string projectId, string workspacePath = "/tmp/agent-job") =>
        new(Prompt: prompt, WorkspacePath: workspacePath, ProjectId: projectId, AgentId: "agent-test");

}
