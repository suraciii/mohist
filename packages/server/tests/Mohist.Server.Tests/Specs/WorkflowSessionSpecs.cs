using System.Net.Http.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WorkflowSessionSpecs
{
    private readonly HttpClient _client;

    public WorkflowSessionSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var sessionName = "builder";

        var ensured = await PostRawAsync<WorkflowSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/ensure", new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            title = "Generate proposal",
            issueNumber = 7,
        });
        await PostRawAsync<WorkflowSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/attach", new
        {
            agentSessionId = "acp-1",
            workDir = "/workspace",
            model = "openai/gpt-4o",
            processPid = 123,
        });

        await PostRawAsync<SessionEventDto[]>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/events", new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            events = new object[]
            {
                new { type = "mohist_prompt", payload = new { text = "write proposal" } },
                new { type = "agent_message_chunk", payload = new { content = new { text = "done" } } },
            },
        });
        await PostRawAsync<WorkflowSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            },
        });

        var detail = await _client.GetDataAsync<WorkflowSessionDetailDto>($"/api/workflows/{workflowRunId}/sessions/{sessionName}");

        Assert.Equal(workflowRunId, ensured.WorkflowRunId);
        Assert.Equal(sessionName, detail.Session.SessionName);
        Assert.Equal("acp-1", detail.Session.AgentSessionId);
        Assert.Equal("completed", detail.Session.Status);
        Assert.Equal("openai/gpt-4o", detail.Session.Model);
        Assert.Equal([1, 2], detail.Events.Select(e => e.Sequence).ToArray());
        Assert.Equal(["mohist_prompt", "agent_message_chunk"], detail.Events.Select(e => e.Type).ToArray());
        Assert.All(detail.Events, e => Assert.Equal("proposal", e.WorkId));
    }

    private async Task<T> PostRawAsync<T>(string path, object body)
    {
        using var response = await _client.PostAsJsonAsync(path, body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
    }

    private sealed record WorkflowSessionDto(string Id, string WorkflowRunId, string SessionName, string? AgentSessionId, string Status, string? Model);
    private sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, SessionEventDto[] Events);
    private sealed record SessionEventDto(long Sequence, string Type, string? WorkId);
}