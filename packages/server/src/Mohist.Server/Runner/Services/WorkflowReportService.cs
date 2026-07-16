using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

public sealed class WorkflowReportService : IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<WorkflowReportService> _log;

    public WorkflowReportService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        WorkflowItemTranslator translator,
        ILogger<WorkflowReportService> log)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _translator = translator;
        _log = log;
    }

    public async Task<(string Ack, string? WorkflowStatus)> ReportAsync(
        string runnerId,
        string workflowRunId,
        string workId,
        WorkResult result,
        CancellationToken ct = default)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return ("missing-workflow", null);

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var workerId = runnerId;

        var activeWork = run.FindActiveWork(workId, workerId);
        if (activeWork is null)
            return (ReportAck.Stale.ToString().ToLowerInvariant(), null);

        if (activeWork.IsTask)
            RuntimeTaskFollowUps.Project(result.AddTasks);

        var report = await _translator.TranslateResultAsync(activeWork.Item, result, workflowRunId, run);
        ReportAck ack = report switch
        {
            WorkflowItemTranslator.InboundReport.Task t when activeWork.IsTask =>
                await workflow.ReceiveTaskReportAsync(workerId, workId, t.Value),
            WorkflowItemTranslator.InboundReport.Checks c when activeWork.IsChecks =>
                await workflow.ReceiveCheckReportAsync(workerId, workId, c.Value),
            _ => ReportAck.Stale,
        };
        return (ack.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
    }
}
