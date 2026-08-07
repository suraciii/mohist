using System.Collections.Concurrent;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// In-memory <see cref="IAgentWorkspaceMaterializer"/> for spawn state-machine
/// specs. Records every materialize/release invocation and lets a spec pin the
/// outcome (materialized path/identity, rejection reason, release result,
/// Unknown). No real network or filesystem: the managed-worktree contract is
/// exercised purely through the coordinator's durable state machine.
/// </summary>
public sealed class FakeAgentWorkspaceMaterializer : IAgentWorkspaceMaterializer
{
    private readonly ConcurrentQueue<MaterializeInvocation> _materializeCalls = new();
    private readonly ConcurrentQueue<ReleaseInvocation> _releaseCalls = new();

    public Func<MaterializeInvocation, MaterializeAgentWorkspaceResult>? MaterializeOutcome { get; set; }
    public AgentWorkspaceReleaseOutcome ReleaseOutcome { get; set; } = AgentWorkspaceReleaseOutcome.Released;

    public IReadOnlyList<MaterializeInvocation> MaterializeCalls => [.. _materializeCalls];
    public IReadOnlyList<ReleaseInvocation> ReleaseCalls => [.. _releaseCalls];

    public Task<MaterializeAgentWorkspaceResult> MaterializeAsync(
        string runnerId,
        MaterializeAgentWorkspaceRequest request,
        CancellationToken ct = default)
    {
        var invocation = new MaterializeInvocation(
            runnerId,
            request.ProjectId,
            request.ChildSessionId,
            request.ParentWorkDir,
            request.Repository.Name,
            request.Repository.GitUrl,
            request.Repository.BaseBranch);
        _materializeCalls.Enqueue(invocation);
        var outcome = MaterializeOutcome?.Invoke(invocation)
            ?? DefaultMaterialized(invocation);
        return Task.FromResult(outcome);
    }

    public Task<ReleaseAgentWorkspaceResult> ReleaseAsync(
        string runnerId,
        ReleaseAgentWorkspaceRequest request,
        CancellationToken ct = default)
    {
        _releaseCalls.Enqueue(new ReleaseInvocation(
            runnerId,
            request.ChildSessionId,
            request.WorkspaceIdentity));
        return Task.FromResult(new ReleaseAgentWorkspaceResult(ReleaseOutcome));
    }

    public void Reset()
    {
        while (_materializeCalls.TryDequeue(out _)) { }
        while (_releaseCalls.TryDequeue(out _)) { }
        MaterializeOutcome = null;
        ReleaseOutcome = AgentWorkspaceReleaseOutcome.Released;
    }

    private static MaterializeAgentWorkspaceResult DefaultMaterialized(MaterializeInvocation invocation) =>
        new(
            AgentWorkspaceMaterializeOutcome.Materialized,
            WorkspaceIdentity: $"agent-wt:{invocation.ChildSessionId}",
            WorkDir: $"/runner-root/agent-workspaces/{invocation.ChildSessionId}");

    public sealed record MaterializeInvocation(
        string RunnerId,
        string ProjectId,
        string ChildSessionId,
        string ParentWorkDir,
        string RepositoryName,
        string GitUrl,
        string BaseBranch);

    public sealed record ReleaseInvocation(
        string RunnerId,
        string ChildSessionId,
        string WorkspaceIdentity);
}
