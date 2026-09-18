using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Storage;

[Trait("level", "L0")]
public class WorkflowArtifactStorageContractTests
{
    private readonly InMemoryWorkflowArtifactStorage _storage = new();

    private static readonly DateTimeOffset SampleRecordedAt =
        new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GenerateStoragePath_UsesKindSpecificLayout()
    {
        Assert.Equal(
            "workflows/wr_1/tasks/ai-review.1/artifacts/art_42/content",
            _storage.GenerateStoragePath("wr_1", "ai-review.1", "art_42", WorkflowArtifactStorageKind.File));
        Assert.Equal(
            "workflows/wr_1/tasks/ai-review.1/artifacts/art_42/files",
            _storage.GenerateStoragePath("wr_1", "ai-review.1", "art_42", WorkflowArtifactStorageKind.Directory));
    }

    [Fact]
    public void GenerateStoragePath_RejectsUnsafeIds()
    {
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("../escape", "task", "art", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task/1", "art", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "..", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "art:colon", WorkflowArtifactStorageKind.File));
    }

    [Fact]
    public async Task WriteFileAsync_RecordsMetadataAndContent()
    {
        var storagePath = _storage.GenerateStoragePath("wr_1", "ai-review.1", "art_1", WorkflowArtifactStorageKind.File);
        var payload = "PASS\n"u8.ToArray();

        var result = await _storage.WriteFileAsync(
            storagePath,
            Bytes(payload),
            WriteFor("artifacts/changes/issue-55/review.md", payload.Length),
            SampleRecordedAt);

        Assert.Equal(WorkflowArtifactStorageKind.File, result.Kind);
        Assert.Equal(payload.Length, result.Size);
        Assert.True(_storage.Contains(storagePath));
        using var reader = new StreamReader(_storage.OpenFileContent(storagePath));
        Assert.Equal("PASS\n", await reader.ReadToEndAsync());

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Equal("file", metadata!.Kind);
        Assert.Equal("artifacts/changes/issue-55/review.md", metadata.Path);
        Assert.Equal("wr_1", metadata.WorkflowRunId);
        Assert.Equal("ai-review.1", metadata.ActionAttemptId);
        Assert.Equal("art_1", metadata.ArtifactId);
        Assert.Equal(SampleRecordedAt, metadata.RecordedAt);
    }

    [Fact]
    public async Task WriteFileAsync_SourcePathIsMetadataOnly()
    {
        const string sourcePath = "../中文目录/工件.md";
        var storagePath = _storage.GenerateStoragePath("wr_x", "t_x", "a_x", WorkflowArtifactStorageKind.File);

        await _storage.WriteFileAsync(storagePath, Bytes("内容"u8.ToArray()), WriteFor(sourcePath, 6), SampleRecordedAt);

        Assert.Equal("workflows/wr_x/tasks/t_x/artifacts/a_x/content", storagePath);
        Assert.Equal(sourcePath, (await _storage.ReadMetadataAsync(storagePath))!.Path);
    }

    [Fact]
    public async Task WriteFileAsync_RefusesOverwriteAndSizeMismatch()
    {
        var existing = _storage.GenerateStoragePath("wr_o", "t_o", "a_o", WorkflowArtifactStorageKind.File);
        await _storage.WriteFileAsync(existing, Bytes("a"u8.ToArray()), WriteFor("a", 1), SampleRecordedAt);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteFileAsync(existing, Bytes("b"u8.ToArray()), WriteFor("b", 1), SampleRecordedAt));

        var mismatched = _storage.GenerateStoragePath("wr_m", "t_m", "a_m", WorkflowArtifactStorageKind.File);
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteFileAsync(mismatched, Bytes("short"u8.ToArray()), WriteFor("a", 99), SampleRecordedAt));
        Assert.False(_storage.Contains(mismatched));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RecordsOrdinalListingMetadataAndContent()
    {
        var storagePath = _storage.GenerateStoragePath("wr_2", "design", "art_2", WorkflowArtifactStorageKind.Directory);
        var entries = new[]
        {
            Entry("specs/data.md", "data-spec"),
            Entry("index.md", "index-x"),
            Entry("specs/auth.md", "auth-spec"),
        };

        var result = await _storage.WriteDirectoryAsync(
            storagePath,
            entries,
            new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 25, ContentType = "inode/directory" },
            SampleRecordedAt);

