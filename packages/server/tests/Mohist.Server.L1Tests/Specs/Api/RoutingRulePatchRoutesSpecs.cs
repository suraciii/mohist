using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Api;

[Trait("level", "L1")]
public sealed class RoutingRulePatchRoutesSpecs : ProjectEventsApiTestSupport
{
    public RoutingRulePatchRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Patch_WithEveryFieldPresent_AppliesTheCanonicalVocabulary()
    {
        var project = await CreateProjectAsync("patch-present");
        var fromAgentId = $"agent-{Guid.NewGuid():N}";
        var toAgentId = $"agent-{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(project.Id, fromAgentId);
        await SeedActiveAgentAsync(project.Id, toAgentId);
        var rule = await CreateRuleAsync(project.Id, fromAgentId, "before");

        var updated = await _client.PatchDataAsync<RoutingRuleDto>(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}",
            new JsonObject
            {
                ["name"] = "after",
                ["match"] = "event.type == \"other.event\"",
                ["agentId"] = toAgentId,
                ["responsePrompt"] = "after prompt",
                ["continue"] = true,
            });

        Assert.Multiple(
            () => Assert.Equal("after", updated.Name),
            () => Assert.Equal("event.type == \"other.event\"", updated.Match),
            () => Assert.Equal(toAgentId, updated.AgentId),
            () => Assert.Equal("after prompt", updated.ResponsePrompt),
            () => Assert.True(updated.Continue));
    }

    [Fact]
    public async Task Patch_WithOmittedFields_PreservesStoredValues()
    {
        var project = await CreateProjectAsync("patch-omitted");
        var agentId = $"agent-{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(project.Id, agentId);
        var rule = await CreateRuleAsync(project.Id, agentId, "kept", continueOnMatch: true);

        var updated = await _client.PatchDataAsync<RoutingRuleDto>(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}",
            new JsonObject { ["name"] = "renamed" });

        Assert.Multiple(
            () => Assert.Equal("renamed", updated.Name),
            () => Assert.Equal(rule.Match, updated.Match),
            () => Assert.Equal(rule.AgentId, updated.AgentId),
            () => Assert.Equal(rule.ResponsePrompt, updated.ResponsePrompt),
            () => Assert.True(updated.Continue));
    }

    [Fact]
    public async Task Patch_WithEmptyObject_ChangesNothing()
    {
        var project = await CreateProjectAsync("patch-empty");
        var agentId = $"agent-{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(project.Id, agentId);
        var rule = await CreateRuleAsync(project.Id, agentId, "unchanged");

        var updated = await _client.PatchDataAsync<RoutingRuleDto>(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}",
            new JsonObject());

        Assert.Multiple(
            () => Assert.Equal(rule.Name, updated.Name),
            () => Assert.Equal(rule.Match, updated.Match),
            () => Assert.Equal(rule.AgentId, updated.AgentId),
            () => Assert.Equal(rule.ResponsePrompt, updated.ResponsePrompt),
            () => Assert.Equal(rule.Continue, updated.Continue),
            () => Assert.Equal(rule.UpdatedAt, updated.UpdatedAt));
    }

    [Theory]
    [InlineData("{\"name\":null}", "name_required")]
    public async Task Patch_WithDirectNull_RemainsPresentAndFollowsFieldValidation(string rawBody, string expectedCode)
    {
        var project = await CreateProjectAsync("patch-null");
        var agentId = $"agent-{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(project.Id, agentId);
        var rule = await CreateRuleAsync(project.Id, agentId, "validated");

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}",
            new StringContent(rawBody, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(expectedCode, body, StringComparison.Ordinal);
        var unchanged = await _client.GetDataAsync<RoutingRuleDto>(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}");
        Assert.Equal(rule.Name, unchanged.Name);
    }

    [Fact]
    public async Task Patch_WithNullContinue_RemainsPresentAndAppliesFalse()
    {
        var project = await CreateProjectAsync("patch-null-continue");
        var agentId = $"agent-{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(project.Id, agentId);
        var rule = await CreateRuleAsync(project.Id, agentId, "nulling", continueOnMatch: true);

        var updated = await _client.PatchDataAsync<RoutingRuleDto>(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}",
            new JsonObject { ["continue"] = null });

        Assert.Multiple(
            () => Assert.False(updated.Continue),
            () => Assert.Equal(rule.Name, updated.Name),
            () => Assert.Equal(rule.Match, updated.Match),
            () => Assert.Equal(rule.AgentId, updated.AgentId),
            () => Assert.Equal(rule.ResponsePrompt, updated.ResponsePrompt));
    }

    // C# member spellings and alternate casing are not presence tokens: the
    // PATCH vocabulary is exactly the lowercase JSON names, and an alias must
    // fail closed by leaving every stored field untouched.
    [Theory]
    [InlineData("""{"Name":"member-spelled name"}""")]
    public async Task Patch_WithCSharpMemberSpelling_IsNotAPresenceTokenAndDoesNotMutate(string rawBody)
    {
        var project = await CreateProjectAsync("patch-member-spelling");
        var agentId = $"agent-{Guid.NewGuid():N}";
        await SeedActiveAgentAsync(project.Id, agentId);
        var rule = await CreateRuleAsync(project.Id, agentId, "canonical");

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}",
            new StringContent(rawBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var unchanged = await _client.GetDataAsync<RoutingRuleDto>(
            $"/api/projects/{project.Id}/routing/rules/{rule.Id}");
        Assert.Multiple(
            () => Assert.Equal(rule.Name, unchanged.Name),
            () => Assert.Equal(rule.Match, unchanged.Match),
            () => Assert.Equal(rule.AgentId, unchanged.AgentId),
            () => Assert.Equal(rule.ResponsePrompt, unchanged.ResponsePrompt),
            () => Assert.Equal(rule.Continue, unchanged.Continue),
            () => Assert.Equal(rule.UpdatedAt, unchanged.UpdatedAt));
    }

    private async Task<RoutingRuleDto> CreateRuleAsync(string projectId, string agentId, string name, bool continueOnMatch = false)
    {
        return await _client.PostDataAsync<RoutingRuleDto>(
            $"/api/projects/{projectId}/routing/rules",
            new
            {
                name,
                match = "event.type == \"test.event\"",
                agentId,
                responsePrompt = $"prompt for {name}",
                @continue = continueOnMatch,
            });
    }

    private async Task SeedActiveAgentAsync(string projectId, string agentId)
    {
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = agentId,
                Status = AgentStatus.Active,
            }, Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
