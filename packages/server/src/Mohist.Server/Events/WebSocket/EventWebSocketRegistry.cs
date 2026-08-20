using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Webhooks;

namespace Mohist.Server.Events.WebSocket;

public sealed class EventWebSocketRegistry : ISingletonService, IHostedService
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, EventProjectConnections> _projects = new(StringComparer.Ordinal);
    private readonly object _lifecycle = new();
    private readonly WebhookPayloadRenderer _renderer;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly IEventMatchFailureSink _matchFailures;
    private bool _stopping;

    public EventWebSocketRegistry(
        WebhookPayloadRenderer renderer,
        TimeProvider time,
        ILoggerFactory logs,
        IEventMatchFailureSink matchFailures)
    {
        _renderer = renderer;
        _time = time;
        _logs = logs;
        _matchFailures = matchFailures;
    }

    internal async Task RunAsync(string projectId, System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        EventProjectConnections? project = null;
        EventWebSocketConnection? connection = null;
        lock (_lifecycle)
        {
            if (!_stopping)
            {
                project = _projects.GetOrAdd(projectId, static _ => new());
                connection = new EventWebSocketConnection(
                    projectId,
                    socket,
                    _time,
                    subscription => project.Update(connection!, subscription),
                    _logs.CreateLogger<EventWebSocketConnection>(),
                    _matchFailures);
                project.Add(connection);
            }
        }

        if (connection is null)
        {
            await CloseRejectedSocketAsync(socket);
            return;
        }

        try
        {
            await connection.RunAsync(ct);
        }
        finally
        {
            project!.Remove(connection);
            // Buckets are retained so a reconnect can never be added to a bucket
            // that a concurrent last disconnect then removes from the registry.
            await connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "Disconnected");
        }
    }

    public Task PublishDomainAsync(CloudEvent cloudEvent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!cloudEvent.Extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
            return Task.CompletedTask;

        var structuredEvent = JsonSerializer.Deserialize<JsonElement>(_renderer.Render(cloudEvent));
        var payload = SerializeNotification("event.domain", new { @event = structuredEvent });
        Publish(projectId, EventNotificationKind.Domain, connection => connection.TryPublishDomain(cloudEvent, payload));
        return Task.CompletedTask;
    }

    public Task PublishTranscriptAsync(string projectId, TranscriptEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(projectId)) return Task.CompletedTask;
        var payload = SerializeNotification("event.transcript", new { @event = envelope });
        Publish(projectId, EventNotificationKind.Transcript, connection => connection.TryPublishTranscript(envelope.Type, payload));
        return Task.CompletedTask;
    }

    public Task PublishTaskLogAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(envelope.ProjectId)
            || string.IsNullOrWhiteSpace(envelope.TaskId))
            return Task.CompletedTask;
        var payload = SerializeNotification("event.task-log", new { delta = envelope });
        Publish(envelope.ProjectId, EventNotificationKind.TaskLog, connection => connection.TryPublishTaskLog(envelope.OwnerId, envelope.TaskId, payload));
        return Task.CompletedTask;
    }

    private void Publish(string projectId, EventNotificationKind kind, Action<EventWebSocketConnection> publish)
    {
        if (!_projects.TryGetValue(projectId, out var project)) return;
        foreach (var connection in project.Snapshot(kind)) publish(connection);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        EventWebSocketConnection[] connections;
        lock (_lifecycle)
        {
            _stopping = true;
            connections = _projects.Values.SelectMany(static project => project.SnapshotAll()).ToArray();
        }
        return Task.WhenAll(connections.Select(connection =>
            connection.FenceAsync(WebSocketCloseStatus.EndpointUnavailable, "Server stopping")));
    }

    private async Task CloseRejectedSocketAsync(System.Net.WebSockets.WebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout, _time);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Server stopping", timeout.Token);
        }
        catch (OperationCanceledException)
        {
            socket.Abort();
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    internal static bool IsWithinMessageLimit(byte[] payload) => payload.Length <= MaxMessageBytes;

    internal static byte[] SerializeNotification(string method, object parameters) =>
        JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters }, CloudEvent.JsonOptions);
}

