using Mohist.Runner.Actions;
using Mohist.Runner.Transport;

namespace Mohist.Runner;

public class RunnerHost
{
    private readonly IServerConnection _connection;
    private readonly ActionManager _actionManager;
    private readonly ILogger<RunnerHost> _log;
    private readonly string _workDir;

    public RunnerHost(
        IServerConnection connection,
        ActionManager actionManager,
        ILogger<RunnerHost> log)
    {
        _connection = connection;
        _actionManager = actionManager;
        _log = log;
        _workDir = Directory.GetCurrentDirectory();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Runner connecting to server...");
        await _connection.ConnectAsync(ct);
        _log.LogInformation("Runner connected, polling for work...");

        while (!ct.IsCancellationRequested)
        {
            var workItem = await _connection.PollAsync(ct);

            if (workItem is null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            _log.LogInformation("Received work: {WorkId} uses={Uses}",
                workItem.WorkId, workItem.Uses);

            var result = await ExecuteAsync(workItem, ct);

            _log.LogInformation("Work {WorkId} completed: {Status}", workItem.WorkId, result.Status);

            await _connection.ReportAsync(workItem, result, ct);
        }
    }

    private async Task<WorkItemResult> ExecuteAsync(WorkItem workItem, CancellationToken ct)
    {
        var action = _actionManager.Resolve(workItem.Uses);

        if (action is null)
            return new WorkItemResult("failed", $"No action found for '{workItem.Uses}'");

        var context = new ActionContext(
                workItem.WorkflowRunId,
                workItem.WorkId,
                workItem.Uses,
                workItem.With,
            ResolveWorkDir(workItem));

        try
        {
            var actionResult = await action.ExecuteAsync(context);

            return new WorkItemResult(
                actionResult.Status,
                actionResult.Message,
                actionResult.Output,
                actionResult.ExitCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WorkItemResult("cancelled", "Runner shutting down");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Action execution failed for {WorkId}", workItem.WorkId);
            return new WorkItemResult("failed", ex.Message);
        }
    }

    private string ResolveWorkDir(WorkItem workItem)
    {
        var dir = Path.Combine(_workDir, workItem.WorkflowRunId);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
