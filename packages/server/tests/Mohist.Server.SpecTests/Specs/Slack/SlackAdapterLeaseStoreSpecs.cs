using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackAdapterLeaseStoreSpecs
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);
    private const string Target = "manager:enr_1";

    [Fact]
    public async Task Issue_bumps_generation_and_supersedes_the_prior_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options));

        var first = await store.IssueAsync(Target, SlackLeaseKind.Runtime, "adapter-A", T0 + RuntimeTtl, T0, credentialFingerprint: null);
        var second = await store.IssueAsync(Target, SlackLeaseKind.Runtime, "adapter-B", T0 + RuntimeTtl, T0, credentialFingerprint: null);

        Assert.Equal(1, first.Generation);
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
    }

    [Fact]
    public async Task Renew_extends_the_active_lease_and_survives_a_store_reload()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = new SlackAdapterLeaseStore(factory);

        var issued = await store.IssueAsync(Target, SlackLeaseKind.Runtime, "adapter-A", T0 + RuntimeTtl, T0, credentialFingerprint: null);
        var renewed = await store.RenewAsync(Target, issued.LeaseId, "adapter-A", T0 + RuntimeTtl + Renewal, T0);

        Assert.NotNull(renewed);
        Assert.Equal(issued.LeaseId, renewed!.LeaseId);
        Assert.True(renewed.ExpiresAt > issued.ExpiresAt);

        var reloaded = new SlackAdapterLeaseStore(factory);
        var active = await reloaded.GetActiveAsync(Target);
        Assert.Equal(renewed.ExpiresAt, active!.ExpiresAt);
    }

    [Fact]
    public async Task Renew_rejects_a_superseded_wrong_adapter_or_expired_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options));

        var first = await store.IssueAsync(Target, SlackLeaseKind.Runtime, "adapter-A", T0 + RuntimeTtl, T0, credentialFingerprint: null);
        Assert.Null(await store.RenewAsync(Target, first.LeaseId, "adapter-wrong", T0 + RuntimeTtl + Renewal, T0));

        await store.IssueAsync(Target, SlackLeaseKind.Runtime, "adapter-B", T0 + RuntimeTtl, T0, credentialFingerprint: null);
        Assert.Null(await store.RenewAsync(Target, first.LeaseId, "adapter-A", T0 + RuntimeTtl + Renewal, T0));

        var live = await store.GetActiveAsync(Target);
        Assert.Null(await store.RenewAsync(Target, live!.LeaseId, "adapter-B", T0 + RuntimeTtl + Renewal, T0 + RuntimeTtl + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ConfirmHello_fences_the_validation_lease_and_bumps_generation()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options));

        var validation = await store.IssueAsync(Target, SlackLeaseKind.Validation, "adapter-A", T0 + ValidationTtl, T0, credentialFingerprint: null);
        Assert.True(await store.ConfirmHelloAsync(Target, validation.LeaseId, T0));
        Assert.Null(await store.GetActiveAsync(Target));
        Assert.Equal(2, await store.GetGenerationAsync(Target));

        Assert.False(await store.ConfirmHelloAsync(Target, validation.LeaseId, T0));
        Assert.Null(await store.RenewAsync(Target, validation.LeaseId, "adapter-A", T0 + ValidationTtl + Renewal, T0));
    }

    [Fact]
    public async Task ConfirmHello_rejects_a_runtime_lease_and_an_unknown_lease()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options));

        var runtime = await store.IssueAsync(Target, SlackLeaseKind.Runtime, "adapter-A", T0 + RuntimeTtl, T0, credentialFingerprint: null);
        Assert.False(await store.ConfirmHelloAsync(Target, runtime.LeaseId, T0));
        Assert.False(await store.ConfirmHelloAsync(Target, "unknown-lease", T0));
    }

    [Fact]
    public async Task Issue_pins_and_confirm_clears_the_credential_fingerprint()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = new SlackAdapterLeaseStore(factory);

        var issued = await store.IssueAsync(
            Target, SlackLeaseKind.Validation, "adapter-A", T0 + ValidationTtl, T0, "fp-candidate");
        Assert.Equal("fp-candidate", issued.CredentialFingerprint);
        Assert.Equal("fp-candidate", (await store.GetActiveAsync(Target))!.CredentialFingerprint);

        var renewed = await store.RenewAsync(Target, issued.LeaseId, "adapter-A", T0 + ValidationTtl + Renewal, T0);
        Assert.Equal("fp-candidate", renewed!.CredentialFingerprint);

        Assert.True(await store.ConfirmHelloAsync(Target, issued.LeaseId, T0));
        Assert.Null(await store.GetActiveAsync(Target));

        await using var db = factory.CreateDbContext();
        var row = await db.SlackAdapterLeases.AsNoTracking().SingleAsync();
        Assert.Null(row.CredentialFingerprint);
    }

    [Fact]
    public async Task GetActive_returns_null_when_no_lease_has_ever_been_issued()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = new SlackAdapterLeaseStore(new TestDbContextFactory(database.Options));

        Assert.Null(await store.GetActiveAsync(Target));
        Assert.Equal(0, await store.GetGenerationAsync(Target));
    }

    [Fact]
    public async Task Active_lease_columns_are_coherent_after_every_transition()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = new SlackAdapterLeaseStore(factory);

        await store.IssueAsync(Target, SlackLeaseKind.Validation, "adapter-A", T0 + ValidationTtl, T0, credentialFingerprint: null);
        await store.ConfirmHelloAsync(Target, (await store.GetActiveAsync(Target))!.LeaseId, T0);

        await using var db = factory.CreateDbContext();
        var row = await db.SlackAdapterLeases.AsNoTracking().SingleAsync();
        Assert.True((row.LeaseId is null) == (row.LeaseKind is null));
        Assert.True((row.LeaseId is null) == (row.AdapterId is null));
        Assert.True((row.LeaseId is null) == (row.ExpiresAt is null));
        Assert.True((row.LeaseId is null) == (row.CredentialFingerprint is null));
    }

    private static readonly TimeSpan ValidationTtl = SlackAdapterLeaseService.ValidationLeaseTtl;
    private static readonly TimeSpan RuntimeTtl = SlackAdapterLeaseService.RuntimeLeaseTtl;
    private static readonly TimeSpan Renewal = TimeSpan.FromMinutes(1);
}
