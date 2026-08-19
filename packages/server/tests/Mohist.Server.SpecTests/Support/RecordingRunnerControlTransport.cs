using System.Collections.Concurrent;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.WebSocket;

namespace Mohist.Server.SpecTests.Support;

public sealed class RecordingRunnerControlTransport : IRunnerControlTransport
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, RecordingRunnerControlOwnerState> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IReadOnlyList<object?>, object?>> _responseFactories = new(StringComparer.Ordinal);
    private readonly List<RecordedRunnerControlMessage> _sentMessages = [];
    private readonly List<RecordedRunnerControlRequest> _requests = [];

    public IReadOnlyList<RecordedRunnerControlMessage> SentMessages
    {
        get { lock (_gate) return _sentMessages.ToArray(); }
    }

    public IReadOnlyList<RecordedRunnerControlRequest> Invocations
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public int OwnerCount => _owners.Count;

    public bool IsConnected(string runnerId)
    {
        if (_owners.ContainsKey(runnerId)) return true;
        if (!_owners.IsEmpty) return false;
        lock (_gate) return _responses.Count > 0 || _responseFactories.Count > 0;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sentMessages.Clear();
            _requests.Clear();
            _responses.Clear();
            _responseFactories.Clear();
        }
    }

    public RecordingRunnerControlOwner CreateOwner(string ownerId)
    {
        var state = new RecordingRunnerControlOwnerState(ownerId);
        if (!_owners.TryAdd(ownerId, state))
            throw new InvalidOperationException($"A Runner control recorder owner already exists for '{ownerId}'.");
        return new(this, state);
    }

    public void SetInvocationResponse(string method, object? response)
    {
        lock (_gate)
        {
            _responseFactories.Remove(method);
            _responses[method] = response;
        }
    }

    public void SetInvocationResponseFactory(string method, Func<IReadOnlyList<object?>, object?> responseFactory)
    {
        lock (_gate)
        {
            _responses.Remove(method);
            _responseFactories[method] = responseFactory;
        }
    }

    public async Task<TResult> SendRequestAsync<TParams, TResult>(
        string runnerId,
        string method,
        TParams parameters,
        Action? requestEnqueued = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var owner = FindOwner(runnerId);
        if (owner is null && !_owners.IsEmpty)
            throw new RunnerControlUnavailableException($"Runner '{runnerId}' has no recording control owner");
        var arguments = new object?[] { parameters };
        var message = new RecordedRunnerControlMessage(runnerId, method, arguments);
        var request = new RecordedRunnerControlRequest(runnerId, method, arguments);
        if (owner is null)
        {
            lock (_gate)
            {
                _sentMessages.Add(message);
                _requests.Add(request);
            }
        }
        else
        {
            lock (owner.Gate)
            {
                owner.SentMessages.Add(message);
                owner.Requests.Add(request);
            }
        }
        requestEnqueued?.Invoke();

        var response = ResolveResponse(owner, method, arguments)
            ?? (string.Equals(method, "session.followup", StringComparison.Ordinal)
                ? new RunnerFollowupDeliveryResult(true)
                : null);
        response = await AwaitResponseAsync(response, ct);
        return response is null ? default! : (TResult)response;
    }

    public Task SendNotificationAsync<TParams>(
        string runnerId,
        string method,
        TParams parameters,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var message = new RecordedRunnerControlMessage(runnerId, method, [parameters]);
        var owner = FindOwner(runnerId);
        if (owner is null && !_owners.IsEmpty)
            throw new RunnerControlUnavailableException($"Runner '{runnerId}' has no recording control owner");
        if (owner is null)
        {
            lock (_gate) _sentMessages.Add(message);
        }
        else
        {
            lock (owner.Gate) owner.SentMessages.Add(message);
        }
        return Task.CompletedTask;
    }

    private RecordingRunnerControlOwnerState? FindOwner(string runnerId)
    {
        return _owners.GetValueOrDefault(runnerId);
    }

    private object? ResolveResponse(
        RecordingRunnerControlOwnerState? owner,
        string method,
        IReadOnlyList<object?> arguments)
    {
        if (owner is not null)
        {
            lock (owner.Gate)
            {
                if (owner.ResponseFactories.TryGetValue(method, out var factory)) return factory(arguments);
                if (owner.Responses.TryGetValue(method, out var response)) return response;
            }
        }
        lock (_gate)
        {
            if (_responseFactories.TryGetValue(method, out var factory)) return factory(arguments);
            return _responses.GetValueOrDefault(method);
        }
    }

    private static async Task<object?> AwaitResponseAsync(object? response, CancellationToken ct)
    {
        if (response is not Task task) return response;
        await task.WaitAsync(ct);
        return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
    }

    internal void ReleaseOwner(RecordingRunnerControlOwnerState owner)
    {
        _owners.TryRemove(new KeyValuePair<string, RecordingRunnerControlOwnerState>(owner.OwnerId, owner));
    }
}

public sealed class RecordingRunnerControlOwner(
    RecordingRunnerControlTransport context,
    RecordingRunnerControlOwnerState state) : IDisposable
{
    public string OwnerId => state.OwnerId;
    public IReadOnlyList<RecordedRunnerControlMessage> SentMessages
    {
        get { lock (state.Gate) return state.SentMessages.ToArray(); }
    }
    public IReadOnlyList<RecordedRunnerControlRequest> Invocations
    {
        get { lock (state.Gate) return state.Requests.ToArray(); }
    }
    public void Clear() => state.Clear();
    public void SetInvocationResponse(string method, object? response) => state.SetResponse(method, response);
    public void SetInvocationResponseFactory(string method, Func<IReadOnlyList<object?>, object?> factory) =>
        state.SetResponseFactory(method, factory);
    public void Dispose() => context.ReleaseOwner(state);
}

public sealed class RecordingRunnerControlOwnerState(string ownerId)
{
    public string OwnerId { get; } = ownerId;
    public object Gate { get; } = new();
    public List<RecordedRunnerControlMessage> SentMessages { get; } = [];
    public List<RecordedRunnerControlRequest> Requests { get; } = [];
    public Dictionary<string, object?> Responses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<IReadOnlyList<object?>, object?>> ResponseFactories { get; } = new(StringComparer.Ordinal);

    public void Clear()
    {
        lock (Gate)
        {
            SentMessages.Clear();
            Requests.Clear();
            Responses.Clear();
            ResponseFactories.Clear();
        }
    }

    public void SetResponse(string method, object? response)
    {
        lock (Gate)
        {
            ResponseFactories.Remove(method);
            Responses[method] = response;
        }
    }

    public void SetResponseFactory(string method, Func<IReadOnlyList<object?>, object?> factory)
    {
        lock (Gate)
        {
            Responses.Remove(method);
            ResponseFactories[method] = factory;
        }
    }
}

public sealed record RecordedRunnerControlMessage(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);
public sealed record RecordedRunnerControlRequest(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);
