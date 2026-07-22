namespace Mohist.Server.Workflow.Services;

/// <summary>
/// System template metadata (read-only catalog, hardcoded in binary).
/// </summary>
public sealed record SystemTemplateInfo(
    string Id,
    string Name,
    string Description,
    bool IsDefault);

/// <summary>
/// Project template metadata.
/// </summary>
public sealed record ProjectTemplateInfo(
    string ProjectId,
    string TemplateId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Name = null,
    string? Description = null);
