using Microsoft.Extensions.FileProviders;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryWebContentProvider : IWebContentProvider
{
    public InMemoryWebContentProvider()
    {
        Files = new InMemoryFileProvider()
            .AddText("index.html", "<html><body>Mohist Test Web</body></html>")
            .AddText("assets/app.css", "body{color:red}")
            .AddText("assets/app-12345678.css", "body{color:red}");
    }

    public IFileProvider Files { get; }
}
