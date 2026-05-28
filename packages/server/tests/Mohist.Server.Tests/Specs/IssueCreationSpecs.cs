using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Queries;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueCreationSpecs
{
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public IssueCreationSpecs(MohistIntegrationFixture fixture)
    {
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        return await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", "/tmp/test", null);
    }

    private async Task<IssueInfo> CreateIssueAsync(string projectId, string title, string? body = null, string[]? labels = null, string? priority = null)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = _grains.GetGrain<IIssueGrain>($"{projectId}:{number}");
        await grain.CreateAsync(projectId, number, title, body, labels, priority, null);
        return (await GetIssueInfoAsync(projectId, number))!;
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQueryService>();
        return await issues.GetInfoAsync(projectId, number);
    }

    [Fact]
    public async Task CreateIssue_ReturnsInfoWithNumber()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Test issue", "body");

        Assert.Equal(1, issue.Number);
        Assert.Equal("Test issue", issue.Title);
        Assert.Equal("body", issue.Body);
        Assert.Equal("backlog", issue.Stage);
        Assert.Equal("active", issue.RuntimeStatus);
        Assert.Equal(project.Id, issue.ProjectId);
        Assert.StartsWith("issue_", issue.Id);
        Assert.Equal("mohist/default", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task CreateIssue_DefaultWorkflowProfile_ComesFromDefaultProfile()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Default profile");

        Assert.Equal("mohist/default", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task StartWorkflow_WithProjectContext_DispatchesProjectVariables()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Context");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", "/tmp/my-project", "trunk"));

        Assert.StartsWith("wr_", wrId);
        Assert.DoesNotContain(project.Id, wrId);
        Assert.False(wrId.EndsWith($"_{created.Number}", StringComparison.Ordinal));

        var wf = _grains.GetGrain<IWorkflowGrain>(wrId);
        var work = await wf.GetWorkAsync("runner-variable-test");

        Assert.NotNull(work);
        Assert.NotNull(work.Variables);
        Assert.Contains("/tmp/my-project", work.Variables);
        Assert.Contains("My Project", work.Variables);
        Assert.Contains("trunk", work.Variables);
    }

    [Fact]
    public async Task CreateIssue_SequentialNumbers()
    {
        var project = await SetupProjectAsync();

        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task CreateIssue_WithLabelsAndPriority()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Labeled", labels: ["bug", "urgent"], priority: "p0");

        Assert.Equal(["bug", "urgent"], issue.Labels);
        Assert.Equal("p0", issue.Priority);
    }

    [Fact]
    public async Task QueryService_ReturnsIssueInfo()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Info test", "desc");

        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal(created.Number, info.Number);
        Assert.Equal("Info test", info.Title);
        Assert.Equal("desc", info.Body);
    }

    [Fact]
    public async Task Update_ChangesTitleAndBody()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Original", "old body");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await grain.UpdateAsync("Updated", "new body");
        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal("Updated", info.Title);
        Assert.Equal("new body", info.Body);
    }

    [Fact]
    public async Task Close_ActiveIssue_CancelsIssueWithoutRewritingLifecycleToWorkflowStage()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Closable");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await grain.StartWorkAsync();
        await grain.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info.Stage);
        Assert.Equal("cancelled", info.RuntimeStatus);
    }

    [Fact]
    public async Task Hydrate_Duplicate_Throws()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Dup");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(project.Id, 999, "dup", null, null, null, null));
    }

    [Fact]
    public async Task IssueWorkflowStatus_ProjectsDefaultChangeDirOutsideWorkflowStatus()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Add Search");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", "/tmp/my-project", "main"));

        var status = await grain.GetWorkflowStatusAsync();

        Assert.NotNull(status);
        Assert.Equal($"openspec/changes/issue-{created.Number}", status.ChangeDir);
        Assert.DoesNotContain("ChangeDir", typeof(WorkflowStatusSnapshot).GetProperties().Select(p => p.Name));
    }

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

    [Fact]
    public async Task AddPrerequisite_StartEligibilityAndStartGateComeFromIssueGrain()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{dependent.Number}");

        await grain.AddPrerequisiteAsync(prereq.Number);
        var info = await GetIssueInfoAsync(project.Id, dependent.Number);
        var eligibility = await grain.GetStartEligibilityAsync();

        Assert.NotNull(info);
        Assert.Contains(prereq.Number, info.PrerequisiteNumbers);
        Assert.False(eligibility.Startable);
        Assert.Contains(eligibility.WaitingForCompletion, p => p.Number == prereq.Number);
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StartWorkAsync());
    }

    [Fact]
    public async Task CompletedPrerequisite_AllowsDependentIssueToStart()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var prereqGrain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{prereq.Number}");
        var dependentGrain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{dependent.Number}");

        await dependentGrain.AddPrerequisiteAsync(prereq.Number);
        var prereqRunId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", "/tmp/my-project", "main"));
        await prereqGrain.CompleteWorkAsync(prereqRunId);

        var eligibility = await dependentGrain.GetStartEligibilityAsync();

        Assert.True(eligibility.Startable);
        Assert.Empty(eligibility.WaitingForCompletion);
    }

}
