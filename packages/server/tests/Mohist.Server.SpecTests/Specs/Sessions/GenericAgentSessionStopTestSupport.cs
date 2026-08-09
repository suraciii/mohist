using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract partial class GenericAgentSessionStopTestSupport : IAsyncLifetime
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly string _runnerId = $"generic-stop-{Guid.NewGuid():N}";
    private readonly List<LaunchStopResource> _launchStopResources = [];
    private readonly List<StopResourcePrecondition> _stopResourcePreconditions = [];

    protected GenericAgentSessionStopTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    protected RunnerConnectionTracker RunnerConnections =>
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>();

    protected string RunnerConnectionId => $"{_runnerId}-conn";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAllStopResourcePreconditionsAsync();
            await AssertTrackedLaunchResourcesReleasedAsync();
        }
        finally
        {
            try
            {
                UnregisterRunnerConnection();
            }
            finally
            {
                await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).UnregisterAsync();
            }
        }
    }

    protected void RegisterRunnerConnection() =>
        RunnerConnections.Register(_runnerId, RunnerConnectionId);

    protected void UnregisterRunnerConnection()
    {
        RunnerConnections.Unregister(_runnerId, RunnerConnectionId);
        Assert.Null(RunnerConnections.GetConnectionId(_runnerId));
    }

    protected async Task AssertNoStopOwnedResourcesAsync(
        string projectId,
        string sessionId,
        string runnerId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var record = Assert.Single(await query.ListByIdsAsync([sessionId]));
        Assert.Equal(projectId, record.Label(AgentSessionQueryMetadataKeys.ProjectId));
        var agentId = record.Label(GenericAgentSessionMetadata.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(agentId));
        var precondition = Assert.Single(
            _stopResourcePreconditions,
            candidate => candidate.TargetSessionId == sessionId);
        Assert.Equal(projectId, precondition.ProjectId);
        Assert.Equal(agentId, precondition.AgentId);
        Assert.Equal(runnerId, precondition.RunnerId);
        await ReleaseStopResourcePreconditionAsync(precondition);

        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(
            GrainKey.Agent(projectId, agentId!));
        var snapshot = await gate.GetSnapshotAsync();
        Assert.Empty(snapshot.ActivePermits);
        Assert.Empty(snapshot.Waiters);
        Assert.Empty(snapshot.PendingNotifications);
        Assert.Empty(
            (await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync())
                .ActiveWorks);
    }

    private async Task ReleaseAllStopResourcePreconditionsAsync()
    {
        foreach (var precondition in _stopResourcePreconditions.ToArray())
            await ReleaseStopResourcePreconditionAsync(precondition);
    }

    private async Task ReleaseStopResourcePreconditionAsync(StopResourcePrecondition precondition)
    {
        if (!_stopResourcePreconditions.Contains(precondition))
            return;

        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(
            GrainKey.Agent(precondition.ProjectId, precondition.AgentId));
        await _fixture.Grains.GetGrain<IAgentJobGrain>(precondition.ActiveJobId)
            .FailAsync("stop-test-resource-terminal", precondition.AgentId);

        await gate.ReleaseAsync(
            precondition.ProjectId,
            precondition.AgentId,
            precondition.SecondaryToken);
        await gate.ReleaseAsync(
            precondition.ProjectId,
            precondition.AgentId,
            precondition.GrantedToken);
        await gate.ReleaseAsync(
            precondition.ProjectId,
            precondition.AgentId,
            precondition.WaitingToken);

        var snapshot = await gate.GetSnapshotAsync();
        Assert.Empty(snapshot.ActivePermits);
        Assert.Empty(snapshot.Waiters);
        Assert.Empty(snapshot.PendingNotifications);
        Assert.Empty(
            (await _fixture.Grains.GetGrain<IRunnerGrain>(precondition.RunnerId).GetRuntimeStateAsync())
                .ActiveWorks);
        _stopResourcePreconditions.Remove(precondition);
    }

    private async Task EstablishStopResourcePreconditionAsync(
        string targetSessionId,
        ProjectRef project,
        AgentRef agent,
        LaunchStopResource activeResource)
    {
        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(
            GrainKey.Agent(project.Id, agent.Id));
        var initial = await gate.GetSnapshotAsync();
        Assert.Single(initial.ActivePermits, permit => permit.OwnerId == activeResource.JobId);
        Assert.Single(
            (await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).GetRuntimeStateAsync())
                .ActiveWorks,
            work => work.OwnerId == activeResource.JobId);

        var grantedToken = $"stop-granted-{Guid.NewGuid():N}";
        var waitingToken = $"stop-waiting-{Guid.NewGuid():N}";
        var secondaryToken = $"stop-secondary-{Guid.NewGuid():N}";
        Assert.Equal(
            AgentConcurrencyAcquireResult.Waiting,
            await gate.AcquireAsync(
                project.Id,
                agent.Id,
                grantedToken,
                $"stop-granted-owner-{Guid.NewGuid():N}",
                AgentConcurrencyPermitOwnerKind.Followup,
                grantedToken));
        _stopResourcePreconditions.Add(new StopResourcePrecondition(
            targetSessionId,
            project.Id,
            agent.Id,
            _runnerId,
            activeResource.JobId,
            grantedToken,
            waitingToken,
            secondaryToken));

        await _fixture.Client.PatchOkAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}",
            new { maxConcurrentRuns = 2 });
        var secondary = await gate.AcquireAsync(
            project.Id,
            agent.Id,
            secondaryToken,
            $"stop-secondary-owner-{Guid.NewGuid():N}",
            AgentConcurrencyPermitOwnerKind.Followup,
            secondaryToken);
        Assert.Equal(AgentConcurrencyAcquireResult.Granted, secondary);
        var secondaryPermit = await gate.GetPermitAsync(secondaryToken);
        Assert.NotNull(secondaryPermit);

        await gate.ReleaseAsync(
            project.Id,
            agent.Id,
            secondaryPermit.Token,
            secondaryPermit.PermitId!,
            secondaryPermit.Generation);
        var afterGrant = await gate.GetSnapshotAsync();
        var grantedPermit = Assert.Single(
            afterGrant.ActivePermits,
            permit => permit.Token == grantedToken);
        Assert.Contains(
            afterGrant.PendingNotifications,
            notification => notification.Token == grantedToken
                && notification.PermitId == grantedPermit.PermitId);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Waiting,
            await gate.AcquireAsync(
                project.Id,
                agent.Id,
                waitingToken,
                $"stop-waiting-owner-{Guid.NewGuid():N}",
                AgentConcurrencyPermitOwnerKind.Followup,
                waitingToken));
        var established = await gate.GetSnapshotAsync();
        Assert.Contains(established.ActivePermits, permit => permit.OwnerId == activeResource.JobId);
        Assert.Contains(established.ActivePermits, permit => permit.Token == grantedToken);
        Assert.Contains(established.Waiters, waiter => waiter.Token == waitingToken);
        Assert.Contains(
            established.PendingNotifications,
            notification => notification.Token == grantedToken);
        Assert.Contains(
            (await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).GetRuntimeStateAsync())
                .ActiveWorks,
            work => work.OwnerId == activeResource.JobId);
    }

    protected async Task AssertLaunchOwnerStateAsync(
        string projectId,
        string agentId,
        string runnerId,
        string jobId,
        bool expectActive,
        bool? expectRunnerWorkActive = null)
    {
        var runnerWorkActive = expectRunnerWorkActive ?? expectActive;
        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(
            GrainKey.Agent(projectId, agentId));
        var snapshot = await gate.GetSnapshotAsync();
        var ownerPermits = snapshot.ActivePermits.Where(permit => permit.OwnerId == jobId).ToArray();
        var ownerWaiters = snapshot.Waiters.Where(waiter => waiter.OwnerId == jobId).ToArray();
        var ownerNotifications = snapshot.PendingNotifications
            .Where(notification => notification.OwnerId == jobId)
            .ToArray();
        var activeWorks = (await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync())
            .ActiveWorks.Where(work => work.OwnerId == jobId)
            .ToArray();

        if (expectActive)
        {
            Assert.Single(ownerPermits);
            Assert.Empty(ownerWaiters);
            Assert.Empty(ownerNotifications);
        }
        else
        {
            Assert.Empty(ownerPermits);
            Assert.Empty(ownerWaiters);
            Assert.Empty(ownerNotifications);
        }

        if (runnerWorkActive)
            Assert.Single(activeWorks);
        else
            Assert.Empty(activeWorks);
    }

    protected Task AssertTrackedLaunchResourcesReleasedAsync() =>
        AssertTrackedLaunchResourcesReleasedCoreAsync();

    private async Task AssertTrackedLaunchResourcesReleasedCoreAsync()
    {
        foreach (var resource in _launchStopResources)
        {
            await AssertLaunchOwnerStateAsync(
                resource.ProjectId,
                resource.AgentId,
                resource.RunnerId,
                resource.JobId,
                expectActive: false);
        }
    }

    protected sealed record LaunchStopResource(
        string ProjectId,
        string AgentId,
        string RunnerId,
        string JobId,
        string SessionId,
        string TurnId);

    private sealed record StopResourcePrecondition(
        string TargetSessionId,
        string ProjectId,
        string AgentId,
        string RunnerId,
        string ActiveJobId,
        string GrantedToken,
        string WaitingToken,
        string SecondaryToken);
}
