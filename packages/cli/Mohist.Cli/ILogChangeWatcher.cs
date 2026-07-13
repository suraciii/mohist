namespace Mohist.Cli;

internal interface ILogChangeWatcher : IDisposable
{
    event Action? Changed;

    void Start();
}

internal sealed class FileSystemLogChangeWatcher : ILogChangeWatcher
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemLogChangeWatcher(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnChanged;
    }

    public event Action? Changed;

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Dispose()
    {
        _watcher.Changed -= OnChanged;
        _watcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Changed?.Invoke();
}
