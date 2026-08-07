namespace Mohist.Server.Agent.Grains;

public interface IAgentLaunchCoordinatorGrain : IGrainWithStringKey, IRemindable
{
    Task<AgentLaunchCoordinatorResult?> ResumeAsync(AgentLaunchCoordinatorRequest request);
    Task<AgentLaunchCoordinatorResult?> ResumeExistingSpawnAsync(string spawnRequestFingerprint);
    Task<AgentLaunchCoordinatorResult> LaunchAsync(AgentLaunchCoordinatorCommandEnvelope command);
}
