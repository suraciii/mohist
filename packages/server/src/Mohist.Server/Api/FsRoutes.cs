using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class FsRoutes
{
    public const string HomeEnvironmentVariable = "HOME";

    public static WebApplication MapFsRoutes(this WebApplication app)
    {
        app.MapGet("/api/fs/home", (IEnvironmentVariableProvider environment) =>
        {
            var home = environment.GetEnvironmentVariable(HomeEnvironmentVariable)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return ApiResults.Ok(home);
        }).RequireScopes(Scope.Operator);

        app.MapGet("/api/fs/list", (string path) =>
        {
            if (!Directory.Exists(path))
                return ApiResults.NotFound("Directory not found");

            try
            {
                var entries = Directory.GetFileSystemEntries(path)
                    .Select(e => new
                    {
                        name = Path.GetFileName(e),
                        path = e,
                        isDirectory = Directory.Exists(e),
                    });
                return ApiResults.Ok(entries);
            }
            catch (Exception ex)
            {
                return ApiResults.BadRequest($"Cannot list directory: {ex.Message}");
            }
        }).RequireScopes(Scope.Operator);

        app.MapGet("/api/fs/search", (string query, int? limit, IEnvironmentVariableProvider environment) =>
        {
            var home = environment.GetEnvironmentVariable(HomeEnvironmentVariable)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var results = new List<object>();
            var searchLimit = limit ?? 50;

            try
            {
                SearchDirectory(home, query, results, searchLimit);
            }
            catch { }

            return ApiResults.Ok(results.Take(searchLimit));
        }).RequireScopes(Scope.Operator);

        return app;
    }

    private static void SearchDirectory(string root, string query, List<object> results, int limit)
    {
        if (results.Count >= limit) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (results.Count >= limit) return;
            var name = Path.GetFileName(dir);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new { name, path = dir, isDirectory = true });
            }

            // Skip hidden and common non-project dirs
            var skipList = new[] { ".git", "node_modules", ".next", "dist", "bin", "obj", ".venv", "vendor" };
            if (skipList.Any(s => name.StartsWith(s)))
                continue;

            try
            {
                SearchDirectory(dir, query, results, limit);
            }
            catch { }
        }
    }
}
