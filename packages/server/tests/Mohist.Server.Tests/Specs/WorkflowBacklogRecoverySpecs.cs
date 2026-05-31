using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Recovery;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowBacklogRecoverySpecs : WorkflowGrainSpecs
{
    public WorkflowBacklogRecoverySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

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
        var run = new WorkflowRun
        {
            Id = workflowId,
            Metadata = new WorkflowRunMetadata(
                Name: "test",
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: annotationProjectId is null ? null : new Dictionary<string, string> { ["projectId"] = annotationProjectId }),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            StartedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    Initialized = true,
                    RequiresApproval = false,
                    Tasks = [new TaskRun { Id = "task-1.1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = TaskRunStatus.Pending }],
                    Checks = []
                }
            ]
        };

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

    private async Task<WorkflowBacklogState?> LoadBacklogStateAsync(string projectId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.BacklogStates.FindAsync(projectId);
        return row is null ? null : JsonSerializer.Deserialize<WorkflowBacklogState>(row.StateJson);
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
