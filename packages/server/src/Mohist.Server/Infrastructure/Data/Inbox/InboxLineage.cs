using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;

namespace Mohist.Server.Infrastructure.Data.Inbox;

public static class InboxLineage
{
    private static readonly string[] InheritedKeys =
    [
        EventCatalog.Lineage.Epic,
        EventCatalog.Lineage.WorkflowRunId,
        EventCatalog.Lineage.Stage,
    ];

    public static IReadOnlyDictionary<string, string> BuildExtensions(
        InboxItemDraft draft,
        IReadOnlyDictionary<string, string> sourceExtensions)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(sourceExtensions);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = draft.ProjectId,
            [EventCatalog.Lineage.Issue] = draft.IssueNumber.ToString(),
        };

        foreach (var key in InheritedKeys)
        {
            if (sourceExtensions.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                extensions[key] = value;
        }

        return extensions;
    }
}
