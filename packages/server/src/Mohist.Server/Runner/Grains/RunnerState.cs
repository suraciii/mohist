namespace Mohist.Server.Runner.Grains;

[GenerateSerializer]
public sealed class RunnerState
{
    [Id(0)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(1)] public string? LastKnownActionCatalogJson { get; set; }
    [Id(2)] public RunnerUpdateInterruptFence? UpdateInterruptFence { get; set; }
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
}

[GenerateSerializer]
public sealed class LegacyRunnerRegistrationState
{
    [Id(1)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(2)] public string? LastKnownActionCatalogJson { get; set; }
}
