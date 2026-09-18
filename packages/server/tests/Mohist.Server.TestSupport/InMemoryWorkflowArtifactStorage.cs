using System.Security.Cryptography;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryWorkflowArtifactStorage : IWorkflowArtifactStorage
{
    private const string Root = "/memory/artifacts";
    private readonly Dictionary<string, StoredArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public CancellationToken? LastDeleteCancellationToken { get; private set; }
    public Action? BeforeDelete { get; set; }
    public string StorageRoot => Root;

    public string GenerateStoragePath(
        string workflowRunId,
        string actionAttemptId,
        string artifactId,
        WorkflowArtifactStorageKind kind) =>
        WorkflowArtifactStoragePath.ForArtifact(workflowRunId, actionAttemptId, artifactId, kind).Value;

    public async Task<WorkflowArtifactStorageWriteResult> WriteFileAsync(
        string storagePath,
        Stream content,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(write);
        var path = WorkflowArtifactStoragePath.Parse(storagePath);
        if (!path.IsFileContent)
            throw new WorkflowArtifactStorageException($"File artifact storage path '{storagePath}' must end with 'content'.");
        if (write.Size < 0)
            throw new WorkflowArtifactStorageException($"Declared size {write.Size} is negative.");

        await using var buffer = new MemoryStream();
        await WorkflowArtifactStreamCopier.CopyAsync(
            content,
            buffer,
            write.Size,
            maxBytes: null,
            storagePath,
            cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var metadata = CreateMetadata(path, write, recordedAt, "file", bytes.LongLength, null);
        Add(path.Value, new StoredArtifact(metadata, bytes, null));
        return new WorkflowArtifactStorageWriteResult(path.Value, WorkflowArtifactStorageKind.File, bytes.LongLength, 1);
    }

    public async Task<WorkflowArtifactStorageWriteResult> WriteDirectoryAsync(
        string storagePath,
        IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> entries,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        WorkflowArtifactDirectoryLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(write);
        var path = WorkflowArtifactStoragePath.Parse(storagePath);
        if (!path.IsDirectoryFiles)
            throw new WorkflowArtifactStorageException($"Directory artifact storage path '{storagePath}' must end with 'files'.");

        var effectiveLimits = limits ?? WorkflowArtifactDirectoryLimits.Default;

        // Pull one entry at a time: validate, copy, and record it before
        // asking the stream for the next. Nothing is registered until the
        // whole stream succeeds, so a mid-stream rejection leaves the
        // storage path retryable.
        var storedEntries = new Dictionary<string, StoredDirectoryEntry>(StringComparer.Ordinal);
        var manifest = new List<WorkflowArtifactDirectoryEntry>();
        long declaredTotalBytes = 0;
        long totalBytes = 0;
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var entry in entries
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (entry is null)
                throw new WorkflowArtifactStorageException("Directory entry is null.");

            var containedPath = WorkflowArtifactContainedPath.Parse(entry.RelativePath).Value;
            if (!seenPaths.Add(containedPath))
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' appears more than once in a single write.");

            if (manifest.Count >= effectiveLimits.MaxFileCount)
                throw new WorkflowArtifactStorageException(
                    $"Directory artifact exceeds file count limit ({manifest.Count + 1} > {effectiveLimits.MaxFileCount}).");

            if (entry.Size < 0)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' has a negative declared size ({entry.Size}).");
            if (entry.Size > effectiveLimits.MaxFileBytes)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' exceeds single-file size limit ({entry.Size} > {effectiveLimits.MaxFileBytes}).");
            if (declaredTotalBytes + entry.Size > effectiveLimits.MaxTotalBytes)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' would exceed total size limit ({effectiveLimits.MaxTotalBytes}).");
            declaredTotalBytes += entry.Size;

            await using var input = entry.OpenContent()
                ?? throw new WorkflowArtifactStorageException($"Content supplier for '{containedPath}' returned a null stream.");
            await using var buffer = new MemoryStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remainingTotalBytes = effectiveLimits.MaxTotalBytes - totalBytes;
            var maximumBytes = Math.Min(
                effectiveLimits.MaxFileBytes,
                remainingTotalBytes);
            var written = await WorkflowArtifactStreamCopier.CopyAsync(
                input,
                buffer,
                entry.Size,
                maximumBytes,
                containedPath,
                cancellationToken,
                hash).ConfigureAwait(false);
            var bytes = buffer.ToArray();

            totalBytes += written;

            var computedHash = WorkflowArtifactContentHash.Complete(hash);
            if (!string.IsNullOrWhiteSpace(entry.ContentHash)
                && !string.Equals(entry.ContentHash, computedHash, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowArtifactStorageException(
                    $"Content hash mismatch for directory entry '{containedPath}': declared '{entry.ContentHash}', wrote '{computedHash}'.");

            storedEntries[containedPath] = new StoredDirectoryEntry(bytes);
            manifest.Add(new WorkflowArtifactDirectoryEntry
            {
                RelativePath = containedPath,
                Size = written,
                ContentHash = computedHash,
                ContentType = entry.ContentType,
            });
        }

        if (manifest.Count == 0)
            throw new WorkflowArtifactStorageException(
                "Directory artifact must contain at least one contained file.");

        // Written in arrival order; the durable manifest is sorted by ordinal
        // relative path to keep the listing contract stable.
        manifest.Sort(static (left, right) =>
            string.CompareOrdinal(left.RelativePath, right.RelativePath));

        var metadata = CreateMetadata(path, write, recordedAt, "directory", totalBytes, storedEntries.Count, manifest);
        Add(path.Value, new StoredArtifact(metadata, null, storedEntries));
        return new WorkflowArtifactStorageWriteResult(
            path.Value,
            WorkflowArtifactStorageKind.Directory,
            totalBytes,
            storedEntries.Count);
    }

    public Stream OpenFileContent(string storagePath)
    {
        var stored = Get(storagePath);
        if (stored.FileContent is null)
            throw new WorkflowArtifactNotFoundException($"Recorded artifact content is missing at '{storagePath}'.");
        return new MemoryStream(stored.FileContent, writable: false);
    }

    public Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = WorkflowArtifactStoragePath.Parse(storagePath).Value;
        var stored = Get(path);
        // The listing is the recorded manifest, not a re-hash of the
        // bytes currently held. Tampered bytes must not become the new
        // expectation reported to consumers.
        return Task.FromResult(
            WorkflowArtifactDirectoryListingFactory.FromMetadata(path, stored.Metadata));
    }

    public Stream OpenDirectoryEntry(string storagePath, string relativePath)
    {
        var stored = Get(storagePath);
        var normalized = WorkflowArtifactContainedPath.Parse(relativePath).Value;
        if (stored.DirectoryEntries is null || !stored.DirectoryEntries.TryGetValue(normalized, out var entry))
            throw new WorkflowArtifactNotFoundException($"Recorded directory entry '{relativePath}' is missing.");
        return new MemoryStream(entry.Content, writable: false);
    }

    public Task<WorkflowArtifactStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = WorkflowArtifactStoragePath.Parse(storagePath).Value;
        lock (_gate)
        {
            return Task.FromResult(_artifacts.TryGetValue(path, out var stored)
                ? CopyMetadata(stored.Metadata)
                : null);
        }
    }

    public Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        LastDeleteCancellationToken = cancellationToken;
        BeforeDelete?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var path = WorkflowArtifactStoragePath.Parse(storagePath).Value;
        lock (_gate)
        {
            _artifacts.Remove(path);
        }
        return Task.CompletedTask;
    }

    public string ResolveAbsolutePath(string storagePath) =>
        $"{Root}/{WorkflowArtifactStoragePath.Parse(storagePath).Value}";

    public bool Contains(string storagePath)
    {
        var path = WorkflowArtifactStoragePath.Parse(storagePath).Value;
        lock (_gate)
        {
            return _artifacts.ContainsKey(path);
        }
    }

    /// <summary>
    /// Test-only seam that replaces the stored bytes of one directory
    /// entry without touching its recorded manifest. Lets a Spec prove
    /// the listing expectation is independent of current content.
    /// </summary>
    public void MutateDirectoryEntryContent(string storagePath, string relativePath, byte[] content)
    {
        var path = WorkflowArtifactStoragePath.Parse(storagePath).Value;
        var normalized = WorkflowArtifactContainedPath.Parse(relativePath).Value;
        lock (_gate)
        {
            if (!_artifacts.TryGetValue(path, out var stored) || stored.DirectoryEntries is null)
                throw new WorkflowArtifactNotFoundException(
                    $"Recorded directory artifact is missing at '{storagePath}'.");
            stored.DirectoryEntries[normalized] = new StoredDirectoryEntry(content);
        }
    }

    private void Add(string storagePath, StoredArtifact artifact)
    {
        lock (_gate)
        {
            if (!_artifacts.TryAdd(storagePath, artifact))
                throw new WorkflowArtifactStorageException(
                    $"Artifact storage path '{storagePath}' already exists; refusing to overwrite a recorded artifact.");
        }
    }

    private StoredArtifact Get(string storagePath)
    {
        var path = WorkflowArtifactStoragePath.Parse(storagePath).Value;
        lock (_gate)
        {
            return _artifacts.TryGetValue(path, out var stored)
                ? stored
                : throw new WorkflowArtifactNotFoundException($"Recorded artifact content is missing at '{storagePath}'.");
        }
    }

    private static WorkflowArtifactStorageMetadata CreateMetadata(
        WorkflowArtifactStoragePath path,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        string kind,
        long size,
        int? fileCount,
        IReadOnlyList<WorkflowArtifactDirectoryEntry>? entries = null)
    {
        var identity = path.TryReadIdentity();
        return new WorkflowArtifactStorageMetadata
        {
            WorkflowRunId = identity?.WorkflowRunId ?? string.Empty,
            ActionAttemptId = identity?.ActionAttemptId ?? string.Empty,
            ArtifactId = identity?.ArtifactId ?? string.Empty,
            Path = write.SourcePath,
            Kind = kind,
            ContentType = write.ContentType,
            ContentHash = write.ContentHash,
            Size = size,
            FileCount = fileCount,
            Entries = entries,
            RecordedAt = recordedAt,
        };
    }

    private static WorkflowArtifactStorageMetadata CopyMetadata(WorkflowArtifactStorageMetadata source) => new()
    {
        WorkflowRunId = source.WorkflowRunId,
        ActionAttemptId = source.ActionAttemptId,
        ArtifactId = source.ArtifactId,
        Path = source.Path,
        Kind = source.Kind,
        RecordedAt = source.RecordedAt,
        ContentType = source.ContentType,
        ContentHash = source.ContentHash,
        Size = source.Size,
        FileCount = source.FileCount,
        Entries = source.Entries?
            .Select(entry => new WorkflowArtifactDirectoryEntry
            {
                RelativePath = entry.RelativePath,
                Size = entry.Size,
                ContentHash = entry.ContentHash,
                ContentType = entry.ContentType,
            })
            .ToArray(),
    };

    private sealed record StoredArtifact(
        WorkflowArtifactStorageMetadata Metadata,
        byte[]? FileContent,
        Dictionary<string, StoredDirectoryEntry>? DirectoryEntries);

    private sealed record StoredDirectoryEntry(byte[] Content);
}
