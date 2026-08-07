using System.Text;
using System.Text.Json;
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
using Mohist.Server.TestSupport;
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
        _service = BuildService(_secrets);
    }

    private SlackInstallAgentService BuildService(ISecretStore secrets)
    {
        var agents = new AgentQuerier(_factory);
        var connections = new AgentConnectionStore(_factory, agents, _secrets, [], _time);
        var enrollments = new SlackWorkspaceEnrollmentStore(_factory, _time);
        var agentApps = new ManagedSlackAgentAppStore(_factory, _time);
        var operations = new ManagedSlackAgentAppApplicationService(_factory, agents, _apps, _apps, new SlackManifestGenerator(), _secrets, _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);
        return new SlackInstallAgentService(
            agents, connections, enrollments, agentApps,
            new SlackManifestGenerator(), operations, binding,
            _apps, _botIdentity, secrets);
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
    public async Task Created_agent_app_manifest_carries_the_sanitized_bot_name_and_a_non_empty_description()
    {
        await SeedAgentAsync(AgentStatus.Active, name: "reviewbot");
        await SeedEnrollmentAsync("enrollment-1");

        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");

        Assert.NotNull(_apps.LastCreateManifestJson);
        using var document = JsonDocument.Parse(_apps.LastCreateManifestJson!);
        var display = document.RootElement.GetProperty("display_information");
        Assert.Equal("reviewbot", display.GetProperty("name").GetString());
        Assert.Equal("A Mohist Agent available in Slack.", display.GetProperty("description").GetString());
        Assert.NotEqual("agent-app", display.GetProperty("name").GetString());
        Assert.Equal("reviewbot",
            document.RootElement.GetProperty("features").GetProperty("bot_user").GetProperty("display_name").GetString());

        string hashBefore;
        await using (var db = _factory.CreateDbContext())
        {
            hashBefore = (await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == installed.AgentApp.Id)).DesiredManifestHash;
        }

        await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");

        await using (var db = _factory.CreateDbContext())
        {
            var rerun = await db.ManagedSlackAgentApps.AsNoTracking()
                .SingleAsync(item => item.Id == installed.AgentApp.Id);
            Assert.Equal(hashBefore, rerun.DesiredManifestHash);
        }
    }

    [Fact]
    public async Task Created_agent_app_manifest_uses_the_agent_description_when_present()
    {
        await SeedAgentAsync(AgentStatus.Active, "Reviews release pull requests.", "reviewbot");
        await SeedEnrollmentAsync("enrollment-1");

        await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");

        Assert.NotNull(_apps.LastCreateManifestJson);
        using var document = JsonDocument.Parse(_apps.LastCreateManifestJson!);
        Assert.Equal("Reviews release pull requests.",
            document.RootElement.GetProperty("display_information").GetProperty("description").GetString());
        Assert.Equal("reviewbot",
            document.RootElement.GetProperty("display_information").GetProperty("name").GetString());
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
        _botIdentity.Result = VerifiedAgentBot(installed.AgentApp.AppId);

        var staged = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-candidate", "xapp-candidate");

        Assert.True(staged.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged.RuntimeCredentialValidationState);
        Assert.True(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken)));
        Assert.True(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateAppToken)));
        Assert.Equal("xoxb-candidate", ReadSecretAsync(agentAppId, SecretKind.CandidateBotToken));
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
    public async Task Runtime_lease_becomes_acquirable_after_verified_hello_and_binding()
    {
        var (_, connectionId, _) = await DriveToVerifiedAsync("xoxb-live", "xapp-live");
        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, connectionId);
        var lease = BuildLeases();

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
        Assert.Equal("xoxb-rotated", ReadSecretAsync(agentAppId, SecretKind.CandidateBotToken));
        Assert.Equal("xapp-rotated", ReadSecretAsync(agentAppId, SecretKind.CandidateAppToken));
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.PreviousBotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.PreviousAppToken));
        // The runtime addresses keep serving the old verified pair until hello.
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.AppToken));

        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, connectionId);
        var leases = BuildLeases();
        var validation = await leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.Verified,
            await leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, appId));
        Assert.Equal("xoxb-rotated", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-rotated", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken)));
        Assert.False(_secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateAppToken)));
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

        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, connectionId);
        var leases = BuildLeases();
        var validation = await leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, "A_WRONG_APP"));
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

    [Fact]
    public async Task Rotation_crash_at_candidate_store_never_serves_the_unverified_pair_to_a_runtime_lease()
    {
        var (agentAppId, connectionId, appId) = await DriveToVerifiedAsync("xoxb-live", "xapp-live");
        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, connectionId);
        var leases = BuildLeases();

        // Before the rotation the runtime lease serves the verified live pair.
        var live = await leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(live);
        Assert.Equal("xoxb-live", live!.BotToken);
        Assert.Equal("xapp-live", live.AppToken);

        var faultingService = BuildService(new FaultingSecretStore(_secrets));

        _botIdentity.Result = VerifiedAgentBot(appId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faultingService.ProvisionCredentialsAsync(agentAppId, "xoxb-rotated", "xapp-rotated"));

        // Stage ran before the faulted Store, so the state has left Verified and
        // the runtime lease is closed: it returns null rather than the new,
        // unverified candidate. The runtime address still holds the old verified
        // pair, also parked in Previous for restore.
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, await ReadAgentAppStateAsync(agentAppId));
        Assert.Null(await leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A"));
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.BotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.AppToken));
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.PreviousBotToken));
        Assert.Equal("xapp-live", ReadSecretAsync(agentAppId, SecretKind.PreviousAppToken));
        Assert.False(_secrets.Addresses.ContainsKey(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken)));

        // Resume with the working store converges; after a correct hello the
        // runtime lease serves the new pair.
        _botIdentity.Result = VerifiedAgentBot(appId);
        var resumed = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-rotated", "xapp-rotated");
        Assert.True(resumed.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, resumed.RuntimeCredentialValidationState);
        Assert.Equal("xoxb-rotated", ReadSecretAsync(agentAppId, SecretKind.CandidateBotToken));
        Assert.Equal("xoxb-live", ReadSecretAsync(agentAppId, SecretKind.BotToken));

        var validation = await leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.Verified,
            await leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, appId));
        Assert.Equal(SlackRuntimeCredentialValidationState.Verified, await ReadAgentAppStateAsync(agentAppId));
        Assert.False(_secrets.Addresses.ContainsKey(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken)));

        var rotated = await leases.AcquireRuntimeLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(rotated);
        Assert.Equal("xoxb-rotated", rotated!.BotToken);
        Assert.Equal("xapp-rotated", rotated.AppToken);
    }

    [Fact]
    public async Task Provision_credentials_with_a_missing_required_scope_fails_closed_without_storing_or_binding()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;
        var incomplete = AgentAppBotScopes.Where(scope => scope != "chat:write").ToHashSet();

        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true,
            WorkspaceTeamId: TeamId,
            BotUserId: "U_REVIEW_BOT",
            AppId: installed.AgentApp.AppId,
            GrantedScopes: incomplete);

        var failed = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-partial", "xapp-partial");

        Assert.False(failed.Accepted);
        Assert.Equal("missing_required_scopes", failed.ErrorClass);
        Assert.False(HasCandidateSecretsAsync(agentAppId));
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
            Assert.Equal(SlackRuntimeCredentialValidationState.NotProvided, row.RuntimeCredentialValidationState);
            Assert.Equal(SlackAgentAppBindingState.Pending, row.BindingState);
            Assert.Equal(string.Empty, row.BotUserId);
        }
    }

    [Fact]
    public async Task Provision_credentials_with_full_required_scopes_stages_candidate_and_allows_extra_scopes()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;
        var superset = new HashSet<string>(AgentAppBotScopes) { "files:read" };

        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true,
            WorkspaceTeamId: TeamId,
            BotUserId: "U_REVIEW_BOT",
            AppId: installed.AgentApp.AppId,
            GrantedScopes: superset);

        var staged = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-full", "xapp-full");

        Assert.True(staged.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged.RuntimeCredentialValidationState);
        Assert.Equal("xoxb-full", ReadSecretAsync(agentAppId, SecretKind.CandidateBotToken));
        Assert.Equal("xapp-full", ReadSecretAsync(agentAppId, SecretKind.CandidateAppToken));
    }

    [Fact]
    public async Task Missing_required_scope_is_repeatable_and_a_full_scope_resupply_then_stages()
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        var agentAppId = installed.AgentApp.Id;
        var incomplete = AgentAppBotScopes.Where(scope => scope != "im:history").ToHashSet();

        _botIdentity.Result = new SlackBotIdentityVerificationResult(
            true,
            WorkspaceTeamId: TeamId,
            BotUserId: "U_REVIEW_BOT",
            AppId: installed.AgentApp.AppId,
            GrantedScopes: incomplete);

        var first = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-missing", "xapp-missing");
        Assert.False(first.Accepted);
        Assert.Equal("missing_required_scopes", first.ErrorClass);
        Assert.False(HasCandidateSecretsAsync(agentAppId));

        var second = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-missing", "xapp-missing");
        Assert.False(second.Accepted);
        Assert.Equal("missing_required_scopes", second.ErrorClass);
        Assert.False(HasCandidateSecretsAsync(agentAppId));

        _botIdentity.Result = VerifiedAgentBot(installed.AgentApp.AppId);
        var staged = await _service.ProvisionCredentialsAsync(agentAppId, "xoxb-full", "xapp-full");
        Assert.True(staged.Accepted);
        Assert.Equal(SlackRuntimeCredentialValidationState.Candidate, staged.RuntimeCredentialValidationState);
        Assert.Equal("xoxb-full", ReadSecretAsync(agentAppId, SecretKind.CandidateBotToken));
    }

    private async Task<(string AgentAppId, string ConnectionId, string AppId)> DriveToVerifiedAsync(string botToken, string appToken)
    {
        await SeedAgentAsync(AgentStatus.Active);
        await SeedEnrollmentAsync("enrollment-1");
        var installed = await _service.InstallAsync(ProjectId, AgentId, "enrollment-1");
        _botIdentity.Result = VerifiedAgentBot(installed.AgentApp.AppId);
        var staged = await _service.ProvisionCredentialsAsync(installed.AgentApp.Id, botToken, appToken);
        Assert.True(staged.Accepted);
        var targetRef = new SlackLeaseTargetRef.Connection(ProjectId, installed.Connection.Id);
        var leases = BuildLeases();
        var validation = await leases.AcquireValidationLeaseAsync("operator-1", targetRef, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal(SlackHelloOutcome.Verified,
            await leases.ReportHelloAsync("operator-1", targetRef, validation!.LeaseId, installed.AgentApp.AppId));
        return (installed.AgentApp.Id, installed.Connection.Id, installed.AgentApp.AppId);
    }

    private static readonly IReadOnlyCollection<string> AgentAppBotScopes =
        SlackManifestDefinition.For(SlackManifestKind.AgentApp).BotScopes;

    private static SlackBotIdentityVerificationResult VerifiedAgentBot(string appId) => new(
        true,
        WorkspaceTeamId: TeamId,
        BotUserId: "U_REVIEW_BOT",
        AppId: appId,
        GrantedScopes: new HashSet<string>(AgentAppBotScopes));

    private bool HasCandidateSecretsAsync(string agentAppId) =>
        _secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken))
        || _secrets.Addresses.ContainsKey(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateAppToken));

    private string ReadSecretAsync(string agentAppId, SecretKind kind) =>
        Encoding.UTF8.GetString(_secrets.Addresses[SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, kind)]);

    private async Task<string> ReadAgentAppStateAsync(string agentAppId)
    {
        await using var db = _factory.CreateDbContext();
        var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == agentAppId);
        return row.RuntimeCredentialValidationState;
    }

    private SlackAdapterLeaseService BuildLeases()
    {
        var connections = new AgentConnectionStore(_factory, new AgentQuerier(_factory), _secrets, [], _time);
        var agentApps = new ManagedSlackAgentAppStore(_factory, _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);
        var provider = new EnrollmentSlackLeaseTargetProvider(
            new SlackWorkspaceEnrollmentStore(_factory, _time), agentApps, binding, _factory, _secrets);
        return new SlackAdapterLeaseService(
            new SlackAdapterLeaseStore(_factory), provider, new SlackLeaseSecretResolver(_secrets), _time);
    }

    private async Task SeedAgentAsync(string status, string? description = null, string name = "Review Bot")
    {
        await using var db = _factory.CreateDbContext();
        db.Agents.Add(new AgentRow
        {
            Id = AgentId,
            State = AgentStore.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = AgentId,
                ProjectId = ProjectId,
                Name = name,
                Status = status,
                Description = description ?? string.Empty,
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

    private sealed class FaultingSecretStore : ISecretStore
    {
        private readonly ISecretStore _inner;

        public FaultingSecretStore(ISecretStore inner) => _inner = inner;

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            // Preserve (previous slot) and every load/delete still succeed, but
            // storing the candidate Bot token fails after the state has left
            // Verified — the boundary the old ordering got wrong.
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
