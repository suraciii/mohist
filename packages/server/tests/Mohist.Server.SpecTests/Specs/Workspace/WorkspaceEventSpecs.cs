using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workspace;

[Collection("MohistIntegration")]
public class WorkspaceEventSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _projectId;

    public WorkspaceEventSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _projectId = CreateProjectAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CreateManual_EmitsWorkspaceCreatedWithConformingLineage()
    {
        await _client.PostOkAsync($"/api/projects/{_projectId}/workspaces", new
        {
            name = "evt-manual",
            repos = new[] { "server" },
        });

        var row = await SingleWorkspaceEventAsync("evt-manual");
        Assert.Equal(EventCatalog.ReverseDns.WorkspaceCreated, row.Type);
        Assert.Equal($"/mohist/projects/{_projectId}/workspaces/evt-manual", row.Source);
        Assert.Equal("evt-manual", row.Subject);
        Assert.Equal("manual", Lineage(row, EventCatalog.Lineage.WorkspaceOriginKind));
        ProducerConformance.Assert(
            EventProducerFamily.Workspace,
            Extensions(row),
            new ProducerLineageContext(
                ProjectId: _projectId,
                Workspace: "evt-manual",
                WorkspaceOriginKind: "manual"));
    }

    [Fact]
    public async Task IssueStart_EmitsWorkspaceCreatedWithIssueLineage()
    {
        var issueNumber = await CreateIssueAndStartAsync();

        var row = await SingleWorkspaceEventAsync($"issue-{issueNumber}");
        Assert.Equal(EventCatalog.ReverseDns.WorkspaceCreated, row.Type);
        Assert.Equal("issue", Lineage(row, EventCatalog.Lineage.WorkspaceOriginKind));
        Assert.Equal(issueNumber.ToString(), Lineage(row, EventCatalog.Lineage.Issue));
        ProducerConformance.Assert(
            EventProducerFamily.Workspace,
            Extensions(row),
            new ProducerLineageContext(
                ProjectId: _projectId,
                Workspace: $"issue-{issueNumber}",
                WorkspaceOriginKind: "issue",
                Issue: issueNumber.ToString()));
    }

    [Fact]
    public async Task IssueStart_Twice_EmitsExactlyOneCreatedEvent()
    {
        var issueNumber = await CreateIssueAndStartAsync();
        await _client.PostOkAsync($"/api/projects/{_projectId}/issues/{issueNumber}/start");

        var rows = await WorkspaceEventsAsync($"issue-{issueNumber}");
        Assert.Single(rows);
    }

    [Fact]
    public async Task Close_EmitsWorkspaceArchivedWithManualLineage()
    {
        await _client.PostOkAsync($"/api/projects/{_projectId}/workspaces", new
        {
            name = "evt-close",
            repos = new[] { "server" },
        });
        await _client.PostOkAsync($"/api/projects/{_projectId}/workspaces/evt-close/close");

        var rows = await WorkspaceEventsAsync("evt-close");
        Assert.Collection(rows,
            created => Assert.Equal(EventCatalog.ReverseDns.WorkspaceCreated, created.Type),
            archived =>
            {
                Assert.Equal(EventCatalog.ReverseDns.WorkspaceArchived, archived.Type);
                Assert.Equal("manual", Lineage(archived, EventCatalog.Lineage.WorkspaceOriginKind));
                ProducerConformance.Assert(
                    EventProducerFamily.Workspace,
                    Extensions(archived),
                    new ProducerLineageContext(
                        ProjectId: _projectId,
                        Workspace: "evt-close",
                        WorkspaceOriginKind: "manual"));
            });
    }

    [Fact]
    public async Task IssueClose_EmitsWorkspaceArchivedWithIssueLineage()
    {
        var issueNumber = await CreateIssueAndStartAsync();
        await _client.PostOkAsync($"/api/projects/{_projectId}/issues/{issueNumber}/stop");
        await _client.PostOkAsync($"/api/projects/{_projectId}/issues/{issueNumber}/close");

        var rows = await WorkspaceEventsAsync($"issue-{issueNumber}");
        Assert.Collection(rows,
            created => Assert.Equal(EventCatalog.ReverseDns.WorkspaceCreated, created.Type),
            archived =>
            {
                Assert.Equal(EventCatalog.ReverseDns.WorkspaceArchived, archived.Type);
                Assert.Equal("issue", Lineage(archived, EventCatalog.Lineage.WorkspaceOriginKind));
                Assert.Equal(issueNumber.ToString(), Lineage(archived, EventCatalog.Lineage.Issue));
                ProducerConformance.Assert(
                    EventProducerFamily.Workspace,
                    Extensions(archived),
                    new ProducerLineageContext(
                        ProjectId: _projectId,
                        Workspace: $"issue-{issueNumber}",
                        WorkspaceOriginKind: "issue",
                        Issue: issueNumber.ToString()));
            });
    }

    [Fact]
    public async Task RoutingTest_DryRun_MatchesWorkspaceCreatedEvent()
    {
        await SeedActiveAgentAsync("agent-ws");
        await _client.PostOkAsync($"/api/projects/{_projectId}/routing/rules", new
        {
            name = "ws-created",
            match = "event.type == \"com.mohist.workspace.created\"",
            agentId = "agent-ws",
            responsePrompt = "workspace {{event.workspace}} created",
        });
        await _client.PostOkAsync($"/api/projects/{_projectId}/workspaces", new
        {
            name = "evt-routed",
            repos = new[] { "server" },
        });

        var response = await _client.GetDataAsync<RoutingTestResponse>(
            $"/api/projects/{_projectId}/routing/test?limit=5");

        var trace = Assert.Single(response.Events, evt => evt.Type == EventCatalog.ReverseDns.WorkspaceCreated);
        var rule = Assert.Single(trace.Rules);
        Assert.True(rule.Matched);
        Assert.Equal("agent-ws", rule.WouldTriggerAgent);
        Assert.Equal("stop", rule.Decision);
    }

    [Fact]
    public async Task ProjectEventsFeed_ShowsWorkspaceCreatedAndArchivedEntries()
    {
        await _client.PostOkAsync($"/api/projects/{_projectId}/workspaces", new
        {
            name = "evt-feed",
            repos = new[] { "server" },
        });
        await _client.PostOkAsync($"/api/projects/{_projectId}/workspaces/evt-feed/close");

        var events = await _client.GetDataAsync<List<ProjectEventDto>>(
            $"/api/projects/{_projectId}/events?limit=50");

        var workspaceEntries = events
            .Where(entry => entry.Origin == "workspace")
            .OrderBy(entry => entry.Type)
            .ToList();
        Assert.Collection(workspaceEntries,
            archived =>
            {
                Assert.Equal(EventCatalog.ReverseDns.WorkspaceArchived, archived.Type);
                Assert.Equal("workspace", archived.SourceAggregateKind);
                Assert.Equal("evt-feed", archived.SourceAggregateId);
            },
            created =>
            {
                Assert.Equal(EventCatalog.ReverseDns.WorkspaceCreated, created.Type);
                Assert.Equal("workspace", created.SourceAggregateKind);
                Assert.Equal("evt-feed", created.SourceAggregateId);
            });

        var filtered = await _client.GetDataAsync<List<ProjectEventDto>>(
            $"/api/projects/{_projectId}/events?limit=50&types=workspace");
        Assert.Equal(2, filtered.Count(entry => entry.Origin == "workspace"));
    }

    private async Task<int> CreateIssueAndStartAsync()
    {
        using var createIssue = await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/issues",
            new { title = "Workspace event spec", isDraft = false });
        createIssue.EnsureSuccessStatusCode();
        var issue = await createIssue.Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();

        await _client.PostOkAsync($"/api/projects/{_projectId}/issues/{issueNumber}/start");
        return issueNumber;
    }

    private async Task<string> CreateProjectAsync()
    {
        var raw = $"wsevt-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var create = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "server", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        create.EnsureSuccessStatusCode();
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("CreateProject returned no id");
    }

    private async Task SeedActiveAgentAsync(string agentId)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Agents.Add(new Mohist.Server.Infrastructure.Data.Agent.AgentRow
        {
            Id = agentId,
            ProjectId = _projectId,
            Name = agentId,
            Status = Mohist.Server.Agent.Domain.AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = _projectId,
                Name = agentId,
                Status = Mohist.Server.Agent.Domain.AgentStatus.Active,
            }, Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }

    private async Task<WorkspaceEventRow> SingleWorkspaceEventAsync(string name) =>
        Assert.Single(await WorkspaceEventsAsync(name));

    private async Task<List<WorkspaceEventRow>> WorkspaceEventsAsync(string name)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var source = WorkspaceEventPersistence.WorkspaceSource(_projectId, name);
        return await db.WorkspaceEvents.AsNoTracking()
            .Where(row => row.Source == source)
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    private static string Lineage(WorkspaceEventRow row, string key) =>
        Extensions(row)[key];

    private static IReadOnlyDictionary<string, string> Extensions(WorkspaceEventRow row) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.ExtensionsJson)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record RoutingTestResponse(
        string ProjectId,
        int Last,
        string? Message,
        IReadOnlyList<RoutingTestEventTrace> Events);

    private sealed record RoutingTestEventTrace(
        string EventId,
        string Type,
        string Source,
        DateTimeOffset Time,
        IReadOnlyList<RoutingTestRuleTrace> Rules);

    private sealed record RoutingTestRuleTrace(
        string RuleId,
        string RuleName,
        int Position,
        bool Matched,
        bool Continue,
        string? Decision,
        string? WouldTriggerAgent,
        string Outcome);

    private sealed record ProjectEventDto(
        long Id,
        string Origin,
        string SourceAggregateKind,
        string SourceAggregateId,
        string Source,
        string Type,
        string Time,
        string EnvelopeId,
        string SpecVersion,
        string? Subject,
        string? DataContentType,
        JsonElement Data,
        string? RunnerId,
        int? IssueNumber,
        int? EpicNumber,
        string? SessionSourceKind,
        string? WorkflowRunId,
        string? AgentId,
        string? AgentName);
}
