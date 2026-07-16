namespace Mohist.Server.Infrastructure.Config;

public interface IConfigDocumentStore
{
    string Location { get; }

    string? Read();

    Task WriteAsync(string content, CancellationToken cancellationToken = default);
}

public sealed class FileConfigDocumentStore : IConfigDocumentStore
{
    private readonly string _path;

    public FileConfigDocumentStore(IEnvironmentVariableProvider environment)
    {
        var home = environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _path = Path.Combine(home, ".mohist", "config.jsonc");
    }

    public string Location => _path;

    public string? Read() => File.Exists(_path) ? File.ReadAllText(_path) : null;

    public async Task WriteAsync(string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(_path, content, cancellationToken).ConfigureAwait(false);
    }
}
