using Mohist.Server.Infrastructure.Events;
using DomainEpic = Mohist.Server.Epic.Domain.Epic;

namespace Mohist.Server.Infrastructure.Data.Epic;

/// <summary>
/// Pure helper that builds the lineage extensions dictionary for an epic
/// CloudEvent from the producing epic aggregate's own state. No
/// cross-aggregate query is issued — stamping uses only identity the
/// epic already holds.
/// </summary>
/// <remarks>
/// Lineage attribute names live on <see cref="EventCatalog.Lineage"/> and
/// stay in sync with <c>design/event-protocol.md</c>. Epic events route by
/// the project-scoped <c>epic</c> number.
/// </remarks>
public static class EpicLineage
{
    /// <summary>
    /// Build the <c>extensions</c> dictionary for an epic event. Always
    /// stamps <c>projectid</c> and <c>epic</c>. Absent affiliation is
    /// not possible here (the epic owns its own identity), so the
    /// dictionary is always populated with these two keys.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildExtensions(DomainEpic state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = state.ProjectId,
            [EventCatalog.Lineage.Epic] = state.Number.ToString(),
        };
    }
}
