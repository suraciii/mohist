using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackConnectionDiagnosticSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackConnectionDiagnosticSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Diagnostic_exposes_complete_canonical_not_configured_detail_and_legacy_connection_facts()
    {
        var seeded = await CreateConnectionAsync(agentConfig: new { });

        using var response = await _fixture.Client.GetAsync(DiagnosticPath(seeded.ProjectId, seeded.ConnectionId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        AssertCanonicalGap(
            data,
            AgentExecutabilityStates.NotConfigured,
            code: "model-missing",
            message: "A model is not configured.",
            nextAction: "Set a model in Agent settings.",
            label: "Agent settings",
            path: $"/agents/{seeded.AgentId}",
            command: $"mo agent edit {seeded.AgentId}");
        Assert.Equal(SetupProgressKind.Complete, data.GetProperty("facts").GetProperty("setupProgress").GetString());
        Assert.Equal(DesiredStateKind.Enabled, data.GetProperty("facts").GetProperty("desiredState").GetString());
        Assert.Equal(ConnectionHealthKind.Healthy, data.GetProperty("facts").GetProperty("connectionHealth").GetString());
        Assert.Equal(AgentReadinessKind.NeedsSetup, data.GetProperty("facts").GetProperty("agentReadiness").GetString());
        var executability = data.GetProperty("executability");
        Assert.False(executability.TryGetProperty("pendingLaunchNote", out var pending)
            && pending.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Diagnostic_exposes_complete_canonical_not_executable_detail_without_replacing_connection_health()
    {
        var seeded = await CreateConnectionAsync(agentConfig: new { model = "provider/model" });
        await SeedConfigurationFailureAsync(seeded);

        using var response = await _fixture.Client.GetAsync(DiagnosticPath(seeded.ProjectId, seeded.ConnectionId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        AssertCanonicalGap(
            data,
            AgentExecutabilityStates.NotExecutable,
            code: "execution-config-failure",
            message: "The runtime could not authenticate with the configured provider.",
            nextAction: "Update the Agent execution settings and run it again.",
            label: "Agent settings",
            path: $"/agents/{seeded.AgentId}",
            command: $"mo agent edit {seeded.AgentId}");
        Assert.Equal(ConnectionHealthKind.Healthy, data.GetProperty("facts").GetProperty("connectionHealth").GetString());
        Assert.False(data.GetProperty("facts").TryGetProperty("healthReason", out var healthReason)
            && healthReason.ValueKind != JsonValueKind.Null);
        Assert.Equal(AgentReadinessKind.Ready, data.GetProperty("facts").GetProperty("agentReadiness").GetString());
        Assert.Equal(ConnectionDiagnosticState.Healthy, data.GetProperty("primaryState").GetString());
    }

    [Fact]
    public async Task Connection_list_and_detail_project_canonical_executability_with_legacy_readiness()
    {
        var seeded = await CreateConnectionAsync(agentConfig: new { });

        using var listResponse = await _fixture.Client.GetAsync($"/api/projects/{seeded.ProjectId}/slack-connections");
        listResponse.EnsureSuccessStatusCode();
        var list = await ReadDataAsync(listResponse);
        var listed = Assert.Single(list.EnumerateArray());
        Assert.Equal(AgentReadinessKind.NeedsSetup, listed.GetProperty("agentReadiness").GetString());
        Assert.Equal(AgentExecutabilityStates.NotConfigured, listed.GetProperty("executability").GetProperty("state").GetString());
        Assert.Equal("model-missing", listed.GetProperty("executability").GetProperty("gaps")[0].GetProperty("code").GetString());

        using var detailResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{seeded.ProjectId}/slack-connections/{seeded.ConnectionId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await ReadDataAsync(detailResponse);
        var connection = detail.GetProperty("connection");
        Assert.Equal(AgentReadinessKind.NeedsSetup, connection.GetProperty("agentReadiness").GetString());
        Assert.Equal(AgentExecutabilityStates.NotConfigured, connection.GetProperty("executability").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Diagnostic_is_project_scoped_for_authorized_reads()
    {
        var seeded = await CreateConnectionAsync(agentConfig: new { });
        var otherProject = await CreateProjectAsync("diagnostic-other-project");

        using var response = await _fixture.Client.GetAsync(DiagnosticPath(otherProject, seeded.ConnectionId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<SeededConnection> CreateConnectionAsync(object agentConfig)
    {
        var projectId = await CreateProjectAsync("diagnostic");
        using var agentResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = $"diagnostic-agent-{Guid.NewGuid():N}",
                instructions = "Run the diagnostic task.",
                agentConfig,
            });
        agentResponse.EnsureSuccessStatusCode();
        var agentData = await ReadDataAsync(agentResponse);
        var agentId = agentData.GetProperty("id").GetString()!;

        using var connectionResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/slack-connections",
            new { agentId });
        connectionResponse.EnsureSuccessStatusCode();
        var connectionData = await ReadDataAsync(connectionResponse);
        var connectionId = connectionData.GetProperty("connection").GetProperty("id").GetString()!;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var connection = await db.AgentConnections.SingleAsync(row => row.Id == connectionId);
        var now = _fixture.TimeProvider.GetUtcNow();
        connection.SetupProgress = SetupProgressKind.Complete;
        connection.DesiredState = DesiredStateKind.Enabled;
        connection.ConnectionHealth = ConnectionHealthKind.Healthy;
        connection.HealthReason = null;
        connection.AgentReadiness = agentConfig is null ? AgentReadinessKind.NeedsSetup :
            agentConfig.GetType().GetProperty("model") is null ? AgentReadinessKind.NeedsSetup : AgentReadinessKind.Ready;
        connection.LastHeartbeatAt = now;
        await db.SaveChangesAsync();
        return new(projectId, agentId, connectionId);
    }

    private async Task SeedConfigurationFailureAsync(SeededConnection seeded)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        var jobKey = $"diagnostic-failed-job-{Guid.NewGuid():N}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.AgentJobs.Add(new AgentJobRow
        {
            JobKey = jobKey,
            State = JSON.Serialize(new AgentJobState
            {
                Status = AgentJobStatus.Failed,
                SubmittedAt = now,
                TerminalAt = now,
                Input = new AgentJobInput(
                    Prompt: "previous diagnostic run",
                    Model: "provider/model",
                    ProjectId: seeded.ProjectId,
                    Runtime: AgentConfigSchema.OpenCodeRuntime,
                    AgentId: seeded.AgentId,
                    AgentInstructions: "Run the diagnostic task.",
                    Skills: []),
                PendingSessionClose = new PendingSessionClose(
                    $"agent-job:{jobKey}:terminal",
                    AgentJobStatus.Failed.ToString(),
                    1,
                    "unauthorized",
                    "unauthorized",
                    now),
            }),
            ProjectId = seeded.ProjectId,
            AgentId = seeded.AgentId,
            Status = AgentJobStatus.Failed.ToString().ToLowerInvariant(),
            SubmittedAt = now.ToString("O"),
            TerminalAt = now.ToString("O"),
            LaunchVisibility = "visible",
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 33, 63)],
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var data = await ReadDataAsync(response);
        return data.GetProperty("id").GetString()!;
    }

    private static void AssertCanonicalGap(
        JsonElement data,
        string state,
        string code,
        string message,
        string nextAction,
        string label,
        string path,
        string command)
    {
        var executability = data.GetProperty("executability");
        Assert.Equal(state, executability.GetProperty("state").GetString());
        var gap = Assert.Single(executability.GetProperty("gaps").EnumerateArray());
        Assert.Equal(code, gap.GetProperty("code").GetString());
        Assert.Equal(message, gap.GetProperty("message").GetString());
        Assert.Equal(nextAction, gap.GetProperty("nextAction").GetString());
        var fix = gap.GetProperty("fixEntryPoint");
        Assert.Equal(label, fix.GetProperty("label").GetString());
        Assert.Equal(path, fix.GetProperty("path").GetString());
        Assert.Equal(command, fix.GetProperty("command").GetString());
    }

    private static string DiagnosticPath(string projectId, string connectionId) =>
        $"/api/projects/{projectId}/slack-connections/{connectionId}/diagnostic";

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record SeededConnection(string ProjectId, string AgentId, string ConnectionId);
}
