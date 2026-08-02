using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

public sealed class AttachmentServiceIngestProviderFileTests
{
    private static readonly DateTimeOffset _frozenNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IngestProviderFileAsync_CreatesPendingRowWithStampedSourceAndPersistedBytes()
    {
        var (database, storage, _, service) = NewStack();
        var projectId = "proj-ingest";
        var deterministicId = "att_deterministic_slack_file_1";
        var payload = "SLACK-BYTES"u8.ToArray();

        var result = await service.IngestProviderFileAsync(
            projectId,
            deterministicId,
            source: "slack",
            fileName: "screenshot.png",
            contentType: "image/png",
            size: payload.LongLength,
            content: new MemoryStream(payload));

        Assert.Equal(deterministicId, result.Id);
        Assert.Equal("screenshot.png", result.FileName);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(payload.LongLength, result.Size);
        Assert.NotNull(result.ExpiresAt);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == deterministicId);
        Assert.Null(row.OwnerKind);
        Assert.Null(row.OwnerId);
        Assert.Null(row.OwnerIssueNumber);
        Assert.Equal("slack", row.Source);
        Assert.Equal(_frozenNow.Add(AttachmentService.PendingTtl), row.ExpiresAt);
        Assert.Equal(storage.GenerateStoragePath(projectId, deterministicId), row.StoragePath);

        Assert.True(storage.Contains(row.StoragePath));
        var metadata = await storage.ReadMetadataAsync(row.StoragePath);
        Assert.NotNull(metadata);
        Assert.Equal(payload.LongLength, metadata!.Size);
        Assert.Equal("screenshot.png", metadata.OriginalFileName);
        Assert.Equal("image/png", metadata.ContentType);
    }

    [Fact]
    public async Task IngestProviderFileAsync_RejectsOversizedStreamBeforeWriting()
    {
        var (database, storage, _, service) = NewStackWithOptions(new AttachmentStorageOptions
        {
            MaxFileBytes = 4,
        });
        var projectId = "proj-ingest-oversized";
        var deterministicId = "att_deterministic_too_big";
        var payload = "LARGER-PAYLOAD"u8.ToArray();

        await Assert.ThrowsAsync<AttachmentLimitException>(() =>
            service.IngestProviderFileAsync(
                projectId,
                deterministicId,
                source: "slack",
                fileName: "huge.bin",
                contentType: "application/octet-stream",
                size: payload.LongLength,
                content: new MemoryStream(payload)));

        await using var db = database.CreateContext();
        Assert.False(await db.Attachments.AsNoTracking().AnyAsync(a => a.Id == deterministicId));
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public async Task IngestProviderFileAsync_IsIdempotentOnDeterministicId_ReturnsExistingAndWritesNothing()
    {
        var (database, storage, _, service) = NewStack();
        var projectId = "proj-ingest-idempotent";
        var deterministicId = "att_deterministic_replay";
        var firstPayload = "FIRST"u8.ToArray();
        var secondPayload = "SECOND-DIFFERENT"u8.ToArray();

        var first = await service.IngestProviderFileAsync(
            projectId,
            deterministicId,
            source: "slack",
            fileName: "first.png",
            contentType: "image/png",
            size: firstPayload.LongLength,
            content: new MemoryStream(firstPayload));

        var bytesAfterFirst = storage.Count;

        var second = await service.IngestProviderFileAsync(
            projectId,
            deterministicId,
            source: "slack",
            fileName: "second.png",
            contentType: "image/png",
            size: secondPayload.LongLength,
            content: new MemoryStream(secondPayload));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(firstPayload.LongLength, second.Size);
        Assert.Equal(first.ExpiresAt, second.ExpiresAt);

        await using var db = database.CreateContext();
        var rows = await db.Attachments.AsNoTracking().Where(a => a.Id == deterministicId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("slack", row.Source);
        Assert.Equal("first.png", row.OriginalFileName);

        Assert.Equal(bytesAfterFirst, storage.Count);
    }

    [Fact]
    public async Task IngestProviderFileAsync_DescriptorFromAcceptanceBatchExposesStampedSource()
    {
        var (_, _, _, service) = NewStack();
        var projectId = "proj-ingest-descriptor";
        var deterministicId = "att_deterministic_descriptor";
        var payload = "BYTES"u8.ToArray();

        await service.IngestProviderFileAsync(
            projectId,
            deterministicId,
            source: "slack",
            fileName: "screenshot.png",
            contentType: "image/png",
            size: payload.LongLength,
            content: new MemoryStream(payload));

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [deterministicId]);

        var accepted = Assert.Single(batch.Results);
        Assert.True(accepted.IsAccepted);
        Assert.NotNull(accepted.Descriptor);
        Assert.Equal("slack", accepted.Descriptor!.Source);
        Assert.Equal("screenshot.png", accepted.Descriptor.OriginalFileName);
        Assert.Equal("image/png", accepted.Descriptor.ContentType);
    }

    [Fact]
    public async Task UploadAsync_StampsUploadSourceAndDescriptorExposesUpload()
    {
        var (_, _, _, service) = NewStack();
        var projectId = "proj-upload-source";

        var upload = await service.UploadAsync(projectId, NewFormFile("note.txt", "text/plain", "hello"u8.ToArray()));

        Assert.NotNull(upload.Id);

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        var accepted = Assert.Single(batch.Results);
        Assert.True(accepted.IsAccepted);
        Assert.NotNull(accepted.Descriptor);
        Assert.Equal(AttachmentService.DefaultUploadSource, accepted.Descriptor!.Source);
        Assert.Equal("upload", accepted.Descriptor.Source);
    }

    private static (TestDatabase Database, FakeAttachmentStorage Storage, FakeTimeProvider Time, AttachmentService Service)
        NewStack()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(_frozenNow);
        var service = new AttachmentService(database.Factory, storage, new AttachmentStorageOptions(), time);
        return (database, storage, time, service);
    }

    private static (TestDatabase Database, FakeAttachmentStorage Storage, FakeTimeProvider Time, AttachmentService Service)
        NewStackWithOptions(AttachmentStorageOptions options)
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(_frozenNow);
        var service = new AttachmentService(database.Factory, storage, options, time);
        return (database, storage, time, service);
    }

    private static TestDatabase NewDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        using (var db = new MohistDbContext(options))
        {
            db.Database.EnsureCreated();
        }
        return new TestDatabase(connection, options, new TestDbContextFactory(options));
    }

    private static IFormFile NewFormFile(string fileName, string contentType, byte[] payload) =>
        new TestFormFile(fileName, contentType, payload.LongLength, payload);

    private sealed class TestDatabase
    {
        public TestDatabase(SqliteConnection keeper, DbContextOptions<MohistDbContext> options, TestDbContextFactory factory)
        {
            Keeper = keeper;
            Options = options;
            Factory = factory;
        }

        public SqliteConnection Keeper { get; }
        public DbContextOptions<MohistDbContext> Options { get; }
        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateContext() => new(Options);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            _options = options;
        }

        public MohistDbContext CreateDbContext() => new(_options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MohistDbContext(_options));
    }

    private sealed class TestFormFile : IFormFile
    {
        private readonly byte[] _payload;

        public TestFormFile(string fileName, string contentType, long declaredLength, byte[] payload)
        {
            FileName = fileName;
            ContentType = contentType;
            Length = declaredLength;
            _payload = payload;
        }

        public string ContentType { get; }
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; }
        public string Name => "file";
        public string FileName { get; }
        public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => OpenReadStream().CopyToAsync(target, cancellationToken);
        public Stream OpenReadStream() => new MemoryStream(_payload, writable: false);
    }
}
