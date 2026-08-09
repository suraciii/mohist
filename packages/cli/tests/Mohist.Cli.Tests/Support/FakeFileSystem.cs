using System.Text;

namespace Mohist.Cli.Tests.Support;

public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _directoryLinks = new(StringComparer.OrdinalIgnoreCase);
    private string _currentDirectory = "/";

    public string CurrentDirectory
    {
        get => _currentDirectory;
        set => _currentDirectory = value;
    }

    public IReadOnlyDictionary<string, string> Files => _files;
    public bool TreatFilesAsSymbolicLinks { get; set; }
    public bool TreatFilesAsWorldReadable { get; set; }

    public bool IsSymbolicLink(string path) => TreatFilesAsSymbolicLinks;
    public bool IsUserOnlyFile(string path) => !TreatFilesAsWorldReadable;

    public void AddFile(string path, string content)
    {
        _files[Normalize(path)] = content;
    }

    public bool Exists(string path)
    {
        var normalized = Normalize(path);
        return _files.ContainsKey(normalized)
            || _directories.Contains(normalized)
            || _directoryLinks.ContainsKey(normalized)
            || HasDescendant(normalized);
    }

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        return _directories.Contains(normalized)
            || _directoryLinks.ContainsKey(normalized)
            || HasDescendant(normalized);
    }

    public void CreateDirectory(string path)
    {
        _directories.Add(Normalize(path));
    }

    public void Delete(string path)
    {
        _files.Remove(Normalize(path));
        _directoryLinks.Remove(Normalize(path));
    }

    public void DeleteDirectory(string path)
    {
        var normalized = Normalize(path);
        var prefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
        foreach (var dir in _directories.Where(d => d == normalized || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _directories.Remove(dir);
        }

        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _files.Remove(key);
        }
        foreach (var link in _directoryLinks.Keys.Where(k => k == normalized || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _directoryLinks.Remove(link);
        }
    }

    public void Move(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destKey = Normalize(destination);

        if (_files.TryGetValue(sourceKey, out var content))
        {
            _files.Remove(sourceKey);
            _files[destKey] = content;
            return;
        }

        if (_directories.Contains(sourceKey))
        {
            var sourcePrefix = sourceKey.EndsWith(Path.DirectorySeparatorChar)
                ? sourceKey
                : sourceKey + Path.DirectorySeparatorChar;
            var destFilePrefix = destKey.EndsWith(Path.DirectorySeparatorChar)
                ? destKey
                : destKey + Path.DirectorySeparatorChar;

            foreach (var file in _files.Keys.Where(k => k.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var suffix = file.Substring(sourcePrefix.Length);
                _files[destFilePrefix + suffix] = _files[file];
                _files.Remove(file);
            }

            foreach (var dir in _directories.Where(d => d.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var suffix = dir.Substring(sourcePrefix.Length);
                _directories.Add(destFilePrefix + suffix);
                _directories.Remove(dir);
            }

            _directories.Add(destKey);
            _directories.Remove(sourceKey);
        }
    }

    public void MoveFile(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destKey = Normalize(destination);
        if (!_files.TryGetValue(sourceKey, out var content))
            throw new FileNotFoundException($"File not found: {source}");
        _files.Remove(sourceKey);
        _files[destKey] = content;
    }

    public string ReadAllText(string path) => _files.TryGetValue(Normalize(path), out var content)
        ? content
        : throw new FileNotFoundException($"File not found: {path}");

    public Task<string> ReadAllTextAsync(string path) => Task.FromResult(ReadAllText(path));

    public void WriteAllText(string path, string contents)
    {
        _files[Normalize(path)] = contents;
    }

    public Task WriteAllTextAsync(string path, string contents)
    {
        WriteAllText(path, contents);
        return Task.CompletedTask;
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        var normalized = Normalize(path);
        var prefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
        return _files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(k => MatchesPattern(Path.GetFileName(k), searchPattern))
            .ToArray();
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal)) return true;
        if (string.Equals(pattern, "*.*", StringComparison.Ordinal))
            return name.Contains('.');
        if (pattern.StartsWith("*.") && pattern.IndexOf('*', 1) < 0)
        {
            var ext = pattern.Substring(1);
            return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.EndsWith("*") && pattern.Substring(0, pattern.Length - 1).IndexOf('*') < 0)
        {
            var prefixPart = pattern.Substring(0, pattern.Length - 1);
            return name.StartsWith(prefixPart, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasDescendant(string path)
    {
        var prefix = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        return _files.Keys.Any(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || _directories.Any(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || _directoryLinks.Keys.Any(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public Stream OpenRead(string path) => new MemoryStream(Encoding.UTF8.GetBytes(ReadAllText(path)));

    public Stream OpenWrite(string path) => new RecordingStream(this, path);

    public void ReplaceDirectorySymbolicLink(string linkPath, string targetPath)
    {
        _directoryLinks[Normalize(linkPath)] = Normalize(targetPath);
    }

    public string? ReadDirectorySymbolicLink(string linkPath) =>
        _directoryLinks.TryGetValue(Normalize(linkPath), out var target) ? target : null;

    public void DeleteDirectorySymbolicLink(string linkPath)
    {
        _directoryLinks.Remove(Normalize(linkPath));
    }

    private static string Normalize(string path) => Path.GetFullPath(path, "/");

    private sealed class RecordingStream : MemoryStream
    {
        private readonly FakeFileSystem _owner;
        private readonly string _path;

        public RecordingStream(FakeFileSystem owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner.WriteAllText(_path, Encoding.UTF8.GetString(ToArray()));
            }

            base.Dispose(disposing);
        }
    }
}
