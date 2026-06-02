using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Recovery;
using Mohist.Server.Workflow.Storage;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[CollectionDefinition("WorkflowBacklogRecovery", DisableParallelization = true)]
public class WorkflowBacklogRecoveryCollection : ICollectionFixture<WorkflowGrainFixture>;

[Collection("WorkflowBacklogRecovery")]
public class WorkflowBacklogRecoverySpecs : WorkflowGrainSpecs, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowBacklogRecoverySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    public async Task InitializeAsync()
    {
        await ResetPersistentStateAsync();
        await ResetClusterActivationsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Recovery_UsesWorkflowVariables_WhenMetadataProjectIdIsMissing()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        await SeedRunnableWorkflowAsync(workflowId, metadataProjectId: null, annotationProjectId: null, variablesProjectId: "project-from-vars");

        await RunRecoveryAsync();

        var backlog = await LoadBacklogStateAsync("project-from-vars");
        Assert.NotNull(backlog);
        Assert.Contains(workflowId, backlog!.Waiting);
        Assert.Null(await LoadBacklogStateAsync("default"));
    }

    [Fact]
    public async Task Recovery_DoesNotRegisterLeasedWorkflow_AsDispatchableBacklog()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        await SeedRunnableWorkflowAsync(workflowId, metadataProjectId: null, annotationProjectId: null, variablesProjectId: "project-from-vars");
        await SeedLeaseAsync(workflowId, new WorkLease("work-1", "task", "build", "task-1", "Task 1", "runner-1"));

        await RunRecoveryAsync();

        var backlog = await LoadBacklogStateAsync("project-from-vars");
        Assert.True(backlog is null || !backlog.All.Contains(workflowId));
    }

    [Fact(Skip = "Replaced by WorkflowQueueSchedulerSpecs.Maintain_* specs; persisted WorkflowLeases/BacklogStates are no longer authoritative.")]
    public async Task Recovery_ReconcilesStalePausedAndTerminalRows_AndKeepsConsistentRunnableState()
    {
        var pausedWorkflowId = $"wf-paused-{Guid.NewGuid():N}";
        var failedWorkflowId = $"wf-failed-{Guid.NewGuid():N}";
        var waitingWorkflowId = $"wf-waiting-{Guid.NewGuid():N}";
        var runningWorkflowId = $"wf-running-{Guid.NewGuid():N}";
        var missingWorkflowId = $"wf-missing-{Guid.NewGuid():N}";

        await SeedWorkflowAsync(pausedWorkflowId, WorkflowRunStatus.Paused, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: true);
        await SeedWorkflowAsync(failedWorkflowId, WorkflowRunStatus.Failed, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: true, failureMessage: "boom");
        await SeedWorkflowAsync(waitingWorkflowId, WorkflowRunStatus.Running, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: true);
        await SeedWorkflowAsync(runningWorkflowId, WorkflowRunStatus.Running, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: true);

        await SeedBacklogStateAsync("test-project", new WorkflowBacklogState(
            [pausedWorkflowId, failedWorkflowId, waitingWorkflowId, missingWorkflowId],
            new Dictionary<string, string>
            {
                [pausedWorkflowId] = "runner-paused",
                [failedWorkflowId] = "runner-failed",
                [runningWorkflowId] = "runner-live",
                [missingWorkflowId] = "runner-missing"
            },
            [pausedWorkflowId, failedWorkflowId, waitingWorkflowId, runningWorkflowId, missingWorkflowId]));

        await SeedLeaseAsync(pausedWorkflowId, new WorkLease("paused-work", "task", "build", "task-1", "Paused Task", "runner-paused"));
        await SeedLeaseAsync(failedWorkflowId, new WorkLease("failed-work", "task", "build", "task-1", "Failed Task", "runner-failed"));
        await SeedLeaseAsync(runningWorkflowId, new WorkLease("running-work", "task", "build", "task-1", "Running Task", "runner-live"));
        await SeedLeaseAsync(missingWorkflowId, new WorkLease("missing-work", "task", "build", "task-1", "Missing Task", "runner-missing"));

        await RunRecoveryAsync();

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(backlog);
        Assert.Equal([waitingWorkflowId], backlog!.Waiting);
        Assert.Equal("runner-live", Assert.Single(backlog.Running).Value);
        Assert.Equal(new HashSet<string> { waitingWorkflowId, runningWorkflowId }, backlog.All);

        Assert.Null(await LoadLeaseJsonAsync(pausedWorkflowId));
        Assert.Null(await LoadLeaseJsonAsync(failedWorkflowId));
        Assert.Null(await LoadLeaseJsonAsync(missingWorkflowId));
        Assert.NotNull(await LoadLeaseJsonAsync(runningWorkflowId));
    }

    [Fact(Skip = "Replaced by WorkflowQueue scheduler lease recovery; persisted WorkflowLeases/BacklogStates are no longer authoritative.")]
    public async Task Recovery_RestoresRunnerAssignment_ForRecoveredRunningLease()
    {
        var workflowId = $"wf-running-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";
        var workId = "task-1.1";

        await SeedWorkflowAsync(workflowId, WorkflowRunStatus.Running, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: true);
        await SeedBacklogStateAsync("test-project", new WorkflowBacklogState(
            [],
            new Dictionary<string, string> { [workflowId] = runnerId },
            [workflowId]));
        await SeedLeaseAsync(workflowId, new WorkLease(workId, "task", "build", "task-1", "Task 1", runnerId));

        await RunRecoveryAsync();

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(backlog);
        Assert.Empty(backlog!.Waiting);
        Assert.Equal(runnerId, Assert.Single(backlog.Running).Value);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", "test-project"));
        Assert.Null(await runner.PollAsync());
        var reportedWorkflowId = await runner.ReportAsync(workId, new WorkDispatchResult("completed"));

        Assert.Equal(workflowId, reportedWorkflowId);

        var afterReport = await runner.PollAsync();
        Assert.Null(afterReport);

        var backlogAfterReport = await LoadBacklogStateAsync("test-project");
        Assert.True(backlogAfterReport is null || (!backlogAfterReport.Waiting.Contains(workflowId) && !backlogAfterReport.Running.ContainsKey(workflowId) && !backlogAfterReport.All.Contains(workflowId)));
        Assert.Null(await LoadLeaseJsonAsync(workflowId));
    }

    [Fact(Skip = "Replaced by WorkflowQueueSchedulerSpecs.Maintain_ClearsLeasedQueueRow_WhenWorkflowHasNoRunnableWork; persisted WorkflowLeases/BacklogStates are no longer authoritative.")]
    public async Task Recovery_RemovesLeasedRunningClaim_WhenRecoveredWorkflowCannotDispatchWork()
    {
        var workflowId = $"wf-no-work-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await SeedWorkflowAsync(workflowId, WorkflowRunStatus.Running, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: false);
        await SeedBacklogStateAsync("test-project", new WorkflowBacklogState(
            [],
            new Dictionary<string, string> { [workflowId] = runnerId },
            [workflowId]));
        await SeedLeaseAsync(workflowId, new WorkLease("stale-work", "task", "build", "task-1", "Task 1", runnerId));

        await RunRecoveryAsync();

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.True(backlog is null || (!backlog.Waiting.Contains(workflowId) && !backlog.Running.ContainsKey(workflowId) && !backlog.All.Contains(workflowId)));
        Assert.Null(await LoadLeaseJsonAsync(workflowId));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", "test-project"));
        Assert.Null(await runner.PollAsync());
    }

    [Fact(Skip = "Replaced by WorkflowQueueSchedulerSpecs.Maintain_PreservesLeasedQueueRow_WhenWorkflowIsRunnable; mismatched lease work details no longer trigger a backlog scrub in the new authority.")]
    public async Task Recovery_RemovesLeasedRunningClaim_WhenPersistedLeaseDoesNotMatchDispatchableWork()
    {
        var workflowId = $"wf-mismatch-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await SeedWorkflowAsync(workflowId, WorkflowRunStatus.Running, annotationProjectId: "test-project", variablesProjectId: "test-project", hasWork: true);
        await SeedBacklogStateAsync("test-project", new WorkflowBacklogState(
            [],
            new Dictionary<string, string> { [workflowId] = runnerId },
            [workflowId]));
        await SeedLeaseAsync(workflowId, new WorkLease("stale-work", "task", "build", "task-x", "Task X", runnerId));

        await RunRecoveryAsync();

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.True(backlog is null || (!backlog.Waiting.Contains(workflowId) && !backlog.Running.ContainsKey(workflowId) && !backlog.All.Contains(workflowId)));
        Assert.Null(await LoadLeaseJsonAsync(workflowId));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", "test-project"));
        Assert.Null(await runner.PollAsync());
    }

    private async Task ResetClusterActivationsAsync()
    {
        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
    }

    [Fact]
    public async Task Recovery_MissingProjectIdentity_IsExplicitAndNotDefaulted()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        var logger = new ListLogger<WorkflowBacklogRecoveryService>();
        await SeedRunnableWorkflowAsync(workflowId, metadataProjectId: null, annotationProjectId: null, variablesProjectId: null);

        await RunRecoveryAsync(logger);

        Assert.Null(await LoadBacklogStateAsync("default"));
        Assert.Contains(logger.Warnings, message => message.Contains(workflowId, StringComparison.Ordinal)
            && message.Contains("missing durable project identity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BacklogRegisterClaimAndRelease_RepairWaitingRunningExclusivity()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await SeedBacklogStateAsync("test-project", new WorkflowBacklogState(
            [workflowId],
            new Dictionary<string, string> { [workflowId] = runnerId },
            [workflowId]));

        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));

        var duplicateClaim = await backlog.ClaimAsync(runnerId);
        Assert.Null(duplicateClaim);

        var afterRegister = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(afterRegister);
        Assert.Empty(afterRegister!.Waiting);
        Assert.Equal(runnerId, Assert.Single(afterRegister.Running).Value);

        await backlog.RegisterAsync(workflowId);

        afterRegister = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(afterRegister);
        Assert.Empty(afterRegister!.Waiting);
        Assert.Equal(runnerId, Assert.Single(afterRegister.Running).Value);

        await backlog.RequeueAsync(workflowId);

        var afterRequeue = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(afterRequeue);
        Assert.Equal([workflowId], afterRequeue!.Waiting);
        Assert.Empty(afterRequeue.Running);

        var claimedWorkflowId = await backlog.ClaimAsync(runnerId);
        Assert.Equal(workflowId, claimedWorkflowId);

        var afterClaim = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(afterClaim);
        Assert.Empty(afterClaim!.Waiting);
        Assert.Equal(runnerId, Assert.Single(afterClaim.Running).Value);
        Assert.False(afterClaim.Waiting.Contains(workflowId) && afterClaim.Running.ContainsKey(workflowId));

        await backlog.ReleaseAsync(workflowId);
        await backlog.ReleaseAsync(workflowId);

        var afterRelease = await LoadBacklogStateAsync("test-project");
        Assert.True(afterRelease is null || (afterRelease.Waiting.Count == 0 && afterRelease.Running.Count == 0 && afterRelease.All.Count == 0));
    }

    [Fact]
    public void TryRestoreRunnableWorkflow_PausedWithPendingWork_DoesNotRecover()
    {
        var run = RunnableRun("wf-paused");
        run.Pause();

        var recovered = TryRestoreRunnableWorkflow(JsonSerializer.Serialize(run, JsonOptions), out var hasWork);

        Assert.False(recovered);
        Assert.False(hasWork);
    }

    [Fact]
    public void TryRestoreRunnableWorkflow_RunningWithPendingWork_Recovers()
    {
        var run = RunnableRun("wf-running");

        var recovered = TryRestoreRunnableWorkflow(JsonSerializer.Serialize(run, JsonOptions), out var hasWork);

        Assert.True(recovered);
        Assert.True(hasWork);
    }

    private async Task RunRecoveryAsync(ListLogger<WorkflowBacklogRecoveryService>? logger = null)
    {
        var recovery = new WorkflowBacklogRecoveryService(
            new PooledDbContextFactory<MohistDbContext>(new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_fixture.ConnectionString)
                .Options),
            Grains,
            logger ?? new ListLogger<WorkflowBacklogRecoveryService>());

        await recovery.StartAsync(CancellationToken.None);
    }

    private async Task SeedRunnableWorkflowAsync(string workflowId, string? metadataProjectId, string? annotationProjectId, string? variablesProjectId)
    {
        var run = CreateRun(workflowId, WorkflowRunStatus.Running, annotationProjectId, hasWork: true);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowId,
            State = JsonSerializer.Serialize(run)
        });

        if (variablesProjectId is not null)
        {
            db.WorkflowVariables.Add(new WorkflowVariablesRow
            {
                WorkflowRunId = workflowId,
                StateJson = JsonSerializer.Serialize(new WorkflowExecutionContext($"{{\"project\":{{\"id\":\"{variablesProjectId}\"}}}}"))
            });
        }

        await db.SaveChangesAsync();

        if (metadataProjectId is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_runs SET MetadataProjectId = {metadataProjectId} WHERE WorkflowRunId = {workflowId}");
        }
    }

    private async Task SeedWorkflowAsync(string workflowId, WorkflowRunStatus status, string? annotationProjectId, string? variablesProjectId, bool hasWork, string? failureMessage = null)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowId,
            State = JsonSerializer.Serialize(CreateRun(workflowId, status, annotationProjectId, hasWork, failureMessage))
        });

        if (variablesProjectId is not null)
        {
            db.WorkflowVariables.Add(new WorkflowVariablesRow
            {
                WorkflowRunId = workflowId,
                StateJson = JsonSerializer.Serialize(new WorkflowExecutionContext($"{{\"project\":{{\"id\":\"{variablesProjectId}\"}}}}"))
            });
        }

        await db.SaveChangesAsync();
    }

    private static WorkflowRun CreateRun(string workflowId, WorkflowRunStatus status, string? annotationProjectId, bool hasWork, string? failureMessage = null)
    {
        var taskStatus = hasWork ? TaskRunStatus.Pending : TaskRunStatus.Completed;
        DateTimeOffset? completedAt = status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed ? DateTimeOffset.UtcNow : null;

        return new WorkflowRun
        {
            Id = workflowId,
            Metadata = new WorkflowRunMetadata(
                Name: "test",
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: annotationProjectId is null ? null : new Dictionary<string, string> { ["projectId"] = annotationProjectId }),
            Status = status,
            CurrentStageId = "build",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt,
            Failure = failureMessage is null ? null : new FailureDetails(FailureReason.TaskFailed, "build", "task-1", Message: failureMessage),
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    Initialized = true,
                    RequiresApproval = false,
                    Failure = failureMessage is null ? null : new FailureDetails(FailureReason.TaskFailed, "build", "task-1", Message: failureMessage),
                    Tasks = [new TaskRun { Id = "task-1.1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = taskStatus }],
                    Checks = []
                }
            ]
        };
    }

    private async Task<WorkflowBacklogState?> LoadBacklogStateAsync(string projectId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.BacklogStates.FindAsync(projectId);
        return row is null ? null : JsonSerializer.Deserialize<WorkflowBacklogState>(row.StateJson);
    }

    private static WorkflowRun RunnableRun(string id)
    {
        var run = WorkflowRun.Create(id, SingleStage());
        run.Start();
        run.CurrentStage().Initialized = true;
        run.CurrentStage().Tasks.Add(new TaskRun
        {
            Id = "task-1.1",
            DefinitionId = "task-1",
            Attempt = 1,
            Title = "Task 1",
            Uses = "spec/task",
            Status = TaskRunStatus.Pending
        });
        return run;
    }

    private static WorkflowDefinition SingleStage() => new("spec/workflow",
    [
        new StageDefinition("build", [new TaskDefinition("task-1", "Task 1", "spec/task")], [])
    ]);

    private static bool TryRestoreRunnableWorkflow(string jsonState, out bool hasWork)
    {
        object?[] args = [jsonState, null, false];
        var result = (bool)typeof(WorkflowBacklogRecoveryService)
            .GetMethod("TryRestoreRunnableWorkflow", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;
        hasWork = (bool)args[2]!;
        return result;
    }

    private async Task<string?> LoadLeaseJsonAsync(string workflowId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowLeases.FindAsync(workflowId);
        return row?.StateJson;
    }

    private async Task SeedBacklogStateAsync(string projectId, WorkflowBacklogState state)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.BacklogStates.FindAsync(projectId);
        var json = JsonSerializer.Serialize(state, WorkflowStorageJson.Options);

        if (row is null)
        {
            db.BacklogStates.Add(new BacklogStateRow
            {
                ProjectId = projectId,
                StateJson = json
            });
        }
        else
        {
            row.StateJson = json;
        }

        await db.SaveChangesAsync();
    }

    private async Task ResetPersistentStateAsync()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        db.BacklogStates.RemoveRange(db.BacklogStates);
        db.WorkflowLeases.RemoveRange(db.WorkflowLeases);
        db.WorkflowQueue.RemoveRange(db.WorkflowQueue);
        db.WorkflowRuns.RemoveRange(db.WorkflowRuns);
        db.WorkflowVariables.RemoveRange(db.WorkflowVariables);
        await db.SaveChangesAsync();
    }

    [Fact(Skip = "Replaced by WorkflowQueue scheduler claim cleanup; persisted WorkflowLeases/BacklogStates are no longer authoritative.")]
    public async Task Unschedule_ClearsLeaseBacklogDiagnostics_AndRunnerAssignment_Idempotently()
    {
        var workflow = await StartWorkflowAsync(new Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition("spec/workflow",
        [
            new Mohist.Server.Workflow.Domain.Definition.StageDefinition("build",
                [new Mohist.Server.Workflow.Domain.Definition.TaskDefinition("task-1", "Task 1", "spec/task")],
                [])
        ]));

        var (work, runnerId) = await PollWorkAnyAsync();
        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));

        var before = await LoadBacklogStateAsync("test-project");
        Assert.NotNull(before);
        Assert.Equal(runnerId, Assert.Single(before!.Running).Value);
        Assert.NotNull(await LoadLeaseJsonAsync(_workflowId!));

        await workflow.ReleaseClaimAsync("stale ownership repaired");
        await workflow.ReleaseClaimAsync("stale ownership repaired");

        var after = await LoadBacklogStateAsync("test-project");
        Assert.True(after is null || (after.Waiting.Count == 0 && after.Running.Count == 0 && after.All.Count == 0));
        Assert.Null(await LoadLeaseJsonAsync(_workflowId!));

        var events = (await EventStore.ListWorkflowEventsAsync(_workflowId!)).ToList();
        var released = events.Single(e => e.Type == "workflow_claim_released");
        Assert.Equal("stale ownership repaired", released.Message);

        Assert.Null(await backlog.ClaimAsync($"probe-{Guid.NewGuid():N}"));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());
    }

    [Fact(Skip = "Replaced by WorkflowQueueSchedulerSpecs.Maintain_ClearsLeasedQueueRow_WhenWorkflowRunIsMissing and the runner-poll path through RunnerGrain; persisted WorkflowLeases/BacklogStates are no longer authoritative.")]
    public async Task PollClaimNoWork_RepairsClaim_ClearsLease_AndDoesNotConsumeCapacity()
    {
        var runnerId = await RegisterRunnerAsync("repair-runner", maxWorkflowSlots: 1);
        _runnerId = runnerId;

        var staleWorkflowId = $"wf-stale-{Guid.NewGuid():N}";
        var activeWorkflowId = $"wf-active-{Guid.NewGuid():N}";

        var staleWorkflow = await CreateWorkflowAsync(staleWorkflowId);
        await staleWorkflow.StartAsync(SingleStage(checks: []), TestInput());
        var backlogGrain = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));
        Assert.Equal(staleWorkflowId, await backlogGrain.ClaimAsync(runnerId));
        Assert.NotNull(await staleWorkflow.GetWorkAsync(runnerId));
        var staleLease = await LoadLeaseJsonAsync(staleWorkflowId);
        Assert.NotNull(staleLease);
        await staleWorkflow.ReleaseClaimAsync("hold");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.True(backlog is null || (!backlog.Waiting.Contains(staleWorkflowId) && !backlog.Running.ContainsKey(staleWorkflowId) && !backlog.All.Contains(staleWorkflowId)));
        Assert.Null(await LoadLeaseJsonAsync(staleWorkflowId));

        var events = (await EventStore.ListWorkflowEventsAsync(staleWorkflowId)).ToList();
        var released = events.Single(e => e.Type == "workflow_claim_released");
        Assert.Equal("hold", released.Message);

        var activeWorkflow = await CreateWorkflowAsync(activeWorkflowId);
        _workflowId = activeWorkflowId;
        await activeWorkflow.StartAsync(SingleStage(checks: []), TestInput());

        var dispatched = await runner.PollAsync();
        Assert.NotNull(dispatched);
        Assert.Equal(activeWorkflowId, dispatched!.WorkflowRunId);
        await ReportAsync(runnerId, dispatched, "completed");
        Assert.Null(await runner.PollAsync());
    }

    [Fact(Skip = "Replaced by the terminal-transition path in WorkflowGrain.ReportResultAsync (which calls ReleaseClaimAsync → scheduler.ClearAsync); persisted WorkflowLeases/BacklogStates are no longer authoritative.")]
    public async Task CompletedWorkflow_ClearsBacklogAndLease_OnTerminalTransition()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (taskWork, runnerId) = await PollWorkAnyAsync();
        var backlogGrain = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));

        await backlogGrain.RegisterAsync(_workflowId!);
        await SeedLeaseAsync(_workflowId!, new WorkLease("checks-build", "checks", "build", "check-1", "Stage checks", runnerId));
        await ReportAsync(runnerId, taskWork, "completed");

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.True(backlog is null || (!backlog.Waiting.Contains(_workflowId!) && !backlog.Running.ContainsKey(_workflowId!) && !backlog.All.Contains(_workflowId!)));
        Assert.Null(await LoadLeaseJsonAsync(_workflowId!));
    }

    [Fact]
    public async Task FailedWorkflow_ClearsBacklogAndLease_OnTerminalTransition()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork, "failed", "boom");

        var backlog = await LoadBacklogStateAsync("test-project");
        Assert.True(backlog is null || (!backlog.Waiting.Contains(_workflowId!) && !backlog.Running.ContainsKey(_workflowId!) && !backlog.All.Contains(_workflowId!)));
        Assert.Null(await LoadLeaseJsonAsync(_workflowId!));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
