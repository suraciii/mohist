using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
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
        var active = await grains.GetGrain<IWorkflowGrain>(workflowRunId).GetActiveWorkAsync(workId);
        var taskRunId = active?.WorkType == WorkItemTypes.Task ? active.TaskRunId : null;
        var run = taskRunId is null
            ? null
            : await ResolveScoped<WorkflowRunQuerier>(serviceProvider).LoadAsync(workflowRunId);
        var task = taskRunId is null
            ? null
            : run?.FindTaskForRecoveryReceipt(taskRunId, workId);
        var settlement = task?.AgentResultSettlement;
        var runtime = task?.Uses switch
        {
            "mohist/opencode" => "opencode",
            "mohist/pi" => "pi",
            _ => null,
        };
        AgentExecutionBinding? binding = null;
        if (taskRunId is not null && runtime is not null && settlement is not null)
        {
            binding = new AgentExecutionBinding(
                taskRunId,
                workId,
                runnerId,
                settlement.AgentSessionId ?? $"test-session:{workId}",
                settlement.AgentTurnId ?? $"test-turn:{workId}",
                settlement.Runtime ?? runtime,
                settlement.RuntimeSessionId ?? $"test-runtime-session:{workId}");
            await grains.GetGrain<IWorkflowGrain>(workflowRunId).BindAgentExecutionAsync(binding);
        }

        var report = ResolveScoped<WorkflowReportService>(serviceProvider);
        await report.ReportAsync(
            runnerId,
            workflowRunId,
            workId,
            taskRunId,
            result,
            CancellationToken.None,
            binding?.AgentSessionId,
            binding?.AgentTurnId,
            binding?.Runtime,
            binding?.RuntimeSessionId);
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
