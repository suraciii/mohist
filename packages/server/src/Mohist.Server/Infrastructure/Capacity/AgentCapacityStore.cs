using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Infrastructure.Capacity;

/// <summary>
/// Derives Agent capacity from the two owner stores and conditionally writes
/// one owner claim. It persists no capacity ledger of its own.
/// </summary>
public sealed class AgentCapacityStore : IAgentCapacityStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public AgentCapacityStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyDictionary<string, AgentCapacitySnapshot>> ReadAsync(
        string projectId,
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = agentIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, AgentCapacitySnapshot>(StringComparer.Ordinal);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var snapshots = await ReadAsync(db, projectId, ids, ct);
        return await MarkUnattributableSessionsAsync(db, projectId, snapshots, ct);
    }

    public async Task<AgentJobCapacityClaimResult> ClaimJobAsync(
        string jobKey,
        long expectedRevision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await BeginImmediateAsync(db, ct);

        var row = await db.AgentJobs.FirstOrDefaultAsync(candidate => candidate.JobKey == jobKey, ct);
        if (row is null)
            return new(AgentCapacityClaimDisposition.Conflict, null);
        if (!TryJob(row.State, out var job))
            return new(AgentCapacityClaimDisposition.Incomplete, null);

        var projectId = job.Input?.ProjectId;
        var agentId = job.Input?.AgentId;
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(agentId))
            return new(AgentCapacityClaimDisposition.Incomplete, null);

        var capacity = (await ReadAsync(db, projectId, [agentId], ct))[agentId];
        if (job.CapacityClaimedAt is not null)
        {
            await transaction.CommitAsync(ct);
            return new(
                AgentCapacityClaimDisposition.AlreadyClaimed,
                capacity,
                AgentJobStore.ToRecord(row));
        }
        if (row.Revision != expectedRevision)
            return new(AgentCapacityClaimDisposition.Conflict, capacity);
        if (!AgentCapacityFacts.IsEligible(job))
            return new(AgentCapacityClaimDisposition.NotEligible, capacity);
        if (job.Input?.AgentSessionId is { } referencedSessionId)
        {
            // The Job's own initial Turn must be its Session's current
            // deliverable head, evaluated against the persisted Session row
            // inside the same claim transaction. A locally ineligible Job
            // fails closed as incomplete or not-eligible evidence, never as
            // an unlimited or full-capacity conclusion.
            var sessionRow = await db.AgentSessions.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == referencedSessionId, ct);
            var localOrder = AgentCapacityFacts.EvaluateJobLocalOrder(
                sessionRow is null ? null : AgentSessionJson.Deserialize(sessionRow),
                job,
                row.JobKey);
            if (localOrder != AgentCapacityClaimDisposition.Claimed)
                return new(localOrder, capacity);
        }

        var admission = Admission(capacity, AgentCapacityFacts.JobEntry(row.JobKey, job));
        if (admission != AgentCapacityClaimDisposition.Claimed)
            return new(admission, capacity);

        var now = _timeProvider.GetUtcNow();
        job.CapacityClaimedAt = now;
        job.ReadySince = now;
        row.State = JSON.Serialize(job);
        row.ReadySince = AgentJobStore.FormatTimestamp(now);
        row.Revision++;
        AgentJobStore.StageDirectApiProjection(row, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(
            AgentCapacityClaimDisposition.Claimed,
            capacity,
            AgentJobStore.ToRecord(row));
    }

    public async Task<AgentTurnCapacityClaimResult> ClaimTurnAsync(
        string sessionId,
        string expectedStateJson,
        string turnId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(expectedStateJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await BeginImmediateAsync(db, ct);
        var row = await db.AgentSessions.FirstOrDefaultAsync(candidate => candidate.Id == sessionId, ct);
        if (row is null || !string.Equals(row.State, expectedStateJson, StringComparison.Ordinal))
            return new(AgentCapacityClaimDisposition.Conflict, null);
        var session = AgentSessionJson.Deserialize(row);
        if (session is null)
            return new(AgentCapacityClaimDisposition.Incomplete, null);

        var projectId = AgentCapacityFacts.ProjectId(session);
        var agentId = AgentCapacityFacts.AgentId(session);
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(agentId))
            return new(AgentCapacityClaimDisposition.Incomplete, null);
        var capacity = (await ReadAsync(db, projectId, [agentId], ct))[agentId];

        var turns = (session.Status.Turns ?? []).ToArray();
        var index = Array.FindIndex(turns, turn => string.Equals(turn.Id, turnId, StringComparison.Ordinal));
        if (index < 0)
            return new(AgentCapacityClaimDisposition.NotEligible, capacity);
        if (turns[index].CapacityClaimedAt is not null)
        {
            await transaction.CommitAsync(ct);
            return new(AgentCapacityClaimDisposition.AlreadyClaimed, capacity, session);
        }
        var head = AgentCapacityFacts.EligibleHead(session);
        if (head is null || !string.Equals(head.Id, turnId, StringComparison.Ordinal))
            return new(AgentCapacityClaimDisposition.NotEligible, capacity);

        AgentCapacityQueueEntry target;
        try
        {
            target = AgentCapacityFacts.TurnEntry(session, head);
        }
        catch (InvalidOperationException)
        {
            return new(AgentCapacityClaimDisposition.Incomplete, capacity);
        }
        var admission = Admission(capacity, target);
        if (admission != AgentCapacityClaimDisposition.Claimed)
            return new(admission, capacity);

        turns[index] = turns[index] with { CapacityClaimedAt = _timeProvider.GetUtcNow() };
        session.Status = session.Status with { Turns = turns };
        var nextState = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "AgentSessions"
            SET "State" = {nextState}
            WHERE "Id" = {sessionId}
              AND "State" = {expectedStateJson};
            """, ct);
        if (affected != 1)
            return new(AgentCapacityClaimDisposition.Conflict, capacity);

        db.ChangeTracker.Clear();
        var committedRow = await db.AgentSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sessionId, ct);
        var committed = AgentSessionJson.Deserialize(committedRow);
        if (committed is null)
            throw new InvalidOperationException(
                $"AgentSession {sessionId} capacity claim committed an unreadable owner document.");
        await transaction.CommitAsync(ct);
        return new(AgentCapacityClaimDisposition.Claimed, capacity, committed);
    }

    private static AgentCapacityClaimDisposition Admission(
        AgentCapacitySnapshot capacity,
        AgentCapacityQueueEntry target)
    {
        if (!capacity.IsComplete || capacity.Occupied is null)
            return AgentCapacityClaimDisposition.Incomplete;
        if (capacity.MaxConcurrentRuns is { } limit && capacity.Occupied >= limit)
            return AgentCapacityClaimDisposition.CapacityFull;
        if (capacity.IsUnlimited)
            return AgentCapacityClaimDisposition.Claimed;
        var first = capacity.Eligible.FirstOrDefault();
        return first == target
            ? AgentCapacityClaimDisposition.Claimed
            : AgentCapacityClaimDisposition.NotInOrder;
    }

    private async Task<IReadOnlyDictionary<string, AgentCapacitySnapshot>> ReadAsync(
        MohistDbContext db,
        string projectId,
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct)
    {
        var ids = agentIds.ToArray();
        var definitions = await ReadDefinitionsAsync(db, projectId, ids, ct);
        var jobs = await db.AgentJobs.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.AgentId != null
                && ids.Contains(row.AgentId)
                && (row.Status == null
                    || row.Status != "completed"
                        && row.Status != "failed"
                        && row.Status != "cancelled"))
            .ToListAsync(ct);
        var parsedJobs = jobs
            .Select(row => (Row: row, Job: TryJob(row.State, out var job) ? job : null))
            .ToList();
        var sessionRows = await db.AgentSessions.AsNoTracking()
            .Where(row => row.LabelProjectId == projectId
                && row.LabelAgentId != null
                && ids.Contains(row.LabelAgentId))
            .ToListAsync(ct);
        var sessions = new Dictionary<string, AgentSession?>(StringComparer.Ordinal);
        foreach (var row in sessionRows)
            sessions[row.Id] = AgentSessionJson.Deserialize(row);

        // Job-referenced Sessions are batch-loaded by id and the already
        // loaded rows are reused, so a referenced Session whose labels would
        // keep it invisible to the label filter still appears as concrete
        // incomplete evidence instead of silently reading as no Session.
        var referencedIds = parsedJobs
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Job?.Input?.AgentSessionId))
            .Select(entry => entry.Job!.Input!.AgentSessionId!)
            .Where(id => !sessions.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (referencedIds.Length > 0)
        {
            var referencedRows = await db.AgentSessions.AsNoTracking()
                .Where(row => referencedIds.Contains(row.Id))
                .ToListAsync(ct);
            foreach (var row in referencedRows)
                sessions[row.Id] = AgentSessionJson.Deserialize(row);
        }

        var snapshots = new Dictionary<string, AgentCapacitySnapshot>(StringComparer.Ordinal);
        foreach (var agentId in ids)
        {
            var definition = definitions[agentId];
            var evidence = definition.Status;
            var occupied = 0;
            var eligible = new List<AgentCapacityQueueEntry>();
            var queued = new List<AgentCapacityQueueEntry>();

            foreach (var (row, job) in parsedJobs.Where(entry =>
                string.Equals(entry.Row.AgentId, agentId, StringComparison.Ordinal)))
            {
                if (row.Status is not ("pending" or "running" or "unknown") || job is null)
                {
                    evidence = Incomplete(evidence);
                    continue;
                }
                var locallyEligible = true;
                if (job.Input?.AgentSessionId is not null)
                {
                    // A missing row leaves the lookup null: absent evidence,
                    // never an unconstrained Job.
                    sessions.TryGetValue(job.Input.AgentSessionId, out var referenced);
                    var localOrder = AgentCapacityFacts.EvaluateJobLocalOrder(referenced, job, row.JobKey);
                    if (localOrder == AgentCapacityClaimDisposition.Incomplete)
                    {
                        evidence = Incomplete(evidence);
                        continue;
                    }
                    // A definitively wrong reference or a Turn ordered behind
                    // its Session's head suppresses eligibility only: the Job
                    // remains an ordinary owner fact for occupancy.
                    locallyEligible = localOrder == AgentCapacityClaimDisposition.Claimed;
                }
                if (AgentCapacityFacts.Occupies(job)) occupied++;
                if (locallyEligible && AgentCapacityFacts.IsEligible(job))
                    eligible.Add(AgentCapacityFacts.JobEntry(row.JobKey, job));
                if (AgentCapacityFacts.IsQueuedJob(job))
                    queued.Add(AgentCapacityFacts.JobEntry(row.JobKey, job));
            }

            foreach (var row in sessionRows.Where(row =>
                string.Equals(row.LabelAgentId, agentId, StringComparison.Ordinal)))
            {
                var session = sessions[row.Id];
                if (session is null)
                {
                    evidence = Incomplete(evidence);
                    continue;
                }
                foreach (var turn in session.Status.Turns ?? [])
                {
                    if (AgentCapacityFacts.Occupies(session, turn)) occupied++;
                    if (!AgentCapacityFacts.IsQueuedTurn(session, turn)) continue;
                    try
                    {
                        queued.Add(AgentCapacityFacts.TurnEntry(session, turn));
                    }
                    catch (InvalidOperationException)
                    {
                        // No acceptance timestamp: the queued fact cannot be
                        // faithfully ordered, so the count stays unknown.
                        evidence = Incomplete(evidence);
                    }
                }
                var head = AgentCapacityFacts.EligibleHead(session);
                if (head is null) continue;
                try
                {
                    eligible.Add(AgentCapacityFacts.TurnEntry(session, head));
                }
                catch (InvalidOperationException)
                {
                    evidence = Incomplete(evidence);
                }
            }

            snapshots.Add(agentId, new AgentCapacitySnapshot(
                projectId,
                agentId,
                evidence,
                definition.Limit,
                evidence == AgentCapacityEvidenceStatus.Complete ? occupied : null,
                AgentCapacityFacts.Order(eligible).ToArray(),
                AgentCapacityFacts.Order(queued).ToArray()));
        }
        return snapshots;
    }

    /// <summary>
    /// Fails the availability read closed while owner work exists that no
    /// label can attribute to a requested Agent: Sessions with a null indexed
    /// Agent label under the requested project, and Sessions with no indexed
    /// project identity at all, enter one batched scan per read, so an
    /// unattributable row never costs an N+1 query. A row whose current work
    /// is provably settled stays invisible. Claims never take this path: a
    /// claim evaluates its own owner facts, not the project-wide projection.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, AgentCapacitySnapshot>> MarkUnattributableSessionsAsync(
        MohistDbContext db,
        string projectId,
        IReadOnlyDictionary<string, AgentCapacitySnapshot> snapshots,
        CancellationToken ct)
    {
        var exceptionalRows = await db.AgentSessions.AsNoTracking()
            .Where(row => row.LabelAgentId == null
                && (row.LabelProjectId == projectId || row.LabelProjectId == null))
            .ToListAsync(ct);
        var unattributable = false;
        foreach (var row in exceptionalRows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null || AgentCapacityFacts.HasUnsettledOrdinaryWork(session))
            {
                unattributable = true;
                break;
            }
        }
        if (!unattributable)
            return snapshots;
        var marked = new Dictionary<string, AgentCapacitySnapshot>(snapshots.Count, StringComparer.Ordinal);
        foreach (var (agentId, snapshot) in snapshots)
            marked[agentId] = snapshot.EvidenceStatus == AgentCapacityEvidenceStatus.Complete
                ? snapshot with
                {
                    EvidenceStatus = AgentCapacityEvidenceStatus.IncompleteOwnerEvidence,
                    Occupied = null,
                }
                : snapshot;
        return marked;
    }

    private static AgentCapacityEvidenceStatus Incomplete(AgentCapacityEvidenceStatus current) =>
        current == AgentCapacityEvidenceStatus.Complete
            ? AgentCapacityEvidenceStatus.IncompleteOwnerEvidence
            : current;

    private static async Task<Dictionary<string, DefinitionEvidence>> ReadDefinitionsAsync(
        MohistDbContext db,
        string projectId,
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct)
    {
        var keys = agentIds
            .Where(agentId => !agentId.StartsWith("builtin:", StringComparison.Ordinal))
            .Select(agentId => GrainKey.Agent(projectId, agentId))
            .ToArray();
        var stored = keys.Length == 0
            ? []
            : await db.Agents.AsNoTracking().Where(row => keys.Contains(row.Id)).ToListAsync(ct);
        var byKey = stored.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, DefinitionEvidence>(StringComparer.Ordinal);
        foreach (var agentId in agentIds)
        {
            if (agentId.StartsWith("builtin:", StringComparison.Ordinal))
            {
                var name = agentId["builtin:".Length..];
                var definition = BuiltInAgentCatalog.Find(name);
                var validProject = !string.Equals(name, BuiltInAgentCatalog.MohistSlackName, StringComparison.Ordinal)
                    || string.Equals(projectId, BuiltInAgentCatalog.MohistSlackProjectId, StringComparison.Ordinal);
                result[agentId] = definition is not null
                    && validProject
                    && string.Equals(BuiltInAgentCatalog.Resolve(definition.Name, projectId).Id, agentId, StringComparison.Ordinal)
                        ? new(AgentCapacityEvidenceStatus.Complete, null)
                        : new(AgentCapacityEvidenceStatus.MissingDefinition, null);
                continue;
            }

            if (!byKey.TryGetValue(GrainKey.Agent(projectId, agentId), out var row))
            {
                result[agentId] = new(AgentCapacityEvidenceStatus.MissingDefinition, null);
                continue;
            }
            DomainAgent? agent;
            try
            {
                agent = AgentStore.Deserialize(row.State);
            }
            catch (JsonException)
            {
                agent = null;
            }
            result[agentId] = agent is not null
                && string.Equals(agent.Id, agentId, StringComparison.Ordinal)
                && string.Equals(agent.ProjectId, projectId, StringComparison.Ordinal)
                && (agent.MaxConcurrentRuns is null or > 0)
                    ? new(AgentCapacityEvidenceStatus.Complete, agent.MaxConcurrentRuns)
                    : new(AgentCapacityEvidenceStatus.MalformedDefinition, null);
        }
        return result;
    }

    private static bool TryJob(string stateJson, out AgentJobState job)
    {
        try
        {
            job = JSON.Deserialize<AgentJobState>(stateJson)!;
            return job is not null;
        }
        catch (JsonException)
        {
            job = null!;
            return false;
        }
    }

    private static async Task<SqliteTransaction> BeginImmediateAsync(
        MohistDbContext db,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = db.Database.GetDbConnection() as SqliteConnection
            ?? throw new InvalidOperationException("Agent capacity claims require the SQLite provider.");
        var transaction = connection.BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(transaction, ct);
        return transaction;
    }

    private sealed record DefinitionEvidence(
        AgentCapacityEvidenceStatus Status,
        int? Limit);
}
