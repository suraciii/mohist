using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Collection("AgentSessionGrainComponent")]
[Trait("level", "L0")]
public sealed class AgentSessionRuntimeEventStateSpecs
{
    private readonly AgentSessionGrainFixture _fixture;
    private readonly string _runnerId = $"session-spec-runner-{Guid.NewGuid():N}";

    public AgentSessionRuntimeEventStateSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task RunnerAppendsUsageUpdate_AccumulatesTokenAndCostCounters()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "usage-accumulate");

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
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

        var grainSession = await session.Grain.GetAsync();
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
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "usage-partial");

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("usage.updated", new
            {
                inputTokens = 10,
                outputTokens = 5,
                costAmount = 0.001,
                costCurrency = "USD",
                contextWindowUsed = 100
            }),
            ("usage.updated", new { inputTokens = 20 }));

        var grainSession = await session.Grain.GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(30, grainSession.InputTokens);
        Assert.Equal(5, grainSession.OutputTokens);
        Assert.Equal(0.001, grainSession.CostAmount);
        Assert.Equal("USD", grainSession.CostCurrency);
        Assert.Equal(100, grainSession.ContextWindowUsed);
    }

    [Fact]
    public async Task RunnerAppendsResolvedModelEvent_WithoutResolvedModelField_DoesNotSetModel()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "resolved-model-divergent");
        var persistence = session.Grain.PersistenceCheckpoint(_fixture.Persistence);

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("model.resolved", new
            {
                model = "anthropic/claude-sonnet-4-20250514",
                source = "newSession"
            }));

        await persistence.WaitAsync();

        var grainSession = await session.Grain.GetAsync();
        Assert.NotNull(grainSession);
        Assert.Null(grainSession.ResolvedModel);
    }

}

internal static class AgentSessionRuntimeEventTestSupport
{
    public static async Task<RuntimeEventTestSession> CreateStartedSessionAsync(
        IGrainFactory grains,
        string runnerId,
        string name)
    {
        var projectId = $"runtime-event-project-{Guid.NewGuid():N}";
        var sessionId = $"runtime-event-session-{Guid.NewGuid():N}";
        var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                projectId,
                "agent-1",
                "Agent One",
                1,
                Title: $"Session grain {name}"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            sessionId,
            WorkDir: $"/workspaces/{projectId}",
            ProcessPid: 1234,
            Runtime: "opencode",
            ExpectedRuntime: "opencode"));
        var receipt = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "record runtime events",
            "test",
            $"runtime-events-{name}"));
        return new RuntimeEventTestSession(grain, sessionId, receipt.TurnId);
    }

    public static Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendAsync(
        RuntimeEventTestSession session,
        params (string Type, object Payload)[] runtimeEvents)
    {
        var inputs = runtimeEvents.Select(runtimeEvent =>
        {
            var properties = JsonSerializer.SerializeToElement(runtimeEvent.Payload)
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            properties["turnId"] = JsonSerializer.SerializeToElement(session.TurnId);
            return new AgentSessionRuntimeEventInput(
                runtimeEvent.Type,
                JsonSerializer.SerializeToElement(properties).GetRawText());
        }).ToArray();

        return session.Grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            inputs,
            session.Id,
            SessionTurnId: session.TurnId));
    }
}

internal sealed record RuntimeEventTestSession(
    IAgentSessionGrain Grain,
    string Id,
    string TurnId);
