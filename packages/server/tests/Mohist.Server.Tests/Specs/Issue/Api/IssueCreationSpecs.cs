using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueCreationSpecs
{
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public IssueCreationSpecs(MohistIntegrationFixture fixture)
    {
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        var project = await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}");
        await projectGrain.AddRepositoryAsync("main", $"file://{Guid.NewGuid():N}", "main");
        return project;
    }

private async Task<IssueInfo> CreateIssueAsync(string projectId, string title, string? body = null, IReadOnlyDictionary<string, string>? labels = null, string? priority = null, string? risk = null, bool isDraft = false)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, title, body, labels, priority, null, issueId, risk, isDraft);
        return (await GetIssueInfoAsync(projectId, number))!;
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetInfoAsync(projectId, number);
    }

    private async Task<WorkflowBacklogState?> LoadBacklogStateAsync(string projectId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.BacklogStates.FindAsync(projectId);
        return row is null ? null : JsonSerializer.Deserialize<WorkflowBacklogState>(row.State);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> GetWorkflowEventsAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return (await events.ListAsync(workflowRunId)).ToList();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_ReturnsInfoWithNumber()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Test issue", "body");

        Assert.Equal(1, issue.Number);
        Assert.Equal("Test issue", issue.Title);
        Assert.Equal("body", issue.Body);
        Assert.Equal("backlog", issue.Status);
        Assert.Equal("active", issue.Health);
        Assert.Equal(project.Id, issue.ProjectId);
        Assert.StartsWith("issue_", issue.Id);
        Assert.Equal("mohist/default", issue.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_DefaultWorkflowProfile_ComesFromDefaultProfile()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Default profile");

        Assert.Equal("mohist/default", issue.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkflow_WithProjectContext_DispatchesProjectVariables()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Context");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project"));

        Assert.StartsWith("wr_", wrId);
        Assert.DoesNotContain(project.Id, wrId);
        Assert.False(wrId.EndsWith($"_{created.Number}", StringComparison.Ordinal));

        var runnerId = $"runner-variable-test-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));
        var work = await runner.PollAsync();

        Assert.NotNull(work);
        Assert.NotNull(work.Variables);
        Assert.Contains("My Project", work.Variables);
        Assert.Contains("repository", work.Variables);
        Assert.Contains("main", work.Variables);
        Assert.Contains("workspace", work.Variables);
        Assert.DoesNotContain("project.path", work.Variables);
        Assert.DoesNotContain("project.baseBranch", work.Variables);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkflow_UsesProjectDefaultTemplate()
    {
        var project = await SetupProjectAsync();
        using (var scope = _services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<ProjectWorkflowProfileManager>();
            await profiles.CreateTemplateAsync(project.Id, """
                id: project-custom
                stages:
                  - stage: custom-stage
                    tasks:
                      - id: custom-task
                        title: Custom task
                        uses: spec/task
                        with:
                          prompt: Project template prompt
                    checks: []
                """);
            await profiles.SetDefaultTemplateAsync(project.Id, "project-custom");
        }

        var created = await CreateIssueAsync(project.Id, "Project template issue");
        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        var wrId = await grain.StartWorkAsync();

        var runnerId = $"runner-template-test-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));
        var work = await runner.PollAsync();

        Assert.NotNull(work);
        Assert.Equal(wrId, work.WorkflowRunId);
        Assert.Equal("custom-stage", work.Stage);
        Assert.StartsWith("custom-task.", work.WorkId);
        Assert.Contains("Project template prompt", work.With);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_SequentialNumbers()
    {
        var project = await SetupProjectAsync();

        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithLabelsAndPriority()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(
            project.Id,
            "Labeled",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["priority"] = "p0",
            },
            priority: "p0");

        Assert.Equal("frontend", issue.Labels["stream"]);
        Assert.Equal("p0", issue.Labels["priority"]);
        Assert.Equal("p0", issue.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Querier_ReturnsIssueInfo()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Info test", "desc");

        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal(created.Number, info.Number);
        Assert.Equal("Info test", info.Title);
        Assert.Equal("desc", info.Body);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Update_ChangesTitleAndBody()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Original", "old body");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        await grain.UpdateAsync("Updated", "new body");
        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal("Updated", info.Title);
        Assert.Equal("new body", info.Body);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Close_ActiveIssue_CancelsIssueWithoutRewritingLifecycleToWorkflowStage()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Closable");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        var wrId = await grain.StartWorkAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("test-stop");

        await grain.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info.Status);
        Assert.Equal("cancelled", info.Health);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Cancel_ActiveIssue_ClearsBacklogLease_AndRunnerAssignment()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Cancelable");

        var issue = _grains.GetGrain<IIssueGrain>(created.Id);
        var workflowRunId = await issue.StartWorkAsync(new WorkflowProjectContext(project.Id, project.Name, RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));

        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));

        WorkDispatch? dispatch = null;
        for (var attempt = 0; attempt < 100 && dispatch is null; attempt++)
        {
            dispatch = await runner.PollAsync();
            if (dispatch is null)
                await Task.Delay(20);
        }

        Assert.NotNull(dispatch);

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await wfGrain.StopAsync("user-cancel");

        await issue.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info!.Status);
        Assert.Equal("cancelled", info.Health);

        var backlog = await LoadBacklogStateAsync(project.Id);
        Assert.True(backlog is null || (!backlog.Waiting.Contains(workflowRunId) && !backlog.All.Contains(workflowRunId)));
        Assert.Null(await runner.PollAsync());

        var events = await GetWorkflowEventsAsync(workflowRunId);
        Assert.Single(events, e => e.Envelope.Type == "com.mohist.workflow.run.stopped");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Hydrate_Duplicate_Throws()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Dup");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(project.Id, 999, "dup", null, null, null, null));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueWorkflowStatus_ProjectsDefaultChangeDirOutsideWorkflowStatus()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Add Search");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", RepositoryBaseBranch: "main"));

        var status = await grain.GetWorkflowStatusAsync();

        Assert.NotNull(status);
        Assert.Equal($"openspec/changes/issue-{created.Number}", status.ChangeDir);
        Assert.DoesNotContain("ChangeDir", typeof(Mohist.Server.Workflow.Services.WorkflowStatusView).GetProperties().Select(p => p.Name));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DifferentProjects_IndependentNumbering()
    {
        var project1 = await SetupProjectAsync();
        var project2 = await SetupProjectAsync();

        var issue1 = await CreateIssueAsync(project1.Id, "P1-Issue");
        var issue2 = await CreateIssueAsync(project2.Id, "P2-Issue");

        Assert.Equal(1, issue1.Number);
        Assert.Equal(1, issue2.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task AddPrerequisite_StartReadinessAndStartGateComeFromIssueGrain()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var grain = _grains.GetGrain<IIssueGrain>(dependent.Id);

        await grain.AddPrerequisiteAsync(prereq.Number);
        var info = await GetIssueInfoAsync(project.Id, dependent.Number);
        var readiness = await grain.GetStartReadinessAsync();

        Assert.NotNull(info);
        Assert.Contains(prereq.Number, info.PrerequisiteNumbers);
        Assert.False(readiness.CanStart);
        var waiting = Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(readiness.Blocker);
        Assert.Equal(prereq.Number, waiting.Issue.Number);
        await Assert.ThrowsAsync<IssueStartBlockedException>(() => grain.StartWorkAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletedPrerequisite_AllowsDependentIssueToStart()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var prereqGrain = _grains.GetGrain<IIssueGrain>(prereq.Id);
        var dependentGrain = _grains.GetGrain<IIssueGrain>(dependent.Id);

        await dependentGrain.AddPrerequisiteAsync(prereq.Number);
        var prereqRunId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", RepositoryBaseBranch: "main"));
        await prereqGrain.CompleteWorkAsync(prereqRunId);

        var readiness = await dependentGrain.GetStartReadinessAsync();

        Assert.True(readiness.CanStart);
        Assert.Null(readiness.Blocker);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithRisk_PersistsAndReturnsIt()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Risked", risk: "high");

        Assert.Equal("high", issue.Risk);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithoutRisk_ReturnsNull()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "NoRisk");

        Assert.Null(issue.Risk);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReadModel_IncludesRisk_AfterCreate()
    {
        var project = await SetupProjectAsync();

        var number = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(project.Id, number, "Medium risk", body: null, labels: null, priority: null, repositoryRef: null, issueId: issueId, risk: "medium");

        using var scope = _services.CreateScope();
        var issuesQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var readModel = await issuesQuery.GetAsync(project.Id, number, project);

        Assert.NotNull(readModel);
        Assert.Equal("medium", readModel!.Risk);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithInvalidRisk_Throws()
    {
        var project = await SetupProjectAsync();
        var number = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.CreateAsync(project.Id, number, "Bad", null, null, null, null, issueId, "unknown"));
    }

}
