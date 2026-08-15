using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class WorkflowAgentSessionCleanupSpecs : AgentSessionGrainInputBoundarySpecsBase
{
    public WorkflowAgentSessionCleanupSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AcceptWorkflowCleanup_CreatesAnAuthorizedIndependentTurnAndReplaysByOperationId()
    {
        var grain = await OpenBoundGrainAsync("pi");
        var original = await grain.AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
            "delivery-1",
            "implement the task",
            "workflow-1",
            "task-1.1",
            "work-1",
            "runner-1",
            "pi",
            "runtime-1",
            "{\"text\":\"implement the task\"}"));
        var binding = Assert.Single(Fixture.SessionWork.ExecutionBindings);
        var command = new AcceptWorkflowAgentSessionCleanupCommand(
            "cleanup-1",
            "clean the worktree",
            "workflow-1",
            "task-1.1",
            "work-1",
            "runner-1",
            original.AgentSessionId,
            "pi",
            "runtime-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AcceptWorkflowCleanupAsync(command));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                "session.activity",
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{original.AgentTurnId}\"}}") },
            "runtime-1",
            binding));
        Fixture.SessionWork.Observations.Clear();

        var cleanup = await grain.AcceptWorkflowCleanupAsync(command);
        var replay = await grain.AcceptWorkflowCleanupAsync(command);

        Assert.Equal(cleanup, replay);
        Assert.NotEqual(original.AgentTurnId, cleanup.AgentTurnId);
        Assert.Equal("workflow-cleanup-input:cleanup-1", cleanup.InputDeliveryId);
        Assert.Equal("workflow-cleanup-turn:cleanup-1", cleanup.AgentTurnId);
        Assert.Equal(new[] { binding }, Fixture.SessionWork.CleanupAuthorizations);
        Assert.Single(Fixture.SessionWork.ExecutionBindings);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                "session.input",
                $"{{\"text\":\"clean the worktree\",\"turnId\":\"{cleanup.AgentTurnId}\"}}") },
            "runtime-1",
            SessionTurnId: cleanup.AgentTurnId));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                "session.activity",
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{cleanup.AgentTurnId}\"}}") },
            "runtime-1",
            SessionTurnId: cleanup.AgentTurnId));
        await grain.PersistenceCheckpoint(Fixture.Persistence).WaitAsync();

        var session = Assert.IsType<AgentSession>(Fixture.StateStore.State);
        var cleanupInput = Assert.Single(session.Status.Inputs!, input => input.Id == cleanup.InputDeliveryId);
        Assert.Equal("workflow-cleanup", cleanupInput.Source);
        var cleanupTurn = Assert.Single(session.Status.Turns!, turn => turn.Id == cleanup.AgentTurnId);
        Assert.Null(cleanupTurn.WorkflowExecution);
        Assert.Equal(AgentTurnStatus.Completed, cleanupTurn.Status);
        Assert.Empty(Fixture.SessionWork.Observations);

        var inputCount = session.Status.Inputs!.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AcceptWorkflowCleanupAsync(command with { WorkId = "other-work" }));
        session = Assert.IsType<AgentSession>(Fixture.StateStore.State);
        Assert.Equal(inputCount, session.Status.Inputs!.Count);
    }
}
