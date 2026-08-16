using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// The projection engine behind the public execution surface. It is the
/// only writer of the public projection tables: in one EF transaction
/// per batch it discovers canonical rows past the per-feed checkpoints,
/// recomputes the allowlisted public execution snapshots with their
/// terminal fences, appends new public Session journal entries (with
/// sequence allocation and replay-deduplicating source-transition
/// identities), advances the checkpoints, and keeps the stream
/// generation bookkeeping — so a crash before commit leaves nothing
/// partial and a crash after commit resumes exactly past the
/// checkpoint without emitting a second sequence for the same
/// normalized source transition.
/// <para>
/// Correctness is checkpoint-driven; write paths merely nudge the
/// hosted projector for latency. The engine never issues a Runner,
/// launch, follow-up, or stop effect — projection recovery replays
/// durable input, never domain effects.
/// </para>
/// </summary>
public sealed partial class PublicApiProjectionEngine
{
    /// <summary>Default cap on targets projected per transaction.</summary>
    public const int DefaultBatchTargetLimit = 25;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicApiProjectionEngine> _log;

    public PublicApiProjectionEngine(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider time,
        ILogger<PublicApiProjectionEngine> log)
    {
        _dbFactory = dbFactory;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Runs one projection sweep: discovers canonical rows past the
    /// checkpoints and projects up to <paramref name="targetLimit"/>
    /// targets in a single transaction. Returns true when a target was
    /// projected (more may remain); false when the projection is
    /// caught up.
    /// </summary>
    /// <param name="commit">
    /// Test seam: when false, the transaction is deliberately rolled
    /// back to simulate a crash before the projection commit — no
    /// snapshot, journal entry, sequence, or checkpoint becomes
    /// visible.
    /// </param>
    public async Task<bool> ProcessPendingAsync(
        CancellationToken ct = default,
        bool commit = true,
        int targetLimit = DefaultBatchTargetLimit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var projected = await ProjectBatchAsync(db, targetLimit, rebuildGeneration: null, onlySession: null, ct);
            if (projected == 0)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            if (commit)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            return true;
        }
        catch
        {
            await SafeRollbackAsync(transaction, ct);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds one Session's public stream in a new generation and
    /// atomically makes that generation active, preserving the
    /// Session's global next-sequence allocator so sequences are never
    /// reused or renumbered when the active generation changes. The
    /// previous generation's journal rows are never mutated; they stay
    /// retained. Returns the new active generation.
    /// </summary>
    public async Task<long> RebuildSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var existingStream = await db.PublicStreamStates.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
            if (existingStream is null)
            {
                throw new InvalidOperationException(
                    $"Cannot rebuild the public stream of AgentSession {sessionId}: no stream state exists.");
            }

            var newGeneration = existingStream.ActiveGeneration + 1;
            var projected = await ProjectBatchAsync(
                db,
                targetLimit: 1,
                rebuildGeneration: newGeneration,
                onlySession: sessionId,
                ct);
            if (projected == 0)
            {
                throw new InvalidOperationException(
                    $"Cannot rebuild the public stream of AgentSession {sessionId}: no canonical facts were found.");
            }

            var stream = await db.PublicStreamStates.FirstAsync(s => s.SessionId == sessionId, ct);
            // The new generation's rows are staged in this transaction;
            // read them from the change tracker so the flip and the
            // bounds commit atomically with the journal itself.
            var newGenerationRows = db.PublicSessionEvents.Local
                .Where(e => e.SessionId == sessionId && e.Generation == newGeneration)
                .OrderBy(e => e.Sequence)
                .ToList();
            stream.ActiveGeneration = newGeneration;
            stream.EarliestSequence = newGenerationRows.FirstOrDefault()?.Sequence;
            stream.LatestSequence = newGenerationRows.LastOrDefault()?.Sequence;
            stream.UpdatedAt = _time.GetUtcNow();

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return newGeneration;
        }
        catch
        {
            await SafeRollbackAsync(transaction, ct);
            throw;
        }
    }

