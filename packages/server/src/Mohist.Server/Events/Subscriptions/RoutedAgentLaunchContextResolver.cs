using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Narrow post-match resolver that turns a routed CloudEvent into an
/// ownership-validated execution context (issue-449 design decision 1).
///
/// <para>
/// The resolver is invoked by <see cref="RoutingDispatchHandler"/> ONLY
/// after <see cref="Agent.Services.RoutingTableEvaluator"/> has produced
/// a matched executable outcome and the response prompt has been
/// rendered. Match evaluation and prompt rendering remain envelope-only
/// so <c>mo routing test</c> and real dispatch always select the same
/// rules; workspace resolution lives strictly on the execution side of
/// the envelope boundary.
/// </para>
///
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Parse project, issue, epic, and workflow-run lineage from
///         the CloudEvent envelope.</item>
///   <item>If <c>workflowrunid</c> is present, load the run. The run is
///         authoritative: it must belong to the envelope project, and
///         when the envelope carries issue/epic they must match the
///         run's lineage. Carry forward the run's lineage values when
///         the envelope omits them so Session metadata remains
///         complete.</item>
///   <item>Otherwise, when the envelope carries issue lineage, load the
///         issue's currently bound WorkflowRun. Accept it only while
///         the run is nonterminal.</item>
///   <item>Require a non-empty <see cref="WorkspaceIdentity.Path"/>.
///         Return the typed unresolved result otherwise so the caller
///         can produce a workspace-unavailable preflight failure.</item>
/// </list>
/// </para>
///
/// <para>
/// An explicit <c>workflowrunid</c> never falls forward to the issue's
/// newer run (doing so could execute a delayed event in an unrelated
/// workspace). An issue-only event never reuses a terminal retained run
/// (the runner-local directory may already be cleanup-eligible).
/// </para>
/// </summary>
public sealed class RoutedAgentLaunchContextResolver : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowRunQuerier _workflowRuns;

    public RoutedAgentLaunchContextResolver(
        IDbContextFactory<MohistDbContext> dbFactory,
        WorkflowRunQuerier workflowRuns)
    {
        _dbFactory = dbFactory;
        _workflowRuns = workflowRuns;
    }

    public async Task<RoutedExecutionContextResolution> ResolveAsync(
        CloudEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!CloudEventLineage.TryReadProjectId(evt.Extensions, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.MissingProject,
                "event carries no project id");
        }

        var issueNumber = CloudEventLineage.TryReadPositiveNumber(
            evt.Extensions, EventCatalog.Lineage.Issue, out var parsedIssue)
                ? parsedIssue
                : (int?)null;
        var envelopeEpic = CloudEventLineage.TryReadPositiveNumber(
            evt.Extensions, EventCatalog.Lineage.Epic, out var parsedEpic)
                ? parsedEpic
                : (int?)null;
        var envelopeWorkflowRunId = CloudEventLineage.ReadValue(
            evt.Extensions, EventCatalog.Lineage.WorkflowRunId);

        var hasIssueLineage = issueNumber is > 0;

        if (!string.IsNullOrWhiteSpace(envelopeWorkflowRunId))
        {
            var routingContext = await _workflowRuns.LoadRoutingContextAsync(envelopeWorkflowRunId!, ct);
            if (routingContext is null)
            {
                return RoutedExecutionContextResolution.Unresolved(
                    RoutedResolutionFailure.WorkflowRunMissing,
                    $"explicit workflow run '{envelopeWorkflowRunId}' is not persisted");
            }

            var run = routingContext.Run;
            if (!string.Equals(routingContext.ProjectId, projectId, StringComparison.Ordinal))
            {
                return RoutedExecutionContextResolution.Unresolved(
                    RoutedResolutionFailure.LineageMismatch,
                    $"workflow run '{envelopeWorkflowRunId}' belongs to project '{routingContext.ProjectId ?? "(unknown)"}', not '{projectId}'");
            }

            if (issueNumber is > 0 && routingContext.IssueNumber != issueNumber)
            {
                return RoutedExecutionContextResolution.Unresolved(
                    RoutedResolutionFailure.LineageMismatch,
                    $"workflow run '{envelopeWorkflowRunId}' belongs to issue {routingContext.IssueNumber?.ToString() ?? "(none)"}, not {issueNumber.Value}");
            }

            if (envelopeEpic is > 0 && routingContext.EpicNumber != envelopeEpic)
            {
                return RoutedExecutionContextResolution.Unresolved(
                    RoutedResolutionFailure.LineageMismatch,
                    $"workflow run '{envelopeWorkflowRunId}' belongs to epic {routingContext.EpicNumber?.ToString() ?? "(none)"}, not {envelopeEpic.Value}");
            }

            if (run.Workspace is null || string.IsNullOrWhiteSpace(run.Workspace.Path))
            {
                return RoutedExecutionContextResolution.Unresolved(
                    RoutedResolutionFailure.WorkspaceEmpty,
                    $"workflow run '{envelopeWorkflowRunId}' has no persisted workspace path");
            }

            return RoutedExecutionContextResolution.Ready(
                new RoutedExecutionContext(
                    WorkflowRunId: run.Id,
                    ProjectId: projectId!,
                    IssueNumber: issueNumber ?? routingContext.IssueNumber,
                    EpicNumber: envelopeEpic ?? routingContext.EpicNumber,
                    WorkspacePath: run.Workspace.Path,
                    TerminalRun: run.Status.IsTerminal()));
        }

        if (!hasIssueLineage)
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.NoLineage,
                "event carries neither an explicit workflow run id nor an issue reference");
        }

        var issueWorkflowRunId = await LoadIssueCurrentWorkflowRunIdAsync(projectId!, issueNumber!.Value, ct);
        if (string.IsNullOrWhiteSpace(issueWorkflowRunId))
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.IssueRunMissing,
                $"issue {issueNumber.Value} has no bound workflow run");
        }

        var boundContext = await _workflowRuns.LoadRoutingContextAsync(issueWorkflowRunId!, ct);
        if (boundContext is null)
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.IssueRunMissing,
                $"issue {issueNumber.Value}'s bound workflow run '{issueWorkflowRunId}' is not persisted");
        }

        var boundRun = boundContext.Run;
        if (boundRun.Status.IsTerminal())
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.IssueRunTerminal,
                $"issue {issueNumber.Value}'s bound workflow run '{boundRun.Id}' is in a terminal status ({boundRun.Status})");
        }

        if (!string.Equals(boundContext.ProjectId, projectId, StringComparison.Ordinal))
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.LineageMismatch,
                    $"issue-bound workflow run '{boundRun.Id}' belongs to project '{boundContext.ProjectId ?? "(unknown)"}', not '{projectId}'");
        }

        if (boundContext.IssueNumber != issueNumber)
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.LineageMismatch,
                $"issue-bound workflow run '{boundRun.Id}' belongs to issue {boundContext.IssueNumber?.ToString() ?? "(none)"}, not {issueNumber.Value}");
        }

        if (envelopeEpic is > 0 && boundContext.EpicNumber != envelopeEpic)
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.LineageMismatch,
                $"issue-bound workflow run '{boundRun.Id}' belongs to epic {boundContext.EpicNumber?.ToString() ?? "(none)"}, not {envelopeEpic.Value}");
        }

        if (boundRun.Workspace is null || string.IsNullOrWhiteSpace(boundRun.Workspace.Path))
        {
            return RoutedExecutionContextResolution.Unresolved(
                RoutedResolutionFailure.WorkspaceEmpty,
                $"issue-bound workflow run '{boundRun.Id}' has no persisted workspace path");
        }

        return RoutedExecutionContextResolution.Ready(
            new RoutedExecutionContext(
                WorkflowRunId: boundRun.Id,
                ProjectId: projectId!,
                IssueNumber: issueNumber,
                    EpicNumber: envelopeEpic ?? boundContext.EpicNumber,
                WorkspacePath: boundRun.Workspace.Path,
                TerminalRun: false));
    }

    private async Task<string?> LoadIssueCurrentWorkflowRunIdAsync(
        string projectId, int issueNumber, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == issueNumber)
            .Select(row => row.WorkflowRunId)
            .FirstOrDefaultAsync(ct);
    }

}

/// <summary>
/// Ownership-validated routing execution context. Carries everything
/// the launcher needs to compose a canonical <see cref="RoutedAgentLaunchPlan"/>
/// without further reads of mutable WorkflowRun state.
/// </summary>
public sealed record RoutedExecutionContext(
    string WorkflowRunId,
    string ProjectId,
    int? IssueNumber,
    int? EpicNumber,
    string WorkspacePath,
    bool TerminalRun);

public enum RoutedResolutionFailure
{
    MissingProject,
    NoLineage,
    WorkflowRunMissing,
    LineageMismatch,
    IssueRunMissing,
    IssueRunTerminal,
    WorkspaceEmpty,
}

public sealed record RoutedExecutionContextResolution(
    RoutedExecutionContext? Context,
    RoutedResolutionFailure? Failure,
    string? FailureMessage)
{
    public bool IsReady => Context is not null;

    public static RoutedExecutionContextResolution Ready(RoutedExecutionContext context) =>
        new(context, null, null);

    public static RoutedExecutionContextResolution Unresolved(
        RoutedResolutionFailure failure,
        string message) =>
        new(null, failure, message);
}
