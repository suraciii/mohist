using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Tests.Support;

internal static class EventStoreTestExtensions
{
    public static Task AppendTestWorkflowEventAsync(
        this IEventStore store,
        string workflowRunId,
        WorkflowEvent e) =>
        store.AppendWorkflowEventAsync(workflowRunId, e);
}
