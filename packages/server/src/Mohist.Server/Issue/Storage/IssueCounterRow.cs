namespace Mohist.Server.Issue.Storage;

public class IssueCounterRow
{
    public string ProjectId { get; set; } = string.Empty;
    public int Next { get; set; } = 1;
}
