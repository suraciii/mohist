using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

public interface ISlackAppManagementPort
{
    Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default);
    Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default);
}

public interface ISlackAppManagementFactPort
{
    Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default);
}

public sealed record SlackAppManagementRequest(string EnrollmentId, string ChildAppId, string WorkspaceTeamId, string? AppId = null);

public sealed record SlackAppManagementFact(
    SlackAppManagementFactOutcome Outcome,
    string? AppId = null,
    string? ErrorClass = null);

public enum SlackAppManagementFactOutcome
{
    Present,
    Absent,
    Unknown,
}

public sealed record SlackAppManagementResult(
    SlackAppManagementOutcome Outcome,
    string? AppId = null,
    string? ErrorClass = null,
    string? ErrorMessage = null);

public enum SlackAppManagementOutcome
{
    Succeeded,
    DefiniteFailure,
    Unknown,
}

public sealed class UnavailableSlackAppManagementPort : ISlackAppManagementPort, ISlackAppManagementFactPort, IScopedService
{
    public Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager app management is not connected in this slice.");

    public Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager app management is not connected in this slice.");

    public Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager app inspection is not connected in this slice.");
}

public sealed class FakeSlackAppManagementPort : ISlackAppManagementPort, ISlackAppManagementFactPort
{
    private readonly Dictionary<string, FakeSlackAppResponse> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _managedAppLimit = int.MaxValue;
    private int _createCalls;
    private int _deleteCalls;

    public int CreateCalls => Volatile.Read(ref _createCalls);
    public int DeleteCalls => Volatile.Read(ref _deleteCalls);

    public void SetResponse(string childAppId, FakeSlackAppResponse response)
    {
        lock (_gate) _responses[childAppId] = response;
    }

    public void SetManagedAppLimit(int limit) => _managedAppLimit = limit;

    public Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _createCalls);
        lock (_gate)
        {
            if (_responses.TryGetValue(request.ChildAppId, out var configured) && configured.Create is not null)
                return Task.FromResult(configured.Create);
            if (_owners.Values.Count(owner => owner == request.EnrollmentId) >= _managedAppLimit)
                return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: "managed_app_limit_reached"));
            if (_owners.TryGetValue(request.ChildAppId, out var owner) && owner != request.EnrollmentId)
                return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: "unauthorized"));
            var appId = request.AppId ?? $"A_FAKE_{request.ChildAppId}";
            _owners[request.ChildAppId] = request.EnrollmentId;
            return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded, appId));
        }
    }

    public Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_responses.TryGetValue(request.ChildAppId, out var configured) && configured.Inspect is not null)
                return Task.FromResult(configured.Inspect);
            if (!_owners.TryGetValue(request.ChildAppId, out var owner) || owner != request.EnrollmentId)
                return Task.FromResult(new SlackAppManagementFact(SlackAppManagementFactOutcome.Absent, ErrorClass: "not_found"));
            return Task.FromResult(new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Present,
                request.AppId ?? $"A_FAKE_{request.ChildAppId}"));
        }
    }

    public Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _deleteCalls);
        lock (_gate)
        {
            if (_responses.TryGetValue(request.ChildAppId, out var configured) && configured.Delete is not null)
                return Task.FromResult(configured.Delete);
            if (!_owners.TryGetValue(request.ChildAppId, out var owner) || owner != request.EnrollmentId)
                return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: "unauthorized"));
            _owners.Remove(request.ChildAppId);
            return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded, request.AppId));
        }
    }
}

public sealed record FakeSlackAppResponse(
    SlackAppManagementResult? Create = null,
    SlackAppManagementResult? Delete = null,
    SlackAppManagementFact? Inspect = null);
