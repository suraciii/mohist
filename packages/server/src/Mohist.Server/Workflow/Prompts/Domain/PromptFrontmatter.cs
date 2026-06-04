namespace Mohist.Server.Workflow.Prompts.Domain;

public sealed record PromptFrontmatter
{
    public string? Name { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? Stage { get; init; }
}
