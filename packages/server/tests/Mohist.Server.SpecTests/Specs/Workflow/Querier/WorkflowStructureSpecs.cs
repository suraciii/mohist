using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public class WorkflowStructureSpecs : WorkflowProfileManagerTestFactory
{
    [Fact]
    public async Task LoadStructureAsync_ReturnsStageSequenceAndApprovalFlags_WithoutTasks()
    {
        // The narrow structure projection must NOT carry tasks or checks.
        // That keeps the grain's Create path from touching per-stage detail
        // until a stage actually initializes.
        var runId = "wr_structure_basic";
        var templateJson = SerializeDefinitionWithStages("struct-template",
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

        await SeedProjectTemplateAsync("struct_proj", runId, "struct-template", templateJson);

        var structure = await Manager.LoadStructureAsync(runId);

        Assert.Equal("struct-template", structure.Id);
        Assert.Equal(new[] { "plan", "build" }, structure.Stages.Select(s => s.Stage).ToArray());
        Assert.True(structure.Stages.Single(s => s.Stage == "plan").RequiresApproval);
        Assert.False(structure.Stages.Single(s => s.Stage == "build").RequiresApproval);
    }

    [Fact]
    public async Task LoadStructureAsync_HonorsExplicitContextAtCreateTime_BeforeRunPersisted()
    {
        // StartAsync passes project/issue context explicitly because the run
        // is not yet persisted when the structure is loaded for Create.
        var runId = "wr_structure_explicit";
        var templateJson = SerializeDefinitionWithStages("explicit-tmpl",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: true));

        // Seed only the project profile — no WorkflowRun row exists yet.
        await using (var db = new MohistDbContext(Database.Options))
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "explicit_proj",
                DefaultTemplateId = "explicit-tmpl",
                Variables = "{}",
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "explicit_proj",
                TemplateId = "explicit-tmpl",
                Template = templateJson,
            });
            db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
            {
                ProjectId = "explicit_proj",
                IssueNumber = 1,
                Variables = "{}",
            });
            await db.SaveChangesAsync();
        }

        // The run is not in the DB; only the explicit context will find the
        // project template.
        var structure = await Manager.LoadStructureAsync(
            runId, projectId: "explicit_proj", issueNumber: 1);

        Assert.Equal("explicit-tmpl", structure.Id);
        Assert.Equal(new[] { "plan" }, structure.Stages.Select(s => s.Stage).ToArray());
        Assert.True(structure.Stages.Single().RequiresApproval);
    }

    [Fact]
    public async Task LoadStructureAsync_FallsBackToSystemDefault_WhenContextMissing()
    {
        // Sanity: when neither the run nor explicit context carries a
        // project, the cascade ends at the system default template.
        var structure = await Manager.LoadStructureAsync("unknown-run-id");

        Assert.NotEmpty(structure.Stages);
        Assert.Contains(structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadStructureAsync_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_structure";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-structure", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Manager.LoadStructureAsync(runId, "proj-all-disabled-structure", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadStartupStructureAsync_WhenProjectCollectionIsMissing_ThrowsBeforeRunBinding()
    {
        var runId = "wr_missing_profile_collection";
        var projectId = "proj-missing-profile-collection";
        await using (var db = new MohistDbContext(Database.Options))
        {
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
            });
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProfileBackedManager().LoadStartupStructureAsync(runId, projectId, 1));

        Assert.Contains(projectId, ex.Message, StringComparison.Ordinal);
        await using var verifyDb = new MohistDbContext(Database.Options);
        Assert.False(await verifyDb.WorkflowRuns.AnyAsync(r => r.WorkflowRunId == runId));
    }

    [Fact]
    public async Task LoadStartupStructureAsync_WhenProjectDefaultIsMissing_ThrowsBeforeRunBinding()
    {
        var runId = "wr_missing_profile_default";
        var projectId = "proj-missing-profile-default";
        await SeedWithoutRunAsync(projectId, 1, issueTemplateJson: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProfileBackedManager().LoadStartupStructureAsync(runId, projectId, 1));

        Assert.Contains("no default Workflow Profile", ex.Message, StringComparison.Ordinal);
        await using var verifyDb = new MohistDbContext(Database.Options);
        Assert.False(await verifyDb.WorkflowRuns.AnyAsync(r => r.WorkflowRunId == runId));
    }

    [Fact]
    public async Task LoadStructureAsync_ExistingRunKeepsOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_structure";
        await SeedAsync(projectId: "proj-existing-disabled-structure", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var structure = await Manager.LoadStructureAsync(runId);

        Assert.Equal("mohist/local", structure.Id);
        Assert.Contains(structure.Stages, s => s.Stage == "integrate");
    }

    [Fact]
    public async Task WorkflowQuerier_ExistingRunYamlAndStatusUseOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_query";
        await SeedAsync(projectId: "proj-existing-disabled-query", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);
        await ReplaceRunStateAsync(runId, "proj-existing-disabled-query", 1, "mohist/local");
        var querier = new WorkflowQuerier(
            new TestDbContextFactory(Database.Options),
            Manager,
            Resolver,
            new Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier(new TestDbContextFactory(Database.Options)));

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
    public async Task WorkflowQuerier_StatusRead_AfterStateUpgrade_UsesCanonicalAssignment()
    {
        var runId = "wr_legacy_claim_status_query";
        await SeedAsync(
            projectId: "proj-legacy-claim-status-query",
            issueNumber: 1,
            runId: runId,
            issueTemplateJson: null,
            issueWorkflowProfileId: "mohist/local");
        await ReplaceRunStateJsonAsync(
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
        await using (var upgradeDb = Database.CreateContext())
        {
            await WorkflowRunStateDataUpgrader.UpgradeAsync(
                upgradeDb,
                backup: static (_, _) => Task.FromResult("test-backup"));
        }
        var querier = new WorkflowQuerier(
            new TestDbContextFactory(Database.Options),
            Manager,
            Resolver,
            new Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier(new TestDbContextFactory(Database.Options)));

        var status = await querier.GetStatusAsync(runId);

        Assert.NotNull(status);
        Assert.Equal("ready", status!.Status);
        Assert.Equal("runner-legacy-claim", status.AssignedTo);
    }
}
