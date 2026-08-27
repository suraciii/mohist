using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
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

    public WorkflowReportService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        WorkflowItemTranslator translator)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _translator = translator;
    }

    public async Task<(string Ack, string? WorkflowStatus)> ReportAsync(
        string runnerId,
        string workflowRunId,
        string workId,
        string? actionAttemptId,
        WorkResult result,
        CancellationToken ct = default)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return ("refused", null);

        var item = run.FindReportShape(actionAttemptId, workId);
        if (item is null)
            return ("refused", null);
        if ((item.IsTask && !WorkReportStatus.IsWork(result.Status))
            || (item.IsChecks && !WorkReportStatus.IsChecks(result.Status)))
            return ("refused", null);

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var report = _translator.TranslateResult(item, result, workflowRunId);

        WorkReportVerdict ack;
        try
        {
            ack = report switch
            {
                WorkflowItemTranslator.InboundReport.Task task when item.IsTask && actionAttemptId is not null =>
                    await workflow.ReceiveTaskReportAsync(
                        runnerId,
                        workId,
                        task.Value with { ActionAttemptId = actionAttemptId }),
                WorkflowItemTranslator.InboundReport.Checks checks when item.IsChecks =>
                    await workflow.ReceiveCheckReportAsync(runnerId, workId, checks.Value),
                _ => WorkReportVerdict.Refused,
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            ack = WorkReportVerdict.Outstanding;
        }
        return (VerdictValue(ack), await workflow.GetRunStatusAsync());
    }

    private static string VerdictValue(WorkReportVerdict verdict) => verdict.ToString().ToLowerInvariant();

}
