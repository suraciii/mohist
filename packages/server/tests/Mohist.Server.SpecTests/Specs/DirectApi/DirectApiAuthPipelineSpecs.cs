using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// The /api/v1 authentication and authorization pipeline order
/// (specs/external-agent-caller-auth): the boundary accepts only a
/// Bearer PAT with a persisted Project grant; carrier, grant, route
/// scope, and Project authorization settle strictly before any endpoint
/// delegate, validation, idempotency, or admission — so every 401/403
/// path is terminal and leaves no side effects.
/// </summary>
public sealed class DirectApiAuthPipelineSpecs(MohistIntegrationFixture fixture)
{
    private const string CreatePatPath = "/api/auth/tokens";

    private static readonly (HttpMethod Method, string Template)[] AllDirectRoutes =
    [
        (HttpMethod.Post, "/api/v1/projects/{0}/agents/{1}/launch"),
        (HttpMethod.Post, "/api/v1/projects/{0}/agent-sessions/{1}/inputs"),
        (HttpMethod.Post, "/api/v1/projects/{0}/agent-turns/{1}/stop"),
        (HttpMethod.Get, "/api/v1/projects/{0}/agent-jobs/{1}"),
        (HttpMethod.Get, "/api/v1/projects/{0}/agent-inputs/{1}"),
        (HttpMethod.Get, "/api/v1/projects/{0}/agent-turns/{1}"),
        (HttpMethod.Get, "/api/v1/projects/{0}/agent-sessions/{1}/events"),
    ];

