using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services;

public class RunnerConnectionTracker : ISingletonService, IAgentSessionConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RunnerConnectionLease> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new();
    private readonly string _processEpoch = Guid.NewGuid().ToString("N");
    private long _nextConnectionGeneration;

    public string Register(string runnerId, string connectionId)
    {
        var lease = _connections.AddOrUpdate(
            runnerId,
            _ => NewLease(connectionId),
            (_, current) => string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal)
                ? current
                : NewLease(connectionId));
        return lease.Generation;
    }

    private RunnerConnectionLease NewLease(string connectionId) =>
        new(connectionId, $"{_processEpoch}:{Interlocked.Increment(ref _nextConnectionGeneration)}");

    public void Unregister(string runnerId, string? connectionId = null)
    {
        if (connectionId is null)
        {
            _connections.TryRemove(runnerId, out _);
            return;
        }

        if (_connections.TryGetValue(runnerId, out var current)
            && string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _connections.TryRemove(new KeyValuePair<string, RunnerConnectionLease>(runnerId, current));
        }
    }

    public IReadOnlyList<string> UnregisterAndGetSessions(string runnerId, string connectionId)
    {
        if (!_connections.TryGetValue(runnerId, out var current)
            || !string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal)
            || !_connections.TryRemove(new KeyValuePair<string, RunnerConnectionLease>(runnerId, current)))
            return [];

        if (!_sessions.TryRemove(runnerId, out var sessions)) return [];
        return sessions.Keys.ToArray();
    }

    public void RegisterSession(string runnerId, string sessionId) =>
        _sessions.GetOrAdd(runnerId, _ => new ConcurrentDictionary<string, byte>())[sessionId] = 0;

    public string? GetConnectionId(string runnerId)
    {
        return _connections.TryGetValue(runnerId, out var connection)
            ? connection.ConnectionId
            : null;
    }

    public string? GetConnectionGeneration(string runnerId)
    {
        return _connections.TryGetValue(runnerId, out var connection)
            ? connection.Generation
            : null;
    }

    public bool Matches(string runnerId, string? connectionId) =>
        connectionId is not null
        && _connections.TryGetValue(runnerId, out var connection)
        && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal);

    public RunnerPollRequest ApplyPollAdmission(string runnerId, RunnerPollRequest req)
    {
        var currentConnectionId = GetConnectionId(runnerId);
        var connectionGeneration = currentConnectionId is not null
            && string.Equals(req.ConnectionId, currentConnectionId, StringComparison.Ordinal)
            ? GetConnectionGeneration(runnerId)
            : null;
        var observation = NormalizeAdmissionObservation(req, connectionGeneration);
        return req with
        {
            ConnectionGeneration = connectionGeneration,
            AdmissionReady = observation.Ready,
            AdmissionReasonCodes = observation.ReasonCodes,
        };
    }

    private static (bool Ready, List<string> ReasonCodes) NormalizeAdmissionObservation(
        RunnerPollRequest request,
        string? connectionGeneration)
    {
        var suppliedReasons = request.AdmissionReasonCodes;
        var knownReasons = suppliedReasons is null
            ? []
            : RunnerAdmissionReasonCodes.Local
                .Where(suppliedReasons.Contains)
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToList();
        var reasonsAreValid = suppliedReasons is not null
            && suppliedReasons.Count == knownReasons.Count
            && suppliedReasons.Distinct(StringComparer.Ordinal).Count() == suppliedReasons.Count;
        var admissionIsConsistent = request.AdmissionReady switch
        {
            true => reasonsAreValid && suppliedReasons!.Count == 0,
            false => reasonsAreValid && suppliedReasons!.Count > 0,
            _ => false,
        };
        var currentConnectionIsValid = !string.IsNullOrWhiteSpace(connectionGeneration);
        if (!currentConnectionIsValid || !admissionIsConsistent)
            return (false, [RunnerAdmissionReasonCodes.ObservationInvalid]);

        return (request.AdmissionReady!.Value, knownReasons);
    }

    private sealed record RunnerConnectionLease(string ConnectionId, string Generation);
}
