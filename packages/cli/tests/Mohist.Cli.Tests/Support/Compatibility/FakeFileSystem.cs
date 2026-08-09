using System.Text;
using Mohist.Cli;

namespace Mohist.Cli.Tests.Compatibility;

public class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _directoryLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string _currentDirectory = "/";

    public string Cwd
    {
        get
        {
            lock (_gate)
            {
                return _currentDirectory;
            }
        }
    }

    public void SetCurrentDirectory(string path)
    {
        lock (_gate)
        {
            _currentDirectory = Normalize(path);
        }
    }

    public IReadOnlyDictionary<string, string> Files
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_files, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyCollection<string> Directories
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_directories, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void AddFile(string path, string content)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files[normalized] = content;
        }
    }

    public void AddDirectory(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _directories.Add(normalized);
        }
    }

    public string Read(string path)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(Normalize(path), out var content))
                return content;
            throw new FileNotFoundException($"Fake filesystem has no file at '{path}'.");
        }
    }

    public bool HasFile(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(Normalize(path));
        }
    }

    public bool HasDirectory(string path)
    {
        lock (_gate)
        {
            return _directories.Contains(Normalize(path));
        }
    }

    public string CurrentDirectory
    {
        get
        {
            lock (_gate)
            {
                return _currentDirectory;
            }
        }
    }

    public bool Exists(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            return _files.ContainsKey(normalized) || _directories.Contains(normalized) || _directoryLinks.ContainsKey(normalized);
        }
    }

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            if (_directories.Contains(normalized))
                return true;
            if (_directoryLinks.ContainsKey(normalized))
                return true;
            return _files.Keys.Any(key => StartsWithDirectory(key, normalized));
        }
    }

    public void CreateDirectory(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _directories.Add(normalized);
        }
    }

    public void Delete(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files.Remove(normalized);
            _directoryLinks.Remove(normalized);
        }
    }

    public void DeleteDirectory(string path)
    {
        var normalized = Normalize(path);
        var prefix = normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            foreach (var dir in _directories.Where(d => d == normalized || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _directories.Remove(dir);
            }
            foreach (var key in _files.Keys.Where(k => StartsWithDirectory(k, normalized)).ToArray())
            {
                _files.Remove(key);
            }
            foreach (var link in _directoryLinks.Keys.Where(k => k == normalized || StartsWithDirectory(k, normalized)).ToArray())
            {
                _directoryLinks.Remove(link);
            }
        }
    }

    public void Move(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destKey = Normalize(destination);
        lock (_gate)
        {
            if (_directories.Contains(sourceKey))
            {
                _directories.Remove(sourceKey);
                _directories.Add(destKey);
            }

            var prefix = sourceKey.EndsWith(Path.DirectorySeparatorChar) ? sourceKey : sourceKey + Path.DirectorySeparatorChar;
            foreach (var key in _files.Keys.Where(k => k == sourceKey || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var relative = key == sourceKey ? string.Empty : key[prefix.Length..];
                var newKey = string.IsNullOrEmpty(relative) ? destKey : destKey + Path.DirectorySeparatorChar + relative;
                var content = _files[key];
                _files.Remove(key);
                _files[newKey] = content;
            }
        }
    }

    public void MoveFile(string source, string destination)
    {
        var sourceKey = Normalize(source);
        var destKey = Normalize(destination);
        lock (_gate)
        {
            if (!_files.TryGetValue(sourceKey, out var content))
                throw new FileNotFoundException($"Fake filesystem has no file at '{source}'.");
            _files.Remove(sourceKey);
            _files[destKey] = content;
        }
    }

    public string ReadAllText(string path) => Read(path);

    public Task<string> ReadAllTextAsync(string path) => Task.FromResult(Read(path));

    public void WriteAllText(string path, string contents)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files[normalized] = contents;
        }
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
        string[] snapshot;
        lock (_gate)
        {
            snapshot = _files.Keys
                .Where(key => searchOption == SearchOption.AllDirectories
                    ? key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    : key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                      && !key.Substring(prefix.Length).Contains(Path.DirectorySeparatorChar))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
        }
        return snapshot;
    }

    public Stream OpenRead(string path) => new MemoryStream(Encoding.UTF8.GetBytes(Read(path)));

    public Stream OpenWrite(string path) => new RecordingStream(this, path);

    public void ReplaceDirectorySymbolicLink(string linkPath, string targetPath)
    {
        lock (_gate)
        {
            _directoryLinks[Normalize(linkPath)] = Normalize(targetPath);
        }
    }

    public string? ReadDirectorySymbolicLink(string linkPath)
    {
        lock (_gate)
        {
            return _directoryLinks.TryGetValue(Normalize(linkPath), out var target) ? target : null;
        }
    }

    public void DeleteDirectorySymbolicLink(string linkPath)
    {
        lock (_gate)
        {
            _directoryLinks.Remove(Normalize(linkPath));
        }
    }

    private static string Normalize(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool StartsWithDirectory(string filePath, string directoryPath)
    {
        var prefix = directoryPath.EndsWith(Path.DirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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

        public override void Close()
        {
            base.Close();
            var content = Encoding.UTF8.GetString(ToArray());
            _owner.WriteAllText(_path, content);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var content = Encoding.UTF8.GetString(ToArray());
                _owner.WriteAllText(_path, content);
            }
            base.Dispose(disposing);
        }
    }
}
