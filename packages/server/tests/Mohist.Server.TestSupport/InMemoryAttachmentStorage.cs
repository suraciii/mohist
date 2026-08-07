using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryAttachmentStorage : IAttachmentStorage
{
    private const string Root = "/memory/attachments";
    private readonly Dictionary<string, StoredAttachment> _attachments = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string StorageRoot => Root;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _attachments.Count;
            }
        }
    }

    public string GenerateStoragePath(string projectId, string attachmentId)
    {
        ValidateId(projectId, nameof(projectId));
        ValidateId(attachmentId, nameof(attachmentId));
        return $"{projectId}/{attachmentId}/content";
    }

    public async Task<AttachmentStorageWriteResult> WriteFileAsync(
        string storagePath,
        Stream content,
        AttachmentFileWrite write,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(write);
        var normalized = ParseStoragePath(storagePath);
        if (write.Size < 0)
            throw new AttachmentStorageException($"Declared size {write.Size} is negative.");

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (write.MaxSize is { } limit && bytes.LongLength > limit)
            throw new AttachmentStorageLimitException($"Attachment upload exceeds the configured size limit of {limit} bytes.");
        if (write.Size > 0 && bytes.LongLength != write.Size)
            throw new AttachmentStorageException(
                $"Content size mismatch for '{storagePath}': declared {write.Size} bytes, wrote {bytes.LongLength} bytes.");

        var segments = normalized.Split('/');
        var stored = new StoredAttachment(bytes, new AttachmentStorageMetadata
        {
            ProjectId = segments[0],
            AttachmentId = segments[1],
            OriginalFileName = write.OriginalFileName,
            ContentType = write.ContentType,
            Size = bytes.LongLength,
            RecordedAt = recordedAt,
        });

        lock (_gate)
        {
            if (!_attachments.TryAdd(normalized, stored))
                throw new AttachmentStorageException(
                    $"Attachment storage path '{storagePath}' already exists; refusing to overwrite a recorded attachment.");
        }

        return new AttachmentStorageWriteResult(normalized, bytes.LongLength);
    }

    public Stream OpenFileContent(string storagePath)
    {
        var normalized = ParseStoragePath(storagePath);
        lock (_gate)
        {
            if (!_attachments.TryGetValue(normalized, out var stored))
                throw new AttachmentNotFoundException($"Recorded attachment content is missing at '{storagePath}'.");
            return new MemoryStream(stored.Content, writable: false);
        }
    }

    public Task<AttachmentStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ParseStoragePath(storagePath);
        lock (_gate)
        {
            return Task.FromResult(_attachments.TryGetValue(normalized, out var stored)
                ? CopyMetadata(stored.Metadata)
                : null);
        }
    }

    public Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ParseStoragePath(storagePath);
        lock (_gate)
        {
            _attachments.Remove(normalized);
        }
        return Task.CompletedTask;
    }

    public string ResolveAbsolutePath(string storagePath) => $"{Root}/{ParseStoragePath(storagePath)}";

    public bool Contains(string storagePath)
    {
        var normalized = ParseStoragePath(storagePath);
        lock (_gate)
        {
            return _attachments.ContainsKey(normalized);
        }
    }

    /// <summary>
    /// Removes the metadata for the given storage path so the next
    /// <see cref="ReadMetadataAsync"/> returns null. Used to simulate
    /// a storage backend that no longer serves a previously-uploaded
    /// attachment. The bytes remain in the fake so other storage
    /// tests can still observe the path.
    /// </summary>
    public void MarkUnreadable(string storagePath)
    {
        var normalized = ParseStoragePath(storagePath);
        lock (_gate)
        {
            if (_attachments.TryGetValue(normalized, out var stored))
            {
                _attachments[normalized] = stored with { Metadata = null! };
            }
        }
    }

    private static string ParseStoragePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new AttachmentStorageException("Storage path must be provided.");
        var normalized = storagePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(storagePath))
            throw new AttachmentStorageException($"Storage path '{storagePath}' must be relative to the attachment storage root.");
        var segments = normalized.Split('/');
        if (segments.Length != 3 || segments.Any(segment => segment.Length == 0 || segment is "." or "..") || segments[2] != "content")
            throw new AttachmentStorageException($"Attachment storage path '{storagePath}' is invalid.");
        return normalized;
    }

    private static void ValidateId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(ch => ch is '/' or '\\' or '\0' or ' ' or ':'))
            throw new AttachmentStorageException($"{paramName} is invalid.");
    }

    private static AttachmentStorageMetadata CopyMetadata(AttachmentStorageMetadata source) => new()
    {
        ProjectId = source.ProjectId,
        AttachmentId = source.AttachmentId,
        OriginalFileName = source.OriginalFileName,
        ContentType = source.ContentType,
        Size = source.Size,
        RecordedAt = source.RecordedAt,
    };

    private sealed record StoredAttachment(byte[] Content, AttachmentStorageMetadata Metadata);
}
