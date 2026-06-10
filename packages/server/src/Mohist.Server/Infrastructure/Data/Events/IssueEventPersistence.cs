namespace Mohist.Server.Infrastructure.Data.Events;

internal static class IssueEventPersistence
{
    // CloudEvents 1.0.2 source URI-reference. Format: /{context}/{aggregate}/{id}.
    public const string SourcePrefix = "/mohist/issues/";
    public static string IssueSource(string issueId) => $"{SourcePrefix}{issueId}";
}
