using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// External callers see the durable Job-only snapshot, never the existing
/// internal launch-observation shape. Session-aware reads remain unmapped.
/// </summary>
[Collection("DirectApiJobRead")]
public sealed class DirectApiAgentJobReadRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public DirectApiAgentJobReadRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GrantedReadonlyPat_ReadsThePersistedAllowlistedJobSnapshot()
    {
        var job = await CreateQueuedJobAsync("direct-read");
        var token = await CreatePatAsync("direct-read", job.ProjectId);

        using var client = DirectClient(token);
        using var response = await client.GetAsync(PathFor(job.ProjectId, job.JobId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(job.ProjectId, data.GetProperty("projectId").GetString());
        Assert.Equal(job.AgentId, data.GetProperty("agentId").GetString());
        Assert.Equal(job.JobId, data.GetProperty("jobId").GetString());
        Assert.Equal("queued", data.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("outcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("reasonCode").ValueKind);

        var fields = data.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(
            new[]
            {
                "acceptedAt", "agentId", "jobId", "observedAt", "outcome", "projectId",
                "reasonCode", "startedAt", "status", "terminalAt",
            },
            fields);
    }

    [Fact]
    public async Task GrantOutsideRequestedProject_IsForbiddenBeforeJobLookup()
    {
        var job = await CreateQueuedJobAsync("direct-forbidden");
        var otherProject = await CreateProjectAsync("direct-other");
        var token = await CreatePatAsync("direct-other", otherProject);

        using var client = DirectClient(token);
        using var response = await client.GetAsync(PathFor(job.ProjectId, job.JobId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CookieAndGrantlessPat_CannotSubstituteForGrantedBearerPat()
    {
        var job = await CreateQueuedJobAsync("direct-pat-only");

        using var cookieClient = _fixture.CreateClient();
        cookieClient.DefaultRequestHeaders.Add(
            "Cookie",
            $"mohist_session={MohistIntegrationFixture.AdminToken}");
        using var cookieResponse = await cookieClient.GetAsync(PathFor(job.ProjectId, job.JobId));
        Assert.Equal(HttpStatusCode.Unauthorized, cookieResponse.StatusCode);

        var grantlessToken = await CreatePatAsync("direct-grantless", projectId: null);
        using var grantlessClient = DirectClient(grantlessToken);
        using var grantlessResponse = await grantlessClient.GetAsync(PathFor(job.ProjectId, job.JobId));
        Assert.Equal(HttpStatusCode.Forbidden, grantlessResponse.StatusCode);
    }

    [Fact]
    public async Task StaleSnapshot_ReturnsProjectionLagWithoutCanonicalFallback()
    {
        var job = await CreateQueuedJobAsync("direct-lag");
        var token = await CreatePatAsync("direct-lag", job.ProjectId);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var row = await db.AgentJobs.SingleAsync(candidate => candidate.JobKey == job.JobId);
            Assert.NotNull(row.DirectApiProjectionJson);
            Assert.True(row.Revision > 0);
            row.DirectApiProjectionRevision = row.Revision - 1;
            await db.SaveChangesAsync();
        }

        using var client = DirectClient(token);
        using var response = await client.GetAsync(PathFor(job.ProjectId, job.JobId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("projection_lag", body.GetProperty("code").GetString());
    }

    private async Task<(string ProjectId, string AgentId, string JobId)> CreateQueuedJobAsync(string prefix)
    {
        var projectId = await CreateProjectAsync(prefix);
        var agent = await CreateAgentAsync(projectId, $"{prefix}-agent");
        using var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "remain queued" });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var data = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return (projectId, agent.Id, data.GetProperty("jobId").GetString()!);
    }

    private async Task<string> CreatePatAsync(string name, string? projectId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/auth/tokens", new
        {
            name = $"{name}-{Guid.NewGuid():N}",
            scope = "readonly",
            ttlHours = 24,
            projectIds = projectId is null ? null : new[] { projectId },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data")
            .GetProperty("token")
            .GetString()!;
    }

    private HttpClient DirectClient(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string PathFor(string projectId, string jobId) =>
        $"/api/v1/projects/{projectId}/agent-jobs/{jobId}";
}
