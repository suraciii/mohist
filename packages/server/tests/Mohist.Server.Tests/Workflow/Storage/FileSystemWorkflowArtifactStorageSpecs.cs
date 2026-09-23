using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Storage;

/// <summary>
/// Design Spec for the production filesystem adapter. It runs the real
/// <see cref="FileSystemWorkflowArtifactStorage"/> over an in-memory
/// <see cref="IWorkflowArtifactFileSystem"/> so the adapter's persisted
/// manifest, recorded hashes, ordinal ordering, and failed-write cleanup are
/// proven without reading or writing a host filesystem.
/// </summary>
[Trait("level", "L0")]
public class FileSystemWorkflowArtifactStorageSpecs
{
    private const string StorageRoot = "/mohist-specs/artifact-storage";

    private static readonly DateTimeOffset SampleRecordedAt =
        new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task WriteDirectoryAsync_RecordsOrderedManifestInMetadataJson()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_manifest", "design", "art_manifest", WorkflowArtifactStorageKind.Directory);

        var result = await storage.WriteDirectoryAsync(
            storagePath,
            AsAsyncEnumerable([
                Entry("specs/data.md", "data-spec", "text/markdown"),
                Entry("index.md", "index-x", "text/markdown"),
                Entry("specs/auth.md", "auth-spec", "text/markdown"),
            ]),
            WriteFor("specs/", 25),
            SampleRecordedAt);

        Assert.Equal(WorkflowArtifactStorageKind.Directory, result.Kind);
        Assert.Equal(25, result.Size);
        Assert.Equal(3, result.FileCount);

        var metadata = await storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Equal("directory", metadata!.Kind);
        Assert.Equal(
            ["index.md", "specs/auth.md", "specs/data.md"],
            metadata.Entries!.Select(entry => entry.RelativePath));
        Assert.Equal([7L, 9L, 9L], metadata.Entries!.Select(entry => entry.Size));
        Assert.Equal(
            [Sha256("index-x"), Sha256("auth-spec"), Sha256("data-spec")],
            metadata.Entries!.Select(entry => entry.ContentHash));
        Assert.All(metadata.Entries!, entry => Assert.Equal("text/markdown", entry.ContentType));

        // The manifest is durable on the storage boundary as pinned JSON,
        // not just an in-object view.
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var metadataPath = Path.Combine(Path.GetDirectoryName(filesRoot)!, "metadata.json");
        using var json = JsonDocument.Parse(fileSystem.ReadFile(metadataPath));
        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(
            ["index.md", "specs/auth.md", "specs/data.md"],
            entries.Select(entry => entry.GetProperty("relativePath").GetString()));
        Assert.Equal(
            ["text/markdown", "text/markdown", "text/markdown"],
            entries.Select(entry => entry.GetProperty("contentType").GetString()));

        // The bytes actually written are the declared bytes.
        Assert.Equal(
            Encoding.UTF8.GetBytes("index-x"),
            fileSystem.ReadFile(Path.Combine(filesRoot, "index.md")));
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_ReturnsRecordedHashAfterStoredFileMutates()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_tamper", "design", "art_tamper", WorkflowArtifactStorageKind.Directory);
        await storage.WriteDirectoryAsync(
            storagePath,
            AsAsyncEnumerable([Entry("index.md", "index-x", "text/markdown")]),
            WriteFor("specs/", 7),
            SampleRecordedAt);

        var recorded = Assert.Single((await storage.ReadMetadataAsync(storagePath))!.Entries!);
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        fileSystem.MutateFile(Path.Combine(filesRoot, "index.md"), Encoding.UTF8.GetBytes("tampered"));

        var listing = await storage.ListDirectoryEntriesAsync(storagePath);
        var listed = Assert.Single(listing.Entries);
        Assert.Equal(recorded.ContentHash, listed.ContentHash);
        Assert.Equal(recorded.Size, listed.Size);
        Assert.Equal(recorded.ContentType, listed.ContentType);

