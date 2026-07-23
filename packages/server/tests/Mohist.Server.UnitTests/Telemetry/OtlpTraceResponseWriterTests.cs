using System.Text;
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
