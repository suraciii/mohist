using System.Net.WebSockets;
using System.Text;

namespace Mohist.Cli.Tests.Support;

internal sealed class FakeEventSocketFactory : IEventSocketFactory
{
    private readonly Queue<FakeEventSocket> _sockets = new();

    public List<string?> BearerTokens { get; } = [];
    public List<Uri> Endpoints { get; } = [];
    public IReadOnlyCollection<FakeEventSocket> Remaining => _sockets;

    public void Add(FakeEventSocket socket) => _sockets.Enqueue(socket);

    public IEventSocket Create(string? bearerToken)
    {
        BearerTokens.Add(bearerToken);
        return _sockets.Dequeue();
    }

    internal void RecordEndpoint(Uri endpoint) => Endpoints.Add(endpoint);
}

internal sealed class FakeEventSocket : IEventSocket
{
    private readonly Queue<FakeMessage?> _messages = new();
    private readonly FakeEventSocketFactory _factory;
    private FakeMessage? _current;
    private int _offset;
    private bool _exhausted;

    public FakeEventSocket(FakeEventSocketFactory factory)
    {
        _factory = factory;
    }

    public Exception? ConnectException { get; init; }
    public Exception? ReceiveException { get; init; }
    public Action? OnExhausted { get; init; }
    public Action? OnClose { get; init; }
    public bool CloseNeverCompletes { get; init; }
    public List<string> SentMessages { get; } = [];
    public List<WebSocketCloseStatus> CloseStatuses { get; } = [];
    public int AbortCount { get; private set; }
    public int DisposeCount { get; private set; }

    public FakeEventSocket AddJson(string json)
    {
        _messages.Enqueue(new FakeMessage(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true));
        return this;
    }

    public FakeEventSocket AddFragment(
        string content,
        bool endOfMessage,
        WebSocketMessageType messageType = WebSocketMessageType.Text)
    {
        _messages.Enqueue(new FakeMessage(Encoding.UTF8.GetBytes(content), messageType, endOfMessage));
        return this;
    }

    public FakeEventSocket AddBinary(string content, bool endOfMessage = true) =>
        AddFragment(content, endOfMessage, WebSocketMessageType.Binary);

    public FakeEventSocket AddClose()
    {
        _messages.Enqueue(null);
        return this;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _factory.RecordEndpoint(uri);
        return ConnectException is null ? Task.CompletedTask : Task.FromException(ConnectException);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        SentMessages.Add(Encoding.UTF8.GetString(message.Span));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<EventSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (_current is null && _messages.Count > 0)
        {
            _current = _messages.Dequeue();
            _offset = 0;
            if (_current is null)
                return new EventSocketReceiveResult(0, true, WebSocketMessageType.Close);
        }

        if (_current is null)
        {
            if (ReceiveException is not null)
                throw ReceiveException;
            if (!_exhausted)
            {
                _exhausted = true;
                OnExhausted?.Invoke();
            }
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(cancelled.SetResult);
            await cancelled.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }

        var count = Math.Min(buffer.Length, _current!.Payload.Length - _offset);
        _current.Payload.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        var frameComplete = _offset == _current.Payload.Length;
        var end = frameComplete && _current.EndOfMessage;
        var messageType = _current.MessageType;
        if (frameComplete)
            _current = null;
        return new EventSocketReceiveResult(count, end, messageType);
    }

    public ValueTask CloseAsync(
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        CloseStatuses.Add(status);
        OnClose?.Invoke();
        return CloseNeverCompletes
            ? new ValueTask(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task)
            : ValueTask.CompletedTask;
    }

    public void Abort() => AbortCount++;

    public void Dispose() => DisposeCount++;

    private sealed record FakeMessage(
        byte[] Payload,
        WebSocketMessageType MessageType,
        bool EndOfMessage);
}
