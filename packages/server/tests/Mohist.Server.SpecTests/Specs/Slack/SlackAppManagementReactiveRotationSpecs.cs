using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// The production app-management adapter drives the Slack apps.manifest API
/// with the enrollment's Configuration access token; when Slack rejects the
/// call because the token expired or was revoked, the adapter rotates the pair
/// through the real rotation service (atomic persistence included) and retries
/// the original call once, transparently. Only when rotation also fails does
/// the call degrade with the unique next action instead of a bare Slack error.
/// </summary>
public sealed class SlackAppManagementReactiveRotationSpecs : IAsyncLifetime
{
    private const string EnrollmentId = "enrollment-reactive";
    private const string TeamId = "T_REACTIVE";
    private const string AgentAppId = "agent-app-1";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private AesGcmSecretStore _secrets = null!;
    private FakeSlackConfigurationCredentialPort _rotationPort = null!;
    private SlackApiTestScript _script = null!;
    private HttpClient _http = null!;
    private SlackAppManagementPortAdapter _adapter = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _secrets = CreateSecretStore(_factory);
        _rotationPort = new FakeSlackConfigurationCredentialPort();
        var rotations = new SlackConfigurationCredentialRotationService(
            new SlackWorkspaceEnrollmentStore(_factory, _time),
            _rotationPort,
            new ProtectedSlackConfigurationCredentialStore(_factory, _secrets),
            _time);
        _script = new SlackApiTestScript();
        _http = new HttpClient(new SlackApiTestHandler(_script))
        {
            BaseAddress = new Uri("https://slack.test/api/"),
        };
        _adapter = new SlackAppManagementPortAdapter(new SlackApiTransport(_http), _secrets, rotations);

        await _secrets.StoreAtomicallyAsync(
        [
            new(SecretStoreAddress.ForSlackWorkspaceEnrollment(EnrollmentId, SecretKind.ConfigurationAccessToken), Encoding.UTF8.GetBytes("xoxe-current")),
            new(SecretStoreAddress.ForSlackWorkspaceEnrollment(EnrollmentId, SecretKind.ConfigurationRefreshToken), Encoding.UTF8.GetBytes("xoxr-current")),
        ]);
        var now = _time.GetUtcNow();
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = EnrollmentId,
            WorkspaceTeamId = TeamId,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            ConfigurationCredentialRef = EnrollmentId,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Expired_access_token_rotates_once_and_retries_the_original_call_transparently()
    {
        _script.Responder = request =>
            string.Equals(request.Headers.Authorization?.ToString(), "Bearer xoxe-rotated", StringComparison.Ordinal)
                ? SlackApiTestScript.JsonResponse("""{"ok":true,"app_id":"A999"}""")
                : SlackApiTestScript.JsonResponse("""{"ok":false,"error":"invalid_auth"}""");
        var expiresAt = _time.GetUtcNow().AddHours(12);
        _rotationPort.Enqueue(new(
            SlackConfigurationCredentialRotationOutcome.Succeeded,
            new("xoxe-rotated", "xoxr-rotated"),
            TeamId,
            expiresAt));

        var result = await _adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: "{}"));

        Assert.Equal(SlackAppManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal("A999", result.AppId);
        Assert.Equal(2, _script.Requests.Count);
        Assert.Equal("Bearer xoxe-current", _script.Requests[0].Authorization);
        Assert.Equal("Bearer xoxe-rotated", _script.Requests[1].Authorization);

        var access = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(EnrollmentId, SecretKind.ConfigurationAccessToken));
        var refresh = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(EnrollmentId, SecretKind.ConfigurationRefreshToken));
        Assert.Equal("xoxe-rotated", Encoding.UTF8.GetString(access!));
        Assert.Equal("xoxr-rotated", Encoding.UTF8.GetString(refresh!));
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync();
        Assert.Equal(1, enrollment.ConfigurationCredentialGeneration);
        Assert.Equal(expiresAt, enrollment.ConfigurationCredentialExpiresAt);
    }

    [Fact]
    public async Task Refresh_token_also_invalid_degrades_with_unique_next_action_without_retrying()
    {
        _script.Responder = _ => SlackApiTestScript.JsonResponse("""{"ok":false,"error":"invalid_auth"}""");
        _rotationPort.Enqueue(new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, ErrorClass: "invalid_refresh_token"));

        var result = await _adapter.CreateAsync(new(EnrollmentId, AgentAppId, TeamId, ManifestJson: "{}"));

        Assert.Equal(SlackAppManagementOutcome.DefiniteFailure, result.Outcome);
        Assert.Equal(SlackAppManagementPortAdapter.ConfigurationCredentialDegradedError, result.ErrorClass);
        Assert.Contains("mo slack setup", result.ErrorMessage);
        var request = Assert.Single(_script.Requests);
        Assert.Equal("Bearer xoxe-current", request.Authorization);

        var access = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(EnrollmentId, SecretKind.ConfigurationAccessToken));
        var refresh = await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(EnrollmentId, SecretKind.ConfigurationRefreshToken));
        Assert.Equal("xoxe-current", Encoding.UTF8.GetString(access!));
        Assert.Equal("xoxr-current", Encoding.UTF8.GetString(refresh!));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, (await db.SlackWorkspaceEnrollments.SingleAsync()).ConfigurationCredentialGeneration);
    }

    [Fact]
    public async Task Unknown_rotation_outcome_is_marked_credential_rotation_unknown_and_never_retried()
    {
        _script.Responder = _ => SlackApiTestScript.JsonResponse("""{"ok":false,"error":"invalid_config_token"}""");
        _rotationPort.Enqueue(new(SlackConfigurationCredentialRotationOutcome.Unknown, ErrorClass: "timeout"));

        var result = await _adapter.ValidateManifestAsync(new(
            new(EnrollmentId, AgentAppId, TeamId),
            new SlackManifest(2, "{}", "hash")));

        Assert.Equal(SlackAppManagementOutcome.Unknown, result.Outcome);
        Assert.Equal(SlackAppManagementPortAdapter.ConfigurationCredentialRotationUnknownError, result.ErrorClass);
        Assert.Contains("mo slack setup", result.ErrorMessage);
        Assert.Single(_script.Requests);
    }

    [Fact]
    public async Task Fact_call_with_failed_rotation_degrades_to_unknown_with_next_action()
    {
        _script.Responder = _ => SlackApiTestScript.JsonResponse("""{"ok":false,"error":"invalid_config_token"}""");
        _rotationPort.Enqueue(new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, ErrorClass: "invalid_refresh_token"));

        var result = await _adapter.InspectAsync(new(EnrollmentId, AgentAppId, TeamId, "A1"));

        Assert.Equal(SlackAppManagementFactOutcome.Unknown, result.Outcome);
        Assert.Equal(SlackAppManagementPortAdapter.ConfigurationCredentialDegradedError, result.ErrorClass);
        Assert.Contains("mo slack setup", result.ErrorMessage);
        Assert.Single(_script.Requests);
    }

    private AesGcmSecretStore CreateSecretStore(TestDbContextFactory factory) => new(
        factory,
        new InMemorySecretKeyFile(),
        Options.Create(new SecretStoreOptions()),
        new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false),
        _time,
        NullLogger<AesGcmSecretStore>.Instance);

    private sealed class InMemorySecretKeyFile : ISecretKeyFile
    {
        private readonly byte[] _key = Enumerable.Repeat((byte)7, 32).ToArray();

        public bool Exists(string path) => true;

        public Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default) => Task.FromResult(_key);

        public Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default) => Task.FromResult<byte[]?>(_key);

        public Task WriteAsync(string path, byte[] key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
