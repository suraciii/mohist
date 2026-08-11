using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.SpecTests.Support;

public sealed class RecordingRunnerHubContext : IHubContext<RunnerHub>
{
    private readonly RecordingHubClients _clients;
    private readonly Action? _afterOwnerMessageRecorded;
    private readonly Action? _afterOwnerCancellationRegistrationLeaseAcquired;
    private readonly Action? _afterOwnerCancellationDisposed;
    private readonly object _globalGate = new();
    private readonly ConcurrentDictionary<string, RecordingRunnerHubOwnerState> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _invocationResponses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IReadOnlyList<object?>, object?>> _invocationResponseFactories = new(StringComparer.Ordinal);
    private readonly List<RecordedRunnerHubMessage> _sentMessages = [];
    private readonly List<RecordedRunnerHubInvocation> _invocations = [];

    public RecordingRunnerHubContext() : this(null, null, null)
    {
    }

    internal RecordingRunnerHubContext(
        Action? afterOwnerMessageRecorded = null,
        Action? afterOwnerCancellationRegistrationLeaseAcquired = null,
        Action? afterOwnerCancellationDisposed = null)
    {
        _afterOwnerMessageRecorded = afterOwnerMessageRecorded;
        _afterOwnerCancellationRegistrationLeaseAcquired = afterOwnerCancellationRegistrationLeaseAcquired;
        _afterOwnerCancellationDisposed = afterOwnerCancellationDisposed;
        _clients = new RecordingHubClients(this);
    }

    public IReadOnlyList<RecordedRunnerHubMessage> SentMessages
    {
        get
        {
            lock (_globalGate)
                return _sentMessages.ToArray();
        }
    }

    public IReadOnlyList<RecordedRunnerHubInvocation> Invocations
    {
        get
        {
            lock (_globalGate)
                return _invocations.ToArray();
        }
    }

    public int OwnerCount => _owners.Count;
    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new NoopGroupManager();

    public void Clear()
    {
        lock (_globalGate)
        {
            _sentMessages.Clear();
            _invocations.Clear();
            _invocationResponses.Clear();
            _invocationResponseFactories.Clear();
        }
    }

    public RecordingRunnerHubOwner CreateOwner(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var state = new RecordingRunnerHubOwnerState(ownerId, _afterOwnerCancellationDisposed);
        if (!_owners.TryAdd(ownerId, state))
            throw new InvalidOperationException($"A RunnerHub recorder owner already exists for '{ownerId}'.");
        return new RecordingRunnerHubOwner(this, state);
    }

    /// <summary>
    /// Registers a return value the recording proxy should hand back when a
    /// server-side invocation targets the named method on any connection
    /// (issue-129 T-005). Only the most recent registration for a method
    /// wins, so a test can overwrite a prior response between assertions.
    /// </summary>
    public void SetInvocationResponse(string method, object? response)
    {
        lock (_globalGate)
        {
            _invocationResponseFactories.Remove(method);
            _invocationResponses[method] = response;
        }
    }

    public void SetInvocationResponseFactory(
        string method,
        Func<IReadOnlyList<object?>, object?> responseFactory)
    {
        ArgumentNullException.ThrowIfNull(responseFactory);
        lock (_globalGate)
        {
            _invocationResponses.Remove(method);
            _invocationResponseFactories[method] = responseFactory;
        }
    }

    private void RecordMessage(RecordingRunnerHubOwnerState? owner, RecordedRunnerHubMessage message)
    {
        if (owner is null)
        {
            lock (_globalGate)
                _sentMessages.Add(message);
            return;
        }

        var recorded = false;
        lock (owner.Gate)
        {
            if (!owner.Disposed)
            {
                owner.SentMessages.Add(message);
                recorded = true;
            }
        }

        if (recorded)
            _afterOwnerMessageRecorded?.Invoke();
    }

