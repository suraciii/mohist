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
        if (workItem.WorkType == "checks")
            return await ExecuteChecksAsync(workItem, ct);

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

    private async Task<WorkItemResult> ExecuteChecksAsync(WorkItem workItem, CancellationToken ct)
    {
        var variables = BuildVariables(workItem);
        var workspace = await _workspaceManager.EnsureAsync(variables, ct);
        variables["workspace"] = JsonSerializer.SerializeToElement(new
        {
            path = workspace.Path,
            branch = workspace.Branch,
            changeDir = workspace.ChangeDir
        });

        var checks = ParseChecksFromWith(workItem.With);
        if (checks.Count == 0)
            return Failure(workItem, "No checks found in dispatch");

        var results = new List<Dictionary<string, string?>>();

        await Parallel.ForEachAsync(checks, ct, async (check, _) =>
        {
            var uses = check.GetValueOrDefault("uses");
            if (uses is null) return;

            var action = _actionManager.Resolve(uses);
            if (action is null)
            {
                lock (results)
                    results.Add(new Dictionary<string, string?> { ["name"] = check.GetValueOrDefault("name"), ["status"] = "fail", ["message"] = $"No action found for '{uses}'" });
                return;
            }

            var checkWith = check.TryGetValue("with", out var withStr) && withStr is not null
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(withStr)
                : null;
            var renderedWith = TemplateRenderer.Render(checkWith, variables);
            var workDir = ResolveWorkDir(renderedWith, variables);

            var context = new ActionContext(
                workItem.WorkflowRunId, workItem.WorkId, "check",
                workItem.Stage, check.GetValueOrDefault("title"), uses,
                renderedWith, variables, workDir, ct);

            try
            {
                var result = await action.ExecuteAsync(context);
                var status = result.Status.ToLowerInvariant() switch
                {
                    "pass" or "passed" or "success" or "succeeded" or "completed" => "pass",
                    "pending" => "pending",
                    _ => "fail"
                };
                lock (results)
                    results.Add(new Dictionary<string, string?> { ["name"] = check.GetValueOrDefault("name"), ["status"] = status, ["message"] = result.Message, ["output"] = result.Output });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Check action failed for {CheckName}", check.GetValueOrDefault("name"));
                lock (results)
                    results.Add(new Dictionary<string, string?> { ["name"] = check.GetValueOrDefault("name"), ["status"] = "fail", ["message"] = ex.Message });
            }
        });

        var output = JsonSerializer.Serialize(results);
        var allPassed = results.All(r => r.GetValueOrDefault("status") == "pass");
        return new WorkItemResult(allPassed ? "pass" : "fail", Output: output);
    }

    private static List<Dictionary<string, string?>> ParseChecksFromWith(Dictionary<string, JsonElement?>? with)
    {
        if (with is null) return [];
        if (!with.TryGetValue("checks", out var checksElement) || checksElement is null) return [];

        var checks = new List<Dictionary<string, string?>>();
        foreach (var item in checksElement.Value.EnumerateArray())
        {
            var dict = new Dictionary<string, string?>();
            if (item.TryGetProperty("name", out var name)) dict["name"] = name.GetString();
            if (item.TryGetProperty("title", out var title)) dict["title"] = title.GetString();
            if (item.TryGetProperty("uses", out var uses)) dict["uses"] = uses.GetString();
            if (item.TryGetProperty("with", out var w)) dict["with"] = w.GetRawText();
            checks.Add(dict);
        }
        return checks;
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
        "check" or "checks" => new WorkItemResult("fail", message),
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
