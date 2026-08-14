using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Auth;

/// <summary>
/// PAT issuance, listing and revocation (docs/auth.md "脚本与外部
/// Agent：个人访问令牌"): the full token appears in exactly one response,
/// every token must expire, names are unique among active credentials,
/// revocation is immediate and list never echoes full values.
/// </summary>
[Collection("IntegrationMisc")]
public sealed class PatTokenSpecs(MohistIntegrationFixture fixture)
{
    private const string CreatePath = "/api/auth/tokens";

    [Fact]
    public async Task Create_ReturnsTheFullTokenExactlyOnce_AndItCallsTheApi()
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "ci-bot",
            scope = "readonly",
            ttlHours = 720,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var token = JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("token").GetString()!;
        Assert.StartsWith("moh_pat_", token, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(body, Regex.Escape(token)));

        // The issued token works as a Bearer credential (MOHIST_TOKEN path).
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var projects = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutTtl_DefaultsTo90Days()
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "default-ttl",
            scope = "operator",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        Assert.Equal(
            fixture.TimeProvider.GetUtcNow().AddDays(90),
            data.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("operator", data.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Create_WithRepeatedProjects_PersistsAnExplicitGrantWithExactlyThatSet()
    {
        await SeedProjectAsync("pat-grant-a");
        await SeedProjectAsync("pat-grant-b");

        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "explicit-grant",
            scope = "operator",
            projectIds = new[] { "pat-grant-a", "pat-grant-b" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        var credentialId = data.GetProperty("id").GetString()!;
        var grant = await ReadGrantAsync(credentialId);
        Assert.Equal("explicit", grant.Kind);
        Assert.Equal(new[] { "pat-grant-a", "pat-grant-b" }, grant.ProjectIds);
    }

    [Fact]
    public async Task Create_WithAllProjects_PersistsOperatorAllWithAnEmptyChildSet()
    {
        await SeedProjectAsync("pat-grant-all");

        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "operator-all-grant",
            scope = "operator",
            allProjects = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        var grant = await ReadGrantAsync(data.GetProperty("id").GetString()!);
        Assert.Equal("operator_all", grant.Kind);
        Assert.Empty(grant.ProjectIds);
    }

    [Fact]
    public async Task Create_WithInvalidGrantBinding_IsForbiddenAndPersistsNothing()
    {
        var requests = new[]
        {
            (Name: "grant-both", Body: (object)new { name = "grant-both", scope = "operator", projectIds = new[] { "missing-a" }, allProjects = true }),
            (Name: "grant-readonly-all", Body: (object)new { name = "grant-readonly-all", scope = "readonly", allProjects = true }),
            (Name: "grant-unknown", Body: (object)new { name = "grant-unknown", scope = "operator", projectIds = new[] { "missing-project" } }),
        };

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var grantsBefore = await db.CredentialProjectGrants.CountAsync();

        foreach (var request in requests)
        {
            using var response = await fixture.Client.PostAsJsonAsync(CreatePath, request.Body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var request in requests)
        {
            Assert.Equal(0, await db.Credentials.CountAsync(row => row.Name == request.Name));
        }

        Assert.Equal(grantsBefore, await db.CredentialProjectGrants.CountAsync());
    }

    [Fact]
    public async Task Create_WithExplicitTtl_ExpiresExactlyThatFarOut()
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "explicit-ttl",
            scope = "readonly",
            ttlHours = 720,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data");
        Assert.Equal(
            fixture.TimeProvider.GetUtcNow().AddHours(720),
            data.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24 * 365 + 1)]
    public async Task Create_WithOutOfRangeTtl_IsRejected(int ttlHours)
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "bad-ttl",
            scope = "operator",
            ttlHours,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithUnknownScope_IsRejected()
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "bad-scope",
            scope = "admin",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutACredential_IsRejected()
    {
        using var client = fixture.CreateClient();
        using var response = await client.PostAsJsonAsync(CreatePath, new
        {
            name = "anonymous",
            scope = "operator",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithADuplicateActiveName_IsRejected()
    {
        using var first = await CreatePatAsync("dup-name", scope: "readonly");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "dup-name",
            scope = "readonly",
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Revoke_MakesTheToken401Immediately_AndLeavesOtherTokensAlone()
    {
        var victim = await CreatePatAndReadAsync("victim", "readonly");
        var survivor = await CreatePatAndReadAsync("survivor", "operator");

        using var revoke = await fixture.Client.PostAsJsonAsync(
            $"{CreatePath}/victim/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var victimClient = fixture.CreateClient();
        victimClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", victim);
        using var victimCall = await victimClient.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, victimCall.StatusCode);

        using var survivorClient = fixture.CreateClient();
        survivorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", survivor);
        using var survivorCall = await survivorClient.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, survivorCall.StatusCode);
    }

    [Fact]
    public async Task Revoke_UnknownName_Returns404()
    {
        using var response = await fixture.Client.PostAsJsonAsync(
            $"{CreatePath}/missing/revoke", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_FreesTheNameForReissuance()
    {
        await CreatePatAsync("reuse-me", "readonly");

        using var revoke = await fixture.Client.PostAsJsonAsync(
            $"{CreatePath}/reuse-me/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var reissued = await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name = "reuse-me",
            scope = "readonly",
        });
        Assert.Equal(HttpStatusCode.Created, reissued.StatusCode);
    }

    [Fact]
    public async Task List_ShowsNameAndPrefix_ButNeverFullTokenValues()
    {
        var token = await CreatePatAndReadAsync("listed-token", "readonly");
        var prefix = token[..("moh_pat_".Length + 8)];

        using var response = await fixture.Client.GetAsync(CreatePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.Contains(prefix, body, StringComparison.Ordinal);
        Assert.Contains("listed-token", body, StringComparison.Ordinal);

        var tokens = JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("tokens");
        var item = tokens.EnumerateArray().Single(element =>
            element.GetProperty("name").GetString() == "listed-token");
        Assert.Equal(prefix, item.GetProperty("prefix").GetString());
        Assert.Equal("readonly", Assert.Single(item.GetProperty("scopes").EnumerateArray()).GetString());
        Assert.False(item.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task List_AfterRevoke_MarksTheTokenRevoked()
    {
        await CreatePatAsync("revoked-listed", "operator");
        await fixture.Client.PostAsJsonAsync($"{CreatePath}/revoked-listed/revoke", new { });

        var tokens = await fixture.Client.GetDataAsync<JsonElement>(CreatePath);
        var item = tokens.GetProperty("tokens").EnumerateArray().Single(element =>
            element.GetProperty("name").GetString() == "revoked-listed");

        var revokedAt = item.GetProperty("revokedAt").GetDateTimeOffset();
        Assert.NotEqual(default, revokedAt);
    }

    private async Task SeedProjectAsync(string projectId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        if (await db.Projects.AnyAsync(project => project.Id == projectId))
            return;

        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
            CreatedAt = fixture.TimeProvider.GetUtcNow(),
            UpdatedAt = fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<(string Kind, string[] ProjectIds)> ReadGrantAsync(string credentialId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var credential = await db.Credentials.SingleAsync(row => row.Id == credentialId);
        var projectIds = await db.CredentialProjectGrants
            .Where(grant => grant.CredentialId == credentialId)
            .OrderBy(grant => grant.ProjectId)
            .Select(grant => grant.ProjectId)
            .ToArrayAsync();
        return (credential.DirectApiProjectGrantKind!, projectIds);
    }

    private async Task<HttpResponseMessage> CreatePatAsync(string name, string scope)
    {
        return await fixture.Client.PostAsJsonAsync(CreatePath, new
        {
            name,
            scope,
        });
    }

    private async Task<string> CreatePatAndReadAsync(string name, string scope)
    {
        using var response = await CreatePatAsync(name, scope);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }
}
