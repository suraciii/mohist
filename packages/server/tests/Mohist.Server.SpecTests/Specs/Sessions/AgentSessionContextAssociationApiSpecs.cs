using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class AgentSessionContextAssociationApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSessionContextAssociationApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueAssociation_ReturnsSessionsReferencingThatIssue()
    {
        var project = await CreateProjectAsync("issue-assoc");
        var agentId = "agent_issueAssoc";
        var agentName = "issue-assoc-agent";
        var sessionId = $"sess-{Guid.NewGuid():N}";

        var issueInfo = await CreateIssueAsync(project, "Issue for agent session");

        await InsertGenericSessionWithContextAsync(project, sessionId, agentId, agentName,
            issueNumber: issueInfo.Number.ToString());

        var result = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/issues/{issueInfo.Number}/agent-sessions");

        var items = result.EnumerateArray().ToList();
        Assert.Single(items);
        var entry = items[0];
        Assert.Equal(sessionId, entry.GetProperty("sessionId").GetString());
        Assert.Equal(agentId, entry.GetProperty("agentId").GetString());
        Assert.Equal(agentName, entry.GetProperty("agentName").GetString());
        Assert.NotNull(entry.GetProperty("status").GetString());
        Assert.NotNull(entry.GetProperty("createdAt").GetString());
        Assert.NotNull(entry.GetProperty("sessionLink").GetString());
        Assert.Contains(sessionId, entry.GetProperty("sessionLink").GetString()!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueAssociation_EmptyList_Returns200WithEmptyArray()
    {
        var project = await CreateProjectAsync("issue-empty");
        var issueInfo = await CreateIssueAsync(project, "Empty issue");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/issues/{issueInfo.Number}/agent-sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
        Assert.Empty(body.GetProperty("data").EnumerateArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task EpicAssociation_ReturnsSessionsReferencingThatEpic()
    {
        var project = await CreateProjectAsync("epic-assoc");
        var agentId = "agent_epicAssoc";
        var agentName = "epic-assoc-agent";
        var sessionId = $"sess-{Guid.NewGuid():N}";

        var epic = await CreateEpicAsync(project, "Epic for agent session");

        await InsertGenericSessionWithContextAsync(project, sessionId, agentId, agentName,
            epicNumber: epic.Number!.Value.ToString());

        var result = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/epics/{epic.Number!.Value}/agent-sessions");

        var items = result.EnumerateArray().ToList();
        Assert.Single(items);
        var entry = items[0];
        Assert.Equal(sessionId, entry.GetProperty("sessionId").GetString());
        Assert.Equal(agentId, entry.GetProperty("agentId").GetString());
        Assert.Equal(agentName, entry.GetProperty("agentName").GetString());
        Assert.NotNull(entry.GetProperty("status").GetString());
        Assert.NotNull(entry.GetProperty("createdAt").GetString());
        Assert.NotNull(entry.GetProperty("sessionLink").GetString());
        Assert.Contains(sessionId, entry.GetProperty("sessionLink").GetString()!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task EpicAssociation_EmptyList_Returns200WithEmptyArray()
    {
        var project = await CreateProjectAsync("epic-empty");
        var epic = await CreateEpicAsync(project, "Empty epic");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/epics/{epic.Number!.Value}/agent-sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
        Assert.Empty(body.GetProperty("data").EnumerateArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task EpicAssociation_ByEpicId_ResolvesCorrectly()
    {
        var project = await CreateProjectAsync("epic-by-id");
        var agentId = "agent_epicById";
        var agentName = "epic-by-id-agent";
        var sessionId = $"sess-{Guid.NewGuid():N}";

        var epic = await CreateEpicAsync(project, "Epic by id");

        await InsertGenericSessionWithContextAsync(project, sessionId, agentId, agentName,
            epicNumber: epic.Number!.Value.ToString());

        var result = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/epics/{epic.Id}/agent-sessions");

        var items = result.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(sessionId, items[0].GetProperty("sessionId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task EpicAssociation_UnknownEpicRef_Returns404()
    {
        var project = await CreateProjectAsync("epic-404");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/epics/epic_unknown/agent-sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task InsertGenericSessionWithContextAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        string? issueNumber = null,
        string? epicNumber = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };
        if (issueNumber is not null)
            labels[GenericAgentSessionMetadata.IssueNumber] = issueNumber;
        if (epicNumber is not null)
            labels[GenericAgentSessionMetadata.EpicNumber] = epicNumber;

        var createdAt = TestTime.UtcDateTime;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: createdAt,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = "opened",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(id);
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        var project = await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "placeholder",
            GitUrl = "git@example.com:placeholder.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        await projectGrain.AddRepositoryAsync("main", $"file://{Guid.NewGuid():N}", "main");
        return project.Id;
    }

    private async Task<IssueInfo> CreateIssueAsync(string projectId, string title)
    {
        var number = await _fixture.Grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await grain.CreateAsync(projectId, number, title, body: null, labels: null, priority: null, repositoryRef: null, risk: null, isDraft: true);
        return new IssueInfo(number);
    }

    private async Task<EpicDto> CreateEpicAsync(string projectId, string title)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/epics",
            new { title, description = "test epic", priority = "p2" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        return new EpicDto(
            data.GetProperty("id").GetString()!,
            data.TryGetProperty("number", out var n) ? n.GetInt32() : null);
    }

    private sealed record IssueInfo(int Number);

    private sealed record EpicDto(string Id, int? Number);
}
