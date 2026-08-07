using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Logging;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class LogsRouteSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public LogsRouteSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private InMemoryLogTailSource Source =>
        _fixture.Services.GetRequiredService<InMemoryLogTailSource>();

    private void ResetState() => Source.ResetDirectoryMissing();

    private Task SeedServerLogAsync(params string[] lines)
    {
        Source.SetLines(lines);
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> GetTailAsync(HttpClient client, string? query = null)
    {
        using var response = await client.GetAsync($"/api/logs/tail{query ?? string.Empty}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        return envelope.GetProperty("data");
    }

    private static async Task AssertBadRequestAsync(HttpClient client, string query, string expectedCode)
    {
        using var response = await client.GetAsync($"/api/logs/tail{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(expectedCode, envelope.GetProperty("code").GetString());
    }

    private static LogEntry AssertEntry(JsonElement entry)
    {
        Assert.True(entry.TryGetProperty("raw", out var raw));
        Assert.True(entry.TryGetProperty("message", out var message));
        return new LogEntry(
            Level: entry.TryGetProperty("level", out var level) && level.ValueKind != JsonValueKind.Null
                ? level.GetString() : null,
            Time: entry.TryGetProperty("time", out var time) && time.ValueKind != JsonValueKind.Null
                ? time.GetString() : null,
            Service: entry.TryGetProperty("service", out var svc) && svc.ValueKind != JsonValueKind.Null
                ? svc.GetString() : null,
            Message: message.GetString() ?? string.Empty,
            Raw: raw.GetString() ?? string.Empty);
    }

    [Fact]
    public async Task Get_WhenLogDirectoryMissing_ReturnsUnavailableWithExpectedLocation()
    {
        ResetState();

        var data = await GetTailAsync(_fixture.Client);

        Assert.True(data.GetProperty("unavailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("cursor").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
        Assert.False(data.GetProperty("reset").GetBoolean());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, data.GetProperty("lines").GetArrayLength());

        var expectedLocation = data.GetProperty("expectedLocation").GetString();
        Assert.Equal(Source.ExpectedLocation, expectedLocation);

        var reason = data.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("does not exist", reason);
    }

    [Fact]
    public async Task Get_WhenLogDirectoryExistsButServerLogMissing_ReturnsUnavailableWithReason()
    {
        Source.ResetFileMissing();

        var data = await GetTailAsync(_fixture.Client);

        Assert.True(data.GetProperty("unavailable").GetBoolean());
        Assert.Equal(Source.ExpectedLocation, data.GetProperty("expectedLocation").GetString());
        var reason = data.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("is missing", reason);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("source").ValueKind);
    }

    [Fact]
    public async Task Get_OnFirstRead_AlwaysCarriesTheAgreedResponseShape()
    {
        ResetState();
        var line = "time=2026-06-30T12:00:00.000Z level=INFO msg=hello service=server component=logs";
        await SeedServerLogAsync(line);

        var data = await GetTailAsync(_fixture.Client);

        // Every field the page depends on must be present.
        Assert.True(data.TryGetProperty("lines", out _));
        Assert.True(data.TryGetProperty("cursor", out _));
        Assert.True(data.TryGetProperty("nextCursor", out _));
        Assert.True(data.TryGetProperty("source", out _));
        Assert.True(data.TryGetProperty("truncated", out _));
        Assert.True(data.TryGetProperty("reset", out _));
        Assert.True(data.TryGetProperty("unavailable", out _));
        Assert.True(data.TryGetProperty("expectedLocation", out _));
        Assert.True(data.TryGetProperty("reason", out _));

        // First read (no cursor) — reset is true so the client replaces
        // its view.
        Assert.True(data.GetProperty("reset").GetBoolean());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.GetProperty("unavailable").GetBoolean());

        // source reflects the active log file name so the Web renders
        // it as the File: line.
        Assert.Equal(InMemoryLogTailSource.SourceName, data.GetProperty("source").GetString());

        // cursor/nextCursor remain the EOF byte offset so auto-follow can
        // poll from the end without replaying the file.
        var eofCursor = data.GetProperty("cursor").GetInt64();
        Assert.True(eofCursor > 0);
        Assert.Equal(eofCursor, data.GetProperty("nextCursor").GetInt64());

        // expectedLocation/reason are null in the available path.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("expectedLocation").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Get_WhenLineCapReachedBeforeEof_ReportsTruncatedTrueAndAdvancesCursor()
    {
        ResetState();
        var lines = Enumerable.Range(1, 5)
            .Select(i => $$"""time=2026-06-30T12:00:0{{i}}.000Z level=INFO msg="line {{i}}" service=server component=logs""")
            .ToArray();
        await SeedServerLogAsync(lines);

        var first = await GetTailAsync(_fixture.Client, "?limit=2");
        var firstLines = first.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, firstLines.Count);
        Assert.Equal("line 1", firstLines[0].GetProperty("message").GetString());
        Assert.Equal("line 2", firstLines[1].GetProperty("message").GetString());
        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.True(first.GetProperty("reset").GetBoolean());

        var firstCursor = first.GetProperty("nextCursor").GetInt64();
        Assert.True(firstCursor > 0);

        // Passing the cursor back yields only lines after the previous
        // read and returns an updated nextCursor.
        var second = await GetTailAsync(_fixture.Client, $"?cursor={firstCursor}&limit=2");
        var secondLines = second.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, secondLines.Count);
        Assert.Equal("line 3", secondLines[0].GetProperty("message").GetString());
        Assert.Equal("line 4", secondLines[1].GetProperty("message").GetString());
        Assert.True(second.GetProperty("truncated").GetBoolean());
        Assert.False(second.GetProperty("reset").GetBoolean());

        var secondCursor = second.GetProperty("nextCursor").GetInt64();
        Assert.True(secondCursor > firstCursor);

        // Final chunk reaches EOF, but still returns the byte offset for the
        // next poll. `truncated=false` is the no-more-immediate-chunk signal.
        var third = await GetTailAsync(_fixture.Client, $"?cursor={secondCursor}&limit=2");
        var thirdLines = third.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(thirdLines);
        Assert.Equal("line 5", thirdLines[0].GetProperty("message").GetString());
        Assert.False(third.GetProperty("truncated").GetBoolean());
        var thirdCursor = third.GetProperty("nextCursor").GetInt64();
        Assert.True(thirdCursor > secondCursor);
        Assert.Equal(thirdCursor, third.GetProperty("cursor").GetInt64());
    }

    [Fact]
    public async Task Get_WhenFileShrinksBelowCursor_ReportsResetTrue()
    {
        ResetState();
        var lines = Enumerable.Range(1, 5)
            .Select(i => $$"""time=2026-06-30T12:00:0{{i}}.000Z level=INFO msg="line {{i}}" service=server component=logs""")
            .ToArray();
        await SeedServerLogAsync(lines);

        var fileLength = System.Text.Encoding.UTF8.GetByteCount(string.Concat(lines.Select(line => line + "\n")));
        // Pretend the client had a cursor near the end of the file, but
        // the file has now been rotated/truncated so its length is
        // smaller. The endpoint must detect the shrink, restart from
        // byte 0, and report reset=true.
        var forgedCursor = fileLength + 10;

        var data = await GetTailAsync(_fixture.Client, $"?cursor={forgedCursor}&limit=2");
        Assert.True(data.GetProperty("reset").GetBoolean());

        var returnedLines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, returnedLines.Count);
        Assert.Equal("line 1", returnedLines[0].GetProperty("message").GetString());
        Assert.Equal("line 2", returnedLines[1].GetProperty("message").GetString());

        // reason explains why reset fired (rotation/truncation path).
        var reason = data.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("rotated", reason);
    }

    [Fact]
    public async Task Get_AvailableButNoNewLinesSinceCursor_IsNotReportedAsUnavailable()
    {
        ResetState();
        var lines = Enumerable.Range(1, 3)
            .Select(i => $$"""time=2026-06-30T12:00:0{{i}}.000Z level=INFO msg="line {{i}}" service=server component=logs""")
            .ToArray();
        await SeedServerLogAsync(lines);

        // Consume the file end-to-end.
        var first = await GetTailAsync(_fixture.Client, "?limit=10");
        Assert.False(first.GetProperty("unavailable").GetBoolean());
        var firstLines = first.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, firstLines.Count);

        // The next read at the returned EOF cursor must NOT report unavailable
        // or reset, even though there are zero new lines.
        var eofCursor = first.GetProperty("nextCursor").GetInt64();
        var second = await GetTailAsync(_fixture.Client, $"?cursor={eofCursor}");
        Assert.False(second.GetProperty("unavailable").GetBoolean());
        Assert.False(second.GetProperty("reset").GetBoolean());
        Assert.Equal(0, second.GetProperty("lines").GetArrayLength());
        Assert.Equal(eofCursor, second.GetProperty("cursor").GetInt64());
        Assert.Equal(eofCursor, second.GetProperty("nextCursor").GetInt64());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("expectedLocation").ValueKind);
    }

    [Fact]
    public async Task Get_AutoFollowFromEof_DoesNotReplayAndReturnsOnlyAppendedLines()
    {
        ResetState();
        var initialLine = "time=2026-06-30T12:00:01.000Z level=INFO msg=initial service=server component=logs";
        var appendedLine = "time=2026-06-30T12:00:02.000Z level=WARN msg=appended service=server component=logs";
        await SeedServerLogAsync(initialLine);

        var first = await GetTailAsync(_fixture.Client, "?limit=10");
        Assert.False(first.GetProperty("truncated").GetBoolean());
        Assert.True(first.GetProperty("reset").GetBoolean());
        var eofCursor = first.GetProperty("nextCursor").GetInt64();
        Assert.True(eofCursor > 0);

        var emptyPoll = await GetTailAsync(_fixture.Client, $"?cursor={eofCursor}&limit=10");
        Assert.False(emptyPoll.GetProperty("reset").GetBoolean());
        Assert.False(emptyPoll.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, emptyPoll.GetProperty("lines").GetArrayLength());
        Assert.Equal(eofCursor, emptyPoll.GetProperty("nextCursor").GetInt64());

        Source.AppendLine(appendedLine);

        var afterAppend = await GetTailAsync(_fixture.Client, $"?cursor={eofCursor}&limit=10");
        Assert.False(afterAppend.GetProperty("reset").GetBoolean());
        var returnedLines = afterAppend.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(returnedLines);
        Assert.Equal("appended", returnedLines[0].GetProperty("message").GetString());
        Assert.Equal("WARN", returnedLines[0].GetProperty("level").GetString());
        Assert.True(afterAppend.GetProperty("nextCursor").GetInt64() > eofCursor);
    }

    [Theory]
    [InlineData("?cursor=-1", "invalid_cursor")]
    [InlineData("?limit=0", "invalid_limit")]
    [InlineData("?limit=-1", "invalid_limit")]
    [InlineData("?maxBytes=0", "invalid_max_bytes")]
    [InlineData("?maxBytes=-1", "invalid_max_bytes")]
    [InlineData("?maxBytes=1048577", "invalid_max_bytes")]
    public async Task Get_WhenQueryParametersAreInvalid_ReturnsBadRequest(string query, string expectedCode)
    {
        ResetState();

        await AssertBadRequestAsync(_fixture.Client, query, expectedCode);
    }

    [Fact]
    public async Task Get_WhenSinglePhysicalLineExceedsMaxBytes_DoesNotReturnOversizedLineAndAdvancesCursor()
    {
        ResetState();
        var oversizedLine = new string('x', 4096);
        var followingLine = "time=2026-06-30T12:00:01.000Z level=INFO msg=\"after oversized\" service=server component=logs";
        await SeedServerLogAsync(oversizedLine, followingLine);

        var first = await GetTailAsync(_fixture.Client, "?maxBytes=64");

        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.True(first.GetProperty("reset").GetBoolean());
        Assert.Equal(0, first.GetProperty("lines").GetArrayLength());
        var cursorAfterOversizedLine = first.GetProperty("nextCursor").GetInt64();
        Assert.True(cursorAfterOversizedLine > 64);

        var second = await GetTailAsync(_fixture.Client, $"?cursor={cursorAfterOversizedLine}&maxBytes=1024");
        Assert.False(second.GetProperty("truncated").GetBoolean());
        Assert.False(second.GetProperty("reset").GetBoolean());

        var lines = second.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        Assert.Equal("after oversized", lines[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Get_NonJsonLine_DegradesToElementWithRawMessageAndNullStructuredFields()
    {
        ResetState();
        await SeedServerLogAsync("this is not json at all");

        var data = await GetTailAsync(_fixture.Client);
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        var entry = AssertEntry(lines[0]);

        Assert.Null(entry.Level);
        Assert.Null(entry.Time);
        Assert.Null(entry.Service);
        Assert.Equal("this is not json at all", entry.Message);
        Assert.Equal("this is not json at all", entry.Raw);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"raw\":\"x\"}")]
    public async Task Get_InvalidStructuredLine_DegradesToRawElement(string line)
    {
        ResetState();
        await SeedServerLogAsync(line);

        var data = await GetTailAsync(_fixture.Client);
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        var entry = AssertEntry(lines[0]);

        Assert.Null(entry.Level);
        Assert.Null(entry.Time);
        Assert.Null(entry.Service);
        Assert.Equal(line, entry.Message);
        Assert.Equal(line, entry.Raw);
    }

    [Fact]
    public async Task Get_MixedLogfmtAndInvalidLines_BothProjectToTheSameElementType()
    {
        ResetState();
        await SeedServerLogAsync(
            "time=2026-06-30T12:00:01.000Z level=INFO msg=structured service=server component=logs",
            "not-json-blob",
            "time=2026-06-30T12:00:03.000Z level=WARN msg=structured service=server component=logs");

        var data = await GetTailAsync(_fixture.Client);
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);

        // Every element is the same shape — there are no raw strings
        // mixed into the list.
        foreach (var element in lines)
        {
            Assert.True(element.TryGetProperty("message", out _));
            Assert.True(element.TryGetProperty("raw", out _));
        }

        var structured1 = AssertEntry(lines[0]);
        Assert.Equal("INFO", structured1.Level);
        Assert.Equal("server", structured1.Service);
        Assert.Equal("structured", structured1.Message);
        Assert.NotNull(structured1.Time);

        var degraded = AssertEntry(lines[1]);
        Assert.Null(degraded.Level);
        Assert.Null(degraded.Time);
        Assert.Null(degraded.Service);
        Assert.Equal("not-json-blob", degraded.Message);
        Assert.Equal("not-json-blob", degraded.Raw);

        var structured2 = AssertEntry(lines[2]);
        Assert.Equal("WARN", structured2.Level);
        Assert.Equal("structured", structured2.Message);
    }

    [Fact]
    public async Task Get_LogfmtLine_RoundTripsWithoutFieldLoss()
    {
        var line = "time=2026-06-30T12:00:00.000Z level=INFO msg=\"logger-roundtrip 42\" service=server component=logs work=w_abc";
        Source.SetLines(line);

        var data = await GetTailAsync(_fixture.Client);
        var entry = AssertEntry(Assert.Single(data.GetProperty("lines").EnumerateArray()));

        Assert.Equal("INFO", entry.Level);
        Assert.Equal("server", entry.Service);
        Assert.Equal("logger-roundtrip 42", entry.Message);
        Assert.Equal(line, entry.Raw);
    }

    [Fact]
    public async Task Get_Source_ReflectsActiveLogFileName()
    {
        ResetState();
        // Drop a server.log with one record and verify source is the
        // file name (not the absolute path).
        var firstLine = "time=2026-06-30T12:00:00.000Z level=INFO msg=x service=server component=logs";
        await SeedServerLogAsync(firstLine);

        var data = await GetTailAsync(_fixture.Client);
        Assert.Equal(InMemoryLogTailSource.SourceName, data.GetProperty("source").GetString());
    }
}
