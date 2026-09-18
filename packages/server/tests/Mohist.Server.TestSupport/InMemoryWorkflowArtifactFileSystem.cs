using System.Text;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.TestSupport;

/// <summary>
/// In-memory <see cref="IWorkflowArtifactFileSystem"/> used by Specs that
/// exercise the real <see cref="FileSystemWorkflowArtifactStorage"/> adapter
/// without touching a host filesystem. It owns byte content, directory
/// membership, move/delete semantics, and failure injection so the adapter's
/// manifest, hashing, ordering, and cleanup behavior can be proven hermetically.
/// </summary>
internal sealed class InMemoryWorkflowArtifactFileSystem : IWorkflowArtifactFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Invoked for every <see cref="OpenWrite"/>; a true result throws once.</summary>
    public Func<string, bool>? FailNextOpenWrite { get; set; }

    /// <summary>Invoked for every <see cref="MoveFile"/>; a true result throws once.</summary>
    public Func<string, bool>? FailNextMoveFile { get; set; }

    public bool FileExists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(path);
        }
    }

    public bool DirectoryExists(string path)
    {
        lock (_gate)
        {
            return _directories.Contains(path);
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_gate)
        {
            // Mirror Directory.CreateDirectory: materialize every parent so a
            // recursive delete of the collection root also removes the tree.
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                _directories.Add(current);
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                    break;
                current = parent;
            }
        }
    }

    public void DeleteFile(string path)
    {
        lock (_gate)
        {
            _files.Remove(path);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        lock (_gate)
        {
            _directories.Remove(path);
            if (!recursive)
                return;

            var prefix = path.EndsWith('/') ? path : path + "/";
            foreach (var directory in _directories.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                _directories.Remove(directory);
            foreach (var file in _files.Keys.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                _files.Remove(file);
        }
    }

    public void MoveFile(string source, string destination)
    {
        if (FailNextMoveFile?.Invoke(source) == true)
        {
            FailNextMoveFile = null;
            throw new IOException($"Configured move failure for '{source}'.");
        }

        lock (_gate)
        {
            if (!_files.Remove(source, out var content))
                throw new FileNotFoundException($"No in-memory file exists at '{source}'.");
            _files[destination] = content;
        }
    }

    public Stream OpenRead(string path)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var content))
                throw new FileNotFoundException($"No in-memory file exists at '{path}'.");
            return new MemoryStream(content, writable: false);
        }
    }

    public Stream OpenWrite(string path)
    {
        if (FailNextOpenWrite?.Invoke(path) == true)
        {
            FailNextOpenWrite = null;
            throw new IOException($"Configured write failure for '{path}'.");
        }

        return new CommittingStream(this, path);
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var content))
                throw new FileNotFoundException($"No in-memory file exists at '{path}'.");
            return Task.FromResult(Encoding.UTF8.GetString(content));
        }
    }

    /// <summary>Test-only seed for a file whose bytes are known to the Spec.</summary>
    public void SeedFile(string path, byte[] content)
    {
        lock (_gate)
        {
            _files[path] = content;
        }
    }

    /// <summary>Test-only read of the bytes currently held for a path.</summary>
    public byte[] ReadFile(string path)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var content))
                throw new FileNotFoundException($"No in-memory file exists at '{path}'.");
            return content;
        }
    }

    /// <summary>Test-only mutation of stored bytes that leaves the recorded manifest untouched.</summary>
    public void MutateFile(string path, byte[] content)
    {
        lock (_gate)
        {
            if (!_files.ContainsKey(path))
                throw new FileNotFoundException($"No in-memory file exists at '{path}'.");
            _files[path] = content;
        }
    }

    /// <summary>Test-only enumeration of recorded paths, ordinal-sorted.</summary>
    public IReadOnlyList<string> Paths()
    {
        lock (_gate)
        {
            return _files.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
    }

    private void Commit(string path, byte[] content)
    {
        lock (_gate)
        {
            _files[path] = content;
        }
    }

    private sealed class CommittingStream : MemoryStream
    {
        private readonly InMemoryWorkflowArtifactFileSystem _owner;
        private readonly string _path;
        private bool _committed;

        public CommittingStream(InMemoryWorkflowArtifactFileSystem owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                _owner.Commit(_path, ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
