using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public class WorkflowStageSpecs : WorkflowProfileManagerTestFactory
{
    [Fact]
    public async Task LoadStageSpecsAsync_ReturnsTasksAndChecksForStage_FromProjectTemplate()
    {
        var runId = "wr_stage_specs_proj";
        var templateJson = SerializeDefinitionWithStages("specs-template",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, new[]
            {
                new CheckDefinition("plan-ok", "Plan OK", "spec/check"),
            }, requiresApproval: false),
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
                new TaskDefinition("test", "Test", "spec/task"),
            }, new[]
            {
                new CheckDefinition("build-ok", "Build OK", "spec/check"),
            }, requiresApproval: false));

        await SeedProjectTemplateAsync("specs_proj", runId, "specs-template", templateJson);

        var build = await Manager.LoadStageSpecsAsync(runId, "build");

        Assert.Equal("build", build.Stage);
        Assert.Equal(new[] { "compile", "test" }, build.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "build-ok" }, build.Checks.Select(c => c.Id).ToArray());
        Assert.Equal("sequential", build.LockBehavior);
        Assert.Equal(new[] { "ci-pool" }, build.Resources);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_HonorsIssueCustomTemplate_PerStage()
    {
        // Issue-level template can replace the project default. The narrow API
        // re-runs the cascade on every call so the choice is honored
        // even when stage-init runs after StartAsync has already loaded
        // a different (e.g. project default) structure.
        var runId = "wr_stage_specs_issue";
        var projectJson = SerializeDefinitionWithStages("project-tmpl",
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));
        var issueJson = SerializeDefinitionWithStages("issue-custom",
            ("build", new[]
            {
                new TaskDefinition("replacement-task", "Replacement", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedIssueOverProjectTemplateAsync(
            "iss_proj", 1, runId,
            issueTemplateJson: issueJson,
            projectDefaultTemplateId: "project-tmpl",
            projectTemplateJson: projectJson);

        var build = await Manager.LoadStageSpecsAsync(runId, "build");

        Assert.Equal(new[] { "replacement-task" }, build.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task LoadStageSpecsAsync_RerunsCascadeBetweenCalls_HotReloadsProfileEdits()
    {
        // The hot-reload promise: profile edits between two calls MUST be
        // visible to the second caller (since this API re-runs the cascade).
        var runId = "wr_stage_specs_hot_reload";
        var templateJson = SerializeDefinitionWithStages("hot-template",
            ("build", new[]
            {
                new TaskDefinition("original-task", "Original", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedProjectTemplateAsync("hot_proj", runId, "hot-template", templateJson);

        var before = await Manager.LoadStageSpecsAsync(runId, "build");
        Assert.Equal(new[] { "original-task" }, before.Tasks.Select(t => t.Id).ToArray());

        // Mutate the project template to a new task — next call must see it.
        var updatedJson = SerializeDefinitionWithStages("hot-template",
            ("build", new[]
            {
                new TaskDefinition("replacement-task", "Replacement", "spec/task"),
                new TaskDefinition("follow-up-task", "Follow Up", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));
        await UpdateProjectTemplateAsync("hot_proj", "hot-template", updatedJson);

        var after = await Manager.LoadStageSpecsAsync(runId, "build");
        Assert.Equal(new[] { "replacement-task", "follow-up-task" }, after.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ThrowsWhenStageMissing()
    {
        var runId = "wr_stage_specs_missing";
        var templateJson = SerializeDefinitionWithStages("missing-template",
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedProjectTemplateAsync("missing_proj", runId, "missing-template", templateJson);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Manager.LoadStageSpecsAsync(runId, "no-such-stage"));

        Assert.Contains("no-such-stage", ex.Message);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_stage_specs";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-stage-specs", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Manager.LoadStageSpecsAsync(runId, "plan", "proj-all-disabled-stage-specs", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ExistingRunKeepsOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_stage_specs";
        await SeedAsync(projectId: "proj-existing-disabled-stage-specs", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var integrate = await Manager.LoadStageSpecsAsync(runId, "integrate");

        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

}
