namespace Mohist.Server.Agent.Grains;

[GenerateSerializer]
public sealed class AgentLaunchCoordinatorState
{
    [Id(0)] public AgentLaunchCoordinatorPlan? Plan { get; set; }
}
