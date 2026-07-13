using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.TestSupport;

internal sealed class InMemoryStorageFileSystem : IStorageFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reparsePoints = new(StringComparer.Ordinal);

    public bool FileExists(string path)
    {
        lock (_gate)
            return _files.ContainsKey(Normalize(path));
    }

    public bool DirectoryExists(string path)
    {
        lock (_gate)
            return _directories.Contains(Normalize(path));
    }

    public void CreateDirectory(string path)
    {
        lock (_gate)
            AddDirectoryAndParents(Normalize(path));
    }

    public void DeleteFile(string path)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            _files.Remove(normalized);
            _reparsePoints.Remove(normalized);
        }
    }

    public void DeleteDirectory(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            foreach (var file in _files.Keys.Where(key => IsWithinDirectory(key, normalized)).ToArray())
            {
                _files.Remove(file);
                _reparsePoints.Remove(file);
            }

            foreach (var directory in _directories.Where(candidate => IsWithinDirectory(candidate, normalized)).ToArray())
            {
                _directories.Remove(directory);
                _reparsePoints.Remove(directory);
            }
        }
    }

    public bool IsDirectoryEmpty(string path)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            return !_files.Keys.Any(key => IsWithinDirectory(key, normalized))
                && !_directories.Any(directory => directory != normalized && IsWithinDirectory(directory, normalized));
        }
    }

    public Stream OpenRead(string path)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            if (!_files.TryGetValue(normalized, out var bytes))
                throw new FileNotFoundException($"No stored file at '{path}'.");
            return new MemoryStream(bytes.ToArray(), writable: false);
        }
    }

    public Stream OpenWrite(string path, FileMode mode)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            if (mode == FileMode.CreateNew && _files.ContainsKey(normalized))
                throw new IOException($"A stored file already exists at '{path}'.");
        }
        return new CommitStream(this, normalized);
    }

    public void MoveFile(string source, string destination, bool overwrite)
    {
        var normalizedSource = Normalize(source);
        var normalizedDestination = Normalize(destination);
        lock (_gate)
        {
            if (!_files.TryGetValue(normalizedSource, out var bytes))
                throw new FileNotFoundException($"No stored file at '{source}'.");
            if (!overwrite && _files.ContainsKey(normalizedDestination))
                throw new IOException($"A stored file already exists at '{destination}'.");

            _files.Remove(normalizedSource);
            _files[normalizedDestination] = bytes;
            if (_reparsePoints.Remove(normalizedSource))
                _reparsePoints.Add(normalizedDestination);
            AddDirectoryAndParents(Path.GetDirectoryName(normalizedDestination)!);
        }
    }

    public IEnumerable<StorageFileEntry> EnumerateFiles(string root)
    {
        var normalized = Normalize(root);
        lock (_gate)
        {
            return _files
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Where(pair => IsWithinDirectory(pair.Key, normalized))
                .Select(pair => new StorageFileEntry(
                    pair.Key,
                    pair.Value.LongLength,
                    _reparsePoints.Contains(pair.Key)))
                .ToArray();
        }
    }

    public bool IsReparsePoint(string path)
    {
        lock (_gate)
            return _reparsePoints.Contains(Normalize(path));
    }

    public void AddFile(string path, byte[] contents, bool isReparsePoint = false)
    {
        var normalized = Normalize(path);
        lock (_gate)
        {
            _files[normalized] = contents.ToArray();
            AddDirectoryAndParents(Path.GetDirectoryName(normalized)!);
            if (isReparsePoint)
                _reparsePoints.Add(normalized);
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(Normalize(path), out var bytes))
                throw new FileNotFoundException($"No stored file at '{path}'.");
            return bytes.ToArray();
        }
    }

    public void MarkReparsePoint(string path)
    {
        lock (_gate)
            _reparsePoints.Add(Normalize(path));
    }

    private void Save(string path, byte[] bytes)
    {
        lock (_gate)
        {
            _files[path] = bytes;
            AddDirectoryAndParents(Path.GetDirectoryName(path)!);
        }
    }

    private void AddDirectoryAndParents(string directory)
    {
        for (var current = directory; ;)
        {
            _directories.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return;
            current = parent;
        }
    }

    private static string Normalize(string path)
    {
        var normalized = Path.GetFullPath(path);
        return normalized.Length > 1
            ? normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : normalized;
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        if (path == directory)
            return true;

        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private sealed class CommitStream(InMemoryStorageFileSystem owner, string path) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                owner.Save(path, ToArray());
            base.Dispose(disposing);
        }
    }
}
