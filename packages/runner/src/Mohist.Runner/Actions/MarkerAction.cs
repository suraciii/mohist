using System.Text.Json;

namespace Mohist.Runner.Actions;

public class MarkerAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var path = JsonInputs.String(context.With, "path");
        var expect = JsonInputs.String(context.With, "expect");

        if (string.IsNullOrWhiteSpace(path))
            return new ActionResult("failure", "Marker check requires 'path'");
        if (string.IsNullOrEmpty(expect))
            return new ActionResult("failure", "Marker check requires 'expect'");

        var fullPath = ResolvePath(context.WorkDir, path);
        if (!File.Exists(fullPath))
            return new ActionResult("failure", $"Marker file not found: {path}");

        var content = await File.ReadAllTextAsync(fullPath, context.CancellationToken);
        var found = content.Contains(expect, StringComparison.Ordinal);
        var output = JsonSerializer.Serialize(new { kind = "marker", path = fullPath, marker = expect, found });

        return found
            ? new ActionResult("success", $"Marker found: {expect}", output)
            : new ActionResult("failure", $"Marker not found: {expect}", output);
    }

    private static string ResolvePath(string workDir, string path) => Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(Path.Combine(workDir, path));
}
