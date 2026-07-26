namespace Mohist.Server.Infrastructure;

public interface IAgentRuntimeOverrideResolver
{
    Task<string?> GetAgentRuntimeOverrideAsync(string projectId, int issueNumber);
}
