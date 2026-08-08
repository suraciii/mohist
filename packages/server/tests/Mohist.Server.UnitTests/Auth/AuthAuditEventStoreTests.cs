using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class AuthAuditEventStoreTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Record_ThenList_ReturnsAllSixEventTypes_WithSubjectTargetAndTime()
    {
        using var setup = CreateStore();

        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_1", CredentialKind.Pat, "ci", Epoch.AddMinutes(1)));
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialRevoked("admin", "pat_1", CredentialKind.Pat, "ci", Epoch.AddMinutes(2)));
        await setup.Store.RecordAsync(AuthAuditEvent.EnrollmentTokenIssued("admin", "hash-a", Epoch.AddMinutes(3)));
        await setup.Store.RecordAsync(AuthAuditEvent.EnrollmentTokenConsumed("admin", "hash-a", "runner-1", Epoch.AddMinutes(4)));
        await setup.Store.RecordAsync(AuthAuditEvent.DeviceApproved("admin", "device-1", Epoch.AddMinutes(5)));
        await setup.Store.RecordAsync(AuthAuditEvent.SessionEstablished("admin", "session-1", Epoch.AddMinutes(6)));

        var events = await setup.Store.ListAsync();

        Assert.Equal(6, events.Count);
        Assert.Equal(
            [
                AuthAuditEventType.SessionEstablished,
                AuthAuditEventType.DeviceApproved,
                AuthAuditEventType.EnrollmentTokenConsumed,
                AuthAuditEventType.EnrollmentTokenIssued,
                AuthAuditEventType.CredentialRevoked,
                AuthAuditEventType.CredentialIssued,
            ],
            events.Select(auditEvent => auditEvent.EventType));
        Assert.All(events, auditEvent => Assert.Equal("admin", auditEvent.SubjectId));

        var issued = events.Single(auditEvent => auditEvent.EventType == AuthAuditEventType.CredentialIssued);
        Assert.Equal("pat_1", issued.TargetId);
        Assert.Equal(AuthAuditEvent.CredentialTargetKind, issued.TargetKind);
        Assert.Equal(Epoch.AddMinutes(1), issued.OccurredAt);
        Assert.Equal("pat", issued.Metadata["kind"]);
        Assert.Equal("ci", issued.Metadata["name"]);

        var consumed = events.Single(auditEvent => auditEvent.EventType == AuthAuditEventType.EnrollmentTokenConsumed);
        Assert.Equal("hash-a", consumed.TargetId);
        Assert.Equal("runner-1", consumed.Metadata["runnerId"]);
    }

    [Fact]
    public async Task List_FiltersByEventType()
    {
        using var setup = CreateStore();
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_1", CredentialKind.Pat, "ci", Epoch.AddMinutes(1)));
        await setup.Store.RecordAsync(AuthAuditEvent.SessionEstablished("admin", "session-1", Epoch.AddMinutes(2)));

        var events = await setup.Store.ListAsync(AuthAuditEventType.CredentialIssued);

        var auditEvent = Assert.Single(events);
        Assert.Equal(AuthAuditEventType.CredentialIssued, auditEvent.EventType);
    }

    [Fact]
    public async Task List_FiltersBySince()
    {
        using var setup = CreateStore();
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_1", CredentialKind.Pat, "ci", Epoch.AddMinutes(1)));
        await setup.Store.RecordAsync(AuthAuditEvent.SessionEstablished("admin", "session-1", Epoch.AddMinutes(3)));

        var events = await setup.Store.ListAsync(since: Epoch.AddMinutes(2));

        var auditEvent = Assert.Single(events);
        Assert.Equal(AuthAuditEventType.SessionEstablished, auditEvent.EventType);
    }

    [Fact]
    public async Task List_RespectsLimit()
    {
        using var setup = CreateStore();
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_1", CredentialKind.Pat, "ci", Epoch.AddMinutes(1)));
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_2", CredentialKind.Pat, "ci", Epoch.AddMinutes(2)));
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_3", CredentialKind.Pat, "ci", Epoch.AddMinutes(3)));

        var events = await setup.Store.ListAsync(limit: 2);

        Assert.Equal(2, events.Count);
        Assert.Equal("pat_3", events[0].TargetId);
        Assert.Equal("pat_2", events[1].TargetId);
    }

    [Fact]
    public async Task List_SkipsRowsWithUnknownStoredEventTypes()
    {
        using var setup = CreateStore();
        await setup.Store.RecordAsync(AuthAuditEvent.CredentialIssued("admin", "pat_1", CredentialKind.Pat, "ci", Epoch.AddMinutes(1)));
        await InsertRawAsync(setup.Connection, "audit_future", "FutureEvent", Epoch.AddMinutes(2));

        var events = await setup.Store.ListAsync();

        var auditEvent = Assert.Single(events);
        Assert.Equal(AuthAuditEventType.CredentialIssued, auditEvent.EventType);
    }

    private static async Task InsertRawAsync(
        SqliteConnection connection,
        string id,
        string eventType,
        DateTimeOffset occurredAt)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "AuthAuditEvents" ("Id", "SubjectId", "EventType", "TargetKind", "TargetId", "OccurredAt", "MetadataJson")
            VALUES ($id, $subject, $eventType, $targetKind, $targetId, $occurredAt, $metadata);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$subject", "admin");
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$targetKind", "credential");
        command.Parameters.AddWithValue("$targetId", "pat_9");
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        command.Parameters.AddWithValue("$metadata", "{}");
        await command.ExecuteNonQueryAsync();
    }

    private static StoreSetup CreateStore()
    {
        var connection = new SqliteConnection("Data Source=auth-audit-event-store-tests;Mode=Memory;Cache=Shared");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "AuthAuditEvents" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AuthAuditEvents" PRIMARY KEY,
                    "SubjectId" TEXT NOT NULL,
                    "EventType" TEXT NOT NULL,
                    "TargetKind" TEXT NOT NULL,
                    "TargetId" TEXT NOT NULL,
                    "OccurredAt" TEXT NOT NULL,
                    "MetadataJson" TEXT NOT NULL
                );
                CREATE INDEX "IX_AuthAuditEvents_EventType_OccurredAt" ON "AuthAuditEvents" ("EventType", "OccurredAt");
                """;
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var store = new AuthAuditEventStore(
            provider.GetRequiredService<IDbContextFactory<MohistDbContext>>());
        return new StoreSetup(store, connection, provider);
    }

    private sealed record StoreSetup(
        AuthAuditEventStore Store,
        SqliteConnection Connection,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Connection.Dispose();
        }
    }
}
