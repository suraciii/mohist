namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionPersistenceTimerOptions
{
    public TimeSpan DueTime { get; set; } = TimeSpan.FromMilliseconds(200);
}
