namespace Mohist.Server.Runner.Domain;

public sealed record TerminalLogOwnership(
    string OwnerKind,
    string OwnerId,
    string WorkId,
    string RunnerId);

public static class TerminalLogOwnerKinds
{
    public const string Workflow = "workflow";
    public const string AgentJob = "agent-job";
}
