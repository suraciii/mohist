using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Agent.Grain;

[Collection("AgentJobGrain")]
[Trait("level", "L1")]
public sealed class AgentJobManagerRuntimeAdmissionSpecs : AgentJobGrainTestSupport
{
    public AgentJobManagerRuntimeAdmissionSpecs(AgentJobGrainFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("pi", false, "pi", true)]
    [InlineData("pi", false, "opencode", false)]
    [InlineData("opencode", false, "opencode", false)]
    [InlineData("opencode", true, "pi", false)]
    [InlineData("opencode", true, "opencode", true)]
    public async Task Manager_agent_job_claim_requires_runtime_specific_admission(
        string runtime,
        bool includeOpenCodeIsolation,
        string readyRuntime,
        bool expectedDispatch)
    {
        var (runnerId, _, job, _) = await PrepareManagerJobAsync(runtime, includeOpenCodeIsolation);

        var response = await Dispatch.PollAsync(
            runnerId,
            CapabilityFencePollRequest(readyRuntime));

        if (!expectedDispatch)
        {
            Assert.Empty(response.Dispatches);
            Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());
            return;
        }

        var dispatch = Assert.Single(response.Dispatches);
        Assert.Equal(runtime, dispatch.AgentDefinition?.Runtime);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

    [Fact]
    public async Task Manager_agent_job_claim_rechecks_readiness_after_expectation_snapshot()
    {
        var (runnerId, runner, job, info) = await PrepareManagerJobAsync("pi", includeOpenCodeIsolation: false);
        await runner.ObserveRuntimeReadinessAsync(
            CapabilityFenceConnection,
            [new RuntimeReadinessWitness("pi", Ready: true, Generation: 1)]);
        var pending = await job.GetRuntimeSnapshotAsync();
        var definition = Assert.IsType<AgentExecutionDefinition>(pending.ExecutionDefinition);
        var expectation = new CapabilityClaimExpectation(
            WorkDispatchOwnerKinds.AgentJob,
            job.GetPrimaryKeyString(),
            pending.CurrentWorkId!,
            definition.Runtime,
            definition.Model,
            definition.ReasoningEffort,
            definition.Variant,
            info.RuntimeCatalogs!["pi"].CapabilityRevision,
            RuntimeGeneration: 1,
            ConnectionGeneration: CapabilityFenceConnection,
            RequiredCapabilities: ManagerCapabilities(includeOpenCodeIsolation: false));

        await runner.RegisterAsync(info, TestRunnerGenerationExtensions.ProcessGeneration);

        Assert.Null(await runner.TryClaimAgentJobAsync(
            job.GetPrimaryKeyString(),
            SlackDeliveryOwnerIds.ManagerProjectId,
            expectation,
            TestRunnerGenerationExtensions.ProcessGeneration));
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());
    }

    [Theory]
    [InlineData("pi", false)]
    [InlineData("opencode", true)]
    public async Task Manager_active_redelivery_requires_selected_runtime_ready(
        string runtime,
        bool includeOpenCodeIsolation)
    {
        var (runnerId, _, job, _) = await PrepareManagerJobAsync(runtime, includeOpenCodeIsolation);
        Assert.Single((await Dispatch.PollAsync(
            runnerId,
            CapabilityFencePollRequest(runtime))).Dispatches);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());

        var response = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                RuntimeReadiness: [new RuntimeReadinessWitness(runtime, Ready: false, Generation: 2)],
                ConnectionGeneration: CapabilityFenceConnection,
                ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(response.Dispatches);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

    private DispatchService Dispatch => _fixture.Cluster.GetSiloServiceProvider(null)
        .GetRequiredService<IServiceScopeFactory>().CreateScope()
        .ServiceProvider.GetRequiredService<DispatchService>();

    private async Task<(string RunnerId, IRunnerGrain Runner, IAgentJobGrain Job, RunnerInfo Info)>
        PrepareManagerJobAsync(string runtime, bool includeOpenCodeIsolation)
    {
        await ClearBacklogAsync();
        var runnerId = $"manager-runtime-{runtime}-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var info = new RunnerInfo(
            runnerId,
            ManagerCapabilities(includeOpenCodeIsolation),
            "manager-runtime-admission-host",
            SlackDeliveryOwnerIds.ManagerProjectId,
            ConnectionGeneration: CapabilityFenceConnection,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create());
        await runner.RegisterAsync(info, TestRunnerGenerationExtensions.ProcessGeneration);

        var jobId = $"manager-runtime-job-{Guid.NewGuid():N}";
        var sessionId = $"manager-session-{Guid.NewGuid():N}";
        var job = JobGrain(jobId);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "manager request",
            ProjectId: SlackDeliveryOwnerIds.ManagerProjectId,
            Runtime: runtime,
            AgentId: "manager-agent",
            AgentSessionId: sessionId,
            PinnedRunnerId: runnerId,
            ExecutionSource: AgentExecutionSources.Slack,
            SlackExecutionContext: ManagerContext(jobId, sessionId)));
        await _fixture.DispatchObserver.WaitForAssignmentPreparedAsync(jobId, TimeSpan.FromSeconds(5));
        return (runnerId, runner, job, info);
    }

    private static string[] ManagerCapabilities(bool includeOpenCodeIsolation)
    {
        var capabilities = new List<string>
        {
            AgentExecutionSources.Version1Capability,
        };
        capabilities.AddRange(ManagerExecutionRuntimeCapabilities.Required);
        if (includeOpenCodeIsolation)
            capabilities.Add(ManagerExecutionRuntimeCapabilities.IsolatedOpenCodeV1);
        return capabilities.ToArray();
    }

    private static AgentSlackExecutionContext ManagerContext(string jobId, string sessionId) =>
        SlackExecutionContextFactory.Create(
            "workspace-1",
            "conversation-1",
            "thread-1",
            "message-1",
            "member-1",
            "enrollment-1",
            sessionId,
            $"dispatch-{jobId}",
            projectId: SlackDeliveryOwnerIds.ManagerProjectId,
            ownerKind: SlackDeliveryOwnerKinds.Manager);
}
