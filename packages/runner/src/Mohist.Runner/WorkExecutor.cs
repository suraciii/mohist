using Mohist.Runner.Actions;
using Mohist.Runner.Transport;

namespace Mohist.Runner;

public class WorkExecutor : IWorkExecutor
{
    private readonly ActionManager _actionManager;
    private readonly ILogger<WorkExecutor> _log;
    private readonly string _workDir;

    public WorkExecutor(ActionManager actionManager, ILogger<WorkExecutor> log)
    {
        _actionManager = actionManager;
        _log = log;
        _workDir = Directory.GetCurrentDirectory();
    }

    public async Task<WorkItemResult> ExecuteAsync(WorkItem workItem, CancellationToken ct)
    {
        var action = _actionManager.Resolve(workItem.Uses);

        if (action is null)
            return Failure(workItem, $"No action found for '{workItem.Uses}'");

        var context = new ActionContext(
            workItem.WorkflowRunId,
            workItem.WorkId,
            workItem.WorkType,
            workItem.Stage,
            workItem.Title,
            workItem.Uses,
            workItem.With,
            ResolveWorkDir(workItem),
            ct);

        try
        {
            var result = await action.ExecuteAsync(context);
            return Normalize(workItem, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(workItem, "Runner shutting down");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Action execution failed for {WorkId}", workItem.WorkId);
            return Failure(workItem, ex.Message);
        }
    }

    private WorkItemResult Normalize(WorkItem workItem, ActionResult result)
    {
        var status = result.Status.ToLowerInvariant();
        return workItem.WorkType switch
        {
            "check" => status switch
            {
                "pass" or "passed" or "success" or "succeeded" or "completed" => new WorkItemResult("pass", result.Message, result.Output, result.ExitCode),
                "pending" => new WorkItemResult("pending", result.Message, result.Output, result.ExitCode),
                _ => new WorkItemResult("fail", result.Message, result.Output, result.ExitCode),
            },
            "load" => status switch
            {
                "loaded" or "success" or "succeeded" or "completed" => new WorkItemResult("loaded", result.Message, result.Output, result.ExitCode),
                _ => new WorkItemResult("failed", result.Message, result.Output, result.ExitCode),
            },
            _ => status switch
            {
                "completed" or "success" or "succeeded" or "pass" or "passed" => new WorkItemResult("completed", result.Message, result.Output, result.ExitCode),
                _ => new WorkItemResult("failed", result.Message, result.Output, result.ExitCode),
            },
        };
    }

    private WorkItemResult Failure(WorkItem workItem, string message) => workItem.WorkType switch
    {
        "check" => new WorkItemResult("fail", message),
        _ => new WorkItemResult("failed", message),
    };

    private string ResolveWorkDir(WorkItem workItem)
    {
        var dir = Path.Combine(_workDir, workItem.WorkflowRunId);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
