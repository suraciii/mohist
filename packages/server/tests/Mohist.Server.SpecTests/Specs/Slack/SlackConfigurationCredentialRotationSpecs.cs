using System.Text;
using System.Data.Common;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
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
    public async Task A_rotation_losing_the_generation_race_writes_no_pair_or_expiry()
    {
        var store = new ProtectedSlackConfigurationCredentialStore(_factory, _secrets);
        var firstExpiresAt = _time.GetUtcNow().AddHours(12);
        var first = await store.StoreVerifiedRotationAsync(
            "enrollment-rotation", "T_ROTATION", 0,
            new("xoxe-first", "xoxr-first"), firstExpiresAt, _time.GetUtcNow());
        Assert.True(first.Stored);

        var secondExpiresAt = _time.GetUtcNow().AddHours(24);
        var second = await store.StoreVerifiedRotationAsync(
            "enrollment-rotation", "T_ROTATION", 0,
            new("xoxe-second", "xoxr-second"), secondExpiresAt, _time.GetUtcNow());

        Assert.False(second.Stored);
        Assert.Equal("configuration_credential_generation_conflict", second.ErrorClass);

        var access = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationAccessToken));
        var refresh = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationRefreshToken));
        Assert.Equal("xoxe-first", Encoding.UTF8.GetString(access!));
        Assert.Equal("xoxr-first", Encoding.UTF8.GetString(refresh!));

        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync();
        Assert.Equal(1, enrollment.ConfigurationCredentialGeneration);
        Assert.Equal(firstExpiresAt, enrollment.ConfigurationCredentialExpiresAt);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_rotation_save_keeps_existing_secrets_and_enrollment_metadata(int failurePoint)
    {
        var oldExpiresAt = _time.GetUtcNow().AddHours(1);
        await SeedExistingRotationAsync(oldExpiresAt);
        var faultingFactory = new FaultingRotationDbContextFactory(
            _database.ConnectionString,
            (RotationSaveFailurePoint)failurePoint);
        var faultingSecrets = CreateSecretStore(faultingFactory);
        var faultingService = new SlackConfigurationCredentialRotationService(
            new SlackWorkspaceEnrollmentStore(_factory, _time),
            _port,
            new ProtectedSlackConfigurationCredentialStore(faultingFactory, faultingSecrets),
            _time);
        var nextExpiresAt = _time.GetUtcNow().AddHours(12);
        _port.Enqueue(new(
            SlackConfigurationCredentialRotationOutcome.Succeeded,
            new("xoxe-next", "xoxr-next"),
            "T_ROTATION",
            nextExpiresAt));

        var error = await Record.ExceptionAsync(() => faultingService.RotateAsync(
            "enrollment-rotation",
            new("xoxe-current", "xoxr-current")));
        Assert.NotNull(error);
        // The enrollment CAS now writes first via ExecuteUpdateAsync, so a failure on the
        // enrollment command surfaces the interceptor exception directly; only a failure
        // inside SaveChanges is wrapped by EF as DbUpdateException.
        Assert.IsType(
            failurePoint == (int)RotationSaveFailurePoint.SecondSecret
                ? typeof(DbUpdateException)
                : typeof(InvalidOperationException),
            error);
        Assert.Equal((RotationSaveFailurePoint)failurePoint, faultingFactory.FailurePoint);

        var access = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationAccessToken));
        var refresh = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationRefreshToken));
        Assert.Equal("xoxe-old", Encoding.UTF8.GetString(access!));
        Assert.Equal("xoxr-old", Encoding.UTF8.GetString(refresh!));

        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync();
        Assert.Equal("T_ROTATION", enrollment.WorkspaceTeamId);
        Assert.Equal("enrollment-rotation", enrollment.ConfigurationCredentialRef);
        Assert.Equal(3, enrollment.ConfigurationCredentialGeneration);
        Assert.Equal(oldExpiresAt, enrollment.ConfigurationCredentialExpiresAt);
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

    private async Task SeedExistingRotationAsync(DateTimeOffset expiresAt)
    {
        await _secrets.StoreAtomicallyAsync(
        [
            new(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationAccessToken), Encoding.UTF8.GetBytes("xoxe-old")),
            new(SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-rotation", SecretKind.ConfigurationRefreshToken), Encoding.UTF8.GetBytes("xoxr-old")),
        ]);
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync();
        enrollment.ConfigurationCredentialRef = "enrollment-rotation";
        enrollment.ConfigurationCredentialGeneration = 3;
        enrollment.ConfigurationCredentialExpiresAt = expiresAt;
        await db.SaveChangesAsync();
    }

    private AesGcmSecretStore CreateSecretStore(IDbContextFactory<MohistDbContext> factory) => new(
        factory,
        new InMemorySecretKeyFile(),
        Options.Create(new SecretStoreOptions()),
        new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false),
        _time,
        NullLogger<AesGcmSecretStore>.Instance);

    private enum RotationSaveFailurePoint
    {
        SecondSecret,
        EnrollmentMetadata,
        SaveChanges,
    }

    private sealed class FaultingRotationDbContextFactory(string connectionString, RotationSaveFailurePoint failurePoint)
        : IDbContextFactory<MohistDbContext>
    {
        private int _storedSecretWrites;
        private bool _failed;

        public RotationSaveFailurePoint? FailurePoint { get; private set; }

        public MohistDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new RotationCommandFailureInterceptor(this), new RotationSaveChangesFailureInterceptor(this))
                .Options;
            return new MohistDbContext(options);
        }

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        private void FailOnCommand(DbCommand command)
        {
            if (_failed)
                return;

            if (failurePoint == RotationSaveFailurePoint.SecondSecret
                && command.CommandText.Contains("UPDATE \"StoredSecrets\"", StringComparison.Ordinal)
                && ++_storedSecretWrites == 2)
            {
                ThrowFailure();
            }

            if (failurePoint == RotationSaveFailurePoint.EnrollmentMetadata
                && command.CommandText.Contains("UPDATE \"SlackWorkspaceEnrollments\"", StringComparison.Ordinal))
            {
                ThrowFailure();
            }
        }

        private void FailOnSaveChanges()
        {
            if (!_failed && failurePoint == RotationSaveFailurePoint.SaveChanges)
                ThrowFailure();
        }

        private void ThrowFailure()
        {
            _failed = true;
            FailurePoint = failurePoint;
            throw new InvalidOperationException("rotation_persist_failure");
        }

        private sealed class RotationCommandFailureInterceptor(FaultingRotationDbContextFactory owner) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                owner.FailOnCommand(command);
                return ValueTask.FromResult(result);
            }

            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                owner.FailOnCommand(command);
                return ValueTask.FromResult(result);
            }
        }

        private sealed class RotationSaveChangesFailureInterceptor(FaultingRotationDbContextFactory owner) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                owner.FailOnSaveChanges();
                return ValueTask.FromResult(result);
            }
        }
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
