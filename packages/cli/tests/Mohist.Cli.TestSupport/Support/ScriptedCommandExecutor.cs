namespace Mohist.Cli.TestSupport;

/// <summary>
/// <see cref="ICommandExecutor"/> fake that returns scripted
/// <c>(exitCode, stdout, stderr)</c> tuples per <c>fileName</c>, in queue
/// order. Unscripted calls fall back to <c>(0, "", "")</c>.
/// </summary>
public sealed class ScriptedCommandExecutor : ICommandExecutor
{
    private readonly Dictionary<string, Queue<(int ExitCode, string Stdout, string Stderr)>> _byFileName = new(StringComparer.Ordinal);

    public void Queue(string fileName, int exitCode, string stdout = "", string stderr = "")
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
        if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
            return Task.FromResult(bucket.Dequeue());
        return Task.FromResult((0, string.Empty, string.Empty));
    }
}
