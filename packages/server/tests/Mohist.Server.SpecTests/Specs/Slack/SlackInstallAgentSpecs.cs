using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackInstallAgentSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
    private const string ProjectId = "project-install";
    private const string AgentId = "agent-review-bot";
    private const string TeamId = "T_INSTALL_AGENT";

    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly TestDbContextFactory _factory;
    private readonly FakeSlackAppManagementPort _apps = new();
    private readonly FakeSlackBotIdentityVerificationPort _botIdentity = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly SlackInstallAgentService _service;

    public SlackInstallAgentSpecs()
    {
        _factory = new TestDbContextFactory(_database.Options);
        var agents = new AgentQuerier(_factory);
        var connections = new AgentConnectionStore(_factory, agents, _secrets, [], _time);
        var enrollments = new SlackWorkspaceEnrollmentStore(_factory, _time);
        var agentApps = new ManagedSlackAgentAppStore(_factory, _time);
        var operations = new ManagedSlackAgentAppApplicationService(_factory, _apps, _apps, new SlackManifestGenerator(), _secrets, _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);
        _service = new SlackInstallAgentService(
            agents, connections, enrollments, agentApps,
            new SlackManifestGenerator(), operations, binding,
            _apps, _botIdentity, _secrets);
    }

    [Fact]
    public async Task First_install_creates_a_team_fixed_connection_and_agent_app_and_rerun_restores_the_same_records()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");

        var first = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");

        Assert.Equal(SlackAppLifecycle.Created, first.AgentApp.AppLifecycle);
        Assert.Equal(1, _apps.CreateCalls);
        Assert.False(string.IsNullOrWhiteSpace(first.InstallUrl));
        Assert.Equal(TeamId, first.Connection.WorkspaceTeamId);
        Assert.Equal(string.Empty, first.Connection.AppId);
        Assert.Equal(string.Empty, first.Connection.BotUserId);
        var appId = first.AgentApp.AppId;
        Assert.False(string.IsNullOrWhiteSpace(appId));
        Assert.Equal(SlackRuntimeCredentialValidationState.NotProvided, first.AgentApp.RuntimeCredentialValidationState);
        Assert.Equal(SlackAgentAppNextAction.ProvideCredentials, first.NextAction);

        Assert.True(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(first.AgentApp.Id, SecretKind.ClientSecret)));
        Assert.True(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(first.AgentApp.Id, SecretKind.SigningSecret)));

        var rerun = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");

        Assert.Equal(first.Connection.Id, rerun.Connection.Id);
        Assert.Equal(first.AgentApp.Id, rerun.AgentApp.Id);
        Assert.Equal(first.InstallUrl, rerun.InstallUrl);
        Assert.Equal(1, _apps.CreateCalls);
        Assert.Equal(SlackAgentAppNextAction.ProvideCredentials, rerun.NextAction);
    }

    [Fact]
    public async Task Unknown_create_never_replays_create_on_rerun()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == installed.AgentApp.Id);
            row.AppLifecycle = SlackAppLifecycle.CreateUnknown;
            row.UnknownOutcome = "timeout";
            await db.SaveChangesAsync();
        }

        var rerun = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");

        Assert.Equal(SlackAppLifecycle.CreateUnknown, rerun.AgentApp.AppLifecycle);
        Assert.Equal(SlackAgentAppNextAction.ReconcileCreate, rerun.NextAction);
        Assert.Equal(1, _apps.CreateCalls);
        Assert.Equal(installed.AgentApp.Id, rerun.AgentApp.Id);
    }

    [Fact]
    public async Task Provision_credentials_rejects_unverified_or_mismatched_identity_without_storing_secrets()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;

        _botIdentity.Result = new SlackBotIdentityVerificationResult(false, ErrorClass: "invalid_auth");
        var failed = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-wrong", "xapp-wrong");
        Assert.False(failed.Accepted);
        Assert.Equal("invalid_auth", failed.ErrorClass);
        Assert.False(HasCandidateSecretsAsync(agentAppId));

        _botIdentity.Result = new SlackBotIdentityVerificationResult(true, WorkspaceTeamId: "T_OTHER", BotUserId: "U_BOT", AppId: installed.AgentApp.AppId);
        var teamMismatch = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-other", "xapp-other");
        Assert.False(teamMismatch.Accepted);
        Assert.Equal("identity_mismatch", teamMismatch.ErrorClass);
        Assert.False(HasCandidateSecretsAsync(agentAppId));
    }

    [Fact]
    public async Task Provision_credentials_stages_candidates_under_agent_app_owner_and_never_binds_the_connection()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;
        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true,
            WorkspaceTeamId: TeamId,
            BotUserId: "U_REVIEW_BOT",
            AppId: installed.AgentApp.AppId,
            GrantedScopes: new HashSet<string>(["chat:write", "users:read"]));

        var staged = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-candidate", "xapp-candidate");

        Assert.True(staged.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged.RuntimeCredentialValidationState);
        Assert.True(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken)));
        Assert.True(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken)));
        Assert.Equal("xoxb-candidate", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForAgentConnection(ProjectId, installed.Connection.Id, SecretKind.BotToken)));

        var rerun = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-candidate", "xapp-candidate");
        Assert.True(rerun.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, rerun.RuntimeCredentialValidationState);

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
            Assert.Equal(SlackAuthorizationState.Authorized, row.Authorization);
            Assert.Equal("U_REVIEW_BOT", row.BotUserId);
            Assert.Contains("chat:write", row.VerifiedScopesJson, StringComparison.Ordinal);
            var connection = await db.AgentConnections.SingleAsync(item => item.Id == installed.Connection.Id);
            Assert.Equal(string.Empty, connection.AppId);
            Assert.Equal(string.Empty, connection.BotUserId);
        }
    }

    [Fact]
    public async Task Socket_hello_validation_binds_connection_once_and_mismatch_deletes_candidates()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;
        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true, WorkspaceTeamId: TeamId, BotUserId: "U_REVIEW_BOT", AppId: installed.AgentApp.AppId);
        await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-candidate", "xapp-candidate");

        var mismatch = await _service.ApplySocketValidationAsync(agentAppId, "A_WRONG_APP");
        Assert.Equal(SlackInstallAgentValidationOutcome.Mismatch, mismatch.Outcome);
        Assert.False(HasCandidateSecretsAsync(agentAppId));
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
            Assert.Equal(SlackRuntimeCredentialValidationState.Failed, row.RuntimeCredentialValidationState);
            Assert.Equal(SlackAgentAppBindingState.Pending, row.BindingState);
        }

        await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-candidate", "xapp-candidate");
        var verified = await _service.ApplySocketValidationAsync(agentAppId, installed.AgentApp.AppId);

        Assert.Equal(SlackInstallAgentValidationOutcome.Verified, verified.Outcome);
        Assert.Equal(SlackAgentAppBindingStatus.Bound, verified.Binding);
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
            Assert.Equal(SlackRuntimeCredentialValidationState.Verified, row.RuntimeCredentialValidationState);
            Assert.Equal(SlackAgentAppBindingState.Bound, row.BindingState);
            var connection = await db.AgentConnections.SingleAsync(item => item.Id == installed.Connection.Id);
            Assert.Equal(installed.AgentApp.AppId, connection.AppId);
            Assert.Equal("U_REVIEW_BOT", connection.BotUserId);
        }

        var again = await _service.ApplySocketValidationAsync(agentAppId, installed.AgentApp.AppId);
        Assert.Equal(SlackInstallAgentValidationOutcome.AlreadyVerified, again.Outcome);
        var progress = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        Assert.Equal(SlackAgentAppNextAction.Ready, progress.NextAction);
        Assert.Equal(SlackAgentAppBindingState.Bound, progress.AgentApp.BindingState);
        Assert.Equal(1, _apps.CreateCalls);
    }

    [Fact]
    public async Task Runtime_lease_becomes_acquirable_after_verified_hello_and_binding()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;
        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true, WorkspaceTeamId: TeamId, BotUserId: "U_REVIEW_BOT", AppId: installed.AgentApp.AppId);
        await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-live", "xapp-live");
        await _service.ApplySocketValidationAsync(agentAppId, installed.AgentApp.AppId);

        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, installed.Connection.Id);
        var provider = new InMemorySlackLeaseTargetProvider().Add(new SlackLeaseTarget(
            targetRef,
            ExpectedAppId: installed.AgentApp.AppId,
            Active: true,
            AppLevelTokenProvisioned: true,
            BotTokenProvisioned: true,
            CredentialVerified: true,
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken),
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken)));
        var lease = new SlackAdapterLeaseService(
            new InMemorySlackLeaseStore(), provider, new SlackLeaseSecretResolver(_secrets), _time);

        var runtime = await lease.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A");

        Assert.NotNull(runtime);
        Assert.Equal("xapp-live", runtime!.AppToken);
        Assert.Equal("xoxb-live", runtime.BotToken);
    }

    [Fact]
    public async Task Verified_agent_app_rotation_stages_new_candidates_and_verifies_the_new_pair()
    {
        var (agentAppId, connectionId, appId) = await DriveToVerifiedAsync("xoxb-live", "xapp-live");
        _botIdentity.Result = VerifiedAgentBot(appId);

        var staged = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-rotated", "xapp-rotated");

        Assert.True(staged.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged.RuntimeCredentialValidationState);
        Assert.Equal("xoxb-rotated", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-rotated", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.PreviousBotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.PreviousAppToken));

        var verified = await _service.ApplySocketValidationAsync(agentAppId, appId);

        Assert.Equal(SlackInstallAgentValidationOutcome.Verified, verified.Outcome);
        Assert.Equal(SlackAgentAppBindingStatus.Bound, verified.Binding);
        Assert.Equal("xoxb-rotated", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-rotated", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken)));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousAppToken)));
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
            Assert.Equal(SlackRuntimeCredentialValidationState.Verified, row.RuntimeCredentialValidationState);
            var connection = await db.AgentConnections.SingleAsync(item => item.Id == connectionId);
            Assert.Equal(appId, connection.AppId);
            Assert.Equal("U_REVIEW_BOT", connection.BotUserId);
        }
    }

    [Fact]
    public async Task Verified_agent_app_resupplying_the_same_credentials_is_an_idempotent_noop()
    {
        var (agentAppId, _, appId) = await DriveToVerifiedAsync("xoxb-live", "xapp-live");
        var verifyCalls = _botIdentity.Requests.Count;
        _botIdentity.Result = VerifiedAgentBot(appId);

        var again = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-live", "xapp-live");

        Assert.True(again.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Verified, again.RuntimeCredentialValidationState);
        Assert.Equal(verifyCalls, _botIdentity.Requests.Count);
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken)));
    }

    [Fact]
    public async Task Verified_agent_app_rotation_with_mismatched_identity_keeps_the_previous_verified_state()
    {
        var (agentAppId, _, _) = await DriveToVerifiedAsync("xoxb-live", "xapp-live");
        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true, WorkspaceTeamId: "T_OTHER", BotUserId: "U_REVIEW_BOT", AppId: "A_OTHER");

        var rejected = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-bad", "xapp-bad");

        Assert.False(rejected.Accepted);
        Assert.Equal("identity_mismatch", rejected.ErrorClass);
        Assert.Equal(SlackRuntimeCredentialValidationState.Verified, rejected.RuntimeCredentialValidationState);
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken)));
    }

    [Fact]
    public async Task In_flight_agent_app_rotation_with_mismatched_hello_restores_the_previous_verified_pair()
    {
        var (agentAppId, connectionId, appId) = await DriveToVerifiedAsync("xoxb-live", "xapp-live");
        _botIdentity.Result = VerifiedAgentBot(appId);
        var staged = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-rotated", "xapp-rotated");
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged.RuntimeCredentialValidationState);

        var mismatch = await _service.ApplySocketValidationAsync(agentAppId, "A_WRONG_APP");

        Assert.Equal(SlackInstallAgentValidationOutcome.Mismatch, mismatch.Outcome);
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken)));
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
            Assert.Equal(SlackRuntimeCredentialValidationState.Verified, row.RuntimeCredentialValidationState);
            Assert.Equal(SlackAgentAppBindingState.Bound, row.BindingState);
            var connection = await db.AgentConnections.SingleAsync(item => item.Id == connectionId);
            Assert.Equal(appId, connection.AppId);
            Assert.Equal("U_REVIEW_BOT", connection.BotUserId);
        }
    }

    private async Task<(string AgentAppId, string ConnectionId, string AppId)> DriveToVerifiedAsync(string botToken, string appToken)
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        _botIdentity.Result = VerifiedAgentBot(installed.AgentApp.AppId);
        var staged = await _service.ProvisionCredentialsAsync(installed.AgentApp.Id, botToken, appToken);
        Assert.True(staged.Accepted);
        var verified = await _service.ApplySocketValidationAsync(installed.AgentApp.Id, installed.AgentApp.AppId);
        Assert.Equal(SlackInstallAgentValidationOutcome.Verified, verified.Outcome);
        return (installed.AgentApp.Id, installed.Connection.Id, installed.AgentApp.AppId);
    }

    private static SlackBotIdentityVerificationResult VerifiedAgentBot(string appId) => new(
        true,
        WorkspaceTeamId: TeamId,
        BotUserId: "U_REVIEW_BOT",
        AppId: appId,
        GrantedScopes: new HashSet<string>(["chat:write", "users:read"]));

    private bool HasCandidateSecretsAsync(string agentAppId) =>
        _secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken))
        || _secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken));

    private string ReadSecretAsync(string agentAppId, SecretKind kind) =>
        Encoding.UTF8.GetString(_secrets.Addresses[SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, kind)]);

    private async Task SeedAgentAsync(string status)
    {
        await using var db = _factory.CreateDbContext();
        db.Agents.Add(new AgentRow
        {
            Id = AgentId,
            State = AgentStore.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = AgentId,
                ProjectId = ProjectId,
                Name = "Review Bot",
                Status = status,
            }),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedEnrollmentAsync(string enrollmentId)
    {
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = TeamId,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            AuditJson = "[]",
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        public Dictionary<SecretStoreAddress, byte[]> Addresses { get; } = [];

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            Addresses[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(Addresses.TryGetValue(address, out var value) ? value : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(Addresses.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
