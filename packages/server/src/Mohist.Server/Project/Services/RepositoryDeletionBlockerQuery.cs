using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Project.Services;

/// <summary>
/// focused projection over <see cref="IssueRow"/> for
/// repository deletion protection. Reads the committed
/// <c>(ProjectId, RepositoryName)</c> index directly instead of going
/// through enriched read models so the result is independent of
/// workflow-health or projection staleness.
/// <para>
/// A row is treated as a blocker when:
/// <list type="bullet">
///   <item><c>ProjectId</c> equals the queried Project,</item>
    ///   <item><c>RepositoryName</c> exactly matches the declared canonical
    ///     repository name, and</item>
///   <item><c>Status</c> is one of the non-terminal values.
///     The virtual <c>Status</c> computed column reflects the
///     JSON-serialized <c>IssueStatus</c> enum with the CamelCase
///     converter (<c>backlog</c> / <c>inProgress</c>).</item>
/// </list>
/// <c>done</c> and <c>cancelled</c> Issues keep their historical
/// <c>RepositoryName</c> projection but never block deletion.
/// </para>
/// </summary>
public sealed class RepositoryDeletionBlockerQuery : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public RepositoryDeletionBlockerQuery(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<bool> HasBlockerAsync(string projectId, string repositoryName)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        if (string.IsNullOrWhiteSpace(repositoryName)) return false;

        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId
                && r.RepositoryName != null
                && r.RepositoryName == repositoryName
                && (r.Status == "backlog" || r.Status == "inProgress"))
            .AnyAsync();
    }
}
