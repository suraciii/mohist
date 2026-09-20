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
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Api;

/// <summary>
/// Route-level contract specs for the task-log upload
/// (<c>POST /api/.../task-log</c>) and read
/// (<c>GET /api/.../logs</c>) endpoints: request binding/validation
/// (400 malformed json / duplicate seq / invalid metadata / oversized
/// text), owner resolution (404 unknown owner), the dependency boundary
/// (upload must not invoke a grain), read addressing (the originating
/// run id is required, its project/issue scope and exact attempt are
/// enforced, and an addressable attempt without lines is an empty page),
/// and original-run/attempt resolution across a later run or retry. The
/// store's write/read calculation matrix (append + dedup, owner-kind
/// isolation, cursor pagination in seq order, empty page for unknown
/// owner) lives in <c>TaskLogStoreSpecs</c>.
/// </summary>
[Trait("level", "L1")]
public class TaskLogRouteSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private const string RunnerId = "runner-tasklog-spec";

    public TaskLogRouteSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"{prefix}-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }

    private async Task<int> CreateIssueAsync(string projectId, string title)
    {
        var number = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        return await _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number))).CreateAsync(
            projectId,
            number,
            title,
            "task log test",
            new Dictionary<string, string>(StringComparer.Ordinal),
            "p3",
            repositoryRef: null,
            isDraft: false);
    }

    private Task SeedActiveWorkflowRunAsync(
        string workflowRunId,
        string taskId,
        string workId,
        string? projectId = null,
        int? issueNumber = null) =>
        SeedActiveWorkflowRunAttemptsAsync(
            workflowRunId,
            [(taskId, workId, WorkflowActionAttemptStatus.Running)],
            projectId,
            issueNumber);

    private async Task SeedActiveWorkflowRunAttemptsAsync(
        string workflowRunId,
        (string TaskId, string WorkId, WorkflowActionAttemptStatus Status)[] attempts,
        string? projectId = null,
        int? issueNumber = null)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        await runStore.SaveAsync(new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: _fixture.TimeProvider.GetUtcNow(),
                ProjectId: projectId,
                IssueNumber: issueNumber),
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
                    Tasks = attempts
                        .Select(attempt => new WorkflowActionAttempt
                        {
                            Id = attempt.TaskId,
                            DefinitionId = attempt.TaskId,
                            Attempt = 1,
                            Title = "Build it",
                            Uses = "core/script",
                            WorkId = attempt.WorkId,
                            WorkerId = RunnerId,
                            Status = attempt.Status,
                            Classification = TaskClassification.Orchestration,
                        })
                        .ToList(),
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
    public async Task GetEndpoint_RequiresTheOriginatingWorkflowRunId()
    {
        var projectId = await CreateProjectAsync("tasklog-required-run");
        var issueNumber = await CreateIssueAsync(projectId, "run id required");

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetEndpoint_TaskWithoutCapturedLines_ReturnsEmptyPageShape()
    {
        var projectId = await CreateProjectAsync("tasklog-empty");
        var issueNumber = await CreateIssueAsync(projectId, "no logs");
        var workflowRunId = $"wr_tasklog_spec_{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, "build.1", workId, projectId, issueNumber);
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, workflowRunId);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?workflowRunId={workflowRunId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.GetProperty("lines").ValueKind);
        Assert.Empty(data.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetEndpoint_ReadsTheOriginatingRunAfterTheIssueStartsAnotherRun()
    {
        var projectId = await CreateProjectAsync("tasklog-origin-run");
        var issueNumber = await CreateIssueAsync(projectId, "original run evidence");

        var originalRunId = $"wr_tasklog_original_{Guid.NewGuid():N}";
        var originalWorkId = $"work-original-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(originalRunId, "build.1", originalWorkId, projectId, issueNumber);
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, originalRunId);
        using (var upload = await PostTaskLogAsync(
            $"/api/workflow-runs/{originalRunId}/work/{originalWorkId}/task-log",
            OneLineBody("original run line")))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var laterRunId = $"wr_tasklog_later_{Guid.NewGuid():N}";
        var laterWorkId = $"work-later-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(laterRunId, "build.1", laterWorkId, projectId, issueNumber);
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, laterRunId);
        using (var upload = await PostTaskLogAsync(
            $"/api/workflow-runs/{laterRunId}/work/{laterWorkId}/task-log",
            OneLineBody("later run line")))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        // The issue now binds the later run; the original run's read must
        // still resolve the original run's attempt and lines.
        using var original = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?workflowRunId={originalRunId}");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var originalData = (await original.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var originalLine = Assert.Single(originalData.GetProperty("lines").EnumerateArray());
        Assert.Equal("original run line", originalLine.GetProperty("text").GetString());

        using var later = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?workflowRunId={laterRunId}");
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        var laterData = (await later.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var laterLine = Assert.Single(laterData.GetProperty("lines").EnumerateArray());
        Assert.Equal("later run line", laterLine.GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetEndpoint_ReadsTheOriginalAttemptAfterTheSameRunRetriesTheTask()
    {
        var projectId = await CreateProjectAsync("tasklog-retry");
        var issueNumber = await CreateIssueAsync(projectId, "retried attempt");
        var workflowRunId = $"wr_tasklog_retry_{Guid.NewGuid():N}";
        var originalWorkId = $"work-original-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, "build.1", originalWorkId, projectId, issueNumber);
        using (var upload = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{originalWorkId}/task-log",
            OneLineBody("first attempt line")))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        // A retry keeps the failed attempt for history and adds the next
        // attempt, so the run now owns both attempts.
        var retryWorkId = $"work-retry-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAttemptsAsync(
            workflowRunId,
            [
                ("build.1", originalWorkId, WorkflowActionAttemptStatus.Failed),
                ("build.2", retryWorkId, WorkflowActionAttemptStatus.Running),
            ],
            projectId,
            issueNumber);

        using var original = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?workflowRunId={workflowRunId}");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var originalData = (await original.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var originalLine = Assert.Single(originalData.GetProperty("lines").EnumerateArray());
        Assert.Equal("first attempt line", originalLine.GetProperty("text").GetString());

        // The newer attempt is addressable but has no retained lines yet; that
        // is an empty page, not a redirect to the older attempt's lines.
        using var retry = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.2/logs?workflowRunId={workflowRunId}");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryData = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Empty(retryData.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task GetEndpoint_RejectsRunOutsideTheAddressedIssueOrProject()
    {
        var projectId = await CreateProjectAsync("tasklog-scope");
        var issueNumber = await CreateIssueAsync(projectId, "scope check");

        var otherProjectRunId = $"wr_tasklog_other_project_{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(
            otherProjectRunId, "build.1", $"work-{Guid.NewGuid():N}", "project-other", issueNumber);

        var otherIssueRunId = $"wr_tasklog_other_issue_{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(
            otherIssueRunId, "build.1", $"work-{Guid.NewGuid():N}", projectId, issueNumber + 1);

        var unknownRunId = $"wr_tasklog_unknown_{Guid.NewGuid():N}";

        foreach (var runId in new[] { otherProjectRunId, otherIssueRunId, unknownRunId })
        {
            using var response = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?workflowRunId={runId}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetEndpoint_RejectsAttemptMissingFromTheAddressedRun()
    {
        var projectId = await CreateProjectAsync("tasklog-attempt");
        var issueNumber = await CreateIssueAsync(projectId, "attempt check");
        var workflowRunId = $"wr_tasklog_attempt_{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(
            workflowRunId, "build.1", $"work-{Guid.NewGuid():N}", projectId, issueNumber);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.99/logs?workflowRunId={workflowRunId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
