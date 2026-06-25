namespace Mohist.Cli;

internal sealed class VerboseGitInspector
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2);

    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;

    public VerboseGitInspector(IFileSystem fileSystem, ICommandExecutor commandExecutor)
    {
        _fileSystem = fileSystem;
        _commandExecutor = commandExecutor;
    }

    internal async Task<InfoVerboseGitRemote> GetGitRemoteVerboseAsync(string? sourcePath)
    {
        using var cts = new CancellationTokenSource(CollectorTimeout);
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new InfoVerboseGitRemote(null, IsGitRepo: false);
        if (!_fileSystem.DirectoryExists(sourcePath))
            return new InfoVerboseGitRemote(null, IsGitRepo: false);
        var gitDir = Path.Combine(sourcePath, ".git");
        if (!_fileSystem.DirectoryExists(gitDir) && !_fileSystem.Exists(gitDir))
            return new InfoVerboseGitRemote(null, IsGitRepo: false);

        try
        {
            var (exit, stdout, _) = await InfoCollector.WithTimeout(
                _commandExecutor.ExecuteAsync("git", ["-C", sourcePath, "remote", "get-url", "origin"]),
                cts.Token);
            if (exit != 0)
                return new InfoVerboseGitRemote(null, IsGitRepo: true);
            var url = stdout.Trim();
            if (string.IsNullOrWhiteSpace(url))
                return new InfoVerboseGitRemote(null, IsGitRepo: true);
            return new InfoVerboseGitRemote(url, IsGitRepo: true);
        }
        catch
        {
            return new InfoVerboseGitRemote(null, IsGitRepo: true);
        }
    }
}