    private void RecordInvocation(RecordingRunnerHubOwnerState? owner, RecordedRunnerHubInvocation invocation)
    {
        if (owner is null)
        {
            lock (_globalGate)
                _invocations.Add(invocation);
            return;
        }

        lock (owner.Gate)
        {
            if (!owner.Disposed)
                owner.Invocations.Add(invocation);
        }
    }

    private object? ResolveInvocationResponse(
        RecordingRunnerHubOwnerState? owner,
        string method,
        IReadOnlyList<object?> arguments)
    {
        if (owner is not null)
        {
            Func<IReadOnlyList<object?>, object?>? ownerFactory = null;
            object? ownerResponse = null;
            var hasOwnerResponse = false;
            lock (owner.Gate)
            {
                if (owner.Disposed)
                    return null;
                if (owner.InvocationResponseFactories.TryGetValue(method, out var configuredFactory))
                    ownerFactory = configuredFactory;
                else if (owner.InvocationResponses.TryGetValue(method, out ownerResponse))
                    hasOwnerResponse = true;
            }

            if (ownerFactory is not null)
                return ownerFactory(arguments);
            if (hasOwnerResponse)
                return ownerResponse;

            return string.Equals(method, "ReceiveFollowup", StringComparison.Ordinal)
                ? new RunnerFollowupDeliveryResult(true)
                : null;
        }

        Func<IReadOnlyList<object?>, object?>? responseFactory = null;
        object? response = null;
        var hasResponse = false;
        lock (_globalGate)
        {
            if (_invocationResponseFactories.TryGetValue(method, out var configuredFactory))
                responseFactory = configuredFactory;
            else if (_invocationResponses.TryGetValue(method, out response))
                hasResponse = true;
        }
        if (responseFactory is not null)
            return responseFactory(arguments);
        if (hasResponse)
            return response;
        if (string.Equals(method, "ReceiveFollowup", StringComparison.Ordinal))
            return new RunnerFollowupDeliveryResult(true);
        return null;
    }

    private RecordingRunnerHubOwnerState? FindOwner(string ownerId) =>
        _owners.TryGetValue(ownerId, out var state) ? state : null;

    internal void ReleaseOwner(RecordingRunnerHubOwnerState owner)
    {
        if (!owner.Release())
            return;
        _owners.TryRemove(new KeyValuePair<string, RecordingRunnerHubOwnerState>(owner.OwnerId, owner));
    }

    private sealed class RecordingHubClients : IHubClients
    {
        private readonly RecordingRunnerHubContext _context;

        public RecordingHubClients(RecordingRunnerHubContext context)
        {
            _context = context;
        }

