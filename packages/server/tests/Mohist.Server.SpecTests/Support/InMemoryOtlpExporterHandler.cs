using System.Net;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Test-only OTLP transport. Exporter requests stay inside the test process;
/// no collector or host port is contacted when telemetry is enabled.
/// </summary>
public sealed class InMemoryOtlpExporterHandler : HttpMessageHandler
{
    public int RequestCount => Volatile.Read(ref _requestCount);

    private int _requestCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _requestCount);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(Array.Empty<byte>()),
        });
    }
}
