using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// issue-477 T-001: narrow WorkflowProfile collection contract. The
/// provider is the sole existence and read-only authority for built-in
/// <c>mohist/*</c> Profiles and the read/write authority for custom
/// (Project-scoped) Profiles. Callers MUST go through this provider
/// rather than touching <c>WorkflowProfileCatalog</c> or
/// <c>WorkflowProfileRecordRow</c> directly; the orchestrator and
/// coordinator use it to validate collection membership before any
/// participant writes the binding.
/// </summary>
public interface IWorkflowProfileProvider
{
    /// <summary>
    /// Lists every Profile visible in the given Project collection:
    /// built-in <c>mohist/*</c> Profiles (sorted first) followed by
    /// custom Profiles. Built-ins are read-only entries; custom Profiles
    /// carry the metadata captured by the most recent save.
    /// </summary>
    Task<IReadOnlyList<WorkflowProfileCollectionEntry>> ListAsync(string projectId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a single Profile ID within the Project collection. Returns
    /// <c>null</c> when the ID is unknown.
    /// </summary>
    Task<WorkflowProfileCollectionEntry?> GetAsync(string projectId, string profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns the resolved WorkflowDefinition for a Profile. Built-ins
    /// emit their authoritative in-binary definition; custom Profiles
    /// deserialize the persisted YAML source. Returns <c>null</c> when
    /// the ID is unknown.
    /// </summary>
    Task<WorkflowDefinition?> GetDefinitionAsync(string projectId, string profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns the persisted YAML source for a custom Profile. Built-ins
    /// are not persisted; this method returns <c>null</c> for them.
    /// </summary>
    Task<string?> GetDefinitionSourceAsync(string projectId, string profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns the source provenance for a custom Profile. <c>null</c>
    /// for built-ins.
    /// </summary>
    Task<WorkflowProfileSourceProvenance?> GetSourceProvenanceAsync(string projectId, string profileId, CancellationToken ct = default);

    /// <summary>
    /// Validates a custom Profile against the authoritative Definition
    /// validator and the Runner-reported Action catalog, then persists
    /// it verbatim. Throws <see cref="WorkflowProfileReadOnlyException"/>
    /// when the target ID is a built-in. Throws
    /// <see cref="WorkflowProfileAlreadyExistsException"/> when the
    /// Profile already exists for create.
    /// </summary>
    Task<WorkflowProfileSaveResult> CreateAsync(
        string projectId,
        WorkflowProfileCollectionEntry request,
        CancellationToken ct = default);

    /// <summary>
    /// Validates and updates a custom Profile, preserving the verbatim
    /// source. Built-in IDs are rejected with
    /// <see cref="WorkflowProfileReadOnlyException"/>; missing customs
    /// surface as <see cref="WorkflowProfileNotFoundException"/>.
    /// </summary>
    Task<WorkflowProfileSaveResult> UpdateAsync(
        string projectId,
        WorkflowProfileCollectionEntry request,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a custom Profile by ID. Built-in IDs are rejected with
    /// <see cref="WorkflowProfileReadOnlyException"/>; missing customs
    /// return <c>false</c>.
    /// </summary>
    Task<bool> DeleteAsync(string projectId, string profileId, CancellationToken ct = default);

    /// <summary>
    /// Membership probe used by the coordinator and the deletion blocker
    /// query. Returns <c>true</c> for any built-in ID or any custom row.
    /// </summary>
    Task<bool> ContainsAsync(string projectId, string profileId, CancellationToken ct = default);
}

/// <summary>
/// issue-477 T-001: a single Profile as exposed through the collection
/// provider. Built-in entries have <see cref="SourceProvenance"/> =
/// <c>BuiltIn</c> and no persisted YAML source; custom entries carry
/// <c>Verbatim</c> or <c>CanonicalLegacy</c> provenance.
/// </summary>
public sealed record WorkflowProfileCollectionEntry(
    string ProjectId,
    string ProfileId,
    string Name,
    string Description,
    WorkflowProfileSourceProvenance SourceProvenance,
    bool IsBuiltIn,
    string? DefinitionSource)
{
    public static WorkflowProfileCollectionEntry BuiltIn(string profileId)
        => new(
            ProjectId: string.Empty,
            ProfileId: profileId,
            Name: profileId,
            Description: string.Empty,
            SourceProvenance: WorkflowProfileSourceProvenance.BuiltIn,
            IsBuiltIn: true,
            DefinitionSource: null);
}

public enum WorkflowProfileSourceProvenance
{
    BuiltIn,
    Verbatim,
    CanonicalLegacy,
}

public sealed record WorkflowProfileSaveResult(
    WorkflowProfileCollectionEntry Profile,
    WorkflowDefinitionValidationResult ValidationResult);

public sealed record WorkflowDefinitionValidationResult(
    IReadOnlyList<ValidationError> DefinitionErrors,
    IReadOnlyList<ValidationError> ActionErrors,
    ActionValidationStatus ActionValidationStatus)
{
    public bool HasDefinitionErrors => DefinitionErrors.Count > 0;
    public bool HasActionErrors => ActionErrors.Count > 0;
    public bool IsValid => DefinitionErrors.Count == 0 && ActionErrors.Count == 0;
}
