namespace Mohist.Server.Infrastructure.Data.Workflow;

[GenerateSerializer]
public sealed record WorkflowBacklogState(
    [property: Id(0)] List<string> Waiting,
    [property: Id(1)] HashSet<string> All);
