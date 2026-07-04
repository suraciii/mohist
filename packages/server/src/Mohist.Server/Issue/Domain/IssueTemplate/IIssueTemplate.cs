namespace Mohist.Server.Issue.Domain.IssueTemplate;

public interface IIssueTemplate
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Body { get; }
}
