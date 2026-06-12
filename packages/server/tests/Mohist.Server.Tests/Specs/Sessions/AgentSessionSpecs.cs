using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Sessions;
using Xunit;

namespace Mohist.Server.Tests.Specs.Sessions;

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task LoadLatestEventsActivity_DoesNotSuppressTerminalOrLivenessEventTypes()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("activity-no-filter", title: "Activity no filter");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeEvents = new object[]
            {
                new
                {
                    type = "session.closed",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.NotNull(card.LastActivity);
        Assert.Equal("session_closed", card.LastActivity!.Text);
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
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
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

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");

        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        Assert.Equal("plan", root.GetProperty("sessionName").GetString());
        Assert.Equal(currentSession.Id, root.GetProperty("acpSessionId").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("status").GetString()));
        Assert.Equal(work.Stage, root.GetProperty("stage").GetString());
        Assert.Equal("Plan session", root.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("createdAt").GetString()));

        var metadata = root.GetProperty("metadata");
        Assert.Equal(6, metadata.GetProperty("partCount").GetInt32());
        Assert.Equal(1, metadata.GetProperty("toolCount").GetInt32());
        Assert.Equal("anthropic/claude-sonnet-4", root.GetProperty("resolvedModel").GetString());
        Assert.Equal(100, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(50, root.GetProperty("outputTokens").GetInt64());
        Assert.Equal(150, root.GetProperty("totalTokens").GetInt64());
        Assert.Equal(10, root.GetProperty("cachedReadTokens").GetInt64());
        Assert.Equal(5, root.GetProperty("thoughtTokens").GetInt64());
        Assert.Equal(0.01, root.GetProperty("costAmount").GetDouble());
        Assert.Equal("USD", root.GetProperty("costCurrency").GetString());
        Assert.Equal(150, root.GetProperty("contextWindowUsed").GetInt64());
        Assert.Equal(200000, root.GetProperty("contextWindowSize").GetInt64());
        Assert.Equal("probe_timeout", root.GetProperty("failureCategory").GetString());
        Assert.Equal(1, root.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, root.GetProperty("toolErrorCount").GetInt32());

        Assert.False(root.TryGetProperty("events", out _));
        Assert.False(root.TryGetProperty("turns", out _));
        Assert.False(root.TryGetProperty("assistant", out _));
        Assert.False(root.TryGetProperty("workflowLogs", out _));
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

        await Task.Delay(20);
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "still working" } }
            }
        });

        var summaries = await _client.GetDataAsync<AgentSessionSummaryDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        var summary = Assert.Single(summaries);

        Assert.Equal("active", summary.Status);
        Assert.NotNull(summary.LastDataAt);
        Assert.True(DateTime.Parse(summary.LastDataAt!) > beforeLastDataAt);
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
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "do the thing", kind = "task" } },
                new { type = "message.delta", payload = new { text = "first message" } },
                new { type = "message.delta", payload = new { text = "second message" } }
            }
        });

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
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
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
    public async Task RunnerAttach_DifferentPhysicalSession_ReturnsConflict()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("attach-conflict", start: false);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { agentSessionId = "acp-1", workDir = "/work", processPid = 1234 });

        using var response = await _client.PostAsJsonAsync(RunnerAgentSessionAttachPath(session), new { agentSessionId = "acp-2", workDir = "/work", processPid = 1234 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("already attached", body);
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
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal([1L, 2L], parts.Select(e => e.Sequence).ToArray());
        Assert.Equal("text", parts[0].Type);
        Assert.Equal("session_closed", parts[1].Type);
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
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        var runtimeEvents = Enumerable.Range(0, 96)
            .Select(i => new { type = "reasoning.delta", payload = new { text = i.ToString("D2"), messageId = "reasoning-1" } })
            .Cast<object>()
            .ToArray();

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new { runtimeEvents });

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
    [Fact(Skip = "AgentSessionEvent persistence is a no-op stub; event read-back not yet available.")]
    public async Task RunnerAppendsSessionEvents_StoresAggregateDomainEvents()
    {
        await Task.CompletedTask;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RunnerReportsTerminalSession_TerminalStatusExists_IgnoresLaterStatusChanges()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("terminal-lock");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeEvents = new[]
            {
                new { type = "session.liveness", payload = new { status = "probing", failureReason = "late" } }
            }
        });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "late-failure", exitCode = 1 } }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("active", grainSession.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AgentSessionOpen_ClosedRuntimeObservation_DoesNotRebindSession()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-reuse");

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
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
        Assert.Equal("active", reopened.Status);
        Assert.Equal(_runnerId, reopened.RunnerId);

        var nextRunnerId = $"{_runnerId}-next";
        var repeated = await grain.OpenAsync(new OpenAgentSessionCommand(
            nextRunnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, session.IssueNumber, session.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, repeated.Id);
        Assert.Equal("active", repeated.Status);
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
        Assert.Equal("active", opened.Status);
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
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
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

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("active", grainSession.Status);
        Assert.Equal(10, grainSession.InputTokens);
        Assert.Equal(0.001, grainSession.CostAmount);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var runtimeEvents = (await LoadTranscriptPartsAsync(db, session.Id)).ToList();
        Assert.Equal(2, runtimeEvents.Count);
        Assert.Equal("session_closed", runtimeEvents[0].Type);
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
            runtimeEvents = new[]
            {
                new
                {
                    type = "model.resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4-20250514", source = "newSession" }
                }
            }
        });

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
            runtimeEvents = new[]
            {
                new
                {
                    type = "session.closed",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("active", grainSession.Status);
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

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.Equal("anthropic/claude-sonnet-4", card.ResolvedModel);
        Assert.Equal(100, card.InputTokens);
        Assert.Equal(50, card.OutputTokens);
        Assert.Equal(150, card.TotalTokens);
        Assert.Equal(10, card.CachedReadTokens);
        Assert.Equal(5, card.ThoughtTokens);
        Assert.Equal(0.01, card.CostAmount);
        Assert.Equal("USD", card.CostCurrency);
        Assert.Equal(150, card.ContextWindowUsed);
        Assert.Equal(200000, card.ContextWindowSize);
        Assert.Equal("probe_timeout", card.FailureCategory);
        Assert.Equal(1, card.ToolCallCount);
        Assert.Equal(1, card.ToolErrorCount);
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
            Metadata = new { CreatedAt = DateTimeOffset.UtcNow, Name = "test" },
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
            var label = await db.AgentSessionLabels.FirstAsync(s =>
                s.SessionId == session.Id && s.Key == AgentSessionQueryMetadataKeys.Stage);
            label.Value = "Plan";
            await db.SaveChangesAsync();
        }

        var runState = JsonSerializer.Serialize(new
        {
            Id = work.WorkflowRunId,
            Metadata = new { CreatedAt = DateTimeOffset.UtcNow, Name = "test" },
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact(Skip = "Requires design decision: report-failed should close session, but current RunnerGrain.ReportAsync does not propagate to session")]
    public async Task RunnerReport_WhenAgentWorkFailsBeforeTelemetry_ClosesCreatedSession()
    {
        var projectName = $"session-report-failure-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Report closes failed session", body = "track report failure", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", new { });
        var work = await PollUntilAgentWorkAsync(issue.Number);

        var sessionName = work.WorkId;
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{work.WorkflowRunId}/{sessionName}/open", new
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

        var sessionId = await ResolveSessionIdAsync(work.WorkflowRunId, sessionName);
        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Equal("prompt_missing", grainSession.FailureCategory);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
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

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"asg-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issueTitle = title ?? $"Session grain {name}";
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = issueTitle, body = "track sessions", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });

        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        var info = await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, issue.Number, work.WorkflowRunId, sessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));
        var session = new CreatedSession(project.Id, issue.Number, work.WorkflowRunId, sessionName, info);
        if (start)
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
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
        return await db.AgentSessionLabels
            .Where(label => label.Key == AgentSessionQueryMetadataKeys.WorkflowRunId && label.Value == workflowRunId)
            .Join(db.AgentSessionLabels.Where(label => label.Key == AgentSessionQueryMetadataKeys.SessionName && label.Value == sessionName),
                left => left.SessionId,
                right => right.SessionId,
                (left, right) => left.SessionId)
            .SingleAsync();
    }

    private Task PostEventEntriesAsync(CreatedSession session, string text) => _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
    {
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

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
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
        string? ResolvedModel,
        long? InputTokens,
        long? OutputTokens,
        long? TotalTokens,
        long? CachedReadTokens,
        long? ThoughtTokens,
        double? CostAmount,
        string? CostCurrency,
        long? ContextWindowUsed,
        long? ContextWindowSize,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount,
        ActivityTaskProgressDto? TaskProgress);
    private sealed record ActivityTaskProgressDto(int Completed, int Total);
    private sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
    private sealed record ActivityWaitingDto(int IssueNumber, string IssueTitle, string Label);
    private sealed record AgentSessionTranscriptTestResponse(AgentSessionTranscriptTurnTestDto[] Turns, int PartCount, string? LastActivityAt);
    private sealed record AgentSessionTranscriptTurnTestDto(string Id, AgentSessionTranscriptUserTestDto User, AgentSessionTranscriptPartTestDto[] Assistant, string StartedAt, string? CompletedAt, bool Incomplete);
    private sealed record AgentSessionTranscriptUserTestDto(string Role, string Text, string Kind, string SentAt);
    private sealed record AgentSessionTranscriptPartTestDto(string Id, string Type, string? Text, string? Message, string? Kind);
}
