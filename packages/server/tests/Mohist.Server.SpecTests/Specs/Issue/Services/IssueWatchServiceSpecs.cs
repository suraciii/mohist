using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Services;

/// <summary>
/// Service-level specs for the per-issue Agent watch state machine in
/// <see cref="WatchEntryStore"/>. Asserts the watching / muted
/// transitions, idempotency, and list-grouping semantics without going
/// through HTTP. The route contract (404 unknown project, 404 unknown
/// issue, 404 archived agent, 409 agent_archived, 404 watch list
/// endpoints, JSON shape of <c>Watching</c> / <c>Muted</c> arrays) stays
/// in <c>IssueWatchApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueWatchServiceSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueWatchServiceSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private WatchEntryStore ResolveStore() =>
        _fixture.Services.GetRequiredService<WatchEntryStore>();

    [Fact]
    public async Task AddAsync_WithNoPriorDeclaration_AddsWatchingEntry()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();

        var entry = await store.AddAsync(projectId, issueNumber, agentId);

        Assert.Equal(projectId, entry.ProjectId);
        Assert.Equal(issueNumber, entry.IssueNumber);
        Assert.Equal(agentId, entry.AgentId);
        Assert.Equal(WatchEntryState.Watching, entry.State);

        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Single(groups.Watching, e => e.AgentId == agentId);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task AddAsync_OnMutedDeclaration_TransitionsToWatching()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();
        await store.RemoveAsync(projectId, issueNumber, agentId);

        await store.AddAsync(projectId, issueNumber, agentId);

        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Single(groups.Watching, e => e.AgentId == agentId);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task AddAsync_OnExistingWatching_IsIdempotent()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();

        var first = await store.AddAsync(projectId, issueNumber, agentId);
        var second = await store.AddAsync(projectId, issueNumber, agentId);

        Assert.Equal(first.CreatedAt, second.CreatedAt);
        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Single(groups.Watching, e => e.AgentId == agentId);
    }

    [Fact]
    public async Task RemoveAsync_OnWatchingDeclaration_RemovesEntry()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();
        await store.AddAsync(projectId, issueNumber, agentId);

        var result = await store.RemoveAsync(projectId, issueNumber, agentId);

        Assert.Null(result);
        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Empty(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task RemoveAsync_WithNoPriorDeclaration_RecordsMuted()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();

        var entry = await store.RemoveAsync(projectId, issueNumber, agentId);

        Assert.NotNull(entry);
        Assert.Equal(WatchEntryState.Muted, entry!.State);
        Assert.Equal(agentId, entry.AgentId);

        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Empty(groups.Watching);
        Assert.Single(groups.Muted, e => e.AgentId == agentId);
    }

    [Fact]
    public async Task RemoveAsync_OnExistingMuted_IsIdempotent()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();

        var first = await store.RemoveAsync(projectId, issueNumber, agentId);
        var second = await store.RemoveAsync(projectId, issueNumber, agentId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.CreatedAt, second!.CreatedAt);
        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Single(groups.Muted, e => e.AgentId == agentId);
    }

    [Fact]
    public async Task ListAsync_WithMixedEntries_GroupsWatchingAndMuted()
    {
        var (projectId, issueNumber, _) = await SeedAsync();
        var watchingA = await CreateAgentAsync(projectId, $"watch-a-{Guid.NewGuid():N}");
        var watchingB = await CreateAgentAsync(projectId, $"watch-b-{Guid.NewGuid():N}");
        var muted = await CreateAgentAsync(projectId, $"muted-{Guid.NewGuid():N}");
        var store = ResolveStore();

        await store.AddAsync(projectId, issueNumber, watchingA.Id);
        await store.AddAsync(projectId, issueNumber, watchingB.Id);
        await store.RemoveAsync(projectId, issueNumber, muted.Id);

        var groups = await store.ListAsync(projectId, issueNumber);

        Assert.Equal(2, groups.Watching.Count);
        Assert.Single(groups.Muted);
        Assert.Contains(groups.Watching, e => e.AgentId == watchingA.Id);
        Assert.Contains(groups.Watching, e => e.AgentId == watchingB.Id);
        Assert.Contains(groups.Muted, e => e.AgentId == muted.Id);
    }

    [Fact]
    public async Task ListAsync_WithNoEntries_ReturnsEmptyGroups()
    {
        var (projectId, issueNumber, _) = await SeedAsync();
        var store = ResolveStore();

        var groups = await store.ListAsync(projectId, issueNumber);

        Assert.Empty(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task AddAsync_ForArchivedAgent_ThrowsAgentArchived()
    {
        var (projectId, issueNumber, _) = await SeedAsync();
        var agent = await CreateAgentAsync(projectId, $"archived-{Guid.NewGuid():N}");
        await ArchiveAgentAsync(projectId, agent.Id);
        var store = ResolveStore();

        var exception = await Assert.ThrowsAsync<WatchEntryValidationException>(
            () => store.AddAsync(projectId, issueNumber, agent.Id));
        Assert.Equal("agent_archived", exception.Code);

        var groups = await store.ListAsync(projectId, issueNumber);
        Assert.Empty(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task AddAsync_ForUnknownAgent_ThrowsAgentNotFound()
    {
        var (projectId, issueNumber, _) = await SeedAsync();
        var store = ResolveStore();

        var exception = await Assert.ThrowsAsync<WatchEntryValidationException>(
            () => store.AddAsync(projectId, issueNumber, "agent_does_not_exist"));
        Assert.Equal("agent_not_found", exception.Code);
    }

    [Fact]
    public async Task IssueQuerier_ListAsync_ProjectsWatchingAndMutedGroups()
    {
        var (projectId, issueNumber, agentId) = await SeedAsync();
        var store = ResolveStore();
        await store.AddAsync(projectId, issueNumber, agentId);
        var querier = _fixture.Services.GetRequiredService<IssueQuerier>();

        var list = await querier.ListAsync(projectId, all: true);

        var item = Assert.Single(list, i => i.Number == issueNumber);
        Assert.Single(item.Watching, entry => entry.AgentId == agentId);
        Assert.Empty(item.Muted);
    }

    private async Task<(string ProjectId, int IssueNumber, string AgentId)> SeedAsync()
    {
        var projectId = await CreateProjectAsync();
        var (issueNumber, _) = await CreateIssueAsync(projectId);
        var agent = await CreateAgentAsync(projectId, $"agent-{Guid.NewGuid():N}");
        return (projectId, issueNumber, agent.Id);
    }

    private async Task<string> CreateProjectAsync()
    {
        var projectId = $"proj-watch-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Projects.Add(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = projectId,
            Name = $"watch-{Guid.NewGuid():N}",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task<(int Number, string IssueId)> CreateIssueAsync(string projectId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var existing = await db.Issues
            .Where(r => r.ProjectId == projectId)
            .Select(r => (int?)r.Number)
            .MaxAsync();
        var number = (existing ?? 0) + 1;
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = number,
            Title = "Watch seed",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            RepositoryRef = "main",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
        };
        db.Issues.Add(new Mohist.Server.Infrastructure.Data.Issue.IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
        return (number, $"issue/{projectId}/{number}");
    }

    private async Task<AgentDefinition> CreateAgentAsync(string projectId, string name)
    {
        var agentId = $"agent-{Guid.NewGuid():N}";
        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = name,
            Status = Mohist.Server.Agent.Domain.AgentStatus.Active,
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
        };
        var json = Mohist.Server.Infrastructure.Data.Agent.AgentStore.Serialize(agent);
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Agents.Add(new Mohist.Server.Infrastructure.Data.Agent.AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = name,
            Status = Mohist.Server.Agent.Domain.AgentStatus.Active,
            State = json,
        });
        await db.SaveChangesAsync();
        return new AgentDefinition(agentId, projectId, name);
    }

    private async Task ArchiveAgentAsync(string projectId, string agentId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.Agents.FirstAsync(r => r.ProjectId == projectId && r.Id == agentId);
        var agent = Mohist.Server.Infrastructure.Data.Agent.AgentStore.Deserialize(row.State);
        if (agent is null) throw new InvalidOperationException("Agent state deserialization failed");
        agent.Status = Mohist.Server.Agent.Domain.AgentStatus.Archived;
        agent.UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        row.State = Mohist.Server.Infrastructure.Data.Agent.AgentStore.Serialize(agent);
        row.Status = Mohist.Server.Agent.Domain.AgentStatus.Archived;
        await db.SaveChangesAsync();
    }

    private sealed record AgentDefinition(string Id, string ProjectId, string Name);
}