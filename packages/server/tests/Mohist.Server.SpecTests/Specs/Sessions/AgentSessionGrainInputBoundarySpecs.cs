using Microsoft.Extensions.Logging;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class AgentSessionGrainInputBoundarySpecsBase : IClassFixture<AgentSessionGrainFixture>
{
    protected readonly AgentSessionGrainFixture Fixture;

    protected AgentSessionGrainInputBoundarySpecsBase(AgentSessionGrainFixture fixture)
    {
        Fixture = fixture;
        Fixture.Reset();
    }

    protected IAgentSessionGrain NewGrain() => Fixture.Grains.GetGrain<IAgentSessionGrain>($"agent-session-input-boundary-{Guid.NewGuid():N}");

    protected OpenAgentSessionCommand Open(string runtime = "test") => new(
        "runner-1",
        runtime,
        WorkDir: "/work",
        Metadata: WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext("project-1", "workflow-1", "build")));

    protected async Task<IAgentSessionGrain> OpenBoundGrainAsync(string runtime = "test")
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open(runtime));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1"));
        return grain;
    }
}

public class AgentSessionGrainInputBoundaryPersistSuccessSpecs : AgentSessionGrainInputBoundarySpecsBase
{
    public AgentSessionGrainInputBoundaryPersistSuccessSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AppendRuntimeEvents_TwoBackToBackInputs_ProduceDistinctTurnsWithoutTimeAdvance()
    {
        // The new `session.input` boundary fences pending transcript data
        // before accepting a later input. Two back-to-back inputs on the
        // same logical and physical session must produce two distinct
        // persisted turns with their own prompts and parts, with a single
        // explicit flush at the end for observation.
        var grain = await OpenBoundGrainAsync();
        var persistence = grain.PersistenceCheckpoint(Fixture.Persistence);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"first-prompt\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"first-answer\"}"),
            }, "runtime-1"));

        // No flush or fake-time advance between inputs: the next input
        // is what triggers the persistence fence.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"second-prompt\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"second-answer\"}"),
            }, "runtime-1"));

        await persistence.WaitAsync();

        var sessionFlushes = Fixture.TranscriptStore.Flushes
            .Where(flush => string.Equals(
                flush.Turn.SessionId,
                grain.GetPrimaryKeyString(),
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, sessionFlushes.Length);
        var firstFlush = sessionFlushes[0];
        var secondFlush = sessionFlushes[1];
        Assert.NotNull(firstFlush.Turn);
        Assert.NotNull(secondFlush.Turn);
        Assert.NotEqual(firstFlush.Turn, secondFlush.Turn);
        Assert.Equal("first-prompt", firstFlush.Turn.PromptText);
        Assert.Equal("second-prompt", secondFlush.Turn.PromptText);
        Assert.Equal("runtime-1", firstFlush.Turn.RuntimeSessionId);
        Assert.Equal("runtime-1", secondFlush.Turn.RuntimeSessionId);

        Assert.Single(firstFlush.Parts);
        Assert.Single(secondFlush.Parts);
        Assert.Equal("first-answer", firstFlush.Parts[0].TextDelta);
        Assert.Equal("second-answer", secondFlush.Parts[0].TextDelta);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AcceptWorkflowInput_ReplayReusesTheFrozenTurnBindingAndRejectsMismatchedRuntimeEvents()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var command = new AcceptWorkflowAgentSessionInputCommand(
            "delivery-1",
            "implement the task",
            "workflow-1",
            "task-1.1",
            "work-1",
            "runner-1",
            "opencode",
            "runtime-1",
            "{\"text\":\"implement the task\"}");

        var first = await grain.AcceptWorkflowInputAsync(command);
        var replay = await grain.AcceptWorkflowInputAsync(command);

        Assert.Equal(first, replay);
        Assert.Equal("delivery-1", first.InputDeliveryId);
        Assert.NotEmpty(first.AgentTurnId);
        var binding = Assert.Single(Fixture.SessionWork.ExecutionBindings.Distinct());
        Assert.Equal(first.AgentTurnId, binding.AgentTurnId);
        Assert.Equal("task-1.1", binding.TaskRunId);
        Assert.Equal("work-1", binding.WorkId);
        Assert.Equal("runner-1", binding.RunnerId);

        var session = Assert.IsType<Mohist.Server.Sessions.Domain.AgentSession>(Fixture.StateStore.State);
        var turn = Assert.Single(session.Status.Turns!);
        Assert.Equal(binding, turn.WorkflowExecution);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new("message.delta", $"{{\"text\":\"working\",\"turnId\":\"{first.AgentTurnId}\"}}"),
            },
            "runtime-1",
            binding));
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AppendRuntimeEventsAsync(
            new AppendAgentSessionRuntimeEventsCommand(
                new List<AgentSessionRuntimeEventInput>
                {
                    new("message.delta", "{\"text\":\"wrong turn\",\"turnId\":\"turn-other\"}"),
                },
                "runtime-1",
                binding)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AppendRuntimeEventsAsync(
            new AppendAgentSessionRuntimeEventsCommand(
                new List<AgentSessionRuntimeEventInput>
                {
                    new("message.delta", "{\"text\":\"wrong\"}"),
                },
                "runtime-1",
                binding with { TaskRunId = "task-2.1" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AppendRuntimeEventsAsync(
            new AppendAgentSessionRuntimeEventsCommand(
                new List<AgentSessionRuntimeEventInput>
                {
                    new("message.delta", "{\"text\":\"missing\"}"),
                },
                "runtime-1")));
    }

    [Fact]
    public async Task AppendWorkflowRuntimeEvents_PreservesFailureWhenTheCloseBatchAlsoReportsIdle()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var receipt = await grain.AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
            "delivery-terminal-batch",
            "execute",
            "workflow-1",
            "task-terminal.1",
            "work-terminal",
            "runner-1",
            "opencode",
            "runtime-1",
            "{\"text\":\"execute\"}"));
        var binding = Assert.Single(Fixture.SessionWork.ExecutionBindings);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new(RuntimeEventTypes.TurnFailed,
                    $"{{\"turnId\":\"{receipt.AgentTurnId}\",\"failureReason\":\"tool failed\"}}"),
                new(RuntimeEventTypes.SessionActivity,
                    $"{{\"turnId\":\"{receipt.AgentTurnId}\",\"activity\":\"idle\",\"status\":\"failed\"}}"),
            },
            "runtime-1",
            binding));

        var observation = Assert.Single(Fixture.SessionWork.Observations);
        Assert.Equal(SessionWorkflowObservationKind.Failed, observation.Kind);
        Assert.Equal("turn-failed", observation.ReasonCode);
        Assert.Equal("tool failed", observation.Message);
    }

    [Fact]
    public async Task DelayedWorkflowObservation_UsesTheOldFrozenTurnBindingAfterSessionReuse()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var first = await grain.AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
            "delivery-1",
            "first task",
            "workflow-1",
            "task-1.1",
            "work-1",
            "runner-1",
            "opencode",
            "runtime-1",
            "{\"text\":\"first task\"}"));
        var firstBinding = Assert.Single(Fixture.SessionWork.ExecutionBindings);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput(
                    "session.activity",
                    $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{first.AgentTurnId}\"}}")
            },
            "runtime-1",
            firstBinding));
        Fixture.SessionWork.Observations.Clear();

        var second = await grain.AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
            "delivery-2",
            "second task",
            "workflow-1",
            "task-2.1",
            "work-2",
            "runner-1",
            "opencode",
            "runtime-1",
            "{\"text\":\"second task\"}"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput(
                    "session.activity",
                    $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{first.AgentTurnId}\"}}")
            },
            "runtime-1",
            firstBinding));

        var observation = Assert.Single(Fixture.SessionWork.Observations);
        Assert.Equal(firstBinding, observation.Binding);
        Assert.Equal(SessionWorkflowObservationKind.Idle, observation.Kind);
        var turns = await grain.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Completed, Assert.Single(turns, turn => turn.Id == first.AgentTurnId).Status);
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(turns, turn => turn.Id == second.AgentTurnId).Status);
    }
}

