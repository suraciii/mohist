using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

public sealed class AttachmentServiceValidateAndBindAgentInputTests
{
    private static readonly DateTimeOffset _frozenNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidateAndBindAgentInput_AcceptsValidAttachmentWithDescriptor()
    {
        var (database, _, _, service) = NewStack();
        var projectId = "proj-accept";
        var sessionId = "session-1";
        var inputId = "input-1";
        var upload = await service.UploadAsync(projectId, NewFormFile("notes.txt", "text/plain", "hello"u8.ToArray()));

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, sessionId, inputId, [upload.Id]);

        var accepted = Assert.Single(batch.Results);
        Assert.Equal(upload.Id, accepted.Id);
        Assert.True(accepted.IsAccepted);
        Assert.NotNull(accepted.Descriptor);
        Assert.Null(accepted.RejectionReason);
        Assert.Null(accepted.RejectionMessage);
        Assert.Equal(upload.Id, accepted.Descriptor!.Id);
        Assert.Equal("notes.txt", accepted.Descriptor.OriginalFileName);
        Assert.Equal("text/plain", accepted.Descriptor.ContentType);
        Assert.Equal("hello"u8.ToArray().LongLength, accepted.Descriptor.Size);
        Assert.Equal("upload", accepted.Descriptor.Source);
        Assert.Equal("usable", accepted.Descriptor.Availability);
        Assert.Equal(1, batch.AcceptedCount);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Equal(AttachmentService.OwnerKindAgentInput, row.OwnerKind);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId(sessionId, inputId), row.OwnerId);
        Assert.Null(row.ExpiresAt);
    }

    [Fact]
    public async Task UnbindAgentInput_RestoresPendingOwnershipAndExpiry()
    {
        var (database, _, time, service) = NewStack();
        var projectId = "proj-unbind";
        var upload = await service.UploadAsync(projectId, NewFormFile("notes.txt", "text/plain", "hello"u8.ToArray()));

        await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);
        time.Advance(TimeSpan.FromHours(1));
        await service.UnbindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(attachment => attachment.Id == upload.Id);
        Assert.Null(row.OwnerKind);
        Assert.Null(row.OwnerId);
        Assert.Equal(time.GetUtcNow().Add(AttachmentService.PendingTtl), row.ExpiresAt);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_CancellationAfterFirstClaimRollsBackWholeBatch()
    {
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterFirstAgentInputBindCommandInterceptor(cancellation);
        var database = NewDatabase(interceptor);
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(_frozenNow);
        var service = new AttachmentService(database.Factory, storage, new AttachmentStorageOptions(), time);
        var projectId = "proj-cancelled-batch";
        var first = await service.UploadAsync(projectId, NewFormFile("first.txt", "text/plain", "first"u8.ToArray()));
        var second = await service.UploadAsync(projectId, NewFormFile("second.txt", "text/plain", "second"u8.ToArray()));

        interceptor.Arm();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [first.Id, second.Id], cancellation.Token));

        Assert.True(interceptor.Cancelled);
        await using var db = database.CreateContext();
        var rows = await db.Attachments.AsNoTracking().Where(row => row.ProjectId == projectId).OrderBy(row => row.Id).ToListAsync();
        Assert.All(rows, row =>
        {
            Assert.Null(row.OwnerKind);
            Assert.Null(row.OwnerId);
            Assert.Equal(_frozenNow.Add(AttachmentService.PendingTtl), row.ExpiresAt);
        });
        Assert.Null(await service.OpenAgentInputContentAsync(projectId, "session-1", "input-1", first.Id));
        Assert.Null(await service.OpenAgentInputContentAsync(projectId, "session-1", "input-1", second.Id));
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_RejectsNotFoundWithReason()
    {
        var (_, _, _, service) = NewStack();

        var batch = await service.ValidateAndBindAgentInputAsync("proj-notfound", "session", "input", ["att_does_not_exist"]);

        var rejected = Assert.Single(batch.Results);
        Assert.Equal("att_does_not_exist", rejected.Id);
        Assert.False(rejected.IsAccepted);
        Assert.Null(rejected.Descriptor);
        Assert.Equal(AgentInputAttachmentRejectionReason.NotFound, rejected.RejectionReason);
        Assert.Equal(0, batch.AcceptedCount);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_RejectsExpiredPendingUpload()
    {
        var (_, _, time, service) = NewStack();
        var projectId = "proj-expired";
        var upload = await service.UploadAsync(projectId, NewFormFile("old.txt", "text/plain", "data"u8.ToArray()));

        time.SetUtcNow(upload.ExpiresAt!.Value.AddMinutes(5));

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        var rejected = Assert.Single(batch.Results);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(AgentInputAttachmentRejectionReason.Expired, rejected.RejectionReason);
        Assert.Contains("expired", rejected.RejectionMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, batch.AcceptedCount);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_RejectsAlreadyBoundAttachment()
    {
        var (database, _, _, service) = NewStack();
        var projectId = "proj-already";
        var upload = await service.UploadAsync(projectId, NewFormFile("a.txt", "text/plain", "A"u8.ToArray()));

        await service.BindAgentInputAsync(projectId, "session-A", "input-A", [upload.Id]);

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-B", "input-B", [upload.Id]);

        var rejected = Assert.Single(batch.Results);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(AgentInputAttachmentRejectionReason.AlreadyBound, rejected.RejectionReason);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId("session-A", "input-A"), row.OwnerId);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_ReplayForSameOwnerReturnsAcceptedDescriptor()
    {
        var (_, _, _, service) = NewStack();
        var projectId = "proj-same-owner";
        var upload = await service.UploadAsync(projectId, NewFormFile("retry.txt", "text/plain", "retry"u8.ToArray()));

        await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);
        var replay = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        var accepted = Assert.Single(replay.Results);
        Assert.True(accepted.IsAccepted);
        Assert.Equal(upload.Id, accepted.Descriptor!.Id);
        Assert.Equal(1, replay.AcceptedCount);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_RejectsUnsupportedContentType()
    {
        var (database, _, _, service) = NewStack();
        var projectId = "proj-type";
        var upload = await service.UploadAsync(projectId, NewFormFile("bin.weird", "application/x-binary-weird", "\u0001\u0002"u8.ToArray()));

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        var rejected = Assert.Single(batch.Results);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(AgentInputAttachmentRejectionReason.UnsupportedType, rejected.RejectionReason);
        Assert.Equal(0, batch.AcceptedCount);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Null(row.OwnerKind);
        Assert.NotNull(row.ExpiresAt);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_RejectsNotReadableAttachment()
    {
        var (database, storage, _, service) = NewStack();
        var projectId = "proj-unreadable";
        var upload = await service.UploadAsync(projectId, NewFormFile("plain.txt", "text/plain", "raw"u8.ToArray()));

        var storagePath = storage.GenerateStoragePath(projectId, upload.Id);
        storage.MarkUnreadable(storagePath);

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id]);

        var rejected = Assert.Single(batch.Results);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(AgentInputAttachmentRejectionReason.NotReadable, rejected.RejectionReason);
        Assert.Equal(0, batch.AcceptedCount);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Null(row.OwnerKind);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_RejectsOversizedAttachment()
    {
        var (service, _, database) = NewStackWithOptions(new AttachmentStorageOptions
        {
            MaxFileBytes = 4,
        });
        var projectId = "proj-oversize";

        // Simulate an attachment that was uploaded before the limit was
        // tightened (the row's Size field records the bytes actually
        // written, exceeding the current runtime limit). The service
        // must reject it on the size guard during validation.
        var oversizedId = $"att_{Guid.NewGuid():N}";
        await using (var db = database.CreateContext())
        {
            db.Attachments.Add(new AttachmentRow
            {
                Id = oversizedId,
                ProjectId = projectId,
                OwnerKind = null,
                OwnerId = null,
                OwnerIssueNumber = null,
                OriginalFileName = "legacy-big.bin",
                ContentType = "application/octet-stream",
                Size = 1024,
                StoragePath = $"{projectId}/{oversizedId}/content",
                CreatedAt = _frozenNow,
                ExpiresAt = _frozenNow.AddHours(24),
            });
            await db.SaveChangesAsync();
        }

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [oversizedId]);

        var rejected = Assert.Single(batch.Results);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(AgentInputAttachmentRejectionReason.ExceedsSizeLimit, rejected.RejectionReason);
        Assert.Equal(0, batch.AcceptedCount);

        await using var verify = database.CreateContext();
        var row = await verify.Attachments.AsNoTracking().SingleAsync(a => a.Id == oversizedId);
        Assert.Null(row.OwnerKind);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_MixedAcceptAndRejectReportsEachIdInOrder()
    {
        var (service, _, database) = NewStackWithOptions(new AttachmentStorageOptions
        {
            MaxFileBytes = 4,
        });
        var projectId = "proj-mixed";
        var valid = await service.UploadAsync(projectId, NewFormFile("good.txt", "text/plain", "ok"u8.ToArray()));
        const string missing = "att_does_not_exist";
        var oversizedId = $"att_{Guid.NewGuid():N}";
        await using (var db = database.CreateContext())
        {
            db.Attachments.Add(new AttachmentRow
            {
                Id = oversizedId,
                ProjectId = projectId,
                OwnerKind = null,
                OwnerId = null,
                OwnerIssueNumber = null,
                OriginalFileName = "legacy-big.bin",
                ContentType = "application/octet-stream",
                Size = 1024,
                StoragePath = $"{projectId}/{oversizedId}/content",
                CreatedAt = _frozenNow,
                ExpiresAt = _frozenNow.AddHours(24),
            });
            await db.SaveChangesAsync();
        }

        var batch = await service.ValidateAndBindAgentInputAsync(
            projectId,
            "session-1",
            "input-1",
            [valid.Id, missing, oversizedId]);

        Assert.Equal(3, batch.Results.Count);
        Assert.Equal(1, batch.AcceptedCount);

        Assert.True(batch.Results[0].IsAccepted);
        Assert.Equal(valid.Id, batch.Results[0].Id);

        Assert.False(batch.Results[1].IsAccepted);
        Assert.Equal(missing, batch.Results[1].Id);
        Assert.Equal(AgentInputAttachmentRejectionReason.NotFound, batch.Results[1].RejectionReason);

        Assert.False(batch.Results[2].IsAccepted);
        Assert.Equal(oversizedId, batch.Results[2].Id);
        Assert.Equal(AgentInputAttachmentRejectionReason.ExceedsSizeLimit, batch.Results[2].RejectionReason);

        await using var db2 = database.CreateContext();
        var bound = await db2.Attachments.AsNoTracking()
            .Where(a => a.Id == valid.Id)
            .SingleAsync();
        Assert.Equal(AttachmentService.OwnerKindAgentInput, bound.OwnerKind);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId("session-1", "input-1"), bound.OwnerId);

        var unbound = await db2.Attachments.AsNoTracking()
            .Where(a => a.Id == oversizedId)
            .SingleAsync();
        Assert.Null(unbound.OwnerKind);
        Assert.NotNull(unbound.ExpiresAt);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_AllRejected_DoesNotBindAnything()
    {
        var (database, _, _, service) = NewStack();
        var projectId = "proj-allrej";

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1",
            ["att_missing_1", "att_missing_2", "att_missing_3"]);

        Assert.Equal(3, batch.Results.Count);
        Assert.Equal(0, batch.AcceptedCount);
        Assert.All(batch.Results, r => Assert.False(r.IsAccepted));

        await using var db = database.CreateContext();
        Assert.Empty(await db.Attachments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_EmptyOrNullInput_ReturnsEmptyBatch()
    {
        var (_, _, _, service) = NewStack();

        var batchNull = await service.ValidateAndBindAgentInputAsync("proj-empty", "session-1", "input-1", null);
        Assert.Empty(batchNull.Results);
        Assert.Equal(0, batchNull.AcceptedCount);

        var batchEmpty = await service.ValidateAndBindAgentInputAsync("proj-empty", "session-1", "input-1", []);
        Assert.Empty(batchEmpty.Results);
        Assert.Equal(0, batchEmpty.AcceptedCount);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_DuplicateIdsInSubmissionReportedOnceInOrder()
    {
        var (_, _, _, service) = NewStack();
        var projectId = "proj-dup";
        var upload = await service.UploadAsync(projectId, NewFormFile("dup.txt", "text/plain", "D"u8.ToArray()));

        var batch = await service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [upload.Id, upload.Id, upload.Id]);

        var accepted = Assert.Single(batch.Results);
        Assert.Equal(upload.Id, accepted.Id);
        Assert.True(accepted.IsAccepted);
        Assert.Equal(1, batch.AcceptedCount);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_OverAggregatePerOwnerLimit_RejectsAllAndBindsNone()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var options = new AttachmentStorageOptions
        {
            MaxCountPerOwner = 1,
        };
        var service = new AttachmentService(database.Factory, storage, options, time);
        var projectId = "proj-cap";
        var first = await service.UploadAsync(projectId, NewFormFile("first.txt", "text/plain", "1"u8.ToArray()));
        var second = await service.UploadAsync(projectId, NewFormFile("second.txt", "text/plain", "2"u8.ToArray()));

        await Assert.ThrowsAsync<AttachmentLimitException>(() =>
            service.ValidateAndBindAgentInputAsync(projectId, "session-1", "input-1", [first.Id, second.Id]));

        await using var db = database.CreateContext();
        var rows = await db.Attachments.AsNoTracking().Where(a => a.OwnerKind == AttachmentService.OwnerKindAgentInput).ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task ValidateAndBindAgentInput_BoundRecordSurvivesCleanupExpiryAndCarriesNoSecrets()
    {
        var (database, storage, time, service) = NewStack();
        var projectId = "proj-survives";
        var sessionId = "session-surv";
        var inputId = "input-surv";
        var upload = await service.UploadAsync(projectId, NewFormFile("persist.bin", "application/octet-stream", "PERSIST"u8.ToArray()));

        await service.ValidateAndBindAgentInputAsync(projectId, sessionId, inputId, [upload.Id]);

        time.SetUtcNow(upload.ExpiresAt!.Value.AddHours(2));

        var removed = await service.CleanupExpiredPendingAsync();
        Assert.Equal(0, removed);

        await using var db = database.CreateContext();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == upload.Id);
        Assert.Equal(AttachmentService.OwnerKindAgentInput, row.OwnerKind);
        Assert.Equal(AttachmentService.BuildAgentInputOwnerId(sessionId, inputId), row.OwnerId);
        Assert.Null(row.ExpiresAt);
        Assert.Equal("persist.bin", row.OriginalFileName);
        Assert.Equal("application/octet-stream", row.ContentType);

        var props = typeof(AttachmentRow).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("TemporaryDownloadUrl", props);
        Assert.DoesNotContain("CallerAccessToken", props);
        Assert.DoesNotContain("PlatformEventPayload", props);

        var storagePath = storage.GenerateStoragePath(projectId, upload.Id);
        Assert.True(storage.Contains(storagePath));
    }

    private static (TestDatabase Database, FakeAttachmentStorage Storage, FakeTimeProvider Time, AttachmentService Service)
        NewStack()
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var service = new AttachmentService(database.Factory, storage, new AttachmentStorageOptions(), time);
        return (database, storage, time, service);
    }

    private static (AttachmentService Service, FakeTimeProvider Time, TestDatabase Database)
        NewStackWithOptions(AttachmentStorageOptions options)
    {
        var database = NewDatabase();
        var storage = new FakeAttachmentStorage();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var service = new AttachmentService(database.Factory, storage, options, time);
        return (service, time, database);
    }

    private static TestDatabase NewDatabase(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var optionsBuilder = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
            optionsBuilder.AddInterceptors(interceptor);
        var options = optionsBuilder.Options;
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

    private sealed class CancelAfterFirstAgentInputBindCommandInterceptor(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        private readonly CancellationTokenSource _cancellation = cancellation;

        public bool Cancelled { get; private set; }
        public bool Armed { get; private set; }

        public void Arm() => Armed = true;

        public override ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed
                && !Cancelled
                && command.CommandText.Contains("UPDATE \"Attachments\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"OwnerKind\"", StringComparison.Ordinal))
            {
                Cancelled = true;
                _cancellation.Cancel();
            }

            return ValueTask.FromResult(result);
        }
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