        // The served bytes really changed; only the recorded expectation held.
        using var reader = new StreamReader(storage.OpenDirectoryEntry(storagePath, "index.md"));
        Assert.Equal("tampered", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task WriteDirectoryAsync_DeclaredHashMismatchRemovesCollectionAndLeavesPathRetryable()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_hash", "design", "art_hash", WorkflowArtifactStorageKind.Directory);
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var collectionRoot = Path.GetDirectoryName(filesRoot)!;

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(
                storagePath,
                AsAsyncEnumerable([
                    new WorkflowArtifactDirectoryEntryInput
                    {
                        RelativePath = "a.md",
                        Size = 6,
                        ContentHash = $"sha256:{new string('0', 64)}",
                        ContentType = "text/markdown",
                        OpenContent = () => Bytes("actual"),
                    },
                ]),
                WriteFor("specs/", 6),
                SampleRecordedAt));

        Assert.False(fileSystem.DirectoryExists(collectionRoot));
        Assert.False(fileSystem.FileExists(Path.Combine(filesRoot, "a.md")));
        Assert.Null(await storage.ReadMetadataAsync(storagePath));
        await Assert.ThrowsAsync<WorkflowArtifactNotFoundException>(() =>
            storage.ListDirectoryEntriesAsync(storagePath));