    private async Task<int> ProjectBatchAsync(
        MohistDbContext db,
        int targetLimit,
        long? rebuildGeneration,
        string? onlySession,
        CancellationToken ct)
    {
        var checkpointRows = await LoadCheckpointRowsAsync(db, ct);
        var checkpoints = checkpointRows.ToDictionary(
            row => CheckpointKey(row.Feed, row.SourceKey),
            row => row.Watermark,
            StringComparer.Ordinal);
        var observedAt = _time.GetUtcNow();

        // --- discovery: canonical rows past the checkpoints ---
        var jobRows = await db.AgentJobs.AsNoTracking().ToListAsync(ct);
        var dirtyJobKeys = new List<string>();
        foreach (var jobRow in jobRows)
        {
            if (!checkpoints.TryGetValue(CheckpointKey(PublicProjectionFeeds.AgentJobs, jobRow.JobKey), out var watermark)
                || !string.Equals(watermark, RevisionWatermark(jobRow.Revision), StringComparison.Ordinal))
            {
                dirtyJobKeys.Add(jobRow.JobKey);
            }
        }

        var sessionRows = await db.AgentSessions.AsNoTracking().ToListAsync(ct);
        var sessionIds = sessionRows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var sessionById = sessionRows.ToDictionary(row => row.Id, StringComparer.Ordinal);

        // A rebuild re-derives its new generation from the durable
        // canonical inputs regardless of the checkpoint position, so
        // the rebuild target is always in scope.
        var rebuilding = rebuildGeneration is not null && onlySession is not null;
        var dirtySessionIds = new List<string>();
        foreach (var row in sessionRows)
        {
            if (onlySession is not null && !string.Equals(row.Id, onlySession, StringComparison.Ordinal))
            {
                continue;
            }

            if (rebuilding)
            {
                dirtySessionIds.Add(row.Id);
                continue;
            }

            var digest = PublicExecutionAggregator.StateDigest(row.State);
            if (!checkpoints.TryGetValue(CheckpointKey(PublicProjectionFeeds.AgentSessions, row.Id), out var watermark)
                || !string.Equals(watermark, digest, StringComparison.Ordinal))
            {
                dirtySessionIds.Add(row.Id);
            }
        }

        var dirtyJobJournalSources = new List<string>();
        foreach (var head in await LoadJournalHeadsAsync(db.AgentJobEvents.AsNoTracking(), ct))
        {
            if (IsJournalBehind(checkpoints, PublicProjectionFeeds.AgentJobEvents, head.Source, head.MaxId))
            {
                dirtyJobJournalSources.Add(head.Source);
            }
        }

        var dirtySessionJournalSources = new List<string>();
        foreach (var head in await LoadJournalHeadsAsync(db.AgentSessionEvents.AsNoTracking(), ct))
        {
            if (IsJournalBehind(checkpoints, PublicProjectionFeeds.AgentSessionEvents, head.Source, head.MaxId))
            {
                dirtySessionJournalSources.Add(head.Source);
            }
        }

        // --- target resolution ---
        // A dirty Job whose Session ledger row exists projects as part
        // of that Session target (the Session owns the public stream);
        // a Job without a live Session row projects on its own Job
        // anchor (a prepared launch).
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var jobKey in dirtyJobKeys)
        {
            AddJobTarget(targets, jobRows, sessionIds, jobKey, onlySession);
        }

        foreach (var source in dirtyJobJournalSources)
        {
            var jobKey = SourceId(source);
            if (jobKey is not null)
            {
                AddJobTarget(targets, jobRows, sessionIds, jobKey, onlySession);
            }
        }

        foreach (var sessionId in dirtySessionIds)
        {
            targets.Add("session:" + sessionId);
        }

        foreach (var source in dirtySessionJournalSources)
        {
            if (SourceId(source) is { } sessionId
                && (onlySession is null || string.Equals(sessionId, onlySession, StringComparison.Ordinal)))
            {
                if (sessionIds.Contains(sessionId) || onlySession is not null)
                {
                    targets.Add("session:" + sessionId);
                }
            }
        }

        if (targets.Count == 0)
        {
            return 0;
        }

        var projectedCount = 0;
        foreach (var target in targets.Take(targetLimit))
        {
            var separator = target.IndexOf(':');
            var kind = target[..separator];
            var id = target[(separator + 1)..];
            var projected = kind == "session"
                ? await ProjectSessionAsync(db, id, sessionById, checkpoints, observedAt, rebuildGeneration, ct)
                : await ProjectPreparedJobAsync(db, id, checkpoints, observedAt, ct);
            if (projected)
            {
                projectedCount++;
            }
        }

