using System.Collections.Concurrent;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.WebSocket;

namespace Mohist.Server.SpecTests.Support;

public sealed class RecordingRunnerControlTransport : IRunnerControlTransport
{
    private readonly AsyncLocal<RecordingRunnerControlGlobalState?> _globalState = new();
    private readonly ConcurrentDictionary<string, RecordingRunnerControlOwnerState> _owners = new(StringComparer.Ordinal);

    public RecordingRunnerControlTransport() => _globalState.Value = new RecordingRunnerControlGlobalState(false);

    private RecordingRunnerControlGlobalState GlobalState =>
        _globalState.Value ??= new RecordingRunnerControlGlobalState(false);

    public IReadOnlyList<RecordedRunnerControlMessage> SentMessages
    {
        get { lock (GlobalState.Gate) return GlobalState.SentMessages.ToArray(); }
    }

    public IReadOnlyList<RecordedRunnerControlRequest> Invocations
    {
        get { lock (GlobalState.Gate) return GlobalState.Requests.ToArray(); }
    }

    public int OwnerCount => _owners.Count;

    public bool IsConnected(string runnerId)
    {
        if (_owners.ContainsKey(runnerId)) return true;
        lock (GlobalState.Gate) return GlobalState.Responses.Count > 0 || GlobalState.ResponseFactories.Count > 0;
    }

    public void Clear() => _globalState.Value = new RecordingRunnerControlGlobalState(true);

    public RecordingRunnerControlOwner CreateOwner(string ownerId)
    {
        var state = new RecordingRunnerControlOwnerState(ownerId);
        if (!_owners.TryAdd(ownerId, state))
            throw new InvalidOperationException($"A Runner control recorder owner already exists for '{ownerId}'.");
        return new(this, state);
    }

    public void SetInvocationResponse(string method, object? response)
        => GlobalState.SetResponse(method, response);

    public void SetInvocationResponseFactory(string method, Func<IReadOnlyList<object?>, object?> responseFactory)
        => GlobalState.SetResponseFactory(method, responseFactory);

    public async Task<TResult> SendRequestAsync<TParams, TResult>(
        string runnerId,
        string method,
        TParams parameters,
        Action? requestEnqueued = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var owner = FindOwner(runnerId);
        if (owner is null && !_owners.IsEmpty && !GlobalState.IsActive)
            throw new RunnerControlUnavailableException($"Runner '{runnerId}' has no recording control owner");
        var arguments = new object?[] { parameters };
        var message = new RecordedRunnerControlMessage(runnerId, method, arguments);
        var request = new RecordedRunnerControlRequest(runnerId, method, arguments);
        if (owner is null)
        {
            lock (GlobalState.Gate)
            {
                GlobalState.SentMessages.Add(message);
                GlobalState.Requests.Add(request);
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
        if (owner is null && !_owners.IsEmpty && !GlobalState.IsActive)
            throw new RunnerControlUnavailableException($"Runner '{runnerId}' has no recording control owner");
        if (owner is null)
        {
            lock (GlobalState.Gate) GlobalState.SentMessages.Add(message);
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
                return owner.Responses.GetValueOrDefault(method);
            }
        }
        lock (GlobalState.Gate)
        {
            if (GlobalState.ResponseFactories.TryGetValue(method, out var factory)) return factory(arguments);
            return GlobalState.Responses.GetValueOrDefault(method);
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

internal sealed class RecordingRunnerControlGlobalState(bool isActive)
{
    private bool _isActive = isActive;

    public bool IsActive
    {
        get { lock (Gate) return _isActive; }
    }
    public object Gate { get; } = new();
    public List<RecordedRunnerControlMessage> SentMessages { get; } = [];
    public List<RecordedRunnerControlRequest> Requests { get; } = [];
    public Dictionary<string, object?> Responses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<IReadOnlyList<object?>, object?>> ResponseFactories { get; } = new(StringComparer.Ordinal);

    public void SetResponse(string method, object? response)
    {
        lock (Gate)
        {
            _isActive = true;
            ResponseFactories.Remove(method);
            Responses[method] = response;
        }
    }

    public void SetResponseFactory(string method, Func<IReadOnlyList<object?>, object?> factory)
    {
        lock (Gate)
        {
            _isActive = true;
            Responses.Remove(method);
            ResponseFactories[method] = factory;
        }
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
