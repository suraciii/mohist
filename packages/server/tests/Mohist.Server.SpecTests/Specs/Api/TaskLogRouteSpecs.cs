using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Route-level contract specs for the task-log upload
/// (<c>POST /api/.../task-log</c>) and read
/// (<c>GET /api/.../logs</c>) endpoints: request binding/validation
/// (400 malformed json / duplicate seq / invalid metadata / oversized
/// text), owner resolution (404 unknown owner), the dependency boundary
/// (upload must not invoke a grain), and one empty-page read shape. The
/// store's write/read calculation matrix (append + dedup, owner-kind
/// isolation, cursor pagination in seq order, empty page for unknown
/// owner) lives in <c>TaskLogStoreSpecs</c>.
/// </summary>
[Collection("IntegrationApi")]
public class TaskLogRouteSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private const string RunnerId = "runner-tasklog-spec";

    public TaskLogRouteSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects",
            $"{prefix}-{Guid.NewGuid():N}");
        return project.GetProperty("id").GetString()!;
    }

    private async Task EnsureRepositoryAsync(string projectId)
    {
        await _fixture.Client.PostOkAsync(
            $"/api/projects/{projectId}/repositories",
            new
            {
                name = "main",
                gitUrl = $"file://{Guid.NewGuid():N}",
                baseBranch = "main",
                setDefault = true,
            });
    }

    private async Task<int> CreateIssueAsync(string projectId, string title)
    {
        await EnsureRepositoryAsync(projectId);
        var issue = await _fixture.Client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title,
                body = "task log test",
                labels = new Dictionary<string, string>(StringComparer.Ordinal),
                priority = "p3",
                isDraft = false,
            });
        return issue.GetProperty("number").GetInt32();
    }

    private async Task SeedActiveWorkflowRunAsync(
        string workflowRunId,
        string taskId,
        string workId,
        string? projectId = null)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        await runStore.SaveAsync(new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: _fixture.TimeProvider.GetUtcNow(),
                ProjectId: projectId),
            CurrentStageId = "build",
            Status = WorkflowRunStatus.Running,
            Assignment = new WorkflowAssignment(RunnerId, _fixture.TimeProvider.GetUtcNow()),
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks =
                    [
                        new TaskRun
                        {
                            Id = taskId,
                            DefinitionId = taskId,
                            Attempt = 1,
                            Title = "Build it",
                            Uses = "core/script",
                            WorkId = workId,
                            WorkerId = RunnerId,
                            Status = TaskRunStatus.Running,
                            Classification = TaskClassification.Orchestration,
                        }
                    ],
                },
            ],
        });
    }

    private async Task BindIssueToWorkflowRunAsync(string projectId, int issueNumber, string workflowRunId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE Issues SET State = json_set(State, '$.workflowRunId', json_quote({0})) WHERE ProjectId = {1} AND Number = {2}",
            workflowRunId, projectId, issueNumber);
    }

    private async Task<HttpResponseMessage> PostTaskLogAsync(string url, object body, string runnerId = RunnerId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JSON.Options),
        };
        request.Headers.TryAddWithoutValidation(TaskLogRoutes.RunnerIdHeader, runnerId);
        return await _fixture.Client.SendAsync(request);
    }

    private object OneLineBody(string text = "line", bool terminal = false) => new
    {
        entries = new[] { new { seq = 1L, timestamp = _fixture.TimeProvider.GetUtcNow(), source = "action", text } },
        truncated = false,
        terminal,
    };

    [Fact]
    public async Task UploadEndpoint_RejectsMalformedJson()
    {
        var workflowRunId = $"wr-tasklog-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        using var content = new StringContent("{not-json", System.Text.Encoding.UTF8, "application/json");

        using var response = await _fixture.Client.PostAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_RejectsDuplicateSeqValues()
    {
        var workflowRunId = $"wr-tasklog-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();

        using var response = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            new
            {
                entries = new[]
                {
                    new { seq = 1L, timestamp = now, source = "action", text = "first" },
                    new { seq = 1L, timestamp = now, source = "action", text = "duplicate" },
                },
                truncated = false,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_RejectsInvalidMetadataAndOversizedText()
    {
        var workflowRunId = $"wr-tasklog-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        using var missingTimestamp = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            new { entries = new[] { new { seq = 1L, source = "action", text = "line" } }, truncated = false });
        Assert.Equal(HttpStatusCode.BadRequest, missingTimestamp.StatusCode);

        using var emptySource = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            new { entries = new[] { new { seq = 1L, timestamp = _fixture.TimeProvider.GetUtcNow(), source = "", text = "line" } }, truncated = false });
        Assert.Equal(HttpStatusCode.BadRequest, emptySource.StatusCode);

        using var hugeText = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            new { entries = new[] { new { seq = 1L, timestamp = _fixture.TimeProvider.GetUtcNow(), source = "action", text = new string('x', TaskLogUploadLimits.MaxTextLength + 1) } }, truncated = false });
        Assert.Equal(HttpStatusCode.BadRequest, hugeText.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_DoesNotInvokeAnyGrain()
    {
        // Dependency-boundary guard: task-log upload is a service/store
        // write path. It must not gain Orleans grain dependencies later.
        Assert.DoesNotContain(
            typeof(TaskLogService).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType.Namespace?.Contains("Grains", StringComparison.Ordinal) == true
                || p.ParameterType.Name.Contains("Grain", StringComparison.Ordinal));

        var workflowRunId = $"wr-tasklog-isolated-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, "task-1", workId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var stateBefore = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .Select(r => r.State)
            .SingleAsync();
        var now = _fixture.TimeProvider.GetUtcNow();
        var body = new
        {
            entries = new[] { new { seq = 1L, timestamp = now, source = "action", text = "isolated" } },
            truncated = false,
        };

        using var response = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("changed", responseJson.GetProperty("data").GetProperty("status").GetString());

        var stateAfter = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .Select(r => r.State)
            .SingleAsync();
        Assert.Equal(stateBefore, stateAfter);
    }

    [Fact]
    public async Task UploadEndpoint_UnknownOwnerWork_ReturnsNotFoundAndDoesNotPersist()
    {
        var workflowRunId = $"wr-tasklog-missing-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        using var response = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            OneLineBody("forged"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", responseJson.GetProperty("details").GetProperty("status").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var count = await db.TaskLogEntries.AsNoTracking()
            .CountAsync(e => e.OwnerKind == "workflow" && e.OwnerId == workflowRunId && e.WorkId == workId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetEndpoint_TaskWithoutCapturedLines_ReturnsEmptyPageShape()
    {
        var projectId = await CreateProjectAsync("tasklog-empty");
        var issueNumber = await CreateIssueAsync(projectId, "no logs");
        var workflowRunId = $"wr_tasklog_spec_{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, "build.1", workId, projectId);
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, workflowRunId);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.GetProperty("lines").ValueKind);
        Assert.Empty(data.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }
}
