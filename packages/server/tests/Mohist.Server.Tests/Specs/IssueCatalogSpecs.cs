using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Variables.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class IssueCatalogSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly IGrainFactory _grains;

    public IssueCatalogSpecs(WorkflowGrainFixture fixture)
    {
        _grains = fixture.Grains;
    }

    private async Task<string> SetupProjectAsync()
    {
        var projects = _grains.GetGrain<IProjectGrain>(Guid.NewGuid().ToString());
        var project = await projects.CreateAsync($"proj-{Guid.NewGuid():N}", "/tmp/test", null);
        return project.Id;
    }

    [Fact]
    public async Task CreateIssue_ReturnsInfoWithNumber()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var issue = await catalog.CreateAsync("Test issue", "body", null, null);

        Assert.Equal(1, issue.Number);
        Assert.Equal("Test issue", issue.Title);
        Assert.Equal("body", issue.Body);
        Assert.Equal("backlog", issue.Stage);
        Assert.Equal("active", issue.Status);
        Assert.Equal(pid, issue.ProjectId);
        Assert.StartsWith("issue_", issue.Id);
    }

    [Fact]
    public async Task CreateIssue_SequentialNumbers()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var i1 = await catalog.CreateAsync("First", null, null, null);
        var i2 = await catalog.CreateAsync("Second", null, null, null);

        Assert.Equal(1, i1.Number);
        Assert.Equal(2, i2.Number);
    }

    [Fact]
    public async Task CreateIssue_WithLabelsAndPriority()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var issue = await catalog.CreateAsync("Labeled", null, ["bug", "urgent"], "p0");

        Assert.Equal(["bug", "urgent"], issue.Labels);
        Assert.Equal("p0", issue.Priority);
    }

    [Fact]
    public async Task ListIssues_ReturnsCreatedIssues()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        await catalog.CreateAsync("A", null, null, null);
        await catalog.CreateAsync("B", null, null, null);

        var list = await catalog.ListAsync();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task ListIssues_FilterByStage()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        await catalog.CreateAsync("A", null, null, null);

        var backlog = await catalog.ListAsync(stage: "backlog");
        var plan = await catalog.ListAsync(stage: "plan");

        Assert.Single(backlog);
        Assert.Empty(plan);
    }

    [Fact]
    public async Task GetInfo_ReturnsIssueFromGrain()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var created = await catalog.CreateAsync("Info test", "desc", null, null);

        var grain = _grains.GetGrain<IIssueGrain>($"{pid}:{created.Number}");
        var info = await grain.GetInfoAsync();

        Assert.Equal(created.Number, info.Number);
        Assert.Equal("Info test", info.Title);
        Assert.Equal("desc", info.Body);
    }

    [Fact]
    public async Task Update_ChangesTitleAndBody()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var created = await catalog.CreateAsync("Original", "old body", null, null);

        var grain = _grains.GetGrain<IIssueGrain>($"{pid}:{created.Number}");
        await grain.UpdateAsync("Updated", "new body");
        var info = await grain.GetInfoAsync();

        Assert.Equal("Updated", info.Title);
        Assert.Equal("new body", info.Body);
    }

    [Fact]
    public async Task Close_ActiveIssue_ResetsToBacklog()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var created = await catalog.CreateAsync("Closable", null, null, null);

        var grain = _grains.GetGrain<IIssueGrain>($"{pid}:{created.Number}");
        await grain.StartWorkflowAsync();
        await grain.CloseAsync();

        var info = await grain.GetInfoAsync();
        Assert.Equal("backlog", info.Stage);
        Assert.Equal("closed", info.Status);
    }

    [Fact]
    public async Task Hydrate_Duplicate_Throws()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var created = await catalog.CreateAsync("Dup", null, null, null);

        var grain = _grains.GetGrain<IIssueGrain>($"{pid}:{created.Number}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.HydrateAsync(pid, 999, "dup", null, null, null));
    }

    [Fact]
    public async Task StartWorkflow_WithProjectContext_AddsProjectVariables()
    {
        var pid = await SetupProjectAsync();
        var catalog = _grains.GetGrain<IIssueCatalogGrain>(pid);
        var created = await catalog.CreateAsync("Context", null, null, null, "openai/gpt-4o", new Dictionary<string, string> { ["plan"] = "anthropic/claude" });

        var grain = _grains.GetGrain<IIssueGrain>($"{pid}:{created.Number}");
        var wrId = await grain.StartWorkflowAsync(new WorkflowProjectContext(pid, "My Project", "/tmp/my-project", "trunk"));

        var scope = _grains.GetGrain<IVariableScopeGrain>(wrId);
        var snapshot = await scope.SnapshotAsync(new VariableSnapshotRequest(wrId, "", ""));

        Assert.Contains("/tmp/my-project", snapshot);
        Assert.Contains("My Project", snapshot);
        Assert.Contains("trunk", snapshot);
        Assert.Contains("openai/gpt-4o", snapshot);
        Assert.Contains("anthropic/claude", snapshot);
    }

    [Fact]
    public async Task DifferentProjects_IndependentNumbering()
    {
        var pid1 = await SetupProjectAsync();
        var pid2 = await SetupProjectAsync();

        var cat1 = _grains.GetGrain<IIssueCatalogGrain>(pid1);
        var cat2 = _grains.GetGrain<IIssueCatalogGrain>(pid2);

        var i1 = await cat1.CreateAsync("P1-Issue", null, null, null);
        var i2 = await cat2.CreateAsync("P2-Issue", null, null, null);

        Assert.Equal(1, i1.Number);
        Assert.Equal(1, i2.Number);
    }
}
