using System.Text;

namespace Mohist.Cli.Tests.Support;

public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _fileBytes = new(StringComparer.OrdinalIgnoreCase);
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
        SetFile(Normalize(path), Encoding.UTF8.GetBytes(content), content);
    }

    public void AddFileBytes(string path, byte[] contents) =>
        SetFile(Normalize(path), contents, Encoding.UTF8.GetString(contents));

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
        var normalized = Normalize(path);
        _files.Remove(normalized);
        _fileBytes.Remove(normalized);
        _directoryLinks.Remove(normalized);
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
            _fileBytes.Remove(key);
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
            var bytes = _fileBytes[sourceKey];
            _fileBytes.Remove(sourceKey);
            SetFile(destKey, bytes, content);
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
                var movedContent = _files[file];
                var bytes = _fileBytes[file];
                _files.Remove(file);
                _fileBytes.Remove(file);
                SetFile(destFilePrefix + suffix, bytes, movedContent);
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
        var bytes = _fileBytes[sourceKey];
        _fileBytes.Remove(sourceKey);
        SetFile(destKey, bytes, content);
    }

    public string ReadAllText(string path) => _files.TryGetValue(Normalize(path), out var content)
        ? content
        : throw new FileNotFoundException($"File not found: {path}");

    public Task<string> ReadAllTextAsync(string path) => Task.FromResult(ReadAllText(path));

    public void WriteAllText(string path, string contents)
    {
        SetFile(Normalize(path), Encoding.UTF8.GetBytes(contents), contents);
    }

    public void WriteAllBytes(string path, byte[] contents) =>
        SetFile(Normalize(path), contents, Encoding.UTF8.GetString(contents));

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

    public Stream OpenRead(string path)
    {
        var normalized = Normalize(path);
        if (_fileBytes.TryGetValue(normalized, out var contents))
            return new MemoryStream(contents.ToArray(), writable: false);
        throw new FileNotFoundException($"File not found: {path}");
    }

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

    private void SetFile(string path, byte[] contents, string text)
    {
        _files[path] = text;
        _fileBytes[path] = contents.ToArray();
    }

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
                _owner.WriteAllBytes(_path, ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
