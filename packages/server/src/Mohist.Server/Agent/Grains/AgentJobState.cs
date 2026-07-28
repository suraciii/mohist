namespace Mohist.Server.Agent.Grains;

[GenerateSerializer]
public sealed class AgentJobState
{
    [Id(0)] public AgentJobStatus Status { get; set; } = AgentJobStatus.Pending;
    [Id(1)] public string? RunnerId { get; set; }
    [Id(2)] public string? WorkId { get; set; }
    [Id(3)] public string? FailureReason { get; set; }
    [Id(4)] public AgentJobTerminalResult? TerminalResult { get; set; }
    [Id(5)] public AgentJobInput? Input { get; set; }
    [Id(6)] public DateTimeOffset? SubmittedAt { get; set; }
    [Id(7)] public DateTimeOffset? RunningSince { get; set; }
    [Id(8)] public TimeSpan NextDispatchDelay { get; set; }
    [Id(9)] public int DispatchAttempts { get; set; }
    [Id(10)] public string? AgentConfigJson { get; set; }
    [Id(11)] public bool RunnerAccepted { get; set; }
    [Id(12)] public string? RuntimeSessionId { get; set; }
    [Id(13)] public PendingSessionClose? PendingSessionClose { get; set; }
    [Id(14)] public RoutedAgentLaunchPlan? RoutedPlan { get; set; }
    [Id(15)] public bool LaunchReady { get; set; }
    [Id(16)] public DateTimeOffset? TerminalAt { get; set; }
    [Id(17)] public PendingFailureEvent? PendingFailureEvent { get; set; }
    /// <summary>
    /// Durable record of the manual-launch preparation command the
    /// coordinator used to materialise this job. Populated by
    /// <see cref="IAgentJobGrain.PrepareManualLaunchAsync"/>; the
    /// canonical <see cref="Input"/> is built from this snapshot so
    /// reminder-driven recovery can re-derive the same args verbatim.
    /// </summary>
    [Id(18)] public PrepareManualLaunchCommand? ManualPlan { get; set; }
    [Id(19)] public PendingTerminalDeliveryEvent? PendingTerminalDeliveryEvent { get; set; }
    [Id(20)] public string? ConcurrencyPermitToken { get; set; }
    [Id(21)] public bool ConcurrencyPermitHeld { get; set; }
}
