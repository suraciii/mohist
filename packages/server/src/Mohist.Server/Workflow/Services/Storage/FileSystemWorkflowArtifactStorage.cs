using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Default <see cref="IWorkflowArtifactStorage"/> implementation. It writes
/// recorded content under generated paths that never embed source artifact
/// paths.
/// </summary>
/// <remarks>
/// Writes are committed by flushing a temporary file and then renaming it into
/// place, so partial files are not exposed as recorded artifact content.
/// </remarks>
public sealed class FileSystemWorkflowArtifactStorage : IWorkflowArtifactStorage
{
    public const string MetadataFileName = WorkflowArtifactStorageLayout.MetadataFileName;
    public const string FileContentName = WorkflowArtifactStorageLayout.FileContentName;
    public const string DirectoryFilesName = WorkflowArtifactStorageLayout.DirectoryFilesName;
    public const string StorageRootName = WorkflowArtifactStorageLayout.StorageRootName;
    public const string WorkflowSegment = WorkflowArtifactStorageLayout.WorkflowSegment;

    private readonly ILogger<FileSystemWorkflowArtifactStorage> _log;
    private readonly WorkflowArtifactDirectoryLimits _defaultLimits;
    private readonly string _root;

    public FileSystemWorkflowArtifactStorage(
        IOptions<WorkflowArtifactStorageOptions> options,
        ILogger<FileSystemWorkflowArtifactStorage> log)
    {
        _log = log;
        var configured = options.Value;
        _root = ResolveStorageRoot(configured);
        _defaultLimits = configured.DirectoryLimits ?? WorkflowArtifactDirectoryLimits.Default;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Test-only constructor that bypasses the options pipeline.</summary>
    public FileSystemWorkflowArtifactStorage(string root, ILogger<FileSystemWorkflowArtifactStorage> log)
        : this(root, log, WorkflowArtifactDirectoryLimits.Default)
    {
    }

    /// <summary>Test-only constructor that accepts explicit directory limits.</summary>
    public FileSystemWorkflowArtifactStorage(
        string root,
        ILogger<FileSystemWorkflowArtifactStorage> log,
        WorkflowArtifactDirectoryLimits defaultLimits)
    {
        _log = log;
        _defaultLimits = defaultLimits;
        _root = ResolveStorageRoot(new WorkflowArtifactStorageOptions { Root = root });
        Directory.CreateDirectory(_root);
    }

    public string StorageRoot => _root;

    public string ResolveAbsolutePath(string storagePath)
    {
        var normalized = WorkflowArtifactStoragePath.Parse(storagePath);
        return Path.GetFullPath(Path.Combine(_root, normalized.Value.Replace('/', Path.DirectorySeparatorChar)));
    }

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
        cancellationToken.ThrowIfCancellationRequested();

        var directory = EnsureStorageDirectoryForFile(storagePath);
        if (Directory.Exists(directory))
            throw new WorkflowArtifactStorageException(
                $"Artifact storage directory '{directory}' already exists; refusing to overwrite a recorded artifact.");

        Directory.CreateDirectory(directory);

        var metadata = new WorkflowArtifactStorageMetadata
        {
            Path = write.SourcePath,
            Kind = "file",
            ContentType = write.ContentType,
            ContentHash = write.ContentHash,
            Size = write.Size,
            RecordedAt = recordedAt,
        };
        PopulateIdentityMetadata(metadata, storagePath);

        var contentPath = Path.Combine(directory, FileContentName);
        try
        {
            var written = await WriteStreamAsync(
                    contentPath,
                    content,
                    write.Size,
                    maxBytes: null,
                    cancellationToken)
                .ConfigureAwait(false);

            metadata.Size = written;
            await WriteMetadataAsync(directory, metadata, cancellationToken).ConfigureAwait(false);

            _log.LogDebug(
                "Persisted file artifact {Storage} ({Bytes} bytes, source '{Source}')",
                storagePath, written, write.SourcePath);

            return new WorkflowArtifactStorageWriteResult(
                StoragePath: storagePath,
                Kind: WorkflowArtifactStorageKind.File,
                Size: written,
                FileCount: 1);
        }
        catch
        {
            // The directory was just created and may now contain a
            // half-written content file. Remove it so the next binding
            // attempt against the same path starts clean.
            TryRemoveDirectory(directory);
            throw;
        }
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
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveLimits = limits ?? _defaultLimits;
        var filesRoot = EnsureStorageDirectoryForDirectory(storagePath);
        if (Directory.Exists(filesRoot))
            throw new WorkflowArtifactStorageException(
                $"Artifact storage directory '{filesRoot}' already exists; refusing to overwrite a recorded artifact.");

        var collectionRoot = Path.GetDirectoryName(filesRoot)
            ?? throw new WorkflowArtifactStorageException(
                $"Unable to resolve collection root for '{storagePath}'.");

        // Validate every declared value before touching the filesystem. A
        // rejection here must not strand a directory or metadata file that
        // would block a later retry against the same storage path.
        var validated = ValidateDirectoryEntries(entries, effectiveLimits);

        try
        {
            Directory.CreateDirectory(filesRoot);

            long totalBytes = 0;
            var manifest = new List<WorkflowArtifactDirectoryEntry>(validated.Count);
            foreach (var (entry, normalizedPath) in validated)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = Path.Combine(
                    filesRoot,
                    normalizedPath.Replace('/', Path.DirectorySeparatorChar));
                var resolvedDestination = Path.GetFullPath(destination);
                if (!resolvedDestination.StartsWith(
                        EnsureTrailingSeparator(filesRoot),
                        StringComparison.Ordinal))
                    throw new WorkflowArtifactStorageException(
                        $"Directory entry '{entry.RelativePath}' resolves outside the artifact collection.");

                var destinationDir = Path.GetDirectoryName(resolvedDestination);
                if (!string.IsNullOrEmpty(destinationDir))
                    Directory.CreateDirectory(destinationDir);

                // Hash the bytes while they are streamed so the recorded
                // digest describes exactly what was written, without a
                // second read that could race a mutation.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long written;
                await using (var input = SafeOpenContent(entry, normalizedPath))
                {
                    var remainingTotalBytes = effectiveLimits.MaxTotalBytes - totalBytes;
                    var maximumBytes = Math.Min(
                        effectiveLimits.MaxFileBytes,
                        remainingTotalBytes);
                    written = await WriteStreamAsync(
                            resolvedDestination,
                            input,
                            entry.Size,
                            maximumBytes,
                            cancellationToken,
                            hash)
                        .ConfigureAwait(false);
                }
                totalBytes += written;

                var computedHash = WorkflowArtifactContentHash.Complete(hash);
                if (!string.IsNullOrWhiteSpace(entry.ContentHash)
                    && !string.Equals(entry.ContentHash, computedHash, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowArtifactStorageException(
                        $"Content hash mismatch for directory entry '{normalizedPath}': declared '{entry.ContentHash}', wrote '{computedHash}'.");

                manifest.Add(new WorkflowArtifactDirectoryEntry
                {
                    RelativePath = normalizedPath,
                    Size = written,
                    ContentHash = computedHash,
                    ContentType = entry.ContentType,
                });
            }

            var metadata = new WorkflowArtifactStorageMetadata
            {
                Path = write.SourcePath,
                Kind = "directory",
                ContentType = write.ContentType,
                ContentHash = write.ContentHash,
                Size = totalBytes,
                FileCount = manifest.Count,
                Entries = manifest,
                RecordedAt = recordedAt,
            };
            PopulateIdentityMetadata(metadata, storagePath);

            await WriteMetadataAsync(collectionRoot, metadata, cancellationToken).ConfigureAwait(false);

            _log.LogDebug(
                "Persisted directory artifact {Storage} ({Files} files, {Bytes} bytes, source '{Source}')",
                storagePath, manifest.Count, totalBytes, write.SourcePath);

            return new WorkflowArtifactStorageWriteResult(
                StoragePath: storagePath,
                Kind: WorkflowArtifactStorageKind.Directory,
                Size: totalBytes,
                FileCount: manifest.Count);
        }
        catch
        {
            // Remove the collection directory (and any partially
            // written files) so the next binding attempt against the
            // same path starts clean.
            TryRemoveDirectory(collectionRoot);
            throw;
        }
    }

    private static IReadOnlyList<(WorkflowArtifactDirectoryEntryInput Entry, string NormalizedPath)> ValidateDirectoryEntries(
        IReadOnlyList<WorkflowArtifactDirectoryEntryInput> entries,
        WorkflowArtifactDirectoryLimits limits)
    {
        if (entries.Count > limits.MaxFileCount)
            throw new WorkflowArtifactStorageException(
                $"Directory artifact exceeds file count limit ({entries.Count} > {limits.MaxFileCount}).");

        long declaredTotalBytes = 0;
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<(WorkflowArtifactDirectoryEntryInput Entry, string NormalizedPath)>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry at index {index} is null.");

            var containedPath = WorkflowArtifactContainedPath.Parse(entry.RelativePath);
            if (!seenPaths.Add(containedPath.Value))
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' appears more than once in a single write.");

            if (entry.Size < 0)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' has a negative declared size ({entry.Size}).");
            if (entry.Size > limits.MaxFileBytes)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' exceeds single-file size limit ({entry.Size} > {limits.MaxFileBytes}).");
            if (declaredTotalBytes + entry.Size > limits.MaxTotalBytes)
                throw new WorkflowArtifactStorageException(
                    $"Directory entry '{entry.RelativePath}' would exceed total size limit ({limits.MaxTotalBytes}).");

            declaredTotalBytes += entry.Size;
            validated.Add((entry, containedPath.Value));
        }

        return validated
            .OrderBy(value => value.NormalizedPath, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryRemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best-effort cleanup; the binding flow will surface a
            // clear error on the next attempt if the partial state
            // is still in the way.
        }
    }

