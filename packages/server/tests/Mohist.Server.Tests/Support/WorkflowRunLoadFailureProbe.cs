using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Tests.Support;

internal sealed class WorkflowRunLoadFailureProbe
{
    private readonly ConcurrentDictionary<string, byte> _workflowRunIds = new(StringComparer.Ordinal);

    public void FailLoadsFor(string workflowRunId) =>
        _workflowRunIds.TryAdd(workflowRunId, 0);

    public void RestoreLoadsFor(string workflowRunId) =>
        _workflowRunIds.TryRemove(workflowRunId, out _);

    public bool ShouldFail(string workflowRunId) =>
        _workflowRunIds.ContainsKey(workflowRunId);
}

internal sealed class FaultInjectingWorkflowRunStore(
    WorkflowRunStore inner,
    WorkflowRunLoadFailureProbe failures) : IWorkflowRunStore
{
    public Task SaveAsync(WorkflowRun run, CancellationToken ct = default) =>
        inner.SaveAsync(run, ct);

    public Task SaveAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowEvent> events,
        CancellationToken ct = default) =>
        inner.SaveAsync(run, events, ct);

    public Task SaveWithArtifactsAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowEvent> events,
        WorkflowArtifactBindingIntent artifacts,
        CancellationToken ct = default) =>
        inner.SaveWithArtifactsAsync(run, events, artifacts, ct);

    public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default)
    {
        if (failures.ShouldFail(workflowRunId))
        {
            throw new InvalidOperationException(
                "Failed to deserialize workflow run state: injected corrupt state");
        }

        return inner.LoadAsync(workflowRunId, ct);
    }

    public Task DeleteAsync(string workflowRunId, CancellationToken ct = default) =>
        inner.DeleteAsync(workflowRunId, ct);
}
