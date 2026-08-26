using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    public async Task<WorkReportVerdict> FailActiveWorkAsync(
        string workerId,
        string workId,
        string processGeneration,
        string message)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return WorkReportVerdict.Refused;
        var activeWork = _run.CurrentActiveWorkFor(workerId);
        if (activeWork is null
            || !string.Equals(activeWork.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(activeWork.ProcessGeneration, processGeneration, StringComparison.Ordinal))
            return WorkReportVerdict.Refused;
        return await FailActiveWorkCoreAsync(activeWork, message);
    }

    private async Task<WorkReportVerdict> FailActiveWorkCoreAsync(WorkflowActiveWork activeWork, string message)
    {
        var now = Now();
        IReadOnlyList<WorkflowEvent> events;
        string? terminalWorkId = null;
        if (activeWork.IsTask)
        {
            terminalWorkId = activeWork.WorkId;
            events = _run!.FailTask(new TaskResult("failed", message), now);
        }
        else if (activeWork.IsChecks)
        {
            events = _run!.FailRunningChecks(message, now);
        }
        else
        {
            return WorkReportVerdict.Refused;
        }

        if (events.Count == 0) return WorkReportVerdict.Refused;
        await CommitAsync(events);
        if (terminalWorkId is not null)
            await DeleteSnapshotBestEffortAsync(terminalWorkId);
        return WorkReportVerdict.Accepted;
    }

 }
