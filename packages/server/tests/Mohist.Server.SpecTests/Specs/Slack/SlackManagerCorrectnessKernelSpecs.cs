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
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackManagerCorrectnessKernelSpecs : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(TestTime.UtcNow);
    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private FakeSlackAppManagementPort _apps = null!;
    private readonly FakeSecretStore _secrets = new();
    private ManagedSlackAgentAppApplicationService _appService = null!;

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _apps = new FakeSlackAppManagementPort();
        _appService = new ManagedSlackAgentAppApplicationService(_factory, new AgentQuerier(_factory), _apps, _apps, new SlackManifestGenerator(), _secrets, _time);
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

        Assert.Equal(1, results.Count(result => result.Status == ManagedSlackAgentAppOperationStatus.Completed));
        Assert.Contains(results, result => result.Status is ManagedSlackAgentAppOperationStatus.Concurrent or ManagedSlackAgentAppOperationStatus.NotAllowed);
        Assert.Equal(1, _apps.CreateCalls);
        await using var db = _factory.CreateDbContext();
        var row = await db.ManagedSlackAgentApps.SingleAsync(item => item.Id == child.Id);
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

        Assert.Equal(ManagedSlackAgentAppOperationStatus.Completed, create.Status);
        Assert.Equal(SlackAppManagementOutcome.Unknown, create.Outcome);
        Assert.Equal(ManagedSlackAgentAppOperationStatus.NotAllowed, retry.Status);
        Assert.Equal(1, _apps.CreateCalls);
        Assert.Equal(SlackAppLifecycle.CreateUnknown, await LifecycleAsync(child.Id));

        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(SlackAppManagementFactOutcome.Present, "A_FACT")));
        var reconciled = await _appService.ReconcileCreateAsync(child.Id);

        Assert.Equal(ManagedSlackAgentAppOperationStatus.Reconciled, reconciled.Status);
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

        var delete = await _appService.DeleteAsync(child.Id, "DELETE");
        var retry = await _appService.DeleteAsync(child.Id, "DELETE");

        Assert.Equal(ManagedSlackAgentAppOperationStatus.Completed, delete.Status);
        Assert.Equal(SlackAppManagementOutcome.Unknown, delete.Outcome);
        Assert.Equal(ManagedSlackAgentAppOperationStatus.NotAllowed, retry.Status);
        Assert.Equal(1, _apps.DeleteCalls);
        Assert.Equal(SlackAppLifecycle.DeleteUnknown, await LifecycleAsync(child.Id));

        _apps.SetResponse(child.Id, new FakeSlackAppResponse(
            Inspect: new SlackAppManagementFact(SlackAppManagementFactOutcome.Absent)));
        var reconciled = await _appService.ReconcileDeleteAsync(child.Id);

        Assert.Equal(ManagedSlackAgentAppOperationStatus.Reconciled, reconciled.Status);
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
        var blocked = await _appService.DeleteAsync(active.Id, "DELETE");
        var unconfirmed = await _appService.DeleteAsync(active.Id, "delete");

        Assert.Equal("active_connection_binding", blocked.ErrorClass);
        Assert.Equal("confirmation_required", unconfirmed.ErrorClass);
        Assert.Equal(0, _apps.DeleteCalls);

        await SoftDeleteConnectionAsync(active.AgentConnectionId);
        _apps.SetResponse(active.Id, new FakeSlackAppResponse(
            Delete: new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded)));
        var deleted = await _appService.DeleteAsync(active.Id, "DELETE");

        Assert.Equal(ManagedSlackAgentAppOperationStatus.Completed, deleted.Status);
        Assert.Equal(SlackAppManagementOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(SlackAppLifecycle.Deleted, await LifecycleAsync(active.Id));
        await using var db = _factory.CreateDbContext();
        var audit = await db.ManagedSlackAgentApps.Where(row => row.Id == active.Id).Select(row => row.AuditJson).SingleAsync();
        Assert.Contains("permanent_delete", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-1", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_axes_derive_transport_readiness_manifest_drift_and_one_next_action()
    {
        var child = new ManagedSlackAgentApp
        {
            AppLifecycle = SlackAppLifecycle.NotCreated,
            Authorization = SlackAuthorizationState.NotStarted,
        };
        Assert.Equal(SlackAgentAppNextAction.CreateAgentApp, child.NextAction);

        child.AppLifecycle = SlackAppLifecycle.CreateUnknown;
        Assert.Equal(SlackAgentAppNextAction.ReconcileCreate, child.NextAction);

        child.AppLifecycle = SlackAppLifecycle.Created;
        child.Authorization = SlackAuthorizationState.Authorized;
        child.DesiredManifestVersion = 2;
        child.DesiredManifestHash = "desired";
        child.AppliedManifestVersion = 1;
        child.AppliedManifestHash = "old";
        child.BindingState = SlackAgentAppBindingState.Bound;
        child.AppLevelTokenRef = "app-token-ref";
        child.BotTokenRef = "bot-token-ref";

        var status = ManagedSlackAgentAppStatusDeriver.Derive(child);
        Assert.Equal(SlackManifestState.DriftKnown, status.ManifestState);
        Assert.Equal(SlackTransportReadiness.Ready, status.TransportReadiness);
        Assert.Equal(SlackAgentAppNextAction.ApplyManifest, status.NextAction);

        child.AppliedManifestVersion = 2;
        child.AppliedManifestHash = "desired";
        status = ManagedSlackAgentAppStatusDeriver.Derive(child);
        Assert.Equal(SlackAgentAppNextAction.Ready, status.NextAction);

        child.AppLevelTokenRef = string.Empty;
        Assert.Equal(SlackTransportReadiness.NotReady, ManagedSlackAgentAppStatusDeriver.DeriveTransportReadiness(child));
        Assert.Equal(SlackAgentAppNextAction.ConfigureSocketCredentials, child.NextAction);

        child.BotTokenRef = "bot-ref";
        Assert.Equal(SlackTransportReadiness.NotReady, ManagedSlackAgentAppStatusDeriver.DeriveTransportReadiness(child));
    }

    [Fact]
    public async Task Verified_child_fact_binds_connection_idempotently_and_preserves_diagnostics()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND",
            botUserId: "U_BIND",
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Verified);
        var connections = new AgentConnectionStore(
            _factory,
            new AgentQuerier(_factory),
            new FakeSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);

        var first = await binding.ReconcileAsync(child.Id);
        var replay = await binding.ReconcileAsync(child.Id);
        Assert.Equal(SlackAgentAppBindingStatus.Bound, first.Status);
        Assert.Equal(SlackAgentAppBindingStatus.Bound, replay.Status);
        Assert.Equal(("A_BIND", "U_BIND"), await ConnectionIdentityAsync(child.AgentConnectionId));

        await SoftDeleteConnectionAsync(child.AgentConnectionId);
        var deleted = await binding.ReconcileAsync(child.Id);
        Assert.Equal(SlackAgentAppBindingStatus.ConnectionDeleted, deleted.Status);
        Assert.Equal(SlackAgentAppBindingState.ConnectionDeleted, await BindingStateAsync(child.Id));
    }

    [Fact]
    public async Task Unverified_child_is_not_bound_before_socket_hello()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_UNVERIFIED",
            botUserId: "U_UNVERIFIED",
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Candidate);
        var connections = new AgentConnectionStore(
            _factory,
            new AgentQuerier(_factory),
            new FakeSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _time);
        var binding = new SlackAgentAppBindingService(_factory, connections, _time);

        var result = await binding.ReconcileAsync(child.Id);

        Assert.Equal(SlackAgentAppBindingStatus.NotVerified, result.Status);
        Assert.Equal(SlackAgentAppBindingState.Pending, await BindingStateAsync(child.Id));
        Assert.Equal((string.Empty, string.Empty), await ConnectionIdentityAsync(child.AgentConnectionId));
    }

    [Fact]
    public async Task Pending_binding_processing_orders_in_memory_for_sqlite_date_compatibility()
    {
        var oldest = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_OLDEST",
            botUserId: "U_OLDEST",
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Verified);
        var newest = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_NEWEST",
            botUserId: "U_NEWEST",
            runtimeCredentialValidationState: SlackRuntimeCredentialValidationState.Verified);
        await SeedBindingObligationAsync(oldest, SlackAgentAppBindingObligationStatus.Pending, _time.GetUtcNow().AddMinutes(-1));
        await SeedBindingObligationAsync(newest, SlackAgentAppBindingObligationStatus.Pending, _time.GetUtcNow());

        var port = new RecordingBindingPort();
        var binding = new SlackAgentAppBindingService(_factory, port, _time);

        var results = await binding.ProcessPendingAsync();

        Assert.Equal([oldest.AgentConnectionId, newest.AgentConnectionId], port.ConnectionIds);
        Assert.All(results, result => Assert.Equal(SlackAgentAppBindingStatus.Bound, result.Status));
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

    private async Task<ManagedSlackAgentAppRow> SeedChildAsync(
        string lifecycle,
        string authorization = SlackAuthorizationState.NotStarted,
        string appId = "",
        string botUserId = "",
        string bindingState = SlackAgentAppBindingState.Pending,
        string runtimeCredentialValidationState = SlackRuntimeCredentialValidationState.NotProvided)
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
        var row = new ManagedSlackAgentAppRow
        {
            Id = childId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = $"T-{suffix}",
            AgentConnectionId = connectionId,
            AppId = appId,
            BotUserId = botUserId,
            AppLifecycle = lifecycle,
            Authorization = authorization,
            RuntimeCredentialValidationState = runtimeCredentialValidationState,
            DesiredManifestVersion = 2,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            OperationFence = 0,
            ClientSecretRef = "client-secret-ref",
            SigningSecretRef = "signing-secret-ref",
            AppLevelTokenRef = "app-token-ref",
            BotTokenRef = string.IsNullOrEmpty(botUserId) ? string.Empty : "bot-token-ref",
            BindingState = bindingState,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ManagedSlackAgentApps.Add(row);
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

    private async Task SeedBindingObligationAsync(
        ManagedSlackAgentAppRow child,
        string status,
        DateTimeOffset? updatedAt = null)
    {
        await using var db = _factory.CreateDbContext();
        var now = updatedAt ?? _time.GetUtcNow();
        db.SlackAgentAppBindingObligations.Add(new SlackAgentAppBindingObligationRow
        {
            Id = $"obligation-{Guid.NewGuid():N}",
            AgentAppId = child.Id,
            AgentConnectionId = child.AgentConnectionId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> LifecycleAsync(string childId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ManagedSlackAgentApps.Where(item => item.Id == childId).Select(item => item.AppLifecycle).SingleAsync();
    }

    private async Task<string> BindingStateAsync(string childId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ManagedSlackAgentApps.Where(item => item.Id == childId).Select(item => item.BindingState).SingleAsync();
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

    private sealed class RecordingBindingPort : ISlackAgentAppBindingPort
    {
        public List<string> ConnectionIds { get; } = [];

        public Task<AgentConnection?> BindSlackIdentityAsync(
            string projectId,
            string id,
            string workspaceTeamId,
            string appId,
            string botUserId,
            string? botName,
            CancellationToken ct = default,
            string? claimToken = null)
        {
            ConnectionIds.Add(id);
            return Task.FromResult<AgentConnection?>(new AgentConnection { Id = id });
        }
    }
}
