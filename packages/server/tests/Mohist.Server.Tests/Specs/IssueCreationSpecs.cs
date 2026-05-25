using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class IssueCreationSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly IGrainFactory _grains;

    public IssueCreationSpecs(WorkflowGrainFixture fixture)
    {
        _grains = fixture.Grains;
    }

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        var projects = _grains.GetGrain<IProjectGrain>(Guid.NewGuid().ToString());
        return await projects.CreateAsync($"proj-{Guid.NewGuid():N}", "/tmp/test", null);
    }

    private async Task<IssueInfo> CreateIssueAsync(string projectId, string title, string? body = null, string[]? labels = null, string? priority = null, string? model = null, Dictionary<string, string>? stageModels = null, string? workflowProfileId = null)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = _grains.GetGrain<IIssueGrain>($"{projectId}:{number}");
        await grain.HydrateAsync(projectId, number, title, body, labels, priority, model, stageModels, workflowProfileId);
        return await grain.GetInfoAsync();
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
        Assert.Equal("active", issue.Status);
        Assert.Equal(project.Id, issue.ProjectId);
        Assert.StartsWith("issue_", issue.Id);
        Assert.Equal("mohist/default", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task CreateIssue_WithWorkflowProfileId_PersistsProfileId()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Custom profile", workflowProfileId: "custom/profile");

        Assert.Equal("custom/profile", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task StartWorkflow_WithUnknownProfile_FailsClearly()
    {
        var project = await SetupProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Unknown profile", workflowProfileId: "missing/profile");
        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{issue.Number}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StartWorkflowAsync());

        Assert.Contains("Workflow profile 'missing/profile' not found", ex.Message);
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
    public async Task GetInfo_ReturnsIssueFromGrain()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Info test", "desc");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        var info = await grain.GetInfoAsync();

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
        var info = await grain.GetInfoAsync();

        Assert.Equal("Updated", info.Title);
        Assert.Equal("new body", info.Body);
    }

    [Fact]
    public async Task Close_ActiveIssue_CancelsIssueWithoutRewritingLifecycleToWorkflowStage()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Closable");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await grain.StartWorkflowAsync();
        await grain.CloseAsync();

        var info = await grain.GetInfoAsync();
        Assert.Equal("cancelled", info.Stage);
        Assert.Equal("cancelled", info.Status);
    }

    [Fact]
    public async Task Hydrate_Duplicate_Throws()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Dup");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.HydrateAsync(project.Id, 999, "dup", null, null, null));
    }

    [Fact]
    public async Task StartWorkflow_WithProjectContext_DispatchesProjectVariables()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Context", model: "openai/gpt-4o", stageModels: new Dictionary<string, string> { ["plan"] = "anthropic/claude" });

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        var wrId = await grain.StartWorkflowAsync(new WorkflowProjectContext(project.Id, "My Project", "/tmp/my-project", "trunk"));

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
        Assert.Contains("openai/gpt-4o", work.Variables);
        Assert.Contains("anthropic/claude", work.Variables);
    }

    [Fact]
    public async Task IssueWorkflowStatus_ProjectsDefaultChangeDirOutsideWorkflowStatus()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Add Search");

        var grain = _grains.GetGrain<IIssueGrain>($"{project.Id}:{created.Number}");
        await grain.StartWorkflowAsync(new WorkflowProjectContext(project.Id, "My Project", "/tmp/my-project", "main"));

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
        var info = await grain.GetInfoAsync();
        var eligibility = await grain.GetStartEligibilityAsync();

        Assert.Contains(prereq.Number, info.PrerequisiteNumbers);
        Assert.False(eligibility.Startable);
        Assert.Contains(eligibility.WaitingForCompletion, p => p.Number == prereq.Number);
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StartWorkflowAsync());
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
        await prereqGrain.StartWorkflowAsync(new WorkflowProjectContext(project.Id, "My Project", "/tmp/my-project", "main"));
        var prereqRunId = await prereqGrain.GetWorkflowRunIdAsync();
        await prereqGrain.CompleteWorkflowAsync(prereqRunId!);

        var eligibility = await dependentGrain.GetStartEligibilityAsync();

        Assert.True(eligibility.Startable);
        Assert.Empty(eligibility.WaitingForCompletion);
    }

}
