using Microsoft.AspNetCore.StaticFiles;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistWebRegistration
{
    public static WebApplication MapMohistWeb(this WebApplication app, IConfiguration configuration)
    {
        var webRoot = ResolveWebRoot(configuration);
        if (webRoot is null)
        {
            app.MapGet("/", () => Results.Text(
                "Mohist Web UI is not built. Run `npm run build:web` or set Mohist:WebRoot.",
                "text/plain"));
            return app;
        }

        var provider = new FileExtensionContentTypeProvider();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot),
            ContentTypeProvider = provider,
        });

        app.MapGet("/issue/{number:int}/session/{**sessionId}", async context => await SendIndexAsync(context, webRoot));
        app.MapGet("/issues/{number:int}/workflow/sessions/{**sessionName}", async context => await SendIndexAsync(context, webRoot));

        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await SendIndexAsync(context, webRoot);
        });

        return app;
    }

    public static string? ResolveWebRoot(IConfiguration configuration)
    {
        var configured = configuration["Mohist:WebRoot"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "index.html")))
            return Path.GetFullPath(configured);

        var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(bundled, "index.html")))
            return bundled;

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, "packages", "web", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }

        return null;
    }

    private static async Task SendIndexAsync(HttpContext context, string webRoot)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
    }
}
