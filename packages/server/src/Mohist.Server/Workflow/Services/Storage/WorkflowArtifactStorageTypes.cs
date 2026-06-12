namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Limits applied to a single directory artifact write. The runner
/// is expected to enforce the same limits on the workspace side
/// before uploading, but the server validates them again at write
/// time so a misbehaving runner cannot flood the storage root.
/// </summary>
public sealed class WorkflowArtifactDirectoryLimits
{
    public const int DefaultMaxFileCount = 2_000;
    public const long DefaultMaxTotalBytes = 256L * 1024L * 1024L;
    public const long DefaultMaxFileBytes = 64L * 1024L * 1024L;

    /// <summary>Maximum number of regular files inside a directory artifact.</summary>
    public int MaxFileCount { get; set; } = DefaultMaxFileCount;

    /// <summary>
    /// Maximum total bytes across all contained files. Files exceeding
    /// <see cref="MaxFileBytes"/> are also rejected individually.
    /// </summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;

    /// <summary>Maximum size of a single contained file.</summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    public static WorkflowArtifactDirectoryLimits Default => new();
}

/// <summary>
/// Options that control a single file write into artifact storage.
/// </summary>
public sealed class WorkflowArtifactFileWrite
{
    /// <summary>Original source path being captured. Stored as metadata only.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Logical size of the content being written in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Declared MIME content type, if known.</summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Declared content hash (for example <c>sha256:&lt;hex&gt;</c>). The
    /// server stores it as metadata; it does not recompute the hash
    /// when writing. Verification of the upload lives in the binding
    /// flow.
    /// </summary>
    public string? ContentHash { get; set; }
}

/// <summary>
/// One contained file inside a directory artifact. The relative path
/// uses forward slashes and is anchored at the captured directory root.
/// Backslashes, leading separators, and any path-traversal segments
/// (<c>..</c>, absolute roots) are rejected by the storage service.
/// </summary>
public sealed class WorkflowArtifactDirectoryEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ContentType { get; set; }
}

/// <summary>
/// Result returned from <c>WriteFileAsync</c> / <c>WriteDirectoryAsync</c>.
/// Captures the durable storage path (relative to the storage root),
/// the size actually persisted, and the entry count for directories.
/// </summary>
public sealed record WorkflowArtifactStorageWriteResult(
    string StoragePath,
    WorkflowArtifactStorageKind Kind,
    long Size,
    int FileCount);

/// <summary>
/// Result returned from <c>ListDirectoryEntriesAsync</c> for one
/// recorded directory artifact. The relative paths are stable for the
/// recorded artifact version; they are not derived from a live
/// workspace.
/// </summary>
public sealed record WorkflowArtifactDirectoryListing(
    string StoragePath,
    IReadOnlyList<WorkflowArtifactDirectoryEntry> Entries,
    long TotalSize);
