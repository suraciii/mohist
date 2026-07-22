using System.Net;
using System.Net.Http;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class MohistCliApiSendAsyncSpecs
{
    private static (MohistCliApi Api, RecordingHttpHandler Handler) CreateApi(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
        string? activeProjectId = "proj_abc")
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        var api = new MohistCliApi(http, output, error, fs, executor);
        return (api, handler);
    }

    private static Task<HttpResponseMessage> ThrowingOffline(HttpRequestMessage _, CancellationToken __) =>
        throw new HttpRequestException("connection refused");

    [Fact]
    public async Task PrintGetAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPostAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintPostAsync("/api/anything", new { });

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPutAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintPutAsync("/api/anything", new { });

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPatchAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintPatchAsync("/api/anything", new { });

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintDeleteAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintDeleteAsync("/api/anything");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintWithOutputAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintWithOutputAsync("/api/anything", "json");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPostWithOutputAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintPostWithOutputAsync("/api/anything", new { }, "json");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPutWithOutputAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintPutWithOutputAsync("/api/anything", new { }, "json");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPatchWithOutputAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintPatchWithOutputAsync("/api/anything", new { }, "json");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintDeleteWithOutputAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.PrintDeleteWithOutputAsync("/api/anything", "json");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task GetDataOrPrintErrorAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var (exit, data) = await api.GetDataOrPrintErrorAsync("/api/anything");

        Assert.Equal(1, exit);
        Assert.Null(data);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task GetDataAsync_ServerUnreachable_PreservesThrowingReaderContract()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await api.GetDataAsync("/api/anything"));

        Assert.DoesNotContain(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PostDataAsync_ServerUnreachable_PreservesThrowingReaderContract()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await api.PostDataAsync("/api/anything", new { }));

        Assert.DoesNotContain(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task UseProjectAsync_ServerUnreachable_WritesServerUnavailableMessageAndExitsOne()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        var exit = await api.UseProjectAsync("proj_abc");

        Assert.Equal(1, exit);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintGetAsync_ServerReachable_ExitsZeroOnSuccessEnvelope()
    {
        var (api, _) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "x" } })));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
        Assert.Contains("\"id\":", api.Output.ToString());
    }

    [Fact]
    public async Task PrintPostAsync_ServerReachable_SerializesBodyAndExitsZero()
    {
        var (api, handler) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exit = await api.PrintPostAsync("/api/anything", new { name = "abc" });

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("\"name\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task PrintPatchAsync_ServerReachable_SerializesBodyAndExitsZero()
    {
        var (api, handler) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exit = await api.PrintPatchAsync("/api/anything", new { name = "abc" });

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("\"name\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task PrintDeleteAsync_ServerReachable_ExitsZero()
    {
        var (api, handler) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exit = await api.PrintDeleteAsync("/api/anything");

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
    }

    [Fact]
    public async Task PrintPutAsync_ServerReachable_SerializesBodyAndExitsZero()
    {
        var (api, handler) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exit = await api.PrintPutAsync("/api/anything", new { name = "abc" });

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("\"name\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task PrintGetAsync_ServerUnreachable_DoesNotWriteAnythingToStdout()
    {
        var (api, _) = CreateApi(ThrowingOffline);

        await api.PrintGetAsync("/api/anything");

        Assert.Equal(string.Empty, api.Output.ToString());
    }

    [Fact]
    public async Task PrintPostAsync_NotFoundEnvelope_ExitsFour()
    {
        var (api, _) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError("Missing", "not_found", HttpStatusCode.NotFound)));

        var exit = await api.PrintPostAsync("/api/anything", new { });

        Assert.Equal(1, exit);
        Assert.Contains("Missing", api.Error.ToString());
        Assert.Contains("not_found", api.Error.ToString());
    }

    [Fact]
    public async Task PrintDeleteAsync_BadRequestEnvelope_ExitsOne()
    {
        var (api, _) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError("Validation failed", "validation_error", HttpStatusCode.BadRequest)));

        var exit = await api.PrintDeleteAsync("/api/anything");

        Assert.Equal(1, exit);
        Assert.Contains("Validation failed", api.Error.ToString());
        Assert.Contains("validation_error", api.Error.ToString());
    }

    [Fact]
    public async Task PrintWithOutputAsync_JsonMode_ServerReachable_PrintsJson()
    {
        var (api, _) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "x" } })));

        var exit = await api.PrintWithOutputAsync("/api/anything", "json");

        Assert.Equal(0, exit);
        Assert.Contains("\"id\":", api.Output.ToString());
        Assert.DoesNotContain(MohistCliApi.ServerUnavailableMessage, api.Error.ToString());
    }

    [Fact]
    public async Task PrintPatchWithOutputAsync_JsonMode_ServerReachable_PrintsJson()
    {
        var (api, handler) = CreateApi((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exit = await api.PrintPatchWithOutputAsync("/api/anything", new { k = "v" }, "json");

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("\"k\"", handler.Requests[0].Body);
    }
}
