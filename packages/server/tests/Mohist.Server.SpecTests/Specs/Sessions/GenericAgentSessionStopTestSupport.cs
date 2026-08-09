using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class GenericAgentSessionStopTestSupport : IAsyncLifetime
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly string _runnerId = $"generic-stop-{Guid.NewGuid():N}";
    private readonly List<LaunchStopResource> _launchStopResources = [];
    private readonly List<StopResourcePrecondition> _stopResourcePreconditions = [];
    private readonly StopLifecycleProbe _stopLifecycle;

    protected GenericAgentSessionStopTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _stopLifecycle = new StopLifecycleProbe(_runnerId);
    }

    protected RunnerConnectionTracker RunnerConnections =>
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>();

    protected string RunnerConnectionId => $"{_runnerId}-conn";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        RecordStopEvent("fixture.teardown.begin");
        try
        {
            RecordStopEvent("pending.cleanup.release-all.begin");
            await ReleaseAllStopResourcePreconditionsAsync();
            RecordStopEvent("pending.cleanup.release-all.end");
            await AssertTrackedLaunchResourcesReleasedAsync();
            RecordStopEvent("fixture.teardown.resources-asserted");
        }
        finally
        {
            try
            {
                UnregisterRunnerConnection();
                RecordStopEvent("fixture.teardown.connection-unregistered");
            }
            finally
            {
                try
                {
                    await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).UnregisterAsync();
                    RecordStopEvent("fixture.teardown.runner-unregistered");
                }
                finally
                {
                    RecordStopEvent("fixture.teardown.end");
                    _stopLifecycle.WriteSummary();
                }
            }
        }
    }

    protected void RecordStopEvent(string name) => _stopLifecycle.Record(name);

    protected void AssertStopEvent(string name) =>
        Assert.Contains(name, _stopLifecycle.Snapshot());

    protected int StopEventCount(string prefix) =>
        _stopLifecycle.Snapshot().Count(name => name.StartsWith(prefix, StringComparison.Ordinal));

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
        RecordStopEvent("pending.cleanup.assert.begin");
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
        RecordStopEvent("pending.cleanup.assert.resources-released");

        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(
            GrainKey.Agent(projectId, agentId!));
        var snapshot = await gate.GetSnapshotAsync();
        Assert.Empty(snapshot.ActivePermits);
        Assert.Empty(snapshot.Waiters);
        Assert.Empty(snapshot.PendingNotifications);
        Assert.Empty(
            (await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync())
                .ActiveWorks);
        RecordStopEvent("pending.cleanup.assert.end");
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

        RecordStopEvent("pending.cleanup.precondition.begin");
        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(
            GrainKey.Agent(precondition.ProjectId, precondition.AgentId));
        await _fixture.Grains.GetGrain<IAgentJobGrain>(precondition.ActiveJobId)
            .FailAsync("stop-test-resource-terminal", precondition.AgentId);
        RecordStopEvent("pending.cleanup.job-terminal");

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
        RecordStopEvent("pending.cleanup.gate-releases-complete");

        var snapshot = await gate.GetSnapshotAsync();
        Assert.Empty(snapshot.ActivePermits);
        Assert.Empty(snapshot.Waiters);
        Assert.Empty(snapshot.PendingNotifications);
        Assert.Empty(
            (await _fixture.Grains.GetGrain<IRunnerGrain>(precondition.RunnerId).GetRuntimeStateAsync())
                .ActiveWorks);
        _stopResourcePreconditions.Remove(precondition);
        RecordStopEvent("pending.cleanup.precondition.end");
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

    protected async Task<(ProjectRef Project, string SessionId)> CreateCanonicalSessionForStopAsync(
        string sourceKind,
        string runtime = "opencode")
    {
        var project = await CreateProjectAsync($"preserves-{sourceKind}");
        var agent = await CreateAgentAsync(project.Id, "stop-agent");
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], $"{_runnerId}-host", project.Id));
        RegisterRunnerConnection();

        var sessionId = $"stop-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var metadata = sourceKind switch
        {
            "workflow" => WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext(
                project.Id,
                $"workflow-{Guid.NewGuid():N}",
                "build")),
            "agent-launch" => GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                agent.Id,
                agent.Name)),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, "Unknown AgentSession source"),
        };

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: runtime,
            WorkDir: $"/workspaces/{project.Id}",
            Metadata: metadata));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            WorkDir: $"/workspaces/{project.Id}"));
        var turnId = $"turn-{Guid.NewGuid():N}";
        var inputId = $"input-{Guid.NewGuid():N}";
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            inputId,
            turnId,
            "before stop",
            "user"));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $"{{\"role\":\"user\",\"text\":\"before stop\",\"kind\":\"task\",\"runtimeSessionId\":\"{runtimeSessionId}\",\"turnId\":\"{turnId}\"}}"),
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.MessageDelta,
                "{\"text\":\"preserved assistant text\"}"),
        }, runtimeSessionId));
        await persistence.WaitAsync();

        var activeResource = await CreateActiveLaunchResourceAsync(project, agent, "stop-resource");
        _launchStopResources.Add(activeResource);
        await EstablishStopResourcePreconditionAsync(sessionId, project, agent, activeResource);
        return (project, sessionId);
    }

    protected async Task<(ProjectRef Project, string SessionId, string TurnId)> CreateQueuedSessionForStopAsync()
    {
        var project = await CreateProjectAsync("queued");
        var agent = await CreateAgentAsync(project.Id, "queued-agent");
        var sessionId = $"queued-stop-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], $"{_runnerId}-host", project.Id));
        RegisterRunnerConnection();
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{project.Id}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                agent.Id,
                agent.Name))));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}",
            turnId,
            "queued input",
            "user"));
        await persistence.WaitAsync();

        var activeResource = await CreateActiveLaunchResourceAsync(project, agent, "queued-resource");
        _launchStopResources.Add(activeResource);
        await EstablishStopResourcePreconditionAsync(sessionId, project, agent, activeResource);
        return (project, sessionId, turnId);
    }

    protected async Task<(ProjectRef Project, string SessionId, string TurnId)> CreateExecutingSessionForStopAsync()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var turn = Assert.Single(
            await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync());
        return (project, sessionId, turn.Id);
    }

    private async Task<LaunchStopResource> CreateActiveLaunchResourceAsync(
        ProjectRef project,
        AgentRef agent,
        string prefix)
    {
        var sessionId = $"{prefix}-session-{Guid.NewGuid():N}";
        var jobId = $"{prefix}-job-{Guid.NewGuid():N}";
        var inputId = $"{prefix}-input-{Guid.NewGuid():N}";
        var turnId = $"{prefix}-turn-{Guid.NewGuid():N}";
        var workDir = $"/workspaces/{project.Id}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "pi",
            workDir,
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                agent.Id,
                agent.Name))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            $"runtime-{Guid.NewGuid():N}",
            workDir));
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "stop resource precondition",
            "agent-launch",
            jobId));

        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            sessionId,
            inputId,
            turnId,
            "stop resource precondition",
            WorkspaceName: null,
            ProjectId: project.Id,
            Runtime: "pi",
            AgentId: agent.Id));
        await job.SubmitPreparedLaunchAsync();
        using var poll = await _fixture.Client.PostAsync($"/api/runner/{_runnerId}/poll", content: null);
        poll.EnsureSuccessStatusCode();
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
        await grain.MarkInitialTurnExecutingAsync(jobId);

        return new LaunchStopResource(
            project.Id,
            agent.Id,
            _runnerId,
            jobId,
            sessionId,
            turnId);
    }

    protected async Task<(
        ProjectRef Project,
        string SessionId,
        string TurnId,
        string JobId,
        string AgentId)> CreateExecutingLaunchSessionForStopAsync()
    {
        var project = await CreateProjectAsync("launch-stop");
        var agent = await CreateAgentAsync(project.Id, "launch-stop-agent");
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], $"{_runnerId}-host", project.Id));
        RegisterRunnerConnection();

        var activeResource = await CreateActiveLaunchResourceAsync(project, agent, "launch-stop");
        _launchStopResources.Add(activeResource);
        await EstablishStopResourcePreconditionAsync(
            activeResource.SessionId,
            project,
            agent,
            activeResource);
        return (
            project,
            activeResource.SessionId,
            activeResource.TurnId,
            activeResource.JobId,
            agent.Id);
    }

    protected async Task<SessionEvidence> ReadSessionEvidenceAsync(string sessionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var record = Assert.Single(await query.ListByIdsAsync([sessionId]));
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var turns = await db.AgentSessionTranscriptTurns
            .AsNoTracking()
            .Where(turn => turn.SessionId == sessionId)
            .OrderBy(turn => turn.Sequence)
            .ThenBy(turn => turn.Id)
            .ToListAsync();
        var turnIds = turns.Select(turn => turn.Id).ToArray();
        var parts = await db.AgentSessionTranscriptParts
            .AsNoTracking()
            .Where(part => turnIds.Contains(part.TurnId))
            .OrderBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .ToListAsync();

        return new SessionEvidence(
            record.Session.Id,
            record.Label(AgentSessionQueryMetadataKeys.SourceKind),
            record.Session.Status.AgentRuntimeSessionId,
            turns.Select(turn => $"{turn.Id}|{turn.Sequence}|{turn.PromptKind}|{turn.PromptText}").ToArray(),
            parts.Select(part => $"{part.Id}|{part.Sequence}|{part.Type}|{part.Text}|{part.PayloadJson}").ToArray());
    }

    protected async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"gen-stop-{Guid.NewGuid():N}";
        if (projectName.Length > 63)
            projectName = projectName[..63];
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return new ProjectRef(project.Id);
    }

    protected async Task<AgentRef> CreateAgentAsync(string projectId, string agentName)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = agentName,
                description = $"description for {agentName}",
                instructions = $"instructions for {agentName}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, agentName);
    }

    protected sealed record ProjectRef(string Id);
    protected sealed record AgentRef(string Id, string Name);
    protected sealed record SessionEvidence(
        string SessionId,
        string? SourceKind,
        string? RuntimeSessionId,
        string[] TranscriptTurns,
        string[] TranscriptParts);
    protected sealed record ProjectDto(string Id, string Name);
    protected sealed record LaunchStopResource(
        string ProjectId,
        string AgentId,
        string RunnerId,
        string JobId,
        string SessionId,
        string TurnId);

    private sealed class StopLifecycleProbe
    {
        private readonly string _runnerId;
        private readonly ConcurrentQueue<string> _events = new();
        private long _sequence;

        public StopLifecycleProbe(string runnerId)
        {
            _runnerId = runnerId;
        }

        public void Record(string name)
        {
            _events.Enqueue(name);
            var sequence = Interlocked.Increment(ref _sequence);
            Console.Error.WriteLine($"STOP_LIFECYCLE_EVENT runner={_runnerId} event={sequence}:{name}");
            Console.Error.Flush();
        }

        public IReadOnlyList<string> Snapshot() => _events.ToArray();

        public void WriteSummary()
        {
            Console.Error.WriteLine(
                $"STOP_LIFECYCLE_TRACE runner={_runnerId} events={string.Join(" -> ", _events)}");
            Console.Error.Flush();
        }
    }

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
