namespace Mohist.Server.Sessions.Domain;

public enum AgentSessionStatus
{
    Opened,
    Bound,
}

public static class AgentSessionStatusNames
{
    public static string ToName(AgentSessionStatus status) => status switch
    {
        AgentSessionStatus.Bound => "bound",
        _ => "opened",
    };

    public static AgentSessionStatus Parse(string status) => status switch
    {
        "bound" or "running" or "probing" or "completed" or "failed" or "cancelled" => AgentSessionStatus.Bound,
        _ => AgentSessionStatus.Opened,
    };

    public static bool TryParse(string status, out AgentSessionStatus result)
    {
        result = Parse(status);
        return true;
    }
}
