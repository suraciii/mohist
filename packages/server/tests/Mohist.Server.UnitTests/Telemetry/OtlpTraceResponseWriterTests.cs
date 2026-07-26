using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class OtlpTraceResponseWriterTests
{
    [Fact]
    public async Task JsonSuccessUsesEmptyJsonRegardlessOfAccept()
    {
        var context = CreateContext();
        context.Request.Headers.Accept = "application/x-protobuf";

        await new OtlpTraceResponseWriter().WriteSuccessAsync(context.Response, "application/json");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("{}", ReadBody(context));
    }

    [Fact]
    public async Task ProtobufSuccessUsesZeroByteDefaultMessage()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteSuccessAsync(context.Response, "application/x-protobuf");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/x-protobuf", context.Response.ContentType);
        Assert.Empty(BodyBytes(context));
    }

    [Fact]
    public async Task JsonPartialSuccessUsesCanonicalNamesAndStringCount()
    {
        var context = CreateContext();
        var outcome = IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(0, 2, 1, 1),
            IngestWriteResult.NotAttempted());

        await new OtlpTraceResponseWriter().WriteOutcomeAsync(context.Response, "application/json", outcome);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("\"partialSuccess\"", ReadBody(context));
        Assert.Contains("\"rejectedSpans\":\"4\"", ReadBody(context));
        Assert.Contains("\"errorMessage\"", ReadBody(context));
    }

    [Fact]
    public async Task ProtobufStatusUsesGoogleRpcFields()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteInvalidArgumentAsync(
            context.Response, "application/x-protobuf", new string('x', 400));

        Assert.Equal(400, context.Response.StatusCode);
        using var input = new CodedInputStream(BodyBytes(context));
        Assert.Equal(8u, input.ReadTag());
        Assert.Equal(3, input.ReadInt32());
        Assert.Equal(18u, input.ReadTag());
        Assert.Equal(256, input.ReadString().Length);
        Assert.True(input.IsAtEnd);
    }

    [Fact]
    public async Task JsonDecodedSizeExceeded_UsesResourceExhausted_Status413()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteDecodedSizeExceededAsync(context.Response, "application/json");

        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        AssertDecodedSizeExceededJsonBody(context);
    }

    [Fact]
    public async Task ProtobufDecodedSizeExceeded_DecodesAsResourceExhaustedWithoutDetails()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteDecodedSizeExceededAsync(context.Response, "application/x-protobuf");

        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal("application/x-protobuf", context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        AssertDecodedSizeExceededProtobufBody(context);
    }

    [Fact]
    public async Task JsonTemporaryAdmission_UsesResourceExhausted_Status429_WithRetryAfter()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteTemporaryAdmissionAsync(context.Response, "application/json");

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        AssertTemporaryAdmissionJsonBody(context);
    }

    [Fact]
    public async Task ProtobufTemporaryAdmission_DecodesAsResourceExhaustedWithoutDetails()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteTemporaryAdmissionAsync(context.Response, "application/x-protobuf");

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("application/x-protobuf", context.Response.ContentType);
        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        AssertTemporaryAdmissionProtobufBody(context);
    }

    [Fact]
    public async Task ProtobufDecodedSizeExceeded_AcceptDoesNotChangeEncoding()
    {
        var context = CreateContext();
        context.Request.Headers.Accept = "application/json";

        await new OtlpTraceResponseWriter().WriteDecodedSizeExceededAsync(context.Response, "application/x-protobuf");

        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal("application/x-protobuf", context.Response.ContentType);
        AssertDecodedSizeExceededProtobufBody(context);
    }

    [Fact]
    public async Task TemporaryAdmission_RespectsRetryAfterOverride()
    {
        var context = CreateContext();

        await new OtlpTraceResponseWriter().WriteTemporaryAdmissionAsync(context.Response, "application/json", retryAfterSeconds: 42);

        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("42", context.Response.Headers.RetryAfter.ToString());
    }

    private static void AssertDecodedSizeExceededJsonBody(DefaultHttpContext context)
    {
        var body = ReadBody(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(8, document.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("Decoded telemetry request exceeds 16 MiB.", document.RootElement.GetProperty("message").GetString());
        Assert.False(document.RootElement.TryGetProperty("details", out _));
    }

    private static void AssertDecodedSizeExceededProtobufBody(DefaultHttpContext context)
    {
        using var input = new CodedInputStream(BodyBytes(context));
        Assert.Equal(8u, input.ReadTag());
        Assert.Equal(8, input.ReadInt32());
        Assert.Equal(18u, input.ReadTag());
        Assert.Equal("Decoded telemetry request exceeds 16 MiB.", input.ReadString());
        Assert.True(input.IsAtEnd);
    }

    private static void AssertTemporaryAdmissionJsonBody(DefaultHttpContext context)
    {
        var body = ReadBody(context);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(8, document.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("Telemetry receiver is at capacity.", document.RootElement.GetProperty("message").GetString());
        Assert.False(document.RootElement.TryGetProperty("details", out _));
    }

    private static void AssertTemporaryAdmissionProtobufBody(DefaultHttpContext context)
    {
        using var input = new CodedInputStream(BodyBytes(context));
        Assert.Equal(8u, input.ReadTag());
        Assert.Equal(8, input.ReadInt32());
        Assert.Equal(18u, input.ReadTag());
        Assert.Equal("Telemetry receiver is at capacity.", input.ReadString());
        Assert.True(input.IsAtEnd);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(DefaultHttpContext context) =>
        Encoding.UTF8.GetString(BodyBytes(context));

    private static byte[] BodyBytes(DefaultHttpContext context) =>
        ((MemoryStream)context.Response.Body).ToArray();
}
