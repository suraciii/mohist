using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Services.Artifacts;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Xunit;
using Orleans;
using System.Text.Json;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Specs.Issue.Profile;

namespace Mohist.Server.Tests.Specs.Workflow;

[Collection("WorkflowGrain")]
public abstract class WorkflowGrainSpecs
{
    protected readonly WorkflowGrainFixture _fixture;
    protected string? _workflowId;
    protected string? _runnerId;

    protected WorkflowGrainSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    protected IGrainFactory Grains => _fixture.Grains;

    protected RecordingEventStore EventStore => _fixture.EventStore;

    protected WorkflowQuerier GetQuerier()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        return new WorkflowQuerier(
            factory,
            new Mohist.Server.Workflow.Services.WorkflowProfileManager(factory, null!, new PromptTemplateEngine()),
            new WorkflowArtifactQuerier(factory));
    }

    protected async Task<string> RegisterRunnerAsync(string? runnerId = null, int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        var projectId = _workflowId is null ? "test-project" : TestProjectId(_workflowId);
        return await RegisterRunnerForProjectAsync(projectId, runnerId, maxWorkflowSlots);
    }

    protected async Task<string> RegisterRunnerForProjectAsync(string projectId, string? runnerId = null, int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        runnerId ??= $"runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId, MaxWorkflowSlots: maxWorkflowSlots));
        return runnerId;
    }

    protected async Task<IWorkflowGrain> CreateWorkflowAsync(string? id = null)
    {
        id ??= $"wf-{Guid.NewGuid():N}";
        _workflowId = id;
        return Grains.GetGrain<IWorkflowGrain>(id);
    }

    protected async Task<IWorkflowGrain> StartWorkflowAsync(
        WorkflowDefinition definition,
        string? id = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        await ClearBacklogAsync();
        var workflowId = id ?? $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var projectId = TestProjectId(workflowId);
        var runnerId = await RegisterRunnerAsync(maxWorkflowSlots: maxWorkflowSlots);
        _runnerId = runnerId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        await workflow.StartAsync(TestInput(projectId));
        return workflow;
    }

    protected async Task<IWorkflowGrain> StartWorkflowWithoutRunnerAsync(WorkflowDefinition definition, string? id = null)
    {
        await ClearBacklogAsync();
        var workflow = await CreateWorkflowAsync(id);
        var projectId = TestProjectId(_workflowId!);
        await SeedWorkflowTemplateAsync(_workflowId!, definition, projectId);
        await workflow.StartAsync(TestInput(projectId));
        return workflow;
    }

    protected WorkflowStartInput TestInput(string? projectId = null, string? issueId = null)
    {
        projectId ??= _workflowId is null ? "test-project" : TestProjectId(_workflowId);
        issueId ??= _workflowId is null ? "test-issue" : TestIssueId(_workflowId);
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["issueId"] = issueId,
        };
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Annotations: annotations));
    }

    protected static string TestProjectId(string workflowId) => $"test-project-{workflowId}";

    protected static string TestIssueId(string workflowId) => $"test-issue-{workflowId}";

    protected async Task DeactivateWorkflowAsync(string workflowId)
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.DeactivateForTestAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var activations = await management.GetDetailedGrainStatistics();
            if (!activations.Any(stat => stat.GrainType.Contains(nameof(WorkflowGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(workflowId, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Workflow grain '{workflowId}' did not deactivate in time.");
    }

    protected async Task ClearBacklogAsync()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        db.BacklogStates.RemoveRange(db.BacklogStates);
        await db.SaveChangesAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
    }

    protected async Task EnqueueWorkflowForTestAsync(string workflowId, string? projectId = null)
    {
        projectId ??= TestProjectId(workflowId);
        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(projectId));
        await backlog.EnqueueAsync(workflowId);
    }

    protected async Task AssignWorkflowToRunnerAsync(string workflowId, string runnerId)
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.AssignRunnerAsync(runnerId);
    }

    protected async Task AssignActiveWorkForTestAsync(
        string runnerId,
        string workflowId,
        string workId = "task-1.1",
        string workType = "task",
        string stage = "build",
        string? title = "Task 1")
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        var projectId = TestProjectId(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(
            tasks: [new("task-1", title ?? "Task 1", "spec/task")],
            checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));
        await workflow.AssignRunnerAsync(runnerId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.NotNull(await runner.PollAsync());
    }

    protected async Task<(WorkDispatch Work, string RunnerId)> PollWorkAnyAsync()
    {
        return await PollWorkAsync(_runnerId!);
    }

    protected async Task<(WorkDispatch Work, string RunnerId)> PollWorkAsync(string runnerId)
    {
        await EnsureRunnerForCurrentWorkflowAsync(runnerId);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var work = await runner.PollAsync();
            if (work is not null)
                return (work, runnerId);

            await Task.Delay(20);
        }

        Assert.Fail($"Runner '{runnerId}' has no work for workflow '{_workflowId}'");
        return default;
    }

    private async Task EnsureRunnerForCurrentWorkflowAsync(string runnerId)
    {
        if (string.IsNullOrWhiteSpace(_workflowId)) return;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(_workflowId),
            MaxWorkflowSlots: RunnerCapacity.DefaultMaxWorkflowSlots));
    }

    protected async Task ReportAsync(string runnerId, string workId, string status, string? message = null)
    {
        await ReportAsync(runnerId, _workflowId!, workId, new WorkResult(status, message));
    }

    protected async Task ReportAsync(string runnerId, string workId, WorkResult result)
    {
        await ReportAsync(runnerId, _workflowId!, workId, result);
    }

    protected async Task ReportAsync(string runnerId, string workflowRunId, string workId, WorkResult result)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var dispatch = new WorkDispatch(workflowRunId, workId);
        await runner.ReportResultAsync(dispatch, workId, result);
    }

    protected async Task ReportAsync(string runnerId, WorkDispatch work, string status, string? message = null)
    {
        await ReportAsync(runnerId, work, new WorkResult(status, message));
    }

    protected async Task ReportAsync(string runnerId, WorkDispatch work, WorkResult result)
    {
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, result);
    }

    protected async Task ReportChecksAsync(string runnerId, WorkDispatch checksWork, params (string Name, string Status, string? Message)[] checkResults)
    {
        var output = JsonSerializer.Serialize(checkResults.Select(cr => new Dictionary<string, string?>
        {
            ["name"] = cr.Name,
            ["status"] = cr.Status,
            ["message"] = cr.Message,
        }));
        await ReportAsync(runnerId, checksWork.WorkId, new WorkResult(
            checkResults.All(cr => cr.Status == "pass") ? "pass" : "fail",
            Output: output));
    }

    protected async Task ReportChecksPassAsync(string runnerId, WorkDispatch checksWork, params string[] checkNames)
    {
        await ReportChecksAsync(runnerId, checksWork, checkNames.Select(n => (n, "pass", (string?)null)).ToArray());
    }

    protected async Task ReportChecksFailAsync(string runnerId, WorkDispatch checksWork, string failedCheckName, string message, params string[] passingCheckNames)
    {
        var results = passingCheckNames.Select(n => (n, "pass", (string?)null)).ToList();
        results.Add((failedCheckName, "fail", message));
        await ReportChecksAsync(runnerId, checksWork, results.ToArray());
    }

    protected async Task<WorkflowRun> LoadRunAsync(string workflowId)
    {
        var store = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(workflowId) ?? throw new InvalidOperationException($"Workflow run '{workflowId}' was not found");
    }

    protected static WorkflowDefinition SingleStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null,
        bool requiresApproval = false,
        string stage = "build")
    {
        return new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition(stage,
                tasks ?? [new("task-1", "Task 1", "spec/task")],
                checks ?? [new("check-1", "Check 1", "spec/check")],
                RequiresApproval: requiresApproval)
        ]);
    }

    protected static Dictionary<string, JsonElement?> With(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;

    protected async Task SeedWorkflowTemplateAsync(string workflowId, WorkflowDefinition definition, string? projectId = null)
    {
        projectId ??= TestProjectId(workflowId);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var templateId = definition.Id;
        var templateJson = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions);

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
            existingTemplate.UpdatedAt = DateTimeOffset.UtcNow;
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
            projectProfile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    protected async Task PatchProjectVariablesAsync(string projectId, VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var manager = new ProjectWorkflowProfileManager(factory, null!, new PromptTemplateEngine());
        await manager.PatchVariablesAsync(projectId, patch);
    }

    protected async Task PatchIssueVariablesAsync(string issueId, VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var manager = new IssueWorkflowProfileManager(factory);
        await manager.PatchVariablesAsync(issueId, patch);
    }

    protected static WorkflowDefinition TwoStages()
    {
        return new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]);
    }

    protected static WorkflowDefinition ApprovalStage()
    {
        return new WorkflowDefinition("spec/workflow",
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
