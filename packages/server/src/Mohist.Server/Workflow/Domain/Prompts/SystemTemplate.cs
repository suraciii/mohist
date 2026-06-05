namespace Mohist.Server.Workflow.Domain.Prompts;

public sealed record SystemTemplate(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body);
