using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workspace.Domain;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workspace;

[Collection("MohistIntegration")]
public sealed class WebConversationWorkspaceSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public WebConversationWorkspaceSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WebConversation_Followup_ReusesSameSessionAndWorkspace()
    {
        var projectId = await CreateProjectAsync();
        var agentId = await CreateAgentAsync(projectId);

        var launch = await LaunchWebSessionAsync(projectId, agentId, "first web task");
        var sessionId = launch.GetProperty("sessionId").GetString()!;
        var workspaceName = await SessionWorkspaceNameAsync(sessionId);

        using var followup = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/followup",
            new { text = "follow-up question" });
        Assert.Equal(HttpStatusCode.OK, followup.StatusCode);

        Assert.Equal(workspaceName, await SessionWorkspaceNameAsync(sessionId));
        Assert.Single(await WorkspaceEventsAsync(projectId, workspaceName!));
    }

    [Fact]
    public async Task WebConversation_NewConversation_GetsNewWorkspace()
    {
        var projectId = await CreateProjectAsync();
        var agentId = await CreateAgentAsync(projectId);

        var first = await LaunchWebSessionAsync(projectId, agentId, "conversation one");
        var second = await LaunchWebSessionAsync(projectId, agentId, "conversation two");
        var firstSessionId = first.GetProperty("sessionId").GetString()!;
        var secondSessionId = second.GetProperty("sessionId").GetString()!;

        Assert.NotEqual(firstSessionId, secondSessionId);
        var firstWorkspace = await SessionWorkspaceNameAsync(firstSessionId);
        var secondWorkspace = await SessionWorkspaceNameAsync(secondSessionId);
        Assert.NotEqual(firstWorkspace, secondWorkspace);
        Assert.Equal(WorkspaceStatus.Active, (await FindWorkspaceAsync(projectId, firstWorkspace!))!.Status);
        Assert.Equal(WorkspaceStatus.Active, (await FindWorkspaceAsync(projectId, secondWorkspace!))!.Status);
    }

    [Fact]
    public async Task WebLaunch_IdempotentReplay_ReusesSessionWithoutNewWorkspace()
    {
        var projectId = await CreateProjectAsync();
        var agentId = await CreateAgentAsync(projectId);
        var key = $"key-{Guid.NewGuid():N}";

        var first = await LaunchWebSessionAsync(projectId, agentId, "replayable task", key);
        var replay = await LaunchWebSessionAsync(projectId, agentId, "replayable task", key);
        var sessionId = first.GetProperty("sessionId").GetString()!;

        Assert.Equal(sessionId, replay.GetProperty("sessionId").GetString());
        var workspaceName = await SessionWorkspaceNameAsync(sessionId);
        Assert.Single(await WorkspaceEventsAsync(projectId, workspaceName!));
    }

    private async Task<string> CreateProjectAsync()
    {
        var raw = $"wcs-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var create = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "server", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        create.EnsureSuccessStatusCode();
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("CreateProject returned no id");
    }

    private async Task<string> CreateAgentAsync(string projectId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = $"web-agent-{Guid.NewGuid():N}",
                description = "web interaction agent",
                instructions = "Work inside the conversation workspace.",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> LaunchWebSessionAsync(string projectId, string agentId, string prompt, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? $"key-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new { prompt });
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<WorkspaceState?> FindWorkspaceAsync(string projectId, string name)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>()
            .FindAsync(projectId, name);
    }

    private async Task<string?> SessionWorkspaceNameAsync(string sessionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return (await db.AgentSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId))
            .LabelWorkspaceName;
    }

    private async Task<List<WorkspaceEventRow>> WorkspaceEventsAsync(string projectId, string name)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var source = WorkspaceEventPersistence.WorkspaceSource(projectId, name);
        return await db.WorkspaceEvents.AsNoTracking()
            .Where(row => row.Source == source)
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    private async Task<WorkspaceEventRow> SingleWorkspaceEventAsync(string projectId, string name) =>
        Assert.Single(await WorkspaceEventsAsync(projectId, name));

    private static string Lineage(WorkspaceEventRow row, string key) =>
        Extensions(row)[key];

    private static IReadOnlyDictionary<string, string> Extensions(WorkspaceEventRow row) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.ExtensionsJson)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
