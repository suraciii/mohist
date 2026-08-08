using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// RFC 8628 device authorization (docs/auth.md "远程 CLI：设备授权登录",
/// design/auth.md "CLI 设备授权"): the CLI mints a flow, the logged-in
/// Web session resolves the user code and approves, the CLI polls for
/// an access + refresh pair, refresh rotates with family revocation on
/// replay, and logout revokes the chain. Verify/decision require a Web
/// session; polling and code guessing are rate-limited per source.
/// </summary>
[Collection("IntegrationMisc")]
public sealed class DeviceFlowSpecs(MohistIntegrationFixture fixture)
{
    private const string SessionCookieName = "mohist_session";
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string AnyUserCode = "ABCDEFGH";

    [Fact]
    public async Task FullFlow_ApprovesOnTheWebSession_AndThePolledTokensWork()
    {
        var source = SourceFor(1);
        var (flow, _) = await CreateFlowAsync(source, "cli-host");

        Assert.Contains("/device?user_code=", flow.VerificationUriComplete, StringComparison.Ordinal);
        Assert.Equal(5, flow.Interval);
        Assert.Equal(600, flow.ExpiresIn);

        using var session = await NewWebSessionAsync();

        using var pending = await PollAsync(source, flow.DeviceCode);
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        Assert.Equal("authorization_pending", CodeOf(pending));

        // Hyphens and case are ignored (XXXX-XXXX grouped form).
        using var verify = await VerifyAsync(session, $"  {flow.UserCode.ToLowerInvariant()}-", source);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verifyData = DataOf(verify);
        var flowId = verifyData.GetProperty("flowId").GetString()!;
        Assert.Equal("cli-host", verifyData.GetProperty("clientName").GetString());
        Assert.Equal(fixture.TimeProvider.GetUtcNow() + DeviceFlowPolicy.FlowTtl, verifyData.GetProperty("expiresAt").GetDateTimeOffset());

        using var approve = await DecideAsync(session, flowId, "approved", source);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        using var tokenResponse = await PollAsync(source, flow.DeviceCode);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = DataOf(tokenResponse);
        var accessToken = tokens.GetProperty("accessToken").GetString()!;
        var refreshToken = tokens.GetProperty("refreshToken").GetString()!;
        Assert.StartsWith("moh_session_", accessToken, StringComparison.Ordinal);
        Assert.StartsWith("moh_refresh_", refreshToken, StringComparison.Ordinal);
        Assert.Equal(
            fixture.TimeProvider.GetUtcNow() + DeviceFlowPolicy.AccessTtl,
            tokens.GetProperty("accessExpiresAt").GetDateTimeOffset());
        Assert.Equal(
            fixture.TimeProvider.GetUtcNow() + DeviceFlowPolicy.RefreshTtl,
            tokens.GetProperty("refreshExpiresAt").GetDateTimeOffset());

        // The access token is a live credential on the business API.
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var projects = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, projects.StatusCode);

        // A second poll for the same device code is a terminal error.
        using var replayed = await PollAsync(source, flow.DeviceCode);
        Assert.Equal("invalid_grant", CodeOf(replayed));

