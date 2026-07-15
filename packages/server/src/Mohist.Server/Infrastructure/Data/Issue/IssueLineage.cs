using Mohist.Server.Infrastructure.Events;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Infrastructure.Data.Issue;

/// <summary>
/// Pure helper that builds the lineage extensions dictionary for an issue
/// CloudEvent from the producing issue aggregate's own state. No
/// cross-aggregate query is issued — stamping uses only identity the
/// issue already holds (D5).
/// </summary>
/// <remarks>
/// Lineage attribute names live on <see cref="EventCatalog.Lineage"/> and
/// stay in sync with <c>design/event-protocol.md</c>. The user-visible
/// issue number is stamped under the protocol name <c>issue</c> (replacing
/// the legacy <c>issueno</c> key, D3). <c>epicid</c> is stamped when the
/// issue's own state carries an <c>EpicId</c> (set by the Epic domain at
/// link/unlink time, T-004) — when <c>EpicId</c> is null, the key is
/// omitted entirely.
/// </remarks>
public static class IssueLineage
{
    /// <summary>
    /// Build the <c>extensions</c> dictionary for an issue event. Always
    /// stamps <c>projectid</c>, <c>issueid</c>, and <c>issue</c> (the
    /// issue number). Additionally stamps <c>epicid</c> when
    /// <see cref="DomainIssue.EpicId"/> is non-null; absent affiliation
    /// is omitted, never an empty string.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildExtensions(DomainIssue state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = state.ProjectId,
            [EventCatalog.Lineage.IssueId] = state.Id,
            [EventCatalog.Lineage.Issue] = state.Number.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(state.EpicId))
        {
            extensions[EventCatalog.Lineage.EpicId] = state.EpicId!;
        }

        return extensions;
    }
}