        if (projectedCount > 0)
        {
            StageCheckpoints(db, checkpointRows, checkpoints, observedAt);
        }

        return projectedCount;
    }

    private static void AddJobTarget(
        HashSet<string> targets,
        IReadOnlyList<AgentJobRow> jobRows,
        HashSet<string> sessionIds,
        string jobKey,
        string? onlySession)
    {
        var jobRow = jobRows.FirstOrDefault(row => string.Equals(row.JobKey, jobKey, StringComparison.Ordinal));
        if (jobRow?.AgentSessionId is { } sessionId && sessionIds.Contains(sessionId))
        {
            if (onlySession is null || string.Equals(sessionId, onlySession, StringComparison.Ordinal))
            {
                targets.Add("session:" + sessionId);
            }

            return;
        }

        if (onlySession is null)
        {
            targets.Add("job:" + jobKey);
        }
    }

    private async Task<bool> ProjectSessionAsync(
        MohistDbContext db,
        string sessionId,
        IReadOnlyDictionary<string, AgentSessionRow> sessionById,
        Dictionary<string, string> checkpoints,
        DateTimeOffset observedAt,
        long? rebuildGeneration,
        CancellationToken ct)
    {
        if (!sessionById.TryGetValue(sessionId, out var row))
        {
            return false;
        }

        var session = AgentSessionJson.Deserialize(row);
        if (session is null)
        {
            _log.LogWarning(
                "Public projection skipped AgentSession {SessionId}: the ledger state could not be deserialized",
                sessionId);
            AdvanceSessionCheckpoints(checkpoints, row, [], [], new Dictionary<string, long>());
            return true;
        }

        var jobRows = await db.AgentJobs.AsNoTracking()
            .Where(j => j.AgentSessionId == sessionId)
            .ToListAsync(ct);
        var journalRows = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => e.Source == AgentSessionSource(sessionId))
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        var jobJournalHeads = await LoadJobJournalHeadsAsync(db, jobRows, ct);

        var facts = BuildFacts(sessionId, row, session, jobRows, journalRows);
        // A target without a public Project identity cannot be served on
        // the public boundary; its facts are consumed and checkpointed,
        // but nothing is published.
        if (string.IsNullOrWhiteSpace(facts.ProjectId))
        {
            AdvanceSessionCheckpoints(checkpoints, row, jobRows, journalRows, jobJournalHeads);
            return true;
        }

        // --- stream state: generation one on first commit ---
        var stream = db.PublicStreamStates.Local.FirstOrDefault(s => s.SessionId == sessionId);
        if (stream is null)
        {
            stream = await db.PublicStreamStates.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        }

        var created = false;
        if (stream is null)
        {
            stream = new PublicStreamStateRow
            {
                SessionId = sessionId,
                ActiveGeneration = 1,
                NextSequence = 1,
                CreatedAt = observedAt,
            };
            db.PublicStreamStates.Add(stream);
            created = true;
        }

        // A rebuild never mutates the live journal in place: it writes a
        // fresh generation (stale rows of an unfinished rebuild attempt
        // are discarded first) and the caller flips the active
        // generation in the same commit.
        var targetGeneration = rebuildGeneration ?? stream.ActiveGeneration;
        var existingEvents = rebuildGeneration is null
            ? await db.PublicSessionEvents
                .Where(e => e.SessionId == sessionId && e.Generation == targetGeneration)
                .ToListAsync(ct)
            : [];
        var existingTransitions = existingEvents
            .Select(e => e.SourceTransition)
            .ToHashSet(StringComparer.Ordinal);

        // --- terminal fences from the anchor snapshots ---
        // A prepared Job anchor is committed with a null SessionId
        // before this Session's row exists, so the fence load must
        // reach those anchors by Job key as well — otherwise a fence
        // set in the prepared phase would be invisible here.
        var sessionJobKeys = jobRows.Select(job => job.JobKey).ToList();
        var anchorRows = await db.PublicExecutionSnapshots
            .Where(s => s.SessionId == sessionId
                || (s.AnchorType == "job" && sessionJobKeys.Contains(s.AnchorId)))
            .ToListAsync(ct);
        var fences = new Dictionary<string, PublicExecutionSnapshotRow>(StringComparer.Ordinal);
        foreach (var anchorRow in anchorRows)
        {
            if (anchorRow.TerminalFact is not null)
            {
                fences[AnchorKey(anchorRow.AnchorType, anchorRow.AnchorId)] = anchorRow;
            }
        }

        // --- desired transitions, from consumed facts only ---
        var transitions = PublicExecutionAggregator.DeriveTransitions(facts, observedAt);
        var newEvents = new List<PublicSessionEventRow>();
        foreach (var transition in transitions)
        {
            if (existingTransitions.Contains(transition.Identity))
            {
                continue;
            }

            // Terminal fence: once an anchor holds a winning terminal
            // fact, a different terminal fact for the same anchor is
            // dropped — at most one terminal public event, and the
            // fenced outcome is never replaced.
            if (rebuildGeneration is null
                && IsTerminalTransition(transition.EventType)
                && fences.TryGetValue(AnchorKey(transition.AnchorKind, transition.AnchorId), out _))
            {
                continue;
            }

            var sequence = stream.NextSequence;
            stream.NextSequence = checked(stream.NextSequence + 1);
            var payload = BuildEventPayload(facts, transition, observedAt, sequence);
            if (payload is null)
            {
                // The transition's anchor has no public payload in this
                // state; the allocated sequence stays reserved (never
                // reused) and the transition is simply not published.
                continue;
            }

            var journalRow = new PublicSessionEventRow
            {
                SessionId = sessionId,
                Generation = targetGeneration,
                Sequence = sequence,
                Type = transition.EventType,
                OccurredAt = FormatTimestamp(transition.OccurredAt),
                PayloadJson = payload,
                SourceTransition = transition.Identity,
                RecordedAt = observedAt,
            };
            db.PublicSessionEvents.Add(journalRow);
            existingTransitions.Add(transition.Identity);
            newEvents.Add(journalRow);
        }

        long? latestSequence = newEvents.Count > 0
            ? newEvents[^1].Sequence
            : existingEvents.Count > 0 ? existingEvents.Max(e => e.Sequence) : stream.LatestSequence;
        if (newEvents.Count > 0 && rebuildGeneration is null)
        {
            stream.LatestSequence = latestSequence;
            stream.EarliestSequence ??= newEvents[0].Sequence;
        }

        // --- snapshot upserts behind the same fences ---
        UpsertAnchors(db, facts, observedAt, latestSequence, fences);

        if (created || newEvents.Count > 0)
        {
            stream.UpdatedAt = observedAt;
        }

        AdvanceSessionCheckpoints(checkpoints, row, jobRows, journalRows, jobJournalHeads);
        return true;
    }

    private async Task<bool> ProjectPreparedJobAsync(
        MohistDbContext db,
        string jobKey,
        Dictionary<string, string> checkpoints,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        var jobRow = await db.AgentJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobKey == jobKey, ct);
        if (jobRow is null)
        {
            return false;
        }

        var state = TryDeserializeJobState(jobRow.State);
        if (state is null)
        {
            _log.LogWarning(
                "Public projection skipped AgentJob {JobKey}: the ledger state could not be deserialized",
                jobKey);
            AdvanceCheckpoint(
                checkpoints,
                PublicProjectionFeeds.AgentJobs,
                jobKey,
                RevisionWatermark(jobRow.Revision));
            return true;
        }

        var facts = new PublicProjectionFacts
        {
            SessionId = string.Empty,
            ProjectId = state.Input?.ProjectId ?? jobRow.ProjectId,
            AgentId = state.Input?.AgentId ?? jobRow.AgentId,
        };

        if (string.IsNullOrWhiteSpace(facts.ProjectId))
        {
            // No public identity: consume and checkpoint, publish nothing.
            AdvanceCheckpoint(
                checkpoints,
                PublicProjectionFeeds.AgentJobs,
                jobKey,
                RevisionWatermark(jobRow.Revision));
            return true;
        }

        var jobFacts = ToJobFacts(jobRow, state);
        var components = PublicExecutionAggregator.ApplyTerminal(
            PublicExecutionAggregator.BuildJobAnchor(facts, jobFacts),
            facts);
        var status = PublicExecutionAggregator.ComputeStatus(components, sessionExists: false);
        var snapshotJson = SerializeSnapshot(facts.ProjectId!, facts.AgentId, components, status, observedAt, sequence: null);

        var existing = db.PublicExecutionSnapshots.Local.FirstOrDefault(
            s => s.AnchorType == "job" && s.AnchorId == jobKey);
        if (existing is null)
        {
            existing = await db.PublicExecutionSnapshots
                .FirstOrDefaultAsync(s => s.AnchorType == "job" && s.AnchorId == jobKey, ct);
        }

        if (existing is null)
        {
            db.PublicExecutionSnapshots.Add(new PublicExecutionSnapshotRow
            {
                AnchorType = "job",
                AnchorId = jobKey,
                ProjectId = facts.ProjectId!,
                AgentId = facts.AgentId,
                SessionId = null,
                SnapshotJson = snapshotJson,
                TerminalFact = components.TerminalFact,
                TerminalOutcome = components.Outcome,
                TerminalAt = FormatNullableTimestamp(components.TerminalAt),
                TerminalSequence = null,
                LastSequence = null,
                UpdatedAt = observedAt,
            });
        }
        else if (existing.TerminalFact is null)
        {
            existing.SnapshotJson = snapshotJson;
            existing.ProjectId = facts.ProjectId!;
            existing.AgentId = facts.AgentId;
            existing.TerminalFact = components.TerminalFact;
            existing.TerminalOutcome = components.Outcome;
            existing.TerminalAt = FormatNullableTimestamp(components.TerminalAt);
            existing.UpdatedAt = observedAt;
        }
        else
        {
            // Fence: a fenced prepared Job keeps its frozen terminal
            // projection; only the observation time advances.
            existing.UpdatedAt = observedAt;
        }

        AdvanceCheckpoint(
            checkpoints,
            PublicProjectionFeeds.AgentJobs,
            jobKey,
            RevisionWatermark(jobRow.Revision));
        var journalHead = await db.AgentJobEvents.AsNoTracking()
            .Where(e => e.Source == AgentJobSource(jobKey))
            .MaxAsync(e => (long?)e.Id, ct);
        if (journalHead is not null)
        {
            AdvanceCheckpoint(
                checkpoints,
                PublicProjectionFeeds.AgentJobEvents,
                AgentJobSource(jobKey),
                journalHead.Value.ToString());
        }

        return true;
    }

    private void UpsertAnchors(
        MohistDbContext db,
        PublicProjectionFacts facts,
        DateTimeOffset observedAt,
        long? latestSequence,
        Dictionary<string, PublicExecutionSnapshotRow> fences)
    {
        foreach (var job in facts.Jobs)
        {
            var components = PublicExecutionAggregator.ApplyTerminal(
                PublicExecutionAggregator.BuildJobAnchor(facts, job),
                facts);
            UpsertAnchor(
                db,
                "job",
                job.JobKey,
                facts,
                components,
                observedAt,
                latestSequence,
                fences);
        }

        foreach (var input in facts.Inputs)
        {
            var components = PublicExecutionAggregator.ApplyTerminal(
                PublicExecutionAggregator.BuildInputAnchor(facts, input),
                facts);
            UpsertAnchor(
                db,
                "input",
                input.InputId,
                facts,
                components,
                observedAt,
                latestSequence,
                fences);
        }

        foreach (var turn in facts.Turns)
        {
            var components = PublicExecutionAggregator.ApplyTerminal(
                PublicExecutionAggregator.BuildTurnAnchor(facts, turn),
                facts);
            UpsertAnchor(
                db,
                "turn",
                turn.TurnId,
                facts,
                components,
                observedAt,
                latestSequence,
                fences);
        }
    }

    private void UpsertAnchor(
        MohistDbContext db,
        string anchorType,
        string anchorId,
        PublicProjectionFacts facts,
        PublicAnchorComponents components,
        DateTimeOffset observedAt,
        long? latestSequence,
        Dictionary<string, PublicExecutionSnapshotRow> fences)
    {
        var status = PublicExecutionAggregator.ComputeStatus(components, sessionExists: true);
        var existing = db.PublicExecutionSnapshots.Local.FirstOrDefault(
            s => s.AnchorType == anchorType && s.AnchorId == anchorId);
        if (existing is null)
        {
            db.PublicExecutionSnapshots.Add(new PublicExecutionSnapshotRow
            {
                AnchorType = anchorType,
                AnchorId = anchorId,
                ProjectId = facts.ProjectId!,
                AgentId = facts.AgentId,
                SessionId = facts.SessionId,
                SnapshotJson = SerializeSnapshot(facts.ProjectId!, facts.AgentId, components, status, observedAt, latestSequence),
                TerminalFact = components.TerminalFact,
                TerminalOutcome = components.Outcome,
                TerminalAt = FormatNullableTimestamp(components.TerminalAt),
                TerminalSequence = null,
                LastSequence = latestSequence,
                UpdatedAt = observedAt,
            });
            return;
        }

        if (existing.TerminalFact is not null)
        {
            // The fence holds: the winning terminal fact keeps its
            // outcome, output, error, and sequence, and the anchor
            // never reverts to a non-terminal public state. Only the
            // observation timestamp advances.
            existing.SessionId ??= facts.SessionId;
            existing.UpdatedAt = observedAt;
            return;
        }

        existing.SnapshotJson = SerializeSnapshot(facts.ProjectId!, facts.AgentId, components, status, observedAt, latestSequence);
        existing.ProjectId = facts.ProjectId!;
        existing.AgentId = facts.AgentId;
        existing.SessionId = facts.SessionId;
        existing.TerminalFact = components.TerminalFact;
        existing.TerminalOutcome = components.Outcome;
        existing.TerminalAt = FormatNullableTimestamp(components.TerminalAt);
        existing.TerminalSequence = null;
        existing.LastSequence = latestSequence;
        existing.UpdatedAt = observedAt;
    }

    private string SerializeSnapshot(
        string projectId,
        string? agentId,
        PublicAnchorComponents components,
        string status,
        DateTimeOffset observedAt,
        long? sequence) =>
        JsonSerializer.Serialize(
            ToDto(projectId, agentId, components, status, observedAt, sequence),
            JSON.PublicApi);

    private string? BuildEventPayload(
        PublicProjectionFacts facts,
        PublicExecutionAggregator.PublicSourceTransition transition,
        DateTimeOffset observedAt,
        long sequence)
    {
        if (transition.EventType == PublicSessionEventTypes.ContextReset)
        {
            var sessionComponents = PublicExecutionAggregator.BuildSessionAnchor(facts);
            var payload = new PublicSessionEventPayload
            {
                ProjectId = facts.ProjectId!,
                AgentId = facts.AgentId,
                SessionId = facts.SessionId,
                SessionActivity = sessionComponents.SessionActivity,
                Admission = sessionComponents.Admission,
                ReasonCode = PublicExecutionFieldValues.Reasons.ContextReset,
            };
            return JsonSerializer.Serialize(payload, JSON.PublicApi);
        }

        var components = transition.AnchorKind switch
        {
            PublicExecutionAggregator.PublicAnchorKind.Input
                when PublicExecutionAggregator.FindInput(facts, transition.AnchorId) is { } input
                => PublicExecutionAggregator.BuildInputAnchor(facts, input),
            PublicExecutionAggregator.PublicAnchorKind.Turn
                when PublicExecutionAggregator.FindTurn(facts, transition.AnchorId) is { } turn
                => PublicExecutionAggregator.BuildTurnAnchor(facts, turn),
            _ => PublicExecutionAggregator.BuildSessionAnchor(facts),
        };
        components = PublicExecutionAggregator.ApplyTerminal(components, facts);
        var status = PublicExecutionAggregator.ComputeStatus(components, sessionExists: true);
        var dto = ToDto(facts.ProjectId!, facts.AgentId, components, status, observedAt, sequence);
        return JsonSerializer.Serialize(dto, JSON.PublicApi);
    }

    private static PublicExecutionRead ToDto(
        string projectId,
        string? agentId,
        PublicAnchorComponents components,
        string status,
        DateTimeOffset observedAt,
        long? sequence) => new()
    {
        ProjectId = projectId,
        AgentId = agentId,
        JobId = components.JobId,
        SessionId = components.SessionId,
        InputId = components.InputId,
        TurnId = components.TurnId,
        Status = status,
        JobStatus = components.JobStatus,
        SessionActivity = components.SessionActivity,
        Admission = components.Admission,
        InputStatus = components.InputStatus,
        TurnStatus = components.TurnStatus,
        Outcome = components.Outcome,
        ReasonCode = components.ReasonCode ?? PublicExecutionAggregator.ResolveReasonCode(components),
        Output = components.Output,
        Error = components.Error,
        AcceptedAt = components.AcceptedAt,
        QueuedAt = components.QueuedAt,
        StartedAt = components.StartedAt,
        TerminalAt = components.TerminalAt,
        ObservedAt = observedAt,
        Sequence = sequence,
    };

    // --- facts extraction ---

    private static PublicProjectionFacts BuildFacts(
        string sessionId,
        AgentSessionRow row,
        AgentSession session,
        IReadOnlyList<AgentJobRow> jobRows,
        IReadOnlyList<AgentSessionEventRow> journalRows)
    {
        var status = session.Status;
        return new PublicProjectionFacts
        {
            SessionId = sessionId,
            ProjectId = row.LabelProjectId,
            AgentId = row.LabelAgentId,
            Activity = status.Activity,
            SessionCreatedAt = ToUtc(status.CreatedAt),
            PendingStopActive = status.PendingStop?.IsActive == true,
            PendingResetActive = status.PendingReset is not null && status.PendingReset.Outcome is null,
            Jobs = jobRows
                .Select(jobRow => ToJobFacts(jobRow, TryDeserializeJobState(jobRow.State) ?? new AgentJobState()))
                .ToList(),
            Inputs = (status.Inputs ?? [])
                .Select(input => new PublicProjectionFacts.InputFacts(
                    input.Id,
                    input.Acceptance,
                    ToUtc(input.RecordedAt),
                    input.JobId))
                .ToList(),
            Turns = (status.Turns ?? [])
                .Select(turn => new PublicProjectionFacts.TurnFacts(
                    turn.Id,
                    turn.Status,
                    turn.InputIds ?? [],
                    turn.JobId,
                    ToUtc(turn.RecordedAt),
                    ToUtc(turn.UpdatedAt),
                    turn.Result))
                .ToList(),
            SessionJournal = journalRows
                .Select(journal => new PublicProjectionFacts.SessionJournalFacts(
                    journal.Id,
                    journal.Type,
                    journal.Time))
                .ToList(),
        };
    }

    private static PublicProjectionFacts.JobFacts ToJobFacts(AgentJobRow row, AgentJobState state) => new(
        row.JobKey,
        state.Status,
        state.Input?.ProjectId ?? row.ProjectId,
        state.Input?.AgentId ?? row.AgentId,
        row.AgentSessionId,
        row.InitialInputId,
        row.InitialTurnId,
        state.SubmittedAt,
        ParseTimestamp(row.ReadySince),
        state.RunningSince,
        state.TerminalAt,
        state.WaitingReason,
        state.TerminalResult);

    private static AgentJobState? TryDeserializeJobState(string stateJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentJobState>(stateJson, JSON.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is null
            ? null
            : new DateTimeOffset(value.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : value.Value.ToUniversalTime());

    private static DateTimeOffset ToUtc(DateTime value) => ToUtc((DateTime?)value)!.Value;

    internal static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static string? FormatNullableTimestamp(DateTimeOffset? value) =>
        value is null ? null : FormatTimestamp(value.Value);

    private static string AgentSessionSource(string sessionId) =>
        AgentSessionEventPersistence.AgentSessionSource(sessionId);

    private static string AgentJobSource(string jobKey) =>
        AgentJobEventPersistence.AgentJobSource(jobKey);

    private static string RevisionWatermark(long revision) => revision.ToString();

    private static string CheckpointKey(string feed, string sourceKey) => feed + "\u001f" + sourceKey;

    private static string? SourceId(string source)
    {
        var separator = source.LastIndexOf('/');
        return separator >= 0 && separator + 1 < source.Length ? source[(separator + 1)..] : null;
    }

    private static bool IsTerminalTransition(string eventType) =>
        eventType == PublicSessionEventTypes.TurnTerminal
        || eventType == PublicSessionEventTypes.InputRejected;

    private static string AnchorKey(string anchorType, string anchorId) => anchorType + ":" + anchorId;

    private static string AnchorKey(PublicExecutionAggregator.PublicAnchorKind kind, string anchorId) => kind switch
    {
        PublicExecutionAggregator.PublicAnchorKind.Session => "session:" + anchorId,
        PublicExecutionAggregator.PublicAnchorKind.Input => "input:" + anchorId,
        PublicExecutionAggregator.PublicAnchorKind.Turn => "turn:" + anchorId,
        _ => anchorId,
    };

}
