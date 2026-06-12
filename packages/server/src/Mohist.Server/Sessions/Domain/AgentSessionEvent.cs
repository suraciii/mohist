namespace Mohist.Server.Sessions.Domain;

public union AgentSessionEvent(
    AgentSessionRuntimeBound,
    AgentSessionUsageRecorded,
    AgentSessionModelChanged);

public sealed record AgentSessionRuntimeBound(string AgentRuntimeSessionId);
public sealed record AgentSessionUsageRecorded(AgentUsageSummary Usage);
public sealed record AgentSessionModelChanged(string? Model);