        var retry = await storage.WriteDirectoryAsync(
            storagePath, AsAsyncEnumerable([Entry("a.md", "actual", "text/markdown")]), WriteFor("specs/", 6), SampleRecordedAt);
        Assert.Equal(6, retry.Size);
    }

    [Fact]
    public async Task WriteDirectoryAsync_MetadataWriteFailureRemovesCollection()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_meta", "design", "art_meta", WorkflowArtifactStorageKind.Directory);
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var collectionRoot = Path.GetDirectoryName(filesRoot)!;
        var metadataTemp = Path.Combine(collectionRoot, "metadata.json.tmp");
        fileSystem.FailNextOpenWrite = path => path == metadataTemp;

        await Assert.ThrowsAsync<IOException>(() =>
            storage.WriteDirectoryAsync(
                storagePath, AsAsyncEnumerable([Entry("a.md", "alpha", "text/plain")]), WriteFor("specs/", 5), SampleRecordedAt));

        Assert.False(fileSystem.DirectoryExists(collectionRoot));
        Assert.Null(await storage.ReadMetadataAsync(storagePath));
        await Assert.ThrowsAsync<WorkflowArtifactNotFoundException>(() =>
            storage.ListDirectoryEntriesAsync(storagePath));
    }

    [Fact]
    public async Task WriteDirectoryAsync_EnumeratesOneEntryAtATime()
    {
        var (storage, _) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_stream", "design", "art_stream", WorkflowArtifactStorageKind.Directory);
        var firstOpened = false;
        var first = new WorkflowArtifactDirectoryEntryInput
        {
            RelativePath = "a.md",
            Size = 5,
            ContentHash = Sha256("alpha"),
            ContentType = "text/plain",
            OpenContent = () =>
            {
                firstOpened = true;
                return Bytes("alpha");
            },
        };

        async IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> Stream()
        {
            yield return first;
            await Task.CompletedTask.ConfigureAwait(false);
            Assert.True(
                firstOpened,
                "storage requested the next entry before completing the current one");
            yield return Entry("b.md", "beta", "text/plain");
        }

        var result = await storage.WriteDirectoryAsync(
            storagePath, Stream(), WriteFor("specs/", 9), SampleRecordedAt);

        Assert.Equal(2, result.FileCount);
        var metadata = await storage.ReadMetadataAsync(storagePath);
        Assert.Equal(
            ["a.md", "b.md"],
            metadata!.Entries!.Select(entry => entry.RelativePath));
    }

    [Fact]
    public async Task WriteDirectoryAsync_MidStreamFailureRemovesCollectionAndLeavesPathRetryable()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_midfail", "design", "art_midfail", WorkflowArtifactStorageKind.Directory);
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var collectionRoot = Path.GetDirectoryName(filesRoot)!;

        async IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> Stream()
        {
            yield return Entry("a.md", "alpha", "text/plain");
            await Task.CompletedTask.ConfigureAwait(false);
            throw new WorkflowArtifactStorageException("stream failed mid-way");
        }

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(storagePath, Stream(), WriteFor("specs/", 5), SampleRecordedAt));

        Assert.False(fileSystem.DirectoryExists(collectionRoot));
        Assert.Null(await storage.ReadMetadataAsync(storagePath));

        var retry = await storage.WriteDirectoryAsync(
            storagePath, AsAsyncEnumerable([Entry("a.md", "alpha", "text/plain")]), WriteFor("specs/", 5), SampleRecordedAt);
        Assert.Equal(5, retry.Size);
    }

    [Fact]
    public async Task WriteDirectoryAsync_RejectsNegativeDeclaredSizeAndLeavesPathRetryable()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_negative", "design", "art_negative", WorkflowArtifactStorageKind.Directory);
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var collectionRoot = Path.GetDirectoryName(filesRoot)!;
        var negative = new WorkflowArtifactDirectoryEntryInput
        {
            RelativePath = "a.md",
            Size = -1,
            OpenContent = () => Bytes("alpha"),
        };

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(
                storagePath, AsAsyncEnumerable([negative]), WriteFor("specs/", 5), SampleRecordedAt));

        Assert.False(fileSystem.DirectoryExists(collectionRoot));
        Assert.Null(await storage.ReadMetadataAsync(storagePath));

        var retry = await storage.WriteDirectoryAsync(
            storagePath, AsAsyncEnumerable([Entry("a.md", "alpha", "text/plain")]), WriteFor("specs/", 5), SampleRecordedAt);
        Assert.Equal(5, retry.Size);
    }

    [Fact]
    public async Task WriteDirectoryAsync_EmptyDirectoryRemovesCollectionAndLeavesPathRetryable()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_empty", "design", "art_empty", WorkflowArtifactStorageKind.Directory);
        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var collectionRoot = Path.GetDirectoryName(filesRoot)!;

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(
                storagePath,
                AsAsyncEnumerable(Array.Empty<WorkflowArtifactDirectoryEntryInput>()),
                WriteFor("specs/", 0),
                SampleRecordedAt));

        Assert.False(fileSystem.DirectoryExists(collectionRoot));
        Assert.Null(await storage.ReadMetadataAsync(storagePath));

        var retry = await storage.WriteDirectoryAsync(
            storagePath, AsAsyncEnumerable([Entry("a.md", "alpha", "text/plain")]), WriteFor("specs/", 5), SampleRecordedAt);
        Assert.Equal(5, retry.Size);
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_RejectsMalformedMetadataRatherThanRebuildingFromFiles()
    {
        var (storage, fileSystem) = Create();
        var storagePath = storage.GenerateStoragePath(
            "wr_bad", "design", "art_bad", WorkflowArtifactStorageKind.Directory);
        await storage.WriteDirectoryAsync(
            storagePath, AsAsyncEnumerable([Entry("a.md", "alpha", "text/plain")]), WriteFor("specs/", 5), SampleRecordedAt);

        var filesRoot = storage.ResolveAbsolutePath(storagePath);
        var metadataPath = Path.Combine(Path.GetDirectoryName(filesRoot)!, "metadata.json");
        fileSystem.SeedFile(metadataPath, Encoding.UTF8.GetBytes("{ not json"));

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.ListDirectoryEntriesAsync(storagePath));
    }

    private static (FileSystemWorkflowArtifactStorage Storage, InMemoryWorkflowArtifactFileSystem FileSystem) Create()
    {
        var fileSystem = new InMemoryWorkflowArtifactFileSystem();
        var storage = new FileSystemWorkflowArtifactStorage(
            StorageRoot,
            NullLogger<FileSystemWorkflowArtifactStorage>.Instance,
            WorkflowArtifactDirectoryLimits.Default,
            fileSystem);
        return (storage, fileSystem);
    }

    private static WorkflowArtifactDirectoryEntryInput Entry(string path, string content, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new WorkflowArtifactDirectoryEntryInput
        {
            RelativePath = path,
            Size = bytes.LongLength,
            ContentHash = Sha256(content),
            ContentType = contentType,
            OpenContent = () => new MemoryStream(bytes, writable: false),
        };
    }

    private static string Sha256(string content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()}";

    private static Stream Bytes(string content) =>
        new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);

    private static WorkflowArtifactFileWrite WriteFor(string sourcePath, long size) => new()
    {
        SourcePath = sourcePath,
        Size = size,
        ContentType = "application/x-mohist-artifact-directory",
        ContentHash = "sha256:dir",
    };

    private static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
