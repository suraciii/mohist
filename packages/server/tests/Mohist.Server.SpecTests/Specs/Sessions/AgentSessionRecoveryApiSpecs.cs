using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class AgentSessionRecoveryApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"recovery-api-{Guid.NewGuid():N}";

    public AgentSessionRecoveryApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactEndpoint_InactiveSession_ReturnsUpdatedMetrics()
    {
        var (project, issue, work, currentSession) = await CreateAndStartSessionAsync("compact-inactive", sessionName: "plan", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(currentSession.Id, data.GetProperty("id").GetString());
        Assert.Equal("compact", data.GetProperty("operation").GetString());
        Assert.True(data.GetProperty("wasCompacted").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactEndpoint_ActiveSession_ReturnsConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-active", sessionName: "plan", attachAndStart: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("active", doc.RootElement.GetProperty("error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactEndpoint_NonexistentSession_ReturnsNotFound()
    {
        var (project, issue) = await CreateProjectAndIssueAsync("compact-not-found");

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/does-not-exist/compact", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResetEndpoint_InactiveSession_ReturnsClearedMetrics()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-inactive", sessionName: "build", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(currentSession.Id, data.GetProperty("id").GetString());
        Assert.Equal("reset", data.GetProperty("operation").GetString());
        Assert.False(data.GetProperty("wasCompacted").GetBoolean());
        Assert.False(data.TryGetProperty("agentSessionId", out _));

        using var runnerSession = await _client.GetAsync(RunnerSessionPath(currentSession));
        Assert.Equal(HttpStatusCode.OK, runnerSession.StatusCode);
        var runnerData = await runnerSession.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(runnerData.TryGetProperty("acpSessionId", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResetEndpoint_ActiveSession_ReturnsConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-active", sessionName: "build", attachAndStart: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResetEndpoint_NonexistentSession_ReturnsNotFound()
    {
        var (project, issue) = await CreateProjectAndIssueAsync("reset-not-found");

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/does-not-exist/reset", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactEndpoint_PersistsCompactionEventAndUpdatesCoderSessionRecord()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-persist", sessionName: "plan", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "compaction")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == currentSession.Id),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .ToListAsync();

        Assert.NotEmpty(parts);
        var compaction = parts.First();
        var payload = JsonDocument.Parse(compaction.PayloadJson).RootElement;
        Assert.Equal("summary", payload.GetProperty("strategy").GetString());

        var row = await db.AgentSessions.AsNoTracking()
            .SingleAsync(r => r.Id == currentSession.Id);
        Assert.Null(row.AgentSessionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task SessionMetadataEndpoint_AfterCompact_ExposesContextUsagePercent()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("compact-dto", sessionName: "plan", attachIdle: true);

        using var compactResponse = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, compactResponse.StatusCode);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");
        var usage = root.GetProperty("usage");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactEndpoint_AfterClosedSession_EmitsContextExhaustionCategoryOnMetadata()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-after-close", sessionName: "plan", attachIdle: true);

        using var compactResponse = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, compactResponse.StatusCode);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");
        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        var usage = root.GetProperty("usage");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentSessionGrain_Compact_RecoversAfterRuntimeEventsMakeSessionActive()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-deactivate", sessionName: "plan", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Theory]
    [InlineData("compact", null)]
    [InlineData("reset", null)]
    [InlineData("compact", "acp")]
    [InlineData("reset", "acp")]
    public async Task RecoveryEndpoint_LegacyBackendBinding_ReturnsRuntimeSessionMissing(
        string operation,
        string? runtime)
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            $"{operation}-legacy-missing",
            sessionName: "plan",
            attachIdle: true);
        var transcriptPath = $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript";
        var transcriptBefore = await _client.GetStringAsync(transcriptPath);

        await SetPersistedRuntimeAsync(currentSession.Id, runtime);

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/{operation}",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime_session_missing", doc.RootElement.GetProperty("code").GetString());
        var details = doc.RootElement.GetProperty("details");
        Assert.Equal(currentSession.Id, details.GetProperty("sessionId").GetString());
        Assert.Equal("reset", details.GetProperty("hint").GetString());
        Assert.Contains(currentSession.Id, doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("Reset", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

        using var metadataResponse = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        Assert.Equal(transcriptBefore, await _client.GetStringAsync(transcriptPath));
    }

    private async Task SetPersistedRuntimeAsync(string sessionId, string? runtimeName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.AgentSessions.SingleAsync(r => r.Id == sessionId);
        var state = JsonNode.Parse(row.State)?.AsObject()
            ?? throw new InvalidOperationException($"Session {sessionId} state could not be parsed.");
        var runtime = state["runtime"]?.AsObject()
            ?? throw new InvalidOperationException($"Session {sessionId} state has no runtime binding.");
        if (runtimeName is null)
            runtime.Remove("runtime");
        else
            runtime["runtime"] = runtimeName;

        if (state["status"]?["runtimeSessionLineage"] is JsonArray lineage && lineage.Count > 0)
        {
            var current = lineage[lineage.Count - 1]?.AsObject()
                ?? throw new InvalidOperationException($"Session {sessionId} current lineage entry is invalid.");
            if (runtimeName is null)
                current.Remove("runtime");
            else
                current["runtime"] = runtimeName;
        }

        row.State = state.ToJsonString();
        await db.SaveChangesAsync();

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.DeactivateForTestAsync();
        _ = await grain.GetAsync();
    }

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateAndStartSessionAsync(
        string name,
        string sessionName = "plan",
        bool attachAndStart = false,
        bool attachIdle = false)
    {
        var (project, issue) = await CreateProjectAndIssueAsync(name);
        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: $"Session api {name}",
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();
        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, sessionName, work, $"Session api {name}");

        if (attachAndStart)
        {
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { agentSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        }
        else if (attachIdle)
        {
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });
            _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        }

        return (project, issue, work, currentSession);
    }

    private async Task<(ProjectDto Project, IssueDto Issue)> CreateProjectAndIssueAsync(string name)
    {
        var projectName = $"recovery-api-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = $"Recovery api {name}", body = "track sessions", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        return (project, issue);
    }

    private async Task<CreatedSession> OpenRunnerSessionAsync(string projectId, int issueNumber, string workflowRunId, string sessionName, WorkDispatch work, string title)
    {
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title,
            issueNumber
        });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);
        var session = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        return new CreatedSession(projectId, issueNumber, workflowRunId, sessionName, session ?? throw new InvalidOperationException($"Session {workflowRunId}/{sessionName} was not created."));
    }

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    private string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    private string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }
}
