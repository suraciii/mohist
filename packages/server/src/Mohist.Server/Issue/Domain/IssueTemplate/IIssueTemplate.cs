namespace Mohist.Server.Issue.Domain.IssueTemplate;

public interface IIssueTemplate
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    IReadOnlyList<IssueTemplateSection> Sections { get; }
}

public sealed record IssueTemplateSection(string Title, string Guidance, string Placeholder);
