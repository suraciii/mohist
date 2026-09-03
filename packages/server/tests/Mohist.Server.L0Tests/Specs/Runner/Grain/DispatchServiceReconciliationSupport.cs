using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Runner.Grain;

[Collection("OrleansGrainL0")]
[Trait("level", "L0")]
public partial class DispatchServiceReconciliationSpecs : WorkflowGrainTestContext
{
    public DispatchServiceReconciliationSpecs(OrleansL0WorkflowGrainFixture fixture) : base(fixture) { }

    private DispatchService Dispatch => _fixture.Cluster.GetSiloServiceProvider(null)
        .GetRequiredService<IServiceScopeFactory>().CreateScope()
        .ServiceProvider.GetRequiredService<DispatchService>();

    private static string WorkKey(string workflowRunId, string workId) =>
        $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}:{workId}";

    private async Task<(string RunnerId, string[] WorkflowIds)> StartReadyWorkflowsAsync(
        string prefix,
        int count,
        int slots)
    {
        await ClearBacklogAsync();
        var projectId = $"{prefix}-project";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"{prefix}-runner", slots);
        var workflowIds = new string[count];
        for (var index = 0; index < count; index++)
        {
            var workflowId = $"{prefix}-workflow-{index}";
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
            await workflow.StartAsync(TestInput(projectId));
            workflowIds[index] = workflowId;
        }
        return (runnerId, workflowIds);
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
        var run = WorkflowRun.Create(workflowRunId, new WorkflowDefinition([
            new StageDefinition("build", [new TaskDefinition("task-1", "Task 1", "spec/task")], [])
        ]), DateTimeOffset.UnixEpoch);
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build", Attempt = 1, Initialized = true, RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = { new WorkflowActionAttempt
            {
                Id = "task-1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1",
                Status = status == "Running" ? WorkflowActionAttemptStatus.Running : WorkflowActionAttemptStatus.Pending,
                WorkerId = runnerId,
            } },
        });
        run.CurrentStageId = "build";
        run.Status = Enum.Parse<WorkflowRunStatus>(status);
        run.Assignment = new WorkflowAssignment(runnerId, TestTime.UtcNow);
        var projection = WorkflowRunWorkProjectionBuilder.Build(run);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId, State = JSON.Serialize(run),
            ActiveWorkId = activeWork ? projection.ActiveWorkId : null,
            ActiveWorkerId = activeWork ? activeWorkerId ?? projection.ActiveWorkerId : null,
            AttentionStatus = null,
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
            Id = id, ProjectId = projectId, Name = agentName, Status = AgentStatus.Archived,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = id, ProjectId = projectId, Name = agentName, Status = AgentStatus.Archived,
            }, JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
