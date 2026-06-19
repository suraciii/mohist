namespace Mohist.Server.Issue.Domain.IssueTemplate;

/// <summary>
/// Represents an issue template that defines what an issue body looks like.
/// Forms an orthogonal project-configuration dimension with
/// <see cref="Services.WorkflowProfiles.IIssueWorkflowProfile"/> ("how" an issue executes).
/// </summary>
public interface IIssueTemplate
{
    string Id { get; }
    string Name { get; }
    string About { get; }
    bool IsDefault { get; }
    IReadOnlyList<string> SuitableFor { get; }
    IssueTemplateDefaults Defaults { get; }
    IReadOnlyList<IssueTemplateSection> Sections { get; }
}

public sealed record IssueTemplateSection(string Title, string Guidance, string Placeholder);

public sealed record IssueTemplateDefaults(
    IReadOnlyDictionary<string, string>? Labels = null,
    string? Risk = null,
    string? Workflow = null);