        public IClientProxy All => new RecordingClientProxy(_context, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, "all-except");
        // IHubClients<T> declares `Client(string) -> T` (IClientProxy here);
        // the non-generic IHubClients inherits from IHubClients<IClientProxy>
        // and re-declares `Client(string) -> ISingleClientProxy` with a default
        // implementation that wraps the IClientProxy in a
        // NonInvokingSingleClientProxy (which throws NotImplementedException
        // for InvokeCoreAsync<T>). Implement both overloads explicitly so
        // callers using the non-generic IHubClients.Client(connectionId) also
        // get an ISingleClientProxy that records invocations (issue-129
        // T-005 CancelAgentSession). The recording proxy implements both
        // IClientProxy and ISingleClientProxy so the wire semantics are
        // unchanged for SendCoreAsync.
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        ISingleClientProxy IHubClients.Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy(_context, string.Join(",", connectionIds));
        public IClientProxy Group(string groupName) => new RecordingClientProxy(_context, groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy(_context, string.Join(",", groupNames));
        public IClientProxy User(string userId) => new RecordingClientProxy(_context, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy(_context, string.Join(",", userIds));
    }

    private sealed class RecordingClientProxy : ISingleClientProxy
    {
        private readonly RecordingRunnerHubContext _context;
        private readonly string _connectionId;
        private RecordingRunnerHubOwnerState? _owner;

        public RecordingClientProxy(RecordingRunnerHubContext context, string connectionId)
        {
            _context = context;
            _connectionId = connectionId;
            _owner = context.FindOwner(connectionId);
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            var owner = BindOwner();
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            if (owner?.LifetimeToken.IsCancellationRequested == true)
                return Task.FromCanceled(owner.LifetimeToken);

            _context.RecordMessage(owner, new RecordedRunnerHubMessage(_connectionId, method, args));
            if (owner?.LifetimeToken.IsCancellationRequested == true)
                return Task.FromCanceled(owner.LifetimeToken);
            return Task.CompletedTask;
        }

        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            var owner = BindOwner();
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);
            if (owner?.LifetimeToken.IsCancellationRequested == true)
                return Task.FromCanceled<T>(owner.LifetimeToken);

            _context.RecordMessage(owner, new RecordedRunnerHubMessage(_connectionId, method, args));
            _context.RecordInvocation(owner, new RecordedRunnerHubInvocation(_connectionId, method, args));
            var response = _context.ResolveInvocationResponse(owner, method, args);
            if (owner?.LifetimeToken.IsCancellationRequested == true)
                return Task.FromCanceled<T>(owner.LifetimeToken);
            if (response is Task<T> pending)
            {
                if (owner is not null)
                    return WaitForOwnerResponseAsync(pending, owner, cancellationToken);
                return pending.WaitAsync(cancellationToken);
            }
            if (response is T typed)
            {
                return Task.FromResult(typed);
            }
            // Fall back to default(T) when no response is registered or the
            // registered response is the wrong runtime type. Tests that
            // exercise the typed return path must set the response to the
            // exact T via SetInvocationResponse before invoking the route.
            return Task.FromResult(default(T)!);
        }

        private RecordingRunnerHubOwnerState? BindOwner()
        {
            var owner = Volatile.Read(ref _owner);
            if (owner is not null)
                return owner;

            owner = _context.FindOwner(_connectionId);
            if (owner is null)
                return null;

            return Interlocked.CompareExchange(ref _owner, owner, null) ?? owner;
        }

        private async Task<T> WaitForOwnerResponseAsync<T>(
            Task<T> response,
            RecordingRunnerHubOwnerState owner,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource linkedCancellation;
            using (var registration = owner.AcquireCancellationRegistration())
            {
                _context._afterOwnerCancellationRegistrationLeaseAcquired?.Invoke();
                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    registration.LifetimeToken);
            }

            using (linkedCancellation)
                return await response.WaitAsync(linkedCancellation.Token);
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class RecordingRunnerHubOwner : IDisposable
{
    private readonly RecordingRunnerHubContext _context;
    private readonly RecordingRunnerHubOwnerState _state;

    internal RecordingRunnerHubOwner(RecordingRunnerHubContext context, RecordingRunnerHubOwnerState state)
    {
        _context = context;
        _state = state;
    }

    public string OwnerId => _state.OwnerId;
    public IReadOnlyList<RecordedRunnerHubMessage> SentMessages => _state.SnapshotMessages();
    public IReadOnlyList<RecordedRunnerHubInvocation> Invocations => _state.SnapshotInvocations();

    public void Clear() => _state.Clear();

    public void SetInvocationResponse(string method, object? response) =>
        _state.SetInvocationResponse(method, response);

    public void SetInvocationResponseFactory(
        string method,
        Func<IReadOnlyList<object?>, object?> responseFactory) =>
        _state.SetInvocationResponseFactory(method, responseFactory);

    public void Dispose() => _context.ReleaseOwner(_state);
}

internal sealed class RecordingRunnerHubOwnerState
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Action? _afterCancellationDisposed;
    private int _pendingCancellationRegistrations;
    private bool _cancellationDisposed;

