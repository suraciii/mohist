using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Representative application composition for the session transcript read
/// path (#676): runtime events flow through the session grain into the
/// transcript store, and the transcript/metadata endpoints project the
/// persisted parts. The persistence rules (turn/append/merge/ordering) and
/// summary reduction are owned by the UnitTests transcript store tests and
/// the accumulator/builder/projector tests; only route binding and the
/// public/raw view contract stay here.
/// </summary>
public class AgentSessionTranscriptProjectionSpecs : AgentSessionTestSupport
{
    public AgentSessionTranscriptProjectionSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SessionTranscriptEndpoints_PersistAggregatedPartsAndProjectPublicRawViews()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("transcript-representative", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var session = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "[mohist-workspace-anchor]\n/workspaces/internal\n[/mohist-workspace-anchor]\n\ninternal system prompt\n\nplan the refactor", kind = "task" } },
                new { type = "model.resolved", payload = new { resolvedModel = "gpt-5.6-sol" } },
                new { type = "message.delta", payload = new { text = "first", messageId = "msg-1" } },
                new { type = "message.delta", payload = new { text = " second", messageId = "msg-1" } },
                new { type = "reasoning.delta", payload = new { text = "thinking", messageId = "reason-1" } },
                new { type = "reasoning.delta", payload = new { text = "deeper", messageId = "reason-2" } },
                new
                {
                    type = "tool_call.started",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read README", rawInput = new { filePath = "README.md" } }
                },
                new
                {
                    type = "tool_call.completed",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "completed", title = "Read README", rawOutput = new { content = "# Project" } }
                },
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 5, persistence);

        await using var db = await dbFactory.CreateDbContextAsync();
        var dbParts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal(
            [TranscriptPartTypes.Model, TranscriptPartTypes.Text, TranscriptPartTypes.Reasoning, TranscriptPartTypes.Reasoning, TranscriptPartTypes.Tool],
            dbParts.Select(p => p.Type).ToArray());
        Assert.Equal("first second", dbParts[1].Text);
        Assert.Equal(2, dbParts[1].RawEventCount);
        Assert.Equal("thinking", dbParts[2].Text);
        Assert.Equal(1, dbParts[3].RawEventCount);

        var response = await _client.GetDataAsync<AgentSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");
        Assert.Equal(5, response.PartCount);
        var turn = Assert.Single(response.Turns);
        Assert.Equal("mohist", turn.User.Role);
        Assert.Equal("task", turn.User.Kind);
        Assert.Equal("plan the refactor", turn.User.Text);

        Assert.Equal(4, turn.Assistant.Length);
        Assert.Equal("text", turn.Assistant[0].Type);
        Assert.Equal("first second", turn.Assistant[0].Text);
        Assert.Equal("reasoning", turn.Assistant[1].Type);
        Assert.Equal("thinking", turn.Assistant[1].Text);
        Assert.Equal("reasoning", turn.Assistant[2].Type);
        Assert.Equal("deeper", turn.Assistant[2].Text);
        Assert.Equal("tool", turn.Assistant[3].Type);
        var toolPart = turn.Assistant[3].Tool;
        Assert.NotNull(toolPart);
        Assert.Equal("tool-1", toolPart.ToolCallId);
        Assert.Equal("read", toolPart.ToolName);
        Assert.Equal("completed", toolPart.Status);
        Assert.Equal("Read README", toolPart.Title);

        // The public view hides tool input payloads; the raw view is explicit.
        var publicJson = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");
        using (var publicDocument = JsonDocument.Parse(publicJson))
        {
            var publicTurn = publicDocument.RootElement.GetProperty("data").GetProperty("turns")[0];
            Assert.Equal("plan the refactor", publicTurn.GetProperty("user").GetProperty("text").GetString());
            Assert.False(publicTurn.GetProperty("assistant")[3].GetProperty("tool").TryGetProperty("rawInput", out _));
        }

        var rawJson = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript?view=raw");
        using var rawDocument = JsonDocument.Parse(rawJson);
        var rawTurn = rawDocument.RootElement.GetProperty("data").GetProperty("turns")[0];
        Assert.Contains("mohist-workspace-anchor", rawTurn.GetProperty("user").GetProperty("text").GetString(), StringComparison.Ordinal);
        // The raw view is diagnostic: the model fact appears as an unknown
        // entry before the projected parts, so the tool part sits at index 4.
        Assert.Equal("unknown", rawTurn.GetProperty("assistant")[0].GetProperty("type").GetString());
        Assert.Contains("README.md", rawTurn.GetProperty("assistant")[4].GetProperty("tool").GetProperty("rawInput").GetString(), StringComparison.Ordinal);

        // The metadata endpoint surfaces the sequence-resolved event summary.
        var metadata = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var metadataDocument = JsonDocument.Parse(metadata);
        var eventSummary = metadataDocument.RootElement.GetProperty("data").GetProperty("eventSummary");
        Assert.Equal("gpt-5.6-sol", eventSummary.GetProperty("resolvedModel").GetString());
    }
}
