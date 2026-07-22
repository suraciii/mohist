using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("IntegrationWorkflow")]
public class WorkflowRerunFromStageApiSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowRerunFromStageApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task RerunFromStage_EmptyStage_Returns400()
    {
        var (projectId, issueNumber, _, _) = await SeedInProgressIssueWithWorkflowRunAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RerunFromStage_NoWorkflowRun_Returns404()
    {
        var (projectId, issueNumber, _) = await SeedProjectWithIssueOnlyAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "build" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RerunFromStage_UnknownStage_Returns400()
    {
        var (projectId, issueNumber, issueKey, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "nope|still-safe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_stage", payload.GetProperty("code").GetString());
        Assert.Contains("nope|still-safe", payload.GetProperty("error").GetString());
        Assert.True(payload.TryGetProperty("details", out var details));
        Assert.True(details.TryGetProperty("eligibleStages", out var eligible));
        var stages = eligible.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("plan", stages);
    }

    [Fact]
    public async Task RerunFromStage_ValidRequest_Returns200()
    {
        var (projectId, issueNumber, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await DriveWorkflowToFailedBuildAsync(wrId, projectId);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "build" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal("build", run.CurrentStageId);
        Assert.Equal(2, run.Stages.Single(s => s.Id == "build").Attempt);
    }

    [Fact]
    public async Task RerunFromStage_NeverReachedStage_Returns400WithEligibleStages()
    {
        var (projectId, issueNumber, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "integrate" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stage_not_reached", payload.GetProperty("code").GetString());
        var stages = payload.GetProperty("details").GetProperty("eligibleStages")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("plan", stages);
        Assert.DoesNotContain("integrate", stages);

        var run = await LoadRunAsync(wrId);
        Assert.Equal("plan", run.CurrentStageId);
    }

    [Fact]
    public async Task RerunFromStage_ActiveWork_Returns409()
    {
        var (projectId, issueNumber, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await DriveWorkflowToRunningBuildAsync(wrId, projectId);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "plan" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active_work_in_range", payload.GetProperty("code").GetString());
        Assert.Contains("Stop or cancel", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RerunFromStage_TimelineOmitsInvalidatedTaskHistory()
    {
        var (projectId, issueNumber, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await DriveWorkflowToFailedBuildAsync(wrId, projectId);

        await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "build" });

        var events = await GetDataArrayAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/events?limit=200");

        var buildTaskCompleted = events.Where(e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.task.completed"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build").ToList();
        var buildTaskFailed = events.Where(e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.task.failed"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build").ToList();

        Assert.Empty(buildTaskCompleted);
        Assert.Empty(buildTaskFailed);
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.stage.started"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build");
    }

    [Fact]
    public async Task RerunFromStage_TimelineWithLowLimitStillOmitsInvalidatedTaskHistory()
    {
        var (projectId, issueNumber, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await DriveWorkflowToFailedBuildAsync(wrId, projectId);

        await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "build" });

        var events = await GetDataArrayAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/events?limit=4");

        Assert.DoesNotContain(events, IsInvalidatedBuildTaskEvent);
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.stage.started"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build");
    }

    [Fact]
    public async Task RerunFromStage_WorkflowRunEventsOmitInvalidatedTaskHistory()
    {
        var (projectId, _, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await DriveWorkflowToFailedBuildAsync(wrId, projectId);

        var workflowGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await workflowGrain.RerunFromStageAsync("build");

        var events = await GetDataArrayAsync($"/api/workflow-runs/{wrId}/events?limit=200");

        Assert.DoesNotContain(events, e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.task.completed"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build");
        Assert.DoesNotContain(events, e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.task.failed"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build");
    }

    [Fact]
    public async Task RerunFromStage_WorkflowRunEventsWithLowLimitStillOmitInvalidatedTaskHistory()
    {
        var (projectId, _, _, wrId) = await SeedInProgressIssueWithWorkflowRunAsync();
        await DriveWorkflowToFailedBuildAsync(wrId, projectId);

        var workflowGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await workflowGrain.RerunFromStageAsync("build");

        var events = await GetDataArrayAsync($"/api/workflow-runs/{wrId}/events?limit=4");

        Assert.DoesNotContain(events, IsInvalidatedBuildTaskEvent);
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "com.mohist.workflow.stage.started"
            && e.GetProperty("data").GetProperty("stage").GetString() == "build");
    }

    private static bool IsInvalidatedBuildTaskEvent(JsonElement e) =>
        e.GetProperty("type").GetString() is "com.mohist.workflow.task.completed" or "com.mohist.workflow.task.failed"
        && e.GetProperty("data").GetProperty("stage").GetString() == "build";

    private async Task<List<JsonElement>> GetDataArrayAsync(string path)
    {
        var envelope = await _client.GetFromJsonAsync<JsonElement>(path);
        Assert.True(envelope.TryGetProperty("data", out var data));
        return data.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<(string projectId, int issueNumber, string issueKey, string wrId)>
        SeedInProgressIssueWithWorkflowRunAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        await SeedWorkflowTemplateAsync(projectId);
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();
        return (projectId, issueNumber, issueKey, wrId);
    }

    private async Task DriveWorkflowToRunningBuildAsync(string wrId, string projectId)
    {
        var runnerId = await RegisterRunnerAsync(projectId);
        var (planTask, _) = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, wrId, planTask.WorkId, "completed");

        var (buildTask, _) = await PollWorkAsync(runnerId);
        Assert.Equal("build", buildTask.Stage);
    }

    private async Task DriveWorkflowToFailedBuildAsync(string wrId, string projectId)
    {
        var runnerId = await RegisterRunnerAsync(projectId);
        var (planTask, _) = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, wrId, planTask.WorkId, "completed");

        var (buildTask, _) = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, wrId, buildTask.WorkId, "failed");
    }

    private async Task<string> RegisterRunnerAsync(string projectId)
    {
        var runnerId = $"rerun-stage-runner-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
        return runnerId;
    }

    private async Task<(WorkDispatch Work, string RunnerId)> PollWorkAsync(string runnerId)
    {
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        var work = await TestWait.ForAsync(
            () => runner.PollAsync(_fixture.Services),
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive work");
        return (work!, runnerId);
    }

    private async Task ReportAsync(string runnerId, string wrId, string workId, string status)
    {
        // The runner grain no longer relays workflow reports; report direct to
        // the owning grain via the shared translator path (mirrors /report).
        await DispatchTestExtensions.ReportWorkflowDirectAsync(
            _grains, _services, runnerId, wrId, workId, new WorkResult(status));
    }

    private async Task<WorkflowRun> LoadRunAsync(string wrId)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(wrId) ?? throw new InvalidOperationException($"Workflow run '{wrId}' not found");
    }

    private async Task SeedWorkflowTemplateAsync(string projectId)
    {
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
            new StageDefinition("integrate", [new("merge", "Merge", "spec/task")], []),
        ]);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        const string templateId = "spec/workflow";
        var existingTemplate = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existingTemplate is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = WorkflowGrainTestHelpers.SerializeProfile(definition),
            });
        }
        else
        {
            existingTemplate.Template = WorkflowGrainTestHelpers.SerializeProfile(definition);
            existingTemplate.UpdatedAt = TestTime.UtcNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
            });
        }
        else
        {
            profile.DefaultTemplateId = templateId;
            profile.UpdatedAt = TestTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task<(string projectId, int issueNumber, string issueKey)>
        SeedProjectWithIssueOnlyAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        return (projectId, issueNumber, issueKey);
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"rerun-stage-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:test.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return (id, name);
    }

    private async Task<(string issueKey, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, "Rerun from stage test", null, null, null, isDraft: false);
        return (issueKey, number);
    }

    private Task DispatchEventsAsync() =>
        _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
}
