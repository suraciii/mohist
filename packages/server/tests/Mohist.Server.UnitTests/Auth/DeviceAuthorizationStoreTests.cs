using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class DeviceAuthorizationStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_FindByHashes_RoundTrips()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());

        await setup.Store.CreateAsync(flow);

        var byDevice = await setup.Store.FindByDeviceCodeHashAsync(flow.DeviceCodeHash);
        var byUser = await setup.Store.FindByUserCodeHashAsync(flow.UserCodeHash);
        Assert.NotNull(byDevice);
        Assert.NotNull(byUser);
        Assert.Equal(flow.Id, byDevice.Id);
        Assert.Equal(flow.Id, byUser.Id);
        Assert.Equal(DeviceFlowStatus.Pending, byDevice.Status);
        Assert.Equal(flow.ClientName, byDevice.ClientName);
        Assert.Equal(flow.ExpiresAt, byDevice.ExpiresAt);
        Assert.Null(byDevice.PrincipalId);
    }

    [Fact]
    public async Task FindByDeviceCodeHash_UnknownHash_ReturnsNull()
    {
        using var setup = CreateStore();

        Assert.Null(await setup.Store.FindByDeviceCodeHashAsync("missing"));
    }

    [Fact]
    public async Task Decide_RecordsThePrincipalAndDecision()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        var decidedAt = setup.Time.GetUtcNow().AddSeconds(1);

        var result = await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", decidedAt);

        Assert.Equal(DeviceDecisionStatus.Decided, result.Status);
        var stored = await setup.Store.FindByDeviceCodeHashAsync(flow.DeviceCodeHash);
        Assert.Equal(DeviceFlowStatus.Approved, stored!.Status);
        Assert.Equal("admin", stored.PrincipalId);
        Assert.Equal(decidedAt, stored.DecidedAt);
    }

    [Fact]
    public async Task Decide_RepeatedIdenticalDecision_IsIdempotent()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());

        var result = await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());

        Assert.Equal(DeviceDecisionStatus.AlreadyDecided, result.Status);
        Assert.Equal(DeviceFlowStatus.Approved, result.CurrentStatus);
    }

    [Fact]
    public async Task Decide_ConflictingDecision_ReportsAlreadyDecided()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());

        var result = await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Denied, "admin", setup.Time.GetUtcNow());

        Assert.Equal(DeviceDecisionStatus.AlreadyDecided, result.Status);
        Assert.Equal(DeviceFlowStatus.Approved, result.CurrentStatus);
    }

    [Fact]
    public async Task Decide_UnknownFlow_ReturnsNotFound()
    {
        using var setup = CreateStore();

        var result = await setup.Store.DecideAsync("missing", DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());

        Assert.Equal(DeviceDecisionStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task IssueDeviceTokens_ConsumesTheFlow_AndMintsAccessPlusRefreshInOneFamily()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());
        var now = setup.Time.GetUtcNow().AddSeconds(5);

        var result = await setup.Store.IssueDeviceTokensAsync(flow.Id, now);

        Assert.Equal(DeviceTokenIssueStatus.Issued, result.Status);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.StartsWith("moh_session_", result.AccessToken, StringComparison.Ordinal);
        Assert.StartsWith("moh_refresh_", result.RefreshToken, StringComparison.Ordinal);
        Assert.Equal(flow.Id, result.Access!.FamilyId);
        Assert.Equal(flow.Id, result.Refresh!.FamilyId);
        Assert.Equal(now + DeviceFlowPolicy.AccessTtl, result.Access.ExpiresAt);
        Assert.Equal(now + DeviceFlowPolicy.RefreshTtl, result.Refresh.ExpiresAt);
        Assert.Equal(CredentialToken.Hash(result.AccessToken), result.Access.TokenHash);
        Assert.Equal(CredentialToken.Hash(result.RefreshToken), result.Refresh.TokenHash);

        var stored = await setup.Store.FindByDeviceCodeHashAsync(flow.DeviceCodeHash);
        Assert.Equal(DeviceFlowStatus.Issued, stored!.Status);
    }

    [Fact]
    public async Task IssueDeviceTokens_SecondPoll_LosesTheRace()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());
        var now = setup.Time.GetUtcNow();
        await setup.Store.IssueDeviceTokensAsync(flow.Id, now);

        var second = await setup.Store.IssueDeviceTokensAsync(flow.Id, now);

        Assert.Equal(DeviceTokenIssueStatus.AlreadyIssued, second.Status);
        Assert.Null(second.AccessToken);
    }

    [Fact]
    public async Task IssueDeviceTokens_OnPendingOrDeniedFlow_ReportsThatStatus()
    {
        using var setup = CreateStore();
        var pending = NewFlow(setup.Time.GetUtcNow(), userCode: "ABCDEFGH");
        var denied = NewFlow(setup.Time.GetUtcNow(), userCode: "JKLMNPQR");
        await setup.Store.CreateAsync(pending);
        await setup.Store.CreateAsync(denied);
        await setup.Store.DecideAsync(denied.Id, DeviceFlowStatus.Denied, "admin", setup.Time.GetUtcNow());

        Assert.Equal(
            DeviceTokenIssueStatus.Pending,
            (await setup.Store.IssueDeviceTokensAsync(pending.Id, setup.Time.GetUtcNow())).Status);
        Assert.Equal(
            DeviceTokenIssueStatus.Denied,
            (await setup.Store.IssueDeviceTokensAsync(denied.Id, setup.Time.GetUtcNow())).Status);
    }

    [Fact]
    public async Task RotateRefresh_RevokesThePresentedToken_AndMintsTheNextPair()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());
        var now = setup.Time.GetUtcNow();
        var issued = await setup.Store.IssueDeviceTokensAsync(flow.Id, now);

        var rotated = await setup.Store.RotateRefreshAsync(
            CredentialToken.Hash(issued.RefreshToken!), now.AddHours(1));

        Assert.Equal(RefreshRotationStatus.Rotated, rotated.Status);
        Assert.NotEqual(issued.RefreshToken, rotated.RefreshToken);
        Assert.NotEqual(issued.AccessToken, rotated.AccessToken);
        Assert.Equal(flow.Id, rotated.Access!.FamilyId);
        Assert.Equal(now.AddHours(1) + DeviceFlowPolicy.AccessTtl, rotated.Access.ExpiresAt);

        // The old refresh is dead; the new pair is live and shares the
        // family anchor.
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(issued.RefreshToken!)));
        Assert.NotNull(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(rotated.RefreshToken!)));
        Assert.Equal(flow.Id, await setup.Store.FindFamilyIdByRefreshTokenAsync(CredentialToken.Hash(rotated.RefreshToken!)));
    }

    [Fact]
    public async Task RotateRefresh_ReplayOfARotatedRefresh_RevokesTheFamily()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());
        var issued = await setup.Store.IssueDeviceTokensAsync(flow.Id, setup.Time.GetUtcNow());
        var rotated = await setup.Store.RotateRefreshAsync(
            CredentialToken.Hash(issued.RefreshToken!), setup.Time.GetUtcNow().AddHours(1));

        var replay = await setup.Store.RotateRefreshAsync(
            CredentialToken.Hash(issued.RefreshToken!), setup.Time.GetUtcNow().AddHours(2));

        Assert.Equal(RefreshRotationStatus.ReplayDetected, replay.Status);
        // The replay revoked the family: the fresh pair from the winning
        // rotation is dead too, and so is the presented token.
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(rotated.RefreshToken!)));
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(rotated.AccessToken!)));
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(issued.RefreshToken!)));
    }

    [Fact]
    public async Task RotateRefresh_ExpiredRefresh_ReturnsExpired()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());
        var issued = await setup.Store.IssueDeviceTokensAsync(flow.Id, setup.Time.GetUtcNow());
        var beyond = setup.Time.GetUtcNow() + DeviceFlowPolicy.RefreshTtl + TimeSpan.FromDays(1);
        setup.Time.SetUtcNow(beyond);

        var rotated = await setup.Store.RotateRefreshAsync(CredentialToken.Hash(issued.RefreshToken!), beyond);

        Assert.Equal(RefreshRotationStatus.Expired, rotated.Status);
    }

    [Fact]
    public async Task RotateRefresh_UnknownHash_ReturnsNotFound()
    {
        using var setup = CreateStore();

        var rotated = await setup.Store.RotateRefreshAsync("missing", setup.Time.GetUtcNow());

        Assert.Equal(RefreshRotationStatus.NotFound, rotated.Status);
    }

    [Fact]
    public async Task RevokeFamily_KillsEveryActiveCredential_OfTheChain()
    {
        using var setup = CreateStore();
        var flow = NewFlow(setup.Time.GetUtcNow());
        await setup.Store.CreateAsync(flow);
        await setup.Store.DecideAsync(flow.Id, DeviceFlowStatus.Approved, "admin", setup.Time.GetUtcNow());
        var issued = await setup.Store.IssueDeviceTokensAsync(flow.Id, setup.Time.GetUtcNow());
        var rotated = await setup.Store.RotateRefreshAsync(
            CredentialToken.Hash(issued.RefreshToken!), setup.Time.GetUtcNow().AddHours(1));
        var revokedAt = setup.Time.GetUtcNow().AddHours(2);

        var revoked = await setup.Store.RevokeFamilyAsync(flow.Id, revokedAt);
        var again = await setup.Store.RevokeFamilyAsync(flow.Id, revokedAt);

        Assert.True(revoked);
        Assert.False(again);
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(rotated.RefreshToken!)));
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(rotated.AccessToken!)));
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(issued.RefreshToken!)));
        Assert.Null(await setup.Credentials.FindActiveAsync(CredentialToken.Hash(issued.AccessToken!)));
    }

    private static DeviceAuthorization NewFlow(DateTimeOffset now, string userCode = "ABCDEFGH") =>
        new(
            $"device_flow_{Guid.NewGuid():N}",
            CredentialToken.Hash($"moh_device_{Guid.NewGuid():N}"),
            CredentialToken.Hash(userCode),
            "cli-host",
            DeviceFlowStatus.Pending,
            PrincipalId: null,
            DecidedAt: null,
            now + DeviceFlowPolicy.FlowTtl,
            now);

    private static StoreSetup CreateStore()
    {
        var time = new FakeTimeProvider(Now);
        var connection = new SqliteConnection("Data Source=device-authorization-store-tests;Mode=Memory;Cache=Shared");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "Credentials" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Credentials" PRIMARY KEY,
                    "PrincipalId" TEXT NOT NULL,
                    "Kind" TEXT NOT NULL,
                    "TokenHash" TEXT NOT NULL,
                    "ScopesJson" TEXT NOT NULL,
                    "Name" TEXT NULL,
                    "Prefix" TEXT NULL,
                    "FamilyId" TEXT NULL,
                    "ExpiresAt" TEXT NULL,
                    "RevokedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ProjectId" TEXT NULL
                );
                CREATE UNIQUE INDEX "IX_Credentials_TokenHash" ON "Credentials" ("TokenHash");
                CREATE UNIQUE INDEX "IX_Credentials_PrincipalId_Name" ON "Credentials" ("PrincipalId", "Name") WHERE "RevokedAt" IS NULL;
                CREATE TABLE "DeviceAuthorizations" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_DeviceAuthorizations" PRIMARY KEY,
                    "DeviceCodeHash" TEXT NOT NULL,
                    "UserCodeHash" TEXT NOT NULL,
                    "ClientName" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "PrincipalId" TEXT NULL,
                    "DecidedAt" TEXT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_DeviceAuthorizations_DeviceCodeHash" ON "DeviceAuthorizations" ("DeviceCodeHash");
                CREATE UNIQUE INDEX "IX_DeviceAuthorizations_UserCodeHash" ON "DeviceAuthorizations" ("UserCodeHash");
                """;
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var store = new DeviceAuthorizationStore(dbFactory, time);
        var credentials = new CredentialStore(dbFactory, time);
        return new StoreSetup(store, credentials, time, connection, provider);
    }

    private sealed record StoreSetup(
        DeviceAuthorizationStore Store,
        CredentialStore Credentials,
        FakeTimeProvider Time,
        SqliteConnection Connection,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Connection.Dispose();
        }
    }
}
