using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.SpecTests.Support;

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
        string taskRunId,
        string artifactId,
        WorkflowArtifactStorageKind kind) =>
        WorkflowArtifactStoragePath.ForArtifact(workflowRunId, taskRunId, artifactId, kind).Value;

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
        IReadOnlyList<WorkflowArtifactDirectoryEntryInput> entries,
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
        if (entries.Count > effectiveLimits.MaxFileCount)
            throw new WorkflowArtifactStorageException(
                $"Directory artifact exceeds file count limit ({entries.Count} > {effectiveLimits.MaxFileCount}).");

        var storedEntries = new Dictionary<string, StoredDirectoryEntry>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in entries.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containedPath = WorkflowArtifactContainedPath.Parse(entry.RelativePath).Value;
            if (entry.Size < 0 || entry.Size > effectiveLimits.MaxFileBytes)
                throw new WorkflowArtifactStorageException($"Directory entry '{entry.RelativePath}' exceeds single-file size limit.");
            if (!storedEntries.TryAdd(containedPath, null!))
                throw new WorkflowArtifactStorageException($"Directory entry '{entry.RelativePath}' appears more than once in a single write.");

            await using var input = entry.OpenContent()
                ?? throw new WorkflowArtifactStorageException($"Content supplier for '{containedPath}' returned a null stream.");
            await using var buffer = new MemoryStream();
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
                cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();

            totalBytes += written;
            storedEntries[containedPath] = new StoredDirectoryEntry(bytes);
        }

        var metadata = CreateMetadata(path, write, recordedAt, "directory", totalBytes, storedEntries.Count);
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
        var path = WorkflowArtifactStoragePath.Parse(storagePath);
        var stored = Get(path.Value);
        if (stored.DirectoryEntries is null)
            throw new WorkflowArtifactNotFoundException($"Recorded directory artifact is missing at '{storagePath}'.");
        var entries = stored.DirectoryEntries
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WorkflowArtifactDirectoryEntry
            {
                RelativePath = pair.Key,
                Size = pair.Value.Content.LongLength,
                ContentType = null,
            })
            .ToArray();
        return Task.FromResult(new WorkflowArtifactDirectoryListing(path.Value, entries, entries.Sum(entry => entry.Size)));
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
        int? fileCount)
    {
        var identity = path.TryReadIdentity();
        return new WorkflowArtifactStorageMetadata
        {
            WorkflowRunId = identity?.WorkflowRunId ?? string.Empty,
            TaskRunId = identity?.TaskRunId ?? string.Empty,
            ArtifactId = identity?.ArtifactId ?? string.Empty,
            Path = write.SourcePath,
            Kind = kind,
            ContentType = write.ContentType,
            ContentHash = write.ContentHash,
            Size = size,
            FileCount = fileCount,
            RecordedAt = recordedAt,
        };
    }

    private static WorkflowArtifactStorageMetadata CopyMetadata(WorkflowArtifactStorageMetadata source) => new()
    {
        WorkflowRunId = source.WorkflowRunId,
        TaskRunId = source.TaskRunId,
        ArtifactId = source.ArtifactId,
        Path = source.Path,
        Kind = source.Kind,
        RecordedAt = source.RecordedAt,
        ContentType = source.ContentType,
        ContentHash = source.ContentHash,
        Size = source.Size,
        FileCount = source.FileCount,
    };

    private sealed record StoredArtifact(
        WorkflowArtifactStorageMetadata Metadata,
        byte[]? FileContent,
        IReadOnlyDictionary<string, StoredDirectoryEntry>? DirectoryEntries);

    private sealed record StoredDirectoryEntry(byte[] Content);
}