    public RecordingRunnerHubOwnerState(string ownerId, Action? afterCancellationDisposed)
    {
        OwnerId = ownerId;
        _afterCancellationDisposed = afterCancellationDisposed;
        LifetimeToken = _lifetimeCancellation.Token;
    }

    public string OwnerId { get; }
    public object Gate { get; } = new();
    public CancellationToken LifetimeToken { get; }
    public List<RecordedRunnerHubMessage> SentMessages { get; } = [];
    public List<RecordedRunnerHubInvocation> Invocations { get; } = [];
    public Dictionary<string, object?> InvocationResponses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<IReadOnlyList<object?>, object?>> InvocationResponseFactories { get; } = new(StringComparer.Ordinal);
    public bool Disposed { get; private set; }

    public bool Release()
    {
        var disposeCancellation = false;
        lock (Gate)
        {
            if (Disposed)
                return false;

            _lifetimeCancellation.Cancel();
            Disposed = true;
            SentMessages.Clear();
            Invocations.Clear();
            InvocationResponses.Clear();
            InvocationResponseFactories.Clear();
            if (_pendingCancellationRegistrations == 0)
            {
                _cancellationDisposed = true;
                disposeCancellation = true;
            }
        }

        if (disposeCancellation)
            DisposeCancellation();
        return true;
    }

    internal RecordingRunnerHubCancellationRegistration AcquireCancellationRegistration()
    {
        lock (Gate)
        {
            if (Disposed)
                throw new OperationCanceledException(LifetimeToken);

            _pendingCancellationRegistrations++;
            return new RecordingRunnerHubCancellationRegistration(this, LifetimeToken);
        }
    }

    internal void ReleaseCancellationRegistration()
    {
        var disposeCancellation = false;
        lock (Gate)
        {
            if (_pendingCancellationRegistrations == 0)
                throw new InvalidOperationException("No RunnerHub cancellation registration is pending.");

            _pendingCancellationRegistrations--;
            if (Disposed && _pendingCancellationRegistrations == 0 && !_cancellationDisposed)
            {
                _cancellationDisposed = true;
                disposeCancellation = true;
            }
        }

        if (disposeCancellation)
            DisposeCancellation();
    }

    public IReadOnlyList<RecordedRunnerHubMessage> SnapshotMessages()
    {
        lock (Gate)
            return SentMessages.ToArray();
    }

    public IReadOnlyList<RecordedRunnerHubInvocation> SnapshotInvocations()
    {
        lock (Gate)
            return Invocations.ToArray();
    }

    public void Clear()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            SentMessages.Clear();
            Invocations.Clear();
            InvocationResponses.Clear();
            InvocationResponseFactories.Clear();
        }
    }

    public void SetInvocationResponse(string method, object? response)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            InvocationResponseFactories.Remove(method);
            InvocationResponses[method] = response;
        }
    }

    public void SetInvocationResponseFactory(
        string method,
        Func<IReadOnlyList<object?>, object?> responseFactory)
    {
        ArgumentNullException.ThrowIfNull(responseFactory);
        lock (Gate)
        {
            ThrowIfDisposed();
            InvocationResponses.Remove(method);
            InvocationResponseFactories[method] = responseFactory;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(RecordingRunnerHubOwner));
    }

    private void DisposeCancellation()
    {
        _lifetimeCancellation.Dispose();
        _afterCancellationDisposed?.Invoke();
    }
}

internal sealed class RecordingRunnerHubCancellationRegistration : IDisposable
{
    private RecordingRunnerHubOwnerState? _owner;

    internal RecordingRunnerHubCancellationRegistration(
        RecordingRunnerHubOwnerState owner,
        CancellationToken lifetimeToken)
    {
        _owner = owner;
        LifetimeToken = lifetimeToken;
    }

    public CancellationToken LifetimeToken { get; }

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.ReleaseCancellationRegistration();
}

public sealed record RecordedRunnerHubMessage(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);

public sealed record RecordedRunnerHubInvocation(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);
