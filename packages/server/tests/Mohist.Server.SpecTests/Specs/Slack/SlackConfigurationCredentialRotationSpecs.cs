using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackConfigurationCredentialRotationSpecs : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private AesGcmSecretStore _secrets = null!;
    private FakeSlackConfigurationCredentialPort _port = null!;
    private SlackConfigurationCredentialRotationService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _secrets = new AesGcmSecretStore(
            _factory,
            new InMemorySecretKeyFile(),
            Options.Create(new SecretStoreOptions()),
            new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false),
            _time,
            NullLogger<AesGcmSecretStore>.Instance);
        _port = new FakeSlackConfigurationCredentialPort();
        _service = new SlackConfigurationCredentialRotationService(
            new SlackWorkspaceEnrollmentStore(_factory, _time),
            _port,
            new ProtectedSlackConfigurationCredentialStore(_factory, _secrets),
            _time);

        var now = _time.GetUtcNow();
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = "enrollment-rotation",
            WorkspaceTeamId = "T_ROTATION",
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Verified_rotation_persists_both_credential_kinds_and_team_binding_together()
    {
        var expiresAt = _time.GetUtcNow().AddHours(12);
        _port.Enqueue(new(
            SlackConfigurationCredentialRotationOutcome.Succeeded,
            new("xoxe-next", "xoxr-next"),
            "T_ROTATION",
            expiresAt));

        var result = await _service.RotateAsync("enrollment-rotation", new("xoxe-current", "xoxr-current"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.Succeeded, result.Outcome);
        Assert.Equal([new SlackConfigurationCredentialPair("xoxe-current", "xoxr-current")], _port.Requests);
        var access = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationAccessToken));
        var refresh = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationRefreshToken));
        Assert.Equal("xoxe-next", Encoding.UTF8.GetString(access!));
        Assert.Equal("xoxr-next", Encoding.UTF8.GetString(refresh!));

        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync();
        Assert.Equal("enrollment-rotation", enrollment.ConfigurationCredentialRef);
        Assert.Equal(1, enrollment.ConfigurationCredentialGeneration);
        Assert.Equal(expiresAt, enrollment.ConfigurationCredentialExpiresAt);
        Assert.Equal(2, await db.StoredSecrets.CountAsync(secret => secret.OwnerKind == SecretOwnerKinds.SlackWorkspaceEnrollment));
    }

    [Fact]
    public async Task Unknown_rotation_outcome_does_not_write_credentials_or_enrollment()
    {
        _port.Enqueue(new(SlackConfigurationCredentialRotationOutcome.Unknown, ErrorClass: "timeout"));

        var result = await _service.RotateAsync("enrollment-rotation", new("xoxe-current", "xoxr-current"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.Unknown, result.Outcome);
        await AssertNoRotationWritesAsync();
    }

    [Fact]
    public async Task Team_mismatch_does_not_write_credentials_or_enrollment()
    {
        _port.Enqueue(new(
            SlackConfigurationCredentialRotationOutcome.Succeeded,
            new("xoxe-next", "xoxr-next"),
            "T_OTHER",
            _time.GetUtcNow().AddHours(12)));

        var result = await _service.RotateAsync("enrollment-rotation", new("xoxe-current", "xoxr-current"));

        Assert.Equal(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal("workspace_mismatch", result.ErrorClass);
        await AssertNoRotationWritesAsync();
    }

    private async Task AssertNoRotationWritesAsync()
    {
        Assert.Null(await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationAccessToken)));
        Assert.Null(await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationRefreshToken)));
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync();
        Assert.Equal(string.Empty, enrollment.ConfigurationCredentialRef);
        Assert.Equal(0, enrollment.ConfigurationCredentialGeneration);
        Assert.Null(enrollment.ConfigurationCredentialExpiresAt);
        Assert.Empty(db.StoredSecrets);
    }

    private sealed class InMemorySecretKeyFile : ISecretKeyFile
    {
        private readonly byte[] _key = Enumerable.Repeat((byte)7, 32).ToArray();

        public bool Exists(string path) => true;

        public Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default) => Task.FromResult(_key);

        public Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default) => Task.FromResult<byte[]?>(_key);

        public Task WriteAsync(string path, byte[] key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
