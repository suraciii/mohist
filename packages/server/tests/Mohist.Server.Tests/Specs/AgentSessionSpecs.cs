using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class AgentSessionSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"session-spec-runner-{Guid.NewGuid():N}";

    public AgentSessionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_SessionApisExposeTranscript()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("transcript", title: "Build session management");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello from agent\n" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var sessions = await _client.GetDataAsync<WorkflowAgentSessionSummaryDto[]>($"/api/issues/{issue.Number}/coder-sessions?projectId={project.Id}");
        Assert.Contains(sessions, s => s.Id == session.Id && s.SessionName == session.SessionName && s.Status == "completed");

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        Assert.Equal(session.Id, detail.Id);
        Assert.Contains("hello from agent", JsonSerializer.Serialize(detail.Turns));

        var current = await _client.GetDataAsync<WorkflowAgentSessionInfoDto[]>($"/api/agent/sessions?projectId={project.Id}");
        Assert.Contains(current, s => s.SessionId == session.Id && s.IssueTitle == issue.Title);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.Equal(issue.Number, card.IssueNumber);
        Assert.Equal(issue.Title, card.IssueTitle);
        Assert.Equal("completed", card.Status);
        Assert.Equal("agent_session_terminal", card.LastActivity?.Text);
        Assert.Equal("text", card.LastActivity?.Kind);
        Assert.Equal(1, activity.Summary.Completed);
        Assert.Equal(0, activity.Summary.Active);
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_ContentTextPayload_AppearsInTranscriptAndActivityPreview()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("content-text", title: "Content text payload");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_message_chunk", payload = new { content = new { type = "text", text = "nested content message\n" }, messageId = "msg-1" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        Assert.Contains("nested content message", JsonSerializer.Serialize(detail.Turns));

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.Equal("agent_session_terminal", card.LastActivity?.Text);
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_ToolEvents_AppearAsTranscriptToolParts()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("tool-transcript", title: "Tool transcript");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_thought_chunk", payload = new { content = new { text = "I should inspect the file." } } },
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README.md",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "hello" }
                    }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();

        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "reasoning" && p.GetProperty("text").GetString() == "I should inspect the file.");
        var toolPart = Assert.Single(assistant, p => p.GetProperty("type").GetString() == "tool");
        var tool = toolPart.GetProperty("tool");
        Assert.Equal("tool-1", tool.GetProperty("toolCallId").GetString());
        Assert.Equal("read", tool.GetProperty("toolName").GetString());
        Assert.Equal("completed", tool.GetProperty("status").GetString());
        Assert.Contains("README.md", tool.GetProperty("input").GetString());
        Assert.Contains("hello", tool.GetProperty("output").GetString());
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_ToolCallUpdate_PreservesFirstObservedIndexAndMergesRawPayload()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("tool-merge-position", title: "Tool merge position");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README.md",
                        rawInput = new { filePath = "README.md" },
                        metadata = new { source = "open" }
                    }
                },
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-2",
                        kind = "bash",
                        status = "in_progress",
                        title = "Run tests",
                        rawInput = new { command = "npm test" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "hello" },
                        metadata = new { bytes = 5 },
                        details = new { format = "markdown" }
                    }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();

        var toolParts = assistant.Where(p => p.GetProperty("type").GetString() == "tool").ToArray();
        Assert.Equal(2, toolParts.Length);

        var firstToolPart = toolParts[0];
        var firstTool = firstToolPart.GetProperty("tool");
        Assert.Equal("tool-1", firstTool.GetProperty("toolCallId").GetString());
        Assert.Equal("completed", firstTool.GetProperty("status").GetString());
        Assert.Contains("README.md", firstTool.GetProperty("input").GetString());
        Assert.Contains("hello", firstTool.GetProperty("output").GetString());
        var firstMetadata = firstTool.GetProperty("metadata");
        Assert.Equal(JsonValueKind.Object, firstMetadata.ValueKind);
        Assert.Equal(5, firstMetadata.GetProperty("bytes").GetInt32());
        var firstDetails = firstTool.GetProperty("details");
        Assert.Equal(JsonValueKind.Object, firstDetails.ValueKind);
        Assert.Equal("markdown", firstDetails.GetProperty("format").GetString());

        var secondToolPart = toolParts[1];
        var secondTool = secondToolPart.GetProperty("tool");
        Assert.Equal("tool-2", secondTool.GetProperty("toolCallId").GetString());
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_PendingToolCallUpdate_DoesNotOverwriteTerminalStatus()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("tool-merge-pending", title: "Tool merge pending");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README.md",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        title = "Read README.md (final)",
                        rawOutput = new { text = "hello" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "pending"
                    }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var toolPart = turn.GetProperty("assistant").EnumerateArray()
            .Single(p => p.GetProperty("type").GetString() == "tool");
        var tool = toolPart.GetProperty("tool");
        Assert.Equal("completed", tool.GetProperty("status").GetString());
        Assert.Equal("Read README.md (final)", tool.GetProperty("title").GetString());
        Assert.Contains("hello", tool.GetProperty("output").GetString());
        Assert.False(string.IsNullOrEmpty(tool.GetProperty("completedAt").GetString()));
    }

    [Fact]
    public async Task MohistPrompt_RecordsFullPayload_UserTextEqualsEventText()
    {
        const string promptBody =
            "Write a detailed implementation plan for the issue.\n\n" +
            "Include:\n" +
            "- The architecture\n" +
            "- The implementation steps\n" +
            "- Test plan\n\n" +
            "Make sure the plan respects the existing constraints and avoid touching unrelated files.";
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("prompt-full-text", title: "Full text payload");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new
                    {
                        text = promptBody,
                        kind = "task",
                        title = "Write implementation plan",
                        outputPath = "/tmp/plan.md",
                        contextFiles = new[] { "src/Issue/Models.cs", "src/Issue/Controllers.cs" }
                    }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var user = turn.GetProperty("user");

        Assert.Equal("mohist", user.GetProperty("role").GetString());
        Assert.Equal(promptBody, user.GetProperty("text").GetString());
        Assert.Equal("task", user.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(user.GetProperty("sentAt").GetString()));
        Assert.Equal(turn.GetProperty("startedAt").GetString(), user.GetProperty("sentAt").GetString());

        var summary = user.GetProperty("summary");
        Assert.Equal("task", summary.GetProperty("kind").GetString());
        Assert.Equal("Write implementation plan", summary.GetProperty("title").GetString());
        Assert.Equal("/tmp/plan.md", summary.GetProperty("outputPath").GetString());
        var contextFiles = summary.GetProperty("contextFiles").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "src/Issue/Models.cs", "src/Issue/Controllers.cs" }, contextFiles);

        Assert.Equal(1, detail.Metadata.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public async Task MohistPrompt_ShortSessionTitle_NotSubstitutedForRealPromptText()
    {
        const string shortSessionTitle = "Cover backend projection and progress behavior";
        const string longPromptBody =
            "Long-form real prompt that the coder agent actually saw. " +
            "It contains many paragraphs of detail describing the task, the constraints, the deliverables, " +
            "the test plan, the definition of done, and the conversation contract. " +
            "This is several hundred characters long and obviously larger than a short task title.";
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("prompt-short-title", title: shortSessionTitle);
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new
                    {
                        text = longPromptBody,
                        kind = "task",
                        title = shortSessionTitle
                    }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var user = turn.GetProperty("user");
        var userText = user.GetProperty("text").GetString();

        Assert.Equal(longPromptBody, userText);
        Assert.NotEqual(shortSessionTitle, userText);
        Assert.NotEqual(session.SessionName, userText);
        Assert.NotEqual(session.Id, userText);

        var summary = user.GetProperty("summary");
        Assert.Equal("task", summary.GetProperty("kind").GetString());
        Assert.Equal(shortSessionTitle, summary.GetProperty("title").GetString());
    }

    [Fact]
    public async Task MohistPrompt_TwoEventsInOneSession_ProduceTwoTurnsInEventOrder()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("prompt-two-turns", title: "Two prompt turns");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new { text = "first prompt body", kind = "task" }
                },
                new { type = "agent_thought_chunk", payload = new { content = new { text = "thinking about first prompt" } } },
                new { type = "agent_message_chunk", payload = new { text = "first prompt response" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new { text = "second prompt body that follows up on the first", kind = "followup" }
                },
                new { type = "agent_message_chunk", payload = new { text = "second prompt response" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turns = detail.Turns.EnumerateArray().ToArray();
        Assert.Equal(2, turns.Length);

        var firstTurnUser = turns[0].GetProperty("user");
        Assert.Equal("first prompt body", firstTurnUser.GetProperty("text").GetString());
        Assert.Equal("task", firstTurnUser.GetProperty("kind").GetString());

        var firstAssistant = turns[0].GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(firstAssistant, p => p.GetProperty("type").GetString() == "reasoning" && p.GetProperty("text").GetString() == "thinking about first prompt");
        Assert.Contains(firstAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("first prompt response"));
        Assert.DoesNotContain(firstAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("second prompt response"));

        var secondTurnUser = turns[1].GetProperty("user");
        Assert.Equal("second prompt body that follows up on the first", secondTurnUser.GetProperty("text").GetString());
        Assert.Equal("followup", secondTurnUser.GetProperty("kind").GetString());

        var secondAssistant = turns[1].GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(secondAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("second prompt response"));
        Assert.DoesNotContain(secondAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("first prompt response"));
        Assert.Contains(secondAssistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "completed");

        Assert.True(string.Compare(turns[0].GetProperty("startedAt").GetString(), turns[1].GetProperty("startedAt").GetString(), StringComparison.Ordinal) < 0);
        Assert.Equal(turns[1].GetProperty("startedAt").GetString(), turns[0].GetProperty("completedAt").GetString());
        Assert.Equal(2, detail.Metadata.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public async Task MohistPrompt_ThoughtToolThoughtTextSequence_AssistantPartsPreserveEmittedOrder()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("prompt-interleave", title: "Thought tool thought text order");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new { text = "investigate the order of parts", kind = "task" }
                },
                new { type = "agent_thought_chunk", payload = new { content = new { text = "first thought before tool" } } },
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "interleave-tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README.md",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "interleave-tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "hello" }
                    }
                },
                new { type = "agent_thought_chunk", payload = new { content = new { text = "thought after tool" } } },
                new { type = "agent_message_chunk", payload = new { text = "final text after second thought" } }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();

        var partTypes = assistant.Select(p => p.GetProperty("type").GetString()).ToArray();
        Assert.Equal(new[] { "reasoning", "tool", "reasoning", "text" }, partTypes);

        var reasoningTexts = assistant
            .Where(p => p.GetProperty("type").GetString() == "reasoning")
            .Select(p => p.GetProperty("text").GetString())
            .ToArray();
        Assert.Equal(new[] { "first thought before tool", "thought after tool" }, reasoningTexts);

        var toolIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "tool");
        var firstReasoningIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "reasoning" && p.GetProperty("text").GetString() == "first thought before tool");
        var secondReasoningIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "reasoning" && p.GetProperty("text").GetString() == "thought after tool");
        var textIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "text");
        Assert.True(firstReasoningIndex < toolIndex);
        Assert.True(toolIndex < secondReasoningIndex);
        Assert.True(secondReasoningIndex < textIndex);
    }

    [Fact]
    public async Task RunnerReportsTerminalFailure_TerminalEventProjectsAsClosingErrorPartWithFailureReason()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("terminal-failure", title: "Terminal failure");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "starting work\n" } },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "failed", failureReason = "model refused", exitCode = 1 }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();

        var errorPart = Assert.Single(assistant, p => p.GetProperty("type").GetString() == "error");
        Assert.Equal("failed", errorPart.GetProperty("kind").GetString());
        Assert.Equal("model refused", errorPart.GetProperty("message").GetString());
        Assert.False(string.IsNullOrEmpty(errorPart.GetProperty("at").GetString()));
        Assert.False(string.IsNullOrEmpty(turn.GetProperty("completedAt").GetString()));
    }

    [Fact]
    public async Task RunnerReportsLivenessTransitions_ProjectedAsRecoveryPartsInEventOrder()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("liveness-transitions", title: "Liveness transitions");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "agent_liveness_status",
                    payload = new { status = "probing", probeDeadlineAt = "2026-06-03T12:00:00Z", lastActivityType = "session" }
                },
                new { type = "agent_message_chunk", payload = new { text = "still working\n" } },
                new
                {
                    type = "agent_liveness_status",
                    payload = new { status = "running", lastActivityType = "message" }
                },
                new
                {
                    type = "agent_liveness_status",
                    payload = new { status = "failed", failureReason = "no progress", lastActivityType = "message" }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();

        var errorParts = assistant.Where(p => p.GetProperty("type").GetString() == "error").ToArray();
        Assert.Equal(3, errorParts.Length);
        Assert.All(errorParts, p => Assert.Equal("recovery", p.GetProperty("kind").GetString()));
        Assert.Contains(errorParts, p => p.GetProperty("message").GetString()!.Contains("Liveness probe sent"));
        Assert.Contains(errorParts, p => p.GetProperty("message").GetString()!.Contains("Liveness recovered"));
        Assert.Contains(errorParts, p => p.GetProperty("message").GetString()!.Contains("Liveness failed"));

        var probingIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("message").GetString()!.Contains("Liveness probe sent"));
        var textIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "text");
        var runningIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("message").GetString()!.Contains("Liveness recovered"));
        Assert.True(probingIndex < textIndex);
        Assert.True(textIndex < runningIndex);
    }

    [Fact]
    public async Task RunnerReportsCoderRecoveryTransitions_ProjectedAsRecoveryPartsWithLiveMapping()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("recovery-transitions", title: "Recovery transitions");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "coder_recovery_status",
                    payload = new { status = "detected" }
                },
                new { type = "agent_message_chunk", payload = new { text = "working\n" } },
                new
                {
                    type = "coder_recovery_status",
                    payload = new { status = "recovering" }
                },
                new
                {
                    type = "coder_recovery_status",
                    payload = new { status = "recovered" }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turn = Assert.Single(detail.Turns.EnumerateArray());
        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();

        var errorParts = assistant.Where(p => p.GetProperty("type").GetString() == "error").ToArray();
        Assert.Equal(3, errorParts.Length);
        Assert.All(errorParts, p => Assert.Equal("recovery", p.GetProperty("kind").GetString()));
        Assert.Contains(errorParts, p => p.GetProperty("message").GetString() == "Recovery detected");
        Assert.Contains(errorParts, p => p.GetProperty("message").GetString() == "Recovery in progress");
        Assert.Contains(errorParts, p => p.GetProperty("message").GetString() == "Recovery succeeded");

        var detectedIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("message").GetString() == "Recovery detected");
        var textIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "text");
        var recoveringIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("message").GetString() == "Recovery in progress");
        var recoveredIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("message").GetString() == "Recovery succeeded");
        Assert.True(detectedIndex < textIndex);
        Assert.True(textIndex < recoveringIndex);
        Assert.True(recoveringIndex < recoveredIndex);
    }

    [Fact]
    public async Task TerminalEvent_RefreshReplay_ProducesSameClosingPartWithoutSseStream()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("terminal-refresh", title: "Terminal refresh");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello\n" } },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "cancelled", failureReason = "user aborted", exitCode = 130 }
                }
            }
        });

        var detail1 = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var detail2 = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");

        var turn1 = Assert.Single(detail1.Turns.EnumerateArray());
        var turn2 = Assert.Single(detail2.Turns.EnumerateArray());
        var closing1 = turn1.GetProperty("assistant").EnumerateArray()
            .Single(p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "cancelled");
        var closing2 = turn2.GetProperty("assistant").EnumerateArray()
            .Single(p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "cancelled");
        Assert.Equal(closing1.GetProperty("message").GetString(), closing2.GetProperty("message").GetString());
        Assert.Equal("user aborted", closing1.GetProperty("message").GetString());
    }

    [Fact]
    public async Task LoadLatestEventsActivity_DoesNotSuppressTerminalOrLivenessEventTypes()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("activity-no-filter", title: "Activity no filter");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.NotNull(card.LastActivity);
        Assert.Equal("agent_session_terminal", card.LastActivity!.Text);
    }

    [Fact]
    public async Task HistoricalSessionWithoutMohistPrompt_ProjectsSingleLegacyMissingTurn()
    {
        const string sessionTitle = "Cover backend projection and progress behavior";
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("legacy-missing", title: sessionTitle);
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_thought_chunk", payload = new { content = new { text = "I should look at the file first." } } },
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "legacy-tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README.md",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "legacy-tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "hello" }
                    }
                },
                new { type = "agent_message_chunk", payload = new { text = "after tool" } },
                new
                {
                    type = "agent_liveness_status",
                    payload = new { status = "running", lastActivityType = "message" }
                },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        var turns = detail.Turns.EnumerateArray().ToArray();
        var turn = Assert.Single(turns);

        var user = turn.GetProperty("user");
        Assert.Equal("legacy-missing", user.GetProperty("kind").GetString());
        Assert.Equal("mohist", user.GetProperty("role").GetString());
        Assert.Equal("Prompt was not recorded for this historical session", user.GetProperty("text").GetString());
        Assert.NotEqual(sessionTitle, user.GetProperty("text").GetString());
        Assert.NotEqual(session.SessionName, user.GetProperty("text").GetString());
        Assert.NotEqual(session.Id, user.GetProperty("text").GetString());
        Assert.Equal("legacy-missing", user.GetProperty("summary").GetProperty("kind").GetString());

        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "reasoning" && p.GetProperty("text").GetString() == "I should look at the file first.");
        var toolPart = Assert.Single(assistant, p => p.GetProperty("type").GetString() == "tool");
        Assert.Equal("legacy-tool-1", toolPart.GetProperty("tool").GetProperty("toolCallId").GetString());
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("after tool"));
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "recovery");
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "completed");

        Assert.Equal(1, detail.Metadata.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public async Task IssueWorkflowSessionApi_UsesCurrentWorkflowRunAndSessionName()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("current-workflow", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>($"{project.Id}:{issue.Number}");
        await issueGrain.StartWorkAsync();
        await PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "old workflow transcript");

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSessionId = GrainKey.WorkflowAgentSession(project.Id, currentWorkflowRunId, "plan");
        var currentSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(currentSessionId)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "plan", _runnerId, work.WorkId, work.WorkType, work.Stage, "Current plan"));
        await PostEventEntriesAsync(project.Id, currentSession.WorkflowRunId, currentSession.SessionName, "current workflow transcript");

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/workflow/sessions/plan?projectId={project.Id}");

        Assert.Equal(currentSession.Id, detail.Id);
        Assert.Equal("plan", detail.SessionName);
        Assert.Contains("current workflow transcript", JsonSerializer.Serialize(detail.Turns));
        Assert.DoesNotContain("old workflow transcript", JsonSerializer.Serialize(detail.Turns));
    }

    [Fact]
    public async Task WorkflowAgentSessionGrain_ForAgentWork_CreatesDeterministicSessionAndKeepsPollIdempotent()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("idempotent", start: false);
        Assert.Equal(GrainKey.WorkflowAgentSession(work.Issue!.ProjectId, work.WorkflowRunId, work.WorkId), session.Id);

        var repeated = await _fixture.Grains
            .GetGrain<IWorkflowAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(repeated);
        Assert.Equal(session.Id, repeated.Id);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentBatches_AssignsMonotonicSequences()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("sequence");

        await Task.WhenAll(
            PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "first"),
            PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "second"));

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var sequences = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToArrayAsync();
        Assert.Equal([1L, 2L], sequences);
        Assert.Contains("first", JsonSerializer.Serialize(detail.Turns));
        Assert.Contains("second", JsonSerializer.Serialize(detail.Turns));
    }

    [Fact]
    public async Task RunnerReportsTerminalSession_TerminalStatusExists_IgnoresLaterStatusChanges()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("terminal-lock");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_liveness_status", payload = new { status = "probing", failureReason = "late" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "failed", failureReason = "late-failure", exitCode = 1 } }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("completed", grainSession.Status);
        Assert.Equal(0, grainSession.ExitCode);
        Assert.Null(grainSession.FailureReason);
    }

    [Fact]
    public async Task WorkflowAgentSessionEnsure_TerminalSessionExists_ReopensSameSessionForRetry()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-reuse");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "failed", failureReason = "first attempt", exitCode = 1 } }
            }
        });

        var retryRunnerId = $"{_runnerId}-retry";
        var grain = _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id);
        var reopened = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(
            project.Id,
            session.IssueNumber,
            session.WorkflowRunId,
            session.SessionName,
            retryRunnerId,
            work.WorkId,
            work.WorkType,
            work.Stage,
            work.Title));

        Assert.Equal(session.Id, reopened.Id);
        Assert.Equal("failed", reopened.Status);
        Assert.Equal(retryRunnerId, reopened.RunnerId);

        var nextRunnerId = $"{_runnerId}-next";
        var repeated = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(
            project.Id,
            session.IssueNumber,
            session.WorkflowRunId,
            session.SessionName,
            nextRunnerId,
            work.WorkId,
            work.WorkType,
            work.Stage,
            work.Title));

        Assert.Equal(session.Id, repeated.Id);
        Assert.Equal("failed", repeated.Status);
        Assert.Equal(nextRunnerId, repeated.RunnerId);
    }

    [Fact]
    public async Task RunnerUnregisters_WorkInFlight_FailsRunningSession()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-unregister");

        await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).FailIfRunningAsync("Runner unregistered");

        var grainSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Contains("unregistered", grainSession.FailureReason);
    }

    [Fact]
    public async Task EnsureWorkflowAgentSession_TerminalSessionExists_KeepsTerminalSessionClosed()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-terminal");

        await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).FailIfRunningAsync("Session liveness probe timed out");

        var ensured = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(
                project.Id,
                work.Issue!.IssueNumber,
                work.WorkflowRunId,
                session.SessionName,
                _runnerId,
                work.WorkId,
                work.WorkType,
                work.Stage,
                work.Title));

        Assert.Equal(session.Id, ensured.Id);
        Assert.Equal("failed", ensured.Status);
        Assert.Contains("liveness", ensured.FailureReason);
    }

    [Fact]
    public async Task EnsureWorkflowAgentSession_NamedTerminalSessionStartsNewWork()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("named-reuse", sessionName: "check");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var ensured = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(
                project.Id,
                issue.Number,
                work.WorkflowRunId,
                session.SessionName,
                _runnerId,
                "fix-review-findings:1.1",
                "task",
                "check",
                "Fix review findings"));

        Assert.Equal(session.Id, ensured.Id);
        Assert.Equal("created", ensured.Status);
        Assert.Equal("fix-review-findings:1.1", ensured.WorkId);
        Assert.Null(ensured.CompletedAt);
        Assert.Null(ensured.FailureReason);
    }

    [Fact]
    public async Task AgentActivity_WhenLeaseOwnerDiffers_ReportsOnlyLeaseOwnedActiveSession()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("lease-owner-activity");
        var staleRunnerId = $"stale-runner-{Guid.NewGuid():N}";

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            var row = await db.WorkflowAgentSessions.SingleAsync(s => s.Id == session.Id);
            row.RunnerId = staleRunnerId;
            await db.SaveChangesAsync();
        }

        await SaveLeaseAsync(work.WorkflowRunId, new WorkLease(work.WorkId, work.WorkType, work.Stage ?? "Build", work.WorkId, work.Title, _runnerId));

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");

        Assert.Equal(0, activity.Summary.Active);
        Assert.DoesNotContain(activity.Sessions, s => s.SessionId == session.Id && s.Status is "created" or "running" or "probing");
        Assert.DoesNotContain(activity.Sessions, s => s.IssueNumber == issue.Number && s.Status is "created" or "running" or "probing");
    }

    [Fact(Skip = "Requires design decision: report-failed should close session, but current RunnerGrain.ReportAsync does not propagate to session")]
    public async Task RunnerReport_WhenAgentWorkFailsBeforeTelemetry_ClosesCreatedSession()
    {
        var projectName = $"session-report-failure-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Report closes failed session", body = "track report failure", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), projectId = project.Id });
        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}", new { });
        var work = await PollUntilAgentWorkAsync(issue.Number);

        var sessionName = work.WorkId;
        var sessionId = GrainKey.WorkflowAgentSession(project.Id, work.WorkflowRunId, sessionName);
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{work.WorkflowRunId}/{sessionName}/ensure", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title = work.Title,
            issueNumber = issue.Number,
        });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
        {
            workId = work.WorkId,
            workflowRunId = work.WorkflowRunId,
            status = "failed",
            projectId = project.Id,
            message = "ACP agent requires 'prompt'",
            exitCode = 1
        });

        var grainSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(sessionId).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Equal("ACP agent requires 'prompt'", grainSession.FailureReason);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        Assert.Equal(0, activity.Summary.Active);
        Assert.Equal(1, activity.Summary.Failed);
        Assert.Contains(activity.Sessions, s => s.IssueNumber == issue.Number && s.Status == "failed");
    }

    private async Task<WorkDispatchDto> PollUntilAgentWorkAsync(int? expectedIssueNumber = null)
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

            if (work.WorkType == "task" && work.Uses == "mohist/openspec-tasks")
            {
                var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
                await workflow.AddTasksAsync(new AddTasksBatchRequest([
                    new AddTasksBatchItem("build-1", "Build task", "mohist/acp-agent")
                ]));
                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
                {
                    workId = work.WorkId,
                    workflowRunId = work.WorkflowRunId,
                    status = "completed",
                    projectId = work.ProjectId
                });
                continue;
            }

            if (work.Uses == "mohist/acp-agent")
            {
                if (expectedIssueNumber is null || work.IssueNumber == expectedIssueNumber)
                    return work;

                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, workflowRunId = work.WorkflowRunId, status = "completed", projectId = work.ProjectId });
                continue;
            }

            await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, workflowRunId = work.WorkflowRunId, status = "completed", projectId = work.ProjectId });
        }

        Assert.Fail("No agent work dispatched");
        return default!;
    }

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, WorkflowAgentSessionInfo Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"session-grain-{name}-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issueTitle = title ?? $"Session grain {name}";
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = issueTitle, body = "track sessions", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });

        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(project.Id, work.WorkflowRunId, sessionName));
        var session = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(project.Id, issue.Number, work.WorkflowRunId, sessionName, _runnerId, work.WorkId, work.WorkType, work.Stage, work.Title));
        if (start)
            await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        return (project, issue, work, session);
    }

    private async Task SaveLeaseAsync(string workflowRunId, WorkLease lease)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.WorkflowLeases.FindAsync(workflowRunId);
        var json = JsonSerializer.Serialize(lease, WorkflowStorageJson.Options);
        if (row is null)
        {
            db.WorkflowLeases.Add(new WorkflowLeaseRow
            {
                WorkflowRunId = workflowRunId,
                StateJson = json
            });
        }
        else
        {
            row.StateJson = json;
        }
        await db.SaveChangesAsync();
    }

    private Task PostEventEntriesAsync(string projectId, string workflowRunId, string sessionName, string text) => _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{projectId}/{workflowRunId}/{sessionName}/events", new
    {
        events = new[]
        {
            new { type = "agent_message_chunk", payload = new { text } }
        }
    });

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, string? ProjectId, string? IssueId, int? IssueNumber);
    private sealed record WorkflowAgentSessionSummaryDto(string Id, string SessionName, string Status);
    private sealed record WorkflowAgentSessionTranscript(string Id, string SessionName, JsonElement Turns, JsonElement Metadata);
    private sealed record WorkflowAgentSessionInfoDto(string SessionId, string IssueTitle, string Status, string? AgentSessionId, string? FailureReason);
    private sealed record ActivityDto(ActivitySummaryDto Summary, ActivityCardDto[] Sessions, ActivityWaitingDto[] Waiting);
    private sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, ActivitySlotUsageDto Slots);
    private sealed record ActivitySlotUsageDto(int Active, int Max);
    private sealed record ActivityCardDto(int IssueNumber, string IssueTitle, string SessionId, string Status, ActivityPreviewDto? LastActivity);
    private sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
    private sealed record ActivityWaitingDto(int IssueNumber, string IssueTitle, string Label);
}
