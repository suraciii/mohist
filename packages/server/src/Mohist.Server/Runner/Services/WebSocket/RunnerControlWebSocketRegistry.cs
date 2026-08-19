using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Runner.Services.WebSocket;

public sealed class RunnerControlWebSocketRegistry : ISingletonService
{
    private static readonly IReadOnlyDictionary<string, (Type Params, Type Result, bool AllowsNull)> RequestMethods =
        new Dictionary<string, (Type, Type, bool)>(StringComparer.Ordinal)
        {
            ["workspace.diff"] = (typeof(WorkspaceQueryParams), typeof(RunnerWorkspaceDiffResult), true),
            ["workspace.commits"] = (typeof(WorkspaceQueryParams), typeof(RunnerWorkspaceCommitsResult), true),
            ["workspace.commit-diff"] = (typeof(WorkspaceCommitDiffParams), typeof(RunnerWorkspaceCommitDiffResult), true),
            ["workspace.status"] = (typeof(WorkspaceQueryParams), typeof(WorkspaceStatus), false),
            ["workspace.file-content"] = (typeof(WorkspaceFileContentParams), typeof(RunnerWorkspaceFileContentResult), false),
            ["workspace.remove"] = (typeof(WorkspaceQueryParams), typeof(WorkspaceRemovalResult), false),
            ["session.followup"] = (typeof(FollowupParams), typeof(RunnerFollowupDeliveryResult), false),
            ["session.stop"] = (typeof(SessionStopParams), typeof(RunnerStopReply), false),
            ["session.command"] = (typeof(SessionCommandRequest), typeof(SessionCommandResult), false),
        };

    private readonly ConcurrentDictionary<string, RunnerControlWebSocketConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RunnerControlConnectionReservation> _connectionIds = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _connectionSignals = new(StringComparer.Ordinal);
    private readonly RunnerControlInstallationGate _installationGate = new();
    private readonly RunnerConnectionTracker _tracker;
    private readonly IGrainFactory _grains;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _logs;

    public RunnerControlWebSocketRegistry(
        RunnerConnectionTracker tracker,
        IGrainFactory grains,
        TimeProvider timeProvider,
        ILoggerFactory logs)
    {
        _tracker = tracker;
        _grains = grains;
        _timeProvider = timeProvider;
        _logs = logs;
    }

    internal Action<string, Guid>? InstallationWaiting { get; set; }
    internal Func<string, Guid, CancellationToken, Task>? InstallationAcquiredAsync { get; set; }

    internal bool TryReserve(Guid connectionId, out RunnerControlConnectionReservation reservation)
    {
        reservation = new RunnerControlConnectionReservation(connectionId);
        return _connectionIds.TryAdd(connectionId, reservation);
    }

    internal void ReleaseReservation(RunnerControlConnectionReservation reservation) =>
        _connectionIds.TryRemove(
            new KeyValuePair<Guid, RunnerControlConnectionReservation>(reservation.ConnectionId, reservation));

    internal async Task RunAsync(
        string runnerId,
        RunnerControlConnectionReservation reservation,
        System.Net.WebSockets.WebSocket socket,
        RunnerControlHandshake handshake,
        CancellationToken ct)
    {
        try
        {
            if (!_connectionIds.TryGetValue(reservation.ConnectionId, out var currentReservation)
                || !ReferenceEquals(currentReservation, reservation))
                throw new InvalidOperationException("Connection ID was not reserved before upgrade");
            await RunConnectionAsync(runnerId, reservation.ConnectionId, socket, handshake, ct);
        }
        finally
        {
            ReleaseReservation(reservation);
        }
    }

