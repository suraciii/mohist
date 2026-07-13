using Mohist.Server.Logging;
using System.Text;

namespace Mohist.Server.Api;

public static class LogsRoutes
{
    private const int DefaultTailLimit = 100;
    private const int DefaultTailMaxBytes = 64 * 1024;
    private const int MaximumTailMaxBytes = 1024 * 1024;

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

            if (maxBytes is > MaximumTailMaxBytes)
            {
                return ApiResults.BadRequest(
                    $"maxBytes must be less than or equal to {MaximumTailMaxBytes}",
                    "invalid_max_bytes");
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
            var readLimit = limit ?? DefaultTailLimit;
            var readMaxBytes = maxBytes ?? DefaultTailMaxBytes;
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

        while (entries.Count < limit && bytesRead < maxBytes)
        {
            var remainingBytes = maxBytes - bytesRead;
            var lineRead = await ReadLineWithinBudgetAsync(stream, remainingBytes);
            if (lineRead.EndOfFile && lineRead.BytesConsumed == 0)
            {
                // EOF reached cleanly within the caps. The byte position
                // matches startPosition + bytesRead (every byte before
                // here has been consumed).
                nextCursor = startPosition + bytesRead;
                return (entries, nextCursor, false);
            }

            bytesRead += lineRead.BytesConsumed;
            nextCursor = startPosition + bytesRead;

            if (lineRead.ExceededBudget)
            {
                return (entries, nextCursor, true);
            }

            entries.Add(LogEntryProjection.Project(lineRead.Line ?? string.Empty));
        }

        // The cap was hit before EOF; the client should pass nextCursor
        // back to continue.
        return (entries, nextCursor, nextCursor < stream.Length);
    }

    private static async Task<BoundedLineRead> ReadLineWithinBudgetAsync(FileStream stream, long byteBudget)
    {
        var lineBytes = new List<byte>(capacity: (int)Math.Min(byteBudget, 4096));
        var consumed = 0L;
        var oneByte = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(0, 1));
            if (read == 0)
            {
                return consumed == 0
                    ? new BoundedLineRead(null, 0, EndOfFile: true, ExceededBudget: false)
                    : new BoundedLineRead(DecodeLine(lineBytes), consumed, EndOfFile: true, ExceededBudget: false);
            }

            consumed++;
            var b = oneByte[0];
            if (consumed > byteBudget)
            {
                if (b != (byte)'\n')
                {
                    consumed += await DrainUntilLineEndAsync(stream);
                }
                return new BoundedLineRead(null, consumed, EndOfFile: false, ExceededBudget: true);
            }

            if (b == (byte)'\n')
            {
                return new BoundedLineRead(DecodeLine(lineBytes), consumed, EndOfFile: false, ExceededBudget: false);
            }

            lineBytes.Add(b);
        }
    }

    private static async Task<long> DrainUntilLineEndAsync(FileStream stream)
    {
        var consumed = 0L;
        var buffer = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0)
            {
                return consumed;
            }

            for (var i = 0; i < read; i++)
            {
                consumed++;
                if (buffer[i] == (byte)'\n')
                {
                    var unread = read - i - 1;
                    if (unread > 0)
                    {
                        stream.Seek(-unread, SeekOrigin.Current);
                    }
                    return consumed;
                }
            }
        }
    }

    private static string DecodeLine(List<byte> lineBytes)
    {
        if (lineBytes.Count > 0 && lineBytes[^1] == (byte)'\r')
        {
            lineBytes.RemoveAt(lineBytes.Count - 1);
        }

        return Encoding.UTF8.GetString(lineBytes.ToArray());
    }

    private sealed record BoundedLineRead(string? Line, long BytesConsumed, bool EndOfFile, bool ExceededBudget);
}
