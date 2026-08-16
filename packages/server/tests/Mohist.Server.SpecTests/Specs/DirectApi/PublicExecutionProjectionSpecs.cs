using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// The public execution projection engine: prepared-launch anchoring,
/// the five-state aggregate with terminal fences, atomic
/// snapshot/journal/checkpoint commits, checkpoint-based crash
/// recovery, stream generations, and the public event vocabulary —
/// all driven from durable canonical facts seeded through the real
/// stores.
/// </summary>
public sealed class PublicExecutionProjectionSpecs : IAsyncDisposable
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
    public async Task Migration_CreatesThePublicProjectionTables_WithoutTouchingExistingOnes()
    {
        await using var db = await _harness.DbFactory.CreateDbContextAsync();
        var tables = await db.Database.SqlQuery<string>(
            $"SELECT name AS \"Value\" FROM sqlite_master WHERE type='table' AND name LIKE 'public_%' ORDER BY name")
            .ToListAsync();

        Assert.Equal(
            ["public_execution_snapshots", "public_projection_checkpoints", "public_session_events", "public_stream_states"],
            tables);
    }

    [Fact]
    public async Task PreparedLaunch_ProjectsJobAnchorWithNullLiveIds_BeforeSessionAcceptance()
    {
        await _harness.SeedJobAsync("job_prep_1", "proj_pub", "agent_pub", "session_prep_1", "input_prep_1", "turn_prep_1");

        var worked = await _harness.Engine.ProcessPendingAsync();

        Assert.True(worked);
        var job = await _harness.SnapshotAsync("job", "job_prep_1");
        Assert.NotNull(job);
        var dto = ParseSnapshot(job!);
        Assert.Equal("job_prep_1", dto.JobId);
        Assert.Equal(PublicExecutionFieldValues.StatusAccepted, dto.Status);
        Assert.Equal(PublicExecutionFieldValues.JobPreparing, dto.JobStatus);
        Assert.Null(dto.SessionId);
        Assert.Null(dto.InputId);
        Assert.Null(dto.TurnId);
        Assert.Null(dto.Sequence);

        // No Session exists yet, so no stream state, no journal, and no
        // session checkpoint — only the consumed job feeds advanced.
        Assert.Null(await _harness.StreamStateAsync("session_prep_1"));
        Assert.Empty(await _harness.EventsAsync());
        var checkpoint = (await _harness.CheckpointsAsync()).Single(row => row.Feed == PublicProjectionFeeds.AgentJobs);
        Assert.Equal("job_prep_1", checkpoint.SourceKey);
    }

    [Fact]
    public async Task SessionAcceptance_JoinsLiveIdsOnTheSameJobAnchor_AndCommitsMutuallyConsistentState()
    {
        await _harness.SeedJobAsync("job_join_1", "proj_pub", "agent_pub", "session_join_1", "input_join_1", "turn_join_1");
        var session = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_join_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_join_1", "job_join_1")],
            turns: [PublicProjectionTestSupport.Turn("turn_join_1", "input_join_1", "job_join_1", AgentTurnStatus.Queued)]);
        await _harness.SaveSessionAsync(session);

        var worked = await _harness.Engine.ProcessPendingAsync();
        Assert.True(worked);

        var job = ParseSnapshot((await _harness.SnapshotAsync("job", "job_join_1"))!);
        Assert.Equal("session_join_1", job.SessionId);
        Assert.Equal("input_join_1", job.InputId);
        Assert.Equal("turn_join_1", job.TurnId);
        Assert.Equal(PublicExecutionFieldValues.StatusQueued, job.Status);
        Assert.Equal(PublicExecutionFieldValues.JobQueued, job.JobStatus);
        Assert.Equal(PublicExecutionFieldValues.TurnQueued, job.TurnStatus);

        // The input and turn anchors exist beside the job anchor.
        var input = ParseSnapshot((await _harness.SnapshotAsync("input", "input_join_1"))!);
        Assert.Equal(PublicExecutionFieldValues.InputAccepted, input.InputStatus);
        var turn = ParseSnapshot((await _harness.SnapshotAsync("turn", "turn_join_1"))!);
        Assert.Equal(PublicExecutionFieldValues.TurnQueued, turn.TurnStatus);

        // Mutual consistency at one checkpoint: stream state, journal,
        // and every snapshot agree on the sequence, and every execution
        // payload is exactly the allowlisted shape.
        var stream = await _harness.StreamStateAsync("session_join_1");
        Assert.NotNull(stream);
        Assert.Equal(1, stream!.ActiveGeneration);
        var events = await _harness.EventsAsync("session_join_1");
        var types = events.Select(row => row.Type).ToList();
        Assert.Equal([PublicSessionEventTypes.InputAccepted, PublicSessionEventTypes.TurnQueued], types);
        Assert.Equal(1, events[0].Sequence);
        Assert.Equal(2, events[1].Sequence);
        Assert.Equal(3, stream.NextSequence);
        Assert.Equal(2, stream.LatestSequence);
        Assert.Equal(1, stream.EarliestSequence);
        Assert.Equal(2, job.Sequence);
        Assert.Equal(2, input.Sequence);
        Assert.Equal(2, turn.Sequence);

        foreach (var row in events)
        {
            var payload = JsonDocument.Parse(row.PayloadJson).RootElement;
            Assert.All(
                payload.EnumerateObject(),
                property => Assert.Contains(
                    property.Name,
                    new[] { "projectId", "agentId", "jobId", "sessionId", "inputId", "turnId", "status", "jobStatus", "sessionActivity", "admission", "inputStatus", "turnStatus", "outcome", "reasonCode", "output", "error", "acceptedAt", "queuedAt", "startedAt", "terminalAt", "observedAt", "sequence" }));
        }

        // The session feed checkpoint proves the consumed state digest.
        var sessionCheckpoint = (await _harness.CheckpointsAsync())
            .Single(row => row.Feed == PublicProjectionFeeds.AgentSessions);
        Assert.Equal("session_join_1", sessionCheckpoint.SourceKey);
        Assert.NotEmpty(sessionCheckpoint.Watermark);
    }

    [Fact]
    public async Task LifecycleHistory_PreservesCompressedQueuedRunningAndTerminalTransitions()
    {
        await _harness.SeedJobAsync("job_history_1", "proj_pub", "agent_pub", "session_history_1", "input_history_1", "turn_history_1");

        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_history_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_history_1", "job_history_1", recordedAt: T0)],
            turns: [PublicProjectionTestSupport.Turn("turn_history_1", "input_history_1", "job_history_1", AgentTurnStatus.Queued, recordedAt: T0)]));
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_history_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_history_1", "job_history_1", recordedAt: T0)],
            turns: [PublicProjectionTestSupport.Turn("turn_history_1", "input_history_1", "job_history_1", AgentTurnStatus.Executing, recordedAt: T0, updatedAt: T0.AddSeconds(1))]));
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_history_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_history_1", "job_history_1", recordedAt: T0)],
            turns: [PublicProjectionTestSupport.Turn(
                "turn_history_1",
                "input_history_1",
                "job_history_1",
                AgentTurnStatus.Completed,
                recordedAt: T0,
                updatedAt: T0.AddSeconds(2),
                result: new AgentTurnResult(Output: """{"text":"Done."}"""))]));

        Assert.Empty(await _harness.EventsAsync("session_history_1"));
        Assert.True(await _harness.Engine.ProcessPendingAsync());

        var events = await _harness.EventsAsync("session_history_1");
        Assert.Equal(
            [
                PublicSessionEventTypes.InputAccepted,
                PublicSessionEventTypes.TurnQueued,
                PublicSessionEventTypes.TurnRunning,
                PublicSessionEventTypes.TurnTerminal,
            ],
            events.Select(row => row.Type));
        Assert.Equal(4, events.Select(row => row.SourceTransition).Distinct(StringComparer.Ordinal).Count());

        var payloads = events
            .Select(row => JsonSerializer.Deserialize<PublicExecutionRead>(row.PayloadJson, JSON.PublicApi)!)
            .ToList();
        Assert.Equal(PublicExecutionFieldValues.StatusQueued, payloads[0].Status);
        Assert.Equal(PublicExecutionFieldValues.StatusQueued, payloads[1].Status);
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, payloads[2].Status);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, payloads[3].Status);
    }

    [Fact]
    public async Task LifecycleHistory_PreservesDistinctUnknownEpisodes()
    {
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_unknown_history", "proj_pub", "agent_pub"),
            AgentSessionActivity.Unknown,
            inputs: [],
            turns: []));
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_unknown_history", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [],
            turns: []));
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_unknown_history", "proj_pub", "agent_pub"),
            AgentSessionActivity.Unknown,
            inputs: [],
            turns: []));

        Assert.True(await _harness.Engine.ProcessPendingAsync());

        var events = (await _harness.EventsAsync("session_unknown_history"))
            .Where(row => row.Type == PublicSessionEventTypes.SessionUnknown)
            .ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(row => row.SourceTransition).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([1L, 2L], events.Select(row => row.Sequence));
    }

    [Fact]
    public async Task FiveStatePrecedence_RunningOverQueued_UnknownOnlyFromFacts_OutcomePendingIsRunning()
    {
        // accepted: a durably accepted input with no turn yet.
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_acc", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_acc", "job_acc")],
            turns: []));
        // running: an accepted input whose turn is executing.
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_run", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_run", "job_run")],
            turns: [PublicProjectionTestSupport.Turn("turn_run", "input_run", "job_run", AgentTurnStatus.Executing)]));
        // outcome_pending: executing while a stop claim is unresolved —
        // running aggregate, blocked admission, never terminal.
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_pending", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_pending", "job_pending")],
            turns: [PublicProjectionTestSupport.Turn("turn_pending", "input_pending", "job_pending", AgentTurnStatus.Executing)],
            pendingStop: new AgentSessionStopClaim("turn_pending", "op_stop_1")));
        // unknown: consumed facts say the activity is unconfirmable.
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_unknown", "proj_pub", "agent_pub"),
            AgentSessionActivity.Unknown,
            inputs: [PublicProjectionTestSupport.Input("input_unknown", "job_unknown")],
            turns: []));

        await _harness.Engine.ProcessPendingAsync();

        var accepted = ParseSnapshot((await _harness.SnapshotAsync("input", "input_acc"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusAccepted, accepted.Status);
        Assert.Equal(PublicExecutionFieldValues.InputAccepted, accepted.InputStatus);
        Assert.Null(accepted.TurnStatus);

        var running = ParseSnapshot((await _harness.SnapshotAsync("turn", "turn_run"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, running.Status);
        Assert.Equal(PublicExecutionFieldValues.TurnRunning, running.TurnStatus);
        Assert.Equal(PublicExecutionFieldValues.AdmissionReady, running.Admission);

        var outcomePending = ParseSnapshot((await _harness.SnapshotAsync("turn", "turn_pending"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusRunning, outcomePending.Status);
        Assert.Equal(PublicExecutionFieldValues.TurnOutcomePending, outcomePending.TurnStatus);
        Assert.Equal(PublicExecutionFieldValues.AdmissionBlocked, outcomePending.Admission);
        Assert.NotEqual(PublicExecutionFieldValues.StatusTerminal, outcomePending.Status);
        Assert.Contains(PublicSessionEventTypes.TurnOutcomePending, (await _harness.EventsAsync("session_pending")).Select(row => row.Type));

        var unknown = ParseSnapshot((await _harness.SnapshotAsync("input", "input_unknown"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusUnknown, unknown.Status);
        Assert.Equal(PublicExecutionFieldValues.AdmissionBlocked, unknown.Admission);
        Assert.Contains(PublicSessionEventTypes.SessionUnknown, (await _harness.EventsAsync("session_unknown")).Select(row => row.Type));
    }

    [Fact]
    public async Task UnknownIsNeverEmittedFromProjectionBacklog()
    {
        // Durable facts exist but the projector has not consumed them:
        // nothing is projected at all — the projection never guesses a
        // public state from facts it has not read.
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_backlog", "proj_pub", "agent_pub"),
            AgentSessionActivity.Unknown,
            inputs: [PublicProjectionTestSupport.Input("input_backlog", null)],
            turns: []));

        Assert.Empty(await _harness.SnapshotsAsync());
        Assert.Empty(await _harness.EventsAsync());

        await _harness.Engine.ProcessPendingAsync();

        // After consumption the facts genuinely say unknown.
        var dto = ParseSnapshot((await _harness.SnapshotAsync("input", "input_backlog"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusUnknown, dto.Status);
    }

    [Fact]
    public async Task DurableRejection_IsTerminalWithOutcomeRejected_OnTheSameJobAnchor()
    {
        await _harness.SeedJobAsync("job_rej_1", "proj_pub", "agent_pub", "session_rej_1", "input_rej_1", "turn_rej_1");
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_rej_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_rej_1", "job_rej_1", AgentSessionInputAcceptance.Rejected)],
            turns: []));

        await _harness.Engine.ProcessPendingAsync();

        var job = ParseSnapshot((await _harness.SnapshotAsync("job", "job_rej_1"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, job.Status);
        Assert.Equal(PublicExecutionFieldValues.OutcomeRejected, job.Outcome);
        Assert.Equal(PublicExecutionFieldValues.InputRejected, job.InputStatus);
        Assert.Null(job.TurnId);
        Assert.NotNull(job.Error);
        Assert.Equal(PublicExecutionFieldValues.OutcomeRejected, job.Error!.Code);
        Assert.DoesNotContain("stack", job.Error.Message, StringComparison.OrdinalIgnoreCase);

        var input = ParseSnapshot((await _harness.SnapshotAsync("input", "input_rej_1"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, input.Status);
        Assert.Equal(PublicExecutionFieldValues.OutcomeRejected, input.Outcome);

        Assert.Contains(PublicSessionEventTypes.InputRejected, (await _harness.EventsAsync("session_rej_1")).Select(row => row.Type));
    }

    [Fact]
    public async Task TerminalFence_LateRunnerResultCannotRevertOrReplaceTheTerminalFact()
    {
        await _harness.SeedJobAsync("job_fence_1", "proj_pub", "agent_pub", "session_fence_1", "input_fence_1", "turn_fence_1");
        var session = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_fence_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_fence_1", "job_fence_1")],
            turns: [PublicProjectionTestSupport.Turn(
                "turn_fence_1",
                "input_fence_1",
                "job_fence_1",
                AgentTurnStatus.Completed,
                updatedAt: T0.AddMinutes(2),
                result: new AgentTurnResult(Output: """{"text":"Original final output."}"""))]);
        await _harness.SaveSessionAsync(session);
        await _harness.SaveJobStatusAsync("job_fence_1", AgentJobStatus.Completed, terminalResult: CompletedResult("""{"text":"Original final output."}"""), terminalAt: new DateTimeOffset(T0.AddMinutes(2)));

        await _harness.Engine.ProcessPendingAsync();

        var fenced = await _harness.SnapshotAsync("turn", "turn_fence_1");
        Assert.Equal("turn:turn_fence_1:terminal", fenced!.TerminalFact);
        var fencedDto = ParseSnapshot(fenced);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, fencedDto.Status);
        Assert.Equal(PublicExecutionFieldValues.OutcomeCompleted, fencedDto.Outcome);
        Assert.Equal("Original final output.", fencedDto.Output!.Text);
        var terminalEvents = (await _harness.EventsAsync("session_fence_1"))
            .Where(row => row.Type == PublicSessionEventTypes.TurnTerminal)
            .ToList();
        var fencedSequence = Assert.Single(terminalEvents).Sequence;
        Assert.Equal(fencedSequence, (await _harness.SnapshotAsync("turn", "turn_fence_1"))!.TerminalSequence);

        // A delayed, conflicting Runner result arrives later: the same
        // turn now claims a failed outcome with different output. The
        // fence keeps the winning terminal fact.
        var stale = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_fence_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_fence_1", "job_fence_1")],
            turns: [PublicProjectionTestSupport.Turn(
                "turn_fence_1",
                "input_fence_1",
                "job_fence_1",
                AgentTurnStatus.Failed,
                updatedAt: T0.AddMinutes(9),
                result: new AgentTurnResult(FailureReason: "provider exploded with stack trace", FailureCategory: "provider"))]);
        await _harness.SaveSessionAsync(stale);
        await _harness.SaveJobStatusAsync("job_fence_1", AgentJobStatus.Failed, terminalResult: new AgentJobTerminalResult(
            AgentJobStatus.Failed, "late", """{"text":"Should never replace."}""", null, "provider boom", 1), terminalAt: new DateTimeOffset(T0.AddMinutes(9)));

        await _harness.Engine.ProcessPendingAsync();

        var after = ParseSnapshot((await _harness.SnapshotAsync("turn", "turn_fence_1"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusTerminal, after.Status);
        Assert.Equal(PublicExecutionFieldValues.OutcomeCompleted, after.Outcome);
        Assert.Equal("Original final output.", after.Output!.Text);
        Assert.Equal(fencedSequence, after.Sequence);
        Assert.Null(after.Error);

        // At most one terminal public event for the target.
        var terminalEventsAfter = (await _harness.EventsAsync("session_fence_1"))
            .Where(row => row.Type == PublicSessionEventTypes.TurnTerminal)
            .ToList();
        Assert.Single(terminalEventsAfter);
        Assert.Equal(fencedSequence, terminalEventsAfter[0].Sequence);

        // The fenced internal terminal fact is stored and stable.
        Assert.Equal("turn:turn_fence_1:terminal", (await _harness.SnapshotAsync("turn", "turn_fence_1"))!.TerminalFact);
    }

    [Fact]
    public async Task CrashBeforeCommit_LeavesNoPartialSnapshotSequenceOrCheckpoint_AndReplayProducesTheSameOutcome()
    {
        await _harness.SeedJobAsync("job_crash_1", "proj_pub", "agent_pub", "session_crash_1", "input_crash_1", "turn_crash_1");
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_crash_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_crash_1", "job_crash_1")],
            turns: [PublicProjectionTestSupport.Turn("turn_crash_1", "input_crash_1", "job_crash_1", AgentTurnStatus.Executing)]));

        var worked = await _harness.Engine.ProcessPendingAsync(commit: false);
        Assert.True(worked);

        Assert.Empty(await _harness.SnapshotsAsync());
        Assert.Empty(await _harness.EventsAsync());
        Assert.Empty(await _harness.CheckpointsAsync());
        Assert.Null(await _harness.StreamStateAsync("session_crash_1"));

        // Replay of the same durable input produces the same outcome.
        Assert.True(await _harness.Engine.ProcessPendingAsync());
        var stream = await _harness.StreamStateAsync("session_crash_1");
        Assert.Equal(1, stream!.ActiveGeneration);
        Assert.Equal(1, stream.EarliestSequence);
        Assert.Equal(2, stream.LatestSequence);
        Assert.Equal(3, stream.NextSequence);
        var events = await _harness.EventsAsync("session_crash_1");
        Assert.Equal([PublicSessionEventTypes.InputAccepted, PublicSessionEventTypes.TurnRunning], events.Select(row => row.Type));
        Assert.Equal([1L, 2L], events.Select(row => row.Sequence));
    }

    [Fact]
    public async Task CrashAfterCommit_ResumesPastTheCheckpoint_WithoutASecondSequenceForTheSameTransition()
    {
        await _harness.SeedJobAsync("job_replay_1", "proj_pub", "agent_pub", "session_replay_1", "input_replay_1", "turn_replay_1");
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_replay_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_replay_1", "job_replay_1")],
            turns: [PublicProjectionTestSupport.Turn("turn_replay_1", "input_replay_1", "job_replay_1", AgentTurnStatus.Executing)]));

        Assert.True(await _harness.Engine.ProcessPendingAsync());
        var afterFirstCommit = await _harness.EventsAsync("session_replay_1");
        Assert.Equal(2, afterFirstCommit.Count);
        var checkpointsAfterCommit = await _harness.CheckpointsAsync();

        // Restart-and-replay: the same engine (or a fresh one) finds
        // nothing new — no second sequence, no renumbering.
        Assert.False(await _harness.Engine.ProcessPendingAsync());
        var afterReplay = await _harness.EventsAsync("session_replay_1");
        Assert.Equal(afterFirstCommit.Select(row => (row.Sequence, row.SourceTransition)), afterReplay.Select(row => (row.Sequence, row.SourceTransition)));
        Assert.Equal(checkpointsAfterCommit.Select(row => (row.Feed, row.SourceKey, row.Watermark)), (await _harness.CheckpointsAsync()).Select(row => (row.Feed, row.SourceKey, row.Watermark)));

        // A brand-new engine instance (ordinary restart) keeps
        // generation one and appends only genuinely new transitions.
        var restarted = new PublicApiProjectionEngine(
            _harness.DbFactory,
            _harness.Time,
            NullLogger<PublicApiProjectionEngine>.Instance);
        var advanced = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_replay_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_replay_1", "job_replay_1")],
            turns: [PublicProjectionTestSupport.Turn(
                "turn_replay_1",
                "input_replay_1",
                "job_replay_1",
                AgentTurnStatus.Completed,
                updatedAt: T0.AddMinutes(4),
                result: new AgentTurnResult(Output: """{"text":"Done."}"""))]);
        await _harness.SaveSessionAsync(advanced);

        Assert.True(await restarted.ProcessPendingAsync());
        var events = await _harness.EventsAsync("session_replay_1");
        Assert.Equal(3, events.Count);
        Assert.Equal(PublicSessionEventTypes.TurnTerminal, events[2].Type);
        Assert.Equal(3, events[2].Sequence);
        var stream = await _harness.StreamStateAsync("session_replay_1");
        Assert.Equal(1, stream!.ActiveGeneration);
        Assert.Equal(3, stream.LatestSequence);
        Assert.Equal(4, stream.NextSequence);
    }

    [Fact]
    public async Task Rebuild_SwitchesGenerationAtomically_PreservingTheSequenceAllocator()
    {
        await _harness.SeedJobAsync("job_gen_1", "proj_pub", "agent_pub", "session_gen_1", "input_gen_1", "turn_gen_1");
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_gen_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_gen_1", "job_gen_1")],
            turns: [PublicProjectionTestSupport.Turn("turn_gen_1", "input_gen_1", "job_gen_1", AgentTurnStatus.Queued)]));
        await _harness.Engine.ProcessPendingAsync();

        var streamBefore = await _harness.StreamStateAsync("session_gen_1");
        var generationOneEvents = await _harness.EventsAsync("session_gen_1", generation: 1);
        Assert.Equal(2, generationOneEvents.Count);

        var newGeneration = await _harness.Engine.RebuildSessionAsync("session_gen_1");

        Assert.Equal(2, newGeneration);
        var stream = await _harness.StreamStateAsync("session_gen_1");
        Assert.Equal(2, stream!.ActiveGeneration);
        // Sequences are never reused or renumbered: the new generation
        // continues past the last published sequence of the old one.
        var generationTwoEvents = await _harness.EventsAsync("session_gen_1", generation: 2);
        Assert.Equal(2, generationTwoEvents.Count);
        Assert.Equal(3, generationTwoEvents[0].Sequence);
        Assert.Equal(4, generationTwoEvents[1].Sequence);
        Assert.Equal(3, stream.EarliestSequence);
        Assert.Equal(4, stream.LatestSequence);
        Assert.Equal(5, stream.NextSequence);

        // The previous generation's journal is retained, not mutated.
        var retained = await _harness.EventsAsync("session_gen_1", generation: 1);
        Assert.Equal(2, retained.Count);
        Assert.Equal([1L, 2L], retained.Select(row => row.Sequence));

        // New transitions continue in the active generation.
        var advanced = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_gen_1", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_gen_1", "job_gen_1")],
            turns: [PublicProjectionTestSupport.Turn(
                "turn_gen_1",
                "input_gen_1",
                "job_gen_1",
                AgentTurnStatus.Completed,
                updatedAt: T0.AddMinutes(4),
                result: new AgentTurnResult(Output: """{"text":"Done."}"""))]);
        await _harness.SaveSessionAsync(advanced);
        await _harness.Engine.ProcessPendingAsync();

        var after = await _harness.EventsAsync("session_gen_1", generation: 2);
        Assert.Equal(3, after.Count);
        Assert.Equal(PublicSessionEventTypes.TurnTerminal, after[2].Type);
        Assert.Equal(5, after[2].Sequence);
        Assert.Equal(2, (await _harness.StreamStateAsync("session_gen_1"))!.ActiveGeneration);
    }

    [Fact]
    public async Task ContextReset_IsEmittedOnlyFromASecondDurableBindingFact_AndCarriesOnlyTheSessionPayload()
    {
        var session = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_reset", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_reset", null)],
            turns: []);

        // The initial bind is a durable fact but not a reset.
        await _harness.SaveSessionAsync(session, [new AgentSessionRuntimeBound("runtime-original", null)]);
        await _harness.Engine.ProcessPendingAsync();
        Assert.DoesNotContain(PublicSessionEventTypes.ContextReset, (await _harness.EventsAsync("session_reset")).Select(row => row.Type));

        // A later durable binding replacement is the context boundary.
        var reset = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_reset", "proj_pub", "agent_pub"),
            AgentSessionActivity.Idle,
            inputs: [PublicProjectionTestSupport.Input("input_reset", null)],
            turns: []);
        reset.Runtime = reset.Runtime with { Runtime = "claude" };
        await _harness.SaveSessionAsync(reset, [new AgentSessionRuntimeBound("runtime-replacement", "claude")]);

        await _harness.Engine.ProcessPendingAsync();

        var resetEvents = (await _harness.EventsAsync("session_reset"))
            .Where(row => row.Type == PublicSessionEventTypes.ContextReset)
            .ToList();
        var resetEvent = Assert.Single(resetEvents);

        using var payload = JsonDocument.Parse(resetEvent.PayloadJson);
        var keys = payload.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.Equal(
            ["projectId", "agentId", "sessionId", "sessionActivity", "admission", "reasonCode"],
            keys);
        Assert.Equal("session_reset", payload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(PublicExecutionFieldValues.Reasons.ContextReset, payload.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task EventVocabulary_IsExactlyTheSevenExecutionTypesPlusContextReset()
    {
        await _harness.SeedJobAsync("job_vocab", "proj_pub", "agent_pub", "session_vocab", "input_vocab", "turn_vocab");
        var session = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_vocab", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_vocab", "job_vocab")],
            turns: [PublicProjectionTestSupport.Turn("turn_vocab", "input_vocab", "job_vocab", AgentTurnStatus.Queued)]);
        await _harness.SaveSessionAsync(session);
        await _harness.Engine.ProcessPendingAsync();

        var types = (await _harness.EventsAsync("session_vocab")).Select(row => row.Type).ToHashSet();
        Assert.All(types, type => Assert.Contains(type, PublicSessionEventTypes.All));
        Assert.Contains(PublicSessionEventTypes.InputAccepted, types);
        Assert.Contains(PublicSessionEventTypes.TurnQueued, types);

        // session.unknown and input.rejected are reachable from facts.
        var advanced = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_vocab", "proj_pub", "agent_pub"),
            AgentSessionActivity.Unknown,
            inputs: [PublicProjectionTestSupport.Input("input_vocab", "job_vocab")],
            turns: [PublicProjectionTestSupport.Turn("turn_vocab", "input_vocab", "job_vocab", AgentTurnStatus.Unknown, updatedAt: T0.AddMinutes(3))]);
        await _harness.SaveSessionAsync(advanced);
        await _harness.Engine.ProcessPendingAsync();
        types = (await _harness.EventsAsync("session_vocab")).Select(row => row.Type).ToHashSet();
        Assert.Contains(PublicSessionEventTypes.SessionUnknown, types);
    }

    [Fact]
    public async Task RetryableDispatchBlock_StaysQueuedWithBlockedAdmissionAndASafeReason()
    {
        await _harness.SeedJobAsync(
            "job_blocked",
            "proj_pub",
            "agent_pub",
            "session_blocked",
            "input_blocked",
            "turn_blocked",
            waitingReason: "capacity-full");
        await _harness.SaveSessionAsync(PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_blocked", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_blocked", "job_blocked")],
            turns: [PublicProjectionTestSupport.Turn("turn_blocked", "input_blocked", "job_blocked", AgentTurnStatus.Queued)]));

        await _harness.Engine.ProcessPendingAsync();

        var job = ParseSnapshot((await _harness.SnapshotAsync("job", "job_blocked"))!);
        Assert.Equal(PublicExecutionFieldValues.StatusQueued, job.Status);
        Assert.Equal(PublicExecutionFieldValues.AdmissionBlocked, job.Admission);
        Assert.Equal(PublicExecutionFieldValues.TurnQueued, job.TurnStatus);
        Assert.NotEqual(PublicExecutionFieldValues.StatusTerminal, job.Status);
    }

    [Fact]
    public async Task ProjectionDrivesNoCanonicalSideEffects()
    {
        await _harness.SeedJobAsync("job_inert", "proj_pub", "agent_pub", "session_inert", "input_inert", "turn_inert");
        var session = PublicProjectionTestSupport.WithFacts(
            _harness.BuildSession("session_inert", "proj_pub", "agent_pub"),
            AgentSessionActivity.Active,
            inputs: [PublicProjectionTestSupport.Input("input_inert", "job_inert")],
            turns: [PublicProjectionTestSupport.Turn("turn_inert", "input_inert", "job_inert", AgentTurnStatus.Executing)]);
        await _harness.SaveSessionAsync(session);
        var ledgerBefore = await _harness.JobStore.LoadLedgerAsync("job_inert");
        var sessionBefore = await _harness.SessionStore.LoadAsync("session_inert");

        for (var i = 0; i < 3; i++)
        {
            await _harness.Engine.ProcessPendingAsync();
        }

        var ledgerAfter = await _harness.JobStore.LoadLedgerAsync("job_inert");
        var sessionAfter = await _harness.SessionStore.LoadAsync("session_inert");
        Assert.Equal(ledgerBefore!.Revision, ledgerAfter!.Revision);
        Assert.Equal(ledgerBefore.StateJson, ledgerAfter.StateJson);
        Assert.Equal(
            JsonSerializer.Serialize(sessionBefore, AgentSessionJson.JsonOptions),
            JsonSerializer.Serialize(sessionAfter, AgentSessionJson.JsonOptions));
        Assert.Empty(await _harness.EventsAsync("session_inert", generation: 2));
    }

    private static PublicExecutionRead ParseSnapshot(PublicExecutionSnapshotRow row) =>
        JsonSerializer.Deserialize<PublicExecutionRead>(row.SnapshotJson, JSON.PublicApi)
        ?? throw new InvalidOperationException("The public snapshot was unreadable.");

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
