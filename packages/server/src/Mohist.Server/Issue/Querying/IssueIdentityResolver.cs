using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.Storage;

namespace Mohist.Server.Issue.Querying;

public sealed class IssueIdentityResolver
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueIdentityResolver(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string?> GetIdAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var identity = await GetAsync(projectId, issueNumber, ct);
        return identity?.IssueId;
    }

    public async Task<IssueIdentity?> GetAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.IssueStates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == LegacyKey(projectId, issueNumber), ct);
        if (row is null) return null;

        var issue = IssueSnapshot.DeserializeIssue(row.StateJson);
        if (issue is null) return null;

        return new IssueIdentity(issue.Id, issue.ProjectId, issue.Number);
    }

    public async Task<IssueIdentity?> GetByIdAsync(string issueId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(issueId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.IssueStates.AsNoTracking().ToListAsync(ct);
        foreach (var row in rows)
        {
            var issue = IssueSnapshot.DeserializeIssue(row.StateJson);
            if (issue is not null && string.Equals(issue.Id, issueId, StringComparison.Ordinal))
                return new IssueIdentity(issue.Id, issue.ProjectId, issue.Number);
        }

        return null;
    }

    public static string LegacyKey(string projectId, int issueNumber) => $"{projectId}:{issueNumber}";
}

public sealed record IssueIdentity(string IssueId, string ProjectId, int Number);
