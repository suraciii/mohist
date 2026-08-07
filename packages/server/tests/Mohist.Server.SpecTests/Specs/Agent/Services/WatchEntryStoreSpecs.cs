using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed class WatchEntryStoreSpecs : IClassFixture<MohistDbFixture>
{
    private readonly MohistDbFixture _fixture;

    public WatchEntryStoreSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Add_WithNoPriorDeclaration_CreatesWatchingEntry()
    {
        await SeedAgentAsync("project-watch-add-none", "agent-watch-add-none", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        var entry = await store.AddAsync("project-watch-add-none", 1, "agent-watch-add-none");

        Assert.Equal(WatchEntryState.Watching, entry.State);
        var groups = await store.ListAsync("project-watch-add-none", 1);
        Assert.Single(groups.Watching, e => e.AgentId == "agent-watch-add-none");
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task Add_TransitionsMutedToWatching()
    {
        await SeedAgentAsync("project-watch-unmute", "agent-watch-unmute", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        await store.RemoveAsync("project-watch-unmute", 1, "agent-watch-unmute");
        var entry = await store.AddAsync("project-watch-unmute", 1, "agent-watch-unmute");

        Assert.Equal(WatchEntryState.Watching, entry.State);
        var groups = await store.ListAsync("project-watch-unmute", 1);
        Assert.Single(groups.Watching, e => e.AgentId == "agent-watch-unmute");
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task Add_IsIdempotentWhenAlreadyWatching()
    {
        await SeedAgentAsync("project-watch-idem", "agent-watch-idem", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        var first = await store.AddAsync("project-watch-idem", 1, "agent-watch-idem");
        var second = await store.AddAsync("project-watch-idem", 1, "agent-watch-idem");

        Assert.Equal(WatchEntryState.Watching, first.State);
        Assert.Equal(WatchEntryState.Watching, second.State);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        var groups = await store.ListAsync("project-watch-idem", 1);
        Assert.Single(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task Remove_DeletesWatchingEntry()
    {
        await SeedAgentAsync("project-watch-remove-watch", "agent-watch-remove-watch", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        await store.AddAsync("project-watch-remove-watch", 1, "agent-watch-remove-watch");
        var result = await store.RemoveAsync("project-watch-remove-watch", 1, "agent-watch-remove-watch");

        Assert.Null(result);
        var groups = await store.ListAsync("project-watch-remove-watch", 1);
        Assert.Empty(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task Remove_CreatesMutedWhenNoDeclarationExists()
    {
        await SeedAgentAsync("project-watch-remove-none", "agent-watch-remove-none", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        var entry = await store.RemoveAsync("project-watch-remove-none", 1, "agent-watch-remove-none");

        Assert.NotNull(entry);
        Assert.Equal(WatchEntryState.Muted, entry!.State);
        var groups = await store.ListAsync("project-watch-remove-none", 1);
        Assert.Single(groups.Muted, e => e.AgentId == "agent-watch-remove-none");
        Assert.Empty(groups.Watching);
    }

    [Fact]
    public async Task Remove_IsIdempotentWhenAlreadyMuted()
    {
        await SeedAgentAsync("project-watch-remove-muted", "agent-watch-remove-muted", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        var first = await store.RemoveAsync("project-watch-remove-muted", 1, "agent-watch-remove-muted");
        var second = await store.RemoveAsync("project-watch-remove-muted", 1, "agent-watch-remove-muted");

        Assert.Equal(WatchEntryState.Muted, first!.State);
        Assert.Equal(WatchEntryState.Muted, second!.State);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        var groups = await store.ListAsync("project-watch-remove-muted", 1);
        Assert.Empty(groups.Watching);
        Assert.Single(groups.Muted);
    }

    [Fact]
    public async Task Add_RejectsUnknownAgent()
    {
        await SeedAgentAsync("project-watch-unknown", "agent-watch-unknown-present", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        var add = await Assert.ThrowsAsync<WatchEntryValidationException>(
            () => store.AddAsync("project-watch-unknown", 1, "agent-not-in-project"));
        Assert.Equal("agent_not_found", add.Code);
        var remove = await Assert.ThrowsAsync<WatchEntryValidationException>(
            () => store.RemoveAsync("project-watch-unknown", 1, "agent-not-in-project"));
        Assert.Equal("agent_not_found", remove.Code);

        var groups = await store.ListAsync("project-watch-unknown", 1);
        Assert.Empty(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task Add_RejectsArchivedAgent()
    {
        await SeedAgentAsync("project-watch-archived", "agent-watch-archived", AgentStatus.Archived);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        var add = await Assert.ThrowsAsync<WatchEntryValidationException>(
            () => store.AddAsync("project-watch-archived", 1, "agent-watch-archived"));
        Assert.Equal("agent_archived", add.Code);
        var remove = await Assert.ThrowsAsync<WatchEntryValidationException>(
            () => store.RemoveAsync("project-watch-archived", 1, "agent-watch-archived"));
        Assert.Equal("agent_archived", remove.Code);

        var groups = await store.ListAsync("project-watch-archived", 1);
        Assert.Empty(groups.Watching);
        Assert.Empty(groups.Muted);
    }

    [Fact]
    public async Task ListAsync_ReturnsSeparateGroupsByState()
    {
        await SeedAgentAsync("project-watch-list", "agent-watch-list-watching", AgentStatus.Active);
        await SeedAgentAsync("project-watch-list", "agent-watch-list-muted", AgentStatus.Active);
        await SeedAgentAsync("project-watch-list", "agent-watch-list-other", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WatchEntryStore>();

        await store.AddAsync("project-watch-list", 5, "agent-watch-list-watching");
        await store.RemoveAsync("project-watch-list", 5, "agent-watch-list-muted");
        await store.AddAsync("project-watch-list", 5, "agent-watch-list-other");

        var groups = await store.ListAsync("project-watch-list", 5);

        Assert.Equal(
            new[] { "agent-watch-list-other", "agent-watch-list-watching" },
            groups.Watching.Select(entry => entry.AgentId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "agent-watch-list-muted" },
            groups.Muted.Select(entry => entry.AgentId).ToArray());
    }

    private async Task SeedAgentAsync(string projectId, string agentId, string status)
    {
        using var scope = _fixture.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId, ProjectId = projectId, Name = agentId, Status = status,
            }, Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
