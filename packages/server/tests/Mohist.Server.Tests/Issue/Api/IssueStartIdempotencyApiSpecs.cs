using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Idempotency;
using Mohist.Server.Infrastructure.Idempotency;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Issue.Api;

[Trait("level", "L1")]
public sealed class IssueStartIdempotencyApiSpecs(DefaultMohistIntegrationFixture fixture)
    : IClassFixture<DefaultMohistIntegrationFixture>
{
    private HttpClient Client => fixture.Client;
    private IGrainFactory Grains => fixture.Grains;
    private IServiceProvider Services => fixture.Services;

    [Fact]
    public async Task IssueStart_SameKey_ReplaysRecordedResponseWithoutSecondRun()
    {
        var (projectId, issueNumber) = await SeedIssueAsync();

        var first = await Client.SendAsync(StartRequest(projectId, issueNumber, "start-key-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var workflowRunId = ReadRunId(firstBody);
        Assert.False(string.IsNullOrEmpty(workflowRunId));

        var replay = await Client.SendAsync(StartRequest(projectId, issueNumber, "start-key-1"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, replayBody);
        Assert.Equal(workflowRunId, ReadRunId(replayBody));
        Assert.Equal(1, await RunCountAsync(projectId, issueNumber));
        var row = await FenceRowAsync(projectId, issueNumber);
        Assert.Equal(IdempotencyMappingStates.Completed, row.State);
    }

    [Fact]
    public async Task IssueStart_MalformedKey_Returns400InvalidBeforeAnyEffect()
    {
        var (projectId, issueNumber) = await SeedIssueAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/issues/{issueNumber}/start");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "bad key\u0007");
        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_invalid", payload.GetProperty("code").GetString());
        Assert.Equal("none", payload.GetProperty("effect").GetString());
        Assert.False(payload.GetProperty("retrySafe").GetBoolean());
        Assert.Equal(0, await RunCountAsync(projectId, issueNumber));
        Assert.Equal(0, await FenceCountAsync(projectId, issueNumber));
    }

    [Fact]
    public async Task IssueStart_UnkeyedRequest_RecordsNoFenceRow()
    {
        var (projectId, issueNumber) = await SeedIssueAsync();

        var response = await Client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/start", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await RunCountAsync(projectId, issueNumber));
        Assert.Equal(0, await FenceCountAsync(projectId, issueNumber));
    }

    private static HttpRequestMessage StartRequest(string projectId, int issueNumber, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/issues/{issueNumber}/start");
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static string ReadRunId(string body)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(body);
        return payload.GetProperty("data").GetProperty("workflowRunId").GetString() ?? string.Empty;
    }

    private async Task<(string ProjectId, int IssueNumber)> SeedIssueAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        await Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"issue-keyed-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "origin",
                GitUrl = "git@example.com:test.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "git diff --check");
        var created = await WorkflowApiTestSupport.CreateIssueInBacklogAsync(Grains, projectId);
        await WorkflowApiTestSupport.SeedWorkflowTemplateAsync(fixture.ConnectionString, projectId);
        return (projectId, created.Number);
    }

    private async Task<int> RunCountAsync(string projectId, int issueNumber)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkflowRuns.AsNoTracking().CountAsync(row =>
            row.MetadataProjectId == projectId && row.IssueNumber == issueNumber);
    }

    private Task<int> FenceCountAsync(string projectId, int issueNumber) =>
        CountRows($"{projectId}|{issueNumber}|");

    private async Task<IdempotencyMappingRow> FenceRowAsync(string projectId, int issueNumber)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.IdempotencyMappings.AsNoTracking()
            .Where(row => row.Command == IdempotencyCommands.IssueStart)
            .ToListAsync();
        return rows.Single(row =>
            row.ScopeKey.StartsWith($"{projectId}|{issueNumber}|", StringComparison.Ordinal));
    }

    private async Task<int> CountRows(string scopePrefix)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return (await db.IdempotencyMappings.AsNoTracking()
            .Where(row => row.Command == IdempotencyCommands.IssueStart)
            .ToListAsync())
            .Count(row => row.ScopeKey.StartsWith(scopePrefix, StringComparison.Ordinal));
    }
}