    [Fact]
    public async Task AuthenticatedCookieSession_IsRejectedOnEveryDirectRoute()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"mohist_session={MohistIntegrationFixture.AdminToken}");

        foreach (var (method, template) in AllDirectRoutes)
        {
            using var request = new HttpRequestMessage(
                method,
                Route(template, "direct-spec-cookie", "job_1"));
            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {template} answered {response.StatusCode}, expected 401.");
            await AssertDirectErrorAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        }
    }

    [Fact]
    public async Task AuthenticatedCookieSession_IsRejectedOnUnmatchedDirectPath()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"mohist_session={MohistIntegrationFixture.AdminToken}");

        using var response = await client.GetAsync("/api/v1/not-a-registered-route");

        await AssertDirectErrorAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task Direct401_IsNonClassifying_AcrossEveryFailureKind()
    {
        var revokedToken = CredentialToken.Generate(CredentialKind.Pat);
        await InsertCredentialRowAsync(
            $"direct-spec-revoked-{Guid.NewGuid():N}",
            revokedToken,
            revokedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            scopesJson: """["operator"]""");
        var integrationToken = CredentialToken.Generate(CredentialKind.Integration);
        await InsertCredentialRowAsync(
            $"direct-spec-integration-{Guid.NewGuid():N}",
            integrationToken,
            revokedAt: null,
            scopesJson: """["operator"]""",
            kind: CredentialKind.Integration);

        using var anonymous = fixture.CreateClient();
        using var unknownToken = fixture.CreateClient();
        unknownToken.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "moh_pat_not-a-real-token-value");
        using var revoked = fixture.CreateClient();
        revoked.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", revokedToken);
        using var cookie = fixture.CreateClient();
        cookie.DefaultRequestHeaders.Add("Cookie", $"mohist_session={MohistIntegrationFixture.AdminToken}");
        using var connection = fixture.CreateClient();
        connection.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", integrationToken);

        var bodies = new List<string>();
        foreach (var client in new[] { anonymous, unknownToken, revoked, cookie, connection })
        {
            using var response = await client.GetAsync(
                Route(AllDirectRoutes[3].Template, "direct-spec-cookie", "job_1"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        using var body = JsonDocument.Parse(bodies[0]);
        Assert.Equal("unauthenticated", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NonPatBearerCredential_CannotSubstituteForAPat_AndAnswersUnauthenticated()
    {
        // A trusted connection-style credential — here a Project-constrained
        // integration credential — is a different adapter's identity. Presented
        // as a Bearer on /api/v1 it is unauthenticated, byte-identical to any
        // other request without a usable PAT, and never a caller.
        var integrationToken = CredentialToken.Generate(CredentialKind.Integration);
        await InsertCredentialRowAsync(
            $"direct-spec-integration-{Guid.NewGuid():N}",
            integrationToken,
            revokedAt: null,
            scopesJson: """["operator"]""",
            kind: CredentialKind.Integration);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", integrationToken);

        foreach (var (method, template) in AllDirectRoutes)
        {
            using var request = new HttpRequestMessage(
                method,
                Route(template, "direct-spec-integration", "job_1"));
            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {template} answered {response.StatusCode}, expected 401.");
            await AssertDirectErrorAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        }
    }

    [Fact]
    public async Task GrantLessOperatorPat_IsForbiddenOnEveryDirectRoute_AndStillUsableOnTheControlPlane()
    {
        var projectId = await SeedProjectAsync();
        var token = await CreatePatAsync(scope: "operator");

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var (method, template) in AllDirectRoutes)
        {
            using var request = new HttpRequestMessage(method, Route(template, projectId, "job_1"));
            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {template} answered {response.StatusCode}, expected 403.");
            await AssertDirectErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }

        // The PAT keeps its control-plane capability: the grant gates only
        // the direct API, it does not narrow the credential.
        using var controlPlane = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, controlPlane.StatusCode);
    }

    [Fact]
    public async Task ReadonlyPat_WriteRoutes_AreForbiddenBeforeBodyValidationAndIdempotency()
    {
        var projectId = await SeedProjectAsync();
        var token = await CreatePatAsync(scope: "readonly", projectIds: [projectId]);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Malformed JSON and a missing Idempotency-Key would be 400s on the
        // write contract; the scope gate fires first, so the answer is 403.
        using var invalidLaunch = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/agents/agent_1/launch")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };
        using var launchResponse = await client.SendAsync(invalidLaunch);
        Assert.Equal(HttpStatusCode.Forbidden, launchResponse.StatusCode);
        await AssertDirectErrorAsync(launchResponse, HttpStatusCode.Forbidden, "forbidden");

        using var inputsResponse = await client.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/agent-sessions/session_1/inputs",
            new { text = "Follow up." });
        Assert.Equal(HttpStatusCode.Forbidden, inputsResponse.StatusCode);

        using var stopResponse = await client.PostAsync(
            $"/api/v1/projects/{projectId}/agent-turns/turn_1/stop", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, stopResponse.StatusCode);
    }

    [Fact]
    public async Task GrantedPats_PassThePipeline_AndReachTheRegisteredRouteDelegate()
    {
        var projectId = await SeedProjectAsync();
        var readonlyToken = await CreatePatAsync(scope: "readonly", projectIds: [projectId]);
        var operatorToken = await CreatePatAsync(scope: "operator", projectIds: [projectId]);

        using var reader = fixture.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readonlyToken);
        foreach (var (method, template) in AllDirectRoutes.Where(r => r.Method == HttpMethod.Get))
        {
            using var response = await reader.GetAsync(Route(template, projectId, "job_1"));
            Assert.True(
                response.StatusCode != HttpStatusCode.Unauthorized
                && response.StatusCode != HttpStatusCode.Forbidden,
                $"GET {template} was rejected with {response.StatusCode}.");
        }

        using var writer = fixture.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        writer.DefaultRequestHeaders.Add("Idempotency-Key", "direct-spec-pipeline-probe");
        using var launch = await writer.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/agents/agent_1/launch",
            new { text = "Investigate." });
        Assert.True(
            launch.StatusCode != HttpStatusCode.Unauthorized
            && launch.StatusCode != HttpStatusCode.Forbidden,
            $"POST launch was rejected with {launch.StatusCode}.");
    }

    [Fact]
    public async Task OutOfGrantProject_IsForbiddenBeforeResourceLookup_EvenWhenTheProjectDoesNotExist()
    {
        var grantedProject = await SeedProjectAsync();
        var token = await CreatePatAsync(scope: "operator", projectIds: [grantedProject]);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "direct-spec-out-of-grant");

        // A Project that does not exist is still 403, never 404: the grant
        // check runs before any resource lookup.
        using var readMissing = await client.GetAsync(
            $"/api/v1/projects/does-not-exist-{Guid.NewGuid():N}/agent-jobs/job_1");
        Assert.Equal(HttpStatusCode.Forbidden, readMissing.StatusCode);
        await AssertDirectErrorAsync(readMissing, HttpStatusCode.Forbidden, "forbidden");

        using var writeMissing = await client.PostAsJsonAsync(
            $"/api/v1/projects/does-not-exist-{Guid.NewGuid():N}/agents/agent_1/launch",
            new { text = "Investigate." });
        Assert.Equal(HttpStatusCode.Forbidden, writeMissing.StatusCode);

        // An existing Project outside the explicit grant is equally
        // forbidden: existence never changes the answer.
        var otherProject = await SeedProjectAsync();
        using var readExisting = await client.GetAsync(
            $"/api/v1/projects/{otherProject}/agent-jobs/job_1");
        Assert.Equal(HttpStatusCode.Forbidden, readExisting.StatusCode);
    }

    [Fact]
    public async Task OperatorAllGrant_IsHonoredOnlyAsAPersistedGrantKind()
    {
        var projectId = await SeedProjectAsync();
        var token = await CreatePatAsync(scope: "operator", allProjects: true);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // operator_all covers a Project that does not even exist: the read
        // passes the pipeline and the implemented handler then answers the
        // canonical missing-resource code.
        using var response = await client.GetAsync(
            $"/api/v1/projects/does-not-exist-{Guid.NewGuid():N}/agent-jobs/job_1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDirectErrorAsync(response, HttpStatusCode.NotFound, "job_not_found");
    }

    [Fact]
    public async Task UnauthorizedAndForbiddenPaths_HaveZeroSideEffects()
    {
        var projectId = await SeedProjectAsync();
        var outOfGrantProject = await SeedProjectAsync();
        var grantlessOperator = await CreatePatAsync(scope: "operator");
        var readonlyWriter = await CreatePatAsync(scope: "readonly", projectIds: [projectId]);

        const string idempotencyKey = "direct-spec-zero-effects";
        var before = await SnapshotCanonicalStateAsync(projectId, outOfGrantProject, idempotencyKey);

        var battery = new List<(HttpClient Client, HttpMethod Method, string Path)>
        {
            (CookieClient(), HttpMethod.Post, $"/api/v1/projects/{projectId}/agents/agent_1/launch"),
            (fixture.CreateClient(), HttpMethod.Get, $"/api/v1/projects/{projectId}/agent-jobs/job_1"),
            (BearerClient(grantlessOperator), HttpMethod.Post,
                $"/api/v1/projects/{projectId}/agents/agent_1/launch"),
            (BearerClient(readonlyWriter), HttpMethod.Post,
                $"/api/v1/projects/{projectId}/agents/agent_1/launch"),
            (BearerClient(grantlessOperator), HttpMethod.Post,
                $"/api/v1/projects/{outOfGrantProject}/agents/agent_1/launch"),
            (BearerClient(readonlyWriter), HttpMethod.Post,
                $"/api/v1/projects/{outOfGrantProject}/agent-sessions/session_1/inputs"),
        };

        foreach (var (client, method, path) in battery)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = method == HttpMethod.Post
                    ? new StringContent("""{"text":"Investigate."}""", Encoding.UTF8, "application/json")
                    : null,
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            using var response = await client.SendAsync(request);
            client.Dispose();

            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{method} {path} answered {response.StatusCode}; the battery must be rejected.");
        }

        var after = await SnapshotCanonicalStateAsync(projectId, outOfGrantProject, idempotencyKey);
        Assert.Equal(before, after);
    }

    private HttpClient CookieClient()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"mohist_session={MohistIntegrationFixture.AdminToken}");
        return client;
    }

    private HttpClient BearerClient(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Route(string template, string projectId, string resourceId) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            template,
            projectId,
            resourceId);

    /// <summary>
    /// The scenario-owned durable state that a rejected launch or follow-up
    /// could touch. Other Specs may legitimately mutate the shared host, so
    /// global row counts cannot express this claim.
    /// </summary>
    private async Task<(int Jobs, int Sessions, int IdempotencyMappings)> SnapshotCanonicalStateAsync(
        string projectId,
        string outOfGrantProject,
        string idempotencyKey)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var launchScope = DirectApiWriteValidation.LaunchScopeKey(projectId, "agent_1", idempotencyKey);
        var outOfGrantLaunchScope = DirectApiWriteValidation.LaunchScopeKey(
            outOfGrantProject,
            "agent_1",
            idempotencyKey);
        var followupScope = DirectApiWriteValidation.FollowupScopeKey("session_1", idempotencyKey);
        return (
            await db.AgentJobs.CountAsync(row =>
                row.ProjectId == projectId || row.ProjectId == outOfGrantProject),
            await db.AgentSessions.CountAsync(row =>
                row.LabelProjectId == projectId || row.LabelProjectId == outOfGrantProject),
            await db.DirectApiIdempotencyMappings.CountAsync(row =>
                row.ScopeKey == launchScope
                || row.ScopeKey == outOfGrantLaunchScope
                || row.ScopeKey == followupScope));
    }

    private static async Task AssertDirectErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        // The direct API envelope is exactly { error: { code, message } } —
        // never the control-plane `success` envelope.
        Assert.Equal(["error"], root.EnumerateObject().Select(property => property.Name));
        var error = root.GetProperty("error");
        Assert.Equal(["code", "message"], error.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private async Task<string> SeedProjectAsync()
    {
        var projectId = $"direct-spec-{Guid.NewGuid():N}";
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
            CreatedAt = fixture.TimeProvider.GetUtcNow(),
            UpdatedAt = fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task<string> CreatePatAsync(
        string scope,
        string[]? projectIds = null,
        bool allProjects = false)
    {
        using var response = await fixture.Client.PostAsJsonAsync(CreatePatPath, new
        {
            name = $"direct-spec-{Guid.NewGuid():N}",
            scope,
            projectIds,
            allProjects,
        });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data")
            .GetProperty("token")
            .GetString()!;
    }

    private async Task InsertCredentialRowAsync(
        string id,
        string token,
        DateTimeOffset? revokedAt,
        string scopesJson,
        CredentialKind kind = CredentialKind.Pat)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Credentials.Add(new CredentialRow
        {
            Id = id,
            PrincipalId = "direct-spec-principal",
            Kind = kind.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = scopesJson,
            Name = $"direct-spec-{Guid.NewGuid():N}",
            ExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            RevokedAt = revokedAt,
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
    }
}
