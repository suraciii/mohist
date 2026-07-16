using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("MohistIntegration2")]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task LoadLatestEventsActivity_DoesNotSuppressTerminalOrLivenessEventTypes()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("activity-no-filter", title: "Activity no filter");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "session.closed",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, _fixture.Grains);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.NotNull(card.LastActivity);
        Assert.Equal("session.closed", card.LastActivity!.Text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueSessionMetadataEndpoint_ReturnsMetadataOnlyWithoutTurnsOrRawEvents()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("metadata-only", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeSessionId = currentSession.Id,
            runtimeEvents = new object[]
            {
                new { type = "reasoning.delta", payload = new { content = new { text = "thinking" } } },
                new { type = "message.delta", payload = new { text = "hello" } },
                new
                {
                    type = "tool_call.started",
                    payload = new
                    {
                        toolCallId = "meta-tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 100,
                        outputTokens = 50,
                        totalTokens = 150,
                        cachedReadTokens = 10,
                        thoughtTokens = 5,
                        costAmount = 0.01,
                        costCurrency = "USD",
                        contextWindowSize = 200000,
                        contextWindowUsed = 150
                    }
                },
                new
                {
                    type = "model.resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call.updated",
                    payload = new { toolCallId = "meta-tool-1", kind = "read", status = "failed", title = "Read README" }
                },
                new
                {
                    type = "session.closed",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(currentSession.Id, 6, _fixture.Grains);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");

        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        Assert.Equal("plan", root.GetProperty("sessionName").GetString());
        Assert.Equal(currentSession.Id, root.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", root.GetProperty("runtime").GetString());
        Assert.False(root.TryGetProperty("acpSessionId", out _));
        Assert.False(root.TryGetProperty("coderSessionId", out _));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("status").GetString()));
        Assert.Equal(work.Stage, root.GetProperty("stage").GetString());
        Assert.Equal("Plan session", root.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("createdAt").GetString()));

        var metadata = root.GetProperty("metadata");
        // 7 parts: reasoning+message flush as one text, tool_call.started,
        // usage.updated, model.resolved, tool_call.updated, session.closed,
        // and the context_health_update snapshot emitted by the grain
        // when usage.first crosses 0% → 0.075% (initial green seed).
        Assert.Equal(7, metadata.GetProperty("partCount").GetInt32());
        Assert.Equal(1, metadata.GetProperty("toolCount").GetInt32());
        var eventSummary = root.GetProperty("eventSummary");
        Assert.Equal("anthropic/claude-sonnet-4", eventSummary.GetProperty("resolvedModel").GetString());
        Assert.Equal("probe_timeout", eventSummary.GetProperty("failureCategory").GetString());
        Assert.Equal(1, eventSummary.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, eventSummary.GetProperty("toolErrorCount").GetInt32());
        var usage = root.GetProperty("usage");
        Assert.Equal(100, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(50, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(150, usage.GetProperty("totalTokens").GetInt64());
        Assert.Equal(10, usage.GetProperty("cachedReadTokens").GetInt64());
        Assert.Equal(5, usage.GetProperty("thoughtTokens").GetInt64());
        Assert.Equal(0.01, usage.GetProperty("costAmount").GetDouble());
        Assert.Equal("USD", usage.GetProperty("costCurrency").GetString());
        Assert.Equal(150, usage.GetProperty("contextWindowUsed").GetInt64());
        Assert.Equal(200000, usage.GetProperty("contextWindowSize").GetInt64());

        Assert.False(root.TryGetProperty("events", out _));
        Assert.False(root.TryGetProperty("turns", out _));
        Assert.False(root.TryGetProperty("assistant", out _));
        Assert.False(root.TryGetProperty("workflowLogs", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueSessionMetadataEndpoint_ProjectsTranscriptEventsInSequenceOrder_WhenRowsWereInsertedOutOfOrder()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("metadata-order", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await SeedOutOfOrderTranscriptPartsAsync(dbFactory, currentSession.Id);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var eventSummary = doc.RootElement.GetProperty("data").GetProperty("eventSummary");

        Assert.Equal("sequence-last-model", eventSummary.GetProperty("resolvedModel").GetString());
        Assert.Equal("sequence-last-failure", eventSummary.GetProperty("failureCategory").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RuntimeEvents_RefreshSessionSummaryActivityWithoutDomainEvents()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("summary-activity", sessionName: "check");
        var beforeSummaries = await _client.GetDataAsync<AgentSessionSummaryDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        var beforeSummary = Assert.Single(beforeSummaries);
        Assert.NotNull(beforeSummary.LastDataAt);
        var beforeLastDataAt = DateTime.Parse(beforeSummary.LastDataAt!);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "still working" } }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, _fixture.Grains);

        var summaries = await _client.GetDataAsync<AgentSessionSummaryDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        var summary = Assert.Single(summaries);

        Assert.Equal("active", summary.Status);
        Assert.NotNull(summary.LastDataAt);
        Assert.True(DateTime.Parse(summary.LastDataAt!) > beforeLastDataAt);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        using var document = JsonDocument.Parse(raw);
        var wireSummary = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(session.Id, wireSummary.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", wireSummary.GetProperty("runtime").GetString());
        Assert.False(wireSummary.TryGetProperty("acpSessionId", out _));
        Assert.False(wireSummary.TryGetProperty("coderSessionId", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CoderSessionSummary_UnboundSessionLeavesRuntimeSessionIdNull()
    {
        var (project, issue, _, _) = await CreateStartedAgentSessionAsync("unbound-summary", start: false, sessionName: "plan");

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");

        using var document = JsonDocument.Parse(raw);
        var summary = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.False(summary.TryGetProperty("runtimeSessionId", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueSessionEventsEndpoint_ReturnsTranscriptSegmentsInAscendingSequence()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("raw-events", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeSessionId = currentSession.Id,
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "do the thing", kind = "task" } },
                new { type = "message.delta", payload = new { text = "first message" } },
                new { type = "message.delta", payload = new { text = "second message" } }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(currentSession.Id, 1, _fixture.Grains);

        var response = await _client.GetDataAsync<AgentSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");

        Assert.Equal(1, response.PartCount);
        var turn = Assert.Single(response.Turns);
        Assert.Equal("mohist", turn.User.Role);
        Assert.Equal("task", turn.User.Kind);
        Assert.Equal("do the thing", turn.User.Text);
        var part = Assert.Single(turn.Assistant);
        Assert.Equal("text", part.Type);
        Assert.Equal("first messagesecond message", part.Text);

        var serialized = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("workflowLogs", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueSessionMetadataEndpoint_MissingSession_ReturnsNotFound()
    {
        var projectName = $"metadata-not-found-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Metadata not found", projectId = project.Id });

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentSessionGrain_ForAgentWork_CreatesGuidSessionAndKeepsPollIdempotent()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("idempotent", start: false);
        Assert.True(Guid.TryParseExact(session.Id, "N", out _));

        var repeated = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(repeated);
        Assert.Equal(session.Id, repeated.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAttach_DifferentPhysicalSession_ReturnsConflictAndPreservesBinding()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("attach-conflict", start: false);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = "acp-1", workDir = "/work", processPid = 1234 });

        using var response = await _client.PostAsJsonAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = "acp-2", workDir = "/work", processPid = 1234 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("agent_session_attach_conflict", body.RootElement.GetProperty("code").GetString());
        var sessionAfterConflict = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(sessionAfterConflict);
        Assert.Equal("acp-1", sessionAfterConflict.AgentSessionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentChunks_BuffersUntilFlush()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("sequence");

        await Task.WhenAll(
            PostEventEntriesAsync(session, "first"),
            PostEventEntriesAsync(session, "second"));

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, _fixture.Grains);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal([1L, 2L], parts.Select(e => e.Sequence).ToArray());
        Assert.Equal("text", parts[0].Type);
        Assert.Equal("session.closed", parts[1].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsManyChunks_PersistsAggregatedTranscriptSegmentsOnly()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("chunk-aggregation", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var session = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        var runtimeEvents = Enumerable.Range(0, 96)
            .Select(i => new { type = "reasoning.delta", payload = new { text = i.ToString("D2"), messageId = "reasoning-1" } })
            .Cast<object>()
            .ToArray();

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new { runtimeSessionId = session.Id, runtimeEvents });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, _fixture.Grains);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await LoadTranscriptPartsAsync(db, session.Id);

        var part = Assert.Single(parts);
        Assert.Equal("reasoning", part.Type);
        Assert.Equal(96, part.RawEventCount);
        Assert.Equal(string.Concat(Enumerable.Range(0, 96).Select(i => i.ToString("D2"))), part.Text);

        var response = await _client.GetDataAsync<AgentSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{session.IssueNumber}/sessions/{session.SessionName}/transcript");
        var turn = Assert.Single(response.Turns);
        var transcriptPart = Assert.Single(turn.Assistant);
        Assert.Equal("reasoning", transcriptPart.Type);
        Assert.Equal(string.Concat(Enumerable.Range(0, 96).Select(i => i.ToString("D2"))), transcriptPart.Text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task DeferredPersistence_SessionDetailTranscriptContainsAllTextAndToolParts()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("deferred-transcript", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var session = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "plan the refactor", kind = "task" } },
                new { type = "message.delta", payload = new { text = "first", messageId = "msg-1" } },
                new { type = "message.delta", payload = new { text = " second", messageId = "msg-1" } },
                new { type = "reasoning.delta", payload = new { text = "thinking", messageId = "reason-1" } },
                new { type = "reasoning.delta", payload = new { text = "deeper", messageId = "reason-2" } }
            }
        });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "tool_call.started",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "tool_call.completed",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        title = "Read README",
                        rawOutput = new { content = "# Project" }
                    }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 4, _fixture.Grains);

        await using var db = await dbFactory.CreateDbContextAsync();
        var dbParts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal(4, dbParts.Length);
        Assert.Equal(["text", "reasoning", "reasoning", "tool"], dbParts.Select(p => p.Type).ToArray());

        var response = await _client.GetDataAsync<AgentSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");

        Assert.Equal(4, response.PartCount);
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
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsSessionEvents_StoresAggregateDomainEvents()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-events-store");
        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var before = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var lastExistingId = before.Count == 0 ? 0 : before.Max(e => e.Id);

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "usage.updated", payload = new { contextWindowUsed = 500, contextWindowSize = 1000 } }
            }
        });

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).FlushForTestAsync();

        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var appended = stored.Where(e => e.Id > lastExistingId).ToArray();

        Assert.Contains(appended, e => e.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded);
        Assert.All(appended, e => Assert.Equal(session.Id, e.Envelope.Subject));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerReportsTerminalSession_TerminalStatusExists_IgnoresLaterStatusChanges()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("terminal-lock");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.liveness", payload = new { status = "probing", failureReason = "late" } }
            }
        });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "late-failure", exitCode = 1 } }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("inactive", grainSession.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentSessionOpen_ClosedRuntimeObservation_DoesNotRebindSession()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-reuse");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "first attempt", exitCode = 1 } }
            }
        });

        var retryRunnerId = $"{_runnerId}-retry";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var reopened = await grain.OpenAsync(new OpenAgentSessionCommand(
            retryRunnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, session.IssueNumber, session.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, reopened.Id);
        Assert.Equal("inactive", reopened.Status);
        Assert.Equal(_runnerId, reopened.RunnerId);

        var nextRunnerId = $"{_runnerId}-next";
        var repeated = await grain.OpenAsync(new OpenAgentSessionCommand(
            nextRunnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, session.IssueNumber, session.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, repeated.Id);
        Assert.Equal("inactive", repeated.Status);
        Assert.Equal(_runnerId, repeated.RunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RuntimeEvents_AfterFailedClosedObservation_KeepSessionActive()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-unregister");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "Runner unregistered", exitCode = 1 } },
                new { type = "message.delta", payload = new { text = "new data" } },
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("active", grainSession.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task OpenAgentSession_ExistingBoundSessionKeepsRuntimeBinding()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-terminal");

        var opened = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id)
            .OpenAsync(new OpenAgentSessionCommand(
                _runnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(project.Id, work.Issue!.IssueNumber, work.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, opened.Id);
        Assert.Equal("active", opened.Status);
        Assert.NotNull(opened.AgentSessionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task OpenAgentSession_ClosedObservationKeepsRuntimeBinding()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("named-reuse", sessionName: "check");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var opened = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id)
            .OpenAsync(new OpenAgentSessionCommand(
                _runnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(project.Id, issue.Number, work.WorkflowRunId, session.SessionName, "fix-review-findings:1.1", "task", "check", "Fix review findings")));

        Assert.Equal(session.Id, opened.Id);
        Assert.Equal("inactive", opened.Status);
        Assert.NotNull(opened.AgentSessionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsUsageUpdate_AccumulatesTokenAndCostCounters()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-accumulate");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 10,
                        outputTokens = 5,
                        totalTokens = 15,
                        cachedReadTokens = 2,
                        thoughtTokens = 1,
                        costAmount = 0.001,
                        costCurrency = "USD",
                        contextWindowSize = 200,
                        contextWindowUsed = 100
                    }
                },
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 20,
                        outputTokens = 10,
                        totalTokens = 30,
                        cachedReadTokens = 3,
                        thoughtTokens = 2,
                        costAmount = 0.002,
                        costCurrency = "EUR",
                        contextWindowSize = 250,
                        contextWindowUsed = 150
                    }
                }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(30, grainSession.InputTokens);
        Assert.Equal(15, grainSession.OutputTokens);
        Assert.Equal(45, grainSession.TotalTokens);
        Assert.Equal(5, grainSession.CachedReadTokens);
        Assert.Equal(3, grainSession.ThoughtTokens);
        Assert.Equal(0.003, grainSession.CostAmount);
        Assert.Equal("EUR", grainSession.CostCurrency);
        Assert.Equal(150, grainSession.ContextWindowUsed);
        Assert.Equal(250, grainSession.ContextWindowSize);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsUsageUpdate_PartialFields_DoesNotEraseExistingValues()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-partial");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 10,
                        outputTokens = 5,
                        costAmount = 0.001,
                        costCurrency = "USD",
                        contextWindowUsed = 100
                    }
                },
                new
                {
                    type = "usage.updated",
                    payload = new { inputTokens = 20 }
                }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(30, grainSession.InputTokens);
        Assert.Equal(5, grainSession.OutputTokens);
        Assert.Equal(0.001, grainSession.CostAmount);
        Assert.Equal("USD", grainSession.CostCurrency);
        Assert.Equal(100, grainSession.ContextWindowUsed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsUsageUpdate_TerminalSession_PersistsEventButDoesNotMutateCounters()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-terminal");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 10,
                        outputTokens = 5,
                        costAmount = 0.001,
                        costCurrency = "USD"
                    }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, _fixture.Grains);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("inactive", grainSession.Status);
        Assert.Equal(10, grainSession.InputTokens);
        Assert.Equal(0.001, grainSession.CostAmount);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var runtimeEvents = (await LoadTranscriptPartsAsync(db, session.Id)).ToList();
        Assert.Equal(2, runtimeEvents.Count);
        Assert.Equal("session.closed", runtimeEvents[0].Type);
        Assert.Equal("usage", runtimeEvents[1].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsResolvedModelEvent_UpdatesResolvedModel()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("resolved-model");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new
                {
                    type = "model.resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4-20250514", source = "newSession" }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, _fixture.Grains);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("anthropic/claude-sonnet-4-20250514", grainSession.ResolvedModel);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsTerminalEvent_WithFailureCategory_PersistsCategory()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("failure-category");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new
                {
                    type = "session.closed",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, _fixture.Grains);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("inactive", grainSession.Status);
        Assert.Equal("probe_timeout", grainSession.FailureCategory);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerAppendsToolCallEvents_CountsCallsAndErrors()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("tool-calls");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new
                {
                    type = "tool_call.started",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }
                },
                new
                {
                    type = "tool_call.started",
                    payload = new { toolCallId = "tool-2", kind = "edit", status = "in_progress", title = "Edit file" }
                },
                new
                {
                    type = "tool_call.updated",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "completed", title = "Read file" }
                },
                new
                {
                    type = "tool_call.updated",
                    payload = new { toolCallId = "tool-2", kind = "edit", status = "failed", title = "Edit file" }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, _fixture.Grains);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(2, grainSession.ToolCallCount);
        Assert.Equal(1, grainSession.ToolErrorCount);
    }


    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentActivity_ExposesObservabilityFields()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("activity-observability");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 100,
                        outputTokens = 50,
                        totalTokens = 150,
                        cachedReadTokens = 10,
                        thoughtTokens = 5,
                        costAmount = 0.01,
                        costCurrency = "USD",
                        contextWindowSize = 200000,
                        contextWindowUsed = 150
                    }
                },
                new
                {
                    type = "model.resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call.started",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }
                },
                new
                {
                    type = "tool_call.updated",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "failed", title = "Read file" }
                },
                new
                {
                    type = "session.closed",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 4, _fixture.Grains);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.EventSummary);
        Assert.NotNull(card.Usage);
        Assert.Equal("anthropic/claude-sonnet-4", card.EventSummary!.ResolvedModel);
        Assert.Equal(100, card.Usage!.InputTokens);
        Assert.Equal(50, card.Usage.OutputTokens);
        Assert.Equal(150, card.Usage.TotalTokens);
        Assert.Equal(10, card.Usage.CachedReadTokens);
        Assert.Equal(5, card.Usage.ThoughtTokens);
        Assert.Equal(0.01, card.Usage.CostAmount);
        Assert.Equal("USD", card.Usage.CostCurrency);
        Assert.Equal(150, card.Usage.ContextWindowUsed);
        Assert.Equal(200000, card.Usage.ContextWindowSize);
        Assert.Equal("probe_timeout", card.EventSummary.FailureCategory);
        Assert.Equal(1, card.EventSummary.ToolCallCount);
        Assert.Equal(1, card.EventSummary.ToolErrorCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentActivity_WhenRunnerActiveWorksExceedVisibleSessions_SlotsReflectRunner()
    {
        // Divergence proof for issue-300/T-002: the runner grain carries more
        // active workflow works than there are visible AgentSessions, so
        // /agent/activity.summary.slots.active must follow the runner active-works
        // count rather than be clamped to the visible AgentSession count.
        var projectName = $"activity-divergence-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        var runnerId = $"activity-divergence-{Guid.NewGuid():N}";
        try
        {
            await _client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
            await _client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 4 });

            var workflowA = $"wf-activity-div-a-{Guid.NewGuid():N}";
            var workflowB = $"wf-activity-div-b-{Guid.NewGuid():N}";
            var workflowProjectId = $"wf-activity-div-project-{Guid.NewGuid():N}";
            await SeedActivityDivergenceTemplateAsync(workflowProjectId);

            var workflowAGrain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowA);
            var workflowBGrain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowB);
            var startInput = new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = workflowProjectId,
                }));
            await workflowAGrain.StartAsync(startInput);
            await workflowBGrain.StartAsync(startInput);
            await workflowAGrain.AssignWorkerAsync(runnerId);
            await workflowBGrain.AssignWorkerAsync(runnerId);

            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var first = await runner.PollAsync(_fixture.Services);
            Assert.NotNull(first);
            var second = await runner.PollAsync(_fixture.Services);
            Assert.NotNull(second);

            var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");

            // summary.slots.active reflects the runner active-works count (2
            // distinct workflow owners), NOT the visible AgentSession count (no
            // AgentSessions were persisted in this scenario, so 0). max reflects
            // the persisted runner slots (4).
            Assert.Equal(2, activity.Summary.Slots.Active);
            Assert.Equal(4, activity.Summary.Slots.Max);
            // summary.active continues to reflect the visible AgentSession count;
            // it does NOT participate in capacity derivation.
            Assert.Equal(0, activity.Summary.Active);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task SeedActivityDivergenceTemplateAsync(string projectId)
    {
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var templateId = "spec/workflow";
        var templateJson = JsonSerializer.Serialize(
            new WorkflowDefinition(templateId,
            [
                new StageDefinition("build",
                    [new TaskDefinition("task-1", "Task 1", "spec/task")],
                    [])
            ]),
            WorkflowYamlSerializer.JsonOptions);

        var existing = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existing is null)
        {
            db.ProjectWorkflowTemplates.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = templateJson,
            });
        }
        else
        {
            existing.Template = templateJson;
            existing.UpdatedAt = TestTime.UtcNow;
        }
        if (await db.ProjectWorkflowProfiles.FindAsync(projectId) is null)
        {
            db.ProjectWorkflowProfiles.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
            });
        }
        await db.SaveChangesAsync();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentActivity_WithResolvableWorkflowStage_ReturnsTaskProgress()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("activity-task-progress");

        var runState = JsonSerializer.Serialize(new
        {
            Id = work.WorkflowRunId,
            Metadata = new { CreatedAt = _fixture.TimeProvider.GetUtcNow(), Name = "test" },
            Status = "Running",
            CurrentStageId = work.Stage ?? "Build",
            Stages = new[]
            {
                new
                {
                    Id = work.Stage ?? "Build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Running",
                    Tasks = new[]
                    {
                        new { Id = "task-1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = "Completed", Uses = "mohist/acp-agent" },
                        new { Id = "task-2", DefinitionId = "task-2", Attempt = 1, Title = "Task 2", Status = "Running", Uses = "mohist/acp-agent" },
                        new { Id = "task-3", DefinitionId = "task-3", Attempt = 1, Title = "Task 3", Status = "Pending", Uses = "mohist/acp-agent" }
                    },
                    Checks = Array.Empty<object>()
                }
            }
        });
        var issueState = JsonSerializer.Serialize(new
        {
            Id = issue.Id,
            ProjectId = project.Id,
            Number = issue.Number,
            WorkflowRunId = work.WorkflowRunId,
            Title = issue.Title
        });

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
                work.WorkflowRunId, runState);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO Issues (IssueId, State) VALUES ({0}, {1})",
                issue.Id, issueState);
        }

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.TaskProgress);
        Assert.Equal(1, card.TaskProgress!.Completed);
        Assert.Equal(3, card.TaskProgress.Total);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentActivity_WhenSessionStageIsStale_UsesWorkflowCurrentStageTaskProgress()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("activity-task-progress-stale-stage");

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE AgentSessions SET State = json_set(State, '$.metadata.labels."mohist.io/stage"', {0}) WHERE Id = {1}""",
                "Plan", session.Id);
        }

        var runState = JsonSerializer.Serialize(new
        {
            Id = work.WorkflowRunId,
            Metadata = new { CreatedAt = _fixture.TimeProvider.GetUtcNow(), Name = "test" },
            Status = "Running",
            CurrentStageId = "Build",
            Stages = new[]
            {
                new
                {
                    Id = "Plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "plan-1", DefinitionId = "plan-1", Attempt = 1, Title = "Plan 1", Status = "Completed", Uses = "mohist/acp-agent" }
                    },
                    Checks = Array.Empty<object>()
                },
                new
                {
                    Id = "Build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Running",
                    Tasks = new[]
                    {
                        new { Id = "task-1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = "Completed", Uses = "mohist/acp-agent" },
                        new { Id = "task-2", DefinitionId = "task-2", Attempt = 1, Title = "Task 2", Status = "Completed", Uses = "mohist/acp-agent" },
                        new { Id = "task-3", DefinitionId = "task-3", Attempt = 1, Title = "Task 3", Status = "Running", Uses = "mohist/acp-agent" },
                        new { Id = "task-4", DefinitionId = "task-4", Attempt = 1, Title = "Task 4", Status = "Pending", Uses = "mohist/acp-agent" }
                    },
                    Checks = Array.Empty<object>()
                }
            }
        });
        var issueState = JsonSerializer.Serialize(new
        {
            Id = issue.Id,
            ProjectId = project.Id,
            Number = issue.Number,
            WorkflowRunId = work.WorkflowRunId,
            Title = issue.Title
        });

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
                work.WorkflowRunId, runState);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO Issues (IssueId, State) VALUES ({0}, {1})",
                issue.Id, issueState);
        }

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.TaskProgress);
        Assert.Equal(2, card.TaskProgress!.Completed);
        Assert.Equal(4, card.TaskProgress.Total);
    }

    private async Task<WorkDispatchDto> PollUntilAgentWorkAsync(int? expectedIssueNumber = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            var work = await response.ReadFirstDispatchAsync<WorkDispatchDto>();
            if (work is null)
                continue;

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

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"asg-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issueTitle = title ?? $"Session grain {name}";
var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = issueTitle, body = "track sessions", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });

        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        var info = await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, issue.Number, work.WorkflowRunId, sessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));
        var session = new CreatedSession(project.Id, issue.Number, work.WorkflowRunId, sessionName, info);
        if (start)
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        return (project, issue, work, session);
    }

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    private string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    private string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

    private async Task<CreatedSession> OpenRunnerSessionAsync(string projectId, int issueNumber, string workflowRunId, string sessionName, WorkDispatch work, string title)
    {
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title,
            issueNumber
        });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);
        var session = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        return new CreatedSession(projectId, issueNumber, workflowRunId, sessionName, session ?? throw new InvalidOperationException($"Session {workflowRunId}/{sessionName} was not created."));
    }

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private Task PostEventEntriesAsync(CreatedSession session, string text) => _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
    {
        runtimeSessionId = session.Id,
        runtimeEvents = new[]
        {
            new { type = "message.delta", payload = new { text } }
        }
    });

    private static async Task<AgentSessionTranscriptPartRow[]> LoadTranscriptPartsAsync(MohistDbContext db, string sessionId)
    {
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Select(e => e.Id)
            .ToArrayAsync();

        return await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToArrayAsync();
    }

    private static async Task SeedOutOfOrderTranscriptPartsAsync(IDbContextFactory<MohistDbContext> dbFactory, string sessionId)
    {
        var baseTime = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        await using var db = await dbFactory.CreateDbContextAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = baseTime,
            UpdatedAt = baseTime.AddMinutes(5),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.AddRange(
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 20,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "metadata-model-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "sequence-last-model" }),
                LastSeenAt = baseTime.AddMinutes(20),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 10,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "metadata-model-inserted-last",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "inserted-last-model" }),
                LastSeenAt = baseTime.AddMinutes(10),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 30,
                Type = TranscriptPartTypes.SessionClosed,
                CorrelationKey = "metadata-closed-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "sequence-last-failure" }),
                LastSeenAt = baseTime.AddMinutes(30),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 15,
                Type = TranscriptPartTypes.SessionClosed,
                CorrelationKey = "metadata-closed-inserted-last",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "inserted-last-failure" }),
                LastSeenAt = baseTime.AddMinutes(15),
            });
        await db.SaveChangesAsync();
    }

    private static AgentSessionMetadata WorkflowSessionMetadata(
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string? workId,
        string? workType,
        string? stage,
        string? title) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
            .WithLabel(AgentSessionQueryMetadataKeys.SessionName, sessionName)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkId, workId)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkType, workType)
            .WithLabel(AgentSessionQueryMetadataKeys.Stage, stage)
            .WithAnnotation(AgentSessionQueryMetadataKeys.Title, title);

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }

    private sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, string? ProjectId, string? IssueId, int? IssueNumber);
    private sealed record AgentSessionSummaryDto(string Id, string SessionName, string Status, string? LastDataAt);
    private sealed record ActivityDto(ActivitySummaryDto Summary, ActivityCardDto[] Sessions, ActivityWaitingDto[] Waiting);
    private sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, ActivitySlotUsageDto Slots);
    private sealed record ActivitySlotUsageDto(int Active, int Max);
    private sealed record ActivityCardDto(
        int IssueNumber,
        string IssueTitle,
        string SessionId,
        string Status,
        ActivityPreviewDto? LastActivity,
        AgentEventSummaryDto? EventSummary,
        AgentUsageDto? Usage,
        ActivityTaskProgressDto? TaskProgress);
    private sealed record AgentEventSummaryDto(
        string? ResolvedModel,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount);
    private sealed record AgentUsageDto(
        long? InputTokens,
        long? OutputTokens,
        long? TotalTokens,
        long? CachedReadTokens,
        long? ThoughtTokens,
        double? CostAmount,
        string? CostCurrency,
        long? ContextWindowUsed,
        long? ContextWindowSize);
    private sealed record ActivityTaskProgressDto(int Completed, int Total);
    private sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
    private sealed record ActivityWaitingDto(int IssueNumber, string IssueTitle, string Label);
    private sealed record AgentSessionTranscriptTestResponse(AgentSessionTranscriptTurnTestDto[] Turns, int PartCount, string? LastActivityAt);
    private sealed record AgentSessionTranscriptTurnTestDto(string Id, AgentSessionTranscriptUserTestDto User, AgentSessionTranscriptPartTestDto[] Assistant, string StartedAt, string? CompletedAt, bool Incomplete);
    private sealed record AgentSessionTranscriptUserTestDto(string Role, string Text, string Kind, string SentAt);
    private sealed record AgentSessionTranscriptPartTestDto(string Id, string Type, string? Text, string? Message, string? Kind, AgentSessionTranscriptToolTestDto? Tool);
    private sealed record AgentSessionTranscriptToolTestDto(string ToolCallId, string ToolName, string Status, string? Title);
}
