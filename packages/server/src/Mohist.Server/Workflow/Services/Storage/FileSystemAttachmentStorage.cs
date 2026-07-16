using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Default <see cref="IAttachmentStorage"/> implementation. Attachment
/// content is stored under <c>{projectId}/{attachmentId}/content</c>
/// with <c>metadata.json</c> beside the content file.
/// </summary>
public sealed class FileSystemAttachmentStorage : IAttachmentStorage
{
    public const string MetadataFileName = "metadata.json";
    public const string FileContentName = "content";
    public const string StorageRootName = "attachments";

    private readonly ILogger<FileSystemAttachmentStorage> _log;
    private readonly string _root;

    public FileSystemAttachmentStorage(
        IOptions<AttachmentStorageOptions> options,
        ILogger<FileSystemAttachmentStorage> log,
        IEnvironmentVariableProvider environment)
    {
        _log = log;
        _root = ResolveStorageRoot(options.Value, environment);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Test-only constructor that bypasses the options pipeline.</summary>
    public FileSystemAttachmentStorage(string root, ILogger<FileSystemAttachmentStorage> log)
    {
        _log = log;
        _root = ResolveStorageRoot(new AttachmentStorageOptions { Root = root }, null);
        Directory.CreateDirectory(_root);
    }

    public string StorageRoot => _root;

    public string GenerateStoragePath(string projectId, string attachmentId)
    {
        ValidateId(projectId, nameof(projectId));
        ValidateId(attachmentId, nameof(attachmentId));
        return string.Join('/', projectId, attachmentId, FileContentName);
    }

    public string ResolveAbsolutePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new AttachmentStorageException("Storage path must be provided.");

        var normalized = SanitizeRelativePath(storagePath);
        var absolute = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var safeRoot = EnsureTrailingSeparator(_root);
        if (!absolute.StartsWith(safeRoot, StringComparison.Ordinal) && !string.Equals(absolute, _root, StringComparison.Ordinal))
            throw new AttachmentStorageException($"Storage path '{storagePath}' resolves outside the attachment storage root.");
        return absolute;
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
        cancellationToken.ThrowIfCancellationRequested();

        var directory = EnsureStorageDirectoryForFile(storagePath);
        if (Directory.Exists(directory))
            throw new AttachmentStorageException(
                $"Attachment storage directory '{directory}' already exists; refusing to overwrite a recorded attachment.");

        Directory.CreateDirectory(directory);

        var metadata = new AttachmentStorageMetadata
        {
            OriginalFileName = write.OriginalFileName,
            ContentType = write.ContentType,
            Size = write.Size,
            RecordedAt = recordedAt,
        };
        PopulateIdentityMetadata(metadata, storagePath);

        var contentPath = Path.Combine(directory, FileContentName);
        try
        {
            var written = await WriteStreamAsync(contentPath, content, write.Size, write.MaxSize, cancellationToken)
                .ConfigureAwait(false);

            metadata.Size = written;
            await WriteMetadataAsync(directory, metadata, cancellationToken).ConfigureAwait(false);

            _log.LogDebug(
                "Persisted attachment {Storage} ({Bytes} bytes, original '{OriginalFileName}')",
                storagePath, written, write.OriginalFileName);

            return new AttachmentStorageWriteResult(storagePath, written);
        }
        catch
        {
            TryRemoveDirectory(directory);
            throw;
        }
    }

