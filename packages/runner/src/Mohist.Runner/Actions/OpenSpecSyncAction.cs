using System.Text.Json;

namespace Mohist.Runner.Actions;

public class OpenSpecSyncAction : IAction
{
    public Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var changeDir = ResolveChangeDir(context);
        if (changeDir is null)
            return Task.FromResult(new ActionResult("failure", "OpenSpec sync requires 'changeDir'"));

        var specsDir = Path.Combine(changeDir, "specs");
        if (!Directory.Exists(specsDir))
            return Task.FromResult(new ActionResult("failure", $"OpenSpec specs directory not found: {specsDir}"));

        var destination = Path.Combine(context.WorkDir, "specs");
        CopyDirectory(specsDir, destination);

        var output = JsonSerializer.Serialize(new { kind = "openspec-sync", source = specsDir, destination });
        return Task.FromResult(new ActionResult("success", "OpenSpec specs synced", output));
    }

    private static string? ResolveChangeDir(ActionContext context)
    {
        var changeDir = JsonInputs.String(context.With, "changeDir");
        if (string.IsNullOrWhiteSpace(changeDir)) return null;
        return Path.IsPathRooted(changeDir) ? changeDir : Path.GetFullPath(Path.Combine(context.WorkDir, changeDir));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