    private async Task RunConnectionAsync(
        string runnerId,
        Guid connectionId,
        System.Net.WebSockets.WebSocket socket,
        RunnerControlHandshake handshake,
        CancellationToken ct)
    {
        var connection = new RunnerControlWebSocketConnection(
            runnerId,
            connectionId,
            socket,
            _timeProvider,
            _logs.CreateLogger<RunnerControlWebSocketConnection>());
        var trackerInstalled = false;
        var published = false;
        Task? run = null;
        try
        {
            using (await _installationGate.AcquireAsync(
                runnerId,
                () => InstallationWaiting?.Invoke(runnerId, connectionId),
                ct))
            {
                if (InstallationAcquiredAsync is not null)
                    await InstallationAcquiredAsync(runnerId, connectionId, ct);
                try
                {
                    if (_connections.TryGetValue(runnerId, out var replaced))
                        await replaced.FenceAsync(WebSocketCloseStatus.NormalClosure, "Replaced");

                    var canonicalConnectionId = connectionId.ToString("D");
                    var generation = _tracker.Register(runnerId, canonicalConnectionId);
                    trackerInstalled = true;
                    await _grains.GetGrain<IRunnerGrain>(runnerId).UpdateRuntimeIdentityAsync(
                        handshake.BuildGitHash,
                        handshake.Component,
                        handshake.Version,
                        handshake.SourceRevision ?? handshake.BuildGitHash,
                        handshake.TreeHash,
                        handshake.ArtifactDigest,
                        handshake.ReleaseId,
                        handshake.Generation,
                        generation);

                    run = connection.RunAsync(ct);
                    if (run.IsCompleted) await run;
                    _connections[runnerId] = connection;
                    published = true;
                    if (_connectionSignals.TryGetValue(runnerId, out var signal))
                        signal.TrySetResult();
                }
                finally
                {
                    if (!published)
                    {
                        await connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "Superseded");
                        if (trackerInstalled)
                        {
                            var sessions = _tracker.UnregisterAndGetSessions(runnerId, connectionId.ToString("D"));
                            trackerInstalled = false;
                            await Task.WhenAll(sessions.Select(sessionId =>
                                _grains.GetGrain<IAgentSessionGrain>(sessionId).RunnerDisconnectedAsync()));
                        }
                    }
                }
            }

            if (run is not null) await run;
        }
        finally
        {
            try
            {
                await connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "Disconnected");
                var removed = published
                    && _connections.TryRemove(new KeyValuePair<string, RunnerControlWebSocketConnection>(runnerId, connection));
                if (trackerInstalled && (removed || !published))
                {
                    var sessions = _tracker.UnregisterAndGetSessions(runnerId, connectionId.ToString("D"));
                    await Task.WhenAll(sessions.Select(sessionId =>
                        _grains.GetGrain<IAgentSessionGrain>(sessionId).RunnerDisconnectedAsync()));
                }
            }
            finally
            {
                connection.CompleteDisconnect();
            }
        }
    }

    public Task<TResult> SendRequestAsync<TParams, TResult>(
        string runnerId,
        string method,
        TParams parameters,
        CancellationToken ct = default)
    {
        if (!RequestMethods.TryGetValue(method, out var contract)
            || contract.Params != typeof(TParams)
            || contract.Result != typeof(TResult))
            throw new ArgumentException($"Unsupported Runner control request contract '{method}'", nameof(method));
        return GetConnection(runnerId).SendRequestAsync<TParams, TResult>(method, parameters, contract.AllowsNull, ct);
    }

    public Task SendNotificationAsync<TParams>(
        string runnerId,
        string method,
        TParams parameters,
        CancellationToken ct = default)
    {
        if (!string.Equals(method, "workflow.status-changed", StringComparison.Ordinal)
            || typeof(TParams) != typeof(WorkflowRunStatusNotification))
            throw new ArgumentException($"Unsupported Runner control notification contract '{method}'", nameof(method));
        return GetConnection(runnerId).SendNotificationAsync(method, parameters, ct);
    }

    internal async Task WaitForConnectionAsync(string runnerId, CancellationToken ct)
    {
        while (!HasReadyConnection(runnerId))
        {
            var signal = _connectionSignals.GetOrAdd(
                runnerId,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (HasReadyConnection(runnerId)) signal.TrySetResult();
            try
            {
                await signal.Task.WaitAsync(ct);
            }
            finally
            {
                _connectionSignals.TryRemove(new KeyValuePair<string, TaskCompletionSource>(runnerId, signal));
            }
        }
    }

    internal Task WaitForCurrentDisconnectionAsync(string runnerId, CancellationToken ct) =>
        GetConnection(runnerId).Disconnected.WaitAsync(ct);

    private bool HasReadyConnection(string runnerId) =>
        _connections.TryGetValue(runnerId, out var connection) && connection.IsAvailable;

    private RunnerControlWebSocketConnection GetConnection(string runnerId) =>
        _connections.TryGetValue(runnerId, out var connection)
            ? connection
            : throw new RunnerControlUnavailableException($"Runner '{runnerId}' has no control connection");

}

