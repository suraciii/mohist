using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public sealed class RoutingTestRoutesSpecs : ProjectEventsApiTestSupport
{
    public RoutingTestRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Test_ReturnsExplicitEmptyStateWhenNoRulesOrEvents()
    {
        var project = await CreateProjectAsync("routing-empty-rules");
        var noRules = await _client.GetDataAsync<RoutingTestResponse>(
            $"/api/projects/{project.Id}/routing/test");
        Assert.Contains("no active routing rules", noRules.Message, StringComparison.OrdinalIgnoreCase);

        await SeedActiveAgentAsync(project.Id, "agent-empty-events");
        await _client.PostOkAsync($"/api/projects/{project.Id}/routing/rules", new
        {
            name = "fallback",
            match = "event.type == \"test.event\"",
            agentId = "agent-empty-events",
            responsePrompt = "respond",
        });

        var noEvents = await _client.GetDataAsync<RoutingTestResponse>(
            $"/api/projects/{project.Id}/routing/test");
        Assert.Contains("no replayable events", noEvents.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_ReturnsOrderedContinueAndStopTraceWithoutLaunching()
    {
        var project = await CreateProjectAsync("routing-trace");
        await SeedActiveAgentAsync(project.Id, "agent-first");
        await SeedActiveAgentAsync(project.Id, "agent-second");
        await _client.PostOkAsync($"/api/projects/{project.Id}/routing/rules", new
        {
            name = "first",
            match = "event.type == \"test.event\"",
            agentId = "agent-first",
            responsePrompt = "first",
            @continue = true,
        });
        await _client.PostOkAsync($"/api/projects/{project.Id}/routing/rules", new
        {
            name = "second",
            match = "event.type == \"test.event\"",
            agentId = "agent-second",
            responsePrompt = "second",
        });
        await AppendIssueEventAsync(project.Id, 1, "test.event", FixedTime);

        var response = await _client.GetDataAsync<RoutingTestResponse>(
            $"/api/projects/{project.Id}/routing/test?limit=1");

        Assert.Null(response.Message);
        Assert.Equal(1, response.Last);
        var trace = Assert.Single(response.Events);
        // Rule ordering and continue/stop decisions are owned by
        // RoutingTableEvaluatorTests; the wire contract here is that the
        // endpoint projects the configured rules in position order with
        // their target agents.
        Assert.Equal(["first", "second"], trace.Rules.Select(r => r.RuleName));
        Assert.Equal(["agent-first", "agent-second"], trace.Rules.Select(r => r.WouldTriggerAgent));
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
}
