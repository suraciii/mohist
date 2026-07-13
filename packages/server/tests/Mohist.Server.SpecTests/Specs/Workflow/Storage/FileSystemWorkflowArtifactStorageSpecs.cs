using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

[Trait(Traits.Speed.Name, Traits.Speed.Service)]
[Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
public class FileSystemWorkflowArtifactStorageSpecs : IDisposable
{
    private readonly string _root;
    private readonly FileSystemWorkflowArtifactStorage _storage;

    public FileSystemWorkflowArtifactStorageSpecs()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mohist-artifacts-{Guid.NewGuid():N}");
        _storage = new FileSystemWorkflowArtifactStorage(
            _root,
            NullLogger<FileSystemWorkflowArtifactStorage>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup; tmpfs may be flaky on CI
        }
    }

    private static Stream Bytes(byte[] data) => new MemoryStream(data, writable: false);

    private static WorkflowArtifactFileWrite WriteFor(string sourcePath, long size) => new()
    {
        SourcePath = sourcePath,
        Size = size,
        ContentType = "text/markdown",
        ContentHash = "sha256:abc",
    };

    private static readonly DateTimeOffset SampleRecordedAt =
        new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GenerateStoragePath_UsesGeneratedSegments()
    {
        var path = _storage.GenerateStoragePath(
            "wr_1",
            "ai-review.1",
            "art_42",
            WorkflowArtifactStorageKind.File);

        Assert.Equal("workflows/wr_1/tasks/ai-review.1/artifacts/art_42/content", path);
    }

    [Fact]
    public void GenerateStoragePath_DirectoryKindEndsWithFilesSegment()
    {
        var path = _storage.GenerateStoragePath(
            "wr_1",
            "ai-review.1",
            "art_42",
            WorkflowArtifactStorageKind.Directory);

        Assert.Equal("workflows/wr_1/tasks/ai-review.1/artifacts/art_42/files", path);
    }

    [Fact]
    public void GenerateStoragePath_RejectsTraversalInIds()
    {
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("../escape", "task", "art", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task/1", "art", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "..", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", ".", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "art:colon", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "art\\backslash", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "", WorkflowArtifactStorageKind.File));
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.GenerateStoragePath("wr", "task", "  ", WorkflowArtifactStorageKind.File));
    }

    [Fact]
    public async Task WriteFileAsync_PersistsMetadataAndContent()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_1", "ai-review.1", "art_1", WorkflowArtifactStorageKind.File);
        var payload = "PASS\n"u8.ToArray();

        var result = await _storage.WriteFileAsync(
            storagePath,
            Bytes(payload),
            WriteFor("openspec/changes/issue-55/review.md", payload.Length),
            SampleRecordedAt);

        Assert.Equal(WorkflowArtifactStorageKind.File, result.Kind);
        Assert.Equal(payload.Length, result.Size);
        Assert.Equal(storagePath, result.StoragePath);

        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        var collection = Path.GetDirectoryName(contentAbsolute)!;
        Assert.True(File.Exists(contentAbsolute));
        Assert.True(File.Exists(Path.Combine(collection, "metadata.json")));

        using var reader = new StreamReader(_storage.OpenFileContent(storagePath));
        var read = await reader.ReadToEndAsync();
        Assert.Equal("PASS\n", read);

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Equal("file", metadata!.Kind);
        Assert.Equal("openspec/changes/issue-55/review.md", metadata.Path);
        Assert.Equal("wr_1", metadata.WorkflowRunId);
        Assert.Equal("ai-review.1", metadata.TaskRunId);
        Assert.Equal("art_1", metadata.ArtifactId);
        Assert.Equal(SampleRecordedAt, metadata.RecordedAt);
    }

    [Fact]
    public async Task WriteFileAsync_SourcePathWithUnusualCharactersIsMetadataOnly()
    {
        var unusualPath = "openspec\\changes:issue-55/..weird*name?.md";
        var storagePath = _storage.GenerateStoragePath(
            "wr_x", "t_x", "a_x", WorkflowArtifactStorageKind.File);

        await _storage.WriteFileAsync(
            storagePath,
            Bytes("x"u8.ToArray()),
            WriteFor(unusualPath, 1),
            SampleRecordedAt);

        // Storage path is the same regardless of source path; only the
        // metadata reflects the unusual source path verbatim.
        Assert.Equal("workflows/wr_x/tasks/t_x/artifacts/a_x/content", storagePath);
        var absolute = _storage.ResolveAbsolutePath(storagePath);
        // The resolved absolute is the `content` file path; the
        // collection directory is its parent.
        Assert.True(Directory.Exists(Path.GetDirectoryName(absolute)!));

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.Equal(unusualPath, metadata!.Path);
    }

    [Fact]
    public async Task WriteFileAsync_SourcePathNeverAppearsAsStorageSegment()
    {
        // Even a deeply nested, slash-heavy, traversal-looking source
        // path must never propagate into the storage directory tree.
        var nasty = "../openspec/changes/issue-55/../../../etc/passwd";
        var storagePath = _storage.GenerateStoragePath(
            "wr_p", "t_p", "a_p", WorkflowArtifactStorageKind.File);

        await _storage.WriteFileAsync(
            storagePath,
            Bytes("ok"u8.ToArray()),
            WriteFor(nasty, 2),
            SampleRecordedAt);

        var absolute = _storage.ResolveAbsolutePath(storagePath);
        // The storage directory layout must remain exactly
        // workflows/wr_p/tasks/t_p/artifacts/a_p/content regardless
        // of the dangerous source path.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "workflows", "wr_p", "tasks", "t_p", "artifacts", "a_p", "content")),
            absolute);
    }

    [Fact]
    public async Task WriteFileAsync_RefusesOverwrite()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_o", "t_o", "a_o", WorkflowArtifactStorageKind.File);

        await _storage.WriteFileAsync(storagePath, Bytes("a"u8.ToArray()), WriteFor("a", 1), SampleRecordedAt);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteFileAsync(storagePath, Bytes("b"u8.ToArray()), WriteFor("a", 1), SampleRecordedAt));
    }

    [Fact]
    public void OpenFileContent_MissingContentThrowsNotFound()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_m", "t_m", "a_m", WorkflowArtifactStorageKind.File);
        // Create the collection directory and write a metadata.json
        // only — no content file. The service should refuse the
        // open and surface a WorkflowArtifactNotFoundException.
        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        var collection = Path.GetDirectoryName(contentAbsolute)!;
        Directory.CreateDirectory(collection);
        File.WriteAllText(Path.Combine(collection, "metadata.json"), "{}");

        Assert.Throws<WorkflowArtifactNotFoundException>(() => _storage.OpenFileContent(storagePath));
    }

    [Fact]
    public async Task OpenFileContent_OnDirectoryPathThrows()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_d", "t_d", "a_d", WorkflowArtifactStorageKind.Directory);
        await _storage.WriteDirectoryAsync(
            storagePath,
            new List<WorkflowArtifactDirectoryEntryInput>
            {
                new()
                {
                    RelativePath = "spec.md",
                    Size = 3,
                    OpenContent = () => Bytes("md!"u8.ToArray()),
                },
            },
            new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 3 },
            SampleRecordedAt);

        Assert.Throws<WorkflowArtifactStorageException>(() => _storage.OpenFileContent(storagePath));
    }

    [Fact]
    public async Task WriteDirectoryAsync_PersistsMetadataAndContainedFiles()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_2", "design", "art_2", WorkflowArtifactStorageKind.Directory);

        var entries = new List<WorkflowArtifactDirectoryEntryInput>
        {
            new() { RelativePath = "specs/auth.md", Size = 9, OpenContent = () => Bytes("auth-spec"u8.ToArray()) },
            new() { RelativePath = "specs/data.md", Size = 9, OpenContent = () => Bytes("data-spec"u8.ToArray()) },
            new() { RelativePath = "index.md", Size = 7, OpenContent = () => Bytes("index-x"u8.ToArray()) },
        };

        var result = await _storage.WriteDirectoryAsync(
            storagePath,
            entries,
            new WorkflowArtifactFileWrite
            {
                SourcePath = "specs/",
                Size = 25,
                ContentType = "inode/directory",
            },
            SampleRecordedAt);

        Assert.Equal(WorkflowArtifactStorageKind.Directory, result.Kind);
        Assert.Equal(25, result.Size);
        Assert.Equal(3, result.FileCount);

        var absolute = _storage.ResolveAbsolutePath(storagePath);
        Assert.True(Directory.Exists(absolute));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(absolute)!, "metadata.json")));

        var listing = await _storage.ListDirectoryEntriesAsync(storagePath);
        Assert.Equal(new[] { "index.md", "specs/auth.md", "specs/data.md" },
            listing.Entries.Select(e => e.RelativePath).ToArray());
        Assert.Equal(25, listing.TotalSize);

        // Contained file content is read back from the recorded storage,
        // not from any external path.
        using (var auth = new StreamReader(_storage.OpenDirectoryEntry(storagePath, "specs/auth.md")))
            Assert.Equal("auth-spec", await auth.ReadToEndAsync());
        using (var idx = new StreamReader(_storage.OpenDirectoryEntry(storagePath, "index.md")))
            Assert.Equal("index-x", await idx.ReadToEndAsync());

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.Equal("directory", metadata!.Kind);
        Assert.Equal("specs/", metadata.Path);
        Assert.Equal(3, metadata.FileCount);
    }

    [Fact]
    public async Task WriteDirectoryAsync_RefusesToFollowSymlinkInCollection()
    {
        // The runner is responsible for not sending symlinked
        // entries. The server re-validates the final filesystem
        // shape by refusing to walk any reparse point when listing.
        // We pre-create a directory with a symlink to outside and
        // assert that ListDirectoryEntriesAsync refuses it.
        var storagePath = _storage.GenerateStoragePath(
            "wr_s", "t_s", "a_s", WorkflowArtifactStorageKind.Directory);

        var outside = Path.Combine(_root, $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "top-secret");

        await _storage.WriteDirectoryAsync(
            storagePath,
            new List<WorkflowArtifactDirectoryEntryInput>
            {
                new() { RelativePath = "real.md", Size = 2, OpenContent = () => Bytes("ok"u8.ToArray()) },
            },
            new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 2 },
            SampleRecordedAt);

        // Inject a symlink into the recorded collection.
        var filesRoot = _storage.ResolveAbsolutePath(storagePath);
        var linkPath = Path.Combine(filesRoot, "leak.md");
        try
        {
            File.CreateSymbolicLink(linkPath, Path.Combine(outside, "secret.txt"));
        }
        catch (PlatformNotSupportedException)
        {
            // Windows without developer mode cannot create symlinks.
            // The traversal-refusal path is still covered by the
            // other specs that inject `..` segments, which is the
            // primary concern from the design.
            return;
        }

        var listing = await _storage.ListDirectoryEntriesAsync(storagePath);
        var relativePaths = listing.Entries.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("leak.md", relativePaths);
    }

    [Fact]
    public async Task WriteDirectoryAsync_RefusesPathTraversalSegments()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_t", "t_t", "a_t", WorkflowArtifactStorageKind.Directory);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                storagePath,
                new List<WorkflowArtifactDirectoryEntryInput>
                {
                    new()
                    {
                        RelativePath = "../escape.md",
                        Size = 1,
                        OpenContent = () => Bytes("x"u8.ToArray()),
                    },
                },
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 1 },
                SampleRecordedAt));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RefusesAbsoluteContainedPath()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_a", "t_a", "a_a", WorkflowArtifactStorageKind.Directory);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                storagePath,
                new List<WorkflowArtifactDirectoryEntryInput>
                {
                    new()
                    {
                        RelativePath = "/etc/passwd",
                        Size = 1,
                        OpenContent = () => Bytes("x"u8.ToArray()),
                    },
                },
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 1 },
                SampleRecordedAt));
    }

    [Fact]
    public async Task WriteDirectoryAsync_RefusesEmptyContainedPath()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_e", "t_e", "a_e", WorkflowArtifactStorageKind.Directory);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                storagePath,
                new List<WorkflowArtifactDirectoryEntryInput>
                {
                    new()
                    {
                        RelativePath = "",
                        Size = 1,
                        OpenContent = () => Bytes("x"u8.ToArray()),
                    },
                },
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 1 },
                SampleRecordedAt));
    }

    [Fact]
    public async Task WriteDirectoryAsync_EnforcesFileCountLimit()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_c", "t_c", "a_c", WorkflowArtifactStorageKind.Directory);
        var limits = new WorkflowArtifactDirectoryLimits { MaxFileCount = 2, MaxTotalBytes = 1024, MaxFileBytes = 1024 };
        var storage = new FileSystemWorkflowArtifactStorage(
            _root,
            NullLogger<FileSystemWorkflowArtifactStorage>.Instance,
            limits);

        var entries = Enumerable.Range(0, 5)
            .Select(i => new WorkflowArtifactDirectoryEntryInput
            {
                RelativePath = $"file-{i}.md",
                Size = 1,
                OpenContent = () => Bytes("x"u8.ToArray()),
            })
            .ToList();

        var ex = await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(
                storagePath,
                entries,
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = entries.Count },
                SampleRecordedAt));
        Assert.Contains("file count limit", ex.Message);
    }

    [Fact]
    public async Task WriteDirectoryAsync_EnforcesTotalSizeLimit()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_s2", "t_s2", "a_s2", WorkflowArtifactStorageKind.Directory);
        var limits = new WorkflowArtifactDirectoryLimits { MaxFileCount = 100, MaxTotalBytes = 5, MaxFileBytes = 5 };
        var storage = new FileSystemWorkflowArtifactStorage(
            _root,
            NullLogger<FileSystemWorkflowArtifactStorage>.Instance,
            limits);

        var entries = new List<WorkflowArtifactDirectoryEntryInput>
        {
            new() { RelativePath = "a.md", Size = 3, OpenContent = () => Bytes("aaa"u8.ToArray()) },
            new() { RelativePath = "b.md", Size = 3, OpenContent = () => Bytes("bbb"u8.ToArray()) },
        };

        var ex = await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(
                storagePath,
                entries,
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 6 },
                SampleRecordedAt));
        Assert.Contains("total size limit", ex.Message);
    }

    [Fact]
    public async Task WriteDirectoryAsync_EnforcesSingleFileSizeLimit()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_s3", "t_s3", "a_s3", WorkflowArtifactStorageKind.Directory);
        var limits = new WorkflowArtifactDirectoryLimits { MaxFileCount = 10, MaxTotalBytes = 1024, MaxFileBytes = 4 };
        var storage = new FileSystemWorkflowArtifactStorage(
            _root,
            NullLogger<FileSystemWorkflowArtifactStorage>.Instance,
            limits);

        var entries = new List<WorkflowArtifactDirectoryEntryInput>
        {
            new() { RelativePath = "a.md", Size = 5, OpenContent = () => Bytes("aaaaa"u8.ToArray()) },
        };

        var ex = await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            storage.WriteDirectoryAsync(
                storagePath,
                entries,
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 5 },
                SampleRecordedAt));
        Assert.Contains("single-file size limit", ex.Message);
    }

    [Fact]
    public async Task WriteDirectoryAsync_RefusesDuplicateRelativePath()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_dup", "t_dup", "a_dup", WorkflowArtifactStorageKind.Directory);

        var entries = new List<WorkflowArtifactDirectoryEntryInput>
        {
            new() { RelativePath = "specs/x.md", Size = 1, OpenContent = () => Bytes("a"u8.ToArray()) },
            new() { RelativePath = "specs/x.md", Size = 1, OpenContent = () => Bytes("b"u8.ToArray()) },
        };

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteDirectoryAsync(
                storagePath,
                entries,
                new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 2 },
                SampleRecordedAt));
    }

    [Fact]
    public async Task OpenDirectoryEntry_RefusesTraversalAfterWrite()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_v", "t_v", "a_v", WorkflowArtifactStorageKind.Directory);

        await _storage.WriteDirectoryAsync(
            storagePath,
            new List<WorkflowArtifactDirectoryEntryInput>
            {
                new() { RelativePath = "real.md", Size = 2, OpenContent = () => Bytes("ok"u8.ToArray()) },
            },
            new WorkflowArtifactFileWrite { SourcePath = "specs/", Size = 2 },
            SampleRecordedAt);

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            _storage.OpenDirectoryEntry(storagePath, "../real.md"));
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_MissingCollectionThrows()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_g", "t_g", "a_g", WorkflowArtifactStorageKind.Directory);

        await Assert.ThrowsAsync<WorkflowArtifactNotFoundException>(() =>
            _storage.ListDirectoryEntriesAsync(storagePath));
    }

    [Fact]
    public async Task ReadMetadataAsync_MissingReturnsNull()
    {
        var storagePath = _storage.GenerateStoragePath(
            "wr_nm", "t_nm", "a_nm", WorkflowArtifactStorageKind.File);
        var dir = _storage.ResolveAbsolutePath(storagePath);
        Directory.CreateDirectory(dir);

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.Null(metadata);
    }

    [Fact]
    public void ResolveAbsolutePath_RejectsTraversalInStoragePath()
    {
        Assert.Throws<WorkflowArtifactStorageException>(() => _storage.ResolveAbsolutePath("../escape"));
        Assert.Throws<WorkflowArtifactStorageException>(() => _storage.ResolveAbsolutePath("workflows/../../etc"));
        Assert.Throws<WorkflowArtifactStorageException>(() => _storage.ResolveAbsolutePath(""));
    }

    [Fact]
    public void Constructor_CreatesStorageRootIfMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-mkdir-{Guid.NewGuid():N}");
        try
        {
            var storage = new FileSystemWorkflowArtifactStorage(
                root,
                NullLogger<FileSystemWorkflowArtifactStorage>.Instance);
            Assert.True(Directory.Exists(root));
            Assert.Equal(Path.GetFullPath(root), storage.StorageRoot);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

[Fact]
    public async Task WriteFileAsync_RollsBackWhenContentSizeDoesNotMatchDeclaredSize()
    {
        // The destination is a normal file that is unwriteable in a
        // sense: the content size declared on the stream does not
        // match the actual byte count. The atomic rename must not
        // happen and the destination must not exist.
        var storagePath = _storage.GenerateStoragePath(
            "wr_atomic", "t_atomic", "a_atomic", WorkflowArtifactStorageKind.File);
        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        var collection = Path.GetDirectoryName(contentAbsolute)!;
        Directory.CreateDirectory(collection);

        await Assert.ThrowsAsync<WorkflowArtifactStorageException>(() =>
            _storage.WriteFileAsync(
                storagePath,
                Bytes("short"u8.ToArray()), // 5 bytes
                WriteFor("a", 99),         // declares 99 bytes
                SampleRecordedAt));

        Assert.False(File.Exists(contentAbsolute));
        Assert.False(File.Exists(contentAbsolute + ".tmp"));
    }

    [Fact]
    public async Task WriteFileAsync_MetadataWithChineseSourcePath_PersistsVerbatimOnDisk()
    {
        // T-004 acceptance: a persisted artifact containing Chinese characters
        // is readable on disk (the JSON.Options.Encoder passes non-ASCII through
        // while the indented serializer is used for human-readable files).
        const string sourcePath = "中文目录/工件.md";
        var storagePath = _storage.GenerateStoragePath(
            "wr_zh", "t_zh", "a_zh", WorkflowArtifactStorageKind.File);
        var payload = "# 标题\n\n这是一段中文内容。"u8.ToArray();

        var write = new WorkflowArtifactFileWrite
        {
            SourcePath = sourcePath,
            Size = payload.Length,
            ContentType = "text/markdown",
            ContentHash = "sha256:zh",
        };

        await _storage.WriteFileAsync(
            storagePath,
            Bytes(payload),
            write,
            SampleRecordedAt);

        var contentAbsolute = _storage.ResolveAbsolutePath(storagePath);
        var collection = Path.GetDirectoryName(contentAbsolute)!;
        var metadataPath = Path.Combine(collection, "metadata.json");
        Assert.True(File.Exists(metadataPath));

        var rawJson = await File.ReadAllTextAsync(metadataPath);
        Assert.Contains(sourcePath, rawJson);
        Assert.Contains("中文目录/工件.md", rawJson);
        Assert.DoesNotContain("\\u4e2d", rawJson);
        Assert.DoesNotContain("\\u5de5", rawJson);
        Assert.DoesNotContain("\\u4ef6", rawJson);

        var metadata = await _storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        Assert.Equal(sourcePath, metadata!.Path);

        using var reader = new StreamReader(_storage.OpenFileContent(storagePath));
        var read = await reader.ReadToEndAsync();
        Assert.Contains("这是一段中文内容", read);
    }
}
