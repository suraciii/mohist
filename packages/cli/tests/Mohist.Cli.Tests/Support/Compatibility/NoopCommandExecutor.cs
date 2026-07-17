using Mohist.Cli;

namespace Mohist.Cli.Tests.Compatibility;

internal sealed class NoopCommandExecutor
    : ICommandExecutor
{
    public Task<(
        int ExitCode,
        string Stdout,
        string Stderr)> ExecuteAsync(
        string fileName,
        string[] args,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult((0, string.Empty, string.Empty));
}
