using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
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
/// P2 scope enforcement (docs/auth.md "Scope 判定"): routes declare
/// required scopes, credentials recorded at issuance (PAT #320) take
/// effect, insufficient scope answers 403 with the principal — clearly
/// distinct from the 401 of an unauthenticated request. Runner
/// credentials stay bound to their RunnerId: any path, hub query or
/// header self-declaring another runner is rejected at the auth layer.
/// </summary>
public sealed class ScopeAuthorizationSpecs(MohistIntegrationFixture fixture)
{
    [Fact]
    public async Task ReadonlyPat_OnBusinessGet_Passes_AndOnWrite_Answers403()
    {
        var token = await CreatePatAsync("scope-readonly", "readonly");

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var get = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var post = await client.PostAsync("/api/projects", new StringContent("""{"name":"x"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("forbidden", body.GetProperty("code").GetString());
        // The fixture's admin client presents the service (operator) token,
        // so the PAT it mints belongs to the service principal.
        Assert.Equal("service", body.GetProperty("details").GetProperty("principal").GetString());
        Assert.Equal(
            new[] { "readonly" },
            body.GetProperty("details").GetProperty("granted")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task ReadonlyPat_OnSensitiveInfrastructureSurface_Answers403_WhileOperatorIsReachable()
    {
        var token = await CreatePatAsync("scope-readonly-infra", "readonly");

        using var readonlyClient = fixture.CreateClient();
        readonlyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var fs = await readonlyClient.GetAsync("/api/fs/home");
        Assert.Equal(HttpStatusCode.Forbidden, fs.StatusCode);
        using var logs = await readonlyClient.GetAsync("/api/logs/tail");
        Assert.Equal(HttpStatusCode.Forbidden, logs.StatusCode);

        using var operatorClient = fixture.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MohistIntegrationFixture.AdminToken);
        using var operatorFs = await operatorClient.GetAsync("/api/fs/home");
        Assert.Equal(HttpStatusCode.OK, operatorFs.StatusCode);
    }

    [Fact]
    public async Task ReadonlyPat_OnEventSocketObservationSurface_PassesAuthorization()
    {
        var token = await CreatePatAsync("scope-readonly-events", "readonly");

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/projects/missing/events/socket");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RunnerACredential_OnRunnerBPath_Answers403_AndOnItsOwnPath_Passes()
    {
        var runnerA = await InsertRunnerCredentialAsync("scope-runner-a");
        var runnerB = await InsertRunnerCredentialAsync("scope-runner-b");

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runnerA);

        using var ownConfig = await client.GetAsync("/api/runner/scope-runner-a/config");
        Assert.Equal(HttpStatusCode.OK, ownConfig.StatusCode);

        using var ownHeartbeat = await client.PostAsync("/api/runner/scope-runner-a/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, ownHeartbeat.StatusCode);

        using var otherHeartbeat = await client.PostAsync("/api/runner/scope-runner-b/heartbeat", null);
        Assert.Equal(HttpStatusCode.Forbidden, otherHeartbeat.StatusCode);
        var body = JsonDocument.Parse(await otherHeartbeat.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("forbidden", body.GetProperty("code").GetString());
        Assert.Equal("scope-runner-a", body.GetProperty("details").GetProperty("boundRunnerId").GetString());
        Assert.Equal("scope-runner-b", body.GetProperty("details").GetProperty("claimedRunnerId").GetString());

        using var otherReport = await client.PostAsync(
            "/api/runner/scope-runner-b/report",
            new StringContent(
                """{"workId":"w-1","status":"success","workflowRunId":"run-1"}""",
                System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, otherReport.StatusCode);
    }

    [Fact]
    public void RunnerHubRoute_IsGone()
    {
        var patterns = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.DoesNotContain(patterns, pattern =>
            pattern?.StartsWith("/hubs/runner", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task RunnerCredential_OnTaskLogUpload_WithAnotherRunnersHeader_Answers403()
    {
        var runnerA = await InsertRunnerCredentialAsync("scope-log-runner-a");

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runnerA);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/workflow-runs/run-1/work/w-1/task-log")
        {
            Content = new StringContent("""{"entries":[]}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-mohist-runner-id", "scope-log-runner-b");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RunnerCredential_OnBusinessSurface_Answers403()
    {
        var runnerA = await InsertRunnerCredentialAsync("scope-biz-runner-a");

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runnerA);

        using var projects = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Forbidden, projects.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated401_AndInsufficientScope403_AreDistinguishable()
    {
        var readonlyToken = await CreatePatAsync("scope-distinguish", "readonly");

        using var anonymous = fixture.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/api/fs/home");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(unauthorized.Headers.WwwAuthenticate).ToString());
        var unauthorizedBody = JsonDocument.Parse(await unauthorized.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unauthorized", unauthorizedBody.GetProperty("code").GetString());
        Assert.False(unauthorizedBody.TryGetProperty("details", out _));

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readonlyToken);
        using var forbidden = await client.GetAsync("/api/fs/home");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(forbidden.Headers.WwwAuthenticate);
        var forbiddenBody = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("forbidden", forbiddenBody.GetProperty("code").GetString());
        Assert.Equal("service", forbiddenBody.GetProperty("details").GetProperty("principal").GetString());

        Assert.NotEqual(
            unauthorizedBody.GetProperty("code").GetString(),
            forbiddenBody.GetProperty("code").GetString());
    }

    private async Task<string> CreatePatAsync(string name, string scope)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/auth/tokens", new
        {
            name,
            scope,
            ttlHours = 720,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement
            .GetProperty("data").GetProperty("token").GetString()!;
    }

    private async Task<string> InsertRunnerCredentialAsync(string runnerId)
    {
        var token = CredentialToken.Generate(CredentialKind.Runner);
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Credentials.Add(new CredentialRow
        {
            Id = $"cred_{runnerId}",
            PrincipalId = MohistPrincipal.AdminPrincipalId,
            Kind = CredentialKind.Runner.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = """["runner"]""",
            Name = runnerId,
            Prefix = CredentialToken.DisplayPrefix(token),
            ExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedAt = fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return token;
    }
}
