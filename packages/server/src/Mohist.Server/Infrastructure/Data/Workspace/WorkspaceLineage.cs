using Mohist.Server.Infrastructure.Events;
using DomainWorkspace = Mohist.Server.Workspace.Domain;

namespace Mohist.Server.Infrastructure.Data.Workspace;

public static class WorkspaceLineage
{
    public static IReadOnlyDictionary<string, string> BuildExtensions(DomainWorkspace.WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = state.ProjectId,
            [EventCatalog.Lineage.Workspace] = state.Name,
            [EventCatalog.Lineage.WorkspaceOriginKind] = WorkspaceRowJson.OriginKind(state.Origin),
        };

        if (state.Origin is DomainWorkspace.WorkspaceOrigin.Issue issue)
        {
            extensions[EventCatalog.Lineage.Issue] = issue.IssueNumber.ToString();
        }

        return extensions;
    }
}
