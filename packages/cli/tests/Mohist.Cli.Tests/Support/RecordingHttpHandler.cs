using System.Net;
using System.Net.Http.Headers;
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

    public static HttpResponseMessage MatchCompileError(
        string message,
        int offset,
        int line,
        int column,
        string source)
    {
        var body = new
        {
            success = false,
            error = message,
            code = "invalid_match_expression",
            details = new { offset, line, column, source },
        };
        return Json(body, HttpStatusCode.BadRequest);
    }

    public static HttpResponseMessage Ndjson(
        IAsyncEnumerable<string> lines,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string contentType = "application/x-ndjson")
    {
        var stream = new AsyncLineStream(lines);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(statusCode) { Content = content };
    }

    public static HttpResponseMessage Ndjson(IEnumerable<string> lines)
    {
        var queue = new AsyncLineQueue(lines);
        return Ndjson(queue.ReadAllAsync());
    }

    private sealed class AsyncLineQueue
    {
        private readonly Queue<string> _lines;

        public AsyncLineQueue(IEnumerable<string> lines)
        {
            _lines = new Queue<string>(lines);
        }

        public IAsyncEnumerable<string> ReadAllAsync()
        {
            return ReadAllInternal();
        }

        private async IAsyncEnumerable<string> ReadAllInternal()
        {
            while (_lines.Count > 0)
            {
                var line = _lines.Dequeue();
                yield return line;
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    private sealed class AsyncLineStream : Stream
    {
        private readonly IAsyncEnumerator<string> _enumerator;
        private byte[]? _buffer;
        private int _bufferPos;
        private int _bufferLen;

        public AsyncLineStream(IAsyncEnumerable<string> lines)
        {
            _enumerator = lines.GetAsyncEnumerator();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_buffer is null || _bufferPos >= _bufferLen)
            {
                if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
                    return 0;
                var line = _enumerator.Current;
                _buffer = Encoding.UTF8.GetBytes(line + "\n");
                _bufferPos = 0;
                _bufferLen = _buffer.Length;
            }

            var available = Math.Min(_bufferLen - _bufferPos, buffer.Length);
            _buffer.AsSpan(_bufferPos, available).CopyTo(buffer.Span);
            _bufferPos += available;
            return available;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
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