using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Workflow;
using Orleans;
using Xunit;
namespace Mohist.Server.Tests.Agent.Grain;

public abstract class AgentJobGrainTestSupport
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

    protected readonly AgentJobGrainFixture _fixture;

    protected AgentJobGrainTestSupport(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.SessionStatePersistence.Reset();
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

    protected async Task WaitForStatusAsync(
        IAgentJobGrain job,
        AgentJobStatus expected,
        TimeSpan? timeout = null,
        RunnerPollRequest? request = null)
    {
        var waitTimeout = timeout ?? DefaultWaitTimeout;
        if (expected == AgentJobStatus.Running)
        {
            await WaitForRunningAsync(job, request, waitTimeout);
            return;
        }

        // The dispatch observer has no terminal-status callback; retain this bounded
        // Orleans convergence wait for transitions that are not dispatch boundaries.
        await WaitForAsync(
            () => job.GetStatusAsync(),
            s => s == expected,
            waitTimeout,
            TimeSpan.FromMilliseconds(25),
            $"status == {expected}",
            () => job.CheckTimeoutsAsync());
    }

    protected async Task WaitForRunningAsync(
        IAgentJobGrain job,
        RunnerPollRequest? request = null,
        TimeSpan? timeout = null)
    {
        var waitTimeout = timeout ?? DefaultWaitTimeout;
        var runnerId = await WaitForAssignedRunnerAsync(job, waitTimeout);
        await PollRunnerAsync(runnerId, request);
        await _fixture.DispatchObserver.WaitForRunnerAcceptedAsync(
            job.GetPrimaryKeyString(),
            waitTimeout);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

    protected async Task ClaimPreparedAgentJobAsync(string runnerId)
    {
        await _fixture.DispatchObserver.WaitForAssignmentPreparedAsync();
        await PollRunnerAsync(runnerId);
        await _fixture.DispatchObserver.WaitForRunnerAcceptedAsync();
    }

    private async Task<string> WaitForAssignedRunnerAsync(
        IAgentJobGrain job,
        TimeSpan timeout)
    {
        var runnerId = await _fixture.DispatchObserver.WaitForAssignmentPreparedAsync(
            job.GetPrimaryKeyString(),
            timeout);
        return runnerId;
    }

    private Task PollRunnerAsync(string runnerId, RunnerPollRequest? request = null)
    {
        var dispatch = _fixture.Cluster
            .GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<DispatchService>();
        return dispatch.PollAsync(
            runnerId,
            request ?? CapabilityFencePollRequest("pi", "opencode"));
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
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots,
        IReadOnlyCollection<string>? additionalCapabilities = null)
    {
        // Every agent-job spec shares the in-memory backlog directory and
        // global runner registry with the rest of the [Collection("RunnerGrain")]
        // cluster. Without a reset here, a stale runner from a prior spec
        // assigns this job before the new runner can, which makes the
        // assertions on snapshot.RunnerId non-deterministic. Clear both
        // before each registration.
        await ClearBacklogAsync();

        var pid = projectId ?? $"agent-job-project-{Guid.NewGuid():N}";
        // Admission claims Agent occupancy against the real Agent
        // definition, so the success-focused runner helper seeds the
        // definition the shared MakeInput() jobs launch under. Specs that
        // exercise missing definitions deliberately never register a
        // runner through this helper.
        await _fixture.SeedAgentAsync(pid, "agent-test", maxConcurrentRuns: null);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var capabilities = new[] { "spec/*", AgentExecutionSources.Version1Capability }
            .Concat(additionalCapabilities ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            capabilities,
            "agent-job-host",
            pid,
            ConnectionGeneration: CapabilityFenceConnection,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
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

    /// <summary>
    /// Opens a real AgentSession carrying the accepted identity labels and
    /// this Job's initial Input and Turn, so the Job's derived capacity
    /// claim can attribute its Session-local order against the persisted
    /// row instead of fabricating a bare session reference.
    /// </summary>
    protected async Task OpenJobSessionAsync(
        string sessionId,
        string projectId,
        string jobKey,
        string inputId,
        string turnId,
        string prompt,
        string agentId = "agent-test",
        string runtime = "opencode",
        string workDir = "/tmp/agent-job-fixture",
        string runnerId = "",
        bool initialLaunch = true)
    {
        AgentSessionMetadata Labels() => new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
        });
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: runtime,
            WorkDir: workDir,
            Metadata: Labels()));
        if (!initialLaunch)
            return;
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            prompt,
            "agent-launch",
            jobKey,
            Runtime: runtime,
            WorkDir: workDir,
            Metadata: Labels()));
    }

    /// <summary>
    /// Connection generation shared by AgentJob registration and poll helpers.
    /// Every claim carries a runtime-readiness witness so the Runner grain can
    /// recheck the same generation at the atomic claim boundary.
    /// </summary>
    protected const string CapabilityFenceConnection = DispatchTestExtensions.ConnectionGeneration;

    /// <summary>
    /// Re-register the runner with a connection-generation identity and
    /// seed the server-side readiness witness for each <paramref name="runtimes"/>
    /// so a later effort-bearing claim matches the fence. The poll request
    /// must also carry the same witnesses via
    /// <see cref="CapabilityFencePollRequest"/>.
    /// </summary>
    protected async Task InstallCapabilityFenceAsync(
        string runnerId,
        string projectId,
        params string[] runtimes)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*", AgentExecutionSources.Version1Capability],
            "agent-job-host",
            projectId,
            ConnectionGeneration: CapabilityFenceConnection,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        await runner.ObserveDispatchObservationAsync(
            TestRunnerGenerationExtensions.ProcessGeneration,
            new RunnerDispatchObservation(
                CapabilityFenceConnection,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: runtimes
                    .Select(runtime => new RuntimeReadinessWitness(runtime, Ready: true, Generation: 1))
                    .ToList()));
    }

    /// <summary>
    /// A poll request that carries the fence witnesses for the registered
    /// connection, so AgentJob dispatch claims clear the capability gate.
    /// </summary>
    protected static RunnerPollRequest CapabilityFencePollRequest(params string[] runtimes) =>
        new(
            [],
            [],
            RuntimeReadiness: runtimes
                .Select(runtime => new RuntimeReadinessWitness(runtime, Ready: true, Generation: 1))
                .ToList(),
            ConnectionGeneration: CapabilityFenceConnection,
            AdmissionReady: true,
            AdmissionReasonCodes: [],
            ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration);
}
