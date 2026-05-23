using Mohist.Runner.Transport;

namespace Mohist.Runner;

public interface IWorkExecutor
{
    Task<WorkItemResult> ExecuteAsync(WorkItem workItem, CancellationToken ct);
}
