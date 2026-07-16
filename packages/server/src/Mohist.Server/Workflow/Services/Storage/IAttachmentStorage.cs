namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Filesystem-backed storage primitives for user-uploaded attachment
/// content. The service owns the on-disk layout rooted under
/// <c>~/.mohist/attachments/</c> and exposes generated-path, write,
/// read, and metadata operations behind a single swappable interface.
/// </summary>
public interface IAttachmentStorage
{
    /// <summary>
    /// Generates a storage path under
    /// <c>{projectId}/{attachmentId}/content</c>. The returned path is
    /// relative to <see cref="StorageRoot"/> and is safe to persist.
    /// </summary>
    string GenerateStoragePath(string projectId, string attachmentId);

    /// <summary>
    /// Persists a single attachment file atomically to the storage
    /// location's <c>content</c> file and writes <c>metadata.json</c>
    /// alongside it.
    /// </summary>
    Task<AttachmentStorageWriteResult> WriteFileAsync(
        string storagePath,
        Stream content,
        AttachmentFileWrite write,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a read-only stream over recorded content.</summary>
    Stream OpenFileContent(string storagePath);

    /// <summary>
    /// Reads the <c>metadata.json</c> sidecar for a stored attachment.
    /// Returns <c>null</c> when metadata is absent.
    /// </summary>
    Task<AttachmentStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a storage path relative to the storage root into an
    /// absolute filesystem path.
    /// </summary>
    string ResolveAbsolutePath(string storagePath);

    /// <summary>Storage root resolved from configuration.</summary>
    string StorageRoot { get; }
}

public sealed class AttachmentFileWrite
{
    public string OriginalFileName { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long Size { get; set; }

    public long? MaxSize { get; set; }
}

public sealed class AttachmentStorageMetadata
{
    public string ProjectId { get; set; } = string.Empty;

    public string AttachmentId { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long Size { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}

public sealed record AttachmentStorageWriteResult(
    string StoragePath,
    long Size);

public class AttachmentStorageException : Exception
{
    public AttachmentStorageException(string message) : base(message) { }
    public AttachmentStorageException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AttachmentStorageLimitException : AttachmentStorageException
{
    public AttachmentStorageLimitException(string message) : base(message) { }
}

public sealed class AttachmentNotFoundException : AttachmentStorageException
{
    public AttachmentNotFoundException(string message) : base(message) { }
}
