using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Test-only helpers that route the old runner-grain verbs (poll, report)
/// through the reconciliation model. The runner grain no longer owns a
/// <c>PollAsync</c> or relays workflow reports; dispatches are computed by the
/// stateless <see cref="DispatchService"/> and reports go direct to the owning
/// workflow grain via <see cref="WorkflowReportService"/>. Spec fixtures resolve
/// the service provider from their in-process test cluster
/// (<c>_fixture.Cluster.GetSiloServiceProvider(null)</c>) or integration host
/// (<c>_fixture.Services</c>).
/// </summary>
public static class DispatchTestExtensions
{
    /// <summary>
    /// Polls via DispatchService with an empty reported set (pure new-claim
    /// path — what a fresh runner needs). Returns the single first dispatch,
    /// or null when nothing is available. Mirrors the old runner.PollAsync()
    /// return shape so specs read naturally.
    /// </summary>
    public static async Task<WorkDispatch?> PollAsync(this IRunnerGrain runner, IServiceProvider serviceProvider)
    {
        var dispatch = ResolveScoped<DispatchService>(serviceProvider);
        var runnerId = runner.GetPrimaryKeyString();
        var response = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
        return response.Dispatches.FirstOrDefault();
    }

    /// <summary>
    /// Polls via DispatchService with an empty reported set and returns the
    /// FULL list of dispatches for the round (repairs + claims). Use this for
    /// multi-slot specs that need to observe every workflow a runner picks up
    /// in a single reconciliation round, where the single-dispatch
    /// <see cref="PollAsync(IRunnerGrain, IServiceProvider)"/> helper would
    /// hide all but the first.
    /// </summary>
    public static async Task<IReadOnlyList<WorkDispatch>> PollAllAsync(this IRunnerGrain runner, IServiceProvider serviceProvider)
    {
        var dispatch = ResolveScoped<DispatchService>(serviceProvider);
        var runnerId = runner.GetPrimaryKeyString();
        var response = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
        return response.Dispatches;
    }

    /// <summary>
    /// Reports a workflow work result direct to the owning grain via the
    /// stateless <see cref="WorkflowReportService"/>. Replaces the old
    /// <c>runner.ReportWorkflowResultAsync</c> relay, which the runner grain no
    /// longer performs. Shared by both the <c>WorkflowGrainSpecs</c> base class
    /// and non-inheriting specs (Backlog, integration) so the report path is
    /// defined once.
    /// </summary>
    public static async Task ReportWorkflowDirectAsync(
        IGrainFactory grains,
        IServiceProvider serviceProvider,
        string runnerId,
        string workflowRunId,
        string workId,
        WorkResult result)
    {
        var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var active = await workflow.GetActiveWorkAsync(workId);
        if (active?.WorkType == WorkItemTypes.Task && result.CompletionBoundary is null)
            result = result with { CompletionBoundary = TestCompletionBoundary(workflowRunId, active, runnerId, result) };

        var report = ResolveScoped<WorkflowReportService>(serviceProvider);
        await report.ReportAsync(
            runnerId,
            workflowRunId,
            workId,
            active?.WorkType == WorkItemTypes.Task ? active.TaskRunId : null,
            result);
    }

    private static WorkflowTaskCompletionBoundary TestCompletionBoundary(
        string workflowRunId,
        WorkflowActiveWorkView active,
        string runnerId,
        WorkResult result)
    {
        var failed = !string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);
        var unknown = string.Equals(result.Status, "unknown", StringComparison.OrdinalIgnoreCase);
        var outcome = unknown
            ? WorkflowTaskWorkspaceOutcomes.Unconfirmed
            : result.WorkspaceOutcome ?? WorkflowTaskWorkspaceOutcomes.CommittedClean;
        var identity = new WorkflowTaskExecutionIdentity(
            workflowRunId,
            active.Stage,
            active.TaskRunId,
            active.WorkId,
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId,
            runnerId,
            $"test-workspace:{workflowRunId}",
            JsonSerializer.SerializeToElement(1));
        var completion = new ActionCompletion(
            1,
            ActionStarted: !unknown,
            failed ? "failed" : unknown ? "unknown" : "succeeded",
            "test",
            failed || unknown ? null : result.Output,
            result.Error,
            (result.ArtifactUploadIds ?? Array.Empty<string>()).ToList(),
            null,
            DateTimeOffset.UnixEpoch);
        var receipt = new CommitReceipt(
            1,
            identity,
            "test-branch",
            "test-head",
            "test-tree",
            "test-branch",
            "test-head",
            "test-tree",
            new List<string>(),
            new List<string>(),
            new List<string>(),
            Authoritative: !unknown,
            unknown ? "boundary-missing" : null,
            DateTimeOffset.UnixEpoch);
        return new WorkflowTaskCompletionBoundary(
            1,
            identity,
            completion,
            receipt,
            outcome,
            unknown ? "boundary-missing" : result.WorkspaceReason,
            $"test-boundary:{workflowRunId}:{active.TaskRunId}:{result.Status}");
    }

    /// <summary>
    /// Resolves a scoped service from either a root or scoped provider.
    /// <see cref="DispatchService"/> / <see cref="WorkflowReportService"/> are
    /// scoped (they depend on a scoped DbContext); the integration host hands
    /// specs its root <see cref="IServiceProvider"/> with scope validation on,
    /// so a transient scope is opened here. Opening a scope is harmless on the
    /// Orleans silo provider too and keeps the helper uniform across fixture
    /// kinds.
    /// </summary>
    private static T ResolveScoped<T>(IServiceProvider serviceProvider) where T : notnull
    {
        var scope = serviceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }
}
