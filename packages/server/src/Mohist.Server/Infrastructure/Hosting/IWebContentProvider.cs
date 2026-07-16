using Microsoft.Extensions.FileProviders;

namespace Mohist.Server.Infrastructure.Hosting;

public interface IWebContentProvider
{
    IFileProvider Files { get; }
}

public sealed class WebContentProvider : IWebContentProvider
{
    public WebContentProvider(IConfiguration configuration)
    {
        var webRoot = ResolveWebRoot(configuration);
        Files = webRoot is null
            ? new NullFileProvider()
            : new PhysicalFileProvider(webRoot);
    }

    public IFileProvider Files { get; }

    private static string? ResolveWebRoot(IConfiguration configuration)
    {
        var configured = configuration["Mohist:WebRoot"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "index.html")))
            return Path.GetFullPath(configured);

        var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(bundled, "index.html")))
            return bundled;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, "packages", "web", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;

            var parent = Directory.GetParent(directory)?.FullName;
            if (parent == directory)
                break;
            directory = parent;
        }

        return null;
    }
}
