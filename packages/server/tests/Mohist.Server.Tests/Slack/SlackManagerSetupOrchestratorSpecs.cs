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
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
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
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.AwaitingInstall, configuration.Phase);
        Assert.Equal(SlackSetupPrimaryAction.ApproveInstall, configuration.PrimaryAction);
        Assert.NotNull(configuration.ManagerAppId);
        Assert.StartsWith("https://api.slack.com/apps/", configuration.InstallUrl);
        Assert.Equal(1, _appManagement.CreateCalls);

        await AssertConfigurationSecretsPersistedAsync("T_SETUP");
        await AssertSingleEnrollmentAsync("T_SETUP", configuration.EnrollmentId!);

        _botIdentity.Result = VerifiedBot("T_SETUP", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-runtime", "xapp-candidate"));

        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, runtime.Phase);
        Assert.Equal(SlackSetupPrimaryAction.AwaitSocketVerification, runtime.PrimaryAction);
        await AssertCandidateSecretsPersistedAsync(runtime.EnrollmentId!);

        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, "T_SETUP");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-candidate", validation!.AppToken);

        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation.LeaseId, configuration.ManagerAppId!));

        var ready = await _orchestrator.GetProgressAsync();
        Assert.Equal(SlackSetupPhase.Ready, ready!.Phase);
        Assert.Equal(SlackSetupPrimaryAction.Ready, ready.PrimaryAction);
        await AssertEnrollmentReadinessAsync(runtime.EnrollmentId!, SlackManagerReadiness.Ready);
        // The verified hello promoted the candidate pair to the runtime
        // addresses; the candidate slot is no longer needed.
        await AssertRuntimeSecretAsync(runtime.EnrollmentId!, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(runtime.EnrollmentId!, SecretKind.AppToken, "xapp-candidate");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.CandidateAppToken)));
    }

    [Fact]
    public async Task Rerunning_configuration_does_not_create_a_second_enrollment_or_app()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_RERUN"));
        var first = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-a", "xoxr-a")));

        _configurationPort.Enqueue(ConfigurationRotation("T_RERUN"));
        var second = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-b", "xoxr-b")));

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
            new(new("xoxe-current", "xoxr-current")));

        _botIdentity.Result = VerifiedBot("T_OTHER", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-runtime", "xapp-candidate"));

        Assert.Equal(SlackSetupPhase.Failed, runtime.Phase);
        Assert.Equal("runtime_credential_mismatch", runtime.ErrorClass);
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.CandidateAppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.CandidateBotToken)));
        await AssertEnrollmentRuntimeStateAsync(runtime.EnrollmentId!, SlackRuntimeCredentialValidationState.NotProvided);
    }

    [Fact]
    public async Task Socket_hello_for_the_wrong_app_does_not_verify_the_enrollment()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_HELLO"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        _botIdentity.Result = VerifiedBot("T_HELLO", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-runtime", "xapp-candidate"));

        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, "T_HELLO");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);

        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "A_WRONG"));
        // A mismatched hello on a first-provision candidate rejects like the
        // control-plane route: the candidate is deleted and validation fails.
        await AssertEnrollmentRuntimeStateAsync(runtime.EnrollmentId!, SlackRuntimeCredentialValidationState.Failed);
        await AssertEnrollmentReadinessAsync(runtime.EnrollmentId!, SlackManagerReadiness.Unknown);
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.CandidateAppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(runtime.EnrollmentId!, SecretKind.CandidateBotToken)));
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
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.AwaitingInstall, progress.Phase);
        // The interrupted create is recovered and the same rerun creates fresh,
        // so the guide never stalls on an unknown outcome.
        Assert.Equal(SlackSetupPrimaryAction.ApproveInstall, progress.PrimaryAction);
        Assert.Equal(1, _appManagement.CreateCalls);
        await AssertEnrollmentAppLifecycleAsync("T_INTERRUPTED", SlackManagerAppLifecycle.Created);
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
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.AwaitingInstall, progress.Phase);
        // A create that succeeded but recorded no identity is recovered and
        // created fresh on the same enrollment instead of stalling.
        Assert.Equal(SlackSetupPrimaryAction.ApproveInstall, progress.PrimaryAction);
        Assert.Equal(1, _appManagement.CreateCalls);
        await AssertEnrollmentAppLifecycleAsync("T_ORPHAN", SlackManagerAppLifecycle.Created);
    }

    [Fact]
    public async Task Create_succeeded_without_install_url_derives_the_canonical_install_url()
    {
        var enrollment = await SeedEnrollmentAsync("T_NO_URL");
        _appManagement.SetResponse(enrollment.Id, new FakeSlackAppResponse(
            Create: new SlackAppManagementResult(
                SlackAppManagementOutcome.Succeeded,
                AppId: "A_NO_URL",
                InstallUrl: null)));

        _configurationPort.Enqueue(ConfigurationRotation("T_NO_URL"));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.AwaitingInstall, progress.Phase);
        Assert.Equal(SlackSetupPrimaryAction.ApproveInstall, progress.PrimaryAction);
        Assert.Equal("A_NO_URL", progress.ManagerAppId);
        Assert.Equal("https://api.slack.com/apps/A_NO_URL/oauth", progress.InstallUrl);
        Assert.Equal(1, _appManagement.CreateCalls);
        await AssertEnrollmentAppFactsAsync(
            "T_NO_URL",
            SlackManagerAppLifecycle.Created,
            "A_NO_URL",
            "https://api.slack.com/apps/A_NO_URL/oauth");
    }

    [Fact]
    public async Task Resume_reconciles_a_known_unknown_app_then_applies_its_manifest()
    {
        var enrollment = await SeedEnrollmentAsync("T_RESUME_UNKNOWN");
        _appManagement.SetResponse(enrollment.Id, new FakeSlackAppResponse(
            Create: new SlackAppManagementResult(
                SlackAppManagementOutcome.Unknown,
                AppId: "A_RESUMED",
                ErrorClass: "transport_error"),
            Inspect: new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Unknown,
                ErrorClass: "transport_error")));
        _configurationPort.Enqueue(ConfigurationRotation("T_RESUME_UNKNOWN"));

        var unknown = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        Assert.Equal(SlackSetupPhase.CreateUnknown, unknown.Phase);
        Assert.Equal(SlackSetupPrimaryAction.RerunSetup, unknown.PrimaryAction);

        _appManagement.SetResponse(enrollment.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Present,
                "A_RESUMED")));
        var resumed = await _orchestrator.ResumeAsync();

        Assert.Equal(SlackSetupPhase.AwaitingInstall, resumed.Phase);
        Assert.Equal(SlackSetupPrimaryAction.ApproveInstall, resumed.PrimaryAction);
        Assert.Equal("https://api.slack.com/apps/A_RESUMED/oauth", resumed.InstallUrl);
        await AssertEnrollmentAppFactsAsync(
            "T_RESUME_UNKNOWN",
            SlackManagerAppLifecycle.Created,
            "A_RESUMED",
            "https://api.slack.com/apps/A_RESUMED/oauth");
    }

    [Fact]
    public async Task Rotation_persistence_rejection_returns_conflict_instead_of_silently_continuing()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_CONFLICT", expiresAt: T0.AddHours(-1)));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.Failed, progress.Phase);
        Assert.Equal(SlackSetupPrimaryAction.SupplyConfiguration, progress.PrimaryAction);
        Assert.Equal("invalid_rotation_result", progress.ErrorClass);
        Assert.Equal(0, _appManagement.CreateCalls);
    }

    [Fact]
    public async Task Disabled_enrollment_cannot_validate_nor_report_hello()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_DISABLED"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        _botIdentity.Result = VerifiedBot("T_DISABLED", configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-runtime", "xapp-candidate"));
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
            new("xoxb-rotated", "xapp-rotated"));

        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);
        Assert.Equal(SlackSetupPrimaryAction.AwaitSocketVerification, rotated.PrimaryAction);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.AwaitingSocket);
        // The runtime addresses keep serving the old verified pair; the new
        // pair waits at the candidate addresses; the old pair is parked in
        // Previous for recovery.
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.CandidateBotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.CandidateAppToken, "xapp-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousBotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousAppToken, "xapp-candidate");

        var manager = new SlackLeaseTargetRef.Manager(enrollmentId, "T_ROTATE");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-rotated", validation!.AppToken);
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation.LeaseId, managerAppId));

        var ready = await _orchestrator.GetProgressAsync();
        Assert.Equal(SlackSetupPhase.Ready, ready!.Phase);
        Assert.Equal(SlackSetupPrimaryAction.Ready, ready.PrimaryAction);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-rotated");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken)));
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
            new("xoxb-runtime", "xapp-candidate"));

        Assert.Equal(SlackSetupPhase.Ready, again.Phase);
        Assert.Equal(SlackSetupPrimaryAction.Ready, again.PrimaryAction);
        Assert.Equal(verifyCalls + 1, _botIdentity.Requests.Count);
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
            new("xoxb-bad", "xapp-bad"));

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
            new("xoxb-rotated", "xapp-rotated"));
        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);

        _botIdentity.Result = VerifiedBot("T_OTHER", managerAppId);
        var rejected = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-bad", "xapp-bad"));

        Assert.Equal(SlackSetupPhase.Failed, rejected.Phase);
        Assert.Equal("runtime_credential_mismatch", rejected.ErrorClass);
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken)));
    }

    [Fact]
    public async Task In_flight_rotation_with_mismatched_hello_restores_the_previous_verified_pair()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_HELLO_ROTATE");
        _botIdentity.Result = VerifiedBot("T_HELLO_ROTATE", managerAppId);
        var rotated = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-rotated", "xapp-rotated"));
        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);

        var manager = new SlackLeaseTargetRef.Manager(enrollmentId, "T_HELLO_ROTATE");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);

        // A mismatched hello rejects like the control-plane route: the candidate
        // is bad, so drop it, restore the parked previous verified pair and
        // return the enrollment to Verified serving the old pair.
        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "A_WRONG_APP"));
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken)));
    }

    [Fact]
    public async Task Rotation_crash_at_candidate_store_never_serves_the_unverified_pair_to_the_runtime_lease()
    {
        var (enrollmentId, managerAppId) = await DriveToReadyAsync("T_CRASH_STORE");
        var manager = new SlackLeaseTargetRef.Manager(enrollmentId, "T_CRASH_STORE");

        // A faulting secret store simulates a process crash at the boundary the
        // old ordering got wrong: after the state has left Verified but while
        // the new candidate is being written to the candidate address.
        var faulting = new SlackManagerSetupOrchestrator(
            _configurationPort,
            new ProtectedSlackConfigurationCredentialStore(_factory, _secrets),
            _enrollments,
            new SlackManifestGenerator(),
            _appManagement,
            _appManagement,
            _botIdentity,
            new FaultingSecretStore(_secrets),
            _time);
        _botIdentity.Result = VerifiedBot("T_CRASH_STORE", managerAppId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faulting.SupplyRuntimeCredentialsAsync(new("xoxb-rotated", "xapp-rotated")));

        // Stage ran before the faulted Store, so the state has left Verified and
        // the runtime lease is closed; the runtime address still holds the old
        // verified pair, also parked in Previous for restore.
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Candidate);
        Assert.Null(await _leases.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A"));
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.AppToken, "xapp-candidate");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousBotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.PreviousAppToken, "xapp-candidate");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken)));

        // Resume with the working store converges to the new pair after hello.
        _botIdentity.Result = VerifiedBot("T_CRASH_STORE", managerAppId);
        var resumed = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-rotated", "xapp-rotated"));
        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, resumed.Phase);

        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, managerAppId));
        await AssertEnrollmentRuntimeStateAsync(enrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(enrollmentId, SecretKind.BotToken, "xoxb-rotated");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken)));
    }

    [Fact]
    public async Task Configuration_setup_persists_manager_app_client_and_signing_secrets_under_the_enrollment_owner()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_APP_SECRETS"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));

        Assert.NotNull(configuration.ManagerAppId);
        await AssertRuntimeSecretAsync(configuration.EnrollmentId!, SecretKind.ClientSecret, $"xoxc-fake-{configuration.ManagerAppId}");
        await AssertRuntimeSecretAsync(configuration.EnrollmentId!, SecretKind.SigningSecret, $"sig-fake-{configuration.ManagerAppId}");
        Assert.DoesNotContain("xoxc", configuration.InstallUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig-fake", configuration.InstallUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rerunning_configuration_keeps_a_single_manager_app_secret_pair_without_a_second_create()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_SECRET_RERUN"));
        var first = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-a", "xoxr-a")));
        var enrollmentId = first.EnrollmentId!;
        var appId = first.ManagerAppId!;
        var clientBefore = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.ClientSecret));
        var signingBefore = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.SigningSecret));

        _configurationPort.Enqueue(ConfigurationRotation("T_SECRET_RERUN"));
        var second = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-b", "xoxr-b")));

        Assert.Equal(enrollmentId, second.EnrollmentId);
        Assert.Equal(appId, second.ManagerAppId);
        Assert.Equal(1, _appManagement.CreateCalls);
        Assert.Equal(clientBefore, await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.ClientSecret)));
        Assert.Equal(signingBefore, await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.SigningSecret)));
    }

    [Fact]
    public async Task Create_without_install_url_uses_the_canonical_url_and_stores_returned_app_secrets()
    {
        var enrollment = await SeedEnrollmentAsync("T_NO_URL_SECRETS");
        _appManagement.SetResponse(enrollment.Id, new FakeSlackAppResponse(
            Create: new SlackAppManagementResult(
                SlackAppManagementOutcome.Succeeded,
                AppId: "A_NO_URL_SECRETS",
                InstallUrl: null,
                ClientSecret: "xoxc-leak",
                SigningSecret: "sig-leak")));

        _configurationPort.Enqueue(ConfigurationRotation("T_NO_URL_SECRETS"));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal(SlackSetupPhase.AwaitingInstall, progress.Phase);
        Assert.Equal("xoxc-leak", Encoding.UTF8.GetString((await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(progress.EnrollmentId!, SecretKind.ClientSecret)))!));
        Assert.Equal("sig-leak", Encoding.UTF8.GetString((await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(progress.EnrollmentId!, SecretKind.SigningSecret)))!));
    }

    [Fact]
    public async Task Several_enrollments_without_a_selector_fail_closed_and_list_the_real_choices()
    {
        var first = await SeedEnrollmentAsync("T_AMBIG_A");
        var second = await SeedEnrollmentAsync("T_AMBIG_B");

        var progress = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.GetProgressAsync());
        Assert.Equal("workspace_selection_required", progress.Code);
        var choices = Assert.IsAssignableFrom<List<SlackSetupWorkspaceChoice>>(progress.Details);
        Assert.Equal(2, choices.Count);
        Assert.Contains(choices, choice => choice.TeamId == "T_AMBIG_A");
        Assert.Contains(choices, choice => choice.TeamId == "T_AMBIG_B");
        Assert.All(choices, choice => Assert.False(string.IsNullOrWhiteSpace(choice.Name)));

        var resume = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.ResumeAsync());
        Assert.Equal("workspace_selection_required", resume.Code);

        var runtime = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.SupplyRuntimeCredentialsAsync(new("xoxb", "xapp")));
        Assert.Equal("workspace_selection_required", runtime.Code);
        // No target means no external verification and no write.
        Assert.Empty(_botIdentity.Requests);
        Assert.Equal(0, _appManagement.CreateCalls);

        _configurationPort.Enqueue(ConfigurationRotation("T_AMBIG_B"));
        var selected = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")), "T_AMBIG_B");

        Assert.Equal(second.Id, selected.EnrollmentId);
        Assert.Equal("T_AMBIG_B", selected.WorkspaceTeamId);
        await AssertEnrollmentAppLifecycleAsync("T_AMBIG_B", SlackManagerAppLifecycle.Created);
        await AssertEnrollmentAppLifecycleAsync("T_AMBIG_A", SlackManagerAppLifecycle.NotCreated);
        Assert.NotEqual(first.Id, selected.EnrollmentId);
    }

    [Fact]
    public async Task A_selector_naming_an_unknown_workspace_fails_before_any_external_write()
    {
        _configurationPort.Enqueue(ConfigurationRotation("T_UNKNOWN_SELECTOR"));

        var configuration = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.SupplyConfigurationAsync(new(new("xoxe-current", "xoxr-current")), "T_NOT_ENROLLED"));
        Assert.Equal("workspace_not_enrolled", configuration.Code);

        var resume = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.ResumeAsync("T_NOT_ENROLLED"));
        Assert.Equal("workspace_not_enrolled", resume.Code);

        Assert.Empty(_configurationPort.Requests);
        Assert.Equal(0, _appManagement.CreateCalls);
        Assert.Empty(_botIdentity.Requests);
    }

    [Fact]
    public async Task Configuration_pair_without_a_selector_has_enrollment_intent_for_the_rotated_team()
    {
        await SeedEnrollmentAsync("T_INTENT_A");
        await SeedEnrollmentAsync("T_INTENT_B");

        _configurationPort.Enqueue(ConfigurationRotation("T_INTENT_B"));
        var resumed = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal("T_INTENT_B", resumed.WorkspaceTeamId);
        Assert.Equal(SlackSetupPhase.AwaitingInstall, resumed.Phase);
        await AssertEnrollmentAppLifecycleAsync("T_INTENT_B", SlackManagerAppLifecycle.Created);
        // Another configured Workspace is never treated as the repair target.
        await AssertEnrollmentAppLifecycleAsync("T_INTENT_A", SlackManagerAppLifecycle.NotCreated);

        _configurationPort.Enqueue(ConfigurationRotation("T_INTENT_NEW"));
        var created = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));

        Assert.Equal("T_INTENT_NEW", created.WorkspaceTeamId);
        await AssertEnrollmentAppLifecycleAsync("T_INTENT_NEW", SlackManagerAppLifecycle.Created);
    }

    [Fact]
    public async Task Configuration_pair_for_another_workspace_is_rejected_against_the_selected_enrollment()
    {
        var selected = await SeedEnrollmentAsync("T_CFG_SEL");
        _configurationPort.Enqueue(ConfigurationRotation("T_CFG_OTHER"));

        var rejected = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")), "T_CFG_SEL");

        Assert.Equal(SlackSetupPhase.Failed, rejected.Phase);
        Assert.Equal(selected.Id, rejected.EnrollmentId);
        Assert.Equal("configuration_workspace_mismatch", rejected.ErrorClass);
        Assert.Equal(SlackSetupPrimaryAction.SupplyConfiguration, rejected.PrimaryAction);
        Assert.Contains("T_CFG_SEL", rejected.Summary, StringComparison.Ordinal);
        Assert.Equal(0, _appManagement.CreateCalls);
        await AssertEnrollmentAppLifecycleAsync("T_CFG_SEL", SlackManagerAppLifecycle.NotCreated);
        await AssertNoEnrollmentAsync("T_CFG_OTHER");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(selected.Id, SecretKind.ConfigurationAccessToken)));
    }

    [Fact]
    public async Task Runtime_credentials_bind_to_the_selected_enrollment_and_a_mismatch_leaves_every_target_unchanged()
    {
        var selected = await DriveToReadyAsync("T_BIND_A", "T_BIND_A");
        var other = await DriveToReadyAsync("T_BIND_B", "T_BIND_B");

        _botIdentity.Result = VerifiedBot("T_BIND_B", other.ManagerAppId);
        var rejected = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-foreign", "xapp-foreign"), "T_BIND_A");

        Assert.Equal(SlackSetupPhase.Failed, rejected.Phase);
        Assert.Equal("runtime_credential_mismatch", rejected.ErrorClass);
        Assert.Equal(selected.EnrollmentId, rejected.EnrollmentId);
        await AssertEnrollmentRuntimeStateAsync(selected.EnrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertEnrollmentRuntimeStateAsync(other.EnrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(selected.EnrollmentId, SecretKind.BotToken, "xoxb-runtime");
        await AssertRuntimeSecretAsync(other.EnrollmentId, SecretKind.BotToken, "xoxb-runtime");
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(selected.EnrollmentId, SecretKind.CandidateBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(other.EnrollmentId, SecretKind.CandidateBotToken)));

        var ambiguous = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.SupplyRuntimeCredentialsAsync(new("xoxb-foreign", "xapp-foreign")));
        Assert.Equal("workspace_selection_required", ambiguous.Code);
    }

    [Fact]
    public async Task Ready_enrollment_rotates_an_explicit_pair_through_candidate_and_socket_hello_for_the_selected_workspace()
    {
        var selected = await DriveToReadyAsync("T_ROTATE_SEL", "T_ROTATE_SEL");
        var other = await DriveToReadyAsync("T_ROTATE_OTHER", "T_ROTATE_OTHER");

        _botIdentity.Result = VerifiedBot("T_ROTATE_SEL", selected.ManagerAppId);
        var rotated = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-rotated", "xapp-rotated"), "T_ROTATE_SEL");

        Assert.Equal(SlackSetupPhase.AwaitingSocketValidation, rotated.Phase);
        Assert.Equal(SlackSetupPrimaryAction.AwaitSocketVerification, rotated.PrimaryAction);
        Assert.Contains("T_ROTATE_SEL", rotated.Summary, StringComparison.Ordinal);
        await AssertEnrollmentRuntimeStateAsync(selected.EnrollmentId, SlackRuntimeCredentialValidationState.AwaitingSocket);
        await AssertRuntimeSecretAsync(selected.EnrollmentId, SecretKind.CandidateBotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(selected.EnrollmentId, SecretKind.CandidateAppToken, "xapp-rotated");
        await AssertRuntimeSecretAsync(selected.EnrollmentId, SecretKind.BotToken, "xoxb-runtime");
        // The other Workspace keeps serving its own verified pair.
        await AssertEnrollmentRuntimeStateAsync(other.EnrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(other.EnrollmentId, SecretKind.BotToken, "xoxb-runtime");

        var manager = new SlackLeaseTargetRef.Manager(selected.EnrollmentId, "T_ROTATE_SEL");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-rotated", validation!.AppToken);
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation.LeaseId, selected.ManagerAppId));

        var ready = await _orchestrator.GetProgressAsync("T_ROTATE_SEL");
        Assert.Equal(SlackSetupPhase.Ready, ready!.Phase);
        Assert.Equal(SlackSetupPrimaryAction.Ready, ready.PrimaryAction);
        await AssertEnrollmentRuntimeStateAsync(selected.EnrollmentId, SlackRuntimeCredentialValidationState.Verified);
        await AssertRuntimeSecretAsync(selected.EnrollmentId, SecretKind.BotToken, "xoxb-rotated");
        await AssertRuntimeSecretAsync(selected.EnrollmentId, SecretKind.AppToken, "xapp-rotated");
    }

    [Fact]
    public async Task Every_projection_exposes_one_user_facing_primary_action_and_a_human_summary()
    {
        string[] userFacing =
        [
            SlackSetupPrimaryAction.SupplyConfiguration,
            SlackSetupPrimaryAction.ApproveInstall,
            SlackSetupPrimaryAction.SupplyRuntimeCredentials,
            SlackSetupPrimaryAction.AwaitSocketVerification,
            SlackSetupPrimaryAction.RerunSetup,
            SlackSetupPrimaryAction.Ready,
        ];
        var projections = new List<SlackSetupProgress>();
        Assert.Null(await _orchestrator.GetProgressAsync());

        _configurationPort.Enqueue(ConfigurationRotation("T_PROJECTION"));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        projections.Add(configuration);

        _botIdentity.Result = VerifiedBot("T_PROJECTION", configuration.ManagerAppId!);
        projections.Add(await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-runtime", "xapp-candidate")));

        var manager = new SlackLeaseTargetRef.Manager(configuration.EnrollmentId!, "T_PROJECTION");
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, configuration.ManagerAppId!));
        projections.Add((await _orchestrator.GetProgressAsync())!);

        // An unresolved App create that recorded an identity projects the
        // ordinary rerun while the provider cannot answer.
        var unresolved = await SeedEnrollmentAsync("T_PROJECTION_UNKNOWN");
        var beginUnknown = await _enrollments.BeginManagerAppCreateAsync(
            unresolved.Id, unresolved.ManagerAppOperationFence, "manager_create_crashed");
        Assert.True(beginUnknown.Accepted);
        var applyUnknown = await _enrollments.ApplyManagerAppCreateResultAsync(
            unresolved.Id, beginUnknown.Enrollment!.ManagerAppOperationFence,
            SlackManagerAppLifecycle.CreateUnknown, "transport_error");
        Assert.True(applyUnknown.Accepted);
        await _enrollments.RecordManagerAppIdentityAsync(unresolved.Id, "A_PROJECTION_UNKNOWN");
        _appManagement.SetResponse(unresolved.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Unknown,
                ErrorClass: "transport_error")));
        _configurationPort.Enqueue(ConfigurationRotation("T_PROJECTION_UNKNOWN"));
        projections.Add(await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current"))));

        // A rejected pair returns the guide to the credential step.
        _botIdentity.Result = VerifiedBot("T_OTHER", configuration.ManagerAppId!);
        projections.Add(await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-bad", "xapp-bad"), "T_PROJECTION"));

        // A Workspace that never received Configuration credentials projects
        // the protected Configuration step.
        await SeedEnrollmentAsync("T_PROJECTION_FRESH");
        projections.Add((await _orchestrator.GetProgressAsync("T_PROJECTION_FRESH"))!);

        Assert.All(projections, projection =>
        {
            Assert.Contains(projection.PrimaryAction, userFacing);
            Assert.False(string.IsNullOrWhiteSpace(projection.Summary));
        });
        Assert.Contains(projections, projection =>
            projection.PrimaryAction == SlackSetupPrimaryAction.RerunSetup
            && projection.Phase == SlackSetupPhase.CreateUnknown);
        Assert.Contains(projections, projection =>
            projection.PrimaryAction == SlackSetupPrimaryAction.SupplyConfiguration
            && projection.Phase == SlackSetupPhase.ConfigurationRequired);
    }

    private async Task AssertNoEnrollmentAsync(string teamId)
    {
        await using var db = _factory.CreateDbContext();
        Assert.False(await db.SlackWorkspaceEnrollments.AnyAsync(enrollment => enrollment.WorkspaceTeamId == teamId));
    }

    private async Task<(string EnrollmentId, string ManagerAppId)> DriveToReadyAsync(
        string teamId,
        string? workspaceTeamId = null)
    {
        _configurationPort.Enqueue(ConfigurationRotation(teamId));
        var configuration = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        _botIdentity.Result = VerifiedBot(teamId, configuration.ManagerAppId!);
        var runtime = await _orchestrator.SupplyRuntimeCredentialsAsync(
            new("xoxb-runtime", "xapp-candidate"), workspaceTeamId);
        var manager = new SlackLeaseTargetRef.Manager(runtime.EnrollmentId!, teamId);
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", manager, validation!.LeaseId, configuration.ManagerAppId!));
        var ready = await _orchestrator.GetProgressAsync(workspaceTeamId);
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

    private async Task AssertCandidateSecretsPersistedAsync(string enrollmentId)
    {
        Assert.Equal("xoxb-runtime", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken)))!));
        Assert.Equal("xapp-candidate", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken)))!));
        // The runtime addresses stay empty until the hello promotes the pair.
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken)));
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

    private sealed class FaultingSecretStore : ISecretStore
    {
        private readonly ISecretStore _inner;

        public FaultingSecretStore(ISecretStore inner) => _inner = inner;

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            // Simulate a crash at the candidate write: Preserve (previous
            // slot) and every load/delete still succeed, but storing the new
            // candidate Bot token fails after the state has already left
            // Verified.
            if (address.Kind == SecretKind.CandidateBotToken)
                throw new InvalidOperationException("fault-injected secret store failure");
            return _inner.StoreAsync(address, plaintext, ct);
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            _inner.LoadAsync(address, ct);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            _inner.DeleteAsync(address, ct);

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) =>
            _inner.Redact(values);
    }
}
