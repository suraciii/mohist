using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Workflow.Querier;

public class WorkflowProfileDefinitionSpecs : IAsyncLifetime
{
    private readonly WorkflowProfileManagerTestContext _test = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _test.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ReturnsTasksAndChecksForStage_FromProjectTemplate()
    {
        var runId = "wr_stage_specs_proj";
        var templateJson = _test.SerializeDefinitionWithStages("specs-template",
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

        await _test.SeedProjectTemplateAsync("specs_proj", runId, "specs-template", templateJson);

        var build = await _test.Manager.LoadStageSpecsAsync(runId, "build");

        Assert.Equal("build", build.Stage);
        Assert.Equal(new[] { "compile", "test" }, build.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "build-ok" }, build.Checks.Select(c => c.Name).ToArray());
        Assert.Equal("sequential", build.LockBehavior);
        Assert.Equal(new[] { "ci-pool" }, build.Resources);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_HonorsIssueCustomTemplate_PerStage()
    {
        // This API re-runs the cascade after StartAsync has loaded structure,
        // so a per-issue template must still replace the project default.
        var runId = "wr_stage_specs_issue";
        var projectJson = _test.SerializeDefinitionWithStages("project-tmpl",
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));
        var issueJson = _test.SerializeDefinitionWithStages("issue-custom",
            ("build", new[]
            {
                new TaskDefinition("replacement-task", "Replacement", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await _test.SeedIssueOverProjectTemplateAsync(
            "iss_proj", "iss_issue", runId,
            issueTemplateJson: issueJson,
            projectDefaultTemplateId: "project-tmpl",
            projectTemplateJson: projectJson);

        var build = await _test.Manager.LoadStageSpecsAsync(runId, "build");

        Assert.Equal(new[] { "replacement-task" }, build.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task LoadStageSpecsAsync_RerunsCascadeBetweenCalls_HotReloadsProfileEdits()
    {
        // Profile edits between calls must be visible to the second call.
        var runId = "wr_stage_specs_hot_reload";
        var templateJson = _test.SerializeDefinitionWithStages("hot-template",
            ("build", new[]
            {
                new TaskDefinition("original-task", "Original", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await _test.SeedProjectTemplateAsync("hot_proj", runId, "hot-template", templateJson);

        var before = await _test.Manager.LoadStageSpecsAsync(runId, "build");
        Assert.Equal(new[] { "original-task" }, before.Tasks.Select(t => t.Id).ToArray());

        var updatedJson = _test.SerializeDefinitionWithStages("hot-template",
            ("build", new[]
            {
                new TaskDefinition("replacement-task", "Replacement", "spec/task"),
                new TaskDefinition("follow-up-task", "Follow Up", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));
        await _test.UpdateProjectTemplateAsync("hot_proj", "hot-template", updatedJson);

        var after = await _test.Manager.LoadStageSpecsAsync(runId, "build");
        Assert.Equal(new[] { "replacement-task", "follow-up-task" }, after.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ThrowsWhenStageMissing()
    {
        var runId = "wr_stage_specs_missing";
        var templateJson = _test.SerializeDefinitionWithStages("missing-template",
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await _test.SeedProjectTemplateAsync("missing_proj", runId, "missing-template", templateJson);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _test.Manager.LoadStageSpecsAsync(runId, "no-such-stage"));

        Assert.Contains("no-such-stage", ex.Message);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_stage_specs";
        await _test.SeedWithoutRunAsync(projectId: "proj-all-disabled-stage-specs", issueId: "issue_all_disabled_stage_specs",
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _test.Manager.LoadStageSpecsAsync(runId, "plan", "proj-all-disabled-stage-specs", "issue_all_disabled_stage_specs"));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ExistingRunKeepsOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_stage_specs";
        await _test.SeedAsync(projectId: "proj-existing-disabled-stage-specs", issueId: "issue_existing_disabled_stage_specs", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var integrate = await _test.Manager.LoadStageSpecsAsync(runId, "integrate");

        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task LoadStructureAsync_ReturnsStageSequenceAndApprovalFlags_WithoutTasks()
    {
        // The creation path receives only structure; per-stage detail stays
        // deferred until the stage initializes.
        var runId = "wr_structure_basic";
        var templateJson = _test.SerializeDefinitionWithStages("struct-template",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, new[]
            {
                new CheckDefinition("plan-ok", "Plan OK", "spec/check"),
            }, requiresApproval: true),
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, new[]
            {
                new CheckDefinition("build-ok", "Build OK", "spec/check"),
            }, requiresApproval: false));

        await _test.SeedProjectTemplateAsync("struct_proj", runId, "struct-template", templateJson);

        var structure = await _test.Manager.LoadStructureAsync(runId);

        Assert.Equal("struct-template", structure.Id);
        Assert.Equal(new[] { "plan", "build" }, structure.Stages.Select(s => s.Stage).ToArray());
        Assert.True(structure.Stages.Single(s => s.Stage == "plan").RequiresApproval);
        Assert.False(structure.Stages.Single(s => s.Stage == "build").RequiresApproval);
    }

    [Fact]
    public async Task LoadStructureAsync_HonorsExplicitContextAtCreateTime_BeforeRunPersisted()
    {
        // StartAsync supplies project and issue before the WorkflowRun exists.
        var runId = "wr_structure_explicit";
        var templateJson = _test.SerializeDefinitionWithStages("explicit-tmpl",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: true));

        await _test.SeedProjectOnlyAsync("explicit_proj", "explicit_issue", "explicit-tmpl", templateJson);

        var structure = await _test.Manager.LoadStructureAsync(
            runId, projectId: "explicit_proj", issueId: "explicit_issue");

        Assert.Equal("explicit-tmpl", structure.Id);
        Assert.Equal(new[] { "plan" }, structure.Stages.Select(s => s.Stage).ToArray());
        Assert.True(structure.Stages.Single().RequiresApproval);
    }

    [Fact]
    public async Task LoadStructureAsync_FallsBackToSystemDefault_WhenContextMissing()
    {
        // With neither persisted nor explicit context, the cascade ends at
        // the system default template.
        var structure = await _test.Manager.LoadStructureAsync("unknown-run-id");

        Assert.NotEmpty(structure.Stages);
        Assert.Contains(structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadStructureAsync_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_structure";
        await _test.SeedWithoutRunAsync(projectId: "proj-all-disabled-structure", issueId: "issue_all_disabled_structure",
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _test.Manager.LoadStructureAsync(runId, "proj-all-disabled-structure", "issue_all_disabled_structure"));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadStructureAsync_ExistingRunKeepsOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_structure";
        await _test.SeedAsync(projectId: "proj-existing-disabled-structure", issueId: "issue_existing_disabled_structure", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var structure = await _test.Manager.LoadStructureAsync(runId);

        Assert.Equal("mohist/local", structure.Id);
        Assert.Contains(structure.Stages, s => s.Stage == "integrate");
    }

    [Fact]
    public async Task WorkflowQuerier_ExistingRunYamlAndStatusUseOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_query";
        await _test.SeedAsync(projectId: "proj-existing-disabled-query", issueId: "issue_existing_disabled_query", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);
        await _test.ReplaceRunStateAsync(runId, "proj-existing-disabled-query", "issue_existing_disabled_query", "mohist/local");
        var querier = new WorkflowQuerier(
            _test.CreateDbContextFactory(),
            _test.Manager,
            new Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier(_test.CreateDbContextFactory()));

        var yaml = await querier.GetDefinitionYamlAsync(runId);
        var status = await querier.GetStatusAsync(runId);

        Assert.NotNull(yaml);
        Assert.Contains("integrate:rebase", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("merge-pr", yaml, StringComparison.Ordinal);
        Assert.NotNull(status);
        var integrate = Assert.Single(status!.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task WorkflowQuerier_StatusRead_MigratesLegacyClaimAssignment()
    {
        var runId = "wr_legacy_claim_status_query";
        await _test.SeedAsync(
            projectId: "proj-legacy-claim-status-query",
            issueId: "issue_legacy_claim_status_query",
            runId: runId,
            issueTemplateJson: null,
            issueWorkflowProfileId: "mohist/local");
        await _test.ReplaceRunStateJsonAsync(
            runId,
            """
            {
              "id": "wr_legacy_claim_status_query",
              "metadata": {
                "createdAt": "2026-06-15T10:00:00Z"
              },
              "status": "ready",
              "claim": {
                "runnerId": "runner-legacy-claim",
                "claimedAt": "2026-06-15T10:01:00Z"
              },
              "currentStageId": "build",
              "stages": []
            }
            """);
        var querier = new WorkflowQuerier(
            _test.CreateDbContextFactory(),
            _test.Manager,
            new Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier(_test.CreateDbContextFactory()));

        var status = await querier.GetStatusAsync(runId);

        Assert.NotNull(status);
        Assert.Equal("ready", status!.Status);
        Assert.Equal("runner-legacy-claim", status.AssignedTo);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ExistingRunIgnoresLaterDisabledProfiles()
    {
        var runId = "wr_existing_disabled_approval";
        await _test.SeedAsync(projectId: "proj-existing-disabled-approval", issueId: "issue_existing_disabled_approval", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var approval = await _test.Manager.LoadApprovalConfigAsync(runId);

        Assert.NotNull(approval?.Feedback?.Task);
        Assert.Equal("apply-feedback", approval!.Feedback!.Task!.Id);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ReturnsConfiguredFeedbackTask_WhenDefined()
    {
        var runId = "wr_approval_defined";
        var feedbackConfig = new ApprovalFeedbackConfig(
            Task: new FeedbackTaskConfig(
                Id: "apply-feedback",
                Title: "Apply Feedback",
                Uses: "spec/task",
                With: null));
        var approval = new ApprovalConfig(Feedback: feedbackConfig);
        var definition = new WorkflowDefinition("approval-template",
            new List<StageDefinition>
            {
                new("plan",
                    new List<TaskDefinition>(),
                    new List<CheckDefinition>(),
                    RequiresApproval: true),
            },
            Approval: approval);
        var templateJson = JsonSerializer.Serialize(definition, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await _test.SeedProjectTemplateAsync("approval_proj", runId, "approval-template", templateJson);

        var loaded = await _test.Manager.LoadApprovalConfigAsync(runId);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Feedback);
        Assert.NotNull(loaded.Feedback!.Task);
        Assert.Equal("apply-feedback", loaded.Feedback.Task!.Id);
        Assert.Equal("spec/task", loaded.Feedback.Task.Uses);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ReturnsNull_WhenNoApprovalConfig()
    {
        var runId = "wr_approval_null";
        var templateJson = _test.SerializeDefinitionWithStages("no-approval-template",
            ("plan", Array.Empty<TaskDefinition>(), Array.Empty<CheckDefinition>(), requiresApproval: false));

        await _test.SeedProjectTemplateAsync("no_approval_proj", runId, "no-approval-template", templateJson);

        var loaded = await _test.Manager.LoadApprovalConfigAsync(runId);

        Assert.Null(loaded);
    }
}
