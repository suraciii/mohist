using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

[Collection("MohistDb")]
public sealed class AttachmentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string Root = "/test/attachment-service";

    private readonly MohistDbFixture _fixture;
    private readonly InMemoryStorageFileSystem _files = new();
    private readonly FileSystemAttachmentStorage _storage;

    public AttachmentServiceTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
        _storage = new FileSystemAttachmentStorage(
            Root,
            NullLogger<FileSystemAttachmentStorage>.Instance,
            _files);
    }

    [Fact]
    public async Task UploadAsync_RejectsStreamThatExceedsDeclaredSizeLimit()
    {
        var service = CreateService(new AttachmentStorageOptions { MaxFileBytes = 4 });

        await Assert.ThrowsAsync<AttachmentLimitException>(() => service.UploadAsync(
            "proj_limit",
            new TestFormFile("too-big.txt", "text/plain", declaredLength: 1, payload: "12345"u8.ToArray())));

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.False(await db.Attachments.AnyAsync(a => a.ProjectId == "proj_limit"));
        Assert.True(_files.IsDirectoryEmpty(Root));
    }

    [Fact]
    public async Task CleanupExpiredPending_RemovesRowsAndStoredContent()
    {
        var service = CreateService();
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var storagePath = _storage.GenerateStoragePath("proj_cleanup", "att_cleanup");
        await _storage.WriteFileAsync(storagePath, new MemoryStream("old"u8.ToArray()), new AttachmentFileWrite
        {
            OriginalFileName = "old.txt",
            ContentType = "text/plain",
            Size = 3,
        }, Now.AddDays(-2));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Attachments.Add(new AttachmentRow
            {
                Id = "att_cleanup",
                ProjectId = "proj_cleanup",
                OriginalFileName = "old.txt",
                ContentType = "text/plain",
                Size = 3,
                StoragePath = storagePath,
                CreatedAt = Now.AddDays(-2),
                ExpiresAt = Now.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var removed = await service.CleanupExpiredPendingAsync();

        Assert.Equal(1, removed);
        await using var verify = await dbFactory.CreateDbContextAsync();
        Assert.False(await verify.Attachments.AnyAsync(a => a.Id == "att_cleanup"));
        Assert.False(_files.FileExists(_storage.ResolveAbsolutePath(storagePath)));
    }

    private AttachmentService CreateService(AttachmentStorageOptions? options = null)
    {
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        return new AttachmentService(
            dbFactory,
            _storage,
            options ?? new AttachmentStorageOptions(),
            new FixedTimeProvider(Now));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
