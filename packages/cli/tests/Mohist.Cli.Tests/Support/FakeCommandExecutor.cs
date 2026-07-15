namespace Mohist.Cli.Tests.Support;

public sealed class FakeCommandExecutor : ICommandExecutor
{
    private readonly Dictionary<string, Queue<(int ExitCode, string Stdout, string Stderr)>> _byFileName = new(StringComparer.Ordinal);
    private readonly Queue<ExpectedCommand> _expectedCommands = [];

    public List<RecordedCommand> Invocations { get; } = [];

    public void QueueForFile(string fileName, int exitCode, string stdout = "", string stderr = "")
    {
        if (!_byFileName.TryGetValue(fileName, out var bucket))
        {
            bucket = new Queue<(int, string, string)>();
            _byFileName[fileName] = bucket;
        }
        bucket.Enqueue((exitCode, stdout, stderr));
    }

    public void QueueExpected(
        string fileName,
        string[] args,
        string? workingDirectory,
        int exitCode,
        string stdout = "",
        string stderr = "")
    {
        _expectedCommands.Enqueue(new ExpectedCommand(fileName, args, workingDirectory, exitCode, stdout, stderr));
    }

    public void AssertExpectedCommandsExecuted()
    {
        if (_expectedCommands.Count > 0)
            throw new InvalidOperationException($"Expected command was not executed: {_expectedCommands.Peek().FileName}");
    }

    public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var recorded = new RecordedCommand(fileName, args.ToArray(), workingDirectory);
        Invocations.Add(recorded);

        if (_expectedCommands.Count > 0)
        {
            var expected = _expectedCommands.Dequeue();
            if (!string.Equals(expected.FileName, fileName, StringComparison.Ordinal)
                || !expected.Args.SequenceEqual(args, StringComparer.Ordinal)
                || !string.Equals(expected.WorkingDirectory, workingDirectory, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected command: {fileName} {string.Join(' ', args)}. " +
                    $"Expected: {expected.FileName} {string.Join(' ', expected.Args)}");
            }

            return Task.FromResult((expected.ExitCode, expected.Stdout, expected.Stderr));
        }

        if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
            return Task.FromResult(bucket.Dequeue());

        throw new InvalidOperationException($"Unexpected command: {fileName} {string.Join(' ', args)}");
    }
}

public sealed record RecordedCommand(string FileName, string[] Args, string? WorkingDirectory);

public sealed record ExpectedCommand(
    string FileName,
    string[] Args,
    string? WorkingDirectory,
    int ExitCode,
    string Stdout,
    string Stderr);
