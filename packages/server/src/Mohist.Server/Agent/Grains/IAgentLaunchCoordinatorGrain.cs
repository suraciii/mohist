namespace Mohist.Server.Agent.Grains;

public interface IAgentLaunchCoordinatorGrain : IGrainWithStringKey, IRemindable
{
    Task<AgentLaunchCoordinatorResult> LaunchAsync(AgentLaunchCoordinatorCommandEnvelope command);
}
