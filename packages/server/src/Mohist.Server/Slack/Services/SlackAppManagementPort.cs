using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

public interface ISlackAppManagementPort
{
    Task<SlackAppManagementResult> ValidateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default);
    Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default);
    Task<SlackAppManagementResult> UpdateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default);
    Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default);
}

public interface ISlackAppManagementFactPort
{
    Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default);
    Task<SlackAppManifestExport> ExportManifestAsync(SlackAppManagementRequest request, CancellationToken ct = default);
}

public sealed record SlackAppManagementRequest(string EnrollmentId, string AgentAppId, string WorkspaceTeamId, string? AppId = null);

public sealed record SlackAppManifestRequest(SlackAppManagementRequest App, SlackManifest Manifest);

public sealed record SlackAppManifestExport(
    SlackAppManagementFactOutcome Outcome,
    string? ManifestJson = null,
    string? ErrorClass = null);

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
    string? InstallUrl = null,
    string? ErrorClass = null,
    string? ErrorMessage = null,
    string? ClientSecret = null,
    string? SigningSecret = null);

public enum SlackAppManagementOutcome
{
    Succeeded,
    DefiniteFailure,
    Unknown,
}

public sealed class UnavailableSlackAppManagementPort : ISlackAppManagementPort, ISlackAppManagementFactPort, IScopedService
{
    public Task<SlackAppManagementResult> ValidateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager manifest validation is not connected in this slice.");

    public Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager app management is not connected in this slice.");

    public Task<SlackAppManagementResult> UpdateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager manifest update is not connected in this slice.");

    public Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager app management is not connected in this slice.");

    public Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager app inspection is not connected in this slice.");

    public Task<SlackAppManifestExport> ExportManifestAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack Manager manifest export is not connected in this slice.");
}

public sealed class FakeSlackAppManagementPort : ISlackAppManagementPort, ISlackAppManagementFactPort
{
    private readonly Dictionary<string, FakeSlackAppResponse> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _managedAppLimit = int.MaxValue;
    private int _createCalls;
    private int _deleteCalls;
    private int _manifestCalls;

    public int CreateCalls => Volatile.Read(ref _createCalls);
    public int DeleteCalls => Volatile.Read(ref _deleteCalls);
    public int ManifestCalls => Volatile.Read(ref _manifestCalls);

    public void SetResponse(string agentAppId, FakeSlackAppResponse response)
    {
        lock (_gate) _responses[agentAppId] = response;
    }

    public void SetManagedAppLimit(int limit) => _managedAppLimit = limit;

    public Task<SlackAppManagementResult> ValidateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _manifestCalls);
        return Task.FromResult(ResponseFor(request.App.AgentAppId).Validate
            ?? new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded));
    }

    public Task<SlackAppManagementResult> CreateAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _createCalls);
        lock (_gate)
        {
            if (_responses.TryGetValue(request.AgentAppId, out var configured) && configured.Create is not null)
                return Task.FromResult(configured.Create);
            if (_owners.Values.Count(owner => owner == request.EnrollmentId) >= _managedAppLimit)
                return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: "managed_app_limit_reached"));
            if (_owners.TryGetValue(request.AgentAppId, out var owner) && owner != request.EnrollmentId)
                return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: "unauthorized"));
            var appId = string.IsNullOrEmpty(request.AppId) ? $"A_FAKE_{request.AgentAppId}" : request.AppId;
            _owners[request.AgentAppId] = request.EnrollmentId;
            return Task.FromResult(new SlackAppManagementResult(
                SlackAppManagementOutcome.Succeeded,
                appId,
                InstallUrl: $"https://fake.slack.com/install/{appId}",
                ClientSecret: $"xoxc-fake-{appId}",
                SigningSecret: $"sig-fake-{appId}"));
        }
    }

    public Task<SlackAppManagementFact> InspectAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_responses.TryGetValue(request.AgentAppId, out var configured) && configured.Inspect is not null)
                return Task.FromResult(configured.Inspect);
            if (!_owners.TryGetValue(request.AgentAppId, out var owner) || owner != request.EnrollmentId)
                return Task.FromResult(new SlackAppManagementFact(SlackAppManagementFactOutcome.Absent, ErrorClass: "not_found"));
            return Task.FromResult(new SlackAppManagementFact(
                SlackAppManagementFactOutcome.Present,
                request.AppId ?? $"A_FAKE_{request.AgentAppId}"));
        }
    }

    public Task<SlackAppManagementResult> UpdateManifestAsync(SlackAppManifestRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _manifestCalls);
        return Task.FromResult(ResponseFor(request.App.AgentAppId).Update
            ?? new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded, request.App.AppId));
    }

    public Task<SlackAppManifestExport> ExportManifestAsync(SlackAppManagementRequest request, CancellationToken ct = default) =>
        Task.FromResult(ResponseFor(request.AgentAppId).Export
            ?? new SlackAppManifestExport(SlackAppManagementFactOutcome.Absent, ErrorClass: "not_found"));

    public Task<SlackAppManagementResult> DeleteAsync(SlackAppManagementRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _deleteCalls);
        lock (_gate)
        {
            if (_responses.TryGetValue(request.AgentAppId, out var configured) && configured.Delete is not null)
                return Task.FromResult(configured.Delete);
            if (!_owners.TryGetValue(request.AgentAppId, out var owner) || owner != request.EnrollmentId)
                return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.DefiniteFailure, ErrorClass: "unauthorized"));
            _owners.Remove(request.AgentAppId);
            return Task.FromResult(new SlackAppManagementResult(SlackAppManagementOutcome.Succeeded, request.AppId));
        }
    }

    private FakeSlackAppResponse ResponseFor(string agentAppId)
    {
        lock (_gate)
            return _responses.TryGetValue(agentAppId, out var response) ? response : new();
    }
}

public sealed record FakeSlackAppResponse(
    SlackAppManagementResult? Create = null,
    SlackAppManagementResult? Delete = null,
    SlackAppManagementFact? Inspect = null,
    SlackAppManagementResult? Validate = null,
    SlackAppManagementResult? Update = null,
    SlackAppManifestExport? Export = null);
