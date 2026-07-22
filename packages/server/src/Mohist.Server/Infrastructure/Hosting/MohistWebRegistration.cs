using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistWebRegistration
{
    public static WebApplication MapMohistWeb(this WebApplication app)
    {
        var files = app.Services.GetRequiredService<IWebContentProvider>().Files;
        var index = files.GetFileInfo("index.html");
        if (!index.Exists)
        {
            app.MapGet("/", () => Results.Text(
                "Mohist Web UI is not built. Run `npm run build:web` or set Mohist:WebRoot.",
                "text/plain"));
            return app;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public,max-age=31536000,immutable"
                    : "no-cache";
            },
        });

        app.MapFallback("{*path:notstaticfile}", async context =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/otel/v1", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.CacheControl = "no-cache";
            await SendIndexAsync(context, files);
        });

        return app;
    }

    private static async Task SendIndexAsync(HttpContext context, IFileProvider files)
    {
        var index = files.GetFileInfo("index.html");
        if (!index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = index.Length;
        await using var content = index.CreateReadStream();
        await content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