        // DB shape: one session-kind access (1h) + one refresh-kind (30d),
        // both operator-scoped and anchored to the flow as family.
        var rows = await LoadCredentialRowsAsync(flowId);
        Assert.Equal(2, rows.Count);
        var accessRow = rows.Single(row => row.Kind == CredentialKind.Session.ToString());
        var refreshRow = rows.Single(row => row.Kind == CredentialKind.Refresh.ToString());
        Assert.Equal(flowId, accessRow.FamilyId);
        Assert.Equal(flowId, refreshRow.FamilyId);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, accessRow.PrincipalId);
        Assert.Equal("""["operator"]""", accessRow.ScopesJson);
        Assert.Equal(fixture.TimeProvider.GetUtcNow() + DeviceFlowPolicy.AccessTtl, accessRow.ExpiresAt);
        Assert.Equal(fixture.TimeProvider.GetUtcNow() + DeviceFlowPolicy.RefreshTtl, refreshRow.ExpiresAt);
    }

    [Fact]
    public async Task Refresh_RollsThePairForward_AndKillsTheOldAccess()
    {
        var source = SourceFor(2);
        var (flow, flowId) = await CreateApprovedFlowAsync(source);
        var first = DataOf(await PollAsync(source, flow.DeviceCode));
        var oldAccess = first.GetProperty("accessToken").GetString()!;
        var refresh = first.GetProperty("refreshToken").GetString()!;

        using var rotated = await RefreshAsync(source, refresh);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var rotatedData = DataOf(rotated);
        var newAccess = rotatedData.GetProperty("accessToken").GetString()!;
        var newRefresh = rotatedData.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(oldAccess, newAccess);
        Assert.NotEqual(refresh, newRefresh);

        // Old access keeps its 1h lifetime — rotation revokes only the
        // presented refresh; the new pair works.
        using var stale = fixture.CreateClient();
        stale.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldAccess);
        Assert.Equal(HttpStatusCode.OK, (await stale.GetAsync("/api/projects")).StatusCode);
        using var fresh = fixture.CreateClient();
        fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newAccess);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/projects")).StatusCode);

        // Exactly one live refresh remains in the family; both access
        // tokens stay valid until their 1h expiry (rotation revokes only
        // the presented refresh).
        var rows = await LoadCredentialRowsAsync(flowId);
        Assert.Equal(4, rows.Count);
        Assert.Equal(1, rows.Count(row => row.RevokedAt is null && row.Kind == CredentialKind.Refresh.ToString()));
        Assert.Equal(2, rows.Count(row => row.RevokedAt is null && row.Kind == CredentialKind.Session.ToString()));
        Assert.Equal(CredentialToken.Hash(newRefresh), rows.Single(row => row.RevokedAt is null && row.Kind == CredentialKind.Refresh.ToString()).TokenHash);
    }

    [Fact]
    public async Task RefreshReplay_RevokesTheWholeSessionChain()
    {
        var source = SourceFor(3);
        var (flow, flowId) = await CreateApprovedFlowAsync(source);
        var first = DataOf(await PollAsync(source, flow.DeviceCode));
        var rotated = DataOf(await RefreshAsync(source, first.GetProperty("refreshToken").GetString()!));
        var replayRefresh = first.GetProperty("refreshToken").GetString()!;
        var rotatedAccess = rotated.GetProperty("accessToken").GetString()!;

        // Replaying the rotated refresh is a leak: the entire family dies.
        using var replay = await RefreshAsync(source, replayRefresh);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", CodeOf(replay));

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotatedAccess);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);

        var rows = await LoadCredentialRowsAsync(flowId);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
    }

    [Fact]
    public async Task DeniedFlow_AnswersAccessDenied()
    {
        var source = SourceFor(4);
        var (flow, flowId) = await CreateFlowAsync(source, "cli-host");
        using var session = await NewWebSessionAsync();
        await DecideAsync(session, flowId, "denied", source);

        using var response = await PollAsync(source, flow.DeviceCode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("access_denied", CodeOf(response));
    }

    [Fact]
    public async Task ExpiredFlow_AnswersExpiredToken()
    {
        var source = SourceFor(5);
        var (flow, _) = await CreateFlowAsync(source, "cli-host");
        fixture.TimeProvider.Advance(DeviceFlowPolicy.FlowTtl + TimeSpan.FromSeconds(1));

        using var response = await PollAsync(source, flow.DeviceCode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("expired_token", CodeOf(response));
    }

    [Fact]
    public async Task Verify_RejectsUnknownCodes()
    {
        var source = SourceFor(6);
        using var session = await NewWebSessionAsync();

        using var missing = await VerifyAsync(session, "ZZZZZZZZ", source);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Verify_AndDecision_RequireALoggedInWebSession()
    {
        var source = SourceFor(7);
        await CreateFlowAsync(source, "cli-host");

        using var anonymous = fixture.CreateClient();
        using var verify = await PostAsync(anonymous, "/api/auth/device/verify", new { userCode = AnyUserCode }, source);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);

        using var decision = await PostAsync(anonymous, "/api/auth/device/decision", new { flowId = "device_flow_x", decision = "approved" }, source);
        Assert.Equal(HttpStatusCode.Unauthorized, decision.StatusCode);
    }

    [Fact]
    public async Task Decision_RejectsInvalidDecisions_AndUnknownFlows()
    {
        var source = SourceFor(8);
        using var session = await NewWebSessionAsync();

        using var invalid = await PostAsync(session, "/api/auth/device/decision", new { flowId = "device_flow_x", decision = "maybe" }, source);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var unknown = await PostAsync(session, "/api/auth/device/decision", new { flowId = "device_flow_x", decision = "approved" }, source);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Approving twice with the same decision is idempotent; flipping
        // an already-recorded decision conflicts.
        var (_, flowId) = await CreateFlowAsync(source, "cli-host");
        await DecideAsync(session, flowId, "approved", source);
        using var again = await DecideAsync(session, flowId, "approved", source);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var flip = await DecideAsync(session, flowId, "denied", source);
        Assert.Equal(HttpStatusCode.Conflict, flip.StatusCode);
    }

    [Fact]
    public async Task Polling_TooFrequent_AnswersSlowDown()
    {
        var source = SourceFor(9);
        var (flow, _) = await CreateFlowAsync(source, "cli-host");

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < DevicePollRateLimiter.LimitPerMinute + 1; attempt++)
        {
            last?.Dispose();
            last = await PollAsync(source, flow.DeviceCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("slow_down", CodeOf(last));
    }

    [Fact]
    public async Task UserCodeGuessing_IsRateLimited()
    {
        var source = SourceFor(10);
        using var session = await NewWebSessionAsync();

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < DeviceGuessRateLimiter.LimitPerMinute + 1; attempt++)
        {
            last?.Dispose();
            last = await VerifyAsync(session, AnyUserCode, source);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("rate_limited", CodeOf(last));
    }

    [Fact]
    public async Task Logout_RevokesTheFamily_ServerSide()
    {
        var source = SourceFor(11);
        var (flow, _) = await CreateApprovedFlowAsync(source);
        var tokens = DataOf(await PollAsync(source, flow.DeviceCode));
        var accessToken = tokens.GetProperty("accessToken").GetString()!;
        var refreshToken = tokens.GetProperty("refreshToken").GetString()!;

        using var logout = await PostAsync(
            fixture.CreateClient(), "/api/auth/logout", new { refreshToken }, SourceFor(12));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);

        using var rotated = await RefreshAsync(source, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, rotated.StatusCode);
        Assert.Equal("invalid_grant", CodeOf(rotated));

        // Logout is idempotent and never reveals whether a token was valid.
        using var again = await PostAsync(
            fixture.CreateClient(), "/api/auth/logout", new { refreshToken = "garbage" }, SourceFor(13));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    private async Task<(DeviceFlowResponse Flow, string FlowId)> CreateApprovedFlowAsync(string source)
    {
        var (flow, flowId) = await CreateFlowAsync(source, "cli-host");
        using var session = await NewWebSessionAsync();
        using var verify = await VerifyAsync(session, flow.UserCode, source);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verifiedFlowId = DataOf(verify).GetProperty("flowId").GetString()!;
        Assert.Equal(flowId, verifiedFlowId);
        await DecideAsync(session, flowId, "approved", source);
        return (flow, flowId);
    }

    private async Task<(DeviceFlowResponse Flow, string FlowId)> CreateFlowAsync(string source, string name)
    {
        using var response = await PostAsync(fixture.CreateClient(), "/api/auth/device/code", new { name }, source);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = DataOf(response);
        using var session = await NewWebSessionAsync();
        using var verify = await VerifyAsync(session, data.GetProperty("userCode").GetString()!, source);
        var flowId = DataOf(verify).GetProperty("flowId").GetString()!;
        return (new DeviceFlowResponse(
            data.GetProperty("deviceCode").GetString()!,
            data.GetProperty("userCode").GetString()!,
            data.GetProperty("verificationUri").GetString()!,
            data.GetProperty("verificationUriComplete").GetString()!,
            data.GetProperty("interval").GetInt32(),
            data.GetProperty("expiresIn").GetInt32()), flowId);
    }

    private Task<HttpResponseMessage> PollAsync(string source, string deviceCode) =>
        PostAsync(
            fixture.CreateClient(),
            "/api/auth/token",
            new { grant_type = DeviceCodeGrantType, device_code = deviceCode },
            source);

    private Task<HttpResponseMessage> RefreshAsync(string source, string refreshToken) =>
        PostAsync(
            fixture.CreateClient(),
            "/api/auth/token",
            new { grant_type = "refresh_token", refresh_token = refreshToken },
            source);

    private Task<HttpResponseMessage> VerifyAsync(HttpClient session, string userCode, string source) =>
        PostAsync(session, "/api/auth/device/verify", new { userCode }, source);

    private Task<HttpResponseMessage> DecideAsync(HttpClient session, string flowId, string decision, string source) =>
        PostAsync(session, "/api/auth/device/decision", new { flowId, decision }, source);

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        object body,
        string source)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Test-Remote-Address", source);
        return await client.SendAsync(request);
    }

    private async Task<HttpClient> NewWebSessionAsync()
    {
        using var exchange = await fixture.Client.PostAsJsonAsync("/api/auth/session", new
        {
            token = MohistIntegrationFixture.AdminToken,
        });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var cookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"));
        var token = cookie.Split(';')[0][(SessionCookieName.Length + 1)..];

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookieName}={token}");
        return client;
    }

    private static string SourceFor(int seed) => $"198.51.100.{seed}";

    private static JsonElement DataOf(HttpResponseMessage response) =>
        DataOfAsync(response).GetAwaiter().GetResult();

    private static async Task<JsonElement> DataOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static string? CodeOf(HttpResponseMessage response) =>
        CodeOfAsync(response).GetAwaiter().GetResult();

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private async Task<IReadOnlyList<CredentialRow>> LoadCredentialRowsAsync(string familyId)
    {
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Credentials
            .AsNoTracking()
            .Where(row => row.FamilyId == familyId)
            .ToListAsync();
    }

    private sealed record DeviceFlowResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string VerificationUriComplete,
        int Interval,
        int ExpiresIn);
}
