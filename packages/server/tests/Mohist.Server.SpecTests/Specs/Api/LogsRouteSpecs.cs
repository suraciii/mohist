using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Api;
using Mohist.Server.Logging;
using Mohist.Server.SpecTests.Support;
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

    private string LogFilePath => Path.Combine(_fixture.LogsPath, FileLoggerProvider.LogFileName);

    /// <summary>
    /// Removes any existing log directory so the next test starts from
    /// a known missing state. The fixture's per-run <c>Mohist:LogsPath</c>
    /// is shared across all tests in the collection, so each test must
    /// arrange its own state.
    /// </summary>
    private void ResetState()
    {
        if (Directory.Exists(_fixture.LogsPath))
        {
            Directory.Delete(_fixture.LogsPath, recursive: true);
        }
    }

    private async Task SeedServerLogAsync(params string[] lines)
    {
        Directory.CreateDirectory(_fixture.LogsPath);
        // UTF-8 without BOM so byte offsets match the cursor math
        // (BOMs would add 3 unaccounted bytes at the file head).
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllLinesAsync(LogFilePath, lines, encoding);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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
        Assert.Equal(LogFilePath, expectedLocation);

        var reason = data.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("does not exist", reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_WhenLogDirectoryExistsButServerLogMissing_ReturnsUnavailableWithReason()
    {
        ResetState();
        // server.log intentionally absent
        Directory.CreateDirectory(_fixture.LogsPath);

        var data = await GetTailAsync(_fixture.Client);

        Assert.True(data.GetProperty("unavailable").GetBoolean());
        Assert.Equal(LogFilePath, data.GetProperty("expectedLocation").GetString());
        var reason = data.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("is missing", reason);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("source").ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_OnFirstRead_AlwaysCarriesTheAgreedResponseShape()
    {
        ResetState();
        var line = """{"level":"INFO","time":"2026-06-30T12:00:00.0000000+00:00","service":"Mohist.Server","message":"hello"}""";
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
        Assert.Equal(FileLoggerProvider.LogFileName, data.GetProperty("source").GetString());

        // cursor/nextCursor remain the EOF byte offset so auto-follow can
        // poll from the end without replaying the file.
        var eofCursor = data.GetProperty("cursor").GetInt64();
        Assert.True(eofCursor > 0);
        Assert.Equal(eofCursor, data.GetProperty("nextCursor").GetInt64());

        // expectedLocation/reason are null in the available path.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("expectedLocation").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("reason").ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_WhenLineCapReachedBeforeEof_ReportsTruncatedTrueAndAdvancesCursor()
    {
        ResetState();
        var lines = Enumerable.Range(1, 5)
            .Select(i => $$"""{"level":"INFO","time":"2026-06-30T12:00:0{{i}}.0000000+00:00","service":"Mohist.Server","message":"line {{i}}"}""")
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_WhenFileShrinksBelowCursor_ReportsResetTrue()
    {
        ResetState();
        var lines = Enumerable.Range(1, 5)
            .Select(i => $$"""{"level":"INFO","time":"2026-06-30T12:00:0{{i}}.0000000+00:00","service":"Mohist.Server","message":"line {{i}}"}""")
            .ToArray();
        await SeedServerLogAsync(lines);

        var fileLength = new FileInfo(LogFilePath).Length;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_AvailableButNoNewLinesSinceCursor_IsNotReportedAsUnavailable()
    {
        ResetState();
        var lines = Enumerable.Range(1, 3)
            .Select(i => $$"""{"level":"INFO","time":"2026-06-30T12:00:0{{i}}.0000000+00:00","service":"Mohist.Server","message":"line {{i}}"}""")
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_AutoFollowFromEof_DoesNotReplayAndReturnsOnlyAppendedLines()
    {
        ResetState();
        var initialLine = """{"level":"INFO","time":"2026-06-30T12:00:01.0000000+00:00","service":"Mohist.Server","message":"initial"}""";
        var appendedLine = """{"level":"WARN","time":"2026-06-30T12:00:02.0000000+00:00","service":"Mohist.Server","message":"appended"}""";
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

        await File.AppendAllLinesAsync(
            LogFilePath,
            new[] { appendedLine },
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var afterAppend = await GetTailAsync(_fixture.Client, $"?cursor={eofCursor}&limit=10");
        Assert.False(afterAppend.GetProperty("reset").GetBoolean());
        var returnedLines = afterAppend.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(returnedLines);
        Assert.Equal("appended", returnedLines[0].GetProperty("message").GetString());
        Assert.Equal("WARN", returnedLines[0].GetProperty("level").GetString());
        Assert.True(afterAppend.GetProperty("nextCursor").GetInt64() > eofCursor);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_WhenSinglePhysicalLineExceedsMaxBytes_DoesNotReturnOversizedLineAndAdvancesCursor()
    {
        ResetState();
        var oversizedLine = new string('x', 4096);
        var followingLine = """{"level":"INFO","time":"2026-06-30T12:00:01.0000000+00:00","service":"Mohist.Server","message":"after oversized"}""";
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"raw\":\"x\"}")]
    public async Task Get_ValidJsonThatDoesNotMatchLogRecord_DegradesToRawElement(string line)
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_MixedJsonAndNonJsonLines_BothProjectToTheSameElementType()
    {
        ResetState();
        await SeedServerLogAsync(
            """{"level":"INFO","time":"2026-06-30T12:00:01.0000000+00:00","service":"Mohist.Server","message":"structured"}""",
            "not-json-blob",
            """{"level":"WARN","time":"2026-06-30T12:00:03.0000000+00:00","service":"Mohist.Server","message":"also structured"}""");

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
        Assert.Equal("Mohist.Server", structured1.Service);
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
        Assert.Equal("also structured", structured2.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_RecordWrittenByFileLoggerProvider_RoundTripsThroughLogEntryProjectionWithoutFieldLoss()
    {
        ResetState();

        // Drive the FileLoggerProvider end-to-end against a fresh
        // temp directory and stand up an ILogPathResolver pointed at
        // it, then point a brand-new FileLoggerProvider at the same
        // path so its writes are what the tail reads. The route is
        // wired to the singleton ILogPathResolver (fixture's logs
        // path), so we copy the produced file into that location
        // before reading through the API. This isolates the test
        // from the singleton provider's file handle, which may be
        // stale from earlier tests in the collection that delete the
        // logs directory.
        var tempDir = Path.Combine(Path.GetTempPath(), $"mohist-logs-provider-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LogPathResolver.ConfigurationKey] = tempDir,
            })
            .Build();
        ILogPathResolver resolver = new LogPathResolver(configuration, new MockEnvironmentVariableProvider());
        using (var freshProvider = new FileLoggerProvider(resolver, _fixture.TimeProvider))
        {
            var freshLogger = freshProvider.CreateLogger("Mohist.Server.Workflow.Grains");
            const string probe = "logger-roundtrip 42";
            freshLogger.LogInformation(probe);

            // Copy the fresh file into the fixture's logs directory as
            // `server.log` so the tail endpoint reads it through its
            // own (singleton) ILogPathResolver.
            Directory.CreateDirectory(_fixture.LogsPath);
            File.Copy(freshProvider.LogFilePath, LogFilePath, overwrite: true);
        }

        var data = await GetTailAsync(_fixture.Client);
        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        var entry = AssertEntry(lines[0]);

        Assert.Equal("INFO", entry.Level);
        Assert.Equal("Mohist.Server", entry.Service);
        Assert.Equal("logger-roundtrip 42", entry.Message);
        Assert.NotNull(entry.Time);
        Assert.NotEmpty(entry.Raw);

        // The raw on the wire must parse back to a LogRecord with the
        // same fields — proves the on-disk format and the wire format
        // share JSON.Options and the round-trip is lossless.
        var parsedBack = JsonSerializer.Deserialize<LogRecord>(entry.Raw, Mohist.Server.Infrastructure.JSON.Options);
        Assert.NotNull(parsedBack);
        Assert.Equal("INFO", parsedBack!.Level);
        Assert.Equal("Mohist.Server", parsedBack.Service);
        Assert.Equal("logger-roundtrip 42", parsedBack.Message);

        // Cleanup the temp dir we created.
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Get_Source_ReflectsActiveLogFileName()
    {
        ResetState();
        // Drop a server.log with one record and verify source is the
        // file name (not the absolute path).
        var firstLine = """{"level":"INFO","time":"2026-06-30T12:00:00.0000000+00:00","service":"Mohist.Server","message":"x"}""";
        await SeedServerLogAsync(firstLine);

        var data = await GetTailAsync(_fixture.Client);
        Assert.Equal(FileLoggerProvider.LogFileName, data.GetProperty("source").GetString());
    }
}
