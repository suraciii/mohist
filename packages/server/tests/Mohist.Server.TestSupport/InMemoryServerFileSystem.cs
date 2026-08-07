using System.Text;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryServerFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly List<string> _deletedPaths = new();

    public bool Exists(string path) => _files.ContainsKey(path) || _directories.Contains(path);

    public string ReadAllText(string path) => _files.TryGetValue(path, out var content)
        ? content
        : throw new FileNotFoundException($"No in-memory file exists at '{path}'.");

    public void CreateDirectory(string path) => _directories.Add(path);

    public long? GetFileLength(string path) =>
        _files.TryGetValue(path, out var content) ? Encoding.UTF8.GetByteCount(content) : null;

    public void WriteAllText(string path, string contents) => _files[path] = contents;

    public void Delete(string path)
    {
        _deletedPaths.Add(path);
        _files.Remove(path);
    }

    /// <summary>
    /// Ordered list of paths that have been deleted since this
    /// instance was constructed. Tests use it to assert exact
    /// deletion ordering on rebuild paths that drive
    /// <see cref="IFileSystem.Delete"/> through a fake seam.
    /// </summary>
    public IReadOnlyList<string> DeletedPaths => _deletedPaths;

    public void Add(string path, string content) => _files[path] = content;
}
