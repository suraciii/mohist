using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Contract specs for <c>GET /api/logs/tail</c>: the response shape, query
/// parameter validation (400 + error code), and the active file-name
/// passthrough. The tail calculation matrix (line-cap truncation, cursor,
/// rotation-shrink reset, max-bytes truncation, logfmt projection,
/// unavailable detection) lives in <c>LogTailReaderSpecs</c> against
/// <see cref="Mohist.Server.Logging.LogTailReader"/> directly.
/// </summary>
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
