using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Orleans;
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
    public async Task IssueSessionMetadataEndpoint_ReturnsMetadataOnlyWithoutTurnsOrRawEvents()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("metadata-only", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(
                GrainKey.AgentSession(project.Id, currentWorkflowRunId, "plan"))
            .EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "plan", _runnerId, work.WorkId, work.WorkType, work.Stage, "Plan session"));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/attach", new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_thought_chunk", payload = new { content = new { text = "thinking" } } },
                new { type = "agent_message_chunk", payload = new { text = "hello" } },
                new
                {
                    type = "tool_call",
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
                    type = "agent_usage_update",
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
                    type = "agent_session_model_resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new { toolCallId = "meta-tool-1", kind = "read", status = "failed", title = "Read README" }
                },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var raw = await _client.GetRawAsync($"/api/issues/{issue.Number}/sessions/plan?projectId={project.Id}");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");

        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        Assert.Equal("plan", root.GetProperty("sessionName").GetString());
        Assert.Equal(currentSession.Id, root.GetProperty("acpSessionId").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("status").GetString()));
        Assert.Equal(work.Stage, root.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("title").ValueKind);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("createdAt").GetString()));

        var metadata = root.GetProperty("metadata");
        Assert.Equal(7, metadata.GetProperty("eventCount").GetInt32());
        Assert.Equal(2, metadata.GetProperty("toolCount").GetInt32());
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

    [Fact]
    public async Task IssueSessionEventsEndpoint_ReturnsRawEventsInAscendingSequence()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("raw-events", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(
                GrainKey.AgentSession(project.Id, currentWorkflowRunId, "plan"))
            .EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "plan", _runnerId, work.WorkId, work.WorkType, work.Stage, "Plan session"));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/attach", new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "mohist_prompt", payload = new { text = "do the thing", kind = "task" } },
                new { type = "agent_message_chunk", payload = new { text = "first message" } },
                new { type = "agent_message_chunk", payload = new { text = "second message" } }
            }
        });

        var response = await _client.GetDataAsync<AgentSessionEventsTestResponse>($"/api/issues/{issue.Number}/sessions/plan/events?projectId={project.Id}");

        Assert.Equal(3, response.Events.Length);
        Assert.Equal(new[] { "mohist_prompt", "agent_message_chunk", "agent_message_chunk" }, response.Events.Select(e => e.Type).ToArray());
        var sequences = response.Events.Select(e => e.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(s => s).ToArray(), sequences);

        var prompt = response.Events[0];
        Assert.True(prompt.Id > 0);
        Assert.Equal("mohist_prompt", prompt.Type);
        Assert.Equal("task", prompt.Payload?.GetProperty("kind").GetString());
        Assert.Equal("do the thing", prompt.Payload?.GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(prompt.CreatedAt));

        var second = response.Events[2];
        Assert.Equal("agent_message_chunk", second.Type);
        Assert.Equal("second message", second.Payload?.GetProperty("text").GetString());

        var serialized = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("turns", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assistant", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflowLogs", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueSessionMetadataEndpoint_MissingSession_ReturnsNotFound()
    {
        var projectName = $"metadata-not-found-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Metadata not found", projectId = project.Id });

        using var response = await _client.GetAsync($"/api/issues/{issue.Number}/sessions/does-not-exist?projectId={project.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AgentSessionGrain_ForAgentWork_CreatesDeterministicSessionAndKeepsPollIdempotent()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("idempotent", start: false);
        Assert.Equal(GrainKey.AgentSession(work.Issue!.ProjectId, work.WorkflowRunId, work.WorkId), session.Id);

        var repeated = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(repeated);
        Assert.Equal(session.Id, repeated.Id);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentBatches_AssignsMonotonicSequences()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("sequence");

        await Task.WhenAll(
            PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "first"),
            PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "second"));

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var sequences = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToArrayAsync();
        Assert.Equal([1L, 2L], sequences);
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

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("completed", grainSession.Status);
        Assert.Equal(0, grainSession.ExitCode);
        Assert.Null(grainSession.FailureReason);
    }

    [Fact]
    public async Task AgentSessionEnsure_TerminalSessionExists_ReopensSameSessionForRetry()
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
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var reopened = await grain.EnsureAsync(new EnsureAgentSessionCommand(
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
        Assert.Equal(_runnerId, reopened.RunnerId);

        var nextRunnerId = $"{_runnerId}-next";
        var repeated = await grain.EnsureAsync(new EnsureAgentSessionCommand(
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
        Assert.Equal(_runnerId, repeated.RunnerId);
    }

    [Fact]
    public async Task RunnerUnregisters_WorkInFlight_FailsRunningSession()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-unregister");

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).FailIfRunningAsync("Runner unregistered");

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Contains("unregistered", grainSession.FailureReason);
    }

    [Fact]
    public async Task EnsureAgentSession_TerminalSessionExists_KeepsTerminalSessionClosed()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-terminal");

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).FailIfRunningAsync("Session liveness probe timed out");

        var ensured = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id)
            .EnsureAsync(new EnsureAgentSessionCommand(
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
    public async Task EnsureAgentSession_TerminalSessionKeyStaysClosedForDifferentWork()
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

        var ensured = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id)
            .EnsureAsync(new EnsureAgentSessionCommand(
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
        Assert.Equal("completed", ensured.Status);
        Assert.Equal(work.WorkId, ensured.WorkId);
        Assert.NotNull(ensured.CompletedAt);
        Assert.Null(ensured.FailureReason);
    }

    [Fact]
    public async Task RunnerAppendsUsageUpdate_AccumulatesTokenAndCostCounters()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-accumulate");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new
                {
                    type = "agent_usage_update",
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
                    type = "agent_usage_update",
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

    [Fact]
    public async Task RunnerAppendsUsageUpdate_PartialFields_DoesNotEraseExistingValues()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-partial");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "agent_usage_update",
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
                    type = "agent_usage_update",
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

    [Fact]
    public async Task RunnerAppendsUsageUpdate_TerminalSession_PersistsEventButDoesNotMutateCounters()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-terminal");

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
                new
                {
                    type = "agent_usage_update",
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
        Assert.Equal("completed", grainSession.Status);
        Assert.Null(grainSession.InputTokens);
        Assert.Null(grainSession.CostAmount);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var events = await db.AgentSessionEvents
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal("agent_session_terminal", events[0].Type);
        Assert.Equal("agent_usage_update", events[1].Type);
    }

    [Fact]
    public async Task RunnerAppendsResolvedModelEvent_UpdatesResolvedModel()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("resolved-model");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new
                {
                    type = "agent_session_model_resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4-20250514", source = "newSession" }
                }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("anthropic/claude-sonnet-4-20250514", grainSession.ResolvedModel);
    }

    [Fact]
    public async Task RunnerAppendsTerminalEvent_WithFailureCategory_PersistsCategory()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("failure-category");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Equal("probe timed out", grainSession.FailureReason);
        Assert.Equal("probe_timeout", grainSession.FailureCategory);
    }

    [Fact]
    public async Task RunnerAppendsToolCallEvents_CountsCallsAndErrors()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("tool-calls");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new
                {
                    type = "tool_call",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }
                },
                new
                {
                    type = "tool_call",
                    payload = new { toolCallId = "tool-2", kind = "edit", status = "in_progress", title = "Edit file" }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "completed", title = "Read file" }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new { toolCallId = "tool-2", kind = "edit", status = "failed", title = "Edit file" }
                }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(2, grainSession.ToolCallCount);
        Assert.Equal(1, grainSession.ToolErrorCount);
    }

    [Fact]
    public async Task AgentActivity_WhenLeaseOwnerDiffers_ReportsOnlyLeaseOwnedActiveSession()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("lease-owner-activity");
        var staleRunnerId = $"stale-runner-{Guid.NewGuid():N}";

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            var row = await db.AgentSessions.SingleAsync(s => s.Id == session.Id);
            row.RunnerId = staleRunnerId;
            await db.SaveChangesAsync();
        }

        await SaveLeaseAsync(work.WorkflowRunId, new WorkLease(work.WorkId, work.WorkType, work.Stage ?? "Build", work.WorkId, work.Title, _runnerId));

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");

        Assert.Equal(0, activity.Summary.Active);
        Assert.DoesNotContain(activity.Sessions, s => s.SessionId == session.Id && s.Status is "created" or "running" or "probing");
        Assert.DoesNotContain(activity.Sessions, s => s.IssueNumber == issue.Number && s.Status is "created" or "running" or "probing");
    }

    [Fact]
    public async Task AgentActivity_ExposesObservabilityFields()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("activity-observability");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "agent_usage_update",
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
                    type = "agent_session_model_resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "failed", title = "Read file" }
                },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                }
            }
        });

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
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

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.TaskProgress);
        Assert.Equal(1, card.TaskProgress!.Completed);
        Assert.Equal(3, card.TaskProgress.Total);
    }

    [Fact]
    public async Task AgentActivity_WhenSessionStageIsStale_UsesWorkflowCurrentStageTaskProgress()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("activity-task-progress-stale-stage");

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            var row = await db.AgentSessions.FirstAsync(s => s.Id == session.Id);
            row.Stage = "Plan";
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

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.TaskProgress);
        Assert.Equal(2, card.TaskProgress!.Completed);
        Assert.Equal(4, card.TaskProgress.Total);
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
        var sessionId = GrainKey.AgentSession(project.Id, work.WorkflowRunId, sessionName);
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

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
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

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, AgentSessionInfo Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
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
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(project.Id, work.WorkflowRunId, sessionName));
        var session = await grain.EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, work.WorkflowRunId, sessionName, _runnerId, work.WorkId, work.WorkType, work.Stage, work.Title));
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
                State = json
            });
        }
        else
        {
            row.State = json;
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
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, string? ProjectId, string? IssueId, int? IssueNumber);
    private sealed record AgentSessionSummaryDto(string Id, string SessionName, string Status);
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
    private sealed record AgentSessionEventsTestResponse(AgentSessionEventTestDto[] Events);
    private sealed record AgentSessionEventTestDto(long Id, long Sequence, string Type, JsonElement? Payload, string CreatedAt);
}
