namespace Mohist.Server.Sessions.Services;

public sealed record SessionWorkflowWorkBinding(
    string WorkflowRunId,
    string RunnerId,
    string WorkId);

public interface ISessionWorkPort
{
    Task AbandonActiveWorkAsync(
        SessionWorkflowWorkBinding binding,
        string reason,
        CancellationToken cancellationToken = default);
}
