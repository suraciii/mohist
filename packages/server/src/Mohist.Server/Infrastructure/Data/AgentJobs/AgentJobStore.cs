using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Domain;

namespace Mohist.Server.Infrastructure.Data.AgentJobs;

public interface IAgentJobStore
{
    /// <summary>
    /// Legacy state-JSON read used by callers that only need the
    /// serialized <c>AgentJobState</c>. New code should prefer
    /// <see cref="LoadLedgerAsync"/> so the scheduling columns are
    /// read alongside the lifecycle JSON.
    /// </summary>
    Task<string?> LoadAsync(string key);

    /// <summary>
    /// State-JSON only mirror used by code paths that pre-date the
    /// owner-ledger migration. Owner writes go through
    /// <see cref="SaveLedgerAsync"/> instead.
    /// </summary>
    Task SaveAsync(string key, string stateJson);

    /// <summary>
    /// Reads the full AgentJob ledger row, including every scheduling
    /// column and the optimistic revision. Returns <c>null</c> when the
    /// row does not exist.
    /// </summary>
    Task<AgentJobLedgerRecord?> LoadLedgerAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Writes the AgentJob ledger row in a single transaction, fencing
    /// against the supplied revision. Throws
    /// <see cref="AgentJobLedgerConflictException"/> when the row's
    /// current revision does not match the supplied
    /// <see cref="AgentJobLedgerRecord.Revision"/>; the caller must
    /// reload and retry. Throws
    /// <see cref="AgentJobLedgerReconstructionException"/> when a
    /// nonterminal row cannot supply a dispatch snapshot.
    /// </summary>
    Task<AgentJobLedgerRecord> SaveLedgerAsync(AgentJobLedgerRecord record, CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions a Pending row to Running for the supplied
    /// runner. Validates that <paramref name="runnerId"/> matches
    /// <see cref="AgentJobLedgerRecord.AssignedRunnerId"/> and that the
    /// row is still Pending; throws <see cref="AgentJobLedgerConflictException"/>
    /// when the row is missing, no longer Pending, or assigned to a
    /// different runner.
    /// </summary>
    Task<AgentJobLedgerRecord> ClaimAsync(string key, string runnerId, DateTimeOffset runningSince, CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions a pending AgentJob while fencing the frozen
    /// work identity. The capability-claim dispatch snapshot is written in
    /// the same ledger transaction as Pending -> Running; the caller owns
    /// the execution-tuple predicate before calling.
    /// </summary>
    Task<AgentJobLedgerRecord> ClaimAsync(
        string key,
        string runnerId,
        DateTimeOffset runningSince,
        string expectedWorkId,
        string dispatchJson,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new AgentJob ledger row if one does not exist. The
    /// initial <see cref="AgentJobLedgerRecord.Revision"/> must be 0;
    /// on success the returned record carries the post-insert revision.
    /// </summary>
    Task<AgentJobLedgerRecord> InsertLedgerAsync(AgentJobLedgerRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> eligible pending jobs
    /// (Pending status, no AssignedRunnerId) ordered by ReadySince ASC.
    /// Excludes terminal jobs by virtue of their Status.
    /// </summary>
    Task<IReadOnlyList<AgentJobLedgerRecord>> ListEligiblePendingAsync(
        string? projectId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns assigned-running work for a runner. Used by the
    /// redelivery path to surface dispatches absent from the runner's
    /// reported set.
    /// </summary>
    Task<IReadOnlyList<AgentJobLedgerRecord>> ListRunningForRunnerAsync(
        string runnerId,
        CancellationToken ct = default);

    Task<bool> IsTerminalWorkAsync(
        string jobKey,
        string runnerId,
        string workId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns assigned-pending work for a runner ordered by ReadySince
    /// ASC. The claim path uses this for poll-time claims that should
    /// hit a runner that already has a prepared assignment.
    /// </summary>
    Task<IReadOnlyList<AgentJobLedgerRecord>> ListAssignedPendingForRunnerAsync(
        string runnerId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns pending jobs whose ReadySince is on or before the cutoff.
    /// Used by the readiness-timeout owner reminder.
    /// </summary>
    Task<IReadOnlyList<AgentJobLedgerRecord>> ListPendingAtOrBeforeReadySinceAsync(
        DateTimeOffset cutoff,
        int limit,
        CancellationToken ct = default);
}

public class AgentJobStore : IAgentJobStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<AgentJobStore> _log;
    private readonly TimeProvider _timeProvider;

    public AgentJobStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<AgentJobStore> log,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _log = log;
        _timeProvider = timeProvider;
    }

    public async Task<string?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AgentJobs.AsNoTracking()
            .Where(r => r.JobKey == key)
            .Select(r => r.State)
            .FirstOrDefaultAsync();
        return row;
    }

    public async Task SaveAsync(string key, string stateJson)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = new AgentJobRow { JobKey = key, State = stateJson };
        var existing = await db.AgentJobs.FindAsync(key);
        if (existing is null)
        {
            StageDirectApiProjection(row);
            db.AgentJobs.Add(row);
        }
        else
        {
            existing.State = stateJson;
            StageDirectApiProjection(existing);
        }
        await db.SaveChangesAsync();
    }

    public async Task<AgentJobLedgerRecord?> LoadLedgerAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var includeSubagentTreeFields = await HasLaunchVisibilityColumnAsync(db, ct);
        var query = db.AgentJobs.AsNoTracking()
            .Where(r => r.JobKey == key);
        var rows = await ProjectRows(query, includeSubagentTreeFields, ct);
        var row = rows.FirstOrDefault();
        return row is null ? null : ToRecord(row);
    }

    public async Task<AgentJobLedgerRecord> InsertLedgerAsync(AgentJobLedgerRecord record, CancellationToken ct = default)
    {
        if (record.Revision != 0)
            throw new ArgumentException(
                "InsertLedgerAsync requires Revision=0; the store assigns the post-insert revision.",
                nameof(record));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = ToRow(record);
        row.Revision = 1;
        StageDirectApiProjection(row);
        db.AgentJobs.Add(row);
        await StageTerminalLogOwnershipAsync(db, record, ct);
        await db.SaveChangesAsync(ct);
        return ToRecord(row);
    }

    public async Task<AgentJobLedgerRecord> SaveLedgerAsync(AgentJobLedgerRecord record, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AgentJobs.FirstOrDefaultAsync(r => r.JobKey == record.JobKey, ct);

        if (existing is null)
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {record.JobKey} does not exist; cannot save with revision {record.Revision}.");
        }

        if (existing.Revision != record.Revision)
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {record.JobKey} revision mismatch: expected {record.Revision}, current {existing.Revision}.");
        }

        if (!string.IsNullOrWhiteSpace(record.AssignedRunnerId) && string.IsNullOrWhiteSpace(record.DispatchJson))
        {
            throw new AgentJobLedgerReconstructionException(
                $"AgentJob ledger row {record.JobKey} cannot have an AssignedRunnerId without a dispatch snapshot.");
        }

        ApplyTo(existing, record);
        existing.Revision = record.Revision + 1;
        StageDirectApiProjection(existing);
        await StageTerminalLogOwnershipAsync(db, record, ct);
        await db.SaveChangesAsync(ct);
        return ToRecord(existing);
    }

