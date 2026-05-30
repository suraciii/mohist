namespace Mohist.Server.Sessions.Domain;

public enum AgentSessionStatus
{
    Created,
    Running,
    Probing,
    Completed,
    Failed,
    Cancelled,
}

public static class AgentSessionStatusNames
{
    public static string ToName(AgentSessionStatus status) => status switch
    {
        AgentSessionStatus.Created => "created",
        AgentSessionStatus.Running => "running",
        AgentSessionStatus.Probing => "probing",
        AgentSessionStatus.Completed => "completed",
        AgentSessionStatus.Failed => "failed",
        AgentSessionStatus.Cancelled => "cancelled",
        _ => "created",
    };

    public static AgentSessionStatus Parse(string status) => status switch
    {
        "running" => AgentSessionStatus.Running,
        "probing" => AgentSessionStatus.Probing,
        "completed" => AgentSessionStatus.Completed,
        "failed" => AgentSessionStatus.Failed,
        "cancelled" => AgentSessionStatus.Cancelled,
        _ => AgentSessionStatus.Created,
    };

    public static AgentSessionStatus ParseActive(string status) => status == "probing"
        ? AgentSessionStatus.Probing
        : AgentSessionStatus.Running;

    public static bool TryParse(string status, out AgentSessionStatus result)
    {
        result = Parse(status);
        return true;
    }
}
