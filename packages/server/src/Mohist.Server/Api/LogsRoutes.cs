using Mohist.Server.Logging;

namespace Mohist.Server.Api;

public static class LogsRoutes
{
    public static WebApplication MapLogsRoutes(this WebApplication app)
    {
        app.MapGet("/api/logs/tail", async (
            long? cursor,
            int? limit,
            int? maxBytes,
            ILogPathResolver pathResolver) =>
        {
            if (cursor is < 0)
            {
                return ApiResults.BadRequest("cursor must be greater than or equal to 0", "invalid_cursor");
            }

            if (limit is <= 0)
            {
                return ApiResults.BadRequest("limit must be greater than 0", "invalid_limit");
            }

            if (maxBytes is <= 0)
            {
                return ApiResults.BadRequest("maxBytes must be greater than 0", "invalid_max_bytes");
            }

            var logDir = pathResolver.Resolve();
            var expectedFile = Path.Combine(logDir, FileLoggerProvider.LogFileName);

            // Source identity resolution: the agreed primary path is
            // `server.log`. The newest-*.log glob is a transitional
            // discovery fallback for the case where server.log is not
            // present yet but an older log file is. Source identity is
            // the active file name so the Web renders it as the
            // `File:` line.
            string? activeFile = ResolveActiveFile(logDir, expectedFile);

            if (activeFile is null)
            {
                var unavailableReason = Directory.Exists(logDir)
                    ? $"Log file '{FileLoggerProvider.LogFileName}' is missing at {expectedFile}."
                    : $"Log directory does not exist at {logDir}.";
                return ApiResults.Ok(BuildUnavailable(expectedFile, unavailableReason));
            }

            var sourceName = Path.GetFileName(activeFile);
            var readLimit = limit ?? 100;
            var readMaxBytes = maxBytes ?? 64 * 1024;
            var fileLength = new FileInfo(activeFile).Length;

            // reset semantics: first read (no cursor) OR the file shrank
            // below the supplied cursor (rotation/truncation). In both
            // cases we restart from byte 0 so the client can replace
            // its view with a consistent snapshot.
            var isFirstRead = !cursor.HasValue;
            var rotated = cursor.HasValue && cursor.Value > fileLength;
            var shouldReset = isFirstRead || rotated;
            var startPosition = shouldReset ? 0L : cursor!.Value;

            var (entries, nextCursor, truncated) = await ReadTailAsync(
                activeFile, startPosition, readLimit, readMaxBytes);

            // Both `cursor` and `nextCursor` carry the same byte offset:
            // the position the client should pass back to continue.
            // `truncated` tells the client whether another immediate chunk
            // is available; an EOF cursor is still required for auto-follow.
            long? wireCursor = nextCursor;
            var reason = rotated
                ? $"Log file '{sourceName}' was rotated or truncated since the previous read; view replaced."
                : null;

            return ApiResults.Ok(new LogTailResponse(
                Lines: entries,
                Cursor: wireCursor,
                NextCursor: wireCursor,
                Source: sourceName,
                Truncated: truncated,
                Reset: shouldReset,
                Unavailable: false,
                ExpectedLocation: null,
                Reason: reason));
        });

        return app;
    }

    /// <summary>
    /// Returns the active log file path. The agreed primary path is
    /// <c>server.log</c>; the directory's newest <c>*.log</c> is a
    /// transitional fallback for environments that have not yet
    /// produced a <c>server.log</c>.
    /// </summary>
    private static string? ResolveActiveFile(string logDir, string expectedFile)
    {
        if (File.Exists(expectedFile))
        {
            return expectedFile;
        }

        if (!Directory.Exists(logDir))
        {
            return null;
        }

        var newest = Directory.GetFiles(logDir, "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return newest;
    }

    private static LogTailResponse BuildUnavailable(string expectedFile, string reason)
        => new(
            Lines: Array.Empty<LogEntry>(),
            Cursor: null,
            NextCursor: null,
            Source: null,
            Truncated: false,
            Reset: false,
            Unavailable: true,
            ExpectedLocation: expectedFile,
            Reason: reason);

    /// <summary>
    /// Reads up to <paramref name="limit"/> lines / <paramref name="maxBytes"/>
    /// bytes starting at <paramref name="startPosition"/>. Each physical
    /// line is projected into a <see cref="LogEntry"/>; non-JSON lines
    /// degrade to the same shape with null structured fields.
    /// </summary>
    /// <returns>
    /// The entries, the byte position immediately after the last line
    /// read (for the next cursor), and whether the read stopped because
    /// the cap was hit before EOF.
    /// </returns>
    /// <remarks>
    /// The cursor is tracked manually from the byte count of each line
    /// (UTF-8) plus one byte for the newline terminator. Using
    /// <c>stream.Position</c> is incorrect here because
    /// <see cref="StreamReader"/> buffers bytes ahead of the line it
    /// returns, so <c>stream.Position</c> jumps past the actual line
    /// position whenever the file fits in a single buffer read.
    /// </remarks>
    private static async Task<(IReadOnlyList<LogEntry> Entries, long NextCursor, bool Truncated)> ReadTailAsync(
        string path,
        long startPosition,
        int limit,
        int maxBytes)
    {
        var entries = new List<LogEntry>(capacity: Math.Min(limit, 256));
        long bytesRead = 0;
        long nextCursor = startPosition;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(startPosition, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        while (entries.Count < limit && bytesRead < maxBytes)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                // EOF reached cleanly within the caps. The byte position
                // matches startPosition + bytesRead (every byte before
                // here has been consumed).
                nextCursor = startPosition + bytesRead;
                return (entries, nextCursor, false);
            }

            bytesRead += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            entries.Add(LogEntryProjection.Project(line));
            nextCursor = startPosition + bytesRead;
        }

        // The cap was hit before EOF; the client should pass nextCursor
        // back to continue.
        return (entries, nextCursor, true);
    }
}