public class AgentSessionGrainInputBoundaryPersistFailureSpecs : AgentSessionGrainInputBoundarySpecsBase
{
    public AgentSessionGrainInputBoundaryPersistFailureSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AppendRuntimeEvents_TranscriptFailureOnSecondInput_LeavesFirstPendingRetryable()
    {
        // The first turn is appended with a session.input and an
        // activity event. The second input arrives while the first
        // transcript flush has not yet succeeded; the persistence
        // fence triggers a deterministic flush of the prior data.
        // When that flush fails, the new input is rejected, the
        // prior pending accumulator state remains retryable, and no
        // part of the second input is appended.
        var grain = await OpenBoundGrainAsync();

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"first-prompt\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"first-answer\"}"),
            }, "runtime-1"));

        // The next input will trigger the prior-data flush; that
        // flush must fail so the new input is rejected.
        var persistence = grain.PersistenceCheckpoint(Fixture.Persistence);
        Fixture.TranscriptStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("transcript store down"));

        var secondInputResults = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"second-prompt\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"second-answer\"}"),
            }, "runtime-1"));

        Assert.Empty(secondInputResults);

        // No flush completed; the first turn is still pending and must
        // contain its own prompt (no overwrite, no append of the
        // second input).
        Assert.Empty(Fixture.TranscriptStore.Flushes);
        var warning = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("rejected session.input", warning.Message);

        // Retry persistence deterministically (no scheduler waits, no
        // fake time): the next flush must surface the first turn
        // unchanged, with no second-input parts anywhere.
        await persistence.WaitAsync();

        Assert.Single(Fixture.TranscriptStore.Flushes);
        var retryFlush = Fixture.TranscriptStore.Flushes[0];
        Assert.Equal("first-prompt", retryFlush.Turn.PromptText);
        Assert.Single(retryFlush.Parts);
        Assert.Equal("first-answer", retryFlush.Parts[0].TextDelta);
    }
}
