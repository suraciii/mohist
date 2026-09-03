using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.Tests.Agent.Services;

/// <summary>
/// Shared launcher-spec helpers, split from AgentLauncherSpecs so the retry
/// specs can reuse them without duplicating fixture plumbing.
/// </summary>
public abstract class AgentLauncherSupportSpecs
{
    protected readonly MohistIntegrationFixture Fixture;

    protected AgentLauncherSupportSpecs(MohistIntegrationFixture fixture)
    {
        Fixture = fixture;
    }

    protected async Task<int> CountSessionsAsync(string projectId)
    {
        await using var scope = Fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            });
        return records.Count;
    }

    protected static string TriggerJobKey(string projectId, string eventId, string subscriptionId)
    {
        var identity = $"{projectId}\n{eventId}\n{subscriptionId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"agent-job-trigger-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    protected static string StableSessionId(string projectId, string eventId, string ruleId)
    {
        var identity = $"{projectId}\n{eventId}\n{ruleId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"agent-session-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    protected async Task<AgentSessionRecord?> LoadSessionByIdAsync(string sessionId)
    {
        await using var scope = Fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByIdsAsync(new[] { sessionId });
        return records.FirstOrDefault();
    }

    protected async Task<string> CreateProjectAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await Fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            verificationCommand = "true",
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var bodyElement = await response.Content.ReadFromJsonAsync<JsonElement>();
        return bodyElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    protected async Task<AgentInfo> CreateAgentAsync(string projectId, string name, string? runtime = null, int maxConcurrentRuns = 1)
    {
        using var response = await Fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                 agentConfig = runtime is null
                     ? (object)new { model = "openai/gpt-5.6" }
                     : new { model = "openai/gpt-5.6", runtime },
                skills = new[] { "coding" },
                maxConcurrentRuns,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = body.GetProperty("data").GetProperty("id").GetString()!;

        await using var scope = Fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var agent = await querier.GetByIdAsync(projectId, agentId);
        Assert.NotNull(agent);
        return agent!;
    }
}
