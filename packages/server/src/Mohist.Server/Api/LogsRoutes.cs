using System.Text.Json;

namespace Mohist.Server.Api;

public static class LogsRoutes
{
    public static WebApplication MapLogsRoutes(this WebApplication app)
    {
        app.MapGet("/api/logs/tail", async (long? cursor, int? limit, int? maxBytes) =>
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var logDir = Path.Combine(home, ".mohist", "logs");

            if (!Directory.Exists(logDir))
                return ApiResults.Ok(new { lines = Array.Empty<object>(), nextCursor = (long?)null });

            var logFile = Directory.GetFiles(logDir, "*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (logFile == null || !File.Exists(logFile))
                return ApiResults.Ok(new { lines = Array.Empty<object>(), nextCursor = (long?)null });

            var lines = new List<object>();
            long nextCursor = 0;
            var readLimit = limit ?? 100;
            var readMaxBytes = maxBytes ?? 64 * 1024;
            var startPosition = cursor ?? 0;

            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(startPosition, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var bytesRead = 0;
            string? line;
            while ((line = await reader.ReadLineAsync()) != null && lines.Count < readLimit && bytesRead < readMaxBytes)
            {
                bytesRead += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                try
                {
                    var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(line);
                    lines.Add(json);
                }
                catch
                {
                    lines.Add(new { raw = line });
                }
            }

            nextCursor = stream.Position;
            var isEnd = line is null;

            return ApiResults.Ok(new
            {
                lines,
                nextCursor = isEnd ? (long?)null : nextCursor,
            });
        });

        return app;
    }
}
