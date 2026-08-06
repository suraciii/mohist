using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Storage;

public class AttachmentStorageContractTests
{
    private readonly InMemoryAttachmentStorage _storage = new();

    private static readonly DateTimeOffset SampleRecordedAt =
        new(2026, 6, 18, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GenerateStoragePath_UsesProjectScopedLayout()
    {
        var path = _storage.GenerateStoragePath("proj_1", "att_42");

        Assert.Equal("proj_1/att_42/content", path);
        Assert.Equal("/memory/attachments/proj_1/att_42/content", _storage.ResolveAbsolutePath(path));
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
    public async Task WriteFileAsync_RecordsContentAndMetadata()
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
        Assert.True(_storage.Contains(storagePath));

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
    public async Task WriteFileAsync_RefusesOverwrite()
    {
        var storagePath = _storage.GenerateStoragePath("proj_o", "att_o");
        await _storage.WriteFileAsync(storagePath, Bytes("a"u8.ToArray()), WriteFor("a.txt", 1), SampleRecordedAt);

        var exception = await Assert.ThrowsAsync<AttachmentStorageException>(() =>
            _storage.WriteFileAsync(storagePath, Bytes("b"u8.ToArray()), WriteFor("b.txt", 1), SampleRecordedAt));

        Assert.Contains("refusing to overwrite", exception.Message);
    }

    [Fact]
    public async Task WriteFileAsync_SizeMismatchLeavesNoRecordedAttachment()
    {
        var storagePath = _storage.GenerateStoragePath("proj_atomic", "att_atomic");

        await Assert.ThrowsAsync<AttachmentStorageException>(() =>
            _storage.WriteFileAsync(
                storagePath,
                Bytes("short"u8.ToArray()),
                WriteFor("bad.txt", 99),
                SampleRecordedAt));

        Assert.False(_storage.Contains(storagePath));
        Assert.Null(await _storage.ReadMetadataAsync(storagePath));
    }

    [Fact]
    public void ResolveAbsolutePath_RejectsInvalidStoragePaths()
    {
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("../escape"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("proj/../../etc/content"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("/etc/passwd"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath("proj/att/content\0"));
        Assert.Throws<AttachmentStorageException>(() => _storage.ResolveAbsolutePath(""));
    }

    [Fact]
    public async Task DeleteAsync_RemovesContentAndMetadata()
    {
        var storagePath = _storage.GenerateStoragePath("proj_d", "att_d");
        await _storage.WriteFileAsync(storagePath, Bytes("x"u8.ToArray()), WriteFor("x.txt", 1), SampleRecordedAt);

        await _storage.DeleteAsync(storagePath);

        Assert.False(_storage.Contains(storagePath));
        Assert.Null(await _storage.ReadMetadataAsync(storagePath));
        Assert.Throws<AttachmentNotFoundException>(() => _storage.OpenFileContent(storagePath));
    }

    [Fact]
    public void Options_ExposeSensibleDefaultLimits()
    {
        var options = new AttachmentStorageOptions();

        Assert.Equal(25L * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(20, options.MaxCountPerOwner);
    }

    private static Stream Bytes(byte[] data) => new MemoryStream(data, writable: false);

    private static AttachmentFileWrite WriteFor(string fileName, long size) => new()
    {
        OriginalFileName = fileName,
        ContentType = "image/png",
        Size = size,
    };
}
