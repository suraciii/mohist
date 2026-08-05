using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
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
    private SlackWorkspaceEnrollmentStore _enrollments = null!;
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
        _enrollments = enrollmentStore;
        var connections = new AgentConnectionStore(
            _factory, new AgentQuerier(_factory), _secrets, [], _time);
        var agentApps = new ManagedSlackAgentAppStore(_factory, _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);
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
            new EnrollmentSlackLeaseTargetProvider(enrollmentStore, agentApps, binding, _factory, _secrets),
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

    [Fact]
    public async Task Rerun_while_app_create_is_interrupted_recovers_to_create_unknown_without_creating_again()
    {
        var enrollment = await SeedEnrollmentAsync("T_INTERRUPTED");
        var begin = await _enrollments.BeginManagerAppCreateAsync(
            enrollment.Id, enrollment.ManagerAppOperationFence, "manager_create_crashed");
        Assert.True(begin.Accepted);

        _configurationPort.Enqueue(ConfigurationRotation("T_INTERRUPTED"));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new("T_INTERRUPTED", new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.CreateUnknown, progress.Phase);
        Assert.Equal(SlackSetupNextAction.ReconcileCreate, progress.NextAction);
        Assert.Equal(0, _appManagement.CreateCalls);
        await AssertEnrollmentAppLifecycleAsync("T_INTERRUPTED", SlackManagerAppLifecycle.CreateUnknown);
    }

    [Fact]
    public async Task Rerun_with_created_but_unrecorded_app_recovers_to_create_unknown_instead_of_stalling()
    {
        var enrollment = await SeedEnrollmentAsync("T_ORPHAN");
        var begin = await _enrollments.BeginManagerAppCreateAsync(
            enrollment.Id, enrollment.ManagerAppOperationFence, "manager_create_crashed");
        Assert.True(begin.Accepted);
        var apply = await _enrollments.ApplyManagerAppCreateResultAsync(
            enrollment.Id, begin.Enrollment!.ManagerAppOperationFence, SlackManagerAppLifecycle.Created, "created");
        Assert.True(apply.Accepted);

        _configurationPort.Enqueue(ConfigurationRotation("T_ORPHAN"));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new("T_ORPHAN", new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.CreateUnknown, progress.Phase);
        Assert.Equal(SlackSetupNextAction.ReconcileCreate, progress.NextAction);
        Assert.Equal(0, _appManagement.CreateCalls);
        await AssertEnrollmentAppLifecycleAsync("T_ORPHAN", SlackManagerAppLifecycle.CreateUnknown);
    }

    [Fact]
    public async Task Create_succeeded_without_install_url_records_create_unknown_and_keeps_the_app_id()
    {
        var enrollment = await SeedEnrollmentAsync("T_NO_URL");
        _appManagement.SetResponse(enrollment.Id, new FakeSlackAppResponse(
            Create: new SlackAppManagementResult(
                SlackAppManagementOutcome.Succeeded,
                AppId: "A_NO_URL",
                InstallUrl: null)));

        _configurationPort.Enqueue(ConfigurationRotation("T_NO_URL"));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new("T_NO_URL", new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.CreateUnknown, progress.Phase);
        Assert.Equal(SlackSetupNextAction.ReconcileCreate, progress.NextAction);
        Assert.Equal("A_NO_URL", progress.ManagerAppId);
        Assert.Null(progress.InstallUrl);
        Assert.Equal(1, _appManagement.CreateCalls);
        await AssertEnrollmentAppFactsAsync("T_NO_URL", SlackManagerAppLifecycle.CreateUnknown, "A_NO_URL", "");
    }

    [Fact]
    public async Task Rotation_persistence_rejection_returns_conflict_instead_of_silently_continuing()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_CONFLICT", expiresAt: T0.AddHours(-1)));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new("T_CONFLICT", new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.Failed, progress.Phase);
        Assert.Equal(SlackSetupNextAction.SupplyConfiguration, progress.NextAction);
        Assert.Equal("invalid_rotation_result", progress.ErrorClass);
        Assert.Equal(0, _appManagement.CreateCalls);
    }

    [Fact]
    public async Task Disabled_enrollment_cannot_validate_nor_report_hello()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_DISABLED"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new("T_DISABLED", new("xoxe-current", "xoxr-current")));
        _botIdentity.Result = VerifiedBot("T_DISABLED", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_DISABLED", "xoxb-runtime", "xapp-candidate"));
        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, runtime.Phase);

        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, "T_DISABLED");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);

        await _enrollments.TransitionLifecycleAsync(runtime.EnrollmentId!, SlackEnrollmentLifecycle.Disabled);

        Assert.Null(await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A"));
        Assert.Equal(SlackHelloOutcome.NoLease,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, configuration.ManagerAppId!));
        await AssertEnrollmentRuntimeStateAsync(runtime.EnrollmentId!, SlackRuntimeCredentialValidationState.AwaitingSocket);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _enrollments.CompleteSocketVerificationAsync(runtime.EnrollmentId!));
    }

    [Fact]
    public async Task Ready_enrollment_resupplying_new_credentials_rotates_and_verifies_the_new_pair()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_ROTATE");

        _botIdentity.Result = VerifiedBot("T_ROTATE", managerAppId);
        var rotated = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_ROTATE", "xoxb-rotated", "xapp-rotated"));

        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);
        Assert.Equal(SlackSetupNextAction.ReportSocketHello, rotated.NextAction);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.AwaitingSocket);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousBotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousAppToken, "xapp-candidate");

        var manager = new SlackLeaseTargetRef.Manager(enrollmentId, "T_ROTATE");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-rotated", validation!.AppToken);
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation.LeaseId, managerAppId));

        var ready = await _orchestrator.GetProgressAsync("T_ROTATE");
        Assert.Equal(SlackSetupPhase.Ready, ready!.Phase);
        Assert.Equal(SlackSetupNextAction.Ready, ready.NextAction);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-rotated");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken)));
    }

    [Fact]
    public async Task Ready_enrollment_resupplying_the_same_credentials_is_an_idempotent_noop()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_NOOP");
        var verifyCalls = _botIdentity.Requests.Count;

        _botIdentity.Result = VerifiedBot("T_NOOP", managerAppId);
        var again = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_NOOP", "xoxb-runtime", "xapp-candidate"));

        Assert.Equal(SlackSetupPhase.Ready, again.Phase);
        Assert.Equal(SlackSetupNextAction.Ready, again.NextAction);
        Assert.Equal(verifyCalls, _botIdentity.Requests.Count);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
    }

    [Fact]
    public async Task Ready_enrollment_rotation_with_mismatched_identity_keeps_the_previous_verified_state()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_BAD_ROTATE");

        _botIdentity.Result = VerifiedBot("T_OTHER", managerAppId);
        var rejected = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_BAD_ROTATE", "xoxb-bad", "xapp-bad"));

        Assert.Equal(SlackSetupPhase.Failed, rejected.Phase);
        Assert.Equal("runtime_credential_mismatch", rejected.ErrorClass);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken)));
    }

    [Fact]
    public async Task In_flight_rotation_with_mismatched_resupply_restores_the_previous_verified_pair()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_RESTORE");
        _botIdentity.Result = VerifiedBot("T_RESTORE", managerAppId);
        var rotated = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_RESTORE", "xoxb-rotated", "xapp-rotated"));
        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);

        _botIdentity.Result = VerifiedBot("T_OTHER", managerAppId);
        var rejected = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_RESTORE", "xoxb-bad", "xapp-bad"));

        Assert.Equal(SlackSetupPhase.Failed, rejected.Phase);
        Assert.Equal("runtime_credential_mismatch", rejected.ErrorClass);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken)));
    }

    [Fact]
    public async Task In_flight_rotation_survives_a_mismatched_hello_with_the_previous_pair_intact()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_HELLO_ROTATE");
        _botIdentity.Result = VerifiedBot("T_HELLO_ROTATE", managerAppId);
        var rotated = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("T_HELLO_ROTATE", "xoxb-rotated", "xapp-rotated"));
        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);

        var manager = new SlackLeaseTargetRef.Manager(enrollmentId, "T_HELLO_ROTATE");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);

        // A mismatched hello intentionally does not roll back a rotation:
        // the parked previous pair stays put so the enrollment remains
        // recoverable until a correct hello or a fresh credential re-supply.
        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "A_WRONG_APP"));
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.AwaitingSocket);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousBotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousAppToken, "xapp-candidate");
    }

    private async Task<(string EnrollmentId, string ManagerAppId)> DriveToReadyAsync(string teamId)
    {
        _configurationPort.Enqueue(ConfigurationRotation(teamId));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new(teamId, new("xoxe-current", "xoxr-current")));
        _botIdentity.Result = VerifiedBot(teamId, configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new(teamId, "xoxb-runtime", "xapp-candidate"));
        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, teamId);
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, configuration.ManagerAppId!));
        var ready = await _orchestrator.GetProgressAsync(teamId);
        Assert.Equal(SlackSetupPhase.Ready, ready!.Phase);
        return (runtime.EnrollmentId!, configuration.ManagerAppId!);
    }

    private async Task AssertRuntimeSecretAsync(string enrollmentId, SecretKind kind, string expected)
    {
        var stored = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, kind));
        Assert.Equal(expected, stored is null ? null : Encoding.UTF8.GetString(stored));
    }

    private static SlackConfigurationCredentialRotationResult ConfigurationRotation(string teamId, DateTimeOffset? expiresAt = null) => new(
        SlackConfigurationCredentialRotationOutcome.Succeeded,
        new("xoxe-rotated", "xoxr-rotated"),
        teamId,
        expiresAt ?? T0.AddHours(12));

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

    private async Task AssertEnrollmentAppLifecycleAsync(string teamId, string lifecycle)
    {
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(e => e.WorkspaceTeamId == teamId);
        Assert.Equal(lifecycle, enrollment.ManagerAppLifecycle);
    }

    private async Task AssertEnrollmentAppFactsAsync(string teamId, string lifecycle, string appId, string installUrl)
    {
        await using var db = _factory.CreateDbContext();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(e => e.WorkspaceTeamId == teamId);
        Assert.Equal(lifecycle, enrollment.ManagerAppLifecycle);
        Assert.Equal(appId, enrollment.ManagerAppId);
        Assert.Equal(installUrl, enrollment.ManagerAppInstallUrl);
    }

    private async Task<SlackWorkspaceEnrollment> SeedEnrollmentAsync(string teamId)
    {
        var enrollment = new SlackWorkspaceEnrollment
        {
            Id = $"enrollment_{Guid.NewGuid():N}",
            WorkspaceTeamId = teamId,
            ManagerActorId = $"manager_actor_{Guid.NewGuid():N}",
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "unknown",
            ManagedAppLimit = 0,
        };
        return await _enrollments.CreateAsync(enrollment);
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
