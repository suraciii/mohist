using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;

namespace Mohist.Server.Issue.Services;

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
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == issueNumber, ct);
        var issue = row is null ? null : IssueSnapshot.DeserializeIssue(row.State);
        return issue is null ? null : ToIdentity(issue);
    }

    public async Task<IssueIdentity?> GetByIdAsync(string issueId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(issueId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var canonicalRow = await db.Issues
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IssueId == issueId, ct);
        var canonicalIssue = canonicalRow is null ? null : IssueSnapshot.DeserializeIssue(canonicalRow.State);
        return canonicalIssue is not null && string.Equals(canonicalIssue.Id, issueId, StringComparison.Ordinal)
            ? ToIdentity(canonicalIssue)
            : null;
    }

    private static IssueIdentity ToIdentity(Domain.Issue issue) =>
        new(issue.Id, issue.ProjectId, issue.Number);

}

public sealed record IssueIdentity(string IssueId, string ProjectId, int Number);
