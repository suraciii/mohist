using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Artifacts;

/// <summary>
/// Lower-owner coverage for pending artifact upload idempotency and
/// conflict semantics behind
/// <c>POST /api/workflow-runs/{run}/work/{work}/artifact-uploads</c>.
/// The HTTP layer keeps its wire contract (multipart binding, status
/// codes, error codes); the same-key/same-hash and same-key/different-
/// hash state matrix lives here against the production
/// <see cref="WorkflowArtifactUploadService"/>.
/// </summary>
public sealed class WorkflowArtifactUploadServiceTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubWorkContextResolver : IWorkflowArtifactUploadWorkContextResolver
    {
        public Task<WorkflowActiveWorkView?> ResolveAsync(
            string workflowRunId,
            string workId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowActiveWorkView?>(new(
                WorkId: workId,
                WorkType: "spec/task",
                Stage: "review",
                TaskRunId: $"task-{workId}",
                Title: "title",
                ProjectId: "proj-artifact",
                IssueNumber: 1));
    }

    private sealed class FakeArtifactStorage : IWorkflowArtifactStorage
    {
        public List<string> WrittenPaths { get; } = [];

        public string GenerateStoragePath(
            string workflowRunId,
            string taskRunId,
            string artifactId,
            WorkflowArtifactStorageKind kind)
            => $"fake://{workflowRunId}/{taskRunId}/{artifactId}";

        public async Task<WorkflowArtifactStorageWriteResult> WriteFileAsync(
            string storagePath,
            Stream content,
            WorkflowArtifactFileWrite write,
            DateTimeOffset recordedAt,
            CancellationToken cancellationToken = default)
        {
            WrittenPaths.Add(storagePath);
            using var reader = new StreamReader(content, leaveOpen: true);
            var actual = Encoding.UTF8.GetBytes(await reader.ReadToEndAsync(cancellationToken));
            return new WorkflowArtifactStorageWriteResult(
                storagePath,
                WorkflowArtifactStorageKind.File,
                actual.LongLength,
                FileCount: 1);
        }

        public Task<WorkflowArtifactStorageWriteResult> WriteDirectoryAsync(
            string storagePath,
            IReadOnlyList<WorkflowArtifactDirectoryEntryInput> entries,
            WorkflowArtifactFileWrite write,
            DateTimeOffset recordedAt,
            WorkflowArtifactDirectoryLimits? limits = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("directory uploads are not part of these tests");

        public Stream OpenFileContent(string storagePath) => throw new NotSupportedException();

        public Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
            string storagePath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Stream OpenDirectoryEntry(string storagePath, string relativePath) => throw new NotSupportedException();

        public Task<WorkflowArtifactStorageMetadata?> ReadMetadataAsync(
            string storagePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowArtifactStorageMetadata?>(null);

        public Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string StorageRoot => "/fake-artifact-root";

        public string ResolveAbsolutePath(string storagePath) => storagePath;
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var keeper = new SqliteConnection($"Data Source=artifact-upload-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(keeper).Options);
        var time = new FakeTimeProvider(FixedTime);
        var storage = new FakeArtifactStorage();
        var service = new WorkflowArtifactUploadService(
            factory, storage, new StubWorkContextResolver(),
            NullLogger<WorkflowArtifactUploadService>.Instance, time,
            WorkflowArtifactUploadService.DefaultPendingTtl);
        return new Harness(service, factory, storage, keeper);
    }

    private sealed record Harness(
        WorkflowArtifactUploadService Service,
        TestDbContextFactory Factory,
        FakeArtifactStorage Storage,
        SqliteConnection Keeper) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Keeper.DisposeAsync();
    }

    private static WorkflowArtifactUploadRequest Request(
        string workflowRunId,
        string workId,
        string path,
        byte[] payload,
        string contentHash) => new()
    {
        WorkflowRunId = workflowRunId,
        WorkId = workId,
        Path = path,
        ContentType = "text/markdown",
        ContentHash = contentHash,
        Size = payload.LongLength,
        OpenContent = () => new MemoryStream(payload, writable: false),
    };

    private static async Task<int> CountPendingRowsAsync(TestDbContextFactory factory, string workflowRunId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .CountAsync(p => p.WorkflowRunId == workflowRunId);
    }

    [Fact]
    public async Task Upload_SameKeySameHash_IsIdempotentWithSinglePendingRow()
    {
        await using var harness = await CreateHarnessAsync();
        const string workflowRunId = "wr-artifact-idem";
        const string workId = "task-1.1";
        var payload = Encoding.UTF8.GetBytes("identical content");

        var first = await harness.Service.UploadAsync(Request(workflowRunId, workId, "review.md", payload, "sha256:stable"));
        var second = await harness.Service.UploadAsync(Request(workflowRunId, workId, "review.md", payload, "sha256:stable"));

        Assert.Equal(WorkflowArtifactUploadResultKind.Created, first.Kind);
        Assert.Equal(WorkflowArtifactUploadResultKind.Idempotent, second.Kind);
        Assert.Equal(first.Pending!.UploadId, second.Pending!.UploadId);
        Assert.Single(harness.Storage.WrittenPaths);
        Assert.Equal(1, await CountPendingRowsAsync(harness.Factory, workflowRunId));
    }

    [Fact]
    public async Task Upload_SameKeyDifferentHash_ConflictsAndPreservesOriginal()
    {
        await using var harness = await CreateHarnessAsync();
        const string workflowRunId = "wr-artifact-conflict";
        const string workId = "task-1.1";

        var first = await harness.Service.UploadAsync(
            Request(workflowRunId, workId, "review.md", Encoding.UTF8.GetBytes("first"), "sha256:aaa"));
        var conflict = await harness.Service.UploadAsync(
            Request(workflowRunId, workId, "review.md", Encoding.UTF8.GetBytes("second"), "sha256:bbb"));

        Assert.Equal(WorkflowArtifactUploadResultKind.Created, first.Kind);
        Assert.Equal(WorkflowArtifactUploadResultKind.Conflict, conflict.Kind);
        Assert.Equal("sha256:aaa", conflict.Conflict!.ExistingContentHash);
        Assert.Equal("sha256:bbb", conflict.Conflict.IncomingContentHash);

        await using var db = await harness.Factory.CreateDbContextAsync();
        var row = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .SingleAsync(p => p.WorkflowRunId == workflowRunId);
        Assert.Equal("sha256:aaa", row.ContentHash);
        Assert.Single(harness.Storage.WrittenPaths);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
