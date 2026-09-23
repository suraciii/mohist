using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Storage;

/// <summary>
/// Design Spec for the read boundary that turns a recorded directory
/// manifest into a listing. The factory is the single owner of the
/// validation shared by the filesystem and in-memory storages, so it is
/// specified hermetically without touching a host filesystem.
/// </summary>
[Trait("level", "L0")]
public class WorkflowArtifactDirectoryListingFactorySpecs
{
    private const string StoragePath = "workflows/wr/tasks/t/artifacts/a/files";

    [Fact]
    public void FromMetadata_SortsOrdinallyAndReturnsRecordedFields()
    {
        var metadata = Directory(
            ("specs/data.md", 9, "sha256:data", "text/markdown"),
            ("index.md", 7, "sha256:index", "text/markdown"),
            ("specs/auth.md", 9, "sha256:auth", "text/markdown"));
        // A deliberately wrong aggregate must not influence the result.
        metadata.Size = 999;

        var listing = WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata);

        Assert.Equal(StoragePath, listing.StoragePath);
        Assert.Equal(
            ["index.md", "specs/auth.md", "specs/data.md"],
            listing.Entries.Select(entry => entry.RelativePath));
        Assert.Equal(
            ["sha256:index", "sha256:auth", "sha256:data"],
            listing.Entries.Select(entry => entry.ContentHash));
        Assert.Equal([7L, 9L, 9L], listing.Entries.Select(entry => entry.Size));
        Assert.All(listing.Entries, entry => Assert.Equal("text/markdown", entry.ContentType));
        Assert.Equal(25, listing.TotalSize);
    }

    [Fact]
    public void FromMetadata_AllowsOmittedContentType()
    {
        var listing = WorkflowArtifactDirectoryListingFactory.FromMetadata(
            StoragePath,
            Directory(("a.md", 1, "sha256:a", null)));

        var entry = Assert.Single(listing.Entries);
        Assert.Null(entry.ContentType);
    }

    [Fact]
    public void FromMetadata_RejectsMissingMetadata()
    {
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, null));
    }

    [Fact]
    public void FromMetadata_RejectsNonDirectoryKind()
    {
        var metadata = Directory(("a.md", 1, "sha256:a", null));
        metadata.Kind = "file";

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Fact]
    public void FromMetadata_RejectsMissingEntryManifest()
    {
        var metadata = Directory(("a.md", 1, "sha256:a", null));
        metadata.Entries = null;

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Fact]
    public void FromMetadata_RejectsNullEntryElement()
    {
        var metadata = Directory(("a.md", 1, "sha256:a", null));
        metadata.Entries = new WorkflowArtifactDirectoryEntry?[] { null }!;

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Fact]
    public void FromMetadata_RejectsDuplicatePathsAfterNormalization()
    {
        var metadata = Directory(
            ("specs/a.md", 1, "sha256:a", null),
            ("specs\\a.md", 1, "sha256:b", null));

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData("specs/../escape.md")]
    [InlineData("specs/./a.md")]
    [InlineData("")]
    public void FromMetadata_RejectsTraversalOrInvalidPaths(string relativePath)
    {
        var metadata = Directory((relativePath, 1, "sha256:a", null));

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Fact]
    public void FromMetadata_RejectsNegativeSize()
    {
        var metadata = Directory(("a.md", -1, "sha256:a", null));

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromMetadata_RejectsEntryWithoutContentHash(string? contentHash)
    {
        var metadata = Directory(("a.md", 1, contentHash!, null));

        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata));
    }

    [Fact]
    public void ParseMetadata_ThenFromMetadata_ReadsPersistedShape()
    {
        const string json = """
        {
          "kind": "directory",
          "entries": [
            { "relativePath": "b.md", "size": 2, "contentHash": "sha256:b", "contentType": "text/markdown" },
            { "relativePath": "a.md", "size": 1, "contentHash": "sha256:a" }
          ]
        }
        """;

        var metadata = WorkflowArtifactDirectoryListingFactory.ParseMetadata(StoragePath, json);
        var listing = WorkflowArtifactDirectoryListingFactory.FromMetadata(StoragePath, metadata);

        Assert.Equal(["a.md", "b.md"], listing.Entries.Select(entry => entry.RelativePath));
        Assert.Equal(3, listing.TotalSize);
        Assert.Equal("sha256:a", listing.Entries[0].ContentHash);
        Assert.Null(listing.Entries[0].ContentType);
        Assert.Equal("sha256:b", listing.Entries[1].ContentHash);
        Assert.Equal("text/markdown", listing.Entries[1].ContentType);
    }

    [Fact]
    public void ParseMetadata_RejectsMalformedJson()
    {
        Assert.Throws<WorkflowArtifactStorageException>(() =>
            WorkflowArtifactDirectoryListingFactory.ParseMetadata(StoragePath, "{ not json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMetadata_ReturnsNullForMissingContent(string? json)
    {
        Assert.Null(WorkflowArtifactDirectoryListingFactory.ParseMetadata(StoragePath, json));
    }

    private static WorkflowArtifactStorageMetadata Directory(
        params (string Path, long Size, string Hash, string? ContentType)[] entries) => new()
    {
        Kind = WorkflowArtifactDirectoryListingFactory.DirectoryKind,
        Entries = entries.Select(entry => new WorkflowArtifactDirectoryEntry
        {
            RelativePath = entry.Path,
            Size = entry.Size,
            ContentHash = entry.Hash,
            ContentType = entry.ContentType,
        }).ToArray(),
    };
}
