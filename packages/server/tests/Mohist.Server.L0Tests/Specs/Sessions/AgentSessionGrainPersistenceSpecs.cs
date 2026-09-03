using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

public abstract class AgentSessionGrainPersistenceSpecsBase
{
    protected readonly AgentSessionGrainFixture Fixture;

    protected AgentSessionGrainPersistenceSpecsBase(AgentSessionGrainFixture fixture)
    {
        Fixture = fixture;
        Fixture.Reset();
    }

    protected IAgentSessionGrain NewGrain() => Fixture.Grains.GetGrain<IAgentSessionGrain>($"agent-session-spec-{Guid.NewGuid():N}");

    protected async Task<IAgentSessionGrain> OpenBoundGrainAsync(string runtime = "test")
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open(runtime));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1"));
        return grain;
    }

    protected OpenAgentSessionCommand Open(string runtime = "test") => new(
        "runner-1",
        runtime,
        WorkDir: "/work",
        Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext("project-1", "agent-1", "Agent One")));

    protected async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }
}

[Collection("AgentSessionGrainL0")]
[Trait("level", "L0")]
public class AgentSessionGrainPersistSuccessSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistSuccessSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Persistence_SavesBoundRuntimeEventsAndTranscript()
    {
        var grain = await OpenBoundGrainAsync();
        var firstPersistence = grain.PersistenceCheckpoint(Fixture.Persistence);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }, "runtime-1"));

        await firstPersistence.WaitAsync();

        Assert.Equal(3, Fixture.StateStore.SaveCount);
        Assert.Single(Fixture.TranscriptStore.Flushes);
        Assert.Single(Fixture.TranscriptStore.Flushes[0].Parts);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);

        var secondPersistence = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\" again\"}")
            }, "runtime-1"));

        await secondPersistence.WaitAsync();

        Assert.Equal(4, Fixture.StateStore.SaveCount);
        Assert.Equal(2, Fixture.TranscriptStore.Flushes.Count);
        Assert.Single(Fixture.TranscriptStore.Flushes[1].Parts);
        Assert.Contains(Fixture.TranscriptStore.Flushes[1].Parts, p => p.TextDelta == " again");
    }

    [Fact]
    public async Task AppendTerminalCloseAsync_DoesNotReportDeferredPersistenceCycle()
    {
        var grain = await OpenBoundGrainAsync();
        var checkpoint = grain.PersistenceCheckpoint(Fixture.Persistence);

        await grain.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            grain.GetPrimaryKeyString(),
            "delivery-1",
            "completed",
            0,
            null,
            null,
            Fixture.TimeProvider.GetUtcNow(),
            "{}",
            "runtime-1"));

        var afterTerminalClose = grain.PersistenceCheckpoint(Fixture.Persistence);
        Assert.Equal(checkpoint.CycleId, afterTerminalClose.CycleId);
    }
}

