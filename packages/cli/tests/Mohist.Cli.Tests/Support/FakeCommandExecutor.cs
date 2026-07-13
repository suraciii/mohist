namespace Mohist.Cli.Tests.Support;

public sealed class FakeCommandExecutor : ICommandExecutor
{
    public List<RecordedCommand> Invocations { get; } = [];

    public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        Invocations.Add(new RecordedCommand(fileName, args.ToArray(), workingDirectory));
        return Task.FromResult((0, string.Empty, string.Empty));
    }
}

public sealed record RecordedCommand(string FileName, string[] Args, string? WorkingDirectory);
