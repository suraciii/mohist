using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.TestSupport;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

/// <summary>
/// Lower-owner coverage for <see cref="AttachmentService"/> upload
/// limits and expired-pending cleanup. Moved from the attachment API
/// specs: these never exercised the HTTP surface, and the API layer
/// keeps only its wire contract.
/// </summary>
public sealed class AttachmentServiceLimitsTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<TestDbContextFactory> CreateFactoryAsync()
    {
        var keeper = new SqliteConnection($"Data Source=attachment-limits-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
        return new TestDbContextFactory(
            new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(keeper).Options);
    }

    [Fact]
    public async Task UploadAsync_RejectsStreamThatExceedsDeclaredSizeLimit()
    {
        var factory = await CreateFactoryAsync();
        var storage = new InMemoryAttachmentStorage();
        var service = new AttachmentService(
            factory,
            storage,
            new AttachmentStorageOptions { MaxFileBytes = 4 },
            new FakeTimeProvider(FixedTime));

        await Assert.ThrowsAsync<AttachmentLimitException>(() => service.UploadAsync(
            "proj_limit",
            new TestFormFile("too-big.txt", "text/plain", declaredLength: 1, payload: "12345"u8.ToArray())));

        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.Attachments.AnyAsync(a => a.ProjectId == "proj_limit"));
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public async Task CleanupExpiredPending_RemovesRowsAndStoredContent()
    {
        var factory = await CreateFactoryAsync();
        var time = new FakeTimeProvider(FixedTime);
        var storage = new InMemoryAttachmentStorage();
        var service = new AttachmentService(
            factory,
            storage,
            new AttachmentStorageOptions(),
            time);
        var storagePath = storage.GenerateStoragePath("proj_cleanup", "att_cleanup");
        await storage.WriteFileAsync(storagePath, new MemoryStream("old"u8.ToArray()), new AttachmentFileWrite
        {
            OriginalFileName = "old.txt",
            ContentType = "text/plain",
            Size = 3,
        }, time.GetUtcNow());

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Attachments.Add(new AttachmentRow
            {
                Id = "att_cleanup",
                ProjectId = "proj_cleanup",
                OriginalFileName = "old.txt",
                ContentType = "text/plain",
                Size = 3,
                StoragePath = storagePath,
                CreatedAt = FixedTime.AddDays(-2),
                ExpiresAt = FixedTime.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var removed = await service.CleanupExpiredPendingAsync();

        Assert.Equal(1, removed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.False(await verify.Attachments.AnyAsync(a => a.Id == "att_cleanup"));
        Assert.False(storage.Contains(storagePath));
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

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
