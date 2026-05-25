namespace Mohist.Server.Issue.Domain;

public static class IssueDomainNames
{
    public static string Status(IssueStatus status) => status switch
    {
        IssueStatus.InProgress => "in_progress",
        _ => status.ToString().ToLowerInvariant(),
    };

}