    public Task<AgentJobLedgerRecord> ClaimAsync(
        string key,
        string runnerId,
        DateTimeOffset runningSince,
        CancellationToken ct = default) =>
        ClaimAsyncCore(key, runnerId, runningSince, null, null, ct);

    public Task<AgentJobLedgerRecord> ClaimAsync(
        string key,
        string runnerId,
        DateTimeOffset runningSince,
        string expectedWorkId,
        string dispatchJson,
        CancellationToken ct = default) =>
        ClaimAsyncCore(key, runnerId, runningSince, expectedWorkId, dispatchJson, ct);

    private async Task<AgentJobLedgerRecord> ClaimAsyncCore(
        string key,
        string runnerId,
        DateTimeOffset runningSince,
        string? expectedWorkId,
        string? dispatchJson,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.AgentJobs.FirstOrDefaultAsync(r => r.JobKey == key, ct);

        if (existing is null)
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} missing on claim; cannot transition Pending -> Running.");
        }

        var status = (existing.Status ?? string.Empty).ToLowerInvariant();
        if (status != "pending")
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} status {existing.Status} cannot be claimed; expected pending.");
        }

        if (!string.Equals(existing.AssignedRunnerId, runnerId, StringComparison.Ordinal))
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} is assigned to {existing.AssignedRunnerId ?? "<unassigned>"}, not {runnerId}.");
        }

        if (string.IsNullOrWhiteSpace(existing.WorkId))
        {
            throw new AgentJobLedgerReconstructionException(
                $"AgentJob ledger row {key} has no WorkId; cannot transition Pending -> Running.");
        }

        if (string.IsNullOrWhiteSpace(existing.DispatchJson))
        {
            throw new AgentJobLedgerReconstructionException(
                $"AgentJob ledger row {key} has no DispatchJson; cannot transition Pending -> Running.");
        }

        if (expectedWorkId is not null
            && !string.Equals(existing.WorkId, expectedWorkId, StringComparison.Ordinal))
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} work identity changed; expected {expectedWorkId}, found {existing.WorkId}.");
        }

        if (expectedWorkId is not null && string.IsNullOrWhiteSpace(dispatchJson))
            throw new ArgumentException("A capability claim requires the updated dispatch snapshot.", nameof(dispatchJson));

        var nextRevision = existing.Revision + 1;
        var runningSinceText = FormatTimestamp(runningSince);
        var nowText = _timeProvider.GetUtcNow().ToString("O");

        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "AgentJobs"
            SET "State" = json_remove(
                    json_set(
                        json_set("State",
                            '$.status', 'running',
                            '$.runnerId', {existing.AssignedRunnerId},
                            '$.workId', {existing.WorkId},
                            '$.runnerAccepted', json('true'),
                            '$.runningSince', {runningSinceText}),
                        '$.revision', {nextRevision}),
                    '$.readySince'),
                "Revision" = {nextRevision},
                "RunningSince" = {runningSinceText},
                "ReadySince" = NULL
            WHERE "JobKey" = {key}
              AND "Revision" = {existing.Revision};
            """, ct);

        if (affected == 0)
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} claim lost the revision race (expected {existing.Revision}, no rows updated).");
        }

        db.ChangeTracker.Clear();
        var updated = await db.AgentJobs
            .FirstOrDefaultAsync(r => r.JobKey == key, ct);
        if (updated is null)
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} vanished mid-claim; cannot confirm Running transition.");
        }
        if (updated.Revision != nextRevision)
        {
            throw new AgentJobLedgerConflictException(
                $"AgentJob ledger row {key} claim revision mismatch after update (expected {nextRevision}, saw {updated.Revision}).");
        }

        if (dispatchJson is not null)
            updated.DispatchJson = dispatchJson;
        StageDirectApiProjection(updated);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _log.LogInformation(
            "AgentJob {Key} claimed by {Runner} at {RunningSince}",
            key, runnerId, nowText);
        return ToRecord(updated);
    }


    public async Task<IReadOnlyList<AgentJobLedgerRecord>> ListEligiblePendingAsync(
        string? projectId,
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var includeSubagentTreeFields = await HasLaunchVisibilityColumnAsync(db, ct);
        var query = db.AgentJobs.AsNoTracking()
            .Where(r => r.Status == "pending" && r.AssignedRunnerId == null);

        if (includeSubagentTreeFields)
            query = query.Where(r => r.LaunchVisibility == null || r.LaunchVisibility == "visible");

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var pid = projectId;
            query = query.Where(r => r.ProjectId == pid);
        }

        var rows = await ProjectRows(query
            .OrderBy(r => r.ReadySince)
            .ThenBy(r => r.JobKey)
            .Take(limit), includeSubagentTreeFields, ct);

        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<AgentJobLedgerRecord>> ListRunningForRunnerAsync(
        string runnerId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var includeSubagentTreeFields = await HasLaunchVisibilityColumnAsync(db, ct);
        var query = db.AgentJobs.AsNoTracking()
            .Where(r => r.AssignedRunnerId == runnerId && r.Status == "running");

        if (includeSubagentTreeFields)
            query = query.Where(r => r.LaunchVisibility == null || r.LaunchVisibility == "visible");

        var rows = await ProjectRows(query, includeSubagentTreeFields, ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<bool> IsTerminalWorkAsync(
        string jobKey,
        string runnerId,
        string workId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobKey)
            || string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(workId))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TerminalLogOwnerships
            .AsNoTracking()
            .AnyAsync(row => row.OwnerKind == TerminalLogOwnerKinds.AgentJob
                && row.OwnerId == jobKey
                && row.WorkId == workId
                && row.RunnerId == runnerId, ct);
    }

    public async Task<IReadOnlyList<AgentJobLedgerRecord>> ListAssignedPendingForRunnerAsync(
        string runnerId,
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var includeSubagentTreeFields = await HasLaunchVisibilityColumnAsync(db, ct);
        var query = db.AgentJobs.AsNoTracking()
            .Where(r => r.AssignedRunnerId == runnerId && r.Status == "pending");

        if (includeSubagentTreeFields)
            query = query.Where(r => r.LaunchVisibility == null || r.LaunchVisibility == "visible");

        var rows = await ProjectRows(query
            .OrderBy(r => r.ReadySince)
            .ThenBy(r => r.JobKey)
            .Take(limit), includeSubagentTreeFields, ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<AgentJobLedgerRecord>> ListPendingAtOrBeforeReadySinceAsync(
        DateTimeOffset cutoff,
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoffText = FormatTimestamp(cutoff);
        var includeSubagentTreeFields = await HasLaunchVisibilityColumnAsync(db, ct);
        var query = db.AgentJobs.AsNoTracking()
            .Where(r => r.Status == "pending" && r.ReadySince != null && r.ReadySince.CompareTo(cutoffText) <= 0);

        if (includeSubagentTreeFields)
            query = query.Where(r => r.LaunchVisibility == null || r.LaunchVisibility == "visible");

        var rows = await ProjectRows(query
            .OrderBy(r => r.ReadySince)
            .ThenBy(r => r.JobKey)
            .Take(limit), includeSubagentTreeFields, ct);
        return rows.Select(ToRecord).ToList();
    }

    private static async Task<bool> HasLaunchVisibilityColumnAsync(
        MohistDbContext db,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT 1 FROM pragma_table_info('AgentJobs') WHERE name = 'LaunchVisibility' LIMIT 1";
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static Task<List<LedgerQueryRow>> ProjectRows(
        IQueryable<AgentJobRow> query,
        bool includeSubagentTreeFields,
        CancellationToken ct) =>
        includeSubagentTreeFields
            ? query.Select(row => new LedgerQueryRow
            {
                JobKey = row.JobKey,
                State = row.State,
                Revision = row.Revision,
                AssignedRunnerId = row.AssignedRunnerId,
                WorkId = row.WorkId,
                ReadySince = row.ReadySince,
                RunningSince = row.RunningSince,
                DispatchJson = row.DispatchJson,
                WorkType = row.WorkType,
                Stage = row.Stage,
                Title = row.Title,
                IssueProjectId = row.IssueProjectId,
                IssueNumber = row.IssueNumber,
                AgentSessionId = row.AgentSessionId,
                InitialInputId = row.InitialInputId,
                InitialTurnId = row.InitialTurnId,
                PinnedRunnerId = row.PinnedRunnerId,
                LaunchVisibility = row.LaunchVisibility,
            }).ToListAsync(ct)
            : query.Select(row => new LedgerQueryRow
            {
                JobKey = row.JobKey,
                State = row.State,
                Revision = row.Revision,
                AssignedRunnerId = row.AssignedRunnerId,
                WorkId = row.WorkId,
                ReadySince = row.ReadySince,
                RunningSince = row.RunningSince,
                DispatchJson = row.DispatchJson,
                WorkType = row.WorkType,
                Stage = row.Stage,
                Title = row.Title,
                IssueProjectId = row.IssueProjectId,
                IssueNumber = row.IssueNumber,
                AgentSessionId = row.AgentSessionId,
                InitialInputId = row.InitialInputId,
                InitialTurnId = row.InitialTurnId,
            }).ToListAsync(ct);

    private static AgentJobRow ToRow(AgentJobLedgerRecord record) => new()
    {
        JobKey = record.JobKey,
        State = record.StateJson,
        Revision = record.Revision,
        AssignedRunnerId = record.AssignedRunnerId,
        WorkId = record.WorkId,
        ReadySince = FormatTimestamp(record.ReadySince),
        RunningSince = FormatTimestamp(record.RunningSince),
        DispatchJson = record.DispatchJson,
        WorkType = record.WorkType,
        Stage = record.Stage,
        Title = record.Title,
        IssueProjectId = record.IssueProjectId,
        IssueNumber = record.IssueNumber,
        AgentSessionId = record.AgentSessionId,
        InitialInputId = record.InitialInputId,
        InitialTurnId = record.InitialTurnId,
        PinnedRunnerId = record.PinnedRunnerId,
        LaunchVisibility = record.LaunchVisibility,
    };

    private static AgentJobLedgerRecord ToRecord(AgentJobRow row) => new(
        row.JobKey,
        row.State,
        row.Revision,
        row.AssignedRunnerId,
        row.WorkId,
        ParseTimestamp(row.ReadySince),
        ParseTimestamp(row.RunningSince),
        row.DispatchJson,
        row.WorkType,
        row.Stage,
        row.Title,
        row.IssueProjectId,
        row.IssueNumber,
        row.AgentSessionId,
        row.InitialInputId,
        row.InitialTurnId,
        row.PinnedRunnerId,
        row.LaunchVisibility,
        null);

    private static AgentJobLedgerRecord ToRecord(LedgerQueryRow row) => new(
        row.JobKey,
        row.State,
        row.Revision,
        row.AssignedRunnerId,
        row.WorkId,
        ParseTimestamp(row.ReadySince),
        ParseTimestamp(row.RunningSince),
        row.DispatchJson,
        row.WorkType,
        row.Stage,
        row.Title,
        row.IssueProjectId,
        row.IssueNumber,
        row.AgentSessionId,
        row.InitialInputId,
        row.InitialTurnId,
        row.PinnedRunnerId,
        row.LaunchVisibility,
        null);

    private static Task StageTerminalLogOwnershipAsync(
        MohistDbContext db,
        AgentJobLedgerRecord record,
        CancellationToken ct) =>
        record.TerminalLogOwnership is null
            ? Task.CompletedTask
            : TerminalLogOwnershipPersistence.StageAsync(db, record.TerminalLogOwnership, ct);

    private sealed class LedgerQueryRow
    {
        public string JobKey { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public long Revision { get; init; }
        public string? AssignedRunnerId { get; init; }
        public string? WorkId { get; init; }
        public string? ReadySince { get; init; }
        public string? RunningSince { get; init; }
        public string? DispatchJson { get; init; }
        public string? WorkType { get; init; }
        public string? Stage { get; init; }
        public string? Title { get; init; }
        public string? IssueProjectId { get; init; }
        public int? IssueNumber { get; init; }
        public string? AgentSessionId { get; init; }
        public string? InitialInputId { get; init; }
        public string? InitialTurnId { get; init; }
        public string? PinnedRunnerId { get; init; }
        public string LaunchVisibility { get; init; } = "visible";
    }

    private static void ApplyTo(AgentJobRow existing, AgentJobLedgerRecord record)
    {
        existing.State = record.StateJson;
        existing.AssignedRunnerId = record.AssignedRunnerId;
        existing.WorkId = record.WorkId;
        existing.ReadySince = FormatTimestamp(record.ReadySince);
        existing.RunningSince = FormatTimestamp(record.RunningSince);
        existing.DispatchJson = record.DispatchJson;
        existing.WorkType = record.WorkType;
        existing.Stage = record.Stage;
        existing.Title = record.Title;
        existing.IssueProjectId = record.IssueProjectId;
        existing.IssueNumber = record.IssueNumber;
        existing.AgentSessionId = record.AgentSessionId;
        existing.InitialInputId = record.InitialInputId;
        existing.InitialTurnId = record.InitialTurnId;
        existing.PinnedRunnerId = record.PinnedRunnerId;
        existing.LaunchVisibility = record.LaunchVisibility;
    }

    private void StageDirectApiProjection(AgentJobRow row)
    {
        var snapshot = DirectApiAgentJobProjection.Create(
            row.JobKey,
            row.State,
            row.Revision,
            _timeProvider.GetUtcNow());
        row.DirectApiProjectionJson = snapshot is null ? null : JSON.Serialize(snapshot);
        row.DirectApiProjectionRevision = snapshot is null ? null : row.Revision;
    }

    internal static string? FormatTimestamp(DateTimeOffset? value) =>
        value is null ? null : value.Value.ToString("O");

    internal static DateTimeOffset? ParseTimestamp(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : DateTimeOffset.Parse(text);
}
