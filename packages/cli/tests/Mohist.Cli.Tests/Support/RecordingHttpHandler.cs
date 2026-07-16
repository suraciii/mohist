using System.Net;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli.Tests.Support;

public sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _requestWaiters = [];
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

    public Task WaitForRequestCountAsync(int count)
    {
        lock (_gate)
        {
            if (Requests.Count >= count)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _requestWaiters.Add((count, signal));
            return signal.Task;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new CapturedRequest
        {
            Method = request.Method,
            RequestUri = request.RequestUri,
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            Headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
        };
        lock (_gate)
        {
            Requests.Add(captured);
            foreach (var waiter in _requestWaiters.Where(waiter => Requests.Count >= waiter.Count))
                waiter.Signal.TrySetResult();
            _requestWaiters.RemoveAll(waiter => waiter.Signal.Task.IsCompleted);
        }
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
    public IReadOnlyDictionary<string, string[]> Headers { get; set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
}
