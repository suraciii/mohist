using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerSetupOrchestratorSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(T0);
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private AesGcmSecretStore _secrets = null!;
    private FakeSlackConfigurationCredentialPort _configurationPort = null!;
    private FakeSlackAppManagementPort _appManagement = null!;
    private FakeSlackBotIdentityVerificationPort _botIdentity = null!;
    private SlackManagerSetupOrchestrator _orchestrator = null!;
    private SlackAdapterLeaseService _leases = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _secrets = CreateSecretStore(_factory);
        _configurationPort = new FakeSlackConfigurationCredentialPort();
        _appManagement = new FakeSlackAppManagementPort();
        _botIdentity = new FakeSlackBotIdentityVerificationPort();
        var enrollmentStore = new SlackWorkspaceEnrollmentStore(_factory, _time);
        _orchestrator = new SlackManagerSetupOrchestrator(
            _configurationPort,
            new ProtectedSlackConfigurationCredentialStore(_factory, _secrets),
            enrollmentStore,
            new SlackManifestGenerator(),
            _appManagement,
            _botIdentity,
            _secrets,
            _time);
        _leases = new SlackAdapterLeaseService(
            new SlackAdapterLeaseStore(_factory),
            new EnrollmentSlackLeaseTargetProvider(enrollmentStore, _factory),
            new SlackLeaseSecretResolver(_secrets),
            _time);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Setup_advances_to_ready_through_configuration_app_runtime_and_socket_hello()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_SETUP"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new("T_SETUP", new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.AwaitingInstall, configuration.Phase);
        Assert.Equal(SlackSetupNextAction.SupplyRuntimeCredentials, configuration.NextAction);
        Assert.NotNull(configuration.ManagerAppId);
        Assert.StartsWith("https://slack.com/oauth/v2/authorize", configuration.InstallUrl);
        Assert.Equal(1, _appManagement.CreateCalls);

        await AssertConfigurationSecretsPersistedAsync("T_SETUP");
        await AssertSingleEnrollmentAsync("T_SETUP", configuration.EnrollmentId!);

        _botIdentity.Result = VerifiedBot("T_SETUP", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_SETUP", "xoxb-runtime", "xapp-candidate"));

        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, runtime.Phase);
        Assert.Equal(SlackSetupNextAction.ReportSocketHello, runtime.NextAction);
        await AssertRuntimeSecretsPersistedAsync(runtime.EnrollmentId!);

        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, "T_SETUP");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-candidate", validation!.AppToken);

        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation.LeaseId, configuration.ManagerAppId!));

        var ready = await _orchestrator.GetProgressAsync("T_SETUP");
        Assert.Equal(SlackSetupPhase.Ready, ready!.Phase);
        Assert.Equal(SlackSetupNextAction.Ready, ready.NextAction);
        await AssertEnrollmentReadinessAsync(runtime.EnrollmentId!, SlackManagerReadiness.Ready);
    }

    [Fact]
    public async Task Rerunning_configuration_does_not_create_a_second_enrollment_or_app()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_RERUN"));
        var first = await _orchestrator.SupplyConfigurationAsync(
            new("T_RERUN", new("xoxe-a", "xoxr-a")));

        _configurationPort.Enqueue(ConfigurationRotation("T_RERUN"));
        var second = await _orchestrator.SupplyConfigurationAsync(
            new("T_RERUN", new("xoxe-b", "xoxr-b")));

        Assert.Equal(first.EnrollmentId, second.EnrollmentId);
        Assert.Equal(first.ManagerAppId, second.ManagerAppId);
        Assert.Equal(first.InstallUrl, second.InstallUrl);
        Assert.Equal(1, _appManagement.CreateCalls);
        await AssertSingleEnrollmentAsync("T_RERUN", first.EnrollmentId!);
    }

    [Fact]
    public async Task Bot_identity_mismatch_deletes_candidate_secrets_and_neither_stages_nor_binds()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_MISMATCH"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new("T_MISMATCH", new("xoxe-current", "xoxr-current")));

        _botIdentity.Result = VerifiedBot("T_OTHER", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_MISMATCH", "xoxb-runtime", "xapp-candidate"));

        Assert.Equal(SlackSetupPhase.Failed, runtime.Phase);
        Assert.Equal("runtime_credential_mismatch", runtime.ErrorClass);
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.AppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.BotToken)));
        await AssertEnrollmentRuntimeStateAsync(runtime.EnrollmentId!, SlackRuntimeCredentialValidationState.NotProvided);
    }

    [Fact]
    public async Task Socket_hello_for_the_wrong_app_does_not_verify_the_enrollment()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_HELLO"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new("T_HELLO", new("xoxe-current", "xoxr-current")));
        _botIdentity.Result = VerifiedBot("T_HELLO", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_HELLO", "xoxb-runtime", "xapp-candidate"));

        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, "T_HELLO");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);

        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "A_WRONG"));
        await AssertEnrollmentRuntimeStateAsync(runtime.EnrollmentId!, SlackRuntimeCredentialValidationState.AwaitingSocket);
        await AssertEnrollmentReadinessAsync(runtime.EnrollmentId!, SlackManagerReadiness.Unknown);
    }

    private static SlackConfigurationCredentialRotationResult ConfigurationRotation(string teamId) => new(
        SlackConfigurationCredentialRotationOutcome.Succeeded,
        new("xoxe-rotated", "xoxr-rotated"),
        teamId,
        T0.AddHours(12));

    private static SlackBotIdentityVerificationResult VerifiedBot(string teamId, string appId) => new(
        Verified: true,
        WorkspaceTeamId: teamId,
        BotUserId: "U_BOT",
        AppId: appId,
        GrantedScopes: new HashSet<string> { "chat:write", "im:history", "users:read" });

    private async Task AssertConfigurationSecretsPersistedAsync(string teamId)
    {
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(e => e.WorkspaceTeamId == teamId);
        Assert.Equal("xoxe-rotated", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollment.Id, SecretKind.ConfigurationAccessToken)))!));
        Assert.Equal("xoxr-rotated", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollment.Id, SecretKind.ConfigurationRefreshToken)))!));
        Assert.DoesNotContain("xoxe-rotated", enrollment.AuditJson, StringComparison.Ordinal);
    }

    private async Task AssertRuntimeSecretsPersistedAsync(string enrollmentId)
    {
        Assert.Equal("xoxb-runtime", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken)))!));
        Assert.Equal("xapp-candidate", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken)))!));
    }

    private async Task AssertSingleEnrollmentAsync(string teamId, string expectedId)
    {
        await using var db = _factory.CreateDbContext();
        var enrollment = Assert.Single(db.SlackWorkspaceEnrollments.Where(e => e.WorkspaceTeamId == teamId));
        Assert.Equal(expectedId, enrollment.Id);
        Assert.NotEmpty(enrollment.ManagerAppManifestHash);
        Assert.NotEmpty(enrollment.ManagerAppInstallUrl);
        Assert.DoesNotContain("xox", enrollment.ManagerAppInstallUrl, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertEnrollmentReadinessAsync(string enrollmentId, string readiness)
    {
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(e => e.Id == enrollmentId);
        Assert.Equal(readiness, enrollment.ManagerReadiness);
    }

    private async Task AssertEnrollmentRuntimeStateAsync(string enrollmentId, string state)
    {
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(e => e.Id == enrollmentId);
        Assert.Equal(state, enrollment.RuntimeCredentialValidationState);
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
