using Mohist.Server.SystemInfo;

namespace Mohist.Server.SpecTests.Support;

public sealed class InMemoryServerFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public bool Exists(string path) => _files.ContainsKey(path);

    public string ReadAllText(string path) => _files.TryGetValue(path, out var content)
        ? content
        : throw new FileNotFoundException($"No in-memory file exists at '{path}'.");

    public void Add(string path, string content) => _files[path] = content;
}
