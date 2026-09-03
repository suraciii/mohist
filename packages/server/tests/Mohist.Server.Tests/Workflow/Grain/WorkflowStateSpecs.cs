using Mohist.Server.Runner.Grains;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure.Data.Workflow;
using System.Text.Json;
using Mohist.Server.Workflow.Grains;
using Xunit;
using System.Linq;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Workflow;

namespace Mohist.Server.Tests.Workflow.Grain;

[Collection("WorkflowRecovery")]
[Trait("level", "L1")]
public class WorkflowStateSpecs : WorkflowGrainSpecs
{
    public WorkflowStateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task StartedWorkflow_RunnerAssignsFromBacklog()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []));
        await workflow.StartAsync(TestInput());

        var work = await runner.PollAsync(Services);
        Assert.NotNull(work);
    }

    [Fact]
    public async Task StartWithoutRunner_RunnerAssignsFromBacklogLater()
    {
        var workflow = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var work = await runner.PollAsync(Services);
        Assert.NotNull(work);
        Assert.StartsWith("task-1.", work.WorkId);
    }

}
