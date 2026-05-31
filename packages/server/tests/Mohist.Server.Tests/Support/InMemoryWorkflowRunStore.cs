using System.Collections.Concurrent;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure.Persistence.Workflow;

namespace Mohist.Server.Tests.Support;

public class InMemoryWorkflowRunStore : IWorkflowRunStore
{
    private readonly ConcurrentDictionary<string, WorkflowRun> _data = new();

    public Task SaveAsync(WorkflowRun run)
    {
        _data[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> LoadAsync(string workflowRunId)
    {
        _data.TryGetValue(workflowRunId, out var value);
        return Task.FromResult(value);
    }
}
