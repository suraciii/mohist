using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using AgentDomain = Mohist.Server.Agent.Domain.Agent;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.DirectApi;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// Direct launch admission is fenced by strict request validation and a
/// durable mapping. The tests use the real PAT boundary and database so a
/// rejected request can be checked for both mapping and canonical side
/// effects.
/// </summary>
[Collection("PublicProjectionIntegration")]
public sealed class DirectApiIdempotencySpecs(PublicProjectionIntegrationFixture fixture)
{
    [Fact]
    public async Task KeyAndBodyValidation_HappensBeforeMappingOrAdmission()
    {
        var projectId = await SeedProjectAsync();
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var before = await CountsAsync();

        using (var missingKey = Request(projectId, "agent_missing", "{\"text\":\"work\"}", null))
        using (var response = await client.SendAsync(missingKey))
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, DirectApiErrorCodes.IdempotencyKeyRequired);

        using (var invalidKey = Request(projectId, "agent_missing", "{\"text\":\"work\"}", new string('k', 129)))
        using (var response = await client.SendAsync(invalidKey))
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, DirectApiErrorCodes.IdempotencyKeyInvalid);

        var invalidBodies = new[]
        {
            "{",
            "[]",
            "{}",
            "{\"text\":null}",
            "{\"text\":\"\"}",
            "{\"text\":1}",
            "{\"text\":\"work\",\"attachments\":[]}",
            "{\"text\":\"one\",\"text\":\"two\"}",
            "{\"text\":\"work\",}",
        };
        for (var index = 0; index < invalidBodies.Length; index++)
        {
            using var request = Request(
                projectId,
                "agent_missing",
                invalidBodies[index],
                $"invalid-body-{index}");
            using var response = await client.SendAsync(request);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, DirectApiErrorCodes.InvalidRequest);
        }

        var after = await CountsAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task WhitespaceOnlyText_IsAcceptedAndPreservedInTheFingerprint()
    {
        var projectId = await SeedProjectAsync();
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active, ready: true);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = Request(projectId, agentId, "{\"text\":\" \"}", "whitespace-text");
            using var response = await client.SendAsync(request);
            Assert.Contains(response.StatusCode, new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.ServiceUnavailable,
            });
        }

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Launch
            && row.ScopeKey == $"{projectId}|{agentId}|whitespace-text");
        Assert.Equal(
            DirectApiWriteValidation.LaunchFingerprint(projectId, agentId, " "),
            mapping.Fingerprint);
        Assert.True(
            string.Equals(mapping.State, DirectApiMappingStates.Completed, StringComparison.Ordinal),
            $"state={mapping.State}; outcome={mapping.Outcome}");
    }

    [Fact]
    public async Task PendingMappingRetry_ReentersTheCanonicalCoordinatorAfterResponseLoss()
    {
        var projectId = await SeedProjectAsync();
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active, ready: true);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "crash-after-insert";
        const string text = "recover the pending launch";
        var fingerprint = DirectApiWriteValidation.LaunchFingerprint(projectId, agentId, text);
        var coordinatorKey = DirectApiWriteValidation.DerivedLaunchCoordinatorKey(projectId, agentId, key);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.DirectApiIdempotencyMappings.Add(new DirectApiIdempotencyMappingRow
            {
                Command = DirectApiCommands.Launch,
                ScopeKey = $"{projectId}|{agentId}|{key}",
                CallerKeyId = "crash-simulated-caller",
                Fingerprint = fingerprint,
                State = DirectApiMappingStates.Pending,
                Outcome = JSON.Serialize(new DirectApiLaunchOutcome(coordinatorKey)),
                CreatedAt = fixture.TimeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        using var request = Request(projectId, agentId, $"{{\"text\":\"{text}\"}}", key);
        using var response = await client.SendAsync(request);
        Assert.Contains(response.StatusCode, new[]
        {
            HttpStatusCode.OK,
            HttpStatusCode.ServiceUnavailable,
        });

        await using var verify = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = await verify.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Launch
            && row.ScopeKey == $"{projectId}|{agentId}|{key}");
        Assert.Equal(DirectApiMappingStates.Completed, mapping.State);
        Assert.Equal(1, await verify.AgentJobs.CountAsync(row => row.AgentId == agentId && row.ProjectId == projectId));
    }

    [Fact]
    public async Task DefinitiveReadinessRejection_IsDurableAndCreatesNoExecution()
    {
        var projectId = await SeedProjectAsync();
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var before = await CountsAsync();

        string firstBody;
        using (var first = Request(projectId, agentId, "{\"text\":\"rejected\"}", "durable-rejection"))
        using (var response = await client.SendAsync(first))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            firstBody = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(firstBody);
            Assert.Equal("terminal", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("rejected", body.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("agent_not_ready", body.RootElement.GetProperty("reasonCode").GetString());
            Assert.Null(body.RootElement.GetProperty("jobId").GetString());
            Assert.Null(body.RootElement.GetProperty("inputId").GetString());
            Assert.Null(body.RootElement.GetProperty("turnId").GetString());
        }

        using (var replay = Request(projectId, agentId, "{\"text\":\"rejected\"}", "durable-rejection"))
        using (var response = await client.SendAsync(replay))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(firstBody, await response.Content.ReadAsStringAsync());
        }

        var after = await CountsAsync();
        Assert.Equal(before with { Mappings = before.Mappings + 1 }, after);
    }

    [Fact]
    public async Task ArchivedAgent_IsNotAWriteMappingTarget()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentAsync(projectId, "agent_archived", AgentStatus.Archived);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var before = await CountsAsync();

        using var request = Request(projectId, "agent_archived", "{\"text\":\"work\"}", "archived");
        using var response = await client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.NotFound, DirectApiErrorCodes.AgentNotFound);

        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task SameKeyWithDifferentExactText_IsAStableConflict()
    {
        var projectId = await SeedProjectAsync();
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active, ready: true);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "exact-text";

        using (var first = Request(projectId, agentId, "{\"text\":\"Fix the bug\"}", key))
        using (var response = await client.SendAsync(first))
            Assert.Contains(response.StatusCode, new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.ServiceUnavailable,
            });

        using (var conflicting = Request(projectId, agentId, "{\"text\":\"Fix the bug \"}", key))
        using (var response = await client.SendAsync(conflicting))
            await AssertErrorAsync(response, HttpStatusCode.Conflict, DirectApiErrorCodes.IdempotencyKeyReused);

        using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mappings = await db.DirectApiIdempotencyMappings
            .Where(row => row.Command == DirectApiCommands.Launch)
            .Where(row => row.ScopeKey == $"{projectId}|{agentId}|{key}")
            .ToListAsync();
        Assert.Single(mappings);
        Assert.Equal(DirectApiWriteValidation.LaunchFingerprint(projectId, agentId, "Fix the bug"), mappings[0].Fingerprint);
    }

    [Fact]
    public async Task CompletedLaunchReplaySurvivesAgentArchive()
    {
        var projectId = await SeedProjectAsync();
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active, ready: true);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "replay-after-archive";
        const string body = "{\"text\":\"recover after archive\"}";

        using var first = await PostLaunchUntilPublicAsync(client, projectId, agentId, body, key);
        var originalJobId = first.RootElement.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(originalJobId));

        await ArchiveAgentAsync(projectId, agentId);

        using var replay = Request(projectId, agentId, body, key);
        using var response = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var replayBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(originalJobId, replayBody.RootElement.GetProperty("jobId").GetString());

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await db.DirectApiIdempotencyMappings.CountAsync(row =>
            row.Command == DirectApiCommands.Launch
            && row.ScopeKey == $"{projectId}|{agentId}|{key}"));
        Assert.Equal(1, await db.AgentJobs.CountAsync(row => row.AgentId == agentId && row.ProjectId == projectId));
    }

    [Fact]
    public async Task ReplayUsesOneDurableMappingAndOneCanonicalLaunch()
    {
        var projectId = await SeedProjectAsync();
        var agentId = $"agent_{Guid.NewGuid():N}";
        await SeedAgentAsync(projectId, agentId, AgentStatus.Active, ready: true);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "replay-three-times";
        const string body = "{\"text\":\"replay me\"}";

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = Request(projectId, agentId, body, key);
            using var response = await client.SendAsync(request);
            Assert.Contains(response.StatusCode, new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.ServiceUnavailable,
            });
        }

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Launch
            && row.ScopeKey == $"{projectId}|{agentId}|{key}");
        Assert.Equal(DirectApiMappingStates.Completed, mapping.State);
        var outcome = JsonDocument.Parse(mapping.Outcome!).RootElement;
        Assert.Equal(
            DirectApiWriteValidation.DerivedLaunchCoordinatorKey(projectId, agentId, key),
            outcome.GetProperty("coordinatorKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(outcome.GetProperty("jobId").GetString()));
        Assert.Equal(1, await db.AgentJobs.CountAsync(row => row.AgentId == agentId && row.ProjectId == projectId));
        Assert.Equal(1, await db.AgentSessions.CountAsync(row => row.LabelProjectId == projectId && row.LabelAgentId == agentId));
    }

    private async Task<JsonDocument> PostLaunchUntilPublicAsync(
        HttpClient client,
        string projectId,
        string agentId,
        string body,
        string key)
    {
        using var first = await client.SendAsync(Request(projectId, agentId, body, key));
        if (first.StatusCode == HttpStatusCode.OK)
            return JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        await fixture.DrainPublicProjectionAsync();
        using var replay = await client.SendAsync(Request(projectId, agentId, body, key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        return JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
    }

    private async Task ArchiveAgentAsync(string projectId, string agentId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Agents.SingleAsync(agent =>
            agent.ProjectId == projectId && agent.Id == agentId);
        var agent = JSON.Deserialize<AgentDomain>(row.State)
            ?? throw new InvalidOperationException("The seeded Agent state is unreadable.");
        agent.Status = AgentStatus.Archived;
        row.State = JSON.Serialize(agent);
        await db.SaveChangesAsync();
    }

    private HttpClient DirectClient(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage Request(
        string projectId,
        string agentId,
        string body,
        string? key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/agents/{agentId}/launch")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private async Task<string> SeedProjectAsync()
    {
        var projectId = $"direct-idempotency-{Guid.NewGuid():N}";
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

    private async Task SeedAgentAsync(
        string projectId,
        string agentId,
        string status,
        bool ready = false)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var agent = new AgentDomain
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            Instructions = ready ? "Complete the requested work." : string.Empty,
            AgentConfig = ready
                ? JsonDocument.Parse("{\"model\":\"provider/model\"}").RootElement.Clone()
                : null,
            Status = status,
            CreatedAt = fixture.TimeProvider.GetUtcNow(),
            UpdatedAt = fixture.TimeProvider.GetUtcNow(),
        };
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            State = JSON.Serialize(agent),
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> CreatePatAsync(string projectId)
        => await DirectApiCredentialTestSupport.CreatePatAsync(
            fixture, "direct-idempotency", [projectId]);

    private async Task<CanonicalCounts> CountsAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return new CanonicalCounts(
            await db.DirectApiIdempotencyMappings.CountAsync(),
            await db.AgentJobs.CountAsync(),
            await db.AgentSessions.CountAsync());
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record CanonicalCounts(int Mappings, int Jobs, int Sessions);
}
