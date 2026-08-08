using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class MohistCliApiEnvelopeSpecs
{
    private static (MohistCliApi Api, RecordingHttpHandler Handler) CreateApi(
        string? activeProjectId = "proj_abc")
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: activeProjectId);
        var api = new MohistCliApi(http, output, error, fs, executor);
        return (api, handler);
    }

    [Fact]
    public async Task PrintGet_Unauthorized401_AndForbidden403_PromptsAreDistinguishable()
    {
        var (unauthorizedApi, unauthorizedHandler) = CreateApi();
        unauthorizedHandler.SetResponder((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Unauthorized,
            """{"success":false,"error":"Authentication required.","code":"unauthorized"}""")));
        var unauthorizedExit = await unauthorizedApi.PrintGetAsync("/api/fs/home");
        Assert.Contains("code=unauthorized", unauthorizedApi.Error.ToString());
        Assert.DoesNotContain("code=forbidden", unauthorizedApi.Error.ToString());

        var (forbiddenApi, forbiddenHandler) = CreateApi();
        forbiddenHandler.SetResponder((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Forbidden,
            """{"success":false,"error":"Insufficient scope.","code":"forbidden"}""")));
        var forbiddenExit = await forbiddenApi.PrintGetAsync("/api/fs/home");
        Assert.Contains("code=forbidden", forbiddenApi.Error.ToString());
        Assert.DoesNotContain("code=unauthorized", forbiddenApi.Error.ToString());

        Assert.Equal(unauthorizedExit, forbiddenExit);
    }

    private static HttpResponseMessage EmptyResponse(HttpStatusCode status, string? reason = null)
    {
        var response = new HttpResponseMessage(status);
        if (reason is not null)
            response.ReasonPhrase = reason;
        return response;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public void ExtractEnvelope_NullNodeOnSuccessStatus_TreatsAsSuccessWithoutBody()
    {
        using var response = EmptyResponse(HttpStatusCode.OK);

        var envelope = MohistCliApi.ExtractEnvelope(null, response);

        Assert.False(envelope.HasBody);
        Assert.True(envelope.Success);
        Assert.Null(envelope.Data);
        Assert.Equal("OK", envelope.Error);
        Assert.Null(envelope.Code);
    }

    [Fact]
    public void ExtractEnvelope_NullNodeOnFailureStatus_TreatsAsFailureWithoutBody()
    {
        using var response = EmptyResponse(HttpStatusCode.BadRequest, "Bad Request");

        var envelope = MohistCliApi.ExtractEnvelope(null, response);

        Assert.False(envelope.HasBody);
        Assert.False(envelope.Success);
        Assert.Null(envelope.Data);
        Assert.Equal("Bad Request", envelope.Error);
    }

    [Fact]
    public void ExtractEnvelope_NodeWithSuccessTrue_PreservesData()
    {
        using var response = EmptyResponse(HttpStatusCode.OK);
        var node = new JsonObject
        {
            ["success"] = true,
            ["data"] = new JsonObject { ["id"] = "abc" },
        };

        var envelope = MohistCliApi.ExtractEnvelope(node, response);

        Assert.True(envelope.HasBody);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.Equal("abc", envelope.Data!["id"]?.GetValue<string>());
    }

    [Fact]
    public void ExtractEnvelope_NodeWithSuccessFalse_ExposesErrorAndCode()
    {
        using var response = EmptyResponse(HttpStatusCode.BadRequest, "Bad Request");
        var node = new JsonObject
        {
            ["success"] = false,
            ["error"] = "Validation failed",
            ["code"] = "validation_error",
        };

        var envelope = MohistCliApi.ExtractEnvelope(node, response);

        Assert.True(envelope.HasBody);
        Assert.False(envelope.Success);
        Assert.Equal("Validation failed", envelope.Error);
        Assert.Equal("validation_error", envelope.Code);
    }

    [Fact]
    public void ExtractEnvelope_NodeWithoutSuccessFieldOnSuccessStatus_TreatsAsSuccess()
    {
        using var response = EmptyResponse(HttpStatusCode.OK);
        var node = new JsonObject
        {
            ["data"] = new JsonObject { ["id"] = "abc" },
        };

        var envelope = MohistCliApi.ExtractEnvelope(node, response);

        Assert.True(envelope.HasBody);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
    }

    [Fact]
    public void ExtractEnvelope_NodeWithoutSuccessFieldOnFailureStatus_TreatsAsFailure()
    {
        using var response = EmptyResponse(HttpStatusCode.InternalServerError, "Internal Server Error");
        var node = new JsonObject
        {
            ["error"] = "Boom",
        };

        var envelope = MohistCliApi.ExtractEnvelope(node, response);

        Assert.True(envelope.HasBody);
        Assert.False(envelope.Success);
        Assert.Equal("Boom", envelope.Error);
    }

    [Fact]
    public void FailureExitCode_NotFoundStatus_ReturnsFour()
    {
        using var response = EmptyResponse(HttpStatusCode.NotFound);

        Assert.Equal(1, MohistCliApi.FailureExitCode(response));
    }

    [Fact]
    public void FailureExitCode_BadRequestStatus_ReturnsOne()
    {
        using var response = EmptyResponse(HttpStatusCode.BadRequest);

        Assert.Equal(1, MohistCliApi.FailureExitCode(response));
    }

    [Fact]
    public void FailureExitCode_StatusCodeOverload_NotFoundStatus_ReturnsFour()
    {
        Assert.Equal(1, MohistCliApi.FailureExitCode(HttpStatusCode.NotFound));
    }

    [Fact]
    public void FailureExitCode_StatusCodeOverload_BadRequestStatus_ReturnsOne()
    {
        Assert.Equal(1, MohistCliApi.FailureExitCode(HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task PrintGet_SuccessEnvelopeOnNotFoundStatus_NoSuccessField_TreatsAsFailureAndExits4()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.NotFound, "{\"error\":\"Not here\"}")));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(1, exit);
        Assert.Contains("Not here", api.Error.ToString());
    }

    [Fact]
    public async Task PrintGet_BodyWithoutSuccessFieldOn2xx_TreatsAsSuccessAndExits0()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"data\":{\"id\":\"x\"}}")));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(0, exit);
        var stdout = api.Output.ToString();
        Assert.Contains("\"id\":", stdout);
        Assert.Contains("\"x\"", stdout);
    }

    [Fact]
    public async Task PrintGet_EmptyBodyOnSuccess_PrintsStatusCodeAndExits0()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(EmptyResponse(HttpStatusCode.NoContent, "No Content")));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(0, exit);
        var stdout = api.Output.ToString();
        Assert.Contains(HttpStatusCode.NoContent.ToString(), stdout);
    }

    [Fact]
    public async Task PrintGet_EmptyBodyOnFailure_PrintsStatusCodeAndExits1()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(EmptyResponse(HttpStatusCode.BadRequest)));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(1, exit);
        var stdout = api.Output.ToString();
        Assert.Contains(HttpStatusCode.BadRequest.ToString(), stdout);
    }

    [Fact]
    public async Task PrintGet_ErrorEnvelopeWithCode_PrintsErrorAndCode()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(
                HttpStatusCode.BadRequest,
                "{\"success\":false,\"error\":\"Validation failed\",\"code\":\"validation_error\"}")));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(1, exit);
        var stderr = api.Error.ToString();
        Assert.Contains("Validation failed", stderr);
        Assert.Contains("validation_error", stderr);
    }

    [Fact]
    public async Task PrintGet_NotFoundEnvelope_Exits4()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(
                HttpStatusCode.NotFound,
                "{\"success\":false,\"error\":\"Not found\",\"code\":\"missing\"}")));

        var exit = await api.PrintGetAsync("/api/anything");

        Assert.Equal(1, exit);
        var stderr = api.Error.ToString();
        Assert.Contains("Not found", stderr);
        Assert.Contains("missing", stderr);
    }

    [Fact]
    public async Task PostAndRead_SuccessEnvelope_ReturnsZeroAndData()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"data\":{\"id\":\"x\"}}")));

        var result = await api.PostAndReadAsync("/api/anything", new { });

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Data);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task PostAndRead_NotFoundEnvelope_ReturnsFourAndCapturesError()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(
                HttpStatusCode.NotFound,
                "{\"success\":false,\"error\":\"Missing\",\"code\":\"not_found\"}")));

        var result = await api.PostAndReadAsync("/api/anything", new { });

        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.Data);
        Assert.Equal("Missing", result.Error);
        Assert.Equal("not_found", result.Code);
    }

    [Fact]
    public async Task GetData_SuccessEnvelope_ReturnsData()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"data\":{\"id\":\"x\"}}")));

        var data = await api.GetDataAsync("/api/anything");

        Assert.NotNull(data);
        Assert.Equal("x", data!["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetData_NotFoundEnvelope_ThrowsWithErrorAndCode()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(JsonResponse(
                HttpStatusCode.NotFound,
                "{\"success\":false,\"error\":\"Missing\",\"code\":\"not_found\"}")));

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await api.GetDataAsync("/api/anything"));
        Assert.Equal("Missing", ex.Message);
    }

    [Fact]
    public async Task GetData_EmptyBodyOnSuccess_ThrowsWithReasonPhrase()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(EmptyResponse(HttpStatusCode.NoContent)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await api.GetDataAsync("/api/anything"));

        Assert.Equal("No Content", ex.Message);
    }

    [Fact]
    public async Task GetDataOrPrintError_EmptyBodyOnSuccess_PrintsReasonPhraseAndExitsOne()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(EmptyResponse(HttpStatusCode.NoContent, "No Content")));

        var (exitCode, data) = await api.GetDataOrPrintErrorAsync("/api/anything");

        Assert.Equal(1, exitCode);
        Assert.Null(data);
        Assert.Contains("No Content", api.Error.ToString());
    }

    [Fact]
    public async Task GetDataOrPrintError_EmptyBodyOnFailure_PrintsReasonPhraseAndExitsOne()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(EmptyResponse(HttpStatusCode.BadRequest, "Bad Request")));

        var (exitCode, data) = await api.GetDataOrPrintErrorAsync("/api/anything");

        Assert.Equal(1, exitCode);
        Assert.Null(data);
        Assert.Contains("Bad Request", api.Error.ToString());
    }

    [Fact]
    public async Task PostData_EmptyBodyOnSuccess_ThrowsWithReasonPhrase()
    {
        var (api, handler) = CreateApi();
        handler.SetResponder((_, _) =>
            Task.FromResult(EmptyResponse(HttpStatusCode.NoContent, "No Content")));

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await api.PostDataAsync("/api/anything", new { }));

        Assert.Equal("No Content", ex.Message);
    }
}
