using System.Text;
using Mohist.Server.Logging;

namespace Mohist.Server.SpecTests.Support;

internal sealed class InMemoryLogFileStore : ILogFileStore, ILogFileSinkFactory
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private long _version;
    private readonly Dictionary<string, DateTimeOffset> _lastWrites = new(StringComparer.Ordinal);

    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public IEnumerable<LogFileDescriptor> EnumerateLogFiles(string directory)
    {
        var normalized = Normalize(directory);
        return _files.Keys
            .Where(path => IsDirectChild(path, normalized) && path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new LogFileDescriptor(path, _lastWrites[path]))
            .ToArray();
    }

    public Stream OpenRead(string path)
    {
        var bytes = _files[Normalize(path)];
        return new MemoryStream(bytes.ToArray(), writable: false);
    }

    public ILogFileSink Open(string path)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        return new LogFileSink(this, Normalize(path));
    }

    public void EnsureDirectory(string path)
    {
        for (var current = Normalize(path); ;)
        {
            _directories.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return;
            current = parent;
        }
    }

    public void SetText(string path, string contents)
    {
        var normalized = Normalize(path);
        EnsureDirectory(Path.GetDirectoryName(normalized)!);
        _files[normalized] = Encoding.UTF8.GetBytes(contents);
        _lastWrites[normalized] = NextWriteTime();
    }

    public void SetLines(string path, IEnumerable<string> lines) =>
        SetText(path, string.Join('\n', lines) + "\n");

    public void AppendLines(string path, IEnumerable<string> lines)
    {
        var normalized = Normalize(path);
        var prefix = _files.TryGetValue(normalized, out var bytes) ? Encoding.UTF8.GetString(bytes) : string.Empty;
        SetText(normalized, prefix + string.Join('\n', lines) + "\n");
    }

    public void ClearDirectory(string path)
    {
        var normalized = Normalize(path);
        foreach (var file in _files.Keys.Where(candidate => IsWithinDirectory(candidate, normalized)).ToArray())
        {
            _files.Remove(file);
            _lastWrites.Remove(file);
        }
        foreach (var directory in _directories.Where(candidate => IsWithinDirectory(candidate, normalized)).ToArray())
            _directories.Remove(directory);
    }

    private void AppendLine(string path, string line)
    {
        var prefix = _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : string.Empty;
        SetText(path, prefix + line + "\n");
    }

    private DateTimeOffset NextWriteTime() => DateTimeOffset.UnixEpoch.AddTicks(++_version);

    private static string Normalize(string path)
    {
        var normalized = Path.GetFullPath(path);
        return normalized.Length > 1
            ? normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : normalized;
    }

    private static bool IsDirectChild(string path, string directory) =>
        Path.GetDirectoryName(path) == directory;

    private static bool IsWithinDirectory(string path, string directory)
    {
        if (path == directory)
            return true;

        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private sealed class LogFileSink(InMemoryLogFileStore owner, string path) : ILogFileSink
    {
        public void WriteLine(string line) => owner.AppendLine(path, line);

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
