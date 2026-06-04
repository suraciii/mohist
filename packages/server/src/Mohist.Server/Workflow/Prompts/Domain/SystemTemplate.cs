namespace Mohist.Server.Workflow.Prompts.Domain;

public sealed record SystemTemplate(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body);
