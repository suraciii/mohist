namespace Mohist.Server.UnitTests.Support;

internal sealed class RejectingHttpMessageHandler : HttpMessageHandler
{
    public static HttpClient CreateClient() => new(new RejectingHttpMessageHandler());

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"Unexpected HTTP request: {request.Method} {request.RequestUri}");
}
