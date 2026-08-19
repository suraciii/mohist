using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// Integration token issuance and revocation (docs/auth.md "入站集成：
/// 独立令牌"): an integration token is narrowed to one project, the full
/// value appears in exactly one response, revocation is immediate and
/// per-token, and the token authenticates through the unified bearer
/// resolution. The per-project narrowing judgment itself is the P2 scope
/// gate (#325); this issue delivers the mechanism and the auth-layer
/// hook (see IntegrationProjectConstraint and
/// AuthResolutionMiddlewareTests).
/// </summary>
public sealed class IntegrationTokenSpecs(MohistIntegrationFixture fixture)
{
    private const string CreatePath = "/api/integration-tokens";

    [Fact]
    public async Task Create_ReturnsTheFullTokenExactlyOnce_WithTheProjectConstraintRecorded()
    {
        var project = await CreateProjectAsync("itok-a");

        using var response = await AdminClient().PostAsJsonAsync(CreatePath, new
        {
            name = "github-webhook",
            projectScope = project.Id,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var token = JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("token").GetString()!;
        Assert.StartsWith("moh_integration_", token, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(body, Regex.Escape(token)));

        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.Equal(project.Id, data.GetProperty("projectId").GetString());
        Assert.Equal("github-webhook", data.GetProperty("name").GetString());

        // The issued token authenticates through the unified bearer
        // resolution (a 403, not 401, proves the credential was accepted
        // and rejected on scope grounds), and the constraint is recorded
        // in the store. The business observation surface requires
        // operator-or-readonly (#325); a webhook-scoped integration
        // credential is denied there.
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var projects = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Forbidden, projects.StatusCode);

        var store = fixture.Services.GetRequiredService<ICredentialStore>();
        var resolved = await store.FindActiveAsync(CredentialToken.Hash(token));
        Assert.NotNull(resolved);
        Assert.Equal(CredentialKind.Integration, resolved.Kind);
        Assert.Equal(project.Id, resolved.ProjectId);
        Assert.Equal(Scope.Webhook, Assert.Single(resolved.Scopes));
    }

    [Fact]
    public async Task Create_AcceptsAProjectName_AndRecordsTheCanonicalId()
    {
        var project = await CreateProjectAsync("itok-by-name");

        using var response = await AdminClient().PostAsJsonAsync(CreatePath, new
        {
            name = "by-name",
            projectScope = project.Name,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        Assert.Equal(project.Id, data.GetProperty("projectId").GetString());
    }

    [Fact]
    public async Task Create_WithAnUnknownProjectScope_IsRejected()
    {
        using var response = await AdminClient().PostAsJsonAsync(CreatePath, new
        {
            name = "ghost-project",
            projectScope = "proj_does_not_exist",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithADuplicateActiveName_IsRejected()
    {
        var project = await CreateProjectAsync("itok-dup");

        using var first = await AdminClient().PostAsJsonAsync(CreatePath, new
        {
            name = "dup-name",
            projectScope = project.Id,
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await AdminClient().PostAsJsonAsync(CreatePath, new
        {
            name = "dup-name",
            projectScope = project.Id,
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutACredential_IsRejected()
    {
        using var client = fixture.CreateClient();
        using var response = await client.PostAsJsonAsync(CreatePath, new
        {
            name = "anonymous",
            projectScope = "proj_a",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithAServiceCredential_IsRejected()
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "not-admin",
            projectScope = "proj_a",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_MakesTheToken401Immediately_AndLeavesOtherTokensAlone()
    {
        var (projectA, _) = await CreateProjectAsync("itok-revoke-a");
        var (projectB, _) = await CreateProjectAsync("itok-revoke-b");
        var victim = await CreateTokenAsync("victim", projectA);
        var survivor = await CreateTokenAsync("survivor", projectB);

        using var revoke = await AdminClient().DeleteAsync($"{CreatePath}/{victim.Id}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var victimClient = fixture.CreateClient();
        victimClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", victim.Token);
        using var victimCall = await victimClient.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, victimCall.StatusCode);

        using var survivorClient = fixture.CreateClient();
        survivorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", survivor.Token);
        using var survivorCall = await survivorClient.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Forbidden, survivorCall.StatusCode);
    }

    [Fact]
    public async Task Revoke_UnknownId_Returns404()
    {
        using var response = await AdminClient().DeleteAsync($"{CreatePath}/itok_missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Token_AuthenticatesButIsForbiddenOnTheBusinessObservationSurface()
    {
        var project = await CreateProjectAsync("itok-surface");
        var issued = await CreateTokenAsync("surface", project.Id);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        using var detail = await client.GetAsync($"/api/projects/{project.Id}");

        // Authentication succeeds (403, not 401); the webhook scope is
        // denied on the operator-or-readonly business surface (#325).
        // The per-project narrowing judgment on webhook surfaces is the
        // P2 scope gate.
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    private HttpClient AdminClient()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MohistIntegrationFixture.AdminToken);
        return client;
    }

    private async Task<ProjectRef> CreateProjectAsync(string name)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name = $"{name}-{Guid.NewGuid():N}",
            repository = new { name = "primary", gitUrl = "git@example.com:primary.git", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        return new ProjectRef(data.GetProperty("id").GetString()!, data.GetProperty("name").GetString()!);
    }

    private async Task<IssuedToken> CreateTokenAsync(string name, string projectId)
    {
        using var response = await AdminClient().PostAsJsonAsync(CreatePath, new
        {
            name,
            projectScope = projectId,
        });
        response.EnsureSuccessStatusCode();
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        return new IssuedToken(
            data.GetProperty("id").GetString()!,
            data.GetProperty("token").GetString()!);
    }

    private sealed record ProjectRef(string Id, string Name);

    private sealed record IssuedToken(string Id, string Token);
}
