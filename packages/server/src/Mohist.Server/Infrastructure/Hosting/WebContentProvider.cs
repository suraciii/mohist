using Microsoft.Extensions.FileProviders;

namespace Mohist.Server.Infrastructure.Hosting;

internal interface IWebContentProvider
{
    IFileProvider? GetFileProvider();
}

internal sealed class FileSystemWebContentProvider(IConfiguration configuration) : IWebContentProvider
{
    public IFileProvider? GetFileProvider()
    {
        var root = MohistWebRegistration.ResolveWebRoot(configuration);
        return root is null ? null : new PhysicalFileProvider(root);
    }
}
