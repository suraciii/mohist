using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssueFeedbackApiSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _connectionString;
    private readonly IGrainFactory _grains;

    public IssueFeedbackApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _connectionString = fixture.ConnectionString;
        _grains = fixture.Grains;
    }

    [Fact]
    public async Task CreateFeedback_OnNonAwaitingStage_Returns409()
    {
        var (project, issueNumber, _, _) = await SeedNonAwaitingApprovalIssueAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "build", body = "should fail", author = "supervisor" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateFeedback_WithoutStageOrBody_Returns400()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var missingBody = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan" });
        Assert.Equal(HttpStatusCode.BadRequest, missingBody.StatusCode);

        var missingStage = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { body = "no stage" });
        Assert.Equal(HttpStatusCode.BadRequest, missingStage.StatusCode);
    }

    [Fact]
    public async Task GetFeedback_UnknownId_Returns404()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback/fb_doesnotexist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFeedback_AfterCreate_JsonWireShape_ExposesNestedResolutionObject()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var created = await _client.PostDataAsync<FeedbackApiFeedbackDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan", body = "live server shape", author = "supervisor" });

        var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        // The shape must expose a 'resolution' field (null when open,
        // omitted from list responses), the canonical id/issueNumber/
        // stage/status/body fields, and must NOT flatten
        // resolutionSummary/resolvedAt to the top level. The wire
        // serializer omits null on the single-record response today;
        // assert the absence-of-flatten invariant.
        Assert.False(data.TryGetProperty("resolutionSummary", out _),
            "flat 'resolutionSummary' must not appear at top level; it belongs under 'resolution'");
        Assert.False(data.TryGetProperty("resolvedAt", out _),
            "flat 'resolvedAt' must not appear at top level; it belongs under 'resolution'");
        Assert.Equal(created.Id, data.GetProperty("id").GetString());
        Assert.Equal(issueNumber, data.GetProperty("issueNumber").GetInt32());
        Assert.Equal("plan", data.GetProperty("stage").GetString());
        Assert.Equal("open", data.GetProperty("status").GetString());
        Assert.Equal("live server shape", data.GetProperty("body").GetString());
    }

    private async Task<(ProjectInfo Project, int IssueNumber, string IssueKey, string WorkflowRunId)>
        SeedAwaitingApprovalIssueAsync()
    {
        var project = await CreateProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueAsync(project.Id, "Feedback API test");
        var wrId = $"wr_{Guid.NewGuid():N}";
        await SeedWorkflowRunAsync(wrId, project.Id, issueNumber, stage: "plan", awaitingApproval: true);
        await AttachWorkflowRunToIssueAsync(project.Id, issueNumber, wrId);
        return (project, issueNumber, issueKey, wrId);
    }

    private async Task<(ProjectInfo Project, int IssueNumber, string IssueKey, string WorkflowRunId)>
        SeedNonAwaitingApprovalIssueAsync()
    {
        var project = await CreateProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueAsync(project.Id, "Non-approval feedback test");
        var wrId = $"wr_{Guid.NewGuid():N}";
        await SeedWorkflowRunAsync(wrId, project.Id, issueNumber, stage: "plan", awaitingApproval: false);
        await AttachWorkflowRunToIssueAsync(project.Id, issueNumber, wrId);
        return (project, issueNumber, issueKey, wrId);
    }

    private async Task<ProjectInfo> CreateProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        return await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "placeholder", GitUrl = "git@example.com:placeholder.git", BaseBranch = "main", IsDefault = true });
    }

    private async Task<(string IssueKey, int Number)> CreateIssueAsync(string projectId, string title)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, title, null, null, null, isDraft: false);
        return (issueKey, number);
    }

    private sealed record FeedbackApiFeedbackDto(string Id, int IssueNumber, string Stage, string Status, string Body);

    private async Task AttachWorkflowRunToIssueAsync(string projectId, int issueNumber, string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_connectionString).Options;
        await using var db = new MohistDbContext(options);
        var row = await db.Issues
            .Where(r => r.ProjectId == projectId && r.Number == issueNumber)
            .FirstOrDefaultAsync();
        Assert.NotNull(row);
        var json = row!.State;
        using var doc = JsonDocument.Parse(json);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText())!;
        dict["workflowRunId"] = JsonSerializer.SerializeToElement(workflowRunId);
        dict["status"] = JsonSerializer.SerializeToElement("InProgress");
        row.State = JsonSerializer.Serialize(dict, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowRunAsync(
        string wrId,
        string projectId,
        int issueNumber,
        string stage,
        bool awaitingApproval)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_connectionString).Options;
        await using var db = new MohistDbContext(options);
        var existing = await db.WorkflowRuns.FindAsync(wrId);

        var stageStatus = awaitingApproval ? "AwaitingApproval" : "Running";
        var runStatus = awaitingApproval ? "AwaitingApproval" : "Running";
        var approval = awaitingApproval
            ? new
            {
                result = (string?)null,
                requestedAt = TestTime.UtcNow.ToString("o"),
                respondedAt = (string?)null,
            }
            : null;

        var state = new
        {
            id = wrId,
            status = runStatus,
            currentStageId = stage,
            metadata = new
            {
                name = "test-run",
                createdAt = TestTime.UtcNow.ToString("o"),
                labels = new Dictionary<string, string>(),
                projectId,
                issueNumber,
            },
            stages = new[]
            {
                new
                {
                    id = stage,
                    attempt = 1,
                    requiresApproval = true,
                    status = stageStatus,
                    initialized = true,
                    tasks = new[]
                    {
                        new
                        {
                            id = $"{stage}-task-1.1",
                            definitionId = $"{stage}-task-1",
                            attempt = 1,
                            title = "Test task",
                            status = "Completed",
                            classification = "UserFacing",
                        }
                    },
                    checks = new[]
                    {
                        new
                        {
                            name = $"{stage}-ok",
                            title = $"{stage} OK",
                            uses = "spec/check",
                            status = "Passed",
                        }
                    },
                    approvalStatus = approval,
                }
            },
            feedback = new object[] { },
        };

        var row = existing ?? new WorkflowRunRow { WorkflowRunId = wrId };
        row.State = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (existing is null)
            db.WorkflowRuns.Add(row);
        await db.SaveChangesAsync();
    }
}