using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobGrainPersistenceSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public AgentJobGrainPersistenceSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.DispatchObserver.Reset();
    }

    private IGrainFactory Grains => _fixture.Grains;

    [Fact]
    public async Task SubmitAsync_PersistsInputAcrossDeactivation_AndReplayResumesSameJob()
    {
        await ClearRunnerRegistryAsync();
        var projectId = $"agent-job-persist-project-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>($"agent-job-persist-{Guid.NewGuid():N}");
        var agentConfig = JsonDocument.Parse(
            "{\"type\":\"opencode\",\"model\":\"openai/gpt-5.6\"}").RootElement.Clone();
        var input = new AgentJobInput(
            "persist me",
            WorkspacePath: "/tmp/agent-job-persist",
            ProjectId: projectId,
            AgentConfig: agentConfig);

        await job.SubmitAsync(input);
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());
        await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            job.SubmitAsync(input with { Prompt = "different" }));

        var runnerId = await RegisterRunnerAsync(projectId, "persist");
        await job.EnsureSubmittedAsync(input);
        await WaitForRunningAsync(job);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CurrentWorkId));

        var dispatch = await Grains.GetGrain<IRunnerGrain>(runnerId)
            .PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(dispatch);
        var with = JsonSerializer.Deserialize<JsonElement>(dispatch!.With!);
        Assert.Equal(
            "openai/gpt-5.6",
            with.GetProperty("prompt").GetProperty("agent-launch").GetProperty("config").GetProperty("model").GetString());
    }

    [Fact]
    public async Task RunnerAcceptanceCrash_ReactivationReusesSameWork()
    {
        await ClearRunnerRegistryAsync();
        var projectId = $"agent-job-acceptance-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerAsync(projectId, "acceptance");
        var jobKey = $"agent-job-acceptance-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var input = new AgentJobInput(
            "survive acceptance crash",
            WorkspacePath: "/tmp/agent-job-acceptance",
            ProjectId: projectId);
        _fixture.DispatchObserver.FailRunnerAccepted = true;

        await job.SubmitAsync(input);
        await _fixture.DispatchObserver.WaitForRunnerAcceptedAsync();

        var prepared = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, prepared.Status);
        Assert.Equal(runnerId, prepared.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(prepared.CurrentWorkId));
        Assert.False(prepared.RunnerAccepted);

        await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        _fixture.DispatchObserver.FailRunnerAccepted = false;

        await job.SubmitAsync(input);
        await WaitForRunningAsync(job);

        var resumed = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(prepared.CurrentWorkId, resumed.CurrentWorkId);
        Assert.True(resumed.RunnerAccepted);
        var runnerState = await Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync();
        var work = Assert.Single(runnerState.ActiveWorks, item => item.OwnerId == jobKey);
        Assert.Equal(prepared.CurrentWorkId, work.WorkId);
    }

    [Fact]
    public async Task PreparedAssignment_OfflineBeforeAcceptance_ReassignsStableWorkToHealthyRunner()
    {
        await ClearRunnerRegistryAsync();
        var projectId = $"agent-job-replacement-project-{Guid.NewGuid():N}";
        var unavailableRunnerId = await RegisterRunnerAsync(projectId, "unavailable");
        var jobKey = $"agent-job-replacement-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var input = new AgentJobInput(
            "replace unaccepted runner",
            WorkspacePath: "/tmp/agent-job-replacement",
            ProjectId: projectId);
        _fixture.DispatchObserver.FailAssignmentPrepared = true;

        await job.SubmitAsync(input);
        await _fixture.DispatchObserver.WaitForAssignmentPreparedAsync();

        var prepared = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, prepared.Status);
        Assert.Equal(unavailableRunnerId, prepared.RunnerId);
        Assert.False(prepared.RunnerAccepted);
        Assert.False(string.IsNullOrWhiteSpace(prepared.CurrentWorkId));

        _fixture.DispatchObserver.FailAssignmentPrepared = false;
        await Grains.GetGrain<IRunnerGrain>(unavailableRunnerId).UnregisterAsync();
        var replacementRunnerId = await RegisterRunnerAsync(projectId, "replacement");
        await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        await job.EnsureSubmittedAsync(input);
        await WaitForRunningAsync(job);

        var resumed = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(replacementRunnerId, resumed.RunnerId);
        Assert.Equal(prepared.CurrentWorkId, resumed.CurrentWorkId);
        Assert.True(resumed.RunnerAccepted);
        var replacementState = await Grains
            .GetGrain<IRunnerGrain>(replacementRunnerId)
            .GetRuntimeStateAsync();
        var work = Assert.Single(replacementState.ActiveWorks, item => item.OwnerId == jobKey);
        Assert.Equal(prepared.CurrentWorkId, work.WorkId);
    }

    private async Task<string> RegisterRunnerAsync(string projectId, string suffix)
    {
        var runnerId = $"agent-job-{suffix}-runner-{Guid.NewGuid():N}";
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            projectId));
        return runnerId;
    }

    private async Task ClearRunnerRegistryAsync()
    {
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var runnerId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(runnerId);
    }

    private static Task WaitForRunningAsync(IAgentJobGrain job) =>
        TestWait.ForAsync(
            () => job.GetStatusAsync(),
            status => status == AgentJobStatus.Running,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "status == Running",
            () => job.CheckTimeoutsAsync());
}
