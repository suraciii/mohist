namespace Mohist.Server.Sessions.Domain;

public union AgentSessionEvent(
    AgentSessionStarted,
    AgentSessionActivated,
    AgentSessionUsageRecorded,
    AgentSessionModelChanged,
    AgentSessionCompleted,
    AgentSessionFailed,
    AgentSessionCancelled,
    AgentSessionStatusChanged);

public sealed record AgentSessionStarted(string AgentRuntimeSessionId);
public sealed record AgentSessionActivated(string Status);
public sealed record AgentSessionUsageRecorded(AgentUsageSummary Usage);
public sealed record AgentSessionModelChanged(string? Model);
public sealed record AgentSessionCompleted(int? ExitCode);
public sealed record AgentSessionFailed(string? Reason, int? ExitCode);
public sealed record AgentSessionCancelled(string? Reason, int? ExitCode);
public sealed record AgentSessionStatusChanged(string Status, string? FailureReason);
