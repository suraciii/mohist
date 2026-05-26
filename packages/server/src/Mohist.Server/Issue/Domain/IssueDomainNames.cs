namespace Mohist.Server.Issue.Domain;

public static class IssueDomainNames
{
    public static string Stage(IssueStage stage) => stage switch
    {
        IssueStage.InProgress => "in_progress",
        _ => stage.ToString().ToLowerInvariant(),
    };

}
