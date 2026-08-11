using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class GenericAgentSessionFollowupApiTestSupport : IAsyncLifetime
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly string _runnerId = $"generic-followup-{Guid.NewGuid():N}";

    protected GenericAgentSessionFollowupApiTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        fixture.Services.GetRequiredService<RecordingRunnerHubContext>().Clear();
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

    protected Task<HttpResponseMessage> PostGenericFollowupAsync(
        string projectId,
        string sessionId,
        object body,
        string? idempotencyKey = null)
    {
        if (idempotencyKey is null)
            return _client.PostAsJsonAsync($"/api/projects/{projectId}/agent-sessions/{sessionId}/followup", body);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    protected static async Task<string[]> GetActiveWorkSnapshotAsync(IRunnerGrain runner) =>
        (await runner.GetRuntimeStateAsync()).ActiveWorks
            .OrderBy(work => work.WorkId, StringComparer.Ordinal)
            .Select(work => $"{work.WorkId}|{work.OwnerKind}|{work.OwnerId}|{work.WorkType}")
            .ToArray();

    protected async Task<(ProjectRef Project, AgentRef Agent, string SessionId, AgentSessionInfo Info)> LaunchGenericSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var runnerId = _runnerId;
        var agent = await CreateAgentAsync(project.Id, $"gen-followup-agent-{name}");

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        using var response = await _fixture.Client.LaunchAgentSessionAsync(project.Id, agent.Id, new { prompt = $"hello from {name}" });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        return (project, agent, sessionId, info);
    }

    protected async Task<(ProjectRef Project, string SessionId)> CreateUnboundGenericLaunchSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var sessionId = $"generic-followup-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: $"input-{Guid.NewGuid():N}",
            TurnId: $"turn-{Guid.NewGuid():N}",
            Prompt: $"hello from {name}",
            Source: "agent-launch",
            JobId: $"job-{Guid.NewGuid():N}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                $"agent-{Guid.NewGuid():N}",
                $"gen-followup-agent-{name}")),
            Runtime: "opencode"));

        return (project, sessionId);
    }

    protected async Task<(ProjectRef Project, AgentRef Agent, string SessionId, AgentSessionInfo Info)> LaunchAndOpenGenericSessionAsync(string name)
    {
        var launched = await LaunchGenericSessionAsync(name);

        var runnerId = _runnerId;
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{launched.Project.Id}/{launched.SessionId}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "task",
                stage = "Build",
                title = $"session for {name}",
                issueNumber = 1,
            });

        // Attach the physical session so AgentRuntimeSessionId is set;
        // StatusName() requires this for the session to read as "active"
        // once runtime events start flowing (same shape the workflow
        // followup tests use).
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{launched.Project.Id}/{launched.SessionId}/attach",
            new
            {
                runtimeSessionId = launched.SessionId,
                workDir = WorkDirFor(launched.Project.Id),
                processPid = 1234,
            });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(launched.SessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        return (launched.Project, launched.Agent, launched.SessionId, info);
    }

    protected async Task<(ProjectRef Project, string SessionId, string RuntimeSessionId)> CreateIdleGenericSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], $"{_runnerId}-host", project.Id));

        var sessionId = $"idle-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: WorkDirFor(project.Id),
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                $"agent-{Guid.NewGuid():N}",
                "idle-agent"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            WorkDir: WorkDirFor(project.Id)));

        return (project, sessionId, runtimeSessionId);
    }

    /// <summary>
    /// Creates a workflow-shaped session via the runner's
    /// <c>POST /api/runner/{id}/sessions/{project}/{wf}/{name}/open</c>
    /// endpoint and attaches a physical session so the existing issue-scoped
    /// followup route is exercised with the same shape production uses.
    /// </summary>
    protected async Task<(ProjectRef Project, IssueRef Issue, string WorkflowRunId, string SessionName, string SessionId)> CreateWorkflowSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = $"Generic followup route shape {name}",
            body = "followup route shape test",
            labels = new Dictionary<string, string>(StringComparer.Ordinal),
            priority = "p1",
            projectId = project.Id,
            isDraft = false,
        });

        var runnerId = _runnerId;
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runnerGrain.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], $"{runnerId}-host", project.Id));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();
        await DispatchEventsAsync();
        var status = await issueGrain.GetWorkflowStatusAsync();
        var workflowRunId = status!.WorkflowRunId!;
        const string sessionName = "plan";

        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{project.Id}/{workflowRunId}/{sessionName}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "task",
                stage = "Build",
                title = $"session for {name}",
                issueNumber = issue.Number,
                workDir = WorkDirFor(project.Id),
                runtime = "opencode",
            });

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var sessionId = await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var attached = await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            AgentSessionId: sessionId,
            Model: null,
            WorkDir: WorkDirFor(project.Id),
            ChangeDir: null,
             ProcessPid: 1234,
             Runtime: "opencode"));
        Assert.Equal(WorkDirFor(project.Id), attached.WorkDir);
        Assert.Equal("opencode", attached.Runtime);

        return (project, new IssueRef(issue.Number), workflowRunId, sessionName, sessionId);
    }

    protected Task DispatchEventsAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    protected async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"gen-followup-{Guid.NewGuid():N}";
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

    protected static string WorkDirFor(string projectId) => $"/workspaces/{projectId}";

    protected sealed record ProjectRef(string Id);
    protected sealed record AgentRef(string Id, string Name);
    protected sealed record IssueRef(int Number);
    protected sealed record ProjectDto(string Id, string Name);
    protected sealed record IssueDto(int Number, string Title);
}
