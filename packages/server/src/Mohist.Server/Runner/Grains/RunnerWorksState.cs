using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Runner.Grains;


/// <summary>
/// Status of a work item tracked by the runner grain.
/// <list type="bullet">
/// <item><term>Pending</term><description>Assigned but not yet picked up (agent-job works)</description></item>
/// <item><term>Running</term><description>Offered to the runner and reoffered until the process reports the stable work key</description></item>
/// </list>
/// </summary>
[GenerateSerializer]
public enum RunnerWorkStatus
{
    Pending,
    Running
}

/// <summary>
/// A single work record tracked by the runner grain. Persisted via
/// Orleans grain storage so the outstanding-work set survives server
/// restarts and grain deactivation.
/// </summary>
[GenerateSerializer]
public sealed class RunnerWork
{
    [Id(0)] public string WorkId { get; set; } = "";
    [Id(1)] public string OwnerKind { get; set; } = "";
    [Id(2)] public string OwnerId { get; set; } = "";
    [Id(3)] public string? WorkType { get; set; }
    [Id(4)] public string? Stage { get; set; }
    [Id(5)] public string? Title { get; set; }
    [Id(6)] public WorkIssueRef? Issue { get; set; }
    [Id(7)] public RunnerWorkStatus Status { get; set; }
    [Id(8)] public DateTimeOffset CreatedAt { get; set; }
    [Id(9)] public DateTimeOffset? StartedAt { get; set; }
    /// <summary>
    /// For agent-job works only: a snapshot of the dispatch envelope
    /// provided by the agent job grain. Needed to return the full
    /// dispatch to the runner on dequeue since agent jobs have no backing
    /// workflow grain to reconstruct from. Workflow works live on the run
    /// and are re-rendered by the stateless DispatchService, so they carry
    /// no snapshot here.
    /// </summary>
    [Id(10)] public WorkDispatch? DispatchSnapshot { get; set; }
}

/// <summary>
/// Orleans persistent state for the runner's durable Agent work and the last
/// registration profile needed to restore poll-driven presence after activation.
/// </summary>
[GenerateSerializer]
public sealed class RunnerWorksState
{
    [Id(0)] public List<RunnerWork> Works { get; set; } = [];
    [Id(1)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(2)] public string? LastKnownActionCatalogJson { get; set; }
}
