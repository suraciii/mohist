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
public class AgentSessionRuntimeEventSpecs : AgentSessionTestSupport
{
    public AgentSessionRuntimeEventSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

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


}
