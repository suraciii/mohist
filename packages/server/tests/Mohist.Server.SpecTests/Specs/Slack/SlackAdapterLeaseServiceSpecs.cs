using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
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
        secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-runtime");
        var service = new SlackAdapterLeaseService(
            new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options)), provider, secrets, clock);

        Assert.True(Assert.Single(await service.DiscoverAsync("operator-1")).CanAcquireValidation);

        var validation = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(validation);
        Assert.Equal("xapp-candidate", validation!.AppToken);
        Assert.False(string.IsNullOrEmpty(validation.AppToken));

        Assert.Equal(SlackHelloOutcome.Verified,
            await service.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));

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
        secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");
        var service = new SlackAdapterLeaseService(
            new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options)), provider, secrets, clock);

        var validation = await service.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await service.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "WRONG"));
        Assert.Equal(SlackHelloOutcome.Verified,
            await service.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));
    }

    private static SlackLeaseTarget Target(
        SlackLeaseTargetRef @ref, string appId, bool active, bool appToken, bool botToken, bool verified, FakeSecretResolver _) =>
        new(@ref, appId, active, appToken, botToken, verified,
            SecretStoreAddressFor(@ref, SecretKind.AppToken),
            SecretStoreAddressFor(@ref, SecretKind.BotToken));

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
