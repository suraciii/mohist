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
            ILogTailSource source) =>
        {
            if (cursor is < 0)
                return ApiResults.BadRequest("cursor must be greater than or equal to 0", "invalid_cursor");
            if (limit is <= 0)
                return ApiResults.BadRequest("limit must be greater than 0", "invalid_limit");
            if (maxBytes is <= 0)
                return ApiResults.BadRequest("maxBytes must be greater than 0", "invalid_max_bytes");
            if (maxBytes is > MaximumTailMaxBytes)
            {
                return ApiResults.BadRequest(
                    $"maxBytes must be less than or equal to {MaximumTailMaxBytes}",
                    "invalid_max_bytes");
            }

            var snapshot = source.Open();
            if (!snapshot.Available || snapshot.OpenContent is null)
            {
                return ApiResults.Ok(BuildUnavailable(
                    source.ExpectedLocation,
                    snapshot.UnavailableReason ?? "Log source is unavailable."));
            }

            await using var content = snapshot.OpenContent();
            var fileLength = content.Length;
            var isFirstRead = !cursor.HasValue;
            var rotated = cursor.HasValue && cursor.Value > fileLength;
            var shouldReset = isFirstRead || rotated;
            var startPosition = shouldReset ? 0L : cursor!.Value;
            var (entries, nextCursor, truncated) = await ReadTailAsync(
                content,
                startPosition,
                limit ?? DefaultTailLimit,
                maxBytes ?? DefaultTailMaxBytes);
            var reason = rotated
                ? $"Log file '{snapshot.Source}' was rotated or truncated since the previous read; view replaced."
                : null;

            return ApiResults.Ok(new LogTailResponse(
                Lines: entries,
                Cursor: nextCursor,
                NextCursor: nextCursor,
                Source: snapshot.Source,
                Truncated: truncated,
                Reset: shouldReset,
                Unavailable: false,
                ExpectedLocation: null,
                Reason: reason));
        });

        return app;
    }

    private static LogTailResponse BuildUnavailable(string expectedLocation, string reason) =>
        new([], null, null, null, false, false, true, expectedLocation, reason);

    private static async Task<(IReadOnlyList<LogEntry> Entries, long NextCursor, bool Truncated)> ReadTailAsync(
        Stream stream,
        long startPosition,
        int limit,
        int maxBytes)
    {
        var entries = new List<LogEntry>(capacity: Math.Min(limit, 256));
        long bytesRead = 0;
        long nextCursor = startPosition;
        stream.Seek(startPosition, SeekOrigin.Begin);

        while (entries.Count < limit && bytesRead < maxBytes)
        {
            var lineRead = await ReadLineWithinBudgetAsync(stream, maxBytes - bytesRead);
            if (lineRead.EndOfFile && lineRead.BytesConsumed == 0)
                return (entries, startPosition + bytesRead, false);

            bytesRead += lineRead.BytesConsumed;
            nextCursor = startPosition + bytesRead;
            if (lineRead.ExceededBudget)
                return (entries, nextCursor, true);

            entries.Add(LogEntryProjection.Project(lineRead.Line ?? string.Empty));
        }

        return (entries, nextCursor, nextCursor < stream.Length);
    }

    private static async Task<BoundedLineRead> ReadLineWithinBudgetAsync(Stream stream, long byteBudget)
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
                    ? new BoundedLineRead(null, 0, true, false)
                    : new BoundedLineRead(DecodeLine(lineBytes), consumed, true, false);
            }

            consumed++;
            var value = oneByte[0];
            if (consumed > byteBudget)
            {
                if (value != (byte)'\n')
                    consumed += await DrainUntilLineEndAsync(stream);
                return new BoundedLineRead(null, consumed, false, true);
            }
            if (value == (byte)'\n')
                return new BoundedLineRead(DecodeLine(lineBytes), consumed, false, false);
            lineBytes.Add(value);
        }
    }

    private static async Task<long> DrainUntilLineEndAsync(Stream stream)
    {
        var consumed = 0L;
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory());
            if (read == 0)
                return consumed;
            for (var index = 0; index < read; index++)
            {
                consumed++;
                if (buffer[index] != (byte)'\n')
                    continue;
                var unread = read - index - 1;
                if (unread > 0)
                    stream.Seek(-unread, SeekOrigin.Current);
                return consumed;
            }
        }
    }

    private static string DecodeLine(List<byte> bytes)
    {
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
            bytes.RemoveAt(bytes.Count - 1);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record BoundedLineRead(string? Line, long BytesConsumed, bool EndOfFile, bool ExceededBudget);
}
