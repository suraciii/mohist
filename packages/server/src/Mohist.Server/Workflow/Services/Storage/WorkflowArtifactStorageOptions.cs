namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Configuration for <see cref="IWorkflowArtifactStorage"/>. The
/// storage root is resolved from <c>Mohist:ArtifactStorage:Root</c>
/// when set; otherwise it defaults to <c>~/.mohist/artifacts</c>.
/// </summary>
public sealed class WorkflowArtifactStorageOptions
{
    public const string SectionName = "Mohist:ArtifactStorage";
    public const string RootEnvironmentVariable = "MOHIST_ARTIFACT_ROOT";

    /// <summary>
    /// Storage root directory. Resolved at construction time. When
    /// <c>null</c>, the default is <c>~/.mohist/artifacts</c>.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>Default directory capture limits.</summary>
    public WorkflowArtifactDirectoryLimits? DirectoryLimits { get; set; }
}
