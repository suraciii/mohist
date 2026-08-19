using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Agent.Api;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// #323 AC3: every Agent definition carries an attribution anchor — an
/// agent Principal row (kind=agent, name=agent name) established at
/// creation, never removed when the Agent is archived, and never backed
/// by a credential. Activity produced by the agent's execution points at
/// that principal: the agent-session activity surface carries the agent
/// id, which is the principal id, so records resolve to the anchor
/// without any new reporting protocol.
/// </summary>
public class AgentPrincipalAttributionSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentPrincipalAttributionSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CreateAgent_EstablishesAgentPrincipalWithoutCredentials()
    {
        var projectId = await CreateProjectAsync("agent-principal-create");
        var agent = await CreateAgentAsync(projectId, "attributed-agent");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var principal = await db.Principals.AsNoTracking().SingleAsync(row => row.Id == agent.Id);
        Assert.Equal("Agent", principal.Kind);
        Assert.Equal("attributed-agent", principal.Name);
        Assert.False(await db.Credentials.AsNoTracking()
            .AnyAsync(credential => credential.PrincipalId == agent.Id));
    }

    [Fact]
    public async Task ArchiveAgent_KeepsPrincipalAsAttributionAnchor()
    {
        var projectId = await CreateProjectAsync("agent-principal-archive");
        var agent = await CreateAgentAsync(projectId, "archived-attributed-agent");

        var deleteResponse = await _fixture.Client.DeleteAsync($"/api/projects/{projectId}/agents/{agent.Id}");
        Assert.True(deleteResponse.IsSuccessStatusCode, $"delete failed: {(int)deleteResponse.StatusCode}");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var principal = await db.Principals.AsNoTracking().SingleAsync(row => row.Id == agent.Id);
        Assert.Equal("Agent", principal.Kind);
        Assert.Equal("archived-attributed-agent", principal.Name);
        var agentRow = await db.Agents.AsNoTracking().SingleAsync(row => row.Id == $"{projectId}:{agent.Id}");
        Assert.Equal("archived", agentRow.Status);
    }

    [Fact]
    public async Task AgentExecutionActivity_RecordsUnderAgentPrincipalId()
    {
        var projectId = await CreateProjectAsync("agent-principal-activity");
        var agent = await CreateAgentAsync(projectId, "activity-attributed-agent");

        // Open an agent session carrying the agent's identity labels — the
        // same labels the execution protocol reports (design: agent
        // activity points at its principal via the existing job/agent
        // identity reporting, no new protocol).
        var sessionId = Guid.NewGuid().ToString("N");
        var metadata = new AgentSessionMetadata()
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
            .WithLabel(GenericAgentSessionMetadata.AgentId, agent.Id)
            .WithLabel(GenericAgentSessionMetadata.AgentName, agent.Name);
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: $"runner-{Guid.NewGuid():N}",
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{projectId}",
            Metadata: metadata));

        // The activity surface reports the session under the agent's
        // principal id — the same id the Principals row carries — so every
        // recorded activity resolves to the anchor.
        var activity = await _fixture.Client.GetDataAsync<AgentActivityPayload>(
            $"/api/projects/{projectId}/agent/activity");
        var card = Assert.Single(activity.Sessions, candidate => candidate.SessionId == sessionId);
        Assert.Equal(agent.Id, card.AgentId);
        Assert.Equal("activity-attributed-agent", card.AgentName);
    }

    private sealed record AgentActivityPayload(
        AgentActivitySummaryPayload Summary,
        AgentActivityCardPayload[] Sessions,
        AgentActivityWaitingPayload[] Waiting,
        JsonElement Amplification);

    private sealed record AgentActivitySummaryPayload(int Active, int Waiting, int Completed, int Failed, JsonElement Slots);

    private sealed record AgentActivityCardPayload(
        int IssueNumber,
        string IssueTitle,
        string IssueStage,
        string? IssueRuntimeStatus,
        string SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        string? Model,
        string? Title,
        string CreatedAt,
        string? CompletedAt,
        string LastActivityAt,
        JsonElement? CurrentWorkItem,
        JsonElement? TaskProgress,
        JsonElement? LastActivity,
        string? FailureReason,
        string? AgentId,
        string? AgentName,
        [property: System.Text.Json.Serialization.JsonPropertyName("eventSummary")] JsonElement EventSummary,
        [property: System.Text.Json.Serialization.JsonPropertyName("usage")] JsonElement Usage);

    private sealed record AgentActivityWaitingPayload(int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);
}
