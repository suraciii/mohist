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

public sealed class RoutingRuleStoreSpecs : IClassFixture<MohistDbFixture>
{
    private readonly MohistDbFixture _fixture;

    public RoutingRuleStoreSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateAndMoveKeepProjectPositionsDense()
    {
        await SeedAgentAsync("project-routing", "agent-routing", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RoutingRuleStore>();

        var a = await store.CreateAsync(NewRule("a"));
        var b = await store.CreateAsync(NewRule("b"));
        var c = await store.CreateAsync(NewRule("c"));
        await store.MoveAsync("project-routing", c.Id, a.Id, null);

        var rules = await store.ListAsync("project-routing");
        Assert.Equal(new[] { "c", "a", "b" }, rules.Select(rule => rule.Name));
        Assert.Equal(new[] { 1, 2, 3 }, rules.Select(rule => rule.Position));
    }

    [Fact]
    public async Task ArchiveIsIdempotentAndPreservesPosition()
    {
        await SeedAgentAsync("project-archive", "agent-archive", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RoutingRuleStore>();
        var created = await store.CreateAsync(NewRule("archivable", "project-archive", "agent-archive"));

        var archived = await store.ArchiveAsync(created.ProjectId, created.Id);
        var archivedAgain = await store.ArchiveAsync(created.ProjectId, created.Id);

        Assert.Equal(RoutingRuleStatus.Archived, archived!.Status);
        Assert.Equal(created.Position, archived.Position);
        Assert.Equal(archived.Position, archivedAgain!.Position);
        Assert.Equal(archived.UpdatedAt, archivedAgain.UpdatedAt);
    }

    [Fact]
    public async Task CreateWithIdempotencyKeyReplaysAndUpdateIsFinalStateIdempotent()
    {
        await SeedAgentAsync("project-idempotency", "agent-idempotency", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RoutingRuleStore>();

        var first = await store.CreateAsync(
            NewRule("retry", "project-idempotency", "agent-idempotency"),
            idempotencyKey: "retry-key");
        var replay = await store.CreateAsync(
            NewRule("retry", "project-idempotency", "agent-idempotency"),
            idempotencyKey: "retry-key");

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await store.ListAsync("project-idempotency"));

        var updated = await store.UpdateAsync(
            first.ProjectId,
            first.Id,
            null,
            null,
            null,
            null,
            true,
            new HashSet<string> { "continue" });
        var repeated = await store.UpdateAsync(
            first.ProjectId,
            first.Id,
            null,
            null,
            null,
            null,
            true,
            new HashSet<string> { "continue" });

        Assert.True(updated!.Continue);
        Assert.Equal(updated.UpdatedAt, repeated!.UpdatedAt);
    }

    [Fact]
    public async Task DeleteRetainsFinalFactForReplayButHidesItFromRoutingLists()
    {
        await SeedAgentAsync("project-delete-fact", "agent-delete-fact", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RoutingRuleStore>();

        var created = await store.CreateAsync(
            NewRule("delete-me", "project-delete-fact", "agent-delete-fact"),
            idempotencyKey: "delete-fact-key");
        var deleted = await store.DeleteAsync(created.ProjectId, created.Id);
        var repeated = await store.DeleteAsync(created.ProjectId, created.Id);

        Assert.Equal(RoutingRuleStatus.Deleted, deleted!.Status);
        Assert.Equal(RoutingRuleStatus.Deleted, repeated!.Status);
        Assert.Equal(created.Id, (await store.GetByIdempotencyKeyAsync(created.ProjectId, "delete-fact-key"))!.Id);
        Assert.Empty(await store.ListAsync(created.ProjectId));
        Assert.Null(await store.DeleteAsync(created.ProjectId, "rule-unknown"));
    }

    [Fact]
    public async Task DeleteReleasesNameForANewActiveRule()
    {
        await SeedAgentAsync("project-delete-name", "agent-delete-name", AgentStatus.Active);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RoutingRuleStore>();

        var deleted = await store.CreateAsync(NewRule("reusable", "project-delete-name", "agent-delete-name"));
        await store.DeleteAsync(deleted.ProjectId, deleted.Id);

        var replacement = await store.CreateAsync(NewRule("reusable", "project-delete-name", "agent-delete-name"));

        Assert.NotEqual(deleted.Id, replacement.Id);
        Assert.Equal("reusable", replacement.Name);
        Assert.Single(await store.ListAsync("project-delete-name"));
    }

    [Fact]
    public async Task InvalidCreateAndUpdateDoNotPersistChanges()
    {
        await SeedAgentAsync("project-validation", "agent-validation", AgentStatus.Active);
        await SeedAgentAsync("project-validation", "agent-archived", AgentStatus.Archived);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RoutingRuleStore>();

        await Assert.ThrowsAsync<RoutingRuleMatchException>(() => store.CreateAsync(NewRule("invalid", "project-validation", "agent-validation", "(event.type == \"x\"")));
        await Assert.ThrowsAsync<RoutingRuleValidationException>(() => store.CreateAsync(NewRule("missing-agent", "project-validation", "missing", "event.type == \"x\"")));
        await Assert.ThrowsAsync<RoutingRuleValidationException>(() => store.CreateAsync(NewRule("archived-agent", "project-validation", "agent-archived")));
        await Assert.ThrowsAsync<RoutingRuleValidationException>(() => store.CreateAsync(NewRule("blank-prompt", "project-validation", "agent-validation", prompt: " ")));

        var created = await store.CreateAsync(NewRule("valid", "project-validation", "agent-validation"));
        await Assert.ThrowsAsync<RoutingRuleMatchException>(() => store.UpdateAsync(created.ProjectId, created.Id, null, "(", null, null, null, new HashSet<string> { "match" }));
        var unchanged = await store.GetAsync(created.ProjectId, created.Id);
        Assert.Equal(created.Match, unchanged!.Match);
        Assert.Equal(created.Position, unchanged.Position);
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

    private static RoutingRule NewRule(string name, string projectId = "project-routing", string agentId = "agent-routing", string match = "event.type == \"x\"", string prompt = "respond") => new()
    {
        Id = $"rule_{Guid.NewGuid():N}", ProjectId = projectId, Name = name, Match = match,
        AgentId = agentId, ResponsePrompt = prompt,
    };
}
