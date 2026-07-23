using System.Text.Json;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Services.Artifacts;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Static helpers for spec files that exercise the <see cref="WorkflowGrain"/>
/// via <see cref="WorkflowGrainFixture"/>. Extracted from the former
/// <c>WorkflowGrainSpecs</c> abstract base class so helpers can be called
/// from non-derived specs and tests do not share mutable instance state
/// (<c>_workflowId</c>, <c>_runnerId</c>) across cases.
/// </summary>
public static class WorkflowGrainTestHelpers
{
    public static string TestProjectId(string workflowId) => $"test-project-{workflowId}";

    public static WorkflowStartInput TestInput(IGrainFactory grains, string workflowId, string? projectId = null, string? issueId = null)
    {
        projectId ??= TestProjectId(workflowId);
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
        };
        if (!string.IsNullOrWhiteSpace(issueId))
            annotations["issueId"] = issueId;
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            Annotations: annotations));
    }

    public static string TestIssueId(string workflowId) => $"test-issue-{workflowId}";

    public static async Task<string> RegisterRunnerAsync(
        IGrainFactory grains,
        string workflowId,
        string? runnerId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        var projectId = TestProjectId(workflowId);
        return await RegisterRunnerForProjectAsync(grains, projectId, runnerId, maxWorkflowSlots);
    }

    public static async Task<string> RegisterRunnerForProjectAsync(
        IGrainFactory grains,
        string projectId,
        string? runnerId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        runnerId ??= $"runner-{Guid.NewGuid():N}";
        var runner = grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }
        return runnerId;
    }

    public static IWorkflowGrain CreateWorkflow(IGrainFactory grains, string workflowId) =>
        grains.GetGrain<IWorkflowGrain>(workflowId);

    public static async Task<IWorkflowGrain> StartWorkflowAsync(
        IGrainFactory grains,
        string connectionString,
        WorkflowDefinition definition,
        string workflowId,
        string? runnerId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        await ClearBacklogAsync(grains, connectionString);
        var projectId = TestProjectId(workflowId);
        runnerId ??= await RegisterRunnerAsync(grains, workflowId, runnerId, maxWorkflowSlots);

        var workflow = CreateWorkflow(grains, workflowId);
        await SeedWorkflowTemplateAsync(connectionString, workflowId, definition, projectId);
        await workflow.StartAsync(TestInput(grains, workflowId, projectId));
        return workflow;
    }

    public static async Task<IWorkflowGrain> StartWorkflowWithoutRunnerAsync(
        IGrainFactory grains,
        string connectionString,
        WorkflowDefinition definition,
        string workflowId)
    {
        await ClearBacklogAsync(grains, connectionString);
        var projectId = TestProjectId(workflowId);
        var workflow = CreateWorkflow(grains, workflowId);
        await SeedWorkflowTemplateAsync(connectionString, workflowId, definition, projectId);
        await workflow.StartAsync(TestInput(grains, workflowId, projectId));
        return workflow;
    }

    public static WorkflowQuerier GetQuerier(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var promptLoader = new InMemoryPromptLoader();
        return new WorkflowQuerier(
            factory,
            new WorkflowProfileManager(
                factory,
                promptLoader,
                new PromptTemplateEngine(),
                CreateEmptyConfigService(),
                new WorkflowRunProfileManager(factory)),
            new WorkflowArtifactQuerier(factory));
    }

    /// <summary>
    /// Builds a <see cref="ConfigService"/> backed by a non-existent config
    /// file, so <see cref="ConfigService.GetVariables"/> returns
    /// <see cref="VariableBundle.Empty"/> — the global layer is empty in
    /// workflow-grain specs that exercise project/issue layers directly.
    /// </summary>
    public static ConfigService CreateEmptyConfigService() =>
        new(
            new ConfigurationBuilder().Build(),
            new MockEnvironmentVariableProvider(),
            NullLogger<ConfigService>.Instance,
            new InMemoryConfigDocumentStore());

    public static async Task ClearBacklogAsync(IGrainFactory grains, string connectionString)
    {
        var registry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var ids = await registry.ListRunnerIdsAsync();
        foreach (var id in ids)
            await registry.UnregisterAsync(id);
    }

    public static async Task EnqueueWorkflowForTestAsync(IGrainFactory grains, string workflowId, string? projectId = null)
    {
        await Task.CompletedTask;
    }

    public static async Task AssignWorkflowToRunnerAsync(IGrainFactory grains, string workflowId, string runnerId)
    {
        var workflow = grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.AssignWorkerAsync(runnerId);
    }

    public static async Task AssignActiveWorkForTestAsync(
        IGrainFactory grains,
        string connectionString,
        string runnerId,
        string workflowId,
        string workId = "task-1.1",
        string workType = "task",
        string stage = "build",
        string? title = "Task 1")
    {
        var workflow = grains.GetGrain<IWorkflowGrain>(workflowId);
        var projectId = TestProjectId(workflowId);
        await SeedWorkflowTemplateAsync(connectionString, workflowId, SingleStage(
            tasks: [new("task-1", title ?? "Task 1", "spec/task")],
            checks: []), projectId);
        await workflow.StartAsync(TestInput(grains, workflowId, projectId));
        await workflow.AssignWorkerAsync(runnerId);

        // The runner grain no longer owns PollAsync (the stateless
        // DispatchService computes dispatches, which needs a service provider
        // this static helper does not hold). Callers that need a dispatch poll
        // through their fixture's service provider; this helper only stages the
        // assignment.
    }

    public static WorkflowDefinition SingleStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null,
        bool requiresApproval = false,
        string stage = "build")
    {
        return new WorkflowDefinition(
        [
            new StageDefinition(stage,
                tasks ?? [new("task-1", "Task 1", "spec/task")],
                checks ?? [new("check-1", "Check 1", "spec/check")],
                RequiresApproval: requiresApproval)
        ]);
    }

    public static Dictionary<string, JsonElement?> With(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;

    public static string SerializeProfile(WorkflowDefinition definition, string profileId = "spec/workflow") =>
        JsonSerializer.Serialize(
            new WorkflowProfile(profileId, profileId, string.Empty, definition),
            WorkflowYamlSerializer.JsonOptions);

    public static async Task SeedWorkflowTemplateAsync(string connectionString, string workflowId, WorkflowDefinition definition, string? projectId = null)
    {
        projectId ??= TestProjectId(workflowId);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var templateId = workflowId;
        var templateJson = SerializeProfile(definition, templateId);

        var existingTemplate = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existingTemplate is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = templateJson,
            });
        }
        else
        {
            existingTemplate.Template = templateJson;
            existingTemplate.UpdatedAt = TestTime.UtcNow;
        }

        var projectProfile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (projectProfile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
            });
        }
        else
        {
            projectProfile.DefaultTemplateId = templateId;
            projectProfile.UpdatedAt = TestTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public static async Task PatchProjectVariablesAsync(string connectionString, string projectId, VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var manager = new ProjectWorkflowProfileManager(factory, null!, new PromptTemplateEngine(), NullActionCatalogSource.Instance);
        await manager.PatchVariablesAsync(projectId, patch);
    }

    public static async Task PatchIssueVariablesAsync(
        string connectionString,
        string projectId,
        int issueNumber,
        VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var manager = new IssueWorkflowProfileManager(factory, NullActionCatalogSource.Instance);
        await manager.PatchVariablesAsync(projectId, issueNumber, patch);
    }

    public static WorkflowDefinition TwoStages()
    {
        return new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]);
    }

    public static WorkflowDefinition ApprovalStage()
    {
        return new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")],
                RequiresApproval: true),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]);
    }
}