        Assert.Equal(WorkflowArtifactStorageKind.Directory, result.Kind);
        Assert.Equal(25, result.Size);
        Assert.Equal(3, result.FileCount);
        var listing = await _storage.ListDirectoryEntriesAsync(storagePath);
        Assert.Equal(["index.md", "specs/auth.md", "specs/data.md"], listing.Entries.Select(entry => entry.RelativePath));
        Assert.All(listing.Entries, entry => Assert.Null(entry.ContentType));
        Assert.Equal(25, listing.TotalSize);
        using var reader = new StreamReader(_storage.OpenDirectoryEntry(storagePath, "specs/auth.md"));
        Assert.Equal("auth-spec", await reader.ReadToEndAsync());
        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.Equal("directory", metadata!.Kind);
        Assert.Equal(3, metadata.FileCount);
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task WriteDirectoryAsync_RejectsInvalidContainedPaths(string relativePath)
    {
        var storagePath = _storage.GenerateStoragePath("wr_t", "t_t", "a_t", WorkflowArtifactStorageKind.Directory);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                storagePath,
                [new WorkflowArtifactDirectoryEntryInput { RelativePath = relativePath, Size = 1, OpenContent = () => Bytes("x"u8.ToArray()) }],
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 1 },
                SampleRecordedAt));
    }

    [Fact]
    public async Task WriteDirectoryAsync_EnforcesLimitsAndDuplicatePaths()
    {
        var fileCountPath = _storage.GenerateStoragePath("wr_c", "t_c", "a_c", WorkflowArtifactStorageKind.Directory);
        var limits = new WorkflowArtifactDirectoryLimits { MaxFileCount = 1, MaxTotalBytes = 5, MaxFileBytes = 4 };
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(fileCountPath, [Entry("a", "a"), Entry("b", "b")], WriteFor("specs/", 2), SampleRecordedAt, limits));

        var totalPath = _storage.GenerateStoragePath("wr_s", "t_s", "a_s", WorkflowArtifactStorageKind.Directory);
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(totalPath, [Entry("a", "aaa"), Entry("b", "bbb")], WriteFor("specs/", 6), SampleRecordedAt, limits));

        var singlePath = _storage.GenerateStoragePath("wr_f", "t_f", "a_f", WorkflowArtifactStorageKind.Directory);
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(singlePath, [Entry("a", "aaaaa")], WriteFor("specs/", 5), SampleRecordedAt, limits));

        var duplicatePath = _storage.GenerateStoragePath("wr_d", "t_d", "a_d", WorkflowArtifactStorageKind.Directory);
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(duplicatePath, [Entry("same", "a"), Entry("same", "b")], WriteFor("specs/", 2), SampleRecordedAt));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RejectsActualBytesBeyondDeclaredSizeAndLimits()
    {
        var limits = new WorkflowArtifactDirectoryLimits
        {
            MaxFileCount = 2,
            MaxTotalBytes = 4,
            MaxFileBytes = 3,
        };
        var entry = new WorkflowArtifactDirectoryEntryInput
        {
            RelativePath = "large.md",
            Size = 0,
            OpenContent = () => Bytes("large"u8.ToArray()),
        };
        var storagePath = _storage.GenerateStoragePath(
            "wr_actual",
            "t_actual",
            "a_actual",
            WorkflowArtifactStorageKind.Directory);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                storagePath,
                [entry],
                WriteFor("specs/", 0),
                SampleRecordedAt,
                limits));
        Assert.False(_storage.Contains(storagePath));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RecordsEntryManifestInMetadata()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_manifest", "design", "art_manifest", WorkflowArtifactStorageKind.Directory);
        var entries = new[]
        {
            Entry("specs/data.md", "data-spec", Sha256("data-spec"), "text/markdown"),
            Entry("index.md", "index-x", Sha256("index-x"), "text/markdown"),
            Entry("specs/auth.md", "auth-spec", Sha256("auth-spec"), "text/markdown"),
        };

        var result = await _storage.WriteDirectoryAsync(
            storagePath, entries, WriteFor("specs/", 25), SampleRecordedAt);

        Assert.Equal(25, result.Size);
        Assert.Equal(3, result.FileCount);
        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Equal("directory", metadata!.Kind);
        Assert.Equal(3, metadata.FileCount);
        Assert.Equal(25, metadata.Size);
        Assert.NotNull(metadata.Entries);

        var manifest = metadata.Entries!.ToArray();
        Assert.Equal(
            ["index.md", "specs/auth.md", "specs/data.md"],
            manifest.Select(entry => entry.RelativePath));
        Assert.Equal(
            [Sha256("index-x"), Sha256("auth-spec"), Sha256("data-spec")],
            manifest.Select(entry => entry.ContentHash));
        Assert.Equal([7L, 9L, 9L], manifest.Select(entry => entry.Size));
        Assert.All(manifest, entry => Assert.Equal("text/markdown", entry.ContentType));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RecordsComputedHashWhenNoneDeclared()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_computed", "design", "art_computed", WorkflowArtifactStorageKind.Directory);

        await _storage.WriteDirectoryAsync(
            storagePath, [Entry("a.md", "alpha")], WriteFor("specs/", 5), SampleRecordedAt);

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        var entry = Assert.Single(metadata!.Entries!);
        Assert.Equal(Sha256("alpha"), entry.ContentHash);
        Assert.Equal(5, entry.Size);
        Assert.Equal("a.md", entry.RelativePath);
    }

    [Fact]
    public async Task WriteFileAsync_OmitsEntryManifest()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_file_manifest", "t", "a", WorkflowArtifactStorageKind.File);

        await _storage.WriteFileAsync(
            storagePath, Bytes("abc"u8.ToArray()), WriteFor("a.md", 3), SampleRecordedAt);

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Null(metadata!.Entries);
    }

    [Fact]
    public async Task MetadataJson_PinsEntryManifestShapeAndOmitsItForFiles()
    {
        var directoryPath = _storage.GenerateStoragePath(
            "wr_json_manifest", "t", "dir", WorkflowArtifactStorageKind.Directory);
        await _storage.WriteDirectoryAsync(
            directoryPath,
            [Entry("a.md", "alpha", Sha256("alpha"), "text/markdown")],
            WriteFor("specs/", 5),
            SampleRecordedAt);

        var directoryMetadata = await _storage.ReadMetadataAsync(directoryPath);
        using var directoryJson = JsonDocument.Parse(JSON.SerializeIndented(directoryMetadata));
        var entry = Assert.Single(directoryJson.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("a.md", entry.GetProperty("relativePath").GetString());
        Assert.Equal(5, entry.GetProperty("size").GetInt64());
        Assert.Equal(Sha256("alpha"), entry.GetProperty("contentHash").GetString());
        Assert.Equal("text/markdown", entry.GetProperty("contentType").GetString());

        var filePath = _storage.GenerateStoragePath(
            "wr_json_manifest", "t", "file", WorkflowArtifactStorageKind.File);
        await _storage.WriteFileAsync(
            filePath, Bytes("abc"u8.ToArray()), WriteFor("a.md", 3), SampleRecordedAt);

        var fileMetadata = await _storage.ReadMetadataAsync(filePath);
        using var fileJson = JsonDocument.Parse(JSON.SerializeIndented(fileMetadata));
        Assert.False(fileJson.RootElement.TryGetProperty("entries", out _));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RejectsDeclaredHashMismatchAndLeavesPathRetryable()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_hash_mismatch", "t", "a", WorkflowArtifactStorageKind.Directory);
        var bad = Entry("a.md", "actual", $"sha256:{new string('0', 64)}", "text/markdown");

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(storagePath, [bad], WriteFor("specs/", 6), SampleRecordedAt));

        Assert.False(_storage.Contains(storagePath));
        Assert.Null(await _storage.ReadMetadataAsync(storagePath));
        await Assert.ThrowsAsync<WorkflowArtifactNotFoundException>(() =>
            _storage.ListDirectoryEntriesAsync(storagePath));

        var retry = await _storage.WriteDirectoryAsync(
            storagePath,
            [Entry("a.md", "actual", Sha256("actual"), "text/markdown")],
            WriteFor("specs/", 6),
            SampleRecordedAt);
        Assert.Equal(6, retry.Size);
        Assert.True(_storage.Contains(storagePath));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RejectsDeclaredSizeMismatchAndLeavesPathRetryable()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_size_mismatch", "t", "a", WorkflowArtifactStorageKind.Directory);
        var bad = new WorkflowArtifactDirectoryEntryInput
        {
            RelativePath = "a.md",
            Size = 99,
            OpenContent = () => Bytes("actual"u8.ToArray()),
        };

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(storagePath, [bad], WriteFor("specs/", 6), SampleRecordedAt));

        Assert.False(_storage.Contains(storagePath));
        Assert.Null(await _storage.ReadMetadataAsync(storagePath));
        await Assert.ThrowsAsync<WorkflowArtifactNotFoundException>(() =>
            _storage.ListDirectoryEntriesAsync(storagePath));

        var retry = await _storage.WriteDirectoryAsync(
            storagePath, [Entry("a.md", "actual")], WriteFor("specs/", 6), SampleRecordedAt);
        Assert.Equal(6, retry.Size);
        Assert.True(_storage.Contains(storagePath));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RejectedDeclaredValuesLeaveNoArtifact()
    {
        var limits = new WorkflowArtifactDirectoryLimits
        {
            MaxFileCount = 1,
            MaxTotalBytes = 10,
            MaxFileBytes = 10,
        };

        var countPath = _storage.GenerateStoragePath(
            "wr_cleanup", "t", "count", WorkflowArtifactStorageKind.Directory);
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                countPath, [Entry("a", "a"), Entry("b", "b")], WriteFor("s/", 2), SampleRecordedAt, limits));
        Assert.False(_storage.Contains(countPath));
        Assert.Null(await _storage.ReadMetadataAsync(countPath));

        var duplicatePath = _storage.GenerateStoragePath(
            "wr_cleanup", "t", "dup", WorkflowArtifactStorageKind.Directory);
        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                duplicatePath, [Entry("same", "a"), Entry("same", "b")], WriteFor("s/", 2), SampleRecordedAt));
        Assert.False(_storage.Contains(duplicatePath));
        Assert.Null(await _storage.ReadMetadataAsync(duplicatePath));

        // The clean rejection must leave the path retryable.
        var retry = await _storage.WriteDirectoryAsync(
            duplicatePath, [Entry("same", "a")], WriteFor("s/", 1), SampleRecordedAt);
        Assert.Equal(1, retry.Size);
        Assert.True(_storage.Contains(duplicatePath));
    }

    [Fact]
    public async Task DeleteAsync_RemovesArtifact()
    {
        var storagePath = _storage.GenerateStoragePath("wr_d", "t_d", "a_d", WorkflowArtifactStorageKind.File);
        await _storage.WriteFileAsync(storagePath, Bytes("x"u8.ToArray()), WriteFor("x", 1), SampleRecordedAt);

        await _storage.DeleteAsync(storagePath);

        Assert.False(_storage.Contains(storagePath));
        Assert.Null(await _storage.ReadMetadataAsync(storagePath));
        Assert.Throws<WorkflowArtifactNotFoundException>(() => _storage.OpenFileContent(storagePath));
    }

    private static WorkflowArtifactDirectoryEntryInput Entry(string path, string content) => new()
    {
        RelativePath = path,
        Size = System.Text.Encoding.UTF8.GetByteCount(content),
        OpenContent = () => Bytes(System.Text.Encoding.UTF8.GetBytes(content)),
    };

    private static WorkflowArtifactDirectoryEntryInput Entry(
        string path,
        string content,
        string? contentHash,
        string? contentType) => new()
        {
            RelativePath = path,
            Size = System.Text.Encoding.UTF8.GetByteCount(content),
            ContentHash = contentHash,
            ContentType = contentType,
            OpenContent = () => Bytes(System.Text.Encoding.UTF8.GetBytes(content)),
        };

    private static string Sha256(string content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()}";

    private static Stream Bytes(byte[] data) => new MemoryStream(data, writable: false);

    private static WorkflowArtifactFileWrite WriteFor(string sourcePath, long size) => new()
    {
        SourcePath = sourcePath,
        Size = size,
        ContentType = "text/markdown",
        ContentHash = "sha256:abc",
    };
}
