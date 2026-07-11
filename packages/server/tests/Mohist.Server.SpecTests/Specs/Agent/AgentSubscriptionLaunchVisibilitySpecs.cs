using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent;

[Collection("IntegrationRunner")]
public class AgentSubscriptionLaunchVisibilitySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSubscriptionLaunchVisibilitySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SubscriptionLaunch_RecordsTriggeringEventAndSubscription()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-merge");
        var agent = await CreateAgentAsync(projectId, "trigger-merge-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "please review",
                new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                triggerLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GenericAgentSessionMetadata.TriggerEventId] = "evt_abc123",
                    [GenericAgentSessionMetadata.TriggerSubscriptionId] = "sub_def456",
                });
        }

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.Equal(agent.Id, result.AgentId);
        Assert.Equal("trigger-merge-agent", result.AgentName);

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);
        Assert.Equal(
            "evt_abc123",
            record!.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerEventId));
        Assert.Equal(
            "sub_def456",
            record.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerSubscriptionId));
    }

    [Fact]
    public async Task ManualLaunch_DoesNotClaimSubscriptionOrigin()
    {
        var projectId = await CreateProjectAsync("launcher-no-trigger");
        var agent = await CreateAgentAsync(projectId, "no-trigger-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "manual trigger",
                new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                triggerLabels: null);
        }

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);

        var labels = record!.Session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.DoesNotContain(labels, kv => kv.Key.StartsWith("mohist.io/trigger/", StringComparison.Ordinal));
    }

    private async Task<AgentSessionRecord?> LoadSessionByIdAsync(string sessionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByIdsAsync(new[] { sessionId });
        return records.FirstOrDefault();
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var bodyElement = await response.Content.ReadFromJsonAsync<JsonElement>();
        return bodyElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private async Task<AgentInfo> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { type = "opencode" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = body.GetProperty("data").GetProperty("id").GetString()!;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var agent = await querier.GetByIdAsync(projectId, agentId);
        Assert.NotNull(agent);
        return agent!;
    }
}
