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
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workspace.Domain;
using Xunit;

namespace Mohist.Server.Tests.Workspace;

[Trait("level", "L1")]
public sealed class WebConversationWorkspaceSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public WebConversationWorkspaceSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

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
        var agentId = await CreateAgentAsync(projectId, maxConcurrentRuns: 2);

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
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            name,
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }

    private async Task<string> CreateAgentAsync(string projectId, int maxConcurrentRuns = 1)
    {
        var agentId = $"agent_{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, agentId)).CreateAsync(
            new AgentCreateData(
                projectId,
                $"web-agent-{Guid.NewGuid():N}",
                "web interaction agent",
                "Work inside the conversation workspace.",
                JsonSerializer.SerializeToElement(new { model = "openai/gpt-5.6" }),
                new[] { "coding" },
                maxConcurrentRuns));
        return agentId;
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
