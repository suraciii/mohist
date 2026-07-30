namespace Mohist.Server.Sessions.Services;

public interface IAgentSessionStopClaimRegistry
{
    IDisposable Register(string sessionId, string turnId);
    bool IsActive(string sessionId, string turnId);
}
