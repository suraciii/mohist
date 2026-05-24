using System.Text.Json;

namespace Mohist.Runner.Actions;

public class ArchiveChangeAction : IAction
{
    public Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var changeDir = ResolveChangeDir(context);
        if (changeDir is null)
            return Task.FromResult(new ActionResult("failure", "Archive change requires 'changeDir'"));
        if (!Directory.Exists(changeDir))
            return Task.FromResult(new ActionResult("failure", $"Change directory not found: {changeDir}"));

        var changesDir = Directory.GetParent(changeDir)?.FullName;
        if (changesDir is null)
            return Task.FromResult(new ActionResult("failure", $"Could not determine changes directory for {changeDir}"));

        var archiveDir = Path.Combine(changesDir, "archive");
        Directory.CreateDirectory(archiveDir);

        var baseName = $"{DateTime.UtcNow:yyyy-MM-dd}-{Path.GetFileName(changeDir)}";
        var destination = UniqueDestination(archiveDir, baseName);
        Directory.Move(changeDir, destination);

        var output = JsonSerializer.Serialize(new { kind = "archive-change", source = changeDir, destination });
        return Task.FromResult(new ActionResult("success", "Change archived", output));
    }

    private static string? ResolveChangeDir(ActionContext context)
    {
        var changeDir = JsonInputs.String(context.With, "changeDir");
        if (string.IsNullOrWhiteSpace(changeDir)) return null;
        return Path.IsPathRooted(changeDir) ? changeDir : Path.GetFullPath(Path.Combine(context.WorkDir, changeDir));
    }

    private static string UniqueDestination(string archiveDir, string baseName)
    {
        var destination = Path.Combine(archiveDir, baseName);
        if (!Directory.Exists(destination)) return destination;

        for (var version = 2; ; version++)
        {
            destination = Path.Combine(archiveDir, $"{baseName}-v{version}");
            if (!Directory.Exists(destination)) return destination;
        }
    }
}
