namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowSessionGrain : IGrainWithStringKey
{
    Task RegisterAsync(string acpSessionId, string workDir);
    Task<WorkflowSessionEntry?> GetAsync();
}

[GenerateSerializer]
public record WorkflowSessionEntry(
    [property: Id(0)] string AcpSessionId,
    [property: Id(1)] string WorkDir,
    [property: Id(2)] string RegisteredAt);

[GenerateSerializer]
public record WorkflowSessionGrainState(
    [property: Id(0)] WorkflowSessionEntry? Entry);
