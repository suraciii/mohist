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
}
