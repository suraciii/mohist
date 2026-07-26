using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

[Collection("IntegrationTelemetry")]
public class OtlpRoutesBoundedIngressSpecs : IAsyncLifetime
{
    private const string OtlpPath = "/otel/v1/traces";

    private readonly OtlpRoutesHostFixture _fixture;
    private OtlpRoutesWebApplicationFactory _factory => _fixture.Factory;

    public OtlpRoutesBoundedIngressSpecs(OtlpRoutesHostFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetOtelStateAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task OverLimitJsonRequest_Returns413_ResourceExhausted_JsonEncoding_NoRows()
    {
        var seam = _factory.Services.GetRequiredService<IOtlpIngestGateTestSeam>();
        Assert.True(seam.BlockNextRequestLease());

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildLargeJsonPayload(LimitedOtlpBodyReader.DefaultMaxBytes + 1), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        seam.ReleaseNextRequestSignal();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertRetryAfter(response, expected: null);
        await AssertResourceExhaustedJsonAsync(response, OtlpTraceResponseWriter.DecodedSizeExceededMessage);
        await AssertNoPersistedRowsAsync();
    }

    [Fact]
    public async Task OverLimitProtobufRequest_Returns413_ResourceExhausted_ProtobufEncoding_NoRows()
    {
        var seam = _factory.Services.GetRequiredService<IOtlpIngestGateTestSeam>();
        Assert.True(seam.BlockNextRequestLease());

        using var client = _factory.CreateOtlpClient();
        using var content = new ByteArrayContent(BuildLargeProtobufPayload(LimitedOtlpBodyReader.DefaultMaxBytes + 256));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        using var response = await client.PostAsync(OtlpPath, content);

        seam.ReleaseNextRequestSignal();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        AssertRetryAfter(response, expected: null);
        await AssertResourceExhaustedProtobufAsync(response, OtlpTraceResponseWriter.DecodedSizeExceededMessage);
        await AssertNoPersistedRowsAsync();
    }

    [Fact]
    public async Task BodyExceedsLimitButSignalFiresFourthAdmit_AcceptsThen413()
    {
        var seam = _factory.Services.GetRequiredService<IOtlpIngestGateTestSeam>();
        Assert.True(seam.BlockNextRequestLease());

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildLargeJsonPayload(LimitedOtlpBodyReader.DefaultMaxBytes + 1), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        seam.ReleaseNextRequestSignal();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertNoPersistedRowsAsync();
    }

    [Fact]
    public async Task FiveRequests_AllAdmitted_BeyondLimit_FifthReceivesJson429_RetryAfterOne()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertRetryAfter(response, "1");
        await AssertResourceExhaustedJsonAsync(response, OtlpTraceResponseWriter.TemporaryAdmissionMessage);

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();
    }

