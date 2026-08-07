using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackAdapterLeaseServiceSpecs
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Validation_then_hello_then_runtime_then_renew_flows_through_the_durable_store()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T_WORKSPACE");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.CandidateAppToken, "xapp-candidate");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-runtime");
        var service = NewService(database, provider, secrets, clock, out _);

        Assert.True(Assert.Single(await service.DiscoverAsync("operator-1")).CanAcquireValidation);

        var validation = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-candidate", validation!.AppToken);
        Assert.False(string.IsNullOrEmpty(validation.AppToken));

        Assert.Equal(SlackHelloOutcome.Verified,
            await service.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));

        // The confirmed hello promotes the candidate to the runtime address; the
        // runtime lease then serves the promoted pair.
        secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");
        var runtime = await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(runtime);
        Assert.Equal("xapp-candidate", runtime!.AppToken);
        Assert.Equal("xoxb-runtime", runtime.BotToken);

        var renewed = await service.RenewLeaseAsync("operator-1", manager, runtime.LeaseId, "adapter-A");
        Assert.NotNull(renewed);
        Assert.True(renewed!.ExpiresAt >= runtime.ExpiresAt);

        var takeover = await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-B");
        Assert.NotNull(takeover);
        Assert.Null(await service.RenewLeaseAsync("operator-1", manager, runtime.LeaseId, "adapter-A"));
    }

    [Fact]
    public async Task Hello_mismatch_neither_verifies_nor_fences_the_validation_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.CandidateAppToken, "xapp-candidate");
        var service = NewService(database, provider, secrets, clock, out _);

        var validation = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await service.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "WRONG"));
        // The mismatch is delegated to the target provider (reject/restore) but
        // does not consume the validation lease, so a corrected hello on the
        // same lease can still proceed.
        Assert.Contains(manager.TargetKey, provider.RejectedTargets);
        Assert.Equal(SlackHelloOutcome.Verified,
            await service.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));
    }

    [Fact]
    public async Task Mismatch_hello_under_a_stale_lease_returns_NoLease_without_rejecting()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.CandidateAppToken, "xapp-candidate");
        var service = NewService(database, provider, secrets, clock, out _);

        var first = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        var second = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");

        // A stale lease id can no longer have opened the Socket, so its
        // mismatched hello must not trigger the rejection side effects.
        Assert.Equal(SlackHelloOutcome.NoLease,
            await service.ReportHelloAsync("operator-1", manager, first!.LeaseId, "WRONG"));
        Assert.Empty(provider.RejectedTargets);

        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await service.ReportHelloAsync("operator-1", manager, second!.LeaseId, "WRONG"));
        Assert.Contains(manager.TargetKey, provider.RejectedTargets);
    }

    [Fact]
    public async Task Candidate_resupply_fences_validation_renew_and_hello_without_rejecting_the_new_candidate()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.CandidateAppToken, "xapp-candidate-v1");
        var service = NewService(database, provider, secrets, clock, out _);

        var validation = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);

        // A resupply stages a new candidate token for the same App. The old
        // lease was issued against the previous generation: renewal and hello
        // must fail closed, and the old token's hello must neither verify nor
        // reject the new candidate.
        secrets.Put(manager, SecretKind.CandidateAppToken, "xapp-candidate-v2");

        Assert.Null(await service.RenewLeaseAsync("operator-1", manager, validation!.LeaseId, "adapter-A"));
        Assert.Equal(SlackHelloOutcome.NoLease,
            await service.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));
        Assert.Equal(SlackHelloOutcome.NoLease,
            await service.ReportHelloAsync("operator-1", manager, validation.LeaseId, "WRONG"));
        Assert.Empty(provider.RejectedTargets);

        // A fresh validation lease resolves the new candidate and verifies it.
        var resupplied = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(resupplied);
        Assert.Equal("xapp-candidate-v2", resupplied!.AppToken);
        Assert.Equal(SlackHelloOutcome.Verified,
            await service.ReportHelloAsync("operator-1", manager, resupplied.LeaseId, "A123"));
    }

    [Fact]
    public async Task Validation_acquire_without_a_staged_candidate_issues_no_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        // The state still exposes a candidate address, but the staged secret is
        // gone (crash between candidate cleanup and Verified). Acquire must
        // fail cleanly without leaving an inert active lease behind.
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        var service = NewService(database, provider, secrets, clock, out var store);

        Assert.Null(await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A"));
        Assert.Null(await store.GetActiveAsync(manager.TargetKey));
        Assert.Equal(0, await store.GetGenerationAsync(manager.TargetKey));
    }

    [Fact]
    public async Task Failed_runtime_acquire_keeps_the_previous_lease_holder_untouched()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live");
        var service = NewService(database, provider, secrets, clock, out var store);

        var incumbent = await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(incumbent);

        // A rotation closes Verified while a competing acquire races it: the
        // acquire must fail without superseding the incumbent's lease.
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        Assert.Null(await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-B"));

        var active = await store.GetActiveAsync(manager.TargetKey);
        Assert.NotNull(active);
        Assert.Equal(incumbent!.LeaseId, active!.LeaseId);
        Assert.Equal("adapter-A", active.AdapterId);

        // The rotation is restored: the incumbent can still renew its lease.
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        Assert.NotNull(await service.RenewLeaseAsync("operator-1", manager, incumbent.LeaseId, "adapter-A"));
    }

    [Fact]
    public async Task Runtime_acquire_with_unresolvable_secrets_issues_no_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live");
        var service = NewService(database, provider, secrets, clock, out var store);

        var incumbent = await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(incumbent);

        // The verified pair vanishes from the secret store: a competing
        // acquire fails loudly, but no lease was issued and the incumbent
        // keeps its lease.
        var emptySecrets = new FakeSecretResolver();
        var competing = new SlackAdapterLeaseService(store, provider, emptySecrets, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            competing.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-B"));

        var active = await store.GetActiveAsync(manager.TargetKey);
        Assert.Equal(incumbent!.LeaseId, active!.LeaseId);
        Assert.NotNull(await service.RenewLeaseAsync("operator-1", manager, incumbent.LeaseId, "adapter-A"));
    }

    [Fact]
    public async Task Runtime_renew_fails_closed_once_the_verified_pair_rotates()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live");
        var service = NewService(database, provider, secrets, clock, out _);

        var runtime = await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(runtime);

        // A completed rotation promoted a new verified pair to the runtime
        // addresses. The old lease pinned the previous pair, so the holder
        // can no longer renew with its stale tokens; it must re-acquire.
        secrets.Put(manager, SecretKind.AppToken, "xapp-live-v2");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live-v2");
        Assert.Null(await service.RenewLeaseAsync("operator-1", manager, runtime!.LeaseId, "adapter-A"));

        var reacquired = await service.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(reacquired);
        Assert.Equal("xapp-live-v2", reacquired!.AppToken);
        Assert.Equal("xoxb-live-v2", reacquired.BotToken);
    }

    [Fact]
    public async Task Runtime_renew_touches_the_connection_heartbeat_only_for_connection_targets()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var clock = new FakeTimeProvider(T0);
        var provider = new InMemorySlackLeaseTargetProvider();
        var secrets = new FakeSecretResolver();
        var factory = new TestDbContextFactory(database.Options);

        var connection = new SlackLeaseTargetRef.Connection("project_1", "connection_1");
        provider.Add(Target(connection, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        secrets.Put(connection, SecretKind.AppToken, "xapp-candidate");
        secrets.Put(connection, SecretKind.BotToken, "xoxb-runtime");
        await using (var db = factory.CreateDbContext())
        {
            db.AgentConnections.Add(new Mohist.Server.Infrastructure.Data.Agent.AgentConnectionRow
            {
                Id = "connection_1",
                ProjectId = "project_1",
                AgentId = "agent_1",
                ProviderKind = Mohist.Server.Agent.Domain.ConnectionProviderKind.Slack,
                WorkspaceTeamId = "T_WORKSPACE",
                AppId = "A123",
                BotUserId = "U123",
                SetupProgress = Mohist.Server.Agent.Domain.SetupProgressKind.Complete,
                DesiredState = Mohist.Server.Agent.Domain.DesiredStateKind.Enabled,
                ConnectionHealth = Mohist.Server.Agent.Domain.ConnectionHealthKind.Healthy,
                AgentReadiness = Mohist.Server.Agent.Domain.AgentReadinessKind.Ready,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            await db.SaveChangesAsync();
        }

        var service = NewService(database, provider, secrets, clock, out _);
        var runtime = await service.AcquireRuntimeLeaseAsync("operator-1", connection, "adapter-A");
        Assert.NotNull(runtime);
        Assert.Null(await ReadHeartbeatAsync(factory, "connection_1"));

        clock.Advance(TimeSpan.FromMinutes(1));
        var renewed = await service.RenewLeaseAsync("operator-1", connection, runtime!.LeaseId, "adapter-A");
        Assert.NotNull(renewed);
        Assert.Equal(T0 + TimeSpan.FromMinutes(1), await ReadHeartbeatAsync(factory, "connection_1"));
    }

    private static async Task<DateTimeOffset?> ReadHeartbeatAsync(TestDbContextFactory factory, string connectionId)
    {
        await using var db = factory.CreateDbContext();
        var row = await db.AgentConnections.SingleAsync(row => row.Id == connectionId);
        return row.LastHeartbeatAt;
    }

    private sealed class NoopSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(true);

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private static SlackAdapterLeaseService NewService(
        TestSqliteDatabase database,
        InMemorySlackLeaseTargetProvider provider,
        FakeSecretResolver secrets,
        FakeTimeProvider clock,
        out SlackAdapterLeaseStore store)
    {
        store = new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options));
        var connections = new AgentConnectionStore(
            new TestDbContextFactory(database.Options),
            new AgentQuerier(new TestDbContextFactory(database.Options)),
            new NoopSecretStore(),
            [],
            clock);
        return new SlackAdapterLeaseService(store, provider, secrets, clock, connections);
    }

    private static SlackLeaseTarget Target(
        SlackLeaseTargetRef @ref, string appId, bool active, bool appToken, bool botToken, bool verified, FakeSecretResolver _) =>
        new(@ref, appId, active, appToken, botToken, verified,
            SecretStoreAddressFor(@ref, SecretKind.AppToken),
            SecretStoreAddressFor(@ref, SecretKind.BotToken),
            CandidateAppLevelTokenAddress: verified
                ? null
                : SecretStoreAddressFor(@ref, SecretKind.CandidateAppToken));

    private static SecretStoreAddress SecretStoreAddressFor(SlackLeaseTargetRef @ref, SecretKind kind) =>
        @ref switch
        {
            SlackLeaseTargetRef.Manager manager =>
                SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, kind),
            SlackLeaseTargetRef.Connection connection =>
                SecretStoreAddress.ForAgentConnection(connection.ProjectId, connection.ConnectionId, kind),
            _ => throw new InvalidOperationException("Unsupported lease target ref.")
        };

    private sealed class FakeSecretResolver : ISlackLeaseSecretResolver
    {
        private readonly Dictionary<SecretStoreAddress, string> _values = new();

        public void Put(SlackLeaseTargetRef @ref, SecretKind kind, string token) =>
            _values[SecretStoreAddressFor(@ref, kind)] = token;

        public Task<string?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(address, out var token) ? token : null);
    }
}
