using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public sealed class AgentSubscriptionApiSpecs(MohistIntegrationFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task List_EmptyAgent_ReturnsCanonicalDataAndNoConnectionState()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-empty");

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("no_connection", data.GetProperty("state").GetString());
        Assert.Equal(AgentExecutabilityStates.Unknown, data.GetProperty("executability").GetString());
        Assert.Equal("no_connection", data.GetProperty("connection").GetString());
        Assert.Empty(data.GetProperty("subscriptions").EnumerateArray());
    }

    [Fact]
    public async Task List_NotConfiguredAgentPreservesBlockedState()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-unconfigured", configured: false);

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("unconfigured", data.GetProperty("state").GetString());
        Assert.Equal(AgentExecutabilityStates.NotConfigured, data.GetProperty("executability").GetString());
        Assert.Empty(data.GetProperty("subscriptions").EnumerateArray());
    }

    [Theory]
    [InlineData("runtime-unavailable")]
    [InlineData("unavailable-runtime")]
    public async Task List_RuntimeUnavailablePreservesUnknownInsteadOfNotConfigured(string failureCategory)
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("sub-readiness");
        await SeedFailedExecutionAsync(projectId, agentId, failureCategory);

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal(AgentExecutabilityStates.Unknown, data.GetProperty("executability").GetString());
        Assert.Equal("no_connection", data.GetProperty("state").GetString());
        Assert.NotEqual("unconfigured", data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task List_InvalidInputPreservesUnknownInsteadOfNotConfigured()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("sub-invalid");
        await SeedFailedExecutionAsync(projectId, agentId, "invalid-input");

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal(AgentExecutabilityStates.Unknown, data.GetProperty("executability").GetString());
        Assert.Equal("no_connection", data.GetProperty("state").GetString());
        Assert.NotEqual("unconfigured", data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task List_ConfigurationFailureReportsNotExecutableSeparatelyFromConnectionState()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("sub-not-executable");
        await SeedFailedExecutionAsync(projectId, agentId, "unauthorized");

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("not_executable", data.GetProperty("state").GetString());
        Assert.Equal(AgentExecutabilityStates.NotExecutable, data.GetProperty("executability").GetString());
        Assert.Equal("no_connection", data.GetProperty("connection").GetString());
    }

    [Fact]
    public async Task List_UnavailableConnectionDoesNotBecomeEmptyOrNoConnection()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-unavailable");
        await SeedConnectionAsync(projectId, agentId);

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("unavailable", data.GetProperty("state").GetString());
        Assert.Equal("unavailable", data.GetProperty("connection").GetString());
        Assert.Empty(data.GetProperty("subscriptions").EnumerateArray());
    }

    [Fact]
    public async Task Create_ReplayAndDeleteAreIdempotent()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-idempotent");
        var path = Path(projectId, agentId);
        const string key = "subscription-create-retry";
        var body = new
        {
            name = "release",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
            @continue = false,
        };
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        firstRequest.Headers.Add("Idempotency-Key", key);

        using var first = await Client.SendAsync(firstRequest);
        var firstData = await ReadDataAsync(first);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        replayRequest.Headers.Add("Idempotency-Key", key);
        using var replay = await Client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayData = await ReadDataAsync(replay);
        Assert.Equal(firstData.GetProperty("id").GetString(), replayData.GetProperty("id").GetString());

        var list = await Client.GetDataAsync<JsonElement>(path);
        Assert.Single(list.GetProperty("subscriptions").EnumerateArray());

        var id = firstData.GetProperty("id").GetString()!;
        using var patched = await Client.PatchAsJsonAsync($"{path}/{id}", new { @continue = true });
        var patchedData = await ReadDataAsync(patched);
        Assert.True(patchedData.GetProperty("continue").GetBoolean());

        using var deleted = await Client.DeleteAsync($"{path}/{id}");
        using var repeatedDelete = await Client.DeleteAsync($"{path}/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeatedDelete.StatusCode);
        Assert.Equal("deleted", (await ReadDataAsync(deleted)).GetProperty("status").GetString());
        Assert.Equal("deleted", (await ReadDataAsync(repeatedDelete)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_SameKeyDifferentRequestReturnsConflict()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-key-conflict");
        var path = Path(projectId, agentId);
        const string key = "subscription-key-conflict";

        using var first = await SendCreateAsync(path, key, new
        {
            name = "release",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
            @continue = false,
        });
        using var different = await SendCreateAsync(path, key, new
        {
            name = "other",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
            @continue = false,
        });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        var body = await different.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_conflict", body.GetProperty("code").GetString());
        var list = await Client.GetDataAsync<JsonElement>(path);
        Assert.Single(list.GetProperty("subscriptions").EnumerateArray());
    }

    [Fact]
    public async Task Create_WhitespaceIsNormalizedBeforePersistenceAndReplayComparison()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-whitespace");
        var path = Path(projectId, agentId);
        const string key = "subscription-whitespace-replay";

        using var first = await SendCreateAsync(path, key, new
        {
            name = "  release  ",
            match = "  event.type == \"release\"  ",
            responsePrompt = "  Summarize the release.  ",
            @continue = true,
        });
        var firstData = await ReadDataAsync(first);
        using var replay = await SendCreateAsync(path, key, new
        {
            name = "release",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
            @continue = true,
        });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayData = await ReadDataAsync(replay);
        Assert.Equal(firstData.GetProperty("id").GetString(), replayData.GetProperty("id").GetString());
        Assert.Equal("release", firstData.GetProperty("name").GetString());
        Assert.Equal("event.type == \"release\"", firstData.GetProperty("match").GetString());
        Assert.Equal("Summarize the release.", firstData.GetProperty("responsePrompt").GetString());
    }

    [Fact]
    public async Task Patch_ContinueAcceptsBooleanAndNullWithNullResettingToFalse()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-continue-values");
        var path = Path(projectId, agentId);
        var created = await Client.PostAsJsonAsync(path, new
        {
            name = "continue-values",
            match = "event.type == \"continue\"",
            responsePrompt = "Inspect the event.",
            @continue = false,
        });
        var id = (await ReadDataAsync(created)).GetProperty("id").GetString()!;

        using var enabled = await PatchRawAsync($"{path}/{id}", "{\"continue\":true}");
        Assert.True((await ReadDataAsync(enabled)).GetProperty("continue").GetBoolean());
        using var disabled = await PatchRawAsync($"{path}/{id}", "{\"continue\":false}");
        Assert.False((await ReadDataAsync(disabled)).GetProperty("continue").GetBoolean());
        using var reset = await PatchRawAsync($"{path}/{id}", "{\"continue\":null}");
        Assert.False((await ReadDataAsync(reset)).GetProperty("continue").GetBoolean());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    public async Task Patch_InvalidContinueTypeReturnsContractErrorAndDoesNotMutate(string jsonValue)
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-invalid-continue");
        var path = Path(projectId, agentId);
        var created = await Client.PostAsJsonAsync(path, new
        {
            name = "invalid-continue",
            match = "event.type == \"invalid\"",
            responsePrompt = "Inspect the event.",
            @continue = false,
        });
        var id = (await ReadDataAsync(created)).GetProperty("id").GetString()!;

        using var response = await PatchRawAsync($"{path}/{id}", $"{{\"continue\":{jsonValue}}}");
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_subscription_request", error.GetProperty("code").GetString());
        var current = await Client.GetDataAsync<JsonElement>($"{path}");
        Assert.False(current.GetProperty("subscriptions").EnumerateArray().Single().GetProperty("continue").GetBoolean());
    }

    [Fact]
    public async Task Delete_UnknownSubscriptionReturnsNotFound()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-unknown-delete");

        using var response = await Client.DeleteAsync($"{Path(projectId, agentId)}/rule_unknown");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_ArchivedAgentReturnsExplicitConflict()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-archived");
        using var archive = await Client.DeleteAsync($"/api/projects/{projectId}/agents/{agentId}");
        archive.EnsureSuccessStatusCode();

        using var response = await Client.PostAsJsonAsync(Path(projectId, agentId), new
        {
            name = "archived-rule",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_archived", body.GetProperty("code").GetString());
    }

    private async Task<(string ProjectId, string AgentId)> CreateProjectAndAgentAsync(
        string prefix,
        string instructions = "subscription spec instructions",
        bool configured = true)
    {
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects", $"{prefix}-{Guid.NewGuid():N}");
        var agent = await Client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", new
        {
            name = "subscription-agent",
            description = "subscription spec agent",
            instructions,
            agentConfig = configured ? new { model = "openai/gpt-5.6", runtime = "pi" } : null,
            skills = Array.Empty<string>(),
            maxConcurrentRuns = 1,
        });
        return (project.Id, agent.Id);
    }

    private static string Path(string projectId, string agentId) =>
        $"/api/projects/{projectId}/agents/{agentId}/subscriptions";

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        return envelope.GetProperty("data");
    }

    private async Task<HttpResponseMessage> SendCreateAsync(string path, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PatchRawAsync(string path, string json)
    {
        return await Client.PatchAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private async Task SeedConnectionAsync(string projectId, string agentId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = $"connection_{Guid.NewGuid():N}",
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "",
            AppId = "",
            BotUserId = "",
            BotName = "",
            SetupProgress = SetupProgressKind.WaitingForSlackService,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Unhealthy,
            AgentReadiness = AgentReadinessKind.Unknown,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = fixture.TimeProvider.GetUtcNow(),
            UpdatedAt = fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedFailedExecutionAsync(string projectId, string agentId, string failureCategory)
    {
        var terminalAt = fixture.TimeProvider.GetUtcNow();
        var jobKey = $"subscription-readiness-{Guid.NewGuid():N}";
        await using var scope = fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.AgentJobs.Add(new AgentJobRow
        {
            JobKey = jobKey,
            State = JSON.Serialize(new AgentJobState
            {
                Status = AgentJobStatus.Failed,
                SubmittedAt = terminalAt,
                TerminalAt = terminalAt,
                Input = new AgentJobInput(
                    "previous execution",
                    Model: "openai/gpt-5.6",
                    ProjectId: projectId,
                    Runtime: "pi",
                    AgentId: agentId,
                    AgentInstructions: "subscription spec instructions",
                    Skills: []),
                PendingSessionClose = new PendingSessionClose(
                    $"agent-job:{jobKey}:terminal",
                    AgentJobStatus.Failed.ToString(),
                    1,
                    failureCategory,
                    failureCategory,
                    terminalAt),
            }),
            ProjectId = projectId,
            AgentId = agentId,
            Status = AgentJobStatus.Failed.ToString().ToLowerInvariant(),
            SubmittedAt = terminalAt.ToString("O"),
            TerminalAt = terminalAt.ToString("O"),
            LaunchVisibility = "visible",
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id);
    private sealed record AgentDto(string Id);
}
