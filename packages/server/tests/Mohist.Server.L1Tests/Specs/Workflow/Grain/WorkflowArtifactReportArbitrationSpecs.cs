using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public sealed class WorkflowArtifactReportArbitrationSpecs : WorkflowGrainSpecs
{
    public WorkflowArtifactReportArbitrationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ConcurrentSuccessAndFailure_SelectsExactlyOneTerminalResult()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var service = Services.GetRequiredService<WorkflowReportService>();

        var reports = await Task.WhenAll(
            service.ReportAsync(
                runnerId,
                work.WorkflowRunId,
                work.WorkId,
                work.ActionAttemptId,
                new WorkResult("completed")),
            service.ReportAsync(
                runnerId,
                work.WorkflowRunId,
                work.WorkId,
                work.ActionAttemptId,
                new WorkResult("failed", "runner failed")));

        Assert.Equal(["accepted", "refused"], reports.Select(report => report.Ack).Order().ToArray());
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Contains(await workflow.GetRunStatusAsync(), new[] { "Completed", "Failed" });
        var eventTypes = (await EventStore.ListAsync(work.WorkflowRunId))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.Equal(1, eventTypes.Count(type =>
            type is EventCatalog.ReverseDns.TaskCompleted or EventCatalog.ReverseDns.TaskFailed));
    }
}
