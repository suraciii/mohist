using Mohist.Server.Infrastructure.Config;

namespace Mohist.Server.TestSupport;

internal sealed class InMemoryConfigFileStore : IConfigFileStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public bool Exists(string path) => _files.ContainsKey(path);

    public string ReadAllText(string path) => _files[path];

    public Task<string> ReadAllTextAsync(string path) => Task.FromResult(ReadAllText(path));

    public void EnsureParentDirectory(string path)
    {
    }

    public Task WriteAllTextAsync(string path, string contents)
    {
        _files[path] = contents;
        return Task.CompletedTask;
    }

    public void WriteAllText(string path, string contents) => _files[path] = contents;
}
