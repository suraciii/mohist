using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssueFeedbackApiSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public IssueFeedbackApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateFeedback_AtAwaitingApprovalStage_ResumesStageAndPersistsFeedback()
    {
        var (project, issueNumber, issueId, wrId) = await SeedAwaitingApprovalIssueAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan", body = "add a quick start section" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<FeedbackEnvelopeDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        var created = envelope.Data!;
        Assert.StartsWith("fb_", created.Id);
        Assert.Equal(issueNumber, created.IssueNumber);
        Assert.Equal(wrId, created.WorkflowRunId);
        Assert.Equal("plan", created.Stage);
        Assert.Equal("add a quick start section", created.Body);
        Assert.Equal("open", created.Status);

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        Assert.Single(run!.Feedback);
        // After RequestChanges/AddRuntimeTask, the legacy approval is
        // replaced with a feedback task the runner can pick up. The
        // seed does not bind a runner, so the new state machine lands
        // the run on Pending (started, has dispatchable work, no
        // assigned runner) — assignment pool will pick it up.
        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        var current = run.Stages.First(s => s.Id == "plan");
        Assert.Equal(StageRunStatus.Running, current.Status);
        Assert.Null(current.ApprovalStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateFeedback_OnNonAwaitingStage_Returns409()
    {
        var (project, issueNumber, _, _) = await SeedNonAwaitingApprovalIssueAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "build", body = "should fail" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListFeedback_ReturnsAllFeedbackForRun_OrderedByCreatedAtDesc()
    {
        var (project, issueNumber, _, wrId) = await SeedAwaitingApprovalIssueAsync();

        // Inject two feedback records directly into the workflow run state with
        // distinct createdAt timestamps. The DB is the source of truth for the
        // list query, so this avoids needing to drive the workflow grain back to
        // approval multiple times.
        var baseTime = TestTime.UtcNow.AddMinutes(-2);
        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: $"fb_{Guid.NewGuid():N}",
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "first feedback",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: baseTime));
        run.Feedback.Add(new ApprovalFeedback(
            Id: $"fb_{Guid.NewGuid():N}",
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "second feedback",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: baseTime.AddMinutes(1)));
        await SaveWorkflowRunAsync(wrId, run);

        var list = await _client.GetDataAsync<FeedbackDto[]>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback");

        Assert.Equal(2, list.Length);
        Assert.Equal("second feedback", list[0].Body);
        Assert.Equal("first feedback", list[1].Body);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListFeedback_WithStageFilter_ReturnsOnlyMatchingStage()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan", body = "plan feedback" });

        var planOnly = await _client.GetDataAsync<FeedbackDto[]>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback?stage=plan");
        Assert.Single(planOnly);
        Assert.Equal("plan", planOnly[0].Stage);

        var checkOnly = await _client.GetDataAsync<FeedbackDto[]>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback?stage=check");
        Assert.Empty(checkOnly);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListFeedback_WithoutAnyFeedback_ReturnsEmptyArray()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var list = await _client.GetDataAsync<FeedbackDto[]>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback");

        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetFeedback_ReturnsFullFeedbackRecord()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var created = await _client.PostDataAsync<FeedbackDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan", body = "show me the body" });

        var single = await _client.GetDataAsync<FeedbackDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback/{created.Id}");

        Assert.Equal(created.Id, single.Id);
        Assert.Equal(issueNumber, single.IssueNumber);
        Assert.Equal("plan", single.Stage);
        Assert.Equal("show me the body", single.Body);
        Assert.Equal("open", single.Status);
        Assert.Null(single.Resolution);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetFeedback_JsonWireShape_ExposesNestedResolutionObject()
    {
        var (project, issueNumber, _, wrId) = await SeedAwaitingApprovalIssueAsync();
        var feedbackId = $"fb_{Guid.NewGuid():N}";

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: feedbackId,
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "live server shape",
            Status: ApprovalFeedbackStatus.Resolved,
            CreatedAt: TestTime.UtcNow.AddMinutes(-5),
            ResolutionTaskId: "apply-feedback.1",
            ResolvedAt: TestTime.UtcNow.AddMinutes(-1),
            ResolutionSummary: "Addressed live"));
        await SaveWorkflowRunAsync(wrId, run);

        var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback/{feedbackId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        // The spec mandates a nested `resolution` object that is null when open.
        Assert.True(data.TryGetProperty("resolution", out var resolution),
            "feedback record must include a top-level 'resolution' field");
        Assert.Equal(JsonValueKind.Object, resolution.ValueKind);

        // Top-level fields the spec mandates (id, issueNumber, workflowRunId, stage, status, body, createdAt)
        // must be present; the resolution sub-fields must NOT be flattened to the top level.
        Assert.Equal(feedbackId, data.GetProperty("id").GetString());
        Assert.Equal(issueNumber, data.GetProperty("issueNumber").GetInt32());
        Assert.Equal(wrId, data.GetProperty("workflowRunId").GetString());
        Assert.Equal("plan", data.GetProperty("stage").GetString());
        Assert.Equal("resolved", data.GetProperty("status").GetString());
        Assert.Equal("live server shape", data.GetProperty("body").GetString());
        Assert.False(data.TryGetProperty("resolutionSummary", out _),
            "flat 'resolutionSummary' must not appear at top level; it belongs under 'resolution'");
        Assert.False(data.TryGetProperty("resolvedAt", out _),
            "flat 'resolvedAt' must not appear at top level; it belongs under 'resolution'");

        // The nested resolution carries resolutionSummary, resolvedAt, and resolutionTaskId when present.
        Assert.Equal("apply-feedback.1", resolution.GetProperty("resolutionTaskId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(resolution.GetProperty("resolvedAt").GetString()));
        Assert.Equal("Addressed live", resolution.GetProperty("resolutionSummary").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListFeedback_JsonWireShape_ExposesNestedResolutionObject()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var created = await _client.PostDataAsync<FeedbackDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan", body = "live list shape" });

        var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        var entry = data.EnumerateArray().Single(e => e.GetProperty("id").GetString() == created.Id);

        Assert.Equal(issueNumber, entry.GetProperty("issueNumber").GetInt32());
        Assert.False(entry.TryGetProperty("resolution", out _),
            "list entries must omit 'resolution' when null (per WhenWritingNull contract)");
        Assert.False(entry.TryGetProperty("resolutionSummary", out _),
            "list entries must not flatten 'resolutionSummary' to the top level");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetFeedback_UnknownId_Returns404()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback/fb_doesnotexist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueDetail_IncludesFeedbackArray()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/issues/{issueNumber}/feedback",
            new { stage = "plan", body = "detailed feedback" });

        var detail = await _client.GetDataAsync<IssueDetailDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}");

        Assert.NotNull(detail.Feedback);
        Assert.Single(detail.Feedback);
        Assert.Equal("plan", detail.Feedback[0].Stage);
        Assert.Equal("detailed feedback", detail.Feedback[0].Body);
        Assert.Equal("open", detail.Feedback[0].Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueDetail_WithoutFeedback_IncludesEmptyFeedbackArray()
    {
        var (project, issueNumber, _, _) = await SeedAwaitingApprovalIssueAsync();

        var detail = await _client.GetDataAsync<IssueDetailDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}");

        Assert.NotNull(detail.Feedback);
        Assert.Empty(detail.Feedback);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueDetail_FeedbackArrayOrderedByCreatedAtDesc()
    {
        var (project, issueNumber, _, wrId) = await SeedAwaitingApprovalIssueAsync();
        var baseTime = TestTime.UtcNow.AddMinutes(-10);

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: "fb_first",
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "first",
            Status: ApprovalFeedbackStatus.Resolved,
            CreatedAt: baseTime));
        run.Feedback.Add(new ApprovalFeedback(
            Id: "fb_middle",
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "middle",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: baseTime.AddMinutes(2)));
        run.Feedback.Add(new ApprovalFeedback(
            Id: "fb_last",
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "last",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: baseTime.AddMinutes(5)));
        await SaveWorkflowRunAsync(wrId, run);

        var detail = await _client.GetDataAsync<IssueDetailDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}");

        Assert.Equal(3, detail.Feedback.Length);
        Assert.Equal("fb_last", detail.Feedback[0].Id);
        Assert.Equal("fb_middle", detail.Feedback[1].Id);
        Assert.Equal("fb_first", detail.Feedback[2].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueDetail_ResolvesFeedbackRecords()
    {
        var (project, issueNumber, _, wrId) = await SeedAwaitingApprovalIssueAsync();
        var feedbackId = $"fb_{Guid.NewGuid():N}";

        // Inject a resolved feedback record directly into the workflow run state
        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: feedbackId,
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "old feedback",
            Status: ApprovalFeedbackStatus.Resolved,
            CreatedAt: TestTime.UtcNow.AddMinutes(-5),
            ResolutionTaskId: "apply-feedback.1",
            ResolvedAt: TestTime.UtcNow,
            ResolutionSummary: "Addressed"));
        await SaveWorkflowRunAsync(wrId, run);

        var detail = await _client.GetDataAsync<IssueDetailDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}");

        var resolved = Assert.Single(detail.Feedback, f => f.Id == feedbackId);
        Assert.Equal("resolved", resolved.Status);
        Assert.NotNull(resolved.Resolution);
        Assert.Equal("Addressed", resolved.Resolution!.ResolutionSummary);
        Assert.Equal("apply-feedback.1", resolved.Resolution.ResolutionTaskId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageState_IncludesFeedbackScopedToStage()
    {
        var (project, issueNumber, issueId, wrId) = await SeedAwaitingApprovalIssueAsync();
        var planFeedbackId = $"fb_{Guid.NewGuid():N}";

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: planFeedbackId,
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "plan feedback",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: TestTime.UtcNow.AddMinutes(-2)));
        run.Feedback.Add(new ApprovalFeedback(
            Id: $"fb_{Guid.NewGuid():N}",
            WorkflowRunId: wrId,
            Stage: "check",
            Body: "check feedback",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: TestTime.UtcNow.AddMinutes(-1)));
        await SaveWorkflowRunAsync(wrId, run);
        await _grains.GetGrain<IIssueGrain>(issueId).DeactivateForTestAsync();

        var status = await _client.GetDataAsync<IssueWorkflowStatusDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/workflow/status");

        Assert.NotNull(status.Workflow);
        var planStage = Assert.Single(status.Workflow!.Stages, s => s.Stage == "plan");
        Assert.NotNull(planStage.Feedback);
        var stageFeedback = Assert.Single(planStage.Feedback!, f => f.Id == planFeedbackId);
        Assert.Equal("plan feedback", stageFeedback.Body);
        Assert.Equal("open", stageFeedback.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageState_DistinguishesOpenAndResolvedFeedback()
    {
        var (project, issueNumber, issueId, wrId) = await SeedAwaitingApprovalIssueAsync();
        var openId = $"fb_{Guid.NewGuid():N}";
        var resolvedId = $"fb_{Guid.NewGuid():N}";

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: openId,
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "still needs work",
            Status: ApprovalFeedbackStatus.Open,
            CreatedAt: TestTime.UtcNow.AddMinutes(-3)));
        run.Feedback.Add(new ApprovalFeedback(
            Id: resolvedId,
            WorkflowRunId: wrId,
            Stage: "plan",
            Body: "completed feedback",
            Status: ApprovalFeedbackStatus.Resolved,
            CreatedAt: TestTime.UtcNow.AddMinutes(-5),
            ResolutionTaskId: "apply-feedback.1",
            ResolvedAt: TestTime.UtcNow.AddMinutes(-1),
            ResolutionSummary: "Done"));
        await SaveWorkflowRunAsync(wrId, run);
        await _grains.GetGrain<IIssueGrain>(issueId).DeactivateForTestAsync();

        var status = await _client.GetDataAsync<IssueWorkflowStatusDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/workflow/status");

        var planStage = Assert.Single(status.Workflow!.Stages, s => s.Stage == "plan");
        Assert.NotNull(planStage.Feedback);
        Assert.Equal(2, planStage.Feedback!.Length);

        var open = Assert.Single(planStage.Feedback, f => f.Id == openId);
        Assert.Equal("open", open.Status);
        Assert.Null(open.Resolution);

        var resolved = Assert.Single(planStage.Feedback, f => f.Id == resolvedId);
        Assert.Equal("resolved", resolved.Status);
        Assert.NotNull(resolved.Resolution);
        Assert.Equal("Done", resolved.Resolution!.ResolutionSummary);
        Assert.Equal("apply-feedback.1", resolved.Resolution.ResolutionTaskId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageState_WithoutFeedback_OmitsOrEmptyFeedbackArray()
    {
        var (project, issueNumber, issueId, _) = await SeedAwaitingApprovalIssueAsync();
        await _grains.GetGrain<IIssueGrain>(issueId).DeactivateForTestAsync();

        var status = await _client.GetDataAsync<IssueWorkflowStatusDto>(
            $"/api/projects/{project.Id}/issues/{issueNumber}/workflow/status");

        Assert.NotNull(status.Workflow);
        var planStage = Assert.Single(status.Workflow!.Stages, s => s.Stage == "plan");
        if (planStage.Feedback is not null)
            Assert.Empty(planStage.Feedback);
    }

    private async Task<(ProjectInfo Project, int IssueNumber, string IssueId, string WorkflowRunId)>
        SeedAwaitingApprovalIssueAsync()
    {
        var project = await CreateProjectAsync();
        var (issueId, issueNumber) = await CreateIssueAsync(project.Id, "Feedback API test");
        var wrId = $"wr_{Guid.NewGuid():N}";
        await SeedWorkflowRunAsync(wrId, project.Id, issueId, issueNumber, stage: "plan", awaitingApproval: true);
        await AttachWorkflowRunToIssueAsync(project.Id, issueNumber, wrId);
        return (project, issueNumber, issueId, wrId);
    }

    private async Task<(ProjectInfo Project, int IssueNumber, string IssueId, string WorkflowRunId)>
        SeedNonAwaitingApprovalIssueAsync()
    {
        var project = await CreateProjectAsync();
        var (issueId, issueNumber) = await CreateIssueAsync(project.Id, "Non-approval feedback test");
        var wrId = $"wr_{Guid.NewGuid():N}";
        await SeedWorkflowRunAsync(wrId, project.Id, issueId, issueNumber, stage: "plan", awaitingApproval: false);
        await AttachWorkflowRunToIssueAsync(project.Id, issueNumber, wrId);
        return (project, issueNumber, issueId, wrId);
    }

    private async Task<ProjectInfo> CreateProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        return await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "placeholder", GitUrl = "git@example.com:placeholder.git", BaseBranch = "main", IsDefault = true });
    }

    private async Task<(string IssueId, int Number)> CreateIssueAsync(string projectId, string title)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, title, null, null, null, null, issueId);
        return (issueId, number);
    }

    private async Task AttachWorkflowRunToIssueAsync(string projectId, int issueNumber, string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.Issues
            .Where(r => r.ProjectId == projectId && r.Number == issueNumber)
            .FirstOrDefaultAsync();
        Assert.NotNull(row);
        var issue = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Deserialize(row!.State);
        Assert.NotNull(issue);

        // The JSON has "workflowRunId" (the single canonical reference name)
        var json = row.State;
        using var doc = JsonDocument.Parse(json);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText())!;
        dict["workflowRunId"] = JsonSerializer.SerializeToElement(workflowRunId);
        dict["status"] = JsonSerializer.SerializeToElement("InProgress");
        row.State = JsonSerializer.Serialize(dict, ReadJsonOptions);
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowRunAsync(
        string wrId,
        string projectId,
        string issueId,
        int issueNumber,
        string stage,
        bool awaitingApproval)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

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
                annotations = new Dictionary<string, string>
                {
                    ["projectId"] = projectId,
                    ["issueId"] = issueId,
                    ["issueNumber"] = issueNumber.ToString(),
                },
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
            feedback = Array.Empty<object>(),
        };

        var row = existing ?? new WorkflowRunRow { WorkflowRunId = wrId };
        row.State = JsonSerializer.Serialize(state, ReadJsonOptions);

        if (existing is null)
        {
            db.WorkflowRuns.Add(row);
        }
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun?> LoadWorkflowRunAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        return row is null ? null : JsonSerializer.Deserialize<WorkflowRun>(row.State, ReadJsonOptions);
    }

    private async Task SaveWorkflowRunAsync(string workflowRunId, WorkflowRun run)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId);
        Assert.NotNull(row);
        row!.State = JsonSerializer.Serialize(run, ReadJsonOptions);
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);

    private sealed record FeedbackEnvelopeDto(bool Success, FeedbackDto? Data, string? Error = null);
    private sealed record FeedbackDto(
        string Id,
        int IssueNumber,
        string WorkflowRunId,
        string Stage,
        string Status,
        string Body,
        string CreatedAt,
        FeedbackResolutionDto? Resolution = null);
    private sealed record FeedbackResolutionDto(
        string? ResolutionTaskId,
        string? ResolvedAt,
        string? ResolutionSummary);

    private sealed record IssueDetailDto(
        int Number,
        string Id,
        string Title,
        string Status,
        FeedbackDto[] Feedback);

    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage, WorkflowStageDto[] Stages);
    private sealed record WorkflowStageDto(
        string Stage,
        string Status,
        StageFeedbackDto[]? Feedback = null);
    private sealed record StageFeedbackDto(
        string Id,
        string Body,
        string Status,
        string CreatedAt,
        StageFeedbackResolutionDto? Resolution = null);
    private sealed record StageFeedbackResolutionDto(
        string? ResolutionTaskId,
        string? ResolvedAt,
        string? ResolutionSummary);
}
