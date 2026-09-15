using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Runner.Services;

public class RunnerStatusService : IScopedService, IRunnerStatusSource
{
    private readonly IGrainFactory _grainFactory;
    private readonly RunnerConnectionTracker _connectionTracker;
    private readonly TimeProvider _timeProvider;
    private readonly RunnerDefinitionStore? _definitions;
    private readonly IRunnerCredentialStatusReader? _credentials;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);

    public RunnerStatusService(
        IGrainFactory grainFactory,
        RunnerConnectionTracker connectionTracker,
        TimeProvider timeProvider)
        : this(grainFactory, connectionTracker, timeProvider, null, null)
    {
    }

    public RunnerStatusService(
        IGrainFactory grainFactory,
        RunnerConnectionTracker connectionTracker,
        TimeProvider timeProvider,
        RunnerDefinitionStore? definitions,
        IRunnerCredentialStatusReader? credentials)
    {
        _grainFactory = grainFactory;
        _connectionTracker = connectionTracker;
        _timeProvider = timeProvider;
        _definitions = definitions;
        _credentials = credentials;
    }

    /// <summary>
    /// Reads an observational snapshot. A claim may win or lose after this
    /// method reads capacity; RunnerGrain remains the authoritative claim
    /// boundary and this read never reserves a slot.
    /// </summary>
    public async Task<RunnerStatusListSnapshot> GetGlobalRunnersAsync(CancellationToken ct = default)
    {
        if (_definitions is null)
            throw new InvalidOperationException("Global Runner status requires the definition store.");

        var observedAt = _timeProvider.GetUtcNow();
        var definitions = await _definitions.ListAsync(ct);
        var rows = new List<RunnerStatusEntry>(definitions.Count);
        foreach (var definition in definitions)
            rows.Add(await ProjectGlobalRunnerAsync(definition, observedAt, ct));

        return new RunnerStatusListSnapshot(observedAt, rows);
    }

    public async Task<RunnerStatusDetailSnapshot?> GetGlobalRunnerAsync(
        string runnerId,
        CancellationToken ct = default)
    {
        if (_definitions is null || string.IsNullOrWhiteSpace(runnerId))
            return null;

        var observedAt = _timeProvider.GetUtcNow();
        var definition = await _definitions.GetAsync(runnerId, ct);
        return definition is null
            ? null
            : new RunnerStatusDetailSnapshot(
                observedAt,
                await ProjectGlobalRunnerAsync(definition, observedAt, ct));
    }

    public virtual async Task<IReadOnlyList<RunnerStatusView>> GetRunnersAsync(string projectId)
    {
        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

        var eligible = await registry.ListEligibleRunnersAsync(projectId);

        var views = new List<RunnerStatusView>();
        foreach (var info in eligible)
        {
            var view = await ProjectRunnerAsync(info);
            views.Add(view);
        }

        return views;
    }

    public async Task<IReadOnlyList<RunnerStatusView>> GetOnlineRunnersAsync(string projectId)
        => await GetOnlineRunnersAsync(projectId, CancellationToken.None);

    public async Task<IReadOnlyList<RunnerStatusView>> GetOnlineRunnersAsync(string projectId, CancellationToken ct)
    {
        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync(projectId);

        var views = new List<RunnerStatusView>();
        foreach (var info in eligible)
        {
            if (!await IsRunnerOnlineAsync(info.RunnerId))
                continue;

            var view = await ProjectRunnerAsync(info);
            // ProjectRunnerAsync may have observed the grain becoming stale between
            // the filter call and the projection; drop it if the projection no
            // longer has a capacity (runtime was lost between reads).
            if (view.Capacity is null)
                continue;

            views.Add(view);
        }

        return views;
    }

    public async Task<RunnerCapacityView> GetCapacityAsync(string projectId)
    {
        var runners = await GetOnlineRunnersAsync(projectId);
        var used = 0;
        var total = 0;
        foreach (var runner in runners)
        {
            var capacity = runner.Capacity;
            if (capacity is null)
                continue;

            used += capacity.UsedSlots;
            total += capacity.TotalSlots;
        }
        return new RunnerCapacityView(used, total);
    }

    public async Task<RunnerStatusView?> GetRunnerAsync(string projectId, string runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
        {
            return null;
        }

        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync(projectId);
        var info = eligible.FirstOrDefault(r => string.Equals(r.RunnerId, runnerId, StringComparison.Ordinal));
        if (info is null)
        {
            return null;
        }

        return await ProjectRunnerAsync(info);
    }

    private async Task<RunnerStatusEntry> ProjectGlobalRunnerAsync(
        RunnerDefinition definition,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var grain = _grainFactory.GetGrain<IRunnerGrain>(definition.Id);
        RunnerRuntimeState? runtime = null;
        RunnerInfo? info = null;

        try
        {
            runtime = await grain.GetRuntimeStateAsync();
        }
        catch
        {
            // The durable definition remains visible when Runner authority is
            // unavailable. Unknown runtime facts stay unknown in the row.
        }

        try
        {
            info = await grain.GetInfoAsync();
        }
        catch
        {
            // Build and capability details are optional observations.
        }

        var connectionId = _connectionTracker.GetConnectionId(definition.Id);
        var connectionGeneration = _connectionTracker.GetConnectionGeneration(definition.Id);
        var connected = connectionId is not null;
        var presenceState = DerivePresenceState(runtime, observedAt);
        DateTimeOffset? lastObservedAt = runtime?.LastHeartbeatAt is { } heartbeat
            && heartbeat != default
                ? heartbeat
                : null;
        var capacity = runtime is null
            ? new RunnerStatusCapacityView(null, definition.Slots)
            : new RunnerStatusCapacityView(runtime.ActiveWorks.Count, definition.Slots);
        var activeWorks = runtime is null
            ? []
            : ProjectActiveWorks(runtime.ActiveWorks);
        var observation = CurrentObservation(runtime?.DispatchObservation, connectionGeneration);
        var credentialStatus = await ReadCredentialStatusAsync(definition.Id, ct);
        var reasonCodes = DeriveAdmissionReasons(
            runtime,
            presenceState,
            connected,
            observation,
            capacity,
            credentialStatus);
        var runtimes = ProjectRuntimes(info, observation);

        return new RunnerStatusEntry(
            new RunnerIdentityStatusView(
                definition.Id,
                info?.Hostname,
                info?.Kind,
                info?.Component,
                info?.SourceRevision,
                info?.ReleaseId,
                info?.Generation),
            new RunnerPresenceStatusView(presenceState, lastObservedAt),
            new RunnerControlStatusView(connected ? "connected" : "disconnected", connectionGeneration),
            new RunnerAdmissionStatusView(
                reasonCodes.Count == 0 ? "ready" : "blocked",
                reasonCodes),
            info?.Capabilities ?? [],
            runtimes,
            capacity,
            activeWorks,
            ProjectDrain(runtime),
            ProjectNextActions(
                definition.Id,
                info?.Hostname,
                presenceState,
                connected,
                credentialStatus,
                reasonCodes,
                runtimes,
                capacity));
    }

    private async Task<RunnerCredentialStatus> ReadCredentialStatusAsync(
        string runnerId,
        CancellationToken ct)
    {
        if (_credentials is null)
            return RunnerCredentialStatus.Unknown;

        try
        {
            return await _credentials.GetStatusAsync(runnerId, ct);
        }
        catch
        {
            // A credential-store read failure is not evidence of a missing or
            // revoked credential and must not produce re-enrollment guidance.
            return RunnerCredentialStatus.Unknown;
        }
    }

    private static string DerivePresenceState(RunnerRuntimeState? runtime, DateTimeOffset observedAt)
    {
        if (runtime is null || runtime.Status == RunnerStatus.Offline)
            return "offline";

        return observedAt - runtime.LastHeartbeatAt > StaleThreshold
            ? "stale"
            : "online";
    }

    private static RunnerDispatchObservation? CurrentObservation(
        RunnerDispatchObservation? observation,
        string? connectionGeneration)
    {
        if (observation is null
            || string.IsNullOrWhiteSpace(connectionGeneration)
            || !string.Equals(
                observation.ConnectionGeneration,
                connectionGeneration,
                StringComparison.Ordinal))
            return null;

        return observation;
    }

    private static IReadOnlyList<string> DeriveAdmissionReasons(
        RunnerRuntimeState? runtime,
        string presenceState,
        bool connected,
        RunnerDispatchObservation? observation,
        RunnerStatusCapacityView capacity,
        RunnerCredentialStatus credentialStatus)
    {
        var reasons = new List<string>();
        if (presenceState == "offline")
            reasons.Add("presence-offline");
        else if (presenceState == "stale")
            reasons.Add("presence-stale");

        if (credentialStatus == RunnerCredentialStatus.Revoked)
            reasons.Add("credential-revoked");
        else if (credentialStatus == RunnerCredentialStatus.Missing)
            reasons.Add("credential-missing");

        if (!connected)
            reasons.Add("control-disconnected");
        if (runtime?.Draining == true)
            reasons.Add("draining");

        if (runtime is null)
        {
            reasons.Add("admission-observation-missing");
        }
        else if (connected)
        {
            if (observation is null)
                reasons.Add("admission-observation-missing");
            else if (!observation.AdmissionReady)
                reasons.AddRange(observation.AdmissionReasonCodes);
        }

        if (capacity.Used is { } used && used >= capacity.Total)
            reasons.Add("capacity-full");

        return reasons;
    }

    private static IReadOnlyList<RunnerRuntimeStatusView> ProjectRuntimes(
        RunnerInfo? info,
        RunnerDispatchObservation? observation)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (info?.RuntimeCatalogs is { } runtimeCatalogs)
        {
            foreach (var name in runtimeCatalogs.Keys)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        foreach (var witness in observation?.RuntimeReadiness ?? [])
        {
            if (!string.IsNullOrWhiteSpace(witness.Runtime))
                names.Add(witness.Runtime);
        }

        var witnesses = (observation?.RuntimeReadiness ?? [])
            .ToDictionary(witness => witness.Runtime, StringComparer.OrdinalIgnoreCase);
        var catalogs = info?.RuntimeCatalogs ?? new Dictionary<string, RuntimeCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        return names
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                catalogs.TryGetValue(name, out var catalog);
                witnesses.TryGetValue(name, out var witness);
                var ready = witness is { Ready: true, Generation: > 0 };
                var readiness = new RunnerRuntimeReadinessStatusView(
                    ready ? "ready" : "not-ready",
                    ready ? witness!.Generation : null,
                    ready ? null : witness is { Generation: > 0 }
                        ? "runtime-reported-not-ready"
                        : "runtime-witness-missing");
                return new RunnerRuntimeStatusView(
                    name,
                    readiness,
                    catalog is null ? null : ProjectCatalog(catalog));
            })
            .ToList();
    }

    private static RunnerRuntimeCatalogStatusView ProjectCatalog(RuntimeCatalogEntry catalog)
    {
        var models = catalog.Models ?? [];
        var variants = (catalog.Variants ?? new Dictionary<string, string[]>(StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
        var reasoningEfforts = (catalog.ReasoningEfforts ?? new Dictionary<string, string[]>(StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
        return new RunnerRuntimeCatalogStatusView(
            catalog.Complete,
            catalog.CapabilityRevision,
            models.Length,
            models,
            variants,
            catalog.SupportsReasoningEffort,
            reasoningEfforts);
    }

    private static RunnerDrainStatusView? ProjectDrain(RunnerRuntimeState? runtime) =>
        runtime?.Draining == true
            ? new RunnerDrainStatusView(
                true,
                string.IsNullOrWhiteSpace(runtime.UpdateInterruptId) ? "generic" : "update",
                runtime.UpdateInterruptId)
            : null;

    private static IReadOnlyList<RunnerNextActionView> ProjectNextActions(
        string runnerId,
        string? hostname,
        string presenceState,
        bool connected,
        RunnerCredentialStatus credentialStatus,
        IReadOnlyList<string> reasonCodes,
        IReadOnlyList<RunnerRuntimeStatusView> runtimes,
        RunnerStatusCapacityView capacity)
    {
        if (credentialStatus is RunnerCredentialStatus.Revoked or RunnerCredentialStatus.Missing)
        {
            return [new RunnerNextActionView(
                "reenroll-runner",
                "Re-enroll the Runner credential.",
                $"mo install runner --repo-root <path> --runner-id {runnerId}")];
        }

        if (credentialStatus == RunnerCredentialStatus.Active
            && (presenceState is "offline" or "stale" || !connected))
        {
            return [new RunnerNextActionView(
                "start-runner",
                string.IsNullOrWhiteSpace(hostname)
                    ? "Start the Runner process."
                    : $"Start the Runner process on {hostname}.",
                "mo service start runner")];
        }

        var actions = new List<RunnerNextActionView>();
        if (reasonCodes.Contains("draining", StringComparer.Ordinal))
        {
            actions.Add(new RunnerNextActionView(
                "wait-for-drain",
                "Wait for the active drain to finish.",
                null));
        }
        if (capacity.Used is { } used && used >= capacity.Total)
        {
            actions.Add(new RunnerNextActionView(
                "wait-for-capacity",
                "Wait for the active owner to release a Runner slot.",
                null));
        }
        if (runtimes.Any(runtime => runtime.Readiness.State == "not-ready"))
        {
            actions.Add(new RunnerNextActionView(
                "wait-for-runtime",
                "Wait for the Runtime to report ready.",
                null));
        }

        foreach (var reason in reasonCodes)
        {
            var action = reason switch
            {
                "provider-policy-invalid" => new RunnerNextActionView(
                    "fix-provider-policy-invalid",
                    "Fix the Runner provider policy before accepting new work.",
                    null),
                "runtime-event-queue-unavailable" => new RunnerNextActionView(
                    "fix-runtime-event-queue-unavailable",
                    "Restore the Runtime event queue before accepting new work.",
                    null),
                "admission-observation-invalid" => new RunnerNextActionView(
                    "fix-admission-observation-invalid",
                    "Refresh the Runner admission observation.",
                    null),
                "admission-observation-missing" => new RunnerNextActionView(
                    "wait-for-admission-observation",
                    "Wait for the Runner admission observation.",
                    null),
                _ => null,
            };
            if (action is not null && actions.All(existing => existing.Code != action.Code))
                actions.Add(action);
        }

        return actions;
    }

    private async Task<bool> IsRunnerOnlineAsync(string runnerId)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerGrain>(runnerId);
        try
        {
            var runtime = await runnerGrain.GetRuntimeStateAsync();
            return runtime.Status == RunnerStatus.Online;
        }
        catch
        {
            return false;
        }
    }

    private async Task<RunnerStatusView> ProjectRunnerAsync(RunnerInfo info)
    {
        var runnerGrain = _grainFactory.GetGrain<IRunnerGrain>(info.RunnerId);
        RunnerRuntimeState? runtime = null;
        int? slots = null;
        try
        {
            runtime = await runnerGrain.GetRuntimeStateAsync();
            // Slots come from the runner grain's persisted definition state.
            slots = await runnerGrain.GetSlotsAsync();
        }
        catch
        {
            // Grain may be deactivated or unavailable
        }

        var now = _timeProvider.GetUtcNow();
        var connectionState = DeriveConnectionState(info.RunnerId);
        var status = DeriveStatus(info, runtime, connectionState, now);

        // Runners are global execution resources: the
        // RunnerInfo.ProjectId field is preserved on the wire for runner-line
        // compatibility but does not bind the runner to any project. The
        // scope view is therefore always "global".
        var scope = new RunnerScopeView("global");

        var activeWorkflowCount = runtime is not null
            ? runtime.ActiveWorks
                .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow)
                .Select(w => w.OwnerId)
                .Distinct(StringComparer.Ordinal)
                .Count()
            : 0;

        var capacity = runtime is not null && slots.HasValue
            ? new RunnerCapacityView(activeWorkflowCount, slots.Value)
            : null;

        var activeWorks = ProjectActiveWorks(runtime?.ActiveWorks);

        return new RunnerStatusView(
            info.RunnerId,
            info.Kind,
            info.Hostname,
            scope,
            status,
            info.RegisteredAt,
            runtime?.LastHeartbeatAt,
            connectionState,
            info.Capabilities,
            info.CoderModels ?? [],
            info.CoderModels?.Length ?? 0,
            capacity,
            activeWorks,
            info.BuildGitHash);
    }

    private static IReadOnlyList<RunnerActiveWorkView> ProjectActiveWorks(IReadOnlyList<RunnerActiveWorkItem>? activeWorks)
    {
        if (activeWorks is null || activeWorks.Count == 0)
        {
            return [];
        }

        var views = new List<RunnerActiveWorkView>(activeWorks.Count);
        foreach (var work in activeWorks)
        {
            views.Add(new RunnerActiveWorkView(
                work.WorkId,
                work.OwnerKind,
                work.OwnerId,
                work.WorkType,
                work.Stage,
                work.Title,
                work.Issue is null
                    ? null
                    : new RunnerActiveWorkIssueView(work.Issue.ProjectId, work.Issue.IssueNumber)));
        }
        return views;
    }

    private string DeriveStatus(RunnerInfo info, RunnerRuntimeState? runtime, string connectionState, DateTimeOffset now)
    {
        if (runtime is null)
            return "offline";

        if (runtime.Status == RunnerStatus.Offline)
            return "offline";

        var elapsed = now - runtime.LastHeartbeatAt;
        if (elapsed > StaleThreshold)
            return "stale";

        if (runtime.ActiveWorks.Count > 0)
            return "busy";

        var requiresLiveConnection = info.Capabilities.Contains("workspace-query", StringComparer.OrdinalIgnoreCase);
        if (requiresLiveConnection && !string.Equals(connectionState, "connected", StringComparison.Ordinal))
            return "offline";

        return "idle";
    }

    private string DeriveConnectionState(string runnerId)
    {
        var connectionId = _connectionTracker.GetConnectionId(runnerId);
        return connectionId is not null ? "connected" : "disconnected";
    }
}