internal enum EventNotificationKind { Domain, Transcript, TaskLog }

internal sealed class EventProjectConnections
{
    private readonly ConcurrentDictionary<Guid, EventWebSocketConnection> _all = new();
    private readonly ConcurrentDictionary<Guid, EventWebSocketConnection> _domain = new();
    private readonly ConcurrentDictionary<Guid, EventWebSocketConnection> _transcript = new();
    private readonly ConcurrentDictionary<Guid, EventWebSocketConnection> _taskLog = new();

    public void Add(EventWebSocketConnection connection) => _all[connection.Id] = connection;

    public void Update(EventWebSocketConnection connection, EventSubscription subscription)
    {
        Set(_domain, connection, subscription.DomainEnabled);
        Set(_transcript, connection, subscription.TranscriptTypes is not null);
        Set(_taskLog, connection, subscription.TaskLogs.Count > 0);
    }

    public void Remove(EventWebSocketConnection connection)
    {
        Remove(_all, connection);
        Remove(_domain, connection);
        Remove(_transcript, connection);
        Remove(_taskLog, connection);
    }

    public EventWebSocketConnection[] Snapshot(EventNotificationKind kind) => kind switch
    {
        EventNotificationKind.Domain => _domain.Values.ToArray(),
        EventNotificationKind.Transcript => _transcript.Values.ToArray(),
        EventNotificationKind.TaskLog => _taskLog.Values.ToArray(),
        _ => [],
    };

    public EventWebSocketConnection[] SnapshotAll() => _all.Values.ToArray();

    private static void Set(
        ConcurrentDictionary<Guid, EventWebSocketConnection> index,
        EventWebSocketConnection connection,
        bool present)
    {
        if (present) index[connection.Id] = connection;
        else Remove(index, connection);
    }

    private static void Remove(
        ConcurrentDictionary<Guid, EventWebSocketConnection> index,
        EventWebSocketConnection connection) =>
        index.TryRemove(new KeyValuePair<Guid, EventWebSocketConnection>(connection.Id, connection));
}

