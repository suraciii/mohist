using System.Security.Cryptography;
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
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerCorrectnessKernelSpecs : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(TestTime.UtcNow);
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private FakeSlackAppManagementPort _apps = null!;
    private ManagedSlackChildAppApplicationService _appService = null!;

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _apps = new FakeSlackAppManagementPort();
        _appService = new ManagedSlackChildAppApplicationService(_factory, _apps, _apps, _time);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_create_attempts_share_one_fence_and_one_external_call()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.NotCreated);
        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Create: new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded, "A_CONCURRENT")));

        var results = await Task.WhenAll(
            _appService.CreateAsync(child.Id),
            _appService.CreateAsync(child.Id));

        Assert.Equal(1, results.Count(result => result.Status == ManagedSlackChildAppOperationStatus.Completed));
        Assert.Contains(results, result => result.Status is ManagedSlackChildAppOperationStatus.Concurrent or ManagedSlackChildAppOperationStatus.NotAllowed);
        Assert.Equal(1, _apps.CreateCalls);
        await using var db = _factory.CreateDbContext();
        var row = await db.ManagedSlackChildApps.SingleAsync(item => item.Id == child.Id);
        Assert.Equal(SlackAppLifecycle.Created, row.AppLifecycle);
        Assert.Equal(1, row.OperationFence);
    }

    [Fact]
    public async Task Unknown_create_can_only_leave_via_fact_reconcile_and_never_replays_create()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.NotCreated);
        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Create: new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "timeout")));

        var create = await _appService.CreateAsync(child.Id);
        var retry = await _appService.CreateAsync(child.Id);

        Assert.Equal(ManagedSlackChildAppOperationStatus.Completed, create.Status);
        Assert.Equal(SlackAppManagementOutcome.Unknown, create.Outcome);
        Assert.Equal(ManagedSlackChildAppOperationStatus.NotAllowed, retry.Status);
        Assert.Equal(1, _apps.CreateCalls);
        Assert.Equal(SlackAppLifecycle.CreateUnknown, await LifecycleAsync(child.Id));

        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(SlackAppManagementFactOutcome.Present, "A_FACT")));
        var reconciled = await _appService.ReconcileCreateAsync(child.Id);

        Assert.Equal(ManagedSlackChildAppOperationStatus.Reconciled, reconciled.Status);
        Assert.Equal(SlackAppManagementFactOutcome.Present, reconciled.FactOutcome);
        Assert.Equal(1, _apps.CreateCalls);
        Assert.Equal(SlackAppLifecycle.Created, await LifecycleAsync(child.Id));
    }

    [Fact]
    public async Task Unknown_delete_is_symmetric_and_reconcile_never_replays_delete()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            appId: "A_DELETE",
            botUserId: "U_DELETE");
        await SoftDeleteConnectionAsync(child.AgentConnectionId);
        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Delete: new SlackAppManagementResult(SlackAppManagementOutcome.Unknown, ErrorClass: "internal_error")));

        var delete = await _appService.DeleteAsync(child.Id, "DELETE", "operator-1");
        var retry = await _appService.DeleteAsync(child.Id, "DELETE", "operator-1");

        Assert.Equal(ManagedSlackChildAppOperationStatus.Completed, delete.Status);
        Assert.Equal(SlackAppManagementOutcome.Unknown, delete.Outcome);
        Assert.Equal(ManagedSlackChildAppOperationStatus.NotAllowed, retry.Status);
        Assert.Equal(1, _apps.DeleteCalls);
        Assert.Equal(SlackAppLifecycle.DeleteUnknown, await LifecycleAsync(child.Id));

        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(SlackAppManagementFactOutcome.Absent)));
        var reconciled = await _appService.ReconcileDeleteAsync(child.Id);

        Assert.Equal(ManagedSlackChildAppOperationStatus.Reconciled, reconciled.Status);
        Assert.Equal(SlackAppManagementFactOutcome.Absent, reconciled.FactOutcome);
        Assert.Equal(1, _apps.DeleteCalls);
        Assert.Equal(SlackAppLifecycle.Deleted, await LifecycleAsync(child.Id));
    }

    [Fact]
    public async Task Permanent_delete_requires_confirmation_audit_and_no_active_connection_binding()
    {
        var active = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            appId: "A_ACTIVE",
            botUserId: "U_ACTIVE");
        var blocked = await _appService.DeleteAsync(active.Id, "DELETE", "operator-1");
        var unconfirmed = await _appService.DeleteAsync(active.Id, "delete", "operator-1");

        Assert.Equal("active_connection_binding", blocked.ErrorClass);
        Assert.Equal("confirmation_required", unconfirmed.ErrorClass);
        Assert.Equal(0, _apps.DeleteCalls);

        await SoftDeleteConnectionAsync(active.AgentConnectionId);
        _apps.SetResponse(active.Id, new FakeSlackAppResponse(
            Delete: new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded)));
        var deleted = await _appService.DeleteAsync(active.Id, "DELETE", "operator-1");

        Assert.Equal(ManagedSlackChildAppOperationStatus.Completed, deleted.Status);
        Assert.Equal(SlackAppManagementOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(SlackAppLifecycle.Deleted, await LifecycleAsync(active.Id));
        await using var db = _factory.CreateDbContext();
        var audit = await db.ManagedSlackChildApps.Where(row => row.Id == active.Id).Select(row => row.AuditJson).SingleAsync();
        Assert.Contains("permanent_delete", audit, StringComparison.Ordinal);
        Assert.Contains("operator-1", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_axes_derive_transport_readiness_manifest_drift_and_one_next_action()
    {
        var child = new ManagedSlackChildApp
        {
            AppLifecycle = SlackAppLifecycle.NotCreated,
            Authorization = SlackAuthorizationState.NotStarted,
            TransportKind = SlackTransportKind.Socket,
        };
        Assert.Equal(SlackChildAppNextAction.CreateChildApp, child.NextAction);

        child.AppLifecycle = SlackAppLifecycle.CreateUnknown;
        Assert.Equal(SlackChildAppNextAction.ReconcileCreate, child.NextAction);

        child.AppLifecycle = SlackAppLifecycle.Created;
        child.Authorization = SlackAuthorizationState.Authorized;
        child.DesiredManifestVersion = 2;
        child.DesiredManifestHash = "desired";
        child.AppliedManifestVersion = 1;
        child.AppliedManifestHash = "old";
        child.BindingState = SlackChildAppBindingState.Bound;
        child.TransportKind = SlackTransportKind.Https;
        child.PublicIngressBaseUrl = "https://mohist.example/slack";
        child.SigningSecretRef = "secret-ref";
        child.AppLevelTokenRef = string.Empty;
        child.BotTokenRef = string.Empty;

        var status = ManagedSlackChildAppStatusDeriver.Derive(child);
        Assert.Equal(SlackManifestState.DriftKnown, status.ManifestState);
        Assert.Equal(SlackTransportReadiness.Ready, status.TransportReadiness);
        Assert.Equal(SlackChildAppNextAction.ApplyManifest, status.NextAction);

        child.AppliedManifestVersion = 2;
        child.AppliedManifestHash = "desired";
        status = ManagedSlackChildAppStatusDeriver.Derive(child);
        Assert.Equal(SlackChildAppNextAction.Ready, status.NextAction);

        child.SigningSecretRef = string.Empty;
        Assert.Equal(SlackTransportReadiness.NotReady, ManagedSlackChildAppStatusDeriver.DeriveTransportReadiness(child));
        Assert.Equal(SlackChildAppNextAction.ConfigureHttpsIngress, child.NextAction);

        child.TransportKind = SlackTransportKind.Socket;
        child.AppLevelTokenRef = string.Empty;
        child.BotTokenRef = "bot-ref";
        Assert.Equal(SlackTransportReadiness.NotReady, ManagedSlackChildAppStatusDeriver.DeriveTransportReadiness(child));
    }

    [Fact]
    public void Manifest_is_canonical_and_drift_fingerprint_excludes_secrets_and_metadata_from_live_schema()
    {
        var generator = new SlackManifestGenerator();
        var first = generator.Generate(new SlackManifestInput(
            "Mohist Agent",
            "Handles work",
            ["chat:write", "commands"],
            SlackManifestTransport.Socket,
            null,
            "capability-1",
            new SlackManifestIdentitySnapshot("connection-1", "agent-1", "T123")));
        var reordered = generator.Generate(new SlackManifestInput(
            "Mohist Agent",
            "Handles work",
            ["commands", "chat:write"],
            SlackManifestTransport.Socket,
            null,
            "capability-1",
            new SlackManifestIdentitySnapshot("connection-1", "agent-1", "T123")));
        var capabilityDrift = generator.Generate(new SlackManifestInput(
            "Mohist Agent",
            "Handles work",
            ["commands", "chat:write"],
            SlackManifestTransport.Socket,
            null,
            "capability-2",
            new SlackManifestIdentitySnapshot("connection-1", "agent-1", "T123")));
        var identityDrift = generator.Generate(new SlackManifestInput(
            "Mohist Agent",
            "Handles work",
            ["commands", "chat:write"],
            SlackManifestTransport.Socket,
            null,
            "capability-1",
            new SlackManifestIdentitySnapshot("connection-2", "agent-1", "T123")));

        Assert.Equal(first.CanonicalJson, reordered.CanonicalJson);
        Assert.Equal(first.Hash, reordered.Hash);
        Assert.NotEqual(first.Hash, capabilityDrift.Hash);
        Assert.NotEqual(first.Hash, identityDrift.Hash);
        Assert.DoesNotContain("product_capability_version", first.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-1", first.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", first.CanonicalJson, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => generator.Generate(new SlackManifestInput(
            "HTTPS Agent", "Handles work", ["chat:write"], SlackManifestTransport.Https, null,
            "capability-1", new SlackManifestIdentitySnapshot("c", "a", "T"))));

        var https = generator.Generate(new SlackManifestInput(
            "HTTPS Agent", "Handles work", ["chat:write"], SlackManifestTransport.Https,
            "https://mohist.example", "capability-1", new SlackManifestIdentitySnapshot("c", "a", "T")));
        Assert.Contains("request_url", https.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("https://mohist.example/slack/events", https.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_state_is_hashed_single_use_and_mismatch_never_stores_a_bot_token()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_OAUTH");
        var states = new SlackOAuthStateService(_factory, _time);
        var issued = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId);

        await using (var before = _factory.CreateDbContext())
        {
            var row = await before.SlackOAuthStates.SingleAsync();
            Assert.Equal(SlackOAuthStateService.Hash(issued.State), row.StateHash);
            Assert.DoesNotContain(issued.State, row.StateHash, StringComparison.Ordinal);
            Assert.Null(row.ConsumedAt);
        }

        Assert.Equal(SlackOAuthStateValidation.Mismatch,
            await states.ConsumeAsync(issued.State, child.Id, "T_OTHER", child.AppId));
        var accepted = await states.ConsumeAsync(issued.State, child.Id, child.WorkspaceTeamId, child.AppId);
        var replay = await states.ConsumeAsync(issued.State, child.Id, child.WorkspaceTeamId, child.AppId);
        Assert.Equal(SlackOAuthStateValidation.Accepted, accepted);
        Assert.Equal(SlackOAuthStateValidation.ReplayAccepted, replay);

        var expired = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId, TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(SlackOAuthStateValidation.Expired,
            await states.ConsumeAsync(expired.State, child.Id, child.WorkspaceTeamId, child.AppId));
    }

    [Fact]
    public async Task OAuth_authorization_stores_only_reference_and_replay_is_idempotent()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_AUTH");
        var states = new SlackOAuthStateService(_factory, _time);
        var issued = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId);
        var sink = new FakeSlackOAuthCredentialSink();
        var authorization = new SlackOAuthAuthorizationService(_factory, states, sink, _time);

        var mismatch = await authorization.AuthorizeAsync(
            issued.State, child.Id, "T_OTHER", child.AppId, "U_AUTH", "xoxb-secret");
        Assert.Equal(SlackOAuthAuthorizationStatus.Rejected, mismatch.Status);
        Assert.Empty(sink.Tokens);
        Assert.Equal(string.Empty, await BotUserIdAsync(child.Id));

        var accepted = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_AUTH", "xoxb-secret");
        var replay = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_AUTH", "xoxb-secret");

        Assert.Equal(SlackOAuthAuthorizationStatus.Accepted, accepted.Status);
        Assert.Equal(SlackOAuthAuthorizationStatus.AlreadyApplied, replay.Status);
        Assert.Single(sink.Tokens);
        await using var db = _factory.CreateDbContext();
        var row = await db.ManagedSlackChildApps.SingleAsync(item => item.Id == child.Id);
        Assert.Equal($"slack-child:{child.Id}:bot-token", row.BotTokenRef);
        Assert.DoesNotContain("xoxb-secret", JsonSerializer.Serialize(row), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verified_child_fact_binds_connection_idempotently_and_preserves_diagnostics()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND",
            botUserId: "U_BIND");
        var connections = new AgentConnectionStore(
            _factory,
            new AgentQuerier(_factory),
            new FakeSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _time);
        var binding = new SlackChildAppBindingService(_factory, connections, _time);

        var first = await binding.ReconcileAsync(child.Id);
        var replay = await binding.ReconcileAsync(child.Id);
        Assert.Equal(SlackChildAppBindingStatus.Bound, first.Status);
        Assert.Equal(SlackChildAppBindingStatus.Bound, replay.Status);
        Assert.Equal(("A_BIND", "U_BIND"), await ConnectionIdentityAsync(child.AgentConnectionId));

        await SoftDeleteConnectionAsync(child.AgentConnectionId);
        var deleted = await binding.ReconcileAsync(child.Id);
        Assert.Equal(SlackChildAppBindingStatus.ConnectionDeleted, deleted.Status);
        Assert.Equal(SlackChildAppBindingState.ConnectionDeleted, await BindingStateAsync(child.Id));
    }

    [Fact]
    public async Task Database_constraints_protect_active_enrollment_child_app_and_staged_binding()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_UNIQUE", botUserId: "U_UNIQUE");
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = "enrollment-duplicate",
            WorkspaceTeamId = child.WorkspaceTeamId,
            ManagerExternalId = "manager-2",
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            ManagerCredentialRef = "credential-ref",
            AuditJson = "[]",
            CreatedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var staged = _factory.CreateDbContext();
        staged.AgentConnections.Add(new AgentConnectionRow
        {
            Id = "half-bound",
            ProjectId = child.WorkspaceTeamId,
            AgentId = "agent-half",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = child.WorkspaceTeamId,
            AppId = "A_HALF",
            BotUserId = string.Empty,
            BotName = "Half",
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Unknown,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => staged.SaveChangesAsync());
    }

    [Fact]
    public async Task Staged_connection_creation_is_internal_and_does_not_change_existing_api_contract()
    {
        await using var db = _factory.CreateDbContext();
        db.Agents.Add(new AgentRow
        {
            Id = "agent-api",
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent { Id = "agent-api", ProjectId = "project-api", Name = "api", Status = AgentStatus.Active }),
        });
        await db.SaveChangesAsync();
        var store = new AgentConnectionStore(
            _factory,
            new AgentQuerier(_factory),
            new FakeSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _time);

        var existingPath = await store.CreateAsync(new AgentConnection
        {
            Id = "connection-api",
            ProjectId = "project-api",
            AgentId = "agent-api",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = string.Empty,
        });
        Assert.Equal(string.Empty, existingPath.WorkspaceTeamId);

        await Assert.ThrowsAsync<AgentConnectionValidationException>(() => store.CreateStagedAsync(new AgentConnection
        {
            Id = "connection-staged-missing-team",
            ProjectId = "project-api",
            AgentId = "agent-api",
            ProviderKind = ConnectionProviderKind.Slack,
        }));
    }

    private async Task<ManagedSlackChildAppRow> SeedChildAsync(
        string lifecycle,
        string authorization = SlackAuthorizationState.NotStarted,
        string appId = "",
        string botUserId = "")
    {
        var suffix = Guid.NewGuid().ToString("N");
        var enrollmentId = $"enrollment-{suffix}";
        var connectionId = $"connection-{suffix}";
        var childId = $"child-{suffix}";
        var now = _time.GetUtcNow();
        await using var db = _factory.CreateDbContext();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = $"T-{suffix}",
            ManagerExternalId = "manager-1",
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "pro",
            ManagedAppLimit = 10,
            ManagerCredentialRef = "credential-ref",
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = connectionId,
            ProjectId = $"project-{suffix}",
            AgentId = $"agent-{suffix}",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = $"T-{suffix}",
            AppId = string.Empty,
            BotUserId = string.Empty,
            BotName = "Test Bot",
            SetupProgress = SetupProgressKind.CreateAppCredentials,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Unknown,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var row = new ManagedSlackChildAppRow
        {
            Id = childId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = $"T-{suffix}",
            AgentConnectionId = connectionId,
            AppId = appId,
            BotUserId = botUserId,
            AppLifecycle = lifecycle,
            Authorization = authorization,
            TransportKind = SlackTransportKind.Socket,
            DesiredManifestVersion = 2,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            OperationFence = 0,
            ClientSecretRef = "client-secret-ref",
            SigningSecretRef = "signing-secret-ref",
            AppLevelTokenRef = "app-token-ref",
            BotTokenRef = string.IsNullOrEmpty(botUserId) ? string.Empty : "bot-token-ref",
            BindingState = SlackChildAppBindingState.Pending,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ManagedSlackChildApps.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task SoftDeleteConnectionAsync(string connectionId)
    {
        await using var db = _factory.CreateDbContext();
        var row = await db.AgentConnections.SingleAsync(item => item.Id == connectionId);
        row.DeletedAt = _time.GetUtcNow();
        row.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    private async Task<string> LifecycleAsync(string childId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ManagedSlackChildApps.Where(item => item.Id == childId).Select(item => item.AppLifecycle).SingleAsync();
    }

    private async Task<string> BotUserIdAsync(string childId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ManagedSlackChildApps.Where(item => item.Id == childId).Select(item => item.BotUserId).SingleAsync();
    }

    private async Task<string> BindingStateAsync(string childId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ManagedSlackChildApps.Where(item => item.Id == childId).Select(item => item.BindingState).SingleAsync();
    }

    private async Task<(string AppId, string BotUserId)> ConnectionIdentityAsync(string connectionId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentConnections.Where(item => item.Id == connectionId)
            .Select(item => new ValueTuple<string, string>(item.AppId, item.BotUserId)).SingleAsync();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