    public Stream OpenFileContent(string storagePath)
    {
        var contentPath = ResolveAbsoluteFileContentPath(storagePath);
        if (!File.Exists(contentPath))
            throw new AttachmentNotFoundException(
                $"Recorded attachment content is missing at '{contentPath}'.");
        RejectReparsePoint(contentPath, $"Attachment content path '{storagePath}' must not be a symlink.");
        return new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<AttachmentStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = ResolveAbsolutePath(storagePath);
        var collectionRoot = Path.GetDirectoryName(absolute) ?? absolute;
        RejectReparsePoint(collectionRoot, $"Attachment storage directory '{storagePath}' must not be a symlink.");
        var metadataPath = Path.Combine(collectionRoot, MetadataFileName);
        if (!File.Exists(metadataPath))
            return null;
        RejectReparsePoint(metadataPath, $"Attachment metadata path '{storagePath}' must not be a symlink.");
        await using var stream = File.OpenRead(metadataPath);
        return await JsonSerializer.DeserializeAsync<AttachmentStorageMetadata>(
            stream, JSON.Indented, cancellationToken).ConfigureAwait(false);
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

    private static string ResolveStorageRoot(AttachmentStorageOptions options, IEnvironmentVariableProvider? environment)
    {
        var configured = options.Root;
        if (string.IsNullOrWhiteSpace(configured))
            configured = environment?.GetEnvironmentVariable(AttachmentStorageOptions.RootEnvironmentVariable);
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
            ?? throw new AttachmentStorageException(
                $"Storage path '{storagePath}' has no parent directory.");
        if (Path.GetFileName(absolute) != FileContentName)
            throw new AttachmentStorageException(
                $"Attachment storage path '{storagePath}' must end with '{FileContentName}'.");
        if (Directory.Exists(parent))
            RejectReparsePoint(parent, $"Attachment storage directory '{storagePath}' must not be a symlink.");
        return parent;
    }

    private string ResolveAbsoluteFileContentPath(string storagePath)
    {
        var absolute = ResolveAbsolutePath(storagePath);
        if (Path.GetFileName(absolute) != FileContentName)
            throw new AttachmentStorageException(
                $"Storage path '{storagePath}' does not point at attachment content.");
        var parent = Path.GetDirectoryName(absolute);
        if (parent is not null && Directory.Exists(parent))
            RejectReparsePoint(parent, $"Attachment storage directory '{storagePath}' must not be a symlink.");
        return absolute;
    }

    private static async Task<long> WriteStreamAsync(
        string destination,
        Stream source,
        long declaredSize,
        long? maxSize,
        CancellationToken cancellationToken)
    {
        if (declaredSize < 0)
            throw new AttachmentStorageException($"Declared size {declaredSize} is negative.");

        var tempPath = destination + ".tmp";
        long written = 0;
        bool committed = false;
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (maxSize is { } limit && written > limit)
                        throw new AttachmentStorageLimitException($"Attachment upload exceeds the configured size limit of {limit} bytes.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (declaredSize > 0 && written != declaredSize)
                throw new AttachmentStorageException(
                    $"Content size mismatch for '{destination}': declared {declaredSize} bytes, wrote {written} bytes.");

            File.Move(tempPath, destination, overwrite: false);
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

    private static async Task WriteMetadataAsync(
        string directory,
        AttachmentStorageMetadata metadata,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var tempPath = metadataPath + ".tmp";
        var committed = false;
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, metadata, JSON.Indented, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, metadataPath, overwrite: false);
            committed = true;
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

    private static void ValidateId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AttachmentStorageException($"{paramName} must be provided.");
        foreach (var ch in value)
        {
            if (ch is '/' or '\\' or '\0' or ' ' or ':')
                throw new AttachmentStorageException(
                    $"{paramName} contains an unsafe character: '{ch}'.");
        }
        if (value == "." || value == "..")
            throw new AttachmentStorageException($"{paramName} must not be a traversal segment.");
    }

    private static string SanitizeRelativePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new AttachmentStorageException("Storage path must be provided.");
        var trimmed = storagePath.Replace('\\', '/');
        if (trimmed.StartsWith("/") || Path.IsPathRooted(storagePath))
            throw new AttachmentStorageException(
                $"Storage path '{storagePath}' must be relative to the attachment storage root.");
        if (trimmed.Contains("..", StringComparison.Ordinal))
            throw new AttachmentStorageException(
                $"Storage path '{storagePath}' contains a traversal segment.");
        if (trimmed.Contains("\0", StringComparison.Ordinal))
            throw new AttachmentStorageException(
                $"Storage path '{storagePath}' contains a NUL character.");
        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                throw new AttachmentStorageException(
                    $"Storage path '{storagePath}' contains an invalid segment.");
        }
        return trimmed;
    }

    private static void PopulateIdentityMetadata(AttachmentStorageMetadata metadata, string storagePath)
    {
        var segments = storagePath.Replace('\\', '/').Split('/');
        if (segments.Length >= 3 && segments[2] == FileContentName)
        {
            metadata.ProjectId = segments[0];
            metadata.AttachmentId = segments[1];
        }
    }

    private static void RejectReparsePoint(string path, string message)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new AttachmentStorageException(message);
    }

    private static void TryRemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            var parent = Directory.GetParent(directory);
            if (parent is not null && Directory.Exists(parent.FullName) && !Directory.EnumerateFileSystemEntries(parent.FullName).Any())
                Directory.Delete(parent.FullName);
        }
        catch
        {
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
