using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class AgentSessionSpecs
{
    private readonly HttpClient _client;
    private readonly string _runnerId = "session-spec-runner";

    public AgentSessionSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_SessionApisExposeTranscript()
    {
        var projectName = $"session-spec-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        await _client.PostOkAsync($"/api/projects/{projectName}/use");
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Build session management", body = "track sessions", labels = Array.Empty<string>(), priority = "p1" });

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });

        var sessionWork = await PollUntilAgentWorkAsync();
        Assert.Equal("mohist/agent", sessionWork.Uses);
        Assert.NotNull(sessionWork.Session);

        var status = await _client.GetDataAsync<AgentStatusDto>("/api/agent/status");
        Assert.Contains(status.ActiveAgents, agent => agent.IssueNumber == issue.Number && agent.WorkId == sessionWork.WorkId);

        var session = sessionWork.Session!;
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/started", new { externalSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/events", new
        {
            events = new[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello from agent\n" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/completed", new { status = "completed", exitCode = 0 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = sessionWork.WorkId, status = "completed" });

        var sessions = await _client.GetDataAsync<CoderSessionSummaryDto[]>($"/api/issues/{issue.Number}/coder-sessions");
        Assert.Contains(sessions, s => s.Id == session.Id && s.Status == "completed");

        var detail = await _client.GetDataAsync<CoderSessionDetailDto>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}");
        Assert.Equal(session.Id, detail.Id);
        Assert.Contains("hello from agent", JsonSerializer.Serialize(detail.Turns));

        var current = await _client.GetDataAsync<AgentSessionInfoDto[]>($"/api/agent/sessions?projectId={project.Id}");
        Assert.Contains(current, s => s.SessionId == session.Id);
    }

    private async Task<WorkDispatchDto> PollUntilAgentWorkAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                await Task.Delay(20);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var work = await response.Content.ReadFromJsonAsync<WorkDispatchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty work dispatch");

            if (work.WorkType == "load")
            {
                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
                {
                    workId = work.WorkId,
                    status = "loaded",
                    output = JsonSerializer.Serialize(new { tasks = new[] { new { id = "build-1", title = "Build task", uses = "mohist/agent" } } })
                });
                continue;
            }

            if (work.Uses == "mohist/agent") return work;

            await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, status = "completed" });
        }

        Assert.Fail("No agent work dispatched");
        return default!;
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record AgentSessionDispatchDto(string Id, string ProjectId, int IssueNumber, string WorkflowRunId, string WorkId, string? Stage, string? Title, string? ExternalSessionId);
    private sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, AgentSessionDispatchDto? Session);
    private sealed record CoderSessionSummaryDto(string Id, string Status);
    private sealed record CoderSessionDetailDto(string Id, JsonElement Turns);
    private sealed record AgentSessionInfoDto(string SessionId);
    private sealed record AgentStatusDto(ActiveAgentDto[] ActiveAgents);
    private sealed record ActiveAgentDto(int IssueNumber, string WorkId, string? Stage, ActiveAgentProgressDto Progress);
    private sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
    private sealed record ActiveWorkItemDto(string Type, string Id, string Title);
    private sealed record TaskProgressDto(int Completed, int Total);
}
