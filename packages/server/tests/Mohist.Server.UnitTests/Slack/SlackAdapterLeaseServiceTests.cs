using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAdapterLeaseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Discover_lists_only_targets_with_an_app_token_and_leaks_no_secrets()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        provider
            .Add(Target(new SlackLeaseTargetRef.Manager("enr_1", "T1"), "A1", active: true,
                appToken: true, botToken: true, verified: false, secrets))
            .Add(Target(new SlackLeaseTargetRef.Manager("enr_2", "T2"), "A2", active: true,
                appToken: false, botToken: true, verified: false, secrets));

        var view = Assert.Single(await lease.DiscoverAsync("operator-1"));

        Assert.Equal(SlackLeaseTargetKind.Manager, view.Kind);
        Assert.Equal("enr_1", view.EnrollmentId);
        Assert.Equal("A1", view.ExpectedAppId);
        Assert.True(view.CanAcquireValidation);
        Assert.False(view.CanAcquireRuntime);
        var viewProperties = typeof(SlackLeaseTargetView).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("AppLevelTokenProvisioned", viewProperties);
        foreach (var forbidden in new[] { "AppToken", "BotToken", "AppLevelTokenAddress", "BotTokenAddress" })
            Assert.DoesNotContain(forbidden, viewProperties);
    }

    [Fact]
    public async Task AcquireValidationLease_returns_only_the_app_token_and_expected_app_id()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");

        var result = await lease.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");

        Assert.NotNull(result);
        Assert.Equal("A123", result!.ExpectedAppId);
        Assert.Equal("xapp-candidate", result.AppToken);
        Assert.NotEqual(default, result.ExpiresAt);
        var resultProperties = typeof(SlackValidationLeaseResult).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("AppToken", resultProperties);
        Assert.DoesNotContain("BotToken", resultProperties);
    }

    [Fact]
    public async Task AcquireValidationLease_is_refused_until_an_app_token_exists_and_after_verification()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");

        Assert.Null(await lease.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A"));

        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");
        var first = await lease.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(first);
        await lease.ReportHelloAsync("operator-1", manager, first!.LeaseId, "A123");

        var second = await lease.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        Assert.Null(second);
    }

    [Fact]
    public async Task ReportHello_verifies_on_match_and_fences_the_validation_lease()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-candidate");

        var validation = await lease.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");

        Assert.Equal(SlackHelloOutcome.AppIdMismatch,
            await lease.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "WRONG"));
        Assert.Equal(SlackHelloOutcome.Verified,
            await lease.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));
        Assert.Equal(SlackHelloOutcome.NoLease,
            await lease.ReportHelloAsync("operator-1", manager, validation.LeaseId, "A123"));
    }

    [Fact]
    public async Task AcquireRuntimeLease_requires_verified_hello_and_active_target()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: false, appToken: true, botToken: true, verified: false, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live");

        Assert.Null(await lease.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A"));

        var validation = await lease.AcquireValidationLeaseAsync("operator-1", manager, "adapter-A");
        await lease.ReportHelloAsync("operator-1", manager, validation!.LeaseId, "A123");

        Assert.Null(await lease.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A"));

        provider.Replace(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        var runtime = await lease.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A");
        Assert.NotNull(runtime);
        Assert.Equal("xapp-live", runtime!.AppToken);
        Assert.Equal("xoxb-live", runtime.BotToken);
    }

    [Fact]
    public async Task RenewLease_extends_active_lease_and_rejects_a_superseded_one()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live");

        var runtime = (await lease.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A"))!;
        var renewed = await lease.RenewLeaseAsync("operator-1", manager, runtime.LeaseId, "adapter-A");
        Assert.NotNull(renewed);
        Assert.Equal(runtime.LeaseId, renewed!.LeaseId);
        Assert.True(renewed.ExpiresAt >= runtime.ExpiresAt);

        var takeover = await lease.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-B");
        Assert.NotNull(takeover);
        Assert.Null(await lease.RenewLeaseAsync("operator-1", manager, runtime.LeaseId, "adapter-A"));
    }

    [Fact]
    public async Task RenewLease_rejects_expired_and_wrong_adapter()
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");
        provider.Add(Target(manager, "A123", active: true, appToken: true, botToken: true, verified: true, secrets));
        secrets.Put(manager, SecretKind.AppToken, "xapp-live");
        secrets.Put(manager, SecretKind.BotToken, "xoxb-live");

        var runtime = (await lease.AcquireRuntimeLeaseAsync("operator-1", manager, "adapter-A"))!;
        Assert.Null(await lease.RenewLeaseAsync("operator-1", manager, runtime.LeaseId, "adapter-wrong"));

        clock.SetUtcNow(Now + SlackAdapterLeaseService.RuntimeLeaseTtl + TimeSpan.FromSeconds(1));
        Assert.Null(await lease.RenewLeaseAsync("operator-1", manager, runtime.LeaseId, "adapter-A"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Every_entry_point_requires_a_non_empty_operator(string operatorId)
    {
        var lease = NewService(out var provider, out var secrets, out var clock);
        var manager = new SlackLeaseTargetRef.Manager("enr_1", "T1");

        await Assert.ThrowsAsync<ArgumentException>(() => lease.DiscoverAsync(operatorId));
        await Assert.ThrowsAsync<ArgumentException>(
            () => lease.AcquireValidationLeaseAsync(operatorId, manager, "adapter-A"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => lease.ReportHelloAsync(operatorId, manager, "lease-1", "A123"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => lease.AcquireRuntimeLeaseAsync(operatorId, manager, "adapter-A"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => lease.RenewLeaseAsync(operatorId, manager, "lease-1", "adapter-A"));
    }

    private static SlackAdapterLeaseService NewService(
        out InMemorySlackLeaseTargetProvider provider, out FakeSecretResolver secrets, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Now);
        provider = new InMemorySlackLeaseTargetProvider();
        secrets = new FakeSecretResolver();
        return new SlackAdapterLeaseService(
            new InMemorySlackLeaseStore(), provider, secrets, clock);
    }

    private static SlackLeaseTarget Target(
        SlackLeaseTargetRef @ref,
        string appId,
        bool active,
        bool appToken,
        bool botToken,
        bool verified,
        FakeSecretResolver secrets) =>
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

internal static class InMemorySlackLeaseTargetProviderExtensions
{
    public static InMemorySlackLeaseTargetProvider Replace(
        this InMemorySlackLeaseTargetProvider provider, SlackLeaseTarget target) => provider.Add(target);
}
