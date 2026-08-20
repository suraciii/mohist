using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

public partial class DispatchServiceReconciliationSpecs
{
    private async Task InsertUnresolvedAgentRunAsync(
        string workflowRunId,
        string assignedRunner,
        string settlementRunner,
        bool binding)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition(
            [new StageDefinition("build",
                [new TaskDefinition("agent", "Agent", "mohist/pi")],
                [])]),
            DateTimeOffset.UnixEpoch);
        var task = new TaskRun
        {
            Id = "agent",
            DefinitionId = "agent",
            Attempt = 1,
            Title = "Agent",
            Uses = "mohist/pi",
            Status = TaskRunStatus.Running,
            WorkId = "agent",
            WorkerId = assignedRunner,
            AgentResultSettlement = new AgentResultSettlement
            {
                State = AgentResultSettlementState.Unknown,
                TaskRunId = "agent",
                WorkId = "agent",
                RunnerId = settlementRunner,
                Runtime = binding ? "pi" : null,
                RuntimeSessionId = binding ? "/pi/sessions/spec" : null,
            },
        };
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = { task },
        });
        run.CurrentStageId = "build";
        run.Status = WorkflowRunStatus.Running;
        run.Assignment = new WorkflowAssignment(assignedRunner, TestTime.UtcNow);

        // Keep the DB row consistent with the active-work projection the store
        // would have written, so the query reaches the settlement-routing check.
        var projection = WorkflowRunWorkProjectionBuilder.Build(run);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
            ActiveWorkId = projection.ActiveWorkId,
            ActiveWorkerId = projection.ActiveWorkerId,
            AttentionStatus = run.HasBlockedAgentResult() ? "blocked" : null,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a legacy/released <c>Blocked</c> agent run exactly as an older
    /// binary would have persisted it: the stale assignment and active-work
    /// columns are still present and the row is indexed with blocked attention.
    /// This simulates the rollout window before the grain repair path clears the
    /// persisted assignment, plus a dispatch snapshot left behind while cleanup
    /// is mid-retry.
    /// </summary>
    private async Task InsertLegacyBlockedAgentRunAsync(
        string workflowRunId,
        string assignedRunner,
        string workId,
        bool withSnapshot)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition(
            [new StageDefinition("build",
                [new TaskDefinition("agent", "Agent", "mohist/pi")],
                [])]),
            DateTimeOffset.UnixEpoch);
        var task = new TaskRun
        {
            Id = "agent",
            DefinitionId = "agent",
            Attempt = 1,
            Title = "Agent",
            Uses = "mohist/pi",
            Status = TaskRunStatus.Running,
            WorkId = workId,
            WorkerId = assignedRunner,
            AgentResultSettlement = new AgentResultSettlement
            {
                State = AgentResultSettlementState.Blocked,
                TaskRunId = "agent",
                WorkId = workId,
                RunnerId = assignedRunner,
                Runtime = "pi",
                RuntimeSessionId = "/pi/sessions/spec",
                ReasonCode = "stop-unconfirmed",
                DeadlineAt = TestTime.UtcNow,
            },
        };
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = { task },
        });
        run.CurrentStageId = "build";
        run.Status = WorkflowRunStatus.Running;
        run.Assignment = new WorkflowAssignment(assignedRunner, TestTime.UtcNow);

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
            // Deliberately stale: the old binary projected active work even for
            // a blocked run, so only the indexed blocked attention excludes it.
            ActiveWorkId = workId,
            ActiveWorkerId = assignedRunner,
            AttentionStatus = "blocked",
        });
        await db.SaveChangesAsync();

        if (withSnapshot)
        {
            var snapshots = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();
            await snapshots.SaveFirstJsonAsync(workflowRunId, workId, "{}");
        }
    }

    private async Task InsertStatusRowAsync(
        string workflowRunId,
        string status,
        string runnerId,
        bool activeWork = true,
        string? activeWorkerId = null)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition(
            [new StageDefinition("build",
                [new TaskDefinition("task-1", "Task 1", "spec/task")],
                [])]),
            DateTimeOffset.UnixEpoch);
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks =
            {
                new TaskRun
                {
                    Id = "task-1",
                    DefinitionId = "task-1",
                    Attempt = 1,
                    Title = "Task 1",
                    Status = status == "Running"
                        ? TaskRunStatus.Running
                        : TaskRunStatus.Pending,
                    WorkerId = runnerId,
                },
            },
        });
        run.CurrentStageId = "build";
        run.Status = Enum.Parse<WorkflowRunStatus>(status);
        run.Assignment = new WorkflowAssignment(runnerId, TestTime.UtcNow);

        // Insert through the same DB layout the store writes: the active-work
        // and attention columns are what the Runner capacity queries filter.
        var projection = WorkflowRunWorkProjectionBuilder.Build(run);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
            ActiveWorkId = activeWork ? projection.ActiveWorkId : null,
            ActiveWorkerId = activeWork ? activeWorkerId ?? projection.ActiveWorkerId : null,
            AttentionStatus = run.HasBlockedAgentResult() ? "blocked" : null,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedArchivedAgentAsync(string projectId, string agentName)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var id = $"agent_{Guid.NewGuid():N}";
        db.Agents.Add(new AgentRow
        {
            Id = id,
            ProjectId = projectId,
            Name = agentName,
            Status = AgentStatus.Archived,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = id,
                ProjectId = projectId,
                Name = agentName,
                Status = AgentStatus.Archived,
            }, Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
