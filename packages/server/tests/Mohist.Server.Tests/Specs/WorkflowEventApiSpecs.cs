using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WorkflowEventApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"workflow-event-runner-{Guid.NewGuid():N}";

    public WorkflowEventApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task IssueEvents_ReturnsDomainEventsForCurrentWorkflowRunOnly()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"workflow-events-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow events", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
            await store.AppendTestWorkflowEventAsync(workflowRunId, new StageStarted("plan"));
            await store.AppendTestWorkflowEventAsync($"other-{Guid.NewGuid():N}", new TaskCompleted("build", "T-other"));
            await store.AppendTestWorkflowEventAsync(workflowRunId, new TaskCompleted("plan", "T-1"));
        }

        var events = await _client.GetDataAsync<WorkflowEventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");

        Assert.Contains(events, e => e.Type == nameof(WorkflowRunStarted));
        Assert.Contains(events, e => e.Type == nameof(StageStarted));
        Assert.Contains(events, e => e.Type == nameof(TaskCompleted));
        Assert.DoesNotContain(events, e => e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty("taskId", out var taskId) && taskId.GetString() == "T-other");
        var taskCompleted = Assert.Single(events, e =>
            e.Type == nameof(TaskCompleted)
            && e.Data.ValueKind == JsonValueKind.Object
            && e.Data.TryGetProperty("taskId", out var taskId)
            && taskId.GetString() == "T-1");
        Assert.Equal("plan", taskCompleted.Data.GetProperty("stage").GetString());
        Assert.Equal("T-1", taskCompleted.Data.GetProperty("taskId").GetString());
        Assert.Equal($"/workflow-runs/{workflowRunId}", taskCompleted.Source);
        Assert.Equal("1.0", taskCompleted.SpecVersion);
    }

    [Fact]
    public async Task WorkflowRunEvents_DoesNotIncludeAgentSessionStreamEvents()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"workflow-event-session-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow events exclude session events", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var sessionName = "plan";
        var session = await _fixture.Grains.GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(project.Id, workflowRunId, sessionName))
            .EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, workflowRunId, sessionName, _runnerId, "work-1", "task", "plan", issue.Title));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello from agent" } },
                new { type = "tool_call_update", payload = new { toolCallId = "tool-1", kind = "read", status = "completed" } }
            }
        });

        var events = await _client.GetDataAsync<WorkflowEventDto[]>($"/api/workflow-runs/{workflowRunId}/events");

        Assert.Contains(events, e => e.Type == nameof(WorkflowRunStarted));
        Assert.DoesNotContain(events, e => e.Type == "agent_message_chunk");
        Assert.DoesNotContain(events, e => e.Type == "tool_call_update");
    }

    [Fact]
    public async Task IssueEvents_OnLegacyRoute_ReturnsNotFound()
    {
        using var response = await _client.GetAsync("/api/issues/1/events");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record WorkflowEventDto(long Id, string Source, string Type, JsonElement Data, string Time, string SpecVersion);
}