[Collection("AgentSessionGrainL0")]
[Trait("level", "L0")]
public class AgentSessionGrainPersistSummarySpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistSummarySpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task OpenBoundGrainAsync_WithoutObservations_PersistsEmptySummary()
    {
        await OpenBoundGrainAsync();

        var saved = Fixture.StateStore.State;
        Assert.NotNull(saved);
        Assert.Equal(AgentSessionTranscriptSummary.Empty, saved!.ActivitySummary);
    }

    [Fact]
    public async Task Persistence_PersistsSummary()
    {
        var grain = await OpenBoundGrainAsync();
        var persistence = grain.PersistenceCheckpoint(Fixture.Persistence);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"run\"}"),
                new AgentSessionRuntimeEventInput("model.resolved", "{\"resolvedModel\":\"model-v1\"}"),
                new AgentSessionRuntimeEventInput("tool_call.updated", "{\"toolCallId\":\"tool-1\",\"status\":\"failed\"}"),
                new AgentSessionRuntimeEventInput("session.activity", "{\"failureCategory\":\"tool_failure\",\"failureReason\":\"failed\"}")
            },
            "runtime-1"));
        await persistence.WaitAsync();

        var saved = Fixture.StateStore.State;
        Assert.NotNull(saved);
        Assert.Equal("model-v1", saved!.ActivitySummary.ResolvedModel);
        Assert.Equal("tool_failure", saved.ActivitySummary.FailureCategory);
        Assert.Equal("failed", saved.ActivitySummary.FailureReason);
        Assert.Equal(1, saved.ActivitySummary.ToolCallCount);
        Assert.Equal(1, saved.ActivitySummary.ToolErrorCount);

        var reloaded = await Fixture.StateStore.LoadAsync(grain.GetPrimaryKeyString());
        Assert.NotNull(reloaded);
        Assert.Equal(saved.ActivitySummary.ResolvedModel, reloaded!.ActivitySummary.ResolvedModel);
        Assert.Equal(saved.ActivitySummary.FailureCategory, reloaded.ActivitySummary.FailureCategory);
        Assert.Equal(saved.ActivitySummary.ToolCallCount, reloaded.ActivitySummary.ToolCallCount);
        Assert.Equal(saved.ActivitySummary.ToolErrorCount, reloaded.ActivitySummary.ToolErrorCount);
    }

    [Fact]
    public async Task RecoverMissingRuntimeSession_DoesNotExposePreviousRuntimeSummary()
    {
        var grain = await OpenBoundGrainAsync();
        var persistence = grain.PersistenceCheckpoint(Fixture.Persistence);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("model.resolved", "{\"resolvedModel\":\"model-a\"}"),
                new AgentSessionRuntimeEventInput("tool_call.updated", "{\"toolCallId\":\"tool-a\",\"status\":\"failed\"}"),
                new AgentSessionRuntimeEventInput("session.activity", "{\"activity\":\"idle\",\"failureCategory\":\"failure-a\",\"failureReason\":\"reason-a\"}")
            },
            "runtime-1"));
        await persistence.WaitAsync();

        var now = Fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await using (var db = Fixture.CreateDbContext())
        {
            var turn = new AgentSessionTranscriptTurnRow
            {
                SessionId = grain.GetPrimaryKeyString(),
                RuntimeSessionId = "runtime-1",
                Sequence = 1,
                PromptText = string.Empty,
                PromptKind = "task",
                StartedAt = now,
                UpdatedAt = now,
            };
            db.AgentSessionTranscriptTurns.Add(turn);
            await db.SaveChangesAsync();

            db.AgentSessionTranscriptParts.AddRange(
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 1,
                    Type = "model",
                    CorrelationKey = "model",
                    PayloadJson = "{\"resolvedModel\":\"model-a\"}",
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    RawEventCount = 1,
                },
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 2,
                    Type = "tool",
                    CorrelationKey = "tool-a",
                    CorrelationId = "tool-a",
                    PayloadJson = "{\"toolCallId\":\"tool-a\",\"status\":\"failed\"}",
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    RawEventCount = 1,
                },
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 3,
                    Type = "session.activity",
                    CorrelationKey = "session.activity",
                    PayloadJson = "{\"activity\":\"idle\",\"failureCategory\":\"failure-a\",\"failureReason\":\"reason-a\"}",
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    RawEventCount = 1,
                });
            await db.SaveChangesAsync();
        }

        var beforeRebind = await grain.GetAsync();
        Assert.NotNull(beforeRebind);
        Assert.Equal("model-a", beforeRebind!.ResolvedModel);
        Assert.Equal(1, beforeRebind.ToolCallCount);
        Assert.Equal(1, beforeRebind.ToolErrorCount);
        Assert.Equal("failure-a", beforeRebind.FailureCategory);

        var afterRebind = await grain.RecoverMissingRuntimeSessionAsync(
            new RecoverMissingRuntimeSessionCommand(
                "runner-1",
                "test",
                "runtime-1",
                "runtime-2"));

        Assert.Equal("runtime-2", afterRebind.AgentSessionId);
        Assert.Null(afterRebind.ResolvedModel);
        Assert.Null(afterRebind.ToolCallCount);
        Assert.Null(afterRebind.ToolErrorCount);
        Assert.Null(afterRebind.FailureCategory);
        Assert.Equal("model-a", Fixture.StateStore.State!.ActivitySummary.ResolvedModel);
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_StaleBinding_DoesNotChangePersistedSummary()
    {
        var grain = await OpenBoundGrainAsync();
        var persistence = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("model.resolved", "{\"resolvedModel\":\"current\"}")
            },
            "runtime-1"));
        await persistence.WaitAsync();

        var before = Fixture.StateStore.State;
        Assert.NotNull(before);
        var saveCount = Fixture.StateStore.SaveCount;
        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("model.resolved", "{\"resolvedModel\":\"stale\"}"),
                new AgentSessionRuntimeEventInput("session.activity", "{\"failureCategory\":\"stale\"}")
            },
            "runtime-stale"));

        Assert.Empty(result);
        Assert.Equal(saveCount, Fixture.StateStore.SaveCount);
        Assert.Equal(before.ActivitySummary, Fixture.StateStore.State!.ActivitySummary);
    }
}
