using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Artifacts;

[Collection("MohistDb")]
public class WorkflowArtifactUploadServiceSpecs
{
    private readonly MohistDbFixture _fixture;
    private readonly InMemoryWorkflowArtifactStorage _storage = new();

    public WorkflowArtifactUploadServiceSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private WorkflowArtifactUploadService BuildService(StubWorkContextResolver? resolver = null)
    {
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        resolver ??= new StubWorkContextResolver();
        return new WorkflowArtifactUploadService(
            dbFactory,
            _storage,
            resolver,
            NullLogger<WorkflowArtifactUploadService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero)),
            TimeSpan.FromHours(24));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task UploadAsync_NewUploadCreatesPendingRowAndContent()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);
        var payload = Bytes("hello world");
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:abc",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Created, result.Kind);
        Assert.NotNull(result.Pending);
        Assert.StartsWith("artup_", result.Pending!.UploadId);
        Assert.Equal(workflowRunId, result.Pending.WorkflowRunId);
        Assert.Equal(workId, result.Pending.WorkId);
        Assert.Equal(taskRunId, result.Pending.TaskRunId);
        Assert.Equal("review.md", result.Pending.Path);
        Assert.Equal("text/markdown", result.Pending.ContentType);
        Assert.Equal("sha256:abc", result.Pending.ContentHash);
        Assert.Equal(payload.LongLength, result.Pending.Size);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UploadId == result.Pending.UploadId);
        Assert.NotNull(row);
        Assert.Equal(workflowRunId, row!.WorkflowRunId);
        Assert.Equal(workId, row.WorkId);
        Assert.Equal(taskRunId, row.TaskRunId);
        Assert.Equal("review.md", row.Path);

        await using var stored = _storage.OpenFileContent(row.StoragePath);
        await using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task UploadAsync_SameKeySameHashReturnsExistingId()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);
        var payload = Bytes("first content");
        var first = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:same",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, first.Kind);

        var second = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:same",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Idempotent, second.Kind);
        Assert.Equal(first.Pending!.UploadId, second.Pending!.UploadId);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .Where(p => p.WorkflowRunId == workflowRunId)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task UploadAsync_SameKeyDifferentHashReturnsConflict()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);
        var firstPayload = Bytes("first");
        var first = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:aaa",
            Size = firstPayload.LongLength,
            OpenContent = () => new MemoryStream(firstPayload, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, first.Kind);

        var secondPayload = Bytes("second");
        var second = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:bbb",
            Size = secondPayload.LongLength,
            OpenContent = () => new MemoryStream(secondPayload, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Conflict, second.Kind);
        Assert.NotNull(second.Conflict);
        Assert.Equal(first.Pending!.UploadId, second.Conflict!.UploadId);
        Assert.Equal("sha256:aaa", second.Conflict.ExistingContentHash);
        Assert.Equal("sha256:bbb", second.Conflict.IncomingContentHash);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstAsync(p => p.WorkflowRunId == workflowRunId);
        await using var stored = _storage.OpenFileContent(row.StoragePath);
        await using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(firstPayload, buffer.ToArray());
    }

    [Fact]
    public async Task UploadAsync_UnknownWorkItemReturnsWorkItemNotFound()
    {
        var service = BuildService();
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = $"wr_{Guid.NewGuid():N}",
            WorkId = "missing-work",
            Path = "review.md",
            ContentHash = "sha256:abc",
            Size = 5,
            OpenContent = () => new MemoryStream(Bytes("hello")),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.WorkItemNotFound, result.Kind);
    }

    [Fact]
    public async Task UploadAsync_MissingPathReturnsInvalid()
    {
        var service = BuildService();
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = $"wr_{Guid.NewGuid():N}",
            WorkId = "task-1.1",
            Path = string.Empty,
            Size = 0,
            OpenContent = () => new MemoryStream(Array.Empty<byte>()),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task UploadAsync_StoragePathIsGeneratedAndSourcePathNotUsedAsPathSegment()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);
        var sourcePath = "../../../etc/passwd-like name";
        var payload = Bytes("data");
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = sourcePath,
            ContentType = "text/plain",
            ContentHash = "sha256:gen",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Created, result.Kind);
        // The storage path on disk is generated and does not embed
        // the source path. We assert against the persisted row,
        // which is the source of truth for "where is the content
        // stored".
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstAsync(p => p.UploadId == result.Pending!.UploadId);
        Assert.StartsWith("workflows/", row.StoragePath);
        Assert.Contains($"/tasks/{taskRunId}/artifacts/{row.UploadId}/content", row.StoragePath);
        Assert.DoesNotContain("..", row.StoragePath);
        Assert.DoesNotContain("passwd", row.StoragePath);
        Assert.DoesNotContain("etc", row.StoragePath);

        // The original source path remains a display field on the
        // row, not a path segment.
        Assert.Equal(sourcePath, row.Path);
    }

    [Fact]
    public async Task UploadAsync_PendingUploadsAreNotVisibleInWorkflowArtifactQueries()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);


        var service = BuildService(resolver);
        var payload = Bytes("review v1");
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:pending",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, result.Kind);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var latest = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == workflowRunId && a.Path == "review.md")
            .ToListAsync();
        Assert.Empty(latest);

        var byTask = await db.WorkflowArtifacts
            .Where(a => a.WorkflowRunId == workflowRunId && a.TaskRunId == taskRunId)
            .ToListAsync();
        Assert.Empty(byTask);

        var pendingRows = await db.WorkflowArtifactPendingUploads
            .Where(p => p.WorkflowRunId == workflowRunId)
            .ToListAsync();
        Assert.Single(pendingRows);
    }

    [Fact]
    public async Task UploadAsync_DirectoryContent_DecodesEnvelopeAndPersistsFilesUnderFilesRoot()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);

        // Build a directory envelope mirroring what the runner
        // produces: a JSON object with kind=directory and base64
        // entries that are decoded server-side.
        var fileA = Bytes("alpha content");
        var fileB = Bytes("beta bytes");
        var envelopeJson = "{" +
            "\"kind\":\"directory\"," +
            "\"files\":[" +
            $"{{\"path\":\"a.md\",\"size\":{fileA.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileA)}\"}}," +
            $"{{\"path\":\"sub/b.md\",\"size\":{fileB.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileB)}\"}}" +
            "]}";
        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeJson);
        var totalBytes = fileA.LongLength + fileB.LongLength;

        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:dir",
            Size = envelopeBytes.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytes, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Created, result.Kind);
        Assert.Equal("directory", result.Pending!.Kind);
        Assert.Equal(2, result.Pending.FileCount);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .FirstAsync(p => p.UploadId == result.Pending.UploadId);

        Assert.Equal("directory", row.Kind);
        Assert.Equal(2, row.FileCount);
        Assert.Equal(totalBytes, row.Size);
        Assert.EndsWith("/files", row.StoragePath);
        Assert.DoesNotContain("specs", row.StoragePath);

        await using var aContent = _storage.OpenDirectoryEntry(row.StoragePath, "a.md");
        await using var aBuffer = new MemoryStream();
        await aContent.CopyToAsync(aBuffer);
        Assert.Equal(fileA, aBuffer.ToArray());

        await using var bContent = _storage.OpenDirectoryEntry(row.StoragePath, "sub/b.md");
        await using var bBuffer = new MemoryStream();
        await bContent.CopyToAsync(bBuffer);
        Assert.Equal(fileB, bBuffer.ToArray());

        var metadata = await _storage.ReadMetadataAsync(row.StoragePath);
        Assert.NotNull(metadata);
        Assert.Equal("directory", metadata!.Kind);
    }

    [Fact]
    public async Task UploadAsync_DirectoryContent_SameKeySameHashIsIdempotent()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);
        var fileA = Bytes("alpha content");
        var envelopeJson = "{" +
            "\"kind\":\"directory\"," +
            "\"files\":[" +
            $"{{\"path\":\"a.md\",\"size\":{fileA.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileA)}\"}}" +
            "]}";
        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeJson);

        var first = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:dir-same",
            Size = envelopeBytes.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytes, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, first.Kind);

        var second = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:dir-same",
            Size = envelopeBytes.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytes, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Idempotent, second.Kind);
        Assert.Equal("directory", second.Pending!.Kind);
        Assert.Equal(first.Pending!.UploadId, second.Pending.UploadId);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .Where(p => p.WorkflowRunId == workflowRunId)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task UploadAsync_DirectoryContent_DifferentHashIsConflict()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var service = BuildService(resolver);
        var fileA = Bytes("alpha content");
        var envelopeJsonA = "{" +
            "\"kind\":\"directory\"," +
            "\"files\":[" +
            $"{{\"path\":\"a.md\",\"size\":{fileA.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileA)}\"}}" +
            "]}";
        var envelopeBytesA = Encoding.UTF8.GetBytes(envelopeJsonA);

        var first = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:dir-a",
            Size = envelopeBytesA.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytesA, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, first.Kind);

        var fileB = Bytes("beta content");
        var envelopeJsonB = "{" +
            "\"kind\":\"directory\"," +
            "\"files\":[" +
            $"{{\"path\":\"a.md\",\"size\":{fileB.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileB)}\"}}" +
            "]}";
        var envelopeBytesB = Encoding.UTF8.GetBytes(envelopeJsonB);

        var second = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:dir-b",
            Size = envelopeBytesB.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytesB, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Conflict, second.Kind);
        Assert.NotNull(second.Conflict);
        Assert.Equal("sha256:dir-a", second.Conflict!.ExistingContentHash);
        Assert.Equal("sha256:dir-b", second.Conflict.IncomingContentHash);
    }

    [Theory]
    [InlineData("not-valid-json")]
    [InlineData("{\"kind\":\"directory\",\"files\":[]}")]
    [InlineData("{\"kind\":\"file\",\"files\":[{\"path\":\"a.md\",\"data\":\"YQ==\"}]}")]
    [InlineData("{\"kind\":\"directory\",\"files\":[{\"path\":\"a.md\",\"data\":\"!!!\"}]}")]
    public async Task UploadAsync_DirectoryContent_MalformedEnvelopeReturnsInvalid(string envelopeJson)
    {
        // Directory envelope validation failures (bad JSON, wrong kind,
        // empty file list, invalid base64) must surface as an Invalid
        // result rather than throwing, so the upload endpoint returns a
        // diagnosable 400 instead of an opaque 500.
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = "task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);
        var service = BuildService(resolver);

        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeJson);
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:bad",
            Size = envelopeBytes.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytes, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Invalid, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task UploadAsync_DirectoryContent_EnvelopeSizeMismatchReturnsInvalid()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = "task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);
        var service = BuildService(resolver);

        var envelopeJson = "{\"kind\":\"directory\",\"files\":[{\"path\":\"a.md\",\"size\":1,\"data\":\"YQ==\"}]}";
        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeJson);
        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:mismatch",
            Size = envelopeBytes.LongLength + 100,
            OpenContent = () => new MemoryStream(envelopeBytes, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Invalid, result.Kind);
        Assert.Contains("mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadAsync_WhenRequestIsCancelledDuringRollback_CleanupUsesIndependentToken()
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, "task-1.1");
        var service = BuildService(resolver);
        using var cancellation = new CancellationTokenSource();
        _storage.BeforeDelete = cancellation.Cancel;

        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:bad",
            Size = 1,
            OpenContent = () => new MemoryStream(Bytes("{"), writable: false),
        }, cancellation.Token);

        Assert.Equal(WorkflowArtifactUploadResultKind.Invalid, result.Kind);
        Assert.Equal(CancellationToken.None, _storage.LastDeleteCancellationToken);
    }

    [Fact]
    public async Task BindAsync_DirectoryPendingUpload_BindsAsDirectoryKind()
    {
        // End-to-end: drive a real runner upload through the upload
        // service for a directory artifact, then bind through the
        // bind service and assert the bound row is a directory and
        // the storage layout contains the contained files.
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var uploadService = BuildService(resolver);
        var fileA = Bytes("alpha content");
        var fileB = Bytes("beta content");
        var envelopeJson = "{" +
            "\"kind\":\"directory\"," +
            "\"files\":[" +
            $"{{\"path\":\"a.md\",\"size\":{fileA.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileA)}\"}}," +
            $"{{\"path\":\"sub/b.md\",\"size\":{fileB.LongLength},\"contentType\":\"text/markdown\",\"data\":\"{Convert.ToBase64String(fileB)}\"}}" +
            "]}";
        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeJson);

        var uploaded = await uploadService.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = "application/x-mohist-artifact-directory",
            ContentHash = "sha256:bind-dir",
            Size = envelopeBytes.LongLength,
            OpenContent = () => new MemoryStream(envelopeBytes, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, uploaded.Kind);

        // Bind the upload through the real bind service.
        var bindService = new WorkflowArtifactBindService(
            _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            NullLogger<WorkflowArtifactBindService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero)));
        var bindResult = await bindService.BindAsync(
            workflowRunId,
            workId,
            taskRunId,
            [uploaded.Pending!.UploadId],
            declaredArtifacts: null,
            projectId: "proj_bind",
            issueNumber: 42);
        Assert.True(bindResult.IsSuccess, bindResult.Error);
        Assert.Single(bindResult.ArtifactRecordedEvents);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var bound = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId)
            .SingleAsync();
        Assert.Equal("directory", bound.Kind);
        Assert.Equal("specs", bound.Path);
        Assert.Equal(taskRunId, bound.TaskRunId);
        Assert.Equal("proj_bind", bound.ProjectId);
        Assert.Equal(42, bound.IssueNumber);
        Assert.EndsWith("/files", bound.ArtifactStoragePath);
        Assert.Equal(fileA.LongLength + fileB.LongLength, bound.Size);

        var aStream = _storage.OpenDirectoryEntry(bound.ArtifactStoragePath, "a.md");
        var aBytes = await new StreamReader(aStream).ReadToEndAsync();
        Assert.Equal("alpha content", Encoding.UTF8.GetString(fileA));
        Assert.Equal("alpha content", aBytes);

        var bStream = _storage.OpenDirectoryEntry(bound.ArtifactStoragePath, "sub/b.md");
        var bBytes = await new StreamReader(bStream).ReadToEndAsync();
        Assert.Equal("beta content", Encoding.UTF8.GetString(fileB));
        Assert.Equal("beta content", bBytes);
    }

    [Fact]
    public async Task BindAsync_DeclaredPathWithTemplateVariable_RendersAndMatchesUploadedPath()
    {
        // The default workflow declares every artifact `path` as a
         // issue-number-based template. The runner
        // renders that template before upload, so the pending upload
        // records the resolved workspace path. The bind service must
        // render the declared path with the same variables so the
        // comparison key agrees on both sides.
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var uploadService = BuildService(resolver);
        var payload = Bytes("looks good");
        var uploaded = await uploadService.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "openspec/changes/issue-55/review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:tpl",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, uploaded.Kind);

        // Bind with a declared path that uses a template variable.
        // The variables mirror what the grain would resolve at bind
        // time (see WorkflowGrain.ResolveBindVariablesAsync).
         var variables = JsonDocument.Parse(
             "{\"issue\":{\"number\":55}}").RootElement.Clone();

        var bindService = new WorkflowArtifactBindService(
            _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            NullLogger<WorkflowArtifactBindService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero)));
        var declaredArtifacts = new TaskArtifactCapture(
            new List<TaskArtifactDeclaration>
            {
             new("openspec/changes/issue-${{ issue.number }}/review.md"),
            });
        var bindResult = await bindService.BindAsync(
            workflowRunId, workId, taskRunId,
            [uploaded.Pending!.UploadId], declaredArtifacts, variables: variables);

        Assert.True(bindResult.IsSuccess, bindResult.Error);
        Assert.Single(bindResult.ArtifactRecordedEvents);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var bound = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId)
            .SingleAsync();
        Assert.Equal("openspec/changes/issue-55/review.md", bound.Path);
        Assert.Equal(taskRunId, bound.TaskRunId);
        Assert.Equal("file", bound.Kind);
    }

    [Fact]
    public async Task BindAsync_DeclaredPathWithMissingTemplateVariable_BindsUploadRegardless()
    {
        // With best-effort artifacts, declared path template validation
        // is the runner's responsibility at capture time. The bind
        // service no longer cross-checks declared paths against
        // uploaded paths, so an undefined template variable does not
        // prevent binding.
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var taskRunId = $"task-1.1";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, taskRunId);

        var uploadService = BuildService(resolver);
        var payload = Bytes("looks good");
        var uploaded = await uploadService.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "openspec/changes/issue-55/review.md",
            ContentType = "text/markdown",
            ContentHash = "sha256:missing-var",
            Size = payload.LongLength,
            OpenContent = () => new MemoryStream(payload, writable: false),
        });
        Assert.Equal(WorkflowArtifactUploadResultKind.Created, uploaded.Kind);

        var variables = JsonDocument.Parse("{}").RootElement.Clone();

        var bindService = new WorkflowArtifactBindService(
            _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            NullLogger<WorkflowArtifactBindService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero)));
        var declaredArtifacts = new TaskArtifactCapture(
            new List<TaskArtifactDeclaration>
            {
             new("openspec/changes/issue-${{ issue.number }}/review.md"),
            });
        var bindResult = await bindService.BindAsync(
            workflowRunId, workId, taskRunId,
            [uploaded.Pending!.UploadId], declaredArtifacts, variables: variables);

        Assert.True(bindResult.IsSuccess, bindResult.Error ?? "expected success");
        Assert.Single(bindResult.ArtifactRecordedEvents);
    }
}
