using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Issue.Grain;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public class IssueCreationGrainSpecs
{
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public IssueCreationGrainSpecs(ComponentWorkflowGrainFixture fixture)
    {
        _grains = fixture.Grains;
        _services = fixture.Cluster.GetSiloServiceProvider(null);
        _connectionString = fixture.ConnectionString;
    }

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        var project = await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "main",
            GitUrl = "git@example.com:main.git",
            BaseBranch = "main",
            IsDefault = true,
        }, "git diff --check");
        return project;
    }

    private async Task<IssueInfo> CreateIssueAsync(string projectId, string title, string? body = null, IReadOnlyDictionary<string, string>? labels = null, string? priority = null, string? risk = null, bool isDraft = false, int[]? prerequisiteNumbers = null)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = IssueGrain(projectId, number);
        await grain.CreateAsync(projectId, number, title, body, labels, priority, null, risk, isDraft, null, null, prerequisiteNumbers);
        return (await GetIssueInfoAsync(projectId, number))!;
    }

    private IIssueGrain IssueGrain(string projectId, int number) =>
        _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetInfoAsync(projectId, number);
    }

    private async Task<IssueReadModel?> GetIssueReadModelAsync(string projectId, int number)
    {
        var project = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetAsync(projectId, number, project);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> GetWorkflowEventsAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return (await events.ListAsync(workflowRunId)).ToList();
    }

    [Fact]
    public async Task StartWorkflow_WithProjectContext_DispatchesProjectVariables()
    {
        // The runner is a global resource. Prior tests in this collection
        // leave runners registered, which can race with this test's poll.
        var registry = _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);
        await WorkflowGrainTestHelpers.ClearBacklogAsync(_grains, _connectionString);

        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Context");

        var grain = IssueGrain(project.Id, created.Number);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project"));
        await _grains.GetGrain<IWorkflowGrain>(wrId).EnsureStartedAsync(new WorkflowIssueContext(project.Id, created.Number, null));

        Assert.StartsWith("wr_", wrId);
        Assert.DoesNotContain(project.Id, wrId);
        Assert.False(wrId.EndsWith($"_{created.Number}", StringComparison.Ordinal));

        var runnerId = $"runner-variable-test-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));

        var work = await PollAnyWorkAsync(runner);

        Assert.Equal(wrId, work.WorkflowRunId);
        Assert.NotNull(work.Variables);
         Assert.Contains("repository", work.Variables);
         Assert.Contains("workspace", work.Variables);
        Assert.DoesNotContain("project.path", work.Variables);
        Assert.DoesNotContain("project.baseBranch", work.Variables);
         using var variables = JsonDocument.Parse(work.Variables);
         var issue = variables.RootElement.GetProperty("issue");
         Assert.Equal(project.Id, issue.GetProperty("projectId").GetString());
         var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("main", repository.GetProperty("name").GetString());
        Assert.Equal("git@example.com:main.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("main", repository.GetProperty("baseBranch").GetString());

        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task Close_ActiveIssue_CancelsIssueWithoutRewritingLifecycleToWorkflowStage()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Closable");

        var grain = IssueGrain(project.Id, created.Number);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("test-stop");

        await grain.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info.Status);
        Assert.Equal("cancelled", info.Health);
    }

    [Fact]
    public async Task Cancel_ActiveIssue_RemovesWorkflowFromRunnerPoll()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Cancelable");

        var issue = IssueGrain(project.Id, created.Number);
        var workflowRunId = await issue.StartWorkAsync(new WorkflowProjectContext(project.Id, project.Name, RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));

        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));
        try
        {
            await PollAnyWorkAsync(runner);

            var wfGrain = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
            await wfGrain.StopAsync("user-cancel");

            await issue.CancelAsync();

            var info = await GetIssueInfoAsync(project.Id, created.Number);
            Assert.NotNull(info);
            Assert.Equal("cancelled", info!.Status);
            Assert.Equal("cancelled", info.Health);

            Assert.Null(await runner.PollAsync(_services));

            var events = await GetWorkflowEventsAsync(workflowRunId);
            Assert.Single(events, e => e.Envelope.Type == "com.mohist.workflow.run.stopped");
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task IssueWorkflowStatus_ProjectsDefaultChangeDirOutsideWorkflowStatus()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Add Search");

        var grain = IssueGrain(project.Id, created.Number);
        await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", RepositoryBaseBranch: "main"));

        var status = await grain.GetWorkflowStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("PLANS", status.ChangeDir);
    }

    [Fact]
    public async Task CompletedPrerequisite_AllowsDependentIssueToStart()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var prereqGrain = IssueGrain(project.Id, prereq.Number);
        var dependentGrain = IssueGrain(project.Id, dependent.Number);

        await dependentGrain.AddPrerequisiteAsync(prereq.Number);
        var prereqRunId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", RepositoryBaseBranch: "main"));
        await prereqGrain.CompleteWorkAsync(prereqRunId);

        var readiness = await dependentGrain.GetStartReadinessAsync();

        Assert.True(readiness.CanStart);
        Assert.Null(readiness.Blocker);
    }

    [Fact]
    public async Task CreateIssue_WithCompletedPrerequisite_MarksReadinessOpen()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Will complete");
        var prereqGrain = IssueGrain(project.Id, prereq.Number);

        // Drive the prerequisite to a completed workflow so the
        // read-model exposes it as Completed and the blocker is null.
        var wrId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(
            project.Id,
            project.Name,
            RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));
        await prereqGrain.CompleteWorkAsync(wrId);

        var dependent = await CreateIssueAsync(
            project.Id,
            "Dependent of completed prereq",
            prerequisiteNumbers: [prereq.Number]);

        var readModel = await GetIssueReadModelAsync(project.Id, dependent.Number);
        Assert.NotNull(readModel);
        Assert.True(readModel!.CanStart);
        Assert.Null(readModel.Blocker);
        var prereqSummary = readModel.Prereq.Single();
        Assert.True(prereqSummary.Completed);
    }

    private async Task<WorkDispatch> PollAnyWorkAsync(IRunnerGrain runner)
    {
        var work = await runner.PollAsync(_services);
        Assert.NotNull(work);
        return work;
    }

    private Task DispatchEventsAsync() =>
        _services.GetRequiredService<IEventDispatcher>().DrainAsync();

}
