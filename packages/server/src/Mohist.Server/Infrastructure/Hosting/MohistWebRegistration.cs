using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistWebRegistration
{
    public static WebApplication MapMohistWeb(this WebApplication app)
    {
        var fileProvider = app.Services.GetRequiredService<IWebContentProvider>().GetFileProvider();
        var index = fileProvider?.GetFileInfo("index.html");
        if (index is not { Exists: true })
        {
            app.MapGet("/", () => Results.Text(
                "Mohist Web UI is not built. Run `npm run build:web` or set Mohist:WebRoot.",
                "text/plain"));
            return app;
        }

        var provider = new FileExtensionContentTypeProvider();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            ContentTypeProvider = provider,
        });

        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            // /api/* and /otel/v1/* have their own route groups; if a
            // request falls through to the SPA fallback for those
            // paths it means no route matched (wrong method, no
            // resource, etc.) and the right answer is 404 — not the
            // web shell. Without this guard, GET /otel/v1/traces from
            // the main port would serve index.html and let callers
            // probe the OTLP listener.
            if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/otel/v1", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await SendIndexAsync(context, index);
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

    private static async Task SendIndexAsync(HttpContext context, IFileInfo index)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = index.Length;
        await using var stream = index.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
