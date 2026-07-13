using Microsoft.Extensions.FileProviders;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.TestSupport;

internal sealed class InMemoryWebContentProvider : IWebContentProvider
{
    private readonly InMemoryFileProvider _files = new();

    public InMemoryWebContentProvider(string index)
    {
        _files.SetFile("index.html", index);
    }

    public IFileProvider GetFileProvider() => _files;
}
