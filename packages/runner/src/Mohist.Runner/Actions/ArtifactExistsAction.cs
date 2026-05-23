using System.Text.Json;

namespace Mohist.Runner.Actions;

public class ArtifactExistsAction : IAction
{
    public Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var path = JsonInputs.String(context.With, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(new ActionResult("failure", "Artifact check requires 'path'"));

        var fullPath = ResolvePath(context.WorkDir, path);
        var exists = File.Exists(fullPath) || Directory.Exists(fullPath);
        var output = JsonSerializer.Serialize(new { kind = "artifact-exists", path = fullPath, exists });

        return Task.FromResult(exists
            ? new ActionResult("success", $"Artifact exists: {path}", output)
            : new ActionResult("failure", $"Artifact not found: {path}", output));
    }

    private static string ResolvePath(string workDir, string path) => Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(Path.Combine(workDir, path));
}