    public Stream OpenFileContent(string storagePath)
    {
        var contentPath = ResolveAbsoluteFileContentPath(storagePath);
        if (!File.Exists(contentPath))
            throw new WorkflowArtifactNotFoundException(
                $"Recorded artifact content is missing at '{contentPath}'.");
        return new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filesRoot = ResolveAbsoluteDirectoryFilesPath(storagePath);
        if (!Directory.Exists(filesRoot))
            throw new WorkflowArtifactNotFoundException(
                $"Recorded directory artifact is missing at '{filesRoot}'.");

        // Serve the upload-time expectation recorded in the manifest.
        // Enumerating files/ would make the expectation a function of
        // whatever bytes happen to be on disk now.
        var metadata = await ReadMetadataAsync(storagePath, cancellationToken).ConfigureAwait(false);
        return WorkflowArtifactDirectoryListingFactory.FromMetadata(storagePath, metadata);
    }

    public Stream OpenDirectoryEntry(string storagePath, string relativePath)
    {
        var filesRoot = ResolveAbsoluteDirectoryFilesPath(storagePath);
        var normalized = WorkflowArtifactContainedPath.Parse(relativePath);
        var destination = Path.GetFullPath(Path.Combine(
            filesRoot,
            normalized.Value.Replace('/', Path.DirectorySeparatorChar)));
        var safeRoot = EnsureTrailingSeparator(filesRoot);
        if (!destination.StartsWith(safeRoot, StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Relative path '{relativePath}' resolves outside the artifact collection.");
        if (!File.Exists(destination))
            throw new WorkflowArtifactNotFoundException(
                $"Recorded directory entry '{relativePath}' is missing.");
        return new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<WorkflowArtifactStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = ResolveAbsolutePath(storagePath);
        // The metadata file lives in the artifact collection
        // directory, one level above the leaf segment (`content`
        // for files, `files/` for directories).
        var collectionRoot = Path.GetDirectoryName(absolute) ?? absolute;
        var metadataPath = Path.Combine(collectionRoot, MetadataFileName);
        if (!File.Exists(metadataPath))
            return null;
        var json = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        return WorkflowArtifactDirectoryListingFactory.ParseMetadata(storagePath, json);
    }

    public Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = ResolveAbsolutePath(storagePath);
        var directory = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static string ResolveStorageRoot(WorkflowArtifactStorageOptions options)
    {
        var configured = options.Root;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configured = Path.Combine(home, ".mohist", StorageRootName);
        }
        return Path.GetFullPath(configured);
    }

    private string EnsureStorageDirectoryForFile(string storagePath)
    {
        var absolute = ResolveAbsolutePath(storagePath);
        var parent = Path.GetDirectoryName(absolute)
            ?? throw new WorkflowArtifactStorageException(
                $"Storage path '{storagePath}' has no parent directory.");
        if (Path.GetFileName(absolute) != FileContentName)
            throw new WorkflowArtifactStorageException(
                $"File artifact storage path '{storagePath}' must end with '{FileContentName}'.");
        return parent;
    }

    private string EnsureStorageDirectoryForDirectory(string storagePath)
    {
        var absolute = ResolveAbsolutePath(storagePath);
        if (!storagePath.EndsWith("/" + DirectoryFilesName, StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Directory artifact storage path '{storagePath}' must end with '{DirectoryFilesName}/'.");
        return absolute;
    }

    private string ResolveAbsoluteFileContentPath(string storagePath)
    {
        var absolute = ResolveAbsolutePath(storagePath);
        if (Path.GetFileName(absolute) != FileContentName)
            throw new WorkflowArtifactStorageException(
                $"Storage path '{storagePath}' does not point at a file artifact.");
        return absolute;
    }

    private string ResolveAbsoluteDirectoryFilesPath(string storagePath)
    {
        var absolute = ResolveAbsolutePath(storagePath);
        var parent = Path.GetDirectoryName(absolute);
        if (parent is null
            || !storagePath.EndsWith("/" + DirectoryFilesName, StringComparison.Ordinal))
            throw new WorkflowArtifactStorageException(
                $"Storage path '{storagePath}' does not point at a directory artifact.");
        var filesRoot = Path.Combine(parent, DirectoryFilesName);
        return filesRoot;
    }

    private static async Task<long> WriteStreamAsync(
        string destination,
        Stream source,
        long declaredSize,
        long? maxBytes,
        CancellationToken cancellationToken,
        IncrementalHash? hash = null)
    {
        var tempPath = destination + ".tmp";
        long written = 0;
        bool committed = false;
        try
        {
            // The FileStream is opened and closed inside the using
            // block. The atomic move is performed after the stream is
            // fully disposed so that platforms that hold a write
            // lock on the file (Windows in particular) do not block
            // the rename.
            await using (var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                written = await WorkflowArtifactStreamCopier.CopyAsync(
                        source,
                        output,
                        declaredSize,
                        maxBytes,
                        destination,
                        cancellationToken,
                        hash)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(tempPath, destination);
            committed = true;
            return written;
        }
        finally
        {
            if (!committed && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private static Stream SafeOpenContent(WorkflowArtifactDirectoryEntryInput entry, string normalizedRelative)
    {
        Stream stream;
        try
        {
            stream = entry.OpenContent();
        }
        catch (Exception ex)
        {
            throw new WorkflowArtifactStorageException(
                $"Failed to open content for directory entry '{normalizedRelative}'.", ex);
        }
        return stream ?? throw new WorkflowArtifactStorageException(
            $"Content supplier for '{normalizedRelative}' returned a null stream.");
    }

    private async Task WriteMetadataAsync(
        string directory,
        WorkflowArtifactStorageMetadata metadata,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var tempPath = metadataPath + ".tmp";
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, metadata, JSON.Indented, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                if (File.Exists(metadataPath))
                    File.Delete(metadataPath);
                File.Move(tempPath, metadataPath);
            }
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static void PopulateIdentityMetadata(WorkflowArtifactStorageMetadata metadata, string storagePath)
    {
        if (WorkflowArtifactStoragePath.Parse(storagePath).TryReadIdentity() is not { } identity)
            return;

        metadata.WorkflowRunId = identity.WorkflowRunId;
        metadata.ActionAttemptId = identity.ActionAttemptId;
        metadata.ArtifactId = identity.ArtifactId;
    }
}