    [Fact]
    public async Task FiveRequests_AllAdmitted_BeyondLimit_FifthProtobuf429_RetryAfterOne()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);

        using var client = _factory.CreateOtlpClient();
        var payload = BuildMinimalProtobufTracePayload("00000000000000000000000000000099", "0000000000000099", "over-proto", "sp");
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        AssertRetryAfter(response, "1");
        await AssertResourceExhaustedProtobufAsync(response, OtlpTraceResponseWriter.TemporaryAdmissionMessage);

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();
    }

    [Fact]
    public async Task FifthRequest_RejectedWithoutReadingBody()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        var runtime = _factory.Services.GetRequiredService<RuntimeObservability>();
        var receivedBefore = runtime.GetSnapshot().Telemetry.ReceivedSpans;

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var after = runtime.GetSnapshot().Telemetry;
        Assert.Equal(receivedBefore, after.ReceivedSpans);
        Assert.Equal(0, after.SavedSpans);
        Assert.Equal(0, after.RejectedSpans);

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();
    }

    [Fact]
    public async Task OverLimitRejected_DoesNotPublishRuntimeObservabilityOutcome()
    {
        var runtime = _factory.Services.GetRequiredService<RuntimeObservability>();
        var receivedBefore = runtime.GetSnapshot().Telemetry.ReceivedSpans;
        var savedBefore = runtime.GetSnapshot().Telemetry.SavedSpans;
        var rejectedBefore = runtime.GetSnapshot().Telemetry.RejectedSpans;
        var droppedBefore = runtime.GetSnapshot().Telemetry.DroppedSpans;

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildLargeJsonPayload(LimitedOtlpBodyReader.DefaultMaxBytes + 1), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var after = runtime.GetSnapshot().Telemetry;
        Assert.Equal(receivedBefore, after.ReceivedSpans);
        Assert.Equal(savedBefore, after.SavedSpans);
        Assert.Equal(rejectedBefore, after.RejectedSpans);
        Assert.Equal(droppedBefore, after.DroppedSpans);
    }

    [Fact]
    public async Task FiveRequests_OverLimit_DoesNotPersistRows()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();
        await AssertNoPersistedRowsAsync();
    }

    [Fact]
    public async Task MalformedJson_AfterAdmission_ReturnsJson400_Not413()
    {
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("not json {", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(3, document.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task UnsupportedMedia_ReturnsJson415_NotAffectedByGate()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("ignored", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(OtlpIngestGate.RequestLeaseLimit, gate.RequestLeasesInUse);
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();
    }

    [Fact]
    public async Task SuccessfulJsonRequest_RetainsWireContract()
    {
        const string payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"bounded-ok"}}]},
                "scopeSpans": [{"spans":[{
                  "traceId":"000000000000000000000000000000a1",
                  "spanId":"00000000000000a1","name":"a",
                  "startTimeUnixNano":"1767225600000000000",
                  "endTimeUnixNano":"1767225601000000000"
                }]}]
              }]
            }
            """;
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SuccessfulProtobufRequest_RetainsWireContract()
    {
        var traceId = "000000000000000000000000000000a2";
        var payload = BuildMinimalProtobufTracePayload(traceId, "00000000000000a2", "bounded-proto-ok", "sp");
        Assert.NotEmpty(payload);
        using var client = _factory.CreateOtlpClient();
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task LeaseReleasedOnResponse_AllowsNewAdmission()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, gate.RequestLeasesInUse);
    }

    [Fact]
    public async Task FourthAdmit_ProvisionalSixthRefusedBeforeBodyRead()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildLargeJsonPayload(LimitedOtlpBodyReader.DefaultMaxBytes + 16), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        AssertRetryAfter(response, "1");
        await AssertResourceExhaustedJsonAsync(response, OtlpTraceResponseWriter.TemporaryAdmissionMessage);

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();
    }

    [Fact]
    public async Task AcceptedAndReleaseOnException_LetsThroughNewRequest()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        var decision = gate.TryAcquireRequestLease();
        Assert.True(decision.Admitted);

        var cts = new CancellationTokenSource();
        cts.Cancel();
        gate.ReleaseRequestLease();

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AssertRetryAfter(HttpResponseMessage response, string? expected)
    {
        if (expected is null)
        {
            Assert.False(response.Headers.Contains("Retry-After"));
        }
        else
        {
            Assert.True(response.Headers.Contains("Retry-After"));
            Assert.Equal(expected, response.Headers.GetValues("Retry-After").Single());
        }
    }

    private async Task AssertResourceExhaustedJsonAsync(HttpResponseMessage response, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(8, document.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
        Assert.False(document.RootElement.TryGetProperty("details", out _));
    }

    private async Task AssertResourceExhaustedProtobufAsync(HttpResponseMessage response, string message)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var input = new CodedInputStream(bytes);
        Assert.Equal(8u, input.ReadTag());
        Assert.Equal(8, input.ReadInt32());
        Assert.Equal(18u, input.ReadTag());
        Assert.Equal(message, input.ReadString());
        Assert.True(input.IsAtEnd);
    }

    private async Task AssertNoPersistedRowsAsync()
    {
        var db = _factory.Services.GetRequiredService<OtelDb>();
        await using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable};";
        var traces = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable};";
        var spans = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        Assert.Equal(0, traces);
        Assert.Equal(0, spans);
    }

    private static string BuildLargeJsonPayload(int approxSize)
    {
        var builder = new StringBuilder(approxSize + 256);
        builder.Append("{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"oversize\"}}]},\"scopeSpans\":[{\"spans\":[");
        builder.Append("{\"traceId\":\"000000000000000000000000000000f0\",\"spanId\":\"00000000000000f0\",\"name\":\"big\",\"startTimeUnixNano\":\"1767225600000000000\",\"endTimeUnixNano\":\"1767225601000000000\",\"attributes\":[{\"key\":\"payload\",\"value\":{\"stringValue\":\"");
        var padTarget = approxSize - builder.Length - 2;
        if (padTarget > 0)
        {
            builder.Append('x', padTarget);
        }
        builder.Append("\"}}]}]}]}");
        return builder.ToString();
    }

    private static byte[] BuildLargeProtobufPayload(int approxSize)
    {
        var payload = "x";
        while (Encoding.UTF8.GetByteCount(payload) < approxSize - 1024)
            payload += payload;
        const string traceId = "000000000000000000000000000000f1";
        const string spanId = "00000000000000f1";
        return BuildMinimalProtobufTracePayload(traceId, spanId, "oversize-proto", payload);
    }

    private static byte[] BuildMinimalProtobufTracePayload(string traceId, string spanId, string serviceName, string spanName)
    {
        var resourceAttribute = Message(w =>
        {
            w.WriteRawTag(10); w.WriteString("service.name");
            w.WriteRawTag(18); w.WriteBytes(Message(v => { v.WriteRawTag(10); v.WriteString(serviceName); }));
        });
        var resource = Message(w => { w.WriteRawTag(10); w.WriteBytes(resourceAttribute); });
        var span = Message(w =>
        {
            w.WriteRawTag(10); w.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(traceId)));
            w.WriteRawTag(18); w.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(spanId)));
            w.WriteRawTag(42); w.WriteString(spanName);
            w.WriteRawTag(48); w.WriteEnum(1);
            w.WriteRawTag(57); w.WriteFixed64(1767225600000000000UL);
            w.WriteRawTag(65); w.WriteFixed64(1767225601000000000UL);
        });
        var scopeSpans = Message(w => { w.WriteRawTag(18); w.WriteBytes(span); });
        var resourceSpans = Message(w =>
        {
            w.WriteRawTag(10); w.WriteBytes(resource);
            w.WriteRawTag(18); w.WriteBytes(scopeSpans);
        });
        return Message(w => { w.WriteRawTag(10); w.WriteBytes(resourceSpans); }).ToByteArray();
    }

    private static ByteString Message(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        write(output);
        output.Flush();
        return ByteString.CopyFrom(stream.ToArray());
    }
}
