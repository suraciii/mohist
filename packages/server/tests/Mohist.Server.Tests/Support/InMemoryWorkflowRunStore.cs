using System.Collections.Concurrent;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

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

    public Task<IReadOnlyList<WorkflowRun>> ListByProjectAsync(string projectId, WorkflowRunPhase? phaseFilter = null)
    {
        var query = _data.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(projectId))
            query = query.Where(r => r.Metadata.Annotations?.TryGetValue("projectId", out var pid) == true && pid == projectId);
        if (phaseFilter.HasValue)
            query = query.Where(r => r.Phase == phaseFilter.Value);
        IReadOnlyList<WorkflowRun> result = query.ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListByDefinitionAsync(string definitionId, WorkflowRunPhase? phaseFilter = null)
    {
        var query = _data.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(definitionId))
            query = query.Where(r => r.Metadata.Annotations?.TryGetValue("definitionId", out var did) == true && did == definitionId);
        if (phaseFilter.HasValue)
            query = query.Where(r => r.Phase == phaseFilter.Value);
        IReadOnlyList<WorkflowRun> result = query.ToList();
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string workflowRunId)
    {
        _data.TryRemove(workflowRunId, out _);
        return Task.CompletedTask;
    }
}
