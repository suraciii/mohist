using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

public interface IWorkflowRunCompletedHandler
{
    Task HandleAsync(CloudEvent evt, CancellationToken ct = default);
}

public interface IWorkflowRunStoppedHandler
{
    Task HandleAsync(CloudEvent evt, CancellationToken ct = default);
}

public interface IWorkflowRunFailedHandler
{
    Task HandleAsync(CloudEvent evt, CancellationToken ct = default);
}

public interface IRunnerDisconnectedHandler
{
    Task HandleAsync(CloudEvent evt, CancellationToken ct = default);
}
