using System.Threading.Channels;

namespace Mohist.Cli;

internal interface ILogChangeObserver : IDisposable
{
    Task ObserveAsync(Func<Task> onChanged, CancellationToken cancellationToken);
}

internal sealed class FileSystemLogChangeObserver : ILogChangeObserver
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemLogChangeObserver(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
    }

    public async Task ObserveAsync(Func<Task> onChanged, CancellationToken cancellationToken)
    {
        var changes = Channel.CreateUnbounded<object?>();
        FileSystemEventHandler handler = (_, _) => changes.Writer.TryWrite(null);
        _watcher.Changed += handler;
        _watcher.EnableRaisingEvents = true;

        try
        {
            await foreach (var _ in changes.Reader.ReadAllAsync(cancellationToken))
                await onChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= handler;
        }
    }

    public void Dispose() => _watcher.Dispose();
}
