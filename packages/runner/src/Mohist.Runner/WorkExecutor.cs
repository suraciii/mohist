using System.Text.Json;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;

namespace Mohist.Runner;

public class WorkExecutor : IWorkExecutor
{
    private readonly ActionManager _actionManager;
    private readonly ILogger<WorkExecutor> _log;
    private readonly string _workDir;
    private readonly IWorkspaceManager _workspaceManager;

    public WorkExecutor(ActionManager actionManager, ILogger<WorkExecutor> log, IWorkspaceManager workspaceManager, string? workDir = null)
    {
        _actionManager = actionManager;
        _log = log;
        _workspaceManager = workspaceManager;
        _workDir = workDir ?? Directory.GetCurrentDirectory();
    }

    public async Task<WorkItemResult> ExecuteAsync(WorkItem workItem, CancellationToken ct)
    {
        var action = _actionManager.Resolve(workItem.Uses);

        if (action is null)
            return Failure(workItem, $"No action found for '{workItem.Uses}'");

        var variables = BuildVariables(workItem);
        var workspace = await _workspaceManager.EnsureAsync(variables, ct);
        variables["workspace"] = JsonSerializer.SerializeToElement(new
        {
            path = workspace.Path,
            branch = workspace.Branch,
            changeDir = workspace.ChangeDir
        });

        var renderedWith = TemplateRenderer.Render(workItem.With, variables);

        var context = new ActionContext(
            workItem.WorkflowRunId,
            workItem.WorkId,
            workItem.WorkType,
            workItem.Stage,
            workItem.Title,
            workItem.Uses,
            renderedWith,
            variables,
            ResolveWorkDir(renderedWith, variables),
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

    private Dictionary<string, JsonElement?> BuildVariables(WorkItem workItem)
    {
        var variables = workItem.Variables is not null
            ? new Dictionary<string, JsonElement?>(workItem.Variables)
            : new Dictionary<string, JsonElement?>();

        variables["runner"] = JsonSerializer.SerializeToElement(new
        {
            os = Environment.OSVersion.Platform.ToString(),
            hostname = Environment.MachineName,
            temp = Path.GetTempPath()
        });

        return variables;
    }

    private string ResolveWorkDir(
        Dictionary<string, JsonElement?>? renderedWith,
        Dictionary<string, JsonElement?> variables)
    {
        var dir = JsonInputs.String(renderedWith, "working-directory")
            ?? ResolveString(variables, "workspace.path")
            ?? Path.Combine(_workDir, "default");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? ResolveString(Dictionary<string, JsonElement?> variables, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !variables.TryGetValue(parts[0], out var current) || current is null)
            return null;

        var element = current.Value;
        for (var i = 1; i < parts.Length; i++)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(parts[i], out element))
                return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }
}
