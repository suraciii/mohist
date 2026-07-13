using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Mohist.Server.TestSupport;

internal sealed class InMemoryFileProvider : IFileProvider
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public void SetFile(string path, string contents) =>
        _files[Normalize(path)] = System.Text.Encoding.UTF8.GetBytes(contents);

    public IFileInfo GetFileInfo(string subpath)
    {
        var path = Normalize(subpath);
        return _files.TryGetValue(path, out var contents)
            ? new MemoryFileInfo(path, contents)
            : new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed class MemoryFileInfo(string path, byte[] contents) : IFileInfo
    {
        public bool Exists => true;
        public long Length => contents.LongLength;
        public string? PhysicalPath => null;
        public string Name => Path.GetFileName(path);
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(contents.ToArray(), writable: false);
    }
}
