using Mohist.Server.Infrastructure.Events;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Data.Issue;

/// <summary>
/// Pure helper that builds the lineage extensions dictionary for an issue
/// CloudEvent from the producing issue aggregate's own state. No
/// cross-aggregate query is issued — stamping uses only identity the
/// issue already holds.
/// </summary>
/// <remarks>
/// Lineage attribute names live on <see cref="EventCatalog.Lineage"/>.
/// The user-visible
/// issue number is stamped under the protocol name <c>issue</c>. The current
/// Epic number is stamped under <c>epic</c>; when it is null, the key is
/// omitted entirely. The parent issue number is stamped under <c>parent</c>;
/// when the issue has no parent, the key is omitted entirely.
/// </remarks>
public static class IssueLineage
{
    /// <summary>
    /// Build the <c>extensions</c> dictionary for an issue event. Always
    /// stamps <c>projectid</c> and <c>issue</c> (the issue number).
    /// Additionally stamps <c>epic</c> when
    /// <see cref="DomainIssue.EpicNumber"/> is non-null and <c>parent</c>
    /// when <see cref="DomainIssue.ParentIssueNumber"/> is non-null;
    /// absent affiliation is omitted, never an empty string.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildExtensions(DomainIssue state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = state.ProjectId,
            [EventCatalog.Lineage.Issue] = state.Number.ToString(),
        };
        if (state.EpicNumber is > 0)
        {
            extensions[EventCatalog.Lineage.Epic] = state.EpicNumber.Value.ToString();
        }
        if (state.ParentIssueNumber is > 0)
        {
            extensions[EventCatalog.Lineage.Parent] = state.ParentIssueNumber.Value.ToString();
        }

        return extensions;
    }
}
