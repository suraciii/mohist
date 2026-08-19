using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Api;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// Web login (docs/auth.md「Web UI：令牌登录」): an operator-level token
/// exchanges for a 7-day HttpOnly session cookie, the probe answers 200
/// while the session lives, and logout revokes it server-side so the
/// same cookie answers 401 afterwards.
/// </summary>
public sealed class AuthSessionSpecs(MohistIntegrationFixture fixture)
{
    private const string SessionCookieName = "mohist_session";

    [Fact]
    public async Task ValidAdminToken_ExchangesForASessionCookie_AndAccessesBusinessApi()
    {
        using var client = fixture.CreateClient();

        using var response = await ExchangeAsync(client, MohistIntegrationFixture.AdminToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessionToken = AssertSingleSessionCookie(response, out var setCookie);

        // The presented admin token appears nowhere in the response; the
        // cookie carries a fresh session-shaped token instead.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(MohistIntegrationFixture.AdminToken, body);
        Assert.DoesNotContain(MohistIntegrationFixture.AdminToken, setCookie);
        Assert.StartsWith($"mohist_session=moh_session_", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);

        var row = await LoadCredentialRowAsync(sessionToken);
        Assert.Equal(CredentialKind.Session.ToString(), row.Kind);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, row.PrincipalId);
        Assert.Equal("""["operator"]""", row.ScopesJson);
        Assert.Equal(fixture.TimeProvider.GetUtcNow() + AuthSessionRoutes.SessionLifetime, row.ExpiresAt);
        Assert.Null(row.RevokedAt);

        using var business = await SendWithSessionCookieAsync(client, HttpMethod.Get, "/api/projects", sessionToken);
        Assert.Equal(HttpStatusCode.OK, business.StatusCode);
    }

    [Fact]
    public async Task OperatorPat_ExchangesForASession()
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        await InsertCredentialRowAsync("cred_spec_operator_pat", token, """["operator"]""");

        using var client = fixture.CreateClient();
        using var response = await ExchangeAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSingleSessionCookie(response, out _);
    }

    [Fact]
    public async Task ReadonlyPat_CannotExchangeForASession()
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        await InsertCredentialRowAsync("cred_spec_readonly_pat", token, """["readonly"]""");

        using var client = fixture.CreateClient();
        using var response = await ExchangeAsync(client, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task InvalidToken_FailsWith401_AndSetsNoCookie()
    {
        using var client = fixture.CreateClient();

        using var response = await ExchangeAsync(client, "not-a-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task SessionProbe_Answers401_WithoutASession()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheSession_AndTheSameCookieFailsAfterwards()
    {
        using var client = fixture.CreateClient();
        var sessionToken = AssertSingleSessionCookie(
            await ExchangeAsync(client, MohistIntegrationFixture.AdminToken), out _);

        using var probe = await SendWithSessionCookieAsync(client, HttpMethod.Get, "/api/auth/session", sessionToken);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        using var logout = await SendWithSessionCookieAsync(client, HttpMethod.Delete, "/api/auth/session", sessionToken);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var row = await LoadCredentialRowAsync(sessionToken);
        Assert.NotNull(row.RevokedAt);

        using var afterLogout = await SendWithSessionCookieAsync(client, HttpMethod.Get, "/api/auth/session", sessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string token) =>
        await client.PostAsync(
            "/api/auth/session",
            new StringContent(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json"));

    private static async Task<HttpResponseMessage> SendWithSessionCookieAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string sessionToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{SessionCookieName}={sessionToken}");
        return await client.SendAsync(request);
    }

    private static string AssertSingleSessionCookie(HttpResponseMessage response, out string rawCookie)
    {
        rawCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var value = rawCookie.Split(';')[0];
        Assert.StartsWith($"{SessionCookieName}=", value);
        return value[(SessionCookieName.Length + 1)..];
    }

    private async Task<CredentialRow> LoadCredentialRowAsync(string sessionToken)
    {
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Credentials.SingleAsync(row => row.TokenHash == CredentialToken.Hash(sessionToken));
    }

    private async Task InsertCredentialRowAsync(string id, string token, string scopesJson)
    {
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Credentials.Add(new CredentialRow
        {
            Id = id,
            PrincipalId = MohistPrincipal.AdminPrincipalId,
            Kind = CredentialKind.Pat.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = scopesJson,
            Name = id,
            ExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            RevokedAt = null,
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
    }
}