internal sealed class EventWebSocketConnection
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private const int QueueCapacity = 256;
    private const int MaxDomainTypes = 256;
    private const int MaxTranscriptTypes = 256;
    private const int MaxTaskLogScopes = 128;
    private const int MaxMatchBytes = 8 * 1024;
    private readonly string _projectId;
    private readonly System.Net.WebSockets.WebSocket _socket;
    private readonly TimeProvider _time;
    private readonly Action<EventSubscription> _subscriptionChanged;
    private readonly ILogger _log;
    private readonly IEventMatchFailureSink _matchFailures;
    private readonly TimeSpan? _matchRegexTimeout;
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _sendCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _requestIds = new(StringComparer.Ordinal);
    private EventSubscription _subscription = EventSubscription.Empty;
    private int _protocolErrors;
    private int _fenced;
    private Task? _fenceTask;

    public EventWebSocketConnection(
        string projectId,
        System.Net.WebSockets.WebSocket socket,
        TimeProvider time,
        Action<EventSubscription> subscriptionChanged,
        ILogger log,
        IEventMatchFailureSink matchFailures,
        TimeSpan? matchRegexTimeout = null)
    {
        _projectId = projectId;
        _socket = socket;
        _time = time;
        _subscriptionChanged = subscriptionChanged;
        _log = log;
        _matchFailures = matchFailures;
        _matchRegexTimeout = matchRegexTimeout;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public async Task RunAsync(CancellationToken ct)
    {
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var send = SendLoopAsync(ct);
        var receive = ReceiveLoopAsync(receiveCancellation.Token);
        await Task.WhenAny(send, receive);
        if (Volatile.Read(ref _fenced) == 0)
            await FenceAsync(WebSocketCloseStatus.NormalClosure, "Disconnected");
        receiveCancellation.Cancel();
        await Task.WhenAll(IgnoreCancellation(send), IgnoreCancellation(receive));
    }

    public void TryPublishDomain(CloudEvent cloudEvent, byte[] payload)
    {
        lock (_gate)
        {
            if (_fenced != 0 || !_subscription.MatchesDomain(cloudEvent)) return;
            EnqueueOrFence(payload);
        }
    }

    public void TryPublishTranscript(string type, byte[] payload)
    {
        lock (_gate)
        {
            if (_fenced != 0 || !_subscription.MatchesTranscript(type)) return;
            EnqueueOrFence(payload);
        }
    }

    public void TryPublishTaskLog(string workflowRunId, string taskId, byte[] payload)
    {
        lock (_gate)
        {
            if (_fenced != 0 || !_subscription.MatchesTaskLog(workflowRunId, taskId)) return;
            EnqueueOrFence(payload);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var writer = new ArrayBufferWriter<byte>();
                WebSocketReceiveResult result;
                WebSocketMessageType? type = null;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await FenceAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription ?? "Peer closed");
                        return;
                    }
                    type ??= result.MessageType;
                    if (result.MessageType != type)
                    {
                        await RejectAsync(null, -32600, "Invalid Request");
                        break;
                    }
                    if (writer.WrittenCount + result.Count > 4 * 1024 * 1024)
                    {
                        await FenceAsync(WebSocketCloseStatus.MessageTooBig, "Message too large");
                        return;
                    }
                    writer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != type) continue;
                if (type != WebSocketMessageType.Text)
                    await RejectAsync(null, -32600, "Invalid Request");
                else
                    await HandleRequestAsync(writer.WrittenMemory);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "Project {ProjectId} event socket disconnected", _projectId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleRequestAsync(ReadOnlyMemory<byte> message)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await RejectAsync(null, -32700, "Parse error");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await RejectAsync(null, -32600, "Invalid Request");
                return;
            }

            var id = ValidId(root);
            var hasId = root.TryGetProperty("id", out _);
            if (!HasOnlyProperties(root, "jsonrpc", "id", "method", "params")
                || !root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0"
                || !root.TryGetProperty("method", out var method)
                || method.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(method.GetString())
                || (hasId && id is null))
            {
                if (!hasId) await CountIdlessErrorAsync();
                else await RejectAsync(id, -32600, "Invalid Request");
                return;
            }

            if (!hasId)
            {
                await CountIdlessErrorAsync();
                return;
            }

            lock (_gate)
            {
                if (!_requestIds.Add(id!))
                {
                    _ = RejectAsync(id, -32600, "Invalid Request");
                    return;
                }
            }

            try
            {
                if (method.GetString() != "subscription.set")
                {
                    await RejectAsync(id, -32601, "Method not found");
                    return;
                }
                if (!root.TryGetProperty("params", out var parameters))
                {
                    await RejectAsync(id, -32602, "Invalid params");
                    return;
                }

                var parsed = ParseSubscription(parameters);
                if (parsed.Error is not null)
                {
                    await RejectAsync(id, -32602, "Invalid params", parsed.Error);
                    return;
                }

                lock (_gate)
                {
                    if (_fenced != 0) return;
                    _subscription = parsed.Subscription!;
                    _subscriptionChanged(_subscription);
                    EnqueueOrFence(SerializeResponse(id!, new { }));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Project {ProjectId} event socket request failed", _projectId);
                await RejectAsync(id, -32603, "Internal error");
            }
            finally
            {
                lock (_gate) _requestIds.Remove(id!);
            }
        }
    }

    private SubscriptionParseResult ParseSubscription(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(parameters, "domain", "transcript", "taskLogs")
            || !parameters.TryGetProperty("domain", out var domain)
            || !parameters.TryGetProperty("transcript", out var transcript)
            || !parameters.TryGetProperty("taskLogs", out var taskLogs)
            || taskLogs.ValueKind != JsonValueKind.Array)
            return SubscriptionParseResult.Fail(new { reason = "domain, transcript, and taskLogs are required" });

        HashSet<string>? domainTypes = null;
        EventMatchExpression? match = null;
        if (domain.ValueKind != JsonValueKind.Null)
        {
            if (domain.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(domain, "types", "match")
                || !domain.TryGetProperty("types", out var types)
                || !domain.TryGetProperty("match", out var matchElement))
                return SubscriptionParseResult.Fail(new { reason = "domain.types and domain.match are required" });
            var typeResult = ParseTypes(types, null, MaxDomainTypes, allowNull: true);
            if (typeResult.Error is not null) return SubscriptionParseResult.Fail(typeResult.Error);
            domainTypes = typeResult.Types;

            if (matchElement.ValueKind != JsonValueKind.Null)
            {
                if (matchElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(matchElement.GetString()))
                    return SubscriptionParseResult.Fail(new { reason = "domain.match must be a non-empty string or null" });
                var source = matchElement.GetString()!;
                if (Encoding.UTF8.GetByteCount(source) > MaxMatchBytes)
                    return SubscriptionParseResult.Fail(new { reason = "domain.match exceeds 8192 UTF-8 bytes" });
                var compiled = EventMatchExpression.Compile(source, _matchRegexTimeout, _matchFailures);
                if (!compiled.IsSuccess)
                {
                    var diagnostic = compiled.Diagnostic!;
                    return SubscriptionParseResult.Fail(new
                    {
                        offset = diagnostic.Offset,
                        line = diagnostic.Line,
                        column = diagnostic.Column,
                        source,
                    });
                }
                match = compiled.Expression;
            }
        }

        HashSet<string>? transcriptTypes = null;
        if (transcript.ValueKind != JsonValueKind.Null)
        {
            if (transcript.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(transcript, "types")
                || !transcript.TryGetProperty("types", out var types))
                return SubscriptionParseResult.Fail(new { reason = "transcript.types is required" });
            var typeResult = ParseTypes(types, EventCatalog.TranscriptTypes, MaxTranscriptTypes, allowNull: false);
            if (typeResult.Error is not null) return SubscriptionParseResult.Fail(typeResult.Error);
            transcriptTypes = typeResult.Types;
        }

        var scopes = new HashSet<TaskLogScope>();
        foreach (var item in taskLogs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(item, "workflowRunId", "taskId")
                || !item.TryGetProperty("workflowRunId", out var run)
                || run.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(run.GetString())
                || !item.TryGetProperty("taskId", out var task)
                || task.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(task.GetString()))
                return SubscriptionParseResult.Fail(new { reason = "taskLogs entries require non-empty workflowRunId and taskId" });
            scopes.Add(new(run.GetString()!, task.GetString()!));
        }
        if (scopes.Count > MaxTaskLogScopes)
            return SubscriptionParseResult.Fail(new { reason = "taskLogs exceeds 128 scopes" });

        return SubscriptionParseResult.Success(new(domain.ValueKind != JsonValueKind.Null, domainTypes, match, transcriptTypes, scopes));
    }

    private static bool HasOnlyProperties(JsonElement value, params string[] allowed)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static TypeParseResult ParseTypes(JsonElement element, IReadOnlyList<string>? catalog, int limit, bool allowNull)
    {
        if (allowNull && element.ValueKind == JsonValueKind.Null) return new(null, null);
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            return new(null, new { reason = $"types must be a non-empty array with at most {limit} values" });
        var known = catalog?.ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return new(null, new { reason = "types contains an empty or unknown value" });
            var value = known is null ? item.GetString()!.Trim() : item.GetString()!;
            if (known is not null && !known.Contains(value))
                return new(null, new { reason = "types contains an empty or unknown value" });
            result.Add(value);
        }
        if (result.Count > limit)
            return new(null, new { reason = $"types exceeds {limit} unique values" });
        return new(result, null);
    }

    private Task CountIdlessErrorAsync()
    {
        if (Interlocked.Increment(ref _protocolErrors) >= 3)
            return FenceAsync(WebSocketCloseStatus.PolicyViolation, "Too many protocol errors");
        return Task.CompletedTask;
    }

    private Task RejectAsync(string? id, int code, string message, object? data = null)
    {
        var payload = SerializeError(id, code, message, data);
        var close = Interlocked.Increment(ref _protocolErrors) >= 3;
        lock (_gate)
        {
            if (_fenced != 0) return Task.CompletedTask;
            if (!EnqueueOrFence(payload)) return Task.CompletedTask;
        }
        return close ? FenceAsync(WebSocketCloseStatus.PolicyViolation, "Too many protocol errors") : Task.CompletedTask;
    }

    private bool EnqueueOrFence(byte[] payload)
    {
        if (!EventWebSocketRegistry.IsWithinMessageLimit(payload))
        {
            _ = FenceAsync(WebSocketCloseStatus.MessageTooBig, "Message too large");
            return false;
        }
        if (_outgoing.Writer.TryWrite(payload)) return true;
        _ = FenceAsync((WebSocketCloseStatus)1013, "Outgoing queue saturated");
        return false;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in _outgoing.Reader.ReadAllAsync(ct))
            {
                await _sendGate.WaitAsync(ct);
                try
                {
                    using var timeout = new CancellationTokenSource(SendTimeout, _time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    await _socket.SendAsync(payload, WebSocketMessageType.Text, true, linked.Token);
                }
                finally { _sendGate.Release(); }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "Project {ProjectId} event socket send failed", _projectId);
        }
        finally { _sendCompleted.TrySetResult(); }
    }

    public Task FenceAsync(WebSocketCloseStatus status, string reason)
    {
        lock (_gate)
        {
            if (_fenceTask is not null) return _fenceTask;
            _fenced = 1;
            _fenceTask = FenceCoreAsync(status, reason);
            return _fenceTask;
        }
    }

    private async Task FenceCoreAsync(WebSocketCloseStatus status, string reason)
    {
        _outgoing.Writer.TryComplete();
        _closed.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout, _time);
            await _sendCompleted.Task.WaitAsync(timeout.Token);
            await _sendGate.WaitAsync(timeout.Token);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(status, reason, timeout.Token);
            }
            finally { _sendGate.Release(); }
        }
        catch (OperationCanceledException) { _socket.Abort(); }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
    }

    private static string? ValidId(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(id.GetString())
            ? id.GetString()
            : null;

    private static byte[] SerializeResponse(string id, object result) =>
        JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result }, CloudEvent.JsonOptions);

    private static byte[] SerializeError(string? id, int code, string message, object? data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id is null) writer.WriteNullValue(); else writer.WriteStringValue(id);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        if (data is not null)
        {
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, data, CloudEvent.JsonOptions);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private sealed record SubscriptionParseResult(EventSubscription? Subscription, object? Error)
    {
        public static SubscriptionParseResult Success(EventSubscription value) => new(value, null);
        public static SubscriptionParseResult Fail(object error) => new(null, error);
    }

    private sealed record TypeParseResult(HashSet<string>? Types, object? Error);
}

internal sealed record EventSubscription(
    bool DomainEnabled,
    HashSet<string>? DomainTypes,
    EventMatchExpression? Match,
    HashSet<string>? TranscriptTypes,
    HashSet<TaskLogScope> TaskLogs)
{
    public static EventSubscription Empty { get; } = new(false, null, null, null, []);

    public bool MatchesDomain(CloudEvent cloudEvent) =>
        DomainEnabled
        && (DomainTypes is null || DomainTypes.Contains(cloudEvent.Type))
        && (Match is null || Match.Matches(new CloudEventEventMatchInput(cloudEvent)));

    public bool MatchesTranscript(string type) => TranscriptTypes?.Contains(type) == true;

    public bool MatchesTaskLog(string workflowRunId, string taskId) => TaskLogs.Contains(new(workflowRunId, taskId));
}

internal readonly record struct TaskLogScope(string WorkflowRunId, string TaskId);
