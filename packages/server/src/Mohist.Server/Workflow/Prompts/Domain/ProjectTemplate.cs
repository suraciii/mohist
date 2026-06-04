namespace Mohist.Server.Workflow.Prompts.Domain;

public sealed record ProjectTemplate(
    string ProjectId,
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body,
    DateTime UpdatedAt);
