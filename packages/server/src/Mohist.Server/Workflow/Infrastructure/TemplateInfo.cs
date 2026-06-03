namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// System template metadata (read-only catalog, hardcoded in binary).
/// </summary>
public sealed record SystemTemplateInfo(
    string Id,
    string Name,
    string Description);

/// <summary>
/// Project template metadata.
/// </summary>
public sealed record ProjectTemplateInfo(
    string ProjectId,
    string TemplateId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
