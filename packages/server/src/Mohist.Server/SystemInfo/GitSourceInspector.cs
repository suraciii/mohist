using System.Diagnostics;

namespace Mohist.Server.SystemInfo;

public sealed record SourceState(
    string? Path,
    string? Branch,
    string? Head,
    bool Dirty);

public interface IGitSourceInspector
{
    Task<SourceState> InspectAsync(string repoPath);
}

public sealed class GitSourceInspector : IGitSourceInspector
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(10);

    private readonly IFileSystem _fileSystem;
    private readonly Func<string, string, string[], Task<(string Output, int ExitCode)>> _runGit;

    public GitSourceInspector(IFileSystem fileSystem)
        : this(fileSystem, DefaultRunGit)
    {
    }

    internal GitSourceInspector(
        IFileSystem fileSystem,
        Func<string, string, string[], Task<(string Output, int ExitCode)>> runGit)
    {
        _fileSystem = fileSystem;
        _runGit = runGit;
    }

    public async Task<SourceState> InspectAsync(string repoPath)
    {
        if (!_fileSystem.Exists(repoPath))
            return new SourceState(repoPath, null, null, false);

        var gitDir = Path.Combine(repoPath, ".git");
        if (!_fileSystem.Exists(gitDir))
            return new SourceState(repoPath, null, null, false);

        var branchTask = _runGit(repoPath, "rev-parse", ["--abbrev-ref", "HEAD"]);
        var headTask = _runGit(repoPath, "rev-parse", ["HEAD"]);
        var statusTask = _runGit(repoPath, "status", ["--porcelain"]);

        await Task.WhenAll(branchTask, headTask, statusTask);

        var branchResult = await branchTask;
        var headResult = await headTask;
        var statusResult = await statusTask;

        var branch = branchResult.ExitCode == 0
            ? branchResult.Output.Trim()
            : null;

        var head = headResult.ExitCode == 0
            ? headResult.Output.Trim()
            : null;

        var dirty = statusResult.ExitCode == 0
            && !string.IsNullOrWhiteSpace(statusResult.Output);

        return new SourceState(repoPath, branch, head, dirty);
    }

    private static async Task<(string Output, int ExitCode)> DefaultRunGit(string workingDir, string command, string[] args)
    {
        var psi = new ProcessStartInfo("git", [command, .. args])
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process == null) return ("", -1);

        using var timeout = new CancellationTokenSource(GitCommandTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort
            }
            return ("", 124);
        }

        var output = await outputTask;
        return (output, process.ExitCode);
    }
}
