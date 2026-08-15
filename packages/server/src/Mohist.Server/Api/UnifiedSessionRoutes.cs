using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Source-agnostic AgentSession read surface addressed by the stable session
/// id. The <c>show</c> / <c>transcript</c>
/// routes resolve a session by id WITHOUT the generic-session source gate, so
/// agent-launch, agent-connection, and workflow sessions resolve by the same
/// stable id. The
/// <c>list</c> route delegates to source-aware querier methods (<c>?agent=</c>,
/// <c>?issue=</c>, <c>?run=</c>) and maps each result to the unified
/// <see cref="UnifiedSessionListItemDto"/>.
/// </summary>
/// <remarks>
/// The older <c>GET .../agent-sessions/{sessionId}</c> route stays for the
/// agent-launch transcript link until the CLI migrates onto this
/// unified surface. Follow-up / stop already resolve canonically by id for
/// both sources and are unchanged here.
/// </remarks>
public static class UnifiedSessionRoutes
{
    public static WebApplication MapUnifiedSessionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", (
            HttpContext context,
            string projectRef,
            string? agent,
            int? issue,
            string? run,
            string? workspace,
            int? limit,
            AgentQuerier agentQuerier,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
            HandleListAsync(context.GetResolvedProject(), agent, issue, run, workspace, limit, agentQuerier, sessions, ct));

        group.MapGet("/{sessionId}", (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
            HandleShowAsync(context.GetResolvedProject(), sessionId, sessions, ct));

        group.MapGet("/{sessionId}/transcript", (
            HttpContext context,
            string projectRef,
            string sessionId,
            string? runtimeSessionId,
            string? view,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
            HandleTranscriptAsync(context.GetResolvedProject(), sessionId, runtimeSessionId, view, sessions, ct));

        return app;
    }

    internal static async Task<IResult> HandleListAsync(
        ProjectInfo project,
        string? agent,
        int? issue,
        string? run,
        string? workspace,
        int? limit,
        AgentQuerier agentQuerier,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        var filterCount = (!string.IsNullOrWhiteSpace(agent) ? 1 : 0)
            + (issue is > 0 ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(run) ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(workspace) ? 1 : 0);
        if (filterCount == 0)
            return ApiResults.BadRequest(
                "One of 'agent', 'issue', 'run', or 'workspace' filter is required.",
                "session_filter_required");
        if (filterCount > 1)
            return ApiResults.BadRequest(
                "Only one of 'agent', 'issue', 'run', or 'workspace' filter may be set.",
                "session_filter_multiple");

        if (!string.IsNullOrWhiteSpace(agent))
            return await ListByAgentAsync(project, agent, limit, agentQuerier, sessions, ct);
        if (issue is > 0)
            return await ListByIssueAsync(project, issue.Value, sessions, ct);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var items = await sessions.ListUnifiedSessionsByWorkspaceAsync(
                project.Id, workspace, limit ?? 100, ct);
            return ApiResults.Ok(items);
        }
        return await ListByRunAsync(project, run!, sessions, ct);
    }

    internal static async Task<IResult> HandleShowAsync(
        ProjectInfo project,
        string sessionId,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ApiResults.NotFound("Session not found");

        var summary = await sessions.GetUnifiedSessionSummaryAsync(project.Id, sessionId, ct);
        return summary is null
            ? ApiResults.NotFound($"Session {sessionId} not found")
            : ApiResults.Ok(summary);
    }

    internal static async Task<IResult> HandleTranscriptAsync(
        ProjectInfo project,
        string sessionId,
        string? runtimeSessionId,
        string? view,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ApiResults.NotFound("Session not found");

        var transcript = await sessions.GetUnifiedSessionTranscriptAsync(project.Id, sessionId, runtimeSessionId, ct, view);
        return transcript is null
            ? ApiResults.NotFound($"Session {sessionId} not found")
            : ApiResults.Ok(transcript);
    }

    internal static Task<IResult> HandleTranscriptAsync(
        ProjectInfo project,
        string sessionId,
        string? runtimeSessionId,
        AgentSessionQuerier sessions,
        CancellationToken ct) =>
        HandleTranscriptAsync(project, sessionId, runtimeSessionId, null, sessions, ct);

    private static async Task<IResult> ListByAgentAsync(
        ProjectInfo project,
        string agentRef,
        int? limit,
        AgentQuerier agentQuerier,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
        if (agent is null)
            return ApiResults.NotFound($"Agent '{agentRef}' not found");

        var items = await sessions.ListUnifiedSessionsByAgentAsync(
            project.Id,
            agent.Id,
            limit ?? 50,
            ct: ct);
        return ApiResults.Ok(items);
    }

    private static async Task<IResult> ListByIssueAsync(
        ProjectInfo project,
        int issueNumber,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        var items = await sessions.ListSummariesByIssueAsync(project.Id, issueNumber, ct);
        var unified = items
            .Select(item => new UnifiedSessionListItemDto(
                Id: item.Id,
                Source: "workflow",
                RuntimeSessionId: item.AgentRuntimeSessionId,
                Runtime: item.AgentRuntime,
                Activity: item.Activity,
                CreatedAt: item.CreatedAt,
                LastActivityAt: item.LastActivityAt,
                Model: item.Model,
                AgentId: null,
                AgentName: null,
                WorkflowRunId: item.WorkflowRunId,
                SessionName: item.SessionName,
                ContextRefs: MapWorkflowContextRefs(issueNumber)))
            .ToList();
        return ApiResults.Ok(unified);
    }

    private static async Task<IResult> ListByRunAsync(
        ProjectInfo project,
        string workflowRunId,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        var items = await sessions.ListByWorkflowAsync(workflowRunId, ct);
        var unified = items
            .Where(item => string.Equals(item.ProjectId, project.Id, StringComparison.Ordinal))
            .Select(item => new UnifiedSessionListItemDto(
                Id: item.Id,
                Source: "workflow",
                RuntimeSessionId: item.AgentSessionId,
                Runtime: item.Runtime,
                Activity: item.Activity,
                CreatedAt: item.CreatedAt,
                LastActivityAt: item.LastDataAt,
                Model: item.Model,
                AgentId: null,
                AgentName: null,
                WorkflowRunId: item.WorkflowRunId,
                SessionName: item.SessionName,
                ContextRefs: MapWorkflowContextRefs(item.IssueNumber)))
            .ToList();
        return ApiResults.Ok(unified);
    }

    private static UnifiedSessionContextRefsDto? MapWorkflowContextRefs(int? issueNumber) =>
        issueNumber is > 0
            ? new UnifiedSessionContextRefsDto(issueNumber, null, null, null)
            : null;

}
