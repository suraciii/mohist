using EnvironmentAbstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Storage;

public class FileSystemAttachmentStorageTests
{
    private const string Root = "/test/attachments";
    private readonly InMemoryStorageFileSystem _files = new();
    private readonly FileSystemAttachmentStorage _storage;

    public FileSystemAttachmentStorageTests()
    {
        _storage = new FileSystemAttachmentStorage(
            Root,
            NullLogger<FileSystemAttachmentStorage>.Instance,
            _files);
    }

    private static Stream Bytes(byte[] data) => new MemoryStream(data, writable: false);

    private static AttachmentFileWrite WriteFor(string fileName, long size) => new()
    {
        OriginalFileName = fileName,
        ContentType = "image/png",
        Size = size,
    };

    private static readonly DateTimeOffset SampleRecordedAt =
        new(2026, 6, 18, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GenerateStoragePath_UsesProjectScopedLayout()
    {
        var path = _storage.GenerateStoragePath("proj_1", "att_42");

        Assert.Equal("proj_1/att_42/content", path);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "proj_1", "att_42", "content")),
            _storage.ResolveAbsolutePath(path));
    }

    [Fact]
    public void GenerateStoragePath_RejectsUnsafeIds()
    {
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("../escape", "att_1"));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", "att/1"));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", ".."));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", "."));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", "att:colon"));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", "att\\backslash"));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", ""));
        Assert.Throws<AttachmentStorageException>(() => _storage.GenerateStoragePath("proj", "  "));
    }

    [Fact]
    public async Task WriteFileAsync_PersistsContentAndMetadataSidecar()
    {
        var storagePath = _storage.GenerateStoragePath("proj_1", "att_1");
        var payload = "PNG"u8.ToArray();

        var result = await _storage.WriteFileAsync(
            storagePath,
            Bytes(payload),
            WriteFor("screen.png", payload.Length),
            SampleRecordedAt);

        Assert.Equal(storagePath, result.StoragePath);
        Assert.Equal(payload.Length, result.Size);

        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        var collection = Path.GetDirectoryName(contentAbsolute)!;
        Assert.True(_files.FileExists(contentAbsolute));
        Assert.True(_files.FileExists(Path.Combine(collection, "metadata.json")));
        Assert.False(_files.FileExists(contentAbsolute + ".tmp"));

        using var reader = new StreamReader(_storage.OpenFileContent(storagePath));
        Assert.Equal("PNG", await reader.ReadToEndAsync());

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Equal("proj_1", metadata!.ProjectId);
        Assert.Equal("att_1", metadata.AttachmentId);
        Assert.Equal("screen.png", metadata.OriginalFileName);
        Assert.Equal("image/png", metadata.ContentType);
        Assert.Equal(payload.Length, metadata.Size);
        Assert.Equal(SampleRecordedAt, metadata.RecordedAt);
    }

    [Fact]
    public async Task WriteFileAsync_RefusesOverwriteOfExistingAttachmentDirectory()
    {
        var storagePath = _storage.GenerateStoragePath("proj_o", "att_o");

        await _storage.WriteFileAsync(storagePath, Bytes("a"u8.ToArray()), WriteFor("a.txt", 1), SampleRecordedAt);

        var ex = await Assert.ThrowsAsync<AttachmentStorageException>(() =>
            _storage.WriteFileAsync(storagePath, Bytes("b"u8.ToArray()), WriteFor("b.txt", 1), SampleRecordedAt));
        Assert.Contains("refusing to overwrite", ex.Message);
    }

    [Fact]
    public async Task WriteFileAsync_AtomicMoveLeavesNoContentOrTempOnFailure()
    {
        var storagePath = _storage.GenerateStoragePath("proj_atomic", "att_atomic");
        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);

        await Assert.ThrowsAsync<AttachmentStorageException>(() =>
            _storage.WriteFileAsync(
                storagePath,
                Bytes("short"u8.ToArray()),
                WriteFor("bad.txt", 99),
                SampleRecordedAt));

        Assert.False(_files.FileExists(contentAbsolute));
        Assert.False(_files.FileExists(contentAbsolute + ".tmp"));
        Assert.False(_files.DirectoryExists(Path.GetDirectoryName(contentAbsolute)!));
    }

    [Fact]
    public void ResolveAbsolutePath_RejectsTraversalAbsoluteRootedAndNulPaths()
    {
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("../escape"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("proj/../../etc/content"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("/etc/passwd"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("proj/att/content\0"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath(""));
    }

    [Fact]
    public void OpenFileContent_RejectsReparsePointAttachmentDirectory()
    {
        var storagePath = _storage.GenerateStoragePath("proj_link", "att_link");
        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        var collection = Path.GetDirectoryName(contentAbsolute)!;
        _files.CreateDirectory(collection);
        _files.MarkReparsePoint(collection);

        var ex = Assert.Throws<AttachmentStorageException>(() => _storage.OpenFileContent(storagePath));
        Assert.Contains("symlink", ex.Message);
    }

    [Fact]
    public async Task ReadMetadataAsync_MissingReturnsNull()
    {
        var storagePath = _storage.GenerateStoragePath("proj_none", "att_none");
        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        _files.CreateDirectory(Path.GetDirectoryName(contentAbsolute)!);

        var metadata = await _storage.ReadMetadataAsync(storagePath);

        Assert.Null(metadata);
    }

    [Fact]
    public void Options_ExposeSensibleDefaultLimits()
    {
        var options = new AttachmentStorageOptions();

        Assert.Equal(25L * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(20, options.MaxCountPerOwner);
    }

    [Fact]
    public void OptionsRoot_UsesConfiguredRootBeforeEnvironmentRoot()
    {
        var configuredRoot = Path.Combine(Root, "configured");
        var environmentRoot = Path.Combine(Root, "environment");
        var environment = new AttachmentStorageEnvironment(environmentRoot);
        var storage = new FileSystemAttachmentStorage(
            Options.Create(new AttachmentStorageOptions { Root = configuredRoot }),
            NullLogger<FileSystemAttachmentStorage>.Instance,
            environment,
            _files);

        Assert.Equal(Path.GetFullPath(configuredRoot), storage.StorageRoot);
    }

    [Fact]
    public void OptionsRoot_UsesEnvironmentRootWhenConfigurationRootMissing()
    {
        var environmentRoot = Path.Combine(Root, "environment-only");
        var environment = new AttachmentStorageEnvironment(environmentRoot);
        var storage = new FileSystemAttachmentStorage(
            Options.Create(new AttachmentStorageOptions()),
            NullLogger<FileSystemAttachmentStorage>.Instance,
            environment,
            _files);

        Assert.Equal(Path.GetFullPath(environmentRoot), storage.StorageRoot);
    }

    private sealed class AttachmentStorageEnvironment(string attachmentRoot) : IEnvironmentVariableProvider
    {
        public string? GetEnvironmentVariable(string variable) =>
            variable == AttachmentStorageOptions.RootEnvironmentVariable ? attachmentRoot : null;

        public string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target) =>
            GetEnvironmentVariable(variable);

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => new Dictionary<string, string>
        {
            [AttachmentStorageOptions.RootEnvironmentVariable] = attachmentRoot,
        };

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables(EnvironmentVariableTarget target) => GetEnvironmentVariables();

        public string ExpandEnvironmentVariables(string name) => name;

        public void SetEnvironmentVariable(string variable, string? value) =>
            throw new NotSupportedException();

        public void SetEnvironmentVariable(string variable, string? value, EnvironmentVariableTarget target) =>
            throw new NotSupportedException();
    }
}
