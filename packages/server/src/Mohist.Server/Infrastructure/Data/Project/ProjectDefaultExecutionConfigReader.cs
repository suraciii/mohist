using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Project;

/// <summary>
/// Scoped, DB-backed reader for the Project default execution configuration.
/// Reads the single <c>ProjectRow.DefaultExecutionConfigJson</c> value
/// directly — no <c>IProjectGrain</c> call from the Agent domain — and caches
/// the result for the request scope, so hydrating Readiness for an N-agent
/// list costs one read, not N. Agents-domain consumers (Readiness, the
/// launcher, the task-first route) share one scope and therefore one
/// consistent view of the default.
/// </summary>
public sealed class ProjectDefaultExecutionConfigReader : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    private string? _cachedProjectId;
    private ExecutionConfigHint? _cached;
    private bool _read;

    public ProjectDefaultExecutionConfigReader(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ExecutionConfigHint?> GetAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (_read && string.Equals(_cachedProjectId, projectId, StringComparison.Ordinal))
            return _cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var json = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.DefaultExecutionConfigJson)
            .FirstOrDefaultAsync(ct);

        _cachedProjectId = projectId;
        _cached = ExecutionConfigJson.Deserialize(json);
        _read = true;
        return _cached;
    }
}
