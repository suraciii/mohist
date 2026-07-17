using Microsoft.Extensions.FileProviders;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SpecTests.Support;

public sealed class InMemoryWebContentProvider : IWebContentProvider
{
    public InMemoryWebContentProvider()
    {
        Files = new InMemoryFileProvider()
            .AddText("index.html", "<html><body>Mohist Test Web</body></html>")
            .AddText("assets/app.css", "body{color:red}");
    }

    public IFileProvider Files { get; }
}
