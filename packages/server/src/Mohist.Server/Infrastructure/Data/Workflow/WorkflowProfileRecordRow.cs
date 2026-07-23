namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// issue-477 T-001: Project-scoped WorkflowProfile collection row. One row
/// per custom (non-built-in) Profile. Built-in <c>mohist/*</c> Profiles are
/// served by <c>WorkflowProfileCatalog</c> and never persisted here. The
/// <c>(ProjectId, ProfileId)</c> composite is the natural key and the
/// target of the nullable custom-Profile backing-key foreign keys on
/// <c>ProjectWorkflowProfiles.DefaultWorkflowProfileIdKey</c>,
/// <c>IssueRow.WorkflowProfileIdKey</c>, and
/// <c>WorkflowRunRow.WorkflowProfileIdKey</c>.
/// </summary>
public class WorkflowProfileRecordRow
{
    public string ProjectId { get; set; } = string.Empty;

    public string ProfileId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Definition YAML source. Verbatim for <c>Verbatim</c> Profiles
    /// (created or edited after the migration). For <c>CanonicalLegacy</c>
    /// Profiles (migrated from legacy semantic JSON) this is the
    /// deterministic canonical YAML renderer output.
    /// </summary>
    public string DefinitionSource { get; set; } = string.Empty;

    /// <summary>
    /// Either <c>Verbatim</c> or <c>CanonicalLegacy</c>. The two
    /// values are stable; the read API maps them to the
    /// public <c>SourceProvenance</c> enum.
    /// </summary>
    public string SourceProvenance { get; set; } = "Verbatim";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
