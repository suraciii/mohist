using Mohist.Server.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Grains;
using Orleans;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class GenericAgentSessionTranscriptAxisTestSupport : IAsyncLifetime
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly string _runnerId = $"generic-transcript-{Guid.NewGuid():N}";

    protected GenericAgentSessionTranscriptAxisTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", content: null);
            _ = response;
        }
        catch
        {
        }
    }

    protected async Task OpenGenericSessionAsync(string projectId, string sessionId, ClaimedDispatch claimedWork)
    {
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/open",
            new
            {
                workId = claimedWork.WorkId,
                workType = claimedWork.WorkType,
                stage = claimedWork.Stage,
                title = "Agent Job",
            });
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/attach",
            new
            {
                runtimeSessionId = sessionId,
                workDir = projectId,
                processPid = 4321,
                agentJobId = claimedWork.AgentJobId,
                workId = claimedWork.WorkId,
            });
    }

    protected async Task<FakeAgentRunResult> RunFakeAcpAgentThroughRuntimeEventsEndpointAsync(
        string projectId,
        string sessionId,
        ClaimedDispatch claimedWork,
        object[] runtimeEvents)
    {
        Assert.Equal(sessionId, claimedWork.AgentSessionId);
        Assert.Equal(projectId, claimedWork.ProjectId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, claimedWork.OwnerKind);
        Assert.Equal(string.Empty, claimedWork.WorkflowRunId);
        Assert.False(string.IsNullOrWhiteSpace(claimedWork.WorkId));
        Assert.False(string.IsNullOrWhiteSpace(claimedWork.WorkType));
        Assert.False(string.IsNullOrWhiteSpace(claimedWork.Stage));

        await OpenGenericSessionAsync(projectId, sessionId, claimedWork);
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/runtime-events",
            new
            {
                workId = claimedWork.WorkId,
                workType = claimedWork.WorkType,
                stage = claimedWork.Stage,
                runtimeSessionId = sessionId,
                runtimeEvents,
            });
        await ReportDispatchCompletedAsync(_runnerId, claimedWork);

        return new FakeAgentRunResult(
            sessionId,
            claimedWork.WorkId,
            claimedWork.WorkType,
            claimedWork.Stage,
            runtimeEvents.Select(ReadRuntimeEventType).ToArray());
    }

    protected static string ReadRuntimeEventType(object runtimeEvent)
    {
        var type = runtimeEvent.GetType().GetProperty("type")?.GetValue(runtimeEvent) as string;
        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("Fake runtime event is missing a type");
        return type;
    }

    protected async Task ReportDispatchCompletedAsync(string runnerId, ClaimedDispatch claimedWork)
    {
        Assert.False(string.IsNullOrWhiteSpace(claimedWork.AgentJobId));
        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(claimedWork.AgentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            claimedWork.WorkId,
            new Mohist.Server.Runner.Grains.WorkResult(
                Status: "completed",
                Message: "ok",
                Output: JSON.DeserializeElement("{}"),
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected completed report");
    }

    protected static JsonElement FindTurnByUserText(JsonElement transcriptData, string text)
    {
        foreach (var turn in transcriptData.GetProperty("turns").EnumerateArray())
        {
            if (turn.GetProperty("user").GetProperty("text").GetString() == text)
                return turn;
        }

        throw new InvalidOperationException($"No transcript turn found for prompt '{text}'");
    }

    protected static void AssertAssistantText(JsonElement turn, string text)
    {
        Assert.Contains(turn.GetProperty("assistant").EnumerateArray(), part =>
            part.GetProperty("type").GetString() == "text"
            && (part.GetProperty("text").GetString()?.Contains(text, StringComparison.Ordinal) ?? false));
    }

    protected static void AssertAssistantTool(JsonElement turn, string toolCallId)
    {
        Assert.Contains(turn.GetProperty("assistant").EnumerateArray(), part =>
            part.GetProperty("type").GetString() == "tool"
            && part.TryGetProperty("tool", out var tool)
            && tool.GetProperty("toolCallId").GetString() == toolCallId);
    }

    protected static async Task<IReadOnlyList<JsonElement>> LoadTranscriptPartPayloadsAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        string partType)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToArrayAsync();
        var payloads = await db.AgentSessionTranscriptParts
            .AsNoTracking()
            .Where(p => turnIds.Contains(p.TurnId) && p.Type == partType)
            .OrderBy(p => p.Sequence)
            .Select(p => p.PayloadJson)
            .ToArrayAsync();
        return payloads.Select(payload => JsonSerializer.Deserialize<JsonElement>(payload)).ToArray();
    }

    protected async Task<string> CreateRunnerHomeWorkspaceAsync(
        string projectId,
        string runnerId,
        string prefix)
    {
        var workspaceName = $"{prefix}-{Guid.NewGuid():N}";
        var workspace = _fixture.Grains.GetGrain<IWorkspaceGrain>(
            GrainKey.Workspace(projectId, workspaceName));
        var now = _fixture.TimeProvider.GetUtcNow();
        await workspace.CreateManualAsync(workspaceName, [], now);
        var home = await workspace.EnsureMaterializedOnAsync(runnerId, $"/tmp/{workspaceName}", now);
        Assert.NotNull(home);
        Assert.Equal(runnerId, home.RunnerId);
        return workspaceName;
    }

    protected async Task<string> RegisterRunnerWithHomeWorkspaceAsync(
        string projectId,
        string workspacePrefix)
    {
        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{_runnerId}-host",
            projectId,
            runtimeCatalogs = CapabilityCatalogTestHelpers.Create(),
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{_runnerId}", new { slots = 2 });
        return await CreateRunnerHomeWorkspaceAsync(projectId, _runnerId, workspacePrefix);
    }

    protected async Task<ClaimedDispatch> ClaimPreparedDispatchAsync(
        string agentJobId,
        string runnerId,
        string expectedSessionId)
    {
        await _fixture.AgentJobDispatches.WaitForAssignmentPreparedAsync(
            agentJobId,
            TimeSpan.FromSeconds(5));
        var assignment = await _fixture.Grains
            .GetGrain<IAgentJobGrain>(agentJobId)
            .GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, assignment.RunnerId);

        var claim = await _fixture.Grains
            .GetGrain<IAgentJobGrain>(agentJobId)
            .ClaimNextAsync(runnerId);
        var claimed = Assert.IsType<ClaimResult>(claim);
        Assert.Equal(agentJobId, claimed.AgentJobId);
        Assert.Equal(expectedSessionId, claimed.Dispatch.AgentSessionId);

        return new ClaimedDispatch(
            WorkflowRunId: claimed.Dispatch.WorkflowRunId,
            WorkId: claimed.Dispatch.WorkId,
            WorkType: claimed.Dispatch.WorkType,
            Stage: claimed.Dispatch.Stage ?? string.Empty,
            AgentJobId: claimed.AgentJobId,
            ProjectId: claimed.Dispatch.ProjectId,
            AgentSessionId: claimed.Dispatch.AgentSessionId,
            OwnerKind: claimed.Dispatch.OwnerKind);
    }

    protected async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"generic-transcript-{Guid.NewGuid():N}";
        if (projectName.Length > 63) projectName = projectName[..63];
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return new ProjectRef(project.Id);
    }

    protected async Task<AgentRef> CreateAgentAsync(string projectId, string agentName)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = agentName,
                description = $"description for {agentName}",
                instructions = $"instructions for {agentName}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, agentName);
    }

    protected sealed record ClaimedDispatch(
        string WorkflowRunId,
        string WorkId,
        string WorkType,
        string Stage,
        string? AgentJobId,
        string? ProjectId,
        string? AgentSessionId,
        string? OwnerKind);

    protected sealed record FakeAgentRunResult(
        string SessionId,
        string WorkId,
        string WorkType,
        string Stage,
        IReadOnlyList<string> EventTypes);

    protected sealed record ProjectDto(string Id, string Name);
    protected sealed record ProjectRef(string Id);
    protected sealed record AgentRef(string Id, string Name);
}
