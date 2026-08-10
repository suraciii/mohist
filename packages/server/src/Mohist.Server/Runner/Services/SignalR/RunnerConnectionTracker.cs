using System.Collections.Concurrent;
using System.Globalization;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerConnectionTracker : ISingletonService, IAgentSessionConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RunnerConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new();
    private readonly ConcurrentDictionary<string, object> _instanceGates = new();
    private readonly ConcurrentDictionary<string, RunnerInstanceKey> _instanceFences = new();
    private readonly ConcurrentDictionary<RunnerInstanceKey, RunnerRuntimeReport> _runtimeReports = new();
    private readonly ConcurrentDictionary<RunnerWaitKey, ConcurrentDictionary<Guid, TaskCompletionSource<RunnerRuntimeConnection>>> _runtimeWaiters = new();

    public bool Register(string runnerId, string connectionId)
    {
        return Register(runnerId, connectionId, null, null, null, null);
    }

    public bool Register(
        string runnerId,
        string connectionId,
        string? runtimeGeneration,
        string? buildGitHash,
        string? artifactDigest,
        string? runtimeSessionToken = null)
    {
        var generation = Normalize(runtimeGeneration);
        var sessionToken = Normalize(runtimeSessionToken);
        var sourceHash = Normalize(buildGitHash);
        var digest = Normalize(artifactDigest);
        if (generation is null || sessionToken is null)
        {
            lock (InstanceGate(runnerId))
            {
                if (generation is not null || sessionToken is not null || _instanceFences.ContainsKey(runnerId))
                    return false;

                _connections[runnerId] = new RunnerConnection(
                    connectionId,
                    null,
                    null,
                    sourceHash,
                    digest);
            }
            return true;
        }

        if (!HasValidManagedIdentity(sourceHash, digest))
            return false;

        var key = new RunnerInstanceKey(runnerId, generation, sessionToken);
        var candidate = new RunnerConnection(
            connectionId,
            generation,
            sessionToken,
            sourceHash,
            digest);
        lock (InstanceGate(runnerId))
        {
            if (!TryAdvanceFence(key))
                return false;

            _connections[runnerId] = candidate;
        }

        if (!IsActiveConnection(key, connectionId))
            return false;

        SignalReady(key);
        return true;
    }

    public bool ReportRuntime(
        string runnerId,
        string? runtimeGeneration,
        string? buildGitHash,
        string? artifactDigest,
        string? runtimeSessionToken = null)
    {
        var generation = Normalize(runtimeGeneration);
        var sessionToken = Normalize(runtimeSessionToken);
        var sourceHash = Normalize(buildGitHash);
        var digest = Normalize(artifactDigest);
        if (generation is null || sessionToken is null)
        {
            if (generation is not null || sessionToken is not null)
                return false;
            lock (InstanceGate(runnerId))
                return !_instanceFences.ContainsKey(runnerId);
        }

        if (!HasValidManagedIdentity(sourceHash, digest))
            return false;

        var key = new RunnerInstanceKey(runnerId, generation, sessionToken);
        lock (InstanceGate(runnerId))
        {
            if (!TryAdvanceFence(key))
                return false;

            _runtimeReports[key] = new RunnerRuntimeReport(
                sourceHash,
                digest);
        }
        SignalReady(key);
        return true;
    }

    public bool UnregisterRuntime(
        string runnerId,
        string? runtimeGeneration,
        string? runtimeSessionToken)
    {
        var generation = Normalize(runtimeGeneration);
        var sessionToken = Normalize(runtimeSessionToken);
        if (generation is null || sessionToken is null)
        {
            if (generation is not null || sessionToken is not null)
                return false;
            lock (InstanceGate(runnerId))
                return !_instanceFences.ContainsKey(runnerId);
        }

        var key = new RunnerInstanceKey(runnerId, generation, sessionToken);
        lock (InstanceGate(runnerId))
        {
            if (!IsActiveFence(key))
                return false;

            _runtimeReports.TryRemove(key, out _);
            return true;
        }
    }

    public RunnerRuntimeConnection? GetRuntimeIdentity(string runnerId, string runtimeGeneration)
    {
        if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(runtimeGeneration))
            return null;

        var generation = Normalize(runtimeGeneration);
        if (generation is null)
            return null;

        if (!_connections.TryGetValue(runnerId, out var connection)
            || !string.Equals(connection.RuntimeGeneration, generation, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(connection.RuntimeSessionToken))
        {
            return null;
        }

        var key = new RunnerInstanceKey(runnerId, generation, connection.RuntimeSessionToken);
        if (!IsActiveFence(key))
            return null;

        var reported = HasValidManagedIdentity(connection.BuildGitHash, connection.ArtifactDigest)
            && _runtimeReports.TryGetValue(key, out var report)
            && HasValidManagedIdentity(report.BuildGitHash, report.ArtifactDigest)
            && RequiredIdentityMatches(report.BuildGitHash, connection.BuildGitHash)
            && RequiredIdentityMatches(report.ArtifactDigest, connection.ArtifactDigest);
        return new RunnerRuntimeConnection(
            runnerId,
            generation,
            connection.BuildGitHash,
            connection.ArtifactDigest,
            reported,
            connection.ConnectionId);
    }

    public async Task<RunnerRuntimeConnection?> WaitForRuntimeIdentityAsync(
        string runnerId,
        string runtimeGeneration,
        CancellationToken cancellationToken)
    {
        var current = GetRuntimeIdentity(runnerId, runtimeGeneration);
        if (current is { IsOnline: true })
            return current;

        var key = new RunnerWaitKey(runnerId, runtimeGeneration);
        var waiterId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<RunnerRuntimeConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = _runtimeWaiters.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, TaskCompletionSource<RunnerRuntimeConnection>>());
        waiters[waiterId] = waiter;

        current = GetRuntimeIdentity(runnerId, runtimeGeneration);
        if (current is { IsOnline: true })
            waiter.TrySetResult(current);

        try
        {
            return await waiter.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            waiters.TryRemove(waiterId, out _);
            if (waiters.IsEmpty)
                _runtimeWaiters.TryRemove(new KeyValuePair<RunnerWaitKey, ConcurrentDictionary<Guid, TaskCompletionSource<RunnerRuntimeConnection>>>(key, waiters));
        }
    }

    public void Unregister(string runnerId, string? connectionId = null)
    {
        if (connectionId is null)
        {
            if (_connections.TryGetValue(runnerId, out var current)
                && current.RuntimeGeneration is null
                && current.RuntimeSessionToken is null)
            {
                _connections.TryRemove(new KeyValuePair<string, RunnerConnection>(runnerId, current));
            }
            return;
        }

        if (_connections.TryGetValue(runnerId, out var connection)
            && connection.RuntimeGeneration is null
            && connection.RuntimeSessionToken is null
            && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _connections.TryRemove(new KeyValuePair<string, RunnerConnection>(runnerId, connection));
        }
    }

    public IReadOnlyList<string> UnregisterAndGetSessions(string runnerId, string connectionId)
    {
        if (!_connections.TryGetValue(runnerId, out var connection)
            || connection.RuntimeGeneration is not null
            || connection.RuntimeSessionToken is not null
            || !string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal)
            || !_connections.TryRemove(new KeyValuePair<string, RunnerConnection>(runnerId, connection)))
            return [];

        if (!_sessions.TryRemove(runnerId, out var sessions)) return [];
        return sessions.Keys.ToArray();
    }

    public IReadOnlyList<string> UnregisterAndGetSessions(
        string runnerId,
        string? runtimeGeneration,
        string connectionId,
        string? runtimeSessionToken)
    {
        var generation = Normalize(runtimeGeneration);
        var sessionToken = Normalize(runtimeSessionToken);
        if (generation is null || sessionToken is null)
            return [];

        var key = new RunnerInstanceKey(runnerId, generation, sessionToken);
        lock (InstanceGate(runnerId))
        {
            if (!_connections.TryGetValue(runnerId, out var connection)
                || !string.Equals(connection.RuntimeGeneration, generation, StringComparison.Ordinal)
                || !string.Equals(connection.RuntimeSessionToken, sessionToken, StringComparison.Ordinal)
                || !string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal)
                || !_connections.TryRemove(new KeyValuePair<string, RunnerConnection>(runnerId, connection)))
            {
                return [];
            }

            _runtimeReports.TryRemove(key, out _);
        }

        if (!_sessions.TryRemove(runnerId, out var sessions)) return [];
        return sessions.Keys.ToArray();
    }

    public void RegisterSession(string runnerId, string sessionId) =>
        _sessions.GetOrAdd(runnerId, _ => new ConcurrentDictionary<string, byte>())[sessionId] = 0;

    public string? GetConnectionId(string runnerId)
    {
        if (!_connections.TryGetValue(runnerId, out var connection))
            return null;

        if (connection.RuntimeGeneration is null && connection.RuntimeSessionToken is null)
            return connection.ConnectionId;

        if (connection.RuntimeGeneration is null || connection.RuntimeSessionToken is null)
            return null;

        return GetRuntimeIdentity(runnerId, connection.RuntimeGeneration) is { IsOnline: true } identity
            ? identity.ConnectionId
            : null;
    }

    private void SignalReady(RunnerInstanceKey key)
    {
        var ready = GetRuntimeIdentity(key.RunnerId, key.RuntimeGeneration);
        if (ready is not { IsOnline: true }
            || !_runtimeWaiters.TryGetValue(new RunnerWaitKey(key.RunnerId, key.RuntimeGeneration), out var waiters))
        {
            return;
        }

        foreach (var waiter in waiters.Values)
            waiter.TrySetResult(ready);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasValidManagedIdentity(string? sourceHash, string? artifactDigest) =>
        IsSourceHash(sourceHash) && IsArtifactDigest(artifactDigest);

    private static bool IsSourceHash(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => (c is >= 'a' and <= 'z')
            || (c is >= 'A' and <= 'Z')
            || (c is >= '0' and <= '9'));

    private static bool IsArtifactDigest(string? value) =>
        value is { Length: 64 }
        && value.All(c => (c is >= 'a' and <= 'f') || (c is >= '0' and <= '9'));

    private object InstanceGate(string runnerId) => _instanceGates.GetOrAdd(runnerId, _ => new object());

    private bool TryAdvanceFence(RunnerInstanceKey candidate)
    {
        if (!_instanceFences.TryGetValue(candidate.RunnerId, out var current))
            return _instanceFences.TryAdd(candidate.RunnerId, candidate);

        if (SameInstance(current, candidate))
            return true;

        if (CompareGeneration(candidate.RuntimeGeneration, current.RuntimeGeneration) <= 0)
            return false;

        return _instanceFences.TryUpdate(candidate.RunnerId, candidate, current);
    }

    private bool IsActiveFence(RunnerInstanceKey key) =>
        _instanceFences.TryGetValue(key.RunnerId, out var current)
        && SameInstance(current, key);

    private bool IsActiveConnection(RunnerInstanceKey key, string connectionId) =>
        IsActiveFence(key)
        && _connections.TryGetValue(key.RunnerId, out var connection)
        && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal)
        && string.Equals(connection.RuntimeGeneration, key.RuntimeGeneration, StringComparison.Ordinal)
        && string.Equals(connection.RuntimeSessionToken, key.RuntimeSessionToken, StringComparison.Ordinal);

    private static bool SameInstance(RunnerInstanceKey left, RunnerInstanceKey right) =>
        string.Equals(left.RuntimeGeneration, right.RuntimeGeneration, StringComparison.Ordinal)
        && string.Equals(left.RuntimeSessionToken, right.RuntimeSessionToken, StringComparison.Ordinal);

    private static int CompareGeneration(string candidate, string active)
    {
        var candidateIsManaged = ulong.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var candidateValue);
        var activeIsManaged = ulong.TryParse(active, NumberStyles.None, CultureInfo.InvariantCulture, out var activeValue);
        if (candidateIsManaged && activeIsManaged)
            return candidateValue.CompareTo(activeValue);
        if (candidateIsManaged)
            return 1;
        if (activeIsManaged)
            return -1;
        return string.Equals(candidate, active, StringComparison.Ordinal) ? 0 : -1;
    }

    private static bool RequiredIdentityMatches(string? reported, string? connected) =>
        !string.IsNullOrWhiteSpace(reported)
        && !string.IsNullOrWhiteSpace(connected)
        && string.Equals(reported, connected, StringComparison.Ordinal);

    private sealed record RunnerConnection(
        string ConnectionId,
        string? RuntimeGeneration,
        string? RuntimeSessionToken,
        string? BuildGitHash,
        string? ArtifactDigest);

    private sealed record RunnerRuntimeReport(string? BuildGitHash, string? ArtifactDigest);

    private sealed record RunnerInstanceKey(string RunnerId, string RuntimeGeneration, string RuntimeSessionToken);

    private sealed record RunnerWaitKey(string RunnerId, string RuntimeGeneration);
}

public sealed record RunnerRuntimeConnection(
    string RunnerId,
    string RuntimeGeneration,
    string? BuildGitHash,
    string? ArtifactDigest,
    bool IsOnline,
    string ConnectionId);
