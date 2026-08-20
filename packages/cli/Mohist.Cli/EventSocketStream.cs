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
        {
            await CloseAndFenceAsync(
                socket,
                WebSocketCloseStatus.NormalClosure,
                "Subscription rejected.").ConfigureAwait(false);
            return ConnectionOutcome.Fatal;
        }
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
            RequireSubscriptionResponse(root);
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
            RequireSubscriptionError(error);

            var text = error.GetProperty("message").GetString()!;
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
        RequireNotification(root);
        var method = root.GetProperty("method");
        if (method.GetString() != "event.domain")
            return;
        var parameters = root.GetProperty("params");
        RequireExactProperties(parameters, "event.domain params", ["event"]);
        var eventElement = parameters.GetProperty("event");
        if (eventElement.ValueKind != JsonValueKind.Object)
            throw ProtocolError("event.domain params.event must be an object.");
        RequireCloudEvent(eventElement);

        var line = JsonSerializer.Serialize(eventElement, MohistCliApi.JsonCompactOutputOptions);
        if (selection is { Kind: JsonSelectionKind.Selected })
        {
            var projected = selection.Project(JsonNode.Parse(line), ResourceCardinality.Stream);
            line = projected.ToJsonString(MohistCliApi.JsonCompactOutputOptions);
        }
        await _output.WriteLineAsync(line).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReceiveMessageAsync(
        IEventSocket socket,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAndFenceAsync(
                    socket,
                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription ?? string.Empty).ConfigureAwait(false);
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new EventSocketProtocolException(
                    WebSocketCloseStatus.InvalidMessageType,
                    "Binary WebSocket messages are not supported.");
            writer.Write(buffer.AsSpan(0, result.Count));
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

    private static void RequireSubscriptionResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw ProtocolError("Subscription response must be an object.");

        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        var hasResult = names.Contains("result", StringComparer.Ordinal);
        var hasError = names.Contains("error", StringComparer.Ordinal);
        if (hasResult == hasError)
            throw ProtocolError("Subscription response must contain exactly one of result or error.");

        RequireExactProperties(
            root,
            "Subscription response",
            hasResult ? ["jsonrpc", "id", "result"] : ["jsonrpc", "id", "error"]);
        RequireJsonRpcVersion(root);
    }

    private static void RequireSubscriptionError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
            throw ProtocolError("Subscription error must be an object.");

        var properties = error.EnumerateObject().ToArray();
        var expectedCount = properties.Any(property => property.NameEquals("data")) ? 3 : 2;
        RequireExactProperties(
            error,
            "Subscription error",
            expectedCount == 3 ? ["code", "message", "data"] : ["code", "message"]);
        if (!error.GetProperty("code").TryGetInt64(out _))
            throw ProtocolError("Subscription error code must be an integer.");
        if (error.GetProperty("message").ValueKind != JsonValueKind.String)
            throw ProtocolError("Subscription error message must be a string.");
    }

    private static void RequireNotification(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw ProtocolError("Notification must be an object.");
        RequireExactProperties(root, "Notification", ["jsonrpc", "method", "params"]);
        RequireJsonRpcVersion(root);
        if (root.GetProperty("method").ValueKind != JsonValueKind.String)
            throw ProtocolError("Notification method must be a string.");
        if (root.GetProperty("params").ValueKind != JsonValueKind.Object)
            throw ProtocolError("Notification params must be an object.");
    }

    private static void RequireCloudEvent(JsonElement cloudEvent)
    {
        RequireNonEmptyString(cloudEvent, "specversion");
        if (cloudEvent.GetProperty("specversion").GetString() != "1.0")
            throw ProtocolError("CloudEvent specversion must be \"1.0\".");
        RequireNonEmptyString(cloudEvent, "id");
        RequireNonEmptyString(cloudEvent, "source");
        RequireNonEmptyString(cloudEvent, "type");
    }

    private static void RequireNonEmptyString(JsonElement value, string propertyName)
    {
        var matching = value.EnumerateObject()
            .Where(property => property.NameEquals(propertyName))
            .ToArray();
        if (matching.Length != 1
            || matching[0].Value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(matching[0].Value.GetString()))
            throw ProtocolError($"CloudEvent {propertyName} must be a non-empty string.");
    }

    private static void RequireJsonRpcVersion(JsonElement root)
    {
        if (root.GetProperty("jsonrpc").ValueKind != JsonValueKind.String
            || root.GetProperty("jsonrpc").GetString() != "2.0")
            throw ProtocolError("WebSocket message must use JSON-RPC 2.0.");
    }

    private static void RequireExactProperties(
        JsonElement value,
        string subject,
        string[] expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw ProtocolError($"{subject} must be an object.");

        var actualProperties = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actualProperties.Length != expectedProperties.Length
            || expectedProperties.Any(expected => actualProperties.Count(actual => actual == expected) != 1)
            || actualProperties.Any(actual => !expectedProperties.Contains(actual, StringComparer.Ordinal)))
            throw ProtocolError($"{subject} has invalid members.");
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
