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
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public class AgentSessionRuntimeEventSpecs : AgentSessionTestSupport
{
    public AgentSessionRuntimeEventSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task ManagerCredentialExpiryRoute_CreatesOneQueuedRecoveryTurn()
    {
        var sessionId = $"manager-route-expiry-{Guid.NewGuid():N}";
        var provenance = new AgentSessionInputProvenance(
            "slack",
            "workspace-route",
            "conversation-route",
            "thread-route",
            "member-route",
            "message-route",
            "connection-route",
            "thread-route");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = SlackDeliveryOwnerIds.ManagerProjectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "manager-agent",
            })));
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "manager-route-input",
            "manager-route-turn",
            "manager request",
            "agent-launch",
            "manager-route-job",
            Provenance: provenance));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-route"));
        await grain.MarkInitialTurnTerminalAsync("manager-route-job", AgentTurnStatus.Completed, null);
        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "continue",
            "agent-session-followup",
            "manager-route-followup",
            Provenance: provenance));

        await _client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{Uri.EscapeDataString(SlackDeliveryOwnerIds.ManagerProjectId)}/{sessionId}/runtime-events",
            new
            {
                runtimeSessionId = "runtime-route",
                agentSessionId = sessionId,
                agentTurnId = followup.TurnId,
                runtimeEvents = new[]
                {
                    new
                    {
                        type = "session.activity",
                        payload = new
                        {
                            activity = "unknown",
                            status = "unknown",
                            reason = "manager-credential-expired",
                            failureCategory = "unknown",
                            operationId = followup.OperationId,
                            turnId = followup.TurnId,
                        },
                    },
                },
            });

        var turns = await grain.ListTurnsAsync();
        var recoveryTurn = Assert.Single(
            turns,
            turn => turn.Id == $"manager-recovery-turn:{sessionId}");
        Assert.Equal(AgentTurnStatus.Queued, recoveryTurn.Status);
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(turns, turn => turn.Id == followup.TurnId).Status);
        Assert.Single(turns, turn => turn.Id == $"manager-recovery-turn:{sessionId}");

        // The recovery turn must enter the ordinary dispatch contract: the
        // dispatcher claims it and would hand the recovery agent a fresh
        // Manager grant instead of leaving the turn recorded but unexecuted.
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(dispatch);
        Assert.Equal($"manager-recovery-turn:{sessionId}", dispatch!.TurnId);
        Assert.Equal($"manager-recovery-input:{sessionId}", dispatch.InputId);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentChunks_BuffersUntilFlush()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("sequence");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await Task.WhenAll(
            PostEventEntriesAsync(session, turnId, "first"),
            PostEventEntriesAsync(session, turnId, "second"));

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("session.activity", new { activity = "idle", status = "completed", operationId = "op-flush" }));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, persistence);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal([1L, 2L], parts.Select(e => e.Sequence).ToArray());
        Assert.Equal("text", parts[0].Type);
        Assert.Equal("session.activity", parts[1].Type);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_StoresAggregateDomainEvents()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-events-store");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var before = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var lastExistingId = before.Count == 0 ? 0 : before.Max(e => e.Id);
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("usage.updated", new { contextWindowUsed = 500, contextWindowSize = 1000 }));

        await persistence.WaitAsync();

        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var appended = stored.Where(e => e.Id > lastExistingId).ToArray();

        Assert.Contains(appended, e => e.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded);
        Assert.All(appended, e => Assert.Equal(session.Id, e.Envelope.Subject));
    }

    [Fact]
    public async Task RunnerAppendsUsageUpdate_AccumulatesTokenAndCostCounters()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("usage-accumulate");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("usage.updated", new
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
            }),
            ("usage.updated", new
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
            }));

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
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("usage.updated", new
            {
                inputTokens = 10,
                outputTokens = 5,
                costAmount = 0.001,
                costCurrency = "USD",
                contextWindowUsed = 100
            }),
            ("usage.updated", new { inputTokens = 20 }));

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
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("session.activity", new { activity = "idle", status = "completed", operationId = "op-terminal" }));

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("usage.updated", new
            {
                inputTokens = 10,
                outputTokens = 5,
                costAmount = 0.001,
                costCurrency = "USD"
            }));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, persistence);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("idle", grainSession.Status);
        Assert.Equal(10, grainSession.InputTokens);
        Assert.Equal(0.001, grainSession.CostAmount);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var runtimeEvents = (await LoadTranscriptPartsAsync(db, session.Id)).ToList();
        Assert.Equal(2, runtimeEvents.Count);
        Assert.Equal("session.activity", runtimeEvents[0].Type);
        Assert.Equal("usage", runtimeEvents[1].Type);
    }

    [Fact]
    public async Task RunnerAppendsResolvedModelEvent_UpdatesResolvedModel()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("resolved-model");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("model.resolved", new
            {
                resolvedModel = "anthropic/claude-sonnet-4-20250514",
                source = "newSession"
            }));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, persistence);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("anthropic/claude-sonnet-4-20250514", grainSession.ResolvedModel);
    }

    [Fact]
    public async Task RunnerAppendsResolvedModelEvent_WithoutResolvedModelField_DoesNotSetModel()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("resolved-model-divergent");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("model.resolved", new
            {
                model = "anthropic/claude-sonnet-4-20250514",
                source = "newSession"
            }));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, persistence);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Null(grainSession.ResolvedModel);
    }

    [Fact]
    public async Task RunnerAppendsTerminalEvent_WithFailureCategory_PersistsCategory()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("failure-category");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("session.activity", new
            {
                activity = "idle",
                status = "failed",
                failureReason = "probe timed out",
                failureCategory = "probe_timeout",
                exitCode = 1,
                operationId = "op-terminal"
            }));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, persistence);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("idle", grainSession.Status);
        Assert.Equal("probe_timeout", grainSession.FailureCategory);
    }

    [Fact]
    public async Task RunnerAppendsToolCallEvents_CountsCallsAndErrors()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("tool-calls");
        var turnId = await AcceptSessionRuntimeEventTurnAsync(session);
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await PostSessionTurnRuntimeEventsAsync(
            session,
            turnId,
            ("tool_call.started", new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }),
            ("tool_call.started", new { toolCallId = "tool-2", kind = "edit", status = "in_progress", title = "Edit file" }),
            ("tool_call.updated", new { toolCallId = "tool-1", kind = "read", status = "completed", title = "Read file" }),
            ("tool_call.updated", new { toolCallId = "tool-2", kind = "edit", status = "failed", title = "Edit file" }));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, persistence);

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(2, grainSession.ToolCallCount);
        Assert.Equal(1, grainSession.ToolErrorCount);
    }


}
