namespace Mohist.Server.Infrastructure.Data.Events;

internal static class WorkspaceEventPersistence
{
    public static string WorkspaceSource(string projectId, string name) =>
        $"/mohist/projects/{projectId}/workspaces/{name}";

    public static string ProjectSourcePrefix(string projectId) =>
        $"/mohist/projects/{projectId}/workspaces/";

    public static bool IsWorkspaceSource(string source) =>
        source.StartsWith("/mohist/projects/", StringComparison.Ordinal)
        && source.Contains("/workspaces/", StringComparison.Ordinal);
}
