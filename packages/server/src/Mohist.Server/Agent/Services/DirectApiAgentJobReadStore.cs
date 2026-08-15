using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Reads only the persisted direct-API Job snapshot. Canonical ledger state is
/// consulted solely for source-revision freshness and Project membership; it
/// is never serialized into the external response.
/// </summary>
public sealed class DirectApiAgentJobReadStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public DirectApiAgentJobReadStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<DirectApiAgentJobReadResult> ReadAsync(
        string projectId,
        string jobId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = await db.AgentJobs.AsNoTracking()
            .Where(row => row.JobKey == jobId)
            .Select(row => new
            {
                row.ProjectId,
                row.Revision,
                row.DirectApiProjectionJson,
                row.DirectApiProjectionRevision,
            })
            .FirstOrDefaultAsync(ct);

        if (source is null || !string.Equals(source.ProjectId, projectId, StringComparison.Ordinal))
            return DirectApiAgentJobReadResult.NotFound;

        if (string.IsNullOrWhiteSpace(source.DirectApiProjectionJson)
            || source.DirectApiProjectionRevision != source.Revision)
        {
            return DirectApiAgentJobReadResult.ProjectionLag;
        }

        var projection = JSON.Deserialize<DirectApiAgentJobProjection>(source.DirectApiProjectionJson);
        if (projection is null
            || projection.SourceRevision != source.Revision
            || !string.Equals(projection.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(projection.JobId, jobId, StringComparison.Ordinal))
        {
            return DirectApiAgentJobReadResult.ProjectionLag;
        }

        return new DirectApiAgentJobReadResult(
            new DirectApiAgentJobReadSnapshot(
                projection.ProjectId,
                projection.AgentId,
                projection.JobId,
                projection.Status,
                projection.Outcome,
                projection.ReasonCode,
                projection.AcceptedAt,
                projection.StartedAt,
                projection.TerminalAt,
                projection.ObservedAt),
            false);
    }
}

/// <summary>Allowlisted Job snapshot returned by the Agent read service.</summary>
public sealed record DirectApiAgentJobReadSnapshot(
    string ProjectId,
    string? AgentId,
    string JobId,
    string Status,
    string? Outcome,
    string? ReasonCode,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? TerminalAt,
    DateTimeOffset ObservedAt);

public sealed record DirectApiAgentJobReadResult(
    DirectApiAgentJobReadSnapshot? Snapshot,
    bool IsProjectionLag)
{
    public static DirectApiAgentJobReadResult NotFound { get; } = new(null, false);
    public static DirectApiAgentJobReadResult ProjectionLag { get; } = new(null, true);
}
