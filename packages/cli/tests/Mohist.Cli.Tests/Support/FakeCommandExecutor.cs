namespace Mohist.Cli.Tests.Support;

public sealed class FakeCommandExecutor : ICommandExecutor
{
    private readonly Dictionary<string, Queue<(int ExitCode, string Stdout, string Stderr)>> _byFileName = new(StringComparer.Ordinal);

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

    public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var recorded = new RecordedCommand(fileName, args.ToArray(), workingDirectory);
        Invocations.Add(recorded);

        if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
            return Task.FromResult(bucket.Dequeue());

        return Task.FromResult((0, string.Empty, string.Empty));
    }
}

public sealed record RecordedCommand(string FileName, string[] Args, string? WorkingDirectory);