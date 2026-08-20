using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Logging;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

/// <summary>
/// Calculation specs for <see cref="LogTailReader"/>, the service behind
/// <c>GET /api/logs/tail</c>. These assert the tail semantics (line-cap
/// truncation + cursor, rotation-shrink reset, no-new-lines, auto-follow,
/// max-bytes line truncation, unavailable detection, logfmt projection)
/// without an HTTP round-trip. The route contract (response shape, query
/// parameter validation, source file-name passthrough) stays in
/// <c>LogsRouteSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class LogTailReaderSpecs
{
    private readonly MohistDbFixture _fixture;

    public LogTailReaderSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private InMemoryLogTailSource Source =>
        _fixture.Services.GetRequiredService<InMemoryLogTailSource>();

    private LogTailReader CreateReader() =>
        _fixture.Services.GetRequiredService<LogTailReader>();

    private void ResetState() => Source.ResetDirectoryMissing();

    private void SeedServerLog(params string[] lines) => Source.SetLines(lines);

    private static string LogfmtLine(int i) =>
        $$"""time=2026-06-30T12:00:0{{i}}.000Z level=INFO msg="line {{i}}" service=server component=logs""";

    [Fact]
    public async Task ReadTailAsync_WhenLogDirectoryMissing_ReturnsUnavailableWithExpectedLocation()
    {
        ResetState();

        var result = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: null);

        Assert.True(result.Unavailable);
        Assert.Null(result.Source);
        Assert.Null(result.Cursor);
        Assert.Null(result.NextCursor);
        Assert.False(result.Reset);
        Assert.False(result.Truncated);
        Assert.Empty(result.Lines);
        Assert.Equal(Source.ExpectedLocation, result.ExpectedLocation);
        Assert.NotNull(result.Reason);
        Assert.Contains("does not exist", result.Reason);
    }

    [Fact]
    public async Task ReadTailAsync_WhenLogDirectoryExistsButServerLogMissing_ReturnsUnavailableWithReason()
    {
        Source.ResetFileMissing();

        var result = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: null);

        Assert.True(result.Unavailable);
        Assert.Equal(Source.ExpectedLocation, result.ExpectedLocation);
        Assert.NotNull(result.Reason);
        Assert.Contains("is missing", result.Reason);
        Assert.Null(result.Source);
    }

    [Fact]
    public async Task ReadTailAsync_WhenLineCapReachedBeforeEof_ReportsTruncatedTrueAndAdvancesCursor()
    {
        ResetState();
        SeedServerLog(Enumerable.Range(1, 5).Select(LogfmtLine).ToArray());

        var first = await CreateReader().ReadTailAsync(cursor: null, limit: 2, maxBytes: null);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal("line 1", first.Lines[0].Message);
        Assert.Equal("line 2", first.Lines[1].Message);
        Assert.True(first.Truncated);
        Assert.True(first.Reset);
        Assert.True(first.NextCursor > 0);

        // Passing the cursor back yields only lines after the previous
        // read and returns an updated nextCursor.
        var second = await CreateReader().ReadTailAsync(first.NextCursor, limit: 2, maxBytes: null);
        Assert.Equal(2, second.Lines.Count);
        Assert.Equal("line 3", second.Lines[0].Message);
        Assert.Equal("line 4", second.Lines[1].Message);
        Assert.True(second.Truncated);
        Assert.False(second.Reset);
        Assert.True(second.NextCursor > first.NextCursor);

        // Final chunk reaches EOF, but still returns the byte offset for the
        // next poll. `truncated=false` is the no-more-immediate-chunk signal.
        var third = await CreateReader().ReadTailAsync(second.NextCursor, limit: 2, maxBytes: null);
        var thirdLine = Assert.Single(third.Lines);
        Assert.Equal("line 5", thirdLine.Message);
        Assert.False(third.Truncated);
        Assert.True(third.NextCursor > second.NextCursor);
        Assert.Equal(third.NextCursor, third.Cursor);
    }

    [Fact]
    public async Task ReadTailAsync_WhenFileShrinksBelowCursor_ReportsResetTrue()
    {
        ResetState();
        var lines = Enumerable.Range(1, 5).Select(LogfmtLine).ToArray();
        SeedServerLog(lines);

        var fileLength = System.Text.Encoding.UTF8.GetByteCount(string.Concat(lines.Select(line => line + "\n")));
        // Pretend the client had a cursor near the end of the file, but
        // the file has now been rotated/truncated so its length is
        // smaller. The reader must detect the shrink, restart from
        // byte 0, and report reset=true.
        var forgedCursor = fileLength + 10;

        var result = await CreateReader().ReadTailAsync(forgedCursor, limit: 2, maxBytes: null);

        Assert.True(result.Reset);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("line 1", result.Lines[0].Message);
        Assert.Equal("line 2", result.Lines[1].Message);
        Assert.NotNull(result.Reason);
        Assert.Contains("rotated", result.Reason);
    }

    [Fact]
    public async Task ReadTailAsync_AvailableButNoNewLinesSinceCursor_IsNotReportedAsUnavailable()
    {
        ResetState();
        SeedServerLog(Enumerable.Range(1, 3).Select(LogfmtLine).ToArray());

        // Consume the file end-to-end.
        var first = await CreateReader().ReadTailAsync(cursor: null, limit: 10, maxBytes: null);
        Assert.False(first.Unavailable);
        Assert.Equal(3, first.Lines.Count);

        // The next read at the returned EOF cursor must NOT report unavailable
        // or reset, even though there are zero new lines.
        var eofCursor = first.NextCursor;
        var second = await CreateReader().ReadTailAsync(eofCursor, limit: null, maxBytes: null);
        Assert.False(second.Unavailable);
        Assert.False(second.Reset);
        Assert.Empty(second.Lines);
        Assert.Equal(eofCursor, second.Cursor);
        Assert.Equal(eofCursor, second.NextCursor);
        Assert.Null(second.ExpectedLocation);
    }

    [Fact]
    public async Task ReadTailAsync_AutoFollowFromEof_DoesNotReplayAndReturnsOnlyAppendedLines()
    {
        ResetState();
        var initialLine = "time=2026-06-30T12:00:01.000Z level=INFO msg=initial service=server component=logs";
        var appendedLine = "time=2026-06-30T12:00:02.000Z level=WARN msg=appended service=server component=logs";
        SeedServerLog(initialLine);

        var first = await CreateReader().ReadTailAsync(cursor: null, limit: 10, maxBytes: null);
        Assert.False(first.Truncated);
        Assert.True(first.Reset);
        var eofCursor = first.NextCursor;
        Assert.True(eofCursor > 0);

        var emptyPoll = await CreateReader().ReadTailAsync(eofCursor, limit: 10, maxBytes: null);
        Assert.False(emptyPoll.Reset);
        Assert.False(emptyPoll.Truncated);
        Assert.Empty(emptyPoll.Lines);
        Assert.Equal(eofCursor, emptyPoll.NextCursor);

        Source.AppendLine(appendedLine);

        var afterAppend = await CreateReader().ReadTailAsync(eofCursor, limit: 10, maxBytes: null);
        Assert.False(afterAppend.Reset);
        var returnedLine = Assert.Single(afterAppend.Lines);
        Assert.Equal("appended", returnedLine.Message);
        Assert.Equal("WARN", returnedLine.Level);
        Assert.True(afterAppend.NextCursor > eofCursor);
    }

    [Fact]
    public async Task ReadTailAsync_WhenSinglePhysicalLineExceedsMaxBytes_DoesNotReturnOversizedLineAndAdvancesCursor()
    {
        ResetState();
        var oversizedLine = new string('x', 4096);
        var followingLine = "time=2026-06-30T12:00:01.000Z level=INFO msg=\"after oversized\" service=server component=logs";
        SeedServerLog(oversizedLine, followingLine);

        var first = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: 64);

        Assert.True(first.Truncated);
        Assert.True(first.Reset);
        Assert.Empty(first.Lines);
        var cursorAfterOversizedLine = first.NextCursor;
        Assert.True(cursorAfterOversizedLine > 64);

        var second = await CreateReader().ReadTailAsync(cursorAfterOversizedLine, limit: null, maxBytes: 1024);
        Assert.False(second.Truncated);
        Assert.False(second.Reset);
        var line = Assert.Single(second.Lines);
        Assert.Equal("after oversized", line.Message);
    }

    [Fact]
    public async Task ReadTailAsync_NonJsonLine_DegradesToElementWithRawMessageAndNullStructuredFields()
    {
        ResetState();
        SeedServerLog("this is not json at all");

        var result = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: null);

        var entry = Assert.Single(result.Lines);
        Assert.Null(entry.Level);
        Assert.Null(entry.Time);
        Assert.Null(entry.Service);
        Assert.Equal("this is not json at all", entry.Message);
        Assert.Equal("this is not json at all", entry.Raw);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"raw\":\"x\"}")]
    public async Task ReadTailAsync_InvalidStructuredLine_DegradesToRawElement(string line)
    {
        ResetState();
        SeedServerLog(line);

        var result = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: null);

        var entry = Assert.Single(result.Lines);
        Assert.Null(entry.Level);
        Assert.Null(entry.Time);
        Assert.Null(entry.Service);
        Assert.Equal(line, entry.Message);
        Assert.Equal(line, entry.Raw);
    }

    [Fact]
    public async Task ReadTailAsync_MixedLogfmtAndInvalidLines_BothProjectToTheSameElementType()
    {
        ResetState();
        SeedServerLog(
            "time=2026-06-30T12:00:01.000Z level=INFO msg=structured service=server component=logs",
            "not-json-blob",
            "time=2026-06-30T12:00:03.000Z level=WARN msg=structured service=server component=logs");

        var result = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: null);

        Assert.Equal(3, result.Lines.Count);

        var structured1 = result.Lines[0];
        Assert.Equal("INFO", structured1.Level);
        Assert.Equal("server", structured1.Service);
        Assert.Equal("structured", structured1.Message);
        Assert.NotNull(structured1.Time);

        var degraded = result.Lines[1];
        Assert.Null(degraded.Level);
        Assert.Null(degraded.Time);
        Assert.Null(degraded.Service);
        Assert.Equal("not-json-blob", degraded.Message);
        Assert.Equal("not-json-blob", degraded.Raw);

        var structured2 = result.Lines[2];
        Assert.Equal("WARN", structured2.Level);
        Assert.Equal("structured", structured2.Message);
    }

    [Fact]
    public async Task ReadTailAsync_LogfmtLine_RoundTripsWithoutFieldLoss()
    {
        ResetState();
        var line = "time=2026-06-30T12:00:00.000Z level=INFO msg=\"logger-roundtrip 42\" service=server component=logs work=w_abc";
        SeedServerLog(line);

        var result = await CreateReader().ReadTailAsync(cursor: null, limit: null, maxBytes: null);

        var entry = Assert.Single(result.Lines);
        Assert.Equal("INFO", entry.Level);
        Assert.Equal("server", entry.Service);
        Assert.Equal("logger-roundtrip 42", entry.Message);
        Assert.Equal(line, entry.Raw);
    }
}
