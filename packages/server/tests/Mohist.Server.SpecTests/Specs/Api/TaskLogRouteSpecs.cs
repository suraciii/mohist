using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

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

    /// <summary>
    /// Persists a workflow run that contains a single task with a
    /// known <c>TaskRun.Id</c> and <c>WorkId</c>. The run's
    /// <c>metadata.annotations.projectId</c> is wired so the GET
    /// endpoint can resolve it via the issue's workflow run id.
    /// </summary>
    private async Task<(string workflowRunId, string workId)> SeedWorkflowRunAsync(
        string projectId,
        string taskId,
        string workId)
    {
        var workflowRunId = $"wr_tasklog_spec_{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, taskId, workId, projectId);
        return (workflowRunId, workId);
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
                Annotations: projectId is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal) { ["projectId"] = projectId }),
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
        // WorkflowRunId is a computed column sourced from the State
        // JSON, so the binding is done by mutating State directly.
        // json_quote() produces a valid JSON string literal from the
        // SQL parameter so json_set accepts it.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE Issues SET State = json_set(State, '$.workflowRunId', json_quote({0})) WHERE ProjectId = {1} AND Number = {2}",
            workflowRunId, projectId, issueNumber);
    }

    private async Task SeedRunnerWorkAsync(string runnerId, string ownerKind, string ownerId, string workId, string status = "outstanding")
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.RunnerWorks.Add(new RunnerWorkRow
        {
            RunnerId = runnerId,
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            WorkId = workId,
            TakenAt = _fixture.TimeProvider.GetUtcNow(),
            Status = status,
        });
        await db.SaveChangesAsync();
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

    private object OneLineBody(string text = "line") => new
    {
        entries = new[] { new { seq = 1L, timestamp = _fixture.TimeProvider.GetUtcNow(), source = "action", text } },
        truncated = false,
    };

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_StoresEntriesAndReturnsAcceptedCount()
    {
        var workflowRunId = $"wr-tasklog-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, "task-1", workId);
        var now = _fixture.TimeProvider.GetUtcNow();
        var body = new
        {
            entries = new[]
            {
                new { seq = 1L, timestamp = now, source = "workspace-prep", text = "Cloning repo..." },
                new { seq = 2L, timestamp = now.AddSeconds(1), source = "branch-check", text = "Stable" },
            },
            truncated = false,
        };

        using var response = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(2, data.GetProperty("accepted").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.TaskLogEntries.AsNoTracking()
            .Where(e => e.OwnerKind == "workflow" && e.OwnerId == workflowRunId && e.WorkId == workId)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Cloning repo...", rows[0].Text);
        Assert.Equal("Stable", rows[1].Text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_AgentJobRoute_StoresUnderAgentJobOwnerKind()
    {
        var agentJobId = $"aj-tasklog-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedRunnerWorkAsync(RunnerId, "agent-job", agentJobId, workId);

        using var response = await PostTaskLogAsync(
            $"/api/agent-jobs/{agentJobId}/work/{workId}/task-log",
            new
            {
                entries = new[] { new { seq = 1L, timestamp = _fixture.TimeProvider.GetUtcNow(), source = "action", text = "agent-job line" } },
                truncated = false,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.TaskLogEntries.AsNoTracking()
            .Where(e => e.OwnerKind == "agent-job" && e.OwnerId == agentJobId && e.WorkId == workId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("agent-job line", rows[0].Text);

        // Same work id under a different owner kind must not collide.
        var workflowCollision = await db.TaskLogEntries.AsNoTracking()
            .CountAsync(e => e.WorkId == workId && e.OwnerKind == "workflow");
        Assert.Equal(0, workflowCollision);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

        var stateAfter = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .Select(r => r.State)
            .SingleAsync();
        Assert.Equal(stateBefore, stateAfter);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_UnknownOwnerWork_ReturnsNotFoundAndDoesNotPersist()
    {
        var workflowRunId = $"wr-tasklog-missing-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        using var response = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            OneLineBody("forged"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var count = await db.TaskLogEntries.AsNoTracking()
            .CountAsync(e => e.OwnerKind == "workflow" && e.OwnerId == workflowRunId && e.WorkId == workId);
        Assert.Equal(0, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_CrossOwnerOverwrite_IsRejectedAndExistingLogStaysIntact()
    {
        var allowedOwnerId = $"wr-tasklog-owner-{Guid.NewGuid():N}";
        var forgedOwnerId = $"wr-tasklog-forged-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(allowedOwnerId, "task-1", workId);

        using (var accepted = await PostTaskLogAsync(
            $"/api/workflow-runs/{allowedOwnerId}/work/{workId}/task-log",
            OneLineBody("original")))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var forged = await PostTaskLogAsync(
            $"/api/workflow-runs/{forgedOwnerId}/work/{workId}/task-log",
            OneLineBody("forged"));

        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var original = await db.TaskLogEntries.AsNoTracking()
            .SingleAsync(e => e.OwnerKind == "workflow" && e.OwnerId == allowedOwnerId && e.WorkId == workId);
        Assert.Equal("original", original.Text);
        var forgedCount = await db.TaskLogEntries.AsNoTracking()
            .CountAsync(e => e.OwnerKind == "workflow" && e.OwnerId == forgedOwnerId && e.WorkId == workId);
        Assert.Equal(0, forgedCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UploadEndpoint_SecondRunnerCannotReplaceAnotherRunnersLog()
    {
        var workflowRunId = $"wr-tasklog-runner-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        await SeedActiveWorkflowRunAsync(workflowRunId, "task-1", workId);

        using (var accepted = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            OneLineBody("from assigned runner")))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var rejected = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            OneLineBody("from second runner"),
            runnerId: $"runner-forged-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.TaskLogEntries.AsNoTracking()
            .SingleAsync(e => e.OwnerKind == "workflow" && e.OwnerId == workflowRunId && e.WorkId == workId);
        Assert.Equal("from assigned runner", row.Text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetEndpoint_TaskWithoutCapturedLines_ReturnsEmptyPage()
    {
        var projectId = await CreateProjectAsync("tasklog-empty");
        var issueNumber = await CreateIssueAsync(projectId, "no logs");
        var (workflowRunId, workId) = await SeedWorkflowRunAsync(projectId, "build.1", workId: $"work-{Guid.NewGuid():N}");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetEndpoint_ReturnsPaginatedLinesInSeqOrder()
    {
        var projectId = await CreateProjectAsync("tasklog-page");
        var issueNumber = await CreateIssueAsync(projectId, "with logs");
        var (workflowRunId, workId) = await SeedWorkflowRunAsync(projectId, "build.1", workId: $"work-{Guid.NewGuid():N}");
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, workflowRunId);

        var now = _fixture.TimeProvider.GetUtcNow();
        var entries = Enumerable.Range(1, 5).Select(seq => new
        {
            seq = (long)seq,
            timestamp = now.AddSeconds(seq),
            source = "action",
            text = $"line {seq}",
        }).ToArray();

        using (var upload = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            new { entries, truncated = false }))
        {
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0].GetProperty("seq").GetInt64());
        Assert.Equal(2, lines[1].GetProperty("seq").GetInt64());
        Assert.Equal("line 1", lines[0].GetProperty("text").GetString());
        var cursor = data.GetProperty("nextCursor").GetInt64();
        Assert.Equal(2L, cursor);

        using var second = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?cursor={cursor}&limit=2");
        var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var secondLines = secondData.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, secondLines.Count);
        Assert.Equal(3, secondLines[0].GetProperty("seq").GetInt64());
        Assert.Equal(4, secondLines[1].GetProperty("seq").GetInt64());
        var secondCursor = secondData.GetProperty("nextCursor").GetInt64();
        Assert.Equal(4L, secondCursor);

        using var final = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs?cursor={secondCursor}&limit=2");
        var finalData = (await final.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var finalLines = finalData.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(finalLines);
        Assert.Equal(5, finalLines[0].GetProperty("seq").GetInt64());
        Assert.Equal(JsonValueKind.Null, finalData.GetProperty("nextCursor").ValueKind);
        Assert.False(finalData.GetProperty("truncated").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetEndpoint_TruncatedBatch_ReportsTruncatedTrueAndRetainedTail()
    {
        var projectId = await CreateProjectAsync("tasklog-trunc");
        var issueNumber = await CreateIssueAsync(projectId, "truncated logs");
        var (workflowRunId, workId) = await SeedWorkflowRunAsync(projectId, "build.1", workId: $"work-{Guid.NewGuid():N}");
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, workflowRunId);

        var now = _fixture.TimeProvider.GetUtcNow();
        var entries = Enumerable.Range(1, 3).Select(seq => new
        {
            seq = (long)seq,
            timestamp = now.AddSeconds(seq),
            source = "action",
            text = $"tail {seq}",
        }).ToArray();

        using (var upload = await PostTaskLogAsync(
            $"/api/workflow-runs/{workflowRunId}/work/{workId}/task-log",
            new { entries, truncated = true }))
        {
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("tail 3", lines[^1].GetProperty("text").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetEndpoint_UnknownTaskId_ReturnsEmptyPage()
    {
        var projectId = await CreateProjectAsync("tasklog-unknown");
        var issueNumber = await CreateIssueAsync(projectId, "unknown task");
        var (workflowRunId, workId) = await SeedWorkflowRunAsync(projectId, "build.1", workId: $"work-{Guid.NewGuid():N}");
        await BindIssueToWorkflowRunAsync(projectId, issueNumber, workflowRunId);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/missing.1/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Empty(data.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetEndpoint_IssueWithoutWorkflowRun_ReturnsEmptyPage()
    {
        var projectId = await CreateProjectAsync("tasklog-norun");
        var issueNumber = await CreateIssueAsync(projectId, "no run yet");

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/tasks/build.1/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Empty(data.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }
}
