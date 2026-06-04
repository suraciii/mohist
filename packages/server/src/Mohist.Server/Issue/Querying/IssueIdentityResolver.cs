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
        var legacyRow = await db.IssueStates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == LegacyKey(projectId, issueNumber), ct);
        var legacyIssue = legacyRow is null ? null : IssueSnapshot.DeserializeIssue(legacyRow.StateJson);
        if (legacyIssue is not null)
        {
            var canonicalRow = await db.IssueStates
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Key == legacyIssue.Id, ct);
            var canonicalIssue = canonicalRow is null ? null : IssueSnapshot.DeserializeIssue(canonicalRow.StateJson);
            var resolvedIssue = IssueStateReader.IsIssue(canonicalIssue, projectId, issueNumber) ? canonicalIssue! : legacyIssue;
            return ToIdentity(resolvedIssue);
        }

        var rows = await db.IssueStates.AsNoTracking().ToListAsync(ct);
        var issue = IssueStateReader.SelectCanonicalOrDefault(IssueStateReader.Deserialize(rows)
            .Where(row => IssueStateReader.IsIssue(row.Issue, projectId, issueNumber)));
        return issue is null ? null : ToIdentity(issue);
    }

    public async Task<IssueIdentity?> GetByIdAsync(string issueId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(issueId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var canonicalRow = await db.IssueStates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == issueId, ct);
        var canonicalIssue = canonicalRow is null ? null : IssueSnapshot.DeserializeIssue(canonicalRow.StateJson);
        if (canonicalIssue is not null && string.Equals(canonicalIssue.Id, issueId, StringComparison.Ordinal))
            return ToIdentity(canonicalIssue);

        var rows = await db.IssueStates.AsNoTracking().ToListAsync(ct);
        foreach (var row in IssueStateReader.Deserialize(rows))
        {
            if (string.Equals(row.Issue.Id, issueId, StringComparison.Ordinal))
                return ToIdentity(row.Issue);
        }

        return null;
    }

    private static IssueIdentity ToIdentity(Domain.Issue issue) =>
        new(issue.Id, issue.ProjectId, issue.Number);

    public static string LegacyKey(string projectId, int issueNumber) => $"{projectId}:{issueNumber}";
}

public sealed record IssueIdentity(string IssueId, string ProjectId, int Number);
