namespace Mohist.Server.Runner.Grains;

[GenerateSerializer]
public sealed class RunnerState
{
    [Id(0)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(1)] public string? LastKnownActionCatalogJson { get; set; }
    [Id(2)] public RunnerUpdateInterruptFence? UpdateInterruptFence { get; set; }
    [Id(3)] public string? CurrentProcessGeneration { get; set; }
    [Id(4)] public string? PendingProcessGeneration { get; set; }
    [Id(5)] public string? ClosingProcessGeneration { get; set; }
    /// <summary>
    /// Absolute UTC expiry of the current two-minute presence lease. Missing
    /// legacy state is offline until real runner traffic renews presence.
    /// </summary>
    [Id(6)] public DateTimeOffset? PresenceLeaseExpiresAt { get; set; }
    /// <summary>Latest presence touch; retained after the lease is cleared.</summary>
    [Id(7)] public DateTimeOffset? LastPresenceAt { get; set; }
    /// <summary>
    /// The one host-environment application owned by this Runner. Raw
    /// snapshot values remain on the host; this record is only control-plane
    /// metadata and activation evidence.
    /// </summary>
    [Id(8)] public RunnerEnvironmentApplication? EnvironmentApplication { get; set; }
    /// <summary>
    /// Latest bounded candidate/tool observation. Raw host values and command
    /// output never enter durable Runner state.
    /// </summary>
    [Id(9)] public RunnerEnvironmentObservation? EnvironmentObservation { get; set; }
    /// <summary>
    /// Durable administrative authority-removal operation. It remains after
    /// settlement until a request presents a different active credential for
    /// the same Runner.
    /// </summary>
    [Id(10)] public RunnerAdministrativeRemoval? AdministrativeRemoval { get; set; }
    /// <summary>
    /// Exact issued credential that admitted <see cref="CurrentProcessGeneration"/>.
    /// Null means the process was admitted through an explicit operator override.
    /// </summary>
    [Id(11)] public string? CurrentRegistrationCredentialId { get; set; }
}

[GenerateSerializer]
public sealed class RunnerAdministrativeRemoval
{
    [Id(0)] public string RemovalId { get; set; } = string.Empty;
    [Id(1)] public DateTimeOffset RevokedAt { get; set; }
    [Id(2)] public RunnerAdministrativeRemovalPhase Phase { get; set; }
    [Id(3)] public string? RemovedProcessGeneration { get; set; }
    /// <summary>The exact credential authority invalidated by this operation.</summary>
    [Id(4)] public string? RemovedCredentialId { get; set; }
}

[GenerateSerializer]
public enum RunnerAdministrativeRemovalPhase
{
    IntentRecorded,
    CredentialRevoked,
    AuthorityFenced,
    SessionsSettling,
    Completed,
}

/// <summary>
/// The one update-owned admission fence for a Runner. It deliberately carries
/// no work state: a fence only protects the update handoff and cannot settle
/// or replace execution owned by Workflow or AgentJob aggregates.
/// </summary>
[GenerateSerializer]
public sealed class RunnerUpdateInterruptFence
{
    [Id(0)] public string? PendingId { get; set; }
    [Id(1)] public string? LastCancelledId { get; set; }
    /// <summary>
    /// Null is the legacy release-updater fence. Environment applications use
    /// a distinct kind so a replacement process cannot release their fence
    /// before activation is confirmed.
    /// </summary>
    [Id(2)] public string? Kind { get; set; }
}

[GenerateSerializer]
public sealed class LegacyRunnerRegistrationState
{
    [Id(1)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(2)] public string? LastKnownActionCatalogJson { get; set; }
}
