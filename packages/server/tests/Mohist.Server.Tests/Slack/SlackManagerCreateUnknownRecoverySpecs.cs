using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// Recovery of an unknown Mohist App create. Reconciliation asks the provider
/// about the recorded operation by its own identity; when the create recorded
/// none, the explicit arbitration is the only action that can change the state.
/// </summary>
[Trait("level", "L0")]
public sealed class SlackManagerCreateUnknownRecoverySpecs : IAsyncLifetime
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
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Adjudicating_an_identity_less_unknown_create_creates_the_app_once_on_the_same_enrollment()
    {
        var enrollment = await SeedEnrollmentAsync("T_ADJUDICATE");
        var begin = await _enrollments.BeginManagerAppCreateAsync(
            enrollment.Id, enrollment.ManagerAppOperationFence, "manager_create_crashed");
        Assert.True(begin.Accepted);
        _configurationPort.Enqueue(ConfigurationRotation("T_ADJUDICATE"));
        var unknown = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        Assert.Equal(SlackSetupPrimaryAction.AdjudicateCreate, unknown.PrimaryAction);

        var adjudicated = await _orchestrator.AdjudicateCreateUnknownAsync();

        Assert.Equal(SlackSetupPhase.AwaitingInstall, adjudicated.Phase);
        Assert.Equal(SlackSetupPrimaryAction.ApproveInstall, adjudicated.PrimaryAction);
        Assert.Equal(1, _appManagement.CreateCalls);
        await AssertEnrollmentAppFactsAsync(
            "T_ADJUDICATE",
            SlackManagerAppLifecycle.Created,
            adjudicated.ManagerAppId!,
            $"https://api.slack.com/apps/{adjudicated.ManagerAppId}/oauth");
    }

    [Fact]
    public async Task Adjudicating_a_create_that_recorded_an_app_identity_is_refused()
    {
        var enrollment = await SeedEnrollmentAsync("T_ADJUDICATE_KNOWN");
        var begin = await _enrollments.BeginManagerAppCreateAsync(
            enrollment.Id, enrollment.ManagerAppOperationFence, "manager_create_lost_response");
        Assert.True(begin.Accepted);
        var apply = await _enrollments.ApplyManagerAppCreateResultAsync(
            enrollment.Id,
            begin.Enrollment!.ManagerAppOperationFence,
            SlackManagerAppLifecycle.CreateUnknown,
            "transport_error");
        Assert.True(apply.Accepted);
        await _enrollments.RecordManagerAppIdentityAsync(enrollment.Id, "A_KNOWN_UNKNOWN");
        // The provider cannot answer yet, so the unknown create keeps its
        // recorded identity and the rerun stays the executable action.
        _appManagement.SetResponse(enrollment.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Unknown,
                ErrorClass: "transport_error")));
        _configurationPort.Enqueue(ConfigurationRotation("T_ADJUDICATE_KNOWN"));
        var progress = await _orchestrator.SupplyConfigurationAsync(
            new(new("xoxe-current", "xoxr-current")));
        Assert.Equal(SlackSetupPhase.CreateUnknown, progress.Phase);
        // A recorded identity makes the operation reconcilable, so the rerun
        // stays the executable action and arbitration is refused.
        Assert.Equal(SlackSetupPrimaryAction.RerunSetup, progress.PrimaryAction);

        var conflict = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.AdjudicateCreateUnknownAsync("T_ADJUDICATE_KNOWN"));

        Assert.Equal("create_reconciliation_required", conflict.Code);
        await AssertEnrollmentAppFactsAsync(
            "T_ADJUDICATE_KNOWN",
            SlackManagerAppLifecycle.CreateUnknown,
            "A_KNOWN_UNKNOWN",
            installUrl: string.Empty);
        Assert.Equal(0, _appManagement.CreateCalls);
    }

    [Fact]
    public async Task Adjudicating_a_setup_that_is_not_awaiting_arbitration_is_refused()
    {
        await SeedEnrollmentAsync("T_ADJUDICATE_FRESH");

        var conflict = await Assert.ThrowsAsync<SlackManagerConflictException>(
            () => _orchestrator.AdjudicateCreateUnknownAsync("T_ADJUDICATE_FRESH"));

        Assert.Equal("create_adjudication_not_required", conflict.Code);
        Assert.Equal(0, _appManagement.CreateCalls);
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

    private AesGcmSecretStore CreateSecretStore(TestDbContextFactory factory) => new(
        factory,
        new InMemorySecretKeyFile(),
        Options.Create(new SecretStoreOptions()),
        new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false),
        _time,
        NullLogger<AesGcmSecretStore>.Instance);

    private static SlackConfigurationCredentialRotationResult ConfigurationRotation(string teamId, DateTimeOffset? expiresAt = null) => new(
        SlackConfigurationCredentialRotationOutcome.Succeeded,
        new("xoxe-rotated", "xoxr-rotated"),
        teamId,
        expiresAt ?? T0.AddHours(12));

    private sealed class InMemorySecretKeyFile : ISecretKeyFile
    {
        private readonly byte[] _key = Enumerable.Repeat((byte)7, 32).ToArray();

        public bool Exists(string path) => true;

        public Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default) => Task.FromResult(_key);

        public Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default) => Task.FromResult<byte[]?>(_key);

        public Task WriteAsync(string path, byte[] key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
