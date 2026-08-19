using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// Auth audit trail (design/auth.md「审计事件」): credential issuance
/// and revocation (PAT, runner), enrollment-token issuance and
/// consumption, device approval persistence and session establishment
/// are all queryable through <c>GET /api/audit/events</c> — with
/// subject, time and target, and never a token plaintext value.
/// </summary>
[Collection("IsolatedIntegration")]
public sealed class AuditEventSpecs(IsolatedMohistIntegrationFixture fixture)
{
    private const string AuditPath = "/api/audit/events";

    [Fact]
    public async Task PatIssuance_AndRevocation_AreRecorded_WithoutTokenPlaintext()
    {
        var (patId, patToken) = await CreatePatAsync("audit-pat");

        var afterIssuance = await GetEventsAsync();
        var issued = afterIssuance.Single(auditEvent =>
            auditEvent.GetProperty("targetId").GetString() == patId);
        Assert.Equal("credentialIssued", issued.GetProperty("eventType").GetString());
        Assert.Equal("service", issued.GetProperty("subjectId").GetString());
        Assert.Equal("credential", issued.GetProperty("targetKind").GetString());
        Assert.Equal("pat", issued.GetProperty("metadata").GetProperty("kind").GetString());
        Assert.Equal("audit-pat", issued.GetProperty("metadata").GetProperty("name").GetString());
        Assert.NotEqual(default, issued.GetProperty("occurredAt").GetDateTimeOffset());

        using var revoke = await fixture.Client.PostAsJsonAsync("/api/auth/tokens/audit-pat/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var afterRevocation = await GetEventsAsync();
        var revoked = afterRevocation.Single(auditEvent =>
            auditEvent.GetProperty("targetId").GetString() == patId
            && auditEvent.GetProperty("eventType").GetString() == "credentialRevoked");
        Assert.Equal("service", revoked.GetProperty("subjectId").GetString());
        Assert.Equal("pat", revoked.GetProperty("metadata").GetProperty("kind").GetString());

        var body = await ReadAuditRawAsync();
        Assert.DoesNotContain(patToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionEstablishment_AndLogout_AreRecorded()
    {
        using var client = fixture.CreateClient();
        using var exchange = await client.PostAsync(
            "/api/auth/session",
            new StringContent(JsonSerializer.Serialize(new { token = MohistIntegrationFixture.AdminToken }), Encoding.UTF8, "application/json"));
        exchange.EnsureSuccessStatusCode();
        var sessionToken = AssertSingleSessionCookie(exchange);
        var sessionCredentialId = (await LoadCredentialRowAsync(sessionToken)).Id;

        var afterLogin = await GetEventsAsync();
        var established = afterLogin.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "sessionEstablished"
            && auditEvent.GetProperty("targetId").GetString() == sessionCredentialId);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, established.GetProperty("subjectId").GetString());
        Assert.Equal("session", established.GetProperty("metadata").GetProperty("kind").GetString());

        using var logout = await SendWithSessionCookieAsync(client, HttpMethod.Delete, "/api/auth/session", sessionToken);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var afterLogout = await GetEventsAsync();
        var revoked = afterLogout.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "credentialRevoked"
            && auditEvent.GetProperty("metadata").GetProperty("kind").GetString() == "session"
            && auditEvent.GetProperty("targetId").GetString() == sessionCredentialId);
        Assert.Equal(MohistPrincipal.AdminPrincipalId, revoked.GetProperty("subjectId").GetString());

        var body = await ReadAuditRawAsync();
        Assert.DoesNotContain(sessionToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerEnrollment_Registration_AndRevocation_AreRecorded()
    {
        var enrollmentToken = await CreateEnrollmentTokenAsync();

        var afterIssuance = await GetEventsAsync();
        var issued = afterIssuance.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "enrollmentTokenIssued");
        Assert.Equal("service", issued.GetProperty("subjectId").GetString());
        Assert.Equal("enrollmentToken", issued.GetProperty("targetKind").GetString());
        Assert.Equal(CredentialToken.Hash(enrollmentToken), issued.GetProperty("targetId").GetString());

        using var register = await fixture.Client.PostAsJsonAsync("/api/runners/register", new
        {
            token = enrollmentToken,
            runnerId = "runner-audited",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var runnerCredential = JsonDocument.Parse(await register.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;

        var afterRegistration = await GetEventsAsync();
        var consumed = afterRegistration.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "enrollmentTokenConsumed");
        Assert.Equal(CredentialToken.Hash(enrollmentToken), consumed.GetProperty("targetId").GetString());
        Assert.Equal("runner-audited", consumed.GetProperty("metadata").GetProperty("runnerId").GetString());

        var runnerIssued = afterRegistration.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "credentialIssued"
            && auditEvent.GetProperty("metadata").GetProperty("kind").GetString() == "runner");
        Assert.Equal(MohistPrincipal.AdminPrincipalId, runnerIssued.GetProperty("subjectId").GetString());
        Assert.Equal("runner-audited", runnerIssued.GetProperty("metadata").GetProperty("name").GetString());

        using var revoke = await fixture.Client.DeleteAsync("/api/runners/runner-audited/credentials");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var afterRevocation = await GetEventsAsync();
        var revoked = afterRevocation.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "credentialRevoked"
            && auditEvent.GetProperty("metadata").GetProperty("kind").GetString() == "runner");
        Assert.Equal("runner-audited", revoked.GetProperty("targetId").GetString());
        Assert.Equal("service", revoked.GetProperty("subjectId").GetString());

        var body = await ReadAuditRawAsync();
        Assert.DoesNotContain(enrollmentToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(runnerCredential, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceApproval_IsPersistableThroughTheEmitEntry()
    {
        // The device-approval endpoint lands with #322; until then the
        // event type must still persist and round-trip through the same
        // store every other audit event uses.
        var store = fixture.Services.GetRequiredService<IAuthAuditEventStore>();
        await store.RecordAsync(AuthAuditEvent.DeviceApproved(
            MohistPrincipal.AdminPrincipalId, "device-1", fixture.TimeProvider.GetUtcNow()));

        var events = await GetEventsAsync();
        var approved = events.Single(auditEvent =>
            auditEvent.GetProperty("eventType").GetString() == "deviceApproved");
        Assert.Equal(MohistPrincipal.AdminPrincipalId, approved.GetProperty("subjectId").GetString());
        Assert.Equal("device-1", approved.GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task List_RequiresAuthentication()
    {
        using var anonymous = fixture.CreateClient();

        using var response = await anonymous.GetAsync(AuditPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithUnknownKind_IsRejected()
    {
        using var response = await fixture.Client.GetAsync($"{AuditPath}?kind=bogusKind");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<CredentialRow> LoadCredentialRowAsync(string sessionToken)
    {
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Credentials.SingleAsync(row => row.TokenHash == CredentialToken.Hash(sessionToken));
    }

    private async Task<(string Id, string Token)> CreatePatAsync(string name)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/auth/tokens", new
        {
            name,
            scope = "readonly",
        });
        response.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        return (data.GetProperty("id").GetString()!, data.GetProperty("token").GetString()!);
    }

    private async Task<string> CreateEnrollmentTokenAsync()
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/runners/enrollment-tokens", new { });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private async Task<JsonElement[]> GetEventsAsync(params string[] query)
    {
        var path = query.Length == 0 ? AuditPath : $"{AuditPath}?{string.Join("&", query)}";
        var data = await fixture.Client.GetDataAsync<JsonElement>(path);
        return data.GetProperty("events").EnumerateArray().ToArray();
    }

    private async Task<string> ReadAuditRawAsync()
    {
        using var response = await fixture.Client.GetAsync(AuditPath);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string? NameOf(JsonElement auditEvent) =>
        auditEvent.GetProperty("metadata").TryGetProperty("name", out var name)
            ? name.GetString()
            : null;

    private static string AssertSingleSessionCookie(HttpResponseMessage response)
    {
        var rawCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var value = rawCookie.Split(';')[0];
        Assert.StartsWith("mohist_session=", value);
        return value["mohist_session=".Length..];
    }

    private static async Task<HttpResponseMessage> SendWithSessionCookieAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string sessionToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"mohist_session={sessionToken}");
        return await client.SendAsync(request);
    }
}
