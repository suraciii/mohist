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
    private readonly IStorageFileSystem _files;

    public FileSystemWorkflowArtifactStorage(
        IOptions<WorkflowArtifactStorageOptions> options,
        ILogger<FileSystemWorkflowArtifactStorage> log)
        : this(options, log, PhysicalStorageFileSystem.Instance)
    {
    }

    internal FileSystemWorkflowArtifactStorage(
        IOptions<WorkflowArtifactStorageOptions> options,
        ILogger<FileSystemWorkflowArtifactStorage> log,
        IStorageFileSystem files)
    {
        _log = log;
        _files = files;
        var configured = options.Value;
        _root = ResolveStorageRoot(configured);
        _defaultLimits = configured.DirectoryLimits ?? WorkflowArtifactDirectoryLimits.Default;
        _files.CreateDirectory(_root);
    }

    /// <summary>Test-only constructor that bypasses the options pipeline.</summary>
    public FileSystemWorkflowArtifactStorage(string root, ILogger<FileSystemWorkflowArtifactStorage> log)
        : this(root, log, WorkflowArtifactDirectoryLimits.Default, PhysicalStorageFileSystem.Instance)
    {
    }

    internal FileSystemWorkflowArtifactStorage(
        string root,
        ILogger<FileSystemWorkflowArtifactStorage> log,
        IStorageFileSystem files)
        : this(root, log, WorkflowArtifactDirectoryLimits.Default, files)
    {
    }

    /// <summary>Test-only constructor that accepts explicit directory limits.</summary>
    public FileSystemWorkflowArtifactStorage(
        string root,
        ILogger<FileSystemWorkflowArtifactStorage> log,
        WorkflowArtifactDirectoryLimits defaultLimits)
        : this(root, log, defaultLimits, PhysicalStorageFileSystem.Instance)
    {
    }

    internal FileSystemWorkflowArtifactStorage(
        string root,
        ILogger<FileSystemWorkflowArtifactStorage> log,
        WorkflowArtifactDirectoryLimits defaultLimits,
        IStorageFileSystem files)
    {
        _log = log;
        _files = files;
        _defaultLimits = defaultLimits;
        _root = ResolveStorageRoot(new WorkflowArtifactStorageOptions { Root = root });
        _files.CreateDirectory(_root);
    }

    public string StorageRoot => _root;

    public string ResolveAbsolutePath(string storagePath)
    {
        var normalized = WorkflowArtifactStoragePath.Parse(storagePath);
        return Path.GetFullPath(Path.Combine(_root, normalized.Value.Replace('/', Path.DirectorySeparatorChar)));
    }

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
        cancellationToken.ThrowIfCancellationRequested();

        var directory = EnsureStorageDirectoryForFile(storagePath);
        if (_files.DirectoryExists(directory))
            throw new WorkflowArtifactStorageException(
                $"Artifact storage directory '{directory}' already exists; refusing to overwrite a recorded artifact.");

        _files.CreateDirectory(directory);

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
            var written = await WriteStreamAsync(contentPath, content, write.Size, cancellationToken)
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
        if (_files.DirectoryExists(filesRoot))
            throw new WorkflowArtifactStorageException(
                $"Artifact storage directory '{filesRoot}' already exists; refusing to overwrite a recorded artifact.");

        _files.CreateDirectory(filesRoot);

        var collectionRoot = Path.GetDirectoryName(filesRoot)
            ?? throw new WorkflowArtifactStorageException(
                $"Unable to resolve collection root for '{storagePath}'.");

        if (entries.Count > effectiveLimits.MaxFileCount)
            throw new WorkflowArtifactStorageException(
                $"Directory artifact exceeds file count limit ({entries.Count} > {effectiveLimits.MaxFileCount}).");

        long totalBytes = 0;
        var sortedEntries = entries
            .Select((entry, index) => (entry, index))
            .OrderBy(t => t.entry.RelativePath, StringComparer.Ordinal)
            .ToList();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var (entry, index) in sortedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null)
                    throw new WorkflowArtifactStorageException(
                        $"Directory entry at index {index} is null.");

                var containedPath = WorkflowArtifactContainedPath.Parse(entry.RelativePath);
                if (!seenPaths.Add(containedPath.Value))
                    throw new WorkflowArtifactStorageException(
                        $"Directory entry '{entry.RelativePath}' appears more than once in a single write.");

                if (entry.Size > effectiveLimits.MaxFileBytes)
                    throw new WorkflowArtifactStorageException(
                        $"Directory entry '{entry.RelativePath}' exceeds single-file size limit ({entry.Size} > {effectiveLimits.MaxFileBytes}).");
                if (totalBytes + entry.Size > effectiveLimits.MaxTotalBytes)
                    throw new WorkflowArtifactStorageException(
                        $"Directory entry '{entry.RelativePath}' would exceed total size limit ({effectiveLimits.MaxTotalBytes}).");

                totalBytes += entry.Size;

                var destination = Path.Combine(filesRoot, containedPath.Value.Replace('/', Path.DirectorySeparatorChar));
                var resolvedDestination = Path.GetFullPath(destination);
                if (!resolvedDestination.StartsWith(
                        EnsureTrailingSeparator(filesRoot),
                        StringComparison.Ordinal))
                    throw new WorkflowArtifactStorageException(
                        $"Directory entry '{entry.RelativePath}' resolves outside the artifact collection.");

                var destinationDir = Path.GetDirectoryName(resolvedDestination);
                if (!string.IsNullOrEmpty(destinationDir))
                    _files.CreateDirectory(destinationDir);

                await using (var input = SafeOpenContent(entry, containedPath.Value))
                {
                    await WriteStreamAsync(resolvedDestination, input, entry.Size, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var metadata = new WorkflowArtifactStorageMetadata
            {
                Path = write.SourcePath,
                Kind = "directory",
                ContentType = write.ContentType,
                ContentHash = write.ContentHash,
                Size = totalBytes,
                FileCount = entries.Count,
                RecordedAt = recordedAt,
            };
            PopulateIdentityMetadata(metadata, storagePath);

            await WriteMetadataAsync(collectionRoot, metadata, cancellationToken).ConfigureAwait(false);

            _log.LogDebug(
                "Persisted directory artifact {Storage} ({Files} files, {Bytes} bytes, source '{Source}')",
                storagePath, entries.Count, totalBytes, write.SourcePath);

            return new WorkflowArtifactStorageWriteResult(
                StoragePath: storagePath,
                Kind: WorkflowArtifactStorageKind.Directory,
                Size: totalBytes,
                FileCount: entries.Count);
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

    private void TryRemoveDirectory(string directory)
    {
        try
        {
            if (_files.DirectoryExists(directory))
                _files.DeleteDirectory(directory);
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
        if (!_files.FileExists(contentPath))
            throw new WorkflowArtifactNotFoundException(
                $"Recorded artifact content is missing at '{contentPath}'.");
        return _files.OpenRead(contentPath);
    }

    public async Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filesRoot = ResolveAbsoluteDirectoryFilesPath(storagePath);
        if (!_files.DirectoryExists(filesRoot))
            throw new WorkflowArtifactNotFoundException(
                $"Recorded directory artifact is missing at '{filesRoot}'.");

        var listing = new List<WorkflowArtifactDirectoryEntry>();
        long total = 0;
        foreach (var file in EnumerateFilesSafe(filesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(filesRoot, file.Path).Replace('\\', '/');
            total += file.Length;
            listing.Add(new WorkflowArtifactDirectoryEntry
            {
                RelativePath = relative,
                Size = file.Length,
                ContentType = null,
            });
        }

        listing.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        return new WorkflowArtifactDirectoryListing(storagePath, listing, total);
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
        if (!_files.FileExists(destination))
            throw new WorkflowArtifactNotFoundException(
                $"Recorded directory entry '{relativePath}' is missing.");
        return _files.OpenRead(destination);
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
        if (!_files.FileExists(metadataPath))
            return null;
        await using var stream = _files.OpenRead(metadataPath);
        return await JsonSerializer.DeserializeAsync<WorkflowArtifactStorageMetadata>(
            stream, JSON.Indented, cancellationToken).ConfigureAwait(false);
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

    private async Task<long> WriteStreamAsync(
        string destination,
        Stream source,
        long declaredSize,
        CancellationToken cancellationToken)
    {
        if (declaredSize < 0)
            throw new WorkflowArtifactStorageException(
                $"Declared size {declaredSize} is negative.");

        var tempPath = destination + ".tmp";
        long written = 0;
        bool committed = false;
        try
        {
            await using (var output = _files.OpenWrite(tempPath, FileMode.Create))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (declaredSize > 0 && written != declaredSize)
                throw new WorkflowArtifactStorageException(
                    $"Content size mismatch for '{destination}': declared {declaredSize} bytes, wrote {written} bytes.");

            if (_files.FileExists(destination))
                _files.DeleteFile(destination);
            _files.MoveFile(tempPath, destination, overwrite: false);
            committed = true;
            return written;
        }
        finally
        {
            if (!committed && _files.FileExists(tempPath))
            {
                try { _files.DeleteFile(tempPath); }
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
            await using (var output = _files.OpenWrite(tempPath, FileMode.Create))
            {
                await JsonSerializer.SerializeAsync(output, metadata, JSON.Indented, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_files.FileExists(tempPath))
            {
                if (_files.FileExists(metadataPath))
                    _files.DeleteFile(metadataPath);
                _files.MoveFile(tempPath, metadataPath, overwrite: false);
            }
        }
    }

    private IEnumerable<StorageFileEntry> EnumerateFilesSafe(string root)
    {
        return _files.EnumerateFiles(root)
            .Where(file => !file.IsReparsePoint);
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
        metadata.TaskRunId = identity.TaskRunId;
        metadata.ArtifactId = identity.ArtifactId;
    }
}
