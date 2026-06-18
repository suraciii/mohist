namespace Mohist.Cli.Tests.Support;

public sealed class FakeCommandExecutor : ICommandExecutor
{
    public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((0, string.Empty, string.Empty));
    }
}
