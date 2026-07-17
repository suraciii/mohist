namespace Mohist.Cli.Tests.Compatibility;

internal sealed class RejectingHttpMessageHandler : HttpMessageHandler
{
    public static HttpClient CreateClient() => new(new RejectingHttpMessageHandler())
    {
        BaseAddress = new Uri("http://localhost:3456"),
    };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"Unexpected HTTP request: {request.Method} {request.RequestUri}");
}
