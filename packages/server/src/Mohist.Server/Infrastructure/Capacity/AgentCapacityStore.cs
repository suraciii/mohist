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
        return await ReadAsync(db, projectId, ids, ct);
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

        var admission = Admission(capacity, AgentCapacityFacts.JobEntry(row.JobKey, job));
        if (admission != AgentCapacityClaimDisposition.Claimed)
            return new(admission, capacity);

        var now = _timeProvider.GetUtcNow();
        job.CapacityClaimedAt = now;
        job.ReadySince ??= now;
        row.State = JSON.Serialize(job);
        row.ReadySince ??= AgentJobStore.FormatTimestamp(job.ReadySince);
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
            .Where(row => row.ProjectId == projectId && row.AgentId != null && ids.Contains(row.AgentId))
            .ToListAsync(ct);
        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(row => row.LabelProjectId == projectId
                && row.LabelAgentId != null
                && ids.Contains(row.LabelAgentId))
            .ToListAsync(ct);

        var snapshots = new Dictionary<string, AgentCapacitySnapshot>(StringComparer.Ordinal);
        foreach (var agentId in ids)
        {
            var definition = definitions[agentId];
            var evidence = definition.Status;
            var occupied = 0;
            var eligible = new List<AgentCapacityQueueEntry>();

            foreach (var row in jobs.Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal)))
            {
                if (!TryJob(row.State, out var job))
                {
                    evidence = Incomplete(evidence);
                    continue;
                }
                if (AgentCapacityFacts.Occupies(job)) occupied++;
                if (AgentCapacityFacts.IsEligible(job))
                    eligible.Add(AgentCapacityFacts.JobEntry(row.JobKey, job));
            }

            foreach (var row in sessions.Where(row => string.Equals(row.LabelAgentId, agentId, StringComparison.Ordinal)))
            {
                var session = AgentSessionJson.Deserialize(row);
                if (session is null)
                {
                    evidence = Incomplete(evidence);
                    continue;
                }
                foreach (var turn in session.Status.Turns ?? [])
                    if (AgentCapacityFacts.Occupies(session, turn)) occupied++;
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
                AgentCapacityFacts.Order(eligible).ToArray()));
        }
        return snapshots;
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