internal sealed class RunnerControlConnectionReservation(Guid connectionId)
{
    public Guid ConnectionId { get; } = connectionId;
}

internal sealed class RunnerControlInstallationGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public async Task<IDisposable> AcquireAsync(string runnerId, Action? waiting, CancellationToken ct)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(runnerId, out entry!))
            {
                entry = new Entry();
                _entries.Add(runnerId, entry);
            }
            entry.References++;
        }

        try
        {
            waiting?.Invoke();
            await entry.Gate.WaitAsync(ct);
            return new Lease(this, runnerId, entry);
        }
        catch
        {
            ReleaseReference(runnerId, entry);
            throw;
        }
    }

    private void Release(string runnerId, Entry entry)
    {
        entry.Gate.Release();
        ReleaseReference(runnerId, entry);
    }

    private void ReleaseReference(string runnerId, Entry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0
                && _entries.TryGetValue(runnerId, out var current)
                && ReferenceEquals(current, entry))
                _entries.Remove(runnerId);
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Lease(RunnerControlInstallationGate owner, string runnerId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(runnerId, entry);
        }
    }
}

public sealed record RunnerControlHandshake(
    string? BuildGitHash,
    string? Component,
    string? Version,
    string? SourceRevision,
    string? TreeHash,
    string? ArtifactDigest,
    string? ReleaseId,
    long? Generation)
{
    public static RunnerControlHandshake FromQuery(IQueryCollection query) => new(
        Normalize(query["buildGitHash"]),
        Normalize(query["component"]),
        Normalize(query["version"]),
        Normalize(query["sourceRevision"]),
        Normalize(query["treeHash"]),
        Normalize(query["artifactDigest"]),
        Normalize(query["releaseId"]),
        long.TryParse(query["generation"], out var generation) && generation > 0 ? generation : null);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class RunnerControlUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class RunnerControlRemoteException(int code, string message, JsonElement? data)
    : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;
}

internal sealed class RunnerControlWebSocketConnection
{
    private const int QueueCapacity = 64;
    private const int MaxPendingRequests = 32;
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new(JSON.Options)
    {
        RespectRequiredConstructorParameters = true,
    };

    private readonly string _runnerId;
    private readonly Guid _connectionId;
    private readonly System.Net.WebSockets.WebSocket _socket;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _socketSendGate = new(1, 1);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextRequestId;
    private int _protocolErrors;
    private int _fenced;
    private Task? _fenceTask;

    public Task Disconnected => _disconnected.Task;
    public bool IsAvailable => Volatile.Read(ref _fenced) == 0;
    internal bool IsSocketSendGateAvailable => _socketSendGate.CurrentCount == 1;

    public RunnerControlWebSocketConnection(
        string runnerId,
        Guid connectionId,
        System.Net.WebSockets.WebSocket socket,
        TimeProvider timeProvider,
        ILogger log)
    {
        _runnerId = runnerId;
        _connectionId = connectionId;
        _socket = socket;
        _timeProvider = timeProvider;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var send = SendLoopAsync(linked.Token);
        var receive = ReceiveLoopAsync(linked.Token);
        try
        {
            await Task.WhenAny(send, receive);
            linked.Cancel();
            await Task.WhenAll(IgnoreCancellation(send), IgnoreCancellation(receive));
        }
        finally
        {
            await FenceAsync(WebSocketCloseStatus.NormalClosure, "Disconnected");
        }
    }

    public Task<TResult> SendRequestAsync<TParams, TResult>(string method, TParams parameters, CancellationToken ct) =>
        SendRequestAsync<TParams, TResult>(method, parameters, allowsNull: false, ct);

