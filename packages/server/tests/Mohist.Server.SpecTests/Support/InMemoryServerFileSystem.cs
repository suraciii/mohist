using System.Text;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.SpecTests.Support;

public sealed class InMemoryServerFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    public bool Exists(string path) => _files.ContainsKey(path) || _directories.Contains(path);

    public string ReadAllText(string path) => _files.TryGetValue(path, out var content)
        ? content
        : throw new FileNotFoundException($"No in-memory file exists at '{path}'.");

    public void CreateDirectory(string path) => _directories.Add(path);

    public long? GetFileLength(string path) =>
        _files.TryGetValue(path, out var content) ? Encoding.UTF8.GetByteCount(content) : null;

    public void WriteAllText(string path, string contents) => _files[path] = contents;

    public void Delete(string path) => _files.Remove(path);

    public void Add(string path, string content) => _files[path] = content;
}
