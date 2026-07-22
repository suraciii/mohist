using Mohist.Server.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
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

    protected async Task OpenGenericSessionAsync(string projectId, string sessionId, PollResult polledWork)
    {
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/open",
            new
            {
                workId = polledWork.WorkId,
                workType = polledWork.WorkType,
                stage = polledWork.Stage,
                title = "Agent Job",
            });
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/attach",
            new
            {
                runtimeSessionId = sessionId,
                workDir = projectId,
                processPid = 4321,
                agentJobId = polledWork.AgentJobId,
                workId = polledWork.WorkId,
            });
    }

    protected async Task<FakeAgentRunResult> RunFakeAcpAgentThroughRuntimeEventsEndpointAsync(
        string projectId,
        string sessionId,
        PollResult polledWork,
        object[] runtimeEvents)
    {
        Assert.Equal(sessionId, polledWork.AgentSessionId);
        Assert.Equal(projectId, polledWork.ProjectId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polledWork.OwnerKind);
        Assert.Equal(string.Empty, polledWork.WorkflowRunId);
        Assert.False(string.IsNullOrWhiteSpace(polledWork.WorkId));
        Assert.False(string.IsNullOrWhiteSpace(polledWork.WorkType));
        Assert.False(string.IsNullOrWhiteSpace(polledWork.Stage));

        await OpenGenericSessionAsync(projectId, sessionId, polledWork);
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/runtime-events",
            new
            {
                workId = polledWork.WorkId,
                workType = polledWork.WorkType,
                stage = polledWork.Stage,
                runtimeSessionId = sessionId,
                runtimeEvents,
            });
        await ReportDispatchCompletedAsync(_runnerId, polledWork);

        return new FakeAgentRunResult(
            sessionId,
            polledWork.WorkId,
            polledWork.WorkType,
            polledWork.Stage,
            runtimeEvents.Select(ReadRuntimeEventType).ToArray());
    }

    protected static string ReadRuntimeEventType(object runtimeEvent)
    {
        var type = runtimeEvent.GetType().GetProperty("type")?.GetValue(runtimeEvent) as string;
        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("Fake runtime event is missing a type");
        return type;
    }

    protected async Task ReportDispatchCompletedAsync(string runnerId, PollResult polledWork)
    {
        Assert.False(string.IsNullOrWhiteSpace(polledWork.AgentJobId));
        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(polledWork.AgentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            polledWork.WorkId,
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

    protected async Task<PollResult> PollOnceAsync(string runnerId, string expectedSessionId)
    {
        var attempts = 50;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            PollResult? match = null;
            foreach (var data in dispatches)
            {
                var polledSessionId = data.TryGetProperty("agentSessionId", out var agentSessionIdElement)
                    && agentSessionIdElement.ValueKind != JsonValueKind.Null
                    ? agentSessionIdElement.GetString()
                    : null;
                if (match is null && polledSessionId == expectedSessionId)
                {
                    var workId = data.GetProperty("workId").GetString() ?? string.Empty;
                    var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement) && agentJobIdElement.ValueKind != JsonValueKind.Null
                        ? agentJobIdElement.GetString()
                        : null;
                    var projectId = data.TryGetProperty("projectId", out var projectIdElement) && projectIdElement.ValueKind != JsonValueKind.Null
                        ? projectIdElement.GetString()
                        : null;
                    var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement) && ownerKindElement.ValueKind != JsonValueKind.Null
                        ? ownerKindElement.GetString()
                        : null;
                    match = new PollResult(
                        WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
                        WorkId: workId,
                        WorkType: data.GetProperty("workType").GetString() ?? string.Empty,
                        Stage: data.GetProperty("stage").GetString() ?? string.Empty,
                        AgentJobId: agentJobId,
                        ProjectId: projectId,
                        AgentSessionId: polledSessionId,
                        OwnerKind: ownerKind);
                }
                else
                {
                    await DrainDispatchElementAsync(runnerId, data);
                }
            }

            if (match is not null) return match;
        }

        throw new InvalidOperationException($"No polled dispatch carrying AgentSessionId='{expectedSessionId}' after {attempts} attempts");
    }

    protected async Task DrainRemainingDispatchAsync(string runnerId, string? expectedSessionId = null)
    {
        var attempts = 30;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            if (dispatches.Count == 0) return;
            foreach (var data in dispatches)
            {
                var polledSessionId = data.TryGetProperty("agentSessionId", out var agentSessionIdElement)
                    && agentSessionIdElement.ValueKind != JsonValueKind.Null
                    ? agentSessionIdElement.GetString()
                    : null;
                if (expectedSessionId is not null && polledSessionId != expectedSessionId)
                    return;

                await DrainDispatchElementAsync(runnerId, data);
            }
        }
    }

    protected async Task DrainDispatchElementAsync(string runnerId, JsonElement data)
    {
        var workId = data.GetProperty("workId").GetString();
        var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement) && ownerKindElement.ValueKind != JsonValueKind.Null
            ? ownerKindElement.GetString()
            : null;

        if (!string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            return;

        var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement) && agentJobIdElement.ValueKind != JsonValueKind.Null
            ? agentJobIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(agentJobId) || string.IsNullOrWhiteSpace(workId))
            return;

        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(agentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            workId!,
            new Mohist.Server.Runner.Grains.WorkResult(
                Status: "completed",
                Message: "ok",
                Output: JSON.DeserializeElement("{}"),
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected drain report");
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

    protected sealed record PollResult(
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
