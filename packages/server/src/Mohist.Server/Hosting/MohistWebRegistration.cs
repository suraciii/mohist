using Microsoft.AspNetCore.StaticFiles;

namespace Mohist.Server.Hosting;

public static class MohistWebRegistration
{
    public static WebApplication MapMohistWeb(this WebApplication app, IConfiguration configuration)
    {
        var webRoot = ResolveWebRoot(configuration);
        if (webRoot is null)
        {
            app.MapGet("/", () => Results.Text(
                "Mohist Web UI is not built. Run `npm --prefix packages/cli/web run build` or set Mohist:WebRoot.",
                "text/plain"));
            return app;
        }

        var provider = new FileExtensionContentTypeProvider();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot),
            ContentTypeProvider = provider,
        });

        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
        });

        return app;
    }

    public static string? ResolveWebRoot(IConfiguration configuration)
    {
        var configured = configuration["Mohist:WebRoot"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "index.html")))
            return Path.GetFullPath(configured);

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, "packages", "cli", "web", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }

        return null;
    }
}
