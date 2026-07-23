namespace Mohist.Server.Sessions.Services;

public interface IAgentSessionConnectionRegistry
{
    void RegisterSession(string runnerId, string sessionId);
}
