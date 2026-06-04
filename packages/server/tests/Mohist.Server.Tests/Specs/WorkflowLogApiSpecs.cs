using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WorkflowLogApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"workflow-log-runner-{Guid.NewGuid():N}";

    public WorkflowLogApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task IssueWorkflowLog_ReturnsWorkflowEntriesInCreatedAtOrder_WithRawPayload()
    {
        var projectName = $"workflow-log-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Workflow log ordering", projectId = project.Id });

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "workflow", "workflow_started", WorkflowRunId: "wr-1", Status: "started", Payload: new { workflowRunId = "wr-1" }));
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "stage", "stage_changed", WorkflowRunId: "wr-1", Stage: "plan", Status: "started", Payload: new { stage = "plan" }));
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "task", "task_started", WorkflowRunId: "wr-1", Stage: "plan", TaskId: "T-1", Status: "started", Payload: new { taskId = "T-1" }));
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "check", "check_started", WorkflowRunId: "wr-1", Stage: "plan", CheckName: "spec/check", Status: "started", Payload: new { check = "spec/check" }));
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "approval", "approval_requested", WorkflowRunId: "wr-1", Stage: "plan", Status: "pending", Payload: new { reason = "plan ready" }));
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "retry", "retry_scheduled", WorkflowRunId: "wr-1", Stage: "plan", Status: "queued", Payload: new { attempts = 1 }));
        }

        var response = await _client.GetDataAsync<WorkflowLogResponse>($"/api/issues/{issue.Number}/workflow-log?projectId={project.Id}");

        var types = response.Entries.Select(e => e.Type).ToArray();
        Assert.Equal(
            new[] { "issue_created", "workflow_started", "stage_changed", "task_started", "check_started", "approval_requested", "retry_scheduled" },
            types);

        var createdAt = response.Entries.Select(e => DateTime.Parse(e.CreatedAt)).ToArray();
        Assert.Equal(createdAt.OrderBy(t => t).ToArray(), createdAt);

        foreach (var entry in response.Entries)
        {
            Assert.Equal(project.Id, entry.ProjectId);
            Assert.Equal(issue.Number, entry.IssueNumber);
            Assert.NotNull(entry.Payload);
        }

        var approval = response.Entries.Single(e => e.Type == "approval_requested");
        Assert.Equal("plan ready", approval.Payload?.GetProperty("reason").GetString());

        var serialized = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("turns", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assistant", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflowLogs", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueWorkflowLog_DoesNotIncludeAgentSessionStreamEvents()
    {
        var projectName = $"workflow-log-session-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Workflow log excludes session events", projectId = project.Id });

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
            await store.AppendAsync(new EventInput(project.Id, issue.Number, "workflow", "workflow_started", WorkflowRunId: "wr-1", Status: "started"));
        }

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(project.Id, issue.Number));
        await issueGrain.StartWorkAsync();
        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var sessionName = "plan";
        var work = new WorkDispatch(
            WorkflowRunId: currentWorkflowRunId,
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issue.Title,
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));
        var session = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(project.Id, currentWorkflowRunId, sessionName))
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, sessionName, _runnerId, work.WorkId, work.WorkType, work.Stage, work.Title));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello from agent" } },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "result" }
                    }
                }
            }
        });

        var response = await _client.GetDataAsync<WorkflowLogResponse>($"/api/issues/{issue.Number}/workflow-log?projectId={project.Id}");

        var types = response.Entries.Select(e => e.Type).ToArray();
        Assert.DoesNotContain("agent_message_chunk", types);
        Assert.DoesNotContain("tool_call_update", types);
        Assert.Contains("issue_created", types);
        Assert.Contains("workflow_started", types);

        var sessionEvents = await _client.GetDataAsync<AgentSessionEventsTestResponse>($"/api/issues/{issue.Number}/sessions/{sessionName}/events?projectId={project.Id}");
        Assert.Contains(sessionEvents.Events, e => e.Type == "agent_message_chunk");
        Assert.Contains(sessionEvents.Events, e => e.Type == "tool_call_update");
    }

    [Fact]
    public async Task IssueWorkflowLog_WithNoProject_ReturnsBadRequest()
    {
        using var response = await _client.GetAsync("/api/issues/1/workflow-log");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record WorkflowLogResponse(WorkflowLogEntryDto[] Entries);
    private sealed record WorkflowLogEntryDto(string Id, string ProjectId, int IssueNumber, string Category, string Type, string? Stage, string? TaskId, string? CheckName, string CreatedAt, JsonElement? Payload);
    private sealed record AgentSessionEventsTestResponse(AgentSessionEventTestDto[] Events);
    private sealed record AgentSessionEventTestDto(long Id, long Sequence, string Type, JsonElement? Payload, string CreatedAt);
}
