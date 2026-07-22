using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Workflow.Services;

public sealed record ResolvedPrompt(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body,
    string Source);

public sealed record EffectivePrompt(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body,
    string Source);

public sealed record PromptPreviewResult(
    string Rendered,
    IReadOnlyList<string> MissingVariables,
    int Depth,
    IReadOnlyList<TemplateRenderError> Errors);
