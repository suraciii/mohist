namespace Mohist.Server.Infrastructure.Data.Events;

internal static class IssueEventPersistence
{
    // CloudEvents 1.0.2 source URI-reference. Format: /mohist/projects/{project}/issues/{number}.
    public const string SourcePrefix = "/mohist/issues/";
    public static string IssueSource(string projectId, int issueNumber) =>
        $"/mohist/projects/{projectId}/issues/{issueNumber}";
}
