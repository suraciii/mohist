namespace Mohist.Server.Infrastructure.Data.Events;

internal static class IssueEventPersistence
{
    public static string IssueSource(string projectId, int issueNumber) =>
        $"/mohist/projects/{projectId}/issues/{issueNumber}";

    public static string ProjectSourcePrefix(string projectId) =>
        $"/mohist/projects/{projectId}/issues/";

    public static bool IsIssueSource(string source) =>
        source.StartsWith("/mohist/projects/", StringComparison.Ordinal)
        && source.Contains("/issues/", StringComparison.Ordinal);
}
