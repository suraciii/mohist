using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
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
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.SpecTests.Specs.Issue.Profile;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Workflow;

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

    // The silo service provider for resolving stateless services
    // (DispatchService) — needed because the runner grain no longer owns the
    // poll verb. Specs route inline runner.PollAsync() calls through the
    // DispatchTestExtensions.PollAsync(runner, Services) helper.
    protected IServiceProvider Services => _fixture.Cluster.GetSiloServiceProvider(null);

    protected RecordingEventStore EventStore => _fixture.EventStore;

    protected WorkflowQuerier GetQuerier()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var promptLoader = new Mohist.Server.Workflow.Services.Prompts.FilePromptLoader();
        return new WorkflowQuerier(
            factory,
            new Mohist.Server.Workflow.Services.WorkflowProfileManager(
                factory,
                promptLoader,
                new PromptTemplateEngine(),
                WorkflowGrainTestHelpers.CreateEmptyConfigService(),
                new Mohist.Server.Workflow.Services.WorkflowRunProfileManager(factory)),
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
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }
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

    protected WorkflowStartInput TestInput(string? projectId = null, int? issueNumber = null)
    {
        projectId ??= _workflowId is null ? "test-project" : TestProjectId(_workflowId);
        issueNumber ??= 1;
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["issueNumber"] = issueNumber.Value.ToString(),
        };
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            Annotations: annotations));
    }

    protected static string TestProjectId(string workflowId) => $"test-project-{workflowId}";

    protected static int TestIssueNumber(string workflowId) => 1;

    protected async Task DeactivateWorkflowAsync(string workflowId)
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.DeactivateForTestAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        await TestWait.ForAsync(
            async () => await management.GetDetailedGrainStatistics(),
            activations => !activations.Any(stat => stat.GrainType.Contains(nameof(WorkflowGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(workflowId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"Workflow grain '{workflowId}' to deactivate");
    }

    protected async Task ClearBacklogAsync()
    {
        // The global runner registry is shared across every test in this
        // collection. Without resetting it, tests that submit agent jobs
        // see work assigned by stale runners registered in earlier specs.
        await ClearGlobalRunnerRegistryAsync();
    }

    protected async Task ClearRunnerRegistryAsync(string registryKey)
    {
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(registryKey);
        var ids = await registry.ListRunnerIdsAsync();
        foreach (var id in ids)
            await registry.UnregisterAsync(id);
    }

    protected async Task ClearGlobalRunnerRegistryAsync()
    {
        await ClearRunnerRegistryAsync(RunnerRegistryKeys.Global);
    }

    protected async Task EnqueueWorkflowForTestAsync(string workflowId, string? projectId = null)
    {
        await Task.CompletedTask;
    }

    protected async Task AssignWorkflowToRunnerAsync(string workflowId, string runnerId)
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.AssignWorkerAsync(runnerId);
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
        await workflow.AssignWorkerAsync(runnerId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var (assigned, _) = await PollWorkAsync(runnerId);
        Assert.False(string.IsNullOrEmpty(assigned.WorkId));
    }

    protected async Task<(WorkDispatch Work, string RunnerId)> PollWorkAnyAsync()
    {
        return await PollWorkAsync(_runnerId!);
    }

    protected async Task<(WorkDispatch Work, string RunnerId)> PollWorkAsync(string runnerId)
    {
        await EnsureRunnerForCurrentWorkflowAsync(runnerId);
        var dispatch = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<Mohist.Server.Runner.Services.DispatchService>();
        WorkDispatch? work = null;
        await TestWait.ForAsync(
            async () =>
            {
                var resp = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
                work = resp.Dispatches.FirstOrDefault();
                return work;
            },
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive work for workflow '{_workflowId}'");
        return (work!, runnerId);
    }

    private async Task EnsureRunnerForCurrentWorkflowAsync(string runnerId)
    {
        if (string.IsNullOrWhiteSpace(_workflowId)) return;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(_workflowId)));
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
        // Report direct to the owning workflow grain (the runner grain no
        // longer relays workflow reports). The work item is reconstructed from
        // the persisted run; translation mirrors the API /report route.
        await ReportWorkflowDirectAsync(runnerId, workflowRunId, workId, result);
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
        var output = JsonSerializer.SerializeToElement(checkResults.Select(cr => new Dictionary<string, string?>
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

    /// <summary>
    /// Reports a workflow work result direct to the owning grain, mirroring
    /// the API /report route (translation is a stateless service). The runner
    /// grain no longer relays workflow reports.
    /// </summary>
    protected async Task ReportWorkflowDirectAsync(string runnerId, string workflowRunId, string workId, WorkResult result)
    {
        await DispatchTestExtensions.ReportWorkflowDirectAsync(
            Grains, _fixture.Cluster.GetSiloServiceProvider(null),
            runnerId, workflowRunId, workId, result);
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
        return new WorkflowDefinition(
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
        var templateId = workflowId;
        var templateJson = WorkflowGrainTestHelpers.SerializeProfile(definition, templateId);

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

    protected async Task PatchProjectVariablesAsync(string projectId, VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var manager = new ProjectWorkflowProfileManager(factory, null!, new PromptTemplateEngine());
        await manager.PatchVariablesAsync(projectId, patch);
    }

    protected async Task PatchIssueVariablesAsync(int issueNumber, VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        var projectId = TestProjectId(_workflowId!);
        await using (var db = new MohistDbContext(options))
        {
            if (!await db.Issues.AnyAsync(row => row.ProjectId == projectId && row.Number == issueNumber))
            {
                db.Issues.Add(new IssueRow
                {
                    State = IssueStore.Serialize(new DomainIssue
                    {
                        ProjectId = projectId,
                        Number = issueNumber,
                        Title = $"Issue {issueNumber}",
                        Priority = "p2",
                    }),
                });
                await db.SaveChangesAsync();
            }
        }
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var manager = new IssueWorkflowProfileManager(factory);
        await manager.PatchVariablesAsync(projectId, issueNumber, patch);
    }

    protected static WorkflowDefinition TwoStages()
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

    protected static WorkflowDefinition ApprovalStage()
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
