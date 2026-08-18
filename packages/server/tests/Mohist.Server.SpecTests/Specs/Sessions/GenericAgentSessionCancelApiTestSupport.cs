using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class GenericAgentSessionCancelApiTestSupport : IAsyncLifetime
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly string _runnerId = $"generic-cancel-{Guid.NewGuid():N}";

    protected GenericAgentSessionCancelApiTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", content: null);
            _ = response;
        }
        catch
        {
        }
    }

    protected async Task<HttpResponseMessage> PostGenericCancelAsync(string projectId, string sessionId)
    {
        var turns = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync();
        var turnId = turns.FirstOrDefault()?.Id ?? $"missing-turn-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/stop")
        {
            Content = JsonContent.Create(new { turnId }),
        };
        request.Headers.Add("Idempotency-Key", $"stop-{Guid.NewGuid():N}");
        return await _client.SendAsync(request);
    }

    protected async Task<(ProjectRef Project, string SessionId)> CreateCanonicalSessionForCancelAsync(string sourceKind, string runtime = "opencode")
    {
        var project = await CreateProjectAsync($"preserves-{sourceKind}");
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .RegisterAsync(new RunnerInfo(
                _runnerId,
                ["spec/*"],
                $"{_runnerId}-host",
                project.Id,
                RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        tracker.Register(_runnerId, $"{_runnerId}-conn");

        var sessionId = $"cancel-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var metadata = sourceKind switch
        {
            "workflow" => WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext(
                project.Id,
                $"workflow-{Guid.NewGuid():N}",
                "build")),
            "agent-launch" => GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                $"agent-{Guid.NewGuid():N}",
                "cancel-agent")),
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
            "before cancel",
            "user"));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $"{{\"role\":\"user\",\"text\":\"before cancel\",\"kind\":\"task\",\"runtimeSessionId\":\"{runtimeSessionId}\",\"turnId\":\"{turnId}\"}}"),
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.MessageDelta,
                "{\"text\":\"preserved assistant text\"}"),
        }, runtimeSessionId));
        await persistence.WaitAsync();

        return (project, sessionId);
    }

    protected async Task<(ProjectRef Project, string SessionId, string TurnId)> CreateQueuedSessionForCancelAsync()
    {
        var project = await CreateProjectAsync("queued");
        var sessionId = $"queued-cancel-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{project.Id}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                "queued-agent",
                "queued-agent"))));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}",
            turnId,
            "queued input",
            "user"));
        await persistence.WaitAsync();
        return (project, sessionId, turnId);
    }

    protected async Task<(ProjectRef Project, string SessionId, string TurnId)> CreateExecutingSessionForCancelAsync()
    {
        var project = await CreateProjectAsync("executing");
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .RegisterAsync(new RunnerInfo(
                _runnerId,
                ["spec/*"],
                $"{_runnerId}-host",
                project.Id,
                RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>()
            .Register(_runnerId, $"{_runnerId}-conn");

        var sessionId = $"executing-cancel-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var workDir = $"/workspaces/{project.Id}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: workDir,
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                $"agent-{Guid.NewGuid():N}",
                "cancel-agent"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId, workDir));
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}",
            turnId,
            "before cancel",
            "user"));
        await grain.MarkTurnExecutingAsync(turnId);
        return (project, sessionId, turnId);
    }

    protected async Task<(ProjectRef Project, string SessionId, string TurnId, string JobId)> CreateExecutingLaunchSessionForStopAsync()
    {
        var project = await CreateProjectAsync("launch-stop");
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .RegisterAsync(new RunnerInfo(
                _runnerId,
                ["spec/*"],
                $"{_runnerId}-host",
                project.Id,
                RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(_runnerId, $"{_runnerId}-conn");

        var sessionId = $"launch-stop-{Guid.NewGuid():N}";
        var jobId = $"job-{Guid.NewGuid():N}";
        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var workDir = $"/workspaces/{project.Id}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "pi",
            workDir,
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(project.Id, "launch-stop-agent", "launch-stop-agent"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand($"runtime-{Guid.NewGuid():N}", workDir));
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId, turnId, "stop this launch", "agent-launch", jobId));

        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            sessionId, inputId, turnId, "stop this launch", WorkspaceName: null, ProjectId: project.Id, Runtime: "pi", AgentId: "launch-stop-agent"));
        await job.SubmitPreparedLaunchAsync();
        await _fixture.AgentJobDispatches.WaitForAssignmentPreparedAsync(jobId, TimeSpan.FromSeconds(5));
        var assignment = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(_runnerId, assignment.RunnerId);
        var claim = await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId)
            .TryClaimAgentJobAsync(jobId, project.Id);
        Assert.NotNull(claim);
        Assert.Equal(jobId, claim.AgentJobId);
        Assert.Equal(sessionId, claim.Dispatch.AgentSessionId);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
        await grain.MarkInitialTurnExecutingAsync(jobId);
        return (project, sessionId, turnId, jobId);
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

    protected async Task<(ProjectRef Project, AgentRef Agent, string SessionId, AgentSessionInfo Info)> LaunchAndOpenGenericSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var runnerId = _runnerId;
        var agent = await CreateAgentAsync(project.Id, $"gen-cancel-agent-{name}");

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
            runtimeCatalogs = CapabilityCatalogTestHelpers.Create(),
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        using var response = await _fixture.Client.LaunchAgentSessionAsync(project.Id, agent.Id, new { prompt = $"hello from {name}" });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

        // Open + attach the generic session so the runner's Runtime.RunnerId
        // is bound and IsActive resolves true (matches the followup
        // helper used in T-004).
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "task",
                stage = "Build",
                title = $"session for {name}",
                issueNumber = 1,
            });

        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/attach",
            new
            {
                runtimeSessionId = sessionId,
                workDir = $"/workspaces/{project.Id}",
                processPid = 1234,
            });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        return (project, agent, sessionId, info);
    }

    protected async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"gen-cancel-{Guid.NewGuid():N}";
        if (projectName.Length > 63) projectName = projectName[..63];
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
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
