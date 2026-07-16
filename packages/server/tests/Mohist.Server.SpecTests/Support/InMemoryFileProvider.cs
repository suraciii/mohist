using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Mohist.Server.SpecTests.Support;

public sealed class InMemoryFileProvider : IFileProvider
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public InMemoryFileProvider AddText(string path, string content)
    {
        _files[Normalize(path)] = Encoding.UTF8.GetBytes(content);
        return this;
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IFileInfo GetFileInfo(string subpath)
    {
        var path = Normalize(subpath);
        return _files.TryGetValue(path, out var content)
            ? new InMemoryFileInfo(path, content)
            : new NotFoundFileInfo(path);
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private static string Normalize(string path) => path.TrimStart('/');

    private sealed class InMemoryFileInfo : IFileInfo
    {
        private readonly byte[] _content;

        public InMemoryFileInfo(string name, byte[] content)
        {
            Name = name;
            _content = content;
        }

        public bool Exists => true;
        public long Length => _content.Length;
        public string? PhysicalPath => null;
        public string Name { get; }
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(_content, writable: false);
    }
}
