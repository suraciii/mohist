using System.Net;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli.TestSupport;

public sealed class RecordingHttpHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public RecordingHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public List<CapturedRequest> Requests { get; } = [];

    public void SetResponder(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new CapturedRequest
        {
            Method = request.Method,
            RequestUri = request.RequestUri,
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
        };
        Requests.Add(captured);
        return await _responder(request, cancellationToken);
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        return response;
    }

    public static HttpResponseMessage JsonError(string error, string? code = null, HttpStatusCode statusCode = HttpStatusCode.BadRequest)
    {
        return Json(new { success = false, error, code }, statusCode);
    }
}

public sealed class CapturedRequest
{
    public HttpMethod Method { get; set; } = null!;
    public Uri? RequestUri { get; set; }
    public string? Body { get; set; }
}