    public async Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        bool allowsNull,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = $"req_{Interlocked.Increment(ref _nextRequestId)}";
        var pending = new PendingRequest(typeof(TResult), allowsNull);
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_pending.Count >= MaxPendingRequests)
                throw new RunnerControlUnavailableException("Runner control connection has 32 requests in flight");
            _pending.Add(id, pending);
        }

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(
                new JsonRpcRequest<TParams>("2.0", id, method, parameters), ProtocolJsonOptions);
        }
        catch
        {
            RemovePending(id);
            throw;
        }
        if (payload.Length > MaxMessageBytes)
        {
            RemovePending(id);
            await FenceAsync(WebSocketCloseStatus.MessageTooBig, "Message too large");
            throw new RunnerControlUnavailableException("Runner control message is too large");
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!TryEnqueue(payload))
            {
                RemovePending(id);
                await FenceAsync((WebSocketCloseStatus)1013, "Outgoing queue saturated");
                throw new RunnerControlUnavailableException("Runner control outgoing queue is saturated");
            }
        }
        catch
        {
            RemovePending(id);
            throw;
        }

        try
        {
            var result = await pending.Task.WaitAsync(RequestTimeout, _timeProvider, ct);
            return result is null ? default! : (TResult)result;
        }
        catch (TimeoutException ex)
        {
            RemovePending(id)?.Unavailable("Runner control request timed out");
            throw new RunnerControlUnavailableException("Runner control request timed out", ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RemovePending(id)?.Cancel(ct);
            throw;
        }
    }

    public async Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) ThrowIfUnavailable();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new JsonRpcNotification<TParams>("2.0", method, parameters), ProtocolJsonOptions);
        if (payload.Length > MaxMessageBytes)
        {
            await FenceAsync(WebSocketCloseStatus.MessageTooBig, "Message too large");
            throw new RunnerControlUnavailableException("Runner control message is too large");
        }
        if (!TryEnqueue(payload))
        {
            await FenceAsync((WebSocketCloseStatus)1013, "Outgoing queue saturated");
            throw new RunnerControlUnavailableException("Runner control outgoing queue is saturated");
        }
    }

    private bool TryEnqueue(byte[] payload)
        => _outgoing.Writer.TryWrite(payload);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var payload in _outgoing.Reader.ReadAllAsync(ct))
        {
            await _socketSendGate.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _socketSendGate.Release();
            }
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
                WebSocketMessageType? messageType = null;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await FenceAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription ?? "Peer closed");
                        return;
                    }
                    messageType ??= result.MessageType;
                    if (result.MessageType != messageType)
                    {
                        await ProtocolErrorAsync(null, "Runner control message changed type between fragments");
                        break;
                    }
                    if (writer.WrittenCount + result.Count > MaxMessageBytes)
                    {
                        await FenceAsync(WebSocketCloseStatus.MessageTooBig, "Message too large");
                        return;
                    }
                    writer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != messageType)
                    continue;
                if (messageType != WebSocketMessageType.Text)
                    await ProtocolErrorAsync(null, "Runner control message was not text");
                else
                    await HandleResponseAsync(writer.WrittenMemory);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            _log.LogInformation(ex, "Runner {RunnerId} control connection {ConnectionId} disconnected", _runnerId, _connectionId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleResponseAsync(ReadOnlyMemory<byte> message)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await ProtocolErrorAsync(null, "Runner control response is not valid JSON");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await ProtocolErrorAsync(null, "Runner control response must be an object");
                return;
            }

            var id = root.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(idElement.GetString())
                    ? idElement.GetString()
                    : null;
            var validVersion = root.TryGetProperty("jsonrpc", out var version)
                && version.ValueKind == JsonValueKind.String
                && string.Equals(version.GetString(), "2.0", StringComparison.Ordinal);
            var hasResult = root.TryGetProperty("result", out var result);
            var hasError = root.TryGetProperty("error", out var error);
            if (!validVersion || id is null || hasResult == hasError)
            {
                await ProtocolErrorAsync(id, "Malformed Runner control response");
                return;
            }

            if (hasError)
            {
                if (error.ValueKind != JsonValueKind.Object
                    || !error.TryGetProperty("code", out var code)
                    || !code.TryGetInt32(out var codeValue)
                    || !error.TryGetProperty("message", out var errorMessage)
                    || errorMessage.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(errorMessage.GetString()))
                {
                    await ProtocolErrorAsync(id, "Malformed Runner control error response");
                    return;
                }

                var pendingError = RemovePending(id);
                if (pendingError is null)
                {
                    LogLateResponse(id);
                    return;
                }
                var data = error.TryGetProperty("data", out var errorData) ? errorData.Clone() : (JsonElement?)null;
                pendingError.RemoteError(codeValue, errorMessage.GetString()!, data);
                return;
            }

            var pendingResult = RemovePending(id);
            if (pendingResult is null)
            {
                LogLateResponse(id);
                return;
            }
            try
            {
                pendingResult.Success(result);
            }
            catch (JsonException)
            {
                pendingResult.Unavailable("Runner control result does not match its contract");
                await ProtocolErrorAsync(null, "Runner control result does not match its contract");
            }
        }
    }

    private async Task ProtocolErrorAsync(string? id, string message)
    {
        if (id is not null)
            RemovePending(id)?.Unavailable(message);
        _log.LogWarning("Runner {RunnerId} control protocol error: {Message}", _runnerId, message);
        if (Interlocked.Increment(ref _protocolErrors) >= 3)
            await FenceAsync(WebSocketCloseStatus.PolicyViolation, "Too many protocol errors");
    }

    private void LogLateResponse(string id) =>
        _log.LogInformation("Runner {RunnerId} returned unknown or completed control request {RequestId}", _runnerId, id);

    public Task FenceAsync(WebSocketCloseStatus status, string reason)
    {
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_fenceTask is not null) return _fenceTask;
            Volatile.Write(ref _fenced, 1);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _fenceTask = completion.Task;
        }
        _ = FenceCoreAsync(status, reason, completion);
        return completion.Task;
    }

    private async Task FenceCoreAsync(WebSocketCloseStatus status, string reason, TaskCompletionSource completion)
    {
        _outgoing.Writer.TryComplete();
        _closed.Cancel();
        PendingRequest[] pending;
        lock (_gate)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var request in pending)
            request.Unavailable($"Runner control connection closed: {reason}");

        var sendGateAcquired = false;
        try
        {
            try
            {
                using var sendGateCts = new CancellationTokenSource(CloseTimeout, _timeProvider);
                await _socketSendGate.WaitAsync(sendGateCts.Token);
                sendGateAcquired = true;
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(CloseTimeout, _timeProvider);
                    try
                    {
                        await _socket.CloseOutputAsync(status, reason, closeCts.Token);
                    }
                    catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
                    {
                        _socket.Abort();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _socket.Abort();
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }
        finally
        {
            if (sendGateAcquired) _socketSendGate.Release();
            completion.TrySetResult();
        }
    }

    public void CompleteDisconnect() => _disconnected.TrySetResult();

    private PendingRequest? RemovePending(string id)
    {
        lock (_gate)
        {
            if (!_pending.Remove(id, out var pending)) return null;
            return pending;
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _fenced) != 0)
            throw new RunnerControlUnavailableException("Runner control connection is unavailable");
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private sealed class PendingRequest(Type resultType, bool allowsNull)
    {
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object?> Task => _completion.Task;

        public void Success(JsonElement result)
        {
            if (result.ValueKind == JsonValueKind.Null && !allowsNull)
                throw new JsonException("Null is not valid for this result contract");
            _completion.TrySetResult(result.Deserialize(resultType, ProtocolJsonOptions));
        }

        public void RemoteError(int code, string message, JsonElement? data) =>
            _completion.TrySetException(new RunnerControlRemoteException(code, message, data));

        public void Unavailable(string message) =>
            _completion.TrySetException(new RunnerControlUnavailableException(message));

        public void Cancel(CancellationToken ct) => _completion.TrySetCanceled(ct);
    }
}
