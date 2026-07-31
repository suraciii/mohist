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

public sealed class AttachmentServiceAgentInputTests
{
    [Fact]
    public async Task BindAgentInputAsync_SetsOwnerKindClearsExpiryAndProtectsFromPendingCleanup()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var service = NewService(database, storage, time);
        var projectId = "proj-agent-input";
        var sessionId = "session-1";
        var inputId = "input-1";

        var upload = await service.UploadAsync(projectId, NewFormFile("design.png", "image/png", "PNG"u8.ToArray()));
        Assert.NotNull(upload.ExpiresAt);
        var originalExpiresAt = upload.ExpiresAt;
        var storagePath = storage.GenerateStoragePath(projectId, upload.Id);

        time.SetUtcNow(originalExpiresAt!.Value.AddHours(1));

        await service.BindAgentInputAsync(projectId, sessionId, inputId, [upload.Id]);

        await using (var db = database.CreateContext())
        {
            var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
            Assert.Equal(AttachmentService.OwnerKindAgentInput, row.OwnerKind);
            Assert.Equal(AttachmentService.BuildAgentInputOwnerId(sessionId, inputId), row.OwnerId);
            Assert.Null(row.OwnerIssueNumber);
            Assert.Null(row.ExpiresAt);
            Assert.Equal("design.png", row.OriginalFileName);
            Assert.Equal("image/png", row.ContentType);
            Assert.True(row.Size > 0);
        }

        var removed = await service.CleanupExpiredPendingAsync();
        Assert.Equal(0, removed);

        await using (var verify = database.CreateContext())
        {
            Assert.True(await verify.Attachments.AnyAsync(a => a.Id == upload.Id));
        }
        Assert.True(storage.Contains(storagePath));
    }

    [Fact]
    public async Task BindAgentInputAsync_StillRemovesUnboundPendingUploadsAfterExpiry()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var service = NewService(database, storage, time);
        var projectId = "proj-agent-input-unbound";
        var boundUpload = await service.UploadAsync(projectId, NewFormFile("attached.txt", "text/plain", "kept"u8.ToArray()));
        var orphanUpload = await service.UploadAsync(projectId, NewFormFile("orphan.txt", "text/plain", "gone"u8.ToArray()));

        await service.BindAgentInputAsync(projectId, "session-A", "input-A", [boundUpload.Id]);

        time.SetUtcNow(orphanUpload.ExpiresAt!.Value.AddHours(2));

        var removed = await service.CleanupExpiredPendingAsync();
        Assert.Equal(1, removed);

        await using var db = database.CreateContext();
        Assert.True(await db.Attachments.AnyAsync(a => a.Id == boundUpload.Id));
        Assert.False(await db.Attachments.AnyAsync(a => a.Id == orphanUpload.Id));
    }

    [Fact]
    public async Task BindAgentInputAsync_RejectsAttachmentAlreadyOwnedByDifferentOwner()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var service = NewService(database, storage, time);
        var projectId = "proj-agent-input-collision";
        var upload = await service.UploadAsync(projectId, NewFormFile("diagram.png", "image/png", "PNG"u8.ToArray()));

        await service.BindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        await Assert.ThrowsAsync<AttachmentValidationException>(() =>
            service.BindAgentInputAsync(projectId, "session-2", "input-2", [upload.Id]));

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Equal(AttachmentService.OwnerKindAgentInput, row.OwnerKind);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId("session-1", "input-1"), row.OwnerId);
    }

    [Fact]
    public async Task BindAgentInputAsync_StoredRecordExposesUserMetadataAndOmitsCallerSecrets()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var service = NewService(database, storage, time);
        var projectId = "proj-agent-input-meta";
        var sessionId = "session-meta";
        var inputId = "input-meta";
        var upload = await service.UploadAsync(projectId, NewFormFile("report.pdf", "application/pdf", "PDFDATA"u8.ToArray()));

        await service.BindAgentInputAsync(projectId, sessionId, inputId, [upload.Id]);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);

        Assert.Equal("report.pdf", row.OriginalFileName);
        Assert.Equal("application/pdf", row.ContentType);
        Assert.Equal("PDFDATA"u8.ToArray().LongLength, row.Size);
        Assert.Equal(AttachmentService.OwnerKindAgentInput, row.OwnerKind);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId(sessionId, inputId), row.OwnerId);

        var metadata = await storage.ReadMetadataAsync(row.StoragePath);
        Assert.NotNull(metadata);
        Assert.Equal("report.pdf", metadata!.OriginalFileName);
        Assert.Equal("application/pdf", metadata.ContentType);
        Assert.Equal("PDFDATA"u8.ToArray().LongLength, metadata.Size);

        var properties = typeof(AttachmentRow).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("OriginalFileName", properties);
        Assert.Contains("ContentType", properties);
        Assert.Contains("Size", properties);
        Assert.Contains("OwnerKind", properties);
        Assert.DoesNotContain("PlatformEventPayload", properties);
        Assert.DoesNotContain("CallerAccessToken", properties);
        Assert.DoesNotContain("TemporaryDownloadUrl", properties);
    }

    private static AttachmentService NewService(
        TestDatabase database,
        FakeAttachmentStorage storage,
        FakeTimeProvider time) =>
        new(database.Factory, storage, new AttachmentStorageOptions(), time);

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