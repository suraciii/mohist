using System.Net;
using System.Net.WebSockets;

namespace Mohist.Cli;

internal interface IEventSocketFactory
{
    IEventSocket Create(string? bearerToken);
}

internal interface IEventSocket : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
    ValueTask<EventSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken);
    void Abort();
}

internal readonly record struct EventSocketReceiveResult(
    int Count,
    bool EndOfMessage,
    WebSocketMessageType MessageType,
    WebSocketCloseStatus? CloseStatus = null);

internal sealed class EventSocketUnauthorizedException : Exception;

internal sealed class ClientEventSocketFactory : IEventSocketFactory
{
    public IEventSocket Create(string? bearerToken) => new ClientEventSocket(bearerToken);
}

internal sealed class ClientEventSocket : IEventSocket
{
    private readonly ClientWebSocket _socket = new();

    public ClientEventSocket(string? bearerToken)
    {
        _socket.Options.CollectHttpResponseDetails = true;
        if (!string.IsNullOrEmpty(bearerToken))
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
    }

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException) when (_socket.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            throw new EventSocketUnauthorizedException();
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _socket.SendAsync(message, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, cancellationToken);

    public async ValueTask<EventSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new EventSocketReceiveResult(
            result.Count,
            result.EndOfMessage,
            result.MessageType);
    }

    public async ValueTask CloseAsync(
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken) =>
        await _socket.CloseAsync(status, description, cancellationToken).ConfigureAwait(false);

    public void Abort() => _socket.Abort();

    public void Dispose() => _socket.Dispose();
}
