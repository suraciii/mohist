using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class EventSocketStream
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(2);
    private readonly HttpClient _http;
    private readonly CliCredentialSession _credentials;
    private readonly IEventSocketFactory _sockets;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<double> _jitter;
    private readonly TimeProvider _timeProvider;

    public EventSocketStream(
        HttpClient http,
        CliCredentialSession credentials,
        IEventSocketFactory sockets,
        TextWriter output,
        TextWriter error,
        Func<TimeSpan, CancellationToken, Task> wait,
        Func<double> jitter,
        TimeProvider timeProvider)
    {
        _http = http;
        _credentials = credentials;
        _sockets = sockets;
        _output = output;
        _error = error;
        _wait = wait;
        _jitter = jitter;
        _timeProvider = timeProvider;
    }

    public async Task<int> RunAsync(
        string projectRef,
        string? match,
        string[]? types,
        JsonSelection? selection,
        CancellationToken cancellationToken)
    {
        var credentialDestination = BuildHttpEndpoint(projectRef);
        var endpoint = BuildSocketEndpoint(credentialDestination);
        var reconnectAttempt = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await RunConnectionAttemptAsync(
                    endpoint, credentialDestination, match, types, selection, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome == ConnectionOutcome.Fatal)
                    return 1;
                if (outcome == ConnectionOutcome.Connected)
                    reconnectAttempt = 0;

                var exponent = Math.Min(reconnectAttempt++, 5);
                var baseDelay = Math.Min(1000 * (1 << exponent), MaximumReconnectDelay.TotalMilliseconds);
                var jitteredDelay = baseDelay * (0.8 + (0.4 * _jitter()));
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(jitteredDelay, MaximumReconnectDelay.TotalMilliseconds));
                await _wait(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CliExitCode.For(CliExitOutcome.Cancelled);
        }
    }

    private async Task<ConnectionOutcome> RunConnectionAttemptAsync(
        Uri endpoint,
        Uri credentialDestination,
        string? match,
        string[]? types,
        JsonSelection? selection,
        CancellationToken cancellationToken)
    {
        var credential = await _credentials.TryResolveAllowedAsync(credentialDestination).ConfigureAwait(false);
        var refreshed = false;
        while (true)
        {
            using var socket = _sockets.Create(credential?.Token);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (EventSocketUnauthorizedException) when (!refreshed && credential is not null)
            {
                refreshed = true;
                var rotation = await _credentials.TryRefreshAsync(credential, credentialDestination, cancellationToken)
                    .ConfigureAwait(false);
                if (rotation is null)
                {
                    if (credential.Source == CliCredentialSource.CredentialFile)
                        _credentials.WriteExpiredSessionMessage();
                    return ConnectionOutcome.Disconnected;
                }
                credential = credential with { Token = rotation.AccessToken, Stored = rotation };
                continue;
            }
            catch (EventSocketUnauthorizedException)
            {
                return ConnectionOutcome.Disconnected;
            }
            catch (WebSocketException)
            {
                return ConnectionOutcome.Disconnected;
            }

            try
            {
                return await RunConnectedAsync(socket, match, types, selection, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CloseAndFenceAsync(
                    socket,
                    WebSocketCloseStatus.NormalClosure,
                    "Client closed.").ConfigureAwait(false);
                throw;
            }
            catch (EventSocketProtocolException ex)
            {
                await CloseAndFenceAsync(socket, ex.CloseStatus, ex.Message).ConfigureAwait(false);
                return ConnectionOutcome.Disconnected;
            }
            catch (WebSocketException)
            {
                return ConnectionOutcome.Disconnected;
            }
        }
    }

    private async Task<ConnectionOutcome> RunConnectedAsync(
        IEventSocket socket,
        string? match,
        string[]? types,
        JsonSelection? selection,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = "req_1",
            method = "subscription.set",
            @params = new
            {
                domain = new { types, match = string.IsNullOrWhiteSpace(match) ? null : match },
                transcript = (object?)null,
                taskLogs = Array.Empty<object>(),
            },
        });
        await socket.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var acknowledged = await AwaitSubscriptionAsync(socket, cancellationToken).ConfigureAwait(false);
        if (acknowledged == SubscriptionOutcome.Rejected)
            return ConnectionOutcome.Fatal;
        if (acknowledged == SubscriptionOutcome.Disconnected)
            return ConnectionOutcome.Disconnected;

        while (true)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return ConnectionOutcome.Connected;
            await WriteDomainEventAsync(message, selection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SubscriptionOutcome> AwaitSubscriptionAsync(
        IEventSocket socket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return SubscriptionOutcome.Disconnected;
            using var document = ParseProtocolMessage(message);
            var root = document.RootElement;
            RequireJsonRpcObject(root);
            if (!root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || id.GetString() != "req_1")
                throw ProtocolError("Subscription response has an invalid id.");

            var hasResult = root.TryGetProperty("result", out var result);
            var hasError = root.TryGetProperty("error", out var error);
            if (hasResult == hasError)
                throw ProtocolError("Subscription response must contain exactly one of result or error.");
            if (hasResult)
            {
                if (result.ValueKind != JsonValueKind.Object)
                    throw ProtocolError("Subscription result must be an object.");
                return SubscriptionOutcome.Accepted;
            }
            if (error.ValueKind != JsonValueKind.Object)
                throw ProtocolError("Subscription error must be an object.");

            var text = error.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? "Subscription rejected."
                : "Subscription rejected.";
            if (error.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("line", out var line)
                && line.TryGetInt32(out var lineNumber)
                && data.TryGetProperty("column", out var column)
                && column.TryGetInt32(out var columnNumber))
                text += $" (line {lineNumber}, column {columnNumber})";
            await _error.WriteLineAsync(text).ConfigureAwait(false);
            return SubscriptionOutcome.Rejected;
        }
    }

    private async Task WriteDomainEventAsync(
        byte[] message,
        JsonSelection? selection,
        CancellationToken cancellationToken)
    {
        using var document = ParseProtocolMessage(message);
        var root = document.RootElement;
        RequireJsonRpcObject(root);
        if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            throw ProtocolError("Notification method must be a string.");
        if (method.GetString() != "event.domain")
            return;
        if (!root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("event", out var eventElement)
            || eventElement.ValueKind != JsonValueKind.Object)
            throw ProtocolError("event.domain params.event must be an object.");

        var line = eventElement.GetRawText();
        if (selection is { Kind: JsonSelectionKind.Selected })
        {
            var projected = selection.Project(JsonNode.Parse(line), ResourceCardinality.Stream);
            line = projected.ToJsonString(MohistCliApi.JsonCompactOutputOptions);
        }
        await _output.WriteLineAsync(line).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReceiveMessageAsync(
        IEventSocket socket,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var memory = writer.GetMemory(16 * 1024);
            var result = await socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new EventSocketProtocolException(
                    WebSocketCloseStatus.InvalidMessageType,
                    "Binary WebSocket messages are not supported.");
            writer.Advance(result.Count);
            if (writer.WrittenCount > MaximumMessageBytes)
                throw new EventSocketProtocolException(
                    WebSocketCloseStatus.MessageTooBig,
                    "Message exceeds 4 MiB.");
            if (result.EndOfMessage)
                return writer.WrittenMemory.ToArray();
        }
    }

    private async Task CloseAndFenceAsync(
        IEventSocket socket,
        WebSocketCloseStatus status,
        string description)
    {
        Task? closeTask = null;
        try
        {
            closeTask = socket.CloseAsync(status, description, CancellationToken.None).AsTask();
            var timeoutTask = Task.Delay(GracefulCloseTimeout, _timeProvider, CancellationToken.None);
            if (await Task.WhenAny(closeTask, timeoutTask).ConfigureAwait(false) == closeTask)
                await closeTask.ConfigureAwait(false);
            else
                _ = closeTask.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static JsonDocument ParseProtocolMessage(byte[] message)
    {
        try
        {
            return JsonDocument.Parse(message);
        }
        catch (JsonException ex)
        {
            throw ProtocolError("WebSocket message is not valid JSON.", ex);
        }
    }

    private static void RequireJsonRpcObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("jsonrpc", out var jsonRpc)
            || jsonRpc.ValueKind != JsonValueKind.String
            || jsonRpc.GetString() != "2.0")
            throw ProtocolError("WebSocket message must be a JSON-RPC 2.0 object.");
    }

    private static EventSocketProtocolException ProtocolError(string message, Exception? inner = null) =>
        new(WebSocketCloseStatus.ProtocolError, message, inner);

    private Uri BuildHttpEndpoint(string projectRef)
    {
        var baseAddress = _http.BaseAddress
            ?? throw new InvalidOperationException("The Mohist server base address is not configured.");
        return new Uri(baseAddress, $"/api/projects/{Uri.EscapeDataString(projectRef)}/events/socket");
    }

    private static Uri BuildSocketEndpoint(Uri httpEndpoint)
    {
        var builder = new UriBuilder(httpEndpoint)
        {
            Scheme = httpEndpoint.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = httpEndpoint.IsDefaultPort ? -1 : httpEndpoint.Port,
        };
        return builder.Uri;
    }

    private enum ConnectionOutcome { Connected, Disconnected, Fatal }
    private enum SubscriptionOutcome { Accepted, Disconnected, Rejected }

    private sealed class EventSocketProtocolException : Exception
    {
        public EventSocketProtocolException(
            WebSocketCloseStatus closeStatus,
            string message,
            Exception? inner = null)
            : base(message, inner) => CloseStatus = closeStatus;

        public WebSocketCloseStatus CloseStatus { get; }
    }
}
