using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.Tests.DirectApi;

[Trait("level", "L0")]
public sealed class PublicExecutionProjectionCollisionTests : IAsyncDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 9, 10, 15, 0, DateTimeKind.Utc);
    private readonly PublicProjectionTestSupport _harness = new();

    private static AgentJobTerminalResult CompletedResult(string outputJson) => new(
        AgentJobStatus.Completed,
        Message: "Done",
        Output: outputJson,
        ArtifactUploadIds: null,
        FailureReason: null,
        ExitCode: 0);

    [Fact]
    public async Task AnchorOwnerConflict_PreservesTerminalOwnerAndLetsAllCheckpointsCatchUp()
    {
        const string jobId = "job_feedback_shared";
        const string inputId = "input_feedback_shared";
        const string turnId = "turn_feedback_shared";
        const string ownerSessionId = "session_feedback_plan";
        const string conflictingSessionId = "session_feedback_check";
        await _harness.SeedJobAsync(
            jobId,
            "proj_pub",
            "agent_pub",
            ownerSessionId,
            inputId,
            turnId,
            status: AgentJobStatus.Completed,
            terminalResult: CompletedResult("""{"text":"plan complete"}"""),
            terminalAt: new DateTimeOffset(T0.AddMinutes(2)));
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession(ownerSessionId, "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input(inputId, jobId, recordedAt: T0)],
            turns:
            [
                PublicProjectionTestSupport.Turn(
                    turnId,
                    inputId,
                    jobId,
                    AgentTurnStatus.Completed,
                    recordedAt: T0,
                    updatedAt: T0.AddMinutes(2),
                    result: new AgentTurnResult(Output: """{"text":"plan complete"}""")),
            ]));
        Assert.True(await _harness.Engine.ProcessPendingAsync());

        var ownedBefore = (await _harness.SnapshotsAsync())
            .Where(row => row.AnchorId is jobId or inputId or turnId)
            .ToDictionary(row => (row.AnchorType, row.AnchorId));
        var ownerEventsBefore = await _harness.EventsAsync(ownerSessionId);
        Assert.Equal(3, ownedBefore.Count);
        Assert.All(ownedBefore.Values, row =>
        {
            Assert.Equal(ownerSessionId, row.SessionId);
            Assert.NotNull(row.TerminalFact);
        });

        // Production shape: one historical Workflow invocation identity was
        // reused by a later Stage. The AgentJob now joins the check Session,
        // while terminal public anchors still belong to the plan Session.
        await _harness.RebindJobAsync(jobId, conflictingSessionId, inputId, turnId);
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession(conflictingSessionId, "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input(inputId, jobId, recordedAt: T0.AddMinutes(3))],
            turns:
            [
                PublicProjectionTestSupport.Turn(
                    turnId,
                    inputId,
                    jobId,
                    AgentTurnStatus.Completed,
                    recordedAt: T0.AddMinutes(3),
                    updatedAt: T0.AddMinutes(4),
                    result: new AgentTurnResult(Output: """{"text":"check complete"}""")),
            ]));

        await _harness.SeedJobAsync(
            "job_unrelated",
            "proj_pub",
            "agent_pub",
            "session_unrelated",
            "input_unrelated",
            "turn_unrelated");
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_unrelated", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_unrelated", "job_unrelated")],
            turns: [PublicProjectionTestSupport.Turn("turn_unrelated", "input_unrelated", "job_unrelated", AgentTurnStatus.Queued)]));

        // Both targets are selected and committed by this one batch.
        Assert.True(await _harness.Engine.ProcessPendingAsync());

        var ownedAfter = (await _harness.SnapshotsAsync())
            .Where(row => row.AnchorId is jobId or inputId or turnId)
            .ToDictionary(row => (row.AnchorType, row.AnchorId));
        Assert.Equal(ownedBefore.Keys, ownedAfter.Keys);
        foreach (var entry in ownedBefore)
        {
            var after = ownedAfter[entry.Key];
            Assert.Equal(entry.Value.SessionId, after.SessionId);
            Assert.Equal(entry.Value.ProjectId, after.ProjectId);
            Assert.Equal(entry.Value.SnapshotJson, after.SnapshotJson);
            Assert.Equal(entry.Value.TerminalFact, after.TerminalFact);
            Assert.Equal(entry.Value.TerminalOutcome, after.TerminalOutcome);
            Assert.Equal(entry.Value.TerminalAt, after.TerminalAt);
            Assert.Equal(entry.Value.TerminalSequence, after.TerminalSequence);
        }
        Assert.Equal(
            ownerEventsBefore.Select(row => (row.Sequence, row.SourceTransition, row.PayloadJson)),
            (await _harness.EventsAsync(ownerSessionId)).Select(row => (row.Sequence, row.SourceTransition, row.PayloadJson)));
        Assert.Empty(await _harness.EventsAsync(conflictingSessionId));
        Assert.NotNull(await _harness.SnapshotAsync("turn", "turn_unrelated"));

        var reads = new PublicExecutionReadQuerier(_harness.DbFactory);
        Assert.False(await reads.IsSessionProjectionBehindAsync(conflictingSessionId));
        Assert.False(await reads.IsSessionProjectionBehindAsync("session_unrelated"));
        var checkpoints = await _harness.CheckpointsAsync();
        Assert.Single(checkpoints, row =>
            row.Feed == PublicProjectionFeeds.AgentSessions
            && row.SourceKey == conflictingSessionId);
        Assert.Single(checkpoints, row =>
            row.Feed == PublicProjectionFeeds.AgentSessions
            && row.SourceKey == "session_unrelated");
        Assert.Equal(3, _harness.ProjectionLogger.Entries.Count(entry =>
            entry.Message.Contains("anchor owner conflict", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(_harness.ProjectionLogger.Entries, entry =>
            entry.Message.Contains("anchor owner conflict", StringComparison.OrdinalIgnoreCase)
            && Equals(entry.State["OwnerSessionId"], ownerSessionId)
            && Equals(entry.State["ConflictingSessionId"], conflictingSessionId));

        // Reconciliation consumed the legacy collision exactly once; the next
        // sweep has no poisoned target to retry.
        Assert.False(await _harness.Engine.ProcessPendingAsync());

        var rebuiltGeneration = await _harness.Engine.RebuildSessionAsync(conflictingSessionId);
        Assert.Equal(2, rebuiltGeneration);
        Assert.Empty(await _harness.EventsAsync(conflictingSessionId, rebuiltGeneration));
        Assert.Equal(
            ownedBefore[("turn", turnId)].SnapshotJson,
            (await _harness.SnapshotAsync("turn", turnId))!.SnapshotJson);
    }

    [Fact]
    public async Task SameBatchAnchorOwnerConflict_UsesFirstTrackedOwnerWithoutDuplicateEventsOrRollback()
    {
        const string firstSessionId = "session_same_batch_first";
        const string secondSessionId = "session_same_batch_second";
        const string inputId = "input_same_batch_shared";
        const string turnId = "turn_same_batch_shared";
        await _harness.SeedJobAsync(
            "job_same_batch_first",
            "proj_pub",
            "agent_pub",
            firstSessionId,
            inputId,
            turnId);
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession(firstSessionId, "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input(inputId, "job_same_batch_first")],
            turns: [PublicProjectionTestSupport.Turn(turnId, inputId, "job_same_batch_first", AgentTurnStatus.Queued)]));
        await _harness.SeedJobAsync(
            "job_same_batch_second",
            "proj_pub",
            "agent_pub",
            secondSessionId,
            inputId,
            turnId);
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession(secondSessionId, "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input(inputId, "job_same_batch_second")],
            turns: [PublicProjectionTestSupport.Turn(turnId, inputId, "job_same_batch_second", AgentTurnStatus.Queued)]));

        Assert.True(await _harness.Engine.ProcessPendingAsync());

        var input = await _harness.SnapshotAsync("input", inputId);
        var turn = await _harness.SnapshotAsync("turn", turnId);
        Assert.NotNull(input);
        Assert.NotNull(turn);
        Assert.Equal(input!.SessionId, turn!.SessionId);
        var ownerSessionId = input.SessionId!;
        var conflictingSessionId = string.Equals(ownerSessionId, firstSessionId, StringComparison.Ordinal)
            ? secondSessionId
            : firstSessionId;
        Assert.Equal(
            [PublicSessionEventTypes.InputAccepted, PublicSessionEventTypes.TurnQueued],
            (await _harness.EventsAsync(ownerSessionId)).Select(row => row.Type));
        Assert.Empty(await _harness.EventsAsync(conflictingSessionId));
        Assert.NotNull(await _harness.SnapshotAsync("job", "job_same_batch_first"));
        Assert.NotNull(await _harness.SnapshotAsync("job", "job_same_batch_second"));

        var reads = new PublicExecutionReadQuerier(_harness.DbFactory);
        Assert.False(await reads.IsSessionProjectionBehindAsync(firstSessionId));
        Assert.False(await reads.IsSessionProjectionBehindAsync(secondSessionId));
        var checkpoints = await _harness.CheckpointsAsync();
        Assert.Single(checkpoints, row =>
            row.Feed == PublicProjectionFeeds.AgentSessions
            && row.SourceKey == firstSessionId);
        Assert.Single(checkpoints, row =>
            row.Feed == PublicProjectionFeeds.AgentSessions
            && row.SourceKey == secondSessionId);
        Assert.Equal(2, _harness.ProjectionLogger.Entries.Count(entry =>
            entry.Message.Contains("anchor owner conflict", StringComparison.OrdinalIgnoreCase)));
        Assert.False(await _harness.Engine.ProcessPendingAsync());
    }

    [Fact]
    public async Task CommittedAnchorCollision_DoesNotStarveBoundedMultiBatchCatchUp()
    {
        const string sharedJobId = "job_multibatch_shared";
        const string sharedInputId = "input_multibatch_shared";
        const string sharedTurnId = "turn_multibatch_shared";
        const string ownerSessionId = "session_multibatch_owner";
        const string conflictingSessionId = "session_multibatch_conflict";
        await _harness.SeedJobAsync(
            sharedJobId,
            "proj_pub",
            "agent_pub",
            ownerSessionId,
            sharedInputId,
            sharedTurnId);
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession(ownerSessionId, "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input(sharedInputId, sharedJobId)],
            turns: [PublicProjectionTestSupport.Turn(sharedTurnId, sharedInputId, sharedJobId, AgentTurnStatus.Queued)]));
        Assert.True(await _harness.Engine.ProcessPendingAsync());

        await _harness.RebindJobAsync(sharedJobId, conflictingSessionId, sharedInputId, sharedTurnId);
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession(conflictingSessionId, "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input(sharedInputId, sharedJobId)],
            turns: [PublicProjectionTestSupport.Turn(sharedTurnId, sharedInputId, sharedJobId, AgentTurnStatus.Queued)]));

        var expected = new List<(string SessionId, string InputId)>();
        for (var index = 0; index < 21; index++)
        {
            var suffix = index.ToString("D2");
            var sessionId = $"session_multibatch_{suffix}";
            var jobId = $"job_multibatch_{suffix}";
            var inputId = $"input_multibatch_{suffix}";
            var turnId = $"turn_multibatch_{suffix}";
            expected.Add((sessionId, inputId));
            await _harness.SeedJobAsync(jobId, "proj_pub", "agent_pub", sessionId, inputId, turnId);
            await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
                _harness.BuildSession(sessionId, "proj_pub", "agent_pub"),
                AgentSessionActivity.Active,
                inputs: [PublicProjectionTestSupport.Input(inputId, jobId)],
                turns: [PublicProjectionTestSupport.Turn(turnId, inputId, jobId, AgentTurnStatus.Queued)]));
        }

        var successfulSweeps = 0;
        var caughtUp = false;
        for (var sweep = 0; sweep < 10; sweep++)
        {
            if (!await _harness.Engine.ProcessPendingAsync(targetLimit: 5))
            {
                caughtUp = true;
                break;
            }

            successfulSweeps++;
        }

        Assert.True(caughtUp);
        Assert.True(successfulSweeps >= 5);
        var reads = new PublicExecutionReadQuerier(_harness.DbFactory);
        var checkpoints = await _harness.CheckpointsAsync();
        foreach (var item in expected)
        {
            Assert.Equal(
                PublicReadStatus.Found,
                (await reads.ReadInputAsync("proj_pub", item.InputId)).Status);
            Assert.Single(checkpoints, row =>
                row.Feed == PublicProjectionFeeds.AgentSessions
                && row.SourceKey == item.SessionId);
        }

        Assert.Single(checkpoints, row =>
            row.Feed == PublicProjectionFeeds.AgentSessions
            && row.SourceKey == conflictingSessionId);
        var warningCount = _harness.ProjectionLogger.Entries.Count(entry =>
            entry.Message.Contains("anchor owner conflict", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, warningCount);
        Assert.False(await _harness.Engine.ProcessPendingAsync(targetLimit: 5));
        Assert.Equal(warningCount, _harness.ProjectionLogger.Entries.Count(entry =>
            entry.Message.Contains("anchor owner conflict", StringComparison.OrdinalIgnoreCase)));
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
