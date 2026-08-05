using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Exercises the production <see cref="EnrollmentSlackLeaseTargetProvider"/>
/// for Agent App (Connection) targets through <see cref="SlackAdapterLeaseService"/>,
/// with migrated SQLite, an in-memory secret store and a fixed clock. No real
/// network, process or wall-clock. Manager targets are covered by the setup
/// orchestrator specs; here the Agent App lease lifecycle is the subject.
/// </summary>
public sealed class SlackConnectionLeaseTargetSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private const string ProjectId = "project-conn";

    private readonly FakeTimeProvider _time = new(Now);
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private InMemorySecretStore _secrets = null!;
    private ManagedSlackAgentAppStore _agentApps = null!;
    private AgentConnectionStore _connections = null!;
    private SlackAgentAppBindingService _binding = null!;
    private EnrollmentSlackLeaseTargetProvider _provider = null!;
    private SlackAdapterLeaseService _leases = null!;

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _secrets = new InMemorySecretStore();
        _connections = new AgentConnectionStore(_factory, new AgentQuerier(_factory), _secrets, [], _time);
        _agentApps = new ManagedSlackAgentAppStore(_factory, _time);
        _binding = new SlackAgentAppBindingService(_factory, _connections, _time);
        _provider = new EnrollmentSlackLeaseTargetProvider(
            new SlackWorkspaceEnrollmentStore(_factory, _time), _agentApps, _binding, _factory, _secrets);
        _leases = new SlackAdapterLeaseService(
            new SlackAdapterLeaseStore(_factory), _provider, new SlackLeaseSecretResolver(_secrets), _time);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Discovery_lists_candidate_connection_targets_and_leaks_no_secrets()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);

        var targets = await _provider.GetTargetsAsync("operator-1");

        var connection = Assert.Single(targets, t => t.Ref is SlackLeaseTargetRef.Connection);
        Assert.Equal(seeded.AppId, connection.ExpectedAppId);
        Assert.True(connection.AppLevelTokenProvisioned);
        Assert.True(connection.BotTokenProvisioned);
        Assert.False(connection.CredentialVerified);
        Assert.False(connection.Active);
    }

    [Fact]
    public async Task Validation_lease_returns_only_the_candidate_app_token_and_refuses_after_verification()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;

        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(seeded.AppId, validation!.ExpectedAppId);
        Assert.Equal("xapp-candidate", validation.AppToken);

        await _leases.ReportHelloAsync("operator-1", targetRef, validation.LeaseId, seeded.AppId);

        Assert.Null(await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A"));
    }

    [Fact]
    public async Task Hello_with_matching_app_id_verifies_and_binds_the_connection()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");

        var outcome = await _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, seeded.AppId);

        Assert.Equal(SlackHelloOutcome.Verified, outcome);
        await AssertAgentAppStateAsync(seeded.AgentAppId, SlackRuntimeCredentialValidationState.Verified, SlackAgentAppBindingState.Bound);
        await AssertConnectionIdentityAsync(seeded.ConnectionId, seeded.AppId);
        // The verified hello promoted the candidate pair to the runtime
        // addresses; the candidate slot is no longer needed.
        Assert.Equal("xapp-candidate", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.AppToken)))!));
        Assert.Equal("xoxb-candidate", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.BotToken)))!));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateAppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateBotToken)));
    }

    [Fact]
    public async Task A_matching_hello_clears_a_rotation_parked_previous_pair()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;

        // A ready rotation in flight parks the previous verified pair while the
        // new candidate awaits its Socket hello; the confirmed hello must drop it.
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousBotToken),
            Encoding.UTF8.GetBytes("xoxb-previous"));
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousAppToken),
            Encoding.UTF8.GetBytes("xapp-previous"));

        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        var outcome = await _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, seeded.AppId);

        Assert.Equal(SlackHelloOutcome.Verified, outcome);
        await AssertAgentAppStateAsync(seeded.AgentAppId, SlackRuntimeCredentialValidationState.Verified, SlackAgentAppBindingState.Bound);
        // The confirmed hello promoted the candidate to the runtime pair; both
        // the candidate slot and the parked previous pair are dropped.
        Assert.Equal("xoxb-candidate", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.BotToken)))!));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateAppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousAppToken)));
    }

    [Fact]
    public async Task Hello_with_mismatched_app_id_rejects_deletes_candidates_and_fails_without_binding()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");

        var outcome = await _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, "A_WRONG");

        Assert.Equal(SlackHelloOutcome.AppIdMismatch, outcome);
        await AssertAgentAppStateAsync(seeded.AgentAppId, SlackRuntimeCredentialValidationState.Failed, SlackAgentAppBindingState.Pending);
        await AssertConnectionIdentityAsync(seeded.ConnectionId, string.Empty);
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateAppToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateBotToken)));
    }

    [Fact]
    public async Task Hello_mismatch_during_a_rotation_restores_the_previous_verified_pair()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: true);
        var targetRef = seeded.TargetRef;
        // A rotation in flight parks the previous verified pair; the candidate
        // (xoxb-candidate) waits at the candidate addresses while the runtime
        // addresses still serve the old verified pair.
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousBotToken),
            Encoding.UTF8.GetBytes("xoxb-previous"));
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousAppToken),
            Encoding.UTF8.GetBytes("xapp-previous"));
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.BotToken),
            Encoding.UTF8.GetBytes("xoxb-previous"));
        await _secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.AppToken),
            Encoding.UTF8.GetBytes("xapp-previous"));

        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        var outcome = await _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, "A_WRONG");

        Assert.Equal(SlackHelloOutcome.AppIdMismatch, outcome);
        await AssertAgentAppStateAsync(seeded.AgentAppId, SlackRuntimeCredentialValidationState.Verified, SlackAgentAppBindingState.Bound);
        Assert.Equal("xoxb-previous", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.BotToken)))!));
        Assert.Equal("xapp-previous", Encoding.UTF8.GetString(
            (await _secrets.LoadAsync(SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.AppToken)))!));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.PreviousAppToken)));
        // The failed candidate is dropped with the rollback.
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateBotToken)));
        Assert.Null(await _secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(seeded.AgentAppId, SecretKind.CandidateAppToken)));
    }

    [Fact]
    public async Task Runtime_lease_requires_a_verified_and_bound_connection()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;

        Assert.Null(await _leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A"));

        var verifiedUnbound = await SeedAsync(SlackRuntimeCredentialValidationState.Verified, bound: false);
        Assert.Null(await _leases.AcquireRuntimeLeaseAsync("operator-1", verifiedUnbound.TargetRef, "adapter-A"));

        var ready = await SeedAsync(SlackRuntimeCredentialValidationState.Verified, bound: true);
        var runtime = await _leases.AcquireRuntimeLeaseAsync("operator-1", ready.TargetRef, "adapter-A");
        Assert.NotNull(runtime);
        Assert.Equal("xapp-live", runtime!.AppToken);
        Assert.Equal("xoxb-live", runtime.BotToken);
    }

    [Fact]
    public async Task Unknown_disabled_and_unprovisioned_targets_are_not_leasable()
    {
        var candidate = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);

        var unknownProject = await _provider.GetTargetAsync("operator-1",
            new SlackLeaseTargetRef.Connection("project-missing", candidate.ConnectionId));
        Assert.Null(unknownProject);

        var unknownConnection = await _provider.GetTargetAsync("operator-1",
            new SlackLeaseTargetRef.Connection(ProjectId, "connection-missing"));
        Assert.Null(unknownConnection);

        await DisableConnectionAsync(candidate.ConnectionId);
        Assert.Null(await _provider.GetTargetAsync("operator-1", candidate.TargetRef));

        var unprovisioned = await SeedAsync(SlackRuntimeCredentialValidationState.NotProvided, bound: false);
        Assert.Null(await _leases.AcquireValidationLeaseAsync("operator-1", unprovisioned.TargetRef, "adapter-A"));
        Assert.Null(await _leases.AcquireRuntimeLeaseAsync("operator-1", unprovisioned.TargetRef, "adapter-A"));
    }

    [Fact]
    public async Task A_confirmed_validation_lease_is_fenced_against_replay()
    {
        var seeded = await SeedAsync(SlackRuntimeCredentialValidationState.Candidate, bound: false);
        var targetRef = seeded.TargetRef;
        var validation = await _leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");

        Assert.Equal(SlackHelloOutcome.Verified,
            await _leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, seeded.AppId));
        Assert.Equal(SlackHelloOutcome.NoLease,
            await _leases.ReportHelloAsync("operator-1", targetRef, validation.LeaseId, seeded.AppId));

        var runtime = await _leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(runtime);
        Assert.Null(await _leases.RenewLeaseAsync("operator-1", targetRef, validation.LeaseId, "adapter-A"));
    }

    private async Task<SeededTarget> SeedAsync(string credentialState, bool bound)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var enrollmentId = $"enrollment-{suffix}";
        var connectionId = $"connection-{suffix}";
        var agentAppId = $"agent_app-{suffix}";
        var appId = $"A_{suffix}";
        var teamId = $"T_{suffix}";
        var botUserId = "U_BOT";
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = teamId,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            AuditJson = "[]",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = connectionId,
            ProjectId = ProjectId,
            AgentId = $"agent-{suffix}",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = teamId,
            AppId = bound ? appId : string.Empty,
            BotUserId = bound ? botUserId : string.Empty,
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Unknown,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        var hasRuntimeCredentials = credentialState
            is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket
            or SlackRuntimeCredentialValidationState.Verified;
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = teamId,
            AgentConnectionId = connectionId,
            AppId = appId,
            BotUserId = hasRuntimeCredentials ? botUserId : string.Empty,
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = hasRuntimeCredentials ? SlackAuthorizationState.Authorized : SlackAuthorizationState.NotStarted,
            RuntimeCredentialValidationState = credentialState,
            DesiredManifestVersion = 2,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            OperationFence = 0,
            AppLevelTokenRef = hasRuntimeCredentials ? agentAppId : string.Empty,
            BotTokenRef = hasRuntimeCredentials ? agentAppId : string.Empty,
            BindingState = bound ? SlackAgentAppBindingState.Bound : SlackAgentAppBindingState.Pending,
            AuditJson = "[]",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync();

        if (hasRuntimeCredentials)
        {
            if (credentialState == SlackRuntimeCredentialValidationState.Verified)
            {
                await _secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-live"));
                await _secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-live"));
            }
            else
            {
                // A candidate awaits its Socket hello at the candidate
                // addresses; the runtime addresses stay empty until promotion.
                await _secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateAppToken), Encoding.UTF8.GetBytes("xapp-candidate"));
                await _secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken), Encoding.UTF8.GetBytes("xoxb-candidate"));
            }
        }

        return new SeededTarget(agentAppId, connectionId, appId);
    }

    private async Task DisableConnectionAsync(string connectionId)
    {
        await using var db = _factory.CreateDbContext();
        var connection = await db.AgentConnections.SingleAsync(item => item.Id == connectionId);
        connection.DesiredState = DesiredStateKind.Disabled;
        connection.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    private async Task AssertAgentAppStateAsync(string agentAppId, string credentialState, string bindingState)
    {
        await using var db = _factory.CreateDbContext();
        var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
        Assert.Equal(credentialState, row.RuntimeCredentialValidationState);
        Assert.Equal(bindingState, row.BindingState);
    }

    private async Task AssertConnectionIdentityAsync(string connectionId, string appId)
    {
        await using var db = _factory.CreateDbContext();
        var connection = await db.AgentConnections.SingleAsync(item => item.Id == connectionId);
        Assert.Equal(appId, connection.AppId);
        Assert.Equal(!string.IsNullOrEmpty(appId), !string.IsNullOrEmpty(connection.BotUserId));
    }

    private sealed record SeededTarget(string AgentAppId, string ConnectionId, string AppId)
    {
        public SlackLeaseTargetRef.Connection TargetRef => new(ProjectId, ConnectionId);
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _values = [];

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _values[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(address, out var value) ? value : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
