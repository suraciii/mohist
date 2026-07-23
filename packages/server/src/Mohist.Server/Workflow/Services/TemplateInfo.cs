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

/// <summary>
/// Outcome of the save-time Action-contract validation step.
/// Performed: a Runner Action catalog was available and the save ran the
/// catalog check. Skipped: no Runner has reported a catalog yet and the
/// save proceeded with Definition-only validation.
/// </summary>
public enum ActionValidationStatus
{
    Performed,
    Skipped,
}

public sealed record ProjectTemplateSaveResult(
    ProjectTemplateInfo Template,
    ActionValidationStatus ActionValidation);
