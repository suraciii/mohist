using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Source-agnostic AgentSession read surface addressed by the stable session
/// id (issue-479 T-004 / design D4). The <c>show</c> / <c>transcript</c>
/// routes resolve a session by id WITHOUT the <c>source-kind == agent-launch</c>
/// gate that the generic-session route applies, so an agent-launch session and
/// a workflow-originated session resolve by the same stable id. The
/// <c>list</c> route delegates to the existing source-specific querier methods
/// (<c>?agent=</c>, <c>?issue=</c>, <c>?run=</c>) and maps each result to the
/// unified <see cref="UnifiedSessionListItemDto"/>.
/// </summary>
/// <remarks>
/// The older <c>GET .../agent-sessions/{sessionId}</c> route stays for the
/// agent-launch transcript link until T-005 switches the CLI onto this
/// unified surface. Follow-up / cancel already resolve canonically by id for
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
            int? limit,
            AgentQuerier agentQuerier,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
            HandleListAsync(context.GetResolvedProject(), agent, issue, run, limit, agentQuerier, sessions, ct));

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
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
            HandleTranscriptAsync(context.GetResolvedProject(), sessionId, runtimeSessionId, sessions, ct));

        return app;
    }

    internal static async Task<IResult> HandleListAsync(
        ProjectInfo project,
        string? agent,
        int? issue,
        string? run,
        int? limit,
        AgentQuerier agentQuerier,
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        var filterCount = (!string.IsNullOrWhiteSpace(agent) ? 1 : 0)
            + (issue is > 0 ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(run) ? 1 : 0);
        if (filterCount == 0)
            return ApiResults.BadRequest(
                "One of 'agent', 'issue', or 'run' filter is required.",
                "session_filter_required");

        if (!string.IsNullOrWhiteSpace(agent))
            return await ListByAgentAsync(project, agent, limit, agentQuerier, sessions, ct);
        if (issue is > 0)
            return await ListByIssueAsync(project, issue.Value, sessions, ct);
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
        AgentSessionQuerier sessions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ApiResults.NotFound("Session not found");

        var transcript = await sessions.GetUnifiedSessionTranscriptAsync(project.Id, sessionId, runtimeSessionId, ct);
        return transcript is null
            ? ApiResults.NotFound($"Session {sessionId} not found")
            : ApiResults.Ok(transcript);
    }

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

        var items = await sessions.ListAgentSessionsAsync(
            project.Id,
            agent.Id,
            statusSet: null,
            limit ?? 50,
            ct: ct);

        var unified = items
            .Select(item => new UnifiedSessionListItemDto(
                Id: item.SessionId,
                Source: "agent-launch",
                RuntimeSessionId: null,
                Runtime: null,
                Activity: item.Activity,
                CreatedAt: item.CreatedAt,
                LastActivityAt: item.LastActivityAt,
                Model: null,
                AgentId: item.AgentId,
                AgentName: item.AgentName,
                WorkflowRunId: null,
                SessionName: null,
                ContextRefs: MapListContextRefs(item.ContextRefs)))
            .ToList();
        return ApiResults.Ok(unified);
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
                WorkflowRunId: null,
                SessionName: item.SessionName,
                ContextRefs: null))
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
                ContextRefs: null))
            .ToList();
        return ApiResults.Ok(unified);
    }

    private static UnifiedSessionContextRefsDto? MapListContextRefs(AgentSessionListContextRefsDto? refs) =>
        refs is null
            ? null
            : new UnifiedSessionContextRefsDto(refs.IssueNumber, refs.EpicNumber, refs.Repository, refs.WorkspacePath);
}
