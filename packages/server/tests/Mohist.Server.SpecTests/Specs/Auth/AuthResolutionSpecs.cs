using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// The unified auth-resolution surface: every non-exempt request on
/// /api, /hubs and /otel/api requires a credential presented as Bearer
/// header or session cookie, and every rejection is an indistinguishable
/// 401 with an RFC 6750 invalid_token challenge (docs/auth.md).
/// </summary>
[Collection("IntegrationMisc")]
public sealed class AuthResolutionSpecs(MohistIntegrationFixture fixture)
{
    private const string RepoName = "hello-world";
    private static readonly string LabeledPayload = """
        {
          "action": "labeled",
          "number": 42,
          "issue": {
            "number": 42,
            "title": "Fix the bug",
            "state": "open",
            "labels": [ { "name": "mohist" } ]
          },
          "repository": {
            "name": "hello-world",
            "full_name": "octocat/hello-world",
            "owner": { "login": "octocat" }
          }
        }
        """;

    [Fact]
    public async Task MissingCredential_OnBusinessApi_Answers401WithChallenge()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            """{"success":false,"error":"Authentication required.","code":"unauthorized"}""",
            body);
    }

    [Fact]
    public async Task MissingCredential_OnSignalRHubs_Answers401()
    {
        using var client = fixture.CreateClient();
        using var negotiate = await client.GetAsync("/hubs/runner/negotiate?negotiateVersion=1");

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        Assert.Equal(
            "Bearer error=\"invalid_token\"",
            Assert.Single(negotiate.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task MissingCredential_OnOtelApi_Answers401()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/otel/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BearerFileCredential_IsAccepted()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MohistIntegrationFixture.AdminToken);

        using var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SessionCookie_IsAccepted()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"mohist_session={MohistIntegrationFixture.AdminToken}");

        using var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ValidBearerHeader_WinsOverAnInvalidCookie()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MohistIntegrationFixture.AdminToken);
        client.DefaultRequestHeaders.Add("Cookie", "mohist_session=not-a-real-token");

        using var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task QueryStringToken_IsRejected_EvenWithAValidBearerHeader()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MohistIntegrationFixture.AdminToken);

        using var response = await client.GetAsync("/api/projects?access_token=leaked");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ActiveIssuedCredential_InTheDatabase_IsAccepted()
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        await InsertCredentialRowAsync(
            "cred_spec_active", "agent-spec", CredentialKind.Pat, token,
            revokedAt: null, expiresAt: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RevokedIssuedCredential_AnswersTheSame401_AsAnUnknownToken()
    {
        var revokedToken = CredentialToken.Generate(CredentialKind.Pat);
        await InsertCredentialRowAsync(
            "cred_spec_revoked", "agent-spec", CredentialKind.Pat, revokedToken,
            revokedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            expiresAt: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var client = fixture.CreateClient();
        using var revokedResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/projects")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", revokedToken) },
        });
        using var unknownResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/projects")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "moh_pat_unknown-token") },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        Assert.Equal(
            await revokedResponse.Content.ReadAsStringAsync(),
            await unknownResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthProbe_IsReachableWithoutACredential()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeviceAuthorizationEndpoints_AreReachableWithoutACredential()
    {
        using var client = fixture.CreateClient();
        using var session = await client.PostAsync("/api/auth/session", null);
        using var device = await client.PostAsync("/api/auth/device/code", null);
        using var token = await client.PostAsync("/api/auth/token", null);

        Assert.NotEqual(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, device.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, token.StatusCode);
    }

    [Fact]
    public async Task GitHubIngress_IsExemptFromAuthentication_AndStillSelfValidates()
    {
        var owner = $"octocat-auth-{Guid.NewGuid():N}";
        var project = await fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-ingress-auth-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepoName });
        var connectionId = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("webhookSecret").GetString()!;
        var bytes = Encoding.UTF8.GetBytes(LabeledPayload);

        // An anonymous request reaches the route (exempt), and the route's
        // own HMAC verification rejects the bad signature.
        using var client = fixture.CreateClient();
        using var badSignature = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        badSignature.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        badSignature.Headers.Add("X-GitHub-Event", "issues");
        badSignature.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");
        using var rejected = await client.SendAsync(badSignature);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        // With the correct signature the anonymous request is served.
        using var goodSignature = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        goodSignature.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        goodSignature.Headers.Add("X-GitHub-Event", "issues");
        goodSignature.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes)).ToLowerInvariant());
        using var accepted = await client.SendAsync(goodSignature);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task HubNegotiate_WithAValidCredential_IsServed()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MohistIntegrationFixture.AdminToken);

        using var response = await client.PostAsync(
            "/hubs/runner/negotiate?negotiateVersion=1",
            new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task InsertCredentialRowAsync(
        string id,
        string principalId,
        CredentialKind kind,
        string token,
        DateTimeOffset? revokedAt,
        DateTimeOffset? expiresAt)
    {
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Credentials.Add(new CredentialRow
        {
            Id = id,
            PrincipalId = principalId,
            Kind = kind.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = """["runner"]""",
            Name = "spec",
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
    }
}
