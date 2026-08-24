namespace Mohist.Cli;

internal sealed partial class WindowsScheduledTaskInstaller
{
    private async Task<int> KillMatchingProcessesAsync(
        string launcherPath,
        string startupPath,
        string metadataPath,
        string processImage,
        CancellationToken cancellationToken = default)
    {
        var imageName = $"{processImage}.exe";
        if (processImage.Equals("mohist-slack", StringComparison.OrdinalIgnoreCase))
        {
            var (queryCode, exactPids, queryError) = await QuerySlackProcessPidsAsync(
                launcherPath,
                startupPath,
                metadataPath,
                cancellationToken);
            if (queryCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
                return queryCode;
            }
            return await KillPidsAsync(exactPids, cancellationToken: cancellationToken);
        }

        // Best-effort stop: enumerate tasklist to find PIDs whose command line references
        // the generated launcher path, then taskkill /F /PID for each. This avoids
        // killing unrelated processes on the user's box.
        var (_, listOut, _) = await _commandExecutor.ExecuteAsync(
            "tasklist",
            ["/FI", $"IMAGENAME eq {imageName}", "/FO", "CSV", "/NH", "/V"],
            cancellationToken: cancellationToken);
        var pids = ParseTaskListPids(listOut, imageName, launcherPath);

        if (pids.Count == 0) return 0;

        return await KillPidsAsync(pids, cancellationToken: cancellationToken);
    }

    private async Task<int> KillPidsAsync(
        IReadOnlyList<int> pids,
        bool includeTree = false,
        CancellationToken cancellationToken = default)
    {
        var lastCode = 0;
        foreach (var pid in pids)
        {
            var args = includeTree
                ? new[] { "/F", "/T", "/PID", pid.ToString() }
                : ["/F", "/PID", pid.ToString()];
            var (code, _, stderr) = await _commandExecutor.ExecuteAsync(
                "taskkill",
                args,
                cancellationToken: cancellationToken);
            if (code != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
                lastCode = code;
            }
        }

        return lastCode;
    }

    private static List<int> ParseTaskListPids(string stdout, string imageName, string launcherPath)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(stdout)) return result;
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var marker = Path.GetFileName(launcherPath);
        foreach (var line in lines)
        {
            if (line.IndexOf(imageName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var fields = line.Split(',');
            if (fields.Length < 2) continue;
            var pidField = fields[1].Trim(' ', '"');
            if (int.TryParse(pidField, out var pid)) result.Add(pid);
        }
        return result;
    }
}
