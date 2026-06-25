namespace Mohist.Cli;

internal sealed class VerboseDiskInspector
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2);

    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;

    public VerboseDiskInspector(IFileSystem fileSystem, ICommandExecutor commandExecutor)
    {
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
    }

    internal async Task<InfoVerboseDiskUsage> GetDiskUsageVerboseAsync(InfoDataDir dataDir)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        var dataRoot = dataDir.Path;
        if (string.IsNullOrWhiteSpace(dataRoot) || !_fileSystem.DirectoryExists(dataRoot))
            return new InfoVerboseDiskUsage(Array.Empty<InfoVerboseDiskCategory>(), Resolved: true);

        var projectsTask = ComputeCategorySizeAsync(Path.Combine(dataRoot, "projects"), cts.Token);
        var logsTask = ComputeCategorySizeAsync(Path.Combine(dataRoot, "logs"), cts.Token);
        var worktreesTask = ComputeCategorySizeAsync(Path.Combine(dataRoot, "worktrees"), cts.Token);
        await Task.WhenAll(projectsTask, logsTask, worktreesTask);

        return new InfoVerboseDiskUsage([
            new("projects", projectsTask.Result.Size, projectsTask.Result.FileCount),
            new("logs", logsTask.Result.Size, logsTask.Result.FileCount),
            new("worktrees", worktreesTask.Result.Size, worktreesTask.Result.FileCount),
        ], Resolved: true);
    }

    private async Task<(string? Size, int? FileCount)> ComputeCategorySizeAsync(string path, CancellationToken ct)
    {
        if (!_fileSystem.DirectoryExists(path))
            return (null, null);
        var size = await ComputeDiskUsageAsync(path, ct);
        int? fileCount = null;
        try
        {
            fileCount = _fileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
        }
        return (size, fileCount);
    }

    private async Task<string?> ComputeDiskUsageAsync(string path, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await InfoCollector.WithTimeout(
                _commandExecutor.ExecuteAsync("du", ["-sh", path]),
                ct);
            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var firstLine = stdout.Split('\n').FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    var parts = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 0 ? parts[0] : firstLine;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
