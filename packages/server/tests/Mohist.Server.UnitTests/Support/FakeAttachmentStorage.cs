using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.UnitTests.Support;

/// <summary>
/// In-memory <see cref="IAttachmentStorage"/> for unit tests. Mirrors the
/// SpecTests in-memory fake but keeps the production boundary
/// (<see cref="IAttachmentStorage"/>) intact and uses pure in-process state,
/// so tests never touch the host filesystem.
/// </summary>
public sealed class FakeAttachmentStorage : IAttachmentStorage
{
    private const string Root = "/memory/unit-test-attachments";
    private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttachmentStorageMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string StorageRoot => Root;

    public int Count
    {
        get
        {
            lock (_gate) return _content.Count;
        }
    }

    public string GenerateStoragePath(string projectId, string attachmentId) =>
        $"{projectId}/{attachmentId}/content";

    public async Task<AttachmentStorageWriteResult> WriteFileAsync(
        string storagePath,
        Stream content,
        AttachmentFileWrite write,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(write);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (write.MaxSize is { } limit && bytes.LongLength > limit)
            throw new AttachmentStorageLimitException($"Attachment upload exceeds the configured size limit of {limit} bytes.");
        var metadata = new AttachmentStorageMetadata
        {
            ProjectId = ReadSegment(storagePath, 0),
            AttachmentId = ReadSegment(storagePath, 1),
            OriginalFileName = write.OriginalFileName,
            ContentType = write.ContentType,
            Size = bytes.LongLength,
            RecordedAt = recordedAt,
        };
        lock (_gate)
        {
            _content[storagePath] = bytes;
            _metadata[storagePath] = metadata;
        }
        return new AttachmentStorageWriteResult(storagePath, bytes.LongLength);
    }

    public Stream OpenFileContent(string storagePath)
    {
        lock (_gate)
        {
            if (!_content.TryGetValue(storagePath, out var bytes))
                throw new AttachmentNotFoundException($"Recorded attachment content is missing at '{storagePath}'.");
            return new MemoryStream(bytes, writable: false);
        }
    }

    public Task<AttachmentStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_metadata.TryGetValue(storagePath, out var meta) ? meta : null);
        }
    }

    public Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _content.Remove(storagePath);
            _metadata.Remove(storagePath);
        }
        return Task.CompletedTask;
    }

    public string ResolveAbsolutePath(string storagePath) => $"{Root}/{storagePath}";

    public bool Contains(string storagePath)
    {
        lock (_gate)
        {
            return _content.ContainsKey(storagePath);
        }
    }

    /// <summary>
    /// Marks the recorded metadata for the given storage path as
    /// unreadable so the next <see cref="ReadMetadataAsync"/> returns
    /// null. Used to simulate a storage backend that no longer serves
    /// a previously-uploaded attachment. The bytes remain in the fake
    /// so other storage tests can still observe the path.
    /// </summary>
    public void MarkUnreadable(string storagePath)
    {
        lock (_gate)
        {
            _metadata.Remove(storagePath);
        }
    }

    private static string ReadSegment(string storagePath, int index)
    {
        var segments = storagePath.Split('/');
        return index < segments.Length ? segments[index] : string.Empty;
    }
}