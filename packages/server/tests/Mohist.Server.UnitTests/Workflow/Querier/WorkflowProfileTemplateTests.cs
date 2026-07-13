using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Querier;

public class WorkflowProfileTemplateTests : IAsyncLifetime
{
    private readonly WorkflowProfileManagerTestContext _test = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _test.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadTemplate_FallsBackToSystemDefault_WhenRunContextMissing()
    {
        var result = await _test.Manager.LoadTemplateAsync("unknown-run-id");

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
    }

    [Fact]
    public async Task LoadTemplate_UsesIssueCustomWithoutRunProfileBinding()
    {
        var runId = "wr_snap01";
        await _test.SeedAsync(projectId: "proj1", issueId: "issue_1", runId: runId,
            issueTemplateJson: _test.SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: _test.SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority2_ReturnsIssueCustomTemplate()
    {
        var runId = "wr_issue01";
        await _test.SeedAsync(projectId: "proj2", issueId: "issue_2", runId: runId,
            issueTemplateJson: _test.SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: _test.SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority3_ReturnsIssueReferencedTemplate()
    {
        var runId = "wr_ref01";
        await _test.SeedAsync(projectId: "proj3", issueId: "issue_3", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: "my-tmpl",
            projectTemplateJson: _test.SerializeDefinition("my-tmpl", stageCount: 4));

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(4, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultCustomTemplate_NoIssueSelection_UsesProjectDefault()
    {
        var runId = "wr_default01";
        await _test.SeedAsync(projectId: "proj4", issueId: "issue_4", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "default-tmpl",
            projectTemplateJson: _test.SerializeDefinition("default-tmpl", stageCount: 5));

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(5, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultSystemTemplate_FallsBackToSystemTemplate()
    {
        var runId = "wr_system_default01";
        await _test.SeedAsync(projectId: "proj-sys", issueId: "issue_sys", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local");

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
        Assert.Contains(result.Structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadTemplate_DisabledProjectDefaultSystemTemplate_UsesFirstEnabledProfile()
    {
        var runId = "wr_disabled_default01";
        await _test.SeedWithoutRunAsync(projectId: "proj-disabled-default", issueId: "issue_disabled_default",
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local"]);

        var result = await _test.Manager.LoadTemplateAsync(runId, "proj-disabled-default", "issue_disabled_default");

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/github-pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_template";
        await _test.SeedWithoutRunAsync(projectId: "proj-all-disabled-template", issueId: "issue_all_disabled_template",
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _test.Manager.LoadTemplateAsync(runId, "proj-all-disabled-template", "issue_all_disabled_template"));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadTemplate_WhenAllProfilesDisabled_ThrowsBeforeIssueCustomTemplate()
    {
        var runId = "wr_all_disabled_custom_template";
        await _test.SeedWithoutRunAsync(projectId: "proj-all-disabled-custom-template", issueId: "issue_all_disabled_custom_template",
            issueTemplateJson: _test.SerializeDefinition("issue-custom-disabled", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _test.Manager.LoadTemplateAsync(runId, "proj-all-disabled-custom-template", "issue_all_disabled_custom_template"));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadTemplate_ExistingRunIgnoresLaterDisabledProfiles()
    {
        var runId = "wr_existing_disabled_template";
        await _test.SeedAsync(projectId: "proj-existing-disabled-template", issueId: "issue_existing_disabled_template", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_NoOverrides_UsesPrSystemTemplate()
    {
        var runId = "wr_issue_pr";
        await _test.SeedAsync(projectId: "proj-issue-pr", issueId: "issue_pr", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/github-pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        var mergePr = Assert.Single(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_IssueDefaultProfile_NoOverrides_UsesDefaultSystemTemplate()
    {
        var runId = "wr_issue_default";
        await _test.SeedAsync(projectId: "proj-issue-default", issueId: "issue_default", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local");

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:open-pr");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_ProjectDefaultIsDifferent_UsesIssueProfile()
    {
        var runId = "wr_issue_pr_proj_default";
        await _test.SeedAsync(projectId: "proj-issue-pr-proj-default", issueId: "issue_pr_proj", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local",
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/github-pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        var mergePr = Assert.Single(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_CustomYamlOverride_TakesPrecedence()
    {
        var runId = "wr_issue_pr_custom";
        await _test.SeedAsync(projectId: "proj-pr-custom", issueId: "issue_pr_custom", runId: runId,
            issueTemplateJson: _test.SerializeDefinition("custom-override", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await _test.Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Single(result.Structure.Stages);
    }
}
