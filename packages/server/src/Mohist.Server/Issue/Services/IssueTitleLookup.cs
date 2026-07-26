using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Authoritative home for the issue-title batch lookup and the
/// single-title fallback resolver used by AgentOps read assemblies.
/// Owned by the Issue read side
/// because the lookup reads <c>db.Issues</c> + <see cref="IssueRowMapper.ByNumber"/> —
/// both Issue-domain data — and matches the architecture rule of
/// placing cross-domain read queries on the domain that owns the data
///.
/// </summary>
/// <remarks>
/// Pure static (no DI) following the <see cref="IssueRowMapper"/> /
/// <see cref="Mohist.Server.Sessions.Services.AgentSessionDtoMapper"/>
/// precedent. Takes <see cref="MohistDbContext"/> as a parameter so the
/// method is testable via the in-memory SQLite fixture without DI
/// gymnastics; the resolver is a pure function over a dictionary.
/// </remarks>
internal static class IssueTitleLookup
{
    /// <summary>
    /// Loads the titles of the given issue numbers for the given project,
    /// returning a number → title dictionary. Distinct numbers are looked
    /// up once (duplicates are deduplicated up front); empty input yields
    /// an empty dictionary without ever touching the database. Rows are
    /// deserialised via <see cref="IssueRowMapper.ByNumber"/> so the
    /// project's <c>ProjectId</c> guard is enforced — issues that belong
    /// to another project are silently dropped, matching the pre-change
    /// semantics.
    /// </summary>
    internal static async Task<Dictionary<int, string>> LoadTitlesAsync(
        MohistDbContext db,
        string projectId,
        IEnumerable<int> issueNumbers,
        CancellationToken ct)
    {
        var numbers = issueNumbers.Distinct().ToArray();
        if (numbers.Length == 0) return [];

        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number != null && numbers.Contains(row.Number.Value))
            .ToListAsync(ct);

        return IssueRowMapper.ByNumber(rows, projectId, numbers)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Title);
    }

    /// <summary>
    /// Resolves a single issue title with the <c>Issue #{number}</c>
    /// fallback. Returns the stored title verbatim when the number maps
    /// to a non-whitespace title; otherwise returns the literal
    /// <c>Issue #{number}</c> byte-identical to the pre-change resolver,
    /// so list / activity-feed projections stay in lockstep.
    /// </summary>
    internal static string Resolve(IReadOnlyDictionary<int, string> titles, int issueNumber) =>
        titles.TryGetValue(issueNumber, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : $"Issue #{issueNumber}";
}