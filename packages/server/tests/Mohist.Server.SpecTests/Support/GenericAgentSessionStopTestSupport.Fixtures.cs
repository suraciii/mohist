using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract partial class GenericAgentSessionStopTestSupport
{
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
}
